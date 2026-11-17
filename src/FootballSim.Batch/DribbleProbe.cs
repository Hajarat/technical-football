using System.Globalization;
using System.Numerics;
using FootballSim.Core;

/// <summary>
/// The dribble probes: scripted stick traces through the real world on a long empty pitch (the keeper is far away and never
/// involved), per dribbling rating and seed. Prints, per probe and rating: possession kept, touches per second after a 2 s
/// warm-up, the ball's distance ahead of the feet at the midpoint between touches, the feet used, the largest steering
/// correction, the contact miss (ball centre to the planned contact point at contact) and touches made by a foot not in swing.
/// </summary>
static class DribbleProbe
{
    sealed record Probe(string Name, float Seconds, Func<float, TickIntent> Stick);

    static TickIntent Dir(float deg, float sprint = 0f, bool close = false)
    {
        float a = deg * MathF.PI / 180f;
        return new TickIntent { MoveX = MathF.Cos(a), MoveZ = MathF.Sin(a), Sprint = sprint, CloseControl = close };
    }

    static readonly Probe[] Probes =
    {
        new("straight_jog", 10f, _ => Dir(0f)),
        new("straight_sprint", 10f, _ => Dir(0f, 1f)),
        new("sprint_stop", 8f, s => s < 4f ? Dir(0f, 1f) : default),
        new("sprint_turn90", 7f, s => s < 3f ? Dir(0f, 1f) : Dir(90f, 1f)),
        new("jog_zigzag", 10f, s => Dir(((int)(s / 0.8f)) % 2 == 0 ? 45f : -45f)),
        new("close_zigzag", 10f, s => Dir(((int)(s / 0.8f)) % 2 == 0 ? 45f : -45f, 0f, true)),
        new("sprint_slowdown", 9f, s => s < 3f ? Dir(0f, 1f) : s < 6f ? Dir(0f) : Dir(0f, 0f, true)),
        new("jog_reverse", 7f, s => s < 3f ? Dir(0f) : Dir(180f)),
        new("sprint_reverse", 7f, s => s < 3f ? Dir(0f, 1f) : Dir(180f, 1f)),
    };

    sealed class Stats
    {
        public int Runs, Kept;
        public double TouchesPerSecond;
        public readonly List<float> Ahead = new();
        public readonly List<float> Miss = new();
        public int Left, Right, NotInSwing;
        public float MaxSteer;
        public readonly Dictionary<ActionKind, int> Kinds = new();
    }

    public static int Run(Dictionary<string, string> opts, string dataDir, Tuning tuning)
    {
        int seeds = opts.TryGetValue("seeds", out var ns) ? int.Parse(ns, CultureInfo.InvariantCulture) : 50;
        ulong seed0 = opts.TryGetValue("seed", out var ss) ? ulong.Parse(ss, CultureInfo.InvariantCulture) : 1UL;
        string attackerId = opts.TryGetValue("attacker", out var a) ? a : "striker_a";
        bool exact = opts.TryGetValue("exact", out var ex) && ex != "0";
        if (exact)
        {
            tuning.Touch.SpeedError = new RatingRange(0f, 0f);
            tuning.Touch.DirectionErrorDeg = new RatingRange(0f, 0f);
        }
        var only = opts.TryGetValue("only", out var o) ? o.Split(',') : null;
        var ratings = opts.TryGetValue("ratings", out var rs)
            ? rs.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray()
            : new[] { 1, 50, 99 };
        var baseSheet = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", attackerId + ".json")).Value;
        var keeper = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", "the_wall.json")).Value;
        if (baseSheet == null || keeper == null) { Console.Error.WriteLine("players not found"); return 1; }

        Console.WriteLine($"dribble probes: attacker={attackerId} (dribbling set per row) seeds={seeds} from {seed0}{(exact ? " errors=0" : "")}");
        var header = new[] { "probe", "dribbling", "kept_pct", "touches_s", "ahead_p10", "ahead_p50", "ahead_p90", "left", "right",
            "max_steer", "miss_p50", "miss_max", "not_in_swing", "kinds" };
        var rows = new List<string[]>();
        bool allKept = true;
        foreach (var probe in Probes)
        {
            if (only != null && !only.Contains(probe.Name)) continue;
            foreach (int rating in ratings)
            {
                var sheet = baseSheet.Clone();
                sheet.Dribbling = rating;
                var st = new Stats();
                for (int i = 0; i < seeds; i++) RunOne(probe, tuning, sheet, keeper, seed0 + (ulong)i, st);
                allKept &= st.Kept == st.Runs;
                st.Ahead.Sort(); st.Miss.Sort();
                string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);
                string P(List<float> l, double q) => l.Count == 0 ? "-" : F(l[Math.Min(l.Count - 1, (int)(q * l.Count))]);
                rows.Add(new[]
                {
                    probe.Name, rating.ToString(), (100.0 * st.Kept / st.Runs).ToString("F0", CultureInfo.InvariantCulture),
                    (st.TouchesPerSecond / st.Runs).ToString("F2", CultureInfo.InvariantCulture),
                    P(st.Ahead, 0.1), P(st.Ahead, 0.5), P(st.Ahead, 0.9), st.Left.ToString(), st.Right.ToString(), F(st.MaxSteer),
                    P(st.Miss, 0.5), st.Miss.Count == 0 ? "-" : F(st.Miss[^1]), st.NotInSwing.ToString(),
                    string.Join(" ", st.Kinds.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}")),
                });
            }
        }
        var widths = header.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        string Line(string[] cells) => string.Join("  ", cells.Select((c, i) => i is 0 or 13 ? c.PadRight(widths[i]) : c.PadLeft(widths[i])));
        Console.WriteLine(Line(header));
        foreach (var r in rows) Console.WriteLine(Line(r));
        Console.WriteLine(allKept ? "possession kept in every run" : "POSSESSION LOST in some runs");
        return 0;
    }

    static void RunOne(Probe probe, Tuning t, PlayerSheet sheet, PlayerSheet keeper, ulong seed, Stats st)
    {
        var sc = new Scenario
        {
            Id = "probe",
            Pitch = new PitchSpec { HalfLength = 400f, Width = 200f },
            Ball = new StartSpot { X = 150.5f, Z = 0f },
            Attacker = new StartSpot { X = 150f, Z = 0f },
        };
        var w = new World(t, sc, sheet, keeper, seed);
        w.Begin();
        var buf = new TickIntent[w.PlayerCount];
        int ticks = (int)(probe.Seconds * t.TickRate);
        int warm = 2 * t.TickRate;
        var dist = new float[ticks + 1];
        var touchTicks = new List<int>();
        bool gained = false, lost = false;
        var p = w.Attacker;
        for (int i = 1; i <= ticks && w.State == RoundState.Live; i++)
        {
            buf[0] = probe.Stick((i - 1) * t.Dt);
            w.Step(buf);
            dist[i] = TouchModel.HorizontalDistance(p.Position, w.Ball.Position);
            st.MaxSteer = MathF.Max(st.MaxSteer, new Vector2(p.SteerVelocity.X, p.SteerVelocity.Z).Length());
            foreach (var e in w.Events)
            {
                if (e.Team != 0 || e.PlayerIndex != 0) continue;
                if (e.Kind == EventKind.PossessionGained) gained = true;
                if (e.Kind == EventKind.PossessionLost && gained) lost = true;
                if (e.Kind != EventKind.Touch) continue;
                var act = p.PreviousAction is { Made: true } pa && pa.ContactTick == w.Tick ? pa : p.Action;
                if (act == null) continue;
                st.Kinds[act.Kind] = st.Kinds.GetValueOrDefault(act.Kind) + 1;
                if (e.Foot == Foot.Left) st.Left++; else st.Right++;
                if (act.Kind is not (ActionKind.First or ActionKind.Soft))
                {
                    st.Miss.Add(act.ContactMiss);
                    if (p.IsStepping && p.SwingFoot != act.Foot) st.NotInSwing++;
                }
                if (i >= warm) touchTicks.Add(i);
            }
        }
        st.Runs++;
        if (gained && !lost && p.InPossession) st.Kept++;
        float window = (ticks - warm) * t.Dt;
        st.TouchesPerSecond += touchTicks.Count / window;
        for (int k = 1; k < touchTicks.Count; k++) st.Ahead.Add(dist[(touchTicks[k - 1] + touchTicks[k]) / 2]);
    }
}
