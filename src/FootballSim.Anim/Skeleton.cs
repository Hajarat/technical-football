using System.Numerics;

namespace FootballSim.Anim;

/// <summary>
/// The driven bones of the rigged body the clips were made for (clips.json "skeleton", read from keeper.glb by tools/anim/retarget.py):
/// per bone its parent (an index into the same list, -1 for the hips), its rest offset from the parent (the local translation, metres,
/// model at 1.80 m) and its rest local rotation. Model space: Y up, the body faces +Z, its left is +X, the ground is y = 0.
/// </summary>
public sealed class Skeleton
{
    public IReadOnlyList<string> Names { get; }
    public IReadOnlyList<int> Parents { get; }
    public IReadOnlyList<Vector3> Offsets { get; }
    public IReadOnlyList<Quaternion> RestLocal { get; }
    /// <summary>Rest global rotations (the hips at its rest offset), from <see cref="RestLocal"/>.</summary>
    public IReadOnlyList<Quaternion> RestGlobal { get; }
    /// <summary>For each bone, the bone on the other side (LeftX and RightX swap; the trunk maps to itself).</summary>
    public IReadOnlyList<int> MirrorOf { get; }
    public int Count => Names.Count;

    public int Hips { get; }
    /// <summary>Per side (0 = left, 1 = right): thigh, shin, foot and toe bones.</summary>
    public readonly int[] UpLeg = new int[2], Leg = new int[2], Foot = new int[2], Toe = new int[2];

    public Skeleton(IReadOnlyList<string> names, IReadOnlyList<int> parents, IReadOnlyList<Vector3> offsets, IReadOnlyList<Quaternion> restLocal)
    {
        int n = names.Count;
        if (parents.Count != n || offsets.Count != n || restLocal.Count != n) throw new ArgumentException("skeleton lists differ in length");
        for (int k = 0; k < n; k++)
            if (parents[k] >= k || (parents[k] < 0 && k != 0)) throw new ArgumentException("bones must be in hierarchy order with the hips first");
        Names = names; Parents = parents; Offsets = offsets; RestLocal = restLocal;
        var g = new Quaternion[n];
        for (int k = 0; k < n; k++) g[k] = parents[k] < 0 ? restLocal[k] : Quaternion.Normalize(g[parents[k]] * restLocal[k]);
        RestGlobal = g;
        var m = new int[n];
        for (int k = 0; k < n; k++)
        {
            string s = Short(names[k]);
            string other = s.StartsWith("Left") ? "Right" + s[4..] : s.StartsWith("Right") ? "Left" + s[5..] : s;
            m[k] = Find(other);
            if (m[k] < 0) throw new ArgumentException($"no mirror bone for {names[k]}");
        }
        MirrorOf = m;
        Hips = Find("Hips");
        string[] sides = { "Left", "Right" };
        for (int i = 0; i < 2; i++)
        {
            UpLeg[i] = Find(sides[i] + "UpLeg"); Leg[i] = Find(sides[i] + "Leg"); Foot[i] = Find(sides[i] + "Foot"); Toe[i] = Find(sides[i] + "ToeBase");
        }
    }

    /// <summary>The bone name without its rig prefix (mixamorig_Hips, mixamorig:Hips → Hips).</summary>
    public static string Short(string bone)
    {
        int cut = Math.Max(bone.LastIndexOf('_'), bone.LastIndexOf(':'));
        return bone[(cut + 1)..];
    }

    /// <summary>Index of the bone whose short name is <paramref name="shortName"/>, or -1.</summary>
    public int Find(string shortName)
    {
        // (compared in place: the motion layers look bones up by name every tick, and a substring per bone tried was garbage)
        for (int k = 0; k < Names.Count; k++) if (ShortSpan(Names[k]).SequenceEqual(shortName)) return k;
        return -1;
    }

    /// <summary><see cref="Short"/> as a span over the name, without making a string.</summary>
    static ReadOnlySpan<char> ShortSpan(string bone) => bone.AsSpan(Math.Max(bone.LastIndexOf('_'), bone.LastIndexOf(':')) + 1);

    /// <summary>
    /// Forward kinematics: global rotations and joint positions (model space) of every bone from local rotations and the hips position.
    /// <paramref name="localPos"/>, when not empty, replaces the rest offsets (index 0 is ignored: the hips sit at <paramref name="hips"/>).
    /// </summary>
    public void Forward(ReadOnlySpan<Quaternion> local, Vector3 hips, Span<Quaternion> globalRot, Span<Vector3> globalPos, ReadOnlySpan<Vector3> localPos = default)
    {
        for (int k = 0; k < Count; k++)
        {
            int p = Parents[k];
            if (p < 0) { globalRot[k] = local[k]; globalPos[k] = hips; continue; }
            Vector3 off = localPos.IsEmpty ? Offsets[k] : localPos[k];
            globalRot[k] = Quaternion.Normalize(globalRot[p] * local[k]);
            globalPos[k] = globalPos[p] + Vector3.Transform(off, globalRot[p]);
        }
    }
}
