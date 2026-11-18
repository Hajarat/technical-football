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
    /// <summary>Players per side. Only the first player of each side has behaviour; the rest stand still.</summary>
    public int TeamSize { get; set; } = 1;
    /// <summary>Where the ball starts, at rest.</summary>
    public StartSpot Ball { get; set; } = new() { X = 41.5f, Z = 0f };
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
    /// <summary>Seconds of Live time before TIME_UP, or null for no clock.</summary>
    public float? ClockLimit { get; set; }
    /// <summary>Distance from the goal centre at which the scripted attacker shoots. 0 means at once.</summary>
    public float ShootDistance { get; set; }
}
