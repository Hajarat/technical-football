using System.Diagnostics;
using System.Globalization;
using System.Text;
using FootballSim.Core;

// Batch runner: scripted shots through the real world, many seeds, one table and one CSV.
//   dotnet run -c Release --project src/FootballSim.Batch -- --scenario penalty --attacker striker_a --keeper the_wall \
//     --sweep finishing=40,50,60,70,80,90 --n 10000 --seed 1
//   ... --replay replays/<file>.json            re-runs a replay and prints its report (a partial one: its ticks and shots so far)
//   ... --shot-audit <file|dir> [--capture C14]  one line per shot at its release: position, facing, stick, aim, the foot rule and
//                                               the geometry's choice, the aimed Z, the ball's offset, then the strike (a folder is
//                                               searched with its subfolders; --capture picks one capture through captures.jsonl)
//   ... --save-replay <file>                    plays one scripted round (use --seed) and saves its replay
//   ... --dump-tuning                           prints the built-in tuning as JSON (the format of data/tuning/default.json)
//   ... --probe dribble [--seeds 50] [--ratings 1,50,99] [--exact 1] [--only straight_jog,...]
//   ... --probe dribble --only close_reverse_flick --save-replay <file> [--index 0] [--in halfway_run]
//                                               plays one probe trace in a real scenario and saves its replay (for screenshots)
//                                               runs the dribble probes (no keeper involved) and prints their metrics
//   ... --probe dribble --dtrace <probe> --ratings R [--index i] [--seeds n] [--from T --to T] [--steps-only 1]
//                                               traces one probe tick by tick (diagnostic)
//   ... --probe sessions [--replays <dir|file>]  re-runs play-test rounds (a folder with its subfolders) and counts touches dropped
//                                               at contact, and speed steps in possession
//   ... --probe shot [--seeds 20] [--ratings 1,50,99]  scripted shots pressed in possession: press to strike, ball distance,
//                                               touches after the press, strike foot and speed steps
//   ... --probe feel [--seeds 20] [--ratings 1,50,99] [--replays <dir|file>]
//                                               speed surges, acceleration, speed steps, touch cadence and heading response of dribbling
//   ... --probe feel --rtrace <replay> [--from T --to T] [--steps-only 1]
//                                               traces one recorded round tick by tick and attributes its speed steps (diagnostic)
//   ... --probe move-identity [--seeds 8] [--only elastico,...] [--cards 1] [--runs 1]
//                                               every skill move from standing, a jog and a sprint: the ball's and the body's
//                                               measures, each move's card, and the pairs of moves that look alike
//   ... --skill-probe [--seeds 50] [--ratings 1,50,99] [--only step_over,...] [--list-failed 1]
//                                               runs the skill-move probes (scripted right-stick traces) and prints their metrics
//   ... --write-skill-replays <dir>             writes one replay per skill move (edge_of_box start) for game screenshots
//   ... --write-shot-replays <dir>              writes one replay per kind of kick and ball-out angle (edge_of_box start) for the game's shot clips
//   ... --write-keeper-replays <dir>            searches scripted rounds for each visible keeper action and writes one replay each
//   ... --scenario <id> --trace-keeper <seed>   prints the keeper's state, tick by tick, for one scripted round
//   ... --keeper-probe [--seeds 40] [--rushing 1,50,99] [--only sprint,...] [--timeline 1]
//                                               human-style attacker plans (TickIntent per tick, no ScriptedAttacker) against the keeper
//   ... --write-probe-replays <dir>             writes keeper-probe showcase replays (charge, jockey, step-over, chip) for screenshots
//   ... --probe-replays <dir|file> [--every 30]  re-runs play-test replays (round-*.json) with the keeper's timeline every 0.25 s
//   ... --probe defender [--n 2000] [--seed 1] [--styles straight,cut,skill] [--sweep awareness=1,50,99]
//                                               scripted rounds of the defender duel per attacker style, with their bands
//   ... --trace-defender <seed> [--style straight|cut|skill] [--every 12]
//                                               prints the defender's state through one scripted round
//   ... --write-defender-replays <dir>          searches scripted rounds for each defender action and writes one replay each
//   ... --probe human [--n 2000] [--seed 1] [--plans a,b] [--attacker even|fast] [--scenario id] [--brain contain|legacy]
//                                               plans played like a person (TickIntent per tick) for an even and a fast attacker,
//                                               first attacks only, with its bands (exit 3 on a miss unless --brain legacy)
//   ... --defence-report <session dir|replay> [--any-tuning 1]
//                                               recorded rounds from their saved reports and replays: goals per round and per first
//                                               attack, past the defender, the keeper at each goal's strike
//   ... --brain legacy|contain                 with any run: legacy sets every brain key of the tuning (defender.brain, keeper.brain) to
//                                               legacy, the older bots; contain (or no flag) plays the shipped tuning
//   ... --lock-probe [--seeds 1000] [--only behind,still_shot,first_time,flick,gesture,race,slide]
//                                               scripted loose-ball rounds, one case per lock-on rule, with their targets
//   ... --lock-probe --trace <case> [--seed 1]  prints one round of a case: the claim, the legs and the events
//   ... --set defender.overshoot_m=0.6,...     overrides plain tuning numbers by their file names (experiments)
//   ... --counters off                          the scripted attacker ignores the keeper (no shorter touches, rounding or chips)
//   ... --close-down off                        the shallow keeper: he never closes down (close_down_range 0), so never charges

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
            if (key is "dump-tuning" or "skill-probe" or "keeper-probe" or "lock-probe" or "pass-probe" or "mate-probe") { opts[key] = "1"; continue; }
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
        // --set a.b=1.5,c.d=2: overrides plain numbers of the loaded tuning by their file names, for experiments.
        if (opts.TryGetValue("set", out var sets))
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(DataLoader.TuningToJson(tuning))!;
            foreach (var pair in sets.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=');
                var keys = kv[0].Split('.');
                var at = node;
                for (int i = 0; i < keys.Length - 1; i++) at = at![keys[i]];
                if (at?[keys[^1]] == null) { Console.Error.WriteLine($"--set: no tuning value '{kv[0]}'"); return 2; }
                at[keys[^1]] = double.Parse(kv[1], CultureInfo.InvariantCulture);
            }
            var again = DataLoader.ParseTuning(node.ToJsonString(), "--set");
            if (again.Error != null) { Console.Error.WriteLine("error: " + again.Error); return 2; }
            again.Value!.Sheets = tuning.Sheets;
            tuning = again.Value!;
        }

        // --brain legacy: every brain key of the tuning set to legacy (the older bots); contain or none: the shipped tuning.
        if (HumanProbe.CheckBrain(opts) is int badBrain) return badBrain;
        tuning = HumanProbe.ApplyBrain(opts, tuning);

        if (opts.TryGetValue("replay", out var replayPath)) return RunReplay(replayPath, dataDir, tuning);
        if (opts.TryGetValue("defence-report", out var defencePath)) return DefenceReport.Run(defencePath, dataDir, tuning,
            opts.TryGetValue("any-tuning", out var anyT) && anyT is "1" or "true");
        if (opts.TryGetValue("shot-audit", out var auditPath))
            return ShotAuditCli.Run(auditPath, opts.TryGetValue("capture", out var capId) ? capId : null, dataDir, tuning);
        if (opts.ContainsKey("skill-probe")) return SkillProbe.Run(opts, dataDir, tuning);
        if (opts.ContainsKey("keeper-probe")) return KeeperProbe.Run(opts, dataDir, tuning);
        if (opts.ContainsKey("lock-probe")) return LockProbe.Run(opts, dataDir, tuning);
        if (opts.ContainsKey("pass-probe")) return PassProbe.Run(opts, dataDir, tuning);
        if (opts.ContainsKey("mate-probe")) return MateProbe.Run(opts, dataDir, tuning);
        if (opts.TryGetValue("write-probe-replays", out var probeDir)) return KeeperProbe.WriteReplays(probeDir, dataDir, tuning);
        if (opts.TryGetValue("probe-replays", out var probeReplays)) return KeeperProbe.RunReplays(probeReplays, dataDir, tuning, opts);
        if (opts.TryGetValue("write-skill-replays", out var skillDir)) return SkillProbe.WriteReplays(skillDir, dataDir, tuning);
        if (opts.TryGetValue("write-shot-replays", out var shotDir)) return ShotReplays.Write(shotDir, dataDir, tuning);
        if (opts.TryGetValue("write-keeper-replays", out var keeperDir)) return KeeperReplays.WriteReplays(keeperDir, dataDir, tuning);
        if (opts.TryGetValue("probe", out var probe))
        {
            if (probe == "feel") return FeelProbe.Run(opts, dataDir, tuning);
            if (probe == "move-identity") return MoveIdentityProbe.Run(opts, dataDir, tuning);
            if (probe == "sessions") return SessionProbe.Run(opts, root ?? Directory.GetCurrentDirectory(), dataDir, tuning);
            if (probe == "shots") return ShotDiag.Run(opts, dataDir, tuning);
            if (probe == "shot") return ShotProbe.Run(opts, dataDir, tuning);
            if (probe == "defender") return DefenderProbe.Run(opts, dataDir, tuning);
            if (probe == "team") return MateProbe.RunTeam(opts, dataDir, tuning);
            if (probe == "human") return HumanProbe.Run(opts, dataDir, tuning);
            if (probe != "dribble") { Console.Error.WriteLine($"Unknown probe '{probe}' (known: dribble, feel, move-identity, sessions, shots, shot, defender, team, human)"); return 2; }
            return DribbleProbe.Run(opts, dataDir, tuning);
        }

        if (opts.TryGetValue("trace-defender", out var traceDefender)) return DefenderProbe.Trace(opts, dataDir, tuning, ulong.Parse(traceDefender, CultureInfo.InvariantCulture));
        if (opts.TryGetValue("write-defender-replays", out var defenderDir)) return DefenderProbe.WriteReplays(defenderDir, opts, dataDir, tuning);

        if (!opts.TryGetValue("scenario", out var scenarioId)) { Console.Error.WriteLine("Need --scenario <id> (or --replay <file>)"); return 2; }
        var sc = Load(DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", scenarioId + ".json")));
        if (sc == null) return 1;
        string attackerId = opts.TryGetValue("attacker", out var a) ? a : sc.DefaultAttacker;
        string keeperId = opts.TryGetValue("keeper", out var k) ? k : sc.DefaultKeeper;
        var attacker = Load(DataLoader.LoadPlayer(Path.Combine(dataDir, "players", attackerId + ".json")));
        var keeper = Load(DataLoader.LoadPlayer(Path.Combine(dataDir, "players", keeperId + ".json")));
        if (attacker == null || keeper == null) return 1;
        // A scenario with a defender: his sheet (--defender, else the scenario's).
        PlayerSheet? defender = null;
        if (sc.Defenders > 0)
        {
            defender = Load(DataLoader.LoadPlayer(Path.Combine(dataDir, "players", (opts.TryGetValue("defender", out var df) ? df : sc.DefaultDefender) + ".json")));
            if (defender == null) return 1;
        }

        // A scenario with a team-mate: his sheet (--mate, else the scenario's).
        PlayerSheet? mateSheet = null;
        if (sc.Mate != null)
        {
            mateSheet = Load(DataLoader.LoadPlayer(Path.Combine(dataDir, "players", (opts.TryGetValue("mate", out var mt) ? mt : sc.DefaultMate) + ".json")));
            if (mateSheet == null) return 1;
        }

        if (opts.TryGetValue("trace-keeper", out var traceSeed))
            return KeeperReplays.Trace(tuning, sc, attacker, keeper, ulong.Parse(traceSeed, CultureInfo.InvariantCulture),
                opts.TryGetValue("trace-from", out var tf) ? int.Parse(tf, CultureInfo.InvariantCulture) : -1,
                opts.TryGetValue("trace-to", out var tt) ? int.Parse(tt, CultureInfo.InvariantCulture) : -1);

        if (opts.TryGetValue("trace-team", out var teamSeed))
            return MateProbe.TraceTeam(tuning, sc, attacker, keeper, defender, mateSheet, MateScript(opts) ?? new ScriptedAttacker(),
                ulong.Parse(teamSeed, CultureInfo.InvariantCulture), opts.TryGetValue("every", out var te) ? int.Parse(te, CultureInfo.InvariantCulture) : 12);

        if (opts.TryGetValue("save-replay", out var savePath))
        {
            ulong rs = opts.TryGetValue("seed", out var rss) ? ulong.Parse(rss, CultureInfo.InvariantCulture) : 1UL;
            var report = RoundRunner.RunToReport(tuning, sc, attacker, keeper, rs, out var replay, MateScript(opts), defender: defender, mate: mateSheet);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(savePath))!);
            replay.Save(savePath);
            Console.WriteLine($"saved {savePath} ({replay.TickCount} ticks), outcome {report.Outcome.Label()}");
            return 0;
        }

        // --counters off: the scripted attacker ignores the keeper (no shorter touches, rounding or chips).
        bool counters = !(opts.TryGetValue("counters", out var cs) && cs is "off" or "0" or "false");
        // --close-down off: the shallow keeper, who holds his arc (he never starts closing down, so never charges either).
        bool closeDown = !(opts.TryGetValue("close-down", out var cd) && cd is "off" or "0" or "false");
        if (!closeDown) tuning.Keeper.CloseDownRange = 0f;
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

        Console.WriteLine($"scenario={sc.Id} attacker={attacker.Id} keeper={keeper.Id} n={n} seed={seed}" + (counters ? "" : " counters=off") + (closeDown ? "" : " close-down=off") +
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
                var script = MateScript(opts) ?? new ScriptedAttacker();
                script.Counters = counters;
                var r = RoundRunner.RunFirstShot(tuning, sc, att, kee, seed + (ulong)i, script, followUp: true, defender: defender, mate: mateSheet);
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
        // A round played with the editor's Custom player carries its sheet; any other names a file in data/players.
        var att = replay.ParseAttackerSheet() is { } embedded ? Load(embedded)
            : Load(DataLoader.LoadPlayer(Path.Combine(dataDir, "players", replay.AttackerId + ".json")));
        var kee = Load(DataLoader.LoadPlayer(Path.Combine(dataDir, "players", replay.KeeperId + ".json")));
        if (sc == null || att == null || kee == null) return 1;
        PlayerSheet? def = null;
        if (sc.Defenders > 0)
        {
            def = Load(DataLoader.LoadPlayer(Path.Combine(dataDir, "players", (replay.DefenderId ?? sc.DefaultDefender) + ".json")));
            if (def == null) return 1;
        }
        PlayerSheet? mate = null;
        if (sc.Mate != null)
        {
            mate = Load(DataLoader.LoadPlayer(Path.Combine(dataDir, "players", (replay.MateId ?? sc.DefaultMate) + ".json")));
            if (mate == null) return 1;
        }
        if (!replay.MatchesTuning(tuning))
            Console.WriteLine("Tuning has changed since this replay, so the result may differ.");
        if (replay.Partial)
        {
            // a capture or an unfinished round: the recorded ticks, then the shots so far (there is no report)
            var world = replay.Play(tuning, sc, att, kee, defender: def, mate: mate);
            Console.WriteLine($"replay {path}: scenario={replay.ScenarioId} attacker={replay.AttackerId} keeper={replay.KeeperId} seed={replay.Seed} ticks={replay.TickCount}");
            if (replay.Capture is { } c) Console.WriteLine($"capture {c.Id}: {c.Tag}{(c.Note.Length > 0 ? " · " + c.Note : "")}");
            Console.WriteLine($"partial: {replay.TickCount} ticks, no report");
            for (int i = 0; i < world.Shots.Count; i++)
            {
                var s = world.Shots[i];
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"shot{i} {s.Type} {s.Foot}/{s.Surface} charge {s.Charge:F2} aimed z {s.Aimed.X:F2} h {s.Aimed.Y:F2} landed z {s.Landed.X:F2} h {s.Landed.Y:F2}"));
            }
            if (world.Shots.Count == 0) Console.WriteLine("no shots");
            return 0;
        }
        var report = replay.Run(tuning, sc, att, kee, def, mate);
        Console.WriteLine($"replay {path}: scenario={replay.ScenarioId} attacker={replay.AttackerId} keeper={replay.KeeperId} seed={replay.Seed} ticks={replay.TickCount}");
        Console.WriteLine(report.ToJson());
        return 0;
    }

    /// <summary>The scripted attacker for --style solo|square|through (a scenario with a team-mate); null without the option.</summary>
    static ScriptedAttacker? MateScript(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("style", out var style)) return null;
        return style switch
        {
            "solo" => new ScriptedAttacker { Team = ScriptedAttacker.TeamStyle.Solo },
            "square" => new ScriptedAttacker { Team = ScriptedAttacker.TeamStyle.Square },
            "through" => new ScriptedAttacker { Team = ScriptedAttacker.TeamStyle.Through },
            _ => null,
        };
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
/// planted at the strike, his mean reaction and his speed at the strike, and the saves and dives he made. Then his closing
/// down (mean depth off his line as the attacker on the ball first came within each band of the goal centre), the rounds he
/// won the ball from a dribble (touched it before any shot), the chips tried and scored, and the rounds he was taken round.
/// </summary>
sealed class KeeperStats
{
    readonly List<float> _depth = new(), _dist = new();
    int _onTarget;
    int _n, _shots, _rush, _charge, _smother, _slide, _slideWon, _smotherClaim, _planted, _reactN, _parried;
    double _react, _speed;
    readonly Dictionary<string, int> _after = new(), _save = new(), _dive = new(), _atStrike = new();
    readonly double[] _bandSum = new double[KeeperReport.DepthBands.Length];
    readonly int[] _bandN = new int[KeeperReport.DepthBands.Length];
    int _won, _wonHeld, _chips, _chipsScored, _chipsTipped, _rounded, _roundedScored;
    readonly int[] _saveCount = new int[Enum.GetValues<SaveKind>().Length];
    readonly int[] _typeCount = new int[Enum.GetValues<SaveType>().Length];
    double _difficulty, _ready;
    int _difficultyN, _readyN;
    int _beaten;
    readonly List<int> _rev = new(), _rev2 = new();

    public void Add(RoundRunner.Result r)
    {
        _n++;
        var k = r.Keeper;
        if (k.Rushed) _rush++;
        if (k.Charged) _charge++;
        if (k.Smothered) _smother++;
        if (k.Slid) { _slide++; if (k.WonBall) _slideWon++; }
        if (r.End == ShotEnd.NoShot && r.Outcome == Outcome.Held && k.Save == SaveKind.Smother) _smotherClaim++;
        for (int i = 0; i < _bandSum.Length && i < k.DepthAtBandM.Length; i++)
            if (float.IsFinite(k.DepthAtBandM[i])) { _bandSum[i] += k.DepthAtBandM[i]; _bandN[i]++; }
        _rev.Add(k.DepthReversals);
        _rev2.Add(k.DepthReversalsFirst2S);
        if (k.WonBall) { _won++; if (r.End == ShotEnd.NoShot && r.Outcome == Outcome.Held) _wonHeld++; }
        if (k.Rounded) { _rounded++; if (r.End == ShotEnd.Scored) _roundedScored++; }
        if (r.Shot is { Type: ShotType.Chip })
        {
            _chips++;
            if (r.End == ShotEnd.Scored) _chipsScored++;
            if (k.Save == SaveKind.TipOver) _chipsTipped++;
        }
        if (r.Shot != null)
        {
            _shots++;
            if (k.PlantedAtStrike) _planted++;
            if (k.OnTarget) _onTarget++;
            if (float.IsFinite(k.DepthAtStrikeM)) { _depth.Add(k.DepthAtStrikeM); _dist.Add(k.BallDistanceAtStrikeM); }
            if (float.IsFinite(k.SpeedAtStrikeMs)) _speed += k.SpeedAtStrikeMs;
            if (float.IsFinite(k.ReactionS)) { _react += k.ReactionS; _reactN++; }
            if (k.Save != SaveKind.None) Bump(_save, k.Save.ToString());
            _saveCount[(int)k.Save]++;
            _typeCount[(int)k.SaveType]++;
            if (float.IsFinite(k.Difficulty)) { _difficulty += k.Difficulty; _difficultyN++; }
            if (k.Ready > 0f) { _ready += k.Ready; _readyN++; }
            Bump(_atStrike, $"{k.ActionAtStrike}{(k.SpeedAtStrikeMs <= 0.3f ? "" : k.SpeedAtStrikeMs <= 1.5f ? " <1.5" : k.SpeedAtStrikeMs <= 3f ? " <3" : " 3+")}");
            if (k.Action == KeeperResult.Beaten) _beaten++;
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
        Console.WriteLine($"{prefix}keeper: charge {Pct(_charge)} of rounds, rush {Pct(_rush)}, smother {Pct(_smother)}, slide or poke {Pct(_slide)} (touched the ball {Pct(_slideWon)}) (claimed before a shot {Pct(_smotherClaim)}), " +
                          $"planted at strike {PctOf(_planted, _shots)} of shots, mean reaction {(_react / Math.Max(1, _reactN)).ToString("F3", ic)} s, " +
                          $"mean speed at strike {(_speed / Math.Max(1, _shots)).ToString("F2", ic)} m/s");
        Console.WriteLine($"{prefix}keeper action at the strike (and speed band, m/s; % of shots): {List(_atStrike, c => PctOf(c, _shots))}");
        Console.WriteLine($"{prefix}shots on target (unhindered flight inside the goal) {PctOf(_onTarget, _shots)} of {_shots} shots");
        Console.WriteLine($"{prefix}at strike: keeper off his line {Spread(_depth)} m, ball from goal centre {Spread(_dist)} m");
        Console.WriteLine($"{prefix}after parry/rebound (% of rounds): {List(_after, Pct)}");
        Console.WriteLine($"{prefix}saves (% of rounds): {List(_save, Pct)}");
        Console.WriteLine($"{prefix}dives (% of rounds): {List(_dive, Pct)}");
        int S(SaveKind kind) => _saveCount[(int)kind];
        Console.WriteLine($"{prefix}first-shot save counts: catch {S(SaveKind.Catch)}, parry {S(SaveKind.Parry)}, block {S(SaveKind.Block)}, " +
                          $"tip {S(SaveKind.TipOver)}, fingertip {S(SaveKind.Fingertip)}, smother {S(SaveKind.Smother)}, loose reflex {S(SaveKind.LooseReflex)}, beaten {_beaten} (of {_shots} shots)");
        int T(SaveType type) => _typeCount[(int)type];
        Console.WriteLine($"{prefix}first-shot save types (what he went for): catch {T(SaveType.Catch)}, parry {T(SaveType.Parry)}, reflex {T(SaveType.Reflex)}, " +
                          $"reflex attempt {T(SaveType.ReflexAttempt)}, failed attempt {T(SaveType.FailedAttempt)}, none {T(SaveType.None)}; " +
                          $"mean difficulty {(_difficulty / Math.Max(1, _difficultyN)).ToString("F2", ic)}, ready at the strike {PctOf(_readyN, _shots)} of shots " +
                          $"(mean {(_ready / Math.Max(1, _readyN)).ToString("F2", ic)})");
        var bands = KeeperReport.DepthBands;
        Console.WriteLine($"{prefix}keeper depth off his line as the attacker on the ball came within (m of goal): " + string.Join(", ",
            bands.Select((b, i) => $"{b.ToString("F0", ic)} m: {(_bandN[i] == 0 ? "-" : (_bandSum[i] / _bandN[i]).ToString("F2", ic))} (n {_bandN[i]})")));
        Console.WriteLine($"{prefix}keeper won the ball from a dribble (before any shot) {Pct(_won)} of rounds (held {Pct(_wonHeld)}), " +
                          $"rounded {Pct(_rounded)} (scored after {PctOf(_roundedScored, _rounded)} of those)");
        static string Rev(List<int> v)
        {
            if (v.Count == 0) return "-";
            var o = v.OrderBy(x => x).ToArray();
            return $"mean {o.Average().ToString("F2", CultureInfo.InvariantCulture)}, p95 {o[(int)(0.95 * (o.Length - 1))]}, max {o[^1]}";
        }
        Console.WriteLine($"{prefix}keeper depth reversals before the shot (in/out direction changes above 0.3 m/s held 0.1 s): {Rev(_rev)}; " +
                          $"in the first 2 s: {Rev(_rev2)}");
        Console.WriteLine($"{prefix}chips attempted {Pct(_chips)} of rounds ({_chips}), scored {PctOf(_chipsScored, _chips)} of chips, tipped over {PctOf(_chipsTipped, _chips)}");
    }
}
