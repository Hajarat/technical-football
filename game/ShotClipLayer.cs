using System;
using System.Collections.Generic;
using FootballSim.Anim;
using Godot;
using NQuat = System.Numerics.Quaternion;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace FootballSim.Game;

/// <summary>
/// --shot-anim mocap: the attacker's struck shot drawn from motion capture (game/assets/anim/attacker/shots, cut by
/// tools/anim/shots.py from two CMU kicks: a jogging kick and one from standing, each for either foot). The simulation decides
/// everything; this layer only draws it.
///
/// From the shoot press the core publishes the shot (<see cref="FigureFrame.Shot"/>: its striking foot, surface and kind, and the
/// seconds to its contact, which takes the place of the next touch). The standing and the running clip of the striking foot are
/// blended by the speed at the press (fixed for the shot) on a common timeline whose knots are the clips' events (start, takeoff,
/// backswing, plant, contact, end). The layer enters the clip as late in its wind-up as the time left to the contact asks for (a
/// running clip no earlier than its takeoff, so no clip foot is held on the ground while the body runs on at a sprint), never
/// playing it fast to fit; then the clip runs on the time to the contact the core publishes, so <c>contact</c> falls on the strike tick
/// (a later estimate slows it, and one that would slow it below <see cref="RateMin"/> before the plant hands back and enters again).
/// The pose relative to the clip's root goes on the simulation's position and facing turned toward the aim; from the entry to the
/// plant it is drawn toward where the clip has the body relative to the ball at the core's contact point (at most
/// <see cref="MaxBallOffset"/>), so the plant foot lands beside the ball and the trunk is over it at the contact, and it is handed back
/// to the simulation's position through the follow-through. The plant foot reaches the rest of the way to its place beside the
/// ball, and every foot the clip has on the ground is held where it landed (<see cref="PlantLock"/>). On the strike tick the
/// striking foot's surface (instep for power, inside for finesse, as the core says) is pinned to the ball (the pin ramps in over the
/// swing's last <see cref="PinIn"/> seconds and out over <see cref="PinOut"/>). Charge and kind scale the striking leg's swing about its
/// contact pose (a light or finesse shot a shorter backswing and follow-through, a full power shot the clip's whole swing); a finesse
/// shot turns the leg out onto the inside of the foot, a chip leans the trunk back. The clip is entered and left by inertialisation
/// (<see cref="Inertializer"/>), its feet handed back by an ankle offset that dies away while the foot swings. A fake read during the
/// wind-up (<see cref="FigureFrame.PendingSkill"/>) hands back to the figure's own (procedural) shot drawing on that tick.
/// Works in the body's model space (the model's own size, ground at y = 0). No clock and no randomness.
/// </summary>
public sealed class ShotClipLayer
{
    const string Folder = "res://assets/anim/attacker/shots";
    /// <summary>Slowest and fastest the clip may run against the simulation while it is fitted to the time left to the contact.</summary>
    public const float RateMin = 0.45f, RateMax = 1.5f;
    /// <summary>The most the body is drawn toward its place relative to the ball (metres).</summary>
    public const float MaxBallOffset = 0.6f;
    /// <summary>Seconds (clip timeline) over which the striking foot's pin to the ball ramps in before the contact and out after it.</summary>
    public const float PinIn = 0.07f, PinOut = 0.06f;
    /// <summary>Below this speed (m/s) at the press the standing clip is drawn, from RunFrom on the running clip, blended between.</summary>
    public const float StandTo = 1.2f, RunFrom = 3.0f;
    /// <summary>The running clip's least share in a power or driven shot (from standing it lends the swing its height).</summary>
    public const float StandRunShare = 0.4f;
    /// <summary>How much faster (share) the follow-through plays at a sprint (7 m/s) than at a jog (3 m/s and below).</summary>
    public const float FollowSpeedUp = 0.3f;
    /// <summary>A clip-planted foot is held only once it is this close (model metres) to its resting height.</summary>
    const float PlantAbove = 0.03f;
    const float FootHandBackHalfLife = 0.06f;

    // the timeline's knots (clips.json events, in order) and their indexes
    static readonly string[] Knots = { "start", "takeoff", "backswing", "plant", "contact", "end" };
    const int KStart = 0, KTakeoff = 1, KBack = 2, KPlant = 3, KContact = 4, KEnd = 5, KLast = 6;

    static readonly bool Dbg = System.Environment.GetEnvironmentVariable("SHOT_CLIP_DBG") == "1";

    sealed class ShotClip
    {
        public Clip Clip = null!;
        public int Foot;
        public bool Run;
        public float[] E = new float[7];
        public NVec2[] Root = Array.Empty<NVec2>();
        public NVec3 KickDir;
        public NVec2 RootAt(float s)
        {
            float f = Math.Clamp(s * Clip.Fps, 0f, Root.Length - 1);
            int a = (int)MathF.Floor(f), b = Math.Min(a + 1, Root.Length - 1);
            return NVec2.Lerp(Root[a], Root[b], f - a);
        }
        public byte Contacts(float s) => Clip.Contacts(Math.Clamp((int)MathF.Round(s * Clip.Fps), 0, Clip.FrameCount - 1));
    }

    static List<ShotClip>? _clips;
    static Skeleton? _libSk;
    static string? _loadError;

    readonly SkinnedBody _body;
    readonly Skeleton _sk;
    readonly int _n;
    readonly NQuat[] _a, _b, _src, _out, _gr, _baseR, _baseRPrev, _shR, _shRPrev, _cR, _baseG, _clipPrevR;
    readonly NVec3[] _gp, _rest, _srcP, _outP, _baseP, _basePPrev, _shP, _shPPrev, _angV, _linV, _dstAngV, _dstLinV, _clipPrevP;
    readonly Inertializer _in;
    readonly PlantLock[] _lock = { new(), new() };
    readonly float _restAnkleY, _legLen;
    readonly int _spine, _spine1;
    readonly float[] _twistSign = new float[2];
    bool _haveShown, _haveShownPrev, _haveBase, _haveBasePrev;

    /// <summary>"mocap" (this layer draws a struck shot) or "procedural" (the figure's own drawing, <c>PlanShot</c>).</summary>
    public string Mode = "procedural";

    // the shot being drawn
    int _shotId = int.MinValue;
    bool _playing, _handBack, _transition, _done, _contactDone, _headingFrozen;
    int _foot;
    ShotClip? _stand, _run;
    float _w, _u, _uEntry, _amp, _heading, _rate = 1f;
    ShotKind _kind;
    NVec3 _off, _res, _ballClip;
    bool _offOk;
    FigureShot _shot;
    readonly bool[] _planted = new bool[2];
    readonly float[] _lowY = new float[2];
    /// <summary>How far (metres) the plant foot's ankle may rise as its heel lifts before it is let go.</summary>
    const float PlantLiftOff = 0.09f;

    /// <summary>On the strike tick: whether the layer drew it, and the striking foot's heading as drawn (world, flat), which the contact check measures with.</summary>
    public bool StrikeDrawn { get; private set; }
    public Vector3 StrikeDir { get; private set; }

    ShotClipLayer(SkinnedBody body, Skeleton sk)
    {
        _body = body; _sk = sk; _n = sk.Count;
        _a = new NQuat[_n]; _b = new NQuat[_n]; _src = new NQuat[_n]; _out = new NQuat[_n]; _gr = new NQuat[_n];
        _baseR = new NQuat[_n]; _baseRPrev = new NQuat[_n]; _shR = new NQuat[_n]; _shRPrev = new NQuat[_n]; _cR = new NQuat[_n];
        _baseG = new NQuat[_n]; _clipPrevR = new NQuat[_n];
        _gp = new NVec3[_n]; _rest = new NVec3[_n]; _srcP = new NVec3[_n]; _outP = new NVec3[_n]; _baseP = new NVec3[_n]; _basePPrev = new NVec3[_n];
        _shP = new NVec3[_n]; _shPPrev = new NVec3[_n]; _angV = new NVec3[_n]; _linV = new NVec3[_n]; _dstAngV = new NVec3[_n]; _dstLinV = new NVec3[_n];
        _clipPrevP = new NVec3[_n];
        for (int k = 0; k < _n; k++) _rest[k] = sk.Offsets[k];
        _in = new Inertializer(_n);
        var rl = new NQuat[_n]; for (int k = 0; k < _n; k++) rl[k] = sk.RestLocal[k];
        sk.Forward(rl, sk.Offsets[0], _gr, _gp);
        _restAnkleY = _gp[sk.Foot[0]].Y;
        _legLen = sk.Offsets[sk.Leg[0]].Length() + sk.Offsets[sk.Foot[0]].Length();
        _spine = sk.Find("Spine"); _spine1 = sk.Find("Spine1");
        // which way a twist of the thigh about its own axis turns the toes out (left foot out = model +x)
        for (int i = 0; i < 2; i++)
        {
            var q = (NQuat[])rl.Clone();
            q[sk.UpLeg[i]] = NQuat.Normalize(q[sk.UpLeg[i]] * NQuat.CreateFromAxisAngle(NVec3.UnitY, 0.3f));
            sk.Forward(q, sk.Offsets[0], _gr, _gp);
            float toeX = _gp[sk.Toe[i]].X - _gp[sk.Foot[i]].X;
            sk.Forward(rl, sk.Offsets[0], _gr, _gp);
            float toeX0 = _gp[sk.Toe[i]].X - _gp[sk.Foot[i]].X;
            float outward = i == 0 ? 1f : -1f;
            _twistSign[i] = (toeX - toeX0) * outward > 0f ? 1f : -1f;
        }
    }

    /// <summary>The layer for <paramref name="body"/> on the stride cycles' skeleton <paramref name="sk"/>, or null (and why) when the clips are missing or do not fit.</summary>
    public static ShotClipLayer? TryCreate(SkinnedBody body, Skeleton sk, out string error)
    {
        error = _loadError ?? "";
        if (_clips == null && _loadError == null)
        {
            try
            {
                var json = FileAccess.GetFileAsString(Folder + "/clips.json");
                if (string.IsNullOrEmpty(json)) throw new System.IO.FileNotFoundException($"{Folder}/clips.json not found");
                var lib = ClipLibrary.Parse(json, file =>
                {
                    var bytes = FileAccess.GetFileAsBytes($"{Folder}/{file}");
                    if (bytes == null || bytes.Length == 0) throw new System.IO.FileNotFoundException($"{Folder}/{file} not found");
                    return bytes;
                });
                var list = new List<ShotClip>();
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    int i = 0;
                    foreach (var c in doc.RootElement.GetProperty("clips").EnumerateArray())
                    {
                        var e = lib.Clips[i++];
                        if (e.Verdict == ClipVerdict.Reject || e.Set != "shot") continue;
                        var sc = new ShotClip { Clip = e.Clip, Foot = c.GetProperty("foot").GetString() == "right" ? 1 : 0, Run = c.GetProperty("entry").GetString() == "run" };
                        for (int k = 0; k < Knots.Length; k++)
                        {
                            if (!e.Clip.Events.TryGetValue(Knots[k], out int fr)) throw new System.IO.InvalidDataException($"{e.Clip.Name}: no event {Knots[k]}");
                            sc.E[k] = fr / (float)e.Clip.Fps;
                        }
                        sc.E[KLast] = (e.Clip.FrameCount - 1) / (float)e.Clip.Fps;
                        for (int k = 1; k <= KLast; k++)
                            if (sc.E[k] <= sc.E[k - 1]) throw new System.IO.InvalidDataException($"{e.Clip.Name}: events out of order");
                        var root = new List<NVec2>();
                        foreach (var r in c.GetProperty("root").EnumerateArray()) root.Add(new NVec2(r[0].GetSingle(), r[1].GetSingle()));
                        sc.Root = root.ToArray();
                        var kd = c.GetProperty("kick_dir");
                        sc.KickDir = new NVec3(kd[0].GetSingle(), kd[1].GetSingle(), kd[2].GetSingle());
                        list.Add(sc);
                    }
                }
                _clips = list; _libSk = lib.Skeleton;
                GD.Print($"SHOT_ANIM clips={list.Count}");
            }
            catch (Exception e) { _loadError = $"shot clips: {e.Message}"; error = _loadError; }
        }
        if (_clips == null || _libSk == null) return null;
        if (_libSk.Count != sk.Count) { error = $"the shot clips have {_libSk.Count} bones, the body {sk.Count}"; return null; }
        for (int k = 0; k < sk.Count; k++)
            if (_libSk.Names[k] != sk.Names[k]) { error = $"shot clip bone {k} is {_libSk.Names[k]}, the body drives {sk.Names[k]}"; return null; }
        for (int f = 0; f < 2; f++)
            if (Pick(f, false) == null || Pick(f, true) == null) { error = $"no standing and running shot clip for the {(f == 1 ? "right" : "left")} foot"; return null; }
        return new ShotClipLayer(body, sk);
    }

    static ShotClip? Pick(int foot, bool run)
    {
        foreach (var c in _clips!) if (c.Foot == foot && c.Run == run) return c;
        return null;
    }

    /// <summary>True while a clip draws the body or the body is still being handed back from one.</summary>
    public bool Active => _playing || _handBack;
    public bool Playing => _playing;
    /// <summary>The striking foot of the shot being drawn (0 left, 1 right).</summary>
    public int Foot => _foot;
    /// <summary>Whether foot <paramref name="i"/> is held where it landed this tick (only while <see cref="Playing"/>).</summary>
    public bool Planted(int i) => _planted[i];
    public string Describe() => _playing ? $"u={_u:F3} w_run={_w:F2} rate={_rate:F2}" : _handBack ? "hand-back" : "-";

    public void Reset()
    {
        _playing = _handBack = _transition = false; _shotId = int.MinValue; _done = false;
        _haveShown = _haveShownPrev = _haveBase = _haveBasePrev = false; _offOk = false;
        _in.Reset(); foreach (var l in _lock) l.Reset();
        for (int i = 0; i < 2; i++) { _legOff[i] = _legOffVel[i] = NVec3.Zero; _planted[i] = false; _hbStep[i] = false; }
    }

    static NVec3 M(Vector3 world, float sc) => ModelSpace.FromWorld(new NVec3(world.X, world.Y, world.Z)) / sc;
    static Vector3 W(NVec3 model, float sc) { var w = ModelSpace.ToWorld(model) * sc; return new Vector3(w.X, w.Y, w.Z); }
    static float Smooth(float x) { x = Math.Clamp(x, 0f, 1f); return x * x * (3f - 2f * x); }

    // ---- the common timeline: u runs 0..6 through the knots; a span's length is the two clips' blended ----

    float Span(int j) => (1f - _w) * (_stand!.E[j + 1] - _stand.E[j]) + _w * (_run!.E[j + 1] - _run.E[j]);

    /// <summary>Blended clip seconds from <paramref name="u0"/> to <paramref name="u1"/> (u0 &lt;= u1).</summary>
    float TimeTo(float u0, float u1)
    {
        float t = 0f;
        for (int j = (int)MathF.Floor(u0); j < KLast && j < u1; j++)
        {
            float a = Math.Max(u0, j), b = Math.Min(u1, j + 1);
            if (b > a) t += (b - a) * Span(j);
        }
        return t;
    }

    /// <summary>The point of the timeline <paramref name="rem"/> blended seconds before the contact (not before the start).</summary>
    float UBeforeContact(float rem)
    {
        float u = KContact;
        for (int j = KContact - 1; j >= 0 && rem > 0f; j--)
        {
            float s = Span(j);
            if (rem <= s) return j + 1 - rem / s;
            rem -= s; u = j;
        }
        return u;
    }

    float Advance(float u, float t)
    {
        while (t > 0f && u < KLast)
        {
            int j = Math.Min((int)MathF.Floor(u), KLast - 1);
            float s = Span(j), left = (j + 1 - u) * s;
            if (t < left) return u + t / s;
            t -= left; u = j + 1;
        }
        return Math.Min(u, KLast);
    }

    static float ClipTime(ShotClip c, float u)
    {
        int j = Math.Clamp((int)MathF.Floor(u), 0, KLast - 1);
        float fr = Math.Clamp(u - j, 0f, 1f);
        return c.E[j] + fr * (c.E[j + 1] - c.E[j]);
    }

    /// <summary>
    /// One physics tick: <paramref name="rot"/> / <paramref name="pos"/> hold the figure's pose (local rotations and positions in the
    /// clip bone order, index 0 the hips' position, model space) and are replaced by what is shown. <paramref name="tw"/> is each
    /// foot's touch weight, <paramref name="basePlanted"/> whether the figure has it planted, <paramref name="ankleH"/> the figure's
    /// ankle height (metres; the contact check's boot offsets are taken from it).
    /// </summary>
    public void Apply(in FigureFrame frame, float height, float ankleH, ReadOnlySpan<float> tw, ReadOnlySpan<bool> basePlanted, bool teleport, Quaternion[] rot, Vector3[] pos)
    {
        float dt = frame.Dt > 0f ? frame.Dt : 1f / 120f;
        float sc = _body.ScaleFor(height);
        for (int k = 0; k < _n; k++) { _baseR[k] = new NQuat(rot[k].X, rot[k].Y, rot[k].Z, rot[k].W); _baseP[k] = new NVec3(pos[k].X, pos[k].Y, pos[k].Z); }
        _planted[0] = _planted[1] = false;
        StrikeDrawn = false;
        if (teleport) Reset();
        var f = frame;
        var sh = f.Shot;
        bool on = Mode == "mocap";
        bool fake = f.PendingSkill.Valid || f.Skill.Valid;

        if (on && sh.Valid && sh.Id != _shotId) { if (_playing) BeginHandBack(dt, "next shot"); NewShot(f); }
        if (_shotId != int.MinValue && sh.Valid && sh.Id == _shotId) _shot = sh;
        // the core moved the strike to the other foot before the contact (a touch played in the wind-up): drawn again on that foot,
        // from the pose shown
        if (on && sh.Valid && sh.Id == _shotId && !sh.Made && (sh.Right ? 1 : 0) != _foot)
        {
            if (_playing) BeginHandBack(dt, "strike foot changed");
            NewShot(f);
        }
        // the shot's recovery may end before the clip's follow-through: the follow-through plays on (from the shot as it was last)
        // unless a skill move or another shot takes over
        bool carry = _playing && _contactDone && !sh.Valid && !fake && on;
        if (carry) f.Shot = _shot;
        else if (_shotId != int.MinValue && (!sh.Valid || sh.Id != _shotId || !on))
        {
            if (_playing) BeginHandBack(dt, "shot over");
            _shotId = int.MinValue;
        }
        if (_playing && fake)
        {
            // a fake read in the wind-up: the figure's own shot drawing (the procedural wind-up, then the move) from this tick
            BeginHandBack(dt, "fake read"); _done = true;
        }
        if (_shotId != int.MinValue && fake) _done = true;
        if (!_playing && !_done && _shotId != int.MinValue && f.Shot.Valid) TryEnter(f, sc);
        if (_playing) Step(f, dt);
        if (!_playing && carry) _shotId = int.MinValue;

        if (_playing)
        {
            ClipPose(f, sc, _u, _src, out var hips, dt);
            for (int k = 0; k < _n; k++) _srcP[k] = _rest[k];
            _srcP[0] = hips;
            if (_transition)
            {
                _transition = false;
                if (_haveShownPrev)
                {
                    float uPrev = Math.Max(0f, _u - _rate * dt / Math.Max(Span(Math.Min((int)_u, KLast - 1)), 1e-4f));
                    ClipPose(f, sc, uPrev, _clipPrevR, out var hipsPrev, 0f);
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
            Constrain(f, sc, ankleH, height, tw, dt);
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

        for (int k = 0; k < _n; k++)
        {
            _shRPrev[k] = _shR[k]; _shPPrev[k] = _shP[k]; _shR[k] = _out[k]; _shP[k] = _outP[k];
            _baseRPrev[k] = _baseR[k]; _basePPrev[k] = _baseP[k];
        }
        _haveShownPrev = _haveShown; _haveShown = true; _haveBasePrev = _haveBase; _haveBase = true;
        for (int k = 0; k < _n; k++) { rot[k] = new Quaternion(_out[k].X, _out[k].Y, _out[k].Z, _out[k].W); pos[k] = new Vector3(_outP[k].X, _outP[k].Y, _outP[k].Z); }
    }

    void NewShot(in FigureFrame f)
    {
        _shotId = f.Shot.Id; _done = false; _contactDone = false; _chargeMax = 0f; _amp = 0f;
        _foot = f.Shot.Right ? 1 : 0;
        _stand = Pick(_foot, false); _run = Pick(_foot, true);
        float speed = new Vector2(f.Velocity.X, f.Velocity.Z).Length();
        _w = SkillEntryBlend.RunWeight(speed, StandTo, RunFrom);
        // a power or driven shot from standing takes some of the running kick's longer swing (its backswing and follow-through)
        if (f.Shot.Kind is ShotKind.Power or ShotKind.Driven) _w = Math.Max(_w, StandRunShare);
        _kind = f.Shot.Kind;
        GD.Print($"SHOT_CLIP shot id={_shotId} foot={(_foot == 1 ? "R" : "L")} kind={_kind} speed={speed:F2} w_run={_w:F2} eta={f.Shot.ContactEta:F3}");
    }

    /// <summary>Enters the clip when the time left to the contact fits it (see the class summary); waits while it is longer or the ball is out of reach.</summary>
    void TryEnter(in FigureFrame f, float sc)
    {
        var sh = f.Shot;
        float uMin = _w >= 0.5f ? KTakeoff : KStart;
        float u;
        if (sh.Made) u = KContact;
        else
        {
            float eta = Math.Max(sh.ContactEta, 0f);
            if (eta > TimeTo(uMin, KContact) + 1e-4f) return;
            Vector3 bodyAtC = new Vector3(f.Position.X, 0, f.Position.Z) + new Vector3(f.Velocity.X, 0, f.Velocity.Z) * eta;
            if (Dist2(sh.ContactPoint, bodyAtC) > 1.3f * f.HeightM / 1.8f) return;
            u = Math.Max(uMin, UBeforeContact(eta));
        }
        _u = _uEntry = u; _rate = 1f; _contactDone = sh.Made;
        if (_amp <= 0f) _amp = Amplitude(f);
        _playing = true; _handBack = false; _transition = true; _headingFrozen = false; _offOk = false;
        _heading = f.Facing;
        foreach (var l in _lock) l.Reset();
        // the striking leg's contact pose, which charge and kind scale the swing about
        SampleBlend(KContact, _cR, out _);
        _ballClip = ClipBall(sc, f.BallRadius);
        GD.Print($"SHOT_CLIP enter id={_shotId} u={u:F3} ct_run={ClipTime(_run!, u):F3} ct_stand={ClipTime(_stand!, u):F3} eta={sh.ContactEta:F3} to_contact={TimeTo(u, KContact):F3}");
    }

    static float Dist2(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    /// <summary>Moves the timeline on: fitted to the time left to the contact before it, onto the contact on the strike tick, in real time after it.</summary>
    void Step(in FigureFrame f, float dt)
    {
        var sh = f.Shot;
        if (_transition) return;   // the entry tick shows the entry point
        if (!sh.Made)
        {
            float eta = Math.Max(sh.ContactEta, 0f);
            float rem = TimeTo(_u, KContact);
            float rate = rem / (eta + dt);
            if (rate < RateMin && _u < KPlant)
            {
                // the estimate slipped later by more than the clip can wait: back to the figure's pose, entered again when it fits
                BeginHandBack(dt, $"estimate slipped (rate {rate:F2})");
                return;
            }
            _rate = Math.Clamp(rate, RateMin, RateMax);
            _u = Math.Min(Advance(_u, _rate * dt), KContact - 1e-4f);
        }
        else if (!_contactDone) { _contactDone = true; _u = KContact; _rate = 1f; }
        else
        {
            // after the contact the clip's follow-through runs a little faster the faster he runs on (its hop covers his run's ground)
            float v = new Vector2(f.Velocity.X, f.Velocity.Z).Length();
            _rate = 1f + FollowSpeedUp * Math.Clamp((v - 3f) / 4f, 0f, 1f);
            _u = Advance(_u, _rate * dt);
        }
        if (_u >= KEnd) BeginHandBack(dt, "follow-through over");
    }

    /// <summary>The two clips sampled at timeline point <paramref name="u"/> and blended (unplaced); their blended hips.</summary>
    NVec3 SampleBlend(float u, NQuat[] outRot, out NVec2 root)
    {
        float cs = ClipTime(_stand!, u), cr = ClipTime(_run!, u);
        var ha = _stand!.Clip.Sample(cs, _a);
        var hb = _run!.Clip.Sample(cr, _b);
        for (int k = 0; k < _n; k++) outRot[k] = _w <= 0f ? _a[k] : _w >= 1f ? _b[k] : NQuat.Slerp(_a[k], _b[k], _w);
        root = NVec2.Lerp(_stand.RootAt(cs), _run.RootAt(cr), _w);
        return NVec3.Lerp(ha, hb, _w);
    }

    /// <summary>The clip's ball (unplaced model space) at the contact: the striking surface of its contact pose, a ball's radius on along its kick.</summary>
    NVec3 ClipBall(float sc, float radius)
    {
        var r = new NQuat[_n];
        var h = SampleBlend(KContact, r, out _);
        _sk.Forward(r, h, _gr, _gp);
        var sp = SurfacePoint(_foot, BootSurface.Laces, _gr[_sk.Foot[_foot]], _gp[_sk.Foot[_foot]]);
        var kd = NVec3.Lerp(_stand!.KickDir, _run!.KickDir, _w);
        var c = sp + NVec3.Normalize(kd) * (radius / sc);
        c.Y = radius / sc;
        return c;
    }

    // the striking leg's swing scale, the finesse turn-out and the chip's lean back (weights along the timeline)
    float SwingScaleW(float u) => u < KBack ? Smooth((u - KTakeoff) / (KBack - KTakeoff)) : u <= KEnd ? 1f : 1f - Smooth(u - KEnd);
    static float FinesseW(float u) => u <= KContact ? Smooth((u - KBack) / (KContact - KBack)) : 1f - Smooth(u - KContact);
    static float ChipW(float u) => u <= KContact ? Smooth(u - KPlant) : 1f - Smooth((u - KContact) / (KEnd - KContact));

    /// <summary>The swing's scale for the shot's charge and kind: 1 = the clip's whole swing.</summary>
    float Amplitude(in FigureFrame f)
    {
        _chargeMax = Math.Max(_chargeMax, f.ShotCharge);
        float c = Math.Clamp(_chargeMax, 0f, 1f);
        return f.Shot.Kind switch
        {
            ShotKind.Power or ShotKind.Driven => 0.8f + 0.2f * c,
            ShotKind.Chip => 0.6f + 0.2f * c,
            _ => 0.6f + 0.2f * c,
        };
    }
    float _chargeMax;

    /// <summary>The blended clip pose at timeline point <paramref name="u"/>, scaled for the shot and placed on the simulation (see the class summary).</summary>
    void ClipPose(in FigureFrame f, float sc, float u, NQuat[] outRot, out NVec3 hips, float dt)
    {
        var h = SampleBlend(u, outRot, out var root);
        // the striking leg's swing about its contact pose, eased in after the takeoff and out after the follow-through
        if (dt > 0f)
        {
            float ampT = Amplitude(f);
            _amp = _amp > 0f ? Mathf.Lerp(_amp, ampT, 1f - Mathf.Exp(-dt / 0.05f)) : ampT;
        }
        float sw = 1f - (1f - _amp) * SwingScaleW(u);
        if (sw < 0.999f)
        {
            int i = _foot;
            foreach (int k in new[] { _sk.UpLeg[i], _sk.Leg[i], _sk.Foot[i], _sk.Toe[i] }) outRot[k] = NQuat.Normalize(NQuat.Slerp(_cR[k], outRot[k], sw));
        }
        bool inside = f.Shot.Surface == BootSurface.Inside;
        if (inside)
        {
            float wf = FinesseW(u);
            int i = _foot;
            outRot[_sk.UpLeg[i]] = NQuat.Normalize(outRot[_sk.UpLeg[i]] * NQuat.CreateFromAxisAngle(NVec3.UnitY, _twistSign[i] * 0.55f * wf));
            outRot[_sk.Leg[i]] = NQuat.Normalize(outRot[_sk.Leg[i]] * NQuat.CreateFromAxisAngle(NVec3.UnitY, _twistSign[i] * 0.35f * wf));
        }
        if (f.Shot.Kind == ShotKind.Chip)
        {
            float lw = ChipW(u);
            var back = NQuat.CreateFromAxisAngle(NVec3.UnitX, -0.12f * lw);
            outRot[_spine] = NQuat.Normalize(outRot[_spine] * back);
            outRot[_spine1] = NQuat.Normalize(outRot[_spine1] * back);
        }
        // the heading: the run's facing turned toward the aim (the clip's kick onto it), eased; fixed from the plant on
        if (dt > 0f && !_headingFrozen)
        {
            float want = AimHeading(f);
            _heading = Mathf.LerpAngle(_heading, want, 1f - Mathf.Exp(-dt / 0.06f));
            if (u >= KPlant) _headingFrozen = true;
        }
        var q = Spring.Yaw(ModelSpace.YawFromFacing(_heading));
        var rm = M(f.Position, sc); rm.Y = 0f;
        // toward the clip's place relative to the ball at the contact point, eased in over the plant step and out through the follow-through
        if (dt > 0f)
        {
            var c = M(f.Shot.ContactPoint, sc); c.Y = 0f;
            var rel = NVec3.Transform(new NVec3(_ballClip.X - root.X, 0f, _ballClip.Z - root.Y), q);
            var full = c - rm - rel;
            float most = MaxBallOffset / sc;
            var clamped = full.Length() > most ? full * (most / full.Length()) : full;
            float wb = u <= KContact ? Smooth((u - _uEntry) / Math.Max(KPlant - _uEntry, 0.05f)) : 1f - Smooth((u - KContact - 0.3f) / (KEnd - KContact - 0.3f));
            if (_uEntry >= KPlant) wb = u <= KContact ? 1f : wb;
            var want = clamped * wb;
            _off = _offOk ? NVec3.Lerp(_off, want, 1f - MathF.Exp(-dt / 0.04f)) : want;
            _offOk = true;
            // what the body does not cover the plant foot reaches (over the plant step, held once it has landed)
            _res = (full - clamped) * wb;
        }
        hips = rm + _off + NVec3.Transform(new NVec3(h.X - root.X, h.Y, h.Z - root.Y), q);
        outRot[_sk.Hips] = NQuat.Normalize(q * outRot[_sk.Hips]);
    }

    /// <summary>The world facing the clip is drawn at: the clip's kick onto the aim, the aim taken at most 0.6 rad from the facing.</summary>
    float AimHeading(in FigureFrame f)
    {
        float face = f.Facing;
        Vector3 a = new(f.Shot.Aim.X, 0, f.Shot.Aim.Z);
        float aim = face;
        if (a.LengthSquared() > 0.01f) aim = face + Math.Clamp(Mathf.Wrap(Mathf.Atan2(a.Z, a.X) - face, -Mathf.Pi, Mathf.Pi), -0.6f, 0.6f);
        var kd = NVec3.Lerp(_stand!.KickDir, _run!.KickDir, _w);
        return aim + MathF.Atan2(kd.X, kd.Z);
    }

    /// <summary>The pin's weight at timeline point <paramref name="u"/>: up over the swing's last PinIn seconds, 1 on the contact, down over PinOut.</summary>
    float PinWeight(float u) => u <= KContact ? 1f - Smooth(TimeTo(u, KContact) / PinIn) : 1f - Smooth(TimeTo(KContact, u) / PinOut);

    /// <summary>The shot's surface point on the ball, as a direction from its centre (model space): behind it along the leave direction, higher or lower per kind.</summary>
    NVec3 HitNormal(in FigureFrame f, float sc)
    {
        var sh = f.Shot;
        Vector3 d = new(sh.BallOutVelocity.X, 0, sh.BallOutVelocity.Z);
        if (!(sh.Made && d.LengthSquared() > 0.04f))
        {
            float hd = _heading - MathF.Atan2(NVec3.Lerp(_stand!.KickDir, _run!.KickDir, _w).X, NVec3.Lerp(_stand.KickDir, _run.KickDir, _w).Z);
            d = new Vector3(Mathf.Cos(hd), 0, Mathf.Sin(hd));
        }
        d = d.Normalized();
        float th = sh.Kind switch { ShotKind.Driven => -0.30f, ShotKind.Chip => 0.45f, ShotKind.Power => 0.12f, _ => 0.05f };
        Vector3 n = -d * Mathf.Cos(th) - Vector3.Up * Mathf.Sin(th);
        return NVec3.Normalize(ModelSpace.FromWorld(new NVec3(n.X, n.Y, n.Z)));
    }

    /// <summary>
    /// The striking ankle that puts its surface on the ball for the foot turned to <paramref name="gFoot"/> (model space): the boot's
    /// own surface point on the ball as near to <paramref name="nrm"/> as can be with the surface point the contact check measures
    /// (the same surface offset turned by the foot's heading only, see PlayerFigure.Off) on the ball too, so both are on it. Gives
    /// that heading (world) in <paramref name="dirWorld"/>.
    /// </summary>
    NVec3 StrikeTarget(int i, BootSurface s, NQuat gFoot, NVec3 centre, float rm, NVec3 nrm, float height, float ankleH, float sc, out Vector3 dirWorld)
    {
        var turn = NQuat.Normalize(gFoot * NQuat.Inverse(_sk.RestGlobal[_sk.Foot[i]]));
        var o = _body.SurfaceRest(i, s);
        var A = NVec3.Transform(new NVec3(o.X, o.Y, o.Z), turn);
        // the foot's heading: from its sideways axis, which stays level while the toes point down
        var side = NVec3.Transform(NVec3.UnitX, turn);
        float yaw = MathF.Atan2(-side.Z, side.X);
        var fm = new NVec3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        var fw = ModelSpace.ToWorld(fm);
        dirWorld = new Vector3(fw.X, 0, fw.Z).Normalized();
        Vector3 fr = new(-dirWorld.Z, 0, dirWorld.X);
        Vector3 off = _body.SurfaceOffset(i, s, height, ankleH);
        Vector3 bw = dirWorld * off.X + Vector3.Up * off.Y + fr * off.Z;
        var B = M(bw, sc);
        var dl = A - B;
        float dlen = dl.Length();
        NVec3 x;
        if (dlen < 1e-6f) x = nrm * rm;
        else if (dlen >= 2f * rm) x = nrm * rm;   // cannot have both on the ball: the boot's own surface
        else
        {
            var e = dl / dlen;
            float hgt = 0.5f * dlen;
            var p = nrm - e * NVec3.Dot(nrm, e);
            if (p.LengthSquared() < 1e-8f) p = NVec3.Cross(e, NVec3.UnitY);
            p = NVec3.Normalize(p);
            x = e * hgt + p * MathF.Sqrt(rm * rm - hgt * hgt);
        }
        return centre + x - A;
    }

    /// <summary>The striking foot onto the ball, the plant foot to its place, the clip's planted feet held, touches drawn by the figure taking their leg.</summary>
    void Constrain(in FigureFrame f, float sc, float ankleH, float height, ReadOnlySpan<float> tw, float dt)
    {
        _sk.Forward(_out, _outP[0], _gr, _gp, _outP);
        var c = M(f.Shot.ContactPoint, sc);
        float rm = f.BallRadius / sc;
        var nrm = HitNormal(f, sc);
        float wpin = PinWeight(_u);
        bool exact = f.Shot.Made && _contactDone && Math.Abs(_u - KContact) < 1e-5f;
        // the pin's shift from the clip's contact pose (placed as now), so the swing comes onto the ball along its own path
        NVec3 pinShift = NVec3.Zero;
        if (wpin > 0f && !exact)
        {
            var r = new NQuat[_n];
            ClipPose(f, sc, KContact, r, out var hc, 0f);
            var gr = new NQuat[_n]; var gp = new NVec3[_n];
            var pp = new NVec3[_n]; for (int k = 0; k < _n; k++) pp[k] = _rest[k]; pp[0] = hc;
            _sk.Forward(r, hc, gr, gp, pp);
            var tgt = StrikeTarget(_foot, f.Shot.Surface, gr[_sk.Foot[_foot]], c, rm, nrm, height, ankleH, sc, out _);
            pinShift = tgt - gp[_sk.Foot[_foot]];
        }
        bool anyTouch = tw[0] > 0f || tw[1] > 0f;
        var baseGp = anyTouch ? new NVec3[_n] : null;
        if (anyTouch) _sk.Forward(_baseR, _baseP[0], _baseG, baseGp!, _baseP);
        float cs = ClipTime(_stand!, _u), cr = ClipTime(_run!, _u);
        byte bits = _w >= 0.5f ? _run!.Contacts(cr) : _stand!.Contacts(cs);
        int pf = 1 - _foot;
        for (int i = 0; i < 2; i++)
        {
            int up = _sk.UpLeg[i], foot = _sk.Foot[i];
            NVec3 target = _gp[foot];
            NQuat gFoot = _gr[foot];
            bool pinned = i == _foot && wpin > 0f;
            if (i == pf && _u <= KEnd)
            {
                // the plant foot reaches what the body does not cover toward its place beside the ball, from the takeoff to its landing
                float wp = Smooth((_u - KTakeoff) / (KPlant - KTakeoff));
                target += new NVec3(_res.X, 0f, _res.Z) * wp;
                var d = target - _gp[up];
                float most = 0.97f * _legLen;
                if (d.Length() > most) target = _gp[up] + d * (most / d.Length());
            }
            if (pinned)
            {
                if (exact) target = StrikeTarget(i, f.Shot.Surface, gFoot, c, rm, nrm, height, ankleH, sc, out var sd);
                else target += pinShift * wpin;
            }
            bool contact = (bits & (i == 0 ? Clip.LeftFoot : Clip.RightFoot)) != 0;
            var lk = _lock[i];
            bool stance = contact && !pinned && (lk.Locked || target.Y < _restAnkleY + PlantAbove);
            // the plant foot is held through its heel rising as the clip strikes (it rolls onto its toes, not off the spot)
            lk.LiftOff = i == pf ? PlantLiftOff / sc : 0.025f / sc;
            var held = new NVec3(lk.Lock.X, target.Y, lk.Lock.Z);
            bool far = lk.Locked && (held - _gp[up]).Length() > 0.99f * _legLen;
            bool was = lk.Locked;
            target = lk.Apply(stance, target, far, dt);
            if (lk.Locked && !was) _lowY[i] = target.Y;
            if (lk.Locked) _lowY[i] = Math.Min(_lowY[i], target.Y);
            // planted while it stands flat (a heel up is a foot rolling on its toes)
            _planted[i] = lk.Settled && target.Y - _lowY[i] < 0.015f / sc;
            float twi = pinned ? 0f : Math.Clamp(tw[i], 0f, 1f);
            if (twi > 0f)
            {
                target = NVec3.Lerp(target, baseGp![foot], twi);
                gFoot = NQuat.Normalize(NQuat.Slerp(gFoot, _baseG[foot], twi));
                _planted[i] = false;
            }
            if (!exact || i != _foot) target = KeepAboveGround(i, target, gFoot);
            SolveLeg(i, target, gFoot, exact && i == _foot ? 1f : 0.97f);
        }
        if (exact)
        {
            // the check the figure makes with this tick's pose: the boot's own surface and the measured one against the ball
            _sk.Forward(_out, _outP[0], _gr, _gp, _outP);
            int fo = _sk.Foot[_foot];
            StrikeTarget(_foot, f.Shot.Surface, _gr[fo], c, rm, nrm, height, ankleH, sc, out var dirW);
            StrikeDrawn = true; StrikeDir = dirW;
            var sp = SurfacePoint(_foot, f.Shot.Surface, _gr[fo], _gp[fo]);
            float skin = ((sp - c).Length() - rm) * sc;
            GD.Print($"SHOT_CLIP contact id={_shotId} surface={f.Shot.Surface} kind={f.Shot.Kind} skin_err_m={skin:F4} amp={_amp:F2} w_run={_w:F2} off_m={_off.Length() * sc:F3} res_m={new NVec3(_res.X, 0, _res.Z).Length() * sc:F3}");
        }
        if (Dbg) _sk.Forward(_out, _outP[0], _gr, _gp, _outP);
        if (Dbg)
            GD.Print($"SHOT_CLIP_DBG ank=({_gp[_sk.Foot[0]].X * sc:F2},{_gp[_sk.Foot[0]].Y * sc:F2},{_gp[_sk.Foot[0]].Z * sc:F2})/({_gp[_sk.Foot[1]].X * sc:F2},{_gp[_sk.Foot[1]].Y * sc:F2},{_gp[_sk.Foot[1]].Z * sc:F2}) hips=({_outP[0].X * sc:F2},{_outP[0].Y * sc:F2},{_outP[0].Z * sc:F2}) src=({_srcP[0].X * sc:F2},{_srcP[0].Z * sc:F2}) id={_shotId} u={_u:F3} rate={_rate:F2} eta={f.Shot.ContactEta:F3} made={(f.Shot.Made ? 1 : 0)} pin={wpin:F2} off=({_off.X * sc:F3},{_off.Z * sc:F3}) res={new NVec3(_res.X, 0, _res.Z).Length() * sc:F3} lock={(_lock[0].Locked ? 1 : 0)}{(_lock[1].Locked ? 1 : 0)} amp={_amp:F2} in={(_in.Active ? 1 : 0)}");
    }

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
    void SolveLeg(int i, NVec3 target, NQuat gFoot, float soft)
    {
        int up = _sk.UpLeg[i], leg = _sk.Leg[i], foot = _sk.Foot[i];
        NVec3 a = _gp[up], b = _gp[leg], c = _gp[foot];
        if ((target - c).LengthSquared() > 1e-12f)
        {
            var thighFwd = NVec3.Transform(NVec3.UnitZ, _gr[up] * NQuat.Inverse(_sk.RestGlobal[up]));
            var line = NVec3.Normalize(c - a);
            var kneeOff = (b - a) - line * NVec3.Dot(b - a, line);
            TwoBoneIk.Apply(a, b, c, _gr[up], _gr[leg], target, kneeOff + thighFwd * 0.02f, out var gUp2, out var gLeg2, out _, soft);
            _out[up] = NQuat.Normalize(NQuat.Inverse(_gr[_sk.Parents[up]]) * gUp2);
            _out[leg] = NQuat.Normalize(NQuat.Inverse(gUp2) * gLeg2);
            _gr[up] = gUp2; _gr[leg] = gLeg2;
        }
        _out[foot] = NQuat.Normalize(NQuat.Inverse(_gr[leg]) * gFoot);
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

    readonly NVec3[] _legOff = new NVec3[2], _legOffVel = new NVec3[2];
    readonly bool[] _hbStep = new bool[2];
    /// <summary>Whether foot <paramref name="i"/> is stepping back in at the hand-back (it is not planted, whatever the figure's own pose has).</summary>
    public bool HandBackStepping(int i) => _handBack && _hbStep[i];

    /// <summary>The clip hands back to the figure's pose: the bones by inertialisation, each foot by its ankle's offset from the figure's.</summary>
    void BeginHandBack(float dt, string why)
    {
        GD.Print($"SHOT_CLIP hand-back id={_shotId} u={_u:F3} why={why}");
        _playing = false; _handBack = true;
        _hbStep[0] = _hbStep[1] = false;
        if (!_haveShownPrev || !_haveBasePrev) { _in.Reset(); _legOff[0] = _legOff[1] = NVec3.Zero; return; }
        for (int k = 0; k < _n; k++)
        {
            _dstAngV[k] = Spring.AngularVelocity(_baseRPrev[k], _baseR[k], dt);
            _dstLinV[k] = (_baseP[k] - _basePPrev[k]) / dt;
        }
        ShownVelocities(dt);
        _in.Transition(_shR, _angV, _shP, _linV, _baseRPrev, _dstAngV, _basePPrev, _dstLinV);
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
            _legOff[i] = shownNow[i] - basePrev[i];
            _legOffVel[i] = (shownNow[i] - shownPrev[i]) / dt - (bn - basePrev[i]) / dt;
        }
    }

    /// <summary>The hand-back's legs: each ankle at the figure's plus its offset (dying away while the foot swings, held while it is planted; a touch takes the leg by its weight).</summary>
    void HandBackLegs(ReadOnlySpan<float> tw, ReadOnlySpan<bool> basePlanted, float dt)
    {
        _sk.Forward(_baseR, _baseP[0], _baseG, _gp, _baseP);
        var baseAnk = new NVec3[2]; for (int i = 0; i < 2; i++) baseAnk[i] = _gp[_sk.Foot[i]];
        _sk.Forward(_out, _outP[0], _gr, _gp, _outP);
        for (int i = 0; i < 2; i++)
        {
            var x = _legOff[i]; var v = _legOffVel[i];
            float tk = 1f - Math.Clamp(tw[i], 0f, 1f);
            if (basePlanted[i] && tw[i] <= 0f && !_hbStep[i] && (baseAnk[i] + x * tk - _gp[_sk.UpLeg[i]]).Length() > 0.99f * _legLen) _hbStep[i] = true;
            if (basePlanted[i] && tw[i] <= 0f && !_hbStep[i]) v = NVec3.Zero;
            else Spring.Decay(ref x, ref v, FootHandBackHalfLife, dt);
            if (_hbStep[i] && x.Length() < 0.01f * _legLen) _hbStep[i] = false;
            _legOff[i] = x; _legOffVel[i] = v;
            var target = baseAnk[i] + x * tk;
            if (_hbStep[i]) target.Y += Math.Min(0.05f * _legLen, 0.3f * new NVec2(x.X, x.Z).Length());
            float twc = Math.Clamp(tw[i], 0f, 1f);
            var gFoot = twc > 0f ? NQuat.Normalize(NQuat.Slerp(_gr[_sk.Foot[i]], _baseG[_sk.Foot[i]], twc)) : _gr[_sk.Foot[i]];
            SolveLeg(i, target, gFoot, 0.97f);
            _sk.Forward(_out, _outP[0], _gr, _gp, _outP);
        }
    }
}
