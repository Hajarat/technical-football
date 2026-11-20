using System;
using FootballSim.Anim;
using Godot;
using NQuat = System.Numerics.Quaternion;
using NVec3 = System.Numerics.Vector3;

namespace FootballSim.Game;

/// <summary>
/// --skill-anim clips: the attacker's skill moves drawn from the authored full-body clips (game/assets/anim/attacker/skills, made
/// by tools/anim/author.py), timed by the simulation. When a move starts, its standing and running clips for the move's playing
/// foot (and side) are picked and blended by the player's speed then (<see cref="SkillEntryBlend"/>, fixed for the move); the clip
/// time follows the core's ticks (<see cref="SkillTimeMap"/>: start→contact onto the move's start→contact tick, contact→release onto
/// contact→end tick, release→end in real time after it). The pose relative to the clip's root (<see cref="SkillClip.Root"/>) is put
/// on the simulation's position and facing; each tick, after the hand-over offset (<see cref="Inertializer"/>), the playing foot's
/// surface is brought onto the live ball by leg IK over the clip's on-ball spans (ramping in through the reach and out after the
/// release; a step-over's circling foot keeps its path relative to the live ball), a foot the clip plants is held where it landed
/// (<see cref="PlantLock"/>), and a touch the core makes on the way out (the exit touch) takes its leg by the touch's weight as the
/// figure draws it. A running clip with a second ending (the clip of the same name with "_swap": the ball roll's support foot
/// swinging into the exit touch instead of standing under it) is crossed over to it while the exit touch the core forecasts is
/// the support foot's (<see cref="FigureFrame.Upcoming"/>), and back when it is not. A clip whose move file declares its stances
/// has its planted feet taken onto the clip's own feet and held from their first stance tick (a foot the run has planted as the clip
/// takes over stays on its spot until the clip first lifts it). When the clip has played out (or a
/// shot or a reset cuts it) the body hands back to the figure's pose by
/// inertialisation, the feet by an ankle offset that dies away while the foot swings and is held while it is planted, so nothing
/// pops and nothing slides. Works in the body's model space (the model's own size, ground at y = 0). No clock and no randomness.
/// </summary>
/// <summary>A skill move as the figure starts drawing it (for the session log): its clips and their blend, or the procedural drawing.</summary>
public readonly record struct SkillStart(string Move, bool Right, float Side, string? StandClip, string? RunClip, float WRun, float EntrySpeed, bool Clips);

public sealed class SkillClipLayer
{
    const string Folder = "res://assets/anim/attacker/skills";
    /// <summary>How long (clip seconds) the ball pin takes to let go after its span ends.</summary>
    public const float PinRampOut = 0.08f;
    /// <summary>A clip-planted foot is held only once it is this close (model metres) to its resting height (a foot still landing from the hand-over comes down first).</summary>
    public const float PlantAbove = 0.025f;
    /// <summary>Half-life (seconds) of a foot's ankle offset at the hand-back, while the foot is in the air.</summary>
    public const float FootHandBackHalfLife = 0.06f;

    static readonly bool Dbg = System.Environment.GetEnvironmentVariable("SKILL_CLIP_DBG") == "1";
    static SkillClipLibrary? _lib;
    /// <summary>The clips whose stances are declared by their move file (clips.json "stance"), not read off their motion.</summary>
    static readonly System.Collections.Generic.HashSet<string> _declared = new();
    static string? _loadError;

    readonly SkinnedBody _body;
    readonly Skeleton _sk;
    readonly int _n;
    readonly NQuat[] _a, _b, _c, _src, _out, _gr, _baseR, _baseRPrev, _shR, _shRPrev;
    readonly NVec3[] _gp, _rest, _srcP, _outP, _baseP, _basePPrev, _shP, _shPPrev, _angV, _linV, _dstAngV, _dstLinV;
    readonly NQuat[] _baseG;
    readonly Inertializer _in;
    readonly PlantLock[] _lock = { new(), new() };
    readonly float _restAnkleY, _legLen;
    bool _haveShown, _haveShownPrev, _haveBase, _haveBasePrev;

    // the move being played
    bool _playing, _handBack, _transition, _pending;
    int _moveId = int.MinValue, _foot;
    SkillClip? _stand, _run, _runAlt;
    /// <summary>The second ending's weight against the running clip's own (0..1, eased), and its linear progress.</summary>
    float _wAlt, _altU, _altWant;
    /// <summary>Seconds a crossover between the two endings takes.</summary>
    public const float EndingBlend = 0.05f;
    /// <summary>A fake shot is drawn as the real shot's wind-up until this long before its contact; its clip checks the swing from there.</summary>
    public const float FakeCheckSeconds = 0.10f;
    float _wRun, _t, _ct, _shotElapsed = float.NaN, _prevSpeed = float.NaN;
    SkillTimeMap? _map;
    BootSurface _surface;
    string _move = "";
    readonly NVec3[] _legOff = new NVec3[2], _legOffVel = new NVec3[2], _baseAnkPrev = new NVec3[2];
    readonly float[] _pinW = new float[2];
    readonly bool[] _planted = new bool[2];

    /// <summary>"clips" (this layer plays the authored clips) or "procedural" (the figure's own drawing of every move).</summary>
    public string Mode = "clips";
    /// <summary>Raised when a move starts being drawn: by its clips (again if a fake restarts on its new foot), or once by the procedural drawing.</summary>
    public event Action<SkillStart>? Started;
    float _entrySpeed;
    int _procId = int.MinValue;

    SkillClipLayer(SkinnedBody body, Skeleton sk)
    {
        _body = body; _sk = sk; _n = sk.Count;
        _a = new NQuat[_n]; _b = new NQuat[_n]; _c = new NQuat[_n]; _src = new NQuat[_n]; _out = new NQuat[_n]; _gr = new NQuat[_n];
        _baseR = new NQuat[_n]; _baseRPrev = new NQuat[_n]; _shR = new NQuat[_n]; _shRPrev = new NQuat[_n];
        _gp = new NVec3[_n]; _rest = new NVec3[_n]; _srcP = new NVec3[_n]; _outP = new NVec3[_n]; _baseP = new NVec3[_n]; _basePPrev = new NVec3[_n];
        _shP = new NVec3[_n]; _shPPrev = new NVec3[_n]; _angV = new NVec3[_n]; _linV = new NVec3[_n]; _dstAngV = new NVec3[_n]; _dstLinV = new NVec3[_n];
        _baseG = new NQuat[_n];
        for (int k = 0; k < _n; k++) _rest[k] = sk.Offsets[k];
        _in = new Inertializer(_n);
        var rl = new NQuat[_n]; for (int k = 0; k < _n; k++) rl[k] = sk.RestLocal[k];
        sk.Forward(rl, sk.Offsets[0], _gr, _gp);
        _restAnkleY = _gp[sk.Foot[0]].Y;
        _legLen = sk.Offsets[sk.Leg[0]].Length() + sk.Offsets[sk.Foot[0]].Length();
    }

    /// <summary>The layer for <paramref name="body"/> on the stride cycles' skeleton <paramref name="sk"/>, or null (and why) when the clips are missing or do not fit.</summary>
    public static SkillClipLayer? TryCreate(SkinnedBody body, Skeleton sk, out string error)
    {
        error = _loadError ?? "";
        if (_lib == null && _loadError == null)
        {
            try
            {
                var json = FileAccess.GetFileAsString(Folder + "/clips.json");
                if (string.IsNullOrEmpty(json)) throw new System.IO.FileNotFoundException($"{Folder}/clips.json not found");
                _lib = SkillClipLibrary.Parse(json, file =>
                {
                    var bytes = FileAccess.GetFileAsBytes($"{Folder}/{file}");
                    if (bytes == null || bytes.Length == 0) throw new System.IO.FileNotFoundException($"{Folder}/{file} not found");
                    return bytes;
                });
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                    foreach (var c in doc.RootElement.GetProperty("clips").EnumerateArray())
                        if (c.TryGetProperty("stance", out _)) _declared.Add(c.GetProperty("name").GetString()!);
                GD.Print($"SKILL_ANIM clips={_lib.Clips.Count}");
            }
            catch (Exception e) { _loadError = $"skill clips: {e.Message}"; error = _loadError; }
        }
        if (_lib == null) return null;
        if (_lib.Skeleton.Count != sk.Count) { error = $"the skill clips have {_lib.Skeleton.Count} bones, the body {sk.Count}"; return null; }
        for (int k = 0; k < sk.Count; k++)
            if (_lib.Skeleton.Names[k] != sk.Names[k]) { error = $"skill clip bone {k} is {_lib.Skeleton.Names[k]}, the body drives {sk.Names[k]}"; return null; }
        return new SkillClipLayer(body, sk);
    }

    public static string MoveName(SkillKind k) => k switch
    {
        SkillKind.StepOver => "step_over", SkillKind.BodyFeint => "body_feint", SkillKind.DragBack => "drag_back",
        SkillKind.BallRoll => "ball_roll", SkillKind.FakeStop => "fake_stop", SkillKind.Elastico => "elastico", SkillKind.Spin => "spin",
        SkillKind.HeelToHeel => "heel_to_heel", _ => "fake_cut",
    };

    /// <summary>
    /// Whether the clips can draw this move (mode clips, a clip pair for its foot and side). The touch controller's own turn at the
    /// feet (<see cref="FigureSkill.FeetTurn"/>, a drag-back kind with its own short timing) stays on the procedural drawing.
    /// </summary>
    public bool Covers(in FigureSkill s) => Mode == "clips" && _lib != null && s.Valid && !s.FeetTurn
        && _lib.Pair(MoveName(s.Kind), s.Right ? 1 : 0, Math.Sign((int)s.Side)) != null;

    /// <summary>True while a clip draws the body or the body is still being handed back from one.</summary>
    public bool Active => _playing || _handBack;
    public bool Playing => _playing;
    /// <summary>Whether foot <paramref name="i"/> is held where it landed this tick (only while <see cref="Playing"/>).</summary>
    public bool Planted(int i) => _planted[i];
    /// <summary>The weight of the ball pin on the playing foot this tick (1 while the clip has its surface on the ball).</summary>
    public float PinWeight(int i) => _pinW[i];
    public string Describe() => _playing && _stand != null ? $"{_stand.Clip.Name}{(_wRun > 0f ? $"+run{_wRun:F2}" : "")} t={_ct:F3}" : _handBack ? "hand-back" : "-";

    public void Reset()
    {
        _playing = _handBack = _transition = _pending = false; _moveId = int.MinValue;
        _haveShown = _haveShownPrev = _haveBase = _haveBasePrev = false; _prevSpeed = float.NaN;
        _in.Reset(); foreach (var l in _lock) l.Reset();
        for (int i = 0; i < 2; i++) { _legOff[i] = _legOffVel[i] = NVec3.Zero; _pinW[i] = 0f; _planted[i] = false; _hbStep[i] = false; }
        _reachOff = NVec3.Zero; Array.Clear(_pinHave); Array.Clear(_legU); Array.Clear(_flagWas); Array.Clear(_anchor); Array.Clear(_anchor0); Array.Clear(_twLim); Array.Clear(_ballLift);
        Array.Clear(_handIn);
    }

    static NVec3 M(Vector3 world, float sc) => ModelSpace.FromWorld(new NVec3(world.X, world.Y, world.Z)) / sc;

    /// <summary>
    /// One physics tick: <paramref name="rot"/> / <paramref name="pos"/> hold the figure's pose (local rotations and positions in the
    /// clip bone order, index 0 the hips' position, model space) and are replaced by what is shown. <paramref name="tw"/> is each
    /// foot's touch weight and <paramref name="basePlanted"/> whether the figure has it planted.
    /// </summary>
    public void Apply(in FigureFrame f, float height, ReadOnlySpan<float> tw, ReadOnlySpan<bool> basePlanted, bool teleport, Quaternion[] rot, Vector3[] pos)
    {
        float dt = f.Dt > 0f ? f.Dt : 1f / 120f;
        float sc = _body.ScaleFor(height);
        for (int k = 0; k < _n; k++) { _baseR[k] = new NQuat(rot[k].X, rot[k].Y, rot[k].Z, rot[k].W); _baseP[k] = new NVec3(pos[k].X, pos[k].Y, pos[k].Z); }
        _planted[0] = _planted[1] = false;
        if (teleport) Reset();

        // the move under way, or a fake shot the core has read but not made yet: the shot's wind-up (run-up, plant, backswing) is
        // drawn until FakeCheckSeconds before the fake's contact, then its clip checks the swing into the stop or the hook
        var s = f.Skill.Valid ? f.Skill : f.PendingSkill.Valid && f.PendingSkill.ContactElapsed >= -FakeCheckSeconds - 1e-4f ? f.PendingSkill : default;
        bool covers = Covers(s);
        // (a fake whose strike foot changes before its contact starts again on the new foot, from the pose shown)
        if (covers && (s.Id != _moveId || _playing && (s.Right ? 1 : 0) != _foot))
        {
            Start(f, s, sc, basePlanted); for (int i = 0; i < 2; i++) { _twBlocked[i] = tw[i] > 0f; _twS[i] = 0f; }
            Started?.Invoke(new SkillStart(_move, _foot == 1, s.Side, _stand!.Clip.Name, _run!.Clip.Name, _wRun, _entrySpeed, true));
        }
        if (!covers && f.Skill.Valid && f.Skill.Id != _moveId && f.Skill.Id != _procId)
        {
            _procId = f.Skill.Id;
            float v = float.IsNaN(_prevSpeed) ? new Vector2(f.Velocity.X, f.Velocity.Z).Length() : _prevSpeed;
            Started?.Invoke(new SkillStart(MoveName(f.Skill.Kind), f.Skill.Right, f.Skill.Side, null, null, 0f, v, false));
        }
        else if (!s.Valid && !_playing) _shotElapsed = f.Shot.Valid ? f.Shot.Elapsed : float.NaN;
        if (_playing)
        {
            if (f.Skill.Valid && f.Skill.Id == _moveId) _pending = false;
            _t = s.Valid && s.Id == _moveId ? s.Elapsed : _t + dt;
            // the clip has played out, or a shot or another move it cannot draw takes over, or a fake read was not made
            bool cut = !s.Valid && f.Shot.Valid || s.Valid && s.Id != _moveId || _pending && !s.Valid;
            if (_t > _map!.SimEnd + 1e-4f || cut) BeginHandBack(sc, dt);
        }

        if (_playing)
        {
            ChooseEnding(f, dt);
            ClipPose(f, sc, _t, _src, out var hips);
            for (int k = 0; k < _n; k++) _srcP[k] = _rest[k];
            _srcP[0] = hips;
            if (_transition)
            {
                _transition = false;
                if (_haveShownPrev)
                {
                    // the offset is taken at the last tick shown, against the clip at that time, and the update below carries both on one tick
                    ClipPose(f, sc, _t - dt, _clipPrevR, out var hipsPrev);
                    for (int k = 0; k < _n; k++)
                    {
                        _dstAngV[k] = Spring.AngularVelocity(_clipPrevR[k], _src[k], dt);
                        _dstLinV[k] = k == 0 ? (hips - hipsPrev) / dt : NVec3.Zero;
                        _clipPrevP[k] = k == 0 ? hipsPrev : _rest[k];
                    }
                    ShownVelocities(dt);
                    _in.Transition(_shR, _angV, _shP, _linV, _clipPrevR, _dstAngV, _clipPrevP, _dstLinV);
                }
                else _in.Reset();
            }
            _in.Update(dt, _src, _srcP, _out, _outP);
            Constrain(f, sc, tw, dt);
        }
        else if (_handBack)
        {
            _in.Update(dt, _baseR, _baseP, _out, _outP);
            // a move the figure draws itself (one no clip covers, chained onto the clip being handed back) has its playing foot
            // on the ball by its contact: the leg's hand-back offset on that foot is gone by then
            _hbOwn[0] = _hbOwn[1] = 1f;
            if (f.Skill.Valid && !covers && f.Skill.Id != _moveId)
            {
                float tc = f.Skill.Elapsed - f.Skill.ContactElapsed;
                _hbOwn[f.Skill.Right ? 1 : 0] = tc > 1e-4f ? 1f - Smooth(f.Skill.Elapsed / tc) : 0f;
            }
            HandBackLegs(tw, basePlanted, dt);
            if (!_in.Active && _legOff[0].LengthSquared() < 1e-6f && _legOff[1].LengthSquared() < 1e-6f) _handBack = false;
        }
        else
        {
            for (int k = 0; k < _n; k++) { _out[k] = _baseR[k]; _outP[k] = _baseP[k]; }
        }

        if (Dbg)
        {
            _sk.Forward(_out, _outP[0], _gr, _gp, _outP);
            for (int i = 0; i < 2; i++) { var w = ModelSpace.ToWorld(_gp[_sk.Foot[i]]) * sc; GD.Print($"SKILL_DBG shown foot={i} world=({w.X:F3},{w.Y:F3},{w.Z:F3}) playing={_playing} hb={_handBack}"); }
        }
        _prevSpeed = new Vector2(f.Velocity.X, f.Velocity.Z).Length();
        // history: the pose shown and the figure's own, for the next hand-over's velocities
        for (int k = 0; k < _n; k++)
        {
            _shRPrev[k] = _shR[k]; _shPPrev[k] = _shP[k]; _shR[k] = _out[k]; _shP[k] = _outP[k];
            _baseRPrev[k] = _baseR[k]; _basePPrev[k] = _baseP[k];
        }
        _haveShownPrev = _haveShown; _haveShown = true; _haveBasePrev = _haveBase; _haveBase = true;
        for (int k = 0; k < _n; k++) { rot[k] = new Quaternion(_out[k].X, _out[k].Y, _out[k].Z, _out[k].W); pos[k] = new Vector3(_outP[k].X, _outP[k].Y, _outP[k].Z); }
    }

    void Start(in FigureFrame f, in FigureSkill s, float sc, ReadOnlySpan<bool> basePlanted)
    {
        int foot = s.Right ? 1 : 0;
        var pair = _lib!.Pair(MoveName(s.Kind), foot, Math.Sign((int)s.Side))!.Value;
        _stand = pair.Stand; _run = pair.Run; _foot = foot; _move = MoveName(s.Kind); _surface = s.Surface;
        _runAlt = null; _wAlt = _altU = _altWant = 0f;
        if (_run != _stand)
            foreach (var c2 in _lib.Clips)
                if (c2.Clip.Name == _run.Clip.Name + "_swap") { _runAlt = c2; break; }
        // the speed he came in at: the last tick before the move (on its first tick the move already moves him)
        float speed = float.IsNaN(_prevSpeed) ? new Vector2(f.Velocity.X, f.Velocity.Z).Length() : _prevSpeed;
        _wRun = SkillEntryBlend.RunWeight(speed, _stand.EntrySpeed, _run.EntrySpeed); _entrySpeed = speed;
        float simContact = s.Elapsed - s.ContactElapsed, simEnd = s.Duration;
        // a fake shot's wind-up began at the shot's press: the time since then counts as gone (still pending, the clip begins on
        // this tick, FakeCheckSeconds before the contact, part way into its own wind-up, and reaches its contact on the core's)
        _pending = !f.Skill.Valid;
        float origin = _pending ? Math.Max(0f, s.Elapsed - f.Dt) : float.IsNaN(_shotElapsed) ? 0f : -Math.Max(0f, _shotElapsed + f.Dt - s.Elapsed);
        var c = _stand;
        // the clip's events onto the core's ticks: contact, the spin's lift, the second contact, release on the end tick
        var knots = new System.Collections.Generic.List<(float, float)> { (simContact, c.EventSeconds("contact")) };
        if (c.HasEvent("lift") && !float.IsNaN(s.LiftElapsed)) knots.Add((s.Elapsed - s.LiftElapsed, c.EventSeconds("lift")));
        if (c.HasEvent("contact2") && !float.IsNaN(s.Contact2Elapsed)) knots.Add((s.Elapsed - s.Contact2Elapsed, c.EventSeconds("contact2")));
        knots.Add((simEnd, c.EventSeconds("release")));
        _map = new SkillTimeMap(c.EventSeconds("start"), knots, c.EventSeconds("end"), origin);
        _moveId = s.Id; _t = s.Elapsed; _playing = true; _handBack = false; _transition = true;
        foreach (var l in _lock) l.Reset();
        _shotElapsed = float.NaN; Array.Clear(_pinHave); Array.Clear(_legU); Array.Clear(_flagWas); Array.Clear(_anchor); Array.Clear(_anchor0);
        BuildPins(s);
        // a foot the run has planted as a running clip with declared stances takes over is held on its spot (see _handIn)
        bool declared = _wRun > 0.5f && _declared.Contains(_run.Clip.Name);
        // (planted: the figure has it so, or its foot is on the ground and has all but stopped, a landing still settling)
        var shownAnk = new NVec3[2];
        if (_haveShown) { _sk.Forward(_shR, _shP[0], _gr, _gp, _shP); for (int i = 0; i < 2; i++) shownAnk[i] = _gp[_sk.Foot[i]]; }
        _sk.Forward(_baseR, _baseP[0], _gr, _gp, _baseP);
        for (int i = 0; i < 2; i++)
        {
            var a = _gp[_sk.Foot[i]];
            bool still = _haveShown && a.Y < _restAnkleY + PlantAbove && new System.Numerics.Vector2(a.X - shownAnk[i].X, a.Z - shownAnk[i].Z).Length() * sc / f.Dt < HandInStill;
            _handIn[i] = declared && (basePlanted[i] || still); _handInSpot[i] = a; _flagWas2[i] = false; _relS[i] = AnchorRelease; _pathOff[i] = false; _pathOffU[i] = 0f; _early[i] = false; _arc[i] = 0f; _turnU[i] = 1f;
        }
        _handInRun = 0f; _startT = s.Elapsed;
        // (the lift's look-ahead from the first tick: the feet as shown on the tick before, the ball where it was then)
        for (int i = 0; i < 2; i++) { _liftTgt[i] = shownAnk[i]; _liftHave[i] = _haveShown; }
        _liftBall = M(f.BallPos - f.BallVel * f.Dt, sc);
        if (Dbg) GD.Print($"SKILL_DBG hand-in L={_handIn[0]} R={_handIn[1]}");
        GD.Print($"SKILL_CLIP start move={_move} foot={(foot == 1 ? "R" : "L")} clip={_stand.Clip.Name}/{_run.Clip.Name} w_run={_wRun:F2} speed={speed:F2} clip_from={_map.ClipStart:F3} contact_s={simContact:F3} end_s={simEnd:F3} origin_s={origin:F3}");
    }

    /// <summary>
    /// The running clip's ending for the exit touch the core forecasts (made at once on the move's end tick): its second ending when
    /// the touch is the support foot's, its own otherwise; kept as it is while no touch is forecast. The two cross over in
    /// <see cref="EndingBlend"/> seconds.
    /// </summary>
    void ChooseEnding(in FigureFrame f, float dt)
    {
        if (_runAlt == null) return;
        var up = f.Upcoming;
        if (f.Skill.Valid && up.Valid && !up.IsSkill)
        {
            float want = (up.Right ? 1 : 0) != _foot ? 1f : 0f;
            if (want != _altWant) GD.Print($"SKILL_CLIP ending={(want > 0.5f ? _runAlt.Clip.Name : _run!.Clip.Name)} exit_foot={(up.Right ? "R" : "L")} t={_t:F3}");
            _altWant = want;
            _altU = want > _altU ? Math.Min(want, _altU + dt / EndingBlend) : Math.Max(want, _altU - dt / EndingBlend);
        }
        _wAlt = Smooth(_altU);
    }

    /// <summary>The running clip (its second ending crossed in by <see cref="_wAlt"/>) at clip time <paramref name="ct"/> into <see cref="_b"/>; its hips.</summary>
    NVec3 RunSample(float ct)
    {
        var hb = _run!.Clip.Sample(ct, _b);
        if (_runAlt == null || _wAlt <= 0f) return hb;
        var hc = _runAlt.Clip.Sample(ct, _c);
        for (int k = 0; k < _n; k++) _b[k] = _wAlt >= 1f ? _c[k] : NQuat.Slerp(_b[k], _c[k], _wAlt);
        return NVec3.Lerp(hb, hc, _wAlt);
    }

    /// <summary>The running clip's root at <paramref name="ct"/>, its second ending crossed in.</summary>
    System.Numerics.Vector2 RunRoot(float ct) => _runAlt == null || _wAlt <= 0f ? _run!.Root(ct) : System.Numerics.Vector2.Lerp(_run!.Root(ct), _runAlt.Root(ct), _wAlt);

    /// <summary>The blended clip pose at simulation time <paramref name="t"/>, placed on the simulation's position and facing.</summary>
    void ClipPose(in FigureFrame f, float sc, float t, NQuat[] outRot, out NVec3 hips)
    {
        float ct = _map!.ClipTime(t);
        if (outRot == _src) _ct = ct;
        var ha = _stand!.Clip.Sample(ct, _a);
        var hb = RunSample(ct);
        for (int k = 0; k < _n; k++) outRot[k] = _wRun <= 0f ? _a[k] : _wRun >= 1f ? _b[k] : NQuat.Slerp(_a[k], _b[k], _wRun);
        var h = NVec3.Lerp(ha, hb, _wRun);
        var root = System.Numerics.Vector2.Lerp(_stand.Root(ct), RunRoot(ct), _wRun);
        var q = Placement(f);
        var rm = M(f.Position, sc); rm.Y = 0f;
        hips = rm + NVec3.Transform(new NVec3(h.X - root.X, h.Y, h.Z - root.Y), q);
        outRot[_sk.Hips] = NQuat.Normalize(q * outRot[_sk.Hips]);
    }

    /// <summary>
    /// The clip's turn onto the simulation's facing: relative to its root's fixed heading, so a turn the clip makes is kept, except for
    /// the spin, whose 360 degrees the simulation's facing already makes (the clip's own heading is taken out).
    /// </summary>
    NQuat Placement(in FigureFrame f)
    {
        float own = _move == "spin" ? _stand!.Heading(_ct) * (1f - _wRun) + _run!.Heading(_ct) * _wRun : _stand!.RootYaw * (1f - _wRun) + _run!.RootYaw * _wRun;
        return Spring.Yaw(ModelSpace.YawFromFacing(f.Facing) - own);
    }

    /// <summary>The authored ball centre this tick, placed as the pose is.</summary>
    NVec3 ClipBall(in FigureFrame f, float sc)
    {
        var rb = _runAlt == null || _wAlt <= 0f ? _run!.Ball(_ct) : NVec3.Lerp(_run!.Ball(_ct), _runAlt.Ball(_ct), _wAlt);
        var b = NVec3.Lerp(_stand!.Ball(_ct), rb, _wRun);
        var root = System.Numerics.Vector2.Lerp(_stand.Root(_ct), RunRoot(_ct), _wRun);
        var rm = M(f.Position, sc); rm.Y = 0f;
        return rm + NVec3.Transform(new NVec3(b.X - root.X, b.Y, b.Z - root.Y), Placement(f));
    }

    /// <summary>The shown pose's angular and linear velocities from the last two ticks shown.</summary>
    void ShownVelocities(float dt)
    {
        for (int k = 0; k < _n; k++)
        {
            _angV[k] = Spring.AngularVelocity(_shRPrev[k], _shR[k], dt);
            _linV[k] = (_shP[k] - _shPPrev[k]) / dt;
        }
    }
    readonly NQuat[] _clipPrevR = new NQuat[64];
    readonly NVec3[] _clipPrevP = new NVec3[64];

    /// <summary>A span of the clip during which a foot's surface is pinned to the ball (clip seconds), and the surface the core names for it.</summary>
    struct Pin { public int Foot; public float From, To, RampFrom; public BootSurface Surface; public bool Path; }
    readonly System.Collections.Generic.List<Pin> _pins = new();

    /// <summary>
    /// The move's pins from its clip: each <c>ball.on</c> span, each touch the core also makes (<c>contact</c>, <c>contact2</c>), and a
    /// step-over's circling foot (its path kept relative to the ball from contact to release). Each foot and surface must agree with
    /// the core's <c>Foot</c> / <c>Foot2</c>; a disagreement is printed (SKILL_CLIP disagree) and the clip's foot is kept.
    /// </summary>
    void BuildPins(FigureSkill s)
    {
        _pins.Clear();
        var c = _stand!;
        int f1 = s.Right ? 1 : 0, f2 = s.Right2 ? 1 : 0;
        bool two = !float.IsNaN(s.Contact2Elapsed);
        BootSurface SurfaceFor(int foot, string ev, string what)
        {
            bool second = ev == "contact2";
            if (second && two) { if (foot != f2) Disagree(what, foot, f2); return s.Surface2; }
            if (foot == f1) return s.Surface;
            if (two && foot == f2) return s.Surface2;
            Disagree(what, foot, f1); return s.Surface;
        }
        foreach (var o in c.On)
            _pins.Add(new Pin { Foot = o.Foot, From = c.EventSeconds(o.From), To = c.EventSeconds(o.To), Surface = SurfaceFor(o.Foot, o.From, $"on {o.From}-{o.To}") });
        foreach (var t in c.Touches)
            if (t.From is "contact" or "contact2" && !_pins.Exists(q => q.Foot == t.Foot && Math.Abs(q.From - c.EventSeconds(t.From)) < 1e-4f))
                _pins.Add(new Pin { Foot = t.Foot, From = c.EventSeconds(t.From), To = c.EventSeconds(t.From), Surface = SurfaceFor(t.Foot, t.From, $"touch {t.From}") });
        if (_pins.Count == 0 && _move == "step_over")
            _pins.Add(new Pin { Foot = f1, From = c.EventSeconds("contact"), To = c.EventSeconds("release"), Surface = BootSurface.Outside, Path = true });
        _pins.Sort((a, b) => a.From.CompareTo(b.From));
        float prevTo = float.NegativeInfinity;
        for (int i = 0; i < _pins.Count; i++)
        {
            var q = _pins[i];
            // the first ramps in from where the clip starts, a later one over at most 0.12 s after the one before it ends
            q.RampFrom = i == 0 ? Math.Min(_map!.ClipStart, q.From) : Math.Min(q.From, Math.Max(prevTo, q.From - 0.12f));
            prevTo = Math.Max(prevTo, q.To);
            _pins[i] = q;
        }
    }

    void Disagree(string what, int clipFoot, int coreFoot) =>
        GD.Print($"SKILL_CLIP disagree move={_move} clip={_stand!.Clip.Name} {what}: clip foot {(clipFoot == 1 ? "R" : "L")}, core foot {(coreFoot == 1 ? "R" : "L")}");

    /// <summary>A pin's weight at clip time <paramref name="ct"/>: up through its reach, 1 over its span, down after it; <paramref name="exact"/> inside the span before its last tick.</summary>
    static float PinWeight(in Pin q, float ct, out bool exact)
    {
        exact = false;
        if (ct < q.From - 1e-4f) return q.From - q.RampFrom > 1e-4f ? Ramp((ct - q.RampFrom) / (q.From - q.RampFrom)) : (ct >= q.RampFrom ? 1f : 0f);
        if (ct <= q.To) { exact = ct < q.To - 1e-4f || q.To == q.From; return 1f; }   // (on the end tick the core's exit touch may already move the ball: the pin holds where it was)
        return 1f - Smooth((ct - q.To) / PinRampOut);
    }

    /// <summary>The strongest pin on foot <paramref name="i"/> now (-1 when none).</summary>
    int PinOn(int i, out float w, out bool exact)
    {
        w = 0f; exact = false; int best = -1;
        for (int k = 0; k < _pins.Count; k++)
        {
            if (_pins[k].Foot != i) continue;
            float wk = PinWeight(_pins[k], _ct, out bool ek);
            if (wk > w) { w = wk; exact = ek; best = k; }
        }
        return best;
    }

    static float Smooth(float x) { x = Math.Clamp(x, 0f, 1f); return x * x * (3f - 2f * x); }

    /// <summary>
    /// How much the move's own touch pin <paramref name="q"/> (a span of one tick) has its foot from a touch the figure draws: up
    /// over the pin's ramp, 1 on its tick, down after it over <see cref="PinRampOut"/> or by the release, whichever is sooner (the
    /// exit touch the figure draws then is made on the end tick).
    /// </summary>
    float OwnWeight(in Pin q)
    {
        if (_ct <= q.To + 1e-4f) return PinWeight(q, _ct, out _);
        float rel = _stand!.EventSeconds("release");
        float span = rel > q.To + 1e-4f ? Math.Min(PinRampOut, rel - q.To) : 1e-4f;
        return 1f - Smooth((_ct - q.To) / span);
    }

    /// <summary>
    /// The reach's ramp: an even pace with an eased start and arrival (its first and last <see cref="RampEase"/> of the time), so
    /// the foot covers the reach at no more than 1.4 times its mean pace (a smoothstep peaks at 1.5) and lands on the ball at its pace.
    /// </summary>
    static float Ramp(float u)
    {
        u = Math.Clamp(u, 0f, 1f);
        const float a = RampEase;
        float v = 1f / (1f - a);
        if (u < a) return 0.5f * v * u * u / a;
        if (u > 1f - a) { float r = 1f - u; return 1f - 0.5f * v * r * r / a; }
        return 0.5f * v * a + v * (u - a);
    }
    const float RampEase = 0.3f;

    /// <summary>The playing foot onto the live ball, the planted feet held, an exit touch's leg taken by its weight, the boots out of the ground.</summary>
    void Constrain(in FigureFrame f, float sc, ReadOnlySpan<float> tw, float dt)
    {
        _handInRun += new System.Numerics.Vector2(f.Velocity.X, f.Velocity.Z).Length() * dt / sc;
        _sk.Forward(_out, _outP[0], _gr, _gp, _outP);
        // per foot the strongest pin, and the ball it holds: the live ball while the clip has the foot on it, then where it was at
        // the span's end (the core's exit touch moves the ball away as the foot lets go)
        var live = M(f.BallPos, sc);
        var want = NVec3.Zero;
        for (int i = 0; i < 2; i++)
        {
            // every pin on this foot adds its shift by its weight (two pins of one foot overlap as one lets go and the next takes
            // it, as an elastico's outside push hands to its inside snap): the target moves on smoothly between them
            _pinRaw[i] = 0f; _pinShift[i] = NVec3.Zero; _pinIdx[i] = -1; _pinOwn[i] = 0f;
            float sum = 0f;
            for (int k2 = 0; k2 < _pins.Count; k2++)
            {
                var q = _pins[k2];
                if (q.Foot != i) continue;
                float wk = PinWeight(q, _ct, out bool exact);
                if (!_pinHave[k2] || exact || _ct < q.From - 1e-4f) { _pinBallK[k2] = live; _pinHave[k2] = _ct >= q.From - 1e-4f; }
                if (wk <= 0f) continue;
                _pinShift[i] += (PinTarget(f, sc, i, q, _pinBallK[k2], 1f) - _gp[_sk.Foot[i]]) * wk;
                sum += wk;
                if (_pinIdx[i] < 0 || wk > PinWeight(_pins[_pinIdx[i]], _ct, out _)) _pinIdx[i] = k2;
            }
            if (sum > 1f) _pinShift[i] /= sum;
            // a pin on its span (or at its touch) has the foot to itself: the core's contact is drawn exactly
            for (int k2 = 0; k2 < _pins.Count; k2++)
            {
                var q = _pins[k2];
                if (q.Foot != i) continue;
                if (_ct >= q.From - 1e-4f && _ct <= q.To + 1e-4f)
                { _pinShift[i] = PinTarget(f, sc, i, q, _pinBallK[k2], 1f) - _gp[_sk.Foot[i]]; sum = 1f; _pinIdx[i] = k2; }
                // a touch of the move itself (an elastico's snap, a heel-to-heel's flick) is not a touch for the figure to draw
                // over it: the pin takes the foot from a touch the figure draws (the exit touch winding up) over its ramp, has it
                // on its tick, and gives it back by the release (the exit touch is made on the end tick)
                if (q.From == q.To) _pinOwn[i] = Math.Max(_pinOwn[i], OwnWeight(q));
            }
            _pinRaw[i] = Math.Min(1f, sum);
            // (where the pins put the ankle: kept in place when the hips come toward the ball below)
            _pinAbs[i] = _gp[_sk.Foot[i]] + _pinShift[i];
            // a ball the leg cannot reach brings the hips toward it (eased, at most ReachShiftMax)
            if (_pinRaw[i] > 0f)
            {
                var tgt = _gp[_sk.Foot[i]] + _pinShift[i];
                var d = tgt - _gp[_sk.UpLeg[i]];
                float len = d.Length(), most = ReachFrom * _legLen;
                if (len > most) { var wi = d * ((len - most) / len); wi.Y = Math.Min(0f, wi.Y); if (wi.LengthSquared() > want.LengthSquared()) want = wi; }
            }
        }
        // the figure's own pose, for a touch it draws now (the exit touch)
        // a touch the figure draws (the exit touch) takes its leg by its weight; one still recovering as the clip began is left to
        // the hand-over's offset until it has faded out
        for (int i = 0; i < 2; i++)
        {
            if (_twBlocked[i] && tw[i] <= 0f) _twBlocked[i] = false;
            // a touch that is dropped (an exit touch the core then does not make) lets go of the leg over TouchFade, not in a tick
            float raw = _twBlocked[i] ? 0f : Math.Clamp(tw[i], 0f, 1f);
            _twS[i] = Math.Max(raw, _twS[i] - dt / TouchFade);
            _twx[i] = _twS[i];
        }
        tw = _twx;
        bool anyTouch = tw[0] > 0f || tw[1] > 0f;
        var baseGp = anyTouch ? new NVec3[_n] : null;
        if (anyTouch) _sk.Forward(_baseR, _baseP[0], _baseG, baseGp!, _baseP);
        // so does the figure's foot for a touch it draws with a foot that made a touch of the move's own, by the touch's weight (the
        // clip's body is not where the figure's is: a fake cut's exit touch, 0.1 s after the hook, reaches past the leg)
        for (int i = 0; i < 2 && anyTouch; i++)
        {
            float twi = tw[i] * (1f - _pinOwn[i]);
            if (twi <= 0f || !OwnTouchFoot(i)) continue;
            var d = baseGp![_sk.Foot[i]] - _gp[_sk.UpLeg[i]];
            float len = d.Length(), most = ReachFrom * _legLen;
            if (len > most) { var wi = d * ((len - most) / len * twi); wi.Y = Math.Min(0f, wi.Y); if (wi.LengthSquared() > want.LengthSquared()) want = wi; }
        }
        if (want.Length() > ReachShiftMax / sc) want *= ReachShiftMax / sc / want.Length();
        float k = 1f - MathF.Exp(-dt / ReachShiftTau);
        _reachOff = NVec3.Lerp(_reachOff, want, k);
        if (_reachOff.LengthSquared() > 1e-10f)
        {
            _outP[0] += _reachOff;
            _sk.Forward(_out, _outP[0], _gr, _gp, _outP);
        }
        // the clip's own pose (before the hand-over's offset), for the feet of its declared stances: moved with the hips' reach
        // toward the ball along the ground, not down with them (a foot in the air keeps its height over the pitch)
        _sk.Forward(_src, _srcP[0] + new NVec3(_reachOff.X, 0f, _reachOff.Z), _clipG, _clipP, _srcP);
        for (int i = 0; i < 2; i++)
        {
            int up = _sk.UpLeg[i], leg = _sk.Leg[i], foot = _sk.Foot[i];
            NVec3 ankle = _gp[foot], target = ankle;
            NQuat gFoot = _gr[foot];
            _pinW[i] = 0f;
            // a touch the core makes with this foot (the exit touch) takes it from the pin, except from a touch of the move's own
            float twi = tw[i] * (1f - _pinOwn[i]);
            float pinI = _pinIdx[i] < 0 ? 0f : _pinRaw[i] * (1f - twi);
            // a foot the clip plants is held where it landed (not while it plays the ball or a touch)
            bool contact = (_stand!.Contacts(_ct) & (i == 0 ? Clip.LeftFoot : Clip.RightFoot)) != 0;
            if (_wRun > 0.5f) contact = ((_wAlt > 0.5f ? _runAlt! : _run!).Contacts(_ct) & (i == 0 ? Clip.LeftFoot : Clip.RightFoot)) != 0;
            var runClip = _wAlt > 0.5f ? _runAlt! : _run!;
            bool declared = _wRun > 0.5f && _declared.Contains(runClip.Clip.Name);
            // (a step-over's circling foot that a declared stance plants, as it lands out of the circle, is the stance's from then
            // on: its path round the ball is not a touch to hold it to)
            // (the path's hold on it lets go over PathOffFade, not in a tick: entered in the stand/run blend band, the path has the
            // foot well away from the running clip's own as the stance begins)
            if (declared && contact && pinI > 0f && _pins[_pinIdx[i]].Path) _pathOff[i] = true;
            float pathKeep = 1f;
            if (_pathOff[i]) { _pathOffU[i] = Math.Min(1f, _pathOffU[i] + dt / PathOffFade); pathKeep = 1f - Smooth(_pathOffU[i]); pinI *= pathKeep; }
            if (pinI > 0f) _pinW[i] = pinI;
            bool flagged = contact && (_pathOff[i] || _pinW[i] <= 1e-3f) && twi <= 1e-3f;
            // a stance its move file declares (a running clip's few-tick stances): the foot is taken from the hand-over's offset to
            // the clip's own foot (stepping into place, lifted when it has some way to go), held there through the stance, and
            // follows the clip's own foot from then on (its hops and landings as authored). Waiting for the offset to die away would
            // leave it gliding over the pitch, toes down, through most of a stance. It lands where the clip's foot is as the stance
            // begins and stays there; at the take-off the clip's foot has moved on from that spot (the simulation outruns or trails
            // the clip's root), and the difference is carried and dies away by its next stance, so the foot leaves from where it stood
            // and comes down where the clip has it.
            var c = _clipP[foot];
            float way = new System.Numerics.Vector2(c.X - ankle.X, c.Z - ankle.Z).Length();
            // (a foot the run had planted at the hand-in stays where it stood: a stance the clip has for it then is held there)
            bool heldIn = _handIn[i] && declared;
            // (held there for more than HandInTravel of his travel, it takes off before the clip's foot does, toward the clip's next
            // stance, over an arc: the run had planted it before the hand-in, and holding it on through the clip's stance would
            // leave it far behind him)
            if (heldIn && flagged && _flagWas[i] && _handInRun > HandInTravel / sc) { heldIn = _handIn[i] = false; _early[i] = true; }
            if (_early[i] && !contact) _early[i] = false;
            if (_early[i]) flagged = false;
            if (heldIn && flagged && !_flagWas[i]) { _land[i] = new NVec3(_lock[i].Locked ? _lock[i].Lock.X : _handInSpot[i].X, c.Y, _lock[i].Locked ? _lock[i].Lock.Z : _handInSpot[i].Z); _legU[i] = 1f; _turnU[i] = 0f; }
            else if (declared && flagged && !_flagWas[i]) { _pullS[i] = Math.Clamp(way * sc / StepSpeed, _legU[i] < 1f ? StanceTakeMin : StancePullMin, StancePullMax); _land[i] = c + _anchor[i]; }
            // the take-off from a stance: the offset of where the foot stood from the clip's foot dies away by its next landing (or
            // over AnchorRelease when it has none, or that is later), so it comes down where the clip has it
            if (declared && !flagged && _flagWas[i]) { _relS[i] = Math.Min(AnchorRelease, NextStance(runClip, i)); _arc[i] = _early[i] ? Math.Min(StepLift / sc, 0.5f * _anchor0[i].Length()) : 0f; }
            if (flagged) _arc[i] = 0f;
            _flagWas[i] = declared && flagged;
            if (declared && flagged) { _anchor0[i] = new NVec3(_land[i].X - c.X, 0f, _land[i].Z - c.Z); _relT[i] = 0f; }
            else _relT[i] += dt;
            _anchor[i] = _anchor0[i] * (1f - Smooth(_relT[i] / _relS[i]));
            c += _anchor[i];
            _legU[i] = declared && flagged ? Math.Min(1f, _legU[i] + dt / _pullS[i]) : declared ? _legU[i] : 0f;
            float lw = Smooth(_legU[i]);
            if (lw > 0f)
            {
                var to = NVec3.Lerp(ankle, c, lw);
                if (flagged && lw < 1f) to.Y += Math.Min(StepLift / sc, 0.5f * way) * 4f * lw * (1f - lw);
                if (_arc[i] > 0f && _relT[i] < _relS[i]) { float a = _relT[i] / _relS[i]; to.Y += _arc[i] * 4f * a * (1f - a); }
                target = to;
                // (a foot held from the hand-in turns from the run's foot to the clip's over StanceTakeMin)
                _turnU[i] = Math.Min(1f, _turnU[i] + dt / StanceTakeMin);
                gFoot = NQuat.Normalize(NQuat.Slerp(gFoot, _clipG[foot], lw * Smooth(_turnU[i])));
            }
            // the ball pin from there (by the exit touch's share): for a foot on its declared stances, from where they have it
            if (pinI > 0f && lw > 0f) target += (_pinAbs[i] + (_pinShift[i] / _pinRaw[i] - _pinShift[i]) - target) * _pinRaw[i] * (1f - twi) * pathKeep;
            else if (pinI > 0f) target += (_pinAbs[i] - target) * (1f - twi) * pathKeep;
            bool stance = flagged && (declared ? lw >= 1f : target.Y < _restAnkleY + PlantAbove);
            var lk = _lock[i];
            lk.SettleSeconds = declared ? StanceSettle : PlantSettle;
            // a foot the run had planted at the hand-in, the clip's foot not on a stance: held where it stood until the pose lifts it
            // (the hand-over's offset dies away from the run's pose into the clip's), then it leaves from there
            if (heldIn && !flagged && !_flagWas2[i] && _pinW[i] <= 1e-3f && twi <= 1e-3f)
            {
                if (!lk.Locked) target = new NVec3(_handInSpot[i].X, target.Y, _handInSpot[i].Z);
                stance = true; lk.SettleSeconds = 0f;
            }
            if (flagged) _flagWas2[i] = true;
            var held = new NVec3(lk.Lock.X, target.Y, lk.Lock.Z);
            bool far = lk.Locked && (held - _gp[up]).Length() > 0.99f * _legLen;
            var got = lk.Apply(stance, target, far, dt);
            target = got;
            if (_handIn[i] && (!lk.Locked || _flagWas2[i] && !flagged)) _handIn[i] = false;
            _planted[i] = lk.Settled;
            if (twi <= 0f) { _turnAxis[i] = NVec3.Zero; _twLim[i] = false; }
            if (twi > 0f)
            {
                var pre = target; var preRot = gFoot;
                target = NVec3.Lerp(target, baseGp![foot], twi);
                gFoot = TurnFoot(i, gFoot, _baseG[foot], twi);
                // the exit touch winding up for its contact on the move's end tick, made by a foot that made a touch of the move's own
                // (a fake cut's hook 0.1 s before, an elastico's push): its leg moves at most TouchStepMax a tick against the
                // hips (or evenly faster when the contact is due sooner than that would arrive) and reaches at most TouchReach of its
                // length, and its foot turns toward the touch's own turn ahead of the touch's weight (all of it by TouchTurnLead) at most
                // TouchTurnMax a tick: the figure's own wind-up can whip the boot back and round faster than the clip's body allows. The
                // contact is drawn where the touch has it.
                bool due = twi < 0.999f && f.Skill.Valid && f.Skill.Id == _moveId && OwnTouchFoot(i);
                if (due)
                {
                    if (!_twLim[i]) { _twLimP[i] = pre - _outP[0]; _twLimR[i] = preRot; _twLim[i] = true; }
                    float left = Math.Max(0f, f.Skill.Duration - f.Skill.Elapsed) / dt;
                    var d = target - _outP[0] - _twLimP[i];
                    float len = d.Length(), step = Math.Max(TouchStepMax / sc, len / (left + 1f));
                    if (len > step) target = _outP[0] + _twLimP[i] + d * (step / len);
                    // over the last TouchCatchTicks the leg goes straight for where the figure's windup has the ankle at the contact, an
                    // even share of the way each tick: the windup's own weights speed up at their end (standing, the hook's pose fading as
                    // the touch's comes in), which would leave the leg the last two ticks' worth of way to cover in one
                    if (left <= TouchCatchTicks && _tcHave[i])
                    {
                        var aim = M(_tcAnk[i], sc);
                        var cur = _outP[0] + _twLimP[i];
                        target = cur + (aim - cur) / (left + 1f);
                    }
                    var h = target - _gp[up];
                    if (h.Length() > TouchReach * _legLen) target = _gp[up] + h * (TouchReach * _legLen / h.Length());
                    var goal = NQuat.Normalize(NQuat.Slerp(preRot, _baseG[foot], Smooth(twi / TouchTurnLead)));
                    var r = NQuat.Normalize(goal * NQuat.Inverse(_twLimR[i]));
                    float ang = 2f * MathF.Acos(Math.Min(1f, Math.Abs(r.W)));
                    gFoot = ang > TouchTurnMax ? NQuat.Normalize(NQuat.Slerp(_twLimR[i], goal, TouchTurnMax / ang)) : goal;
                    _twLimP[i] = target - _outP[0]; _twLimR[i] = gFoot;
                }
                else if (_twLim[i])
                {
                    // (on the contact tick the foot turns the rest of the way, at most TouchTurnMax)
                    var r = NQuat.Normalize(gFoot * NQuat.Inverse(_twLimR[i]));
                    float ang = 2f * MathF.Acos(Math.Min(1f, Math.Abs(r.W)));
                    if (ang > TouchTurnMax) gFoot = NQuat.Normalize(NQuat.Slerp(_twLimR[i], gFoot, TouchTurnMax / ang));
                    _twLim[i] = false;
                }
                _planted[i] = false;
            }
            // a step-over's circling boot is lifted over the ball when it would pass through it, at most BallLiftRate: the hand-in
            // from the stride can carry the swinging foot through a ball close ahead (a step-over begun at a sprint)
            // (over the hand-in the stride's swing, carried on by the hand-over, can reach the ball within a tick or two: the lift
            // works from the first tick, before the path's pin has any weight, looks a tick ahead, the foot and the ball moving on
            // as they did, and rises at up to HandInLiftRate, so the boot clears the ball instead of passing through it while the
            // lift catches up)
            bool handInLift = _t - _startT < HandInLiftSeconds && _liftHave[i];
            bool pathFoot = _pinIdx[i] >= 0 ? _pins[_pinIdx[i]].Path : handInLift && PathPinOn(i);
            bool liftOn = twi <= 0f && pathFoot && !_pathOff[i];
            float needLift = liftOn ? LiftOverBall(i, target, gFoot, live, (f.BallRadius + BallClear) / sc) : 0f;
            if (liftOn && handInLift)
                needLift = Math.Max(needLift, LiftOverBall(i, 2f * target - _liftTgt[i], gFoot, 2f * live - _liftBall, (f.BallRadius + BallClear) / sc));
            float rise = handInLift ? HandInLiftRate : BallLiftRate;
            _ballLift[i] = needLift > _ballLift[i] ? Math.Min(needLift, _ballLift[i] + rise / sc * dt) : Math.Max(needLift, _ballLift[i] - BallLiftRate / sc * dt);
            _liftTgt[i] = target; _liftHave[i] = true;
            if (i == 1) _liftBall = live;
            target.Y += _ballLift[i];
            target = KeepAboveGround(i, target, gFoot);
            if (Dbg) GD.Print($"SKILL_DBG ct={_ct:F3} foot={i} ank=({ankle.X:F3},{ankle.Y:F3},{ankle.Z:F3}) tgt=({target.X:F3},{target.Y:F3},{target.Z:F3}) pin={_pinW[i]:F2} st={(stance ? 1 : 0)} lock={(lk.Locked ? 1 : 0)} tw={tw[i]:F2} far={(far ? 1 : 0)} legw={_legU[i]:F2}");
            SolveLeg(i, target, gFoot);
            if (Dbg && _pinIdx[i] >= 0)
            {
                _sk.Forward(_out, _outP[0], _gr, _gp, _outP);
                var sp = SurfacePoint(i, _pins[_pinIdx[i]].Surface, _gr[foot], _gp[foot]);
                GD.Print($"SKILL_DBG pin foot={i} surf={_pins[_pinIdx[i]].Surface} err={((sp - live).Length() - f.BallRadius / sc) * sc:F3} solved_vs_target={(_gp[foot] - target).Length() * sc:F3} own={_pinOwn[i]}");
            }
        }
    }

    /// <summary>The playing foot's ankle that puts its surface on <paramref name="ball"/> as the clip has it on its own ball (or, for a step-over, its path moved with the ball), by weight <paramref name="w"/>.</summary>
    NVec3 PinTarget(in FigureFrame f, float sc, int i, in Pin q, NVec3 ball, float w)
    {
        NVec3 ankle = _gp[_sk.Foot[i]];
        var cb = ClipBall(f, sc);
        NVec3 shift;
        if (q.Path) { shift = ball - cb; shift.Y = 0f; }
        else
        {
            var sp = SurfacePoint(i, q.Surface, _gr[_sk.Foot[i]], ankle);
            var d = sp - cb;
            var nrm = d.LengthSquared() > 1e-8f ? NVec3.Normalize(d) : NVec3.UnitY;
            shift = ball + nrm * (f.BallRadius / sc) - sp;
        }
        return ankle + shift * w;
    }

    NVec3 _reachOff;
    readonly NQuat[] _clipG = new NQuat[64];
    readonly NVec3[] _clipP = new NVec3[64];
    /// <summary>Per foot, how far a declared stance has taken the leg from the hand-over's offset to the clip's own foot (0..1, linear).</summary>
    readonly float[] _legU = new float[2];
    readonly float[] _pullS = { 0.03f, 0.03f };
    readonly bool[] _flagWas = new bool[2];
    readonly NVec3[] _land = new NVec3[2], _anchor = new NVec3[2], _anchor0 = new NVec3[2];
    readonly float[] _relT = new float[2];
    /// <summary>Seconds over which the offset of a foot that left its stance from the clip's foot dies away (eased), at most: it is gone by the foot's next stance.</summary>
    const float AnchorRelease = 0.14f;
    readonly float[] _relS = { AnchorRelease, AnchorRelease };
    /// <summary>
    /// Per foot, whether the run had it planted as the clip took over (a running clip with declared stances): it is held where it stood
    /// until the clip's first lift of it (the end of the stance the clip has it on then, or, when the clip has it off the ground, the
    /// hand-over lifting it), instead of being dragged onto the clip's foot. Its spot, model space.
    /// </summary>
    readonly bool[] _handIn = new bool[2], _flagWas2 = new bool[2];
    readonly NVec3[] _handInSpot = new NVec3[2];
    /// <summary>His travel (m) since the hand-in after which a foot held from it steps onto the clip's foot, and his travel so far (model).</summary>
    const float HandInTravel = 0.25f;
    float _handInRun;
    /// <summary>Per foot, whether a foot held from the hand-in took off before the clip's foot (until the clip's stance ends), and the height of its arc.</summary>
    readonly bool[] _early = new bool[2];
    readonly float[] _turnU = { 1f, 1f };
    readonly float[] _arc = new float[2];
    /// <summary>Per foot, whether a declared stance has taken it from a step-over's path pin.</summary>
    readonly bool[] _pathOff = new bool[2];
    readonly float[] _pathOffU = new float[2];
    /// <summary>Seconds over which a declared stance takes a step-over's circling foot from its path pin (eased).</summary>
    const float PathOffFade = 0.06f;
    /// <summary>A foot on the ground moving slower than this (m/s) as the clip takes over counts as planted (a landing settling; a swinging foot moves at twice the body's speed).</summary>
    const float HandInStill = 2.5f;

    /// <summary>Simulation seconds from now to the next declared stance of foot <paramref name="i"/> in <paramref name="c"/> (infinity when it has none).</summary>
    float NextStance(SkillClip c, int i)
    {
        byte bit = i == 0 ? Clip.LeftFoot : Clip.RightFoot;
        int f0 = Math.Max(0, (int)MathF.Round(_ct * c.Clip.Fps) + 1);
        while (f0 < c.Clip.FrameCount && (c.Clip.Contacts(f0) & bit) != 0) f0++;   // (past the stance it is on, when it leaves one early)
        for (int fr = f0; fr < c.Clip.FrameCount; fr++)
            if ((c.Clip.Contacts(fr) & bit) != 0)
            {
                // (the time map's inverse, by bisection: it rises monotonically)
                float want = fr / (float)c.Clip.Fps, lo = _t, hi = _t + 3f;
                for (int k = 0; k < 30; k++) { float mid = 0.5f * (lo + hi); if (_map!.ClipTime(mid) < want) lo = mid; else hi = mid; }
                return Math.Max(1e-3f, 0.5f * (lo + hi) - _t);
            }
        return float.PositiveInfinity;
    }
    /// <summary>
    /// A declared stance's foot is taken to the clip's own at StepSpeed (m/s), in StancePullMin to StancePullMax seconds (at least
    /// StanceTakeMin from the hand-over's pose, where the foot turns from the figure's to the clip's as well), lifted on the way by
    /// at most StepLift (metres).
    /// </summary>
    const float StepSpeed = 6f, StancePullMin = 1f / 120f, StanceTakeMin = 0.04f, StancePullMax = 0.08f, StepLift = 0.07f;
    /// <summary>A declared stance's settle (its landing speed carried on, fading, before it stands still), and every other planted foot's.</summary>
    const float StanceSettle = 0.017f, PlantSettle = 0.05f;
    readonly NVec3[] _pinShift = new NVec3[2], _pinBallK = new NVec3[8];
    readonly bool[] _pinHave = new bool[8];
    readonly int[] _pinIdx = { -1, -1 };
    readonly float[] _pinRaw = new float[2];
    /// <summary>Per foot, how much a touch pin of the move's own has the foot from a touch the figure draws (0..1).</summary>
    readonly float[] _pinOwn = new float[2];
    readonly NVec3[] _pinAbs = new NVec3[2];
    readonly float[] _twx = new float[2];
    readonly bool[] _twBlocked = new bool[2];
    readonly float[] _twS = new float[2];
    /// <summary>Seconds over which a touch's hold on a leg fades when the touch is dropped.</summary>
    const float TouchFade = 0.10f;
    /// <summary>Per foot, whether an exit touch winds up, and its leg as shown last tick (its ankle target against the hips, its foot's turn).</summary>
    readonly bool[] _twLim = new bool[2];
    readonly NVec3[] _twLimP = new NVec3[2];
    readonly NQuat[] _twLimR = new NQuat[2];
    /// <summary>
    /// An exit touch winding up moves its leg at most TouchStepMax (metres, against the hips) a tick, reaches at most TouchReach of
    /// its length, and turns its foot at most TouchTurnMax (radians) a tick, toward the touch's own turn by its weight over TouchTurnLead.
    /// </summary>
    const float TouchStepMax = 0.06f, TouchReach = 0.96f, TouchTurnMax = 0.24f, TouchTurnLead = 0.4f;
    /// <summary>Over its last this many ticks the exit touch's windup goes evenly for the ankle at its contact (see <see cref="SetTouchContact"/>).</summary>
    const float TouchCatchTicks = 3f;
    readonly bool[] _tcHave = new bool[2];
    readonly Vector3[] _tcAnk = new Vector3[2];
    /// <summary>Foot <paramref name="i"/>'s ankle (world) at the contact of the touch the figure winds up for now, or none.</summary>
    public void SetTouchContact(int i, bool have, Vector3 ankle) { _tcHave[i] = have; _tcAnk[i] = ankle; }
    /// <summary>The playing leg reaches its pinned target up to this share of its length; beyond, the hips come toward it (at most ReachShiftMax metres, eased over ReachShiftTau seconds).</summary>
    const float ReachFrom = 0.95f, ReachShiftMax = 0.18f, ReachShiftTau = 0.04f;

    /// <summary>The boot's surface point for a foot at <paramref name="ankle"/> turned to <paramref name="gFoot"/> (model space).</summary>
    NVec3 SurfacePoint(int i, BootSurface s, NQuat gFoot, NVec3 ankle)
    {
        var o = _body.SurfaceRest(i, s);
        var turn = gFoot * NQuat.Inverse(_sk.RestGlobal[_sk.Foot[i]]);
        return ankle + NVec3.Transform(new NVec3(o.X, o.Y, o.Z), turn);
    }

    readonly NVec3[] _turnAxis = new NVec3[2];

    /// <summary>
    /// Foot <paramref name="i"/> turned <paramref name="w"/> of the way from <paramref name="a"/> to <paramref name="b"/> (global
    /// rotations): the shorter way, except near half a turn, where the shorter way can change sides from one tick to the next as both
    /// move (a touch drawn toward a foot the clip has turned the other way round, as out of a spin): there it keeps turning the way it
    /// turned on the tick before.
    /// </summary>
    NQuat TurnFoot(int i, NQuat a, NQuat b, float w)
    {
        var r = NQuat.Normalize(b * NQuat.Inverse(a));
        if (r.W < 0f) r = new NQuat(-r.X, -r.Y, -r.Z, -r.W);
        float sh = MathF.Sqrt(r.X * r.X + r.Y * r.Y + r.Z * r.Z);
        if (sh < 1e-6f) { _turnAxis[i] = NVec3.Zero; return a; }
        var axis = new NVec3(r.X, r.Y, r.Z) / sh;
        float ang = 2f * MathF.Atan2(sh, r.W);
        if (ang > 0.6f * MathF.PI && _turnAxis[i].LengthSquared() > 0.5f && NVec3.Dot(axis, _turnAxis[i]) < 0f) { axis = -axis; ang = 2f * MathF.PI - ang; }
        _turnAxis[i] = axis;
        return NQuat.Normalize(NQuat.CreateFromAxisAngle(axis, ang * w) * a);
    }

    /// <summary>Whether foot <paramref name="i"/> makes a touch of the move's own (a pin of one tick).</summary>
    bool OwnTouchFoot(int i)
    {
        foreach (var q in _pins) if (q.Foot == i && q.From == q.To) return true;
        return false;
    }

    /// <summary>Whether foot <paramref name="i"/> has a step-over's path pin.</summary>
    bool PathPinOn(int i)
    {
        foreach (var q in _pins) if (q.Foot == i && q.Path) return true;
        return false;
    }

    /// <summary>How far the ankle target must rise for the boot's surfaces to be at least <paramref name="clear"/> from the ball's centre <paramref name="ball"/> (0 when they are).</summary>
    float LiftOverBall(int i, NVec3 target, NQuat gFoot, NVec3 ball, float clear)
    {
        float lift = 0f;
        for (int k = 0; k < 4; k++)
        {
            var p = SurfacePoint(i, (BootSurface)k, gFoot, target);
            float h2 = (p.X - ball.X) * (p.X - ball.X) + (p.Z - ball.Z) * (p.Z - ball.Z);
            if ((p - ball).LengthSquared() < clear * clear) lift = Math.Max(lift, ball.Y + MathF.Sqrt(clear * clear - h2) - p.Y);
        }
        return lift;
    }
    readonly float[] _ballLift = new float[2];
    /// <summary>A step-over's boot lifted over the ball stays this far (metres) off its surface; the lift changes at most this fast (m/s).</summary>
    const float BallClear = 0.01f, BallLiftRate = 1.5f;
    /// <summary>Over this long (s) from the clip's start the lift looks a tick ahead and rises at up to HandInLiftRate (m/s).</summary>
    const float HandInLiftSeconds = 0.05f, HandInLiftRate = 9f;
    /// <summary>Per foot, the ankle target before the lift on the tick before (model space), whether there is one; the live ball then.</summary>
    readonly NVec3[] _liftTgt = new NVec3[2];
    readonly bool[] _liftHave = new bool[2];
    NVec3 _liftBall;
    float _startT;

    /// <summary>Raises the ankle target so the boot (toe joint, toe tip, sole) stays above the ground.</summary>
    NVec3 KeepAboveGround(int i, NVec3 target, NQuat gFoot)
    {
        int toe = _sk.Toe[i];
        var toeJ = target + NVec3.Transform(_rest[toe], gFoot);
        var gToe = NQuat.Normalize(gFoot * _out[toe]);
        var tip = toeJ + NVec3.Transform(NVec3.UnitY * AttackerAnimator.ToeTip, gToe);
        var sole = SurfacePoint(i, BootSurface.Sole, gFoot, target);
        float low = Math.Min(toeJ.Y - 0.015f, Math.Min(tip.Y - 0.004f, sole.Y));
        if (low < 0f) target.Y -= low;
        return target;
    }

    /// <summary>Two-bone IK of leg <paramref name="i"/> of the shown pose onto <paramref name="target"/>, the foot turned to <paramref name="gFoot"/>.</summary>
    void SolveLeg(int i, NVec3 target, NQuat gFoot)
    {
        int up = _sk.UpLeg[i], leg = _sk.Leg[i], foot = _sk.Foot[i];
        NVec3 a = _gp[up], b = _gp[leg], c = _gp[foot];
        if ((target - c).LengthSquared() > 1e-12f)
        {
            var thighFwd = NVec3.Transform(NVec3.UnitZ, _gr[up] * NQuat.Inverse(_sk.RestGlobal[up]));
            var line = NVec3.Normalize(c - a);
            var kneeOff = (b - a) - line * NVec3.Dot(b - a, line);
            TwoBoneIk.Apply(a, b, c, _gr[up], _gr[leg], target, kneeOff + thighFwd * 0.02f, out var gUp2, out var gLeg2, out _, 0.97f);
            _out[up] = NQuat.Normalize(NQuat.Inverse(_gr[_sk.Parents[up]]) * gUp2);
            _out[leg] = NQuat.Normalize(NQuat.Inverse(gUp2) * gLeg2);
            _gr[up] = gUp2; _gr[leg] = gLeg2;
        }
        _out[foot] = NQuat.Normalize(NQuat.Inverse(_gr[leg]) * gFoot);
    }

    /// <summary>The clip hands back to the figure's pose: the bones by inertialisation, each foot by its ankle's offset from the figure's.</summary>
    void BeginHandBack(float sc, float dt)
    {
        _playing = false; _handBack = true;
        _hbTw0[0] = _hbTw0[1] = 0f; _hbStep[0] = _hbStep[1] = false;
        if (!_haveShownPrev || !_haveBasePrev) { _in.Reset(); _legOff[0] = _legOff[1] = NVec3.Zero; return; }
        for (int k = 0; k < _n; k++)
        {
            _dstAngV[k] = Spring.AngularVelocity(_baseRPrev[k], _baseR[k], dt);
            _dstLinV[k] = (_baseP[k] - _basePPrev[k]) / dt;
        }
        ShownVelocities(dt);
        // at the last tick shown, against the figure's pose then; the update this tick carries both on
        _in.Transition(_shR, _angV, _shP, _linV, _baseRPrev, _dstAngV, _basePPrev, _dstLinV);
        // the feet: shown ankle less the figure's, with the velocities' difference
        _sk.Forward(_shR, _shP[0], _gr, _gp, _shP);
        var shownNow = new NVec3[2]; for (int i = 0; i < 2; i++) shownNow[i] = _gp[_sk.Foot[i]];
        _sk.Forward(_shRPrev, _shPPrev[0], _gr, _gp, _shPPrev);
        var shownPrev = new NVec3[2]; for (int i = 0; i < 2; i++) shownPrev[i] = _gp[_sk.Foot[i]];
        _sk.Forward(_baseRPrev, _basePPrev[0], _gr, _gp, _basePPrev);
        var basePrev = new NVec3[2]; for (int i = 0; i < 2; i++) basePrev[i] = _gp[_sk.Foot[i]];
        _sk.Forward(_baseR, _baseP[0], _gr, _gp, _baseP);
        for (int i = 0; i < 2; i++)
        {
            var bn = _gp[_sk.Foot[i]];
            // the shown foot against the figure's at the last tick shown (the first hand-back tick decays it on by one tick)
            _legOff[i] = shownNow[i] - basePrev[i];
            _legOffVel[i] = (shownNow[i] - shownPrev[i]) / dt - (bn - basePrev[i]) / dt;
            // a touch already had this leg by its weight in the last tick shown: the offset carries that share already
            _hbTw0[i] = Math.Clamp(_twS[i], 0f, 1f) * (1f - _pinOwn[i]);
        }
    }
    readonly float[] _hbTw0 = new float[2];
    /// <summary>Per foot, the share of its hand-back offset a move the figure draws itself leaves it (0 on the move's playing foot by its contact).</summary>
    readonly float[] _hbOwn = { 1f, 1f };
    readonly bool[] _hbStep = new bool[2];
    /// <summary>Whether foot <paramref name="i"/> is stepping back in at the hand-back (it is not planted, whatever the figure's own pose has).</summary>
    public bool HandBackStepping(int i) => _handBack && _hbStep[i];

    /// <summary>The hand-back's legs: each ankle at the figure's plus its offset (dying away while the foot swings, held while it is planted; none at a touch's contact).</summary>
    void HandBackLegs(ReadOnlySpan<float> tw, ReadOnlySpan<bool> basePlanted, float dt)
    {
        _sk.Forward(_baseR, _baseP[0], _baseG, _gp, _baseP);
        var baseAnk = new NVec3[2]; for (int i = 0; i < 2; i++) baseAnk[i] = _gp[_sk.Foot[i]];
        _sk.Forward(_out, _outP[0], _gr, _gp, _outP);
        for (int i = 0; i < 2; i++)
        {
            var x = _legOff[i]; var v = _legOffVel[i];
            // (a touch takes the leg by its weight: the share it already had when the hand-back began is in the offset)
            float tk = _hbTw0[i] >= 0.999f ? 0f : Math.Clamp((1f - Math.Clamp(tw[i], 0f, 1f)) / (1f - _hbTw0[i]), 0f, 1f);
            tk *= _hbOwn[i];
            // a planted foot held where it is would be dragged once the leg cannot reach it there: it steps back in instead
            if (basePlanted[i] && tw[i] <= 0f && !_hbStep[i] && (baseAnk[i] + x * tk - _gp[_sk.UpLeg[i]]).Length() > 0.99f * _legLen) _hbStep[i] = true;
            if (basePlanted[i] && tw[i] <= 0f && !_hbStep[i])
            {
                // planted: held where it is, offset and all (it dies away once the foot swings again)
                v = NVec3.Zero;
            }
            else Spring.Decay(ref x, ref v, FootHandBackHalfLife, dt);
            if (_hbStep[i] && x.Length() < 0.01f * _legLen) _hbStep[i] = false;
            _legOff[i] = x; _legOffVel[i] = v;
            var target = baseAnk[i] + x * tk;
            // (the step back in clears the ground)
            if (_hbStep[i]) target.Y += Math.Min(0.05f * _legLen, 0.3f * new System.Numerics.Vector2(x.X, x.Z).Length());
            float twc = Math.Clamp(tw[i], 0f, 1f);
            if (twc <= 0f) _turnAxis[i] = NVec3.Zero;
            var gFoot = twc > 0f ? TurnFoot(i, _gr[_sk.Foot[i]], _baseG[_sk.Foot[i]], twc) : _gr[_sk.Foot[i]];
            SolveLeg(i, target, gFoot);
            _sk.Forward(_out, _outP[0], _gr, _gp, _outP);
        }
    }
}
