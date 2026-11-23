using System.Numerics;
using FootballSim.Core;
using Xunit;
using static FootballSim.Core.Tests.TestData;
using static FootballSim.Core.Tests.LockOnLab;

namespace FootballSim.Core.Tests;

/// <summary>Going for a loose ball: the attacker's regain mode and first touch, as his team's claim gives them (see <see cref="LockOn"/>).</summary>
public class RegainTests
{
    [Fact]
    public void AnUncontestedBallRollingAwayAheadIsRegainedWithTheStickReleased()
    {
        // 4 m ahead and 1.5 m to the side, rolling on at 4 m/s.
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var w = Open(seed);
            Loose(w, 4f, 1.5f, 4f, 0f);
            var modes = new List<RegainMode>();
            int ticks = TicksToRegain(w, default, 360, modes);
            Assert.InRange(ticks, 1, 300);
            Assert.All(modes.Take(ticks - 1), m => Assert.Equal(RegainMode.Free, m));
        }
    }

    [Fact]
    public void AStickPushedAcrossTheBallStillGoesForIt()
    {
        // The stick 90 deg off the ball (not away from it), sprint released: he still runs on to it.
        var w = Open();
        Loose(w, 3f, 0f, 3f, 0f);
        Assert.InRange(TicksToRegain(w, new TickIntent { MoveZ = 1f }, 360), 1, 300);
    }

    [Fact]
    public void ABallBehindHimIsGoneForFromAnyDistance()
    {
        // Behind him rolling away, and far beyond the old 10 m limit: he turns and gets both, with the stick at rest.
        var w = Open();
        w.Attacker.Position = new Vector3(20f, 0f, 0f);
        Loose(w, -4f, 0f, -2f, 0f);
        Assert.InRange(TicksToRegain(w, default, 600), 1, 480);
        var far = Open();
        Loose(far, 14f, 2f);
        var buf = new TickIntent[far.PlayerCount];
        far.Step(buf);
        Assert.True(far.HoldsClaim(far.Attacker));
        Assert.NotEqual(RegainMode.None, far.Attacker.Regain);
        Assert.InRange(TicksToRegain(far, default, 600), 1, 480);
    }

    [Fact]
    public void AnUncontestedStillBallBeyondFootReachIsTakenWithinRegainReach()
    {
        var t = new Tuning();
        float reach = TouchModel.FootReach(Sheet(), t);
        float d = (reach + t.Possession.RegainReach) / 2f;
        Assert.True(d > reach + 0.1f);
        var w = Open(1, t);
        Loose(w, d, 0f);
        Assert.Equal(1, TicksToRegain(w, default, 1));
        var touch = Assert.Single(w.Events, e => e.Kind == EventKind.Touch);
        Assert.Equal(0, touch.Team);
    }

    [Fact]
    public void AContestedBallIsRacedForButNotTakenBeyondFootReach()
    {
        var t = new Tuning();
        float reach = TouchModel.FootReach(Sheet(), t);
        float d = (reach + t.Possession.RegainReach) / 2f;
        var w = Open(1, t);
        Loose(w, d, 0f);
        // The keeper 2 m from the ball (inside regain_keeper_distance): a contest.
        w.Keeper.Position = new Vector3(w.Ball.Position.X + 2f, 0f, w.Ball.Position.Z);
        var buf = new TickIntent[w.PlayerCount];
        w.Step(buf);
        Assert.Equal(RegainMode.Contested, w.Attacker.Regain);
        Assert.DoesNotContain(w.Events, e => e.Kind == EventKind.Touch);
        Assert.Null(w.BallController);
        // He still runs at it (the race), rather than standing by a released stick.
        Assert.True(w.Attacker.Velocity.X > 0f);
    }

    [Fact]
    public void TheKeeperArrivingAboutAsSoonContestsTheBall()
    {
        var w = Open();
        Loose(w, 6f, 0f);
        var buf = new TickIntent[w.PlayerCount];
        w.Keeper.Position = new Vector3(w.Ball.Position.X + 30f, 0f, 0f);
        w.Step(buf);
        Assert.Equal(RegainMode.Free, w.Attacker.Regain);
        // Moved in until his rush would be there within the margin of the attacker: contested, though more than 3 m away.
        w.Keeper.Position = new Vector3(w.Ball.Position.X + 5f, 0f, 0f);
        for (int i = 0; i < 6; i++) w.Step(buf);
        Assert.Equal(RegainMode.Contested, w.Attacker.Regain);
    }

    [Fact]
    public void ABallLooseAtTheStartOfTheRoundIsNotGoneFor()
    {
        // The ball on the spot before anyone has played it: he stands by it with the stick released.
        var t = new Tuning();
        float reach = TouchModel.FootReach(Sheet(), t);
        var w = Open(1, t);
        Loose(w, (reach + t.Possession.RegainReach) / 2f, 0f);
        w.BallPlayed = false;
        var modes = new List<RegainMode>();
        Assert.Equal(-1, TicksToRegain(w, default, 120, modes));
        Assert.All(modes, m => Assert.Equal(RegainMode.None, m));
        Assert.Null(w.Claims[0].Player);
        Assert.True(w.Attacker.Speed < 0.1f);
    }

    [Fact]
    public void FinesseHeldWithTheStickAwayLetsTheBallRun()
    {
        var w = Open();
        Loose(w, 3f, 0f, 3f, 0f);
        var away = new TickIntent { MoveX = -1f, Finesse = true };
        var modes = new List<RegainMode>();
        Assert.Equal(-1, TicksToRegain(w, away, 240, modes));
        Assert.All(modes, m => Assert.Equal(RegainMode.None, m));
        // The stick away without the modifier does not: he still goes for it.
        var v = Open();
        Loose(v, 3f, 0f, 3f, 0f);
        var buf = new TickIntent[v.PlayerCount];
        buf[0] = new TickIntent { MoveX = -1f };
        v.Step(buf);
        Assert.Equal(RegainMode.Free, v.Attacker.Regain);
    }

    [Theory]
    [InlineData("loose_heavy_touch", true)]
    [InlineData("loose_fifty_fifty", true)]
    [InlineData("loose_parry", true)]
    [InlineData("loose_across", true)]
    [InlineData("loose_keepers_ball", false)]
    public void LooseBallStartSpotsBeginWithALostBall(string id, bool attackerFirst)
    {
        // The stick released throughout: who gets there first is the claim (and the keeper) alone.
        var w = Live(id);
        Assert.True(w.BallPlayed);
        var buf = new TickIntent[w.PlayerCount];
        PlayerState? first = null;
        for (int i = 0; i < 600 && first == null && w.State == RoundState.Live; i++) { w.Step(buf); first = w.BallController; }
        Assert.Same(attackerFirst ? w.Attacker : w.Keeper, first);
    }

    [Fact]
    public void ABallSpeedRollsTheBallFromTheStart()
    {
        var sc = Scenario("loose_parry");
        var w = Live("loose_parry");
        float h = sc.BallHeadingDeg * MathF.PI / 180f;
        Assert.Equal(sc.BallSpeed * MathF.Cos(h), w.Ball.Velocity.X, 3);
        Assert.Equal(sc.BallSpeed * MathF.Sin(h), w.Ball.Velocity.Z, 3);
        Assert.False(Live("penalty").BallPlayed);
    }

    [Fact]
    public void AKnockOnAskedForOnTheWayIsTheFirstTouch()
    {
        var w = Open();
        Loose(w, 5f, 0f, 1f, 0f);
        bool touched = Run(w, i => new TickIntent { MoveX = 1f, Sprint = 1f, KnockOn = i == 10 }, 480, x => Has(x, EventKind.Touch));
        Assert.True(touched);
        Assert.Equal(ActionKind.KnockOn, w.Attacker.TouchAction!.Kind);
    }

    [Fact]
    public void TheRegainIsDeterministic()
    {
        string Trace(ulong seed)
        {
            var w = Open(seed);
            Loose(w, 4f, 1.5f, 4f, 0f);
            var buf = new TickIntent[w.PlayerCount];
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 480; i++)
            {
                buf[0] = i < 200 ? default : new TickIntent { MoveX = 1f, MoveZ = 0.3f };
                w.Step(buf);
                sb.Append(w.Attacker.Position).Append(w.Ball.Position).Append(w.Attacker.Regain).Append(w.Attacker.LockOnTick).Append('|');
            }
            return sb.ToString();
        }
        Assert.Equal(Trace(3), Trace(3));
    }

    [Fact]
    public void ATuningFileWithTheOldRegainKeysLoadsWithAWarningEach()
    {
        var r = DataLoader.ParseTuning("{ \"possession\": { \"regain_cone_deg\": 75, \"regain_max_distance\": 10, \"regain_reach\": 1.1 } }");
        Assert.True(r.Ok, r.Error);
        Assert.Equal(1.1f, r.Value!.Possession.RegainReach);
        Assert.Contains(r.Warnings, x => x.Field == "possession.regain_cone_deg");
        Assert.Contains(r.Warnings, x => x.Field == "possession.regain_max_distance");
    }
}
