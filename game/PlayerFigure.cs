using System;
using System.Collections.Generic;
using Godot;

namespace FootballSim.Game;

/// <summary>The part of the boot that plays the ball (same order as the simulation's FootSurface).</summary>
public enum BootSurface { Inside, Laces, Outside, Sole }

/// <summary>Where a touch is in time (same order as the simulation's ActionPhase).</summary>
public enum TouchPhase { Planned, PreWindup, Windup, Contact, Recovery, Done }

/// <summary>One action of the simulation as plain values: what the figure draws for a touch. The figure never decides touch timing.</summary>
public struct FigureAction
{
    public bool Valid;
    /// <summary>The simulation's ActionKind as an integer (for logs only).</summary>
    public int Kind;
    public TouchPhase Phase;
    public bool Right;
    public BootSurface Surface;
    /// <summary>The tick the action was planned on; identifies the action.</summary>
    public int Id;
    /// <summary>The tick of the contact (with <see cref="Id"/> it identifies the made touch).</summary>
    public int ContactTick;
    /// <summary>Seconds from now to the contact (negative once it has passed).</summary>
    public float ContactEta;
    /// <summary>Seconds from the contact to the end of the recovery.</summary>
    public float RecoverySeconds;
    /// <summary>Ball centre at the contact (the prediction until the contact, then fixed).</summary>
    public Vector3 ContactPoint;
    /// <summary>Ball velocity applied at the contact (zero before it).</summary>
    public Vector3 BallOutVelocity;
    /// <summary>Where the ball is meant to meet the next foot (NaN when no next meet is planned); its direction from <see cref="ContactPoint"/> is the expected push.</summary>
    public Vector3 NextMeetPoint;
    /// <summary>Distance from the ball to <see cref="ContactPoint"/> at the contact (NaN before it).</summary>
    public float ContactMiss;
    public bool Made;
    /// <summary>A skill move: the figure draws it from <see cref="FigureFrame.Skill"/>, not as a dribble touch.</summary>
    public bool IsSkill;
}

/// <summary>The simulation's shot kinds, in the order of its ActionKind from Power on.</summary>
public enum ShotKind { Power, Finesse, Chip, Driven, LowFinesse }

/// <summary>A shot under way (from the press to the end of its recovery) as plain values: the figure draws the pose and decides no timing.</summary>
public struct FigureShot
{
    public bool Valid;
    public ShotKind Kind;
    public bool Right;
    public BootSurface Surface;
    /// <summary>The tick the shot was pressed on; identifies the shot.</summary>
    public int Id;
    public TouchPhase Phase;
    /// <summary>Seconds since the press.</summary>
    public float Elapsed;
    /// <summary>Seconds from now to the contact (negative once it has passed); the core can move it until the contact.</summary>
    public float ContactEta;
    /// <summary>Seconds from the contact to the end of the recovery.</summary>
    public float RecoverySeconds;
    /// <summary>Ball centre at the contact (the prediction until the contact, then fixed) and the ball velocity applied then (zero before it).</summary>
    public Vector3 ContactPoint, BallOutVelocity;
    public bool Made;
    /// <summary>Shoot not released yet: the contact tick is then only the earliest possible one.</summary>
    public bool Held;
    /// <summary>From the contact point toward the goal's centre (flat; zero when unknown): the drawn estimate of where the ball goes until the contact.</summary>
    public Vector3 Aim;
}

/// <summary>The simulation's skill moves, in the order of its ActionKind from StepOver on.</summary>
public enum SkillKind { StepOver, BodyFeint, DragBack, BallRoll, FakeStop, FakeCut }

/// <summary>A skill move under way as plain values: the figure draws the pose for its elapsed time and decides no timing.</summary>
public struct FigureSkill
{
    public bool Valid;
    public SkillKind Kind;
    /// <summary>The tick the move started on; identifies the move.</summary>
    public int Id;
    /// <summary>-1 left, +1 right, 0 none (relative to the running direction).</summary>
    public float Side;
    /// <summary>The foot the move is drawn with, and the part of it that meets the ball.</summary>
    public bool Right;
    public BootSurface Surface;
    /// <summary>Seconds since the move started (the reach or close-in begins), and its whole length.</summary>
    public float Elapsed, Duration;
    /// <summary>Seconds since the move's own motion began (negative during the reach or close-in), and the length of that motion.</summary>
    public float ExecElapsed, ExecDuration;
    /// <summary>Seconds since the sole landed (negative during the reach), and the length of the reach (0 without one).</summary>
    public float ContactElapsed, ReachSpan;
    /// <summary>Ball centre at the sole's first contact (fixed once made), and the ball velocity applied then.</summary>
    public Vector3 ContactPoint, OutVelocity;
}

/// <summary>Plain-value input for one figure for one physics tick. The figure never sees the simulation types.</summary>
public struct FigureFrame
{
    public Vector3 Position;          // feet centre on the ground
    public float Facing;              // radians, 0 = +X, positive toward +Z
    public Vector3 Velocity;
    /// <summary>The running speed without the steering correction; NaN when unknown (lean then follows the velocity).</summary>
    public float RunSpeed;
    public float HeightM;
    public bool InPossession, CloseControl;
    public Vector3 BallPos, BallVel;
    public float BallRadius;
    /// <summary>A touch / strike happened this tick; BallPos is the ball at contact and BallVel its velocity just after.</summary>
    public bool TouchEvent, StrikeEvent, EventRight;
    /// <summary>The skill move under way (a touch event it raises is <see cref="SkillTouch"/>, never <see cref="TouchEvent"/>).</summary>
    public FigureSkill Skill;
    /// <summary>The shot under way (a touch event it raises is the strike event).</summary>
    public FigureShot Shot;
    public bool SkillTouch;
    /// <summary>The simulation's stride clock (false when the frame carries none): phase 0..2 ([0,1) the right foot swings, [1,2) the left), steps per second,
    /// the swing foot, progress through its swing (0.5 = swing middle) and each foot's own phase (swing during [0, 0.5)).</summary>
    public bool HasGait, IsStepping, SwingRight;
    public float GaitPhase, StepFrequency, SwingProgress, RightFootPhase, LeftFootPhase;
    /// <summary>The pending or latest touch, and the one before it while it still follows through.</summary>
    public FigureAction Action, PrevAction;
    /// <summary>
    /// The touch the core is expected to make at once soon (an exit, soft or first touch, which has no windup of its own), as an unmade
    /// action in its windup; <see cref="UpcomingReady"/> when it waits on the stick with the ball still at the feet. Not valid when none.
    /// </summary>
    public FigureAction Upcoming; public bool UpcomingReady;
    /// <summary>Look-ahead (NaN when none): seconds to the next touch, which foot, where the ball will be.</summary>
    public float NextTouchEta; public bool NextTouchRight; public Vector3 NextTouchPoint;
    /// <summary>Seconds until a pending shot makes contact (NaN when none) and the foot that will strike (also used while charging).</summary>
    public float StrikeEta; public bool StrikeRight;
    public float ShotCharge;
    /// <summary>The ball is in the keeper's hands (also true while <see cref="KeeperAction"/> is Holding).</summary>
    public bool Holding;
    /// <summary>The keeper's contract state (Phase D): the action, the save kind while diving, the seconds since the action began, and where the hands go.</summary>
    public FootballSim.Core.KeeperAction KeeperAction;
    public FootballSim.Core.KeeperDive KeeperDive;
    public float KeeperActionTime;
    public Vector3 HandPosition;
    /// <summary>Where the keeper's current save, smother or slide expects to meet the ball, and when (seconds after the action began; NaN when none is under way).</summary>
    public Vector3 SaveTarget; public float SaveContactTime;
    public float Dt;

    public static FigureFrame At(Vector3 pos, float facing) => new()
    {
        Position = pos, Facing = facing, HeightM = 1.8f, BallRadius = 0.11f, BallPos = pos + new Vector3(2, 0.11f, 0), RunSpeed = float.NaN,
        NextTouchEta = float.NaN, StrikeEta = float.NaN, SaveContactTime = float.NaN, Dt = 1f / 120f,
    };
}

/// <summary>Foot-contact, foot-slide and pose-limit measurements collected by figures (replay mode prints them on quit).</summary>
public sealed class FigureStats
{
    public readonly List<float> SkillContact = new();
    /// <summary>Lowest pelvis ratio and largest |lean| (degrees) while a skill move is drawn.</summary>
    public float SkillMinPelvisRatio = float.MaxValue, SkillLeanMaxDeg;
    /// <summary>
    /// Largest per-tick move of the playing foot during a move and the three ticks after it: relative to the body, and in the world;
    /// and the largest sole-to-ball distance from the landing to the start of the move's hand-back.
    /// </summary>
    public float SkillFootJumpBody, SkillFootJumpWorld, SkillSoleMaxErr;
    /// <summary>The same per-tick foot move (body-relative, either foot) outside skill moves, for scale: ordinary running and touches.</summary>
    public float PlainFootJumpBody;
    public readonly List<float> Contact = new(), ContactTouch = new(), ContactStrike = new(), Slide = new(), Skin = new(), TouchMiss = new(), ContactBall = new();
    /// <summary>The keeper's planted-foot slides (his rush and charge reuse the run cycle), kept apart from the attacker's <see cref="Slide"/>.</summary>
    public readonly List<float> SlideKeeper = new();
    public float MinPelvisRatio = float.MaxValue, MinKneeM = float.MaxValue, MinLimbYM = float.MaxValue, LeanMinDeg = float.MaxValue, LeanMaxDeg = float.MinValue;
    /// <summary>Running only (speed above 2 m/s, no shot under way): lowest pelvis ratio (close control apart), lowest in close control, largest |lean|, largest per-step bob.</summary>
    public float RunMinPelvisRatio = float.MaxValue, CloseMinPelvisRatio = float.MaxValue, RunLeanMaxDeg, RunBobMaxM;
    /// <summary>
    /// The outfield body's shape (BODY_POSE): mean trunk lean while running steadily (no acceleration to speak of) at a jog (4-5.8 m/s) and a
    /// sprint (above 6.8 m/s), the largest bank, the elbow's inside angle range while running, and the mean running pelvis ratio.
    /// </summary>
    public float LeanCloseSum; public int LeanCloseN;
    public float LeanJogSum, LeanSprintSum, BankMaxDeg, ElbowMinDeg = float.MaxValue, ElbowMaxDeg, RunRatioSum; public int LeanJogN, LeanSprintN, RunRatioN;
    public string BodyLine() =>
        $"BODY_POSE lean_jog_deg={(LeanJogN > 0 ? LeanJogSum / LeanJogN : float.NaN):F1} lean_sprint_deg={(LeanSprintN > 0 ? LeanSprintSum / LeanSprintN : float.NaN):F1} bank_max_deg={BankMaxDeg:F1}" +
        $" lean_close_deg={(LeanCloseN > 0 ? LeanCloseSum / LeanCloseN : float.NaN):F1} elbow_deg_min={(ElbowMinDeg == float.MaxValue ? float.NaN : ElbowMinDeg):F0} elbow_deg_max={ElbowMaxDeg:F0} run_pelvis_ratio_mean={(RunRatioN > 0 ? RunRatioSum / RunRatioN : float.NaN):F3}";
    /// <summary>
    /// The shown pose in --attacker-anim mocap: the lowest toe tip, the fastest bone turn (local, degrees per second; shots left out)
    /// and where, ticks over 1,500 deg/s, and the ticks measured and those drawn wholly from the cycles.
    /// </summary>
    public float MinToeYM = float.MaxValue, BoneMaxDegS; public string BoneMaxAt = "-"; public int BoneFast, MocapTicks, MocapFull;
    /// <summary>The same for the playing foot's own bones (foot, toes) while it plays the ball (touch, skill move, kick): exempt from the body check, reported apart.</summary>
    public float FootTurnMaxDegS; public string FootTurnAt = "-"; public int FootTurnFast;
    /// <summary>The same bone turn on shot ticks (press to the end of the follow-through), the striking leg left out while it is drawn for the strike.</summary>
    public float ShotBoneMaxDegS; public string ShotBoneAt = "-"; public int ShotBoneFast, ShotBoneTicks;
    public string MocapLine() =>
        $"MOCAP_POSE min_toe_y_m={(MinToeYM == float.MaxValue ? float.NaN : MinToeYM):F3} bone_max_deg_s={BoneMaxDegS:F0} at={BoneMaxAt} bone_over_1500={BoneFast} ticks={MocapTicks} full_mocap_ticks={MocapFull}" +
        $"\nPLAYING_FOOT_TURN max_deg_s={FootTurnMaxDegS:F0} at={FootTurnAt} over_1500={FootTurnFast}" +
        $"\nSHOT_BONE max_deg_s={ShotBoneMaxDegS:F0} at={ShotBoneAt} over_1500={ShotBoneFast} ticks={ShotBoneTicks}";
    /// <summary>Touches counted, and those where the foot drawn in the swing differs from the simulation's action foot.</summary>
    public int Touches, FootMismatches;
    /// <summary>The shots' soft touches (made on the press tick, not drawn; out of <see cref="Touches"/>).</summary>
    public int SoftTouches;

    /// <summary>What the attacker is doing on a tick, for the per-frame foot-jump stat.</summary>
    public enum JumpContext { Skill, Exit, Touch, Shot, Other }
    static readonly string[] JumpNames = { "skill", "exit", "touch", "shot", "other" };
    /// <summary>
    /// Per foot and tick: the larger of the ankle's and the boot tip's world move since the previous tick (one rendered frame at 120 Hz) and since
    /// two ticks before (one rendered frame at 60 Hz), by context; and the tick, foot and size of the largest one-tick move per context.
    /// </summary>
    public readonly List<float>[] Jump = { new(), new(), new(), new(), new() }, Jump60 = { new(), new(), new(), new(), new() };
    public readonly (int Tick, int Foot, float M)[] JumpWorst = new (int, int, float)[5];
    /// <summary>The match tick being presented (set by the match before the figures update), for the worst-jump report.</summary>
    public int Tick;
    public float JumpDt = 1f / 120f;

    /// <summary>
    /// Per context: the largest excess of a one-tick move over its limit (<paramref name="limit"/> in <see cref="AddJump"/>: 0.10 m for the
    /// procedural body, 0.10 m + 1.2 x speed x dt in mocap mode, where a swinging foot really travels two steps per step of the body), and
    /// the change of a foot's velocity from one tick to the next (m/s per tick: what a pop is, whatever the speed), all samples and the worst.
    /// </summary>
    public readonly float[] JumpOver = new float[5];
    public readonly List<float>[] JumpDv = { new(), new(), new(), new(), new() };
    public readonly (int Tick, int Foot, float M)[] DvWorst = new (int, int, float)[5];

    public void AddJump(JumpContext c, int foot, float m, float m60, float limit = 0.10f, float dv = float.NaN)
    {
        int i = (int)c;
        Jump[i].Add(m);
        if (!float.IsNaN(m60)) Jump60[i].Add(m60);
        if (m > JumpWorst[i].M) JumpWorst[i] = (Tick, foot, m);
        JumpOver[i] = Math.Max(JumpOver[i], m - limit);
        if (!float.IsNaN(dv)) { JumpDv[i].Add(dv); if (dv > DvWorst[i].M) DvWorst[i] = (Tick, foot, dv); }
    }

    public IEnumerable<string> JumpLines()
    {
        var all = new List<float>(); var all60 = new List<float>();
        for (int i = 0; i < 5; i++)
        {
            all.AddRange(Jump[i]); all60.AddRange(Jump60[i]);
            var w = JumpWorst[i];
            var dw = DvWorst[i];
            yield return $"FOOT_JUMP ctx={JumpNames[i]} dt_s={JumpDt:F5} n={Jump[i].Count} max_m={Max(Jump[i]):F3} p99_m={P99(Jump[i]):F3} max60_m={Max(Jump60[i]):F3} p99_60_m={P99(Jump60[i]):F3} worst_tick={w.Tick} worst_foot={(w.Foot == 1 ? "R" : "L")}" +
                $" over_m={Math.Max(0f, JumpOver[i]):F3} dv_max_mps={Max(JumpDv[i]):F2} dv_p99_mps={P99(JumpDv[i]):F2} dv_tick={dw.Tick} dv_foot={(dw.Foot == 1 ? "R" : "L")}";
        }
        yield return $"FOOT_JUMP ctx=all dt_s={JumpDt:F5} n={all.Count} max_m={Max(all):F3} p99_m={P99(all):F3} max60_m={Max(all60):F3} p99_60_m={P99(all60):F3}";
        yield return StrideLine();
    }

    /// <summary>
    /// Running steps of the attacker (speed above 2 m/s, no skill move or shot): the time from one foot's plant to the next foot's plant
    /// and the ground distance between the two plants. Regular running has a small spread (cv = standard deviation / mean).
    /// </summary>
    public readonly List<float> StepTime = new(), StepLen = new();

    public string StrideLine() =>
        $"STRIDE n={StepTime.Count} step_s_mean={Mean(StepTime):F3} step_s_cv={Cv(StepTime):F3} step_m_mean={Mean(StepLen):F3} step_m_cv={Cv(StepLen):F3}";

    static float Cv(List<float> l)
    {
        if (l.Count < 2) return 0;
        float m = Mean(l), v = 0;
        foreach (var x in l) v += (x - m) * (x - m);
        return m > 0 ? MathF.Sqrt(v / (l.Count - 1)) / m : 0;
    }

    static float P99(List<float> l)
    {
        if (l.Count == 0) return 0;
        var s = new List<float>(l); s.Sort();
        return s[Math.Min(s.Count - 1, (int)Math.Ceiling(0.99 * s.Count) - 1)];
    }

    public string ContactLine() => Line("FOOT_CONTACT", Contact);
    public string ContactTouchLine() => Line("FOOT_CONTACT_TOUCH", ContactTouch);
    public string ContactStrikeLine() => Line("FOOT_CONTACT_STRIKE", ContactStrike);
    /// <summary>The attacker's planted-foot slide, then the keeper's on its own line.</summary>
    public string SlideLine() => Line("FOOT_SLIDE", Slide) + "\n" + Line("FOOT_SLIDE_KEEPER", SlideKeeper);
    public string ContactBallLine() => Line("FOOT_CONTACT_BALL", ContactBall);
    public string SkinLine() => Line("SKIN_CONTACT", Skin);
    public string SkillLine() => Line("SKILL_CONTACT", SkillContact) + $" skill_min_pelvis_ratio={F(SkillMinPelvisRatio)} skill_lean_max_deg={SkillLeanMaxDeg:F1} foot_jump_body_m={SkillFootJumpBody:F3} foot_jump_world_m={SkillFootJumpWorld:F3} plain_foot_jump_body_m={PlainFootJumpBody:F3} sole_max_err_m={SkillSoleMaxErr:F3}";
    public string TouchMissLine() => Line("TOUCH_MISS", TouchMiss);
    public string TouchFootLine() => $"TOUCH_FOOT n={Touches} mismatches={FootMismatches} soft_undrawn={SoftTouches}";
    public string PoseLine() =>
        $"POSE_LIMITS min_pelvis_ratio={MinPelvisRatio:F3} min_knee_m={MinKneeM:F3} min_limb_y_m={MinLimbYM:F3} lean_deg_min={LeanMinDeg:F1} lean_deg_max={LeanMaxDeg:F1}" +
        $" run_min_pelvis_ratio={F(RunMinPelvisRatio)} close_min_pelvis_ratio={F(CloseMinPelvisRatio)} run_lean_max_deg={RunLeanMaxDeg:F1} run_bob_max_m={RunBobMaxM:F3}" + "\n" + BodyLine() + (MocapTicks > 0 || BoneMaxDegS > 0 ? "\n" + MocapLine() : "");

    static string F(float v) => v == float.MaxValue ? "n/a" : v.ToString("F3");
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

    // ---- kit ----
    readonly FigureMaterials _mat = new()
    {
        Shirt = MeshKit.Shaded(Colors.Red, 0.85f), Shorts = MeshKit.Shaded(Colors.White, 0.85f), Socks = MeshKit.Shaded(Colors.Red, 0.9f),
        Skin = MeshKit.Shaded(Colors.White, 0.6f, 0.3f, 0.6f), Boots = MeshKit.Shaded(Colors.Black, 0.4f, 0.3f, 0.2f),
        Gloves = MeshKit.Shaded(Colors.White, 0.55f), Hair = MeshKit.Shaded(Colors.Black, 0.7f, 0.3f, 0.3f),
        Sole = MeshKit.Shaded(new Color(0.9f, 0.9f, 0.9f), 0.5f, 0.2f), Cuff = MeshKit.Shaded(new Color(0.95f, 0.95f, 0.95f), 0.8f),
        Eye = MeshKit.Shaded(new Color(0.05f, 0.05f, 0.05f), 0.4f, 0f),
    };
    FigureKit _kit = FigureKit.Attacker;
    Label3D _number = null!;

    /// <summary>Colours, skin tone and shirt number; assign any time (the default is the attacker kit; KeeperView sets the keeper's).</summary>
    public FigureKit Kit
    {
        get => _kit;
        set { _kit = value; ApplyKit(); }
    }

    /// <summary>Skin tone only.</summary>
    public Color SkinTone { get => _kit.Skin; set => Kit = _kit with { Skin = value }; }

    public void SetColors(Color shirt, Color shorts, Color socks) => Kit = _kit with { Shirt = shirt, Shorts = shorts, Socks = socks };

    void ApplyKit()
    {
        _mat.Shirt.AlbedoColor = _kit.Shirt; _mat.Shorts.AlbedoColor = _kit.Shorts; _mat.Socks.AlbedoColor = _kit.Socks;
        _mat.Skin.AlbedoColor = _kit.Skin; _mat.Boots.AlbedoColor = _kit.Boots; _mat.Gloves.AlbedoColor = _kit.Gloves; _mat.Hair.AlbedoColor = _kit.Hair;
        if (_number != null) { _number.Text = _kit.Number.ToString(); _number.Modulate = _kit.NumberColor; }
        _body?.ApplyKit(_kit);
    }

    // ---- skinned body (optional): follows the joints computed below; the primitive meshes are hidden while it is active ----
    Node3D _prims = null!;
    SkinnedBody? _body;
    readonly FigureJoints _joints = new();

    /// <summary>The joints computed by the latest <c>Update</c> (world space), for anything that follows the figure.</summary>
    public FigureJoints Joints => _joints;

    /// <summary>True while a skinned body replaces the primitive meshes.</summary>
    public bool IsSkinned => _body != null;

    /// <summary>Replaces the primitive meshes with the rigged model at <paramref name="path"/>; on failure the primitives stay and the reason is returned.</summary>
    public bool TryUseSkinned(string path, out string error)
    {
        var body = SkinnedBody.TryLoad(path, out error);
        if (body == null) return false;
        _body?.QueueFree();
        _body = body;
        AddChild(body);
        _prims.Visible = false;
        _number.Visible = false;
        body.ApplyKit(_kit);
        if (!IsKeeper) body.CurlFingers(FingerCurlRad, ThumbCurlRad);   // the attacker's hands: a loose half fist (the keeper keeps open gloves)
        return true;
    }

    // ---- parts ----
    MeshInstance3D _pelvis = null!, _torso = null!, _head = null!, _neck = null!;
    readonly MeshInstance3D[] _thigh = new MeshInstance3D[2], _shin = new MeshInstance3D[2], _foot = new MeshInstance3D[2], _shoulder = new MeshInstance3D[2],
        _upper = new MeshInstance3D[2], _fore = new MeshInstance3D[2], _hand = new MeshInstance3D[2];

    // ---- dimensions (functions of height) ----
    float H = 1.8f, Leg, AnkleH, FootLen, FootW, FootH, HipW, TorsoLen, ShoulderW, ArmUp, ArmLow, Instep;

    sealed class FootState
    {
        public bool Planted; public Vector3 Plant;
        public bool Timed, TFixed; public Vector3 TFrom, TTarget; public float TT, TDur;
        public Vector3 Ankle; public Vector3 Dir = new(1, 0, 0);
        public bool Tracking; public Vector3 TrackStart; public float TrackMax;
        /// <summary>Running cycle: the foot is in its stance window; blending back to the cycle (0..1) after the idle stance, a pin or a dive; where the blend starts.</summary>
        public bool InStance, Released; public float Blend = 1f; public Vector3 BlendFrom;
        /// <summary>Running cycle: last tick's path point, and an offset from it being taken out (weight 1 to 0) where the path did not continue from the drawn foot.</summary>
        public bool HasRaw; public Vector3 PrevRaw, RawVel, Off; public float OffW;
    }
    readonly FootState[] _feet = { new(), new() };   // 0 = left, 1 = right

    /// <summary>The touch the foot is drawing: its windup length as first seen, and the foot's pelvis-relative position and yaw at the contact.</summary>
    sealed class TouchFoot
    {
        public int Id = int.MinValue; public float Span = 0.12f;
        public Vector3 RelC; public float Psi;
        public int ContactId = int.MinValue, ContactTick;
        /// <summary>The surface the windup is drawn with: the one the touch had when its windup began (the core may flip it for a tick or two before the contact).</summary>
        public int SurfId = int.MinValue; public BootSurface Surf;
        /// <summary>
        /// The windup's target last tick (relative to the body) and the jump being taken out: when the core moves a committed touch's
        /// contact point (a re-predicted ball), the target's jump is kept as an offset that shrinks with the time left to the contact,
        /// so the foot bends its path onto the new point and still meets it exactly at the contact tick.
        /// </summary>
        public int TgtId = int.MinValue; public Vector3 TgtRel, TgtStep, TgtOff; public float TgtTau0;
    }

    /// <summary>
    /// Per foot, for the last few touches (by id, whichever slot they are in: a touch moves from pending to previous at its contact): the
    /// ball-out direction drawn last tick, and the tick each entry was last used.
    /// </summary>
    readonly Vector3[,] _odDir = new Vector3[2, 3];
    readonly int[,] _odId = { { int.MinValue, int.MinValue, int.MinValue }, { int.MinValue, int.MinValue, int.MinValue } };
    readonly int[,] _odUsed = new int[2, 3];
    int _odClock;

    /// <summary>The drawn ball-out direction for touch <paramref name="id"/> of foot <paramref name="i"/>: <paramref name="d"/> turned to from last tick's at no more than <see cref="OutDirTurnRate"/>.</summary>
    Vector3 DrawnOutDir(int i, int id, Vector3 d, float dt)
    {
        int slot = -1, oldest = 0;
        for (int e = 0; e < 3; e++)
        {
            if (_odId[i, e] == id) { slot = e; break; }
            if (_odUsed[i, e] < _odUsed[i, oldest]) oldest = e;
        }
        _odClock++;
        if (slot < 0 || _teleporting)
        {
            if (slot < 0) slot = oldest;
            _odId[i, slot] = id; _odDir[i, slot] = d; _odUsed[i, slot] = _odClock;
            return d;
        }
        Vector3 od = _odDir[i, slot];
        float from = Mathf.Atan2(od.Z, od.X), to = Mathf.Atan2(d.Z, d.X);
        float diff = Mathf.Wrap(to - from, -Mathf.Pi, Mathf.Pi), step = OutDirTurnRate * Math.Max(dt, 1e-4f);
        float yaw = from + Math.Clamp(diff, -step, step);
        var r = new Vector3(Mathf.Cos(yaw), 0, Mathf.Sin(yaw));
        _odDir[i, slot] = r; _odUsed[i, slot] = _odClock;
        return r;
    }

    /// <summary>
    /// How fast the drawn ball-out direction of a touch may turn, radians per second. Before contact it is the run direction, at contact the
    /// launch: a turn touch would otherwise swing the foot's contact surface round the ball (and the ankle with it) in one tick.
    /// </summary>
    const float OutDirTurnRate = 4f * Mathf.Pi;
    /// <summary>Seconds before a dribble touch's contact the foot starts toward it (the core's windup is shorter: the rest comes from its pre-windup).</summary>
    const float TouchLead = 0.20f;
    readonly TouchFoot[] _tf = { new(), new() }, _tfUp = { new(), new() };
    /// <summary>Per foot: the touch pose drawn last tick came from an unmade touch (a windup or a predicted touch), its weight, and the ankle drawn
    /// (relative to the pelvis); a fade from that ankle when such a touch vanishes without contact, and how long is left of it.</summary>
    readonly bool[] _ovWind = new bool[2], _ovWindPrev = new bool[2];
    readonly float[] _twPrev = new float[2], _kwPrev = new float[2], _dropT = new float[2], _yawPrev = new float[2], _dropYaw = new float[2];
    readonly Vector3[] _ankRelPrev = new Vector3[2], _dropFrom = new Vector3[2];
    bool _teleporting;
    const float DropFade = 0.12f;

    // ---- state ----
    bool _init, _moving;
    Vector3 _lastPos, _prevHv, _accS;
    float _speedS, _lean, _runPrev, _accFS, _ratioS = 0.9945f;
    // shot sequences
    float _strikeT = -1f, _windT, _lastCharge;
    bool _strikeRight, _backswingRight;
    float _backswing;
    // the shot being drawn: its press tick, the highest charge seen since, the foot's body-relative start and its ankle at the contact
    int _shId = int.MinValue; float _shCharge, _shSwingT; bool _shReleased; Vector3 _shFromRel; bool _shContactSet; Vector3 _shRelC;
    bool _stActive; Vector3 _stRel0; int _stFoot;
    Vector3 _contactAnkle, _contactDir, _followAnkle;
    // per-tick kick plan
    float _kW; int _kFoot; Vector3 _kAnkle, _kDir; bool _pinOn; int _pinFoot; Vector3 _pinTarget; float _leanExtra; bool _seq;
    float _leanExtraS; bool _pinWas; Vector3 _pinLock;
    // skill moves: the move being drawn, where the foot was when it started (relative to the body), body offsets for this tick, last frame's final ankles
    int _skId = int.MinValue; Vector3 _skFromRel; bool _skActive, _skSoleLogged, _skInLogged; int _skFoot, _skTail; bool _skJumpValid; readonly Vector3[] _prevAnkWorld = new Vector3[2];
    Vector3 _skPelOff, _skShoulderOff;
    readonly Vector3[] _prevAnkRel = new Vector3[2];
    // bob measurement: extremes of the pelvis height above its commanded height within one step
    int _bobStep = -1; float _bobMin, _bobMax;

    static float Smooth(float x) => Mathf.SmoothStep(0f, 1f, Mathf.Clamp(x, 0f, 1f));
    static float Frac(float x) => x - Mathf.Floor(x);
    static float Dist2(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    public override void _Ready()
    {
        bool sleeves = IsKeeper;
        _prims = new Node3D { Name = "Primitives" };
        AddChild(_prims);
        _pelvis = Part("Pelvis", FigureMeshes.Pelvis(_mat));
        _torso = Part("Torso", FigureMeshes.Torso(_mat));
        _neck = Part("Neck", FigureMeshes.Neck(_mat));
        _head = Part("Head", FigureMeshes.Head(_mat));
        for (int i = 0; i < 2; i++)
        {
            _thigh[i] = Part($"Thigh{i}", FigureMeshes.Thigh(_mat)); _shin[i] = Part($"Shin{i}", FigureMeshes.Shin(_mat));
            _foot[i] = Part($"Foot{i}", FigureMeshes.Boot(_mat));
            _shoulder[i] = Part($"Shoulder{i}", FigureMeshes.Shoulder(_mat));
            _upper[i] = Part($"Upper{i}", FigureMeshes.UpperArm(_mat, sleeves)); _fore[i] = Part($"Fore{i}", FigureMeshes.Forearm(_mat, sleeves));
            _hand[i] = Part($"Hand{i}", FigureMeshes.Hand(_mat, IsKeeper));
        }
        _number = new Label3D
        {
            Name = "Number", PixelSize = 0.0016f, FontSize = 150, OutlineSize = 0, Shaded = false, DoubleSided = false,
            AlphaCut = Label3D.AlphaCutMode.Discard, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_number);
        ApplyKit();
        SetDims(1.8f);
    }

    MeshInstance3D Part(string n, Mesh mesh)
    {
        var mi = new MeshInstance3D { Name = n, Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.On };
        _prims.AddChild(mi);
        return mi;
    }

    void SetDims(float h)
    {
        H = h;
        Leg = 0.48f * h; AnkleH = 0.035f * h; FootLen = 0.17f * h; FootW = 0.075f * h; FootH = 0.05f * h;
        HipW = 0.095f * h; TorsoLen = 0.30f * h; ShoulderW = 0.15f * h; ArmUp = 0.19f * h; ArmLow = 0.17f * h;
        Instep = 0.45f * FootLen;
        // the outfield figure's ankle sits where the rigged boot's does, so the skinned leg spans exactly the figure's hip-to-ankle line
        if (!IsKeeper && _body != null) AnkleH = _body.AnkleRestHeightFor(h);
    }

    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Snap to a frame with no history (round reset): feet planted under the hips, no interpolation across the jump.</summary>
    public void Teleport(in FigureFrame f)
    {
        _init = false;
        _strikeT = -1f; _windT = 0; _lastCharge = 0; _lean = 0; _leanExtraS = 0; _stActive = false; _pinWas = false; _skId = int.MinValue; _skJumpValid = false; _jN = 0; _jWasSkill = _jExit = false;
        _accFS = 0; _runPrev = float.NaN; _ratioS = 0.9945f; _bobStep = -1; _kInit = false;
        _stepW = 0; _closeW = 0; _latS = 0; _bank = 0; _pelYaw = 0; _headYaw = float.NaN; _headPitch = 0;
        for (int i = 0; i < 2; i++) { _cycPitch[i] = 0f; _cycToe[i] = 0f; _hintPrev[i] = false; _hoId[i] = int.MinValue; }
        foreach (var t in _tf) { t.Id = t.ContactId = t.SurfId = int.MinValue; }
        foreach (var t in _tfUp) { t.Id = t.ContactId = t.SurfId = int.MinValue; }
        for (int i = 0; i < 2; i++) { _twPrev[i] = 0f; _kwPrev[i] = 0f; _dropT[i] = 0f; _ovWindPrev[i] = false; }
        _teleporting = true;
        _keeperAnim?.Teleport();
        Update(f);
        _teleporting = false;
        ResetPhysicsInterpolation();
        _body?.ResetInterpolation();
    }

    void InitState(Vector3 pos, Vector3 fwd, Vector3 right)
    {
        _init = true; _lastPos = pos; _prevHv = Vector3.Zero; _accS = Vector3.Zero; _speedS = 0; _moving = false;
        for (int i = 0; i < 2; i++)
        {
            var ft = _feet[i];
            ft.Planted = true; ft.Timed = false; ft.Tracking = false; ft.InStance = false; ft.Blend = 1f;
            ft.Plant = pos + right * (i == 0 ? -HipW * 0.9f : HipW * 0.9f); ft.Plant.Y = AnkleH;
            ft.Ankle = ft.Plant; ft.Dir = fwd; ft.HasRaw = false; ft.OffW = 0f;
        }
    }

    /// <summary>The pelvis height standing: the keeper's reference keeps his slightly bent legs; the outfield figure's is a straight leg.</summary>
    float StandY => IsKeeper ? AnkleH + Leg * 0.97f : AnkleH + Leg;

    const float MaxLeanRad = 12f * Mathf.Pi / 180f;

    float _sinkS;
    const float SinkRecover = 0.08f;

    // ---- the outfield body's layers (the keeper keeps his own pose code and none of these) ----
    // Each is a smooth function of the simulation's stride clock and smoothed speed / acceleration, so no change of speed, direction or plan
    // pops the pose, and nothing reads a clock or a random number.
    /// <summary>Forward lean with speed (on top of the acceleration lean): radians at <see cref="SpeedLeanFullMs"/> and above, none below 1 m/s.</summary>
    const float SpeedLeanMaxRad = 0.24f, SpeedLeanFromMs = 3f, SpeedLeanFullMs = 7.4f;
    /// <summary>The share of the speed lean the pelvis takes forward over the feet (times its height): the lean is a body line from the ankles, not a bend at the waist.</summary>
    const float LeanFromAnkles = 0.6f;
    /// <summary>Outfield hip-to-ankle span as a fraction of the leg: standing, running at a jog / a sprint, in close control.</summary>
    const float StandLeg = 0.98f, RunLegJog = 0.965f, RunLegSprint = 0.95f, RunLegClose = 0.93f;
    /// <summary>The forward lean with speed (radians).</summary>
    float SpeedLean => SpeedLeanMaxRad * Mathf.Clamp((_speedS - SpeedLeanFromMs) / (SpeedLeanFullMs - SpeedLeanFromMs), 0f, 1f);
    /// <summary>0 at a jog's speed and below, 1 at a sprint's: what makes the sprint a different gait (higher heel recovery, bigger arms).</summary>
    float SprintW => Mathf.Clamp((_speedS - 5f) / 2.2f, 0f, 1f);
    /// <summary>The share of the trunk's forward lean the pelvis takes (the spine bends the rest).</summary>
    const float PelvisLeanShare = 0.75f;
    /// <summary>Banking into a turn: roll = atan(lateral acceleration / g) * gain, capped; eased with the tau; the pelvis moves into the turn by
    /// this fraction of its height times sin(roll) so the feet stay outside the body as they do on a real curve.</summary>
    const float BankGain = 0.8f, BankMaxRad = 0.26f, BankTau = 0.08f, BankShift = 0.45f;
    /// <summary>Pelvis yaw with the stride: radians per metre of fore-aft spread between the feet (the hip goes forward with its leg), capped; eased.</summary>
    const float PelYawPerM = 0.16f, PelYawMax = 0.17f, PelYawTau = 0.04f;
    /// <summary>The chest turns against the pelvis: its yaw is minus the pelvis's times this (walk to sprint).</summary>
    const float ChestCounterWalk = 0.5f, ChestCounterSprint = 1.2f;
    /// <summary>Pelvis drop on the swinging leg's side (radians), half of it taken back by the chest; pelvis shift over the stance foot (metres at 1.8 m).</summary>
    const float PelDropRad = 0.07f, SwayM = 0.018f;
    /// <summary>Pelvis bob amplitude (metres at 1.8 m), walking and running; lowest at mid-stance.</summary>
    const float BobWalk = 0.010f, BobRun = 0.022f;
    /// <summary>How fast the stepping, close-control and stride-shape weights follow their state.</summary>
    const float StepWTau = 0.12f;
    /// <summary>Swing ankle apex above its ground height (metres at 1.8 m): walk, extra by a run, extra at a sprint; the apex comes early (heel recovery behind the body).</summary>
    const float SwingLiftWalk = 0.04f, SwingLiftRun = 0.12f, SwingLiftSprint = 0.30f, SwingApexPow = 0.8f;
    /// <summary>
    /// Foot pitch through the stride (radians, positive = toes up), each walk value plus the run extra: the heel strikes toes-up, the foot is
    /// flat through mid-stance, rolls onto its toes to push off and folds toes-down behind in the heel recovery, then turns up for the next strike.
    /// </summary>
    const float HeelStrikeWalk = 0.10f, HeelStrikeRun = 0.14f, ToeOffWalk = 0.18f, ToeOffRun = 0.30f, ToeOffSprint = 0.15f, RecoveryWalk = 0.30f, RecoveryRun = 0.60f, RecoverySprint = 0.35f, PitchTau = 0.025f;
    /// <summary>
    /// Arms (radians): a compact runner's arm. Shoulder swing amplitude walk / jog / sprint (about +-20 and +-40 degrees), centred a little behind
    /// the body so the hand travels from behind the hip to the lower chest; elbow flexion standing / walking / jogging / sprinting (100-110 and
    /// 90 degrees inside) and its swing (more bent in front); the forearm comes across the body in front; out from the body by default, in close
    /// control and in a turn (at full bank).
    /// </summary>
    const float ArmSwingWalk = 0.20f, ArmSwingJog = 0.36f, ArmSwingSprintAmp = 0.70f, ArmBack = -0.14f, ArmAcross = 0.35f;
    const float ElbowStand = 0.25f, ElbowWalk = 0.55f, ElbowJog = 1.30f, ElbowSprint = 1.57f, ElbowSwing = 0.22f;
    /// <summary>Finger and thumb curl per joint (radians): a loose half fist.</summary>
    const float FingerCurlRad = 0.45f, ThumbCurlRad = 0.25f;
    const float ArmOutBase = 0.15f, ArmOutClose = 0.22f, ArmOutTurn = 0.25f;
    /// <summary>Head: stays level, looks along the travel and, with the ball, partly toward it (yaw share, pitch share of the angle down to it,
    /// capped), within a yaw limit from the chest; eased with the tau.</summary>
    const float HeadBallYaw = 0.45f, HeadBallPitch = 0.40f, HeadPitchMaxRad = 0.45f, HeadPitchBase = 0.06f, HeadYawMaxRad = 0.9f, HeadTau = 0.10f;
    // their state: stepping / close-control weights, smoothed lateral acceleration, bank, pelvis yaw, head look, per-foot pitch and toe bend from the cycle
    float _stepW, _closeW, _latS, _bank, _pelYaw, _headYaw = float.NaN, _headPitch;
    readonly float[] _cycPitch = new float[2], _cycToe = new float[2];
    /// <summary>How fast a foot may turn about the vertical outside a touch's windup and contact (radians per second); the yaw drawn last tick.</summary>
    const float FootTurnRate = 3f * Mathf.Pi;
    readonly float[] _yawOut = new float[2]; readonly bool[] _yawOutOk = new bool[2];
    // what the render pass draws beyond the torso basis (outfield only)
    bool _split; Basis _pelB, _headB; readonly float[] _pitch = new float[2], _toeBend = new float[2];

    /// <summary>How far the ankle rises when the foot pitches by <paramref name="pitch"/> so the sole stays on the ground: about the ball of the foot when the heel is up, about the heel when the toes are up.</summary>
    float PivotLift(float pitch)
    {
        float a = AnkleH, c = Mathf.Cos(pitch), s = Mathf.Sin(Math.Abs(pitch));
        float lever = pitch < 0f ? 0.45f * FootLen : 0.20f * FootLen;
        return a * c + lever * s - a;
    }

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
        _lastPos = pos;

        _speedS = Mathf.Lerp(_speedS, speed, 1f - Mathf.Exp(-dt / 0.10f));
        _accS = _accS.Lerp((hv - _prevHv) / dt, 1f - Mathf.Exp(-dt / 0.10f)); _prevHv = hv;
        float accF = Mathf.Clamp(_accS.Dot(fwd), -9f, 9f);
        float runSpeed = float.IsNaN(f.RunSpeed) ? speed : f.RunSpeed;
        float accRaw = float.IsNaN(_runPrev) ? 0f : (runSpeed - _runPrev) / dt; _runPrev = runSpeed;
        _accFS = Mathf.Lerp(_accFS, accRaw, 1f - Mathf.Exp(-dt / 0.12f));
        float sfs = Mathf.Clamp((_speedS - 0.5f) / 8f, 0f, 1f);

        bool stepping = f.HasGait && f.IsStepping;
        Vector3 fm = speed > 0.5f ? hv / speed : fwd;

        // ---- the outfield body's smoothed drivers: stepping and close-control weights, lateral (centripetal) acceleration and the bank it gives ----
        bool body = !IsKeeper;
        float runW = Mathf.Clamp((_speedS - 1.5f) / 2.5f, 0f, 1f);   // 0 at a walk, 1 from a jog on: the stride's running shape
        if (body)
        {
            float aw = 1f - Mathf.Exp(-dt / StepWTau);
            _stepW = Mathf.Lerp(_stepW, stepping ? 1f : 0f, aw);
            _closeW = Mathf.Lerp(_closeW, f.CloseControl ? 1f : 0f, aw);
            float ab = 1f - Mathf.Exp(-dt / BankTau);
            _latS = Mathf.Lerp(_latS, Mathf.Clamp(_accS.Dot(right), -12f, 12f), ab);
            float bankT = Mathf.Clamp(Mathf.Atan(_latS / 9.81f) * BankGain * Mathf.Clamp(_speedS / 3f, 0f, 1f), -BankMaxRad, BankMaxRad);
            _bank = Mathf.Lerp(_bank, bankT, ab);
        }

        if (IsKeeper)
        {
            // every keeper state but Rush and Charge is drawn by the keeper pose code; those reuse the run cycle below, blended with it
            bool rushing = f.KeeperAction is FootballSim.Core.KeeperAction.Rush or FootballSim.Core.KeeperAction.Charge;
            _wR = rushing ? Math.Min(1f, _wR + dt / 0.12f) : Math.Max(0f, _wR - dt / 0.15f);
            if (!rushing) { UpdateKeeper(f, pos, fwd, right, hv, speed, dt, null); return; }
        }

        // ---- posture: pelvis height as a fraction of the standing height ----
        float ratioT;
        if (IsKeeper)
        {
            float legFrac = Mathf.Lerp(0.965f, 0.90f, sfs);
            legFrac = stepping ? legFrac : (f.Holding ? 0.94f : 0.84f);
            ratioT = (AnkleH + Leg * legFrac) / StandY;
            _ratioS = ratioT;
        }
        else
        {
            // the hip-to-ankle span as a fraction of the leg: tall running (a little lower at a sprint, lower in close control), the bob takes
            // it lower at mid-stance; never pulled down to reach a ball
            float legFrac = stepping ? (f.CloseControl ? RunLegClose : Mathf.Lerp(RunLegJog, RunLegSprint, SprintW)) : StandLeg;
            ratioT = (AnkleH + Leg * legFrac) / StandY;
            _ratioS = Mathf.Lerp(_ratioS, ratioT, 1f - Mathf.Exp(-dt / 0.12f));
        }
        float desiredY = StandY * _ratioS;
        float reach = 0.985f * Leg;
        float hgt = desiredY - AnkleH;
        float hmax = Mathf.Sqrt(Math.Max(0.01f, reach * reach - hgt * hgt));   // how far from the hip a straight-ish leg can plant
        float hwf = HipW * (IsKeeper ? 2.4f : 0.9f);

        // ---- shot plan: wind-up / strike / follow-through ----
        PlanKick(f, pos, fwd, right, fm, dt, desiredY, hmax);
        if (_pinOn) { if (!_pinWas) _pinLock = _pinTarget; _pinTarget = _pinLock; }   // the support foot goes where the ball will be
        _pinWas = _pinOn;

        // ---- pelvis: sim position; reach assist toward a shot target (capped: at most 0.25 m of translation, never below 0.85 of standing height) ----
        Vector3 pel = new(pos.X, desiredY, pos.Z);
        pel += _skPelOff;
        float floorY = IsKeeper ? 0.5f * StandY : (_skActive ? 0.91f : 0.85f) * StandY;
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
                _leanExtra += (_skActive ? 0.05f : 0.22f) * Math.Min(1f, sh / 0.25f);
                float needV = Mathf.Sqrt(Math.Max(0f, lim * lim - dhLen * dhLen));
                float drop = Math.Max(0f, vert - needV) * _kW;
                pel.Y = Math.Max(floorY, pel.Y - drop);
            }
        }
        Vector3 pelXZ = new(pel.X, 0, pel.Z);

        // ---- running cycle from the simulation's stride clock ----
        // Each foot is on the ground for a stance window centred on the other foot's swing middle (phase 0.75 of its own cycle) and in the air
        // for the rest. The window is as wide as a straight-ish leg allows: the planted foot moves from ahead of the hips to behind them as
        // the body passes over it, so the step length is speed / step frequency and a planted foot never moves.
        float stepLen = 0f, w = 0.5f;
        if (stepping)
        {
            stepLen = speed / Math.Max(f.StepFrequency, 1.5f);
            w = Mathf.Clamp(0.85f * hmax / Math.Max(stepLen, 0.01f), 0.04f, 0.5f);
            // the outfield stride follows the smoothed speed (a speed nudge must not move a plant's timing or place)
            if (body)
            {
                stepLen = _speedS / Math.Max(f.StepFrequency, 1.5f);
                w = Mathf.Clamp(0.85f * hmax / Math.Max(stepLen, 0.01f), 0.04f, 0.5f);
            }
        }
        MocapBegin(f, pos, fwd, stepping, _speedS / Math.Max(f.StepFrequency, 1.5f), dt);
        // pelvis bob: one per step, lowest at the swing middle when the body is over the planted foot
        if (stepping)
        {
            float bob = body ? Mathf.Lerp(BobWalk, BobRun, runW) * Mathf.Clamp(_speedS / 2f, 0f, 1f) : 0.010f * Mathf.Clamp(_speedS / 3f, 0f, 1f);
            pel.Y += bob * k * Mathf.Cos(2f * Mathf.Pi * f.GaitPhase);
        }
        float hgtNow = pel.Y - AnkleH;
        float hmaxNow = Mathf.Sqrt(Math.Max(0.01f, reach * reach - hgtNow * hgtNow));
        if (stepping && !_moving)
            foreach (var ft in _feet) { ft.Blend = 0f; ft.BlendFrom = ft.Ankle; ft.Timed = false; ft.InStance = false; ft.Planted = false; }
        else if (!stepping && _moving)
            foreach (var ft in _feet) { ft.Planted = false; ft.Timed = false; ft.InStance = false; }
        _moving = stepping;

        for (int i = 0; i < 2; i++)
        {
            var ft = _feet[i];
            float side = i == 0 ? -1f : 1f;
            if (_pinOn && i == _pinFoot)
            {
                StepPin(ft, dt);
                ft.Blend = 0f; ft.BlendFrom = ft.Ankle; ft.InStance = false;
                if (body) { float ap = 1f - Mathf.Exp(-dt / PitchTau); _cycPitch[i] = Mathf.Lerp(_cycPitch[i], 0f, ap); _cycToe[i] = Mathf.Lerp(_cycToe[i], 0f, ap); }
                continue;
            }
            float pitchT = 0f, toeT = 0f;
            if (stepping)
            {
                ft.Timed = false;
                float phi = i == 0 ? f.LeftFootPhase : f.RightFootPhase;
                float sA = 0.75f - w * 0.5f, sB = 0.75f + w * 0.5f;
                Vector3 lat = right * side * hwf;
                bool stance = phi >= sA && phi < sB, leave = false;
                Vector3 gait;
                if (stance)
                {
                    if (!ft.InStance) { ft.InStance = true; ft.Released = false; ft.Plant = pelXZ + fm * (w * stepLen) + lat; ft.Plant.Y = AnkleH; if (_mW <= 0f) MeasureStep(f, ft.Plant, speed); }
                    // the body gained speed and the planted foot is about to be out of reach: lift it early, to the rear of the cycle
                    if (!ft.Released && Dist2(pelXZ + right * side * HipW, ft.Plant) > 0.97f * hmaxNow) { ft.Released = true; ft.Blend = 0f; ft.BlendFrom = ft.Ankle; }
                    gait = ft.Released ? pelXZ - fm * (w * stepLen) + lat : ft.Plant;
                    if (ft.Released) gait.Y = AnkleH;
                    if (body)
                    {
                        // heel strike (toes up) easing flat, then the roll onto the toes to push off; a foot lifted early stays on its toes
                        float st = Mathf.Clamp((phi - sA) / w, 0f, 1f);
                        float hs = HeelStrikeWalk + HeelStrikeRun * runW, to = ToeOffWalk + ToeOffRun * runW + ToeOffSprint * SprintW;
                        pitchT = ft.Released ? -to : hs * (1f - Smooth(st / 0.25f)) - to * Smooth((st - 0.5f) / 0.5f);
                        toeT = Math.Max(0f, -pitchT);
                    }
                }
                else
                {
                    leave = ft.InStance && !ft.Released && ft.Blend >= 1f;
                    ft.InStance = false; ft.Released = false;
                    float pu = phi >= sB ? phi : phi + 1f;
                    float sa = Mathf.Clamp((pu - sB) / (1f - w), 0f, 1f);
                    gait = pelXZ + fm * (w * stepLen * (2f * Smooth(sa) - 1f)) + lat;
                    if (body)
                    {
                        // heel recovery: the ankle comes up early, behind the body, then the knee drives it forward and down to the strike;
                        // the foot folds toes-down behind (on from the push-off) and turns toes-up for the heel strike
                        float apex = SwingLiftWalk + SwingLiftRun * runW + SwingLiftSprint * SprintW;
                        gait.Y = AnkleH + apex * k * Mathf.Sin(Mathf.Pi * Mathf.Pow(sa, SwingApexPow));
                        float hs = HeelStrikeWalk + HeelStrikeRun * runW, to = ToeOffWalk + ToeOffRun * runW + ToeOffSprint * SprintW;
                        float rc = RecoveryWalk + RecoveryRun * runW + RecoverySprint * SprintW;
                        pitchT = sa < 0.35f ? Mathf.Lerp(-to, -rc, Smooth(sa / 0.35f)) : Mathf.Lerp(-rc, hs, Smooth((sa - 0.35f) / 0.65f));
                        toeT = Math.Max(0f, -pitchT) * (1f - Smooth(sa / 0.3f));
                    }
                    else gait.Y = AnkleH + (0.04f + 0.16f * sfs) * k * Mathf.Sin(Mathf.Pi * sa);
                    // wind-up: while the shot is charging, the striking foot trails further back on its natural back-swing
                    if (_backswing > 0f && (i == 1) == _backswingRight)
                    {
                        float bs = Mathf.Sin(Mathf.Pi * sa);
                        gait -= fwd * (0.22f * _backswing * k * bs);
                        gait.Y += 0.42f * _backswing * k * bs;
                    }
                }
                // Where the cycle's own path does not continue from where the foot was drawn (the foot leaves the ground from where it was
                // planted, which the swing start matches only at a steady speed; the speed changed at once, as in a stop against the wall),
                // the difference is carried and taken out over a short time, so the foot keeps the path's motion and never jumps.
                bool jumped = ft.HasRaw && (gait - (ft.PrevRaw + ft.RawVel)).Length() > 0.05f;
                ft.RawVel = ft.HasRaw ? gait - ft.PrevRaw : Vector3.Zero;
                ft.PrevRaw = gait; ft.HasRaw = true;
                if (leave || jumped)
                {
                    ft.Off = ft.Ankle - gait; ft.OffW = 1f; ft.Blend = 1f;
                }
                if (ft.Blend < 1f)
                {
                    ft.Blend = Math.Min(1f, ft.Blend + dt / 0.10f);
                    gait = ft.BlendFrom.Lerp(gait, Smooth(ft.Blend));
                }
                if (ft.OffW > 0f)
                {
                    gait += ft.Off * Smooth(ft.OffW);
                    ft.OffW = Math.Max(0f, ft.OffW - dt / 0.10f);
                }
                ft.Ankle = gait;
                ft.Planted = stance && !ft.Released && ft.Blend >= 1f && ft.OffW <= 0f;
            }
            else { IdleFoot(i, ft, pelXZ, right, side, hwf, dt); ft.HasRaw = false; ft.OffW = 0f; }
            ft.Dir = fwd;
            if (body)
            {
                // the cycle's foot pitch and toe bend, eased so a start, stop or blend never snaps the foot round
                float ap = _teleporting ? 1f : 1f - Mathf.Exp(-dt / PitchTau);
                _cycPitch[i] = Mathf.Lerp(_cycPitch[i], stepping ? pitchT * _stepW : 0f, ap);
                _cycToe[i] = Mathf.Lerp(_cycToe[i], stepping ? toeT * _stepW : 0f, ap);
            }
        }

        // ---- assembling the final ankles ----

        var ank = new Vector3[2]; var dir = new Vector3[2]; var planted = new bool[2];
        var tsurf = new BootSurface[2]; var tw = new float[2];
        var fTouch = SoftTouchFrame(f, dt);   // (the touch the shot's soft touch takes out of the core's slots still follows through)
        for (int i = 0; i < 2; i++)
        {
            ank[i] = _feet[i].Ankle; dir[i] = _feet[i].Dir; planted[i] = _feet[i].Planted && !_feet[i].Timed;
            MocapFoot(i, f, pelXZ, right, hmaxNow, speed, ref ank[i], ref dir[i], ref planted[i], dt);
            tw[i] = TouchOverlay(fTouch, i, pelXZ, hv, fm, k, ref ank[i], ref dir[i], out tsurf[i]);
            if (tw[i] > 0f) planted[i] = false;
            if (_kW > 0f && i == _kFoot)
            {
                // a touch the core makes with the striking foot while the shot still winds up (the ball kept at that foot) is drawn over
                // the drawn-back foot: the foot comes through to the ball for it and goes back to the wind-up after
                float kwi = f.Shot.Valid && !f.Shot.Made ? _kW * (1f - tw[i]) : _kW;
                ank[i] = ank[i].Lerp(_kAnkle, kwi);
                dir[i] = dir[i].Lerp(_kDir, kwi).Normalized();
                planted[i] = false;
            }
            // a shot's plant step to beside the ball (when the stride does not have that foot down at the strike), held where it lands
            float plw = _plW > 0f && i == _plFoot && !IsKeeper ? _plW : 0f;
            if (plw > 0f)
            {
                ank[i] = ank[i].Lerp(_plAnkle, plw);
                dir[i] = dir[i].Lerp(_plDir, plw).Normalized();
                planted[i] = _plHeld;
            }
            // an unmade touch (a windup, or a predicted immediate touch) that vanishes without its contact (dropped, or not made as
            // predicted): the foot goes from where it was drawn back to its pose over a short swing instead of in one frame
            // the same when a shot or skill pose is dropped at once (a fake shot aborts the shot, a lost ball ends the move)
            float kw = _kW > 0f && i == _kFoot ? (f.Shot.Valid && !f.Shot.Made ? _kW * (1f - tw[i]) : _kW) : 0f;
            // the stride's foot pitch gives way to a touch, kick or skill pose by their weights (a contact is drawn flat, exactly as planned)
            float pw = (1f - tw[i]) * (1f - kw) * (1f - plw);
            // (so does a made touch's follow-through the shot's soft touch takes out of the core's slots: the soft touch is not drawn)
            bool dropped = (_ovWindPrev[i] || f.Action.Valid && f.Action.Kind == SoftTouchKind) && _twPrev[i] > 0.05f && tw[i] < _twPrev[i] - 0.2f || kw < _kwPrev[i] - 0.2f;
            if (dropped && !(tw[i] >= 0.999f && !_ovWind[i]))
            { _dropT[i] = DropFade; _dropFrom[i] = _ankRelPrev[i]; _dropYaw[i] = _yawPrev[i]; }
            _kwPrev[i] = kw;
            if (tw[i] >= 0.999f && !_ovWind[i]) _dropT[i] = 0f;   // a contact is drawn exactly
            if (_dropT[i] > 0f)
            {
                float u = Smooth(1f - _dropT[i] / DropFade);
                ank[i] = (pelXZ + _dropFrom[i]).Lerp(ank[i], u);
                float yaw = Mathf.LerpAngle(_dropYaw[i], Mathf.Atan2(dir[i].Z, dir[i].X), u);
                dir[i] = new Vector3(Mathf.Cos(yaw), 0, Mathf.Sin(yaw));
                planted[i] = false;
                _dropT[i] -= dt;
                pw *= u;
            }
            if (body)
            {
                float mp = MocapPitch(i, _cycPitch[i], ank[i], out float mToe, out float mLift);
                ank[i].Y += mLift;
                _pitch[i] = mp * pw; _toeBend[i] = (Mathf.Lerp(_cycToe[i], 0f, _mW) + mToe) * pw;
                MocapPitchRate(i, tw[i] >= 0.999f && !_ovWind[i], dt, ref ank[i]);
                ank[i].Y += PivotLift(_pitch[i]) * (1f - _mW);   // the cycle's ankle already rises as its heel lifts
                if (_pitch[i] < -0.03f) planted[i] = false;   // the heel is up: the foot rolls on its toes and is no longer planted flat
            }
            if (body)
            {
                // outside a windup and its contact tick (which must be drawn exactly) the foot turns at a limited rate, so handing back from a
                // touch's sideways contact foot to the stride never swings the boot round in a frame or two; a shot's striking foot is never
                // limited (its swing turns the boot onto the strike direction faster, and the kick weight already blends it in and out)
                float yawW = Mathf.Atan2(dir[i].Z, dir[i].X);
                if (!_teleporting && _yawOutOk[i] && !_ovWind[i] && tw[i] < 0.999f && kw <= 0f)
                {
                    float step = FootTurnRate * dt;
                    yawW = _yawOut[i] + Math.Clamp(Mathf.Wrap(yawW - _yawOut[i], -Mathf.Pi, Mathf.Pi), -step, step);
                    dir[i] = new Vector3(Mathf.Cos(yawW), 0, Mathf.Sin(yawW));
                }
                _yawOut[i] = yawW; _yawOutOk[i] = true;
            }
            _twPrev[i] = tw[i]; _ovWindPrev[i] = _ovWind[i];
            _ankRelPrev[i] = ank[i] - pelXZ; _yawPrev[i] = Mathf.Atan2(dir[i].Z, dir[i].X);
        }
        if (body)
        {
            // the pelvis shifts over the stance foot and into a turn (the feet stay where the stride put them), and yaws with the stride:
            // the hip goes forward with its leg, so it follows the fore-aft spread of the drawn feet (a touch reaching forward turns it too)
            pel -= right * (SwayM * k * (1f - 0.5f * sfs) * _stepW * Mathf.Sin(Mathf.Pi * f.GaitPhase));
            pel += right * (Mathf.Sin(_bank) * BankShift * (pel.Y - AnkleH));
            float spread = (ank[1] - ank[0]).Dot(fwd);
            float yawT = Mathf.Clamp(PelYawPerM * spread, -PelYawMax, PelYawMax);
            _pelYaw = _teleporting ? yawT : Mathf.Lerp(_pelYaw, yawT, 1f - Mathf.Exp(-dt / PelYawTau));
            pel = MocapPelvis(pel, right);
            pel.Y -= PlantPelvisDrop(pel, right);
        }
        // a touch from a standstill (the first touch of a loose ball) can lie beyond a straight leg: only then the pelvis sinks by what is missing
        // (at most 6 cm); a running player never does, the simulation steers the ball into the zone ahead of the swinging foot
        // (the outfield figure runs tall, so its touch poses may sink the pelvis while running too, by what the leg lacks, at most 10 cm)
        float pelSink0 = pel.Y;
        if (!stepping || body)
            for (int i = 0; i < 2; i++)
            {
                if (tw[i] <= 0f) continue;
                Vector3 hip = pel + right * (i == 0 ? -HipW : HipW);
                Vector3 dd = ank[i] - hip;
                float lim = 0.995f * Leg, dhs = new Vector2(dd.X, dd.Z).Length();
                if (dhs >= lim) continue;
                float drop = (pel.Y - ank[i].Y) - Mathf.Sqrt(lim * lim - dhs * dhs);
                // (with the cycles the foot leaving the ground can be behind a straight leg as a touch begins: the sink goes with the touch's weight)
                if (drop > 0f) pel.Y = Math.Max(floorY, pel.Y - Math.Min(drop, body ? 0.10f : 0.06f) * Mathf.Lerp(1f, tw[i], _mW));
            }
        if (body)
        {
            // the outfield pelvis comes back up from a touch's sink over SinkRecover, not in the ticks the touch's weight falls in
            // (a first touch from a standstill would straighten the standing knee at 2,000 degrees per second)
            float sink = pelSink0 - pel.Y;
            _sinkS = _teleporting ? sink : Math.Max(sink, _sinkS * Mathf.Exp(-dt / SinkRecover));
            pel.Y = Math.Max(floorY, pelSink0 - _sinkS);
        }
        // a planted foot that would be over-extended lowers the pelvis a little (never below the floor); the stance window above keeps this from happening
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

        // ---- lean: outfield players lean with their acceleration (signed, at most 12 degrees) and forward with their speed; keepers and shots keep the old lean ----
        float leanT;
        if (IsKeeper) leanT = stepping ? 0.07f + 0.19f * sfs + Mathf.Clamp(0.012f * accF, 0f, 0.08f) : 0f;
        else leanT = (stepping ? Mathf.Clamp(_accFS / 4f, -1f, 1f) * MaxLeanRad : 0f) + SpeedLean;
        _lean = Mathf.Lerp(_lean, leanT, 1f - Mathf.Exp(-dt / 0.12f));
        _leanExtraS = Mathf.Lerp(_leanExtraS, _leanExtra, 1f - Mathf.Exp(-dt / 0.05f));
        float lean = _lean + _leanExtraS;
        lean = Mathf.Clamp(lean, -0.42f, 0.42f);
        Basis B = new Basis(right, -lean) * new Basis(fwd, Vector3.Up, right);
        Basis P = B;
        bool running0 = body && stepping && speed > 2f && !_seq && _strikeT < 0f && _leanExtraS < 0.01f && f.ShotCharge <= 0.001f && !f.Skill.Valid;
        if (Stats != null && body)
        {
            Stats.BankMaxDeg = Math.Max(Stats.BankMaxDeg, Math.Abs(_bank) * 180f / Mathf.Pi);
            bool steady = running0 && Math.Abs(_accFS) < 1f;
            if (_atkAnim == null) LeanStats(running0, speed, lean, f.CloseControl);   // mocap: from the shown pose, after it is drawn
        }
        // the speed lean tips the whole body forward from the ankles: the pelvis moves ahead of the feet, the trunk leans with it
        if (body) pel += fwd * (Mathf.Sin(SpeedLean) * LeanFromAnkles * (pel.Y - AnkleH) * (1f - _mW));
        float counter = Mathf.Lerp(ChestCounterWalk, ChestCounterSprint, sfs);
        if (body)
        {
            // split trunk: the pelvis yaws with the stride, drops on the swinging side, banks and takes part of the lean; the chest counter-turns,
            // takes back half the drop, banks and takes the whole lean (the spine bends between them)
            Basis yawB = new Basis(fwd, Vector3.Up, right);
            float roll = PelDropRad * _stepW * Mathf.Sin(Mathf.Pi * f.GaitPhase);   // + right side down while the right leg swings
            P = new Basis(Vector3.Up, _pelYaw) * new Basis(fwd, _bank + roll) * new Basis(right, -lean * PelvisLeanShare) * yawB;
            B = new Basis(Vector3.Up, -_pelYaw * counter) * new Basis(fwd, _bank - 0.5f * roll) * new Basis(right, -lean) * yawB;
            P = MocapPelvisBasis(P, fwd, right);
        }

        // ---- legs ----
        var knee = new Vector3[2]; var ankEff = new Vector3[2];
        for (int i = 0; i < 2; i++)
        {
            Vector3 hip = body ? pel + P * new Vector3(0, 0, i == 0 ? -HipW : HipW) : pel + right * (i == 0 ? -HipW : HipW);
            // (a shot's plant foot takes the soft reach out by its weight, so neither its landing nor its release moves it in a tick)
            if (_plW > 0f && i == _plFoot && !IsKeeper) ank[i] = MocapSoftReach(hip, ank[i]).Lerp(ank[i], _plW);
            else if (!planted[i] && tw[i] <= 0f && !(_kW > 0f && i == _kFoot)) ank[i] = MocapSoftReach(hip, ank[i]);
            SolveLeg(hip, ank[i], fwd, out knee[i], out ankEff[i]);
        }

        // ---- arms: each swings against the leg on its own side (forward when the opposite foot lands) ----
        Vector3 shoulderC = pel + B * new Vector3(0, 0.035f * H + TorsoLen - 0.03f * H, 0) + _skShoulderOff;
        var handT = new Vector3[2]; var elbowPole = new Vector3[2];
        float amp = 0.15f + 0.45f * sfs;
        // outfield arms: bent elbows (near a right angle at a run), swinging against the legs by speed, out from the body for balance in close
        // control and in a turn; built from shoulder and elbow angles in the chest frame, so the elbow lands where the angles put it
        float armA = _stepW * Mathf.Lerp(Mathf.Lerp(ArmSwingWalk, ArmSwingJog, runW), ArmSwingSprintAmp, SprintW);
        float elbow0 = Mathf.Lerp(ElbowStand, Mathf.Lerp(ElbowWalk, Mathf.Lerp(ElbowJog, ElbowSprint, SprintW), runW), _stepW);
        float abd = ArmOutBase + ArmOutClose * _closeW + ArmOutTurn * Math.Abs(_bank) / BankMaxRad + 0.35f * _lastCharge;
        for (int i = 0; body && i < 2; i++)
        {
            float side = i == 0 ? -1f : 1f;
            Vector3 sh = shoulderC + B * new Vector3(0, 0, side * ShoulderW);
            float oppPhi = i == 0 ? f.RightFootPhase : f.LeftFootPhase;
            float sw = Mathf.Cos(2f * Mathf.Pi * (oppPhi - 0.70f));
            float al = armA * sw + ArmBack * runW * _stepW;
            float be = elbow0 + ElbowSwing * runW * _stepW * sw;
            if (Stats != null && running0) { Stats.ElbowMinDeg = Math.Min(Stats.ElbowMinDeg, 180f - be * 180f / Mathf.Pi); Stats.ElbowMaxDeg = Math.Max(Stats.ElbowMaxDeg, 180f - be * 180f / Mathf.Pi); }
            Vector3 u = new(Mathf.Sin(al) * Mathf.Cos(abd), -Mathf.Cos(al) * Mathf.Cos(abd), side * Mathf.Sin(abd));
            float across = ArmAcross * runW * _stepW * Math.Max(0f, sw) * (1f - _closeW);
            Vector3 v = new Vector3(Mathf.Sin(al + be), -Mathf.Cos(al + be), side * (0.3f * Mathf.Sin(abd) - across)).Normalized();
            Vector3 el = sh + B * u * ArmUp;
            handT[i] = el + B * v * ArmLow;
            Vector3 pole = el - (sh + handT[i]) * 0.5f;
            elbowPole[i] = pole.LengthSquared() > 1e-8f ? pole.Normalized() : (B * new Vector3(-1f, -0.2f, side * 0.5f)).Normalized();
        }
        for (int i = 0; !body && i < 2; i++)
        {
            float side = i == 0 ? -1f : 1f;
            Vector3 sh = shoulderC + B * new Vector3(0, 0, side * ShoulderW);
            float oppPhi = i == 0 ? f.RightFootPhase : f.LeftFootPhase;
            float alpha = stepping ? amp * Mathf.Cos(2f * Mathf.Pi * (oppPhi - 0.70f)) : 0f;
            float bend = 0.10f + 0.30f * sfs;          // about 90 degrees of elbow at a sprint
            Vector3 dirLocal = new Vector3(Mathf.Sin(alpha), -Mathf.Cos(alpha), side * (0.12f + 0.35f * _lastCharge)).Normalized();
            handT[i] = sh + B * dirLocal * ((ArmUp + ArmLow) * (1f - bend));
            elbowPole[i] = (B * new Vector3(-1f, -0.2f, side * 0.5f)).Normalized();
        }
        if (IsKeeper) UpdateKeeper(f, pos, fwd, right, hv, speed, dt, new KPose { Pel = pel, Q = B.GetRotationQuaternion(), Knee = knee, Ank = ankEff, Dir = dir, Hand = handT, Pole = elbowPole });
        else
        {
            // the head stays level and steady: it looks along the travel and, with the ball, partly at it; within a yaw limit from the chest
            Vector3 look = speed > 1f ? fm : fwd;
            float yawT = Mathf.Atan2(look.Z, look.X);
            float pitchT = HeadPitchBase;
            Vector3 toBall = new(f.BallPos.X - pos.X, 0, f.BallPos.Z - pos.Z);
            float bd = toBall.Length();
            if (f.InPossession && bd > 0.15f)
            {
                yawT = Mathf.LerpAngle(yawT, Mathf.Atan2(toBall.Z, toBall.X), HeadBallYaw);
                pitchT = Mathf.Clamp(Mathf.Atan2(0.93f * H - f.BallPos.Y, bd) * HeadBallPitch, HeadPitchBase, HeadPitchMaxRad);
            }
            float chestYaw = f.Facing - _pelYaw * counter;
            yawT = chestYaw + Mathf.Clamp(Mathf.Wrap(yawT - chestYaw, -Mathf.Pi, Mathf.Pi), -HeadYawMaxRad, HeadYawMaxRad);
            float ah = 1f - Mathf.Exp(-dt / HeadTau);
            if (float.IsNaN(_headYaw) || _teleporting) { _headYaw = yawT; _headPitch = pitchT; }
            else { _headYaw = Mathf.LerpAngle(_headYaw, yawT, ah); _headPitch = Mathf.Lerp(_headPitch, pitchT, ah); }
            Vector3 hf = new(Mathf.Cos(_headYaw), 0, Mathf.Sin(_headYaw)), hr = new(-hf.Z, 0, hf.X);
            _split = true; _pelB = P;
            _headB = new Basis(hr, -_headPitch) * new Basis(hf, Vector3.Up, hr);
            RenderBody(pel, B, shoulderC, knee, ankEff, dir, handT, elbowPole);
            MocapShown(f, ankEff, knee, ref pel, ref lean, fwd, dt);
            if (_atkAnim != null && Stats != null) LeanStats(running0, speed, lean, f.CloseControl);
        }
        var posFlat = new Vector3(pos.X, 0, pos.Z);
        // the playing foot's per-tick move (body-relative and world), during the move and the three ticks after it
        if (!IsKeeper && Stats != null && (f.Skill.Valid || _skTail > 0))
        {
            if (f.Skill.Valid) { _skFoot = f.Skill.Right ? 1 : 0; _skTail = 3; } else _skTail--;
            Vector3 rel = ankEff[_skFoot] - posFlat; rel.Y = ankEff[_skFoot].Y;
            if (_skJumpValid)
            {
                Stats.SkillFootJumpBody = Math.Max(Stats.SkillFootJumpBody, (rel - _prevAnkRel[_skFoot]).Length());
                Stats.SkillFootJumpWorld = Math.Max(Stats.SkillFootJumpWorld, (ankEff[_skFoot] - _prevAnkWorld[_skFoot]).Length());
            }
        }
        if (!IsKeeper && Stats != null && !f.Skill.Valid && _skTail <= 0 && _skJumpValid)
            for (int i = 0; i < 2; i++)
            {
                Vector3 relI = ankEff[i] - posFlat; relI.Y = ankEff[i].Y;
                Stats.PlainFootJumpBody = Math.Max(Stats.PlainFootJumpBody, (relI - _prevAnkRel[i]).Length());
            }
        if (!IsKeeper && Stats != null) MeasureJump(f, ankEff, dir, tw);
        if (ShotLog && !IsKeeper && Stats != null && _kW > 0f) GD.Print($"SHOT_ANK tick={Stats.Tick} kA=({_kAnkle.X:F3},{_kAnkle.Y:F3},{_kAnkle.Z:F3}) ank=({ankEff[_kFoot].X:F3},{ankEff[_kFoot].Y:F3},{ankEff[_kFoot].Z:F3}) pel=({pel.X:F3},{pel.Y:F3},{pel.Z:F3}) kW={_kW:F2} plA=({_plAnkle.X:F3},{_plAnkle.Y:F3},{_plAnkle.Z:F3}) plShown=({ankEff[_plFoot].X:F3},{ankEff[_plFoot].Y:F3},{ankEff[_plFoot].Z:F3}) plW={_plW:F2} held={(_plHeld ? 1 : 0)}");
        _skJumpValid = true;
        _prevAnkWorld[0] = ankEff[0]; _prevAnkWorld[1] = ankEff[1];
        for (int i = 0; i < 2; i++) { _prevAnkRel2[i] = _prevAnkRel[i]; _prevAnkRel[i] = ankEff[i] - posFlat; _prevAnkRel[i].Y = ankEff[i].Y; }

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
            if (f.Skill.Valid) { Stats.SkillMinPelvisRatio = Math.Min(Stats.SkillMinPelvisRatio, pel.Y / StandY); Stats.SkillLeanMaxDeg = Math.Max(Stats.SkillLeanMaxDeg, Math.Abs(deg)); }
            Stats.LeanMinDeg = Math.Min(Stats.LeanMinDeg, deg); Stats.LeanMaxDeg = Math.Max(Stats.LeanMaxDeg, deg);
            bool running = stepping && speed > 2f && !_seq && _strikeT < 0f && _leanExtraS < 0.01f && f.ShotCharge <= 0.001f;
            if (running)
            {
                float ratio = pel.Y / StandY;
                if (f.CloseControl) Stats.CloseMinPelvisRatio = Math.Min(Stats.CloseMinPelvisRatio, ratio);
                else { Stats.RunMinPelvisRatio = Math.Min(Stats.RunMinPelvisRatio, ratio); Stats.RunRatioSum += ratio; Stats.RunRatioN++; }
                Stats.RunLeanMaxDeg = Math.Max(Stats.RunLeanMaxDeg, Math.Abs(deg));
                int stepId = (int)Mathf.Floor(f.GaitPhase);
                float dy = pel.Y - desiredY;
                if (stepId != _bobStep) { _bobStep = stepId; _bobMin = _bobMax = dy; }
                else { _bobMin = Math.Min(_bobMin, dy); _bobMax = Math.Max(_bobMax, dy); }
                Stats.RunBobMaxM = Math.Max(Stats.RunBobMaxM, _bobMax - _bobMin);
            }
            else _bobStep = -1;
        }
        if (!IsKeeper && f.Skill.Valid) LogSkillFoot(f, ankEff, dir);
        if (!IsKeeper && Stats != null && (f.TouchEvent || f.StrikeEvent))
        {
            // the touch just made is the current action, or the previous one when the next touch was planned on the same tick
            bool curMade = f.Action.Valid && f.Action.Made && f.Action.Phase == TouchPhase.Contact;
            bool prevMade = f.PrevAction.Valid && f.PrevAction.Made && f.PrevAction.Phase == TouchPhase.Contact;
            // the shot's soft touch is not drawn (see TouchOverlay): counted apart, out of the contact and foot checks
            if (f.TouchEvent && !f.StrikeEvent && (curMade || prevMade) && (curMade ? f.Action : f.PrevAction).Kind == SoftTouchKind)
            {
                Stats.SoftTouches++; LastContactError = float.NaN;
            }
            else if (f.TouchEvent && !f.StrikeEvent && (curMade || prevMade))
            {
                // the boot's contact surface of the action's foot against the ball surface at the contact tick; the figure aims at the action's contact point
                var a = curMade ? f.Action : f.PrevAction;
                int fi = a.Right ? 1 : 0;
                Vector3 off = Off(fi, a.Surface);
                Vector3 fd = dir[fi]; fd.Y = 0; fd = fd.Normalized();
                Vector3 fr = new(-fd.Z, 0, fd.X);
                Vector3 pt = ankEff[fi] + fd * off.X + Vector3.Up * off.Y + fr * off.Z;
                float err = Math.Abs((pt - a.ContactPoint).Length() - f.BallRadius);
                Stats.Contact.Add(err); Stats.ContactTouch.Add(err);
                LastContactError = err;
                Stats.ContactBall.Add(Math.Abs((pt - f.BallPos).Length() - f.BallRadius));
                if (!float.IsNaN(a.ContactMiss)) Stats.TouchMiss.Add(a.ContactMiss);
                if (_body != null && _body.HasSurfaces)
                    Stats.Skin.Add(Math.Abs((_body.SurfacePoint(fi, a.Surface) - a.ContactPoint).Length() - f.BallRadius));
                // the foot drawn in the swing (the stride clock's) and the foot the touch event names are both the action's foot
                Stats.Touches++;
                if (a.Right != f.EventRight || (f.HasGait && f.IsStepping && f.SwingRight != a.Right) || tw[fi] < 0.999f) Stats.FootMismatches++;
            }
            else if (f.StrikeEvent && f.Shot.Valid)
            {
                // the boot's contact surface of the shot's foot against the ball surface, aimed at the action's contact point
                int fi = f.Shot.Right ? 1 : 0;
                Vector3 off = Off(fi, f.Shot.Surface);
                Vector3 fd = dir[fi]; fd.Y = 0; fd = fd.Normalized();
                Vector3 fr = new(-fd.Z, 0, fd.X);
                Vector3 pt = ankEff[fi] + fd * off.X + Vector3.Up * off.Y + fr * off.Z;
                float err = Math.Abs((pt - f.Shot.ContactPoint).Length() - f.BallRadius);
                Stats.Contact.Add(err); Stats.ContactStrike.Add(err);
                LastContactError = err;
            }
            else
            {
                // the instep point of the striking foot (or, if no kick was planned, the nearer foot) to the ball surface
                float err = float.MaxValue;
                for (int i = 0; i < 2; i++)
                {
                    if (_kW > 0f && i != _kFoot) continue;
                    Vector3 instep = ankEff[i] + dir[i] * Instep;
                    err = Math.Min(err, Math.Abs((instep - f.BallPos).Length() - f.BallRadius));
                }
                Stats.Contact.Add(err);
                (f.StrikeEvent ? Stats.ContactStrike : Stats.ContactTouch).Add(err);
                LastContactError = err;
                if (_kW <= 0f) GD.Print($"FOOT_NOKICK {StatsName} err={err:F3}");
            }
        }
    }

    /// <summary>BODY_POSE's mean running lean at a jog and a sprint (steady speed only).</summary>
    void LeanStats(bool running0, float speed, float lean, bool close)
    {
        bool steady = running0 && Math.Abs(_accFS) < 1f;
        if (steady && close) { Stats!.LeanCloseSum += lean * 180f / Mathf.Pi; Stats.LeanCloseN++; }
        if (steady && speed >= 4f && speed < 5.8f) { Stats!.LeanJogSum += lean * 180f / Mathf.Pi; Stats.LeanJogN++; }
        if (steady && speed >= 6.8f) { Stats!.LeanSprintSum += lean * 180f / Mathf.Pi; Stats.LeanSprintN++; }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // touches

    // per-frame foot jump: each foot's ankle and boot tip on the last two ticks, how many ticks of history there are, and the exit-touch watch after a move
    readonly Vector3[,] _jAnk = new Vector3[2, 2], _jToe = new Vector3[2, 2];
    int _jN, _jExitId; bool _jWasSkill, _jExit; float _jExitT;

    /// <summary>
    /// Records each foot's world move since the previous tick (and since two ticks before) under the context of this tick: a skill move, the exit
    /// after one (from the move's end until the first touch made after it has recovered), a shot, a plain touch, or anything else.
    /// </summary>
    void MeasureJump(in FigureFrame f, Vector3[] ank, Vector3[] dir, float[] tw)
    {
        float dt = f.Dt > 0 ? f.Dt : 1f / 120f;
        Stats!.JumpDt = dt;
        bool shot = f.Shot.Valid || _strikeT >= 0f || !float.IsNaN(f.StrikeEta) || f.ShotCharge > 0.001f;
        if (f.Skill.Valid) { _jWasSkill = true; _jExit = false; }
        else if (_jWasSkill) { _jWasSkill = false; _jExit = true; _jExitId = int.MinValue; _jExitT = 0f; }
        if (_jExit && !f.Skill.Valid)
        {
            _jExitT += dt;
            if (_jExitId == int.MinValue && f.Action.Valid && !f.Action.IsSkill && f.Action.Phase is TouchPhase.Windup or TouchPhase.Contact or TouchPhase.Recovery)
                _jExitId = f.Action.Id;
            if (_jExitId != int.MinValue)
            {
                bool live = (f.Action.Valid && f.Action.Id == _jExitId && f.Action.Phase != TouchPhase.Done)
                    || (f.PrevAction.Valid && f.PrevAction.Id == _jExitId && f.PrevAction.Phase != TouchPhase.Done);
                if (!live) _jExit = false;
            }
            if (_jExitT > 1.5f || shot) _jExit = false;
        }
        var c = f.Skill.Valid ? FigureStats.JumpContext.Skill : shot ? FigureStats.JumpContext.Shot : _jExit ? FigureStats.JumpContext.Exit
            : tw[0] > 0f || tw[1] > 0f ? FigureStats.JumpContext.Touch : FigureStats.JumpContext.Other;
        for (int i = 0; i < 2; i++)
        {
            Vector3 fd = dir[i]; fd.Y = 0; fd = fd.LengthSquared() > 1e-6f ? fd.Normalized() : Vector3.Right;
            Vector3 toe = _shown ? _shownToe[i] : ank[i] + fd * (0.8f * FootLen);
            if (_jN >= 1)
            {
                float m = Math.Max((ank[i] - _jAnk[i, 0]).Length(), (toe - _jToe[i, 0]).Length());
                float m60 = _jN >= 2 ? Math.Max((ank[i] - _jAnk[i, 1]).Length(), (toe - _jToe[i, 1]).Length()) : float.NaN;
                // the change of velocity over one tick (ankle or boot tip, whichever is larger), m/s per tick
                float dv = _jN >= 2 ? Math.Max(((ank[i] - _jAnk[i, 0]) - (_jAnk[i, 0] - _jAnk[i, 1])).Length(), ((toe - _jToe[i, 0]) - (_jToe[i, 0] - _jToe[i, 1])).Length()) / dt : float.NaN;
                float limit = _atkAnim != null ? 0.10f + 1.2f * new Vector2(f.Velocity.X, f.Velocity.Z).Length() * dt : 0.10f;
                Stats.AddJump(c, i, m, m60, limit, dv);
                if (MocapLogOn && (m > 0.09f || dv > 6f)) GD.Print($"MOCAP_JUMP tick={Stats.Tick} foot={i} ctx={c} m={m:F3} dv={dv:F2} ank={(ank[i] - _jAnk[i, 0]).Length():F3} toe={(toe - _jToe[i, 0]).Length():F3} tw={tw[i]:F2} w={_mW:F2} act={(f.Action.Valid ? $"{f.Action.Phase}/{(f.Action.Right ? "R" : "L")}/{f.Action.Surface}/eta{f.Action.ContactEta:F3}/made{(f.Action.Made ? 1 : 0)}/id{f.Action.Id}/cp({f.Action.ContactPoint.X:F2},{f.Action.ContactPoint.Z:F2})/tf{_tf[i].Surf}{_tf[i].SurfId}" : "-")} prev={(f.PrevAction.Valid ? $"{f.PrevAction.Phase}/{(f.PrevAction.Right ? "R" : "L")}/eta{f.PrevAction.ContactEta:F3}" : "-")} up={(f.Upcoming.Valid ? "1" : "0")} next={f.NextTouchEta:F3}{(f.NextTouchRight ? "R" : "L")} pitch={_pitch[i]:F2} yaw={Mathf.Atan2(dir[i].Z, dir[i].X):F2}");
            }
            _jAnk[i, 1] = _jAnk[i, 0]; _jToe[i, 1] = _jToe[i, 0];
            _jAnk[i, 0] = ank[i]; _jToe[i, 0] = toe;
        }
        _jN = Math.Min(_jN + 1, 2);
    }

    // stride measurement: the previous plant's tick and place, and whether the body was running then
    int _stepTick = -1; Vector3 _stepAt; bool _stepRunning;

    void MeasureStep(in FigureFrame f, Vector3 plant, float speed)
    {
        if (IsKeeper || Stats == null) return;
        // (in mocap mode only steps drawn wholly by one body count: a step across a procedural / cycle hand-over mixes two gaits' timing)
        bool run = speed > 2f && !_seq && _strikeT < 0f && !f.Skill.Valid && !f.Shot.Valid && f.ShotCharge <= 0.001f && (_mW <= 0f || _mW >= 0.99f);
        if (run && _stepRunning && _stepTick >= 0 && Stats.Tick > _stepTick)
        {
            Stats.StepTime.Add((Stats.Tick - _stepTick) * Stats.JumpDt);
            if (MocapLogOn) GD.Print($"MOCAP_STEP tick={Stats.Tick} dt={(Stats.Tick - _stepTick)} len={new Vector2(plant.X - _stepAt.X, plant.Z - _stepAt.Z).Length():F3} s={f.GaitPhase:F3} v={speed:F2}");
            Stats.StepLen.Add(new Vector2(plant.X - _stepAt.X, plant.Z - _stepAt.Z).Length());
        }
        _stepTick = Stats.Tick; _stepAt = plant; _stepRunning = run;
    }

    /// <summary>The boot's contact surface point relative to the ankle joint in the foot's frame (forward, up, the character's right), metres.</summary>
    Vector3 Off(int foot, BootSurface surface)
    {
        if (_body != null && _body.HasSurfaces) return _body.SurfaceOffset(foot, surface, H, AnkleH);
        float k = H / 1.8f, sg = foot == 1 ? 1f : -1f;
        return k * surface switch
        {
            BootSurface.Laces => new Vector3(0.172f, 0.027f, 0f),
            BootSurface.Inside => new Vector3(0.122f, -0.016f, -sg * 0.065f),
            BootSurface.Outside => new Vector3(0.122f, -0.016f, sg * 0.065f),
            _ => new Vector3(0.122f, -0.063f, 0f),
        };
    }

    /// <summary>The foot direction that turns the chosen surface toward the ball-out direction <paramref name="d"/> (the toes point along it for laces and sole, sideways for the foot's sides).</summary>
    static Vector3 SurfaceDir(BootSurface surface, int foot, Vector3 d)
    {
        bool right = foot == 1;
        return surface switch
        {
            BootSurface.Inside => right ? new Vector3(-d.Z, 0, d.X) : new Vector3(d.Z, 0, -d.X),
            BootSurface.Outside => right ? new Vector3(d.Z, 0, -d.X) : new Vector3(-d.Z, 0, d.X),
            _ => d,
        };
    }

    /// <summary>
    /// Shapes foot <paramref name="i"/>'s swing around the simulation's touch: the boot's contact surface meets the action's contact point on the
    /// contact tick, arriving from behind along the ball-out direction, then follows through along it and hands back to the stride. Returns the
    /// weight of the touch pose (0 when this foot plays no touch now).
    /// </summary>
    float TouchOverlay(in FigureFrame f, int i, Vector3 pelXZ, Vector3 hv, Vector3 fm, float k, ref Vector3 ank, ref Vector3 dirv, out BootSurface surf)
    {
        surf = BootSurface.Laces;
        if (IsKeeper) return 0f;
        float best = 0f;
        float r = f.BallRadius;
        _ovWind[i] = false;
        float upW = 0f;
        (int Id, float Tau) handover = (int.MinValue, 0f);   // a touch that takes over from the early hint this tick
        int windId = int.MinValue; float windTau = 0f;        // the unmade touch drawn this tick
        for (int n = 0; n < 3; n++)
        {
            // the pending or latest touch, the one before it still following through, and the immediate touch the core is about to make
            FigureAction a = n == 0 ? f.Action : n == 1 ? f.PrevAction : f.Upcoming;
            if (!a.Valid || a.IsSkill || (a.Right ? 1 : 0) != i) continue;
            // the shot's soft touch is not drawn: the core makes it on the press tick (no windup to draw), and it only keeps the ball
            // rolling with him; the shot's own sequence plays the ball
            if (a.Kind == SoftTouchKind) continue;
            // the foot starts toward a pending touch a little before the core's windup (TouchLead), so it need not cover the way from the stride's
            // swing to the contact zone in the windup alone
            if (a.Phase is TouchPhase.Planned or TouchPhase.Done || (a.Phase == TouchPhase.PreWindup && !(n == 0 && !a.Made && a.ContactEta <= TouchLead))) continue;
            bool ready = n == 2 && f.UpcomingReady;
            var tf = n == 2 ? _tfUp[i] : _tf[i];
            float tau = a.ContactEta;
            Vector3 centre = a.ContactPoint;

            // the direction the ball leaves in: the launch once made, else the push toward the planned next meet, else the run direction
            Vector3 d = new(a.BallOutVelocity.X, 0, a.BallOutVelocity.Z);
            if (a.Made && d.LengthSquared() > 0.04f) d = d.Normalized();
            else
            {
                Vector3 np = new(a.NextMeetPoint.X - centre.X, 0, a.NextMeetPoint.Z - centre.Z);
                d = !float.IsNaN(a.NextMeetPoint.X) && np.LengthSquared() > 0.09f ? np.Normalized() : fm;
            }
            // the drawn direction follows the wanted one at a limited rate within one touch (a new touch starts from its own)
            d = DrawnOutDir(i, a.Id, d, f.Dt);
            // until the contact the foot is shaped for the surface the windup began with; the contact itself uses the touch's own surface
            BootSurface sf = a.Surface;
            if (!a.Made) { if (tf.SurfId != a.Id) { tf.SurfId = a.Id; tf.Surf = a.Surface; } sf = tf.Surf; }
            Vector3 fd = SurfaceDir(sf, i, d);
            Vector3 rv = new(-fd.Z, 0, fd.X);
            Vector3 off = Off(i, sf);
            Vector3 p = sf == BootSurface.Sole ? centre + Vector3.Up * r : centre - d * r;   // the boot's surface point at the contact
            Vector3 c0 = p - (fd * off.X + Vector3.Up * off.Y + rv * off.Z);                        // the ankle that puts it there

            float wgt; Vector3 target; Vector3 fdUse = fd;
            if (ready)
            {
                // a still ball at the feet of a standing player: the foot that will play it rests just behind it, ready for the stick
                if (tf.Id != a.Id) { tf.Id = a.Id; tf.Span = _teleporting ? 1f : 0f; }
                tf.Span = Math.Min(1f, tf.Span + f.Dt / 0.3f);
                wgt = Smooth(tf.Span);
                target = c0 - d * (0.04f * k);
                target.Y = Math.Max(AnkleH, c0.Y - 0.05f * k);
            }
            else if (!a.Made && tau > 0f)
            {
                // windup: the foot comes through from behind the contact point, carried with the body
                // a touch first seen while the early hint drew this foot carries its approach on (the same weight curve), else it starts its own
                if (tf.Id != a.Id) { tf.Id = a.Id; tf.Span = _hintPrev[i] ? Math.Max(tau, TouchLead) : Math.Max(tau, 0.04f); if (_hintPrev[i]) handover = (a.Id, tau); }
                wgt = Smooth(1f - tau / tf.Span);
                target = c0 - hv * tau - d * (0.12f * k * Smooth(tau / tf.Span));
                target = WindupTargetJump(tf, a.Id, target, pelXZ, tau);
            }
            else
            {
                // contact tick and recovery: the foot follows through along the ball-out direction (relative to the body) and the stride takes over.
                // The windup id is left alone: the touch planned on an immediate touch's contact tick carries the same id and needs its own windup.
                if (tf.ContactId != a.Id || tf.ContactTick != a.ContactTick)
                {
                    tf.ContactId = a.Id; tf.ContactTick = a.ContactTick;
                    tf.RelC = c0 - pelXZ; tf.Psi = Mathf.Atan2(fd.Z, fd.X);
                }
                float s = Math.Max(0f, -tau);
                float rec = Math.Max(a.RecoverySeconds, 0.06f);
                wgt = s <= 0.02f ? 1f : 1f - Smooth((s - 0.02f) / (rec - 0.02f));
                target = pelXZ + tf.RelC + d * (0.25f * k * Smooth(s / 0.08f));
                target.Y = tf.RelC.Y + 0.10f * k * Smooth(s / 0.10f);
                fdUse = new Vector3(Mathf.Cos(tf.Psi), 0, Mathf.Sin(tf.Psi));
            }
            // a predicted immediate touch (re)appearing is eased in, as the hint is: the prediction comes and goes with the stick
            // (but always fast enough to be all there by its contact)
            if (n == 2 && !_teleporting)
            {
                float rise = Math.Max(f.Dt / HintRise, (1f - _upW[i]) * f.Dt / Math.Max(tau, f.Dt));
                wgt = Math.Min(wgt, _upW[i] + rise); upW = Math.Max(upW, wgt);
            }
            if (wgt <= best) continue;
            best = wgt; surf = a.Surface;
            _ovWind[i] = !a.Made;
            if (!a.Made && tau > 0f) { windId = a.Id; windTau = tau; } else windId = int.MinValue;
            Vector3 cur = dirv;
            float yaw = Mathf.LerpAngle(Mathf.Atan2(cur.Z, cur.X), Mathf.Atan2(fdUse.Z, fdUse.X), wgt);
            ank = ank.Lerp(target, wgt);
            dirv = new Vector3(Mathf.Cos(yaw), 0, Mathf.Sin(yaw));
        }
        // Early hint: no touch of this foot is under way yet, but the core expects this foot's next contact within TouchLead; the foot starts
        // toward where the ball will be (inside of the foot, along the run) on the same curve a windup uses, so a touch committed only one
        // windup before its contact continues the approach instead of starting it. Never a commitment: if no touch comes, the drop fade takes it back.
        bool hint = false;
        if (best <= 0f && f.InPossession && !f.Skill.Valid && !f.Shot.Valid && (f.NextTouchRight ? 1 : 0) == i
            && f.NextTouchEta > 0f && f.NextTouchEta <= TouchLead && !float.IsNaN(f.NextTouchPoint.X))
        {
            float eta = f.NextTouchEta;
            Vector3 centre = new(f.NextTouchPoint.X, r, f.NextTouchPoint.Z);
            Vector3 d = fm, fd = SurfaceDir(BootSurface.Inside, i, d), rv = new(-fd.Z, 0, fd.X);
            Vector3 off = Off(i, BootSurface.Inside);
            Vector3 c0 = centre - d * r - (fd * off.X + Vector3.Up * off.Y + rv * off.Z);
            // (rising no faster than over HintRise: the core's forecast can jump inside TouchLead at once)
            float wgt = Smooth(1f - eta / TouchLead);
            if (!_teleporting) wgt = Math.Min(wgt, _hintW[i] + f.Dt / HintRise);
            Vector3 target = c0 - hv * eta - d * (0.12f * k * Smooth(eta / TouchLead));
            float yaw = Mathf.LerpAngle(Mathf.Atan2(dirv.Z, dirv.X), Mathf.Atan2(fd.Z, fd.X), wgt);
            ank = ank.Lerp(target, wgt);
            dirv = new Vector3(Mathf.Cos(yaw), 0, Mathf.Sin(yaw));
            best = wgt; surf = BootSurface.Inside; _ovWind[i] = true; hint = wgt > 0f;
        }
        // Where the touch took over from the hint, what the hint drew (carried on at its last velocity) and what the touch draws differ by the
        // guess's error (place, surface): that difference is kept and taken out by the contact, so the foot bends its path instead of jumping.
        if (handover.Id != int.MinValue && handover.Id == windId)
        {
            _hoId[i] = windId; _hoTau0[i] = Math.Max(windTau, 1e-3f);
            _hoOff[i] = _ovAnk[i, 0] + (_ovAnk[i, 0] - _ovAnk[i, 1]) - ank;
            _hoYaw[i] = Mathf.Wrap(_ovYaw[i] - Mathf.Atan2(dirv.Z, dirv.X), -Mathf.Pi, Mathf.Pi);
        }
        if (_hoId[i] != int.MinValue)
        {
            if (windId != _hoId[i]) _hoId[i] = int.MinValue;
            else
            {
                float u = Smooth(windTau / _hoTau0[i]);
                ank += _hoOff[i] * u;
                float yaw = Mathf.Atan2(dirv.Z, dirv.X) + _hoYaw[i] * u;
                dirv = new Vector3(Mathf.Cos(yaw), 0, Mathf.Sin(yaw));
            }
        }
        _ovAnk[i, 1] = _ovAnk[i, 0]; _ovAnk[i, 0] = ank; _ovYaw[i] = Mathf.Atan2(dirv.Z, dirv.X);
        _hintPrev[i] = hint; _hintW[i] = hint ? best : 0f; _upW[i] = upW;
        return best;
    }

    /// <summary>The windup target with a jump of it (more than <see cref="WindupJumpM"/> off its last step) spread over the time left to the contact.</summary>
    Vector3 WindupTargetJump(TouchFoot tf, int id, Vector3 target, Vector3 pelXZ, float tau)
    {
        Vector3 rel = target - pelXZ;
        if (tf.TgtId != id || _teleporting) { tf.TgtId = id; tf.TgtRel = rel; tf.TgtStep = Vector3.Zero; tf.TgtOff = Vector3.Zero; tf.TgtTau0 = tau; return target; }
        Vector3 step = rel - tf.TgtRel;
        if ((step - tf.TgtStep).Length() > WindupJumpM)
        {
            // the new point is taken up from where the target was going: the jump becomes an offset dying away by the contact
            Vector3 jump = step - tf.TgtStep;
            float left = tf.TgtOff.LengthSquared() > 0f ? Smooth(tau / Math.Max(tf.TgtTau0, 1e-3f)) : 0f;
            tf.TgtOff = tf.TgtOff * left - jump;
            tf.TgtTau0 = Math.Max(tau, 1e-3f);
            step = tf.TgtStep;
        }
        tf.TgtStep = step; tf.TgtRel = rel;
        if (tf.TgtOff.LengthSquared() <= 0f) return target;
        return target + tf.TgtOff * Mathf.Clamp(tau / tf.TgtTau0, 0f, 1f);
    }
    /// <summary>A windup target's change of step (metres per tick) above which it counts as a jump.</summary>
    const float WindupJumpM = 0.03f;

    readonly float[] _hintW = new float[2], _upW = new float[2];
    const float HintRise = 0.08f;

    /// <summary>Per foot: last tick drew the early touch hint (see TouchOverlay).</summary>
    readonly bool[] _hintPrev = new bool[2];
    /// <summary>Per foot: the hint-to-touch handover being taken out (touch id, eta when it began, place and yaw difference) and the last two overlay ankles and yaw.</summary>
    readonly int[] _hoId = { int.MinValue, int.MinValue }; readonly float[] _hoTau0 = new float[2], _hoYaw = new float[2], _ovYaw = new float[2];
    readonly Vector3[] _hoOff = new Vector3[2]; readonly Vector3[,] _ovAnk = new Vector3[2, 2];

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
            ft.TFrom = ft.Planted ? ft.Plant : ft.Ankle; ft.Planted = false;
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
            else if (ft.Tracking)
            {
                (IsKeeper ? Stats?.SlideKeeper : Stats?.Slide)?.Add(ft.TrackMax); ft.Tracking = false;
                if (MocapLogOn && ft.TrackMax > 0.015f) GD.Print($"MOCAP_SLIDE tick={Stats?.Tick} foot={i} max={ft.TrackMax:F3} from=({ft.TrackStart.X:F3},{ft.TrackStart.Y:F3},{ft.TrackStart.Z:F3}) to=({ankle[i].X:F3},{ankle[i].Y:F3},{ankle[i].Z:F3})");
            }
        }
    }

    /// <summary>Closes any stance still being tracked (call before reading the stats).</summary>
    public void FlushStats()
    {
        foreach (var ft in _feet) if (ft.Tracking) { (IsKeeper ? Stats?.SlideKeeper : Stats?.Slide)?.Add(ft.TrackMax); ft.Tracking = false; }
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
        _skActive = false; _skPelOff = Vector3.Zero; _skShoulderOff = Vector3.Zero;
        float k = H / 1.8f;
        if (!IsKeeper && f.Skill.Valid)
        {
            // a skill move owns the legs and body until it ends; a shot sequence under way is over (the core dropped it)
            _strikeT = -1f; _stActive = false; _windT = 0f; _lastCharge = 0f;
            if (PlanSkill(f, pos, fwd, right, k)) return;
        }
        _skId = int.MinValue;
        if (ShotLog && Stats != null && (f.Shot.Valid || !float.IsNaN(f.StrikeEta)))
            GD.Print($"SHOT_LOG tick={Stats.Tick} el={f.Shot.Elapsed:F3} eta={f.Shot.ContactEta:F3} seta={f.StrikeEta:F3} sf={(f.StrikeRight ? "R" : "L")} foot={(f.Shot.Right ? "R" : "L")} kind={f.Shot.Kind} held={(f.Shot.Held ? 1 : 0)} made={(f.Shot.Made ? 1 : 0)} ch={f.ShotCharge:F2} d={Dist2(f.BallPos, pos):F3} v={new Vector2(f.Velocity.X, f.Velocity.Z).Length():F2} step={(f.IsStepping ? 1 : 0)} sw={(f.SwingRight ? "R" : "L")}/{f.SwingProgress:F2} act={(f.Action.Valid ? $"{f.Action.Phase}/{(f.Action.Right ? "R" : "L")}/eta{f.Action.ContactEta:F3}" : "-")} ev={(f.TouchEvent ? "T" : "")}{(f.StrikeEvent ? "S" : "")} mW={_mW:F2}");
        float r = f.BallRadius;
        Vector3 posXZ = new(pos.X, 0, pos.Z);
        Vector3 ballXZ = new(f.BallPos.X, 0, f.BallPos.Z);
        // a shot action owns the striking leg and the body from the press to the end of its recovery
        if (!IsKeeper && f.Shot.Valid) { PlanShot(f, pos, fwd, right, k, hmax); return; }
        ShotIdle(dt);
        _shId = int.MinValue;
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
            _stActive = false;
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
            if (_strikeT >= T) { _strikeT = -1f; _seq = false; }
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
    }


    // ---------------------------------------------------------------------------------------------------------------
    // shots

    /// <summary>The ball-surface angle each shot is struck at, radians below the ball's centre (negative = above it).</summary>
    static float StrikeAngle(ShotKind k) => k switch { ShotKind.Driven => -0.30f, ShotKind.Chip => 0.45f, ShotKind.Power => 0.12f, _ => 0.05f };

    /// <summary>
    /// Poses the shot action, timed backwards from its contact (the core's estimate, which may slip a step later until the commit):
    /// until the strike sequence's length before it the run goes on untouched; then the striking foot is drawn back and swings
    /// through to meet the ball with the action's surface at its contact point on the contact tick, and follows through during the
    /// recovery. The other foot is the plant: the stride's own stance foot when the stride has it down at the strike, else a step
    /// to beside the ball held where it lands (see <see cref="PlanPlant"/>). Nothing here decides when the ball is struck.
    /// </summary>
    void PlanShot(in FigureFrame f, Vector3 pos, Vector3 fwd, Vector3 right, float k, float hmax)
    {
        var sh = f.Shot;
        int fi = sh.Right ? 1 : 0;
        float side = fi == 1 ? 1f : -1f;
        float r = f.BallRadius;
        float dt = f.Dt > 0f ? f.Dt : 1f / 120f;
        Vector3 posXZ = new(pos.X, 0, pos.Z);
        if (sh.Id != _shId)
        {
            _shId = sh.Id; _shCharge = 0f; _shContactSet = false; _shReleased = false;
            _shFromRel = _prevAnkRel[fi];
            ShotSequenceReset(fwd);
            _shRelPrevOk = false; _shTfSet = false; _shVRel = Vector3.Zero;
        }
        _shCharge = Math.Max(_shCharge, f.ShotCharge);
        float cb = Mathf.Clamp(_shCharge, 0f, 1f);
        float tau = sh.Made ? sh.ContactEta : Math.Max(sh.ContactEta, 0f);
        float s = sh.Made ? Math.Max(0f, -tau) : 0f;     // seconds since the contact
        float rec = Math.Max(sh.RecoverySeconds, 0.08f);
        bool inside = sh.Kind is ShotKind.Finesse or ShotKind.LowFinesse or ShotKind.Chip;

        // the sequence's length and the swing's share of it; a later estimate before the contact eases the sequence out (ShotSlip)
        float len = SequenceSeconds(sh.Kind, cb);
        float swingT = Math.Min(sh.Kind switch { ShotKind.Driven => 0.12f, ShotKind.Chip => 0.14f, ShotKind.Power => 0.14f, _ => 0.17f }, 0.6f * len);
        // the plant step (when one is drawn) starts a stride step before it lands, so it is as long as a step of the run
        float plantLen = Math.Max(len, PlantLand(swingT) + 1f / Math.Max(f.StepFrequency, 2.5f));
        // (the estimate is only drawn toward while the ball will be within the body's reach at the contact)
        Vector3 bodyAtC = posXZ + new Vector3(f.Velocity.X, 0, f.Velocity.Z) * tau;
        bool inRange = Dist2(sh.ContactPoint, bodyAtC) < ShotReachM * k;
        float tauD = sh.Made ? tau : ShotSlip(tau, plantLen, swingT, dt, inRange);

        // the direction the ball leaves in: until the contact the drawn estimate (the run, turned toward the aim), kept for the contact
        // tick itself (the boot surface is on the ball whichever way it faces); the follow-through turns to the launch
        Vector3 launch = new(sh.BallOutVelocity.X, 0, sh.BallOutVelocity.Z);
        launch = sh.Made && launch.LengthSquared() > 0.04f ? launch.Normalized() : _shDirPre;
        if (!sh.Made) _shDirPre = AimEstimate(f, fwd);
        Vector3 d = _shDirPre;
        Vector3 fd = SurfaceDir(sh.Surface, fi, d);
        Vector3 rv = new(-fd.Z, 0, fd.X);
        Vector3 off = Off(fi, sh.Surface);
        // the boot's surface point on the ball: behind it along the leave direction, higher or lower on it per kind
        float th = StrikeAngle(sh.Kind);
        // (the core's estimate rolls the ball on to the contact tick, but the strike meets it before that tick's roll: one tick less)
        Vector3 centre = sh.ContactPoint;
        if (!sh.Made && tau >= dt) centre -= new Vector3(f.BallVel.X, 0, f.BallVel.Z) * dt;
        Vector3 p = centre - d * (r * Mathf.Cos(th)) - Vector3.Up * (r * Mathf.Sin(th));
        Vector3 c0 = p - (fd * off.X + Vector3.Up * off.Y + rv * off.Z);

        // wind-up pose: the foot drawn back behind the hip and lifted; a finesse foot also goes out to its side, a chip stays low
        float b = 0.35f + 0.65f * cb;
        float back = sh.Kind switch { ShotKind.Chip => 0.10f + 0.12f * b, ShotKind.Driven => 0.14f + 0.26f * b, ShotKind.Power => 0.16f + 0.30f * b, _ => 0.12f + 0.20f * b };
        float lift = sh.Kind switch { ShotKind.Chip => 0.10f + 0.12f * b, ShotKind.Driven => 0.14f + 0.30f * b, ShotKind.Power => 0.22f + 0.50f * b, _ => 0.14f + 0.28f * b };
        float outw = inside && sh.Kind != ShotKind.Chip ? 0.14f + 0.10f * b : 0f;
        Vector3 wind = posXZ + right * (side * (HipW + outw * k)) - fwd * (back * k);
        wind.Y = AnkleH + lift * k;

        Vector3 ankle; Vector3 dirUse = fwd;
        float seqW;
        if (!sh.Made)
        {
            // the backswing takes the foot from wherever the stride has it to the wind-up pose by the swing's start, then the swing
            // brings it through to the contact pose like a pendulum (down first, then forward, accelerating), exactly there on the contact tick
            seqW = Smooth((len - tauD) / Math.Max(len - swingT, 0.02f)) * _sqGate;
            float sw = Mathf.Clamp(1f - tauD / swingT, 0f, 1f);
            float hz = sw * sw, vt = Smooth(sw);
            ankle = new Vector3(Mathf.Lerp(wind.X, c0.X, hz), Mathf.Lerp(wind.Y, c0.Y, vt), Mathf.Lerp(wind.Z, c0.Z, hz));
            dirUse = fwd.Lerp(fd, sw).Normalized();
            // the foot's velocity relative to the body, which the follow-through carries on from the contact
            Vector3 rel = ankle - posXZ;
            _shVRel = _shRelPrevOk ? (rel - _shRelPrev) / dt : Vector3.Zero;
            _shRelPrev = rel; _shRelPrevOk = true;
        }
        else
        {
            // the contact tick and the recovery: the foot follows through from where it met the ball, relative to the body
            if (!_shContactSet) { _shContactSet = true; _shRelC = c0 - posXZ; _shPsiC = Mathf.Atan2(fd.Z, fd.X); }
            var (flen, up, across, tf) = sh.Kind switch
            {
                ShotKind.Driven => (0.45f, 0.08f, 0f, 0.08f),
                ShotKind.Chip => (0.26f, 0.26f, 0f, 0.09f),
                ShotKind.Finesse => (0.34f, 0.16f, 0.26f, 0.14f),
                ShotKind.LowFinesse => (0.42f, 0.04f, 0.22f, 0.12f),
                _ => (0.42f + 0.15f * cb, 0.30f + 0.18f * cb, 0f, 0.14f),
            };
            // the foot leaves the ball at the swing's own speed (relative to the body) and slows as the leg runs out: a cubic from the
            // contact with that velocity to the follow-through's end at rest, over the time that speed gives it (no faster than the kind's)
            float xd = Mathf.Clamp(s / tf, 0f, 1f);
            Vector3 dl = d.Lerp(launch, Smooth(xd)).Normalized();
            Vector3 end = dl * (flen * k) - right * (side * across * k) + Vector3.Up * (up * k);
            if (!_shTfSet)
            {
                _shTfSet = true;
                float ve = _shVRel.Dot(end.Normalized());
                _shTf = ve > 0.5f ? Mathf.Clamp(1.5f * end.Length() / ve, 0.05f, 1.5f * tf) : tf;
                _shV0 = _shVRel * _shTf;
                if (_shV0.Dot(end.Normalized()) > 1.5f * end.Length()) _shV0 *= 1.5f * end.Length() / _shV0.Dot(end.Normalized());
            }
            float x = Mathf.Clamp(s / _shTf, 0f, 1f);
            ankle = posXZ + _shRelC + _shV0 * (x * x * x - 2f * x * x + x) + end * (3f * x * x - 2f * x * x * x);
            ankle.Y = Math.Max(ankle.Y, AnkleH);
            Vector3 fdl = SurfaceDir(sh.Surface, fi, dl);
            float psi = Mathf.LerpAngle(_shPsiC, Mathf.Atan2(fdl.Z, fdl.X), Smooth(x));
            dirUse = new Vector3(Mathf.Cos(psi), 0, Mathf.Sin(psi));
            seqW = 1f - Smooth((s - 0.45f * rec) / (0.55f * rec));
        }
        _kFoot = fi; _kDir = dirUse; _kAnkle = ankle;
        _kW = seqW;
        _seq = _kW > 0f;

        PlanPlant(f, pos, fwd, right, k, hmax, fi, side, centre, tau, tauD, s, plantLen, swingT);

        // body: over the ball for a driven shot, opened toward the target for a finesse; from the backswing on
        float wind01 = sh.Made ? 1f - Smooth(s / 0.06f) : seqW;
        float fol = !sh.Made ? 0f : 1f - Smooth((s - 0.05f) / (rec - 0.05f));
        float fwdLean = sh.Kind switch { ShotKind.Driven => 0.26f, ShotKind.Power => 0.18f, ShotKind.Chip => 0.08f, _ => 0.10f };
        _leanExtra = (fwdLean + 0.05f * b) * fol;   // no backward lean here: the shoulders' offset shows the wind-up and the decel lean may already be at its limit
        float open = inside && sh.Kind != ShotKind.Chip ? 1f : 0f;
        float bodyW = Math.Max(wind01 * (!sh.Made ? 1f : 0f), fol);
        _skShoulderOff = right * (side * 0.12f * k * open * bodyW) - fwd * (0.05f * k * open * bodyW)
            + (sh.Kind == ShotKind.Power ? -fwd * (0.04f * k * b * wind01) : Vector3.Zero);
        _skPelOff = right * (side * 0.04f * k * open * bodyW);
        // (eased: the follow-through's share starts whole on the contact tick)
        _shUpper = Mathf.Lerp(_shUpper, Math.Max(0.6f * wind01, fol), 1f - Mathf.Exp(-dt / 0.06f));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // skill moves

    /// <summary>
    /// Poses the legs and body for a skill move from its elapsed time. The foot starts from where it was (so nothing pops), is drawn
    /// to the move's pose, and hands back to the stride over the last part of the move. The ball is read live, so a foot on the
    /// ball stays on it while the core rolls it.
    /// </summary>
    bool PlanSkill(in FigureFrame f, Vector3 pos, Vector3 fwd, Vector3 right, float k)
    {
        var s = f.Skill;
        int fi = s.Right ? 1 : 0;
        bool sole = s.Kind is SkillKind.DragBack or SkillKind.BallRoll or SkillKind.FakeStop;
        // a step-over or feint's close-in before its own motion is ordinary running
        if (!sole && s.ExecElapsed < 0f) return false;
        Vector3 posXZ = new(pos.X, 0, pos.Z);
        if (s.Id != _skId)
        {
            _skId = s.Id; _skFromRel = _prevAnkRel[fi]; _skSoleLogged = _skInLogged = false;
            SkillStartFromProcedural(fi, posXZ);
        }
        Vector3 from = posXZ + new Vector3(_skFromRel.X, 0, _skFromRel.Z); from.Y = _skFromRel.Y;
        float D = Math.Max(s.ExecDuration, 0.06f), t = Mathf.Clamp(s.ExecElapsed, 0f, D);
        float u = t / D, sd = s.Side;
        float inT = s.Kind is SkillKind.StepOver or SkillKind.BodyFeint ? Math.Min(0.10f, 0.4f * D) : s.Kind == SkillKind.FakeCut ? 0.05f : 0f;
        // the hand-back: a step-over or feint's foot comes back from beside the ball to the stride (or on to the exit touch) over a longer span
        float outT = Math.Min(sole ? 0.08f : s.Kind is SkillKind.StepOver or SkillKind.BodyFeint ? 0.11f : 0.07f, 0.5f * D);
        float inW = inT > 0f ? Smooth(t / inT) : 1f;
        float outW = 1f - Smooth((t - (D - outT)) / outT);
        float e = Mathf.Sin(Mathf.Pi * u);                  // 0 -> 1 -> 0 over the move
        float body = Smooth(t / 0.06f) * outW;              // the body's share of the pose
        float r = f.BallRadius;
        Vector3 ball = new(f.BallPos.X, 0, f.BallPos.Z);
        // during a sole move's reach the foot goes to where the ball will be at the landing (the core's prediction)
        bool reaching = sole && s.ContactElapsed < 0f && s.ReachSpan > 0f;
        float ur = reaching ? Mathf.Clamp(1f + s.ContactElapsed / s.ReachSpan, 0f, 1f) : 1f;
        if (reaching) ball = new Vector3(s.ContactPoint.X, 0, s.ContactPoint.Z);
        Vector3 target = from, fd = fwd;

        switch (s.Kind)
        {
            case SkillKind.StepOver:
            {
                // the foot circles over the ball from its inside to its outside: across the ball's top, a little ahead of its centre
                float lat = Mathf.Lerp(-0.20f, 0.28f, Smooth(u));
                target = ball + fwd * ((-0.04f + 0.13f * e) * k) + right * (sd * lat * k);
                target.Y = AnkleH + (0.25f * k) * Mathf.Pow(e, 0.7f);
                fd = (fwd * Mathf.Cos(0.5f * e) + right * sd * Mathf.Sin(0.5f * e)).Normalized();
                _skPelOff = right * (sd * 0.05f * k * e * body) + Vector3.Down * (0.02f * k * e * body);
                _skShoulderOff = right * (sd * 0.12f * k * e * body) - fwd * (0.03f * k * e * body);
                break;
            }
            case SkillKind.BodyFeint:
            {
                // hips and shoulders go toward the side and come back; the side foot makes a short drop step out to that side
                target = posXZ + right * (sd * (HipW * 0.9f + 0.20f * k * e)) + fwd * (0.06f * k * e);
                target.Y = AnkleH + 0.05f * k * Mathf.Sin(2f * Mathf.Pi * u) * (u < 0.5f ? 1f : 0.4f);
                target.Y = Math.Max(AnkleH, target.Y);
                _skPelOff = right * (sd * 0.11f * k * e * body) + Vector3.Down * (0.025f * k * e * body) - fwd * (0.03f * k * e * body);
                _skShoulderOff = right * (sd * 0.17f * k * e * body) - fwd * (0.04f * k * e * body);
                _leanExtra = -0.04f * e * body;
                break;
            }
            case SkillKind.BallRoll:
            {
                // the far foot's sole on top of the ball, drawing it across the body toward the side; toes turned to where it goes
                fd = (fwd * Mathf.Cos(0.6f) + right * sd * Mathf.Sin(0.6f)).Normalized();
                target = SoleAnkle(s, ball, fd, r, fi);
                _skPelOff = right * (sd * 0.04f * k * e * body) + Vector3.Down * (0.015f * k * e * body);
                _skShoulderOff = right * (sd * 0.08f * k * e * body);
                break;
            }
            case SkillKind.DragBack:
            case SkillKind.FakeStop:
            {
                // sole on top of the ball, pulling it back (drag-back) or stopping it (fake stop); the body rocks back
                target = SoleAnkle(s, ball, fwd, r, fi);
                float back = s.Kind == SkillKind.DragBack ? 1f : 0.5f;
                _skPelOff = -fwd * (0.05f * k * back * e * body) + Vector3.Down * (0.02f * k * e * body);
                _skShoulderOff = -fwd * (0.08f * k * back * e * body);
                _leanExtra = -0.03f * back * e * body;
                break;
            }
            default: // fake cut
            {
                // the boot hooks the ball toward the cut with the inside or outside, then follows through along the push
                Vector3 d = new(s.OutVelocity.X, 0, s.OutVelocity.Z);
                d = d.LengthSquared() > 0.04f ? d.Normalized() : fwd;
                fd = SurfaceDir(s.Surface, fi, d);
                Vector3 rv = new(-fd.Z, 0, fd.X);
                Vector3 off = Off(fi, s.Surface);
                Vector3 centre = new(s.ContactPoint.X, 0, s.ContactPoint.Z);
                Vector3 c0 = centre - d * r - (fd * off.X + rv * off.Z); c0.Y = r - off.Y;
                target = c0 + d * (0.22f * k * Smooth(t / 0.08f)); target.Y = c0.Y + 0.06f * k * Smooth(t / 0.08f);
                _skPelOff = d * (0.04f * k * e * body);
                _skShoulderOff = d * (0.07f * k * e * body);
                break;
            }
        }
        if (reaching)
        {
            // from where the foot was, up and over onto the ball: lifted on the way, arriving flat on top of it
            float rs = ur;   // linear: the reach can be only a few ticks long, and an eased one would peak at 1.5 times the mean pace
            Vector3 p = from.Lerp(target, rs);
            p.Y += 0.12f * k * Mathf.Sin(Mathf.Pi * rs);
            target = p;
            fd = (fwd * (1f - rs) + fd * rs).Normalized();
            _skPelOff = Vector3.Zero; _skShoulderOff = Vector3.Zero; _leanExtra = 0f;
            inW = 1f;
        }
        target.Y = Math.Max(target.Y, AnkleH);
        _kFoot = fi; _kDir = fd; _seq = true; _skActive = true;
        if (_skFirst) SkillStartVelocity(fi, from, target, reaching ? s.ReachSpan : 0f, f.Dt);
        _kAnkle = from.Lerp(target, inW) + SkillStartOffset(f.Dt);
        _kW = outW;
        return true;
    }

    /// <summary>The ankle that puts the foot's sole on the very top of the ball, toes along <paramref name="fd"/>.</summary>
    Vector3 SoleAnkle(in FigureSkill s, Vector3 ballXZ, Vector3 fd, float r, int foot)
    {
        Vector3 off = Off(foot, BootSurface.Sole);
        Vector3 rv = new(-fd.Z, 0, fd.X);
        Vector3 a = ballXZ - (fd * off.X + rv * off.Z);
        a.Y = 2f * r - off.Y;
        return a;
    }

    /// <summary>
    /// Prints how far the sole's (or the hooking surface's) point is from the ball surface at the move's first contact tick and once
    /// the foot has had time to arrive; the same distances feed the SKIN_CONTACT line when a skinned body is worn.
    /// </summary>
    void LogSkillFoot(in FigureFrame f, Vector3[] ank, Vector3[] dir)
    {
        var s = f.Skill;
        if (s.Kind is SkillKind.StepOver or SkillKind.BodyFeint) return;
        if (s.Kind != SkillKind.FakeCut && s.ContactElapsed >= 0f && Stats != null)
        {
            // sole on the ball from the landing until the hand-back at the end begins
            int fj = s.Right ? 1 : 0;
            Vector3 o = Off(fj, BootSurface.Sole);
            Vector3 d0 = dir[fj]; d0.Y = 0; d0 = d0.Normalized();
            Vector3 q = ank[fj] + d0 * o.X + Vector3.Up * o.Y + new Vector3(-d0.Z, 0, d0.X) * o.Z;
            float er = Math.Abs((q - f.BallPos).Length() - f.BallRadius);
            if (s.Elapsed < s.Duration - Math.Min(0.08f, 0.5f * Math.Max(s.ExecDuration, 0.06f))) Stats.SkillSoleMaxErr = Math.Max(Stats.SkillSoleMaxErr, er);
        }
        bool atContact = f.SkillTouch && !_skSoleLogged;
        bool arrived = s.ContactElapsed >= 0.04f && !_skInLogged;
        if (!atContact && !arrived) return;
        int fi = s.Right ? 1 : 0;
        Vector3 off = Off(fi, s.Surface);
        Vector3 fd = dir[fi]; fd.Y = 0; fd = fd.Normalized();
        Vector3 fr = new(-fd.Z, 0, fd.X);
        Vector3 pt = ank[fi] + fd * off.X + Vector3.Up * off.Y + fr * off.Z;
        float err = Math.Abs((pt - f.BallPos).Length() - f.BallRadius);
        if (atContact) _skSoleLogged = true;
        if (arrived) _skInLogged = true;
        GD.Print($"SKILL_FOOT kind={s.Kind} {(atContact ? "contact" : "arrived")} t={s.Elapsed:F3} err_m={err:F3}");
        if (atContact && Stats != null) Stats.SkillContact.Add(err);
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
        float k = H / 1.8f;
        // torso and pelvis
        Basis Pb = _split ? _pelB : B, Hb = _split ? _headB : B;
        _pelvis.Transform = new Transform3D(Pb.Scaled(Vector3.One * k), pel);
        Vector3 torsoC = pel + B * new Vector3(0, 0.035f * H + TorsoLen * 0.5f, 0);
        _torso.Transform = new Transform3D(B.Scaled(Vector3.One * k), torsoC);
        Vector3 neck = pel + B * new Vector3(0, 0.035f * H + TorsoLen + 0.02f * H, 0);
        Vector3 headC = neck + B.Y * (0.078f * H + 0.012f * H);
        SetSeg(_neck, pel + B.Y * (0.30f * H), pel + B.Y * (0.43f * H), FigureMeshes.NeckLen, k);
        _head.Transform = new Transform3D(Hb.Scaled(Vector3.One * k), headC);
        // number on the upper back: faces -forward (the label's +Z maps to -B.X)
        _number.Transform = new Transform3D(B * new Basis(Vector3.Up, -Mathf.Pi * 0.5f) * Basis.FromScale(Vector3.One * k), torsoC + B * (new Vector3(-0.158f, 0.07f, 0) * k));

        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? -1f : 1f;
            Vector3 hip = pel + Pb * new Vector3(0, _split ? 0f : -0.01f * H, side * HipW);   // the outfield figure's leg IK hip
            SetSeg(_thigh[i], hip, knee[i], FigureMeshes.RefLen, k);
            SetSeg(_shin[i], knee[i], ankle[i], FigureMeshes.RefLen, k);
            Vector3 fd = dir[i]; fd.Y = 0; fd = fd.LengthSquared() > 1e-6f ? fd.Normalized() : Vector3.Right;
            Vector3 fr = new(-fd.Z, 0, fd.X);
            float fp = _split ? _pitch[i] : 0f;
            Basis fb = new Basis(fd, Vector3.Up, fr);
            if (fp != 0f) fb = new Basis(fr, fp) * fb;
            Vector3 fc = fp != 0f ? ankle[i] + fb * new Vector3(0.4f * FootLen, -AnkleH + FootH * 0.5f, 0) : ankle[i] + fd * (0.4f * FootLen) + new Vector3(0, -AnkleH + FootH * 0.5f, 0);
            _foot[i].Transform = new Transform3D(fb.Scaled(Vector3.One * k), fc);

            Vector3 sh = shoulderC + B * new Vector3(0, 0, side * ShoulderW);
            SolveArm(sh, handT[i], elbowPole[i], out var elbow, out var hand);
            _shoulder[i].Transform = new Transform3D(Basis.FromScale(Vector3.One * k), sh);
            SetSeg(_upper[i], sh, elbow, FigureMeshes.RefArmUp, k);
            SetSeg(_fore[i], elbow, hand, FigureMeshes.RefArmLow, k);
            // hand: +Y along the forearm, palm facing the body (thickness along the body's lateral axis), thumb forward
            Vector3 hy = (hand - elbow); hy = hy.LengthSquared() > 1e-8f ? hy.Normalized() : Vector3.Down;
            Vector3 hz = B.Z - hy * B.Z.Dot(hy);
            hz = hz.LengthSquared() > 1e-6f ? hz.Normalized() : Vector3.Back;
            _hand[i].Transform = new Transform3D(new Basis(hy.Cross(hz), hy, hz).Scaled(Vector3.One * k), hand);

            _joints.Hip[i] = hip; _joints.Knee[i] = knee[i]; _joints.Ankle[i] = ankle[i]; _joints.FootDir[i] = fd;
            _joints.FootPitch[i] = fp; _joints.ToeBend[i] = _split ? _toeBend[i] : 0f;
            _joints.Shoulder[i] = sh; _joints.Elbow[i] = elbow; _joints.Hand[i] = hand;
        }
        _joints.Pelvis = pel; _joints.Torso = B; _joints.Height = H; _joints.AnkleHeight = AnkleH;
        _joints.SplitTrunk = _split; _joints.PelvisBasis = Pb; _joints.HeadBasis = Hb;
        if (_keeperAnim != null) _keeperAnim.Present(_joints, _animFrame);   // --keeper-anim mocap (PlayerFigure.Keeper.cs)
        else if (!MocapPresent()) _body?.Pose(_joints);
    }

    /// <summary>Stretches a limb mesh (centred on its midpoint, +Y distal, built for <paramref name="refLen"/> metres plus end caps) between two joints.</summary>
    static void SetSeg(MeshInstance3D m, Vector3 a, Vector3 b, float refLen, float k)
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
        float sy = len / (refLen * k);   // the mesh is in metres for a 1.8 m figure
        m.Transform = new Transform3D(new Basis(x * k, y * (k * sy), z * k), (a + b) * 0.5f);
    }
}
