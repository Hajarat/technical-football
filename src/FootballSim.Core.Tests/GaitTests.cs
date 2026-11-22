using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>The stride clock: frequency by speed, alternating feet, swing windows, idling, and the first-step lock.</summary>
public class GaitTests
{
    static readonly Tuning T = new();

    [Fact]
    public void FrequencyFollowsSpeedAndIdlesAtRest()
    {
        Assert.Equal(0f, Gait.Frequency(0f, T));
        Assert.Equal(0f, Gait.Frequency(0.2f, T));
        Assert.Equal(2.4f + 0.17f * 5f, Gait.Frequency(5f, T), 4);
        Assert.Equal(2.4f + 0.17f * 8f, Gait.Frequency(8f, T), 4);
    }

    [Fact]
    public void StepsAlternateAndSwingMiddlesFallOnTheFrequency()
    {
        var p = TestData.Outfield();
        float speed = 6f, f = Gait.Frequency(speed, T);
        var mids = new List<(int Tick, Foot Foot)>();
        for (int i = 1; i <= 600; i++)
            if (Gait.Advance(p, speed, T.Dt, T) is Foot foot) mids.Add((i, foot));
        Assert.True(mids.Count >= 15, $"{mids.Count} swing middles");
        for (int i = 1; i < mids.Count; i++)
        {
            Assert.NotEqual(mids[i - 1].Foot, mids[i].Foot);                              // left, right, left ...
            Assert.InRange((mids[i].Tick - mids[i - 1].Tick) * T.Dt, 1f / f - T.Dt, 1f / f + T.Dt);
        }
        Assert.Equal(Foot.Right, mids[0].Foot);                                           // phase 0 starts a right step
    }

    [Fact]
    public void PhaseRunsOnSmoothlyThroughSpeedChangesAndFootPhasesMatchTheSwing()
    {
        var p = TestData.Outfield();
        float prev = p.GaitPhase;
        for (int i = 0; i < 1200; i++)
        {
            float speed = 2f + 6f * (0.5f + 0.5f * MathF.Sin(i * 0.01f));
            Gait.Advance(p, speed, T.Dt, T);
            float step = p.GaitPhase - prev;
            if (step < 0f) step += 2f;
            Assert.InRange(step, 0f, Gait.Frequency(8f, T) * T.Dt + 1e-5f);              // never jumps
            prev = p.GaitPhase;
            // The foot in swing is the one whose own stride phase is in its swing half.
            float own = p.SwingFoot == Foot.Right ? p.RightFootPhase : p.LeftFootPhase;
            float other = p.SwingFoot == Foot.Right ? p.LeftFootPhase : p.RightFootPhase;
            Assert.InRange(own, 0f, 0.5f);
            Assert.InRange(other, 0.5f, 1f);
            Assert.Equal(p.SwingProgress, own * 2f, 4);
        }
    }

    [Fact]
    public void AtRestTheClockFinishesTheStepThenHolds()
    {
        var p = TestData.Outfield();
        for (int i = 0; i < 40; i++) Gait.Advance(p, 5f, T.Dt, T);
        Assert.True(p.GaitPhase % 1f > 0.01f);
        for (int i = 0; i < 120; i++) Gait.Advance(p, 0f, T.Dt, T);
        Assert.Equal(MathF.Floor(p.GaitPhase), p.GaitPhase);
        float held = p.GaitPhase;
        Gait.Advance(p, 0f, T.Dt, T);
        Assert.Equal(held, p.GaitPhase);
        Assert.False(p.IsStepping);
    }

    [Fact]
    public void PredictedSwingMiddleTickMatchesTheClockAtSteadySpeed()
    {
        var p = TestData.Outfield();
        for (int i = 0; i < 50; i++) Gait.Advance(p, 6f, T.Dt, T);
        p.CurrentTick = 50;
        int right = p.NextSwingMiddleTick(Foot.Right, T.Dt), left = p.NextSwingMiddleTick(Foot.Left, T.Dt);
        int tick = 50, seenR = -1, seenL = -1;
        while (tick < 300 && (seenR < 0 || seenL < 0))
        {
            tick++;
            var f = Gait.Advance(p, 6f, T.Dt, T);
            if (f == Foot.Right && seenR < 0) seenR = tick;
            if (f == Foot.Left && seenL < 0) seenL = tick;
        }
        Assert.InRange(seenR, right - 1, right + 1);
        Assert.InRange(seenL, left - 1, left + 1);
    }

    [Fact]
    public void ALockLandsTheSwingMiddleOnItsTick()
    {
        var p = TestData.Outfield();
        p.GaitLockTicks = 36;
        p.GaitLockTarget = 1.5f;   // the left swing middle, one and a half steps on, from a standstill
        Foot? crossedAt36 = null;
        for (int i = 1; i <= 36; i++)
        {
            var f = Gait.Advance(p, 0f, T.Dt, T);
            if (i == 36) crossedAt36 = f;
            else if (f == Foot.Left) Assert.Fail($"left swing middle at tick {i}");
        }
        Assert.Equal(Foot.Left, crossedAt36);
        Assert.Equal(1.5f, p.GaitPhase, 5);
    }

    [Fact]
    public void TheWorldRunsTheClockFromTheRunningSpeed()
    {
        var w = TestData.Live("breakaway");
        var buf = new TickIntent[w.PlayerCount];
        buf[0] = new TickIntent { MoveX = 1, Sprint = 1 };
        for (int i = 0; i < 240; i++) w.Step(buf);
        var p = w.Attacker;
        // Outside a committed touch's windup (where the touch controller holds the clock to the contact) the clock runs from
        // the running speed.
        int unlocked = 0;
        for (int i = 0; i < 240 && unlocked < 2; i++) { w.Step(buf); unlocked = p.GaitLockTicks == 0 ? unlocked + 1 : 0; }
        Assert.Equal(2, unlocked);
        Assert.True(p.IsStepping);
        Assert.Equal(Gait.Frequency(p.RunSpeed, w.Tuning), p.StepFrequency, 3);
        Assert.Equal(w.Tick, p.CurrentTick);
    }
}
