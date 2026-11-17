using System.Globalization;
using System.Numerics;
using FootballSim.Core;

/// <summary>
/// The skill probes: scripted right-stick (and shoot/cancel) traces through the real world on a long empty pitch, like the
/// dribble probes, per move, side, entry state, skill_moves rating and seed. The attacker runs along +X, so the player's
/// forward is +X and his right +Z. The gesture comes at a random moment of the dribble (2.0 to 3.0 s, drawn per seed); when
/// the move ends the left stick goes to the exit direction with sprint held. Prints, per row: gesture recognised, move made,
/// possession kept 2 s after the move, the time from the move's end to 90% of the with-ball top speed (t90), the same for a
/// baseline without the move (the same seed and entry, turning to the same exit direction with sprint on the tick the move
/// ended), and the ball's distance from the player at the move's end.
/// <para>
/// <c>--write-skill-replays dir</c> writes one replay per move from the edge_of_box start (jog toward goal, the gesture after
/// about 1 s, 2 s of running, then a shot), for the game's <c>--replay</c>.
/// </para>
/// </summary>
static class SkillProbe
{
    /// <summary>
    /// One scripted move: what to expect, the trace from the gesture's first tick, and the exit direction (in the run's frame)
    /// taken when the move ends.
    /// </summary>
    sealed record Move(string Name, Gesture Expect, int Side, string[] Entries, Func<int, int, Script> Trace, Vector2 Exit);

    /// <summary>
    /// One tick of a trace: the right stick, the left stick until the move ends (null: the entry run), shoot and cancel.
    /// </summary>
    readonly record struct Script(Vector2 Right, Vector2? Left, bool Shoot, bool Cancel);

    static readonly string[] AllEntries = { "standing", "jog", "sprint" };

    static Vector2 Rot(Vector2 v, float deg)
    {
        float a = deg * MathF.PI / 180f, c = MathF.Cos(a), s = MathF.Sin(a);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    static readonly Vector2 Ahead = new(1f, 0f);

    // Traces, in world axes for a player running along +X: forward = (1, 0), his right = (0, 1).
    static Script StepOver(int i, int side) => new(i < 6 ? Ahead : i < 12 ? new Vector2(0f, side) : Vector2.Zero, null, false, false);
    static Script Feint(int i, int side) => new(i < 6 ? new Vector2(0f, side) : Vector2.Zero, null, false, false);
    static Script Roll(int i, int side) => new(i < 20 ? new Vector2(0f, side) : Vector2.Zero, null, false, false);
    static Script DragBack(int i, int side) => new(i < 8 ? new Vector2(-1f, 0f) : Vector2.Zero, null, false, false);
    static Script Knock(int i, int side) => new(i < 8 ? Ahead : Vector2.Zero, null, false, false);
    static Script FakeStop(int i, int side) => new(Vector2.Zero, Vector2.Zero, i < 15, i is >= 6 and < 10);
    static Script FakeCut(int i, int side) => new(Vector2.Zero, i < 1 ? null : Rot(Ahead, side * 60f), i < 15, i is >= 6 and < 10);

    // Exits: away from the sold side at 45 degrees (step-over, feint), a right turn after a drag-back, the cut (fake cut),
    // straight on otherwise.
    static readonly Move[] Moves =
    {
        new("step_over", Gesture.StepOver, 1, AllEntries, StepOver, Rot(Ahead, -45f)),
        new("step_over", Gesture.StepOver, -1, AllEntries, StepOver, Rot(Ahead, 45f)),
        new("body_feint", Gesture.BodyFeint, 1, AllEntries, Feint, Rot(Ahead, -45f)),
        new("body_feint", Gesture.BodyFeint, -1, AllEntries, Feint, Rot(Ahead, 45f)),
        new("ball_roll", Gesture.BallRoll, 1, AllEntries, Roll, Ahead),
        new("ball_roll", Gesture.BallRoll, -1, AllEntries, Roll, Ahead),
        new("drag_back", Gesture.DragBack, 0, AllEntries, DragBack, new Vector2(0f, 1f)),
        new("knock_on", Gesture.KnockOn, 0, new[] { "sprint" }, Knock, Ahead),
        new("fake_stop", Gesture.FakeStop, 0, AllEntries, FakeStop, Ahead),
        new("fake_cut", Gesture.FakeCut, 1, AllEntries, FakeCut, Rot(Ahead, 60f)),
        new("fake_cut", Gesture.FakeCut, -1, AllEntries, FakeCut, Rot(Ahead, -60f)),
    };

    const int TraceTicks = 20;

    /// <summary>The left stick before the gesture. Standing: a short jog to take the ball, then the stick released (a stop).</summary>
    static TickIntent EntryIntent(string entry, int tick, int tickRate) => entry switch
    {
        "standing" => tick < tickRate / 2 ? new TickIntent { MoveX = 1f } : default,
        "jog" => new TickIntent { MoveX = 1f },
        _ => new TickIntent { MoveX = 1f, Sprint = 1f },
    };

    /// <summary>The gesture's tick for a seed: a random moment between 2.0 and 3.0 s (the same for the move and its baseline).</summary>
    static int GestureTick(ulong seed, int tickRate) => 2 * tickRate + new Random((int)(seed * 7919UL % int.MaxValue)).Next(tickRate);

    sealed class Result
    {
        public bool Recognised, Made, Kept;
        public int End = -1;
        public float T90 = float.NaN, BaseT90 = float.NaN, BallDistance = float.NaN;
        /// <summary>Step-over and feint: the ball's distance ahead of the player (along the run) on the move's first tick.</summary>
        public float StartAhead = float.NaN;
        /// <summary>Step-over and feint: the ball's distance ahead of the player when the move's own motion starts (after the close-in).</summary>
        public float ExecAhead = float.NaN;
        /// <summary>Step-over and feint: the ball's distance ahead of the player at the move's middle (the foot over the ball).</summary>
        public float MidAhead = float.NaN;
        /// <summary>Sole moves: the ball centre's distance from the foot's contact point when the sole lands.</summary>
        public float SoleMiss = float.NaN;
    }

    public static int Run(Dictionary<string, string> opts, string dataDir, Tuning tuning)
    {
        int seeds = opts.TryGetValue("seeds", out var ns) ? int.Parse(ns, CultureInfo.InvariantCulture) : 50;
        ulong seed0 = opts.TryGetValue("seed", out var ss) ? ulong.Parse(ss, CultureInfo.InvariantCulture) : 1UL;
        string attackerId = opts.TryGetValue("attacker", out var a) ? a : "striker_a";
        var ratings = opts.TryGetValue("ratings", out var rs)
            ? rs.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray()
            : new[] { 1, 50, 99 };
        var only = opts.TryGetValue("only", out var o) ? o.Split(',') : null;
        var baseSheet = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", attackerId + ".json")).Value;
        var keeper = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", "the_wall.json")).Value;
        if (baseSheet == null || keeper == null) { Console.Error.WriteLine("players not found"); return 1; }

        Console.WriteLine($"skill probes: attacker={attackerId} (skill_moves set per row) seeds={seeds} from {seed0}; gesture at a random " +
                          "moment from 2.0 to 3.0 s; exit with sprint: away from the sold side at 45 deg (step-over, feint), right turn " +
                          "(drag-back), the cut (fake cut), straight on (others); t90 = seconds from the move's end to 90% of the with-ball " +
                          "top speed, base_t90 = the same turn without the move (both medians over the seeds)");
        var header = new[] { "move", "side", "entry", "skill_moves", "recognised_pct", "made_pct", "kept_pct", "t90_s", "base_t90_s", "ball_dist_end_m",
            "start_ahead_m", "exec_ahead_m", "mid_ahead_m", "sole_miss_m" };
        var rows = new List<string[]>();
        foreach (var move in Moves)
        {
            if (only != null && !only.Contains(move.Name)) continue;
            foreach (var entry in move.Entries)
                foreach (int rating in ratings)
                {
                    var sheet = baseSheet.Clone();
                    sheet.SkillMoves = rating;
                    var results = new Result[seeds];
                    Parallel.For(0, seeds, i =>
                    {
                        ulong seed = seed0 + (ulong)i;
                        var r = RunOne(move, entry, tuning, sheet, keeper, seed, -1);
                        if (r.Made) r.BaseT90 = RunOne(move, entry, tuning, sheet, keeper, seed, r.End).T90;
                        results[i] = r;
                    });
                    string Pct(Func<Result, bool> f) => (100.0 * results.Count(f) / seeds).ToString("F0", CultureInfo.InvariantCulture);
                    string Mean(Func<Result, float> f)
                    {
                        var v = results.Where(r => r.Made).Select(f).Where(x => !float.IsNaN(x)).ToList();
                        return v.Count == 0 ? "-" : v.Average().ToString("F2", CultureInfo.InvariantCulture);
                    }
                    // t90 is bimodal (a run now and then stalls for a stride or two on its exit touches), so its median is shown.
                    string Median(Func<Result, float> f)
                    {
                        var v = results.Where(r => r.Made).Select(f).Where(x => !float.IsNaN(x)).OrderBy(x => x).ToList();
                        return v.Count == 0 ? "-" : v[v.Count / 2].ToString("F2", CultureInfo.InvariantCulture);
                    }
                    string MeanMax(Func<Result, float> f)
                    {
                        var v = results.Where(r => r.Made).Select(f).Where(x => !float.IsNaN(x)).ToList();
                        return v.Count == 0 ? "-" : $"{v.Average().ToString("F2", CultureInfo.InvariantCulture)}/{v.Max().ToString("F2", CultureInfo.InvariantCulture)}";
                    }
                    rows.Add(new[]
                    {
                        move.Name, move.Side > 0 ? "right" : move.Side < 0 ? "left" : "-", entry, rating.ToString(),
                        Pct(r => r.Recognised), Pct(r => r.Made), Pct(r => r.Kept), Median(r => r.T90), Median(r => r.BaseT90), Mean(r => r.BallDistance),
                        MeanMax(r => r.StartAhead), MeanMax(r => r.ExecAhead), MeanMax(r => r.MidAhead), MeanMax(r => r.SoleMiss),
                    });
                }
        }
        var widths = header.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        string Line(string[] cells) => string.Join("  ", cells.Select((c, i) => i < 3 ? c.PadRight(widths[i]) : c.PadLeft(widths[i])));
        Console.WriteLine(Line(header));
        foreach (var r in rows) Console.WriteLine(Line(r));
        return 0;
    }

    /// <summary>
    /// One run. With <paramref name="baselineTurn"/> at -1, the move: its trace from the gesture tick, then the exit with
    /// sprint once it has ended. Otherwise the baseline: no trace, the exit with sprint from that tick.
    /// </summary>
    static Result RunOne(Move move, string entry, Tuning t, PlayerSheet sheet, PlayerSheet keeper, ulong seed, int baselineTurn)
    {
        var sc = new Scenario
        {
            Id = "probe",
            Pitch = new PitchSpec { Length = 400f, Width = 200f },
            Ball = new StartSpot { X = 150.5f, Z = 0f },
            Attacker = new StartSpot { X = 150f, Z = 0f },
        };
        var w = new World(t, sc, sheet, keeper, seed);
        w.Begin();
        var p = w.Attacker;
        var buf = new TickIntent[w.PlayerCount];
        bool baseline = baselineTurn >= 0;
        int g0 = GestureTick(seed, t.TickRate);
        int total = g0 + 2 * t.TickRate + 4 * t.TickRate;
        float top90 = 0.9f * Movement.TopSpeedWithBall(sheet, t);
        var res = new Result();
        int end = baseline ? baselineTurn : -1, lostAt = -1;
        BallAction? act = null;
        bool gained = false;
        for (int tick = 1; tick <= total && w.State == RoundState.Live; tick++)
        {
            var it = EntryIntent(entry, tick, t.TickRate);
            int i = tick - 1 - g0;
            if (end >= 0 && tick >= end)
            {
                it = new TickIntent { MoveX = move.Exit.X, MoveZ = move.Exit.Y, Sprint = 1f };
            }
            else if (!baseline && i >= 0)
            {
                var s = move.Trace(Math.Min(i, TraceTicks - 1), move.Side);
                if (i < TraceTicks) { it.RightX = s.Right.X; it.RightZ = s.Right.Y; it.ShootHeld = s.Shoot; it.Cancel = s.Cancel; }
                if (s.Left is Vector2 l) { it.MoveX = l.X; it.MoveZ = l.Y; }
            }
            buf[0] = it;
            w.Step(buf);
            foreach (var e in w.Events)
            {
                if (e.Team != 0 || e.PlayerIndex != 0) continue;
                if (e.Kind == EventKind.PossessionGained) gained = true;
                if (e.Kind == EventKind.PossessionLost && gained && lostAt < 0) lostAt = w.Tick;
                if (baseline || end >= 0 || w.Tick <= g0) continue;
                if (e.Kind == EventKind.SkillMove && e.Skill is ActionKind k && move.Expect != Gesture.KnockOn && k == SkillMoves.KindOf(move.Expect))
                {
                    act = p.SkillMove;
                    end = act?.EndTick ?? w.Tick;
                    if (k is ActionKind.StepOver or ActionKind.BodyFeint && act != null)
                    {
                        // The ball as the move started (the event's position) against the player then (one tick of his run back).
                        var fwd = act.RunDirection;
                        var root = p.Position - p.Velocity * t.Dt;
                        res.StartAhead = (e.Position.X - root.X) * fwd.X + (e.Position.Z - root.Z) * fwd.Y;
                    }
                }
                if (e.Kind == EventKind.Touch && move.Expect == Gesture.KnockOn && MadeNow(p, w.Tick)?.Kind == ActionKind.KnockOn)
                    end = w.Tick;
            }
            if (!baseline && p.LastGesture == move.Expect && p.LastGestureTick > g0) res.Recognised = true;
            // The ball against the player at the start of the tick (the move's ticks are counted from there).
            if (act is { Kind: ActionKind.StepOver or ActionKind.BodyFeint } && w.Tick == act.ExecuteTick - 1)
                res.ExecAhead = (w.Ball.Position.X - p.Position.X) * act.RunDirection.X + (w.Ball.Position.Z - p.Position.Z) * act.RunDirection.Y;
            if (act is { Kind: ActionKind.StepOver or ActionKind.BodyFeint } && w.Tick == act.ContactTick)
                res.MidAhead = (w.Ball.Position.X - p.Position.X) * act.RunDirection.X + (w.Ball.Position.Z - p.Position.Z) * act.RunDirection.Y;
            if (end >= 0 && w.Tick == end) res.BallDistance = TouchModel.HorizontalDistance(p.Position, w.Ball.Position);
            if (end >= 0 && w.Tick >= end && float.IsNaN(res.T90) && p.RunSpeed >= top90) res.T90 = (w.Tick - end) * t.Dt;
            if (end >= 0 && w.Tick >= end + 2 * t.TickRate && !float.IsNaN(res.T90)) break;
        }
        res.End = end;
        res.Made = end >= 0;
        if (act is { Made: true, Kind: ActionKind.BallRoll or ActionKind.DragBack or ActionKind.FakeStop }) res.SoleMiss = act.ContactMiss;
        if (!res.Made) return res;
        res.Kept = (lostAt < 0 || lostAt > end + 2 * t.TickRate) && p.InPossession && w.Tick >= end + 2 * t.TickRate;
        return res;
    }

    /// <summary>The touch made on this tick: the previous action when the next one is already planned, else the current one.</summary>
    static BallAction? MadeNow(PlayerState p, int tick) =>
        p.PreviousAction is { Made: true } pa && pa.ContactTick == tick ? pa : p.Action is { Made: true } a && a.ContactTick == tick ? a : null;

    // ---- replays for screenshots ----

    public static int WriteReplays(string dir, string dataDir, Tuning tuning)
    {
        var sc = DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", "edge_of_box.json")).Value;
        var attacker = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", "striker_a.json")).Value;
        var keeper = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", "the_wall.json")).Value;
        if (sc == null || attacker == null || keeper == null) { Console.Error.WriteLine("edge_of_box, striker_a or the_wall not found"); return 1; }
        Directory.CreateDirectory(dir);
        var list = new (string File, Move Move, bool Sprint)[]
        {
            ("skill-step-over-right.json", Moves[0], false),
            ("skill-step-over-left.json", Moves[1], false),
            ("skill-body-feint-right.json", Moves[2], false),
            ("skill-body-feint-left.json", Moves[3], false),
            ("skill-ball-roll-right.json", Moves[4], false),
            ("skill-ball-roll-left.json", Moves[5], false),
            ("skill-drag-back.json", Moves[6], false),
            ("skill-knock-on.json", Moves[7], true),
            ("skill-fake-stop.json", Moves[8], false),
            ("skill-fake-cut-right.json", Moves[9], false),
            ("skill-fake-cut-left.json", Moves[10], false),
        };
        foreach (var (file, move, sprint) in list)
        {
            // The gesture about 1 s in; if the move is not made then (the ball out of reach), a little later.
            for (int delay = 0; delay <= 60; delay += 6)
                if (WriteOne(Path.Combine(dir, file), move, sprint, tuning.TickRate + delay, delay == 60, sc, attacker, keeper, tuning)) break;
        }
        return 0;
    }

    static bool WriteOne(string path, Move move, bool sprint, int g0, bool last, Scenario sc, PlayerSheet attacker, PlayerSheet keeper, Tuning tuning)
    {
        {
            var w = new World(tuning, sc, attacker, keeper, 1);
            var rec = new ReplayRecorder(w, attacker.Id, keeper.Id, sc.Id);
            w.Recorder = rec;
            w.Begin();
            var p = w.Attacker;
            var buf = new TickIntent[w.PlayerCount];
            int runEnd = g0 + TraceTicks + 2 * tuning.TickRate;
            int shootTicks = (int)(0.4f * tuning.TickRate);
            bool made = false;
            for (int tick = 1; tick <= runEnd + shootTicks + 10 * tuning.TickRate && w.State != RoundState.Report; tick++)
            {
                // Run at the goal centre (the stick from the player toward it).
                var toGoal = new Vector2(w.Pitch.GoalLineX - p.Position.X, -p.Position.Z);
                toGoal = toGoal.LengthSquared() > 1e-6f ? Vector2.Normalize(toGoal) : new Vector2(1f, 0f);
                var it = new TickIntent { MoveX = toGoal.X, MoveZ = toGoal.Y, Sprint = sprint ? 1f : 0f };
                int i = tick - 1 - g0;
                // The traces are written for a run along +X; the run here is along toGoal, so turn them by its angle.
                float turn = MathF.Atan2(toGoal.Y, toGoal.X) * 180f / MathF.PI;
                if (i >= 0 && i < TraceTicks)
                {
                    var s = move.Trace(i, move.Side);
                    var r = Rot(s.Right, turn);
                    it.RightX = r.X; it.RightZ = r.Y;
                    if (s.Left is Vector2 l) { var lw = Rot(l, turn); it.MoveX = lw.X; it.MoveZ = lw.Y; }
                    it.ShootHeld = s.Shoot; it.Cancel = s.Cancel;
                }
                else if (tick > runEnd && tick <= runEnd + shootTicks) it.ShootHeld = true;
                buf[0] = it;
                w.Step(buf);
                foreach (var e in w.Events)
                    if (e.Kind == EventKind.SkillMove || move.Expect == Gesture.KnockOn && e.Kind == EventKind.Touch && MadeNow(p, w.Tick)?.Kind == ActionKind.KnockOn)
                        made = true;
            }
            if (!made && !last) return false;
            rec.Replay.Save(path);
            Console.WriteLine($"wrote {path} ({rec.Replay.TickCount} ticks, gesture at tick {g0 + 1}, move {(made ? "made" : "NOT made")}, outcome {w.Outcome.Label()})");
            return true;
        }
    }
}
