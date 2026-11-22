using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

public class ShotModelTests
{
    static readonly Tuning T = new();
    static readonly Pitch P = new(new PitchSpec(), T.Frame);

    static ShotContext Ctx(PlayerState shooter, Vector3? ball = null, float charge = 0.5f, ShotType type = ShotType.Power,
        Vector2? stick = null, PlayerState? keeper = null)
    {
        return new ShotContext
        {
            Shooter = shooter,
            Keeper = keeper,
            BallPosition = ball ?? new Vector3(shooter.Position.X + 0.5f, T.Ball.Radius, shooter.Position.Z),
            Stick = stick ?? new Vector2(1f, 0f),
            Charge = charge,
            Type = type,
        };
    }

    static ShotResult Shot(in ShotContext c, ulong seed = 1) => ShotModel.Compute(c, P, T, new SimRng(seed));

    static PlayerState Shooter(int finishing = 78, float x = 41f, float z = 0f, float facing = 0f)
    {
        var s = TestData.Sheet(60);
        s.Finishing = finishing; s.LongShots = finishing;
        return TestData.Outfield(s, x, z, facing);
    }

    [Fact]
    public void SigmaMatchesTheFormulaAtFixedRatings()
    {
        foreach (int fin in new[] { 1, 40, 78, 99 })
        {
            var p = Shooter(fin);
            var r = Shot(Ctx(p));
            float expected = T.Shot.BaseSigmaDeg - T.Shot.SigmaSkillSlope * (fin - 1) / 98f;
            Assert.Equal(expected, r.SigmaH, 3);
            Assert.Equal(T.Shot.VerticalRatio * expected, r.SigmaV, 3);
            Assert.All(r.Factors, f => Assert.Equal(1f, f.Value));
        }
    }

    [Fact]
    public void ErrorScaleMultipliesTheSpread()
    {
        var t = new Tuning { ErrorScale = 2f };
        var r = ShotModel.Compute(Ctx(Shooter()), P, t, new SimRng(1));
        Assert.Equal(2f * (t.Shot.BaseSigmaDeg - t.Shot.SigmaSkillSlope * 77f / 98f), r.SigmaH, 3);
    }

    [Fact]
    public void SkillMixesFromFinishingToLongShotsBetween18And25Metres()
    {
        var s = TestData.Sheet(50);
        s.Finishing = 90; s.LongShots = 30;
        float fin = Rating.R01(90), lng = Rating.R01(30);
        Assert.Equal(fin, ShotModel.SkillR01(s, 17f, T), 5);
        Assert.Equal(lng, ShotModel.SkillR01(s, 26f, T), 5);
        Assert.Equal((fin + lng) / 2f, ShotModel.SkillR01(s, 21.5f, T), 4);
    }

    [Fact]
    public void SpeedMultiplierGrowsWithRunSpeed()
    {
        var p = Shooter();
        float top = Movement.TopSpeed(p.Sheet, T);
        p.Velocity = new Vector3(top, 0, 0);
        float slope = T.Shot.SpeedMultiplierSlope;
        Assert.Equal(1f + slope, Shot(Ctx(p)).Factor("speed"), 3);
        p.Velocity = new Vector3(top / 2, 0, 0);
        Assert.Equal(1f + slope / 2f, Shot(Ctx(p)).Factor("speed"), 3);
    }

    [Fact]
    public void AngleMultiplierFollowsTheBodyAngleCurve()
    {
        // Shooter faces +X; aim the stick so the shot is 0, 30, 60 and 90 degrees off the facing.
        foreach (float deg in new[] { 0f, 30f, 60f, 90f })
        {
            float a = deg * MathF.PI / 180f;
            // A stick at 'deg' casts to z beyond the clamp for large angles, so the curve is read at the yaw it gives.
            var p = Shooter(facing: 0f);
            var ball = new Vector3(p.Position.X + 0.5f, T.Ball.Radius, p.Position.Z);
            var c = Ctx(p, ball, stick: new Vector2(MathF.Cos(a), MathF.Sin(a)));
            var r = Shot(c);
            float yaw = MathF.Abs(r.IntendedYaw) * 180f / MathF.PI;
            Assert.Equal(T.Shot.AngleAccuracy.Evaluate(yaw), r.Factor("angle"), 3);
        }
        // Direct: free to 30 degrees, 1.5 at 60, 2.4 for a player facing 90 degrees away from a straight shot.
        Assert.Equal(1f, Shot(Ctx(Shooter(facing: MathF.PI / 6), stick: new Vector2(1f, 0f))).Factor("angle"), 3);
        Assert.Equal(1.5f, Shot(Ctx(Shooter(facing: MathF.PI / 3), stick: new Vector2(1f, 0f))).Factor("angle"), 3);
        Assert.Equal(2.4f, Shot(Ctx(Shooter(facing: MathF.PI / 2), stick: new Vector2(1f, 0f))).Factor("angle"), 3);
    }

    [Fact]
    public void FootMultiplierAndFootChoice()
    {
        var s = TestData.Sheet(60);
        s.PreferredFoot = Foot.Right; s.WeakFoot = 1;
        var p = TestData.Outfield(s, 41f, 0f, 0f);
        // A ball in front of him aimed straight on is the strong foot's, whichever side of him it lies (see ShotFootTests for
        // the whole choice).
        Assert.Equal(Foot.Right, Shot(Ctx(p, new Vector3(41.5f, 0.11f, 0.4f))).Foot);
        Assert.Equal(Foot.Right, Shot(Ctx(p, new Vector3(41.5f, 0.11f, -0.4f))).Foot);
        // Struck with the weak foot all the same (the foot decided beforehand): weak_foot_accuracy at a rating of 1.
        var c = Ctx(p, new Vector3(41.5f, 0.11f, -0.3f));
        c.Foot = Foot.Left;
        var left = Shot(c);
        Assert.Equal(Foot.Left, left.Foot);
        Assert.Equal(1.72f, left.Factor("foot"), 3);
        c.Foot = Foot.Right;
        var right = Shot(c);
        Assert.Equal(Foot.Right, right.Foot);
        Assert.Equal(1f, right.Factor("foot"), 3);
        s.WeakFoot = 50;
        c.Foot = Foot.Left;
        Assert.Equal(1.36f, Shot(c).Factor("foot"), 3);
        s.WeakFoot = 99;
        Assert.Equal(1f, Shot(c).Factor("foot"), 3);
    }

    [Fact]
    public void PressureMultiplierRunsFromFourMetresToOne()
    {
        var s = TestData.Sheet(60);
        s.Composure = 1;
        var p = TestData.Outfield(s, 41f, 0f);
        PlayerState KeeperAt(float dx) => new(1, 0, PlayerRole.Keeper, TestData.Sheet(50)) { Position = new Vector3(41f + dx, 0, 0) };
        Assert.Equal(1f, Shot(Ctx(p, keeper: KeeperAt(4.5f))).Factor("pressure"), 3);
        Assert.Equal(1f, Shot(Ctx(p, keeper: KeeperAt(4f))).Factor("pressure"), 3);
        Assert.Equal(1.3f, Shot(Ctx(p, keeper: KeeperAt(2.5f))).Factor("pressure"), 3);
        Assert.Equal(1.6f, Shot(Ctx(p, keeper: KeeperAt(1f))).Factor("pressure"), 3);
        s.Composure = 99;
        Assert.Equal(1f, Shot(Ctx(p, keeper: KeeperAt(1f))).Factor("pressure"), 3);
    }

    [Fact]
    public void PowerMultiplierRisesFromEightyPercentCharge()
    {
        var p = Shooter();
        Assert.Equal(1f, Shot(Ctx(p, charge: 0.79f)).Factor("power"), 3);
        Assert.Equal(1f, Shot(Ctx(p, charge: 0.8f)).Factor("power"), 3);
        Assert.Equal(1.25f, Shot(Ctx(p, charge: 0.9f)).Factor("power"), 3);
        Assert.Equal(1.5f, Shot(Ctx(p, charge: 1.0f)).Factor("power"), 3);
    }

    [Fact]
    public void AirMultiplierAppliesWhenTheBallIsOffTheGround()
    {
        var p = Shooter();
        var low = Ctx(p, new Vector3(41.5f, T.Ball.Radius + 0.1f, 0));
        var high = Ctx(p, new Vector3(41.5f, T.Ball.Radius + 0.2f, 0));
        Assert.Equal(1f, Shot(low).Factor("air"), 3);
        Assert.Equal(1.3f, Shot(high).Factor("air"), 3);
    }

    [Fact]
    public void TypeMultiplierIsPerShotType()
    {
        var p = Shooter();
        Assert.Equal(1.0f, Shot(Ctx(p, type: ShotType.Power)).Factor("type"), 3);
        Assert.Equal(0.8f, Shot(Ctx(p, type: ShotType.Finesse)).Factor("type"), 3);
        Assert.Equal(1.1f, Shot(Ctx(p, type: ShotType.Chip)).Factor("type"), 3);
        Assert.Equal(T.Shot.DrivenTypeMultiplier, Shot(Ctx(p, type: ShotType.Driven)).Factor("type"), 3);
        Assert.Equal(T.Shot.LowFinesseTypeMultiplier, Shot(Ctx(p, type: ShotType.LowFinesse)).Factor("type"), 3);
    }

    [Fact]
    public void SpeedAndElevationPerType()
    {
        var p = Shooter();
        var k = T.Shot;
        float vmax = ShotModel.MaxSpeed(p.Sheet, T);
        foreach (float c in new[] { 0f, 0.5f, 1f })
        {
            float power = vmax * (k.ChargeBaseSpeed + k.ChargeSpeedSlope * c);
            Assert.Equal(power, ShotModel.IntendedSpeed(p.Sheet, ShotType.Power, c, T), 3);
            Assert.Equal(k.FinesseSpeedFraction * power, ShotModel.IntendedSpeed(p.Sheet, ShotType.Finesse, c, T), 3);
            Assert.Equal(k.LowFinesseSpeedFraction * power, ShotModel.IntendedSpeed(p.Sheet, ShotType.LowFinesse, c, T), 3);
            Assert.Equal(k.ChipSpeedFraction * power, ShotModel.IntendedSpeed(p.Sheet, ShotType.Chip, c, T), 3);
            Assert.Equal(1.05f * power, ShotModel.IntendedSpeed(p.Sheet, ShotType.Driven, c, T), 3);
            Assert.Equal(k.PowerElevationBaseDeg + k.PowerElevationChargeDeg * c, ShotModel.ElevationDeg(ShotType.Power, c, T), 3);
            Assert.Equal(k.FinesseElevationBaseDeg + k.FinesseElevationChargeDeg * c, ShotModel.ElevationDeg(ShotType.Finesse, c, T), 3);
            Assert.Equal(k.ChipElevationBaseDeg + k.ChipElevationChargeDeg * c, ShotModel.ElevationDeg(ShotType.Chip, c, T), 3);
            // Driven and low finesse stay low: 1 to 5 degrees of loft by charge.
            Assert.InRange(ShotModel.ElevationDeg(ShotType.Driven, c, T), 1f, 5f);
            Assert.InRange(ShotModel.ElevationDeg(ShotType.LowFinesse, c, T), 1f, 4f);
        }
        // Launch speed averages to the intended speed (noise is 4%) for every type but the chip, at the ideal charge (a
        // chip's speed also carries its charge miss), times run_speed_power for a shooter standing still.
        var ball = new Vector3(41.5f, T.Ball.Radius, 0f);
        float ideal = ShotModel.IdealCharge(ShotModel.GoalDistance(ball, P), T);
        foreach (var type in new[] { ShotType.Power, ShotType.Finesse, ShotType.Chip, ShotType.Driven, ShotType.LowFinesse })
        {
            double sum = 0; int n = 600;
            for (ulong i = 0; i < (ulong)n; i++) sum += Shot(Ctx(p, ball, charge: ideal, type: type), i).Speed;
            Assert.InRange(sum / n / (ShotModel.IntendedSpeed(p.Sheet, type, ideal, T) * k.RunSpeedPower.Evaluate(0f)), 0.99, 1.01);
        }
    }

    [Fact]
    public void DrivenIsFlatterFasterAndSlightlyLessAccurateThanPower()
    {
        var p = Shooter();
        foreach (float c in new[] { 0.2f, 0.5f, 0.9f })
        {
            var pw = Shot(Ctx(p, charge: c));
            var dr = Shot(Ctx(p, charge: c, type: ShotType.Driven));
            Assert.True(dr.ElevationDeg < pw.ElevationDeg - 2f, $"charge {c}: driven {dr.ElevationDeg} vs power {pw.ElevationDeg}");
            Assert.True(MathF.Asin(dr.Launch.Y / dr.Launch.Length()) < MathF.Asin(pw.Launch.Y / pw.Launch.Length()));
            Assert.True(dr.SigmaH > pw.SigmaH);
            Assert.InRange(dr.SigmaH / pw.SigmaH, 1.01f, 1.25f);
        }
    }

    [Fact]
    public void TypeSelectionFollowsTheModifiers()
    {
        Assert.Equal(ShotType.Power, ShotModel.TypeFor(finesse: false, chip: false, driven: false));
        Assert.Equal(ShotType.Finesse, ShotModel.TypeFor(finesse: true, chip: false, driven: false));
        Assert.Equal(ShotType.Driven, ShotModel.TypeFor(finesse: false, chip: false, driven: true));
        Assert.Equal(ShotType.LowFinesse, ShotModel.TypeFor(finesse: true, chip: false, driven: true));
        // Chip wins over everything.
        Assert.Equal(ShotType.Chip, ShotModel.TypeFor(finesse: false, chip: true, driven: false));
        Assert.Equal(ShotType.Chip, ShotModel.TypeFor(finesse: true, chip: true, driven: true));
        Assert.Equal(ActionKind.LowFinesse, ShotModel.KindFor(ShotType.LowFinesse));
        Assert.Equal(FootSurface.Laces, ShotModel.SurfaceFor(ShotType.Power));
        Assert.Equal(FootSurface.Laces, ShotModel.SurfaceFor(ShotType.Driven));
        Assert.Equal(FootSurface.Inside, ShotModel.SurfaceFor(ShotType.Finesse));
        Assert.Equal(FootSurface.Inside, ShotModel.SurfaceFor(ShotType.LowFinesse));
        Assert.Equal(FootSurface.Inside, ShotModel.SurfaceFor(ShotType.Chip));
    }

    [Fact]
    public void IdealChargeFollowsTheDistanceCurve()
    {
        foreach (var (x, y) in Enumerable.Range(0, T.Shot.IdealCharge.Count).Select(i => T.Shot.IdealCharge[i]))
            Assert.Equal(y, ShotModel.IdealCharge(x, T), 4);
        Assert.True(ShotModel.IdealCharge(11f, T) < ShotModel.IdealCharge(20f, T));
        Assert.Equal(ShotModel.IdealCharge(new Vector2(11f, 3f).Length(), T),
            ShotModel.IdealCharge(ShotModel.GoalDistance(new Vector3(P.GoalLineX - 11f, 0.11f, 3f), P), T), 4);
    }

    [Fact]
    public void OverChargingRisesAndUnderChargingDrivesLow()
    {
        var p = Shooter();
        var ball = new Vector3(41.5f, T.Ball.Radius, 0f);
        float ideal = ShotModel.IdealCharge(ShotModel.GoalDistance(ball, P), T);
        foreach (var type in new[] { ShotType.Power, ShotType.Finesse, ShotType.Driven, ShotType.LowFinesse })
        {
            var at = Shot(Ctx(p, ball, ideal, type));
            var over = Shot(Ctx(p, ball, ideal + 0.2f, type));
            var under = Shot(Ctx(p, ball, ideal - 0.2f, type));
            Assert.Equal(0f, at.ChargeMissDeg, 4);
            Assert.Equal(T.Shot.ChargeMissPitchDeg * 0.2f, over.ChargeMissDeg, 3);
            Assert.Equal(-T.Shot.ChargeMissPitchDeg * 0.2f, under.ChargeMissDeg, 3);
            // Same seed, so the same random part: the launch angle moves by the charge miss plus the loft's own change.
            float Pitch(ShotResult r) => MathF.Asin(r.Launch.Y / r.Launch.Length()) * 180f / MathF.PI;
            Assert.True(Pitch(over) > Pitch(at), $"{type}: over-charged {Pitch(over)} not above {Pitch(at)}");
            Assert.True(Pitch(under) < Pitch(at), $"{type}: under-charged {Pitch(under)} not below {Pitch(at)}");
            // Averaged over many draws, the vertical error is centred on the charge miss.
            double mean = Enumerable.Range(0, 400).Average(i => Shot(Ctx(p, ball, ideal + 0.2f, type), (ulong)i).PitchErrorDeg);
            Assert.InRange(mean, over.ChargeMissDeg - 0.3, over.ChargeMissDeg + 0.3);
        }
    }

    [Fact]
    public void AChipsChargeMissGoesIntoDepthNotHeight()
    {
        var p = Shooter();
        var ball = new Vector3(41.5f, T.Ball.Radius, 0f);
        float ideal = ShotModel.IdealCharge(ShotModel.GoalDistance(ball, P), T);
        float Carry(float charge)
        {
            var r = Shot(Ctx(p, ball, charge, ShotType.Chip));
            Assert.Equal(0f, r.ChargeMissDeg);
            var b = new Ball(T.Ball) { Position = ball, Velocity = r.Launch, Spin = r.Spin };
            for (int i = 0; i < 1200 && (i < 5 || b.Position.Y > T.Ball.Radius + 1e-3f); i++) b.Step(T.Dt, null, null);
            return b.Position.X - ball.X; // first landing
        }
        float atIdeal = Carry(ideal), over = Carry(ideal + 0.15f), under = Carry(ideal - 0.15f);
        Assert.True(over > atIdeal + 0.5f, $"over {over} vs ideal {atIdeal}");
        Assert.True(under < atIdeal - 0.5f, $"under {under} vs ideal {atIdeal}");
        // The miss adds its own speed factor on top of the charge's speed.
        double mean = Enumerable.Range(0, 400).Average(i => Shot(Ctx(p, ball, ideal + 0.15f, ShotType.Chip), (ulong)i).Speed);
        // (He stands still: run_speed_power at 0 scales it too.)
        double expected = ShotModel.IntendedSpeed(p.Sheet, ShotType.Chip, ideal + 0.15f, T) * (1f + T.Shot.ChipChargeMissSpeed * 0.15f) * T.Shot.RunSpeedPower.Evaluate(0f);
        Assert.InRange(mean / expected, 0.99, 1.01);
    }

    [Fact]
    public void ShotSpinPerType()
    {
        var p = Shooter();
        const float rev = 2 * MathF.PI;
        var k = T.Shot;
        float Scale(ShotResult r) => r.Speed / k.SpinReferenceSpeed;
        int powerLeft = 0, powerRight = 0;
        for (ulong i = 0; i < 200; i++)
        {
            // Power: a light curl about the axis that bends the ball sideways, to a random side, within the rated amount.
            var pw = Shot(Ctx(p), i);
            Assert.InRange(pw.Spin.Length(), 0f, k.PowerSpinRev.ForRating(p.Sheet.Curve) * Scale(pw) * rev + 1e-3f);
            Assert.True(MathF.Abs(Vector3.Dot(Vector3.Normalize(pw.Launch), pw.Spin)) < 1e-2f, "power spin is side spin");
            float side = Vector3.Cross(pw.Spin, pw.Launch).Z;
            if (side > 0) powerRight++; else if (side < 0) powerLeft++;
            // Driven: little spin.
            var dr = Shot(Ctx(p, type: ShotType.Driven), i);
            Assert.InRange(dr.Spin.Length(), 0f, k.DrivenSpinRev.ForRating(p.Sheet.Curve) * Scale(dr) * rev + 1e-3f);
            var chip = Shot(Ctx(p, type: ShotType.Chip), i);
            Assert.InRange(chip.Spin.Length(), k.ChipBackspinMinRev * rev - 1e-3f, k.ChipBackspinMaxRev * rev + 1e-3f);
            // Backspin: spin x velocity points up.
            Assert.True(Vector3.Cross(chip.Spin, chip.Launch).Y > 0);
        }
        Assert.True(powerLeft > 60 && powerRight > 60, $"power curl sides {powerLeft}/{powerRight}");
        Assert.True(k.DrivenSpinRev.At99 < k.PowerSpinRev.At99);

        var right = TestData.Sheet(60); right.PreferredFoot = Foot.Right; right.Curve = 99;
        var pr = TestData.Outfield(right, 41f, 0f);
        foreach (var type in new[] { ShotType.Finesse, ShotType.LowFinesse })
        {
            var fin = Shot(Ctx(pr, type: type));
            // Grows with the shot's speed and the curve rating.
            Assert.Equal(k.FinesseSpinRev.At99 * Scale(fin) * rev, fin.Spin.Length(), 2);
            // A right foot bends away from the foot's side, toward -Z when shooting along +X: force = spin x velocity.
            Assert.True(Vector3.Cross(fin.Spin, fin.Launch).Z < 0);
            var left = right.Clone(); left.PreferredFoot = Foot.Left;
            var fl = Shot(Ctx(TestData.Outfield(left, 41f, 0f), type: type));
            Assert.True(Vector3.Cross(fl.Spin, fl.Launch).Z > 0);
            // Harder struck, more curl.
            Assert.True(Shot(Ctx(pr, charge: 0.9f, type: type)).Spin.Length() > Shot(Ctx(pr, charge: 0.3f, type: type)).Spin.Length());
            // A lower curve rating curls less.
            var dull = right.Clone(); dull.Curve = 20;
            Assert.True(Shot(Ctx(TestData.Outfield(dull, 41f, 0f), type: type)).Spin.Length() < fin.Spin.Length());
        }
    }

    [Fact]
    public void AimAssistPullsATargetJustOutsideTheFrame()
    {
        float hw = P.GoalHalfWidth;
        float inside = hw - 0.6f;
        Assert.Equal(2f, ShotModel.AssistTargetZ(2f, P, T), 4);                     // inside the frame: untouched
        float raw = hw + 0.5f;
        Assert.Equal(raw + 0.3f * (inside - raw), ShotModel.AssistTargetZ(raw, P, T), 4);
        Assert.Equal(-(raw + 0.3f * (inside - raw)), ShotModel.AssistTargetZ(-raw, P, T), 4);
        float far = hw + 1.6f;                                                       // outside by more than 1.5 m: untouched
        Assert.Equal(far, ShotModel.AssistTargetZ(far, P, T), 4);
        var none = new Tuning { AimAssist = 0f };
        Assert.Equal(raw, ShotModel.AssistTargetZ(raw, P, none), 4);
    }

    [Fact]
    public void TargetIsTheStickCastToTheGoalLineAndClamped()
    {
        var ball = new Vector3(40.5f, 0.11f, 0f);
        Assert.Equal(0f, ShotModel.RawTargetZ(new Vector2(1, 0), 0f, ball, P, T), 4);
        Assert.Equal(8f, ShotModel.RawTargetZ(new Vector2(1, 1), 0f, ball, P, T), 4);   // 12 m * 1 = 12, clamped
        Assert.Equal(-8f, ShotModel.RawTargetZ(new Vector2(0, -1), 0f, ball, P, T), 4); // sideways stick
        Assert.Equal(1.2f, ShotModel.RawTargetZ(new Vector2(1, 0.1f), 0f, ball, P, T), 4);
    }

    [Fact]
    public void StickAtRestShootsWhereThePlayerFaces()
    {
        var p = Shooter(facing: 0.2f);
        var ball = new Vector3(41.5f, T.Ball.Radius, 0f);
        var r = Shot(Ctx(p, ball, stick: Vector2.Zero));
        Assert.True(float.IsFinite(r.Launch.X) && float.IsFinite(r.Launch.Y) && float.IsFinite(r.Launch.Z));
        Assert.True(r.Launch.Length() > 5f, "no zero-length direction");
        float expectedZ = MathF.Tan(0.2f) * (P.GoalLineX - ball.X);
        Assert.Equal(expectedZ, r.Aimed.X, 2);
        Assert.InRange(r.IntendedYaw, 0.1f, 0.3f);
        // And a stick inside the dead zone counts as at rest.
        var r2 = Shot(Ctx(p, ball, stick: new Vector2(0.01f, 0.01f)));
        Assert.Equal(r.Aimed.X, r2.Aimed.X, 3);
    }

    [Fact]
    public void StrikeNeedsTheBallInReachAndLow()
    {
        var p = TestData.Outfield(TestData.Sheet(50, 1.80f), 10f, 0f);
        var ball = new Ball(T.Ball);
        float reach = ShotModel.StrikeReach(p.Sheet, T);
        Assert.Equal(0.55f + 0.30f * 0.5f + 0.2f, reach, 3);
        ball.Position = new Vector3(10f + reach - 0.01f, 0.11f, 0f);
        Assert.True(ShotModel.CanStrike(p, ball, T));
        ball.Position = new Vector3(10f + reach + 0.01f, 0.11f, 0f);
        Assert.False(ShotModel.CanStrike(p, ball, T));
        ball.Position = new Vector3(10.3f, 0.7f, 0f);
        Assert.False(ShotModel.CanStrike(p, ball, T));
    }

    [Fact]
    public void ChargeRunsZeroToOneOverThreeTenthsOfASecond()
    {
        Assert.Equal(0f, ShotModel.ChargeAfter(0f, T));
        Assert.Equal(0.5f, ShotModel.ChargeAfter(0.15f, T), 4);
        Assert.Equal(1f, ShotModel.ChargeAfter(0.3f, T), 4);
        Assert.Equal(1f, ShotModel.ChargeAfter(5f, T));
    }

    [Fact]
    public void ResultRecordsEveryFactor()
    {
        var r = Shot(Ctx(Shooter()));
        Assert.Equal(new[] { "speed", "angle", "mouth", "foot", "surface", "pressure", "power", "air", "type" }, r.Factors.Select(f => f.Name).ToArray());
        Assert.Equal(new[] { "angle", "weak_foot", "surface", "run_speed" }, r.SpeedFactors.Select(f => f.Name).ToArray());
        Assert.True(r.Speed > 0 && r.Distance > 0);
    }
}
