using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FootballSim.Core;

/// <summary>The tags a capture can carry, in the order the capture panel lists them; the first is selected when it opens.</summary>
public static class CaptureTags
{
    public static readonly IReadOnlyList<string> All = new[]
    {
        "wrong foot", "bad aim", "shot power / accuracy", "touch / dribble", "keeper", "skill move", "animation", "other",
    };

    /// <summary>The tag as it appears in a file name: lower case, every run of other characters one hyphen ("shot-power-accuracy").</summary>
    public static string Slug(string tag)
    {
        var sb = new StringBuilder();
        foreach (char c in tag.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().TrimEnd('-');
    }
}

/// <summary>One line of <c>sessions/captures.jsonl</c>: everything needed to find a capture again by its id, its tag or its note.</summary>
public sealed class CaptureEntry
{
    public string Id { get; set; } = "";
    /// <summary>The session folder's name (<c>yyyyMMdd-HHmmss</c>).</summary>
    public string Session { get; set; } = "";
    public int Round { get; set; }
    public string Wall { get; set; } = "";
    public string Scenario { get; set; } = "";
    public string Attacker { get; set; } = "";
    public string Keeper { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Note { get; set; } = "";
    public int Tick { get; set; }
    public float Clock { get; set; }
    public bool Partial { get; set; }
    /// <summary>The replay file, relative to <c>sessions/</c> (<c>&lt;session&gt;/capture-C14-penalty-wrong-foot.json</c>).</summary>
    public string File { get; set; } = "";
}

/// <summary>
/// The capture index, <c>sessions/captures.jsonl</c>: one JSON line per capture across every session. Ids are <c>C&lt;n&gt;</c>, n one
/// more than the highest valid id in the file, so they never repeat. Reading never throws: a line that does not parse is skipped
/// with a warning and never lowers the next id below the highest valid one.
/// </summary>
public static class CaptureIndex
{
    public const string FileName = "captures.jsonl";

    static readonly JsonSerializerOptions LineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        // Notes stay readable (and searchable with grep) in the file: non-ASCII text is written as itself, not as \u escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>The number in an id such as <c>C14</c>, or null when it is not one.</summary>
    public static int? Number(string? id) =>
        id is { Length: > 1 } && (id[0] == 'C' || id[0] == 'c') && int.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0
            ? n : null;

    /// <summary>The entries of an index file's text, in file order; each unusable line adds a warning (1-based line number).</summary>
    public static List<CaptureEntry> Parse(string? text, out List<string> warnings)
    {
        warnings = new List<string>();
        var entries = new List<CaptureEntry>();
        if (string.IsNullOrEmpty(text)) return entries;
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;
            CaptureEntry? e = null;
            try { e = JsonSerializer.Deserialize<CaptureEntry>(line, LineOptions); }
            catch (JsonException ex) { warnings.Add($"{FileName} line {i + 1}: not valid JSON ({ex.Message}); skipped"); continue; }
            if (e == null || Number(e.Id) == null) { warnings.Add($"{FileName} line {i + 1}: no valid id; skipped"); continue; }
            entries.Add(e);
        }
        return entries;
    }

    /// <summary>The id the next capture gets: one more than the highest valid id in the index (C1 for a missing or empty file).</summary>
    public static string NextId(string? text, out List<string> warnings)
    {
        int max = 0;
        foreach (var e in Parse(text, out warnings)) max = Math.Max(max, Number(e.Id)!.Value);
        return $"C{max + 1}";
    }

    /// <summary>The entry with this id (any case), or null.</summary>
    public static CaptureEntry? Find(string? text, string id, out List<string> warnings)
    {
        var entries = Parse(text, out warnings);
        int? want = Number(id);
        return want == null ? null : entries.LastOrDefault(e => Number(e.Id) == want);
    }

    /// <summary>One index line (no newline).</summary>
    public static string Line(CaptureEntry e) => JsonSerializer.Serialize(e, LineOptions);

    /// <summary>The capture's replay file name, <c>capture-C14-&lt;scenario&gt;-&lt;tag slug&gt;.json</c>.</summary>
    public static string ReplayFileName(string id, string scenario, string tag) => $"capture-{id}-{scenario}-{CaptureTags.Slug(tag)}.json";

    /// <summary>The next id from the index file at <paramref name="path"/>; a missing or unreadable file counts as empty (with a warning).</summary>
    public static string NextIdFromFile(string path, out List<string> warnings)
    {
        string? text = null;
        string? error = null;
        try { if (System.IO.File.Exists(path)) text = System.IO.File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = ex.Message; }
        string id = NextId(text, out warnings);
        if (error != null) warnings.Insert(0, $"{FileName}: unreadable ({error}); counting from C1");
        return id;
    }

    /// <summary>Appends one line to the index file at <paramref name="path"/>. Returns an error message, or null.</summary>
    public static string? Append(string path, CaptureEntry e)
    {
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } dir) Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(path, Line(e) + "\n");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
    }
}
