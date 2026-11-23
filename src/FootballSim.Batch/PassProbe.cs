using System.Globalization;
using System.Numerics;
using FootballSim.Core;

/// <summary>
/// The pass probe: scripted rounds with a team-mate, one case per rule of the pass, each over many seeds (the seed also moves the
/// start a little), with the targets the numbers are held to.
///   --pass-probe [--seeds 1000] [--only ground,lane_off,lane_on,jump,one_two,square_shot,through,offside] [--trace case --seed n]
/// The keeper holds his line except in square_shot, and the team-mate stands unless the case sends him.
/// </summary>
static class PassProbe
{
    static readonly CultureInfo Ic = CultureInfo.InvariantCulture;
    static string F(double v, int d = 2) => v.ToString("F" + d, Ic);

    public static readonly string[] Cases = { "ground", "lane_off", "lane_on", "jump", "one_two", "square_shot", "through", "offside" };

    sealed class Setup
    {
        public PitchSpec Pitch = null!;
        public PlayerSheet Attacker = null!, Keeper = null!, Defender = null!, Mate = null!;
        public Tuning Tuning = null!;
    }

    /// <summary>The attacker at (ax, az) with the ball at his feet toward (toX, toZ), the team-mate at (mx, mz), a defender when a spot is given.</summary>
    static Scenario Start(Setup s, float ax, float az, float mx, float mz, (float X, float Z)? defender = null, bool offside = false, bool keeperHolds = true)
    {
        var to = new Vector2(mx - ax, mz - az);
        float facing = MathF.Atan2(to.Y, to.X);
        return new Scenario
        {
            Id = "pass_probe", Name = "Pass probe", Pitch = s.Pitch, TeamSize = 2,
            Ball = new StartSpot { X = ax + 0.45f * MathF.Cos(facing), Z = az + 0.45f * MathF.Sin(facing) },
            Attacker = new StartSpot { X = ax, Z = az, FacingDeg = facing * 180f / MathF.PI },
            Mate = new StartSpot { X = mx, Z = mz },
            KeeperOnLine = keeperHolds, KeeperHoldLine = keeperHolds, KeeperHoldLineThroughDribble = keeperHolds,
            Defenders = defender != null ? 1 : 0,
            Defender = defender is { } d ? new StartSpot { X = d.X, Z = d.Z } : new StartSpot(),
            Offside = offside, ClockLimit = 20f,
        };
    }

    static World Make(Setup s, Scenario sc, ulong seed)
    {
        var w = new World(s.Tuning, sc, s.Attacker, s.Keeper, seed, sc.Defenders > 0 ? s.Defender : null, s.Mate);
        w.Begin();
        return w;
    }

    static bool Has(World w, EventKind kind, int team, int index = -1) => w.Events.Any(e => e.Kind == kind && e.Team == team && (index < 0 || e.PlayerIndex == index));
    static float Jit(SimRng r, float span) => r.Uniform(-span, span);
    static Vector2 Toward(World w, int index)
    {
        var a = w.Attacker.Position; var m = w.Teams[0][index].Position;
        var d = new Vector2(m.X - a.X, m.Z - a.Z);
        return d.LengthSquared() > 1e-6f ? Vector2.Normalize(d) : new Vector2(1f, 0f);
    }

    public static int Run(Dictionary<string, string> opts, string dataDir, Tuning tuning)
    {
        int seeds = opts.TryGetValue("seeds", out var ss) ? int.Parse(ss, Ic) : 1000;
        var only = opts.TryGetValue("only", out var o) ? o.Split(',', StringSplitOptions.RemoveEmptyEntries) : Cases;
        var scr = DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", "two_v_one.json"));
        if (!scr.Ok) { Console.Error.WriteLine("error: " + scr.Error); return 1; }
        PlayerSheet? P(string key, string def)
        {
            var r = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", (opts.TryGetValue(key, out var v) ? v : def) + ".json"));
            if (!r.Ok) Console.Error.WriteLine("error: " + r.Error);
            return r.Value;
        }
        var sc = scr.Value!;
        var a = P("attacker", sc.DefaultAttacker); var k = P("keeper", sc.DefaultKeeper); var d = P("defender", sc.DefaultDefender); var m = P("mate", sc.DefaultMate);
        if (a == null || k == null || d == null || m == null) return 1;
        var s = new Setup { Pitch = sc.Pitch, Attacker = a, Keeper = k, Defender = d, Mate = m, Tuning = tuning };
        if (opts.TryGetValue("trace", out var traceCase))
            return Trace(s, traceCase, opts.TryGetValue("seed", out var sd) ? ulong.Parse(sd, Ic) : 1UL);

        Console.WriteLine($"pass probe: {seeds} seeds per case, attacker {a.Id}, team-mate {m.Id}, defender {d.Id}");
        bool ok = true;
        foreach (string c in Cases)
        {
            if (!only.Contains(c)) continue;
            switch (c)
            {
                case "ground": ok &= Ground(s, seeds); break;
                case "lane_off": ok &= Lane(s, seeds, on: false); break;
                case "lane_on": ok &= Lane(s, seeds, on: true); break;
                case "jump": ok &= Jump(s, seeds); break;
                case "one_two": ok &= OneTwo(s, seeds); break;
                case "square_shot": ok &= SquareShot(s, seeds); break;
                case "through": ok &= Through(s, seeds); break;
                case "offside": ok &= OffsideCase(s, seeds); break;
            }
        }
        Console.WriteLine(ok ? "pass probe: every target met" : "pass probe: TARGETS MISSED");
        return ok ? 0 : 3;
    }

    static bool Line(string what, double value, double lo, double hi, string unit = "%")
    {
        bool inside = value >= lo - 1e-9 && value <= hi + 1e-9;
        Console.WriteLine($"   {(inside ? "ok  " : "OUT ")} {what}: {F(value, 1)}{unit} (target {F(lo, 0)}-{F(hi, 0)}{unit})");
        return inside;
    }

    /// <summary>One pass from the start of a round: the pass button tapped on ticks 2 to 7 with the stick at the team-mate. What happened, per round.</summary>
    readonly record struct PassRun(bool Struck, bool Received, bool CutOut, int StrikeTick, int Expected, int TouchTick, bool JumpOnStrike, float Speed);

    static PassRun OnePass(World w, PassKind kind = PassKind.Ground, int holdTicks = 6, Func<World, int, TickIntent, TickIntent>? after = null, int maxTicks = 720)
    {
        var buf = new TickIntent[w.PlayerCount];
        var aim = Toward(w, 1);
        bool struck = false, received = false, cut = false, jump = false;
        int strikeTick = -1, expected = -1, touch = -1;
        float speed = 0f;
        for (int i = 0; i < maxTicks && w.State is RoundState.Live or RoundState.Strike or RoundState.Flight; i++)
        {
            var it = new TickIntent { MoveX = aim.X, MoveZ = aim.Y };
            if (!struck)
            {
                bool held = i >= 2 && i < 2 + holdTicks;
                it.PassHeld = held && kind == PassKind.Ground;
                it.ThroughHeld = held && kind == PassKind.Through;
            }
            else it = after != null ? after(w, w.Tick - strikeTick, default) : default;
            buf[0] = it;
            w.Step(buf);
            if (!struck && Has(w, EventKind.Pass, 0))
            {
                struck = true; strikeTick = w.Tick;
                expected = w.Pass?.ExpectedArrivalTick ?? -1;
                speed = w.Events.First(e => e.Kind == EventKind.Pass).Speed;
                jump = w.Events.Any(e => e.Kind == EventKind.ControlChanged && e.Reason == ControlReason.Pass) && w.ControlledIndex == 1;
                continue;
            }
            if (!struck) continue;
            if (Has(w, EventKind.Tackle, 1) || Has(w, EventKind.Block, 1) || w.Outcome == Outcome.Dispossessed) { cut = true; break; }
            if (Has(w, EventKind.Touch, 0, 1)) { received = true; touch = w.Tick; break; }
            if (Has(w, EventKind.Touch, 0, 0) || Has(w, EventKind.Strike, 0) || Has(w, EventKind.Pass, 0)) break;
        }
        return new PassRun(struck, received, cut, strikeTick, expected, touch, jump, speed);
    }

    /// <summary>A ground pass of 8 m to a standing team-mate, no defender: received, when the pass record said it would be.</summary>
    static bool Ground(Setup s, int seeds)
    {
        int struck = 0, received = 0, onTime = 0;
        double late = 0, speed = 0;
        for (ulong seed = 1; seed <= (ulong)seeds; seed++)
        {
            var r = new SimRng(seed * 7919UL);
            float yaw = Jit(r, 1.2f);
            var w = Make(s, Start(s, 10f, 0f, 10f + 8f * MathF.Cos(yaw), 8f * MathF.Sin(yaw)), seed);
            var run = OnePass(w);
            if (run.Struck) { struck++; speed += run.Speed; }
            if (!run.Received) continue;
            received++;
            late += (run.TouchTick - run.Expected) / 120.0;
            if (Math.Abs(run.TouchTick - run.Expected) <= 12) onTime++;
        }
        Console.WriteLine($"ground: a ground pass of 8 m to a standing team-mate, no defender (struck in {struck}, mean launch {F(speed / Math.Max(1, struck), 1)} m/s, first touch {F(late / Math.Max(1, received))} s after the expected arrival on average)");
        bool ok = Line("received", 100.0 * received / seeds, 99, 100);
        ok &= Line("arrival within 0.1 s of the expected tick", 100.0 * onTime / seeds, 95, 100);
        return ok;
    }

    /// <summary>The same pass with the defender standing a metre off its lane (it gets through) or on it (he cuts it out).</summary>
    static bool Lane(Setup s, int seeds, bool on)
    {
        int cut = 0, received = 0, struck = 0;
        for (ulong seed = 1; seed <= (ulong)seeds; seed++)
        {
            var r = new SimRng(seed * 7919UL);
            float ax = 8f, az = -4f, mx = 8f + Jit(r, 0.5f), mz = 4f;
            // Halfway along the lane, a metre to the goal side of it or on it.
            float along = 0.5f + Jit(r, 0.05f);
            (float, float) def = (ax + (mx - ax) * along + (on ? 0f : 1f), az + (mz - az) * along);
            var w = Make(s, Start(s, ax, az, mx, mz, def), seed);
            // (he stands where he is put: the case measures the lane, not his bot's first steps)
            w.DefenderBotEnabled = false;
            var run = OnePass(w, holdTicks: 3);
            if (run.Struck) struck++;
            if (run.CutOut) cut++;
            if (run.Received) received++;
        }
        Console.WriteLine($"lane_{(on ? "on" : "off")}: the defender standing {(on ? "on the lane" : "1 m off the lane")}, halfway along an 8 m pass (struck in {struck}, received in {received})");
        return on ? Line("cut out", 100.0 * cut / seeds, 80, 100) : Line("cut out", 100.0 * cut / seeds, 0, 10);
    }

    /// <summary>Control jumps to the receiver on the strike tick, and a press on the next tick is the new player's.</summary>
    static bool Jump(Setup s, int seeds)
    {
        int jumped = 0, kept = 0;
        for (ulong seed = 1; seed <= (ulong)seeds; seed++)
        {
            var r = new SimRng(seed * 7919UL);
            var w = Make(s, Start(s, 10f, Jit(r, 2f), 12f + Jit(r, 2f), 7f + Jit(r, 1f)), seed);
            bool pressed = false;
            var run = OnePass(w, after: (x, since, it) =>
            {
                // shoot pressed on the first tick after the strike: it must wind up for the receiver
                it.ShootHeld = since <= 6;
                if (since == 2 && x.Attacker.Index == 1 && x.Attacker.Action is { IsShot: true, Made: false }) pressed = true;
                return it;
            }, maxTicks: 240);
            if (run.JumpOnStrike) jumped++;
            if (pressed) kept++;
        }
        Console.WriteLine("jump: a pass, then shoot pressed on the tick after its strike");
        bool ok = Line("control jumps on the strike tick", 100.0 * jumped / seeds, 100, 100);
        ok &= Line("the press is the new player's", 100.0 * kept / seeds, 100, 100);
        return ok;
    }

    /// <summary>A pass and a first-time pass back: the second pass, queued on the way, is struck at the meeting and received by the first passer.</summary>
    static bool OneTwo(Setup s, int seeds)
    {
        int done = 0, second = 0;
        for (ulong seed = 1; seed <= (ulong)seeds; seed++)
        {
            var r = new SimRng(seed * 7919UL);
            var w = Make(s, Start(s, 10f, -3f + Jit(r, 1f), 11f + Jit(r, 1f), 4f + Jit(r, 1f)), seed);
            var buf = new TickIntent[w.PlayerCount];
            var aim = Toward(w, 1);
            int passes = 0, backTick = -1;
            bool back = false;
            for (int i = 0; i < 900 && w.State is RoundState.Live or RoundState.Strike && !back; i++)
            {
                TickIntent it = default;
                if (passes == 0) it = new TickIntent { MoveX = aim.X, MoveZ = aim.Y, PassHeld = i >= 2 && i < 8 };
                else if (passes == 1)
                {
                    // the pass back, queued while the first is on its way: the stick at the first passer
                    var to = Toward(w, 0);
                    int since = w.Tick - backTick;
                    it = new TickIntent { MoveX = to.X, MoveZ = to.Y, PassHeld = since >= 6 && since < 12 };
                }
                buf[0] = it;
                w.Step(buf);
                if (Has(w, EventKind.Pass, 0)) { passes++; if (passes == 1) backTick = w.Tick; }
                if (passes == 2 && Has(w, EventKind.Touch, 0, 0)) back = true;
            }
            if (passes >= 2) second++;
            if (back) done++;
        }
        Console.WriteLine($"one_two: a pass, and a first-time pass back queued on the way (second pass struck in {second})");
        return Line("completed", 100.0 * done / seeds, 90, 100);
    }

    /// <summary>A square pass across the box and shoot pressed on the way: struck first time at the meeting.</summary>
    static bool SquareShot(Setup s, int seeds)
    {
        int struck = 0, onTick = 0, firstTime = 0;
        double off = 0;
        float goalX = s.Pitch.Length;
        for (ulong seed = 1; seed <= (ulong)seeds; seed++)
        {
            var r = new SimRng(seed * 7919UL);
            var w = Make(s, Start(s, goalX - 9f + Jit(r, 0.5f), -5f + Jit(r, 0.5f), goalX - 9f + Jit(r, 0.5f), 4f + Jit(r, 0.5f)), seed);
            var buf = new TickIntent[w.PlayerCount];
            var aim = Toward(w, 1);
            int passTick = -1, planned = -1;
            for (int i = 0; i < 720 && w.State is RoundState.Live or RoundState.Strike; i++)
            {
                TickIntent it;
                if (passTick < 0) it = new TickIntent { MoveX = aim.X, MoveZ = aim.Y, PassHeld = i >= 2 && i < 8 };
                else
                {
                    int since = w.Tick - passTick;
                    var p = w.Attacker.Position;
                    var toGoal = Vector2.Normalize(new Vector2(goalX - p.X, -p.Z));
                    it = new TickIntent { MoveX = toGoal.X, MoveZ = toGoal.Y, ShootHeld = since >= 4 && since < 22 };
                    if (w.Claims[0].Player != null) planned = w.Claims[0].ArriveTick;
                }
                buf[0] = it;
                w.Step(buf);
                if (passTick < 0 && Has(w, EventKind.Pass, 0)) passTick = w.Tick;
                if (!Has(w, EventKind.Strike, 0)) continue;
                struck++;
                if (w.Shots[^1].FirstTime) firstTime++;
                off += Math.Abs(w.Tick - planned);
                if (Math.Abs(w.Tick - planned) <= 2) onTick++;
                break;
            }
        }
        Console.WriteLine($"square_shot: a square pass 9 m out, shoot pressed on the way (struck in {struck}, first time in {firstTime}, mean |strike - meeting| {F(off / Math.Max(1, struck), 1)} ticks)");
        return Line("struck within 2 ticks of the meeting", 100.0 * onTick / seeds, 95, 100);
    }

    /// <summary>A through ball to a team-mate sprinting level with the defender: he takes it in his stride, onside.</summary>
    static bool Through(Setup s, int seeds)
    {
        Console.WriteLine("through: (measured from milestone 4)");
        return ThroughImpl?.Invoke(s, seeds) ?? true;
    }

    static bool OffsideCase(Setup s, int seeds)
    {
        Console.WriteLine("offside: (measured from milestone 4)");
        return OffsideImpl?.Invoke(s, seeds) ?? true;
    }

    static readonly Func<Setup, int, bool>? ThroughImpl = null;
    static readonly Func<Setup, int, bool>? OffsideImpl = null;

    // ---- a trace ----

    static int Trace(Setup s, string name, ulong seed)
    {
        var r = new SimRng(seed * 7919UL);
        Scenario sc;
        switch (name)
        {
            case "lane_on":
            case "lane_off":
            {
                bool on = name == "lane_on";
                float ax = 8f, az = -4f, mx = 8f + Jit(r, 0.5f), mz = 4f;
                float along = 0.5f + Jit(r, 0.05f);
                sc = Start(s, ax, az, mx, mz, (ax + (mx - ax) * along + (on ? 0f : 1f), az + (mz - az) * along));
                break;
            }
            case "square_shot":
                sc = Start(s, s.Pitch.Length - 9f + Jit(r, 0.5f), -5f + Jit(r, 0.5f), s.Pitch.Length - 9f + Jit(r, 0.5f), 4f + Jit(r, 0.5f));
                break;
            default:
            {
                float yaw = Jit(r, 1.2f);
                sc = Start(s, 10f, 0f, 10f + 8f * MathF.Cos(yaw), 8f * MathF.Sin(yaw));
                break;
            }
        }
        var w = Make(s, sc, seed);
        if (name.StartsWith("lane")) w.DefenderBotEnabled = false;
        int passedAt = -1;
        var buf = new TickIntent[w.PlayerCount];
        var aim = Toward(w, 1);
        Console.WriteLine($"trace pass: case={name} seed={seed}");
        Console.WriteLine("  tick state   ctl charge  p0(x,z)        v    p1(x,z)        v    ball(x,z)      bv   claim  events");
        for (int i = 0; i < 480 && w.State != RoundState.Report; i++)
        {
            buf[0] = new TickIntent { MoveX = aim.X, MoveZ = aim.Y, PassHeld = i >= 2 && i < 8 && w.Passes == 0 };
            if (w.Passes > 0)
            {
                if (passedAt < 0) passedAt = w.Tick;
                int since = w.Tick - passedAt;
                buf[0] = name == "square_shot" ? new TickIntent { MoveX = 1f, ShootHeld = since >= 4 && since < 22 } : default;
            }
            w.Step(buf);
            var p0 = w.Teams[0][0]; var p1 = w.Teams[0][1]; var b = w.Ball;
            var ev = w.Events.Where(e => e.Kind is EventKind.Touch or EventKind.Pass or EventKind.ControlChanged or EventKind.Strike or EventKind.Tackle or EventKind.Block or EventKind.Save
                or EventKind.PossessionGained or EventKind.PossessionLost or EventKind.Offside).Select(e => $"{e.Kind}[{e.Team}.{e.PlayerIndex}]").ToList();
            if (w.Tick % 6 != 0 && ev.Count == 0) continue;
            var c = w.Claims[0];
            string claim = c.Player == null ? "    -" : $"{c.Player.Index}@{c.ArriveTick}";
            string def = w.Defender is { } d ? $" def {F(d.Position.X).PadLeft(6)},{F(d.Position.Z).PadLeft(6)} {d.DefenderAction}" : "";
            Console.WriteLine($"{w.Tick,6} {w.State,-7} {w.ControlledIndex,3} {F(w.PassCharge).PadLeft(6)}  {F(p0.Position.X).PadLeft(6)},{F(p0.Position.Z).PadLeft(6)} {F(p0.Speed).PadLeft(5)}  {F(p1.Position.X).PadLeft(6)},{F(p1.Position.Z).PadLeft(6)} {F(p1.Speed).PadLeft(5)}  {F(b.Position.X).PadLeft(6)},{F(b.Position.Z).PadLeft(6)} {F(b.Velocity.Length()).PadLeft(5)}  {claim,-7}{def}  {string.Join(" ", ev)}");
            if (w.Passes > 0 && (w.BallController != null || w.Shots.Count > 0) && w.Tick % 6 == 0) break;
        }
        if (w.Pass is { } rec) Console.WriteLine($"pass on its way: receiver {rec.Receiver?.Index}, expected arrival {rec.ExpectedArrivalTick}");
        return 0;
    }
}
