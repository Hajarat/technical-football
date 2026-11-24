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
    /// <summary>Driven-shot modifier held. Absent in older replays (read as false) and left out of the file while released.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Driven { get; set; }
    // The defending buttons. Absent in replays without a defender and left out of the file while released.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Jockey { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Tackle { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Slide { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Barge { get; set; }
    // The pass buttons. Absent in older replays and left out of the file while released.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Pass { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Through { get; set; }

    public static IntentRecord From(in TickIntent i) => new()
    {
        Mx = i.MoveX, Mz = i.MoveZ, Sprint = i.Sprint, Close = i.CloseControl, Shoot = i.ShootHeld, Finesse = i.Finesse, Chip = i.Chip, Knock = i.KnockOn,
        Rx = i.RightX, Rz = i.RightZ, Cancel = i.Cancel, Skill = i.SkillKey, Driven = i.Driven,
        Jockey = i.Jockey, Tackle = i.Tackle, Slide = i.Slide, Barge = i.Barge, Pass = i.PassHeld, Through = i.ThroughHeld,
    };

    public TickIntent ToIntent() => new()
    {
        MoveX = Mx, MoveZ = Mz, Sprint = Sprint, CloseControl = Close, ShootHeld = Shoot, Finesse = Finesse, Chip = Chip, KnockOn = Knock,
        RightX = Rx, RightZ = Rz, Cancel = Cancel, SkillKey = Skill, Driven = Driven,
        Jockey = Jockey, Tackle = Tackle, Slide = Slide, Barge = Barge, PassHeld = Pass, ThroughHeld = Through,
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
    /// <summary>The defender's sheet id, in a round with a defender; absent otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefenderId { get; set; }
    /// <summary>The team-mate's sheet id, in a round with one; absent otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MateId { get; set; }
    /// <summary>
    /// A hash of the team tuning (passes, the team-mate, control, receiving), in a round with a team-mate; absent otherwise (those
    /// values are not part of <see cref="TuningHash"/>, so a round without one is recorded exactly as before they existed).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TeamHash { get; set; }
    /// <summary>
    /// A hash of the defender and defending tuning, in a round with a defender; absent otherwise (those blocks are not part of
    /// <see cref="TuningHash"/>, so a round without a defender is recorded exactly as before they existed).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefendingHash { get; set; }
    /// <summary>
    /// The hash of the move sheets the round was played with (<see cref="MoveSheets.Sha"/>); absent for a round played without
    /// sheets and for one recorded before they existed (they are not part of <see cref="TuningHash"/>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SheetsHash { get; set; }
    /// <summary>
    /// The aim assistance level the round was played at (see <see cref="World.AimAssist"/>). Left out of the file at Off, and a
    /// replay without it (every one recorded before the levels existed, and every batch round) runs at Off.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public AimAssistLevel AimAssist { get; set; }
    /// <summary>
    /// The attacker's full sheet in the data/players format, written only when the attacker was the player editor's Custom player
    /// (who has no file). When present it is used instead of looking up <see cref="AttackerId"/>; older replays have none.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? AttackerSheet { get; set; }
    /// <summary>
    /// True for a round cut short: a capture taken before the report, or a round that began but was quit or abandoned. It holds
    /// every tick up to that moment and has no report. Left out of the file when false, so a finished round is written as before.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Partial { get; set; }
    /// <summary>The capture this file was written for (id, tag, note, when); null for a plain round replay, and left out of the file then.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReplayCapture? Capture { get; set; }
    /// <summary>Intents per tick: both teams' players, team 0 first.</summary>
    public int PlayersPerTick { get; set; } = 2;
    /// <summary>Flat list: tick 0's players, then tick 1's, and so on.</summary>
    public List<IntentRecord> Intents { get; set; } = new();

    /// <summary>Number of recorded ticks.</summary>
    public int TickCount => PlayersPerTick > 0 ? Intents.Count / PlayersPerTick : 0;

    /// <summary>True if the replay was recorded under the same tuning.</summary>
    public bool MatchesTuning(Tuning t) => TuningHash == DataLoader.TuningHash(t) && (DefendingHash == null || DefendingHash == DataLoader.DefendingHash(t))
        && (TeamHash == null || TeamHash == DataLoader.TeamHash(t)) && (SheetsHash == null || SheetsHash == t.Sheets?.Sha);

    /// <summary>Embeds the attacker's sheet (see <see cref="AttackerSheet"/>).</summary>
    public void SetAttackerSheet(PlayerSheet sheet)
    {
        using var doc = JsonDocument.Parse(sheet.ToJson());
        AttackerSheet = doc.RootElement.Clone();
    }

    /// <summary>The embedded attacker sheet parsed as a player file, or null when the replay has none.</summary>
    public LoadResult<PlayerSheet>? ParseAttackerSheet() =>
        AttackerSheet is { } e ? DataLoader.ParsePlayer(e.GetRawText(), "replay: attacker_sheet", AttackerId) : null;

    /// <summary>
    /// The attacker this replay was played with: the embedded sheet when there is one, otherwise <paramref name="byId"/> applied to
    /// <see cref="AttackerId"/>. Null (with an error) when neither gives a sheet.
    /// </summary>
    public PlayerSheet? ResolveAttacker(Func<string, PlayerSheet?> byId, out string? error)
    {
        error = null;
        if (ParseAttackerSheet() is { } r)
        {
            if (r.Ok) return r.Value;
            error = r.Error;
            return null;
        }
        var s = byId(AttackerId);
        if (s == null) error = $"attacker '{AttackerId}' not found";
        return s;
    }

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
    /// Plays the recorded ticks in a fresh world (Begin, then each tick) and returns the world after the last of them, with no idle
    /// tail: a partial replay ends where it was cut. <paramref name="afterStep"/>, when given, sees the world after every step with
    /// the attacker's intent of that tick. The sheets and scenario must be the ones named in the replay.
    /// </summary>
    /// <param name="defender">The defender's sheet, in a scenario with one (a default sheet when null).</param>
    /// <param name="mate">The team-mate's sheet, in a scenario with one (the attacker's when null).</param>
    public World Play(Tuning tuning, Scenario scenario, PlayerSheet attacker, PlayerSheet keeper, Action<World, TickIntent>? afterStep = null,
        PlayerSheet? defender = null, PlayerSheet? mate = null)
    {
        var world = new World(tuning, scenario, attacker, keeper, Seed, defender, mate) { AimAssist = AimAssist };
        world.Begin();
        int n = world.PlayerCount;
        var buf = new TickIntent[n];
        int per = Math.Max(1, PlayersPerTick);
        for (int tick = 0; tick < TickCount; tick++)
        {
            Array.Clear(buf);
            for (int p = 0; p < n && p < per; p++) buf[p] = Intents[tick * per + p].ToIntent();
            world.Step(buf);
            afterStep?.Invoke(world, buf[0]);
        }
        return world;
    }

    /// <summary>The idle ticks that may follow the recording before a round must have reached its report.</summary>
    public static int TailCap(Tuning tuning) => (int)((tuning.Round.MaxSeconds + tuning.Round.OutcomeSeconds + 5f) * tuning.TickRate);

    /// <summary>
    /// Plays the replay in a fresh world: Begin, then each recorded tick (<see cref="Play"/>), then idle ticks until the report
    /// (capped). The sheets and scenario must be the ones named in the replay.
    /// </summary>
    /// <param name="defender">The defender's sheet, in a scenario with one (a default sheet when null).</param>
    public RoundReport Run(Tuning tuning, Scenario scenario, PlayerSheet attacker, PlayerSheet keeper, PlayerSheet? defender = null, PlayerSheet? mate = null)
    {
        var world = Play(tuning, scenario, attacker, keeper, defender: defender, mate: mate);
        var buf = new TickIntent[world.PlayerCount];
        int cap = TailCap(tuning);
        for (int i = 0; i < cap && world.State != RoundState.Report; i++) world.Step(buf);
        return world.Report ?? throw new InvalidOperationException("Replay did not reach a report");
    }

    /// <summary>A copy that shares nothing mutable with this replay: the intents list is new (its records are never changed).</summary>
    public Replay Copy() => new()
    {
        Version = Version, Seed = Seed, AttackerId = AttackerId, KeeperId = KeeperId, ScenarioId = ScenarioId, TuningHash = TuningHash,
        AttackerSheet = AttackerSheet, Partial = Partial, Capture = Capture?.Copy(), PlayersPerTick = PlayersPerTick,
        DefenderId = DefenderId, DefendingHash = DefendingHash, MateId = MateId, TeamHash = TeamHash, SheetsHash = SheetsHash,
        AimAssist = AimAssist,
        Intents = new List<IntentRecord>(Intents),
    };
}

/// <summary>The capture block of a captured round's replay: which capture, what the person said about it, and when it was taken.</summary>
public sealed class ReplayCapture
{
    /// <summary><c>C&lt;n&gt;</c>, unique across sessions (see <see cref="CaptureIndex"/>).</summary>
    public string Id { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Note { get; set; } = "";
    /// <summary>The world tick the capture was taken at (ticks from Begin).</summary>
    public int Tick { get; set; }
    /// <summary>The round clock then, seconds.</summary>
    public float Clock { get; set; }
    /// <summary>Local wall time of the capture, <c>yyyy-MM-ddTHH:mm:ss</c>.</summary>
    public string Wall { get; set; } = "";

    public ReplayCapture Copy() => (ReplayCapture)MemberwiseClone();
}

/// <summary>
/// Collects the intents of each simulated tick. Attach to <see cref="World.Recorder"/> before <see cref="World.Begin"/>, with
/// the world's aim assistance level already set (the header takes it when the recording starts).
/// </summary>
public sealed class ReplayRecorder
{
    readonly Replay _replay;
    readonly int _players;

    /// <summary>Starts a recording for a world.</summary>
    public ReplayRecorder(World world, string attackerId, string keeperId, string scenarioId, string? defenderId = null, string? mateId = null)
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
            DefenderId = world.Defender != null ? defenderId ?? world.Defender.Sheet.Id : null,
            MateId = world.HasMates ? mateId ?? world.Teams[0][1].Sheet.Id : null,
            TeamHash = world.HasMates ? DataLoader.TeamHash(world.Tuning) : null,
            DefendingHash = world.Defender != null ? DataLoader.DefendingHash(world.Tuning) : null,
            SheetsHash = world.Tuning.Sheets?.Sha,
            AimAssist = world.AimAssist,
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

    /// <summary>A copy of the recording so far, which later ticks do not change (see <see cref="Replay.Copy"/>).</summary>
    public Replay Snapshot() => _replay.Copy();
}
