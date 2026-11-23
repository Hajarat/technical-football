using System.Globalization;
using System.Numerics;
using FootballSim.Core;

/// <summary>
/// The lock-on probe: scripted rounds on a loose ball, one case per rule, each over many seeds (the seed also moves the start a
/// little, so the cases are not one geometry), with the targets the numbers are held to.
///   --lock-probe [--seeds 1000] [--only behind,still_shot,first_time,flick,gesture,race,slide] [--trace case --seed n]
/// The keeper holds his line in every case, so what is measured is the claim and not his rush.
/// </summary>
static class LockProbe
{
    static readonly CultureInfo Ic = CultureInfo.InvariantCulture;
    static string F(double v, int d = 2) => v.ToString("F" + d, Ic);

    public static readonly string[] Cases = { "behind", "still_shot", "first_time", "flick", "gesture", "race", "slide" };

    sealed class Setup
    {
        public Scenario Scenario = null!;
        public PlayerSheet Attacker = null!, Keeper = null!, Defender = null!;
        public Tuning Tuning = null!;
    }

    /// <summary>A loose-ball start in the arena: the ball at (bx, bz) rolling at <paramref name="speed"/> along <paramref name="headingDeg"/>, the attacker at (ax, az).</summary>
    static Scenario Start(Setup s, float bx, float bz, float speed, float headingDeg, float ax, float az, float facingDeg = 0f, (float X, float Z)? defender = null)
    {
        var b = s.Scenario;
        return new Scenario
        {
            Id = "lock_probe", Name = "Lock-on probe", Pitch = b.Pitch, TeamSize = 1,
            Ball = new StartSpot { X = bx, Z = bz }, BallSpeed = speed, BallHeadingDeg = headingDeg, BallLoose = true,
            Attacker = new StartSpot { X = ax, Z = az, FacingDeg = facingDeg },
            KeeperOnLine = true, KeeperHoldLine = true, KeeperHoldLineThroughDribble = true,
            Defenders = defender != null ? 1 : 0,
            Defender = defender is { } d ? new StartSpot { X = d.X, Z = d.Z } : new StartSpot(),
            ClockLimit = 20f,
        };
    }

    static World Make(Setup s, Scenario sc, ulong seed)
    {
        var w = new World(s.Tuning, sc, s.Attacker, s.Keeper, seed, sc.Defenders > 0 ? s.Defender : null);
        w.Begin();
        return w;
    }

    static bool Has(World w, EventKind kind, int team) => w.Events.Any(e => e.Kind == kind && e.Team == team);

    public static int Run(Dictionary<string, string> opts, string dataDir, Tuning tuning)
    {
        int seeds = opts.TryGetValue("seeds", out var ss) ? int.Parse(ss, Ic) : 1000;
        var only = opts.TryGetValue("only", out var o) ? o.Split(',', StringSplitOptions.RemoveEmptyEntries) : Cases;
        var scr = DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", "loose_parry.json"));
        if (!scr.Ok) { Console.Error.WriteLine("error: " + scr.Error); return 1; }
        PlayerSheet? P(string key, string def)
        {
            var r = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", (opts.TryGetValue(key, out var v) ? v : def) + ".json"));
            if (!r.Ok) Console.Error.WriteLine("error: " + r.Error);
            return r.Value;
        }
        var a = P("attacker", "striker_a"); var k = P("keeper", "the_wall"); var d = P("defender", "centre_back");
        if (a == null || k == null || d == null) return 1;
        var s = new Setup { Scenario = scr.Value!, Attacker = a, Keeper = k, Defender = d, Tuning = tuning };
        if (opts.TryGetValue("trace", out var traceCase))
            return Trace(s, traceCase, opts.TryGetValue("seed", out var sd) ? ulong.Parse(sd, Ic) : 1UL);

        Console.WriteLine($"lock-on probe: {seeds} seeds per case, attacker {a.Id}, defender {d.Id}");
        bool ok = true;
        foreach (string c in Cases)
        {
            if (!only.Contains(c)) continue;
            switch (c)
            {
                case "behind": ok &= Behind(s, seeds); break;
                case "still_shot": ok &= StillShot(s, seeds); break;
                case "first_time": ok &= FirstTime(s, seeds); break;
                case "flick": ok &= FlickCase(s, seeds); break;
                case "gesture": ok &= GestureCase(s, seeds); break;
                case "race": ok &= Race(s, seeds); break;
                case "slide": ok &= Slide(s, seeds); break;
                default: Console.Error.WriteLine($"Unknown case '{c}' (known: {string.Join(", ", Cases)})"); return 2;
            }
        }
        Console.WriteLine(ok ? "lock-on probe: every target met" : "lock-on probe: TARGETS MISSED");
        return ok ? 0 : 3;
    }

    static bool Line(string what, double value, double lo, double hi, string unit = "%")
    {
        bool inside = value >= lo - 1e-9 && value <= hi + 1e-9;
        Console.WriteLine($"   {(inside ? "ok  " : "OUT ")} {what}: {F(value, 1)}{unit} (target {F(lo, lo % 1 == 0 ? 0 : 1)}{(hi >= 1e6 ? "+" : "-" + F(hi, hi % 1 == 0 ? 0 : 1))}{unit})");
        return inside;
    }

    static float Jit(SimRng r, float span) => r.Uniform(-span, span);

    // ---- the cases ----

    /// <summary>Ball 4 m behind him rolling away at 2 m/s, stick at rest: he turns and gets it, when the first solve said he would.</summary>
    static bool Behind(Setup s, int seeds)
    {
        int touched = 0, onTime = 0;
        double ratio = 0;
        for (ulong seed = 1; seed <= (ulong)seeds; seed++)
        {
            var r = new SimRng(seed * 7919UL);
            var w = Make(s, Start(s, 8f + Jit(r, 0.5f), Jit(r, 1f), 2f + Jit(r, 0.4f), 180f + Jit(r, 15f), 12f, 0f), seed);
            var buf = new TickIntent[w.PlayerCount];
            int estimate = -1, at = -1;
            for (int i = 0; i < 120 * 8 && w.State != RoundState.Report && at < 0; i++)
            {
                w.Step(buf);
                if (estimate < 0 && w.Claims[0].Player != null) estimate = w.Claims[0].ArriveTick;
                if (Has(w, EventKind.Touch, 0)) at = w.Tick;
            }
            if (at < 0) continue;
            touched++;
            double q = estimate > 0 ? at / (double)estimate : 9;
            ratio += q;
            if (Math.Abs(q - 1) <= 0.15) onTime++;
        }
        Console.WriteLine($"behind: ball 4 m behind him rolling away at 2 m/s, stick at rest (time to the touch / first estimate, mean {F(ratio / Math.Max(1, touched))})");
        bool ok = Line("first touch", 100.0 * touched / seeds, 100, 100);
        ok &= Line("time within 15% of the first solve's estimate", 100.0 * onTime / seeds, 100, 100);
        return ok;
    }

    /// <summary>Still ball 8 m away, shoot tapped: the shot is kept and struck when he gets there.</summary>
    static bool StillShot(Setup s, int seeds)
    {
        int struck = 0;
        double wait = 0;
        for (ulong seed = 1; seed <= (ulong)seeds; seed++)
        {
            var r = new SimRng(seed * 7919UL);
            var w = Make(s, Start(s, 14f, Jit(r, 2f), 0f, 0f, 6f + Jit(r, 0.3f), Jit(r, 2f)), seed);
            var buf = new TickIntent[w.PlayerCount];
            for (int i = 0; i < 120 * 6 && w.State != RoundState.Report; i++)
            {
                buf[0] = new TickIntent { MoveX = 1f, ShootHeld = i >= 6 && i < 12 };
                w.Step(buf);
                if (Has(w, EventKind.Strike, 0)) { struck++; wait += w.SimTime; break; }
            }
        }
        Console.WriteLine($"still_shot: still ball 8 m away, shoot tapped (struck after {F(wait / Math.Max(1, struck))} s on average)");
        return Line("struck", 100.0 * struck / seeds, 99, 100);
    }

    /// <summary>
    /// The start of the queued-shot cases: a parry rolling across at 8 m/s, 6 m from goal. <paramref name="away"/> false: he
    /// stands by its path, so the ball comes to him; true: he starts 3 m to the side of it and runs in, so it crosses in front of him and is gone.
    /// </summary>
    static Scenario QueuedStart(Setup s, SimRng r, bool away)
    {
        float bx = s.Scenario.Pitch.Length - 6f + Jit(r, 0.4f);
        float speed = 8f + Jit(r, 0.5f), heading = 90f + Jit(r, 4f);
        return away
            ? Start(s, bx, -8.5f, speed, heading, bx - 3f + Jit(r, 0.3f), -2.5f + Jit(r, 0.6f))
            : Start(s, bx, -8.5f, speed, heading, bx - 1.6f + Jit(r, 0.3f), 1.5f + Jit(r, 0.6f));
    }

    /// <summary>The attacker's intent in the queued-shot cases: the stick at rest until the press (pushed, it would slide the meeting), then aimed at the goal with shoot held 0.15 s.</summary>
    static TickIntent QueuedIntent(World w, Scenario sc, float before, int i, ref int press)
    {
        // The press comes the given time before the ball reaches his feet (his earliest meeting at a first touch's reach; the
        // claim's own meeting can be a toe-poke from further off, a little sooner).
        var first = w.Attacker.LockOnSolve.First;
        if (press < 0 && w.Claims[0].Player != null && first.Found && first.ArriveTick - (w.Tick + 1) <= (int)MathF.Round(before * 120f)) press = i;
        if (press < 0) return default;
        var toGoal = Vector2.Normalize(new Vector2(sc.Pitch.Length - sc.Attacker.X, -sc.Attacker.Z));
        return new TickIntent { MoveX = toGoal.X, MoveZ = toGoal.Y, ShootHeld = i < press + 18 };
    }

    readonly record struct QueuedResult(int Pressed, int Struck, int FirstTime, int Flicks, int OnTick, double Speed, double Off);

    static QueuedResult Queued(Setup s, int seeds, float before, bool away)
    {
        int struck = 0, firstTime = 0, flicks = 0, onTick = 0, pressed = 0;
        double speed = 0, off = 0;
        for (ulong seed = 1; seed <= (ulong)seeds; seed++)
        {
            var sc = QueuedStart(s, new SimRng(seed * 7919UL), away);
            var w = Make(s, sc, seed);
            var buf = new TickIntent[w.PlayerCount];
            int press = -1, planned = -1;
            for (int i = 0; i < 120 * 5 && w.State != RoundState.Report; i++)
            {
                buf[0] = QueuedIntent(w, sc, before, i, ref press);
                if (press >= 0 && w.Claims[0].Player != null) planned = w.Claims[0].ArriveTick;
                w.Step(buf);
                if (!Has(w, EventKind.Strike, 0)) continue;
                struck++;
                var shot = w.Shots[^1];
                if (shot.Flick) flicks++;
                else if (shot.FirstTime) firstTime++;
                if (Math.Abs(w.Tick - planned) <= 2) onTick++;
                off += Math.Abs(w.Tick - planned);
                speed += shot.Speed;
                break;
            }
            if (press >= 0) pressed++;
        }
        return new QueuedResult(pressed, struck, firstTime, flicks, onTick, speed / Math.Max(1, struck), off / Math.Max(1, struck));
    }

    /// <summary>The ball comes to him, shoot pressed 0.8 s before it reaches him: struck first time at the meeting.</summary>
    static bool FirstTime(Setup s, int seeds)
    {
        var q = Queued(s, seeds, 0.8f, away: false);
        Console.WriteLine($"first_time: a ball rolling across at 8 m/s, 6 m from goal, comes to him; shoot pressed 0.8 s before it reaches him (pressed in {q.Pressed}, struck in {q.Struck}, mean ball speed {F(q.Speed, 1)} m/s, mean |strike - planned meeting| {F(q.Off, 1)} ticks)");
        bool ok = Line("first-time strike", 100.0 * q.FirstTime / seeds, 95, 100);
        ok &= Line("contact within 2 ticks of the planned meeting", 100.0 * q.OnTick / seeds, 95, 100);
        return ok;
    }

    /// <summary>
    /// The same ball crossing in front of him as he runs in, shoot pressed 0.3 s before it reaches him: it is not coming to him, so
    /// it is a flick, at shot.flick_power of the speed the same charge has struck first time from the same start.
    /// </summary>
    static bool FlickCase(Setup s, int seeds)
    {
        var q = Queued(s, seeds, 0.3f, away: true);
        var reference = Queued(s, seeds, 0.8f, away: true);
        Console.WriteLine($"flick: the same ball crossing in front of him as he runs in; shoot pressed 0.3 s before it reaches him (pressed in {q.Pressed}, struck in {q.Struck}, mean ball speed {F(q.Speed, 1)} m/s; struck first time from the same start {F(reference.Speed, 1)} m/s)");
        bool ok = Line("flick", 100.0 * q.Flicks / seeds, 95, 100);
        ok &= Line("ball speed against the same charge struck first time", reference.Speed > 0 ? 100.0 * q.Speed / reference.Speed : 0, 55, 65);
        return ok;
    }

    /// <summary>A skill gesture made 1.0 s before the meeting: kept, and the move starts just after his first touch.</summary>
    static bool GestureCase(Setup s, int seeds)
    {
        int started = 0, touched = 0;
        double gap = 0;
        for (ulong seed = 1; seed <= (ulong)seeds; seed++)
        {
            var r = new SimRng(seed * 7919UL);
            var sc = Start(s, 16f + Jit(r, 0.5f), Jit(r, 1f), 2.5f + Jit(r, 0.5f), 180f + Jit(r, 10f), 6f, Jit(r, 1f));
            var w = Make(s, sc, seed);
            var buf = new TickIntent[w.PlayerCount];
            int gesture = -1, touch = -1, move = -1;
            for (int i = 0; i < 120 * 6 && w.State != RoundState.Report && move < 0; i++)
            {
                bool now = gesture < 0 && w.Claims[0].Player != null && w.Claims[0].ArriveTick - (w.Tick + 1) <= 120;
                if (now) gesture = i;
                buf[0] = new TickIntent { MoveX = 1f, SkillKey = now ? SkillKey.StepOver : SkillKey.None };
                w.Step(buf);
                if (touch < 0 && Has(w, EventKind.Touch, 0)) touch = w.Tick;
                if (Has(w, EventKind.SkillMove, 0)) move = w.Tick;
                if (touch >= 0 && w.Tick - touch > 120) break;
            }
            if (touch >= 0) touched++;
            if (move >= 0 && touch >= 0 && move - touch <= 30) { started++; gap += (move - touch) / 120.0; }
        }
        Console.WriteLine($"gesture: a step-over asked for 1.0 s before the meeting (first touch in {touched}, move {F(gap / Math.Max(1, started))} s after it on average)");
        return Line("move starts within 0.25 s of his first touch", 100.0 * started / seeds, 95, 100);
    }

    /// <summary>
    /// A still ball between attacker and defender, one of them 0.2 s earlier by the solver: the earlier one plays it first (the
    /// attacker by a touch, the defender by getting it under control before the attacker has touched it).
    /// </summary>
    static bool Race(Setup s, int seeds)
    {
        bool ok = true;
        foreach (bool attackerFirst in new[] { true, false })
        {
            int won = 0, found = 0;
            for (ulong seed = 1; seed <= (ulong)seeds; seed++)
            {
                var r = new SimRng(seed * 7919UL);
                float bz = Jit(r, 2f), da = 5f + Jit(r, 1f);
                // The defender's distance is searched so that the solver's arrivals are 0.2 s (24 ticks) apart.
                float lo = 1.5f, hi = 12f, dd = 5f;
                World? w = null;
                for (int it = 0; it < 24; it++)
                {
                    dd = (lo + hi) / 2f;
                    w = Make(s, Start(s, 12f, bz, 0f, 0f, 12f - da, bz, 0f, (12f + dd, bz)), seed);
                    w.Step(new TickIntent[w.PlayerCount]);
                    if (w.Claims[0].Player == null || w.Claims[1].Player == null) break;
                    int diff = w.Claims[1].ArriveTick - w.Claims[0].ArriveTick;   // positive: the attacker is earlier
                    int want = attackerFirst ? 24 : -24;
                    if (diff == want) break;
                    if (diff > want) hi = dd; else lo = dd;
                }
                if (w == null || w.Claims[0].Player == null || w.Claims[1].Player == null
                    || Math.Abs(w.Claims[1].ArriveTick - w.Claims[0].ArriveTick - (attackerFirst ? 24 : -24)) > 2) continue;
                found++;
                var buf = new TickIntent[w.PlayerCount];
                int winner = -1;
                for (int i = 0; i < 120 * 6 && w.State is RoundState.Live or RoundState.Strike && winner < 0; i++)
                {
                    w.Step(buf);
                    if (Has(w, EventKind.Touch, 0)) winner = 0;
                    else if (w.Outcome == Outcome.Dispossessed || Has(w, EventKind.Tackle, 1) || Has(w, EventKind.Block, 1)) winner = 1;
                }
                if (winner == (attackerFirst ? 0 : 1)) won++;
            }
            Console.WriteLine($"race: a still ball, the {(attackerFirst ? "attacker" : "defender")} 0.2 s earlier by the solver ({found} of {seeds} starts found)");
            ok &= Line("the earlier player plays it first", 100.0 * won / Math.Max(1, found), 100, 100);
            ok &= found >= seeds * 0.9;
        }
        return ok;
    }

    /// <summary>The stick held toward a sample 1 s later than the earliest: he meets the ball there.</summary>
    static bool Slide(Setup s, int seeds)
    {
        int met = 0, touched = 0;
        double miss = 0;
        for (ulong seed = 1; seed <= (ulong)seeds; seed++)
        {
            var r = new SimRng(seed * 7919UL);
            var sc = Start(s, 16f + Jit(r, 0.5f), -7f, 5f + Jit(r, 0.5f), 90f + Jit(r, 5f), 10f + Jit(r, 0.5f), Jit(r, 1f));
            var w = Make(s, sc, seed);
            var buf = new TickIntent[w.PlayerCount];
            w.Step(buf);
            if (w.Claims[0].Player == null) continue;
            int later = w.Claims[0].BallTick + 120;
            if (!w.Forecast.Has(later)) continue;
            var target = w.Forecast.PositionAt(later);
            var stick = Vector2.Normalize(new Vector2(target.X - w.Attacker.Position.X, target.Z - w.Attacker.Position.Z));
            for (int i = 0; i < 120 * 6 && w.State != RoundState.Report; i++)
            {
                buf[0] = new TickIntent { MoveX = stick.X, MoveZ = stick.Y };
                var ball = w.Ball.Position;
                w.Step(buf);
                if (!Has(w, EventKind.Touch, 0)) continue;
                touched++;
                float d = new Vector2(ball.X - target.X, ball.Z - target.Z).Length();
                miss += d;
                if (d <= 0.5f) met++;
                break;
            }
        }
        Console.WriteLine($"slide: the stick held toward the sample 1 s later than the earliest (touched in {touched}, mean distance from that sample {F(miss / Math.Max(1, touched))} m)");
        return Line("meets within 0.5 m of that sample", 100.0 * met / seeds, 95, 100);
    }

    // ---- a trace ----

    /// <summary>Prints one round of a case every few ticks: the claim, the legs and the events.</summary>
    static int Trace(Setup s, string name, ulong seed)
    {
        var r = new SimRng(seed * 7919UL);
        Scenario sc;
        Func<World, int, TickIntent> script;
        int press = -1, gesture = -1;
        switch (name)
        {
            case "behind":
                sc = Start(s, 8f + Jit(r, 0.5f), Jit(r, 1f), 2f + Jit(r, 0.4f), 180f + Jit(r, 15f), 12f, 0f);
                script = (w, i) => default;
                break;
            case "still_shot":
                sc = Start(s, 14f, Jit(r, 2f), 0f, 0f, 6f + Jit(r, 0.3f), Jit(r, 2f));
                script = (w, i) => new TickIntent { MoveX = 1f, ShootHeld = i >= 6 && i < 12 };
                break;
            case "first_time":
            case "flick":
            {
                float before = name == "flick" ? 0.3f : 0.8f;
                var start = sc = QueuedStart(s, r, away: name == "flick");
                script = (w, i) => QueuedIntent(w, start, before, i, ref press);
                break;
            }
            case "gesture":
                sc = Start(s, 16f + Jit(r, 0.5f), Jit(r, 1f), 2.5f + Jit(r, 0.5f), 180f + Jit(r, 10f), 6f, Jit(r, 1f));
                script = (w, i) =>
                {
                    bool now = gesture < 0 && w.Claims[0].Player != null && w.Claims[0].ArriveTick - (w.Tick + 1) <= 120;
                    if (now) gesture = i;
                    return new TickIntent { MoveX = 1f, SkillKey = now ? SkillKey.StepOver : SkillKey.None };
                };
                break;
            case "slide":
            {
                sc = Start(s, 16f + Jit(r, 0.5f), -7f, 5f + Jit(r, 0.5f), 90f + Jit(r, 5f), 10f + Jit(r, 0.5f), Jit(r, 1f));
                Vector2? stick = null;
                script = (w, i) =>
                {
                    if (stick == null && w.Claims[0].Player != null && w.Forecast.Has(w.Claims[0].BallTick + 120))
                    {
                        var target = w.Forecast.PositionAt(w.Claims[0].BallTick + 120);
                        stick = Vector2.Normalize(new Vector2(target.X - w.Attacker.Position.X, target.Z - w.Attacker.Position.Z));
                        Console.WriteLine($"  target sample {F(target.X)},{F(target.Z)}");
                    }
                    return stick is { } v ? new TickIntent { MoveX = v.X, MoveZ = v.Y } : default;
                };
                break;
            }
            case "race":
                sc = Start(s, 12f, 0f, 0f, 0f, 7f, 0f, 0f, (12f + (seed % 2 == 0 ? 3.2f : 5.5f), 0f));
                script = (w, i) => default;
                break;
            default:
                Console.Error.WriteLine($"Unknown case '{name}'");
                return 2;
        }
        var world = Make(s, sc, seed);
        var buf = new TickIntent[world.PlayerCount];
        Console.WriteLine($"trace lock-on: case={name} seed={seed}");
        Console.WriteLine("  tick state   mode       arrive early slack  point(x,z)      att(x,z)        v   face  ball(x,z,y)          bv   opp   events");
        for (int i = 0; i < 120 * 6 && world.State != RoundState.Report; i++)
        {
            buf[0] = script(world, i);
            world.Step(buf);
            var a = world.Attacker; var b = world.Ball; var c = world.Claims[0];
            var ev = world.Events.Where(e => e.Kind is EventKind.Touch or EventKind.Strike or EventKind.Save or EventKind.Goal or EventKind.SkillMove
                or EventKind.PossessionGained or EventKind.PossessionLost or EventKind.Tackle or EventKind.Block).Select(e => $"{e.Kind}{(e.Team >= 0 ? $"[{e.Team}]" : "")}").ToList();
            if (world.Tick % 6 != 0 && ev.Count == 0) continue;
            string claim = c.Player == null ? "   -     -     -" : $"{c.ArriveTick,5} {(c.Early ? "early" : "    -")} {a.LockOnSlack,5}";
            string opp = c.Player == null || c.OpponentTick == int.MaxValue ? "    -" : c.OpponentTick.ToString().PadLeft(5);
            opp += $" first {a.LockOnSolve.First.ArriveTick} later {a.LockOnSolve.Later.ArriveTick} free {a.LockOnSolve.FreeHint} min {a.MinArrivalTick}";
            if (world.Defender is { } df) opp += $" def {F(df.Position.X).PadLeft(6)},{F(df.Position.Z).PadLeft(6)} v{F(df.Speed)} {df.DefenderAction} arr {(world.Claims[1].Player == null ? "-" : world.Claims[1].ArriveTick.ToString())}";
            Console.WriteLine($"{world.Tick,6} {world.State,-7} {a.Regain,-9} {claim}  {F(c.Point.X).PadLeft(6)},{F(c.Point.Z).PadLeft(6)}  {F(a.Position.X).PadLeft(6)},{F(a.Position.Z).PadLeft(6)} {F(a.Speed).PadLeft(5)} {F(a.Facing * 180 / MathF.PI, 0).PadLeft(5)}  {F(b.Position.X).PadLeft(6)},{F(b.Position.Z).PadLeft(6)},{F(b.Position.Y).PadLeft(5)} {F(b.Velocity.Length()).PadLeft(5)} {opp}  {string.Join(" ", ev)}");
            if (world.State == RoundState.Flight && ev.Count == 0 && world.Tick % 24 == 0) break;
        }
        foreach (var shot in world.Shots) Console.WriteLine($"shot: tick {shot.Tick} speed {F(shot.Speed, 1)} first_time {shot.FirstTime} flick {shot.Flick} charge {F(shot.Charge)} sigma_h {F(shot.SigmaH)}");
        return 0;
    }
}
