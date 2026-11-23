using System.Text.Json;
using System.Text.Json.Nodes;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

public class PlayerPresetsTests
{
    /// <summary>A sheet whose every field differs from the defaults and from the shipped players.</summary>
    static PlayerSheet Odd(int seed)
    {
        var s = new PlayerSheet { Id = "x", Name = "x", HeightM = 1.60f + seed * 0.01f, WeightKg = 55f + seed, PreferredFoot = seed % 2 == 0 ? Foot.Left : Foot.Right };
        int i = 0;
        foreach (var n in PlayerSheet.RatingNames) s.SetRating(n, 1 + (seed * 7 + i++ * 13) % 99);
        if (seed % 2 == 1) s.Traits = PlayerTrait.OutsideFoot;
        return s;
    }

    static PlayerPresets RoundTrip(PlayerPresets p, out List<LoadWarning> warnings) => PlayerPresets.Parse(p.ToJson(), out warnings);

    [Fact]
    public void EveryFieldSurvivesSaveAndLoadInCustomAndInEachSlot()
    {
        var store = new PlayerPresets { Active = true, Base = "striker_a", Custom = PlayerPresets.AsCustom(Odd(11)) };
        var when = new DateTime(2026, 11, 22, 16, 42, 10);
        for (int i = 0; i < PlayerPresets.SlotCount; i++) store.Save(i, Odd(i), "flyer", when);

        var back = RoundTrip(store, out var warnings);
        Assert.Empty(warnings);
        Assert.True(back.Active);
        Assert.Equal("striker_a", back.Base);
        Assert.NotNull(back.Custom);
        Assert.Equal("custom", back.Custom!.Id);
        Assert.Equal("Custom", back.Custom.Name);
        Assert.True(PlayerPresets.SameValues(Odd(11), back.Custom));
        Assert.Equal(Odd(11).HeightM, back.Custom.HeightM);
        for (int i = 0; i < PlayerPresets.SlotCount; i++)
        {
            var p = back.Slots[i]!;
            Assert.Equal("from flyer · 16:42", p.Name);
            Assert.Equal("2026-11-22T16:42:10", p.SavedAt);
            var orig = Odd(i);
            foreach (var n in PlayerSheet.RatingNames) Assert.Equal(orig.GetRating(n), p.Sheet.GetRating(n));
            Assert.Equal(orig.HeightM, p.Sheet.HeightM);
            Assert.Equal(orig.WeightKg, p.Sheet.WeightKg);
            Assert.Equal(orig.PreferredFoot, p.Sheet.PreferredFoot);
            Assert.Equal(orig.Traits, p.Sheet.Traits);
        }
    }

    [Fact]
    public void TheFileAlwaysHasTenSlotsAndClearEmptiesOne()
    {
        var store = new PlayerPresets();
        store.Save(3, Odd(3), "striker_a", DateTime.Now);
        var json = JsonNode.Parse(store.ToJson())!;
        Assert.Equal(10, json["presets"]!.AsArray().Count);
        Assert.Equal(1, json["version"]!.GetValue<int>());
        Assert.NotNull(store.Load(3));
        Assert.Null(store.Load(4));
        store.Clear(3);
        Assert.Null(store.Load(3));
        Assert.All(RoundTrip(store, out _).Slots, Assert.Null);
    }

    [Fact]
    public void LoadGivesACopy()
    {
        var store = new PlayerPresets();
        store.Save(0, Odd(1), "striker_a", DateTime.Now);
        var a = store.Load(0)!;
        a.SetRating("finishing", Odd(1).Finishing == 1 ? 2 : 1);
        Assert.Equal(Odd(1).Finishing, store.Load(0)!.Finishing);
        Assert.True(store.MatchesAPreset(Odd(1)));
        Assert.False(store.MatchesAPreset(a));
    }

    [Fact]
    public void AMissingFileGivesAnEmptyStoreWithAWarning()
    {
        var store = PlayerPresets.Load(Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.json"), out var w);
        Assert.Null(store.Custom);
        Assert.False(store.Active);
        Assert.All(store.Slots, Assert.Null);
        Assert.NotEmpty(w);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    [InlineData("{ \"version\": 2, \"active\": true, \"custom\": { \"dribbling\": 80 } }")]
    [InlineData("{ \"active\": true }")]
    public void BrokenOrWrongVersionFilesGiveAnEmptyStore(string json)
    {
        var store = PlayerPresets.Parse(json, out var w);
        Assert.Null(store.Custom);
        Assert.False(store.Active);
        Assert.All(store.Slots, Assert.Null);
        Assert.NotEmpty(w);
    }

    [Fact]
    public void ABadSlotIsEmptiedAndTheOthersStillLoad()
    {
        var store = new PlayerPresets();
        store.Save(0, Odd(0), "striker_a", DateTime.Now);
        store.Save(2, Odd(2), "striker_a", DateTime.Now);
        var json = JsonNode.Parse(store.ToJson())!;
        json["presets"]![1] = 42;                                   // not an object
        json["presets"]![2]!["sheet"]!["traits"] = new JsonArray("telepathy"); // a sheet the loader rejects
        var back = PlayerPresets.Parse(json.ToJsonString(), out var w);
        Assert.NotNull(back.Slots[0]);
        Assert.Null(back.Slots[1]);
        Assert.Null(back.Slots[2]);
        Assert.Contains(w, x => x.Field == "presets[1]");
        Assert.Contains(w, x => x.Field == "presets[2].sheet");
    }

    [Fact]
    public void ARatingOf150IsClampedTo99WithAWarning()
    {
        var store = new PlayerPresets { Custom = PlayerPresets.AsCustom(Odd(4)) };
        var json = JsonNode.Parse(store.ToJson())!;
        json["custom"]!["finishing"] = 150;
        var back = PlayerPresets.Parse(json.ToJsonString(), out var w);
        Assert.Equal(99, back.Custom!.Finishing);
        Assert.Contains(w, x => x.Field == "finishing");
    }

    [Fact]
    public void TwelveSlotsKeepTheFirstTen()
    {
        var store = new PlayerPresets();
        for (int i = 0; i < 10; i++) store.Save(i, Odd(i), "striker_a", DateTime.Now);
        var json = JsonNode.Parse(store.ToJson())!;
        json["presets"]!.AsArray().Add(JsonNode.Parse(json["presets"]![0]!.ToJsonString()));
        json["presets"]!.AsArray().Add(null);
        var back = PlayerPresets.Parse(json.ToJsonString(), out var w);
        Assert.Equal(10, back.Slots.Length);
        Assert.All(back.Slots, Assert.NotNull);
        Assert.Contains(w, x => x.Field == "presets");
    }

    [Fact]
    public void ActiveWithoutACustomSheetIsNotActive()
    {
        var back = PlayerPresets.Parse("{ \"version\": 1, \"active\": true, \"custom\": null, \"presets\": [] }", out _);
        Assert.False(back.Active);
    }

    [Fact]
    public void SaveWritesThroughATemporaryFileAndKeepsNoLeftover()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"presets-{Guid.NewGuid():N}");
        string path = Path.Combine(dir, "player_editor.json");
        try
        {
            var store = new PlayerPresets { Custom = PlayerPresets.AsCustom(Odd(5)), Active = true };
            Assert.Null(store.Save(path));
            store.Active = false;
            Assert.Null(store.Save(path));
            Assert.False(File.Exists(path + ".tmp"));
            var back = PlayerPresets.Load(path, out var w);
            Assert.Empty(w);
            Assert.False(back.Active);
            Assert.True(PlayerPresets.SameValues(Odd(5), back.Custom!));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ---- rows ----

    [Fact]
    public void RowsAreTheOutfieldRatingsThenFootTraitsHeightAndWeight()
    {
        var rows = PlayerEditRow.All();
        var expected = PlayerSheet.RatingNames.Where(n => !PlayerSheet.KeeperRatingNames.Contains(n))
            .SelectMany(n => n == "skill_moves" ? new[] { n }.Concat(PlayerSheet.PassingRatingNames) : new[] { n })
            .Concat(new[] { "preferred_foot" })
            .Concat(PlayerSheet.TraitNames.Select(t => t.Name))
            .Concat(new[] { "height_m", "weight_kg" });
        Assert.Equal(expected, rows.Select(r => r.Name));
        Assert.Equal(17, rows.Count(r => r.Kind == PlayerEditKind.Rating));
        Assert.Equal(Enum.GetValues<PlayerTrait>().Length - 1, rows.Count(r => r.Kind == PlayerEditKind.Trait));
    }

    [Fact]
    public void ANewRatingNameAddsARow()
    {
        var names = PlayerSheet.RatingNames.Concat(new[] { "vision" }).ToList();
        var rows = PlayerEditRow.All(names, PlayerSheet.KeeperRatingNames);
        Assert.Equal(PlayerEditRow.All().Count + 1, rows.Count);
        Assert.Equal("vision", rows.Last(r => r.Kind == PlayerEditKind.Rating).Name);
    }

    [Fact]
    public void StepsClampAsAPlayerFileIsClamped()
    {
        var s = Odd(0);
        var rows = PlayerEditRow.All().ToDictionary(r => r.Name);
        rows["finishing"].Step(s, 500);
        Assert.Equal(99, s.Finishing);
        rows["finishing"].Step(s, -500);
        Assert.Equal(1, s.Finishing);
        rows["height_m"].Step(s, -100);
        Assert.Equal(1.60f, s.HeightM);
        rows["height_m"].Step(s, 3);
        Assert.Equal(1.63f, s.HeightM);
        rows["height_m"].Step(s, 1000);
        Assert.Equal(2.05f, s.HeightM);
        rows["weight_kg"].Step(s, 1000);
        Assert.Equal(100f, s.WeightKg);
        rows["weight_kg"].Step(s, -1000);
        Assert.Equal(55f, s.WeightKg);
        var foot = s.PreferredFoot;
        Assert.True(rows["preferred_foot"].Step(s, 1));
        Assert.NotEqual(foot, s.PreferredFoot);
        Assert.True(rows["outside_foot"].Step(s, -1));
        Assert.True(s.Has(PlayerTrait.OutsideFoot));
        Assert.False(rows["finishing"].Step(s, -1)); // already at 1
        // every edited value reads back from a file unchanged (nothing the editor makes is clamped again)
        var back = DataLoader.ParsePlayer(s.ToJson());
        Assert.Empty(back.Warnings);
        Assert.True(PlayerPresets.SameValues(s, back.Value!));
    }

    // ---- replay ----

    [Fact]
    public void ARoundWithACustomSheetReplaysExactlyFromItsEmbeddedSheet()
    {
        var custom = PlayerPresets.AsCustom(Odd(9));
        custom.SetRating("close_dribbling", 97); custom.SetRating("finishing", 3); custom.HeightM = 1.97f; custom.WeightKg = 61f;
        var original = RoundRunner.RunToReport(new Tuning(), TestData.Scenario("breakaway"), custom, TestData.Player("the_wall"), 4242, out var replay);
        Assert.Equal("custom", replay.AttackerId);
        replay.SetAttackerSheet(custom);

        var loaded = Replay.FromJson(replay.ToJson(), out var err);
        Assert.Null(err);
        var att = loaded!.ResolveAttacker(_ => null, out var aerr);
        Assert.Null(aerr);
        Assert.True(PlayerPresets.SameValues(custom, att!));
        var rerun = loaded.Run(new Tuning(), TestData.Scenario("breakaway"), att!, TestData.Player("the_wall"));
        Assert.Equal(original.ToJson(), rerun.ToJson());
    }

    [Fact]
    public void AReplayWithoutASheetStillRunsOnItsAttackerId()
    {
        var original = RoundRunner.RunToReport(new Tuning(), TestData.Scenario("penalty"), TestData.Player("striker_a"), TestData.Player("the_wall"), 7, out var replay);
        string json = replay.ToJson();
        Assert.DoesNotContain("attacker_sheet", json);
        var loaded = Replay.FromJson(json, out _)!;
        Assert.Null(loaded.AttackerSheet);
        var att = loaded.ResolveAttacker(id => TestData.Player(id), out var aerr);
        Assert.Null(aerr);
        Assert.Equal("striker_a", att!.Id);
        Assert.Equal(original.ToJson(), loaded.Run(new Tuning(), TestData.Scenario("penalty"), att, TestData.Player("the_wall")).ToJson());
    }
}
