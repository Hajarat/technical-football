using System.Globalization;
using System.Numerics;
using FootballSim.Core;

/// <summary>
/// Diagnostic for the scoring calibration (<c>--probe shots --scenario &lt;id&gt; [--n N --seed S]</c>): plays the scripted rounds
/// like the batch run and, per round, reads the state at the strike (distance to the goal, the attacker's speed, the charge
/// against the ideal, the ball's offset from the striking foot's contact point and from the feet, the shot's accuracy factors)
/// and how the round ended, then prints means and shares, the reasons wide shots went wide, and why rounds had no shot.
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
        var touchesBefore = new List<float>(); var timeToShot = new List<float>();
        var factors = new Dictionary<string, List<float>>();
        var ends = new Dictionary<string, int>();
        var noShot = new Dictionary<string, int>();
        int wide = 0, wideAimOut = 0, wideError = 0;
        for (int r = 0; r < n; r++)
        {
            var w = new World(tuning, sc, att, kee, seed0 + (ulong)r);
            var script = new ScriptedAttacker();
            w.Begin();
            var buf = new TickIntent[w.PlayerCount];
            int cap = (int)((tuning.Round.MaxSeconds + 10f) * tuning.TickRate);
            var p = w.Attacker;
            Vector3 ballPrev = default, posPrev = default; float facePrev = 0f, speedPrev = 0f; Foot footPrev = Foot.Right;
            int touches = 0;
            bool shot = false;
            for (int i = 0; i < cap; i++)
            {
                ballPrev = w.Ball.Position; posPrev = p.Position; facePrev = p.Facing; speedPrev = p.Speed; footPrev = p.StrikeFoot;
                buf[0] = script.Next(w);
                w.Step(buf);
                touches += w.Events.Count(e => e.Kind == EventKind.Touch && e.Team == 0);
                if (w.Shots.Count > 0) { shot = true; break; }
                if (w.State is RoundState.Outcome or RoundState.Report) break;
            }
            if (!shot)
            {
                string why = w.KeeperReport.WonBall ? "keeper won the ball" : w.BallController == p ? "in possession, no shot (time)" : "ball loose / lost";
                noShot[why] = noShot.GetValueOrDefault(why) + 1;
                continue;
            }
            var s = w.Shots[0];
            // The ball at the strike tick, before it was struck, from the feet and from the striking foot's contact point.
            var rel = new Vector2(ballPrev.X - posPrev.X, ballPrev.Z - posPrev.Z);
            var fwd = new Vector2(MathF.Cos(facePrev), MathF.Sin(facePrev));
            var cp = TouchPlanner.ContactPointFor(p, s.Foot, tuning);
            dist.Add(s.Distance); speed.Add(speedPrev); charge.Add(s.Charge); chargeMiss.Add(s.ChargeMissDeg); sigmaH.Add(s.SigmaH);
            offFeet.Add(rel.Length()); ahead.Add(Vector2.Dot(rel, fwd));
            offFoot.Add(new Vector2(ballPrev.X - cp.X, ballPrev.Z - cp.Z).Length());
            touchesBefore.Add(touches); timeToShot.Add(w.SimTime);
            foreach (var f in s.Factors) { if (!factors.TryGetValue(f.Name, out var l)) factors[f.Name] = l = new(); l.Add(f.Value); }
            var res = RoundRunner.RunFirstShot(tuning, sc, att, kee, seed0 + (ulong)r);
            ends[res.End.ToString()] = ends.GetValueOrDefault(res.End.ToString()) + 1;
            if (res.End == ShotEnd.Wide)
            {
                wide++;
                // Aimed outside the posts already, or aimed inside and sent wide by the error.
                if (MathF.Abs(s.Aimed.X) > w.Pitch.GoalHalfWidth) wideAimOut++; else wideError++;
            }
        }
        string F(List<float> l) => l.Count == 0 ? "-" : $"{l.Average():F2} (p10 {Q(l, 0.1):F2}, p90 {Q(l, 0.9):F2})";
        float Q(List<float> l, double q) { var c = l.OrderBy(x => x).ToList(); return c[Math.Min(c.Count - 1, (int)(q * c.Count))]; }
        Console.WriteLine($"shot diagnostic: scenario {id}, {n} rounds from seed {seed0}, {dist.Count} with a shot");
        Console.WriteLine($"  ends: {string.Join(", ", ends.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {100.0 * kv.Value / n:F1}%"))}");
        Console.WriteLine($"  no shot: {(noShot.Count == 0 ? "none" : string.Join(", ", noShot.Select(kv => $"{kv.Key} {kv.Value}")))}");
        Console.WriteLine($"  distance to goal at strike m: {F(dist)}");
        Console.WriteLine($"  attacker speed at strike m/s: {F(speed)}");
        Console.WriteLine($"  charge: {F(charge)}   charge miss deg: {F(chargeMiss)}   sigma_h deg: {F(sigmaH)}");
        Console.WriteLine($"  ball from the feet m: {F(offFeet)}   ahead along the facing m: {F(ahead)}   from the striking foot's contact point m: {F(offFoot)}");
        Console.WriteLine($"  touches before the shot: {F(touchesBefore)}   seconds to the shot: {F(timeToShot)}");
        foreach (var kv in factors.OrderBy(kv => kv.Key)) Console.WriteLine($"  factor {kv.Key}: {F(kv.Value)}");
        Console.WriteLine($"  wide {wide}: aimed outside the posts {wideAimOut}, aimed inside and sent wide {wideError}");
        return 0;
    }
}
