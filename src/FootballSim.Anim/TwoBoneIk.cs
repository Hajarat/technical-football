using System.Numerics;

namespace FootballSim.Anim;

/// <summary>Two-bone inverse kinematics (thigh and shin, upper arm and forearm).</summary>
public static class TwoBoneIk
{
    /// <summary>
    /// Places the middle and end joints of a chain rooted at <paramref name="root"/> with bone lengths <paramref name="upper"/> and
    /// <paramref name="lower"/> so the end meets <paramref name="target"/>; the middle joint bends toward <paramref name="pole"/> (a
    /// direction). A target out of reach gives a straight chain pointing at it; one too close, the most folded chain toward it.
    /// </summary>
    public static void Solve(Vector3 root, float upper, float lower, Vector3 target, Vector3 pole, out Vector3 mid, out Vector3 end) =>
        Solve(root, upper, lower, target, pole, 1f, out mid, out end);

    /// <summary>
    /// <see cref="Solve(Vector3, float, float, Vector3, Vector3, out Vector3, out Vector3)"/> with a soft reach: past
    /// <paramref name="softFrom"/> of the full length the chain approaches straight smoothly (exponentially) instead of snapping
    /// straight, so the middle joint never whips as a target nears full reach; the end then falls a little short of such a target.
    /// </summary>
    public static void Solve(Vector3 root, float upper, float lower, Vector3 target, Vector3 pole, float softFrom, out Vector3 mid, out Vector3 end)
    {
        Vector3 d = target - root;
        float len = d.Length();
        Vector3 dn = len > 1e-6f ? d / len : -Vector3.UnitY;
        float full = upper + lower, soft = full * Math.Clamp(softFrom, 0f, 1f);
        if (softFrom < 1f && len > soft)
        {
            float band = full - soft;
            len = soft + band * (1f - MathF.Exp(-(len - soft) / band));
        }
        len = Math.Clamp(len, Math.Abs(upper - lower) + 1e-4f, full);
        end = root + dn * len;
        float x = (len * len + upper * upper - lower * lower) / (2f * len);
        float h = MathF.Sqrt(Math.Max(0f, upper * upper - x * x));
        Vector3 p = pole - dn * Vector3.Dot(pole, dn);
        if (p.LengthSquared() < 1e-10f)
        {
            p = Math.Abs(dn.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitZ;
            p -= dn * Vector3.Dot(p, dn);
        }
        mid = root + dn * x + Vector3.Normalize(p) * h;
    }

    /// <summary>
    /// Bends an existing chain (joint positions <paramref name="a"/>, <paramref name="b"/>, <paramref name="c"/>, global rotations
    /// <paramref name="ga"/> of the upper bone and <paramref name="gb"/> of the lower) so its end meets <paramref name="target"/>, the
    /// middle joint bending toward <paramref name="pole"/> (for a knee: the thigh's forward). Each bone turns the shortest way onto its
    /// new direction, so a chain already bent toward the pole and aimed at the target is left as it is, and the result moves
    /// continuously with the target. Returns the new global rotations of both bones.
    /// </summary>
    public static void Apply(Vector3 a, Vector3 b, Vector3 c, Quaternion ga, Quaternion gb, Vector3 target, Vector3 pole,
        out Quaternion ga2, out Quaternion gb2) => Apply(a, b, c, ga, gb, target, pole, out ga2, out gb2, out _);

    /// <summary><see cref="Apply(Vector3, Vector3, Vector3, Quaternion, Quaternion, Vector3, Vector3, out Quaternion, out Quaternion)"/>, also giving where the end got to.</summary>
    public static void Apply(Vector3 a, Vector3 b, Vector3 c, Quaternion ga, Quaternion gb, Vector3 target, Vector3 pole,
        out Quaternion ga2, out Quaternion gb2, out Vector3 reached, float softFrom = 1f)
    {
        float l1 = (b - a).Length(), l2 = (c - b).Length();
        if (l1 < 1e-6f || l2 < 1e-6f) { ga2 = ga; gb2 = gb; reached = c; return; }
        // the soft reach starts no nearer than the chain already reaches, so a target at its end still leaves it as it is
        float soft = Math.Max(softFrom, Math.Min(1f, (c - a).Length() / (l1 + l2)));
        Solve(a, l1, l2, target, pole, soft, out var b2, out var c2);
        var r1 = FromTo(b - a, b2 - a);
        var r2 = FromTo(Vector3.Transform(c - b, r1), c2 - b2);
        ga2 = Quaternion.Normalize(r1 * ga);
        gb2 = Quaternion.Normalize(r2 * r1 * gb);
        reached = c2;
    }

    /// <summary>The shortest rotation turning direction <paramref name="from"/> onto <paramref name="to"/>.</summary>
    public static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        float lf = from.Length(), lt = to.Length();
        if (lf < 1e-9f || lt < 1e-9f) return Quaternion.Identity;
        Vector3 f = from / lf, t = to / lt;
        float d = Vector3.Dot(f, t);
        if (d < -1f + 1e-6f)
        {
            Vector3 axis = Vector3.Cross(f, Vector3.UnitX);
            if (axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(f, Vector3.UnitY);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        Vector3 cr = Vector3.Cross(f, t);   // accurate for small turns too (no cosine threshold)
        return Quaternion.Normalize(new Quaternion(cr, 1f + d));
    }
}
