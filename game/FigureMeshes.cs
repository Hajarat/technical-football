using Godot;

namespace FootballSim.Game;

/// <summary>The materials a figure's meshes are built with (shared by every part of one figure).</summary>
public sealed class FigureMaterials
{
    public StandardMaterial3D Shirt = null!, Shorts = null!, Socks = null!, Skin = null!, Boots = null!, Gloves = null!, Hair = null!, Sole = null!, Cuff = null!, Eye = null!;
}

/// <summary>
/// Procedural body-part meshes in metres for a 1.8 m figure; the figure scales them to its actual height. Limbs are tapered capsules centred on
/// their midpoint with +Y toward the distal joint; the rest sit around their own origin (hand: the wrist, +Y along the fingers, thumb on +X;
/// boot: x forward, y up, z right).
/// </summary>
public static class FigureMeshes
{
    public const float RefLen = 0.432f, RefArmUp = 0.342f, RefArmLow = 0.306f;
    public const float ThighCapProx = 0.06f, ThighCapDist = 0.05f, ShinCapProx = 0.05f, ShinCapDist = 0.035f;
    public const float UpperCapProx = 0.045f, UpperCapDist = 0.035f, ForeCapProx = 0.035f, ForeCapDist = 0.03f;
    public const float NeckLen = 0.234f;

    public static ArrayMesh Pelvis(FigureMaterials m) => MeshKit.Lathe(new (float, float)[]
    {
        (-0.10f, 0f), (-0.095f, 0.15f), (-0.06f, 0.225f), (0f, 0.245f), (0.05f, 0.23f), (0.08f, 0.20f), (0.095f, 0.12f), (0.10f, 0f),
    }, 14, aspect: new Vector2(0.62f, 1f), mat: m.Shorts);

    public static ArrayMesh Torso(FigureMaterials m) => MeshKit.Lathe(new (float, float)[]
    {
        (-0.27f, 0f), (-0.265f, 0.15f), (-0.24f, 0.185f), (-0.12f, 0.20f), (0.02f, 0.235f), (0.14f, 0.265f), (0.21f, 0.275f),
        (0.245f, 0.22f), (0.265f, 0.12f), (0.272f, 0f),
    }, 16, aspect: new Vector2(0.60f, 1f), mat: m.Shirt);

    public static ArrayMesh Neck(FigureMaterials m) =>
        MeshKit.Lathe(MeshKit.TubeProfile(NeckLen, 0.02f, 0.02f, new[] { (0f, 0.052f), (NeckLen, 0.048f) }, out _, 3), 10, mat: m.Skin);

    public static ArrayMesh Head(FigureMaterials m)
    {
        var head = new (float, float)[]
        {
            (-0.12f, 0f), (-0.115f, 0.030f), (-0.095f, 0.052f), (-0.06f, 0.068f), (-0.02f, 0.078f), (0.03f, 0.082f), (0.07f, 0.078f),
            (0.10f, 0.062f), (0.112f, 0.04f), (0.12f, 0f),
        };
        var asp = new Vector2(1.12f, 1f);
        var mesh = MeshKit.Lathe(head, 14, aspect: asp, mat: m.Skin);
        // hair: a cap slightly larger than the skull, tilted so the hairline sits higher at the front and the nape stays covered
        var hair = new (float, float)[]
        {
            (0.00f, 0.090f), (0.035f, 0.0895f), (0.07f, 0.084f), (0.10f, 0.067f), (0.115f, 0.045f), (0.125f, 0f),
        };
        MeshKit.Lathe(hair, 14, aspect: new Vector2(1.14f, 1.02f), orient: new Basis(Vector3.Back, 0.30f), offset: new Vector3(-0.014f, 0f, 0f), into: mesh, mat: m.Hair);
        MeshKit.Lathe(MeshKit.SphereProfile(0.016f, 1f, 4), 6, offset: new Vector3(0.100f, -0.012f, 0f), into: mesh, mat: m.Skin);                 // nose
        MeshKit.Lathe(MeshKit.SphereProfile(0.011f, 1f, 4), 6, offset: new Vector3(0.093f, 0.030f, 0.033f), into: mesh, mat: m.Eye);
        MeshKit.Lathe(MeshKit.SphereProfile(0.011f, 1f, 4), 6, offset: new Vector3(0.093f, 0.030f, -0.033f), into: mesh, mat: m.Eye);
        MeshKit.Lathe(MeshKit.SphereProfile(0.020f, 1.2f, 4), 6, offset: new Vector3(-0.005f, -0.005f, 0.080f), into: mesh, mat: m.Skin);          // ears
        MeshKit.Lathe(MeshKit.SphereProfile(0.020f, 1.2f, 4), 6, offset: new Vector3(-0.005f, -0.005f, -0.080f), into: mesh, mat: m.Skin);
        return mesh;
    }

    public static ArrayMesh Shoulder(FigureMaterials m) => MeshKit.Lathe(MeshKit.SphereProfile(0.052f, 1f, 6), 10, mat: m.Shirt);

    public static ArrayMesh Thigh(FigureMaterials m)
    {
        var st = new (float, float)[]
        {
            (0f, 0.090f), (0.08f, 0.100f), (0.20f, 0.097f), (0.29f, 0.101f), (0.29f, 0.080f), (0.36f, 0.072f), (RefLen, 0.062f),
        };
        var prof = MeshKit.TubeProfile(RefLen, ThighCapProx, ThighCapDist, st, out int b);
        return MeshKit.Lathe(prof, 12, new[] { b + 4 }, mats: new Material[] { m.Shorts, m.Skin });
    }

    public static ArrayMesh Shin(FigureMaterials m) =>
        MeshKit.Lathe(MeshKit.TubeProfile(RefLen, ShinCapProx, ShinCapDist, new (float, float)[]
        {
            (0f, 0.058f), (0.10f, 0.061f), (0.25f, 0.050f), (0.40f, 0.038f), (RefLen, 0.037f),
        }, out _), 12, mat: m.Socks);

    public static ArrayMesh UpperArm(FigureMaterials m, bool longSleeve)
    {
        if (longSleeve)
            return MeshKit.Lathe(MeshKit.TubeProfile(RefArmUp, UpperCapProx, UpperCapDist, new (float, float)[]
            {
                (0f, 0.050f), (0.08f, 0.057f), (0.20f, 0.049f), (RefArmUp, 0.041f),
            }, out _), 10, mat: m.Shirt);
        var st = new (float, float)[] { (0f, 0.050f), (0.08f, 0.057f), (0.17f, 0.053f), (0.17f, 0.043f), (0.25f, 0.039f), (RefArmUp, 0.036f) };
        var prof = MeshKit.TubeProfile(RefArmUp, UpperCapProx, UpperCapDist, st, out int b);
        return MeshKit.Lathe(prof, 10, new[] { b + 3 }, mats: new Material[] { m.Shirt, m.Skin });
    }

    public static ArrayMesh Forearm(FigureMaterials m, bool longSleeve) =>
        MeshKit.Lathe(MeshKit.TubeProfile(RefArmLow, ForeCapProx, ForeCapDist, new (float, float)[]
        {
            (0f, 0.038f), (0.08f, 0.039f), (0.22f, 0.030f), (RefArmLow, 0.026f),
        }, out _), 10, mat: longSleeve ? m.Shirt : m.Skin);

    /// <summary>Mitten hand with a thumb (a glove with a cuff for the keeper). Origin at the wrist.</summary>
    public static ArrayMesh Hand(FigureMaterials m, bool glove)
    {
        float g = glove ? 1.35f : 1f;
        var mat = glove ? m.Gloves : m.Skin;
        var palm = new (float, float)[] { (0f, 0f), (0.005f * g, 0.026f * g), (0.03f * g, 0.034f * g), (0.07f * g, 0.034f * g), (0.095f * g, 0.024f * g), (0.108f * g, 0f) };
        var mesh = MeshKit.Lathe(palm, 10, aspect: new Vector2(1.15f, 0.62f), mat: mat);
        MeshKit.Lathe(MeshKit.SphereProfile(0.013f * g, 2.2f, 4), 7, orient: new Basis(Vector3.Back, -0.6f),
            offset: new Vector3(0.036f * g, 0.026f * g, 0f), into: mesh, mat: mat);
        if (glove)
            MeshKit.Lathe(new (float, float)[] { (-0.015f, 0f), (-0.014f, 0.037f), (0.022f, 0.037f), (0.023f, 0f) }, 10, aspect: new Vector2(1f, 0.9f), into: mesh, mat: m.Cuff);
        return mesh;
    }

    /// <summary>Football boot: upper, flat sole and an ankle collar. Origin at the box centre the old foot used (x forward, ground at y = -0.045).</summary>
    public static ArrayMesh Boot(FigureMaterials m)
    {
        float Y = -0.045f;
        var upper = new (float, float)[]
        {
            (-0.153f, 0f), (-0.148f, 0.040f), (-0.12f, 0.058f), (-0.05f, 0.065f), (0.05f, 0.060f), (0.11f, 0.050f), (0.145f, 0.032f), (0.153f, 0f),
        };
        var toX = new Basis(Vector3.Back, -Mathf.Pi * 0.5f);   // lathe axis Y -> +X; ring x -> -Y (height), ring z stays width
        var mesh = MeshKit.Lathe(upper, 12, aspect: new Vector2(0.72f, 1f), orient: toX, offset: new Vector3(0f, Y + 0.047f, 0f), mat: m.Boots);
        MeshKit.Lathe(new (float, float)[] { (-0.150f, 0f), (-0.145f, 0.050f), (-0.10f, 0.066f), (0.10f, 0.064f), (0.148f, 0.040f), (0.156f, 0f) }, 12,
            aspect: new Vector2(0.16f, 1f), orient: toX, offset: new Vector3(0f, Y + 0.0105f, 0f), into: mesh, mat: m.Sole);
        // ankle collar around where the shin enters (ankle is 0.4 of the foot length behind the box centre)
        MeshKit.Lathe(new (float, float)[] { (-0.02f, 0f), (-0.018f, 0.046f), (0.05f, 0.044f), (0.052f, 0f) }, 10, offset: new Vector3(-0.118f, Y + 0.03f, 0f), into: mesh, mat: m.Boots);
        return mesh;
    }
}
