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
            if (key == "dump-tuning") { opts[key] = "1"; continue; }
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

        if (!opts.TryGetValue("scenario", out var scenarioId)) { Console.Error.WriteLine("Need --scenario <id> (or --replay <file>)"); return 2; }
        var sc = Load(DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", scenarioId + ".json")));
        if (sc == null) return 1;
        string attackerId = opts.TryGetValue("attacker", out var a) ? a : sc.DefaultAttacker;
        string keeperId = opts.TryGetValue("keeper", out var k) ? k : sc.DefaultKeeper;
        var attacker = Load(DataLoader.LoadPlayer(Path.Combine(dataDir, "players", attackerId + ".json")));
        var keeper = Load(DataLoader.LoadPlayer(Path.Combine(dataDir, "players", keeperId + ".json")));
        if (attacker == null || keeper == null) return 1;

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
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                var r = RoundRunner.RunFirstShot(tuning, sc, att, kee, seed + (ulong)i);
                counts[(int)r.End]++;
            }
            sw.Stop();
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
