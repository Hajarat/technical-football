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
        bool ok = Line("onside, of ticks", 100.0 * onside / Math.Max(1, ticks), 90, 100);
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
            Console.WriteLine($"{w.Tick,6}  {F(c.X).PadLeft(6)},{F(c.Z).PadLeft(6)}  {F(p.X).PadLeft(6)},{F(p.Z).PadLeft(6)} {F(mate.Speed).PadLeft(5)}  {mate.MateRun,-7} {F(w.OffsideLine).PadLeft(6)} {F(w.OffsideLine - p.X).PadLeft(6)}  {F(g.X).PadLeft(6)},{F(g.Y).PadLeft(6)}  {MateBot.Lane(Flat(p), scene, tuning),4}  {F(df.X).PadLeft(6)},{F(df.Z).PadLeft(6)} {w.Defender.DefenderAction}");
        }
        Console.WriteLine($"outcome {w.Outcome} at tick {w.Tick}, runs {mate.MateRuns}");
        return 0;
    }
}
