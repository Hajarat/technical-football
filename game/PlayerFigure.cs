using System;
using System.Collections.Generic;
using Godot;

namespace FootballSim.Game;

/// <summary>Plain-value input for one figure for one physics tick. The figure never sees the simulation types.</summary>
public struct FigureFrame
{
    public Vector3 Position;          // feet centre on the ground
    public float Facing;              // radians, 0 = +X, positive toward +Z
    public Vector3 Velocity;
    public float HeightM;
    public bool InPossession, CloseControl;
    public Vector3 BallPos, BallVel;
    public float BallRadius;
    /// <summary>A touch / strike happened this tick; BallPos is the ball at contact and BallVel its velocity just after.</summary>
    public bool TouchEvent, StrikeEvent, EventRight;
    /// <summary>Look-ahead (NaN when none): seconds to the next touch, which foot, where the ball will be.</summary>
    public float NextTouchEta; public bool NextTouchRight; public Vector3 NextTouchPoint;
    /// <summary>Seconds until a pending shot makes contact (NaN when none) and the foot that will strike (also used while charging).</summary>
    public float StrikeEta; public bool StrikeRight;
    public float ShotCharge;
    public bool Diving, Holding, Reacting;
    public Vector3 HandPosition;
    public float Dt;

    public static FigureFrame At(Vector3 pos, float facing) => new()
    {
        Position = pos, Facing = facing, HeightM = 1.8f, BallRadius = 0.11f, BallPos = pos + new Vector3(2, 0.11f, 0),
        NextTouchEta = float.NaN, StrikeEta = float.NaN, Dt = 1f / 120f,
    };
}

/// <summary>Foot-contact, foot-slide and pose-limit measurements collected by figures (replay mode prints them on quit).</summary>
public sealed class FigureStats
{
    public readonly List<float> Contact = new(), ContactTouch = new(), ContactStrike = new(), Slide = new();
    public float MinPelvisRatio = float.MaxValue, MinKneeM = float.MaxValue, MinLimbYM = float.MaxValue, LeanMinDeg = float.MaxValue, LeanMaxDeg = float.MinValue;

    public string ContactLine() => Line("FOOT_CONTACT", Contact);
    public string ContactTouchLine() => Line("FOOT_CONTACT_TOUCH", ContactTouch);
    public string ContactStrikeLine() => Line("FOOT_CONTACT_STRIKE", ContactStrike);
    public string SlideLine() => Line("FOOT_SLIDE", Slide);
    public string PoseLine() =>
        $"POSE_LIMITS min_pelvis_ratio={MinPelvisRatio:F3} min_knee_m={MinKneeM:F3} min_limb_y_m={MinLimbYM:F3} lean_deg_min={LeanMinDeg:F1} lean_deg_max={LeanMaxDeg:F1}";

    static string Line(string name, List<float> l) => $"{name} n={l.Count} mean_m={Mean(l):F3} max_m={Max(l):F3}";
    static float Mean(List<float> l) { if (l.Count == 0) return 0; float s = 0; foreach (var v in l) s += v; return s / l.Count; }
    static float Max(List<float> l) { float m = 0; foreach (var v in l) m = Math.Max(m, v); return m; }
}

/// <summary>
/// A jointed, low-poly figure built from primitives and animated procedurally from plain per-tick values:
/// a distance-driven run cycle with planted stance feet, two-bone leg and arm IK, touches, shots and the keeper's set stance,
/// dive and hold. All transforms are written in Update(), which must be called from _PhysicsProcess. The figure root stays at
/// the origin; joints are placed in world coordinates.
/// </summary>
public partial class PlayerFigure : Node3D
{
    public bool IsKeeper;
    public FigureStats? Stats;
    public string StatsName = "";

    // ---- colours ----
    readonly StandardMaterial3D _shirt = MeshKit.Lit(Colors.Red), _shorts = MeshKit.Lit(Colors.White), _socks = MeshKit.Lit(Colors.Red),
        _skin = MeshKit.Lit(new Color(0.85f, 0.65f, 0.5f)), _boots = MeshKit.Lit(new Color(0.08f, 0.08f, 0.08f)), _gloves = MeshKit.Lit(new Color(0.1f, 0.9f, 0.9f));

    public void SetColors(Color shirt, Color shorts, Color socks)
    {
        _shirt.AlbedoColor = shirt; _shorts.AlbedoColor = shorts; _socks.AlbedoColor = socks;
    }

    // ---- parts ----
    MeshInstance3D _pelvis = null!, _torso = null!, _head = null!;
    readonly MeshInstance3D[] _thigh = new MeshInstance3D[2], _shin = new MeshInstance3D[2], _foot = new MeshInstance3D[2], _knee = new MeshInstance3D[2],
        _upper = new MeshInstance3D[2], _fore = new MeshInstance3D[2], _hand = new MeshInstance3D[2], _elbow = new MeshInstance3D[2];

    // ---- dimensions (functions of height) ----
    float H = 1.8f, Leg, AnkleH, FootLen, FootW, FootH, HipW, TorsoLen, ShoulderW, ArmUp, ArmLow, Instep;

    sealed class FootState
    {
        public bool Planted; public Vector3 Plant;
        public bool Timed, TFixed; public Vector3 TFrom, TTarget; public float TT, TDur;
        public Vector3 FromRel;
        public bool ForceSwing; public float SwingS;
        public Vector3 Ankle; public Vector3 Dir = new(1, 0, 0);
        public bool Tracking; public Vector3 TrackStart; public float TrackMax;
    }
    readonly FootState[] _feet = { new(), new() };   // 0 = left, 1 = right

    // ---- state ----
    bool _init, _moving;
    Vector3 _lastPos, _prevHv, _accS;
    float _speedS, _phase, _lean, _stepLen = 1f, _fst = 0.5f;
    // kick sequences
    float _strikeT = -1f, _touchT = -1f, _windT, _lastCharge;
    bool _strikeRight, _touchRight, _backswingRight;
    float _backswing, _phaseMul = 1f;
    bool _stActive; Vector3 _stRel0; int _stFoot;
    Vector3 _contactAnkle, _contactDir, _followAnkle;
    // per-tick kick plan
    float _kW; int _kFoot; Vector3 _kAnkle, _kDir; bool _pinOn; int _pinFoot; Vector3 _pinTarget; float _leanExtra; bool _seq;
    float _leanExtraS; bool _pinWas; Vector3 _pinLock;

    static float Smooth(float x) => Mathf.SmoothStep(0f, 1f, Mathf.Clamp(x, 0f, 1f));
    static float Frac(float x) => x - Mathf.Floor(x);
    static float Dist2(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    public override void _Ready()
    {
        _pelvis = Box("Pelvis", _shorts);
        _torso = Box("Torso", _shirt);
        _head = Sph("Head", _skin);
        for (int i = 0; i < 2; i++)
        {
            _thigh[i] = Cyl($"Thigh{i}", _shorts); _shin[i] = Cyl($"Shin{i}", _socks); _knee[i] = Sph($"Knee{i}", _socks);
            _foot[i] = Box($"Foot{i}", _boots);
            _upper[i] = Cyl($"Upper{i}", _shirt); _fore[i] = Cyl($"Fore{i}", IsKeeper ? _shirt : _skin);
            _elbow[i] = Sph($"Elbow{i}", IsKeeper ? _shirt : _skin); _hand[i] = Sph($"Hand{i}", IsKeeper ? _gloves : _skin);
        }
        SetDims(1.8f);
    }

    MeshInstance3D Box(string n, Material m) { var mi = new MeshInstance3D { Name = n, Mesh = new BoxMesh { Size = Vector3.One }, MaterialOverride = m }; AddChild(mi); return mi; }
    MeshInstance3D Sph(string n, Material m) { var mi = new MeshInstance3D { Name = n, Mesh = new SphereMesh { Radius = 0.5f, Height = 1f, RadialSegments = 8, Rings = 4 }, MaterialOverride = m }; AddChild(mi); return mi; }
    MeshInstance3D Cyl(string n, Material m) { var mi = new MeshInstance3D { Name = n, Mesh = new CylinderMesh { TopRadius = 0.5f, BottomRadius = 0.5f, Height = 1f, RadialSegments = 8, Rings = 1 }, MaterialOverride = m }; AddChild(mi); return mi; }

    void SetDims(float h)
    {
        H = h;
        Leg = 0.48f * h; AnkleH = 0.035f * h; FootLen = 0.17f * h; FootW = 0.075f * h; FootH = 0.05f * h;
        HipW = 0.095f * h; TorsoLen = 0.30f * h; ShoulderW = 0.15f * h; ArmUp = 0.19f * h; ArmLow = 0.17f * h;
        Instep = 0.45f * FootLen;
    }

    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Snap to a frame with no history (round reset): feet planted under the hips, no interpolation across the jump.</summary>
    public void Teleport(in FigureFrame f)
    {
        _init = false;
        _strikeT = _touchT = -1f; _windT = 0; _lastCharge = 0; _lean = 0; _leanExtraS = 0; _stActive = false; _phaseMul = 1f; _pinWas = false;
        Update(f);
        ResetPhysicsInterpolation();
    }

    void InitState(Vector3 pos, Vector3 fwd, Vector3 right)
    {
        _init = true; _lastPos = pos; _prevHv = Vector3.Zero; _accS = Vector3.Zero; _speedS = 0; _phase = 0; _moving = false;
        for (int i = 0; i < 2; i++)
        {
            var ft = _feet[i];
            ft.Planted = true; ft.Timed = false; ft.Tracking = false; ft.ForceSwing = false;
            ft.Plant = pos + right * (i == 0 ? -HipW * 0.9f : HipW * 0.9f); ft.Plant.Y = AnkleH;
            ft.Ankle = ft.Plant; ft.Dir = fwd;
        }
    }

    float StandY => AnkleH + Leg * 0.97f;

    public void Update(in FigureFrame f)
    {
        float dt = f.Dt > 0 ? f.Dt : 1f / 120f;
        SetDims(f.HeightM > 0.5f ? f.HeightM : 1.8f);
        float k = H / 1.8f;
        Vector3 pos = f.Position;
        Vector3 fwd = new(Mathf.Cos(f.Facing), 0, Mathf.Sin(f.Facing));
        Vector3 right = new(-fwd.Z, 0, fwd.X);
        Vector3 hv = new(f.Velocity.X, 0, f.Velocity.Z);
        float speed = hv.Length();
        if (!_init) InitState(pos, fwd, right);

        float dist = Dist2(pos, _lastPos); _lastPos = pos;
        _speedS = Mathf.Lerp(_speedS, speed, 1f - Mathf.Exp(-dt / 0.10f));
        _accS = _accS.Lerp((hv - _prevHv) / dt, 1f - Mathf.Exp(-dt / 0.10f)); _prevHv = hv;
        float accF = Mathf.Clamp(_accS.Dot(fwd), -9f, 9f);
        float sfs = Mathf.Clamp((_speedS - 0.5f) / 8f, 0f, 1f);

        bool nowMoving = speed > (_moving ? 0.3f : 0.5f);
        Vector3 fm = nowMoving ? hv / speed : fwd;

        if (IsKeeper && f.Diving) { UpdateDive(f, fwd, right, dt); return; }

        // ---- posture: pelvis height as a fraction of the standing height, gait parameters ----
        float legFrac = Mathf.Lerp(0.965f, 0.90f, sfs);
        if (f.CloseControl && !IsKeeper) legFrac = Math.Min(legFrac, 0.865f);
        if (IsKeeper) legFrac = nowMoving ? legFrac : (f.Holding ? 0.94f : 0.84f);
        float desiredY = AnkleH + Leg * legFrac;
        float reach = 0.985f * Leg;
        float hgt = desiredY - AnkleH;
        float hmax = Mathf.Sqrt(Math.Max(0.01f, reach * reach - hgt * hgt));   // how far from the hip a straight-ish leg can plant
        _stepLen = Mathf.Lerp(0.7f, 2.2f, sfs) * k * (f.CloseControl ? 0.65f : 1f);
        float C = 2f * _stepLen;
        _fst = Mathf.Clamp(Mathf.Lerp(0.62f, 0.28f, sfs), 0.10f, 0.9f);
        _fst = Math.Min(_fst, 1.8f * hmax / C);
        float span = _fst * C;
        float hwf = HipW * (IsKeeper ? 2.4f : 0.9f);

        // ---- kick plan: shot wind-up / strike / follow-through, touches ----
        PlanKick(f, pos, fwd, right, fm, dt, desiredY, hmax);
        if (_pinOn) { if (!_pinWas) _pinLock = _pinTarget; _pinTarget = _pinLock; }   // the support foot goes where the ball will be
        _pinWas = _pinOn;

        // ---- pelvis: sim position; reach assist toward a kick target (capped: at most 0.25 m of translation, never below 0.85 of standing height) ----
        Vector3 pel = new(pos.X, desiredY, pos.Z);
        float floorY = IsKeeper ? 0.5f * StandY : 0.85f * StandY;
        if (_kW > 0f)
        {
            Vector3 hip = pel + right * (_kFoot == 0 ? -HipW : HipW);
            Vector3 d = _kAnkle - hip;
            float lim = 0.995f * Leg;
            float vert = pel.Y - _kAnkle.Y;
            Vector3 dh = new(d.X, 0, d.Z);
            float dhLen = dh.Length();
            float dhMax = Mathf.Sqrt(Math.Max(0.01f, lim * lim - vert * vert));
            float over = dhLen - dhMax;
            if (over > 0f)
            {
                float sh = Math.Min(over, 0.25f) * _kW;
                pel += dh / dhLen * sh;
                dhLen -= sh;
                _leanExtra += 0.22f * Math.Min(1f, sh / 0.25f);
                float needV = Mathf.Sqrt(Math.Max(0f, lim * lim - dhLen * dhLen));
                float drop = Math.Max(0f, vert - needV) * _kW;
                pel.Y = Math.Max(floorY, pel.Y - drop);
            }
        }
        Vector3 pelXZ = new(pel.X, 0, pel.Z);
        float reachNow = Mathf.Sqrt(Math.Max(0.01f, reach * reach - (pel.Y - AnkleH) * (pel.Y - AnkleH)));

        // ---- gait ----
        if (nowMoving && !_moving)
        {
            _phase = _fst * 0.5f;
            foreach (var ft in _feet)
                if (ft.Timed) { ft.Timed = false; ft.Planted = false; ft.FromRel = ft.Ankle - pelXZ; }
        }
        _moving = nowMoving;
        if (_moving && !_seq)
        {
            // Dribble: re-phase smoothly so the named foot is nearly through its forward swing when the next touch arrives.
            float mulTarget = 1f;
            if (!float.IsNaN(f.NextTouchEta) && f.NextTouchEta > 0.03f && f.NextTouchEta < 0.6f && _speedS > 1f)
            {
                int ti = f.NextTouchRight ? 1 : 0;
                float phiNow = Frac(_phase + (ti == 0 ? 0f : 0.5f));
                float phiT = Frac(_fst + 0.8f * (1f - _fst));
                float natural = _speedS * f.NextTouchEta / C;
                float need = Frac(phiT - phiNow);
                need += Mathf.Round(natural - need);
                mulTarget = Mathf.Clamp(need / Math.Max(0.01f, natural), 0.6f, 1.7f);
            }
            _phaseMul = Mathf.Lerp(_phaseMul, mulTarget, 1f - Mathf.Exp(-dt / 0.10f));
            _phase = Frac(_phase + dist / C * _phaseMul);
        }

        for (int i = 0; i < 2; i++)
        {
            var ft = _feet[i];
            float side = i == 0 ? -1f : 1f;
            if (_pinOn && i == _pinFoot) { StepPin(ft, dt); continue; }
            if (_moving)
            {
                if (ft.Timed) { ft.Timed = false; ft.Planted = false; ft.FromRel = ft.Ankle - pelXZ; }
                float phi = Frac(_phase + (i == 0 ? 0f : 0.5f));
                Vector3 target = pelXZ + fm * (span * 0.5f) + right * side * hwf; target.Y = AnkleH;
                Vector3 hipXZ = pelXZ + right * side * HipW;
                if (ft.Planted && !ft.ForceSwing && Dist2(hipXZ, ft.Plant) > reachNow)
                { ft.ForceSwing = true; ft.SwingS = 0f; ft.Planted = false; ft.FromRel = ft.Plant - pelXZ; }   // stance foot left behind: catch up
                if (ft.ForceSwing)
                {
                    ft.SwingS += 2.5f * Math.Max(dist, 0.004f) / Math.Max(0.3f, C * (1f - _fst));
                    float s = Mathf.Clamp(ft.SwingS, 0f, 1f);
                    ft.Ankle = pelXZ + ft.FromRel.Lerp(target - pelXZ, Smooth(s));
                    ft.Ankle.Y = AnkleH + (0.05f + 0.20f * sfs) * k * Mathf.Sin(Mathf.Pi * s);
                    if (s >= 1f) { ft.ForceSwing = false; ft.Planted = true; ft.Plant = target; ft.Ankle = ft.Plant; }
                }
                else if (phi < _fst)
                {
                    if (!ft.Planted) { ft.Planted = true; ft.Plant = target; }
                    ft.Ankle = ft.Plant;
                }
                else
                {
                    if (ft.Planted) { ft.Planted = false; ft.FromRel = ft.Plant - pelXZ; }
                    float s = (phi - _fst) / Math.Max(0.01f, 1f - _fst);
                    Vector3 rel = ft.FromRel.Lerp(target - pelXZ, Smooth(s));
                    float arc = (0.05f + 0.20f * sfs) * k * Mathf.Sin(Mathf.Pi * s);
                    ft.Ankle = pelXZ + rel; ft.Ankle.Y = AnkleH + arc;
                    // wind-up: while the shot is charging, the striking foot trails further back on its natural back-swing
                    if (_backswing > 0f && (i == 1) == _backswingRight)
                    {
                        float bs = Mathf.Sin(Mathf.Pi * s);
                        ft.Ankle -= fwd * (0.22f * _backswing * k * bs);
                        ft.Ankle.Y += 0.42f * _backswing * k * bs;
                    }
                }
            }
            else IdleFoot(i, ft, pelXZ, right, side, hwf, dt);
            ft.Dir = fwd;
        }

        // ---- pelvis bob, assembling the final ankles ----
        float bob = _moving ? 0.010f * H * sfs * Mathf.Sin(4f * Mathf.Pi * _phase) : 0f;
        pel.Y += bob;

        var ank = new Vector3[2]; var dir = new Vector3[2]; var planted = new bool[2];
        for (int i = 0; i < 2; i++)
        {
            ank[i] = _feet[i].Ankle; dir[i] = _feet[i].Dir; planted[i] = _feet[i].Planted && !_feet[i].Timed;
            if (_kW > 0f && i == _kFoot)
            {
                ank[i] = ank[i].Lerp(_kAnkle, _kW);
                dir[i] = dir[i].Lerp(_kDir, _kW).Normalized();
                planted[i] = false;
            }
        }
        // a planted foot that would be over-extended lowers the pelvis a little (never below the floor); the re-step check above prevents most of this
        float maxH = 0f;
        for (int i = 0; i < 2; i++)
            if (planted[i])
            {
                Vector3 hip = pel + right * (i == 0 ? -HipW : HipW);
                maxH = Math.Max(maxH, Dist2(hip, ank[i]));
            }
        if (maxH > 0f)
        {
            float cap = AnkleH + Mathf.Sqrt(Math.Max(0.01f, 0.985f * 0.985f * Leg * Leg - maxH * maxH));
            if (pel.Y > cap) pel.Y = Math.Max(cap, floorY);
        }
        if (pel.Y < floorY) pel.Y = floorY;

        // ---- lean: forward only while moving; extra lean in the follow-through and when reaching ----
        float leanT = _moving ? 0.07f + 0.19f * sfs + Mathf.Clamp(0.012f * accF, 0f, 0.08f) : 0f;
        _lean = Mathf.Lerp(_lean, leanT, 1f - Mathf.Exp(-dt / 0.12f));
        _leanExtraS = Mathf.Lerp(_leanExtraS, _leanExtra, 1f - Mathf.Exp(-dt / 0.05f));
        float lean = _lean + _leanExtraS + (IsKeeper && f.Reacting ? 0.12f : 0f);
        lean = Mathf.Clamp(lean, 0f, 0.42f);
        Basis B = new Basis(right, -lean) * new Basis(fwd, Vector3.Up, right);

        // ---- legs ----
        var knee = new Vector3[2]; var ankEff = new Vector3[2];
        for (int i = 0; i < 2; i++)
        {
            Vector3 hip = pel + right * (i == 0 ? -HipW : HipW);
            SolveLeg(hip, ank[i], fwd, out knee[i], out ankEff[i]);
        }

        // ---- arms ----
        Vector3 shoulderC = pel + B * new Vector3(0, 0.035f * H + TorsoLen - 0.03f * H, 0);
        var handT = new Vector3[2]; var elbowPole = new Vector3[2];
        float amp = 0.15f + 0.45f * sfs;
        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? -1f : 1f;
            Vector3 sh = shoulderC + B * new Vector3(0, 0, side * ShoulderW);
            float oppFwd = (ankEff[1 - i] - pel).Dot(fwd);
            float alpha = Mathf.Clamp(oppFwd / 0.55f, -1f, 1f) * amp;
            float bend = 0.10f + 0.30f * sfs;          // about 90 degrees of elbow at a sprint
            Vector3 dirLocal = new Vector3(Mathf.Sin(alpha), -Mathf.Cos(alpha), side * (0.12f + 0.35f * _lastCharge)).Normalized();
            handT[i] = sh + B * dirLocal * ((ArmUp + ArmLow) * (1f - bend));
            if (IsKeeper && !f.Holding) handT[i] = sh + B * new Vector3(0.30f * H, -0.12f * H, side * 0.05f * H);
            if (f.Holding && IsKeeper) handT[i] = f.HandPosition + right * side * 0.07f;
            elbowPole[i] = (B * new Vector3(-1f, -0.2f, side * 0.5f)).Normalized();
        }
        RenderBody(pel, B, shoulderC, knee, ankEff, dir, handT, elbowPole);

        // ---- measurements (on the pose just produced; nothing is corrected afterwards) ----
        TrackSlide(planted, ankEff);
        if (!IsKeeper && Stats != null)
        {
            Stats.MinPelvisRatio = Math.Min(Stats.MinPelvisRatio, pel.Y / StandY);
            for (int i = 0; i < 2; i++)
            {
                Stats.MinKneeM = Math.Min(Stats.MinKneeM, knee[i].Y);
                Stats.MinLimbYM = Math.Min(Stats.MinLimbYM, Math.Min(knee[i].Y, Math.Min(ankEff[i].Y - AnkleH, pel.Y)));
            }
            float deg = lean * 180f / Mathf.Pi;
            Stats.LeanMinDeg = Math.Min(Stats.LeanMinDeg, deg); Stats.LeanMaxDeg = Math.Max(Stats.LeanMaxDeg, deg);
        }
        if (!IsKeeper && (f.TouchEvent || f.StrikeEvent))
        {
            // the instep point of the touching foot (or, if no kick was planned, the nearer foot) to the ball surface
            float err = float.MaxValue;
            for (int i = 0; i < 2; i++)
            {
                if (_kW > 0f && i != _kFoot) continue;
                Vector3 instep = ankEff[i] + dir[i] * Instep;
                err = Math.Min(err, Math.Abs((instep - f.BallPos).Length() - f.BallRadius));
            }
            Stats?.Contact.Add(err);
            (f.StrikeEvent ? Stats?.ContactStrike : Stats?.ContactTouch)?.Add(err);
            LastContactError = err;
            if (_kW <= 0f) GD.Print($"FOOT_NOKICK {StatsName} err={err:F3}");
        }
    }

    /// <summary>Distance from the touching foot's instep point to the ball surface at the latest contact event (for logging).</summary>
    public float LastContactError { get; private set; } = float.NaN;

    // ---------------------------------------------------------------------------------------------------------------
    // feet

    void IdleFoot(int i, FootState ft, Vector3 pelXZ, Vector3 right, float side, float hwf, float dt)
    {
        Vector3 neutral = pelXZ + right * side * hwf; neutral.Y = AnkleH;
        if (!ft.Timed && !ft.Planted)
        {
            ft.Timed = true; ft.TFixed = false; ft.TFrom = ft.Ankle; ft.TT = 0; ft.TDur = 0.2f;
        }
        else if (!ft.Timed && ft.Planted && Dist2(ft.Plant, neutral) > 0.32f && _feet[1 - i].Planted && !_feet[1 - i].Timed)
        {
            ft.Timed = true; ft.TFixed = false; ft.TFrom = ft.Plant; ft.TT = 0; ft.TDur = 0.2f; ft.Planted = false;
        }
        if (ft.Timed)
        {
            ft.TT += dt;
            Vector3 target = ft.TFixed ? ft.TTarget : neutral;
            float s = Mathf.Clamp(ft.TT / ft.TDur, 0f, 1f);
            ft.Ankle = ft.TFrom.Lerp(target, Smooth(s));
            ft.Ankle.Y = AnkleH + 0.07f * H / 1.8f * Mathf.Sin(Mathf.Pi * s);
            if (s >= 1f) { ft.Timed = false; ft.Planted = true; ft.Plant = new Vector3(target.X, AnkleH, target.Z); ft.Ankle = ft.Plant; }
        }
        else ft.Ankle = ft.Plant;
    }

    void StepPin(FootState ft, float dt)
    {
        if (ft.Planted && !ft.Timed && Dist2(ft.Plant, _pinTarget) < 0.08f) { ft.Ankle = ft.Plant; return; }
        if (!ft.Timed || !ft.TFixed || Dist2(ft.TTarget, _pinTarget) > 0.05f)
        {
            ft.TFrom = ft.Planted ? ft.Plant : ft.Ankle; ft.Planted = false; ft.ForceSwing = false;
            ft.Timed = true; ft.TFixed = true; ft.TTarget = new Vector3(_pinTarget.X, AnkleH, _pinTarget.Z); ft.TT = 0; ft.TDur = 0.07f;
        }
        ft.TT += dt;
        float s = Mathf.Clamp(ft.TT / ft.TDur, 0f, 1f);
        ft.Ankle = ft.TFrom.Lerp(ft.TTarget, Smooth(s));
        ft.Ankle.Y = AnkleH + 0.05f * Mathf.Sin(Mathf.Pi * s);
        if (s >= 1f) { ft.Timed = false; ft.Planted = true; ft.Plant = ft.TTarget; ft.Ankle = ft.Plant; }
    }

    void TrackSlide(bool[] planted, Vector3[] ankle)
    {
        for (int i = 0; i < 2; i++)
        {
            var ft = _feet[i];
            if (planted[i])
            {
                if (!ft.Tracking) { ft.Tracking = true; ft.TrackStart = ankle[i]; ft.TrackMax = 0; }
                else ft.TrackMax = Math.Max(ft.TrackMax, (ankle[i] - ft.TrackStart).Length());
            }
            else if (ft.Tracking) { Stats?.Slide.Add(ft.TrackMax); ft.Tracking = false; }
        }
    }

    /// <summary>Closes any stance still being tracked (call before reading the stats).</summary>
    public void FlushStats()
    {
        foreach (var ft in _feet) if (ft.Tracking) { Stats?.Slide.Add(ft.TrackMax); ft.Tracking = false; }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // kicks

    Vector3 ContactAnkle(Vector3 ballC, Vector3 kd, float r)
    {
        Vector3 pc = ballC - kd * r;
        Vector3 a = pc - kd * Instep;
        a.Y = Math.Max(AnkleH, pc.Y);
        return a;
    }

    /// <summary>The kicking foot drawn back (standing wind-up): behind the hip and lifted in proportion to the charge.</summary>
    Vector3 WindPose(int foot, float c, Vector3 pos, Vector3 fwd, Vector3 right)
    {
        float k = H / 1.8f;
        Vector3 a = new Vector3(pos.X, 0, pos.Z) + right * (foot == 1 ? HipW : -HipW) - fwd * ((0.14f + 0.18f * c) * k);
        a.Y = AnkleH + (0.20f + 0.45f * c) * k;
        return a;
    }

    void PlanKick(in FigureFrame f, Vector3 pos, Vector3 fwd, Vector3 right, Vector3 fm, float dt, float desiredY, float hmax)
    {
        _kW = 0f; _pinOn = false; _leanExtra = 0f; _seq = false; _backswing = 0f;
        float k = H / 1.8f;
        float r = f.BallRadius;
        Vector3 posXZ = new(pos.X, 0, pos.Z);
        Vector3 ballXZ = new(f.BallPos.X, 0, f.BallPos.Z);
        bool strikePending = !float.IsNaN(f.StrikeEta);
        float c = f.ShotCharge;

        if (c > 0.001f) { _lastCharge = c; _windT += dt; }
        else if (!strikePending && _strikeT < 0f) { _windT = 0f; _lastCharge = 0f; }

        // ---- events: the ball has just been struck / touched ----
        if (f.StrikeEvent)
        {
            _strikeT = 0f; _strikeRight = f.EventRight;   // the core guarantees this is the foot fixed at the button press
            Vector3 kd = new(f.BallVel.X, 0, f.BallVel.Z);
            kd = kd.LengthSquared() > 0.04f ? kd.Normalized() : fwd;
            _contactDir = kd; _contactAnkle = ContactAnkle(f.BallPos, kd, r);
            float cc = Math.Max(_lastCharge, 0.2f);
            _followAnkle = _contactAnkle + kd * ((0.30f + 0.25f * cc) * k); _followAnkle.Y += (0.12f + 0.25f * cc) * k;
            _touchT = -1f; _stActive = false;
        }
        else if (f.TouchEvent && _strikeT < 0f)
        {
            _touchT = 0f; _touchRight = f.EventRight;
            Vector3 kd = new(f.BallVel.X, 0, f.BallVel.Z);
            kd = kd.LengthSquared() > 0.04f ? kd.Normalized() : fm;
            _contactDir = kd; _contactAnkle = ContactAnkle(f.BallPos, kd, r);
        }

        // ---- follow-through after a strike (about 0.25 s, forward torso lean) ----
        if (_strikeT >= 0f)
        {
            const float T = 0.30f;
            float t = _strikeT;
            _kFoot = _strikeRight ? 1 : 0; _kDir = _contactDir; _seq = true;
            _kAnkle = _contactAnkle.Lerp(_followAnkle, Smooth(t / 0.14f));
            _kW = t < 0.001f ? 1f : 1f - Smooth((t - 0.14f) / (T - 0.14f));
            _leanExtra = 0.18f * (1f - Smooth((t - 0.05f) / (T - 0.05f)));
            _strikeT += dt;
            if (_strikeT >= T) { _strikeT = -1f; Rephase(_kFoot); _seq = false; }
            return;
        }

        // ---- the strike itself: StrikeEta counts down the 0.10 s strike delay ----
        if (strikePending)
        {
            int foot = f.StrikeRight ? 1 : 0;
            float eta = Math.Max(0f, f.StrikeEta);
            if (!_stActive) { _stActive = true; _stRel0 = _feet[foot].Ankle - posXZ; _stFoot = foot; }
            float u = Mathf.Clamp(1f - eta / 0.10f, 0f, 1f);
            Vector3 predicted = new(f.BallPos.X + f.BallVel.X * eta, f.BallPos.Y, f.BallPos.Z + f.BallVel.Z * eta);
            Vector3 cur = posXZ + _stRel0;
            Vector3 wind = WindPose(foot, Math.Max(_lastCharge, 0.3f), pos, fwd, right);
            Vector3 contact = ContactAnkle(predicted, fwd, r);
            _kFoot = foot; _kDir = fwd; _seq = true; _kW = 1f;
            _kAnkle = u < 0.4f ? cur.Lerp(wind, Smooth(u / 0.4f)) : wind.Lerp(contact, Mathf.Pow((u - 0.4f) / 0.6f, 2f));
            _leanExtra = 0.04f * u;
            // support foot: beside where the ball will be, as far forward as the leg can reach
            float sideSign = (1 - foot) == 1 ? 1f : -1f;
            Vector3 pt = new Vector3(predicted.X, 0, predicted.Z) + right * sideSign * 0.22f * k - fwd * 0.05f;
            Vector3 dv = pt - posXZ;
            float maxD = hmax + 0.25f;
            if (dv.Length() > maxD) pt = posXZ + dv.Normalized() * maxD;
            _pinOn = true; _pinFoot = 1 - foot; _pinTarget = pt;
            return;
        }
        _stActive = false;

        // ---- wind-up while the shot charges: the upper body shows it; a moving player's striking leg trails on its back-swing ----
        if (c > 0.001f)
        {
            _backswing = c; _backswingRight = f.StrikeRight;
            if (!_moving && Dist2(ballXZ, pos) < 1.2f * k)
            {
                int foot = f.StrikeRight ? 1 : 0;
                _kFoot = foot; _kDir = fwd;
                _kAnkle = WindPose(foot, c, pos, fwd, right);
                _kW = Smooth(_windT / 0.15f);
            }
        }

        // ---- dribble touch follow-through, then the approach of the next touch ----
        if (_touchT >= 0f)
        {
            const float T = 0.18f;
            float t = _touchT;
            _kFoot = _touchRight ? 1 : 0; _kDir = _contactDir;
            _kAnkle = _contactAnkle + _contactDir * (0.12f * k * Smooth(t / 0.10f));
            _kW = t < 0.001f ? 1f : 1f - Smooth((t - 0.04f) / (T - 0.04f));
            _touchT += dt;
            if (_touchT >= T) _touchT = -1f;
            return;
        }
        if (!float.IsNaN(f.NextTouchEta) && f.NextTouchEta <= 0.15f && c <= 0.001f)
        {
            _kFoot = f.NextTouchRight ? 1 : 0; _kDir = fm;
            _kAnkle = ContactAnkle(f.NextTouchPoint, fm, r);
            _kW = Smooth(1f - f.NextTouchEta / 0.15f);
        }
    }

    /// <summary>After a kick the run cycle resumes with the kicking foot just lifting off.</summary>
    void Rephase(int kickFoot)
    {
        _phase = Frac(_fst - (kickFoot == 0 ? 0f : 0.5f));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // keeper dive

    void UpdateDive(in FigureFrame f, Vector3 fwd, Vector3 right, float dt)
    {
        _kW = 0; _moving = false; _strikeT = _touchT = -1f;
        Vector3 pos = f.Position;
        Vector3 chest = pos + new Vector3(0, 0.5f * H, 0);
        Vector3 d = f.HandPosition - chest;
        if (d.LengthSquared() < 1e-4f) d = fwd;
        float angle = Math.Min(Vector3.Up.AngleTo(d), 78f * Mathf.Pi / 180f);
        Vector3 axis = Vector3.Up.Cross(d);
        Quaternion q = axis.LengthSquared() > 1e-8f ? new Quaternion(axis.Normalized(), angle) : Quaternion.Identity;
        Basis B = new Basis(q) * new Basis(fwd, Vector3.Up, right);
        float pelY = 0.5f * H * Mathf.Cos(angle) + 0.07f + 0.12f * Mathf.Sin(angle);
        Vector3 pel = new(pos.X, pelY, pos.Z);
        Vector3 bodyUp = B.Y; Vector3 bodyRight = B.Z;
        var knee = new Vector3[2]; var ank = new Vector3[2]; var dir = new Vector3[2];
        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? -1f : 1f;
            Vector3 hip = pel + bodyRight * side * HipW;
            Vector3 target = hip - bodyUp * (Leg * 0.93f) + bodyRight * side * 0.10f * H - B.X * 0.05f * H;
            target.Y = Math.Max(target.Y, AnkleH);
            SolveLeg(hip, target, B.X, out knee[i], out ank[i]);
            dir[i] = fwd;
        }
        Vector3 shoulderC = pel + B * new Vector3(0, 0.035f * H + TorsoLen - 0.03f * H, 0);
        var handT = new Vector3[2]; var pole = new Vector3[2];
        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? -1f : 1f;
            handT[i] = f.HandPosition + bodyRight * side * 0.07f * H;
            pole[i] = (B * new Vector3(-0.3f, -1f, side * 0.6f)).Normalized();
        }
        RenderBody(pel, B, shoulderC, knee, ank, dir, handT, pole);
        for (int i = 0; i < 2; i++) { var ft = _feet[i]; ft.Planted = false; ft.Timed = false; ft.Ankle = ank[i]; ft.Plant = new Vector3(ank[i].X, AnkleH, ank[i].Z); }
        TrackSlide(new[] { false, false }, ank);
        _lastPos = pos;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // IK and rendering

    /// <summary>Two-bone IK with equal bone lengths; the knee bends toward <paramref name="pole"/>. Returns the knee and the reachable ankle.</summary>
    void SolveLeg(Vector3 hip, Vector3 ankle, Vector3 pole, out Vector3 knee, out Vector3 ankleOut)
    {
        float L = Leg * 0.5f;
        Vector3 d = ankle - hip;
        float len = d.Length();
        Vector3 dn = len > 1e-5f ? d / len : Vector3.Down;
        len = Mathf.Clamp(len, 0.12f * Leg, 0.995f * Leg);
        ankleOut = hip + dn * len;
        float half = len * 0.5f;
        float h = Mathf.Sqrt(Math.Max(0f, L * L - half * half));
        Vector3 p = pole - dn * pole.Dot(dn);
        if (p.LengthSquared() < 1e-6f) p = Vector3.Right - dn * Vector3.Right.Dot(dn);
        knee = hip + dn * half + p.Normalized() * h;
    }

    void SolveArm(Vector3 shoulder, Vector3 hand, Vector3 pole, out Vector3 elbow, out Vector3 handOut)
    {
        float L1 = ArmUp, L2 = ArmLow;
        Vector3 d = hand - shoulder;
        float len = d.Length();
        Vector3 dn = len > 1e-5f ? d / len : Vector3.Down;
        len = Mathf.Clamp(len, Math.Abs(L1 - L2) + 0.02f, (L1 + L2) * 0.995f);
        handOut = shoulder + dn * len;
        float x = (len * len + L1 * L1 - L2 * L2) / (2f * len);
        float h = Mathf.Sqrt(Math.Max(0f, L1 * L1 - x * x));
        Vector3 p = pole - dn * pole.Dot(dn);
        if (p.LengthSquared() < 1e-6f) p = Vector3.Back - dn * Vector3.Back.Dot(dn);
        elbow = shoulder + dn * x + p.Normalized() * h;
    }

    void RenderBody(Vector3 pel, Basis B, Vector3 shoulderC, Vector3[] knee, Vector3[] ankle, Vector3[] dir, Vector3[] handT, Vector3[] elbowPole)
    {
        // torso and pelvis
        SetBox(_pelvis, pel, B, new Vector3(0.15f * H, 0.085f * H, 2f * HipW + 0.045f * H));
        Vector3 torsoC = pel + B * new Vector3(0, 0.035f * H + TorsoLen * 0.5f, 0);
        SetBox(_torso, torsoC, B, new Vector3(0.17f * H, TorsoLen, 0.30f * H));
        Vector3 neck = pel + B * new Vector3(0, 0.035f * H + TorsoLen + 0.02f * H, 0);
        Vector3 headC = neck + B.Y * (0.078f * H + 0.012f * H);
        SetSph(_head, headC, 0.155f * H);

        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? -1f : 1f;
            Vector3 hip = pel + B * new Vector3(0, -0.01f * H, side * HipW);
            SetSeg(_thigh[i], hip, knee[i], 0.118f * H);
            SetSeg(_shin[i], knee[i], ankle[i], 0.092f * H);
            SetSph(_knee[i], knee[i], 0.100f * H);
            Vector3 fd = dir[i]; fd.Y = 0; fd = fd.LengthSquared() > 1e-6f ? fd.Normalized() : Vector3.Right;
            Vector3 fr = new(-fd.Z, 0, fd.X);
            Vector3 fc = ankle[i] + fd * (0.4f * FootLen) + new Vector3(0, -AnkleH + FootH * 0.5f, 0);
            SetBox(_foot[i], fc, new Basis(fd, Vector3.Up, fr), new Vector3(FootLen, FootH, FootW));

            Vector3 sh = shoulderC + B * new Vector3(0, 0, side * ShoulderW);
            SolveArm(sh, handT[i], elbowPole[i], out var elbow, out var hand);
            SetSeg(_upper[i], sh, elbow, 0.078f * H);
            SetSeg(_fore[i], elbow, hand, 0.064f * H);
            SetSph(_elbow[i], elbow, 0.072f * H);
            SetSph(_hand[i], hand, (IsKeeper ? 0.105f : 0.068f) * H);
        }
    }

    static void SetBox(MeshInstance3D m, Vector3 c, Basis orient, Vector3 size) =>
        m.Transform = new Transform3D(orient * Basis.FromScale(size), c);

    static void SetSph(MeshInstance3D m, Vector3 c, float diameter) =>
        m.Transform = new Transform3D(Basis.FromScale(Vector3.One * diameter), c);

    static void SetSeg(MeshInstance3D m, Vector3 a, Vector3 b, float diameter)
    {
        Vector3 d = b - a;
        float len = d.Length();
        if (len < 1e-4f) { m.Visible = false; return; }
        m.Visible = true;
        Vector3 y = d / len;
        Vector3 x = Vector3.Right;
        if (Math.Abs(y.Dot(x)) > 0.9f) x = Vector3.Back;
        x = (x - y * x.Dot(y)).Normalized();
        Vector3 z = x.Cross(y);
        m.Transform = new Transform3D(new Basis(x * diameter, y * (len + 0.02f), z * diameter), (a + b) * 0.5f);
    }
}
