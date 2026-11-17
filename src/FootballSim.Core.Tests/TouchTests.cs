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
        Assert.Equal(TouchMode.CloseControl, TouchModel.ModeFor(new TickIntent { CloseControl = true, Sprint = 1 }, T));
        Assert.Equal(TouchMode.Sprint, TouchModel.ModeFor(new TickIntent { Sprint = 0.8f }, T));
        Assert.Equal(TouchMode.Jog, TouchModel.ModeFor(new TickIntent { Sprint = 0.1f }, T));
    }

    [Theory]
    [InlineData(true, 1.2f * 1.02f)]
    [InlineData(false, 2.0f * 1.05f)]
    public void StandingTouchGivesTheMinimumPushPlusOvershoot(bool close, float expected)
    {
        var s = TestData.Sheet(60);
        double sum = 0; int n = 400;
        for (ulong i = 0; i < (ulong)n; i++)
        {
            var (p, b) = Setup(s);
            Assert.True(Touch(p, b, new TickIntent { MoveX = 1, CloseControl = close }, 1f, new SimRng(i)));
            sum += b.Velocity.Length();
        }
        Assert.InRange(sum / n / expected, 0.99, 1.01);
    }

    [Fact]
    public void RunningTouchMatchesThePlayersSpeedPlusOvershoot()
    {
        var s = TestData.Sheet(60);
        double sum = 0; int n = 400;
        for (ulong i = 0; i < (ulong)n; i++)
        {
            var (p, b) = Setup(s, 5f);
            Assert.True(Touch(p, b, new TickIntent { MoveX = 1 }, 1f, new SimRng(i)));
            sum += b.Velocity.Length();
        }
        Assert.InRange(sum / n / (5f * 1.05f), 0.99, 1.01);
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
