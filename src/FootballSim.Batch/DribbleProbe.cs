using System.Globalization;
using System.Numerics;
using FootballSim.Core;

/// <summary>
/// The dribble probes: scripted stick traces through the real world on a long empty pitch (the keeper is far away and never
/// involved), per dribbling rating and seed. Prints, per probe and rating: possession kept, touches per second after a 2 s
/// warm-up, the ball's distance ahead of the feet at the midpoint between touches, the feet used, the largest steering
/// correction, the contact miss (ball centre to the planned contact point at contact), touches made by a foot not in swing,
/// touches dropped at their contact, the time from a stick turn past the execution angle to the turn touch, and for the flick
/// probes the time until the ball moves along the new stick line. The human-like traces (drift, curves, flicks) vary by seed
/// and are sampled at a gamepad's 60 Hz.
/// </summary>
static class DribbleProbe
{
    /// <param name="Stick">The stick at a time, seconds, for a seed.</param>
    /// <param name="Flick">For a flick probe, the time the stick leaves its line for a seed (the ball's alignment is timed from it).</param>
    /// <param name="SpeedCheck">The lowest speed after the first second is reported against the with-ball jog speed.</param>
    sealed record Probe(string Name, float Seconds, Func<float, int, TickIntent> Stick, Func<int, float>? Flick = null, bool SpeedCheck = false)
    {
        public Probe(string name, float seconds, Func<float, TickIntent> stick) : this(name, seconds, (s, _) => stick(s)) { }
    }

    static TickIntent Dir(float deg, float sprint = 0f, bool close = false)
    {
        float a = deg * MathF.PI / 180f;
        return new TickIntent { MoveX = MathF.Cos(a), MoveZ = MathF.Sin(a), Sprint = sprint, CloseControl = close };
    }

    /// <summary>The stick as a gamepad polls it, at 60 Hz (the simulation runs at 120 Hz, so each value is held two ticks).</summary>
    static float Pad(float s) => MathF.Floor(s * 60f) / 60f;

    /// <summary>A seed's fraction in [0, 1) for spreading start times over the stride.</summary>
    static float Frac(int seed) => (seed * 0.6180339887f) % 1f;

    /// <summary>Smooth noise in [-1, 1], about 2 Hz, with seed-chosen phases (a thumb wandering on a held stick).</summary>
    static float Wander(float s, int seed)
    {
        var r = new Random(seed * 7919 + 17);
        float p1 = (float)r.NextDouble() * MathF.Tau, p2 = (float)r.NextDouble() * MathF.Tau;
        return 0.6f * MathF.Sin(MathF.Tau * 2.0f * s + p1) + 0.4f * MathF.Sin(MathF.Tau * 1.3f * s + p2);
    }

    /// <summary>Jog straight, then sweep the stick by <paramref name="deg"/> over 0.25 s (to either side by seed) and hold it.</summary>
    static TickIntent Curve(float s, int seed, float deg)
    {
        s = Pad(s);
        float t0 = CurveStart(seed), side = seed % 2 == 0 ? 1f : -1f;
        float u = Math.Clamp((s - t0) / 0.25f, 0f, 1f);
        return Dir(side * deg * u);
    }

    static float CurveStart(int seed) => 2.5f + 0.7f * Frac(seed);

    /// <summary>Dribble straight, then flick the stick about 165 degrees, through neutral for two ticks.</summary>
    static TickIntent Flick(float s, int seed, bool close)
    {
        float t0 = CurveStart(seed), side = seed % 2 == 0 ? 1f : -1f;
        if (s < t0) return Dir(0f, 0f, close);
        if (s < t0 + 2f / 120f - 1e-4f) return new TickIntent { CloseControl = close };
        return Dir(side * 165f, 0f, close);
    }

    static readonly Probe[] Probes =
    {
        new("straight_jog", 10f, (_, _) => Dir(0f), SpeedCheck: true),
        new("straight_sprint", 10f, _ => Dir(0f, 1f)),
        new("sprint_stop", 8f, s => s < 4f ? Dir(0f, 1f) : default),
        new("sprint_turn90", 7f, s => s < 3f ? Dir(0f, 1f) : Dir(90f, 1f)),
        new("jog_zigzag", 10f, s => Dir(((int)(s / 0.8f)) % 2 == 0 ? 45f : -45f)),
        new("close_zigzag", 10f, s => Dir(((int)(s / 0.8f)) % 2 == 0 ? 45f : -45f, 0f, true)),
        new("sprint_slowdown", 9f, s => s < 3f ? Dir(0f, 1f) : s < 6f ? Dir(0f) : Dir(0f, 0f, true)),
        new("jog_reverse", 7f, s => s < 3f ? Dir(0f) : Dir(180f)),
        new("sprint_reverse", 7f, s => s < 3f ? Dir(0f, 1f) : Dir(180f, 1f)),
        // Human-like traces.
        new("jog_drift", 6f, (s, seed) => Dir(8f * Wander(Pad(s), seed)), SpeedCheck: true),
        new("jog_curve45", 5f, (s, seed) => Curve(s, seed, 45f)),
        new("jog_curve100", 5f, (s, seed) => Curve(s, seed, 100f)),
        new("jog_reverse_flick", 5f, (s, seed) => Flick(s, seed, false), CurveStart),
        new("close_reverse_flick", 5f, (s, seed) => Flick(s, seed, true), CurveStart),
    };

    /// <summary>A committed touch whose stick at contact was turned back to 30 degrees from its commit (internal to the core).</summary>
    static readonly System.Reflection.FieldInfo ClampedField =
        typeof(BallAction).GetField("Clamped", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

    static readonly System.Reflection.FieldInfo StretchedField =
        typeof(BallAction).GetField("Stretched", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

    sealed class Stats
    {
        public readonly List<float> TurnMiss = new();
        public int Runs, Kept;
        public double TouchesPerSecond;
        public readonly List<float> Ahead = new();
        public readonly List<float> Miss = new();
        public int Left, Right, NotInSwing;
        public float MaxSteer;
        public readonly Dictionary<ActionKind, int> Kinds = new();
        public int Drops, Stretched, Clamped;
        public readonly List<float> Latency = new();
        public readonly List<float> Align = new();
        public int AlignMissed;
        public float MinSpeedPct = float.PositiveInfinity;
        public readonly SpeedSteps Steps = new();
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
        if (opts.TryGetValue("dtrace", out var dtName))
        {
            // Diagnostic: one probe at the first --ratings value, seeds --seed.. (--seeds of them), traced tick by tick (--from/--to world
            // ticks; --steps-only prints only the per-run summary).
            var probe = Probes.First(x => x.Name == dtName);
            var sheet = baseSheet.Clone();
            sheet.Dribbling = ratings[0];
            int from = opts.TryGetValue("from", out var fs) ? int.Parse(fs, CultureInfo.InvariantCulture) : 0;
            int to = opts.TryGetValue("to", out var ts) ? int.Parse(ts, CultureInfo.InvariantCulture) : -1;
            int first = opts.TryGetValue("index", out var ix0) ? int.Parse(ix0, CultureInfo.InvariantCulture) : 0;
            for (int i = first; i < first + seeds; i++)
            {
                var sc = new Scenario { Id = "probe", Pitch = new PitchSpec { Length = 400f, Width = 200f }, Ball = new StartSpot { X = 150.5f, Z = 0f }, Attacker = new StartSpot { X = 150f, Z = 0f } };
                var w = new World(tuning, sc, sheet, keeper, seed0 + (ulong)i);
                w.Begin();
                Console.WriteLine($"== {dtName} dribbling {ratings[0]} index {i}");
                int idx = i;
                ReplayTrace.TraceWorld(w, tuning, (int)(probe.Seconds * tuning.TickRate), (j, buf) => buf[0] = probe.Stick(j * tuning.Dt, idx), from, to, opts.ContainsKey("steps-only"), true);
            }
            return 0;
        }
        if (opts.TryGetValue("save-replay", out var replayPath)) return SaveReplay(opts, replayPath, only, dataDir, tuning, baseSheet, keeper, attackerId, seed0);

        Console.WriteLine($"dribble probes: attacker={attackerId} (dribbling set per row) seeds={seeds} from {seed0}{(exact ? " errors=0" : "")}");
        var header = new[] { "probe", "dribbling", "kept_pct", "touches_s", "ahead_p10", "ahead_p50", "ahead_p90", "left", "right",
            "max_steer", "miss_p50", "miss_max", "not_in_swing", "drops", "stretched", "clamped", "turn_miss(p50/p90/max)", "turn_latency_s(p50/p90)", "align_s(p50/p90/miss)", "vmin_pct", "steps", "skill_steps", "kinds" };
        var rows = new List<string[]>();
        var pooled = new List<string>();
        bool allKept = true;
        foreach (var probe in Probes)
        {
            if (only != null && !only.Contains(probe.Name)) continue;
            var lat = new List<float>();
            var align = new List<float>();
            int drops = 0;
            foreach (int rating in ratings)
            {
                var sheet = baseSheet.Clone();
                sheet.Dribbling = rating;
                var st = new Stats();
                for (int i = 0; i < seeds; i++) RunOne(probe, tuning, sheet, keeper, seed0 + (ulong)i, i, st);
                allKept &= st.Kept == st.Runs;
                st.Ahead.Sort(); st.Miss.Sort(); st.TurnMiss.Sort(); st.Latency.Sort(); st.Align.Sort();
                lat.AddRange(st.Latency); align.AddRange(st.Align); drops += st.Drops;
                string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);
                string P(List<float> l, double q) => l.Count == 0 ? "-" : F(l[Math.Min(l.Count - 1, (int)(q * l.Count))]);
                rows.Add(new[]
                {
                    probe.Name, rating.ToString(), (100.0 * st.Kept / st.Runs).ToString("F0", CultureInfo.InvariantCulture),
                    (st.TouchesPerSecond / st.Runs).ToString("F2", CultureInfo.InvariantCulture),
                    P(st.Ahead, 0.1), P(st.Ahead, 0.5), P(st.Ahead, 0.9), st.Left.ToString(), st.Right.ToString(), F(st.MaxSteer),
                    P(st.Miss, 0.5), st.Miss.Count == 0 ? "-" : F(st.Miss[^1]), st.NotInSwing.ToString(),
                    st.Drops.ToString(), st.Stretched.ToString(), st.Clamped.ToString(),
                    st.TurnMiss.Count == 0 ? "-" : $"{P(st.TurnMiss, 0.5)}/{P(st.TurnMiss, 0.9)}/{F(st.TurnMiss[^1])}",
                    st.Latency.Count == 0 ? "-" : $"{P(st.Latency, 0.5)}/{P(st.Latency, 0.9)}",
                    probe.Flick == null ? "-" : $"{P(st.Align, 0.5)}/{P(st.Align, 0.9)}/{st.AlignMissed}",
                    float.IsFinite(st.MinSpeedPct) ? st.MinSpeedPct.ToString("F0", CultureInfo.InvariantCulture) : "-",
                    st.Steps.Steps.ToString(), st.Steps.SkillSteps.ToString(),
                    string.Join(" ", st.Kinds.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}")),
                });
            }
            lat.Sort(); align.Sort();
            string Q(List<float> l, double q) => l.Count == 0 ? "-" : l[Math.Min(l.Count - 1, (int)(q * l.Count))].ToString("F2", CultureInfo.InvariantCulture);
            if (lat.Count > 0 || align.Count > 0)
                pooled.Add($"{probe.Name,-20} drops {drops,4}  turn_latency_s {Q(lat, 0.5)}/{Q(lat, 0.9)} (n {lat.Count})" +
                           (probe.Flick == null ? "" : $"  align_s {Q(align, 0.5)}/{Q(align, 0.9)} (n {align.Count})"));
        }
        var widths = header.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        string Line(string[] cells) => string.Join("  ", cells.Select((c, i) => i == 0 || i == header.Length - 1 ? c.PadRight(widths[i]) : c.PadLeft(widths[i])));
        Console.WriteLine(Line(header));
        foreach (var r in rows) Console.WriteLine(Line(r));
        if (pooled.Count > 0)
        {
            Console.WriteLine($"pooled over dribbling {string.Join("/", ratings)} (p50/p90):");
            foreach (var line in pooled) Console.WriteLine("  " + line);
        }
        Console.WriteLine(allKept ? "possession kept in every run" : "POSSESSION LOST in some runs");
        return 0;
    }

    /// <summary>
    /// Plays the first --only probe's stick trace (seed index --index, default 0) for the shipped attacker sheet in a real
    /// scenario (--in, default halfway_run) and saves the round's replay, for screenshots in the game.
    /// </summary>
    static int SaveReplay(Dictionary<string, string> opts, string path, string[]? only, string dataDir, Tuning t, PlayerSheet sheet,
        PlayerSheet keeper, string attackerId, ulong seed)
    {
        var probe = Probes.FirstOrDefault(pr => only == null || only.Contains(pr.Name));
        if (probe == null) { Console.Error.WriteLine("no such probe"); return 2; }
        int index = opts.TryGetValue("index", out var ix) ? int.Parse(ix, CultureInfo.InvariantCulture) : 0;
        string scId = opts.TryGetValue("in", out var si) ? si : "halfway_run";
        var sc = DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", scId + ".json")).Value;
        if (sc == null) { Console.Error.WriteLine($"scenario {scId} not found"); return 1; }
        var w = new World(t, sc, sheet, keeper, seed + (ulong)index);
        var rec = new ReplayRecorder(w, attackerId, "the_wall", scId);
        w.Recorder = rec;
        var st = new Stats();
        RunTrace(w, probe, t, sheet, index, st);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        rec.Replay.Save(path);
        string flick = probe.Flick == null ? "" : $", flick at {probe.Flick(index):F2} s (tick {(int)MathF.Ceiling(probe.Flick(index) * t.TickRate) + 1})";
        Console.WriteLine($"saved {path}: {probe.Name} in {scId}, {rec.Replay.TickCount} ticks{flick}, align {(st.Align.Count > 0 ? st.Align[0].ToString("F2", CultureInfo.InvariantCulture) : "-")} s, kinds {string.Join(" ", st.Kinds.Select(kv => $"{kv.Key}:{kv.Value}"))}");
        return 0;
    }

    static void RunOne(Probe probe, Tuning t, PlayerSheet sheet, PlayerSheet keeper, ulong seed, int index, Stats st)
    {
        var sc = new Scenario
        {
            Id = "probe",
            Pitch = new PitchSpec { Length = 400f, Width = 200f },
            Ball = new StartSpot { X = 150.5f, Z = 0f },
            Attacker = new StartSpot { X = 150f, Z = 0f },
        };
        var w = new World(t, sc, sheet, keeper, seed);
        RunTrace(w, probe, t, sheet, index, st);
    }

    /// <summary>Plays a probe's stick trace in a begun-or-not world and gathers its metrics into <paramref name="st"/>.</summary>
    static void RunTrace(World w, Probe probe, Tuning t, PlayerSheet sheet, int index, Stats st)
    {
        w.Begin();
        var buf = new TickIntent[w.PlayerCount];
        int ticks = (int)(probe.Seconds * t.TickRate);
        int warm = 2 * t.TickRate;
        var dist = new float[ticks + 1];
        var touchTicks = new List<int>();
        bool gained = false, lost = false;
        var p = w.Attacker;
        float jogWithBall = t.Movement.JogSpeed * t.Touch.TopSpeedWithBall.ForRating(sheet.Dribbling);
        float execRad = t.Touch.ExecutionAngle * MathF.PI / 180f;
        float stickRef = float.NaN;
        int turnFrom = -1;
        int flickTick = probe.Flick == null ? -1 : (int)MathF.Ceiling(probe.Flick(index) * t.TickRate) + 1;
        float flickAngle = float.NaN;
        bool aligned = false;
        bool atSpeed = false;
        for (int i = 1; i <= ticks && w.State == RoundState.Live; i++)
        {
            var stick = probe.Stick((i - 1) * t.Dt, index);
            buf[0] = stick;
            var waiting = p.TouchAction is { Pending: true } pw ? pw : null;
            st.Steps.Before(p);
            w.Step(buf);
            st.Steps.After(p, t.Dt, i > t.TickRate);
            // A touch that was waiting and ended on or after its contact tick without being made: dropped at contact.
            if (waiting != null && !waiting.Made && waiting.Phase == ActionPhase.Done && w.Tick >= waiting.ContactTick) st.Drops++;
            bool pushed = stick.Move.Length() >= t.Touch.StickThreshold;
            float stickAngle = pushed ? MathF.Atan2(stick.MoveZ, stick.MoveX) : float.NaN;
            if (pushed && float.IsNaN(stickRef)) stickRef = stickAngle;
            // A stick turn past the execution angle from where the last turn left it opens a wait for the turn touch; the stick
            // coming back inside it first closes it unmeasured.
            if (pushed && !float.IsNaN(stickRef))
            {
                bool off = MathF.Abs(Movement.WrapAngle(stickAngle - stickRef)) > execRad;
                if (off && turnFrom < 0) turnFrom = i;
                else if (!off) turnFrom = -1;
            }
            if (flickTick > 0 && i >= flickTick && pushed && float.IsNaN(flickAngle)) flickAngle = stickAngle;
            if (flickTick > 0 && !aligned && !float.IsNaN(flickAngle) && p.InPossession)
            {
                var bv = new Vector2(w.Ball.Velocity.X, w.Ball.Velocity.Z);
                if (bv.Length() > 0.3f && MathF.Abs(Movement.WrapAngle(MathF.Atan2(bv.Y, bv.X) - flickAngle)) <= 30f * MathF.PI / 180f)
                {
                    aligned = true;
                    st.Align.Add((i - flickTick + 1) * t.Dt);
                }
            }
            // The lowest speed once up to speed: from the first second, and not before the run first reaches 95% of the with-ball
            // jog speed (a slow accelerator is still speeding up from the standing start after one second).
            if (probe.SpeedCheck && i * t.Dt >= 1f && (atSpeed |= p.Speed >= 0.95f * jogWithBall))
                st.MinSpeedPct = MathF.Min(st.MinSpeedPct, 100f * p.Speed / jogWithBall);
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
                if (act.Kind is not (ActionKind.First or ActionKind.Soft) && !act.IsSkill)
                {
                    st.Miss.Add(act.ContactMiss);
                    if ((bool)StretchedField.GetValue(act)!) st.Stretched++;
                    if (act.Kind is ActionKind.Turn or ActionKind.Cut) st.TurnMiss.Add(act.ContactMiss);
                    if ((bool)ClampedField.GetValue(act)!) st.Clamped++;
                    if (p.IsStepping && p.SwingFoot != act.Foot) st.NotInSwing++;
                }
                // The turn touch: a turn, a cut or a touch at the feet, or any touch that sends the ball along the stick.
                if (turnFrom > 0 && pushed)
                {
                    var bo = new Vector2(act.BallOutVelocity.X, act.BallOutVelocity.Z);
                    bool along = bo.Length() > 0.3f && MathF.Abs(Movement.WrapAngle(MathF.Atan2(bo.Y, bo.X) - stickAngle)) <= 1.5f * execRad;
                    if (act.Kind is ActionKind.Turn or ActionKind.Cut or ActionKind.DragBack || along)
                    {
                        st.Latency.Add((i - turnFrom) * t.Dt);
                        turnFrom = -1;
                        stickRef = stickAngle;
                    }
                }
                if (i >= warm) touchTicks.Add(i);
            }
        }
        st.Runs++;
        if (flickTick > 0 && !aligned) st.AlignMissed++;
        if (gained && !lost && p.InPossession) st.Kept++;
        float window = (ticks - warm) * t.Dt;
        st.TouchesPerSecond += touchTicks.Count / window;
        for (int k = 1; k < touchTicks.Count; k++) st.Ahead.Add(dist[(touchTicks[k - 1] + touchTicks[k]) / 2]);
    }
}

/// <summary>
/// Re-runs recorded play-test rounds (round-*.json, a folder searched with its subfolders, so a folder of session folders gives
/// every round in them) with the current code and counts, per round, the committed touches not played at their contact (all,
/// and with the stick pushed), and the speed steps in possession (see <see cref="SpeedSteps"/>). The rounds play out differently
/// from the recording as the simulation changes; the counts are what matters.
/// </summary>
static class SessionProbe
{
    /// <summary>The play-test rounds kept with the tests (2026-11-19, three penalty rounds with a gamepad).</summary>
    public static string DefaultDir(string root) => Path.Combine(root, "src", "FootballSim.Core.Tests", "TestData", "playtest");

    public static int Run(Dictionary<string, string> opts, string root, string dataDir, Tuning tuning)
    {
        string src = opts.TryGetValue("replays", out var r) ? r : DefaultDir(root);
        var files = Directory.Exists(src) ? Directory.GetFiles(src, "round-*.json", SearchOption.AllDirectories).OrderBy(f => f).ToArray()
            : File.Exists(src) ? new[] { src } : Array.Empty<string>();
        if (files.Length == 0) { Console.WriteLine($"session probe: no round-*.json under {src}; skipped"); return 0; }
        Console.WriteLine($"session probe: {files.Length} rounds from {src}");
        Console.WriteLine("steps = possession ticks whose |d|v||/dt is over 15 m/s^2 (shots excluded, skill moves included; skill = those during a move), acc_max = largest |dv|/dt over the same ticks, m/s^2");
        Console.WriteLine("shot = speed steps from a shoot press to 0.5 s after the strike (in possession or not); shots = presses in possession: struck / keeper touched first / neither (dropped or faked), committed = dribble touches committed after a press, foot_ok = strikes on the swinging foot in its window (or standing)");
        Console.WriteLine($"{"round",-50} {"ticks",6} {"unplayed",8} {"unplayed_pushed",15} {"poss_ticks",10} {"steps",6} {"skill",6} {"wall",5} {"shot",5} {"acc_max",8}  {"shots",8} {"committed",9} {"foot_ok",7}  outcome");
        int totalDrops = 0, totalPushedDrops = 0;
        var all = new SpeedSteps();
        var shots = new ShotAudit();
        foreach (var f in files)
        {
            var rp = Replay.Load(f, out var err);
            if (rp == null) { Console.Error.WriteLine(err); return 1; }
            var sc = DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", rp.ScenarioId + ".json")).Value;
            var att = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", rp.AttackerId + ".json")).Value;
            var kee = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", rp.KeeperId + ".json")).Value;
            if (sc == null || att == null || kee == null) { Console.Error.WriteLine($"{f}: scenario or players not found"); return 1; }
            var c = Audit(rp, tuning, sc, att, kee);
            totalDrops += c.Drops; totalPushedDrops += c.DropsPushed;
            all.Add(c.Steps);
            string name = Path.GetFileName(Path.GetDirectoryName(f)) + "/" + Path.GetFileName(f);
            var a = c.Shots;
            shots.Add(a);
            Console.WriteLine($"{name,-50} {rp.TickCount,6} {c.Drops,8} {c.DropsPushed,15} {c.Steps.Ticks,10} {c.Steps.Steps,6} {c.Steps.SkillSteps,6} {c.Steps.Wall,5} {c.Steps.ShotSteps,5} {c.Steps.AccMax,8:F1}  {$"{a.Struck}/{a.KeeperFirst}/{a.Neither}",8} {a.CommittedAfter,9} {$"{a.FootOk}/{a.Struck}",7}  {c.Outcome}");
        }
        Console.WriteLine($"shots pressed in possession: {shots.Presses}, struck {shots.Struck}, keeper touched first {shots.KeeperFirst}, neither {shots.Neither}; dribble touches committed after a press {shots.CommittedAfter}; strikes on the swinging foot {shots.FootOk}/{shots.Struck}; shot speed steps {all.ShotSteps} in {all.ShotTicks} ticks");
        Console.WriteLine($"total: {totalDrops} committed touches not played ({totalPushedDrops} with the stick pushed), " +
                          $"{all.Steps} speed steps in {all.Ticks} possession ticks ({all.PerThousand:F1} per 1000, skill {all.SkillSteps}; {all.Wall} more against the boards), acc_max {all.AccMax:F1} m/s^2");
        return 0;
    }

    public readonly record struct Counts(int Drops, int DropsPushed, SpeedSteps Steps, string Outcome, ShotAudit Shots);

    /// <summary>Plays a replay's recorded ticks and counts the committed touches not played at their contact.</summary>
    public static Counts Audit(Replay rp, Tuning t, Scenario sc, PlayerSheet att, PlayerSheet kee)
    {
        var w = new World(t, sc, att, kee, rp.Seed);
        w.Begin();
        var buf = new TickIntent[w.PlayerCount];
        int per = Math.Max(1, rp.PlayersPerTick);
        int drops = 0, dropsPushed = 0;
        var p = w.Attacker;
        var steps = new SpeedSteps();
        var shots = new ShotAudit();
        for (int tick = 0; tick < rp.TickCount && w.State != RoundState.Report; tick++)
        {
            Array.Clear(buf);
            for (int i = 0; i < w.PlayerCount && i < per; i++) buf[i] = rp.Intents[tick * per + i].ToIntent();
            var waiting = p.TouchAction is { Pending: true } pw ? pw : null;
            steps.Before(p);
            shots.Before(p);
            w.Step(buf);
            shots.After(w);
            steps.After(p, t.Dt, w.State == RoundState.Live, w.IsSimulating);
            bool pushed = buf[0].Move.Length() >= t.Touch.StickThreshold;
            if (waiting != null && !waiting.Made && waiting.Phase == ActionPhase.Done && w.Tick >= waiting.ContactTick)
            {
                drops++;
                if (pushed) dropsPushed++;
            }
        }
        return new Counts(drops, dropsPushed, steps, w.Report?.Outcome.Label() ?? w.State.ToString(), shots);
    }
}
