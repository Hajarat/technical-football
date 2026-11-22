using System.Numerics;
using FootballSim.Core;
using Xunit.Abstractions;

namespace FootballSim.Core.Tests;

/// <summary>The touch controller: committed touches, stop touches, turns, smoothness, and a shot pressed during a windup.</summary>
public class TouchControllerTests
{
    readonly ITestOutputHelper _out;

    public TouchControllerTests(ITestOutputHelper output) { _out = output; }

    static Tuning Exact()
    {
        var t = new Tuning();
        t.Touch.SpeedError = new RatingRange(0f, 0f);
        t.Touch.DirectionErrorDeg = new RatingRange(0f, 0f);
        return t;
    }

    static World Lab(ulong seed, Tuning? t = null, int dribbling = 70, float x = 2f)
    {
        var s = TestData.Sheet(75);
        s.Dribbling = dribbling;
        var sc = new Scenario
        {
            Id = "lab",
            Pitch = new PitchSpec { Length = 300f, Width = 200f },
            Ball = new StartSpot { X = x + 0.5f },
            Attacker = new StartSpot { X = x },
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
                    // Played within the reach of the body (a turn touch may play the ball anywhere within the turn reach of it).
                    Assert.InRange(c.ContactMiss, 0f, w.Tuning.Touch.FeetTurnReach);
                    last = now;
                    touches++;
                }
            }
            Assert.True(touches > 6, $"seed {seed}: {touches} touches");
            Assert.True(w.Attacker.InPossession);
        }
    }

    // ---- smoothness (smoothness) ----

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
    public void AReleasedStickBeforeTheCommitIsPlayedAsAStopTouch()
    {
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var w = Lab(seed, dribbling: 60);
            for (int i = 0; i < 240; i++) Step(w, Sprint);
            // Released while no touch is committed (between touches): the next touch is a stop.
            for (int i = 0; i < 600 && w.Attacker.Action is { Pending: true }; i++) Step(w, Sprint);
            BallAction? stop = null;
            for (int i = 0; i < 240 && stop == null; i++)
            {
                Step(w, default);
                foreach (var c in ContactsOf(w)) stop = c;
            }
            Assert.NotNull(stop);
            Assert.Equal(ActionKind.Stop, stop!.Kind);
            Assert.Equal(FootSurface.Sole, stop.Surface);
            // A stop, not a forward push: the ball leaves slower than the player and comes to rest just ahead of him.
            Assert.True(new Vector2(stop.BallOutVelocity.X, stop.BallOutVelocity.Z).Length() < w.Attacker.Speed + 0.5f);
            for (int i = 0; i < 6 * 120; i++) Step(w, default);
            Assert.True(w.Ball.Velocity.Length() < 0.06f && w.Attacker.Speed < 0.1f);
            float ahead = w.Ball.Position.X - w.Attacker.Position.X;
            Assert.InRange(ahead, -0.7f, 1.2f);
            Assert.True(w.Attacker.InPossession);
        }
    }

    [Fact]
    public void ACommittedTouchKeepsItsFootSurfaceAndKindToItsContact()
    {
        // The view swings the committed surface of the boot onto the ball over the windup: nothing of it may change on the way.
        int touches = 0;
        foreach (var stick in new Func<int, TickIntent>[]
        {
            i => Jog, i => Sprint, i => Toward((i / 50) % 2 == 0 ? 45f : -45f), i => Toward((i / 37) % 3 * 70f, (i / 200) % 2),
            i => (i / 90) % 2 == 0 ? Sprint : default, i => Toward((i / 13) % 2 == 0 ? 30f : -60f) with { KnockOn = i % 300 == 150 },
        })
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var w = Lab(seed);
            BallAction? watched = null;
            (Foot, FootSurface, ActionKind) at = default;
            for (int i = 0; i < 8 * 120; i++)
            {
                Step(w, stick(i));
                var a = w.Attacker.TouchAction;
                if (a is { Pending: true } && a != watched) { watched = a; at = (a.Foot, a.Surface, a.Kind); }
                if (watched is { Made: true } m && m.ContactTick == w.Tick)
                {
                    Assert.Equal(at, (m.Foot, m.Surface, m.Kind));
                    touches++;
                    watched = null;
                }
                else if (watched is { Pending: true } pw) Assert.Equal(at, (pw.Foot, pw.Surface, pw.Kind));
            }
            Assert.True(w.Attacker.InPossession, $"seed {seed}: possession lost");
        }
        Assert.True(touches > 300, $"{touches} committed touches");
    }

    [Fact]
    public void APlayerInPossessionIsAlwaysBackOnTheBallWithinOneAndAHalfSeconds()
    {
        // The possession invariant: no push is faster or longer than he can catch, and a ball out of reach at a swing is gone
        // after by the legs, so contacts never stop for longer than 1.5 s while the stick is pushed, at any dribbling.
        foreach (int dribbling in new[] { 1, 50, 99 })
        foreach (var (stick, si) in new Func<int, TickIntent>[]
        {
            i => Sprint, i => i < 360 ? Sprint : Toward(90f, 1f), i => i < 360 ? Sprint : Toward(180f, 1f),
            i => Toward((i / 96) % 2 == 0 ? 45f : -45f), i => Toward((i / 60) * 50f, (i / 150) % 2),
        }.Select((f, n) => (f, n)))
        for (ulong seed = 1; seed <= 6; seed++)
        {
            var w = Lab(seed, dribbling: dribbling, x: 150f);   // mid-pitch: the back wall is at X = 0
            int last = -1, unplayedAt = -1, stepsSince = 0, reversal = -1000;
            float jog = w.Tuning.Movement.JogSpeed * w.Tuning.Touch.TopSpeedWithBall.Close.ForRating(dribbling);
            for (int i = 0; i < 8 * 120; i++)
            {
                var waiting = w.Attacker.TouchAction is { Pending: true } pw ? pw : null;
                var it = stick(i);
                // A reversal (the stick 120 degrees or more off the ball's travel) above jog speed may take up to 2 s to its contact.
                var bv = new Vector2(w.Ball.Velocity.X, w.Ball.Velocity.Z);
                if (it.Move.Length() > 0.5f && bv.Length() > 0.3f && w.Attacker.Speed > jog * 1.05f
                    && MathF.Abs(Movement.WrapAngle(MathF.Atan2(it.MoveZ, it.MoveX) - MathF.Atan2(bv.Y, bv.X))) >= 120f * MathF.PI / 180f)
                    reversal = w.Tick;
                Step(w, it);
                if (waiting is { Made: false, Phase: ActionPhase.Done } && w.Tick >= waiting.ContactTick) { unplayedAt = w.Tick; stepsSince = 0; }
                if (unplayedAt > 0 && w.Attacker.StepBegun) stepsSince++;
                if (ContactsOf(w).Any() || w.Attacker.SkillMove != null) { last = w.Tick; unplayedAt = -1; }
                // A committed touch not played is followed by a played contact within two steps.
                Assert.True(unplayedAt < 0 || stepsSince <= 3, $"dribbling {dribbling} seed {seed}: no contact two steps after a touch not played at {unplayedAt}");
                float limit = last <= reversal ? 2.0f : 1.5f;
                if (last > 0 && i > 120) Assert.True((w.Tick - last) * w.Tuning.Dt <= limit, $"dribbling {dribbling} stick {si} seed {seed}: no contact for {(w.Tick - last) * w.Tuning.Dt:F2} s at tick {w.Tick}, ball {TouchModel.HorizontalDistance(w.Attacker.Position, w.Ball.Position):F2} m at {w.Ball.Velocity.Length():F2} m/s, player {w.Attacker.Speed:F2} m/s, chasing {w.Attacker.Chasing}, eta {w.Attacker.NextTouchEta:F2}");
            }
            Assert.True(w.Attacker.InPossession);
        }
    }

    [Fact]
    public void AStickTurnAtAnyMomentOfASprintPushIsPlayedWithinTheLatencyBound()
    {
        foreach (int dribbling in new[] { 1, 50, 99 })
        {
            var lat = new List<float>();
            for (ulong seed = 1; seed <= 4; seed++)
            for (int at = 0; at < 120; at += 12)
            {
                var w = Lab(seed, dribbling: dribbling);
                for (int i = 0; i < 360 + at; i++) Step(w, Sprint);
                var before = w.Ball.Velocity;
                float t = float.NaN;
                for (int i = 1; i <= 240 && float.IsNaN(t); i++)
                {
                    Step(w, Toward(90f, 1f));
                    foreach (var c in ContactsOf(w))
                        if (c.Kind is ActionKind.Turn or ActionKind.Cut) t = i * w.Tuning.Dt;
                }
                Assert.True(w.Attacker.InPossession);
                lat.Add(float.IsNaN(t) ? 9f : t);
            }
            lat.Sort();
            float p90 = lat[(int)(0.9 * lat.Count)];
            _out.WriteLine($"dribbling {dribbling}: turn touch p50 {lat[lat.Count / 2]:F2} p90 {p90:F2} s");
            // The dispatch's sprint-row bounds (1.0 / 0.8 / 0.6 s at dribbling 1 / 50 / 99) with one more swing of slack.
            float bound = dribbling == 1 ? 1.25f : dribbling == 50 ? 1.05f : 0.85f;
            Assert.True(p90 <= bound, $"dribbling {dribbling}: p90 {p90:F2} s");
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
            // A touch already committed when the stick is let go is played as committed; every touch after it is a stop.
            var committed = w.Attacker.Action is { Pending: true } pc ? pc : null;
            int release = w.Tick, debounce = (int)MathF.Round(w.Tuning.Touch.ReleaseDebounce * w.Tuning.TickRate);
            var kinds = new List<ActionKind>();
            for (int i = 0; i < 5 * 120; i++)
            {
                Step(w, default);
                foreach (var c in ContactsOf(w))
                {
                    // ... and so is one committed while a release no longer than release_debounce still reads as the stick.
                    if (c == committed || c.PlanTick <= release + debounce) continue;
                    kinds.Add(c.Kind);
                    // The ball is met ahead of the feet, never behind them.
                    var fwd = new Vector2(MathF.Cos(w.Attacker.Facing), MathF.Sin(w.Attacker.Facing));
                    var rel = new Vector2(c.ContactPoint.X - w.Attacker.Position.X, c.ContactPoint.Z - w.Attacker.Position.Z);
                    Assert.True(Vector2.Dot(rel, fwd) > 0.1f, $"seed {seed}: contact {Vector2.Dot(rel, fwd):F2} m ahead");
                }
            }
            Assert.All(kinds, k => Assert.True(k == ActionKind.Stop, $"seed {seed}: {k} after the stick was released"));
            if (kinds.Contains(ActionKind.Stop)) stops++;
            Assert.True(w.Attacker.InPossession, $"seed {seed}: possession lost");
            Assert.True(w.Ball.Velocity.Length() < 0.06f, $"seed {seed}: ball still rolling");
            Assert.True(w.Attacker.Speed < 0.1f, $"seed {seed}: still moving");
            Assert.InRange(TouchModel.HorizontalDistance(w.Attacker.Position, w.Ball.Position), 0f, 1.3f);
        }
        _out.WriteLine($"{stops} of 30 sharp stops made a stop touch");
        Assert.Equal(30, stops);
    }

    [Fact]
    public void StickAndSprintReleasedTogetherEndInAPlayedStopTouch()
    {
        for (ulong seed = 21; seed <= 40; seed++)
        foreach (int dribbling in new[] { 1, 99 })
        {
            var w = Lab(seed, dribbling: dribbling);
            for (int i = 0; i < 300 + 5 * (int)seed; i++) Step(w, Sprint);
            BallAction? stop = null;
            for (int i = 0; i < 6 * 120; i++)
            {
                Step(w, default);
                foreach (var c in ContactsOf(w)) if (c.Kind == ActionKind.Stop) stop ??= c;
            }
            Assert.True(stop != null, $"seed {seed} dribbling {dribbling}: no stop touch (ball {TouchModel.HorizontalDistance(w.Attacker.Position, w.Ball.Position):F2} m, speed {w.Attacker.Speed:F2})");
            Assert.True(stop!.Made);
            // Stopped, or stepping up to a ball that settled a little off (at dribbling 1 the stop's rated error can leave it so).
            Assert.True(w.Attacker.InPossession && w.Attacker.Speed < 0.3f, $"seed {seed} dribbling {dribbling}: speed {w.Attacker.Speed:F2}");
            Assert.InRange(TouchModel.HorizontalDistance(w.Attacker.Position, w.Ball.Position), 0f, 1.3f);
        }
    }

    [Fact]
    public void AStraightJogAtDribblingOneNeverDipsUnderEightyFivePercent()
    {
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var w = Lab(seed, dribbling: 1);
            var p = w.Attacker;
            float pace = w.Tuning.Movement.JogSpeed * w.Tuning.Touch.TopSpeedWithBall.Close.ForRating(1);
            bool atSpeed = false;
            for (int i = 0; i < 10 * 120; i++)
            {
                Step(w, Jog);
                if (i < 120 || !(atSpeed |= p.Speed >= 0.95f * pace)) continue;
                Assert.True(p.Speed >= 0.85f * pace - 1e-3f, $"seed {seed} tick {w.Tick}: {p.Speed:F2} m/s of {pace:F2}");
            }
            Assert.True(p.InPossession);
        }
    }

    [Fact]
    public void TheGroundSpeedStaysContinuousThroughACut()
    {
        for (ulong seed = 1; seed <= 10; seed++)
        foreach (var it in new[] { Jog, Sprint })
        {
            var w = Lab(seed);
            var p = w.Attacker;
            for (int i = 0; i < 360; i++) Step(w, it);
            float prev = p.Speed, worst = 0f;
            bool cut = false;
            for (int i = 0; i < 240; i++)
            {
                Step(w, Toward(100f, it.Sprint));
                cut |= ContactsOf(w).Any(c => c.Kind == ActionKind.Cut);
                if (p.SkillMove == null) worst = MathF.Max(worst, MathF.Abs(p.Speed - prev));
                prev = p.Speed;
            }
            Assert.True(cut || p.SkillMove != null, $"seed {seed}: no cut");
            Assert.True(worst <= 15f * w.Tuning.Dt, $"seed {seed}: ground speed changed {worst / w.Tuning.Dt:F1} m/s^2 in a tick");
        }
    }

    /// <summary>
    /// Shoot pressed while a touch winds up: that touch is made as committed, no other touch is made after it, and the strike
    /// is the next contact, on the foot swinging in its contact window. (Until 2026-11-19 a soft touch followed the committed
    /// one; the strike now takes the place of the next touch.)
    /// </summary>
    [Fact]
    public void AShotPressedDuringAWindupPlaysThatTouchThenStrikesAtTheNextContact()
    {
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var w = Lab(seed);
            var a = RunUntilPhase(w, Jog, ActionPhase.Windup);
            int contactTick = a.ContactTick;
            var p = w.Attacker;
            for (int i = 0; i < 360 && w.Shots.Count == 0; i++)
            {
                Step(w, new TickIntent { MoveX = 1, ShootHeld = i < 24 });
                foreach (var c in ContactsOf(w))
                {
                    Assert.Same(a, c);                              // no touch but the one already winding up
                    Assert.Equal(contactTick, w.Tick);
                    Assert.NotEqual(ActionKind.Soft, c.Kind);
                }
                if (w.Events.FirstOrDefault(e => e.Kind == EventKind.Strike) is { Kind: EventKind.Strike } strike)
                {
                    Assert.True(a.Made, $"seed {seed}: struck before the committed touch");
                    Assert.True(TouchPlanner.FootInWindow(p, strike.Foot), $"seed {seed}: struck off the swinging foot's window");
                }
            }
            Assert.Single(w.Shots);
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
            Assert.False(float.IsNaN(p.NextTouchEta) && p.Action is { Pending: true });
            if (!float.IsNaN(p.NextTouchEta)) pending++;
            if (p.Action is not { Pending: true } a) continue;
            Assert.Equal((a.ContactTick - w.Tick) * w.Tuning.Dt, p.ActionContactEta, 4);
            Assert.Equal(p.ActionContactEta, p.NextTouchEta, 4);
            Assert.Equal(a.Foot, p.NextTouchFoot);
            Assert.Equal(a.ContactPoint, p.NextTouchPoint);
            Assert.Equal(a.PhaseAt(w.Tick, TouchPlanner.WindupTicks(w.Tuning), TouchPlanner.WindupTicks(w.Tuning)), a.Phase);
            // A touch exists only from its commit, one windup before the contact.
            Assert.Equal(ActionPhase.Windup, a.Phase);
            Assert.InRange(a.ContactTick - a.PlanTick, 0, TouchPlanner.WindupTicks(w.Tuning));
            if (p.PreviousAction is { } prev) Assert.True(prev.Made && prev.Phase is ActionPhase.Contact or ActionPhase.Recovery);
        }
        // The next contact is forecast (committed, or the swing the controller steers for) nearly all the time.
        Assert.True(pending > 0.95 * ticks, $"{pending} of {ticks} ticks had a forecast touch");
    }
}
