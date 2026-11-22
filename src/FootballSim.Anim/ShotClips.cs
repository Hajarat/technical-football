using System.Numerics;
using System.Text.Json;

namespace FootballSim.Anim;

/// <summary>
/// One shot clip with its clips.json labels (game/assets/anim/attacker/shots; tools/anim/shots.py and shot_author.py): the kind
/// of kick, the striking foot, the entry, and the ball-out angle bin it was made for.
/// </summary>
public sealed class ShotClip
{
    /// <summary>The events every shot clip carries, in time order.</summary>
    public static readonly string[] Knots = { "start", "takeoff", "backswing", "plant", "contact", "end" };

    public Clip Clip { get; }
    /// <summary>"instep" (the captured kicks: the power kick's 0-degree bin), "power", "finesse", "chip" or "outside".</summary>
    public string Kind { get; }
    /// <summary>The striking foot: 0 left, 1 right.</summary>
    public int Foot { get; }
    /// <summary>The running entry (else from standing).</summary>
    public bool Run { get; }
    /// <summary>
    /// The ball-out angle the clip is made for and the angles it covers, degrees against the approach, positive toward the
    /// striking foot's own side (his right for the right foot).
    /// </summary>
    public float BinDeg { get; }
    public float BinLo { get; }
    public float BinHi { get; }
    /// <summary>Seconds of each event in <see cref="Knots"/>, then the clip's last frame.</summary>
    public float[] E { get; } = new float[7];
    /// <summary>The hips' smoothed ground track per frame (x, z; model space, the hips over the origin at the contact).</summary>
    public Vector2[] Root { get; }
    /// <summary>The striking ankle's direction of travel at the contact (model space, flat).</summary>
    public Vector3 KickDir { get; }
    /// <summary>
    /// The yaw of the direction the ball leaves in against the clip's own forward (+Z), radians toward his left (+X): the bin's
    /// angle for an authored clip, the striking ankle's travel for a captured one.
    /// </summary>
    public float OutYaw { get; }
    /// <summary>The ball's centre at the contact in the clip's own space (an authored clip's ball track), or null for a captured kick.</summary>
    public Vector3? BallAtContact { get; }
    public bool Authored { get; }

    public ShotClip(Clip clip, string kind, int foot, bool run, float binDeg, float binLo, float binHi, Vector2[] root, Vector3 kickDir,
        Vector3? ballAtContact, bool authored)
    {
        Clip = clip; Kind = kind; Foot = foot; Run = run; BinDeg = binDeg; BinLo = binLo; BinHi = binHi; Root = root; KickDir = kickDir;
        BallAtContact = ballAtContact; Authored = authored;
        for (int k = 0; k < Knots.Length; k++)
        {
            if (!clip.Events.TryGetValue(Knots[k], out int fr)) throw new InvalidDataException($"{clip.Name}: no event {Knots[k]}");
            E[k] = fr / (float)clip.Fps;
        }
        E[6] = (clip.FrameCount - 1) / (float)clip.Fps;
        for (int k = 1; k <= 6; k++)
            if (E[k] <= E[k - 1]) throw new InvalidDataException($"{clip.Name}: events out of order");
        // toward the striking foot's own side is toward his right for the right foot (model -X), his left for the left foot
        OutYaw = authored ? (foot == 1 ? -1f : 1f) * binDeg * MathF.PI / 180f : MathF.Atan2(kickDir.X, kickDir.Z);
    }

    /// <summary>Half the width of the bin, degrees.</summary>
    public float HalfWidthDeg => 0.5f * (BinHi - BinLo);

    public Vector2 RootAt(float seconds)
    {
        float f = Math.Clamp(seconds * Clip.Fps, 0f, Root.Length - 1);
        int a = (int)MathF.Floor(f), b = Math.Min(a + 1, Root.Length - 1);
        return Vector2.Lerp(Root[a], Root[b], f - a);
    }

    public byte Contacts(float seconds) => Clip.Contacts(Math.Clamp((int)MathF.Round(seconds * Clip.Fps), 0, Clip.FrameCount - 1));
}

/// <summary>What <see cref="ShotClipLibrary.Pick"/> found: the clip, and whether it is a stand-in for a kind or bin the library lacks.</summary>
public readonly record struct ShotPick(ShotClip Clip, bool Fallback);

/// <summary>The shot clips, picked by kind, entry, striking foot and the ball-out angle.</summary>
public sealed class ShotClipLibrary
{
    public const string Instep = "instep", Power = "power", Finesse = "finesse", Chip = "chip", Outside = "outside";

    public Skeleton Skeleton { get; }
    public IReadOnlyList<ShotClip> Clips { get; }
    /// <summary>The clip folder as the chooser reads it (every clip listed, in file order).</summary>
    public ClipLibrary Library { get; }

    public ShotClipLibrary(ClipLibrary library, IReadOnlyList<ShotClip> clips) { Library = library; Skeleton = library.Skeleton; Clips = clips; }

    /// <summary>Parses the shots' clips.json; <paramref name="readFile"/> returns a listed .kclip file's bytes.</summary>
    public static ShotClipLibrary Parse(string json, Func<string, byte[]> readFile)
    {
        var lib = ClipLibrary.Parse(json, readFile);
        using var doc = JsonDocument.Parse(json);
        var list = new List<ShotClip>();
        int i = 0;
        foreach (var c in doc.RootElement.GetProperty("clips").EnumerateArray())
        {
            var e = lib.Clips[i++];
            if (e.Verdict == ClipVerdict.Reject || (e.Set != "shot" && !e.Set.StartsWith("shot:"))) continue;
            // a file from before the kinds: every clip a captured instep kick for the 0-degree bin
            string kind = c.TryGetProperty("kind", out var kd) ? kd.GetString() ?? Instep : Instep;
            float bin = c.TryGetProperty("bin_deg", out var bd) ? bd.GetSingle() : 0f;
            float lo = -22.5f, hi = 22.5f;
            if (c.TryGetProperty("bin_range", out var br)) { lo = br[0].GetSingle(); hi = br[1].GetSingle(); }
            var root = c.GetProperty("root").EnumerateArray().Select(r => new Vector2(r[0].GetSingle(), r[1].GetSingle())).ToArray();
            var k = c.GetProperty("kick_dir");
            bool authored = c.TryGetProperty("source", out var src) && src.GetString() == "authored";
            Vector3? ball = null;
            if (authored && c.TryGetProperty("ball", out var b) && b.TryGetProperty("track", out var tr) && e.Clip.Events.TryGetValue("contact", out int cf))
            {
                var p = tr[Math.Clamp(cf, 0, tr.GetArrayLength() - 1)];
                ball = new Vector3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle());
            }
            list.Add(new ShotClip(e.Clip, kind, c.GetProperty("foot").GetString() == "right" ? 1 : 0, c.GetProperty("entry").GetString() == "run",
                bin, lo, hi, root, new Vector3(k[0].GetSingle(), k[1].GetSingle(), k[2].GetSingle()), ball, authored));
        }
        return new ShotClipLibrary(lib, list);
    }

    /// <summary>The clip kind a shot is drawn with: the outside of the foot whatever the shot, else by the shot's own kind.</summary>
    public static string KindFor(bool outside, bool chip, bool finesse) => outside ? Outside : chip ? Chip : finesse ? Finesse : Power;

    /// <summary>
    /// The clip of <paramref name="kind"/> for the entry and striking foot whose bin holds <paramref name="angleDeg"/> (the ball-out
    /// angle against his facing, positive toward the striking foot's own side). The captured kicks count as the power kick's
    /// 0-degree bin. A bin that reaches straight behind (-180) holds the same angles from the other side too: a ball leaving
    /// 170 degrees toward the foot's own side is the kick struck behind him, as one 170 degrees across is. When the kind has no
    /// clip for that angle (or none at all), the power kick's bin nearest the angle stands in
    /// (<see cref="ShotPick.Fallback"/>), then the other entry's; null only when the foot has no clip at all.
    /// </summary>
    public ShotPick? Pick(string kind, bool run, int foot, float angleDeg)
    {
        angleDeg = Math.Clamp(angleDeg, -180f, 180f);
        // How far the angle is outside a clip's bin, degrees (0 inside), the bin behind him counted from either side.
        float Outside(ShotClip c)
        {
            float d = angleDeg < c.BinLo ? c.BinLo - angleDeg : angleDeg > c.BinHi ? angleDeg - c.BinHi : 0f;
            if (c.BinLo > -180f) return d;
            float m = -angleDeg;
            return MathF.Min(d, m < c.BinLo ? c.BinLo - m : m > c.BinHi ? m - c.BinHi : 0f);
        }
        ShotClip? In(string k, bool r)
        {
            foreach (var c in Clips)
                if (c.Foot == foot && c.Run == r && Is(c, k) && Outside(c) <= 0f) return c;
            return null;
        }
        ShotClip? Nearest(bool r)
        {
            ShotClip? best = null;
            float bestD = float.MaxValue;
            foreach (var c in Clips)
            {
                if (c.Foot != foot || c.Run != r || !Is(c, Power)) continue;
                float d = Outside(c);
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }
        if (In(kind, run) is { } exact) return new ShotPick(exact, false);
        if (Nearest(run) is { } near) return new ShotPick(near, true);
        if (In(kind, !run) is { } other) return new ShotPick(other, true);
        return Nearest(!run) is { } last ? new ShotPick(last, true) : null;
    }

    static bool Is(ShotClip c, string kind) => c.Kind == kind || kind == Power && c.Kind == Instep;
}
