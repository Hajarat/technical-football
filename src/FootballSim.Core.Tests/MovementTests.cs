using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

public class MovementTests
{
    static readonly Tuning T = new();
    static readonly float Dt = T.Dt;

    static void Run(PlayerState p, TickIntent it, float seconds, bool hasBall = false)
    {
        float now = 0;
        for (int i = 0; i < (int)(seconds / Dt); i++) { now += Dt; Movement.Step(p, it, Dt, now, hasBall, T, null); }
    }

    [Theory]
    [InlineData(1, 6.0f)]
    [InlineData(99, 9.8f)]
    [InlineData(50, 6.0f + 3.8f * 49f / 98f)]
    public void SprintReachesTheRatedTopSpeed(int rating, float expected)
    {
        var s = TestData.Sheet(50); s.SprintSpeed = rating;
        var p = TestData.Outfield(s);
        Run(p, new TickIntent { MoveX = 1, Sprint = 1 }, 12f);
        Assert.InRange(p.Speed / expected, 0.995f, 1.001f);
    }

    [Fact]
    public void JogIsSixtyPercentAndCloseControlThirtyFive()
    {
        var s = TestData.Sheet(60);
        float top = Movement.TopSpeed(s, T);
        var p = TestData.Outfield(s);
        Run(p, new TickIntent { MoveX = 1 }, 12f);
        Assert.InRange(p.Speed / (0.6f * top), 0.995f, 1.001f);
        var q = TestData.Outfield(s);
        Run(q, new TickIntent { MoveX = 1, CloseControl = true, Sprint = 1 }, 12f);
        Assert.InRange(q.Speed / (0.35f * top), 0.995f, 1.001f);
        var h = TestData.Outfield(s);
        Run(h, new TickIntent { MoveX = 0.5f, Sprint = 1 }, 12f);
        Assert.InRange(h.Speed / (0.5f * top), 0.995f, 1.001f);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    [InlineData(99)]
    public void TimeToNinetyPercentFollowsTheTimeConstant(int accel)
    {
        var s = TestData.Sheet(50); s.Acceleration = accel;
        var p = TestData.Outfield(s);
        float top = Movement.TopSpeed(s, T);
        float tau = Movement.TimeConstant(s, T);
        float now = 0; int ticks = 0;
        while (p.Speed < 0.9f * top && ticks < 5000) { now += Dt; Movement.Step(p, new TickIntent { MoveX = 1, Sprint = 1 }, Dt, now, false, T, null); ticks++; }
        float expected = tau * MathF.Log(10f);
        Assert.InRange(ticks * Dt / expected, 0.97f, 1.03f);
    }

    [Fact]
    public void AccelerationRatingRunsFromOnePointOneToPointFourFive()
    {
        var s = TestData.Sheet(50);
        s.Acceleration = 1; Assert.Equal(1.10f, Movement.TimeConstant(s, T), 3);
        s.Acceleration = 99; Assert.Equal(0.45f, Movement.TimeConstant(s, T), 3);
    }

    [Fact]
    public void TurnRateFollowsAgilityAndFallsWithSpeed()
    {
        var s = TestData.Sheet(50); s.Agility = 99; s.WeightKg = 75;
        Assert.Equal(420f, Movement.TurnRateDeg(s, T), 2);
        // Standing start: one tick turns by rate * dt (speed is still ~0).
        var p = TestData.Outfield(s, facing: 0f);
        Movement.Step(p, new TickIntent { MoveZ = 1 }, Dt, Dt, false, T, null);
        Assert.InRange(p.Facing * 180f / MathF.PI, 420f * Dt * 0.97f, 420f * Dt * 1.001f);
        // At top speed the rate is halved.
        var q = TestData.Outfield(s, facing: 0f);
        q.Velocity = new Vector3(Movement.TopSpeed(s, T), 0, 0);
        Movement.Step(q, new TickIntent { MoveZ = 1, Sprint = 1 }, Dt, Dt, false, T, null);
        Assert.InRange(q.Facing * 180f / MathF.PI, 210f * Dt * 0.97f, 210f * Dt * 1.03f);
    }

    [Fact]
    public void HeavierPlayersTurnSlower()
    {
        var light = TestData.Sheet(50, weight: 55); var heavy = TestData.Sheet(50, weight: 100);
        Assert.True(Movement.TurnRateDeg(light, T) > Movement.TurnRateDeg(heavy, T));
        Assert.Equal(300f * 1.12f, Movement.TurnRateDeg(light, T), 1);  // 1 - 0.15 * (55 - 75) / 25 = 1.12
        Assert.Equal(300f * 0.85f, Movement.TurnRateDeg(heavy, T), 1);  // 1 - 0.15 = 0.85
    }

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    public void SharpTurnCostsTheBalancePercentage(int balance)
    {
        var s = TestData.Sheet(50); s.Balance = balance; s.Agility = 99;
        var p = TestData.Outfield(s, facing: 0f);
        // At high speed the turn rate halves, so a 60 degree turn inside 0.2 s only happens at low speed (spec numbers).
        p.Velocity = new Vector3(2f, 0, 0);
        float loss = Movement.BalanceLoss(s, T);
        float now = 0, worstRatio = 1f;
        for (int i = 0; i < 120; i++)
        {
            float before = p.Speed;
            now += Dt;
            Movement.Step(p, new TickIntent { MoveX = -1, CloseControl = true }, Dt, now, false, T, null); // turn through 180 degrees
            worstRatio = MathF.Min(worstRatio, p.Speed / before);
        }
        // Single-tick drop is (1 - loss) times the normal one-tick change.
        Assert.InRange(worstRatio, (1f - loss) * 0.97f, (1f - loss) * 1.03f);
    }

    [Fact]
    public void GentleTurnsCostNothing()
    {
        var s = TestData.Sheet(50);
        var p = TestData.Outfield(s, facing: 0f);
        p.Velocity = new Vector3(6f, 0, 0);
        float now = 0;
        for (int i = 0; i < 60; i++)
        {
            now += Dt;
            float before = p.Speed;
            Movement.Step(p, new TickIntent { MoveX = 1, MoveZ = 0.3f, Sprint = 1 }, Dt, now, false, T, null);
            Assert.True(p.Speed >= before * 0.999f);
        }
    }

    [Fact]
    public void DribblingLowersTopSpeedWithTheBall()
    {
        var s = TestData.Sheet(50); s.Dribbling = 1;
        float top = Movement.TopSpeed(s, T);
        Assert.Equal(top * 0.80f, Movement.TopSpeedWithBall(s, T), 3);
        s.Dribbling = 99;
        Assert.Equal(top * 0.96f, Movement.TopSpeedWithBall(s, T), 3);
    }

    [Fact]
    public void PlayersStayOnThePitch()
    {
        var pitch = new Pitch(new PitchSpec(), T.Frame);
        var p = TestData.Outfield(TestData.Sheet(99), 52f, 0f);
        float now = 0;
        for (int i = 0; i < 600; i++) { now += Dt; Movement.Step(p, new TickIntent { MoveX = 1, Sprint = 1 }, Dt, now, false, T, pitch); }
        Assert.Equal(pitch.GoalLineX, p.Position.X);
    }
}
