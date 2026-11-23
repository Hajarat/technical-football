using System.Numerics;
using FootballSim.Core;
using Xunit;
using static FootballSim.Core.Tests.TestData;
using static FootballSim.Core.Tests.LockOnLab;

namespace FootballSim.Core.Tests;

/// <summary>The loose ball's forecast path (see <see cref="BallForecast"/>).</summary>
public class BallForecastTests
{
    static (Ball Ball, Pitch Pitch, Tuning T) Rolling(Vector3 pos, Vector3 vel)
    {
        var t = new Tuning();
        var pitch = new Pitch(Scenario("loose_parry").Pitch, t.Frame, t.Arena);
        var ball = new Ball(t.Ball) { Position = new Vector3(pos.X, t.Ball.Radius, pos.Z), Velocity = vel };
        ball.Spin = ball.RollingSpin(vel);
        return (ball, pitch, t);
    }

    [Fact]
    public void MatchesTheRealBallTickForTickThroughABoardBounce()
    {
        var (ball, pitch, t) = Rolling(new Vector3(15f, 0f, 0f), new Vector3(1f, 0f, 14f));
        var f = new BallForecast();
        f.Build(ball, pitch, 100, 100 + 480, t.Dt);
        Assert.Equal(100, f.StartTick);
        Assert.Equal(ball.Position, f.PositionAt(100));
        bool board = false;
        for (int i = 1; i <= 480; i++)
        {
            board |= ball.Step(t.Dt, pitch, null).BoardHit;
            Assert.True(f.Has(100 + i));
            Assert.Equal(ball.Position, f.PositionAt(100 + i));
            Assert.Equal(ball.Velocity, f.VelocityAt(100 + i));
            Assert.True(f.Matches(ball, 100 + i, 0.05f));
        }
        Assert.True(board, "the ball should have met a board inside four seconds");
    }

    [Fact]
    public void ItGrowsATickAtATimeAndStopsAtRest()
    {
        var (ball, pitch, t) = Rolling(new Vector3(10f, 0f, 0f), new Vector3(2f, 0f, 0f));
        var f = new BallForecast();
        f.Build(ball, pitch, 0, 60, t.Dt);
        Assert.Equal(60, f.LastTick);
        Assert.False(f.Has(61));
        f.Extend(61);
        Assert.Equal(61, f.LastTick);
        f.Extend(100000);
        Assert.True(f.RestIndex > 0);
        Assert.True(f.RestingAt(f.StartTick + f.RestIndex));
        Assert.False(f.RestingAt(f.StartTick + f.RestIndex - 1));
        // A resting ball has every later tick, at the spot it stopped on.
        Assert.True(f.Has(1_000_000));
        Assert.Equal(f.PositionAt(f.LastTick), f.PositionAt(1_000_000));
    }

    [Fact]
    public void ABallSomebodyPlayedHasLeftThePath()
    {
        var (ball, pitch, t) = Rolling(new Vector3(10f, 0f, 0f), new Vector3(5f, 0f, 0f));
        var f = new BallForecast();
        f.Build(ball, pitch, 0, 480, t.Dt);
        for (int i = 1; i <= 30; i++) ball.Step(t.Dt, pitch, null);
        Assert.True(f.Matches(ball, 30, 0.05f));
        // Off its sample by more than the drift.
        var at = ball.Position;
        ball.Position = at + new Vector3(0f, 0f, 0.06f);
        Assert.False(f.Matches(ball, 30, 0.05f));
        ball.Position = at + new Vector3(0f, 0f, 0.04f);
        Assert.True(f.Matches(ball, 30, 0.05f));
        // On its sample but going another way (a touch on this tick).
        ball.Position = at;
        ball.Velocity = new Vector3(0f, 0f, 5f);
        Assert.False(f.Matches(ball, 30, 0.05f));
        // The wrong tick, and a cleared forecast, match nothing.
        Assert.False(new BallForecast().Matches(ball, 30, 0.05f));
    }

    [Fact]
    public void EndsInGoal()
    {
        // A ball rolling into the net: the forecast ends there, nobody claims it and nobody runs into the goal after it.
        var (ball, pitch, t) = Rolling(new Vector3(pitchGoal - 3f, 0f, 0.5f), new Vector3(6f, 0f, 0f));
        var f = new BallForecast();
        f.Build(ball, pitch, 0, 480, t.Dt);
        Assert.True(f.EndsInGoal);
        Assert.Equal(f.StartTick + f.GoalIndex, f.LastTick);
        Assert.False(f.Has(f.LastTick + 1));

        var w = Arena(pitchGoal - 3f, 0.5f, 6f, 0f, pitchGoal - 6f, 0.5f);
        var buf = new TickIntent[w.PlayerCount];
        var start = w.Attacker.Position;
        for (int i = 0; i < 240 && w.State == RoundState.Live; i++)
        {
            w.Step(buf);
            Assert.Null(w.Claims[0].Player);
            Assert.Equal(RegainMode.None, w.Attacker.Regain);
        }
        Assert.Equal(Outcome.Goal, w.Outcome);
        Assert.True(Vector3.Distance(start, w.Attacker.Position) < 0.05f);
    }

    const float pitchGoal = 30f;

    [Fact]
    public void TheWorldRebuildsOnANewBallPathAndClearsItWhenTheBallIsControlled()
    {
        var w = Open();
        Loose(w, 6f, 0f, 3f, 0f);
        var buf = new TickIntent[w.PlayerCount];
        w.Step(buf);
        Assert.True(w.Forecast.Valid);
        int built = w.Forecast.StartTick;
        for (int i = 0; i < 10; i++) w.Step(buf);
        Assert.Equal(built, w.Forecast.StartTick);
        int since = w.Claims[0].SinceTick;
        // Somebody plays it: a new path, and a new claim.
        w.Ball.Velocity = new Vector3(0f, 0f, 4f);
        w.Step(buf);
        Assert.True(w.Forecast.StartTick > built);
        Assert.True(w.Claims[0].SinceTick > since);
        Assert.InRange(TicksToRegain(w, default, 600), 1, 600);
        w.Step(buf);
        Assert.False(w.Forecast.Valid);
        Assert.Null(w.Claims[0].Player);
    }
}
