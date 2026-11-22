namespace FootballSim.Core;

/// <summary>What a preset action did.</summary>
public enum PresetOutcome
{
    /// <summary>Saved into an empty slot.</summary>
    Saved,
    /// <summary>Saved over a filled slot (the second press).</summary>
    Overwritten,
    /// <summary>Loaded the slot into the working copy.</summary>
    Loaded,
    /// <summary>Cleared the slot (the second press).</summary>
    Cleared,
    /// <summary>
    /// The first press on a filled slot (save), on any slot with a preset (clear), or on a slot whose load would lose unsaved
    /// edits (load): press again within the window.
    /// </summary>
    NeedsConfirm,
    /// <summary>Load or clear on an empty slot: nothing happened.</summary>
    Empty,
}

/// <summary>One preset action: which (save, load, clear), on which slot (0-based), from which column, and what came of it.</summary>
public sealed record PresetResult(string Action, int Slot, bool FromPresets, PresetOutcome Outcome, string Message)
{
    /// <summary>True when the store changed and has to be written.</summary>
    public bool StoreChanged => Outcome is PresetOutcome.Saved or PresetOutcome.Overwritten or PresetOutcome.Cleared;

    /// <summary>The outcome as the session log writes it: saved, overwritten, loaded, cleared, needs_confirm or empty.</summary>
    public string OutcomeName => Outcome switch
    {
        PresetOutcome.NeedsConfirm => "needs_confirm",
        _ => Outcome.ToString().ToLowerInvariant(),
    };
}

/// <summary>
/// The player editor's column focus, highlighted preset slot and the second-press confirmations, without any drawing or input
/// reading. Save, load and clear act on the highlighted slot whichever column has focus: the stats column only moves its own row,
/// so the slot stays where it was last put. A filled slot takes a second save within <see cref="ConfirmSeconds"/> to overwrite, a
/// slot is cleared on a second clear within it, a load that would lose unsaved edits takes a second load within it, and any other
/// action, a column switch or a move drops a pending confirmation.
/// The editor's Base row uses the same confirmation (<see cref="ConfirmOrArm"/>), so only one is ever pending.
/// </summary>
public sealed class PresetActions
{
    public const float ConfirmSeconds = 2f;

    string _pending = "", _pendingKey = "";
    int _pendingSlot = -1;
    float _pendingUntil;

    public PresetActions(PlayerPresets store) => Store = store;

    /// <summary>The store the slots live in; changed in place.</summary>
    public PlayerPresets Store { get; private set; }
    /// <summary>True when the presets column has focus (the stats column otherwise).</summary>
    public bool PresetsFocus { get; private set; }
    /// <summary>The highlighted slot, 0-based.</summary>
    public int Slot { get; private set; }
    /// <summary>The action waiting for its second press ("save", "clear", or the caller's own), or empty.</summary>
    public string Pending => _pending;

    /// <summary>Starts over on a store: the stats column, slot 1, nothing pending.</summary>
    public void Reset(PlayerPresets store)
    {
        Store = store;
        PresetsFocus = false;
        Slot = 0;
        ClearPending();
    }

    public void ToggleColumn()
    {
        PresetsFocus = !PresetsFocus;
        ClearPending();
    }

    /// <summary>Moves the highlighted slot, wrapping round the ten.</summary>
    public void MoveSlot(int delta)
    {
        if (delta == 0) return;
        Slot = ((Slot + delta) % PlayerPresets.SlotCount + PlayerPresets.SlotCount) % PlayerPresets.SlotCount;
        ClearPending();
    }

    /// <summary>Drops a confirmation whose window has passed. True when one was dropped (its message should go).</summary>
    public bool Expire(float now)
    {
        if (_pending.Length == 0 || now <= _pendingUntil) return false;
        ClearPending();
        return true;
    }

    public void ClearPending() { _pending = ""; _pendingKey = ""; _pendingSlot = -1; }

    /// <summary>
    /// The second-press rule for any action: true (and nothing pending any more) when the same action on the same slot and key
    /// was armed within the window; otherwise arms it and returns false.
    /// </summary>
    public bool ConfirmOrArm(string action, int slot, string key, float now)
    {
        if (_pending == action && _pendingSlot == slot && _pendingKey == key && now <= _pendingUntil)
        {
            ClearPending();
            return true;
        }
        _pending = action; _pendingSlot = slot; _pendingKey = key; _pendingUntil = now + ConfirmSeconds;
        return false;
    }

    /// <summary>Saves <paramref name="work"/> into the highlighted slot; a filled slot needs a second press.</summary>
    public PresetResult Save(PlayerSheet work, string baseId, DateTime when, float now)
    {
        int s = Slot;
        bool filled = Store.Slots[s] != null;
        if (filled && !ConfirmOrArm("save", s, "", now))
            return Result("save", PresetOutcome.NeedsConfirm, $"Slot {s + 1} is filled: press save again to overwrite");
        ClearPending();
        Store.Save(s, work, baseId, when);
        return Result("save", filled ? PresetOutcome.Overwritten : PresetOutcome.Saved,
            $"{(filled ? "Overwrote" : "Saved into")} slot {s + 1}: {Store.Slots[s]!.Name}");
    }

    /// <summary>
    /// A copy of the highlighted slot's sheet for the working copy, or null: for an empty slot (Empty), and for the first press
    /// when the load would lose unsaved edits (NeedsConfirm). It would lose them when the slot differs from <paramref name="work"/>
    /// and <paramref name="workUnsaved"/> holds: the working copy matches no preset and no shipped attacker (the Base row's rule).
    /// </summary>
    public PresetResult Load(PlayerSheet work, bool workUnsaved, float now, out PlayerSheet? sheet)
    {
        int s = Slot;
        sheet = Store.Load(s);
        if (sheet == null)
        {
            ClearPending();
            return Result("load", PresetOutcome.Empty, $"Slot {s + 1} is empty: nothing to load");
        }
        bool loses = workUnsaved && !PlayerPresets.SameValues(sheet, work);
        if (loses && !ConfirmOrArm("load", s, "", now))
        {
            sheet = null;
            return Result("load", PresetOutcome.NeedsConfirm, $"Load slot {s + 1}: unsaved edits will be lost: press load again");
        }
        ClearPending();
        return Result("load", PresetOutcome.Loaded, $"Loaded slot {s + 1}: {Store.Slots[s]!.Name}");
    }

    /// <summary>Clears the highlighted slot on the second press.</summary>
    public PresetResult Clear(float now)
    {
        int s = Slot;
        if (Store.Slots[s] == null)
        {
            ClearPending();
            return Result("clear", PresetOutcome.Empty, $"Slot {s + 1} is empty: nothing to clear");
        }
        if (!ConfirmOrArm("clear", s, "", now))
            return Result("clear", PresetOutcome.NeedsConfirm, $"Clear slot {s + 1}? Press clear again");
        Store.Clear(s);
        return Result("clear", PresetOutcome.Cleared, $"Cleared slot {s + 1}");
    }

    PresetResult Result(string action, PresetOutcome outcome, string message) => new(action, Slot, PresetsFocus, outcome, message);
}
