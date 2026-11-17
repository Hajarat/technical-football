using System.Numerics;

namespace FootballSim.Core;

/// <summary>A read-only look at the strike that would happen now: where it starts, its intended yaw, the aimed Z and sigma_h.</summary>
/// <param name="Origin">Ball position.</param>
/// <param name="IntendedYaw">Radians, 0 = +X.</param>
/// <param name="AimedZ">Aimed Z on the goal line, after aim assist.</param>
/// <param name="SigmaH">Standard deviation of the sideways error, degrees.</param>
public readonly record struct ShotPreview(Vector3 Origin, float IntendedYaw, float AimedZ, float SigmaH);

/// <summary>One named multiplier that fed a shot's spread.</summary>
public readonly record struct ShotFactor(string Name, float Value);

/// <summary>Everything the shot model needs to know at the moment of the strike.</summary>
public struct ShotContext
{
    /// <summary>The player striking the ball.</summary>
    public PlayerState Shooter;
    /// <summary>The opposing keeper, for the pressure multiplier. May be null.</summary>
    public PlayerState? Keeper;
    public Vector3 BallPosition;
    /// <summary>Stick direction (world X, Z) at release. Near zero means "at rest": the shot goes where the shooter faces.</summary>
    public Vector2 Stick;
    /// <summary>0..1.</summary>
    public float Charge;
    public ShotType Type;
    /// <summary>The striking foot when it was already decided (at the press); null picks it from the ball position.</summary>
    public Foot? Foot;
}

/// <summary>A shot as struck, with every factor that shaped it. Recorded in the round report.</summary>
public sealed class ShotResult
{
    /// <summary>Where the shot was aimed on the goal plane: (Z, height). Height ignores drag and spin.</summary>
    public Vector2 Aimed { get; init; }
    /// <summary>Where the ball crossed the goal-line plane: (Z, height). NaN if it never did.</summary>
    public Vector2 Landed { get; internal set; } = new(float.NaN, float.NaN);
    public ShotType Type { get; init; }
    public Foot Foot { get; init; }
    public float Charge { get; init; }
    /// <summary>Standard deviation of the sideways error, degrees.</summary>
    public float SigmaH { get; init; }
    /// <summary>Standard deviation of the vertical error, degrees.</summary>
    public float SigmaV { get; init; }
    /// <summary>The multipliers that make up M, by name: speed, angle, foot, pressure, power, air, type.</summary>
    public IReadOnlyList<ShotFactor> Factors { get; init; } = Array.Empty<ShotFactor>();
    /// <summary>Launch speed, m/s.</summary>
    public float Speed { get; init; }
    /// <summary>Launch spin, radians per second.</summary>
    public Vector3 Spin { get; init; }
    /// <summary>Distance from the ball to the goal centre, metres.</summary>
    public float Distance { get; init; }
    /// <summary>Launch velocity after the error draw.</summary>
    public Vector3 Launch { get; init; }
    /// <summary>Intended yaw toward the aimed point, radians (0 = +X).</summary>
    public float IntendedYaw { get; init; }
    /// <summary>The sideways error actually drawn, degrees (launch yaw minus intended yaw).</summary>
    public float YawErrorDeg { get; init; }
    /// <summary>The vertical error actually drawn, degrees.</summary>
    public float PitchErrorDeg { get; init; }
    /// <summary>Elevation before error, degrees.</summary>
    public float ElevationDeg { get; init; }
    /// <summary>Simulation tick of the strike.</summary>
    public int Tick { get; init; }

    /// <summary>The value of one named factor, or 1 if absent.</summary>
    public float Factor(string name)
    {
        foreach (var f in Factors) if (f.Name == name) return f.Value;
        return 1f;
    }
}

/// <summary>
/// The shot model: charge, target from the stick, aim assist, striking foot, the spread multipliers, the three shot types,
/// the error draw, launch velocity and spin.
/// </summary>
public static class ShotModel
{
    /// <summary>Foot reach plus margin: how close the ball must be for a strike.</summary>
    public static float StrikeReach(PlayerSheet s, Tuning t) => TouchModel.FootReach(s, t) + t.Shot.StrikeReachMargin;

    /// <summary>A strike needs the ball within foot reach + 0.2 m horizontally and under 0.6 m high.</summary>
    public static bool CanStrike(PlayerState p, Ball ball, Tuning t)
    {
        if (ball.Position.Y >= t.Shot.StrikeMaxHeight) return false;
        var d = new Vector2(ball.Position.X - p.Position.X, ball.Position.Z - p.Position.Z);
        return d.Length() <= StrikeReach(p.Sheet, t);
    }

    /// <summary>Charge after holding shoot for <paramref name="seconds"/>: 0 to 1 over the charge time.</summary>
    public static float ChargeAfter(float seconds, Tuning t) => Math.Clamp(seconds / t.Shot.ChargeSeconds, 0f, 1f);

    /// <summary>
    /// The striking foot: the preferred foot, unless the ball is more than 0.25 m to the other side of the facing line.
    /// Facing 0 looks along +X, so the player's right is +Z.
    /// </summary>
    public static Foot StrikingFoot(PlayerState p, Vector3 ballPosition, Tuning t)
    {
        var right = new Vector2(-MathF.Sin(p.Facing), MathF.Cos(p.Facing));
        var off = new Vector2(ballPosition.X - p.Position.X, ballPosition.Z - p.Position.Z);
        float lateral = Vector2.Dot(off, right);
        float wrong = t.Shot.WrongFootOffset;
        if (p.Sheet.PreferredFoot == Foot.Right) return lateral < -wrong ? Foot.Left : Foot.Right;
        return lateral > wrong ? Foot.Right : Foot.Left;
    }

    /// <summary>
    /// The raw aimed point on the goal line (Z): the stick direction cast from the ball to the goal line, clamped to 4.5 m
    /// either side of centre. A stick at rest means the facing direction. A direction that never reaches the goal line
    /// aims at the clamp on its side.
    /// </summary>
    public static float RawTargetZ(Vector2 stick, float facing, Vector3 ball, Pitch pitch, Tuning t)
    {
        Vector2 dir = stick.Length() < t.Movement.StickDeadZone
            ? new Vector2(MathF.Cos(facing), MathF.Sin(facing))
            : Vector2.Normalize(stick);
        float z;
        if (dir.X > 1e-3f) z = ball.Z + dir.Y / dir.X * (pitch.GoalLineX - ball.X);
        else z = MathF.Abs(dir.Y) < 1e-3f ? 0f : MathF.Sign(dir.Y) * t.Shot.MaxTargetZ;
        return Math.Clamp(z, -t.Shot.MaxTargetZ, t.Shot.MaxTargetZ);
    }

    /// <summary>
    /// Aim assist: when the raw target is outside the frame by less than 1.5 m, pull it <c>aim_assist</c> of the way toward
    /// a point 0.6 m inside the nearer post.
    /// </summary>
    public static float AssistTargetZ(float rawZ, Pitch pitch, Tuning t)
    {
        float outside = MathF.Abs(rawZ) - pitch.GoalHalfWidth;
        if (outside <= 0f || outside >= t.Shot.AssistRange) return rawZ;
        float inside = MathF.Sign(rawZ) * (pitch.GoalHalfWidth - t.Shot.AssistInset);
        return rawZ + t.AimAssist * (inside - rawZ);
    }

    /// <summary>Skill rating (0..1) for a shot from the given distance: finishing inside 18 m, long_shots beyond 25 m, linear between.</summary>
    public static float SkillR01(PlayerSheet s, float distance, Tuning t)
    {
        float fin = Rating.R01(s.Finishing), lng = Rating.R01(s.LongShots);
        float a = t.Shot.FinishingRange, b = t.Shot.LongShotRange;
        if (distance <= a) return fin;
        if (distance >= b) return lng;
        return fin + (lng - fin) * (distance - a) / (b - a);
    }

    /// <summary>sigma_h = (7.0 - 6.2 * r01(skill)) * M * error_scale, degrees.</summary>
    public static float SigmaH(float skillR01, float m, Tuning t) =>
        (t.Shot.BaseSigmaDeg - t.Shot.SigmaSkillSlope * skillR01) * m * t.ErrorScale;

    /// <summary>Peak launch speed for the shooter, m/s (shot_power).</summary>
    public static float MaxSpeed(PlayerSheet s, Tuning t) => t.Shot.Power.ForRating(s.ShotPower);

    /// <summary>Intended launch speed, m/s, for a type and charge, before the speed noise.</summary>
    public static float IntendedSpeed(PlayerSheet s, ShotType type, float charge, Tuning t)
    {
        float normal = MaxSpeed(s, t) * (t.Shot.ChargeBaseSpeed + t.Shot.ChargeSpeedSlope * charge);
        return type switch
        {
            ShotType.Finesse => normal * t.Shot.FinesseSpeedFraction,
            ShotType.Chip => normal * t.Shot.ChipSpeedFraction,
            _ => normal,
        };
    }

    /// <summary>Intended elevation, degrees, for a type and charge.</summary>
    public static float ElevationDeg(ShotType type, float charge, Tuning t) => type switch
    {
        ShotType.Finesse => t.Shot.FinesseElevationBaseDeg + t.Shot.FinesseElevationChargeDeg * charge,
        ShotType.Chip => t.Shot.ChipElevationBaseDeg + t.Shot.ChipElevationChargeDeg * charge,
        _ => t.Shot.NormalElevationBaseDeg + t.Shot.NormalElevationChargeDeg * charge,
    };

    /// <summary>The spread multipliers M = speed * angle * foot * pressure * power * air * type, each by name.</summary>
    public static ShotFactor[] Multipliers(in ShotContext c, Foot foot, float shotYaw, Tuning t)
    {
        var k = t.Shot;
        var p = c.Shooter;
        float top = Movement.TopSpeed(p.Sheet, t);
        float runFrac = Math.Clamp(p.Speed / top, 0f, 1f);
        float mSpeed = 1f + k.SpeedMultiplierSlope * runFrac;

        float angle = MathF.Abs(Movement.WrapAngle(shotYaw - p.Facing)) * 180f / MathF.PI;
        float mAngle = 1f + k.AngleMultiplierSlope * Math.Clamp((angle - k.AngleFreeDeg) / k.AngleSpanDeg, 0f, 1f);

        float mFoot = foot == p.Sheet.PreferredFoot ? 1f : 1f + k.WeakFootStep * (5 - p.Sheet.WeakFoot);

        float mPressure = 1f;
        if (c.Keeper != null)
        {
            var d = new Vector2(c.Keeper.Position.X - p.Position.X, c.Keeper.Position.Z - p.Position.Z).Length();
            float closeness = Math.Clamp((k.PressureFarMetres - d) / (k.PressureFarMetres - k.PressureNearMetres), 0f, 1f);
            mPressure = 1f + k.PressureSlope * (1f - Rating.R01(p.Sheet.Composure)) * closeness;
        }

        float mPower = c.Charge < k.PowerThreshold
            ? 1f
            : 1f + (k.PowerMaxMultiplier - 1f) * (c.Charge - k.PowerThreshold) / (1f - k.PowerThreshold);

        float mAir = c.BallPosition.Y - t.Ball.Radius > k.AirHeight ? k.AirMultiplier : 1f;

        float mType = c.Type switch
        {
            ShotType.Finesse => k.FinesseTypeMultiplier,
            ShotType.Chip => k.ChipTypeMultiplier,
            _ => k.NormalTypeMultiplier,
        };

        return new[]
        {
            new ShotFactor("speed", mSpeed), new ShotFactor("angle", mAngle), new ShotFactor("foot", mFoot),
            new ShotFactor("pressure", mPressure), new ShotFactor("power", mPower), new ShotFactor("air", mAir),
            new ShotFactor("type", mType),
        };
    }

    /// <summary>
    /// Computes a strike: target, foot, multipliers, error draw, launch velocity and spin. All randomness comes from
    /// <paramref name="rng"/>, in a fixed order (yaw, pitch, speed, then spin).
    /// </summary>
    public static ShotResult Compute(in ShotContext c, Pitch pitch, Tuning t, SimRng rng, int tick = 0)
    {
        var k = t.Shot;
        var p = c.Shooter;
        Vector3 ball = c.BallPosition;

        float yaw0 = IntendedYaw(c, pitch, t, out float targetZ);
        float dx = pitch.GoalLineX - ball.X;

        Foot foot = c.Foot ?? StrikingFoot(p, ball, t);
        float distance = new Vector2(pitch.GoalLineX - ball.X, ball.Z).Length();
        var factors = Multipliers(c, foot, yaw0, t);
        float m = 1f;
        foreach (var f in factors) m *= f.Value;
        float sigmaH = SigmaH(SkillR01(p.Sheet, distance, t), m, t);
        float sigmaV = k.VerticalRatio * sigmaH;

        float elev = ElevationDeg(c.Type, c.Charge, t);
        float vIntended = IntendedSpeed(p.Sheet, c.Type, c.Charge, t);

        float yawErr = rng.Normal(0f, sigmaH);
        float pitchErr = rng.Normal(0f, sigmaV);
        float speed = MathF.Max(0.1f, vIntended * rng.Normal(1f, k.SpeedNoise));

        const float deg = MathF.PI / 180f;
        float yaw = yaw0 + yawErr * deg;
        float pitchRad = (elev + pitchErr) * deg;
        var dir = new Vector3(MathF.Cos(pitchRad) * MathF.Cos(yaw), MathF.Sin(pitchRad), MathF.Cos(pitchRad) * MathF.Sin(yaw));
        Vector3 launch = dir * speed;

        Vector3 spin;
        const float rev = 2f * MathF.PI;
        switch (c.Type)
        {
            case ShotType.Finesse:
            {
                // Bends away from the striking foot's side: a right foot bends left. Force = spin x velocity, so spin = v x bend.
                var rightOfShot = new Vector3(-MathF.Sin(yaw), 0f, MathF.Cos(yaw));
                Vector3 bend = foot == Foot.Right ? -rightOfShot : rightOfShot;
                spin = Vector3.Normalize(Vector3.Cross(dir, bend)) * (k.FinesseSpinRev.ForRating(p.Sheet.Curve) * rev);
                break;
            }
            case ShotType.Chip:
            {
                // Backspin lifts the ball: spin = v x up.
                Vector3 axis = Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitY));
                spin = axis * (rng.Uniform(k.ChipBackspinMinRev, k.ChipBackspinMaxRev) * rev);
                break;
            }
            default:
            {
                Vector3 axis = rng.UnitVector();
                spin = axis * (rng.Uniform(0f, k.NormalSpinMaxRev) * rev);
                break;
            }
        }

        // Aimed point on the goal plane, ignoring drag and spin.
        float dh = MathF.Abs(dx) < 1e-3f ? 0f : dx / MathF.Cos(yaw0);
        float tFlight = dh / MathF.Max(0.1f, vIntended * MathF.Cos(elev * deg));
        float aimedY = ball.Y + vIntended * MathF.Sin(elev * deg) * tFlight - 0.5f * t.Ball.Gravity * tFlight * tFlight;

        return new ShotResult
        {
            Aimed = new Vector2(targetZ, aimedY),
            Type = c.Type,
            Foot = foot,
            Charge = c.Charge,
            SigmaH = sigmaH,
            SigmaV = sigmaV,
            Factors = factors,
            Speed = speed,
            Spin = spin,
            Distance = distance,
            Launch = launch,
            IntendedYaw = yaw0,
            YawErrorDeg = yawErr,
            PitchErrorDeg = pitchErr,
            ElevationDeg = elev,
            Tick = tick,
        };
    }

    /// <summary>The intended yaw (radians, 0 = +X) and the aimed Z on the goal line, before any error is drawn.</summary>
    static float IntendedYaw(in ShotContext c, Pitch pitch, Tuning t, out float targetZ)
    {
        var p = c.Shooter;
        Vector3 ball = c.BallPosition;
        float rawZ = RawTargetZ(c.Stick, p.Facing, ball, pitch, t);
        targetZ = AssistTargetZ(rawZ, pitch, t);
        float dx = pitch.GoalLineX - ball.X, dz = targetZ - ball.Z;
        float yaw0 = MathF.Abs(dx) < 1e-3f && MathF.Abs(dz) < 1e-3f ? p.Facing : MathF.Atan2(dz, dx);
        if (dx <= 0f) yaw0 = StickYawOrFacing(c.Stick, p.Facing, t);
        return yaw0;
    }

    /// <summary>
    /// What a strike made right now would aim at and how wide it would scatter: no random draw, no state change.
    /// Uses the same aim, foot and spread code as <see cref="Compute"/>.
    /// </summary>
    public static ShotPreview Preview(in ShotContext c, Pitch pitch, Tuning t)
    {
        float yaw0 = IntendedYaw(c, pitch, t, out float targetZ);
        Foot foot = c.Foot ?? StrikingFoot(c.Shooter, c.BallPosition, t);
        float distance = new Vector2(pitch.GoalLineX - c.BallPosition.X, c.BallPosition.Z).Length();
        float m = 1f;
        foreach (var f in Multipliers(c, foot, yaw0, t)) m *= f.Value;
        float sigmaH = SigmaH(SkillR01(c.Shooter.Sheet, distance, t), m, t);
        return new ShotPreview(c.BallPosition, yaw0, targetZ, sigmaH);
    }

    static float StickYawOrFacing(Vector2 stick, float facing, Tuning t) =>
        stick.Length() < t.Movement.StickDeadZone ? facing : MathF.Atan2(stick.Y, stick.X);
}
