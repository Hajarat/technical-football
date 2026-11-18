using System.Diagnostics;
using System.Globalization;
using System.Text;
using FootballSim.Core;

// Batch runner: scripted shots through the real world, many seeds, one table and one CSV.
//   dotnet run -c Release --project src/FootballSim.Batch -- --scenario penalty --attacker striker_a --keeper the_wall \
//     --sweep finishing=40,50,60,70,80,90 --n 10000 --seed 1
//   ... --replay replays/<file>.json            re-runs a replay and prints its report
//   ... --save-replay <file>                    plays one scripted round (use --seed) and saves its replay
//   ... --dump-tuning                           prints the built-in tuning as JSON (the format of data/tuning/default.json)
//   ... --probe dribble [--seeds 50] [--ratings 1,50,99] [--exact 1] [--only straight_jog,...]
//                                               runs the nine dribble probes (no keeper involved) and prints their metrics
//   ... --skill-probe [--seeds 50] [--ratings 1,50,99] [--only step_over,...]
//                                               runs the skill-move probes (scripted right-stick traces) and prints their metrics
//   ... --write-skill-replays <dir>             writes one replay per skill move (edge_of_box start) for game screenshots
//   ... --write-keeper-replays <dir>            searches scripted rounds for each visible keeper action and writes one replay each
//   ... --scenario <id> --trace-keeper <seed>   prints the keeper's state, tick by tick, for one scripted round

return Cli.Run(args);

static class Cli
{
    public static int Run(string[] args)
    {
        var opts = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) { Console.Error.WriteLine($"Unexpected argument '{args[i]}'"); return 2; }
            string key = args[i][2..];
            if (key is "dump-tuning" or "skill-probe") { opts[key] = "1"; continue; }
            if (i + 1 >= args.Length) { Console.Error.WriteLine($"Missing value for --{key}"); return 2; }
            opts[key] = args[++i];
        }

        if (opts.ContainsKey("dump-tuning")) { Console.WriteLine(DataLoader.TuningToJson(new Tuning())); return 0; }

        string? root = FindRoot();
        string dataDir = opts.TryGetValue("data", out var dd) ? dd : Path.Combine(root ?? Directory.GetCurrentDirectory(), "data");
        string outDir = Path.Combine(root ?? Directory.GetCurrentDirectory(), "out");

        var tr = DataLoader.LoadTuning(Path.Combine(dataDir, "tuning", "default.json"));
        if (tr.Error != null) Console.Error.WriteLine("warning: " + tr.Error);
        foreach (var w in tr.Warnings) Console.Error.WriteLine("warning: " + w);
        var tuning = tr.Value!;

        if (opts.TryGetValue("replay", out var replayPath)) return RunReplay(replayPath, dataDir, tuning);
        if (opts.ContainsKey("skill-probe")) return SkillProbe.Run(opts, dataDir, tuning);
        if (opts.TryGetValue("write-skill-replays", out var skillDir)) return SkillProbe.WriteReplays(skillDir, dataDir, tuning);
        if (opts.TryGetValue("write-keeper-replays", out var keeperDir)) return KeeperReplays.WriteReplays(keeperDir, dataDir, tuning);
        if (opts.TryGetValue("probe", out var probe))
        {
            if (probe != "dribble") { Console.Error.WriteLine($"Unknown probe '{probe}' (known: dribble)"); return 2; }
            return DribbleProbe.Run(opts, dataDir, tuning);
        }

        if (!opts.TryGetValue("scenario", out var scenarioId)) { Console.Error.WriteLine("Need --scenario <id> (or --replay <file>)"); return 2; }
        var sc = Load(DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", scenarioId + ".json")));
        if (sc == null) return 1;
        string attackerId = opts.TryGetValue("attacker", out var a) ? a : sc.DefaultAttacker;
        string keeperId = opts.TryGetValue("keeper", out var k) ? k : sc.DefaultKeeper;
        var attacker = Load(DataLoader.LoadPlayer(Path.Combine(dataDir, "players", attackerId + ".json")));
        var keeper = Load(DataLoader.LoadPlayer(Path.Combine(dataDir, "players", keeperId + ".json")));
        if (attacker == null || keeper == null) return 1;

        if (opts.TryGetValue("trace-keeper", out var traceSeed))
            return KeeperReplays.Trace(tuning, sc, attacker, keeper, ulong.Parse(traceSeed, CultureInfo.InvariantCulture));

        if (opts.TryGetValue("save-replay", out var savePath))
        {
            ulong rs = opts.TryGetValue("seed", out var rss) ? ulong.Parse(rss, CultureInfo.InvariantCulture) : 1UL;
            var report = RoundRunner.RunToReport(tuning, sc, attacker, keeper, rs, out var replay);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(savePath))!);
            replay.Save(savePath);
            Console.WriteLine($"saved {savePath} ({replay.TickCount} ticks), outcome {report.Outcome.Label()}");
            return 0;
        }

        int n = opts.TryGetValue("n", out var ns) ? int.Parse(ns, CultureInfo.InvariantCulture) : 10000;
        ulong seed = opts.TryGetValue("seed", out var ss) ? ulong.Parse(ss, CultureInfo.InvariantCulture) : 1UL;

        string field = "";
        var values = new List<int?>();
        bool onKeeper = false;
        if (opts.TryGetValue("sweep", out var sweep))
        {
            int eq = sweep.IndexOf('=');
            if (eq < 1) { Console.Error.WriteLine("--sweep needs <field>=<v1,v2,...>"); return 2; }
            field = sweep[..eq];
            if (field.StartsWith("keeper.")) { onKeeper = true; field = field[7..]; }
            else if (field.StartsWith("attacker.")) field = field[9..];
            else onKeeper = PlayerSheet.KeeperRatingNames.Contains(field);
            if (!PlayerSheet.RatingNames.Contains(field)) { Console.Error.WriteLine($"Unknown rating '{field}'. Known: {string.Join(", ", PlayerSheet.RatingNames)}"); return 2; }
            foreach (var v in sweep[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                values.Add(int.Parse(v, CultureInfo.InvariantCulture));
        }
        else values.Add(null);

        Console.WriteLine($"scenario={sc.Id} attacker={attacker.Id} keeper={keeper.Id} n={n} seed={seed}" +
                          (field != "" ? $" sweep={(onKeeper ? "keeper." : "attacker.")}{field}" : ""));
        var header = new[] { "value", "n", "scored", "held", "parried", "rebound", "wide", "over", "out", "time_up", "no_shot", "scored_pct", "seconds" };
        var rows = new List<string[]>();
        var keeperLines = new List<(string Value, KeeperStats Stats)>();
        var total = Stopwatch.StartNew();
        foreach (var v in values)
        {
            var att = attacker; var kee = keeper;
            if (v is int val)
            {
                if (onKeeper) { kee = keeper.Clone(); kee.SetRating(field, val); }
                else { att = attacker.Clone(); att.SetRating(field, val); }
            }
            var counts = new int[Enum.GetValues<ShotEnd>().Length];
            var stats = new KeeperStats();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                var r = RoundRunner.RunFirstShot(tuning, sc, att, kee, seed + (ulong)i, followUp: true);
                counts[(int)r.End]++;
                stats.Add(r);
            }
            sw.Stop();
            keeperLines.Add((v?.ToString() ?? "-", stats));
            int C(ShotEnd e) => counts[(int)e];
            rows.Add(new[]
            {
                v?.ToString() ?? "-", n.ToString(), C(ShotEnd.Scored).ToString(), C(ShotEnd.Held).ToString(), C(ShotEnd.Parried).ToString(),
                C(ShotEnd.Rebound).ToString(), C(ShotEnd.Wide).ToString(), C(ShotEnd.Over).ToString(), C(ShotEnd.Out).ToString(),
                C(ShotEnd.TimeUp).ToString(), C(ShotEnd.NoShot).ToString(),
                (100.0 * C(ShotEnd.Scored) / n).ToString("F1", CultureInfo.InvariantCulture),
                sw.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture),
            });
        }
        total.Stop();

        PrintTable(field == "" ? "-" : field, header, rows);
        foreach (var (value, st) in keeperLines) st.Print(field == "" ? "" : $"{field}={value}: ");
        Console.WriteLine($"total wall time {total.Elapsed.TotalSeconds:F2} s for {values.Count * n} rounds");

        Directory.CreateDirectory(outDir);
        string path = Path.Combine(outDir, $"batch-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var sb = new StringBuilder();
        sb.AppendLine("scenario,attacker,keeper,field,seed," + string.Join(",", header));
        foreach (var row in rows)
            sb.AppendLine($"{sc.Id},{attacker.Id},{keeper.Id},{(field == "" ? "" : (onKeeper ? "keeper." : "attacker.") + field)},{seed},{string.Join(",", row)}");
        File.WriteAllText(path, sb.ToString());
        Console.WriteLine("wrote " + path);
        return 0;
    }

    static int RunReplay(string path, string dataDir, Tuning tuning)
    {
        var replay = Replay.Load(path, out var err);
        if (replay == null) { Console.Error.WriteLine(err); return 1; }
        var sc = Load(DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", replay.ScenarioId + ".json")));
        var att = Load(DataLoader.LoadPlayer(Path.Combine(dataDir, "players", replay.AttackerId + ".json")));
        var kee = Load(DataLoader.LoadPlayer(Path.Combine(dataDir, "players", replay.KeeperId + ".json")));
        if (sc == null || att == null || kee == null) return 1;
        if (!replay.MatchesTuning(tuning))
            Console.WriteLine("Tuning has changed since this replay, so the result may differ.");
        var report = replay.Run(tuning, sc, att, kee);
        Console.WriteLine($"replay {path}: scenario={replay.ScenarioId} attacker={replay.AttackerId} keeper={replay.KeeperId} seed={replay.Seed} ticks={replay.TickCount}");
        Console.WriteLine(report.ToJson());
        return 0;
    }

    static T? Load<T>(LoadResult<T> r) where T : class
    {
        foreach (var w in r.Warnings) Console.Error.WriteLine("warning: " + w);
        if (r.Value == null || r.Error != null) { Console.Error.WriteLine("error: " + r.Error); return null; }
        return r.Value;
    }

    static string? FindRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "FootballSim.slnx")) && Directory.Exists(Path.Combine(dir.FullName, "data")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        return null;
    }

    static void PrintTable(string field, string[] header, List<string[]> rows)
    {
        var widths = header.Select((h, i) => Math.Max(h.Length, rows.Max(r => r[i].Length))).ToArray();
        string Line(string[] cells) => string.Join("  ", cells.Select((c, i) => c.PadLeft(widths[i])));
        Console.WriteLine(Line(header.Select((h, i) => i == 0 ? field : h).ToArray()));
        Console.WriteLine(new string('-', widths.Sum() + 2 * (widths.Length - 1)));
        foreach (var r in rows) Console.WriteLine(Line(r));
    }
}

/// <summary>
/// The keeper's side of a batch: what became of parried first shots, how many rounds had a rush or a smother, how often he was
/// planted at the strike, his mean reaction and his speed at the strike, and the saves and dives he made.
/// </summary>
sealed class KeeperStats
{
    readonly List<float> _depth = new(), _dist = new();
    int _onTarget;
    int _n, _shots, _rush, _smother, _smotherClaim, _planted, _reactN, _parried;
    double _react, _speed;
    readonly Dictionary<string, int> _after = new(), _save = new(), _dive = new();

    public void Add(RoundRunner.Result r)
    {
        _n++;
        var k = r.Keeper;
        if (k.Rushed) _rush++;
        if (k.Smothered) _smother++;
        if (r.End == ShotEnd.NoShot && r.Outcome == Outcome.Held && k.Save == SaveKind.Smother) _smotherClaim++;
        if (r.Shot != null)
        {
            _shots++;
            if (k.PlantedAtStrike) _planted++;
            if (k.OnTarget) _onTarget++;
            if (float.IsFinite(k.DepthAtStrikeM)) { _depth.Add(k.DepthAtStrikeM); _dist.Add(k.BallDistanceAtStrikeM); }
            if (float.IsFinite(k.SpeedAtStrikeMs)) _speed += k.SpeedAtStrikeMs;
            if (float.IsFinite(k.ReactionS)) { _react += k.ReactionS; _reactN++; }
            if (k.Save != SaveKind.None) Bump(_save, k.Save.ToString());
            if (k.Dive != KeeperDive.None) Bump(_dive, k.Dive.ToString());
        }
        if (r.End is ShotEnd.Parried or ShotEnd.Rebound)
        {
            _parried++;
            Bump(_after, $"{r.End.ToString().ToLowerInvariant()}->{r.After.Label().ToLowerInvariant()}");
        }
    }

    static string Spread(List<float> v)
    {
        if (v.Count == 0) return "-";
        var o = v.OrderBy(x => x).ToArray();
        var ic = CultureInfo.InvariantCulture;
        return $"mean {o.Average().ToString("F2", ic)} (p10 {o[(int)(0.1 * (o.Length - 1))].ToString("F2", ic)}, p90 {o[(int)(0.9 * (o.Length - 1))].ToString("F2", ic)})";
    }

    static void Bump(Dictionary<string, int> d, string key) => d[key] = d.TryGetValue(key, out var c) ? c + 1 : 1;

    string Pct(int c) => (100.0 * c / Math.Max(1, _n)).ToString("F1", CultureInfo.InvariantCulture) + "%";
    string PctOf(int c, int of) => (100.0 * c / Math.Max(1, of)).ToString("F1", CultureInfo.InvariantCulture) + "%";

    static string List(Dictionary<string, int> d, Func<int, string> f) =>
        d.Count == 0 ? "-" : string.Join(", ", d.OrderByDescending(e => e.Value).Select(e => $"{e.Key} {f(e.Value)}"));

    public void Print(string prefix)
    {
        var ic = CultureInfo.InvariantCulture;
        Console.WriteLine($"{prefix}keeper: rush {Pct(_rush)} of rounds, smother {Pct(_smother)} (claimed before a shot {Pct(_smotherClaim)}), " +
                          $"planted at strike {PctOf(_planted, _shots)} of shots, mean reaction {(_react / Math.Max(1, _reactN)).ToString("F3", ic)} s, " +
                          $"mean speed at strike {(_speed / Math.Max(1, _shots)).ToString("F2", ic)} m/s");
        Console.WriteLine($"{prefix}shots on target (unhindered flight inside the goal) {PctOf(_onTarget, _shots)} of {_shots} shots");
        Console.WriteLine($"{prefix}at strike: keeper off his line {Spread(_depth)} m, ball from goal centre {Spread(_dist)} m");
        Console.WriteLine($"{prefix}after parry/rebound (% of rounds): {List(_after, Pct)}");
        Console.WriteLine($"{prefix}saves (% of rounds): {List(_save, Pct)}");
        Console.WriteLine($"{prefix}dives (% of rounds): {List(_dive, Pct)}");
    }
}
