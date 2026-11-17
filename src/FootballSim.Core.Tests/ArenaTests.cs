using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>The walled arena: the ball off the boards and ceiling, players kept inside, and rounds that play on after a miss.</summary>
public class ArenaTests
{
    static readonly Tuning T = new();
    static readonly float R = T.Ball.Radius;
    static readonly Pitch P = new(TestData.Scenario("penalty").Pitch, T.Frame, T.Arena);

    /// <summary>True when a ball centre is inside the outline (inset by the ball radius), or in the goal mouth.</summary>
    static bool Inside(Vector3 c, float inset, float eps = 1e-3f)
    {
        if (c.X > P.GoalLineX - inset && MathF.Abs(c.Z) < P.GoalHalfWidth && c.Y < P.BarHeight) return true; // the goal mouth
        if (c.X < inset - eps || c.X > P.GoalLineX - inset + eps) return false;
        if (MathF.Abs(c.Z) > P.HalfWidth - inset + eps) return false;
        if (c.Y > P.Ceiling - inset + eps) return false;
        // Chamfers: x + |z| <= L + W/2 - C, offset by the inset along the board normal.
        if (c.X + MathF.Abs(c.Z) > P.GoalLineX + P.HalfWidth - P.GoalChamfer - inset * MathF.Sqrt(2f) + eps) return false;
        // Rounded back corners.
        var centre = new Vector2(P.CornerRadius, MathF.Sign(c.Z) * (P.HalfWidth - P.CornerRadius));
        if (c.X < centre.X && MathF.Abs(c.Z) > MathF.Abs(centre.Y)
            && (new Vector2(c.X, c.Z) - centre).Length() > P.CornerRadius - inset + eps) return false;
        return true;
    }

    static Ball BallAt(Vector3 pos, Vector3 vel) => new(T.Ball) { Position = pos, Velocity = vel };

    [Fact]
    public void ArenaShapeComesFromTheScenarioAndTuning()
    {
        Assert.Equal(30f, P.GoalLineX);
        Assert.Equal(10f, P.HalfWidth);
        Assert.Equal(6f, P.CornerRadius);
        Assert.Equal(4f, P.GoalChamfer);
        Assert.Equal(8f, P.Ceiling);
        Assert.Equal(1f, P.BoardHeight);
        Assert.Equal(0.70f, P.BoardRestitution);
        Assert.Equal(0.30f, P.BoardFriction);
        Assert.Equal(6f, P.EndWallHalfWidth);
    }

    [Fact]
    public void FastBallNeverTunnelsThroughAnyBoard()
    {
        // 36 m/s (0.30 m per tick) in every direction from the middle, low and rising: the ball stays inside the outline on every tick.
        int hits = 0;
        for (int deg = 0; deg < 360; deg += 5)
        {
            float a = deg * MathF.PI / 180f;
            foreach (float vy in new[] { 0f, 4f, 14f })
            {
                var b = BallAt(new Vector3(15f, 1f, 0f), new Vector3(36f * MathF.Cos(a), vy, 36f * MathF.Sin(a)));
                for (int i = 0; i < 360; i++)
                {
                    var res = b.Step(T.Dt, P, null);
                    if (res.BoardHit) hits++;
                    if (P.IsGoal(b.Position, R)) break; // straight into the goal: nothing to stop it here
                    Assert.True(Inside(b.Position, R), $"{deg} deg vy {vy}: ball outside at {b.Position} on tick {i}");
                }
            }
        }
        Assert.True(hits > 100);
    }

    [Fact]
    public void BallOneTickFromABoardAtFullSpeedIsStoppedThatTick()
    {
        // The end point of the tick is 0.25 m beyond the board; the sweep finds the contact on the path.
        var b = BallAt(new Vector3(15f, 1f, -P.HalfWidth + R + 0.05f), new Vector3(0f, 0f, -36f));
        var res = b.Step(T.Dt, P, null);
        Assert.True(res.BoardHit);
        Assert.True(b.Velocity.Z > 0f);
        Assert.True(Inside(b.Position, R));
    }

    [Fact]
    public void HeadOnHitKeepsSeventyPercentOfTheSpeed()
    {
        foreach (var (pos, vel, axis) in new[]
        {
            (new Vector3(15f, 1.5f, -P.HalfWidth + R + 0.1f), new Vector3(0f, 0f, -20f), 2),   // side wall
            (new Vector3(R + 0.1f, 1.5f, 0f), new Vector3(-20f, 0f, 0f), 0),                  // back wall
            (new Vector3(P.GoalLineX - R - 0.1f, 1.5f, 5f), new Vector3(20f, 0f, 0f), 0),     // end wall beside the goal
        })
        {
            var b = BallAt(pos, vel);
            var res = b.Step(T.Dt, P, null);
            Assert.True(res.BoardHit);
            float vin = axis == 0 ? vel.X : vel.Z, vout = axis == 0 ? b.Velocity.X : b.Velocity.Z;
            Assert.InRange(-vout / vin, 0.68f, 0.71f); // 0.70 less a tick of drag
        }
    }

    [Fact]
    public void ChamferSendsAWideBallBackTowardTheMiddle()
    {
        // Rolling wide of the goal toward the +Z goal-end corner.
        var b = BallAt(new Vector3(22f, R, 5f), new Vector3(15f, 0f, 8f));
        b.Spin = b.RollingSpin(b.Velocity);
        BallStepResult hit = default;
        for (int i = 0; i < 240 && !hit.BoardHit; i++) hit = b.Step(T.Dt, P, null);
        Assert.True(hit.BoardHit);
        Assert.True(hit.BoardPoint.X > P.GoalLineX - P.GoalChamfer && hit.BoardPoint.Z < P.HalfWidth - R - 0.01f, $"hit at {hit.BoardPoint}");
        Assert.True(b.Velocity.Z < 0f, "back toward the middle");
        Assert.True(b.Velocity.X < 0f, "and away from the goal end");
    }

    [Fact]
    public void RoundedCornerReflectsAboutItsRadius()
    {
        var b = BallAt(new Vector3(8f, R, 0f), Vector3.Normalize(new Vector3(-1f, 0f, 1f)) * 20f);
        b.Spin = b.RollingSpin(b.Velocity);
        BallStepResult hit = default;
        Vector3 before = default;
        for (int i = 0; i < 240 && !hit.BoardHit; i++) { before = b.Velocity; hit = b.Step(T.Dt, P, null); }
        Assert.True(hit.BoardHit);
        var c = new Vector2(P.CornerRadius, P.HalfWidth - P.CornerRadius);
        var at = new Vector2(hit.BoardPoint.X, hit.BoardPoint.Z);
        Assert.True(at.X < c.X && at.Y > c.Y, $"hit at {at} should be on the arc");
        Assert.Equal(P.CornerRadius - R, (at - c).Length(), 2);
        var n = Vector2.Normalize(c - at);
        var vin = new Vector2(before.X, before.Z);
        var vout = new Vector2(b.Velocity.X, b.Velocity.Z);
        Assert.True(Vector2.Dot(vin, n) < 0f);
        Assert.InRange(Vector2.Dot(vout, n) / -Vector2.Dot(vin, n), 0.65f, 0.72f);
        Assert.True(Inside(b.Position, R));
    }

    [Fact]
    public void CeilingSendsTheBallDown()
    {
        var b = BallAt(new Vector3(15f, 7f, 0f), new Vector3(2f, 20f, 0f));
        BallStepResult hit = default;
        for (int i = 0; i < 60 && !hit.BoardHit; i++) hit = b.Step(T.Dt, P, null);
        Assert.True(hit.BoardHit);
        Assert.True(b.Velocity.Y < 0f);
        Assert.True(b.Position.Y <= P.Ceiling - R + 1e-3f);
        Assert.True(P.IsOverContact(hit.BoardPoint, R));
    }

    [Fact]
    public void BallAboveTheBarInTheGoalMouthComesBack()
    {
        var b = BallAt(new Vector3(28f, 3.5f, 0f), new Vector3(20f, 3f, 0f));
        BallStepResult hit = default;
        for (int i = 0; i < 60 && !hit.BoardHit; i++)
        {
            hit = b.Step(T.Dt, P, null);
            Assert.True(b.Position.X <= P.GoalLineX - R + 1e-3f);
        }
        Assert.True(hit.BoardHit);
        Assert.True(b.Velocity.X < 0f);
        Assert.True(P.IsOverContact(hit.BoardPoint, R));
    }

    [Fact]
    public void BallUnderTheBarInTheGoalMouthGoesIn()
    {
        var b = BallAt(new Vector3(28f, 1f, 0f), new Vector3(20f, 0f, 0f));
        bool goal = false;
        for (int i = 0; i < 60 && !goal; i++)
        {
            Assert.False(b.Step(T.Dt, P, null).BoardHit);
            goal = P.IsGoal(b.Position, R);
        }
        Assert.True(goal);
    }

    [Fact]
    public void PlayersStayInsideTheOutlineAndStopAtTheEndWall()
    {
        var s = TestData.Sheet(99);
        foreach (var (start, stick) in new[]
        {
            (new Vector2(5f, 5f), new Vector2(-1f, 1f)),   // into the rounded back corner
            (new Vector2(25f, 5f), new Vector2(1f, 1f)),   // into the chamfer
            (new Vector2(25f, 0f), new Vector2(1f, 0f)),   // into the goal mouth
            (new Vector2(15f, 0f), new Vector2(0f, -1f)),  // into the side wall
        })
        {
            var p = TestData.Outfield(s, start.X, start.Y, MathF.Atan2(stick.Y, stick.X));
            float now = 0f;
            for (int i = 0; i < 480; i++)
            {
                now += T.Dt;
                Movement.Step(p, new TickIntent { MoveX = stick.X, MoveZ = stick.Y, Sprint = 1 }, T.Dt, now, false, T, P);
                Assert.True(Inside(p.Position with { Y = 0f }, Pitch.PlayerRadius) && p.Position.X <= P.GoalLineX - Pitch.PlayerRadius + 1e-3f,
                    $"player outside at {p.Position}");
            }
            // Pressed into the board, he has no velocity into it.
            var probe = p.Position + p.Velocity * 0.05f;
            Assert.True(Inside(probe with { Y = 0f }, Pitch.PlayerRadius, 0.02f), $"velocity {p.Velocity} still points into the board");
        }
        var mouth = new Vector3(P.GoalLineX + 1f, 0f, 0f);
        var v = new Vector3(5f, 0f, 0f);
        Assert.True(P.ClampPlayer(ref mouth, ref v));
        Assert.Equal(P.GoalLineX - Pitch.PlayerRadius, mouth.X, 4);
        Assert.Equal(0f, v.X);
    }

    [Fact]
    public void WideShotScoresAsWideAndPlayGoesOnUntilTheClock()
    {
        // Aim 3 m outside the post: the shot comes back off the end-wall boards, the round plays on and ends on the 20 s clock.
        var sc = TestData.Scenario("penalty");
        var att = TestData.Player("striker_a");
        var kee = TestData.Player("the_wall");
        int wide = 0, timeUp = 0;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var first = RoundRunner.RunFirstShot(T, sc, att, kee, seed, new ScriptedAttacker { AimInset = -3f });
            Assert.NotEqual(ShotEnd.Out, first.End);
            if (first.End != ShotEnd.Wide) continue;
            wide++;
            var report = RoundRunner.RunToReport(T, sc, att, kee, seed, out _, new ScriptedAttacker { AimInset = -3f });
            Assert.NotEqual(Outcome.Wide, report.Outcome);
            Assert.NotEqual(Outcome.Out, report.Outcome);
            if (report.Outcome == Outcome.TimeUp)
            {
                timeUp++;
                Assert.InRange(report.Clock, 19.99f, 20.01f);
            }
        }
        Assert.True(wide >= 10, $"wide {wide}");
        Assert.True(timeUp >= 1, $"time up {timeUp}");
    }

    [Fact]
    public void AttackerCanShootAgainAfterAMiss()
    {
        var w = TestData.Live("penalty");
        var buf = new TickIntent[w.PlayerCount];
        // First shot: hard and wide, off the boards.
        for (int i = 0; i < 60; i++) { buf[0] = new TickIntent { ShootHeld = true }; w.Step(buf); }
        buf[0] = new TickIntent { MoveX = 1f, MoveZ = 0.6f };
        w.Step(buf);
        buf[0] = default;
        for (int i = 0; i < 600 && !w.Events.Any(e => e.Kind == EventKind.BoardHit); i++) w.Step(buf);
        Assert.Single(w.Shots);
        Assert.Equal(RoundState.Live, w.State);
        // Put the ball back at the attacker's feet and shoot again: a second strike happens in the same round.
        w.Ball.Position = new Vector3(w.Attacker.Position.X + 0.5f, R, w.Attacker.Position.Z);
        w.Ball.Velocity = Vector3.Zero;
        w.Ball.Spin = Vector3.Zero;
        for (int i = 0; i < 40; i++) { buf[0] = new TickIntent { ShootHeld = true }; w.Step(buf); }
        buf[0] = new TickIntent { MoveX = 1f };
        w.Step(buf);
        buf[0] = default;
        for (int i = 0; i < 60 && w.Shots.Count < 2; i++) w.Step(buf);
        Assert.Equal(2, w.Shots.Count);
    }

    [Fact]
    public void KeeperClaimsASlowLooseBallNearHimPlaceholder()
    {
        var w = TestData.Live("edge_of_box");
        var k = w.Keeper.Position;
        w.Ball.Position = new Vector3(k.X - 1f, R, k.Z);
        w.Ball.Velocity = new Vector3(-1f, 0f, 0f);
        w.Step(new TickIntent[w.PlayerCount]);
        Assert.Equal(RoundState.Outcome, w.State);
        Assert.Equal(Outcome.Held, w.Outcome);
        Assert.Equal(w.Keeper, w.BallController);
        Assert.Equal(KeeperAction.Holding, w.Keeper.KeeperAction);
        Assert.Equal(w.Keeper.HandPosition, w.Ball.Position);
    }

    [Theory]
    [InlineData(1f, 0.11f, 6f)]   // too fast
    [InlineData(1f, 1.8f, 0f)]    // too high
    [InlineData(4f, 0.11f, 0f)]   // out of reach
    public void KeeperLeavesABallHeCannotClaim(float distance, float height, float speed)
    {
        var w = TestData.Live("edge_of_box");
        var k = w.Keeper.Position;
        w.Ball.Position = new Vector3(k.X - distance, height, k.Z + 0.01f);
        w.Ball.Velocity = new Vector3(0f, 0f, speed);
        w.Step(new TickIntent[w.PlayerCount]);
        Assert.Equal(RoundState.Live, w.State);
        Assert.Null(w.BallController);
    }
}
