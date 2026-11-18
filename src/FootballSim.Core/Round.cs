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
    /// <summary>How the ball was played when the keeper met it (None when he did not).</summary>
    public SaveKind Save { get; set; } = SaveKind.None;
    /// <summary>The save motion of the dive for this shot (None before he reacted, or for a smother).</summary>
    public KeeperDive Dive { get; set; } = KeeperDive.None;
    /// <summary>The keeper's speed at the strike, m/s (NaN before a strike).</summary>
    public float SpeedAtStrikeMs { get; set; } = float.NaN;
    /// <summary>The keeper's distance off his goal line at the strike, metres (NaN before a strike).</summary>
    public float DepthAtStrikeM { get; set; } = float.NaN;
    /// <summary>The ball's distance from the goal centre at the strike, metres (NaN before a strike).</summary>
    public float BallDistanceAtStrikeM { get; set; } = float.NaN;
    /// <summary>True if the shot, flying on unhindered (no keeper, no frame), would cross the goal line inside the goal.</summary>
    public bool OnTarget { get; set; }
    /// <summary>True if the keeper was planted (set and still for the shot) at the strike.</summary>
    public bool PlantedAtStrike { get; set; }
    /// <summary>True if the keeper rushed out at the ball this round before the first strike.</summary>
    public bool Rushed { get; set; }
    /// <summary>True if the keeper went down in a smother this round before the first strike.</summary>
    public bool Smothered { get; set; }
}

/// <summary>How the keeper played the ball when he met it.</summary>
public enum SaveKind { None, Catch, Parry, Block, TipOver, Fingertip, Smother }

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
