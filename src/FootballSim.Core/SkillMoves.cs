using System.Numerics;

namespace FootballSim.Core;

/// <summary>
/// Skill moves as committed actions. A recognised gesture (see <see cref="GestureReader"/>) is buffered; the move starts when
/// the player has the ball within the execution radius (step_over_radius for a step-over) and below max_ball_height, or is
/// dropped after buffer_seconds. Starting drops the waiting touch. While the move lasts it owns the legs and the ball: each
/// move sets the running speed along the running direction it started on, a steering velocity, and for the sole moves the
/// ball's path. When it ends the touch planner plans at once toward the left stick, so a move never loses the ball.
/// <list type="bullet">
/// <item>Step-over and body feint: no contact; the player eases to the ball's pace so it ends pace_match_ahead in front.</item>
/// <item>Ball roll: the sole rolls the ball sideways toward the side (ball_roll_distance by speed) and the player follows.</item>
/// <item>Drag-back: the sole pulls the ball back drag_back_distance; the player stops and steps back drag_back_step_back.</item>
/// <item>Fake stop: the ball is stopped under the sole and the player stops. Fake cut: the ball is hooked toward the left stick.</item>
/// </list>
/// A move ends with an exit burst through the touch boost: larger for a better effective skill and for an exit away from the
/// side the move sold. Moves chained within chain_window use chain_multiplier times the previous effective skill.
/// </summary>
public static class SkillMoves
{
    static int Ticks(float seconds, Tuning t) => Math.Max(1, (int)MathF.Round(seconds * t.TickRate));

    /// <summary>The drag-back's run stops over this first share of the move; the step back takes the whole move.</summary>
    const float DragBackStopShare = 0.4f;
    /// <summary>The fake cut keeps this share of its entry speed by the end of the move (the plant before the hook).</summary>
    const float FakeCutSpeedKept = 0.5f;
    /// <summary>The step-over and feint pacing spreads the gap over at least this long, seconds ...</summary>
    const float PaceHorizon = 0.1f;
    /// <summary>... and changes the running speed by at most this, m/s squared (a hard stride, not a jolt).</summary>
    const float PaceAccel = 12f;
    /// <summary>From a standstill or a walk the step-over and feint may step up to the ball at this pace, m/s.</summary>
    const float PaceApproach = 1.5f;
    /// <summary>A move's exit burst is lost if no boosting touch follows the move within this long, seconds.</summary>
    const float ExitTouchWithin = 1.0f;
    /// <summary>The reach is planned this much inside sole_reach_tolerance, for what the prediction does not see, metres.</summary>
    const float ReachMargin = 0.05f;
    /// <summary>How far from the feet the sole's foot may be when a move starts (half a stride), metres.</summary>
    const float SoleStrideAllowance = 0.35f;
    /// <summary>The step-over and feint close in so the ball is this much inside step_over_max_ahead at the move's middle, metres ...</summary>
    const float CloseInMargin = 0.05f;
    /// <summary>... running at most this much faster than they came in to do it, m/s.</summary>
    const float CloseInExtra = 1.5f;
    /// <summary>The longest close-in before a step-over or feint, seconds.</summary>
    const float CloseInMax = 0.15f;

    /// <summary>Clears every skill-move state of the player (a new round).</summary>
    internal static void Reset(PlayerState p)
    {
        p.SkillAction = null;
        p.SkillBufKind = Gesture.None;
        p.SkillBufSide = SkillSide.None;
        p.SkillBufCut = Vector2.Zero;
        p.SkillBufTick = -1;
        p.SkillExitTick = -1;
        p.SkillCooldownUntil = -100f;
        p.SkillChainUntil = -100f;
        p.SkillBoostUntil = -100f;
        p.SkillBoostFactor = 1f;
        p.SkillBoostDuration = 0f;
        p.SkillBoostCap = 0f;
        p.SkillBurstUntil = -100f;
        p.SkillBurstFactor = 1f;
        p.SkillBurstCap = 0f;
        p.SkillEffective = 0f;
        p.SkillChainCount = 0;
    }

    /// <summary>The action kind of a skill gesture.</summary>
    public static ActionKind KindOf(Gesture g) => g switch
    {
        Gesture.StepOver => ActionKind.StepOver,
        Gesture.BodyFeint => ActionKind.BodyFeint,
        Gesture.BallRoll => ActionKind.BallRoll,
        Gesture.DragBack => ActionKind.DragBack,
        Gesture.FakeStop => ActionKind.FakeStop,
        Gesture.FakeCut => ActionKind.FakeCut,
        _ => throw new ArgumentException($"{g} is not a skill move", nameof(g)),
    };

    /// <summary>How close the ball must be for a move to start, metres.</summary>
    public static float Radius(ActionKind kind, Tuning t) => kind == ActionKind.StepOver ? t.Skill.StepOverRadius : t.Skill.ExecutionRadius;

    /// <summary>A recognised skill gesture: it waits (at most buffer_seconds) for the move to be able to start.</summary>
    internal static void Request(PlayerState p, in GestureRead g, int tick)
    {
        p.SkillBufKind = g.Kind;
        p.SkillBufSide = g.Side;
        p.SkillBufCut = g.CutDirection;
        p.SkillBufTick = tick;
    }

    /// <summary>The move under way is abandoned (possession lost, a strike): no exit burst, nothing buffered.</summary>
    internal static void Abort(PlayerState p)
    {
        if (p.SkillAction is { } a) a.Phase = ActionPhase.Done;
        p.SkillAction = null;
        p.SkillBufTick = -1;
        p.SkillBoostUntil = -100f;
        p.SkillBurstUntil = -100f;
    }

    /// <summary>
    /// Starts the buffered move if it can start now (in possession, no move under way, past a drag-back's cooldown, the ball
    /// low enough and in the execution radius, or arriving at the foot for the planned touch), before the player moves this
    /// tick. Returns the SkillMove event, or null.
    /// <para>
    /// With a push in flight (a touch planned within meet_wait_seconds of the gesture) the gesture waits for that touch's
    /// meet: on its contact tick the ball is at the foot, and the move starts instead of the touch. Without one it waits
    /// buffer_seconds for the ball to come into reach.
    /// </para>
    /// </summary>
    internal static SimEvent? TryStart(PlayerState p, Ball ball, bool inPossession, int tick, float now, Tuning t)
    {
        var k = t.Skill;
        if (p.SkillBufTick < 0) return null;
        var meet = p.TouchAction is { Pending: true } pa && pa.ContactTick - p.SkillBufTick <= Ticks(k.MeetWaitSeconds, t) ? pa : null;
        if (tick - p.SkillBufTick > Ticks(meet != null ? k.MeetWaitSeconds : k.BufferSeconds, t)) { p.SkillBufTick = -1; return null; }
        if (!inPossession || p.SkillAction != null || now < p.SkillCooldownUntil) return null;
        var kind = KindOf(p.SkillBufKind);
        if (ball.Position.Y >= k.MaxBallHeight) return null;
        float dist = TouchModel.HorizontalDistance(p.Position, ball.Position);
        // At the meet the ball is being steered into the contact zone ahead of the foot: within the zone's slack of the radius.
        bool atMeet = meet != null && tick >= meet.ContactTick && dist <= Radius(kind, t) + t.Touch.ContactZoneRadius;
        if (dist > Radius(kind, t) && !atMeet) return null;
        // With a meet still to come, a move that would start awkwardly waits for it: a step-over or feint with the ball too far
        // ahead for the leg to reach over it, a sole move whose foot cannot land on the ball within the reach.
        if (meet != null && !atMeet)
        {
            Vector2 fwd = GestureReader.FrameForward(p, t);
            if (kind is ActionKind.StepOver or ActionKind.BodyFeint
                && Vector2.Dot(new Vector2(ball.Position.X - p.Position.X, ball.Position.Z - p.Position.Z), fwd) > k.StepOverMaxAhead)
                return null;
            if (IsSole(kind) && PlanReach(p, FootFor(p, kind, p.SkillBufSide, ball, t), fwd, ball, now, t).Miss > k.SoleReachTolerance - ReachMargin)
                return null;
        }
        p.SkillBufTick = -1;
        var a = Start(p, kind, p.SkillBufSide, p.SkillBufCut, ball, tick, now, t);
        return new SimEvent(EventKind.SkillMove, ball.Position, p.Speed, Foot: a.Foot, Team: p.Team, PlayerIndex: p.Index, Skill: kind);
    }

    /// <summary>True for the moves whose sole lands on the ball after a reach: ball roll, drag-back, fake stop.</summary>
    static bool IsSole(ActionKind kind) => kind is ActionKind.BallRoll or ActionKind.DragBack or ActionKind.FakeStop;

    /// <summary>The foot that plays a move.</summary>
    static Foot FootFor(PlayerState p, ActionKind kind, SkillSide side, Ball ball, Tuning t) => kind switch
    {
        ActionKind.StepOver or ActionKind.BodyFeint => side == SkillSide.Left ? Foot.Left : Foot.Right,
        // The sole of the far foot rolls the ball across the body toward the side.
        ActionKind.BallRoll => side == SkillSide.Left ? Foot.Right : Foot.Left,
        ActionKind.DragBack => TouchModel.ChooseFoot(p.Sheet, p.Position, p.Facing, ball.Position, t),
        _ => p.StrikeFoot,
    };

    /// <summary>
    /// The reach of a sole move: predicts the player running on at his speed along <paramref name="fwd"/> and the free ball,
    /// and returns the first tick offset (0 to sole_reach_max) at which the foot's contact point can be brought within
    /// sole_reach_tolerance of the ball by steering at most sole_reach_steer and the foot can have got there at
    /// sole_reach_foot_speed, with the miss left there; if none can, the offset with the smallest miss.
    /// </summary>
    static (int Ticks, float Miss) PlanReach(PlayerState p, Foot foot, Vector2 fwd, Ball ball, float now, Tuning t)
    {
        var k = t.Skill;
        var c = p.Scratch();
        var b = ball.ScratchA();
        float entry = MathF.Max(0f, Vector2.Dot(new Vector2(p.Velocity.X, p.Velocity.Z), fwd));
        float cap = Possession.SpeedCap(p, true, false, t);
        var legs = new TickIntent { MoveX = fwd.X, MoveZ = fwd.Y, Sprint = 1f };
        int max = Math.Max(0, (int)MathF.Round(k.SoleReachMax * t.TickRate));
        int best = 0;
        float bestMiss = float.MaxValue;
        for (int j = 0; j <= max; j++)
        {
            // Contact on tick +j: the player has moved j + 1 times, the ball j times (it moves after the player each tick).
            Movement.Step(c, legs, t.Dt, now + (j + 1) * t.Dt, new MoveAssist(cap, 1f, Vector2.Zero, MathF.Min(entry, cap)), t, null);
            if (j > 0) b.Step(t.Dt, null, null);
            var cp = TouchPlanner.ContactPointFor(c, foot, t);
            float d = new Vector2(b.Position.X - cp.X, b.Position.Z - cp.Z).Length();
            float miss = MathF.Max(0f, d - k.SoleReachSteer * j * t.Dt);
            // The foot needs time to get there from wherever it is in the stride (up to a half stride from the feet): no faster
            // than sole_reach_foot_speed, so the drawn foot never snaps onto the ball.
            float fromFeet = new Vector2(b.Position.X - c.Position.X, b.Position.Z - c.Position.Z).Length() + SoleStrideAllowance;
            bool footThere = j == max || k.SoleReachFootSpeed <= 0f || fromFeet <= k.SoleReachFootSpeed * (j + 1) * t.Dt;
            if (footThere && miss <= k.SoleReachTolerance - ReachMargin) return (j, miss);
            if (miss < bestMiss) { bestMiss = miss; best = j; }
        }
        return (best, bestMiss);
    }

    /// <summary>
    /// The touch planner's turn at the feet (see TouchPlanner): a drag-back whose sole pulls the ball onto the stick's line
    /// <paramref name="dir"/> (unit, world X, Z) instead of straight back, the player stepping that way as he stops. It is not a
    /// skill move of the player's: no gesture, no chain, no exit burst; the drag-back's cooldown keeps it from starting again
    /// before the exit touch has sent the ball along the stick.
    /// </summary>
    internal static BallAction StartFeetTurn(PlayerState p, Vector2 dir, Ball ball, int tick, float now, Tuning t) =>
        Start(p, ActionKind.DragBack, SkillSide.None, dir, ball, tick, now, t, feetTurn: true);

    static BallAction Start(PlayerState p, ActionKind kind, SkillSide side, Vector2 cut, Ball ball, int tick, float now, Tuning t,
        bool feetTurn = false)
    {
        var k = t.Skill;
        if (!feetTurn)
        {
            p.SkillChainCount = now < p.SkillChainUntil ? p.SkillChainCount + 1 : 0;
            p.SkillEffective = p.Sheet.SkillMoves * MathF.Pow(k.ChainMultiplier, p.SkillChainCount);
            p.SkillBoostUntil = -100f; // an earlier move's burst, waiting for its touch or under way, is gone
            p.SkillBurstUntil = -100f;
        }

        // The waiting touch is dropped; the move owns the ball until it ends.
        if (p.TouchAction is { Pending: true } waiting) waiting.Phase = ActionPhase.Done;
        p.Replanning = false;
        p.EarlyTurnTick = -1;
        p.SoftWaitAction = null;
        p.Recovering = false;
        p.KnockOnRequested = false;

        bool contactless = kind is ActionKind.StepOver or ActionKind.BodyFeint;
        Vector2 fwd = GestureReader.FrameForward(p, t);
        float entry = MathF.Max(0f, Vector2.Dot(new Vector2(p.Velocity.X, p.Velocity.Z), fwd));
        float ballSpeed = new Vector2(ball.Velocity.X, ball.Velocity.Z).Length();
        float seconds = kind switch
        {
            ActionKind.StepOver => ballSpeed < k.StillBallSpeed ? k.StepOverStillSeconds : k.StepOverSeconds,
            ActionKind.BodyFeint => k.BodyFeintSeconds,
            ActionKind.BallRoll => p.SkillChainCount == 0 ? k.BallRollFirstSeconds : k.BallRollChainSeconds,
            ActionKind.DragBack => k.DragBackSeconds,
            _ => k.FakeSeconds,
        };
        int dur = Ticks(seconds, t);
        Foot foot = FootFor(p, kind, side, ball, t);
        int reach = IsSole(kind) ? PlanReach(p, foot, fwd, ball, now, t).Ticks : contactless ? CloseInTicks(p, ball, fwd, entry, t) : 0;
        FootSurface surface = kind switch
        {
            ActionKind.BallRoll or ActionKind.DragBack or ActionKind.FakeStop => FootSurface.Sole,
            // A hook toward the foot's own side is played with the outside of the foot.
            ActionKind.FakeCut => (side == SkillSide.Right) == (foot == Foot.Right) ? FootSurface.Outside : FootSurface.Inside,
            ActionKind.StepOver => FootSurface.Outside,
            _ => FootSurface.Inside,
        };
        var a = new BallAction
        {
            Kind = kind,
            Side = kind is ActionKind.DragBack or ActionKind.FakeStop ? SkillSide.None : side,
            Foot = foot,
            Surface = surface,
            StartTick = tick,
            PlanTick = tick,
            // After the reach (a sole move's foot reaching for the ball, a step-over closing in) the move runs its full time:
            // a sole lands at once, the step-over's foot passes the ball half way.
            ExecuteTick = tick + reach,
            ContactTick = contactless ? tick + reach + dur / 2 : tick + reach,
            EndTick = tick + reach + dur,
            ContactPoint = new Vector3(ball.Position.X, t.Ball.Radius, ball.Position.Z),
            Legs = LegsMode.Hold,
            HoldDirection = fwd,
            SkillEntry = entry,
            SkillCut = cut,
            SkillEffective = p.SkillEffective,
            FeetTurn = feetTurn,
        };
        if (p.TouchAction is { Made: true } prev && prev.PhaseAt(tick, TouchPlanner.PreWindupTicks(t), TouchPlanner.WindupTicks(t)) != ActionPhase.Done)
            p.PrevTouchAction = prev;
        else p.PrevTouchAction = null;
        p.TouchAction = a;
        p.SkillAction = a;
        TouchPlanner.UpdatePhases(p, tick, t);
        return a;
    }

    /// <summary>
    /// What the legs do this tick while a move lasts: they hold the move's running direction at the move's own running speed,
    /// plus its sideways or backward step.
    /// </summary>
    internal static MoveAssist BeginTick(PlayerState p, Ball ball, int tick, Tuning t, out TickIntent legs)
    {
        var a = p.SkillAction!;
        var k = t.Skill;
        Vector2 fwd = a.HoldDirection;
        legs = new TickIntent { MoveX = fwd.X, MoveZ = fwd.Y, Sprint = 1f };
        float cap = Possession.SpeedCap(p, true, false, t);
        float entry = a.SkillEntry;
        if (IsSole(a.Kind) && tick < a.ContactTick) return ReachTick(p, a, ball, tick, cap, t);
        int from = MoveFrom(a);
        int dur = a.EndTick - from;
        float durSec = dur * t.Dt;
        float u1 = Math.Clamp((tick + 1 - from) / (float)dur, 0f, 1f); // progress at the end of this tick
        float run;
        Vector2 steer = Vector2.Zero;
        switch (a.Kind)
        {
            case ActionKind.StepOver:
            case ActionKind.BodyFeint:
            {
                // Pace the ball: its own speed along the run plus the gap error spread over the time left (never less than
                // PaceHorizon, so the last ticks do not jerk), changing no faster than PaceAccel. The move is a dummy, not a
                // sprint: the player never runs faster than he came in (or PaceApproach from a walk), so a ball pushed far
                // ahead is left for the next touch to catch.
                var rel = new Vector2(ball.Position.X - p.Position.X, ball.Position.Z - p.Position.Z);
                float along = Vector2.Dot(rel, fwd);
                float vb = Vector2.Dot(new Vector2(ball.Velocity.X, ball.Velocity.Z), fwd);
                // The foot reaches the contact zone's lead ahead of the player, so that is where "ahead of the foot" starts.
                float top = Movement.TopSpeed(p.Sheet, t);
                float foot = t.Touch.ContactZoneAhead.Lerp(top > 0f ? p.RunSpeed / top : 0f);
                float ahead = foot + k.PaceMatchAhead, by = a.EndTick, limit = MathF.Max(entry, PaceApproach);
                // Before the middle (the foot passing over the ball) the player closes in so the ball is within reach of the
                // leg there, a little faster than he came in if he must.
                if (tick < a.ContactTick && along > k.StepOverMaxAhead - CloseInMargin - 0.0001f)
                {
                    ahead = MathF.Min(ahead, k.StepOverMaxAhead - CloseInMargin);
                    by = a.ContactTick;
                    limit += CloseInExtra;
                }
                float left = MathF.Max(PaceHorizon, (by - tick) * t.Dt);
                float want = Math.Clamp(vb + (along - ahead) / left, 0f, MathF.Min(cap, limit));
                float now = p.RunSpeed, step = PaceAccel * t.Dt;
                run = Math.Clamp(want, now - step, now + step);
                break;
            }
            case ActionKind.BallRoll:
            {
                run = MathF.Min(entry, cap);
                var d = a.SkillBallTo - a.SkillBallFrom;
                Vector2 right = GestureReader.RightOf(fwd);
                float lateral = d.X * right.X + d.Z * right.Y;
                steer = right * (lateral * k.BallRollFollow / durSec);
                break;
            }
            case ActionKind.DragBack:
            {
                // The run stops over the first part of the move while the player steps back; together he ends
                // drag_back_step_back behind where he started.
                run = entry * MathF.Max(0f, 1f - u1 / DragBackStopShare);
                float drift = entry * DragBackStopShare * 0.5f * durSec;
                // A turn at the feet steps toward the stick's line, where the sole pulls the ball.
                steer = (a.FeetTurn ? a.SkillCut : -fwd) * ((k.DragBackStepBack + drift) / durSec);
                break;
            }
            case ActionKind.FakeStop:
                run = entry * (1f - u1);
                break;
            default: // fake cut
                run = entry * (1f - (1f - FakeCutSpeedKept) * u1);
                break;
        }
        return new MoveAssist(cap, 1f, steer, run);
    }

    /// <summary>The tick the move's own motion starts: the sole's landing for a sole move, the start for the others.</summary>
    static int MoveFrom(BallAction a) => a.ExecuteTick;

    /// <summary>
    /// How long a step-over or feint closes in before its own motion: with the ball further ahead than step_over_max_ahead
    /// (less a margin), the player is run forward (PaceAccel, up to CloseInExtra above his entry pace) against the rolling
    /// ball until it is within it, at most CloseInMax.
    /// </summary>
    static int CloseInTicks(PlayerState p, Ball ball, Vector2 fwd, float entry, Tuning t)
    {
        var k = t.Skill;
        float along = Vector2.Dot(new Vector2(ball.Position.X - p.Position.X, ball.Position.Z - p.Position.Z), fwd);
        float target = k.StepOverMaxAhead - CloseInMargin;
        if (along <= target) return 0;
        float vb = Vector2.Dot(new Vector2(ball.Velocity.X, ball.Velocity.Z), fwd), vp = entry;
        float limit = MathF.Max(entry, PaceApproach) + CloseInExtra;
        int max = Ticks(CloseInMax, t);
        for (int n = 1; n <= max; n++)
        {
            vp = MathF.Min(limit, vp + PaceAccel * t.Dt);
            if (vb > 0f) vb = MathF.Max(0f, vb - t.Ball.RollDeceleration * t.Dt);
            along += (vb - vp) * t.Dt;
            if (along <= target) return n;
        }
        return max;
    }

    /// <summary>
    /// A tick of a sole move's reach: the player runs on at his entry speed and is steered (at most sole_reach_steer) so the
    /// foot's contact point meets the free ball at the contact tick, predicted as the touch planner's pre-windup does. The
    /// action's contact point is the predicted ball centre there.
    /// </summary>
    static MoveAssist ReachTick(PlayerState p, BallAction a, Ball ball, int tick, float cap, Tuning t)
    {
        float run = MathF.Min(a.SkillEntry, cap);
        Vector2 fwd = a.HoldDirection;
        var legs = new TickIntent { MoveX = fwd.X, MoveZ = fwd.Y, Sprint = 1f };
        int moves = a.ContactTick - tick + 1;
        var c = p.Scratch();
        var b = ball.ScratchA();
        for (int i = 0; i < moves; i++)
        {
            Movement.Step(c, legs, t.Dt, p.MoveTime + (i + 1) * t.Dt, new MoveAssist(cap, 1f, Vector2.Zero, run), t, null);
            if (i < moves - 1) b.Step(t.Dt, null, null);
        }
        var cp = TouchPlanner.ContactPointFor(c, a.Foot, t);
        var e = new Vector2(b.Position.X - cp.X, b.Position.Z - cp.Z);
        Vector2 steer = e / (moves * t.Dt);
        float max = t.Skill.SoleReachSteer, len = steer.Length();
        if (len > max) steer *= max / len;
        a.ContactPoint = new Vector3(b.Position.X, t.Ball.Radius, b.Position.Z);
        return new MoveAssist(cap, 1f, steer, run);
    }

    /// <summary>
    /// A sole move's sole lands: the ball's path is laid from where the ball is now (ball roll sideways toward the side, the
    /// drag-back's pull back, the fake stop's stop ahead of where the player will stop).
    /// </summary>
    static void LaySolePath(PlayerState p, BallAction a, Ball ball, Tuning t)
    {
        var k = t.Skill;
        Vector2 fwd = a.HoldDirection, right = GestureReader.RightOf(fwd);
        var f3 = new Vector3(fwd.X, 0f, fwd.Y);
        var r3 = new Vector3(right.X, 0f, right.Y);
        var b0 = ball.Position;
        float durSec = (a.EndTick - a.ContactTick) * t.Dt;
        float entry = a.SkillEntry;
        Vector3 to = b0;
        switch (a.Kind)
        {
            case ActionKind.BallRoll:
                to = b0 + r3 * ((a.Side == SkillSide.Left ? -1f : 1f) * k.BallRollDistance.Evaluate(entry)) + f3 * (entry * durSec);
                break;
            case ActionKind.DragBack:
                // A turn at the feet pulls the ball onto the stick's line rather than straight back.
                to = a.FeetTurn ? b0 + new Vector3(a.SkillCut.X, 0f, a.SkillCut.Y) * k.DragBackDistance : b0 - f3 * k.DragBackDistance;
                break;
            case ActionKind.FakeStop:
            {
                // The player's speed falls linearly to zero, so he covers half the entry pace over the move.
                var stopAt = p.Position + f3 * (entry * durSec * 0.5f);
                to = new Vector3(stopAt.X, b0.Y, stopAt.Z) + f3 * k.FakeStopAhead;
                break;
            }
        }
        a.SkillBallFrom = b0;
        a.SkillBallTo = to;
    }

    /// <summary>
    /// After the player has moved this tick: the contact on the contact tick (a sole landing on the ball, or the fake cut's
    /// hook), and the ball's path while the sole controls it. Returns the touch made, if any.
    /// </summary>
    internal static TouchResult? Step(PlayerState p, Ball ball, int tick, float now, Tuning t)
    {
        var a = p.SkillAction!;
        var k = t.Skill;
        TouchResult? made = null;
        bool sole = IsSole(a.Kind);
        float miss = 0f;
        if (sole && tick == a.ContactTick)
        {
            var cp = TouchPlanner.ContactPointFor(p, a.Foot, t);
            miss = new Vector2(ball.Position.X - cp.X, ball.Position.Z - cp.Z).Length();
            LaySolePath(p, a, ball, t);
        }
        Vector3 contactBall = ball.Position;
        if (sole && tick >= a.ContactTick)
        {
            int dur = a.EndTick - a.ContactTick;
            float u1 = Math.Clamp((tick + 1 - a.ContactTick) / (float)dur, 0f, 1f);
            Vector3 target = Path(a, u1);
            var v = new Vector3((target.X - ball.Position.X) / t.Dt, ball.Velocity.Y, (target.Z - ball.Position.Z) / t.Dt);
            ball.Velocity = v;
            ball.Spin = ball.RollingSpin(v);
        }
        if (tick == a.ContactTick && a.Kind is ActionKind.BallRoll or ActionKind.DragBack or ActionKind.FakeStop or ActionKind.FakeCut)
        {
            if (a.Kind == ActionKind.FakeCut)
            {
                // The hook: a push toward the left stick that reaches fake_cut_distance from the player in fake_cut_time.
                var target = p.Position + new Vector3(a.SkillCut.X, 0f, a.SkillCut.Y) * k.FakeCutDistance;
                target.Y = t.Ball.Radius;
                float speed = TouchPlanner.SolvePush(ball, target, Ticks(k.FakeCutTime, t), 0f, 0.3f, MathF.Max(t.Touch.MaxPush, 0.3f), t,
                    out var dir, out _, out _);
                var v = dir * speed;
                ball.Velocity = v;
                ball.Spin = ball.RollingSpin(v);
            }
            p.LastTouchTime = now;
            p.LastTouchFoot = a.Foot;
            p.LastTouchKind = a.Kind;
            a.Made = true;
            a.ContactMiss = miss;
            a.ContactPoint = new Vector3(contactBall.X, t.Ball.Radius, contactBall.Z);
            a.BallOutVelocity = ball.Velocity;
            a.BallOutSpin = ball.Spin;
            made = new TouchResult(a.Kind, a.Foot, ball.Position, ball.Velocity, 0f);
        }
        TouchPlanner.UpdatePhases(p, tick, t);
        return made;
    }

    /// <summary>Where the sole has the ball at progress <paramref name="u"/> (0..1) of the move.</summary>
    static Vector3 Path(BallAction a, float u)
    {
        var from = a.SkillBallFrom;
        var d = a.SkillBallTo - from;
        if (a.Kind == ActionKind.BallRoll)
        {
            // Along the run at the player's pace, across the body eased in and out.
            var f = new Vector3(a.HoldDirection.X, 0f, a.HoldDirection.Y);
            var along = f * Vector3.Dot(d, f);
            float s = u * u * (3f - 2f * u);
            return from + along * u + (d - along) * s;
        }
        // Pulled back: the sole draws it smoothly from rest to rest. Stopped: it slows from its own pace to rest.
        float e = a.Kind == ActionKind.DragBack ? u * u * (3f - 2f * u) : 1f - (1f - u) * (1f - u);
        return from + d * e;
    }

    /// <summary>
    /// The move has run its time (called at the start of the tick it ends on): leaves the ball rolling with the player (or at
    /// rest after a drag-back or fake stop), opens the chain window, starts a drag-back's cooldown, readies the exit burst, and
    /// hands the ball back to the touch planner: on this tick it makes the exit touch at once if it can (see
    /// TouchPlanner.Step), else it plans one as close as the windup.
    /// </summary>
    internal static void Finish(PlayerState p, in TickIntent intent, Ball ball, int tick, float now, Tuning t)
    {
        var a = p.SkillAction!;
        var k = t.Skill;
        p.SkillAction = null;
        a.Phase = ActionPhase.Done;
        Vector2 fwd = a.HoldDirection;
        switch (a.Kind)
        {
            case ActionKind.BallRoll:
            {
                float pace = MathF.Max(0f, Vector2.Dot(new Vector2(p.Velocity.X, p.Velocity.Z), fwd));
                var v = new Vector3(fwd.X * pace, ball.Velocity.Y, fwd.Y * pace);
                ball.Velocity = v;
                ball.Spin = ball.RollingSpin(v);
                break;
            }
            case ActionKind.DragBack:
            case ActionKind.FakeStop:
                ball.Velocity = new Vector3(0f, ball.Velocity.Y, 0f);
                ball.Spin = Vector3.Zero;
                break;
        }
        if (a.Kind == ActionKind.DragBack) p.SkillCooldownUntil = now + k.DragBackCooldown;
        if (a.FeetTurn)
        {
            // The planner's own turn: straight into the exit touch, with no chain window or exit burst.
            p.Replanning = true;
            p.Recovering = false;
            p.SkillExitTick = tick;
            return;
        }
        p.SkillChainUntil = now + k.ChainWindow;

        // The exit burst rides on the exit touch (the planner's next touch), like a touch boost: started here it would make
        // the legs outrun a ball at the feet before that touch could be planned.
        float scale = ExitScale(a, intent.Move, t);
        if (scale > 0f)
        {
            p.SkillBoostFactor = 1f + k.Burst.ForRating(a.SkillEffective) * scale;
            p.SkillBoostDuration = k.BurstDuration.ForRating(a.SkillEffective);
            p.SkillBoostCap = k.BurstSpeedCap.ForRating(a.SkillEffective) * scale;
            p.SkillBoostUntil = now + ExitTouchWithin;
        }
        p.Replanning = true;
        p.Recovering = false;
        p.SkillExitTick = tick;
    }

    /// <summary>
    /// The exit burst's scale, 0..1, from the exit angle: the left stick at the move's end against the running direction the
    /// move started on (straight on with the stick released). A step-over or body feint sold its side, so going away from that
    /// side gets the most (sided_exit_burst), and a step-over gets none for an exit more than step_over_no_burst_toward_deg
    /// toward it; the other moves sold straight on and scale by the size of the exit angle (straight_exit_burst).
    /// </summary>
    public static float ExitScale(BallAction a, Vector2 stick, Tuning t)
    {
        var k = t.Skill;
        float exit = 0f; // degrees, positive to the right of the run
        if (stick.Length() >= t.Touch.StickThreshold)
        {
            Vector2 fwd = a.HoldDirection;
            exit = MathF.Atan2(Vector2.Dot(stick, GestureReader.RightOf(fwd)), Vector2.Dot(stick, fwd)) * 180f / MathF.PI;
        }
        if (a.Kind is ActionKind.StepOver or ActionKind.BodyFeint)
        {
            float toward = a.Side == SkillSide.Left ? -exit : exit;
            if (a.Kind == ActionKind.StepOver && toward > k.StepOverNoBurstTowardDeg) return 0f;
            return Math.Clamp(k.SidedExitBurst.Evaluate(-toward), 0f, 1f);
        }
        return Math.Clamp(k.StraightExitBurst.Evaluate(MathF.Abs(exit)), 0f, 1f);
    }

    /// <summary>
    /// A touch is being made: a skill move's exit burst waiting for its exit touch starts on it and lasts its duration through
    /// the touches that follow. Called where the touch boost is set (so the next meet is predicted with the burst) and again
    /// for every touch (an exit touch without the touch boost, such as a slowdown, still starts it).
    /// </summary>
    internal static void ApplyExitBurst(PlayerState p, float now)
    {
        if (now >= p.SkillBoostUntil) return;
        p.SkillBurstUntil = now + p.SkillBoostDuration;
        p.SkillBurstFactor = p.SkillBoostFactor;
        p.SkillBurstCap = p.SkillBoostCap;
        p.SkillBoostUntil = -100f;
    }
}
