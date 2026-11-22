using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>Close and sprint dribbling: which rating each dribbling number reads, and the player file from before the split.</summary>
public class DribblingSplitTests
{
    static readonly Tuning T = TestData.Tuning();

    static PlayerSheet Sheet(int close, int sprint)
    {
        var s = TestData.Sheet(60);
        s.CloseDribbling = close;
        s.SprintDribbling = sprint;
        return s;
    }

    static PlayerState Player(PlayerSheet s) => new World(T, TestData.Scenario("penalty"), s, TestData.Player("the_wall"), 1).Attacker;

    [Fact]
    public void AFileWithOnlyTheOldRatingGivesBothRatingsThatValueWithOneWarning()
    {
        var r = DataLoader.ParsePlayer("""{ "id": "old", "dribbling": 64, "preferred_foot": "right" }""", "old.json");
        Assert.True(r.Ok);
        Assert.Equal(64, r.Value!.CloseDribbling);
        Assert.Equal(64, r.Value.SprintDribbling);
        Assert.Equal(64, r.Value.SkillMoves);   // (a sheet without skill_moves uses its close dribbling)
        Assert.Single(r.Warnings, w => w.Field.Contains("dribbling"));
        Assert.Contains(r.Warnings, w => w.File == "old.json" && w.Field == "dribbling");
    }

    [Fact]
    public void TheNewRatingsWinOverTheOldOneAndRoundTrip()
    {
        var r = DataLoader.ParsePlayer("""{ "id": "new", "dribbling": 64, "close_dribbling": 40, "sprint_dribbling": 80 }""", "new.json");
        Assert.Equal(40, r.Value!.CloseDribbling);
        Assert.Equal(80, r.Value.SprintDribbling);
        Assert.Equal(40, r.Value.SkillMoves);
        Assert.DoesNotContain(r.Warnings, w => w.Field.Contains("dribbling"));
        var again = DataLoader.ParsePlayer(r.Value.ToJson(), "again.json");
        Assert.Equal(40, again.Value!.CloseDribbling);
        Assert.Equal(80, again.Value.SprintDribbling);
        Assert.DoesNotContain("\"dribbling\"", r.Value.ToJson());
    }

    [Fact]
    public void SprintDribblingAloneMovesTheSprintNumbersAndLeavesTheCloseOnes()
    {
        var lo = Sheet(50, 20);
        var hi = Sheet(50, 95);
        Assert.True(TouchModel.StepsPerTouch(hi, TouchMode.Sprint, T) < TouchModel.StepsPerTouch(lo, TouchMode.Sprint, T));
        Assert.True(Movement.TopSpeedWithBall(hi, 1f, T) > Movement.TopSpeedWithBall(lo, 1f, T));
        Assert.True(TouchModel.KnockOnCatchTime(hi, T) < TouchModel.KnockOnCatchTime(lo, T));
        Assert.NotEqual(TouchModel.LiftDeg(lo, ActionKind.Sprint, TouchMode.Sprint, T), TouchModel.LiftDeg(hi, ActionKind.Sprint, TouchMode.Sprint, T));
        Assert.NotEqual(TouchModel.LiftDeg(lo, ActionKind.KnockOn, TouchMode.Jog, T), TouchModel.LiftDeg(hi, ActionKind.KnockOn, TouchMode.Jog, T));

        Assert.Equal(TouchModel.StepsPerTouch(lo, TouchMode.CloseControl, T), TouchModel.StepsPerTouch(hi, TouchMode.CloseControl, T));
        Assert.Equal(Movement.TopSpeedWithBall(lo, 0f, T), Movement.TopSpeedWithBall(hi, 0f, T));
        Assert.Equal(TouchModel.LiftDeg(lo, ActionKind.Jog, TouchMode.Jog, T), TouchModel.LiftDeg(hi, ActionKind.Jog, TouchMode.Jog, T));
        Assert.Equal(TouchModel.LiftDeg(lo, ActionKind.Close, TouchMode.CloseControl, T), TouchModel.LiftDeg(hi, ActionKind.Close, TouchMode.CloseControl, T));
        foreach (float deg in new[] { 20f, 45f, 90f })
            Assert.Equal(TouchPlanner.EarlyFraction(Player(lo), deg, T), TouchPlanner.EarlyFraction(Player(hi), deg, T));
    }

    [Fact]
    public void CloseDribblingAloneMovesTheCloseNumbersAndLeavesTheSprintOnes()
    {
        var lo = Sheet(20, 50);
        var hi = Sheet(95, 50);
        Assert.True(TouchModel.StepsPerTouch(hi, TouchMode.CloseControl, T) <= TouchModel.StepsPerTouch(lo, TouchMode.CloseControl, T));
        Assert.True(T.Touch.StepsPerTouch.Close.ForRating(hi.CloseDribbling) < T.Touch.StepsPerTouch.Close.ForRating(lo.CloseDribbling));
        Assert.True(Movement.TopSpeedWithBall(hi, 0f, T) > Movement.TopSpeedWithBall(lo, 0f, T));
        foreach (float deg in new[] { 20f, 45f, 90f })
            Assert.True(TouchPlanner.EarlyFraction(Player(hi), deg, T) < TouchPlanner.EarlyFraction(Player(lo), deg, T));
        Assert.NotEqual(TouchModel.LiftDeg(lo, ActionKind.Jog, TouchMode.Jog, T), TouchModel.LiftDeg(hi, ActionKind.Jog, TouchMode.Jog, T));

        Assert.Equal(TouchModel.StepsPerTouch(lo, TouchMode.Sprint, T), TouchModel.StepsPerTouch(hi, TouchMode.Sprint, T));
        Assert.Equal(Movement.TopSpeedWithBall(lo, 1f, T), Movement.TopSpeedWithBall(hi, 1f, T));
        Assert.Equal(TouchModel.KnockOnCatchTime(lo, T), TouchModel.KnockOnCatchTime(hi, T));
        Assert.Equal(TouchModel.LiftDeg(lo, ActionKind.Sprint, TouchMode.Sprint, T), TouchModel.LiftDeg(hi, ActionKind.Sprint, TouchMode.Sprint, T));
    }

    [Theory]
    [InlineData(ActionKind.Sprint, TouchMode.Sprint, true)]
    [InlineData(ActionKind.Turn, TouchMode.Sprint, true)]
    [InlineData(ActionKind.KnockOn, TouchMode.Jog, true)]
    [InlineData(ActionKind.Jog, TouchMode.Jog, false)]
    [InlineData(ActionKind.Cut, TouchMode.Jog, false)]
    [InlineData(ActionKind.Close, TouchMode.CloseControl, false)]
    [InlineData(ActionKind.Heavy, TouchMode.Sprint, false)]
    [InlineData(ActionKind.TrapLow, TouchMode.Sprint, false)]
    public void ATouchIsRatedBySprintDribblingOnlyWithTheSprintOrKnockOnProfile(ActionKind kind, TouchMode mode, bool sprint)
    {
        var s = Sheet(30, 80);
        Assert.Equal(sprint ? 80 : 30, TouchModel.DribblingFor(s, kind, mode));
        var k = T.Touch.Profiles;
        bool sprintProfile = ReferenceEquals(TouchModel.Profile(kind, mode, T), k.Sprint) || ReferenceEquals(TouchModel.Profile(kind, mode, T), k.KnockOn);
        Assert.Equal(sprint, sprintProfile);
    }
}
