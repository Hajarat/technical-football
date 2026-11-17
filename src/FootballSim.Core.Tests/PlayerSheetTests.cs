using System.Text.Json;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

public class PlayerSheetTests
{
    const string Full = """
    { "id": "x", "name": "X", "acceleration": 60, "sprint_speed": 61, "agility": 62, "balance": 63, "dribbling": 64, "ball_control": 65,
      "first_touch": 66, "finishing": 67, "long_shots": 68, "shot_power": 69, "curve": 70, "composure": 71, "weak_foot": 4,
      "reflexes": 72, "diving": 73, "reach": 74, "positioning": 75, "handling": 76, "rushing_out": 77,
      "height_m": 1.85, "weight_kg": 80, "preferred_foot": "left" }
    """;

    [Fact]
    public void LoadsAFullSheetWithoutWarnings()
    {
        var r = DataLoader.ParsePlayer(Full, "x.json");
        Assert.True(r.Ok);
        Assert.Empty(r.Warnings);
        var s = r.Value!;
        Assert.Equal(67, s.Finishing); Assert.Equal(77, s.RushingOut); Assert.Equal(4, s.WeakFoot);
        Assert.Equal(1.85f, s.HeightM); Assert.Equal(80f, s.WeightKg); Assert.Equal(Foot.Left, s.PreferredFoot);
    }

    [Fact]
    public void ShippedPlayersLoadCleanlyWithTheDocumentedRatings()
    {
        foreach (var id in new[] { "striker_a", "flyer", "sunday_league", "the_wall", "stand_in" })
        {
            var r = DataLoader.LoadPlayer(Path.Combine(TestData.DataDir, "players", id + ".json"));
            Assert.True(r.Ok, r.Error);
            Assert.Empty(r.Warnings);
            Assert.Equal(id, r.Value!.Id);
        }
        Assert.Equal(78, TestData.Player("striker_a").Finishing);
        Assert.Equal(94, TestData.Player("flyer").SprintSpeed);
        Assert.Equal(62, TestData.Player("flyer").Finishing);
        Assert.InRange(TestData.Player("sunday_league").Finishing, 40, 50);
        Assert.InRange(TestData.Player("the_wall").Reflexes, 85, 90);
        Assert.InRange(TestData.Player("stand_in").Reflexes, 50, 60);
        Assert.Equal(6, PlayerSheet.KeeperRatingNames.Count);
    }

    [Fact]
    public void MissingRatingsBecomeFiftyAndEachIsReportedByFileAndField()
    {
        var r = DataLoader.ParsePlayer("""{ "id": "half", "finishing": 80, "reflexes": 70 }""", "half.json");
        Assert.True(r.Ok);
        Assert.Equal(80, r.Value!.Finishing);
        Assert.Equal(50, r.Value.Diving);
        Assert.Equal(50, r.Value.Acceleration);
        Assert.Contains(r.Warnings, w => w.File == "half.json" && w.Field == "diving");
        Assert.Contains(r.Warnings, w => w.Field == "preferred_foot");
        Assert.DoesNotContain(r.Warnings, w => w.Field == "finishing");
    }

    [Fact]
    public void OutOfRangeValuesAreClamped()
    {
        var r = DataLoader.ParsePlayer("""{ "finishing": 150, "diving": 0, "weak_foot": 9, "height_m": 3.0, "weight_kg": 10, "reach": -4 }""", "bad.json");
        Assert.True(r.Ok);
        var s = r.Value!;
        Assert.Equal(99, s.Finishing); Assert.Equal(1, s.Diving); Assert.Equal(5, s.WeakFoot); Assert.Equal(1, s.Reach);
        Assert.Equal(2.05f, s.HeightM); Assert.Equal(55f, s.WeightKg);
        foreach (var f in new[] { "finishing", "diving", "weak_foot", "height_m", "weight_kg", "reach" })
            Assert.Contains(r.Warnings, w => w.Field == f && w.File == "bad.json");
    }

    [Fact]
    public void WrongTypedValuesFallBackToTheDefault()
    {
        var r = DataLoader.ParsePlayer("""{ "finishing": "high", "reflexes": null, "preferred_foot": "both" }""", "t.json");
        Assert.True(r.Ok);
        Assert.Equal(50, r.Value!.Finishing);
        Assert.Equal(50, r.Value.Reflexes);
        Assert.Equal(Foot.Right, r.Value.PreferredFoot);
        Assert.Contains(r.Warnings, w => w.Field == "finishing");
        Assert.Contains(r.Warnings, w => w.Field == "preferred_foot");
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ \"finishing\": 80, ")]                  // half-written
    [InlineData("{ \"finishing\": 80,, }")]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    public void InvalidJsonReturnsAnErrorAndNeverThrows(string text)
    {
        var r = DataLoader.ParsePlayer(text, "broken.json");
        Assert.False(r.Ok);
        Assert.Null(r.Value);
        Assert.Contains("broken.json", r.Error);
    }

    [Fact]
    public void InvalidJsonReportsALineNumber()
    {
        var r = DataLoader.ParsePlayer("{\n  \"finishing\": 80,\n  \"diving\": ,\n}", "line.json");
        Assert.False(r.Ok);
        Assert.Contains("line", r.Error);
    }

    [Fact]
    public void MissingFileReturnsAnError()
    {
        var r = DataLoader.LoadPlayer(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid() + ".json"));
        Assert.False(r.Ok);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void SaveRoundTrips()
    {
        var s = TestData.Player("striker_a");
        s.PreferredFoot = Foot.Left;
        var back = DataLoader.ParsePlayer(s.ToJson(), "rt.json");
        Assert.True(back.Ok);
        Assert.Empty(back.Warnings);
        foreach (var n in PlayerSheet.RatingNames) Assert.Equal(s.GetRating(n), back.Value!.GetRating(n));
        Assert.Equal(s.HeightM, back.Value!.HeightM);
        Assert.Equal(s.WeightKg, back.Value.WeightKg);
        Assert.Equal(Foot.Left, back.Value.PreferredFoot);
        Assert.Equal(s.Id, back.Value.Id);
    }

    [Fact]
    public void SheetsClampWhenSetByName()
    {
        var s = new PlayerSheet();
        s.SetRating("finishing", 500);
        s.SetRating("weak_foot", 0);
        Assert.Equal(99, s.Finishing);
        Assert.Equal(1, s.WeakFoot);
        Assert.Throws<ArgumentException>(() => s.SetRating("nonsense", 1));
        var c = s.Clone(); c.SetRating("finishing", 10);
        Assert.Equal(99, s.Finishing);
    }

    // ---- tuning and scenario files ----

    [Fact]
    public void DefaultTuningFileEqualsTheBuiltInDefaults()
    {
        var r = DataLoader.LoadTuning(Path.Combine(TestData.DataDir, "tuning", "default.json"));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(DataLoader.TuningToJson(new Tuning()), DataLoader.TuningToJson(r.Value!));
        Assert.Equal(DataLoader.TuningHash(new Tuning()), DataLoader.TuningHash(r.Value!));
    }

    [Fact]
    public void MissingOrBrokenTuningFallsBackToDefaults()
    {
        var missing = DataLoader.LoadTuning(Path.Combine(Path.GetTempPath(), "none-" + Guid.NewGuid() + ".json"));
        Assert.NotNull(missing.Error);
        Assert.Contains("default tuning", missing.Error);
        Assert.Equal(120, missing.Value!.TickRate);
        var broken = DataLoader.ParseTuning("{ \"error_scale\": ", "t.json");
        Assert.NotNull(broken.Error);
        Assert.NotNull(broken.Value);
        var partial = DataLoader.ParseTuning("""{ "error_scale": 2.0, "shot": { "base_sigma_deg": 5 } }""", "t.json");
        Assert.True(partial.Ok);
        Assert.Equal(2f, partial.Value!.ErrorScale);
        Assert.Equal(5f, partial.Value.Shot.BaseSigmaDeg);
        Assert.Equal(6.2f, partial.Value.Shot.SigmaSkillSlope);
    }

    [Fact]
    public void ShippedScenariosLoadAndNameTheirPlayers()
    {
        foreach (var id in new[] { "penalty", "breakaway", "edge_of_box", "tight_angle" })
        {
            var r = DataLoader.LoadScenario(Path.Combine(TestData.DataDir, "scenarios", id + ".json"));
            Assert.True(r.Ok, r.Error);
            Assert.Empty(r.Warnings);
            Assert.Equal(id, r.Value!.Id);
            Assert.True(File.Exists(Path.Combine(TestData.DataDir, "players", r.Value.DefaultAttacker + ".json")));
            Assert.True(File.Exists(Path.Combine(TestData.DataDir, "players", r.Value.DefaultKeeper + ".json")));
        }
        Assert.True(TestData.Scenario("penalty").KeeperOnLine);
        Assert.Null(TestData.Scenario("penalty").ClockLimit);
        Assert.Equal(15f, TestData.Scenario("breakaway").ClockLimit);
        Assert.Equal(38f, 52.5f - TestData.Scenario("breakaway").Ball.X, 2);
        Assert.Equal(20f, 52.5f - TestData.Scenario("edge_of_box").Ball.X, 2);
        Assert.Equal(11f, 52.5f - TestData.Scenario("penalty").Ball.X, 2);
        Assert.Equal(14f, TestData.Scenario("tight_angle").Ball.Z);
        Assert.Equal(12f, 52.5f - TestData.Scenario("tight_angle").Ball.X, 2);
    }

    [Fact]
    public void BadScenarioJsonReturnsAnError()
    {
        Assert.False(DataLoader.ParseScenario("{ \"ball\": ", "s.json").Ok);
        var fixedUp = DataLoader.ParseScenario("""{ "team_size": 99, "clock_limit": -3, "pitch": { "goal_width": 500 } }""", "s.json");
        Assert.True(fixedUp.Ok);
        Assert.Equal(11, fixedUp.Value!.TeamSize);
        Assert.Null(fixedUp.Value.ClockLimit);
        Assert.Equal(20f, fixedUp.Value.Pitch.GoalWidth);
        Assert.Equal(3, fixedUp.Warnings.Count);
    }
}
