using Godot;

namespace FootballSim.Game;

/// <summary>Small helpers for flat meshes and unshaded materials. No state.</summary>
public static class MeshKit
{
    public static StandardMaterial3D Lit(Color c) => new() { AlbedoColor = c, Roughness = 0.9f };

    public static StandardMaterial3D Unshaded(Color c, bool noDepthTest = false)
    {
        var m = new StandardMaterial3D
        {
            AlbedoColor = c,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            NoDepthTest = noDepthTest,
        };
        if (c.A < 1f) m.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        return m;
    }

    /// <summary>Adds a flat quad strip segment on the ground between two (x, z) points, extended by half a width at each end.</summary>
    public static void AddSegment(SurfaceTool st, Vector2 a, Vector2 b, float width, float y)
    {
        var dir = (b - a).Normalized();
        var perp = new Vector2(-dir.Y, dir.X) * (width * 0.5f);
        var ext = dir * (width * 0.5f);
        var p0 = a - ext + perp; var p1 = a - ext - perp;
        var p2 = b + ext - perp; var p3 = b + ext + perp;
        Vector3 V(Vector2 p) => new(p.X, y, p.Y);
        st.AddVertex(V(p0)); st.AddVertex(V(p1)); st.AddVertex(V(p2));
        st.AddVertex(V(p0)); st.AddVertex(V(p2)); st.AddVertex(V(p3));
    }

    public static SurfaceTool BeginTriangles()
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetNormal(Vector3.Up);
        return st;
    }

    /// <summary>Flat ring in the XZ plane, centred on the origin, at y = 0.</summary>
    public static ArrayMesh Ring(float inner, float outer, int segments = 48)
    {
        var st = BeginTriangles();
        for (int i = 0; i < segments; i++)
        {
            float a0 = Mathf.Tau * i / segments, a1 = Mathf.Tau * (i + 1) / segments;
            Vector3 P(float r, float a) => new(r * Mathf.Cos(a), 0, r * Mathf.Sin(a));
            st.AddVertex(P(inner, a0)); st.AddVertex(P(outer, a0)); st.AddVertex(P(outer, a1));
            st.AddVertex(P(inner, a0)); st.AddVertex(P(outer, a1)); st.AddVertex(P(inner, a1));
        }
        return st.Commit();
    }
}
