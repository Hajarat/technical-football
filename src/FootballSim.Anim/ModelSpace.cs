using System.Numerics;

namespace FootballSim.Anim;

/// <summary>
/// Between the game's world axes (a facing of 0 looks along +X, positive facings turn toward +Z, the body's right at facing 0 is +Z)
/// and the model's (the rigged body faces +Z with its left on +X). Both are Y up; the turn between them is a quarter turn about Y.
/// </summary>
public static class ModelSpace
{
    public static Vector3 FromWorld(Vector3 w) => new(-w.Z, w.Y, w.X);
    public static Vector3 ToWorld(Vector3 m) => new(m.Z, m.Y, -m.X);
    /// <summary>The model-space yaw (radians about +Y, 0 faces +Z) of a world facing.</summary>
    public static float YawFromFacing(float facing) => Spring.WrapPi(-facing);
}
