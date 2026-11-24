using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>The home keeper: his home depth, set, mirror, pounce and the run back once taken round.</summary>
public class KeeperHomeTests
{
    static readonly Tuning T = new();
    static readonly Pitch P = new(new PitchSpec(), T.Frame);
    static float Dt => T.Dt;
    static readonly PlayerSheet Wall = TestData.Player("the_wall");
    static readonly PlayerSheet Striker = TestData.Player("striker_a");

    static (KeeperBrain Brain, PlayerState Keeper, Ball Ball) Rig(Vector3 ball, Vector3 keeperAt, Tuning? t = null)
    {
        var b = new Ball(T.Ball) { Position = ball };
        var k = new PlayerState(1, 0, PlayerRole.Keeper, Wall) { Position = keeperAt, Facing = MathF.PI };
        return (new KeeperBrain(k, P, t ?? T, false, Striker), k, b);
    }

    static float Depth(PlayerState k) => P.GoalLineX - k.Position.X;
    static Vector3 At(float depth, float z = 0f, float y = 0f) => new(P.GoalLineX - depth, y, z);

    /// <summary>The attacker on the ball, <paramref name="behind"/> metres behind it along his run, next touch <paramref name="next"/> s off.</summary>
    static KeeperSituation OnBall(Vector3 ball, Vector3 vel, float next = 0.05f, int touchTick = 1, float behind = 0.4f, bool covers = false)
    {
        var dir = vel.LengthSquared() > 1e-6f ? Vector3.Normalize(new Vector3(vel.X, 0f, vel.Z)) : new Vector3(1f, 0f, 0f);
        return new KeeperSituation
        {
            AttackerNextTouch = next, AttackerTouchTick = touchTick, AttackerHasBall = true, StrikeIn = float.PositiveInfinity,
            AttackerPosition = new Vector3(ball.X, 0f, ball.Z) - dir * behind, AttackerVelocity = vel, DefenderCovers = covers,
        };
    }

    /// <summary>A carrier running straight at goal from 25 m with the ball at his foot, at <paramref name="speed"/> m/s.</summary>
    static IEnumerable<(KeeperBrain Brain, PlayerState Keeper, Ball Ball, KeeperSituation Sit)> RunIn(float speed, float z = 0f, float until = 3f)
    {
        var start = At(25f, z, 0.11f);
        var (brain, keeper, ball) = Rig(start, Core.Keeper.ArcTarget(start, P, T, Wall));
        var dir = Vector3.Normalize(new Vector3(P.GoalLineX, 0.11f, 0f) - start);
        var vel = new Vector3(dir.X, 0f, dir.Z) * speed;
        var rng = new SimRng(7);
        int tick = 0;
        while (Depth(keeper) + 0.5f < P.GoalLineX - ball.Position.X && Vector2.Distance(Flat(ball.Position), Flat(keeper.Position)) > until)
        {
            ball.Position += vel * Dt;
            ball.Velocity = vel;
            // a close-control dribble: a touch every 0.25 s, the next one never far off
            var sit = OnBall(ball.Position, vel, next: 0.25f - (tick % 30) * Dt, touchTick: tick / 30);
            brain.Update(Dt, ball, rng, sit);
            tick++;
            yield return (brain, keeper, ball, sit);
        }
    }

    static Vector2 Flat(Vector3 v) => new(v.X, v.Z);

    [Theory]
    [InlineData(3f)]
    [InlineData(5f)]
    [InlineData(7.5f)]
    public void HisDepthNeverGoesBeyondHisHomeDepthWithoutAWonRace(float speed)
    {
        foreach (var (brain, keeper, ball, _) in RunIn(speed))
        {
            Assert.DoesNotContain(brain.Thought.Mode, new[] { KeeperMode.Rush, KeeperMode.Anticipate, KeeperMode.Smother });
            // his cap is read for the ball looked ahead along its run
            var look = ball.Position + ball.Velocity * T.Keeper.ArcLookahead;
            float cap = T.Keeper.HomeDepthM.Evaluate(Core.Keeper.MouthDistance(Flat(look), P));
            Assert.True(Depth(keeper) <= cap + 0.05f, $"{Depth(keeper):F2} m off his line, cap {cap:F2} m (ball {P.GoalLineX - ball.Position.X:F1} m out)");
        }
    }

    [Fact]
    public void TheLegacyKeeperComesFurtherOutThanTheHomeDepth()
    {
        var t = new Tuning();
        t.Keeper.Brain = "legacy";
        var start = At(25f, 0f, 0.11f);
        var (brain, keeper, ball) = Rig(start, Core.Keeper.ArcTarget(start, P, t, Wall), t);
        var vel = new Vector3(5f, 0f, 0f);
        float most = 0f, mostCap = 0f;
        var rng = new SimRng(7);
        for (int i = 0; i < 400 && P.GoalLineX - ball.Position.X > 7f; i++)
        {
            ball.Position += vel * Dt;
            brain.Update(Dt, ball, rng, OnBall(ball.Position, vel, next: 0.05f, touchTick: i / 30));
            float cap = T.Keeper.HomeDepthM.Evaluate(Core.Keeper.MouthDistance(Flat(ball.Position), P));
            if (Depth(keeper) - cap > most - mostCap) { most = Depth(keeper); mostCap = cap; }
        }
        Assert.True(most > mostCap + 1f, $"legacy: {most:F2} m off his line against a home depth of {mostCap:F2} m");
    }

    [Theory]
    [InlineData(3f)]
    [InlineData(5f)]
    [InlineData(7.5f)]
    public void HeIsUnderSetSpeedBeforeTheCarrierIsWithinTheSetGap(float speed)
    {
        // Set, he brakes to under set_speed before the carrier is within set_gap_m; from then on he may mirror (side-step or give
        // ground), but never come on at the ball.
        float slowest = float.PositiveInfinity;
        float? towardAtGap = null;
        foreach (var (brain, keeper, ball, _) in RunIn(speed, until: 1f))
        {
            var toBall = Flat(ball.Position) - Flat(keeper.Position);
            if (toBall.Length() > T.Keeper.SetGapM)
            {
                if (brain.IsSet) slowest = MathF.Min(slowest, keeper.Speed);
                continue;
            }
            slowest = MathF.Min(slowest, keeper.Speed);
            towardAtGap = Vector2.Dot(Flat(keeper.Velocity), Vector2.Normalize(toBall));
            break;
        }
        Assert.NotNull(towardAtGap);
        Assert.True(slowest < T.Keeper.SetSpeed, $"never under {T.Keeper.SetSpeed} m/s ({slowest:F2}) before the carrier came within {T.Keeper.SetGapM} m (at {speed} m/s)");
        Assert.True(towardAtGap < T.Keeper.SetSpeed, $"coming on at {towardAtGap:F2} m/s as the carrier came within {T.Keeper.SetGapM} m (at {speed} m/s)");
    }

    [Fact]
    public void MirroringKeepsHimOnTheBallToGoalLineAgainstACarrierCrossingAt5()
    {
        // Set on a carrier 5.5 m from him, who then runs across the goal at 5 m/s.
        var ballAt = At(9.5f, -4f, 0.11f);
        var (brain, keeper, ball) = Rig(ballAt, At(4f, -1.8f));
        var rng = new SimRng(3);
        var inVel = new Vector3(2f, 0f, 0f);
        for (int i = 0; i < 12; i++) brain.Update(Dt, ball, rng, OnBall(ball.Position, inVel));
        Assert.True(brain.IsSet);
        var cross = new Vector3(0f, 0f, 5f);
        var goal = new Vector2(P.GoalLineX, 0f);
        float worst = 0f;
        for (int i = 0; i < (int)(1.6f / Dt); i++)
        {
            ball.Position += cross * Dt;
            ball.Velocity = cross;
            brain.Update(Dt, ball, rng, OnBall(ball.Position, cross, next: 0.05f, touchTick: 1 + i / 30));
            Assert.True(brain.IsSet, $"not set at {i}");
            if (i * Dt < 0.25f) continue;   // (a moment to pick the run up)
            worst = MathF.Max(worst, World.SegmentDistance(Flat(keeper.Position), Flat(ball.Position), goal));
        }
        Assert.True(worst <= 0.75f, $"{worst:F2} m off the line from the ball to the goal centre");
    }

    /// <summary>Set on a carrier, then the ball <paramref name="offFoot"/> m ahead of his foot and 2 m from the keeper, his next touch that far off.</summary>
    static KeeperBrain PounceRig(float offFoot, float next, bool covers = false)
    {
        var (brain, keeper, ball) = Rig(At(8f, 0f, 0.11f), At(2.5f));
        var rng = new SimRng(5);
        var vel = new Vector3(1.5f, 0f, 0f);
        for (int i = 0; i < 6; i++) brain.Update(Dt, ball, rng, OnBall(ball.Position, vel, covers: covers));
        Assert.True(brain.IsSet);
        ball.Position = keeper.Position - new Vector3(2f, 0f, 0f) + new Vector3(0f, 0.11f, 0f);
        ball.Velocity = Vector3.Zero;
        brain.Update(Dt, ball, rng, OnBall(ball.Position, vel, next: next, touchTick: 2, behind: offFoot, covers: covers));
        return brain;
    }

    [Fact]
    public void APounceStartsOnABallOffTheFootInsideHisReachAndNotOnOneAtTheFoot()
    {
        Assert.True(2f > T.Keeper.SmotherReach && 2f <= T.Keeper.HullRadius + Core.Keeper.PounceReach(Wall, T));
        var pushed = PounceRig(1.2f, 0.8f);
        Assert.True(pushed.Report.Pounced);
        Assert.Equal(KeeperMode.Pounce, pushed.Thought.Mode);
        var atFoot = PounceRig(0.3f, 0.05f);
        Assert.False(atFoot.Report.Pounced);
        Assert.True(atFoot.IsSet);
    }

    [Fact]
    public void TakenRoundHeRunsBackAtRushSpeed()
    {
        // The ball 2.5 m off his line and wide, he 7 m out: well past him.
        var (brain, keeper, ball) = Rig(At(2.5f, 4f, 0.11f), At(7f, -2f));
        var rng = new SimRng(1);
        float top = 0f;
        for (int i = 0; i < 240; i++)
        {
            brain.Update(Dt, ball, rng, OnBall(ball.Position, new Vector3(0f, 0f, -0.5f)));
            if (brain.Thought.Mode != KeeperMode.Recover) break;
            Assert.Equal(KeeperAction.Rush, keeper.KeeperAction);
            top = MathF.Max(top, keeper.Speed);
        }
        float rush = Core.Keeper.RushSpeed(Wall, T);
        Assert.True(top >= 0.95f * rush && top <= rush + 0.01f, $"top speed {top:F2} m/s, rush speed {rush:F2}");
        Assert.True(top > Core.Keeper.JogSpeed(Wall, T) + 0.2f);
    }

    [Fact]
    public void OnTheGroundHeDoesNotStartToRecoverUntilHeIsFree()
    {
        var (brain, keeper, ball) = Rig(At(9f, 0f, 0.11f), At(5f));
        var rng = new SimRng(2);
        brain.BeginDiveForTest(keeper.Position + new Vector3(0f, 0.2f, 1.5f), 0.3f);
        int guard = 0;
        while (keeper.KeeperAction != KeeperAction.Grounded && guard++ < 400) brain.Update(Dt, ball, rng, KeeperSituation.None);
        Assert.Equal(KeeperAction.Grounded, keeper.KeeperAction);
        // the ball goes past him while he is down
        ball.Position = At(1.5f, -3f, 0.11f);
        var sit = OnBall(ball.Position, new Vector3(0.5f, 0f, 0f));
        bool free = false;
        for (int i = 0; i < 400 && !free; i++)
        {
            brain.Update(Dt, ball, rng, sit);
            if (keeper.KeeperAction is KeeperAction.Grounded or KeeperAction.GettingUp)
            {
                Assert.Equal(KeeperMode.Play, brain.Thought.Mode);
                continue;
            }
            free = true;
        }
        Assert.True(free);
        brain.Update(Dt, ball, rng, sit);
        Assert.Equal(KeeperMode.Recover, brain.Thought.Mode);
        Assert.Equal(KeeperAction.Rush, keeper.KeeperAction);
    }

    [Theory]
    [InlineData(0f, 0f)]       // on the goal centre
    [InlineData(0.6f, 0.4f)]   // within a metre of it
    [InlineData(0f, 2.5f)]     // the carrier on the goal line, inside the post
    [InlineData(0f, -6f)]      // on the goal line, wide
    [InlineData(-0.2f, 1f)]    // just over the line
    public void TheMirrorSpotAndTheRunBackStayFiniteAndOnThePitchWhenTheLineHasNoLength(float ballDepth, float ballZ)
    {
        foreach (var keeperAt in new[] { At(0.6f, 0f), At(3f, 1f), At(0.6f, ballZ) })
        {
            var spot = KeeperBrain.MirrorSpot(Flat(At(ballDepth, ballZ)), Flat(keeperAt), P, T);
            Assert.True(float.IsFinite(spot.X) && float.IsFinite(spot.Y), $"spot {spot}");
            Assert.True(spot.X <= P.GoalLineX && MathF.Abs(spot.Y) <= P.HalfWidth, $"spot {spot}");
            // and the brain, set or taken round, keeps him finite and inside the arena
            var (brain, keeper, ball) = Rig(At(ballDepth, ballZ, 0.11f), keeperAt);
            var rng = new SimRng(4);
            for (int i = 0; i < 120; i++)
            {
                brain.Update(Dt, ball, rng, OnBall(ball.Position, new Vector3(0.3f, 0f, 0f)));
                Assert.True(float.IsFinite(keeper.Position.X) && float.IsFinite(keeper.Position.Z), $"keeper at {keeper.Position}");
                Assert.True(keeper.Position.X <= P.GoalLineX + 1e-3f && MathF.Abs(keeper.Position.Z) <= P.HalfWidth, $"keeper at {keeper.Position}");
            }
        }
    }

    [Fact]
    public void WithACoveringDefenderHeNeitherChargesNorRushesButABallWithinHisReachIsStillHis()
    {
        // a carrier 3 m in front of him (inside engage_range), the ball at his foot: no charge
        {
            var (brain, keeper, ball) = Rig(At(8f, 0f, 0.11f), At(5f));
            var rng = new SimRng(6);
            for (int i = 0; i < 60; i++)
            {
                brain.Update(Dt, ball, rng, OnBall(ball.Position, new Vector3(0.5f, 0f, 0f), covers: true));
                Assert.DoesNotContain(keeper.KeeperAction, new[] { KeeperAction.Charge, KeeperAction.Jockey, KeeperAction.Rush });
                Assert.DoesNotContain(brain.Thought.Mode, new[] { KeeperMode.Charge, KeeperMode.Jockey, KeeperMode.Rush, KeeperMode.Anticipate });
            }
        }
        // a loose ball he would win 5 m out: left to the defender
        {
            var (brain, keeper, ball) = Rig(At(9f, 0f, 0.11f), At(3f));
            ball.Velocity = new Vector3(2f, 0f, 0f);
            var sit = new KeeperSituation { AttackerNextTouch = 2f, AttackerTouchTick = 1, StrikeIn = float.PositiveInfinity, AttackerPosition = At(14f), DefenderCovers = true };
            brain.Update(Dt, ball, new SimRng(6), sit);
            Assert.Equal("defender covers", brain.Thought.RaceBlock);
            Assert.DoesNotContain(brain.Thought.Mode, new[] { KeeperMode.Rush, KeeperMode.Anticipate, KeeperMode.Smother });
        }
        // a loose ball within his dive: his
        {
            var (brain, keeper, ball) = Rig(At(3f, 0.8f, 0.11f), At(3f));
            var sit = new KeeperSituation { AttackerNextTouch = 1.5f, AttackerTouchTick = 1, StrikeIn = float.PositiveInfinity, AttackerPosition = At(12f), DefenderCovers = true };
            brain.Update(Dt, ball, new SimRng(6), sit);
            Assert.Equal(KeeperAction.Smother, keeper.KeeperAction);
        }
        // set on a carrier, a ball pushed off his foot within his pounce: his too
        var pushed = PounceRig(1.2f, 0.8f, covers: true);
        Assert.True(pushed.Report.Pounced);
        Assert.Equal(KeeperMode.Pounce, pushed.Thought.Mode);
    }
}
