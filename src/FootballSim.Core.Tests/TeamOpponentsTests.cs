using System.Numerics;
using FootballSim.Core;
using Xunit;
using static FootballSim.Core.Tests.TestData;
using static FootballSim.Core.Tests.ControlTests;

namespace FootballSim.Core.Tests;

/// <summary>The defender and the keeper against two attackers: the reaction delay, the anchor on the meeting point, the scripted styles and the team's report.</summary>
public class TeamOpponentsTests
{
    [Fact]
    public void TheDefenderMakesNoClaimRunUntilHisReactionDelayIsOver()
    {
        var w = WithBall(8f, -4f, 8f, 4f, defender: (13f, 0f));
        w.DefenderBotEnabled = false;
        Assert.False(w.DefenderReacting);
        Assert.True(StrikePass(w, holdTicks: 3));
        Assert.True(w.DefenderReacting);
        var struckAt = w.Ball.Position;
        float need = TouchModel.HorizontalDistance(w.Defender!.Position, struckAt) * w.Tuning.LockOn.ReactionRatio.Evaluate(w.Defender.Sheet.Awareness);
        Assert.True(need > 0.2f);
        var buf = new TickIntent[w.PlayerCount];
        int reacted = -1;
        for (int i = 0; i < 120 && reacted < 0 && w.Pass != null; i++)
        {
            w.Step(buf);
            if (w.DefenderReacting)
            {
                // Still reacting: no claim of his, and the ball has not yet gone his reaction distance.
                Assert.Null(w.Claims[1].Player);
                Assert.True(TouchModel.HorizontalDistance(w.Ball.Position, struckAt) < need + 0.3f);
                Assert.Equal(struckAt.X, w.DefenderAnchor.X, 2);
                Assert.False(w.DefenderGoesForBall);
            }
            else reacted = w.Tick;
        }
        Assert.True(reacted > 0, "he never reacted");
        Assert.True(TouchModel.HorizontalDistance(w.Ball.Position, struckAt) >= need - 0.3f);
        // Reacted, he anchors on where the pass will be met (the receiver's claim), and goes for the ball only if he is first.
        w.Step(buf);
        if (w.Claims[0].Player != null)
        {
            Assert.Equal(w.Claims[0].Point.X, w.DefenderAnchor.X, 3);
            Assert.Equal(w.Claims[1].Player != null && w.Claims[1].ArriveTick <= w.Claims[0].ArriveTick, w.DefenderGoesForBall);
        }
    }

    [Fact]
    public void TheReactionDelayIsNoLongerThanItsMaximum()
    {
        // A defender far from a slow pass: the distance rule would take long, the time cap ends it.
        var t = new Tuning();
        t.LockOn.ReactionRatio = new Curve((1f, 5f), (99f, 5f));
        var w = WithBall(8f, -4f, 8f, 4f, defender: (20f, 0f), tuning: t);
        w.DefenderBotEnabled = false;
        Assert.True(StrikePass(w, holdTicks: 3));
        var buf = new TickIntent[w.PlayerCount];
        int ticks = 0;
        while (w.DefenderReacting && ticks < 240) { w.Step(buf); ticks++; }
        Assert.InRange(ticks, 1, (int)(t.LockOn.ReactionMaxSeconds * t.TickRate) + 2);
    }

    [Fact]
    public void WithoutATeamMateTheDefenderHasNoReactionDelay()
    {
        var sc = Scenario("defender_duel");
        var w = new World(Tuning(), sc, Player(sc.DefaultAttacker), Player(sc.DefaultKeeper), 3, Player(sc.DefaultDefender));
        w.Begin();
        var script = new ScriptedAttacker();
        var buf = new TickIntent[w.PlayerCount];
        for (int i = 0; i < 1200 && w.State != RoundState.Report; i++)
        {
            buf[0] = script.Next(w);
            w.Step(buf);
            Assert.False(w.DefenderReacting);
            Assert.True(w.DefenderGoesForBall);
            Assert.Null(w.PassMeeting);
        }
        Assert.Null(w.Report!.Team);
        Assert.DoesNotContain("\"team\"", w.Report.ToJson());
    }

    [Fact]
    public void TheKeeperReadsWhereAPassWillBeMet()
    {
        var w = WithBall(20f, -5f, 20f, 4f);
        Assert.Null(w.PassMeeting);
        Assert.Null(w.KeeperSituation().Coming);
        Assert.True(StrikePass(w));
        var buf = new TickIntent[w.PlayerCount];
        w.Step(buf); w.Step(buf);
        Assert.NotNull(w.PassMeeting);
        var coming = w.KeeperSituation().Coming;
        Assert.NotNull(coming);
        // The meeting is by the receiver, not where the ball is now.
        Assert.True(Vector3.Distance(coming!.Value, w.Teams[0][1].Position) < 2f);
        Assert.True(Vector3.Distance(coming.Value, w.Ball.Position) > 3f);
        // The pass is not a shot: he does not plant for it.
        Assert.False(w.KeeperSituation().Windup);
        for (int i = 0; i < 480 && w.BallController == null; i++) w.Step(buf);
        Assert.Null(w.PassMeeting);
    }

    static RoundReport Play(ScriptedAttacker.TeamStyle style, ulong seed)
    {
        var sc = Scenario("two_v_one");
        return RoundRunner.RunToReport(Tuning(), sc, Player(sc.DefaultAttacker), Player(sc.DefaultKeeper), seed, out _, new ScriptedAttacker { Team = style },
            defender: Player(sc.DefaultDefender), mate: Player(sc.DefaultMate));
    }

    [Trait("Category", "Slow")]
    [Fact]
    public void TheScriptedStylesPassOrDoNot()
    {
        int square = 0, through = 0;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var solo = Play(ScriptedAttacker.TeamStyle.Solo, seed);
            Assert.NotNull(solo.Team);
            Assert.Equal(0, solo.Team!.Passes);
            var sq = Play(ScriptedAttacker.TeamStyle.Square, seed);
            Assert.InRange(sq.Team!.Passes, 0, 1);
            square += sq.Team.Passes;
            if (sq.Team.Passes > 0) Assert.True(sq.Team.ControlChanges >= 1);
            var th = Play(ScriptedAttacker.TeamStyle.Through, seed);
            through += th.Team!.Passes;
            Assert.Equal(th.Outcome == Outcome.Offside, th.Team.Offside);
        }
        Assert.True(square >= 16, $"the square style passed in {square} of 20 rounds");
        Assert.True(through >= 12, $"the through style passed in {through} of 20 rounds");
    }

    [Fact]
    public void TheTeamReportIsInTheRoundsJsonAndReplaysTheSame()
    {
        var sc = Scenario("two_v_one");
        var t = Tuning();
        var report = RoundRunner.RunToReport(t, sc, Player(sc.DefaultAttacker), Player(sc.DefaultKeeper), 7, out var replay, new ScriptedAttacker { Team = ScriptedAttacker.TeamStyle.Square },
            defender: Player(sc.DefaultDefender), mate: Player(sc.DefaultMate));
        Assert.Contains("\"team\"", report.ToJson());
        Assert.Contains("\"passes\"", report.ToJson());
        var again = replay.Run(t, sc, Player(sc.DefaultAttacker), Player(sc.DefaultKeeper), Player(sc.DefaultDefender), Player(sc.DefaultMate));
        Assert.Equal(report.ToJson(), again.ToJson());
    }
}
