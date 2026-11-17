using System.Globalization;
using Godot;

namespace FootballSim.Game;

/// <summary>2D HUD: power bar, clock, spot line, report card, gamepad notice, warning line. Plain setters only.</summary>
public partial class Hud : CanvasLayer
{
    ProgressBar _power = null!;
    ColorRect _ideal = null!;
    Label _shotType = null!;
    float _shotTypeLeft;
    const float PowerBarWidth = 400f;
    Label _clock = null!, _spot = null!, _notice = null!, _warning = null!, _title = null!, _body = null!, _toast = null!;
    float _toastLeft;
    PanelContainer _report = null!;

    public override void _Ready()
    {
        var root = new Control { Name = "Root", MouseFilter = Control.MouseFilterEnum.Ignore };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        _spot = MakeLabel(root, 17, HorizontalAlignment.Left);
        _spot.Position = new Vector2(20, 14);

        _clock = MakeLabel(root, 36, HorizontalAlignment.Center);
        TopWide(_clock, 10, 56);
        _clock.Text = "0.0 s";

        _notice = MakeLabel(root, 18, HorizontalAlignment.Center);
        TopWide(_notice, 60, 120);
        _notice.OffsetLeft = 150; _notice.OffsetRight = -150;
        _notice.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _notice.LabelSettings!.FontColor = new Color(1f, 0.85f, 0.3f);
        _notice.Visible = false;

        var powerLabel = MakeLabel(root, 16, HorizontalAlignment.Center);
        BottomCentre(powerLabel, -74, -52, 400);
        powerLabel.Text = "POWER";
        _power = new ProgressBar { MinValue = 0, MaxValue = 1, Value = 0, ShowPercentage = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        BottomCentre(_power, -48, -26, 400);
        root.AddChild(_power);
        // the ideal-charge marker: a thin bright tick across the bar, so the player learns where to release
        _ideal = new ColorRect { Color = new Color(1f, 0.9f, 0.2f), Size = new Vector2(4, 30), Position = new Vector2(0, -4), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _power.AddChild(_ideal);
        _shotType = MakeLabel(root, 30, HorizontalAlignment.Center);
        BottomCentre(_shotType, -160, -120, 400);
        _shotType.Visible = false;

        _warning = MakeLabel(root, 18, HorizontalAlignment.Left);
        _warning.AnchorTop = _warning.AnchorBottom = 1;
        _warning.OffsetLeft = 20; _warning.OffsetTop = -36; _warning.OffsetBottom = -10; _warning.OffsetRight = 1260;
        _warning.LabelSettings!.FontColor = new Color(1f, 0.55f, 0.3f);
        _warning.Visible = false;

        _toast = MakeLabel(root, 20, HorizontalAlignment.Right);
        _toast.AnchorLeft = _toast.AnchorRight = 1; _toast.AnchorTop = _toast.AnchorBottom = 1;
        _toast.OffsetLeft = -700; _toast.OffsetRight = -20; _toast.OffsetTop = -36; _toast.OffsetBottom = -10;
        _toast.LabelSettings!.FontColor = new Color(0.6f, 1f, 0.6f);
        _toast.Visible = false;

        _report = new PanelContainer { Name = "ReportCard", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        var style = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.7f), CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8 };
        style.SetContentMarginAll(14);
        _report.AddThemeStyleboxOverride("panel", style);
        _report.AnchorTop = _report.AnchorBottom = 0.5f;
        _report.OffsetLeft = 20; _report.OffsetRight = 400; _report.OffsetTop = -110; _report.OffsetBottom = 110;
        _report.GrowVertical = Control.GrowDirection.Both;
        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _report.AddChild(box);
        _title = MakeLabel(null, 40, HorizontalAlignment.Left);
        _body = MakeLabel(null, 17, HorizontalAlignment.Left);
        _body.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_title); box.AddChild(_body);
        root.AddChild(_report);
    }

    public override void _Process(double delta)
    {
        if (_toastLeft > 0f && (_toastLeft -= (float)delta) <= 0f) _toast.Visible = false;
        if (_shotTypeLeft > 0f && (_shotTypeLeft -= (float)delta) <= 0f) _shotType.Visible = false;
    }

    /// <summary>A short confirmation in the bottom-right corner, hidden after the given seconds.</summary>
    public void Toast(string text, float seconds = 3f)
    {
        _toast.Text = text;
        _toast.Visible = true;
        _toastLeft = seconds;
    }

    /// <summary>Shot power, 0..1.</summary>
    public void SetPower(float value) => _power.Value = Mathf.Clamp(value, 0f, 1f);

    /// <summary>Marks the ideal charge (0..1) on the power bar, or hides the marker.</summary>
    public void SetIdealMarker(float ideal, bool visible)
    {
        _ideal.Visible = visible;
        _ideal.Position = new Vector2(Mathf.Clamp(ideal, 0f, 1f) * (PowerBarWidth - _ideal.Size.X), _ideal.Position.Y);
    }

    /// <summary>Names the shot type (for example "DRIVEN") above the power bar for a moment.</summary>
    public void ShowShotType(string name, float seconds = 0.9f)
    {
        _shotType.Text = name;
        _shotType.Visible = true;
        _shotTypeLeft = seconds;
    }

    /// <summary>Clock in seconds, shown as "11.4 s".</summary>
    public void SetClock(float seconds) => _clock.Text = seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    /// <summary>Line naming the start spot and attacker.</summary>
    public void SetSpotLine(string text) => _spot.Text = text;

    /// <summary>Show the outcome card, e.g. title "GOAL" with a few lines.</summary>
    public void ShowReport(string title, params string[] lines)
    {
        _title.Text = title;
        _body.Text = string.Join("\n", lines);
        _report.Visible = true;
    }

    public void HideReport() => _report.Visible = false;

    /// <summary>Shows or hides the "Connect a gamepad to play" notice.</summary>
    public void SetGamepadNotice(bool visible) => SetNotice(visible ? "Connect a gamepad to play" : "");

    /// <summary>Centred notice under the clock (for example "Paused"); empty string hides it.</summary>
    public void SetNotice(string text)
    {
        _notice.Text = text;
        _notice.Visible = text.Length > 0;
    }

    /// <summary>Small warning line; empty string hides it.</summary>
    public void SetWarning(string text)
    {
        _warning.Text = text;
        _warning.Visible = text.Length > 0;
    }

    static Label MakeLabel(Control? parent, int size, HorizontalAlignment align)
    {
        var l = new Label
        {
            HorizontalAlignment = align,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            LabelSettings = new LabelSettings { FontSize = size, OutlineSize = Mathf.Max(3, size / 6), OutlineColor = new Color(0, 0, 0, 0.9f), FontColor = Colors.White },
        };
        parent?.AddChild(l);
        return l;
    }

    static void TopWide(Control c, float top, float bottom)
    {
        c.AnchorLeft = 0; c.AnchorRight = 1; c.AnchorTop = c.AnchorBottom = 0;
        c.OffsetLeft = 0; c.OffsetRight = 0; c.OffsetTop = top; c.OffsetBottom = bottom;
    }

    static void BottomCentre(Control c, float top, float bottom, float width)
    {
        c.AnchorLeft = c.AnchorRight = 0.5f; c.AnchorTop = c.AnchorBottom = 1;
        c.OffsetLeft = -width / 2; c.OffsetRight = width / 2; c.OffsetTop = top; c.OffsetBottom = bottom;
    }
}
