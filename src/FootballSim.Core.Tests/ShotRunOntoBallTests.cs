using System.Numerics;
using FootballSim.Core;
using Xunit.Abstractions;

namespace FootballSim.Core.Tests;

/// <summary>
/// From the shoot press to the strike the left stick only aims: the legs run on to the ball, so a shot pressed in a dribble at
/// an angle to the aim still meets the ball (2026-11-19 play-test, rounds 1 and 2 of the second session, where the legs
/// followed the aim stick and the ball rolled on past the striking foot until the shot was dropped).
/// </summary>
public class ShotRunOntoBallTests
{
    readonly ITestOutputHelper _out;

    public ShotRunOntoBallTests(ITestOutputHelper output) { _out = output; }

    static World Lab(ulong seed, int dribbling)
    {
        var s = TestData.Sheet(75);
        s.Dribbling = dribbling;
        var sc = new Scenario
        {
            Id = "lab",
            Pitch = new PitchSpec { Length = 300f, Width = 200f },
            Ball = new StartSpot { X = 2.5f },
            Attacker = new StartSpot { X = 2.0f },
        };
        var w = new World(new Tuning(), sc, s, TestData.Sheet(50), seed);
        w.Begin();
        return w;
    }

    static TickIntent Dir(float deg, bool shoot = false)
    {
        float a = deg * MathF.PI / 180f;
        return new TickIntent { MoveX = MathF.Cos(a), MoveZ = MathF.Sin(a), ShootHeld = shoot };
    }

    static void Step(World w, TickIntent it)
    {
        var buf = new TickIntent[w.PlayerCount];
        buf[0] = it;
        w.Step(buf);
    }

    /// <summary>
    /// A jog dribble along <paramref name="deg"/>, shoot pressed, the stick swung to 0 degrees (the aim) three ticks later,
    /// shoot let go after 0.3 s. Returns the distance from the ball to the striking foot's contact point at the strike, or
    /// NaN when no shot was struck by the time the shot was dropped (or 2 s after the release).
    /// </summary>
    static float RunOne(float deg, ulong seed, int dribbling)
    {
        var w = Lab(seed, dribbling);
        for (int i = 0; i < 360; i++) Step(w, Dir(deg));
        int hold = (int)MathF.Round(0.3f * w.Tuning.TickRate);
        for (int i = 0; i < hold + 240; i++)
        {
            Step(w, Dir(i < 3 ? deg : 0f, shoot: i < hold));
            if (w.Shots.Count > 0)
            {
                var p = w.Attacker;
                var shot = p.ShotAction!;
                var foot = TouchPlanner.ContactPointFor(p, shot.Foot, w.Tuning);
                return new Vector2(shot.ContactPoint.X - foot.X, shot.ContactPoint.Z - foot.Z).Length();
            }
            if (i > hold && w.State == RoundState.Live && w.Attacker.ShotAction == null) break;
        }
        return float.NaN;
    }

    [Fact]
    public void AShotPressedInADribbleAtAnAngleToTheAimIsStruck()
    {
        var all = new List<float>();
        int runs = 0, struck = 0;
        foreach (float deg in new[] { 20f, 45f, 70f, 90f })
        {
            var d = new List<float>();
            int n = 0, ok = 0;
            foreach (int rating in new[] { 1, 50, 99 })
                for (ulong seed = 1; seed <= 50; seed++)
                {
                    n++;
                    float m = RunOne(deg, seed, rating);
                    if (!float.IsNaN(m)) { ok++; d.Add(m); }
                }
            d.Sort();
            float med = d.Count > 0 ? d[d.Count / 2] : float.NaN, max = d.Count > 0 ? d[^1] : float.NaN;
            _out.WriteLine($"dribble at {deg,2:F0} deg, aim 0: struck {ok}/{n} ({100f * ok / n:F1}%), ball to foot at contact median {med:F3} m max {max:F3} m");
            runs += n; struck += ok; all.AddRange(d);
        }
        all.Sort();
        _out.WriteLine($"all: struck {struck}/{runs}, median {(all.Count > 0 ? all[all.Count / 2] : float.NaN):F3} m, max {(all.Count > 0 ? all[^1] : float.NaN):F3} m");
        Assert.Equal(runs, struck);
    }

    /// <summary>
    /// Rounds 1 and 2 of the second 2026-11-19 play-test session (a shot pressed in a dribble at 45 degrees and at -90 degrees to
    /// the aim) strike a shot, and the first one is not dropped at its deadline. Skipped when the rounds are not in the tree.
    /// </summary>
    [Fact]
    public void ThePlayTestRoundsWhereTheShotWasDroppedNowStrike()
    {
        string dir = Path.Combine(TestData.DataDir, "..", "src", "FootballSim.Core.Tests", "TestData", "playtest");
        foreach (var name in new[] { "round-101-penalty-held.json", "round-102-penalty-held.json" })
        {
            string f = Path.Combine(dir, name);
            if (!File.Exists(f)) { _out.WriteLine($"{f} missing; skipped"); continue; }
            var rp = Replay.Load(f, out var err);
            Assert.True(rp != null, err);
            var t = new Tuning();
            var w = new World(t, TestData.Scenario(rp!.ScenarioId), TestData.Player(rp.AttackerId), TestData.Player(rp.KeeperId), rp.Seed);
            w.Begin();
            var buf = new TickIntent[w.PlayerCount];
            int per = Math.Max(1, rp.PlayersPerTick);
            int dropped = 0;
            bool pending = false;
            for (int tick = 0; tick < rp.TickCount && w.State != RoundState.Report; tick++)
            {
                Array.Clear(buf);
                for (int i = 0; i < w.PlayerCount && i < per; i++) buf[i] = rp.Intents[tick * per + i].ToIntent();
                bool wasStrike = w.State == RoundState.Strike;
                int shots = w.Shots.Count;
                w.Step(buf);
                // A shot that was waiting for its strike and is gone with no shot made was dropped. Only the shots before the
                // first strike count: a press made while the first shot's ball is already flying has nothing to strike.
                if (wasStrike && w.State == RoundState.Live && shots == 0 && w.Shots.Count == 0) dropped++;
                pending |= w.State == RoundState.Strike;
            }
            _out.WriteLine($"{name}: {w.Shots.Count} shots, {dropped} dropped");
            Assert.True(pending, $"{name}: no shot was released");
            Assert.NotEmpty(w.Shots);
            Assert.Equal(0, dropped);
        }
    }
}
