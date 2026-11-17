using System.Numerics;

namespace FootballSim.Core;

/// <summary>Player speed, acceleration, turning, balance and weight. Models take a player, never "the attacker".</summary>
public static class Movement
{
    /// <summary>Top speed without the ball, m/s (sprint_speed).</summary>
    public static float TopSpeed(PlayerSheet s, Tuning t) => t.Movement.SprintSpeed.ForRating(s.SprintSpeed);

    /// <summary>Top speed while in control of the ball, m/s (dribbling scales sprint_speed).</summary>
    public static float TopSpeedWithBall(PlayerSheet s, Tuning t) =>
        TopSpeed(s, t) * t.Touch.TopSpeedWithBall.ForRating(s.Dribbling);

    /// <summary>Speed time constant, seconds (acceleration).</summary>
    public static float TimeConstant(PlayerSheet s, Tuning t) => t.Movement.TimeConstant.ForRating(s.Acceleration);

    /// <summary>
    /// Speed time constant at simulation time <paramref name="now"/>, seconds: divided by the touch boost while it lasts (from a
    /// dribble, turn or cut touch until the boost runs out or the player touches the ball again).
    /// </summary>
    public static float TimeConstantAt(PlayerState p, float now, Tuning t)
    {
        float tau = TimeConstant(p.Sheet, t);
        return TouchBoostActive(p, now) ? tau / p.TouchBoostFactor : tau;
    }

    /// <summary>True while the boost from the player's most recent touch lasts.</summary>
    internal static bool TouchBoostActive(PlayerState p, float now) => p.TouchBoostFrom == p.LastTouchTime && now < p.TouchBoostUntil;

    /// <summary>The speed the legs ease toward for an intent under a speed cap: cap * stick deflection * mode fraction, m/s.</summary>
    internal static float TargetSpeed(in TickIntent intent, float top, Tuning t)
    {
        var m = t.Movement;
        float mag = MathF.Min(1f, intent.Move.Length());
        if (mag < m.StickDeadZone) mag = 0f;
        float mode = intent.CloseControl
            ? m.CloseControlFraction
            : m.JogFraction + (m.SprintFraction - m.JogFraction) * Math.Clamp(intent.Sprint, 0f, 1f);
        return top * mag * mode;
    }

    /// <summary>
    /// Distance a player covers in <paramref name="ticks"/> ticks after <paramref name="start"/> running straight, with the speed
    /// starting at <paramref name="v0"/> and easing toward <paramref name="target"/> exactly as <see cref="Step"/> does (touch
    /// boost included), metres. Turning, the balance loss and the sideways assist are left out.
    /// </summary>
    internal static float RunDistance(PlayerState p, float v0, float target, float start, int ticks, Tuning t)
    {
        float dt = t.Dt, speed = MathF.Max(v0, 0f), dist = 0f;
        for (int j = 1; j <= ticks; j++)
        {
            float tau = MathF.Max(TimeConstantAt(p, start + j * dt, t), dt);
            speed = MathF.Max(speed + (target - speed) / tau * dt, 0f);
            dist += speed * dt;
        }
        return dist;
    }

    /// <summary>Turn rate in degrees per second before the speed penalty: agility, scaled by weight.</summary>
    public static float TurnRateDeg(PlayerSheet s, Tuning t)
    {
        var m = t.Movement;
        float weight = Math.Clamp(1f - m.WeightTurnSlope * (s.WeightKg - m.WeightReference) / m.WeightRange,
            m.WeightFactorMin, m.WeightFactorMax);
        return m.TurnRateDeg.ForRating(s.Agility) * weight;
    }

    /// <summary>Fraction of speed lost on a sharp turn (balance).</summary>
    public static float BalanceLoss(PlayerSheet s, Tuning t) => t.Movement.BalanceLoss.ForRating(s.Balance);

    /// <summary>Wraps an angle to -pi..pi.</summary>
    public static float WrapAngle(float a)
    {
        const float twoPi = MathF.PI * 2f;
        a %= twoPi;
        if (a > MathF.PI) a -= twoPi;
        else if (a < -MathF.PI) a += twoPi;
        return a;
    }

    /// <summary>
    /// Moves a player one tick. Velocity points along the facing. Speed follows
    /// dv/dt = (target - v) / time_constant (shortened by a touch boost); the facing turns toward the stick at the turn rate.
    /// </summary>
    /// <param name="now">Simulation seconds, for the sharp-turn window.</param>
    /// <param name="hasBall">True if the player is in control of the ball (lowers top speed).</param>
    public static void Step(PlayerState p, in TickIntent intent, float dt, float now, bool hasBall, Tuning t, Pitch? pitch) =>
        Step(p, intent, dt, now, new MoveAssist(hasBall ? TopSpeedWithBall(p.Sheet, t) : TopSpeed(p.Sheet, t), 1f, Vector2.Zero), t, pitch);

    /// <summary>
    /// Moves a player one tick with an explicit speed cap, turn-rate scale and a sideways correction velocity (the possession
    /// assist). The forward speed follows the facing; the sideways correction is added on top and does not feed back into it.
    /// </summary>
    internal static void Step(PlayerState p, in TickIntent intent, float dt, float now, in MoveAssist assist, Tuning t, Pitch? pitch)
    {
        var m = t.Movement;
        float top = assist.TopSpeed;
        Vector2 stick = intent.Move;
        float mag = stick.Length();
        if (mag > 1f) { stick /= mag; mag = 1f; }
        if (mag < m.StickDeadZone) mag = 0f;

        float mode = intent.CloseControl
            ? m.CloseControlFraction
            : m.JogFraction + (m.SprintFraction - m.JogFraction) * Math.Clamp(intent.Sprint, 0f, 1f);
        float target = top * mag * mode;
        var fwd0 = new Vector3(MathF.Cos(p.Facing), 0f, MathF.Sin(p.Facing));
        float speed = MathF.Max(0f, Vector3.Dot(p.Velocity, fwd0));
        float tau = MathF.Max(TimeConstantAt(p, now, t), dt);
        speed += (target - speed) / tau * dt;

        float facing = p.Facing;
        if (mag > 0f)
        {
            float desired = MathF.Atan2(stick.Y, stick.X);
            float diff = WrapAngle(desired - facing);
            float rate = TurnRateDeg(p.Sheet, t) * assist.TurnScale * (1f - m.TurnSpeedPenalty * Math.Clamp(speed / top, 0f, 1f)) * MathF.PI / 180f;
            float maxTurn = rate * dt;
            facing = WrapAngle(facing + Math.Clamp(diff, -maxTurn, maxTurn));
        }

        // A turn over SharpTurnDeg inside SharpTurnWindow costs the balance percentage of current speed.
        float change = MathF.Abs(WrapAngle(facing - p.TurnRefHeading)) * 180f / MathF.PI;
        if (change > m.SharpTurnDeg && now - p.TurnRefTime <= m.SharpTurnWindow)
        {
            speed *= 1f - BalanceLoss(p.Sheet, t);
            p.TurnRefHeading = facing;
            p.TurnRefTime = now;
        }
        else if (now - p.TurnRefTime > m.SharpTurnWindow)
        {
            p.TurnRefHeading = facing;
            p.TurnRefTime = now;
        }

        speed = MathF.Max(speed, 0f);
        p.MoveTime = now;
        p.Facing = facing;
        p.Velocity = new Vector3(MathF.Cos(facing) * speed + assist.Lateral.X, 0f, MathF.Sin(facing) * speed + assist.Lateral.Y);
        Vector3 pos = p.Position + p.Velocity * dt;
        if (pitch != null)
        {
            pos.X = Math.Clamp(pos.X, pitch.MinX, pitch.GoalLineX);
            pos.Z = Math.Clamp(pos.Z, -pitch.HalfWidth, pitch.HalfWidth);
        }
        p.Position = pos;
        p.HandPosition = pos;
    }
}

/// <summary>Per-tick movement modifiers: the speed cap, a turn-rate scale and a sideways correction velocity (world X, Z).</summary>
internal readonly record struct MoveAssist(float TopSpeed, float TurnScale, Vector2 Lateral);
