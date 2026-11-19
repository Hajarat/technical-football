using System.Numerics;

namespace FootballSim.Anim;

/// <summary>
/// One motion clip for a rigged body: per frame the hips position and every driven bone's local rotation, plus tagged event frames and
/// foot contacts. Written by tools/anim/retarget.py (the .kclip format, see <see cref="ClipReader"/>), read once and never changed.
/// </summary>
public sealed class Clip
{
    /// <summary>Contact bit of the left and right foot in <see cref="Contacts"/>.</summary>
    public const byte LeftFoot = 1, RightFoot = 2;

    public string Name { get; }
    /// <summary>Frames per second the clip was sampled at (60 for the keeper clips).</summary>
    public int Fps { get; }
    public bool Loop { get; }
    /// <summary>Bone names in the order of each frame's rotations (SkinnedBody's driven order, mixamorig_Hips first).</summary>
    public IReadOnlyList<string> Bones { get; }
    public int FrameCount { get; }
    public int BoneCount => Bones.Count;
    /// <summary>Event name to frame (at <see cref="Fps"/>), e.g. "contact", "takeoff", "landing".</summary>
    public IReadOnlyDictionary<string, int> Events { get; }

    readonly Vector3[] _hips;
    readonly Quaternion[] _rot;   // FrameCount x BoneCount
    readonly byte[] _contacts;

    public Clip(string name, int fps, bool loop, IReadOnlyList<string> bones, Vector3[] hips, Quaternion[] rotations,
        IReadOnlyDictionary<string, int> events, byte[] contacts)
    {
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        if (rotations.Length != hips.Length * bones.Count) throw new ArgumentException("rotations must hold one per bone per frame");
        if (contacts.Length != hips.Length) throw new ArgumentException("contacts must hold one per frame");
        Name = name; Fps = fps; Loop = loop; Bones = bones; FrameCount = hips.Length; Events = events;
        _hips = hips; _rot = rotations; _contacts = contacts;
    }

    public float Duration => FrameCount / (float)Fps;

    /// <summary>Hips position in model space (metres, ground at y = 0) at a frame.</summary>
    public Vector3 Hips(int frame) => _hips[frame];

    /// <summary>The local rotations of every bone at a frame.</summary>
    public ReadOnlySpan<Quaternion> Rotations(int frame) => new(_rot, frame * Bones.Count, Bones.Count);

    /// <summary>Contact bits (<see cref="LeftFoot"/>, <see cref="RightFoot"/>) at a frame.</summary>
    public byte Contacts(int frame) => _contacts[frame];

    /// <summary>
    /// Samples the clip at <paramref name="seconds"/> (clamped to the clip, or wrapped if it loops): bone rotations are slerped and
    /// the hips lerped between the two nearest frames. Writes <see cref="BoneCount"/> rotations into <paramref name="rotations"/>.
    /// </summary>
    public Vector3 Sample(float seconds, Span<Quaternion> rotations)
    {
        if (rotations.Length < BoneCount) throw new ArgumentException("rotations is too short");
        float f = seconds * Fps;
        int last = FrameCount - 1;
        int a; float u;
        if (Loop && FrameCount > 1)
        {
            f %= FrameCount;
            if (f < 0) f += FrameCount;
            a = (int)MathF.Floor(f); u = f - a;
            int b = (a + 1) % FrameCount;
            return Blend(a, b, u, rotations);
        }
        if (f <= 0 || last == 0) { a = 0; u = 0; }
        else if (f >= last) { a = last; u = 0; }
        else { a = (int)MathF.Floor(f); u = f - a; }
        return Blend(a, Math.Min(a + 1, last), u, rotations);
    }

    Vector3 Blend(int a, int b, float u, Span<Quaternion> rotations)
    {
        var ra = Rotations(a); var rb = Rotations(b);
        for (int k = 0; k < BoneCount; k++) rotations[k] = u == 0 ? ra[k] : Quaternion.Slerp(ra[k], rb[k], u);
        return u == 0 ? _hips[a] : Vector3.Lerp(_hips[a], _hips[b], u);
    }
}
