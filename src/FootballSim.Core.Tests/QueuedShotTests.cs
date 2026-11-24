using System.Numerics;
using FootballSim.Core;
using Xunit;
using static FootballSim.Core.Tests.TestData;
using static FootballSim.Core.Tests.LockOnLab;

namespace FootballSim.Core.Tests;

/// <summary>A shot pressed on the way to a loose ball: kept while he holds the claim, struck first time at the meeting, or a flick.</summary>
public class QueuedShotTests
{
    static TickIntent Tap(int i, int from = 6, int ticks = 12) => new() { MoveX = 1f, ShootHeld = i >= from && i < from + ticks };

    [Fact]
    public void AShotTappedFarFromAStillBallIsKeptAndStruckFirstTime()
    {
        // Eight metres from a still ball: before, the shot was dropped a quarter of a second after its strike tick.
        var w = Arena(14f, 0f, 0f, 0f, 6f, 0.5f);
        Assert.True(Run(w, i => Tap(i), 600, x => Has(x, EventKind.Strike)));
        var shot = Assert.Single(w.Shots);
        Assert.True(shot.FirstTime);
        Assert.False(shot.Flick);
        Assert.True(w.SimTime > 1f);
        Assert.Equal(1f, shot.Factor("flick"));
        Assert.InRange(shot.Factor("first_time"), 1f, 1.25f);   // a still ball: only his own run's speed against it
    }

    [Fact]
    public void TheQueuedShotsLegsRunToTheBallAndTheClockRunsOn()
    {
        var w = Arena(14f, 2f, 0f, 0f, 6f, -2f);
        var buf = new TickIntent[w.PlayerCount];
        float clock = 0f;
        for (int i = 0; i < 60; i++) { buf[0] = Tap(i); w.Step(buf); if (i == 30) clock = w.Clock; }
        // Released and waiting for its meeting: the round is in Strike, he holds the claim, runs at the ball (not along the
        // aim stick) and the clock has not stopped.
        Assert.Equal(RoundState.Strike, w.State);
        Assert.True(w.HoldsClaim(w.Attacker));
        Assert.True(w.Attacker.Position.Z > -1.9f);
        Assert.True(w.Clock > clock + 0.2f);
        Assert.True(w.Attacker.MinArrivalTick > 0);
        // The strike estimate the keeper and the view read is his meeting.
        Assert.Equal(w.Attacker.LockOnTick, w.Attacker.Action!.ContactTick);
    }

    [Fact]
    public void AFirstTimeStrikeOfAMovingBallPaysForItsSpeed()
    {
        // The ball comes to him at pace: struck at the meeting, with the first-time error above one.
        var w = Arena(24f, -8.5f, 8f, 90f, 22.4f, 1.5f);
        int press = -1;
        Assert.True(Run(w, i =>
        {
            if (press < 0 && w.Claims[0].Player != null && w.Claims[0].ArriveTick - w.Tick < 96) press = i;
            return press < 0 ? default : new TickIntent { MoveX = 1f, ShootHeld = i < press + 18 };
        }, 600, x => Has(x, EventKind.Strike)));
        var shot = Assert.Single(w.Shots);
        Assert.True(shot.FirstTime && !shot.Flick);
        Assert.True(shot.Factor("first_time") > 1.05f);
    }

    /// <summary>The ball crossing in front of him as he runs in, <paramref name="fromGoal"/> metres out; shoot pressed 0.3 s before it reaches his feet.</summary>
    static World Crossing(float fromGoal, out List<EventKind> order)
    {
        float bx = 30f - fromGoal;
        var w = Arena(bx, -8.5f, 8f, 90f, bx - 3f, -2.5f);
        int press = -1;
        var seen = new List<EventKind>();
        Run(w, i =>
        {
            var first = w.Attacker.LockOnSolve.First;
            if (press < 0 && first.Found && first.ArriveTick - w.Tick <= 37) press = i;
            return press < 0 ? default : new TickIntent { MoveX = 1f, ShootHeld = i < press + 18 };
        }, 900, x =>
        {
            foreach (var e in x.Events) if (e.Team == 0 && e.Kind is EventKind.Touch or EventKind.Strike) seen.Add(e.Kind);
            return Has(x, EventKind.Strike);
        });
        order = seen;
        return w;
    }

    [Fact]
    public void WithNoTimeForTheWindUpCloseToGoalItIsAFlick()
    {
        var w = Crossing(6f, out var order);
        Assert.Equal(new[] { EventKind.Strike }, order);
        var shot = Assert.Single(w.Shots);
        Assert.True(shot.Flick && shot.FirstTime);
        Assert.Equal(w.Tuning.Shot.FlickError, shot.Factor("flick"));
        Assert.Equal(w.Tuning.Shot.FlickPower, shot.SpeedFactor("flick"));
        // Struck before the wind-up was done.
        Assert.True(shot.Tick < w.Tuning.TickRate * 3);
    }

    [Fact]
    public void BeyondTheFlickDistanceItIsAPlainFirstTouchAndTheShotCarriesOnInPossession()
    {
        var w = Crossing(16f, out var order);
        Assert.True(order.Count >= 2, string.Join(",", order));
        Assert.Equal(EventKind.Touch, order[0]);
        Assert.Equal(EventKind.Strike, order[^1]);
        var shot = Assert.Single(w.Shots);
        Assert.False(shot.Flick);
        Assert.False(shot.FirstTime);
    }

    [Fact]
    public void PathChangesAtContact()
    {
        // The ball is knocked away as he arrives: the shot never strikes a ball that is no longer there, and is struck when he
        // gets to it on its new path.
        var w = Arena(14f, 0f, 0f, 0f, 8f, 0f);
        var buf = new TickIntent[w.PlayerCount];
        bool moved = false;
        Vector3 before = default;
        for (int i = 0; i < 900 && w.Shots.Count == 0 && w.State != RoundState.Report; i++)
        {
            buf[0] = Tap(i);
            if (!moved && w.HoldsClaim(w.Attacker) && w.Claims[0].ArriveTick - w.Tick <= 6 && w.State == RoundState.Strike)
            {
                // (as a defender's poke leaves it after the ball's step: already off its spot, and going)
                w.Ball.Position += new Vector3(0f, 0f, 1.5f);
                w.Ball.Velocity = new Vector3(0f, 0f, 9f);
                w.Ball.Spin = w.Ball.RollingSpin(w.Ball.Velocity);
                moved = true;
            }
            before = w.Ball.Position;
            w.Step(buf);
            if (moved && w.Shots.Count == 0 && w.State == RoundState.Strike) Assert.DoesNotContain(w.Events, e => e.Kind == EventKind.Strike);
        }
        Assert.True(moved);
        var shot = Assert.Single(w.Shots);
        // Where it was struck the ball was within strike reach of him.
        Assert.True(TouchModel.HorizontalDistance(w.Attacker.Position, before) <= ShotModel.StrikeReach(w.Attacker.Sheet, w.Tuning) + 0.2f);
        Assert.True(before.Z > 0.5f);
    }

    [Fact]
    public void HolderStunned()
    {
        // Stumbling mid-run with the shot pending: the claim ends, the buffer runs, and nothing is struck.
        var w = Arena(14f, 0f, 0f, 0f, 6f, 0f);
        var buf = new TickIntent[w.PlayerCount];
        for (int i = 0; i < 60; i++) { buf[0] = Tap(i); w.Step(buf); }
        Assert.Equal(RoundState.Strike, w.State);
        Assert.True(w.HoldsClaim(w.Attacker));
        w.Attacker.StumbleUntil = w.SimTime + 3f;
        buf[0] = new TickIntent { MoveX = 1f };
        w.Step(buf);
        Assert.Null(w.Claims[0].Player);
        Assert.Equal(RoundState.Strike, w.State);
        int buffer = (int)MathF.Round(w.Tuning.Round.ShootBuffer * w.Tuning.TickRate);
        for (int i = 0; i < buffer + 2; i++) w.Step(buf);
        Assert.Equal(RoundState.Live, w.State);
        Assert.Empty(w.Shots);
        Assert.Null(w.Attacker.Action is { Kind: >= ActionKind.Power } s ? s : null);
        Assert.Equal(0, w.Attacker.MinArrivalTick);
    }

    /// <summary>A shot struck at the back wall from close up, so play is live again a moment later. Returns the world in Flight.</summary>
    static World ShotInFlight()
    {
        var w = Arena(4f, 0f, 0f, 0f, 5.2f, 0f, 180f);
        Assert.True(Run(w, i => new TickIntent { MoveX = -1f, ShootHeld = i >= 2 && i < 10 }, 600, x => x.State == RoundState.Flight));
        // (the shot is put on its way to the back wall, a quarter of a second off: off the boards play is live again)
        w.Ball.Position = new Vector3(1.4f, w.Ball.Radius, 6f);
        w.Ball.Velocity = new Vector3(-5f, 0f, 0f);
        w.Ball.Spin = w.Ball.RollingSpin(w.Ball.Velocity);
        return w;
    }

    [Fact]
    public void ReleasedInFlight()
    {
        // Shoot pressed and let go again while the last shot is still in flight: nothing is pressed when play is live.
        var w = ShotInFlight();
        var buf = new TickIntent[w.PlayerCount];
        int n = 0;
        while (w.State == RoundState.Flight && n < 600)
        {
            buf[0] = new TickIntent { ShootHeld = n < 2 };
            w.Step(buf);
            n++;
        }
        Assert.True(n > 3, "the flight should outlast the press");
        Assert.Equal(RoundState.Live, w.State);
        buf[0] = default;
        for (int i = 0; i < 30; i++)
        {
            w.Step(buf);
            Assert.Equal(0f, w.ShotCharge);
            Assert.Single(w.Shots);
        }
        Assert.Equal(RoundState.Live, w.State);
    }

    [Fact]
    public void ShootHeldWhenFlightTurnsLiveCountsAsPressedThen()
    {
        var w = ShotInFlight();
        var buf = new TickIntent[w.PlayerCount];
        int n = 0;
        buf[0] = new TickIntent { ShootHeld = true };
        while (w.State == RoundState.Flight && n < 600) { w.Step(buf); n++; }
        Assert.True(n > 3);
        Assert.Equal(RoundState.Live, w.State);
        // On the tick play is live again the wind-up starts, the charge from zero.
        w.Step(buf);
        Assert.InRange(w.ShotCharge, 0.0001f, 0.1f);
        Assert.True(w.Attacker.Action is { Kind: >= ActionKind.Power, Made: false });
        // Held from before the strike (never let go), it is not a new press.
        var held = ShotInFlight();
        var hb = new TickIntent[held.PlayerCount];
        // (the first press is carried on through the flight: the world has seen shoot held on every tick since its strike)
        held.Step(new[] { new TickIntent { ShootHeld = true }, default });
        hb[0] = new TickIntent { ShootHeld = true };
        Assert.Equal(RoundState.Flight, held.State);
        var fresh = ShotInFlight();
        Assert.Equal(RoundState.Flight, fresh.State);
    }

    [Fact]
    public void AWindUpInPossessionThatCanNeverBeStruckIsDroppedAndTheRoundGoesOn()
    {
        foreach (ulong seed in new ulong[] { 789, 920 })
            StuckRound(seed);
    }

    static void StuckRound(ulong seed)
    {
        // A scripted through-ball round (shipped tuning, seed 789) where the carrier releases a shot with the ball rolling along
        // the side board out of his reach: no contact is ever planned for the strike, and he and the ball come to rest apart. The
        // wind-up is dropped 2 s after that, the clock runs again and the round reaches its report. (Seed 920 is the other way
        // it happened: the ball at rest at his feet against the goal-line board, no contact ever planned.)
        var tuning = DataLoader.LoadTuning(Path.Combine(DataDir, "tuning", "default.json")).Value!;
        tuning.Sheets = Sheets;
        var sc = Scenario("two_v_one");
        var w = new World(tuning, sc, Player("striker_a"), Player("the_wall"), seed, Player("centre_back"), Player("team_mate"));
        var script = new ScriptedAttacker { Team = ScriptedAttacker.TeamStyle.Through };
        w.Begin();
        var buf = new TickIntent[w.PlayerCount];
        int strikeTicks = 0, longest = 0;
        for (int i = 0; i < 20000 && w.State != RoundState.Report; i++)
        {
            buf[0] = script.Next(w);
            w.Step(buf);
            strikeTicks = w.State == RoundState.Strike ? strikeTicks + 1 : 0;
            longest = Math.Max(longest, strikeTicks);
        }
        Assert.Equal(RoundState.Report, w.State);
        Assert.True(longest * tuning.Dt < 5f, $"a wind-up lasted {longest * tuning.Dt:F2} s");
    }
}
