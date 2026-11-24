using Godot;

namespace FootballSim.Game;

/// <summary>
/// Full controls list for the active device, read from <see cref="Bindings"/>. Two columns of groups so it fits 1280x720
/// (the window scales it). Rebuilt only when the device, pad kind, face-swap note or aim assistance level changes.
/// </summary>
public partial class ControlsPanel : PanelContainer
{
    const int FontSize = 15;
    static readonly Color Heading = new(1f, 0.85f, 0.3f), Dim = new(0.65f, 0.65f, 0.65f), Key = new(0.6f, 0.9f, 1f);

    bool _isBuilt, _bSwapped;
    FootballSim.Core.AimAssistLevel _bAim;
    InputDevice _bDevice;
    PadKind _bKind;
    string _bPad = "";
    HBoxContainer _columns = null!;
    Label _title = null!, _foot = null!;

    public override void _Ready()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Ignore;
        var style = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.82f), CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8 };
        style.SetContentMarginAll(12);
        AddThemeStyleboxOverride("panel", style);
        AnchorLeft = 0; AnchorRight = 1; AnchorTop = 0; AnchorBottom = 0;
        OffsetLeft = 20; OffsetRight = -20; OffsetTop = 96;
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 4);
        AddChild(box);
        _title = Make("", 20, Colors.White);
        box.AddChild(_title);
        _columns = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _columns.AddThemeConstantOverride("separation", 28);
        box.AddChild(_columns);
        _foot = Make("", FontSize - 1, Dim);
        box.AddChild(_foot);
    }

    /// <summary>Shows the list for a device. <paramref name="padName"/> is the pad's name when known (it is shown in the title).</summary>
    /// <param name="aim">The aim assistance level in use, shown in its row.</param>
    public void Open(InputDevice device, PadKind kind, string padName, bool swapped, FootballSim.Core.AimAssistLevel aim)
    {
        // (rebuilt only when what it shows changes; compared field by field so an open panel makes no garbage every tick)
        if (!_isBuilt || device != _bDevice || kind != _bKind || padName != _bPad || swapped != _bSwapped || aim != _bAim)
        {
            _isBuilt = true; _bDevice = device; _bKind = kind; _bPad = padName; _bSwapped = swapped; _bAim = aim;
            Build(device, kind, padName, swapped, aim);
        }
        Visible = true;
    }

    public void Close() => Visible = false;

    void Build(InputDevice device, PadKind kind, string padName, bool swapped, FootballSim.Core.AimAssistLevel aim)
    {
        bool pad = device == InputDevice.Gamepad;
        string kindName = kind switch { PadKind.PlayStation => "PlayStation", PadKind.Nintendo => "Nintendo", _ => "Xbox" };
        _title.Text = pad ? $"Controls: gamepad ({kindName} buttons{(padName.Length > 0 ? ", " + padName : "")})" : "Controls: keyboard and mouse";
        _foot.Text = pad
            ? "Skill-move directions are against the running direction; \"→ or ←\" works to either side. Keyboard works alongside the pad."
              + (swapped ? " Face buttons are swapped for this pad (keyboard B toggles)." : "")
            : "Skill-move keys use the side held on A / D (right by default). Also works alongside a gamepad. Shoot = Left mouse or Space.";
        foreach (var c in _columns.GetChildren()) c.QueueFree();
        var groups = Bindings.Groups(kind, aim);
        var left = Column(); var right = Column();
        // movement, shooting and the player editor on the left; skill moves and round / menu on the right
        for (int i = 0; i < groups.Count; i++) AddGroup(i < 2 || i >= 4 ? left : right, groups[i], pad);
        _columns.AddChild(left); _columns.AddChild(right);
    }

    VBoxContainer Column()
    {
        var c = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        c.AddThemeConstantOverride("separation", 0);
        return c;
    }

    void AddGroup(VBoxContainer col, Bindings.Group g, bool pad)
    {
        var h = Make(g.Title.ToUpperInvariant(), FontSize + 1, Heading);
        col.AddChild(new Control { CustomMinimumSize = new Vector2(0, 5), MouseFilter = MouseFilterEnum.Ignore });
        col.AddChild(h);
        var grid = new GridContainer { Columns = 2, MouseFilter = MouseFilterEnum.Ignore };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 0);
        foreach (var r in g.Rows)
        {
            string text = pad ? r.Pad : r.Keyboard;
            var name = Make(r.Action + (r.NotBuilt ? " (not built yet)" : ""), FontSize, r.NotBuilt ? Dim : Colors.White);
            name.CustomMinimumSize = new Vector2(190, 0);
            var binding = Make(text, FontSize, r.NotBuilt ? Dim : Key);
            binding.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            binding.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            binding.CustomMinimumSize = new Vector2(150, 0);
            grid.AddChild(name); grid.AddChild(binding);
        }
        col.AddChild(grid);
    }

    static Label Make(string text, int size, Color colour) => new()
    {
        Text = text,
        MouseFilter = MouseFilterEnum.Ignore,
        LabelSettings = new LabelSettings { FontSize = size, FontColor = colour, OutlineSize = Mathf.Max(2, size / 8), OutlineColor = new Color(0, 0, 0, 0.9f) },
    };
}
