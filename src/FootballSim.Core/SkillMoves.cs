using System.Numerics;

namespace FootballSim.Core;

/// <summary>
/// Skill moves as committed actions. A recognised gesture (see <see cref="GestureReader"/>) is buffered; the move starts when
/// the player has the ball within the execution radius (step_over_radius for a step-over) and below max_ball_height, or is
/// dropped after buffer_seconds. Starting drops the waiting touch. While the move lasts it owns the legs and the ball: each
/// move sets the running speed along the running direction it started on, a steering velocity, and for the sole moves the
/// ball's path. When it ends the touch planner plans at once toward the left stick, so a move never loses the ball.
/// <list type="bullet">
/// <item>Step-over and body feint: no contact; from standing the player eases to the ball's pace so it ends pace_match_ahead in
/// front, at a run he runs on and the ball rolls on with him (see CarryDummyBall).</item>
/// <item>Ball roll: the sole rolls the ball sideways toward the side (ball_roll_distance by speed) and the player steps with it.</item>
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
        p.SkillBufFakeFrom = -1;
        p.SkillExitTick = -1;
        p.SkillBurstAtTick = -1;
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
        Gesture.Elastico => ActionKind.Elastico,
        Gesture.SpinAndGo => ActionKind.Spin,
        Gesture.HeelToHeel => ActionKind.HeelToHeel,
        _ => throw new ArgumentException($"{g} is not a skill move", nameof(g)),
    };

    /// <summary>How close the ball must be for a move to start, metres.</summary>
    public static float Radius(ActionKind kind, Tuning t) => kind == ActionKind.StepOver ? t.Skill.StepOverRadius : t.Skill.ExecutionRadius;

    /// <summary>
    /// A recognised skill gesture: it waits (at most buffer_seconds) for the move to be able to start. A fake shot comes here on
    /// its contact tick with <paramref name="fakeFrom"/> the shoot press's tick.
    /// </summary>
    internal static void Request(PlayerState p, in GestureRead g, int tick, int fakeFrom = -1)
    {
        p.SkillBufKind = g.Kind;
        p.SkillBufSide = g.Side;
        p.SkillBufCut = g.CutDirection;
        p.SkillBufTick = tick;
        p.SkillBufFakeFrom = fakeFrom;
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
    /// In possession the gesture waits (at most meet_wait_seconds) on the touch controller's forecast: while it waits the
    /// controller treats it as a demand and goes to the ball without committing a touch, and the move starts once the ball is
    /// in reach, at the forecast contact at the latest. A touch already committed when the gesture comes is played first (on a
    /// one-step push), then the move. Without possession it waits buffer_seconds for the ball to come into reach.
    /// </para>
    /// </summary>
    internal static SimEvent? TryStart(PlayerState p, Ball ball, bool inPossession, int tick, float now, Tuning t)
    {
        var k = t.Skill;
        if (p.SkillBufTick < 0) return null;
        if (tick - p.SkillBufTick > Ticks(inPossession ? k.MeetWaitSeconds : k.BufferSeconds, t)) { p.SkillBufTick = -1; return null; }
        if (!inPossession || p.SkillAction != null || now < p.SkillCooldownUntil) return null;
        var kind = KindOf(p.SkillBufKind);
        float dist = TouchModel.HorizontalDistance(p.Position, ball.Position);
        // A fake shot on its contact tick (the shot's wind-up has run onto the ball): its contact is made now, with no reach.
        if (p.SkillBufFakeFrom >= 0 && tick == p.SkillBufTick && ball.Position.Y < k.MaxBallHeight && dist <= k.ExecutionRadius + t.Touch.ContactZoneRadius)
        {
            int from = p.SkillBufFakeFrom;
            p.SkillBufTick = -1;
            p.SkillBufFakeFrom = -1;
            var f = Start(p, kind, p.SkillBufSide, p.SkillBufCut, ball, tick, now, t, fakeFrom: from);
            return new SimEvent(EventKind.SkillMove, ball.Position, p.Speed, Foot: f.Foot, Team: p.Team, PlayerIndex: p.Index, Skill: kind);
        }
        if (p.TouchAction is { Pending: true }) return null;
        if (ball.Position.Y >= k.MaxBallHeight) return null;
        // At the forecast contact the ball is being steered into the contact zone ahead of the foot: within the zone's slack of
        // the radius.
        bool meet = p.ForecastTick >= 0;
        bool atMeet = meet && tick >= p.ForecastTick && dist <= Radius(kind, t) + t.Touch.ContactZoneRadius;
        if (dist > Radius(kind, t) && !atMeet) return null;
        // With a contact still to come, a move that would start awkwardly waits for it: a step-over or feint with the ball too
        // far ahead for the leg to reach over it, a sole move whose foot cannot land on the ball within the reach.
        if (meet && !atMeet)
        {
            Vector2 fwd = GestureReader.FrameForward(p, t);
            if (kind is ActionKind.StepOver or ActionKind.BodyFeint
                && Vector2.Dot(new Vector2(ball.Position.X - p.Position.X, ball.Position.Z - p.Position.Z), fwd) > k.StepOverMaxAhead)
                return null;
            if (Reaches(kind) && PlanReach(p, FootFor(p, kind, p.SkillBufSide, ball, t), fwd, ball, now, t, ReachMax(kind, t)).Miss > k.SoleReachTolerance - ReachMargin)
                return null;
        }
        p.SkillBufTick = -1;
        var a = Start(p, kind, p.SkillBufSide, p.SkillBufCut, ball, tick, now, t);
        return new SimEvent(EventKind.SkillMove, ball.Position, p.Speed, Foot: a.Foot, Team: p.Team, PlayerIndex: p.Index, Skill: kind);
    }

    /// <summary>True for the moves whose sole lands on the ball after a reach: ball roll, drag-back, fake stop.</summary>
    static bool IsSole(ActionKind kind) => kind is ActionKind.BallRoll or ActionKind.DragBack or ActionKind.FakeStop;

    /// <summary>True for the moves with two contacts: elastico, spin, heel-to-heel.</summary>
    static bool TwoContacts(ActionKind kind) => kind is ActionKind.Elastico or ActionKind.Spin or ActionKind.HeelToHeel;

    /// <summary>
    /// True for the moves that reach for the ball before their first contact and then carry it along their path: the sole moves
    /// and the two-contact moves.
    /// </summary>
    static bool Reaches(ActionKind kind) => IsSole(kind) || TwoContacts(kind);

    /// <summary>The longest reach before the first contact (the heel-to-heel's is its fixed time to the heel's contact), seconds.</summary>
    static float ReachMax(ActionKind kind, Tuning t) => kind switch
    {
        ActionKind.Elastico => t.Skill.ElasticoReachMax,
        ActionKind.Spin => t.Skill.SpinReachMax,
        ActionKind.HeelToHeel => t.Skill.HeelToHeelContactSeconds,
        _ => t.Skill.SoleReachMax,
    };

    /// <summary>
    /// The running speed the move eases down to from its start (the reach included), no faster than balance_deceleration, m/s;
    /// +infinity for the moves that keep the entry speed through their reach (the ball roll has its own, <see cref="RollRun"/>).
    /// </summary>
    static float EasedForwardSpeed(ActionKind kind, Tuning t) => kind switch
    {
        ActionKind.Elastico => t.Skill.ElasticoForwardSpeed,
        ActionKind.Spin => t.Skill.SpinForwardSpeed,
        _ => float.PositiveInfinity,
    };

    static Foot Other(Foot f) => f == Foot.Left ? Foot.Right : Foot.Left;

    /// <summary>The foot that plays a move.</summary>
    static Foot FootFor(PlayerState p, ActionKind kind, SkillSide side, Ball ball, Tuning t) => kind switch
    {
        ActionKind.StepOver or ActionKind.BodyFeint => side == SkillSide.Left ? Foot.Left : Foot.Right,
        // The sole of the far foot rolls the ball across the body toward the side; the elastico is played by the foot away from
        // its exit side (it pushes the ball out with the outside and snaps it back across with the inside); the spin's first sole
        // is the foot away from the side it turns to.
        ActionKind.BallRoll or ActionKind.Elastico or ActionKind.Spin => side == SkillSide.Left ? Foot.Right : Foot.Left,
        // The heel-to-heel leads with the foot nearer the ball.
        ActionKind.HeelToHeel => Vector2.Dot(new Vector2(ball.Position.X - p.Position.X, ball.Position.Z - p.Position.Z),
            GestureReader.RightOf(GestureReader.FrameForward(p, t))) >= 0f ? Foot.Right : Foot.Left,
        ActionKind.DragBack => TouchModel.ChooseFoot(p.Sheet, p.Position, p.Facing, ball.Position, t),
        _ => p.StrikeFoot,
    };

    /// <summary>
    /// The reach of a sole move: predicts the player running on at his speed along <paramref name="fwd"/> and the free ball,
    /// and returns the first tick offset (0 to sole_reach_max) at which the foot's contact point can be brought within
    /// sole_reach_tolerance of the ball by steering at most sole_reach_steer and the foot can have got there at
    /// sole_reach_foot_speed, with the miss left there; if none can, the offset with the smallest miss.
    /// </summary>
    static (int Ticks, float Miss) PlanReach(PlayerState p, Foot foot, Vector2 fwd, Ball ball, float now, Tuning t, float maxSeconds,
        bool eased = true)
    {
        var k = t.Skill;
        var c = p.Scratch();
        var b = ball.ScratchA();
        float entry = MathF.Max(0f, Vector2.Dot(new Vector2(p.Velocity.X, p.Velocity.Z), fwd));
        float cap = Possession.SpeedCap(p, true, false, t);
        var legs = new TickIntent { MoveX = fwd.X, MoveZ = fwd.Y, Sprint = 1f };
        int max = Math.Max(0, (int)MathF.Round(maxSeconds * t.TickRate));
        int best = 0;
        float bestMiss = float.MaxValue, reached = 0f;
        for (int j = 0; j <= max; j++)
        {
            // Contact on tick +j: the player has moved j + 1 times, the ball j times (it moves after the player each tick).
            Movement.Step(c, legs, t.Dt, now + (j + 1) * t.Dt, new MoveAssist(cap, 1f, Vector2.Zero, MathF.Min(entry, cap)), t, null);
            if (j > 0) b.Step(t.Dt, null, null);
            var cp = TouchPlanner.ContactPointFor(c, foot, t);
            float d = new Vector2(b.Position.X - cp.X, b.Position.Z - cp.Z).Length();
            // The reach's step is eased in no faster than balance_deceleration (the move is kept to a smooth speed), so it covers
            // less than sole_reach_steer held from the start.
            float miss = MathF.Max(0f, d - reached);
            reached += (eased ? MathF.Min(k.SoleReachSteer, t.Movement.BalanceDeceleration * (j + 1) * t.Dt) : k.SoleReachSteer) * t.Dt;
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
    internal static BallAction StartFeetTurn(PlayerState p, Vector2 dir, Ball ball, int tick, float now, Tuning t)
    {
        // The body comes round at the post-turn rotation rate, as after a turn touch.
        p.PostTurnUntil = now + t.Movement.PostTurnSeconds;
        return Start(p, ActionKind.DragBack, SkillSide.None, dir, ball, tick, now, t, feetTurn: true);
    }

    /// <summary>The part of the foot a move plays the ball with.</summary>
    static FootSurface SurfaceFor(ActionKind kind, SkillSide side, Foot foot) => kind switch
    {
        ActionKind.BallRoll or ActionKind.DragBack or ActionKind.FakeStop or ActionKind.Spin => FootSurface.Sole,
        ActionKind.Elastico => FootSurface.Outside,
        ActionKind.HeelToHeel => FootSurface.Heel,
        // A hook toward the foot's own side is played with the outside of the foot.
        ActionKind.FakeCut => (side == SkillSide.Right) == (foot == Foot.Right) ? FootSurface.Outside : FootSurface.Inside,
        ActionKind.StepOver => FootSurface.Outside,
        _ => FootSurface.Inside,
    };

    /// <summary>A fake stop's or fake cut's length from its contact, seconds.</summary>
    static float FakeSeconds(ActionKind kind, Tuning t) => kind == ActionKind.FakeStop ? t.Skill.FakeStopSeconds : t.Skill.FakeSeconds;

    /// <summary>
    /// The move a fake shot read on the cancel will be when it is made on <paramref name="contactTick"/> (see
    /// <see cref="PlayerState.PendingSkill"/>), as <see cref="Start"/> would make it if it started now: the shot's strike foot, its
    /// surface, the shoot press as its start, its contact and end ticks, the ball rolled on to the contact as its contact point.
    /// Changes no state.
    /// </summary>
    internal static BallAction PendingFake(PlayerState p, in GestureRead g, int pressTick, int contactTick, int tick, Ball ball, Tuning t)
    {
        var kind = KindOf(g.Kind);
        Foot foot = FootFor(p, kind, g.Side, ball, t);
        float eta = Math.Max(0, contactTick - tick) * t.Dt;
        return new BallAction
        {
            Kind = kind,
            Phase = ActionPhase.Windup,
            Side = kind == ActionKind.FakeStop ? SkillSide.None : g.Side,
            Foot = foot,
            Surface = SurfaceFor(kind, g.Side, foot),
            StartTick = pressTick,
            PlanTick = tick,
            ExecuteTick = contactTick,
            ContactTick = contactTick,
            EndTick = contactTick + Ticks(FakeSeconds(kind, t), t),
            ContactPoint = new Vector3(ball.Position.X + ball.Velocity.X * eta, t.Ball.Radius, ball.Position.Z + ball.Velocity.Z * eta),
            HoldDirection = GestureReader.FrameForward(p, t),
            SkillCut = g.CutDirection,
        };
    }

    static BallAction Start(PlayerState p, ActionKind kind, SkillSide side, Vector2 cut, Ball ball, int tick, float now, Tuning t,
        bool feetTurn = false, int fakeFrom = -1)
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
        p.Chasing = false;
        p.KnockOnRequested = false;
        // After the move the next touch may be played on the first swing that reaches the ball.
        p.PushSteps = 1;

        bool contactless = kind is ActionKind.StepOver or ActionKind.BodyFeint;
        Vector2 fwd = GestureReader.FrameForward(p, t);
        float entry = MathF.Max(0f, Vector2.Dot(new Vector2(p.Velocity.X, p.Velocity.Z), fwd));
        float ballSpeed = new Vector2(ball.Velocity.X, ball.Velocity.Z).Length();
        float seconds = kind switch
        {
            ActionKind.StepOver => ballSpeed < k.StillBallSpeed ? k.StepOverStillSeconds : k.StepOverSeconds,
            ActionKind.BodyFeint => k.BodyFeintSeconds,
            ActionKind.BallRoll => p.SkillChainCount > 0 ? k.BallRollChainSeconds
                : k.BallRollFirstSeconds + (k.BallRollRunSeconds - k.BallRollFirstSeconds) * RollRunShare(entry, t),
            ActionKind.DragBack => feetTurn ? k.FeetTurnSeconds : k.DragBackSeconds,
            ActionKind.FakeStop or ActionKind.FakeCut => FakeSeconds(kind, t),
            // The two-contact moves from their first contact.
            ActionKind.Elastico => k.ElasticoSeconds,
            ActionKind.Spin => k.SpinSeconds,
            ActionKind.HeelToHeel => k.HeelToHeelSeconds - k.HeelToHeelContactSeconds,
            _ => k.FakeSeconds,
        };
        int dur = Ticks(seconds, t);
        Foot foot = FootFor(p, kind, side, ball, t);
        int reach = fakeFrom >= 0 ? 0
            : kind == ActionKind.HeelToHeel ? Ticks(k.HeelToHeelContactSeconds, t)
            : Reaches(kind) ? PlanReach(p, foot, fwd, ball, now, t, feetTurn ? k.FeetTurnReachMax : ReachMax(kind, t), eased: !feetTurn).Ticks
            // (At a run the ball is brought to him as he runs on instead, see CarryDummyBall.)
            : contactless ? (int)MathF.Round(CloseInTicks(p, ball, fwd, entry, t) * (1f - DummyRunShare(entry, t))) : 0;
        // A two-contact move's contacts are fixed from its start (the heel-to-heel's from the start, the others' from the first).
        if (kind == ActionKind.HeelToHeel) dur = Ticks(k.HeelToHeelSeconds, t) - reach;
        FootSurface surface = SurfaceFor(kind, side, foot);
        var a = new BallAction
        {
            Kind = kind,
            Side = kind is ActionKind.DragBack or ActionKind.FakeStop ? SkillSide.None : side,
            Foot = foot,
            Surface = surface,
            // A fake shot started at the shoot press: its wind-up was the shot's (shown as the shot action until now).
            StartTick = fakeFrom >= 0 ? fakeFrom : tick,
            PlanTick = tick,
            // After the reach (a sole move's foot reaching for the ball, a step-over closing in) the move runs its full time:
            // a sole lands at once, the step-over's foot passes the ball half way.
            ExecuteTick = tick + reach,
            ContactTick = contactless ? tick + reach + dur / 2 : tick + reach,
            EndTick = tick + reach + dur,
            ContactPoint = new Vector3(ball.Position.X, t.Ball.Radius, ball.Position.Z),
            HoldDirection = fwd,
            SkillEntry = entry,
            SkillCut = cut,
            SkillEffective = p.SkillEffective,
            FeetTurn = feetTurn,
        };
        if (TwoContacts(kind))
        {
            int c1 = tick + reach;
            a.Contact2Tick = kind switch
            {
                ActionKind.Elastico => c1 + Ticks(k.ElasticoContact2Seconds, t),
                ActionKind.Spin => c1 + Ticks(k.SpinContact2Seconds, t),
                _ => tick + Ticks(k.HeelToHeelContact2Seconds, t),
            };
            // The elastico's inside snaps the ball back with the same foot; the spin's other sole and the heel-to-heel's trailing
            // foot take the second contact.
            a.Foot2 = kind == ActionKind.Elastico ? foot : Other(foot);
            a.Surface2 = kind == ActionKind.Spin ? FootSurface.Sole : FootSurface.Inside;
            if (kind == ActionKind.Spin) a.LiftTick = c1 + Ticks(k.SpinLiftSeconds, t);
        }
        if (p.TouchAction is { Made: true } prev && prev.PhaseAt(tick, TouchPlanner.WindupTicks(t), TouchPlanner.WindupTicks(t)) != ActionPhase.Done)
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
        // A turn at the feet turns the body onto the stick's line while the sole pulls the ball there, so the player comes out
        // of it facing his new way instead of running on and turning about afterwards.
        Vector2 face = a.FeetTurn ? a.SkillCut : fwd;
        legs = new TickIntent { MoveX = face.X, MoveZ = face.Y, Sprint = 1f };
        float cap = Possession.SpeedCap(p, true, false, t);
        float entry = a.SkillEntry;
        if (Reaches(a.Kind) && tick < a.ContactTick) return ReachTick(p, a, ball, tick, cap, t);
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
                // At a run he runs on instead (the ball is kept rolling with him, see CarryDummyBall), his run turning toward the
                // left stick (at most step_over_run_turn_deg off the move's line); a move entered in between blends the two.
                float share = DummyRunShare(entry, t);
                if (share > 0f)
                {
                    // (From the running speed he has: the entry pace may include the controller's steering, which eases out.)
                    float dummy = Math.Clamp(DummyRun(a, tick, t), now - k.StepOverRunBrake * t.Dt, now + step);
                    // A step under way (the steering before the move, a ball roll's step) eases out as the skill tick eases it
                    // (SkillSteerAccel); the run takes up what it would cost his ground speed beyond step_over_run_brake.
                    Vector2 s0 = p.PlannerSteer + p.SteerCarry;
                    float fl = s0.Length(), fade = DummySteerFade * t.Dt;
                    Vector2 s1 = fl > fade ? s0 * (1f - fade / fl) : Vector2.Zero;
                    var fc = new Vector2(MathF.Cos(p.Facing), MathF.Sin(p.Facing));
                    float floorV = new Vector2(p.Velocity.X, p.Velocity.Z).Length() - k.StepOverRunBrake * t.Dt;
                    float fs = Vector2.Dot(fc, s1), disc = floorV * floorV - s1.LengthSquared() + fs * fs;
                    if (floorV > 0f && disc > 0f) dummy = MathF.Max(dummy, MathF.Min(MathF.Max(entry, now), -fs + MathF.Sqrt(disc)));
                    steer = s1;
                    run += (dummy - run) * share;
                    var dir = DummyRunDirection(p, a, t);
                    var lf = Vector2.Normalize(fwd * (1f - share) + dir * share);
                    legs = new TickIntent { MoveX = lf.X, MoveZ = lf.Y, Sprint = 1f };
                }
                break;
            }
            case ActionKind.BallRoll:
            {
                // The run eases down a little (see RollRun) while the player steps toward the side with the ball: a quick step
                // that rises over the first part of the roll and eases off toward the release (see PlanRoll).
                run = RollRun(a, tick, t);
                float lat0 = a.SideOffset0;
                // On the sole's landing tick the ball's place against him is not taken yet (the step is still nil then).
                if (tick == a.ContactTick) lat0 = Vector2.Dot(new Vector2(ball.Position.X - p.Position.X, ball.Position.Z - p.Position.Z), SideOf(a));
                var plan = PlanRoll(a, lat0, t);
                steer = SideOf(a) * (plan.Peak * RollStepShape((tick + 0.5f - from) / dur));
                break;
            }
            case ActionKind.DragBack:
            {
                // The run stops over the first part of the move while the player steps back; together he ends
                // drag_back_step_back behind where he started.
                run = entry * MathF.Max(0f, 1f - u1 / DragBackStopShare);
                float drift = entry * DragBackStopShare * 0.5f * durSec;
                // A turn at the feet steps toward the stick's line, where the sole pulls the ball. The step rises and falls
                // (a half sine over the move, the same distance), so the player is at rest when the move ends and nothing is left
                // to vanish then.
                float um = u1;
                steer = (a.FeetTurn ? a.SkillCut : -fwd) * (((a.FeetTurn ? k.FeetTurnStepBack : k.DragBackStepBack) + drift) / durSec * MathF.PI * 0.5f * MathF.Sin(MathF.PI * um));
                break;
            }
            case ActionKind.FakeStop:
                run = entry * (1f - u1);
                break;
            case ActionKind.Elastico:
            {
                // The run eases down to elastico_forward_speed as in the ball roll; over the snap's half of the move the player
                // starts driving toward the exit side, so he comes out of it going that way.
                float hold = MathF.Min(entry, cap);
                run = MathF.Min(hold, MathF.Max(MathF.Min(hold, k.ElasticoForwardSpeed), p.RunSpeed - t.Movement.BalanceDeceleration * t.Dt));
                float r = u1 <= 0.5f ? 0f : (u1 - 0.5f) / 0.5f;
                steer = SideOf(a) * (ElasticoExitSideSpeed * r * r * (3f - 2f * r));
                break;
            }
            case ActionKind.Spin:
                return SpinTick(p, a, ball, tick, cap, t, ref legs);
            case ActionKind.HeelToHeel:
                // He keeps his speed: the ball goes back between his feet and is flicked on ahead of him.
                run = MathF.Min(entry, cap);
                break;
            default: // fake cut
                run = entry * (1f - (1f - FakeCutSpeedKept) * u1);
                break;
        }
        return new MoveAssist(cap, 1f, steer, run);
    }

    /// <summary>
    /// A tick of the spin from its first sole's landing: the body turns 360 degrees toward the side (its facing set each tick,
    /// half way round at the second sole's contact) while the feet carry him on: his ground velocity (the move's step, which
    /// does not turn with the body; the running speed is held at zero while he turns) goes on from what it was at the landing,
    /// along the run easing to the forward speed that brings him spin_forward ahead of where the ball was first taken (at most
    /// spin_forward_speed, within balance_deceleration), and across it rising and falling to spin_side toward the side, so he
    /// comes out facing his original way and running along it.
    /// </summary>
    static MoveAssist SpinTick(PlayerState p, BallAction a, Ball ball, int tick, float cap, Tuning t, ref TickIntent legs)
    {
        int dur = a.EndTick - a.ContactTick, i = tick - a.ContactTick;
        float T = dur * t.Dt;
        if (i == 0) PlanSpin(p, a, ball, cap, t);
        Vector2 f = a.HoldDirection, s = SideOf(a);
        float tm = (i + 0.5f) / dur;
        float vl = a.SpinVel0.Y * (1f - 3f * tm * tm + 2f * tm * tm * tm) + a.SpinSideBump * 6f * tm * (1f - tm) / T;
        float vf = SpinForwardAt(a, i, t);
        float te = Math.Clamp((i + 1f) / dur, 0f, 1f);
        float turn = te * te * te * (10f - 15f * te + 6f * te * te);
        float facing = Movement.WrapAngle(a.SpinFacing0 + (a.Side == SkillSide.Left ? -1f : 1f) * 2f * MathF.PI * turn);
        legs = new TickIntent { MoveX = MathF.Cos(facing), MoveZ = MathF.Sin(facing), Sprint = 1f };
        return new MoveAssist(cap, 1f, Vector2.Zero, 0f, Carry: f * vf + s * vl, Facing: facing);
    }

    /// <summary>The spin's forward pace on its <paramref name="i"/>-th tick from the landing: from the landing's toward the planned one within the budget.</summary>
    static float SpinForwardAt(BallAction a, int i, Tuning t)
    {
        float b = t.Movement.BalanceDeceleration * t.Dt * (i + 1);
        return a.SpinVel0.X + Math.Clamp(a.SpinForward - a.SpinVel0.X, -b, b);
    }

    /// <summary>Plans the spin's travel on its first motion tick (see <see cref="SpinTick"/>).</summary>
    static void PlanSpin(PlayerState p, BallAction a, Ball ball, float cap, Tuning t)
    {
        var k = t.Skill;
        Vector2 f = a.HoldDirection, s = SideOf(a);
        var v0 = new Vector2(p.Velocity.X, p.Velocity.Z);
        a.SpinFacing0 = MathF.Atan2(f.Y, f.X);
        a.SpinVel0 = new Vector2(Vector2.Dot(v0, f), Vector2.Dot(v0, s));
        var toBall = new Vector2(ball.Position.X - p.Position.X, ball.Position.Z - p.Position.Z);
        int dur = a.EndTick - a.ContactTick;
        float T = dur * t.Dt;
        float wantF = Vector2.Dot(toBall, f) + k.SpinForward, wantS = Vector2.Dot(toBall, s) + k.SpinSide;
        a.SpinSideBump = wantS - a.SpinVel0.Y * T * 0.5f;
        float Travel(float v)
        {
            a.SpinForward = v;
            float d = 0f;
            for (int i = 0; i < dur; i++) d += SpinForwardAt(a, i, t) * t.Dt;
            return d;
        }
        float lo = 0f, hi = MathF.Max(0f, MathF.Min(cap, k.SpinForwardSpeed));
        if (Travel(hi) <= wantF) { a.SpinForward = hi; return; }
        if (Travel(lo) >= wantF) { a.SpinForward = lo; return; }
        for (int n = 0; n < 20; n++)
        {
            float mid = 0.5f * (lo + hi);
            if (Travel(mid) > wantF) hi = mid; else lo = mid;
        }
        a.SpinForward = 0.5f * (lo + hi);
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

    /// <summary>Step-over and body feint at a run: the ball ends this far ahead of the player along his travel at the release, metres ...</summary>
    const float DummyRunAhead = 0.45f;
    /// <summary>... rolling this much faster than he runs then, m/s (released at that pace for the ordinary dribble touch).</summary>
    const float DummyReleaseMargin = 0.15f;
    /// <summary>The steering under way as a run step-over or body feint starts eases out at this, m/s^2 (the skill tick's own ease, TouchPlanner.SkillSteerAccel).</summary>
    const float DummySteerFade = 30f;

    /// <summary>How much of the run version of the step-over and body feint a move entered at <paramref name="entry"/> m/s takes, 0..1.</summary>
    static float DummyRunShare(float entry, Tuning t) => Math.Clamp(t.Skill.StepOverRunBlend.Evaluate(entry), 0f, 1f);

    /// <summary>
    /// The run step-over's and body feint's running speed on <paramref name="tick"/>: from the entry pace (a move entered above
    /// the cap on a touch's boost does not drop to it in a tick) it eases down no faster than step_over_run_brake to
    /// step_over_run_keep of it, m/s.
    /// </summary>
    static float DummyRun(BallAction a, int tick, Tuning t)
    {
        var k = t.Skill;
        float hold = a.SkillEntry, floor = hold * k.StepOverRunKeep.Evaluate(hold);
        return MathF.Min(hold, MathF.Max(floor, hold - k.StepOverRunBrake * (tick + 1 - a.StartTick) * t.Dt));
    }

    /// <summary>
    /// The way the run step-over and body feint run: the left stick held (debounced as the controller reads it), turned at most
    /// step_over_run_turn_deg off the move's line; the move's line with the stick released.
    /// </summary>
    static Vector2 DummyRunDirection(PlayerState p, BallAction a, Tuning t)
    {
        Vector2 fwd = a.HoldDirection, stick = p.LastStickMove;
        if (p.StickReleasedTicks > Math.Max(0, (int)MathF.Round(t.Touch.ReleaseDebounce * t.TickRate)) || stick.Length() < t.Touch.StickThreshold)
            return fwd;
        Vector2 right = GestureReader.RightOf(fwd);
        float max = t.Skill.StepOverRunTurnDeg * MathF.PI / 180f;
        float ang = Math.Clamp(MathF.Atan2(Vector2.Dot(stick, right), Vector2.Dot(stick, fwd)), -max, max);
        return fwd * MathF.Cos(ang) + right * MathF.Sin(ang);
    }

    /// <summary>The player's direction of travel on the ground, the move's line when he is (nearly) still.</summary>
    static Vector2 TravelOf(PlayerState p, BallAction a)
    {
        var v = new Vector2(p.Velocity.X, p.Velocity.Z);
        return v.Length() > 0.3f ? Vector2.Normalize(v) : a.HoldDirection;
    }

    /// <summary>
    /// Step-over and body feint at a run, after the player has moved this tick: the ball (not touched: the dummy's leg goes
    /// round it) keeps rolling with him, brought smoothly from where it is against him (along his travel and across it, and
    /// its pace against his) to <see cref="DummyRunAhead"/> straight ahead of him at the release, rolling
    /// <see cref="DummyReleaseMargin"/> faster than he runs, so the next touch is an ordinary dribble touch. Planned afresh each
    /// tick from where it is (a cubic over the ticks left). A move entered between a walk and a run blends this with the free
    /// ball (step_over_run_blend).
    /// </summary>
    static void CarryDummyBall(PlayerState p, BallAction a, Ball ball, int tick, Tuning t)
    {
        float share = DummyRunShare(a.SkillEntry, t);
        if (share <= 0f) return;
        Vector2 d = TravelOf(p, a), s = GestureReader.RightOf(d);
        var pv = new Vector2(p.Velocity.X, p.Velocity.Z);
        // (Against him as the last tick ended: he has moved this tick, the ball moves after him.)
        var rel = new Vector2(ball.Position.X - p.Position.X, ball.Position.Z - p.Position.Z) + pv * t.Dt;
        var rv = new Vector2(ball.Velocity.X, ball.Velocity.Z) - pv;
        // Before the middle (the leg passing round it) it is brought within reach of the leg there, at his pace; then on to the
        // release.
        bool first = tick < a.ContactTick;
        float aheadTo = first ? MathF.Min(DummyRunAhead, t.Skill.StepOverMaxAhead - CloseInMargin) : DummyRunAhead;
        int n = Math.Max(1, (first ? a.ContactTick : a.EndTick) - tick);
        float T = n * t.Dt, x = 1f / n, x2 = x * x, x3 = x2 * x;
        float h00 = 2f * x3 - 3f * x2 + 1f, h10 = x3 - 2f * x2 + x, h01 = -2f * x3 + 3f * x2, h11 = x3 - x2;
        float along = h00 * Vector2.Dot(rel, d) + h10 * T * Vector2.Dot(rv, d) + h01 * aheadTo + h11 * T * (first ? 0f : DummyReleaseMargin);
        float across = h00 * Vector2.Dot(rel, s) + h10 * T * Vector2.Dot(rv, s);
        var target = new Vector2(p.Position.X, p.Position.Z) + d * along + s * across;
        var carry = (target - new Vector2(ball.Position.X, ball.Position.Z)) / t.Dt;
        var free = new Vector2(ball.Velocity.X, ball.Velocity.Z);
        var v = free + (carry - free) * share;
        ball.Velocity = new Vector3(v.X, ball.Velocity.Y, v.Y);
        ball.Spin = ball.RollingSpin(ball.Velocity);
    }

    /// <summary>The run step-over's and body feint's ball as it is released: along his travel at his ground speed plus the margin (blended as in <see cref="CarryDummyBall"/>).</summary>
    static Vector3 DummyRelease(PlayerState p, BallAction a, Ball ball, Tuning t)
    {
        float share = DummyRunShare(a.SkillEntry, t);
        var free = new Vector2(ball.Velocity.X, ball.Velocity.Z);
        Vector2 d = TravelOf(p, a);
        var rel = d * (new Vector2(p.Velocity.X, p.Velocity.Z).Length() + DummyReleaseMargin);
        var v = free + (rel - free) * share;
        return new Vector3(v.X, ball.Velocity.Y, v.Y);
    }

    /// <summary>
    /// A tick of a sole move's reach: the player runs on at his entry speed and is steered (at most sole_reach_steer) so the
    /// foot's contact point meets the free ball at the contact tick, predicted as the touch planner's pre-windup does. The
    /// action's contact point is the predicted ball centre there.
    /// </summary>
    static MoveAssist ReachTick(PlayerState p, BallAction a, Ball ball, int tick, float cap, Tuning t)
    {
        // A ball roll's, elastico's or spin's run eases toward its forward speed from the move's start, the reach included.
        float hold = MathF.Min(a.SkillEntry, cap);
        float eased = EasedForwardSpeed(a.Kind, t);
        bool roll = float.IsFinite(eased), ballRoll = a.Kind == ActionKind.BallRoll;
        float floor = MathF.Min(hold, eased), budget = t.Movement.BalanceDeceleration * t.Dt;
        int at = tick;
        float Next(float r) => ballRoll ? RollRun(a, ++at, t) : roll ? MathF.Min(hold, MathF.Max(floor, r - budget)) : hold;
        float run = ballRoll ? RollRun(a, tick, t) : Next(MathF.Min(hold, p.RunSpeed));
        Vector2 fwd = a.HoldDirection;
        var legs = new TickIntent { MoveX = fwd.X, MoveZ = fwd.Y, Sprint = 1f };
        int moves = a.ContactTick - tick + 1;
        var c = p.Scratch();
        var b = ball.ScratchA();
        float rp = run;
        for (int i = 0; i < moves; i++)
        {
            Movement.Step(c, legs, t.Dt, p.MoveTime + (i + 1) * t.Dt, new MoveAssist(cap, 1f, Vector2.Zero, rp), t, null);
            rp = Next(rp);
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

    /// <summary>Ball roll from standing: the ball ends this far ahead of the player along the run at the release, metres.</summary>
    const float BallRollEndAhead = 0.3f;
    /// <summary>Ball roll at a run: the ball ends this far ahead of the player along his direction of travel at the release ...</summary>
    const float RollRunAhead = 0.45f;
    /// <summary>... and this far to the move's side of that line (he drifts on that way as his step eases out after it), metres.</summary>
    const float RollRunAside = 0.05f;
    /// <summary>The ball roll's step rises to its peak over this share of the roll ...</summary>
    const float RollStepRise = 0.5f;
    /// <summary>... and eases off to this share of the peak by the release (the rest eases out after it, see TouchPlanner).</summary>
    const float RollStepKeep = 0.65f;
    /// <summary>The ball roll's step is at most this fast, m/s.</summary>
    const float RollStepMax = 3.2f;
    /// <summary>At a run the ball closes on the release this much faster than his planned ground speed (it is never slower than him), m/s.</summary>
    const float RollReleaseMargin = 0.15f;

    /// <summary>The ball roll's release pace along the run for a player moving at <paramref name="pv"/>: his pace along it from standing, his ground speed at a run, m/s.</summary>
    static float RollReleasePace(BallAction a, Vector2 pv, Tuning t)
    {
        float along = MathF.Max(0f, Vector2.Dot(pv, a.HoldDirection));
        return along + (pv.Length() - along) * RollRunShare(a.SkillEntry, t);
    }

    /// <summary>At a run the ball roll's release follows the left stick turned at most this far toward the roll's side, degrees.</summary>
    const float RollReleaseStickMaxDeg = 60f;

    /// <summary>
    /// The way the ball roll releases the ball (unit, X, Z): along the run, or at a run (ball_roll_run_blend) toward the left
    /// stick when it points toward the roll's side, at most <see cref="RollReleaseStickMaxDeg"/> off the run, so the exit touch
    /// that way is an ordinary one instead of a braking turn; a stick straight on or toward the other side keeps the run's line.
    /// </summary>
    internal static Vector2 RollReleaseDir(BallAction a, Vector2 stick, Tuning t)
    {
        Vector2 fwd = a.HoldDirection, side = SideOf(a);
        if (stick.Length() < t.Touch.StickThreshold || a.Side == SkillSide.None) return fwd;
        float ang = MathF.Atan2(Vector2.Dot(stick, side), Vector2.Dot(stick, fwd));
        if (ang <= 0f) return fwd;
        ang = MathF.Min(ang, RollReleaseStickMaxDeg * MathF.PI / 180f) * RollRunShare(a.SkillEntry, t);
        return fwd * MathF.Cos(ang) + side * MathF.Sin(ang);
    }

    /// <summary>How much of the run version of the ball roll a roll entered at <paramref name="entry"/> m/s takes, 0..1.</summary>
    static float RollRunShare(float entry, Tuning t) => Math.Clamp(t.Skill.BallRollRunBlend.Evaluate(entry), 0f, 1f);

    /// <summary>How far the ball roll's planned step has moved the player toward the side over its first <paramref name="ticks"/> ticks, per m/s of peak, metres.</summary>
    static float RollStepTravel(BallAction a, int ticks, Tuning t)
    {
        int dur = Math.Max(1, a.EndTick - a.ContactTick);
        float d = 0f;
        for (int i = 0; i < Math.Min(ticks, dur); i++) d += RollStepShape((i + 0.5f) / dur) * t.Dt;
        return d;
    }

    /// <summary>
    /// The ball roll's running speed on <paramref name="tick"/>: the entry pace through the reach (as the reach is planned), then
    /// from the sole's landing it eases down no faster than ball_roll_brake to ball_roll_keep of the entry running speed, m/s.
    /// </summary>
    static float RollRun(BallAction a, int tick, Tuning t)
    {
        var k = t.Skill;
        // From the entry pace itself, so a roll entered above the cap (on a touch's boost) does not drop to it in a tick.
        float hold = a.SkillEntry;
        float floor = hold * k.BallRollKeep.Evaluate(a.SkillEntry);
        return MathF.Min(hold, MathF.Max(floor, hold - k.BallRollBrake * (tick + 1 - a.ContactTick) * t.Dt));
    }

    /// <summary>The ball roll's step at progress <paramref name="x"/> (0..1) of the roll, as a share of its peak.</summary>
    static float RollStepShape(float x) =>
        x < RollStepRise ? Smooth(x / RollStepRise) : 1f - (1f - RollStepKeep) * Smooth((x - RollStepRise) / (1f - RollStepRise));

    /// <summary>
    /// The ball roll's plan from the sole's landing (<paramref name="lat0"/>: the ball toward the move's side of the player then,
    /// metres): the step's peak, m/s, the ball's distance ahead of the player along the run at the release, metres, and the
    /// player's planned ground velocity then along the run and toward the side, m/s. From standing he steps ball_roll_follow of
    /// the roll's distance and the ball ends BallRollEndAhead in front of him; at a run his step is sized so that at the release
    /// the ball is RollRunAhead ahead of him along his travel (and RollRunAside toward the side), so he has gone sideways with
    /// it instead of letting it cross under him; a roll in between blends the two (ball_roll_run_blend).
    /// </summary>
    static (float Peak, float AheadEnd, float RunEnd, float StepEnd) PlanRoll(BallAction a, float lat0, Tuning t)
    {
        var k = t.Skill;
        int dur = Math.Max(1, a.EndTick - a.ContactTick);
        float dist = k.BallRollDistance.Evaluate(a.SkillEntry);
        float travel = RollStepTravel(a, dur, t);
        float share = RollRunShare(a.SkillEntry, t);
        float uE = RollRun(a, a.EndTick - 1, t);
        // Positive while the ball would still end further toward the side than wanted.
        float Excess(float peak)
        {
            float vs = RollStepKeep * peak, n = MathF.Max(1e-3f, MathF.Sqrt(uE * uE + vs * vs));
            return dist + lat0 - peak * travel - (RollRunAhead * vs + RollRunAside * uE) / n;
        }
        float lo = 0f, hi = RollStepMax, run;
        if (Excess(lo) <= 0f) run = lo;
        else if (Excess(hi) >= 0f) run = hi;
        else
        {
            for (int i = 0; i < 24; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (Excess(mid) > 0f) lo = mid; else hi = mid;
            }
            run = 0.5f * (lo + hi);
        }
        float stand = travel > 0f ? MathF.Min(RollStepMax, k.BallRollFollow * dist / travel) : 0f;
        float peakOut = stand + (run - stand) * share;
        float vsE = RollStepKeep * run, nE = MathF.Max(1e-3f, MathF.Sqrt(uE * uE + vsE * vsE));
        float aheadRun = (RollRunAhead * uE - RollRunAside * vsE) / nE;
        return (peakOut, BallRollEndAhead + (aheadRun - BallRollEndAhead) * share, uE, RollStepKeep * peakOut);
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
                // Across the run (along it the ball is kept at his foot as he travels, see CarriedBall).
                to = b0 + r3 * ((a.Side == SkillSide.Left ? -1f : 1f) * k.BallRollDistance.Evaluate(entry));
                break;
            case ActionKind.DragBack:
                // A turn at the feet pulls the ball onto the stick's line rather than straight back.
                to = a.FeetTurn ? b0 + new Vector3(a.SkillCut.X, 0f, a.SkillCut.Y) * k.FeetTurnDistance : b0 - f3 * k.DragBackDistance;
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
    internal static TouchResult? Step(PlayerState p, in TickIntent intent, Ball ball, int tick, float now, Tuning t, Pitch? pitch)
    {
        var a = p.SkillAction!;
        var k = t.Skill;
        TouchResult? made = null;
        bool carried = Reaches(a.Kind);
        float miss = 0f;
        if (carried && tick == a.ContactTick)
        {
            var cp = TouchPlanner.ContactPointFor(p, a.Foot, t);
            miss = new Vector2(ball.Position.X - cp.X, ball.Position.Z - cp.Z).Length();
            LaySolePath(p, a, ball, t);
            var rel = new Vector2(ball.Position.X - p.Position.X, ball.Position.Z - p.Position.Z);
            a.RollAhead0 = Vector2.Dot(rel, a.HoldDirection);
            if ((TwoContacts(a.Kind) || a.Kind == ActionKind.BallRoll) && a.Side != SkillSide.None) a.SideOffset0 = Vector2.Dot(rel, SideOf(a));
            var face = new Vector2(MathF.Cos(p.Facing), MathF.Sin(p.Facing));
            a.SpinBall0 = new Vector2(Vector2.Dot(rel, face), Vector2.Dot(rel, GestureReader.RightOf(face)));
        }
        Vector3 contactBall = ball.Position;
        if (tick == a.Contact2Tick && TwoContacts(a.Kind))
            a.Contact2PathMiss = new Vector2(ball.Position.X - a.LastTarget.X, ball.Position.Z - a.LastTarget.Z).Length();
        // The heel-to-heel carries the ball only until the flick and the elastico until its inside plays it on; the others to the
        // release.
        if (carried && tick >= a.ContactTick && !(a.Kind is ActionKind.HeelToHeel or ActionKind.Elastico && tick >= a.Contact2Tick))
        {
            Vector3 target = CarriedBall(p, a, tick, t);
            a.LastTarget = target;
            var v = new Vector3((target.X - ball.Position.X) / t.Dt, ball.Velocity.Y, (target.Z - ball.Position.Z) / t.Dt);
            ball.Velocity = v;
            ball.Spin = ball.RollingSpin(v);
        }
        if (a.Kind is ActionKind.StepOver or ActionKind.BodyFeint) CarryDummyBall(p, a, ball, tick, t);
        if (a.Kind == ActionKind.HeelToHeel && tick == a.Contact2Tick)
        {
            // The trailing foot's flick: straight on at heel_to_heel_flick_ratio times his running speed (at least the minimum),
            // never faster than he can catch, and chased as a knock-on is (without the dribble cap) until he meets it.
            var f3 = new Vector3(a.HoldDirection.X, 0f, a.HoldDirection.Y);
            float want = MathF.Max(k.HeelToHeelFlickMinSpeed, k.HeelToHeelFlickRatio * p.RunSpeed);
            float speed = MathF.Min(want, TouchPlanner.CatchablePush(p, ball, f3, 0f, t));
            var v = f3 * speed;
            ball.Velocity = new Vector3(v.X, ball.Velocity.Y, v.Z);
            ball.Spin = ball.RollingSpin(ball.Velocity);
            p.KnockOnFrom = now;
            p.KnockOnUntil = now + TouchModel.KnockOnCatchTime(p.Sheet, t) + t.Touch.KnockOnChaseGrace;
            p.KnockOnDirection = a.HoldDirection;
        }
        if (a.Kind == ActionKind.Elastico && tick == a.Contact2Tick)
        {
            var v = ElasticoExit(p, a, intent, ball, tick, now, t, pitch);
            ball.Velocity = v;
            ball.Spin = ball.RollingSpin(v);
        }
        if (tick == a.ContactTick && (carried || a.Kind == ActionKind.FakeCut))
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
        else if (tick == a.Contact2Tick && TwoContacts(a.Kind))
        {
            // The second contact: the elastico's inside (its exit touch), the spin's other sole, the heel-to-heel's flick.
            p.LastTouchTime = now;
            p.LastTouchFoot = a.Foot2;
            p.LastTouchKind = a.Kind;
            made = new TouchResult(a.Kind, a.Foot2, contactBall, ball.Velocity, 0f);
        }
        TouchPlanner.UpdatePhases(p, tick, t);
        return made;
    }

    /// <summary>How far ahead the exit-touch forecast looks, seconds (more than a touch's windup, so the view can draw one).</summary>
    const float ExitForecastSeconds = 0.2f;

    /// <summary>
    /// The exit touch the core expects on the end tick of the move under way (see <see cref="PlayerState.SkillExitTouch"/>), or null:
    /// none under way, more than <see cref="ExitForecastSeconds"/> to go, the stick released, a heel-to-heel or elastico (its second
    /// contact is the exit touch), or the ball predicted out of foot reach then. What the touch planner decides for an immediate touch, on the player and
    /// the ball carried on to the end tick at their pace and the ball's velocity as <see cref="Finish"/> leaves it (exact on the tick
    /// before the end, where the ball is already where the touch meets it). Changes no state.
    /// </summary>
    internal static BallAction? ExitForecast(PlayerState p, in TickIntent intent, Ball ball, int tick, Tuning t)
    {
        var a = p.SkillAction;
        if (a == null || a.Kind is ActionKind.HeelToHeel or ActionKind.Elastico || a.Phase == ActionPhase.Done) return null;
        int n = a.EndTick - tick;
        if (n < 1 || n * t.Dt > ExitForecastSeconds + 1e-5f) return null;
        if (intent.Move.Length() < t.Touch.StickThreshold) return null;
        float dt = t.Dt;
        var pv = new Vector3(p.Velocity.X, 0f, p.Velocity.Z);
        // The player on the end tick (he moves before the touch on it), the ball where it is left after the tick before.
        var feet = p.Position + pv * (n * dt);
        Vector3 b;
        if (Reaches(a.Kind) && tick >= a.ContactTick)
        {
            b = CarriedBall(p, a, a.EndTick - 1, t);
            // (the carried ball goes with the player: moved on with him to the tick before the end)
            if (a.Kind is ActionKind.Spin or ActionKind.BallRoll) b += pv * ((n - 1) * dt);
        }
        else b = ball.Position + new Vector3(ball.Velocity.X, 0f, ball.Velocity.Z) * ((n - 1) * dt);
        b.Y = ball.Position.Y;
        if (!TouchModel.InReach(p.Sheet, feet, b, t)) return null;

        // The ball's travel as Finish leaves it.
        Vector2 fwd = a.HoldDirection;
        Vector3 bv = a.Kind switch
        {
            ActionKind.BallRoll => RollReleaseDir(a, intent.Move, t) is var rd ? new Vector3(rd.X, 0f, rd.Y) * RollReleasePace(a, new Vector2(pv.X, pv.Z), t) : default,
            ActionKind.DragBack or ActionKind.FakeStop => Vector3.Zero,
            ActionKind.Spin => FacedPace(p),
            ActionKind.StepOver or ActionKind.BodyFeint => DummyRelease(p, a, ball, t) with { Y = 0f },
            _ => new Vector3(ball.Velocity.X, 0f, ball.Velocity.Z),
        };
        float run = a.Kind == ActionKind.Spin ? FacedPace(p).Length() : p.RunSpeed;
        Foot foot = p.IsStepping ? Gait.SwingFoot((p.GaitPhase + p.StepFrequency * n * dt) % 2f)
            : TouchModel.ChooseFoot(p.Sheet, feet, p.Facing, b, t);
        var k = t.Touch;
        var wanted = Vector3.Normalize(new Vector3(intent.Move.X, 0f, intent.Move.Y));
        ActionKind kind = p.KnockOnRequested ? ActionKind.KnockOn : TouchPlanner.ModeKind(TouchModel.ModeFor(intent, t));
        float turnSign = 0f;
        if (kind != ActionKind.KnockOn)
        {
            var bv2 = new Vector2(bv.X, bv.Z);
            float reference = bv2.Length() > 0.3f ? MathF.Atan2(bv2.Y, bv2.X) : p.Facing;
            float turnDeg = MathF.Abs(Movement.WrapAngle(MathF.Atan2(wanted.Z, wanted.X) - reference)) * 180f / MathF.PI;
            if (run > k.ExecutionMinSpeed && turnDeg > k.ExecutionAngle) kind = ActionKind.Turn;
            if (turnDeg > k.CutDeg && run > k.CutMinSpeed) kind = ActionKind.Cut;
            turnSign = TouchPlanner.TurnSign(wanted, bv);
        }
        var cp = new Vector3(b.X, t.Ball.Radius, b.Z);
        return new BallAction
        {
            Kind = kind,
            Phase = ActionPhase.Windup,
            Foot = foot,
            Surface = TouchPlanner.SurfaceFor(kind, foot, turnSign),
            PlanTick = a.EndTick,
            ContactTick = a.EndTick,
            EndTick = a.EndTick,
            ContactPoint = cp,
            NextMeetTick = a.EndTick,
            NextMeetPoint = cp + wanted * 2f,
            NextMeetFoot = foot,
        };
    }

    /// <summary>The spin's ball velocity out of the turn: along his facing at his pace that way.</summary>
    static Vector3 FacedPace(PlayerState p)
    {
        var face = new Vector2(MathF.Cos(p.Facing), MathF.Sin(p.Facing));
        float pace = MathF.Max(0f, Vector2.Dot(new Vector2(p.Velocity.X, p.Velocity.Z), face));
        return new Vector3(face.X * pace, 0f, face.Y * pace);
    }

    /// <summary>Elastico: the carried ball eases toward this far ahead of the player along the run (reached at the move's end), metres.</summary>
    const float ElasticoEndAhead = 0.25f;
    /// <summary>
    /// The elastico's inside plays the ball on across him toward the move's side: at least this far off the run (it comes back
    /// across in front of him, even for an exit straight on) ...
    /// </summary>
    const float ElasticoExitMinDeg = 30f;
    /// <summary>... and at most this far, degrees (what that foot's inside can play).</summary>
    const float ElasticoExitMaxDeg = 90f;
    /// <summary>With the stick released the elastico's inside sets the ball across in front of him this far off the run, degrees.</summary>
    const float ElasticoSetDeg = 45f;
    /// <summary>The elastico's player drives toward the exit side at this pace by the release, m/s.</summary>
    const float ElasticoExitSideSpeed = 2.0f;
    /// <summary>The spin brings the ball to this far ahead of the player in his own frame, metres ...</summary>
    const float SpinBallAhead = 0.3f;
    /// <summary>... over this share of the move.</summary>
    const float SpinBallSettle = 0.15f;

    /// <summary>The unit ground direction (X, Z) toward the move's side.</summary>
    static Vector2 SideOf(BallAction a) => GestureReader.RightOf(a.HoldDirection) * (a.Side == SkillSide.Left ? -1f : 1f);

    static float Smooth(float u) { u = Math.Clamp(u, 0f, 1f); return u * u * (3f - 2f * u); }

    /// <summary>
    /// Where a carried ball is to be at the end of <paramref name="tick"/> (the player has moved this tick): a sole move's path,
    /// and for the ball roll, elastico and heel-to-heel the ball kept at the player's foot along the run as he actually travels.
    /// </summary>
    static Vector3 CarriedBall(PlayerState p, BallAction a, int tick, Tuning t)
    {
        var k = t.Skill;
        var f3 = new Vector3(a.HoldDirection.X, 0f, a.HoldDirection.Y);
        int dur = a.EndTick - a.ContactTick;
        float u1 = Math.Clamp((tick + 1 - a.ContactTick) / (float)dur, 0f, 1f);
        float along = Vector3.Dot(p.Position, f3);
        switch (a.Kind)
        {
            case ActionKind.Elastico:
            {
                // Across the run, from where it was first played: the outside pushes it elastico_push_distance away from the side
                // over elastico_push_seconds and the foot wraps round it, until the inside plays it on at the second contact (see
                // ElasticoExit); along it the ball eases toward ElasticoEndAhead ahead of him.
                var s2 = SideOf(a);
                var s3 = new Vector3(s2.X, 0f, s2.Y);
                float tau = (tick + 1 - a.ContactTick) * t.Dt;
                float tp = Ticks(k.ElasticoPushSeconds, t) * t.Dt;
                float lat = -k.ElasticoPushDistance * Smooth(tau / tp);
                float ahead = a.RollAhead0 + (ElasticoEndAhead - a.RollAhead0) * u1;
                var b = a.SkillBallFrom + s3 * lat;
                return b - f3 * Vector3.Dot(b, f3) + f3 * (along + ahead);
            }
            case ActionKind.HeelToHeel:
            {
                // The heel knocks it back heel_to_heel_back against the trailing foot (relative to the running player).
                float uh = Math.Clamp((tick + 1 - a.ContactTick) / (float)(a.Contact2Tick - a.ContactTick), 0f, 1f);
                float ahead = a.RollAhead0 - k.HeelToHeelBack * Smooth(uh);
                var b = a.SkillBallFrom;
                return b - f3 * Vector3.Dot(b, f3) + f3 * (along + ahead);
            }
            case ActionKind.Spin:
            {
                // The ball turns with the body, in front of it: from where it was against him at the first contact to
                // SpinBallAhead straight ahead of his facing over the first SpinBallSettle of the move, so it is dragged back and
                // round as he turns and is ahead of him again at the release.
                float e = Smooth(u1 / SpinBallSettle);
                var b0 = a.SpinBall0;
                float ahead = b0.X + (SpinBallAhead - b0.X) * e, right = b0.Y * (1f - e);
                var face = new Vector2(MathF.Cos(p.Facing), MathF.Sin(p.Facing));
                var rt = GestureReader.RightOf(face);
                return new Vector3(p.Position.X + face.X * ahead + rt.X * right, t.Ball.Radius, p.Position.Z + face.Y * ahead + rt.Y * right);
            }
        }
        Vector3 target = Path(a, u1);
        if (a.Kind == ActionKind.BallRoll)
        {
            // Along the run the ball is kept at the player's foot as he actually travels: from where it was ahead of him when
            // the sole landed to the plan's distance at the release, closing at the end on his planned ground speed (the pace it
            // is released at, see Finish), so the exit touch is a normal one.
            var plan = PlanRoll(a, a.SideOffset0, t);
            float share = RollRunShare(a.SkillEntry, t);
            // Across it the sole takes the ball over the roll's distance; where his step has fallen behind (or got ahead of) the
            // plan, the ball goes with him, so it still ends where the plan puts it against him.
            var s2 = SideOf(a);
            var s3 = new Vector3(s2.X, 0f, s2.Y);
            float stepped = Vector3.Dot(p.Position - a.SkillBallFrom, s3) + a.SideOffset0;
            // (Never further across than the longest roll, ball_roll_distance's last point.)
            var rd = t.Skill.BallRollDistance;
            float roomOut = MathF.Max(0f, rd[rd.Count - 1].Y - rd.Evaluate(a.SkillEntry));
            target += s3 * MathF.Min(roomOut, stepped - plan.Peak * RollStepTravel(a, tick - a.ContactTick, t));
            float m0 = plan.AheadEnd - a.RollAhead0;
            float m1 = share * (MathF.Sqrt(plan.RunEnd * plan.RunEnd + plan.StepEnd * plan.StepEnd) - plan.RunEnd + RollReleaseMargin) * dur * t.Dt;
            float x = u1, x2 = x * x, x3 = x2 * x;
            float ahead = (2f * x3 - 3f * x2 + 1f) * a.RollAhead0 + (x3 - 2f * x2 + x) * m0 + (-2f * x3 + 3f * x2) * plan.AheadEnd + (x3 - x2) * m1;
            target = target - f3 * Vector3.Dot(target, f3) + f3 * (along + ahead);
        }
        return target;
    }

    /// <summary>
    /// The elastico's second contact is its exit touch: the inside plays the ball on across him toward the stick, turned into
    /// <see cref="ElasticoExitMinDeg"/> to <see cref="ElasticoExitMaxDeg"/> toward the move's side off the run, as a dribble touch
    /// made at once that way would push it (see TouchPlanner.SkillExitPush), onto a swing at least a windup after the move's end,
    /// so the controller's next touch, planned from the end as usual, meets it. With the stick released it is set across in front
    /// of him (<see cref="ElasticoSetDeg"/> off the run) as a close-control touch would push it. Readies the exit burst's scale for
    /// that direction (the burst starts at the end, see <see cref="Finish"/>). Returns the ball's velocity.
    /// </summary>
    static Vector3 ElasticoExit(PlayerState p, BallAction a, in TickIntent intent, Ball ball, int tick, float now, Tuning t, Pitch? pitch)
    {
        Vector2 fwd = a.HoldDirection, side = SideOf(a), stick = intent.Move;
        var user = intent;
        float ang;
        if (stick.Length() >= t.Touch.StickThreshold)
        {
            float min = ElasticoExitMinDeg * MathF.PI / 180f, max = ElasticoExitMaxDeg * MathF.PI / 180f;
            ang = Math.Clamp(MathF.Atan2(Vector2.Dot(stick, side), Vector2.Dot(stick, fwd)), min, max);
        }
        else
        {
            ang = ElasticoSetDeg * MathF.PI / 180f;
            user.CloseControl = true;
            user.Sprint = 0f;
        }
        var dir = fwd * MathF.Cos(ang) + side * MathF.Sin(ang);
        a.ExitBurstScale = ExitScale(a, stick.Length() >= t.Touch.StickThreshold ? dir : Vector2.Zero, t);
        int minJ = Math.Max(0, a.EndTick - tick) + TouchPlanner.WindupTicks(t);
        var v = TouchPlanner.SkillExitPush(p, user, dir, ball, minJ, now, t, pitch, out int steps);
        // The next touch is counted from here, as after a push.
        p.PushSteps = steps;
        p.StepsSinceTouch = 0;
        return v;
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
                // Released along the run, or at a run toward the left stick turned toward the roll's side (see RollReleaseDir): at
                // his pace along the run from standing, at his ground speed at a run (his step toward the side eases out after the
                // move, so the ball is not left behind) ...
                // ... and never slower than the sole was taking it that way.
                var dir = RollReleaseDir(a, intent.Move, t);
                float pace = MathF.Max(RollReleasePace(a, new Vector2(p.Velocity.X, p.Velocity.Z), t),
                    Vector2.Dot(new Vector2(ball.Velocity.X, ball.Velocity.Z), dir));
                var v = new Vector3(dir.X * pace, ball.Velocity.Y, dir.Y * pace);
                ball.Velocity = v;
                ball.Spin = ball.RollingSpin(v);
                break;
            }
            case ActionKind.StepOver:
            case ActionKind.BodyFeint:
                // At a run the ball rolls on ahead of him a little faster than he runs (see CarryDummyBall).
                if (DummyRunShare(a.SkillEntry, t) > 0f)
                {
                    ball.Velocity = DummyRelease(p, a, ball, t);
                    ball.Spin = ball.RollingSpin(ball.Velocity);
                }
                break;
            case ActionKind.DragBack:
            case ActionKind.FakeStop:
                ball.Velocity = new Vector3(0f, ball.Velocity.Y, 0f);
                ball.Spin = Vector3.Zero;
                break;
            case ActionKind.Spin:
            {
                // Out of the turn facing his original way: his step becomes his run again (no change of his ground velocity), and
                // the ball, ahead of him, rolls on at his pace.
                var face = new Vector2(MathF.Cos(p.Facing), MathF.Sin(p.Facing));
                float pace = MathF.Max(0f, Vector2.Dot(new Vector2(p.Velocity.X, p.Velocity.Z), face));
                p.RunSpeed = pace;
                p.SteerCarry = Vector2.Zero;
                p.SteerVelocity = Vector3.Zero;
                var v = new Vector3(face.X * pace, ball.Velocity.Y, face.Y * pace);
                ball.Velocity = v;
                ball.Spin = ball.RollingSpin(v);
                break;
            }
        }
        if (a.Kind == ActionKind.DragBack) p.SkillCooldownUntil = now + (a.FeetTurn ? k.FeetTurnCooldown : k.DragBackCooldown);
        if (a.FeetTurn)
        {
            // The controller's own turn: straight into the exit touch, with no chain window or exit burst.
            p.SkillExitTick = tick;
            return;
        }
        p.SkillChainUntil = now + k.ChainWindow;

        // The exit burst rides on the exit touch (the planner's next touch), like a touch boost: started here it would make
        // the legs outrun a ball at the feet before that touch could be planned.
        // (The elastico's for the way its second contact played the ball on.)
        float scale = a.Kind == ActionKind.Elastico && !float.IsNaN(a.ExitBurstScale) ? a.ExitBurstScale : ExitScale(a, intent.Move, t);
        if (scale > 0f)
        {
            bool roll = a.Kind == ActionKind.BallRoll;
            p.SkillBoostFactor = 1f + (roll ? k.BallRollBurst : k.Burst).ForRating(a.SkillEffective) * scale;
            p.SkillBoostDuration = (roll ? k.BallRollBurstDuration : k.BurstDuration).ForRating(a.SkillEffective);
            p.SkillBoostCap = k.BurstSpeedCap.ForRating(a.SkillEffective) * scale;
            p.SkillBoostUntil = now + ExitTouchWithin;
        }
        if (a.Kind is ActionKind.HeelToHeel or ActionKind.Elastico)
        {
            // The heel-to-heel's flick or the elastico's inside was the exit touch (the ball is already played on): the burst
            // starts once he has moved this tick (as after an exit touch), and the controller plans the next touch as usual (after
            // the flick he runs on to the ball as after a knock-on).
            p.SkillBurstAtTick = tick;
            return;
        }
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
        if (a.Kind == ActionKind.Elastico)
        {
            // The elastico sold the side away from its exit side: going away from that one gets the most.
            float towardSold = a.Side == SkillSide.Left ? exit : -exit;
            return Math.Clamp(k.SidedExitBurst.Evaluate(-towardSold), 0f, 1f);
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
