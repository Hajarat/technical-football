using System;
using System.Collections.Generic;
using FootballSim.Anim;
using Godot;
using NQuat = System.Numerics.Quaternion;
using NVec3 = System.Numerics.Vector3;

namespace FootballSim.Game;

/// <summary>
/// A full-body clip that plays instead of the stride cycles, timed by the core (a skill move's authored clip, for one): the seam for
/// later work, nothing implements it yet. When <see cref="AttackerAnimator.FullBody"/> is set and <see cref="TryPose"/> returns true,
/// its pose (local rotations in the clip bone order and the hips relative to the simulation's root, model space at the model's size)
/// replaces the cycles' for that tick, blended in and out by the same weight and drawn with the same layers and touch IK.
/// </summary>
public interface IFullBodyClip
{
    bool TryPose(in FigureFrame f, Span<NQuat> local, out NVec3 hips, out byte contacts);
}

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
    readonly int[] _clav = new int[2], _arm = new int[2], _fore = new int[2], _hand = new int[2];

    /// <summary>The seam for a full-body clip timed by the core (see <see cref="IFullBodyClip"/>); null: the cycles always play.</summary>
    public IFullBodyClip? FullBody;

    AttackerAnimator(SkinnedBody body, Skeleton sk, List<StrideCycle> cycles)
    {
        _body = body; _sk = sk;
        _motion = new CycleMotion(sk, cycles);
        int n = sk.Count;
        _local = new NQuat[n]; _gr = new NQuat[n]; _gp = new NVec3[n];
        _clipLocal = new Quaternion[n]; _rot = new Quaternion[n]; _gP = new Quaternion[n]; _gF = new Quaternion[n]; _pos = new Vector3[n];
        _spine2 = sk.Find("Spine2"); _neck = sk.Find("Neck"); _head = sk.Find("Head");
        // the rest foot (the left; the right mirrors it)
        var rest = new NQuat[n]; for (int k = 0; k < n; k++) rest[k] = sk.RestLocal[k];
        sk.Forward(rest, sk.Offsets[0], _gr, _gp);
        NVec3 an = _gp[sk.Foot[0]], tj = _gp[sk.Toe[0]];
        _df = tj.Z - an.Z; _du = tj.Y - an.Y;
        var td = NVec3.Transform(NVec3.UnitY, _gr[sk.Toe[0]]);
        _th0 = MathF.Atan2(td.Y, td.Z);
        string[] s = { "Left", "Right" };
        for (int i = 0; i < 2; i++) { _clav[i] = sk.Find(s[i] + "Shoulder"); _arm[i] = sk.Find(s[i] + "Arm"); _fore[i] = sk.Find(s[i] + "ForeArm"); _hand[i] = sk.Find(s[i] + "Hand"); }
    }

    public SkinnedBody Body => _body;
    public CycleMotion Motion => _motion;

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
    /// <summary>The stride stretch this tick and the blended cycles' own speed.</summary>
    public float Stride { get; private set; } = 1f;
    public float NativeSpeed => _motion.NativeSpeed;

    public void Teleport() => _motion.Reset();

    /// <summary>
    /// Samples the cycles for this tick. <paramref name="pos"/> is the simulation's root (feet centre on the ground), <paramref name="fwd"/>
    /// the facing, <paramref name="speed"/> the smoothed speed, <paramref name="stepLength"/> the simulation's step (speed over steps per second).
    /// </summary>
    public void Compute(in FigureFrame f, Vector3 pos, Vector3 fwd, float speed, float stepLength, float height, float dt)
    {
        float sc = _body.ScaleFor(height);
        NVec3 hips = _motion.Step(f.GaitPhase, speed, dt, _local, stepLength > 0.05f ? stepLength : float.NaN, sc);
        byte clipContacts = 0;
        bool full = FullBody != null && FullBody.TryPose(f, _local, out hips, out clipContacts);
        _sk.Forward(_local, hips, _gr, _gp);
        Stride = stepLength > 0.05f ? Math.Clamp(_motion.Stretch(stepLength, sc), StrideMin, 1f) : 1f;
        Vector3 right = new(-fwd.Z, 0, fwd.X), left = -right;
        Vector3 root = new(pos.X, 0, pos.Z);
        Vector3 W(NVec3 m, float zScale) => root + left * (m.X * sc) + Vector3.Up * (m.Y * sc) + fwd * (m.Z * zScale * sc);
        for (int i = 0; i < 2; i++)
        {
            NVec3 a = _gp[_sk.Foot[i]];
            // the foot's fore-aft place relative to the hips is stretched, the hips' own sway is not
            var m = new NVec3(a.X, a.Y, hips.Z + (a.Z - hips.Z) * Stride);
            Ankle[i] = W(m, 1f);
            Stance[i] = full ? (clipContacts & (i == 0 ? Clip.LeftFoot : Clip.RightFoot)) != 0 : _motion.InStance(i, f.GaitPhase);
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
    /// (<see cref="HeadLook"/>) and the upper arms out by <paramref name="armOut"/> radians.
    /// </summary>
    public void Present(FigureJoints j, float w, float armOut)
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
        }
        _body.PoseBones(_rot, _pos[0], _pos);
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

/// <summary>The figure's side of --attacker-anim mocap (see <see cref="AttackerAnimator"/>): its weight, the held feet and the measurements of the shown pose.</summary>
public partial class PlayerFigure
{
    AttackerAnimator? _atkAnim;
    bool _atkFailed;
    /// <summary>"mocap" (the stride cycles drive his running body; needs the skinned body) or "procedural" (today's run cycle). Outfield only.</summary>
    public string AttackerAnimMode { get; set; } = "procedural";
    /// <summary>The mocap animator while --attacker-anim mocap drives the body, else null.</summary>
    public AttackerAnimator? AttackerAnim => _atkAnim;
    /// <summary>The cycles' share of the pose (0 = the procedural pose, 1 = the cycles), eased; per foot the hold of a planted foot.</summary>
    float _mW;
    readonly PlantLock[] _mLock = { new(), new() };
    readonly bool[] _mLockWas = new bool[2];
    /// <summary>How fast the mocap weight follows its target (seconds); the walking pace below which the cycles give way to the procedural steps.</summary>
    const float MocapTau = 0.10f, MocapFromMs = 0.7f, MocapFullMs = 1.5f;
    /// <summary>Extra forward lean (radians) when running faster than the cycles' own speed, at 2.2 m/s over it.</summary>
    const float MocapOverLean = 0.10f;

    static readonly bool Log = System.Environment.GetEnvironmentVariable("ATTACKER_ANIM_LOG") == "1";
    bool MocapLogOn => Log && _atkAnim != null;
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
        }
        if (_atkAnim == null) return;
        if (_teleporting) { _atkAnim.Teleport(); foreach (var l in _mLock) l.Reset(); }
        bool free = stepping && !_skActive && !f.Shot.Valid && _kW <= 0f && _strikeT < 0f && !_seq && f.ShotCharge <= 0.001f && !_pinOn;
        float target = free ? Smooth((_speedS - MocapFromMs) / (MocapFullMs - MocapFromMs)) : 0f;
        _mW = _teleporting ? target : Mathf.Lerp(_mW, target, 1f - Mathf.Exp(-dt / MocapTau));
        if (_mW < 1e-3f && target == 0f) _mW = 0f;
        _atkAnim.Compute(f, pos, fwd, _speedS, stepLen, H, dt);
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
            float most = Math.Max(0.96f * Leg, (c0 - a.Hip[i]).Length());
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
        if (lk.Locked && !_mLockWas[i] && cycle) MeasureStep(f, new Vector3(lk.Lock.X, lk.Lock.Y, lk.Lock.Z), speed);
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
        pm += right * (Mathf.Sin(_bank) * BankShift * (pm.Y - AnkleH));
        return pel.Lerp(pm, _mW);
    }

    /// <summary>Mocap: the pelvis turn between the procedural one and the cycle's, the cycle's tipped by the acceleration lean (and more over its own speed) and banked into a turn.</summary>
    Basis MocapPelvisBasis(Basis P, Vector3 fwd, Vector3 right)
    {
        if (_atkAnim == null || _mW <= 0f) return P;
        float lean = (_lean - SpeedLean) + MocapOverLean * Mathf.Clamp((_speedS - _atkAnim.NativeSpeed) / 2.2f, 0f, 1f);
        Basis pm = new Basis(right, -lean) * new Basis(fwd, _bank) * _atkAnim.PelvisBasis;
        return P.Orthonormalized().Slerp(pm.Orthonormalized(), _mW);
    }

    /// <summary>Mocap: shows the body (the cycles' trunk and arms in by the weight); false when not in mocap mode.</summary>
    bool MocapPresent()
    {
        if (_atkAnim == null) return false;
        _atkAnim.Present(_joints, _mW, AttackerAnimator.ArmOutClose * _closeW);
        return true;
    }

    // ---- measurements of the shown pose (mocap mode): the skinned body's own joints, as drawn ----
    bool _shown;
    readonly Vector3[] _shownToe = new Vector3[2];
    readonly Quaternion[] _bonePrev = new Quaternion[22];
    bool _boneHave;

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
        bool shot = BoneSpeeds(f, b, dt);
        if (f.Skill.Valid || shot) return;
        Stats.MocapTicks++; if (_mW >= 0.999f) Stats.MocapFull++;
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
                if (deg > 1500f) { Stats.BoneFast++; if (Log) GD.Print($"MOCAP_FAST tick={Stats.Tick} bone={b.DrivenBoneNames[k]} deg_s={deg:F0} w={_mW:F2} knee_deg={KneeDeg(b, k):F1}"); }
                if (deg > Stats.BoneMaxDegS) { Stats.BoneMaxDegS = deg; Stats.BoneMaxAt = $"{b.DrivenBoneNames[k]}@{Stats.Tick}"; }
            }
        if (Log && _atkAnim != null) GD.Print($"MOCAP_KNEE tick={Stats.Tick} hips={b.DrivenBoneWorld(0).Y:F3} L={KneeDeg(b, 11):F1} R={KneeDeg(b, 19):F1} Ltw={_bonePrev[11].AngleTo(b.DrivenLocalRotation(11)) * 57.3f:F1} Rtw={_bonePrev[19].AngleTo(b.DrivenLocalRotation(19)) * 57.3f:F1}");
        for (int k = 0; k < n; k++) _bonePrev[k] = b.DrivenLocalRotation(k);
        _boneHave = true;
        return shot;
    }
}
