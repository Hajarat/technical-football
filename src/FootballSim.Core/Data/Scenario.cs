namespace FootballSim.Core;

/// <summary>
/// Arena size and goal size, as data. X runs from the back wall (0) to <see cref="Length"/> (the goal line, in the goal-end wall);
/// Z spans <see cref="Width"/> centred on 0. The corner shapes and boards are in the tuning's arena block.
/// </summary>
public sealed class PitchSpec
{
    public float Length { get; set; } = 52.5f;
    public float Width { get; set; } = 68f;
    public float GoalWidth { get; set; } = 7.32f;
    public float GoalHeight { get; set; } = 2.44f;
    /// <summary>Depth of the penalty area from the goal line, metres.</summary>
    public float PenaltyAreaDepth { get; set; } = 16.5f;
    /// <summary>Width of the penalty area, metres; in an arena narrower than this the area's sides are the side walls.</summary>
    public float PenaltyAreaWidth { get; set; } = 40.32f;
}

/// <summary>A start position on the ground, in metres.</summary>
public sealed class StartSpot
{
    public float X { get; set; }
    public float Z { get; set; }
    /// <summary>Facing in degrees, 0 = +X, positive turning toward +Z. Used for the attacker.</summary>
    public float FacingDeg { get; set; }
}

/// <summary>A start spot and its rules, loaded from data/scenarios.</summary>
public sealed class Scenario
{
    public string Id { get; set; } = "scenario";
    public string Name { get; set; } = "Scenario";
    public string DefaultAttacker { get; set; } = "striker_a";
    public string DefaultKeeper { get; set; } = "the_wall";
    public PitchSpec Pitch { get; set; } = new();
    /// <summary>
    /// Players per side. Without <see cref="Mate"/>, only the first player of each side has behaviour and the rest stand still.
    /// With it, this is the number of attackers: the one the intents drive and his team-mates (played by the team-mate bot);
    /// the other side is then the keeper and the defenders only.
    /// </summary>
    public int TeamSize { get; set; } = 1;
    /// <summary>
    /// Where the attacker's team-mate starts (team 0, player 1), facing the goal; null (the default) for no team-mate. Needs a
    /// <see cref="TeamSize"/> of 2 or more; a third attacker and on start beside him.
    /// </summary>
    public StartSpot? Mate { get; set; }
    /// <summary>The team-mate's sheet when none is chosen.</summary>
    public string DefaultMate { get; set; } = "team_mate";
    /// <summary>Offside is called (see Offside): a pass to a team-mate beyond the line at its strike ends the round.</summary>
    public bool Offside { get; set; }
    /// <summary>Where the ball starts (at rest unless <see cref="BallSpeed"/> is set).</summary>
    public StartSpot Ball { get; set; } = new() { X = 41.5f, Z = 0f };
    /// <summary>The ball's rolling speed when play starts, m/s, along <see cref="BallHeadingDeg"/>.</summary>
    public float BallSpeed { get; set; }
    /// <summary>The direction the ball rolls at the start, degrees (0 = +X toward the goal, positive turning toward +Z).</summary>
    public float BallHeadingDeg { get; set; }
    /// <summary>
    /// The ball starts as a lost ball (as after a heavy touch or a parry) instead of a ball on the spot: the attacker goes for it
    /// by himself from the first tick (see <see cref="LockOn"/>).
    /// </summary>
    public bool BallLoose { get; set; }
    /// <summary>Where the attacker starts.</summary>
    public StartSpot Attacker { get; set; } = new() { X = 40.9f, Z = 0f, FacingDeg = 0f };
    /// <summary>The keeper starts on his goal line instead of on his arc. He then plays as usual unless <see cref="KeeperHoldLine"/>.</summary>
    public bool KeeperOnLine { get; set; }
    /// <summary>
    /// Penalties: the keeper stays on his goal line until the strike (he may shuffle along it, but does not come off it or rush).
    /// After the strike, or once the taker dribbles the ball off the spot instead of shooting, he plays as usual.
    /// </summary>
    public bool KeeperHoldLine { get; set; }
    /// <summary>Tests: with <see cref="KeeperHoldLine"/>, the keeper stays on his line even when the attacker dribbles.</summary>
    public bool KeeperHoldLineThroughDribble { get; set; }
    /// <summary>
    /// Where the keeper starts: this far off his goal line on the line from the ball to the goal centre, metres. Null starts him
    /// on his set spot. He then moves as usual.
    /// </summary>
    public float? KeeperOffLine { get; set; }
    /// <summary>Outfield defenders on the keeper's side: 0 or 1. The defender is team 1, player 1, and is played by the defender bot.</summary>
    public int Defenders { get; set; }
    /// <summary>Where the defender starts. He faces the attacker.</summary>
    public StartSpot Defender { get; set; } = new() { X = 16f, Z = 0f };
    /// <summary>The defender's sheet when none is chosen.</summary>
    public string DefaultDefender { get; set; } = "centre_back";
    /// <summary>Seconds of Live time before TIME_UP, or null for no clock.</summary>
    public float? ClockLimit { get; set; }
    /// <summary>Distance from the goal centre at which the scripted attacker shoots. 0 means at once.</summary>
    public float ShootDistance { get; set; }
    /// <summary>
    /// The scripted attacker's intended strike distance from the goal, metres (0: none): running in, it presses shoot only once
    /// the ball, carried on at his speed through the wind-up, will be this close at the strike. The scoring bands were measured
    /// at these distances.
    /// </summary>
    public float StrikeDistance { get; set; }
}
