using System.Globalization;
using System.Numerics;
using FootballSim.Core;

/// <summary>
/// Diagnostic for the scoring calibration (<c>--probe shots --scenario &lt;id&gt; [--n N --seed S]</c>): plays the scripted rounds
/// like the batch run and, per round, reads the state at the strike (distance to the goal, the attacker's speed, the charge
/// against the ideal, the ball's offset from the striking foot's contact point and from the feet, the shot's accuracy and speed
/// factors, its foot and surface, the rule of the foot choice that decided it (fixed from the release) and whether the
/// contact check handed it to the other foot, the body angle, the launch speed, the time from the press to the strike and his
/// turn fatigue just before the strike) and how the round
/// ended, then prints means and shares (on target = the shots that, flying free of the keeper, go in), the reasons wide shots went
/// wide, why rounds had no shot, and the share of all sprint pushes that were cut short near the keeper. It also prints the
/// launch solve's turn (adjust: how far the launch was turned against the shot's bend) and the drift (where the shot crossed
/// the goal line, flying free of the keeper and the frame, minus where it was aimed: the drawn error alone, since the solve
/// cancels the bend), and with <c>--list 1</c> those two for every shot. <c>--type finesse</c> (power, driven, finesse,
/// low_finesse, chip) and <c>--side 1</c> or <c>-1</c> (the +Z or -Z post) set the scripted attacker's shot.
/// </summary>
static class ShotDiag
{
    public static int Run(Dictionary<string, string> opts, string dataDir, Tuning tuning)
    {
        string id = opts["scenario"];
        int n = opts.TryGetValue("n", out var ns) ? int.Parse(ns, CultureInfo.InvariantCulture) : 1000;
        ulong seed0 = opts.TryGetValue("seed", out var ss) ? ulong.Parse(ss, CultureInfo.InvariantCulture) : 1UL;
        var sc = DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", id + ".json")).Value!;
        var att = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", "striker_a.json")).Value!;
        var kee = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", "the_wall.json")).Value!;
        var dist = new List<float>(); var speed = new List<float>(); var charge = new List<float>(); var chargeMiss = new List<float>();
        var offFoot = new List<float>(); var offFeet = new List<float>(); var ahead = new List<float>(); var sigmaH = new List<float>();
        var touchesBefore = new List<float>(); var timeToShot = new List<float>(); var fatigue = new List<float>();
        var factors = new Dictionary<string, List<float>>();
        var launch = new List<float>(); var pressToStrike = new List<float>(); var bodyAngle = new List<float>();
        var feet = new Dictionary<string, int>();
        var adjust = new List<float>(); var drift = new List<float>(); var absDrift = new List<float>();
        ShotType type = opts.TryGetValue("type", out var tn) ? Enum.Parse<ShotType>(tn.Replace("_", ""), ignoreCase: true) : ShotType.Power;
        int side = opts.TryGetValue("side", out var sd) ? int.Parse(sd, CultureInfo.InvariantCulture) : 0;
        bool list = opts.ContainsKey("list");
        ScriptedAttacker Script() => new() { Type = type, Side = side };
        var rules = new Dictionary<StrikeRule, int>();
        int onTarget = 0, farPost = 0, swapped = 0, noContact = 0;
        var ends = new Dictionary<string, int>();
        var noShot = new Dictionary<string, int>();
        int wide = 0, wideAimOut = 0, wideError = 0;
        int sprintPushes = 0, cutPushes = 0;
        // The rounds in parallel (each depends only on its seed), then gathered in round order.
        var reads = new RoundRead[n];
        Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = 4 }, r =>
            reads[r] = ReadRound(tuning, sc, att, kee, seed0 + (ulong)r, Script, list));
        foreach (var rd in reads)
        {
            sprintPushes += rd.SprintPushes; cutPushes += rd.CutPushes;
            if (rd.NoShot is { } why)
            {
                noShot[why] = noShot.GetValueOrDefault(why) + 1;
                continue;
            }
            dist.Add(rd.Distance); speed.Add(rd.Speed); charge.Add(rd.Charge); chargeMiss.Add(rd.ChargeMiss); sigmaH.Add(rd.SigmaH);
            offFeet.Add(rd.OffFeet); ahead.Add(rd.Ahead);
            offFoot.Add(rd.OffFoot);
            touchesBefore.Add(rd.Touches); timeToShot.Add(rd.TimeToShot); fatigue.Add(rd.Fatigue);
            foreach (var (name, value) in rd.Factors) { if (!factors.TryGetValue(name, out var l)) factors[name] = l = new(); l.Add(value); }
            launch.Add(rd.Launch); bodyAngle.Add(rd.BodyAngle);
            if (rd.OnTarget) onTarget++;
            adjust.Add(rd.Adjust);
            if (!float.IsNaN(rd.Crossed)) { drift.Add(rd.Crossed - rd.AimedZ); absDrift.Add(MathF.Abs(rd.Crossed - rd.AimedZ)); }
            if (rd.ListLine != null) Console.WriteLine(rd.ListLine);
            if (rd.PressToStrike is float pts) pressToStrike.Add(pts);
            feet[rd.How] = feet.GetValueOrDefault(rd.How) + 1;
            rules[rd.Rule] = rules.GetValueOrDefault(rd.Rule) + 1;
            if (rd.Rule == StrikeRule.FarPost) farPost++;
            if (rd.HandedOver) { if (rd.Rule == StrikeRule.Ahead) swapped++; else noContact++; }
            ends[rd.End.ToString()] = ends.GetValueOrDefault(rd.End.ToString()) + 1;
            if (rd.End == ShotEnd.Wide)
            {
                wide++;
                if (rd.AimedOut) wideAimOut++; else wideError++;
            }
        }
        string F(List<float> l) => l.Count == 0 ? "-" : $"{l.Average():F2} (p10 {Q(l, 0.1):F2}, p90 {Q(l, 0.9):F2})";
        float Q(List<float> l, double q) { var c = l.OrderBy(x => x).ToList(); return c[Math.Min(c.Count - 1, (int)(q * c.Count))]; }
        Console.WriteLine($"shot diagnostic: scenario {id}, {n} rounds from seed {seed0}, {dist.Count} with a shot ({type}{(side == 0 ? "" : side > 0 ? ", the +Z post" : ", the -Z post")})");
        Console.WriteLine($"  ends: {string.Join(", ", ends.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {100.0 * kv.Value / n:F1}%"))}");
        Console.WriteLine($"  on target: {(dist.Count == 0 ? "-" : $"{100.0 * onTarget / dist.Count:F1}% of the shots")}   launch speed m/s: {F(launch)}   press to strike s: {F(pressToStrike)}");
        Console.WriteLine($"  body angle deg: {F(bodyAngle)}   struck with: {string.Join(", ", feet.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {100.0 * kv.Value / Math.Max(1, dist.Count):F1}%"))}");
        Console.WriteLine($"  far_post: {(dist.Count == 0 ? "-" : $"yes {100.0 * farPost / dist.Count:F1}%, no {100.0 * (dist.Count - farPost) / dist.Count:F1}%")}   run_speed factor: {(factors.TryGetValue("speed x run_speed", out var rs) ? F(rs) : "-")}");
        Console.WriteLine($"  rule: {(dist.Count == 0 ? "-" : string.Join(", ", Enum.GetValues<StrikeRule>().Select(r => $"{r.ToString().ToLowerInvariant()} {100.0 * rules.GetValueOrDefault(r) / dist.Count:F1}%")))}");
        Console.WriteLine($"  handed over: {(dist.Count == 0 ? "-" : $"swap {100.0 * swapped / dist.Count:F1}%, no contact {100.0 * noContact / dist.Count:F1}%")}");
        Console.WriteLine($"  launch solve: adjust deg {F(adjust)}   drift m (crossed Z minus aimed Z, {drift.Count} crossed the line): {F(drift)}   |drift| {F(absDrift)}");
        Console.WriteLine($"  no shot: {(noShot.Count == 0 ? "none" : string.Join(", ", noShot.Select(kv => $"{kv.Key} {kv.Value}")))}");
        Console.WriteLine($"  distance to goal at strike m: {F(dist)}");
        Console.WriteLine($"  attacker speed at strike m/s: {F(speed)}");
        Console.WriteLine($"  charge: {F(charge)}   charge miss deg: {F(chargeMiss)}   sigma_h deg: {F(sigmaH)}");
        Console.WriteLine($"  ball from the feet m: {F(offFeet)}   ahead along the facing m: {F(ahead)}   from the striking foot's contact point m: {F(offFoot)}");
        Console.WriteLine($"  touches before the shot: {F(touchesBefore)}   seconds to the shot: {F(timeToShot)}");
        Console.WriteLine($"  turn fatigue at the strike (0-100): {F(fatigue)}");
        Console.WriteLine($"  sprint pushes cut near the keeper: {(sprintPushes == 0 ? "-" : $"{100.0 * cutPushes / sprintPushes:F1}% ({cutPushes} of {sprintPushes})")}");
        foreach (var kv in factors.OrderBy(kv => kv.Key)) Console.WriteLine($"  factor {kv.Key}: {F(kv.Value)}");
        Console.WriteLine($"  wide {wide}: aimed outside the posts {wideAimOut}, aimed inside and sent wide {wideError}");
        return 0;
    }

    /// <summary>What one round gave the diagnostic: the push counts, and either why it had no shot or the reads at the strike.</summary>
    sealed class RoundRead
    {
        public int SprintPushes, CutPushes;
        public string? NoShot;
        public float Distance, Speed, Charge, ChargeMiss, SigmaH, OffFeet, Ahead, OffFoot, Touches, TimeToShot, Fatigue, Launch, BodyAngle;
        public readonly List<(string Name, float Value)> Factors = new();
        public bool OnTarget, HandedOver, AimedOut;
        public float Adjust, Crossed, AimedZ;
        public string? ListLine;
        public float? PressToStrike;
        public string How = "";
        public StrikeRule Rule;
        public ShotEnd End;
    }

    /// <summary>Plays one scripted round to its first strike and reads it there (then plays it again to see how the shot ended).</summary>
    static RoundRead ReadRound(Tuning tuning, Scenario sc, PlayerSheet att, PlayerSheet kee, ulong seed, Func<ScriptedAttacker> Script, bool list)
    {
        var rd = new RoundRead();
        var w = new World(tuning, sc, att, kee, seed);
        var script = Script();
        w.Begin();
        var buf = new TickIntent[w.PlayerCount];
        int cap = (int)((tuning.Round.MaxSeconds + 10f) * tuning.TickRate);
        var p = w.Attacker;
        Vector3 ballPrev = default, posPrev = default; float facePrev = 0f, speedPrev = 0f, fatiguePrev = 0f; Foot footPrev = Foot.Right;
        int touches = 0;
        bool shot = false;
        int pressTick = -1;
        for (int i = 0; i < cap; i++)
        {
            if (p.ShotAction is { Made: false } sa) pressTick = sa.PlanTick;
            ballPrev = w.Ball.Position; posPrev = p.Position; facePrev = p.Facing; speedPrev = p.Speed; footPrev = p.StrikeFoot; fatiguePrev = p.TurnFatigue;
            buf[0] = script.Next(w);
            w.Step(buf);
            touches += w.Events.Count(e => e.Kind == EventKind.Touch && e.Team == 0);
            if (p.TouchAction is { Made: true, Kind: ActionKind.Sprint } push && push.ContactTick == w.Tick)
            {
                rd.SprintPushes++;
                if (push.DangerCut > 0f) rd.CutPushes++;
            }
            if (w.Shots.Count > 0) { shot = true; break; }
            if (w.State is RoundState.Outcome or RoundState.Report) break;
        }
        if (!shot)
        {
            rd.NoShot = w.KeeperReport.WonBall ? "keeper won the ball" : w.BallController == p ? "in possession, no shot (time)" : "ball loose / lost";
            return rd;
        }
        var s = w.Shots[0];
        // The ball at the strike tick, before it was struck, from the feet and from the striking foot's contact point.
        var rel = new Vector2(ballPrev.X - posPrev.X, ballPrev.Z - posPrev.Z);
        var fwd = new Vector2(MathF.Cos(facePrev), MathF.Sin(facePrev));
        var cp = TouchPlanner.ContactPointFor(p, s.Foot, tuning);
        rd.Distance = s.Distance; rd.Speed = speedPrev; rd.Charge = s.Charge; rd.ChargeMiss = s.ChargeMissDeg; rd.SigmaH = s.SigmaH;
        rd.OffFeet = rel.Length(); rd.Ahead = Vector2.Dot(rel, fwd);
        rd.OffFoot = new Vector2(ballPrev.X - cp.X, ballPrev.Z - cp.Z).Length();
        rd.Touches = touches; rd.TimeToShot = w.SimTime; rd.Fatigue = fatiguePrev;
        foreach (var f in s.Factors) rd.Factors.Add((f.Name, f.Value));
        foreach (var f in s.SpeedFactors) rd.Factors.Add(("speed x " + f.Name, f.Value));
        rd.Launch = s.Speed; rd.BodyAngle = s.BodyAngleDeg;
        // On target: the ball as struck, flying free of the keeper, goes in.
        rd.OnTarget = ShotModel.FliesIn(w.Ball.Position, w.Ball.Velocity, w.Ball.Spin, w.Pitch, tuning);
        rd.Adjust = s.AdjustDeg;
        float crossed = CrossedZ(s, w.Pitch, tuning);
        rd.Crossed = crossed; rd.AimedZ = s.Aimed.X;
        if (list) rd.ListLine = string.Create(CultureInfo.InvariantCulture,
            $"  seed {seed}: {s.Type} {s.Foot}/{s.Surface} rule {w.StrikeGeometry.Rule} from {s.Distance:F1} m aimed z {s.Aimed.X:F2} adjust {s.AdjustDeg:F2} deg yaw error {s.YawErrorDeg:F2} deg crossed z {crossed:F2} drift {crossed - s.Aimed.X:F2} m");
        if (pressTick >= 0) rd.PressToStrike = (s.Tick - pressTick) * tuning.Dt;
        rd.How = (s.Foot == att.PreferredFoot ? "strong " : "weak ") + s.Surface.ToString().ToLowerInvariant();
        // The decision of the release still stands at the strike. A foot that differs from it was handed over: by the
        // dead-ahead swap when Ahead decided, otherwise because the chosen foot had no contact.
        var decision = w.StrikeGeometry;
        rd.Rule = decision.Rule;
        rd.HandedOver = s.Foot != decision.Choice.Foot;
        rd.End = RoundRunner.RunFirstShot(tuning, sc, att, kee, seed, Script()).End;
        // Aimed outside the posts already, or aimed inside and sent wide by the error.
        rd.AimedOut = MathF.Abs(s.Aimed.X) > w.Pitch.GoalHalfWidth;
        return rd;
    }

    /// <summary>Where a shot as struck crosses the goal line (Z), flying over the ground alone (no frame, boards or keeper); NaN if it never does.</summary>
    static float CrossedZ(ShotResult s, Pitch pitch, Tuning t)
    {
        var b = new Ball(t.Ball) { Position = s.Origin, Velocity = s.Launch, Spin = s.Spin };
        for (int i = 0; i < 6 * t.TickRate; i++)
        {
            var before = b.Position;
            b.Step(t.Dt, null, null);
            if (b.Position.X >= pitch.GoalLineX && b.Position.X > before.X)
                return before.Z + (b.Position.Z - before.Z) * (pitch.GoalLineX - before.X) / (b.Position.X - before.X);
        }
        return float.NaN;
    }
}
