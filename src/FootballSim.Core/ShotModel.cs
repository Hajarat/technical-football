using System.Numerics;

namespace FootballSim.Core;

/// <summary>A read-only look at the strike that would happen now: where it starts, its intended yaw, the aimed Z and sigma_h.</summary>
/// <param name="Origin">Ball position.</param>
/// <param name="IntendedYaw">Radians, 0 = +X.</param>
/// <param name="AimedZ">Aimed Z on the goal line, after aim assist.</param>
/// <param name="SigmaH">Standard deviation of the sideways error, degrees.</param>
public readonly record struct ShotPreview(Vector3 Origin, float IntendedYaw, float AimedZ, float SigmaH);

/// <summary>The foot and the part of it a shot is struck with (see <see cref="ShotModel.ChooseStrike"/>).</summary>
public readonly record struct StrikeChoice(Foot Foot, FootSurface Surface);

/// <summary>One named multiplier that fed a shot's spread or its speed.</summary>
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
    /// <summary>The striking foot when it was already decided (at the release); null chooses foot and surface here (see <see cref="ShotModel.ChooseStrike"/>).</summary>
    public Foot? Foot;
    /// <summary>With <see cref="Foot"/>: the part of the foot decided with it; null means the shot type's own (see <see cref="ShotModel.SurfaceFor"/>).</summary>
    public FootSurface? Surface;
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
    /// <summary>The part of the foot it was struck with.</summary>
    public FootSurface Surface { get; init; }
    public float Charge { get; init; }
    /// <summary>Standard deviation of the sideways error, degrees.</summary>
    public float SigmaH { get; init; }
    /// <summary>Standard deviation of the random part of the vertical error, degrees.</summary>
    public float SigmaV { get; init; }
    /// <summary>The ideal charge for the shot's distance (see <see cref="ShotModel.IdealCharge"/>).</summary>
    public float IdealCharge { get; init; }
    /// <summary>
    /// The mean vertical error from missing the ideal charge, degrees (positive rises). Zero for a chip, whose miss goes into
    /// its speed instead.
    /// </summary>
    public float ChargeMissDeg { get; init; }
    /// <summary>The multipliers that make up M, by name: speed, angle, mouth, foot, surface, pressure, power, air, type.</summary>
    public IReadOnlyList<ShotFactor> Factors { get; init; } = Array.Empty<ShotFactor>();
    /// <summary>The multipliers on the intended launch speed, by name: angle, weak_foot, surface.</summary>
    public IReadOnlyList<ShotFactor> SpeedFactors { get; init; } = Array.Empty<ShotFactor>();
    /// <summary>The multiplier on the side spin (below 1 for a weak-foot strike).</summary>
    public float CurlFactor { get; init; } = 1f;
    /// <summary>The body angle: how far the intended direction was from where the shooter faced, degrees (0 to 180).</summary>
    public float BodyAngleDeg { get; init; }
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
    /// <summary>The vertical error, degrees: the charge miss plus the random part drawn.</summary>
    public float PitchErrorDeg { get; init; }
    /// <summary>Elevation before error, degrees.</summary>
    public float ElevationDeg { get; init; }
    /// <summary>Simulation tick of the strike.</summary>
    public int Tick { get; init; }

    /// <summary>The value of one named spread factor, or 1 if absent.</summary>
    public float Factor(string name)
    {
        foreach (var f in Factors) if (f.Name == name) return f.Value;
        return 1f;
    }

    /// <summary>The value of one named speed factor, or 1 if absent.</summary>
    public float SpeedFactor(string name)
    {
        foreach (var f in SpeedFactors) if (f.Name == name) return f.Value;
        return 1f;
    }
}

/// <summary>
/// The shot model: charge, target from the stick, aim assist, the striking foot and surface, the spread and speed multipliers
/// (body angle, goal mouth, weak foot), the five shot types, the ideal charge, the error draw, launch velocity and spin.
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

    /// <summary>The ideal charge for a shot from <paramref name="distance"/> metres from the goal centre.</summary>
    public static float IdealCharge(float distance, Tuning t) => Math.Clamp(t.Shot.IdealCharge.Evaluate(distance), 0f, 1f);

    /// <summary>Distance from a ball position to the goal centre on the goal line, metres (ground plane).</summary>
    public static float GoalDistance(Vector3 ball, Pitch pitch) => new Vector2(pitch.GoalLineX - ball.X, ball.Z).Length();

    /// <summary>The shot type for the modifiers held: chip wins; driven with finesse is a low finesse; then driven, finesse, power.</summary>
    public static ShotType TypeFor(bool finesse, bool chip, bool driven) =>
        chip ? ShotType.Chip
        : driven && finesse ? ShotType.LowFinesse
        : driven ? ShotType.Driven
        : finesse ? ShotType.Finesse
        : ShotType.Power;

    /// <summary>The shot action kind for a shot type.</summary>
    public static ActionKind KindFor(ShotType type) => type switch
    {
        ShotType.Finesse => ActionKind.Finesse,
        ShotType.Chip => ActionKind.Chip,
        ShotType.Driven => ActionKind.Driven,
        ShotType.LowFinesse => ActionKind.LowFinesse,
        _ => ActionKind.Power,
    };

    /// <summary>The part of the foot a shot type is struck with: laces for power and driven, the inside for the rest.</summary>
    public static FootSurface SurfaceFor(ShotType type) => type is ShotType.Power or ShotType.Driven ? FootSurface.Laces : FootSurface.Inside;

    /// <summary>
    /// The ball-out angle for a foot, degrees: how far the intended direction <paramref name="yaw"/> is from the facing, positive
    /// toward that foot's own side (a right foot's is the player's right, +Z when he faces +X). Straight behind counts as across
    /// the body for the strong foot.
    /// </summary>
    public static float OpenAngleDeg(Foot foot, Foot preferred, float facing, float yaw)
    {
        float right = Movement.WrapAngle(yaw - facing) * 180f / MathF.PI;
        if (MathF.Abs(right) > 179.5f) right = preferred == Foot.Right ? -180f : 180f;
        return foot == Foot.Right ? right : -right;
    }

    /// <summary>The body angle, degrees (0 to 180): how far the intended direction is from where the shooter faces.</summary>
    public static float BodyAngleDeg(float facing, float yaw) => MathF.Abs(Movement.WrapAngle(yaw - facing)) * 180f / MathF.PI;

    /// <summary>The wind-up added for a body angle, seconds (see <see cref="ShotTuning.AngleExtraWindupS"/>).</summary>
    public static float ExtraWindupSeconds(float facing, float yaw, Tuning t) =>
        MathF.Max(0f, t.Shot.AngleExtraWindupS.Evaluate(BodyAngleDeg(facing, yaw)));

    /// <summary>How far toward its own side a foot can strike the shot type, degrees.</summary>
    public static float OpenMaxDeg(ShotType type, Tuning t) =>
        type is ShotType.Finesse or ShotType.LowFinesse ? t.Shot.FinesseOpenMaxDeg : t.Shot.OpenMaxDeg;

    /// <summary>How far toward the strong foot's side its outside may strike, degrees: by the curve rating, never past the foot's limit.</summary>
    public static float OutsideMaxDeg(PlayerSheet s, ShotType type, Tuning t) =>
        MathF.Min(OpenMaxDeg(type, t), t.Shot.OutsideMaxDeg.ForRating(s.Curve));

    /// <summary>
    /// The striking foot and surface, decided once for a shot (at the release). Each foot is scored and the cheaper one strikes
    /// (the strong foot on a tie):
    /// <list type="bullet">
    /// <item>A foot cannot strike a ball leaving further toward its own side than its limit (<see cref="OpenMaxDeg"/>): beyond
    /// that only the other foot can; across the body a foot reaches all the way round.</item>
    /// <item>Opening the hips toward its own side costs, from outside_min_deg up to the limit (w_open).</item>
    /// <item>With the outside_foot trait the strong foot strikes that band with its outside instead, up to
    /// <see cref="OutsideMaxDeg"/>, for the flat cost w_outside.</item>
    /// <item>The ball on the other foot's side of the body costs (w_ball), and so does the weak foot, by its stars (w_weak).</item>
    /// <item>In a dribble, <paramref name="rightTick"/> and <paramref name="leftTick"/> are each foot's earliest strike tick
    /// (negative: that foot has none): waiting longer for a foot than for the other costs (w_wait), a foot more than
    /// foot_wait_max_s later is passed over while the other can strike, and a foot with no contact is taken only when neither
    /// has one. Both zero (the default): no timing, as for a ball at the feet.</item>
    /// </list>
    /// </summary>
    public static StrikeChoice ChooseStrike(PlayerSheet sheet, Vector3 position, float facing, Vector3 ball, float yaw, ShotType type,
        Tuning t, int rightTick = 0, int leftTick = 0)
    {
        var k = t.Shot;
        Foot strong = sheet.PreferredFoot, weak = strong == Foot.Right ? Foot.Left : Foot.Right;
        float costStrong = Cost(strong, out var surfStrong), costWeak = Cost(weak, out var surfWeak);
        return costWeak < costStrong ? new StrikeChoice(weak, surfWeak) : new StrikeChoice(strong, surfStrong);

        float Cost(Foot f, out FootSurface surface)
        {
            surface = SurfaceFor(type);
            float s = OpenAngleDeg(f, strong, facing, yaw);
            float max = OpenMaxDeg(type, t), free = MathF.Min(k.OutsideMinDeg, max);
            float cost = k.WOpen * (max > free ? Math.Clamp((s - free) / (max - free), 0f, 1f) : s > free ? 1f : 0f);
            if (f == strong && sheet.Has(PlayerTrait.OutsideFoot) && s > free && s <= OutsideMaxDeg(sheet, type, t))
            {
                cost = k.WOutside;
                surface = FootSurface.Outside;
            }
            var right = new Vector2(-MathF.Sin(facing), MathF.Cos(facing));
            float lateral = Vector2.Dot(new Vector2(ball.X - position.X, ball.Z - position.Z), right);
            float otherSide = f == Foot.Right ? -lateral : lateral;
            cost += k.WBall * Math.Clamp((otherSide - k.FootBallFreeM) / MathF.Max(1e-3f, k.FootBallSpanM), 0f, 1f);
            if (f == weak) cost += k.WWeak * (5 - sheet.WeakFoot) / 4f;
            if (s > max) cost += CannotOpen;
            if (rightTick != 0 || leftTick != 0)
            {
                int mine = f == Foot.Right ? rightTick : leftTick, other = f == Foot.Right ? leftTick : rightTick;
                if (mine < 0) cost += NoContact;
                else if (other >= 0 && mine > other)
                {
                    float wait = (mine - other) * t.Dt;
                    cost += k.WWait * wait / MathF.Max(1e-3f, k.FootWaitMaxS);
                    if (wait > k.FootWaitMaxS) cost += TooLate;
                }
            }
            return cost;
        }
    }

    // What rules a foot out, as costs above any the weights add up to: a foot with no contact at all is the last resort, then a
    // foot that cannot open that far, then a foot whose swing comes too late.
    const float NoContact = 1000f, CannotOpen = 100f, TooLate = 10f;

    /// <summary>
    /// How open the goal mouth is from the ball, 0 to 1: the angle between the posts as a share of the angle they would make
    /// seen square on from the same distance. 1 in front of goal, falling toward 0 along the goal line; 0 on or behind it.
    /// </summary>
    public static float GoalMouthOpen(Vector3 ball, Pitch pitch)
    {
        float dx = pitch.GoalLineX - ball.X;
        if (dx <= 1e-4f) return 0f;
        float near = MathF.Atan2(pitch.GoalHalfWidth - ball.Z, dx), far = MathF.Atan2(-pitch.GoalHalfWidth - ball.Z, dx);
        float square = 2f * MathF.Atan2(pitch.GoalHalfWidth, GoalDistance(ball, pitch));
        return square <= 1e-6f ? 0f : Math.Clamp((near - far) / square, 0f, 1f);
    }

    /// <summary>
    /// The raw aimed point on the goal line (Z): the stick direction cast from the ball to the goal line, clamped to max_target_z
    /// either side of centre (wider than the frame, so a wild aim misses). A stick at rest means the facing direction. A direction that never reaches the goal line
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

    /// <summary>sigma_h = (base_sigma_deg - sigma_skill_slope * r01(skill)) * M * error_scale, degrees.</summary>
    public static float SigmaH(float skillR01, float m, Tuning t) =>
        (t.Shot.BaseSigmaDeg - t.Shot.SigmaSkillSlope * skillR01) * m * t.ErrorScale;

    /// <summary>Peak launch speed for the shooter, m/s (shot_power).</summary>
    public static float MaxSpeed(PlayerSheet s, Tuning t) => t.Shot.Power.ForRating(s.ShotPower);

    /// <summary>Intended launch speed, m/s, for a type and charge, before the speed noise.</summary>
    public static float IntendedSpeed(PlayerSheet s, ShotType type, float charge, Tuning t)
    {
        float power = MaxSpeed(s, t) * (t.Shot.ChargeBaseSpeed + t.Shot.ChargeSpeedSlope * charge);
        return type switch
        {
            ShotType.Finesse => power * t.Shot.FinesseSpeedFraction,
            ShotType.LowFinesse => power * t.Shot.LowFinesseSpeedFraction,
            ShotType.Chip => power * t.Shot.ChipSpeedFraction,
            ShotType.Driven => power * t.Shot.DrivenSpeedFactor,
            _ => power,
        };
    }

    /// <summary>Intended elevation, degrees, for a type and charge.</summary>
    public static float ElevationDeg(ShotType type, float charge, Tuning t) => type switch
    {
        ShotType.Finesse => t.Shot.FinesseElevationBaseDeg + t.Shot.FinesseElevationChargeDeg * charge,
        ShotType.LowFinesse => t.Shot.LowFinesseElevationBaseDeg + t.Shot.LowFinesseElevationChargeDeg * charge,
        ShotType.Chip => t.Shot.ChipElevationBaseDeg + t.Shot.ChipElevationChargeDeg * charge,
        ShotType.Driven => t.Shot.DrivenElevationBaseDeg + t.Shot.DrivenElevationChargeDeg * charge,
        _ => t.Shot.PowerElevationBaseDeg + t.Shot.PowerElevationChargeDeg * charge,
    };

    /// <summary>
    /// The spread multipliers M = speed * angle * mouth * foot * surface * pressure * power * air * type, each by name. The angle
    /// is the body angle (<see cref="BodyAngleDeg"/>) through angle_accuracy; the mouth is <see cref="GoalMouthOpen"/> through
    /// goal_mouth_accuracy.
    /// </summary>
    public static ShotFactor[] Multipliers(in ShotContext c, StrikeChoice strike, float shotYaw, Pitch pitch, Tuning t)
    {
        var k = t.Shot;
        var p = c.Shooter;
        float top = Movement.TopSpeed(p.Sheet, t);
        float runFrac = Math.Clamp(p.Speed / top, 0f, 1f);
        float mSpeed = 1f + k.SpeedMultiplierSlope * runFrac;

        float mAngle = MathF.Max(0f, k.AngleAccuracy.Evaluate(BodyAngleDeg(p.Facing, shotYaw)));
        float mMouth = MathF.Max(0f, k.GoalMouthAccuracy.Evaluate(GoalMouthOpen(c.BallPosition, pitch)));

        float mFoot = strike.Foot == p.Sheet.PreferredFoot ? 1f : 1f + k.WeakFootStep * (5 - p.Sheet.WeakFoot);
        float mSurface = strike.Surface == FootSurface.Outside ? k.OutsideAccuracy : 1f;

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
            ShotType.LowFinesse => k.LowFinesseTypeMultiplier,
            ShotType.Chip => k.ChipTypeMultiplier,
            ShotType.Driven => k.DrivenTypeMultiplier,
            _ => k.PowerTypeMultiplier,
        };

        return new[]
        {
            new ShotFactor("speed", mSpeed), new ShotFactor("angle", mAngle), new ShotFactor("mouth", mMouth),
            new ShotFactor("foot", mFoot), new ShotFactor("surface", mSurface),
            new ShotFactor("pressure", mPressure), new ShotFactor("power", mPower), new ShotFactor("air", mAir),
            new ShotFactor("type", mType),
        };
    }

    /// <summary>
    /// The multipliers on the intended launch speed, each by name: angle (the body angle through angle_power), weak_foot (a
    /// weak-foot strike, by its stars) and surface (the outside of the foot).
    /// </summary>
    public static ShotFactor[] SpeedMultipliers(PlayerState p, StrikeChoice strike, float shotYaw, Tuning t)
    {
        var k = t.Shot;
        float angle = MathF.Max(0f, k.AnglePower.Evaluate(BodyAngleDeg(p.Facing, shotYaw)));
        float weak = strike.Foot == p.Sheet.PreferredFoot ? 1f : MathF.Max(0f, k.WeakFootPower.Evaluate(p.Sheet.WeakFoot));
        float surface = strike.Surface == FootSurface.Outside ? k.OutsideSpeed : 1f;
        return new[] { new ShotFactor("angle", angle), new ShotFactor("weak_foot", weak), new ShotFactor("surface", surface) };
    }

    /// <summary>The strike a context asks for: its own foot and surface when decided, else chosen here without timing.</summary>
    static StrikeChoice StrikeFor(in ShotContext c, float yaw, Tuning t) => c.Foot is Foot f
        ? new StrikeChoice(f, c.Surface ?? SurfaceFor(c.Type))
        : ChooseStrike(c.Shooter.Sheet, c.Shooter.Position, c.Shooter.Facing, c.BallPosition, yaw, c.Type, t);

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

        var strike = StrikeFor(c, yaw0, t);
        Foot foot = strike.Foot;
        float distance = new Vector2(pitch.GoalLineX - ball.X, ball.Z).Length();
        var factors = Multipliers(c, strike, yaw0, pitch, t);
        float m = 1f;
        foreach (var f in factors) m *= f.Value;
        var speedFactors = SpeedMultipliers(p, strike, yaw0, t);
        float speedM = 1f;
        foreach (var f in speedFactors) speedM *= f.Value;
        float curl = foot == p.Sheet.PreferredFoot ? 1f : MathF.Max(0f, k.WeakFootCurl.Evaluate(p.Sheet.WeakFoot));
        float sigmaH = SigmaH(SkillR01(p.Sheet, distance, t), m, t);
        float sigmaV = k.VerticalRatio * sigmaH;

        float elev = ElevationDeg(c.Type, c.Charge, t);
        float vIntended = IntendedSpeed(p.Sheet, c.Type, c.Charge, t) * speedM;

        // Missing the ideal charge for the distance: too much rises, too little drives low. A chip's height is set by its
        // loft, so its miss changes how far it carries instead.
        float ideal = IdealCharge(distance, t);
        float miss = c.Charge - ideal;
        bool chip = c.Type == ShotType.Chip;
        float missDeg = chip ? 0f : k.ChargeMissPitchDeg * miss;
        float speedMiss = chip ? MathF.Max(0.1f, 1f + k.ChipChargeMissSpeed * miss) : 1f;

        float yawErr = rng.Normal(0f, sigmaH);
        float pitchErr = missDeg + rng.Normal(0f, sigmaV);
        float speed = MathF.Max(0.1f, vIntended * speedMiss * rng.Normal(1f, k.SpeedNoise));

        const float deg = MathF.PI / 180f;
        float yaw = yaw0 + yawErr * deg;
        float pitchRad = (elev + pitchErr) * deg;
        var dir = new Vector3(MathF.Cos(pitchRad) * MathF.Cos(yaw), MathF.Sin(pitchRad), MathF.Cos(pitchRad) * MathF.Sin(yaw));
        Vector3 launch = dir * speed;

        // Side spin (about the axis that bends the ball sideways) grows with the launch speed and the curve rating.
        Vector3 spin;
        const float rev = 2f * MathF.PI;
        float speedScale = speed / MathF.Max(0.1f, k.SpinReferenceSpeed);
        var rightOfShot = new Vector3(-MathF.Sin(yaw), 0f, MathF.Cos(yaw));
        switch (c.Type)
        {
            case ShotType.Finesse:
            case ShotType.LowFinesse:
            {
                // Bends away from the striking foot's side: a right foot bends left (its outside bends the other way).
                // Force = spin x velocity, so spin = v x bend.
                Vector3 bend = (foot == Foot.Right) != (strike.Surface == FootSurface.Outside) ? -rightOfShot : rightOfShot;
                spin = Vector3.Normalize(Vector3.Cross(dir, bend)) * (k.FinesseSpinRev.ForRating(p.Sheet.Curve) * speedScale * curl * rev);
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
                // Power: a light curl to either side; driven: barely any. Signed amount, so the side is part of the draw.
                var range = c.Type == ShotType.Driven ? k.DrivenSpinRev : k.PowerSpinRev;
                float amount = rng.Uniform(-1f, 1f) * range.ForRating(p.Sheet.Curve) * speedScale * curl;
                spin = Vector3.Normalize(Vector3.Cross(dir, rightOfShot)) * (amount * rev);
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
            Surface = strike.Surface,
            Charge = c.Charge,
            SigmaH = sigmaH,
            SigmaV = sigmaV,
            IdealCharge = ideal,
            ChargeMissDeg = missDeg,
            Factors = factors,
            SpeedFactors = speedFactors,
            CurlFactor = curl,
            BodyAngleDeg = BodyAngleDeg(p.Facing, yaw0),
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

    /// <summary>
    /// The intended yaw (radians, 0 = +X) and the aimed Z on the goal line, before any error is drawn. A ball on or behind the
    /// goal line is aimed where the stick points (where the shooter faces with the stick at rest).
    /// </summary>
    public static float IntendedYaw(in ShotContext c, Pitch pitch, Tuning t, out float targetZ)
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
        float distance = new Vector2(pitch.GoalLineX - c.BallPosition.X, c.BallPosition.Z).Length();
        float m = 1f;
        foreach (var f in Multipliers(c, StrikeFor(c, yaw0, t), yaw0, pitch, t)) m *= f.Value;
        float sigmaH = SigmaH(SkillR01(c.Shooter.Sheet, distance, t), m, t);
        return new ShotPreview(c.BallPosition, yaw0, targetZ, sigmaH);
    }

    /// <summary>
    /// True when a ball flying free from this state (no keeper, no player) goes into the goal before it meets a board, the
    /// frame or the ceiling, or runs out of <paramref name="seconds"/>: whether a shot as struck was on target.
    /// </summary>
    public static bool FliesIn(Vector3 position, Vector3 velocity, Vector3 spin, Pitch pitch, Tuning t, float seconds = 6f)
    {
        var b = new Ball(t.Ball) { Position = position, Velocity = velocity, Spin = spin };
        for (float left = seconds; left > 0f; left -= t.Dt)
        {
            var r = b.Step(t.Dt, pitch, null);
            if (pitch.IsGoal(b.Position, b.Radius)) return true;
            if (r.BoardHit || r.FrameHit) return false;
        }
        return false;
    }

    static float StickYawOrFacing(Vector2 stick, float facing, Tuning t) =>
        stick.Length() < t.Movement.StickDeadZone ? facing : MathF.Atan2(stick.Y, stick.X);
}
