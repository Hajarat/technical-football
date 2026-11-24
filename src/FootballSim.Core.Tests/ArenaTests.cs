using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>The walled arena: the ball off the boards and ceiling, players kept inside, and rounds that play on after a miss.</summary>
public class ArenaTests
{
    static readonly Tuning T = new();
    static readonly float R = T.Ball.Radius;
    static readonly Pitch P = new(TestData.Scenario("penalty").Pitch, T.Frame, T.Arena);

    /// <summary>True when a ball centre is inside the outline (inset by the ball radius), or in the goal mouth.</summary>
    static bool Inside(Vector3 c, float inset, float eps = 1e-3f)
    {
        if (c.X > P.GoalLineX - inset && MathF.Abs(c.Z) < P.GoalHalfWidth && c.Y < P.BarHeight) return true; // the goal mouth
        if (c.X < inset - eps || c.X > P.GoalLineX - inset + eps) return false;
        if (MathF.Abs(c.Z) > P.HalfWidth - inset + eps) return false;
        if (c.Y > P.Ceiling - inset + eps) return false;
        // Chamfers: x + |z| <= L + W/2 - C, offset by the inset along the board normal.
        if (c.X + MathF.Abs(c.Z) > P.GoalLineX + P.HalfWidth - P.GoalChamfer - inset * MathF.Sqrt(2f) + eps) return false;
        // Rounded back corners.
        var centre = new Vector2(P.CornerRadius, MathF.Sign(c.Z) * (P.HalfWidth - P.CornerRadius));
        if (c.X < centre.X && MathF.Abs(c.Z) > MathF.Abs(centre.Y)
            && (new Vector2(c.X, c.Z) - centre).Length() > P.CornerRadius - inset + eps) return false;
        return true;
    }

    static Ball BallAt(Vector3 pos, Vector3 vel) => new(T.Ball) { Position = pos, Velocity = vel };

    [Fact]
    public void ArenaShapeComesFromTheScenarioAndTuning()
    {
        Assert.Equal(30f, P.GoalLineX);
        Assert.Equal(10f, P.HalfWidth);
        Assert.Equal(6f, P.CornerRadius);
        Assert.Equal(4f, P.GoalChamfer);
        Assert.Equal(8f, P.Ceiling);
        Assert.Equal(1f, P.BoardHeight);
        Assert.Equal(0.70f, P.BoardRestitution);
        Assert.Equal(0.30f, P.BoardFriction);
        Assert.Equal(6f, P.EndWallHalfWidth);
    }

    [Fact]
    public void FastBallNeverTunnelsThroughAnyBoard()
    {
        // 36 m/s (0.30 m per tick) in every direction from the middle, low and rising: the ball stays inside the outline on every tick.
        int hits = 0;
        for (int deg = 0; deg < 360; deg += 5)
        {
            float a = deg * MathF.PI / 180f;
            foreach (float vy in new[] { 0f, 4f, 14f })
            {
                var b = BallAt(new Vector3(15f, 1f, 0f), new Vector3(36f * MathF.Cos(a), vy, 36f * MathF.Sin(a)));
                for (int i = 0; i < 360; i++)
                {
                    var res = b.Step(T.Dt, P, null);
                    if (res.BoardHit) hits++;
                    if (P.IsGoal(b.Position, R)) break; // straight into the goal: nothing to stop it here
                    Assert.True(Inside(b.Position, R), $"{deg} deg vy {vy}: ball outside at {b.Position} on tick {i}");
                }
            }
        }
        Assert.True(hits > 100);
    }

    [Fact]
    public void BallOneTickFromABoardAtFullSpeedIsStoppedThatTick()
    {
        // The end point of the tick is 0.25 m beyond the board; the sweep finds the contact on the path.
        var b = BallAt(new Vector3(15f, 1f, -P.HalfWidth + R + 0.05f), new Vector3(0f, 0f, -36f));
        var res = b.Step(T.Dt, P, null);
        Assert.True(res.BoardHit);
        Assert.True(b.Velocity.Z > 0f);
        Assert.True(Inside(b.Position, R));
    }

    [Fact]
    public void HeadOnHitKeepsSeventyPercentOfTheSpeed()
    {
        foreach (var (pos, vel, axis) in new[]
        {
            (new Vector3(15f, 1.5f, -P.HalfWidth + R + 0.1f), new Vector3(0f, 0f, -20f), 2),   // side wall
            (new Vector3(R + 0.1f, 1.5f, 0f), new Vector3(-20f, 0f, 0f), 0),                  // back wall
            (new Vector3(P.GoalLineX - R - 0.1f, 1.5f, 5f), new Vector3(20f, 0f, 0f), 0),     // end wall beside the goal
        })
        {
            var b = BallAt(pos, vel);
            var res = b.Step(T.Dt, P, null);
            Assert.True(res.BoardHit);
            float vin = axis == 0 ? vel.X : vel.Z, vout = axis == 0 ? b.Velocity.X : b.Velocity.Z;
            Assert.InRange(-vout / vin, 0.68f, 0.71f); // 0.70 less a tick of drag
        }
    }

    [Fact]
    public void ChamferSendsAWideBallBackTowardTheMiddle()
    {
        // Rolling wide of the goal toward the +Z goal-end corner.
        var b = BallAt(new Vector3(22f, R, 5f), new Vector3(15f, 0f, 8f));
        b.Spin = b.RollingSpin(b.Velocity);
        BallStepResult hit = default;
        for (int i = 0; i < 240 && !hit.BoardHit; i++) hit = b.Step(T.Dt, P, null);
        Assert.True(hit.BoardHit);
        Assert.True(hit.BoardPoint.X > P.GoalLineX - P.GoalChamfer && hit.BoardPoint.Z < P.HalfWidth - R - 0.01f, $"hit at {hit.BoardPoint}");
        Assert.True(b.Velocity.Z < 0f, "back toward the middle");
        Assert.True(b.Velocity.X < 0f, "and away from the goal end");
    }

    [Fact]
    public void RoundedCornerReflectsAboutItsRadius()
    {
        var b = BallAt(new Vector3(8f, R, 0f), Vector3.Normalize(new Vector3(-1f, 0f, 1f)) * 20f);
        b.Spin = b.RollingSpin(b.Velocity);
        BallStepResult hit = default;
        Vector3 before = default;
        for (int i = 0; i < 240 && !hit.BoardHit; i++) { before = b.Velocity; hit = b.Step(T.Dt, P, null); }
        Assert.True(hit.BoardHit);
        var c = new Vector2(P.CornerRadius, P.HalfWidth - P.CornerRadius);
        var at = new Vector2(hit.BoardPoint.X, hit.BoardPoint.Z);
        Assert.True(at.X < c.X && at.Y > c.Y, $"hit at {at} should be on the arc");
        Assert.Equal(P.CornerRadius - R, (at - c).Length(), 2);
        var n = Vector2.Normalize(c - at);
        var vin = new Vector2(before.X, before.Z);
        var vout = new Vector2(b.Velocity.X, b.Velocity.Z);
        Assert.True(Vector2.Dot(vin, n) < 0f);
        Assert.InRange(Vector2.Dot(vout, n) / -Vector2.Dot(vin, n), 0.65f, 0.72f);
        Assert.True(Inside(b.Position, R));
    }

    [Fact]
    public void CeilingSendsTheBallDown()
    {
        var b = BallAt(new Vector3(15f, 7f, 0f), new Vector3(2f, 20f, 0f));
        BallStepResult hit = default;
        for (int i = 0; i < 60 && !hit.BoardHit; i++) hit = b.Step(T.Dt, P, null);
        Assert.True(hit.BoardHit);
        Assert.True(b.Velocity.Y < 0f);
        Assert.True(b.Position.Y <= P.Ceiling - R + 1e-3f);
        Assert.True(P.IsOverContact(hit.BoardPoint, R));
    }

    [Fact]
    public void BallAboveTheBarInTheGoalMouthComesBack()
    {
        var b = BallAt(new Vector3(28f, 3.5f, 0f), new Vector3(20f, 3f, 0f));
        BallStepResult hit = default;
        for (int i = 0; i < 60 && !hit.BoardHit; i++)
        {
            hit = b.Step(T.Dt, P, null);
            Assert.True(b.Position.X <= P.GoalLineX - R + 1e-3f);
        }
        Assert.True(hit.BoardHit);
        Assert.True(b.Velocity.X < 0f);
        Assert.True(P.IsOverContact(hit.BoardPoint, R));
    }

    [Fact]
    public void BallUnderTheBarInTheGoalMouthGoesIn()
    {
        var b = BallAt(new Vector3(28f, 1f, 0f), new Vector3(20f, 0f, 0f));
        bool goal = false;
        for (int i = 0; i < 60 && !goal; i++)
        {
            Assert.False(b.Step(T.Dt, P, null).BoardHit);
            goal = P.IsGoal(b.Position, R);
        }
        Assert.True(goal);
    }

    [Fact]
    public void APlayerRunningAtABoardBrakesByHimselfBeforeIt()
    {
        // Sprinting from 8 m out straight at the side wall, at 45 degrees to it, into the back corner and at the end wall: he
        // reaches it moving into it (along the board's normal) at 1.5 m/s or less and his speed never falls by more than 2 m/s in
        // a tick.
        var s = TestData.Sheet(99);
        float r = MathF.Sqrt(0.5f);
        foreach (var (start, stick, normal) in new[]
        {
            (new Vector2(15f, -2f), new Vector2(0f, 1f), new Vector2(0f, 1f)),
            (new Vector2(15f, -2f), new Vector2(r, r), new Vector2(0f, 1f)),
            (new Vector2(8f, -2f), new Vector2(-r, r), new Vector2(-r, r)),
            (new Vector2(20f, -4.5f), new Vector2(1f, 0f), new Vector2(1f, 0f)),
        })
        {
            var p = TestData.Outfield(s, start.X, start.Y, MathF.Atan2(stick.Y, stick.X));
            float now = 0f, prev = 0f, into = float.NaN;
            for (int i = 0; i < 480; i++)
            {
                now += T.Dt;
                var before = p.Velocity;
                Movement.Step(p, new TickIntent { MoveX = stick.X, MoveZ = stick.Y, Sprint = 1 }, T.Dt, now, false, T, P);
                float speed = new Vector2(p.Velocity.X, p.Velocity.Z).Length();
                Assert.True(prev - speed <= 2f, $"from {start}: speed {prev:F2} -> {speed:F2} in one tick at {p.Position}");
                // The first tick the boards hold him (a step along their normal would leave the outline): his velocity into
                // them the tick before.
                // (in a rounded back corner the normal is the arc's, out from its centre)
                var at = new Vector2(p.Position.X, p.Position.Z);
                var corner = new Vector2(P.CornerRadius, MathF.Sign(at.Y) * (P.HalfWidth - P.CornerRadius));
                var n = at.X < corner.X && MathF.Abs(at.Y) > MathF.Abs(corner.Y) ? Vector2.Normalize(at - corner) : normal;
                var probe = p.Position + new Vector3(n.X, 0f, n.Y) * 0.02f;
                if (float.IsNaN(into) && !Inside(probe with { Y = 0f }, Pitch.PlayerRadius)) into = Vector2.Dot(new Vector2(before.X, before.Z), n);
                prev = speed;
            }
            Assert.False(float.IsNaN(into), $"from {start}: never reached the boards");
            Assert.True(into <= 1.5f, $"from {start}: reached the boards at {into:F2} m/s");
        }
    }

    [Fact]
    public void WithTheBallStillAgainstABoardHeAlwaysPlaysItAwayOrStrikesIt()
    {
        // The ball lies still 0.2, 0.35 or 0.5 m from the back wall, a side wall or the goal-end wall (beside the goal). In
        // possession, he comes to it from 6 m at a jog or a sprint, straight at the wall or 45 degrees either way, the stick held
        // into the wall, until 0.3 s after he reaches the wall with no touch winding up (at most 0.8 s). Then either the stick
        // turns straight away from the wall and within 1.0 s a touch sends the ball off it (0.3 m further out than it was, moving
        // away), or shoot is pressed for 0.2 s with the stick toward the goal and the strike comes within 1.2 s. Throughout he keeps
        // the ball, his speed never falls by more than 2 m/s in a tick, he reaches the wall at 1.5 m/s or less, and the ball is
        // never sent up off the boards. (The keeper holds his line, so the goal-end ball is his to keep.)
        var walls = new (string Name, Vector2 Spot, Vector2 In)[]
        {
            ("back wall", new Vector2(0f, 0.5f), new Vector2(1f, 0f)),
            ("near side wall", new Vector2(12f, -10f), new Vector2(0f, 1f)),
            ("far side wall", new Vector2(12f, 10f), new Vector2(0f, -1f)),
            ("goal-end wall", new Vector2(30f, 5.5f), new Vector2(-1f, 0f)),
        };
        foreach (var (name, spot, inward) in walls)
            foreach (float d in new[] { 0.2f, 0.35f, 0.5f })
                foreach (float sprint in new[] { 0f, 1f })
                    foreach (float angle in new[] { 0f, 45f, -45f })
                        foreach (bool shoot in new[] { false, true })
                            AtTheBoard($"{name}, ball {d} m off, {(sprint > 0f ? "sprint" : "jog")} at {angle} deg, {(shoot ? "shoot" : "stick away")}",
                                spot, inward, d, sprint, angle, shoot);
    }

    static void AtTheBoard(string label, Vector2 spot, Vector2 inward, float d, float sprint, float angleDeg, bool shoot)
    {
        float a = angleDeg * MathF.PI / 180f;
        var approach = new Vector2(-inward.X * MathF.Cos(a) + inward.Y * MathF.Sin(a), -inward.X * MathF.Sin(a) - inward.Y * MathF.Cos(a));
        var ball = spot + inward * d;
        var start = ball - approach * 6f;
        var sc = TestData.Scenario("penalty");
        sc.Attacker = new StartSpot { X = start.X, Z = start.Y, FacingDeg = MathF.Atan2(approach.Y, approach.X) * 180f / MathF.PI };
        var b0 = start + approach * 0.6f;
        sc.Ball = new StartSpot { X = b0.X, Z = b0.Y };
        sc.ClockLimit = 60f;
        sc.KeeperHoldLineThroughDribble = true;
        var w = new World(new Tuning(), sc, TestData.Player("striker_a"), TestData.Player("the_wall"), 7);
        w.Begin();
        var p = w.Attacker;
        var buf = new TickIntent[w.PlayerCount];
        void Step(Vector2 stick, float spr = 0f, bool shootHeld = false)
        {
            buf[0] = new TickIntent { MoveX = stick.X, MoveZ = stick.Y, Sprint = spr, ShootHeld = shootHeld };
            w.Step(buf);
        }
        // He takes the ball at his feet, then it is left lying still by the board, 6 m ahead of him.
        for (int i = 0; i < 240 && !p.InPossession; i++) Step(approach * 0.5f);
        for (int i = 0; i < 240 && p.TouchAction is { Pending: true }; i++) Step(approach * 0.5f);
        Assert.True(p.InPossession, $"{label}: never had the ball");
        w.Ball.Position = new Vector3(ball.X, w.Tuning.Ball.Radius, ball.Y);
        w.Ball.Velocity = Vector3.Zero;
        w.Ball.Spin = Vector3.Zero;

        float Gap(Vector3 q) => Vector2.Dot(new Vector2(q.X, q.Z) - spot, inward);
        var toGoal = Vector2.Normalize(new Vector2(P.GoalLineX, 0f) - ball);
        float prev = p.Speed, into = float.NaN, gap0 = 0f;
        int reached = -1, from = -1, done = -1;
        for (int i = 0; i < 1200 && done < 0; i++)
        {
            var before = p.Velocity;
            if (from < 0) Step(approach, sprint);
            else if (!shoot) Step(inward);
            else Step(toGoal, 0f, w.Tick - from < 24);
            bool struck = w.Events.Any(e => e.Kind == EventKind.Strike);
            Assert.True(p.InPossession || struck, $"{label}: lost the ball at tick {w.Tick} ({string.Join(",", w.Events.Select(e => e.Kind))})");
            Assert.True(prev - p.Speed <= 2f, $"{label}: speed {prev:F2} -> {p.Speed:F2} in one tick at tick {w.Tick}");
            Assert.True(w.Ball.Position.Y < 0.6f, $"{label}: the ball went up to {w.Ball.Position.Y:F2} m at tick {w.Tick}");
            prev = p.Speed;
            if (reached < 0 && Gap(p.Position) <= Pitch.PlayerRadius + 0.005f)
            {
                reached = w.Tick;
                into = -Vector2.Dot(new Vector2(before.X, before.Z), inward);
            }
            if (from < 0 && (reached >= 0 && w.Tick - reached >= 36 && (p.TouchAction is not { Pending: true } || w.Tick - reached >= 96) || i >= 600))
            {
                from = w.Tick;
                gap0 = Gap(w.Ball.Position);
            }
            if (from < 0) continue;
            if (shoot ? struck
                : Gap(w.Ball.Position) > MathF.Max(gap0, d) + 0.3f && Vector2.Dot(new Vector2(w.Ball.Velocity.X, w.Ball.Velocity.Z), inward) > 0.3f)
                done = w.Tick;
            else if (w.Tick - from > 240) break;
        }
        Assert.True(reached >= 0, $"{label}: never reached the wall");
        Assert.True(into <= 1.5f, $"{label}: reached the wall at {into:F2} m/s");
        float took = done < 0 ? float.PositiveInfinity : (done - from) * T.Dt;
        Assert.True(took <= (shoot ? 1.2f : 1.0f), $"{label}: {(shoot ? "no strike" : "the ball still at the wall")} {took:F2} s after the stick turned");
    }

    public static IEnumerable<object[]> SkillMovesAtTheBoards() => new (string, int)[]
    {
        ("step_over", 1), ("step_over", -1), ("body_feint", 1), ("body_feint", -1), ("ball_roll", 1), ("ball_roll", -1),
        ("drag_back", 0), ("fake_stop", 0), ("fake_cut", 1), ("fake_cut", -1), ("elastico", 1), ("elastico", -1),
        ("heel_to_heel", 0), ("spin", 1), ("spin", -1),
    }.Select(m => new object[] { m.Item1, m.Item2 });

    /// <summary>
    /// A skill move started by the boards plays the ball along them, never into them: the ball is not sent up off a board, he
    /// keeps it, and the boards never stop him in one tick. Each move (both sides) at a jog and a sprint, its gesture made with the
    /// ball 0.3, 0.6 or 1.0 m off the board: running straight at the back wall, a side wall and a rounded back corner, and running
    /// along a side wall (the board on his left and on his right). From the gesture until 0.5 s after the move ends: the ball
    /// stays below 0.6 m, he keeps the ball, and his speed never falls by more than 2 m/s in a tick (the drag-back 2.6: its sole's
    /// reach steps his speed by up to 2.3 m/s in the open too).
    /// </summary>
    [Theory]
    [MemberData(nameof(SkillMovesAtTheBoards))]
    public void ASkillMoveAtTheBoardsKeepsTheBallOffThem(string move, int side)
    {
        var corner = new Vector2(P.CornerRadius, -(P.HalfWidth - P.CornerRadius));
        var cornerIn = Vector2.Normalize(new Vector2(1f, 1f));
        var into = new (string Name, Vector2 Spot, Vector2 In)[]
        {
            ("back wall", new Vector2(0f, 0f), new Vector2(1f, 0f)),
            ("side wall", new Vector2(16f, -P.HalfWidth), new Vector2(0f, 1f)),
            ("back corner", corner - cornerIn * P.CornerRadius, cornerIn),
        };
        foreach (float sprint in new[] { 0f, 1f })
            foreach (float d in new[] { 0.3f, 0.6f, 1.0f })
            {
                foreach (var (name, spot, inward) in into)
                    MoveAtTheBoard($"{move} {side} into the {name}, {(sprint > 0f ? "sprint" : "jog")}, {d} m", move, side, sprint, d,
                        spot + inward * 6f, -inward, along: false);
                foreach (float s in new[] { -1f, 1f })
                    MoveAtTheBoard($"{move} {side} along the {(s < 0f ? "near" : "far")} side wall, {(sprint > 0f ? "sprint" : "jog")}, {d} m", move, side, sprint, d,
                        new Vector2(6.5f, s * (P.HalfWidth - d - R)), new Vector2(1f, 0f), along: true);
            }
    }

    static Vector2 Turned(Vector2 v, Vector2 head) => new(v.X * head.X - v.Y * head.Y, v.X * head.Y + v.Y * head.X);

    /// <summary>The move's stick trace on tick <paramref name="i"/> from its start, for a player running along +X (his right +Z).</summary>
    static void MoveTrace(ref TickIntent it, string move, int i, int side)
    {
        var ahead = new Vector2(1f, 0f);
        var sideV = new Vector2(0f, side);
        Vector2 right = Vector2.Zero;
        switch (move)
        {
            case "step_over": right = i < 6 ? ahead : i < 12 ? sideV : Vector2.Zero; break;
            case "body_feint": right = i < 6 ? sideV : Vector2.Zero; break;
            case "ball_roll": right = i < 26 ? sideV : Vector2.Zero; break;
            case "elastico": right = i < 6 ? -ahead : i < 12 ? sideV : Vector2.Zero; break;
            case "heel_to_heel": right = i < 5 ? ahead : i is >= 9 and < 14 ? -ahead : Vector2.Zero; break;
            case "spin": right = i < 5 ? ahead : i is >= 9 and < 13 ? sideV : Vector2.Zero; break;
            case "drag_back":
                if (i < 8) { it.MoveX = -1f; it.MoveZ = 0f; it.Finesse = it.Chip = true; }
                break;
            case "fake_stop": it.MoveX = it.MoveZ = 0f; it.ShootHeld = i < 15; it.Cancel = i is >= 6 and < 10; break;
            case "fake_cut":
                if (i >= 1) { float a = side * MathF.PI / 3f; it.MoveX = MathF.Cos(a); it.MoveZ = MathF.Sin(a); }
                it.ShootHeld = i < 15; it.Cancel = i is >= 6 and < 10;
                break;
        }
        it.RightX = right.X; it.RightZ = right.Y;
    }

    static void MoveAtTheBoard(string label, string move, int side, float sprint, float d, Vector2 start, Vector2 head, bool along)
    {
        var sc = TestData.Scenario("penalty");
        // (Along a side wall in a longer arena, so a sprint has room to get going before the move and to run on after it.)
        if (along) sc.Pitch = new PitchSpec { Length = 60f, Width = sc.Pitch.Width };
        sc.Attacker = new StartSpot { X = start.X, Z = start.Y, FacingDeg = MathF.Atan2(head.Y, head.X) * 180f / MathF.PI };
        sc.Ball = new StartSpot { X = start.X + head.X * 0.6f, Z = start.Y + head.Y * 0.6f };
        sc.ClockLimit = 60f;
        sc.KeeperHoldLineThroughDribble = true;
        var w = new World(new Tuning(), sc, TestData.Player("striker_a"), TestData.Player("the_wall"), 7);
        w.Begin();
        var p = w.Attacker;
        var buf = new TickIntent[w.PlayerCount];
        float BallGap() => w.Pitch.BoardGap(new Vector2(w.Ball.Position.X, w.Ball.Position.Z)) - R;
        int g0 = -1, end = -1;
        bool gained = false, started = false;
        float prev = 0f;
        for (int tick = 1; tick <= 1200 && w.IsSimulating; tick++)
        {
            if (g0 < 0 && gained && (along ? tick >= (sprint > 0f ? 264 : 180) : BallGap() <= d)) g0 = tick;
            var it = new TickIntent { MoveX = head.X, MoveZ = head.Y, Sprint = sprint };
            if (g0 > 0 && tick - g0 < 30)
            {
                var local = it with { MoveX = 1f, MoveZ = 0f };
                MoveTrace(ref local, move, tick - g0, side);
                // No stick gesture registers with sprint held: it is let go of until the gesture has registered.
                if (move is not ("fake_stop" or "fake_cut") && p.LastGestureTick < g0) local.Sprint = 0f;
                var l = Turned(new Vector2(local.MoveX, local.MoveZ), head);
                var r = Turned(new Vector2(local.RightX, local.RightZ), head);
                it = local with { MoveX = l.X, MoveZ = l.Y, RightX = r.X, RightZ = r.Y };
            }
            buf[0] = it;
            w.Step(buf);
            foreach (var e in w.Events)
            {
                if (e.Team != 0) continue;
                if (e.Kind == EventKind.PossessionGained) gained = true;
                Assert.False(g0 > 0 && e.Kind == EventKind.PossessionLost, $"{label}: lost the ball at tick {w.Tick}");
            }
            if (g0 > 0 && p.SkillAction != null && !started) { started = true; end = p.SkillAction.EndTick; }
            if (g0 > 0)
            {
                Assert.True(w.Ball.Position.Y < 0.6f, $"{label}: the ball went up to {w.Ball.Position.Y:F2} m at tick {w.Tick}");
                float limit = move == "drag_back" ? 2.6f : 2f;
                Assert.True(prev - p.Speed <= limit, $"{label}: speed {prev:F2} -> {p.Speed:F2} in one tick at tick {w.Tick}");
                if (tick >= (end > 0 ? end + 60 : g0 + 180)) break;
            }
            prev = p.Speed;
        }
        Assert.True(g0 > 0, $"{label}: never had the ball by the board");
        Assert.True(started, $"{label}: no move");
        Assert.True(p.InPossession, $"{label}: lost the ball");
    }

    [Fact]
    public void PlayersStayInsideTheOutlineAndStopAtTheEndWall()
    {
        var s = TestData.Sheet(99);
        foreach (var (start, stick) in new[]
        {
            (new Vector2(5f, 5f), new Vector2(-1f, 1f)),   // into the rounded back corner
            (new Vector2(25f, 5f), new Vector2(1f, 1f)),   // into the chamfer
            (new Vector2(25f, 0f), new Vector2(1f, 0f)),   // into the goal mouth
            (new Vector2(15f, 0f), new Vector2(0f, -1f)),  // into the side wall
        })
        {
            var p = TestData.Outfield(s, start.X, start.Y, MathF.Atan2(stick.Y, stick.X));
            float now = 0f;
            for (int i = 0; i < 480; i++)
            {
                now += T.Dt;
                Movement.Step(p, new TickIntent { MoveX = stick.X, MoveZ = stick.Y, Sprint = 1 }, T.Dt, now, false, T, P);
                Assert.True(Inside(p.Position with { Y = 0f }, Pitch.PlayerRadius) && p.Position.X <= P.GoalLineX - Pitch.PlayerRadius + 1e-3f,
                    $"player outside at {p.Position}");
            }
            // Pressed into the board, he has no velocity into it.
            var probe = p.Position + p.Velocity * 0.05f;
            Assert.True(Inside(probe with { Y = 0f }, Pitch.PlayerRadius, 0.02f), $"velocity {p.Velocity} still points into the board");
        }
        var mouth = new Vector3(P.GoalLineX + 1f, 0f, 0f);
        var v = new Vector3(5f, 0f, 0f);
        Assert.True(P.ClampPlayer(ref mouth, ref v));
        Assert.Equal(P.GoalLineX - Pitch.PlayerRadius, mouth.X, 4);
        Assert.Equal(0f, v.X);
    }

    [Fact]
    public void WideShotScoresAsWideAndPlayGoesOnUntilTheClock()
    {
        // Aim 3 m outside the post: the shot comes back off the end-wall boards, the round plays on and ends on the 20 s clock.
        var sc = TestData.Scenario("penalty");
        var att = TestData.Player("striker_a");
        var kee = TestData.Player("the_wall");
        int wide = 0, timeUp = 0;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var first = RoundRunner.RunFirstShot(T, sc, att, kee, seed, new ScriptedAttacker { AimInset = -3f });
            Assert.NotEqual(ShotEnd.Out, first.End);
            if (first.End != ShotEnd.Wide) continue;
            wide++;
            var report = RoundRunner.RunToReport(T, sc, att, kee, seed, out _, new ScriptedAttacker { AimInset = -3f });
            Assert.NotEqual(Outcome.Wide, report.Outcome);
            Assert.NotEqual(Outcome.Out, report.Outcome);
            if (report.Outcome == Outcome.TimeUp)
            {
                timeUp++;
                Assert.InRange(report.Clock, 19.99f, 20.01f);
            }
        }
        Assert.True(wide >= 10, $"wide {wide}");
        Assert.True(timeUp >= 1, $"time up {timeUp}");
    }

    [Fact]
    public void AttackerCanShootAgainAfterAMiss()
    {
        var w = TestData.Live("penalty");
        var buf = new TickIntent[w.PlayerCount];
        // First shot: hard and wide, off the boards.
        for (int i = 0; i < 60; i++) { buf[0] = new TickIntent { ShootHeld = true }; w.Step(buf); }
        buf[0] = new TickIntent { MoveX = 1f, MoveZ = 0.6f };
        w.Step(buf);
        buf[0] = default;
        for (int i = 0; i < 600 && !w.Events.Any(e => e.Kind == EventKind.BoardHit); i++) w.Step(buf);
        Assert.Single(w.Shots);
        Assert.Equal(RoundState.Live, w.State);
        // Put the ball back at the attacker's feet and shoot again: a second strike happens in the same round.
        w.Ball.Position = new Vector3(w.Attacker.Position.X + 0.5f, R, w.Attacker.Position.Z);
        w.Ball.Velocity = Vector3.Zero;
        w.Ball.Spin = Vector3.Zero;
        for (int i = 0; i < 40; i++) { buf[0] = new TickIntent { ShootHeld = true }; w.Step(buf); }
        buf[0] = new TickIntent { MoveX = 1f };
        w.Step(buf);
        buf[0] = default;
        for (int i = 0; i < 60 && w.Shots.Count < 2; i++) w.Step(buf);
        Assert.Equal(2, w.Shots.Count);
    }

    [Fact]
    public void KeeperSmothersASlowLooseBallNearHim()
    {
        var w = TestData.Live("edge_of_box");
        var k = w.Keeper.Position;
        w.Ball.Position = new Vector3(k.X - 1f, R, k.Z);
        w.Ball.Velocity = new Vector3(-1f, 0f, 0f);
        bool smother = false;
        for (int i = 0; i < 120 && w.State == RoundState.Live; i++)
        {
            w.Step(new TickIntent[w.PlayerCount]);
            smother |= w.Keeper.KeeperAction == KeeperAction.Smother;
        }
        Assert.True(smother, "he goes down on it");
        Assert.Equal(RoundState.Outcome, w.State);
        Assert.Equal(Outcome.Held, w.Outcome);
        Assert.Equal(w.Keeper, w.BallController);
        Assert.Equal(KeeperAction.Holding, w.Keeper.KeeperAction);
        Assert.Equal(w.Keeper.HandPosition, w.Ball.Position);
    }

    [Theory]
    [InlineData(1f, 0.11f, 6f)]   // too fast
    [InlineData(1f, 1.8f, 0f)]    // too high
    [InlineData(4f, 0.11f, 0f)]   // out of reach
    public void KeeperLeavesABallHeCannotClaim(float distance, float height, float speed)
    {
        var w = TestData.Live("edge_of_box");
        var k = w.Keeper.Position;
        w.Ball.Position = new Vector3(k.X - distance, height, k.Z + 0.01f);
        w.Ball.Velocity = new Vector3(0f, 0f, speed);
        w.Step(new TickIntent[w.PlayerCount]);
        Assert.Equal(RoundState.Live, w.State);
        Assert.Null(w.BallController);
    }
}
