using Godot;

namespace FootballSim.Game;

/// <summary>One physics tick of gamepad state. Mirrors the core's TickIntent for the continuous fields.</summary>
public struct PadSnapshot
{
    public float MoveX, MoveZ;     // -1..1, radial dead zone applied; stick right = +MoveX, stick up = -MoveZ
    public float Sprint;           // 0..1, right trigger
    public bool CloseControl;      // left trigger past half
    public bool ShootHeld;         // B / Circle
    public bool Finesse;           // right bumper
    public bool Chip;              // left bumper
    // One-shot presses: true only on the tick the button went down.
    public bool OverlayToggle;     // right stick click
    public bool NextSpot, PrevSpot; // D-pad right / left
    public bool NextAttacker, NextKeeper; // D-pad up / down
    public bool Confirm;           // A / Cross
    public bool Retry;             // Y / Triangle
    public bool Flag;              // View / Share / Back
    public bool Pause;             // Menu / Options / Start
}

/// <summary>
/// Registers gamepad actions in code and returns one merged PadSnapshot per call to Poll(): the pad, plus a keyboard
/// fallback (see the README table). When both push the stick, the larger magnitude wins; buttons are ORed.
/// </summary>
public partial class InputReader : Node
{
    const float DeadZone = 0.15f;
    const string Left = "pad_left", Right = "pad_right", Up = "pad_up", Down = "pad_down",
        SprintA = "pad_sprint", CloseA = "pad_close", Shoot = "pad_shoot", Fin = "pad_finesse", ChipA = "pad_chip",
        Overlay = "pad_overlay", Next = "pad_next", Prev = "pad_prev", Confirm = "pad_confirm", Retry = "pad_retry",
        Flag = "pad_flag", Pause = "pad_pause", NextAtk = "pad_next_attacker", NextKeep = "pad_next_keeper";

    public bool GamepadConnected => Input.GetConnectedJoypads().Count > 0;
    /// <summary>False in replay mode: the keyboard must not affect a replay.</summary>
    public bool KeyboardEnabled = true;
    /// <summary>Which device last produced input: "gamepad", "keyboard", or "none" until something is touched.</summary>
    public string Source { get; private set; } = "none";
    /// <summary>Name of the first connected pad, or empty.</summary>
    public string ActivePadName
    {
        get
        {
            var ids = Input.GetConnectedJoypads();
            return ids.Count > 0 ? Input.GetJoyName((int)ids[0]) : "";
        }
    }

    readonly System.Collections.Generic.Dictionary<Key, bool> _keyPrev = new();

    bool Held(params Key[] keys)
    {
        foreach (var k in keys) if (Input.IsKeyPressed(k)) return true;
        return false;
    }

    /// <summary>True only on the poll where one of the keys went down.</summary>
    bool Pressed(Key id, params Key[] keys)
    {
        bool now = Held(keys);
        _keyPrev.TryGetValue(id, out bool was);
        _keyPrev[id] = now;
        return now && !was;
    }

    public override void _Ready()
    {
        Axis(Left, JoyAxis.LeftX, -1); Axis(Right, JoyAxis.LeftX, 1);
        Axis(Up, JoyAxis.LeftY, -1); Axis(Down, JoyAxis.LeftY, 1);
        Axis(SprintA, JoyAxis.TriggerRight, 1); Axis(CloseA, JoyAxis.TriggerLeft, 1);
        BindFaceButtons();
        Btn(Fin, JoyButton.RightShoulder); Btn(ChipA, JoyButton.LeftShoulder);
        Btn(Overlay, JoyButton.RightStick); Btn(Next, JoyButton.DpadRight); Btn(Prev, JoyButton.DpadLeft); Btn(NextAtk, JoyButton.DpadUp); Btn(NextKeep, JoyButton.DpadDown);
        Btn(Flag, JoyButton.Back); Btn(Pause, JoyButton.Start);
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

    JoyButton Face(JoyButton b) => !SwapFaceButtons ? b : b switch
    {
        JoyButton.A => JoyButton.B, JoyButton.B => JoyButton.A, JoyButton.X => JoyButton.Y, JoyButton.Y => JoyButton.X, _ => b,
    };

    void BindFaceButtons()
    {
        foreach (var a in new[] { Shoot, Confirm, Retry }) { if (InputMap.HasAction(a)) InputMap.ActionEraseEvents(a); }
        Btn(Shoot, Face(JoyButton.B)); Btn(Confirm, Face(JoyButton.A)); Btn(Retry, Face(JoyButton.Y));
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

    /// <summary>Call exactly once per _PhysicsProcess.</summary>
    public PadSnapshot Poll()
    {
        TrackPad();
        if (KeyboardEnabled && Pressed(Key.B, Key.B)) ToggleSwap();
        var pad = PollPad();
        if (!KeyboardEnabled) { Source = PadTouched(pad) ? "gamepad" : Source; return pad; }
        var key = PollKeyboard();
        bool padTouched = PadTouched(pad), keyTouched = PadTouched(key);
        if (keyTouched && !padTouched) Source = "keyboard";
        else if (padTouched) Source = "gamepad";
        return Merge(pad, key);
    }

    static bool PadTouched(in PadSnapshot p) =>
        p.MoveX != 0 || p.MoveZ != 0 || p.Sprint > 0.05f || p.CloseControl || p.ShootHeld || p.Finesse || p.Chip ||
        p.OverlayToggle || p.NextSpot || p.PrevSpot || p.NextAttacker || p.NextKeeper || p.Confirm || p.Retry || p.Flag || p.Pause;

    static PadSnapshot Merge(in PadSnapshot a, in PadSnapshot b)
    {
        var m = a;
        if (new Vector2(b.MoveX, b.MoveZ).LengthSquared() > new Vector2(a.MoveX, a.MoveZ).LengthSquared()) { m.MoveX = b.MoveX; m.MoveZ = b.MoveZ; }
        m.Sprint = Mathf.Max(a.Sprint, b.Sprint);
        m.CloseControl |= b.CloseControl; m.ShootHeld |= b.ShootHeld; m.Finesse |= b.Finesse; m.Chip |= b.Chip;
        m.OverlayToggle |= b.OverlayToggle; m.NextSpot |= b.NextSpot; m.PrevSpot |= b.PrevSpot;
        m.NextAttacker |= b.NextAttacker; m.NextKeeper |= b.NextKeeper;
        m.Confirm |= b.Confirm; m.Retry |= b.Retry; m.Flag |= b.Flag; m.Pause |= b.Pause;
        return m;
    }

    PadSnapshot PollKeyboard()
    {
        float x = (Held(Key.Right, Key.D) ? 1f : 0f) - (Held(Key.Left, Key.A) ? 1f : 0f);
        float z = (Held(Key.Down, Key.S) ? 1f : 0f) - (Held(Key.Up, Key.W) ? 1f : 0f);
        var v = new Vector2(x, z);
        if (v.LengthSquared() > 1f) v = v.Normalized();
        return new PadSnapshot
        {
            MoveX = v.X, MoveZ = v.Y,
            Sprint = Held(Key.Shift) ? 1f : 0f,
            CloseControl = Held(Key.Ctrl, Key.Alt),
            ShootHeld = Held(Key.Space),
            Finesse = Held(Key.E),
            Chip = Held(Key.Q),
            OverlayToggle = Pressed(Key.O, Key.O),
            NextSpot = Pressed(Key.Bracketright, Key.Bracketright),
            PrevSpot = Pressed(Key.Bracketleft, Key.Bracketleft),
            NextAttacker = Pressed(Key.Comma, Key.Comma),
            NextKeeper = Pressed(Key.Period, Key.Period),
            Confirm = Pressed(Key.Enter, Key.Enter, Key.KpEnter),
            Retry = Pressed(Key.R, Key.R),
            Flag = Pressed(Key.F, Key.F),
            Pause = Pressed(Key.P, Key.P, Key.Escape),
        };
    }

    PadSnapshot PollPad()
    {
        float x = Input.GetActionRawStrength(Right) - Input.GetActionRawStrength(Left);
        float y = Input.GetActionRawStrength(Down) - Input.GetActionRawStrength(Up);
        var v = new Vector2(x, y);
        float len = v.Length();
        v = len < DeadZone ? Vector2.Zero : v / len * Mathf.Min(1f, (len - DeadZone) / (1f - DeadZone));
        return new PadSnapshot
        {
            MoveX = v.X,
            MoveZ = v.Y,
            Sprint = Mathf.Clamp(Input.GetActionRawStrength(SprintA), 0f, 1f),
            CloseControl = Input.GetActionRawStrength(CloseA) > 0.5f,
            ShootHeld = Input.IsActionPressed(Shoot),
            Finesse = Input.IsActionPressed(Fin),
            Chip = Input.IsActionPressed(ChipA),
            OverlayToggle = Input.IsActionJustPressed(Overlay),
            NextSpot = Input.IsActionJustPressed(Next),
            PrevSpot = Input.IsActionJustPressed(Prev),
            NextAttacker = Input.IsActionJustPressed(NextAtk),
            NextKeeper = Input.IsActionJustPressed(NextKeep),
            Confirm = Input.IsActionJustPressed(Confirm),
            Retry = Input.IsActionJustPressed(Retry),
            Flag = Input.IsActionJustPressed(Flag),
            Pause = Input.IsActionJustPressed(Pause),
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
