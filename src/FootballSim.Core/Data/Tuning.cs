namespace FootballSim.Core;

/// <summary>A value that runs linearly from <see cref="At1"/> at rating 1 to <see cref="At99"/> at rating 99 (the spec's lerp(a, b)).</summary>
public sealed class RatingRange
{
    public float At1 { get; set; }
    public float At99 { get; set; }

    public RatingRange() { }
    public RatingRange(float at1, float at99) { At1 = at1; At99 = at99; }

    /// <summary>Value at a 0..1 rating fraction.</summary>
    public float Lerp(float r01) => At1 + (At99 - At1) * r01;

    /// <summary>Value for an integer rating 1..99.</summary>
    public float ForRating(float rating) => Lerp(Rating.R01(rating));
}

/// <summary>Ball flight and ground constants.</summary>
public sealed class BallTuning
{
    public float Mass { get; set; } = 0.43f;
    public float Radius { get; set; } = 0.11f;
    public float CrossSection { get; set; } = 0.038f;
    public float AirDensity { get; set; } = 1.225f;
    public float DragCoefficient { get; set; } = 0.25f;
    public float MaxLiftCoefficient { get; set; } = 0.35f;
    /// <summary>Fraction of spin lost per second in the air.</summary>
    public float SpinDecayPerSecond { get; set; } = 0.02f;
    public float BounceRestitution { get; set; } = 0.60f;
    public float BounceSidewaysKept { get; set; } = 0.75f;
    /// <summary>Fraction of spin kept at each ground bounce (not in the spec; added so a bounce does not keep full spin).</summary>
    public float BounceSpinKept { get; set; } = 0.5f;
    /// <summary>Rolling deceleration, m/s squared.</summary>
    public float RollDeceleration { get; set; } = 1.2f;
    public float Gravity { get; set; } = 9.81f;
    /// <summary>Rolling speed below which the ball is at rest, m/s.</summary>
    public float RestSpeed { get; set; } = 0.05f;
    /// <summary>A bounce that would leave less vertical speed than this becomes rolling, m/s.</summary>
    public float MinBounceSpeed { get; set; } = 0.6f;
    /// <summary>A bounce arriving faster than this raises a Bounce event, m/s.</summary>
    public float BounceEventSpeed { get; set; } = 1.0f;
}

/// <summary>Goal frame and net constants. Goal size itself is in the scenario's pitch.</summary>
public sealed class FrameTuning
{
    public float PostRadius { get; set; } = 0.06f;
    public float Restitution { get; set; } = 0.70f;
    /// <summary>After a goal the ball keeps this fraction of its speed.</summary>
    public float NetSpeedKept { get; set; } = 0.10f;
    /// <summary>How far behind the goal line the net stops the ball, metres.</summary>
    public float NetDepth { get; set; } = 2.0f;
}

/// <summary>Attacker speed, acceleration, turning, balance and weight.</summary>
public sealed class MovementTuning
{
    public RatingRange SprintSpeed { get; set; } = new(6.0f, 9.8f);
    /// <summary>Speed time constant, seconds (acceleration rating).</summary>
    public RatingRange TimeConstant { get; set; } = new(1.10f, 0.45f);
    /// <summary>Fraction of top speed when not sprinting ("jog is 60%").</summary>
    public float JogFraction { get; set; } = 0.6f;
    public float SprintFraction { get; set; } = 1.0f;
    public float CloseControlFraction { get; set; } = 0.35f;
    /// <summary>Turn rate, degrees per second (agility rating).</summary>
    public RatingRange TurnRateDeg { get; set; } = new(180f, 420f);
    /// <summary>Turn rate is scaled by (1 - this * speed fraction).</summary>
    public float TurnSpeedPenalty { get; set; } = 0.5f;
    public float SharpTurnDeg { get; set; } = 60f;
    public float SharpTurnWindow { get; set; } = 0.2f;
    /// <summary>Fraction of speed lost on a sharp turn (balance rating).</summary>
    public RatingRange BalanceLoss { get; set; } = new(0.50f, 0.15f);
    public float WeightReference { get; set; } = 75f;
    public float WeightRange { get; set; } = 25f;
    public float WeightTurnSlope { get; set; } = 0.15f;
    public float WeightFactorMin { get; set; } = 0.85f;
    public float WeightFactorMax { get; set; } = 1.15f;
    /// <summary>Stick deflection below this counts as no input.</summary>
    public float StickDeadZone { get; set; } = 0.05f;
}

/// <summary>
/// Touches. A touch sets the ball's speed relative to the player's: in possession the ball leaves at the player's speed along
/// the stick plus a small overshoot, so touches are small and frequent and the ball stays at the feet.
/// </summary>
public sealed class TouchTuning
{
    public float ReachBase { get; set; } = 0.55f;
    public float ReachPerHeight { get; set; } = 0.30f;
    public float HeightMin { get; set; } = 1.60f;
    public float HeightSpan { get; set; } = 0.40f;
    public float MaxBallHeight { get; set; } = 0.4f;
    /// <summary>Top speed with the ball as a fraction of top speed (dribbling rating).</summary>
    public RatingRange TopSpeedWithBall { get; set; } = new(0.80f, 0.96f);
    /// <summary>Sprint input at or above this counts as sprinting (touch cadence and overshoot).</summary>
    public float SprintThreshold { get; set; } = 0.5f;
    /// <summary>Stick deflection below this asks for no dribble touch; in possession it asks for a trap.</summary>
    public float StickThreshold { get; set; } = 0.1f;

    /// <summary>Minimum seconds between touches in close control.</summary>
    public float CloseControlCadence { get; set; } = 0.16f;
    /// <summary>Minimum seconds between touches at a jog.</summary>
    public float JogCadence { get; set; } = 0.22f;
    /// <summary>Minimum seconds between touches at a sprint.</summary>
    public float SprintCadence { get; set; } = 0.30f;

    /// <summary>Ball speed after a touch is the player's speed along the touch times (1 + overshoot).</summary>
    public float CloseControlOvershoot { get; set; } = 0.02f;
    public float JogOvershoot { get; set; } = 0.05f;
    /// <summary>Sprint overshoot (dribbling rating).</summary>
    public RatingRange SprintOvershoot { get; set; } = new(0.22f, 0.06f);
    /// <summary>Ball speed after a touch never falls below this, m/s (a touch from a standstill).</summary>
    public float MinPush { get; set; } = 2.0f;
    public float CloseControlMinPush { get; set; } = 1.2f;
    /// <summary>Standard deviation of the touch speed factor (dribbling), before the speed scaling.</summary>
    public RatingRange SpeedError { get; set; } = new(0.10f, 0.02f);
    /// <summary>Standard deviation of the touch direction, degrees (ball_control), before the speed scaling.</summary>
    public RatingRange DirectionErrorDeg { get; set; } = new(7f, 1f);
    /// <summary>Touch errors scale by (base + slope * speed fraction).</summary>
    public float ErrorSpeedBase { get; set; } = 0.4f;
    public float ErrorSpeedSlope { get; set; } = 0.6f;
    /// <summary>A weak-foot touch multiplies its errors by (1 + this * (5 - weak_foot stars)).</summary>
    public float WeakFootErrorStep { get; set; } = 0.10f;
    /// <summary>The ball must be this far past the body's centreline on the other side to be touched with the other foot, metres.</summary>
    public float FootSideOffset { get; set; } = 0.15f;

    /// <summary>A wanted direction this far off the ball's travel allows an early touch, degrees.</summary>
    public float TurnTriggerDeg { get; set; } = 20f;
    /// <summary>Earliest touch after the last one for turns, traps and soft touches, seconds.</summary>
    public float TurnMinInterval { get; set; } = 0.10f;
    /// <summary>A turn sharper than this at speed is a cut: the ball leaves at the player's post-turn speed, no overshoot.</summary>
    public float CutDeg { get; set; } = 60f;
    /// <summary>Player speed above which a sharp turn is a cut, m/s.</summary>
    public float CutMinSpeed { get; set; } = 2.0f;

    /// <summary>A trap is made when the ball would come to rest more than this from where the player stops, metres.</summary>
    public float TrapTolerance { get; set; } = 0.25f;
    /// <summary>Where a trap aims the ball to come to rest: this far ahead of where the player stops, metres.</summary>
    public float TrapRestOffset { get; set; } = 0.35f;

    /// <summary>First contact with a loose ball: direction error at relative speed equal to the reference (first_touch).</summary>
    public RatingRange FirstTouchDirectionErrorDeg { get; set; } = new(25f, 3f);
    /// <summary>First contact: fraction of the relative ball speed kept as unwanted extra velocity (first_touch).</summary>
    public RatingRange FirstTouchSpeedKept { get; set; } = new(0.45f, 0.05f);
    /// <summary>Relative speed at which the first-touch direction error reaches its full value, m/s.</summary>
    public float FirstTouchReferenceSpeed { get; set; } = 15f;
    /// <summary>With the stick released, a loose ball is only stopped if it moves at least this fast relative to the player, m/s.</summary>
    public float FirstTouchMinRelativeSpeed { get; set; } = 0.5f;
}

/// <summary>Possession: who controls the ball, when control is lost, the possession assist and the next-touch prediction.</summary>
public sealed class PossessionTuning
{
    /// <summary>Possession is lost when the ball stays further than the lose distance for this long, seconds.</summary>
    public float LoseSeconds { get; set; } = 0.15f;
    /// <summary>Lose distance at a standstill or walking pace, metres.</summary>
    public float LoseDistanceSlow { get; set; } = 2.0f;
    /// <summary>Lose distance at top speed, metres (linear in speed fraction between).</summary>
    public float LoseDistanceFast { get; set; } = 3.5f;
    /// <summary>Multiplies the sideways auto-steer toward the ball's line (0 turns it off).</summary>
    public float AssistStrength { get; set; } = 1.0f;
    /// <summary>Sideways correction speed per metre of offset, 1/s.</summary>
    public float AssistGain { get; set; } = 4f;
    /// <summary>Sideways correction speed cap, m/s.</summary>
    public float AssistMaxSpeed { get; set; } = 1.5f;
    /// <summary>With the ball ahead and out of reach, the speed cap rises to this fraction of off-ball top speed.</summary>
    public float ChaseSpeedFraction { get; set; } = 0.97f;
    /// <summary>
    /// A released stick or a new direction first carries the player on to the ball when it is ahead and at most this far
    /// beyond foot reach, metres; a ball knocked further than that is left to roll.
    /// </summary>
    public float ReachBallMargin { get; set; } = 0.5f;
    /// <summary>Off possession the turn rate is multiplied by (1 + this).</summary>
    public float OffBallTurnBonus { get; set; } = 0.15f;
    /// <summary>How far ahead the next-touch prediction looks, seconds.</summary>
    public float PredictionHorizon { get; set; } = 1.5f;
}

/// <summary>Shot model constants.</summary>
public sealed class ShotTuning
{
    public float BaseSigmaDeg { get; set; } = 7.0f;
    public float SigmaSkillSlope { get; set; } = 6.2f;
    public float VerticalRatio { get; set; } = 0.7f;
    public float SpeedNoise { get; set; } = 0.04f;
    /// <summary>Inside this distance the finishing rating applies.</summary>
    public float FinishingRange { get; set; } = 18f;
    /// <summary>Beyond this distance the long_shots rating applies.</summary>
    public float LongShotRange { get; set; } = 25f;
    public RatingRange Power { get; set; } = new(22f, 36f);
    public float ChargeBaseSpeed { get; set; } = 0.45f;
    public float ChargeSpeedSlope { get; set; } = 0.55f;
    public float ChargeSeconds { get; set; } = 0.9f;

    public float SpeedMultiplierSlope { get; set; } = 0.5f;
    public float AngleFreeDeg { get; set; } = 30f;
    public float AngleSpanDeg { get; set; } = 60f;
    public float AngleMultiplierSlope { get; set; } = 0.6f;
    public float WeakFootStep { get; set; } = 0.18f;
    public float PressureSlope { get; set; } = 0.6f;
    public float PressureFarMetres { get; set; } = 4f;
    public float PressureNearMetres { get; set; } = 1f;
    public float PowerThreshold { get; set; } = 0.8f;
    public float PowerMaxMultiplier { get; set; } = 1.5f;
    public float AirHeight { get; set; } = 0.15f;
    public float AirMultiplier { get; set; } = 1.3f;
    public float NormalTypeMultiplier { get; set; } = 1.0f;
    public float FinesseTypeMultiplier { get; set; } = 0.8f;
    public float ChipTypeMultiplier { get; set; } = 1.1f;

    public float NormalElevationBaseDeg { get; set; } = 4f;
    public float NormalElevationChargeDeg { get; set; } = 14f;
    public float NormalSpinMaxRev { get; set; } = 2f;
    public float FinesseSpeedFraction { get; set; } = 0.75f;
    public float FinesseElevationBaseDeg { get; set; } = 6f;
    public float FinesseElevationChargeDeg { get; set; } = 10f;
    /// <summary>Finesse sidespin, revolutions per second (curve rating).</summary>
    public RatingRange FinesseSpinRev { get; set; } = new(4f, 10f);
    public float ChipSpeedFraction { get; set; } = 0.55f;
    public float ChipElevationBaseDeg { get; set; } = 28f;
    public float ChipElevationChargeDeg { get; set; } = 12f;
    public float ChipBackspinMinRev { get; set; } = 4f;
    public float ChipBackspinMaxRev { get; set; } = 6f;

    /// <summary>The aimed point on the goal line is clamped to this far either side of centre.</summary>
    public float MaxTargetZ { get; set; } = 4.5f;
    /// <summary>Aim assist applies when the raw target is outside the frame by less than this.</summary>
    public float AssistRange { get; set; } = 1.5f;
    /// <summary>The assist pulls toward a point this far inside the nearer post.</summary>
    public float AssistInset { get; set; } = 0.6f;
    /// <summary>A strike needs the ball within foot reach plus this.</summary>
    public float StrikeReachMargin { get; set; } = 0.2f;
    public float StrikeMaxHeight { get; set; } = 0.6f;
    /// <summary>The ball must be this far to the other side of the facing line to switch foot.</summary>
    public float WrongFootOffset { get; set; } = 0.25f;
}

/// <summary>Keeper model constants.</summary>
public sealed class KeeperTuning
{
    public RatingRange Reaction { get; set; } = new(0.34f, 0.16f);
    public float ReactionNoise { get; set; } = 0.02f;
    /// <summary>Reaction noise is clamped to this many standard deviations, so the reaction time has hard bounds.</summary>
    public float ReactionNoiseClampSigma { get; set; } = 3f;
    public float MinReaction { get; set; } = 0.05f;
    public RatingRange DiveSpeed { get; set; } = new(3.0f, 6.0f);
    public float PushOff { get; set; } = 0.12f;
    public float ArmSweep { get; set; } = 0.9f;
    public float ArmSweepTime { get; set; } = 0.15f;
    public RatingRange Stretch { get; set; } = new(2.2f, 2.9f);
    public float StretchHeightReference { get; set; } = 1.90f;
    public float PredictionNoise { get; set; } = 0.1f;
    public RatingRange CatchSpeed { get; set; } = new(14f, 26f);
    public float CatchZoneFraction { get; set; } = 0.7f;
    public float ParrySpeedMin { get; set; } = 0.4f;
    public float ParrySpeedMax { get; set; } = 0.6f;
    /// <summary>Sideways steering of a parry relative to its backward travel, at handling 99. Zero at handling 1.</summary>
    public float ParryWideMax { get; set; } = 1.5f;
    public float ParryLift { get; set; } = 0.15f;
    public float ParryYawNoiseDeg { get; set; } = 8f;
    /// <summary>Standing distance in front of the goal line along the ball-to-goal line, metres (basic keeper).</summary>
    public float SetDistance { get; set; } = 1.5f;
    public float SetMoveSpeed { get; set; } = 4.5f;
    /// <summary>Height of the keeper's reach centre as a fraction of his height.</summary>
    public float HomeHeightFraction { get; set; } = 0.55f;
    public float HandRadius { get; set; } = 0.20f;
    /// <summary>Seconds after a parry before the keeper is set again.</summary>
    public float RecoverSeconds { get; set; } = 1.0f;
    /// <summary>Fraction of the keeper's body that follows the hands sideways in a dive (view only).</summary>
    public float BodyFollow { get; set; } = 0.8f;
    // Parsed and held for later slices, not used yet.
    public RatingRange PositionError { get; set; } = new(0.60f, 0.05f);
    public float StillMovingReactionPenalty { get; set; } = 0.05f;
    public RatingRange RushSpeed { get; set; } = new(4.5f, 7.0f);
    public RatingRange RushMisjudge { get; set; } = new(0.40f, 0.05f);
    public float FingertipZoneFraction { get; set; } = 0.10f;
    public float FingertipSpeedKept { get; set; } = 0.70f;
    public float FingertipMaxBendDeg { get; set; } = 25f;
}

/// <summary>Round state machine timings.</summary>
public sealed class RoundTuning
{
    public float ShootBuffer { get; set; } = 0.25f;
    public float StrikeDelay { get; set; } = 0.10f;
    public float OutcomeSeconds { get; set; } = 1.5f;
    /// <summary>Safety cap on a round's running time when the scenario has no clock, seconds.</summary>
    public float MaxSeconds { get; set; } = 120f;
}

/// <summary>
/// Every tunable number. Built-in defaults equal data/tuning/default.json; a file only needs the values it changes.
/// </summary>
public sealed class Tuning
{
    /// <summary>Ticks per second. The world always steps 1 / TickRate seconds.</summary>
    public int TickRate { get; set; } = 120;
    /// <summary>Multiplies every shot spread.</summary>
    public float ErrorScale { get; set; } = 1.0f;
    public float AimAssist { get; set; } = 0.3f;
    public BallTuning Ball { get; set; } = new();
    public FrameTuning Frame { get; set; } = new();
    public MovementTuning Movement { get; set; } = new();
    public TouchTuning Touch { get; set; } = new();
    public PossessionTuning Possession { get; set; } = new();
    public ShotTuning Shot { get; set; } = new();
    public KeeperTuning Keeper { get; set; } = new();
    public RoundTuning Round { get; set; } = new();

    /// <summary>Seconds per tick.</summary>
    public float Dt => 1f / TickRate;
}
