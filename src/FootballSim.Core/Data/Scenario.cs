namespace FootballSim.Core;

/// <summary>Pitch size and goal size, as data. The pitch is a half: X runs from 0 to <see cref="HalfLength"/> (the goal line).</summary>
public sealed class PitchSpec
{
    public float HalfLength { get; set; } = 52.5f;
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
    /// <summary>True for penalties: the keeper stands on the goal line instead of in front of it.</summary>
    public bool KeeperOnLine { get; set; }
    /// <summary>Seconds of Live time before TIME_UP, or null for no clock.</summary>
    public float? ClockLimit { get; set; }
    /// <summary>Distance from the goal centre at which the scripted attacker shoots. 0 means at once.</summary>
    public float ShootDistance { get; set; }
}
