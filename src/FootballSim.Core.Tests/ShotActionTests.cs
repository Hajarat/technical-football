using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>The shot as one action in the world: wind-up, aim lock, type latching, stun, fake shot, determinism, replays.</summary>
public class ShotActionTests
{
    static TickIntent[] Buf(World w) => new TickIntent[w.PlayerCount];

    static void Step(World w, TickIntent it)
    {
        var a = Buf(w);
        a[0] = it;
        w.Step(a);
    }

    /// <summary>Steps until the shot is struck (or the cap), feeding <paramref name="it"/> after the release.</summary>
    static void StepToStrike(World w, TickIntent it = default, int max = 400)
    {
        for (int i = 0; i < max && w.Shots.Count == 0; i++) Step(w, it);
    }

    [Fact]
    public void TheShotIsOneActionFromThePressToTheEndOfItsRecovery()
    {
        var w = TestData.Live("penalty");
        var p = w.Attacker;
        Step(w, new TickIntent { ShootHeld = true });
        int press = w.Tick;
        var shot = Assert.IsType<BallAction>(p.Action);
        Assert.True(shot.IsShot);
        Assert.Equal(ActionKind.Power, shot.Kind);
        Assert.Equal(press, shot.PlanTick);
        Assert.Equal(ActionPhase.Windup, shot.Phase);
        Assert.Equal(FootSurface.Laces, shot.Surface);
        Assert.False(shot.Released);
        Assert.False(shot.Pending);
        Assert.Null(p.PreviousAction);
        for (int i = 0; i < 9; i++) Step(w, new TickIntent { ShootHeld = true });
        Assert.Same(shot, p.Action);
        Assert.Equal(press + 54, shot.ContactTick);          // the earliest contact while still held
        Step(w, new TickIntent { MoveX = 1 });
        Assert.True(shot.Released);
        Assert.Equal(press + 54, shot.ContactTick);
        int contact = shot.ContactTick;
        while (w.Tick < contact - 1) { Step(w, default); Assert.Equal(ActionPhase.Windup, shot.Phase); Assert.Same(shot, p.Action); }
        Step(w, default);
        Assert.Equal(contact, w.Tick);
        Assert.True(shot.Made);
        Assert.Equal(ActionPhase.Contact, shot.Phase);
        Assert.Equal(w.Shots[0].Launch, shot.BallOutVelocity);
        Assert.Equal(w.Shots[0].Foot, shot.Foot);
        Assert.Equal(contact + 30, shot.EndTick);            // 0.25 s of follow-through
        Step(w, default);
        Assert.Equal(ActionPhase.Recovery, shot.Phase);
        while (w.Tick < shot.EndTick - 1 && w.IsSimulating) { Step(w, default); Assert.Same(shot, p.Action); }
        Step(w, default);
        Assert.NotSame(shot, p.Action);
    }

    [Theory]
    [InlineData(false, false, false, ShotType.Power, ActionKind.Power, FootSurface.Laces)]
    [InlineData(true, false, false, ShotType.Finesse, ActionKind.Finesse, FootSurface.Inside)]
    [InlineData(false, false, true, ShotType.Driven, ActionKind.Driven, FootSurface.Laces)]
    [InlineData(true, false, true, ShotType.LowFinesse, ActionKind.LowFinesse, FootSurface.Inside)]
    [InlineData(true, true, true, ShotType.Chip, ActionKind.Chip, FootSurface.Inside)]
    public void TheTypeIsLatchedFromTheModifiersHeldDuringThePress(bool finesse, bool chip, bool driven, ShotType type,
        ActionKind kind, FootSurface surface)
    {
        var w = TestData.Live("penalty");
        // Modifiers held only for part of the press still count.
        for (int i = 0; i < 12; i++)
            Step(w, new TickIntent { ShootHeld = true, Finesse = finesse && i is >= 2 and < 4, Chip = chip && i == 5, Driven = driven && i >= 8 });
        Assert.Equal(kind, w.Attacker.Action!.Kind);
        Assert.Equal(surface, w.Attacker.Action.Surface);
        Step(w, new TickIntent { MoveX = 1 });
        StepToStrike(w);
        var s = Assert.Single(w.Shots);
        Assert.Equal(type, s.Type);
        Assert.Equal(kind, w.Attacker.Action!.Kind);
    }

    [Fact]
    public void AStickAtRestAtTheReleaseAimsWhereItWasLastPushedWithinHalfASecond()
    {
        float AimedZ(int restTicks)
        {
            var w = TestData.Live("penalty");
            var stick = new Vector2(1f, 0.3f);
            // Pushed while charging, then let go for restTicks before the release.
            for (int i = 0; i < 70; i++) Step(w, new TickIntent { ShootHeld = true, MoveX = i < 70 - restTicks ? stick.X : 0, MoveZ = i < 70 - restTicks ? stick.Y : 0 });
            Step(w, default);
            StepToStrike(w);
            return Assert.Single(w.Shots).Aimed.X;
        }
        float pushed = AimedZ(0);
        Assert.True(pushed > 2f, $"aimed {pushed}");
        Assert.Equal(pushed, AimedZ(30), 3);                 // 0.25 s at rest: still locked to the last push
        Assert.Equal(pushed, AimedZ(59), 3);                 // just inside 0.5 s
        Assert.InRange(AimedZ(70), -0.5f, 0.5f);              // never pushed in the last 0.5 s: where the shooter faces
    }

    [Fact]
    public void TheAimIsLockedFromTheReleaseToTheContact()
    {
        var w = TestData.Live("penalty");
        for (int i = 0; i < 10; i++) Step(w, new TickIntent { ShootHeld = true });
        Step(w, new TickIntent { MoveX = 1f, MoveZ = 0.3f });
        StepToStrike(w, new TickIntent { MoveX = 1f, MoveZ = -0.6f }); // a different stick during the wind-up is ignored
        var locked = Assert.Single(w.Shots).Aimed.X;
        var v = TestData.Live("penalty");
        for (int i = 0; i < 10; i++) Step(v, new TickIntent { ShootHeld = true });
        Step(v, new TickIntent { MoveX = 1f, MoveZ = 0.3f });
        StepToStrike(v);
        Assert.Equal(Assert.Single(v.Shots).Aimed.X, locked, 3);
    }

    [Fact]
    public void IdealChargeIsPublishedForTheBallsDistance()
    {
        var w = TestData.Live("edge_of_box");
        Step(w, default);
        float d = ShotModel.GoalDistance(w.Ball.Position, w.Pitch);
        Assert.Equal(ShotModel.IdealCharge(d, w.Tuning), w.IdealCharge, 4);
        Assert.True(w.IdealCharge > TestData.Live("penalty").IdealCharge);
    }

    [Fact]
    public void AfterTheStrikeTheShooterIsStunnedSlowedAndMakesNoTouch()
    {
        foreach (float charge in new[] { 0.2f, 0.9f })
        {
            var w = TestData.Live("penalty");
            int hold = (int)MathF.Round(charge * w.Tuning.Shot.ChargeSeconds * w.Tuning.TickRate);
            for (int i = 0; i < hold; i++) Step(w, new TickIntent { ShootHeld = true });
            Step(w, new TickIntent { MoveX = 1 });
            StepToStrike(w);
            var p = w.Attacker;
            float c = w.Shots[0].Charge;
            float stun = w.Tuning.Shot.StunBaseSeconds + w.Tuning.Shot.StunChargeSeconds * c;
            Assert.Equal(w.SimTime + stun, p.StunUntil, 4);
            float cap = w.Tuning.Shot.StunSpeedFraction * Movement.TopSpeed(p.Sheet, w.Tuning);
            // Sprinting at the ball (put back at his feet) during the stun: capped, no touch.
            int ticks = (int)(stun * w.Tuning.TickRate) - 2;
            for (int i = 0; i < ticks; i++)
            {
                w.Ball.Position = p.Position + new Vector3(0.4f, w.Tuning.Ball.Radius, 0f);
                w.Ball.Velocity = Vector3.Zero;
                Step(w, new TickIntent { MoveX = 1, Sprint = 1 });
                Assert.True(p.Speed <= cap + 1e-3f, $"charge {charge}: speed {p.Speed} over the stun cap {cap}");
                Assert.DoesNotContain(w.Events, e => e.Kind == EventKind.Touch);
                if (!w.IsSimulating || w.State == RoundState.Outcome) break;
            }
        }
    }

    [Fact]
    public void AFakeShotStillAbortsTheShotAction()
    {
        var w = TestData.Live("penalty");
        Step(w, new TickIntent { ShootHeld = true });
        var shot = w.Attacker.Action!;
        for (int i = 0; i < 4; i++) Step(w, new TickIntent { ShootHeld = true });
        Step(w, new TickIntent { ShootHeld = true, Cancel = true });
        Assert.Equal(ActionPhase.Done, shot.Phase);
        Assert.False(shot.Made);
        Assert.NotSame(shot, w.Attacker.Action);
        for (int i = 0; i < 120; i++) Step(w, new TickIntent { ShootHeld = i < 30 });
        Assert.Empty(w.Shots);
        Assert.Equal(RoundState.Live, w.State);
    }

    [Fact]
    public void TheSoftTouchInsideTheWindupIsVisibleAsTheTouchAction()
    {
        // Dribbling at a jog, then shoot: Action shows the shot while TouchAction carries the soft touch the view draws.
        var sc = TestData.Scenario("breakaway");
        var w = new World(new Tuning(), sc, TestData.Player("striker_a"), TestData.Player("the_wall"), 3);
        w.Begin();
        var p = w.Attacker;
        for (int i = 0; i < 240 && !p.InPossession; i++) Step(w, new TickIntent { MoveX = 1 });
        for (int i = 0; i < 120; i++) Step(w, new TickIntent { MoveX = 1 });
        Assert.True(p.InPossession);
        BallAction? soft = null;
        for (int i = 0; i < 120 && w.Shots.Count == 0; i++)
        {
            Step(w, new TickIntent { MoveX = 1, ShootHeld = i < 15 });
            Assert.True(p.Action is { IsShot: true }, $"tick {i}: the shot is the action");
            if (w.Events.Any(e => e.Kind == EventKind.Touch))
            {
                Assert.NotNull(p.TouchAction);
                if (p.TouchAction!.Kind == ActionKind.Soft) soft = p.TouchAction;
            }
        }
        Assert.NotNull(soft);
        Assert.True(soft!.Made);
        Assert.Single(w.Shots);
    }

    [Fact]
    public void AShotRoundIsDeterministicAndReplaysAfterASaveAndLoad()
    {
        var sc = TestData.Scenario("edge_of_box");
        var att = TestData.Player("striker_a"); var kee = TestData.Player("the_wall");
        var script = new ScriptedAttacker { Type = ShotType.LowFinesse };
        var a = RoundRunner.RunToReport(new Tuning(), sc, att, kee, 99, out var replay, script);
        var b = RoundRunner.RunToReport(new Tuning(), sc, att, kee, 99, out _, new ScriptedAttacker { Type = ShotType.LowFinesse });
        Assert.Equal(a.ToJson(), b.ToJson());
        Assert.Equal(ShotType.LowFinesse, a.Shots[0].Type);
        Assert.Contains(replay.Intents, i => i.Driven);
        var json = replay.ToJson();
        Assert.Contains("\"driven\": true", json);
        var loaded = Replay.FromJson(json, out var err);
        Assert.Null(err);
        Assert.Equal(a.ToJson(), loaded!.Run(new Tuning(), sc, att, kee).ToJson());
    }

    [Fact]
    public void AnOldReplayWithoutDrivenLoads()
    {
        const string old = """
        { "version": 1, "seed": 3, "attacker_id": "striker_a", "keeper_id": "the_wall", "scenario_id": "penalty",
          "tuning_hash": "0000000000000000", "players_per_tick": 2,
          "intents": [
            { "mx": 0, "mz": 0, "sprint": 0, "close": false, "shoot": true, "finesse": false, "chip": false, "knock": false },
            { "mx": 0, "mz": 0, "sprint": 0, "close": false, "shoot": false, "finesse": false, "chip": false, "knock": false },
            { "mx": 1, "mz": 0, "sprint": 0, "close": false, "shoot": false, "finesse": false, "chip": false, "knock": false },
            { "mx": 0, "mz": 0, "sprint": 0, "close": false, "shoot": false, "finesse": false, "chip": false, "knock": false }
          ] }
        """;
        var r = Replay.FromJson(old, out var err);
        Assert.Null(err);
        Assert.NotNull(r);
        Assert.Equal(2, r!.TickCount);
        Assert.All(r.Intents, i => Assert.False(i.Driven));
        Assert.False(r.Intents[0].ToIntent().Driven);
        var report = r.Run(new Tuning(), TestData.Scenario("penalty"), TestData.Player("striker_a"), TestData.Player("the_wall"));
        Assert.Equal(ShotType.Power, Assert.Single(report.Shots).Type);
        // Written back, a replay with Driven never held leaves the field out, as before.
        Assert.DoesNotContain("driven", r.ToJson());
    }

    [Fact]
    public void TheScriptedAttackerJudgesTheChargeAroundTheIdeal()
    {
        var sc = TestData.Scenario("edge_of_box");
        var att = TestData.Player("striker_a"); var kee = TestData.Player("the_wall");
        var t = new Tuning();
        var charges = Enumerable.Range(0, 400).Select(i => RoundRunner.RunFirstShot(t, sc, att, kee, 500 + (ulong)i).Shot!.Charge).ToArray();
        float ideal = ShotModel.IdealCharge(ShotModel.GoalDistance(new Vector3(sc.Ball.X, 0.11f, sc.Ball.Z), new Pitch(sc.Pitch, t.Frame, t.Arena)), t);
        double mean = charges.Average(), sd = Math.Sqrt(charges.Average(c => (c - mean) * (c - mean)));
        Assert.InRange(mean, ideal - 0.02, ideal + 0.04);    // the charge goes up in steps of one tick, rounded up
        Assert.InRange(sd, 0.10, 0.14);
        // A fixed charge overrides the model.
        Assert.Equal(0.5f, RoundRunner.RunFirstShot(t, sc, att, kee, 7, new ScriptedAttacker { Charge = 0.5f }).Shot!.Charge, 1);
    }
    // ---- the low shot: shoot tapped twice ----

    /// <summary>
    /// A press of shoot held <paramref name="first"/> ticks, released <paramref name="gap"/> ticks, then pressed again
    /// <paramref name="second"/> ticks; finesse (RB) and chip (LB) held throughout. Returns the struck shot.
    /// </summary>
    static ShotResult DoubleTap(int first, int gap, int second, bool finesse = false, bool chip = false)
    {
        var w = TestData.Live("penalty");
        for (int i = 0; i < first; i++) Step(w, new TickIntent { ShootHeld = true, Finesse = finesse, Chip = chip });
        for (int i = 0; i < gap; i++) Step(w, new TickIntent { Finesse = finesse, Chip = chip });
        for (int i = 0; i < second && w.Shots.Count == 0; i++) Step(w, new TickIntent { ShootHeld = true, Finesse = finesse, Chip = chip });
        StepToStrike(w);
        return Assert.Single(w.Shots);
    }

    [Theory]
    [InlineData(false, false, ShotType.Driven)]      // B, B: low shot
    [InlineData(true, false, ShotType.LowFinesse)]   // RB + B, B: low curved
    [InlineData(false, true, ShotType.Chip)]         // LB + B, B: the low chip is not built, a chip
    public void ShootTappedTwiceIsALowShot(bool finesse, bool chip, ShotType type)
    {
        Assert.Equal(type, DoubleTap(8, 6, 6, finesse, chip).Type);
        // The second press on the last tick of the 0.25 s window still counts.
        Assert.Equal(type, DoubleTap(8, 30 - 8, 6, finesse, chip).Type);
    }

    [Theory]
    [InlineData(false, false, ShotType.Power)]
    [InlineData(true, false, ShotType.Finesse)]
    [InlineData(false, true, ShotType.Chip)]
    public void ASecondPressAfterTheWindowOrASingleTapChangesNothing(bool finesse, bool chip, ShotType type)
    {
        Assert.Equal(type, DoubleTap(8, 31 - 8, 6, finesse, chip).Type);   // one tick too late
        Assert.Equal(type, DoubleTap(8, 0, 0, finesse, chip).Type);        // a single tap
        Assert.Equal(type, DoubleTap(40, 4, 6, finesse, chip).Type);       // held past the window, then pressed again
    }

    [Fact]
    public void TheLowShotKeepsTheFirstPressCharge()
    {
        var one = DoubleTap(8, 0, 0);
        var two = DoubleTap(8, 6, 20);
        Assert.Equal(one.Charge, two.Charge);
        Assert.Equal(ShotType.Driven, two.Type);
    }

    [Fact]
    public void ShoulderPressedOnlyWithTheSecondTapCountsForTheLowShot()
    {
        var w = TestData.Live("penalty");
        for (int i = 0; i < 8; i++) Step(w, new TickIntent { ShootHeld = true });
        for (int i = 0; i < 6; i++) Step(w, default);
        Assert.Equal(ActionKind.Power, w.Attacker.Action!.Kind);
        for (int i = 0; i < 6; i++) Step(w, new TickIntent { ShootHeld = true, Finesse = true });
        Assert.Equal(ActionKind.LowFinesse, w.Attacker.Action!.Kind);
        StepToStrike(w);
        Assert.Equal(ShotType.LowFinesse, Assert.Single(w.Shots).Type);
    }

    [Fact]
    public void ShootThenCancelIsStillAFakeShot()
    {
        // B then A (cancel) inside 0.1 s: a fake, no strike, whatever comes after inside the low-shot window.
        var w = TestData.Live("penalty");
        for (int i = 0; i < 4; i++) Step(w, new TickIntent { ShootHeld = true });
        for (int i = 0; i < 4; i++) Step(w, new TickIntent { ShootHeld = true, Cancel = true });
        for (int i = 0; i < 6; i++) Step(w, default);
        for (int i = 0; i < 6; i++) Step(w, new TickIntent { ShootHeld = true });
        Assert.Equal(Gesture.FakeStop, w.Attacker.LastGesture);
        Assert.Empty(w.Shots);
    }
}
