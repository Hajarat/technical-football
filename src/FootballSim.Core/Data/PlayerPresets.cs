using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FootballSim.Core;

/// <summary>One saved player in the editor's store: a generated name, when it was saved, and the sheet.</summary>
public sealed class PlayerPreset
{
    public string Name { get; set; } = "";
    /// <summary>Local time of the save, <c>yyyy-MM-ddTHH:mm:ss</c>.</summary>
    public string SavedAt { get; set; } = "";
    public PlayerSheet Sheet { get; set; } = new();
}

/// <summary>
/// The player editor's store (the game keeps it at <c>user://player_editor.json</c>): the last Custom player, whether it was the
/// attacker at quit, the shipped attacker it was last copied from, and ten preset slots. Parsing never throws: a missing, broken or
/// wrong-version file gives an empty store and a warning, and a bad slot becomes empty while the others still load.
/// </summary>
public sealed class PlayerPresets
{
    public const int FileVersion = 1;
    public const int SlotCount = 10;
    /// <summary>The id and name the edited player plays under. It wins over a data/players file with the same id.</summary>
    public const string CustomId = "custom", CustomName = "Custom";
    const string Label = "player_editor.json";

    /// <summary>Custom was the attacker at quit: the game selects it again on launch.</summary>
    public bool Active { get; set; }
    /// <summary>The shipped attacker Custom was last copied from (empty when unknown).</summary>
    public string Base { get; set; } = "";
    /// <summary>The last Custom player, or null when the editor has never made one.</summary>
    public PlayerSheet? Custom { get; set; }
    /// <summary>Always <see cref="SlotCount"/> entries; null is an empty slot.</summary>
    public PlayerPreset?[] Slots { get; } = new PlayerPreset?[SlotCount];

    /// <summary>Gives a sheet the Custom id and name, so it plays and is recorded as Custom.</summary>
    public static PlayerSheet AsCustom(PlayerSheet s)
    {
        var c = s.Clone();
        c.Id = CustomId;
        c.Name = CustomName;
        return c;
    }

    /// <summary>True when two sheets have the same ratings, body, foot and traits (id and name are ignored).</summary>
    public static bool SameValues(PlayerSheet a, PlayerSheet b) => AsCustom(a).ToJson() == AsCustom(b).ToJson();

    /// <summary>The generated preset name, <c>from &lt;base&gt; · HH:MM</c>.</summary>
    public static string PresetName(string baseId, DateTime when) =>
        $"from {(baseId.Length > 0 ? baseId : CustomId)} · {when.ToString("HH:mm", CultureInfo.InvariantCulture)}";

    /// <summary>Saves a copy of <paramref name="sheet"/> into a slot (0-based), replacing what was there.</summary>
    public void Save(int slot, PlayerSheet sheet, string baseId, DateTime when) => Slots[slot] = new PlayerPreset
    {
        Name = PresetName(baseId, when),
        SavedAt = when.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        Sheet = AsCustom(sheet),
    };

    /// <summary>A copy of a slot's sheet, or null for an empty slot.</summary>
    public PlayerSheet? Load(int slot) => Slots[slot] is { } p ? AsCustom(p.Sheet) : null;

    public void Clear(int slot) => Slots[slot] = null;

    /// <summary>True when the sheet's values equal one of the presets'.</summary>
    public bool MatchesAPreset(PlayerSheet s)
    {
        foreach (var p in Slots) if (p != null && SameValues(p.Sheet, s)) return true;
        return false;
    }

    // ---- file ----

    /// <summary>The store as indented JSON. Sheets are written in the data/players format.</summary>
    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", FileVersion);
            w.WriteBoolean("active", Active);
            w.WriteString("base", Base);
            w.WritePropertyName("custom");
            if (Custom != null) WriteSheet(w, Custom); else w.WriteNullValue();
            w.WriteStartArray("presets");
            foreach (var p in Slots)
            {
                if (p == null) { w.WriteNullValue(); continue; }
                w.WriteStartObject();
                w.WriteString("name", p.Name);
                w.WriteString("saved_at", p.SavedAt);
                w.WritePropertyName("sheet");
                WriteSheet(w, p.Sheet);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    static void WriteSheet(Utf8JsonWriter w, PlayerSheet s)
    {
        using var doc = JsonDocument.Parse(AsCustom(s).ToJson());
        doc.RootElement.WriteTo(w);
    }

    /// <summary>
    /// Reads a store. Null text (no file) or anything unusable gives an empty store; every problem is a warning, never an exception.
    /// Each sheet goes through <see cref="DataLoader.ParsePlayer"/>, so its values are clamped as a player file's are.
    /// </summary>
    public static PlayerPresets Parse(string? json, out List<LoadWarning> warnings)
    {
        warnings = new List<LoadWarning>();
        var store = new PlayerPresets();
        if (json == null) { warnings.Add(new LoadWarning(Label, "", "no file yet; starting empty")); return store; }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException e) { warnings.Add(new LoadWarning(Label, "", $"invalid JSON ({e.Message}); starting empty")); return store; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { warnings.Add(new LoadWarning(Label, "", "not a JSON object; starting empty")); return store; }
            if (!root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int ver) || ver != FileVersion)
            {
                warnings.Add(new LoadWarning(Label, "version", $"expected {FileVersion}, got {(root.TryGetProperty("version", out var vv) ? vv.GetRawText() : "nothing")}; starting empty"));
                return store;
            }
            if (root.TryGetProperty("active", out var a))
            {
                if (a.ValueKind is JsonValueKind.True or JsonValueKind.False) store.Active = a.GetBoolean();
                else warnings.Add(new LoadWarning(Label, "active", "not true or false; using false"));
            }
            if (root.TryGetProperty("base", out var b) && b.ValueKind == JsonValueKind.String) store.Base = b.GetString() ?? "";
            if (root.TryGetProperty("custom", out var c) && c.ValueKind != JsonValueKind.Null)
                store.Custom = ParseSheet(c, "custom", warnings);
            if (store.Custom == null) store.Active = false;

            if (root.TryGetProperty("presets", out var ps) && ps.ValueKind != JsonValueKind.Null)
            {
                if (ps.ValueKind != JsonValueKind.Array) warnings.Add(new LoadWarning(Label, "presets", "not a list; every slot empty"));
                else
                {
                    int i = 0;
                    foreach (var e in ps.EnumerateArray())
                    {
                        if (i >= SlotCount) { warnings.Add(new LoadWarning(Label, "presets", $"{ps.GetArrayLength()} slots; only the first {SlotCount} are kept")); break; }
                        store.Slots[i] = ParsePreset(e, $"presets[{i}]", warnings);
                        i++;
                    }
                }
            }
            return store;
        }
    }

    static PlayerPreset? ParsePreset(JsonElement e, string field, List<LoadWarning> warnings)
    {
        if (e.ValueKind == JsonValueKind.Null) return null;
        if (e.ValueKind != JsonValueKind.Object) { warnings.Add(new LoadWarning(Label, field, "not an object; slot left empty")); return null; }
        if (!e.TryGetProperty("sheet", out var s) || s.ValueKind == JsonValueKind.Null)
        {
            warnings.Add(new LoadWarning(Label, field, "no sheet; slot left empty"));
            return null;
        }
        var sheet = ParseSheet(s, field + ".sheet", warnings);
        if (sheet == null) return null;
        string Str(string name) => e.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";
        return new PlayerPreset { Name = Str("name"), SavedAt = Str("saved_at"), Sheet = sheet };
    }

    static PlayerSheet? ParseSheet(JsonElement e, string field, List<LoadWarning> warnings)
    {
        if (e.ValueKind != JsonValueKind.Object) { warnings.Add(new LoadWarning(Label, field, "not an object; left empty")); return null; }
        var r = DataLoader.ParsePlayer(e.GetRawText(), $"{Label}: {field}", CustomId);
        warnings.AddRange(r.Warnings);
        if (!r.Ok) { warnings.Add(new LoadWarning(Label, field, (r.Error ?? "unusable") + "; left empty")); return null; }
        return AsCustom(r.Value!);
    }

    /// <summary>Reads a store from a file; a missing or unreadable file gives an empty store and a warning.</summary>
    public static PlayerPresets Load(string path, out List<LoadWarning> warnings)
    {
        string? text = null;
        string? error = null;
        try { if (File.Exists(path)) text = File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error = e.Message; }
        var store = Parse(text, out warnings);
        if (error != null) warnings.Insert(0, new LoadWarning(Label, "", $"unreadable ({error}); starting empty"));
        return store;
    }

    /// <summary>
    /// Writes the store to a temporary file beside <paramref name="path"/> and renames it over the old one, so a write cut short (the
    /// game quitting) leaves the old file whole. Returns an error message, or null.
    /// </summary>
    public string? Save(string path)
    {
        string tmp = path + ".tmp";
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } dir) Directory.CreateDirectory(dir);
            File.WriteAllText(tmp, ToJson());
            File.Move(tmp, path, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return e.Message;
        }
    }
}

/// <summary>What an editor row edits.</summary>
public enum PlayerEditKind { Rating, Foot, Trait, Height, Weight }

/// <summary>
/// One editable field of an outfield player sheet, as the player editor lists it. <see cref="Step"/> changes the field by whole
/// steps and clamps it exactly as <see cref="DataLoader.ParsePlayer"/> clamps a player file.
/// </summary>
public sealed record PlayerEditRow(PlayerEditKind Kind, string Name, PlayerTrait Trait = PlayerTrait.None)
{
    /// <summary>
    /// The rows in order: the outfield ratings (<paramref name="ratingNames"/> minus <paramref name="keeperNames"/>, in file order),
    /// preferred_foot, one row per trait, height_m and weight_kg. Defaults to the sheet's own lists, so a rating added there gets a row.
    /// </summary>
    public static IReadOnlyList<PlayerEditRow> All(IReadOnlyList<string>? ratingNames = null, IReadOnlyList<string>? keeperNames = null)
    {
        ratingNames ??= PlayerSheet.RatingNames;
        keeperNames ??= PlayerSheet.KeeperRatingNames;
        var rows = new List<PlayerEditRow>();
        foreach (var n in ratingNames) if (!keeperNames.Contains(n)) rows.Add(new PlayerEditRow(PlayerEditKind.Rating, n));
        rows.Add(new PlayerEditRow(PlayerEditKind.Foot, "preferred_foot"));
        foreach (var t in Enum.GetValues<PlayerTrait>())
        {
            if (t == PlayerTrait.None) continue;
            string name = t.ToString();
            foreach (var (n, tr) in PlayerSheet.TraitNames) if (tr == t) name = n;
            rows.Add(new PlayerEditRow(PlayerEditKind.Trait, name, t));
        }
        rows.Add(new PlayerEditRow(PlayerEditKind.Height, "height_m"));
        rows.Add(new PlayerEditRow(PlayerEditKind.Weight, "weight_kg"));
        return rows;
    }

    /// <summary>
    /// Changes the field by <paramref name="steps"/>: a rating by 1 per step (1 to 99), height by 0.01 m (1.60 to 2.05), weight by 1 kg
    /// (55 to 100); foot and traits flip on an odd number of steps. Returns true if the value changed.
    /// </summary>
    public bool Step(PlayerSheet s, int steps)
    {
        string before = Value(s);
        switch (Kind)
        {
            case PlayerEditKind.Rating: s.SetRating(Name, s.GetRating(Name) + steps); break;
            case PlayerEditKind.Foot: if (steps % 2 != 0) s.PreferredFoot = s.PreferredFoot == Foot.Left ? Foot.Right : Foot.Left; break;
            case PlayerEditKind.Trait: if (steps % 2 != 0) s.Traits ^= Trait; break;
            case PlayerEditKind.Height:
                int cm = Math.Clamp((int)MathF.Round(s.HeightM * 100f) + steps, (int)MathF.Round(PlayerSheet.HeightMin * 100f), (int)MathF.Round(PlayerSheet.HeightMax * 100f));
                s.HeightM = cm / 100f;
                break;
            case PlayerEditKind.Weight:
                s.WeightKg = Math.Clamp(MathF.Round(s.WeightKg) + steps, PlayerSheet.WeightMin, PlayerSheet.WeightMax);
                break;
        }
        return Value(s) != before;
    }

    /// <summary>The field's value as the editor shows it.</summary>
    public string Value(PlayerSheet s) => Kind switch
    {
        PlayerEditKind.Rating => s.GetRating(Name).ToString(CultureInfo.InvariantCulture),
        PlayerEditKind.Foot => s.PreferredFoot == Foot.Left ? "left" : "right",
        PlayerEditKind.Trait => s.Has(Trait) ? "on" : "off",
        PlayerEditKind.Height => s.HeightM.ToString("0.00", CultureInfo.InvariantCulture),
        _ => s.WeightKg.ToString("0", CultureInfo.InvariantCulture),
    };
}
