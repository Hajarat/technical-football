using System.Collections.Generic;
using Godot;

namespace FootballSim.Game;

/// <summary>
/// The walled arena, built in code from the simulation's arena: grass inside the outline, low boards all round (either side of
/// the goal too), a few markings and the goal. Outside the boards a plain dark floor. The back corners are rounded (drawn as
/// short straight pieces) and the goal-end corners cut by straight boards, matching the simulation's shape.
/// </summary>
public partial class Pitch : Node3D
{
    const float LineW = 0.12f, LineY = 0.015f;
    const float BoardThickness = 0.15f;
    /// <summary>How far inside the boards the outline marking runs, metres.</summary>
    const float OutlineInset = 0.3f;
    const int ArcSegments = 12;

    FootballSim.Core.Pitch? _built;

    /// <summary>
    /// Builds (or rebuilds) the arena for the simulation's pitch. Called whenever a world is created; does nothing if the
    /// arena has the same shape as the one already built.
    /// </summary>
    public void Build(FootballSim.Core.Pitch p)
    {
        if (_built != null && SameShape(_built, p)) return;
        _built = p;
        foreach (var c in GetChildren()) { RemoveChild(c); c.QueueFree(); }
        BuildGround(p);
        BuildBoards(p);
        BuildMarkings(p);
        BuildGoal(p);
    }

    static bool SameShape(FootballSim.Core.Pitch a, FootballSim.Core.Pitch b) =>
        a.GoalLineX == b.GoalLineX && a.HalfWidth == b.HalfWidth && a.CornerRadius == b.CornerRadius && a.GoalChamfer == b.GoalChamfer
        && a.BoardHeight == b.BoardHeight && a.GoalHalfWidth == b.GoalHalfWidth && a.BarHeight == b.BarHeight && a.PostRadius == b.PostRadius
        && a.PenaltyAreaDepth == b.PenaltyAreaDepth && a.PenaltyAreaHalfWidth == b.PenaltyAreaHalfWidth;

    /// <summary>
    /// The outline, moved <paramref name="inset"/> metres inward (negative = outward), as a closed loop of (x, z) points:
    /// goal-end wall, +Z chamfer, +Z side, rounded back corner, back wall, rounded back corner, -Z side, -Z chamfer.
    /// </summary>
    static List<Vector2> Outline(FootballSim.Core.Pitch p, float inset)
    {
        float L = p.GoalLineX, h = p.HalfWidth, R = p.CornerRadius, C = p.GoalChamfer;
        float cut = inset * (Mathf.Sqrt2 - 1f); // a chamfer corner moves along the wall by this when the outline is inset
        var pts = new List<Vector2>();
        void Arc(float cz, float a0, float a1)
        {
            float r = Mathf.Max(0f, R - inset);
            for (int i = 0; i <= ArcSegments; i++)
            {
                float a = Mathf.Lerp(a0, a1, i / (float)ArcSegments);
                pts.Add(new Vector2(R + r * Mathf.Cos(a), cz + r * Mathf.Sin(a)));
            }
        }
        pts.Add(new Vector2(L - inset, -(h - C - cut)));
        pts.Add(new Vector2(L - inset, h - C - cut));
        if (C > 0f) pts.Add(new Vector2(L - C - cut, h - inset));
        if (R > 0f) Arc(h - R, Mathf.Pi / 2f, Mathf.Pi);
        else { pts.Add(new Vector2(inset, h - inset)); }
        if (R > 0f) Arc(-(h - R), Mathf.Pi, 1.5f * Mathf.Pi);
        else { pts.Add(new Vector2(inset, -(h - inset))); }
        if (C > 0f) pts.Add(new Vector2(L - C - cut, -(h - inset)));
        return pts;
    }

    void BuildGround(FootballSim.Core.Pitch p)
    {
        // Grass: a fan over the convex outline, just above the surround floor.
        var grass = MeshKit.Lit(new Color(0.24f, 0.52f, 0.22f));
        grass.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        var st = MeshKit.BeginTriangles();
        var loop = Outline(p, 0f);
        var centre = new Vector3(p.GoalLineX / 2f, 0f, 0f);
        for (int i = 0; i < loop.Count; i++)
        {
            Vector2 a = loop[i], b = loop[(i + 1) % loop.Count];
            st.AddVertex(centre); st.AddVertex(new Vector3(a.X, 0f, a.Y)); st.AddVertex(new Vector3(b.X, 0f, b.Y));
        }
        AddMesh("Grass", st.Commit(), Vector3.Zero, grass);
        // Plain dark floor all round, so nothing past the boards draws attention.
        AddBox("Surround", new Vector3(400f, 0.1f, 400f), new Vector3(p.GoalLineX / 2f, -0.06f, 0), MeshKit.Lit(new Color(0.10f, 0.11f, 0.12f)), shadows: false);
    }

    void BuildBoards(FootballSim.Core.Pitch p)
    {
        var mat = MeshKit.Lit(new Color(0.86f, 0.87f, 0.9f));
        float hgt = p.BoardHeight;
        // Board centre line half a thickness outside the playing outline, so the inner face is the simulation's wall.
        var loop = Outline(p, -BoardThickness / 2f);
        int n = 0;
        void Board(Vector2 a, Vector2 b)
        {
            Vector2 d = b - a;
            float len = d.Length();
            if (len < 1e-3f) return;
            Vector2 mid = (a + b) / 2f;
            // A box whose local X runs along the segment; a little overlap closes the joints between short pieces.
            var basis = new Basis(Vector3.Up, -Mathf.Atan2(d.Y, d.X));
            var mi = new MeshInstance3D { Name = $"Board{n++}", Mesh = new BoxMesh { Size = new Vector3(len + BoardThickness * 0.5f, hgt, BoardThickness) }, MaterialOverride = mat };
            mi.Transform = new Transform3D(basis, new Vector3(mid.X, hgt / 2f, mid.Y));
            AddChild(mi);
        }
        // The goal-end wall (the first edge) is split around the goal mouth.
        float gx = loop[0].X, mouth = p.GoalHalfWidth + p.PostRadius;
        Board(loop[0], new Vector2(gx, -mouth));
        Board(new Vector2(gx, mouth), loop[1]);
        for (int i = 1; i < loop.Count; i++) Board(loop[i], loop[(i + 1) % loop.Count]);
    }

    void BuildMarkings(FootballSim.Core.Pitch p)
    {
        var st = MeshKit.BeginTriangles();
        void Seg(Vector2 a, Vector2 b) => MeshKit.AddSegment(st, a, b, LineW, LineY);

        // Outline just inside the boards.
        var loop = Outline(p, OutlineInset);
        for (int i = 0; i < loop.Count; i++) Seg(loop[i], loop[(i + 1) % loop.Count]);

        // Goal area: 5.5 m deep, 18.32 m wide, its sides cut off where they meet the outline line (the arena is narrow).
        float L = p.GoalLineX;
        float boxX = L - 5.5f, boxZ = Mathf.Min(18.32f / 2f, p.HalfWidth - OutlineInset);
        float chamferX = L + p.HalfWidth - p.GoalChamfer - OutlineInset * Mathf.Sqrt2 - boxZ; // where z = boxZ meets the chamfer line
        float sideEnd = Mathf.Min(L - OutlineInset, chamferX);
        Seg(new Vector2(boxX, -boxZ), new Vector2(boxX, boxZ));
        if (sideEnd > boxX)
        {
            Seg(new Vector2(boxX, -boxZ), new Vector2(sideEnd, -boxZ));
            Seg(new Vector2(boxX, boxZ), new Vector2(sideEnd, boxZ));
        }

        // Penalty area: the core's depth and width, its front line and sides cut off at the outline line the same way.
        float areaX = L - p.PenaltyAreaDepth;
        float areaZ = Mathf.Min(p.PenaltyAreaHalfWidth, OutlineHalfWidthAt(p, areaX, OutlineInset));
        float areaChamferX = L + p.HalfWidth - p.GoalChamfer - OutlineInset * Mathf.Sqrt2 - areaZ;
        float areaSideEnd = Mathf.Min(L - OutlineInset, areaChamferX);
        if (areaZ > 0f) Seg(new Vector2(areaX, -areaZ), new Vector2(areaX, areaZ));
        // In an arena narrower than the area its sides are the boards: no side lines to draw.
        if (p.PenaltyAreaHalfWidth < p.HalfWidth - OutlineInset && areaSideEnd > areaX)
        {
            Seg(new Vector2(areaX, -areaZ), new Vector2(areaSideEnd, -areaZ));
            Seg(new Vector2(areaX, areaZ), new Vector2(areaSideEnd, areaZ));
        }

        AddMesh("Markings", st.Commit(), Vector3.Zero, MeshKit.Unshaded(Colors.White));

        var spot = new MeshInstance3D { Name = "PenaltySpot", Mesh = MeshKit.Ring(0f, 0.22f, 16), Position = new(L - 11f, LineY, 0), MaterialOverride = MeshKit.Unshaded(Colors.White), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(spot);
    }

    /// <summary>
    /// Half the width of the outline moved <paramref name="inset"/> metres inward at <paramref name="x"/>: the side walls, narrowed
    /// by a rounded back corner or a goal-end chamfer where <paramref name="x"/> is in one (0 outside the arena).
    /// </summary>
    static float OutlineHalfWidthAt(FootballSim.Core.Pitch p, float x, float inset)
    {
        float L = p.GoalLineX, h = p.HalfWidth, R = p.CornerRadius, C = p.GoalChamfer;
        if (x < inset || x > L - inset) return 0f;
        float z = h - inset;
        if (C > 0f) z = Mathf.Min(z, L + h - C - inset * Mathf.Sqrt2 - x);
        if (R > 0f && x < R)
        {
            float r = Mathf.Max(0f, R - inset), dx = R - x;
            z = Mathf.Min(z, dx >= r ? h - R : h - R + Mathf.Sqrt(r * r - dx * dx));
        }
        return Mathf.Max(0f, z);
    }

    void BuildGoal(FootballSim.Core.Pitch p)
    {
        var white = MeshKit.Lit(Colors.White);
        float gx = p.GoalLineX, postZ = p.GoalHalfWidth, barY = p.BarHeight, postR = p.PostRadius;
        // Posts.
        foreach (float s in new[] { -1f, 1f })
            AddCylinder($"Post{(s < 0 ? "Left" : "Right")}", postR, barY, new(gx, barY / 2, s * postZ), Basis.Identity, white);
        // Crossbar, along Z.
        AddCylinder("Crossbar", postR, postZ * 2 + postR * 2, new(gx, barY, 0), Basis.FromEuler(new(Mathf.Pi / 2, 0, 0)), white);

        // Translucent net volume behind the goal line.
        var net = new StandardMaterial3D
        {
            AlbedoColor = new Color(1, 1, 1, 0.22f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        AddBox("Net", new Vector3(p.NetDepth, barY, postZ * 2), new(gx + p.NetDepth / 2f, barY / 2, 0), net, shadows: false);
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
