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
    /// <summary>The save he went for, decided when he moved (None when the ball met him before he moved, or for a smother).</summary>
    public SaveType SaveType { get; set; } = SaveType.None;
    /// <summary>The difficulty of the save he went for (0 easy, 1 at full stretch, more moving the wrong way; NaN before he moved).</summary>
    public float Difficulty { get; set; } = float.NaN;
    /// <summary>How ready he was at the strike, 0..1 (set close to the shooter: his hull is big).</summary>
    public float Ready { get; set; }
    /// <summary>Reflex time drawn for the shot, seconds.</summary>
    public float ReflexS { get; set; } = float.NaN;
    /// <summary>The keeper's speed at the strike, m/s (NaN before a strike).</summary>
    public float SpeedAtStrikeMs { get; set; } = float.NaN;
    /// <summary>The keeper's distance off his goal line at the strike, metres (NaN before a strike).</summary>
    public float DepthAtStrikeM { get; set; } = float.NaN;
    /// <summary>The ball's distance from the goal centre at the strike, metres (NaN before a strike).</summary>
    public float BallDistanceAtStrikeM { get; set; } = float.NaN;
    /// <summary>True if the shot, flying on unhindered (no keeper, no frame), would cross the goal line inside the goal.</summary>
    public bool OnTarget { get; set; }
    /// <summary>What the keeper was doing at the strike (his action on the tick before it).</summary>
    public KeeperAction ActionAtStrike { get; set; } = KeeperAction.Set;
    /// <summary>True if the keeper was planted (set and still for the shot) at the strike.</summary>
    public bool PlantedAtStrike { get; set; }
    /// <summary>True if the keeper rushed out at the ball this round before the first strike.</summary>
    public bool Rushed { get; set; }
    /// <summary>True if the keeper charged an attacker on the ball (closing him down at rush speed) this round before the first strike.</summary>
    public bool Charged { get; set; }
    /// <summary>True if the keeper went down in a smother this round before the first strike.</summary>
    public bool Smothered { get; set; }
    /// <summary>True if the keeper went into a slide tackle or a poke at a ball outside his penalty area this round before the first strike.</summary>
    public bool Slid { get; set; }
    /// <summary>True if the keeper got to the ball (a smother, block or claim) this round before the first strike: won from a dribble.</summary>
    public bool WonBall { get; set; }
    /// <summary>True if the attacker took the ball past the keeper (goal-side of him, close by) this round before the first strike.</summary>
    public bool Rounded { get; set; }
    /// <summary>
    /// The keeper's distance off his goal line when an attacker on the ball first came within each of <see cref="DepthBands"/>
    /// of the goal centre before the first strike, metres (NaN for a band not reached).
    /// </summary>
    public float[] DepthAtBandM { get; set; } = NewBands();
    /// <summary>
    /// Times his movement off or toward his line reversed before the first strike: the sign of his depth speed changed, each
    /// direction held above 0.3 m/s for more than 0.1 s. <see cref="DepthReversalsFirst2S"/> counts those in the round's first 2 s.
    /// </summary>
    public int DepthReversals { get; set; }
    public int DepthReversalsFirst2S { get; set; }

    /// <summary>The attacker's distances from the goal centre at which <see cref="DepthAtBandM"/> is sampled, metres.</summary>
    public static readonly float[] DepthBands = { 20f, 15f, 10f, 6f };

    static float[] NewBands() { var a = new float[DepthBands.Length]; Array.Fill(a, float.NaN); return a; }
}

/// <summary>
/// The save the keeper went for, decided when he moved: a dive he expects to hold (Catch) or to push
/// away (Parry), one that cannot get there in time (FailedAttempt), or, when his full reaction would come too late, a reflex
/// save that gets to the ball (Reflex) or one that cannot quite (ReflexAttempt).
/// </summary>
public enum SaveType { None, Catch, Parry, Reflex, ReflexAttempt, FailedAttempt }

/// <summary>How the keeper played the ball when he met it (Slide: knocked away in a slide tackle outside his area).</summary>
public enum SaveKind { None, Catch, Parry, Block, TipOver, Fingertip, Smother, LooseReflex, Slide }

/// <summary>The defender's numbers for a round (only in a scenario with a defender).</summary>
public sealed class DefenderReport
{
    /// <summary>Auto tackles (no button) that took the ball.</summary>
    public int AutoWon { get; set; }
    /// <summary>Lunges (standing tackles) started, and those that met the ball.</summary>
    public int LungesTried { get; set; }
    public int LungesWon { get; set; }
    /// <summary>Slide tackles started, and those that met the ball.</summary>
    public int SlidesTried { get; set; }
    public int SlidesWon { get; set; }
    /// <summary>Shots (a struck ball in flight) that met his body, leg or foot.</summary>
    public int Blocks { get; set; }
    /// <summary>Shoulder barges that landed, and those he won (the attacker stumbled).</summary>
    public int Barges { get; set; }
    public int BargesWon { get; set; }
    /// <summary>Lunges or slides that met the attacker's body before the ball.</summary>
    public int Fouls { get; set; }
    /// <summary>Seconds his bot spent pressing (active).</summary>
    public float TimeActive { get; set; }
    /// <summary>True if he controlled a loose ball (the round ended DISPOSSESSED).</summary>
    public bool Collected { get; set; }
    /// <summary>How the ball he collected was won (None when it came loose another way, or was not collected).</summary>
    public TackleKind WonBy { get; set; }
}

/// <summary>The record of one round, available when the state reaches Report.</summary>
/// <summary>What the attacking pair did in a round with a team-mate.</summary>
public sealed class TeamReport
{
    /// <summary>Passes struck, and how many of them a team-mate's first touch received (a first-time shot or pass back is not a touch).</summary>
    public int Passes { get; init; }
    public int Received { get; init; }
    /// <summary>The round ended on an offside call.</summary>
    public bool Offside { get; init; }
    /// <summary>Times control moved to another player (on a pass, or to the team's claim holder on a loose ball).</summary>
    public int ControlChanges { get; init; }
}

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
    /// <summary>The defender's numbers; absent (and left out of the JSON) in a scenario without one.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DefenderReport? Defender { get; init; }

    /// <summary>The team's numbers; absent (and left out of the JSON) in a scenario without a team-mate.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TeamReport? Team { get; init; }

    /// <summary>The report as JSON, used to compare two runs exactly.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, DataLoader.Options);
}
