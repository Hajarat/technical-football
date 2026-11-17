using System.Numerics;

namespace FootballSim.Core;

/// <summary>What a player is doing in the match.</summary>
public enum PlayerRole { Outfield, Keeper }

/// <summary>The keeper's visible state, so a view can lean the body.</summary>
public enum KeeperAction { Set, Reacting, Diving, Holding }

/// <summary>The round state machine ("Round contract" in the spec).</summary>
public enum RoundState { Setup, Live, Strike, Flight, Outcome, Report }

/// <summary>
/// How a round ended. <see cref="None"/> while it is still running. In the arena a round ends only as Goal, Held or TimeUp;
/// Wide and Over are kept for scripted shot results and older reports, Out only marks a round stopped because a value became
/// not-a-number. Claimed is reserved and not produced yet.
/// </summary>
public enum Outcome { None, Goal, Held, Wide, Over, Claimed, Out, TimeUp }

/// <summary>Kinds of event raised during one <c>World.Step</c>.</summary>
/// <remarks>
/// OutOfPlay is no longer raised: the arena's boards keep the ball in play. BoardHit is the ball meeting a board or the ceiling
/// (position = ball centre at contact, speed = arriving speed).
/// </remarks>
public enum EventKind { Touch, Strike, Bounce, FrameHit, Save, Goal, OutOfPlay, PossessionGained, PossessionLost, BoardHit }

/// <summary>Shot flavour chosen by the bumpers.</summary>
public enum ShotType { Normal, Finesse, Chip }

/// <summary>Which foot strikes.</summary>
public enum Foot { Left, Right }

/// <summary>An event raised by the simulation, for sound, particles or logging.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Position">Where, in world coordinates.</param>
/// <param name="Speed">Ball speed at the moment, m/s.</param>
/// <param name="Held">For <see cref="EventKind.Save"/>: true if the keeper caught it, false if it was parried.</param>
/// <param name="Foot">For Touch and Strike: the foot that made contact.</param>
/// <param name="Team">For player events (Touch, Strike, PossessionGained, PossessionLost): the player's team, else -1.</param>
/// <param name="PlayerIndex">For player events: the player's index in the team, else -1.</param>
public readonly record struct SimEvent(EventKind Kind, Vector3 Position, float Speed, bool Held = false, Foot Foot = Foot.Right, int Team = -1, int PlayerIndex = -1);

/// <summary>Helpers for <see cref="Outcome"/>.</summary>
public static class OutcomeExtensions
{
    /// <summary>The spec's upper-case label, for example <c>TIME_UP</c>.</summary>
    public static string Label(this Outcome o) => o switch
    {
        Outcome.TimeUp => "TIME_UP",
        _ => o.ToString().ToUpperInvariant(),
    };
}

/// <summary>Rating helpers.</summary>
public static class Rating
{
    /// <summary>r01(x) = (x - 1) / 98, clamped to 0..1.</summary>
    public static float R01(float rating) => Math.Clamp((rating - 1f) / 98f, 0f, 1f);
}
