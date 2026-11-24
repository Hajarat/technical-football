using System.Numerics;
using FootballSim.Core;
using Xunit;

namespace FootballSim.Core.Tests;

/// <summary>
/// The containing defender bot (tuning defender.brain = contain): his spot, the drop, the gap he keeps, running with the
/// carrier, the triggers that send him for the ball, the cut-off point when beaten, the awkward inputs (a still carrier, one
/// running away, the ball on the goal line), no button or mode change while he is not free, a pass struck as he goes in, and
/// the keeper's yield.
/// </summary>
public class DefenderContainTests
{
    static readonly Tuning T = new();
    static readonly Scenario Duel = TestData.Scenario("defender_duel");
    static readonly PlayerSheet Striker = TestData.Player("striker_a");
    static readonly PlayerSheet Pace = TestData.Player("pace_star");
    static readonly PlayerSheet Wall = TestData.Player("the_wall");
    static readonly PlayerSheet CentreBack = TestData.Player("centre_back");
    static readonly Pitch DuelPitch = new(Duel.Pitch, T.Frame, T.Arena);
    static Vector2 Goal => new(DuelPitch.GoalLineX, 0f);
    static Vector2 G(Vector3 v) => new(v.X, v.Z);

    /// <summary>The shipped tuning with the triggers that send him for the ball switched off, to watch Contain and Run with alone.</summary>
    static readonly Tuning NoWin = new() { Defender = new DefenderTuning { WinMarginS = new Curve((1f, 50f), (99f, 50f)), PressSpeed = 0f } };

    static World Live(PlayerSheet? attacker = null, ulong seed = 1, Tuning? tuning = null)
    {
        var w = new World(tuning ?? T, Duel, attacker ?? Striker, Wall, seed, CentreBack);
        w.Begin();
        return w;
    }

    static void Step(World w, TickIntent attacker = default, int ticks = 1)
    {
        var buf = new TickIntent[w.PlayerCount];
        buf[0] = attacker;
        for (int i = 0; i < ticks; i++) w.Step(buf);
    }

    static void GiveAttackerTheBall(World w)
    {
        for (int i = 0; i < 30 && w.BallController != w.Attacker; i++) Step(w, new TickIntent { MoveX = 1f });
        Assert.Same(w.Attacker, w.BallController);
    }

    /// <summary>Puts the carrier, still, at (<paramref name="x"/>, <paramref name="z"/>) facing the goal, with the ball at his feet.</summary>
    static void Place(World w, float x, float z)
    {
        var a = w.Attacker;
        a.Position = new Vector3(x, 0f, z);
        a.Velocity = Vector3.Zero;
        a.Facing = 0f;
        w.Ball.Position = new Vector3(x + 0.45f, T.Ball.Radius, z);
        w.Ball.Velocity = Vector3.Zero;
    }

    [Fact]
    public void TheShippedTuningPlaysTheContainingBot()
    {
        Assert.Equal("contain", T.Defender.Brain);
        Assert.False(Live().DefenderBot!.Legacy);
        var legacy = new World(new Tuning { Defender = new DefenderTuning { Brain = "legacy" } }, Duel, Striker, Wall, 1, CentreBack);
        legacy.Begin();
        Assert.True(legacy.DefenderBot!.Legacy);
    }

    // ---- the spot ----

    [Theory]
    [InlineData(25f)]
    [InlineData(15f)]
    [InlineData(8f)]
    public void TheSpotIsOnTheBallsLineGapGoalSideClampedToTheEngageAndLastLines(float fromGoal)
    {
        var k = T.Defender;
        float gap = DefenderBot.GapFor(CentreBack, 0f, T);
        var ball = Goal - new Vector2(fromGoal, 0f);
        var outside = DefenderBot.SpotFor(ball, fromGoal, gap, true, DuelPitch, T);
        float expect = Math.Clamp(fromGoal - gap, k.LastLineM, k.EngageLineM);
        Assert.Equal(expect, Vector2.Distance(outside, Goal), 3);
        Assert.Equal(0f, outside.Y, 3);   // on the long centre line there is nothing to shade toward
        // Inside the gap the engage line no longer holds him; the last line still does.
        var inside = DefenderBot.SpotFor(ball, fromGoal, gap, false, DuelPitch, T);
        Assert.Equal(MathF.Max(fromGoal - gap, k.LastLineM), Vector2.Distance(inside, Goal), 3);
        if (fromGoal == 25f) Assert.Equal(k.EngageLineM, Vector2.Distance(outside, Goal), 3);
        if (fromGoal == 8f) Assert.Equal(k.LastLineM, Vector2.Distance(outside, Goal), 3);
    }

    [Fact]
    public void TheSpotIsShadedTowardTheLongCentreLine()
    {
        float gap = DefenderBot.GapFor(CentreBack, 0f, T);
        var ball = new Vector2(Goal.X - 14f, 5f);
        float bd = Vector2.Distance(ball, Goal);
        var spot = DefenderBot.SpotFor(ball, bd, gap, false, DuelPitch, T);
        var u = Vector2.Normalize(Goal - ball);
        var rel = spot - ball;
        Assert.Equal(T.Defender.ShadeM, MathF.Abs(rel.X * u.Y - rel.Y * u.X), 3);
        Assert.True(spot.Y < ball.Y + u.Y * Vector2.Dot(rel, u), "the shade goes toward z = 0");
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(0.6f, 0.5f, 0.3f)]
    [InlineData(0.2f, 3f, 0.1f)]
    public void WithTheBallOnTheGoalCentreOrTheCarrierOnTheGoalLineTheSpotStaysFiniteAndInside(float back, float z, float carrier)
    {
        var ball = new Vector2(Goal.X - back, z);
        foreach (bool outside in new[] { true, false })
        {
            var spot = DefenderBot.SpotFor(ball, carrier, DefenderBot.GapFor(CentreBack, 8f, T), outside, DuelPitch, T);
            Assert.True(float.IsFinite(spot.X) && float.IsFinite(spot.Y));
            Assert.InRange(spot.X, DuelPitch.MinX, DuelPitch.GoalLineX);
            Assert.InRange(spot.Y, -DuelPitch.HalfWidth, DuelPitch.HalfWidth);
            Assert.True(Vector2.Distance(spot, Goal) >= T.Defender.LastLineM - T.Defender.ShadeM - 0.4f);
        }
    }

    [Fact]
    public void TheGapGrowsOnlyWithPaceBeyondHisSprintJockeyAndIsCapped()
    {
        float fast = Movement.JockeyTopSpeed(CentreBack, 1f, T);
        float g0 = DefenderBot.GapFor(CentreBack, 0f, T);
        Assert.Equal(T.Defender.ContainGapM.ForRating(CentreBack.Jockeying), g0, 4);
        Assert.Equal(g0, DefenderBot.GapFor(CentreBack, fast, T), 4);
        Assert.Equal(g0 + T.Defender.GapPaceS, DefenderBot.GapFor(CentreBack, fast + 1f, T), 3);
        Assert.Equal(T.Defender.GapMaxM, DefenderBot.GapFor(CentreBack, fast + 50f, T), 4);
    }

    // ---- drop, contain, run with ----

    [Fact]
    public void DroppingHeNeverSprintsUpThePitch()
    {
        // Deep near his goal with the carrier far off and jogging away (too quick for the slow trigger): his spot is up the
        // pitch, and he goes there only at the jockey pace.
        var w = Live();
        GiveAttackerTheBall(w);
        var d = w.Defender!;
        d.Position = new Vector3(DuelPitch.GoalLineX - 5f, 0f, 1f);
        float slow = Movement.JockeyTopSpeed(CentreBack, 0f, T);
        int drop = 0;
        for (int i = 0; i < 240; i++)
        {
            float before = Vector2.Distance(G(d.Position), Goal);
            Step(w, new TickIntent { MoveX = -0.7f });
            if (w.DefenderBot!.Mode != DefenderMode.Drop) continue;
            drop++;
            float away = (Vector2.Distance(G(d.Position), Goal) - before) / T.Dt;
            if (away > 0.05f)
            {
                Assert.True(d.Jockeying, "moving up the pitch he jockeys");
                Assert.True(away <= slow + 0.05f, $"up the pitch at {away:F2} m/s, faster than the jockey pace {slow:F2}");
            }
        }
        Assert.True(drop > 100, $"only {drop} ticks of dropping");
        Assert.True(Vector2.Distance(G(d.Position), Goal) > 10f, $"he did go up to his spot ({Vector2.Distance(G(d.Position), Goal):F2} m, spot {Vector2.Distance(w.DefenderBot!.HoldSpot, Goal):F2}, mode {w.DefenderBot.Mode}, carrier {Vector2.Distance(G(w.Attacker.Position), Goal):F2})");
    }

    /// <summary>Drives the carrier at goal at about <paramref name="speed"/> m/s (a little feedback on the stick, no sprint).</summary>
    static TickIntent Carry(World w, float speed, ref float mag)
    {
        mag = Math.Clamp(mag + 0.02f * (speed - w.Attacker.Speed), 0.05f, 1f);
        var to = Vector2.Normalize(Goal - G(w.Attacker.Position));
        return new TickIntent { MoveX = to.X * mag, MoveZ = to.Y * mag, Sprint = speed > 4f ? 0.6f : 0f };
    }

    [Theory]
    [InlineData(3f)]
    [InlineData(5f)]
    public void ContainingHeHoldsTheGapWithinHalfAMetre(float speed)
    {
        var w = Live(tuning: NoWin);
        GiveAttackerTheBall(w);
        var d = w.Defender!;
        Place(w, 6f, 0f);
        float mag = speed / 7f;
        // The carrier gets up to his pace with the defender out of the way; then the defender is put on his spot, at the
        // carrier's pace, and left to contain.
        for (int i = 0; i < 240; i++)
        {
            d.Position = new Vector3(DuelPitch.GoalLineX - 1f, 0f, DuelPitch.HalfWidth - 1f);
            Step(w, Carry(w, speed, ref mag));
        }
        Assert.InRange(w.Attacker.Speed, speed - 0.5f, speed + 0.5f);
        float gap0 = DefenderBot.GapFor(CentreBack, w.Attacker.Speed, T);
        var spot = DefenderBot.SpotFor(G(w.Ball.Position), Vector2.Distance(G(w.Attacker.Position), Goal), gap0, false, DuelPitch, T);
        d.Position = new Vector3(spot.X, 0f, spot.Y);
        d.Velocity = w.Attacker.Velocity;
        d.Facing = MathF.PI;
        int held = 0;
        for (int i = 0; i < 240 && w.State == RoundState.Live && w.BallController == w.Attacker; i++)
        {
            Step(w, Carry(w, speed, ref mag));
            float gap = DefenderBot.GapFor(CentreBack, w.Attacker.Speed, T);
            float depth = Vector2.Distance(G(w.Attacker.Position), Goal) - Vector2.Distance(G(d.Position), Goal);
            if (Vector2.Distance(G(d.Position), Goal) <= T.Defender.LastLineM + 0.5f) break;   // (his last line: no more ground)
            Assert.Equal(DefenderMode.Contain, w.DefenderBot!.Mode);
            Assert.InRange(depth, gap - 0.5f, gap + 0.5f);
            held++;
        }
        Assert.True(held >= 60, $"only {held} ticks of containing");
    }

    [Fact]
    public void AgainstACarrierAtSevenAndAHalfMetresASecondHeRunsWithHim()
    {
        var w = Live(Pace, tuning: NoWin);
        GiveAttackerTheBall(w);
        var d = w.Defender!;
        var run = new TickIntent { MoveX = 1f, Sprint = 1f };
        // Up to speed first, with the defender held out of the way in a corner, then he is put in front, inside the gap.
        for (int i = 0; i < 360 && w.Attacker.Speed < 7.5f; i++)
        {
            d.Position = new Vector3(DuelPitch.GoalLineX - 1f, 0f, DuelPitch.HalfWidth - 1f);
            Step(w, run);
        }
        Assert.True(w.Attacker.Speed >= 7.5f, $"the carrier reached only {w.Attacker.Speed:F2} m/s");
        Assert.Same(w.Attacker, w.BallController);
        var a = G(w.Attacker.Position);
        // (in front, inside the gap, already backing off at his jockey pace)
        d.Position = new Vector3(a.X + 3f, 0f, 0f);
        d.Velocity = new Vector3(Movement.JockeyTopSpeed(CentreBack, 0f, T), 0f, 0f);
        d.Facing = MathF.PI;
        bool runWith = false;
        for (int i = 0; i < 90 && w.BallController == w.Attacker && !runWith; i++)
        {
            Step(w, run);
            runWith = w.DefenderBot!.Mode == DefenderMode.RunWith;
        }
        Assert.True(runWith, "he never ran with the carrier");
    }

    // ---- the triggers ----

    [Fact]
    public void TheLooseTriggerFiresForASprintPushOfAboutAMetreAndNotForACloseControlTouch()
    {
        float margin = T.Defender.WinMarginS.Evaluate(CentreBack.Awareness);
        // A close-control walk with him three metres goal-side: never his.
        var w = Live();
        GiveAttackerTheBall(w);
        var d = w.Defender!;
        var walk = new TickIntent { MoveX = 0.4f, CloseControl = true };
        // Two seconds of close control first, with the defender out of the way, so the first touch's push has died away.
        for (int i = 0; i < 240; i++)
        {
            d.Position = new Vector3(DuelPitch.GoalLineX - 1f, 0f, DuelPitch.HalfWidth - 1f);
            Step(w, walk);
        }
        for (int i = 0; i < 120 && w.BallController == w.Attacker; i++)
        {
            var at = G(w.Attacker.Position) + new Vector2(3f, 0f);
            d.Position = new Vector3(at.X, 0f, at.Y);
            d.Velocity = Vector3.Zero;
            Step(w, walk);
            Assert.False(w.DefenderBot!.LooseRace(w, d, w.Attacker, margin, out _), $"a close-control touch read as loose at tick {w.Tick}");
        }
        // A sprint dribble at him: each push runs the ball on ahead of the carrier until his next touch, and he gets there first.
        var s = Live();
        GiveAttackerTheBall(s);
        var sd = s.Defender!;
        int fired = 0;
        float lead = 0f;
        for (int i = 0; i < 240 && s.BallController == s.Attacker; i++)
        {
            Step(s, new TickIntent { MoveX = 1f, Sprint = 1f });
            var a = G(s.Attacker.Position);
            float ahead = G(s.Ball.Position).X - a.X;
            lead = MathF.Max(lead, ahead);
            var at = a + new Vector2(3f, 0f);
            sd.Position = new Vector3(at.X, 0f, at.Y);
            sd.Velocity = Vector3.Zero;
            if (ahead >= 0.8f && s.DefenderBot!.LooseRace(s, sd, s.Attacker, margin, out _)) fired++;
        }
        Assert.True(lead >= 0.8f, $"the pushes reached only {lead:F2} m ahead");
        Assert.True(fired > 0, "a sprint push was never loose enough");
    }

    [Fact]
    public void ACarrierStandingStillAtTwentyMetresGetsNoCreepPastTheEngageLineThenTheSlowTriggerSendsHimAfterTheFarWait()
    {
        var w = Live();
        GiveAttackerTheBall(w);
        var a = w.Attacker;
        var d = w.Defender!;
        Place(w, DuelPitch.GoalLineX - 20f, 0f);
        d.Position = new Vector3(DuelPitch.GoalLineX - 10f, 0f, 0f);
        float engage = T.Defender.EngageLineM;
        int sentAt = -1;
        for (int i = 0; i < 600 && w.State == RoundState.Live; i++)
        {
            Step(w);
            Assert.Same(a, w.BallController);
            if (w.DefenderBot!.WinsSlow > 0) { sentAt = i; break; }
            Assert.True(Vector2.Distance(G(d.Position), Goal) <= engage + 0.3f, "crept past the engage line before the slow trigger");
        }
        // (20 m out is beyond his engage line and his gap: the far wait, press_far_after_s, not press_after_s)
        Assert.InRange(sentAt * T.Dt, T.Defender.PressFarAfterS - 0.1f, T.Defender.PressFarAfterS + 0.1f);
        Assert.Equal(DefenderMode.Win, w.DefenderBot!.Mode);
        // Sent, he goes out to the carrier.
        float before = Vector2.Distance(G(d.Position), G(a.Position));
        Step(w, default, 60);
        Assert.True(w.BallController != a || Vector2.Distance(G(d.Position), G(a.Position)) < before - 1f);
    }

    [Fact]
    public void ACarrierRunningBackToHisOwnEndDrawsHimNoFurtherThanTheEngageLine()
    {
        var w = Live();
        GiveAttackerTheBall(w);
        var a = w.Attacker;
        var d = w.Defender!;
        Place(w, DuelPitch.GoalLineX - 14f, 0f);
        float engage = T.Defender.EngageLineM;
        for (int i = 0; i < 360 && w.State == RoundState.Live && w.BallController == a; i++)
        {
            Step(w, new TickIntent { MoveX = -1f, Sprint = 1f });
            if (w.DefenderBot!.Mode is DefenderMode.Drop or DefenderMode.Contain)
                Assert.True(Vector2.Distance(G(d.Position), Goal) <= engage + 0.3f, $"followed him out to {Vector2.Distance(G(d.Position), Goal):F2} m");
        }
        Assert.Equal(0, w.DefenderBot!.WinsSlow);
    }

    // ---- beaten ----

    [Theory]
    [InlineData(20f, 3f, 17f, 0f)]
    [InlineData(24f, -6f, 22f, -2f)]
    [InlineData(15f, 0f, 12f, 1f)]
    public void TheCutOffPointIsGoalSideOfTheCarrierOnHisWayToGoal(float cx, float cz, float dx, float dz)
    {
        var carrier = new Vector2(cx, cz);
        var self = new Vector2(dx, dz);
        float minGoal = T.Defender.RecoverMinGoalM;
        var p = DefenderBot.CutOffPoint(carrier, 7f, self, Movement.TopSpeed(CentreBack, T), Goal, minGoal);
        Assert.True(Vector2.Distance(p, Goal) < Vector2.Distance(carrier, Goal), "the point is goal-side of the carrier");
        Assert.True(Vector2.Distance(p, Goal) >= minGoal - 1e-3f);
        // On the carrier's line to goal.
        var u = Vector2.Normalize(Goal - carrier);
        var rel = p - carrier;
        Assert.Equal(0f, rel.X * u.Y - rel.Y * u.X, 3);
    }

    [Fact]
    public void BeatenHeRecoversAndIsBackInFrontOnceNoLongerBeaten()
    {
        var w = Live();
        GiveAttackerTheBall(w);
        var a = w.Attacker;
        var d = w.Defender!;
        // Well behind the carrier (further from goal by more than the overshoot), out of every tackle's reach.
        d.Position = new Vector3(a.Position.X - 3f, 0f, 3f);
        Step(w);
        Assert.Equal(DefenderMode.Recover, w.DefenderBot!.Mode);
        Assert.Equal(DefenderAction.Recover, d.DefenderAction);
        // In front again: he contains or drops.
        d.Position = new Vector3(a.Position.X + 3f, 0f, 0.2f);
        Step(w);
        Assert.True(w.DefenderBot.Mode is DefenderMode.Contain or DefenderMode.Drop or DefenderMode.Win);
    }

    // ---- not free: no button, no mode change ----

    static World Idle(out PlayerState d)
    {
        // A carrier standing still 18 m out: the slow trigger is due as soon as the defender is free.
        var w = Live();
        GiveAttackerTheBall(w);
        Place(w, DuelPitch.GoalLineX - 18f, 0f);
        d = w.Defender!;
        d.Position = new Vector3(DuelPitch.GoalLineX - 14f, 0f, 0f);
        return w;
    }

    static void HeldUntilFree(World w, PlayerState d)
    {
        var mode = w.DefenderBot!.Mode;
        int wins = w.DefenderBot.WinsSlow + w.DefenderBot.WinsLoose + w.DefenderBot.WinsLastLine;
        int starts = 0, ticks = 0;
        while (!Defending.Free(d, w.SimTime) && ticks < 600)
        {
            Step(w);
            ticks++;
            starts += w.Events.Count(e => e.Kind is EventKind.TackleStart or EventKind.Barge);
            if (!Defending.Free(d, w.SimTime))
            {
                Assert.Equal(mode, w.DefenderBot.Mode);
                Assert.Equal(wins, w.DefenderBot.WinsSlow + w.DefenderBot.WinsLoose + w.DefenderBot.WinsLastLine);
            }
        }
        Assert.True(ticks > 20, "he was not held long");
        Assert.Equal(0, starts);
        // Free again, the trigger that was due sends him.
        for (int i = 0; i < 120 && w.DefenderBot.WinsSlow == 0; i++) Step(w);
        Assert.True(w.DefenderBot.WinsSlow > 0);
    }

    [Fact]
    public void OnTheGroundHePressesNothingAndKeepsHisModeUntilHeIsUp()
    {
        var w = Idle(out var d);
        d.Def.GroundedUntil = w.SimTime + 0.5f;
        d.Def.GetUpUntil = w.SimTime + 1.0f;
        HeldUntilFree(w, d);
    }

    [Fact]
    public void StumblingHePressesNothingAndKeepsHisModeUntilHeIsSteady()
    {
        var w = Idle(out var d);
        d.StumbleUntil = w.SimTime + 0.6f;
        HeldUntilFree(w, d);
    }

    [Fact]
    public void StunnedByALungeHePressesNothingAndKeepsHisModeUntilHeIsFree()
    {
        var w = Idle(out var d);
        // A lunge at a ball far out of reach: planted through it, then stunned by the miss.
        Defending.StartLunge(d, w.Ball, w.Tick, T);
        HeldUntilFree(w, d);
    }

    // ---- a pass as he goes in ----

    [Fact]
    public void APassStruckAsHeGoesInSendsHimToTheAnchorWithNoLungeAtABallThatHasGone()
    {
        var w = ControlTests.WithBall(14f, 0f, 14f, 6f, defender: (19f, 0f));
        var buf = new TickIntent[w.PlayerCount];
        var bot = w.DefenderBot!;
        for (int i = 0; i < 30 && w.BallController != w.Attacker; i++) { buf[0] = new TickIntent { MoveX = 0.3f, CloseControl = true }; w.Step(buf); }
        Assert.Same(w.Attacker, w.BallController);
        buf[0] = default;
        // The carrier stands: the slow trigger sends the defender in.
        for (int i = 0; i < 240 && !(bot.Mode == DefenderMode.Win && TouchModel.HorizontalDistance(w.Defender!.Position, w.Attacker.Position) < 4.0f); i++) w.Step(buf);
        Assert.Equal(DefenderMode.Win, bot.Mode);
        int tacklesBefore = w.DefenderReport!.LungesTried + w.DefenderReport.SlidesTried;
        Assert.True(ControlTests.StrikePass(w, holdTicks: 3));
        int struck = w.Tick;
        int tackleStarts = 0;
        bool anchored = false;
        for (int i = 0; i < 30 && w.BallController == null && w.State is RoundState.Live; i++)
        {
            w.Step(buf);
            tackleStarts += w.Events.Count(e => e.Kind == EventKind.TackleStart);
            Assert.NotEqual(DefenderMode.Win, bot.Mode);
            if (i == 0)
            {
                Assert.True(w.DefenderReacting || !w.DefenderGoesForBall);
                anchored = bot.Mode is DefenderMode.Drop or DefenderMode.Contain;
                // His spot is worked out from the anchor, not from the ball on its way.
                var anchor = w.DefenderAnchor;
                Assert.True(Vector2.Distance(bot.HoldSpot, new Vector2(anchor.X, anchor.Z)) <= T.Defender.GapMaxM + T.Defender.ShadeM + 0.1f);
            }
        }
        Assert.True(anchored, "on the strike he plays on the anchor");
        Assert.Equal(0, tackleStarts);
        Assert.Equal(tacklesBefore, w.DefenderReport!.LungesTried + w.DefenderReport.SlidesTried);
        Assert.True(w.Tick > struck);
    }

    [Fact]
    public void HeNeverRunsWithTheCarrierForASingleTick()
    {
        // (a run with that the next tick ends at once draws him out of his stance and back: the straight attacker used to cause one
        // in most rounds)
        int spells = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var w = Live(seed: seed);
            var script = new ScriptedAttacker { Style = ScriptedAttacker.AttackStyle.Straight };
            var modes = new List<DefenderMode>();
            TestData.Play(w, script, stopWhen: x => { modes.Add(x.DefenderBot!.Mode); return false; });
            for (int i = 1; i + 1 < modes.Count; i++)
            {
                if (modes[i] != DefenderMode.RunWith || modes[i - 1] == DefenderMode.RunWith) continue;
                spells++;
                Assert.False(modes[i + 1] == DefenderMode.Contain, $"seed {seed}: a one-tick run with at tick {i}");
            }
        }
        Assert.True(spells > 0, "no run with in these rounds");
    }

    // ---- the keeper's yield ----

    [Fact]
    public void AHeavyTouchBetweenKeeperAndDefenderAtNineMetresIsDecidedByTheYieldWithNoDoubleClaim()
    {
        for (ulong seed = 1; seed <= 12; seed++)
        {
            var w = Live(Pace, seed);
            GiveAttackerTheBall(w);
            var d = w.Defender!;
            Place(w, DuelPitch.GoalLineX - 14f, 0f);
            d.Position = new Vector3(DuelPitch.GoalLineX - 9f, 0f, 0.4f);
            for (int i = 0; i < 240 && w.State is RoundState.Live or RoundState.Strike; i++)
            {
                Step(w, new TickIntent { MoveX = 1f, Sprint = 1f });
                bool covers = w.DefenderCovers();
                bool keeperHas = w.BallController == w.Keeper;
                bool defenderHas = w.BallController == d;
                Assert.False(keeperHas && defenderHas);
                var dive = TouchModel.HorizontalDistance(w.Keeper.Position, w.Ball.Position) <= 2.5f;
                if (covers && !dive)
                    Assert.True(w.Keeper.KeeperAction is not (KeeperAction.Rush or KeeperAction.Charge), $"seed {seed}: the keeper went for a ball the defender covers ({w.Keeper.KeeperAction})");
            }
        }
    }


    // ---- the play-test's three ways past him ----

    [Fact]
    public void ASlowCarrierNearHisEngageLineDrawsHimAfterPressAfterSecondsAndOneFarOutOnlyAfterTheFarWait()
    {
        var k = T.Defender;
        float gap = DefenderBot.GapFor(CentreBack, 0f, T);
        foreach (bool far in new[] { false, true })
        {
            var w = Live();
            GiveAttackerTheBall(w);
            var d = w.Defender!;
            float fromGoal = far ? k.EngageLineM + gap + 4f : k.EngageLineM + gap - 0.5f;
            Place(w, DuelPitch.GoalLineX - fromGoal, 0f);
            d.Position = new Vector3(DuelPitch.GoalLineX - (k.EngageLineM - 1f), 0f, 0f);
            int sentAt = -1;
            for (int i = 0; i < 600 && sentAt < 0; i++)
            {
                Step(w);
                if (w.DefenderBot!.WinsSlow > 0) sentAt = i + 1;
            }
            float expect = far ? k.PressFarAfterS : k.PressAfterS;
            Assert.InRange(sentAt * T.Dt, expect - 0.05f, expect + 0.1f);
        }
    }

    [Fact]
    public void ClosingOnAStillCarrierHeArrivesUnderControlAndLungesOnlyAtABallInsideHisReach()
    {
        var w = Idle(out var d);
        float decel = T.Defending.JockeyDecel;
        bool closed = false;
        for (int i = 0; i < 360 && w.BallController == w.Attacker; i++)
        {
            Step(w);
            if (w.DefenderBot!.Mode != DefenderMode.Win) continue;
            float dist = Vector2.Distance(G(d.Position), G(w.Ball.Position));
            // no faster than he can stop from short of the ball (half a metre a second of slack: his brakes lag the cap a little)
            float cap = MathF.Max(0.5f, MathF.Sqrt(2f * decel * MathF.Max(0f, dist - 0.6f)));
            if (dist < 3f) Assert.True(d.Speed <= cap + 0.5f, $"closing at {d.Speed:F2} m/s {dist:F2} m from the ball");
            if (w.Events.Any(e => e.Kind == EventKind.TackleStart))
                Assert.True(dist <= Defending.LungeReach(d, w.Ball.Position, T) + 0.2f, $"lunged at a ball {dist:F2} m away");
            closed |= dist < 1.5f;
        }
        Assert.True(closed, "he never closed on the still carrier");
    }

    [Fact]
    public void SteppedOffHisLineWithTheCarrierNearHeContainsAndDropsAgainOnlyOnceBackWithinHalfOfIt()
    {
        var w = Live(tuning: NoWin);
        GiveAttackerTheBall(w);
        var a = w.Attacker;
        var d = w.Defender!;
        var k = T.Defender;
        float gap = DefenderBot.GapFor(CentreBack, 0f, T);
        // The carrier walks at goal, outside the gap but within twice it; the defender stands on his engage line.
        Place(w, DuelPitch.GoalLineX - (k.EngageLineM + 1.6f * gap), 0f);
        Vector3 At(float off) => new(DuelPitch.GoalLineX - k.EngageLineM, 0f, off);
        var walk = new TickIntent { MoveX = 0.3f, CloseControl = true };
        d.Position = At(0f); d.Velocity = Vector3.Zero;
        Step(w, walk);
        Assert.Equal(DefenderMode.Drop, w.DefenderBot!.Mode);
        d.Position = At(k.DropOfflineM + 0.4f); d.Velocity = Vector3.Zero;
        Step(w, walk);
        Assert.Equal(DefenderMode.Contain, w.DefenderBot.Mode);
        // Between half the threshold and the threshold he stays containing (no flicker) ...
        d.Position = At(0.75f * k.DropOfflineM); d.Velocity = Vector3.Zero;
        Step(w, walk);
        Assert.Equal(DefenderMode.Contain, w.DefenderBot.Mode);
        // ... and drops again once back within half of it.
        d.Position = At(0.2f * k.DropOfflineM); d.Velocity = Vector3.Zero;
        Step(w, walk);
        Assert.Equal(DefenderMode.Drop, w.DefenderBot.Mode);
    }

    // ---- replays ----

    [Theory]
    [InlineData(ScriptedAttacker.AttackStyle.Cut, 3UL)]
    [InlineData(ScriptedAttacker.AttackStyle.Skill, 8UL)]
    public void AReplayPlaysTheRecordedDefenderWithTheBotOffWhicheverBotRecordedIt(ScriptedAttacker.AttackStyle style, ulong seed)
    {
        // Recorded with the legacy bot, played back under the shipped tuning: the defender follows his recorded intents.
        var legacy = new Tuning { Defender = new DefenderTuning { Brain = "legacy" } };
        var played = RoundRunner.RunToReport(legacy, Duel, Striker, Wall, seed, out var replay, new ScriptedAttacker { Style = style }, CentreBack);
        var w = new World(T, Duel, Striker, Wall, seed, CentreBack);
        w.Begin();
        Assert.True(replay.HoldsDefender(w));
        var back = replay.Run(T, Duel, Striker, Wall, CentreBack);
        Assert.Equal(played.Outcome, back.Outcome);
        Assert.Equal(played.Ticks, back.Ticks);
        var a = played.Defender!; var b = back.Defender!;
        Assert.Equal((a.AutoWon, a.LungesTried, a.LungesWon, a.SlidesTried, a.SlidesWon, a.Blocks, a.Barges, a.Fouls, a.Collected, a.WonBy, a.BeatenS),
            (b.AutoWon, b.LungesTried, b.LungesWon, b.SlidesTried, b.SlidesWon, b.Blocks, b.Barges, b.Fouls, b.Collected, b.WonBy, b.BeatenS));
    }

    [Fact]
    public void AReplayWithoutTheDefendersIntentsRunsHisBot()
    {
        var played = RoundRunner.RunToReport(T, Duel, Striker, Wall, 5, out var replay, new ScriptedAttacker { Style = ScriptedAttacker.AttackStyle.Cut }, CentreBack);
        // Only the attacker's intents kept: the bot plays the defender, as it did when the round was played.
        var thin = new Replay { Seed = replay.Seed, AttackerId = replay.AttackerId, KeeperId = replay.KeeperId, ScenarioId = replay.ScenarioId, PlayersPerTick = 1 };
        for (int i = 0; i < replay.TickCount; i++) thin.Intents.Add(replay.Intents[i * replay.PlayersPerTick]);
        var w = new World(T, Duel, Striker, Wall, 5, CentreBack);
        w.Begin();
        Assert.False(thin.HoldsDefender(w));
        var back = thin.Run(T, Duel, Striker, Wall, CentreBack);
        Assert.Equal(played.Outcome, back.Outcome);
        Assert.Equal(played.Ticks, back.Ticks);
        Assert.Equal(played.Defender!.TimeActive, back.Defender!.TimeActive);
    }
}
