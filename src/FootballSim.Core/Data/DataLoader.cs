using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FootballSim.Core;

/// <summary>One note about a value that was missing, clamped or ignored while loading a file.</summary>
public readonly record struct LoadWarning(string File, string Field, string Message)
{
    public override string ToString() => $"{File}: {Field}: {Message}";
}

/// <summary>The outcome of loading a data file. Loading never throws; a bad file sets <see cref="Error"/>.</summary>
public sealed class LoadResult<T> where T : class
{
    public T? Value { get; init; }
    public IReadOnlyList<LoadWarning> Warnings { get; init; } = Array.Empty<LoadWarning>();
    /// <summary>Set when the file could not be used (unreadable or not valid JSON). For tuning, <see cref="Value"/> then holds the built-in defaults.</summary>
    public string? Error { get; init; }
    public bool Ok => Value != null && Error == null;
}

/// <summary>Loads player, tuning and scenario files from JSON.</summary>
public static class DataLoader
{
    /// <summary>JSON options shared by tuning, scenarios and replays: snake_case names, comments and trailing commas tolerated.</summary>
    public static JsonSerializerOptions Options { get; } = MakeOptions();

    static JsonSerializerOptions MakeOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = true,
        IncludeFields = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    static readonly JsonSerializerOptions CompactOptions = new(MakeOptions()) { WriteIndented = false };

    // ---- players ----

    /// <summary>Loads a player sheet from a file.</summary>
    public static LoadResult<PlayerSheet> LoadPlayer(string path)
    {
        var text = ReadFile(path, out var err);
        if (text == null) return new LoadResult<PlayerSheet> { Error = err };
        return ParsePlayer(text, path, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Parses player JSON. Missing ratings become 50, out-of-range values are clamped; each change is a warning.</summary>
    public static LoadResult<PlayerSheet> ParsePlayer(string json, string fileLabel = "player", string? defaultId = null)
    {
        var warnings = new List<LoadWarning>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException e) { return new LoadResult<PlayerSheet> { Error = JsonError(fileLabel, e) }; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new LoadResult<PlayerSheet> { Error = $"{fileLabel}: expected a JSON object" };

            var s = new PlayerSheet();
            s.Id = ReadString(root, "id", fileLabel, warnings, defaultId ?? "unknown", warn: false);
            s.Name = ReadString(root, "name", fileLabel, warnings, s.Id, warn: false);
            foreach (var n in PlayerSheet.RatingNames)
            {
                // skill_moves came later than the other ratings: a sheet without it uses its dribbling, without a warning.
                if (n == "skill_moves" && !root.TryGetProperty(n, out _)) { s.SkillMoves = s.Dribbling; continue; }
                bool star = n == "weak_foot";
                int lo = 1, hi = star ? 5 : 99, def = star ? 3 : PlayerSheet.RatingDefault;
                s.SetRating(n, (int)MathF.Round(ReadNumber(root, n, fileLabel, warnings, def, lo, hi)));
            }
            s.HeightM = ReadNumber(root, "height_m", fileLabel, warnings, 1.80f, PlayerSheet.HeightMin, PlayerSheet.HeightMax);
            s.WeightKg = ReadNumber(root, "weight_kg", fileLabel, warnings, 75f, PlayerSheet.WeightMin, PlayerSheet.WeightMax);
            var foot = ReadString(root, "preferred_foot", fileLabel, warnings, "right", warn: true).ToLowerInvariant();
            if (foot != "left" && foot != "right")
            {
                warnings.Add(new LoadWarning(fileLabel, "preferred_foot", $"'{foot}' is not left or right; using right"));
                foot = "right";
            }
            s.PreferredFoot = foot == "left" ? Foot.Left : Foot.Right;
            return new LoadResult<PlayerSheet> { Value = s, Warnings = warnings };
        }
    }

    static string ReadString(JsonElement root, string field, string file, List<LoadWarning> w, string def, bool warn)
    {
        if (!root.TryGetProperty(field, out var e) || e.ValueKind != JsonValueKind.String)
        {
            if (warn) w.Add(new LoadWarning(file, field, root.TryGetProperty(field, out _) ? $"not a string; using '{def}'" : $"missing; using '{def}'"));
            return def;
        }
        return e.GetString() ?? def;
    }

    static float ReadNumber(JsonElement root, string field, string file, List<LoadWarning> w, float def, float lo, float hi)
    {
        if (!root.TryGetProperty(field, out var e))
        {
            w.Add(new LoadWarning(file, field, $"missing; using {def}"));
            return def;
        }
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetSingle(out float v) || float.IsNaN(v) || float.IsInfinity(v))
        {
            w.Add(new LoadWarning(file, field, $"not a number; using {def}"));
            return def;
        }
        if (v < lo || v > hi)
        {
            float c = Math.Clamp(v, lo, hi);
            w.Add(new LoadWarning(file, field, $"{v} is outside {lo}..{hi}; clamped to {c}"));
            return c;
        }
        return v;
    }

    // ---- scenarios ----

    /// <summary>Loads a scenario file.</summary>
    public static LoadResult<Scenario> LoadScenario(string path)
    {
        var text = ReadFile(path, out var err);
        if (text == null) return new LoadResult<Scenario> { Error = err };
        return ParseScenario(text, path, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>Parses scenario JSON. Missing fields keep their defaults; impossible values are corrected with a warning.</summary>
    public static LoadResult<Scenario> ParseScenario(string json, string fileLabel = "scenario", string? defaultId = null)
    {
        Scenario? s;
        try { s = JsonSerializer.Deserialize<Scenario>(json, Options); }
        catch (JsonException e) { return new LoadResult<Scenario> { Error = JsonError(fileLabel, e) }; }
        if (s == null) return new LoadResult<Scenario> { Error = $"{fileLabel}: empty file" };
        var w = new List<LoadWarning>();
        if (defaultId != null && s.Id == "scenario") s.Id = defaultId;
        s.Pitch ??= new PitchSpec();
        s.Ball ??= new StartSpot();
        s.Attacker ??= new StartSpot();
        s.Pitch.Length = Fix(s.Pitch.Length, 10f, 200f, "pitch.length");
        s.Pitch.Width = Fix(s.Pitch.Width, 10f, 200f, "pitch.width");
        s.Pitch.GoalWidth = Fix(s.Pitch.GoalWidth, 1f, 20f, "pitch.goal_width");
        s.Pitch.GoalHeight = Fix(s.Pitch.GoalHeight, 0.5f, 6f, "pitch.goal_height");
        s.Pitch.PenaltyAreaDepth = Fix(s.Pitch.PenaltyAreaDepth, 1f, s.Pitch.Length, "pitch.penalty_area_depth");
        s.Pitch.PenaltyAreaWidth = Fix(s.Pitch.PenaltyAreaWidth, s.Pitch.GoalWidth, 200f, "pitch.penalty_area_width");
        if (s.TeamSize < 1 || s.TeamSize > 11)
        {
            w.Add(new LoadWarning(fileLabel, "team_size", $"{s.TeamSize} is outside 1..11; clamped"));
            s.TeamSize = Math.Clamp(s.TeamSize, 1, 11);
        }
        if (s.KeeperOffLine is float kol && (float.IsNaN(kol) || kol < 0f || kol > s.Pitch.Length))
        {
            w.Add(new LoadWarning(fileLabel, "keeper_off_line", $"{kol} is outside 0..{s.Pitch.Length}; using the set spot"));
            s.KeeperOffLine = null;
        }
        if (s.ClockLimit is <= 0f)
        {
            w.Add(new LoadWarning(fileLabel, "clock_limit", "must be positive; using no clock"));
            s.ClockLimit = null;
        }
        return new LoadResult<Scenario> { Value = s, Warnings = w };

        float Fix(float v, float lo, float hi, string field)
        {
            if (float.IsNaN(v) || v < lo || v > hi)
            {
                float c = float.IsNaN(v) ? lo : Math.Clamp(v, lo, hi);
                w.Add(new LoadWarning(fileLabel, field, $"{v} is outside {lo}..{hi}; clamped to {c}"));
                return c;
            }
            return v;
        }
    }

    // ---- tuning ----

    /// <summary>Loads a tuning file. A missing or unusable file gives the built-in defaults with <see cref="LoadResult{T}.Error"/> set.</summary>
    public static LoadResult<Tuning> LoadTuning(string path)
    {
        var text = ReadFile(path, out var err);
        if (text == null) return new LoadResult<Tuning> { Value = new Tuning(), Error = err + " (default tuning in use)" };
        return ParseTuning(text, path);
    }

    /// <summary>Parses tuning JSON. Values the file omits keep their built-in defaults.</summary>
    public static LoadResult<Tuning> ParseTuning(string json, string fileLabel = "tuning")
    {
        try
        {
            var t = JsonSerializer.Deserialize<Tuning>(json, Options);
            if (t == null) return new LoadResult<Tuning> { Value = new Tuning(), Error = $"{fileLabel}: empty file (default tuning in use)" };
            if (t.Ball == null) return new LoadResult<Tuning> { Value = new Tuning(), Error = $"{fileLabel}: ball: must be an object (default tuning in use)" };
            if (t.Touch == null) return new LoadResult<Tuning> { Value = new Tuning(), Error = $"{fileLabel}: touch: must be an object (default tuning in use)" };
            if (t.Skill == null) return new LoadResult<Tuning> { Value = new Tuning(), Error = $"{fileLabel}: skill: must be an object (default tuning in use)" };
            if (t.Keeper == null) return new LoadResult<Tuning> { Value = new Tuning(), Error = $"{fileLabel}: keeper: must be an object (default tuning in use)" };
            if (t.Shot == null) return new LoadResult<Tuning> { Value = new Tuning(), Error = $"{fileLabel}: shot: must be an object (default tuning in use)" };
            foreach (var (field, curve) in new[] { ("ball.drag_curve", t.Ball.DragCurve), ("ball.lift_curve", t.Ball.LiftCurve), ("ball.restitution_curve", t.Ball.RestitutionCurve),
                ("touch.early_execution", t.Touch.EarlyExecution), ("skill.ball_roll_distance", t.Skill.BallRollDistance),
                ("skill.ball_roll_run_blend", t.Skill.BallRollRunBlend), ("skill.ball_roll_keep", t.Skill.BallRollKeep),
                ("skill.step_over_run_blend", t.Skill.StepOverRunBlend), ("skill.step_over_run_keep", t.Skill.StepOverRunKeep),
                ("skill.heel_to_heel_run_blend", t.Skill.HeelToHeelRunBlend), ("skill.elastico_run_blend", t.Skill.ElasticoRunBlend),
                ("skill.sided_exit_burst", t.Skill.SidedExitBurst), ("skill.straight_exit_burst", t.Skill.StraightExitBurst),
                ("shot.ideal_charge", t.Shot.IdealCharge), ("keeper.arc_depth", t.Keeper?.ArcDepth), ("keeper.arc_depth_bearing", t.Keeper?.ArcDepthBearing), ("keeper.near_post_bias", t.Keeper?.NearPostBias),
                ("keeper.rush_margin", t.Keeper?.RushMargin), ("keeper.difficulty_low", t.Keeper?.DifficultyLow),
                ("keeper.difficulty_mid", t.Keeper?.DifficultyMid), ("keeper.difficulty_high", t.Keeper?.DifficultyHigh),
                ("keeper.counter_momentum_penalty", t.Keeper?.CounterMomentumPenalty),
                ("keeper.catch_range_standing_min", t.Keeper?.CatchRangeStandingMin), ("keeper.catch_range_standing_max", t.Keeper?.CatchRangeStandingMax),
                ("keeper.catch_range_diving_min", t.Keeper?.CatchRangeDivingMin), ("keeper.catch_range_diving_max", t.Keeper?.CatchRangeDivingMax),
                ("keeper.catch_range_time", t.Keeper?.CatchRangeTime) })
            {
                string? bad = curve == null ? "must be an array of [x, y] pairs" : curve.Validate();
                if (bad != null) return new LoadResult<Tuning> { Value = new Tuning(), Error = $"{fileLabel}: {field}: {bad} (default tuning in use)" };
            }
            var w = new List<LoadWarning>();
            if (t.TickRate < 1 || t.TickRate > 1000)
            {
                w.Add(new LoadWarning(fileLabel, "tick_rate", $"{t.TickRate} is outside 1..1000; using 120"));
                t.TickRate = 120;
            }
            return new LoadResult<Tuning> { Value = t, Warnings = w };
        }
        catch (JsonException e)
        {
            return new LoadResult<Tuning> { Value = new Tuning(), Error = JsonError(fileLabel, e) + " (default tuning in use)" };
        }
    }

    /// <summary>The tuning as indented JSON, the format of data/tuning/default.json.</summary>
    public static string TuningToJson(Tuning t) => JsonSerializer.Serialize(t, Options);

    /// <summary>A short stable hash of every tuning value, recorded in replays.</summary>
    public static string TuningHash(Tuning t)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(t, CompactOptions));
        return Convert.ToHexString(SHA256.HashData(bytes)).Substring(0, 16).ToLowerInvariant();
    }

    // ---- shared ----

    static string? ReadFile(string path, out string? error)
    {
        try { error = null; return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = $"{path}: {e.Message}";
            return null;
        }
    }

    static string JsonError(string file, JsonException e) =>
        e.LineNumber is long line
            ? $"{file}: invalid JSON at line {line + 1}: {e.Message}"
            : $"{file}: invalid JSON: {e.Message}";
}

/// <summary>
/// Reads and writes a <see cref="Curve"/> as an array of [x, y] number pairs. A wrong shape throws a JsonException, which names the
/// field's path; the values themselves are checked by <see cref="Curve.Validate"/> after loading.
/// </summary>
sealed class CurveJsonConverter : JsonConverter<Curve>
{
    public override Curve Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException();
        var points = new List<(float, float)>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException();
            float x = ReadNumber(ref reader, options), y = ReadNumber(ref reader, options);
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw new JsonException();
            points.Add((x, y));
        }
        return new Curve(points.ToArray());
    }

    static float ReadNumber(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        if (!reader.Read() || reader.TokenType is not (JsonTokenType.Number or JsonTokenType.String)) throw new JsonException();
        // Same number rules as every other tuning value (named literals such as "NaN" parse, then fail validation).
        return JsonSerializer.Deserialize<float>(ref reader, options);
    }

    public override void Write(Utf8JsonWriter writer, Curve value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        for (int i = 0; i < value.Count; i++)
        {
            var (x, y) = value[i];
            writer.WriteStartArray();
            JsonSerializer.Serialize(writer, x, options);
            JsonSerializer.Serialize(writer, y, options);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }
}
