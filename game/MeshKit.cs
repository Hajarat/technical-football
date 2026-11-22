using System;
using System.Collections.Generic;
using Godot;

namespace FootballSim.Game;

/// <summary>Small helpers for flat meshes and unshaded materials. No state.</summary>
public static class MeshKit
{
    public static StandardMaterial3D Lit(Color c) => new() { AlbedoColor = c, Roughness = 0.9f };

    /// <summary>Lit material with a chosen roughness and a subtle rim light (the rim keeps silhouettes readable against the pitch).</summary>
    public static StandardMaterial3D Shaded(Color c, float roughness, float rim = 0.25f, float rimTint = 0.4f) => new()
    {
        AlbedoColor = c, Roughness = roughness, Metallic = 0f, RimEnabled = rim > 0f, Rim = rim, RimTint = rimTint,
    };

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

    /// <summary>
    /// Surface of revolution about the Y axis from a (y, r) profile (ascending y). Normals are smooth between profile segments and split where the
    /// profile turns sharply (hems, steps). <paramref name="bandEnds"/> are profile indices where a new surface starts (the vertex is shared), so
    /// one profile can carry several materials; <paramref name="mats"/> are assigned per band. <paramref name="aspect"/> scales the ring's (x, z),
    /// making elliptical sections; <paramref name="orient"/>/<paramref name="offset"/> place the result. Surfaces are appended to <paramref name="into"/>.
    /// </summary>
    public static ArrayMesh Lathe(IReadOnlyList<(float y, float r)> p, int segs = 12, int[]? bandEnds = null, Vector2? aspect = null,
        Basis? orient = null, Vector3 offset = default, ArrayMesh? into = null, Material? mat = null, Material?[]? mats = null)
    {
        var mesh = into ?? new ArrayMesh();
        var ax = aspect ?? Vector2.One;
        var rot = orient ?? Basis.Identity;
        int n = p.Count;
        var sn = new Vector2[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            var d = new Vector2(p[i + 1].r - p[i].r, p[i + 1].y - p[i].y);
            sn[i] = d.LengthSquared() < 1e-12f ? Vector2.Zero : new Vector2(d.Y, -d.X).Normalized();
        }
        Vector2 Nrm(int i, int s)
        {
            if (i == 0 && p[i].r < 1e-5f) return new Vector2(0, -1);
            if (i == n - 1 && p[i].r < 1e-5f) return new Vector2(0, 1);
            if (i == 0) return sn[0];
            if (i == n - 1) return sn[n - 2];
            var a = sn[i - 1]; var b = sn[i];
            if (a.Dot(b) > 0.7f) { var m = a + b; return m.LengthSquared() > 1e-8f ? m.Normalized() : b; }
            return sn[s];
        }
        var ends = new List<int> { 0 };
        if (bandEnds != null) ends.AddRange(bandEnds);
        ends.Add(n - 1);
        for (int band = 0; band < ends.Count - 1; band++)
        {
            var st = new SurfaceTool();
            st.Begin(Mesh.PrimitiveType.Triangles);
            for (int s = ends[band]; s < ends[band + 1]; s++)
            {
                for (int k = 0; k < segs; k++)
                {
                    void V(int i, int kk)
                    {
                        float a = Mathf.Tau * kk / segs;
                        float cx = Mathf.Cos(a), cz = Mathf.Sin(a);
                        var nn = Nrm(i, s);
                        var pos = new Vector3(p[i].r * cx * ax.X, p[i].y, p[i].r * cz * ax.Y);
                        var nor = new Vector3(nn.X * cx / ax.X, nn.Y, nn.X * cz / ax.Y).Normalized();
                        st.SetNormal(rot * nor);
                        st.AddVertex(rot * pos + offset);
                    }
                    // clockwise seen from outside: (s,k) (s,k+1) (s+1,k) and (s,k+1) (s+1,k+1) (s+1,k)
                    V(s, k); V(s, k + 1); V(s + 1, k);
                    V(s, k + 1); V(s + 1, k + 1); V(s + 1, k);
                }
            }
            st.Commit(mesh);
            int surf = mesh.GetSurfaceCount() - 1;
            var m2 = mats != null && mats.Length > 0 ? mats[Math.Min(band, mats.Length - 1)] : mat;
            if (m2 != null) mesh.SurfaceSetMaterial(surf, m2);
        }
        return mesh;
    }

    /// <summary>Ellipsoid profile for <see cref="Lathe"/>: radius <paramref name="r"/>, y stretched by <paramref name="yScale"/>.</summary>
    public static List<(float y, float r)> SphereProfile(float r, float yScale = 1f, int rings = 6)
    {
        var l = new List<(float, float)>();
        for (int j = 0; j <= rings; j++)
        {
            float phi = Mathf.Pi * (-0.5f + (float)j / rings);
            l.Add((r * yScale * Mathf.Sin(phi), j == 0 || j == rings ? 0f : r * Mathf.Cos(phi)));
        }
        return l;
    }

    /// <summary>
    /// Profile of a tapered capsule limb: stations (t metres along the limb from the proximal joint, radius), with rounded caps of the given
    /// lengths extending beyond both joints. y is centred on the limb midpoint so the mesh can be placed at (a + b) / 2.
    /// Returns the profile and the profile index of each station (for band splits).
    /// </summary>
    public static List<(float y, float r)> TubeProfile(float len, float capProx, float capDist, (float t, float r)[] st, out int stationBase, int capSteps = 4)
    {
        var l = new List<(float y, float r)>();
        float mid = len * 0.5f;
        for (int j = 0; j < capSteps; j++)
        {
            float phi = Mathf.Pi * 0.5f * (1f - (float)j / capSteps);   // 90 deg (tip) down toward 0 (full radius)
            l.Add((-mid - capProx * Mathf.Sin(phi), j == 0 ? 0f : st[0].r * Mathf.Cos(phi)));
        }
        stationBase = l.Count;
        foreach (var (t, r) in st) l.Add((t - mid, r));
        for (int j = 1; j <= capSteps; j++)
        {
            float phi = Mathf.Pi * 0.5f * ((float)j / capSteps);
            l.Add((len - mid + capDist * Mathf.Sin(phi), j == capSteps ? 0f : st[^1].r * Mathf.Cos(phi)));
        }
        return l;
    }
}
