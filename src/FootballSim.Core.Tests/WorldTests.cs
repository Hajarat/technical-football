using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

public class WorldTests
{
    [Fact]
    public void TwoTeamsOfFiveStepWithoutError()
    {
        var sc = TestData.Scenario("breakaway");
        sc.TeamSize = 5;
        var w = new World(new Tuning(), sc, TestData.Player("striker_a"), TestData.Player("the_wall"), 3);
        Assert.Equal(2, w.Teams.Count);
        Assert.Equal(5, w.Teams[0].Count);
        Assert.Equal(5, w.Teams[1].Count);
        Assert.Equal(10, w.PlayerCount);
        Assert.Equal(PlayerRole.Keeper, w.Teams[1][0].Role);
        Assert.All(w.Teams[1].Skip(1), p => Assert.Equal(PlayerRole.Outfield, p.Role));

        var before = new List<Vector3>();
        foreach (var team in w.Teams) foreach (var p in team.Skip(1)) before.Add(p.Position);

        w.Begin();
        var script = new ScriptedAttacker();
        var buf = new TickIntent[10];
        var rng = new SimRng(1);
        for (int i = 0; i < 4000 && w.State != RoundState.Report; i++)
        {
            for (int k = 1; k < 10; k++)
                buf[k] = new TickIntent { MoveX = rng.Uniform(-1, 1), MoveZ = rng.Uniform(-1, 1), Sprint = 1, ShootHeld = rng.NextFloat() < 0.5f };
            buf[0] = script.Next(w);
            w.Step(buf);
        }
        Assert.Equal(RoundState.Report, w.State);

        var after = new List<Vector3>();
        foreach (var team in w.Teams) foreach (var p in team.Skip(1)) after.Add(p.Position);
        Assert.Equal(before, after);   // extra players just stand still
    }

    [Fact]
    public void WorldsOfAnySizeAcceptShortOrLongIntentLists()
    {
        foreach (int size in new[] { 1, 2, 11 })
        {
            var sc = TestData.Scenario("penalty");
            sc.TeamSize = size;
            var w = new World(new Tuning(), sc, TestData.Player("striker_a"), TestData.Player("the_wall"), 1);
            w.Begin();
            w.Step(ReadOnlySpan<TickIntent>.Empty);
            w.Step(new TickIntent[1]);
            w.Step(new TickIntent[40]);
            Assert.Equal(3, w.Tick);
        }
    }

    [Fact]
    public void StateIsReadableAfterSetup()
    {
        var w = new World(new Tuning(), TestData.Scenario("penalty"), TestData.Player("striker_a"), TestData.Player("the_wall"), 1);
        Assert.Equal(RoundState.Setup, w.State);
        Assert.Equal(Outcome.None, w.Outcome);
        Assert.Null(w.Report);
        Assert.Equal(new Vector3(41.5f, 0.11f, 0f), w.Ball.Position);
        Assert.Equal(0f, w.Clock);
        Assert.Equal(0f, w.ShotCharge);
        Assert.Equal(PlayerRole.Outfield, w.Attacker.Role);
        Assert.Equal(KeeperAction.Set, w.Keeper.KeeperAction);
        Assert.Equal(w.Pitch.GoalLineX, w.Keeper.Position.X, 3);    // penalty: on the line
        Assert.Equal("striker_a", w.Attacker.Sheet.Id);
        Assert.Empty(w.Events);
    }

    [Fact]
    public void KeeperStandsOffTheLineOutsidePenalties()
    {
        var w = new World(new Tuning(), TestData.Scenario("edge_of_box"), TestData.Player("striker_a"), TestData.Player("the_wall"), 1);
        Assert.Equal(w.Pitch.GoalLineX - 1.5f, w.Keeper.Position.X, 3);
        Assert.Equal(0f, w.Keeper.Position.Z, 3);
    }

    [Fact]
    public void EventsAreRaisedAndClearedEachStep()
    {
        var kinds = new HashSet<EventKind>();
        bool clearedBetween = false;
        for (ulong seed = 1; seed <= 10 && !(kinds.Contains(EventKind.Touch) && kinds.Contains(EventKind.Strike)); seed++)
        {
            var w = TestData.Live("breakaway", seed: seed);
            var script = new ScriptedAttacker();
            var buf = new TickIntent[2];
            for (int i = 0; i < 4000 && w.State != RoundState.Report; i++)
            {
                buf[0] = script.Next(w);
                int before = w.Events.Count;
                w.Step(buf);
                foreach (var e in w.Events) kinds.Add(e.Kind);
                if (before > 0 && w.Events.Count == 0) clearedBetween = true;
            }
        }
        Assert.Contains(EventKind.Touch, kinds);
        Assert.Contains(EventKind.Strike, kinds);
        Assert.True(clearedBetween);
        Assert.All(kinds, k => Assert.True(Enum.IsDefined(k)));
    }

    [Fact]
    public void FacingConventionIsZeroAlongPlusXTurningTowardPlusZ()
    {
        var w = TestData.Live("penalty");
        var buf = new TickIntent[2];
        buf[0] = new TickIntent { MoveZ = 1, Sprint = 1 };
        for (int i = 0; i < 60; i++) w.Step(buf);
        Assert.InRange(w.Attacker.Facing, 0.5f, MathF.PI);
        Assert.True(w.Attacker.Velocity.Z > 0);
    }

    [Fact]
    public void KeeperStaysInsideTheGoalWidthWhileTrackingTheBall()
    {
        var w = TestData.Live("tight_angle");
        w.Ball.Position = new Vector3(40.5f, 0.11f, 30f);
        var buf = new TickIntent[2];
        for (int i = 0; i < 400; i++) w.Step(buf);
        Assert.InRange(MathF.Abs(w.Keeper.Position.Z), 0f, w.Pitch.GoalHalfWidth);
    }

    [Fact]
    public void PreviewShotMatchesTheStrikeThatFollows()
    {
        var w = TestData.Live("penalty");
        Assert.Null(w.PreviewShot());
        var hold = new TickIntent[2];
        hold[0] = new TickIntent { ShootHeld = true };   // no stick: the player stays put and aims along its facing
        ShotPreview? last = null;
        for (int i = 0; i < 40; i++) { w.Step(hold); last = w.PreviewShot(); }
        Assert.NotNull(last);
        w.Step(new TickIntent[2]);   // release
        for (int i = 0; i < 40 && w.Shots.Count == 0; i++) w.Step(new TickIntent[2]);
        Assert.Single(w.Shots);
        Assert.Equal(w.Shots[0].SigmaH, last!.Value.SigmaH, 4);
        Assert.Equal(w.Shots[0].IntendedYaw, last.Value.IntendedYaw, 4);
        Assert.Equal(w.Shots[0].Aimed.X, last.Value.AimedZ, 4);
        Assert.Null(w.PreviewShot());
    }

    [Fact]
    public void KeeperRushScenarioStartsTheKeeperOffHisLine()
    {
        var w = TestData.Live("keeper_rush");
        Assert.Equal(w.Pitch.GoalLineX - 8f, w.Keeper.Position.X, 3);
        Assert.Equal(0f, w.Keeper.Position.Z, 3);
        var h = TestData.Live("halfway_run");
        Assert.Equal(w.Pitch.GoalLineX, h.Keeper.Position.X, 3);   // on his line
        var a = TestData.Live("angled_run");
        float angle = MathF.Atan2(a.Ball.Position.Z, a.Pitch.GoalLineX - a.Ball.Position.X) * 180f / MathF.PI;
        Assert.InRange(angle, 28f, 32f);
    }
}
