using System.Numerics;
using FootballSim.Core;
using Xunit.Abstractions;

namespace FootballSim.Core.Tests;

/// <summary>Charging and jockeying: closing an attacker on the ball down, the chip risk weighed, pouncing and blocking.</summary>
public class KeeperChargeTests(ITestOutputHelper output)
{
    static readonly Tuning T = new();
    static readonly Pitch P = new(new PitchSpec(), T.Frame);
    static float Dt => T.Dt;
    static readonly PlayerSheet Striker = TestData.Player("striker_a");
    /// <summary>The legacy keeper (keeper.brain legacy), for the charge he alone makes.</summary>
    static readonly Tuning Legacy = new() { Keeper = { Brain = "legacy" } };

    static PlayerSheet Keeper(int rushing)
    {
        var s = TestData.Player("the_wall").Clone();
        s.SetRating("rushing_out", rushing);
        return s;
    }

    static (KeeperBrain Brain, PlayerState Keeper, Ball Ball) Rig(PlayerSheet sheet, Vector3 ball, Vector3 keeperAt, Tuning? t = null)
    {
        var b = new Ball(T.Ball) { Position = ball };
        var k = new PlayerState(1, 0, PlayerRole.Keeper, sheet) { Position = keeperAt, Facing = MathF.PI };
        return (new KeeperBrain(k, P, t ?? T, false, Striker), k, b);
    }

    static float Depth(PlayerState k) => P.GoalLineX - k.Position.X;

    /// <summary>An attacker on the ball half a metre behind it, its next touch <paramref name="nextTouch"/> s off, moving at <paramref name="vel"/>.</summary>
    static KeeperSituation OnBall(Vector3 ball, Vector3 vel, float nextTouch = 0.3f, int touchTick = 1) => new()
    {
        AttackerNextTouch = nextTouch, AttackerTouchTick = touchTick, AttackerHasBall = true, StrikeIn = float.PositiveInfinity,
        AttackerPosition = ball - new Vector3(0.5f, ball.Y, 0f), AttackerVelocity = vel,
    };

    [Fact]
    public void DistanceInIsTheInverseOfTravelTimeAndCloseTimeCountsTheAttackerComingOn()
    {
        foreach (float d in new[] { 0.5f, 3f, 12f })
            foreach (float v0 in new[] { -2f, 0f, 4f })
            {
                float t = Core.Keeper.TravelTime(d, v0, 7f, 6.5f);
                Assert.Equal(d, Core.Keeper.DistanceIn(t, v0, 7f, 6.5f), 2);
            }
        float alone = Core.Keeper.CloseTime(10f, 0f, 0f, 7f, 6.5f);
        float met = Core.Keeper.CloseTime(10f, 0f, 7f, 7f, 6.5f);
        Assert.Equal(Core.Keeper.TravelTime(10f, 0f, 7f, 6.5f), alone, 2);
        Assert.True(met < 0.6f * alone, $"{met:F2} vs {alone:F2}");
        Assert.True(float.IsPositiveInfinity(Core.Keeper.CloseTime(100f, 0f, 0f, 7f, 6.5f)));
    }

    [Fact]
    public void TheChipRiskCutsTheTimeHeAcceptsAndABraveKeeperWeighsItLess()
    {
        var brave = Keeper(99);
        var timid = Keeper(1);
        Assert.True(Core.Keeper.ChargeTolerance(brave, T, 0f) > Core.Keeper.ChargeTolerance(timid, T, 0f));
        Assert.True(Core.Keeper.ChargeTolerance(timid, T, 4f) < 0.7f * Core.Keeper.ChargeTolerance(timid, T, 0f));
        float braveLoss = Core.Keeper.ChargeTolerance(brave, T, 4f) / Core.Keeper.ChargeTolerance(brave, T, 0f);
        float timidLoss = Core.Keeper.ChargeTolerance(timid, T, 4f) / Core.Keeper.ChargeTolerance(timid, T, 0f);
        Assert.True(braveLoss > timidLoss, $"brave keeps {braveLoss:P0}, timid {timidLoss:P0}");
        // No chip risk (inside his chip-safe depth): nothing is taken off.
        Assert.Equal(Core.Keeper.ChargeTolerance(timid, T, 0f), Core.Keeper.ChargeTolerance(timid, T, -3f));
    }

    /// <summary>
    /// An attacker on the ball running at goal at <paramref name="speed"/> from 24 m, the keeper on his arc: the ball's distance
    /// from goal when he first charges (NaN if he never does before it is 9 m out).
    /// </summary>
    static float ChargeStartsAt(PlayerSheet s, float speed, ulong seed)
    {
        var start = new Vector3(P.GoalLineX - 24f, 0.11f, 0f);
        var (brain, keeper, ball) = Rig(s, start, Core.Keeper.ArcTarget(start, P, T, s), Legacy);
        var rng = new SimRng(seed);
        var vel = new Vector3(speed, 0f, 0f);
        while (P.GoalLineX - ball.Position.X > 9f)
        {
            ball.Position += vel * Dt;
            ball.Velocity = vel;
            brain.Update(Dt, ball, rng, OnBall(ball.Position, vel));
            if (keeper.KeeperAction == KeeperAction.Charge) return P.GoalLineX - ball.Position.X;
        }
        return float.NaN;
    }

    [Fact]
    public void HeChargesEarlierTheFasterTheAttackerComesAndTheBraverHeIs()
    {
        float sprint = ChargeStartsAt(Keeper(50), 7f, 1), slow = ChargeStartsAt(Keeper(50), 2.8f, 1);
        float brave = ChargeStartsAt(Keeper(99), 4.5f, 1), timid = ChargeStartsAt(Keeper(1), 4.5f, 1);
        output.WriteLine($"charge starts with the ball at (rushing 50): sprint {sprint:F1} m, close control {slow:F1} m; jog: rushing 99 {brave:F1} m, rushing 1 {timid:F1} m");
        Assert.True(sprint <= T.Keeper.ChargeRange + 1e-3f, $"{sprint}");
        Assert.True(sprint > slow + 1f, $"sprint {sprint:F1} close control {slow:F1}");
        Assert.True(brave > timid + 2f, $"brave {brave:F1} timid {timid:F1}");
    }

    [Fact]
    public void ChargingHeClosesTheDribblerDownAndDoesNotLingerSixToEightMetresOff()
    {
        // An attacker jogging in from 22 m with the ball: once he charges he comes through 8 to 6 m from the ball fast, and he is
        // within 3 m of it by the time it is 12 m out.
        var s = Keeper(88);
        var start = new Vector3(P.GoalLineX - 22f, 0.11f, 0f);
        var (brain, keeper, ball) = Rig(s, start, Core.Keeper.ArcTarget(start, P, T, s), Legacy);
        var rng = new SimRng(3);
        var vel = new Vector3(4.5f, 0f, 0f);
        float band = 0f, at12 = float.NaN;
        while (P.GoalLineX - ball.Position.X > 11f)
        {
            ball.Position += vel * Dt;
            ball.Velocity = vel;
            brain.Update(Dt, ball, rng, OnBall(ball.Position, vel));
            float d = Vector2.Distance(new Vector2(keeper.Position.X, keeper.Position.Z), new Vector2(ball.Position.X, ball.Position.Z));
            if (d is >= 6f and <= 8f) band += Dt;
            if (float.IsNaN(at12) && P.GoalLineX - ball.Position.X <= 12f) at12 = d;
        }
        output.WriteLine($"6-8 m from the ball for {band:F2} s; {at12:F2} m from it with the ball 12 m out");
        Assert.True(band <= 0.3f, $"{band:F2} s in the band");
        Assert.True(at12 <= 3f, $"{at12:F2} m");
    }

    [Fact]
    public void AnAttackerWhoStopsFarOutLetsHimDropTheChargeAndRecoverToHisChipSafeDepth()
    {
        // Charging an attacker coming in fast from 19 m, who then stops dead 18 m out and shields the ball there: he drops the
        // charge and gives ground back to within his judged chip-safe depth.
        var s = Keeper(88);
        var start = new Vector3(P.GoalLineX - 20f, 0.11f, 0f);
        var (brain, keeper, ball) = Rig(s, start, new Vector3(P.GoalLineX - 4f, 0f, 0f), Legacy);
        var rng = new SimRng(5);
        var vel = new Vector3(7f, 0f, 0f);
        bool charged = false;
        for (int i = 0; i < 400; i++)
        {
            var v = P.GoalLineX - ball.Position.X > 18f ? vel : Vector3.Zero;
            ball.Position += v * Dt;
            ball.Velocity = v;
            brain.Update(Dt, ball, rng, OnBall(ball.Position, v));
            charged |= keeper.KeeperAction == KeeperAction.Charge;
        }
        float safe = Core.Keeper.ChipSafeDepth(s, Striker, T, P, 18f) * (1f + 2f * Core.Keeper.ChipJudgeErrorSd(s, T));
        output.WriteLine($"charged {charged}; settles {Depth(keeper):F2} m out, chip-safe (judged, at most) {safe:F2}, {keeper.KeeperAction}");
        Assert.True(charged);
        Assert.NotEqual(KeeperAction.Charge, keeper.KeeperAction);
        Assert.NotEqual(KeeperAction.Jockey, keeper.KeeperAction);
        Assert.True(Depth(keeper) <= safe + 0.3f, $"{Depth(keeper):F2} beyond {safe:F2}");
    }

    [Fact]
    public void AtTheFeetHePouncesOnABallLeftLooseButNeedsHisMarginWhileTheAttackerIsOnIt()
    {
        // The ball 1.3 m in front of him and rolling slowly on, the attacker's next touch 0.3 s off. With the attacker at the ball
        // (0.5 m behind it) the dive is not sure enough to go; with the attacker 2 m behind it (a touch too far) he smothers.
        var s = Keeper(88);
        var keeperAt = new Vector3(P.GoalLineX - 8f, 0f, 0f);
        var ballAt = new Vector3(P.GoalLineX - 9.3f, 0.11f, 0f);
        int Dives(float behind)
        {
            int n = 0;
            for (ulong seed = 1; seed <= 20; seed++)
            {
                var (brain, keeper, ball) = Rig(s, ballAt, keeperAt);
                ball.Velocity = new Vector3(1f, 0f, 0f);
                var sit = OnBall(ball.Position, new Vector3(2f, 0f, 0f), 0.3f, 2) with { AttackerPosition = ballAt - new Vector3(behind, 0.11f, 0f) };
                brain.Update(Dt, ball, new SimRng(seed), sit);
                if (keeper.KeeperAction == KeeperAction.Smother) n++;
            }
            return n;
        }
        int onIt = Dives(0.5f), loose = Dives(2f);
        output.WriteLine($"smothers of 20 (his misjudgement differs): attacker on it {onIt}, a touch too far {loose}");
        Assert.True(loose >= 15, $"{loose}");
        Assert.True(onIt <= loose - 5, $"{onIt} vs {loose}");
    }

    [Fact]
    public void ChargingIntoAWindupCloseToTheBallHeKeepsComingToBlockItOtherwiseHePullsUp()
    {
        // Charging at 6 m/s along the line, the shot winding up with contact in 0.45 s and the attacker still coming on.
        PlayerState Run(float gap)
        {
            var s = Keeper(88);
            var ballAt = new Vector3(P.GoalLineX - 14f, 0.11f, 0f);
            var (brain, keeper, ball) = Rig(s, ballAt, new Vector3(ballAt.X + gap, 0f, 0f), Legacy);
            var vel = new Vector3(4f, 0f, 0f);
            var rng = new SimRng(2);
            // Get him charging first.
            for (int i = 0; i < 6; i++) brain.Update(Dt, ball, rng, OnBall(ball.Position, vel));
            keeper.Velocity = new Vector3(-6f, 0f, 0f);
            var sit = OnBall(ball.Position, vel) with { Windup = true, StrikeIn = 0.45f };
            brain.Update(Dt, ball, rng, sit);
            return keeper;
        }
        var near = Run(5f);
        var far = Run(11f);
        output.WriteLine($"5 m off: {near.KeeperAction}; 11 m off: {far.KeeperAction}");
        Assert.True(near.KeeperAction is KeeperAction.Charge or KeeperAction.Jockey, $"{near.KeeperAction}");
        Assert.Equal(KeeperAction.Planted, far.KeeperAction);
    }

    [Fact]
    public void APenaltyKeeperHoldsHisLineWhileTheBallIsOnTheSpot()
    {
        var s = Keeper(99);
        var b = new Ball(T.Ball) { Position = new Vector3(P.GoalLineX - 11f, 0.11f, 0f) };
        var k = new PlayerState(1, 0, PlayerRole.Keeper, s) { Position = new Vector3(P.GoalLineX, 0f, 0f), Facing = MathF.PI };
        var brain = new KeeperBrain(k, P, T, true, Striker);
        for (int i = 0; i < 240; i++)
        {
            brain.Update(Dt, b, new SimRng(1), OnBall(b.Position, Vector3.Zero));
            Assert.NotEqual(KeeperAction.Charge, k.KeeperAction);
            Assert.NotEqual(KeeperAction.Jockey, k.KeeperAction);
        }
        Assert.True(Depth(k) < 0.5f, $"{Depth(k):F2} m off his line");
    }

    [Fact]
    public void APenaltyTakerWhoDribblesOffTheSpotFacesAKeeperInOpenPlay()
    {
        var s = Keeper(99);
        var b = new Ball(T.Ball) { Position = new Vector3(P.GoalLineX - 11f, 0.11f, 0f) };
        var k = new PlayerState(1, 0, PlayerRole.Keeper, s) { Position = new Vector3(P.GoalLineX, 0f, 0f), Facing = MathF.PI };
        var brain = new KeeperBrain(k, P, T, true, Striker);
        var vel = new Vector3(3f, 0f, 0f);
        bool cameOut = false;
        for (int i = 0; i < 180; i++)
        {
            b.Position += vel * Dt;
            brain.Update(Dt, b, new SimRng(1), OnBall(b.Position, vel));
            cameOut |= k.KeeperAction is KeeperAction.Charge or KeeperAction.Jockey or KeeperAction.Rush or KeeperAction.Smother || Depth(k) > 1.5f;
        }
        Assert.True(cameOut, $"still {Depth(k):F2} m off his line, {k.KeeperAction}");
    }
}
