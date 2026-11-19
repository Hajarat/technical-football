using System.Numerics;

namespace FootballSim.Anim;

/// <summary>
/// Pose-offset inertialization: at a switch between two pose sources (one clip frame to another, or clips to the procedural pose) the
/// difference between what was shown and the new source, and the difference of their velocities, become an offset that decays to
/// zero with a critically damped spring while the new source plays underneath, so the switch never pops. Per bone a rotation offset
/// (applied on the left: out = offset · source) and a translation offset (out = offset + source; the hips' position, and any bone
/// whose local offset the source moves).
/// </summary>
public sealed class Inertializer
{
    /// <summary>Half-life of a switch, and of one into lying or a get-up (spec appendix E).</summary>
    public const float HalfLife = 0.08f, HalfLifeGround = 0.12f;

    readonly Vector3[] _rx, _rv, _px, _pv;
    float _halfLife = HalfLife;

    public Inertializer(int bones)
    {
        _rx = new Vector3[bones]; _rv = new Vector3[bones]; _px = new Vector3[bones]; _pv = new Vector3[bones];
    }

    public int BoneCount => _rx.Length;

    /// <summary>True while any offset is still visible (above about 0.01 degree or 0.1 mm).</summary>
    public bool Active
    {
        get
        {
            for (int k = 0; k < _rx.Length; k++)
                if (_rx[k].LengthSquared() > 3e-8f || _px[k].LengthSquared() > 1e-8f || _rv[k].LengthSquared() > 1e-6f || _pv[k].LengthSquared() > 1e-6f) return true;
            return false;
        }
    }

    /// <summary>Drops every offset (a teleport or a seek: the next pose is shown as it is).</summary>
    public void Reset()
    {
        Array.Clear(_rx); Array.Clear(_rv); Array.Clear(_px); Array.Clear(_pv);
    }

    /// <summary>
    /// Starts a switch. <paramref name="shownRot"/>/<paramref name="shownPos"/> is the pose shown now (with its velocities), dst* the
    /// new source at the same instant. Velocities are angular (scaled angle-axis per second, left frame) and linear.
    /// </summary>
    public void Transition(ReadOnlySpan<Quaternion> shownRot, ReadOnlySpan<Vector3> shownAngVel, ReadOnlySpan<Vector3> shownPos, ReadOnlySpan<Vector3> shownVel,
        ReadOnlySpan<Quaternion> dstRot, ReadOnlySpan<Vector3> dstAngVel, ReadOnlySpan<Vector3> dstPos, ReadOnlySpan<Vector3> dstVel, float halfLife = HalfLife)
    {
        _halfLife = halfLife;
        for (int k = 0; k < _rx.Length; k++)
        {
            _rx[k] = Spring.Log(Quaternion.Normalize(shownRot[k] * Quaternion.Inverse(dstRot[k])));
            _rv[k] = shownAngVel[k] - dstAngVel[k];
            _px[k] = shownPos[k] - dstPos[k];
            _pv[k] = shownVel[k] - dstVel[k];
        }
    }

    /// <summary>Decays the offsets by <paramref name="dt"/> and writes the source with the offsets applied.</summary>
    public void Update(float dt, ReadOnlySpan<Quaternion> srcRot, ReadOnlySpan<Vector3> srcPos, Span<Quaternion> outRot, Span<Vector3> outPos)
    {
        for (int k = 0; k < _rx.Length; k++)
        {
            Spring.Decay(ref _rx[k], ref _rv[k], _halfLife, dt);
            Spring.Decay(ref _px[k], ref _pv[k], _halfLife, dt);
            outRot[k] = Quaternion.Normalize(Spring.Exp(_rx[k]) * srcRot[k]);
            outPos[k] = _px[k] + srcPos[k];
        }
    }

    /// <summary>The current rotation offset of a bone (angle-axis, radians) and translation offset.</summary>
    public Vector3 RotationOffset(int bone) => _rx[bone];
    public Vector3 PositionOffset(int bone) => _px[bone];
}
