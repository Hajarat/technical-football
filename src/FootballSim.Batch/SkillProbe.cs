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
    /// One tick of a trace: the right stick, the left stick until the move ends (null: the entry run), shoot, cancel and both
    /// shoulder buttons (finesse and chip).
    /// </summary>
    readonly record struct Script(Vector2 Right, Vector2? Left, bool Shoot, bool Cancel, bool Shoulders = false);

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
    // The drag-back is the left stick back with both shoulder buttons held (the right stick back alone does nothing).
    static Script DragBack(int i, int side) => i < 8 ? new(Vector2.Zero, new Vector2(-1f, 0f), false, false, true) : new(Vector2.Zero, null, false, false);
    static Script Knock(int i, int side) => new(i < 8 ? Ahead : Vector2.Zero, null, false, false);
    static Script FakeStop(int i, int side) => new(Vector2.Zero, Vector2.Zero, i < 15, i is >= 6 and < 10);
    static Script FakeCut(int i, int side) => new(Vector2.Zero, i < 1 ? null : Rot(Ahead, side * 60f), i < 15, i is >= 6 and < 10);
    // Elastico, heel-to-heel, spin.
    static Script Elastico(int i, int side) => new(i < 6 ? new Vector2(-1f, 0f) : i < 12 ? new Vector2(0f, side) : Vector2.Zero, null, false, false);
    static Script HeelToHeel(int i, int side) => new(i < 5 ? Ahead : i is >= 9 and < 14 ? new Vector2(-1f, 0f) : Vector2.Zero, null, false, false);
    static Script Spin(int i, int side) => new(i < 5 ? Ahead : i is >= 9 and < 13 ? new Vector2(0f, side) : Vector2.Zero, null, false, false);

    // Exits: away from the sold side at 45 degrees (step-over, feint, elastico: toward its side), a right turn after a drag-back,
    // the cut (fake cut), straight on otherwise.
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
        new("elastico", Gesture.Elastico, 1, AllEntries, Elastico, Rot(Ahead, 45f)),
        new("elastico", Gesture.Elastico, -1, AllEntries, Elastico, Rot(Ahead, -45f)),
        new("heel_to_heel", Gesture.HeelToHeel, 0, AllEntries, HeelToHeel, Ahead),
        new("spin", Gesture.SpinAndGo, 1, AllEntries, Spin, Ahead),
        new("spin", Gesture.SpinAndGo, -1, AllEntries, Spin, Ahead),
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
        /// <summary>The move's length from its start (the reach included), s; its speed steps (|d|v||/dt over 15 m/s^2 between its start and end).</summary>
        public float Dur = float.NaN;
        public int Steps;
        /// <summary>Ball roll: the largest forward (running) speed from 0.15 s after the move's start to its end, m/s.</summary>
        public float FwdLate = float.NaN;
        /// <summary>Ball roll: the running speed at the sole's landing, 0.15 s and 0.25 s after it, and at the release, m/s.</summary>
        public float RLand = float.NaN, R15 = float.NaN, R25 = float.NaN, REnd = float.NaN;
        /// <summary>From the move's start to its end, along and across the run: the player's travel and the ball's sideways travel, and the ball's distance ahead of the player at the end (negative: behind), metres.</summary>
        public float PFwd = float.NaN, PSide = float.NaN, BSide = float.NaN, BAhead = float.NaN;
        /// <summary>The ball's travel along the run from the start to the end, and its place to the side of the player at the end (toward the move's side positive), metres.</summary>
        public float BFwd = float.NaN, BRelSide = float.NaN;
        /// <summary>The largest forward speed (ground velocity along the run) from the first contact to the end, and at the end, m/s.</summary>
        public float FwdMax = float.NaN, FwdEnd = float.NaN;
        /// <summary>Two-contact moves: the ball's distance from the move's path at the second contact, metres; the largest distance of the ball from the player from the first contact to the end; the facing's turn over the move, degrees (signed toward the side).</summary>
        public float PathMiss2 = float.NaN, BallMax = float.NaN, Turn = float.NaN;
        /// <summary>Speed steps from the end of the move over the next 0.3 s (a speed-up no quicker than the speed law under the exit burst excepted).</summary>
        public int ExitSteps;
        /// <summary>Heel-to-heel: the ball's speed leaving the flick, m/s.</summary>
        public float Flick = float.NaN;
        /// <summary>With --list-failed: the ball and the planned touch every 4 ticks for 40 ticks from the gesture.</summary>
        public string Diag = "";
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
                          "top speed, base_t90 = the same turn without the move (both medians over the seeds); dur = the move from its start " +
                          "(the sole's reach included) to its end, steps = ticks in it whose |d|v||/dt is over 15 m/s^2 (all seeds), fwd_late_max = " +
                          "the ball roll's largest running speed from 0.15 s after its start, p_fwd / p_side = the player's travel along and across " +
                          "the run (side: toward the move's side positive), ball_side = the ball's, ball_end_ahead = the ball ahead of the player at " +
                          "the end (negative behind), means; run_land/... = the ball roll's running speed at the sole's landing, 0.15 s and 0.25 s " +
                          "after it and on its last tick, means");
        var header = new[] { "move", "side", "entry", "skill_moves", "recognised_pct", "made_pct", "kept_pct", "t90_s", "base_t90_s", "ball_dist_end_m",
            "start_ahead_m", "exec_ahead_m", "mid_ahead_m", "sole_miss_m", "dur_s", "steps", "exit_steps", "fwd_late_max", "p_fwd_m", "p_side_m", "ball_fwd_m", "ball_side_m", "ball_end_ahead_m", "ball_end_side_m",
            "fwd_max/release", "c2_path_miss_max", "ball_dist_max", "turn_deg", "flick_mps", "run_land/+0.15/+0.25/release" };
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
                    bool listFailed = opts.ContainsKey("list-failed");
                    Parallel.For(0, seeds, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
                    {
                        ulong seed = seed0 + (ulong)i;
                        var r = RunOne(move, entry, tuning, sheet, keeper, seed, -1, listFailed);
                        if (r.Made) r.BaseT90 = RunOne(move, entry, tuning, sheet, keeper, seed, r.End).T90;
                        results[i] = r;
                    });
                    if (listFailed)
                        for (int i = 0; i < seeds; i++) if (!results[i].Made || opts.ContainsKey("trace")) Console.WriteLine($"NOT MADE {move.Name} {entry} rating {rating} seed {seed0 + (ulong)i}: {results[i].Diag}");
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
                    string Max(Func<Result, float> f)
                    {
                        var v = results.Where(r => r.Made).Select(f).Where(x => !float.IsNaN(x)).ToList();
                        return v.Count == 0 ? "-" : v.Max().ToString("F2", CultureInfo.InvariantCulture);
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
                        Mean(r => r.Dur), results.Where(r => r.Made).Sum(r => r.Steps).ToString(), results.Where(r => r.Made).Sum(r => r.ExitSteps).ToString(), Max(r => r.FwdLate),
                        Mean(r => r.PFwd), Mean(r => r.PSide), Mean(r => r.BFwd), Mean(r => r.BSide), Mean(r => r.BAhead), Mean(r => r.BRelSide),
                        $"{Max(r => r.FwdMax)}/{Mean(r => r.FwdEnd)}", Max(r => r.PathMiss2), Max(r => r.BallMax), Mean(r => r.Turn), Mean(r => r.Flick),
                        move.Name == "ball_roll" ? $"{Mean(r => r.RLand)}/{Mean(r => r.R15)}/{Mean(r => r.R25)}/{Mean(r => r.REnd)}" : "-",
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
    static Result RunOne(Move move, string entry, Tuning t, PlayerSheet sheet, PlayerSheet keeper, ulong seed, int baselineTurn, bool diag = false)
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
        int startTick = int.MaxValue;
        Vector2 p0 = default, b0 = default;
        for (int tick = 1; tick <= total && w.State is RoundState.Live or RoundState.Strike; tick++)
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
                if (i < TraceTicks) { it.RightX = s.Right.X; it.RightZ = s.Right.Y; it.ShootHeld = s.Shoot; it.Cancel = s.Cancel; it.Finesse = it.Chip = s.Shoulders; }
                if (s.Left is Vector2 l) { it.MoveX = l.X; it.MoveZ = l.Y; }
            }
            buf[0] = it;
            float sp0 = p.Speed, face0 = p.Facing;
            w.Step(buf);
            if (act != null && w.Tick > act.EndTick && w.Tick <= act.EndTick + 36 && w.Shots.Count == 0)
            {
                float law = (t.Movement.SprintSpeed.At99 - sp0) / Movement.TimeConstantAt(p, w.SimTime, t) * t.Dt;
                float change = p.Speed - sp0;
                if (change > MathF.Max(15f * t.Dt, law) || -change > 15f * t.Dt) { res.ExitSteps++; if (diag) res.Diag += $" [EXIT STEP e+{w.Tick - act.EndTick} {sp0:F2}->{p.Speed:F2} run {p.RunSpeed:F2} law {law / t.Dt:F1}]"; }
            }
            if (act != null && w.Tick > startTick && w.Tick <= act.EndTick)
            {
                res.Turn = (float.IsNaN(res.Turn) ? 0f : res.Turn) + Movement.WrapAngle(p.Facing - face0) * 180f / MathF.PI * (move.Side < 0 ? -1f : 1f);
                if (w.Tick >= act.ContactTick)
                {
                    float fv = p.Velocity.X * act.RunDirection.X + p.Velocity.Z * act.RunDirection.Y;
                    res.FwdMax = float.IsNaN(res.FwdMax) ? fv : MathF.Max(res.FwdMax, fv);
                    if (w.Tick == act.EndTick - 1) res.FwdEnd = fv;
                    if (w.Tick == act.Contact2Tick && act.Kind == ActionKind.HeelToHeel) res.Flick = new Vector2(w.Ball.Velocity.X, w.Ball.Velocity.Z).Length();
                    float bd = TouchModel.HorizontalDistance(p.Position, w.Ball.Position);
                    if (w.Tick < act.EndTick) res.BallMax = float.IsNaN(res.BallMax) ? bd : MathF.Max(res.BallMax, bd);
                }
            }
            if (act != null && w.Tick <= act.EndTick && w.Tick > startTick)
            {
                if (MathF.Abs(p.Speed - sp0) / t.Dt > 15f) { res.Steps++; if (diag) res.Diag += $" [step t+{w.Tick - startTick} {sp0:F2}->{p.Speed:F2} run {p.RunSpeed:F2}]"; }
                if (diag) res.Diag += $" [t+{w.Tick - startTick} run {p.RunSpeed:F2} v {p.Speed:F2}]";
                if (act.Kind == ActionKind.BallRoll)
                {
                    int since = w.Tick - act.ContactTick, at15 = (int)MathF.Round(0.15f * t.TickRate), at25 = (int)MathF.Round(0.25f * t.TickRate);
                    if (since == 0) res.RLand = p.RunSpeed;
                    if (since == at15) res.R15 = p.RunSpeed;
                    if (since == at25) res.R25 = p.RunSpeed;
                    if (w.Tick == act.EndTick - 1) res.REnd = p.RunSpeed;
                }
                if (act.Kind == ActionKind.BallRoll && (w.Tick - startTick) * t.Dt > 0.15f && w.Tick < act.EndTick) res.FwdLate = float.IsNaN(res.FwdLate) ? p.RunSpeed : MathF.Max(res.FwdLate, p.RunSpeed);
                if (w.Tick == act.EndTick)
                {
                    var f = act.RunDirection;
                    var r = new Vector2(-f.Y, f.X) * (move.Side < 0 ? -1f : 1f);
                    var dp = new Vector2(p.Position.X - p0.X, p.Position.Z - p0.Y);
                    var db = new Vector2(w.Ball.Position.X - b0.X, w.Ball.Position.Z - b0.Y);
                    res.Dur = (act.EndTick - startTick) * t.Dt;
                    res.PFwd = Vector2.Dot(dp, f); res.PSide = Vector2.Dot(dp, r); res.BSide = Vector2.Dot(db, r); res.BFwd = Vector2.Dot(db, f);
                    res.BAhead = (w.Ball.Position.X - p.Position.X) * f.X + (w.Ball.Position.Z - p.Position.Z) * f.Y;
                    res.BRelSide = (w.Ball.Position.X - p.Position.X) * r.X + (w.Ball.Position.Z - p.Position.Z) * r.Y;
                    res.PathMiss2 = act.Contact2PathMiss;
                }
            }
            if (diag && !baseline && i >= 0 && i < 40 && i % 4 == 0)
                res.Diag += $" [g+{i} dist {TouchModel.HorizontalDistance(p.Position, w.Ball.Position):F2} ahead {(w.Ball.Position.X - p.Position.X):F2} bv {w.Ball.Velocity.X:F2} pv {p.Velocity.X:F2} act {p.Action?.Kind}/{p.Action?.Phase} eta {p.ActionContactEta:F2} skill {p.SkillMove?.Kind} gest {p.LastGesture}@{p.LastGestureTick - g0}]";
            foreach (var e in w.Events)
            {
                if (e.Team != 0 || e.PlayerIndex != 0) continue;
                if (e.Kind == EventKind.PossessionGained) gained = true;
                if (e.Kind == EventKind.PossessionLost && gained && lostAt < 0) lostAt = w.Tick;
                if (baseline || end >= 0 || w.Tick <= g0) continue;
                if (e.Kind == EventKind.SkillMove && e.Skill is ActionKind k && move.Expect != Gesture.KnockOn
                    && k == SkillMoves.KindOf(move.Expect))
                {
                    act = p.SkillMove;
                    end = act?.EndTick ?? w.Tick;
                    startTick = w.Tick - 1;
                    p0 = new Vector2(p.Position.X - p.Velocity.X * t.Dt, p.Position.Z - p.Velocity.Z * t.Dt);
                    b0 = new Vector2(e.Position.X, e.Position.Z);
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
            ("skill-elastico-right.json", Moves[11], false),
            ("skill-elastico-left.json", Moves[12], false),
            ("skill-heel-to-heel.json", Moves[13], false),
            ("skill-spin-right.json", Moves[14], false),
            ("skill-spin-left.json", Moves[15], false),
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
                    it.ShootHeld = s.Shoot; it.Cancel = s.Cancel; it.Finesse = it.Chip = s.Shoulders;
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
