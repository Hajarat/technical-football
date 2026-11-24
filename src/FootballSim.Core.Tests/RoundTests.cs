using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

public class RoundTests
{
    static TickIntent[] Buf(World w) => new TickIntent[w.PlayerCount];

    /// <summary>Holds shoot for <paramref name="hold"/> ticks, then releases with the stick set. Returns the buffer for further steps.</summary>
    static TickIntent[] Shoot(World w, int hold, Vector2 stick, bool chip = false)
    {
        var a = Buf(w);
        for (int i = 0; i < hold; i++) { a[0] = new TickIntent { ShootHeld = true, Chip = chip }; w.Step(a); }
        a[0] = new TickIntent { MoveX = stick.X, MoveZ = stick.Y, Chip = chip };
        w.Step(a);
        a[0] = default;
        return a;
    }

    static void StepUntil(World w, TickIntent[] a, Func<World, bool> done, int max = 4000)
    {
        for (int i = 0; i < max && !done(w); i++) w.Step(a);
    }

    static Scenario WithClock(float? clock, Action<Scenario>? edit = null)
    {
        var s = TestData.Scenario("penalty");
        s.ClockLimit = clock;
        edit?.Invoke(s);
        return s;
    }

    [Fact]
    public void SetupDoesNothingUntilBegin()
    {
        var w = new World(new Tuning(), TestData.Scenario("penalty"), TestData.Player("striker_a"), TestData.Player("the_wall"), 1);
        Assert.Equal(RoundState.Setup, w.State);
        var a = Buf(w);
        a[0] = new TickIntent { MoveX = 1, Sprint = 1 };
        var before = w.Attacker.Position;
        for (int i = 0; i < 10; i++) w.Step(a);
        Assert.Equal(0, w.Tick);
        Assert.Equal(before, w.Attacker.Position);
        w.Begin();
        Assert.Equal(RoundState.Live, w.State);
        w.Step(a);
        Assert.Equal(1, w.Tick);
    }

    [Fact]
    public void ChargeBuildsOverThreeTenthsOfASecondAndResetsOnRelease()
    {
        var w = TestData.Live();
        var a = Buf(w);
        for (int i = 0; i < 18; i++) { a[0] = new TickIntent { ShootHeld = true }; w.Step(a); }
        Assert.InRange(w.ShotCharge, 0.49f, 0.51f);
        for (int i = 0; i < 17; i++) { a[0] = new TickIntent { ShootHeld = true }; w.Step(a); }
        Assert.True(w.ShotCharge < 1f);
        a[0] = new TickIntent { ShootHeld = true };
        w.Step(a);
        Assert.Equal(1f, w.ShotCharge, 4);                   // 36 ticks = 0.3 s
        for (int i = 0; i < 50; i++) { a[0] = new TickIntent { ShootHeld = true }; w.Step(a); }
        Assert.Equal(1f, w.ShotCharge);
        a[0] = default;
        w.Step(a);
        Assert.Equal(0f, w.ShotCharge);
    }

    /// <summary>Holds shoot for <paramref name="hold"/> ticks from Live, releases, and returns the press tick, release tick and strike tick.</summary>
    static (int Press, int Release, int Strike) StrikeTiming(int hold)
    {
        var w = TestData.Live();
        var a = Buf(w);
        int press = w.Tick + 1;
        for (int i = 0; i < hold; i++) { a[0] = new TickIntent { ShootHeld = true }; w.Step(a); }
        Assert.Equal(RoundState.Live, w.State);
        a[0] = new TickIntent { MoveX = 1 };
        w.Step(a);
        int release = w.Tick;
        Assert.Equal(RoundState.Strike, w.State);
        Assert.Empty(w.Shots);
        Assert.True(w.Attacker.Action is { IsShot: true, Released: true, Made: false, Phase: ActionPhase.Windup });
        int expected = w.Attacker.Action!.ContactTick;
        a[0] = default;
        while (w.State == RoundState.Strike && w.Tick < release + 200) w.Step(a);
        Assert.Equal(RoundState.Flight, w.State);
        Assert.Single(w.Shots);
        Assert.Equal(expected, w.Tick);
        Assert.Equal(w.Tick, w.Shots[0].Tick);
        return (press, release, w.Tick);
    }

    [Fact]
    public void ContactComesNoSoonerThanTheWindupAfterThePress()
    {
        // A short press: the wind-up (striker_a's finishing 78: 0.36 s = 44 ticks after the press) decides.
        var (press, release, strike) = StrikeTiming(10);
        Assert.Equal(press + 10, release);
        Assert.Equal(press + 44, strike);
    }

    [Fact]
    public void ContactComesNoSoonerThanTheLeadAfterTheRelease()
    {
        // A long press: the lead after the release (0.12 s = 14 ticks) decides.
        var (press, release, strike) = StrikeTiming(80);
        Assert.Equal(release + 14, strike);
        Assert.True(strike > press + 44);
        // A release just before the wind-up ends still gets the full lead.
        var (p2, r2, s2) = StrikeTiming(50);
        Assert.Equal(p2 + 50, r2);
        Assert.Equal(r2 + 14, s2);
    }

    [Fact]
    public void ChargeIsFrozenAtTheRelease()
    {
        var w = TestData.Live();
        var a = Shoot(w, 12, new Vector2(1, 0));
        while (w.State == RoundState.Strike) w.Step(a);
        Assert.Equal(12f * w.Tuning.Dt / w.Tuning.Shot.ChargeSeconds, Assert.Single(w.Shots).Charge, 4);
    }

    [Fact]
    public void ReleaseWithTheBallOutOfReachWaitsThenDrops()
    {
        var sc = WithClock(null, s => { s.Attacker.X = 8f; });
        var w = new World(new Tuning(), sc, TestData.Player("striker_a"), TestData.Player("the_wall"), 1);
        w.Begin();
        var a = Shoot(w, 20, new Vector2(1, 0));
        Assert.Equal(RoundState.Strike, w.State);
        for (int i = 0; i < 120; i++)
        {
            w.Step(a);
            Assert.DoesNotContain(w.Events, e => e.Kind == EventKind.Strike);
        }
        Assert.Equal(RoundState.Live, w.State);
        Assert.Empty(w.Shots);
        Assert.Null(w.Report);
        Assert.Equal(Outcome.None, w.Outcome);
        Assert.True(w.Attacker.Action is not { IsShot: true });
    }

    [Fact]
    public void AReleasedShotStrikesIfTheBallComesIntoReachBeforeTheDeadline()
    {
        var sc = WithClock(null, s => { s.Attacker.X = 15.0f; });
        var w = new World(new Tuning(), sc, TestData.Player("striker_a"), TestData.Player("the_wall"), 1);
        w.Begin();
        w.Ball.Position = new Vector3(18.0f, 0.11f, 0f);        // 3 m away: out of reach at the contact tick
        var a = Shoot(w, 20, new Vector2(1, 0));
        int contact = w.Attacker.Action!.ContactTick;
        while (w.Tick < contact + 10) w.Step(a);
        Assert.Equal(RoundState.Strike, w.State);
        Assert.Empty(w.Shots);
        w.Ball.Position = new Vector3(w.Attacker.Position.X + 0.4f, 0.11f, 0f); // heavy touch brings it back
        w.Ball.Velocity = Vector3.Zero;
        w.Step(a);
        Assert.Equal(RoundState.Flight, w.State);
        Assert.Single(w.Shots);
        Assert.Equal(contact + 11, w.Attacker.Action!.ContactTick);
    }

    [Fact]
    public void ShotsAreRecordedAndTheRoundReachesAReport()
    {
        var w = TestData.Live("penalty", keeper: "stand_in", seed: 7);
        var states = new List<RoundState> { w.State };
        var script = new ScriptedAttacker();
        var buf = Buf(w);
        for (int i = 0; i < 4000 && w.State != RoundState.Report; i++)
        {
            buf[0] = script.Next(w);
            w.Step(buf);
            if (states[^1] != w.State) states.Add(w.State);
        }
        Assert.Equal(RoundState.Report, w.State);
        Assert.NotNull(w.Report);
        Assert.Equal(new[] { RoundState.Live, RoundState.Strike, RoundState.Flight }, states.Take(3).ToArray());
        Assert.Contains(RoundState.Outcome, states);
        Assert.Equal(w.Outcome, w.Report!.Outcome);
        Assert.Equal(7UL, w.Report.Seed);
        Assert.Single(w.Report.Shots.Take(1));
    }

    static World FindRound(Func<World, bool> accept, string scenario = "penalty", string keeper = "the_wall", int maxSeeds = 400, int maxTicks = 3000)
    {
        for (ulong seed = 1; seed <= (ulong)maxSeeds; seed++)
        {
            var w = TestData.Live(scenario, keeper: keeper, seed: seed);
            if (accept(w)) return w;
        }
        throw new Exception("no seed produced the wanted round");
    }

    /// <summary>Plays with the scripted attacker; returns true if <paramref name="hit"/> became true on some tick.</summary>
    static bool PlayUntil(World w, Func<World, bool> hit, int maxTicks = 3000)
    {
        var script = new ScriptedAttacker();
        var buf = Buf(w);
        for (int i = 0; i < maxTicks && w.State != RoundState.Report; i++)
        {
            buf[0] = script.Next(w);
            w.Step(buf);
            if (hit(w)) return true;
        }
        return false;
    }

    [Fact]
    public void GoalEndsTheRoundKeepsTenPercentOfBallSpeedAndReportsAfterOneAndAHalfSeconds()
    {
        var w = FindRound(x => PlayUntil(x, y => y.State == RoundState.Outcome && y.Outcome == Outcome.Goal), keeper: "stand_in");
        Assert.Equal(Outcome.Goal, w.Outcome);
        Assert.Contains(w.Events, e => e.Kind == EventKind.Goal);
        float speed = w.Ball.Velocity.Length();
        Assert.True(speed < 4.5f, $"net speed {speed}");
        int ticks = 0;
        var buf = Buf(w);
        while (w.State == RoundState.Outcome && ticks < 400) { w.Step(buf); ticks++; }
        Assert.InRange(ticks, 179, 181);
        Assert.Equal(RoundState.Report, w.State);
        Assert.Equal(Outcome.Goal, w.Report!.Outcome);
        Assert.True(w.Ball.Position.X <= w.Pitch.GoalLineX + w.Pitch.NetDepth + 0.01f);
    }

    [Fact]
    public void HeldOutcomeKeepsTheBallInTheHands()
    {
        var w = FindRound(x => PlayUntil(x, y => y.Outcome == Outcome.Held && y.KeeperReport.Action == KeeperResult.Held), scenario: "tight_angle", maxSeeds: 600);
        Assert.Equal(KeeperResult.Held, w.KeeperReport.Action);
        Assert.Equal(KeeperAction.Holding, w.Keeper.KeeperAction);
        var buf = Buf(w);
        for (int i = 0; i < 30; i++) w.Step(buf);
        Assert.Equal(w.Keeper.HandPosition, w.Ball.Position);
        StepUntil(w, buf, x => x.State == RoundState.Report);
        Assert.Equal(Outcome.Held, w.Report!.Outcome);
    }

    [Fact]
    public void ShotWideOfThePostComesBackOffTheBoardsAndPlayGoesOn()
    {
        var w = FindRound(x =>
        {
            var a = Shoot(x, 30, new Vector2(1f, 0.45f));
            StepUntil(x, a, y => y.State is RoundState.Outcome or RoundState.Report || y.Events.Any(e => e.Kind == EventKind.BoardHit));
            return x.Events.Any(e => e.Kind == EventKind.BoardHit) && !x.Pitch.IsOverContact(x.Events.First(e => e.Kind == EventKind.BoardHit).Position, x.Ball.Radius);
        });
        Assert.Equal(RoundState.Live, w.State);
        Assert.Equal(Outcome.None, w.Outcome);
    }

    [Fact]
    public void HighChipHitsTheWallAboveTheBarAndPlayGoesOn()
    {
        var w = FindRound(x =>
        {
            var a = Shoot(x, 32, new Vector2(1f, 0f), chip: true);
            StepUntil(x, a, y => y.State is RoundState.Outcome or RoundState.Report || y.Events.Any(e => e.Kind == EventKind.BoardHit));
            return x.Events.Any(e => e.Kind == EventKind.BoardHit && x.Pitch.IsOverContact(e.Position, x.Ball.Radius)
                                     && e.Position.X >= x.Pitch.GoalLineX - x.Ball.Radius - 0.01f);
        });
        Assert.Equal(RoundState.Live, w.State);
        Assert.Equal(Outcome.None, w.Outcome);
        Assert.True(w.Ball.Velocity.X < 0f, "the ball comes back off the end wall");
    }

    [Fact]
    public void BallRollingIntoTheSideBoardComesBackIntoPlay()
    {
        var w = TestData.Live("edge_of_box");
        w.Ball.Position = new Vector3(15f, 0.11f, w.Pitch.HalfWidth - 0.3f);
        w.Ball.Velocity = new Vector3(0, 0, 6f);
        var buf = Buf(w);
        StepUntil(w, buf, x => x.Events.Any(e => e.Kind == EventKind.BoardHit), 400);
        Assert.Contains(w.Events, e => e.Kind == EventKind.BoardHit);
        Assert.Equal(RoundState.Live, w.State);
        Assert.True(w.Ball.Velocity.Z < 0f);
        for (int i = 0; i < 240; i++) w.Step(buf);
        Assert.Equal(RoundState.Live, w.State);
        Assert.True(MathF.Abs(w.Ball.Position.Z) <= w.Pitch.HalfWidth - w.Ball.Radius + 1e-3f);
        Assert.DoesNotContain(w.Events, e => e.Kind == EventKind.OutOfPlay);
    }

    [Fact]
    public void ClockEndsTheRoundAsTimeUp()
    {
        var sc = WithClock(2f);
        var w = new World(new Tuning(), sc, TestData.Player("striker_a"), TestData.Player("the_wall"), 1);
        w.Begin();
        var buf = Buf(w);
        int ticks = 0;
        while (w.State == RoundState.Live && ticks < 1000) { w.Step(buf); ticks++; }
        Assert.Equal(RoundState.Outcome, w.State);
        Assert.Equal(Outcome.TimeUp, w.Outcome);
        Assert.InRange(ticks, 239, 241);
        Assert.InRange(w.Clock, 1.99f, 2.01f);
        StepUntil(w, buf, x => x.State == RoundState.Report);
        Assert.Equal(Outcome.TimeUp, w.Report!.Outcome);
    }

    [Fact]
    public void ClockOnlyRunsInLive()
    {
        var w = TestData.Live("penalty");
        var a = Shoot(w, 54, new Vector2(1, 0));
        float atRelease = w.Clock;
        StepUntil(w, a, x => x.State == RoundState.Flight, 50);
        float clockAtFlight = w.Clock;
        Assert.InRange(clockAtFlight - atRelease, 0f, 0.02f);  // the strike delay did not count
        Assert.Equal(RoundState.Flight, w.State);
        for (int i = 0; i < 10; i++) w.Step(a);
        Assert.Equal(clockAtFlight, w.Clock, 5);
    }

    [Fact]
    public void FrameHitWithTheBallStillInPlayKeepsTheRoundLive()
    {
        bool found = false;
        for (ulong seed = 1; seed <= 600 && !found; seed++)
        {
            var w = TestData.Live("penalty", seed: seed);
            var script = new ScriptedAttacker();
            var buf = Buf(w);
            for (int i = 0; i < 3000 && w.State != RoundState.Outcome && w.State != RoundState.Report; i++)
            {
                var before = w.State;
                buf[0] = script.Next(w);
                w.Step(buf);
                if (before == RoundState.Flight && w.Events.Any(e => e.Kind == EventKind.FrameHit) && w.State == RoundState.Live)
                {
                    found = true;
                    Assert.Equal(Outcome.None, w.Outcome);
                    for (int k = 0; k < 20; k++) w.Step(buf);
                    Assert.NotEqual(RoundState.Report, w.State);
                    break;
                }
            }
        }
        Assert.True(found, "no post or bar rebound found in 600 penalties");
    }

    [Fact]
    public void ParryKeepsTheRoundLive()
    {
        bool found = false;
        for (ulong seed = 1; seed <= 600 && !found; seed++)
        {
            var w = TestData.Live("penalty", seed: seed);
            found = PlayUntil(w, x => x.Events.Any(e => e.Kind == EventKind.Save && !e.Held));
            if (found)
            {
                Assert.Equal(RoundState.Live, w.State);
                Assert.Equal(Outcome.None, w.Outcome);
                Assert.True(w.Ball.Velocity.Length() > 1f, "a parried ball is still moving");
            }
        }
        Assert.True(found);
    }

    [Fact]
    public void BallSittingOnTheGoalLineKeepsTheRoundLive()
    {
        var w = TestData.Live("penalty");
        // Inside the mouth near a post, out of the keeper's reach (he would claim a still ball next to him).
        w.Ball.Position = new Vector3(w.Pitch.GoalLineX + 0.05f, w.Ball.Radius, 3f);
        var buf = Buf(w);
        for (int i = 0; i < 120; i++) w.Step(buf);
        Assert.Equal(RoundState.Live, w.State);
        Assert.Equal(Outcome.None, w.Outcome);
    }

    [Fact]
    public void NotANumberEndsTheRoundAsOutAndIsLogged()
    {
        var w = TestData.Live("penalty", seed: 42);
        w.Ball.Position = new Vector3(float.NaN, 0.11f, 0f);
        w.Step(Buf(w));
        Assert.Equal(RoundState.Outcome, w.State);
        Assert.Equal(Outcome.Out, w.Outcome);
        Assert.Contains("seed 42", w.Diagnostics.Single());
    }

    [Fact]
    public void ResetWithTheSameSeedReplaysTheSameRound()
    {
        var w = TestData.Live("penalty", seed: 9);
        TestData.Play(w, new ScriptedAttacker());
        var first = w.Report!.ToJson();
        w.Reset(9);
        Assert.Equal(RoundState.Setup, w.State);
        Assert.Null(w.Report);
        w.Begin();
        TestData.Play(w, new ScriptedAttacker());
        Assert.Equal(first, w.Report!.ToJson());
    }

    [Fact]
    public void ClaimedExistsInTheEnumButIsNeverProducedYet()
    {
        Assert.True(Enum.IsDefined(Outcome.Claimed));
        var seen = new HashSet<Outcome>();
        for (ulong seed = 1; seed <= 150; seed++)
            seen.Add(RoundRunner.RunFirstShot(new Tuning(), TestData.Scenario("edge_of_box"), TestData.Player("striker_a"), TestData.Player("stand_in"), seed).Outcome);
        Assert.DoesNotContain(Outcome.Claimed, seen);
    }

    [Fact]
    public void OutcomeLabelsMatchTheSpec()
    {
        Assert.Equal("TIME_UP", Outcome.TimeUp.Label());
        Assert.Equal("GOAL", Outcome.Goal.Label());
    }
}
