using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>Skill moves through the real world: on an open field (the keeper far away), from scripted stick traces.</summary>
public partial class SkillMoveTests
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

    /// <summary>
    /// One tick of a stick gesture that registers on trace tick <paramref name="at"/>: the right stick, with sprint let go of
    /// until then (no gesture registers with sprint held).
    /// </summary>
    static TickIntent Gest(TickIntent it, int i, int at, Vector2 r)
    {
        if (i <= at) it.Sprint = 0f;
        return Right(it, r);
    }

    // Traces for a player running along +X: forward is +X, his right is +Z.
    static TickIntent StepOver(int i, TickIntent it, int side) => Gest(it, i, 6, i < 6 ? new Vector2(1f, 0f) : i < 12 ? new Vector2(0f, side) : Vector2.Zero);
    static TickIntent Feint(int i, TickIntent it, int side) => Gest(it, i, 5, i < 5 ? new Vector2(0f, side) : Vector2.Zero);
    static TickIntent Roll(int i, TickIntent it, int side) => Gest(it, i, 20, i < 22 ? new Vector2(0f, side) : Vector2.Zero);
    /// <summary>The drag-back: the left stick back with both shoulder buttons held (the right stick back alone does nothing).</summary>
    static TickIntent Drag(int i, TickIntent it)
    {
        if (i >= 8) return it;
        it.MoveX = -1f; it.MoveZ = 0f; it.Finesse = true; it.Chip = true;
        if (i == 0) it.Sprint = 0f;
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
        "elastico" => ((i, it) => Elastico(i, it, side), ActionKind.Elastico),
        "spin" => ((i, it) => Spin(i, it, side), ActionKind.Spin),
        "mcgeady" => ((i, it) => McGeady(i, it, side), ActionKind.Spin),
        "heel_to_heel" => ((i, it) => HeelToHeel(i, it), ActionKind.HeelToHeel),
        _ => ((i, it) => Drag(i, it), ActionKind.DragBack),
    };

    // The two-contact moves: elastico (back, then straight on to the side), spin (forward flick, rest, the side) and heel-to-heel
    // (forward flick, rest, back).
    static TickIntent Elastico(int i, TickIntent it, int side) => Gest(it, i, 6, i < 6 ? new Vector2(-1f, 0f) : i < 12 ? new Vector2(0f, side) : Vector2.Zero);
    static TickIntent Spin(int i, TickIntent it, int side) => Gest(it, i, 11, i < 5 ? new Vector2(1f, 0f) : i < 9 ? Vector2.Zero : i < 13 ? new Vector2(0f, side) : Vector2.Zero);
    /// <summary>The McGeady spin: a back flick, rest, then the side he comes out to.</summary>
    static TickIntent McGeady(int i, TickIntent it, int side) => Gest(it, i, 11, i < 5 ? new Vector2(-1f, 0f) : i < 9 ? Vector2.Zero : i < 13 ? new Vector2(0f, side) : Vector2.Zero);
    static TickIntent HeelToHeel(int i, TickIntent it) => Gest(it, i, 11, i < 5 ? new Vector2(1f, 0f) : i < 9 ? Vector2.Zero : i < 14 ? new Vector2(-1f, 0f) : Vector2.Zero);

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
                // The sole moves are kept to a smooth speed (their reach's step eased in within balance_deceleration), so the sole
                // may land a little further off than the reach tolerance (0.30 m): at most 0.33 m (2026-11-19).
                Assert.InRange(m.ContactMiss, 0f, 0.33f);
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
        // (played from its move sheet, at a jog: the card's numbers)
        Assert.InRange(ballStart.X - w.Ball.Position.X, 0.82f, 0.98f);  // pulled back about 0.90 m along his run
        Assert.InRange(p.Position.X - w.Ball.Position.X, 0.15f, 0.35f); // to rest behind his hip line
        Assert.InRange(MathF.Abs(w.Ball.Position.Z - p.Position.Z), 0.05f, 0.22f); // and to the playing foot's side
        Assert.InRange(MathF.Abs(Movement.WrapAngle(p.Facing)) * 180f / MathF.PI, 85f, 95f); // he has opened a quarter turn: side-on to it
        Assert.True(playerStart.X - p.Position.X < 0.3f, "he ends near where the sole landed (a short run-on, then the hop back)");
        Assert.True(p.Speed < 0.3f, $"still moving at {p.Speed} m/s");
        Assert.True(new Vector2(w.Ball.Velocity.X, w.Ball.Velocity.Z).Length() < 0.01f, "the ball is left at rest");
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

    [Theory]
    [InlineData(Entry.Jog, -1)]
    [InlineData(Entry.Jog, 1)]
    [InlineData(Entry.Sprint, -1)]
    [InlineData(Entry.Sprint, 1)]
    public void BallRollAtARunIsABriskRollHeKeepsRunningThrough(Entry e, int side)
    {
        var w = Field();
        var p = w.Attacker;
        var t = TestData.Tuning();
        var buf = new TickIntent[w.PlayerCount];
        BallAction? a = null;
        float minRun = float.MaxValue, maxBrake = 0f, run0 = float.NaN, along = float.NaN, perp = float.NaN, ballSpeed = float.NaN, speed = float.NaN;
        float sideStart = float.NaN, sideEnd = float.NaN;
        bool lost = false;
        for (int tick = 1; tick <= 480 && (a == null || w.Tick < a.EndTick + 30); tick++)
        {
            int i = tick - 241;
            // The left stick stays forward throughout.
            buf[0] = i < 0 ? Run(e, tick) : Roll(i, Run(e, tick), side);
            w.Step(buf);
            if (a == null && w.Events.Any(ev => ev.Kind == EventKind.SkillMove)) { a = p.SkillMove!; run0 = p.RunSpeed; }
            lost |= w.Events.Any(ev => ev.Kind == EventKind.PossessionLost);
            if (a == null || w.Tick >= a.EndTick) continue;
            minRun = MathF.Min(minRun, p.RunSpeed);
            maxBrake = MathF.Max(maxBrake, (run0 - p.RunSpeed) / t.Dt);
            run0 = p.RunSpeed;
            if (w.Tick == a.ContactTick - 1) sideStart = w.Ball.Position.Z;
            if (w.Tick == a.EndTick - 1)
            {
                // The ball against him along his direction of travel as the move ends.
                var v = new Vector2(p.Velocity.X, p.Velocity.Z);
                var dir = Vector2.Normalize(v);
                var rel = new Vector2(w.Ball.Position.X - p.Position.X, w.Ball.Position.Z - p.Position.Z);
                along = Vector2.Dot(rel, dir);
                perp = MathF.Abs(dir.X * rel.Y - dir.Y * rel.X);
                ballSpeed = new Vector2(w.Ball.Velocity.X, w.Ball.Velocity.Z).Length();
                speed = v.Length();
                sideEnd = w.Ball.Position.Z;
            }
        }
        Assert.NotNull(a);
        Assert.Equal(ActionKind.BallRoll, a!.Kind);
        Assert.True(a.SkillEntry >= 2.5f, $"entry {a.SkillEntry} m/s");
        Assert.Equal(t.Skill.BallRollRunSeconds, (a.EndTick - a.ContactTick) * t.Dt, 3);
        Assert.True(minRun >= (e == Entry.Jog ? 0.7f : 0.6f) * a.SkillEntry, $"run fell to {minRun} m/s from {a.SkillEntry}");
        Assert.True(maxBrake <= t.Skill.BallRollBrake + 0.01f, $"braked at {maxBrake} m/s^2");
        Assert.InRange(along, 0.35f, 0.6f);
        Assert.True(perp <= 0.25f, $"ball {perp} m to the side of his travel");
        Assert.True(ballSpeed >= speed, $"ball {ballSpeed} m/s, player {speed} m/s");
        Assert.InRange((sideEnd - sideStart) * side, 0.6f, 1.1f);
        Assert.False(lost);
        Assert.True(p.InPossession);
    }

    [Theory]
    [InlineData(Entry.Jog, -1, 45f)]
    [InlineData(Entry.Jog, 1, 45f)]
    [InlineData(Entry.Sprint, -1, 45f)]
    [InlineData(Entry.Sprint, 1, 60f)]
    [InlineData(Entry.Jog, 1, -45f)]
    [InlineData(Entry.Sprint, -1, 0f)]
    public void BallRollAtARunReleasesTheBallTowardTheStickOnItsSide(Entry e, int side, float deg)
    {
        // The left stick turns toward the roll's side (deg > 0) 0.1 s before its end: the ball is released that way, so the exit
        // touch is an ordinary dribble touch (not a turn), he is running along the stick within 0.25 s and never brakes in a
        // step. Straight on or toward the other side the ball is released along the run as before.
        for (ulong seed = 1; seed <= 3; seed++)
        {
            var w = Field(seed);
            var p = w.Attacker;
            var t = w.Tuning;
            var buf = new TickIntent[w.PlayerCount];
            BallAction? a = null;
            float a0 = deg * side * MathF.PI / 180f;
            var stick = new Vector2(MathF.Cos(a0), MathF.Sin(a0));
            Vector2 release = default;
            ActionKind? exit = null;
            int onStickAt = -1, steps = 0;
            float prev = float.NaN;
            bool lost = false;
            for (int tick = 1; tick <= 480 && (a == null || w.Tick < a.EndTick + 60); tick++)
            {
                int i = tick - 241;
                var it = i >= 0 && i < 40 ? Roll(i, Run(e, tick), side) : Run(e, tick);
                if (a != null && tick >= a.EndTick - 12) { it.MoveX = stick.X; it.MoveZ = stick.Y; }
                buf[0] = it;
                w.Step(buf);
                if (a == null && w.Events.Any(ev => ev.Kind == EventKind.SkillMove)) a = p.SkillMove!;
                lost |= w.Events.Any(ev => ev.Kind == EventKind.PossessionLost);
                if (a == null) continue;
                var v = new Vector2(p.Velocity.X, p.Velocity.Z);
                if (w.Tick >= a.StartTick + 1 && w.Tick <= a.EndTick + 30 && !float.IsNaN(prev) && MathF.Abs(v.Length() - prev) > 15f * t.Dt) steps++;
                prev = v.Length();
                if (w.Tick == a.EndTick)
                {
                    // The exit touch is made at once on the end tick: the ball's travel just before it is the release.
                    exit = w.Events.Any(ev => ev.Kind == EventKind.Touch) ? p.TouchAction?.Kind : null;
                }
                if (w.Tick >= a.EndTick - 1 && onStickAt < 0
                    && MathF.Abs(MathF.Atan2(stick.X * v.Y - stick.Y * v.X, Vector2.Dot(stick, v))) * 180f / MathF.PI <= 15f)
                    onStickAt = w.Tick;
            }
            string at = $"{e} side {side} {deg} deg seed {seed}";
            Assert.NotNull(a);
            Assert.Equal(ActionKind.BallRoll, a!.Kind);
            if (deg > 0f)
            {
                Assert.True(exit is ActionKind.Jog or ActionKind.Sprint, $"{at}: exit touch {exit}");
                Assert.True(onStickAt >= 0 && (onStickAt - (a.EndTick - 1)) * t.Dt <= 0.25f + 1e-4f, $"{at}: on the stick at tick {onStickAt}, end {a.EndTick}");
                Assert.Equal(0, steps);
            }
            Assert.False(lost, $"{at}: lost the ball");
            Assert.True(p.InPossession, $"{at}: not in possession");
        }
    }

    [Fact]
    public void BallRollReleaseFollowsTheStickOnlyTowardItsSide()
    {
        var a = new BallAction { Kind = ActionKind.BallRoll, Side = SkillSide.Right, HoldDirection = new Vector2(1f, 0f), SkillEntry = 5f };
        var t = TestData.Tuning();
        float Deg(Vector2 d) => MathF.Atan2(d.Y, d.X) * 180f / MathF.PI;
        // His right is +Z: 45 degrees toward it is followed, 80 is held to 60, the other way and straight on keep the run.
        Assert.Equal(45f, Deg(SkillMoves.RollReleaseDir(a, new Vector2(MathF.Cos(MathF.PI / 4f), MathF.Sin(MathF.PI / 4f)), t)), 2);
        Assert.Equal(60f, Deg(SkillMoves.RollReleaseDir(a, new Vector2(MathF.Cos(1.4f), MathF.Sin(1.4f)), t)), 2);
        Assert.Equal(0f, Deg(SkillMoves.RollReleaseDir(a, new Vector2(1f, -1f), t)), 2);
        Assert.Equal(0f, Deg(SkillMoves.RollReleaseDir(a, new Vector2(1f, 0f), t)), 2);
        // From standing the run's line is kept.
        a.SkillEntry = 0.5f;
        Assert.Equal(0f, Deg(SkillMoves.RollReleaseDir(a, new Vector2(1f, 1f), t)), 2);
    }

    public static IEnumerable<object[]> FlowingMoves()
    {
        foreach (string move in new[] { "step_over", "body_feint", "elastico", "spin" })
        foreach (var e in new[] { Entry.Jog, Entry.Sprint })
        foreach (int side in new[] { -1, 1 })
            yield return new object[] { move, e, side };
    }

    /// <summary>The share of his entry pace a flowing move keeps at least: the step-over and body feint 85% at a jog (4 m/s) to 75% at a sprint (7.5 m/s).</summary>
    static float Keep(string move, float entry) => move switch
    {
        "elastico" => 0.7f,
        "spin" => 0.6f,
        _ => 0.85f - 0.10f * Math.Clamp((entry - 4f) / 3.5f, 0f, 1f),
    };

    [Theory]
    [MemberData(nameof(FlowingMoves))]
    public void FlowingMovesAtARunKeepHimMovingWithTheBallRollingOn(string move, Entry e, int side)
    {
        // The left stick stays forward throughout: he never drops below the move's share of his entry pace (the step-over and
        // body feint easing no harder than step_over_run_brake), the ball ends ahead of him along his travel rolling at least
        // as fast as he runs, and he is back to the share and running along the stick within 0.25 s of the end.
        var (trace, kind) = TraceFor(move, side);
        for (ulong seed = 1; seed <= 3; seed++)
        {
            var w = Field(seed);
            var p = w.Attacker;
            var t = w.Tuning;
            var buf = new TickIntent[w.PlayerCount];
            BallAction? a = null;
            float minSpeed = float.MaxValue, maxBrake = 0f, run0 = float.NaN, along = float.NaN, perp = float.NaN, ballSpeed = float.NaN, speed = float.NaN;
            int backAt = -1, onStickAt = -1;
            bool lost = false;
            for (int tick = 1; tick <= 480 && (a == null || w.Tick < a.EndTick + 40); tick++)
            {
                int i = tick - 241;
                buf[0] = i >= 0 && i < 40 ? trace(i, Run(e, tick)) : Run(e, tick);
                w.Step(buf);
                if (a == null && w.Events.Any(ev => ev.Kind == EventKind.SkillMove)) { a = p.SkillMove!; run0 = p.RunSpeed; }
                lost |= w.Events.Any(ev => ev.Kind == EventKind.PossessionLost);
                if (a == null) continue;
                var v = new Vector2(p.Velocity.X, p.Velocity.Z);
                float share = Keep(move, a.SkillEntry);
                if (w.Tick < a.EndTick)
                {
                    minSpeed = MathF.Min(minSpeed, v.Length() / a.SkillEntry);
                    maxBrake = MathF.Max(maxBrake, (run0 - p.RunSpeed) / t.Dt);
                    run0 = p.RunSpeed;
                }
                if (w.Tick == a.EndTick - 1)
                {
                    var dir = Vector2.Normalize(v);
                    var rel = new Vector2(w.Ball.Position.X - p.Position.X, w.Ball.Position.Z - p.Position.Z);
                    along = Vector2.Dot(rel, dir);
                    perp = MathF.Abs(dir.X * rel.Y - dir.Y * rel.X);
                    speed = v.Length();
                }
                // Released on the end tick.
                if (w.Tick == a.EndTick) ballSpeed = new Vector2(w.Ball.Velocity.X, w.Ball.Velocity.Z).Length();
                if (w.Tick >= a.EndTick - 1)
                {
                    if (backAt < 0 && v.Length() >= share * a.SkillEntry) backAt = w.Tick;
                    if (onStickAt < 0 && MathF.Abs(MathF.Atan2(v.Y, v.X)) * 180f / MathF.PI <= 15f) onStickAt = w.Tick;
                }
            }
            string at = $"{move} {e} side {side} seed {seed}";
            Assert.NotNull(a);
            Assert.Equal(kind, a!.Kind);
            float keep = Keep(move, a.SkillEntry);
            Assert.True(a.SkillEntry >= 2.5f, $"{at}: entry {a.SkillEntry} m/s");
            Assert.True(minSpeed >= keep, $"{at}: ground speed fell to {minSpeed:F2} of the entry {a.SkillEntry:F2} m/s");
            if (move is "step_over" or "body_feint")
                Assert.True(maxBrake <= t.Skill.StepOverRunBrake + 0.01f, $"{at}: braked at {maxBrake:F1} m/s^2");
            if (move is "step_over" or "body_feint")
            {
                Assert.InRange(along, 0.35f, 0.6f);
                Assert.True(perp <= 0.25f, $"{at}: ball {perp:F2} m to the side of his travel");
                Assert.True(onStickAt >= 0 && (onStickAt - (a.EndTick - 1)) * t.Dt <= 0.25f + 1e-4f, $"{at}: on the stick at tick {onStickAt}, end {a.EndTick}");
            }
            else
            {
                // The elastico's inside plays the ball across in front of him toward its side (its exit touch), the spin leaves
                // it in front of his facing: ahead of him.
                Assert.True(along > 0f, $"{at}: the ball {along:F2} m ahead of him");
            }
            Assert.True(ballSpeed >= speed, $"{at}: ball {ballSpeed:F2} m/s, player {speed:F2} m/s");
            Assert.True(backAt >= 0 && (backAt - (a.EndTick - 1)) * t.Dt <= 0.25f + 1e-4f, $"{at}: back to {keep} of the entry at tick {backAt}, end {a.EndTick}");
            Assert.False(lost, $"{at}: lost the ball");
            Assert.True(p.InPossession, $"{at}: not in possession");
        }
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
    [InlineData("elastico")]
    [InlineData("spin")]
    [InlineData("heel_to_heel")]
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
        // Body feints right at 2.0 s, 2.7 s and 3.4 s (chained; each after the last one's cooldown, or it would be dropped),
        // then one at 6.5 s after the 2 s window has lapsed.
        var at = new[] { 240, 324, 408, 780 };
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
    public void AMovesCooldownHoldsTheNextMoveBack()
    {
        // A body feint to the right at 2 s, then feints to the left asked for over and over: the next one starts only once the
        // first one's cooldown has run.
        (int End, int Next) Feints(float cooldown)
        {
            var t = TestData.Tuning();
            t.Skill.BodyFeintCooldown = cooldown;
            t.Skill.CooldownScale = new RatingRange(1f, 1f);
            var w = Field(t: t);
            var p = w.Attacker;
            var buf = new TickIntent[w.PlayerCount];
            int end = -1, next = -1;
            for (int tick = 1; tick <= 700 && next < 0; tick++)
            {
                var it = Run(Entry.Standing, tick);
                int i = tick - 241;
                if (i is >= 0 and < 4) it = Right(it, new Vector2(0f, 1f));
                else if (i >= 16 && i % 16 < 4) it = Right(it, new Vector2(0f, -1f));
                buf[0] = it;
                w.Step(buf);
                if (!w.Events.Any(e => e.Kind == EventKind.SkillMove)) continue;
                Assert.Equal(ActionKind.BodyFeint, p.SkillMove!.Kind);
                if (end < 0) end = p.SkillMove.EndTick; else next = w.Tick;
            }
            Assert.True(end > 0 && next > 0, $"end {end}, next {next}");
            return (end, next);
        }
        var free = Feints(0f);
        var held = Feints(0.5f);
        Assert.Equal(free.End, held.End);
        Assert.True(free.Next - free.End < 30, $"without a cooldown the next feint starts {free.Next - free.End} ticks after the first ends");
        Assert.True(held.Next - held.End >= 59, $"with a 0.5 s cooldown the next feint starts {held.Next - held.End} ticks after the first ends");
    }

    [Fact]
    public void EveryMoveHasACooldownLongerForALowerSkill()
    {
        var t = TestData.Tuning();
        foreach (var kind in new[] { ActionKind.StepOver, ActionKind.BodyFeint, ActionKind.BallRoll, ActionKind.DragBack, ActionKind.FakeStop,
                     ActionKind.FakeCut, ActionKind.Elastico, ActionKind.HeelToHeel, ActionKind.Spin })
        {
            float top = SkillMoves.Cooldown(new BallAction { Kind = kind, SkillEffective = 99f }, t);
            float low = SkillMoves.Cooldown(new BallAction { Kind = kind, SkillEffective = 1f }, t);
            Assert.True(top > 0f, $"{kind}: no cooldown at the top rating");
            Assert.True(low > top, $"{kind}: {low} s at rating 1, {top} s at rating 99");
        }
        // The drag-back's at the top rating is what it was; the controller's turn at the feet keeps its own, whatever the skill.
        Assert.Equal(t.Skill.DragBackCooldown, SkillMoves.Cooldown(new BallAction { Kind = ActionKind.DragBack, SkillEffective = 99f }, t), 5);
        Assert.Equal(t.Skill.FeetTurnCooldown, SkillMoves.Cooldown(new BallAction { Kind = ActionKind.DragBack, FeetTurn = true, SkillEffective = 1f }, t), 5);
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
    public void AStraightMovesExitBurstGrowsWithTheExitAngle()
    {
        var t = TestData.Tuning();
        Vector2 Dir(float deg) => new(MathF.Cos(deg * MathF.PI / 180f), MathF.Sin(deg * MathF.PI / 180f));
        foreach (var kind in new[] { ActionKind.BallRoll, ActionKind.DragBack, ActionKind.FakeStop, ActionKind.FakeCut, ActionKind.HeelToHeel, ActionKind.Spin })
        {
            var a = new BallAction { Kind = kind, Side = SkillSide.Right, HoldDirection = new Vector2(1f, 0f) };
            float straight = SkillMoves.ExitScale(a, Dir(0f), t), half = SkillMoves.ExitScale(a, Dir(45f), t), square = SkillMoves.ExitScale(a, Dir(90f), t);
            Assert.Equal(straight, SkillMoves.ExitScale(a, Vector2.Zero, t), 5);   // the stick released: straight on
            Assert.True(half > straight + 0.1f && square > half + 0.1f, $"{kind}: {straight} at 0, {half} at 45, {square} at 90 degrees");
            Assert.True(square >= 1.4f * straight, $"{kind}: {square} square against {straight} straight on");
            Assert.Equal(square, SkillMoves.ExitScale(a, Dir(-90f), t), 5);          // either way
            Assert.InRange(SkillMoves.ExitScale(a, Dir(180f), t), straight, square);  // turning back: between the two
        }

        // Through the world: a ball roll to the right at a jog, then the stick straight on or square to the right as it ends. The
        // burst waiting for the exit touch is the larger for the square exit, by the curve's share.
        float Boost(float exitDeg)
        {
            var w = Field();
            var p = w.Attacker;
            var buf = new TickIntent[w.PlayerCount];
            int end = -1;
            float boost = float.NaN;
            for (int tick = 1; tick <= 500 && float.IsNaN(boost); tick++)
            {
                int i = tick - 241;
                var it = Run(Entry.Jog, tick);
                if (i is >= 0 and < 22) it = Roll(i, it, 1);
                if (end > 0 && tick >= end - 1) it = new TickIntent { MoveX = Dir(exitDeg).X, MoveZ = Dir(exitDeg).Y, Sprint = 1f };
                buf[0] = it;
                w.Step(buf);
                if (w.Events.Any(e => e.Kind == EventKind.SkillMove)) end = p.SkillMove!.EndTick;
                if (end > 0 && w.Tick == end) boost = p.SkillBoostFactor - 1f;
            }
            Assert.True(boost > 0f, $"no burst readied for an exit at {exitDeg} degrees");
            return boost;
        }
        float on = Boost(0f), across = Boost(90f);
        // Full-sim ratio tracks the straight_exit_burst curve to ~1%; per-kind touch profiles add minor sim variance, so assert to 1 dp rather than 2.
        Assert.Equal(t.Skill.StraightExitBurst.Evaluate(90f) / t.Skill.StraightExitBurst.Evaluate(0f), across / on, 1);
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
                // A body feint to the right from a standstill (the left stick pushed once it is under way), then off straight ahead.
                if (i >= 0) it = Right(new TickIntent { MoveX = end > 0 ? 1f : 0f }, i < 4 ? new Vector2(0f, 1f) : Vector2.Zero);
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

    // ---- elastico, spin, heel-to-heel ----

    public static IEnumerable<object[]> TwoContactMoves()
    {
        foreach (var e in new[] { Entry.Standing, Entry.Jog, Entry.Sprint })
        {
            foreach (int side in new[] { -1, 1 })
            {
                yield return new object[] { "elastico", e, side };
                yield return new object[] { "spin", e, side };
            }
            yield return new object[] { "heel_to_heel", e, 0 };
        }
    }

    /// <summary>Per tick of a two-contact move run: the player and ball, recorded from the move's start to its end (inclusive).</summary>
    sealed class Track
    {
        public BallAction Move = null!;
        public readonly Dictionary<int, (Vector3 P, Vector3 V, float Run, float Facing, Vector3 B)> At = new();
        public float Entry;
        public bool Lost, InPossessionAtEnd;
        public readonly List<(int Tick, Foot Foot)> Touches = new();
        /// <summary>The first touch after the second contact (-1: none) and whether it was a skill move's; the ball's velocity on the second contact.</summary>
        public int NextTouch = -1;
        public bool NextTouchSkill;
        public Vector3 BallV2;
    }

    static Track PlayTwoContact(string move, Entry e, int side, ulong seed)
    {
        var (trace, _) = TraceFor(move, side);
        var w = Field(seed);
        var p = w.Attacker;
        var tr = new Track();
        var buf = new TickIntent[w.PlayerCount];
        bool gained = false;
        // The exit: toward the side at 45 degrees for the elastico (from the gesture's end: its second contact plays the ball on that
        // way), square to the side for the McGeady spin and straight on otherwise from the move's end, with sprint.
        float exitDeg = move == "elastico" ? side * 45f : move == "mcgeady" ? side * 90f : 0f;
        var exit = new TickIntent { MoveX = MathF.Cos(exitDeg * MathF.PI / 180f), MoveZ = MathF.Sin(exitDeg * MathF.PI / 180f), Sprint = 1f };
        int g0 = -1;
        for (int tick = 1; tick <= 900 && w.IsSimulating; tick++)
        {
            var it = Run(e, tick);
            // Jogging or sprinting, the gesture is made as the ball comes back to the foot.
            if (g0 < 0 && tick > 240 && (e == Entry.Standing || p.ActionContactEta is > 0.05f and < 0.2f || tick > 360)) g0 = tick - 1;
            int i = g0 < 0 ? -1 : tick - 1 - g0;
            if (i >= 0 && i < 20) it = trace(i, it);
            else if (tr.Move != null && (w.Tick >= tr.Move.EndTick || move == "elastico" && i >= 20)) it = exit;
            float run0 = p.RunSpeed;
            buf[0] = it;
            w.Step(buf);
            foreach (var ev in w.Events)
            {
                if (ev.Team != 0 || ev.PlayerIndex != 0) continue;
                if (ev.Kind == EventKind.PossessionGained) gained = true;
                if (ev.Kind == EventKind.PossessionLost && gained) tr.Lost = true;
                if (ev.Kind == EventKind.SkillMove && tr.Move == null) { tr.Move = p.SkillMove!; tr.Entry = run0; }
                if (ev.Kind == EventKind.Touch && tr.Move != null && w.Tick < tr.Move.EndTick) tr.Touches.Add((w.Tick, ev.Foot));
                if (ev.Kind == EventKind.Touch && tr.Move != null && w.Tick > tr.Move.Contact2Tick && tr.NextTouch < 0)
                {
                    tr.NextTouch = w.Tick;
                    tr.NextTouchSkill = p.TouchAction is { IsSkill: true };
                }
            }
            if (tr.Move != null && w.Tick == tr.Move.Contact2Tick) tr.BallV2 = w.Ball.Velocity;
            if (tr.Move != null && w.Tick <= tr.Move.EndTick) tr.At[w.Tick] = (p.Position, p.Velocity, p.RunSpeed, p.Facing, w.Ball.Position);
            if (tr.Move != null && w.Tick > tr.Move.EndTick + 240) break;
        }
        tr.InPossessionAtEnd = p.InPossession;
        Assert.NotNull(tr.Move);
        return tr;
    }

    [Theory]
    [MemberData(nameof(TwoContactMoves))]
    public void TheTwoContactMovesAreMadeWithBothContactsOnTheirTicksAndKeepTheBall(string move, Entry e, int side)
    {
        for (ulong seed = 1; seed <= 3; seed++)
        {
            var tr = PlayTwoContact(move, e, side, seed);
            var m = tr.Move;
            var opposite = side < 0 ? Foot.Right : Foot.Left;
            switch (move)
            {
                case "elastico":
                    Assert.Equal(ActionKind.Elastico, m.Kind);
                    Assert.InRange(m.ContactTick - m.StartTick, 0, 12);          // reach at most 0.10 s
                    Assert.Equal(26, m.Contact2Tick - m.ContactTick);            // the inside's snap 0.22 s after the first contact
                    Assert.Equal(32, m.EndTick - m.ContactTick);                 // the release on the sheet's frame, 0.267 s
                    Assert.NotNull(m.Sheet);                                     // played from its move sheet
                    Assert.Equal(opposite, m.Foot); Assert.Equal(m.Foot, m.Foot2);
                    Assert.Equal(FootSurface.Outside, m.Surface); Assert.Equal(FootSurface.Inside, m.Surface2);
                    Assert.Equal(-1, m.LiftTick);
                    break;
                case "spin":
                    Assert.Equal(ActionKind.Spin, m.Kind);
                    Assert.InRange(m.ContactTick - m.StartTick, 0, 15);          // reach at most 0.12 s
                    Assert.Equal(36, m.Contact2Tick - m.ContactTick);            // the other sole at 0.30 s
                    Assert.Equal(72, m.EndTick - m.ContactTick);                 // the release at 0.60 s
                    Assert.InRange(m.LiftTick, m.ContactTick + 1, m.Contact2Tick - 1);
                    Assert.Equal(opposite, m.Foot); Assert.NotEqual(m.Foot, m.Foot2);
                    Assert.Equal(FootSurface.Sole, m.Surface); Assert.Equal(FootSurface.Sole, m.Surface2);
                    Assert.False(m.McGeady);
                    break;
                default:
                    Assert.Equal(ActionKind.HeelToHeel, m.Kind);
                    Assert.Equal(SkillSide.None, m.Side);
                    Assert.Equal(14, m.ContactTick - m.StartTick);               // the heel at 0.12 s
                    Assert.Equal(26, m.Contact2Tick - m.StartTick);              // the flick at 0.22 s
                    Assert.Equal(42, m.EndTick - m.StartTick);                   // 0.35 s
                    Assert.NotEqual(m.Foot, m.Foot2);
                    Assert.Equal(FootSurface.Heel, m.Surface); Assert.Equal(FootSurface.Inside, m.Surface2);
                    break;
            }
            if (move != "heel_to_heel") Assert.Equal(side < 0 ? SkillSide.Left : SkillSide.Right, m.Side);
            Assert.True(m.Made);
            // A touch on each contact, with its foot, and none in between.
            Assert.Equal(new[] { (m.ContactTick, m.Foot), (m.Contact2Tick, m.Foot2) }, tr.Touches.ToArray());
            Assert.False(tr.Lost, $"{move} {e} {side} seed {seed}: possession lost");
            Assert.True(tr.InPossessionAtEnd);
        }
    }

    [Theory]
    [MemberData(nameof(TwoContactMoves))]
    public void TheTwoContactMovesKeepTheForwardSpeedBoundAndNoSpeedStep(string move, Entry e, int side)
    {
        var t = TestData.Tuning();
        float budget = t.Movement.BalanceDeceleration * t.Dt;
        for (ulong seed = 1; seed <= 3; seed++)
        {
            var tr = PlayTwoContact(move, e, side, seed);
            var m = tr.Move;
            float prev = float.NaN, prevFwd = float.NaN, fwd0 = float.NaN;
            for (int tick = m.StartTick; tick < m.EndTick; tick++)
            {
                var s = tr.At[tick];
                float ground = new Vector2(s.V.X, s.V.Z).Length();
                // No change of the ground speed faster than 15 m/s^2 inside the move (a move played from its sheet: no faster than
                // the sheet itself changes his velocity, with the eases onto it).
                float step = m.Sheet != null ? MathF.Max(15f, SheetAccel(m.Sheet, t) + 2f * t.Movement.BalanceDeceleration) : 15f;
                if (!float.IsNaN(prev)) Assert.True(MathF.Abs(ground - prev) <= step * t.Dt + 1e-4f, $"{move} {e} seed {seed} tick +{tick - m.StartTick}: {prev:F2} -> {ground:F2} m/s");
                prev = ground;
                float fwd = s.V.X * m.RunDirection.X + s.V.Z * m.RunDirection.Y;
                if (tick == m.ContactTick) fwd0 = fwd;
                if (move == "heel_to_heel")
                {
                    // He keeps his speed.
                    Assert.InRange(s.Run, tr.Entry - 0.2f, tr.Entry + 0.2f);
                    continue;
                }
                if (move == "spin" && e != Entry.Standing)
                {
                    // At a run the spin's pace along the run eases from the entry pace no faster than spin_run_brake to
                    // spin_run_keep of it (from the start, the reach included): never below 60% of the entry.
                    float keepS = t.Skill.SpinRunKeep * tr.Entry;
                    Assert.True(fwd >= MathF.Max(keepS, tr.Entry - t.Skill.SpinRunBrake * t.Dt * (tick - m.StartTick + 1)) - 0.1f, $"{move} {e} tick +{tick - m.StartTick}: forward {fwd:F2} from {tr.Entry:F2}");
                    Assert.True(fwd >= 0.6f * tr.Entry, $"{move} {e} tick +{tick - m.StartTick}: forward {fwd:F2} from {tr.Entry:F2}");
                    continue;
                }
                if (move == "elastico" && m.Sheet != null)
                {
                    // Played from its sheet: the running speed is held at nothing (his ground velocity is the sheet's step), and
                    // at a run he keeps 0.80 of his pace or more.
                    Assert.Equal(0f, s.Run);
                    if (e != Entry.Standing) Assert.True(ground >= 0.80f * tr.Entry - 0.05f, $"{move} {e} tick +{tick - m.StartTick}: {ground:F2} m/s from {tr.Entry:F2}");
                    continue;
                }
                if (move == "elastico" && e != Entry.Standing)
                {
                    // At a run the elastico's run eases from the entry pace no faster than elastico_run_brake to
                    // elastico_run_keep of it (from the start, the reach included): never below 70% of the entry.
                    float keep = t.Skill.ElasticoRunKeep * tr.Entry;
                    Assert.InRange(s.Run, MathF.Max(keep, tr.Entry - t.Skill.ElasticoRunBrake * t.Dt * (tick - m.StartTick + 1)) - 0.05f, tr.Entry + 0.05f);
                    Assert.True(s.Run >= 0.7f * tr.Entry, $"{move} {e} tick +{tick - m.StartTick}: run {s.Run:F2} from {tr.Entry:F2}");
                    continue;
                }
                // The elastico's and spin's run eases down to 2.0 m/s no faster than balance_deceleration (from the start, the
                // reach included), and from the first contact on their forward speed (the run and the step along it) never rises
                // above 2.0 m/s and falls toward it as fast as the budget allows.
                Assert.True(s.Run <= MathF.Max(2.0f, tr.Entry - budget * (tick - m.StartTick)) + 0.05f, $"{move} {e} tick +{tick - m.StartTick}: run {s.Run:F2}");
                if (tick > m.ContactTick && prevFwd > 2.0f) Assert.True(fwd <= prevFwd + 1e-3f, $"{move} {e} tick +{tick - m.StartTick}: forward {prevFwd:F2} -> {fwd:F2}");
                if (tick > m.ContactTick) Assert.True(fwd <= MathF.Max(2.0f, fwd0 - 0.8f * budget * (tick - m.ContactTick)) + 0.05f, $"{move} {e} tick +{tick - m.StartTick}: forward {fwd:F2} from {fwd0:F2}");
                prevFwd = fwd;
            }
        }
    }

    [Theory]
    [InlineData(Entry.Standing, -1)]
    [InlineData(Entry.Standing, 1)]
    [InlineData(Entry.Jog, -1)]
    [InlineData(Entry.Sprint, 1)]
    public void TheElasticoPushesTheBallOutAndItsInsidePlaysItOnAsTheExitTouch(Entry e, int side)
    {
        var t = TestData.Tuning();
        var tr = PlayTwoContact("elastico", e, side, 1);
        var m = tr.Move;
        var f = m.RunDirection;
        var s = new Vector2(-f.Y, f.X) * side;
        float Lat(Vector3 v) => v.X * s.X + v.Z * s.Y;
        float Along(Vector3 v) => v.X * f.X + v.Z * f.Y;
        // (against him: the ball's place in his frame, which does not turn with his facing)
        Vector3 Rel(int tick) => tr.At[tick].B - tr.At[tick].P;
        var b0 = Rel(Math.Max(m.StartTick, m.ContactTick - 1));
        // The outside's push: about 0.50 m away from the side against him by 0.11 s, held there as the foot wraps round it.
        float push = Lat(Rel(m.ContactTick + 13) - b0), wrap = Lat(Rel(m.Contact2Tick - 1) - b0);
        Assert.True(push is > -0.58f and < -0.42f && wrap is > -0.58f and < -0.42f, $"push {push:F3} wrap {wrap:F3}");
        // His hips go with the push, then he drives back toward the side.
        float hips = Enumerable.Range(m.StartTick, m.Contact2Tick - m.StartTick).Min(k => Lat(tr.At[k].P - tr.At[m.StartTick].P));
        Assert.True(hips is > -0.40f and < -0.08f, $"hips {hips:F3}");
        // The inside plays it on toward the stick (45 degrees to the side, held inside the sheet's 40 to 75) at the second contact:
        // that is the exit, so no touch is made on the end tick; the next touch is an ordinary one, at least a windup after the second contact.
        float deg = MathF.Atan2(Lat(tr.BallV2), Along(tr.BallV2)) * 180f / MathF.PI;
        Assert.InRange(deg, 38f, 77f);
        Assert.True(new Vector2(tr.BallV2.X, tr.BallV2.Z).Length() > 1f, $"ball {tr.BallV2} after the second contact");
        Assert.True(tr.NextTouch > m.EndTick, $"next touch on {tr.NextTouch}, the move ends on {m.EndTick}");
        Assert.True(tr.NextTouch - m.Contact2Tick >= TouchPlanner.WindupTicks(t), $"next touch {tr.NextTouch - m.Contact2Tick} ticks after the second contact");
        Assert.False(tr.NextTouchSkill);
        Assert.False(tr.Lost);
        // He comes out of it driving toward the side.
        var end = tr.At[m.EndTick - 1];
        Assert.True(Lat(end.V) > 1.8f, $"sideways {Lat(end.V):F2} m/s at the end");
    }

    [Theory]
    [InlineData(Entry.Standing, -1)]
    [InlineData(Entry.Standing, 1)]
    [InlineData(Entry.Jog, -1)]
    [InlineData(Entry.Jog, 1)]
    [InlineData(Entry.Sprint, -1)]
    [InlineData(Entry.Sprint, 1)]
    public void TheMcGeadySpinTurnsAwayFromItsSideAndComesOutSquareToItWithTheBall(Entry e, int side)
    {
        var t = TestData.Tuning();
        var k = t.Skill;
        for (ulong seed = 1; seed <= 3; seed++)
        {
            var tr = PlayTwoContact("mcgeady", e, side, seed);
            var m = tr.Move;
            string at = $"mcgeady {e} {side} seed {seed}";
            // The spin's action, marked as the McGeady spin: the sole on the side he comes out to, then the other foot's outside.
            Assert.Equal(ActionKind.Spin, m.Kind);
            Assert.True(m.McGeady);
            Assert.Equal(side < 0 ? SkillSide.Left : SkillSide.Right, m.Side);
            Assert.Equal(side < 0 ? Foot.Left : Foot.Right, m.Foot);
            Assert.NotEqual(m.Foot, m.Foot2);
            Assert.Equal(FootSurface.Sole, m.Surface); Assert.Equal(FootSurface.Outside, m.Surface2);
            Assert.InRange(m.ContactTick - m.StartTick, 0, 15);                  // reach at most 0.12 s
            // 0.55 s from the first contact, longer at a run: his pace along the run is shed no faster than mcgeady_run_brake.
            int dur = m.EndTick - m.ContactTick;
            Assert.True(dur >= 66, $"{at}: {dur} ticks");
            if (e == Entry.Standing) Assert.Equal(66, dur);
            Assert.InRange(m.Contact2Tick, m.ContactTick + dur / 2, m.EndTick - 1);
            Assert.InRange(m.LiftTick, m.ContactTick + 1, m.Contact2Tick - 1);
            Assert.True(m.Made);
            Assert.Equal(new[] { (m.ContactTick, m.Foot), (m.Contact2Tick, m.Foot2) }, tr.Touches.ToArray());

            var f = m.RunDirection;
            var s = new Vector2(-f.Y, f.X) * side;
            float turned = 0f, prevGround = float.NaN, prevFwd = float.NaN;
            for (int tick = m.ContactTick; tick < m.EndTick; tick++)
            {
                var now = tr.At[tick];
                if (tick > m.ContactTick) turned += Movement.WrapAngle(now.Facing - tr.At[tick - 1].Facing) * 180f / MathF.PI * side;
                // The ball goes round with him, at his feet all the way.
                float d = new Vector2(now.B.X - now.P.X, now.B.Z - now.P.Z).Length();
                Assert.True(d < 0.75f, $"{at} tick +{tick - m.ContactTick}: ball {d:F2} m away");
                // No change of the ground speed faster than 15 m/s^2, and his pace along the old run only dies away.
                float ground = new Vector2(now.V.X, now.V.Z).Length(), fwd = now.V.X * f.X + now.V.Z * f.Y;
                if (!float.IsNaN(prevGround)) Assert.True(MathF.Abs(ground - prevGround) <= 15f * t.Dt + 1e-4f, $"{at} tick +{tick - m.ContactTick}: {prevGround:F2} -> {ground:F2} m/s");
                if (!float.IsNaN(prevFwd))
                {
                    Assert.True(MathF.Abs(fwd) <= MathF.Abs(prevFwd) + 1e-3f, $"{at} tick +{tick - m.ContactTick}: forward {prevFwd:F2} -> {fwd:F2}");
                    Assert.True(MathF.Abs(prevFwd - fwd) <= k.McGeadyRunBrake * t.Dt + 1e-3f, $"{at} tick +{tick - m.ContactTick}: forward {prevFwd:F2} -> {fwd:F2}");
                }
                prevGround = ground; prevFwd = fwd;
            }
            // Three quarters of a turn away from the side: he ends facing it ...
            Assert.InRange(turned, -272f, -266f);
            var end = tr.At[m.EndTick - 1];
            var face = new Vector2(MathF.Cos(end.Facing), MathF.Sin(end.Facing));
            Assert.True(Vector2.Dot(face, s) > 0.99f, $"{at}: facing {face} at the end, the side is {s}");
            // ... running that way (his pace along the old run gone), at the standing exit pace or a share of his entry pace ...
            float side1 = end.V.X * s.X + end.V.Z * s.Y, fwd1 = end.V.X * f.X + end.V.Z * f.Y;
            Assert.True(MathF.Abs(fwd1) < 0.15f, $"{at}: {fwd1:F2} m/s along the old run at the end");
            Assert.True(side1 >= (e == Entry.Standing ? k.McGeadyExitSpeed - 0.3f : 2.0f), $"{at}: {side1:F2} m/s toward the side at the end");
            // ... with the ball in front of him there, and to the side of where he took it.
            var rel = new Vector2(end.B.X - end.P.X, end.B.Z - end.P.Z);
            Assert.InRange(Vector2.Dot(rel, s), 0.2f, 0.5f);
            Assert.True(MathF.Abs(Vector2.Dot(rel, f)) < 0.1f, $"{at}: ball {Vector2.Dot(rel, f):F2} m along the old run from him at the end");
            var b0 = tr.At[m.ContactTick].B;
            Assert.True((end.B.X - b0.X) * s.X + (end.B.Z - b0.Z) * s.Y > 0.5f, $"{at}: the ball ended {(end.B.X - b0.X) * s.X + (end.B.Z - b0.Z) * s.Y:F2} m to the side");
            // The planner takes it on from there with an ordinary touch; the ball is his throughout.
            Assert.True(tr.NextTouch >= m.EndTick, $"{at}: next touch on {tr.NextTouch}, the move ends on {m.EndTick}");
            Assert.False(tr.NextTouchSkill);
            Assert.False(tr.Lost, $"{at}: possession lost");
            Assert.True(tr.InPossessionAtEnd);
        }
    }

    [Fact]
    public void TheMcGeadySpinsBurstIsLargestOutToItsSideAndItHasItsOwnCooldown()
    {
        var t = TestData.Tuning();
        Vector2 Dir(float deg) => new(MathF.Cos(deg * MathF.PI / 180f), MathF.Sin(deg * MathF.PI / 180f));
        var a = new BallAction { Kind = ActionKind.Spin, McGeady = true, Side = SkillSide.Right, HoldDirection = new Vector2(1f, 0f), SkillEffective = 99f };
        Assert.Equal(1f, SkillMoves.ExitScale(a, Dir(90f), t), 3);       // out to its side
        Assert.Equal(0.7f, SkillMoves.ExitScale(a, Dir(0f), t), 3);      // straight on after all
        Assert.Equal(0f, SkillMoves.ExitScale(a, Dir(-90f), t), 3);      // back the way he turned from
        t.Skill.McGeadyCooldown = 0.4f;
        Assert.Equal(0.4f, SkillMoves.Cooldown(a, t), 5);
        Assert.Equal(t.Skill.SpinCooldown, SkillMoves.Cooldown(new BallAction { Kind = ActionKind.Spin, SkillEffective = 99f }, t), 5);
    }

    [Theory]
    [InlineData(Entry.Standing, -1)]
    [InlineData(Entry.Standing, 1)]
    [InlineData(Entry.Jog, 1)]
    [InlineData(Entry.Sprint, -1)]
    public void TheSpinTurnsTheFacingThroughAFullCircleWithTheBallAtHisFeet(Entry e, int side)
    {
        var tr = PlayTwoContact("spin", e, side, 1);
        var m = tr.Move;
        var f = m.RunDirection;
        var s = new Vector2(-f.Y, f.X) * side;
        float turned = 0f, atContact2 = float.NaN;
        for (int tick = m.ContactTick + 1; tick <= m.EndTick - 1; tick++)
        {
            turned += Movement.WrapAngle(tr.At[tick].Facing - tr.At[tick - 1].Facing) * side;
            if (tick == m.Contact2Tick) atContact2 = turned;
            // The ball stays at his feet once the sole has it.
            if (tick >= m.ContactTick + 12) Assert.InRange(TouchModel.HorizontalDistance(tr.At[tick].P, tr.At[tick].B), 0f, 0.36f);
        }
        Assert.InRange(turned * 180f / MathF.PI, 358f, 362f);           // toward the side, a full circle
        Assert.InRange(atContact2 * 180f / MathF.PI, 170f, 190f);       // half way at the other sole's contact
        var end = tr.At[m.EndTick - 1];
        // Ahead of him at the release, he facing his original way.
        var face = new Vector2(MathF.Cos(end.Facing), MathF.Sin(end.Facing));
        Assert.True(Vector2.Dot(face, f) > 0.999f);
        Assert.InRange((end.B.X - end.P.X) * f.X + (end.B.Z - end.P.Z) * f.Y, 0.2f, 0.36f);
        // Displaced about 0.5 m toward the side of where the ball was first taken (and about 0.6 m ahead of it from standing).
        var b0 = tr.At[m.ContactTick].B;
        var d = new Vector2(end.P.X - b0.X, end.P.Z - b0.Z);
        Assert.InRange(Vector2.Dot(d, s), 0.4f, 0.6f);
        if (e == Entry.Standing) Assert.InRange(Vector2.Dot(d, f), 0.45f, 0.75f);
    }

    [Theory]
    [InlineData(Entry.Standing)]
    [InlineData(Entry.Jog)]
    [InlineData(Entry.Sprint)]
    public void TheHeelToHeelKnocksTheBallBackThenFlicksItOnAhead(Entry e)
    {
        var t = TestData.Tuning();
        var tr = PlayTwoContact("heel_to_heel", e, 0, 1);
        var m = tr.Move;
        var f = m.RunDirection;
        float Ahead(int tick) => (tr.At[tick].B.X - tr.At[tick].P.X) * f.X + (tr.At[tick].B.Z - tr.At[tick].P.Z) * f.Y;
        // Back about 0.15 m against the trailing foot (relative to him) by the flick.
        Assert.InRange(Ahead(m.Contact2Tick - 1) - Ahead(m.ContactTick), -0.2f, -0.1f);
        var v = tr.At[m.Contact2Tick + 1].B - tr.At[m.Contact2Tick].B;
        float speed = new Vector2(v.X, v.Z).Length() / t.Dt;
        if (e == Entry.Standing)
        {
            // The flick: straight on at about 1.3 times his speed (5 m/s from standing), never more than he can catch.
            float want = MathF.Max(5f, 1.3f * tr.At[m.Contact2Tick].Run);
            Assert.InRange(speed, MathF.Min(want, 7f) - 0.3f, want + 0.3f);
        }
        else
        {
            // At a run a short flick he runs onto: a little faster than he runs (see the next test).
            float run = tr.At[m.Contact2Tick].Run;
            Assert.InRange(speed, run, run + 1.5f);
        }
        Assert.True(Ahead(m.EndTick) > Ahead(m.Contact2Tick), "the ball did not run on ahead of him");
    }

    [Theory]
    [InlineData(Entry.Jog)]
    [InlineData(Entry.Sprint)]
    public void AtARunTheHeelToHeelIsAShortFlickHeRunsOnto(Entry e)
    {
        // The ball is never more than 0.8 m ahead of him until the next touch, which comes within 0.5 s of the move's end, and
        // he leaves the move at no less than his entry pace.
        var (trace, _) = TraceFor("heel_to_heel", 0);
        for (ulong seed = 1; seed <= 4; seed++)
        {
            var w = Field(seed);
            var p = w.Attacker;
            var t = w.Tuning;
            var buf = new TickIntent[w.PlayerCount];
            BallAction? a = null;
            float aheadMax = float.MinValue, endSpeed = float.NaN;
            int next = -1;
            bool lost = false;
            for (int tick = 1; tick <= 600 && (a == null || (next < 0 && w.Tick < a.EndTick + 240)); tick++)
            {
                int i = tick - 241;
                buf[0] = i >= 0 && i < 40 ? trace(i, Run(e, tick)) : Run(e, tick);
                w.Step(buf);
                if (a == null && w.Events.Any(ev => ev.Kind == EventKind.SkillMove)) a = p.SkillMove!;
                lost |= w.Events.Any(ev => ev.Kind == EventKind.PossessionLost);
                if (a == null) continue;
                var f = a.RunDirection;
                aheadMax = MathF.Max(aheadMax, (w.Ball.Position.X - p.Position.X) * f.X + (w.Ball.Position.Z - p.Position.Z) * f.Y);
                if (w.Tick == a.EndTick - 1) endSpeed = new Vector2(p.Velocity.X, p.Velocity.Z).Length();
                if (w.Tick >= a.EndTick && w.Events.Any(ev => ev.Kind == EventKind.Touch)) next = w.Tick;
            }
            string at = $"{e} seed {seed}";
            Assert.NotNull(a);
            Assert.Equal(ActionKind.HeelToHeel, a!.Kind);
            Assert.True(aheadMax <= 0.8f, $"{at}: the ball went {aheadMax:F2} m ahead");
            Assert.True(next >= 0 && (next - a.EndTick) * t.Dt <= 0.5f + 1e-4f, $"{at}: next touch at tick {next}, end {a.EndTick}");
            Assert.True(endSpeed >= a.SkillEntry - 0.01f, $"{at}: left the move at {endSpeed:F2} m/s from {a.SkillEntry:F2}");
            Assert.False(lost, $"{at}: lost the ball");
            Assert.True(p.InPossession, $"{at}: not in possession");
        }
    }

    [Fact]
    public void ThroughASpinTheKeeperReadsTheBallAsAtHisFoot()
    {
        var tr = PlayTwoContact("spin", Entry.Jog, 1, 1);
        // Re-run to the middle of the spin and read the keeper's situation there.
        var (trace, _) = TraceFor("spin", 1);
        var w = Field(1);
        var p = w.Attacker;
        var buf = new TickIntent[w.PlayerCount];
        int g0 = -1;
        for (int tick = 1; tick <= tr.Move.Contact2Tick; tick++)
        {
            var it = Run(Entry.Jog, tick);
            if (g0 < 0 && tick > 240 && (p.ActionContactEta is > 0.05f and < 0.2f || tick > 360)) g0 = tick - 1;
            int i = g0 < 0 ? -1 : tick - 1 - g0;
            if (i >= 0 && i < 20) it = trace(i, it);
            buf[0] = it;
            w.Step(buf);
            if (p.SkillMove is { Kind: ActionKind.Spin, Made: true } sp && w.Tick > sp.ContactTick)
            {
                var s = w.KeeperSituation();
                Assert.Equal(0f, s.AttackerNextTouch);
                Assert.Equal(sp.StartTick, s.AttackerTouchTick);
            }
        }
        Assert.Equal(tr.Move.StartTick, p.SkillMove!.StartTick);
    }

    // ---- drag-back over the turn at the feet ----

    /// <summary>
    /// Close control along +X, then the left stick reversed (the controller's turn at the feet starts) and, <paramref name="after"/>
    /// ticks after that turn began, both shoulder buttons with the stick held back. Returns the turn's start tick and the move under
    /// way 0.1 s after the shoulders.
    /// </summary>
    static (int TurnStart, BallAction? Move, bool Kept) DragOverFeetTurn(ulong seed, int after)
    {
        var w = Field(seed);
        var p = w.Attacker;
        var buf = new TickIntent[w.PlayerCount];
        int turn = -1, shoulders = -1;
        BallAction? move = null;
        for (int tick = 1; tick <= 900; tick++)
        {
            var it = new TickIntent { MoveX = 1f, CloseControl = true };
            if (tick > 240) it = new TickIntent { MoveX = -1f, CloseControl = true };
            if (shoulders >= 0 && tick >= shoulders) { it.Finesse = true; it.Chip = true; }
            buf[0] = it;
            w.Step(buf);
            if (turn < 0 && p.SkillMove is { FeetTurn: true } ft) { turn = ft.StartTick; shoulders = turn + after; }
            if (shoulders >= 0 && w.Tick == shoulders + 12) move = p.SkillMove;
            if (turn < 0 && tick > 300) break;
        }
        return (turn, move, p.InPossession);
    }

    [Fact]
    public void ADragBackJustAfterTheTurnAtTheFeetReplacesIt()
    {
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var (turn, move, kept) = DragOverFeetTurn(seed, 6);
            Assert.True(turn > 0, $"seed {seed}: no turn at the feet");
            Assert.True(move is { Kind: ActionKind.DragBack, FeetTurn: false }, $"seed {seed}: the drag-back did not replace the turn ({move?.Kind}, feet turn {move?.FeetTurn})");
            Assert.Equal(turn + 6, move!.StartTick);
            Assert.True(kept);
        }
    }

    [Fact]
    public void ADragBackLateInTheTurnAtTheFeetWaitsForIt()
    {
        var (turn, move, _) = DragOverFeetTurn(1, 20);
        Assert.True(turn > 0);
        Assert.True(move == null || move.StartTick != turn + 20, "a drag-back 0.17 s into the turn at the feet replaced it");
    }

    [Theory]
    [InlineData("drag_back", Entry.Jog, 0)]
    [InlineData("drag_back", Entry.Sprint, 0)]
    [InlineData("ball_roll", Entry.Jog, 1)]
    [InlineData("step_over", Entry.Jog, -1)]
    public void AGestureHeldBackForACommittedTouchIsPublishedAsTheMoveItWillStart(string move, Entry e, int side)
    {
        // The gesture comes while a touch is committed (its contact at least 4 ticks off): from the tick it is read until the tick
        // before the move starts the core publishes the move it will start, and the move then made starts on the published tick
        // with the published kind, foot, surface and side, the ball where the forecast had it as the move began (the forecast of the
        // tick before; a sole's reach then moves the contact on, which the forecast leaves out).
        var (trace, kind) = TraceFor(move, side);
        var w = Field();
        var p = w.Attacker;
        var buf = new TickIntent[w.PlayerCount];
        int g0 = -1, read = -1;
        BallAction? last = null;
        Vector3 ballBefore = default;
        for (int tick = 1; tick <= 900; tick++)
        {
            ballBefore = w.Ball.Position;
            if (g0 < 0 && tick > 240 && p.InPossession && p.TouchAction is { Pending: true } pt && pt.ContactTick - w.Tick >= 6) g0 = tick;
            var it = Run(e, tick);
            // (the ball roll by its key: its stick hold would outlast the committed touch)
            if (g0 >= 0 && tick - g0 < 20) { if (move != "ball_roll") it = trace(tick - g0, it); else if (tick == g0) { it.SkillKey = SkillKey.BallRoll; it.MoveZ = side * 0.5f; } }
            buf[0] = it;
            w.Step(buf);
            var pending = w.Attacker.PendingSkill;
            if (w.Events.Any(x => x.Kind == EventKind.SkillMove))
            {
                Assert.True(read >= 0, "the move started on the tick its gesture was read: nothing was held back");
                Assert.Null(pending);
                var m = p.SkillMove!;
                Assert.NotNull(last);
                Assert.Equal(kind, m.Kind);
                Assert.Equal(last!.Kind, m.Kind);
                Assert.Equal(last.StartTick, m.StartTick);
                Assert.Equal(w.Tick, m.StartTick);
                Assert.Equal(last.Foot, m.Foot);
                Assert.Equal(last.Surface, m.Surface);
                Assert.Equal(last.Side, m.Side);
                Assert.Equal(ballBefore.X, last.ContactPoint.X, 4);
                Assert.Equal(ballBefore.Z, last.ContactPoint.Z, 4);
                if (m.ContactTick == m.StartTick) { Assert.Equal(m.ContactPoint.X, last.ContactPoint.X, 4); Assert.Equal(m.ContactPoint.Z, last.ContactPoint.Z, 4); }
                return;
            }
            if (read < 0 && p.SkillBufTick >= 0)
            {
                read = p.SkillBufTick;
                Assert.Equal(w.Tick, read);
                Assert.True(p.TouchAction is { Pending: true }, "the gesture was read with no touch committed");
            }
            if (read >= 0)
            {
                Assert.NotNull(pending);
                Assert.Equal(kind, pending!.Kind);
                Assert.True(pending.StartTick > w.Tick);
                last = pending;
            }
            else Assert.Null(pending);
        }
        Assert.Fail("no move made");
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

    /// <summary>The tick the same shot, never cancelled, is struck on (the trace starts on tick 241).</summary>
    static int StrikeTick(Entry e, Vector2 left, int shootTicks = 30) =>
        PlayShot(e, 1000, left, shootTicks).Events.First(x => x.E.Kind == EventKind.Strike).Tick;

    [Theory]
    [InlineData(Entry.Standing)]
    [InlineData(Entry.Jog)]
    [InlineData(Entry.Sprint)]
    public void CancelAnyTimeBeforeTheStrikeIsAFakeStop(Entry e)
    {
        // From the press to 0.05 s (6 ticks) before the strike the shot would have made.
        int last = StrikeTick(e, Vector2.Zero) - 241 - 6;
        foreach (int at in new[] { 1, 6, 12, 18, 30, last })
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
        foreach (int at in new[] { 6, 18 })
        {
            var o = PlayShot(Entry.Jog, at, new Vector2(0.5f, side * 0.87f));
            Assert.Equal(ActionKind.FakeCut, o.Kind);
            Assert.Equal(side < 0 ? SkillSide.Left : SkillSide.Right, o.Move!.Side);
            Assert.DoesNotContain(o.Events, x => x.E.Kind == EventKind.Strike);
            Assert.True(o.Move.BallOutVelocity.Z * side > 0f, "the ball was not cut toward the stick");
            Assert.True(o.InPossessionAtEnd);
        }
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

    [Theory]
    [InlineData(Entry.Standing)]
    [InlineData(Entry.Jog)]
    public void CancelInTheLastTwentiethOfASecondLeavesTheShotAlone(Entry e)
    {
        int strike = StrikeTick(e, Vector2.Zero);
        var o = PlayShot(e, strike - 241 - 5, Vector2.Zero);
        Assert.Null(o.Kind);
        Assert.Equal(strike, o.Events.First(x => x.E.Kind == EventKind.Strike).Tick);
    }

    [Fact]
    public void AReleasedShotCanStillBeFakedBeforeTheStrike()
    {
        // Shoot tapped for 3 ticks (released, waiting out the strike delay), cancel at 8 and at 30 ticks: no strike.
        foreach (int at in new[] { 8, 30 })
        {
            var o = PlayShot(Entry.Jog, at, Vector2.Zero, shootTicks: 3);
            Assert.Equal(ActionKind.FakeStop, o.Kind);
            Assert.DoesNotContain(o.Events, x => x.E.Kind == EventKind.Strike);
        }
    }

    [Theory]
    [InlineData(Entry.Standing, 0, 6)]
    [InlineData(Entry.Jog, 0, 6)]
    [InlineData(Entry.Sprint, 0, 6)]
    [InlineData(Entry.Jog, 1, 6)]
    [InlineData(Entry.Jog, -1, 6)]
    [InlineData(Entry.Jog, 0, 18)]
    [InlineData(Entry.Sprint, 1, 18)]
    public void AFakesContactComesWhenTheStrikeWouldHave(Entry e, int side, int cancelAt)
    {
        // Shoot pressed on tick 241 (the trace's first tick): the fake's contact on the tick the same shot would have been struck
        // on, the fake stop's sole on the ball 0.15 s, the fake cut's 0.10 s, a touch raised at the contact.
        var left = side == 0 ? Vector2.Zero : new Vector2(0.5f, side * 0.87f);
        int strike = StrikeTick(e, left);
        var o = PlayShot(e, cancelAt, left);
        var m = o.Move!;
        Assert.Equal(side == 0 ? ActionKind.FakeStop : ActionKind.FakeCut, o.Kind);
        Assert.Equal(241, m.StartTick);
        Assert.Equal(strike, m.ContactTick);
        Assert.Equal(m.ContactTick, o.MoveStart);
        Assert.Equal(m.ContactTick, m.ExecuteTick);
        Assert.Equal(side == 0 ? 18 : 12, m.EndTick - m.ContactTick);
        Assert.True(m.Made);
        Assert.Contains(o.Events, x => x.E.Kind == EventKind.Touch && x.Tick == m.ContactTick);
        Assert.DoesNotContain(o.Events, x => x.E.Kind == EventKind.Strike);
        Assert.False(o.Lost);
        Assert.True(o.InPossessionAtEnd);
    }

    [Theory]
    [InlineData(Entry.Standing)]
    [InlineData(Entry.Jog)]
    public void ALateCancelsContactComesATenthOfASecondAfterIt(Entry e)
    {
        // Cancel 8 ticks before the strike: the fake's contact 12 ticks after the cancel, the shot never struck.
        int strike = StrikeTick(e, Vector2.Zero);
        int cancel = strike - 8;
        var o = PlayShot(e, cancel - 241, Vector2.Zero);
        Assert.Equal(ActionKind.FakeStop, o.Kind);
        Assert.Equal(cancel + 12, o.Move!.ContactTick);
        Assert.DoesNotContain(o.Events, x => x.E.Kind == EventKind.Strike);
        Assert.True(o.InPossessionAtEnd);
    }

    [Theory]
    [InlineData(Entry.Standing, 0)]
    [InlineData(Entry.Jog, 0)]
    [InlineData(Entry.Sprint, 0)]
    [InlineData(Entry.Jog, 1)]
    [InlineData(Entry.Sprint, -1)]
    public void TheKeeperCannotTellAFakeFromAShotBeforeItsContact(Entry e, int side)
    {
        // The same round with and without the cancel: until the fake's contact the shot winds up as a real one, so the keeper,
        // the attacker and the ball are the same on every tick and the shot action is published as a wind-up in both.
        var left = side == 0 ? Vector2.Zero : new Vector2(0.5f, side * 0.87f);
        TickIntent Trace(int i, TickIntent it, bool cancel)
        {
            if (i >= 1 && side != 0) { it.MoveX = left.X; it.MoveZ = left.Y; }
            it.ShootHeld = i < 15;
            it.Cancel = cancel && i is >= 6 and < 9;
            return it;
        }
        for (ulong seed = 1; seed <= 3; seed++)
        {
            var fake = Field(seed);
            var real = Field(seed);
            var buf = new TickIntent[fake.PlayerCount];
            int contact = -1;
            for (int tick = 1; tick <= 400 && contact < 0; tick++)
            {
                int i = tick - 241;
                var it = Run(e, tick);
                buf[0] = i >= 0 ? Trace(i, it, true) : it;
                fake.Step(buf);
                buf[0] = i >= 0 ? Trace(i, it, false) : it;
                real.Step(buf);
                if (fake.Events.Any(x => x.Kind == EventKind.SkillMove)) { contact = fake.Tick; break; }
                Assert.Equal(real.Keeper.Position, fake.Keeper.Position);
                Assert.Equal(real.Keeper.Velocity, fake.Keeper.Velocity);
                Assert.Equal(real.Keeper.Facing, fake.Keeper.Facing);
                Assert.Equal(real.Attacker.Position, fake.Attacker.Position);
                Assert.Equal(real.Ball.Position, fake.Ball.Position);
                if (i >= 0) Assert.True(fake.Attacker.Action is { IsShot: true, Made: false }, $"tick {tick}: no shot wind-up shown");
            }
            // From the contact the fake is seen: the shot is gone.
            Assert.False(fake.Attacker.Action is { IsShot: true });
            Assert.True(real.Attacker.Action is { IsShot: true });
            // The contact came on the tick the real shot is struck on.
            int strike = real.Events.Any(x => x.Kind == EventKind.Strike) ? real.Tick : -1;
            for (int tick = real.Tick + 1; tick <= 500 && strike < 0; tick++)
            {
                buf[0] = Run(e, tick);
                real.Step(buf);
                if (real.Events.Any(x => x.Kind == EventKind.Strike)) strike = real.Tick;
            }
            Assert.Equal(strike, contact);
        }
    }

    [Theory]
    [InlineData(Entry.Standing, 0)]
    [InlineData(Entry.Jog, 0)]
    [InlineData(Entry.Sprint, 0)]
    [InlineData(Entry.Jog, 1)]
    [InlineData(Entry.Sprint, -1)]
    public void AFakeIsPublishedFromTheCancelAsTheMoveItWillBe(Entry e, int side)
    {
        // From the tick the cancel is read to the tick before the fake's contact the core publishes the pending move; on the contact
        // tick the move made is that one (same start, foot, surface, side, contact and end) and the pending one is gone.
        var left = side == 0 ? Vector2.Zero : new Vector2(0.5f, side * 0.87f);
        var w = Field();
        var buf = new TickIntent[w.PlayerCount];
        BallAction? last = null;
        int first = -1;
        for (int tick = 1; tick <= 400; tick++)
        {
            int i = tick - 241;
            var it = Run(e, tick);
            if (i >= 0)
            {
                if (i >= 1 && side != 0) { it.MoveX = left.X; it.MoveZ = left.Y; }
                it.ShootHeld = i < 15;
                it.Cancel = i is >= 6 and < 9;
            }
            buf[0] = it;
            w.Step(buf);
            var pending = w.Attacker.PendingSkill;
            if (w.Events.Any(x => x.Kind == EventKind.SkillMove))
            {
                Assert.Null(pending);
                var m = w.Attacker.SkillMove!;
                Assert.NotNull(last);
                Assert.Equal(last!.Kind, m.Kind);
                Assert.Equal(last.Foot, m.Foot);
                Assert.Equal(last.Surface, m.Surface);
                Assert.Equal(last.Side, m.Side);
                Assert.Equal(last.StartTick, m.StartTick);
                Assert.Equal(last.ContactTick, m.ContactTick);
                Assert.Equal(last.EndTick, m.EndTick);
                Assert.Equal(241 + 6, first);
                return;
            }
            if (i < 6) Assert.Null(pending);
            else
            {
                Assert.NotNull(pending);
                Assert.True(w.Attacker.Action is { IsShot: true, Made: false });
                if (first < 0) first = tick;
                last = pending;
            }
        }
        Assert.Fail("no fake made");
    }

    [Theory]
    [InlineData("ball_roll", Entry.Jog, 1)]
    [InlineData("ball_roll", Entry.Standing, -1)]
    [InlineData("elastico", Entry.Jog, 1)]
    [InlineData("elastico", Entry.Sprint, -1)]
    [InlineData("elastico", Entry.Standing, 1)]
    [InlineData("spin", Entry.Jog, 1)]
    [InlineData("spin", Entry.Sprint, 1)]
    [InlineData("drag_back", Entry.Jog, 0)]
    public void TheExitTouchIsForecastAsItIsMade(string move, Entry e, int side)
    {
        // With the stick pushed on through the move's end: the exit touch forecast on the tick before the end is the touch the
        // controller takes on the end tick (its plan tick and foot); made at once, it is the one forecast (kind, surface, contact
        // tick) where the forecast put the ball. With none forecast then, no touch is made at once.
        var (trace, kind) = TraceFor(move, side);
        var w = Field();
        var buf = new TickIntent[w.PlayerCount];
        BallAction? forecast = null, move0 = null;
        ActionKind? made = null;
        for (int tick = 1; tick <= 240 + 80 + 120; tick++)
        {
            var it = Run(e, tick);
            int i = tick - 1 - 240;
            if (i >= 0 && i < 80) it = trace(i, it);
            if (i >= 0 && it.MoveX == 0f && it.MoveZ == 0f) it.MoveX = 1f;
            buf[0] = it;
            w.Step(buf);
            var a = w.Attacker;
            foreach (var ev in w.Events) if (ev.Kind == EventKind.SkillMove && made == null) { made = ev.Skill; move0 = a.SkillMove; }
            if (move0 == null) continue;
            if (w.Tick == move0.EndTick - 1) forecast = a.SkillExitTouch;
            if (w.Tick == move0.EndTick)
            {
                var t = a.TouchAction;
                bool immediate = t is { Made: true } && t.PlanTick == w.Tick && t.ContactTick == w.Tick && !t.IsSkill;
                if (forecast == null) { Assert.False(immediate, "an exit touch made at once with none forecast"); break; }
                // (the controller may commit the exit touch with a windup on the end tick instead: the same plan tick and foot)
                Assert.True(t is { PlanTick: var pt } && pt == forecast.PlanTick && !t.IsSkill, "no exit touch on the end tick where one was forecast");
                Assert.Equal(forecast.Foot, t!.Foot);
                if (!immediate) break;
                Assert.Equal(forecast.Kind, t.Kind);
                Assert.Equal(forecast.Surface, t.Surface);
                Assert.Equal(forecast.ContactTick, t.ContactTick);
                Assert.InRange(TouchModel.HorizontalDistance(forecast.ContactPoint, t.ContactPoint), 0f, 0.01f);
                break;
            }
        }
        Assert.Equal(kind, made);
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

    /// <summary>The right stick back alone (the start of an elastico) for a player running along +X: it makes no move.</summary>
    static Vector2 IgnoredStick(string trace, int i) => i < 10 ? new Vector2(-1f, 0f) : Vector2.Zero;

    [Theory]
    [InlineData("rs_back")]
    public void TheRightStickBackAloneChangesNothing(string trace)
    {
        // (with sprint held a right-stick flick arms a heavy touch, whichever way it points)
        foreach (var e in new[] { Entry.Standing, Entry.Jog })
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
            Assert.Equal(Gesture.None, b.Attacker.LastGesture);
        }
    }

    [Fact]
    public void ASprintingRightStickFlickIsAKnockOnWhereItPoints()
    {
        float knockDeg = float.NaN;
        bool Knocked(Func<int, Vector2> stick, Entry e = Entry.Sprint)
        {
            var w = Field(1);
            var p = w.Attacker;
            var buf = new TickIntent[w.PlayerCount];
            bool knocked = false;
            for (int tick = 1; tick <= 480; tick++)
            {
                var it = Run(e, tick);
                int i = tick - 241;
                if (i >= 0) { var r = stick(i); it.RightX = r.X; it.RightZ = r.Y; }
                buf[0] = it;
                w.Step(buf);
                bool now = !knocked && p.LastTouchKind == ActionKind.KnockOn && w.Events.Any(x => x.Kind == EventKind.Touch);
                if (now) { knocked = true; knockDeg = MathF.Atan2(w.Ball.Velocity.Z, w.Ball.Velocity.X) * 180f / MathF.PI; }
            }
            return knocked;
        }
        // Forward, or 40 degrees to his right: the next touch is the heavy one, along the flick (within its touch error).
        Assert.True(Knocked(i => i < 6 ? new Vector2(1f, 0f) : Vector2.Zero));
        Assert.InRange(knockDeg, -12f, 12f);
        float c = MathF.Cos(40f * MathF.PI / 180f), s = MathF.Sin(40f * MathF.PI / 180f);
        Assert.True(Knocked(i => i < 6 ? new Vector2(c, s) : Vector2.Zero));
        Assert.InRange(knockDeg, 28f, 52f);
        // With sprint held the flicks of a heel-to-heel or a spin are a heavy touch too; without sprint a flick is none.
        Assert.True(Knocked(i => HeelToHeel(i, default).Right));
        Assert.False(Knocked(i => i < 6 ? new Vector2(1f, 0f) : Vector2.Zero, Entry.Jog));
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
        Assert.Equal(p.GProg, c.GProg);
        Assert.NotSame(p.GProg, c.GProg);
        Assert.Equal(p.GDetKind, c.GDetKind);
        Assert.Equal(p.GDetTick, c.GDetTick);
        Assert.Equal(p.GHeavyTick, c.GHeavyTick);
        Assert.Equal(p.SkillChainUntil, c.SkillChainUntil);
    }
}
