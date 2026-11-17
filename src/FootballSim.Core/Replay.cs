using System.Text.Json;
using System.Text.Json.Serialization;

namespace FootballSim.Core;

/// <summary>A <see cref="TickIntent"/> in file form. Compact names keep a replay small.</summary>
public sealed class IntentRecord
{
    public float Mx { get; set; }
    public float Mz { get; set; }
    public float Sprint { get; set; }
    public bool Close { get; set; }
    public bool Shoot { get; set; }
    public bool Finesse { get; set; }
    public bool Chip { get; set; }
    /// <summary>Knock-on press. Absent in older replays, which read as false.</summary>
    public bool Knock { get; set; }
    // The skill-move inputs. Absent in older replays (they read as at rest) and left out of the file while at rest, so a
    // replay without skill input is written exactly as before.
    /// <summary>Right stick X.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float Rx { get; set; }
    /// <summary>Right stick Z.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public float Rz { get; set; }
    /// <summary>Cancel held.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Cancel { get; set; }
    /// <summary>Skill key pressed on this tick.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SkillKey Skill { get; set; }

    public static IntentRecord From(in TickIntent i) => new()
    {
        Mx = i.MoveX, Mz = i.MoveZ, Sprint = i.Sprint, Close = i.CloseControl, Shoot = i.ShootHeld, Finesse = i.Finesse, Chip = i.Chip, Knock = i.KnockOn,
        Rx = i.RightX, Rz = i.RightZ, Cancel = i.Cancel, Skill = i.SkillKey,
    };

    public TickIntent ToIntent() => new()
    {
        MoveX = Mx, MoveZ = Mz, Sprint = Sprint, CloseControl = Close, ShootHeld = Shoot, Finesse = Finesse, Chip = Chip, KnockOn = Knock,
        RightX = Rx, RightZ = Rz, Cancel = Cancel, SkillKey = Skill,
    };
}

/// <summary>
/// A recorded round: seed, who played, which scenario, a hash of the tuning, and every tick's intents from Begin onward.
/// Running it again with the same data reproduces the identical <see cref="RoundReport"/>.
/// </summary>
public sealed class Replay
{
    public int Version { get; set; } = 1;
    public ulong Seed { get; set; }
    public string AttackerId { get; set; } = "";
    public string KeeperId { get; set; } = "";
    public string ScenarioId { get; set; } = "";
    public string TuningHash { get; set; } = "";
    /// <summary>Intents per tick: both teams' players, team 0 first.</summary>
    public int PlayersPerTick { get; set; } = 2;
    /// <summary>Flat list: tick 0's players, then tick 1's, and so on.</summary>
    public List<IntentRecord> Intents { get; set; } = new();

    /// <summary>Number of recorded ticks.</summary>
    public int TickCount => PlayersPerTick > 0 ? Intents.Count / PlayersPerTick : 0;

    /// <summary>True if the replay was recorded under the same tuning.</summary>
    public bool MatchesTuning(Tuning t) => TuningHash == DataLoader.TuningHash(t);

    /// <summary>The replay as JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, DataLoader.Options);

    /// <summary>Writes the replay to a file.</summary>
    public void Save(string path) => File.WriteAllText(path, ToJson());

    /// <summary>Reads a replay from JSON. Returns null and an error message instead of throwing.</summary>
    public static Replay? FromJson(string json, out string? error)
    {
        try
        {
            var r = JsonSerializer.Deserialize<Replay>(json, DataLoader.Options);
            error = r == null ? "empty replay" : null;
            return r;
        }
        catch (JsonException e)
        {
            error = $"invalid replay JSON: {e.Message}";
            return null;
        }
    }

    /// <summary>Reads a replay from a file. Returns null and an error message instead of throwing.</summary>
    public static Replay? Load(string path, out string? error)
    {
        try { return FromJson(File.ReadAllText(path), out error); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = $"{path}: {e.Message}";
            return null;
        }
    }

    /// <summary>
    /// Plays the replay in a fresh world: Begin, then each recorded tick, then idle ticks until the report (capped).
    /// The sheets and scenario must be the ones named in the replay.
    /// </summary>
    public RoundReport Run(Tuning tuning, Scenario scenario, PlayerSheet attacker, PlayerSheet keeper)
    {
        var world = new World(tuning, scenario, attacker, keeper, Seed);
        world.Begin();
        int n = world.PlayerCount;
        var buf = new TickIntent[n];
        int per = Math.Max(1, PlayersPerTick);
        for (int tick = 0; tick < TickCount; tick++)
        {
            Array.Clear(buf);
            for (int p = 0; p < n && p < per; p++) buf[p] = Intents[tick * per + p].ToIntent();
            world.Step(buf);
        }
        Array.Clear(buf);
        int cap = (int)((tuning.Round.MaxSeconds + tuning.Round.OutcomeSeconds + 5f) * tuning.TickRate);
        for (int i = 0; i < cap && world.State != RoundState.Report; i++) world.Step(buf);
        return world.Report ?? throw new InvalidOperationException("Replay did not reach a report");
    }
}

/// <summary>Collects the intents of each simulated tick. Attach to <see cref="World.Recorder"/> before <see cref="World.Begin"/>.</summary>
public sealed class ReplayRecorder
{
    readonly Replay _replay;
    readonly int _players;

    /// <summary>Starts a recording for a world.</summary>
    public ReplayRecorder(World world, string attackerId, string keeperId, string scenarioId)
    {
        _players = world.PlayerCount;
        _replay = new Replay
        {
            Seed = world.Seed,
            AttackerId = attackerId,
            KeeperId = keeperId,
            ScenarioId = scenarioId,
            TuningHash = DataLoader.TuningHash(world.Tuning),
            PlayersPerTick = _players,
        };
    }

    /// <summary>Records one tick's intents. Called by <see cref="World.Step"/>.</summary>
    public void Record(ReadOnlySpan<TickIntent> intents)
    {
        for (int i = 0; i < _players; i++)
            _replay.Intents.Add(IntentRecord.From(i < intents.Length ? intents[i] : default));
    }

    /// <summary>The recording so far.</summary>
    public Replay Replay => _replay;
}
