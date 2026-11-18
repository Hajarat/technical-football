using System.Numerics;

namespace FootballSim.Core;

/// <summary>What a player is doing in the match.</summary>
public enum PlayerRole { Outfield, Keeper }

/// <summary>
/// The keeper's visible state (Phase D contract; the game's KeeperView animates from it with
/// <see cref="PlayerState.KeeperDive"/>, <see cref="PlayerState.KeeperActionTime"/>, Velocity, Facing and HandPosition).
/// Set: standing on the arc in stance, still or near-still. Shuffle: side-stepping/jogging along the arc to a new spot.
/// Rush: running out at the ball. Planted: the attacker is winding up a shot, keeper decelerated to still, knees bent.
/// Reacting: the ball is struck, keeper has not reacted yet. Diving: hands moving to the ball (see KeeperDive for the kind).
/// Smother: diving at the attacker's feet / loose ball during a rush. Holding: ball in his hands.
/// Grounded: lying after a dive, smother or block. GettingUp: rising from the ground (KeeperActionTime runs 0 to get-up time).
/// Backpedal: moving back toward his goal facing the play (closing down's retreat, recovering, or under a chip after it is
/// struck). Spread: stopped close to an attacker on the ball, made big (legs wide, arms low and out), before or during a windup.
/// Charge: running out at an attacker on the ball to close him down (not yet a race for a loose ball). Jockey: close to an
/// attacker on the ball, low and spread, stepping with him to stay between the ball and the goal, ready to pounce.
/// </summary>
public enum KeeperAction { Set, Shuffle, Rush, Planted, Reacting, Diving, Smother, Holding, Grounded, GettingUp, Backpedal, Spread, Charge, Jockey }

/// <summary>The kind of save motion while <see cref="KeeperAction.Diving"/> (None otherwise).
/// Low: diving along the ground. Side: a flat side dive at mid height. High: a leaping dive / tip over the bar.
/// Feet: a standing block at the feet (legs and body spread, no dive). Catch: a standing catch at the body, no dive.
/// Back: a backward leap after backpedalling under a chip, body extended and falling back toward his goal, reaching up and
/// back for the ball. The reflex saves, standing, made before his full reaction when that would come too late: ReflexHand, a
/// hand flicked out at the ball; ReflexFoot, a leg shot out at a low ball; BodyBlock, the ready (spread) body thrown sideways
/// at it, legs and arms wide.</summary>
public enum KeeperDive { None, Low, Side, High, Feet, Catch, Back, ReflexHand, ReflexFoot, BodyBlock }

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
/// (position = ball centre at contact, speed = arriving speed). SkillMove is a skill move starting (position = ball centre,
/// speed = the player's speed, <see cref="SimEvent.Skill"/> = the move); a move's sole contact also raises a Touch.
/// </remarks>
public enum EventKind { Touch, Strike, Bounce, FrameHit, Save, Goal, OutOfPlay, PossessionGained, PossessionLost, BoardHit, SkillMove }

/// <summary>
/// Shot flavour chosen by the modifiers held while shooting: chip wins; driven with finesse is a low finesse; then driven,
/// finesse, and power when none is held.
/// </summary>
public enum ShotType { Power, Finesse, Chip, Driven, LowFinesse }

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
/// <param name="Skill">For <see cref="EventKind.SkillMove"/>: the move that started; null otherwise.</param>
public readonly record struct SimEvent(EventKind Kind, Vector3 Position, float Speed, bool Held = false, Foot Foot = Foot.Right, int Team = -1,
    int PlayerIndex = -1, ActionKind? Skill = null);

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
