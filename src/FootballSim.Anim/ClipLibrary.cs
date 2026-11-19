using System.Numerics;
using System.Text.Json;

namespace FootballSim.Anim;

/// <summary>The audition's verdict on a clip (clips.toml "verdict"). Rejected clips stay on disk but never enter a pose database.</summary>
public enum ClipVerdict { Keep, Trim, Reject }

/// <summary>One clip with its clips.json labels: chooser set, side, audition verdict and tags (e.g. "idle").</summary>
public sealed record ClipEntry(Clip Clip, string Set, string Side, ClipVerdict Verdict, IReadOnlyList<string>? Tags = null)
{
    public bool HasTag(string tag) => Tags != null && Tags.Contains(tag);
}

/// <summary>
/// A clip folder: clips.json (written by tools/anim/retarget.py: the skeleton and, per clip, its file, chooser set, side and verdict)
/// and the .kclip files it lists. No Godot: the game hands it the folder's path (or the file bytes).
/// </summary>
public sealed class ClipLibrary
{
    public Skeleton Skeleton { get; }
    public IReadOnlyList<ClipEntry> Clips { get; }

    public ClipLibrary(Skeleton skeleton, IReadOnlyList<ClipEntry> clips)
    {
        Skeleton = skeleton; Clips = clips;
        foreach (var c in clips)
        {
            if (c.Clip.BoneCount != skeleton.Count) throw new InvalidDataException($"{c.Clip.Name}: {c.Clip.BoneCount} bones, the skeleton has {skeleton.Count}");
            for (int k = 0; k < skeleton.Count; k++)
                if (c.Clip.Bones[k] != skeleton.Names[k]) throw new InvalidDataException($"{c.Clip.Name}: bone {k} is {c.Clip.Bones[k]}, the skeleton's is {skeleton.Names[k]}");
        }
    }

    /// <summary>Reads clips.json and every clip it lists from <paramref name="folder"/>.</summary>
    public static ClipLibrary Load(string folder) =>
        Parse(File.ReadAllText(Path.Combine(folder, "clips.json")), file => File.ReadAllBytes(Path.Combine(folder, file)));

    /// <summary>Parses clips.json text; <paramref name="readFile"/> returns a listed .kclip file's bytes.</summary>
    public static ClipLibrary Parse(string json, Func<string, byte[]> readFile)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var names = root.GetProperty("bones").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var sk = root.GetProperty("skeleton");
        var parents = sk.GetProperty("parents").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        var offsets = sk.GetProperty("offsets").EnumerateArray().Select(e => new Vector3(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle())).ToArray();
        var rest = sk.GetProperty("rest").EnumerateArray()
            .Select(e => Quaternion.Normalize(new Quaternion(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle(), e[3].GetSingle()))).ToArray();
        var skeleton = new Skeleton(names, parents, offsets, rest);
        var clips = new List<ClipEntry>();
        foreach (var c in root.GetProperty("clips").EnumerateArray())
        {
            string file = c.GetProperty("file").GetString()!;
            var clip = ClipReader.Read(readFile(file), Path.GetFileNameWithoutExtension(file));
            string set = c.TryGetProperty("set", out var s) ? s.GetString() ?? "" : "";
            string side = c.TryGetProperty("side", out var sd) ? sd.GetString() ?? "none" : "none";
            var verdict = c.TryGetProperty("verdict", out var v) ? ParseVerdict(v.GetString(), clip.Name) : ClipVerdict.Keep;
            var tags = c.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array
                ? tg.EnumerateArray().Select(e => e.GetString() ?? "").ToArray() : Array.Empty<string>();
            clips.Add(new ClipEntry(clip, set, side, verdict, tags));
        }
        return new ClipLibrary(skeleton, clips);
    }

    static ClipVerdict ParseVerdict(string? v, string clip) => v switch
    {
        "keep" or null => ClipVerdict.Keep,
        "trim" => ClipVerdict.Trim,
        "reject" => ClipVerdict.Reject,
        _ => throw new InvalidDataException($"{clip}: unknown verdict '{v}'"),
    };
}
