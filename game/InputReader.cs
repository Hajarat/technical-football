using Godot;

namespace FootballSim.Game;

/// <summary>
/// The player editor's input for one tick, pad and keyboard merged. Directions are held states (the editor repeats them); the
/// rest are presses, true only on the tick the button went down.
/// </summary>
public struct EditorInput
{
    public bool Up, Down, Left, Right;   // D-pad / arrow keys, held
    public bool Big;                     // Shift held: an arrow steps 10
    public bool Minus10, Plus10;         // LB / RB
    public bool Column;                  // Y / Triangle, Tab: switch between the stats and the presets
    public bool Load, Save, Clear;       // A, X, View on the pad; Enter, S, Delete on the keyboard
    public bool Close;                   // B or Menu; Esc or T

    public static EditorInput Merge(in EditorInput a, in EditorInput b) => new()
    {
        Up = a.Up || b.Up, Down = a.Down || b.Down, Left = a.Left || b.Left, Right = a.Right || b.Right, Big = a.Big || b.Big,
        Minus10 = a.Minus10 || b.Minus10, Plus10 = a.Plus10 || b.Plus10, Column = a.Column || b.Column,
        Load = a.Load || b.Load, Save = a.Save || b.Save, Clear = a.Clear || b.Clear, Close = a.Close || b.Close,
    };
}

/// <summary>
/// The capture panel's input for one tick, pad and keyboard merged; every field is a press, true only on the tick the button went
/// down. The note is typed on the keyboard and read by the panel itself, never from the pad.
/// </summary>
public struct CaptureInput
{
    public bool Up, Down;        // D-pad / arrow keys: the tag
    public bool Save;            // A / Cross, Enter: save with the tag and the note
    public bool SaveNoNote;      // B / Circle, Esc: save with the tag and no note

    public static CaptureInput Merge(in CaptureInput a, in CaptureInput b) => new()
    {
        Up = a.Up || b.Up, Down = a.Down || b.Down, Save = a.Save || b.Save, SaveNoNote = a.SaveNoNote || b.SaveNoNote,
    };
}

/// <summary>One physics tick of gamepad state. Mirrors the core's TickIntent for the continuous fields.</summary>
public struct PadSnapshot
{
    public float MoveX, MoveZ;     // -1..1, radial dead zone applied; stick right = +MoveX, stick up = -MoveZ
    public float Sprint;           // 0..1, right trigger
    public bool Shield;            // left trigger past half (the core's close control)
    public bool ShootHeld;         // B / Circle; pressed again after the release, before the strike, a low shot (read by the core)
    public bool Finesse;           // right bumper: curved shot; with the left bumper and the left stick back, a drag-back (core)
    public bool Chip;              // left bumper: chip
    public bool Driven;            // not set by any device (there is no driven button: the low shot is shoot pressed twice); kept for replays
    public bool KnockOn;           // keyboard X: true from the press for a short window (see InputReader); the pad's knock-on is a right-stick gesture read by the core
    public float RightX, RightZ;   // -1..1, radial dead zone applied; stick right = +RightX, stick up = -RightZ (read by the core's gesture reader)
    public bool Cancel;            // A / Cross, right mouse or G held: a shot cancelled within the core's window becomes a fake
    public bool PassHeld;          // A / Cross, or F: a ground pass (the core reads it as the fake-shot cancel while a shot winds up)
    public bool ThroughHeld;       // Y / Triangle, or R: a through ball (the round flow reads Y as retry only at the report)
    public FootballSim.Core.SkillKey SkillKey; // keyboard 1-4 (the pad makes skill moves with the right stick): set for one poll per press
    // One-shot presses: true only on the tick the button went down.
    public bool OverlayToggle;     // right stick click
    public bool NextSpot, PrevSpot; // D-pad right / left
    public bool NextAttacker, NextKeeper; // D-pad up / down
    public bool Confirm;           // A / Cross
    public bool Retry;             // Y / Triangle
    public bool OpenCapture;       // View / Share / Back, keyboard M: open the capture panel
    public bool Pause;             // Menu / Options / Start
    public bool ControlsToggle;    // keyboard H: show or hide the controls panel
    public bool OpenEditor;        // keyboard T: open the player editor (the pad opens it with Y while paused, read from Retry)
    /// <summary>The player editor's reading of the same tick; Match uses it instead of the rest while the editor is open.</summary>
    public EditorInput Editor;
    /// <summary>The capture panel's reading of the same tick; Match uses it instead of the rest while the panel is open.</summary>
    public CaptureInput Capture;

    /// <summary>The shield button under the core's name for it (<see cref="FootballSim.Core.TickIntent.CloseControl"/>).</summary>
    public readonly bool CloseControl => Shield;
}

/// <summary>
/// Registers gamepad actions in code and returns one merged PadSnapshot per call to Poll(): the pad, plus a keyboard
/// fallback (see the keyboard table in docs/controls.md). When both push the stick, the larger magnitude wins; buttons are ORed.
/// </summary>
public partial class InputReader : Node
{
    const float DeadZone = 0.15f;
    /// <summary>Mouse travel, in pixels, before mouse motion counts as the person using keyboard and mouse.</summary>
    const float MouseMotionThreshold = 8f;
    /// <summary>Stick or trigger deflection that counts as the person using the pad (above its own dead zone).</summary>
    const float PadAxisThreshold = 0.3f;
    const string Left = "pad_left", Right = "pad_right", Up = "pad_up", Down = "pad_down",
        SprintA = "pad_sprint", ShieldA = "pad_shield",
        RLeft = "pad_rs_left", RRight = "pad_rs_right", RUp = "pad_rs_up", RDown = "pad_rs_down";
    // the same names as StringNames, made once: polling with a string builds a new StringName on every call
    static readonly StringName LeftName = Left, RightName = Right, UpName = Up, DownName = Down, SprintAName = SprintA, ShieldAName = ShieldA,
        RLeftName = RLeft, RRightName = RRight, RUpName = RUp, RDownName = RDown;
    /// <summary>
    /// A knock-on key press stays in the snapshot this long, seconds, so it is not lost when the ball is out of reach on the tick of
    /// the press. The core makes at most one knock-on from it: the ball then leaves reach.
    /// </summary>
    const float KnockOnHoldSeconds = 0.25f;
    int _knockOnLeft;

    // The connected pads, read when Godot reports a pad connecting or going away rather than on every call: asking Input for the
    // list builds a Godot array (and the name a string) each time, and the HUD asks several times a tick.
    int _padCount;
    string _padName = "";
    void RefreshPads()
    {
        var ids = Input.GetConnectedJoypads();
        _padCount = ids.Count;
        _padName = ids.Count > 0 ? Input.GetJoyName(ids[0]) : "";
    }

    public bool GamepadConnected => _padCount > 0;
    /// <summary>False in replay mode: the keyboard must not affect a replay.</summary>
    public bool KeyboardEnabled = true;
    /// <summary>True while the capture panel takes typed text: letter keys type the note, so the face-swap key does nothing.</summary>
    public bool TextEntry;
    /// <summary>Which device last produced a meaningful input event: "gamepad", "keyboard", or "none" until something is touched.</summary>
    public string Source { get; private set; } = "none";
    /// <summary>The device the person is using: <see cref="Source"/>, or the pad when one is connected and nothing was touched yet.</summary>
    public string Mode => Source == "none" && GamepadConnected ? "gamepad" : Source;
    public InputDevice Device => Mode == "gamepad" ? InputDevice.Gamepad : InputDevice.Keyboard;
    float _mouseTravel;
    /// <summary>Name of the first connected pad, or empty.</summary>
    public string ActivePadName => _padName;

    readonly System.Collections.Generic.Dictionary<KeyBind, bool> _keyPrev = new();

    /// <summary>True only on the poll where the binding went down.</summary>
    bool Pressed(KeyBind b)
    {
        bool now = b.Held();
        _keyPrev.TryGetValue(b, out bool was);
        _keyPrev[b] = now;
        return now && !was;
    }

    /// <summary>
    /// The one place the active device is decided: the most recent meaningful event. A key press, a mouse button or mouse motion
    /// beyond a few pixels is keyboard and mouse (ignored in replay mode); a pad button or a stick or trigger past a threshold is the pad.
    /// </summary>
    public override void _Input(InputEvent e)
    {
        switch (e)
        {
            case InputEventKey { Pressed: true, Echo: false } when KeyboardEnabled:
            case InputEventMouseButton { Pressed: true } when KeyboardEnabled:
                Source = "keyboard"; _mouseTravel = 0;
                break;
            case InputEventMouseMotion m when KeyboardEnabled:
                _mouseTravel += m.Relative.Length();
                if (_mouseTravel > MouseMotionThreshold) { Source = "keyboard"; _mouseTravel = 0; }
                break;
            case InputEventJoypadButton { Pressed: true }:
                Source = "gamepad";
                break;
            case InputEventJoypadMotion j when Mathf.Abs(j.AxisValue) > PadAxisThreshold:
                Source = "gamepad";
                break;
        }
    }

    public override void _Ready()
    {
        Input.JoyConnectionChanged += (_, _) => RefreshPads();
        RefreshPads();
        Axis(Left, JoyAxis.LeftX, -1); Axis(Right, JoyAxis.LeftX, 1);
        Axis(Up, JoyAxis.LeftY, -1); Axis(Down, JoyAxis.LeftY, 1);
        Axis(SprintA, JoyAxis.TriggerRight, 1); Axis(ShieldA, JoyAxis.TriggerLeft, 1);
        Axis(RLeft, JoyAxis.RightX, -1); Axis(RRight, JoyAxis.RightX, 1);
        Axis(RUp, JoyAxis.RightY, -1); Axis(RDown, JoyAxis.RightY, 1);
        BindFaceButtons();
        BindFixedButtons();
        LoadVibration();
    }

    // ---- face-button swap (A<->B, X<->Y) for pads that report them crossed, e.g. Switch-mode Xbox-layout pads ----

    const string ConfigPath = "user://controls.cfg";
    string? _boundName;
    /// <summary>True when A/B and X/Y are exchanged for the active pad.</summary>
    public bool SwapFaceButtons { get; private set; }
    /// <summary>Raised when the swap state is first known, the pad changes, or the person toggles it: (pad name, swapped, reason).</summary>
    public event System.Action<string, bool, string>? SwapChanged;

    /// <summary>Default for a pad name: Nintendo / Switch / Pro Controller pads arrive with the face buttons crossed.</summary>
    public static bool DefaultSwap(string padName) =>
        padName.Contains("Nintendo", System.StringComparison.OrdinalIgnoreCase) ||
        padName.Contains("Switch", System.StringComparison.OrdinalIgnoreCase) ||
        padName.Contains("Pro Controller", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>Registers every gamepad button binding that is not a face button.</summary>
    void BindFixedButtons()
    {
        foreach (var p in Bindings.Pad.All) if (!IsFace(p)) Btn(p.Action, p.Button(false));
    }

    static bool IsFace(PadBind p) => p == Bindings.Pad.Shoot || p == Bindings.Pad.Confirm || p == Bindings.Pad.Retry || p == Bindings.Pad.EditorSave;

    /// <summary>(Re)registers the face-button actions for the current swap state.</summary>
    void BindFaceButtons()
    {
        foreach (var p in Bindings.Pad.All)
        {
            if (!IsFace(p)) continue;
            if (InputMap.HasAction(p.Action)) InputMap.ActionEraseEvents(p.Action);
            Btn(p.Action, p.Button(SwapFaceButtons));
        }
    }

    /// <summary>Notices a new or changed pad and picks its swap setting: the saved choice if there is one, else the default.</summary>
    void TrackPad()
    {
        string name = GamepadConnected ? ActivePadName : "";
        if (name == _boundName) return;
        _boundName = name;
        var cfg = new ConfigFile();
        bool swap = DefaultSwap(name);
        if (cfg.Load(ConfigPath) == Error.Ok && name.Length > 0 && cfg.HasSectionKey("face_swap", name))
            swap = (bool)cfg.GetValue("face_swap", name);
        SwapFaceButtons = swap;
        BindFaceButtons();
        SwapChanged?.Invoke(name, swap, "detected");
    }

    /// <summary>Flips the swap for the active pad and remembers it under the pad's name.</summary>
    public void ToggleSwap()
    {
        string name = _boundName ?? "";
        SwapFaceButtons = !SwapFaceButtons;
        BindFaceButtons();
        if (name.Length > 0)
        {
            var cfg = new ConfigFile();
            cfg.Load(ConfigPath);
            cfg.SetValue("face_swap", name, SwapFaceButtons);
            cfg.Save(ConfigPath);
        }
        SwapChanged?.Invoke(name, SwapFaceButtons, "toggle");
    }

    // ---- controller vibration (on by default, saved for every pad) ----

    /// <summary>True when events may vibrate the pad.</summary>
    public bool Vibration { get; private set; } = true;
    /// <summary>Raised when the person toggles vibration: (on).</summary>
    public event System.Action<bool>? VibrationChanged;

    void LoadVibration()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) == Error.Ok && cfg.HasSectionKey("feedback", "vibration"))
            Vibration = (bool)cfg.GetValue("feedback", "vibration");
    }

    /// <summary>Turns vibration on or off and remembers it; turning it off stops any vibration under way.</summary>
    public void ToggleVibration()
    {
        Vibration = !Vibration;
        if (!Vibration) foreach (int id in Input.GetConnectedJoypads()) Input.StopJoyVibration(id);
        var cfg = new ConfigFile();
        cfg.Load(ConfigPath);
        cfg.SetValue("feedback", "vibration", Vibration);
        cfg.Save(ConfigPath);
        VibrationChanged?.Invoke(Vibration);
    }

    /// <summary>Call exactly once per _PhysicsProcess.</summary>
    public PadSnapshot Poll()
    {
        TrackPad();
        // (the key's edge is read every poll, so a B typed into a note does not toggle the swap when the panel closes)
        if (KeyboardEnabled && Pressed(Bindings.SwapFace) && !TextEntry) ToggleSwap();
        if (KeyboardEnabled && Pressed(Bindings.Vibration) && !TextEntry) ToggleVibration();
        var pad = PollPad();
        if (!KeyboardEnabled) return HoldKnockOn(pad);
        return HoldKnockOn(Merge(pad, PollKeyboard()));
    }

    /// <summary>Turns a knock-on press (true on one poll) into a short window of polls with KnockOn set.</summary>
    PadSnapshot HoldKnockOn(PadSnapshot p)
    {
        if (p.KnockOn) _knockOnLeft = Mathf.Max(1, Mathf.RoundToInt(KnockOnHoldSeconds * Engine.PhysicsTicksPerSecond));
        p.KnockOn = _knockOnLeft > 0;
        if (_knockOnLeft > 0) _knockOnLeft--;
        return p;
    }

    static PadSnapshot Merge(in PadSnapshot a, in PadSnapshot b)
    {
        var m = a;
        if (new Vector2(b.MoveX, b.MoveZ).LengthSquared() > new Vector2(a.MoveX, a.MoveZ).LengthSquared()) { m.MoveX = b.MoveX; m.MoveZ = b.MoveZ; }
        m.Sprint = Mathf.Max(a.Sprint, b.Sprint);
        m.Shield |= b.Shield; m.ShootHeld |= b.ShootHeld; m.Finesse |= b.Finesse; m.Chip |= b.Chip; m.Driven |= b.Driven; m.KnockOn |= b.KnockOn; m.Cancel |= b.Cancel; m.PassHeld |= b.PassHeld; m.ThroughHeld |= b.ThroughHeld;
        if (new Vector2(b.RightX, b.RightZ).LengthSquared() > new Vector2(a.RightX, a.RightZ).LengthSquared()) { m.RightX = b.RightX; m.RightZ = b.RightZ; }
        if (b.SkillKey != FootballSim.Core.SkillKey.None) m.SkillKey = b.SkillKey;
        m.OverlayToggle |= b.OverlayToggle; m.NextSpot |= b.NextSpot; m.PrevSpot |= b.PrevSpot;
        m.NextAttacker |= b.NextAttacker; m.NextKeeper |= b.NextKeeper;
        m.Confirm |= b.Confirm; m.Retry |= b.Retry; m.OpenCapture |= b.OpenCapture; m.Pause |= b.Pause; m.ControlsToggle |= b.ControlsToggle;
        m.OpenEditor |= b.OpenEditor;
        m.Editor = EditorInput.Merge(a.Editor, b.Editor);
        m.Capture = CaptureInput.Merge(a.Capture, b.Capture);
        return m;
    }

    /// <summary>
    /// Keyboard and mouse: WASD runs and aims, left mouse shoots, right mouse cancels, Ctrl sprints, Shift is the chip modifier,
    /// Q is the shield, E the curved-shot modifier and the arrow keys are the D-pad.
    /// </summary>
    PadSnapshot PollKeyboard()
    {
        float x = (Bindings.Right.Held() ? 1f : 0f) - (Bindings.Left.Held() ? 1f : 0f);
        float z = (Bindings.Down.Held() ? 1f : 0f) - (Bindings.Up.Held() ? 1f : 0f);
        var v = new Vector2(x, z);
        if (v.LengthSquared() > 1f) v = v.Normalized();
        return new PadSnapshot
        {
            MoveX = v.X, MoveZ = v.Y,
            Sprint = Bindings.Sprint.Held() ? 1f : 0f,
            Shield = Bindings.Shield.Held(),
            ShootHeld = Bindings.Shoot.Held(),
            Finesse = Bindings.Finesse.Held(),
            Chip = Bindings.Chip.Held(),
            KnockOn = Pressed(Bindings.KnockOn),
            Cancel = Bindings.Cancel.Held(),
            PassHeld = Bindings.Pass.Held(),
            ThroughHeld = Bindings.Through.Held(),
            SkillKey = SkillKeyPressed(),
            OverlayToggle = Pressed(Bindings.Overlay),
            NextSpot = Pressed(Bindings.NextSpot),
            PrevSpot = Pressed(Bindings.PrevSpot),
            NextAttacker = Pressed(Bindings.NextAttacker),
            NextKeeper = Pressed(Bindings.NextKeeper),
            Confirm = Pressed(Bindings.Confirm),
            Retry = Pressed(Bindings.Retry),
            OpenCapture = Pressed(Bindings.CaptureOpen),
            Pause = Pressed(Bindings.Pause),
            ControlsToggle = Pressed(Bindings.Controls),
            OpenEditor = Pressed(Bindings.EditorOpen),
            Editor = new EditorInput
            {
                Up = Bindings.Editor.Up.Held(), Down = Bindings.Editor.Down.Held(),
                Left = Bindings.Editor.Left.Held(), Right = Bindings.Editor.Right.Held(),
                Big = Bindings.Chip.Held(),
                Column = Pressed(Bindings.Editor.Column), Load = Pressed(Bindings.Editor.Load), Save = Pressed(Bindings.Editor.Save),
                Clear = Pressed(Bindings.Editor.Clear), Close = Pressed(Bindings.Editor.Close),
            },
            Capture = new CaptureInput
            {
                Up = Pressed(Bindings.CaptureKeys.Up), Down = Pressed(Bindings.CaptureKeys.Down),
                Save = Pressed(Bindings.CaptureKeys.Save), SaveNoNote = Pressed(Bindings.CaptureKeys.SaveNoNote),
            },
        };
    }

    /// <summary>Keys 1-4 select a skill move directly (one poll per press); all four are polled every tick so their edge state stays current.</summary>
    static FootballSim.Core.SkillKey SkillKeyOf(bool one, bool two, bool three, bool four) =>
        four ? FootballSim.Core.SkillKey.DragBack : three ? FootballSim.Core.SkillKey.BallRoll :
        two ? FootballSim.Core.SkillKey.BodyFeint : one ? FootballSim.Core.SkillKey.StepOver : FootballSim.Core.SkillKey.None;

    FootballSim.Core.SkillKey SkillKeyPressed() =>
        SkillKeyOf(Pressed(Bindings.Skill1), Pressed(Bindings.Skill2), Pressed(Bindings.Skill3), Pressed(Bindings.Skill4));

    PadSnapshot PollPad()
    {
        float x = Input.GetActionRawStrength(RightName) - Input.GetActionRawStrength(LeftName);
        float y = Input.GetActionRawStrength(DownName) - Input.GetActionRawStrength(UpName);
        var v = new Vector2(x, y);
        float len = v.Length();
        v = len < DeadZone ? Vector2.Zero : v / len * Mathf.Min(1f, (len - DeadZone) / (1f - DeadZone));
        // The right stick goes to the core raw (same axes and radial dead zone as the left); the core reads the gestures.
        var r = new Vector2(Input.GetActionRawStrength(RRightName) - Input.GetActionRawStrength(RLeftName),
            Input.GetActionRawStrength(RDownName) - Input.GetActionRawStrength(RUpName));
        float rlen = r.Length();
        r = rlen < DeadZone ? Vector2.Zero : r / rlen * Mathf.Min(1f, (rlen - DeadZone) / (1f - DeadZone));
        return new PadSnapshot
        {
            MoveX = v.X,
            MoveZ = v.Y,
            Sprint = Mathf.Clamp(Input.GetActionRawStrength(SprintAName), 0f, 1f),
            Shield = Input.GetActionRawStrength(ShieldAName) > 0.5f,
            ShootHeld = Input.IsActionPressed(Bindings.Pad.Shoot.ActionName),
            Finesse = Input.IsActionPressed(Bindings.Pad.Finesse.ActionName),
            Chip = Input.IsActionPressed(Bindings.Pad.Chip.ActionName),
            RightX = r.X,
            RightZ = r.Y,
            // A / Cross doubles as confirm, but the round flow reads confirm only in Setup and Report, never while live.
            Cancel = Input.IsActionPressed(Bindings.Pad.Confirm.ActionName),
            PassHeld = Input.IsActionPressed(Bindings.Pad.Confirm.ActionName),
            ThroughHeld = Input.IsActionPressed(Bindings.Pad.Retry.ActionName),
            OverlayToggle = Input.IsActionJustPressed(Bindings.Pad.Overlay.ActionName),
            NextSpot = Input.IsActionJustPressed(Bindings.Pad.NextSpot.ActionName),
            PrevSpot = Input.IsActionJustPressed(Bindings.Pad.PrevSpot.ActionName),
            NextAttacker = Input.IsActionJustPressed(Bindings.Pad.NextAttacker.ActionName),
            NextKeeper = Input.IsActionJustPressed(Bindings.Pad.NextKeeper.ActionName),
            Confirm = Input.IsActionJustPressed(Bindings.Pad.Confirm.ActionName),
            Retry = Input.IsActionJustPressed(Bindings.Pad.Retry.ActionName),
            OpenCapture = Input.IsActionJustPressed(Bindings.Pad.Capture.ActionName),
            Pause = Input.IsActionJustPressed(Bindings.Pad.Pause.ActionName),
            Editor = new EditorInput
            {
                Up = Input.IsActionPressed(Bindings.Pad.NextAttacker.ActionName), Down = Input.IsActionPressed(Bindings.Pad.NextKeeper.ActionName),
                Left = Input.IsActionPressed(Bindings.Pad.PrevSpot.ActionName), Right = Input.IsActionPressed(Bindings.Pad.NextSpot.ActionName),
                Minus10 = Input.IsActionJustPressed(Bindings.Pad.Chip.ActionName), Plus10 = Input.IsActionJustPressed(Bindings.Pad.Finesse.ActionName),
                Column = Input.IsActionJustPressed(Bindings.Pad.Retry.ActionName),
                Load = Input.IsActionJustPressed(Bindings.Pad.Confirm.ActionName),
                Save = Input.IsActionJustPressed(Bindings.Pad.EditorSave.ActionName),
                Clear = Input.IsActionJustPressed(Bindings.Pad.Capture.ActionName),
                Close = Input.IsActionJustPressed(Bindings.Pad.Shoot.ActionName) || Input.IsActionJustPressed(Bindings.Pad.Pause.ActionName),
            },
            Capture = new CaptureInput
            {
                Up = Input.IsActionJustPressed(Bindings.Pad.NextAttacker.ActionName), Down = Input.IsActionJustPressed(Bindings.Pad.NextKeeper.ActionName),
                Save = Input.IsActionJustPressed(Bindings.Pad.Confirm.ActionName), SaveNoNote = Input.IsActionJustPressed(Bindings.Pad.Shoot.ActionName),
            },
        };
    }

    static void Add(string name, InputEvent ev)
    {
        if (!InputMap.HasAction(name)) InputMap.AddAction(name, 0f);
        InputMap.ActionAddEvent(name, ev);
    }

    static void Axis(string name, JoyAxis axis, float sign) => Add(name, new InputEventJoypadMotion { Device = -1, Axis = axis, AxisValue = sign });
    static void Btn(string name, JoyButton b) => Add(name, new InputEventJoypadButton { Device = -1, ButtonIndex = b });
}
