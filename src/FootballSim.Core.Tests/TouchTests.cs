using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

public class TouchTests
{
    static readonly Tuning T = new();

    /// <summary>A long empty pitch with the ball 0.5 m in front of a standing attacker.</summary>
    static World Lab(PlayerSheet attacker, ulong seed, Tuning? t = null)
    {
        var sc = new Scenario
        {
            Id = "lab",
            Pitch = new PitchSpec { Length = 150f },
            Ball = new StartSpot { X = 2.5f },
            Attacker = new StartSpot { X = 2.0f },
        };
        var w = new World(t ?? new Tuning(), sc, attacker, TestData.Sheet(50), seed);
        w.Begin();
        return w;
    }

    [Fact]
    public void TheCloseStylesCadenceFollowsDribblingOnItsOwnRange()
    {
        var t = new Tuning();
        t.Touch.StepsPerTouch.Close = new RatingRange(3f, 1f);
        t.Touch.StepsPerTouch.Sprint = new RatingRange(5f, 2f);
        var s = TestData.Sheet(50);
        s.CloseDribbling = s.SprintDribbling = 1;
        Assert.Equal(3, TouchModel.StepsPerTouch(s, TouchMode.CloseControl, t));
        Assert.Equal(5, TouchModel.StepsPerTouch(s, TouchMode.Sprint, t));
        s.CloseDribbling = s.SprintDribbling = 50;
        Assert.Equal(2, TouchModel.StepsPerTouch(s, TouchMode.CloseControl, t));
        s.CloseDribbling = s.SprintDribbling = 99;
        Assert.Equal(1, TouchModel.StepsPerTouch(s, TouchMode.CloseControl, t));
        Assert.Equal(2, TouchModel.StepsPerTouch(s, TouchMode.Sprint, t));
        // the jog's is the same for every player
        Assert.Equal(2, TouchModel.StepsPerTouch(s, TouchMode.Jog, t));
    }

    [Fact]
    public void ModeStepsPerTouchAndLiftFollowTheTuning()
    {
        var s = TestData.Sheet(50);
        Assert.Equal(TouchMode.CloseControl, TouchModel.ModeFor(new TickIntent { CloseControl = true, Sprint = 1 }, T));
        Assert.Equal(TouchMode.Sprint, TouchModel.ModeFor(new TickIntent { Sprint = 0.8f }, T));
        Assert.Equal(TouchMode.Jog, TouchModel.ModeFor(new TickIntent { Sprint = 0.1f }, T));
        Assert.Equal((int)MathF.Round(T.Touch.StepsPerTouch.Close.ForRating(s.CloseDribbling), MidpointRounding.AwayFromZero),
            TouchModel.StepsPerTouch(s, TouchMode.CloseControl, T));
        Assert.Equal(2, TouchModel.StepsPerTouch(s, TouchMode.Jog, T));
        s.CloseDribbling = s.SprintDribbling = 1; Assert.Equal(4, TouchModel.StepsPerTouch(s, TouchMode.Sprint, T));
        s.CloseDribbling = s.SprintDribbling = 50; Assert.Equal(3, TouchModel.StepsPerTouch(s, TouchMode.Sprint, T));
        s.CloseDribbling = s.SprintDribbling = 99; Assert.Equal(2, TouchModel.StepsPerTouch(s, TouchMode.Sprint, T));
        Assert.Equal(T.Touch.Profiles.Jog.LaunchAngle.At99, TouchModel.LiftDeg(s, ActionKind.Jog, TouchMode.Jog, T), 4);
        s.CloseDribbling = s.SprintDribbling = 1; Assert.Equal(T.Touch.Profiles.Sprint.LaunchAngle.At1, TouchModel.LiftDeg(s, ActionKind.Sprint, TouchMode.Sprint, T), 4);
        Assert.Equal(T.Touch.Profiles.KnockOn.LaunchAngle.At1, TouchModel.LiftDeg(s, ActionKind.KnockOn, TouchMode.Jog, T), 4);
        Assert.Equal(12f, TouchModel.LiftDeg(s, ActionKind.Heavy, TouchMode.Sprint, T), 4);
    }

    [Fact]
    public void ContactPointIsAheadOfTheSwingingFootByTheContactZone()
    {
        var p = TestData.Outfield(TestData.Sheet(50), x: 10f, z: 2f);
        var r = TouchPlanner.ContactPointFor(p, Foot.Right, T);
        Assert.Equal(10.25f, r.X, 4);                         // 0.25 m ahead at rest
        Assert.Equal(2f + T.Touch.LateralFootOffset, r.Z, 4); // right of the centreline (+Z when facing +X)
        Assert.Equal(T.Ball.Radius, r.Y, 4);
        var l = TouchPlanner.ContactPointFor(p, Foot.Left, T);
        Assert.Equal(2f - T.Touch.LateralFootOffset, l.Z, 4);
        p.RunSpeed = Movement.TopSpeed(p.Sheet, T);
        Assert.Equal(10.45f, TouchPlanner.ContactPointFor(p, Foot.Right, T).X, 4); // 0.45 m at top speed
    }

    [Fact]
    public void TouchBoostGrowsWithDribblingAndTheTurn()
    {
        var s = TestData.Sheet(50);
        s.CloseDribbling = s.SprintDribbling = 1;
        Assert.Equal(1.15f, TouchModel.TouchBoostFactor(s.CloseDribbling, 0f, T), 3);
        s.CloseDribbling = s.SprintDribbling = 99;
        Assert.Equal(1.35f, TouchModel.TouchBoostFactor(s.CloseDribbling, 0f, T), 3);
        Assert.Equal(1.50f, TouchModel.TouchBoostFactor(s.CloseDribbling, 45f, T), 3);
        Assert.Equal(1.65f, TouchModel.TouchBoostFactor(s.CloseDribbling, 90f, T), 3);
        Assert.Equal(1.65f, TouchModel.TouchBoostFactor(s.CloseDribbling, 170f, T), 3);
    }

    [Fact]
    public void DirectionErrorGrowsWithSpeedAndFollowsBallControl()
    {
        var s = TestData.Sheet(50); s.BallControl = 1;
        Assert.Equal(7f * 0.4f, TouchModel.DirectionSigmaDeg(s, 0f, T), 4);
        Assert.Equal(2.5f, TouchModel.DirectionSigmaDeg(s, 1f, T) / TouchModel.DirectionSigmaDeg(s, 0f, T), 4);
        s.BallControl = 99;
        Assert.Equal(1f * 0.4f, TouchModel.DirectionSigmaDeg(s, 0f, T), 4);
    }

    [Trait("Category", "Slow")]
    [Fact]
    public void TouchSpeedErrorsStillFollowTheRatings()
    {
        // The first push from a standstill is solved the same with or without errors, so the speed ratio is the drawn factor.
        double Spread(int dribbling, out double meanRatio)
        {
            var s = TestData.Sheet(60); s.CloseDribbling = s.SprintDribbling = dribbling;
            var exact = new Tuning();
            exact.Touch.SpeedError = new RatingRange(0f, 0f);
            exact.Touch.DirectionErrorDeg = new RatingRange(0f, 0f);
            double sum = 0, sq = 0; int n = 400;
            var buf = new TickIntent[2];
            buf[0] = new TickIntent { MoveX = 1 };
            for (ulong i = 0; i < (ulong)n; i++)
            {
                var a = Lab(s, i, exact); a.Step(buf);
                var b = Lab(s, i); b.Step(buf);
                Assert.Contains(b.Events, e => e.Kind == EventKind.Touch);
                double r = b.Ball.Velocity.Length() / a.Ball.Velocity.Length();
                sum += r; sq += r * r;
            }
            meanRatio = sum / n;
            return Math.Sqrt(sq / n - meanRatio * meanRatio);
        }
        double lo = Spread(10, out var m1), hi = Spread(95, out var m2);
        Assert.InRange(m1, 0.98, 1.02);
        Assert.InRange(m2, 0.99, 1.01);
        Assert.True(lo > 2 * hi, $"speed spread: dribbling 10 {lo:F3}, 95 {hi:F3}");
        Assert.InRange(lo, 0.10 * 0.4 * 0.8, 0.10 * 0.4 * 1.2);   // lerp(0.10, 0.02) at dribbling 10 is about 0.093, times 0.4 at rest
    }

    [Fact]
    public void FirstTouchNeedsReachHeightAndAStickOrAMovingBall()
    {
        var p = TestData.Outfield(TestData.Sheet(60));
        var b = new Ball(T.Ball);
        var go = new TickIntent { MoveX = 1 };
        TouchResult? Touch(TickIntent it) => TouchPlanner.Step(p, it, b, false, TouchStepMode.Normal, 120, 1f, T, null, new SimRng(1));
        b.Position = new Vector3(TouchModel.FootReach(p.Sheet, T) + 0.05f, T.Ball.Radius, 0);
        Assert.Null(Touch(go));                      // out of reach
        b.Position = new Vector3(0.4f, T.Touch.Profiles.TrapHigh.MaxBallHeight + 0.05f, 0);
        Assert.Null(Touch(go));                      // too high for any trap
        b.Position = new Vector3(0.4f, T.Ball.Radius, 0);
        Assert.Null(Touch(default));                 // no stick and a still ball: no touch
        var r = Touch(go);
        Assert.NotNull(r);
        Assert.Equal(ActionKind.First, r!.Value.Kind);
        Assert.True(b.Velocity.X > 1f);
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
                var p = TestData.Outfield(s);
                p.Velocity = new Vector3(3f, 0, 0);
                p.RunSpeed = 3f;
                var b = new Ball(T.Ball) { Position = new Vector3(0.4f, T.Ball.Radius, 0), Velocity = new Vector3(-9f, 0, 0) };
                var r = TouchPlanner.Step(p, new TickIntent { MoveX = 1 }, b, false, TouchStepMode.Normal, 120, 1f, T, null, new SimRng(i));
                Assert.NotNull(r);
                Assert.Equal(ActionKind.First, r!.Value.Kind);
                sum += MathF.Abs(r.Value.DirectionErrorDeg);
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

    [Fact]
    public void PushSolveLandsTheBallOnTheTargetWithTheSkid()
    {
        foreach (float lift in new[] { 0f, 3f, 8f })
            foreach (var (dist, ticks) in new[] { (1.5f, 37), (3.5f, 75), (8f, 130) })
            {
                var b = new Ball(T.Ball) { Position = new Vector3(0f, T.Ball.Radius, 0f) };
                var target = new Vector3(dist, T.Ball.Radius, 0f);
                float u = TouchPlanner.SolvePush(b, target, ticks, lift, 0.5f, 20f, T, out var dir, out bool tooShort, out float residual);
                Assert.False(tooShort);
                float e = lift * MathF.PI / 180f;
                b.Velocity = dir * (u * MathF.Cos(e)) + new Vector3(0, u * MathF.Sin(e), 0);
                for (int i = 0; i < ticks; i++) b.Step(T.Dt, null, null);
                Assert.InRange(b.Position.X, dist - MathF.Max(0.03f, residual + 1e-3f), dist + MathF.Max(0.03f, residual + 1e-3f));
                if (lift == 0f) Assert.True(residual < 0.01f, $"ground push {dist} m in {ticks} ticks: residual {residual:F3}");
            }
    }
}
