using System.Numerics;

namespace FootballSim.Core;

/// <summary>How the player is moving the ball, which sets the touch cadence and overshoot.</summary>
public enum TouchMode { CloseControl, Jog, Sprint }

/// <summary>What a touch did.</summary>
public enum TouchKind
{
    /// <summary>An ordinary dribble touch: the ball leaves at the player's speed plus the overshoot.</summary>
    Dribble,
    /// <summary>An early touch to follow a change of direction.</summary>
    Turn,
    /// <summary>A sharp turn at speed: the ball leaves in the new direction at the player's post-turn speed.</summary>
    Cut,
    /// <summary>The stick was released: the ball is slowed so it comes to rest where the player stops.</summary>
    Trap,
    /// <summary>Shoot was pressed in possession: the ball is set to the player's speed so it stays strikable.</summary>
    Soft,
    /// <summary>First contact with a loose ball (first_touch rating). The player gains possession.</summary>
    First,
}

/// <summary>A touch as made: its kind, the foot, where the ball was, and the ball velocity it left with.</summary>
/// <param name="DirectionErrorDeg">The direction error actually drawn, degrees.</param>
public readonly record struct TouchResult(TouchKind Kind, Foot Foot, Vector3 ContactPoint, Vector3 BallVelocity, float DirectionErrorDeg);

/// <summary>
/// Touches. The ball stays a free physical object; a touch is a discrete event that sets its velocity relative to the
/// player's. In possession the ball leaves at the player's speed along the stick times (1 + a small overshoot), so touches are
/// small and frequent; off possession the first contact uses the first_touch rating. Models take a player and the ball.
/// </summary>
public static class TouchModel
{
    /// <summary>Foot reach, metres: 0.55 + 0.30 * (height - 1.60) / 0.40.</summary>
    public static float FootReach(PlayerSheet s, Tuning t)
    {
        var k = t.Touch;
        return k.ReachBase + k.ReachPerHeight * (s.HeightM - k.HeightMin) / k.HeightSpan;
    }

    /// <summary>The touch mode for an intent: close control wins, then sprint, else jog.</summary>
    public static TouchMode ModeFor(in TickIntent intent, Tuning t) =>
        intent.CloseControl ? TouchMode.CloseControl
        : intent.Sprint >= t.Touch.SprintThreshold ? TouchMode.Sprint
        : TouchMode.Jog;

    /// <summary>Minimum seconds between ordinary touches for a mode.</summary>
    public static float Cadence(TouchMode mode, Tuning t) => mode switch
    {
        TouchMode.CloseControl => t.Touch.CloseControlCadence,
        TouchMode.Jog => t.Touch.JogCadence,
        _ => t.Touch.SprintCadence,
    };

    /// <summary>Overshoot for a mode: the ball leaves at the player's speed times (1 + this). Sprint depends on dribbling.</summary>
    public static float Overshoot(PlayerSheet s, TouchMode mode, Tuning t) => mode switch
    {
        TouchMode.CloseControl => t.Touch.CloseControlOvershoot,
        TouchMode.Jog => t.Touch.JogOvershoot,
        _ => t.Touch.SprintOvershoot.ForRating(s.Dribbling),
    };

    /// <summary>Smallest ball speed a touch gives, m/s.</summary>
    public static float MinPush(TouchMode mode, Tuning t) =>
        mode == TouchMode.CloseControl ? t.Touch.CloseControlMinPush : t.Touch.MinPush;

    /// <summary>Error scale for a speed fraction: base + slope * fraction (0.4 + 0.6 * f).</summary>
    public static float ErrorSpeedScale(float speedFrac, Tuning t) =>
        t.Touch.ErrorSpeedBase + t.Touch.ErrorSpeedSlope * Math.Clamp(speedFrac, 0f, 1f);

    /// <summary>Standard deviation of the touch speed factor: lerp(0.10, 0.02) by dribbling times the speed scale.</summary>
    public static float SpeedSigma(PlayerSheet s, float speedFrac, Tuning t) =>
        t.Touch.SpeedError.ForRating(s.Dribbling) * ErrorSpeedScale(speedFrac, t);

    /// <summary>Standard deviation of the touch direction, degrees: lerp(7, 1) by ball_control times the speed scale.</summary>
    public static float DirectionSigmaDeg(PlayerSheet s, float speedFrac, Tuning t) =>
        t.Touch.DirectionErrorDeg.ForRating(s.BallControl) * ErrorSpeedScale(speedFrac, t);

    /// <summary>First-contact direction error, degrees: lerp(25, 3) by first_touch times (relative speed / 15, capped at 1).</summary>
    public static float FirstTouchSigmaDeg(PlayerSheet s, float relativeSpeed, Tuning t) =>
        t.Touch.FirstTouchDirectionErrorDeg.ForRating(s.FirstTouch) * Math.Clamp(relativeSpeed / t.Touch.FirstTouchReferenceSpeed, 0f, 1f);

    /// <summary>First contact: fraction of the relative ball velocity kept as unwanted extra velocity, lerp(0.45, 0.05) by first_touch.</summary>
    public static float FirstTouchSpeedKept(PlayerSheet s, Tuning t) => t.Touch.FirstTouchSpeedKept.ForRating(s.FirstTouch);

    /// <summary>Error multiplier for a foot: 1 for the preferred foot, (1 + 0.10 * (5 - weak_foot)) for the other.</summary>
    public static float FootErrorFactor(PlayerSheet s, Foot foot, Tuning t) =>
        foot == s.PreferredFoot ? 1f : 1f + t.Touch.WeakFootErrorStep * (5 - s.WeakFoot);

    /// <summary>
    /// The touching foot for a ball at <paramref name="ballPosition"/> from a player at <paramref name="feet"/> facing
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

    /// <summary>True if the ball is within foot reach horizontally and low enough to touch.</summary>
    public static bool InReach(PlayerState p, Ball ball, Tuning t) => InReach(p.Sheet, p.Position, ball.Position, t);

    internal static bool InReach(PlayerSheet s, Vector3 feet, Vector3 ball, Tuning t) =>
        ball.Y <= t.Touch.MaxBallHeight && HorizontalDistance(feet, ball) <= FootReach(s, t);

    static Vector3 Flat(Vector3 v) => new(v.X, 0f, v.Z);

    /// <summary>
    /// How far a player decelerating from its current speed to rest under the speed time constant still travels, along its
    /// velocity: speed * time_constant.
    /// </summary>
    public static float StopDistance(PlayerState p, Tuning t) => p.Speed * Movement.TimeConstant(p.Sheet, t);

    /// <summary>The ball speed that makes a trap bring the ball to rest about 0.35 m ahead of where the player stops.</summary>
    static float TrapSpeed(PlayerState p, Ball ball, Vector3 dir, Tuning t)
    {
        float lead = Vector3.Dot(Flat(ball.Position - p.Position), dir);
        float roll = StopDistance(p, t) + t.Touch.TrapRestOffset - lead;
        return roll <= 0f ? 0f : MathF.Sqrt(2f * t.Ball.RollDeceleration * roll);
    }

    /// <summary>The direction a trap works along: the player's velocity, or the facing when nearly still.</summary>
    static Vector3 TrapDirection(PlayerState p) =>
        p.Speed > 0.2f ? Vector3.Normalize(Flat(p.Velocity)) : new Vector3(MathF.Cos(p.Facing), 0f, MathF.Sin(p.Facing));

    /// <summary>True while a ball in possession would come to rest further than the trap tolerance from where the player stops.</summary>
    internal static bool NeedsTrap(PlayerState p, Vector3 feet, Vector3 ballPos, Vector3 ballVel, Tuning t)
    {
        var bv = Flat(ballVel);
        float bs = bv.Length();
        float ballRoll = bs * bs / (2f * t.Ball.RollDeceleration);
        Vector3 dir = TrapDirection(p);
        float ballRest = Vector3.Dot(Flat(ballPos - feet) + (bs > 1e-3f ? bv / bs * ballRoll : Vector3.Zero), dir);
        float playerRest = StopDistance(p, t) + t.Touch.TrapRestOffset;
        return MathF.Abs(ballRest - playerRest) > t.Touch.TrapTolerance;
    }

    /// <summary>Angle in degrees between the wanted direction and the ball's travel (or the facing when the ball is still).</summary>
    static float TurnAngleDeg(PlayerState p, Ball ball, Vector2 wanted)
    {
        var bv = new Vector2(ball.Velocity.X, ball.Velocity.Z);
        float reference = bv.Length() > 0.3f ? MathF.Atan2(bv.Y, bv.X)
            : p.Speed > 0.3f ? p.Facing
            : MathF.Atan2(wanted.Y, wanted.X);
        return MathF.Abs(Movement.WrapAngle(MathF.Atan2(wanted.Y, wanted.X) - reference)) * 180f / MathF.PI;
    }

    /// <summary>
    /// Decides whether the player touches the ball this tick and, if so, makes the touch: sets the ball's velocity and spin and the
    /// player's last-touch time and foot. All randomness comes from <paramref name="rng"/>.
    /// </summary>
    /// <param name="inPossession">True if the player controls the ball; false makes the touch a first contact.</param>
    /// <param name="softOnly">Shoot is held: only a soft touch (ball to the player's speed) may happen.</param>
    public static bool TryTouch(PlayerState p, in TickIntent intent, Ball ball, float now, bool inPossession, bool softOnly,
        Tuning t, SimRng rng, out TouchResult result)
    {
        result = default;
        var k = t.Touch;
        if (!InReach(p, ball, t)) return false;
        float since = now - p.LastTouchTime;
        Vector2 stick = intent.Move;
        float mag = stick.Length();
        bool stickActive = mag >= k.StickThreshold;
        Vector3 relVel = Flat(ball.Velocity - p.Velocity);
        float relSpeed = relVel.Length();
        var mode = ModeFor(intent, t);

        TouchKind kind;
        if (softOnly)
        {
            // The press is deliberate: the soft touch does not wait for the cadence, or a sprint touch just made would carry
            // the ball out of strike reach during the charge.
            if (!inPossession || since <= 0f) return false;
            kind = TouchKind.Soft;
        }
        else if (!inPossession)
        {
            if (since < k.TurnMinInterval) return false;
            if (!stickActive && relSpeed < k.FirstTouchMinRelativeSpeed) return false; // a still ball and no stick: stand by it
            kind = TouchKind.First;
        }
        else if (!stickActive)
        {
            if (since < k.TurnMinInterval || !NeedsTrap(p, p.Position, ball.Position, ball.Velocity, t)) return false;
            kind = TouchKind.Trap;
        }
        else
        {
            float turn = TurnAngleDeg(p, ball, stick);
            if (turn > k.TurnTriggerDeg && since >= k.TurnMinInterval)
                kind = turn > k.CutDeg && p.Speed > k.CutMinSpeed ? TouchKind.Cut : TouchKind.Turn;
            else if (since >= Cadence(mode, t)) kind = TouchKind.Dribble;
            else return false;
        }

        float top = Movement.TopSpeed(p.Sheet, t);
        float speedFrac = Math.Clamp(p.Speed / top, 0f, 1f);
        Foot foot = ChooseFoot(p.Sheet, p.Position, p.Facing, ball.Position, t);
        float footK = FootErrorFactor(p.Sheet, foot, t);
        float sigmaSpeed = SpeedSigma(p.Sheet, speedFrac, t) * footK;
        float sigmaDir = DirectionSigmaDeg(p.Sheet, speedFrac, t) * footK;

        Vector3 v;
        float errDeg = 0f;
        switch (kind)
        {
            case TouchKind.Soft:
                // Never faster than the player: a soft touch must not push the ball out of strike reach.
                v = Flat(p.Velocity) * (1f - MathF.Abs(rng.Normal(0f, sigmaSpeed)));
                break;
            case TouchKind.Trap:
            {
                Vector3 dir = TrapDirection(p);
                v = dir * (TrapSpeed(p, ball, dir, t) * rng.Normal(1f, sigmaSpeed));
                break;
            }
            case TouchKind.First when !stickActive:
            {
                // No stick: the contact stops the ball relative to the player, badly for a poor first touch.
                errDeg = rng.Normal(0f, MathF.Sqrt(sigmaDir * sigmaDir + Sq(FirstTouchSigmaDeg(p.Sheet, relSpeed, t) * footK)));
                v = Flat(p.Velocity) + Rotate(relVel * FirstTouchSpeedKept(p.Sheet, t), errDeg);
                break;
            }
            default:
            {
                if (kind == TouchKind.First)
                    sigmaDir = MathF.Sqrt(sigmaDir * sigmaDir + Sq(FirstTouchSigmaDeg(p.Sheet, relSpeed, t) * footK));
                errDeg = rng.Normal(0f, sigmaDir);
                float yaw = MathF.Atan2(stick.Y, stick.X) + errDeg * MathF.PI / 180f;
                var dir = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
                float speed;
                if (kind == TouchKind.Cut)
                    speed = p.Speed * (1f - Movement.BalanceLoss(p.Sheet, t));
                else
                {
                    float along = Vector3.Dot(p.Velocity, dir);
                    speed = MathF.Max(along, MinPush(mode, t)) * (1f + Overshoot(p.Sheet, mode, t));
                }
                v = dir * (speed * rng.Normal(1f, sigmaSpeed));
                if (kind == TouchKind.First) v += relVel * FirstTouchSpeedKept(p.Sheet, t);
                break;
            }
        }

        result = new TouchResult(kind, foot, ball.Position, v, errDeg);
        ball.Velocity = new Vector3(v.X, 0f, v.Z);
        ball.Spin = Vector3.Zero;
        p.LastTouchTime = now;
        p.LastTouchFoot = foot;
        return true;
    }

    static float Sq(float x) => x * x;

    static Vector3 Rotate(Vector3 v, float deg)
    {
        float a = deg * MathF.PI / 180f, c = MathF.Cos(a), s = MathF.Sin(a);
        return new Vector3(v.X * c - v.Z * s, 0f, v.X * s + v.Z * c);
    }
}
