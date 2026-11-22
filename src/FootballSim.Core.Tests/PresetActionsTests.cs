using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>The player editor's preset actions: save, load and clear on the highlighted slot from either column.</summary>
public class PresetActionsTests
{
    static readonly DateTime When = new(2026, 11, 22, 20, 33, 0);

    static PlayerSheet Sheet(int finishing)
    {
        var s = PlayerPresets.AsCustom(TestData.Player("striker_a"));
        s.SetRating("finishing", finishing);
        return s;
    }

    /// <summary>The store as the game writes it and reads it back at the next launch.</summary>
    static PlayerPresets Reloaded(PlayerPresets store) => PlayerPresets.Parse(store.ToJson(), out _);

    [Fact]
    public void SaveFromTheStatsColumnFillsTheHighlightedSlot()
    {
        var store = new PlayerPresets();
        var a = new PresetActions(store);
        Assert.False(a.PresetsFocus);                 // the editor opens on the stats column, slot 1
        var r = a.Save(Sheet(81), "flyer", When, 0f);
        Assert.Equal(PresetOutcome.Saved, r.Outcome);
        Assert.Equal("saved", r.OutcomeName);
        Assert.True(r.StoreChanged);
        Assert.False(r.FromPresets);
        Assert.Equal(0, r.Slot);
        Assert.Contains("slot 1", r.Message);
        var back = Reloaded(store);
        Assert.Equal(81, back.Slots[0]!.Sheet.GetRating("finishing"));
        Assert.Equal("from flyer · 20:33", back.Slots[0]!.Name);
        for (int i = 1; i < PlayerPresets.SlotCount; i++) Assert.Null(back.Slots[i]);
    }

    [Fact]
    public void SaveFromThePresetsColumnUsesTheSlotPicked()
    {
        var store = new PlayerPresets();
        var a = new PresetActions(store);
        a.ToggleColumn();
        a.MoveSlot(3);
        var r = a.Save(Sheet(64), "striker_a", When, 0f);
        Assert.Equal(PresetOutcome.Saved, r.Outcome);
        Assert.True(r.FromPresets);
        Assert.Equal(3, r.Slot);
        // back in the stats column the slot stays where it was put, and a save goes there
        a.ToggleColumn();
        Assert.Equal(3, a.Slot);
        var back = Reloaded(store);
        Assert.Equal(64, back.Slots[3]!.Sheet.GetRating("finishing"));
        Assert.Null(back.Slots[0]);
        a.MoveSlot(-4);                               // wraps to slot 10
        Assert.Equal(9, a.Slot);
    }

    [Fact]
    public void OverwritingAFilledSlotNeedsASecondPress()
    {
        var store = new PlayerPresets();
        var a = new PresetActions(store);
        a.Save(Sheet(70), "flyer", When, 0f);

        var first = a.Save(Sheet(90), "flyer", When, 1f);
        Assert.Equal(PresetOutcome.NeedsConfirm, first.Outcome);
        Assert.Equal("needs_confirm", first.OutcomeName);
        Assert.False(first.StoreChanged);
        Assert.Equal(70, Reloaded(store).Slots[0]!.Sheet.GetRating("finishing"));

        var second = a.Save(Sheet(90), "flyer", When, 2.5f);
        Assert.Equal(PresetOutcome.Overwritten, second.Outcome);
        Assert.True(second.StoreChanged);
        Assert.Equal(90, Reloaded(store).Slots[0]!.Sheet.GetRating("finishing"));

        // too late: the window has passed, so the next press asks again
        a.Save(Sheet(50), "flyer", When, 10f);
        Assert.True(a.Expire(12.1f));
        Assert.Equal(PresetOutcome.NeedsConfirm, a.Save(Sheet(50), "flyer", When, 12.2f).Outcome);
        Assert.Equal(90, Reloaded(store).Slots[0]!.Sheet.GetRating("finishing"));
        // and moving to another slot drops the pending overwrite
        a.MoveSlot(1);
        a.MoveSlot(-1);
        Assert.Equal(PresetOutcome.NeedsConfirm, a.Save(Sheet(50), "flyer", When, 12.3f).Outcome);
    }

    [Fact]
    public void ClearingNeedsASecondPress()
    {
        var store = new PlayerPresets();
        var a = new PresetActions(store);
        a.ToggleColumn();
        a.MoveSlot(2);
        a.Save(Sheet(77), "flyer", When, 0f);

        var first = a.Clear(1f);
        Assert.Equal(PresetOutcome.NeedsConfirm, first.Outcome);
        Assert.NotNull(Reloaded(store).Slots[2]);
        a.ToggleColumn();                             // a column switch drops it
        Assert.Equal(PresetOutcome.NeedsConfirm, a.Clear(1.2f).Outcome);
        var second = a.Clear(1.5f);                   // from the stats column too
        Assert.Equal(PresetOutcome.Cleared, second.Outcome);
        Assert.True(second.StoreChanged);
        Assert.False(second.FromPresets);
        Assert.Null(Reloaded(store).Slots[2]);

        var empty = a.Clear(2f);
        Assert.Equal(PresetOutcome.Empty, empty.Outcome);
        Assert.False(empty.StoreChanged);
        Assert.Contains("empty", empty.Message);
    }

    [Fact]
    public void LoadGivesACopyForTheWorkingSheet()
    {
        var store = new PlayerPresets();
        var a = new PresetActions(store);
        a.MoveSlot(5);
        var empty = a.Load(Sheet(50), workUnsaved: true, 0f, out var none);
        Assert.Null(none);
        Assert.Equal(PresetOutcome.Empty, empty.Outcome);
        Assert.Equal("empty", empty.OutcomeName);

        a.Save(Sheet(88), "flyer", When, 0f);
        var r = a.Load(Sheet(50), workUnsaved: false, 0f, out var work);   // from the stats column
        Assert.Equal(PresetOutcome.Loaded, r.Outcome);
        Assert.False(r.StoreChanged);
        Assert.NotNull(work);
        Assert.Equal(PlayerPresets.CustomId, work!.Id);
        Assert.Equal(88, work.GetRating("finishing"));
        work.SetRating("finishing", 10);              // editing the working copy leaves the slot alone
        Assert.Equal(88, Reloaded(store).Slots[5]!.Sheet.GetRating("finishing"));
    }

    [Fact]
    public void ALoadThatWouldLoseEditsNeedsASecondPress()
    {
        var store = new PlayerPresets();
        var a = new PresetActions(store);
        a.Save(Sheet(88), "flyer", When, 0f);
        var edited = Sheet(42);

        var first = a.Load(edited, workUnsaved: true, 1f, out var none);
        Assert.Equal(PresetOutcome.NeedsConfirm, first.Outcome);
        Assert.Equal("needs_confirm", first.OutcomeName);
        Assert.Equal("Load slot 1: unsaved edits will be lost: press load again", first.Message);
        Assert.Null(none);
        Assert.Equal("load", a.Pending);

        var second = a.Load(edited, workUnsaved: true, 2.5f, out var work);
        Assert.Equal(PresetOutcome.Loaded, second.Outcome);
        Assert.Equal(88, work!.GetRating("finishing"));
        Assert.Equal("", a.Pending);

        // too late: the window has passed, so it asks again
        a.Load(edited, workUnsaved: true, 10f, out _);
        Assert.Equal(PresetOutcome.NeedsConfirm, a.Load(edited, workUnsaved: true, 12.5f, out _).Outcome);
        // any other action drops it
        a.Clear(12.6f);
        Assert.Equal(PresetOutcome.NeedsConfirm, a.Load(edited, workUnsaved: true, 12.7f, out _).Outcome);
    }

    [Fact]
    public void ALoadWithNothingToLoseTakesOnePress()
    {
        var store = new PlayerPresets();
        var a = new PresetActions(store);
        a.Save(Sheet(88), "flyer", When, 0f);
        // the working copy is saved somewhere (or is a shipped attacker): nothing to lose
        var r = a.Load(Sheet(42), workUnsaved: false, 1f, out var work);
        Assert.Equal(PresetOutcome.Loaded, r.Outcome);
        Assert.Equal(88, work!.GetRating("finishing"));
        // unsaved, but the slot holds the same values: nothing would change
        var same = a.Load(Sheet(88), workUnsaved: true, 2f, out var again);
        Assert.Equal(PresetOutcome.Loaded, same.Outcome);
        Assert.NotNull(again);
    }

    [Fact]
    public void ASlotChangeBetweenLoadPressesResetsTheConfirmation()
    {
        var store = new PlayerPresets();
        var a = new PresetActions(store);
        a.Save(Sheet(88), "flyer", When, 0f);
        a.MoveSlot(1);
        a.Save(Sheet(77), "flyer", When, 0f);
        a.MoveSlot(-1);
        var edited = Sheet(42);

        Assert.Equal(PresetOutcome.NeedsConfirm, a.Load(edited, workUnsaved: true, 1f, out _).Outcome);
        a.MoveSlot(1);
        Assert.Equal("", a.Pending);
        var other = a.Load(edited, workUnsaved: true, 1.2f, out var none);
        Assert.Equal(PresetOutcome.NeedsConfirm, other.Outcome);
        Assert.Equal(1, other.Slot);
        Assert.Null(none);
        a.MoveSlot(-1);
        a.MoveSlot(1);
        Assert.Equal(PresetOutcome.NeedsConfirm, a.Load(edited, workUnsaved: true, 1.4f, out _).Outcome);
        Assert.Equal(PresetOutcome.Loaded, a.Load(edited, workUnsaved: true, 1.6f, out var work).Outcome);
        Assert.Equal(77, work!.GetRating("finishing"));
    }

    [Fact]
    public void TheBaseRowConfirmationSharesTheSinglePendingAction()
    {
        var a = new PresetActions(new PlayerPresets());
        Assert.False(a.ConfirmOrArm("base", -1, "flyer", 0f));
        Assert.Equal("base", a.Pending);
        Assert.False(a.ConfirmOrArm("base", -1, "striker_a", 0.5f));   // another target arms afresh
        Assert.True(a.ConfirmOrArm("base", -1, "striker_a", 1f));
        Assert.Equal("", a.Pending);
    }
}
