using System.Globalization;
using System.Numerics;
using FootballSim.Core;

/// <summary>
/// The identity probe: every skill move from standing, a jog and a sprint (the skill probe's gestures and exit sticks, 8 seeds, both sides of a sided move),
/// measured so that two moves that look alike fail. Six ball measures in the player's frame (centred on him, axes along his run
/// at the gesture and across it; it does not turn with his facing), from the move's start to 0.3 s after its release: how far the
/// ball gets to each side, forward and back, its lift, and the move's own contacts. Four body measures over the move: his sideways
/// range off his entry line, his total turn, his lowest pace as a share of his entry pace, and the move's length. A pair of moves
/// fails when fewer than two ball measures differ by a unit (two moves that never touch the ball: fewer than two body measures).
/// Each move with a card is also checked against it (see docs/dribbling.md); a missed band fails the card.
/// </summary>
static class MoveIdentityProbe
{
    static readonly string[] Entries = { "standing", "jog", "sprint" };
    static readonly string[] BallNames = { "side_to", "side_away", "fwd", "back", "lift", "contacts" };
    static readonly float[] BallUnits = { 0.15f, 0.15f, 0.15f, 0.15f, 0.05f, 1f };
    static readonly string[] BodyNames = { "body_side", "turn_deg", "low_pace", "dur_s" };
    static readonly float[] BodyUnits = { 0.15f, 90f, 0.15f, 0.08f };

    /// <summary>One band of a move's card: a measure and its range, at the entries it applies to (null: all), with other ranges by entry.</summary>
    sealed record Band(string Measure, float Min, float Max, string[]? At = null, Dictionary<string, (float Min, float Max)>? By = null);

    const float Inf = float.PositiveInfinity;

    /// <summary>The cards of the moves whose sheets the simulation follows (the others get theirs as they are converted).</summary>
    static readonly Dictionary<string, Band[]> Cards = new()
    {
        ["elastico"] = new[]
        {
            new Band("out_m", 0.45f, 0.55f),
            new Band("out_s", 0f, 0.125f),
            new Band("contact2_s", 0.21f, 0.23f),
            new Band("whip_mps", 8f, Inf),
            new Band("hips_m", 0.15f, 0.20f),
            new Band("exit_side_mps", 2f, Inf),
            new Band("keep", 0.80f, Inf, new[] { "jog", "sprint" }),
            new Band("exit_deg", 40f, 75f),
        },
        ["drag_back"] = new[]
        {
            new Band("pull_m", 0.82f, 0.98f, null, new() { ["standing"] = (0.62f, 0.78f), ["sprint"] = (1.02f, 1.18f) }),
            new Band("pull_s", 0.35f, 0.37f),
            new Band("end_behind_m", 0.20f, 0.30f),
            new Band("end_side_m", 0.10f, 0.18f),
            new Band("end_ball_mps", 0f, 0.35f),
            new Band("stop_share", 0f, 0.45f, new[] { "jog", "sprint" }),
            new Band("hop_m", 0.20f, 0.30f),
            new Band("open_deg", 80f, 100f),
        },
    };

    sealed class Trial
    {
        public bool Made;
        public float[] Ball = new float[6];
        public float[] Body = new float[4];
        public Dictionary<string, float> Card = new();
        /// <summary>With --runs 1: the run's entry pace, reach and the ball against him at the first contact.</summary>
        public string Line = "";
    }

    public static int Run(Dictionary<string, string> opts, string dataDir, Tuning tuning)
    {
        int seeds = opts.TryGetValue("seeds", out var ns) ? int.Parse(ns, CultureInfo.InvariantCulture) : 8;
        ulong seed0 = opts.TryGetValue("seed", out var ss) ? ulong.Parse(ss, CultureInfo.InvariantCulture) : 1UL;
        string attackerId = opts.TryGetValue("attacker", out var a) ? a : "striker_a";
        var only = opts.TryGetValue("only", out var o) ? o.Split(',') : null;
        var sheet = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", attackerId + ".json")).Value;
        var keeper = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", "the_wall.json")).Value;
        if (sheet == null || keeper == null) { Console.Error.WriteLine("players not found"); return 1; }

        // the skill probe's scripts per move: both sides of a sided move (the ball lies a little to one side of a dribbler, so a
        // sided move measures differently on its two sides; every measure is taken toward the move's own side and the two are averaged)
        var moves = SkillProbe.Moves.Where(m => m.Expect != Gesture.KnockOn).GroupBy(m => m.Name).Select(g => g.ToList())
            .Where(g => only == null || only.Contains(g[0].Name)).ToList();
        Console.WriteLine($"move identity: attacker={attackerId} seeds={seeds} from {seed0}; the player's frame (centred on him, axes along his run at the " +
                          "gesture and across it); ball measures from the move's start to 0.3 s after its release: side_to / side_away = furthest toward " +
                          "and away from the move's side (his right for a move without one), fwd / back = furthest ahead and furthest back (negative: " +
                          "behind him), lift above rest, the move's own contacts; body measures over the move: sideways range, total turn, lowest pace " +
                          "as a share of the entry pace, length; means over the seeds; driver = what played it");
        var header = new[] { "move", "entry", "driver", "made" }.Concat(BallNames).Concat(BodyNames).Append("card").ToArray();
        var rows = new List<string[]>();
        var mean = new Dictionary<(string, string), (float[] Ball, float[] Body)>();
        var cardLines = new List<string>();
        int cardsFailed = 0;
        foreach (var sides in moves)
            foreach (var entry in Entries)
            {
                var move = sides[0];
                var runs = new Trial[seeds * sides.Count];
                Parallel.For(0, runs.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => runs[i] = RunOne(sides[i / seeds], entry, tuning, sheet, keeper, seed0 + (ulong)(i % seeds)));
                var made = runs.Where(r => r.Made).ToList();
                if (opts.ContainsKey("runs")) for (int i = 0; i < runs.Length; i++) Console.WriteLine($"RUN {move.Name} {entry} side {sides[i / seeds].Side} seed {seed0 + (ulong)(i % seeds)}: {runs[i].Line}");
                string F(float v, string f = "F2") => float.IsNaN(v) ? "-" : v.ToString(f, CultureInfo.InvariantCulture);
                var ball = Enumerable.Range(0, 6).Select(k => made.Count == 0 ? float.NaN : made.Average(r => r.Ball[k])).ToArray();
                var body = Enumerable.Range(0, 4).Select(k => made.Count == 0 ? float.NaN : made.Average(r => r.Body[k])).ToArray();
                if (made.Count > 0) mean[(move.Name, entry)] = (ball, body);
                string card = "-";
                if (Cards.TryGetValue(move.Name, out var bands) && made.Count > 0)
                {
                    var missed = new List<string>();
                    var all = new List<string>();
                    foreach (var b in bands)
                    {
                        if (b.At != null && !b.At.Contains(entry)) continue;
                        var (lo, hi) = b.By != null && b.By.TryGetValue(entry, out var by) ? by : (b.Min, b.Max);
                        float v = made.Average(r => r.Card.TryGetValue(b.Measure, out var x) ? x : float.NaN);
                        all.Add($"{b.Measure}={F(v, "F3")}");
                        if (!(v >= lo - 1e-4f && v <= hi + 1e-4f)) missed.Add($"{b.Measure}={F(v, "F3")} (band {F(lo)} to {(float.IsInfinity(hi) ? "any" : F(hi))})");
                    }
                    card = missed.Count == 0 ? "pass" : "FAIL";
                    if (opts.ContainsKey("cards")) cardLines.Add($"CARDS {move.Name} {entry}: " + string.Join(" ", all));
                    if (missed.Count > 0) { cardsFailed++; cardLines.Add($"CARD {move.Name} {entry}: " + string.Join("; ", missed)); }
                }
                rows.Add(new[] { move.Name, entry, Driver(move.Name, tuning), $"{made.Count}/{runs.Length}" }
                    .Concat(ball.Select((v, k) => F(v, k == 5 ? "F0" : "F2"))).Concat(body.Select((v, k) => F(v, k == 1 ? "F0" : "F2"))).Append(card).ToArray());
            }
        var widths = header.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        string Line(string[] cells) => string.Join("  ", cells.Select((c, i) => i < 3 ? c.PadRight(widths[i]) : c.PadLeft(widths[i])));
        Console.WriteLine(Line(header));
        foreach (var r in rows) Console.WriteLine(Line(r));
        foreach (var l in cardLines) Console.WriteLine(l);

        // the pairs: fewer than two measures a unit apart
        int pairsFailed = 0;
        foreach (var entry in Entries)
            for (int i = 0; i < moves.Count; i++)
                for (int j = i + 1; j < moves.Count; j++)
                {
                    string ni = moves[i][0].Name, nj = moves[j][0].Name;
                    if (!mean.TryGetValue((ni, entry), out var x) || !mean.TryGetValue((nj, entry), out var y)) continue;
                    bool noBall = x.Ball[5] < 0.5f && y.Ball[5] < 0.5f;
                    float[] u = noBall ? BodyUnits : BallUnits, p = noBall ? x.Body : x.Ball, q = noBall ? y.Body : y.Ball;
                    string[] names = noBall ? BodyNames : BallNames;
                    var apart = Enumerable.Range(0, u.Length).Where(k => MathF.Abs(p[k] - q[k]) >= u[k] - 1e-4f).Select(k => names[k]).ToList();
                    if (apart.Count >= 2) continue;
                    // (fake stop against fake cut is reported and not counted: their ball rules are not this work's)
                    bool counted = !(ni.StartsWith("fake_") && nj.StartsWith("fake_"));
                    if (counted) pairsFailed++;
                    Console.WriteLine($"PAIR {entry}: {ni} and {nj}: {(apart.Count == 0 ? "no measure apart" : "only " + apart[0] + " apart")}" +
                                      $" ({(noBall ? "body" : "ball")} measures){(counted ? "" : " (reported, not counted)")}");
                }
        Console.WriteLine($"IDENTITY pairs_failed={pairsFailed} cards_failed={cardsFailed}");
        return 0;
    }

    static string Driver(string move, Tuning t) =>
        t.Sheets != null && t.Skill.Driver.TryGetValue(move, out var d) && d == "sheet" ? "sheet" : "legacy";

    static Trial RunOne(SkillProbe.Move move, string entry, Tuning t, PlayerSheet sheet, PlayerSheet keeper, ulong seed)
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
        int g0 = SkillProbe.GestureTick(seed, t.TickRate);
        int total = g0 + 3 * t.TickRate;
        var res = new Trial();
        BallAction? act = null;
        int start = -1, end = -1, after = (int)MathF.Round(0.3f * t.TickRate);
        Vector2 f = default, r = default, origin = default, pPrev = default;
        float sgn = 1f, entryPace = 0f, facing0 = 0f, turn = 0f, sideTo = float.MinValue, sideAway = float.MinValue, fwd = float.MinValue, back = float.MaxValue, lift = 0f;
        float latMin = 0f, latMax = 0f, lowPace = float.MaxValue, fwdMax = float.MinValue;
        float side0 = float.NaN, outMax = float.MinValue, outAt = 0f, whip = float.MinValue, hips = float.MinValue, stopAt = float.NaN;
        Vector2 ballAtContact = default;
        for (int tick = 1; tick <= total && w.State is RoundState.Live or RoundState.Strike; tick++)
        {
            var it = SkillProbe.EntryIntent(entry, tick, t.TickRate);
            int i = tick - 1 - g0;
            if (end >= 0 && tick >= end) it = new TickIntent { MoveX = move.Exit.X, MoveZ = move.Exit.Y, Sprint = 1f };
            else if (i >= 0)
            {
                var s = move.Trace(Math.Min(i, SkillProbe.TraceTicks - 1), move.Side);
                if (i < SkillProbe.TraceTicks) { it.RightX = s.Right.X; it.RightZ = s.Right.Y; it.ShootHeld = s.Shoot; it.Cancel = s.Cancel; it.Finesse = it.Chip = s.Shoulders; }
                if (s.Left is Vector2 l) { it.MoveX = l.X; it.MoveZ = l.Y; }
                if (SkillProbe.SprintOff(move, p, i, g0)) it.Sprint = 0f;
            }
            buf[0] = it;
            var before = new Vector2(p.Position.X, p.Position.Z);
            float speedBefore = p.Speed, facingBefore = p.Facing;
            w.Step(buf);
            if (act == null && w.Tick > g0)
                foreach (var e in w.Events)
                    if (e.Team == 0 && e.PlayerIndex == 0 && e.Kind == EventKind.SkillMove && e.Skill is ActionKind k
                        && k == SkillMoves.KindOf(move.Expect) && p.SkillMove?.McGeady == (move.Expect == Gesture.McGeady))
                    {
                        act = p.SkillMove!;
                        start = w.Tick; end = act.EndTick;
                        f = act.RunDirection; r = new Vector2(-f.Y, f.X);
                        // toward the move's side; the playing foot's side for a move without one
                        sgn = act.Side == SkillSide.Left ? -1f : act.Side == SkillSide.Right ? 1f : act.Foot == Foot.Left ? -1f : 1f;
                        origin = before; entryPace = speedBefore; facing0 = facingBefore; pPrev = before;
                        var rel0 = new Vector2(w.Ball.Position.X, w.Ball.Position.Z) - new Vector2(p.Position.X, p.Position.Z);
                        res.Line = string.Create(CultureInfo.InvariantCulture, $"entry {entryPace:F2} m/s foot {act.Foot} reach {act.ContactTick - start} ticks, ball at start ahead {Vector2.Dot(rel0, f):F3} side {Vector2.Dot(rel0, r) * sgn:F3} ball pace {Vector2.Dot(new Vector2(w.Ball.Velocity.X, w.Ball.Velocity.Z), f):F2}");
                    }
            if (act == null) continue;
            var pos = new Vector2(p.Position.X, p.Position.Z);
            var rel = new Vector2(w.Ball.Position.X, w.Ball.Position.Z) - pos;
            float relF = Vector2.Dot(rel, f), relS = Vector2.Dot(rel, r) * sgn;
            if (w.Tick <= end + after)
            {
                sideTo = MathF.Max(sideTo, relS); sideAway = MathF.Max(sideAway, -relS);
                fwd = MathF.Max(fwd, relF); back = MathF.Min(back, relF);
                lift = MathF.Max(lift, w.Ball.Position.Y - t.Ball.Radius);
            }
            var vel = new Vector2(p.Velocity.X, p.Velocity.Z);
            var bvel = new Vector2(w.Ball.Velocity.X, w.Ball.Velocity.Z);
            if (w.Tick < end)
            {
                float lat = Vector2.Dot(pos - origin, r) * sgn;
                latMin = MathF.Min(latMin, lat); latMax = MathF.Max(latMax, lat);
                turn += MathF.Abs(Movement.WrapAngle(p.Facing - facingBefore)) * 180f / MathF.PI;
                lowPace = MathF.Min(lowPace, vel.Length());
                if (w.Tick >= act.ContactTick - 1) fwdMax = MathF.Max(fwdMax, Vector2.Dot(pos - origin, f));
                if (float.IsNaN(stopAt) && Vector2.Dot(vel, f) <= 0.1f) stopAt = (w.Tick + 1 - start) / (float)Math.Max(1, end - start);
                int lastContact = act.Contact2Tick > 0 ? act.Contact2Tick : end - 1;
                if (w.Tick <= lastContact) hips = MathF.Max(hips, -lat);
                if (w.Tick == act.ContactTick)
                {
                    side0 = relS; ballAtContact = new Vector2(w.Ball.Position.X, w.Ball.Position.Z);
                    res.Line += string.Create(CultureInfo.InvariantCulture, $" contact+{w.Tick - start} ball ahead {relF:F3} side {relS:F3} miss {act.ContactMiss:F3} pace {vel.Length():F2}");
                    if (act.Sheet != null)
                        res.Line += string.Create(CultureInfo.InvariantCulture, $" nudge ahead {Vector2.Dot(act.MeetNudge, f):F3} side {Vector2.Dot(act.MeetNudge, r) * sgn:F3} made {act.MeetNudgeMade.Length():F3}");
                }
                if (!float.IsNaN(side0) && w.Tick >= act.ContactTick && w.Tick <= lastContact)
                {
                    float outNow = -(relS - side0);
                    if (outNow > outMax + 0.02f) outAt = (w.Tick - act.ContactTick) * t.Dt;
                    outMax = MathF.Max(outMax, outNow);
                }
            }
            if (act.Contact2Tick > 0 && w.Tick >= act.Contact2Tick && w.Tick <= act.Contact2Tick + (int)MathF.Round(0.06f * t.TickRate))
                whip = MathF.Max(whip, Vector2.Dot(bvel - vel, r) * sgn);
            if (act.Contact2Tick > 0 && w.Tick == act.Contact2Tick)
                res.Card["exit_deg"] = MathF.Atan2(Vector2.Dot(bvel, r) * sgn, Vector2.Dot(bvel, f)) * 180f / MathF.PI;
            if (w.Tick == end - 1)
            {
                // the last tick the move has the ball and the body
                res.Card["end_behind_m"] = -relF;
                res.Card["end_side_m"] = relS;
                res.Card["end_ball_mps"] = bvel.Length();
                res.Card["exit_side_mps"] = Vector2.Dot(vel, r) * sgn;
                res.Card["hop_m"] = fwdMax - Vector2.Dot(pos - origin, f);
                res.Card["open_deg"] = Movement.WrapAngle(p.Facing - facing0) * 180f / MathF.PI * sgn;
                res.Card["pull_m"] = Vector2.Dot(ballAtContact - new Vector2(w.Ball.Position.X, w.Ball.Position.Z), f);
            }
            pPrev = pos;
            if (w.Tick >= end + after) break;
        }
        if (act == null) return res;
        res.Made = true;
        float pace0 = MathF.Max(entryPace, 1f);
        res.Ball = new[] { sideTo, sideAway, fwd, back, lift, (act.Made ? 1f : 0f) + (act.Contact2Tick > 0 ? 1f : 0f) };
        res.Body = new[] { latMax - latMin, turn, MathF.Min(1f, lowPace / pace0), (end - start + 1) * t.Dt };
        res.Card["out_m"] = outMax;
        res.Card["out_s"] = outAt;
        res.Card["contact2_s"] = act.Contact2Tick > 0 ? (act.Contact2Tick - act.ContactTick) * t.Dt : float.NaN;
        res.Card["whip_mps"] = whip;
        res.Card["hips_m"] = hips;
        res.Card["keep"] = lowPace / MathF.Max(entryPace, 1e-3f);
        res.Card["pull_s"] = (end - act.ContactTick) * t.Dt;
        res.Card["stop_share"] = float.IsNaN(stopAt) ? 1f : stopAt;
        return res;
    }
}
