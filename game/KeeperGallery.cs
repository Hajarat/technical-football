using System;
using System.Collections.Generic;
using FootballSim.Core;
using Godot;

namespace FootballSim.Game;

/// <summary>
/// --keeper-gallery &lt;dir&gt;: shows every keeper state without the simulation. It drives the keeper's figure from scripted frames (the same
/// FigureFrame the match builds, with plausible hand positions and velocities), once for each dive side, and writes screenshots at a few moments
/// of each, from the side and from the front. Name format: shot_&lt;script&gt;_&lt;moment&gt;_&lt;view&gt;.png. Joint sanity (non-finite values,
/// lowest joint) is printed per script as GALLERY_CHECK. Run it once with --keeper-anim procedural and once with mocap for
/// before/after sheets; --gallery-only a,b plays only the scripts whose names start with one of those.
/// GALLERY_CHECK's lowest_joint_y_m and max_joint_step are the figure's own (procedural) joints; foot_slide_cm, body_lowest_joint_m and
/// max_bone_deg_per_s measure the rigged body as drawn, in either mode: the largest horizontal drift of an ankle while its foot stays
/// planted (mocap: a foot the foot lock holds; procedural: the figure's own planted foot; see PlayerFigure.KeeperFootPlanted), the
/// lowest bone joint, the fastest bone turn from one tick to the next (POP above 1,500 deg/s), and how far the chest (the line
/// between the shoulder joints) faces away from his facing in the stance and movement states other than the run; pop_ticks counts the
/// ticks with a POP; hand_error_cm is the glove's distance from the simulation's hand point at a save's contact tick (see CheckContact).
/// The scripted frames carry the core's SaveTarget / SaveContactTime (the hands' target and the tick they reach it). --video also
/// writes every other tick from the front to &lt;dir&gt;/video/&lt;script&gt;/f_NNNN.png; --bake-keeper-pose &lt;dir&gt; records the dive,
/// smother and slide scripts' bone rotations instead of screenshots (tools/anim/bake_procedural.py).
/// </summary>
public sealed class KeeperGallery
{
    const float Dt = 1f / 120f;
    static readonly string[] Views = { "side", "front" };

    sealed class Phase
    {
        public KeeperAction Action; public KeeperDive Dive; public float Dur; public Func<float, Vector3>? Hand;
        /// <summary>The save's meeting point and when (seconds after the action began; NaN: no save), as the core's SaveTarget / SaveContactTime.</summary>
        public Vector3 Target; public float Contact = float.NaN;
        /// <summary>Seconds of the same action already gone before this phase (the core changing the dive mid-save keeps the action's clock).</summary>
        public float Carry;
    }

    sealed class Script
    {
        public string Name = "";
        public readonly List<Phase> Phases = new();
        public readonly List<(float T, string Tag)> Moments = new();
        public Func<float, (Vector3 Pos, Vector3 Vel)>? Motion;
        public bool Running;
        /// <summary>The way he faces (default: down the arena at the ball).</summary>
        public float Facing = Mathf.Pi;
        /// <summary>--bake-keeper-pose: record this script's bone rotations from this phase to the end of the next lying phase.</summary>
        public int BakeFrom = -1;
        public float Total { get { float t = 0; foreach (var p in Phases) t += p.Dur; return t; } }
        public float StartOf(int phase) { float t = 0; for (int i = 0; i < phase; i++) t += Phases[i].Dur; return t; }
    }

    readonly Match _m;
    readonly ShotTool _shots;
    readonly Vector3 _p0, _fwd, _right;
    readonly float _facing = Mathf.Pi;   // toward the ball, down the arena
    const float PopDegPerSec = 1500f;
    readonly List<Script> _scripts = new();
    int _si = -1;
    Script _s = null!;
    float _t;
    int _mi, _settle, _view;
    bool _capturing, _finished;
    float _minY, _gaitPhase, _maxStep;
    string _maxStepWhere = "";
    Vector3[]? _prevJ;
    string _minWhere = "";
    bool _bad;
    // the rigged body as drawn: ankle plants, lowest joint, bone turning speed
    Quaternion[]? _prevRot;
    readonly bool[] _planted = new bool[2];
    readonly Vector3[] _plantAt = new Vector3[2], _prevAnkle = new Vector3[2];
    float _slide, _bodyLow, _boneSpeed;
    string _slideWhere = "", _bodyLowWhere = "", _boneWhere = "";
    int[] _ankleBone = { -1, -1 }, _armBone = { -1, -1 };
    float _chestOff;
    string _chestWhere = "";
    // the glove (or the reaching foot) against the simulation's hand point at the save's contact tick
    float _handErr = -1f;
    string _handWhere = "";
    int _pops;
    // --bake-keeper-pose: the recorded ticks (hips in model space, then every driven bone's local rotation) and the contact
    readonly string? _bakeDir = ToolArgs.Current?.BakeDir;
    readonly List<float[]> _bake = new();
    float _bakeT0 = -1f, _bakeContact = float.NaN, _bakeEnd = -1f;
    // --video: every other tick (60 fps) from the front, with a camera fixed per script, to <dir>/video/<script>/f_NNNN.png
    readonly bool _video = ToolArgs.Current?.Video == true;
    readonly string _dir;
    ShotTool? _videoShots;
    int _videoFrame, _videoTick;
    Vector3 _videoLook;
    static readonly string[] BakeScripts = { "dive_low_right", "dive_side_right", "dive_high_right", "smother_right", "slide" };

    public KeeperGallery(Match m, string dir, float goalLineX)
    {
        _m = m;
        _dir = dir;
        _shots = new ShotTool(m, dir);
        _p0 = new Vector3(goalLineX - 0.9f, 0, 0);
        _fwd = new Vector3(Mathf.Cos(_facing), 0, Mathf.Sin(_facing));
        _right = new Vector3(-_fwd.Z, 0, _fwd.X);
        m.Camera.Fov = 38f; m.Camera.Near = 0.2f;
        Build();
        var only = ToolArgs.Current?.GalleryOnly;
        if (only != null && only.Count > 0) _scripts.RemoveAll(sc => !only.Exists(o => sc.Name.StartsWith(o, StringComparison.Ordinal)));
        if (_bakeDir != null) _scripts.RemoveAll(sc => Array.IndexOf(BakeScripts, sc.Name) < 0);
    }

    // ---- scripts ----

    Vector3 Rel(float fwd, float right, float up) => _p0 + _fwd * fwd + _right * right + new Vector3(0, up, 0);

    Phase Ph(KeeperAction a, float dur, KeeperDive k = KeeperDive.None, Func<float, Vector3>? hand = null) =>
        new() { Action = a, Dive = k, Dur = dur, Hand = hand };

    /// <summary>A save phase: the hands reach <paramref name="target"/> at <paramref name="contact"/> (the core's SaveTarget and SaveContactTime).</summary>
    Phase Save(KeeperAction a, float dur, KeeperDive k, Vector3 target, float contact, float carry = 0f) =>
        new() { Action = a, Dive = k, Dur = dur, Hand = Reach(target, contact, carry), Target = target, Contact = contact, Carry = carry };

    /// <summary>Hands travel from the ready position in front of the chest to the target, eased, over the phase.</summary>
    Func<float, Vector3> Reach(Vector3 to, float dur, float carry = 0f)
    {
        var from = Rel(0.45f, 0, 1.0f);
        return u => from.Lerp(to, Mathf.SmoothStep(0f, 1f, Mathf.Clamp((u + carry) / dur, 0f, 1f)));
    }

    static Vector3 Hold(Vector3 v) => v;

    /// <summary>The hands' target for a save of this kind on the side <paramref name="s"/> (+1 = the keeper's right).</summary>
    Vector3 Target(KeeperDive kind, float s) => kind switch
    {
        KeeperDive.Low => Rel(0.4f, 2.0f * s, 0.25f),
        KeeperDive.Side => Rel(0.3f, 2.0f * s, 1.0f),
        KeeperDive.High => Rel(0.2f, 1.2f * s, 2.5f),
        KeeperDive.Feet => Rel(0.45f, 0.35f * s, 0.15f),
        KeeperDive.ReflexHand => Rel(0.30f, 1.15f * s, 1.05f),
        KeeperDive.ReflexFoot => Rel(0.30f, 1.05f * s, 0.12f),
        KeeperDive.BodyBlock => Rel(0.25f, 0.95f * s, 0.75f),
        _ => Rel(0.45f, 0.15f * s, 1.15f),
    };

    void Build()
    {
        Script S(string name) { var s = new Script { Name = name }; _scripts.Add(s); return s; }
        void Mom(Script s, float t, string tag) => s.Moments.Add((t, tag));
        void MomPh(Script s, int phase, float frac, string tag) => Mom(s, s.StartOf(phase) + s.Phases[phase].Dur * frac - (frac >= 1f ? Dt : 0f), tag);

        // stances
        var set = S("set"); set.Phases.Add(Ph(KeeperAction.Set, 1f)); MomPh(set, 0, 0.9f, "end");
        var planted = S("planted"); planted.Phases.Add(Ph(KeeperAction.Set, 0.4f)); planted.Phases.Add(Ph(KeeperAction.Planted, 0.8f)); MomPh(planted, 1, 1f, "end");
        var reacting = S("reacting"); reacting.Phases.Add(Ph(KeeperAction.Planted, 0.5f)); reacting.Phases.Add(Ph(KeeperAction.Reacting, 0.6f)); MomPh(reacting, 1, 1f, "end");
        foreach (float s in new[] { 1f, -1f })
        {
            string side = s > 0 ? "right" : "left";
            var sh = S("shuffle_" + side);
            sh.Phases.Add(Ph(KeeperAction.Set, 0.3f)); sh.Phases.Add(Ph(KeeperAction.Shuffle, 1.6f)); sh.Phases.Add(Ph(KeeperAction.Set, 0.6f));
            sh.Running = true;
            sh.Motion = t =>
            {
                float u = Mathf.Clamp((t - 0.3f) / 1.6f, 0f, 1f);
                // 2.8 m along the arc in 1.6 s with a smooth start and stop; the velocity is along the keeper's right
                float along = Mathf.SmoothStep(0f, 1f, u) * 2.8f - 1.4f;
                float vel = u > 0f && u < 1f ? 6f * u * (1f - u) * 2.8f / 1.6f : 0f;
                return (_p0 + _right * (s * along), _right * (s * vel));
            };
            MomPh(sh, 1, 0.25f, "a"); MomPh(sh, 1, 0.5f, "b"); MomPh(sh, 1, 0.75f, "c");
        }
        var rush = S("rush"); rush.Running = true;
        rush.Phases.Add(Ph(KeeperAction.Set, 0.3f)); rush.Phases.Add(Ph(KeeperAction.Rush, 1.4f)); rush.Phases.Add(Ph(KeeperAction.Set, 0.8f));
        rush.Motion = t =>
        {
            float u = Mathf.Clamp((t - 0.3f) / 1.4f, 0f, 1f);
            float x = Mathf.SmoothStep(0f, 1f, u) * 7f, v = u > 0f && u < 1f ? 6f * u * (1f - u) * 7f / 1.4f : 0f;
            if (t > 1.7f) { v = 0; x = 7f; }
            return (_p0 + _fwd * x, _fwd * v);
        };
        MomPh(rush, 1, 0.2f, "a"); MomPh(rush, 1, 0.5f, "b"); MomPh(rush, 1, 0.8f, "c");
        // backpedalling: 4 m back toward his goal in 1.3 s facing the play (peaking near his backpedal speed), then set
        var back = S("backpedal"); back.Running = true;
        back.Phases.Add(Ph(KeeperAction.Set, 0.3f)); back.Phases.Add(Ph(KeeperAction.Backpedal, 1.3f)); back.Phases.Add(Ph(KeeperAction.Set, 0.6f));
        back.Motion = t =>
        {
            float u = Mathf.Clamp((t - 0.3f) / 1.3f, 0f, 1f);
            float x = 4f - Mathf.SmoothStep(0f, 1f, u) * 4f, v = u > 0f && u < 1f ? 6f * u * (1f - u) * 4f / 1.3f : 0f;
            return (_p0 + _fwd * x, -_fwd * v);
        };
        MomPh(back, 1, 0.25f, "a"); MomPh(back, 1, 0.5f, "b"); MomPh(back, 1, 0.75f, "c");
        // charging: running out at an attacker on the ball (the run cycle, as the rush), then jockeying in front of him
        var charge = S("charge_jockey"); charge.Running = true;
        charge.Phases.Add(Ph(KeeperAction.Set, 0.3f)); charge.Phases.Add(Ph(KeeperAction.Charge, 1.2f)); charge.Phases.Add(Ph(KeeperAction.Jockey, 1.4f));
        charge.Motion = t =>
        {
            float u = Mathf.Clamp((t - 0.3f) / 1.2f, 0f, 1f);
            float x = Mathf.SmoothStep(0f, 1f, u) * 6f, v = u > 0f && u < 1f ? 6f * u * (1f - u) * 6f / 1.2f : 0f;
            // jockeying: stepping 1.2 m to his right and back with the attacker (starting and ending still), giving a little ground
            float w = Mathf.Clamp((t - 1.5f) / 1.4f, 0f, 1f);
            float sn = Mathf.Sin(Mathf.Pi * w);
            float across = 1.2f * sn * sn, vAcross = 1.2f * Mathf.Pi / 1.4f * Mathf.Sin(2f * Mathf.Pi * w);
            float back = 0.5f * Mathf.SmoothStep(0f, 1f, w), vBack = 0.5f * 6f * w * (1f - w) / 1.4f;
            if (t > 1.5f) return (_p0 + _fwd * (x - back) + _right * across, _right * vAcross - _fwd * vBack);
            return (_p0 + _fwd * x, _fwd * v);
        };
        MomPh(charge, 1, 0.3f, "a"); MomPh(charge, 1, 0.75f, "b"); MomPh(charge, 2, 0.25f, "jockey_a"); MomPh(charge, 2, 0.6f, "jockey_b");
        // staying big: from his set stance he stops and spreads, close to an attacker on the ball
        var spread = S("spread"); spread.Phases.Add(Ph(KeeperAction.Set, 0.4f)); spread.Phases.Add(Ph(KeeperAction.Spread, 0.9f));
        MomPh(spread, 1, 0.3f, "a"); MomPh(spread, 1, 1f, "end");
        // the backward leap under a chip: backpedalling, then springing up and back at a ball dropping behind him, then on his back
        var leap = S("backward_leap"); leap.Running = true;
        var leapAt = Rel(-1.1f, 0.35f, 2.75f);
        leap.Phases.Add(Ph(KeeperAction.Backpedal, 0.6f)); leap.Phases.Add(Save(KeeperAction.Diving, 0.45f, KeeperDive.Back, leapAt, 0.35f));
        leap.Phases.Add(Ph(KeeperAction.Grounded, 0.6f, KeeperDive.None, _ => leapAt));
        leap.Motion = t =>
        {
            float u = Mathf.Clamp(t / 0.9f, 0f, 1f);
            return (_p0 + _fwd * (2.2f - 2.2f * Mathf.SmoothStep(0f, 1f, u)), -_fwd * (t < 0.9f ? 3.5f * (1f - u) : 0f));
        };
        MomPh(leap, 0, 0.6f, "back"); MomPh(leap, 1, 0.35f, "spring"); MomPh(leap, 1, 0.8f, "reach"); MomPh(leap, 2, 1f, "down");
        var holdStand = S("holding_standing");
        var catchPos = Rel(0.35f, 0, 1.1f);
        holdStand.Phases.Add(Ph(KeeperAction.Reacting, 0.4f)); holdStand.Phases.Add(Save(KeeperAction.Diving, 0.3f, KeeperDive.Catch, Target(KeeperDive.Catch, 1), 0.3f));
        holdStand.Phases.Add(Ph(KeeperAction.Holding, 1f, KeeperDive.None, _ => catchPos));
        MomPh(holdStand, 2, 0.5f, "hold");

        // saves, once for each side; lying ones get up afterwards
        foreach (var kind in new[] { KeeperDive.Low, KeeperDive.Side, KeeperDive.High, KeeperDive.Feet, KeeperDive.Catch, KeeperDive.None,
                     KeeperDive.ReflexHand, KeeperDive.ReflexFoot, KeeperDive.BodyBlock })
            foreach (float s in new[] { 1f, -1f })
            {
                bool smother = kind == KeeperDive.None;
                string side = s > 0 ? "right" : "left";
                string name = (smother ? "smother" : "dive_" + kind.ToString().ToLowerInvariant()) + "_" + side;
                var sc = S(name);
                var tgt = smother ? Rel(0.9f, 1.4f * s, 0.15f) : Target(kind, s);
                bool reflexKind = kind is KeeperDive.ReflexHand or KeeperDive.ReflexFoot or KeeperDive.BodyBlock;
                float dur = reflexKind ? 0.22f : kind is KeeperDive.Feet or KeeperDive.Catch ? 0.35f : 0.55f;
                // a reflex comes before his full reaction; ready close to the shooter (the body block) he waits spread
                var waiting = kind == KeeperDive.BodyBlock ? KeeperAction.Spread : KeeperAction.Planted;
                sc.Phases.Add(Ph(waiting, 0.4f));
                sc.Phases.Add(Ph(waiting == KeeperAction.Spread ? KeeperAction.Spread : KeeperAction.Reacting, reflexKind ? 0.1f : 0.3f));
                sc.Phases.Add(Save(smother ? KeeperAction.Smother : KeeperAction.Diving, dur + 0.15f, smother ? KeeperDive.None : kind, tgt, dur));
                if (s > 0 && (smother || kind is KeeperDive.Low or KeeperDive.Side or KeeperDive.High)) sc.BakeFrom = 2;
                MomPh(sc, 1, 1f, "react");
                MomPh(sc, 2, 0.15f / (dur + 0.15f) + 0.0f, "dive_a");
                MomPh(sc, 2, 0.55f, "dive_b");
                MomPh(sc, 2, 1f, "dive_c");
                bool ground = !(kind is KeeperDive.Feet or KeeperDive.Catch || reflexKind);
                if (ground)
                {
                    sc.Phases.Add(Ph(KeeperAction.Grounded, 0.7f, KeeperDive.None, _ => tgt));
                    MomPh(sc, 3, 1f, "ground");
                    sc.Phases.Add(Ph(KeeperAction.GettingUp, KeeperView.GetUpSeconds, KeeperDive.None, _ => tgt));
                    foreach (var (f, tag) in new[] { (0.2f, "up20"), (0.35f, "up35"), (0.5f, "up50"), (0.7f, "up70"), (0.9f, "up90") }) MomPh(sc, 4, f, tag);
                    // holding the ball where he lies: same save, then Holding to the end
                    var hs = S("holding_after_" + name);
                    hs.Phases.Add(Ph(KeeperAction.Planted, 0.4f)); hs.Phases.Add(Ph(KeeperAction.Reacting, 0.3f));
                    hs.Phases.Add(Save(smother ? KeeperAction.Smother : KeeperAction.Diving, dur + 0.15f, smother ? KeeperDive.None : kind, tgt, dur));
                    hs.Phases.Add(Ph(KeeperAction.Holding, 1f, KeeperDive.None, _ => tgt));
                    MomPh(hs, 3, 0.1f, "hold_a"); MomPh(hs, 3, 1f, "hold_b");
                }
                else
                {
                    // the core ends a standing save in Holding (catch) or straight back in Set (feet block, reflex); none lies down
                    sc.Phases.Add(Ph(kind == KeeperDive.Catch ? KeeperAction.Holding : KeeperAction.Set, 0.6f, KeeperDive.None, _ => tgt));
                    MomPh(sc, 3, 1f, "after");
                }
            }

        // the slide tackle outside his area (the core's slide: the leading foot from slide_foot_start ahead at slide_speed out to
        // slide_reach, the body following slide_body_follow of it), at a ball 2 m ahead; then lying and the get-up
        var slide = S("slide"); slide.Running = true;
        var slideBall = Rel(2.0f, 0f, 0.11f);
        const float footStart = 0.3f, footSpeed = 6f, reach = 2.4f, follow = 0.75f;
        float slideContact = (2.0f - footStart) / footSpeed, slideDur = (reach - footStart) / footSpeed + 0.1f;
        slide.Phases.Add(Ph(KeeperAction.Planted, 0.4f));
        slide.Phases.Add(new Phase { Action = KeeperAction.Slide, Dur = slideDur, Target = slideBall, Contact = slideContact });
        slide.Phases.Add(Ph(KeeperAction.Grounded, 0.7f));
        slide.Phases.Add(Ph(KeeperAction.GettingUp, KeeperView.GetUpSeconds));
        slide.BakeFrom = 1;
        slide.Motion = t =>
        {
            float u = Mathf.Clamp(t - 0.4f, 0f, slideDur);
            float sFoot = Math.Min(reach, footStart + footSpeed * u);
            float along = follow * Math.Max(0f, sFoot - footStart);
            float v = t > 0.4f && sFoot < reach ? follow * footSpeed : 0f;
            return (_p0 + _fwd * along, _fwd * v);
        };
        MomPh(slide, 1, 0.3f, "slide_a"); MomPh(slide, 1, 0.65f, "slide_b"); MomPh(slide, 1, 1f, "slide_c");
        MomPh(slide, 2, 1f, "ground"); MomPh(slide, 3, 0.35f, "up35"); MomPh(slide, 3, 0.7f, "up70");

        // the core changing the save mid-way (review focus): a reflex hand at a ball to his right, upgraded after 0.12 s to a side dive at
        // a point further out and later; the action stays Diving (its clock runs on), the set switches and the IK follows the new contact
        var up = S("dive_upgraded_right");
        var first = Target(KeeperDive.ReflexHand, 1f);
        var second = Rel(0.3f, 1.8f, 0.9f);
        up.Phases.Add(Ph(KeeperAction.Planted, 0.4f)); up.Phases.Add(Ph(KeeperAction.Reacting, 0.1f));
        up.Phases.Add(Save(KeeperAction.Diving, 0.12f, KeeperDive.ReflexHand, first, 0.22f));
        up.Phases.Add(Save(KeeperAction.Diving, 0.58f, KeeperDive.Side, second, 0.55f, 0.12f));
        up.Phases.Add(Ph(KeeperAction.Grounded, 0.7f, KeeperDive.None, _ => second));
        up.Phases.Add(Ph(KeeperAction.GettingUp, KeeperView.GetUpSeconds, KeeperDive.None, _ => second));
        MomPh(up, 2, 0.9f, "reflex"); MomPh(up, 3, 0.4f, "dive_b"); MomPh(up, 3, 1f, "dive_c"); MomPh(up, 4, 1f, "ground"); MomPh(up, 5, 0.5f, "up50");
    }

    // ---- driving ----

    /// <summary>Advances one physics tick. Returns true when everything has been shown and every screenshot is written.</summary>
    public bool Step()
    {
        if (_finished) return _shots.InFlight == 0 && (_videoShots == null || _videoShots.InFlight == 0);
        if (_videoShots != null && _videoShots.InFlight > 0) return false;
        if (_capturing)
        {
            if (_shots.InFlight > 0) return false;
            if (_view < Views.Length) { Capture(_view++); return false; }
            _capturing = false; _mi++;
            return false;
        }
        if (_settle > 0)
        {
            if (--_settle == 0) { _capturing = true; _view = 0; Capture(_view++); }
            return false;
        }
        if (_s == null || _t >= _s.Total + 0.05f && _mi >= _s.Moments.Count)
        {
            if (_s != null) Report();
            if (++_si >= _scripts.Count) { _finished = true; return false; }
            _s = _scripts[_si]; _t = 0; _mi = 0; _minY = 1e9f; _bad = false; _maxStep = 0; _prevJ = null;
            _prevRot = null; _planted[0] = _planted[1] = false; _slide = 0; _bodyLow = 1e9f; _boneSpeed = 0; _slideWhere = _bodyLowWhere = _boneWhere = ""; _chestOff = 0; _chestWhere = "";
            _handErr = -1f; _handWhere = ""; _pops = 0;
            _bake.Clear(); _bakeT0 = -1f; _bakeContact = float.NaN; _bakeEnd = -1f;
            if (_bakeDir != null) _s.Moments.Clear();
            if (_video) StartVideo();
            var f0 = Frame(0, out _);
            _m.Keeper.Teleport(f0);
            _m.Camera.ResetPhysicsInterpolation();
            return false;
        }
        var f = Frame(_t, out _);
        _m.Keeper.Apply(f);
        _gaitPhase += Mathf.Clamp(1.8f + 0.28f * f.Velocity.Length(), 1.8f, 4.2f) * Dt;
        Check();
        CheckContact(f);
        if (_bakeDir != null) Bake(f);
        _m.Ball.Apply(BallAt(f));
        if (_videoShots != null && _videoTick++ % 2 == 0) VideoFrame();
        _t += Dt;
        if (_mi < _s.Moments.Count && _t >= _s.Moments[_mi].T) _settle = 3;
        return false;
    }

    Vector3 BallAt(in FigureFrame f)
    {
        bool hands = f.KeeperAction is KeeperAction.Diving or KeeperAction.Smother or KeeperAction.Holding or KeeperAction.Grounded or KeeperAction.GettingUp && f.HandPosition != f.Position;
        return hands ? f.HandPosition : _p0 + _fwd * 3f + new Vector3(0, 0.11f, 0);
    }

    FigureFrame Frame(float t, out int phase)
    {
        Vector3 pos = _p0, vel = Vector3.Zero;
        if (_s.Motion != null) (pos, vel) = _s.Motion(t);
        var f = FigureFrame.At(pos, _s.Facing);
        f.Velocity = vel; f.RunSpeed = vel.Length(); f.Dt = Dt;
        float acc = 0;
        phase = 0;
        for (int i = 0; i < _s.Phases.Count; i++)
        {
            if (t < acc + _s.Phases[i].Dur || i == _s.Phases.Count - 1) { phase = i; break; }
            acc += _s.Phases[i].Dur;
        }
        var ph = _s.Phases[phase];
        float u = Math.Min(t - acc, ph.Dur);
        f.KeeperAction = ph.Action; f.KeeperDive = ph.Dive; f.KeeperActionTime = u + ph.Carry;
        f.SaveTarget = ph.Target; f.SaveContactTime = ph.Contact;
        f.Holding = ph.Action == KeeperAction.Holding;
        f.HandPosition = ph.Hand != null ? ph.Hand(u) : ph.Action == KeeperAction.Slide ? new Vector3(pos.X, 0.3f, pos.Z) : pos;
        f.BallPos = BallAt(f);
        if (_s.Running && vel.Length() > 0.05f)
        {
            // a stride clock for the run cycle and the shuffle, as the simulation would supply it
            float freq = Mathf.Clamp(1.8f + 0.28f * vel.Length(), 1.8f, 4.2f);
            float gp = _gaitPhase % 2f;
            f.HasGait = true; f.IsStepping = true; f.StepFrequency = freq; f.GaitPhase = gp;
            f.SwingRight = gp < 1f; f.SwingProgress = gp % 1f;
            f.RightFootPhase = gp / 2f; f.LeftFootPhase = (gp / 2f + 0.5f) % 1f;
        }
        return f;
    }

    void StartVideo()
    {
        string d = System.IO.Path.Combine(_dir, "video", _s.Name);
        System.IO.Directory.CreateDirectory(d);
        _videoShots = new ShotTool(_m, d, "");
        _videoFrame = 0; _videoTick = 0;
        // the camera looks between where he starts and his save's meeting point (or the middle of his travel), and stays there
        Vector3 a = _s.Motion != null ? _s.Motion(0).Pos : _p0, b = _s.Motion != null ? _s.Motion(_s.Total).Pos : _p0;
        Vector3 c = (a + b) * 0.5f;
        foreach (var ph in _s.Phases) if (float.IsFinite(ph.Contact)) { c = (a + new Vector3(ph.Target.X, 0, ph.Target.Z)) * 0.5f; break; }
        _videoLook = new Vector3(c.X, 0.8f, c.Z);
    }

    void VideoFrame()
    {
        var f = Frame(_t, out int phase);
        var ph = _s.Phases[phase];
        var anim = _m.Keeper.Figure.KeeperAnim;
        _m.Hud.SetSpotLine($"{_s.Name} | {ph.Action}{(ph.Dive != KeeperDive.None ? " " + ph.Dive : "")} t={f.KeeperActionTime:F2}s | " +
                           (anim != null ? $"mocap: {anim.Describe()}" : "procedural"));
        _m.Camera.Transform = new Transform3D(Basis.Identity, _videoLook + new Vector3(-6.0f, 0.6f, 0)).LookingAt(_videoLook, Vector3.Up);
        _m.Camera.ResetPhysicsInterpolation();
        _videoShots!.Request($"f_{_videoFrame++:D4}");
    }

    void Capture(int view)
    {
        var f = Frame(Math.Max(0, _t - Dt), out int phase);
        var ph = _s.Phases[phase];
        var anim = _m.Keeper.Figure.KeeperAnim;
        string label = $"{_s.Name} {_s.Moments[_mi].Tag} | {ph.Action}{(ph.Dive != KeeperDive.None ? " " + ph.Dive : "")} t={f.KeeperActionTime:F2}s" +
                       (anim != null ? $" | mocap: {anim.Describe()}" : " | procedural");
        _m.Hud.SetSpotLine(label);
        Vector3 c = f.Position;
        if (f.KeeperAction is KeeperAction.Diving or KeeperAction.Smother or KeeperAction.Grounded or KeeperAction.GettingUp or KeeperAction.Holding && f.HandPosition != f.Position)
            c = (c + new Vector3(f.HandPosition.X, 0, f.HandPosition.Z)) * 0.5f;
        var look = new Vector3(c.X, 0.9f, c.Z);
        Vector3 eye = view == 0 ? look + new Vector3(0, 0.5f, 5.6f) : look + new Vector3(-5.0f, 0.5f, 0);
        _m.Camera.Transform = new Transform3D(Basis.Identity, eye).LookingAt(look, Vector3.Up);
        _m.Camera.ResetPhysicsInterpolation();
        _shots.Request($"{_s.Name}_{_s.Moments[_mi].Tag}_{Views[view]}");
    }

    void Check()
    {
        var j = _m.Keeper.Figure.Joints;
        void One(string name, Vector3 v)
        {
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) _bad = true;
            if (v.Y < _minY) { _minY = v.Y; _minWhere = $"{name}@{_t:F2}s"; }
        }
        One("pelvis", j.Pelvis);
        Trace(j);
        // frame-to-frame snaps: the largest single-tick move of any joint (a snap shows as several times the speed of a fast dive)
        var cur = new Vector3[10];
        cur[0] = j.Pelvis;
        for (int i = 0; i < 2; i++) { cur[1 + i] = j.Knee[i]; cur[3 + i] = j.Ankle[i]; cur[5 + i] = j.Shoulder[i]; cur[7 + i] = j.Elbow[i]; }
        // (the scripted hand target of the Diving/Smother phases itself starts with a jump, so those phases are left out)
        var act = _s.Phases[PhaseAt(_t)].Action;
        if (_prevJ != null && _t > Dt * 2 && act != KeeperAction.Diving && act != KeeperAction.Smother)
            for (int i = 0; i < cur.Length; i++)
            {
                float step = (cur[i] - _prevJ[i]).Length();
                if (step > _maxStep) { _maxStep = step; _maxStepWhere = $"joint{i}@{_t:F2}s {act}"; }
            }
        _prevJ = cur;
        for (int i = 0; i < 2; i++) { One("hip", j.Hip[i]); One("knee", j.Knee[i]); One("shoulder", j.Shoulder[i]); One("elbow", j.Elbow[i]); One("hand", j.Hand[i]); One("ankle", j.Ankle[i] - new Vector3(0, j.AnkleHeight, 0)); }
        CheckBody(act);
    }

    /// <summary>The rigged body as drawn this tick: ankle plants and their drift, the lowest joint, the fastest bone turn.</summary>
    void CheckBody(KeeperAction act)
    {
        var body = _m.Keeper.Figure.Body;
        if (body == null) return;
        int n = body.DrivenCount;
        var names = body.DrivenBoneNames;
        if (_ankleBone[0] < 0)
            for (int k = 0; k < n; k++)
            {
                if (names[k].EndsWith("LeftFoot")) _ankleBone[0] = k;
                if (names[k].EndsWith("RightFoot")) _ankleBone[1] = k;
            }
        var xf = body.GlobalTransform;
        for (int k = 0; k < n; k++)
        {
            var w = xf * body.DrivenBoneOrigin(k);
            if (!float.IsFinite(w.X) || !float.IsFinite(w.Y) || !float.IsFinite(w.Z)) _bad = true;
            if (w.Y < _bodyLow) { _bodyLow = w.Y; _bodyLowWhere = $"{Short(names[k])}@{_t:F2}s {act}"; }
        }
        // the chest's heading against his facing (stance and movement states but the run): the line between the shoulder joints
        if (_armBone[0] < 0)
            for (int k = 0; k < n; k++)
            {
                if (names[k].EndsWith("LeftArm")) _armBone[0] = k;
                if (names[k].EndsWith("RightArm")) _armBone[1] = k;
            }
        if (_armBone[0] >= 0 && act is KeeperAction.Set or KeeperAction.Planted or KeeperAction.Reacting or KeeperAction.Shuffle
                or KeeperAction.Spread or KeeperAction.Jockey or KeeperAction.Backpedal)
        {
            var across = xf * body.DrivenBoneOrigin(_armBone[1]) - xf * body.DrivenBoneOrigin(_armBone[0]);   // toward his right
            var chest = new Vector3(across.Z, 0, -across.X);   // his right is (-fwd.z, 0, fwd.x), so his front is (right.z, 0, -right.x)
            if (chest.LengthSquared() > 1e-6f)
            {
                float off = Mathf.RadToDeg(Math.Abs(Mathf.Wrap(Mathf.Atan2(chest.Z, chest.X) - _facing, -Mathf.Pi, Mathf.Pi)));
                if (off > _chestOff) { _chestOff = off; _chestWhere = $"{_t:F2}s {act}"; }
            }
        }
        var fig = _m.Keeper.Figure;
        for (int i = 0; i < 2; i++)
        {
            if (_ankleBone[i] < 0) continue;
            var a = xf * body.DrivenBoneOrigin(_ankleBone[i]);
            var flat = new Vector3(a.X, 0, a.Z);
            bool planted = fig.KeeperFootPlanted(i);
            if (planted && !_planted[i]) _plantAt[i] = flat;
            _planted[i] = planted;
            if (planted)
            {
                float d = (flat - _plantAt[i]).Length();
                if (d > _slide) { _slide = d; _slideWhere = $"{(i == 0 ? "L" : "R")}@{_t:F2}s {act}"; }
            }
            _prevAnkle[i] = flat;
        }
        if (_prevRot == null) _prevRot = new Quaternion[n];
        else
            for (int k = 0; k < n; k++)
            {
                float dot = Math.Min(1f, Math.Abs(body.DrivenLocalRotation(k).Normalized().Dot(_prevRot[k].Normalized())));
                float deg = Mathf.RadToDeg(2f * MathF.Acos(dot)) / Dt;
                if (deg > _boneSpeed) { _boneSpeed = deg; _boneWhere = $"{Short(names[k])}@{_t:F2}s {act}"; }
                if (deg > PopDegPerSec)
                {
                    if (k == 0 || _lastPopTick != _tickCount) { _pops++; _lastPopTick = _tickCount; }
                    var anim = _m.Keeper.Figure.KeeperAnim;
                    string feet = anim == null ? "" : $" feet={Feet(anim, 0)}{Feet(anim, 1)}";
                    GD.Print($"GALLERY_POP script={_s.Name} t={_t:F3} {act} bone={Short(names[k])} deg_per_s={deg:F0} anim={(anim != null ? anim.Describe() : "procedural")}{feet}");
                }
            }
        for (int k = 0; k < n; k++) _prevRot[k] = body.DrivenLocalRotation(k);
        _tickCount++;
    }

    int _tickCount, _lastPopTick = -1;

    /// <summary>
    /// At a save's contact tick (the first tick its action time reaches the contact): how far the glove is from the simulation's hand
    /// point. Both hands meeting the ball (dives, catch, smother, backward leap): the middle of the two gloves; a reflex hand or a body
    /// block: the near glove; a foot save (reflex foot, feet): the near ankle; the slide: the leading (right) ankle against its meeting
    /// point. The glove's middle is the wrist plus 8 cm along the forearm (FootballSim.Anim.Limbs).
    /// </summary>
    void CheckContact(in FigureFrame f)
    {
        if (!float.IsFinite(f.SaveContactTime)) return;
        float tc = Math.Max(f.SaveContactTime, 0f);
        if (f.KeeperActionTime < tc || f.KeeperActionTime - Dt >= tc) return;
        var body = _m.Keeper.Figure.Body;
        if (body == null) return;
        var xf = body.GlobalTransform;
        var names = body.DrivenBoneNames;
        Vector3 B(string shortName) { for (int k = 0; k < names.Count; k++) if (names[k].EndsWith("_" + shortName) || names[k].EndsWith(":" + shortName)) return xf * body.DrivenBoneOrigin(k); return Vector3.Zero; }
        Vector3 Glove(int side) { var e = B(side == 0 ? "LeftForeArm" : "RightForeArm"); var w = B(side == 0 ? "LeftHand" : "RightHand"); var d = w - e; return d.LengthSquared() > 1e-10f ? w + d.Normalized() * 0.08f * xf.Basis.Scale.X : w; }
        int near = KeeperChooserSide(f) > 0 ? 1 : 0;
        Vector3 point, target = f.HandPosition;
        string limb;
        if (f.KeeperAction == KeeperAction.Slide) { point = B("RightFoot"); target = f.SaveTarget; limb = "right_ankle"; }
        else if (f.KeeperDive is KeeperDive.ReflexFoot or KeeperDive.Feet) { point = B(near == 1 ? "RightFoot" : "LeftFoot"); limb = "near_ankle"; }
        else if (f.KeeperDive is KeeperDive.ReflexHand or KeeperDive.BodyBlock) { point = Glove(near); limb = "near_glove"; }
        else { point = (Glove(0) + Glove(1)) * 0.5f; limb = "gloves_mid"; }
        float err = (point - target).Length();
        if (err > _handErr) { _handErr = err; _handWhere = $"{limb}@{_t:F2}s {f.KeeperAction}{(f.KeeperDive != KeeperDive.None ? "_" + f.KeeperDive : "")}"; }
    }

    static int KeeperChooserSide(in FigureFrame f)
    {
        var p = f.KeeperAction == KeeperAction.Slide ? f.SaveTarget : f.HandPosition;
        int s = FootballSim.Anim.KeeperChooser.SideOf(new System.Numerics.Vector3(p.X, p.Y, p.Z), new System.Numerics.Vector3(f.Position.X, f.Position.Y, f.Position.Z), f.Facing);
        return s == 0 ? 1 : s;
    }

    /// <summary>--bake-keeper-pose: records the drawn bones from the script's bake phase to the end of the lying that follows.</summary>
    void Bake(in FigureFrame f)
    {
        if (_s.BakeFrom < 0) return;
        int phase = PhaseAt(_t);
        if (_bakeT0 < 0f && phase >= _s.BakeFrom) { _bakeT0 = _t; _bakeContact = _s.Phases[_s.BakeFrom].Contact; }
        if (_bakeT0 < 0f || f.KeeperAction == KeeperAction.GettingUp || phase > _s.BakeFrom + 1) return;
        var body = _m.Keeper.Figure.Body;
        if (body == null) return;
        int n = body.DrivenCount;
        var row = new float[3 + 4 * n];
        var h = body.DrivenBoneOrigin(0);
        row[0] = h.X; row[1] = h.Y; row[2] = h.Z;
        for (int k = 0; k < n; k++) { var q = body.DrivenLocalRotation(k).Normalized(); row[3 + 4 * k] = q.X; row[4 + 4 * k] = q.Y; row[5 + 4 * k] = q.Z; row[6 + 4 * k] = q.W; }
        _bake.Add(row);
        _bakeEnd = _t;
    }

    void WriteBake()
    {
        if (_bake.Count == 0) return;
        var sb = new System.Text.StringBuilder();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        sb.Append("{\"script\":\"").Append(_s.Name).Append("\",\"fps\":120,\"facing\":").Append(_s.Facing.ToString("R", inv));
        sb.Append(",\"contact_s\":").Append(float.IsFinite(_bakeContact) ? _bakeContact.ToString("R", inv) : "null");
        sb.Append(",\"bones\":[");
        var names = _m.Keeper.Figure.Body!.DrivenBoneNames;
        for (int k = 0; k < names.Count; k++) sb.Append(k > 0 ? "," : "").Append('"').Append(names[k]).Append('"');
        sb.Append("],\"frames\":[");
        for (int i = 0; i < _bake.Count; i++)
        {
            sb.Append(i > 0 ? ",\n[" : "[");
            for (int j = 0; j < _bake[i].Length; j++) sb.Append(j > 0 ? "," : "").Append(_bake[i][j].ToString("R", inv));
            sb.Append(']');
        }
        sb.Append("]}\n");
        string path = System.IO.Path.Combine(_bakeDir!, _s.Name + ".json");
        System.IO.File.WriteAllText(path, sb.ToString());
        GD.Print($"BAKE {_s.Name} frames={_bake.Count} seconds={_bakeEnd - _bakeT0 + Dt:F3} contact={_bakeContact:F3} {path}");
    }

    /// <summary>A foot's lock state for logs: L locked, s stepping, - free.</summary>
    static string Feet(KeeperAnimator a, int foot) => a.Motion.FootLock.Locked(foot) ? "L" : a.Motion.FootLock.Stepping(foot) ? "s" : "-";

    static string Short(string bone) => bone.StartsWith("mixamorig_") ? bone["mixamorig_".Length..] : bone;

    /// <summary>KEEPER_TRACE every 0.05 s: torso lean from upright (deg), its sideways and forward parts (along the keeper's own right and facing, + = right / ahead), the torso's front direction's height (-1 = face down, 0 = on his side, +1 = on his back), the hands' mean height and the pelvis height.</summary>
    void Trace(FigureJoints j)
    {
        if (_s.Name.StartsWith("holding_")) return;
        int tick = Mathf.RoundToInt(_t / Dt);
        if (tick % 6 != 0) return;
        var f = Frame(_t, out int phase);
        var mid = (j.Shoulder[0] + j.Shoulder[1]) * 0.5f;
        var axis = (mid - j.Pelvis).Normalized();
        var right = (j.Shoulder[1] - j.Shoulder[0]).Normalized();
        var front = axis.Cross(right).Normalized();
        float lean = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(axis.Y, -1f, 1f)));
        float hh = (j.Hand[0].Y + j.Hand[1].Y) * 0.5f;
        GD.Print($"KEEPER_TRACE script={_s.Name} t={_t:F2} {f.KeeperAction} u={f.KeeperActionTime:F2} lean_deg={lean:F0} side={axis.Dot(_right):F2} ahead={axis.Dot(_fwd):F2} front_y={front.Y:F2} hand_y={hh:F2} pelvis_y={j.Pelvis.Y:F2} hand_span={(j.Hand[1] - j.Hand[0]).Length():F2}");
    }

    int PhaseAt(float t) { Frame(t, out int ph); return ph; }

    void Report()
    {
        if (_bakeDir != null) WriteBake();
        GD.Print($"GALLERY_CHECK script={_s.Name} anim={_m.Keeper.AnimMode}{(_m.Keeper.Figure.KeeperAnim == null && _m.Keeper.AnimMode == "mocap" ? "(unavailable)" : "")} non_finite={_bad} " +
        $"lowest_joint_y_m={_minY:F3} at={_minWhere} max_joint_step_m_per_tick={_maxStep:F3} at={_maxStepWhere} " +
        $"foot_slide_cm={_slide * 100f:F1} at={_slideWhere} body_lowest_joint_m={_bodyLow:F3} at={_bodyLowWhere} " +
        $"max_bone_deg_per_s={_boneSpeed:F0} at={_boneWhere}{(_boneSpeed > PopDegPerSec ? " POP" : "")} pop_ticks={_pops} chest_off_facing_deg={_chestOff:F0} at={_chestWhere} " +
        $"hand_error_cm={(_handErr < 0 ? "n/a" : (_handErr * 100f).ToString("F1"))} at={_handWhere}");
    }
}
