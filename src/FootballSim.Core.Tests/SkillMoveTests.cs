using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>Skill moves through the real world: on an open field (the keeper far away), from scripted stick traces.</summary>
public class SkillMoveTests
{
    public enum Entry { Standing, Jog, Sprint }

    static World Field(ulong seed = 1, PlayerSheet? sheet = null, Tuning? t = null)
    {
        var sc = new Scenario
        {
            Id = "field",
            Pitch = new PitchSpec { Length = 400f, Width = 200f },
            Ball = new StartSpot { X = 150.5f, Z = 0f },
            Attacker = new StartSpot { X = 150f, Z = 0f },
        };
        var w = new World(t ?? TestData.Tuning(), sc, sheet ?? TestData.Player("striker_a"), TestData.Player("the_wall"), seed);
        w.Begin();
        return w;
    }

    /// <summary>The left stick before the gesture: standing is a short jog to take the ball, then the stick released.</summary>
    static TickIntent Run(Entry e, int tick) => e switch
    {
        Entry.Standing => tick < 60 ? new TickIntent { MoveX = 1f } : default,
        Entry.Jog => new TickIntent { MoveX = 1f },
        _ => new TickIntent { MoveX = 1f, Sprint = 1f },
    };

    sealed class Outcome
    {
        public readonly List<(int Tick, SimEvent E)> Events = new();
        public bool Lost;
        public int MoveStart = -1, MoveEnd = -1;
        public ActionKind? Kind;
        public BallAction? Move;
        public float BallDistanceAtEnd = float.NaN;
        /// <summary>The ball's distance ahead of the player along the move's run direction at its contact tick.</summary>
        public float AheadAtContact = float.NaN;
        public bool InPossessionAtEnd;
    }

    /// <summary>
    /// Runs a field world: the entry run for 2 s, then <paramref name="trace"/> (tick from the gesture start to the intent; the
    /// run is passed in) for <paramref name="traceTicks"/>, then <paramref name="after"/> for 2.5 s.
    /// </summary>
    /// <param name="startWhen">When set, the trace starts on the first tick from 2 s on where this holds (at most 1 s later).</param>
    static Outcome Play(Entry e, Func<int, TickIntent, TickIntent> trace, int traceTicks, Func<TickIntent, TickIntent>? after = null,
        World? world = null, int g0 = 240, Func<World, bool>? startWhen = null)
    {
        var w = world ?? Field();
        var p = w.Attacker;
        var o = new Outcome();
        var buf = new TickIntent[w.PlayerCount];
        bool gained = false;
        int total = g0 + 120 + traceTicks + 300;
        bool waiting = startWhen != null;
        for (int tick = 1; tick <= total && w.IsSimulating; tick++)
        {
            if (waiting && tick - 1 >= g0 && (startWhen!(w) || tick - 1 >= g0 + 120)) { g0 = tick - 1; waiting = false; }
            var it = Run(e, tick);
            int i = waiting ? -1 : tick - 1 - g0;
            if (i >= 0 && i < traceTicks) it = trace(i, it);
            else if (i >= traceTicks && after != null) it = after(it);
            buf[0] = it;
            w.Step(buf);
            foreach (var ev in w.Events)
            {
                if (ev.Team != 0 || ev.PlayerIndex != 0) continue;
                o.Events.Add((w.Tick, ev));
                if (ev.Kind == EventKind.PossessionGained) gained = true;
                if (ev.Kind == EventKind.PossessionLost && gained) o.Lost = true;
                if (ev.Kind == EventKind.SkillMove && o.MoveStart < 0)
                {
                    o.MoveStart = w.Tick; o.Kind = ev.Skill; o.Move = p.SkillMove; o.MoveEnd = p.SkillMove!.EndTick;
                }
            }
            if (o.Move != null && w.Tick == o.Move.ContactTick)
                o.AheadAtContact = (w.Ball.Position.X - p.Position.X) * o.Move.RunDirection.X + (w.Ball.Position.Z - p.Position.Z) * o.Move.RunDirection.Y;
            if (w.Tick == o.MoveEnd)
            {
                o.BallDistanceAtEnd = TouchModel.HorizontalDistance(p.Position, w.Ball.Position);
            }
        }
        o.InPossessionAtEnd = p.InPossession;
        return o;
    }

    static TickIntent Right(TickIntent it, Vector2 r) { it.RightX = r.X; it.RightZ = r.Y; return it; }

    // Traces for a player running along +X: forward is +X, his right is +Z.
    static TickIntent StepOver(int i, TickIntent it, int side) => Right(it, i < 6 ? new Vector2(1f, 0f) : i < 12 ? new Vector2(0f, side) : Vector2.Zero);
    static TickIntent Feint(int i, TickIntent it, int side) => Right(it, i < 5 ? new Vector2(0f, side) : Vector2.Zero);
    static TickIntent Roll(int i, TickIntent it, int side) => Right(it, i < 16 ? new Vector2(0f, side) : Vector2.Zero);
    /// <summary>The drag-back: the left stick back with both shoulder buttons held (the right stick back alone does nothing).</summary>
    static TickIntent Drag(int i, TickIntent it)
    {
        if (i >= 8) return it;
        it.MoveX = -1f; it.MoveZ = 0f; it.Finesse = true; it.Chip = true;
        return it;
    }

    public static IEnumerable<object[]> Moves()
    {
        foreach (var e in new[] { Entry.Standing, Entry.Jog })
        {
            foreach (int side in new[] { -1, 1 })
            {
                yield return new object[] { "step_over", e, side };
                yield return new object[] { "body_feint", e, side };
                yield return new object[] { "ball_roll", e, side };
            }
            yield return new object[] { "drag_back", e, 0 };
        }
    }

    static (Func<int, TickIntent, TickIntent> Trace, ActionKind Kind) TraceFor(string move, int side) => move switch
    {
        "step_over" => ((i, it) => StepOver(i, it, side), ActionKind.StepOver),
        "body_feint" => ((i, it) => Feint(i, it, side), ActionKind.BodyFeint),
        "ball_roll" => ((i, it) => Roll(i, it, side), ActionKind.BallRoll),
        _ => ((i, it) => Drag(i, it), ActionKind.DragBack),
    };

    [Theory]
    [MemberData(nameof(Moves))]
    public void EachMoveIsMadeAndThePlayerKeepsTheBall(string move, Entry e, int side)
    {
        var (trace, kind) = TraceFor(move, side);
        // Several seeds: the touch errors move the ball around, the move must still be made and the ball kept. Jogging, the
        // gesture is made as the ball comes back to the foot (a jog touch pushes it beyond the execution radius for a while;
        // how often a move is made at any moment is the skill probe's made_pct).
        for (ulong seed = 1; seed <= 4; seed++)
        {
            var o = Play(e, trace, 40, world: Field(seed),
                startWhen: e == Entry.Jog ? w => w.Attacker.ActionContactEta is > 0.05f and < 0.2f : null);
            Assert.True(o.MoveStart > 0, $"{move} {e} side {side} seed {seed}: no move made");
            Assert.Equal(kind, o.Kind);
            Assert.Equal(side == 0 ? SkillSide.None : side < 0 ? SkillSide.Left : SkillSide.Right, o.Move!.Side);
            Assert.Equal(o.MoveStart, o.Move.StartTick);
            var m = o.Move;
            if (kind is ActionKind.BallRoll or ActionKind.DragBack)
            {
                // The reach: the sole lands at most 0.12 s after the start, the ball centre within 0.30 m of the foot's contact point.
                Assert.InRange(m.ContactTick - m.StartTick, 0, 15);
                Assert.Equal(m.ContactTick, m.ExecuteTick);
                Assert.True(m.Made);
                Assert.InRange(m.ContactMiss, 0f, 0.30f);
            }
            else
            {
                // The foot passes over the ball with it no more than 0.5 m ahead.
                Assert.InRange(o.AheadAtContact, -1f, 0.5f);
                Assert.InRange(m.ContactTick - m.ExecuteTick, 0, 30);
            }
            Assert.False(o.Lost, $"{move} {e} side {side} seed {seed}: possession lost");
            Assert.True(o.InPossessionAtEnd);
            Assert.InRange(o.BallDistanceAtEnd, 0f, 1.3f);
        }
    }

    [Fact]
    public void AMoveDropsTheWaitingTouchAndThePlannerResumesWhenItEnds()
    {
        var w = Field();
        var p = w.Attacker;
        int end = -1;
        bool plannedAfter = false;
        var buf = new TickIntent[w.PlayerCount];
        for (int tick = 1; tick <= 400; tick++)
        {
            var it = Run(Entry.Jog, tick);
            int i = tick - 241;
            if (i >= 0 && i < 40) it = StepOver(i, it, 1);
            var before = p.Action;
            buf[0] = it;
            w.Step(buf);
            if (w.Events.Any(e => e.Kind == EventKind.SkillMove))
            {
                Assert.True(p.Action!.IsSkill);
                Assert.False(p.Action.Pending);
                if (before is { } b && b != p.Action && !b.Made) Assert.Equal(ActionPhase.Done, b.Phase);
                end = p.Action.EndTick;
            }
            // On the tick the move ends the controller takes the ball over: an exit touch at once, or a forecast contact.
            if (end > 0 && w.Tick == end) plannedAfter = !float.IsNaN(p.NextTouchEta) || w.Events.Any(e => e.Kind == EventKind.Touch);
        }
        Assert.True(end > 0);
        Assert.True(plannedAfter, "the controller did not take the ball over on the tick the move ended");
    }

    [Fact]
    public void BallRollAndDragBackAreSoleTouchesTheOthersHaveNoContact()
    {
        var roll = Play(Entry.Jog, (i, it) => Roll(i, it, 1), 40);
        Assert.Contains(roll.Events, x => x.E.Kind == EventKind.Touch && x.Tick == roll.Move!.ContactTick);
        Assert.DoesNotContain(roll.Events, x => x.E.Kind == EventKind.Touch && x.Tick >= roll.MoveStart && x.Tick < roll.Move!.ContactTick);
        Assert.Equal(FootSurface.Sole, roll.Move!.Surface);
        Assert.True(roll.Move.Made);
        var drag = Play(Entry.Jog, (i, it) => Drag(i, it), 40);
        Assert.Equal(FootSurface.Sole, drag.Move!.Surface);
        Assert.True(drag.Move.Made);
        var step = Play(Entry.Jog, (i, it) => StepOver(i, it, -1), 40);
        Assert.False(step.Move!.Made);
        Assert.DoesNotContain(step.Events, x => x.E.Kind == EventKind.Touch && x.Tick >= step.MoveStart && x.Tick < step.MoveEnd);
    }

    [Fact]
    public void DragBackPullsTheBallBackAndThePlayerStops()
    {
        var w = Field();
        var p = w.Attacker;
        Vector3 ballStart = default, playerStart = default;
        int end = -1;
        var buf = new TickIntent[w.PlayerCount];
        for (int tick = 1; tick <= 400 && (end < 0 || w.Tick < end); tick++)
        {
            int i = tick - 241;
            // Released after the gesture so the planner does not run off once the move ends.
            buf[0] = i < 0 ? Run(Entry.Jog, tick) : Drag(i, default);
            w.Step(buf);
            if (w.Events.Any(e => e.Kind == EventKind.SkillMove)) end = p.SkillMove!.EndTick;
            // measured from the sole's landing (the reach before it runs on with the ball)
            if (p.SkillMove is { } dm && w.Tick == dm.ContactTick) { ballStart = dm.ContactPoint; playerStart = p.Position; }
        }
        Assert.True(end > 0);
        Assert.InRange(playerStart.X - p.Position.X, 0.1f, 0.6f);    // stepped back about 0.3 m
        Assert.InRange(ballStart.X - w.Ball.Position.X, 0.5f, 0.8f); // pulled back about 0.65 m
        Assert.True(p.Speed < 0.1f, $"still moving at {p.Speed} m/s");
        Assert.True(p.InPossession);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void BallRollRollsTheBallTowardItsSide(int side)
    {
        var w = Field();
        var p = w.Attacker;
        var buf = new TickIntent[w.PlayerCount];
        float startZ = float.NaN;
        int end = -1;
        for (int tick = 1; tick <= 400 && (end < 0 || w.Tick < end); tick++)
        {
            int i = tick - 241;
            buf[0] = i < 0 ? Run(Entry.Standing, tick) : Roll(i, default, side);
            w.Step(buf);
            if (w.Events.Any(e => e.Kind == EventKind.SkillMove)) { startZ = w.Ball.Position.Z; end = p.SkillMove!.EndTick; }
        }
        Assert.True(end > 0);
        float moved = (w.Ball.Position.Z - startZ) * side;
        Assert.InRange(moved, 0.5f, 0.75f); // about 0.6 m from a standstill
        Assert.True(p.InPossession);
    }

    [Fact]
    public void AGestureWithTheBallOutOfReachWaitsThenIsDropped()
    {
        var t = TestData.Tuning();
        int wait = (int)MathF.Round(t.Skill.MeetWaitSeconds * t.TickRate), buffer = (int)MathF.Round(t.Skill.BufferSeconds * t.TickRate);
        // In possession it waits meet_wait_seconds (the touch controller goes to the ball meanwhile), then is dropped.
        var p = TestData.Outfield();
        var ball = new Ball(t.Ball) { Position = new Vector3(2f, t.Ball.Radius, 0f) };
        SkillMoves.Request(p, new GestureRead(Gesture.BodyFeint, SkillSide.Right, Vector2.Zero), 10);
        for (int tick = 10; tick <= 10 + wait; tick++) Assert.Null(SkillMoves.TryStart(p, ball, true, tick, tick * t.Dt, t));
        Assert.True(p.SkillBufTick >= 0);
        Assert.Null(SkillMoves.TryStart(p, ball, true, 11 + wait, (11 + wait) * t.Dt, t));
        Assert.Equal(-1, p.SkillBufTick); // dropped
        // Without possession only buffer_seconds.
        var q = TestData.Outfield();
        SkillMoves.Request(q, new GestureRead(Gesture.BodyFeint, SkillSide.Right, Vector2.Zero), 10);
        Assert.Null(SkillMoves.TryStart(q, ball, false, 11 + buffer, (11 + buffer) * t.Dt, t));
        Assert.Equal(-1, q.SkillBufTick);

        // The ball comes into reach while it waits: the move starts.
        SkillMoves.Request(p, new GestureRead(Gesture.BodyFeint, SkillSide.Right, Vector2.Zero), 300);
        ball.Position = new Vector3(2f, t.Ball.Radius, 0f);
        Assert.Null(SkillMoves.TryStart(p, ball, true, 310, 310 * t.Dt, t));
        ball.Position = new Vector3(0.7f, t.Ball.Radius, 0f);
        Assert.NotNull(SkillMoves.TryStart(p, ball, true, 400, 400 * t.Dt, t));
        Assert.Equal(ActionKind.BodyFeint, p.SkillMove!.Kind);
    }

    [Fact]
    public void AGestureWaitsForACommittedTouchAndThenTheForecastContact()
    {
        var t = TestData.Tuning();
        var p = TestData.Outfield();
        var ball = new Ball(t.Ball) { Position = new Vector3(0.6f, t.Ball.Radius, 0f) };
        // A touch already committed is played first: the move never drops it.
        var touch = new BallAction { Kind = ActionKind.Sprint, PlanTick = 95, ContactTick = 105 };
        p.TouchAction = touch;
        SkillMoves.Request(p, new GestureRead(Gesture.DragBack, SkillSide.None, Vector2.Zero), 100);
        for (int tick = 100; tick < 105; tick++) Assert.Null(SkillMoves.TryStart(p, ball, true, tick, tick * t.Dt, t));
        touch.Made = true;
        // Then, the ball out of reach, it waits for the forecast contact; there the ball is at the foot (here just outside the
        // radius, inside the contact zone's slack).
        p.SetForecast(250, Foot.Right, new Vector3(0.8f, t.Ball.Radius, 0f));
        ball.Position = new Vector3(2f, t.Ball.Radius, 0f);
        for (int tick = 106; tick < 250; tick++) Assert.Null(SkillMoves.TryStart(p, ball, true, tick, tick * t.Dt, t));
        ball.Position = new Vector3(0.8f, t.Ball.Radius, 0f);
        Assert.NotNull(SkillMoves.TryStart(p, ball, true, 250, 250 * t.Dt, t));
        Assert.Equal(ActionKind.DragBack, p.SkillMove!.Kind);
        Assert.Equal(ActionPhase.Contact, touch.PhaseAt(105, 14, 14));
    }

    [Theory]
    [InlineData("step_over")]
    [InlineData("drag_back")]
    public void AtASprintAGestureRightAfterAPushIsMadeWhenTheBallIsReached(string move)
    {
        var (trace, kind) = TraceFor(move, 1);
        for (ulong seed = 1; seed <= 4; seed++)
        {
            var w = Field(seed);
            var p = w.Attacker;
            // Gesture while a sprint push is on its way, the ball far out of reach and its meet more than 0.5 s off.
            var o = Play(Entry.Sprint, (i, it) => trace(i, it), 40, world: w, startWhen: ww =>
                TouchModel.HorizontalDistance(ww.Attacker.Position, ww.Ball.Position) > 1.3f && ww.Attacker.NextTouchEta > 0.5f);
            int gestureAt = p.LastGestureTick;
            Assert.True(o.MoveStart > 0, $"{move} seed {seed}: no move made");
            Assert.Equal(kind, o.Kind);
            // Made once the controller has brought the ball back within reach (it commits no touch while the gesture waits).
            Assert.True(o.MoveStart >= gestureAt, $"{move} seed {seed}: made before the gesture");
            Assert.False(o.Lost);
            Assert.True(o.InPossessionAtEnd);
        }
    }

    [Theory]
    [InlineData("step_over")]
    [InlineData("body_feint")]
    [InlineData("ball_roll")]
    [InlineData("drag_back")]
    [InlineData("fake_cut")]
    public void TheGroundSpeedIsContinuousThroughASkillExit(string move)
    {
        // From the move's end the controller takes the step over and eases it out: no change of the ground speed faster than
        // 15 m/s^2 (a speed step) over the hand-over, except a speed-up no quicker than the speed law under the exit burst.
        var (trace, _) = TraceFor(move, 1);
        foreach (var e in new[] { Entry.Jog, Entry.Sprint })
        for (ulong seed = 1; seed <= 3; seed++)
        {
            var w = Field(seed);
            var p = w.Attacker;
            var buf = new TickIntent[w.PlayerCount];
            int end = -1;
            float prev = 0f, worst = 0f;
            for (int tick = 1; tick <= 480; tick++)
            {
                var it = Run(e, tick);
                int i = tick - 241;
                if (i >= 0 && i < 40) it = trace(i, it);
                buf[0] = it;
                w.Step(buf);
                if (w.Events.Any(x => x.Kind == EventKind.SkillMove)) end = p.SkillMove!.EndTick;
                // A speed-up from the exit burst may be as quick as the speed law makes it (the move's boosted time constant).
                float law = (w.Tuning.Movement.SprintSpeed.At99 - prev) / Movement.TimeConstantAt(p, w.SimTime, w.Tuning) * w.Tuning.Dt;
                float change = p.Speed - prev;
                if (end > 0 && w.Tick > end && w.Tick <= end + 36 && w.Shots.Count == 0)
                    worst = MathF.Max(worst, change > 0f ? (change > MathF.Max(15f * w.Tuning.Dt, law) ? change : 0f) : -change);
                prev = p.Speed;
            }
            Assert.True(end > 0, $"{move} {e} seed {seed}: no move");
            Assert.True(worst <= 15f * w.Tuning.Dt, $"{move} {e} seed {seed}: ground speed changed {worst / w.Tuning.Dt:F1} m/s^2 in a tick after the move");
            Assert.True(p.InPossession);
        }
    }

    [Fact]
    public void TheStepOverReachesFurtherThanTheOthers()
    {
        var t = TestData.Tuning();
        foreach (var (g, starts) in new[] { (Gesture.StepOver, true), (Gesture.BallRoll, false), (Gesture.DragBack, false), (Gesture.BodyFeint, false) })
        {
            var p = TestData.Outfield();
            var ball = new Ball(t.Ball) { Position = new Vector3(0.9f, t.Ball.Radius, 0f) };
            SkillMoves.Request(p, new GestureRead(g, SkillSide.Right, Vector2.Zero), 1);
            Assert.Equal(starts, SkillMoves.TryStart(p, ball, true, 1, 0f, t) != null);
        }
        // Too high: no move.
        var q = TestData.Outfield();
        var high = new Ball(t.Ball) { Position = new Vector3(0.4f, 0.35f, 0f) };
        SkillMoves.Request(q, new GestureRead(Gesture.StepOver, SkillSide.Right, Vector2.Zero), 1);
        Assert.Null(SkillMoves.TryStart(q, high, true, 1, 0f, t));
    }

    [Fact]
    public void ChainedMovesLoseSkillAndTheChainLapses()
    {
        var sheet = TestData.Player("striker_a").Clone();
        sheet.SkillMoves = 90;
        var w = Field(sheet: sheet);
        var p = w.Attacker;
        var effective = new List<(int Tick, float Eff, int Chain)>();
        var buf = new TickIntent[w.PlayerCount];
        // Body feints right at 2.0 s, 2.6 s and 3.2 s (chained), then one at 6.5 s after the 2 s window has lapsed.
        var at = new[] { 240, 312, 384, 780 };
        for (int tick = 1; tick <= 900; tick++)
        {
            var it = Run(Entry.Standing, tick);
            foreach (int a in at)
                if (tick - 1 - a is >= 0 and < 4) it = Right(it, new Vector2(0f, 1f));
            buf[0] = it;
            w.Step(buf);
            if (w.Events.Any(e => e.Kind == EventKind.SkillMove)) effective.Add((w.Tick, p.SkillEffective, p.SkillChainCount));
        }
        Assert.Equal(4, effective.Count);
        Assert.Equal(new[] { 0, 1, 2, 0 }, effective.Select(x => x.Chain));
        Assert.Equal(90f, effective[0].Eff, 3);
        Assert.Equal(72f, effective[1].Eff, 3);
        Assert.Equal(57.6f, effective[2].Eff, 3);
        Assert.Equal(90f, effective[3].Eff, 3);
    }

    [Fact]
    public void ExitBurstIsLargestAwayFromTheSoldSide()
    {
        var t = TestData.Tuning();
        var a = new BallAction { Kind = ActionKind.StepOver, Side = SkillSide.Right, HoldDirection = new Vector2(1f, 0f) };
        Vector2 Dir(float deg) => new(MathF.Cos(deg * MathF.PI / 180f), MathF.Sin(deg * MathF.PI / 180f));
        // Positive Z is the player's right when he runs along +X.
        Assert.Equal(1f, SkillMoves.ExitScale(a, Dir(-90f), t), 3);   // hard away from the sold side
        Assert.Equal(0.7f, SkillMoves.ExitScale(a, Dir(0f), t), 3);    // straight on
        Assert.Equal(0.7f, SkillMoves.ExitScale(a, Vector2.Zero, t), 3);
        Assert.True(SkillMoves.ExitScale(a, Dir(40f), t) > 0f);         // a little toward it
        Assert.Equal(0f, SkillMoves.ExitScale(a, Dir(50f), t));          // more than 45 toward it: none
        var feint = new BallAction { Kind = ActionKind.BodyFeint, Side = SkillSide.Left, HoldDirection = new Vector2(1f, 0f) };
        Assert.Equal(1f, SkillMoves.ExitScale(feint, Dir(90f), t), 3);
        Assert.True(SkillMoves.ExitScale(feint, Dir(-50f), t) > 0f);    // a feint scales smoothly, no cut-off
        var drag = new BallAction { Kind = ActionKind.DragBack, HoldDirection = new Vector2(1f, 0f) };
        Assert.True(SkillMoves.ExitScale(drag, Dir(90f), t) > SkillMoves.ExitScale(drag, Dir(0f), t));
    }

    [Fact]
    public void ABetterSkillGivesABiggerExitBurstOnTheExitTouch()
    {
        float SpeedAfterExitTouch(int rating)
        {
            var sheet = TestData.Player("striker_a").Clone();
            sheet.SkillMoves = rating;
            var w = Field(sheet: sheet);
            var p = w.Attacker;
            var buf = new TickIntent[w.PlayerCount];
            int end = -1, exit = -1;
            float speed = float.NaN;
            for (int tick = 1; tick <= 500; tick++)
            {
                int i = tick - 241;
                var it = Run(Entry.Standing, tick);
                // A body feint to the right from a standstill, then off straight ahead.
                if (i >= 0) it = Right(new TickIntent { MoveX = 1f }, i < 4 ? new Vector2(0f, 1f) : Vector2.Zero);
                buf[0] = it;
                w.Step(buf);
                if (w.Events.Any(e => e.Kind == EventKind.SkillMove)) end = p.SkillMove!.EndTick;
                if (end > 0 && w.Tick >= end && exit < 0 && w.Events.Any(e => e.Kind == EventKind.Touch)) exit = w.Tick;
                if (exit > 0 && w.Tick == exit + 30) speed = p.RunSpeed;
            }
            Assert.True(exit > 0);
            return speed;
        }
        float hi = SpeedAfterExitTouch(99), lo = SpeedAfterExitTouch(1);
        Assert.True(hi > lo + 0.1f, $"skill 99: {hi} m/s, skill 1: {lo} m/s, 0.25 s after the exit touch");
    }

    // ---- fake shot ----

    static Outcome PlayShot(Entry e, int cancelAt, Vector2 left, int shootTicks = 30)
    {
        var w = Field();
        return Play(e, (i, it) =>
        {
            if (i >= 1) { it.MoveX = left.X; it.MoveZ = left.Y; }
            it.ShootHeld = i < shootTicks;
            it.Cancel = i >= cancelAt && i < cancelAt + 3;
            return it;
        }, 60, world: w);
    }

    [Theory]
    [InlineData(Entry.Standing)]
    [InlineData(Entry.Jog)]
    public void CancelJustAfterTheShotStartsIsAFakeStop(Entry e)
    {
        foreach (int at in new[] { 1, 6, 12 })
        {
            var o = PlayShot(e, at, Vector2.Zero);
            Assert.Equal(ActionKind.FakeStop, o.Kind);
            Assert.DoesNotContain(o.Events, x => x.E.Kind == EventKind.Strike);
            Assert.False(o.Lost);
            Assert.True(o.InPossessionAtEnd);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void CancelWithTheLeftStickHeldIsAFakeCutThatWay(int side)
    {
        var o = PlayShot(Entry.Jog, 6, new Vector2(0.5f, side * 0.87f));
        Assert.Equal(ActionKind.FakeCut, o.Kind);
        Assert.Equal(side < 0 ? SkillSide.Left : SkillSide.Right, o.Move!.Side);
        Assert.DoesNotContain(o.Events, x => x.E.Kind == EventKind.Strike);
        Assert.True(o.Move.BallOutVelocity.Z * side > 0f, "the ball was not cut toward the stick");
        Assert.True(o.InPossessionAtEnd);
    }

    [Fact]
    public void AFakeDropsTheSoftTouchPlannedForTheShot()
    {
        var w = Field();
        var p = w.Attacker;
        var buf = new TickIntent[w.PlayerCount];
        BallAction? soft = null;
        for (int tick = 1; tick <= 400; tick++)
        {
            var it = Run(Entry.Jog, tick);
            int i = tick - 241;
            if (i is >= 0 and < 15) { it.ShootHeld = true; it.Cancel = i is >= 6 and < 8; }
            buf[0] = it;
            w.Step(buf);
            if (i == 5) soft = p.TouchAction is { Pending: true, Kind: ActionKind.Soft } a ? a : null;
            if (i == 6 && soft != null) Assert.NotEqual(soft, p.TouchAction is { Pending: true } q ? q : null);
        }
        Assert.True(p.InPossession);
    }

    [Fact]
    public void CancelLaterThanTheWindowLeavesTheShotAlone()
    {
        var o = PlayShot(Entry.Jog, 13, Vector2.Zero);
        Assert.Null(o.Kind);
        Assert.Contains(o.Events, x => x.E.Kind == EventKind.Strike);
    }

    [Fact]
    public void AReleasedShotCanStillBeFakedInsideTheWindow()
    {
        // Shoot tapped for 3 ticks (released, waiting out the strike delay), cancel at 8 ticks: no strike.
        var o = PlayShot(Entry.Jog, 8, Vector2.Zero, shootTicks: 3);
        Assert.Equal(ActionKind.FakeStop, o.Kind);
        Assert.DoesNotContain(o.Events, x => x.E.Kind == EventKind.Strike);
    }

    // ---- ordinary dribbling never triggers ----

    public static IEnumerable<object[]> OrdinaryTraces() => Enumerable.Range(0, 10).Select(i => new object[] { i });

    static TickIntent Dir(float deg, float sprint = 0f, bool close = false)
    {
        float a = deg * MathF.PI / 180f;
        return new TickIntent { MoveX = MathF.Cos(a), MoveZ = MathF.Sin(a), Sprint = sprint, CloseControl = close };
    }

    static TickIntent Ordinary(int trace, int tick, Random rng)
    {
        float s = tick / 120f;
        TickIntent Noise(TickIntent it)
        {
            var v = new Vector2((float)rng.NextDouble() * 0.49f, 0f);
            float a = (float)rng.NextDouble() * MathF.PI * 2f;
            it.RightX = v.X * MathF.Cos(a); it.RightZ = v.X * MathF.Sin(a);
            return it;
        }
        return trace switch
        {
            0 => Dir(0f),                                                          // straight jog
            1 => Dir(((int)(s / 0.8f)) % 2 == 0 ? 45f : -45f),                     // zig-zag
            2 => s < 3f ? Dir(0f, 1f) : Dir(90f, 1f),                              // sprint, 90 degree turn
            3 => s < 4f ? Dir(0f, 1f) : default,                                   // sprint, stop
            4 => Dir(0f, 1f),                                                      // straight sprint
            5 => Noise(Dir(0f)),                                                   // jog with right-stick noise below 0.5
            6 => Right(Dir(((int)(s / 0.8f)) % 2 == 0 ? 30f : -30f, 1f), new Vector2(0.45f * MathF.Cos(s), 0.45f * MathF.Sin(s))), // sprint zig-zag, slow drift
            7 => Right(Dir(0f), s % 2f < 1f ? new Vector2(1f, 0f) : (s % 2f) < 1f + 3f / 120f ? new Vector2(-0.3f, 0f) : Vector2.Zero), // forward push, spring back (jog)
            8 => Noise(Dir(((int)(s / 0.8f)) % 2 == 0 ? 45f : -45f, 0f, true)),    // close-control zig-zag with noise
            _ => Noise(s < 3f ? Dir(0f, 1f) : Dir(180f, 1f)),                      // sprint, reverse, noise
        };
    }

    [Theory]
    [MemberData(nameof(OrdinaryTraces))]
    public void OrdinaryDribblingNeverTriggersAMove(int trace)
    {
        var w = Field();
        var p = w.Attacker;
        var rng = new Random(trace + 1);
        var buf = new TickIntent[w.PlayerCount];
        for (int tick = 1; tick <= 1200 && w.State == RoundState.Live; tick++)
        {
            buf[0] = Ordinary(trace, tick, rng);
            w.Step(buf);
            Assert.DoesNotContain(w.Events, e => e.Kind == EventKind.SkillMove);
            Assert.Equal(Gesture.None, p.LastGesture);
            Assert.False(p.Action is { Kind: ActionKind.KnockOn } || p.PreviousAction is { Kind: ActionKind.KnockOn });
        }
        Assert.True(p.InPossession);
    }

    public static IEnumerable<object[]> IgnoredTraces() => new[]
    {
        new object[] { "rs_back" }, new object[] { "elastico" }, new object[] { "heel_to_heel" }, new object[] { "spin" },
    };

    /// <summary>
    /// Right-stick traces that make no move: back alone, and the recognised but not built elastico, heel-to-heel and spin
    /// (right side), for a player running along +X.
    /// </summary>
    static Vector2 IgnoredStick(string trace, int i) => trace switch
    {
        "rs_back" => i < 10 ? new Vector2(-1f, 0f) : Vector2.Zero,
        "elastico" => i < 6 ? new Vector2(-1f, 0f) : i < 12 ? new Vector2(0f, 1f) : Vector2.Zero,
        "heel_to_heel" => i < 5 ? new Vector2(1f, 0f) : i < 9 ? Vector2.Zero : i < 14 ? new Vector2(-1f, 0f) : Vector2.Zero,
        _ => i < 5 ? new Vector2(1f, 0f) : i < 9 ? Vector2.Zero : i < 13 ? new Vector2(0f, 1f) : Vector2.Zero,
    };

    [Theory]
    [MemberData(nameof(IgnoredTraces))]
    public void TheRightStickBackAloneAndTheMovesNotBuiltChangeNothing(string trace)
    {
        foreach (var e in new[] { Entry.Standing, Entry.Jog, Entry.Sprint })
        {
            // The same run with and without the trace: no move, no knock-on, the same player and ball on every tick.
            var a = Field(2);
            var b = Field(2);
            var buf = new TickIntent[a.PlayerCount];
            for (int tick = 1; tick <= 600; tick++)
            {
                var it = Run(e, tick);
                buf[0] = it;
                a.Step(buf);
                int i = tick - 241;
                if (i >= 0) { var r = IgnoredStick(trace, i); it.RightX = r.X; it.RightZ = r.Y; }
                buf[0] = it;
                b.Step(buf);
                Assert.DoesNotContain(b.Events, x => x.Kind == EventKind.SkillMove);
                Assert.Equal(a.Attacker.Position, b.Attacker.Position);
                Assert.Equal(a.Ball.Position, b.Ball.Position);
            }
            var expect = trace switch { "rs_back" => Gesture.None, "elastico" => Gesture.Elastico, "heel_to_heel" => Gesture.HeelToHeel, _ => Gesture.SpinAndGo };
            Assert.Equal(expect, b.Attacker.LastGesture);
        }
    }

    [Fact]
    public void ASprintingForwardFlickIsAKnockOnUnlessAHeelToHeelFollows()
    {
        bool Knocked(Func<int, Vector2> stick)
        {
            var w = Field(1);
            var p = w.Attacker;
            var buf = new TickIntent[w.PlayerCount];
            bool knocked = false;
            for (int tick = 1; tick <= 480; tick++)
            {
                var it = Run(Entry.Sprint, tick);
                int i = tick - 241;
                if (i >= 0) { var r = stick(i); it.RightX = r.X; it.RightZ = r.Y; }
                buf[0] = it;
                w.Step(buf);
                knocked |= p.Action is { Kind: ActionKind.KnockOn } || p.PreviousAction is { Kind: ActionKind.KnockOn };
            }
            return knocked;
        }
        Assert.True(Knocked(i => i < 6 ? new Vector2(1f, 0f) : Vector2.Zero));
        Assert.False(Knocked(i => IgnoredStick("heel_to_heel", i)));
        Assert.False(Knocked(i => IgnoredStick("spin", i)));
    }

    [Fact]
    public void WithTheRightStickAtRestTheWorldIsUnchanged()
    {
        // The skill inputs at rest, written explicitly, give the same round as the intents without them.
        var a = Field(5);
        var b = Field(5);
        var buf = new TickIntent[a.PlayerCount];
        for (int tick = 1; tick <= 900; tick++)
        {
            var it = Ordinary(1, tick, new Random(0));
            buf[0] = it;
            a.Step(buf);
            it.RightX = 0.3f; it.RightZ = -0.2f; // below the actuation
            buf[0] = it;
            b.Step(buf);
            Assert.Equal(a.Attacker.Position, b.Attacker.Position);
            Assert.Equal(a.Ball.Position, b.Ball.Position);
        }
    }

    // ---- determinism and replays ----

    static List<TickIntent> SkillTrace()
    {
        var list = new List<TickIntent>();
        for (int tick = 1; tick <= 1100; tick++)
        {
            var it = Run(Entry.Jog, tick);
            int i = tick - 241;
            if (i is >= 0 and < 40) it = StepOver(i, it, 1);
            i = tick - 480;
            if (i is >= 0 and < 40) it = Roll(i, it, -1);
            if (tick == 700) it.SkillKey = SkillKey.DragBack;
            if (tick is >= 900 and < 940) { it.ShootHeld = true; it.Cancel = tick is >= 905 and < 908; }
            list.Add(it);
        }
        return list;
    }

    [Fact]
    public void TheSameSkillInputsGiveTheSameRoundTwice()
    {
        string Run1()
        {
            var w = Field(3);
            var buf = new TickIntent[w.PlayerCount];
            var sb = new System.Text.StringBuilder();
            foreach (var it in SkillTrace())
            {
                buf[0] = it;
                w.Step(buf);
                sb.Append(w.Attacker.Position).Append(w.Ball.Position).Append(w.Attacker.Action?.Kind).Append(';');
            }
            return sb.ToString();
        }
        Assert.Equal(Run1(), Run1());
    }

    [Fact]
    public void ReplaysRecordTheSkillInputsAndPlayThemBack()
    {
        var t = TestData.Tuning();
        var sc = TestData.Scenario("halfway_run");
        // The keeper holds his line, so he does not come out and claim the ball before the trace has made all its moves.
        sc.KeeperHoldLine = true;
        sc.KeeperHoldLineThroughDribble = true;
        var att = TestData.Player("striker_a");
        var kee = TestData.Player("the_wall");
        var w = new World(t, sc, att, kee, 9);
        w.Recorder = new ReplayRecorder(w, att.Id, kee.Id, sc.Id);
        w.Begin();
        var buf = new TickIntent[w.PlayerCount];
        var moves = 0;
        foreach (var it in SkillTrace().Take(1000))
        {
            buf[0] = it;
            w.Step(buf);
            moves += w.Events.Count(e => e.Kind == EventKind.SkillMove);
            if (w.State == RoundState.Report) break;
        }
        var empty = default(TickIntent);
        for (int i = 0; i < 4000 && w.State != RoundState.Report; i++) { buf[0] = empty; w.Step(buf); }
        Assert.True(moves >= 2, $"only {moves} moves made");
        var json = w.Recorder.Replay.ToJson();
        Assert.Contains("\"rx\"", json);
        Assert.Contains("\"cancel\"", json);
        Assert.Contains("\"skill\": \"drag_back\"", json);
        var back = Replay.FromJson(json, out var err)!;
        Assert.Null(err);
        var original = w.Recorder.Replay;
        for (int i = 0; i < original.Intents.Count; i++)
        {
            var x = original.Intents[i]; var y = back.Intents[i];
            Assert.Equal((x.Rx, x.Rz, x.Cancel, x.Skill), (y.Rx, y.Rz, y.Cancel, y.Skill));
        }
        Assert.Equal(w.Report!.ToJson(), back.Run(t, sc, att, kee).ToJson());
    }

    [Fact]
    public void AnOldReplayWithoutTheSkillFieldsLoadsWithThemAtRest()
    {
        string path = Path.Combine(TestData.DataDir, "..", "game", "tests", "smoke_replay.json");
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("\"rx\"", text);
        var r = Replay.Load(path, out var err);
        Assert.Null(err);
        Assert.All(r!.Intents, i => Assert.True(i.Rx == 0f && i.Rz == 0f && !i.Cancel && i.Skill == SkillKey.None));
        // Written again, a replay without skill input has no skill fields: the file format is unchanged.
        Assert.DoesNotContain("\"rx\"", r.ToJson());
        Assert.DoesNotContain("\"skill\"", r.ToJson());
    }

    [Fact]
    public void AStateCopyCarriesTheSkillState()
    {
        var w = Field();
        var buf = new TickIntent[w.PlayerCount];
        var p = w.Attacker;
        foreach (var it in SkillTrace().Take(400)) { buf[0] = it; w.Step(buf); if (p.SkillMove != null) break; }
        Assert.NotNull(p.SkillMove);
        var c = TestData.Outfield(p.Sheet).CopyFrom(p);
        Assert.Same(p.SkillMove, c.SkillMove);
        Assert.Equal(p.LastGesture, c.LastGesture);
        Assert.Equal(p.LastGestureTick, c.LastGestureTick);
        Assert.Equal(p.SkillEffective, c.SkillEffective);
        Assert.Equal(p.GZone, c.GZone);
        Assert.Equal(p.GFwdLastTick, c.GFwdLastTick);
        Assert.Equal(p.SkillChainUntil, c.SkillChainUntil);
    }
}
