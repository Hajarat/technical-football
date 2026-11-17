using Godot;

namespace FootballSim.Game;

/// <summary>Static grey-box half pitch (X 0..52.5, Z -34..34) with run-off, markings and goal. Built in code.</summary>
public partial class Pitch : Node3D
{
    public const float GoalLineX = 52.5f;
    public const float HalfWidth = 34f;
    const float LineW = 0.12f, LineY = 0.015f;
    const float PostR = 0.06f, PostZ = 3.66f, BarY = 2.44f;

    public override void _Ready()
    {
        BuildGround();
        BuildMarkings();
        BuildGoal();
    }

    void BuildGround()
    {
        // Playing surface plus run-off: X -12..64.5, Z -40..40.
        AddBox("Ground", new Vector3(76.5f, 0.1f, 80f), new Vector3(26.25f, -0.05f, 0), MeshKit.Lit(new Color(0.24f, 0.52f, 0.22f)), shadows: false);
        // Wide darker surround so no void shows past the run-off.
        AddBox("Surround", new Vector3(500f, 0.1f, 500f), new Vector3(26.25f, -0.1f, 0), MeshKit.Lit(new Color(0.17f, 0.38f, 0.17f)), shadows: false);
    }

    void BuildMarkings()
    {
        var st = MeshKit.BeginTriangles();
        void Seg(float x0, float z0, float x1, float z1) => MeshKit.AddSegment(st, new(x0, z0), new(x1, z1), LineW, LineY);
        void Poly(System.Collections.Generic.List<Vector2> pts)
        {
            for (int i = 0; i < pts.Count - 1; i++) MeshKit.AddSegment(st, pts[i], pts[i + 1], LineW, LineY);
        }

        // Boundary: halfway line, touchlines, goal line.
        Seg(0, -HalfWidth, 0, HalfWidth);
        Seg(0, -HalfWidth, GoalLineX, -HalfWidth);
        Seg(0, HalfWidth, GoalLineX, HalfWidth);
        Seg(GoalLineX, -HalfWidth, GoalLineX, HalfWidth);

        // Penalty area: 16.5 deep, 40.32 wide. Six-yard box: 5.5 deep, 18.32 wide.
        float paX = GoalLineX - 16.5f, paZ = 40.32f / 2;
        Seg(GoalLineX, -paZ, paX, -paZ); Seg(paX, -paZ, paX, paZ); Seg(paX, paZ, GoalLineX, paZ);
        float sbX = GoalLineX - 5.5f, sbZ = 18.32f / 2;
        Seg(GoalLineX, -sbZ, sbX, -sbZ); Seg(sbX, -sbZ, sbX, sbZ); Seg(sbX, sbZ, GoalLineX, sbZ);

        // Half centre circle (radius 9.15) on the attacked side of the halfway line.
        var cc = new System.Collections.Generic.List<Vector2>();
        for (int i = 0; i <= 40; i++)
        {
            float a = Mathf.Lerp(-Mathf.Pi / 2, Mathf.Pi / 2, i / 40f);
            cc.Add(new(9.15f * Mathf.Cos(a), 9.15f * Mathf.Sin(a)));
        }
        Poly(cc);

        // Penalty arc: radius 9.15 around the spot (11 m), only the part outside the area.
        float spotX = GoalLineX - 11f;
        float a0 = Mathf.Acos((paX - spotX) / 9.15f);
        var arc = new System.Collections.Generic.List<Vector2>();
        for (int i = 0; i <= 24; i++)
        {
            float a = Mathf.Lerp(a0, Mathf.Tau - a0, i / 24f);
            arc.Add(new(spotX + 9.15f * Mathf.Cos(a), 9.15f * Mathf.Sin(a)));
        }
        Poly(arc);

        AddMesh("Markings", st.Commit(), Vector3.Zero, MeshKit.Unshaded(Colors.White));

        var spot = new MeshInstance3D { Name = "PenaltySpot", Mesh = MeshKit.Ring(0f, 0.22f, 16), Position = new(spotX, LineY, 0), MaterialOverride = MeshKit.Unshaded(Colors.White), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(spot);
    }

    void BuildGoal()
    {
        var white = MeshKit.Lit(Colors.White);
        // Posts.
        foreach (float s in new[] { -1f, 1f })
            AddCylinder($"Post{(s < 0 ? "Left" : "Right")}", PostR, BarY, new(GoalLineX, BarY / 2, s * PostZ), Basis.Identity, white);
        // Crossbar, along Z.
        AddCylinder("Crossbar", PostR, PostZ * 2 + PostR * 2, new(GoalLineX, BarY, 0), Basis.FromEuler(new(Mathf.Pi / 2, 0, 0)), white);

        // Translucent net volume behind the goal line, about 2 m deep.
        var net = new StandardMaterial3D
        {
            AlbedoColor = new Color(1, 1, 1, 0.22f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        AddBox("Net", new Vector3(2f, BarY, PostZ * 2), new(GoalLineX + 1f, BarY / 2, 0), net, shadows: false);
    }

    void AddCylinder(string name, float r, float h, Vector3 pos, Basis basis, Material mat)
    {
        var mi = new MeshInstance3D { Name = name, Mesh = new CylinderMesh { TopRadius = r, BottomRadius = r, Height = h, RadialSegments = 12, Rings = 1 }, MaterialOverride = mat };
        mi.Transform = new Transform3D(basis, pos);
        AddChild(mi);
    }

    void AddBox(string name, Vector3 size, Vector3 pos, Material mat, bool shadows)
    {
        var mi = new MeshInstance3D { Name = name, Mesh = new BoxMesh { Size = size }, Position = pos, MaterialOverride = mat };
        if (!shadows) mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        AddChild(mi);
    }

    void AddMesh(string name, Mesh mesh, Vector3 pos, Material mat)
    {
        AddChild(new MeshInstance3D { Name = name, Mesh = mesh, Position = pos, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
    }
}
