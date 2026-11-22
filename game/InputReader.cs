using Godot;

namespace FootballSim.Game;

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
    // A, X, Y and right bumper + A are pass buttons on a standard layout: passing is not built, so during play A only
    // cancels a shot, X does nothing, and Y is read only at the report (retry).
    public FootballSim.Core.SkillKey SkillKey; // keyboard 1-4 (the pad makes skill moves with the right stick): set for one poll per press
    // One-shot presses: true only on the tick the button went down.
    public bool OverlayToggle;     // right stick click
    public bool NextSpot, PrevSpot; // D-pad right / left
    public bool NextAttacker, NextKeeper; // D-pad up / down
    public bool Confirm;           // A / Cross
    public bool Retry;             // Y / Triangle
    public bool Flag;              // View / Share / Back
    public bool Pause;             // Menu / Options / Start
    public bool ControlsToggle;    // keyboard H: show or hide the controls panel

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
    /// <summary>
    /// A knock-on key press stays in the snapshot this long, seconds, so it is not lost when the ball is out of reach on the tick of
    /// the press. The core makes at most one knock-on from it: the ball then leaves reach.
    /// </summary>
    const float KnockOnHoldSeconds = 0.25f;
    int _knockOnLeft;

    public bool GamepadConnected => Input.GetConnectedJoypads().Count > 0;
    /// <summary>False in replay mode: the keyboard must not affect a replay.</summary>
    public bool KeyboardEnabled = true;
    /// <summary>Which device last produced a meaningful input event: "gamepad", "keyboard", or "none" until something is touched.</summary>
    public string Source { get; private set; } = "none";
    /// <summary>The device the person is using: <see cref="Source"/>, or the pad when one is connected and nothing was touched yet.</summary>
    public string Mode => Source == "none" && GamepadConnected ? "gamepad" : Source;
    public InputDevice Device => Mode == "gamepad" ? InputDevice.Gamepad : InputDevice.Keyboard;
    float _mouseTravel;
    /// <summary>Name of the first connected pad, or empty.</summary>
    public string ActivePadName
    {
        get
        {
            var ids = Input.GetConnectedJoypads();
            return ids.Count > 0 ? Input.GetJoyName((int)ids[0]) : "";
        }
    }

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
        Axis(Left, JoyAxis.LeftX, -1); Axis(Right, JoyAxis.LeftX, 1);
        Axis(Up, JoyAxis.LeftY, -1); Axis(Down, JoyAxis.LeftY, 1);
        Axis(SprintA, JoyAxis.TriggerRight, 1); Axis(ShieldA, JoyAxis.TriggerLeft, 1);
        Axis(RLeft, JoyAxis.RightX, -1); Axis(RRight, JoyAxis.RightX, 1);
        Axis(RUp, JoyAxis.RightY, -1); Axis(RDown, JoyAxis.RightY, 1);
        BindFaceButtons();
        BindFixedButtons();
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

    static bool IsFace(PadBind p) => p == Bindings.Pad.Shoot || p == Bindings.Pad.Confirm || p == Bindings.Pad.Retry;

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

    /// <summary>Call exactly once per _PhysicsProcess.</summary>
    public PadSnapshot Poll()
    {
        TrackPad();
        if (KeyboardEnabled && Pressed(Bindings.SwapFace)) ToggleSwap();
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
        m.Shield |= b.Shield; m.ShootHeld |= b.ShootHeld; m.Finesse |= b.Finesse; m.Chip |= b.Chip; m.Driven |= b.Driven; m.KnockOn |= b.KnockOn; m.Cancel |= b.Cancel;
        if (new Vector2(b.RightX, b.RightZ).LengthSquared() > new Vector2(a.RightX, a.RightZ).LengthSquared()) { m.RightX = b.RightX; m.RightZ = b.RightZ; }
        if (b.SkillKey != FootballSim.Core.SkillKey.None) m.SkillKey = b.SkillKey;
        m.OverlayToggle |= b.OverlayToggle; m.NextSpot |= b.NextSpot; m.PrevSpot |= b.PrevSpot;
        m.NextAttacker |= b.NextAttacker; m.NextKeeper |= b.NextKeeper;
        m.Confirm |= b.Confirm; m.Retry |= b.Retry; m.Flag |= b.Flag; m.Pause |= b.Pause; m.ControlsToggle |= b.ControlsToggle;
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
            SkillKey = SkillKeyPressed(),
            OverlayToggle = Pressed(Bindings.Overlay),
            NextSpot = Pressed(Bindings.NextSpot),
            PrevSpot = Pressed(Bindings.PrevSpot),
            NextAttacker = Pressed(Bindings.NextAttacker),
            NextKeeper = Pressed(Bindings.NextKeeper),
            Confirm = Pressed(Bindings.Confirm),
            Retry = Pressed(Bindings.Retry),
            Flag = Pressed(Bindings.Flag),
            Pause = Pressed(Bindings.Pause),
            ControlsToggle = Pressed(Bindings.Controls),
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
        float x = Input.GetActionRawStrength(Right) - Input.GetActionRawStrength(Left);
        float y = Input.GetActionRawStrength(Down) - Input.GetActionRawStrength(Up);
        var v = new Vector2(x, y);
        float len = v.Length();
        v = len < DeadZone ? Vector2.Zero : v / len * Mathf.Min(1f, (len - DeadZone) / (1f - DeadZone));
        // The right stick goes to the core raw (same axes and radial dead zone as the left); the core reads the gestures.
        var r = new Vector2(Input.GetActionRawStrength(RRight) - Input.GetActionRawStrength(RLeft),
            Input.GetActionRawStrength(RDown) - Input.GetActionRawStrength(RUp));
        float rlen = r.Length();
        r = rlen < DeadZone ? Vector2.Zero : r / rlen * Mathf.Min(1f, (rlen - DeadZone) / (1f - DeadZone));
        return new PadSnapshot
        {
            MoveX = v.X,
            MoveZ = v.Y,
            Sprint = Mathf.Clamp(Input.GetActionRawStrength(SprintA), 0f, 1f),
            Shield = Input.GetActionRawStrength(ShieldA) > 0.5f,
            ShootHeld = Input.IsActionPressed(Bindings.Pad.Shoot.Action),
            Finesse = Input.IsActionPressed(Bindings.Pad.Finesse.Action),
            Chip = Input.IsActionPressed(Bindings.Pad.Chip.Action),
            RightX = r.X,
            RightZ = r.Y,
            // A / Cross doubles as confirm, but the round flow reads confirm only in Setup and Report, never while live.
            Cancel = Input.IsActionPressed(Bindings.Pad.Confirm.Action),
            OverlayToggle = Input.IsActionJustPressed(Bindings.Pad.Overlay.Action),
            NextSpot = Input.IsActionJustPressed(Bindings.Pad.NextSpot.Action),
            PrevSpot = Input.IsActionJustPressed(Bindings.Pad.PrevSpot.Action),
            NextAttacker = Input.IsActionJustPressed(Bindings.Pad.NextAttacker.Action),
            NextKeeper = Input.IsActionJustPressed(Bindings.Pad.NextKeeper.Action),
            Confirm = Input.IsActionJustPressed(Bindings.Pad.Confirm.Action),
            Retry = Input.IsActionJustPressed(Bindings.Pad.Retry.Action),
            Flag = Input.IsActionJustPressed(Bindings.Pad.Flag.Action),
            Pause = Input.IsActionJustPressed(Bindings.Pad.Pause.Action),
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
