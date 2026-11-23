using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>Sheet-driven moves (the follower in SkillMoves): the two converted so far, the elastico and the drag-back.</summary>
public partial class SkillMoveTests
{
    /// <summary>
    /// One player and a ball stepped as the world steps them in possession (the move's start, the legs, his move, the ball's
    /// play, the ball's own step), with nothing else on the pitch: a move can be set up exactly.
    /// </summary>
    internal sealed class Bench
    {
        public readonly Tuning T;
        public readonly PlayerState P;
        public readonly Ball B;
        public readonly Pitch? Pitch;
        public int Tick = 1;
        public float Now;
        public BallAction? Move;
        public readonly List<(int Tick, Foot Foot)> Touches = new();
        readonly SimRng _rng = new(1);

        /// <summary>He is at <paramref name="at"/> facing <paramref name="facing"/> at <paramref name="pace"/>; the ball lies <paramref name="ahead"/> and <paramref name="right"/> of him, rolling his way at <paramref name="ballPace"/>.</summary>
        public Bench(float pace, float ahead, float right, float ballPace = 0f, Tuning? t = null, Pitch? pitch = null, Vector3 at = default, float facing = 0f)
        {
            T = t ?? TestData.Tuning();
            Pitch = pitch;
            P = TestData.Outfield(TestData.Player("striker_a"), at.X, at.Z, facing);
            var f = new Vector3(MathF.Cos(facing), 0f, MathF.Sin(facing));
            var r = new Vector3(-f.Z, 0f, f.X);
            P.Velocity = f * pace;
            P.RunSpeed = pace;
            P.InPossession = true;
            B = new Ball(T.Ball) { Position = at + f * ahead + r * right + new Vector3(0f, T.Ball.Radius, 0f), Velocity = f * ballPace };
            B.Spin = B.RollingSpin(B.Velocity);
        }

        public bool Start(Gesture g, SkillSide side)
        {
            SkillMoves.Request(P, new GestureRead(g, side, Vector2.Zero), Tick);
            if (SkillMoves.TryStart(P, B, true, Tick, Now, T) == null) return false;
            Move = P.SkillAction;
            return Move != null;
        }

        public void Step(TickIntent it = default)
        {
            var assist = TouchPlanner.BeginTick(P, it, B, true, true, Tick, Now, T, Pitch, out var legs);
            Movement.Step(P, legs, T.Dt, Now, assist, T, Pitch);
            if (TouchPlanner.Step(P, it, B, true, P.SkillAction != null ? TouchStepMode.Normal : TouchStepMode.None, Tick, Now, T, Pitch, _rng) is { } touch)
                Touches.Add((Tick, touch.Foot));
            B.Step(T.Dt, Pitch, null);
            Tick++;
            Now += T.Dt;
        }
    }

    public static IEnumerable<object[]> SheetMoves() =>
        from move in new[] { "elastico_left", "elastico_right", "drag_back_right", "drag_back_left" }
        from pace in new[] { 0f, 5.3f, 7.6f }
        select new object[] { move, pace };

    /// <summary>A bench with the ball where the move's playing foot finds it (the drag-back's foot is the one on the ball's side).</summary>
    static Bench Staged(string move, float pace, Tuning? t = null, Pitch? pitch = null, Vector3 at = default, float facing = 0f, float ahead = 0.45f)
    {
        float right = move switch { "drag_back_right" => 0.2f, "drag_back_left" => -0.2f, _ => 0f };
        var b = new Bench(pace, ahead + 0.02f * pace, right, MathF.Max(0f, pace - 0.8f), t, pitch, at, facing);
        bool ok = move.StartsWith("elastico") ? b.Start(Gesture.Elastico, move.EndsWith("left") ? SkillSide.Left : SkillSide.Right) : b.Start(Gesture.DragBack, SkillSide.None);
        Assert.True(ok, $"{move} at {pace} m/s did not start");
        Assert.NotNull(b.Move!.Sheet);
        if (move.StartsWith("drag_back")) Assert.Equal(move.EndsWith("right") ? Foot.Right : Foot.Left, b.Move.Foot);
        return b;
    }

    /// <summary>The hardest the blended sheet itself changes his velocity, m/s^2 (its root at the tick rate, from its start to its release).</summary>
    static float SheetAccel(SheetPlay s, Tuning t)
    {
        float worst = 0f, dt = t.Dt;
        for (float tau = s.Event(0) + dt; tau + dt <= s.EventSeconds("release") + 1e-4f; tau += dt)
        {
            RootRow a = s.Root(tau - dt), b = s.Root(tau), c = s.Root(tau + dt);
            worst = MathF.Max(worst, new Vector2(a.X - 2f * b.X + c.X, a.Z - 2f * b.Z + c.Z).Length() / (dt * dt));
        }
        return worst;
    }

    [Theory]
    [MemberData(nameof(SheetMoves))]
    public void ASheetMoveHasItsContactsOnTheirTicksTheBallOnItsLegAndHandsTheRunHisGroundSpeed(string move, float pace)
    {
        var b = Staged(move, pace);
        var t = b.T;
        var m = b.Move!;
        var s = m.Sheet!;
        // the sheet's own acceleration, with room for the eases on top of it (the nudge and what he came in with)
        float limit = SheetAccel(s, t) + 2f * t.Movement.BalanceDeceleration + 1f;
        Vector3 v0 = b.P.Velocity;
        while (b.Tick < m.EndTick)
        {
            int tick = b.Tick;
            b.Step();
            var dv = b.P.Velocity - v0;
            Assert.True(new Vector2(dv.X, dv.Z).Length() / t.Dt <= limit, $"{move} {pace} tick +{tick - m.StartTick}: {new Vector2(dv.X, dv.Z).Length() / t.Dt:F1} m/s^2 (limit {limit:F1})");
            v0 = b.P.Velocity;
            Assert.Equal(0f, b.P.RunSpeed);
            // a driven leg has the ball on its target when the tick ends (to 1 cm)
            bool driven = tick >= m.ContactTick && (m.Contact2Tick < 0 || tick < m.Contact2Tick);
            if (driven)
            {
                float off = (b.B.Position - m.LastTarget).Length();
                Assert.True(off < 0.01f, $"{move} {pace} tick +{tick - m.StartTick}: the ball is {off:F3} m off its leg");
            }
        }
        // the contacts raise their touches on their ticks, with their feet
        var want = new List<(int, Foot)> { (m.ContactTick, m.Foot) };
        if (m.Contact2Tick > 0) want.Add((m.Contact2Tick, m.Foot2));
        Assert.Equal(want, b.Touches);
        Assert.True(m.Made);
        Assert.Equal(m.ContactTick + (int)MathF.Round((s.EventSeconds("release") - s.EventSeconds("contact")) * t.TickRate), m.EndTick);
        // the release: his running speed is his ground speed along his facing, and the rest of his velocity is carried
        var v = new Vector2(b.P.Velocity.X, b.P.Velocity.Z);
        var face = new Vector2(MathF.Cos(b.P.Facing), MathF.Sin(b.P.Facing));
        SkillMoves.Finish(b.P, default, b.B, b.Tick, b.Now, t);
        Assert.Null(b.P.SkillAction);
        Assert.Equal(MathF.Max(0f, Vector2.Dot(v, face)), b.P.RunSpeed, 4);
        Assert.True((face * b.P.RunSpeed + b.P.SteerCarry - v).Length() < 1e-4f, "his velocity is handed on whole");
        if (move.StartsWith("drag_back"))
        {
            Assert.True(new Vector2(b.B.Velocity.X, b.B.Velocity.Z).Length() < 1e-6f, "the drag-back leaves the ball at rest");
            Assert.Equal(b.Tick, b.P.SkillExitTick);
            Assert.NotNull(b.P.SkillExitRule);
        }
        else Assert.Equal(b.Tick, b.P.SkillBurstAtTick);
    }

    [Theory]
    [InlineData("elastico", 0f)]
    [InlineData("elastico", 5.3f)]
    [InlineData("drag_back", 0f)]
    [InlineData("drag_back", 5.3f)]
    [InlineData("drag_back", 7.6f)]
    public void SheetMoveMirrors(string move, float pace)
    {
        // The mirrored side of a move is the mirror of the authored side's, tick by tick, up to its release.
        var a = Staged(move == "elastico" ? "elastico_left" : "drag_back_right", pace);
        var b = Staged(move == "elastico" ? "elastico_right" : "drag_back_left", pace);
        Assert.Equal(a.Move!.EndTick, b.Move!.EndTick);
        Assert.Equal(a.Move.ContactTick, b.Move.ContactTick);
        Assert.NotEqual(a.Move.Foot, b.Move.Foot);
        // (the elastico's launch is solved onto his next swing, whose foot this bench does not mirror: compared up to it)
        int until = a.Move.Contact2Tick > 0 ? a.Move.Contact2Tick : a.Move.EndTick;
        while (a.Tick < until)
        {
            a.Step(); b.Step();
            string at = $"{move} {pace} tick +{a.Tick - a.Move.StartTick}";
            Assert.True(MathF.Abs(a.P.Position.X - b.P.Position.X) < 1e-4f && MathF.Abs(a.P.Position.Z + b.P.Position.Z) < 1e-4f, $"{at}: {a.P.Position} against {b.P.Position}");
            Assert.True(MathF.Abs(Movement.WrapAngle(a.P.Facing + b.P.Facing)) < 1e-4f, $"{at}: facing {a.P.Facing} against {b.P.Facing}");
            Assert.True(MathF.Abs(a.B.Position.X - b.B.Position.X) < 1e-4f && MathF.Abs(a.B.Position.Z + b.B.Position.Z) < 1e-4f, $"{at}: ball {a.B.Position} against {b.B.Position}");
        }
        Assert.Equal(a.Touches.Select(x => x.Tick), b.Touches.Select(x => x.Tick));
        Assert.NotEmpty(a.Touches);
    }

    [Theory]
    [MemberData(nameof(SheetMoves))]
    public void AbortOnEveryTickReleasesBall(string move, float pace)
    {
        // Possession lost or the move aborted on any tick: the ball keeps the velocity it has and is never driven again.
        int ticks = Staged(move, pace).Move!.EndTick - 1;
        for (int k = 0; k <= ticks; k++)
        {
            var b = Staged(move, pace);
            for (int i = 0; i < k; i++) b.Step();
            SkillMoves.Abort(b.P);
            Assert.Null(b.P.SkillAction);
            var free = new Ball(b.T.Ball) { Position = b.B.Position, Velocity = b.B.Velocity, Spin = b.B.Spin };
            var velocity = b.B.Velocity;
            Assert.Equal(velocity, b.B.Velocity);
            for (int i = 0; i < 30; i++)
            {
                b.Step();
                free.Step(b.T.Dt, null, null);
                Assert.True((b.B.Position - free.Position).Length() < 1e-6f, $"{move} {pace}: aborted on tick +{k}, the ball was driven {i + 1} ticks later");
            }
        }
    }

    [Theory]
    [InlineData("elastico_left")]
    [InlineData("elastico_right")]
    [InlineData("drag_back_right")]
    [InlineData("drag_back_left")]
    public void SheetMoveIntoBoards(string move)
    {
        // Started facing a board within its travel: his track slows as he nears the board (he is never stopped by it from a run
        // in one tick), the ball's track is cut short or bent 2 cm clear of it, and the move still plays through.
        var w = TestData.Live("edge_of_box");
        var pitch = w.Pitch;
        var t = TestData.Tuning();
        float clear = t.Ball.Radius + 0.02f;
        // (find the side board: the furthest he can stand toward +Z at his start's X)
        float x = w.Attacker.Position.X, z = 0f;
        while (pitch.KeepOffBoards(new Vector2(x, z + 0.05f), 0.35f) == new Vector2(x, z + 0.05f)) z += 0.05f;
        // (how fast he can be coming at the board from each distance: he brakes for it by himself before the move)
        foreach (var (pace, back) in new[] { (0f, 0.3f), (0f, 0.9f), (1.2f, 0.3f), (3f, 0.9f), (4f, 1.5f) })
        {
            var b = Staged(move, pace, t, pitch, new Vector3(x, 0f, z - back), MathF.PI / 2f, ahead: 0.35f);
            var m = b.Move!;
            string at = $"{move} at {pace} m/s, {back} m off the board";
            Vector3 ball0 = b.B.Position;
            float speed0 = new Vector2(b.P.Velocity.X, b.P.Velocity.Z).Length();
            while (b.Tick < m.EndTick)
            {
                b.Step();
                var bp = new Vector2(b.B.Position.X, b.B.Position.Z);
                if (b.Tick > m.ContactTick + 1)
                    Assert.True((pitch.KeepOffBoards(bp, clear - 2e-3f) - bp).Length() < 1e-4f, $"{at} tick +{b.Tick - m.StartTick}: the ball is within 2 cm of a board");
                Assert.True((b.B.Position - ball0).Length() < 0.15f, $"{at} tick +{b.Tick - m.StartTick}: the ball jumped {(b.B.Position - ball0).Length():F2} m");
                ball0 = b.B.Position;
                var pp = new Vector2(b.P.Position.X, b.P.Position.Z);
                Assert.True((pitch.KeepOffBoards(pp, 0.2f) - pp).Length() < 1e-3f, $"{at}: he is in the board");
                float speed = new Vector2(b.P.Velocity.X, b.P.Velocity.Z).Length();
                // he loses no more speed in a tick than his sheet's hardest stop and the eases allow (a board never stops him dead)
                Assert.True(speed0 - speed <= (SheetAccel(m.Sheet!, t) + 2f * t.Movement.BalanceDeceleration + 1f) * t.Dt + 0.05f, $"{at} tick +{b.Tick - m.StartTick}: {speed0:F2} -> {speed:F2} m/s in a tick");
                speed0 = speed;
                Assert.False(float.IsNaN(b.B.Position.X + b.P.Position.X));
            }
            Assert.True(m.Made, at);
            Assert.Equal(m.Contact2Tick > 0 ? 2 : 1, b.Touches.Count);
            // within reach of him still
            Assert.True(TouchModel.HorizontalDistance(b.P.Position, b.B.Position) < 1.2f, $"{at}: the ball ends {TouchModel.HorizontalDistance(b.P.Position, b.B.Position):F2} m from him");
        }
    }

    [Fact]
    public void ASheetMoveIsTheSameTwiceOnTheSameSeedAndInputs()
    {
        foreach (var (move, side) in new[] { ("elastico", 1), ("elastico", -1) })
            foreach (var e in new[] { Entry.Standing, Entry.Jog, Entry.Sprint })
            {
                var a = PlayTwoContact(move, e, side, 3);
                var b = PlayTwoContact(move, e, side, 3);
                Assert.NotNull(a.Move.Sheet);
                Assert.Equal(a.Move.EndTick, b.Move.EndTick);
                Assert.Equal(a.At.Count, b.At.Count);
                foreach (var (tick, s) in a.At) Assert.Equal(s, b.At[tick]);
                Assert.Equal(a.BallV2, b.BallV2);
                Assert.Equal(a.NextTouch, b.NextTouch);
            }
        // and the drag-back, through a whole scripted run
        (Vector3, Vector3, int) Drag(Entry e)
        {
            var w = Field(5);
            var buf = new TickIntent[w.PlayerCount];
            int end = -1;
            for (int tick = 1; tick <= 420; tick++)
            {
                int i = tick - 241;
                buf[0] = i < 0 ? Run(e, tick) : SkillMoveTests.Drag(i, i < 60 ? default : new TickIntent { MoveZ = 1f });
                w.Step(buf);
                if (w.Events.Any(x => x.Kind == EventKind.SkillMove)) { end = w.Attacker.SkillMove!.EndTick; Assert.NotNull(w.Attacker.SkillMove.Sheet); }
            }
            return (w.Attacker.Position, w.Ball.Position, end);
        }
        foreach (var e in new[] { Entry.Standing, Entry.Jog, Entry.Sprint }) Assert.Equal(Drag(e), Drag(e));
    }

    [Fact]
    public void WithItsDriverLegacyAMoveIsPlayedByItsFormulas()
    {
        // skill.driver picks what plays each move; without the sheets every move is legacy too
        var legacy = TestData.Tuning();
        legacy.Skill.Driver["elastico"] = "legacy";
        legacy.Skill.Driver["drag_back"] = "legacy";
        foreach (var t in new[] { legacy, new Tuning() })
        {
            var e = new Bench(0f, 0.4f, 0f, 0f, t);
            Assert.True(e.Start(Gesture.Elastico, SkillSide.Left));
            Assert.Null(e.Move!.Sheet);
            var d = new Bench(0f, 0.4f, 0.1f, 0f, t);
            Assert.True(d.Start(Gesture.DragBack, SkillSide.None));
            Assert.Null(d.Move!.Sheet);
        }
        // the controller's own turn at the feet (a drag-back kind) stays on its own timing
        var sheet = new Bench(0f, 0.4f, 0.1f);
        var turn = SkillMoves.StartFeetTurn(sheet.P, new Vector2(-1f, 0f), sheet.B, 1, 0f, sheet.T);
        Assert.Null(turn.Sheet);
        Assert.True(turn.FeetTurn);
    }

    [Fact]
    public void ADragBacksExitTouchIsHeldInsideTheSheetsBand()
    {
        // 60 to 180 degrees off the old run on the open side (the playing foot's): a stick outside it is held to its nearest edge
        var rule = TestData.Sheets.Find("drag_back", SkillSide.None, Foot.Right)!.Value.Stand.Exit;
        Assert.Equal(SheetExitKind.Touch, rule.Kind);
        var t = TestData.Tuning();
        Vector2 fwd = new(1f, 0f), open = new(0f, 1f);
        float Deg(Vector2 stick)
        {
            var it = SkillMoves.ExitIntent(rule, fwd, open, new TickIntent { MoveX = stick.X, MoveZ = stick.Y }, t);
            return MathF.Atan2(Vector2.Dot(it.Move, open), Vector2.Dot(it.Move, fwd)) * 180f / MathF.PI;
        }
        Assert.Equal(90f, Deg(new Vector2(0f, 1f)), 2);       // square to the open side: inside the band
        Assert.Equal(60f, Deg(new Vector2(1f, 0f)), 2);       // straight on: held to 60
        Assert.Equal(60f, Deg(new Vector2(1f, 0.2f)), 2);
        Assert.Equal(180f, MathF.Abs(Deg(new Vector2(-1f, -0.3f))), 2); // back and to the other side: held to straight back
        Assert.Equal(60f, Deg(new Vector2(1f, -0.2f)), 2);    // a little to the other side, forward: the near edge
        // the stick at rest stays at rest: the ball is left where the sole put it
        Assert.Equal(0f, SkillMoves.ExitIntent(rule, fwd, open, default, t).Move.Length());
    }
}
