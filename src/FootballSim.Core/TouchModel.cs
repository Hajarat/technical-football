using System.Numerics;

namespace FootballSim.Core;

/// <summary>How the player is moving the ball, which sets the steps per touch.</summary>
public enum TouchMode { CloseControl, Jog, Sprint }

/// <summary>
/// Touch rules shared by the planner: foot reach for a loose ball, touch mode, the rated speed and direction errors, the weak
/// foot, first-touch numbers, and the stop (trap) arithmetic. The planned touches themselves live in <see cref="TouchPlanner"/>.
/// </summary>
public static class TouchModel
{
    /// <summary>Foot reach for a loose ball, a soft touch and a strike, metres: 0.55 + 0.30 * (height - 1.60) / 0.40.</summary>
    public static float FootReach(PlayerSheet s, Tuning t)
    {
        var k = t.Touch;
        return k.ReachBase + k.ReachPerHeight * (s.HeightM - k.HeightMin) / k.HeightSpan;
    }

    /// <summary>
    /// The touch mode for an intent: close control wins, then sprint; a stick pushed no further than walk_max_input asks for
    /// walking touches, which are played like close control (a touch on every step, short pushes; the reference data's walking touch
    /// is below an input of 0.7); else jog.
    /// </summary>
    public static TouchMode ModeFor(in TickIntent intent, Tuning t) =>
        intent.CloseControl ? TouchMode.CloseControl
        : intent.Sprint >= t.Touch.SprintThreshold ? TouchMode.Sprint
        : Walking(intent, t) ? TouchMode.CloseControl
        : TouchMode.Jog;

    /// <summary>True when the stick is pushed (past the stick threshold) but no further than walk_max_input.</summary>
    public static bool Walking(in TickIntent intent, Tuning t)
    {
        float mag = intent.Move.Length();
        return mag >= t.Touch.StickThreshold && mag <= t.Touch.WalkMaxInput;
    }

    /// <summary>
    /// Steps of the stride clock between two dribble touches in a mode, at least 1: the close style's and the speed (sprint)
    /// style's by close dribbling and the speed (sprint) style's by sprint dribbling, each on its own range, the sprint's
    /// times composure_sprint_steps by composure before the rounding; a jog's the same for every player.
    /// </summary>
    public static int StepsPerTouch(PlayerSheet s, TouchMode mode, Tuning t)
    {
        var k = t.Touch.StepsPerTouch;
        float v = mode switch
        {
            TouchMode.CloseControl => k.Close.ForRating(s.CloseDribbling),
            TouchMode.Jog => k.Jog,
            _ => k.Sprint.ForRating(s.SprintDribbling) * t.Touch.ComposureSprintSteps.ForRating(s.Composure),
        };
        return Math.Max(1, (int)MathF.Round(v, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// The trajectory and contact profile of a touch: a knock-on, a heavy touch and each trap their own; every other touch (a
    /// dribble touch, a turn, cut, stop, soft or first touch) the profile of the mode it is played in.
    /// </summary>
    public static TouchProfile Profile(ActionKind kind, TouchMode mode, Tuning t)
    {
        var k = t.Touch.Profiles;
        return kind switch
        {
            ActionKind.KnockOn => k.KnockOn,
            ActionKind.Heavy => k.Heavy,
            ActionKind.TrapLow => k.TrapLow,
            ActionKind.TrapMid => k.TrapMid,
            ActionKind.TrapHigh => k.TrapHigh,
            _ => mode switch { TouchMode.CloseControl => k.Close, TouchMode.Jog => k.Jog, _ => k.Sprint },
        };
    }

    /// <summary>
    /// The dribbling rating a touch is rated by (its launch angle, touch boost and speed error): sprint dribbling for a touch
    /// with the sprint or the knock-on profile, close dribbling for every other (see <see cref="Profile"/>).
    /// </summary>
    public static int DribblingFor(PlayerSheet s, ActionKind kind, TouchMode mode) => kind switch
    {
        ActionKind.KnockOn => s.SprintDribbling,
        ActionKind.Heavy or ActionKind.TrapLow or ActionKind.TrapMid or ActionKind.TrapHigh => s.CloseDribbling,
        _ => mode == TouchMode.Sprint ? s.SprintDribbling : s.CloseDribbling,
    };

    /// <summary>Rated launch angle of a touch, degrees: its profile's launch angle by its dribbling rating (before any flattening).</summary>
    public static float LiftDeg(PlayerSheet s, ActionKind kind, TouchMode mode, Tuning t) =>
        Profile(kind, mode, t).LaunchAngle.ForRating(DribblingFor(s, kind, mode));

    /// <summary>
    /// Touch boost factor: 1 + touch_boost (the touch's dribbling rating, see <see cref="DribblingFor"/>) + the turn extra,
    /// linear in the turn angle up to its full angle.
    /// </summary>
    public static float TouchBoostFactor(int dribbling, float turnDeg, Tuning t)
    {
        var k = t.Touch;
        float ramp = k.TouchBoostTurnFullDeg > 0f ? Math.Clamp(turnDeg / k.TouchBoostTurnFullDeg, 0f, 1f) : 1f;
        return 1f + k.TouchBoost.ForRating(dribbling) + k.TouchBoostTurnExtra * ramp;
    }

    /// <summary>Seconds after a knock-on at which the chasing player is planned to meet the ball (sprint dribbling).</summary>
    public static float KnockOnCatchTime(PlayerSheet s, Tuning t) => t.Touch.KnockOnCatchTime.ForRating(s.SprintDribbling);

    /// <summary>Error scale for a speed fraction: base + slope * fraction (0.4 + 0.6 * f).</summary>
    public static float ErrorSpeedScale(float speedFrac, Tuning t) =>
        t.Touch.ErrorSpeedBase + t.Touch.ErrorSpeedSlope * Math.Clamp(speedFrac, 0f, 1f);

    /// <summary>
    /// Standard deviation of the touch speed factor: lerp(0.10, 0.02) by the touch's dribbling rating (see
    /// <see cref="DribblingFor"/>) times the speed scale.
    /// </summary>
    public static float SpeedSigma(int dribbling, float speedFrac, Tuning t) =>
        t.Touch.SpeedError.ForRating(dribbling) * ErrorSpeedScale(speedFrac, t);

    /// <summary>Standard deviation of the touch direction, degrees: lerp(7, 1) by ball_control times the speed scale.</summary>
    public static float DirectionSigmaDeg(PlayerSheet s, float speedFrac, Tuning t) =>
        t.Touch.DirectionErrorDeg.ForRating(s.BallControl) * ErrorSpeedScale(speedFrac, t);

    /// <summary>First-contact direction error, degrees: lerp(25, 3) by first_touch times (relative speed / 15, capped at 1).</summary>
    public static float FirstTouchSigmaDeg(PlayerSheet s, float relativeSpeed, Tuning t) =>
        t.Touch.FirstTouchDirectionErrorDeg.ForRating(s.FirstTouch) * Math.Clamp(relativeSpeed / t.Touch.FirstTouchReferenceSpeed, 0f, 1f);

    /// <summary>First contact: fraction of the relative ball velocity kept as unwanted extra velocity, lerp(0.45, 0.05) by first_touch.</summary>
    public static float FirstTouchSpeedKept(PlayerSheet s, Tuning t) => t.Touch.FirstTouchSpeedKept.ForRating(s.FirstTouch);

    /// <summary>Error multiplier for a foot: 1 for the preferred foot, weak_foot_error by the weak_foot rating for the other.</summary>
    public static float FootErrorFactor(PlayerSheet s, Foot foot, Tuning t) =>
        foot == s.PreferredFoot ? 1f : MathF.Max(0f, t.Touch.WeakFootError.Evaluate(s.WeakFoot));

    /// <summary>
    /// The foot for a ball at <paramref name="ballPosition"/> from a player at <paramref name="feet"/> facing
    /// <paramref name="facing"/>: the preferred foot unless the ball is more than 0.15 m past the centreline on the other side.
    /// Facing 0 looks along +X, so the player's right is +Z.
    /// </summary>
    public static Foot ChooseFoot(PlayerSheet s, Vector3 feet, float facing, Vector3 ballPosition, Tuning t)
    {
        float lateral = Lateral(feet, facing, ballPosition);
        float off = t.Touch.FootSideOffset;
        if (s.PreferredFoot == Foot.Right) return lateral < -off ? Foot.Left : Foot.Right;
        return lateral > off ? Foot.Right : Foot.Left;
    }

    /// <summary>Signed sideways offset of a point from the player's facing line, metres (positive = the player's right).</summary>
    public static float Lateral(Vector3 feet, float facing, Vector3 point)
    {
        var right = new Vector2(-MathF.Sin(facing), MathF.Cos(facing));
        return Vector2.Dot(new Vector2(point.X - feet.X, point.Z - feet.Z), right);
    }

    /// <summary>Horizontal distance from the feet to the ball centre, metres.</summary>
    public static float HorizontalDistance(Vector3 feet, Vector3 ball) => new Vector2(ball.X - feet.X, ball.Z - feet.Z).Length();

    /// <summary>
    /// True if the ball is at the feet: in the low band (no higher than trap_low's max_ball_height) and within its share of foot
    /// reach horizontally. A touch at the feet (the soft touch, a skill move's exit touch, a strike) needs it.
    /// </summary>
    public static bool InReach(PlayerState p, Ball ball, Tuning t) => InReach(p.Sheet, p.Position, ball.Position, t);

    internal static bool InReach(PlayerSheet s, Vector3 feet, Vector3 ball, Tuning t) => InReach(s, feet, ball, t.Touch.Profiles.TrapLow, t);

    /// <summary>True if the ball is no higher than the profile's max_ball_height and within its share of foot reach horizontally.</summary>
    internal static bool InReach(PlayerSheet s, Vector3 feet, Vector3 ball, TouchProfile profile, Tuning t) =>
        ball.Y <= profile.MaxBallHeight && HorizontalDistance(feet, ball) <= FootReach(s, t) * profile.Reach;

    /// <summary>
    /// The trap a first contact makes of a ball at <paramref name="ball"/> from a player at <paramref name="feet"/>, by the height
    /// band the ball centre is in (low, mid, high: up to each trap's max_ball_height), when it is within that trap's share of foot
    /// reach; null for a ball above the high band or out of its band's reach.
    /// </summary>
    public static ActionKind? TrapKindFor(PlayerSheet s, Vector3 feet, Vector3 ball, Tuning t)
    {
        var k = t.Touch.Profiles;
        var (kind, prof) = ball.Y <= k.TrapLow.MaxBallHeight ? (ActionKind.TrapLow, k.TrapLow)
            : ball.Y <= k.TrapMid.MaxBallHeight ? (ActionKind.TrapMid, k.TrapMid)
            : (ActionKind.TrapHigh, k.TrapHigh);
        return InReach(s, feet, ball, prof, t) ? kind : null;
    }

    /// <summary>The highest ball centre a touch at the feet plays, metres (trap_low's max_ball_height).</summary>
    public static float FeetHeight(Tuning t) => t.Touch.Profiles.TrapLow.MaxBallHeight;

    internal static Vector3 Flat(Vector3 v) => new(v.X, 0f, v.Z);

    /// <summary>
    /// How far a player decelerating from its current running speed to rest under the speed time constant still travels, along
    /// its velocity: speed * time_constant.
    /// </summary>
    public static float StopDistance(PlayerState p, Tuning t) => p.RunSpeed * Movement.TimeConstant(p.Sheet, t);

    /// <summary>
    /// The rolling speed (no skid) that brings the ball to rest about 0.35 m ahead of where the player stops, along
    /// <paramref name="dir"/>, from a ball <paramref name="ballPos"/> and a player at <paramref name="feet"/>.
    /// </summary>
    internal static float StopSpeed(PlayerState p, Vector3 feet, Vector3 ballPos, Vector3 dir, Tuning t)
    {
        float lead = Vector3.Dot(Flat(ballPos - feet), dir);
        float roll = StopDistance(p, t) + t.Touch.TrapRestOffset - lead;
        return roll <= 0f ? 0f : MathF.Sqrt(2f * t.Ball.RollDeceleration * roll);
    }

    /// <summary>The direction a stop works along: the facing (the running direction).</summary>
    internal static Vector3 StopDirection(PlayerState p) => new(MathF.Cos(p.Facing), 0f, MathF.Sin(p.Facing));

    /// <summary>
    /// True while a ball in possession needs a stop touch: it would come to rest (skid included) more than the trap tolerance
    /// beyond its aim (the rest offset ahead of where the player stops), or further than that same margin behind the player's
    /// stopping point.
    /// </summary>
    internal static bool NeedsStop(PlayerState p, Ball ball, Tuning t)
    {
        Vector3 rest = ball.PredictRest(t.Dt, 10f);
        Vector3 dir = StopDirection(p);
        float rel = Vector3.Dot(Flat(rest - p.Position), dir) - StopDistance(p, t);
        float margin = t.Touch.TrapRestOffset + t.Touch.TrapTolerance;
        return rel > margin || rel < -margin;
    }

    /// <summary>Angle in degrees between a ground direction and the ball's travel (or the facing when the ball is nearly still).</summary>
    internal static float TurnAngleDeg(PlayerState p, Ball ball, Vector3 dir)
    {
        var bv = new Vector2(ball.Velocity.X, ball.Velocity.Z);
        float reference = bv.Length() > 0.3f ? MathF.Atan2(bv.Y, bv.X) : p.Facing;
        return MathF.Abs(Movement.WrapAngle(MathF.Atan2(dir.Z, dir.X) - reference)) * 180f / MathF.PI;
    }

    /// <summary>Rotates a ground vector by <paramref name="deg"/> degrees about +Y in the X-Z plane (toward +Z for positive).</summary>
    internal static Vector3 Rotate(Vector3 v, float deg)
    {
        float a = deg * MathF.PI / 180f, c = MathF.Cos(a), s = MathF.Sin(a);
        return new Vector3(v.X * c - v.Z * s, v.Y, v.X * s + v.Z * c);
    }
}
