using System.Numerics;
using FootballSim.Core;
using Xunit.Abstractions;

namespace FootballSim.Core.Tests;

/// <summary>The sprint push near the keeper: cut short of the first tick he could reach the attacker's predicted path.</summary>
public class SprintPushDangerTests
{
    readonly ITestOutputHelper _out;
    public SprintPushDangerTests(ITestOutputHelper output) => _out = output;

    /// <summary>A tuning whose scan never starts (no push has that many steps): today's pushes.</summary>
    static Tuning NoScan()
    {
        var t = new Tuning();
        t.Touch.DangerMinSteps = new RatingRange(99f, 99f);
        return t;
    }

    /// <summary>A long pitch, the attacker at its middle; the goal and the keeper are 150 m away along +X.</summary>
    static World Lab(ulong seed, int sprintDribbling, Tuning? t = null, int rushingOut = 50)
    {
        var s = TestData.Sheet(75);
        s.SprintDribbling = sprintDribbling;
        var keeper = TestData.Sheet(50);
        keeper.RushingOut = rushingOut;
        var sc = new Scenario
        {
            Id = "lab",
            Pitch = new PitchSpec { Length = 300f, Width = 200f },
            Ball = new StartSpot { X = 150.5f },
            Attacker = new StartSpot { X = 150f },
        };
        var w = new World(t ?? new Tuning(), sc, s, keeper, seed);
        w.Begin();
        return w;
    }

    static TickIntent Stick(float deg, float sprint = 1f)
    {
        float a = deg * MathF.PI / 180f;
        return new TickIntent { MoveX = MathF.Cos(a), MoveZ = MathF.Sin(a), Sprint = sprint };
    }

    static void Step(World w, TickIntent it)
    {
        var buf = new TickIntent[w.PlayerCount];
        buf[0] = it;
        w.Step(buf);
    }

    /// <summary>The touch made this tick, if one was.</summary>
    static BallAction? Touch(World w) =>
        w.Events.Any(e => e.Kind == EventKind.Touch && e.Team == 0) && w.Attacker.TouchAction is { Made: true } a && a.ContactTick == w.Tick ? a : null;

    static PlayerState Opponent(float x, float z, PlayerRole role = PlayerRole.Keeper, int rating = 50)
    {
        var s = TestData.Sheet(rating);
        return new PlayerState(1, 0, role, s) { Position = new Vector3(x, 0f, z) };
    }

    // ---- the reach test ----

    [Fact]
    public void AKeeperBehindOrLevelNeverCounts()
    {
        var t = new Tuning();
        Vector3 start = new(10f, 0f, 5f), at = new(16f, 0f, 5f);
        // Behind him, level with him to either side, and right on him: never, however long the push has been out.
        foreach (var (x, z) in new[] { (8f, 5f), (4f, 6f), (10f, 5.5f), (10f, 4f), (10f, 5f), (10.05f, 5.8f) })
            Assert.False(TouchPlanner.Endangered(start, at, 2f, new[] { Opponent(x, z) }, t), $"({x}, {z})");
        // Just ahead of him he does.
        Assert.True(TouchPlanner.Endangered(start, at, 0.1f, new[] { Opponent(11f, 5.5f) }, t));
    }

    [Fact]
    public void TheReachIsTheKeepersRushSpeedTimesTheTimePlusTheRadius()
    {
        var t = new Tuning();
        Vector3 start = new(0f, 0f, 0f), at = new(4f, 0f, 0f);
        foreach (int rushing in new[] { 1, 50, 99 })
        {
            var s = TestData.Sheet(50);
            s.RushingOut = rushing;
            s.SprintSpeed = 99;   // (a keeper is read by his rush speed, not his sprint speed)
            float rush = Keeper.RushSpeed(s, t);
            foreach (float elapsed in new[] { 0.1f, 0.4f, 0.8f })
            {
                float reach = rush * elapsed + t.Touch.DangerRadius;
                // Ahead on his line: the closest point is the path's end, 4 m on.
                var inside = new PlayerState(1, 0, PlayerRole.Keeper, s) { Position = new Vector3(4f + reach - 0.02f, 0f, 0f) };
                var outside = new PlayerState(1, 0, PlayerRole.Keeper, s) { Position = new Vector3(4f + reach + 0.02f, 0f, 0f) };
                Assert.True(TouchPlanner.Endangered(start, at, elapsed, new[] { inside }, t), $"rushing {rushing} after {elapsed} s");
                Assert.False(TouchPlanner.Endangered(start, at, elapsed, new[] { outside }, t), $"rushing {rushing} after {elapsed} s");
            }
        }
        // An outfield opponent closes at his top speed.
        var runner = Opponent(0f, 0f, PlayerRole.Outfield, 80);
        Assert.Equal(Movement.TopSpeed(runner.Sheet, t), TouchPlanner.DangerSpeedOf(runner, t));
        Assert.Equal(Keeper.RushSpeed(runner.Sheet, t), TouchPlanner.DangerSpeedOf(Opponent(0f, 0f, PlayerRole.Keeper, 80), t));
    }

    [Fact]
    public void AKeeperBesideThePathCountsByItsClosestPointNotItsEnd()
    {
        // The keeper stands 10 m on, 1.2 m off the line. A path that has run 20 m passes him at 1.2 m: within his reach almost
        // at once, though its end is 10 m from him. A path that stops 5 m short of him is out of reach.
        var t = new Tuning();
        var keeper = new[] { Opponent(10f, 1.2f) };
        float rush = Keeper.RushSpeed(keeper[0].Sheet, t);
        float needs = (1.2f - t.Touch.DangerRadius) / rush;
        Assert.True(TouchPlanner.Endangered(Vector3.Zero, new Vector3(20f, 0f, 0f), needs + 0.01f, keeper, t));
        Assert.False(TouchPlanner.Endangered(Vector3.Zero, new Vector3(20f, 0f, 0f), needs - 0.01f, keeper, t));
        Assert.False(TouchPlanner.Endangered(Vector3.Zero, new Vector3(5f, 0f, 0f), needs + 0.01f, keeper, t));
        // Any one opponent is enough.
        Assert.True(TouchPlanner.Endangered(Vector3.Zero, new Vector3(20f, 0f, 0f), needs + 0.01f, new[] { Opponent(-5f, 0f), keeper[0] }, t));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(50, 2)]
    [InlineData(51, 1)]
    [InlineData(99, 1)]
    public void TheFewestStepsFollowSprintDribbling(int rating, int steps)
    {
        var s = TestData.Sheet(50);
        s.SprintDribbling = rating;
        s.CloseDribbling = 100 - rating;
        Assert.Equal(steps, TouchPlanner.DangerMinSteps(s, new Tuning()));
    }

    // ---- the walk ----

    /// <summary>A sprinter in full stride with the ball, just after a touch; the prediction walks from him.</summary>
    static World Sprinting(int sprintDribbling, Tuning t)
    {
        var w = Lab(1, sprintDribbling, t);
        for (int i = 0; i < 3 * 120; i++) Step(w, Stick(0f));
        for (int i = 0; i < 120 && Touch(w) == null; i++) Step(w, Stick(0f));
        Assert.True(w.Attacker.InPossession);
        return w;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TheWalkStopsAtTheLastStepBeforeTheDangerTickAndNeverUnderTheMinimum(int minSteps)
    {
        // A keeper who does not move (rush speed 0) on the line ahead: the danger tick is the one his predicted run comes
        // within the radius of him, so it moves out with his distance.
        var t = new Tuning();
        t.Keeper.RushSpeed = new RatingRange(0f, 0f);
        var w = Sprinting(60, t);
        var p = w.Attacker;
        var user = Stick(0f);
        const int Full = 4;
        var free = new TouchPlanner.Meet[Full + 1];
        for (int n = 1; n <= Full; n++)
        {
            Assert.True(TouchPlanner.FindMeet(p, user, n, 0, w.SimTime, t, w.Pitch, out free[n]));
            Assert.Equal(n, free[n].Swings);
            Assert.False(free[n].Danger);
        }
        int last = 0;
        var seen = new HashSet<int>();
        for (float ahead = 0.5f; ahead <= 14f; ahead += 0.1f)
        {
            var keeper = new[] { Opponent(p.Position.X + ahead, p.Position.Z) };
            Assert.True(TouchPlanner.FindMeet(p, user, Full, 0, w.SimTime, t, w.Pitch, out var m, 0, keeper, minSteps));
            seen.Add(m.Swings);
            Assert.InRange(m.Swings, minSteps, Full);
            // The step chosen is exactly the plain push of that many steps.
            Assert.Equal(free[m.Swings].J, m.J);
            Assert.Equal(free[m.Swings].Foot, m.Foot);
            Assert.Equal(free[m.Swings].Cp, m.Cp);
            Assert.Equal(m.Swings < Full, m.Danger);
            Assert.True(m.Swings >= last, $"keeper {ahead:F1} m ahead: {m.Swings} steps after {last}");
            last = m.Swings;
            if (m.Danger && m.Swings > minSteps)
            {
                // Cut to a step before the danger tick: where he is predicted at its swing middle the keeper is still out of
                // reach, and (unless the foot was kept by going one step sooner) a step later he would not be.
                float run = free[m.Swings].Cp.X - p.Position.X, next = free[Math.Min(Full, m.Swings + 2)].Cp.X - p.Position.X;
                Assert.True(ahead - run > t.Touch.DangerRadius - 0.6f, $"keeper {ahead:F1} m ahead: cut to {m.Swings} steps, {run:F2} m on");
                Assert.True(ahead - next < t.Touch.DangerRadius + 0.6f, $"keeper {ahead:F1} m ahead: cut to {m.Swings} steps, two more are {next:F2} m on");
                // The foot the full push would have played, unless that step is under the minimum.
                Assert.True((Full - m.Swings) % 2 == 0 || m.Swings - 1 < minSteps, $"keeper {ahead:F1} m ahead: {m.Swings} steps changes the foot");
            }
        }
        _out.WriteLine($"min {minSteps}: steps seen {string.Join(",", seen.OrderBy(x => x))}");
        Assert.Contains(minSteps, seen);
        Assert.Contains(Full, seen);
        Assert.Equal(Full, last);   // far enough ahead: the whole push
    }

    // ---- in the world ----

    /// <summary>Runs a sprint along <paramref name="deg"/> with the keeper held <paramref name="ahead"/> metres on along +X each tick (null: left alone).</summary>
    static List<(int Tick, ActionKind Kind, int Steps, float Cut, float Slow, Vector3 Ball)> Run(World w, float deg, float? ahead, int ticks, float sprint = 1f, bool zigZag = false)
    {
        var log = new List<(int, ActionKind, int, float, float, Vector3)>();
        float side = 1f;
        for (int i = 0; i < ticks; i++)
        {
            if (ahead is float d) w.Keeper.Position = w.Attacker.Position + new Vector3(d, 0f, 0f);
            Step(w, Stick(zigZag ? 45f * side : deg, sprint));
            if (Touch(w) is not { } a) continue;
            log.Add((w.Tick, a.Kind, w.Attacker.PushSteps, a.DangerCut, Possession.DangerSlow(w.Attacker), w.Ball.Velocity));
            if (zigZag && a.Kind is ActionKind.Turn or ActionKind.Cut) side = -side;
        }
        return log;
    }

    [Theory]
    [InlineData(0f)]     // toward a keeper 150 m away
    [InlineData(180f)]   // away from him: he is behind
    public void AKeeperFarAwayOrBehindGivesExactlyTodaysPush(float deg)
    {
        foreach (ulong seed in new ulong[] { 1, 2, 3 })
        {
            var a = Run(Lab(seed, 60), deg, null, 6 * 120);
            var b = Run(Lab(seed, 60, NoScan()), deg, null, 6 * 120);
            Assert.True(a.Count > 4, $"only {a.Count} touches");
            Assert.Equal(b, a);
            Assert.All(a, x => { Assert.Equal(0f, x.Cut); Assert.Equal(1f, x.Slow); });
        }
        // Behind and close: still today's push.
        var near = Run(Lab(1, 60), 0f, -3f, 6 * 120);
        var plain = Run(Lab(1, 60, NoScan()), 0f, -3f, 6 * 120);
        Assert.Equal(plain, near);
    }

    [Theory]
    [InlineData(99, 5f)]
    [InlineData(99, 8f)]
    [InlineData(60, 6f)]
    [InlineData(30, 6f)]
    [InlineData(1, 6f)]
    public void AKeeperAheadCutsSprintPushesByTheFormulas(int rating, float ahead)
    {
        var t = new Tuning();
        var w = Lab(1, rating, t);
        var free = Run(Lab(1, rating, NoScan()), 0f, ahead, 6 * 120);
        var log = Run(w, 0f, ahead, 6 * 120);
        int min = TouchPlanner.DangerMinSteps(w.Attacker.Sheet, t), spt = TouchModel.StepsPerTouch(w.Attacker.Sheet, TouchMode.Sprint, t);
        var sprint = log.Where(x => x.Kind == ActionKind.Sprint && x.Tick > 240).ToList();
        int cutCount = 0;
        foreach (var x in sprint)
        {
            Assert.InRange(x.Steps, 1, spt + 1);
            if (x.Cut <= 0f) { Assert.Equal(1f, x.Slow); continue; }
            cutCount++;
            Assert.True(x.Steps >= min, $"tick {x.Tick}: {x.Steps} steps under the minimum {min}");
            // The share cut, for a full push of steps_per_touch or (the foot-switch push) one more.
            float a = (spt - x.Steps) / (float)Math.Max(1, spt - min), b = (spt + 1 - x.Steps) / (float)Math.Max(1, spt + 1 - min);
            Assert.True(MathF.Abs(x.Cut - a) < 1e-4f || MathF.Abs(x.Cut - b) < 1e-4f, $"tick {x.Tick}: cut {x.Cut} with {x.Steps} of {spt} steps (min {min})");
            Assert.Equal(t.Touch.DangerSpeed.Evaluate(x.Cut), x.Slow, 5);
            Assert.InRange(x.Slow, 0.9f, 1f);
        }
        double mean = sprint.Count == 0 ? 0 : sprint.Average(x => x.Steps);
        var freeSprint = free.Where(x => x.Kind == ActionKind.Sprint && x.Tick > 240).ToList();
        _out.WriteLine($"sprint dribbling {rating}, keeper {ahead} m ahead: {cutCount} of {sprint.Count} sprint pushes cut, mean {mean:F2} steps (min {min}, plain {freeSprint.Average(x => x.Steps):F2})");
        if (spt > min)
        {
            Assert.True(cutCount > 0, "no push was cut");
            Assert.True(mean < freeSprint.Average(x => x.Steps), "the pushes are no shorter");
        }
        else Assert.Equal(0, cutCount);   // nothing to cut: the cadence is the minimum already
        Assert.True(w.Attacker.InPossession);
    }

    [Fact]
    public void ACutPushSlowsHimThroughThatPushOnly()
    {
        var t = new Tuning();
        var w = Lab(1, 99, t);
        Run(w, 0f, null, 3 * 120);
        var p = w.Attacker;
        float cap = Movement.TopSpeedWithBall(p.Sheet, 1f, t);
        // The keeper comes close ahead: the next sprint push is cut and the cap drops by its factor ...
        var cutLog = Run(w, 0f, 5f, 120);
        Assert.Contains(cutLog, x => x.Cut > 0f);
        float slow = Possession.DangerSlow(p);
        Assert.InRange(slow, 0.9f, 0.999f);
        Assert.Equal(cap * slow, Possession.SpeedCap(p, true, false, Stick(0f), t), 4);
        Assert.True(p.Speed <= cap * 1.001f);
        // ... and with him gone again, the first full push ends it.
        w.Keeper.Position = p.Position + new Vector3(150f, 0f, 0f);
        var after = Run(w, 0f, null, 240);
        Assert.Contains(after, x => x.Kind == ActionKind.Sprint && x.Cut == 0f);
        Assert.Equal(1f, Possession.DangerSlow(p));
        Assert.Equal(cap, Possession.SpeedCap(p, true, false, Stick(0f), t), 4);
    }

    [Fact]
    public void TheScanLeavesJogsCloseControlAndTurnsAlone()
    {
        // The keeper held 4 m ahead, where every sprint push is in danger: a jog, close control and a sprinting zig-zag's
        // turns and cuts are played exactly as without the scan.
        foreach (float sprint in new[] { 0f })
        {
            var a = Run(Lab(1, 60), 0f, 4f, 5 * 120, sprint);
            var b = Run(Lab(1, 60, NoScan()), 0f, 4f, 5 * 120, sprint);
            Assert.True(a.Count > 5);
            Assert.Equal(b, a);
        }
        var cc = Lab(1, 60); var ccFree = Lab(1, 60, NoScan());
        foreach (var w in new[] { cc, ccFree })
            for (int i = 0; i < 5 * 120; i++)
            {
                w.Keeper.Position = w.Attacker.Position + new Vector3(4f, 0f, 0f);
                Step(w, new TickIntent { MoveX = 1f, CloseControl = true });
            }
        Assert.Equal(ccFree.Attacker.Position, cc.Attacker.Position);
        Assert.Equal(ccFree.Ball.Position, cc.Ball.Position);

        var zig = Run(Lab(1, 60), 0f, 4f, 6 * 120, 1f, zigZag: true);
        Assert.Contains(zig, x => x.Kind is ActionKind.Turn or ActionKind.Cut);
        Assert.All(zig.Where(x => x.Kind != ActionKind.Sprint), x => Assert.Equal(0f, x.Cut));
    }

    [Fact]
    public void TheSameRoundTwiceGivesTheSameResult()
    {
        foreach (var scenario in new[] { "breakaway", "keeper_rush", "halfway_run" })
            foreach (ulong seed in new ulong[] { 1, 7 })
            {
                var a = RoundRunner.RunFirstShot(new Tuning(), TestData.Scenario(scenario), TestData.Player("striker_a"), TestData.Player("the_wall"), seed);
                var b = RoundRunner.RunFirstShot(new Tuning(), TestData.Scenario(scenario), TestData.Player("striker_a"), TestData.Player("the_wall"), seed);
                Assert.Equal(a.End, b.End);
                Assert.Equal(a.ToString(), b.ToString());
            }
    }
}
