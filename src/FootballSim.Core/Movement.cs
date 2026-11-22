using System.Numerics;

namespace FootballSim.Core;

/// <summary>Player speed, acceleration, turning, balance and weight. Models take a player, never "the attacker".</summary>
public static class Movement
{
    /// <summary>Top speed without the ball, m/s (sprint_speed).</summary>
    public static float TopSpeed(PlayerSheet s, Tuning t) => t.Movement.SprintSpeed.ForRating(s.SprintSpeed);

    /// <summary>
    /// Top speed while in control of the ball, m/s: sprint_speed scaled by dribbling, by the close style's range blended into
    /// the speed style's by <paramref name="speedShare"/> (0..1: 0 the close style, 1 the speed style; see
    /// <see cref="SpeedStyleShare"/>). The blend is continuous, so the cap never steps as the sprint input moves.
    /// </summary>
    public static float TopSpeedWithBall(PlayerSheet s, float speedShare, Tuning t)
    {
        var k = t.Touch.TopSpeedWithBall;
        float close = k.Close.ForRating(s.CloseDribbling), speed = k.Speed.ForRating(s.SprintDribbling);
        return TopSpeed(s, t) * (close + (speed - close) * Math.Clamp(speedShare, 0f, 1f));
    }

    /// <summary>Top speed with the ball for the dribbling style an intent asks for (see <see cref="SpeedStyleShare"/>).</summary>
    public static float TopSpeedWithBall(PlayerSheet s, in TickIntent intent, Tuning t) => TopSpeedWithBall(s, SpeedStyleShare(intent), t);

    /// <summary>
    /// How much of the speed dribbling style an intent asks for (0..1), the rest being the close style: the sprint input, none
    /// under close control. The same share blends the jog pace into the sprint pace (<see cref="ModeFraction"/>).
    /// </summary>
    public static float SpeedStyleShare(in TickIntent intent) => SprintShare(intent);

    /// <summary>
    /// Speed time constant, seconds (acceleration): the soft landing of the speed law, which eases the last of a change in speed
    /// in exponentially instead of meeting the target at the full acceleration.
    /// </summary>
    public static float TimeConstant(PlayerSheet s, Tuning t) => t.Movement.TimeConstant.ForRating(s.Acceleration);

    /// <summary>
    /// The multiplier on the player's acceleration at simulation time <paramref name="now"/>: the touch boost while it lasts (from
    /// a dribble, turn or cut touch until the boost runs out or the player touches the ball again), a skill move's exit burst.
    /// </summary>
    public static float AccelMultiplierAt(PlayerState p, float now)
    {
        // A skill move's exit burst runs on through later touches; a touch boost larger than it still wins.
        if (now < p.SkillBurstUntil) return TouchBoostActive(p, now) ? MathF.Max(p.TouchBoostFactor, p.SkillBurstFactor) : p.SkillBurstFactor;
        return TouchBoostActive(p, now) ? p.TouchBoostFactor : 1f;
    }

    /// <summary>
    /// The speed law's soft-landing time constant at simulation time <paramref name="now"/>, seconds: the time constant shortened
    /// by the acceleration multiplier. Near its target the running speed eases in with it (the touch planner's model of the law).
    /// </summary>
    public static float TimeConstantAt(PlayerState p, float now, Tuning t) => TimeConstant(p.Sheet, t) / AccelMultiplierAt(p, now);

    /// <summary>
    /// The most the player speeds up, m/s^2, at running speed <paramref name="speed"/>: the jog curve blended into the sprint
    /// curve by <paramref name="sprint"/> (0..1, the sprint input), by the speed as a fraction of the top speed without the ball,
    /// scaled by acceleration.
    /// </summary>
    public static float AccelCap(PlayerSheet s, float speed, float sprint, Tuning t)
    {
        var m = t.Movement;
        float top = TopSpeed(s, t);
        float f = top > 0f ? Math.Clamp(speed / top, 0f, 1f) : 0f;
        float jog = m.AccelBySpeedJog.Evaluate(f), full = m.AccelBySpeedSprint.Evaluate(f);
        return (jog + (full - jog) * Math.Clamp(sprint, 0f, 1f)) * m.AccelRatingScale.ForRating(s.Acceleration);
    }

    /// <summary>
    /// The most the player slows down by easing off, m/s^2, at running speed <paramref name="speed"/>: the deceleration curve,
    /// by the speed as a fraction of the top speed without the ball.
    /// </summary>
    public static float DecelCap(PlayerSheet s, float speed, Tuning t)
    {
        float top = TopSpeed(s, t);
        return t.Movement.DecelBySpeed.Evaluate(top > 0f ? Math.Clamp(speed / top, 0f, 1f) : 0f);
    }

    /// <summary>The sprint share (0..1) the acceleration curves blend by: the sprint input, none under close control.</summary>
    internal static float SprintShare(in TickIntent intent) => intent.CloseControl ? 0f : Math.Clamp(intent.Sprint, 0f, 1f);

    /// <summary>True while the boost from the player's most recent touch lasts.</summary>
    internal static bool TouchBoostActive(PlayerState p, float now) => p.TouchBoostFrom == p.LastTouchTime && now < p.TouchBoostUntil;

    /// <summary>
    /// The fraction of the speed cap the legs aim for: close control, or jog to sprint by the sprint input. The jog is a fixed
    /// speed (jog_speed, 5.3 m/s for every player, from reference data), so its fraction is jog_speed over the player's top speed; with the
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

    /// <summary>The most a player's running speed falls in one tick braking for the boards, m/s.</summary>
    const float WallBrakeStep = 1.5f;

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
    /// Moves a player one tick. Velocity points along the facing. Speed follows dv/dt = (target - v) / time_constant, speeding
    /// up no harder than the acceleration cap (both scaled by a touch boost); the facing turns toward the stick at the turn rate.
    /// </summary>
    /// <param name="now">Simulation seconds, for the sharp-turn window.</param>
    /// <param name="hasBall">True if the player is in control of the ball (lowers top speed).</param>
    public static void Step(PlayerState p, in TickIntent intent, float dt, float now, bool hasBall, Tuning t, Pitch? pitch) =>
        Step(p, intent, dt, now, new MoveAssist(hasBall ? TopSpeedWithBall(p.Sheet, intent, t) : TopSpeed(p.Sheet, t), 1f, Vector2.Zero), t, pitch);

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
        float legs = target;
        // The steering's part along the run moves the target (never above the off-ball top speed, never below a standstill).
        if (assist.SteerAlong != 0f) target = Math.Clamp(target + assist.SteerAlong, 0f, MathF.Max(top, TopSpeed(p.Sheet, t)));
        var fwd0 = new Vector3(MathF.Cos(p.Facing), 0f, MathF.Sin(p.Facing));
        float speed = MathF.Max(0f, Vector3.Dot(p.Velocity - p.SteerVelocity, fwd0));
        float speed0 = speed;
        float mult = AccelMultiplierAt(p, now);
        float tau = MathF.Max(TimeConstant(p.Sheet, t) / mult, dt);
        bool law = float.IsNaN(assist.RunSpeed);
        float budget = m.BalanceDeceleration * dt;
        // A skill move sets the running speed itself (it paces the player against the ball); otherwise it goes to the target
        // at dv/dt = (target - v) / time_constant, the legs' own part of it speeding up no harder than the acceleration cap
        // (times the boost) and easing off no harder than the deceleration cap (the time constant eases the last of either in),
        // and never falling faster than the deceleration budget. The steering's part along the run is added at its full
        // (target - v) / time_constant rate, which the touch planner counts on.
        if (law)
        {
            float rate = (target - speed) / tau;
            float own = (legs - speed) / tau;
            if (own > 0f)
            {
                float cap = AccelCap(p.Sheet, speed, SprintShare(intent), t) * mult;
                if (own > cap) rate = rate - own + cap;
            }
            else if (own < 0f)
            {
                float cap = DecelCap(p.Sheet, speed, t);
                if (own < -cap) rate = rate - own - cap;
            }
            speed = MathF.Max(speed + rate * dt, speed0 - budget);
        }
        else speed = assist.RunSpeed;
        // Out of a braking arc he speeds up no faster than it braked him, until the speed law asks for less.
        if (p.TurnArcRelease)
        {
            float up = speed0 + t.Touch.TurnArcBrake * dt;
            if (law && speed > up) speed = up;
            else p.TurnArcRelease = false;
        }
        // The stun after a strike brings the running speed down to its cap, no faster than the deceleration budget (the strike
        // plants the shooter without a step in his speed); so does a braking arc (a sharp turn with the ball at a run) to its speed.
        speed = MathF.Min(speed, MathF.Max(assist.HardCap, speed0 - budget));
        // (The arc's braking is the turn's balance loss: no other is taken while it lasts.)
        bool arc = p.TurnArcBraking && p.TurnArcSpeed > 0f;
        if (arc) { speed = MathF.Min(speed, MathF.Max(p.TurnArcSpeed, speed0 - t.Touch.TurnArcBrake * dt)); p.SpeedDebt = 0f; }

        float facing = p.Facing;
        // A spin sets the facing itself.
        if (!float.IsNaN(assist.Facing)) facing = Movement.WrapAngle(assist.Facing);
        else if (mag > 0f)
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
            // The turn rate is scaled by the heading change still to make (turn_angle_mult): a gentle change comes round
            // briskly, a sharp one more slowly.
            float rate = TurnRateDeg(p.Sheet, t) * assist.TurnScale * (1f - m.TurnSpeedPenalty * Math.Clamp(speed / top, 0f, 1f))
                * m.TurnAngleMult.Evaluate(MathF.Abs(diff) * 180f / MathF.PI) * MathF.PI / 180f;
            if (now < p.PostTurnUntil) rate = MathF.Max(rate, m.PostTurnRotationRate * MathF.PI / 180f);
            if (arc) rate = MathF.Max(rate, t.Touch.TurnArcRotation.ForRating(p.Sheet.CloseDribbling) * MathF.PI / 180f);
            // (an outfield player without the ball turns no faster than his sideways grip allows at his speed)
            float maxTurn = MathF.Min(rate, assist.MaxTurnRate) * dt;
            // A small heading change eases in (time constant turn_ease) instead of snapping round at the full rate; a large
            // turn runs at the turn rate until it is within rate * turn_ease of the stick.
            if (m.TurnEase > 0f) maxTurn = MathF.Min(maxTurn, MathF.Abs(diff) * MathF.Min(1f, dt / m.TurnEase));
            facing = WrapAngle(facing + Math.Clamp(diff, -maxTurn, maxTurn));
        }

        // A turn inside SharpTurnWindow costs its share (sharp_turn_cost, by the angle come round) of the balance percentage of
        // current speed, taken as the angle grows; a full share starts a new window. The loss is not taken at once (a step in
        // the velocity): it is shed at balance_deceleration, on top of the easing toward the target.
        float change = MathF.Abs(WrapAngle(facing - p.TurnRefHeading)) * 180f / MathF.PI;
        if (now - p.TurnRefTime <= m.SharpTurnWindow)
        {
            float share = Math.Clamp(m.SharpTurnCost.Evaluate(change), 0f, 1f);
            float taken = p.TurnCostFrom == p.TurnRefTime ? p.TurnCostTaken : 0f;
            if (share > taken)
            {
                p.SpeedDebt += speed * BalanceLoss(p.Sheet, t) * (share - taken);
                p.TurnCostTaken = share;
                p.TurnCostFrom = p.TurnRefTime;
            }
            if (share >= 1f)
            {
                p.TurnRefHeading = facing;
                p.TurnRefTime = now;
            }
        }
        else
        {
            p.TurnRefHeading = facing;
            p.TurnRefTime = now;
        }
        if (p.SpeedDebt > 0f)
        {
            // The balance loss takes what the legs' own easing off (or a move's own running speed) leaves of the budget.
            float room = budget + speed - speed0;
            float shed = MathF.Min(p.SpeedDebt, MathF.Max(0f, room));
            speed -= shed;
            p.SpeedDebt -= shed;
            if (speed <= 0f) p.SpeedDebt = 0f;
        }
        // The braking arc holds its speed: the balance loss does not take the run lower.
        if (arc && speed < MathF.Min(p.TurnArcSpeed, speed0)) speed = MathF.Min(p.TurnArcSpeed, speed0);
        if (arc) { p.SpeedDebt = 0f; p.TurnRefHeading = facing; p.TurnRefTime = now; }

        speed = MathF.Max(speed, 0f);
        // Running at the boards he brakes by himself, so he comes to them at wall_arrive_speed or less (never stopped by them from
        // a run in one tick); a turn onto the boards close to them brakes harder than the budget, at most WallBrakeStep a tick.
        if (pitch != null)
        {
            var fwd1 = new Vector2(MathF.Cos(facing), MathF.Sin(facing));
            float tc0 = MathF.Cos(WrapAngle(facing - p.Facing)), ts0 = MathF.Sin(WrapAngle(facing - p.Facing));
            var steer0 = new Vector2(assist.Steer.X * tc0 - assist.Steer.Y * ts0, assist.Steer.X * ts0 + assist.Steer.Y * tc0) + assist.Carry;
            // (from where he will be after this tick's step, so he is at the speed allowed there when he gets there)
            var at = new Vector2(p.Position.X, p.Position.Z) + (fwd1 * speed + steer0) * dt;
            float wall = pitch.WallRunCap(at, fwd1, steer0, m.WallArriveSpeed, m.BalanceDeceleration);
            if (speed > wall) speed = MathF.Max(wall, MathF.Min(speed, speed0) - WallBrakeStep);
        }
        // The sideways correction turns with the body: as the facing turns this tick it keeps its angle to the run instead of
        // swinging into or against it. A skill move's step (the carry) keeps its direction on the ground.
        float turned = WrapAngle(facing - p.Facing), tc = MathF.Cos(turned), ts = MathF.Sin(turned);
        var steered = new Vector2(assist.Steer.X * tc - assist.Steer.Y * ts, assist.Steer.X * ts + assist.Steer.Y * tc);
        var steer = steered + assist.Carry;
        if (assist.ShareCarry && assist.Carry != assist.CarryFrom)
        {
            // A move's step easing out after it shares the deceleration budget with the run: it is held back as far as needed
            // for the ground speed to fall no faster than balance_deceleration (it never holds a speed-up back).
            var run = new Vector2(MathF.Cos(facing), MathF.Sin(facing)) * speed + steered;
            float v0 = new Vector2(p.Velocity.X, p.Velocity.Z).Length();
            Vector2 cFrom = assist.CarryFrom, cTo = assist.Carry;
            float Ground(float s) => (run + cFrom + (cTo - cFrom) * s).Length();
            if (Ground(1f) < v0 - budget && Ground(0f) > Ground(1f))
            {
                float lo = 0f, hi = 1f;
                for (int i = 0; i < 12; i++)
                {
                    float mid = 0.5f * (lo + hi);
                    if (Ground(mid) < v0 - budget) hi = mid; else lo = mid;
                }
                var c = cFrom + (cTo - cFrom) * lo;
                p.SteerCarry = c;
                steer = steered + c;
            }
        }
        // The arc ends once he faces within turn_arc_done_deg of the stick that asked for it, or, braking and come round to
        // within 45 degrees of it, of where his legs take him (the ball's line), so he never stays slow on a line he already runs.
        float offStick = MathF.Abs(WrapAngle(MathF.Atan2(p.TurnArcStick.Y, p.TurnArcStick.X) - facing)) * 180f / MathF.PI;
        float offLegs = mag > 0f ? MathF.Abs(WrapAngle(MathF.Atan2(stick.Y, stick.X) - facing)) * 180f / MathF.PI : 0f;
        if (p.TurnArcSpeed > 0f && (offStick <= t.Touch.TurnArcDoneDeg || arc && offStick <= 45f && offLegs <= t.Touch.TurnArcDoneDeg))
        {
            // (the turn it made is paid for: no sharp-turn loss for it after the arc either)
            if (p.TurnArcBraking) { p.SpeedDebt = 0f; p.TurnRefHeading = facing; p.TurnRefTime = now; p.TurnArcRelease = true; }
            p.TurnArcSpeed = 0f;
            p.TurnArcBraking = false;
        }
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

    // ---- without the ball: running and jockeying ----

    /// <summary>The jockey pace, m/s: jockey_speed by jockeying, blended into sprint_jockey_speed by the sprint input.</summary>
    public static float JockeyTopSpeed(PlayerSheet s, float sprint, Tuning t)
    {
        var k = t.Defending;
        float slow = k.JockeySpeed.ForRating(s.Jockeying), fast = k.SprintJockeySpeed.ForRating(s.Jockeying);
        return slow + (fast - slow) * Math.Clamp(sprint, 0f, 1f);
    }

    /// <summary>
    /// Moves an outfield player who has not got the ball one tick: jockeying while the intent holds jockey (see
    /// <see cref="StepJockey"/>), otherwise the run of <see cref="Step(PlayerState, in TickIntent, float, float, bool, Tuning, Pitch?)"/>
    /// with its speed capped at <paramref name="cap"/>. Out of a jockey the part of his velocity off his facing dies away at
    /// jockey_decel instead of vanishing.
    /// </summary>
    /// <param name="face">The point he faces while jockeying (the ball carrier), or null to face the way he steps.</param>
    internal static void StepOffBall(PlayerState p, in TickIntent intent, Vector2? face, float cap, float dt, float now, Tuning t, Pitch? pitch)
    {
        var d = p.Def;
        if (intent.Jockey) { StepJockey(p, intent, face, cap, dt, now, t, pitch); return; }
        if (p.Jockeying)
        {
            var fwd = new Vector2(MathF.Cos(p.Facing), MathF.Sin(p.Facing));
            var v = new Vector2(p.Velocity.X, p.Velocity.Z);
            d.Carry = v - fwd * MathF.Max(0f, Vector2.Dot(v, fwd));
            p.SteerVelocity = new Vector3(d.Carry.X, 0f, d.Carry.Y);
            p.Jockeying = false;
        }
        float len = d.Carry.Length(), step = t.Defending.JockeyDecel * dt;
        d.Carry = len <= step ? Vector2.Zero : d.Carry * ((len - step) / len);
        float top = MathF.Min(TopSpeed(p.Sheet, t), cap);
        // Running, he cannot swerve harder than run_turn_accel: the faster he goes, the slower his heading comes round, so a
        // defender who dives in at speed is beaten by a cut.
        float turn = t.Defending.RunTurnAccel.ForRating(p.Sheet.Agility) / MathF.Max(p.RunSpeed, t.Defending.RunTurnMinSpeed);
        Step(p, intent, dt, now, new MoveAssist(top, 1f + t.Possession.OffBallTurnBonus, Vector2.Zero, HardCap: cap, TurnAbout: true, Carry: d.Carry, MaxTurnRate: turn), t, pitch);
    }

    /// <summary>
    /// Jockeying: his velocity goes to the stick's direction at the jockey pace (whatever way he faces), speeding up no harder
    /// than his acceleration cap and slowing at jockey_decel, and he turns to face <paramref name="face"/> at jockey_turn_deg.
    /// </summary>
    internal static void StepJockey(PlayerState p, in TickIntent intent, Vector2? face, float cap, float dt, float now, Tuning t, Pitch? pitch)
    {
        var k = t.Defending;
        Vector2 stick = intent.Move;
        float mag = stick.Length();
        if (mag > 1f) { stick /= mag; mag = 1f; }
        if (mag < t.Movement.StickDeadZone) mag = 0f;
        float sprint = Math.Clamp(intent.Sprint, 0f, 1f);
        float top = MathF.Min(JockeyTopSpeed(p.Sheet, sprint, t), cap);
        Vector2 desired = mag > 0f ? stick / stick.Length() * (mag * top) : Vector2.Zero;
        var vel = new Vector2(p.Velocity.X, p.Velocity.Z);
        float speed0 = vel.Length();
        float rate = desired.Length() > speed0 ? MathF.Max(AccelCap(p.Sheet, speed0, sprint, t), k.JockeyDecel) : k.JockeyDecel;
        var dv = desired - vel;
        float dl = dv.Length(), max = rate * dt;
        vel = dl <= max ? desired : vel + dv * (max / dl);

        float facing = p.Facing;
        Vector2 look = face is { } f ? f - new Vector2(p.Position.X, p.Position.Z) : stick;
        if (look.LengthSquared() > 1e-6f)
        {
            float diff = WrapAngle(MathF.Atan2(look.Y, look.X) - facing);
            float turn = k.JockeyTurnDeg * MathF.PI / 180f * dt;
            facing = WrapAngle(facing + Math.Clamp(diff, -turn, turn));
        }
        float speed = vel.Length();
        p.Jockeying = true;
        p.Def.Carry = Vector2.Zero;
        p.SpeedDebt = 0f;
        p.TurnRefHeading = facing;
        p.TurnRefTime = now;
        p.MoveTime = now;
        p.Facing = facing;
        p.RunSpeed = speed;
        p.SteerVelocity = Vector3.Zero;
        p.Velocity = new Vector3(vel.X, 0f, vel.Y);
        Vector3 pos = p.Position + p.Velocity * dt;
        if (pitch != null)
        {
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
/// running speed the run is brought down to within the deceleration budget (the post-strike stun), whether a stick turned about brakes the run (without the
/// ball), the steering's part along the run, m/s, added to the legs' target speed (it reaches the running speed through the
/// speed law), and a skill move's step, a velocity (world X, Z) that, unlike <see cref="Steer"/>, does not turn with the body; with ShareCarry its ease-out from CarryFrom is held within the deceleration budget; and the facing to take this tick (NaN: it turns toward the stick as usual; a spin sets it).
/// </summary>
internal readonly record struct MoveAssist(float TopSpeed, float TurnScale, Vector2 Steer, float RunSpeed = float.NaN,
    float HardCap = float.PositiveInfinity, bool TurnAbout = false, float SteerAlong = 0f, Vector2 Carry = default,
    Vector2 CarryFrom = default, bool ShareCarry = false, float Facing = float.NaN, float MaxTurnRate = float.PositiveInfinity);
