using Godot;

namespace FootballSim.Game;

/// <summary>
/// Optional debug drawing: a ground wedge from a point between two directions, an aimed and a landed marker
/// on the goal plane, a keeper reach circle, and the lock-on: the loose ball's forecast path and a ring at each team's
/// meeting point. Hidden unless Enabled. Setters take plain values.
/// </summary>
public partial class DebugOverlay : Node3D
{
    ImmediateMesh _wedgeMesh = new(), _pathMesh = new();
    MeshInstance3D _wedge = null!, _aimed = null!, _landed = null!, _reach = null!, _path = null!;
    readonly MeshInstance3D[] _meet = new MeshInstance3D[2];

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
        _path = new MeshInstance3D { Name = "ForecastPath", Mesh = _pathMesh, MaterialOverride = MeshKit.Unshaded(new Color(1f, 1f, 1f, 0.8f)), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_path);
        for (int i = 0; i < 2; i++)
        {
            _meet[i] = new MeshInstance3D
            {
                Name = "Meeting" + i, Visible = false, Mesh = MeshKit.Ring(0.42f, 0.5f, 40),
                MaterialOverride = MeshKit.Unshaded(i == 0 ? new Color(1f, 0.85f, 0.1f, 0.95f) : new Color(1f, 0.3f, 0.25f, 0.95f)),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(_meet[i]);
        }
        Visible = false;
    }

    /// <summary>Translucent ground wedge from origin between two directions (only X and Z are used) out to length metres.</summary>
    public void SetWedge(Vector3 origin, Vector3 dirA, Vector3 dirB, float length)
    {
        // (the same wedge as last time is left as it is rather than built again)
        if (_wedgeSet && origin == _wOrigin && dirA == _wDirA && dirB == _wDirB && length == _wLength) return;
        _wedgeSet = true; _wOrigin = origin; _wDirA = dirA; _wDirB = dirB; _wLength = length;
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

    public void ClearWedge()
    {
        if (!_wedgeSet && _wedgeCleared) return;
        _wedgeSet = false; _wedgeCleared = true;
        _wedgeMesh.ClearSurfaces();
    }
    bool _wedgeSet, _wedgeCleared;
    Vector3 _wOrigin, _wDirA, _wDirB;
    float _wLength;

    /// <summary>Marker for the aimed point (goal plane, world metres).</summary>
    public void SetAimed(Vector3 point) => Place(_aimed, point);
    public void ClearAimed() => _aimed.Visible = false;

    /// <summary>Marker for where the ball actually crossed the goal plane.</summary>
    public void SetLanded(Vector3 point) => Place(_landed, point);
    public void ClearLanded() => _landed.Visible = false;

    /// <summary>Flat circle on the ground around the keeper, radius in metres.</summary>
    public void SetKeeperReach(Vector3 centre, float radius)
    {
        // the ring's mesh is built only when the radius changes, not every tick
        if (_reach.Mesh == null || radius != _reachRadius)
        {
            _reach.Mesh = MeshKit.Ring(Mathf.Max(0.01f, radius - 0.07f), radius + 0.07f, 64);
            _reachRadius = radius;
        }
        _reach.Position = new Vector3(centre.X, 0.05f, centre.Z);
        _reach.Visible = true;
        _reach.ResetPhysicsInterpolation();
    }
    public void ClearKeeperReach() => _reach.Visible = false;
    float _reachRadius;

    /// <summary>The loose ball's forecast path: a dotted line through the given ball centres (one per few ticks), on the ground or in the air.</summary>
    public void SetForecastPath(System.Collections.Generic.IReadOnlyList<Vector3> points)
    {
        _pathMesh.ClearSurfaces();
        if (points.Count < 2) return;
        _pathMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        for (int i = 0; i + 1 < points.Count; i += 2)
        {
            _pathMesh.SurfaceAddVertex(new Vector3(points[i].X, Mathf.Max(0.05f, points[i].Y), points[i].Z));
            _pathMesh.SurfaceAddVertex(new Vector3(points[i + 1].X, Mathf.Max(0.05f, points[i + 1].Y), points[i + 1].Z));
        }
        _pathMesh.SurfaceEnd();
    }

    /// <summary>A ring on the ground at a team's meeting point with the loose ball (team 0 yellow, team 1 red).</summary>
    public void SetMeeting(int team, Vector3 point)
    {
        var m = _meet[team];
        m.Position = new Vector3(point.X, 0.05f, point.Z);
        m.Visible = true;
        m.ResetPhysicsInterpolation();
    }

    public void ClearMeeting(int team) => _meet[team].Visible = false;

    /// <summary>Nothing of the lock-on is drawn (no loose ball to claim).</summary>
    public void ClearLockOn()
    {
        _pathMesh.ClearSurfaces();
        _meet[0].Visible = false; _meet[1].Visible = false;
    }

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
