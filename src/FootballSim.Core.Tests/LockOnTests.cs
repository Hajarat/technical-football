using System.Numerics;
using FootballSim.Core;
using Xunit;
using static FootballSim.Core.Tests.TestData;
using static FootballSim.Core.Tests.LockOnLab;

namespace FootballSim.Core.Tests;

/// <summary>The claim on a loose ball: the solver, the holder rule, the stick slide, the mode and how a claim ends (see <see cref="LockOn"/>).</summary>
public class LockOnTests
{
    static (BallForecast F, Ball Ball, Tuning T) Path(Vector3 pos, Vector3 vel, int now = 1)
    {
        var t = new Tuning();
        var ball = new Ball(t.Ball) { Position = new Vector3(pos.X, t.Ball.Radius, pos.Z), Velocity = vel };
        ball.Spin = ball.RollingSpin(vel);
        var f = new BallForecast();
        f.Build(ball, null, now - 1, now - 1 + 480, t.Dt);
        return (f, ball, t);
    }

    [Fact]
    public void TheSolverGivesTheRunsOwnArrivalAtAStillBall()
    {
        var (f, _, t) = Path(new Vector3(6f, 0f, 0f), Vector3.Zero);
        var p = Outfield();
        var m = LockOn.Solve(p, f, 1, 1, 481, 0f, t, null);
        Assert.True(m.Found);
        Assert.Equal(6f, m.Point.X, 3);
        // He really is there by then: his run stepped tick by tick with the holder's legs.
        float reach = LockOn.ReachAt(p.Sheet, m.Point.Y, t);
        int ticks = 0;
        while (TouchModel.HorizontalDistance(p.Position, m.Point) > reach && ticks < 600)
        {
            var legs = LockOn.Legs(p, m.Point, Vector3.Zero, true, reach, 0, 0, t);
            Movement.Step(p, legs, t.Dt, ticks * t.Dt, new MoveAssist(Movement.TopSpeed(p.Sheet, t), 1f + t.Possession.OffBallTurnBonus, Vector2.Zero, TurnAbout: true), t, null);
            ticks++;
        }
        Assert.InRange(ticks, m.ArriveTick - 6, m.ArriveTick + 6);
        // A nearer player is there sooner; a window that closes before he can be there finds nothing.
        var near = LockOn.Solve(Outfield(x: 3f), f, 1, 1, 481, 0f, t, null);
        Assert.True(near.ArriveTick < m.ArriveTick);
        Assert.False(LockOn.Solve(Outfield(), f, 1, 1, 30, 0f, t, null).Found);
    }

    [Fact]
    public void TheSolverTakesTheEarliestSampleOfARollingBallHeCanReach()
    {
        // Rolling across his front: he meets it on its way, well before it stops, and no earlier sample was his.
        var (f, _, t) = Path(new Vector3(8f, 0f, -6f), new Vector3(0f, 0f, 5f));
        var p = Outfield();
        var m = LockOn.Solve(p, f, 1, 1, 481, 0f, t, null);
        Assert.True(m.Found);
        Assert.True(f.RestIndex < 0 || m.ArriveTick < f.StartTick + f.RestIndex);
        float reach = LockOn.ReachAt(p.Sheet, m.Point.Y, t);
        for (int a = m.ArriveTick - 12; a < m.ArriveTick - 1; a += 3)
            Assert.Equal(int.MaxValue, LockOn.RunTicks(p, f.PositionAt(a - 1), f.VelocityAt(a - 1), false, reach, a, LockOnRun.Attacker, 0f, t, null));
        // From a later tick on (a queued action): the first sample from there.
        var later = LockOn.Solve(p, f, 1, m.ArriveTick + 40, 481, 0f, t, null);
        Assert.True(later.Found);
        Assert.Equal(m.ArriveTick + 40, later.ArriveTick);
    }

    [Fact]
    public void ABallTooHighIsNotASampleHePlays()
    {
        // Dropping from four metres: only the samples at a playable height count.
        var t = new Tuning();
        var ball = new Ball(t.Ball) { Position = new Vector3(1f, 4f, 0f), Velocity = Vector3.Zero };
        var f = new BallForecast();
        f.Build(ball, null, 0, 480, t.Dt);
        var m = LockOn.Solve(Outfield(), f, 1, 1, 481, 0f, t, null);
        Assert.True(m.Found);
        Assert.True(m.Point.Y <= LockOn.PlayableHeight(t));
        Assert.True(m.ArriveTick > 30);
    }

    [Fact]
    public void InReachAtStart()
    {
        // The ball already within reach on the tick the claim starts: the meeting is now, and it is one touch, not a run.
        var (f, _, t) = Path(new Vector3(0.4f, 0f, 0f), Vector3.Zero);
        var m = LockOn.Solve(Outfield(), f, 1, 1, 481, 0f, t, null);
        Assert.True(m.Found);
        Assert.Equal(1, m.ArriveTick);
        Assert.Equal(0, m.RunTicks);

        var w = Open();
        Loose(w, 0.4f, 0f);
        var start = w.Attacker.Position;
        Assert.Equal(1, TicksToRegain(w, default, 1));
        Assert.Single(w.Events, e => e.Kind == EventKind.Touch);
        Assert.True(Vector3.Distance(start, w.Attacker.Position) < 0.05f);
    }

    /// <summary>One Update over a synthetic team 0 of two outfield players and an empty team 1.</summary>
    static LockOnClaim[] Update(BallForecast f, Ball ball, LockOnClaim[] claims, PlayerState a, PlayerState b, int now, Tuning t,
        TickIntent intentA = default, bool stick = false)
    {
        a.LockOnSolve = default; b.LockOnSolve = default;
        var candidates = new IReadOnlyList<LockOnCandidate>[]
        {
            new[] { new LockOnCandidate(a, intentA, stick, LockOnRun.Attacker), new LockOnCandidate(b, default, false, LockOnRun.Attacker) },
            Array.Empty<LockOnCandidate>(),
        };
        var teams = new IReadOnlyList<PlayerState>[] { new[] { a, b }, Array.Empty<PlayerState>() };
        LockOn.Update(f, claims, candidates, teams, null, ball, null, true, now, now * t.Dt, t);
        return claims;
    }

    [Fact]
    public void ATeamMateTakesTheClaimOnlyByArrivingTheMarginEarlier()
    {
        var (f, ball, t) = Path(new Vector3(10f, 0f, 0f), Vector3.Zero);
        int margin = (int)MathF.Round(t.LockOn.TakeoverMarginSeconds * t.TickRate);
        var a = new PlayerState(0, 0, PlayerRole.Outfield, Sheet()) { Position = new Vector3(5f, 0f, 0f) };
        var b = new PlayerState(0, 1, PlayerRole.Outfield, Sheet()) { Position = new Vector3(4f, 0f, 0.5f) };
        var claims = new LockOnClaim[2];
        Update(f, ball, claims, a, b, 1, t);
        Assert.Same(a, claims[0].Player);
        Assert.NotEqual(RegainMode.None, a.Regain);
        Assert.Equal(RegainMode.None, b.Regain);
        Assert.Equal(-1, b.LockOnTick);
        Assert.Null(claims[1].Player);

        int Arrive(PlayerState p) => LockOn.Solve(p, f, 1, 1, 481, t.Dt, t, null).ArriveTick;
        // The team-mate a little nearer than the holder, inside the margin: the holder keeps it.
        b.Position = new Vector3(5.3f, 0f, 0.5f);
        Assert.InRange(Arrive(a) - Arrive(b), 1, margin - 1);
        Update(f, ball, claims, a, b, 1, t);
        Assert.Same(a, claims[0].Player);
        // The margin earlier or more: he takes it, and with no holder the earliest takes it at once.
        b.Position = new Vector3(8f, 0f, 0.5f);
        Assert.True(Arrive(a) - Arrive(b) >= margin);
        Update(f, ball, claims, a, b, 1, t);
        Assert.Same(b, claims[0].Player);
        Assert.Equal(RegainMode.None, a.Regain);
        b.Position = new Vector3(5.3f, 0f, 0.5f);
        var fresh = new LockOnClaim[2];
        Update(f, ball, fresh, a, b, 1, t);
        Assert.Same(b, fresh[0].Player);
    }

    [Fact]
    public void TheStickSlidesTheMeetingToALaterSampleWithinItsAngle()
    {
        var (f, ball, t) = Path(new Vector3(8f, 0f, -6f), new Vector3(0f, 0f, 5f));
        var a = new PlayerState(0, 0, PlayerRole.Outfield, Sheet()) { Position = new Vector3(2f, 0f, 0f) };
        var far = new PlayerState(0, 1, PlayerRole.Outfield, Sheet()) { Position = new Vector3(-30f, 0f, 0f) };
        var claims = new LockOnClaim[2];
        Update(f, ball, claims, a, far, 1, t);
        int earliest = claims[0].ArriveTick;
        Assert.False(claims[0].Slid);
        // The stick toward the sample one second on: that is the meeting.
        var target = f.PositionAt(earliest - 1 + 120);
        var dir = Vector2.Normalize(new Vector2(target.X - a.Position.X, target.Z - a.Position.Z));
        Update(f, ball, claims, a, far, 1, t, new TickIntent { MoveX = dir.X, MoveZ = dir.Y }, stick: true);
        Assert.True(claims[0].Slid);
        Assert.InRange(claims[0].ArriveTick, earliest + 110, earliest + 130);
        Assert.True(a.LockOnSlack > 20);
        // The stick further than the slide angle from every later sample: the earliest stands.
        Update(f, ball, claims, a, far, 1, t, new TickIntent { MoveX = -1f }, stick: true);
        Assert.False(claims[0].Slid);
        Assert.Equal(earliest, claims[0].ArriveTick);
        // The stick is not read for a candidate whose stick does not choose (a bot, or a stick that aims a shot).
        Update(f, ball, claims, a, far, 1, t, new TickIntent { MoveX = dir.X, MoveZ = dir.Y }, stick: false);
        Assert.Equal(earliest, claims[0].ArriveTick);
    }

    [Fact]
    public void TheHoldersLegsGoToTheMeetingWhateverTheStickSays()
    {
        // The ball to his right, the stick held straight on: he turns to the ball.
        var w = Open();
        Loose(w, 0f, 6f);
        var buf = new TickIntent[w.PlayerCount];
        for (int i = 0; i < 90; i++) { buf[0] = new TickIntent { MoveX = 1f }; w.Step(buf); }
        Assert.True(w.Attacker.Position.Z > 1f);
        Assert.True(w.HoldsClaim(w.Attacker));
        Assert.True(w.Attacker.LockOnTick >= w.Tick);
        Assert.Equal(w.Claims[0].Point, w.Attacker.LockOnPoint);
        // What the view and the keeper read: his meeting is his next touch.
        Assert.False(float.IsNaN(w.Attacker.NextTouchEta));
        Assert.Equal(w.Attacker.LockOnPoint, w.Attacker.NextTouchPoint);
        Assert.Equal((w.Attacker.LockOnTick - w.Tick) * w.Tuning.Dt, w.KeeperSituation().AttackerNextTouch, 4);
    }

    [Fact]
    public void AClaimEndsWithAControllerOutsideLivePlayOrWhenTheHolderCannotGo()
    {
        var buf = new TickIntent[2];
        World Going()
        {
            var w = Open();
            Loose(w, 8f, 0f);
            for (int i = 0; i < 10; i++) w.Step(buf);
            Assert.True(w.HoldsClaim(w.Attacker));
            Assert.True(w.Attacker.Speed > 0.5f);
            return w;
        }
        // Stunned: no longer a candidate, and his legs are the stick's again on the same tick.
        var stunned = Going();
        stunned.Attacker.StunUntil = stunned.SimTime + 1f;
        stunned.Step(buf);
        Assert.Null(stunned.Claims[0].Player);
        Assert.Equal(RegainMode.None, stunned.Attacker.Regain);
        Assert.Equal(-1, stunned.Attacker.LockOnTick);
        // Stumbling, and just tackled.
        var stumbling = Going();
        stumbling.Attacker.StumbleUntil = stumbling.SimTime + 1f;
        stumbling.Step(buf);
        Assert.Null(stumbling.Claims[0].Player);
        var tackled = Going();
        tackled.Attacker.TackledLockUntil = tackled.SimTime + 1f;
        tackled.Step(buf);
        Assert.Null(tackled.Claims[0].Player);
        // He lets it run.
        var letting = Going();
        buf[0] = new TickIntent { MoveX = -1f, Finesse = true };
        letting.Step(buf);
        Assert.Null(letting.Claims[0].Player);
        buf[0] = default;
        // The ball is controlled: by him, at the meeting.
        var got = Going();
        Assert.InRange(TicksToRegain(got, default, 600), 1, 600);
        got.Step(buf);
        Assert.Null(got.Claims[0].Player);
        Assert.False(got.Forecast.Valid);
    }

    [Fact]
    public void TheDefenderHoldsHisTeamsClaimAndTheEarlierPlayerPlaysTheBall()
    {
        // The defender much nearer a still ball: he is first to it and ends the round; the attacker ran all the same.
        var w = Arena(12f, 0f, 0f, 0f, 5f, 0f, 0f, (14f, 0f));
        var buf = new TickIntent[w.PlayerCount];
        w.Step(buf);
        Assert.Same(w.Attacker, w.Claims[0].Player);
        Assert.Same(w.Defender, w.Claims[1].Player);
        Assert.True(w.Claims[1].ArriveTick < w.Claims[0].ArriveTick);
        Assert.Equal(RegainMode.Contested, w.Attacker.Regain);
        Assert.Equal(w.Claims[1].ArriveTick, w.Claims[0].OpponentTick);
        float x0 = w.Attacker.Position.X;
        for (int i = 0; i < 600 && w.State is RoundState.Live or RoundState.Strike; i++) w.Step(buf);
        Assert.Equal(Outcome.Dispossessed, w.Outcome);
        Assert.True(w.Attacker.Position.X > x0 + 1f);
        // The attacker much nearer: his touch comes first.
        var v = Arena(12f, 0f, 0f, 0f, 10f, 0f, 0f, (20f, 0f));
        Assert.InRange(TicksToRegain(v, default, 600), 1, 600);
    }
}
