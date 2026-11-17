using System.Numerics;

namespace FootballSim.Core;

/// <summary>A predicted touch: seconds from now, the foot and the expected ball centre. Eta is NaN when none is expected.</summary>
public readonly record struct TouchForecast(float Eta, Foot Foot, Vector3 Point)
{
    /// <summary>No touch expected.</summary>
    public static TouchForecast None => new(float.NaN, Foot.Right, Vector3.Zero);
}

/// <summary>
/// Possession rules that are not touches: the distance at which control is lost, the possession assist that steers the player
/// onto the ball's line, and the next-touch prediction a view uses to swing a foot. Models take a player and the ball.
/// </summary>
public static class Possession
{
    /// <summary>
    /// Distance beyond which the ball counts as away from the player: 2.0 m at walking pace to 3.5 m at a sprint. While the
    /// player chases a knock-on (see <see cref="ChasingKnockOn"/>) the ball is never away.
    /// </summary>
    public static float LoseDistance(PlayerState p, Tuning t)
    {
        if (ChasingKnockOn(p, t)) return float.PositiveInfinity;
        var k = t.Possession;
        float frac = Math.Clamp(p.Speed / Movement.TopSpeed(p.Sheet, t), 0f, 1f);
        return k.LoseDistanceSlow + (k.LoseDistanceFast - k.LoseDistanceSlow) * frac;
    }

    /// <summary>
    /// True while the player's last touch was a knock-on, its planned catch time plus the grace has not passed, and the player
    /// still runs along it at knock_on_chase_min_speed or faster. The next touch, the grace running out, or easing off ends
    /// the chase; then the normal lose distance applies.
    /// </summary>
    public static bool ChasingKnockOn(PlayerState p, Tuning t) =>
        p.KnockOnFrom == p.LastTouchTime
        && p.MoveTime < p.KnockOnUntil
        && Vector2.Dot(new Vector2(p.Velocity.X, p.Velocity.Z), p.KnockOnDirection) >= t.Touch.KnockOnChaseMinSpeed;

    /// <summary>The speed cap for a player: off-ball top speed off possession or while chasing a knock-on; in possession the
    /// with-ball cap, raised toward 97% of top speed while the ball is ahead and out of reach.</summary>
    internal static float SpeedCap(PlayerSheet s, Vector3 feet, float facing, Vector3 ball, bool inPossession, Tuning t, bool chasing = false)
    {
        if (!inPossession || chasing) return Movement.TopSpeed(s, t);
        float withBall = Movement.TopSpeedWithBall(s, t);
        var rel = new Vector2(ball.X - feet.X, ball.Z - feet.Z);
        float ahead = Vector2.Dot(rel, new Vector2(MathF.Cos(facing), MathF.Sin(facing)));
        if (ahead > 0f && rel.Length() > TouchModel.FootReach(s, t))
            return MathF.Max(withBall, Movement.TopSpeed(s, t) * t.Possession.ChaseSpeedFraction);
        return withBall;
    }

    /// <summary>
    /// The movement modifiers for this tick. In possession, with the stick pushed, the player gets a sideways correction toward
    /// the ball's line: 4 * offset per second, capped at 1.5 m/s, both scaled by the assist strength. Off possession the turn
    /// rate is 15% sharper and there is no with-ball cap.
    /// </summary>
    internal static MoveAssist Assist(PlayerState p, in TickIntent intent, Ball ball, bool inPossession, Tuning t)
    {
        var k = t.Possession;
        float cap = SpeedCap(p.Sheet, p.Position, p.Facing, ball.Position, inPossession, t, inPossession && ChasingKnockOn(p, t));
        if (!inPossession) return new MoveAssist(cap, 1f + k.OffBallTurnBonus, Vector2.Zero);

        Vector2 lateral = Vector2.Zero;
        if (intent.Move.Length() >= t.Touch.StickThreshold && k.AssistStrength > 0f)
        {
            var fwd = new Vector2(MathF.Cos(p.Facing), MathF.Sin(p.Facing));
            var right = new Vector2(-fwd.Y, fwd.X);
            var rel = new Vector2(ball.Position.X - p.Position.X, ball.Position.Z - p.Position.Z);
            if (Vector2.Dot(rel, fwd) > -0.1f && rel.Length() < LoseDistance(p, t))
            {
                float off = Vector2.Dot(rel, right);
                float max = k.AssistMaxSpeed * k.AssistStrength;
                lateral = right * Math.Clamp(k.AssistGain * k.AssistStrength * off, -max, max);
            }
        }
        return new MoveAssist(cap, 1f, lateral);
    }

    /// <summary>
    /// True when a player in possession must first get to the ball: it is ahead and just out of reach (within 0.5 m of the
    /// reach) while the stick is
    /// released (a stop) or asks for a new direction (a turn). The player then carries on to the ball at its current speed so
    /// the trap or the turn touch can be made, instead of stopping or turning away from it.
    /// </summary>
    internal static bool MustReachBall(PlayerSheet s, Vector3 feet, float facing, in TickIntent intent, Vector3 ballPos, Vector3 ballVel,
        float playerSpeed, Tuning t)
    {
        var rel = new Vector2(ballPos.X - feet.X, ballPos.Z - feet.Z);
        float dist = rel.Length();
        float reach = TouchModel.FootReach(s, t);
        if (dist <= reach || dist > reach + t.Possession.ReachBallMargin || ballPos.Y > t.Touch.MaxBallHeight) return false;
        if (Vector2.Dot(rel, new Vector2(MathF.Cos(facing), MathF.Sin(facing))) <= 0f) return false;
        Vector2 stick = intent.Move;
        if (stick.Length() < t.Touch.StickThreshold) return true;
        var bv = new Vector2(ballVel.X, ballVel.Z);
        float travel = bv.Length() > 0.3f ? MathF.Atan2(bv.Y, bv.X) : facing;
        return MathF.Abs(Movement.WrapAngle(MathF.Atan2(stick.Y, stick.X) - travel)) * 180f / MathF.PI > t.Touch.TurnTriggerDeg;
    }

    /// <summary>
    /// The intent the player's legs follow this tick. Normally the stick; in possession, while <see cref="MustReachBall"/>, the
    /// stick points at the ball with the deflection that holds the current speed (or the stick's own, if larger).
    /// </summary>
    internal static TickIntent MovementIntent(PlayerState p, in TickIntent intent, Ball ball, bool inPossession, float speedCap, Tuning t)
    {
        if (!inPossession || !MustReachBall(p.Sheet, p.Position, p.Facing, intent, ball.Position, ball.Velocity, p.Speed, t)) return intent;
        var m = t.Movement;
        float mode = intent.CloseControl ? m.CloseControlFraction
            : m.JogFraction + (m.SprintFraction - m.JogFraction) * Math.Clamp(intent.Sprint, 0f, 1f);
        float hold = Math.Clamp(p.Speed / MathF.Max(speedCap * mode, 1e-3f), 0f, 1f);
        float mag = MathF.Max(MathF.Min(1f, intent.Move.Length()), hold);
        var d = Vector2.Normalize(new Vector2(ball.Position.X - p.Position.X, ball.Position.Z - p.Position.Z));
        var steered = intent;
        steered.MoveX = d.X * mag;
        steered.MoveZ = d.Y * mag;
        return steered;
    }

    /// <summary>
    /// Predicts the next touch of a player in possession. The ball is integrated forward on its own (rolling, bouncing) and the
    /// player along its facing with the speed easing toward the stick's target (touch boost included), until the ball is in reach and the touch timing
    /// allows it, up to the prediction horizon. Returns <see cref="TouchForecast.None"/> when no touch is expected: no
    /// possession, shoot held after the soft touch, or the stick released with the ball already settling at the feet.
    /// </summary>
    /// <param name="softPending">Shoot is held and the soft touch has not been made yet.</param>
    public static TouchForecast PredictNextTouch(PlayerState p, in TickIntent intent, Ball ball, float now, bool inPossession,
        bool shootHeld, bool softPending, Tuning t)
    {
        if (!inPossession) return TouchForecast.None;
        var k = t.Touch;
        if (shootHeld && !softPending) return TouchForecast.None;
        bool stickActive = intent.Move.Length() >= k.StickThreshold;
        float interval;
        if (shootHeld) interval = 0f;
        else if (!stickActive)
        {
            if (!TouchModel.NeedsTrap(p, p.Position, ball.Position, ball.Velocity, t)) return TouchForecast.None;
            interval = k.TurnMinInterval;
        }
        else interval = TouchModel.Cadence(TouchModel.ModeFor(intent, t), t);

        float dt = t.Dt;
        var sheet = p.Sheet;
        Vector3 feet = p.Position;
        float facing = p.Facing;
        var dir = new Vector3(MathF.Cos(facing), 0f, MathF.Sin(facing));
        float speed = MathF.Max(0f, Vector3.Dot(p.Velocity, dir));
        var m = t.Movement;
        float mag = MathF.Min(1f, intent.Move.Length());
        if (mag < m.StickDeadZone || shootHeld && !stickActive) mag = 0f;
        float mode = intent.CloseControl ? m.CloseControlFraction
            : m.JogFraction + (m.SprintFraction - m.JogFraction) * Math.Clamp(intent.Sprint, 0f, 1f);
        float withBall = Movement.TopSpeedWithBall(sheet, t);
        float chase = ChasingKnockOn(p, t) ? Movement.TopSpeed(sheet, t)
            : MathF.Max(withBall, Movement.TopSpeed(sheet, t) * t.Possession.ChaseSpeedFraction);
        float reach = TouchModel.FootReach(sheet, t);
        float reach2 = reach * reach;
        float carry = reach + t.Possession.ReachBallMargin;
        float carry2 = carry * carry;
        float maxH = k.MaxBallHeight;
        float since0 = now - p.LastTouchTime;
        int steps = (int)MathF.Ceiling(t.Possession.PredictionHorizon / dt);

        // A rolling ball keeps its direction, so whether the stick asks for a stop or a turn is fixed for the whole forecast.
        bool wantsChange = !stickActive;
        if (stickActive)
        {
            var bv0 = new Vector2(ball.Velocity.X, ball.Velocity.Z);
            float travel = bv0.Length() > 0.3f ? MathF.Atan2(bv0.Y, bv0.X) : facing;
            wantsChange = MathF.Abs(Movement.WrapAngle(MathF.Atan2(intent.Move.Y, intent.Move.X) - travel)) * 180f / MathF.PI > k.TurnTriggerDeg;
        }

        // A ball rolling on the ground is integrated inline with the same per-tick update as Ball.Step (no frame, no hands);
        // anything else steps a copy of the ball.
        bool rolling = ball.OnGround && MathF.Abs(ball.Velocity.Y) < 1e-6f;
        Ball? copy = rolling ? null : ball.Clone();
        Vector3 bp = ball.Position, bv = new(ball.Velocity.X, 0f, ball.Velocity.Z);
        float dec = t.Ball.RollDeceleration * dt, rest = t.Ball.RestSpeed;
        for (int j = 1; j <= steps; j++)
        {
            if (copy != null) { bp = copy.Position; }
            // Same order as the world: the player moves, the touch is checked, then the ball moves.
            float rx = bp.X - feet.X, rz = bp.Z - feet.Z;
            float d2 = rx * rx + rz * rz;
            bool ahead = rx * dir.X + rz * dir.Z > 0f;
            bool outOfReach = d2 > reach2;
            float target = (ahead && outOfReach ? chase : withBall) * mag * mode;
            if (wantsChange && ahead && outOfReach && d2 <= carry2 && bp.Y <= maxH)
            {
                // MustReachBall: carry on to the ball at the current speed.
                target = MathF.Max(target, speed);
                float l = MathF.Sqrt(d2);
                dir = new Vector3(rx / l, 0f, rz / l);
            }
            speed += (target - speed) / MathF.Max(Movement.TimeConstantAt(p, now + j * dt, t), dt) * dt;
            feet += dir * (MathF.Max(speed, 0f) * dt);
            if (since0 + j * dt >= interval - 1e-5f && TouchModel.InReach(sheet, feet, bp, t))
                return new TouchForecast(j * dt, TouchModel.ChooseFoot(sheet, feet, facing, bp, t), bp);
            if (copy != null) copy.Step(dt, null, null);
            else
            {
                float sp = bv.Length();
                bv = sp <= dec || sp < rest ? Vector3.Zero : bv * ((sp - dec) / sp);
                bp += bv * dt;
                // A ball at rest that the player is not moving toward will not come into reach.
                if (bv == Vector3.Zero && speed < 1e-3f && target < 1e-3f) break;
            }
        }
        return TouchForecast.None;
    }
}
