using System.Numerics;

namespace FootballSim.Anim;

/// <summary>
/// Critically damped springs in closed form (exact for any time step, so the result does not depend on the frame rate). A half-life
/// is the time a spring at rest takes to close half of its gap.
/// </summary>
public static class Spring
{
    const float Ln2 = 0.69314718056f;

    /// <summary>The spring's damping for a half-life (seconds).</summary>
    public static float Damping(float halfLife) => 4f * Ln2 / Math.Max(halfLife, 1e-5f);

    /// <summary>Decays <paramref name="x"/> (with velocity <paramref name="v"/>) toward zero.</summary>
    public static void Decay(ref Vector3 x, ref Vector3 v, float halfLife, float dt)
    {
        float y = Damping(halfLife) * 0.5f;
        Vector3 j1 = v + x * y;
        float e = MathF.Exp(-y * dt);
        x = e * (x + j1 * dt);
        v = e * (v - j1 * y * dt);
    }

    /// <summary>Decays a scalar toward zero.</summary>
    public static void Decay(ref float x, ref float v, float halfLife, float dt)
    {
        float y = Damping(halfLife) * 0.5f;
        float j1 = v + x * y;
        float e = MathF.Exp(-y * dt);
        x = e * (x + j1 * dt);
        v = e * (v - j1 * y * dt);
    }

    /// <summary>
    /// Moves a position <paramref name="x"/> whose velocity <paramref name="v"/> (and acceleration <paramref name="a"/>) springs toward
    /// <paramref name="goalVel"/>: the motion a body makes when it changes speed smoothly.
    /// </summary>
    public static void Velocity(ref Vector2 x, ref Vector2 v, ref Vector2 a, Vector2 goalVel, float halfLife, float dt)
    {
        float y = Damping(halfLife) * 0.5f;
        Vector2 j0 = v - goalVel;
        Vector2 j1 = a + j0 * y;
        float e = MathF.Exp(-y * dt);
        x = e * (-j1 / (y * y) + (-j0 - j1 * dt) / y) + j1 / (y * y) + j0 / y + goalVel * dt + x;
        v = e * (j0 + j1 * dt) + goalVel;
        a = e * (a - j1 * y * dt);
    }

    /// <summary>An angle (radians) springing toward <paramref name="goal"/> the short way round.</summary>
    public static void Angle(ref float x, ref float v, float goal, float halfLife, float dt)
    {
        float y = Damping(halfLife) * 0.5f;
        float j0 = WrapPi(x - goal);
        float j1 = v + j0 * y;
        float e = MathF.Exp(-y * dt);
        x = WrapPi(e * (j0 + j1 * dt) + goal);
        v = e * (v - j1 * y * dt);
    }

    /// <summary>An angle wrapped into [−π, π).</summary>
    public static float WrapPi(float a)
    {
        a = (a + MathF.PI) % (2f * MathF.PI);
        if (a < 0) a += 2f * MathF.PI;
        return a - MathF.PI;
    }

    /// <summary>The rotation as a scaled angle-axis vector (axis times angle, the angle in [0, π]).</summary>
    public static Vector3 Log(Quaternion q)
    {
        if (q.W < 0) q = Quaternion.Negate(q);
        var v = new Vector3(q.X, q.Y, q.Z);
        float s = v.Length();
        if (s < 1e-8f) return v * 2f;
        float angle = 2f * MathF.Atan2(s, q.W);
        return v * (angle / s);
    }

    /// <summary>The rotation for a scaled angle-axis vector (<see cref="Log"/>'s inverse).</summary>
    public static Quaternion Exp(Vector3 v)
    {
        float angle = v.Length();
        if (angle < 1e-8f) return Quaternion.Normalize(new Quaternion(v * 0.5f, 1f));
        float h = angle * 0.5f;
        return new Quaternion(v * (MathF.Sin(h) / angle), MathF.Cos(h));
    }

    /// <summary>Rotation about +Y by <paramref name="yaw"/> radians (turns +Z toward +X).</summary>
    public static Quaternion Yaw(float yaw) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);

    /// <summary>The angular velocity (scaled angle-axis per second, in the frame rotations are applied in) that turns <paramref name="from"/> into <paramref name="to"/> over <paramref name="dt"/>.</summary>
    public static Vector3 AngularVelocity(Quaternion from, Quaternion to, float dt) => Log(Quaternion.Normalize(to * Quaternion.Inverse(from))) / dt;
}
