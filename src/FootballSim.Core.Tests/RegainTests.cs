using System.Numerics;
using FootballSim.Core;
using Xunit;
using static FootballSim.Core.Tests.TestData;

namespace FootballSim.Core.Tests;

/// <summary>The eager regain of a loose ball (see <see cref="Regain"/>).</summary>
public class RegainTests
{
    /// <summary>An open lab pitch: the attacker at x = 2 facing +X, the keeper on his line 50 m away, nothing touched yet.</summary>
    static World Lab(ulong seed = 1, Tuning? tuning = null)
    {
        var sc = new Scenario
        {
            Id = "lab",
            Pitch = new PitchSpec { Length = 52.5f },
            Ball = new StartSpot { X = 2.5f, Z = 0f },
            Attacker = new StartSpot { X = 2.0f, Z = 0f },
        };
        var w = new World(tuning ?? new Tuning(), sc, Sheet(), Sheet(50), seed);
        w.Begin();
        return w;
    }

    /// <summary>Puts the loose ball at (dx, dz) from the attacker, rolling at (vx, vz), as a ball he has lost.</summary>
    static void Loose(World w, float dx, float dz, float vx = 0f, float vz = 0f)
    {
        w.BallPlayed = true;
        var a = w.Attacker.Position;
        w.Ball.Position = new Vector3(a.X + dx, w.Ball.Radius, a.Z + dz);
        w.Ball.Velocity = new Vector3(vx, 0f, vz);
    }

    /// <summary>Steps with the attacker's intent until he gains the ball or the ticks run out; the tick count, or -1.</summary>
    static int TicksToRegain(World w, TickIntent intent, int max, List<RegainMode>? modes = null)
    {
        var buf = new TickIntent[w.PlayerCount];
        for (int i = 1; i <= max; i++)
        {
            buf[0] = intent;
            w.Step(buf);
            modes?.Add(w.Attacker.Regain);
            if (w.BallController == w.Attacker) return i;
        }
        return -1;
    }

    [Fact]
    public void AnUncontestedBallRollingAwayAheadIsRegainedWithTheStickReleased()
    {
        // 4 m ahead and 1.5 m to the side, rolling on at 4 m/s: without the regain a released stick leaves it.
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var w = Lab(seed);
            Loose(w, 4f, 1.5f, 4f, 0f);
            var modes = new List<RegainMode>();
            int ticks = TicksToRegain(w, default, 360, modes);
            Assert.InRange(ticks, 1, 300);
            Assert.All(modes.Take(ticks - 1), m => Assert.Equal(RegainMode.Free, m));
        }
        var off = new Tuning();
        off.Possession.RegainMaxDistance = 0f;
        var still = Lab(1, off);
        Loose(still, 4f, 1.5f, 4f, 0f);
        Assert.Equal(-1, TicksToRegain(still, default, 360));
    }

    [Fact]
    public void AStickPushedAcrossTheBallStillGoesForIt()
    {
        // The stick 90 deg off the ball (not away from it), sprint released: he still runs on to it.
        var w = Lab();
        Loose(w, 3f, 0f, 3f, 0f);
        Assert.InRange(TicksToRegain(w, new TickIntent { MoveZ = 1f }, 360), 1, 300);
    }

    [Fact]
    public void AnUncontestedStillBallBeyondFootReachIsTakenWithinRegainReach()
    {
        var t = new Tuning();
        float reach = TouchModel.FootReach(Sheet(), t);
        float d = (reach + t.Possession.RegainReach) / 2f;
        Assert.True(d > reach + 0.1f);
        var w = Lab(1, t);
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
        var w = Lab(1, t);
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
        var t = new Tuning();
        var ball = new Ball(t.Ball) { Position = new Vector3(10f, t.Ball.Radius, 0f) };
        var att = Outfield(x: 4f);
        var keeper = new PlayerState(1, 0, PlayerRole.Keeper, Sheet(50)) { Position = new Vector3(20f, 0f, 0f) };
        Assert.False(Regain.Contested(att, ball, keeper, t));
        Assert.Equal(RegainMode.Free, Regain.Assess(att, default, ball, keeper, t));
        // Moved in until he would be there within the margin of the attacker: contested, though more than 3 m away.
        keeper.Position = new Vector3(15f, 0f, 0f);
        Assert.True(Regain.Contested(att, ball, keeper, t));
        Assert.Equal(RegainMode.Contested, Regain.Assess(att, default, ball, keeper, t));
    }

    [Fact]
    public void ABallLooseAtTheStartOfTheRoundIsNotGoneFor()
    {
        // The ball on the spot before anyone has played it: he stands by it with the stick released.
        var t = new Tuning();
        float reach = TouchModel.FootReach(Sheet(), t);
        var w = Lab(1, t);
        Loose(w, (reach + t.Possession.RegainReach) / 2f, 0f);
        w.BallPlayed = false;
        var modes = new List<RegainMode>();
        Assert.Equal(-1, TicksToRegain(w, default, 120, modes));
        Assert.All(modes, m => Assert.Equal(RegainMode.None, m));
        Assert.True(w.Attacker.Speed < 0.1f);
    }

    [Fact]
    public void ABallBehindThePlayerIsNotChased()
    {
        var w = Lab();
        Loose(w, -3f, 0f, -2f, 0f);
        var modes = new List<RegainMode>();
        Assert.Equal(-1, TicksToRegain(w, default, 240, modes));
        Assert.All(modes, m => Assert.Equal(RegainMode.None, m));
        Assert.True(w.Attacker.Speed < 0.1f);
    }

    [Fact]
    public void ABallBeyondTheMaxDistanceOrOutsideTheConeIsNotChased()
    {
        var t = new Tuning();
        var keeper = new PlayerState(1, 0, PlayerRole.Keeper, Sheet(50)) { Position = new Vector3(50f, 0f, 0f) };
        var att = Outfield();
        Ball At(float x, float z) => new(t.Ball) { Position = new Vector3(x, t.Ball.Radius, z) };
        Assert.Equal(RegainMode.Free, Regain.Assess(att, default, At(9f, 0f), keeper, t));
        Assert.Equal(RegainMode.None, Regain.Assess(att, default, At(11f, 0f), keeper, t));
        Assert.Equal(RegainMode.Free, Regain.Assess(att, default, At(3f, 3f * MathF.Tan(70f * MathF.PI / 180f)), keeper, t));
        Assert.Equal(RegainMode.None, Regain.Assess(att, default, At(3f, 3f * MathF.Tan(80f * MathF.PI / 180f)), keeper, t));
    }

    [Fact]
    public void FinesseHeldWithTheStickAwayLetsTheBallRun()
    {
        var w = Lab();
        Loose(w, 3f, 0f, 3f, 0f);
        var away = new TickIntent { MoveX = -1f, Finesse = true };
        var modes = new List<RegainMode>();
        Assert.Equal(-1, TicksToRegain(w, away, 240, modes));
        Assert.All(modes, m => Assert.Equal(RegainMode.None, m));
        // The stick away without the modifier does not: he still goes for it.
        var v = Lab();
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
        // The stick released throughout: who gets there first is the regain (and the keeper) alone.
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
    public void TheRegainIsDeterministic()
    {
        string Trace(ulong seed)
        {
            var w = Lab(seed);
            Loose(w, 4f, 1.5f, 4f, 0f);
            var buf = new TickIntent[w.PlayerCount];
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 480; i++)
            {
                buf[0] = i < 200 ? default : new TickIntent { MoveX = 1f, MoveZ = 0.3f };
                w.Step(buf);
                sb.Append(w.Attacker.Position).Append(w.Ball.Position).Append(w.Attacker.Regain).Append('|');
            }
            return sb.ToString();
        }
        Assert.Equal(Trace(3), Trace(3));
    }
}
