using Godot;

namespace FootballSim.Game;

/// <summary>
/// Optional debug drawing: a ground wedge from a point between two directions, an aimed and a landed marker
/// on the goal plane, and a keeper reach circle. Hidden unless Enabled. Setters take plain values.
/// </summary>
public partial class DebugOverlay : Node3D
{
    ImmediateMesh _wedgeMesh = new();
    MeshInstance3D _wedge = null!, _aimed = null!, _landed = null!, _reach = null!;

    public bool Enabled
    {
        get => Visible;
        set => Visible = value;
    }

    public override void _Ready()
    {
        _wedge = new MeshInstance3D { Name = "Wedge", Mesh = _wedgeMesh, MaterialOverride = MeshKit.Unshaded(new Color(1f, 0.55f, 0.1f, 0.3f)), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_wedge);
        _aimed = Marker("Aimed", new Color(0.1f, 0.9f, 1f));
        _landed = Marker("Landed", new Color(1f, 0.2f, 0.9f));
        _reach = new MeshInstance3D { Name = "KeeperReach", Visible = false, MaterialOverride = MeshKit.Unshaded(new Color(0.2f, 1f, 0.3f, 0.85f)), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_reach);
        Visible = false;
    }

    /// <summary>Translucent ground wedge from origin between two directions (only X and Z are used) out to length metres.</summary>
    public void SetWedge(Vector3 origin, Vector3 dirA, Vector3 dirB, float length)
    {
        _wedgeMesh.ClearSurfaces();
        float a0 = Mathf.Atan2(dirA.Z, dirA.X);
        float delta = Mathf.AngleDifference(a0, Mathf.Atan2(dirB.Z, dirB.X));
        const int steps = 16;
        var o = new Vector3(origin.X, 0.04f, origin.Z);
        _wedgeMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        for (int i = 0; i < steps; i++)
        {
            float t0 = a0 + delta * i / steps, t1 = a0 + delta * (i + 1) / steps;
            _wedgeMesh.SurfaceAddVertex(o);
            _wedgeMesh.SurfaceAddVertex(o + new Vector3(Mathf.Cos(t0), 0, Mathf.Sin(t0)) * length);
            _wedgeMesh.SurfaceAddVertex(o + new Vector3(Mathf.Cos(t1), 0, Mathf.Sin(t1)) * length);
        }
        _wedgeMesh.SurfaceEnd();
    }

    public void ClearWedge() => _wedgeMesh.ClearSurfaces();

    /// <summary>Marker for the aimed point (goal plane, world metres).</summary>
    public void SetAimed(Vector3 point) => Place(_aimed, point);
    public void ClearAimed() => _aimed.Visible = false;

    /// <summary>Marker for where the ball actually crossed the goal plane.</summary>
    public void SetLanded(Vector3 point) => Place(_landed, point);
    public void ClearLanded() => _landed.Visible = false;

    /// <summary>Flat circle on the ground around the keeper, radius in metres.</summary>
    public void SetKeeperReach(Vector3 centre, float radius)
    {
        _reach.Mesh = MeshKit.Ring(Mathf.Max(0.01f, radius - 0.07f), radius + 0.07f, 64);
        _reach.Position = new Vector3(centre.X, 0.05f, centre.Z);
        _reach.Visible = true;
        _reach.ResetPhysicsInterpolation();
    }
    public void ClearKeeperReach() => _reach.Visible = false;

    MeshInstance3D Marker(string name, Color c)
    {
        var m = new MeshInstance3D { Name = name, Visible = false, Mesh = new SphereMesh { Radius = 0.3f, Height = 0.6f, RadialSegments = 12, Rings = 6 }, MaterialOverride = MeshKit.Unshaded(c, noDepthTest: true), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(m);
        return m;
    }

    static void Place(MeshInstance3D m, Vector3 p)
    {
        m.Position = p;
        m.Visible = true;
        m.ResetPhysicsInterpolation();
    }
}
