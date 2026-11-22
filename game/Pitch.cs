using System.Collections.Generic;
using Godot;

namespace FootballSim.Game;

/// <summary>
/// The walled arena, built in code from the simulation's arena: grass inside the outline, low boards all round (either side of
/// the goal too), a few markings and the goal. Outside the boards a concrete surround. The back corners are rounded (drawn as
/// short straight pieces) and the goal-end corners cut by straight boards, matching the simulation's shape.
/// </summary>
public partial class Pitch : Node3D
{
    const float LineW = 0.12f, LineY = 0.015f;
    const float BoardThickness = 0.15f;
    /// <summary>How far inside the boards the outline marking runs, metres.</summary>
    const float OutlineInset = 0.3f;
    const int ArcSegments = 12;
    const float GrassTileM = 2f, StripeM = 5f, StripeAmount = 0.06f;
    /// <summary>The cage: a post every PostSpacing metres, strands and rail sizes, all in metres.</summary>
    const float PostSpacing = 4f, PostSize = 0.06f, RailSize = 0.07f;

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
        BuildCage(p);
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

    static Texture2D Tex(string name) => GD.Load<Texture2D>($"res://assets/textures/{name}.jpg");

    /// <summary>A lit material from a CC0 colour / normal / roughness set, projected in world metres (<paramref name="tileM"/> per repeat).</summary>
    static StandardMaterial3D Surface(string set, Color tint, float tileM, float normalScale = 0.8f, bool colourMap = true) => new()
    {
        AlbedoTexture = colourMap ? Tex(set + "_color") : null, AlbedoColor = tint,
        NormalEnabled = true, NormalTexture = Tex(set + "_normal"), NormalScale = normalScale,
        RoughnessTexture = Tex(set + "_roughness"),
        Uv1Triplanar = true, Uv1WorldTriplanar = true, Uv1Scale = Vector3.One / tileM,
        TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
    };

    void BuildGround(FootballSim.Core.Pitch p)
    {
        // Grass: a fan over the convex outline, just above the surround floor. The shader tiles the CC0 grass and mows the stripes.
        var grass = new ShaderMaterial { Shader = GD.Load<Shader>("res://grass.gdshader") };
        grass.SetShaderParameter("albedo_tex", Tex("grass_color"));
        grass.SetShaderParameter("normal_tex", Tex("grass_normal"));
        grass.SetShaderParameter("rough_tex", Tex("grass_roughness"));
        grass.SetShaderParameter("tint", new Color(0.9f, 0.9f, 0.82f));
        grass.SetShaderParameter("tile_m", GrassTileM);
        grass.SetShaderParameter("stripe_m", StripeM);
        grass.SetShaderParameter("stripe_amount", StripeAmount);
        var st = MeshKit.BeginTriangles();
        var loop = Outline(p, 0f);
        var centre = new Vector3(p.GoalLineX / 2f, 0f, 0f);
        for (int i = 0; i < loop.Count; i++)
        {
            Vector2 a = loop[i], b = loop[(i + 1) % loop.Count];
            st.AddVertex(centre); st.AddVertex(new Vector3(a.X, 0f, a.Y)); st.AddVertex(new Vector3(b.X, 0f, b.Y));
        }
        AddMesh("Grass", st.Commit(), Vector3.Zero, grass);
        // Concrete all round, so nothing past the boards draws attention.
        AddBox("Surround", new Vector3(400f, 0.1f, 400f), new Vector3(p.GoalLineX / 2f, -0.06f, 0), Surround(), shadows: false);
    }

    static StandardMaterial3D Surround() => Surface("surround", new Color(0.55f, 0.57f, 0.6f), 3f, 0.6f);

    void BuildBoards(FootballSim.Core.Pitch p)
    {
        // a clean painted finish: a flat muted dark blue-grey; only the set's normal and roughness give it a faint surface
        var mat = Surface("boards", new Color(0.20f, 0.25f, 0.32f), 1.5f, 0.3f, colourMap: false);
        var cap = MeshKit.Lit(new Color(0.95f, 0.95f, 0.95f));
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
            // a white cap along the top; the board's inner face stays the simulation's wall
            var top = new MeshInstance3D { Name = $"BoardCap{n}", Mesh = new BoxMesh { Size = new Vector3(len + BoardThickness * 0.5f, 0.04f, BoardThickness + 0.04f) }, MaterialOverride = cap };
            top.Transform = new Transform3D(basis, new Vector3(mid.X, hgt + 0.02f, mid.Y));
            AddChild(top);
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

        // The net volume behind the goal line: the cage's net material on the sides, back and top.
        float depth = p.NetDepth;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        NetQuad(st, new Vector3(gx, 0, -postZ), new Vector3(gx + depth, 0, -postZ), barY, 0f, depth, new Vector2(0, -1));
        NetQuad(st, new Vector3(gx, 0, postZ), new Vector3(gx + depth, 0, postZ), barY, 0f, depth, new Vector2(0, 1));
        NetQuad(st, new Vector3(gx + depth, 0, -postZ), new Vector3(gx + depth, 0, postZ), barY, 0f, postZ * 2, new Vector2(1, 0));
        // top: a horizontal quad (u along X, v along Z)
        Color none = new(0.5f, 0f, 0.5f);
        void V(float x, float z, float u, float v) { st.SetColor(none); st.SetUV(new Vector2(u, v)); st.AddVertex(new Vector3(x, barY, z)); }
        V(gx, -postZ, 0, 0); V(gx + depth, -postZ, depth, 0); V(gx + depth, postZ, depth, postZ * 2);
        V(gx, -postZ, 0, 0); V(gx + depth, postZ, depth, postZ * 2); V(gx, postZ, 0, postZ * 2);
        AddCageMesh("Net", st.Commit(), NetMaterial(solid: false));
    }

    /// <summary>
    /// A vertical net panel from (x, z) corner <paramref name="a"/> to <paramref name="b"/> (their y is ignored), between heights
    /// <paramref name="y0"/> and <paramref name="y1"/> (either order), with u running <paramref name="u0"/> to <paramref name="u1"/> along it.
    /// <paramref name="outward"/> is the wall's outward normal (x, z), stored in the vertex colour for the camera-side fade.
    /// </summary>
    static void NetQuad(SurfaceTool st, Vector3 a, Vector3 b, float y1, float y0, float uSpan, Vector2 outward, float u0 = 0f)
    {
        var col = new Color(outward.X * 0.5f + 0.5f, 0f, outward.Y * 0.5f + 0.5f);
        void V(Vector3 p, float y, float u, float v) { st.SetColor(col); st.SetUV(new Vector2(u, v)); st.AddVertex(new Vector3(p.X, y, p.Z)); }
        float u1 = u0 + uSpan;
        V(a, y0, u0, y0); V(b, y0, u1, y0); V(b, y1, u1, y1);
        V(a, y0, u0, y0); V(b, y1, u1, y1); V(a, y1, u0, y1);
    }

    static ShaderMaterial NetMaterial(bool solid)
    {
        var m = new ShaderMaterial { Shader = GD.Load<Shader>("res://cage_net.gdshader"), RenderPriority = 0 };
        m.SetShaderParameter("solid", solid);
        return m;
    }

    void AddCageMesh(string name, Mesh mesh, Material mat) =>
        AddChild(new MeshInstance3D { Name = name, Mesh = mesh, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });

    /// <summary>
    /// The cage on every wall from the board top up to the ceiling: a diamond net, a thin post every few metres and a rail at the
    /// ceiling. Beside the goal the net runs to the ground-level boards; over the mouth it starts above the crossbar. No roof net.
    /// </summary>
    void BuildCage(FootballSim.Core.Pitch p)
    {
        float y0 = p.BoardHeight, y1 = p.Ceiling;
        var loop = Outline(p, -BoardThickness / 2f);
        var centre = new Vector2(p.GoalLineX / 2f, 0f);
        float mouth = p.GoalHalfWidth + p.PostRadius, aboveBar = p.BarHeight + p.PostRadius;
        var net = new SurfaceTool(); net.Begin(Mesh.PrimitiveType.Triangles);
        var solid = new SurfaceTool(); solid.Begin(Mesh.PrimitiveType.Triangles);
        float u = 0f, nextPost = 0f;
        for (int i = 0; i < loop.Count; i++)
        {
            Vector2 a = loop[i], b = loop[(i + 1) % loop.Count], d = b - a;
            float len = d.Length();
            if (len < 1e-3f) continue;
            var dir = d / len;
            var n = new Vector2(dir.Y, -dir.X);
            if (n.Dot((a + b) / 2f - centre) < 0f) n = -n;
            Vector3 A = new(a.X, 0, a.Y), B = new(b.X, 0, b.Y);
            if (i == 0)
            {
                // the goal-end wall: net beside the mouth to the board top, over the mouth from above the bar
                Vector3 M0 = new(a.X, 0, -mouth), M1 = new(a.X, 0, mouth);
                float sideLen = Mathf.Abs(-mouth - a.Y);
                NetQuad(net, A, M0, y1, y0, sideLen, n, u);
                NetQuad(net, M0, M1, y1, aboveBar, mouth * 2f, n, u + sideLen);
                NetQuad(net, M1, B, y1, y0, Mathf.Abs(b.Y - mouth), n, u + sideLen + mouth * 2f);
            }
            else NetQuad(net, A, B, y1, y0, len, n, u);
            // posts every PostSpacing metres along the whole outline, and a rail on top
            while (nextPost <= u + len + 1e-4f)
            {
                var at = a + dir * Mathf.Max(0f, nextPost - u);
                bool inMouth = i == 0 && Mathf.Abs(at.Y) < mouth - 0.01f;
                if (!inMouth) AddOrientedBox(solid, new Vector3(at.X, y1 / 2f, at.Y), dir, y1, PostSize, n);
                nextPost += PostSpacing;
            }
            AddOrientedBox(solid, new Vector3((a.X + b.X) / 2f, y1, (a.Y + b.Y) / 2f), dir, RailSize, RailSize, n, alongLen: len);
            u += len;
        }
        // posts at both ends of the mouth, where the cage is held either side of it
        foreach (float s in new[] { -1f, 1f })
            AddOrientedBox(solid, new Vector3(loop[0].X, y1 / 2f, s * mouth), Vector2.Right, y1, PostSize, Vector2.Right);
        AddCageMesh("CageNet", net.Commit(), NetMaterial(solid: false));
        AddCageMesh("CagePosts", solid.Commit(), NetMaterial(solid: true));
    }

    /// <summary>
    /// A box centred at <paramref name="c"/> whose long side along the wall is <paramref name="alongLen"/> (or <paramref name="size"/> for a post),
    /// <paramref name="height"/> tall, <paramref name="size"/> thick; its vertex colour carries the wall's outward normal.
    /// </summary>
    static void AddOrientedBox(SurfaceTool st, Vector3 c, Vector2 dir, float height, float size, Vector2 outward, float alongLen = -1f)
    {
        float hl = (alongLen < 0f ? size : alongLen) / 2f, hw = size / 2f, hh = height / 2f;
        var col = new Color(outward.X * 0.5f + 0.5f, 0f, outward.Y * 0.5f + 0.5f);
        var ax = new Vector3(dir.X, 0, dir.Y) * hl; var az = new Vector3(-dir.Y, 0, dir.X) * hw; var ay = Vector3.Up * hh;
        Vector3 P(int sx, int sy, int sz) => c + ax * sx + ay * sy + az * sz;
        void Face(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
        {
            foreach (var q in new[] { p0, p1, p2, p0, p2, p3 }) { st.SetColor(col); st.SetUV(Vector2.Zero); st.AddVertex(q); }
        }
        Face(P(-1, -1, -1), P(1, -1, -1), P(1, 1, -1), P(-1, 1, -1));
        Face(P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1));
        Face(P(-1, -1, -1), P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1));
        Face(P(1, -1, -1), P(1, -1, 1), P(1, 1, 1), P(1, 1, -1));
        Face(P(-1, 1, -1), P(1, 1, -1), P(1, 1, 1), P(-1, 1, 1));
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
