using System.Globalization;
using System.Text.Json;
using FootballSim.Core;

/// <summary>
/// The session report (--defence-report &lt;session dir or replay&gt;): for recorded rounds, goals per round and per first attack by
/// scenario, how first attacks ended, the time and way the ball first got past the defender, and for each goal's strike the
/// keeper's depth off his line and his distance off the shot line. Every number is read from the round's saved report (the
/// session log's round_end line) when that report holds it, whatever the hashes: a round played on another machine does not
/// always replay to the same ending. Only a round whose saved report predates the measured fields (no first_attack block) is
/// replayed with its recorded intents under the current code, and only when its hashes match the current data (or with
/// --any-tuning 1); otherwise it is counted from its outcome alone. A folder is searched with its subfolders for round-*.json.
/// </summary>
static class DefenceReport
{
    static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

    enum Source { Saved, Replay, Neither }

    sealed class Round
    {
        public string File = "", Scenario = "";
        public RoundReport? Saved;          // from the session log
        public RoundReport? Replayed;       // under the current code (only when the saved report lacks the measured fields)
        public bool Matches, Partial;
        public Outcome Recorded = Outcome.None;
        public string Hashes = "";          // what differs from the current tuning
        /// <summary>The report the measured numbers come from: the saved one when it holds them, else the replay.</summary>
        public RoundReport? Measured => Saved?.FirstAttack != null ? Saved : Replayed;
        public Source Source => Saved?.FirstAttack != null ? Source.Saved : Replayed != null ? Source.Replay : Source.Neither;
        public Outcome Outcome => Recorded != Outcome.None ? Recorded : Replayed?.Outcome ?? Outcome.None;
    }

    /// <param name="anyTuning">Replay a round whose saved report lacks the measured fields whatever its hashes (--any-tuning 1, a
    /// diagnostic: rounds recorded under other tuning are then replayed under the current one and marked so).</param>
    public static int Run(string path, string dataDir, Tuning tuning, bool anyTuning = false)
    {
        string[] files = Directory.Exists(path)
            ? Directory.GetFiles(path, "round-*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToArray()
            : File.Exists(path) ? new[] { path } : Array.Empty<string>();
        if (files.Length == 0) { Console.Error.WriteLine($"{path}: no round-*.json replays"); return 1; }
        var saved = SavedReports(files);
        var rounds = new Round[files.Length];
        string? failed = null;
        Parallel.For(0, files.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
            var r = rounds[i] = new Round { File = files[i] };
            var replay = Replay.Load(files[i], out var err);
            if (replay == null) { failed = err; return; }
            r.Scenario = replay.ScenarioId;
            r.Partial = replay.Partial;
            saved.TryGetValue(Path.GetFullPath(files[i]), out r.Saved);
            r.Recorded = r.Saved?.Outcome ?? OutcomeFromName(files[i]);
            if (r.Partial) return;
            r.Matches = replay.MatchesTuning(tuning);
            if (!r.Matches) r.Hashes = Differs(replay, tuning);
            // (the saved report holds every measured number: no replay)
            if (r.Saved?.FirstAttack != null || !r.Matches && !anyTuning) return;
            r.Replayed = ReplayRound(replay, dataDir, tuning, out err);
            if (r.Replayed == null) failed = $"{files[i]}: {err}";
        });
        if (failed != null) { Console.Error.WriteLine("error: " + failed); return 1; }

        var done = rounds.Where(r => !r.Partial).ToList();
        var measured = done.Where(r => r.Measured != null).ToList();
        var replayed = done.Where(r => r.Replayed != null).ToList();
        var older = done.Where(r => !r.Matches).ToList();
        int asRecorded = replayed.Count(r => r.Recorded == Outcome.None || r.Replayed!.Outcome == r.Recorded);
        Console.WriteLine($"defence report: {path}");
        if (anyTuning) Console.WriteLine("--any-tuning: rounds without the measured fields in their saved report are replayed whatever their hashes");
        Console.WriteLine($"{done.Count} finished rounds{(rounds.Length > done.Count ? $" ({rounds.Length - done.Count} unfinished skipped)" : "")}; " +
                          $"numbers from saved reports {done.Count(r => r.Source == Source.Saved)}, from replays {replayed.Count}, " +
                          $"neither (outcome only: saved report without the measured fields and other tuning) {done.Count(r => r.Source == Source.Neither)}");
        Console.WriteLine($"replayed rounds: {replayed.Count}, {asRecorded} ending as recorded; recorded under other tuning: {older.Count}");
        foreach (var r in replayed.Where(r => r.Recorded != Outcome.None && r.Replayed!.Outcome != r.Recorded))
            Console.WriteLine($"   replay differs: {Path.GetFileName(r.File)} recorded {r.Recorded.Label()}, now {r.Replayed!.Outcome.Label()}");

        Console.WriteLine();
        Console.WriteLine("scenario          rounds  goals  goals/round  first-attack goals  outcomes");
        foreach (var g in done.GroupBy(r => r.Scenario).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var l = g.ToList();
            int goals = l.Count(r => r.Outcome == Outcome.Goal);
            var fa = l.Where(r => r.Measured != null).ToList();
            string faGoals = fa.Count == 0 ? "-" : $"{fa.Count(r => r.Measured!.FirstAttack!.Goal)} of {fa.Count} ({Pct(fa.Count(r => r.Measured!.FirstAttack!.Goal), fa.Count)})";
            string outcomes = string.Join(", ", l.GroupBy(r => r.Outcome).OrderByDescending(x => x.Count()).ThenBy(x => x.Key)
                .Select(x => $"{x.Count()} {x.Key.Label().ToLowerInvariant()}"));
            Console.WriteLine($"{g.Key,-17} {l.Count,6}  {goals,5}  {Pct(goals, l.Count),11}  {faGoals,18}  {outcomes}");
        }

        Console.WriteLine();
        Console.WriteLine("first attacks, how they ended:" + (measured.Count == 0 ? " no round with the measured fields (saved or replayed)" : ""));
        foreach (var g in measured.GroupBy(r => r.Scenario).OrderBy(g => g.Key, StringComparer.Ordinal))
            Console.WriteLine($"   {g.Key,-17} " + string.Join(", ", Enum.GetValues<FirstAttackEnd>()
                .Select(e => (e, c: g.Count(r => r.Measured!.FirstAttack!.EndedBy == e))).Where(x => x.c > 0)
                .Select(x => $"{x.e.ToString().ToLowerInvariant()} {x.c}")));

        var withDef = measured.Where(r => r.Measured!.Defender != null).ToList();
        if (withDef.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"past the defender (the ball {World.PastMarginM.ToString("F1", Ic)} m nearer the goal centre than he is, the attackers still on it " +
                              $"{World.PastHoldS.ToString("F1", Ic)} s later; by pass: within {World.PastByPassS.ToString("F1", Ic)} s of a pass):");
            foreach (var g in withDef.GroupBy(r => r.Scenario).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var past = g.Where(r => r.Measured!.Defender!.BeatenS != null).ToList();
                var t = past.Select(r => r.Measured!.Defender!.BeatenS!.Value).OrderBy(x => x).ToArray();
                Console.WriteLine($"   {g.Key,-17} past in {past.Count} of {g.Count()} rounds (by pass {past.Count(r => r.Measured!.Defender!.BeatenBy == BeatenBy.Pass)}, " +
                                  $"by dribble {past.Count(r => r.Measured!.Defender!.BeatenBy == BeatenBy.Dribble)}); seconds from the start: " +
                                  (t.Length == 0 ? "-" : $"median {S(Quantile(t, 0.5))}, quartiles {S(Quantile(t, 0.25))} and {S(Quantile(t, 0.75))}, range {S(t[0])} to {S(t[^1])}"));
            }
        }

        var goalRounds = measured.Where(r => r.Outcome == Outcome.Goal).ToList();
        if (goalRounds.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("goals: the keeper at the goal's strike, the last strike (depth off his line; distance from the segment ball to goal centre), m");
            foreach (var r in goalRounds)
            {
                var rep = r.Measured!;
                var k = rep.Keeper;
                string fa = rep.FirstAttack!.Goal ? "first attack" : "after a miss";
                // (the keeper block is the last strike's: its depth is the goal's strike's)
                string at = rep.Shots.Count == 0 || k.OffLineAtStrikeM.Length == 0 ? "no shot"
                    : $"depth {k.DepthAtStrikeM.ToString("F1", Ic),4}  off the shot line {k.OffLineAtStrikeM[^1].ToString("F1", Ic),4}";
                string from = r.Source == Source.Saved ? "saved" : "replay";
                string differs = r.Replayed != null && r.Recorded != Outcome.None && r.Recorded != r.Replayed.Outcome
                    ? $"  (recorded {r.Recorded.Label().ToLowerInvariant()}: the replay ends differently)" : "";
                Console.WriteLine($"   {Path.GetFileName(r.File),-40} {fa,-13} {at}  [{from}]{differs}");
            }
        }

        var neither = done.Where(r => r.Source == Source.Neither).ToList();
        if (neither.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("counted from their outcome only (saved report without the measured fields, recorded under other tuning):");
            foreach (var r in neither) Console.WriteLine($"   {Path.GetFileName(r.File)}: {r.Outcome.Label().ToLowerInvariant()}{(r.Saved == null ? " (no saved report: outcome from the file name)" : "")}; {r.Hashes}");
        }
        return 0;
    }

    /// <summary>Which of the replay's hashes differ from the current tuning's: recorded, then now.</summary>
    static string Differs(Replay r, Tuning t)
    {
        var d = new List<string>();
        void Check(string name, string? rec, string? now) { if (rec != null && rec != now) d.Add($"{name} {rec} (now {now})"); }
        Check("tuning_hash", r.TuningHash, DataLoader.TuningHash(t));
        Check("defending_hash", r.DefendingHash, DataLoader.DefendingHash(t));
        Check("team_hash", r.TeamHash, DataLoader.TeamHash(t));
        Check("sheets_hash", r.SheetsHash, t.Sheets?.Sha);
        return string.Join(", ", d);
    }

    static string Pct(int c, int n) => (100.0 * c / Math.Max(1, n)).ToString("F0", Ic) + "%";
    static string S(float v) => v.ToString("F2", Ic) + " s";

    /// <summary>The p-quantile of sorted values, linear between the nearest ranks.</summary>
    public static float Quantile(float[] sorted, double p)
    {
        if (sorted.Length == 0) return float.NaN;
        double at = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(at), hi = Math.Min(sorted.Length - 1, lo + 1);
        return (float)(sorted[lo] + (sorted[hi] - sorted[lo]) * (at - lo));
    }

    /// <summary>The round_end reports of the session logs beside the files, by the full path of their replay.</summary>
    static Dictionary<string, RoundReport> SavedReports(string[] files)
    {
        var map = new Dictionary<string, RoundReport>();
        foreach (var dir in files.Select(f => Path.GetDirectoryName(Path.GetFullPath(f))!).Distinct())
        {
            string log = Path.Combine(dir, "session.jsonl");
            if (!File.Exists(log)) continue;
            foreach (var line in File.ReadLines(log))
            {
                if (!line.Contains("\"round_end\"")) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("report", out var rep) || !root.TryGetProperty("replay", out var file) || file.ValueKind != JsonValueKind.String) continue;
                    if (RoundReport.FromJson(rep.GetRawText()) is { } r) map[Path.Combine(dir, file.GetString()!)] = r;
                }
                catch (JsonException) { }
            }
        }
        return map;
    }

    static Outcome OutcomeFromName(string file)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        string tail = name[(name.LastIndexOf('-') + 1)..];
        foreach (var o in Enum.GetValues<Outcome>())
            if (string.Equals(o.Label().Replace(' ', '_'), tail, StringComparison.OrdinalIgnoreCase)) return o;
        return Outcome.None;
    }

    /// <summary>Plays a replay with its recorded intents as --replay does (the embedded Custom sheet when there is one).</summary>
    static RoundReport? ReplayRound(Replay replay, string dataDir, Tuning tuning, out string? error)
    {
        error = null;
        PlayerSheet? P(string id) => DataLoader.LoadPlayer(Path.Combine(dataDir, "players", id + ".json")).Value;
        var sc = DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", replay.ScenarioId + ".json")).Value;
        if (sc == null) { error = $"scenario '{replay.ScenarioId}' not found"; return null; }
        var att = replay.ResolveAttacker(P, out error);
        var kee = P(replay.KeeperId);
        if (att == null || kee == null) { error ??= $"keeper '{replay.KeeperId}' not found"; return null; }
        var def = sc.Defenders > 0 ? P(replay.DefenderId ?? sc.DefaultDefender) : null;
        var mate = sc.Mate != null ? P(replay.MateId ?? sc.DefaultMate) : null;
        return replay.Run(tuning, sc, att, kee, def, mate);
    }
}
