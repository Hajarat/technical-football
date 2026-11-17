using Godot;

namespace FootballSim.Game;

/// <summary>
/// An outfield player: a jointed figure plus a possession ring on the ground (solid and bright while in possession,
/// thin and dim otherwise). The view sits at the origin; the figure places its own joints in world space.
/// </summary>
public partial class PlayerView : Node3D
{
    public const float Height = 1.8f, Radius = 0.25f;
    public static readonly Color PossessionColor = new(0.25f, 1f, 0.35f, 0.95f);

    readonly PlayerFigure _figure = new() { Name = "Figure" };
    MeshInstance3D _ringOn = null!, _ringOff = null!;

    public PlayerFigure Figure => _figure;

    public override void _Ready()
    {
        AddChild(_figure);
        _ringOn = Ring("RingInPossession", MeshKit.Ring(0.50f, 0.82f), PossessionColor);
        _ringOff = Ring("RingOff", MeshKit.Ring(0.74f, 0.78f), new Color(0.75f, 0.75f, 0.75f, 0.35f));
        _ringOn.Visible = false;
    }

    MeshInstance3D Ring(string name, Mesh mesh, Color c)
    {
        var mi = new MeshInstance3D { Name = name, Mesh = mesh, MaterialOverride = MeshKit.Unshaded(c), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(mi);
        return mi;
    }

    /// <summary>Shirt colour; shorts are white and the socks match the shirt.</summary>
    public void SetTeamColor(Color color) => _figure.SetColors(color, new Color(0.95f, 0.95f, 0.95f), color);

    /// <summary>Plain pose: feet position and facing (0 = +X, positive toward +Z). Call from _PhysicsProcess only.</summary>
    public void Apply(Vector3 position, float facing) => Apply(FigureFrame.At(position, facing), false);

    /// <summary>Full per-tick input for the figure, and whether the player is in possession. Call from _PhysicsProcess only.</summary>
    public void Apply(in FigureFrame frame, bool inPossession)
    {
        _figure.Update(frame);
        SetRing(frame.Position, inPossession);
    }

    public void Teleport(Vector3 position, float facing) => Teleport(FigureFrame.At(position, facing), false);

    public void Teleport(in FigureFrame frame, bool inPossession)
    {
        _figure.Teleport(frame);
        SetRing(frame.Position, inPossession);
        _ringOn.ResetPhysicsInterpolation();
        _ringOff.ResetPhysicsInterpolation();
    }

    void SetRing(Vector3 pos, bool inPossession)
    {
        _ringOn.Visible = inPossession;
        _ringOff.Visible = !inPossession;
        var p = new Vector3(pos.X, 0.03f, pos.Z);
        _ringOn.Position = p;
        _ringOff.Position = p;
    }
}
