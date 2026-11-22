using System.Numerics;

namespace FootballSim.Anim;

/// <summary>What the attacker is doing, as far as picking a clip set goes.</summary>
public enum AttackerState { Loco, Skill, Shot }

/// <summary>
/// The attacker's chooser, the counterpart of <see cref="KeeperChooser"/>: his state picks one clip set and the matcher searches only
/// inside it. Three kinds of set: locomotion (the walk, jog, run and turn recordings together, with their mirrors), one dribble set per
/// skill move (its authored clips: standing and running entries, either foot, either side, each with the ball it was made with), and
/// the shots. Each kind weighs the features its own way (<see cref="WeightsFor"/>).
/// </summary>
public static class AttackerChooser
{
    public const string Loco = "loco", Shot = "shot", SkillPrefix = "skill:";

    /// <summary>The clip sets (clips.json "set") of the locomotion recordings that go into <see cref="Loco"/>: the ones with a stride all the way through.</summary>
    public static readonly string[] LocoClipSets = { "walk", "jog", "run", "turn" };

    /// <summary>The dribble set of a skill move ("ball_roll" gives "skill:ball_roll").</summary>
    public static string SkillSet(string move) => SkillPrefix + move;

    /// <summary>The set for a state: locomotion, the move's dribble set, or the shots.</summary>
    public static string SetFor(AttackerState state, string? move = null) => state switch
    {
        AttackerState.Skill => SkillSet(move ?? ""),
        AttackerState.Shot => Shot,
        _ => Loco,
    };

    /// <summary>
    /// A set's feature weights. Locomotion: the feet and hips for continuity, the trajectory, and on top the simulation's stride
    /// phase (strongly: the simulation's stride clock leads, and the foot it swings is the foot that plays the ball) and the turn
    /// demand (the stick's angle off the facing, so a turn recording is entered as the stick asks, before the body has turned). A
    /// dribble set: the pose and the speed he enters at, the ball's place and velocity relative to him, and the turn demand; no
    /// trajectory (the move's path is the simulation's) and no stride phase (the playing foot is given). The shots: the pose and
    /// entry speed and the time to the contact (the kicks were recorded without a ball, so the ball does not count).
    /// </summary>
    public static FeatureWeights WeightsFor(string set)
    {
        if (set == Loco) return LocoWeights;
        if (set == Shot) return ShotWeights;
        return SkillWeights;
    }

    static readonly FeatureWeights LocoWeights = new(FootPosition: 0.75f, FootVelocity: 1f, HipVelocity: 1f, TrajectoryPosition: 1f,
        TrajectoryFacing: 1.5f, GaitPhase: 2.5f, TurnDemand: 1.5f);
    static readonly FeatureWeights SkillWeights = new(FootPosition: 0.75f, FootVelocity: 0.5f, HipVelocity: 2f, TrajectoryPosition: 0f,
        TrajectoryFacing: 0f, TurnDemand: 1f, BallPosition: 1.5f, BallVelocity: 1f);
    static readonly FeatureWeights ShotWeights = new(FootPosition: 0.75f, FootVelocity: 0.5f, HipVelocity: 2f, TrajectoryPosition: 0f,
        TrajectoryFacing: 0f, SaveTime: 1f);

    /// <summary>
    /// A set's labels: a dribble set and the shots count down to their "contact" (the playing foot is the contact limb) and are
    /// entered before it; locomotion has none, and is never entered in a recording's last half second (the features' look-ahead).
    /// </summary>
    public static PoseSetOptions OptionsFor(string set) =>
        set == Loco ? new PoseSetOptions { EndExclusion = Features.TurnFrames / 60f }
        : new PoseSetOptions("contact", ContactLimb.ReachingFoot, SearchUntil: "contact");

    /// <summary>
    /// The locomotion set from the attacker's recordings (game/assets/anim/attacker): every kept clip of <see cref="LocoClipSets"/>
    /// with a readable stride, and its mirror. Null when none is left.
    /// </summary>
    public static PoseSet? BuildLoco(ClipLibrary library)
    {
        var clips = new List<(Clip, string, float)>();
        foreach (var e in library.Clips)
        {
            if (e.Verdict == ClipVerdict.Reject || Array.IndexOf(LocoClipSets, e.Set) < 0) continue;
            if (GaitPhases.Of(e.Clip) == null) continue;
            clips.Add((e.Clip, e.Side, e.Bias));
            clips.Add((Mirror.Apply(e.Clip, library.Skeleton), Mirror.Side(e.Side), e.Bias));
        }
        return clips.Count == 0 ? null : new PoseSet(Loco, clips, library.Skeleton, WeightsFor(Loco), OptionsFor(Loco));
    }

    /// <summary>
    /// The dribble sets: one per skill move, holding all of the move's clips (no mirrors: left and right are authored), each with its
    /// authored ball as the ball features.
    /// </summary>
    public static Dictionary<string, PoseSet> BuildSkills(SkillClipLibrary library)
    {
        var byClip = new Dictionary<Clip, SkillClip>();
        foreach (var c in library.Clips) byClip[c.Clip] = c;
        IReadOnlyList<Vector3>? Ball(Clip clip)
        {
            if (!byClip.TryGetValue(clip, out var sc)) return null;
            var track = new Vector3[clip.FrameCount];
            for (int f = 0; f < track.Length; f++) track[f] = sc.Ball(f / (float)clip.Fps);
            return track;
        }
        var sets = new Dictionary<string, PoseSet>();
        foreach (var move in library.Clips.Select(c => c.Move).Distinct())
        {
            string name = SkillSet(move);
            var clips = library.Clips.Where(c => c.Move == move).Select(c => (c.Clip, c.Side, 1f)).ToList();
            sets[name] = new PoseSet(name, clips, library.Skeleton, WeightsFor(name), OptionsFor(name) with { BallTrack = Ball });
        }
        return sets;
    }

    /// <summary>The shot set from the shots' clip library (every kept clip of set "shot"; left and right are separate clips).</summary>
    public static PoseSet? BuildShots(ClipLibrary library)
    {
        var clips = library.Clips.Where(e => e.Verdict != ClipVerdict.Reject && e.Set == Shot).Select(e => (e.Clip, e.Side, e.Bias)).ToList();
        return clips.Count == 0 ? null : new PoseSet(Shot, clips, library.Skeleton, WeightsFor(Shot), OptionsFor(Shot));
    }

    /// <summary>
    /// The raw query for entering a dribble set or the shots: the shown pose's feet (ankles relative to the root, character space)
    /// and their velocities, the hips' velocity (his entry speed along +Z), the ball relative to the root and its velocity relative
    /// to his, the turn demand (radians, positive toward his left), and the seconds to the contact.
    /// </summary>
    public static void EntryQuery(Span<float> query, Vector3 leftFoot, Vector3 rightFoot, Vector3 leftVel, Vector3 rightVel, Vector3 hipVel,
        Vector3 ball, Vector3 ballVel, float turnDemand, float secondsToContact)
    {
        query.Clear();
        Features.WritePose(query, leftFoot, rightFoot, leftVel, rightVel, hipVel);
        Features.WriteBall(query, ball, ballVel);
        Features.WriteTurn(query, turnDemand);
        Features.WriteSave(query, Vector3.Zero, secondsToContact);
    }

    /// <summary>
    /// The cost of entering clip <paramref name="clip"/> of <paramref name="set"/> at its first frame for a raw
    /// <paramref name="query"/> (see <see cref="EntryQuery"/>): the matcher's cost of that frame.
    /// </summary>
    public static float EntryCost(PoseSet set, int clip, ReadOnlySpan<float> query)
    {
        Span<float> norm = stackalloc float[Features.Count];
        set.Normalize(query, norm);
        return Matcher.Cost(set, set.Clips[clip].Start, norm);
    }

    /// <summary>
    /// The clip of <paramref name="set"/> to enter, among those <paramref name="allowed"/> lets through (the playing foot and the
    /// side are the simulation's): the one whose first frame costs least for the query; ties go to the earlier clip. -1 when none
    /// is allowed.
    /// </summary>
    public static int PickEntry(PoseSet set, ReadOnlySpan<float> query, Func<PoseClip, bool> allowed, out float cost)
    {
        int best = -1;
        cost = float.PositiveInfinity;
        for (int i = 0; i < set.Clips.Count; i++)
        {
            if (!allowed(set.Clips[i])) continue;
            float c = EntryCost(set, i, query);
            if (c < cost) { cost = c; best = i; }
        }
        return best;
    }

    /// <summary>
    /// The share of the second of two entries (a move's running clip against its standing one) from their entry costs. What both
    /// cost alike says nothing about which fits (a ball further off than either was made with, a pose neither starts from), so
    /// <see cref="CommonShare"/> of the cheaper one's cost is taken off both first; then all of the cheaper one when the other
    /// costs <see cref="BlendSpan"/> times as much or more, an even blend at equal costs, smooth between (so a pace between the
    /// two recordings still blends them, as the speed-only rule did, but by the whole query: pose, ball and stick as well as speed).
    /// </summary>
    public static float BlendShare(float costFirst, float costSecond)
    {
        if (!(costFirst > 0f) && !(costSecond > 0f)) return 0.5f;
        float common = CommonShare * MathF.Min(MathF.Max(costFirst, 0f), MathF.Max(costSecond, 0f));
        costFirst -= common; costSecond -= common;
        float sum = MathF.Max(costFirst + costSecond, 1e-9f);
        // 0 when the first costs nothing, 1 when the second does
        float share = costFirst / sum;
        float lo = 1f / (1f + BlendSpan), hi = BlendSpan / (1f + BlendSpan);
        float u = Math.Clamp((share - lo) / (hi - lo), 0f, 1f);
        return u * u * (3f - 2f * u);
    }

    /// <summary>
    /// <see cref="EntryQuery"/> from the pose shown: local rotations and offsets with the hips placed in the model's axes where
    /// the figure stands (<paramref name="root"/> on the ground under him, facing <paramref name="yaw"/>: model yaw, 0 faces +Z).
    /// With <paramref name="prevRot"/> (the pose a tick before) the feet's and hips' velocities are taken by difference, his own
    /// travel included; without it they move with him at <paramref name="speed"/> (model metres per second along his facing).
    /// </summary>
    public static void EntryQuery(Span<float> query, Skeleton sk, ReadOnlySpan<Quaternion> rot, ReadOnlySpan<Vector3> pos,
        ReadOnlySpan<Quaternion> prevRot, ReadOnlySpan<Vector3> prevPos, float dt, Vector3 root, float yaw, float speed, Vector3 ball,
        Vector3 ballVel, float turnDemand, float secondsToContact)
    {
        var gr = new Quaternion[sk.Count]; var gp = new Vector3[sk.Count];
        sk.Forward(rot, pos[0], gr, gp, pos);
        Vector3 lf = gp[sk.Foot[0]], rf = gp[sk.Foot[1]], hp = gp[sk.Hips];
        var run = new Vector3(0, 0, speed);
        Vector3 lv = run, rv = run, hv = run;
        if (!prevRot.IsEmpty && dt > 0f)
        {
            sk.Forward(prevRot, prevPos[0], gr, gp, prevPos);
            lv = Features.ToCharacter((lf - gp[sk.Foot[0]]) / dt, yaw);
            rv = Features.ToCharacter((rf - gp[sk.Foot[1]]) / dt, yaw);
            hv = Features.ToCharacter((hp - gp[sk.Hips]) / dt, yaw);
        }
        EntryQuery(query, Features.ToCharacter(lf - root, yaw), Features.ToCharacter(rf - root, yaw), lv, rv, hv, ball, ballVel, turnDemand, secondsToContact);
    }

    /// <summary>
    /// The share of <paramref name="second"/> against <paramref name="first"/> (two clips of <paramref name="set"/>, e.g. a move's
    /// standing and running entries) for a raw entry query: <see cref="BlendShare"/> of their entry costs. NaN when either clip is
    /// not in the set (the caller keeps its own rule).
    /// </summary>
    public static float EntryShare(PoseSet set, Clip first, Clip second, ReadOnlySpan<float> query)
    {
        int a = -1, b = -1;
        for (int i = 0; i < set.Clips.Count; i++)
        {
            if (ReferenceEquals(set.Clips[i].Clip, first)) a = i;
            if (ReferenceEquals(set.Clips[i].Clip, second)) b = i;
        }
        if (a < 0 || b < 0) return float.NaN;
        if (a == b) return 0f;
        // A pace beyond both recordings counts as the nearer one's own pace (he sprints faster than any clip was made at):
        // the query's forward speed is held between the two entries' own.
        Span<float> q = stackalloc float[Features.Count];
        query.CopyTo(q);
        int z = Features.HipVel + 2;
        float va = set.Raw(set.Clips[a].Start)[z], vb = set.Raw(set.Clips[b].Start)[z];
        q[z] = Math.Clamp(q[z], MathF.Min(va, vb), MathF.Max(va, vb));
        return BlendShare(EntryCost(set, a, q), EntryCost(set, b, q));
    }

    /// <summary>The cost ratio at which the dearer of two entries is left out altogether.</summary>
    public const float BlendSpan = 2f;
    /// <summary>The share of the cheaper entry's cost taken off both before they are compared.</summary>
    public const float CommonShare = 0.9f;
}
