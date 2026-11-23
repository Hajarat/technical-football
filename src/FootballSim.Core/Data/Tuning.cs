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
    /// <summary>Kilograms.</summary>
    public float Mass { get; set; } = 0.45f;
    /// <summary>Metres.</summary>
    public float Radius { get; set; } = 0.115f;
    /// <summary>The area drag and lift act on, square metres: the ball's disc, pi * radius^2 (it follows the radius, so it is not a tuning value).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public float CrossSection => MathF.PI * Radius * Radius;
    public float AirDensity { get; set; } = 1.225f;
    /// <summary>
    /// Drag coefficient against airspeed, m/s (drag force is 0.5 * rho * Cd * A * v^2). The drop between about 9 and 14 m/s is the
    /// drag crisis: a slow ball is subcritical (about 0.45), a fast one supercritical (about 0.2 to 0.25).
    /// </summary>
    public Curve DragCurve { get; set; } = new((0f, 0.45f), (9f, 0.42f), (14f, 0.22f), (40f, 0.24f));
    /// <summary>The drag force is multiplied by this: one knob for how far and how flat the ball flies (1 leaves the curve as it is).</summary>
    public float DragForceMultiplier { get; set; } = 1f;
    /// <summary>
    /// Magnus lift coefficient against the spin parameter S = radius * (spin across the velocity, rad/s) / speed (dimensionless).
    /// The lift acts along spin x velocity.
    /// </summary>
    public Curve LiftCurve { get; set; } = new((0f, 0f), (0.1f, 0.15f), (0.3f, 0.28f), (0.6f, 0.35f));
    /// <summary>The spin (Magnus) force is multiplied by this: one knob for how much the ball curls, dips and lifts (1 leaves the curve as it is).</summary>
    public float SpinForceMultiplier { get; set; } = 1f;
    /// <summary>Spin decay rate in the air, per second: spin is multiplied by exp(-this * t).</summary>
    public float SpinDecayPerSecond { get; set; } = 0.02f;
    /// <summary>Largest spin the ball can hold, rad/s (95 is about 15 revolutions per second).</summary>
    public float MaxSpin { get; set; } = 95f;
    /// <summary>Ground bounce restitution (outgoing over incoming vertical speed) against the incoming vertical speed, m/s.</summary>
    public Curve RestitutionCurve { get; set; } = new((0f, 0.70f), (10f, 0.60f), (25f, 0.50f));
    /// <summary>
    /// Ball-ground friction coefficient at a bounce (Coulomb, dimensionless) against the incidence angle, degrees: the angle of
    /// the incoming velocity above the ground (near 0 a grazing skip, 90 a ball dropping straight down). Flat by default; a
    /// curve rising with the angle gives more grip to a steep landing and more skid to a grazing one.
    /// </summary>
    public Curve BounceFrictionCurve { get; set; } = Curve.Constant(0.55f);
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
    /// <summary>Top speed, m/s (sprint_speed rating): 6.8-8.5 m/s, from reference data.</summary>
    public RatingRange SprintSpeed { get; set; } = new(6.8f, 8.5f);
    /// <summary>
    /// Speed time constant, seconds (acceleration rating): the speed law's soft landing, the last of a change in speed eased in
    /// exponentially; the acceleration curves cap it.
    /// </summary>
    public RatingRange TimeConstant { get; set; } = new(1.10f, 0.45f);
    /// <summary>
    /// The most a jogging player speeds up, m/s^2, by his running speed as a fraction of his top speed (0..1), before
    /// <see cref="AccelRatingScale"/>.
    /// </summary>
    public Curve AccelBySpeedJog { get; set; } = new((0f, 7.5f), (1f, 7.5f));
    /// <summary>The same at a full sprint; the sprint input (0..1) blends the jog curve into this one.</summary>
    public Curve AccelBySpeedSprint { get; set; } = new((0f, 10f), (0.3f, 12.5f), (1f, 12.5f));
    /// <summary>Scale on the acceleration curves (acceleration rating).</summary>
    public RatingRange AccelRatingScale { get; set; } = new(0.6f, 1.15f);
    /// <summary>The most a player slows down by easing off the stick, m/s^2, by his running speed as a fraction of his top speed.</summary>
    public Curve DecelBySpeed { get; set; } = new((0f, 5f), (0.5f, 8f), (1f, 11f));
    /// <summary>Scale on the turn rate by the heading change still to make, degrees (0..180).</summary>
    public Curve TurnAngleMult { get; set; } = new((0f, 1f), (45f, 1.06f), (90f, 1.05f), (180f, 1f));
    /// <summary>Jogging speed without the ball, the same for every player (5.3 m/s, from reference data), m/s.</summary>
    public float JogSpeed { get; set; } = 5.3f;
    public float SprintFraction { get; set; } = 1.0f;
    public float CloseControlFraction { get; set; } = 0.35f;
    /// <summary>Turn rate with the ball, degrees per second (agility rating): 450 at the top, from reference data.</summary>
    public RatingRange TurnRateDeg { get; set; } = new(193f, 450f);
    /// <summary>Turn rate is scaled by (1 - this * speed fraction).</summary>
    public float TurnSpeedPenalty { get; set; } = 0.5f;
    /// <summary>
    /// Heading changes smaller than turn rate times this ease in exponentially with this time constant, seconds (0: every
    /// change at the full turn rate). A turn's own angle multiplier on its acceleration, as the reference data's turn acceleration has.
    /// </summary>
    public float TurnEase { get; set; } = 0.05f;
    /// <summary>
    /// For post_turn_seconds after a turn or cut touch the turn rate is at least this, degrees per second (the reference data turns at least
    /// 1080 deg/s after a trap): the body comes round onto the ball's new line in one step.
    /// </summary>
    public float PostTurnRotationRate { get; set; } = 540f;
    public float PostTurnSeconds { get; set; } = 0.3f;
    /// <summary>
    /// The share (0..1) of the balance loss a turn costs by how far the facing has come round inside sharp_turn_window,
    /// degrees: none for a gentle turn, rising to the full loss for a sharp one (a full share starts a new window).
    /// </summary>
    public Curve SharpTurnCost { get; set; } = new((55f, 0f), (75f, 1f));
    public float SharpTurnWindow { get; set; } = 0.2f;
    /// <summary>Fraction of speed lost on a sharp turn (balance rating).</summary>
    public RatingRange BalanceLoss { get; set; } = new(0.50f, 0.15f);
    /// <summary>
    /// The rate a sharp turn's or a cut's speed loss is shed at, m/s^2: a planted cut, harder than the reference data's passive deceleration
    /// (5 m/s^2, no input); its active deceleration curves are not known.
    /// </summary>
    public float BalanceDeceleration { get; set; } = 12f;
    /// <summary>
    /// Running at the boards a player brakes by himself, at balance_deceleration, so that he comes to them no faster than this
    /// (his velocity into them, m/s): the boards never stop him from a run in one tick.
    /// </summary>
    public float WallArriveSpeed { get; set; } = 1.2f;
    /// <summary>
    /// Without the ball, the stick more than this from the facing while running is a turn about (135 degrees, from
    /// reference data): the player plants and brakes, losing <see cref="ReverseSpeedLoss"/> of his speed at
    /// balance_deceleration while he turns (the reference data plays a speed-dependent 180 turn instead of turning on the spot).
    /// </summary>
    public float ReverseTurnDeg { get; set; } = 135f;
    /// <summary>Share of the running speed a turn about without the ball costs (see <see cref="ReverseTurnDeg"/>).</summary>
    public float ReverseSpeedLoss { get; set; } = 0.65f;
    /// <summary>A turn about only costs speed above this running speed, m/s.</summary>
    public float ReverseMinSpeed { get; set; } = 2f;
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

/// <summary>
/// Top speed with the ball as a fraction of top speed, per dribbling style (the close style by close dribbling, the speed style by sprint dribbling). The sprint input blends the
/// close style (no sprint: close control, walking touches, the jog) into the speed style (a full sprint).
/// </summary>
public sealed class TopSpeedWithBallTuning
{
    /// <summary>The close style: the cap on a dribble with no sprint input (close control, a walk, a jog).</summary>
    public RatingRange Close { get; set; } = new(0.80f, 0.96f);
    /// <summary>The speed style: the cap on a dribble at a full sprint.</summary>
    public RatingRange Speed { get; set; } = new(0.80f, 0.97f);
}

/// <summary>How many steps of the stride clock pass between two dribble touches, per touch mode.</summary>
public sealed class StepsPerTouchTuning
{
    /// <summary>
    /// The close style (close control, walking touches), by close dribbling rating (rounded to whole steps: 2 up to about 25, a
    /// touch on every step from about 26).
    /// </summary>
    public RatingRange Close { get; set; } = new(1.7f, 0.9f);
    public float Jog { get; set; } = 2f;
    /// <summary>
    /// At a sprint, by sprint dribbling rating (rounded to whole steps: 4 at 1, 3 from about 27 to 70, 2 from about 71; the 1.8 at 99
    /// moves the two-step band down so a sprint-dribbling-72 sprinter's turns meet the latency targets).
    /// </summary>
    public RatingRange Sprint { get; set; } = new(4f, 1.8f);
}

/// <summary>The most steps to the next meet while turning (see <see cref="TouchTuning.TurningStickRateDeg"/>).</summary>
public sealed class TurningStepsTuning
{
    public int Jog { get; set; } = 2;
    public int Sprint { get; set; } = 3;
}

/// <summary>
/// One touch kind's trajectory and contact limits: the solved push is launched at <see cref="LaunchAngle"/> (falling back to a
/// flatter angle, then a ground pass, when that cannot land on the meet), its arc kept below <see cref="PeakHeightCap"/>, its
/// speed within [<see cref="MinPush"/>, <see cref="MaxPush"/>]; the kind plays a ball no higher than <see cref="MaxBallHeight"/>
/// within <see cref="Reach"/> of the player's foot reach. A file that gives a profile replaces the whole profile (omitted fields
/// take the values below, not the kind's defaults).
/// </summary>
public class TouchProfile
{
    /// <summary>Launch angle above the ground, degrees, by the touch's dribbling rating (see TouchModel.DribblingFor).</summary>
    public RatingRange LaunchAngle { get; set; } = new(8f, 3f);
    /// <summary>Highest the ball centre may rise on the solved arc, metres (a steeper launch is flattened to keep under it).</summary>
    public float PeakHeightCap { get; set; } = 1f;
    /// <summary>Slowest push, m/s (a reversal's hook excepted).</summary>
    public float MinPush { get; set; } = 2f;
    /// <summary>Fastest push, m/s (nor faster than the player can catch within 1.5 s).</summary>
    public float MaxPush { get; set; } = 12f;
    /// <summary>The kind plays the ball only with its centre at or below this, metres.</summary>
    public float MaxBallHeight { get; set; } = 0.45f;
    /// <summary>The kind plays a ball within this share of the player's foot reach (see TouchModel.FootReach), horizontally.</summary>
    public float Reach { get; set; } = 1f;

    public TouchProfile() { }
    public TouchProfile(RatingRange launchAngle, float peakHeightCap, float minPush, float maxPush, float maxBallHeight, float reach)
    {
        LaunchAngle = launchAngle; PeakHeightCap = peakHeightCap; MinPush = minPush; MaxPush = maxPush; MaxBallHeight = maxBallHeight; Reach = reach;
    }

    /// <summary>Null when the profile is usable; otherwise what is wrong.</summary>
    public virtual string? Validate()
    {
        if (LaunchAngle == null) return "launch_angle: must be an object";
        foreach (var (name, v, lo, hi) in new[]
        {
            ("launch_angle.at1", LaunchAngle.At1, 0f, 45f), ("launch_angle.at99", LaunchAngle.At99, 0f, 45f),
            ("peak_height_cap", PeakHeightCap, 0f, 10f), ("min_push", MinPush, 0f, 40f), ("max_push", MaxPush, 0f, 40f),
            ("max_ball_height", MaxBallHeight, 0f, 3f), ("reach", Reach, 0f, 3f),
        })
            if (!float.IsFinite(v) || v < lo || v > hi) return $"{name}: {v} is outside {lo}..{hi}";
        if (MaxPush < MinPush) return $"max_push {MaxPush} is below min_push {MinPush}";
        if (MaxBallHeight <= 0f) return "max_ball_height must be positive";
        if (Reach <= 0f) return "reach must be positive";
        return null;
    }
}

/// <summary>
/// A trap's profile: the height band it takes (up to <see cref="TouchProfile.MaxBallHeight"/>, above the band below it) within its
/// share of foot reach, and how it settles the ball: it keeps <see cref="Kept"/> times the first_touch share of the ball's speed
/// relative to the player, and lets the ball drop <see cref="RestOffset"/> ahead of him (toward the stick, or his facing with the
/// stick released), launched at the profile's angle and pushed within [min_push, max_push] relative to him, its arc under the cap.
/// </summary>
public sealed class TrapProfile : TouchProfile
{
    /// <summary>Multiplies the first_touch share of the relative ball speed the trap leaves on the ball (lower: a deader trap).</summary>
    public float Kept { get; set; } = 1f;
    /// <summary>How far ahead of the player the trapped ball is let drop, metres (0: it is only killed).</summary>
    public float RestOffset { get; set; }

    public TrapProfile() { LaunchAngle = new(0f, 0f); MinPush = 0f; MaxPush = 3f; }
    public TrapProfile(float maxBallHeight, float reach, float kept, float restOffset, float peakHeightCap, float maxPush)
        : base(new(0f, 0f), peakHeightCap, 0f, maxPush, maxBallHeight, reach)
    {
        Kept = kept; RestOffset = restOffset;
    }

    public override string? Validate()
    {
        string? bad = base.Validate();
        if (bad != null) return bad;
        if (!float.IsFinite(Kept) || Kept < 0f || Kept > 3f) return $"kept: {Kept} is outside 0..3";
        if (!float.IsFinite(RestOffset) || RestOffset < 0f || RestOffset > 3f) return $"rest_offset: {RestOffset} is outside 0..3";
        return null;
    }
}

/// <summary>
/// The touch profiles by kind (see TouchModel.Profile): a turn, cut, stop, soft or first touch takes the profile of the touch mode
/// it is played in (close control, jog, sprint); a knock-on and a heavy touch their own.
/// </summary>
public sealed class TouchProfilesTuning
{
    /// <summary>Close control and walking touches: short, low and soft.</summary>
    public TouchProfile Close { get; set; } = new(new(8f, 3f), 0.2f, 1.2f, 12f, 0.45f, 1f);
    /// <summary>A jogging nudge: a little higher and shorter than a sprint push.</summary>
    public TouchProfile Jog { get; set; } = new(new(8f, 5f), 0.25f, 2f, 10f, 0.45f, 1f);
    /// <summary>A sprinting push: flatter and longer, skimming the ground.</summary>
    public TouchProfile Sprint { get; set; } = new(new(8f, 3f), 0.2f, 2f, 12f, 0.45f, 1f);
    /// <summary>A knock-on: driven far ahead to run onto (it has to outrun the skid to reach its catch point).</summary>
    public TouchProfile KnockOn { get; set; } = new(new(8f, 3f), 0.5f, 2f, 18f, 0.45f, 1f);
    /// <summary>A heavy touch (no longer made; kept for older replays).</summary>
    public TouchProfile Heavy { get; set; } = new(new(12f, 12f), 1f, 2f, 12f, 0.45f, 1f);
    /// <summary>
    /// The low band, a ball at the feet: also the height and reach of every touch at the feet (see TouchModel.InReach). Killed
    /// as a first touch with the stick released always was.
    /// </summary>
    public TrapProfile TrapLow { get; set; } = new(0.45f, 1f, 1f, 0f, 0.45f, 3f);
    /// <summary>The mid band (shin, thigh): cushioned and dropped a little ahead.</summary>
    public TrapProfile TrapMid { get; set; } = new(0.95f, 0.85f, 0.6f, 0.4f, 0.95f, 3f);
    /// <summary>The high band (chest): killed deader, closer to the body, and dropped further ahead.</summary>
    public TrapProfile TrapHigh { get; set; } = new(1.6f, 0.7f, 0.5f, 0.5f, 1.6f, 3f);

    /// <summary>Every profile with its JSON name.</summary>
    public IEnumerable<(string Name, TouchProfile? Profile)> All() =>
        new (string, TouchProfile?)[]
        {
            ("close", Close), ("jog", Jog), ("sprint", Sprint), ("knock_on", KnockOn), ("heavy", Heavy),
            ("trap_low", TrapLow), ("trap_mid", TrapMid), ("trap_high", TrapHigh),
        };

    /// <summary>Null when every profile is usable; otherwise the first problem, prefixed by the profile's name.</summary>
    public string? Validate()
    {
        foreach (var (name, p) in All())
        {
            string? bad = p == null ? "must be an object" : p.Validate();
            if (bad != null) return $"{name}: {bad}";
        }
        if (!(TrapLow.MaxBallHeight < TrapMid.MaxBallHeight && TrapMid.MaxBallHeight < TrapHigh.MaxBallHeight))
            return $"trap bands: max_ball_height must rise from trap_low to trap_mid to trap_high ({TrapLow.MaxBallHeight}, {TrapMid.MaxBallHeight}, {TrapHigh.MaxBallHeight})";
        return null;
    }
}

/// <summary>
/// Touches. In possession the touch controller (see TouchPlanner) finds, every tick, the next step whose contact window can
/// meet the ball, steers the player toward it and commits the touch one windup before its contact; at contact the push speed
/// is solved so the ball meets the foot again a few steps later.
/// </summary>
public sealed class TouchTuning
{
    public float ReachBase { get; set; } = 0.55f;
    public float ReachPerHeight { get; set; } = 0.30f;
    public float HeightMin { get; set; } = 1.60f;
    public float HeightSpan { get; set; } = 0.40f;
    /// <summary>Top speed with the ball as a fraction of top speed, per dribbling style (the close style by close dribbling, the speed style by sprint dribbling).</summary>
    public TopSpeedWithBallTuning TopSpeedWithBall { get; set; } = new();
    /// <summary>Sprint input at or above this counts as sprinting (touch mode).</summary>
    public float SprintThreshold { get; set; } = 0.5f;
    /// <summary>Stick deflection below this asks for no dribble touch; in possession it asks for a stop.</summary>
    public float StickThreshold { get; set; } = 0.1f;
    /// <summary>A stick pushed no further than this (and no sprint) dribbles with walking touches, played as close control.</summary>
    public float WalkMaxInput { get; set; } = 0.7f;

    /// <summary>
    /// The windup (the foot drawn back) starts this long before contact, seconds: the touch controller commits a touch (its foot,
    /// kind and surface) this long before its contact.
    /// </summary>
    public float Windup { get; set; } = 0.12f;
    /// <summary>
    /// The execution time of a dribble touch, seconds from its commit to its contact, by the ball's speed relative to the
    /// player, m/s (the size of the horizontal difference of their velocities when the touch is planned). The touch
    /// controller looks for a meet no sooner than this (less its commit slack) and commits the touch this long before it: a
    /// shorter time for a fast ball lets the foot meet it on an earlier swing instead of the legs steering or chasing after
    /// it. Flat at <see cref="Windup"/>, every touch would take the windup whatever the ball does; as tuned, a ball up to
    /// 4 m/s relative to him (every ordinary dribble push) takes the windup, and from there the time falls to 0.05 s at
    /// 8 m/s. A shot's strike keeps the windup.
    /// </summary>
    public Curve ExecutionTime { get; set; } = new((0f, 0.12f), (4f, 0.12f), (8f, 0.05f));
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
    /// <summary>
    /// Early execution by the angle between the stick and the ball's travel, degrees: the fraction of the normal cadence a turn
    /// may cut it to. A demand (a stick turn, a release, a waiting gesture, a shot's soft touch) shortens the touch controller's
    /// cooldown to this fraction of the last push's steps (at least one step, never sooner than <see cref="EarlyMinInterval"/>),
    /// and a turn or cut touch pushes the ball to a meet this fraction of the usual steps away (at least one). Multiplied by
    /// <see cref="EarlyExecutionDribbling"/>.
    /// </summary>
    public Curve EarlyExecution { get; set; } = new((10f, 1.0f), (30f, 0.6f), (60f, 0.35f), (90f, 0.25f));
    /// <summary>Multiplier on <see cref="EarlyExecution"/> by the close dribbling rating.</summary>
    public RatingRange EarlyExecutionDribbling { get; set; } = new(1.15f, 0.85f);
    /// <summary>Touches are at least this far apart (from the previous touch), seconds.</summary>
    public float EarlyMinInterval { get; set; } = 0.22f;
    /// <summary>
    /// Turn fatigue (0 to 100, see PlayerState.TurnFatigue) added by one touch of the touch controller, by the angle from his
    /// facing at the contact to the direction the ball is pushed, degrees (0 to 180). Multiplied by <see cref="FatigueGain"/>.
    /// </summary>
    public Curve FatigueTurn { get; set; } = new((0f, 0f), (30f, 0f), (60f, 10f), (90f, 20f), (135f, 32f), (180f, 40f));
    /// <summary>Multiplier on <see cref="FatigueTurn"/> by the close dribbling rating.</summary>
    public RatingRange FatigueGain { get; set; } = new(1.5f, 0.5f);
    /// <summary>Turn fatigue drained per second in possession, by the close dribbling rating.</summary>
    public RatingRange FatigueRecovery { get; set; } = new(10f, 30f);
    /// <summary>
    /// How much of the early touch after a turn the turn fatigue takes away, 0 to 1 by the fatigue: the early-execution
    /// fraction moves this share of the way back to 1, the normal cadence (see <see cref="EarlyExecution"/>).
    /// </summary>
    public Curve FatiguePenalty { get; set; } = new((0f, 0f), (20f, 0f), (60f, 1f));
    /// <summary>
    /// A sprint push is cut short when an opponent ahead of him could reach the path he is predicted to run while it is out
    /// of his feet: the opponent's reach is his top speed (a keeper's rush speed) times the time since the touch, plus this,
    /// metres.
    /// </summary>
    public float DangerRadius { get; set; } = 1.0f;
    /// <summary>The fewest steps a sprint push is cut to near an opponent, by the sprint dribbling rating (rounded to whole steps).</summary>
    public RatingRange DangerMinSteps { get; set; } = new(2f, 1f);
    /// <summary>
    /// The legs' speed cap through a sprint push that was cut short near an opponent, as a factor by the share of the push
    /// that was cut (0: nothing, 1: cut down to the fewest steps).
    /// </summary>
    public Curve DangerSpeed { get; set; } = new((0f, 1f), (1f, 0.9f));
    /// <summary>
    /// Multiplier on steps_per_touch.sprint by the composure rating, before the rounding to whole steps: a player without
    /// composure takes his sprint touches a little further apart.
    /// </summary>
    public RatingRange ComposureSprintSteps { get; set; } = new(1.1f, 1f);
    /// <summary>
    /// Multiplier on the early-execution fraction by the composure rating (with <see cref="EarlyExecutionDribbling"/>; the
    /// fraction stays at 1 or under): a player without composure gets less of the early touch after a turn.
    /// </summary>
    public RatingRange ComposureEarly { get; set; } = new(1.1f, 1f);
    /// <summary>
    /// The stick counts as moving (a turn is likely) when its smoothed turn rate is above this, degrees per second; then, as
    /// for a touch sent more than the execution angle off the ball's travel, the push goes to a meet at most
    /// <see cref="TurningMaxSteps"/> steps away.
    /// </summary>
    public float TurningStickRateDeg { get; set; } = 20f;
    /// <summary>The most steps to the next meet while turning, per mode.</summary>
    public TurningStepsTuning TurningMaxSteps { get; set; } = new();
    /// <summary>Largest correction added to the player's velocity while steering the ball into the contact zone, m/s.</summary>
    public float SteerMax { get; set; } = 0.6f;
    /// <summary>
    /// The steering brings the ball only to within this of the swinging foot's contact point, metres; the foot reaches the
    /// rest (at most the contact zone radius), so small misses never move the body.
    /// </summary>
    public float ReachTolerance { get; set; } = 0.08f;
    /// <summary>
    /// A turn, cut, stop or close-control touch plays the ball anywhere within this distance of the body (the feet), metres, by
    /// speed (the reference data's execution radius is 0.6 m), at its normal rated error.
    /// </summary>
    public SpeedRange TurnReach { get; set; } = new(0.45f, 0.6f);
    /// <summary>
    /// How far ahead the stick's turn rate is carried on in predictions, seconds: the predicted turn eases out over this time
    /// constant (never longer than the stick has been turning), so a thumb that stops turning is not predicted to go on.
    /// </summary>
    public float StickLeadTime { get; set; } = 0.15f;
    /// <summary>
    /// At contact a ball up to this far from the contact point (beyond the contact zone) is still played, stretched, metres;
    /// further off the touch is dropped. With the contact point 0.25-0.45 m ahead of the body this keeps the ball within about
    /// the reference data's 0.6 m execution radius.
    /// </summary>
    public float StretchReach { get; set; } = 0.25f;
    /// <summary>
    /// A stick released for no longer than this is read as still held where it was, seconds (a flick through neutral is
    /// polled at 60 Hz, so it can read neutral for a tick or two); a longer release is a stop.
    /// </summary>
    public float ReleaseDebounce { get; set; } = 0.04f;
    /// <summary>
    /// A stick turned at least this far from the ball's travel (the facing for a still ball), degrees, with the ball within
    /// <see cref="FeetTurnReach"/> of the body, turns the ball at the feet at once: the sole pulls it back onto the stick's
    /// line (the drag-back's motion) instead of waiting for a swing in front of the foot.
    /// </summary>
    public float FeetTurnDeg { get; set; } = 120f;
    /// <summary>The ball's largest distance from the body for that turn at the feet, metres (the reference data's execution radius, 0.6 m).</summary>
    public float FeetTurnReach { get; set; } = 0.6f;
    /// <summary>
    /// The turn at the feet only up to this running speed, m/s; faster, a stick turned feet_turn_deg or more from his travel is a
    /// braking arc: he slows to turn_arc_keep of his entry speed (never below turn_arc_min_speed) and turns with the ball.
    /// </summary>
    public float FeetTurnMaxSpeed { get; set; } = 3f;
    /// <summary>The braking arc's speed, as a share of the speed he came into it at.</summary>
    public float TurnArcKeep { get; set; } = 0.4f;
    /// <summary>The braking arc's lowest speed, m/s.</summary>
    public float TurnArcMinSpeed { get; set; } = 2.4f;
    /// <summary>The braking arc ends when his travel is within this of the stick, degrees.</summary>
    public float TurnArcDoneDeg { get; set; } = 15f;
    /// <summary>The braking arc brakes at this, m/s^2.</summary>
    public float TurnArcBrake { get; set; } = 13.5f;
    /// <summary>While the braking arc brakes, his body turns at least this fast, deg/s, by close dribbling rating.</summary>
    public RatingRange TurnArcRotation { get; set; } = new(600f, 900f);
    /// <summary>The steering correction changes by at most this, m/s^2 (0: at once), so it never steps the velocity.</summary>
    public float SteerAccel { get; set; } = 6f;
    /// <summary>With an even steps_per_touch, every this-many-th touch takes one step more so the other foot plays on.</summary>
    public int FootSwitchEvery { get; set; } = 4;
    /// <summary>Steps of the stride clock between dribble touches, per mode.</summary>
    public StepsPerTouchTuning StepsPerTouch { get; set; } = new();
    /// <summary>Trajectory and contact limits per touch kind.</summary>
    public TouchProfilesTuning Profiles { get; set; } = new();

    /// <summary>
    /// The fastest push of a skill move's hook (a fake cut), m/s. Touches take theirs from their profile (see <see cref="Profiles"/>).
    /// </summary>
    public float MaxPush { get; set; } = 12f;
    /// <summary>Standard deviation of the touch speed factor (the touch's dribbling rating), before the speed scaling.</summary>
    public RatingRange SpeedError { get; set; } = new(0.10f, 0.02f);
    /// <summary>Standard deviation of the touch direction, degrees (ball_control), before the speed scaling.</summary>
    public RatingRange DirectionErrorDeg { get; set; } = new(7f, 1f);
    /// <summary>Touch errors scale by (base + slope * speed fraction).</summary>
    public float ErrorSpeedBase { get; set; } = 0.4f;
    public float ErrorSpeedSlope { get; set; } = 0.6f;
    /// <summary>A weak-foot touch's error multiplier, by the weak_foot rating (1 to 99).</summary>
    public Curve WeakFootError { get; set; } = new((1f, 1.4f), (99f, 1f));
    /// <summary>The ball must be this far past the body's centreline on the other side to be touched with the other foot, metres.</summary>
    public float FootSideOffset { get; set; } = 0.15f;

    /// <summary>Earliest first touch of a loose ball after the player's previous touch, seconds.</summary>
    public float TurnMinInterval { get; set; } = 0.10f;
    /// <summary>A touch turning the ball more than this at speed is a cut: the player loses the balance percentage of speed.</summary>
    public float CutDeg { get; set; } = 60f;
    /// <summary>Player speed above which a sharp turn is a cut, m/s.</summary>
    public float CutMinSpeed { get; set; } = 2.0f;

    /// <summary>
    /// After a dribble, turn or cut touch the acceleration (its cap and its soft landing) is multiplied by (1 + this + the turn extra) for the boost
    /// duration, so the player accelerates harder out of the touch (the touch's dribbling rating).
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
    /// (sprint dribbling rating).
    /// </summary>
    public RatingRange KnockOnCatchTime { get; set; } = new(1.8f, 1.3f);
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
    /// <summary>
    /// Off possession the turn rate is multiplied by (1 + this): -0.2 makes the top of the range the reference data's 360 deg/s without the
    /// ball against 450 with it.
    /// </summary>
    public float OffBallTurnBonus { get; set; } = -0.2f;
    /// <summary>How far ahead the touch controller looks for a step that can play the ball, seconds.</summary>
    public float PredictionHorizon { get; set; } = 1.5f;
    /// <summary>The keeper within this of the loose ball contests it, metres.</summary>
    public float RegainKeeperDistance { get; set; } = 3f;
    /// <summary>The keeper, or the other team's claim holder, contests the ball when he would reach the meeting no more than this after the holder, seconds.</summary>
    public float RegainKeeperMargin { get; set; } = 0.3f;
    /// <summary>An uncontested regain takes the ball from this far from the body (beyond foot reach, at the stretched errors), metres.</summary>
    public float RegainReach { get; set; } = 1.2f;
}

/// <summary>Lock-on: the claim on a loose ball (see LockOn). Values marked (ref) are read from reference data.</summary>
public sealed class LockOnTuning
{
    /// <summary>How far ahead the loose ball's path is forecast, seconds (the reference searches 10 s on a full pitch).</summary>
    public float HorizonSeconds { get; set; } = 4f;
    /// <summary>The real ball further than this from its forecast sample has left the path: the forecast is rebuilt, metres.</summary>
    public float RebuildDriftM { get; set; } = 0.05f;
    /// <summary>A team-mate takes the claim from the holder only by arriving this much earlier, seconds (ref).</summary>
    public float TakeoverMarginSeconds { get; set; } = 0.1f;
    /// <summary>The stick slides the meeting to a later sample whose bearing is within this of the stick, degrees (ref).</summary>
    public float StickSlideDeg { get; set; } = 40f;
    /// <summary>
    /// A computer-controlled player's reaction to an opponent's strike: he makes no claim run until the ball has travelled his
    /// distance to it at the strike times this, by awareness (the shape from reference data). Part of the team hash, not the tuning hash.
    /// </summary>
    public Curve ReactionRatio { get; set; } = new((1f, 0.35f), (99f, 0.05f));
    /// <summary>... or this long has passed, seconds (ref). Part of the team hash.</summary>
    public float ReactionMaxSeconds { get; set; } = 0.5f;
}

/// <summary>A value per kind of pass.</summary>
public sealed class PassPair
{
    public float Ground { get; set; }
    public float Through { get; set; }
    public float For(PassKind kind) => kind == PassKind.Through ? Through : Ground;
}

/// <summary>A low and a high end.</summary>
public sealed class MinMax
{
    public float Min { get; set; }
    public float Max { get; set; }
}

/// <summary>
/// Passes (see PassModel). Values marked (ref) are read from reference data; the others are ours, to be set by the pass probe.
/// The whole block is part of the team hash, not the tuning hash.
/// </summary>
public sealed class PassTuning
{
    /// <summary>The hold that points the pass at its furthest; the pass is released for the player then, seconds (ref).</summary>
    public float ChargeSeconds { get; set; } = 0.4f;
    /// <summary>The nearest a selection point lies along the stick, and the nearest team-mate that can be passed to, metres.</summary>
    public float MinDistance { get; set; } = 1f;
    /// <summary>The furthest a selection point lies along the stick, at a full hold, metres.</summary>
    public PassPair MaxDistance { get; set; } = new() { Ground = 22f, Through = 26f };
    /// <summary>The speed the ball should still have when it reaches the target, m/s.</summary>
    public PassPair ArrivalSpeed { get; set; } = new() { Ground = 5f, Through = 6f };
    /// <summary>The fastest a pass can be struck: min at rating 1, max at 99, m/s (times the body-angle and weak-foot power of a shot).</summary>
    public MinMax SpeedCap { get; set; } = new() { Min = 14f, Max = 24f };
    /// <summary>From the press to the strike, by the charge 0..1, seconds (the shape from reference data).</summary>
    public Curve ExecutionSeconds { get; set; } = new((0f, 0.3f), (1f, 0.4f));
    /// <summary>Added to it by the body angle to the target, degrees (the shot's body-angle curve).</summary>
    public Curve AngleExtraSeconds { get; set; } = new((0f, 0f), (45f, 0f), (90f, 0.10f), (135f, 0.20f), (180f, 0.30f));
    /// <summary>A through ball's target lies this long ahead of the receiver's run: [min, max] seconds.</summary>
    public float[] ThroughAheadSeconds { get; set; } = { 0.6f, 1.4f };
    /// <summary>The passer is planted this long after the strike, seconds.</summary>
    public float StunSeconds { get; set; } = 0.15f;
    /// <summary>A flicked pass (queued on the way to the ball, no time for it) is struck at this share of the speed cap.</summary>
    public float FlickSpeed { get; set; } = 0.7f;
    /// <summary>The speed a pass is raised by per step while the defender still reaches its lane first, m/s.</summary>
    public float SpeedStep { get; set; } = 1f;
}

/// <summary>Who the team's intent drives (see World.Controlled). Part of the team hash.</summary>
public sealed class ControlTuning
{
    /// <summary>With the ball loose and no named receiver, control jumps to the team's claim holder once he has held the claim this long, seconds.</summary>
    public float ClaimJumpSeconds { get; set; } = 0.1f;
    /// <summary>After a jump the new player's left stick reads as at rest this long, unless it has turned more than 30 degrees since, seconds.</summary>
    public float StickSettleSeconds { get; set; } = 0.15f;
}

/// <summary>
/// The team-mate's bot (see MateBot): the fields he steers by and his run in behind. The shapes are from reference data, scaled
/// to a 20 m wide arena; the weights are ours, set by the team-mate probe. Part of the team hash.
/// </summary>
public sealed class MateTuning
{
    /// <summary>The fields are sampled this far either side of him on both axes, metres.</summary>
    public float SampleM { get; set; } = 2f;
    /// <summary>Spread: he keeps this far off the carrier (and where the carrier will be in 0.2 s), metres; the layer is clamped at 3 m either way.</summary>
    public float SpreadRadiusM { get; set; } = 4f;
    public float SpreadWeight { get; set; } = 1f;
    /// <summary>Opponents: he keeps this far off each outfield opponent, metres.</summary>
    public float OpponentRadiusM { get; set; } = 2.5f;
    public float OpponentWeight { get; set; } = 0.1f;
    /// <summary>Free space onside: the distance to the nearest opponent or board, zero beyond the offside line.</summary>
    public float SpaceWeight { get; set; } = 1f;
    /// <summary>Pass lane: 1 where a ground pass from the ball gets there before the defender reaches its lane.</summary>
    public float LaneWeight { get; set; } = 1f;
    /// <summary>Gap: closeness to the widest gap between the defender and the boards beyond the line, while a run is armed.</summary>
    public float GapWeightArmed { get; set; } = 3f;
    /// <summary>Offside line: closeness to the line from the onside side, while a run is armed ...</summary>
    public float LineWeightArmed { get; set; } = 5f;
    /// <summary>... and on go, when it pushes him on past it.</summary>
    public float LineWeightGo { get; set; } = -3f;
    /// <summary>Goal mouth: closeness to the goal centre.</summary>
    public float GoalWeight { get; set; } = 0.3f;
    /// <summary>A slope flatter than this leaves him standing, facing the ball, per metre.</summary>
    public float StillGradient { get; set; } = 0.05f;
    /// <summary>Moving away from the goal he goes at this share of the jog pace.</summary>
    public float BackwardJogShare { get; set; } = 0.6f;
    /// <summary>A run is armed with the ball within this of the offside line, metres.</summary>
    public float ArmBallToLineM { get; set; } = 10f;
    /// <summary>... while the attack has got no further than this (0 at the back wall, 1 at attack_full_from_goal_m from the goal) or the carrier is within arm_carrier_to_defender_m of the defender.</summary>
    public float ArmMaxProgress { get; set; } = 0.5f;
    public float ArmCarrierToDefenderM { get; set; } = 12f;
    /// <summary>Attack progress is 1 with the ball this close to the goal line, metres.</summary>
    public float AttackFullFromGoalM { get; set; } = 8f;
    /// <summary>He goes once within this of the line, metres.</summary>
    public float GoWithinM { get; set; } = 0.5f;
    /// <summary>Armed, he waits this far onside of the line, so a step up by the defender does not catch him, metres.</summary>
    public float ArmedOnsideM { get; set; } = 0.8f;
    /// <summary>Back onside by more than this he arms again, metres.</summary>
    public float RearmOnsideM { get; set; } = 2f;
    /// <summary>A pass under way cancels his run while he is less than this onside, metres.</summary>
    public float PassCancelOnsideM { get; set; } = 3.5f;
    /// <summary>So does the ball within this of the goal line, metres.</summary>
    public float RunCancelGoalM { get; set; } = 8f;
    /// <summary>The free-space field counts no more than this distance to the nearest opponent or board, metres: further out he is free enough.</summary>
    public float SpaceCapM { get; set; } = 4f;
    /// <summary>He moves at his full asked speed on a slope this steep or steeper, eased in below it, per metre.</summary>
    public float FullGradient { get; set; } = 0.4f;
    /// <summary>A run that has been past the line this long with no pass is given up, seconds.</summary>
    public float GoBeyondSeconds { get; set; } = 0f;
    /// <summary>After a run he waits this long before he arms again, seconds.</summary>
    public float RearmSeconds { get; set; } = 4f;
    /// <summary>He holds to where the line will be this long from now while it comes back up the pitch at him, seconds.</summary>
    public float LineLeadSeconds { get; set; } = 0.8f;
    /// <summary>He does not go while the line comes back up the pitch faster than this, m/s.</summary>
    public float GoMaxLineSpeed { get; set; } = 0.75f;
    /// <summary>He does not move toward a board nearer than this, metres, nor toward the goal line nearer than goal_line_margin_m.</summary>
    public float BoardMarginM { get; set; } = 1.5f;
    public float GoalLineMarginM { get; set; } = 3f;
    /// <summary>A player within this of his path makes him curve round, until nobody is within body_clear_m, metres.</summary>
    public float BodyAvoidM { get; set; } = 1f;
    public float BodyClearM { get; set; } = 2f;
    /// <summary>After a bump he steers at least this far off it, degrees, for bump_steer_ticks.</summary>
    public float BumpSteerDeg { get; set; } = 60f;
    public int BumpSteerTicks { get; set; } = 10;
}

/// <summary>A received pass: what its speed costs the first touch. Part of the team hash.</summary>
public sealed class ReceiveTuning
{
    /// <summary>The first touch's speed and direction spreads are multiplied by this, against the ball's ground speed relative to him, m/s.</summary>
    public Curve Error { get; set; } = new((0f, 1f), (10f, 1.3f), (20f, 1.8f));
    /// <summary>No next touch for this long after the first, against the same speed, seconds.</summary>
    public Curve Cooldown { get; set; } = new((0f, 0.1f), (10f, 0.2f), (20f, 0.35f));
    /// <summary>Both are multiplied by this, by first_touch.</summary>
    public RatingRange Rating { get; set; } = new(1.3f, 0.7f);
    /// <summary>The cooldown with a shot, pass or skill gesture queued, seconds.</summary>
    public float QueuedCooldown { get; set; } = 0.1f;
}

/// <summary>Shot model constants.</summary>
public sealed class ShotTuning
{
    /// <summary>
    /// A first-time strike (struck at the meeting, no trap): the direction and height spreads are multiplied by this, against the
    /// ball's ground speed relative to the shooter, m/s.
    /// </summary>
    public Curve FirstTimeError { get; set; } = new((0f, 1f), (10f, 1.25f), (20f, 1.6f));
    /// <summary>A flick (a queued shot struck at the first contact, before its wind-up is done) is made only this close to the goal centre, metres.</summary>
    public float FlickMaxDistanceM { get; set; } = 10f;
    /// <summary>A flick's ball speed is the same charge's times this.</summary>
    public float FlickPower { get; set; } = 0.6f;
    /// <summary>A flick's direction and height spreads are multiplied by this.</summary>
    public float FlickError { get; set; } = 1.5f;
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
    /// <summary>
    /// Spread multiplier by the body angle: how far the intended direction is from where the shooter faces, degrees (0 to 180).
    /// </summary>
    public Curve AngleAccuracy { get; set; } = new((0f, 1f), (30f, 1f), (60f, 1.5f), (90f, 2.4f), (120f, 3.4f), (180f, 4.5f));
    /// <summary>Intended launch speed multiplier by the same body angle, degrees.</summary>
    public Curve AnglePower { get; set; } = new((0f, 1f), (45f, 1f), (90f, 0.82f), (135f, 0.65f), (180f, 0.55f));
    /// <summary>
    /// Seconds added to the wind-up by the body angle at the release, degrees: the body has further to come round before the
    /// strike.
    /// </summary>
    public Curve AngleExtraWindupS { get; set; } = new((0f, 0f), (45f, 0f), (90f, 0.10f), (135f, 0.20f), (180f, 0.30f));
    /// <summary>
    /// Spread multiplier by how open the goal mouth is from the ball: the angle between the posts as a share of the angle they
    /// would make seen square on from the same distance (1 in front of goal, toward 0 along the goal line).
    /// </summary>
    public Curve GoalMouthAccuracy { get; set; } = new((0.1f, 2.2f), (0.2f, 1.8f), (0.4f, 1.35f), (0.6f, 1.2f), (0.85f, 1.05f), (1f, 1f));
    /// <summary>A weak-foot strike's spread multiplier, by the weak_foot rating (1 to 99).</summary>
    public Curve WeakFootAccuracy { get; set; } = new((1f, 1.72f), (99f, 1f));
    /// <summary>A weak-foot strike's launch speed multiplier, by the weak_foot rating (1 to 99).</summary>
    public Curve WeakFootPower { get; set; } = new((1f, 0.85f), (99f, 1f));
    /// <summary>A weak-foot strike's side-spin multiplier, by the weak_foot rating (1 to 99).</summary>
    public Curve WeakFootCurl { get; set; } = new((1f, 0.6f), (99f, 1f));
    /// <summary>Launch speed multiplier by the shooter's running speed as a share of his top speed (0 standing, 1 flat out).</summary>
    public Curve RunSpeedPower { get; set; } = new((0f, 0.97f), (0.5f, 1f), (1f, 1.03f));
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

    /// <summary>The aimed point on the goal line is clamped to this far either side of centre (wider than the frame: a wild aim misses).</summary>
    public float MaxTargetZ { get; set; } = 8f;
    /// <summary>Aim assist applies when the raw target is outside the frame by less than this.</summary>
    public float AssistRange { get; set; } = 1.5f;
    /// <summary>The assist pulls toward a point this far inside the nearer post.</summary>
    public float AssistInset { get; set; } = 0.6f;
    /// <summary>A strike needs the ball within foot reach plus this.</summary>
    public float StrikeReachMargin { get; set; } = 0.2f;
    public float StrikeMaxHeight { get; set; } = 0.6f;

    // ---- the striking foot (see ShotModel.ChooseStrike) ----
    /// <summary>
    /// A ball leaving more than this far from where the shooter faces, degrees, either way, is struck by the foot on the side
    /// it leaves, whatever his preference.
    /// </summary>
    public float FootBySideDeg { get; set; } = 150f;
    /// <summary>A ball leaving within this of where he faces counts as dead ahead, degrees: the strong foot, the type's own surface.</summary>
    public float DeadAheadDeg { get; set; } = 22.5f;
    /// <summary>
    /// The dead-ahead swap, seconds: in a dribble, a shot aimed dead ahead goes to the weak foot when its swing comes at least
    /// this much sooner than the strong foot's (see ShotModel.WithContact). Counted in whole ticks.
    /// </summary>
    public float DeadAheadSwapS { get; set; } = 0.1f;
    /// <summary>The ceiling of the outside cap (see <see cref="OutsideMaxDeg"/>), degrees ...</summary>
    public float OpenMaxDeg { get; set; } = 67.5f;
    /// <summary>... and for a finesse or low finesse shot.</summary>
    public float FinesseOpenMaxDeg { get; set; } = 79f;
    /// <summary>
    /// The outside cap, degrees (curve rating): how far toward his strong foot's side a player still strikes with that foot's
    /// outside; further round the weak foot strikes. Never past the ceiling.
    /// </summary>
    public RatingRange OutsideMaxDeg { get; set; } = new(30f, 67.5f);
    /// <summary>The outside_foot trait widens the outside cap by this, degrees (still never past the ceiling).</summary>
    public float OutsideFootBonusDeg { get; set; } = 12f;
    /// <summary>The far-post rule's distance band for each shot type (see ShotModel.FarPostApplies).</summary>
    public FarPostBands FarPostBand { get; set; } = new();
    /// <summary>A shooter within this of the pitch's centre line (Z = 0) is on neither flank, metres: no far-post rule.</summary>
    public float FarPostFlankMinM { get; set; } = 0.3f;
    /// <summary>A strike with the outside of the foot: spread multiplier ...</summary>
    public float OutsideAccuracy { get; set; } = 1.15f;
    /// <summary>... and launch speed multiplier.</summary>
    public float OutsideSpeed { get; set; } = 0.92f;
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
    public float ArmSweep { get; set; } = 0.9f;
    public float ArmSweepTime { get; set; } = 0.15f;
    public RatingRange Stretch { get; set; } = new(2.2f, 2.9f);
    public float StretchHeightReference { get; set; } = 1.90f;
    public float PredictionNoise { get; set; } = 0.1f;
    public float ParrySpeedMin { get; set; } = 0.4f;
    public float ParrySpeedMax { get; set; } = 0.6f;
    /// <summary>Sideways steering of a parry relative to its backward travel, at handling 99. Zero at handling 1.</summary>
    public float ParryWideMax { get; set; } = 1.5f;
    public float ParryLift { get; set; } = 0.15f;
    /// <summary>Standard deviation of a parry's direction error, degrees, at difficulty 0.5 (times 0.5 + the difficulty).</summary>
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
    /// <summary>
    /// Set (planted or spread, still) at the strike within this distance of the ball, he is fully ready, metres (3.5, from
    /// reference data)...
    /// </summary>
    public float ReadyFull { get; set; } = 3.5f;
    /// <summary>...and not ready at all from this far, linear between, metres.</summary>
    public float ReadyRange { get; set; } = 6f;
    /// <summary>
    /// Fully ready, his standing body (what the ball hits before he moves) has this effective radius around his upright axis: his
    /// torso with a set keeper's spread legs and arms, averaged; not ready it is <see cref="BodyRadius"/>, linear between, metres.
    /// </summary>
    public float HullRadius { get; set; } = 0.42f;
    /// <summary>
    /// His save hull (0.75 m, from reference data): a ball his dive will not get to in time that passes within this of his upright
    /// axis always draws a reflex (a save or an attempt), metres.
    /// </summary>
    public float SaveHullRadius { get; set; } = 0.75f;
    /// <summary>A ball that hits the body slower than this is held; faster it is blocked, m/s.</summary>
    public float BodyHoldSpeed { get; set; } = 8f;

    // ---- hold arc ----
    /// <summary>The arc's base distance out from the goal mouth (the reference geometry), metres (0.70, from reference data).</summary>
    public float ArcRadius { get; set; } = 0.70f;
    /// <summary>The keeper never stands closer than this to his goal line on the arc, metres (0.60, from reference data).</summary>
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
    /// <summary>Each movement decision moves toward a point at most this far away, metres (3.5, from reference data).</summary>
    public float MoveTargetCap { get; set; } = 3.5f;
    /// <summary>Closing down never takes him (or holds him) further than this from the goal mouth, metres.</summary>
    public float HoldGroundMaxOut { get; set; } = 8.5f;
    /// <summary>Seconds of slack in a rush race inside which he starts toward the ball before he is sure (anticipation), by rushing_out.</summary>
    public RatingRange RushAnticipation { get; set; } = new(0.0f, 0.25f);
    /// <summary>Anticipating a rush, he moves toward the ball at this fraction of his rush speed.</summary>
    public float AnticipateSpeedFraction { get; set; } = 0.75f;

    // ---- closing down ----
    /// <summary>
    /// Closing down: how far out from the goal mouth he comes, metres, by the ball's distance from the mouth, once an attacker
    /// comes in with the ball (scaled by <see cref="CloseDownPositioning"/>; never shallower than his arc).
    /// </summary>
    public Curve CloseDownDepth { get; set; } = new((8f, 5.0f), (12f, 4.6f), (16f, 3.6f), (20f, 2.6f), (25f, 1.7f), (30f, 1.2f));
    /// <summary>Multiplier on <see cref="CloseDownDepth"/> beyond the arc by positioning: a poorly positioned keeper comes out less.</summary>
    public RatingRange CloseDownPositioning { get; set; } = new(0.6f, 1.0f);
    /// <summary>He starts closing down once the ball is within this distance of the goal mouth...</summary>
    public float CloseDownRange { get; set; } = 30f;
    /// <summary>...and the attacker comes at goal with it at least this fast, m/s (from then on he keeps closing down this round).</summary>
    public float CloseDownMinSpeed { get; set; } = 1.5f;
    /// <summary>Closing down (not committing), he stays at least this far short of the ball along the cover line, metres.</summary>
    public float CloseDownGap { get; set; } = 3.0f;
    /// <summary>A reposition longer than <see cref="JogThreshold"/> is run at rush speed once the ball is this close to the mouth, metres.</summary>
    public float UrgentRange { get; set; } = 12f;
    /// <summary>Moving off or toward his line, he turns round only for a new spot at least this much the other way from where he would stop, metres.</summary>
    public float ReverseThreshold { get; set; } = 1.0f;
    /// <summary>Moving away from the ball faster than this, he is shown backpedalling, m/s.</summary>
    public float BackpedalShowSpeed { get; set; } = 1.0f;

    // ---- chips ----
    /// <summary>Backpedalling speed (facing the ball), m/s, by positioning.</summary>
    public RatingRange BackpedalSpeed { get; set; } = new(3.5f, 5.0f);
    /// <summary>The highest ball he can get a hand to with a standing leap, as a fraction of his height (a chip above it is over him).</summary>
    public float ChipReachHeightFraction { get; set; } = 1.45f;
    /// <summary>Recovering from a chip, he heads for where it has dropped to this fraction of his height, to leap at it there...</summary>
    public float ChipTakeHeightFraction { get; set; } = 1.25f;
    /// <summary>...needing to get within this distance of that point...</summary>
    public float ChipTipReach { get; set; } = 0.3f;
    /// <summary>...this long before the ball does (the leap's timing), seconds.</summary>
    public float ChipLeapLead { get; set; } = 0.2f;
    /// <summary>Standard deviation of his error judging his chip-safe depth, as a fraction of it, by positioning (drawn once a round).</summary>
    public RatingRange ChipJudgeError { get; set; } = new(0.30f, 0.05f);
    /// <summary>A ball met in a backward leap after backpedalling is held as a diving catch of a ball this much faster would be (its speed is divided by it).</summary>
    public float BackpedalCatchFactor { get; set; } = 0.4f;

    // ---- staying big ----
    /// <summary>Within this distance of an attacker on the ball that he cannot beat to it, he stops and spreads (makes himself big), metres.</summary>
    public float PreSpreadRange { get; set; } = 3.5f;
    /// <summary>
    /// Closing down an attacker on the ball who is within this distance of him and in front, he always charges him (to
    /// <see cref="JockeyDistance"/>) instead of keeping <see cref="CloseDownGap"/>, metres.
    /// </summary>
    public float EngageRange { get; set; } = 8.5f;

    // ---- charging and jockeying ----
    /// <summary>
    /// Charging (closing an attacker down at rush speed, through to jockeying range): only while the ball is within this distance
    /// of the goal centre, metres.
    /// </summary>
    public float ChargeRange { get; set; } = 20f;
    /// <summary>
    /// He charges once he can get to within <see cref="JockeyDistance"/> of the ball (the attacker coming on toward him) within
    /// this many seconds, by rushing_out: the time he accepts being caught on the way, before the chip risk is weighed.
    /// </summary>
    public RatingRange ChargeTime { get; set; } = new(0.35f, 2.0f);
    /// <summary>
    /// The chip risk weighed against a charge: <see cref="ChargeTime"/> is divided by 1 + this x the metres he would end up beyond
    /// his judged chip-safe depth (for the ball's distance now), by rushing_out (a brave keeper weighs it less), per metre.
    /// </summary>
    public RatingRange ChargeChipWeight { get; set; } = new(0.4f, 0f);
    /// <summary>
    /// He charges only while the depth where he would meet the attacker is at most this far beyond his judged chip-safe depth for
    /// the ball's distance now (<see cref="ChargeChipHysteresis"/> more once going), metres, by rushing_out: the chip risk a
    /// brave keeper accepts.
    /// </summary>
    public RatingRange ChargeChipAllowance { get; set; } = new(1f, 6f);
    /// <summary>Once charging, he carries on up to this much further beyond his chip-safe depth than he would start at, metres.</summary>
    public float ChargeChipHysteresis { get; set; } = 2f;
    /// <summary>With the ball up to this much beyond <see cref="ChargeRange"/> and a charge he would make, he holds his depth (no retreat first), metres.</summary>
    public float ChargeHoldMargin { get; set; } = 4f;
    /// <summary>A charge is dropped once the time to close exceeds this multiple of what he accepts (plus 0.25 s).</summary>
    public float ChargeDropFactor { get; set; } = 1.5f;
    /// <summary>
    /// Charging when a shot winds up, he keeps coming to block it if, coming on until the last <see cref="ChargePlantLead"/>
    /// seconds and then braking, he would be within <see cref="ChargeBlockDistance"/> metres of the ball at the strike (he may be
    /// caught still moving, or chipped); otherwise he pulls up at once to be set.
    /// </summary>
    public float ChargePlantLead { get; set; } = 0.3f;
    /// <summary>See <see cref="ChargePlantLead"/>, metres.</summary>
    public float ChargeBlockDistance { get; set; } = 4.5f;
    /// <summary>Charging, he closes to this distance from the ball and then jockeys there, metres.</summary>
    public float JockeyDistance { get; set; } = 2.0f;
    /// <summary>Jockeying, he gives ground (toward his goal) no faster than this, m/s.</summary>
    public float JockeyRetreatSpeed { get; set; } = 1.5f;

    /// <summary>
    /// With the attacker within this distance of the ball (close control, jogging touches), a dive at it still wants his rush
    /// margin; further from it (a ball pushed past him) he lunges with none, metres.
    /// </summary>
    public float ContestedDistance { get; set; } = 1.0f;
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

    // ---- outside the penalty area: the slide tackle and the poke ----
    /// <summary>
    /// Outside his penalty area he may not use his hands, so he plays a loose ball there with his feet: he races for it up to this
    /// far beyond the area's front line, metres (inside the area he rushes and charges out to the line, the area's depth).
    /// </summary>
    public float SlideBeyondArea { get; set; } = 5f;
    /// <summary>He goes into a slide tackle at a ball outside his area from this far away (the leading foot's full reach), metres.</summary>
    public float SlideReach { get; set; } = 2.4f;
    /// <summary>How far ahead of his feet the leading foot is when the slide starts, metres.</summary>
    public float SlideFootStart { get; set; } = 0.3f;
    /// <summary>Speed of the leading foot along the ground in a slide, m/s.</summary>
    public float SlideSpeed { get; set; } = 6f;
    /// <summary>The body travels this fraction of the leading foot's travel in a slide (a poke: a fifth of it).</summary>
    public float SlideBodyFollow { get; set; } = 0.75f;
    /// <summary>Radius of the sliding leg the ball can meet, metres.</summary>
    public float SlideFootRadius { get; set; } = 0.25f;
    /// <summary>He slides only at a ball whose centre is below this height; a higher one (below the claim height) he pokes at standing, metres.</summary>
    public float SlideMaxHeight { get; set; } = 0.35f;
    /// <summary>A slide that has not met the ball ends on the ground after this long at most, seconds.</summary>
    public float SlideMaxSeconds { get; set; } = 0.7f;
    /// <summary>A ball met in a slide or a poke is knocked on along the slide's direction at this speed, m/s...</summary>
    public float SlideKnockSpeedMin { get; set; } = 4f;
    public float SlideKnockSpeedMax { get; set; } = 8f;
    /// <summary>...within this angle either side of it, degrees, and lifted a little (vertical part of the unit direction).</summary>
    public float SlideKnockAngleDeg { get; set; } = 30f;
    public float SlideKnockLift { get; set; } = 0.1f;
    /// <summary>A ball outside his area closer than this he pokes at with a foot, standing, instead of sliding, metres.</summary>
    public float PokeReach { get; set; } = 1.0f;
    /// <summary>Speed of the foot going to the ball in a poke, m/s.</summary>
    public float PokeSpeed { get; set; } = 4f;
    /// <summary>A poke lasts this long before he is set again (he stays on his feet), seconds.</summary>
    public float PokeSeconds { get; set; } = 0.35f;

    // ---- the reflex save ----
    /// <summary>
    /// Seconds from the strike to his reflex, by reflexes (from reference data), with the reaction's noise and
    /// still-moving penalty. At the reflex he decides: when his full reaction and dive will not get to the ball in time, he makes
    /// a reflex save instead.
    /// </summary>
    public RatingRange ReflexTime { get; set; } = new(0.20f, 0.08f);
    /// <summary>The reflex move's execution time for an easy save, by reflexes (from reference data)...</summary>
    public RatingRange ReflexExecutionEasy { get; set; } = new(0.04f, 0.0f);
    /// <summary>...and for one at the edge of his range, seconds (from reference data), by difficulty between.</summary>
    public RatingRange ReflexExecutionHard { get; set; } = new(0.08f, 0.03f);
    /// <summary>A reflex hand, foot or body moves out at this speed, m/s, by reflexes...</summary>
    public RatingRange ReflexSpeed { get; set; } = new(3.0f, 4.8f);
    /// <summary>...to at most this far from his upright axis (his standing hull plus a short lunge), metres, by reach, scaled by height / <see cref="StretchHeightReference"/>.</summary>
    public RatingRange ReflexRange { get; set; } = new(1.3f, 1.7f);
    /// <summary>A reflex can be aimed no higher than this fraction of his height.</summary>
    public float ReflexHeightFraction { get; set; } = 1.15f;
    /// <summary>Within this much beyond his reflex range, a ball neither a dive nor a full reflex gets to in time draws a reflex attempt, metres.</summary>
    public float ReflexAttemptMargin { get; set; } = 0.6f;
    /// <summary>Fraction of the body that follows a reflex limb sideways (a short lunge).</summary>
    public float ReflexBodyFollow { get; set; } = 0.2f;
    /// <summary>A reflex target at most this high is played with a foot (a leg shot out), metres.</summary>
    public float ReflexFootHeight { get; set; } = 0.45f;
    /// <summary>Set and still, his feet are this far out from his axis (where a reflex foot starts), metres; moving, a body's radius.</summary>
    public float StanceFootOffset { get; set; } = 0.35f;
    /// <summary>Ready (at least half) at the strike, a reflex target up to this high is a body block (the spread body thrown at it), metres.</summary>
    public float ReflexBodyHeight { get; set; } = 1.3f;

    // ---- save difficulty ----
    /// <summary>
    /// Save difficulty (0 easy, 1 at full stretch) by the lateral range the save needs as a fraction of his stretch,
    /// from reference data, for a low ball...
    /// </summary>
    public Curve DifficultyLow { get; set; } = new((0f, 0.10f), (0.35f, 0.25f), (0.7f, 0.70f), (1f, 1f));
    /// <summary>...a ball at mid height (from reference data)...</summary>
    public Curve DifficultyMid { get; set; } = new((0f, 0f), (0.35f, 0.15f), (0.7f, 0.60f), (1f, 1f));
    /// <summary>...and a high ball (from reference data).</summary>
    public Curve DifficultyHigh { get; set; } = new((0f, 0.10f), (0.35f, 0.30f), (0.7f, 0.75f), (1f, 1f));
    /// <summary>The heights the low, mid and high curves are for, metres; linear between, flat beyond.</summary>
    public float DifficultyLowHeight { get; set; } = 0.4f;
    public float DifficultyMidHeight { get; set; } = 1.0f;
    public float DifficultyHighHeight { get; set; } = 1.8f;
    /// <summary>
    /// Added to the difficulty by his speed away from the save (moving the wrong way), m/s (from reference data).
    /// </summary>
    public Curve CounterMomentumPenalty { get; set; } = new((0f, 0f), (1f, 0.2f), (2f, 0.45f), (4f, 0.8f));
    /// <summary>
    /// The dive's execution time (from his reaction until the push-off carries him) for an easy save, by reflexes (from reference data)...
    /// </summary>
    public RatingRange SaveExecutionEasy { get; set; } = new(0.13f, 0.07f);
    /// <summary>...and for one at full stretch, seconds (from reference data), by difficulty between (capped at 1).</summary>
    public RatingRange SaveExecutionHard { get; set; } = new(0.17f, 0.11f);

    // ---- catch range ----
    /// <summary>
    /// How far from his hands' rest point a ball can be held standing, metres, by its speed in m/s, at handling 1 (from reference data)...
    /// </summary>
    public Curve CatchRangeStandingMin { get; set; } = new((8f, 1.2f), (12f, 0.8f), (16f, 0f));
    /// <summary>...at handling 99 (from reference data), linear between by handling.</summary>
    public Curve CatchRangeStandingMax { get; set; } = new((14f, 1.4f), (22f, 1.1f), (26f, 0.6f), (30f, 0f));
    /// <summary>The same in a dive, at handling 1 (from reference data)...</summary>
    public Curve CatchRangeDivingMin { get; set; } = new((8f, 2.0f), (12f, 1.4f), (15f, 0f));
    /// <summary>...and 99 (from reference data).</summary>
    public Curve CatchRangeDivingMax { get; set; } = new((14f, 2.2f), (20f, 2.0f), (23f, 1.4f), (26f, 0f));
    /// <summary>The catch range is scaled by this, by the seconds from the strike to the contact (from reference data).</summary>
    public Curve CatchRangeTime { get; set; } = new((0.15f, 0.3f), (0.3f, 0.7f), (0.5f, 1f));
    /// <summary>A save harder than this is never held.</summary>
    public float CatchMaxDifficulty { get; set; } = 0.8f;
    /// <summary>A dive save at least this hard is a fingertip (the ball kept going, bent away).</summary>
    public float FingertipDifficulty { get; set; } = 0.92f;

    // ---- saves ----
    /// <summary>The dive wedge: a target is at least this far from the feet, metres (0.05, from reference data).</summary>
    public float WedgeMinRadius { get; set; } = 0.05f;
    /// <summary>The dive wedge: a target is at most this far from the feet, metres (3.5, from reference data).</summary>
    public float WedgeMaxRadius { get; set; } = 3.5f;
    /// <summary>The dive wedge: a target is within this angle of the keeper's sideways axis, degrees (75, from reference data).</summary>
    public float WedgeHalfAngleDeg { get; set; } = 75f;
    /// <summary>The dive wedge: above this height a target must be in the keeper's save plane, metres (0.8, from reference data).</summary>
    public float WedgeLowCap { get; set; } = 0.8f;
    /// <summary>Above the low cap, how far in front of the keeper a target may be, metres.</summary>
    public float WedgeHighForward { get; set; } = 0.5f;
    /// <summary>Largest random delay to a dive's start for an easy save, seconds (0.001, from reference data).</summary>
    public float SaveTimeJitterEasy { get; set; } = 0.001f;
    /// <summary>Largest random delay to a dive's start for a save at full stretch, seconds (0.025, from reference data).</summary>
    public float SaveTimeJitterHard { get; set; } = 0.025f;
    /// <summary>A ball at the feet is held as a standing catch of a ball this much faster would be: its speed is divided by it.</summary>
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
    /// <summary>...with the keeper within this distance of his line is tipped over the bar, metres (0.7 and 3 m, from reference data).</summary>
    public float TipOverBarDistance { get; set; } = 3.0f;
    /// <summary>Slowest a parried ball leaves the keeper, m/s (3.0, from reference data).</summary>
    public float MinParrySpeed { get; set; } = 3.0f;
    /// <summary>Slowest a blocked ball leaves the keeper, m/s (4.5, from reference data).</summary>
    public float BlockSpeed { get; set; } = 4.5f;
    /// <summary>A blocked ball keeps between these fractions of its speed.</summary>
    public float BlockSpeedMin { get; set; } = 0.25f;
    public float BlockSpeedMax { get; set; } = 0.45f;
    /// <summary>Largest angle of a block off straight back out, degrees.</summary>
    public float BlockAngleDeg { get; set; } = 45f;
    /// <summary>A fingertip save keeps this fraction of the ball's speed...</summary>
    public float FingertipSpeedKept { get; set; } = 0.70f;
    /// <summary>...and bends its path away from the goal by up to this much, degrees.</summary>
    public float FingertipMaxBendDeg { get; set; } = 40f;
    /// <summary>
    /// The chance a reflex touch is loose (the ball keeps going, bent a little: an uncontrolled reflex deflection) is the sum of
    /// this, by how off-centre the ball meets the limb (0 head-on, 1 a graze)...
    /// </summary>
    public Curve ReflexLooseEdge { get; set; } = new((0.2f, 0f), (1f, 1.8f));
    /// <summary>...and this, by the save's difficulty...</summary>
    public Curve ReflexLooseDifficulty { get; set; } = new((0.3f, 0f), (1f, 0.8f));
    /// <summary>...times this, by the ball's speed at the touch, m/s...</summary>
    public Curve ReflexLooseSpeed { get; set; } = new((15f, 0.5f), (25f, 1f), (30f, 1.3f));
    /// <summary>...and this, by the mean of handling and reflexes.</summary>
    public RatingRange ReflexLooseRating { get; set; } = new(1.5f, 0.7f);
    /// <summary>A loose reflex touch keeps between these fractions of the ball's speed...</summary>
    public float ReflexLooseSpeedMin { get; set; } = 0.65f;
    public float ReflexLooseSpeedMax { get; set; } = 0.9f;
    /// <summary>...and bends its path away from the limb by a half to the whole of this for a head-on touch, degrees...</summary>
    public float ReflexLooseMaxBendDeg { get; set; } = 40f;
    /// <summary>...and of this fraction of it for a graze.</summary>
    public float ReflexLooseGrazeBend { get; set; } = 0.5f;
    /// <summary>A dive that has not met the ball ends (the keeper lands) this long after its reach stops growing, seconds...</summary>
    public float DiveLandSeconds { get; set; } = 0.15f;
    /// <summary>...and no later than this after the reaction, seconds. A standing save lasts at least <see cref="StandSaveSeconds"/>.</summary>
    public float DiveMaxSeconds { get; set; } = 0.8f;
    public float StandSaveSeconds { get; set; } = 0.35f;

    // ---- recovery ----
    /// <summary>Lying after a dive, smother or block before getting up, seconds.</summary>
    public float GroundedSeconds { get; set; } = 0.3f;
    /// <summary>Getting up takes this long, seconds (1.0, from reference data).</summary>
    public float GetUpSeconds { get; set; } = 1.0f;
    /// <summary>A new save can start this long into getting up, seconds (0.2, from reference data).</summary>
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
    /// <summary>
    /// Cancel pressed any time from the shoot press until this long before the shot's planned strike turns it into a fake shot,
    /// seconds.
    /// </summary>
    public float FakeShotCutoff { get; set; } = 0.05f;
    /// <summary>The drag-back's left stick: at least this far from forward (degrees), with both shoulder buttons held.</summary>
    public float DragBackStickDeg { get; set; } = 120f;
    /// <summary>A drag-back asked for within this long of the touch controller's own turn at the feet starting replaces it, seconds.</summary>
    public float DragBackOverFeetTurn { get; set; } = 0.1f;
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
    /// seconds; the ball is free until then. (The turn at the feet has its own, <see cref="FeetTurnReachMax"/>.)
    /// </summary>
    public float SoleReachMax { get; set; } = 0.12f;
    /// <summary>The sole lands with the ball centre within this of the foot's contact point, metres.</summary>
    public float SoleReachTolerance { get; set; } = 0.3f;
    /// <summary>The largest steering velocity the reach may add to bring the foot to the ball, m/s.</summary>
    public float SoleReachSteer { get; set; } = 2.5f;
    /// <summary>A sole move's reach lasts at least as long as the foot needs at this speed from up to half a stride off, m/s (0: no floor).</summary>
    public float SoleReachFootSpeed { get; set; } = 8f;

    // ---- the moves ----
    public float StepOverSeconds { get; set; } = 0.42f;
    /// <summary>The step-over over a still ball (slower than <see cref="StillBallSpeed"/>).</summary>
    public float StepOverStillSeconds { get; set; } = 0.35f;
    /// <summary>A ball slower than this counts as still, m/s.</summary>
    public float StillBallSpeed { get; set; } = 0.1f;
    /// <summary>
    /// Step-over and body feint: the player eases to the ball's pace so it ends this far ahead of his foot (the contact zone's
    /// lead ahead of him).
    /// </summary>
    public float PaceMatchAhead { get; set; } = 0.25f;
    public float BodyFeintSeconds { get; set; } = 0.35f;
    /// <summary>
    /// How much of the run version of the step-over and body feint (the run kept, the ball rolling on with him) a move entered at
    /// this running speed takes, m/s to 0..1; the rest is the standing version (paced to the ball).
    /// </summary>
    public Curve StepOverRunBlend { get; set; } = new((1f, 0f), (2.5f, 1f));
    /// <summary>
    /// At a run the step-over's and body feint's running speed eases down from the move's start, no faster than
    /// step_over_run_brake, to this share of the entry running speed, by entry speed m/s.
    /// </summary>
    public Curve StepOverRunKeep { get; set; } = new((4f, 0.9f), (7.5f, 0.82f));
    /// <summary>The run step-over's and body feint's running speed eases down no faster than this, m/s^2.</summary>
    public float StepOverRunBrake { get; set; } = 6f;
    /// <summary>At a run the step-over and body feint turn the run toward the left stick by at most this off the move's line, degrees.</summary>
    public float StepOverRunTurnDeg { get; set; } = 45f;
    /// <summary>The ball roll from standing when it is the first move of a chain (the sole on the ball, the reach before it apart).</summary>
    public float BallRollFirstSeconds { get; set; } = 0.4f;
    /// <summary>The same entered at a run (see <see cref="BallRollRunBlend"/>).</summary>
    public float BallRollRunSeconds { get; set; } = 0.3f;
    /// <summary>The ball roll later in a chain.</summary>
    public float BallRollChainSeconds { get; set; } = 0.27f;
    /// <summary>
    /// How much of the run version of the ball roll (its time, its run kept, its step with the ball) a roll entered at this
    /// running speed takes, m/s to 0..1; the rest is the standing version.
    /// </summary>
    public Curve BallRollRunBlend { get; set; } = new((1f, 0f), (2.5f, 1f));
    /// <summary>
    /// During a ball roll the running speed eases down from the move's start (the reach included), no faster than
    /// ball_roll_brake, to this share of the entry running speed, by entry speed m/s.
    /// </summary>
    public Curve BallRollKeep { get; set; } = new((4f, 0.85f), (7.5f, 0.75f));
    /// <summary>The ball roll's running speed eases down no faster than this, m/s^2.</summary>
    public float BallRollBrake { get; set; } = 6f;
    /// <summary>How far the ball is rolled sideways against the player's speed, m/s to metres.</summary>
    public Curve BallRollDistance { get; set; } = new((0f, 0.6f), (4f, 1.0f));
    /// <summary>
    /// From standing the player moves sideways this fraction of the roll distance with the ball; at a run his step is planned so
    /// the ball ends ahead of him along his travel (see SkillMoves).
    /// </summary>
    public float BallRollFollow { get; set; } = 0.5f;
    public float DragBackSeconds { get; set; } = 0.4f;
    /// <summary>
    /// No move starts until this long after a drag-back ends, seconds at the top rating (see <see cref="CooldownScale"/>). The
    /// touch controller's turn at the feet waits for it too.
    /// </summary>
    public float DragBackCooldown { get; set; } = 0.2f;
    /// <summary>How far the sole pulls the ball back.</summary>
    public float DragBackDistance { get; set; } = 0.65f;
    /// <summary>How far the player steps back (he ends standing).</summary>
    public float DragBackStepBack { get; set; } = 0.3f;
    /// <summary>
    /// The turn at the feet (the touch controller's sole turn of a reversed stick, built on the drag-back's motion): its
    /// duration, the cooldown after it, how far the sole pulls the ball onto the stick's line, the player's step that way, and
    /// the longest reach before the sole lands, seconds and metres.
    /// </summary>
    public float FeetTurnSeconds { get; set; } = 0.25f;
    public float FeetTurnCooldown { get; set; } = 0.2f;
    public float FeetTurnDistance { get; set; } = 1.0f;
    public float FeetTurnStepBack { get; set; } = 0.3f;
    public float FeetTurnReachMax { get; set; } = 0.1f;
    /// <summary>
    /// A fake shot's contact (the fake stop's sole landing, the fake cut's hook) comes on the tick the shot's strike was planned
    /// for when the cancel was read, or this long after the cancel if that is later, seconds: until then the shot winds up as a
    /// real one (the cancel is not seen before the contact).
    /// </summary>
    public float FakeContactAfterCancel { get; set; } = 0.1f;
    /// <summary>The fake cut, from its contact to its end.</summary>
    public float FakeSeconds { get; set; } = 0.1f;
    /// <summary>The fake stop's sole on the ball, from its landing to its end.</summary>
    public float FakeStopSeconds { get; set; } = 0.15f;
    /// <summary>The fake stop leaves the ball at rest this far ahead of where the player stops.</summary>
    public float FakeStopAhead { get; set; } = 0.15f;
    /// <summary>The fake cut pushes the ball this far along the left stick ...</summary>
    public float FakeCutDistance { get; set; } = 1.2f;
    /// <summary>... in this long.</summary>
    public float FakeCutTime { get; set; } = 0.5f;
    /// <summary>
    /// Elastico: the reach for the ball (at most, seconds); from the first contact the outside's push away from the exit side
    /// (this long, this far), the inside's contact that plays the ball on as the exit touch (this long after the first) and the
    /// move's end. The running speed eases down to at most elastico_forward_speed (within balance_deceleration).
    /// </summary>
    public float ElasticoReachMax { get; set; } = 0.10f;
    public float ElasticoPushSeconds { get; set; } = 0.11f;
    public float ElasticoPushDistance { get; set; } = 0.30f;
    public float ElasticoContact2Seconds { get; set; } = 0.22f;
    public float ElasticoSeconds { get; set; } = 0.26f;
    public float ElasticoForwardSpeed { get; set; } = 2.0f;
    /// <summary>
    /// How much of the run version of the elastico a move entered at this running speed takes, m/s to 0..1: at a run the running
    /// speed eases from the entry pace no faster than elastico_run_brake to elastico_run_keep of it instead of down to
    /// elastico_forward_speed.
    /// </summary>
    public Curve ElasticoRunBlend { get; set; } = new((1f, 0f), (2.5f, 1f));
    public float ElasticoRunKeep { get; set; } = 0.8f;
    public float ElasticoRunBrake { get; set; } = 6f;
    /// <summary>
    /// Spin: the reach (at most, seconds); from the first sole's landing the move's length, the second sole's contact and the
    /// first sole's lift (seconds after the landing); the player's displacement forward and toward the side from where the ball
    /// was first taken, metres; the running speed eased down to at most spin_forward_speed; the ball kept within
    /// spin_ball_radius of him.
    /// </summary>
    public float SpinReachMax { get; set; } = 0.12f;
    public float SpinSeconds { get; set; } = 0.60f;
    public float SpinContact2Seconds { get; set; } = 0.30f;
    public float SpinLiftSeconds { get; set; } = 0.25f;
    public float SpinForward { get; set; } = 0.6f;
    public float SpinSide { get; set; } = 0.5f;
    public float SpinForwardSpeed { get; set; } = 2.0f;
    /// <summary>
    /// How much of the run version of the spin a move entered at this running speed takes, m/s to 0..1: at a run his pace along
    /// the run eases from the entry pace no faster than spin_run_brake to spin_run_keep of it (he travels on through the turn)
    /// instead of down to spin_forward_speed.
    /// </summary>
    public Curve SpinRunBlend { get; set; } = new((1f, 0f), (2.5f, 1f));
    public float SpinRunKeep { get; set; } = 0.7f;
    public float SpinRunBrake { get; set; } = 6f;
    public float SpinBallRadius { get; set; } = 0.35f;
    /// <summary>
    /// McGeady spin (a spin's variant, see SkillMoves): the reach (at most, seconds); from the first sole's landing the move's
    /// length (longer at a run, see mcgeady_run_brake), the other foot's contact and the first foot's lift (seconds, stretched
    /// with the move); how far the body turns, away from the side he comes
    /// out to (degrees: 270 leaves him facing that side); how far toward that side of where the ball was first taken he ends at
    /// least (metres); the running speed his run eases down to through the reach from standing (m/s); and the pace he comes out
    /// with toward the side from standing (m/s).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("mcgeady_reach_max")]
    public float McGeadyReachMax { get; set; } = 0.12f;
    [System.Text.Json.Serialization.JsonPropertyName("mcgeady_seconds")]
    public float McGeadySeconds { get; set; } = 0.55f;
    [System.Text.Json.Serialization.JsonPropertyName("mcgeady_contact2_seconds")]
    public float McGeadyContact2Seconds { get; set; } = 0.32f;
    [System.Text.Json.Serialization.JsonPropertyName("mcgeady_lift_seconds")]
    public float McGeadyLiftSeconds { get; set; } = 0.22f;
    [System.Text.Json.Serialization.JsonPropertyName("mcgeady_turn_deg")]
    public float McGeadyTurnDeg { get; set; } = 270f;
    [System.Text.Json.Serialization.JsonPropertyName("mcgeady_side")]
    public float McGeadySide { get; set; } = 0.6f;
    [System.Text.Json.Serialization.JsonPropertyName("mcgeady_forward_speed")]
    public float McGeadyForwardSpeed { get; set; } = 1.5f;
    [System.Text.Json.Serialization.JsonPropertyName("mcgeady_exit_speed")]
    public float McGeadyExitSpeed { get; set; } = 2.5f;
    /// <summary>
    /// How much of the run version of the McGeady spin a move entered at this running speed takes, m/s to 0..1: at a run he
    /// comes out toward the side at mcgeady_run_keep of the entry pace instead of mcgeady_exit_speed. His pace along the old
    /// run is shed over the turn no faster than mcgeady_run_brake, m/s squared: a move entered faster than mcgeady_seconds
    /// times that lasts as much longer as it takes.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("mcgeady_run_blend")]
    public Curve McGeadyRunBlend { get; set; } = new((1f, 0f), (2.5f, 1f));
    [System.Text.Json.Serialization.JsonPropertyName("mcgeady_run_keep")]
    public float McGeadyRunKeep { get; set; } = 0.55f;
    [System.Text.Json.Serialization.JsonPropertyName("mcgeady_run_brake")]
    public float McGeadyRunBrake { get; set; } = 12f;
    /// <summary>
    /// Heel-to-heel: the move's length, the heel's contact and the trailing foot's flick (seconds from the start), how far the
    /// heel knocks the ball back, and the flick's speed: this times the player's running speed, at least the minimum, never
    /// faster than he can catch.
    /// </summary>
    public float HeelToHeelSeconds { get; set; } = 0.35f;
    public float HeelToHeelContactSeconds { get; set; } = 0.12f;
    public float HeelToHeelContact2Seconds { get; set; } = 0.22f;
    public float HeelToHeelBack { get; set; } = 0.15f;
    public float HeelToHeelFlickRatio { get; set; } = 1.3f;
    public float HeelToHeelFlickMinSpeed { get; set; } = 5.0f;
    /// <summary>
    /// How much of the run version of the heel-to-heel flick a move entered at this running speed takes, m/s to 0..1; the rest
    /// is the standing flick (heel_to_heel_flick_ratio, at least heel_to_heel_flick_min_speed).
    /// </summary>
    public Curve HeelToHeelRunBlend { get; set; } = new((1f, 0f), (2.5f, 1f));
    /// <summary>
    /// At a run the flick is a short one he runs onto: paced so that, with him keeping his pace, the ball is this far ahead of
    /// him 0.2 s after the move's end, metres (where the next dribble touch meets it).
    /// </summary>
    public float HeelToHeelRunFlickAhead { get; set; } = 0.45f;

    // ---- cooldowns ----
    /// <summary>
    /// Each move's cooldown: no move starts until this long after one of this kind ends, seconds at the top rating (times
    /// <see cref="CooldownScale"/>). A gesture made in a cooldown waits for it as it waits for the ball (meet_wait_seconds). The
    /// drag-back's is <see cref="DragBackCooldown"/>; the turn at the feet has its own fixed <see cref="FeetTurnCooldown"/>.
    /// </summary>
    public float StepOverCooldown { get; set; } = 0.10f;
    public float BodyFeintCooldown { get; set; } = 0.10f;
    /// <summary>(Short: ball rolls are made to be chained, see <see cref="BallRollChainSeconds"/>.)</summary>
    public float BallRollCooldown { get; set; } = 0.05f;
    public float FakeStopCooldown { get; set; } = 0.15f;
    public float FakeCutCooldown { get; set; } = 0.15f;
    public float ElasticoCooldown { get; set; } = 0.20f;
    public float HeelToHeelCooldown { get; set; } = 0.20f;
    public float SpinCooldown { get; set; } = 0.25f;
    [System.Text.Json.Serialization.JsonPropertyName("mcgeady_cooldown")]
    public float McGeadyCooldown { get; set; } = 0.25f;
    /// <summary>A move's cooldown is multiplied by this for the effective skill_moves it was played with (a chained move's is lower).</summary>
    public RatingRange CooldownScale { get; set; } = new(1.5f, 1f);

    // ---- chain and exit burst ----
    /// <summary>A move starting within this long of the previous one's end is chained.</summary>
    public float ChainWindow { get; set; } = 2.0f;
    /// <summary>Each chained move uses this times the previous move's effective skill.</summary>
    public float ChainMultiplier { get; set; } = 0.8f;
    /// <summary>
    /// After a move the acceleration (its cap and its soft landing) is multiplied by (1 + this * the exit-angle scale) for the burst duration (effective
    /// skill_moves).
    /// </summary>
    public RatingRange Burst { get; set; } = new(0f, 0.7f);
    /// <summary>
    /// The ball roll's own exit burst and its duration (it gives back the speed the roll's slower run costs), in place of
    /// <see cref="Burst"/> and <see cref="BurstDuration"/>.
    /// </summary>
    public RatingRange BallRollBurst { get; set; } = new(0.5f, 1.8f);
    public RatingRange BallRollBurstDuration { get; set; } = new(0.4f, 1.1f);
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
    /// <summary>
    /// The burst scale of a move that sells straight on (ball roll, drag-back, fakes, heel-to-heel, spin) against the size of the
    /// exit angle, degrees: straight on is worth what it is after a step-over or feint (0.7), a square exit the most, and turning
    /// back a little less again.
    /// </summary>
    public Curve StraightExitBurst { get; set; } = new((0f, 0.7f), (90f, 1f), (180f, 0.85f));
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
/// The defender bot: where he holds, when he presses and for how long, and when he presses the tackle buttons. Values marked
/// (ref) are read from reference data; the others are ours. Curves "by awareness" take the rating 1..99 as x.
/// </summary>
public sealed class DefenderTuning
{
    /// <summary>How far off the ball he holds, on its line to the goal centre, against the ball's distance from the goal centre, metres (ref 2.5 to 5; the distances are ours).</summary>
    public Curve HoldDistanceM { get; set; } = new((8f, 2.5f), (20f, 5f));
    /// <summary>The side offset of the hold spot, metres: drawn once per hold spell, uniform between these two, with a random sign; by awareness (ref at 1 and 50).</summary>
    public Curve HoldOffsetMinM { get; set; } = new((1f, 3f), (50f, 1.5f), (99f, 0.5f));
    public Curve HoldOffsetMaxM { get; set; } = new((1f, 4f), (50f, 2f), (99f, 1f));
    /// <summary>Holding, he sprints to his spot from further than this, metres; nearer he jockeys (ref).</summary>
    public float PassiveSprintDistanceM { get; set; } = 6f;
    /// <summary>He may press only while at least this much nearer his goal's centre than the attacker, metres.</summary>
    public float GoalSideMarginM { get; set; } = 0.5f;
    /// <summary>Seconds he holds before he may press, by awareness (ref).</summary>
    public Curve ToActiveAfterS { get; set; } = new((1f, 6f), (50f, 2f), (99f, 1f));
    /// <summary>The longest a press lasts, seconds, by awareness (ref at 50 and 99).</summary>
    public Curve ActiveMaxS { get; set; } = new((1f, 3f), (50f, 5f), (99f, 8f));
    /// <summary>Beaten: the attacker is this much nearer the goal centre than he is, metres (ref).</summary>
    public float OvershootM { get; set; } = 0.4f;
    /// <summary>Pressing, he runs at where the attacker will be this many seconds on (ref).</summary>
    public float ExtrapolateS { get; set; } = 0.5f;
    /// <summary>Pressing, he sprints while further than this from that point, metres; nearer he runs.</summary>
    public float PressSprintDistanceM { get; set; } = 4f;
    /// <summary>Pressing, with the attacker within this he jockeys in, metres, with this much of the sprint input (0 the jockey pace, 1 the sprint-jockey pace).</summary>
    public float PressJockeyDistanceM { get; set; } = 3f;
    public float PressJockeySprint { get; set; } = 0.75f;
    /// <summary>He lunges or slides only with the attacker within this, metres (ref).</summary>
    public float TackleMaxDistanceM { get; set; } = 1.5f;
    /// <summary>... and the ball between them: the direction attacker to ball within this of the direction ball to defender, degrees.</summary>
    public float TackleMaxAngleDeg { get; set; } = 30f;
    /// <summary>Inside the tackle distance, running straight on (facing dot move above this) he bends in from the side ... (ref)</summary>
    public float BendDot { get; set; } = 0.7f;
    /// <summary>... by a share of sideways movement of base + gain * (1 - distance / tackle distance) (ref).</summary>
    public float BendWeightBase { get; set; } = 0.4f;
    public float BendWeightGain { get; set; } = 0.6f;
    /// <summary>After a body contact he steers at least this far off the line to the attacker, degrees, for this long, seconds (ref 20 ticks at 80 Hz).</summary>
    public float BumpSteerDeg { get; set; } = 60f;
    public float BumpSteerS { get; set; } = 0.25f;
    /// <summary>He holds a tackle button this long, seconds.</summary>
    public float TackleHoldS { get; set; } = 0.1f;
    /// <summary>The chance, drawn once per press, that his tackle in it is a slide; by slide_tackle.</summary>
    public RatingRange SlidePreference { get; set; } = new(0f, 0.35f);
    /// <summary>The chance he barges an attacker running alongside, drawn every barge_roll_s; by strength.</summary>
    public RatingRange BargePreference { get; set; } = new(0f, 0.3f);
    public float BargeRollS { get; set; } = 0.25f;
    /// <summary>The keeper neither charges nor rushes while the defender is within this half-angle of his line to the ball, degrees.</summary>
    public float KeeperYieldConeDeg { get; set; } = 25f;
    /// <summary>He controls a loose ball (the round ends DISPOSSESSED) once it has been within this of his feet, metres ...</summary>
    public float ControlDistanceM { get; set; } = 0.6f;
    /// <summary>... no faster than this against his own velocity, m/s ...</summary>
    public float ControlSpeed { get; set; } = 3f;
    /// <summary>... for this long, seconds.</summary>
    public float ControlS { get; set; } = 0.15f;
}

/// <summary>The auto tackle: a dribbled ball that comes within an outfield player's reach is taken without a button.</summary>
public sealed class AutoTackleTuning
{
    /// <summary>A dribbled ball within this of him, metres, and below max_height_m is always taken (ref).</summary>
    public float GuaranteeM { get; set; } = 0.45f;
    public float MaxHeightM { get; set; } = 1.0f;
    /// <summary>The reach against his speed, m/s, running and jockeying, metres ...</summary>
    public Curve RadiusWalkM { get; set; } = new((0f, 1.2f), (4f, 0.8f));
    public Curve RadiusJockeyM { get; set; } = new((0f, 1.4f), (4f, 1.0f));
    /// <summary>... times this, by stand_tackle (ref).</summary>
    public RatingRange RadiusFactor { get; set; } = new(0.25f, 1f);
    /// <summary>The widest angle off his facing the ball may lie at, degrees, against his speed as a share of the mode's top speed.</summary>
    public Curve AngleDeg { get; set; } = new((0f, 90f), (1f, 45f));
    /// <summary>Shielding: no tackle while the dribbler's body is within shield_m of the ball and within lane_half_m of the line from the tackler to it (ref).</summary>
    public float ShieldM { get; set; } = 1.0f;
    public float LaneHalfM { get; set; } = 0.35f;
    /// <summary>The ball is poked away at a speed drawn between these, m/s, along the freest direction within poke_spread_deg of his facing.</summary>
    public float PokeSpeedMin { get; set; } = 3f;
    public float PokeSpeedMax { get; set; } = 5f;
    public float PokeSpreadDeg { get; set; } = 45f;
    /// <summary>Seconds between auto tackles, and after any touch of the ball by the tackler.</summary>
    public float CooldownS { get; set; } = 1f;
}

/// <summary>The standing tackle (a lunge at the ball).</summary>
public sealed class StandTackleTuning
{
    /// <summary>The lunge's reach by stand_tackle, metres (ref) ...</summary>
    public RatingRange ReachM { get; set; } = new(0.9f, 1.5f);
    /// <summary>... times this, against his speed, m/s ...</summary>
    public Curve ReachSpeedFactor { get; set; } = new((0f, 0.8f), (7f, 1.2f));
    /// <summary>... and times off_run_factor when the ball lies further than off_run_deg off his run.</summary>
    public float OffRunDeg { get; set; } = 60f;
    public float OffRunFactor { get; set; } = 0.5f;
    /// <summary>The foot can meet the ball from exec_min_s to exec_max_s after the press: the first meeting with the predicted ball in that window is the one he lunges for.</summary>
    public float ExecMinS { get; set; } = 0.15f;
    public float ExecMaxS { get; set; } = 0.45f;
    /// <summary>The foot's radius in the ball's path, metres (ref); a ball within certain_m of it and below certain_height_m is won outright (ref).</summary>
    public float FootRadiusM { get; set; } = 0.20f;
    public float CertainM { get; set; } = 0.30f;
    public float CertainHeightM { get; set; } = 0.5f;
    /// <summary>The foot stays out, able to win the ball (or trip the opponent), this long after its contact tick, seconds.</summary>
    public float ActiveS { get; set; } = 0.1f;
    /// <summary>Seconds after a lunge before the next tackle (ref).</summary>
    public float CooldownS { get; set; } = 1.0f;
    /// <summary>A lunge that misses stuns him this long, seconds, against his speed at the press, m/s.</summary>
    public Curve MissStunS { get; set; } = new((0f, 0.3f), (7f, 0.8f));
}

/// <summary>The slide tackle.</summary>
public sealed class SlideTackleTuning
{
    /// <summary>How far the slide carries his body, metres, by slide_tackle ...</summary>
    public RatingRange ReachM { get; set; } = new(2.0f, 3.5f);
    /// <summary>... with the leading foot this far ahead of it (ref).</summary>
    public float LungeM { get; set; } = 0.8f;
    /// <summary>The foot wins a ball within this of it, and fouls an opponent whose centre is within opponent_radius_m of it, metres (ref).</summary>
    public float BallRadiusM { get; set; } = 0.15f;
    public float OpponentRadiusM { get; set; } = 0.30f;
    /// <summary>A slide starts at no less than this speed, m/s.</summary>
    public float MinSpeed { get; set; } = 5f;
    /// <summary>Seconds on the ground after a slide that won the ball, missed, or hit the opponent (ref) ...</summary>
    public float RecoveryWonS { get; set; } = 0.6f;
    public float RecoveryMissedS { get; set; } = 0.8f;
    public float RecoveryHitS { get; set; } = 0.4f;
    /// <summary>... then this long getting up.</summary>
    public float GetupS { get; set; } = 0.5f;
}

/// <summary>The shoulder barge.</summary>
public sealed class BargeTuning
{
    /// <summary>A barge lands on an opponent within this, metres ...</summary>
    public float DistanceM { get; set; } = 1.2f;
    /// <summary>... whose bearing off the barger's facing is between these, degrees (alongside) ...</summary>
    public float BearingMinDeg { get; set; } = 80f;
    public float BearingMaxDeg { get; set; } = 135f;
    /// <summary>... the two facing within this of each other, degrees (ref).</summary>
    public float FacingDeg { get; set; } = 90f;
    /// <summary>The sideways push, m/s, times the strength ratio and the weight ratio (the stronger, heavier one pushes; the other stumbles).</summary>
    public float Impulse { get; set; } = 1.5f;
    /// <summary>A push dies away at this, m/s squared.</summary>
    public float PushDecel { get; set; } = 6f;
    /// <summary>The lean lasts this long, seconds, and the next barge waits cooldown_s.</summary>
    public float LeanS { get; set; } = 0.35f;
    public float CooldownS { get; set; } = 1.0f;
}

/// <summary>Blocking a shot with the body and a leg.</summary>
public sealed class BlockTuning
{
    /// <summary>He sets for a shot winding up with the ball within this of him, metres, in front of him.</summary>
    public float SetRangeM { get; set; } = 4f;
    /// <summary>Set, his leg reach grows from the body radius to reach_m over this long, seconds, by blocking.</summary>
    public RatingRange GrowS { get; set; } = new(0.6f, 0.25f);
    public float ReachM { get; set; } = 0.9f;
    /// <summary>The leg blocks a ball below this, metres (ref); the body covers the rest.</summary>
    public float MaxHeightM { get; set; } = 0.75f;
    /// <summary>A deflected ball keeps this share of its speed, against the speed it arrived at, m/s ...</summary>
    public Curve ExitSpeed { get; set; } = new((5f, 0.6f), (30f, 0.35f));
    /// <summary>... and leaves no steeper than this, degrees (ref).</summary>
    public float LiftDeg { get; set; } = 40f;
}

/// <summary>
/// The defending actions of any outfield player: the body, jockeying, the auto tackle, the lunge, the slide, the barge, the
/// block, and what being tackled does. Values marked (ref) are read from reference data; the others are ours.
/// </summary>
public sealed class DefendingTuning
{
    /// <summary>An outfield player's body: a disc of this radius against other outfield players, and a capsule of it, body_height_m tall, in a struck ball's path (ref).</summary>
    public float BodyRadiusM { get; set; } = 0.30f;
    public float BodyHeightM { get; set; } = 1.8f;
    /// <summary>Jockeying pace, m/s, by jockeying, and with sprint held (ref).</summary>
    public RatingRange JockeySpeed { get; set; } = new(3.0f, 5.2f);
    public RatingRange SprintJockeySpeed { get; set; } = new(6.0f, 7.0f);
    /// <summary>Jockeying, he turns to face the ball carrier at this, degrees per second, and slows at jockey_decel, m/s squared (ref).</summary>
    public float JockeyTurnDeg { get; set; } = 540f;
    public float JockeyDecel { get; set; } = 5f;
    /// <summary>
    /// Running without the ball, his heading turns no faster than this sideways acceleration, m/s squared, over his speed (by
    /// agility); below run_turn_min_speed, m/s, the limit is that of this speed.
    /// </summary>
    public RatingRange RunTurnAccel { get; set; } = new(5f, 9f);
    public float RunTurnMinSpeed { get; set; } = 2f;
    /// <summary>Tackled or barged, a player stumbles this long, seconds, by balance: no control of the ball, his speed capped at stumble_speed, m/s.</summary>
    public RatingRange StumbleS { get; set; } = new(0.9f, 0.3f);
    public float StumbleSpeed { get; set; } = 2f;
    /// <summary>A tackled player cannot touch the ball for this long, seconds (ref).</summary>
    public float TackledLockS { get; set; } = 0.5f;
    public AutoTackleTuning Auto { get; set; } = new();
    public StandTackleTuning Stand { get; set; } = new();
    public SlideTackleTuning Slide { get; set; } = new();
    public BargeTuning Barge { get; set; } = new();
    public BlockTuning Block { get; set; } = new();
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
    public LockOnTuning LockOn { get; set; } = new();
    /// <summary>Passes (the team hash).</summary>
    public PassTuning Pass { get; set; } = new();
    /// <summary>Who the team's intent drives (the team hash).</summary>
    public ControlTuning Control { get; set; } = new();
    /// <summary>The team-mate's bot (the team hash).</summary>
    public MateTuning Mate { get; set; } = new();
    /// <summary>Receiving a pass (the team hash).</summary>
    public ReceiveTuning Receive { get; set; } = new();
    public ShotTuning Shot { get; set; } = new();
    public KeeperTuning Keeper { get; set; } = new();
    public RoundTuning Round { get; set; } = new();
    public SkillTuning Skill { get; set; } = new();
    /// <summary>The defender bot (see DefenderBot).</summary>
    public DefenderTuning Defender { get; set; } = new();
    /// <summary>The defending actions of any outfield player (see Defending).</summary>
    public DefendingTuning Defending { get; set; } = new();

    /// <summary>Seconds per tick.</summary>
    public float Dt => 1f / TickRate;
}

/// <summary>
/// The far-post rule's distance band per shot type: [min, max] metres from the ball to the goal centre, a negative end meaning
/// no limit on that side.
/// </summary>
public sealed class FarPostBands
{
    public float[] Power { get; set; } = { -1f, 50f };
    public float[] Driven { get; set; } = { -1f, 50f };
    public float[] Chip { get; set; } = { -1f, 2.5f };
    public float[] Finesse { get; set; } = { -1f, -1f };
    public float[] LowFinesse { get; set; } = { -1f, -1f };

    /// <summary>The band for a shot type.</summary>
    public (float Min, float Max) For(ShotType type)
    {
        var b = type switch
        {
            ShotType.Driven => Driven,
            ShotType.Chip => Chip,
            ShotType.Finesse => Finesse,
            ShotType.LowFinesse => LowFinesse,
            _ => Power,
        };
        return (b[0], b[1]);
    }

    /// <summary>Null when every band is a [min, max] pair of finite numbers; otherwise the field at fault and what is wrong.</summary>
    public string? Validate()
    {
        foreach (var (name, b) in new[] { ("power", Power), ("driven", Driven), ("chip", Chip), ("finesse", Finesse), ("low_finesse", LowFinesse) })
        {
            if (b == null || b.Length != 2) return $"{name}: must be [min, max]";
            if (!float.IsFinite(b[0]) || !float.IsFinite(b[1])) return $"{name}: must be finite numbers";
            if (b[0] >= 0f && b[1] >= 0f && b[0] > b[1]) return $"{name}: min is above max";
        }
        return null;
    }
}
