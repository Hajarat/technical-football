using Godot;

namespace FootballSim.Game;

/// <summary>
/// Sphere radius 0.11 m with a pentagon pattern that turns with the simulation's ball spin (integrated per rendered frame),
/// plus a ground ring so it stays findable from the broadcast camera.
/// </summary>
public partial class BallView : Node3D
{
    public const float Radius = 0.11f;
    MeshInstance3D _ring = null!, _sphere = null!;
    Vector3 _spin;
    readonly StandardMaterial3D _ringMat = MeshKit.Unshaded(LooseColor);
    public static readonly Color LooseColor = new(1f, 0.5f, 0.1f, 0.9f);

    public override void _Ready()
    {
        _sphere = new MeshInstance3D
        {
            Name = "Sphere", Mesh = new SphereMesh { Radius = Radius, Height = Radius * 2, RadialSegments = 32, Rings = 16 },
            MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://ball_pattern.gdshader") },
        };
        AddChild(_sphere);
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

    /// <summary>The simulation's ball spin (world angular velocity, rad/s); the mesh turns with it every rendered frame.</summary>
    public void SetSpin(Vector3 angularVelocity) => _spin = angularVelocity;

    public override void _Process(double delta)
    {
        float rate = _spin.Length();
        if (rate < 1e-3f) return;
        // the ball node itself is never rotated (physics interpolation and the ring stay as they are); only the mesh turns
        _sphere.Basis = (new Basis(_spin / rate, rate * (float)delta) * _sphere.Basis).Orthonormalized();
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
