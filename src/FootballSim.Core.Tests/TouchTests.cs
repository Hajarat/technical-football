using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

public class TouchTests
{
    static readonly Tuning T = new();

    static (PlayerState p, Ball b) Setup(PlayerSheet? s = null, float speed = 0f)
    {
        var p = TestData.Outfield(s ?? TestData.Sheet(60));
        p.Velocity = new Vector3(speed, 0, 0);
        var b = new Ball(T.Ball) { Position = new Vector3(0.4f, T.Ball.Radius, 0) };
        return (p, b);
    }

    static bool Touch(PlayerState p, Ball b, TickIntent it, float now, SimRng rng, bool inPossession = true, bool softOnly = false) =>
        TouchModel.TryTouch(p, it, b, now, inPossession, softOnly, T, rng, out _);

    [Fact]
    public void CadenceAndOvershootByMode()
    {
        var s = TestData.Sheet(50);
        Assert.Equal(0.16f, TouchModel.Cadence(TouchMode.CloseControl, T));
        Assert.Equal(0.22f, TouchModel.Cadence(TouchMode.Jog, T));
        Assert.Equal(0.30f, TouchModel.Cadence(TouchMode.Sprint, T));
        Assert.Equal(0.02f, TouchModel.Overshoot(s, TouchMode.CloseControl, T));
        Assert.Equal(0.05f, TouchModel.Overshoot(s, TouchMode.Jog, T));
        s.Dribbling = 1; Assert.Equal(0.22f, TouchModel.Overshoot(s, TouchMode.Sprint, T), 4);
        s.Dribbling = 99; Assert.Equal(0.06f, TouchModel.Overshoot(s, TouchMode.Sprint, T), 4);
        Assert.Equal(0.60f, TouchModel.Lead(s, TouchMode.Sprint, T), 4);
        s.Dribbling = 1; Assert.Equal(1.20f, TouchModel.Lead(s, TouchMode.Sprint, T), 4);
        Assert.Equal(27, TouchModel.CadenceTicks(TouchMode.Jog, T));
        Assert.Equal(TouchMode.CloseControl, TouchModel.ModeFor(new TickIntent { CloseControl = true, Sprint = 1 }, T));
        Assert.Equal(TouchMode.Sprint, TouchModel.ModeFor(new TickIntent { Sprint = 0.8f }, T));
        Assert.Equal(TouchMode.Jog, TouchModel.ModeFor(new TickIntent { Sprint = 0.1f }, T));
    }

    /// <summary>The default tuning with the touch speed and direction errors switched off.</summary>
    static Tuning Exact()
    {
        var t = new Tuning();
        t.Touch.SpeedError = new RatingRange(0f, 0f);
        t.Touch.DirectionErrorDeg = new RatingRange(0f, 0f);
        return t;
    }

    /// <summary>
    /// Makes one touch with <paramref name="t"/>, then steps the player (same intent) and the rolling ball for the mode's cadence
    /// and returns how far the ball is ahead of the feet along +X, and the ball speed the touch gave.
    /// </summary>
    static (float Ahead, float Speed) PlannedTouch(Tuning t, PlayerSheet s, TickIntent it, float speed)
    {
        var p = TestData.Outfield(s);
        p.Velocity = new Vector3(speed, 0, 0);
        var b = new Ball(t.Ball) { Position = new Vector3(0.4f, t.Ball.Radius, 0) };
        float now = 1f;
        Assert.True(TouchModel.TryTouch(p, it, b, now, true, false, t, new SimRng(1), out var r));
        Assert.Equal(TouchKind.Dribble, r.Kind);
        float v = b.Velocity.Length();
        int n = TouchModel.CadenceTicks(TouchModel.ModeFor(it, t), t);
        var cap = Movement.TopSpeedWithBall(s, t);
        for (int i = 0; i < n; i++)
        {
            now += t.Dt;
            Movement.Step(p, it, t.Dt, now, new MoveAssist(cap, 1f, Vector2.Zero), t, null);
            b.Step(t.Dt, null, null);
        }
        return (b.Position.X - p.Position.X, v);
    }

    [Theory]
    [InlineData(false, 0f, 0f)]
    [InlineData(false, 0f, 4.5f)]
    [InlineData(false, 1f, 8f)]
    [InlineData(true, 0f, 2.5f)]
    public void APlannedTouchArrivesTheLeadAheadOfTheFeetAtTheNextTouch(bool close, float sprint, float speed)
    {
        var t = Exact();
        var s = TestData.Sheet(80);
        var it = new TickIntent { MoveX = 1, Sprint = sprint, CloseControl = close };
        var (ahead, v) = PlannedTouch(t, s, it, speed);
        float lead = TouchModel.Lead(s, TouchModel.ModeFor(it, t), t);
        // From a standstill the minimum push may send the ball a little further than planned.
        if (v > TouchModel.MinPush(TouchModel.ModeFor(it, t), t) + 1e-3f) Assert.InRange(ahead, lead - 0.03f, lead + 0.03f);
        else Assert.InRange(ahead, lead - 0.03f, lead + 1.0f);
    }

    [Fact]
    public void PlannedTouchErrorsStillFollowTheRatings()
    {
        // Same sigmas and draw order as before: the mean speed matches the error-free plan and the spread falls with dribbling.
        double Spread(int dribbling, out double meanRatio)
        {
            var s = TestData.Sheet(60); s.Dribbling = dribbling;
            var (_, clean) = PlannedTouch(Exact(), s, new TickIntent { MoveX = 1 }, 5f);
            double sum = 0, sq = 0; int n = 600;
            for (ulong i = 0; i < (ulong)n; i++)
            {
                var (p, b) = Setup(s, 5f);
                Assert.True(Touch(p, b, new TickIntent { MoveX = 1 }, 1f, new SimRng(i)));
                double r = b.Velocity.Length() / clean;
                sum += r; sq += r * r;
            }
            meanRatio = sum / n;
            return Math.Sqrt(sq / n - meanRatio * meanRatio);
        }
        double lo = Spread(10, out var m1), hi = Spread(95, out var m2);
        Assert.InRange(m1, 0.98, 1.02);
        Assert.InRange(m2, 0.99, 1.01);
        Assert.True(lo > 2 * hi, $"speed spread: dribbling 10 {lo:F3}, 95 {hi:F3}");
    }

    [Fact]
    public void RollSpeedCoversTheDistanceInTheTime()
    {
        float a = T.Ball.RollDeceleration;
        float v = TouchModel.RollSpeedFor(2f, 0.3f, a);
        Assert.Equal(2f, v * 0.3f - 0.5f * a * 0.09f, 4);
        // Too short to still be rolling at the time: the ball is sent to stop at the distance.
        float slow = TouchModel.RollSpeedFor(0.02f, 0.3f, a);
        Assert.Equal(0.02f, slow * slow / (2f * a), 4);
        Assert.Equal(0f, TouchModel.RollSpeedFor(-0.5f, 0.3f, a));
    }

    [Fact]
    public void TouchBoostGrowsWithDribblingAndTheTurn()
    {
        var s = TestData.Sheet(50);
        s.Dribbling = 1;
        Assert.Equal(1.15f, TouchModel.TouchBoostFactor(s, 0f, T), 3);
        s.Dribbling = 99;
        Assert.Equal(1.35f, TouchModel.TouchBoostFactor(s, 0f, T), 3);
        Assert.Equal(1.50f, TouchModel.TouchBoostFactor(s, 45f, T), 3);
        Assert.Equal(1.65f, TouchModel.TouchBoostFactor(s, 90f, T), 3);
        Assert.Equal(1.65f, TouchModel.TouchBoostFactor(s, 170f, T), 3);

        // A dribble touch starts the boost; a trap ends it.
        var (p, b) = Setup(s, 4f);
        Assert.True(Touch(p, b, new TickIntent { MoveX = 1 }, 1f, new SimRng(1)));
        float tau = Movement.TimeConstant(s, T);
        Assert.True(Movement.TimeConstantAt(p, 1.1f, T) < tau);
        Assert.Equal(tau, Movement.TimeConstantAt(p, 1.26f, T));
        b.Position = new Vector3(p.Position.X + 0.3f, T.Ball.Radius, 0);
        b.Velocity = new Vector3(8f, 0, 0);
        Assert.True(TouchModel.TryTouch(p, default, b, 1.15f, true, false, T, new SimRng(2), out var trap));
        Assert.Equal(TouchKind.Trap, trap.Kind);
        Assert.Equal(tau, Movement.TimeConstantAt(p, 1.2f, T));
    }

    [Fact]
    public void DirectionErrorGrowsWithSpeed()
    {
        double Spread(float speed)
        {
            var s = TestData.Sheet(50); s.BallControl = 1;
            double sq = 0; int n = 1000;
            for (ulong i = 0; i < (ulong)n; i++)
            {
                var (p, b) = Setup(s, speed);
                Touch(p, b, new TickIntent { MoveX = 1 }, 1f, new SimRng(i));
                float ang = MathF.Atan2(b.Velocity.Z, b.Velocity.X) * 180f / MathF.PI;
                sq += ang * ang;
            }
            return Math.Sqrt(sq / n);
        }
        float top = Movement.TopSpeed(TestData.Sheet(50), T);
        double slow = Spread(0f), fast = Spread(top);
        Assert.InRange(fast / slow, 2.2, 2.8);                     // (0.4 + 0.6) / 0.4 = 2.5
        Assert.InRange(slow, 7 * 0.4 * 0.92, 7 * 0.4 * 1.08);       // ball_control 1: 7 degrees * 0.4
    }

    [Fact]
    public void TouchNeedsReachHeightCadenceAndAStick()
    {
        var (p, b) = Setup();
        var rng = new SimRng(1);
        var go = new TickIntent { MoveX = 1 };
        b.Position = new Vector3(TouchModel.FootReach(p.Sheet, T) + 0.05f, T.Ball.Radius, 0);
        Assert.False(Touch(p, b, go, 1f, rng), "out of reach");
        b.Position = new Vector3(0.4f, 0.5f, 0);
        Assert.False(Touch(p, b, go, 1f, rng), "too high");
        b.Position = new Vector3(0.4f, T.Ball.Radius, 0);
        Assert.False(Touch(p, b, default, 1f, rng), "no stick and a still ball: no touch");
        Assert.True(Touch(p, b, go, 1f, rng));
        b.Position = new Vector3(0.4f, T.Ball.Radius, 0);
        b.Velocity = new Vector3(2.1f, 0, 0);
        Assert.False(Touch(p, b, go, 1.1f, rng), "0.22 s jog cadence");
        Assert.True(Touch(p, b, go, 1.23f, rng));
    }

    [Fact]
    public void ATurnAllowsAnEarlyTouchAndASharpOneIsACut()
    {
        var s = TestData.Sheet(60);
        var (p, b) = Setup(s, 5f);
        b.Velocity = new Vector3(5f, 0, 0);
        p.LastTouchTime = 0.9f;
        // 0.12 s after the last touch: too early for the jog cadence straight on, fine for a 90 degree turn.
        Assert.False(Touch(p, b, new TickIntent { MoveX = 1 }, 1.02f, new SimRng(1)));
        Assert.True(TouchModel.TryTouch(p, new TickIntent { MoveZ = 1 }, b, 1.02f, true, false, T, new SimRng(1), out var r));
        Assert.Equal(TouchKind.Cut, r.Kind);
        float expected = 5f * (1f - Movement.BalanceLoss(s, T));
        Assert.InRange(b.Velocity.Length(), expected * 0.85f, expected * 1.15f);
        Assert.InRange(MathF.Atan2(b.Velocity.Z, b.Velocity.X) * 180f / MathF.PI, 80f, 100f);
    }

    [Fact]
    public void FootReachGrowsWithHeight()
    {
        Assert.Equal(0.55f, TouchModel.FootReach(TestData.Sheet(50, 1.60f), T), 3);
        Assert.Equal(0.85f, TouchModel.FootReach(TestData.Sheet(50, 2.00f), T), 3);
    }

    [Fact]
    public void FirstTouchRatingShapesTheFirstContact()
    {
        // A loose ball rolling at the player: the first contact keeps part of the relative speed and errs by first_touch.
        double Error(int firstTouch)
        {
            var s = TestData.Sheet(60); s.FirstTouch = firstTouch;
            double sum = 0; int n = 400;
            for (ulong i = 0; i < (ulong)n; i++)
            {
                var (p, b) = Setup(s, 3f);
                b.Velocity = new Vector3(-9f, 0, 0);
                TouchModel.TryTouch(p, new TickIntent { MoveX = 1 }, b, 1f, false, false, T, new SimRng(i), out var r);
                Assert.Equal(TouchKind.First, r.Kind);
                sum += MathF.Abs(r.DirectionErrorDeg);
            }
            return sum / n;
        }
        Assert.True(Error(5) > 2 * Error(95));
        Assert.Equal(0.45f, TouchModel.FirstTouchSpeedKept(new PlayerSheet { FirstTouch = 1 }, T), 3);
    }

    [Fact]
    public void WeakFootTouchesAreLessAccurate()
    {
        var s = TestData.Sheet(60); s.WeakFoot = 1;
        Assert.Equal(1.4f, TouchModel.FootErrorFactor(s, Foot.Left, T), 3);
        Assert.Equal(1f, TouchModel.FootErrorFactor(s, Foot.Right, T), 3);
        var p = TestData.Outfield(s);
        Assert.Equal(Foot.Left, TouchModel.ChooseFoot(s, p.Position, 0f, new Vector3(0.3f, 0.11f, -0.2f), T));
        Assert.Equal(Foot.Right, TouchModel.ChooseFoot(s, p.Position, 0f, new Vector3(0.3f, 0.11f, -0.1f), T));
    }
}
