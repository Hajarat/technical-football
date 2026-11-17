using System.Text.Json;

namespace FootballSim.Core;

/// <summary>What the keeper did about the most recent shot.</summary>
public enum KeeperResult { None, Held, Parried, Beaten }

/// <summary>The keeper's numbers for a shot: reaction time, distance needed, reach at arrival, margin and the action taken.</summary>
public sealed class KeeperReport
{
    /// <summary>Reaction time drawn for the shot, seconds.</summary>
    public float ReactionS { get; set; } = float.NaN;
    /// <summary>Distance from the keeper's reach centre to where the ball arrived, metres.</summary>
    public float NeededM { get; set; } = float.NaN;
    /// <summary>How far the hands had reached at that moment, metres.</summary>
    public float ReachM { get; set; } = float.NaN;
    /// <summary>Reach minus distance needed: positive means the keeper could get there.</summary>
    public float MarginM { get; set; } = float.NaN;
    public KeeperResult Action { get; set; } = KeeperResult.None;
}

/// <summary>The record of one round, available when the state reaches Report.</summary>
public sealed class RoundReport
{
    public Outcome Outcome { get; init; }
    public ulong Seed { get; init; }
    /// <summary>Ticks from Begin until the outcome was decided.</summary>
    public int Ticks { get; init; }
    /// <summary>Seconds of Live time when the outcome was decided.</summary>
    public float Clock { get; init; }
    public IReadOnlyList<ShotResult> Shots { get; init; } = Array.Empty<ShotResult>();
    public KeeperReport Keeper { get; init; } = new();

    /// <summary>The report as JSON, used to compare two runs exactly.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, DataLoader.Options);
}
