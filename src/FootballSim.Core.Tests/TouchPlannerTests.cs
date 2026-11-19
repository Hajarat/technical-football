using System.Numerics;
using FootballSim.Core;
using Xunit.Abstractions;

namespace FootballSim.Core.Tests;

/// <summary>The touch planner's review focus: replans, stop touches, and a shot pressed during a windup.</summary>
public class TouchPlannerTests
{
    readonly ITestOutputHelper _out;

    public TouchPlannerTests(ITestOutputHelper output) { _out = output; }

    static Tuning Exact()
    {
        var t = new Tuning();
        t.Touch.SpeedError = new RatingRange(0f, 0f);
        t.Touch.DirectionErrorDeg = new RatingRange(0f, 0f);
        return t;
    }

    static World Lab(ulong seed, Tuning? t = null, int dribbling = 70)
    {
        var s = TestData.Sheet(75);
        s.Dribbling = dribbling;
        var sc = new Scenario
        {
            Id = "lab",
            Pitch = new PitchSpec { Length = 300f, Width = 200f },
            Ball = new StartSpot { X = 2.5f },
            Attacker = new StartSpot { X = 2.0f },
        };
        var w = new World(t ?? new Tuning(), sc, s, TestData.Sheet(50), seed);
        w.Begin();
        return w;
    }

    static readonly TickIntent Jog = new() { MoveX = 1 };
    static readonly TickIntent Sprint = new() { MoveX = 1, Sprint = 1 };

    static void Step(World w, TickIntent it)
    {
        var buf = new TickIntent[w.PlayerCount];
        buf[0] = it;
        w.Step(buf);
    }

    /// <summary>Steps with <paramref name="it"/> until the attacker's pending action reaches <paramref name="phase"/> (after a warm-up).</summary>
    static BallAction RunUntilPhase(World w, TickIntent it, ActionPhase phase, int warmTicks = 240)
    {
        for (int i = 0; i < warmTicks; i++) Step(w, it);
        for (int i = 0; i < 600; i++)
        {
            if (w.Attacker.Action is { Pending: true } a && a.Phase == phase) return a;
            Step(w, it);
        }
        throw new Exception($"no action reached {phase}");
    }

    static IEnumerable<BallAction> ContactsOf(World w)
    {
        if (!w.Events.Any(e => e.Kind == EventKind.Touch && e.Team == 0)) yield break;
        var p = w.Attacker;
        // During a shot the touch (the soft touch) is seen through TouchAction; Action shows the shot.
        yield return p.PrevTouchAction is { Made: true } pa && pa.ContactTick == w.Tick ? pa : p.TouchAction!;
    }

    [Fact]
    public void ReleasingSprintDuringThePreWindupReplans()
    {
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var w = Lab(seed);
            var a = RunUntilPhase(w, Sprint, ActionPhase.PreWindup);
            Assert.Equal(ActionKind.Sprint, a.Kind);
            Step(w, Jog);
            Assert.Equal(ActionPhase.Done, a.Phase);
            Assert.False(a.Made);
            // A new touch is planned at once, or after a short run to the ball.
            for (int i = 0; i < 60 && w.Attacker.Action == a; i++) Step(w, Jog);
            Assert.NotSame(a, w.Attacker.Action);
            Assert.False(a.Made);
            // The leg is never stretched: every later contact is in the zone, on the foot in swing.
            for (int i = 0; i < 360; i++)
            {
                Step(w, Jog);
                foreach (var c in ContactsOf(w))
                {
                    Assert.InRange(c.ContactMiss, 0f, w.Tuning.Touch.ContactZoneRadius);
                    Assert.Equal(w.Attacker.SwingFoot, c.Foot);
                }
            }
            Assert.True(w.Attacker.InPossession);
        }
    }

    [Fact]
    public void AStickChangeBeyondTheReplanAngleReplansAndASmallerOneIsAnEarlyTurn()
    {
        var w = Lab(3);
        var a = RunUntilPhase(w, Jog, ActionPhase.Planned);
        Step(w, new TickIntent { MoveX = MathF.Cos(0.8f), MoveZ = MathF.Sin(0.8f) });   // 46 degrees: planned again as a turn
        Assert.Equal(ActionPhase.Done, a.Phase);
        Assert.False(a.Made);
        var e = w.Attacker.Action!;
        Assert.True(e.Pending);
        Assert.NotEqual(LegsMode.Hold, e.Legs);

        var v = Lab(4);
        var b = RunUntilPhase(v, Jog, ActionPhase.Planned);
        Step(v, new TickIntent { MoveX = MathF.Cos(2.1f), MoveZ = MathF.Sin(2.1f) });   // 120 degrees: dropped
        Assert.Equal(ActionPhase.Done, b.Phase);
        Assert.False(b.Made);
        Assert.NotSame(b, v.Attacker.Action);
    }

    [Theory]
    [InlineData("[]", "at least one")]
    [InlineData("[[30, 0.6], [10, 1]]", "strictly increasing")]
    [InlineData("null", "touch.early_execution")]
    public void AnInvalidEarlyExecutionCurveIsRejectedByTheLoader(string curve, string expected)
    {
        var r = DataLoader.ParseTuning($$"""{ "touch": { "early_execution": {{curve}} } }""", "t.json");
        Assert.False(r.Ok);
        Assert.Contains("t.json", r.Error);
        Assert.Contains(expected, r.Error);
        Assert.NotNull(r.Value);
    }

    static TickIntent Toward(float deg, float sprint = 0f)
    {
        float r = deg * MathF.PI / 180f;
        return new TickIntent { MoveX = MathF.Cos(r), MoveZ = MathF.Sin(r), Sprint = sprint };
    }

    static float Deg(float rad) => rad * 180f / MathF.PI;
    static float WrapDeg(float d) => Deg(Movement.WrapAngle(d * MathF.PI / 180f));

    /// <summary>
    /// Jogs straight until a touch is made whose next contact is at most <paramref name="maxEta"/> seconds away (a two-step jog
    /// push); returns that contact's distance in time.
    /// </summary>
    static float RunToPush(World w, float maxEta)
    {
        for (int i = 0; i < 240; i++) Step(w, Jog);
        for (int i = 0; i < 600; i++)
        {
            Step(w, Jog);
            if (ContactsOf(w).Any() && w.Attacker.Action is { Pending: true } n && n.ContactEta(w.Tick, w.Tuning.Dt) is var eta && eta <= maxEta) return eta;
        }
        throw new Exception("no push");
    }

    [Fact]
    public void ATurnOfThirtyFiveDegreesNeverHoldsTheLegsBeforeTheWindup()
    {
        foreach (bool shortPush in new[] { true, false })
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var w = Lab(seed, Exact());
            if (shortPush) RunToPush(w, 0.7f);
            else RunUntilPhase(w, Jog, ActionPhase.Planned);
            for (int i = 0; i < 120; i++)
            {
                Step(w, Toward(35f));
                if (w.Attacker.Action is { Pending: true } a && a.Phase != ActionPhase.Windup)
                    Assert.True(a.Legs != LegsMode.Hold, $"seed {seed}: legs held in {a.Phase}");
            }
            // The legs end up on the stick.
            Assert.InRange(WrapDeg(Deg(w.Attacker.Facing) - 35f), -5f, 5f);
            Assert.True(w.Attacker.InPossession);
        }
    }

    [Fact]
    public void AnEarlyTurnTouchFiresWithinTheBound()
    {
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var w = Lab(seed, Exact());
            float planned = RunToPush(w, 0.7f);
            float pushAt = w.Attacker.LastTouchTime;
            var before = w.Ball.Velocity;
            BallAction? turn = null;
            float at = float.NaN;
            for (int i = 0; i < 120 && turn == null; i++)
            {
                Step(w, Toward(35f));
                foreach (var c in ContactsOf(w)) { turn = c; at = w.Attacker.LastTouchTime; }
            }
            Assert.NotNull(turn);
            // No later than the push's planned meet (sooner when the ball is in reach at an earlier swing; after a two-step jog push
            // it rarely is), and never sooner than early_min_interval after it.
            Assert.InRange(at - pushAt, w.Tuning.Touch.EarlyMinInterval - 1e-4f, planned + 1e-4f);
            Assert.True(turn!.Kind is ActionKind.Turn or ActionKind.Cut, $"seed {seed}: {turn.Kind}");
            // The ball now heads toward the stick side.
            float change = WrapDeg(Deg(MathF.Atan2(turn.BallOutVelocity.Z, turn.BallOutVelocity.X)) - Deg(MathF.Atan2(before.Z, before.X)));
            Assert.True(change > 15f, $"seed {seed}: ball turned {change:F1} degrees");
        }
    }

    [Fact]
    public void EarlyTurnTouchesKeepTheMinimumIntervalUnderAFastWiggle()
    {
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var w = Lab(seed);
            for (int i = 0; i < 240; i++) Step(w, Jog);
            float last = float.NaN;
            int touches = 0;
            for (int i = 0; i < 6 * 120; i++)
            {
                Step(w, Toward((i / 12) % 2 == 0 ? 30f : -30f));   // a 60 degree flick every 0.1 s
                foreach (var c in ContactsOf(w))
                {
                    float now = w.Attacker.LastTouchTime;
                    if (!float.IsNaN(last))
                        Assert.True(now - last >= w.Tuning.Touch.EarlyMinInterval - 1e-4f, $"seed {seed}: touches {now - last:F3} s apart");
                    // Played, if stretched (the steering along the run reaches the ball through the speed law).
                    Assert.InRange(c.ContactMiss, 0f, MathF.Max(w.Tuning.Touch.ContactZoneRadius, w.Tuning.Touch.StretchReach));
                    last = now;
                    touches++;
                }
            }
            Assert.True(touches > 6, $"seed {seed}: {touches} touches");
            Assert.True(w.Attacker.InPossession);
        }
    }

    [Fact]
    public void RunningToTheBallNeverTurnsTheLegsMoreThanTheApproachLimitOffTheStick()
    {
        var t0 = new Tuning();
        float max = t0.Touch.ApproachMaxStickDeg;
        int bent = 0;
        foreach (float sprint in new[] { 0f, 1f })
        foreach (float turnBy in new[] { 20f, 35f })
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var w = Lab(seed);
            var p = w.Attacker;
            var go = new TickIntent { MoveX = 1, Sprint = sprint };
            RunUntilPhase(w, go, ActionPhase.Planned);
            for (int i = 0; i < 3 * 120; i++)
            {
                float stick = i < 120 ? turnBy : (i < 240 ? 0f : -turnBy);   // never more than turnBy from the ball's line
                Step(w, Toward(stick, sprint));
                if (p.Action is not { Pending: true } a) continue;
                float legs = stick;
                if (a.Legs == LegsMode.Stick && a.HoldDirection != Vector2.Zero) { legs = stick + Deg(MathF.Atan2(a.HoldDirection.Y, a.HoldDirection.X)); bent++; }
                else if (a.Legs == LegsMode.Approach)
                    legs = Deg(MathF.Atan2(w.Ball.Position.Z - p.Position.Z, w.Ball.Position.X - p.Position.X));
                else continue;
                Assert.True(MathF.Abs(WrapDeg(legs - stick)) <= max + 1f,
                    $"seed {seed} sprint {sprint} turn {turnBy}: legs {legs:F0} vs stick {stick:F0} ({a.Legs})");
            }
            Assert.True(p.InPossession);
        }
        _out.WriteLine($"{bent} ticks with the legs bent toward the ball");
    }

    // ---- smoothness (GOALS-parity pass) ----

    [Fact]
    public void TheSteeringNeverStepsTheVelocity()
    {
        foreach (var it in new[] { Jog, Sprint, Toward(45f), Toward(-45f, 1f) })
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var w = Lab(seed);
            var p = w.Attacker;
            Vector3 prevV = p.Velocity;
            float max = 0f;
            for (int i = 0; i < 6 * 120; i++)
            {
                // straight for 3 s, then the stick turned, as a keyboard does
                Step(w, i < 360 ? Jog with { Sprint = it.Sprint } : it);
                if (i > 120) max = MathF.Max(max, MathF.Abs(new Vector2(p.Velocity.X, p.Velocity.Z).Length() - new Vector2(prevV.X, prevV.Z).Length()));
                prevV = p.Velocity;
            }
            // The ground speed (running speed and steering together) never changes faster than 15 m/s^2 (a speed step): the
            // steering's part along the run goes through the speed law, its sideways part turns with the body.
            float limit = 15f * w.Tuning.Dt;
            Assert.True(max <= limit, $"seed {seed}: ground speed changed by {max:F3} m/s in a tick");
            Assert.True(p.InPossession);
        }
    }

    [Fact]
    public void AStraightJogKeepsAnEvenCadenceWithBothFeet()
    {
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var w = Lab(seed, Exact());
            for (int i = 0; i < 360; i++) Step(w, Jog);
            var ticks = new List<int>();
            int left = 0, right = 0;
            for (int i = 0; i < 8 * 120; i++)
            {
                Step(w, Jog);
                foreach (var c in ContactsOf(w)) { ticks.Add(w.Tick); if (c.Foot == Foot.Left) left++; else right++; }
            }
            Assert.True(left > 0 && right > 0, $"seed {seed}: feet {left}/{right}");
            var gaps = ticks.Zip(ticks.Skip(1), (a, b) => (b - a) * w.Tuning.Dt).ToList();
            // two steps (about 0.62 s) or, on every fourth touch, three (about 0.93 s): never a one-step push between them
            Assert.True(gaps.Min() > 0.5f, $"seed {seed}: shortest gap {gaps.Min():F2} s");
            Assert.True(gaps.Max() < 1.05f, $"seed {seed}: longest gap {gaps.Max():F2} s");
        }
    }

    [Fact]
    public void AStickPushedNoFurtherThanTheWalkLimitDribblesWithATouchEveryStep()
    {
        var w = Lab(1);
        var walk = new TickIntent { MoveX = 0.5f };
        for (int i = 0; i < 360; i++) Step(w, walk);
        var kinds = new List<ActionKind>();
        for (int i = 0; i < 4 * 120; i++) { Step(w, walk); kinds.AddRange(ContactsOf(w).Select(c => c.Kind)); }
        Assert.True(kinds.Count >= 10, $"{kinds.Count} touches in 4 s");
        // close-control touches (a foot-to-foot touch on every step sends the ball across by more than the execution angle: a turn)
        Assert.All(kinds, k => Assert.True(k is ActionKind.Close or ActionKind.Turn, k.ToString()));
        Assert.True(w.Attacker.InPossession);
    }

    [Fact]
    public void AJogTurnOfNinetyDegreesIsPlayedOnTheNextReachableSwing()
    {
        var times = new List<float>();
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var w = Lab(seed);
            var p = w.Attacker;
            int pre = 360 + (int)(seed % 10) * 8;   // the turn at every point of the cadence
            for (int i = 0; i < pre; i++) Step(w, Jog);
            float t90 = float.NaN;
            for (int i = 1; i <= 180 && float.IsNaN(t90); i++)
            {
                Step(w, Toward(90f));
                if (MathF.Abs(WrapDeg(Deg(MathF.Atan2(p.Velocity.Z, p.Velocity.X)) - 90f)) <= 9f) t90 = i * w.Tuning.Dt;
            }
            Assert.True(p.InPossession, $"seed {seed}: possession lost");
            times.Add(float.IsNaN(t90) ? 9f : t90);
        }
        times.Sort();
        Assert.True(times[times.Count / 2] <= 0.5f, $"median {times[times.Count / 2]:F2} s to 90% of the turn");
    }

    [Fact]
    public void ACutShedsItsSpeedOverSeveralTicks()
    {
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var w = Lab(seed);
            var p = w.Attacker;
            for (int i = 0; i < 360; i++) Step(w, Jog);
            float prev = p.RunSpeed, worst = 0f;
            bool cut = false;
            for (int i = 0; i < 240; i++)
            {
                Step(w, Toward(90f));
                cut |= ContactsOf(w).Any(c => c.Kind == ActionKind.Cut);
                worst = MathF.Max(worst, prev - p.RunSpeed);
                prev = p.RunSpeed;
            }
            Assert.True(cut, $"seed {seed}: no cut");
            // easing toward the target plus balance_deceleration, never the balance percentage at once
            Assert.True(worst <= (w.Tuning.Movement.BalanceDeceleration + 6f) * w.Tuning.Dt, $"seed {seed}: run speed fell {worst:F2} m/s in a tick");
        }
    }

    [Fact]
    public void NeutralStickDuringTheWindupBecomesAStopTouch()
    {
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var w = Lab(seed, dribbling: 60);
            var a = RunUntilPhase(w, Sprint, ActionPhase.Windup);
            // A release no longer than release_debounce still reads as the stick held (a flick through neutral).
            int debounce = (int)MathF.Round(w.Tuning.Touch.ReleaseDebounce * w.Tuning.TickRate);
            for (int i = 0; i <= debounce; i++) Step(w, default);
            Assert.Equal(ActionKind.Stop, a.Kind);
            for (int i = 0; i < 40 && !a.Made; i++) Step(w, default);
            Assert.True(a.Made, $"seed {seed}: the stop touch was not made");
            Assert.Equal(ActionKind.Stop, a.Kind);
            Assert.Equal(FootSurface.Sole, a.Surface);
            // A stop, not a forward push: the ball leaves slower than the player and comes to rest just ahead of him.
            Assert.True(new Vector2(a.BallOutVelocity.X, a.BallOutVelocity.Z).Length() < w.Attacker.Speed + 0.5f);
            for (int i = 0; i < 6 * 120; i++) Step(w, default);
            Assert.True(w.Ball.Velocity.Length() < 0.06f && w.Attacker.Speed < 0.1f);
            // Near where he stops: the stop aims 0.35 m ahead; the rated speed error may leave it a little short of his feet.
            float ahead = w.Ball.Position.X - w.Attacker.Position.X;
            Assert.InRange(ahead, -0.7f, 1.2f);
            Assert.True(w.Attacker.InPossession);
        }
    }

    [Fact]
    public void ASharpStopPicksAStopTouchAndNeverReachesBack()
    {
        int stops = 0;
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var w = Lab(seed);
            for (int i = 0; i < 3 * 120; i++) Step(w, Sprint);
            var kinds = new List<ActionKind>();
            for (int i = 0; i < 5 * 120; i++)
            {
                Step(w, default);
                foreach (var c in ContactsOf(w))
                {
                    kinds.Add(c.Kind);
                    // The ball is met ahead of the feet, never behind them (the run now slows no faster than
                    // balance_deceleration, so a stop can be met a little closer to the feet than before).
                    var fwd = new Vector2(MathF.Cos(w.Attacker.Facing), MathF.Sin(w.Attacker.Facing));
                    var rel = new Vector2(c.ContactPoint.X - w.Attacker.Position.X, c.ContactPoint.Z - w.Attacker.Position.Z);
                    Assert.True(Vector2.Dot(rel, fwd) > 0.05f, $"seed {seed}: contact {Vector2.Dot(rel, fwd):F2} m ahead");
                }
            }
            Assert.All(kinds, k => Assert.True(k is ActionKind.Stop or ActionKind.Slowdown, $"seed {seed}: {k} after the stick was released"));
            if (kinds.Contains(ActionKind.Stop)) stops++;
            Assert.True(w.Attacker.InPossession, $"seed {seed}: possession lost");
            Assert.True(w.Ball.Velocity.Length() < 0.06f, $"seed {seed}: ball still rolling");
            Assert.InRange(TouchModel.HorizontalDistance(w.Attacker.Position, w.Ball.Position), 0f, 1.3f);
        }
        _out.WriteLine($"{stops} of 30 sharp stops made a stop touch");
        Assert.Equal(30, stops);
    }

    [Fact]
    public void AShotPressedDuringAWindupWaitsForThatContactThenPlansTheSoftTouch()
    {
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var w = Lab(seed);
            var a = RunUntilPhase(w, Jog, ActionPhase.Windup);
            int contactTick = a.ContactTick;
            var press = new TickIntent { MoveX = 1, ShootHeld = true };
            BallAction? soft = null;
            for (int i = 0; i < 180 && soft == null; i++)
            {
                Step(w, press);
                foreach (var c in ContactsOf(w))
                {
                    if (c == a)
                    {
                        Assert.Equal(contactTick, w.Tick);
                        Assert.NotEqual(ActionKind.Soft, c.Kind);   // the touch already winding up is made as planned
                    }
                    else
                    {
                        Assert.True(a.Made, $"seed {seed}: a touch before the winding-up one");
                        Assert.Equal(ActionKind.Soft, c.Kind);
                        Assert.True(w.Tick > contactTick);
                        soft = c;
                    }
                }
            }
            Assert.NotNull(soft);
        }
    }

    [Fact]
    public void TheCurrentActionIsPublishedForTheView()
    {
        var w = Lab(1, Exact());
        var p = w.Attacker;
        int pending = 0, ticks = 0;
        for (int i = 0; i < 4 * 120; i++)
        {
            Step(w, Jog);
            if (!p.InPossession) continue;
            ticks++;
            if (p.Action is not { Pending: true } a) continue;
            pending++;
            Assert.Equal((a.ContactTick - w.Tick) * w.Tuning.Dt, p.ActionContactEta, 4);
            Assert.Equal(p.ActionContactEta, p.NextTouchEta, 4);
            Assert.Equal(a.Foot, p.NextTouchFoot);
            Assert.Equal(a.ContactPoint, p.NextTouchPoint);
            Assert.Equal(a.PhaseAt(w.Tick, TouchPlanner.PreWindupTicks(w.Tuning), TouchPlanner.WindupTicks(w.Tuning)), a.Phase);
            if (p.PreviousAction is { } prev) Assert.True(prev.Made && prev.Phase is ActionPhase.Contact or ActionPhase.Recovery);
        }
        Assert.True(pending > 0.95 * ticks, $"{pending} of {ticks} ticks had a planned touch");
    }
}
