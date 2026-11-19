using System.Numerics;
using System.Text;

namespace FootballSim.Anim;

/// <summary>
/// Reads and writes .kclip files (little-endian, version 1), the format tools/anim/retarget.py writes:
/// <code>
/// char[4] "KCLP"; u16 version; u16 fps; u32 frames; u16 bones; u16 flags (bit 0 = loop)
/// bones x (u8 length, utf-8 name)
/// frames x (f32 x3 hips position, model space, metres; bones x i16 x3 local rotation, smallest-three)
/// u16 events; events x (u8 length, utf-8 name, i32 frame); frames x u8 contact bits (bit 0 = left foot, bit 1 = right)
/// </code>
/// Smallest-three: of the quaternion (x, y, z, w) the largest-magnitude component is dropped (the quaternion negated so it is
/// positive) and the other three, in order, are stored as round(v / sqrt(1/2) * 16383) shifted left one bit; the dropped component's
/// index sits in the low bits of the first two values (bit 0 of the first is index bit 0, bit 0 of the second index bit 1).
/// </summary>
public static class ClipReader
{
    public const int Version = 1;
    const float Sq = 0.70710678118654752f;
    const int QMax = 16383;
    static readonly byte[] Magic = "KCLP"u8.ToArray();

    public static Clip Read(string path) => Read(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));

    public static Clip Read(byte[] data, string name)
    {
        using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        var magic = r.ReadBytes(4);
        if (!magic.AsSpan().SequenceEqual(Magic)) throw new InvalidDataException($"{name}: not a .kclip file");
        int version = r.ReadUInt16();
        if (version != Version) throw new InvalidDataException($"{name}: .kclip version {version}, expected {Version}");
        int fps = r.ReadUInt16();
        int frames = checked((int)r.ReadUInt32());
        int boneCount = r.ReadUInt16();
        int flags = r.ReadUInt16();
        var bones = new string[boneCount];
        for (int k = 0; k < boneCount; k++) bones[k] = ReadName(r);
        var hips = new Vector3[frames];
        var rot = new Quaternion[frames * boneCount];
        for (int f = 0; f < frames; f++)
        {
            hips[f] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            for (int k = 0; k < boneCount; k++) rot[f * boneCount + k] = Decode(r.ReadInt16(), r.ReadInt16(), r.ReadInt16());
        }
        int eventCount = r.ReadUInt16();
        var events = new Dictionary<string, int>(eventCount);
        for (int e = 0; e < eventCount; e++) { string n = ReadName(r); events[n] = r.ReadInt32(); }
        var contacts = r.ReadBytes(frames);
        if (contacts.Length != frames) throw new InvalidDataException($"{name}: truncated");
        return new Clip(name, fps, (flags & 1) != 0, bones, hips, rot, events, contacts);
    }

    public static void Write(string path, Clip clip) => File.WriteAllBytes(path, Write(clip));

    public static byte[] Write(Clip clip)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write((ushort)Version); w.Write((ushort)clip.Fps); w.Write((uint)clip.FrameCount);
            w.Write((ushort)clip.BoneCount); w.Write((ushort)(clip.Loop ? 1 : 0));
            foreach (var b in clip.Bones) WriteName(w, b);
            for (int f = 0; f < clip.FrameCount; f++)
            {
                var h = clip.Hips(f);
                w.Write(h.X); w.Write(h.Y); w.Write(h.Z);
                foreach (var q in clip.Rotations(f))
                {
                    Encode(q, out short a, out short b, out short c);
                    w.Write(a); w.Write(b); w.Write(c);
                }
            }
            w.Write((ushort)clip.Events.Count);
            foreach (var (n, f) in clip.Events) { WriteName(w, n); w.Write(f); }
            for (int f = 0; f < clip.FrameCount; f++) w.Write(clip.Contacts(f));
        }
        return ms.ToArray();
    }

    static string ReadName(BinaryReader r) => Encoding.UTF8.GetString(r.ReadBytes(r.ReadByte()));

    static void WriteName(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        if (b.Length > 255) throw new ArgumentException($"name too long: {s}");
        w.Write((byte)b.Length); w.Write(b);
    }

    /// <summary>Smallest-three encoding of a unit quaternion (see the class summary).</summary>
    public static void Encode(Quaternion q, out short a, out short b, out short c)
    {
        q = Quaternion.Normalize(q);
        Span<float> v = stackalloc float[] { q.X, q.Y, q.Z, q.W };
        int big = 0;
        for (int i = 1; i < 4; i++) if (MathF.Abs(v[i]) > MathF.Abs(v[big])) big = i;
        float sign = v[big] < 0 ? -1f : 1f;
        Span<int> s = stackalloc int[3];
        for (int i = 0, j = 0; i < 4; i++)
        {
            if (i == big) continue;
            float x = Math.Clamp(v[i] * sign / Sq, -1f, 1f);
            s[j++] = (int)MathF.Round(x * QMax);
        }
        a = (short)((s[0] << 1) | (big & 1));
        b = (short)((s[1] << 1) | ((big >> 1) & 1));
        c = (short)(s[2] << 1);
    }

    public static Quaternion Decode(short a, short b, short c)
    {
        int big = (a & 1) | ((b & 1) << 1);
        float x0 = (a >> 1) / (float)QMax * Sq, x1 = (b >> 1) / (float)QMax * Sq, x2 = (c >> 1) / (float)QMax * Sq;
        float w = MathF.Sqrt(MathF.Max(0f, 1f - x0 * x0 - x1 * x1 - x2 * x2));
        return big switch
        {
            0 => new Quaternion(w, x0, x1, x2),
            1 => new Quaternion(x0, w, x1, x2),
            2 => new Quaternion(x0, x1, w, x2),
            _ => new Quaternion(x0, x1, x2, w),
        };
    }
}
