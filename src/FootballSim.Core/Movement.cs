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
        // A skill move's exit burst runs on through later touches; a touch boost larger than it still wins.
        if (now < p.SkillBurstUntil) return tau / (TouchBoostActive(p, now) ? MathF.Max(p.TouchBoostFactor, p.SkillBurstFactor) : p.SkillBurstFactor);
        return TouchBoostActive(p, now) ? tau / p.TouchBoostFactor : tau;
    }

    /// <summary>True while the boost from the player's most recent touch lasts.</summary>
    internal static bool TouchBoostActive(PlayerState p, float now) => p.TouchBoostFrom == p.LastTouchTime && now < p.TouchBoostUntil;

    /// <summary>
    /// The fraction of the speed cap the legs aim for: close control, or jog to sprint by the sprint input. The jog is a fixed
    /// speed (jog_speed, GOALS' 5.3 m/s for every player), so its fraction is jog_speed over the player's top speed; with the
    /// ball the cap is lowered by dribbling, and the jog with it.
    /// </summary>
    internal static float ModeFraction(in TickIntent intent, PlayerSheet s, Tuning t)
    {
        var m = t.Movement;
        if (intent.CloseControl) return m.CloseControlFraction;
        float top = TopSpeed(s, t);
        float jog = top > 0f ? MathF.Min(m.SprintFraction, m.JogSpeed / top) : m.SprintFraction;
        return jog + (m.SprintFraction - jog) * Math.Clamp(intent.Sprint, 0f, 1f);
    }

    /// <summary>Turn rate in degrees per second before the speed penalty: agility, scaled by weight.</summary>
    public static float TurnRateDeg(PlayerSheet s, Tuning t)
    {
        var m = t.Movement;
        float weight = Math.Clamp(1f - m.WeightTurnSlope * (s.WeightKg - m.WeightReference) / m.WeightRange,
            m.WeightFactorMin, m.WeightFactorMax);
        return m.TurnRateDeg.ForRating(s.Agility) * weight;
    }

    /// <summary>Fraction of speed lost on a sharp turn or a cut (balance), shed at balance_deceleration.</summary>
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
    /// Moves a player one tick with an explicit speed cap, turn-rate scale and a steering correction (the touch planner
    /// bringing the ball into the contact zone). The running speed follows the facing and eases toward the legs' target plus
    /// the correction's part along the run (<see cref="MoveAssist.SteerAlong"/>), so the correction never changes the speed
    /// outside the speed law; the sideways part (<see cref="MoveAssist.Steer"/>) is added to the velocity and does not feed
    /// back into the running speed, nor into the stride clock, which advances at the running speed. The running speed never
    /// falls faster than balance_deceleration: the legs easing off and a sharp turn's or cut's balance loss share that one
    /// budget.
    /// </summary>
    internal static void Step(PlayerState p, in TickIntent intent, float dt, float now, in MoveAssist assist, Tuning t, Pitch? pitch)
    {
        var m = t.Movement;
        float top = assist.TopSpeed;
        Vector2 stick = intent.Move;
        float mag = stick.Length();
        if (mag > 1f) { stick /= mag; mag = 1f; }
        if (mag < m.StickDeadZone) mag = 0f;

        float target = top * mag * ModeFraction(intent, p.Sheet, t);
        // The steering's part along the run moves the target (never above the off-ball top speed, never below a standstill).
        if (assist.SteerAlong != 0f) target = Math.Clamp(target + assist.SteerAlong, 0f, MathF.Max(top, TopSpeed(p.Sheet, t)));
        var fwd0 = new Vector3(MathF.Cos(p.Facing), 0f, MathF.Sin(p.Facing));
        float speed = MathF.Max(0f, Vector3.Dot(p.Velocity - p.SteerVelocity, fwd0));
        float speed0 = speed;
        float tau = MathF.Max(TimeConstantAt(p, now, t), dt);
        bool law = float.IsNaN(assist.RunSpeed);
        float budget = m.BalanceDeceleration * dt;
        // A skill move sets the running speed itself (it paces the player against the ball); otherwise it eases to the target,
        // easing off no faster than the deceleration budget.
        if (law) speed = MathF.Max(speed + (target - speed) / tau * dt, speed0 - budget);
        else speed = assist.RunSpeed;
        // The stun after a strike holds the running speed under its cap at once: the strike plants the shooter.
        speed = MathF.Min(speed, assist.HardCap);

        float facing = p.Facing;
        if (mag > 0f)
        {
            float desired = MathF.Atan2(stick.Y, stick.X);
            float diff = WrapAngle(desired - facing);
            // Turning about without the ball: a plant and brake, once per turn (the sharp-turn window apart).
            if (assist.TurnAbout && MathF.Abs(diff) * 180f / MathF.PI > m.ReverseTurnDeg && speed > m.ReverseMinSpeed
                && now - p.ReverseTime > m.SharpTurnWindow)
            {
                p.SpeedDebt = MathF.Max(p.SpeedDebt, speed * m.ReverseSpeedLoss);
                p.ReverseTime = now;
            }
            float rate = TurnRateDeg(p.Sheet, t) * assist.TurnScale * (1f - m.TurnSpeedPenalty * Math.Clamp(speed / top, 0f, 1f)) * MathF.PI / 180f;
            if (now < p.PostTurnUntil) rate = MathF.Max(rate, m.PostTurnRotationRate * MathF.PI / 180f);
            float maxTurn = rate * dt;
            // A small heading change eases in (time constant turn_ease) instead of snapping round at the full rate; a large
            // turn runs at the turn rate until it is within rate * turn_ease of the stick.
            if (m.TurnEase > 0f) maxTurn = MathF.Min(maxTurn, MathF.Abs(diff) * MathF.Min(1f, dt / m.TurnEase));
            facing = WrapAngle(facing + Math.Clamp(diff, -maxTurn, maxTurn));
        }

        // A turn over SharpTurnDeg inside SharpTurnWindow costs the balance percentage of current speed. The loss is not taken
        // at once (a step in the velocity): it is shed at balance_deceleration, on top of the easing toward the target.
        float change = MathF.Abs(WrapAngle(facing - p.TurnRefHeading)) * 180f / MathF.PI;
        if (change > m.SharpTurnDeg && now - p.TurnRefTime <= m.SharpTurnWindow)
        {
            p.SpeedDebt += speed * BalanceLoss(p.Sheet, t);
            p.TurnRefHeading = facing;
            p.TurnRefTime = now;
        }
        else if (now - p.TurnRefTime > m.SharpTurnWindow)
        {
            p.TurnRefHeading = facing;
            p.TurnRefTime = now;
        }
        if (p.SpeedDebt > 0f)
        {
            // Under the speed law the balance loss takes what the legs' own easing off leaves of the budget.
            float room = law ? budget + speed - speed0 : budget;
            float shed = MathF.Min(p.SpeedDebt, MathF.Max(0f, room));
            speed -= shed;
            p.SpeedDebt -= shed;
            if (speed <= 0f) p.SpeedDebt = 0f;
        }

        speed = MathF.Max(speed, 0f);
        // The sideways correction turns with the body: as the facing turns this tick it keeps its angle to the run instead of
        // swinging into or against it. A skill move's step (the carry) keeps its direction on the ground.
        float turned = WrapAngle(facing - p.Facing), tc = MathF.Cos(turned), ts = MathF.Sin(turned);
        var steer = new Vector2(assist.Steer.X * tc - assist.Steer.Y * ts, assist.Steer.X * ts + assist.Steer.Y * tc) + assist.Carry;
        p.MoveTime = now;
        p.Facing = facing;
        p.RunSpeed = speed;
        p.SteerVelocity = new Vector3(steer.X, 0f, steer.Y);
        p.Velocity = new Vector3(MathF.Cos(facing) * speed + steer.X, 0f, MathF.Sin(facing) * speed + steer.Y);
        Vector3 pos = p.Position + p.Velocity * dt;
        if (pitch != null)
        {
            // The boards stop the player: he stays inside the arena outline and loses the part of his velocity into the board.
            var v = p.Velocity;
            if (pitch.ClampPlayer(ref pos, ref v)) p.Velocity = v;
        }
        p.Position = pos;
        p.HandPosition = pos;
        Gait.Advance(p, speed, dt, t);
    }
}

/// <summary>
/// Per-tick movement modifiers: the speed cap, a turn-rate scale, a steering correction velocity added to the velocity (world
/// X, Z) and, for a skill move, the running speed to use this tick (NaN: the legs ease toward the stick's target as usual), a
/// running speed never exceeded this tick (the post-strike stun), whether a stick turned about brakes the run (without the
/// ball), the steering's part along the run, m/s, added to the legs' target speed (it reaches the running speed through the
/// speed law), and a skill move's step, a velocity (world X, Z) that, unlike <see cref="Steer"/>, does not turn with the body.
/// </summary>
internal readonly record struct MoveAssist(float TopSpeed, float TurnScale, Vector2 Steer, float RunSpeed = float.NaN,
    float HardCap = float.PositiveInfinity, bool TurnAbout = false, float SteerAlong = 0f, Vector2 Carry = default);
