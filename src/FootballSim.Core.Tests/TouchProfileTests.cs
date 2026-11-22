using System.Numerics;
using FootballSim.Core;
using Xunit.Abstractions;

namespace FootballSim.Core.Tests;

/// <summary>The per-kind touch profiles: lookup, loading and validation.</summary>
public class TouchProfileTests
{
    static readonly Tuning T = new();
    readonly ITestOutputHelper _out;

    public TouchProfileTests(ITestOutputHelper output) { _out = output; }

    [Fact]
    public void ProfileFollowsTheKindThenTheMode()
    {
        var k = T.Touch.Profiles;
        Assert.Same(k.KnockOn, TouchModel.Profile(ActionKind.KnockOn, TouchMode.Jog, T));
        Assert.Same(k.Heavy, TouchModel.Profile(ActionKind.Heavy, TouchMode.Sprint, T));
        Assert.Same(k.Close, TouchModel.Profile(ActionKind.Close, TouchMode.CloseControl, T));
        Assert.Same(k.Jog, TouchModel.Profile(ActionKind.Turn, TouchMode.Jog, T));
        Assert.Same(k.Sprint, TouchModel.Profile(ActionKind.Cut, TouchMode.Sprint, T));
        Assert.Same(k.Sprint, TouchModel.Profile(ActionKind.First, TouchMode.Sprint, T));
        Assert.Same(k.TrapLow, TouchModel.Profile(ActionKind.TrapLow, TouchMode.Sprint, T));
        Assert.Same(k.TrapMid, TouchModel.Profile(ActionKind.TrapMid, TouchMode.Jog, T));
        Assert.Same(k.TrapHigh, TouchModel.Profile(ActionKind.TrapHigh, TouchMode.CloseControl, T));
    }

    [Fact]
    public void TrapKindsAreTouchesAndLeaveTheOtherKindsInTheirRanges()
    {
        // The traps sit with the touches, before the skill moves: the shots stay contiguous from Power on (probes take
        // Kind >= Power as a shot), the skill-move ranges keep their members, and nothing is stored by number.
        foreach (var k in new[] { ActionKind.TrapLow, ActionKind.TrapMid, ActionKind.TrapHigh })
        {
            Assert.True(k > ActionKind.First && k < ActionKind.StepOver && k < ActionKind.Power);
            var a = new BallAction { Kind = k };
            Assert.False(a.IsShot);
            Assert.False(a.IsSkill);
        }
        Assert.Equal(ActionKind.LowFinesse - ActionKind.Power, 4);
        Assert.Equal(ActionKind.FakeCut - ActionKind.StepOver, 5);
    }

    [Fact]
    public void TrapBandsRiseAndATrapTakesAHigherBallThanAPush()
    {
        var k = T.Touch.Profiles;
        Assert.True(k.TrapLow.MaxBallHeight < k.TrapMid.MaxBallHeight && k.TrapMid.MaxBallHeight < k.TrapHigh.MaxBallHeight);
        Assert.True(k.TrapMid.MaxBallHeight > k.Jog.MaxBallHeight);
        Assert.Equal(k.TrapLow.MaxBallHeight, TouchModel.FeetHeight(T));
        // a higher band takes the ball closer to the body and kills it deader
        Assert.True(k.TrapHigh.Reach < k.TrapMid.Reach && k.TrapMid.Reach <= k.TrapLow.Reach);
        Assert.True(k.TrapHigh.Kept < k.TrapMid.Kept && k.TrapMid.Kept < k.TrapLow.Kept);
        var bad = DataLoader.ParseTuning("""{ "touch": { "profiles": { "trap_mid": { "max_ball_height": 2.0 } } } }""", "t.json");
        Assert.NotNull(bad.Error);
        Assert.Contains("trap bands", bad.Error);
        var badKept = DataLoader.ParseTuning("""{ "touch": { "profiles": { "trap_high": { "kept": -1, "max_ball_height": 1.6 } } } }""", "t.json");
        Assert.NotNull(badKept.Error);
        Assert.Contains("touch.profiles.trap_high: kept", badKept.Error);
    }

    [Fact]
    public void SprintPushesAreFlatterAndLongerThanJogNudges()
    {
        var k = T.Touch.Profiles;
        // as steep at dribbling 1, flatter above it
        Assert.True(k.Sprint.LaunchAngle.At1 <= k.Jog.LaunchAngle.At1);
        foreach (int r in new[] { 30, 50, 99 })
            Assert.True(k.Sprint.LaunchAngle.ForRating(r) < k.Jog.LaunchAngle.ForRating(r), $"dribbling {r}");
        Assert.True(k.Sprint.PeakHeightCap < k.Jog.PeakHeightCap);
        Assert.True(k.Sprint.MinPush >= k.Jog.MinPush);
        Assert.True(k.Sprint.MaxPush > k.Jog.MaxPush);
        Assert.True(k.Close.MinPush < k.Jog.MinPush);
        Assert.True(k.KnockOn.MaxPush > k.Sprint.MaxPush);
    }

    /// <summary>A long empty pitch with the ball 0.5 m in front of a standing attacker.</summary>
    static World Lab(int dribbling, ulong seed)
    {
        var sc = new Scenario
        {
            Id = "lab",
            Pitch = new PitchSpec { Length = 150f },
            Ball = new StartSpot { X = 2.5f },
            Attacker = new StartSpot { X = 2.0f },
        };
        var s = TestData.Sheet(60);
        s.CloseDribbling = s.SprintDribbling = dribbling;
        var w = new World(new Tuning(), sc, s, TestData.Sheet(50), seed);
        w.Begin();
        return w;
    }

    /// <summary>
    /// Dribbles straight for 6 s and returns, over the touches of <paramref name="kind"/> made from 1 s on, the mean and the
    /// largest height the ball centre reaches before the next touch, and the mean launch speed.
    /// </summary>
    static (float MeanPeak, float MaxPeak, float MeanSpeed, int N) Peaks(TickIntent it, ActionKind kind, int dribbling)
    {
        float sum = 0f, max = 0f, speed = 0f; int n = 0;
        for (ulong seed = 1; seed <= 6; seed++)
        {
            var w = Lab(dribbling, seed);
            var buf = new TickIntent[w.PlayerCount];
            buf[0] = it;
            float peak = float.NaN;
            for (int i = 0; i < 6 * 120 && w.State == RoundState.Live; i++)
            {
                w.Step(buf);
                var a = w.Attacker.TouchAction;
                bool touched = w.Events.Any(e => e.Kind == EventKind.Touch && e.Team == 0);
                if (touched)
                {
                    if (!float.IsNaN(peak)) { sum += peak; max = MathF.Max(max, peak); n++; }
                    peak = float.NaN;
                    if (i >= 120 && a is { Made: true } && a.Kind == kind)
                    {
                        peak = w.Ball.Position.Y;
                        speed += a.BallOutVelocity.Length();
                    }
                }
                else if (!float.IsNaN(peak)) peak = MathF.Max(peak, w.Ball.Position.Y);
            }
        }
        return (n > 0 ? sum / n : float.NaN, max, n > 0 ? speed / n : float.NaN, n);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(90)]
    public void SprintAndJogPushesFlyDifferently(int dribbling)
    {
        var jog = Peaks(new TickIntent { MoveX = 1 }, ActionKind.Jog, dribbling);
        var sprint = Peaks(new TickIntent { MoveX = 1, Sprint = 1 }, ActionKind.Sprint, dribbling);
        _out.WriteLine($"dribbling {dribbling}: jog peak mean {jog.MeanPeak:F3} max {jog.MaxPeak:F3} speed {jog.MeanSpeed:F2} (n {jog.N}); sprint peak mean {sprint.MeanPeak:F3} max {sprint.MaxPeak:F3} speed {sprint.MeanSpeed:F2} (n {sprint.N})");
        Assert.True(jog.N >= 20 && sprint.N >= 20, $"touches: jog {jog.N}, sprint {sprint.N}");
        Assert.True(MathF.Abs(sprint.MeanPeak - jog.MeanPeak) > 0.001f, $"mean peak: sprint {sprint.MeanPeak:F4} m, jog {jog.MeanPeak:F4} m");
        // a well-rated dribbler's sprint push skims lower than his jog nudge, though it is launched faster
        if (dribbling >= 90) Assert.True(sprint.MeanPeak < jog.MeanPeak, $"mean peak: sprint {sprint.MeanPeak:F4} m, jog {jog.MeanPeak:F4} m");
        // neither arc rises over its profile's cap (the speed error may carry it a little over)
        Assert.True(jog.MaxPeak <= T.Touch.Profiles.Jog.PeakHeightCap + 0.01f, $"jog peak {jog.MaxPeak:F3} m");
        Assert.True(sprint.MaxPeak <= T.Touch.Profiles.Sprint.PeakHeightCap + 0.01f, $"sprint peak {sprint.MaxPeak:F3} m");
        Assert.True(sprint.MeanSpeed > jog.MeanSpeed + 1f, $"launch speed: sprint {sprint.MeanSpeed:F2}, jog {jog.MeanSpeed:F2} m/s");
    }

    [Fact]
    public void APeakCapFlattensTheLaunch()
    {
        // The same push solved under a low cap keeps under it (and still lands on the target), against an uncapped one.
        var t = new Tuning();
        var p = TestData.Outfield(TestData.Sheet(1));
        var ball = new Ball(t.Ball) { Position = new Vector3(0.3f, t.Ball.Radius, 0f) };
        var target = new Vector3(4f, t.Ball.Radius, 0f);
        float u = TouchPlanner.SolvePush(ball, target, 60, 6f, 0f, 12f, t, out var dir, out _, out _);
        float free = TouchPlanner.ArcPeak(ball, dir, u, 6f, 60, t);
        t.Touch.Profiles.Sprint.PeakHeightCap = t.Ball.Radius + 0.5f * (free - t.Ball.Radius);
        float u2 = TouchPlanner.SolveLiftedPush(p, ball, target, 60, ActionKind.Sprint, TouchMode.Sprint, 0f, 12f, t, out var dir2, out bool @short, out float lift);
        Assert.False(@short);
        Assert.True(lift < 6f, $"lift {lift:F2}");
        Assert.True(TouchPlanner.ArcPeak(ball, dir2, u2, lift, 60, t) <= t.Touch.Profiles.Sprint.PeakHeightCap + 0.006f);
    }

    [Theory]
    [InlineData("""{ "touch": { "profiles": { "jog": { "min_push": 5, "max_push": 3 } } } }""", "touch.profiles.jog: max_push")]
    [InlineData("""{ "touch": { "profiles": { "sprint": { "launch_angle": { "at1": -1, "at99": 3 } } } } }""", "touch.profiles.sprint: launch_angle.at1")]
    [InlineData("""{ "touch": { "profiles": { "close": { "max_ball_height": 0 } } } }""", "touch.profiles.close: max_ball_height")]
    [InlineData("""{ "touch": { "profiles": { "knock_on": { "reach": 0 } } } }""", "touch.profiles.knock_on: reach")]
    [InlineData("""{ "touch": { "profiles": { "heavy": null } } }""", "touch.profiles.heavy: must be an object")]
    public void BadProfilesFallBackToDefaults(string json, string expect)
    {
        var r = DataLoader.ParseTuning(json, "t.json");
        Assert.NotNull(r.Error);
        Assert.Contains(expect, r.Error);
        Assert.Contains("default tuning", r.Error);
        Assert.Equal(DataLoader.TuningToJson(new Tuning()), DataLoader.TuningToJson(r.Value!));
    }

    [Fact]
    public void AGivenProfileLoads()
    {
        var r = DataLoader.ParseTuning("""{ "touch": { "profiles": { "jog": { "launch_angle": { "at1": 10, "at99": 4 }, "peak_height_cap": 0.3, "min_push": 1.5, "max_push": 9, "max_ball_height": 0.4, "reach": 0.9 } } } }""", "t.json");
        Assert.True(r.Ok, r.Error);
        var j = r.Value!.Touch.Profiles.Jog;
        Assert.Equal(10f, j.LaunchAngle.At1);
        Assert.Equal(0.3f, j.PeakHeightCap);
        Assert.Equal(9f, j.MaxPush);
        Assert.Equal(0.9f, j.Reach);
        Assert.Equal(new Tuning().Touch.Profiles.Sprint.MaxPush, r.Value.Touch.Profiles.Sprint.MaxPush);
    }
}
