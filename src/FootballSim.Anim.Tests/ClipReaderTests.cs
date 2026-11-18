using System.Numerics;
using System.Text.Json;
using FootballSim.Anim;

namespace FootballSim.Anim.Tests;

/// <summary>The .kclip format: a written clip reads back the same, rotations within the smallest-three error, and the committed keeper clips load.</summary>
public class ClipReaderTests
{
    static float Angle(Quaternion a, Quaternion b)
    {
        float d = MathF.Min(1f, MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))));
        return 2f * MathF.Acos(d);
    }

    static Quaternion RandomRotation(Random rng)
    {
        // uniform over rotations (Shoemake)
        double u1 = rng.NextDouble(), u2 = rng.NextDouble() * 2 * Math.PI, u3 = rng.NextDouble() * 2 * Math.PI;
        double a = Math.Sqrt(1 - u1), b = Math.Sqrt(u1);
        return new Quaternion((float)(a * Math.Sin(u2)), (float)(a * Math.Cos(u2)), (float)(b * Math.Sin(u3)), (float)(b * Math.Cos(u3)));
    }

    [Fact]
    public void SmallestThreeErrorIsUnderAMilliradian()
    {
        var rng = new Random(7);
        float worst = 0;
        for (int i = 0; i < 20000; i++)
        {
            var q = RandomRotation(rng);
            ClipReader.Encode(q, out short a, out short b, out short c);
            worst = MathF.Max(worst, Angle(q, ClipReader.Decode(a, b, c)));
        }
        // edge cases: identity, half turns, a quaternion with two equal largest components, a negative largest component
        foreach (var q in new[] { Quaternion.Identity, new Quaternion(1, 0, 0, 0), new Quaternion(0, 0, 0, -1),
                     Quaternion.Normalize(new Quaternion(0.5f, 0.5f, 0.5f, 0.5f)), Quaternion.Normalize(new Quaternion(-0.7f, 0.1f, 0.7f, 0.1f)) })
        {
            ClipReader.Encode(q, out short a, out short b, out short c);
            worst = MathF.Max(worst, Angle(q, ClipReader.Decode(a, b, c)));
        }
        Assert.True(worst < 0.001f, $"worst error {worst} rad");
    }

    [Fact]
    public void WrittenClipReadsBack()
    {
        var rng = new Random(3);
        string[] bones = { "mixamorig_Hips", "mixamorig_Spine", "mixamorig_LeftUpLeg" };
        int frames = 40;
        var hips = new Vector3[frames];
        var rot = new Quaternion[frames * bones.Length];
        var contacts = new byte[frames];
        for (int f = 0; f < frames; f++)
        {
            hips[f] = new Vector3(f * 0.01f, 1.0f - f * 0.002f, -f * 0.03f);
            for (int k = 0; k < bones.Length; k++) rot[f * bones.Length + k] = RandomRotation(rng);
            contacts[f] = (byte)(f % 4);
        }
        var events = new Dictionary<string, int> { ["takeoff"] = 5, ["contact"] = 22 };
        var clip = new Clip("test", 60, true, bones, hips, rot, events, contacts);

        var back = ClipReader.Read(ClipReader.Write(clip), "test");

        Assert.Equal(60, back.Fps);
        Assert.True(back.Loop);
        Assert.Equal(bones, back.Bones);
        Assert.Equal(frames, back.FrameCount);
        Assert.Equal(events, back.Events);
        for (int f = 0; f < frames; f++)
        {
            Assert.Equal(hips[f], back.Hips(f));
            Assert.Equal(contacts[f], back.Contacts(f));
            for (int k = 0; k < bones.Length; k++)
                Assert.True(Angle(rot[f * bones.Length + k], back.Rotations(f)[k]) < 0.001f, $"frame {f} bone {k}");
        }
    }

    [Fact]
    public void SampleInterpolatesAndClamps()
    {
        string[] bones = { "b" };
        var a = Quaternion.Identity;
        var b = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f);
        var clip = new Clip("s", 10, false, bones, new[] { Vector3.Zero, Vector3.UnitX }, new[] { a, b }, new Dictionary<string, int>(), new byte[2]);
        Span<Quaternion> q = stackalloc Quaternion[1];
        var h = clip.Sample(0.05f, q);
        Assert.Equal(0.5f, h.X, 4);
        Assert.Equal(0.5f, Angle(a, q[0]), 3);
        h = clip.Sample(5f, q);
        Assert.Equal(1f, h.X, 4);
        Assert.Equal(1f, Angle(a, q[0]), 3);
    }

    static string? KeeperClipDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var d = Path.Combine(dir.FullName, "game", "assets", "anim", "keeper");
            if (File.Exists(Path.Combine(d, "clips.json"))) return d;
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void CommittedKeeperClipsLoadAndMatchTheirIndex()
    {
        var dir = KeeperClipDir();
        Assert.NotNull(dir);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!, "clips.json")));
        var bones = doc.RootElement.GetProperty("bones").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(22, bones.Length);
        int n = 0;
        foreach (var entry in doc.RootElement.GetProperty("clips").EnumerateArray())
        {
            var clip = ClipReader.Read(Path.Combine(dir!, entry.GetProperty("file").GetString()!));
            Assert.Equal(entry.GetProperty("frame_count").GetInt32(), clip.FrameCount);
            Assert.Equal(60, clip.Fps);
            Assert.Equal(bones, clip.Bones);
            foreach (var ev in entry.GetProperty("events").EnumerateObject())
            {
                Assert.Equal(ev.Value.GetInt32(), clip.Events[ev.Name]);
                Assert.InRange(clip.Events[ev.Name], 0, clip.FrameCount - 1);
            }
            for (int f = 0; f < clip.FrameCount; f++)
            {
                var h = clip.Hips(f);
                Assert.True(float.IsFinite(h.X) && float.IsFinite(h.Y) && float.IsFinite(h.Z));
                foreach (var q in clip.Rotations(f)) Assert.InRange(q.Length(), 0.999f, 1.001f);
            }
            n++;
        }
        Assert.True(n > 0);
    }
}
