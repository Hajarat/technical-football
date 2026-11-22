using FootballSim.Core;

namespace FootballSim.Anim;

/// <summary>What the attacker's body does when the defender gets to him: nothing, losing his balance, pushed sideways, brought down.</summary>
public enum Reaction { None, Stumble, Barged, Fall }

/// <summary>
/// The defender's chooser: his action picks one clip set of game/assets/anim/defender and the matcher searches only inside it
/// (<see cref="KeeperMotion"/> plays it, as it plays the keeper's). An action with no set, or whose set has no kept clip, is drawn
/// by the outfield figure as before (the run cycles and the procedural defending poses), blended in and out. The attacker's
/// reactions to him (<see cref="Reaction"/>) are sets of the same folder.
/// </summary>
public static class DefenderChooser
{
    /// <summary>Moving in his stance with his facing held: side shuffles, dropping off, closing down.</summary>
    public const string Jockey = "jockey";
    /// <summary>Standing in his stance: the set clips tagged "idle".</summary>
    public const string SetIdle = "set:idle";
    public const string Lunge = "lunge", Block = "block", Barge = "barge", Slide = "slide", GetUp = KeeperChooser.GetUp;
    public const string Stumble = "stumble", Barged = "barged", Fall = "fall";

    /// <summary>Every set this chooser can return (the pose database is built for these).</summary>
    public static readonly string[] Sets = { Jockey, SetIdle, Lunge, Block, Barge, Slide, GetUp, Stumble, Barged, Fall };

    /// <summary>Below this speed (m/s) he stands in his stance; from <see cref="MoveSpeed"/> he steps. Between the two the last choice stays.</summary>
    public const float StandSpeed = 0.35f, MoveSpeed = 0.6f;

    /// <summary>
    /// The clip set for the defender's action, or null for one the outfield figure draws itself (running to his spot, pressing at a
    /// sprint, chasing a loose ball). <paramref name="moving"/> is whether he is stepping (see <see cref="Moving"/>).
    /// A slide's clip runs on through the lying that follows it, so Grounded keeps the slide's set.
    /// </summary>
    public static string? SetFor(DefenderAction action, bool jockeying, bool moving) => action switch
    {
        DefenderAction.Jockey => moving ? Jockey : SetIdle,
        DefenderAction.Hold or DefenderAction.Press => !moving ? SetIdle : jockeying ? Jockey : null,
        DefenderAction.Block => Block,
        DefenderAction.Lunge => Lunge,
        DefenderAction.Barge => Barge,
        DefenderAction.Slide or DefenderAction.Grounded => Slide,
        DefenderAction.GettingUp => GetUp,
        DefenderAction.Stumble => Stumble,
        _ => null,
    };

    /// <summary>The clip set for the attacker's reaction (null for none).</summary>
    public static string? SetFor(Reaction reaction) => reaction switch
    {
        Reaction.Stumble => Stumble,
        Reaction.Barged => Barged,
        Reaction.Fall => Fall,
        _ => null,
    };

    /// <summary>Whether he is stepping, with a dead band so a speed hovering at the limit does not flip between standing and stepping.</summary>
    public static bool Moving(float speed, bool was) => speed >= MoveSpeed || was && speed > StandSpeed;

    /// <summary>A set played through once it is entered, with no search while it plays: everything but the stance and its steps.</summary>
    public static bool OneShot(string? set) => set is Lunge or Block or Barge or Slide or GetUp or Stumble or Barged or Fall;

    /// <summary>A set that ends on the ground or rises from it: its switches blend slowly and no foot is held planted while he lies.</summary>
    public static bool IsGround(string? set) => set is Slide or GetUp or Fall;

    /// <summary>Whether the set keeps his chest square to his facing (he faces the ball carrier): the stance, its steps and the block.</summary>
    public static bool SquaresChest(string? set) => set is Jockey or SetIdle or Block;

    /// <summary>
    /// The feature weights of a set. Standing ignores the trajectory (he is going nowhere). A tackle, a block and the slide are
    /// matched by the feet and hips he has and the time to their contact (no save point: the foot's IK takes it to the ball, and a
    /// deep lunge's trailing foot is as far from the hips as its leading one, so the clip is never aimed by a limb). A get-up by the
    /// body's height and tilt and the time to "up". A reaction by the feet and hips he has when it starts.
    /// </summary>
    public static FeatureWeights? WeightsFor(string set) => set switch
    {
        SetIdle => new FeatureWeights(TrajectoryPosition: 0f, TrajectoryFacing: 0f),
        Lunge or Block or Slide => new FeatureWeights(TrajectoryFacing: 0f, SaveTime: 1.5f),
        GetUp => new FeatureWeights(TrajectoryPosition: 0f, TrajectoryFacing: 0f, SaveTime: 1.5f, Body: 2f),
        Stumble or Barged or Fall or Barge => new FeatureWeights(TrajectoryFacing: 0f),
        _ => null,
    };

    /// <summary>
    /// A set's labels: a tackle, a block and the slide count down to their "contact" with the reaching foot and are entered before
    /// it; a get-up counts down to "up"; a fall is entered before its "landing", a stumble and a barged clip before their "recover"
    /// (where he starts to catch himself), so none is entered as it ends.
    /// </summary>
    public static PoseSetOptions? OptionsFor(string set) => set switch
    {
        Lunge or Block or Slide => new PoseSetOptions("contact", ContactLimb.ReachingFoot, SearchUntil: "contact"),
        GetUp => new PoseSetOptions("up", SearchUntil: "up"),
        Fall => new PoseSetOptions(SearchUntil: "landing"),
        Stumble or Barged => new PoseSetOptions(SearchUntil: "recover"),
        _ => null,
    };
}
