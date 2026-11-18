using System;
using System.Collections.Generic;
using FootballSim.Core;
using Godot;

namespace FootballSim.Game;

/// <summary>
/// --keeper-gallery &lt;dir&gt;: shows every keeper state without the simulation. It drives the keeper's figure from scripted frames (the same
/// FigureFrame the match builds, with plausible hand positions and velocities), once for each dive side, and writes screenshots at a few moments
/// of each, from the side and from the front. Name format: shot_&lt;script&gt;_&lt;moment&gt;_&lt;view&gt;.png. Joint sanity (non-finite values,
/// lowest joint) is printed per script as GALLERY_CHECK.
/// </summary>
public sealed class KeeperGallery
{
    const float Dt = 1f / 120f;
    static readonly string[] Views = { "side", "front" };

    sealed class Phase
    {
        public KeeperAction Action; public KeeperDive Dive; public float Dur; public Func<float, Vector3>? Hand;
    }

    sealed class Script
    {
        public string Name = "";
        public readonly List<Phase> Phases = new();
        public readonly List<(float T, string Tag)> Moments = new();
        public Func<float, (Vector3 Pos, Vector3 Vel)>? Motion;
        public bool Running;
        public float Total { get { float t = 0; foreach (var p in Phases) t += p.Dur; return t; } }
        public float StartOf(int phase) { float t = 0; for (int i = 0; i < phase; i++) t += Phases[i].Dur; return t; }
    }

    readonly Match _m;
    readonly ShotTool _shots;
    readonly Vector3 _p0, _fwd, _right;
    readonly float _facing = Mathf.Pi;   // toward the ball, down the arena
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

    public KeeperGallery(Match m, string dir, float goalLineX)
    {
        _m = m;
        _shots = new ShotTool(m, dir);
        _p0 = new Vector3(goalLineX - 0.9f, 0, 0);
        _fwd = new Vector3(Mathf.Cos(_facing), 0, Mathf.Sin(_facing));
        _right = new Vector3(-_fwd.Z, 0, _fwd.X);
        m.Camera.Fov = 38f; m.Camera.Near = 0.2f;
        Build();
    }

    // ---- scripts ----

    Vector3 Rel(float fwd, float right, float up) => _p0 + _fwd * fwd + _right * right + new Vector3(0, up, 0);

    Phase Ph(KeeperAction a, float dur, KeeperDive k = KeeperDive.None, Func<float, Vector3>? hand = null) =>
        new() { Action = a, Dive = k, Dur = dur, Hand = hand };

    /// <summary>Hands travel from the ready position in front of the chest to the target, eased, over the phase.</summary>
    Func<float, Vector3> Reach(Vector3 to, float dur)
    {
        var from = Rel(0.45f, 0, 1.0f);
        return u => from.Lerp(to, Mathf.SmoothStep(0f, 1f, Mathf.Clamp(u / dur, 0f, 1f)));
    }

    static Vector3 Hold(Vector3 v) => v;

    /// <summary>The hands' target for a save of this kind on the side <paramref name="s"/> (+1 = the keeper's right).</summary>
    Vector3 Target(KeeperDive kind, float s) => kind switch
    {
        KeeperDive.Low => Rel(0.4f, 2.0f * s, 0.25f),
        KeeperDive.Side => Rel(0.3f, 2.0f * s, 1.0f),
        KeeperDive.High => Rel(0.2f, 1.2f * s, 2.5f),
        KeeperDive.Feet => Rel(0.45f, 0.35f * s, 0.15f),
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
        var holdStand = S("holding_standing");
        var catchPos = Rel(0.35f, 0, 1.1f);
        holdStand.Phases.Add(Ph(KeeperAction.Reacting, 0.4f)); holdStand.Phases.Add(Ph(KeeperAction.Diving, 0.3f, KeeperDive.Catch, Reach(Target(KeeperDive.Catch, 1), 0.3f)));
        holdStand.Phases.Add(Ph(KeeperAction.Holding, 1f, KeeperDive.None, _ => catchPos));
        MomPh(holdStand, 2, 0.5f, "hold");

        // saves, once for each side; lying ones get up afterwards
        foreach (var kind in new[] { KeeperDive.Low, KeeperDive.Side, KeeperDive.High, KeeperDive.Feet, KeeperDive.Catch, KeeperDive.None })
            foreach (float s in new[] { 1f, -1f })
            {
                bool smother = kind == KeeperDive.None;
                string side = s > 0 ? "right" : "left";
                string name = (smother ? "smother" : "dive_" + kind.ToString().ToLowerInvariant()) + "_" + side;
                var sc = S(name);
                var tgt = smother ? Rel(0.9f, 1.4f * s, 0.15f) : Target(kind, s);
                float dur = kind is KeeperDive.Feet or KeeperDive.Catch ? 0.35f : 0.55f;
                sc.Phases.Add(Ph(KeeperAction.Planted, 0.4f));
                sc.Phases.Add(Ph(KeeperAction.Reacting, 0.3f));
                sc.Phases.Add(Ph(smother ? KeeperAction.Smother : KeeperAction.Diving, dur + 0.15f, smother ? KeeperDive.None : kind, Reach(tgt, dur)));
                MomPh(sc, 1, 1f, "react");
                MomPh(sc, 2, 0.15f / (dur + 0.15f) + 0.0f, "dive_a");
                MomPh(sc, 2, 0.55f, "dive_b");
                MomPh(sc, 2, 1f, "dive_c");
                bool ground = !(kind is KeeperDive.Feet or KeeperDive.Catch);
                if (ground)
                {
                    sc.Phases.Add(Ph(KeeperAction.Grounded, 0.7f, KeeperDive.None, _ => tgt));
                    MomPh(sc, 3, 1f, "ground");
                    sc.Phases.Add(Ph(KeeperAction.GettingUp, KeeperView.GetUpSeconds, KeeperDive.None, _ => tgt));
                    foreach (var (f, tag) in new[] { (0.2f, "up20"), (0.35f, "up35"), (0.5f, "up50"), (0.7f, "up70"), (0.9f, "up90") }) MomPh(sc, 4, f, tag);
                    // holding the ball where he lies: same save, then Holding to the end
                    var hs = S("holding_after_" + name);
                    hs.Phases.Add(Ph(KeeperAction.Planted, 0.4f)); hs.Phases.Add(Ph(KeeperAction.Reacting, 0.3f));
                    hs.Phases.Add(Ph(smother ? KeeperAction.Smother : KeeperAction.Diving, dur + 0.15f, smother ? KeeperDive.None : kind, Reach(tgt, dur)));
                    hs.Phases.Add(Ph(KeeperAction.Holding, 1f, KeeperDive.None, _ => tgt));
                    MomPh(hs, 3, 0.1f, "hold_a"); MomPh(hs, 3, 1f, "hold_b");
                }
                else
                {
                    // the core ends a standing save in Holding (catch) or straight back in Set (feet block); neither lies down
                    sc.Phases.Add(Ph(kind == KeeperDive.Catch ? KeeperAction.Holding : KeeperAction.Set, 0.6f, KeeperDive.None, _ => tgt));
                    MomPh(sc, 3, 1f, "after");
                }
            }
    }

    // ---- driving ----

    /// <summary>Advances one physics tick. Returns true when everything has been shown and every screenshot is written.</summary>
    public bool Step()
    {
        if (_finished) return _shots.InFlight == 0;
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
            var f0 = Frame(0, out _);
            _m.Keeper.Teleport(f0);
            _m.Camera.ResetPhysicsInterpolation();
            return false;
        }
        var f = Frame(_t, out _);
        _m.Keeper.Apply(f);
        _gaitPhase += Mathf.Clamp(1.8f + 0.28f * f.Velocity.Length(), 1.8f, 4.2f) * Dt;
        Check();
        _m.Ball.Apply(BallAt(f));
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
        var f = FigureFrame.At(pos, _facing);
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
        f.KeeperAction = ph.Action; f.KeeperDive = ph.Dive; f.KeeperActionTime = u;
        f.Holding = ph.Action == KeeperAction.Holding;
        f.HandPosition = ph.Hand != null ? ph.Hand(u) : pos;
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

    void Capture(int view)
    {
        var f = Frame(Math.Max(0, _t - Dt), out int phase);
        var ph = _s.Phases[phase];
        string label = $"{_s.Name} {_s.Moments[_mi].Tag} | {ph.Action}{(ph.Dive != KeeperDive.None ? " " + ph.Dive : "")} t={f.KeeperActionTime:F2}s";
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
    }

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

    void Report() => GD.Print($"GALLERY_CHECK script={_s.Name} non_finite={_bad} lowest_joint_y_m={_minY:F3} at={_minWhere} max_joint_step_m_per_tick={_maxStep:F3} at={_maxStepWhere}");
}
