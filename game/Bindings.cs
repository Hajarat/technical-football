using System.Collections.Generic;
using Godot;

namespace FootballSim.Game;

public enum InputDevice { Keyboard, Gamepad }

/// <summary>Label set a pad is shown with; picked from the pad's name.</summary>
public enum PadKind { Xbox, PlayStation, Nintendo }

/// <summary>Where a face button sits on the pad, whatever it is called there. The shoot button is always <see cref="Right"/>.</summary>
public enum FaceSlot { Bottom, Right, Left, Top }

/// <summary>A keyboard / mouse binding: the keys and mouse buttons that trigger it and the text the controls panel shows for it.</summary>
public sealed class KeyBind
{
    public readonly string Label;
    readonly Key[] _keys;
    readonly MouseButton[] _mouse;

    public KeyBind(string label, params Key[] keys) : this(label, keys, System.Array.Empty<MouseButton>()) { }
    public KeyBind(string label, Key[] keys, MouseButton[] mouse) { Label = label; _keys = keys; _mouse = mouse; }

    public bool Held()
    {
        foreach (var k in _keys) if (Input.IsKeyPressed(k)) return true;
        foreach (var m in _mouse) if (Input.IsMouseButtonPressed(m)) return true;
        return false;
    }
}

/// <summary>
/// A gamepad button binding. Face buttons are declared by slot, so the button actually bound follows the face swap, and the label
/// is the name that slot has on the pad in hand (the swap exists so that the right-hand button is the shoot button on every pad).
/// </summary>
public sealed class PadBind
{
    public readonly string Action;
    readonly JoyButton _button;
    readonly FaceSlot? _slot;

    public PadBind(string action, JoyButton button) { Action = action; _button = button; }
    public PadBind(string action, FaceSlot slot) { Action = action; _slot = slot; _button = JoyButton.Invalid; }

    /// <summary>The button index that triggers this action when the pad reports its face buttons crossed or not.</summary>
    public JoyButton Button(bool swapped) => _slot is not { } s ? _button : s switch
    {
        FaceSlot.Bottom => swapped ? JoyButton.B : JoyButton.A,
        FaceSlot.Right => swapped ? JoyButton.A : JoyButton.B,
        FaceSlot.Left => swapped ? JoyButton.Y : JoyButton.X,
        _ => swapped ? JoyButton.X : JoyButton.Y,
    };

    public string Label(PadKind kind) => _slot is { } s ? FaceName(kind, s) : ButtonName(kind, _button);

    public static string FaceName(PadKind kind, FaceSlot s) => (kind, s) switch
    {
        (PadKind.PlayStation, FaceSlot.Bottom) => "Cross", (PadKind.PlayStation, FaceSlot.Right) => "Circle",
        (PadKind.PlayStation, FaceSlot.Left) => "Square", (PadKind.PlayStation, FaceSlot.Top) => "Triangle",
        (PadKind.Nintendo, FaceSlot.Bottom) => "B", (PadKind.Nintendo, FaceSlot.Right) => "A",
        (PadKind.Nintendo, FaceSlot.Left) => "Y", (PadKind.Nintendo, FaceSlot.Top) => "X",
        (_, FaceSlot.Bottom) => "A", (_, FaceSlot.Right) => "B", (_, FaceSlot.Left) => "X", _ => "Y",
    };

    public static string ButtonName(PadKind kind, JoyButton b) => (kind, b) switch
    {
        (PadKind.PlayStation, JoyButton.LeftShoulder) => "L1", (PadKind.PlayStation, JoyButton.RightShoulder) => "R1",
        (PadKind.PlayStation, JoyButton.Back) => "Share", (PadKind.PlayStation, JoyButton.Start) => "Options",
        (PadKind.PlayStation, JoyButton.RightStick) => "R3",
        (PadKind.Nintendo, JoyButton.LeftShoulder) => "L", (PadKind.Nintendo, JoyButton.RightShoulder) => "R",
        (PadKind.Nintendo, JoyButton.Back) => "-", (PadKind.Nintendo, JoyButton.Start) => "+",
        (PadKind.Nintendo, JoyButton.RightStick) => "RS click",
        (_, JoyButton.LeftShoulder) => "LB", (_, JoyButton.RightShoulder) => "RB",
        (_, JoyButton.Back) => "View", (_, JoyButton.Start) => "Menu",
        (_, JoyButton.RightStick) => "RS click",
        (_, JoyButton.DpadUp) => "D-pad ↑", (_, JoyButton.DpadDown) => "D-pad ↓",
        (_, JoyButton.DpadLeft) => "D-pad ←", (_, JoyButton.DpadRight) => "D-pad →",
        _ => b.ToString(),
    };

    public static string Trigger(PadKind kind, bool left) => kind switch
    {
        PadKind.PlayStation => left ? "L2" : "R2",
        PadKind.Nintendo => left ? "ZL" : "ZR",
        _ => left ? "LT" : "RT",
    };

    /// <summary>Kind from a joypad name as the OS reports it; an unknown or missing name gets Xbox naming.</summary>
    public static PadKind KindOf(string padName)
    {
        if (InputReader.DefaultSwap(padName)) return PadKind.Nintendo;
        foreach (var s in new[] { "PlayStation", "PS3", "PS4", "PS5", "DualShock", "DualSense", "Sony" })
            if (padName.Contains(s, System.StringComparison.OrdinalIgnoreCase)) return PadKind.PlayStation;
        return PadKind.Xbox;
    }
}

/// <summary>
/// Every button and key binding, declared once. <see cref="InputReader"/> polls and registers them; <see cref="ControlsPanel"/>
/// shows their labels. Axes (sticks, triggers) are not buttons and are named in the panel rows.
/// </summary>
public static class Bindings
{
    // ---- keyboard and mouse (see docs/controls.md, the keyboard table) ----
    public static readonly KeyBind Left = new("A", Key.A), Right = new("D", Key.D), Up = new("W", Key.W), Down = new("S", Key.S);
    public static readonly KeyBind Sprint = new("Ctrl", Key.Ctrl);
    public static readonly KeyBind Shield = new("Q", Key.Q);
    public static readonly KeyBind Shoot = new("Left mouse / Space", new[] { Key.Space }, new[] { MouseButton.Left });
    public static readonly KeyBind Finesse = new("E", Key.E);
    public static readonly KeyBind Chip = new("Shift", Key.Shift);
    public static readonly KeyBind KnockOn = new("X", Key.X);
    public static readonly KeyBind Pass = new("F", Key.F), Through = new("R", Key.R);
    public static readonly KeyBind Cancel = new("Right mouse / G", new[] { Key.G }, new[] { MouseButton.Right });
    public static readonly KeyBind Skill1 = new("1", Key.Key1, Key.Kp1), Skill2 = new("2", Key.Key2, Key.Kp2),
        Skill3 = new("3", Key.Key3, Key.Kp3), Skill4 = new("4", Key.Key4, Key.Kp4);
    public static readonly KeyBind Overlay = new("O", Key.O);
    public static readonly KeyBind NextSpot = new("→", Key.Right), PrevSpot = new("←", Key.Left);
    public static readonly KeyBind NextAttacker = new("↑", Key.Up), NextKeeper = new("↓", Key.Down);
    public static readonly KeyBind Confirm = new("Enter", Key.Enter, Key.KpEnter);
    public static readonly KeyBind Retry = new("Backspace", Key.Backspace);
    /// <summary>Opens the capture panel, in any state (see <see cref="CapturePanel"/>).</summary>
    public static readonly KeyBind CaptureOpen = new("M", Key.M);
    public static readonly KeyBind Pause = new("P / Esc", Key.P, Key.Escape);
    public static readonly KeyBind SwapFace = new("B", Key.B);
    /// <summary>Turns controller vibration on or off (saved).</summary>
    public static readonly KeyBind Vibration = new("V", Key.V);
    public static readonly KeyBind Controls = new("H", Key.H);
    /// <summary>Opens the player editor while play is stopped (paused, in Setup or at the report).</summary>
    public static readonly KeyBind EditorOpen = new("T", Key.T);

    /// <summary>
    /// Keys read only while the player editor is open (gameplay input is ignored then). Separate instances from the gameplay keys on
    /// the same physical keys, so each keeps its own pressed edge. Shift (the chip key) held makes an arrow step 10.
    /// </summary>
    public static class Editor
    {
        public static readonly KeyBind Up = new("↑", Key.Up), Down = new("↓", Key.Down), Left = new("←", Key.Left), Right = new("→", Key.Right);
        public static readonly KeyBind Column = new("Tab", Key.Tab);
        public static readonly KeyBind Load = new("Enter", Key.Enter, Key.KpEnter);
        public static readonly KeyBind Save = new("S", Key.S);
        public static readonly KeyBind Clear = new("Delete", Key.Delete);
        public static readonly KeyBind Close = new("Esc / T", Key.Escape, Key.T);
    }

    /// <summary>
    /// Keys read only while the capture panel is open (gameplay input is ignored then, and letter keys type the note instead).
    /// Separate instances from the gameplay keys on the same physical keys, so each keeps its own pressed edge.
    /// </summary>
    public static class CaptureKeys
    {
        public static readonly KeyBind Up = new("↑", Key.Up), Down = new("↓", Key.Down);
        public static readonly KeyBind Save = new("Enter", Key.Enter, Key.KpEnter);
        public static readonly KeyBind SaveNoNote = new("Esc", Key.Escape);
    }

    // ---- gamepad buttons (the sticks and triggers are axes) ----
    public static class Pad
    {
        public static readonly PadBind Shoot = new("pad_shoot", FaceSlot.Right), Confirm = new("pad_confirm", FaceSlot.Bottom),
            Retry = new("pad_retry", FaceSlot.Top);
        public static readonly PadBind Finesse = new("pad_finesse", JoyButton.RightShoulder), Chip = new("pad_chip", JoyButton.LeftShoulder);
        public static readonly PadBind Overlay = new("pad_overlay", JoyButton.RightStick);
        public static readonly PadBind NextSpot = new("pad_next", JoyButton.DpadRight), PrevSpot = new("pad_prev", JoyButton.DpadLeft);
        public static readonly PadBind NextAttacker = new("pad_next_attacker", JoyButton.DpadUp), NextKeeper = new("pad_next_keeper", JoyButton.DpadDown);
        /// <summary>View / Share / -: opens the capture panel; in the player editor, clears a preset slot.</summary>
        public static readonly PadBind Capture = new("pad_capture", JoyButton.Back), Pause = new("pad_pause", JoyButton.Start);
        /// <summary>X / Square: read only in the player editor (save into a preset slot); nothing during play.</summary>
        public static readonly PadBind EditorSave = new("pad_editor_save", FaceSlot.Left);
        public static readonly PadBind[] All =
        {
            Shoot, Confirm, Retry, Finesse, Chip, Overlay, NextSpot, PrevSpot, NextAttacker, NextKeeper, Capture, Pause, EditorSave,
        };
    }

    /// <summary>One line of the panel: what it does, the keyboard text, the pad text, and whether the move is not built yet.</summary>
    public sealed record Row(string Action, string Keyboard, string Pad, bool NotBuilt = false);
    public sealed record Group(string Title, List<Row> Rows);

    /// <summary>
    /// The panel content for a pad kind. Gesture wording follows GestureReader and docs/controls.md: skill-move directions are taken
    /// against the running direction, "→ or ←" means either side, and the timings live in the `skill` tuning section.
    /// </summary>
    public static List<Group> Groups(PadKind k)
    {
        string B(PadBind p) => p.Label(k);
        string shoot = B(Pad.Shoot), rb = B(Pad.Finesse), lb = B(Pad.Chip), cancel = B(Pad.Confirm);
        string rt = PadBind.Trigger(k, false), lt = PadBind.Trigger(k, true);
        string pads(PadBind a, PadBind b) => $"{B(a)} / {B(b)}";
        return new List<Group>
        {
            new("Movement", new()
            {
                new("Run and aim", $"{Up.Label} {Left.Label} {Down.Label} {Right.Label} ({Right.Label} = toward the goal)", "Left stick (right = toward the goal)"),
                new("Sprint", Sprint.Label, rt),
                new("Shield (close control)", Shield.Label, $"{lt} (past half)"),
            }),
            new("Shooting", new()
            {
                new("Power shot", $"{Shoot.Label} (hold, release)", $"{shoot} (hold, release)"),
                new("Curved shot", $"{Finesse.Label} + shoot", $"{rb} + {shoot}"),
                new("Chip", $"{Chip.Label} + shoot", $"{lb} + {shoot}"),
                new("Low shot", "shoot, release, shoot again", $"{shoot}, release, {shoot} again"),
                new("Low curved shot", $"{Finesse.Label} + shoot, release, shoot again", $"{rb} + {shoot}, release, {shoot} again"),
                new("Low chip", $"{Chip.Label} + shoot, release, shoot again (plays as a chip)", $"{lb} + {shoot}, release, {shoot} again (plays as a chip)"),
                new("Fake shot", $"shoot, then {Cancel.Label}", $"{shoot}, then {cancel}"),
            }),
            new("Passing (with a team-mate)", new()
            {
                new("Ground pass", $"{Pass.Label} (stick aims, hold points further)", $"{cancel} (stick aims, hold points further)"),
                new("Through ball", $"{Through.Label} (stick aims, hold points further)", $"{B(Pad.Retry)} (stick aims, hold points further)"),
                new("First-time pass or shot", "pass or shoot while the pass is on its way", "pass or shoot while the pass is on its way"),
            }),
            new("Skill moves", new()
            {
                new("Step-over", $"{Skill1.Label} (side from {Left.Label} / {Right.Label})", "Right stick ↑ then → or ← (no return to centre)"),
                new("Body feint", $"{Skill2.Label} (side from {Left.Label} / {Right.Label})", "Right stick → or ← flicked from centre, back to centre"),
                new("Ball roll", $"{Skill3.Label} (side from {Left.Label} / {Right.Label})", "Right stick → or ← held"),
                new("Drag-back", $"{Skill4.Label}, or back + {Finesse.Label} + {Chip.Label}", $"Left stick ↓ + {lb} + {rb}"),
                new("Knock-on", KnockOn.Label, "Right stick ↑ flick while sprinting"),
                new("Elastico", "no key", "Right stick ↓ then → or ←"),
                new("Heel-to-heel", "no key", "Right stick ↑ flick, centre, then ↓"),
                new("Spin and go", "no key", "Right stick ↑ flick, centre, then → or ←"),
            }),
            new("Round and menu", new()
            {
                new("Start round / next round", Confirm.Label, B(Pad.Confirm)),
                new("Retry (at the report)", Retry.Label, B(Pad.Retry)),
                new("Start spot: previous / next", $"{PrevSpot.Label} / {NextSpot.Label}", pads(Pad.PrevSpot, Pad.NextSpot)),
                new("Attacker / keeper select", $"{NextAttacker.Label} / {NextKeeper.Label}", pads(Pad.NextAttacker, Pad.NextKeeper)),
                new("Pause", Pause.Label, B(Pad.Pause)),
                new("Player editor (stopped play)", $"{EditorOpen.Label} (paused, Setup or report)", $"{B(Pad.Retry)} while paused"),
                new("Capture this round (tag and note)", CaptureOpen.Label, B(Pad.Capture)),
                new("Debug overlay", Overlay.Label, B(Pad.Overlay)),
                new("Swap face buttons", SwapFace.Label, $"keyboard {SwapFace.Label}"),
                new("Controller vibration on / off", Vibration.Label, $"keyboard {Vibration.Label}"),
                new("This panel", Controls.Label, $"{B(Pad.Pause)} (shown while paused)"),
            }),
            new("Player editor", new()
            {
                new("Row / change by 1 (hold repeats)", $"{Editor.Up.Label}{Editor.Down.Label} / {Editor.Left.Label}{Editor.Right.Label}", "D-pad ↑↓ / ←→"),
                new("Change by 10", $"{Chip.Label} + {Editor.Left.Label}{Editor.Right.Label}", pads(Pad.Chip, Pad.Finesse)),
                new("Stats / presets column", Editor.Column.Label, B(Pad.Retry)),
                new("Marked slot: load / save / clear (either column)", $"{Editor.Load.Label} / {Editor.Save.Label} / {Editor.Clear.Label}", $"{B(Pad.Confirm)} / {B(Pad.EditorSave)} / {B(Pad.Capture)}"),
                new("Close (applies the player)", Editor.Close.Label, pads(Pad.Shoot, Pad.Pause)),
            }),
        };
    }
}
