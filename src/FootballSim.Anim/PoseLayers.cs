using System.Numerics;

namespace FootballSim.Anim;

/// <summary>
/// Body targets for the stance layer, in the character space of the simulation's root (model metres; the body faces +Z, its left is
/// +X): hips height, forward trunk lean (radians), each ankle's sideways place (x) and each hand relative to the point between the
/// shoulders. The game takes them from the procedural pose of the same state, so mocap keeps the hand-built intent ("made big").
/// </summary>
public struct StanceTargets
{
    /// <summary>How much of the layer the state wants (0 = none, 1 = all of it).</summary>
    public float Weight;
    public float HipsHeight, Lean;
    public float LeftAnkleX, RightAnkleX;
    public Vector3 LeftHand, RightHand;
}

/// <summary>
/// Corrections over a placed clip pose (model space, before the foot lock): turning the trunk so the chest faces a heading, and the
/// stance layer that brings hips height, lean, stance width and hands to <see cref="StanceTargets"/>. Each changes only what it must;
/// legs keep the clip's stepping and the arms its swing where the layer is off.
/// </summary>
public static class PoseLayers
{
    static readonly float[] TwistShare = { 0.1f, 0.2f, 0.3f, 0.4f };   // hips, spine, spine1, spine2: more at the top

    /// <summary>The chest's heading (yaw about +Y; 0 faces +Z): Spine2's forward turned from rest, flattened.</summary>
    public static float ChestYaw(Skeleton sk, ReadOnlySpan<Quaternion> globalRot)
    {
        int c = sk.Find("Spine2");
        var fwd = Vector3.Transform(Vector3.UnitZ, globalRot[c] * Quaternion.Inverse(sk.RestGlobal[c]));
        if (fwd.X * fwd.X + fwd.Z * fwd.Z < 1e-6f) fwd = Vector3.Transform(Vector3.UnitZ, globalRot[sk.Hips] * Quaternion.Inverse(sk.RestGlobal[sk.Hips]));
        return MathF.Atan2(fwd.X, fwd.Z);
    }

    /// <summary>How far (radians) the chest must turn to come within <paramref name="tolerance"/> of <paramref name="heading"/>.</summary>
    public static float ChestCorrection(float chestYaw, float heading, float tolerance)
    {
        float d = Spring.WrapPi(heading - chestYaw);
        return d - Math.Clamp(d, -tolerance, tolerance);
    }

    /// <summary>
    /// Turns the trunk by <paramref name="angle"/> about the vertical, shared over hips, spine, spine1 and spine2 (more at the top) so
    /// the waist does not kink; the thighs are turned back by the hips' share, so the legs keep the clip's direction.
    /// <paramref name="globalRot"/> is this pose's forward pass (before the turn).
    /// </summary>
    public static void TwistTrunk(Skeleton sk, Span<Quaternion> local, ReadOnlySpan<Quaternion> globalRot, float angle)
    {
        if (MathF.Abs(angle) < 1e-6f) return;
        Span<int> trunk = stackalloc int[] { sk.Hips, sk.Find("Spine"), sk.Find("Spine1"), sk.Find("Spine2") };   // (on the stack: this runs every tick)
        for (int t = 0; t < trunk.Length; t++)
            RotateLocal(sk, local, globalRot, trunk[t], Vector3.UnitY, angle * TwistShare[t]);
        for (int i = 0; i < 2; i++)
            RotateLocal(sk, local, globalRot, sk.UpLeg[i], Vector3.UnitY, -angle * TwistShare[0]);
    }

    static readonly string[] SpineNames = { "Spine", "Spine1", "Spine2" };

    /// <summary>Pitches the trunk forward by <paramref name="angle"/> about the body's sideways axis (model +X turned by <paramref name="yaw"/>), over the three spine bones.</summary>
    public static void LeanTrunk(Skeleton sk, Span<Quaternion> local, ReadOnlySpan<Quaternion> globalRot, float yaw, float angle)
    {
        if (MathF.Abs(angle) < 1e-6f) return;
        var axis = Vector3.Transform(Vector3.UnitX, Spring.Yaw(yaw));   // the body's left: a turn about it tips the chest forward
        foreach (var name in SpineNames)
            RotateLocal(sk, local, globalRot, sk.Find(name), axis, angle / 3f);
    }

    /// <summary>
    /// Rotates bone <paramref name="k"/> by <paramref name="angle"/> about a model-space axis through its joint (its parent's
    /// forward pass is <paramref name="globalRot"/>): local' = (Gp⁻¹ · R · Gp) · local, so earlier turns in the chain add up.
    /// </summary>
    static void RotateLocal(Skeleton sk, Span<Quaternion> local, ReadOnlySpan<Quaternion> globalRot, int k, Vector3 axis, float angle)
    {
        var r = Quaternion.CreateFromAxisAngle(axis, angle);
        int p = sk.Parents[k];
        if (p < 0) { local[k] = Quaternion.Normalize(r * local[k]); return; }
        var gp = globalRot[p];
        local[k] = Quaternion.Normalize(Quaternion.Inverse(gp) * r * gp * local[k]);
    }

    /// <summary>
    /// Bends a two-bone chain (upper, lower, end bones; the end keeps its global rotation) so its end meets <paramref name="target"/>,
    /// the middle joint bending the way it points (<paramref name="pole"/> for an almost straight chain). Needs this pose's forward pass.
    /// </summary>
    public static void Reach(Skeleton sk, Span<Quaternion> local, ReadOnlySpan<Quaternion> gr, ReadOnlySpan<Vector3> gp,
        int upper, int lower, int end, Vector3 target, Vector3 pole)
    {
        Vector3 a = gp[upper], b = gp[lower], c = gp[end];
        if ((target - c).LengthSquared() < 1e-10f) return;
        Vector3 line = Vector3.Normalize(c - a);
        Vector3 bend = (b - a) - line * Vector3.Dot(b - a, line);
        TwoBoneIk.Apply(a, b, c, gr[upper], gr[lower], target, bend + pole * 0.02f, out var g1, out var g2, out _, 0.96f);
        local[upper] = Quaternion.Normalize(Quaternion.Inverse(gr[sk.Parents[upper]]) * g1);
        local[lower] = Quaternion.Normalize(Quaternion.Inverse(g1) * g2);
        local[end] = Quaternion.Normalize(Quaternion.Inverse(g2) * gr[end]);
    }
}
