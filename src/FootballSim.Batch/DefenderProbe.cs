using System.Globalization;
using System.Numerics;
using FootballSim.Core;

/// <summary>
/// The defender probe: scripted rounds of the defender duel in three attacker styles (straight at him, a cut at four metres,
/// a skill move at three), with how each ended, what the defender did, and the bands those numbers are held to.
///   --probe defender [--n 2000] [--seed 1] [--styles straight,cut,skill] [--scenario defender_duel]
///                    [--attacker id] [--keeper id] [--defender id] [--sweep awareness=1,50,99]
/// Also the defender's trace (--trace-defender) and one replay per defender action (--write-defender-replays).
/// </summary>
static class DefenderProbe
{
    static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

    /// <summary>
    /// The bands per style: the share of rounds in which the defender wins the ball from the dribble, that is, the round ends
    /// DISPOSSESSED with no shot struck, in percent. (The scripted attacker stands still after its shot, so the rounds that end
    /// DISPOSSESSED after one say little about the duel; they are printed beside it.) They are regression rows set about five
    /// points either side of the measured rates, not difficulty targets; the legacy bot (defender.brain = legacy) keeps its own.
    /// </summary>
    public static readonly Dictionary<string, (float Lo, float Hi)> DispossessedBands = new()
    {
        ["straight"] = (85f, 95f), ["cut"] = (19f, 29f), ["skill"] = (10f, 20f),
    };
    public static readonly Dictionary<string, (float Lo, float Hi)> LegacyDispossessedBands = new()
    {
        ["straight"] = (65f, 85f), ["cut"] = (30f, 45f), ["skill"] = (20f, 35f),
    };
    /// <summary>Shots blocked, percent of all the styles' shots together (the legacy bot's beside it), and fouls, percent of rounds (every style).</summary>
    public static readonly (float Lo, float Hi) BlockedBand = (31f, 41f);
    public static readonly (float Lo, float Hi) LegacyBlockedBand = (10f, 25f);
    public const float FoulMax = 5f;

    public sealed class Tally
    {
        public int N, Shots, Blocks, Fouls, AutoWon, LungesTried, LungesWon, SlidesTried, SlidesWon, Barges, BargesWon, KeeperCharged, KeeperRushed;
        public readonly int[] Outcomes = new int[Enum.GetValues<Outcome>().Length];
        public readonly int[] WonBy = new int[Enum.GetValues<TackleKind>().Length];
        public double Active, Clock;
        /// <summary>Rounds that ended DISPOSSESSED with no shot struck: the ball was won from the dribble.</summary>
        public int CleanWins;
        public float Pct(Outcome o) => 100f * Outcomes[(int)o] / Math.Max(1, N);
        public float BlockedPct => 100f * Blocks / Math.Max(1, Shots);
        public float FoulPct => 100f * Fouls / Math.Max(1, N);

        public void Add(RoundReport r)
        {
            N++;
            Outcomes[(int)r.Outcome]++;
            Shots += r.Shots.Count;
            if (r.Outcome == Outcome.Dispossessed && r.Shots.Count == 0) CleanWins++;
            Clock += r.Clock;
            if (r.Keeper.Charged) KeeperCharged++;
            if (r.Keeper.Rushed) KeeperRushed++;
            if (r.Defender is not { } d) return;
            Blocks += d.Blocks; Fouls += d.Fouls; AutoWon += d.AutoWon;
            LungesTried += d.LungesTried; LungesWon += d.LungesWon; SlidesTried += d.SlidesTried; SlidesWon += d.SlidesWon;
            Barges += d.Barges; BargesWon += d.BargesWon;
            Active += d.TimeActive;
            if (d.Collected) WonBy[(int)d.WonBy]++;
        }
    }

    /// <summary>Overrides of the scripted styles from the command line (--cut-angle, --cut-distance, --skill-distance, --skill-key, --style-close), for experiments.</summary>
    public static Dictionary<string, string> Overrides = new();

    public static ScriptedAttacker Script(string style)
    {
        var s = new ScriptedAttacker
        {
            Style = style switch
            {
                "straight" => ScriptedAttacker.AttackStyle.Straight,
                "cut" => ScriptedAttacker.AttackStyle.Cut,
                "skill" => ScriptedAttacker.AttackStyle.Skill,
                "shoot" => ScriptedAttacker.AttackStyle.Shoot,
                _ => ScriptedAttacker.AttackStyle.Default,
            },
        };
        float F(string key, float def) => Overrides.TryGetValue(key, out var v) ? float.Parse(v, Ic) : def;
        s.CutAngleDeg = F("cut-angle", s.CutAngleDeg);
        s.CutDistance = F("cut-distance", s.CutDistance);
        s.SkillDistance = F("skill-distance", s.SkillDistance);
        s.ShootGap = F("shoot-gap", s.ShootGap);
        s.StyleCloseRange = F("style-close", s.StyleCloseRange);
        if (Overrides.TryGetValue("skill-key", out var sk)) s.SkillMove = Enum.Parse<SkillKey>(sk, ignoreCase: true);
        return s;
    }

    /// <summary>Plays <paramref name="n"/> scripted rounds of one style to their reports (in parallel; each depends only on its seed).</summary>
    public static Tally Run(Tuning tuning, Scenario sc, PlayerSheet attacker, PlayerSheet keeper, PlayerSheet defender, string style, int n, ulong seed)
    {
        var reports = new RoundReport[n];
        Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
            reports[i] = RoundRunner.RunToReport(tuning, sc, attacker, keeper, seed + (ulong)i, out _, Script(style), defender));
        var t = new Tally();
        foreach (var r in reports) t.Add(r);
        return t;
    }

    public static int Run(Dictionary<string, string> opts, string dataDir, Tuning tuning)
    {
        if (!Load(opts, dataDir, out var sc, out var attacker, out var keeper, out var defender)) return 1;
        Overrides = opts;
        int n = opts.TryGetValue("n", out var ns) ? int.Parse(ns, Ic) : 2000;
        ulong seed = opts.TryGetValue("seed", out var ss) ? ulong.Parse(ss, Ic) : 1UL;
        var styles = (opts.TryGetValue("styles", out var st) ? st : "straight,cut,skill,shoot").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool legacy = tuning.Defender.Brain == "legacy";
        string field = "";
        var values = new List<int?> { null };
        if (opts.TryGetValue("sweep", out var sweep))
        {
            int eq = sweep.IndexOf('=');
            if (eq < 1) { Console.Error.WriteLine("--sweep needs <defender rating>=<v1,v2,...>"); return 2; }
            field = sweep[..eq];
            values = sweep[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(v => (int?)int.Parse(v, Ic)).ToList();
        }
        Console.WriteLine($"defender probe: scenario={sc.Id} attacker={attacker.Id} keeper={keeper.Id} defender={defender.Id} n={n} seed={seed}" + (field != "" ? $" sweep=defender.{field}" : ""));
        bool ok = true;
        foreach (var v in values)
        {
            var def = defender;
            if (v is int val) { def = defender.Clone(); def.SetRating(field, val); Console.WriteLine($"-- {field}={val}"); }
            Console.WriteLine("style      dispossessed (no shot) goal   held  time_up  foul   shots  blocked  | auto  lunge(won/tried)  slide(won/tried)  barge(won/thrown)  | won by auto/lunge/slide/other | active s  round s | keeper charge/rush");
            int shots = 0, blocks = 0;
            foreach (var style in styles)
            {
                var t = Run(tuning, sc, attacker, keeper, def, style, n, seed);
                shots += t.Shots; blocks += t.Blocks;
                string F(float x) => x.ToString("F1", Ic).PadLeft(5);
                Console.WriteLine($"{style,-10} {F(t.Pct(Outcome.Dispossessed))}%   ({F(100f * t.CleanWins / Math.Max(1, t.N))}%) {F(t.Pct(Outcome.Goal))}% {F(t.Pct(Outcome.Held))}% {F(t.Pct(Outcome.TimeUp))}%  {F(t.FoulPct)}% {t.Shots,6}  {F(t.BlockedPct)}%  | {t.AutoWon,4}  {t.LungesWon,5}/{t.LungesTried,-5}       {t.SlidesWon,5}/{t.SlidesTried,-5}       {t.BargesWon,5}/{t.Barges,-5}        | " +
                                  $"{t.WonBy[(int)TackleKind.Auto]}/{t.WonBy[(int)TackleKind.Lunge]}/{t.WonBy[(int)TackleKind.Slide]}/{t.WonBy[(int)TackleKind.None]} | " +
                                  $"{(t.Active / Math.Max(1, t.N)).ToString("F2", Ic),7}  {(t.Clock / Math.Max(1, t.N)).ToString("F2", Ic),6}  | {F(100f * t.KeeperCharged / Math.Max(1, t.N))}%/{F(100f * t.KeeperRushed / Math.Max(1, t.N))}%");
                if (v != null) continue;
                if ((legacy ? LegacyDispossessedBands : DispossessedBands).TryGetValue(style, out var band))
                    ok &= Check($"{style}: ball won from the dribble (DISPOSSESSED, no shot)", 100f * t.CleanWins / Math.Max(1, t.N), band.Lo, band.Hi);
                ok &= Check($"{style}: fouls", t.FoulPct, 0f, FoulMax);
            }
            var blockedBand = legacy ? LegacyBlockedBand : BlockedBand;
            if (v == null && styles.Length == 4) ok &= Check("all styles: shots blocked", 100f * blocks / Math.Max(1, shots), blockedBand.Lo, blockedBand.Hi);
        }
        Console.WriteLine(ok ? "bands: all inside" : "bands: OUTSIDE (see above)");
        return ok ? 0 : 3;
    }

    static bool Check(string what, float value, float lo, float hi)
    {
        bool inside = value >= lo && value <= hi;
        Console.WriteLine($"   {(inside ? "ok  " : "OUT ")} {what} {value.ToString("F1", Ic)}% (band {lo.ToString("F0", Ic)}-{hi.ToString("F0", Ic)}%)");
        return inside;
    }

    public static bool Load(Dictionary<string, string> opts, string dataDir, out Scenario sc, out PlayerSheet attacker, out PlayerSheet keeper, out PlayerSheet defender)
    {
        sc = null!; attacker = keeper = defender = null!;
        string id = opts.TryGetValue("scenario", out var s) ? s : "defender_duel";
        var scr = DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", id + ".json"));
        if (!scr.Ok) { Console.Error.WriteLine("error: " + scr.Error); return false; }
        sc = scr.Value!;
        if (sc.Defenders < 1) { Console.Error.WriteLine($"error: scenario '{id}' has no defender"); return false; }
        PlayerSheet? P(string key, string def)
        {
            var r = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", (opts.TryGetValue(key, out var v) ? v : def) + ".json"));
            if (!r.Ok) Console.Error.WriteLine("error: " + r.Error);
            return r.Value;
        }
        var a = P("attacker", sc.DefaultAttacker); var k = P("keeper", sc.DefaultKeeper); var d = P("defender", sc.DefaultDefender);
        if (a == null || k == null || d == null) return false;
        attacker = a; keeper = k; defender = d;
        return true;
    }

    /// <summary>Prints one scripted round every <paramref name="every"/> ticks, and every tick something happened.</summary>
    public static int Trace(Dictionary<string, string> opts, string dataDir, Tuning tuning, ulong seed)
    {
        if (!Load(opts, dataDir, out var sc, out var attacker, out var keeper, out var defender)) return 1;
        Overrides = opts;
        string style = opts.TryGetValue("style", out var st) ? st : "straight";
        int every = opts.TryGetValue("every", out var es) ? int.Parse(es, Ic) : 12;
        var w = new World(tuning, sc, attacker, keeper, seed, defender);
        var script = Script(style);
        w.Begin();
        var buf = new TickIntent[w.PlayerCount];
        Console.WriteLine($"trace defender: scenario={sc.Id} style={style} seed={seed} defender={defender.Id}");
        Console.WriteLine("  tick     t  state    mode     action     def(x,z)       v    att(x,z)       v    ball(x,z,y)          gap   ctl  events");
        var last = DefenderAction.Hold;
        string F(float x, int wd = 6) => x.ToString("F2", Ic).PadLeft(wd);
        for (int i = 0; i < 20000 && w.State != RoundState.Report; i++)
        {
            buf[0] = script.Next(w);
            w.Step(buf);
            var d = w.Defender!; var a = w.Attacker; var b = w.Ball.Position;
            var ev = w.Events.Where(e => e.Kind is EventKind.Tackle or EventKind.TackleStart or EventKind.Block or EventKind.Barge or EventKind.Foul or EventKind.Strike or EventKind.Save or EventKind.Goal
                or EventKind.PossessionGained or EventKind.PossessionLost).Select(e => $"{e.Kind}{(e.Team >= 0 ? $"[{e.Team}.{e.PlayerIndex}]" : "")}").ToList();
            if (w.Tick % every != 0 && ev.Count == 0 && d.DefenderAction == last) continue;
            last = d.DefenderAction;
            string ctl = w.BallController == null ? "-" : w.BallController == a ? "att" : w.BallController == d ? "def" : "kee";
            Console.WriteLine($"{w.Tick,6} {F(w.SimTime)}  {w.State,-7}  {w.DefenderBot!.Mode,-7}  {d.DefenderAction,-9}  {F(d.Position.X)},{F(d.Position.Z)} {F(d.Speed, 5)}  {F(a.Position.X)},{F(a.Position.Z)} {F(a.Speed, 5)}  {F(b.X)},{F(b.Z)},{F(b.Y, 5)}  {F(Vector3.Distance(d.Position, a.Position), 5)}  {ctl,-4} {string.Join(" ", ev)}");
        }
        Console.WriteLine($"outcome {w.Outcome.Label()} at {w.Report?.Clock.ToString("F2", Ic)} s");
        if (w.Report != null) Console.WriteLine(w.Report.ToJson());
        return 0;
    }

    /// <summary>
    /// Searches scripted rounds for one that shows each defender action or ending and writes a replay of each
    /// (defender-&lt;what&gt;.json), for the game's screenshots and the smoke test.
    /// </summary>
    public static int WriteReplays(string dir, Dictionary<string, string> opts, string dataDir, Tuning tuning)
    {
        if (!Load(opts, dataDir, out var sc, out var attacker, out var keeper, out var defender)) return 1;
        Directory.CreateDirectory(dir);
        var wanted = new (string Name, string Style, Func<RoundReport, HashSet<DefenderAction>, bool> Is)[]
        {
            ("hold", "straight", (r, seen) => seen.Contains(DefenderAction.Hold) && seen.Contains(DefenderAction.Jockey)),
            ("press", "cut", (r, seen) => seen.Contains(DefenderAction.Press) && r.Outcome == Outcome.Goal),
            ("auto-tackle", "straight", (r, seen) => r.Defender!.AutoWon > 0 && r.Outcome == Outcome.Dispossessed),
            ("lunge", "cut", (r, seen) => r.Defender!.LungesWon > 0),
            ("lunge-miss", "skill", (r, seen) => r.Defender!.LungesTried > r.Defender.LungesWon && r.Defender.Fouls == 0),
            ("slide", "cut", (r, seen) => r.Defender!.SlidesWon > 0),
            ("barge", "cut", (r, seen) => r.Defender!.BargesWon > 0),
            ("block", "shoot", (r, seen) => r.Defender!.Blocks > 0 && r.Shots.Count == 1),
            ("collect", "straight", (r, seen) => r.Outcome == Outcome.Dispossessed && seen.Contains(DefenderAction.Collect)),
            ("foul", "cut", (r, seen) => r.Outcome == Outcome.Foul),
        };
        int missing = 0;
        foreach (var (name, style, isIt) in wanted)
        {
            bool found = false;
            for (ulong seed = 1; seed <= 3000 && !found; seed++)
            {
                var w = new World(tuning, sc, attacker, keeper, seed, defender);
                var rec = new ReplayRecorder(w, attacker.Id, keeper.Id, sc.Id, defender.Id);
                w.Recorder = rec;
                var script = Script(style);
                var seen = new HashSet<DefenderAction>();
                w.Begin();
                var buf = new TickIntent[w.PlayerCount];
                for (int i = 0; i < 20000 && w.State != RoundState.Report; i++)
                {
                    buf[0] = script.Next(w);
                    w.Step(buf);
                    seen.Add(w.Defender!.DefenderAction);
                }
                if (w.Report == null || !isIt(w.Report, seen)) continue;
                string path = Path.Combine(dir, $"defender-{name}.json");
                rec.Replay.Save(path);
                Console.WriteLine($"wrote {path}: style {style}, seed {seed}, {rec.Replay.TickCount} ticks, outcome {w.Report.Outcome.Label()}");
                found = true;
            }
            if (!found) { Console.WriteLine($"no round found for '{name}' in 3000 seeds"); missing++; }
        }
        return missing == 0 ? 0 : 3;
    }
}
