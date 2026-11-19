using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Anim;

/// <summary>
/// The chooser (spec appendix F): the keeper's state picks one clip set and the matcher searches only inside it. This milestone serves
/// his stance and movement states from clips; every other state returns null and keeps the procedural pose.
/// </summary>
public static class KeeperChooser
{
    public const string Stance = "stance", Backpedal = "backpedal", Run = "run";
    /// <summary>Standing still: the stance clips tagged "idle" (the whole stance set when none is).</summary>
    public const string StanceIdle = "stance:idle";

    /// <summary>Every set this chooser can return (the pose database is built for these).</summary>
    public static readonly string[] Sets = { StanceIdle, Stance, Backpedal, Run };

    /// <summary>
    /// The feature weights of a set: spec appendix E's, except that standing still ignores the trajectory (he is not going anywhere,
    /// and matching it made the ready stance hunt for its stillest half second instead of playing through its sway): the idle clip
    /// plays on and, at its end, jumps to the frame whose pose fits.
    /// </summary>
    public static FeatureWeights? WeightsFor(string set) => set == StanceIdle ? IdleWeights : null;

    static readonly FeatureWeights IdleWeights = new(TrajectoryPosition: 0f, TrajectoryFacing: 0f);

    /// <summary>The clip set for a state, or null for a state drawn procedurally.</summary>
    public static string? SetFor(KeeperAction action, KeeperDive dive) => action switch
    {
        // standing in his stance: the ready-stance (idle) clips, which hold still and loop through their sway
        KeeperAction.Set or KeeperAction.Planted or KeeperAction.Reacting => StanceIdle,
        // moving in his stance: the matcher steps from the trajectory
        KeeperAction.Shuffle or KeeperAction.Spread or KeeperAction.Jockey => Stance,
        KeeperAction.Backpedal => Backpedal,
        KeeperAction.Rush or KeeperAction.Charge => Run,
        _ => null,
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
