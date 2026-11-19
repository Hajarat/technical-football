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
/// figure draws it. When the clip has played out (or a shot or a reset cuts it) the body hands back to the figure's pose by
/// inertialisation, the feet by an ankle offset that dies away while the foot swings and is held while it is planted, so nothing
/// pops and nothing slides. Works in the body's model space (the model's own size, ground at y = 0). No clock and no randomness.
/// </summary>
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
    static string? _loadError;

    readonly SkinnedBody _body;
    readonly Skeleton _sk;
    readonly int _n;
    readonly NQuat[] _a, _b, _src, _out, _gr, _baseR, _baseRPrev, _shR, _shRPrev;
    readonly NVec3[] _gp, _rest, _srcP, _outP, _baseP, _basePPrev, _shP, _shPPrev, _angV, _linV, _dstAngV, _dstLinV;
    readonly NQuat[] _baseG;
    readonly Inertializer _in;
    readonly PlantLock[] _lock = { new(), new() };
    readonly float _restAnkleY, _legLen;
    bool _haveShown, _haveShownPrev, _haveBase, _haveBasePrev;

    // the move being played
    bool _playing, _handBack, _transition;
    int _moveId = int.MinValue, _foot;
    SkillClip? _stand, _run;
    float _wRun, _t, _ct, _shotElapsed = float.NaN, _prevSpeed = float.NaN;
    SkillTimeMap? _map;
    BootSurface _surface;
    string _move = "";
    readonly NVec3[] _legOff = new NVec3[2], _legOffVel = new NVec3[2], _baseAnkPrev = new NVec3[2];
    readonly float[] _pinW = new float[2];
    readonly bool[] _planted = new bool[2];

    /// <summary>"clips" (this layer plays the authored clips) or "procedural" (the figure's own drawing of every move).</summary>
    public string Mode = "clips";

    SkillClipLayer(SkinnedBody body, Skeleton sk)
    {
        _body = body; _sk = sk; _n = sk.Count;
        _a = new NQuat[_n]; _b = new NQuat[_n]; _src = new NQuat[_n]; _out = new NQuat[_n]; _gr = new NQuat[_n];
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
        SkillKind.BallRoll => "ball_roll", SkillKind.FakeStop => "fake_stop", _ => "fake_cut",
    };

    /// <summary>Whether the clips can draw this move (mode clips, a clip pair for its foot and side).</summary>
    public bool Covers(in FigureSkill s) => Mode == "clips" && _lib != null && s.Valid && _lib.Pair(MoveName(s.Kind), s.Right ? 1 : 0, Math.Sign((int)s.Side)) != null;

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
        _playing = _handBack = _transition = false; _moveId = int.MinValue;
        _haveShown = _haveShownPrev = _haveBase = _haveBasePrev = false; _prevSpeed = float.NaN;
        _in.Reset(); foreach (var l in _lock) l.Reset();
        for (int i = 0; i < 2; i++) { _legOff[i] = _legOffVel[i] = NVec3.Zero; _pinW[i] = 0f; _planted[i] = false; }
        _reachOff = NVec3.Zero; _pinBallHave = false;
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

        bool covers = Covers(f.Skill);
        if (covers && f.Skill.Id != _moveId) { Start(f, sc); for (int i = 0; i < 2; i++) _twBlocked[i] = tw[i] > 0f; }
        else if (!f.Skill.Valid && !_playing) _shotElapsed = f.Shot.Valid ? f.Shot.Elapsed : float.NaN;
        if (_playing)
        {
            _t = f.Skill.Valid && f.Skill.Id == _moveId ? f.Skill.Elapsed : _t + dt;
            // the clip has played out, or a shot or another move it cannot draw takes over
            bool cut = !f.Skill.Valid && f.Shot.Valid || f.Skill.Valid && f.Skill.Id != _moveId;
            if (_t > _map!.SimEnd + 1e-4f || cut) BeginHandBack(sc, dt);
        }

        if (_playing)
        {
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

    void Start(in FigureFrame f, float sc)
    {
        var s = f.Skill;
        int foot = s.Right ? 1 : 0;
        var pair = _lib!.Pair(MoveName(s.Kind), foot, Math.Sign((int)s.Side))!.Value;
        _stand = pair.Stand; _run = pair.Run; _foot = foot; _move = MoveName(s.Kind); _surface = s.Surface;
        // the speed he came in at: the last tick before the move (on its first tick the move already moves him)
        float speed = float.IsNaN(_prevSpeed) ? new Vector2(f.Velocity.X, f.Velocity.Z).Length() : _prevSpeed;
        _wRun = SkillEntryBlend.RunWeight(speed, _stand.EntrySpeed, _run.EntrySpeed);
        float simContact = s.Elapsed - s.ContactElapsed, simEnd = s.Duration;
        // a fake shot's wind-up began at the shot's press: the time since then counts as gone
        float origin = float.IsNaN(_shotElapsed) ? 0f : -Math.Max(0f, _shotElapsed + f.Dt - s.Elapsed);
        var c = _stand;
        _map = new SkillTimeMap(c.EventSeconds("start"), c.EventSeconds("contact"), c.EventSeconds("release"), c.EventSeconds("end"), origin, simContact, simEnd);
        _moveId = s.Id; _t = s.Elapsed; _playing = true; _handBack = false; _transition = true;
        foreach (var l in _lock) l.Reset();
        _shotElapsed = float.NaN; _pinBallHave = false;
        GD.Print($"SKILL_CLIP start move={_move} foot={(foot == 1 ? "R" : "L")} clip={_stand.Clip.Name}/{_run.Clip.Name} w_run={_wRun:F2} speed={speed:F2} clip_from={_map.ClipStart:F3} contact_s={simContact:F3} end_s={simEnd:F3} origin_s={origin:F3}");
    }

    /// <summary>The blended clip pose at simulation time <paramref name="t"/>, placed on the simulation's position and facing.</summary>
    void ClipPose(in FigureFrame f, float sc, float t, NQuat[] outRot, out NVec3 hips)
    {
        float ct = _map!.ClipTime(t);
        if (outRot == _src) _ct = ct;
        var ha = _stand!.Clip.Sample(ct, _a);
        var hb = _run!.Clip.Sample(ct, _b);
        for (int k = 0; k < _n; k++) outRot[k] = _wRun <= 0f ? _a[k] : _wRun >= 1f ? _b[k] : NQuat.Slerp(_a[k], _b[k], _wRun);
        var h = NVec3.Lerp(ha, hb, _wRun);
        var root = System.Numerics.Vector2.Lerp(_stand.Root(ct), _run.Root(ct), _wRun);
        var q = Placement(f);
        var rm = M(f.Position, sc); rm.Y = 0f;
        hips = rm + NVec3.Transform(new NVec3(h.X - root.X, h.Y, h.Z - root.Y), q);
        outRot[_sk.Hips] = NQuat.Normalize(q * outRot[_sk.Hips]);
    }

    NQuat Placement(in FigureFrame f) => Spring.Yaw(ModelSpace.YawFromFacing(f.Facing) - (_stand!.RootYaw * (1f - _wRun) + _run!.RootYaw * _wRun));

    /// <summary>The authored ball centre this tick, placed as the pose is.</summary>
    NVec3 ClipBall(in FigureFrame f, float sc)
    {
        var b = NVec3.Lerp(_stand!.Ball(_ct), _run!.Ball(_ct), _wRun);
        var root = System.Numerics.Vector2.Lerp(_stand.Root(_ct), _run.Root(_ct), _wRun);
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

    /// <summary>The pin weight of the playing foot at clip time <paramref name="ct"/>: up through the reach, 1 over an on-ball span, down after it.</summary>
    float PinWeightAt(float ct, out bool exact)
    {
        exact = false;
        var c = _stand!;
        float w = 0f;
        float from = float.NaN, to = float.NaN;
        foreach (var s in c.On) if (s.Foot == _foot) { from = c.EventSeconds(s.From); to = c.EventSeconds(s.To); break; }
        // a touch event (the fake cut's hook): the surface meets the ball on it, the pin letting go after it
        if (float.IsNaN(from))
            foreach (var s in c.Touches)
                if (s.Foot == _foot)
                {
                    float e = c.EventSeconds(s.From);
                    if (float.IsNaN(from) || e < from) from = to = e;
                }
        if (float.IsNaN(from) && _move == "step_over") { from = c.EventSeconds("contact"); to = c.EventSeconds("release"); }
        if (float.IsNaN(from)) return 0f;
        float c0 = Math.Min(_map!.ClipStart, from);
        if (ct < from) w = from - c0 > 1e-4f ? Ramp((ct - c0) / (from - c0)) : 1f;
        else if (ct <= to) { w = 1f; exact = ct < to - 1e-4f; }   // (on the end tick the core's exit touch may already move the ball: the pin holds where it was)
        else w = 1f - Smooth((ct - to) / PinRampOut);
        return w;
    }

    static float Smooth(float x) { x = Math.Clamp(x, 0f, 1f); return x * x * (3f - 2f * x); }

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
        _sk.Forward(_out, _outP[0], _gr, _gp, _outP);
        // the ball the pin holds: the live ball while the clip has the foot on it, then where it was at the span's end (the core's
        // exit touch moves the ball away as the foot lets go)
        var live = M(f.BallPos, sc);
        float pin = PinWeightAt(_ct, out bool exact);
        if (exact || !_pinBallHave) { _pinBall = live; _pinBallHave = true; }
        var ball = exact || _ct < PinFrom() ? live : _pinBall;
        if (_ct < PinFrom()) _pinBallHave = false;
        // a ball the playing leg cannot reach brings the hips toward it (eased, at most ReachShiftMax)
        var want = NVec3.Zero;
        if (pin > 0f)
        {
            int fp = _foot;
            var tgt = PinTarget(f, sc, fp, ball, pin);
            var hip = _gp[_sk.UpLeg[fp]];
            var d = tgt - hip;
            float len = d.Length(), most = ReachFrom * _legLen;
            if (len > most) { want = d * ((len - most) / len); want.Y = Math.Min(0f, want.Y); }
            if (want.Length() > ReachShiftMax / sc) want *= ReachShiftMax / sc / want.Length();
        }
        float k = 1f - MathF.Exp(-dt / ReachShiftTau);
        _reachOff = NVec3.Lerp(_reachOff, want, k);
        if (_reachOff.LengthSquared() > 1e-10f)
        {
            _outP[0] += _reachOff;
            _sk.Forward(_out, _outP[0], _gr, _gp, _outP);
        }
        // the figure's own pose, for a touch it draws now (the exit touch)
        bool anyTouch = tw[0] > 0f && !_twBlocked[0] || tw[1] > 0f && !_twBlocked[1];
        var baseGp = anyTouch ? new NVec3[_n] : null;
        if (anyTouch) _sk.Forward(_baseR, _baseP[0], _baseG, baseGp!, _baseP);
        // a touch the figure draws (the exit touch) takes its leg by its weight; one still recovering as the clip began is left to
        // the hand-over's offset until it has faded out
        for (int i = 0; i < 2; i++)
        {
            if (_twBlocked[i] && tw[i] <= 0f) _twBlocked[i] = false;
            _twx[i] = _twBlocked[i] ? 0f : Math.Clamp(tw[i], 0f, 1f);
        }
        tw = _twx;
        for (int i = 0; i < 2; i++)
        {
            int up = _sk.UpLeg[i], leg = _sk.Leg[i], foot = _sk.Foot[i];
            NVec3 ankle = _gp[foot], target = ankle;
            NQuat gFoot = _gr[foot];
            _pinW[i] = 0f;
            // a touch the core makes with this foot (the exit touch) takes it from the pin
            float pinI = pin * (1f - tw[i]);
            if (i == _foot && pinI > 0f) { _pinW[i] = pinI; target = PinTarget(f, sc, i, ball, pinI); }
            // a foot the clip plants is held where it landed (not while it plays the ball or a touch)
            bool contact = (_stand!.Contacts(_ct) & (i == 0 ? Clip.LeftFoot : Clip.RightFoot)) != 0;
            if (_wRun > 0.5f) contact = (_run!.Contacts(_ct) & (i == 0 ? Clip.LeftFoot : Clip.RightFoot)) != 0;
            bool stance = contact && _pinW[i] <= 1e-3f && tw[i] <= 1e-3f && target.Y < _restAnkleY + PlantAbove;
            var lk = _lock[i];
            var held = new NVec3(lk.Lock.X, target.Y, lk.Lock.Z);
            bool far = lk.Locked && (held - _gp[up]).Length() > 0.99f * _legLen;
            var got = lk.Apply(stance, target, far, dt);
            target = got;
            _planted[i] = lk.Settled;
            // (not the playing foot while the clip has it on the ball: a touch the figure still draws there is the move's own)
            float twi = tw[i];
            if (twi > 0f)
            {
                target = NVec3.Lerp(target, baseGp![foot], twi);
                gFoot = NQuat.Slerp(gFoot, _baseG[foot], twi);
                _planted[i] = false;
            }
            target = KeepAboveGround(i, target, gFoot);
            if (Dbg) GD.Print($"SKILL_DBG ct={_ct:F3} foot={i} ank=({ankle.X:F3},{ankle.Y:F3},{ankle.Z:F3}) tgt=({target.X:F3},{target.Y:F3},{target.Z:F3}) pin={_pinW[i]:F2} st={(stance ? 1 : 0)} lock={(lk.Locked ? 1 : 0)} tw={tw[i]:F2}");
            SolveLeg(i, target, gFoot);
        }
    }

    /// <summary>The playing foot's ankle that puts its surface on <paramref name="ball"/> as the clip has it on its own ball (or, for a step-over, its path moved with the ball), by weight <paramref name="w"/>.</summary>
    NVec3 PinTarget(in FigureFrame f, float sc, int i, NVec3 ball, float w)
    {
        NVec3 ankle = _gp[_sk.Foot[i]];
        var cb = ClipBall(f, sc);
        NVec3 shift;
        if (_move == "step_over") { shift = ball - cb; shift.Y = 0f; }
        else
        {
            var sp = SurfacePoint(i, _surface, _gr[_sk.Foot[i]], ankle);
            var d = sp - cb;
            var nrm = d.LengthSquared() > 1e-8f ? NVec3.Normalize(d) : NVec3.UnitY;
            shift = ball + nrm * (f.BallRadius / sc) - sp;
        }
        return ankle + shift * w;
    }

    /// <summary>The clip time the pin's span (or touch) begins.</summary>
    float PinFrom()
    {
        var c = _stand!;
        foreach (var s in c.On) if (s.Foot == _foot) return c.EventSeconds(s.From);
        float from = float.NaN;
        foreach (var s in c.Touches) if (s.Foot == _foot) { float e = c.EventSeconds(s.From); if (float.IsNaN(from) || e < from) from = e; }
        if (float.IsNaN(from) && _move == "step_over") from = c.EventSeconds("contact");
        return float.IsNaN(from) ? float.MaxValue : from;
    }

    NVec3 _pinBall, _reachOff;
    readonly float[] _twx = new float[2];
    readonly bool[] _twBlocked = new bool[2];
    bool _pinBallHave;
    /// <summary>The playing leg reaches its pinned target up to this share of its length; beyond, the hips come toward it (at most ReachShiftMax metres, eased over ReachShiftTau seconds).</summary>
    const float ReachFrom = 0.95f, ReachShiftMax = 0.18f, ReachShiftTau = 0.04f;

    /// <summary>The boot's surface point for a foot at <paramref name="ankle"/> turned to <paramref name="gFoot"/> (model space).</summary>
    NVec3 SurfacePoint(int i, BootSurface s, NQuat gFoot, NVec3 ankle)
    {
        var o = _body.SurfaceRest(i, s);
        var turn = gFoot * NQuat.Inverse(_sk.RestGlobal[_sk.Foot[i]]);
        return ankle + NVec3.Transform(new NVec3(o.X, o.Y, o.Z), turn);
    }

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
        }
    }

    /// <summary>The hand-back's legs: each ankle at the figure's plus its offset (dying away while the foot swings, held while it is planted; none at a touch's contact).</summary>
    void HandBackLegs(ReadOnlySpan<float> tw, ReadOnlySpan<bool> basePlanted, float dt)
    {
        _sk.Forward(_baseR, _baseP[0], _baseG, _gp, _baseP);
        var baseAnk = new NVec3[2]; for (int i = 0; i < 2; i++) baseAnk[i] = _gp[_sk.Foot[i]];
        _sk.Forward(_out, _outP[0], _gr, _gp, _outP);
        for (int i = 0; i < 2; i++)
        {
            var x = _legOff[i]; var v = _legOffVel[i];
            if (basePlanted[i] && tw[i] <= 0f)
            {
                // planted: held where it is (its height still settles)
                float y = x.Y, vy = v.Y;
                Spring.Decay(ref y, ref vy, FootHandBackHalfLife, dt);
                x.Y = y; v = new NVec3(0f, vy, 0f);
            }
            else Spring.Decay(ref x, ref v, FootHandBackHalfLife, dt);
            _legOff[i] = x; _legOffVel[i] = v;
            var target = baseAnk[i] + x * (1f - Math.Clamp(tw[i], 0f, 1f));
            var gFoot = NQuat.Slerp(_gr[_sk.Foot[i]], _baseG[_sk.Foot[i]], Math.Clamp(tw[i], 0f, 1f));
            SolveLeg(i, target, gFoot);
            _sk.Forward(_out, _outP[0], _gr, _gp, _outP);
        }
    }
}
