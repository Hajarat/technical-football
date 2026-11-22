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
    [InlineData(1, 6.8f)]
    [InlineData(99, 8.5f)]
    [InlineData(50, 6.8f + 1.7f * 49f / 98f)]
    public void SprintReachesTheRatedTopSpeed(int rating, float expected)
    {
        var s = TestData.Sheet(50); s.SprintSpeed = rating;
        var p = TestData.Outfield(s);
        Run(p, new TickIntent { MoveX = 1, Sprint = 1 }, 12f);
        Assert.InRange(p.Speed / expected, 0.995f, 1.001f);
    }

    [Fact]
    public void JogIsFiveThreeForEveryoneAndCloseControlThirtyFive()
    {
        var s = TestData.Sheet(60);
        float top = Movement.TopSpeed(s, T);
        var p = TestData.Outfield(s);
        Run(p, new TickIntent { MoveX = 1 }, 12f);
        Assert.InRange(p.Speed / 5.3f, 0.995f, 1.001f);
        var slow = TestData.Sheet(60); slow.SprintSpeed = 1;
        var ps = TestData.Outfield(slow);
        Run(ps, new TickIntent { MoveX = 1 }, 12f);
        Assert.InRange(ps.Speed / 5.3f, 0.995f, 1.001f);
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

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    public void SpeedingUpNeverExceedsTheAccelerationCap(int accel)
    {
        var s = TestData.Sheet(50); s.Acceleration = accel; s.SprintSpeed = 99;
        var p = TestData.Outfield(s);
        float now = 0, prev = 0f;
        for (int i = 0; i < 360; i++)
        {
            float cap = Movement.AccelCap(s, prev, 1f, T);
            now += Dt; Movement.Step(p, new TickIntent { MoveX = 1, Sprint = 1 }, Dt, now, false, T, null);
            Assert.InRange((p.Speed - prev) / Dt, 0f, cap * 1.0001f);
            Assert.True(cap < 15f, $"cap {cap:F1} m/s^2 at {prev:F2} m/s");
            prev = p.Speed;
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    public void EasingOffNeverExceedsTheDecelerationCap(int accel)
    {
        var s = TestData.Sheet(50); s.Acceleration = accel; s.SprintSpeed = 99;
        float top = Movement.TopSpeed(s, T);
        var p = TestData.Outfield(s);
        p.Velocity = new Vector3(top, 0, 0);
        float now = 0, prev = top, hardest = 0f;
        for (int i = 0; i < 480; i++)
        {
            float cap = Movement.DecelCap(s, prev, T);
            now += Dt; Movement.Step(p, default, Dt, now, false, T, null);
            float d = (prev - p.Speed) / Dt;
            Assert.InRange(d, 0f, cap * 1.0001f);
            hardest = MathF.Max(hardest, d);
            prev = p.Speed;
        }
        Assert.True(p.Speed < 0.5f, $"still at {p.Speed:F2} m/s");
        Assert.True(hardest < T.Movement.BalanceDeceleration, $"hardest {hardest:F1} m/s^2");
    }

    [Fact]
    public void AJogLaunchesGentlerThanASprintAndTheSprintInputBlendsThem()
    {
        var s = TestData.Sheet(50); s.Acceleration = 99;
        float Peak(float sprint)
        {
            var p = TestData.Outfield(s);
            float now = 0, prev = 0f, peak = 0f;
            for (int i = 0; i < 120; i++)
            {
                now += Dt; Movement.Step(p, new TickIntent { MoveX = 1, Sprint = sprint }, Dt, now, false, T, null);
                peak = MathF.Max(peak, (p.Speed - prev) / Dt);
                prev = p.Speed;
            }
            return peak;
        }
        float jog = Peak(0f), half = Peak(0.5f), sprint = Peak(1f);
        Assert.InRange(jog / Movement.AccelCap(s, 0f, 0f, T), 0.999f, 1.001f);
        Assert.InRange(sprint, Movement.AccelCap(s, 0f, 1f, T) * 0.999f, Movement.AccelCap(s, Movement.TopSpeed(s, T), 1f, T) * 1.001f);
        Assert.True(jog < half && half < sprint, $"jog {jog:F1} half {half:F1} sprint {sprint:F1}");
        // The blend is linear in the sprint input (continuous: no step in the cap as the trigger is squeezed).
        Assert.InRange(Movement.AccelCap(s, 3f, 0.5f, T) - 0.5f * (Movement.AccelCap(s, 3f, 0f, T) + Movement.AccelCap(s, 3f, 1f, T)), -1e-4f, 1e-4f);
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
        Assert.Equal(450f, Movement.TurnRateDeg(s, T), 2);
        // Standing start: one tick turns by rate * dt, scaled by turn_angle_mult for the 90 degrees still to turn (speed is
        // still ~0; a 90 degree turn is far outside the ease).
        float angle = T.Movement.TurnAngleMult.Evaluate(90f);
        var p = TestData.Outfield(s, facing: 0f);
        Movement.Step(p, new TickIntent { MoveZ = 1 }, Dt, Dt, false, T, null);
        Assert.InRange(p.Facing * 180f / MathF.PI, 450f * angle * Dt * 0.97f, 450f * angle * Dt * 1.001f);
        // At top speed the rate is halved.
        var q = TestData.Outfield(s, facing: 0f);
        q.Velocity = new Vector3(Movement.TopSpeed(s, T), 0, 0);
        Movement.Step(q, new TickIntent { MoveZ = 1, Sprint = 1 }, Dt, Dt, false, T, null);
        Assert.InRange(q.Facing * 180f / MathF.PI, 225f * angle * Dt * 0.97f, 225f * angle * Dt * 1.03f);
    }

    [Fact]
    public void WithoutTheBallATurnAboutBrakesAndTheTurnRateTopsOutAtGoalsThreeSixty()
    {
        var s = TestData.Sheet(50); s.Agility = 99; s.WeightKg = 75;
        Assert.InRange(Movement.TurnRateDeg(s, T) * (1f + T.Possession.OffBallTurnBonus), 359f, 361f);
        var p = TestData.Outfield(s, facing: 0f);
        p.Velocity = new Vector3(5f, 0, 0);
        var assist = new MoveAssist(Movement.TopSpeed(s, T), 1f + T.Possession.OffBallTurnBonus, Vector2.Zero, TurnAbout: true);
        float now = 0f, min = float.MaxValue;
        for (int i = 0; i < 120; i++)
        {
            now += Dt;
            Movement.Step(p, new TickIntent { MoveX = -1 }, Dt, now, assist, T, null);
            min = MathF.Min(min, p.Speed);
        }
        // the plant costs reverse_speed_loss of the 5 m/s, shed over several ticks, while he turns round
        Assert.True(min < 5f * (1f - 0.8f * T.Movement.ReverseSpeedLoss), $"lowest speed {min:F2}");
        Assert.InRange(MathF.Abs(Movement.WrapAngle(p.Facing - MathF.PI)), 0f, 0.05f);
    }

    [Fact]
    public void HeavierPlayersTurnSlower()
    {
        var light = TestData.Sheet(50, weight: 55); var heavy = TestData.Sheet(50, weight: 100);
        Assert.True(Movement.TurnRateDeg(light, T) > Movement.TurnRateDeg(heavy, T));
        float mid = 193f + (450f - 193f) * 49f / 98f;
        Assert.Equal(mid * 1.12f, Movement.TurnRateDeg(light, T), 1);  // 1 - 0.15 * (55 - 75) / 25 = 1.12
        Assert.Equal(mid * 0.85f, Movement.TurnRateDeg(heavy, T), 1);  // 1 - 0.15 = 0.85
    }

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    public void SharpTurnCostsTheBalancePercentage(int balance)
    {
        var s = TestData.Sheet(50); s.Balance = balance; s.Agility = 99;
        float loss = Movement.BalanceLoss(s, T);
        var noLoss = new Tuning(); noLoss.Movement.BalanceLoss = new RatingRange(0f, 0f);
        (float worst, float min) Turn(Tuning t)
        {
            var p = TestData.Outfield(s, facing: 0f);
            // At high speed the turn rate halves, so a 60 degree turn inside 0.2 s only happens at low speed (spec numbers).
            p.Velocity = new Vector3(2f, 0, 0);
            float now = 0, worstDrop = 0f, minSpeed = float.MaxValue;
            for (int i = 0; i < 120; i++)
            {
                float before = p.Speed;
                now += Dt;
                Movement.Step(p, new TickIntent { MoveX = -1, CloseControl = true }, Dt, now, false, t, null); // turn through 180 degrees
                worstDrop = MathF.Max(worstDrop, before - p.Speed);
                minSpeed = MathF.Min(minSpeed, p.Speed);
            }
            return (worstDrop, minSpeed);
        }
        var (worst, min) = Turn(T);
        var (_, minFree) = Turn(noLoss);
        // The loss is shed at balance_deceleration, never in one tick: no one-tick drop is larger than that rate times dt.
        Assert.InRange(worst, 0f, T.Movement.BalanceDeceleration * Dt * 1.01f);
        // About the balance percentage of the 2 m/s is lost (the run eases back toward its target meanwhile); a slow turn may
        // cross the sharp-turn angle twice in the half turn.
        Assert.InRange(minFree - min, 0.6f * loss * 2f, 2f * (1f - (1f - loss) * (1f - loss)) + 0.01f);
    }

    [Fact]
    public void ATurnCostsItsShareOfTheBalanceLossByTheAngleComeRound()
    {
        var s = TestData.Sheet(50);
        float loss = Movement.BalanceLoss(s, T);
        var cost = T.Movement.SharpTurnCost;
        float lo = cost[0].X, hi = cost[cost.Count - 1].X;
        // Facing set straight onto the stick each tick (a spin's facing), so the angle come round inside the window is exact.
        float Debt(float deg)
        {
            var p = TestData.Outfield(s, facing: 0f);
            p.Velocity = new Vector3(4f, 0, 0);
            float rad = deg * MathF.PI / 180f;
            var assist = new MoveAssist(Movement.TopSpeed(s, T), 1f, Vector2.Zero, RunSpeed: 4f, Facing: rad);
            Movement.Step(p, new TickIntent { MoveX = MathF.Cos(rad), MoveZ = MathF.Sin(rad) }, Dt, Dt, assist, T, null);
            // (what is left of the loss plus what this tick already shed)
            return p.SpeedDebt + (4f - p.RunSpeed);
        }
        Assert.Equal(0f, Debt(lo - 1f));
        float mid = 0.5f * (lo + hi);
        Assert.InRange(Debt(mid) / (4f * loss), cost.Evaluate(mid) - 0.01f, cost.Evaluate(mid) + 0.01f);
        Assert.InRange(Debt(mid) / (4f * loss), 0.05f, 0.95f);
        Assert.InRange(Debt(hi + 1f) / (4f * loss), 0.99f, 1.01f);
    }

    [Fact]
    public void ASmallHeadingChangeEasesInAndALargeOneRunsAtTheTurnRate()
    {
        var s = TestData.Sheet(50); s.Agility = 99; s.WeightKg = 75;
        // 3 degrees: one tick covers dt / turn_ease of it, not the full rate's 3.75 degrees.
        var p = TestData.Outfield(s, facing: 0f);
        float small = 3f * MathF.PI / 180f;
        Movement.Step(p, new TickIntent { MoveX = MathF.Cos(small), MoveZ = MathF.Sin(small) }, Dt, Dt, false, T, null);
        Assert.InRange(p.Facing / small, Dt / T.Movement.TurnEase * 0.99f, Dt / T.Movement.TurnEase * 1.01f);
        // And it arrives smoothly: within 2% after five time constants.
        Run(p, new TickIntent { MoveX = MathF.Cos(small), MoveZ = MathF.Sin(small) }, 5f * T.Movement.TurnEase);
        Assert.InRange(p.Facing / small, 0.98f, 1.0001f);
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
        var s = TestData.Sheet(50);
        float top = Movement.TopSpeed(s, T);
        var k = T.Touch.TopSpeedWithBall;
        foreach (int rating in new[] { 1, 99 })
        {
            s.CloseDribbling = s.SprintDribbling = rating;
            Assert.Equal(top * k.Close.ForRating(rating), Movement.TopSpeedWithBall(s, 0f, T), 3);
            Assert.Equal(top * k.Speed.ForRating(rating), Movement.TopSpeedWithBall(s, 1f, T), 3);
        }
        Assert.True(k.Close.At99 > k.Close.At1 && k.Speed.At99 > k.Speed.At1);
    }

    [Fact]
    public void EachDribblingStyleHasItsOwnTopSpeedByDribbling()
    {
        var t = new Tuning();
        t.Touch.TopSpeedWithBall.Close = new RatingRange(0.60f, 0.90f);
        t.Touch.TopSpeedWithBall.Speed = new RatingRange(0.80f, 1.00f);
        var s = TestData.Sheet(50);
        float top = Movement.TopSpeed(s, t);
        s.CloseDribbling = s.SprintDribbling = 1;
        // the style follows the intent: close control and no sprint are the close style, a full sprint the speed style
        Assert.Equal(top * 0.60f, Movement.TopSpeedWithBall(s, new TickIntent { MoveX = 1f, Sprint = 1f, CloseControl = true }, t), 3);
        Assert.Equal(top * 0.60f, Movement.TopSpeedWithBall(s, new TickIntent { MoveX = 1f }, t), 3);
        Assert.Equal(top * 0.80f, Movement.TopSpeedWithBall(s, new TickIntent { MoveX = 1f, Sprint = 1f }, t), 3);
        s.CloseDribbling = s.SprintDribbling = 99;
        Assert.Equal(top * 0.90f, Movement.TopSpeedWithBall(s, new TickIntent { MoveX = 1f }, t), 3);
        Assert.Equal(top * 1.00f, Movement.TopSpeedWithBall(s, new TickIntent { MoveX = 1f, Sprint = 1f }, t), 3);
    }

    [Fact]
    public void TheStyleBlendIsContinuousInTheSprintInput()
    {
        var t = new Tuning();
        t.Touch.TopSpeedWithBall.Close = new RatingRange(0.60f, 0.90f);
        t.Touch.TopSpeedWithBall.Speed = new RatingRange(0.80f, 1.00f);
        var s = TestData.Sheet(50); s.CloseDribbling = s.SprintDribbling = 30;
        // The pace the legs aim for (cap times mode fraction) rises with the sprint input without a step anywhere, the
        // sprint threshold (where the touch mode changes) included.
        float Pace(float sprint)
        {
            var i = new TickIntent { MoveX = 1f, Sprint = sprint };
            return Movement.TopSpeedWithBall(s, i, t) * Movement.ModeFraction(i, s, t);
        }
        float span = Pace(1f) - Pace(0f);
        Assert.True(span > 0.5f);
        const int n = 200;
        for (int j = 1; j <= n; j++)
        {
            float d = Pace(j / (float)n) - Pace((j - 1) / (float)n);
            Assert.InRange(d, 0f, 3f * span / n);
        }
    }

    [Fact]
    public void PlayersStayOnThePitch()
    {
        var pitch = new Pitch(new PitchSpec(), T.Frame);
        var p = TestData.Outfield(TestData.Sheet(99), 52f, 0f);
        float now = 0;
        for (int i = 0; i < 600; i++) { now += Dt; Movement.Step(p, new TickIntent { MoveX = 1, Sprint = 1 }, Dt, now, false, T, pitch); }
        Assert.Equal(pitch.GoalLineX - Pitch.PlayerRadius, p.Position.X, 4);
    }
}
