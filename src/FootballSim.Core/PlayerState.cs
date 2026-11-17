using System.Numerics;

namespace FootballSim.Core;

/// <summary>One player's state, read by the game layer to draw the player.</summary>
public sealed class PlayerState
{
    /// <summary>0 or 1; team 0 attacks the goal at X = half length.</summary>
    public int Team { get; }
    /// <summary>Position in the team's list.</summary>
    public int Index { get; }
    /// <summary>Feet position on the ground (Y = 0).</summary>
    public Vector3 Position { get; internal set; }
    public Vector3 Velocity { get; internal set; }
    /// <summary>Radians, 0 = +X, positive turning toward +Z.</summary>
    public float Facing { get; internal set; }
    public PlayerRole Role { get; }
    public PlayerSheet Sheet { get; }
    /// <summary>For keepers: what the keeper is doing. Outfield players stay at Set.</summary>
    public KeeperAction KeeperAction { get; internal set; }
    /// <summary>For keepers: where the hands are this tick, so a view can lean the body. For outfield players, the feet position.</summary>
    public Vector3 HandPosition { get; internal set; }

    // ---- Contract for the on-the-ball rework (possession, feet). The game layer animates from these. ----

    /// <summary>True while this player controls the ball (dribbling it). False when the ball is loose or someone else's.</summary>
    public bool InPossession { get; internal set; }
    /// <summary>Seconds until this player's next expected touch of the ball, or NaN when none is expected.</summary>
    public float NextTouchEta { get; internal set; } = float.NaN;
    /// <summary>The foot expected to make that touch. Meaningful only when <see cref="NextTouchEta"/> is a number.</summary>
    public Foot NextTouchFoot { get; internal set; }
    /// <summary>Where the ball centre is expected to be at that touch.</summary>
    public Vector3 NextTouchPoint { get; internal set; }
    /// <summary>Seconds until a pending shot makes contact with the ball, or NaN when no strike is pending.</summary>
    public float StrikeEta { get; internal set; } = float.NaN;
    /// <summary>The foot that will strike, or struck last. Meaningful while charging, during the strike delay and just after.</summary>
    public Foot StrikeFoot { get; internal set; }
    /// <summary>The foot used for this player's most recent touch or strike.</summary>
    public Foot LastTouchFoot { get; internal set; }

    /// <summary>Horizontal speed, m/s.</summary>
    public float Speed => new Vector2(Velocity.X, Velocity.Z).Length();

    internal float LastTouchTime = -100f;
    internal float TurnRefHeading;
    internal float TurnRefTime;

    internal PlayerState(int team, int index, PlayerRole role, PlayerSheet sheet)
    {
        Team = team;
        Index = index;
        Role = role;
        Sheet = sheet;
    }
}
