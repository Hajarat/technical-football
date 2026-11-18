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

/// <summary>The walled arena's shape and its boards. Length, width and goal size are in the scenario's pitch.</summary>
public sealed class ArenaTuning
{
    /// <summary>Radius of the two rounded corners at the attacker's end, metres.</summary>
    public float CornerRadius { get; set; } = 6f;
    /// <summary>The goal-end corners are cut by a straight board starting this far along each wall from the corner, metres.</summary>
    public float GoalCornerChamfer { get; set; } = 4f;
    /// <summary>Height of the drawn boards, metres. The ball meets an invisible wall above them, up to the ceiling.</summary>
    public float BoardHeight { get; set; } = 1.0f;
    /// <summary>Height of the ceiling, metres.</summary>
    public float Ceiling { get; set; } = 8.0f;
    /// <summary>Fraction of the speed into a board that the ball keeps coming off it.</summary>
    public float BoardRestitution { get; set; } = 0.70f;
    /// <summary>Coulomb friction coefficient between the ball and a board (the same bounce-friction model as the ground).</summary>
    public float BoardFriction { get; set; } = 0.30f;
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
    public float BaseSigmaDeg { get; set; } = 4.9f;
    public float SigmaSkillSlope { get; set; } = 4.34f;
    public float VerticalRatio { get; set; } = 0.7f;
    public float SpeedNoise { get; set; } = 0.04f;
    /// <summary>Inside this distance the finishing rating applies.</summary>
    public float FinishingRange { get; set; } = 18f;
    /// <summary>Beyond this distance the long_shots rating applies.</summary>
    public float LongShotRange { get; set; } = 25f;
    public RatingRange Power { get; set; } = new(26f, 43f);
    public float ChargeBaseSpeed { get; set; } = 0.3f;
    public float ChargeSpeedSlope { get; set; } = 0.7f;
    /// <summary>Seconds of holding shoot that fill the charge from 0 to 1.</summary>
    public float ChargeSeconds { get; set; } = 0.3f;
    /// <summary>The strike's contact comes no sooner than this after the press, seconds (the wind-up).</summary>
    public float WindupSeconds { get; set; } = 0.45f;
    /// <summary>... and no sooner than this after the release, seconds.</summary>
    public float ReleaseLeadSeconds { get; set; } = 0.12f;
    /// <summary>A stick at rest at the release aims along its last pushed direction if that push was this recent, seconds.</summary>
    public float AimLockSeconds { get; set; } = 0.5f;
    /// <summary>The shot action's follow-through after contact, seconds.</summary>
    public float RecoverySeconds { get; set; } = 0.25f;
    /// <summary>
    /// The charge a perfectly judged shot uses, by distance from the ball to the goal centre (metres). Missing it moves the
    /// shot vertically (or, for a chip, in depth).
    /// </summary>
    public Curve IdealCharge { get; set; } = new((6f, 0.30f), (11f, 0.45f), (16f, 0.62f), (22f, 0.76f), (30f, 0.90f));
    /// <summary>Mean vertical error per unit of charge above the ideal, degrees: over-charging rises, under-charging drives low.</summary>
    public float ChargeMissPitchDeg { get; set; } = 14f;
    /// <summary>A chip's launch speed is multiplied by 1 + this times (charge - ideal): over-charged chips go deeper.</summary>
    public float ChipChargeMissSpeed { get; set; } = 0.2f;
    /// <summary>After a strike the shooter's speed cap is <see cref="StunSpeedFraction"/> of top speed for this long ...</summary>
    public float StunBaseSeconds { get; set; } = 0.15f;
    /// <summary>... plus this times the charge, seconds; no touches meanwhile.</summary>
    public float StunChargeSeconds { get; set; } = 0.35f;
    public float StunSpeedFraction { get; set; } = 0.4f;

    /// <summary>
    /// Spread multiplier at full running speed is 1 + this (linear in speed / top speed). Calibrated in Phase D so a finishing-78
    /// striker at full sprint puts 10-15 m shots on target about 60-75% of the time.
    /// </summary>
    public float SpeedMultiplierSlope { get; set; } = 3.0f;
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
    public float PowerTypeMultiplier { get; set; } = 1.0f;
    public float FinesseTypeMultiplier { get; set; } = 0.8f;
    public float ChipTypeMultiplier { get; set; } = 1.1f;
    public float DrivenTypeMultiplier { get; set; } = 1.1f;
    public float LowFinesseTypeMultiplier { get; set; } = 0.85f;

    /// <summary>Side spin grows with launch speed: the rated spin applies at this speed, m/s, in proportion below and above it.</summary>
    public float SpinReferenceSpeed { get; set; } = 25f;
    /// <summary>
    /// Power shot loft before the charge miss, degrees: base plus this times the charge. Harder shots fly flatter, so the
    /// charge slope is slightly negative; over-charging still lifts the shot through the charge miss.
    /// </summary>
    public float PowerElevationBaseDeg { get; set; } = 10f;
    public float PowerElevationChargeDeg { get; set; } = -3f;
    /// <summary>Power shot side spin (a light curl to a random side), revolutions per second at the reference speed (curve rating).</summary>
    public RatingRange PowerSpinRev { get; set; } = new(0.5f, 2f);
    public float DrivenSpeedFactor { get; set; } = 1.05f;
    public float DrivenElevationBaseDeg { get; set; } = 3.5f;
    public float DrivenElevationChargeDeg { get; set; } = 1.5f;
    /// <summary>Driven shot side spin (little, to a random side), revolutions per second at the reference speed (curve rating).</summary>
    public RatingRange DrivenSpinRev { get; set; } = new(0.2f, 0.6f);
    public float FinesseSpeedFraction { get; set; } = 0.8f;
    public float FinesseElevationBaseDeg { get; set; } = 15f;
    public float FinesseElevationChargeDeg { get; set; } = -3f;
    /// <summary>Finesse (and low finesse) side spin, revolutions per second at the reference speed (curve rating).</summary>
    public RatingRange FinesseSpinRev { get; set; } = new(4f, 10f);
    public float LowFinesseSpeedFraction { get; set; } = 0.8f;
    public float LowFinesseElevationBaseDeg { get; set; } = 1f;
    public float LowFinesseElevationChargeDeg { get; set; } = 3f;
    public float ChipSpeedFraction { get; set; } = 0.5f;
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
    public RatingRange Reaction { get; set; } = new(0.34f, 0.145f);
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
    /// <summary>Height of the keeper's reach centre as a fraction of his height.</summary>
    public float HomeHeightFraction { get; set; } = 0.55f;
    public float HandRadius { get; set; } = 0.20f;
    /// <summary>Set (still at the strike), the keeper holds his hands this far out to each side of his body's centre, metres...</summary>
    public float HandSpan { get; set; } = 0.5f;
    /// <summary>...at this fraction of his height (about the waist). His reach grows from the span, not from zero.</summary>
    public float HandSpanHeightFraction { get; set; } = 0.5f;
    /// <summary>Fraction of the keeper's body that follows the hands sideways in a dive.</summary>
    public float BodyFollow { get; set; } = 0.8f;
    /// <summary>The body the ball can hit while he plays a shot: a capsule of this radius, metres...</summary>
    public float BodyRadius { get; set; } = 0.22f;
    /// <summary>...upright from this height (shins)...</summary>
    public float BodyLow { get; set; } = 0.15f;
    /// <summary>...to this fraction of his height (shoulders); in a dive it runs from the hip height toward the hands.</summary>
    public float BodyTopFraction { get; set; } = 0.82f;
    public float HipHeight { get; set; } = 0.95f;
    /// <summary>Planted closer than this to the ball at the strike, the keeper is spread (makes himself big), metres...</summary>
    public float SpreadRange { get; set; } = 6f;
    /// <summary>...fully spread from this close, metres.</summary>
    public float SpreadFull { get; set; } = 3f;
    /// <summary>Spread, his body is a capsule across the shot this far either side of his centre, metres...</summary>
    public float SpreadHalfWidth { get; set; } = 0.45f;
    /// <summary>...at this height, metres...</summary>
    public float SpreadHeight { get; set; } = 0.6f;
    /// <summary>...with this radius, metres.</summary>
    public float SpreadRadius { get; set; } = 0.4f;
    /// <summary>A ball that hits the body slower than this is held; faster it is blocked, m/s.</summary>
    public float BodyHoldSpeed { get; set; } = 8f;

    // ---- hold arc ----
    /// <summary>The arc's base distance out from the goal mouth (the reference geometry), metres (GOALS 0.70).</summary>
    public float ArcRadius { get; set; } = 0.70f;
    /// <summary>The keeper never stands closer than this to his goal line on the arc, metres (GOALS 0.60).</summary>
    public float ArcMinFromLine { get; set; } = 0.60f;
    /// <summary>Extra distance out beyond <see cref="ArcRadius"/>, metres, by the ball's distance from the goal mouth (more as it comes closer).</summary>
    public Curve ArcDepth { get; set; } = new((6f, 0.9f), (12f, 0.8f), (20f, 0.6f), (30f, 0.4f));
    /// <summary>Further extra distance out, metres, by the ball's bearing from the goal's centre line in degrees (closing down a tight angle).</summary>
    public Curve ArcDepthBearing { get; set; } = new((40f, 0f), (55f, 0.9f), (70f, 2.1f), (85f, 2.7f));
    /// <summary>Multiplier on <see cref="ArcDepth"/> by positioning: a poorly positioned keeper stays deeper.</summary>
    public RatingRange ArcDepthPositioning { get; set; } = new(0.5f, 1.0f);
    /// <summary>The arc spot is worked out for the ball this far ahead along its ground velocity, seconds.</summary>
    public float ArcLookahead { get; set; } = 0.2f;
    /// <summary>Fraction of the half-angle between the posts the cover line turns toward the near post, by the ball's bearing in degrees.</summary>
    public Curve NearPostBias { get; set; } = new((0f, 0f), (85f, 0f));
    /// <summary>Holding his line, the keeper stays this far inside each post, metres.</summary>
    public float LineInset { get; set; } = 0.5f;
    /// <summary>A new arc spot is taken only when the ideal spot has moved this far from the last one, metres.</summary>
    public float RepositionThreshold { get; set; } = 0.25f;
    /// <summary>A reposition longer than this is jogged instead of side-stepped, metres.</summary>
    public float JogThreshold { get; set; } = 1.5f;
    /// <summary>Side-step speed by positioning, m/s.</summary>
    public RatingRange ShuffleSpeed { get; set; } = new(2.0f, 3.5f);
    /// <summary>Jogging speed by positioning, m/s.</summary>
    public RatingRange JogSpeed { get; set; } = new(3.5f, 5.5f);
    /// <summary>Acceleration on foot by rushing_out, m/s².</summary>
    public RatingRange MoveAccel { get; set; } = new(5.0f, 8.0f);
    /// <summary>How fast the keeper turns to face the ball, degrees per second.</summary>
    public float TurnRateDeg { get; set; } = 540f;
    /// <summary>Deceleration when the keeper plants for a shot (and while he reacts with momentum), m/s².</summary>
    public float PlantDecel { get; set; } = 12f;
    /// <summary>Below this speed the keeper counts as still (set), m/s.</summary>
    public float StillSpeed { get; set; } = 0.3f;
    /// <summary>The keeper plants when a shot winds up with the ball within this distance of the goal centre, metres...</summary>
    public float SetRange { get; set; } = 25f;
    /// <summary>...once its strike is at most this far off, seconds (the shooter's last stride).</summary>
    public float PlantLead { get; set; } = 0.30f;
    /// <summary>Standard deviation of the error in each new arc spot, metres, by positioning (drawn per reposition).</summary>
    public RatingRange PositionError { get; set; } = new(0.60f, 0.05f);
    /// <summary>Added to the reaction when the keeper is still moving at the strike, seconds.</summary>
    public float StillMovingReactionPenalty { get; set; } = 0.05f;

    // ---- rush ----
    /// <summary>Top speed running out, m/s, by rushing_out.</summary>
    public RatingRange RushSpeed { get; set; } = new(4.5f, 7.0f);
    /// <summary>Standard deviation of the keeper's error judging a rush race, seconds, by rushing_out (drawn per attacker touch).</summary>
    public RatingRange RushMisjudge { get; set; } = new(0.40f, 0.05f);
    /// <summary>Time margin wanted over the attacker's next touch before rushing, seconds, by distance to the ball in metres.</summary>
    public Curve RushMargin { get; set; } = new((2f, 0.05f), (6f, 0.15f), (12f, 0.30f));
    /// <summary>Multiplier on <see cref="RushMargin"/> by rushing_out.</summary>
    public RatingRange RushMarginRating { get; set; } = new(3.5f, 2.5f);
    /// <summary>The keeper rushes only for a ball he can take within this distance of the goal centre, metres (GOALS 15).</summary>
    public float RushMaxFromGoal { get; set; } = 15f;
    /// <summary>Each movement decision moves toward a point at most this far away, metres (GOALS 3.5).</summary>
    public float MoveTargetCap { get; set; } = 3.5f;
    /// <summary>One-on-one: once the attacker is on the ball within this shooting range of the goal mouth, metres...</summary>
    public float HoldGroundRange { get; set; } = 20f;
    /// <summary>...running at goal at least this fast, m/s, a keeper further out than his arc spot stops backpedalling...</summary>
    public float HoldGroundMinSpeed { get; set; } = 3f;
    /// <summary>...if he is no further than this from the goal mouth, metres (further out, he still recovers to his arc).</summary>
    public float HoldGroundMaxOut { get; set; } = 8.5f;
    /// <summary>Seconds to commit to a rush from standing (added to the race time when not already rushing).</summary>
    public float RushStartDelay { get; set; } = 0.15f;
    /// <summary>The keeper rushes or claims only a ball whose centre is below this height, metres.</summary>
    public float ClaimMaxHeight { get; set; } = 0.6f;
    /// <summary>With no touch planned, the keeper reckons the attacker plays the ball once within this distance of it, metres...</summary>
    public float AttackerReach { get; set; } = 0.5f;
    /// <summary>...after running to it at his top speed, plus this, seconds.</summary>
    public float AttackerReactTime { get; set; } = 0.1f;
    /// <summary>A ball predicted further ahead than this is not raced for, seconds.</summary>
    public float RushLookahead { get; set; } = 2.0f;

    // ---- smother and claim ----
    /// <summary>The keeper dives at a loose ball (smother) from this far away, metres.</summary>
    public float SmotherReach { get; set; } = 1.5f;
    /// <summary>Speed of the hands going to the ball in a smother, m/s.</summary>
    public float SmotherSpeed { get; set; } = 5.0f;
    /// <summary>Radius of the body spread on the ball in a smother (instead of the hand radius), metres.</summary>
    public float SmotherRadius { get; set; } = 0.40f;
    /// <summary>A ball met in a smother slower than this is held, m/s; faster it is blocked.</summary>
    public float SmotherHoldSpeed { get; set; } = 9f;
    /// <summary>A smother that has not met the ball after this long ends on the ground, seconds.</summary>
    public float SmotherMaxSeconds { get; set; } = 0.6f;

    // ---- saves ----
    /// <summary>The dive wedge: a target is at least this far from the feet, metres (GOALS 0.05).</summary>
    public float WedgeMinRadius { get; set; } = 0.05f;
    /// <summary>The dive wedge: a target is at most this far from the feet, metres (GOALS 3.5).</summary>
    public float WedgeMaxRadius { get; set; } = 3.5f;
    /// <summary>The dive wedge: a target is within this angle of the keeper's sideways axis, degrees (GOALS 75).</summary>
    public float WedgeHalfAngleDeg { get; set; } = 75f;
    /// <summary>The dive wedge: above this height a target must be in the keeper's save plane, metres (GOALS 0.8).</summary>
    public float WedgeLowCap { get; set; } = 0.8f;
    /// <summary>Above the low cap, how far in front of the keeper a target may be, metres.</summary>
    public float WedgeHighForward { get; set; } = 0.5f;
    /// <summary>Largest random delay to a dive's start for an easy save, seconds (GOALS 0.001).</summary>
    public float SaveTimeJitterEasy { get; set; } = 0.001f;
    /// <summary>Largest random delay to a dive's start for a save at full stretch, seconds (GOALS 0.025).</summary>
    public float SaveTimeJitterHard { get; set; } = 0.025f;
    /// <summary>Catch speed in a dive as a fraction of the standing catch speed.</summary>
    public float DivingCatchFactor { get; set; } = 0.9f;
    /// <summary>Catch speed for a ball blocked at the feet as a fraction of the standing catch speed.</summary>
    public float FeetCatchFactor { get; set; } = 0.5f;
    /// <summary>A target this close to the body (horizontally) is a standing save, metres.</summary>
    public float CatchBodyRadius { get; set; } = 0.45f;
    /// <summary>A target below <see cref="FeetBlockHeight"/> and this close is blocked at the feet, metres.</summary>
    public float FeetBlockRadius { get; set; } = 0.7f;
    public float FeetBlockHeight { get; set; } = 0.40f;
    /// <summary>Dive kind by target height: low below this, metres.</summary>
    public float LowDiveHeight { get; set; } = 0.6f;
    /// <summary>Dive kind by target height: high from this, metres.</summary>
    public float HighDiveHeight { get; set; } = 1.5f;
    /// <summary>A ball met above this fraction of the goal height...</summary>
    public float TipOverBarHeight { get; set; } = 0.7f;
    /// <summary>...with the keeper within this distance of his line is tipped over the bar, metres (GOALS 0.7 and 3 m).</summary>
    public float TipOverBarDistance { get; set; } = 3.0f;
    /// <summary>Slowest a parried ball leaves the keeper, m/s (GOALS 3.0).</summary>
    public float MinParrySpeed { get; set; } = 3.0f;
    /// <summary>Slowest a blocked ball leaves the keeper, m/s (GOALS 4.5).</summary>
    public float BlockSpeed { get; set; } = 4.5f;
    /// <summary>A blocked ball keeps between these fractions of its speed.</summary>
    public float BlockSpeedMin { get; set; } = 0.25f;
    public float BlockSpeedMax { get; set; } = 0.45f;
    /// <summary>Largest angle of a block off straight back out, degrees.</summary>
    public float BlockAngleDeg { get; set; } = 45f;
    /// <summary>Contact in the outer fraction of the keeper's stretch is a fingertip save.</summary>
    public float FingertipZoneFraction { get; set; } = 0.05f;
    /// <summary>A fingertip save keeps this fraction of the ball's speed...</summary>
    public float FingertipSpeedKept { get; set; } = 0.70f;
    /// <summary>...and bends its path away from the goal by up to this much, degrees.</summary>
    public float FingertipMaxBendDeg { get; set; } = 40f;
    /// <summary>A dive that has not met the ball ends (the keeper lands) this long after its reach stops growing, seconds...</summary>
    public float DiveLandSeconds { get; set; } = 0.15f;
    /// <summary>...and no later than this after the reaction, seconds. A standing save lasts at least <see cref="StandSaveSeconds"/>.</summary>
    public float DiveMaxSeconds { get; set; } = 0.8f;
    public float StandSaveSeconds { get; set; } = 0.35f;

    // ---- recovery ----
    /// <summary>Lying after a dive, smother or block before getting up, seconds.</summary>
    public float GroundedSeconds { get; set; } = 0.3f;
    /// <summary>Getting up takes this long, seconds (GOALS 1.0).</summary>
    public float GetUpSeconds { get; set; } = 1.0f;
    /// <summary>A new save can start this long into getting up, seconds (GOALS 0.2).</summary>
    public float GetUpSaveAfter { get; set; } = 0.2f;
}

/// <summary>
/// Skill moves: the right-stick gestures that ask for them, when a move can start, each move's timing and geometry, the chain
/// rule and the exit burst. Times are seconds (converted to ticks at the tick rate), distances metres, angles degrees.
/// </summary>
public sealed class SkillTuning
{
    // ---- gestures (GestureReader) ----
    /// <summary>The right stick counts as pushed from this deflection; below it, it is neutral.</summary>
    public float MinActuation { get; set; } = 0.5f;
    /// <summary>
    /// Default angle tolerance of a gesture direction around its forward, back or side direction. A pushed stick belongs to
    /// its nearest of the four directions when within this of it, so at 45 or more every pushed stick has a direction.
    /// </summary>
    public float AngleToleranceDeg { get; set; } = 60f;
    /// <summary>The step-over's side flick must be within this of the exact side.</summary>
    public float StepOverSideToleranceDeg { get; set; } = 30f;
    /// <summary>Two recognised gestures are at least this far apart; a gesture sooner than this is ignored.</summary>
    public float GestureGap { get; set; } = 0.08f;
    /// <summary>A step-over is forward then the side within this long.</summary>
    public float StepOverWindow { get; set; } = 0.25f;
    /// <summary>A body feint is a side flick from neutral that is back at neutral within this long.</summary>
    public float FeintMaxHold { get; set; } = 0.1f;
    /// <summary>A side held this long is a ball roll (shorter and released, a body feint).</summary>
    public float RollMinHold { get; set; } = 0.1f;
    /// <summary>A forward flick at least this far while sprinting asks for a knock-on.</summary>
    public float KnockOnActuation { get; set; } = 0.85f;
    /// <summary>The knock-on waits this long before it fires, so a step-over can start with the same forward flick.</summary>
    public float KnockOnWait { get; set; } = 0.15f;
    /// <summary>Cancel pressed within this long of the shot starting turns it into a fake shot.</summary>
    public float FakeShotWindow { get; set; } = 0.1f;
    /// <summary>
    /// Each step of a heel-to-heel or a spin (a flick held, the stick back at neutral, the next flick) comes within this long
    /// of the one before. A sprinting forward flick released to neutral holds its knock-on while the next step may still come.
    /// </summary>
    public float ComboStepWindow { get; set; } = 0.125f;
    /// <summary>Above this speed the gesture frame's forward is the running direction, below it the facing, m/s.</summary>
    public float FrameMinSpeed { get; set; } = 1.0f;
    /// <summary>A keyboard skill key takes its side from the left stick's sideways deflection beyond this (else right).</summary>
    public float KeySideThreshold { get; set; } = 0.2f;

    // ---- starting a move (SkillMoves) ----
    /// <summary>A gesture made with the ball out of reach and no meet planned waits this long for it, then is dropped.</summary>
    public float BufferSeconds { get; set; } = 0.3f;
    /// <summary>
    /// A gesture made with the ball out of reach while a push is in flight waits for the ball's planned meet with the foot,
    /// if that meet comes within this long; at the meet the move starts instead of the touch.
    /// </summary>
    public float MeetWaitSeconds { get; set; } = 1.5f;
    /// <summary>The step-over starts with the ball within this of the player.</summary>
    public float StepOverRadius { get; set; } = 1.0f;
    /// <summary>The other moves start with the ball within this of the player.</summary>
    public float ExecutionRadius { get; set; } = 0.75f;
    /// <summary>A move starts only with the ball centre lower than this.</summary>
    public float MaxBallHeight { get; set; } = 0.3f;
    /// <summary>
    /// A step-over or body feint waits (while a meet is planned) until the ball is no more than this far ahead of the player,
    /// so the leg can reach over it.
    /// </summary>
    public float StepOverMaxAhead { get; set; } = 0.5f;
    /// <summary>
    /// A sole move (ball roll, drag-back, fake stop) reaches for the ball for at most this long before the sole lands on it,
    /// seconds; the ball is free until then.
    /// </summary>
    public float SoleReachMax { get; set; } = 0.1f;
    /// <summary>The sole lands with the ball centre within this of the foot's contact point, metres.</summary>
    public float SoleReachTolerance { get; set; } = 0.3f;
    /// <summary>The largest steering velocity the reach may add to bring the foot to the ball, m/s.</summary>
    public float SoleReachSteer { get; set; } = 2.5f;

    // ---- the moves ----
    public float StepOverSeconds { get; set; } = 0.25f;
    /// <summary>The step-over over a still ball (slower than <see cref="StillBallSpeed"/>).</summary>
    public float StepOverStillSeconds { get; set; } = 0.15f;
    /// <summary>A ball slower than this counts as still, m/s.</summary>
    public float StillBallSpeed { get; set; } = 0.1f;
    /// <summary>
    /// Step-over and body feint: the player eases to the ball's pace so it ends this far ahead of his foot (the contact zone's
    /// lead ahead of him).
    /// </summary>
    public float PaceMatchAhead { get; set; } = 0.25f;
    public float BodyFeintSeconds { get; set; } = 0.25f;
    /// <summary>The ball roll when it is the first move of a chain.</summary>
    public float BallRollFirstSeconds { get; set; } = 0.3f;
    /// <summary>The ball roll later in a chain.</summary>
    public float BallRollChainSeconds { get; set; } = 0.2f;
    /// <summary>How far the ball is rolled sideways against the player's speed, m/s to metres.</summary>
    public Curve BallRollDistance { get; set; } = new((0f, 0.6f), (4f, 1.0f));
    /// <summary>The player moves sideways this fraction of the roll distance with the ball.</summary>
    public float BallRollFollow { get; set; } = 0.5f;
    public float DragBackSeconds { get; set; } = 0.25f;
    /// <summary>No other move starts until this long after a drag-back.</summary>
    public float DragBackCooldown { get; set; } = 0.2f;
    /// <summary>How far the sole pulls the ball back.</summary>
    public float DragBackDistance { get; set; } = 1.0f;
    /// <summary>How far the player steps back (he ends standing).</summary>
    public float DragBackStepBack { get; set; } = 0.3f;
    /// <summary>Fake stop and fake cut.</summary>
    public float FakeSeconds { get; set; } = 0.1f;
    /// <summary>The fake stop leaves the ball at rest this far ahead of where the player stops.</summary>
    public float FakeStopAhead { get; set; } = 0.25f;
    /// <summary>The fake cut pushes the ball this far along the left stick ...</summary>
    public float FakeCutDistance { get; set; } = 1.2f;
    /// <summary>... in this long.</summary>
    public float FakeCutTime { get; set; } = 0.5f;

    // ---- chain and exit burst ----
    /// <summary>A move starting within this long of the previous one's end is chained.</summary>
    public float ChainWindow { get; set; } = 2.0f;
    /// <summary>Each chained move uses this times the previous move's effective skill.</summary>
    public float ChainMultiplier { get; set; } = 0.8f;
    /// <summary>
    /// After a move the speed time constant is divided by (1 + this * the exit-angle scale) for the burst duration (effective
    /// skill_moves).
    /// </summary>
    public RatingRange Burst { get; set; } = new(0f, 0.7f);
    /// <summary>How long the exit burst lasts from the exit touch, seconds (effective skill_moves).</summary>
    public RatingRange BurstDuration { get; set; } = new(0.2f, 0.9f);
    /// <summary>
    /// While the exit burst lasts the with-ball speed cap rises by this fraction of top speed times the exit-angle scale
    /// (effective skill_moves).
    /// </summary>
    public RatingRange BurstSpeedCap { get; set; } = new(0f, 0.03f);
    /// <summary>
    /// The burst scale of a move that sells a side (step-over, body feint) against the exit angle away from that side, degrees:
    /// largest going away from it, none going toward it.
    /// </summary>
    public Curve SidedExitBurst { get; set; } = new((-90f, 0f), (0f, 0.7f), (90f, 1f));
    /// <summary>The burst scale of a move that sells straight on (ball roll, drag-back, fakes) against the size of the exit angle.</summary>
    public Curve StraightExitBurst { get; set; } = new((0f, 0.8f), (90f, 0.88f));
    /// <summary>A step-over gives no burst at all for an exit more than this toward its sold side.</summary>
    public float StepOverNoBurstTowardDeg { get; set; } = 45f;
}

/// <summary>Round state machine timings.</summary>
public sealed class RoundTuning
{
    /// <summary>A released shot whose contact tick finds the ball out of reach waits this long for it, then is dropped, seconds.</summary>
    public float ShootBuffer { get; set; } = 0.25f;
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
    public ArenaTuning Arena { get; set; } = new();
    public MovementTuning Movement { get; set; } = new();
    public TouchTuning Touch { get; set; } = new();
    public PossessionTuning Possession { get; set; } = new();
    public ShotTuning Shot { get; set; } = new();
    public KeeperTuning Keeper { get; set; } = new();
    public RoundTuning Round { get; set; } = new();
    public SkillTuning Skill { get; set; } = new();

    /// <summary>Seconds per tick.</summary>
    public float Dt => 1f / TickRate;
}
