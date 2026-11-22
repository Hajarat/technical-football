using System.Collections.Generic;
using System.Linq;
using FootballSim.Core;
using Godot;

namespace FootballSim.Game;

/// <summary>What the editor asks of <see cref="Match"/> after a tick.</summary>
public enum EditorResult { None, StoreChanged, Close }

/// <summary>
/// The player editor: two columns over the paused game. The stats column is a Base row (the shipped attacker Custom was copied
/// from) and one row per <see cref="PlayerEditRow"/>; the presets column is the store's ten slots. It edits a working copy, which
/// <see cref="Match"/> applies when it closes. Save, load and clear act on the highlighted slot from either column (the column, slot
/// and confirmation rules are <see cref="PresetActions"/>). Driven one physics tick at a time by <see cref="Step"/>; it never reads
/// input itself.
/// </summary>
public partial class PlayerEditor : PanelContainer
{
    const int FontSize = 15;
    /// <summary>Seconds a direction is held before it repeats, the repeat rate it starts at and the one it speeds up to (steps per second).</summary>
    const float RepeatDelay = 0.35f, RepeatStartRate = 6f, RepeatMaxRate = 20f, RepeatRampSeconds = 1f;
    static readonly Color Heading = new(1f, 0.85f, 0.3f), Dim = new(0.65f, 0.65f, 0.65f), Value = new(0.6f, 0.9f, 1f),
        Selected = new(1f, 0.95f, 0.55f), Marked = new(0.85f, 0.8f, 0.55f), Warn = new(1f, 0.6f, 0.3f);

    readonly IReadOnlyList<PlayerEditRow> _rows = PlayerEditRow.All();
    readonly List<(Label Name, Label Value)> _statLabels = new();
    readonly List<Label> _slotLabels = new();
    Label _title = null!, _diff = null!, _confirm = null!, _hint1 = null!, _hint2 = null!, _presetsHead = null!;

    PlayerPresets _store = null!;
    readonly PresetActions _actions = new(new PlayerPresets());
    List<PlayerSheet> _shipped = new();
    PlayerSheet _atOpen = new();
    int _row;                 // row 0 is Base
    float _time;
    string _message = "";
    string _hintKey = "";
    readonly Repeater _up = new(), _down = new(), _left = new(), _right = new();

    public bool IsOpen { get; private set; }
    /// <summary>The sheet being edited (id <c>custom</c>).</summary>
    public PlayerSheet Work { get; private set; } = new();
    /// <summary>The shipped attacker Custom was last copied from.</summary>
    public string BaseId { get; private set; } = "";
    /// <summary>True when the sheet differs from what it was when the editor opened.</summary>
    public bool Dirty => !PlayerPresets.SameValues(Work, _atOpen);
    /// <summary>Raised for every save, load and clear press with its slot and outcome (Match logs it).</summary>
    public event System.Action<PresetResult>? PresetAction;

    public override void _Ready()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Ignore;
        var style = new StyleBoxFlat { BgColor = new Color(0.02f, 0.03f, 0.05f, 0.9f), CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10, CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10 };
        style.SetContentMarginAll(16);
        AddThemeStyleboxOverride("panel", style);
        AnchorLeft = AnchorRight = 0.5f; AnchorTop = AnchorBottom = 0.5f;
        OffsetLeft = -400; OffsetRight = 400; OffsetTop = -300; OffsetBottom = 300;
        GrowHorizontal = GrowDirection.Both; GrowVertical = GrowDirection.Both;

        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 6);
        AddChild(box);
        var head = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _title = Make("Player editor · Custom", 22, Colors.White);
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(_title);
        head.AddChild(Make("applies on close", FontSize, Dim));
        box.AddChild(head);

        var cols = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        cols.AddThemeConstantOverride("separation", 36);
        box.AddChild(cols);
        var stats = new GridContainer { Columns = 2, MouseFilter = MouseFilterEnum.Ignore };
        stats.AddThemeConstantOverride("h_separation", 14);
        stats.AddThemeConstantOverride("v_separation", 1);
        for (int i = 0; i <= _rows.Count; i++)
        {
            var n = Make(i == 0 ? "Base player" : _rows[i - 1].Name, FontSize, Colors.White);
            n.CustomMinimumSize = new Vector2(170, 0);
            var v = Make("", FontSize, Value);
            v.CustomMinimumSize = new Vector2(150, 0);
            stats.AddChild(n); stats.AddChild(v);
            _statLabels.Add((n, v));
        }
        cols.AddChild(stats);
        var presets = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        presets.AddThemeConstantOverride("separation", 1);
        _presetsHead = Make($"Presets ({PlayerPresets.SlotCount})", FontSize + 1, Heading);
        presets.AddChild(_presetsHead);
        for (int i = 0; i < PlayerPresets.SlotCount; i++)
        {
            var l = Make("", FontSize, Colors.White);
            presets.AddChild(l);
            _slotLabels.Add(l);
        }
        cols.AddChild(presets);

        _diff = Make("", FontSize - 1, Dim);
        _diff.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_diff);
        _confirm = Make("", FontSize, Warn);
        box.AddChild(_confirm);
        _hint1 = Make("", FontSize - 1, Dim);
        _hint2 = Make("", FontSize - 1, Dim);
        box.AddChild(_hint1); box.AddChild(_hint2);
    }

    /// <summary>
    /// Opens on a working copy of <paramref name="sheet"/>. <paramref name="shipped"/> are the attackers from data/players, which the Base
    /// row steps through; <paramref name="store"/> holds the presets, which the editor changes in place.
    /// </summary>
    public void Open(PlayerSheet sheet, string baseId, IEnumerable<PlayerSheet> shipped, PlayerPresets store)
    {
        Work = PlayerPresets.AsCustom(sheet);
        _atOpen = Work.Clone();
        BaseId = baseId;
        _shipped = shipped.ToList();
        _store = store;
        _actions.Reset(store);
        _row = 1;
        _message = "";
        _up.Reset(); _down.Reset(); _left.Reset(); _right.Reset();
        IsOpen = true;
        Visible = true;
        Refresh();
    }

    public void Close()
    {
        IsOpen = false;
        Visible = false;
    }

    /// <summary>Shows the hint bar for the device in use (rebuilt only when it changes).</summary>
    public void SetDevice(InputDevice device, PadKind kind)
    {
        string key = $"{device}/{kind}";
        if (key == _hintKey) return;
        _hintKey = key;
        if (device == InputDevice.Gamepad)
        {
            string B(PadBind p) => p.Label(kind);
            _hint1.Text = $"↑↓ row or slot   ←→ −1/+1 (hold repeats)   {B(Bindings.Pad.Chip)}/{B(Bindings.Pad.Finesse)} −10/+10   {B(Bindings.Pad.Retry)} column   {B(Bindings.Pad.Shoot)}/{B(Bindings.Pad.Pause)} close";
            _hint2.Text = $"marked slot, either column: {B(Bindings.Pad.EditorSave)} save (twice to overwrite)   {B(Bindings.Pad.Confirm)} load (twice if edits would be lost)   {B(Bindings.Pad.Capture)} clear (twice)";
            _presetsHead.Text = $"Presets ({PlayerPresets.SlotCount})   {B(Bindings.Pad.Retry)}: switch column";
        }
        else
        {
            _hint1.Text = $"↑↓ row or slot   ←→ −1/+1 (hold repeats)   {Bindings.Chip.Label}+←→ −10/+10   {Bindings.Editor.Column.Label} column   {Bindings.Editor.Close.Label} close";
            _hint2.Text = $"marked slot, either column: {Bindings.Editor.Save.Label} save (twice to overwrite)   {Bindings.Editor.Load.Label} load (twice if edits would be lost)   {Bindings.Editor.Clear.Label} clear (twice)";
            _presetsHead.Text = $"Presets ({PlayerPresets.SlotCount})   {Bindings.Editor.Column.Label}: switch column";
        }
    }

    /// <summary>One physics tick of editor input.</summary>
    public EditorResult Step(in EditorInput input, float dt)
    {
        if (!IsOpen) return EditorResult.None;
        _time += dt;
        if (_actions.Expire(_time)) _message = "";
        if (input.Close) return EditorResult.Close;
        var result = EditorResult.None;
        if (input.Column) { DropPending(); _actions.ToggleColumn(); }

        int vertical = _down.Steps(input.Down, dt) - _up.Steps(input.Up, dt);
        int lefts = _left.Steps(input.Left, dt), rights = _right.Steps(input.Right, dt);
        if (_actions.PresetsFocus)
        {
            if (vertical != 0) { DropPending(); _actions.MoveSlot(vertical); }
        }
        else
        {
            if (vertical != 0) { _row = Wrap(_row + vertical, _rows.Count + 1); DropPending(); }
            int steps = (rights - lefts) * (input.Big ? 10 : 1) + (input.Plus10 ? 10 : 0) - (input.Minus10 ? 10 : 0);
            if (_row == 0)
            {
                // the Base row moves one attacker per fresh press, never by repeat (a held stick must not run through confirmations)
                int dir = (_right.JustPressed ? 1 : 0) - (_left.JustPressed ? 1 : 0) + (input.Plus10 ? 1 : 0) - (input.Minus10 ? 1 : 0);
                if (dir != 0) StepBase(dir > 0 ? 1 : -1);
            }
            else if (steps != 0)
            {
                _rows[_row - 1].Step(Work, steps);
                _message = "";
            }
        }
        // Save, load and clear act on the highlighted slot whichever column has focus.
        if (input.Load) Act(_actions.Load(Work, WouldLose(), _time, out var loaded), loaded);
        if (input.Save && Act(_actions.Save(Work, BaseId, System.DateTime.Now, _time))) result = EditorResult.StoreChanged;
        if (input.Clear && Act(_actions.Clear(_time))) result = EditorResult.StoreChanged;
        Refresh();
        return result;
    }

    // ---- actions ----

    /// <summary>True when replacing the sheet now would lose edits: it matches neither a preset nor a shipped attacker.</summary>
    bool WouldLose() => !_store.MatchesAPreset(Work) && !_shipped.Any(s => PlayerPresets.SameValues(s, Work));

    void StepBase(int dir)
    {
        if (_shipped.Count == 0) return;
        int i = _shipped.FindIndex(s => s.Id == BaseId);
        var target = _shipped[i < 0 ? (dir > 0 ? 0 : _shipped.Count - 1) : Wrap(i + dir, _shipped.Count)];
        if (WouldLose() && !_actions.ConfirmOrArm("base", -1, target.Id, _time))
        {
            _message = $"Custom will be replaced by {target.Id}: press {(dir > 0 ? "→" : "←")} again";
            return;
        }
        _actions.ClearPending();
        Work = PlayerPresets.AsCustom(target);
        BaseId = target.Id;
        _message = $"Loaded {target.Id}";
    }

    /// <summary>Shows a preset action's outcome, takes a loaded sheet as the working copy, and reports it. True when the store changed.</summary>
    bool Act(PresetResult r, PlayerSheet? loaded = null)
    {
        if (loaded != null) Work = loaded;
        _message = r.Message;
        PresetAction?.Invoke(r);
        return r.StoreChanged;
    }

    /// <summary>Drops a pending confirmation, and its message with it.</summary>
    void DropPending()
    {
        if (_actions.Pending.Length > 0) _message = "";
        _actions.ClearPending();
    }

    static int Wrap(int i, int n) => ((i % n) + n) % n;

    // ---- drawing ----

    void Refresh()
    {
        for (int i = 0; i < _statLabels.Count; i++)
        {
            var (name, value) = _statLabels[i];
            bool sel = !_actions.PresetsFocus && i == _row;
            string v = i == 0 ? (BaseId.Length > 0 ? BaseId : "none") : _rows[i - 1].Value(Work);
            value.Text = sel ? $"◂ {v} ▸" : v;
            Colour(name, sel ? Selected : Colors.White);
            Colour(value, sel ? Selected : Value);
        }
        for (int i = 0; i < _slotLabels.Count; i++)
        {
            var p = _store.Slots[i];
            // the highlighted slot stays marked while the stats column has focus (save, load and clear still act on it), hollow then
            bool marked = i == _actions.Slot, sel = marked && _actions.PresetsFocus;
            _slotLabels[i].Text = $"{(sel ? "▸ " : marked ? "▹ " : "   ")}{i + 1,2}  {(p == null ? "empty" : p.Name)}";
            Colour(_slotLabels[i], sel ? Selected : marked ? Marked : p == null ? Dim : Colors.White);
        }
        _diff.Text = DiffLine();
        _confirm.Text = _message;
    }

    /// <summary>"edited from striker_a: close_dribbling 72 → 84, ..." against the Base attacker, or "same as striker_a".</summary>
    string DiffLine()
    {
        var b = _shipped.Find(s => s.Id == BaseId);
        if (b == null) return "";
        var changes = new List<string>();
        foreach (var r in _rows)
        {
            string from = r.Value(b), to = r.Value(Work);
            if (from != to) changes.Add($"{r.Name} {from} → {to}");
        }
        if (changes.Count == 0) return $"same as {BaseId}";
        const int Shown = 4;
        return $"edited from {BaseId}: {string.Join(", ", changes.Take(Shown))}{(changes.Count > Shown ? $" (+{changes.Count - Shown} more)" : "")}";
    }

    static void Colour(Label l, Color c) => l.LabelSettings.FontColor = c;

    static Label Make(string text, int size, Color colour) => new()
    {
        Text = text,
        MouseFilter = MouseFilterEnum.Ignore,
        LabelSettings = new LabelSettings { FontSize = size, FontColor = colour, OutlineSize = Mathf.Max(2, size / 8), OutlineColor = new Color(0, 0, 0, 0.9f) },
    };

    /// <summary>A held direction as steps: one on the press, then after <see cref="RepeatDelay"/> a rate rising to <see cref="RepeatMaxRate"/>.</summary>
    sealed class Repeater
    {
        float _held = -1f, _acc;
        public bool JustPressed { get; private set; }

        public void Reset() { _held = -1f; _acc = 0f; JustPressed = false; }

        public int Steps(bool down, float dt)
        {
            JustPressed = false;
            if (!down) { _held = -1f; _acc = 0f; return 0; }
            if (_held < 0f) { _held = 0f; _acc = 0f; JustPressed = true; return 1; }
            _held += dt;
            if (_held < RepeatDelay) return 0;
            float ramp = Mathf.Clamp((_held - RepeatDelay) / RepeatRampSeconds, 0f, 1f);
            _acc += dt * Mathf.Lerp(RepeatStartRate, RepeatMaxRate, ramp);
            int n = (int)_acc;
            _acc -= n;
            return n;
        }
    }
}
