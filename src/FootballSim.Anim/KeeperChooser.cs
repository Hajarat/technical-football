using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Anim;

/// <summary>How he lies: judged from the pelvis of the last pose shown (its forward axis down, level or up).</summary>
public enum Lying { Front, Side, Back }

/// <summary>
/// The chooser (spec appendix F): the keeper's state picks one clip set and the matcher searches only inside it. A set with no kept
/// clips (the database gives null) is drawn procedurally, blended in and out.
/// </summary>
public static class KeeperChooser
{
    public const string Stance = "stance", Backpedal = "backpedal", Run = "run";
    /// <summary>Standing still: the stance clips tagged "idle" (the whole stance set when none is).</summary>
    public const string StanceIdle = "stance:idle";
    public const string DiveLow = "dive_low", DiveSide = "dive_side", DiveHigh = "dive_high", Feet = "feet", Catch = "catch",
        BackLeap = "back_leap", ReflexHand = "reflex_hand", ReflexFoot = "reflex_foot", BodyBlock = "body_block", Smother = "smother",
        Slide = "slide", Hold = "hold", LieFront = "lie_front", LieSide = "lie_side", LieBack = "lie_back", GetUp = "getup";
    /// <summary>The two halves of a hybrid save (spec "What CMU covers"): a CMU take-off, then the flight baked from the procedural pose.</summary>
    public const string TakeoffTag = "takeoff", FlightTag = "flight";

    static readonly string[] Chains = { DiveLow, DiveSide, DiveHigh, Smother };

    /// <summary>Every set this chooser can return, with the halves of the hybrid ones (the pose database is built for these).</summary>
    public static readonly string[] Sets = BuildSets();

    static string[] BuildSets()
    {
        var l = new List<string> { StanceIdle, Stance, Backpedal, Run, Feet, Catch, BackLeap, ReflexHand, ReflexFoot, BodyBlock, Slide, Hold,
            LieFront, LieSide, LieBack, GetUp };
        foreach (var c in Chains) { l.Add(Takeoff(c)); l.Add(Flight(c)); }
        return l.ToArray();
    }

    /// <summary>True for a hybrid set: it plays its take-off clips (tag "takeoff") to their take-off event, then its baked flight (tag "flight").</summary>
    public static bool IsChain(string? set) => set != null && Array.IndexOf(Chains, set) >= 0;
    public static string Takeoff(string set) => set + ":" + TakeoffTag;
    public static string Flight(string set) => set + ":" + FlightTag;
    public static bool IsGround(string? set) => set is LieFront or LieSide or LieBack or GetUp;

    static string Base(string set) { int c = set.IndexOf(':'); return c < 0 ? set : set[..c]; }

    /// <summary>
    /// The feature weights of a set: spec appendix E's, except that standing still ignores the trajectory (he is not going anywhere,
    /// and matching it made the ready stance hunt for its stillest half second instead of playing through its sway); save sets add the
    /// save point and the time to contact; a take-off is matched by the feet and hips and by the time to its take-off event (the time left before the flight's lead); lying and getting
    /// up are matched by the body's height and tilt (front, side or back) instead of a trajectory, a get-up also by the time to "up".
    /// </summary>
    public static FeatureWeights? WeightsFor(string set)
    {
        if (set == StanceIdle) return IdleWeights;
        if (set.EndsWith(":" + TakeoffTag)) return TakeoffWeights;
        if (set.EndsWith(":" + FlightTag)) return FlightWeights;
        return Base(set) switch
        {
            Feet or Catch or BackLeap or ReflexHand or ReflexFoot or BodyBlock or Slide or DiveLow or DiveSide or DiveHigh or Smother => FeatureWeights.Save,
            LieFront or LieSide or LieBack => LieWeights,
            GetUp => GetUpWeights,
            _ => null,
        };
    }

    static readonly FeatureWeights IdleWeights = new(TrajectoryPosition: 0f, TrajectoryFacing: 0f);
    static readonly FeatureWeights TakeoffWeights = new(TrajectoryPosition: 0f, TrajectoryFacing: 0f, SaveTime: 3f);
    static readonly FeatureWeights FlightWeights = new(TrajectoryPosition: 0f, TrajectoryFacing: 0f, SavePoint: 2f, SaveTime: 1.5f);
    static readonly FeatureWeights LieWeights = new(TrajectoryPosition: 0f, TrajectoryFacing: 0f, Body: 2f);
    static readonly FeatureWeights GetUpWeights = new(TrajectoryPosition: 0f, TrajectoryFacing: 0f, SaveTime: 1.5f, Body: 2f);

    /// <summary>
    /// A set's labels: a save set's time counts down to its "contact" and is entered before it (a flight from its take-off on); the
    /// contact limb is both hands, the reaching hand (reflex hand) or the reaching foot (reflex foot, feet, slide); a take-off counts down to and is entered
    /// before its take-off event; lying from a landing on; a get-up counts down to "up" and is entered before it.
    /// </summary>
    public static PoseSetOptions? OptionsFor(string set)
    {
        if (set.EndsWith(":" + TakeoffTag)) return new PoseSetOptions("takeoff", SearchUntil: "takeoff");
        if (set.EndsWith(":" + FlightTag)) return new PoseSetOptions("contact", ContactLimb.Hands, SearchFrom: "takeoff", SearchUntil: "contact");
        return Base(set) switch
        {
            ReflexHand => new PoseSetOptions("contact", ContactLimb.ReachingHand, SearchUntil: "contact"),
            ReflexFoot or Feet or Slide => new PoseSetOptions("contact", ContactLimb.ReachingFoot, SearchUntil: "contact"),
            Catch or BackLeap or BodyBlock or DiveLow or DiveSide or DiveHigh or Smother => new PoseSetOptions("contact", ContactLimb.Hands, SearchUntil: "contact"),
            LieFront or LieSide or LieBack => new PoseSetOptions(SearchFrom: "landing"),
            GetUp => new PoseSetOptions("up", SearchUntil: "up"),
            _ => null,
        };
    }

    /// <summary>
    /// The clip set for a state, or null for a state drawn procedurally. <paramref name="lying"/> is how the last clip left him and
    /// <paramref name="onGround"/> whether he is down (a dive, smother or slide took him there), for holding the ball where he lies.
    /// </summary>
    public static string? SetFor(KeeperAction action, KeeperDive dive, Lying lying = Lying.Side, bool onGround = false) => action switch
    {
        // standing in his stance: the ready-stance (idle) clips, which hold still and loop through their sway
        KeeperAction.Set or KeeperAction.Planted or KeeperAction.Reacting => StanceIdle,
        // moving in his stance: the matcher steps from the trajectory
        KeeperAction.Shuffle or KeeperAction.Spread or KeeperAction.Jockey => Stance,
        KeeperAction.Backpedal => Backpedal,
        KeeperAction.Rush or KeeperAction.Charge => Run,
        KeeperAction.Diving => dive switch
        {
            KeeperDive.Low => DiveLow,
            KeeperDive.High => DiveHigh,
            KeeperDive.Feet => Feet,
            KeeperDive.Catch => Catch,
            KeeperDive.Back => BackLeap,
            KeeperDive.ReflexHand => ReflexHand,
            KeeperDive.ReflexFoot => ReflexFoot,
            KeeperDive.BodyBlock => BodyBlock,
            _ => DiveSide,
        },
        KeeperAction.Smother => Smother,
        KeeperAction.Slide => Slide,
        KeeperAction.Holding => onGround ? LieSet(lying) : Hold,
        KeeperAction.Grounded => LieSet(lying),
        KeeperAction.GettingUp => GetUp,
        _ => null,
    };

    public static string LieSet(Lying lying) => lying switch { Lying.Front => LieFront, Lying.Back => LieBack, _ => LieSide };

    /// <summary>Whether a clip-driven state keeps his chest square to his facing: the stance and movement states but the run.</summary>
    public static bool SquaresChest(KeeperAction action) => action is KeeperAction.Set or KeeperAction.Planted or KeeperAction.Reacting
        or KeeperAction.Shuffle or KeeperAction.Spread or KeeperAction.Jockey or KeeperAction.Backpedal;

    /// <summary>
    /// How much of the stance layer (the procedural "made big" stance: wide feet, arms out low) a state takes over its clip: all of it
    /// spread, part of it jockeying (he still steps with the attacker), none otherwise.
    /// </summary>
    public static float StanceWeight(KeeperAction action) => action switch
    {
        KeeperAction.Spread => 1f,
        KeeperAction.Jockey => 0.6f,
        _ => 0f,
    };

    /// <summary>
    /// Which side of the keeper a point is on, by his facing (radians, 0 = +X, positive toward +Z, the simulation's convention):
    /// +1 on his right, −1 on his left, 0 straight ahead or behind. Never by world Z, so it holds whichever way he faces.
    /// </summary>
    public static int SideOf(Vector3 point, Vector3 position, float facing)
    {
        Vector3 right = new(-MathF.Sin(facing), 0, MathF.Cos(facing));
        float s = Vector3.Dot(point - position, right);
        return s > 1e-4f ? 1 : s < -1e-4f ? -1 : 0;
    }
}
