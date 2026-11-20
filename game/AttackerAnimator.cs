using System;
using System.Collections.Generic;
using FootballSim.Anim;
using Godot;
using NQuat = System.Numerics.Quaternion;
using NVec3 = System.Numerics.Vector3;

namespace FootballSim.Game;

/// <summary>
/// --attacker-anim mocap: the attacker's running body from motion-captured stride cycles (game/assets/anim/attacker/cycles, made by
/// tools/anim/cycles.py), locked to the simulation's stride clock (<see cref="CycleMotion"/>). Each physics tick <see cref="Compute"/>
/// samples the cycles at the clock's phase, blended by the smoothed speed, and gives <see cref="PlayerFigure"/> the cycle's feet
/// (their fore-aft travel stretched so a step covers the simulation's step), foot pitch and yaw, stance, pelvis place and turn; the
/// figure blends them with its procedural pose by its mocap weight, holds the planted feet (<see cref="PlantLock"/>), plays touches,
/// skills and shots on top and solves the legs to its ankles. <see cref="Present"/> then solves the body (SkinnedBody.Solve: the
/// legs, and the procedural trunk and arms), puts the cycle's spine, neck, head and arms in by the same weight, turns the head toward
/// the figure's look and the arms out in close control, and shows it. No clock and no randomness.
/// </summary>
public sealed class AttackerAnimator
{
    const string Folder = "res://assets/anim/attacker/cycles";
    /// <summary>Share of the head's (and half of it the neck's) turn that comes from the figure's look (along the travel, partly at the ball).</summary>
    public const float HeadLook = 0.65f;
    /// <summary>How far the upper arms turn out from the body in close control (radians).</summary>
    public const float ArmOutClose = 0.16f;
    /// <summary>The stride's fore-aft shortening is kept above this (a walk's steps shortened to a shuffle look wrong; the figure fades to procedural there).</summary>
    public const float StrideMin = 0.5f;

    static (Skeleton Sk, List<StrideCycle> Cycles)? _data;
    static string? _loadError;

    readonly SkinnedBody _body;
    readonly Skeleton _sk;
    readonly CycleMotion _motion;
    readonly NQuat[] _local;
    readonly NQuat[] _gr;
    readonly NVec3[] _gp;
    readonly Quaternion[] _clipLocal, _rot, _gP, _gF;
    readonly Vector3[] _pos;
    readonly int _spine2, _neck, _head;
    readonly int[] _spineChain;
    readonly Quaternion[] _restG;
    readonly int[] _clav = new int[2], _arm = new int[2], _fore = new int[2], _hand = new int[2];

    /// <summary>The skill moves' authored clips (--skill-anim clips, see <see cref="SkillClipLayer"/>); null when they cannot load.</summary>
    public SkillClipLayer? Skills { get; }
    /// <summary>The struck shot from motion capture (--shot-anim mocap, see <see cref="ShotClipLayer"/>); null when its clips cannot load.</summary>
    public ShotClipLayer? Shots { get; }
    FigureFrame _frame;

    AttackerAnimator(SkinnedBody body, Skeleton sk, List<StrideCycle> cycles)
    {
        _body = body; _sk = sk;
        _motion = new CycleMotion(sk, cycles);
        _refMotion = new CycleMotion(sk, cycles);
        int n = sk.Count;
        _refLocal = new NQuat[n]; _refGr = new NQuat[n]; _refGp = new NVec3[n];
        _local = new NQuat[n]; _gr = new NQuat[n]; _gp = new NVec3[n];
        _clipLocal = new Quaternion[n]; _rot = new Quaternion[n]; _gP = new Quaternion[n]; _gF = new Quaternion[n]; _pos = new Vector3[n];
        _spine2 = sk.Find("Spine2"); _neck = sk.Find("Neck"); _head = sk.Find("Head");
        _spineChain = new[] { sk.Find("Spine"), sk.Find("Spine1"), _spine2 };
        _restG = new Quaternion[n];
        for (int k = 0; k < n; k++) { var r = sk.RestGlobal[k]; _restG[k] = new Quaternion(r.X, r.Y, r.Z, r.W); }
        // the rest foot (the left; the right mirrors it)
        var rest = new NQuat[n]; for (int k = 0; k < n; k++) rest[k] = sk.RestLocal[k];
        sk.Forward(rest, sk.Offsets[0], _gr, _gp);
        NVec3 an = _gp[sk.Foot[0]], tj = _gp[sk.Toe[0]];
        _df = tj.Z - an.Z; _du = tj.Y - an.Y;
        var td = NVec3.Transform(NVec3.UnitY, _gr[sk.Toe[0]]);
        _th0 = MathF.Atan2(td.Y, td.Z);
        string[] s = { "Left", "Right" };
        for (int i = 0; i < 2; i++) { _clav[i] = sk.Find(s[i] + "Shoulder"); _arm[i] = sk.Find(s[i] + "Arm"); _fore[i] = sk.Find(s[i] + "ForeArm"); _hand[i] = sk.Find(s[i] + "Hand"); }
        Skills = SkillClipLayer.TryCreate(body, sk, out string err);
        if (Skills == null) GD.PushWarning($"--skill-anim clips unavailable, the moves are drawn procedurally: {err}");
        Shots = ShotClipLayer.TryCreate(body, sk, out string serr);
        if (Shots == null) GD.PushWarning($"--shot-anim mocap unavailable, the shot is drawn procedurally: {serr}");
    }

    public SkinnedBody Body => _body;
    public CycleMotion Motion => _motion;
    static readonly bool LogCycles = System.Environment.GetEnvironmentVariable("ATTACKER_ANIM_LOG") == "1";

    /// <summary>The boot's toe tip lies this far along the toe bone from its joint (model metres; tools/anim/retarget.py TOE_TIP).</summary>
    public const float ToeTip = 0.08f;
    readonly float _df, _du, _th0;

    /// <summary>
    /// The model's foot at rest, in world metres for a figure of <paramref name="height"/>: the toe joint's place from the ankle joint along
    /// the foot (<paramref name="df"/> forward, <paramref name="du"/> up), the toe tip's distance from the toe joint and its angle up from
    /// the horizontal (radians). A foot pitched by p (toes up) turns (df, du) by p; toes bent by b turn the tip by p + b.
    /// </summary>
    public void ToeGeometry(float height, out float df, out float du, out float lt, out float th0)
    {
        float sc = _body.ScaleFor(height);
        df = _df * sc; du = _du * sc; lt = ToeTip * sc; th0 = _th0;
    }

    /// <summary>The animator for <paramref name="body"/>, or null (and the reason) when the cycles are missing or do not fit the body.</summary>
    public static AttackerAnimator? TryCreate(SkinnedBody body, out string error)
    {
        error = _loadError ?? "";
        if (_data == null && _loadError == null)
        {
            try
            {
                var json = FileAccess.GetFileAsString(Folder + "/clips.json");
                if (string.IsNullOrEmpty(json)) throw new System.IO.FileNotFoundException($"{Folder}/clips.json not found");
                var (sk, cycles, _) = StrideCycle.Load(json, file =>
                {
                    var bytes = FileAccess.GetFileAsBytes($"{Folder}/{file}");
                    if (bytes == null || bytes.Length == 0) throw new System.IO.FileNotFoundException($"{Folder}/{file} not found");
                    return bytes;
                });
                _data = (sk, cycles);
                foreach (var c in cycles)
                    GD.Print($"ATTACKER_ANIM cycle={c.Clip.Name} speed={c.Speed:F2} steps_per_s={c.StepsPerSecond:F3} blend_speed={c.BlendSpeed:F2} stance={c.StanceShare:F3} full_map={c.FullMap}");
            }
            catch (Exception e) { _loadError = $"attacker cycles: {e.Message}"; error = _loadError; }
        }
        if (_data == null) return null;
        var names = body.DrivenBoneNames;
        var skel = _data.Value.Sk;
        if (names.Count != skel.Count) { error = $"the body drives {names.Count} bones, the cycles {skel.Count}"; return null; }
        for (int k = 0; k < names.Count; k++)
            if (names[k] != skel.Names[k]) { error = $"cycle bone {k} is {skel.Names[k]}, the body drives {names[k]}"; return null; }
        return new AttackerAnimator(body, skel, _data.Value.Cycles);
    }

    // ---- this tick's cycle pose, in the world ----
    /// <summary>Each foot's ankle as the cycle has it (world, the stride stretched, not yet held), whether it is on the ground, its pitch (radians, toes up) and direction.</summary>
    public readonly Vector3[] Ankle = new Vector3[2], FootDir = new Vector3[2], Hip = new Vector3[2];
    public readonly bool[] Stance = new bool[2];
    public readonly float[] Pitch = new float[2];
    /// <summary>The point between the hip joints (the figure's pelvis) and the pelvis's turn (world basis, X forward, Y up, Z his right).</summary>
    public Vector3 Pelvis;
    public Basis PelvisBasis = Basis.Identity;
    /// <summary>The cycle's own trunk roll (hips to neck, radians toward his right) and its shoulders' turn against its hips (radians toward his right) this tick, before any layer.</summary>
    public float CycleRoll { get; private set; }
    public float CycleShoulderTurn { get; private set; }
    /// <summary>The stride stretch this tick and the blended cycles' own speed.</summary>
    public float Stride { get; private set; } = 1f;
    public float NativeSpeed => _motion.NativeSpeed;

    public void Teleport() { _motion.Reset(); _phase.Reset(); _strideInit = false; }
    readonly PhaseFollower _phase = new();
    bool _strideInit;
    /// <summary>How fast (seconds) the stride's fore-aft shortening follows the step.</summary>
    public const float StrideTau = 0.06f;

    /// <summary>
    /// Samples the cycles for this tick. <paramref name="pos"/> is the simulation's root (feet centre on the ground), <paramref name="fwd"/>
    /// the facing, <paramref name="speed"/> the smoothed speed, <paramref name="stepLength"/> the simulation's step (speed over steps per second).
    /// </summary>
    public void Compute(in FigureFrame f, Vector3 pos, Vector3 fwd, float speed, float stepLength, float height, float dt)
    {
        float sc = _body.ScaleFor(height);
        float phase = _phase.Step(f.GaitPhase, f.StepFrequency, dt);
        _frame = f;
        NVec3 hips = _motion.Step(phase, speed, dt, _local, stepLength > 0.05f ? stepLength : float.NaN, sc);
        _sk.Forward(_local, hips, _gr, _gp);
        if (LogCycles) GD.Print($"MOCAP_CYCLE s={f.GaitPhase:F3} f={f.StepFrequency:F3} {_motion.Describe(f.GaitPhase)}");
        // eased, so a step that changes at once (the clock stopping, a speed step) never moves the feet in a tick
        float strideT = stepLength > 0.05f ? Math.Clamp(_motion.Stretch(stepLength, sc), StrideMin, 1f) : StrideMin;
        Stride = _strideInit ? Mathf.Lerp(Stride, strideT, 1f - Mathf.Exp(-dt / StrideTau)) : strideT;
        _strideInit = true;
        Vector3 right = new(-fwd.Z, 0, fwd.X), left = -right;
        Vector3 root = new(pos.X, 0, pos.Z);
        Vector3 W(NVec3 m, float zScale) => root + left * (m.X * sc) + Vector3.Up * (m.Y * sc) + fwd * (m.Z * zScale * sc);
        for (int i = 0; i < 2; i++)
        {
            NVec3 a = _gp[_sk.Foot[i]];
            // the foot's fore-aft place relative to the hips is stretched, the hips' own sway is not
            var m = new NVec3(a.X, a.Y, hips.Z + (a.Z - hips.Z) * Stride);
            Ankle[i] = W(m, 1f);
            Stance[i] = _motion.InStance(i, phase);
            // the foot's turn from rest, applied to the rest's forward: its pitch and its yaw from the body's heading
            var d = _gr[_sk.Foot[i]] * NQuat.Inverse(_sk.RestGlobal[_sk.Foot[i]]);
            // (the yaw from the foot's sideways axis, which stays level while the toes point down in the heel recovery)
            var t = NVec3.Transform(NVec3.UnitZ, d);
            var side = NVec3.Transform(NVec3.UnitX, d);
            float yaw = MathF.Atan2(-side.Z, side.X);
            Pitch[i] = MathF.Atan2(t.Y, t.X * MathF.Sin(yaw) + t.Z * MathF.Cos(yaw));
            FootDir[i] = (fwd * MathF.Cos(yaw) + left * MathF.Sin(yaw)).Normalized();
        }
        for (int i = 0; i < 2; i++) Hip[i] = W(_gp[_sk.UpLeg[i]], 1f);
        {
            // the cycle's own trunk roll (toward his right) and shoulders' turn against the hips at this phase, for the measurements
            // (model: x his left, y up, z forward)
            var up = _gp[_neck] - (_gp[_sk.UpLeg[0]] + _gp[_sk.UpLeg[1]]) * 0.5f;
            CycleRoll = MathF.Atan2(-up.X, up.Y);
            NVec3 hr = _gp[_sk.UpLeg[1]] - _gp[_sk.UpLeg[0]], sr = _gp[_arm[1]] - _gp[_arm[0]];
            // a model line's heading as the world has it at facing 0 (model z -> world X, model x -> world -Z): positive toward his right
            static float Yaw(NVec3 r) => MathF.Atan2(-r.X, r.Z);
            CycleShoulderTurn = Spring.WrapPi(Yaw(sr) - Yaw(hr));
        }
        var mid = (_gp[_sk.UpLeg[0]] + _gp[_sk.UpLeg[1]]) * 0.5f;
        Pelvis = W(mid, 1f);
        // the hips bone's turn from rest, from the model's frame (x his left, z forward) into the world (X forward, Z his right) basis
        var dh = _gr[_sk.Hips] * NQuat.Inverse(_sk.RestGlobal[_sk.Hips]);
        Basis charToWorld = new(left, Vector3.Up, fwd);   // model x, y, z -> world
        Basis turn = new(new Quaternion(dh.X, dh.Y, dh.Z, dh.W));
        Basis facing = new(fwd, Vector3.Up, right);
        PelvisBasis = (charToWorld * turn * charToWorld.Transposed() * facing).Orthonormalized();
        for (int k = 0; k < _local.Length; k++) _clipLocal[k] = new Quaternion(_local[k].X, _local[k].Y, _local[k].Z, _local[k].W);
    }

    /// <summary>
    /// Shows the body for this tick: the figure's joints are solved (legs to its ankles, the pelvis where it put it), then spine,
    /// neck, head and arms take the cycle's rotations by <paramref name="w"/>, the head turns toward the procedural look
    /// (<see cref="HeadLook"/>) and the upper arms out by <paramref name="armOut"/> radians; then the skill layer replaces the pose while a
    /// clip plays (<paramref name="tw"/> each foot's touch weight, <paramref name="planted"/> the figure's planted feet), and the shot
    /// layer while a struck shot is drawn from motion capture.
    /// </summary>
    public void Present(FigureJoints j, float w, float armOut, ReadOnlySpan<float> tw, ReadOnlySpan<bool> planted, bool teleport) =>
        Present(j, w, armOut, default, tw, planted, teleport);

    /// <summary>
    /// <see cref="Present(FigureJoints, float, float, ReadOnlySpan{float}, ReadOnlySpan{bool}, bool)"/> with the body-language layers
    /// (<see cref="BodyLayers"/>) laid over the cycles' trunk, head and arms by the same weight, before a skill or shot clip takes the pose.
    /// </summary>
    public void Present(FigureJoints j, float w, float armOut, in BodyLayers bl, ReadOnlySpan<float> tw, ReadOnlySpan<bool> planted, bool teleport)
    {
        _body.Solve(j);
        var sr = _body.SolvedRotations; var sp = _body.SolvedPositions;
        int n = _rot.Length;
        for (int k = 0; k < n; k++) { _rot[k] = sr[k]; _pos[k] = sp[k]; }
        if (w > 0f)
        {
            for (int k = 1; k < n; k++)
            {
                if (IsLeg(k)) continue;
                _rot[k] = sr[k].Slerp(_clipLocal[k], w);
                if (IsArm(k)) _pos[k] = sp[k].Lerp(_body.RestLocalPosition(k), w);
            }
            // the head and neck look where the figure looks: a share of the way from the cycle's to the procedural turn (world)
            Globals(sr, _gP); Globals(_rot, _gF);
            var headT = _gF[_head].Slerp(_gP[_head], HeadLook * w);
            var neckT = _gF[_neck].Slerp(_gP[_neck], 0.5f * HeadLook * w);
            _rot[_neck] = (_gF[_sk.Parents[_neck]].Inverse() * neckT).Normalized();
            _rot[_head] = (neckT.Inverse() * headT).Normalized();
            // the arms out from the body (close control), about the chest's forward
            if (Math.Abs(armOut) > 1e-4f)
            {
                Globals(_rot, _gF);
                Vector3 fwdM = (_gF[_spine2] * new Vector3(0, 0, 1)).Normalized();
                for (int i = 0; i < 2; i++)
                {
                    var g = new Quaternion(fwdM, (i == 0 ? 1f : -1f) * armOut * w) * _gF[_arm[i]];
                    _rot[_arm[i]] = (_gF[_clav[i]].Inverse() * g).Normalized();
                }
            }
            Layers(bl, w);
        }
        Skills?.Apply(_frame, j.Height, tw, planted, teleport, _rot, _pos);
        Shots?.Apply(_frame, j.Height, j.AnkleHeight, tw, planted, teleport, _rot, _pos);
        _body.PoseBones(_rot, _pos[0], _pos);
        // the head's forward as shown (its turn from rest applied to the model's forward), for the measurements
        Globals(_rot, _gF);
        var hq = _gF[_head] * _restG[_head].Inverse();
        var hm = hq * new Vector3(0, 0, 1);
        HeadForward = new Vector3(hm.Z, hm.Y, -hm.X).Normalized();   // model (x his left, y up, z forward) to world
    }

    readonly CycleMotion _refMotion;
    readonly Dictionary<int, float> _steadyLean = new();
    readonly NQuat[] _refLocal; readonly NQuat[] _refGr; readonly NVec3[] _refGp;

    /// <summary>
    /// The cycles' own trunk lean (hips to neck, radians forward) at a steady <paramref name="speed"/>: the blend the speed asks for, averaged
    /// over a stride (32 phases); per 0.1 m/s, cached. The measurements' reference for "the same speed held steady".
    /// </summary>
    public float SteadyLean(float speed)
    {
        int key = (int)MathF.Round(speed * 10f);
        if (_steadyLean.TryGetValue(key, out float v)) return v;
        float sum = 0f;
        for (int p = 0; p < 32; p++)
        {
            _refMotion.Reset();
            var hips = _refMotion.Step(p / 16f, key / 10f, 1f / 120f, _refLocal);
            _sk.Forward(_refLocal, hips, _refGr, _refGp);
            var up = _refGp[_neck] - (_refGp[_sk.UpLeg[0]] + _refGp[_sk.UpLeg[1]]) * 0.5f;
            sum += MathF.Atan2(up.Z, up.Y);
        }
        return _steadyLean[key] = sum / 32f;
    }

    /// <summary>The shown head's forward direction (world), set by <see cref="Present(FigureJoints, float, float, in BodyLayers, ReadOnlySpan{float}, ReadOnlySpan{bool}, bool)"/>.</summary>
    public Vector3 HeadForward { get; private set; } = new(1, 0, 0);

    /// <summary>
    /// The body-language layers over the cycles' pose (see <see cref="BodyLayers"/>), by the cycles' weight <paramref name="w"/>: the
    /// spine turned about the vertical (a right-handed turn about up is toward his left, hence the sign; a third per spine bone, so the shoulders turn by the whole of it against the hips), the neck and
    /// head by the head's lead on top, each arm out from the body about the chest's forward.
    /// </summary>
    void Layers(in BodyLayers bl, float w)
    {
        if (Math.Abs(bl.TrunkYaw) > 1e-5f)
            for (int s = 0; s < 3; s++) TurnGlobal(_spineChain[s], new Quaternion(Vector3.Up, -bl.TrunkYaw * w / 3f));
        if (Math.Abs(bl.HeadYaw) > 1e-5f)
        {
            TurnGlobal(_neck, new Quaternion(Vector3.Up, -0.4f * bl.HeadYaw * w));
            TurnGlobal(_head, new Quaternion(Vector3.Up, -0.6f * bl.HeadYaw * w));
        }
        for (int i = 0; i < 2; i++)
        {
            float a = i == 0 ? bl.ArmOutL : bl.ArmOutR;
            if (Math.Abs(a) < 1e-5f) continue;
            Globals(_rot, _gF);
            Vector3 fwdM = (_gF[_spine2] * new Vector3(0, 0, 1)).Normalized();
            TurnGlobal(_arm[i], new Quaternion(fwdM, (i == 0 ? 1f : -1f) * a * w));
        }
    }

    /// <summary>Turns bone <paramref name="k"/> by <paramref name="q"/> in body space (its children with it).</summary>
    void TurnGlobal(int k, Quaternion q)
    {
        Globals(_rot, _gF);
        int p = _sk.Parents[k];
        var g = (q * _gF[k]).Normalized();
        _rot[k] = p < 0 ? g : (_gF[p].Inverse() * g).Normalized();
    }

    bool IsLeg(int k) { for (int i = 0; i < 2; i++) if (k == _sk.UpLeg[i] || k == _sk.Leg[i] || k == _sk.Foot[i] || k == _sk.Toe[i]) return true; return false; }
    bool IsArm(int k) { for (int i = 0; i < 2; i++) if (k == _arm[i] || k == _fore[i] || k == _hand[i]) return true; return false; }

    /// <summary>Global rotations (body space) of every bone from local ones.</summary>
    void Globals(ReadOnlySpan<Quaternion> local, Quaternion[] g)
    {
        for (int k = 0; k < local.Length; k++)
        {
            int p = _sk.Parents[k];
            g[k] = p < 0 ? local[k] : (g[p] * local[k]).Normalized();
        }
    }
}

/// <summary>
/// The body-language layers' amounts for one tick (radians), laid over the cycles' trunk, head and arms by <see cref="AttackerAnimator"/>:
/// the shoulders' turn against the hips and the head's against the shoulders (positive toward his right), each arm's extra abduction.
/// </summary>
public struct BodyLayers
{
    public float TrunkYaw, HeadYaw, ArmOutL, ArmOutR;
}

/// <summary>The figure's side of --attacker-anim mocap (see <see cref="AttackerAnimator"/>): its weight, the held feet and the measurements of the shown pose.</summary>
public partial class PlayerFigure
{
    AttackerAnimator? _atkAnim;
    bool _atkFailed;
    /// <summary>"mocap" (the stride cycles drive his running body; needs the skinned body) or "procedural" (today's run cycle). Outfield only.</summary>
    public string AttackerAnimMode { get; set; } = "procedural";
    /// <summary>--skill-anim: "clips" (a move with authored clips is drawn from them; needs mocap mode) or "procedural" (<see cref="PlanSkill"/> draws every move).</summary>
    public string SkillAnimMode { get; set; } = "clips";
    /// <summary>--shot-anim: "mocap" (a struck shot is drawn from the motion-captured kicks over the figure's pose; needs mocap mode) or "procedural" (<see cref="PlanShot"/> draws it).</summary>
    public string ShotAnimMode { get; set; } = "mocap";
    /// <summary>A skill move starts being drawn (see <see cref="SkillClipLayer.Started"/>; only with --attacker-anim mocap).</summary>
    public event Action<SkillStart>? SkillStarted;
    // this tick's touch weights and planted feet, for the skill layer
    float[] _layTw = new float[2];
    bool[] _layPlanted = new bool[2];
    int _layWas; bool _laySkillWas;
    /// <summary>The mocap animator while --attacker-anim mocap drives the body, else null.</summary>
    public AttackerAnimator? AttackerAnim => _atkAnim;
    /// <summary>The cycles' share of the pose (0 = the procedural pose, 1 = the cycles), eased; per foot the hold of a planted foot.</summary>
    float _mW;
    readonly PlantLock[] _mLock = { new(), new() };
    readonly bool[] _mLockWas = new bool[2];
    /// <summary>How fast the mocap weight follows its target (seconds); the walking pace below which the cycles give way to the procedural steps.</summary>
    const float MocapTau = 0.10f, MocapFromMs = 0.7f, MocapFullMs = 1.5f;

    static readonly bool Log = System.Environment.GetEnvironmentVariable("ATTACKER_ANIM_LOG") == "1";
    bool MocapLogOn => Log && !IsKeeper;
    /// <summary>The cycles' weight this tick (0 when not in mocap mode).</summary>
    public float MocapWeight => _mW;

    /// <summary>
    /// Mocap: binds the animator, eases the weight (1 while stepping at a walking pace or more with no skill move, shot, kick or
    /// strike under way) and samples the cycles for this tick.
    /// </summary>
    void MocapBegin(in FigureFrame f, Vector3 pos, Vector3 fwd, bool stepping, float stepLen, float dt)
    {
        if (IsKeeper || _body == null || AttackerAnimMode != "mocap") { _atkAnim = null; _mW = 0f; return; }
        if (_atkAnim == null && !_atkFailed)
        {
            _atkAnim = AttackerAnimator.TryCreate(_body, out string err);
            if (_atkAnim == null) { _atkFailed = true; GD.PushWarning($"--attacker-anim mocap unavailable, using procedural: {err}"); return; }
            if (_atkAnim.Skills != null) _atkAnim.Skills.Started += m => SkillStarted?.Invoke(m);
        }
        if (_atkAnim == null) return;
        if (_atkAnim.Skills != null) _atkAnim.Skills.Mode = SkillAnimMode;
        if (_atkAnim.Shots != null) _atkAnim.Shots.Mode = ShotAnimMode;
        if (_teleporting) { _atkAnim.Teleport(); foreach (var l in _mLock) l.Reset(); }
        // (a shot action runs on the cycles: its striking leg and plant are drawn over them, see PlanShot)
        bool free = stepping && !_skActive && (f.Shot.Valid || _kW <= 0f && _strikeT < 0f && !_seq && f.ShotCharge <= 0.001f && !_pinOn);
        float target = free ? Smooth((_speedS - MocapFromMs) / (MocapFullMs - MocapFromMs)) : 0f;
        _mW = _teleporting ? target : Mathf.Lerp(_mW, target, 1f - Mathf.Exp(-dt / MocapTau));
        if (_mW < 1e-3f && target == 0f) _mW = 0f;
        _atkAnim.Compute(f, pos, fwd, _speedS, stepLen, H, dt);
        BodyLanguageStep(f, dt);
    }

    /// <summary>
    /// Mocap: foot <paramref name="i"/>'s ankle, direction and planted flag between the procedural stride's and the cycle's by the
    /// weight, the planted foot held where it landed (the cycle's stance once it has most of the weight, the procedural before).
    /// </summary>
    void MocapFoot(int i, in FigureFrame f, Vector3 pelXZ, Vector3 right, float hmaxNow, float speed, ref Vector3 ank, ref Vector3 dir, ref bool planted, float dt)
    {
        if (_atkAnim == null || _mW <= 0f) { _mLock[i].Reset(); _mLockWas[i] = false; return; }
        var a = _atkAnim;
        // a foot about to play the ball swings low: its heel recovery is taken down toward the ground (a dribbler's lower kick-back),
        // so the touch's windup starts from near the ball instead of from behind the hip
        Vector3 cyc = a.Ankle[i];
        float lift = cyc.Y - AnkleH;
        // eased: a touch that appears or is dropped (the core replans), or possession won or lost, must not move the foot in a tick
        float liftT = Mathf.Lerp(f.InPossession ? PossessionLift : 1f, TouchLift, TouchPrep(f, i));
        _prep[i] = _teleporting ? liftT : Mathf.Lerp(_prep[i], liftT, 1f - Mathf.Exp(-dt / TouchPrepTau));
        if (lift > 0f)
        {
            Vector3 c0 = cyc;
            cyc.Y = AnkleH + lift * _prep[i];
            // lowered, a foot behind the body may fall out of the leg's reach: it comes in toward the hip instead (as far as the cycle had it, at most)
            Vector3 d = cyc - a.Hip[i];
            // ... and keeps a visible knee bend through the low swing to a touch (the more it is lowered, the more bent)
            float low = Mathf.Clamp((1f - _prep[i]) / (1f - TouchLift), 0f, 1f);
            float most = Mathf.Lerp(Math.Max(0.96f * Leg, (c0 - a.Hip[i]).Length()), 0.92f * Leg, low);
            if (d.Length() > most) cyc = a.Hip[i] + d * (most / d.Length());
        }
        Vector3 want = ank.Lerp(cyc, _mW);
        bool cycle = _mW >= 0.5f;
        bool stance = cycle ? a.Stance[i] : planted;
        var lk = _mLock[i];
        // a held foot further from its hip than a straight leg reaches at this pelvis height is let go
        Vector3 pm = pelXZ.Lerp(new Vector3(a.Pelvis.X, 0, a.Pelvis.Z), _mW);
        Vector3 hip = pm + right * (i == 0 ? -HipW : HipW);
        Vector3 held = new(lk.Lock.X, lk.Lock.Y, lk.Lock.Z);
        float dy = a.Pelvis.Y - held.Y;
        float reach = 0.995f * Leg, hmax = Mathf.Sqrt(Math.Max(0.01f, reach * reach - dy * dy));
        bool far = lk.Locked && Dist2(hip, held) > Math.Max(hmax, hmaxNow);
        var got = lk.Apply(stance, new NVec3(want.X, want.Y, want.Z), far, dt);
        ank = new Vector3(got.X, got.Y, got.Z);
        // with the cycles in, a step is counted where a foot is held (the procedural plants are not drawn): one count per landing
        if (lk.Locked && !_mLockWas[i]) MeasureStep(f, new Vector3(lk.Lock.X, lk.Lock.Y, lk.Lock.Z), speed);
        _mLockWas[i] = lk.Locked;
        if (cycle) planted = lk.Settled;
        if (Log && Stats != null) GD.Print($"MOCAP_FOOT tick={Stats.Tick} foot={(i == 0 ? "L" : "R")} w={_mW:F2} s={f.GaitPhase:F3} st={(stance ? 1 : 0)} lock={(lk.Locked ? 1 : 0)} far={(far ? 1 : 0)} want=({want.X:F3},{want.Y:F3},{want.Z:F3}) ank=({ank.X:F3},{ank.Y:F3},{ank.Z:F3}) stride={a.Stride:F2} v={_speedS:F2}");
        float y0 = Mathf.Atan2(dir.Z, dir.X), y1 = Mathf.Atan2(a.FootDir[i].Z, a.FootDir[i].X);
        float yaw = Mathf.LerpAngle(y0, y1, _mW);
        dir = new Vector3(Mathf.Cos(yaw), 0, Mathf.Sin(yaw));
    }

    /// <summary>The cycle's swing height above the ground kept while in possession, and for a foot about to touch the ball (see <see cref="TouchPrep"/>).</summary>
    const float PossessionLift = 0.8f, TouchLift = 0.35f;
    /// <summary>Per foot: the share of the cycle's swing height drawn (eased), 1 = all of it.</summary>
    readonly float[] _prep = { 1f, 1f };
    /// <summary>How long before a touch's contact (seconds) its foot starts to swing low, and by when it is all the way low.</summary>
    const float TouchPrepFrom = 0.42f, TouchPrepFull = 0.14f, TouchPrepTau = 0.06f;

    /// <summary>0..1: how near foot <paramref name="i"/>'s next touch is (the pending touch of that foot, or the core's expected next contact), 0 when none is due.</summary>
    float TouchPrep(in FigureFrame f, int i)
    {
        float eta = float.PositiveInfinity;
        if (f.Action.Valid && !f.Action.IsSkill && !f.Action.Made && (f.Action.Right ? 1 : 0) == i && f.Action.ContactEta > 0f) eta = f.Action.ContactEta;
        if (f.InPossession && (f.NextTouchRight ? 1 : 0) == i && f.NextTouchEta > 0f) eta = Math.Min(eta, f.NextTouchEta);
        if (f.Upcoming.Valid && (f.Upcoming.Right ? 1 : 0) == i) eta = Math.Min(eta, Math.Max(0f, f.Upcoming.ContactEta));
        if (float.IsInfinity(eta)) return 0f;
        return Smooth(1f - (eta - TouchPrepFull) / (TouchPrepFrom - TouchPrepFull));
    }

    /// <summary>Mocap: the foot's pitch between the stride's and the cycle's; toes bent up to stay on the ground while the heel is up near it.</summary>
    float MocapPitch(int i, float proc, Vector3 ank, out float toe, out float lift)
    {
        toe = 0f; lift = 0f;
        if (_atkAnim == null || _mW <= 0f) return proc;
        // a foot about to play the ball levels out as it swings low (TouchPrep), so the touch's windup does not have to turn it round
        float p = _atkAnim.Pitch[i] * (1f - 0.75f * Mathf.Clamp((1f - _prep[i]) / (1f - TouchLift), 0f, 1f));
        float pitch = Mathf.Lerp(proc, p, _mW);
        // a flat or toes-up foot keeps its heel out of the ground (the cycles' heels sit up to a centimetre low: their retarget
        // grounded the toes and ankle only)
        if (pitch > -0.05f && ank.Y < AnkleH) lift = (AnkleH - ank.Y) * _mW;
        return pitch;
    }

    readonly float[] _pitchOut = new float[2], _toeOut = new float[2];
    bool _pitchOk;
    /// <summary>How fast the drawn foot pitch and toe bend may change in mocap mode (radians per second): a pose handed over at once (a skill or kick weight jumping to 1) would turn the foot in a tick.</summary>
    const float PitchRate = 14f;

    /// <summary>Mocap: limits the change of foot <paramref name="i"/>'s pitch and toe bend per tick, except on a touch's contact (drawn exactly).</summary>
    void MocapPitchRate(int i, bool exact, float dt, ref Vector3 ank)
    {
        if (_atkAnim == null) return;
        if (_pitchOk && !_teleporting && !exact)
        {
            float st = PitchRate * dt;
            _pitch[i] = _pitchOut[i] + Math.Clamp(_pitch[i] - _pitchOut[i], -st, st);
            _toeBend[i] = _toeOut[i] + Math.Clamp(_toeBend[i] - _toeOut[i], -st, st);
        }
        _pitchOut[i] = _pitch[i]; _toeOut[i] = _toeBend[i];
        if (i == 1) _pitchOk = true;
        if (!exact) KeepToesUp(i, ref ank);
    }

    /// <summary>
    /// Mocap: the boot out of the ground for the pitch actually drawn, from the model's own foot (the toe joint's and the toe tip's
    /// place relative to the ankle at rest, see <see cref="AttackerAnimator.ToeGeometry"/>): a toe joint that would be below
    /// <see cref="ToeJointClear"/> lifts the foot, then the toes bend up (never past flat) so their tip stays above <see cref="ToeTipClear"/>.
    /// </summary>
    void KeepToesUp(int i, ref Vector3 ank)
    {
        if (_atkAnim == null) return;
        _atkAnim.ToeGeometry(H, out float df, out float du, out float lt, out float th0);
        float p = _pitch[i];
        // the heel (on the sole, 3 cm behind the ankle) out of the ground too
        float heelY = ank.Y - 0.03f * H / 1.8f * Mathf.Sin(p) - AnkleH * Mathf.Cos(p);
        if (heelY < 0f) ank.Y -= heelY;
        float toeY = ank.Y + df * Mathf.Sin(p) + du * Mathf.Cos(p);
        if (toeY < ToeJointClear) { ank.Y += ToeJointClear - toeY; toeY = ToeJointClear; }
        float tipY = toeY + lt * Mathf.Sin(th0 + p + _toeBend[i]);
        if (tipY < ToeTipClear)
        {
            float need = Mathf.Asin(Mathf.Clamp((ToeTipClear - toeY) / lt, -1f, 1f)) - th0 - p;
            float most = Math.Max(_toeBend[i], -p);
            // no faster than the pitch may turn (a toe flicking up is a pop too): what the bend cannot take, the foot's lift does
            if (_pitchOk && !_teleporting) most = Math.Min(most, Math.Max(_toeBend[i], _toePrev[i] + PitchRate / 120f));
            _toeBend[i] = Mathf.Clamp(need, _toeBend[i], Math.Max(most, _toeBend[i]));
            tipY = toeY + lt * Mathf.Sin(th0 + p + _toeBend[i]);
            if (tipY < ToeTipClear) ank.Y += ToeTipClear - tipY;   // toes flat and still low: the whole foot comes up
        }
        _toeOut[i] = _toePrev[i] = _toeBend[i];
    }
    readonly float[] _toePrev = new float[2];

    /// <summary>
    /// Mocap: a swinging foot's ankle is brought in toward the hip as the leg nears full length (past <see cref="SoftReachFrom"/> of it,
    /// approaching straight exponentially, as <see cref="TwoBoneIk.Solve(System.Numerics.Vector3, float, float, System.Numerics.Vector3, System.Numerics.Vector3, float, out System.Numerics.Vector3, out System.Numerics.Vector3)"/>
    /// does): near straight a knee's angle changes many times faster than the foot moves, so a foot swept through full reach whips it.
    /// Planted, touching and kicking feet are left where they are.
    /// </summary>
    Vector3 MocapSoftReach(Vector3 hip, Vector3 ank)
    {
        if (_atkAnim == null || _mW <= 0f) return ank;
        Vector3 d = ank - hip;
        float len = d.Length(), full = Leg, soft = SoftReachFrom * full;
        if (len <= soft || len < 1e-4f) return ank;
        float band = full - soft;
        float to = soft + band * (1f - Mathf.Exp(-(len - soft) / band));
        return hip + d * (Mathf.Lerp(len, to, _mW) / len);
    }
    const float SoftReachFrom = 0.93f;

    /// <summary>Clearance (metres) kept under the toe joint and the boot's toe tip.</summary>
    const float ToeJointClear = 0.015f, ToeTipClear = 0.004f;

    // a skill move's start in mocap mode: the move is drawn from the procedural foot, the difference to the drawn one dying away
    Vector3 _skOff, _skOffVel;
    bool _skFirst;
    readonly Vector3[] _prevAnkRel2 = new Vector3[2];
    /// <summary>Half-life (seconds) of the difference between the drawn foot and the procedural one a skill move starts from.</summary>
    const float SkillStartHalfLife = 0.05f;

    /// <summary>
    /// Mocap: a skill move starts from where the procedural stride has the playing foot (its reach then covers the procedural distance,
    /// not the cycle's, which can be far behind or high); the difference from where the foot is drawn is kept as an offset that decays.
    /// </summary>
    void SkillStartFromProcedural(int fi, Vector3 posXZ)
    {
        _skOff = _skOffVel = Vector3.Zero; _skFirst = false;
        if (_atkAnim == null || _mW <= 0f || _teleporting) return;
        _skFirst = true;
        Vector3 proc = _feet[fi].Ankle - posXZ; proc.Y = _feet[fi].Ankle.Y;
        _skOff = _skFromRel - proc;
        _skFromRel = proc;
    }

    /// <summary>
    /// On a move's first tick: the offset starts with the drawn foot's velocity (relative to the body) less the move's own at its start
    /// (the reach's straight line to the ball, or none), so the foot's velocity carries on through the hand-over instead of changing at once.
    /// </summary>
    void SkillStartVelocity(int fi, Vector3 from, Vector3 target, float reachSpan, float dt)
    {
        _skFirst = false;
        if (dt <= 0f) dt = 1f / 120f;
        Vector3 vd = (_prevAnkRel[fi] - _prevAnkRel2[fi]) / dt;
        Vector3 vr = reachSpan > dt ? (target - from) / reachSpan : Vector3.Zero;
        _skOffVel = vd - vr;
    }

    /// <summary>The skill start offset this tick (decayed by a critically damped spring).</summary>
    Vector3 SkillStartOffset(float dt)
    {
        if (_skOff.LengthSquared() < 1e-10f && _skOffVel.LengthSquared() < 1e-8f) return Vector3.Zero;
        var x = new NVec3(_skOff.X, _skOff.Y, _skOff.Z); var v = new NVec3(_skOffVel.X, _skOffVel.Y, _skOffVel.Z);
        Spring.Decay(ref x, ref v, SkillStartHalfLife, dt > 0f ? dt : 1f / 120f);
        _skOff = new Vector3(x.X, x.Y, x.Z); _skOffVel = new Vector3(v.X, v.Y, v.Z);
        return _skOff;
    }

    /// <summary>Mocap: the pelvis between the procedural one and the cycle's (moved into a turn as the procedural one is).</summary>
    Vector3 MocapPelvis(Vector3 pel, Vector3 right)
    {
        if (_atkAnim == null || _mW <= 0f) return pel;
        Vector3 pm = _atkAnim.Pelvis;
        float bank = BodyLangOff ? _bank : _bl.Bank;
        pm += right * (Mathf.Sin(bank) * BankShift * (pm.Y - AnkleH));
        if (!BodyLangOff) pm.Y -= _bl.PelvisDrop * H / 1.8f;
        return pel.Lerp(pm, _mW);
    }

    /// <summary>Mocap: the pelvis turn between the procedural one and the cycle's, the cycle's tipped by the acceleration lean (and more over its own speed) and banked into a turn.</summary>
    Basis MocapPelvisBasis(Basis P, Vector3 fwd, Vector3 right)
    {
        if (_atkAnim == null || _mW <= 0f) return P;
        // the acceleration lean only (the figure's lean less its speed lean): the cycles carry their own lean with speed
        float lean = _lean - SpeedLean;
        if (!BodyLangOff) lean += _bl.Lean;   // (the body-language layers: the acceleration's lean)
        Basis pm = new Basis(right, -lean) * new Basis(fwd, BodyLangOff ? _bank : _bl.Bank) * _atkAnim.PelvisBasis;
        return P.Orthonormalized().Slerp(pm.Orthonormalized(), _mW);
    }

    /// <summary>Mocap: shows the body (the cycles' trunk and arms in by the weight); false when not in mocap mode.</summary>
    bool MocapPresent()
    {
        if (_atkAnim == null) return false;
        _atkAnim.Present(_joints, _mW * (1f - ShotUpperShare * _shUpper), AttackerAnimator.ArmOutClose * _closeW, BodyLangOff ? default : BodyLayersNow(), _layTw, _layPlanted, _teleporting);
        return true;
    }

    // ---- measurements of the shown pose (mocap mode): the skinned body's own joints, as drawn ----
    bool _shown;
    readonly Vector3[] _shownToe = new Vector3[2];
    readonly Quaternion[] _bonePrev = new Quaternion[22];
    bool _boneHave;

    /// <summary>Hip-to-ankle distance over the leg's length (thigh plus shin) of the shown leg on bone k's side.</summary>
    static float Reach(SkinnedBody b, int k)
    {
        int o = k >= 14 ? 14 : 6;
        Vector3 h = b.DrivenBoneWorld(o + 4), kn = b.DrivenBoneWorld(o + 5), a = b.DrivenBoneWorld(o + 6);
        return (a - h).Length() / ((kn - h).Length() + (a - kn).Length());
    }

    static float KneeDeg(SkinnedBody b, int k)
    {
        int o = k >= 14 ? 14 : 6;
        Vector3 h = b.DrivenBoneWorld(o + 4), kn = b.DrivenBoneWorld(o + 5), a = b.DrivenBoneWorld(o + 6);
        return (kn - h).AngleTo(a - kn) * 180f / Mathf.Pi;
    }

    /// <summary>
    /// Mocap: replaces the figure's ankles, knees, pelvis and lean with the shown body's (foot and shin bone heads, the point between
    /// the hip joints, the chest's lean), records the toe tips, and the fastest bone turn of the tick outside shots.
    /// </summary>
    void MocapShown(in FigureFrame f, Vector3[] ankEff, Vector3[] knee, ref Vector3 pel, ref float lean, Vector3 fwd, float dt)
    {
        _shown = false;
        if (_atkAnim == null && _body != null && Stats != null && !IsKeeper) { BoneSpeeds(f, _body, dt); return; }
        if (_atkAnim == null || _body == null) return;
        _shown = true;
        var b = _body;
        for (int i = 0; i < 2; i++)
        {
            int o = i == 0 ? 6 : 14;   // driven order per side: shoulder, arm, forearm, hand, thigh, shin, foot, toe
            knee[i] = b.DrivenBoneWorld(o + 5);
            ankEff[i] = b.DrivenBoneWorld(o + 6);
            _shownToe[i] = b.DrivenBoneWorld(o + 7) + b.DrivenBoneAxisWorld(o + 7) * (AttackerAnimator.ToeTip * b.ScaleFor(H));
        }
        pel = (b.DrivenBoneWorld(10) + b.DrivenBoneWorld(18)) * 0.5f;
        Vector3 up = b.DrivenBoneWorld(4) - b.DrivenBoneWorld(0);   // hips to neck
        lean = Mathf.Atan2(up.Dot(fwd), up.Y);
        if (Stats == null) return;
        for (int i = 0; i < 2; i++)
        {
            Stats.MinToeYM = Math.Min(Stats.MinToeYM, _shownToe[i].Y);
            if (Log && _shownToe[i].Y < -0.005f) GD.Print($"MOCAP_TOE tick={Stats.Tick} foot={i} toe_y={_shownToe[i].Y:F3} ank_y={ankEff[i].Y:F3} pitch={_pitch[i]:F2} bend={_toeBend[i]:F2} lock={(_mLock[i].Locked ? 1 : 0)} w={_mW:F2}");
        }
        BodyLangMeasure(f, ankEff, pel, lean, fwd, dt);
        bool shot = BoneSpeeds(f, b, dt);
        if (f.Skill.Valid || shot) return;
        Stats.MocapTicks++; if (_mW >= 0.999f) Stats.MocapFull++;
    }

    /// <summary>Whether driven bone <paramref name="k"/> is the foot or toes of a foot playing the ball this tick (touch pose, skill or kick weight).</summary>
    bool PlayingFootBone(in FigureFrame f, int k)
    {
        int side = k is 12 or 13 ? 0 : k is 20 or 21 ? 1 : -1;   // driven order: left foot 12, toe 13; right 20, 21
        if (side < 0) return false;
        return _twPrev[side] > 0f || _kW > 0f && side == _kFoot || f.Skill.Valid && (f.Skill.Right ? 1 : 0) == side;
    }

    /// <summary>Whether driven bone <paramref name="k"/> is in the leg (thigh, shin, foot, toes) of a foot playing the ball this tick.</summary>
    bool PlayingLegBone(in FigureFrame f, int k)
    {
        int side = k is >= 10 and <= 13 ? 0 : k is >= 18 and <= 21 ? 1 : -1;   // driven order: left thigh 10 .. toe 13; right 18 .. 21
        if (side < 0) return false;
        return _twPrev[side] > 0f || _kW > 0f && side == _kFoot || f.Skill.Valid && (f.Skill.Right ? 1 : 0) == side;
    }

    /// <summary>The fastest bone turn of the tick (local rotations, outside shots) into the stats; returns whether a shot is under way.</summary>
    bool BoneSpeeds(in FigureFrame f, SkinnedBody b, float dt)
    {
        bool shot = f.Shot.Valid || _strikeT >= 0f || !float.IsNaN(f.StrikeEta) || f.ShotCharge > 0.001f;
        int n = Math.Min(b.DrivenCount, _bonePrev.Length);
        if (_boneHave && !_teleporting && !shot)
            for (int k = 0; k < n; k++)
            {
                float deg = _bonePrev[k].AngleTo(b.DrivenLocalRotation(k)) * 180f / Mathf.Pi / dt;
                // (BODY_LANG: the same outside the whole leg of a foot playing the ball)
                if (!PlayingLegBone(f, k))
                {
                    if (deg > 1500f) Stats.BodyLang.OffLegFast++;
                    if (deg > Stats.BodyLang.OffLegMaxDegS) { Stats.BodyLang.OffLegMaxDegS = deg; Stats.BodyLang.OffLegAt = $"{b.DrivenBoneNames[k]}@{Stats.Tick}"; }
                }
                // the playing foot turning onto its surface (a touch's windup and contact, a skill move, a kick) is reported apart
                if (PlayingFootBone(f, k))
                {
                    if (deg > 1500f) Stats.FootTurnFast++;
                    if (deg > Stats.FootTurnMaxDegS) { Stats.FootTurnMaxDegS = deg; Stats.FootTurnAt = $"{b.DrivenBoneNames[k]}@{Stats.Tick}"; }
                    continue;
                }
                if (deg > 1500f) { Stats.BoneFast++; if (Log) GD.Print($"MOCAP_FAST tick={Stats.Tick} bone={b.DrivenBoneNames[k]} deg_s={deg:F0} w={_mW:F2} knee_deg={KneeDeg(b, k):F1} reach={Reach(b, k):F3}"); }
                if (deg > Stats.BoneMaxDegS) { Stats.BoneMaxDegS = deg; Stats.BoneMaxAt = $"{b.DrivenBoneNames[k]}@{Stats.Tick}"; }
            }
        if (_boneHave && !_teleporting && shot) ShotBoneSpeeds(b, n, dt);
        if (Log && _atkAnim != null) GD.Print($"MOCAP_KNEE tick={Stats.Tick} hips={b.DrivenBoneWorld(0).Y:F3} L={KneeDeg(b, 11):F1} R={KneeDeg(b, 19):F1} Ltw={_bonePrev[11].AngleTo(b.DrivenLocalRotation(11)) * 57.3f:F1} Rtw={_bonePrev[19].AngleTo(b.DrivenLocalRotation(19)) * 57.3f:F1}");
        for (int k = 0; k < n; k++) _bonePrev[k] = b.DrivenLocalRotation(k);
        _boneHave = true;
        return shot;
    }
}
