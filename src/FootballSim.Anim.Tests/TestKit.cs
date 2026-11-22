using System.Numerics;
using FootballSim.Anim;

namespace FootballSim.Anim.Tests;

/// <summary>Shared fixtures: a small symmetric skeleton with synthetic clips, and the committed keeper clip library.</summary>
static class TestKit
{
    public static float Angle(Quaternion a, Quaternion b)
    {
        float d = MathF.Min(1f, MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))));
        return 2f * MathF.Acos(d);
    }

    /// <summary>Hips, spine and two legs (thigh, shin, foot, toe), mirror-symmetric, rest rotations identity: hips 1 m up, ankles 5 cm up.</summary>
    public static Skeleton Legs()
    {
        string[] names = { "Hips", "Spine", "LeftUpLeg", "LeftLeg", "LeftFoot", "LeftToeBase", "RightUpLeg", "RightLeg", "RightFoot", "RightToeBase" };
        int[] parents = { -1, 0, 0, 2, 3, 4, 0, 6, 7, 8 };
        Vector3[] offsets =
        {
            new(0, 1f, 0), new(0, 0.2f, 0),
            new(0.1f, -0.05f, 0), new(0, -0.45f, 0), new(0, -0.45f, 0), new(0, -0.03f, 0.12f),
            new(-0.1f, -0.05f, 0), new(0, -0.45f, 0), new(0, -0.45f, 0), new(0, -0.03f, 0.12f),
        };
        var rest = Enumerable.Repeat(Quaternion.Identity, names.Length).ToArray();
        return new Skeleton(names.Select(n => "mixamorig_" + n).ToArray(), parents, offsets, rest);
    }

    /// <summary>
    /// A synthetic walk: the hips travel along model +X (his left) at <paramref name="speed"/> m/s, the legs swing at <paramref name="hz"/>
    /// and the spine sways; a foot is in contact on its half of the cycle. Every frame differs.
    /// </summary>
    public static Clip Walk(Skeleton sk, string name, int frames, float speed, float hz, float phase = 0f, bool loop = false)
    {
        int b = sk.Count;
        var hips = new Vector3[frames];
        var rot = new Quaternion[frames * b];
        var contacts = new byte[frames];
        for (int f = 0; f < frames; f++)
        {
            float t = f / 60f;
            float c = MathF.Sin(2 * MathF.PI * hz * t + phase);
            hips[f] = new Vector3(speed * t, 0.98f + 0.01f * c, 0.02f * t);
            for (int k = 0; k < b; k++) rot[f * b + k] = Quaternion.Identity;
            rot[f * b + 0] = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.1f * MathF.Sin(t));
            rot[f * b + 1] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.05f * c + 0.01f * t);
            rot[f * b + sk.UpLeg[0]] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.25f * c);
            rot[f * b + sk.UpLeg[1]] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -0.25f * c);
            rot[f * b + sk.Leg[0]] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.2f + 0.2f * c);
            rot[f * b + sk.Leg[1]] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.2f - 0.2f * c);
            contacts[f] = (byte)(c >= 0 ? Clip.LeftFoot : Clip.RightFoot);
        }
        return new Clip(name, 60, loop, sk.Names, hips, rot, new Dictionary<string, int>(), contacts);
    }

    /// <summary>A standing clip: both feet planted, the body breathing a little.</summary>
    public static Clip Stand(Skeleton sk, string name, int frames)
    {
        int b = sk.Count;
        var hips = new Vector3[frames];
        var rot = new Quaternion[frames * b];
        var contacts = new byte[frames];
        for (int f = 0; f < frames; f++)
        {
            float t = f / 60f;
            hips[f] = new Vector3(0, 0.93f + 0.005f * MathF.Sin(3 * t), 0);
            for (int k = 0; k < b; k++) rot[f * b + k] = Quaternion.Identity;
            rot[f * b + 1] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.05f * MathF.Sin(2 * t));
            // knees bent forward (thigh forward, shin back): a crouch the legs can hold a small sideways nudge in
            rot[f * b + sk.UpLeg[0]] = rot[f * b + sk.UpLeg[1]] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.3f);
            rot[f * b + sk.Leg[0]] = rot[f * b + sk.Leg[1]] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.6f);
            rot[f * b + sk.Foot[0]] = rot[f * b + sk.Foot[1]] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.3f);
            contacts[f] = Clip.LeftFoot | Clip.RightFoot;
        }
        return new Clip(name, 60, false, sk.Names, hips, rot, new Dictionary<string, int>(), contacts);
    }

    public static string? KeeperClipDir()
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

    static ClipLibrary? _keeper;
    static readonly object Gate = new();

    /// <summary>The committed keeper clips (loaded once).</summary>
    public static ClipLibrary Keeper
    {
        get
        {
            lock (Gate)
            {
                if (_keeper != null) return _keeper;
                var dir = KeeperClipDir() ?? throw new InvalidOperationException("game/assets/anim/keeper not found");
                return _keeper = ClipLibrary.Load(dir);
            }
        }
    }
}
