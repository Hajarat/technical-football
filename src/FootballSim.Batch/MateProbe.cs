using System.Globalization;
using System.Numerics;
using FootballSim.Core;

/// <summary>
/// The team-mate probe: two_v_one rounds in which a scripted carrier dribbles at the defender and never passes, over many seeds
/// (the seed also moves the starts a little), measuring what the team-mate's bot does off the ball against the targets.
///   --mate-probe [--seeds 1000] [--trace --seed n]
/// The carrier jogs at the defender, stops <see cref="HoldDistance"/> short of him and holds the ball there, stepping away when
/// he closes in.
/// </summary>
static class MateProbe
{
    static readonly CultureInfo Ic = CultureInfo.InvariantCulture;
    static string F(double v, int d = 2) => v.ToString("F" + d, Ic);

    /// <summary>The carrier stops this far from the defender, metres.</summary>
    const float HoldDistance = 7f;

    static Vector2 Flat(Vector3 v) => new(v.X, v.Z);

    static TickIntent Carrier(World w)
    {
        var a = w.Attacker;
        if (w.Defender is not { } d) return new TickIntent { MoveX = 1f };
        var to = Flat(d.Position) - Flat(a.Position);
        float dist = to.Length();
        var dir = dist > 1e-3f ? to / dist : new Vector2(1f, 0f);
        if (dist > HoldDistance) return new TickIntent { MoveX = dir.X, MoveZ = dir.Y };
        // He holds the ball; when the defender comes close he moves off across the pitch, away from him and toward its middle.
        if (dist < HoldDistance - 2f)
        {
            float side = a.Position.Z > 0f ? -1f : 1f;
            var across = Vector2.Normalize(new Vector2(-dir.X * 0.4f, side));
            return new TickIntent { MoveX = across.X, MoveZ = across.Y };
        }
        return default;
    }

    /// <summary>The scenario's own starts, kept so each round's can be moved from them.</summary>
    static StartSpot? _attacker, _mate, _defender;

    static (World World, Scenario Scenario) Make(Scenario sc, PlayerSheet a, PlayerSheet k, PlayerSheet d, PlayerSheet m, Tuning tuning, ulong seed)
    {
        _attacker ??= sc.Attacker; _mate ??= sc.Mate!; _defender ??= sc.Defender;
        var r = new SimRng(seed * 7919UL);
        float J(float span) => r.Uniform(-span, span);
        float ax = _attacker.X + J(1.5f), az = _attacker.Z + J(3f);
        sc.Attacker = new StartSpot { X = ax, Z = az, FacingDeg = 0f };
        sc.Ball = new StartSpot { X = ax + 0.5f, Z = az };
        sc.Mate = new StartSpot { X = _mate.X + J(1.5f), Z = _mate.Z + J(3f) };
        sc.Defender = new StartSpot { X = _defender.X + J(2f), Z = _defender.Z + J(2f) };
        var w = new World(tuning, sc, a, k, seed, d, m);
        w.Begin();
        return (w, sc);
    }

    public static int Run(Dictionary<string, string> opts, string dataDir, Tuning tuning)
    {
        int seeds = opts.TryGetValue("seeds", out var ss) ? int.Parse(ss, Ic) : 1000;
        var scr = DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", "two_v_one.json"));
        if (!scr.Ok) { Console.Error.WriteLine("error: " + scr.Error); return 1; }
        var sc = scr.Value!;
        PlayerSheet? P(string key, string def)
        {
            var r = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", (opts.TryGetValue(key, out var v) ? v : def) + ".json"));
            if (!r.Ok) Console.Error.WriteLine("error: " + r.Error);
            return r.Value;
        }
        var a = P("attacker", sc.DefaultAttacker); var k = P("keeper", sc.DefaultKeeper); var d = P("defender", sc.DefaultDefender); var m = P("mate", sc.DefaultMate);
        if (a == null || k == null || d == null || m == null) return 1;
        if (opts.ContainsKey("trace")) return Trace(sc, a, k, d, m, tuning, opts.TryGetValue("seed", out var sd) ? ulong.Parse(sd, Ic) : 1UL);

        long ticks = 0, onside = 0, near = 0, nearTicks = 0, laneOpen = 0, spaced = 0, reversals = 0, moving = 0;
        var offBy = new long[4];
        long tooNear = 0;
        var backFrom = new int[4];
        int longRounds = 0, longWithRun = 0, runs = 0;
        double seconds = 0;
        var outcomes = new SortedDictionary<string, int>();
        for (ulong seed = 1; seed <= (ulong)seeds; seed++)
        {
            var (w, _) = Make(sc, a, k, d, m, tuning, seed);
            var buf = new TickIntent[w.PlayerCount];
            var mate = w.Teams[0][1];
            Vector2 last = default;
            var prevRun = MateRun.Support;
            int roundTicks = 0;
            while (w.State is RoundState.Live or RoundState.Strike && roundTicks < 20 * tuning.TickRate)
            {
                buf[0] = Carrier(w);
                w.Step(buf);
                if (w.State is not (RoundState.Live or RoundState.Strike)) break;
                // (measured while the carrier has the ball: once it is lost the round is over for the probe)
                if (w.ControlledIndex != 0 || w.Events.Any(e => e.Kind == EventKind.Tackle && e.Team == 1)) break;
                roundTicks++; ticks++;
                if (mate.MateRun == MateRun.Back && prevRun != MateRun.Back) backFrom[(int)prevRun]++;
                prevRun = mate.MateRun;
                if (!Offside.Beyond(mate, w.OffsideLine)) onside++; else offBy[(int)mate.MateRun]++;
                float toCarrier = Vector2.Distance(Flat(mate.Position), Flat(w.Attacker.Position));
                if (toCarrier is >= 4f and <= 12f) spaced++; else if (toCarrier < 4f) tooNear++;
                if (w.Defender is { } def && Vector2.Distance(Flat(w.Attacker.Position), Flat(def.Position)) <= 12f)
                {
                    nearTicks++;
                    var scene = MateBot.SceneOf(w, mate);
                    if (MateBot.Lane(Flat(mate.Position), scene, tuning) > 0f) laneOpen++;
                }
                var h = mate.MateHeading;
                if (h != default)
                {
                    moving++;
                    if (last != default && Vector2.Dot(h, last) < 0f) reversals++;
                    last = h;
                }
            }
            seconds += roundTicks / (double)tuning.TickRate;
            runs += mate.MateRuns;
            if (roundTicks > 6 * tuning.TickRate) { longRounds++; if (mate.MateRuns > 0) longWithRun++; }
            string o = w.Outcome.ToString();
            outcomes[o] = outcomes.GetValueOrDefault(o) + 1;
        }
        Console.WriteLine($"team-mate probe: {seeds} rounds of two_v_one, the carrier dribbles at the defender and holds the ball {F(HoldDistance, 0)} m short of him");
        Console.WriteLine($"   rounds lasted {F(seconds / seeds, 1)} s on average ({longRounds} longer than 6 s); {F((double)runs / seeds, 2)} runs a round; outcomes: {string.Join(", ", outcomes.Select(p => $"{p.Key} {p.Value}"))}");
        Console.WriteLine($"   offside ticks by what he was doing: support {F(100.0 * offBy[0] / Math.Max(1, ticks), 1)}%, armed {F(100.0 * offBy[1] / Math.Max(1, ticks), 1)}%, go {F(100.0 * offBy[2] / Math.Max(1, ticks), 1)}%, back {F(100.0 * offBy[3] / Math.Max(1, ticks), 1)}%");
        Console.WriteLine($"   came back onside {backFrom.Sum()} times: caught by the line in support {backFrom[0]}, armed {backFrom[1]}; after a run {backFrom[2]}");
        Console.WriteLine($"   nearer the carrier than 4 m in {F(100.0 * tooNear / Math.Max(1, ticks), 1)}% of ticks, further than 12 m in {F(100.0 * (ticks - spaced - tooNear) / Math.Max(1, ticks), 1)}%");
        bool ok = Line("onside, of ticks", 100.0 * onside / Math.Max(1, ticks), 75, 100);
        ok &= Line("a ground-pass lane open, of the ticks the carrier is within 12 m of the defender", 100.0 * laneOpen / Math.Max(1, nearTicks), 70, 100);
        ok &= Line("4 to 12 m from the carrier, of ticks", 100.0 * spaced / Math.Max(1, ticks), 85, 100);
        ok &= Line("direction reversals per second", reversals / Math.Max(1e-9, seconds), 0, 1, "");
        ok &= Line("a run in behind, of rounds longer than 6 s", 100.0 * longWithRun / Math.Max(1, longRounds), 60, 100);
        Console.WriteLine(ok ? "team-mate probe: every target met" : "team-mate probe: TARGETS MISSED");
        return ok ? 0 : 3;
    }

    static bool Line(string what, double value, double lo, double hi, string unit = "%")
    {
        bool inside = value >= lo - 1e-9 && value <= hi + 1e-9;
        Console.WriteLine($"   {(inside ? "ok  " : "OUT ")} {what}: {F(value, unit == "" ? 2 : 1)}{unit} (target {F(lo, 0)}-{F(hi, 0)}{unit})");
        return inside;
    }

    static int Trace(Scenario sc, PlayerSheet a, PlayerSheet k, PlayerSheet d, PlayerSheet m, Tuning tuning, ulong seed)
    {
        var (w, _) = Make(sc, a, k, d, m, tuning, seed);
        var buf = new TickIntent[w.PlayerCount];
        var mate = w.Teams[0][1];
        Console.WriteLine($"trace team-mate: seed={seed}");
        Console.WriteLine("  tick  carrier(x,z)    mate(x,z)       v   run      line  onside  slope(x,z)     lane  defender(x,z)");
        for (int i = 0; i < 20 * tuning.TickRate && w.State is RoundState.Live or RoundState.Strike; i++)
        {
            buf[0] = Carrier(w);
            w.Step(buf);
            if (w.ControlledIndex != 0 || w.Events.Any(e => e.Kind == EventKind.Tackle && e.Team == 1)) break;
            if (w.Tick % 12 != 0) continue;
            var scene = MateBot.SceneOf(w, mate);
            var g = MateBot.Gradient(Flat(mate.Position), scene, tuning);
            var c = w.Attacker.Position; var p = mate.Position; var df = w.Defender!.Position;
            Console.WriteLine($"{w.Tick,6}  {F(c.X).PadLeft(6)},{F(c.Z).PadLeft(6)}  {F(p.X).PadLeft(6)},{F(p.Z).PadLeft(6)} {F(mate.Speed).PadLeft(5)}  {mate.MateRun,-7} {F(w.OffsideLine).PadLeft(6)} {F(w.OffsideLine - p.X).PadLeft(6)}  {F(g.X).PadLeft(6)},{F(g.Y).PadLeft(6)}  {MateBot.Lane(Flat(p), scene, tuning),4}  {F(df.X).PadLeft(6)},{F(df.Z).PadLeft(6)} {w.Defender.DefenderAction} {w.DefenderBot?.Mode}");
        }
        Console.WriteLine($"outcome {w.Outcome} at tick {w.Tick}, runs {mate.MateRuns}");
        return 0;
    }

    /// <summary>
    /// --trace-team seed [--style solo|square|through] [--every 12]: one scripted round of a scenario with a team-mate, a line
    /// every few ticks and on every pass, control change, touch, tackle, strike and offside.
    /// </summary>
    public static int TraceTeam(Tuning tuning, Scenario sc, PlayerSheet a, PlayerSheet k, PlayerSheet? d, PlayerSheet? m, ScriptedAttacker script, ulong seed, int every)
    {
        var w = new World(tuning, sc, a, k, seed, d, m);
        w.Begin();
        var buf = new TickIntent[w.PlayerCount];
        Console.WriteLine($"trace team: scenario={sc.Id} seed={seed} style={script.Team}");
        Console.WriteLine("  tick state   ctl  p0(x,z)        p1(x,z)       run      ball(x,z)      bv   line  defender(x,z)        events");
        for (int i = 0; i < 40 * tuning.TickRate && w.State != RoundState.Report; i++)
        {
            buf[0] = script.Next(w);
            w.Step(buf);
            var ev = w.Events.Where(e => e.Kind is EventKind.Pass or EventKind.ControlChanged or EventKind.Offside or EventKind.Strike or EventKind.Tackle
                or EventKind.Block or EventKind.Save or EventKind.Goal or EventKind.PossessionGained or EventKind.PossessionLost)
                .Select(e => e.Kind == EventKind.ControlChanged ? $"ControlChanged[{e.Other}>{e.PlayerIndex} {e.Reason}]" : $"{e.Kind}[{e.Team}.{e.PlayerIndex}]").ToList();
            if (w.Tick % every != 0 && ev.Count == 0) continue;
            var p0 = w.Teams[0][0].Position; var b = w.Ball;
            string p1 = w.HasMates ? $"{F(w.Teams[0][1].Position.X).PadLeft(6)},{F(w.Teams[0][1].Position.Z).PadLeft(6)} {w.Teams[0][1].MateRun,-7}" : "".PadLeft(21);
            string def = w.Defender is { } df ? $"{F(df.Position.X).PadLeft(6)},{F(df.Position.Z).PadLeft(6)} {df.DefenderAction,-7}" : "";
            Console.WriteLine($"{w.Tick,6} {w.State,-7} {w.ControlledIndex,3}  {F(p0.X).PadLeft(6)},{F(p0.Z).PadLeft(6)}  {p1}  {F(b.Position.X).PadLeft(6)},{F(b.Position.Z).PadLeft(6)} {F(b.Velocity.Length()).PadLeft(5)} {(w.OffsideOn ? F(w.OffsideLine) : "-").PadLeft(6)}  {def}  {string.Join(" ", ev)}");
        }
        Console.WriteLine($"outcome {w.Outcome} at tick {w.Tick}; passes {w.Passes} (received {w.PassesReceived}), offside {w.Offsides}, shots {w.Shots.Count}");
        return 0;
    }

    /// <summary>
    /// The bands of the team probe: rounds that end in a goal, percent, per scripted style (regression rows set about five points
    /// either side of the measured rates, not difficulty targets; the legacy defender bot keeps its own), and how far the square
    /// pass has to beat playing alone, points.
    /// </summary>
    public static readonly Dictionary<string, (float Lo, float Hi)> GoalBands = new()
    {
        ["solo"] = (2f, 12f), ["square"] = (18f, 28f), ["through"] = (0f, 9f),
    };
    public static readonly Dictionary<string, (float Lo, float Hi)> LegacyGoalBands = new()
    {
        ["solo"] = (3f, 10f), ["square"] = (13f, 24f), ["through"] = (15f, 28f),
    };
    public const float SquareOverSolo = 10f;
    /// <summary>
    /// two_v_one may take no more than this many times as long as defender_duel: 10,000 rounds played alone (the same script,
    /// so the rounds are alike), and a second of play in every style (the passing styles' rounds last about twice as long).
    /// </summary>
    public const float SpeedRatioMax = 2f;

    /// <summary>
    /// --probe team [--n 2000] [--seed 1] [--styles solo,square,through] [--speed 1]: scripted rounds of two_v_one per style with
    /// how each ended and what the pair did, against the bands; with --speed, the time 10,000 rounds take beside defender_duel's.
    /// </summary>
    public static int RunTeam(Dictionary<string, string> opts, string dataDir, Tuning tuning)
    {
        int n = opts.TryGetValue("n", out var ns) ? int.Parse(ns, Ic) : 2000;
        ulong seed = opts.TryGetValue("seed", out var sd) ? ulong.Parse(sd, Ic) : 1UL;
        var styles = (opts.TryGetValue("styles", out var st) ? st : "solo,square,through").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var scr = DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", "two_v_one.json"));
        if (!scr.Ok) { Console.Error.WriteLine("error: " + scr.Error); return 1; }
        var sc = scr.Value!;
        PlayerSheet? P(string key, string def)
        {
            var r = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", (opts.TryGetValue(key, out var v) ? v : def) + ".json"));
            if (!r.Ok) Console.Error.WriteLine("error: " + r.Error);
            return r.Value;
        }
        var a = P("attacker", sc.DefaultAttacker); var k = P("keeper", sc.DefaultKeeper); var d = P("defender", sc.DefaultDefender); var m = P("mate", sc.DefaultMate);
        if (a == null || k == null || d == null || m == null) return 1;
        Console.WriteLine($"team probe: {n} rounds of two_v_one per style, attacker {a.Id}, team-mate {m.Id}, defender {d.Id}, keeper {k.Id}");
        Console.WriteLine("  style     goal%  held%  dispossessed%  offside%  time_up%  other%   passes/round  received%  shots/round  seconds/round");
        bool ok = true;
        var goal = new Dictionary<string, double>();
        foreach (string style in styles)
        {
            if (!Enum.TryParse<ScriptedAttacker.TeamStyle>(style, true, out var team)) { Console.Error.WriteLine($"unknown style '{style}'"); return 2; }
            var outcomes = new int[Enum.GetValues<Outcome>().Length];
            long passes = 0, received = 0, shots = 0;
            double clock = 0;
            for (int i = 0; i < n; i++)
            {
                var r = RoundRunner.RunToReport(tuning, sc, a, k, seed + (ulong)i, out _, new ScriptedAttacker { Team = team }, defender: d, mate: m);
                outcomes[(int)r.Outcome]++;
                passes += r.Team?.Passes ?? 0; received += r.Team?.Received ?? 0; shots += r.Shots.Count; clock += r.Clock;
            }
            double Pct(Outcome o) => 100.0 * outcomes[(int)o] / n;
            double other = 100.0 - Pct(Outcome.Goal) - Pct(Outcome.Held) - Pct(Outcome.Dispossessed) - Pct(Outcome.Offside) - Pct(Outcome.TimeUp);
            goal[style] = Pct(Outcome.Goal);
            Console.WriteLine($"  {style,-8} {F(Pct(Outcome.Goal), 1),6} {F(Pct(Outcome.Held), 1),6} {F(Pct(Outcome.Dispossessed), 1),14} {F(Pct(Outcome.Offside), 1),9} {F(Pct(Outcome.TimeUp), 1),9} {F(other, 1),7} {F((double)passes / n, 2),14} {F(100.0 * received / Math.Max(1, passes), 1),10} {F((double)shots / n, 2),12} {F(clock / n, 1),14}");
        }
        foreach (string style in styles)
            if ((tuning.Defender.Brain == "legacy" ? LegacyGoalBands : GoalBands).TryGetValue(style, out var band)) ok &= Line($"{style}: rounds that end in a goal", goal[style], band.Lo, band.Hi);
        if (goal.ContainsKey("solo") && goal.ContainsKey("square"))
            ok &= Line("square over solo, points", goal["square"] - goal["solo"], SquareOverSolo, 100, "");
        if (opts.ContainsKey("speed"))
        {
            // The same script in both (it plays alone), so the rounds are alike and the time compares the simulation; then
            // the passing styles, whose rounds last longer and have more shots, by the time a simulated second takes.
            var duel = DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", "defender_duel.json")).Value!;
            (double Seconds, long Ticks) Time(Scenario s, PlayerSheet? mate, ScriptedAttacker.TeamStyle team)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long ticks = 0;
                for (int i = 0; i < 10000; i++)
                {
                    RoundRunner.RunToReport(tuning, s, a, k, seed + (ulong)i, out var replay, new ScriptedAttacker { Team = team }, defender: d, mate: mate);
                    ticks += replay.TickCount;
                }
                return (sw.Elapsed.TotalSeconds, ticks);
            }
            var tDuel = Time(duel, null, ScriptedAttacker.TeamStyle.Solo);
            Console.WriteLine($"  10,000 rounds: defender_duel {F(tDuel.Seconds, 1)} s ({F(tDuel.Ticks / 120.0 / 10000, 1)} s of play a round)");
            foreach (var team in new[] { ScriptedAttacker.TeamStyle.Solo, ScriptedAttacker.TeamStyle.Square, ScriptedAttacker.TeamStyle.Through })
            {
                var tt = Time(sc, m, team);
                double total = tt.Seconds / Math.Max(1e-9, tDuel.Seconds);
                double perTick = tt.Seconds / Math.Max(1, tt.Ticks) / (tDuel.Seconds / Math.Max(1, tDuel.Ticks));
                Console.WriteLine($"  10,000 rounds: two_v_one {team.ToString().ToLowerInvariant()} {F(tt.Seconds, 1)} s ({F(tt.Ticks / 120.0 / 10000, 1)} s of play a round): {F(total)} times defender_duel, {F(perTick)} times per second of play");
                ok &= Line($"two_v_one {team.ToString().ToLowerInvariant()} over defender_duel, per second of play", perTick, 0, SpeedRatioMax, "");
                if (team == ScriptedAttacker.TeamStyle.Solo) ok &= Line("two_v_one solo over defender_duel, 10,000 rounds", total, 0, SpeedRatioMax, "");
            }
        }
        Console.WriteLine(ok ? "team probe: every band met" : "team probe: BANDS MISSED");
        return ok ? 0 : 3;
    }
}
