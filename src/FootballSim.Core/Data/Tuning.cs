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

/// <summary>
/// A piecewise-linear function y(x) through points sorted by x, held flat beyond the first and last point.
/// In JSON it is an array of [x, y] pairs, e.g. [[0, 0.45], [9, 0.42]]. <see cref="Validate"/> checks a loaded curve.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(CurveJsonConverter))]
public sealed class Curve
{
    readonly float[] _x;
    readonly float[] _y;

    public Curve(params (float X, float Y)[] points)
    {
        _x = new float[points.Length];
        _y = new float[points.Length];
        for (int i = 0; i < points.Length; i++) { _x[i] = points[i].X; _y[i] = points[i].Y; }
    }

    /// <summary>A curve with the same value everywhere.</summary>
    public static Curve Constant(float y) => new((0f, y));

    public int Count => _x.Length;
    public (float X, float Y) this[int i] => (_x[i], _y[i]);

    /// <summary>The value at <paramref name="x"/>: linear between points, the end value outside them, 0 for an empty curve.</summary>
    public float Evaluate(float x)
    {
        int n = _x.Length;
        if (n == 0) return 0f;
        if (x <= _x[0]) return _y[0];
        for (int i = 1; i < n; i++)
        {
            if (x <= _x[i])
            {
                float f = (x - _x[i - 1]) / (_x[i] - _x[i - 1]);
                return _y[i - 1] + (_y[i] - _y[i - 1]) * f;
            }
        }
        return _y[n - 1];
    }

    /// <summary>Null when the curve is usable; otherwise what is wrong (no points, a non-finite value, x not strictly increasing).</summary>
    public string? Validate()
    {
        if (_x.Length == 0) return "needs at least one [x, y] point";
        for (int i = 0; i < _x.Length; i++)
        {
            if (!float.IsFinite(_x[i]) || !float.IsFinite(_y[i])) return $"point {i} is not a finite number";
            if (i > 0 && !(_x[i] > _x[i - 1])) return $"x must be strictly increasing (point {i}: {_x[i]} after {_x[i - 1]})";
        }
        return null;
    }
}

/// <summary>Ball flight and ground constants.</summary>
public sealed class BallTuning
{
    public float Mass { get; set; } = 0.43f;
    public float Radius { get; set; } = 0.11f;
    public float CrossSection { get; set; } = 0.038f;
    public float AirDensity { get; set; } = 1.225f;
    /// <summary>
    /// Drag coefficient against airspeed, m/s (drag force is 0.5 * rho * Cd * A * v^2). The drop between about 9 and 14 m/s is the
    /// drag crisis: a slow ball is subcritical (about 0.45), a fast one supercritical (about 0.2 to 0.25).
    /// </summary>
    public Curve DragCurve { get; set; } = new((0f, 0.45f), (9f, 0.42f), (14f, 0.22f), (40f, 0.24f));
    /// <summary>
    /// Magnus lift coefficient against the spin parameter S = radius * (spin across the velocity, rad/s) / speed (dimensionless).
    /// The lift acts along spin x velocity.
    /// </summary>
    public Curve LiftCurve { get; set; } = new((0f, 0f), (0.1f, 0.15f), (0.3f, 0.28f), (0.6f, 0.35f));
    /// <summary>Spin decay rate in the air, per second: spin is multiplied by exp(-this * t).</summary>
    public float SpinDecayPerSecond { get; set; } = 0.02f;
    /// <summary>Largest spin the ball can hold, rad/s (95 is about 15 revolutions per second).</summary>
    public float MaxSpin { get; set; } = 95f;
    /// <summary>Ground bounce restitution (outgoing over incoming vertical speed) against the incoming vertical speed, m/s.</summary>
    public Curve RestitutionCurve { get; set; } = new((0f, 0.70f), (10f, 0.60f), (25f, 0.50f));
    /// <summary>Ball-ground friction coefficient at a bounce (Coulomb, dimensionless).</summary>
    public float BounceFriction { get; set; } = 0.55f;
    /// <summary>Moment of inertia as a fraction of mass * radius^2 (a thin-shelled ball is about 0.66; a hollow shell is 2/3).</summary>
    public float InertiaFactor { get; set; } = 0.66f;
    /// <summary>Rolling deceleration, m/s squared.</summary>
    public float RollDeceleration { get; set; } = 1.2f;
    /// <summary>
    /// Sliding friction coefficient on grass while the ball skids on the ground (its contact point slips): the ball slows at
    /// this times gravity (0.40 is about 3.9 m/s squared) and its spin changes to match, until it rolls.
    /// </summary>
    public float SkidFriction { get; set; } = 0.40f;
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
    /// <summary>Stride clock: steps per second at a standstill pace (f(v) = base + per_speed * v).</summary>
    public float StepFrequencyBase { get; set; } = 2.4f;
    /// <summary>Stride clock: extra steps per second per m/s of running speed.</summary>
    public float StepFrequencyPerSpeed { get; set; } = 0.17f;
    /// <summary>Below this running speed the stride clock idles: the current step is finished, then no new step starts, m/s.</summary>
    public float StepIdleSpeed { get; set; } = 0.3f;
}

/// <summary>A length that runs linearly from <see cref="Rest"/> at a standstill to <see cref="Sprint"/> at top speed.</summary>
public sealed class SpeedRange
{
    public float Rest { get; set; }
    public float Sprint { get; set; }

    public SpeedRange() { }
    public SpeedRange(float rest, float sprint) { Rest = rest; Sprint = sprint; }

    /// <summary>Value at a 0..1 fraction of top speed.</summary>
    public float Lerp(float speedFraction) => Rest + (Sprint - Rest) * Math.Clamp(speedFraction, 0f, 1f);
}

/// <summary>How many steps of the stride clock pass between two dribble touches, per touch mode.</summary>
public sealed class StepsPerTouchTuning
{
    public float Close { get; set; } = 1f;
    public float Jog { get; set; } = 2f;
    /// <summary>At a sprint, by dribbling rating (rounded to whole steps).</summary>
    public RatingRange Sprint { get; set; } = new(6f, 4f);
}

/// <summary>The most steps to the next meet while turning (see <see cref="TouchTuning.TurningStickRateDeg"/>).</summary>
public sealed class TurningStepsTuning
{
    public int Jog { get; set; } = 2;
    public int Sprint { get; set; } = 3;
}

/// <summary>Launch angle above the ground of a touch, degrees.</summary>
public sealed class TouchLiftTuning
{
    /// <summary>Close control and jog touches (dribbling rating).</summary>
    public RatingRange Jog { get; set; } = new(8f, 3f);
    /// <summary>Sprint touches and knock-ons (dribbling rating).</summary>
    public RatingRange Sprint { get; set; } = new(8f, 3f);
    /// <summary>A heavy touch.</summary>
    public float Heavy { get; set; } = 12f;
}

/// <summary>
/// Touches. In possession every touch is a committed action planned on the stride: the planner picks the contact tick (the
/// middle of a swing of the stride clock), the foot in swing then, and the contact point ahead of that foot; the player is
/// steered so the ball is there; at contact the push speed is solved so the ball meets the foot again a few steps later.
/// </summary>
public sealed class TouchTuning
{
    public float ReachBase { get; set; } = 0.55f;
    public float ReachPerHeight { get; set; } = 0.30f;
    public float HeightMin { get; set; } = 1.60f;
    public float HeightSpan { get; set; } = 0.40f;
    /// <summary>A touch is only made with the ball centre at or below this height, metres.</summary>
    public float MaxBallHeight { get; set; } = 0.45f;
    /// <summary>Top speed with the ball as a fraction of top speed (dribbling rating).</summary>
    public RatingRange TopSpeedWithBall { get; set; } = new(0.80f, 0.96f);
    /// <summary>Sprint input at or above this counts as sprinting (touch mode).</summary>
    public float SprintThreshold { get; set; } = 0.5f;
    /// <summary>Stick deflection below this asks for no dribble touch; in possession it asks for a stop.</summary>
    public float StickThreshold { get; set; } = 0.1f;

    /// <summary>A touch is planned at least this long before its contact, seconds (the pre-windup).</summary>
    public float PreWindup { get; set; } = 0.30f;
    /// <summary>The windup (the foot drawn back) starts this long before contact, seconds.</summary>
    public float Windup { get; set; } = 0.12f;
    /// <summary>The recovery (follow-through) ends this long after contact, seconds.</summary>
    public float Recovery { get; set; } = 0.15f;
    /// <summary>The contact point is this far ahead of the swinging foot's hip, metres, from a standstill to top speed.</summary>
    public SpeedRange ContactZoneAhead { get; set; } = new(0.25f, 0.45f);
    /// <summary>The ball centre must be within this of the contact point at contact, metres (horizontal).</summary>
    public float ContactZoneRadius { get; set; } = 0.12f;
    /// <summary>The contact point lies this far to the swinging foot's side of the body's centreline, metres.</summary>
    public float LateralFootOffset { get; set; } = 0.09f;
    /// <summary>Above <see cref="ExecutionMinSpeed"/>, a touch sent more than this off the ball's travel is a turn, degrees.</summary>
    public float ExecutionAngle { get; set; } = 10f;
    /// <summary>Speed above which the execution angle applies, m/s.</summary>
    public float ExecutionMinSpeed { get; set; } = 1f;
    /// <summary>A stick change larger than this before contact drops the planned touch and plans again, degrees.</summary>
    public float ReplanAngle { get; set; } = 90f;
    /// <summary>
    /// Early execution by the angle between the stick and the ball's travel, degrees: the fraction of the normal cadence a turn
    /// may cut it to. A stick turn pulls the waiting touch forward to no sooner after the previous touch than this fraction of
    /// the time the touch had (never below <see cref="EarlyMinInterval"/>), and a turn or cut touch pushes the ball to a meet
    /// this fraction of the usual steps away (at least one). Multiplied by <see cref="EarlyExecutionDribbling"/>.
    /// </summary>
    public Curve EarlyExecution { get; set; } = new((10f, 1.0f), (30f, 0.6f), (60f, 0.35f), (90f, 0.25f));
    /// <summary>Multiplier on <see cref="EarlyExecution"/> by the dribbling rating.</summary>
    public RatingRange EarlyExecutionDribbling { get; set; } = new(1.15f, 0.85f);
    /// <summary>Early turn touches are at least this far apart (from the previous touch), seconds.</summary>
    public float EarlyMinInterval { get; set; } = 0.22f;
    /// <summary>
    /// The stick counts as moving (a turn is likely) when its smoothed turn rate is above this, degrees per second; then, as
    /// for a touch sent more than the execution angle off the ball's travel, the push goes to a meet at most
    /// <see cref="TurningMaxSteps"/> steps away.
    /// </summary>
    public float TurningStickRateDeg { get; set; } = 20f;
    /// <summary>The most steps to the next meet while turning, per mode.</summary>
    public TurningStepsTuning TurningMaxSteps { get; set; } = new();
    /// <summary>
    /// With the stick pushed and no touch possible on the stick's path, the legs may bend this far from the stick toward the
    /// ball, degrees. Only when no such bend reaches the ball do they run on to it or run to it directly (the last resort).
    /// </summary>
    public float ApproachMaxStickDeg { get; set; } = 45f;
    /// <summary>Largest correction added to the player's velocity while steering the ball into the contact zone, m/s.</summary>
    public float SteerMax { get; set; } = 0.6f;
    /// <summary>Steps of the stride clock between dribble touches, per mode.</summary>
    public StepsPerTouchTuning StepsPerTouch { get; set; } = new();
    /// <summary>Launch angle of touches, degrees.</summary>
    public TouchLiftTuning LiftDeg { get; set; } = new();

    /// <summary>Ball speed after a planned touch never falls below this, m/s.</summary>
    public float MinPush { get; set; } = 2.0f;
    public float CloseControlMinPush { get; set; } = 1.2f;
    /// <summary>A planned touch never sends the ball faster than this, m/s; a touch that would need more is a heavy touch.</summary>
    public float MaxPush { get; set; } = 12f;
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

    /// <summary>Earliest first touch of a loose ball after the player's previous touch, seconds.</summary>
    public float TurnMinInterval { get; set; } = 0.10f;
    /// <summary>A touch turning the ball more than this at speed is a cut: the player loses the balance percentage of speed.</summary>
    public float CutDeg { get; set; } = 60f;
    /// <summary>Player speed above which a sharp turn is a cut, m/s.</summary>
    public float CutMinSpeed { get; set; } = 2.0f;

    /// <summary>
    /// After a dribble, turn or cut touch the speed time constant is divided by (1 + this + the turn extra) for the boost
    /// duration, so the player accelerates harder out of the touch (dribbling rating).
    /// </summary>
    public RatingRange TouchBoost { get; set; } = new(0.15f, 0.35f);
    /// <summary>How long the touch boost lasts after the touch, seconds.</summary>
    public float TouchBoostDuration { get; set; } = 0.25f;
    /// <summary>Extra boost for a touch that changes direction, reached at the full turn angle (linear from 0 degrees).</summary>
    public float TouchBoostTurnExtra { get; set; } = 0.3f;
    /// <summary>Turn angle at which the touch boost gets all of its turn extra, degrees.</summary>
    public float TouchBoostTurnFullDeg { get; set; } = 90f;

    /// <summary>
    /// A knock-on is planned so the player, chasing at full top speed, meets the ball this long after the touch, seconds
    /// (dribbling rating).
    /// </summary>
    public RatingRange KnockOnCatchTime { get; set; } = new(1.8f, 1.3f);
    /// <summary>A knock-on is never pushed faster than this, m/s (it has to outrun the skid to reach its catch point).</summary>
    public float KnockOnMaxPush { get; set; } = 18f;
    /// <summary>A knock-on's speed and direction errors are the sprint ones (full speed fraction) times this.</summary>
    public float KnockOnErrorScale { get; set; } = 1.5f;
    /// <summary>
    /// After a knock-on the player chases it (no dribble speed cap, the ball is not lost by distance) while running along it at
    /// least this fast, m/s.
    /// </summary>
    public float KnockOnChaseMinSpeed { get; set; } = 1.0f;
    /// <summary>The chase ends this long after the planned catch time if the player has not touched the ball again, seconds.</summary>
    public float KnockOnChaseGrace { get; set; } = 0.5f;

    /// <summary>A stop touch is made when the ball would come to rest more than this from where the player stops, metres.</summary>
    public float TrapTolerance { get; set; } = 0.25f;
    /// <summary>Where a stop touch aims the ball to come to rest: this far ahead of where the player stops, metres.</summary>
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

/// <summary>Possession: who controls the ball, when control is lost, the recovery run and the planning horizon.</summary>
public sealed class PossessionTuning
{
    /// <summary>Possession is lost when the ball stays further than the lose distance for this long, seconds.</summary>
    public float LoseSeconds { get; set; } = 0.15f;
    /// <summary>Lose distance at a standstill or walking pace, metres.</summary>
    public float LoseDistanceSlow { get; set; } = 2.0f;
    /// <summary>Lose distance at top speed, metres (linear in speed fraction between).</summary>
    public float LoseDistanceFast { get; set; } = 3.5f;
    /// <summary>While the player runs to recover a ball that no touch can be planned for, the speed cap is this fraction of top speed.</summary>
    public float ChaseSpeedFraction { get; set; } = 0.97f;
    /// <summary>Off possession the turn rate is multiplied by (1 + this).</summary>
    public float OffBallTurnBonus { get; set; } = 0.15f;
    /// <summary>How far ahead the touch planner looks for a swing that can play the ball, seconds.</summary>
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
