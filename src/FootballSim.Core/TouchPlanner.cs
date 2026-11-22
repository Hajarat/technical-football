using System.Numerics;

namespace FootballSim.Core;

/// <summary>A touch as made: its kind, the foot, where the ball was, and the ball velocity it left with.</summary>
/// <param name="DirectionErrorDeg">The direction error actually drawn, degrees.</param>
public readonly record struct TouchResult(ActionKind Kind, Foot Foot, Vector3 ContactPoint, Vector3 BallVelocity, float DirectionErrorDeg);

/// <summary>Which touches the controller may make this tick.</summary>
internal enum TouchStepMode
{
    /// <summary>No touch (a shot without the ball, or the stun after a strike).</summary>
    None,
    /// <summary>Dribble touches, and first contact with a loose ball.</summary>
    Normal,
    /// <summary>A shot pressed with the ball at the feet: the soft touch that keeps it there.</summary>
    SoftOnly,
    /// <summary>A shot under way in possession: only a touch committed before the press (the strike is the next contact).</summary>
    CommittedOnly,
}

/// <summary>
/// The dribble touch controller: a closed loop on the stride clock. Nothing is planned ahead and held. Every tick, before the
/// player moves, it looks for the next swing middle (the moment a foot can play the ball) that is eligible (at least the touch's
/// execution time ahead: the windup, or what execution_time gives for the ball's speed relative to him; and past the cooldown since the last contact) and at which the legs can bring the ball within reach, and steers the
/// player toward it; the legs either follow the stick (the miss trimmed by a sideways step and a small change of the target
/// speed through the speed law) or, when that cannot bring the ball within reach, run to the ball. Its execution time before that swing
/// the touch is committed (a <see cref="BallAction"/> from then on); its kind, direction and push are decided at contact from
/// the stick as it is then. A push is solved so the ball comes back within reach a set number of steps later. A ball out of
/// reach at the contact is simply not played: the legs keep going to it and the next eligible swing plays it. Everything is
/// deterministic; random numbers are drawn only at contact.
/// </summary>
public static class TouchPlanner
{
    // ---- timing ----

    static int Ticks(float seconds, Tuning t) => Math.Max(0, (int)MathF.Round(seconds * t.TickRate));
    internal static int WindupTicks(Tuning t) => Ticks(t.Touch.Windup, t);
    internal static int RecoveryTicks(Tuning t) => Ticks(t.Touch.Recovery, t);

    /// <summary>The ball's speed relative to the player, m/s: the size of the horizontal difference of their velocities.</summary>
    internal static float RelativeBallSpeed(PlayerState p, Ball ball) => TouchModel.Flat(ball.Velocity - p.Velocity).Length();

    /// <summary>
    /// The execution time of a dribble touch planned now, seconds from its commit to its contact: execution_time at the
    /// ball's speed relative to the player (see <see cref="TouchTuning.ExecutionTime"/>).
    /// </summary>
    internal static float ExecutionTime(PlayerState p, Ball ball, Tuning t) => t.Touch.ExecutionTime.Evaluate(RelativeBallSpeed(p, ball));

    /// <summary>
    /// <see cref="ExecutionTime"/> in ticks, at least one: how far ahead of its contact a dribble touch is committed, and (less
    /// the commit slack) the soonest meet the controller looks for.
    /// </summary>
    internal static int ExecutionTicks(PlayerState p, Ball ball, Tuning t) => Math.Max(1, Ticks(ExecutionTime(p, ball, t), t));
    /// <summary>How far ahead the controller looks for an eligible swing, ticks (prediction_horizon).</summary>
    static int HorizonTicks(Tuning t) => Math.Max(1, Ticks(t.Possession.PredictionHorizon, t));

    /// <summary>Sets the phase of the player's current and previous actions for world tick <paramref name="tick"/>.</summary>
    internal static void UpdatePhases(PlayerState p, int tick, Tuning t)
    {
        int win = WindupTicks(t);
        if (p.TouchAction is { } a) a.Phase = a.PhaseAt(tick, win, win);
        if (p.PrevTouchAction is { } b)
        {
            b.Phase = b.PhaseAt(tick, win, win);
            if (b.Phase == ActionPhase.Done) p.PrevTouchAction = null;
        }
        p.ActionContactEta = p.TouchAction is { Pending: true } c ? c.ContactEta(tick, t.Dt) : float.NaN;
    }

    // ---- geometry ----

    /// <summary>
    /// Where a foot meets the ball for a player in this state: the swinging foot's side of the body's centreline by the lateral
    /// foot offset, plus the contact-zone lead along the facing (0.25 m at rest to 0.45 m at top speed); Y is the ball radius.
    /// </summary>
    public static Vector3 ContactPointFor(PlayerState p, Foot foot, Tuning t)
    {
        float top = Movement.TopSpeed(p.Sheet, t);
        float ahead = t.Touch.ContactZoneAhead.Lerp(top > 0f ? p.RunSpeed / top : 0f);
        float c = MathF.Cos(p.Facing), s = MathF.Sin(p.Facing);
        float side = foot == Foot.Right ? 1f : -1f;
        float lat = side * t.Touch.LateralFootOffset;
        return new Vector3(p.Position.X + c * ahead - s * lat, t.Ball.Radius, p.Position.Z + s * ahead + c * lat);
    }

    static float Horizontal(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    static Vector2 FacingDir(PlayerState p) => new(MathF.Cos(p.Facing), MathF.Sin(p.Facing));

    static Vector2 Dir(float a) => new(MathF.Cos(a), MathF.Sin(a));

    // ---- legs ----

    /// <summary>
    /// The pace the legs hold from a shoot press to the strike: the left stick only aims, and the runner keeps his running
    /// speed (as a stick at that pace, sprint held) while the go-get-it legs run on to the ball, so the strike comes where his
    /// next touch would have.
    /// </summary>
    static TickIntent ShotPace(PlayerState p, Tuning t)
    {
        var run = new TickIntent { Sprint = 1f };
        float full = Possession.SpeedCap(p, true, false, run, t) * Movement.ModeFraction(run, p.Sheet, t);
        run.MoveX = full > 1e-3f ? MathF.Min(1f, p.RunSpeed / full) : 0f;
        return run;
    }

    /// <summary>
    /// The go-get-it legs: toward where the ball will be, at the stick's own pace (<paramref name="user"/>; the touch
    /// controller's speed trim slows or speeds it to arrive at the ball on the swing), or faster when the ball runs away: its
    /// own speed away plus the gap over the speed time constant (so he arrives at a pace that lets him play it instead of
    /// running past it), at least 0.8 m/s, up to the off-ball top speed. <paramref name="offBall"/> (a loose ball he goes
    /// for) measures the stick's pace and the cap against the off-ball top speed. A ball against the boards (see
    /// <see cref="AtBoard"/>) within his turn reach and about still is not run at at the stick's pace: he turns to it where he
    /// stands instead of running a circle round it.
    /// </summary>
    static TickIntent ChaseIntent(PlayerState p, Ball ball, in TickIntent user, Tuning t, bool offBall = false, Pitch? pitch = null)
    {
        var rel = new Vector2(ball.Position.X - p.Position.X, ball.Position.Z - p.Position.Z);
        float dist = rel.Length();
        if (dist < 1e-4f) return default;
        Vector2 dir = InterceptDirection(p, ball);
        var bv = new Vector2(ball.Velocity.X, ball.Velocity.Z);
        float away = MathF.Max(0f, Vector2.Dot(bv, rel / dist));
        float cap = offBall ? Movement.TopSpeed(p.Sheet, t) : Possession.SpeedCap(p, true, true, Possession.SpeedStyleLegs, t);
        float stick = MathF.Min(1f, user.Move.Length());
        float stickCap = offBall ? cap : Possession.SpeedCap(p, true, false, user, t);
        float pace = stick < t.Movement.StickDeadZone ? 0f : stickCap * stick * Movement.ModeFraction(user, p.Sheet, t);
        if (dist < t.Touch.TurnReach.Rest + Pitch.PlayerRadius && bv.Length() < BoardStillBall && AtBoard(ball, t, pitch)) pace = 0f;
        float target = MathF.Max(pace, away + MathF.Max(0f, dist - 0.3f) / Movement.TimeConstant(p.Sheet, t));
        float mag = Math.Clamp(target, 0.8f, cap) / MathF.Max(cap, 1e-3f);
        return new TickIntent { MoveX = dir.X * mag, MoveZ = dir.Y * mag, Sprint = 1f };
    }

    /// <summary>
    /// The ground direction to run on to the ball: toward where it will be when the player, holding his speed, has closed the
    /// gap (the ball's own deceleration ignored, the look-ahead kept to 0.3..1.5 s); the facing when the ball is at the feet.
    /// </summary>
    static Vector2 InterceptDirection(PlayerState p, Ball ball)
    {
        var rel = new Vector2(ball.Position.X - p.Position.X, ball.Position.Z - p.Position.Z);
        float dist = rel.Length();
        if (dist < 0.05f) return FacingDir(p);
        var bv = new Vector2(ball.Velocity.X, ball.Velocity.Z);
        float closing = MathF.Max(p.RunSpeed - Vector2.Dot(bv, rel / dist), 1f);
        float look = Math.Clamp(dist / closing, 0.3f, 1.5f);
        var aim = rel + bv * look;
        return aim.LengthSquared() > 1e-6f ? Vector2.Normalize(aim) : FacingDir(p);
    }

    /// <summary>The user's stick turned by <paramref name="turn"/> radians (the predicted turn of a turning stick).</summary>
    static TickIntent Turned(in TickIntent user, float turn)
    {
        if (turn == 0f) return user;
        float c = MathF.Cos(turn), s = MathF.Sin(turn);
        var r = user;
        r.MoveX = user.MoveX * c - user.MoveZ * s;
        r.MoveZ = user.MoveX * s + user.MoveZ * c;
        return r;
    }

    /// <summary>
    /// How far the stick is expected to have turned <paramref name="ahead"/> seconds from now, radians: the smooth stick turn
    /// rate carried on and easing out over stick_lead_time (never longer than the stick has been turning), at most
    /// <see cref="StickLeadMaxDeg"/>. Predictions of the stick-following path use it, so a curving stick is predicted as a
    /// curve, but a thumb that stops turning is not predicted to go on turning.
    /// </summary>
    static float Lead(PlayerState p, float ahead, Tuning t)
    {
        if (p.StickRate == 0f) return 0f;
        float tau = MathF.Min(t.Touch.StickLeadTime, p.StickTurnTicks * t.Dt);
        if (tau <= 0f) return 0f;
        float max = StickLeadMaxDeg * MathF.PI / 180f;
        return Math.Clamp(p.StickRate * tau * (1f - MathF.Exp(-ahead / tau)), -max, max);
    }

    /// <summary>The predicted stick turn never runs further than this, degrees.</summary>
    const float StickLeadMaxDeg = 90f;
    /// <summary>A stick moving more than this in one tick jumped (a flick), degrees: it is not a smooth turn.</summary>
    const float StickJumpDeg = 3f;
    /// <summary>A stick turned further than this from where it settled has changed, degrees.</summary>
    const float StickSettleDeg = 10f;
    /// <summary>A stick changed (or sprint pressed or let go) within this is still turning: a sprint push stays short, seconds.</summary>
    const float StickSettleSeconds = 0.35f;
    /// <summary>Smoothing time of the stick turn rate, seconds.</summary>
    const float StickRateSmoothing = 0.1f;

    /// <summary>Updates the smooth stick turn rate from this tick's stick (starting afresh on a round's first tick).</summary>
    static void TrackStick(PlayerState p, in TickIntent intent, int tick, Tuning t)
    {
        if (tick <= 1)
        {
            p.StickAnglePrev = float.NaN; p.StickRate = 0f; p.StickTurnTicks = 0; p.SpeedDebt = 0f; p.TouchCadenceCount = 0;
            p.PlannerSteer = Vector2.Zero; p.SteerCarry = Vector2.Zero; p.ReverseTime = -100f; p.PostTurnUntil = -100f;
            p.StepsSinceTouch = 0; p.PushSteps = 1; p.Chasing = false; p.ClearForecast();
            p.StickSettledAngle = float.NaN; p.StickChangeTick = -100000; p.SprintPrev = intent.Sprint > 0.5f;
        }
        bool sprint = intent.Sprint > 0.5f;
        if (sprint != p.SprintPrev) p.StickChangeTick = tick;
        p.SprintPrev = sprint;
        Vector2 m = intent.Move;
        if (m.Length() < t.Touch.StickThreshold) p.StickSettledAngle = float.NaN;
        else
        {
            // The stick's last change: a turn of more than StickSettleDeg from where it last settled, smooth or a jump.
            float sa = MathF.Atan2(m.Y, m.X);
            if (float.IsNaN(p.StickSettledAngle)) p.StickSettledAngle = sa;
            else if (MathF.Abs(Movement.WrapAngle(sa - p.StickSettledAngle)) * 180f / MathF.PI > StickSettleDeg)
            {
                p.StickSettledAngle = sa;
                p.StickChangeTick = tick;
            }
        }
        if (m.Length() < t.Touch.StickThreshold) { p.StickAnglePrev = float.NaN; p.StickRate = 0f; p.StickTurnTicks = 0; return; }
        float a = MathF.Atan2(m.Y, m.X);
        if (!float.IsNaN(p.StickAnglePrev))
        {
            float d = Movement.WrapAngle(a - p.StickAnglePrev);
            if (MathF.Abs(d) * 180f / MathF.PI > StickJumpDeg) p.StickRate = 0f;
            else p.StickRate += (d / t.Dt - p.StickRate) * MathF.Min(1f, t.Dt / StickRateSmoothing);
            if (MathF.Abs(p.StickRate) < 1e-3f) p.StickRate = 0f;
        }
        p.StickTurnTicks = p.StickRate == 0f ? 0 : p.StickTurnTicks + 1;
        p.StickAnglePrev = a;
    }

    /// <summary>Counts how long the stick has been released and keeps its last pushed value (starting afresh on a round's first tick).</summary>
    static void TrackRelease(PlayerState p, in TickIntent intent, int tick, Tuning t)
    {
        if (tick <= 1) { p.StickReleasedTicks = 0; p.LastStickMove = Vector2.Zero; }
        if (intent.Move.Length() >= t.Touch.StickThreshold) { p.StickReleasedTicks = 0; p.LastStickMove = intent.Move; }
        else p.StickReleasedTicks++;
    }

    /// <summary>
    /// The stick as the controller reads it in possession: a release no longer than release_debounce reads as the last pushed
    /// stick, so a flick through neutral (a gamepad polled at 60 Hz can read neutral for a tick or two) does not stop the ball.
    /// Not while shoot is held or a shot winds up.
    /// </summary>
    static TickIntent Debounced(PlayerState p, in TickIntent intent, Tuning t)
    {
        if (intent.Move.Length() >= t.Touch.StickThreshold || p.LastStickMove == Vector2.Zero) return intent;
        // While a shot is pressed or winds up the stick at rest is the shot's own (the run holds on to the ball).
        if (intent.ShootHeld || p.ShotAction != null) return intent;
        if (p.StickReleasedTicks > Math.Max(0, (int)MathF.Round(t.Touch.ReleaseDebounce * t.TickRate))) return intent;
        var r = intent;
        r.MoveX = p.LastStickMove.X;
        r.MoveZ = p.LastStickMove.Y;
        return r;
    }

    /// <summary>
    /// Before the player moves this tick: updates the action phases, runs a skill move or a turn at the feet, and otherwise the
    /// controller (see <see cref="Control"/>); returns what the legs follow and the movement modifiers (speed cap and steering).
    /// </summary>
    internal static MoveAssist BeginTick(PlayerState p, in TickIntent rawIntent, Ball ball, bool inPossession, bool live, int tick,
        float now, Tuning t, Pitch? pitch, out TickIntent legs)
    {
        p.CurrentTick = tick;
        TrackRelease(p, rawIntent, tick, t);
        var intent = inPossession ? Debounced(p, rawIntent, t) : rawIntent;
        TrackStick(p, intent, tick, t);
        UpdatePhases(p, tick, t);
        if (!inPossession)
        {
            p.TurnArcSpeed = 0f; p.TurnArcBraking = false;
            p.Chasing = false;
            p.ClearForecast();
            // The steering under way when the ball went (a strike, the keeper taking it) eases out instead of stopping at once.
            p.PlannerSteer = Toward(Rotate(p.PlannerSteer, Movement.WrapAngle(p.Facing - p.SteerFacing)), Vector2.Zero, SteerStep(t));
            p.SteerFacing = p.Facing;
            p.SteerCarry = Toward(p.SteerCarry, Vector2.Zero, SteerStep(t));
            // A loose ball he goes for (see Regain): the go-get-it legs, off the ball.
            legs = p.Regain != RegainMode.None ? ChaseIntent(p, ball, intent, t, offBall: true) : intent;
            float top = Movement.TopSpeed(p.Sheet, t);
            return Stunned(p, now)
                ? new MoveAssist(top * t.Shot.StunSpeedFraction, 1f + t.Possession.OffBallTurnBonus, p.PlannerSteer, HardCap: top * t.Shot.StunSpeedFraction, TurnAbout: true, Carry: p.SteerCarry)
                : new MoveAssist(top, 1f + t.Possession.OffBallTurnBonus, p.PlannerSteer, TurnAbout: true, Carry: p.SteerCarry);
        }

        // The sideways correction under way turns with the body: as the facing turned it keeps its angle to the run instead
        // of swinging into or against it.
        p.PlannerSteer = Rotate(p.PlannerSteer, Movement.WrapAngle(p.Facing - p.SteerFacing));
        p.SteerFacing = p.Facing;
        // A skill move owns the legs while it lasts; on the tick it ends, the controller takes over at once.
        if (p.SkillAction is { } skill)
        {
            p.TurnArcSpeed = 0f; p.TurnArcBraking = false;
            if (tick < skill.EndTick) return SkillTick(p, ball, tick, t, pitch, out legs);
            SkillMoves.Finish(p, intent, ball, tick, now, t);
            UpdatePhases(p, tick, t);
        }

        // The stick reversed with the ball at the feet: the sole turns it at once (a drag-back onto the stick's line); at a run,
        // a braking arc instead.
        if (live && FeetTurn(p, intent, ball, tick, now, t, pitch)) return SkillTick(p, ball, tick, t, pitch, out legs);
        TurnArc(p, intent, ball, live && !Stunned(p, now) && p.ShotAction == null, t);

        Vector2 steer;
        float along = 0f;
        if (live && !Stunned(p, now)) steer = Control(p, intent, ball, tick, now, t, pitch, out legs, out along);
        else
        {
            legs = intent;
            p.Chasing = false;
            p.ClearForecast();
            steer = Toward(p.PlannerSteer, Vector2.Zero, SteerStep(t));
        }
        p.PlannerSteer = steer;
        // A skill move's step eases out (at steer_accel) on top of it.
        Vector2 carryFrom = p.SteerCarry;
        p.SteerCarry = Toward(p.SteerCarry, Vector2.Zero, SteerStep(t));
        float cap = Possession.SpeedCap(p, true, p.Chasing, legs, t);
        if (Stunned(p, now))
        {
            float stun = Movement.TopSpeed(p.Sheet, t) * t.Shot.StunSpeedFraction;
            return new MoveAssist(MathF.Min(cap, stun), 1f, steer, HardCap: stun, Carry: p.SteerCarry);
        }
        // The move's step easing out shares the run's deceleration budget (see Movement.Step).
        return new MoveAssist(cap, 1f, steer, SteerAlong: along, Carry: p.SteerCarry, CarryFrom: carryFrom, ShareCarry: true);
    }

    /// <summary>A skill move's step eases in at this, m/s^2 (a quick plant, not a jolt).</summary>
    const float SkillSteerAccel = 30f;

    static Vector2 Rotate(Vector2 v, float a)
    {
        if (a == 0f || v == Vector2.Zero) return v;
        float c = MathF.Cos(a), s = MathF.Sin(a);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    /// <summary>
    /// A tick of a skill move or a turn at the feet: the move's own legs, speed and step. A turn at the feet (the controller's
    /// own turn, part of the dribble) is kept to a smooth speed: its step eases in from the steering under way at
    /// <see cref="SkillSteerAccel"/>, its running speed changes by at most balance_deceleration, and its step is held back as
    /// far as needed for the ground speed to change no faster than that either. That step is kept as
    /// <see cref="PlayerState.SteerCarry"/>, so after the turn it eases out from there.
    /// </summary>
    static MoveAssist SkillTick(PlayerState p, Ball ball, int tick, Tuning t, Pitch? pitch, out TickIntent legs)
    {
        var m = SkillMoves.BeginTick(p, ball, tick, t, out legs);
        p.Chasing = false;
        p.SetForecast(p.SkillAction is { Made: false } sk && sk.ContactTick >= tick ? sk.ContactTick : -1, p.SkillAction?.Foot ?? Foot.Right,
            p.SkillAction?.ContactPoint ?? Vector3.Zero);
        // A spin's turn moves him by its step alone (see SkillMoves.SpinTick), its ground velocity planned to run on smoothly.
        if (p.SkillAction is { Kind: ActionKind.Spin } spin && tick >= spin.ContactTick)
        {
            p.PlannerSteer = Vector2.Zero;
            p.SteerCarry = StepOffBoards(p, m.Carry, pitch, t);
            return m with { Carry = p.SteerCarry };
        }
        Vector2 from = p.PlannerSteer + p.SteerCarry;
        // A turn at the feet moves the player by his run alone, the body coming round onto the stick (no step against the run
        // that the turning run would then swing into).
        Vector2 aim = p.SkillAction is { FeetTurn: true } ? Vector2.Zero : m.Steer;
        Vector2 steer = Toward(from, aim, MathF.Max(SteerStep(t), SkillSteerAccel * t.Dt));
        p.PlannerSteer = Vector2.Zero;
        // A turn at the feet, the ball roll, fake stop and fake cut are kept to a smooth speed (below); the step-over and body
        // feint pace themselves, and the drag-back keeps its own stop (shed within the budget, the player would run on past the
        // ball he pulls back).
        if (!Smoothed(p.SkillAction))
        {
            // A skill move of the player's steps as it was built, its step eased in from the steering under way; it is kept as
            // the carry, which the controller eases out from there once the move ends.
            // (A drag-back's step is shaped to rise from and fall to rest itself; its sole's reach for the ball before the
            // contact is taken as it is, and from the contact the step left from that reach eases onto the shaped step, so the
            // reach does not vanish in one tick.)
            if (p.SkillAction.Kind == ActionKind.DragBack)
                steer = tick < p.SkillAction.ContactTick ? m.Steer : Toward(from, m.Steer, MathF.Max(SteerStep(t), SkillSteerAccel * t.Dt));
            steer = StepOffBoards(p, steer, pitch, t);
            p.SteerCarry = steer;
            return m with { Steer = Vector2.Zero, Carry = steer };
        }
        float budget = t.Movement.BalanceDeceleration * t.Dt;
        float run0 = p.RunSpeed, run = Math.Clamp(m.RunSpeed, run0 - budget, run0 + budget);
        Vector2 fwd = FacingDir(p);
        float v0 = new Vector2(p.Velocity.X, p.Velocity.Z).Length();
        // The ground speed with this tick's run and step; the step is moved back toward the last one until it changes no faster
        // than the budget (the run alone always keeps within it).
        float Change(Vector2 s) => MathF.Abs((fwd * run + s).Length() - v0);
        if (Change(steer) > budget)
        {
            float lo = 0f, hi = 1f;
            for (int i = 0; i < 12; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (Change(from + (steer - from) * mid) > budget) hi = mid; else lo = mid;
            }
            steer = from + (steer - from) * lo;
        }
        steer = StepOffBoards(p, steer, pitch, t);
        p.SteerCarry = steer;
        return m with { Steer = Vector2.Zero, Carry = steer, RunSpeed = run };
    }

    /// <summary>
    /// A move's step with its part into a board he is near cut as his run is at the boards (see Movement.Step): to what he can
    /// shed at balance_deceleration before he reaches it and still arrive at wall_arrive_speed, so a step into a board slows
    /// as he nears it instead of the board stopping him in one tick.
    /// </summary>
    static Vector2 StepOffBoards(PlayerState p, Vector2 step, Pitch? pitch, Tuning t) =>
        pitch == null || step == Vector2.Zero ? step
            : pitch.WallStepCap(new Vector2(p.Position.X, p.Position.Z) + step * t.Dt, step, t.Movement.WallArriveSpeed, t.Movement.BalanceDeceleration);

    /// <summary>The moves whose run and step <see cref="SkillTick"/> keeps within the deceleration budget.</summary>
    static bool Smoothed(BallAction? a) =>
        a is { FeetTurn: true } or { Kind: ActionKind.BallRoll or ActionKind.FakeStop or ActionKind.FakeCut or ActionKind.Elastico or ActionKind.Spin or ActionKind.HeelToHeel };

    /// <summary>True during the stun after a strike: the shooter is slowed and makes no touches.</summary>
    internal static bool Stunned(PlayerState p, float now) => now < p.StunUntil;

    /// <summary>
    /// Starts a turn at the feet when the stick points at least feet_turn_deg from the ball's travel (the facing for a still
    /// ball) while running, with the ball low and within feet_turn_reach of the body, and no touch already winding up (that one
    /// turns the ball sooner): the sole pulls the ball onto the stick's line at once, beside or behind the planted foot too,
    /// instead of waiting for a swing in front of the foot (see <see cref="SkillMoves.StartFeetTurn"/>). Not after a knock-on,
    /// during a shot or the stun, or within a drag-back's cooldown. Nor when the stick's line from the ball runs into a board
    /// within the sole's pull: the sole would press it into the board; a touch plays it instead.
    /// </summary>
    static bool FeetTurn(PlayerState p, in TickIntent intent, Ball ball, int tick, float now, Tuning t, Pitch? pitch)
    {
        var k = t.Touch;
        Vector2 stick = intent.Move;
        if (stick.Length() < k.StickThreshold || p.SkillAction != null || p.ShotAction != null || Stunned(p, now)) return false;
        if (now < p.SkillCooldownUntil || p.SkillExitTick == tick || Possession.CanLoseByDistance(p)) return false;
        if (p.RunSpeed <= k.ExecutionMinSpeed || p.RunSpeed > k.FeetTurnMaxSpeed || ball.Position.Y > t.Skill.MaxBallHeight) return false;
        if (p.TouchAction is { Pending: true }) return false;
        if (Horizontal(ball.Position, p.Position) > k.FeetTurnReach) return false;
        var wanted = new Vector3(stick.X, 0f, stick.Y);
        if (TouchModel.TurnAngleDeg(p, ball, wanted) < k.FeetTurnDeg) return false;
        if (pitch != null)
        {
            var from = new Vector3(ball.Position.X, t.Ball.Radius, ball.Position.Z);
            if (pitch.SweepBoards(from, from + Vector3.Normalize(wanted) * t.Skill.FeetTurnDistance, t.Ball.Radius, out _)) return false;
        }
        SkillMoves.StartFeetTurn(p, Vector2.Normalize(stick), ball, tick, now, t);
        return true;
    }

    /// <summary>
    /// The braking arc: above feet_turn_max_speed, a stick turned feet_turn_deg or more from his travel arms it at
    /// turn_arc_keep of the speed he has then (never below turn_arc_min_speed). He runs on to the ball, and from the touch that
    /// turns it round (see <see cref="Execute"/>) brakes to that speed within the deceleration budget and keeps it while he comes
    /// round with the ball, until he faces within turn_arc_done_deg of the stick (see <see cref="Movement"/>); the stick
    /// released, a skill move, a shot, the ball lost or a ball he goes after running away faster than the arc's speed ends it.
    /// Then he speeds up along the stick as usual.
    /// </summary>
    static void TurnArc(PlayerState p, in TickIntent intent, Ball ball, bool live, Tuning t)
    {
        var k = t.Touch;
        Vector2 stick = intent.Move;
        float mag = stick.Length();
        if (!live || p.TurnArcSpeed > 0f && mag < k.StickThreshold) { p.TurnArcSpeed = 0f; p.TurnArcBraking = false; return; }
        if (p.TurnArcSpeed > 0f)
        {
            p.TurnArcStick = stick / mag;
            // Braking, he never lets the ball get away from him: a ball he goes after that runs away faster than the arc's speed
            // ends it, and he speeds up after it.
            var rel = new Vector2(ball.Position.X - p.Position.X, ball.Position.Z - p.Position.Z);
            float d = rel.Length();
            if (p.TurnArcBraking && p.Chasing && d > 1e-3f && Vector2.Dot(new Vector2(ball.Velocity.X, ball.Velocity.Z), rel / d) > p.TurnArcSpeed)
            {
                p.TurnArcSpeed = 0f;
                p.TurnArcBraking = false;
                p.TurnArcRelease = true;
            }
            return;
        }
        if (mag < k.StickThreshold || p.RunSpeed <= k.FeetTurnMaxSpeed) return;
        var v = new Vector2(p.Velocity.X, p.Velocity.Z);
        float speed = v.Length();
        if (speed < 1e-3f || MathF.Acos(Math.Clamp(Vector2.Dot(v / speed, stick / mag), -1f, 1f)) * 180f / MathF.PI < k.FeetTurnDeg) return;
        p.TurnArcSpeed = MathF.Max(k.TurnArcKeep * speed, k.TurnArcMinSpeed);
        p.TurnArcBraking = false;
        p.TurnArcStick = stick / mag;
    }

    /// <summary>
    /// A drag-back asked for (the gesture or its key) within drag_back_over_feet_turn of the controller's own turn at the feet
    /// starting replaces it: the turn is dropped, so the drag-back starts in its place.
    /// </summary>
    internal static void YieldFeetTurn(PlayerState p, int tick, Tuning t)
    {
        if (p.SkillAction is not { FeetTurn: true } turn) return;
        if (tick - turn.StartTick > (int)MathF.Round(t.Skill.DragBackOverFeetTurn * t.TickRate)) return;
        turn.Phase = ActionPhase.Done;
        p.SkillAction = null;
    }

    // ---- the controller ----

    /// <summary>A swing the controller can play the ball on.</summary>
    struct Target
    {
        public bool Found;
        /// <summary>Ticks from now to the contact (the player moves J + 1 more times before it, the ball J times).</summary>
        public int J;
        public Foot Foot;
        /// <summary>Where the ball centre is predicted at the contact.</summary>
        public Vector3 Ball;
        /// <summary>The ball's miss from the contact point at the contact, without steering (world X, Z).</summary>
        public Vector2 Miss;
        /// <summary>The facing predicted at the contact, radians.</summary>
        public float Facing;
        /// <summary>The legs' target speed at the contact (the speed trim's reference), m/s.</summary>
        public float LegsSpeed;
        public bool Chase;
        /// <summary>The go-get-it legs held to the stick's speed trim (they may not slow the run more than the stick's legs).</summary>
        public bool Held;
        /// <summary>The ball from the body (the feet) at the contact (world X, Z), and the turn reach there, metres.</summary>
        public Vector2 Body;
        public float TurnReach;
        /// <summary>The stride clock's phase at the contact.</summary>
        public float Phase;
        /// <summary>
        /// The go-get-it legs' speed-up bound, m/s: the least room between their target speed and the off-ball top speed on
        /// the run to the contact (unbounded unless the search counted it).
        /// </summary>
        public float Headroom;
    }

    /// <summary>
    /// The swings since the last contact before a touch may be played: the last push's steps (steps_per_touch, see
    /// <see cref="NextSteps"/>); with a demand (the stick turned past the execution angle off the ball's travel, the stick
    /// released for a stop, a skill gesture waiting, a shot's strike) the early-execution fraction of them, at least one.
    /// </summary>
    static int Cooldown(PlayerState p, bool demand, float turnDeg, Tuning t)
    {
        int n = Math.Max(1, p.PushSteps);
        if (!demand) return n;
        return Math.Max(1, (int)MathF.Round(EarlyFraction(p, turnDeg, t) * n));
    }

    /// <summary>
    /// The controller, every tick in possession (no skill move): finds the swing to play the ball on and the legs that get there,
    /// steers toward it, commits the touch its execution time (see <see cref="ExecutionTicks"/>) before its contact, and publishes the forecast. Returns this tick's
    /// sideways steering; <paramref name="along"/> is the speed trim.
    /// <list type="bullet">
    /// <item>The stick released with the ball settling where the player stops (or after a knock-on): no touch, the legs follow
    /// the stick.</item>
    /// <item>A committed touch keeps its swing and the legs it was committed on.</item>
    /// <item>A shot under way: from the press the left stick only aims and the go-get-it legs run on to the ball; the strike is
    /// the next contact of the foot chosen for the shot, on that foot's first eligible swing at or after the earliest strike
    /// tick, and is committed like a touch, one windup ahead (once released, unless the ball is held at the feet for a strike
    /// at the earliest tick).</item>
    /// <item>Otherwise the earliest eligible swing of the stick-following legs at which the steering brings the ball within
    /// reach; with a demand, the go-get-it legs instead when they play it on an earlier swing; without one, when the
    /// stick-following legs find none past the cooldown, the earliest swing of either from the first swing on.</item>
    /// </list>
    /// </summary>
    static Vector2 Control(PlayerState p, in TickIntent intent, Ball ball, int tick, float now, Tuning t, Pitch? pitch,
        out TickIntent legs, out float along)
    {
        var k = t.Touch;
        along = 0f;
        bool stickActive = intent.Move.Length() >= k.StickThreshold;
        bool restricted = Possession.CanLoseByDistance(p);
        bool shot = p.ShotAction != null;
        var committed = p.TouchAction is { Pending: true } pa ? pa : null;
        if (committed == null && !stickActive && !shot && (restricted || !TouchModel.NeedsStop(p, ball, t)))
        {
            legs = intent;
            p.Chasing = false;
            p.ClearForecast();
            return Toward(p.PlannerSteer, Vector2.Zero, SteerStep(t));
        }

        float turnDeg = stickActive ? TouchModel.TurnAngleDeg(p, ball, new Vector3(intent.Move.X, 0f, intent.Move.Y)) : 0f;
        // A turn is the stick turned off the ball's travel and off the run: a ball a few degrees off a held stick (the rated
        // direction error) is brought back by the next touch at its own cadence, not hurried.
        float offRun = stickActive ? MathF.Abs(Movement.WrapAngle(MathF.Atan2(intent.Move.Y, intent.Move.X) - p.Facing)) * 180f / MathF.PI : 0f;
        bool turn = stickActive && !restricted && turnDeg > k.ExecutionAngle && offRun > k.ExecutionAngle && p.RunSpeed > k.ExecutionMinSpeed;
        bool demand = turn || !stickActive || shot || p.SkillBufTick >= 0;
        // A turn, a stop or a close-control touch may play the ball anywhere within the turn reach of the body; so may any touch
        // or strike of a ball against the boards, where he cannot always stand to bring it in front of his foot.
        bool atBoard = AtBoard(ball, t, pitch);
        bool turnish = turn || !stickActive || TouchModel.ModeFor(intent, t) == TouchMode.CloseControl || atBoard;
        Target tg;
        float openShare = 0f;
        // The touch's execution time by the ball's speed relative to him: a fast ball is met by a touch thrown at it sooner
        // (a shorter commit lead, so an earlier swing can play it), not by the legs going after it. A strike keeps the windup.
        int exec = shot ? WindupTicks(t) : ExecutionTicks(p, ball, t);
        int minJ = shot ? StrikeMinJ(p, p.ShotEarliestTick, p.ShotNotBeforeTick, tick, now, t)
            : Math.Max(Math.Max(0, exec - CommitSlack), (int)MathF.Ceiling((k.EarlyMinInterval - (now - p.LastTouchTime)) / t.Dt - 1e-3f));
        bool strikeCommitted = shot && p.StrikeContactTick >= 0;
        if (committed != null)
        {
            // The foot is drawn back: the legs it was committed on carry it to the contact (in a windup the steering has no
            // time left to win by changing them). For a turn or cut the body opens toward the committed direction as far as
            // the contact still holds (the foot plays the ball across the opening body).
            int j = Math.Max(0, committed.ContactTick - tick);
            var user = shot ? ShotPace(p, t) : intent;
            bool chase = shot || committed.ChaseLegs;
            tg = At(p, user, ball, j, committed.Foot, chase, now, t, pitch);
            if (committed.Kind is ActionKind.Turn or ActionKind.Cut && committed.CommitMove != Vector2.Zero)
                foreach (float share in OpenShares)
                {
                    var q = At(p, user, ball, j, committed.Foot, chase, now, t, pitch, committed.CommitMove, share);
                    if (Residual(p, q, now, t, 1f, true) <= k.ContactZoneRadius) { tg = q; openShare = share; break; }
                }
        }
        else if (strikeCommitted)
        {
            // The committed strike: the go-get-it legs it was committed on carry the striking foot to it.
            tg = At(p, ShotPace(p, t), ball, Math.Max(0, p.StrikeContactTick - tick), p.StrikeContactFoot, true, now, t, pitch);
        }
        else
        {
            // Never sooner than early_min_interval after the last touch (minJ), and at least the execution time ahead less the slack.
            int h = HorizonTicks(t);
            int need = Cooldown(p, demand, turn ? turnDeg : 90f, t);
            if (shot) tg = StrikeSearch(p, ball, need, minJ, Math.Max(h, minJ + h), now, t, pitch, atBoard, p.ShotFootLock);
            else if (demand)
            {
                // The stick's legs where they cost no time (the body already turns to the stick), else go and get it sooner.
                tg = Search(p, intent, ball, chase: false, need, minJ, h, now, t, pitch, turnish: turnish, board: atBoard);
                // (Leaving the stick's legs for the go-get-it legs needs a gain of a few ticks, so they do not flick between.)
                var c = Search(p, intent, ball, chase: true, need, minJ, tg.Found ? tg.J - (p.Chasing ? 1 : LegsSwitchTicks) : h, now, t, pitch, turnish: turnish, board: atBoard);
                if (c.Found) tg = c;
            }
            else
            {
                // The step the push was solved for if half the trim reaches it; a push far enough off that it needs more is
                // played sooner, where its miss is smaller.
                // At a sprint the push runs out and comes back: its step is kept while the full trim reaches it.
                float comfort = TouchModel.ModeFor(intent, t) == TouchMode.Sprint ? 1f : ComfortTrim;
                tg = Search(p, intent, ball, chase: false, need, minJ, h, now, t, pitch, trim: comfort, turnish: turnish, firstOnly: true);
                // A push that ran short or wide of its meet: the first step that reaches it on the stick's legs; else go and get
                // it without slowing more than the stick's legs may; else the stick's legs with the leg stretched (a mis-hit
                // costs a stretched touch, not a slowdown); else go and get it however.
                if (!tg.Found) tg = Search(p, intent, ball, chase: false, 1, minJ, h, now, t, pitch, turnish: turnish, board: atBoard);
                if (!tg.Found && stickActive) tg = Search(p, intent, ball, chase: true, 1, minJ, h, now, t, pitch, held: true, turnish: turnish, board: atBoard);
                if (!tg.Found && stickActive) tg = Search(p, intent, ball, chase: false, 1, minJ, h, now, t, pitch, k.StretchReach, turnish: turnish, board: atBoard);
                // The go-get-it legs within 15% of the pace, stretching for it, before they may run at any speed.
                if (!tg.Found && stickActive) tg = Search(p, intent, ball, chase: true, 1, minJ, h, now, t, pitch, k.StretchReach, held: true, turnish: turnish, board: atBoard);
                if (!tg.Found) tg = Search(p, intent, ball, chase: true, 1, minJ, h, now, t, pitch, turnish: turnish, board: atBoard);
            }
        }

        p.Chasing = !tg.Found || tg.Chase;
        legs = p.Chasing ? ChaseIntent(p, ball, shot ? ShotPace(p, t) : intent, t, pitch: pitch) : intent;
        if (openShare > 0f) legs = Opened(legs, committed!.CommitMove, openShare);
        if (!tg.Found)
        {
            p.ClearForecast();
            return Toward(p.PlannerSteer, Vector2.Zero, SteerStep(t));
        }
        // A demand is played sooner where the stride can be quickened (at most gait_nudge) to bring the swing onto the ball (not
        // a shot's strike: it is the first swing from the earliest strike tick on, often steps away, and the stride is left to run).
        if (committed == null && !shot && demand && tg.J > exec) Nudge(p, intent, ball, ref tg, shot, turnish, minJ, exec, now, t, pitch);
        Vector2 steer = SteerFor(p, tg, now, t, out along);
        // A skill gesture waiting for the ball: no touch is committed (the move plays the ball when it is reached). During a
        // shot no dribble touch is committed: its strike is the next contact.
        bool gesture = p.SkillBufTick >= 0 && !shot;
        if (committed == null && !shot && !gesture && tg.J <= exec) committed = Commit(p, intent, ball, tg, tick, t);
        else if (shot && committed == null && !strikeCommitted && p.ShotCommitAllowed && tg.J <= WindupTicks(t)) CommitStrike(p, tg, tick);
        if (committed != null) committed.ContactPoint = new Vector3(tg.Ball.X, t.Ball.Radius, tg.Ball.Z);
        p.SetForecast(tick + tg.J, tg.Foot, new Vector3(tg.Ball.X, t.Ball.Radius, tg.Ball.Z));
        return steer;
    }

    /// <summary>
    /// Quickens the stride (the stride clock's lock, at most <see cref="GaitNudge"/> faster) so the target's swing middle comes
    /// sooner, no nearer than the touch's execution time (<paramref name="exec"/> ticks), if the ball is still within reach
    /// there; otherwise leaves the stride alone.
    /// </summary>
    static void Nudge(PlayerState p, in TickIntent intent, Ball ball, ref Target tg, bool shot, bool turnish, int minJ, int exec, float now,
        Tuning t, Pitch? pitch)
    {
        int j = Math.Max(Math.Max(exec, minJ), (int)MathF.Ceiling(tg.J / (1f + GaitNudge)));
        if (j >= tg.J || p.GaitLockTicks > 0) return;
        float s0 = p.GaitPhase;
        p.GaitLockTicks = j + 1;
        p.GaitLockTarget = s0 + Gait.StepsToSwingMiddle(s0, tg.Foot);
        var q = At(p, shot ? ShotPace(p, t) : intent, ball, j, tg.Foot, tg.Chase, now, t, pitch);
        q.Held = tg.Held;
        if (Residual(p, q, now, t, 1f, turnish) <= t.Touch.ContactZoneRadius) { tg = q; return; }
        p.GaitLockTicks = 0;
    }

    /// <summary>
    /// The strike's contact: the first tick that meets the ball (the first eligible contact, not the best of its window) on a
    /// swing of the foot chosen for the shot (<paramref name="foot"/>; null: whichever foot is swinging there): the strike waits
    /// for that foot, up to a step longer than the next touch would have taken. Its speed-up is bounded by the room the
    /// go-get-it legs leave on the way (after a push they run at their cap until they close on the ball), and it is planned
    /// <see cref="StrikeMargin"/> inside the contact zone, so the forecast published from the press is a contact the run can
    /// make rather than the edge of one, which slid later every few ticks and then a whole swing.
    /// </summary>
    static Target StrikeSearch(PlayerState p, Ball ball, int need, int minJ, int maxJ, float now, Tuning t, Pitch? pitch, bool atBoard,
        Foot? foot) =>
        Search(p, ShotPace(p, t), ball, chase: true, need, minJ, maxJ, now, t, pitch, t.Touch.ContactZoneRadius - StrikeMargin,
            turnish: atBoard, firstTick: true, board: atBoard, headroom: true, only: foot);

    /// <summary>
    /// The fewest ticks ahead a strike may be planned: the wind-up less the commit slack, the interval after the last touch, the
    /// shot's earliest strike tick, and a tick before the strike estimate already published (it may move later, never earlier).
    /// </summary>
    static int StrikeMinJ(PlayerState p, int earliestTick, int notBeforeTick, int tick, float now, Tuning t)
    {
        int minJ = Math.Max(Math.Max(0, WindupTicks(t) - CommitSlack),
            (int)MathF.Ceiling((t.Touch.EarlyMinInterval - (now - p.LastTouchTime)) / t.Dt - 1e-3f));
        if (earliestTick >= 0) minJ = Math.Max(minJ, earliestTick - tick);
        if (notBeforeTick >= 0) minJ = Math.Max(minJ, notBeforeTick - tick);
        return minJ;
    }

    /// <summary>
    /// For the choice of the striking foot: the tick of <paramref name="foot"/>'s earliest strike contact, searched as the
    /// controller searches the strike (the go-get-it legs, never before <paramref name="earliestTick"/> nor before
    /// <paramref name="notBeforeTick"/>, a tick before the strike estimate already published, or -1), or -1 when that foot has
    /// none within the horizon. Read-only: nothing is committed.
    /// </summary>
    internal static int StrikeTickFor(PlayerState p, Ball ball, Foot foot, int earliestTick, int notBeforeTick, int tick, float now, Tuning t, Pitch? pitch)
    {
        int h = HorizonTicks(t), minJ = StrikeMinJ(p, earliestTick, notBeforeTick, tick, now, t);
        bool atBoard = AtBoard(ball, t, pitch);
        var tg = StrikeSearch(p, ball, Cooldown(p, true, 90f, t), minJ, Math.Max(h, minJ + h), now, t, pitch, atBoard, foot);
        return tg.Found ? tick + tg.J : -1;
    }

    /// <summary>A strike's contact is planned this far inside the contact zone, metres.</summary>
    const float StrikeMargin = 0.02f;

    /// <summary>A ball against the boards slower than this counts as about still for the go-get-it legs, m/s.</summary>
    const float BoardStillBall = 1f;

    /// <summary>
    /// True when the ball is so close to a board that he may not be able to stand where it is in front of his foot: closer to
    /// it than his body's radius, the contact zone's lead at rest and its radius.
    /// </summary>
    static bool AtBoard(Ball ball, Tuning t, Pitch? pitch) =>
        pitch != null && pitch.BoardGap(new Vector2(ball.Position.X, ball.Position.Z))
            <= Pitch.PlayerRadius + t.Touch.ContactZoneAhead.Rest + t.Touch.ContactZoneRadius;

    /// <summary>
    /// Commits the shot's strike on the target's swing, one windup before its contact: its contact tick and foot (the foot
    /// chosen for the shot, in swing there) are fixed, and the stride clock is held so the contact falls in that foot's contact window, as for a touch.
    /// </summary>
    static void CommitStrike(PlayerState p, in Target tg, int tick)
    {
        p.StrikeContactTick = tick + tg.J;
        p.StrikeContactFoot = tg.Foot;
        float s0 = p.GaitPhase, d = tg.Phase - s0;
        if (d < 0f) d += 2f;
        p.GaitLockTicks = tg.J + 1;
        p.GaitLockTarget = s0 + d;
    }

    /// <summary>
    /// True when <paramref name="foot"/> can play the ball now: the player standing, or that foot swinging inside its contact
    /// window (with the slack a committed contact keeps).
    /// </summary>
    internal static bool FootInWindow(PlayerState p, Foot foot) =>
        !p.IsStepping || p.SwingFoot == foot && p.SwingProgress >= WindowFrom - WindowSlack && p.SwingProgress <= WindowTo + WindowSlack;

    /// <summary>The shares of the way to a committed turn's direction the body may open during the windup, tried largest first.</summary>
    static readonly float[] OpenShares = { 1f, 0.66f, 0.33f };

    /// <summary>How much faster the stride may run to bring a swing onto the ball for a demand (+25%).</summary>
    const float GaitNudge = 0.25f;

    /// <summary>
    /// The share of the speed trim the step a jog or close-control push was solved for may ask for before an earlier step is
    /// taken (a sprint push's step is kept while the full trim reaches it).
    /// </summary>
    const float ComfortTrim = 0.25f;

    /// <summary>A swing found up to this many ticks inside the windup may still be committed (the stride clock moves with the speed).</summary>
    const int CommitSlack = 2;

    /// <summary>
    /// Commits the touch on the target's swing: a <see cref="BallAction"/> from now to its contact. Its foot, kind and surface
    /// are decided here from the stick as it is now and do not change before the contact (the view swings that surface of the
    /// boot onto the ball over the windup): a knock-on when one was asked for, a stop with
    /// the stick released, else a dribble touch, a turn with the stick past the execution angle off the ball's travel, a cut
    /// past cut_deg. The stride clock is held over the windup so the contact falls in the foot's contact window.
    /// </summary>
    static BallAction Commit(PlayerState p, in TickIntent intent, Ball ball, in Target tg, int tick, Tuning t)
    {
        var k = t.Touch;
        bool stickActive = intent.Move.Length() >= k.StickThreshold;
        ActionKind kind;
        float turnSign = 0f;
        if (!stickActive) kind = ActionKind.Stop;
        else if (p.KnockOnRequested) kind = ActionKind.KnockOn;
        else
        {
            kind = ModeKind(TouchModel.ModeFor(intent, t));
            float turnDeg = TouchModel.TurnAngleDeg(p, ball, new Vector3(intent.Move.X, 0f, intent.Move.Y));
            if (p.RunSpeed > k.ExecutionMinSpeed && turnDeg > k.ExecutionAngle) kind = ActionKind.Turn;
            if (turnDeg > k.CutDeg && p.RunSpeed > k.CutMinSpeed) kind = ActionKind.Cut;
            turnSign = TurnSign(new Vector3(intent.Move.X, 0f, intent.Move.Y), ball);
        }
        var a = new BallAction
        {
            Kind = kind,
            Foot = tg.Foot,
            Surface = SurfaceFor(kind, tg.Foot, turnSign),
            PlanTick = tick,
            ContactTick = tick + tg.J,
            ContactPoint = new Vector3(tg.Ball.X, t.Ball.Radius, tg.Ball.Z),
            ChaseLegs = tg.Chase,
            CommitMove = stickActive ? intent.Move : Vector2.Zero,
        };
        // The stride clock is held to the predicted phase at the contact over the windup (the speed the steering changes would
        // move it a little).
        float s0 = p.GaitPhase, d = tg.Phase - s0;
        if (d < 0f) d += 2f;
        p.GaitLockTicks = tg.J + 1;
        p.GaitLockTarget = s0 + d;
        Install(p, a, tick, t);
        return a;
    }

    /// <summary>The side a ball is turned to toward <paramref name="wanted"/> against its travel (it picks the foot's inside or outside).</summary>
    static float TurnSign(Vector3 wanted, Ball ball) => TurnSign(wanted, ball.Velocity);
    internal static float TurnSign(Vector3 wanted, Vector3 ballVelocity) =>
        MathF.Sign(Movement.WrapAngle(MathF.Atan2(wanted.Z, wanted.X) - MathF.Atan2(ballVelocity.Z, ballVelocity.X)));

    /// <summary>The stick a committed touch is played with: the stick now, or the stick at the commit when it has been released since.</summary>
    static TickIntent CommittedStick(BallAction a, in TickIntent intent, Tuning t)
    {
        var r = intent;
        if (a.CommitMove == Vector2.Zero || intent.Move.Length() >= t.Touch.StickThreshold) return r;
        r.MoveX = a.CommitMove.X;
        r.MoveZ = a.CommitMove.Y;
        return r;
    }

    /// <summary>
    /// A committed touch's push direction (ground, unit) turned back to within <see cref="CommitClampDeg"/> of the stick at the
    /// commit: what the committed surface can play without the boot turning over. A larger stick change is answered by the
    /// next touch.
    /// </summary>
    static Vector3 ClampToCommit(BallAction a, Vector3 dir)
    {
        Vector2 c = a.CommitMove;
        if (c == Vector2.Zero) return dir;
        float ca = MathF.Atan2(c.Y, c.X), d = Movement.WrapAngle(MathF.Atan2(dir.Z, dir.X) - ca), max = CommitClampDeg * MathF.PI / 180f;
        if (MathF.Abs(d) <= max) return dir;
        a.Clamped = true;
        float ang = ca + MathF.Sign(d) * max;
        return new Vector3(MathF.Cos(ang), 0f, MathF.Sin(ang));
    }

    /// <summary>How far the stick may turn from where a touch was committed and still steer that touch, degrees.</summary>
    const float CommitClampDeg = 30f;

    /// <summary>
    /// Predicts the player on the legs (the stick, carried on by its turn rate, or the go-get-it legs) without steering, and the
    /// free ball, and returns the contact on the first step (at least <paramref name="minJ"/> ticks ahead, the
    /// <paramref name="need"/>-th step begun since the last contact or later) whose contact window has a tick at which the
    /// steering brings the ball within reach (see <see cref="Residual"/>): of that window's ticks, the one that meets the ball
    /// best. None within <paramref name="maxJ"/> ticks: not found. <paramref name="turnish"/>: a turn, cut, stop or close-control
    /// touch, which may also play the ball anywhere within the turn reach of the body. <paramref name="headroom"/>: the go-get-it
    /// legs' speed-up is bounded by the least room above their target speed on the way (see <see cref="Target.Headroom"/>).
    /// <paramref name="only"/>: contacts on that foot's swings alone (a strike's chosen foot).
    /// </summary>
    static Target Search(PlayerState p, in TickIntent user, Ball ball, bool chase, int need, int minJ, int maxJ, float now, Tuning t,
        Pitch? pitch, float tol = 0f, bool held = false, float trim = 1f, bool turnish = false, bool firstOnly = false, bool firstTick = false,
        bool board = false, bool headroom = false, Foot? only = null)
    {
        var c = p.Scratch();
        var b = ball.ScratchA();
        int steps = p.StepsSinceTouch;
        float cap = Possession.SpeedCap(p, true, chase, chase ? Possession.SpeedStyleLegs : user, t);
        float tn = now;
        if (tol <= 0f) tol = p.Chasing && !chase ? ReachableBackTol : t.Touch.ContactZoneRadius;
        Target best = default;
        float bestRes = float.MaxValue;
        bool looked = false;
        // The speed law moves the target with the speed-up no higher than the off-ball top speed (see Movement.Step).
        float ceiling = MathF.Max(cap, Movement.TopSpeed(p.Sheet, t)), room = float.PositiveInfinity;
        // the highest ball the touch to come plays: its mode's profile
        var userMode = TouchModel.ModeFor(user, t);
        float maxHeight = TouchModel.Profile(ModeKind(userMode), userMode, t).MaxBallHeight;
        for (int i = 0; i <= maxJ; i++)
        {
            if (i > 0) tn += t.Dt;
            var legs = chase ? ChaseIntent(c, b, user, t, pitch: pitch) : Turned(user, Lead(p, tn - now, t));
            if (headroom) room = MathF.Min(room, MathF.Max(0f, ceiling - cap * MathF.Min(1f, legs.Move.Length()) * Movement.ModeFraction(legs, c.Sheet, t)));
            Movement.Step(c, legs, t.Dt, tn, new MoveAssist(cap, 1f, Vector2.Zero), t, pitch);
            if (c.StepBegun)
            {
                steps++;
                if (best.Found) return best;   // the window of the step before held a contact
            }
            bool inWindow = c.IsStepping && InWindow(c.SwingProgress);
            if (inWindow && steps >= need && i >= minJ && b.Position.Y <= maxHeight && (only == null || c.SwingFoot == only))
            {
                looked = true;
                var tg = Make(c, b, c.SwingFoot, i, legs, chase, t);
                tg.Held = held;
                tg.Headroom = room;
                float res = Residual(p, tg, now, t, trim, turnish, board);
                if (res <= tol && res < bestRes) { best = tg; bestRes = res; if (firstTick) return best; }
            }
            else if (best.Found || firstOnly && looked) return best;
            b.Step(t.Dt, null, null);
        }
        return best;
    }

    /// <summary>The share of a swing in which its foot can play the ball: swing progress from here ...</summary>
    const float WindowFrom = 0.3f;
    /// <summary>... to here (0.5 is the swing middle).</summary>
    const float WindowTo = 0.8f;
    /// <summary>A committed contact still lands this far outside the window (the stride clock moves with the speed).</summary>
    const float WindowSlack = 0.08f;

    static bool InWindow(float progress) => progress >= WindowFrom && progress <= WindowTo;

    /// <summary>
    /// The prediction for a committed contact <paramref name="j"/> ticks ahead on the given legs; with
    /// <paramref name="openShare"/> above zero the legs' direction is turned that share of the way to <paramref name="open"/>
    /// (the body opening toward a committed turn).
    /// </summary>
    static Target At(PlayerState p, in TickIntent user, Ball ball, int j, Foot foot, bool chase, float now, Tuning t, Pitch? pitch,
        Vector2 open = default, float openShare = 0f)
    {
        var c = p.Scratch();
        var b = ball.ScratchA();
        float cap = Possession.SpeedCap(p, true, chase, chase ? Possession.SpeedStyleLegs : user, t);
        float tn = now;
        TickIntent legs = user;
        for (int i = 0; i <= j; i++)
        {
            if (i > 0) tn += t.Dt;
            legs = chase ? ChaseIntent(c, b, user, t, pitch: pitch) : Turned(user, Lead(p, tn - now, t));
            if (openShare > 0f) legs = Opened(legs, open, openShare);
            Movement.Step(c, legs, t.Dt, tn, new MoveAssist(cap, 1f, Vector2.Zero), t, pitch);
            if (i < j) b.Step(t.Dt, null, null);
        }
        var tg = Make(c, b, foot, j, legs, chase, t);
        tg.Phase = c.GaitPhase;
        return tg;
    }

    /// <summary>The legs turned <paramref name="share"/> of the way from their own direction to <paramref name="dir"/>, at their own pace.</summary>
    static TickIntent Opened(in TickIntent legs, Vector2 dir, float share)
    {
        float m = legs.Move.Length();
        if (m < 1e-4f || dir == Vector2.Zero) return legs;
        float a0 = MathF.Atan2(legs.MoveZ, legs.MoveX), d = Movement.WrapAngle(MathF.Atan2(dir.Y, dir.X) - a0);
        float a = a0 + d * share;
        var r = legs;
        r.MoveX = MathF.Cos(a) * m;
        r.MoveZ = MathF.Sin(a) * m;
        return r;
    }

    static Target Make(PlayerState c, Ball b, Foot f, int j, in TickIntent legs, bool chase, Tuning t)
    {
        var cp = ContactPointFor(c, f, t);
        float mag = MathF.Min(1f, legs.Move.Length());
        float top = Movement.TopSpeed(c.Sheet, t);
        return new Target
        {
            Found = true, J = j, Foot = f, Ball = b.Position, Miss = new Vector2(b.Position.X - cp.X, b.Position.Z - cp.Z),
            Facing = c.Facing, LegsSpeed = Possession.SpeedCap(c, true, chase, legs, t) * (mag < t.Movement.StickDeadZone ? 0f : mag)
                * Movement.ModeFraction(legs, c.Sheet, t),
            Chase = chase, Body = new Vector2(b.Position.X - c.Position.X, b.Position.Z - c.Position.Z),
            TurnReach = t.Touch.TurnReach.Lerp(top > 0f ? c.RunSpeed / top : 0f), Phase = c.GaitPhase,
            Headroom = float.PositiveInfinity,
        };
    }

    /// <summary>
    /// The speed trim's bounds, m/s, for a trim held <paramref name="span"/> seconds: the stick-following legs' running speed
    /// stays within <see cref="AlongTrim"/> of their target speed by the contact (a straight dribble never slows below 85% of its
    /// pace; the trim reaches the running speed through the speed law, so a short one may ask for more), and never above the
    /// off-ball top speed; the go-get-it legs anywhere from a standstill to the off-ball top speed.
    /// </summary>
    static (float Lo, float Hi) TrimBounds(PlayerState p, in Target tg, float span, float tau, Tuning t)
    {
        float top = Movement.TopSpeed(p.Sheet, t);
        if (tg.Chase && !tg.Held) return (-tg.LegsSpeed, MathF.Min(tg.Headroom, MathF.Max(0f, top - tg.LegsSpeed)));
        float target = MathF.Max(tg.LegsSpeed, 1f), v0 = p.RunSpeed;
        // Held for span the running speed moves (target + trim - v0) * reach of the way from v0.
        float reach = MathF.Max(1f - MathF.Exp(-span / tau), 1e-3f);
        float lo = MathF.Min(-AlongTrim * target, ((1f - AlongTrim) * target - v0) / reach - (target - v0));
        float hi = MathF.Max(AlongTrim * target, (MathF.Min((1f + AlongTrim) * target, top) - v0) / reach - (target - v0));
        // As much either way: near the off-ball top speed, where the run cannot speed up much, it does not slow more either
        // (a one-sided trim would carry a net slowing bias); a ball short beyond it is played a step sooner.
        float room = MathF.Max(0f, top - target);
        lo = MathF.Max(lo, -target);
        if (hi > room) { hi = room; lo = MathF.Max(lo, -MathF.Max(room, AlongTrim * target * 0.5f)); }
        return (lo, hi);
    }

    /// <summary>The stick-following legs' speed trim: their running speed stays within this fraction of their target speed.</summary>
    const float AlongTrim = 0.15f;

    /// <summary>
    /// What is left of the target's miss after the steering: the sideways step covers up to steer_max held (eased in at
    /// steer_accel), the speed trim its bounds (scaled by <paramref name="trim"/>) through the speed law. For a
    /// <paramref name="turnish"/> touch a ball within the turn reach of the body and ahead of the feet counts as met (0); for a
    /// ball against the boards (<paramref name="board"/>) beside the feet too.
    /// </summary>
    static float Residual(PlayerState p, in Target tg, float now, Tuning t, float trim = 1f, bool turnish = false, bool board = false)
    {
        var k = t.Touch;
        float span = (tg.J + 1) * t.Dt;
        Vector2 fwd = Dir(tg.Facing), right = new(-fwd.Y, fwd.X);
        if (turnish && tg.Body.Length() <= tg.TurnReach - TurnReachMargin && Vector2.Dot(tg.Body, fwd) >= (board ? BoardMinAhead : MinAhead))
            return 0f;
        float eLat = Vector2.Dot(tg.Miss, right), eAlong = Vector2.Dot(tg.Miss, fwd);
        float ramp = k.SteerAccel > 0f ? k.SteerMax / k.SteerAccel : 0f;
        float latCap = k.SteerMax * MathF.Max(0f, span - 0.5f * ramp);
        float tau = MathF.Max(Movement.TimeConstantAt(p, now, t), t.Dt);
        float gain = span - tau * (1f - MathF.Exp(-span / tau));
        var (lo, hi) = TrimBounds(p, tg, span, tau, t);
        lo *= trim; hi *= trim;
        float rl = MathF.Max(0f, MathF.Abs(eLat) - latCap);
        float ra = eAlong < lo * gain ? lo * gain - eAlong : eAlong > hi * gain ? eAlong - hi * gain : 0f;
        return MathF.Sqrt(rl * rl + ra * ra);
    }

    /// <summary>A turn-reach contact is planned this far inside the reach, metres ...</summary>
    const float TurnReachMargin = 0.05f;
    /// <summary>... and never with the ball less than this ahead of the feet (the foot never reaches back), metres.</summary>
    const float MinAhead = 0.1f;
    /// <summary>... except a ball against the boards, which may be played from beside the feet (he cannot always get behind it).</summary>
    const float BoardMinAhead = 0f;

    /// <summary>How many ticks sooner the go-get-it legs must play the ball for a demand to leave the stick's legs.</summary>
    const int LegsSwitchTicks = 4;

    /// <summary>Going back from the go-get-it legs to the stick's needs this much less of a miss left, metres (no flicking between them).</summary>
    const float ReachableBackTol = 0.06f;

    /// <summary>
    /// The steering toward the target: the miss (less reach_tolerance; the foot reaches the rest) is split along and across
    /// the run predicted at the contact. Across: a sideways step held to the contact, reached from this tick's one at no more
    /// than steer_accel and held at most steer_max (it turns with the body). Along: <paramref name="along"/>, a change of the
    /// legs' target speed held to the contact (within the trim bounds), which reaches the running speed through the speed law.
    /// A skill move's step still easing out is counted in. Returns this tick's sideways step.
    /// </summary>
    static Vector2 SteerFor(PlayerState p, in Target tg, float now, Tuning t, out float along)
    {
        along = 0f;
        int moves = tg.J + 1;
        var e = tg.Miss;
        float el = e.Length(), tol = t.Touch.ReachTolerance;
        e = el <= tol ? Vector2.Zero : e * (1f - tol / el);
        float span = moves * t.Dt;
        float max = t.Touch.SteerMax, stepMax = SteerStep(t);
        var s0 = p.PlannerSteer;
        Vector2 fwdC = Dir(tg.Facing), rightC = new(-fwdC.Y, fwdC.X);
        Vector2 fwd = FacingDir(p), right = new(-fwd.Y, fwd.X);
        e -= SteerPath(p.SteerCarry, Vector2.Zero, moves, stepMax, t.Dt);
        float eLat = Vector2.Dot(e, rightC);
        float target = eLat / span;
        Vector2 moved = Vector2.Zero;
        for (int it = 0; it < 4; it++)
        {
            target = Math.Clamp(target, -max, max);
            moved = SteerPath(s0, right * target, moves, stepMax, t.Dt);
            target += (eLat - Vector2.Dot(moved, rightC)) / span;
        }
        target = Math.Clamp(target, -max, max);
        moved = SteerPath(s0, right * target, moves, stepMax, t.Dt);
        float tau = MathF.Max(Movement.TimeConstantAt(p, now, t), t.Dt);
        float gain = span - tau * (1f - MathF.Exp(-span / tau));
        var (lo, hi) = TrimBounds(p, tg, span, tau, t);
        if (gain > 1e-4f) along = Math.Clamp((Vector2.Dot(e, fwdC) - Vector2.Dot(moved, fwdC)) / gain, lo, hi);
        return Toward(s0, right * target, stepMax);
    }

    /// <summary>The largest change of the steering correction in one tick, m/s.</summary>
    static float SteerStep(Tuning t) => t.Touch.SteerAccel > 0f ? t.Touch.SteerAccel * t.Dt : float.PositiveInfinity;

    static Vector2 Toward(Vector2 from, Vector2 to, float maxStep)
    {
        var d = to - from;
        float l = d.Length();
        return l <= maxStep ? to : from + d * (maxStep / l);
    }

    /// <summary>The displacement over <paramref name="ticks"/> ticks of a correction easing from <paramref name="s0"/> to <paramref name="target"/>.</summary>
    static Vector2 SteerPath(Vector2 s0, Vector2 target, int ticks, float stepMax, float dt)
    {
        Vector2 s = s0, d = Vector2.Zero;
        for (int i = 0; i < ticks; i++) { s = Toward(s, target, stepMax); d += s * dt; }
        return d;
    }

    /// <summary>
    /// The early execution fraction for a turn of <paramref name="turnDeg"/> (the stick against the ball's travel): the share
    /// of the normal cadence a turn may cut it to, early_execution at the angle times the dribbling multiplier.
    /// </summary>
    static float EarlyFraction(PlayerState p, float turnDeg, Tuning t) =>
        t.Touch.EarlyExecution.Evaluate(turnDeg) * t.Touch.EarlyExecutionDribbling.ForRating(p.Sheet.Dribbling);

    static void Install(PlayerState p, BallAction a, int tick, Tuning t)
    {
        int win = WindupTicks(t);
        if (p.TouchAction is { Made: true } prev && prev.PhaseAt(tick, win, win) != ActionPhase.Done) p.PrevTouchAction = prev;
        p.TouchAction = a;
        UpdatePhases(p, tick, t);
    }

    internal static ActionKind ModeKind(TouchMode m) => m switch
    {
        TouchMode.CloseControl => ActionKind.Close,
        TouchMode.Jog => ActionKind.Jog,
        _ => ActionKind.Sprint,
    };

    /// <summary>
    /// The foot surface for a kind; a turn is played with the outside of the foot on the side it turns to (a trap is drawn as a
    /// touch with the inside, like a first touch).
    /// </summary>
    internal static FootSurface SurfaceFor(ActionKind kind, Foot foot, float turnSign) => kind switch
    {
        ActionKind.Stop or ActionKind.Soft => FootSurface.Sole,
        ActionKind.Sprint or ActionKind.KnockOn or ActionKind.Heavy => FootSurface.Laces,
        ActionKind.Turn or ActionKind.Cut => (turnSign > 0f) == (foot == Foot.Right) ? FootSurface.Outside : FootSurface.Inside,
        _ => FootSurface.Inside,
    };

    /// <summary>A knock-on was asked for: the next touch committed is a knock-on (one already committed is played as it is).</summary>
    internal static void RequestKnockOn(PlayerState p) => p.KnockOnRequested = true;

    /// <summary>The shot is over (struck, faked, dropped or the round ended): a committed strike is let go.</summary>
    internal static void OnShotEnded(PlayerState p)
    {
        p.StrikeContactTick = -1;
        p.ShotEarliestTick = -1;
        p.ShotNotBeforeTick = -1;
        p.ShotCommitAllowed = false;
        p.ShotFootLock = null;
    }

    /// <summary>The player no longer controls the ball: any waiting touch is let go.</summary>
    internal static void OnPossessionLost(PlayerState p)
    {
        SkillMoves.Abort(p);
        if (p.TouchAction is { Pending: true } a) a.Phase = ActionPhase.Done;
        p.Chasing = false;
        p.KnockOnRequested = false;
        p.StrikeContactTick = -1;
        p.ActionContactEta = float.NaN;
        p.ClearForecast();
    }

    // ---- the tick's touch ----

    /// <summary>
    /// After the player has moved this tick: counts the swing, makes a contact that is due (or lets it go when the ball is out of
    /// reach), the soft touch of a shot pressed with the ball at the feet, a skill move's exit touch, and a first touch of a loose
    /// ball. Returns the touch made, if any.
    /// </summary>
    internal static TouchResult? Step(PlayerState p, in TickIntent rawIntent, Ball ball, bool inPossession, TouchStepMode mode,
        int tick, float now, Tuning t, Pitch? pitch, SimRng rng)
    {
        if (p.StepBegun) p.StepsSinceTouch++;
        // A skill move under way plays the ball itself, whatever the shoot button does.
        if (inPossession && p.SkillAction != null) return SkillMoves.Step(p, Debounced(p, rawIntent, t), ball, tick, now, t, pitch);
        if (p.SkillBurstAtTick == tick)
        {
            p.SkillBurstAtTick = -1;
            SkillMoves.ApplyExitBurst(p, now);
        }
        if (mode == TouchStepMode.None) return null;
        if (!inPossession) return mode == TouchStepMode.Normal ? FirstTouch(p, rawIntent, ball, tick, now, t, pitch, rng) : null;
        var intent = Debounced(p, rawIntent, t);
        UpdatePhases(p, tick, t);

        var a = p.TouchAction;
        if (a is { Pending: true } && tick >= a.ContactTick)
        {
            var r = Execute(p, a, intent, ball, tick, now, t, pitch, rng, immediate: false);
            if (r != null) return r;
        }
        if (mode == TouchStepMode.CommittedOnly) return null;

        a = p.TouchAction;
        if (mode == TouchStepMode.SoftOnly)
        {
            // Shoot pressed with the ball at the feet: the soft touch keeps it there through the wind-up.
            if (TouchModel.InReach(p, ball, t) && now - p.LastTouchTime > 0f)
            {
                var s = new BallAction { Kind = ActionKind.Soft, PlanTick = tick, ContactTick = tick };
                s.Foot = p.IsStepping ? p.SwingFoot : TouchModel.ChooseFoot(p.Sheet, p.Position, p.Facing, ball.Position, t);
                Install(p, s, tick, t);
                return Execute(p, s, intent, ball, tick, now, t, pitch, rng, immediate: true);
            }
            return null;
        }

        // The tick a skill move ended: with the stick pushed and the ball in foot reach, the exit touch is made at once toward
        // the stick (an early touch, like a first touch), so the move flows into the run instead of waiting for a stride.
        if (p.SkillExitTick == tick && a is not { Pending: true } && intent.Move.Length() >= t.Touch.StickThreshold && TouchModel.InReach(p, ball, t))
        {
            p.SkillExitTick = -1;
            var exit = new BallAction { Kind = ModeKind(TouchModel.ModeFor(intent, t)), PlanTick = tick, ContactTick = tick };
            exit.Foot = p.IsStepping ? p.SwingFoot : TouchModel.ChooseFoot(p.Sheet, p.Position, p.Facing, ball.Position, t);
            Install(p, exit, tick, t);
            return Execute(p, exit, intent, ball, tick, now, t, pitch, rng, immediate: true);
        }
        return null;
    }

    // ---- contact ----

    /// <summary>The rated errors for a touch: speed-factor and direction standard deviations.</summary>
    static (float SigmaSpeed, float SigmaDir) Sigmas(PlayerState p, Foot foot, ActionKind kind, Tuning t)
    {
        float top = Movement.TopSpeed(p.Sheet, t);
        float frac = Math.Clamp(p.Speed / top, 0f, 1f);
        float footK = TouchModel.FootErrorFactor(p.Sheet, foot, t);
        if (kind == ActionKind.KnockOn)
            return (TouchModel.SpeedSigma(p.Sheet, 1f, t) * footK * t.Touch.KnockOnErrorScale,
                    TouchModel.DirectionSigmaDeg(p.Sheet, 1f, t) * footK * t.Touch.KnockOnErrorScale);
        return (TouchModel.SpeedSigma(p.Sheet, frac, t) * footK, TouchModel.DirectionSigmaDeg(p.Sheet, frac, t) * footK);
    }

    /// <summary>
    /// A rated error: normal about <paramref name="mean"/> with <paramref name="sigma"/>, cut off at two sigma (a rating shows
    /// as spread in the ball's distance and the cadence, never as a ball the player cannot get back to).
    /// </summary>
    static float Rated(SimRng rng, float mean, float sigma) => mean + Math.Clamp(rng.Normal(0f, sigma), -2f * sigma, 2f * sigma);

    /// <summary>A touch played from beyond stretch_reach (the ball within feet_turn_reach of the body) has its errors scaled by this.</summary>
    const float FarStretchErrorScale = 1.5f;

    /// <summary>
    /// Steps of the stride clock to the next meet: steps_per_touch; when that is even (the same foot every touch), every
    /// foot_switch_every-th touch takes one step more so the other foot plays the next run of touches. The cadence stays even
    /// (2, 2, 2, 3 for a jog) instead of alternating short and long pushes.
    /// </summary>
    static int NextSteps(PlayerState p, TouchMode mode, Tuning t)
    {
        int spt = TouchModel.StepsPerTouch(p.Sheet, mode, t);
        if (spt % 2 == 1) return spt;
        p.TouchCadenceCount++;
        return p.TouchCadenceCount % Math.Max(1, t.Touch.FootSwitchEvery) == 0 ? spt + 1 : spt;
    }

    struct Meet
    {
        public int J;
        public Foot Foot;
        public Vector3 Cp;
        /// <summary>Steps begun from now to the meet, the meet's own included.</summary>
        public int Swings;
    }

    /// <summary>
    /// Predicts the player from now with the legs following <paramref name="user"/> and finds the swing middle of the step
    /// <paramref name="steps"/> steps on (at least the windup ahead), or the one nearest <paramref name="nearTicks"/> when that
    /// is set.
    /// </summary>
    static bool FindMeet(PlayerState from, in TickIntent user, int steps, int nearTicks, float now, Tuning t, Pitch? pitch, out Meet meet,
        int minJ = 0)
    {
        meet = default;
        int pre = Math.Max(Math.Max(1, WindupTicks(t)), minJ);
        var c = from.Scratch();
        int horizon = nearTicks > 0 ? nearTicks + Ticks(1f, t) : Ticks(3f, t);
        int count = 0, bestGap = int.MaxValue;
        bool found = false;
        Meet prev = default;
        float tn = now;
        float cap = Possession.SpeedCap(from, true, false, user, t);
        for (int j = 1; j <= horizon; j++)
        {
            tn += t.Dt;
            Movement.Step(c, Turned(user, Lead(from, tn - now, t)), t.Dt, tn, new MoveAssist(cap, 1f, Vector2.Zero), t, pitch);
            if (c.StepBegun) count++;
            if (c.SwingMiddleCrossed is not Foot f || count == 0) continue;
            if (j < pre) continue;
            if (nearTicks > 0)
            {
                int gap = Math.Abs(j - nearTicks);
                if (gap < bestGap) { bestGap = gap; meet = new Meet { J = j, Foot = f, Cp = ContactPointFor(c, f, t), Swings = count }; found = true; }
                else if (j > nearTicks) break;
                continue;
            }
            // A push never spans more than max_meet_seconds (a slow stride after a turn would stretch its steps): the last
            // swing middle inside that is its meet.
            // (Cut short there, it keeps the foot the requested steps would have played: one step sooner if the parity differs.)
            if (j > Ticks(MaxMeetSeconds, t) && found)
            {
                if ((steps - meet.Swings) % 2 != 0 && prev.Swings > 0) meet = prev;
                return true;
            }
            prev = meet;
            meet = new Meet { J = j, Foot = f, Cp = ContactPointFor(c, f, t), Swings = count };
            found = true;
            if (count >= steps) return true;
        }
        return found;
    }

    /// <summary>
    /// The push of a skill move's own contact that is also its exit touch (the elastico's inside): the ball sent toward
    /// <paramref name="dir"/> (unit, world X, Z) as a dribble touch made at once along it would send it (its mode's steps, cut
    /// short for a turn, onto the swing that many steps on, no sooner than <paramref name="minJ"/> ticks), without the rated
    /// errors. Returns the ball's velocity; <paramref name="steps"/> the steps it was solved for. Changes no state.
    /// </summary>
    internal static Vector3 SkillExitPush(PlayerState p, in TickIntent intent, Vector2 dir, Ball ball, int minJ, float now, Tuning t,
        Pitch? pitch, out int steps)
    {
        var k = t.Touch;
        var user = intent;
        user.MoveX = dir.X;
        user.MoveZ = dir.Y;
        var mode = TouchModel.ModeFor(user, t);
        var wanted = new Vector3(dir.X, 0f, dir.Y);
        float turnDeg = TouchModel.TurnAngleDeg(p, ball, wanted);
        steps = TouchModel.StepsPerTouch(p.Sheet, mode, t);
        bool turning = p.RunSpeed > k.ExecutionMinSpeed && turnDeg > k.ExecutionAngle;
        if (turning)
        {
            steps = Math.Max(1, (int)MathF.Round(EarlyFraction(p, turnDeg, t) * steps));
            steps = Math.Min(steps, mode == TouchMode.Sprint ? k.TurningMaxSteps.Sprint : mode == TouchMode.Jog ? k.TurningMaxSteps.Jog : steps);
        }
        ActionKind kind = !turning ? ModeKind(mode) : turnDeg > k.CutDeg && p.RunSpeed > k.CutMinSpeed ? ActionKind.Cut : ActionKind.Turn;
        bool hasMeet = FindMeet(p, user, steps, 0, now, t, pitch, out var meet, minJ);
        if (hasMeet) steps = Math.Max(1, Math.Min(steps, meet.Swings));
        Vector3 target = hasMeet ? meet.Cp : p.Position + wanted * 2f;
        var prof = TouchModel.Profile(kind, mode, t);
        float u = SolveLiftedPush(p, ball, target, hasMeet ? meet.J : Ticks(0.6f, t), kind, mode, prof.MinPush, MathF.Max(prof.MaxPush, prof.MinPush), t,
            out var d, out _, out float lift);
        if (u > p.RunSpeed + 1f) u = MathF.Min(u, CatchablePush(p, ball, d, lift, prof.MaxPush, t));
        return Launch(d, u, lift, 0f);
    }

    /// <summary>The longest a push is solved for, seconds (inside the 1.5 s the player is always back on the ball).</summary>
    const float MaxMeetSeconds = 1.2f;

    /// <summary>
    /// The meet of a reversal: predicts the player on the stick's legs (braking, turning about and running back) and returns
    /// the first swing middle after his facing has come within <see cref="ReversalAlignDeg"/> of the stick.
    /// </summary>
    static bool FindReversalMeet(PlayerState from, in TickIntent user, float now, Tuning t, Pitch? pitch, out Meet meet)
    {
        meet = default;
        var c = from.Scratch();
        float stick = MathF.Atan2(user.Move.Y, user.Move.X), tn = now;
        float cap = Possession.SpeedCap(from, true, false, user, t);
        bool aligned = false;
        int count = 0;
        for (int j = 1; j <= Ticks(2.5f, t); j++)
        {
            tn += t.Dt;
            Movement.Step(c, user, t.Dt, tn, new MoveAssist(cap, 1f, Vector2.Zero), t, pitch);
            if (c.StepBegun) count++;
            aligned |= MathF.Abs(Movement.WrapAngle(c.Facing - stick)) * 180f / MathF.PI <= ReversalAlignDeg;
            if (aligned && j >= WindupTicks(t) && c.SwingMiddleCrossed is Foot f)
            {
                meet = new Meet { J = j, Foot = f, Cp = ContactPointFor(c, f, t), Swings = Math.Max(1, count) };
                return true;
            }
        }
        return false;
    }

    /// <summary>A reversal's meet comes once the facing is within this of the stick, degrees.</summary>
    const float ReversalAlignDeg = 30f;

    /// <summary>
    /// Solves the launch speed (bisection on a simulated ball, skid and bounces included) that brings the ball's centre to
    /// <paramref name="target"/> after <paramref name="ticks"/> ticks, launched along the ground direction toward it at
    /// <paramref name="liftDeg"/>. Clamped to [lo, hi]; <paramref name="short"/> is true when even hi falls short;
    /// <paramref name="residual"/> is how far the solved ball ends from the target along the direction, metres (a lifted ball
    /// can have a gap in reachable distances where an extra small bounce starts or stops).
    /// </summary>
    internal static float SolvePush(Ball ball, Vector3 target, int ticks, float liftDeg, float lo, float hi, Tuning t,
        out Vector3 dir, out bool @short, out float residual)
    {
        var d = new Vector3(target.X - ball.Position.X, 0f, target.Z - ball.Position.Z);
        float dist = d.Length();
        dir = dist > 1e-5f ? d / dist : Vector3.UnitX;
        float lift = liftDeg * MathF.PI / 180f, cl = MathF.Cos(lift), sl = MathF.Sin(lift);
        Vector3 dirLocal = dir;
        float Along(float u)
        {
            var b = ball.ScratchB();
            b.Velocity = dirLocal * (u * cl) + new Vector3(0f, u * sl, 0f);
            b.Spin = Vector3.Zero;
            for (int i = 0; i < ticks; i++) b.Step(t.Dt, null, null);
            return (b.Position.X - ball.Position.X) * dirLocal.X + (b.Position.Z - ball.Position.Z) * dirLocal.Z;
        }
        @short = false;
        float atHi = Along(hi);
        if (atHi < dist) { @short = true; residual = dist - atHi; return hi; }
        float atLo = Along(lo);
        if (atLo >= dist) { residual = atLo - dist; return lo; }
        for (int i = 0; i < 24; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (Along(mid) < dist) lo = mid; else hi = mid;
        }
        float u = 0.5f * (lo + hi);
        residual = MathF.Abs(Along(u) - dist);
        return u;
    }

    /// <summary>
    /// <see cref="SolvePush"/> at the touch's rated launch angle (its profile's, by dribbling), flattened until the solved arc's
    /// peak stays under the profile's peak height cap, falling back to the lowest angle of the profile's range and then to a
    /// ground pass when that angle cannot land the ball on the target (a gap in reachable distances).
    /// </summary>
    internal static float SolveLiftedPush(PlayerState p, Ball ball, Vector3 target, int ticks, ActionKind kind, TouchMode mode, float lo,
        float hi, Tuning t, out Vector3 dir, out bool @short, out float liftDeg)
    {
        var prof = TouchModel.Profile(kind, mode, t);
        liftDeg = prof.LaunchAngle.ForRating(p.Sheet.Dribbling);
        float u = SolvePush(ball, target, ticks, liftDeg, lo, hi, t, out dir, out @short, out float residual);
        // A launch whose arc would rise over the cap is flattened to the angle that peaks at the cap for the solved speed, and
        // solved again (a flatter launch needs a little more speed, so a few rounds).
        for (int i = 0; i < PeakRounds && liftDeg > 0f && ArcPeak(ball, dir, u, liftDeg, ticks, t) > prof.PeakHeightCap + PeakTolerance; i++)
        {
            float rise = MathF.Max(0f, prof.PeakHeightCap - ball.Position.Y);
            float vy = MathF.Sqrt(2f * t.Ball.Gravity * rise);
            float flat = u > 1e-4f ? MathF.Asin(Math.Clamp(vy / u, 0f, 1f)) * 180f / MathF.PI : 0f;
            // each round at least a tenth of a degree flatter, never steeper
            liftDeg = i == PeakRounds - 1 ? MathF.Min(flat, liftDeg) * 0.5f : MathF.Min(flat, liftDeg - 0.1f);
            if (liftDeg < 0.1f) liftDeg = 0f;
            u = SolvePush(ball, target, ticks, liftDeg, lo, hi, t, out dir, out @short, out residual);
        }
        if (@short || residual <= SolveTolerance) return u;
        foreach (float alt in new[] { MathF.Min(prof.LaunchAngle.At1, prof.LaunchAngle.At99), 0f })
        {
            if (alt >= liftDeg) continue;   // only ever flatter: a fallback must not lift the ball over the cap
            float u2 = SolvePush(ball, target, ticks, alt, lo, hi, t, out var dir2, out bool short2, out float res2);
            if (res2 < residual) { u = u2; dir = dir2; @short = short2; residual = res2; liftDeg = alt; }
            if (residual <= SolveTolerance) break;
        }
        return u;
    }

    /// <summary>Rounds of flattening a launch to keep its arc under the profile's peak height cap.</summary>
    const int PeakRounds = 4;
    /// <summary>An arc may peak this much over the cap before it is flattened, metres.</summary>
    const float PeakTolerance = 0.005f;

    /// <summary>
    /// The highest the ball centre rises (its first arc) when launched along ground direction <paramref name="dir"/> at
    /// <paramref name="speed"/> and <paramref name="liftDeg"/>, simulated (drag and lift included) for at most
    /// <paramref name="ticks"/> ticks, metres.
    /// </summary>
    internal static float ArcPeak(Ball ball, Vector3 dir, float speed, float liftDeg, int ticks, Tuning t)
    {
        float lift = liftDeg * MathF.PI / 180f;
        var b = ball.ScratchB();
        b.Velocity = dir * (speed * MathF.Cos(lift)) + new Vector3(0f, speed * MathF.Sin(lift), 0f);
        b.Spin = Vector3.Zero;
        float peak = b.Position.Y;
        for (int i = 0; i < Math.Max(1, ticks) && b.Velocity.Y > 0f; i++)
        {
            b.Step(t.Dt, null, null);
            peak = MathF.Max(peak, b.Position.Y);
        }
        return peak;
    }

    /// <summary>A solved push that ends further than this from its target falls back to a lower lift, metres.</summary>
    const float SolveTolerance = 0.01f;

    /// <summary>
    /// The fastest push along <paramref name="dir"/> the player can still catch: one whose ball has not run further ahead of
    /// where it starts than he covers at his running speed (or the share of his top speed) in <see cref="CatchSeconds"/> (a push faster than that could
    /// be lost by a player who chases it at once; with this bound the controller's go-get-it legs always get back to it).
    /// </summary>
    internal static float CatchablePush(PlayerState p, Ball ball, Vector3 dir, float liftDeg, float maxPush, Tuning t)
    {
        // He covers at least his running speed over that time, and at least the share of top speed allowing for his acceleration.
        float reach = MathF.Max(p.RunSpeed, Movement.TopSpeed(p.Sheet, t) * CatchMargin) * CatchSeconds;
        var target = ball.Position + dir * reach;
        return SolvePush(ball, target, Ticks(CatchSeconds, t), liftDeg, 0f, maxPush, t, out _, out _, out _);
    }

    /// <summary>A push is never longer than the player can catch within this, seconds.</summary>
    const float CatchSeconds = 1.5f;
    /// <summary>... allowing for his acceleration up to top speed: the share of top speed times that time the ball may run.</summary>
    const float CatchMargin = 0.8f;

    /// <summary>
    /// Makes a contact: the ball must be within stretch_reach of the contact point (or, stretched further, within
    /// feet_turn_reach of the body) with the foot in swing (unless <paramref name="immediate"/>); otherwise the touch is not
    /// played and returns null. The kind is decided here from the stick as it is now: a stop with the stick released, else a
    /// dribble touch, a turn past the execution angle off the ball's travel, a cut past cut_deg. Sets the ball's velocity and
    /// spin, draws the rated errors, starts the boost, and records the next meet the push is solved onto.
    /// </summary>
    static TouchResult? Execute(PlayerState p, BallAction a, in TickIntent stick, Ball ball, int tick, float now, Tuning t,
        Pitch? pitch, SimRng rng, bool immediate)
    {
        var k = t.Touch;
        Vector3 cp = immediate ? new Vector3(ball.Position.X, t.Ball.Radius, ball.Position.Z) : ContactPointFor(p, a.Foot, t);
        float miss = immediate ? 0f : Horizontal(ball.Position, cp);
        bool far = immediate && a.Stretched; // a touch made at once is stretched only when its caller says so (a regain toe-poke)
        if (!immediate)
        {
            // A plain touch plays a ball within stretch_reach of its contact point; a turn, cut, stop or close-control touch one
            // anywhere within the turn reach of the body too, at the rated errors. Further off, but within feet_turn_reach (the
            // execution radius) of the body, the leg stretches for it with larger errors (counted as stretched). A ball further
            // still, too high, or a foot not in its contact window is not played: the legs keep going to it and the next step
            // plays it.
            bool footOk = !p.IsStepping || p.SwingFoot == a.Foot
                && p.SwingProgress >= WindowFrom - WindowSlack && p.SwingProgress <= WindowTo + WindowSlack;
            float body = Horizontal(ball.Position, p.Position);
            float top = Movement.TopSpeed(p.Sheet, t);
            bool turnish = a.Kind is ActionKind.Turn or ActionKind.Cut or ActionKind.Stop or ActionKind.Close;
            bool reach = miss <= MathF.Max(k.ContactZoneRadius, k.StretchReach)
                || turnish && body <= k.TurnReach.Lerp(top > 0f ? p.RunSpeed / top : 0f);
            far = !reach;
            float maxHeight = TouchModel.Profile(a.Kind, TouchModel.ModeFor(CommittedStick(a, stick, t), t), t).MaxBallHeight;
            if (ball.Position.Y > maxHeight || !footOk || far && body > k.FeetTurnReach)
            {
                a.Phase = ActionPhase.Done;
                a.ContactMiss = miss;
                return null;
            }
            a.Stretched = far;
        }

        // A committed touch keeps the kind it was committed with and plays the ball along the stick within what its surface can
        // play; a touch made at once (a first touch, a skill move's exit touch, the soft touch with the ball at the feet) is
        // decided now.
        var intent = immediate ? stick : CommittedStick(a, stick, t);
        bool stickActive = intent.Move.Length() >= k.StickThreshold;
        var mode = TouchModel.ModeFor(intent, t);
        ActionKind kind = a.Kind;
        if (immediate && kind is not (ActionKind.Soft or ActionKind.First or ActionKind.TrapLow or ActionKind.TrapMid or ActionKind.TrapHigh))
            kind = p.KnockOnRequested ? ActionKind.KnockOn : !stickActive ? ActionKind.Stop : ModeKind(mode);
        Foot foot = a.Foot;
        var (sigmaS, sigmaD) = Sigmas(p, foot, kind, t);
        if (far) { sigmaS *= FarStretchErrorScale; sigmaD *= FarStretchErrorScale; }
        Vector3 v, spin = Vector3.Zero;
        float errDeg = 0f, turnSign = 0f;
        Meet meet = default;
        bool hasMeet = false;
        int pushSteps = 1;
        p.LastTouchTime = now;
        p.LastTouchFoot = foot;

        switch (kind)
        {
            case ActionKind.Soft:
                // Never faster than the player: a soft touch must not push the ball out of strike reach. It leaves rolling.
                v = TouchModel.Flat(p.Velocity) * (1f - MathF.Abs(Rated(rng, 0f, sigmaS)));
                spin = ball.RollingSpin(v);
                break;
            case ActionKind.Stop:
            {
                // Brought to rest about 0.35 m ahead of where the player stops; a sole touch leaves the ball rolling.
                Vector3 dir = TouchModel.StopDirection(p);
                v = dir * (TouchModel.StopSpeed(p, p.Position, ball.Position, dir, t) * Rated(rng, 1f, sigmaS));
                spin = ball.RollingSpin(v);
                break;
            }
            case ActionKind.TrapLow:
            case ActionKind.TrapMid:
            case ActionKind.TrapHigh:
            {
                // A trap (a first contact): the ball is killed against the player, the first_touch share of its speed relative to
                // him kept (scaled by the trap's own kept, turned by the first-touch error), and let drop the trap's rest offset
                // ahead of him toward the stick (his facing with it released), over the time it takes to fall to the ground.
                var prof = (TrapProfile)TouchModel.Profile(kind, mode, t);
                Vector3 relVel = TouchModel.Flat(ball.Velocity - p.Velocity);
                float extra = TouchModel.FirstTouchSigmaDeg(p.Sheet, relVel.Length(), t) * TouchModel.FootErrorFactor(p.Sheet, foot, t);
                sigmaD = MathF.Sqrt(sigmaD * sigmaD + extra * extra);
                errDeg = Rated(rng, 0f, sigmaD);
                v = TouchModel.Flat(p.Velocity) + TouchModel.Rotate(relVel * (TouchModel.FirstTouchSpeedKept(p.Sheet, t) * prof.Kept), errDeg);
                float settle = 0f;
                if (prof.RestOffset > 0f || prof.MinPush > 0f)
                {
                    float fall = MathF.Sqrt(2f * MathF.Max(0f, ball.Position.Y - t.Ball.Radius) / t.Ball.Gravity);
                    settle = Math.Clamp(prof.RestOffset / MathF.Max(fall, TrapSettleMinSeconds), prof.MinPush, MathF.Max(prof.MinPush, prof.MaxPush));
                }
                if (settle > 0f)
                {
                    var toward = stickActive ? Vector3.Normalize(new Vector3(intent.Move.X, 0f, intent.Move.Y)) : TouchModel.StopDirection(p);
                    float lift = prof.LaunchAngle.ForRating(p.Sheet.Dribbling) * MathF.PI / 180f;
                    v += toward * (settle * MathF.Cos(lift));
                    // (it pops up no higher than the trap's cap)
                    float rise = MathF.Max(0f, prof.PeakHeightCap - ball.Position.Y);
                    v.Y = MathF.Min(settle * MathF.Sin(lift), MathF.Sqrt(2f * t.Ball.Gravity * rise));
                }
                // a ball killed at the feet leaves rolling; one dropped from the body falls without spin
                spin = kind == ActionKind.TrapLow ? ball.RollingSpin(v) : Vector3.Zero;
                break;
            }
            case ActionKind.KnockOn:
            {
                // Pushed so the player, chasing without the dribble cap, meets the ball on the swing nearest the catch time.
                float catchTime = TouchModel.KnockOnCatchTime(p.Sheet, t);
                int catchTicks = Math.Max(1, (int)MathF.Round(catchTime / t.Dt));
                float aim = stickActive ? MathF.Atan2(intent.Move.Y, intent.Move.X) : p.Facing;
                var aimDir = new Vector2(MathF.Cos(aim), MathF.Sin(aim));
                p.KnockOnFrom = now;
                p.KnockOnUntil = now + catchTicks * t.Dt + k.KnockOnChaseGrace;
                p.KnockOnDirection = aimDir;
                var user = stickActive ? intent : new TickIntent { MoveX = aimDir.X, MoveZ = aimDir.Y, Sprint = 1f };
                errDeg = Rated(rng, 0f, sigmaD);
                hasMeet = FindMeet(p, user, 0, catchTicks, now, t, pitch, out meet);
                Vector3 target = hasMeet ? meet.Cp : p.Position + new Vector3(aimDir.X, 0f, aimDir.Y) * 10f;
                int ticks = hasMeet ? meet.J : catchTicks;
                var prof = TouchModel.Profile(kind, TouchMode.Sprint, t);
                float u = SolveLiftedPush(p, ball, target, ticks, kind, TouchMode.Sprint, prof.MinPush,
                    MathF.Max(prof.MaxPush, prof.MinPush), t, out var dir, out _, out float lift);
                if (!immediate) dir = ClampToCommit(a, dir);
                v = Launch(dir, u, lift, errDeg) * Rated(rng, 1f, sigmaS);
                pushSteps = hasMeet ? meet.Swings : 1;
                break;
            }
            default:
            {
                // A push (a dribble touch, a turn or cut, or a first touch with the stick pushed).
                bool first = kind == ActionKind.First;
                Vector3 relVel = TouchModel.Flat(ball.Velocity - p.Velocity);
                if (first)
                {
                    float extra = TouchModel.FirstTouchSigmaDeg(p.Sheet, relVel.Length(), t) * TouchModel.FootErrorFactor(p.Sheet, foot, t);
                    sigmaD = MathF.Sqrt(sigmaD * sigmaD + extra * extra);
                }
                errDeg = Rated(rng, 0f, sigmaD);
                if (!stickActive)
                {
                    // (a first touch is made with the stick released only as a trap; kept for a caller that asks for one)
                    v = TouchModel.Flat(p.Velocity) + TouchModel.Rotate(relVel * TouchModel.FirstTouchSpeedKept(p.Sheet, t), errDeg);
                    spin = ball.RollingSpin(v);
                    break;
                }
                // The turn is the stick's wanted direction against the ball's travel (the facing for a still ball).
                var wanted = Vector3.Normalize(new Vector3(intent.Move.X, 0f, intent.Move.Y));
                float turnDeg = TouchModel.TurnAngleDeg(p, ball, wanted);
                int steps = NextSteps(p, mode, t);
                // A skill gesture waiting for the ball: a one-step push, so the move can start at the next step.
                if (p.SkillBufTick >= 0) steps = 1;
                if (!first)
                {
                    // A turn cuts the push to the early fraction of the cadence, so the next touch can follow the stick sooner.
                    bool turning = immediate ? p.RunSpeed > k.ExecutionMinSpeed && turnDeg > k.ExecutionAngle : kind is ActionKind.Turn or ActionKind.Cut;
                    if (turning) steps = Math.Max(1, (int)MathF.Round(EarlyFraction(p, turnDeg, t) * steps));
                    // While the stick is moving or a turn is under way the ball is kept within a few steps, so a turn that
                    // follows can be played at the next swing; straight-line touches keep their cadence.
                    // (At a sprint, also a stick that changed or a sprint pressed or let go within StickSettleSeconds: a
                    // long sprint push is only for a steady stick.)
                    bool unsettled = mode == TouchMode.Sprint && (tick - p.StickChangeTick) * t.Dt < StickSettleSeconds;
                    if (turning || unsettled || MathF.Abs(p.StickRate) * 180f / MathF.PI > k.TurningStickRateDeg)
                        steps = Math.Min(steps, mode == TouchMode.Sprint ? k.TurningMaxSteps.Sprint : mode == TouchMode.Jog ? k.TurningMaxSteps.Jog : steps);
                    if (immediate)
                    {
                        kind = ModeKind(mode);
                        if (turning) kind = ActionKind.Turn;
                        if (turnDeg > k.CutDeg && p.RunSpeed > k.CutMinSpeed) kind = ActionKind.Cut;
                    }
                    // The cut costs the balance percentage of speed, shed at balance_deceleration with the legs' own easing off.
                    if (kind == ActionKind.Cut) p.SpeedDebt += p.RunSpeed * Movement.BalanceLoss(p.Sheet, t);
                }
                // The side the ball is turned to (it picks the foot's inside or outside): by the stick, as the view expects.
                turnSign = TurnSign(wanted, ball);
                if (kind is ActionKind.Turn or ActionKind.Cut) p.PostTurnUntil = now + t.Movement.PostTurnSeconds;
                if (!first)
                {
                    p.TouchBoostFrom = now;
                    p.TouchBoostUntil = now + k.TouchBoostDuration;
                    p.TouchBoostFactor = TouchModel.TouchBoostFactor(p.Sheet, turnDeg, t);
                    SkillMoves.ApplyExitBurst(p, now);
                }
                // A reversal at speed (the stick turned back past feet_turn_deg) is a hook: the ball is turned onto the stick's
                // line slowly enough to meet the player where his predicted path brings him back to it (he brakes within the
                // deceleration budget, turns and comes back), never sent away faster than that.
                bool reversal = !first && turnDeg >= k.FeetTurnDeg && p.RunSpeed > k.ExecutionMinSpeed;
                // A braking arc armed: he brakes from this touch on (its path below is planned with the braking).
                if (p.TurnArcSpeed > 0f && kind is ActionKind.Turn or ActionKind.Cut) p.TurnArcBraking = true;
                hasMeet = reversal ? FindReversalMeet(p, intent, now, t, pitch, out meet) : FindMeet(p, intent, steps, 0, now, t, pitch, out meet);
                if (reversal && hasMeet) steps = Math.Max(1, meet.Swings);
                // A push cut short by MaxMeetSeconds counts the steps it was solved for, so the next touch is played there.
                else if (hasMeet) steps = Math.Max(1, Math.Min(steps, meet.Swings));
                Vector3 target = hasMeet ? meet.Cp : p.Position + new Vector3(intent.Move.X, 0f, intent.Move.Y) * 2f;
                var prof = TouchModel.Profile(kind, mode, t);
                float minPush = reversal ? 0f : prof.MinPush;
                int ticks = hasMeet ? meet.J : Ticks(0.6f, t);
                float u = SolveLiftedPush(p, ball, target, ticks, kind, mode, minPush, MathF.Max(prof.MaxPush, minPush), t, out var dir,
                    out _, out float lift);
                // (A braking arc's hook goes where he comes round, which a surface turned 30 degrees off the stick cannot always
                // reach; the arc's quick turn of the body opens it.)
                if (!immediate && !(reversal && hasMeet && p.TurnArcBraking)) dir = ClampToCommit(a, dir);
                // A touch committed along his run before the stick turned back (a braking arc armed since, not yet braking):
                // its surface plays the ball on no faster than he runs that way, so the next swing turns it instead of chasing it.
                if (!first && !immediate && p.TurnArcSpeed > 0f && kind is not (ActionKind.Turn or ActionKind.Cut))
                    u = MathF.Min(u, MathF.Max(0f, Vector3.Dot(TouchModel.Flat(p.Velocity), dir)));
                // Never a push he cannot catch: the speed error is drawn on the bounded push, so the bound has room for it.
                if (u > p.RunSpeed + 1f) u = MathF.Min(u, CatchablePush(p, ball, dir, lift, prof.MaxPush, t) / (1f + 2f * sigmaS));
                v = Launch(dir, u, lift, errDeg) * Rated(rng, 1f, sigmaS);
                if (first) v += relVel * TouchModel.FirstTouchSpeedKept(p.Sheet, t);
                pushSteps = first ? 1 : steps;
                break;
            }
        }

        SkillMoves.ApplyExitBurst(p, now); // an exit touch of a kind without the touch boost still starts the burst
        p.LastTouchKind = kind;
        if (kind == ActionKind.KnockOn) p.KnockOnRequested = false;
        p.StepsSinceTouch = 0;
        p.PushSteps = pushSteps;
        ball.Velocity = v;
        ball.Spin = spin;

        a.Kind = kind;
        a.Made = true;
        a.ContactTick = tick;
        // The foot meets the ball where it is: within the contact zone the foot reaches the last few centimetres itself.
        a.ContactPoint = new Vector3(ball.Position.X, t.Ball.Radius, ball.Position.Z);
        a.ContactMiss = miss;
        a.BallOutVelocity = v;
        a.BallOutSpin = spin;
        a.EndTick = tick + RecoveryTicks(t);
        // A committed touch keeps the surface the view has been swinging onto the ball since its commit.
        if (immediate) a.Surface = SurfaceFor(kind, foot, turnSign);
        a.Phase = ActionPhase.Contact;
        if (hasMeet)
        {
            a.NextMeetTick = tick + meet.J;
            a.NextMeetPoint = meet.Cp;
            a.NextMeetFoot = meet.Foot;
            p.SetForecast(a.NextMeetTick, meet.Foot, meet.Cp);
        }
        else p.ClearForecast();
        p.TouchAction = a;
        p.PrevTouchAction = null;
        return new TouchResult(kind, foot, ball.Position, v, errDeg);
    }

    /// <summary>A trap lets the ball drop its rest offset ahead over no less than this, seconds (a ball already at the feet).</summary>
    const float TrapSettleMinSeconds = 0.25f;

    /// <summary>The launch velocity along ground direction <paramref name="dir"/> at <paramref name="speed"/> and the lift, turned by the error.</summary>
    static Vector3 Launch(Vector3 dir, float speed, float liftDeg, float errDeg)
    {
        float lift = liftDeg * MathF.PI / 180f;
        var v = dir * (speed * MathF.Cos(lift)) + new Vector3(0f, speed * MathF.Sin(lift), 0f);
        return TouchModel.Rotate(v, errDeg);
    }

    /// <summary>
    /// First contact with a loose ball: as soon as it is in reach of the trap for its height band (and the last touch is at least
    /// turn_min_interval ago), with the first_touch errors. A ball at the feet with the stick pushed is pushed to the next meet
    /// like a dribble touch; otherwise (the stick released, or a ball above the feet) it is trapped by its band (see
    /// <see cref="TouchModel.TrapKindFor"/>).
    /// </summary>
    static TouchResult? FirstTouch(PlayerState p, in TickIntent intent, Ball ball, int tick, float now, Tuning t, Pitch? pitch, SimRng rng)
    {
        var k = t.Touch;
        // A free regain also takes a ball beyond reach, out to regain_reach: a toe-poke at the feet, at the stretched errors.
        var trap = TouchModel.TrapKindFor(p.Sheet, p.Position, ball.Position, t);
        bool inReach = trap != null;
        if (!inReach && !Regain.InRegainReach(p, ball, t)) return null;
        if (now - p.LastTouchTime < k.TurnMinInterval) return null;
        bool stickActive = intent.Move.Length() >= k.StickThreshold;
        float rel = TouchModel.Flat(ball.Velocity - p.Velocity).Length();
        // A still ball and no stick: stand by it, unless he is going for it.
        if (!stickActive && rel < k.FirstTouchMinRelativeSpeed && p.Regain == RegainMode.None) return null;
        var mode = TouchModel.ModeFor(intent, t);
        bool push = stickActive && (trap is null or ActionKind.TrapLow)
            && ball.Position.Y <= TouchModel.Profile(ActionKind.First, mode, t).MaxBallHeight;
        var kind = push ? ActionKind.First : trap ?? ActionKind.TrapLow;
        var a = new BallAction { Kind = kind, PlanTick = tick, ContactTick = tick, Stretched = !inReach };
        a.Foot = p.IsStepping ? p.SwingFoot : TouchModel.ChooseFoot(p.Sheet, p.Position, p.Facing, ball.Position, t);
        p.TouchAction = a;
        p.PrevTouchAction = null;
        p.Chasing = false;
        return Execute(p, a, intent, ball, tick, now, t, pitch, rng, immediate: true);
    }
}
