using System.Numerics;
using FootballSim.Core;
using Xunit;
using static FootballSim.Core.Tests.TestData;

namespace FootballSim.Core.Tests;

/// <summary>Who the team's intent drives: control follows the ball (see World.ControlledIndex).</summary>
public class ControlTests
{
    /// <summary>
    /// The arena with a team-mate: the attacker at (ax, az), the mate at (mx, mz), a loose ball at (bx, bz) rolling at
    /// <paramref name="speed"/> along <paramref name="headingDeg"/>; the keeper holds his line; a defender when a spot is given.
    /// </summary>
    public static World Pair(float bx, float bz, float ax, float az, float mx, float mz, float speed = 0f, float headingDeg = 0f,
        (float X, float Z)? defender = null, bool loose = true, bool offside = false, ulong seed = 1, Tuning? tuning = null)
    {
        var sc = new Scenario
        {
            Id = "lab_pair", Pitch = Scenario("loose_parry").Pitch, TeamSize = 2,
            Ball = new StartSpot { X = bx, Z = bz }, BallSpeed = speed, BallHeadingDeg = headingDeg, BallLoose = loose,
            Attacker = new StartSpot { X = ax, Z = az }, Mate = new StartSpot { X = mx, Z = mz },
            KeeperOnLine = true, KeeperHoldLine = true, KeeperHoldLineThroughDribble = true,
            Defenders = defender != null ? 1 : 0,
            Defender = defender is { } d ? new StartSpot { X = d.X, Z = d.Z } : new StartSpot(),
            Offside = offside, ClockLimit = 20f,
        };
        var w = new World(tuning ?? new Tuning(), sc, Player("striker_a"), Player("the_wall"), seed, defender != null ? Player("centre_back") : null, Player("team_mate"));
        w.Begin();
        return w;
    }

    static int Changes(World w) => w.Events.Count(e => e.Kind == EventKind.ControlChanged);

    [Fact]
    public void TwoVOneHasATeamMateAndOnlyTheKeeperAndDefenderAgainstHim()
    {
        var sc = Scenario("two_v_one");
        Assert.NotNull(sc.Mate);
        Assert.True(sc.Offside);
        var w = new World(Tuning(), sc, Player(sc.DefaultAttacker), Player(sc.DefaultKeeper), 1, Player(sc.DefaultDefender), Player(sc.DefaultMate));
        Assert.True(w.HasMates);
        Assert.Equal(2, w.Teams[0].Count);
        Assert.Equal(2, w.Teams[1].Count);
        Assert.Same(w.Teams[1][1], w.Defender);
        Assert.Equal("team_mate", w.Teams[0][1].Sheet.Id);
        Assert.Equal(sc.Mate!.X, w.Teams[0][1].Position.X, 3);
        Assert.Equal(sc.Mate.Z, w.Teams[0][1].Position.Z, 3);
        Assert.Equal(0, w.ControlledIndex);
        Assert.Same(w.Teams[0][0], w.Attacker);
        // A scenario without a team-mate keeps its standing extras, and no mates.
        var plain = new World(Tuning(), new Scenario { TeamSize = 3 }, Sheet(), Sheet(), 1);
        Assert.False(plain.HasMates);
        Assert.Equal(3, plain.Teams[1].Count);
    }

    [Fact]
    public void WithTheBallLooseControlJumpsToTheTeamsClaimHolder()
    {
        // A still ball beside the team-mate, far from the attacker: after claim_jump_seconds the pad drives the team-mate.
        var w = Pair(15f, 5f, 4f, -5f, 13f, 5f);
        var buf = new TickIntent[w.PlayerCount];
        int jumpTick = -1;
        for (int i = 0; i < 60 && jumpTick < 0; i++)
        {
            w.Step(buf);
            if (Changes(w) > 0) jumpTick = w.Tick;
        }
        int wait = (int)MathF.Round(w.Tuning.Control.ClaimJumpSeconds * w.Tuning.TickRate);
        Assert.InRange(jumpTick, wait, wait + 2);
        var e = w.Events.Single(x => x.Kind == EventKind.ControlChanged);
        Assert.Equal((0, 1, 0, ControlReason.Claim), (e.Team, e.PlayerIndex, e.Other, e.Reason));
        Assert.Equal(1, w.ControlledIndex);
        Assert.Same(w.Teams[0][1], w.Attacker);
        // He goes for the ball by himself and the pad's player gets it.
        for (int i = 0; i < 360 && w.BallController == null; i++) w.Step(buf);
        Assert.Same(w.Teams[0][1], w.BallController);
        // The other attacker is now the team-mate: he never touched the ball.
        Assert.False(w.Teams[0][0].InPossession);
    }

    [Fact]
    public void ClaimHysteresis()
    {
        // Both attackers equally near a rebound: control goes to one of them once and stays.
        var w = Pair(15f, 0f, 9f, -3f, 9f, 3f);
        var buf = new TickIntent[w.PlayerCount];
        int jumps = 0;
        for (int i = 0; i < 360 && w.BallController == null; i++) { w.Step(buf); jumps += Changes(w); }
        Assert.NotNull(w.BallController);
        Assert.InRange(jumps, 0, 1);
        Assert.Same(w.Attacker, w.BallController);
    }

    [Fact]
    public void HeldAcrossJump()
    {
        // Shoot held as control jumps: the new player does not take the hold as a fresh press.
        var w = Pair(15f, 5f, 4f, -5f, 13f, 5f);
        var buf = new TickIntent[w.PlayerCount];
        buf[0] = new TickIntent { ShootHeld = true };
        bool jumped = false;
        for (int i = 0; i < 60; i++)
        {
            w.Step(buf);
            jumped |= Changes(w) > 0;
            if (jumped)
            {
                Assert.Equal(0f, w.ShotCharge);
                Assert.Null(w.Attacker.Action is { IsShot: true } s ? s : null);
            }
        }
        Assert.True(jumped);
        Assert.Equal(RoundState.Live, w.State);
        // The old player's shot was dropped with the jump.
        Assert.Null(w.Teams[0][0].Action is { IsShot: true } old ? old : null);
        // Let go and pressed again, it is a press.
        buf[0] = default; w.Step(buf);
        buf[0] = new TickIntent { ShootHeld = true }; w.Step(buf);
        Assert.True(w.ShotCharge > 0f);
    }

    [Fact]
    public void AfterAJumpTheStickReadsAsAtRestUntilItTurnsOrTheSettleTimeIsOver()
    {
        World Jumped(TickIntent held)
        {
            var w = Pair(15f, 5f, 4f, -5f, 13f, 5f);
            var buf = new TickIntent[w.PlayerCount];
            buf[0] = held;
            for (int i = 0; i < 60 && w.ControlledIndex == 0; i++) w.Step(buf);
            Assert.Equal(1, w.ControlledIndex);
            return w;
        }
        // The stick held straight back (away from the ball, the modifier not held): the new player still takes the earliest
        // meeting, as with the stick at rest; turned 90 degrees, the push is read at once.
        var back = new TickIntent { MoveX = -1f };
        var w = Jumped(back);
        var buf = new TickIntent[w.PlayerCount];
        buf[0] = back;
        w.Step(buf);
        Assert.False(w.Claims[0].Slid);
        int settle = (int)MathF.Round(w.Tuning.Control.StickSettleSeconds * w.Tuning.TickRate);
        // Past the settle time the same stick is a push again: with the modifier it lets the ball run.
        buf[0] = new TickIntent { MoveX = -1f, Finesse = true };
        var v = Jumped(buf[0]);
        var vb = new TickIntent[v.PlayerCount];
        vb[0] = new TickIntent { MoveX = -1f, Finesse = true };
        v.Step(vb);
        Assert.True(v.HoldsClaim(v.Attacker));           // at rest: he still goes for it
        for (int i = 0; i < settle + 2; i++) v.Step(vb);
        Assert.False(v.HoldsClaim(v.Attacker));          // read again: let it run
    }

    [Fact]
    public void TheTeamMateIsABodyOthersCannotStandIn()
    {
        var w = Pair(4.5f, 0f, 4f, 0f, 4.3f, 0.2f, loose: false);
        var buf = new TickIntent[w.PlayerCount];
        for (int i = 0; i < 30; i++) w.Step(buf);
        float min = 2f * w.Tuning.Defending.BodyRadiusM;
        Assert.True(Vector3.Distance(w.Teams[0][0].Position, w.Teams[0][1].Position) >= min - 0.01f);
    }

    [Fact]
    public void ARoundWithATeamMateReplaysToTheSameReport()
    {
        var sc = Scenario("two_v_one");
        var t = Tuning();
        var report = RoundRunner.RunToReport(t, sc, Player(sc.DefaultAttacker), Player(sc.DefaultKeeper), 5, out var replay,
            defender: Player(sc.DefaultDefender), mate: Player(sc.DefaultMate));
        Assert.Equal("team_mate", replay.MateId);
        Assert.NotNull(replay.TeamHash);
        Assert.True(replay.MatchesTuning(t));
        var again = replay.Run(t, sc, Player(sc.DefaultAttacker), Player(sc.DefaultKeeper), Player(sc.DefaultDefender), Player(sc.DefaultMate));
        Assert.Equal(report.ToJson(), again.ToJson());
        // A round without one records neither, and its tuning hash does not move with the team values.
        RoundRunner.RunToReport(t, Scenario("penalty"), Player("striker_a"), Player("the_wall"), 5, out var plain);
        Assert.Null(plain.MateId);
        Assert.Null(plain.TeamHash);
        var changed = new Tuning();
        changed.Pass.ChargeSeconds = 0.5f; changed.Mate.SampleM = 3f; changed.Control.ClaimJumpSeconds = 0.2f; changed.LockOn.ReactionMaxSeconds = 1f;
        changed.Receive.QueuedCooldown = 0.2f;
        Assert.Equal(DataLoader.TuningHash(t), DataLoader.TuningHash(changed));
        Assert.NotEqual(DataLoader.TeamHash(t), DataLoader.TeamHash(changed));
    }
}
