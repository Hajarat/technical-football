using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

public class PitchTests
{
    static readonly Tuning T = new();
    static readonly Pitch P = new(new PitchSpec(), T.Frame);
    static readonly float R = T.Ball.Radius;

    [Fact]
    public void GoalNeedsTheWholeBallOverTheLine()
    {
        Assert.True(P.IsGoal(new Vector3(P.GoalLineX + R + 0.01f, 1f, 0f), R));
        Assert.False(P.IsGoal(new Vector3(P.GoalLineX + R - 0.02f, 1f, 0f), R));
        Assert.False(P.IsGoal(new Vector3(P.GoalLineX + 0.0f, 1f, 0f), R)); // centre on the line
    }

    [Fact]
    public void BallStoppedOnTheLineIsNotAGoal()
    {
        Assert.False(P.IsGoal(new Vector3(P.GoalLineX + 0.05f, R, 0f), R));
    }

    [Fact]
    public void GoalNeedsTheWholeBallBetweenThePostsAndUnderTheBar()
    {
        float x = P.GoalLineX + R + 0.05f;
        Assert.True(P.IsGoal(new Vector3(x, 1f, P.GoalHalfWidth - R - 0.01f), R));
        Assert.False(P.IsGoal(new Vector3(x, 1f, P.GoalHalfWidth - R + 0.02f), R));
        Assert.True(P.IsGoal(new Vector3(x, P.BarHeight - R - 0.01f, 0f), R));
        Assert.False(P.IsGoal(new Vector3(x, P.BarHeight - R + 0.02f, 0f), R));
    }

    [Fact]
    public void SweptTestHitsAPostThatBothEndpointsMiss()
    {
        // The path crosses the post but both end points are clear of it (0.40 m path, a post plus ball is 0.17 m).
        var from = new Vector3(P.GoalLineX - 0.2f, 1f, P.GoalHalfWidth);
        var to = new Vector3(P.GoalLineX + 0.2f, 1f, P.GoalHalfWidth);
        float reach = R + P.PostRadius;
        Assert.True(MathF.Abs(from.X - P.GoalLineX) > reach && MathF.Abs(to.X - P.GoalLineX) > reach);
        Assert.True(P.SweepFrame(from, to, R, out var hit));
        Assert.InRange(hit.T, 0.05f, 0.5f);
        Assert.True(hit.Normal.X < -0.9f);
        Assert.Equal(P.GoalLineX - reach, hit.Point.X, 3);
    }

    [Fact]
    public void SweptTestHitsTheCrossbar()
    {
        var from = new Vector3(P.GoalLineX - 0.3f, P.BarHeight + 0.01f, 0f);
        var to = new Vector3(P.GoalLineX + 0.3f, P.BarHeight + 0.01f, 0f);
        Assert.True(P.SweepFrame(from, to, R, out var hit));
        Assert.True(hit.Normal.X < -0.5f);
    }

    [Fact]
    public void SweptTestMissesAShotBetweenThePosts()
    {
        var from = new Vector3(P.GoalLineX - 0.3f, 1f, 0f);
        var to = new Vector3(P.GoalLineX + 0.3f, 1f, 0f);
        Assert.False(P.SweepFrame(from, to, R, out _));
    }

    [Fact]
    public void BallHittingTheInsideOfAPostRebounds()
    {
        // Travelling toward +Z inside the goal mouth, just behind the line, the ball meets the post's inner face.
        var b = new Ball(T.Ball)
        {
            Position = new Vector3(P.GoalLineX + 0.02f, 1f, P.GoalHalfWidth - 0.5f),
            Velocity = new Vector3(0f, 0f, 10f),
        };
        bool hit = false;
        for (int i = 0; i < 20 && !hit; i++) hit = b.Step(T.Dt, P, null).FrameHit;
        Assert.True(hit);
        Assert.True(b.Velocity.Z < 0f, "ball should come back into the goal mouth");
        Assert.True(MathF.Abs(b.Position.Z) < P.GoalHalfWidth - P.PostRadius);
    }

    [Fact]
    public void PitchSizeIsData()
    {
        var spec = new PitchSpec { Length = 30f, Width = 40f, GoalWidth = 5f, GoalHeight = 2f };
        var p = new Pitch(spec, T.Frame);
        Assert.Equal(30f, p.GoalLineX);
        Assert.Equal(20f, p.HalfWidth);
        Assert.True(p.IsGoal(new Vector3(30.2f, 1f, 0f), R));
        Assert.False(p.IsGoal(new Vector3(30.2f, 1f, 2.6f), R));
    }
}
