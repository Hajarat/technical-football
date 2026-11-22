using Godot;

namespace FootballSim.Game;

/// <summary>Sphere radius 0.11 m plus a ground ring so it stays findable from the broadcast camera.</summary>
public partial class BallView : Node3D
{
    public const float Radius = 0.11f;
    MeshInstance3D _ring = null!;
    readonly StandardMaterial3D _ringMat = MeshKit.Unshaded(LooseColor);
    public static readonly Color LooseColor = new(1f, 0.5f, 0.1f, 0.9f);

    public override void _Ready()
    {
        AddChild(new MeshInstance3D { Name = "Sphere", Mesh = new SphereMesh { Radius = Radius, Height = Radius * 2, RadialSegments = 16, Rings = 8 }, MaterialOverride = MeshKit.Lit(Colors.White) });
        _ring = new MeshInstance3D
        {
            Name = "GroundRing",
            Mesh = MeshKit.Ring(0.5f, 0.68f),
            MaterialOverride = _ringMat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_ring);
    }

    /// <summary>Position of the ball centre in world metres. Call from _PhysicsProcess only.</summary>
    public void Apply(Vector3 position)
    {
        Position = position;
        _ring.Position = new Vector3(0, 0.03f - position.Y, 0);
    }

    /// <summary>Ground ring colour: green while a player is in possession, orange while the ball is loose.</summary>
    public void SetPossession(bool inPossession) => _ringMat.AlbedoColor = inPossession ? PlayerView.PossessionColor : LooseColor;

    /// <summary>Move without interpolating from the old position (round reset).</summary>
    public void Teleport(Vector3 position)
    {
        Apply(position);
        ResetPhysicsInterpolation();
    }
}
