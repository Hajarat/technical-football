using System.Collections.Generic;
using System.Globalization;
using FootballSim.Core;
using Godot;

namespace FootballSim.Game;

/// <summary>What the capture panel asks of <see cref="Match"/> after a tick.</summary>
public enum CaptureChoice { None, Save, SaveNoNote }

/// <summary>
/// The capture panel over the frozen game: one of <see cref="CaptureTags.All"/> picked with ↑↓ and a note typed on the keyboard.
/// There is no cancel: A (Enter) saves with the note, B (Esc) saves without it. Driven one physics tick at a time by
/// <see cref="Step"/>; only the note's characters are read here, from key events, and never from the pad.
/// </summary>
public partial class CapturePanel : PanelContainer
{
    public const int NoteMax = 200;
    const int FontSize = 16;
    static readonly Color Dim = new(0.65f, 0.65f, 0.65f), Selected = new(1f, 0.95f, 0.55f), NoteColour = new(0.6f, 0.9f, 1f);

    readonly List<Label> _tagLabels = new();
    Label _title = null!, _detail = null!, _note = null!, _count = null!, _hint = null!;
    string _hintKey = "";

    public bool IsOpen { get; private set; }
    public int TagIndex { get; private set; }
    public string Tag => CaptureTags.All[TagIndex];
    /// <summary>The note typed so far (at most <see cref="NoteMax"/> characters).</summary>
    public string Note { get; private set; } = "";

    public override void _Ready()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Ignore;
        var style = new StyleBoxFlat { BgColor = new Color(0.02f, 0.03f, 0.05f, 0.92f), CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10, CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10 };
        style.SetContentMarginAll(18);
        AddThemeStyleboxOverride("panel", style);
        AnchorLeft = AnchorRight = 0.5f; AnchorTop = AnchorBottom = 0.5f;
        OffsetLeft = -320; OffsetRight = 320; OffsetTop = -200; OffsetBottom = 200;
        GrowHorizontal = GrowDirection.Both; GrowVertical = GrowDirection.Both;

        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 6);
        AddChild(box);
        _title = Make("Capture", 22, Colors.White);
        box.AddChild(_title);
        _detail = Make("", FontSize - 1, Dim);
        _detail.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_detail);

        var tags = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        tags.AddThemeConstantOverride("separation", 1);
        foreach (var _ in CaptureTags.All)
        {
            var l = Make("", FontSize, Colors.White);
            tags.AddChild(l);
            _tagLabels.Add(l);
        }
        box.AddChild(tags);

        var noteHead = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        var head = Make("Note (keyboard)", FontSize, Colors.White);
        head.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        noteHead.AddChild(head);
        _count = Make("", FontSize - 2, Dim);
        noteHead.AddChild(_count);
        box.AddChild(noteHead);
        _note = Make("", FontSize, NoteColour);
        _note.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
        _note.CustomMinimumSize = new Vector2(0, 48);
        box.AddChild(_note);
        _hint = Make("", FontSize - 2, Dim);
        _hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_hint);
    }

    /// <summary>Opens with the first tag selected and an empty note. <paramref name="title"/> and <paramref name="detail"/> say what is captured.</summary>
    public void Open(string title, string detail)
    {
        TagIndex = 0;
        Note = "";
        _title.Text = title;
        _detail.Text = detail;
        IsOpen = true;
        Visible = true;
        Refresh();
    }

    public void Close()
    {
        IsOpen = false;
        Visible = false;
    }

    /// <summary>Shows the hint line for the device in use (rebuilt only when it changes).</summary>
    public void SetDevice(InputDevice device, PadKind kind)
    {
        string key = $"{device}/{kind}";
        if (key == _hintKey) return;
        _hintKey = key;
        _hint.Text = device == InputDevice.Gamepad
            ? $"D-pad ↑↓ tag   {Bindings.Pad.Confirm.Label(kind)} save   {Bindings.Pad.Shoot.Label(kind)} save without the note   (type the note on the keyboard)"
            : $"{Bindings.CaptureKeys.Up.Label}{Bindings.CaptureKeys.Down.Label} tag   type a note   {Bindings.CaptureKeys.Save.Label} save   {Bindings.CaptureKeys.SaveNoNote.Label} save without the note";
    }

    /// <summary>One physics tick of panel input.</summary>
    public CaptureChoice Step(in CaptureInput input)
    {
        if (!IsOpen) return CaptureChoice.None;
        if (input.Save) return CaptureChoice.Save;
        if (input.SaveNoNote) return CaptureChoice.SaveNoNote;
        int n = CaptureTags.All.Count;
        int dir = (input.Down ? 1 : 0) - (input.Up ? 1 : 0);
        if (dir != 0) { TagIndex = ((TagIndex + dir) % n + n) % n; Refresh(); }
        return CaptureChoice.None;
    }

    /// <summary>The note's typing: printable characters append (up to <see cref="NoteMax"/>), Backspace removes the last one.</summary>
    public override void _Input(InputEvent e)
    {
        if (!IsOpen || e is not InputEventKey { Pressed: true } k) return;
        if (k.Keycode == Key.Backspace)
        {
            if (Note.Length > 0)
            {
                // a whole character: a surrogate pair or a combining sequence goes in one press
                var info = new StringInfo(Note);
                Note = info.LengthInTextElements > 1 ? info.SubstringByTextElements(0, info.LengthInTextElements - 1) : "";
                Refresh();
            }
            GetViewport().SetInputAsHandled();
            return;
        }
        if (k.Echo || k.CtrlPressed || k.MetaPressed) return;
        long code = k.Unicode;
        if (code < 32 || code == 127 || code is >= 0xD800 and <= 0xDFFF || code > 0x10FFFF) return;
        string ch = char.ConvertFromUtf32((int)code);
        if (Note.Length + ch.Length > NoteMax) return;
        Note += ch;
        Refresh();
        GetViewport().SetInputAsHandled();
    }

    void Refresh()
    {
        for (int i = 0; i < _tagLabels.Count; i++)
        {
            bool sel = i == TagIndex;
            _tagLabels[i].Text = $"{(sel ? "▸ " : "   ")}{CaptureTags.All[i]}";
            _tagLabels[i].LabelSettings.FontColor = sel ? Selected : Colors.White;
        }
        _note.Text = Note + "▏";
        _count.Text = $"{Note.Length}/{NoteMax}";
    }

    static Label Make(string text, int size, Color colour) => new()
    {
        Text = text,
        MouseFilter = MouseFilterEnum.Ignore,
        LabelSettings = new LabelSettings { FontSize = size, FontColor = colour, OutlineSize = Mathf.Max(2, size / 8), OutlineColor = new Color(0, 0, 0, 0.9f) },
    };
}
