using System.Globalization;
using System.Numerics;
using FootballSim.Core;

/// <summary>
/// The feel probe: numbers for "smooth" dribbling, through the real world on a long empty pitch (no keeper involved).
/// <list type="bullet">
/// <item>Straight jog and sprint dribbles (measured from 3 s to the end): the player's ground speed (mean, coefficient of
/// variation), the surge per touch (largest minus smallest speed between two touches; of the whole velocity and of the running
/// speed alone, so a surge from the steering shows as the difference), the mean steering correction, the per-tick acceleration
/// (p99 and max of |dv|/dt), the touch interval (mean, standard deviation), the ball's distance from the feet (p10/p50/p90) and
/// its sideways spread, and the facing's wobble (standard deviation of its turn rate).</item>
/// <item>Heading response: from 3 s of a straight jog or sprint, the stick turned by 45, 90 or 180 degrees at once; the time
/// for the velocity's direction to cover 90% of the turn, and the lowest speed on the way as a share of the speed before, with
/// the ball and without it (the ball far away).</item>
/// <item>With <c>--replays &lt;dir|file&gt;</c>: the same per-tick acceleration and surge numbers over recorded rounds (real
/// human input), while the attacker is in possession.</item>
/// </list>
/// </summary>
static class FeelProbe
{
    static TickIntent Dir(float deg, float sprint = 0f, bool close = false, float mag = 1f)
    {
        float a = deg * MathF.PI / 180f;
        return new TickIntent { MoveX = MathF.Cos(a) * mag, MoveZ = MathF.Sin(a) * mag, Sprint = sprint, CloseControl = close };
    }

    static string F(double v, int d = 2) => v.ToString("F" + d, CultureInfo.InvariantCulture);

    static float Pct(List<float> l, double q)
    {
        if (l.Count == 0) return float.NaN;
        var s = new List<float>(l); s.Sort();
        return s[Math.Min(s.Count - 1, (int)(q * s.Count))];
    }

    static double Mean(List<float> l) => l.Count == 0 ? double.NaN : l.Average(x => (double)x);
    static double Std(List<float> l)
    {
        if (l.Count < 2) return double.NaN;
        double m = Mean(l);
        return Math.Sqrt(l.Sum(x => (x - m) * (x - m)) / (l.Count - 1));
    }

    static World NewWorld(Tuning t, PlayerSheet sheet, PlayerSheet keeper, ulong seed, bool withBall)
    {
        var sc = new Scenario
        {
            Id = "probe",
            Pitch = new PitchSpec { Length = 400f, Width = 200f },
            Ball = new StartSpot { X = withBall ? 150.5f : 390f, Z = withBall ? 0f : 90f },
            Attacker = new StartSpot { X = 150f, Z = 0f },
        };
        var w = new World(t, sc, sheet, keeper, seed);
        w.Begin();
        return w;
    }

    sealed class Straight
    {
        public readonly List<float> Speed = new(), Surge = new(), RunSurge = new(), Steer = new(), Acc = new(), Interval = new(),
            Dist = new(), Lateral = new(), FacingRate = new(), SteerLong = new(), SteerLat = new();
        public int Touches;
        public readonly SpeedSteps Steps = new();
    }

    static void RunStraight(Tuning t, PlayerSheet sheet, PlayerSheet keeper, ulong seed, TickIntent stick, float seconds, Straight st)
    {
        var w = NewWorld(t, sheet, keeper, seed, true);
        var buf = new TickIntent[w.PlayerCount];
        var p = w.Attacker;
        int ticks = (int)(seconds * t.TickRate), from = 3 * t.TickRate;
        Vector3 v0 = p.Velocity; float f0 = p.Facing;
        int lastTouch = -1;
        float wMin = float.MaxValue, wMax = 0f, rMin = float.MaxValue, rMax = 0f;
        for (int i = 1; i <= ticks && w.State == RoundState.Live; i++)
        {
            buf[0] = stick;
            st.Steps.Before(p);
            w.Step(buf);
            st.Steps.After(p, t.Dt, i > t.TickRate);
            bool touched = w.Events.Any(e => e.Team == 0 && e.PlayerIndex == 0 && e.Kind == EventKind.Touch);
            float sp = new Vector2(p.Velocity.X, p.Velocity.Z).Length();
            if (i > from)
            {
                st.Speed.Add(sp);
                st.Steer.Add(new Vector2(p.SteerVelocity.X, p.SteerVelocity.Z).Length());
                st.SteerLong.Add(MathF.Abs(p.SteerVelocity.X * MathF.Cos(p.Facing) + p.SteerVelocity.Z * MathF.Sin(p.Facing)));
                st.SteerLat.Add(MathF.Abs(-p.SteerVelocity.X * MathF.Sin(p.Facing) + p.SteerVelocity.Z * MathF.Cos(p.Facing)));
                st.Acc.Add(new Vector2(p.Velocity.X - v0.X, p.Velocity.Z - v0.Z).Length() / t.Dt);
                st.FacingRate.Add(MathF.Abs(Movement.WrapAngle(p.Facing - f0)) / t.Dt * 180f / MathF.PI);
                var rel = new Vector2(w.Ball.Position.X - p.Position.X, w.Ball.Position.Z - p.Position.Z);
                st.Dist.Add(rel.Length());
                st.Lateral.Add(TouchModel.Lateral(p.Position, p.Facing, w.Ball.Position));
                wMin = MathF.Min(wMin, sp); wMax = MathF.Max(wMax, sp);
                rMin = MathF.Min(rMin, p.RunSpeed); rMax = MathF.Max(rMax, p.RunSpeed);
                if (touched)
                {
                    st.Touches++;
                    if (lastTouch > from) { st.Interval.Add((i - lastTouch) * t.Dt); st.Surge.Add(wMax - wMin); st.RunSurge.Add(rMax - rMin); }
                    wMin = rMin = float.MaxValue; wMax = rMax = 0f;
                }
            }
            if (touched) lastTouch = i;
            v0 = p.Velocity; f0 = p.Facing;
        }
    }

    sealed class Turn
    {
        public readonly List<float> T90 = new(), FacingT90 = new(), Dip = new();
        public int Kept, Runs;
        public readonly SpeedSteps Steps = new();
    }

    static void RunTurn(Tuning t, PlayerSheet sheet, PlayerSheet keeper, ulong seed, bool sprint, float deg, bool withBall, Turn st)
    {
        var w = NewWorld(t, sheet, keeper, seed, withBall);
        var buf = new TickIntent[w.PlayerCount];
        var p = w.Attacker;
        // The turn comes 3.0 to 3.7 s in by the seed, so the runs see it at every point of the touch cadence.
        int pre = 3 * t.TickRate + (int)(seed % 20) * (int)(0.7f * t.TickRate) / 20, post = 3 * t.TickRate;
        float sp = sprint ? 1f : 0f;
        float before = 0f, minSpeed = float.MaxValue;
        float target = deg * MathF.PI / 180f;
        float t90 = float.NaN, ft90 = float.NaN;
        bool lost = false;
        for (int i = 1; i <= pre + post && w.State == RoundState.Live; i++)
        {
            buf[0] = i <= pre ? Dir(0f, sp) : Dir(deg, sp);
            st.Steps.Before(p);
            w.Step(buf);
            st.Steps.After(p, t.Dt, i > t.TickRate);
            if (withBall && i > t.TickRate && !p.InPossession) lost = true;
            float speed = new Vector2(p.Velocity.X, p.Velocity.Z).Length();
            if (i == pre) before = speed;
            if (i <= pre) continue;
            minSpeed = MathF.Min(minSpeed, speed);
            float s = (i - pre) * t.Dt;
            // the turn still to go, by the velocity's direction and by the facing (radians, 0 when done)
            float vh = speed > 0.05f ? MathF.Atan2(p.Velocity.Z, p.Velocity.X) : float.NaN;
            if (float.IsNaN(t90) && !float.IsNaN(vh) && MathF.Abs(Movement.WrapAngle(target - vh)) <= 0.1f * target) t90 = s;
            if (float.IsNaN(ft90) && MathF.Abs(Movement.WrapAngle(target - p.Facing)) <= 0.1f * target) ft90 = s;
        }
        st.Runs++;
        if (!lost) st.Kept++;
        if (!float.IsNaN(t90)) st.T90.Add(t90);
        if (!float.IsNaN(ft90)) st.FacingT90.Add(ft90);
        if (before > 0f) st.Dip.Add(minSpeed / before);
    }

    public static int Run(Dictionary<string, string> opts, string dataDir, Tuning tuning)
    {
        int seeds = opts.TryGetValue("seeds", out var ns) ? int.Parse(ns, CultureInfo.InvariantCulture) : 20;
        string attackerId = opts.TryGetValue("attacker", out var a) ? a : "striker_a";
        var sheet = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", attackerId + ".json")).Value;
        var keeper = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", "the_wall.json")).Value;
        if (sheet == null || keeper == null) { Console.Error.WriteLine("players not found"); return 1; }
        if (opts.TryGetValue("rtrace", out var rtf)) return ReplayTrace.Run(rtf, dataDir, tuning, opts);
        if (opts.TryGetValue("replays", out var rp)) return RunReplays(rp, dataDir, tuning);
        if (opts.TryGetValue("trace", out var tr)) return Trace(tr, tuning, sheet, keeper, opts);

        Console.WriteLine($"feel probe: attacker={attackerId} seeds={seeds}");
        Console.WriteLine("straight dribbles, 3-10 s: speed m/s, surge = max-min speed between touches (all / running speed only), acc = |dv|/dt m/s^2 per tick, interval s, ball distance from the feet m, lateral = sideways spread of the ball sd m, facing = |turn rate| of the facing, deg/s (p99, max); steps = ticks from 1 s on with the ball whose |d|v||/dt is over 15 m/s^2 (skill-move ticks included; skill = those among them), over all seeds");
        Console.WriteLine("run           speed  cv     surge  surge_pct  run_surge  steer  st_long  st_lat  acc_p99  acc_max  interval  int_sd  int_cv  dist_p10  dist_p50  dist_p90  lateral_sd  facing_p99  facing_max  touches_s  steps  skill");
        foreach (var (name, stick) in new[] { ("jog", Dir(0f)), ("sprint", Dir(0f, 1f)), ("close", Dir(0f, 0f, true)), ("walk_0.5", Dir(0f, 0f, false, 0.5f)) })
        {
            var st = new Straight();
            for (int i = 0; i < seeds; i++) RunStraight(tuning, sheet, keeper, 1UL + (ulong)i, stick, 10f, st);
            double sm = Mean(st.Speed);
            Console.WriteLine(string.Join("  ", new[]
            {
                name.PadRight(12), F(sm), F(Std(st.Speed) / sm, 3), F(Mean(st.Surge)).PadLeft(5), F(100 * Mean(st.Surge) / sm, 1).PadLeft(9),
                F(Mean(st.RunSurge)).PadLeft(9), F(Mean(st.Steer)).PadLeft(5), F(Mean(st.SteerLong)).PadLeft(7), F(Mean(st.SteerLat)).PadLeft(6),
                F(Pct(st.Acc, 0.99), 1).PadLeft(7), F(st.Acc.Max(), 1).PadLeft(7),
                F(Mean(st.Interval), 3).PadLeft(8), F(Std(st.Interval), 3).PadLeft(6), F(Std(st.Interval) / Mean(st.Interval), 3).PadLeft(6),
                F(Pct(st.Dist, 0.1)).PadLeft(8), F(Pct(st.Dist, 0.5)).PadLeft(8), F(Pct(st.Dist, 0.9)).PadLeft(8), F(Std(st.Lateral), 3).PadLeft(10),
                F(Pct(st.FacingRate, 0.99), 1).PadLeft(10), F(st.FacingRate.Max(), 1).PadLeft(10), F(st.Touches / (7.0 * seeds)).PadLeft(9),
                st.Steps.Steps.ToString().PadLeft(5), st.Steps.SkillSteps.ToString().PadLeft(5),
            }));
        }
        Console.WriteLine();
        Console.WriteLine("heading response: stick turned at once after 3.0-3.7 s straight (by seed); t90 = s until the velocity's direction is within 10% of the turn (median, p90), facing_t90 the same for the facing, dip = lowest speed / speed before (median); steps / skill as above, with the ball");
        Console.WriteLine("ball     pace    turn  t90_p50  t90_p90  facing_t90  dip_p50  kept_pct  steps  skill");
        foreach (bool withBall in new[] { true, false })
        foreach (bool sprint in new[] { false, true })
        foreach (float deg in new[] { 45f, 90f, 180f })
        {
            var st = new Turn();
            for (int i = 0; i < seeds; i++) RunTurn(tuning, sheet, keeper, 1UL + (ulong)i, sprint, deg, withBall, st);
            Console.WriteLine(string.Join("  ", new[]
            {
                (withBall ? "with" : "without").PadRight(7), (sprint ? "sprint" : "jog").PadRight(6), F(deg, 0).PadLeft(4),
                F(Pct(st.T90, 0.5)).PadLeft(7), F(Pct(st.T90, 0.9)).PadLeft(7), F(Pct(st.FacingT90, 0.5)).PadLeft(10), F(Pct(st.Dip, 0.5)).PadLeft(7),
                (withBall ? F(100.0 * st.Kept / st.Runs, 0) : "-").PadLeft(8),
                (withBall ? st.Steps.Steps.ToString() : "-").PadLeft(5), (withBall ? st.Steps.SkillSteps.ToString() : "-").PadLeft(5),
            }));
        }
        return 0;
    }

    /// <summary>
    /// Prints one turn tick by tick: <c>--trace jog90</c>, <c>sprint45</c>, ... (add <c>off</c> for no ball, e.g. <c>sprint90off</c>);
    /// <c>--from</c> / <c>--to</c> seconds after the turn (default -0.3 to 1.5), <c>--at</c> seconds for the turn (default 3).
    /// </summary>
    static int Trace(string spec, Tuning t, PlayerSheet sheet, PlayerSheet keeper, Dictionary<string, string> opts)
    {
        bool sprint = spec.StartsWith("sprint");
        bool off = spec.EndsWith("off");
        float deg = float.Parse(new string(spec.Where(char.IsDigit).ToArray()), CultureInfo.InvariantCulture);
        float from = opts.TryGetValue("from", out var fs) ? float.Parse(fs, CultureInfo.InvariantCulture) : -0.3f;
        float to = opts.TryGetValue("to", out var ts) ? float.Parse(ts, CultureInfo.InvariantCulture) : 1.5f;
        ulong seed = opts.TryGetValue("seed", out var ss) ? ulong.Parse(ss, CultureInfo.InvariantCulture) : 1UL;
        var w = NewWorld(t, sheet, keeper, seed, !off);
        var buf = new TickIntent[w.PlayerCount];
        var p = w.Attacker;
        int pre = opts.TryGetValue("at", out var at) ? (int)MathF.Round(float.Parse(at, CultureInfo.InvariantCulture) * t.TickRate) : 3 * t.TickRate;
        Console.WriteLine("t_s     facing  vel_dir  speed  run    steer  ball_ahead  ball_lat  ball_v  action        phase      legs      eta    touch");
        for (int i = 1; i <= pre + (int)(to * t.TickRate) && w.State == RoundState.Live; i++)
        {
            buf[0] = i <= pre ? Dir(0f, sprint ? 1f : 0f) : Dir(deg, sprint ? 1f : 0f);
            w.Step(buf);
            float s = (i - pre) * t.Dt;
            if (s < from) continue;
            var rel = new Vector2(w.Ball.Position.X - p.Position.X, w.Ball.Position.Z - p.Position.Z);
            var fwd = new Vector2(MathF.Cos(p.Facing), MathF.Sin(p.Facing));
            var a = p.Action;
            bool touched = w.Events.Any(e => e.Team == 0 && e.PlayerIndex == 0 && e.Kind == EventKind.Touch);
            Console.WriteLine(string.Join("  ", new[]
            {
                F(s, 3).PadLeft(6), F(p.Facing * 180f / MathF.PI, 1).PadLeft(6), F(MathF.Atan2(p.Velocity.Z, p.Velocity.X) * 180f / MathF.PI, 1).PadLeft(7),
                F(p.Speed).PadLeft(5), F(p.RunSpeed).PadLeft(5), F(new Vector2(p.SteerVelocity.X, p.SteerVelocity.Z).Length()).PadLeft(5),
                F(Vector2.Dot(rel, fwd)).PadLeft(10), F(TouchModel.Lateral(p.Position, p.Facing, w.Ball.Position)).PadLeft(8),
                F(new Vector2(w.Ball.Velocity.X, w.Ball.Velocity.Z).Length()).PadLeft(6),
                (a == null ? "-" : a.Kind.ToString()).PadRight(12), (a == null ? "-" : a.Phase.ToString()).PadRight(9),
                (p.Chasing ? "Chase" : "Stick").PadRight(8),
                F(p.NextTouchEta).PadLeft(5), touched ? "TOUCH" : "",
            }));
        }
        return 0;
    }

    /// <summary>
    /// Per recorded round: per-tick acceleration and speed steps while in possession (see <see cref="SpeedSteps"/>: skill-move
    /// ticks included and counted on their own, shots excluded), and the speed surge between touches. A folder is searched
    /// with its subfolders, so a folder of session folders gives every round in them.
    /// </summary>
    static int RunReplays(string path, string dataDir, Tuning tuning)
    {
        var files = Directory.Exists(path) ? Directory.GetFiles(path, "round-*.json", SearchOption.AllDirectories).OrderBy(f => f).ToArray() : new[] { path };
        Console.WriteLine("replays (attacker in possession, shots excluded, skill moves included): acc = |dv|/dt m/s^2 per tick (turning included); dspd = |d speed|/dt m/s^2 per tick; steps = ticks with dspd over 15 m/s^2 (a speed step), skill = those during a skill move; surge = max-min speed between touches with the stick unchanged; interval cv over runs of same-stick touches; shot = speed steps from a shoot press to 0.5 s after the strike (in possession or not)");
        Console.WriteLine("file                                              ticks  acc_p99  acc_max  dspd_p99  dspd_max  steps  skill  shot  surge_mean  surge_p90  touches  int_cv  worst");
        var all = new SpeedSteps(); var allSurge = new List<float>(); var allCv = new List<float>();
        foreach (var f in files)
        {
            var replay = Replay.Load(f, out var err);
            if (replay == null) { Console.Error.WriteLine(err); continue; }
            var sc = DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", replay.ScenarioId + ".json")).Value;
            var att = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", replay.AttackerId + ".json")).Value;
            var kee = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", replay.KeeperId + ".json")).Value;
            if (sc == null || att == null || kee == null) continue;
            var w = new World(tuning, sc, att, kee, replay.Seed);
            w.Begin();
            var buf = new TickIntent[w.PlayerCount];
            int per = Math.Max(1, replay.PlayersPerTick);
            var p = w.Attacker;
            var steps = new SpeedSteps(); var surge = new List<float>(); var intervals = new List<float>(); var cvs = new List<float>();
            float worstDs = 0f; string worst = "-";
            int lastTouch = -1, touches = 0;
            float wMin = float.MaxValue, wMax = 0f;
            Vector2 lastStick = Vector2.Zero; bool lastSprint = false; bool clean = false;
            for (int i = 0; i < replay.TickCount && w.State != RoundState.Report; i++)
            {
                Array.Clear(buf);
                for (int k = 0; k < buf.Length && k < per; k++) buf[k] = replay.Intents[i * per + k].ToIntent();
                if (w.State != RoundState.Live) { steps.Before(p); w.Step(buf); steps.After(p, tuning.Dt, false, w.IsSimulating); continue; }
                var stick = buf[0].Move; bool spr = buf[0].Sprint >= 0.5f;
                bool same = Vector2.Distance(stick, lastStick) < 0.05f && spr == lastSprint;
                if (!same) { clean = false; if (intervals.Count >= 3) cvs.Add((float)(Std(intervals) / Mean(intervals))); intervals.Clear(); }
                lastStick = stick; lastSprint = spr;
                steps.Before(p);
                float dsMax0 = steps.DspdMax;
                w.Step(buf);
                bool touched = w.Events.Any(e => e.Team == 0 && e.PlayerIndex == 0 && e.Kind == EventKind.Touch);
                float sp = new Vector2(p.Velocity.X, p.Velocity.Z).Length();
                int n0 = steps.Ticks;
                steps.After(p, tuning.Dt, w.State == RoundState.Live, w.IsSimulating);
                if (steps.Ticks > n0)
                {
                    if (steps.DspdMax > dsMax0 && steps.DspdMax > worstDs) { worstDs = steps.DspdMax; worst = $"t={i * tuning.Dt:F2}s:{(p.SkillMove != null ? "skill " + p.SkillMove.Kind : p.TouchAction?.Kind.ToString())}@({p.Position.X:F1},{p.Position.Z:F1})"; }
                    wMin = MathF.Min(wMin, sp); wMax = MathF.Max(wMax, sp);
                }
                if (touched)
                {
                    touches++;
                    if (clean && lastTouch >= 0 && wMax > 0f && stick.Length() > 0.1f) { surge.Add(wMax - wMin); intervals.Add((i - lastTouch) * tuning.Dt); }
                    clean = true; lastTouch = i; wMin = float.MaxValue; wMax = 0f;
                }
            }
            if (intervals.Count >= 3) cvs.Add((float)(Std(intervals) / Mean(intervals)));
            all.Add(steps); allSurge.AddRange(surge); allCv.AddRange(cvs);
            string name = Path.GetFileName(Path.GetDirectoryName(f)) + "/" + Path.GetFileName(f);
            Console.WriteLine(string.Join("  ", new[]
            {
                name.PadRight(48), steps.Ticks.ToString().PadLeft(5), F(steps.AccP99, 1).PadLeft(7), F(steps.AccMax, 1).PadLeft(7),
                F(steps.DspdP99, 1).PadLeft(8), F(steps.DspdMax, 1).PadLeft(8),
                steps.Steps.ToString().PadLeft(5), steps.SkillSteps.ToString().PadLeft(5), steps.ShotSteps.ToString().PadLeft(4), F(Mean(surge)).PadLeft(10), F(Pct(surge, 0.9)).PadLeft(9), touches.ToString().PadLeft(7), F(Mean(cvs), 3).PadLeft(6), worst,
            }));
        }
        Console.WriteLine($"all: ticks {all.Ticks}  acc_p99 {F(all.AccP99, 1)}  acc_max {F(all.AccMax, 1)}  dspd_p99 {F(all.DspdP99, 1)}  dspd_max {F(all.DspdMax, 1)}  steps {all.Steps} ({F(all.PerThousand, 1)} per 1000 ticks, skill {all.SkillSteps})  shot {all.ShotSteps} in {all.ShotTicks} ticks  surge_mean {F(Mean(allSurge))}  surge_p90 {F(Pct(allSurge, 0.9))}  int_cv {F(Mean(allCv), 3)}");
        return 0;
    }
}
