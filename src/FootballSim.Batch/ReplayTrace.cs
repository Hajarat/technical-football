using System.Globalization;
using System.Numerics;
using System.Reflection;
using FootballSim.Core;

/// <summary>
/// Diagnostic: re-simulates one play-test replay and prints the attacker tick by tick (<c>--probe feel --rtrace &lt;file&gt;
/// [--from T] [--to T] [--steps-only 1]</c>, world ticks), then attributes every speed step (see <see cref="SpeedSteps"/>) to the
/// touch controller's state on that tick, and lists the committed touches not played at their contact. The dribble probe's
/// <c>--dtrace</c> uses it for a scripted trace.
/// </summary>
static class ReplayTrace
{
    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    static readonly FieldInfo Debt = typeof(PlayerState).GetField("SpeedDebt", NP)!;
    static readonly FieldInfo PostTurn = typeof(PlayerState).GetField("PostTurnUntil", NP)!;
    static readonly FieldInfo Skill = typeof(PlayerState).GetField("SkillAction", NP)!;
    static readonly FieldInfo StickRate = typeof(PlayerState).GetField("StickRate", NP)!;
    static readonly FieldInfo BoostUntil = typeof(PlayerState).GetField("TouchBoostUntil", NP)!;
    static readonly FieldInfo BoostFactor = typeof(PlayerState).GetField("TouchBoostFactor", NP)!;
    static readonly FieldInfo LastTouchKind = typeof(PlayerState).GetField("LastTouchKind", NP)!;

    static string F(double v, int d = 2) => v.ToString("F" + d, CultureInfo.InvariantCulture);
    static float Deg(float r) => r * 180f / MathF.PI;

    public static int Run(string file, string dataDir, Tuning tuning, Dictionary<string, string> opts)
    {
        int from = opts.TryGetValue("from", out var fs) ? int.Parse(fs, CultureInfo.InvariantCulture) : 0;
        int to = opts.TryGetValue("to", out var ts) ? int.Parse(ts, CultureInfo.InvariantCulture) : -1;
        bool quiet = opts.ContainsKey("steps-only");
        var replay = Replay.Load(file, out var err);
        if (replay == null) { Console.Error.WriteLine(err); return 1; }
        var sc = DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", replay.ScenarioId + ".json")).Value!;
        var att = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", replay.AttackerId + ".json")).Value!;
        var kee = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", replay.KeeperId + ".json")).Value!;
        var w = new World(tuning, sc, att, kee, replay.Seed);
        w.Begin();
        int per = Math.Max(1, replay.PlayersPerTick);
        TraceWorld(w, tuning, replay.TickCount, (i, buf) => { for (int k = 0; k < buf.Length && k < per; k++) buf[k] = replay.Intents[i * per + k].ToIntent(); },
            from, to, quiet, false);
        return 0;
    }

    /// <summary>The per-tick trace over a begun world; <paramref name="fill"/> sets tick i's intents.</summary>
    public static void TraceWorld(World w, Tuning tuning, int tickCount, Action<int, TickIntent[]> fill, int from, int to, bool quiet, bool countDrops)
    {
        var buf = new TickIntent[w.PlayerCount];
        var p = w.Attacker;
        Vector3 v0 = p.Velocity;
        float run0 = p.RunSpeed;
        Vector3 st0 = p.SteerVelocity;
        var cause = new Dictionary<string, int>();
        var stepTicks = new List<int>();
        var drops = new List<string>();
        float vmin = float.MaxValue; int vminTick = -1;
        if (!quiet)
            Console.WriteLine("tick  stick_deg mag spr | facing veldir  |v|   run  dRun/dt dSt/dt dV/dt | steer | ahead  lat   bv  | kind      phase     legs     eta  debt  post tau   fat  | touch");
        for (int i = 0; i < tickCount && w.State != RoundState.Report; i++)
        {
            Array.Clear(buf);
            fill(i, buf);
            var waiting = p.TouchAction is { Pending: true } pw ? pw : null;
            string wInfo = waiting == null ? "" : $"{waiting.Kind}/{(p.Chasing ? "Chase" : "Stick")}/tick{waiting.ContactTick}/committed{waiting.PlanTick}";
            var stick = buf[0].Move;
            bool hadBall = p.InPossession;
            bool skillBefore = Skill.GetValue(p) != null;
            w.Step(buf);
            int tick = w.Tick;
            bool touched = w.Events.Any(e => e.Team == 0 && e.PlayerIndex == 0 && e.Kind == EventKind.Touch);
            bool dropped = waiting != null && !waiting.Made && waiting.Phase == ActionPhase.Done && w.Tick >= waiting.ContactTick;
            if (countDrops && i * tuning.Dt >= 1f && p.Speed < vmin && p.InPossession && p.Speed > 0f && w.Tick > 240) { vmin = p.Speed; vminTick = w.Tick; }
            if (dropped)
            {
                var cpp = TouchPlanner.ContactPointFor(p, waiting!.Foot, tuning);
                drops.Add($"tick {w.Tick} {wInfo} miss {F(Vector2.Distance(new Vector2(cpp.X, cpp.Z), new Vector2(w.Ball.Position.X, w.Ball.Position.Z)))} swing {(p.IsStepping ? p.SwingFoot.ToString() : "idle")} foot {waiting.Foot} ballY {F(w.Ball.Position.Y)}");
            }
            float sp = new Vector2(p.Velocity.X, p.Velocity.Z).Length();
            float sp0 = new Vector2(v0.X, v0.Z).Length();
            float dspd = (sp - sp0) / tuning.Dt;
            float dRun = (p.RunSpeed - run0) / tuning.Dt;
            float dSt = (new Vector2(p.SteerVelocity.X, p.SteerVelocity.Z) - new Vector2(st0.X, st0.Z)).Length() / tuning.Dt;
            var a = p.TouchAction;
            string kind = a == null ? "-" : a.Kind.ToString();
            string phase = a == null ? "-" : a.Phase.ToString();
            string legs = p.Regain == RegainMode.Free ? "Regain" : p.Regain == RegainMode.Contested ? "Race" : p.Chasing ? "Chase" : "Stick";
            float debt = (float)Debt.GetValue(p)!;
            bool post = w.SimTime < (float)PostTurn.GetValue(p)!;
            bool skill = Skill.GetValue(p) != null;
            float tau = Movement.TimeConstantAt(p, w.SimTime, tuning);
            bool measured = w.State == RoundState.Live && hadBall && p.InPossession && p.Action is not { Kind: >= ActionKind.Power };
            bool isStep = measured && MathF.Abs(dspd) > SpeedSteps.Threshold;
            var rel = new Vector2(w.Ball.Position.X - p.Position.X, w.Ball.Position.Z - p.Position.Z);
            var fwd = new Vector2(MathF.Cos(p.Facing), MathF.Sin(p.Facing));
            if (isStep)
            {
                stepTicks.Add(tick);
                // The source of the speed step: the running speed (legs target / debt), the steering, or the facing swinging the
                // steering against the run (|v| = |run*fwd + steer|).
                string src = MathF.Abs(dRun) > SpeedSteps.Threshold ? (dRun < 0 ? "run-" : "run+") : dSt > SpeedSteps.Threshold ? "steer" : "mix";
                string pend = a is { Pending: true } ? $"{kind}/{legs}/{phase}" : $"none(last {LastTouchKind.GetValue(p)})";
                string key = $"{src} {(p.SkillMove is { FeetTurn: true } ? "feet-turn " : skill || skillBefore ? "skill " : "")}{pend}{(p.Chasing ? " chasing" : "")}{(debt > 0 ? " debt" : "")}{(skillBefore ? " skill-ended" : "")}{(touched ? " touch" : "")}";
                cause[key] = cause.GetValueOrDefault(key) + 1;
            }
            if (!quiet && tick >= from && (to < 0 || tick <= to))
            {
                Console.WriteLine(string.Join(" ", new[]
                {
                    tick.ToString().PadLeft(4),
                    (stick.Length() < 0.1f ? "-" : F(Deg(MathF.Atan2(stick.Y, stick.X)), 0)).PadLeft(9), F(stick.Length()).PadLeft(4), (buf[0].Sprint >= 0.5f ? "S" : ".").PadLeft(3), "|",
                    F(Deg(p.Facing), 0).PadLeft(6), F(Deg(MathF.Atan2(p.Velocity.Z, p.Velocity.X)), 0).PadLeft(6), F(sp).PadLeft(5), F(p.RunSpeed).PadLeft(5),
                    F(dRun, 1).PadLeft(7), F(dSt, 1).PadLeft(6), F(dspd, 1).PadLeft(6), "|", F(new Vector2(p.SteerVelocity.X, p.SteerVelocity.Z).Length()).PadLeft(5), "|",
                    F(Vector2.Dot(rel, fwd)).PadLeft(5), F(TouchModel.Lateral(p.Position, p.Facing, w.Ball.Position)).PadLeft(5),
                    F(new Vector2(w.Ball.Velocity.X, w.Ball.Velocity.Z).Length()).PadLeft(5), "|",
                    (p.SkillMove is { FeetTurn: true } ? "FeetTurn" : skill ? "SKILL:" + p.SkillMove?.Kind : kind).PadRight(9), phase.PadRight(9), legs.PadRight(8), F(p.NextTouchEta).PadLeft(5),
                    F(debt).PadLeft(5), (post ? "P" : ".").PadLeft(3), F(tau).PadLeft(5), F(p.TurnFatigue, 1).PadLeft(5), "|",
                    dropped ? "UNPLAYED" : "", touched ? "TOUCH " + w.Events.First(e => e.Kind == EventKind.Touch).ToString() : "", isStep ? "<STEP" : "", !measured ? "(unmeasured)" : "",
                }));
            }
            v0 = p.Velocity; run0 = p.RunSpeed; st0 = p.SteerVelocity;
        }
        Console.WriteLine($"steps {stepTicks.Count}: ticks {string.Join(",", stepTicks)}");
        foreach (var kv in cause.OrderByDescending(kv => kv.Value)) Console.WriteLine($"  {kv.Value,4}  {kv.Key}");
        foreach (var d in drops) Console.WriteLine("  event " + d);
        if (countDrops) Console.WriteLine($"  vmin {F(vmin)} at tick {vminTick} (after 2 s)");
    }
}
