using System.Numerics;

namespace FootballSim.Core;

/// <summary>A touch as made: its kind, the foot, where the ball was, and the ball velocity it left with.</summary>
/// <param name="DirectionErrorDeg">The direction error actually drawn, degrees.</param>
public readonly record struct TouchResult(ActionKind Kind, Foot Foot, Vector3 ContactPoint, Vector3 BallVelocity, float DirectionErrorDeg);

/// <summary>Which touches the planner may make this tick.</summary>
internal enum TouchStepMode
{
    /// <summary>No touch (a released shot waiting for the ball, or shoot held after the soft touch).</summary>
    None,
    /// <summary>Dribble touches, and first contact with a loose ball.</summary>
    Normal,
    /// <summary>Shoot is held in possession: only the soft touch (after any touch already winding up).</summary>
    SoftOnly,
}

/// <summary>
/// Touches as committed actions on the stride. In possession every touch is planned: the planner picks a future swing middle
/// of the stride clock (at least min_plan_ahead ahead) where the ball can be met, the foot in swing then, and the contact
/// point ahead of that foot. During the pre-windup and windup the player's velocity is nudged (at most steer_max) so the
/// ball arrives inside the contact zone. At the contact tick the ball gets its new velocity: for a push the planner picks the
/// next meet (the swing steps_per_touch steps later, predicted with the stride clock and the movement model) and solves the
/// push speed by simulating the ball (skid included) so its centre arrives at that meet's contact point; then the rated speed
/// and direction errors are drawn. The next touch is planned on that meet straight away. A loose ball is met by a first touch
/// as soon as it is in reach. Everything is deterministic; random numbers are drawn only at contact.
/// </summary>
public static class TouchPlanner
{
    // ---- timing ----

    static int Ticks(float seconds, Tuning t) => Math.Max(0, (int)MathF.Round(seconds * t.TickRate));
    internal static int PreWindupTicks(Tuning t) => Math.Max(1, Ticks(t.Touch.PreWindup, t));
    /// <summary>The soonest swing a fresh plan or a next meet may use, ticks (min_plan_ahead).</summary>
    internal static int PlanAheadTicks(Tuning t) => Math.Max(1, Ticks(t.Touch.MinPlanAhead, t));
    internal static int WindupTicks(Tuning t) => Ticks(t.Touch.Windup, t);
    internal static int RecoveryTicks(Tuning t) => Ticks(t.Touch.Recovery, t);

    /// <summary>Sets the phase of the player's current and previous actions for world tick <paramref name="tick"/>.</summary>
    internal static void UpdatePhases(PlayerState p, int tick, Tuning t)
    {
        int pre = PreWindupTicks(t), win = WindupTicks(t);
        if (p.TouchAction is { } a) a.Phase = a.PhaseAt(tick, pre, win);
        if (p.PrevTouchAction is { } b)
        {
            b.Phase = b.PhaseAt(tick, pre, win);
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

    // ---- legs ----

    /// <summary>Hold the current running speed along <paramref name="dir"/> (run on to the ball).</summary>
    static TickIntent HoldIntent(PlayerState p, Vector2 dir, Tuning t)
    {
        float cap = Possession.SpeedCap(p, true, false, t);
        float mag = Math.Clamp(p.RunSpeed / MathF.Max(cap, 1e-3f), 0f, 1f);
        return new TickIntent { MoveX = dir.X * mag, MoveZ = dir.Y * mag, Sprint = 1f };
    }

    /// <summary>
    /// The legs while a shot winds up with no touch waiting: with the stick at rest the run holds its speed along the facing,
    /// so a ball rolling with the shooter (after the soft touch) stays at the striking foot until contact; with the stick pushed
    /// they follow it.
    /// </summary>
    internal static TickIntent ShotWindupLegs(PlayerState p, in TickIntent legs, Tuning t) =>
        legs.Move.Length() >= t.Touch.StickThreshold ? legs : HoldIntent(p, FacingDir(p), t);

    /// <summary>
    /// Run to the ball (the recovery run): toward where it will be, at the ball's own speed away plus the gap over the speed
    /// time constant (so the player arrives at about walking pace instead of running past it), at least 0.8 m/s.
    /// </summary>
    static TickIntent ChaseIntent(PlayerState p, Ball ball, Tuning t)
    {
        var rel = new Vector2(ball.Position.X - p.Position.X, ball.Position.Z - p.Position.Z);
        float dist = rel.Length();
        if (dist < 1e-4f) return default;
        Vector2 dir = InterceptDirection(p, ball);
        var bv = new Vector2(ball.Velocity.X, ball.Velocity.Z);
        float away = MathF.Max(0f, Vector2.Dot(bv, rel / dist));
        float cap = Possession.SpeedCap(p, true, true, t);
        float target = away + MathF.Max(0f, dist - 0.3f) / Movement.TimeConstant(p.Sheet, t);
        float mag = Math.Clamp(target, 0.8f, cap) / MathF.Max(cap, 1e-3f);
        return new TickIntent { MoveX = dir.X * mag, MoveZ = dir.Y * mag, Sprint = 1f };
    }

    static Vector3 KnockFlat(Vector3 v) => new(v.X, 0f, v.Z);

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

    /// <summary>
    /// The stick-following legs, bent by the unit rotation <paramref name="bend"/> (cos, sin of the bend angle; zero for no
    /// bend): the user's stick turned toward the ball, at the stick's own deflection and sprint.
    /// </summary>
    static TickIntent BentIntent(in TickIntent user, Vector2 bend)
    {
        if (bend == Vector2.Zero) return user;
        var r = user;
        r.MoveX = user.MoveX * bend.X - user.MoveZ * bend.Y;
        r.MoveZ = user.MoveX * bend.Y + user.MoveZ * bend.X;
        return r;
    }

    /// <summary>
    /// Ease off: with the stick pushed the legs turn toward the stick while slowing (the smallest deflection that still
    /// turns), with it released they just slow down.
    /// </summary>
    static TickIntent EaseIntent(in TickIntent user, Tuning t)
    {
        Vector2 stick = user.Move;
        float len = stick.Length();
        if (len < t.Touch.StickThreshold) return default;
        float mag = t.Movement.StickDeadZone * 1.01f;
        return new TickIntent { MoveX = stick.X / len * mag, MoveZ = stick.Y / len * mag };
    }

    /// <summary>
    /// What the legs follow. For <see cref="LegsMode.Stick"/>, <paramref name="holdDir"/> is the bend toward the ball (zero:
    /// the stick itself); for <see cref="LegsMode.Hold"/> it is the direction held.
    /// </summary>
    static TickIntent LegsFor(PlayerState p, LegsMode mode, Vector2 holdDir, in TickIntent user, Ball? ball, Tuning t) => mode switch
    {
        LegsMode.Hold => HoldIntent(p, holdDir, t),
        LegsMode.Neutral => holdDir == Vector2.Zero ? EaseIntent(user, t) : default,
        LegsMode.Approach when ball != null => ChaseIntent(p, ball, t),
        _ => BentIntent(user, holdDir),
    };

    /// <summary>
    /// One predicted tick of a player in possession (no steering), the same way the world moves it. <paramref name="ball"/> is
    /// the (predicted) ball at the start of that tick; <paramref name="turn"/> turns the user's stick by the predicted stick
    /// turn, radians.
    /// </summary>
    static void PredictStep(PlayerState c, LegsMode mode, Vector2 holdDir, in TickIntent user, Ball? ball, float now, Tuning t, Pitch? pitch,
        float turn = 0f)
    {
        var legs = LegsFor(c, mode, holdDir, turn == 0f ? user : BentIntent(user, new Vector2(MathF.Cos(turn), MathF.Sin(turn))), ball, t);
        float cap = Possession.SpeedCap(c, true, false, t);
        Movement.Step(c, legs, t.Dt, now, new MoveAssist(cap, 1f, Vector2.Zero), t, pitch);
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

    /// <summary>A stick within this of where a touch was planned, and not turning, is held still, degrees.</summary>
    const float SteadyStickDeg = 0.5f;
    /// <summary>The predicted stick turn never runs further than this, degrees.</summary>
    const float StickLeadMaxDeg = 90f;
    /// <summary>A stick moving more than this in one tick jumped (a flick), degrees: it is not a smooth turn.</summary>
    const float StickJumpDeg = 3f;
    /// <summary>Smoothing time of the stick turn rate, seconds.</summary>
    const float StickRateSmoothing = 0.1f;

    /// <summary>Updates the smooth stick turn rate from this tick's stick (starting afresh on a round's first tick).</summary>
    static void TrackStick(PlayerState p, in TickIntent intent, int tick, Tuning t)
    {
        if (tick <= 1) { p.StickAnglePrev = float.NaN; p.StickRate = 0f; p.StickTurnTicks = 0; p.EarlyTurnTick = -1; p.SpeedDebt = 0f; p.TouchCadenceCount = 0; p.PlannerSteer = Vector2.Zero; p.ReverseTime = -100f; p.PostTurnUntil = -100f; }
        Vector2 m = intent.Move;
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
    /// The stick as the planner reads it in possession: a release no longer than release_debounce reads as the last pushed
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
    /// Before the player moves this tick: updates the action phases, reviews a waiting plan against the stick (a stop, a turn,
    /// a slowdown, a replan), and returns what the legs follow and the movement modifiers (speed cap and steering).
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
            p.Recovering = false;
            p.PlannerSteer = Vector2.Zero;
            legs = intent;
            float top = Movement.TopSpeed(p.Sheet, t);
            return Stunned(p, now)
                ? new MoveAssist(top * t.Shot.StunSpeedFraction, 1f + t.Possession.OffBallTurnBonus, Vector2.Zero, HardCap: top * t.Shot.StunSpeedFraction, TurnAbout: true)
                : new MoveAssist(top, 1f + t.Possession.OffBallTurnBonus, Vector2.Zero, TurnAbout: true);
        }

        // A skill move owns the legs while it lasts; on the tick it ends, the planner takes over at once.
        if (p.SkillAction is { } skill)
        {
            // The move moves the player itself; the planner's steering starts again from nothing after it.
            if (tick < skill.EndTick) { p.PlannerSteer = Vector2.Zero; return SkillMoves.BeginTick(p, ball, tick, t, out legs); }
            SkillMoves.Finish(p, intent, ball, tick, now, t);
            UpdatePhases(p, tick, t);
        }

        // The stick reversed with the ball at the feet: the sole turns it at once (a drag-back onto the stick's line).
        if (live && FeetTurn(p, intent, ball, tick, now, t))
        {
            p.PlannerSteer = Vector2.Zero;
            return SkillMoves.BeginTick(p, ball, tick, t, out legs);
        }

        if (live && p.TouchAction is { Pending: true } a0) ReviewPlan(p, a0, intent, ball, tick, now, t, pitch);
        UpdatePhases(p, tick, t);

        var a = p.TouchAction is { Pending: true } pa ? pa : null;
        // The steering correction changes at no more than steer_accel: it eases in for a touch and back out after it.
        Vector2 steer = SteerToward(p, Vector2.Zero, t);
        if (a != null)
        {
            legs = LegsFor(p, a.Legs, a.HoldDirection, intent, ball, t);
            if (live && a.Phase is ActionPhase.PreWindup or ActionPhase.Windup) steer = Steer(p, a, intent, ball, tick, now, t, pitch);
            a.Steer = steer;
        }
        else legs = p.Recovering && live ? ChaseIntent(p, ball, t) : intent;
        p.PlannerSteer = steer;
        float cap = Possession.SpeedCap(p, true, p.Recovering && a == null, t);
        if (Stunned(p, now))
        {
            float stun = Movement.TopSpeed(p.Sheet, t) * t.Shot.StunSpeedFraction;
            return new MoveAssist(MathF.Min(cap, stun), 1f, steer, HardCap: stun);
        }
        return new MoveAssist(cap, 1f, steer);
    }

    /// <summary>True during the stun after a strike: the shooter is slowed and makes no touches.</summary>
    internal static bool Stunned(PlayerState p, float now) => now < p.StunUntil;

    /// <summary>
    /// Starts a turn at the feet when the stick points at least feet_turn_deg from the ball's travel (the facing for a still
    /// ball) while running, with the ball low and within feet_turn_reach of the body, and no touch already winding up (that one
    /// turns the ball sooner): the sole pulls the ball onto the stick's line at once, beside or behind the planted foot too,
    /// instead of waiting for a swing in front of the foot (see <see cref="SkillMoves.StartFeetTurn"/>). Not after a knock-on
    /// or heavy touch, during a shot or the stun, or within a drag-back's cooldown.
    /// </summary>
    static bool FeetTurn(PlayerState p, in TickIntent intent, Ball ball, int tick, float now, Tuning t)
    {
        var k = t.Touch;
        Vector2 stick = intent.Move;
        if (stick.Length() < k.StickThreshold || p.SkillAction != null || p.ShotAction != null || Stunned(p, now)) return false;
        if (now < p.SkillCooldownUntil || p.SkillExitTick == tick || Possession.CanLoseByDistance(p)) return false;
        if (p.RunSpeed <= k.ExecutionMinSpeed || ball.Position.Y > t.Skill.MaxBallHeight) return false;
        if (p.TouchAction is { Pending: true, Phase: ActionPhase.Windup or ActionPhase.Contact }) return false;
        if (Horizontal(ball.Position, p.Position) > k.FeetTurnReach) return false;
        var wanted = new Vector3(stick.X, 0f, stick.Y);
        if (TouchModel.TurnAngleDeg(p, ball, wanted) < k.FeetTurnDeg) return false;
        SkillMoves.StartFeetTurn(p, Vector2.Normalize(stick), ball, tick, now, t);
        return true;
    }

    /// <summary>
    /// Drops a waiting plan (no contact); the planner plans again at once, and that replan may pick a swing as close as the
    /// windup (the steering then has only the time left).
    /// </summary>
    static void Drop(PlayerState p, BallAction a)
    {
        a.Phase = ActionPhase.Done;
        p.Replanning = true;
    }

    static bool Pushable(ActionKind k) => k is not (ActionKind.Stop or ActionKind.Soft or ActionKind.First);

    /// <summary>
    /// The stick against a waiting plan. Released: the touch becomes a stop touch and the legs run on to the ball (after a
    /// knock-on or heavy touch the plan is dropped instead). Pushed again after a stop: plan again. Turned more than the replan
    /// angle, or a change of touch mode (sprint pressed or released): plan again. Turned more than the execution angle: the
    /// legs keep following the stick, and
    /// <list type="bullet">
    /// <item>inside the windup the touch is kept, even past the replan angle, and the legs hold their run until contact so the
    /// swing lands (at most the windup); the touch turns the ball at contact;</item>
    /// <item>otherwise, with the stick more than the execution angle off the ball's travel, the touch is planned again as an
    /// early turn touch (see <see cref="FreshPlan"/>), on the stick-following legs. The ball turns at the next swing that can
    /// reach it: right after a long push (a sprint touch, a three-step jog touch) the ball is out of reach until about its
    /// planned meet, and until then the legs bend toward it (or, more than approach_max_stick_deg off, run on to it);</item>
    /// <item>otherwise (the stick back on the ball's line) the touch is kept if the stick-following legs still meet the ball
    /// at its contact, and planned again if not.</item>
    /// </list>
    /// Within the execution angle the touch is checked again on a drift of the stick and routinely in the pre-windup (see
    /// <see cref="Drifted"/>). Inside the windup and on the contact tick nothing but a release changes the touch.
    /// </summary>
    static void ReviewPlan(PlayerState p, BallAction a, in TickIntent intent, Ball ball, int tick, float now, Tuning t, Pitch? pitch)
    {
        var k = t.Touch;
        Vector2 stick = intent.Move;
        bool stickActive = stick.Length() >= k.StickThreshold;
        if (a.Kind == ActionKind.Soft)
        {
            // The soft touch for a shot: the stick let go (the shot released) does not stop the run on to the ball.
            if (!stickActive && a.Legs == LegsMode.Stick) { a.Legs = LegsMode.Hold; a.HoldDirection = FacingDir(p); }
            return;
        }
        bool restricted = Possession.CanLoseByDistance(p);
        if (!stickActive)
        {
            if (a.Kind == ActionKind.Stop) return;
            if (restricted) { Drop(p, a); return; }
            a.Kind = ActionKind.Stop;
            a.Surface = FootSurface.Sole;
            if (a.Legs == LegsMode.Stick) { a.Legs = LegsMode.Hold; a.HoldDirection = FacingDir(p); }
            a.PlanStickAngle = float.NaN;
            return;
        }
        if (a.Kind == ActionKind.Stop || float.IsNaN(a.PlanStickAngle)) { Drop(p, a); return; }
        float angle = MathF.Atan2(stick.Y, stick.X);
        float change = MathF.Abs(Movement.WrapAngle(angle - a.PlanStickAngle)) * 180f / MathF.PI;
        if (!restricted && a.Phase is ActionPhase.Windup or ActionPhase.Contact)
        {
            // The foot is already drawn back: the touch is made whatever the stick does now (a change of touch mode waits for the
            // next touch). Turned past the execution angle, or drifted so that following the stick would carry the ball out of
            // reach, the run holds until contact (at most the windup) so the swing lands; the touch turns the ball toward the stick.
            if (a.Legs != LegsMode.Hold && (change > k.ExecutionAngle || Drifted(p, a, angle, intent, ball, tick, now, t, pitch)))
            {
                a.Legs = LegsMode.Hold;
                a.HoldDirection = FacingDir(p);
            }
            return;
        }
        var wanted = new Vector3(stick.X, 0f, stick.Y);
        if (change > k.ReplanAngle)
        {
            // Turned past the replan angle: planned again, as an early turn touch when the ball is to be turned.
            float turnBy = TouchModel.TurnAngleDeg(p, ball, wanted);
            if (!restricted && turnBy > k.ExecutionAngle && p.RunSpeed > k.ExecutionMinSpeed) EarlyTurn(p, a, turnBy, t);
            else Drop(p, a);
            return;
        }
        var mode = TouchModel.ModeFor(intent, t);
        if (Speed(mode) != Speed(a.PlanMode)) { Drop(p, a); return; }
        if (restricted || a.Legs == LegsMode.Hold) return;
        if (change <= k.ExecutionAngle)
        {
            // A slow drift of the stick (a thumb never holds still), or a prediction gone stale: once the legs following the
            // stick no longer bring the ball within the steering's reach at the contact, the touch is planned again at once.
            if (Drifted(p, a, angle, intent, ball, tick, now, t, pitch)) Drop(p, a);
            return;
        }
        float turnDeg = TouchModel.TurnAngleDeg(p, ball, wanted);
        if (turnDeg > k.ExecutionAngle) { EarlyTurn(p, a, turnDeg, t); return; }
        if (!PlanHolds(p, a, LegsMode.Stick, default, intent, ball, tick, now, t, pitch)) { Drop(p, a); return; }
        a.PlanStickAngle = angle;
        a.Legs = LegsMode.Stick;
        a.HoldDirection = Vector2.Zero;
    }

    /// <summary>
    /// Drops a waiting plan for an early turn touch: planned again at once, on a swing no sooner after the previous touch than
    /// the early fraction of the cadence this touch had (and with the turn's reach and steering).
    /// </summary>
    static void EarlyTurn(PlayerState p, BallAction a, float turnDeg, Tuning t)
    {
        int last = (int)MathF.Round(p.LastTouchTime / t.Dt);
        float cadence = MathF.Max(0f, (a.ContactTick - last) * t.Dt);
        int from = last + Ticks(MathF.Max(t.Touch.EarlyMinInterval, EarlyFraction(p, turnDeg, t) * cadence), t);
        Drop(p, a);
        p.EarlyTurnTick = from;
    }

    /// <summary>
    /// True when a check of the waiting touch finds that the plan's legs no longer hold (see <see cref="PlanHolds"/>). It is
    /// checked once the stick has drifted drift_recheck_deg from where it was last checked, and every recheck_interval in the
    /// pre-windup; each check moves the reference to the current stick.
    /// </summary>
    static bool Drifted(PlayerState p, BallAction a, float angle, in TickIntent intent, Ball ball, int tick, float now, Tuning t, Pitch? pitch)
    {
        if (float.IsNaN(a.CheckStickAngle)) { a.CheckStickAngle = a.PlanStickAngle; a.CheckTick = tick; }
        if (float.IsNaN(a.CheckStickAngle)) return false;
        bool drifted = MathF.Abs(Movement.WrapAngle(angle - a.CheckStickAngle)) * 180f / MathF.PI >= t.Touch.DriftRecheckDeg;
        // Without a drift, checked every recheck_interval once in the pre-windup (where the steering's reach is what it is) while
        // the stick is not held still (it is turning, or is off where the touch was planned) or the touch follows a turn or cut
        // (the body is still coming round onto the ball's new line); never while a skill move waits for this touch's meet.
        bool unsteady = p.StickRate != 0f || MathF.Abs(Movement.WrapAngle(angle - a.PlanStickAngle)) * 180f / MathF.PI > SteadyStickDeg
            || p.LastTouchKind is ActionKind.Turn or ActionKind.Cut;
        if (!drifted && (a.Phase != ActionPhase.PreWindup || !unsteady || p.SkillBufTick >= 0
            || tick - a.CheckTick < Math.Max(1, Ticks(t.Touch.RecheckInterval, t)))) return false;
        a.CheckStickAngle = angle;
        a.CheckTick = tick;
        return !PlanHolds(p, a, a.Legs, a.HoldDirection, intent, ball, tick, now, t, pitch);
    }

    /// <summary>
    /// True when the legs in <paramref name="legs"/> (no steering) still bring the ball within the contact zone plus what the
    /// steering can correct (a turn touch's steering for a turn) at the plan's contact tick.
    /// </summary>
    static bool PlanHolds(PlayerState p, BallAction a, LegsMode legs, Vector2 holdDir, in TickIntent intent, Ball ball, int tick, float now,
        Tuning t, Pitch? pitch)
    {
        var k = t.Touch;
        int moves = a.ContactTick - tick + 1;
        if (moves < 1) return false;
        var c = p.Scratch();
        var b = BallScratch(ball);
        float tn = now;
        for (int i = 0; i < moves; i++)
        {
            if (i > 0) tn += t.Dt;
            PredictStep(c, legs, holdDir, intent, b, tn, t, pitch, Lead(p, tn - now, t));
            if (i < moves - 1) b.Step(t.Dt, null, null);
        }
        if (c.IsStepping && c.SwingFoot != a.Foot) return false;
        float budget = a.TurnStep ? TurnBudget(p, moves * t.Dt, t) : SteerBudget(moves * t.Dt, t);
        return Horizontal(b.Position, ContactPointFor(c, a.Foot, t)) <= budget && b.Position.Y <= k.MaxBallHeight;
    }

    static int Speed(TouchMode m) => m switch { TouchMode.CloseControl => 0, TouchMode.Jog => 1, _ => 2 };

    /// <summary>
    /// The steering correction for this tick: predicts the player (without steering) and the ball to the contact tick, and
    /// finds the correction that, reached from this tick's one at no more than steer_accel and held at most steer_max, brings
    /// the ball to within reach_tolerance of the contact point by the contact (the foot reaches the rest of the way). Returns
    /// this tick's step toward it and sets the action's contact point to where the ball will be.
    /// </summary>
    static Vector2 Steer(PlayerState p, BallAction a, in TickIntent intent, Ball ball, int tick, float now, Tuning t, Pitch? pitch)
    {
        int moves = a.ContactTick - tick + 1;
        if (moves < 1) return SteerToward(p, Vector2.Zero, t);
        var c = p.Scratch();
        var b = BallScratch(ball);
        float tn = now;
        for (int i = 0; i < moves; i++)
        {
            if (i > 0) tn += t.Dt;
            PredictStep(c, a.Legs, a.HoldDirection, intent, b, tn, t, pitch, Lead(p, tn - now, t));
            if (i < moves - 1) b.Step(t.Dt, null, null);
        }
        Vector3 cp = ContactPointFor(c, a.Foot, t);
        var e = new Vector2(b.Position.X - cp.X, b.Position.Z - cp.Z);
        float el = e.Length(), tol = t.Touch.ReachTolerance;
        e = el <= tol ? Vector2.Zero : e * (1f - tol / el);
        float span = moves * t.Dt;
        float max = a.TurnStep ? t.Touch.TurnSteerMax : t.Touch.SteerMax;
        var s0 = p.PlannerSteer;
        float stepMax = a.TurnStep ? MathF.Max(SteerStep(t), t.Touch.TurnSteerAccel * t.Dt) : SteerStep(t);
        // The held correction whose eased-in path covers the miss (a few fixed-point rounds; the ease shortens the path).
        Vector2 target = e / span;
        Vector2 moved = Vector2.Zero;
        for (int it = 0; it < 4; it++)
        {
            float len = target.Length();
            if (len > max) target *= max / len;
            moved = SteerPath(s0, target, moves, stepMax, t.Dt);
            target += (e - moved) / span;
        }
        float tl = target.Length();
        if (tl > max) target *= max / tl;
        a.ContactPoint = new Vector3(b.Position.X, t.Ball.Radius, b.Position.Z);
        return Toward(s0, target, stepMax);
    }

    /// <summary>The largest change of the steering correction in one tick, m/s.</summary>
    static float SteerStep(Tuning t) => t.Touch.SteerAccel > 0f ? t.Touch.SteerAccel * t.Dt : float.PositiveInfinity;

    /// <summary>
    /// This tick's steering correction on the way from the last tick's one to <paramref name="target"/> (a turn step's faster
    /// correction, above steer_max, eases out at the turn's rate).
    /// </summary>
    static Vector2 SteerToward(PlayerState p, Vector2 target, Tuning t) =>
        Toward(p.PlannerSteer, target, p.PlannerSteer.Length() > t.Touch.SteerMax + 1e-4f ? MathF.Max(SteerStep(t), t.Touch.TurnSteerAccel * t.Dt) : SteerStep(t));

    /// <summary>
    /// The budget of an early turn touch's swing <paramref name="seconds"/> ahead: the zone radius plus 0.9 of what the turn's
    /// steering covers in that time (at most the pre-windup), and never more than turn_reach by speed.
    /// </summary>
    static float TurnBudget(PlayerState p, float seconds, Tuning t)
    {
        var k = t.Touch;
        float time = MathF.Min(k.PreWindup, seconds), max = k.TurnSteerMax, acc = MathF.Max(k.TurnSteerAccel, 1e-3f);
        float ramp = max / acc;
        float reach = time <= ramp ? 0.5f * acc * time * time : max * (time - 0.5f * ramp);
        float top = Movement.TopSpeed(p.Sheet, t);
        return MathF.Max(SteerBudget(seconds, t), MathF.Min(k.ContactZoneRadius + 0.9f * reach, k.TurnReach.Lerp(top > 0f ? p.RunSpeed / top : 0f)));
    }

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
    /// How far off the contact zone a ball may be predicted for a swing <paramref name="seconds"/> ahead and still be planned:
    /// the zone radius plus 0.8 of what the steering covers from rest in that time (at most the pre-windup), easing in at
    /// steer_accel up to steer_max.
    /// </summary>
    static float SteerBudget(float seconds, Tuning t)
    {
        var k = t.Touch;
        float time = MathF.Min(k.PreWindup, seconds), max = k.SteerMax;
        float ramp = k.SteerAccel > 0f ? max / k.SteerAccel : 0f;
        float reach = time <= ramp ? 0.5f * k.SteerAccel * time * time : max * (time - 0.5f * ramp);
        return k.ContactZoneRadius + 0.8f * reach;
    }

    static Ball BallScratch(Ball b) => b.ScratchA();
    static Ball BallScratch2(Ball b) => b.ScratchB();

    // ---- planning ----

    struct Candidate
    {
        public int J;
        public Foot Foot;
        public Vector3 Cp;
        public LegsMode Legs;
        public Vector2 HoldDir;
        public bool Lock;
        public float LockTarget;
    }

    /// <summary>
    /// Looks for the earliest swing middle at least min_plan_ahead ahead where the ball can be brought into the contact zone,
    /// with the legs in <paramref name="legsMode"/>. When both feet can play it within a step, the preferred foot is used unless
    /// the ball sits on the other foot's side. From a standstill the first step is timed (the stride clock is locked) so its
    /// swing middle falls exactly min_plan_ahead ahead.
    /// </summary>
    static bool Search(PlayerState p, Ball ball, LegsMode legsMode, Vector2 holdDir, in TickIntent user, float now, Tuning t,
        Pitch? pitch, int minTicks, out Candidate best, int maxTicks = int.MaxValue, int extraTicks = 0)
    {
        var k = t.Touch;
        best = default;
        int pre = PlanAheadTicks(t);
        int horizon = Math.Max(pre, Ticks(t.Possession.PredictionHorizon, t) + extraTicks);
        var c = p.Scratch();
        var b = BallScratch(ball);
        bool idleStart = !p.IsStepping && p.GaitPhase == MathF.Floor(p.GaitPhase);
        float lockTarget = 0f;
        if (idleStart)
        {
            Foot want = TouchModel.ChooseFoot(p.Sheet, p.Position, p.Facing, ball.Position, t);
            float floor = MathF.Floor(p.GaitPhase);
            lockTarget = floor + (Gait.SwingFoot(p.GaitPhase) == want ? 0.5f : 1.5f);
            c.GaitLockTicks = pre;
            c.GaitLockTarget = lockTarget;
            horizon = pre;
        }
        bool found = false;
        int firstStepEnd = -1;
        float tn = now;
        horizon = Math.Min(horizon, maxTicks);

        for (int j = 1; j <= horizon; j++)
        {
            tn += t.Dt;
            PredictStep(c, legsMode, holdDir, user, b, tn, t, pitch, Lead(p, tn - now, t));
            b.Step(t.Dt, null, null);
            if (found && j > firstStepEnd) break;
            if (c.SwingMiddleCrossed is not Foot f || j < (idleStart ? pre : minTicks)) continue;
            Vector3 cp = ContactPointFor(c, f, t);
            float budget = p.PlanningTurn ? TurnBudget(p, j * t.Dt, t) : SteerBudget(j * t.Dt, t);
            bool valid = Horizontal(b.Position, cp) <= budget && b.Position.Y <= k.MaxBallHeight;
            if (!valid) continue;
            var cand = new Candidate { J = j, Foot = f, Cp = new Vector3(b.Position.X, t.Ball.Radius, b.Position.Z), Legs = legsMode, HoldDir = holdDir,
                Lock = idleStart, LockTarget = lockTarget };
            if (!found)
            {
                best = cand;
                found = true;
                // Look one more step ahead for the other foot.
                float freq = MathF.Max(c.StepFrequency, 1f);
                firstStepEnd = j + (int)MathF.Ceiling(1f / (freq * t.Dt)) + 1;
                // a turn touch takes the first swing that can play it, whichever foot
                if (p.PlanningTurn || f == p.Sheet.PreferredFoot && !BallOnOtherSide(c, b.Position, f, t)) return true;
                continue;
            }
            if (f == best.Foot) continue;
            // Two feet can play it: the ball's side decides, the preferred foot otherwise.
            float lateral = TouchModel.Lateral(c.Position, c.Facing, b.Position);
            float off = k.FootSideOffset;
            Foot side = lateral < -off ? Foot.Left : lateral > off ? Foot.Right : p.Sheet.PreferredFoot;
            if (side == f) best = cand;
            return true;
        }
        return found;
    }

    static bool BallOnOtherSide(PlayerState c, Vector3 ball, Foot f, Tuning t)
    {
        float lateral = TouchModel.Lateral(c.Position, c.Facing, ball);
        float off = t.Touch.FootSideOffset;
        return f == Foot.Right ? lateral < -off : lateral > off;
    }

    /// <summary>
    /// Plans a touch afresh. With the stick pushed the legs follow the stick if any swing works that way; otherwise they ease
    /// off while turning to the stick (a slowdown); only if neither works do they run to the ball (never more than
    /// approach_max_stick_deg off the stick). With the stick released (and the ball not settling at the feet), a stop touch
    /// with the legs easing off, holding their run or running to the ball, the earliest valid swing winning. With no swing
    /// possible the player makes a recovery run. After a knock-on or a heavy touch nothing is planned with the stick released
    /// and there is no recovery run: the ball is the player's to chase, and it can be lost by distance.
    /// <para>
    /// After a stick turn (<see cref="PlayerState.EarlyTurnTick"/>) this is an early turn touch: the earliest swing at least the
    /// windup ahead and no sooner than that tick; the touch turns the ball toward the stick (a turn, or a cut above cut_deg)
    /// and the push is solved onto the stick-following path.
    /// </para>
    /// </summary>
    static BallAction? FreshPlan(PlayerState p, in TickIntent intent, Ball ball, int tick, float now, Tuning t, Pitch? pitch,
        ActionKind? forced)
    {
        var k = t.Touch;
        bool stickActive = intent.Move.Length() >= k.StickThreshold;
        bool restricted = Possession.CanLoseByDistance(p);
        Span<LegsMode> modes = stackalloc LegsMode[4];
        int n;
        if (forced == ActionKind.Soft)
        {
            modes[0] = LegsMode.Stick; modes[1] = LegsMode.Hold; n = stickActive ? 2 : 1;
            if (!stickActive) modes[0] = LegsMode.Hold;
        }
        else if (!stickActive)
        {
            if (restricted || !TouchModel.NeedsStop(p, ball, t)) { p.Recovering = false; return null; }
            modes[0] = LegsMode.Neutral; modes[1] = LegsMode.Hold; modes[2] = LegsMode.Approach; n = 3;
        }
        else { modes[0] = LegsMode.Stick; modes[1] = LegsMode.Neutral; modes[2] = LegsMode.Approach; n = 3; }
        // With the stick pushed the order is a preference (the first mode with a valid swing wins), otherwise the earliest.
        bool ordered = stickActive && forced != ActionKind.Soft;

        Vector2 holdDir = InterceptDirection(p, ball);
        int minTicks = p.Replanning ? Math.Max(1, WindupTicks(t)) : PlanAheadTicks(t);
        float turnDeg = stickActive ? TouchModel.TurnAngleDeg(p, ball, new Vector3(intent.Move.X, 0f, intent.Move.Y)) : 0f;
        // A turn touch after a stick turn, and the plan that ends a recovery run with the stick off the ball's travel: the
        // turn's reach and steering apply.
        bool offLine = p.Recovering && !restricted && p.RunSpeed > k.ExecutionMinSpeed && turnDeg > k.ExecutionAngle;
        bool early = (p.EarlyTurnTick >= 0 || offLine) && stickActive && forced == null;
        if (early && p.EarlyTurnTick >= 0) minTicks = Math.Max(minTicks, p.EarlyTurnTick - tick);
        // A replan with the stick pushed off the ball's travel (a dropped touch, a push whose meet was lost) plays the ball again
        // as soon as it can: on the next swing that reaches it, with the turn's reach and steering (a step toward it).
        bool quick = early || p.Replanning && stickActive && forced == null && !restricted && turnDeg > k.ExecutionAngle;
        p.Replanning = false;
        p.EarlyTurnTick = -1;
        Candidate best = default;
        bool any = false;
        p.PlanningTurn = quick;
        if (ordered) any = StickPlan(p, ball, intent, holdDir, now, t, pitch, minTicks, out best);
        else
        {
            for (int i = 0; i < n; i++)
            {
                var dir = modes[i] == LegsMode.Stick ? Vector2.Zero : holdDir;
                if (!Search(p, ball, modes[i], dir, intent, now, t, pitch, minTicks, out var c)) continue;
                if (!any || c.J < best.J) { best = c; any = true; }
            }
        }
        p.PlanningTurn = false;
        if (!any)
        {
            p.Recovering = !restricted;
            return null;
        }
        p.Recovering = false;
        var mode = TouchModel.ModeFor(intent, t);
        ActionKind kind = forced ?? (!stickActive ? ActionKind.Stop : best.Legs == LegsMode.Neutral ? ActionKind.Slowdown : ModeKind(mode));
        if (early && kind != ActionKind.Slowdown) kind = turnDeg > k.CutDeg ? ActionKind.Cut : ActionKind.Turn;
        if (p.KnockOnRequested && Pushable(kind)) kind = ActionKind.KnockOn;
        var a = new BallAction
        {
            Kind = kind,
            Foot = best.Foot,
            Surface = SurfaceFor(kind, best.Foot, 0f),
            PlanTick = tick,
            ContactTick = tick + best.J,
            ContactPoint = best.Cp,
            Legs = best.Legs,
            HoldDirection = best.HoldDir,
            PlanStickAngle = stickActive ? MathF.Atan2(intent.Move.Y, intent.Move.X) : float.NaN,
            PlanMode = mode,
            TurnStep = quick,
        };
        if (best.Lock)
        {
            p.GaitLockTicks = best.J;
            p.GaitLockTarget = best.LockTarget;
        }
        Install(p, a, tick, t);
        return a;
    }

    /// <summary>
    /// The legs for a touch planned with the stick pushed: the earliest swing of following the stick, easing off while turning
    /// to the stick, and following the stick bent toward the ball by the least angle (up to approach_max_stick_deg) that lets
    /// the foot reach it, in that order of preference on a tie; only if none works, the last resort: running on to the ball at
    /// the current pace (<see cref="LegsMode.Hold"/> toward the intercept) or running to it, whichever plays it sooner. That
    /// is the only case the legs may turn further than approach_max_stick_deg from the stick (the ball is further off it).
    /// With nothing in the prediction horizon, the stick side is searched again half a second further.
    /// </summary>
    static bool StickPlan(PlayerState p, Ball ball, in TickIntent intent, Vector2 toBall, float now, Tuning t, Pitch? pitch,
        int minTicks, out Candidate best)
    {
        if (StickSidePlan(p, ball, intent, toBall, now, t, pitch, minTicks, 0, out best)) return true;
        // The last resort: run on to the ball at the current pace, run to it, or ease off without turning (a ball at the feet
        // rolls ahead into the contact zone), whichever plays it sooner.
        bool any = Search(p, ball, LegsMode.Hold, toBall, intent, now, t, pitch, minTicks, out best);
        if (Search(p, ball, LegsMode.Approach, toBall, intent, now, t, pitch, minTicks, out var ap, any ? best.J - 1 : int.MaxValue)
            && (!any || ap.J < best.J)) { best = ap; any = true; }
        int slowMax = SlowdownMaxTicks(p, t);
        if (slowMax > 0 && Search(p, ball, LegsMode.Neutral, toBall, intent, now, t, pitch, minTicks, out var ez, any ? Math.Min(best.J - 1, slowMax) : slowMax)
            && (!any || ez.J < best.J)) { best = ez; any = true; }
        if (any) return true;
        // Nothing within the prediction horizon: the stick side as far as the meet refinement looks (a long push that ran long).
        return StickSidePlan(p, ball, intent, toBall, now, t, pitch, minTicks, Ticks(0.5f, t), out best);
    }

    /// <summary>The earliest stick-side swing (the stick, easing off, or a bend of at most approach_max_stick_deg).</summary>
    static bool StickSidePlan(PlayerState p, Ball ball, in TickIntent intent, Vector2 toBall, float now, Tuning t, Pitch? pitch,
        int minTicks, int extra, out Candidate best)
    {
        bool any = Search(p, ball, LegsMode.Stick, Vector2.Zero, intent, now, t, pitch, minTicks, out best, int.MaxValue, extra);
        float sa = MathF.Atan2(intent.Move.Y, intent.Move.X);
        float off = Movement.WrapAngle(MathF.Atan2(toBall.Y, toBall.X) - sa);
        float side = off < 0f ? -1f : 1f;
        // With the ball on the stick's line a miss is a push that ran long or wide: only bends up to the execution angle (a
        // push sent a little wide is followed by easing the run across to it; a run to the ball, or the recovery run, catches
        // a long one up sooner than a bent plan at the dribbling pace, but slows the run).
        float maxBend = MathF.Abs(off) * 180f / MathF.PI <= t.Touch.ExecutionAngle ? t.Touch.ExecutionAngle : t.Touch.ApproachMaxStickDeg;
        foreach (float deg in BendsDeg)
        {
            if (deg > maxBend + 1e-3f) break;
            float r = side * deg * MathF.PI / 180f;
            var bend = new Vector2(MathF.Cos(r), MathF.Sin(r));
            int max = any ? best.J - 1 : int.MaxValue;
            if (max < minTicks) break;
            if (!Search(p, ball, LegsMode.Stick, bend, intent, now, t, pitch, minTicks, out var c, max, extra)) continue;
            if (!any || c.J < best.J) { best = c; any = true; }
        }
        // Easing off only when it plays the ball at least a step sooner than running on (and within slowdown_max_ahead): with
        // the stick pushed the run is kept wherever it can be.
        int slowMax = SlowdownMaxTicks(p, t);
        if (any) slowMax = Math.Min(slowMax, best.J - 1 - StepTicks(p, t));
        if (slowMax >= minTicks && Search(p, ball, LegsMode.Neutral, Vector2.Zero, intent, now, t, pitch, minTicks, out var n, slowMax, extra))
        {
            best = n;
            any = true;
        }
        return any;
    }

    /// <summary>One step of the stride clock at the player's step frequency, ticks.</summary>
    static int StepTicks(PlayerState p, Tuning t) => (int)MathF.Ceiling(1f / (MathF.Max(p.StepFrequency, 1f) * t.Dt));

    /// <summary>
    /// The furthest swing a slowdown may be planned on with the stick pushed, ticks (slowdown_max_ahead); 0 for a recovering
    /// player, who runs on to the ball instead of easing off.
    /// </summary>
    static int SlowdownMaxTicks(PlayerState p, Tuning t) => p.Recovering ? 0 : Math.Max(1, Ticks(t.Touch.SlowdownMaxAhead, t));

    /// <summary>The bends tried, degrees: fine first (a small touch error), then coarser.</summary>
    static readonly float[] BendsDeg = { 1f, 2f, 3f, 4f, 5f, 7f, 10f, 15f, 20f, 25f, 30f, 35f, 40f, 45f };

    /// <summary>
    /// The early execution fraction for a turn of <paramref name="turnDeg"/> (the stick against the ball's travel): the share
    /// of the normal cadence a turn may cut it to, early_execution at the angle times the dribbling multiplier.
    /// </summary>
    static float EarlyFraction(PlayerState p, float turnDeg, Tuning t) =>
        t.Touch.EarlyExecution.Evaluate(turnDeg) * t.Touch.EarlyExecutionDribbling.ForRating(p.Sheet.Dribbling);

    static void Install(PlayerState p, BallAction a, int tick, Tuning t)
    {
        if (p.TouchAction is { Made: true } prev && prev.PhaseAt(tick, PreWindupTicks(t), WindupTicks(t)) != ActionPhase.Done) p.PrevTouchAction = prev;
        p.TouchAction = a;
        UpdatePhases(p, tick, t);
    }

    static ActionKind ModeKind(TouchMode m) => m switch
    {
        TouchMode.CloseControl => ActionKind.Close,
        TouchMode.Jog => ActionKind.Jog,
        _ => ActionKind.Sprint,
    };

    /// <summary>The foot surface for a kind; a turn is played with the outside of the foot on the side it turns to.</summary>
    static FootSurface SurfaceFor(ActionKind kind, Foot foot, float turnSign) => kind switch
    {
        ActionKind.Stop or ActionKind.Soft => FootSurface.Sole,
        ActionKind.Sprint or ActionKind.KnockOn or ActionKind.Heavy => FootSurface.Laces,
        ActionKind.Turn or ActionKind.Cut => (turnSign > 0f) == (foot == Foot.Right) ? FootSurface.Outside : FootSurface.Inside,
        _ => FootSurface.Inside,
    };

    /// <summary>A knock-on was asked for: the next touch made (or the one waiting) is a knock-on.</summary>
    internal static void RequestKnockOn(PlayerState p)
    {
        p.KnockOnRequested = true;
        if (p.TouchAction is { Pending: true } a && Pushable(a.Kind)) a.Kind = ActionKind.KnockOn;
    }

    /// <summary>Shoot was pressed in possession: a touch already winding up is made first, then the soft touch.</summary>
    internal static void OnShootPressed(PlayerState p, int tick, Tuning t)
    {
        UpdatePhases(p, tick, t);
        p.SoftWaitAction = p.TouchAction is { Pending: true, Phase: ActionPhase.PreWindup or ActionPhase.Windup } a ? a : null;
    }

    /// <summary>
    /// The shot was turned into a fake: a waiting soft touch (planned for the strike, never reviewed against the stick) is
    /// dropped so the next touch is planned afresh for the dribble.
    /// </summary>
    internal static void OnShotAborted(PlayerState p)
    {
        p.SoftWaitAction = null;
        if (p.TouchAction is { Pending: true, Kind: ActionKind.Soft } a) Drop(p, a);
    }

    /// <summary>The player no longer controls the ball: any waiting touch is dropped.</summary>
    internal static void OnPossessionLost(PlayerState p)
    {
        SkillMoves.Abort(p);
        if (p.TouchAction is { Pending: true } a) Drop(p, a);
        p.Replanning = false;
        p.EarlyTurnTick = -1;
        p.Recovering = false;
        p.KnockOnRequested = false;
        p.SoftWaitAction = null;
        p.ActionContactEta = float.NaN;
    }

    // ---- the tick's touch ----

    /// <summary>
    /// After the player has moved this tick: makes a contact that is due (or drops it if the ball is not in the zone, or the
    /// foot is not the one in swing), the soft touch while shoot is held, a first touch of a loose ball, and plans the next
    /// touch when none is waiting. Returns the touch made, if any.
    /// </summary>
    internal static TouchResult? Step(PlayerState p, in TickIntent rawIntent, Ball ball, bool inPossession, TouchStepMode mode,
        int tick, float now, Tuning t, Pitch? pitch, SimRng rng)
    {
        // A skill move under way plays the ball itself, whatever the shoot button does.
        if (inPossession && p.SkillAction != null) return SkillMoves.Step(p, ball, tick, now, t);
        if (mode == TouchStepMode.None) return null;
        if (!inPossession) return mode == TouchStepMode.Normal ? FirstTouch(p, rawIntent, ball, tick, now, t, pitch, rng) : null;
        var intent = Debounced(p, rawIntent, t);
        UpdatePhases(p, tick, t);

        var a = p.TouchAction;
        if (a is { Pending: true } && tick >= a.ContactTick)
        {
            if (mode == TouchStepMode.SoftOnly && a != p.SoftWaitAction) a.Kind = ActionKind.Soft;
            var r = Execute(p, a, intent, ball, tick, now, t, pitch, rng, immediate: false);
            if (a == p.SoftWaitAction) p.SoftWaitAction = null;
            if (r != null) return r;
        }

        a = p.TouchAction;
        if (mode == TouchStepMode.SoftOnly)
        {
            if (a is { Pending: true } && a == p.SoftWaitAction) return null;
            if (TouchModel.InReach(p, ball, t) && now - p.LastTouchTime > 0f)
            {
                if (a is { Pending: true }) Drop(p, a);
                var s = new BallAction { Kind = ActionKind.Soft, PlanTick = tick, ContactTick = tick };
                s.Foot = p.IsStepping ? p.SwingFoot : TouchModel.ChooseFoot(p.Sheet, p.Position, p.Facing, ball.Position, t);
                Install(p, s, tick, t);
                return Execute(p, s, intent, ball, tick, now, t, pitch, rng, immediate: true);
            }
            if (a is { Pending: true }) { a.Kind = ActionKind.Soft; a.Surface = FootSurface.Sole; return null; }
            FreshPlan(p, intent, ball, tick, now, t, pitch, ActionKind.Soft);
            return null;
        }

        // The tick a skill move ended: with the stick pushed and the ball in foot reach, the exit touch is made at once toward
        // the stick (an early touch, like a first touch), so the move flows into the run instead of waiting for a stride.
        if (p.SkillExitTick == tick && a is not { Pending: true } && intent.Move.Length() >= t.Touch.StickThreshold && TouchModel.InReach(p, ball, t))
        {
            p.SkillExitTick = -1;
            p.Replanning = false;
            var exit = new BallAction { Kind = ModeKind(TouchModel.ModeFor(intent, t)), PlanTick = tick, ContactTick = tick, Legs = LegsMode.Stick };
            exit.Foot = p.IsStepping ? p.SwingFoot : TouchModel.ChooseFoot(p.Sheet, p.Position, p.Facing, ball.Position, t);
            Install(p, exit, tick, t);
            return Execute(p, exit, intent, ball, tick, now, t, pitch, rng, immediate: true);
        }
        if (a is not { Pending: true }) FreshPlan(p, intent, ball, tick, now, t, pitch, null);
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
    }

    /// <summary>
    /// Predicts the player from now with the legs following <paramref name="user"/> and finds the swing middle
    /// <paramref name="steps"/> steps on (at least min_plan_ahead ahead), or the one nearest <paramref name="nearTicks"/> when
    /// that is set.
    /// </summary>
    static bool FindMeet(PlayerState from, in TickIntent user, int steps, int nearTicks, float now, Tuning t, Pitch? pitch, out Meet meet)
    {
        meet = default;
        int pre = PlanAheadTicks(t);
        var c = from.Scratch();
        int horizon = nearTicks > 0 ? nearTicks + Ticks(1f, t) : Ticks(3f, t);
        int count = 0, bestGap = int.MaxValue;
        bool found = false;
        float tn = now;
        for (int j = 1; j <= horizon; j++)
        {
            tn += t.Dt;
            PredictStep(c, LegsMode.Stick, default, user, null, tn, t, pitch, Lead(from, tn - now, t));
            if (c.SwingMiddleCrossed is not Foot f) continue;
            count++;
            if (j < pre) continue;
            if (nearTicks > 0)
            {
                int gap = Math.Abs(j - nearTicks);
                if (gap < bestGap) { bestGap = gap; meet = new Meet { J = j, Foot = f, Cp = ContactPointFor(c, f, t) }; found = true; }
                else if (j > nearTicks) break;
                continue;
            }
            if (count >= steps) { meet = new Meet { J = j, Foot = f, Cp = ContactPointFor(c, f, t) }; return true; }
        }
        return found;
    }

    /// <summary>
    /// After the push is made (errors included): keeps the planned meet if the real ball can still be steered into its contact
    /// zone, otherwise moves it to the nearest swing (the same legs) where it can. False when no swing in the horizon works.
    /// </summary>
    static bool RefineMeet(PlayerState from, Ball ball, in TickIntent user, ref Meet meet, float now, Tuning t, Pitch? pitch)
    {
        var k = t.Touch;
        int pre = PlanAheadTicks(t);
        float budget = SteerBudget(k.PreWindup, t);
        int horizon = Math.Max(meet.J, Ticks(t.Possession.PredictionHorizon, t)) + Ticks(0.5f, t);
        var c = from.Scratch();
        var b = BallScratch(ball);
        bool found = false;
        Meet best = default;
        int bestGap = int.MaxValue;
        float bestMiss = float.MaxValue;
        float tn = now;
        for (int j = 1; j <= horizon; j++)
        {
            tn += t.Dt;
            PredictStep(c, LegsMode.Stick, default, user, b, tn, t, pitch, Lead(from, tn - now, t));
            b.Step(t.Dt, null, null);
            if (found && j - meet.J > bestGap) break;
            if (c.SwingMiddleCrossed is not Foot f || j < pre) continue;
            Vector3 cp = ContactPointFor(c, f, t);
            float miss = Horizontal(b.Position, cp);
            if (miss > budget || b.Position.Y > k.MaxBallHeight) continue;
            int gap = Math.Abs(j - meet.J);
            if (gap < bestGap || gap == bestGap && miss < bestMiss)
            {
                best = new Meet { J = j, Foot = f, Cp = cp };
                bestGap = gap; bestMiss = miss; found = true;
            }
        }
        if (found) meet = best;
        return found;
    }

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
            var b = BallScratch2(ball);
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
    /// <see cref="SolvePush"/> at the touch's rated lift, falling back to the lowest lift of the rated range and then to a
    /// ground pass when the rated lift cannot land the ball on the target (a gap in reachable distances).
    /// </summary>
    static float SolveLiftedPush(PlayerState p, Ball ball, Vector3 target, int ticks, ActionKind kind, TouchMode mode, float lo,
        float hi, Tuning t, out Vector3 dir, out bool @short, out float liftDeg)
    {
        liftDeg = TouchModel.LiftDeg(p.Sheet, kind, mode, t);
        var range = mode == TouchMode.Sprint || kind == ActionKind.KnockOn ? t.Touch.LiftDeg.Sprint : t.Touch.LiftDeg.Jog;
        float u = SolvePush(ball, target, ticks, liftDeg, lo, hi, t, out dir, out @short, out float residual);
        if (@short || residual <= SolveTolerance) return u;
        foreach (float alt in new[] { MathF.Min(range.At1, range.At99), 0f })
        {
            float u2 = SolvePush(ball, target, ticks, alt, lo, hi, t, out var dir2, out bool short2, out float res2);
            if (res2 < residual) { u = u2; dir = dir2; @short = short2; residual = res2; liftDeg = alt; }
            if (residual <= SolveTolerance) break;
        }
        return u;
    }

    /// <summary>A solved push that ends further than this from its target falls back to a lower lift, metres.</summary>
    const float SolveTolerance = 0.01f;

    /// <summary>
    /// Makes a contact: checks the ball is in the contact zone and the foot is the one in swing (unless
    /// <paramref name="immediate"/>), sets the ball's velocity and spin for the kind, draws the rated errors, starts the boost,
    /// and plans the next touch on the next meet. Returns null (and drops the action) when the contact cannot be made.
    /// </summary>
    static TouchResult? Execute(PlayerState p, BallAction a, in TickIntent intent, Ball ball, int tick, float now, Tuning t,
        Pitch? pitch, SimRng rng, bool immediate)
    {
        var k = t.Touch;
        Vector3 cp = immediate ? new Vector3(ball.Position.X, t.Ball.Radius, ball.Position.Z) : ContactPointFor(p, a.Foot, t);
        float miss = immediate ? 0f : Horizontal(ball.Position, cp);
        if (!immediate)
        {
            // Beyond the contact zone, a ball within stretch_reach is still played (the leg stretches for it, the rated errors
            // as usual); only a ball further off, too high or a foot not in swing drops the touch.
            bool footOk = !p.IsStepping || p.SwingFoot == a.Foot;
            float reach = MathF.Max(k.ContactZoneRadius, k.StretchReach);
            if (miss > reach || ball.Position.Y > k.MaxBallHeight || !footOk) { Drop(p, a); return null; }
        }

        ActionKind kind = a.Kind;
        if (p.KnockOnRequested && Pushable(kind)) kind = ActionKind.KnockOn;
        Foot foot = a.Foot;
        var (sigmaS, sigmaD) = Sigmas(p, foot, kind, t);
        var mode = TouchModel.ModeFor(intent, t);
        bool stickActive = intent.Move.Length() >= k.StickThreshold;
        Vector3 v, spin = Vector3.Zero;
        float errDeg = 0f, turnSign = 0f;
        BallAction? next = null;
        Meet meet = default;
        bool hasMeet = false;
        p.LastTouchTime = now;
        p.LastTouchFoot = foot;

        switch (kind)
        {
            case ActionKind.Soft:
                // Never faster than the player: a soft touch must not push the ball out of strike reach. It leaves rolling.
                v = TouchModel.Flat(p.Velocity) * (1f - MathF.Abs(rng.Normal(0f, sigmaS)));
                spin = ball.RollingSpin(v);
                break;
            case ActionKind.Stop:
            {
                // Brought to rest about 0.35 m ahead of where the player stops; a sole touch leaves the ball rolling.
                Vector3 dir = TouchModel.StopDirection(p);
                v = dir * (TouchModel.StopSpeed(p, p.Position, ball.Position, dir, t) * rng.Normal(1f, sigmaS));
                spin = ball.RollingSpin(v);
                break;
            }
            case ActionKind.KnockOn:
            {
                // Planned so the player, chasing without the dribble cap, meets the ball on the swing nearest the catch time.
                float catchTime = TouchModel.KnockOnCatchTime(p.Sheet, t);
                int catchTicks = Math.Max(1, (int)MathF.Round(catchTime / t.Dt));
                float aim = stickActive ? MathF.Atan2(intent.Move.Y, intent.Move.X) : p.Facing;
                var aimDir = new Vector2(MathF.Cos(aim), MathF.Sin(aim));
                p.KnockOnFrom = now;
                p.KnockOnUntil = now + catchTicks * t.Dt + k.KnockOnChaseGrace;
                p.KnockOnDirection = aimDir;
                var user = stickActive ? intent : new TickIntent { MoveX = aimDir.X, MoveZ = aimDir.Y, Sprint = 1f };
                errDeg = rng.Normal(0f, sigmaD);
                hasMeet = FindMeet(p, user, 0, catchTicks, now, t, pitch, out meet);
                Vector3 target = hasMeet ? meet.Cp : p.Position + new Vector3(aimDir.X, 0f, aimDir.Y) * 10f;
                int ticks = hasMeet ? meet.J : catchTicks;
                float u = SolveLiftedPush(p, ball, target, ticks, kind, TouchMode.Sprint, TouchModel.MinPush(TouchMode.Sprint, t),
                    MathF.Max(k.KnockOnMaxPush, k.MinPush), t, out var dir, out _, out float lift);
                v = Launch(dir, u, lift, errDeg) * rng.Normal(1f, sigmaS);
                if (hasMeet) hasMeet = RefineAfterLaunch(p, ball, v, user, ref meet, now, t, pitch);
                if (hasMeet) next = NewPlan(p, ActionKind.Sprint, meet, intent, tick, t, stickActive);
                break;
            }
            default:
            {
                // A push (a dribble touch, a turn, a slowdown, or a first touch with the stick pushed).
                bool first = kind == ActionKind.First;
                Vector3 relVel = TouchModel.Flat(ball.Velocity - p.Velocity);
                if (first)
                {
                    float extra = TouchModel.FirstTouchSigmaDeg(p.Sheet, relVel.Length(), t) * TouchModel.FootErrorFactor(p.Sheet, foot, t);
                    sigmaD = MathF.Sqrt(sigmaD * sigmaD + extra * extra);
                }
                errDeg = rng.Normal(0f, sigmaD);
                if (!stickActive)
                {
                    // A first touch with the stick released stops the ball relative to the player, badly for a poor first touch.
                    v = TouchModel.Flat(p.Velocity) + TouchModel.Rotate(relVel * TouchModel.FirstTouchSpeedKept(p.Sheet, t), errDeg);
                    spin = ball.RollingSpin(v);
                    break;
                }
                // The turn is the stick's wanted direction against the ball's travel (the facing for a still ball).
                var wanted = Vector3.Normalize(new Vector3(intent.Move.X, 0f, intent.Move.Y));
                float turnDeg = TouchModel.TurnAngleDeg(p, ball, wanted);
                int steps = NextSteps(p, mode, t);
                if (!first)
                {
                    // A turn cuts the push to the early fraction of the cadence, so the next touch can follow the stick sooner.
                    bool turning = p.RunSpeed > k.ExecutionMinSpeed && turnDeg > k.ExecutionAngle;
                    if (turning) steps = Math.Max(1, (int)MathF.Round(EarlyFraction(p, turnDeg, t) * steps));
                    // While the stick is moving or a turn is under way the ball is kept within a few steps, so a turn that
                    // follows can be played at the next swing; straight-line touches keep their cadence.
                    if (turning || MathF.Abs(p.StickRate) * 180f / MathF.PI > k.TurningStickRateDeg)
                        steps = Math.Min(steps, mode == TouchMode.Sprint ? k.TurningMaxSteps.Sprint : mode == TouchMode.Jog ? k.TurningMaxSteps.Jog : steps);
                }
                hasMeet = FindMeet(p, intent, steps, 0, now, t, pitch, out meet);
                Vector3 target = hasMeet ? meet.Cp : p.Position + new Vector3(intent.Move.X, 0f, intent.Move.Y) * 2f;
                var toTarget = new Vector3(target.X - ball.Position.X, 0f, target.Z - ball.Position.Z);
                if (!first)
                {
                    if (a.Legs == LegsMode.Neutral) kind = ActionKind.Slowdown;
                    else kind = ModeKind(mode);
                    if (p.RunSpeed > k.ExecutionMinSpeed && turnDeg > k.ExecutionAngle) kind = ActionKind.Turn;
                    if (turnDeg > k.CutDeg && p.RunSpeed > k.CutMinSpeed)
                    {
                        kind = ActionKind.Cut;
                        // The cut costs the balance percentage of speed, shed over the next ticks at balance_deceleration; the next
                        // meet is found again from the slowing run.
                        p.SpeedDebt += p.RunSpeed * Movement.BalanceLoss(p.Sheet, t);
                        hasMeet = FindMeet(p, intent, steps, 0, now, t, pitch, out meet);
                        if (hasMeet) target = meet.Cp;
                    }
                }
                // The side the ball is turned to (it picks the foot's inside or outside): by the stick, as the view expects while the
                // touch winds up; the push's own direction can fall on the other side of a turn about.
                var turnTo = kind is ActionKind.Turn or ActionKind.Cut ? new Vector3(intent.Move.X, 0f, intent.Move.Y) : toTarget;
                turnSign = MathF.Sign(Movement.WrapAngle(MathF.Atan2(turnTo.Z, turnTo.X) - MathF.Atan2(ball.Velocity.Z, ball.Velocity.X)));
                if (kind is ActionKind.Turn or ActionKind.Cut) p.PostTurnUntil = now + t.Movement.PostTurnSeconds;
                if (kind is not (ActionKind.KnockOn or ActionKind.First or ActionKind.Slowdown))
                {
                    p.TouchBoostFrom = now;
                    p.TouchBoostUntil = now + k.TouchBoostDuration;
                    p.TouchBoostFactor = TouchModel.TouchBoostFactor(p.Sheet, turnDeg, t);
                    SkillMoves.ApplyExitBurst(p, now);
                    if (hasMeet) hasMeet = FindMeet(p, intent, steps, 0, now, t, pitch, out meet);
                    if (hasMeet) target = meet.Cp;
                }
                float minPush = TouchModel.MinPush(mode, t);
                int ticks = hasMeet ? meet.J : Ticks(0.6f, t);
                float u = SolveLiftedPush(p, ball, target, ticks, kind, mode, minPush, MathF.Max(k.MaxPush, minPush), t, out var dir,
                    out bool tooShort, out float lift);
                if (tooShort && !first)
                {
                    kind = ActionKind.Heavy;
                    lift = TouchModel.LiftDeg(p.Sheet, kind, mode, t);
                    hasMeet = false;
                }
                v = Launch(dir, u, lift, errDeg) * rng.Normal(1f, sigmaS);
                if (first) v += relVel * TouchModel.FirstTouchSpeedKept(p.Sheet, t);
                if (hasMeet) hasMeet = RefineAfterLaunch(p, ball, v, intent, ref meet, now, t, pitch);
                if (hasMeet) next = NewPlan(p, ModeKind(mode), meet, intent, tick, t, true);
                break;
            }
        }

        SkillMoves.ApplyExitBurst(p, now); // an exit touch of a kind without the touch boost still starts the burst
        p.LastTouchKind = kind;
        p.KnockOnRequested = false;
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
        a.Surface = SurfaceFor(kind, foot, turnSign);
        a.Phase = ActionPhase.Contact;
        if (hasMeet)
        {
            a.NextMeetTick = tick + meet.J;
            a.NextMeetPoint = meet.Cp;
            a.NextMeetFoot = meet.Foot;
        }
        p.TouchAction = a;
        p.PrevTouchAction = null;
        if (next != null) Install(p, next, tick, t);
        // A push whose next meet could not be kept (a short or wide push off the rated errors): the touch after it may be
        // planned on a swing as close as the windup, so the ball is played again rather than waited for by easing off.
        else if (stickActive && kind is not (ActionKind.KnockOn or ActionKind.Heavy or ActionKind.Stop or ActionKind.Soft)) p.Replanning = true;
        return new TouchResult(kind, foot, ball.Position, v, errDeg);
    }

    /// <summary>Checks the next meet against the ball as actually launched (velocity <paramref name="v"/>, no spin).</summary>
    static bool RefineAfterLaunch(PlayerState p, Ball ball, Vector3 v, in TickIntent user, ref Meet meet, float now, Tuning t, Pitch? pitch)
    {
        Vector3 keepV = ball.Velocity, keepW = ball.Spin;
        ball.Velocity = v;
        ball.Spin = Vector3.Zero;
        bool ok = RefineMeet(p, ball, user, ref meet, now, t, pitch);
        ball.Velocity = keepV;
        ball.Spin = keepW;
        return ok;
    }

    /// <summary>The launch velocity along ground direction <paramref name="dir"/> at <paramref name="speed"/> and the lift, turned by the error.</summary>
    static Vector3 Launch(Vector3 dir, float speed, float liftDeg, float errDeg)
    {
        float lift = liftDeg * MathF.PI / 180f;
        var v = dir * (speed * MathF.Cos(lift)) + new Vector3(0f, speed * MathF.Sin(lift), 0f);
        return TouchModel.Rotate(v, errDeg);
    }

    static BallAction NewPlan(PlayerState p, ActionKind kind, in Meet meet, in TickIntent intent, int tick, Tuning t, bool stickActive) => new()
    {
        Kind = kind,
        Foot = meet.Foot,
        Surface = SurfaceFor(kind, meet.Foot, 0f),
        PlanTick = tick,
        ContactTick = tick + meet.J,
        ContactPoint = meet.Cp,
        Legs = LegsMode.Stick,
        PlanStickAngle = stickActive ? MathF.Atan2(intent.Move.Y, intent.Move.X) : float.NaN,
        PlanMode = TouchModel.ModeFor(intent, t),
    };

    /// <summary>
    /// First contact with a loose ball: as soon as it is in foot reach (and the last touch is at least turn_min_interval ago),
    /// with the first_touch errors. With the stick pushed the ball is pushed to the next meet like a dribble touch.
    /// </summary>
    static TouchResult? FirstTouch(PlayerState p, in TickIntent intent, Ball ball, int tick, float now, Tuning t, Pitch? pitch, SimRng rng)
    {
        var k = t.Touch;
        if (!TouchModel.InReach(p, ball, t)) return null;
        if (now - p.LastTouchTime < k.TurnMinInterval) return null;
        bool stickActive = intent.Move.Length() >= k.StickThreshold;
        float rel = TouchModel.Flat(ball.Velocity - p.Velocity).Length();
        if (!stickActive && rel < k.FirstTouchMinRelativeSpeed) return null; // a still ball and no stick: stand by it
        var a = new BallAction { Kind = ActionKind.First, PlanTick = tick, ContactTick = tick, Legs = LegsMode.Stick };
        a.Foot = p.IsStepping ? p.SwingFoot : TouchModel.ChooseFoot(p.Sheet, p.Position, p.Facing, ball.Position, t);
        p.TouchAction = a;
        p.PrevTouchAction = null;
        p.Recovering = false;
        return Execute(p, a, intent, ball, tick, now, t, pitch, rng, immediate: true);
    }
}
