using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

public class ShotModelTests
{
    static readonly Tuning T = new();
    static readonly Pitch P = new(new PitchSpec(), T.Frame);

    static ShotContext Ctx(PlayerState shooter, Vector3? ball = null, float charge = 0.5f, ShotType type = ShotType.Normal,
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
            float expected = 7.0f - 6.2f * (fin - 1) / 98f;
            Assert.Equal(expected, r.SigmaH, 3);
            Assert.Equal(0.7f * expected, r.SigmaV, 3);
            Assert.All(r.Factors, f => Assert.Equal(1f, f.Value));
        }
    }

    [Fact]
    public void ErrorScaleMultipliesTheSpread()
    {
        var t = new Tuning { ErrorScale = 2f };
        var r = ShotModel.Compute(Ctx(Shooter()), P, t, new SimRng(1));
        Assert.Equal(2f * (7.0f - 6.2f * 77f / 98f), r.SigmaH, 3);
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
        Assert.Equal(1.5f, Shot(Ctx(p)).Factor("speed"), 3);
        p.Velocity = new Vector3(top / 2, 0, 0);
        Assert.Equal(1.25f, Shot(Ctx(p)).Factor("speed"), 3);
    }

    [Fact]
    public void AngleMultiplierRisesAfterThirtyDegrees()
    {
        // Shooter faces +X; aim the stick so the shot is 0, 30, 60 and 90 degrees off the facing.
        foreach (var (deg, expected) in new[] { (0f, 1f), (30f, 1f), (60f, 1.3f), (90f, 1.6f) })
        {
            float a = deg * MathF.PI / 180f;
            // Ball at the goal line's centre height; a stick at 'deg' casts to z beyond the clamp for large angles, so use yaw directly.
            var p = Shooter(facing: 0f);
            var ball = new Vector3(p.Position.X + 0.5f, T.Ball.Radius, p.Position.Z);
            var c = Ctx(p, ball, stick: new Vector2(MathF.Cos(a), MathF.Sin(a)));
            var r = Shot(c);
            float yaw = MathF.Abs(r.IntendedYaw) * 180f / MathF.PI;
            float want = 1f + 0.6f * Math.Clamp((yaw - 30f) / 60f, 0f, 1f);
            Assert.Equal(want, r.Factor("angle"), 3);
        }
        // Direct: a player facing 90 degrees away from a straight shot gets the full 1.6.
        var q = Shooter(facing: MathF.PI / 2);
        var rr = Shot(Ctx(q, stick: new Vector2(1f, 0f)));
        Assert.Equal(1.6f, rr.Factor("angle"), 3);
    }

    [Fact]
    public void FootMultiplierAndFootChoice()
    {
        var s = TestData.Sheet(60);
        s.PreferredFoot = Foot.Right; s.WeakFoot = 1;
        var p = TestData.Outfield(s, 41f, 0f, 0f);
        // Ball on the right (+Z when facing +X) or near the facing line: preferred foot.
        Assert.Equal(Foot.Right, ShotModel.StrikingFoot(p, new Vector3(41.5f, 0.11f, 0.4f), T));
        Assert.Equal(Foot.Right, ShotModel.StrikingFoot(p, new Vector3(41.5f, 0.11f, -0.2f), T));
        // More than 0.25 m to the left: the other foot, with the weak-foot penalty 1 + 0.18 * (5 - 1).
        var left = Shot(Ctx(p, new Vector3(41.5f, 0.11f, -0.3f)));
        Assert.Equal(Foot.Left, left.Foot);
        Assert.Equal(1.72f, left.Factor("foot"), 3);
        var right = Shot(Ctx(p, new Vector3(41.5f, 0.11f, 0.3f)));
        Assert.Equal(Foot.Right, right.Foot);
        Assert.Equal(1f, right.Factor("foot"), 3);

        s.PreferredFoot = Foot.Left;
        Assert.Equal(Foot.Right, ShotModel.StrikingFoot(p, new Vector3(41.5f, 0.11f, 0.3f), T));
        Assert.Equal(Foot.Left, ShotModel.StrikingFoot(p, new Vector3(41.5f, 0.11f, 0.2f), T));
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
        Assert.Equal(1.0f, Shot(Ctx(p, type: ShotType.Normal)).Factor("type"), 3);
        Assert.Equal(0.8f, Shot(Ctx(p, type: ShotType.Finesse)).Factor("type"), 3);
        Assert.Equal(1.1f, Shot(Ctx(p, type: ShotType.Chip)).Factor("type"), 3);
    }

    [Fact]
    public void SpeedAndElevationPerType()
    {
        var p = Shooter();
        float vmax = ShotModel.MaxSpeed(p.Sheet, T);
        foreach (float c in new[] { 0f, 0.5f, 1f })
        {
            Assert.Equal(vmax * (0.45f + 0.55f * c), ShotModel.IntendedSpeed(p.Sheet, ShotType.Normal, c, T), 3);
            Assert.Equal(0.75f * vmax * (0.45f + 0.55f * c), ShotModel.IntendedSpeed(p.Sheet, ShotType.Finesse, c, T), 3);
            Assert.Equal(0.55f * vmax * (0.45f + 0.55f * c), ShotModel.IntendedSpeed(p.Sheet, ShotType.Chip, c, T), 3);
            Assert.Equal(4f + 14f * c, ShotModel.ElevationDeg(ShotType.Normal, c, T), 3);
            Assert.Equal(6f + 10f * c, ShotModel.ElevationDeg(ShotType.Finesse, c, T), 3);
            Assert.Equal(28f + 12f * c, ShotModel.ElevationDeg(ShotType.Chip, c, T), 3);
        }
        // Launch speed averages to the intended speed (noise is 4%).
        foreach (var type in new[] { ShotType.Normal, ShotType.Finesse, ShotType.Chip })
        {
            double sum = 0; int n = 600;
            for (ulong i = 0; i < (ulong)n; i++) sum += Shot(Ctx(p, charge: 0.7f, type: type), i).Speed;
            Assert.InRange(sum / n / ShotModel.IntendedSpeed(p.Sheet, type, 0.7f, T), 0.99, 1.01);
        }
    }

    [Fact]
    public void ShotSpinPerType()
    {
        var p = Shooter();
        const float rev = 2 * MathF.PI;
        for (ulong i = 0; i < 50; i++)
        {
            Assert.InRange(Shot(Ctx(p), i).Spin.Length(), 0f, 2f * rev + 1e-3f);
            var chip = Shot(Ctx(p, type: ShotType.Chip), i);
            Assert.InRange(chip.Spin.Length(), 4f * rev - 1e-3f, 6f * rev + 1e-3f);
            // Backspin: spin x velocity points up.
            Assert.True(Vector3.Cross(chip.Spin, chip.Launch).Y > 0);
        }
        var right = TestData.Sheet(60); right.PreferredFoot = Foot.Right; right.Curve = 99;
        var pr = TestData.Outfield(right, 41f, 0f);
        var fin = Shot(Ctx(pr, type: ShotType.Finesse));
        Assert.Equal(10f * rev, fin.Spin.Length(), 2);
        // A right foot bends away from the foot's side, toward -Z when shooting along +X: force = spin x velocity.
        Assert.True(Vector3.Cross(fin.Spin, fin.Launch).Z < 0);
        var left = right.Clone(); left.PreferredFoot = Foot.Left;
        var fl = Shot(Ctx(TestData.Outfield(left, 41f, 0f), type: ShotType.Finesse));
        Assert.True(Vector3.Cross(fl.Spin, fl.Launch).Z > 0);
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
        Assert.Equal(4.5f, ShotModel.RawTargetZ(new Vector2(1, 1), 0f, ball, P, T), 4);   // 12 m * 1 = 12, clamped
        Assert.Equal(-4.5f, ShotModel.RawTargetZ(new Vector2(0, -1), 0f, ball, P, T), 4); // sideways stick
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
    public void ChargeRunsZeroToOneOverNineTenthsOfASecond()
    {
        Assert.Equal(0f, ShotModel.ChargeAfter(0f, T));
        Assert.Equal(0.5f, ShotModel.ChargeAfter(0.45f, T), 4);
        Assert.Equal(1f, ShotModel.ChargeAfter(5f, T));
    }

    [Fact]
    public void ResultRecordsEveryFactor()
    {
        var r = Shot(Ctx(Shooter()));
        Assert.Equal(new[] { "speed", "angle", "foot", "pressure", "power", "air", "type" }, r.Factors.Select(f => f.Name).ToArray());
        Assert.True(r.Speed > 0 && r.Distance > 0);
    }
}
