using System.Numerics;

namespace FootballSim.Anim;

/// <summary>
/// Weights of the feature groups (spec appendix E's starting values). The save point and the time to contact count only in save sets
/// (<see cref="Save"/>); the body group (hips height and tilt) only where lying and getting up are matched by pose. The gait phase,
/// the turn demand and the ball (its place and velocity relative to the root) count only in the attacker's sets
/// (<see cref="AttackerChooser.WeightsFor"/>); at weight 0, as everywhere else, they change no cost.
/// </summary>
public sealed record FeatureWeights(float FootPosition = 0.75f, float FootVelocity = 1f, float HipVelocity = 1f,
    float TrajectoryPosition = 1f, float TrajectoryFacing = 1.5f, float SavePoint = 0f, float SaveTime = 0f, float Body = 0f,
    float GaitPhase = 0f, float TurnDemand = 0f, float BallPosition = 0f, float BallVelocity = 0f)
{
    public static readonly FeatureWeights Default = new();
    /// <summary>A save set's: the defaults plus the save point (2.0) and the time to contact (1.5).</summary>
    public static readonly FeatureWeights Save = new(SavePoint: 2f, SaveTime: 1.5f);
}

/// <summary>Which part of the body a save set's clips meet the ball with (where <see cref="PoseClip.LimbAtTarget"/> is taken).</summary>
public enum ContactLimb { Hands, ReachingHand, ReachingFoot }

/// <summary>
/// How a set's frames are labelled beyond the pose: the event its time feature counts down to (a save's "contact", a get-up's "up";
/// null for none), the limb that meets the ball there, and the events bounding the searchable frames (from <see cref="SearchFrom"/>
/// inclusive to <see cref="SearchUntil"/> exclusive; a clip without the event is not bounded by it).
/// </summary>
public sealed record PoseSetOptions(string? TargetEvent = null, ContactLimb Limb = ContactLimb.Hands, string? SearchFrom = null, string? SearchUntil = null)
{
    public static readonly PoseSetOptions None = new();
    /// <summary>
    /// The ball a clip was made with: its centre per frame in the clip's model space (an authored skill clip's ball track), or null
    /// for a clip without one (its ball features are zero). Asked once per clip when the set is built.
    /// </summary>
    public Func<Clip, IReadOnlyList<Vector3>?>? BallTrack { get; init; }
    /// <summary>
    /// Frames within this long of a one-shot clip's end are kept but never searched (<see cref="PoseSet.EndExclusionSeconds"/> unless
    /// set: the attacker's locomotion leaves out the whole half second its trajectory and turn features look ahead, where they are
    /// carried on past the recording and so always look like a perfectly straight run).
    /// </summary>
    public float EndExclusion { get; init; } = PoseSet.EndExclusionSeconds;
}

/// <summary>
/// The matching features of one pose (47 floats), in character space: about the root on the ground under the hips, turned so the
/// body faces +Z (the model's own axes: left +X, up +Y). Groups, in order: left and right foot (ankle) position; left and right foot
/// velocity; hip velocity; the root's position 10, 20 and 30 frames (60 Hz) ahead (x, z); its facing then (x, z of a unit vector);
/// the save point relative to the hips; the time to the set's target event (seconds); the body: hips height, and the hips' forward
/// and up axes (turned from rest); the gait phase (sin and cos of pi times the stride phase: 0.5 at the left foot's mid-stance, 1.5
/// at the right's; zero for a clip without a readable stride); the turn demand (the root's facing change over the next half second,
/// radians, positive toward his left: a query puts the stick's angle off the facing here); the ball's place relative to the root
/// and its velocity relative to the root's (zero for a clip made without a ball).
/// </summary>
public static class Features
{
    public const int Count = 47;
    public const int FootPos = 0, FootVel = 6, HipVel = 12, TrajPos = 15, TrajDir = 21, SavePt = 27, SaveT = 30, Body = 31,
        Gait = 38, Turn = 40, BallPos = 41, BallVel = 44;
    /// <summary>How far ahead the turn demand looks, in 60 Hz frames (half a second).</summary>
    public const int TurnFrames = 30;
    /// <summary>The largest turn demand a query asks for, radians (what a body turns in the half second the feature spans).</summary>
    public const float TurnMax = 1.2f;
    /// <summary>Trajectory sample times, in 60 Hz frames ahead.</summary>
    public static readonly int[] TrajFrames = { 10, 20, 30 };
    /// <summary>The same in seconds.</summary>
    public static readonly float[] TrajSeconds = { 10 / 60f, 20 / 60f, 30 / 60f };
    /// <summary>First index and length of each group.</summary>
    public static readonly (int Start, int Length)[] Groups =
        { (FootPos, 6), (FootVel, 6), (HipVel, 3), (TrajPos, 6), (TrajDir, 6), (SavePt, 3), (SaveT, 1), (Body, 7),
          (Gait, 2), (Turn, 1), (BallPos, 3), (BallVel, 3) };

    public static float Weight(FeatureWeights w, int group) => group switch
    {
        0 => w.FootPosition, 1 => w.FootVelocity, 2 => w.HipVelocity, 3 => w.TrajectoryPosition, 4 => w.TrajectoryFacing,
        5 => w.SavePoint, 6 => w.SaveTime, 7 => w.Body, 8 => w.GaitPhase, 9 => w.TurnDemand, 10 => w.BallPosition, _ => w.BallVelocity,
    };

    /// <summary>The gait phase group for stride phase <paramref name="phase"/> (period 2; NaN: no stride, both zero).</summary>
    public static void WriteGait(Span<float> dst, float phase)
    {
        bool ok = float.IsFinite(phase);
        dst[Gait] = ok ? MathF.Sin(MathF.PI * phase) : 0f;
        dst[Gait + 1] = ok ? MathF.Cos(MathF.PI * phase) : 0f;
    }

    /// <summary>The turn demand, radians (positive toward his left), held to <see cref="TurnMax"/> either way.</summary>
    public static void WriteTurn(Span<float> dst, float turn) => dst[Turn] = Math.Clamp(turn, -TurnMax, TurnMax);

    /// <summary>The ball: its centre relative to the root and its velocity relative to the root's, both in character space.</summary>
    public static void WriteBall(Span<float> dst, Vector3 position, Vector3 velocity)
    {
        Put(dst, BallPos, position);
        Put(dst, BallVel, velocity);
    }

    /// <summary>The save point (character space, relative to the hips) and the seconds to the target event.</summary>
    public static void WriteSave(Span<float> dst, Vector3 point, float time)
    {
        Put(dst, SavePt, point);
        dst[SaveT] = time;
    }

    /// <summary>The body group: hips height and the hips' forward and up axes in character space.</summary>
    public static void WriteBody(Span<float> dst, float hipsHeight, Vector3 forward, Vector3 up)
    {
        dst[Body] = hipsHeight;
        Put(dst, Body + 1, forward);
        Put(dst, Body + 4, up);
    }

    /// <summary>The body group of a pose whose hips global rotation is <paramref name="hipsRot"/>, about a root facing <paramref name="yaw"/>.</summary>
    public static void WriteBody(Span<float> dst, Skeleton sk, Quaternion hipsRot, float hipsHeight, float yaw)
    {
        var turn = hipsRot * Quaternion.Inverse(sk.RestGlobal[sk.Hips]);
        WriteBody(dst, hipsHeight, ToCharacter(Vector3.Transform(Vector3.UnitZ, turn), yaw), ToCharacter(Vector3.Transform(Vector3.UnitY, turn), yaw));
    }

    /// <summary>
    /// The way a body faces (yaw about +Y, 0 = +Z) from its hips' turn from rest: the forward axis pitched back upright about the body's
    /// own sideways axis (forward.xz − forward.y · up.xz), so lying face down it is the way his head points and on his back the way his
    /// feet do (the way he faces once he sits or pushes up), and it never spins when the forward axis points up or down.
    /// </summary>
    public static Vector2 Heading(Quaternion turnFromRest)
    {
        var f = Vector3.Transform(Vector3.UnitZ, turnFromRest);
        var u = Vector3.Transform(Vector3.UnitY, turnFromRest);
        return new Vector2(f.X - f.Y * u.X, f.Z - f.Y * u.Z);
    }

    public static void WritePose(Span<float> dst, Vector3 leftFoot, Vector3 rightFoot, Vector3 leftVel, Vector3 rightVel, Vector3 hipVel)
    {
        Put(dst, FootPos, leftFoot); Put(dst, FootPos + 3, rightFoot);
        Put(dst, FootVel, leftVel); Put(dst, FootVel + 3, rightVel);
        Put(dst, HipVel, hipVel);
    }

    public static void WriteTrajectory(Span<float> dst, ReadOnlySpan<Vector2> pos, ReadOnlySpan<Vector2> dir)
    {
        for (int i = 0; i < 3; i++)
        {
            dst[TrajPos + 2 * i] = pos[i].X; dst[TrajPos + 2 * i + 1] = pos[i].Y;
            dst[TrajDir + 2 * i] = dir[i].X; dst[TrajDir + 2 * i + 1] = dir[i].Y;
        }
    }

    static void Put(Span<float> d, int i, Vector3 v) { d[i] = v.X; d[i + 1] = v.Y; d[i + 2] = v.Z; }

    /// <summary>A model-space vector in the character space of a root facing <paramref name="yaw"/>.</summary>
    public static Vector3 ToCharacter(Vector3 v, float yaw) => Vector3.Transform(v, Spring.Yaw(-yaw));

    public static Vector2 ToCharacter(Vector2 xz, float yaw)
    {
        var v = ToCharacter(new Vector3(xz.X, 0, xz.Y), yaw);
        return new Vector2(v.X, v.Z);
    }
}

/// <summary>
/// The query trajectory: where a body whose velocity and facing spring toward the wanted ones (critically damped) will be at the
/// feature times, relative to where it is now, in its character space.
/// </summary>
public static class Trajectory
{
    /// <param name="vel">current velocity (model space x, z)</param>
    /// <param name="acc">current acceleration</param>
    /// <param name="goalVel">wanted velocity</param>
    /// <param name="yaw">current facing (radians about +Y; 0 faces +Z)</param>
    /// <param name="yawVel">current turning speed</param>
    /// <param name="goalYaw">wanted facing</param>
    public static void Predict(Vector2 vel, Vector2 acc, Vector2 goalVel, float yaw, float yawVel, float goalYaw, float halfLife,
        Span<Vector2> pos, Span<Vector2> dir)
    {
        for (int i = 0; i < 3; i++)
        {
            float t = Features.TrajSeconds[i];
            Vector2 x = Vector2.Zero, v = vel, a = acc;
            Spring.Velocity(ref x, ref v, ref a, goalVel, halfLife, t);
            float y = yaw, w = yawVel;
            Spring.Angle(ref y, ref w, goalYaw, halfLife, t);
            pos[i] = Features.ToCharacter(x, yaw);
            float d = Spring.WrapPi(y - yaw);
            dir[i] = new Vector2(MathF.Sin(d), MathF.Cos(d));
        }
    }
}

/// <summary>
/// A clip's root: per frame the point on the ground under the hips and the way the body faces (<see cref="Features.Heading"/> of the
/// hips' turn from rest), both averaged over ±6 frames (0.1 s) so the hips' sway does not shake them. Root motion is the change of this root.
/// </summary>
public sealed class RootTrack
{
    public const int Smooth = 6;
    readonly Vector2[] _pos;
    readonly float[] _yaw;
    public bool Loop { get; }
    public int Fps { get; }

    public RootTrack(Clip clip, Skeleton sk)
    {
        int n = clip.FrameCount;
        Loop = clip.Loop; Fps = clip.Fps;
        var rawPos = new Vector2[n];
        var rawFwd = new Vector2[n];
        var restInv = Quaternion.Inverse(sk.RestGlobal[sk.Hips]);
        for (int f = 0; f < n; f++)
        {
            var h = clip.Hips(f);
            rawPos[f] = new Vector2(h.X, h.Z);
            var flat = Features.Heading(clip.Rotations(f)[sk.Hips] * restInv);
            rawFwd[f] = flat.LengthSquared() > 1e-8f ? Vector2.Normalize(flat) : f > 0 ? rawFwd[f - 1] : Vector2.UnitY;
        }
        _pos = new Vector2[n]; _yaw = new float[n];
        for (int f = 0; f < n; f++)
        {
            Vector2 p = Vector2.Zero, d = Vector2.Zero;
            int c = 0;
            for (int o = -Smooth; o <= Smooth; o++)
            {
                int g = Loop ? ((f + o) % n + n) % n : Math.Clamp(f + o, 0, n - 1);
                p += rawPos[g]; d += rawFwd[g]; c++;
            }
            _pos[f] = p / c;
            _yaw[f] = d.LengthSquared() > 1e-8f ? MathF.Atan2(d.X, d.Y) : MathF.Atan2(rawFwd[f].X, rawFwd[f].Y);
        }
    }

    public int FrameCount => _pos.Length;
    public Vector2 Position(int frame) => _pos[frame];
    public float Yaw(int frame) => _yaw[frame];

    /// <summary>The root at a time (seconds, clamped to the clip or wrapped if it loops), between the two nearest frames.</summary>
    public void Sample(float seconds, out Vector2 pos, out float yaw)
    {
        int n = _pos.Length;
        float f = seconds * Fps;
        int a, b; float u;
        if (Loop && n > 1)
        {
            f %= n; if (f < 0) f += n;
            a = (int)MathF.Floor(f); u = f - a; b = (a + 1) % n;
        }
        else
        {
            f = Math.Clamp(f, 0, n - 1);
            a = Math.Min((int)MathF.Floor(f), n - 1); u = f - a; b = Math.Min(a + 1, n - 1);
        }
        pos = Vector2.Lerp(_pos[a], _pos[b], u);
        yaw = Spring.WrapPi(_yaw[a] + Spring.WrapPi(_yaw[b] - _yaw[a]) * u);
    }

    /// <summary>The root's velocity at a time (model metres per second), in the clip's character space there.</summary>
    public Vector2 Velocity(float seconds)
    {
        float h = 2f / Fps;
        Sample(seconds - h, out var a, out _);
        Sample(seconds + h, out var b, out _);
        Sample(seconds, out _, out float yaw);
        float span = Loop ? 2f * h : Math.Min(seconds + h, (FrameCount - 1) / (float)Fps) - Math.Max(seconds - h, 0f);
        return span > 1e-6f ? Features.ToCharacter(b - a, yaw) / span : Vector2.Zero;
    }

    /// <summary>The root at <paramref name="frame"/> + <paramref name="ahead"/>; past the end of a one-shot clip it carries on at the last frame's velocity and facing.</summary>
    public void Ahead(int frame, int ahead, out Vector2 pos, out float yaw)
    {
        int n = _pos.Length, g = frame + ahead;
        if (Loop) { g %= n; pos = _pos[g]; yaw = _yaw[g]; return; }
        if (g < n) { pos = _pos[g]; yaw = _yaw[g]; return; }
        Vector2 v = n > 1 ? _pos[n - 1] - _pos[n - 2] : Vector2.Zero;
        pos = _pos[n - 1] + v * (g - (n - 1));
        yaw = _yaw[n - 1];
    }
}

/// <summary>
/// A clip's stride phase per frame, read from its foot contacts: each contact span's middle frame is that foot's mid-stance (0.5 for
/// the left foot, 1.5 for the right, as the simulation's stride clock has it; one step is 1), the phase running on unwrapped from
/// span to span and linearly between them, and on at the nearest span's rate before the first and after the last. A span cut by the
/// clip's start or end is left out when two whole ones remain. Null for a clip with fewer than two usable spans, or one whose feet
/// do not alternate (standing, a shuffle with both feet down).
/// </summary>
public static class GaitPhases
{
    public static float[]? Of(Clip clip)
    {
        int n = clip.FrameCount;
        var spans = new List<(float Mid, int Foot, bool Whole)>();
        for (int foot = 0; foot < 2; foot++)
        {
            byte bit = foot == 0 ? Clip.LeftFoot : Clip.RightFoot;
            int from = -1;
            for (int f = 0; f <= n; f++)
            {
                bool on = f < n && (clip.Contacts(f) & bit) != 0;
                if (on && from < 0) from = f;
                else if (!on && from >= 0)
                {
                    spans.Add(((from + f - 1) * 0.5f, foot, from > 0 && f < n));
                    from = -1;
                }
            }
        }
        if (spans.Count(s => s.Whole) >= 2) spans.RemoveAll(s => !s.Whole);
        if (spans.Count < 2) return null;
        spans.Sort((x, y) => x.Mid.CompareTo(y.Mid));
        var frames = new float[spans.Count];
        var phase = new float[spans.Count];
        for (int i = 0; i < spans.Count; i++)
        {
            frames[i] = spans[i].Mid;
            if (i == 0) { phase[0] = spans[0].Foot == 0 ? 0.5f : 1.5f; continue; }
            if (spans[i].Foot == spans[i - 1].Foot || !(frames[i] > frames[i - 1])) return null;
            phase[i] = phase[i - 1] + 1f;
        }
        var result = new float[n];
        for (int f = 0; f < n; f++)
        {
            int k = 0;
            while (k < frames.Length - 2 && f >= frames[k + 1]) k++;
            float u = (f - frames[k]) / (frames[k + 1] - frames[k]);
            result[f] = phase[k] + u * (phase[k + 1] - phase[k]);
        }
        return result;
    }
}

/// <summary>One clip inside a <see cref="PoseSet"/>: its frames are the set's frames <see cref="Start"/> .. <see cref="Start"/> + count − 1.</summary>
public sealed record PoseClip(Clip Clip, RootTrack Root, int Start, string Side, float Bias = 1f)
{
    public int End => Start + Clip.FrameCount;
    /// <summary>The frame of the set's target event in this clip (a save's contact, a get-up's "up"), or -1.</summary>
    public int TargetFrame { get; init; } = -1;
    /// <summary>Where the set's contact limb is at <see cref="TargetFrame"/> (model space); zero without one.</summary>
    public Vector3 LimbAtTarget { get; init; }
    /// <summary>A named event's frame in the clip, or -1.</summary>
    public int Event(string name) => Clip.Events.TryGetValue(name, out int f) ? f : -1;
    /// <summary>-1 for a clip moving to its left, +1 to its right, 0 for neither.</summary>
    public int SideSign => Side == "left" ? -1 : Side == "right" ? 1 : 0;
}

/// <summary>Where the gloves and the reaching foot are (shared by the pose database, the hand IK and the gallery's check).</summary>
public static class Limbs
{
    /// <summary>The glove's middle: this far (metres) past the wrist along the forearm.</summary>
    public const float GloveReach = 0.08f;

    public static Vector3 Glove(Vector3 elbow, Vector3 wrist)
    {
        var d = wrist - elbow;
        return d.LengthSquared() > 1e-10f ? wrist + Vector3.Normalize(d) * GloveReach : wrist;
    }

    /// <summary>The contact limb's point in a forward-kinematics pose (<paramref name="gp"/> model positions).</summary>
    public static Vector3 Point(Skeleton sk, ReadOnlySpan<Vector3> gp, ContactLimb limb)
    {
        Vector3 hips = gp[sk.Hips];
        if (limb == ContactLimb.ReachingFoot)
        {
            Vector3 l = gp[sk.Foot[0]], r = gp[sk.Foot[1]];
            return (l - hips).LengthSquared() >= (r - hips).LengthSquared() ? l : r;
        }
        Vector3 gl = Glove(gp[sk.Find("LeftForeArm")], gp[sk.Find("LeftHand")]);
        Vector3 gr = Glove(gp[sk.Find("RightForeArm")], gp[sk.Find("RightHand")]);
        if (limb == ContactLimb.Hands) return (gl + gr) * 0.5f;
        return (gl - hips).LengthSquared() >= (gr - hips).LengthSquared() ? gl : gr;
    }
}

/// <summary>
/// The frames of one chooser set and their features: raw, and normalised for the search (each group's values minus their mean, divided
/// by the group's standard deviation over the set and multiplied by its weight). Frames within 0.2 s of a one-shot clip's end are kept
/// (for the current frame's features) but never searched.
/// </summary>
public sealed class PoseSet
{
    public const float EndExclusionSeconds = 0.2f;

    public string Name { get; }
    public IReadOnlyList<PoseClip> Clips { get; }
    public int FrameCount { get; }
    readonly float[] _raw, _norm, _mean, _scale;
    readonly bool[] _searchable;
    readonly byte[] _contacts;
    readonly sbyte[] _side;
    readonly float[] _boneSpeed, _bias, _phase;

    /// <summary>The labels the set was built with (target event, contact limb, search bounds).</summary>
    public PoseSetOptions Options { get; }

    public PoseSet(string name, IReadOnlyList<(Clip Clip, string Side)> clips, Skeleton sk, FeatureWeights? weights = null)
        : this(name, clips.Select(c => (c.Clip, c.Side, 1f)).ToList(), sk, weights) { }

    /// <summary>A set whose clips carry a matcher cost factor each (1 = none; see <see cref="Bias"/>).</summary>
    public PoseSet(string name, IReadOnlyList<(Clip Clip, string Side, float Bias)> clips, Skeleton sk, FeatureWeights? weights = null,
        PoseSetOptions? options = null)
    {
        Name = name;
        weights ??= FeatureWeights.Default;
        Options = options ?? PoseSetOptions.None;
        var list = new List<PoseClip>();
        int start = 0;
        foreach (var (c, side, bias) in clips)
        {
            var pc = new PoseClip(c, new RootTrack(c, sk), start, side, bias);
            int tf = Options.TargetEvent != null && c.Events.TryGetValue(Options.TargetEvent, out int ev) ? Math.Clamp(ev, 0, c.FrameCount - 1) : -1;
            if (tf >= 0)
            {
                var gr = new Quaternion[sk.Count]; var gp = new Vector3[sk.Count];
                sk.Forward(c.Rotations(tf), c.Hips(tf), gr, gp);
                pc = pc with { TargetFrame = tf, LimbAtTarget = Limbs.Point(sk, gp, Options.Limb) };
            }
            list.Add(pc);
            start += c.FrameCount;
        }
        Clips = list; FrameCount = start;
        int F = Features.Count;
        _raw = new float[FrameCount * F]; _norm = new float[FrameCount * F];
        _searchable = new bool[FrameCount]; _contacts = new byte[FrameCount]; _boneSpeed = new float[FrameCount]; _bias = new float[FrameCount];
        _phase = new float[FrameCount];
        _side = new sbyte[FrameCount];
        foreach (var pc in list) { Extract(pc, sk); for (int f = pc.Start; f < pc.End; f++) { _bias[f] = pc.Bias; _side[f] = (sbyte)pc.SideSign; } }

        _mean = new float[F]; _scale = new float[F];
        if (FrameCount > 0)
        {
            var std = new double[F];
            for (int d = 0; d < F; d++)
            {
                double m = 0;
                for (int i = 0; i < FrameCount; i++) m += _raw[i * F + d];
                m /= FrameCount;
                double v = 0;
                for (int i = 0; i < FrameCount; i++) { double e = _raw[i * F + d] - m; v += e * e; }
                _mean[d] = (float)m; std[d] = Math.Sqrt(v / FrameCount);
            }
            for (int g = 0; g < Features.Groups.Length; g++)
            {
                var (s0, len) = Features.Groups[g];
                double gs = 0;
                for (int d = s0; d < s0 + len; d++) gs += std[d];
                gs /= len;
                float sc = Features.Weight(weights, g) / (float)Math.Max(gs, 1e-4);
                for (int d = s0; d < s0 + len; d++) _scale[d] = sc;
            }
            for (int i = 0; i < FrameCount; i++) Normalize(new ReadOnlySpan<float>(_raw, i * F, F), new Span<float>(_norm, i * F, F));
        }
    }

    void Extract(PoseClip pc, Skeleton sk)
    {
        var clip = pc.Clip;
        int n = clip.FrameCount, F = Features.Count, b = sk.Count;
        var gr = new Quaternion[b];
        var pos = new Vector3[n, 3];   // left ankle, right ankle, hips (model space)
        var gp = new Vector3[b];
        for (int f = 0; f < n; f++)
        {
            sk.Forward(clip.Rotations(f), clip.Hips(f), gr, gp);
            pos[f, 0] = gp[sk.Foot[0]]; pos[f, 1] = gp[sk.Foot[1]]; pos[f, 2] = gp[sk.Hips];
        }
        int tail = clip.Loop ? 0 : (int)MathF.Round(Options.EndExclusion * clip.Fps);
        Span<Vector3> vel = stackalloc Vector3[3];
        Span<Vector2> tp = stackalloc Vector2[3];
        Span<Vector2> td = stackalloc Vector2[3];
        var phases = GaitPhases.Of(clip);
        var ball = Options.BallTrack?.Invoke(clip);
        if (ball != null && ball.Count != n) ball = null;
        for (int f = 0; f < n; f++)
        {
            int a = clip.Loop ? (f - 1 + n) % n : Math.Max(f - 1, 0), c = clip.Loop ? (f + 1) % n : Math.Min(f + 1, n - 1);
            float span = clip.Loop ? 2f : Math.Max(1, c - a);
            for (int j = 0; j < 3; j++) vel[j] = n > 1 ? (pos[c, j] - pos[a, j]) * (clip.Fps / span) : Vector3.Zero;
            var root = pc.Root.Position(f);
            float yaw = pc.Root.Yaw(f);
            var r3 = new Vector3(root.X, 0, root.Y);
            var row = new Span<float>(_raw, (pc.Start + f) * F, F);
            Features.WritePose(row, Features.ToCharacter(pos[f, 0] - r3, yaw), Features.ToCharacter(pos[f, 1] - r3, yaw),
                Features.ToCharacter(vel[0], yaw), Features.ToCharacter(vel[1], yaw), Features.ToCharacter(vel[2], yaw));
            for (int i = 0; i < 3; i++)
            {
                pc.Root.Ahead(f, Features.TrajFrames[i], out var p, out float y);
                tp[i] = Features.ToCharacter(p - root, yaw);
                float d = Spring.WrapPi(y - yaw);
                td[i] = new Vector2(MathF.Sin(d), MathF.Cos(d));
            }
            Features.WriteTrajectory(row, tp, td);
            // the save point (the limb at the target frame, relative to the hips now) and the time to it, in this frame's character space
            if (pc.TargetFrame >= 0)
                Features.WriteSave(row, Features.ToCharacter(pc.LimbAtTarget - pos[f, 2], yaw), (pc.TargetFrame - f) / (float)clip.Fps);
            Features.WriteBody(row, sk, clip.Rotations(f)[sk.Hips], pos[f, 2].Y, yaw);
            _phase[pc.Start + f] = phases != null ? phases[f] : float.NaN;
            Features.WriteGait(row, _phase[pc.Start + f]);
            pc.Root.Ahead(f, Features.TurnFrames, out _, out float yawAhead);
            Features.WriteTurn(row, Spring.WrapPi(yawAhead - yaw));
            if (ball != null)
            {
                // relative to the root, and its velocity less the root's (both by the neighbouring frames)
                Vector3 bv = n > 1 ? (ball[c] - ball[a]) * (clip.Fps / span) : Vector3.Zero;
                var ra = pc.Root.Position(a); var rc = pc.Root.Position(c);
                Vector3 rv = n > 1 ? new Vector3(rc.X - ra.X, 0, rc.Y - ra.Y) * (clip.Fps / span) : Vector3.Zero;
                Features.WriteBall(row, Features.ToCharacter(ball[f] - r3, yaw), Features.ToCharacter(bv - rv, yaw));
            }
            int from = Options.SearchFrom != null && pc.Event(Options.SearchFrom) is int a0 && a0 >= 0 ? a0 : 0;
            int until = Options.SearchUntil != null && pc.Event(Options.SearchUntil) is int b0 && b0 >= 0 ? b0 : n;
            _searchable[pc.Start + f] = f < n - tail && f >= from && f < until;
            _contacts[pc.Start + f] = clip.Contacts(f);
        }
        // the fastest bone turn around each frame (radians per second at the clip's own rate, the most over ±3 frames)
        var turn = new float[n];
        for (int f = 0; f + 1 < n; f++)
        {
            var a = clip.Rotations(f); var c = clip.Rotations(f + 1);
            float m = 0;
            for (int k = 0; k < b; k++) m = Math.Max(m, 2f * MathF.Acos(Math.Min(1f, MathF.Abs(Quaternion.Dot(a[k], c[k])))));
            turn[f] = m * clip.Fps;
        }
        if (n > 1) turn[n - 1] = turn[n - 2];
        for (int f = 0; f < n; f++)
        {
            float m = 0;
            for (int o = -3; o <= 3; o++) m = Math.Max(m, turn[Math.Clamp(f + o, 0, n - 1)]);
            _boneSpeed[pc.Start + f] = m;
        }
    }

    /// <summary>A raw feature vector normalised the way this set's frames are.</summary>
    public void Normalize(ReadOnlySpan<float> raw, Span<float> dst)
    {
        for (int d = 0; d < Features.Count; d++) dst[d] = (raw[d] - _mean[d]) * _scale[d];
    }

    public ReadOnlySpan<float> Raw(int frame) => new(_raw, frame * Features.Count, Features.Count);
    public ReadOnlySpan<float> Normalized(int frame) => new(_norm, frame * Features.Count, Features.Count);
    public bool Searchable(int frame) => _searchable[frame];
    public byte Contacts(int frame) => _contacts[frame];
    /// <summary>The matcher's cost factor for a frame (its clip's match_bias).</summary>
    public float Bias(int frame) => _bias[frame];
    /// <summary>The side the frame's clip moves to: -1 left, +1 right, 0 neither.</summary>
    public int Side(int frame) => _side[frame];

    /// <summary>The stride phase of a frame (unwrapped along its clip, see <see cref="GaitPhases"/>), or NaN for a clip without a readable stride.</summary>
    public float GaitPhase(int frame) => _phase[frame];

    /// <summary>The fastest bone turn near a frame at the clip's own rate (radians per second).</summary>
    public float BoneSpeed(int frame) => _boneSpeed[frame];

    /// <summary>The clip holding set frame <paramref name="frame"/> and the frame within it.</summary>
    public PoseClip Locate(int frame, out int clipFrame)
    {
        // clips are few; a linear walk is plenty
        foreach (var pc in Clips)
            if (frame < pc.End) { clipFrame = frame - pc.Start; return pc; }
        throw new ArgumentOutOfRangeException(nameof(frame));
    }

    /// <summary>Index of a clip in <see cref="Clips"/> by name, or -1.</summary>
    public int IndexOf(string clipName)
    {
        for (int i = 0; i < Clips.Count; i++) if (Clips[i].Clip.Name == clipName) return i;
        return -1;
    }
}

/// <summary>
/// The pose database: one <see cref="PoseSet"/> per chooser set, built at game start from the clip library. Rejected clips are left
/// out; every kept clip enters with its mirror, so left and right share the recordings. A chooser set named "set:tag" holds the
/// set's clips that carry the tag; when none does, <see cref="Set"/> gives the whole set instead.
/// </summary>
public sealed class PoseDatabase
{
    readonly Dictionary<string, PoseSet> _sets = new();
    public Skeleton Skeleton { get; }

    public PoseDatabase(ClipLibrary library, IEnumerable<string> sets, FeatureWeights? weights = null, bool mirror = true)
        : this(library, sets, _ => weights, mirror) { }

    /// <summary>A database whose sets may weigh the features differently (<paramref name="weightsFor"/> gives a set's, or null for the default).</summary>
    public PoseDatabase(ClipLibrary library, IEnumerable<string> sets, Func<string, FeatureWeights?> weightsFor, bool mirror = true)
        : this(library, sets, weightsFor, _ => null, mirror) { }

    /// <summary>A database whose sets carry labels too (<paramref name="optionsFor"/>: target event, contact limb, search bounds).</summary>
    public PoseDatabase(ClipLibrary library, IEnumerable<string> sets, Func<string, FeatureWeights?> weightsFor,
        Func<string, PoseSetOptions?> optionsFor, bool mirror = true, IReadOnlyCollection<string>? drop = null)
    {
        Skeleton = library.Skeleton;
        var all = new List<string>();
        foreach (var set in sets)
        {
            all.Add(set);
            int c = set.IndexOf(':');
            if (c > 0) all.Add(set[..c]);
        }
        foreach (var set in all)
        {
            if (_sets.ContainsKey(set)) continue;
            var clips = new List<(Clip, string, float)>();
            int colon = set.IndexOf(':');
            string baseSet = colon < 0 ? set : set[..colon], tag = colon < 0 ? "" : set[(colon + 1)..];
            foreach (var e in library.Clips)
            {
                if (e.Set != baseSet || e.Verdict == ClipVerdict.Reject || (tag.Length > 0 && !e.HasTag(tag))) continue;
                if (drop != null && drop.Contains(baseSet)) continue;   // a set emptied on purpose (the fallback's test)
                clips.Add((e.Clip, e.Side, e.Bias));
                if (mirror) clips.Add((Mirror.Apply(e.Clip, library.Skeleton), Mirror.Side(e.Side), e.Bias));
            }
            _sets[set] = new PoseSet(set, clips, library.Skeleton, weightsFor(set), optionsFor(set));
        }
    }

    /// <summary>
    /// The set's frames, or null when the set is unknown or holds no frame (the caller then falls back). An empty "set:tag" gives
    /// the whole set.
    /// </summary>
    public PoseSet? Set(string? name)
    {
        if (name == null) return null;
        if (_sets.TryGetValue(name, out var s) && s.FrameCount > 0) return s;
        int colon = name.IndexOf(':');
        return colon > 0 ? Set(name[..colon]) : null;
    }

    /// <summary>The set exactly as named, or null when it is unknown or holds no frame (no fallback from "set:tag" to the set).</summary>
    public PoseSet? Exact(string? name) => name != null && _sets.TryGetValue(name, out var s) && s.FrameCount > 0 ? s : null;

    public IEnumerable<PoseSet> Sets => _sets.Values;
}
