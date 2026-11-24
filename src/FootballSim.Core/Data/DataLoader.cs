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

    /// <summary>
    /// The options <see cref="TuningHash"/> writes with: compact, and without the defender and defending blocks, which have their
    /// own hash (<see cref="DefendingHash"/>). A round without a defender reads neither block, so its replay's hash does not move
    /// when they are tuned.
    /// </summary>
    static readonly JsonSerializerOptions HashOptions = MakeHashOptions();

    static JsonSerializerOptions MakeHashOptions()
    {
        var o = MakeOptions();
        o.WriteIndented = false;
        o.TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                info =>
                {
                    // (the team values, added for a round with a team-mate, have their own hash too: see TeamHash)
                    if (info.Type == typeof(LockOnTuning))
                        for (int i = info.Properties.Count - 1; i >= 0; i--)
                            if (info.Properties[i].Name is "reaction_ratio" or "reaction_max_seconds") info.Properties.RemoveAt(i);
                    if (info.Type != typeof(Tuning)) return;
                    for (int i = info.Properties.Count - 1; i >= 0; i--)
                        if (info.Properties[i].Name is "defender" or "defending" or "pass" or "control" or "mate" or "receive") info.Properties.RemoveAt(i);
                },
            },
        };
        return o;
    }

    // ---- players ----

    /// <summary>Loads a player sheet from a file.</summary>
    public static LoadResult<PlayerSheet> LoadPlayer(string path)
    {
        var text = ReadFile(path, out var err);
        if (text == null) return new LoadResult<PlayerSheet> { Error = err };
        return ParsePlayer(text, path, Path.GetFileNameWithoutExtension(path));
    }

    /// <summary>
    /// Parses player JSON. Missing ratings become 50, out-of-range values are clamped; each change is a warning. An unknown
    /// name in <c>traits</c> is an error.
    /// </summary>
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
            // A file from before dribbling was split has one dribbling rating: both ratings take it, with one warning.
            bool legacy = root.TryGetProperty(LegacyDribbling, out _)
                && !root.TryGetProperty("close_dribbling", out _) && !root.TryGetProperty("sprint_dribbling", out _);
            if (legacy)
            {
                int d = (int)MathF.Round(ReadNumber(root, LegacyDribbling, fileLabel, warnings, PlayerSheet.RatingDefault, 1, 99));
                s.CloseDribbling = s.SprintDribbling = d;
                warnings.Add(new LoadWarning(fileLabel, LegacyDribbling, $"replaced by close_dribbling and sprint_dribbling; both set to {d}"));
            }
            foreach (var n in PlayerSheet.RatingNames)
            {
                if (legacy && n is ("close_dribbling" or "sprint_dribbling")) continue;
                // skill_moves came later than the other ratings: a sheet without it uses its close dribbling, without a warning.
                if (n == "skill_moves" && !root.TryGetProperty(n, out _)) { s.SkillMoves = s.CloseDribbling; continue; }
                s.SetRating(n, (int)MathF.Round(ReadNumber(root, n, fileLabel, warnings, PlayerSheet.RatingDefault, 1, 99)));
            }
            // The defending ratings came later: a sheet without one reads 50, without a warning.
            foreach (var n in PlayerSheet.DefendingRatingNames)
                if (root.TryGetProperty(n, out _))
                    s.SetRating(n, (int)MathF.Round(ReadNumber(root, n, fileLabel, warnings, PlayerSheet.RatingDefault, 1, 99)));
            // So did the passing ratings.
            foreach (var n in PlayerSheet.PassingRatingNames)
                if (root.TryGetProperty(n, out _))
                    s.SetRating(n, (int)MathF.Round(ReadNumber(root, n, fileLabel, warnings, PlayerSheet.RatingDefault, 1, 99)));
            s.HeightM = ReadNumber(root, "height_m", fileLabel, warnings, 1.80f, PlayerSheet.HeightMin, PlayerSheet.HeightMax);
            s.WeightKg = ReadNumber(root, "weight_kg", fileLabel, warnings, 75f, PlayerSheet.WeightMin, PlayerSheet.WeightMax);
            var foot = ReadString(root, "preferred_foot", fileLabel, warnings, "right", warn: true).ToLowerInvariant();
            if (foot != "left" && foot != "right")
            {
                warnings.Add(new LoadWarning(fileLabel, "preferred_foot", $"'{foot}' is not left or right; using right"));
                foot = "right";
            }
            s.PreferredFoot = foot == "left" ? Foot.Left : Foot.Right;
            // Traits: a missing list means none; a name that is not a trait fails the file (a typo would silently drop it).
            if (root.TryGetProperty("traits", out var traits))
            {
                if (traits.ValueKind != JsonValueKind.Array) return new LoadResult<PlayerSheet> { Error = $"{fileLabel}: traits: must be a list of trait names" };
                foreach (var e in traits.EnumerateArray())
                {
                    string? name = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                    PlayerTrait trait = PlayerTrait.None;
                    foreach (var (n, tr) in PlayerSheet.TraitNames) if (n == name) trait = tr;
                    if (trait == PlayerTrait.None)
                        return new LoadResult<PlayerSheet> { Error = $"{fileLabel}: traits: unknown trait '{(name ?? e.GetRawText())}' (known: {string.Join(", ", PlayerSheet.TraitNames.Select(x => x.Name))})" };
                    s.Traits |= trait;
                }
            }
            return new LoadResult<PlayerSheet> { Value = s, Warnings = warnings };
        }
    }

    /// <summary>The one dribbling rating of player files written before it was split into close and sprint dribbling.</summary>
    const string LegacyDribbling = "dribbling";

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
        s.Defender ??= new StartSpot { X = 16f };
        if (s.Defenders < 0 || s.Defenders > 1)
        {
            w.Add(new LoadWarning(fileLabel, "defenders", $"{s.Defenders} is outside 0..1; clamped"));
            s.Defenders = Math.Clamp(s.Defenders, 0, 1);
        }
        s.Pitch.Length = Fix(s.Pitch.Length, 10f, 200f, "pitch.length");
        s.Pitch.Width = Fix(s.Pitch.Width, 10f, 200f, "pitch.width");
        s.Pitch.GoalWidth = Fix(s.Pitch.GoalWidth, 1f, 20f, "pitch.goal_width");
        s.Pitch.GoalHeight = Fix(s.Pitch.GoalHeight, 0.5f, 6f, "pitch.goal_height");
        s.Pitch.PenaltyAreaDepth = Fix(s.Pitch.PenaltyAreaDepth, 1f, s.Pitch.Length, "pitch.penalty_area_depth");
        s.Pitch.PenaltyAreaWidth = Fix(s.Pitch.PenaltyAreaWidth, s.Pitch.GoalWidth, 200f, "pitch.penalty_area_width");
        if (s.Mate != null && s.TeamSize < 2)
        {
            w.Add(new LoadWarning(fileLabel, "mate", "needs team_size 2 or more; using 2"));
            s.TeamSize = 2;
        }
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
        var r = ParseTuning(text, path);
        // The move sheets live beside the tuning folder (data/moves/sheets.json); without them every move is legacy.
        string? dataDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(path)));
        if (r.Value != null && dataDir != null && File.Exists(SheetsPath(dataDir)))
        {
            var sheets = LoadSheets(dataDir);
            r.Value.Sheets = sheets.Value;
            var w = new List<LoadWarning>(r.Warnings);
            if (sheets.Error != null) w.Add(new LoadWarning(SheetsPath(dataDir), "", sheets.Error + " (every move is played by its legacy formulas)"));
            w.AddRange(sheets.Warnings);
            return new LoadResult<Tuning> { Value = r.Value, Error = r.Error, Warnings = w };
        }
        return r;
    }

    /// <summary>The move sheets' file under a data folder.</summary>
    public static string SheetsPath(string dataDir) => Path.Combine(dataDir, "moves", "sheets.json");

    /// <summary>The skill clips' index the sheets' hashes are checked against (beside the data folder, in the game's assets).</summary>
    public static string SheetClipIndexPath(string dataDir) =>
        Path.GetFullPath(Path.Combine(dataDir, "..", "game", "assets", "anim", "attacker", "skills", "clips.json"));

    /// <summary>
    /// Loads the move sheets of a data folder and checks them against the skill clips' index: a sheet whose hash the index does not
    /// carry is refused (the error names the file and the sheet). Without the index (a data folder away from the game's assets)
    /// the sheets load with a warning.
    /// </summary>
    public static LoadResult<MoveSheets> LoadSheets(string dataDir)
    {
        string path = SheetsPath(dataDir), index = SheetClipIndexPath(dataDir);
        var text = ReadFile(path, out var err);
        if (text == null) return new LoadResult<MoveSheets> { Error = err };
        var w = new List<LoadWarning>();
        string? indexText = File.Exists(index) ? ReadFile(index, out _) : null;
        if (indexText == null) w.Add(new LoadWarning(path, "", $"the clip index {index} was not found; the sheets' hashes are not checked"));
        try { return new LoadResult<MoveSheets> { Value = MoveSheets.Parse(text, path, indexText, index), Warnings = w }; }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new LoadResult<MoveSheets> { Error = e is FormatException ? e.Message : $"{path}: {e.Message}" };
        }
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
            if (t.Keeper.Brain is not ("home" or "legacy")) return new LoadResult<Tuning> { Value = new Tuning(), Error = $"{fileLabel}: keeper.brain: must be home or legacy (default tuning in use)" };
            if (t.Shot == null) return new LoadResult<Tuning> { Value = new Tuning(), Error = $"{fileLabel}: shot: must be an object (default tuning in use)" };
            if (t.Defender == null) return new LoadResult<Tuning> { Value = new Tuning(), Error = $"{fileLabel}: defender: must be an object (default tuning in use)" };
            if (t.Defending?.Auto == null || t.Defending.Stand == null || t.Defending.Slide == null || t.Defending.Barge == null || t.Defending.Block == null)
                return new LoadResult<Tuning> { Value = new Tuning(), Error = $"{fileLabel}: defending: must be an object with auto, stand, slide, barge and block (default tuning in use)" };
            foreach (var (field, curve) in new[] { ("ball.drag_curve", t.Ball.DragCurve), ("ball.lift_curve", t.Ball.LiftCurve), ("ball.restitution_curve", t.Ball.RestitutionCurve), ("ball.bounce_friction_curve", t.Ball.BounceFrictionCurve),
                ("movement.accel_by_speed_jog", t.Movement?.AccelBySpeedJog), ("movement.accel_by_speed_sprint", t.Movement?.AccelBySpeedSprint),
                ("movement.decel_by_speed", t.Movement?.DecelBySpeed), ("movement.turn_angle_mult", t.Movement?.TurnAngleMult), ("movement.sharp_turn_cost", t.Movement?.SharpTurnCost),
                ("touch.early_execution", t.Touch.EarlyExecution), ("touch.fatigue_turn", t.Touch.FatigueTurn), ("touch.fatigue_penalty", t.Touch.FatiguePenalty), ("touch.danger_speed", t.Touch.DangerSpeed), ("touch.weak_foot_error", t.Touch.WeakFootError), ("touch.execution_time", t.Touch.ExecutionTime), ("skill.ball_roll_distance", t.Skill.BallRollDistance),
                ("skill.ball_roll_run_blend", t.Skill.BallRollRunBlend), ("skill.ball_roll_keep", t.Skill.BallRollKeep),
                ("skill.step_over_run_blend", t.Skill.StepOverRunBlend), ("skill.step_over_run_keep", t.Skill.StepOverRunKeep),
                ("skill.heel_to_heel_run_blend", t.Skill.HeelToHeelRunBlend), ("skill.elastico_run_blend", t.Skill.ElasticoRunBlend), ("skill.spin_run_blend", t.Skill.SpinRunBlend), ("skill.mcgeady_run_blend", t.Skill.McGeadyRunBlend),
                ("skill.sprint_blend", t.Skill.SprintBlend), ("skill.drag_back_run_blend", t.Skill.DragBackRunBlend),
                ("skill.sided_exit_burst", t.Skill.SidedExitBurst), ("skill.straight_exit_burst", t.Skill.StraightExitBurst),
                ("shot.ideal_charge", t.Shot.IdealCharge), ("shot.angle_accuracy", t.Shot.AngleAccuracy), ("shot.angle_power", t.Shot.AnglePower),
                ("shot.windup_by_finishing_s", t.Shot.WindupByFinishingS), ("shot.angle_extra_windup_s", t.Shot.AngleExtraWindupS), ("shot.goal_mouth_accuracy", t.Shot.GoalMouthAccuracy),
                ("shot.weak_foot_accuracy", t.Shot.WeakFootAccuracy), ("shot.weak_foot_power", t.Shot.WeakFootPower), ("shot.weak_foot_curl", t.Shot.WeakFootCurl),
                ("shot.run_speed_power", t.Shot.RunSpeedPower),
                ("keeper.arc_depth", t.Keeper?.ArcDepth), ("keeper.arc_depth_bearing", t.Keeper?.ArcDepthBearing), ("keeper.near_post_bias", t.Keeper?.NearPostBias),
                ("keeper.rush_margin", t.Keeper?.RushMargin), ("keeper.difficulty_low", t.Keeper?.DifficultyLow),
                ("keeper.difficulty_mid", t.Keeper?.DifficultyMid), ("keeper.difficulty_high", t.Keeper?.DifficultyHigh),
                ("keeper.counter_momentum_penalty", t.Keeper?.CounterMomentumPenalty),
                ("keeper.catch_range_standing_min", t.Keeper?.CatchRangeStandingMin), ("keeper.catch_range_standing_max", t.Keeper?.CatchRangeStandingMax),
                ("keeper.catch_range_diving_min", t.Keeper?.CatchRangeDivingMin), ("keeper.catch_range_diving_max", t.Keeper?.CatchRangeDivingMax),
                ("keeper.catch_range_time", t.Keeper?.CatchRangeTime), ("keeper.home_depth_m", t.Keeper?.HomeDepthM),
                ("shot.first_time_error", t.Shot?.FirstTimeError),
                ("lock_on.reaction_ratio", t.LockOn?.ReactionRatio), ("pass.execution_seconds", t.Pass?.ExecutionSeconds), ("pass.angle_extra_seconds", t.Pass?.AngleExtraSeconds),
                ("receive.error", t.Receive?.Error), ("receive.cooldown", t.Receive?.Cooldown),
                ("defender.hold_distance_m", t.Defender.HoldDistanceM), ("defender.hold_offset_min_m", t.Defender.HoldOffsetMinM), ("defender.hold_offset_max_m", t.Defender.HoldOffsetMaxM),
                ("defender.to_active_after_s", t.Defender.ToActiveAfterS), ("defender.active_max_s", t.Defender.ActiveMaxS), ("defender.win_margin_s", t.Defender.WinMarginS),
                ("defending.auto.radius_walk_m", t.Defending.Auto.RadiusWalkM), ("defending.auto.radius_jockey_m", t.Defending.Auto.RadiusJockeyM), ("defending.auto.angle_deg", t.Defending.Auto.AngleDeg),
                ("defending.stand.reach_speed_factor", t.Defending.Stand.ReachSpeedFactor), ("defending.stand.miss_stun_s", t.Defending.Stand.MissStunS),
                ("defending.block.exit_speed", t.Defending.Block.ExitSpeed) })
            {
                string? bad = curve == null ? "must be an array of [x, y] pairs" : curve.Validate();
                if (bad != null) return new LoadResult<Tuning> { Value = new Tuning(), Error = $"{fileLabel}: {field}: {bad} (default tuning in use)" };
            }
            string? badBand = t.Shot.FarPostBand == null ? "must be an object" : t.Shot.FarPostBand.Validate();
            if (badBand != null) return new LoadResult<Tuning> { Value = new Tuning(), Error = $"{fileLabel}: shot.far_post_band: {badBand} (default tuning in use)" };
            string? badProfile = t.Touch.Profiles == null ? "must be an object" : t.Touch.Profiles.Validate();
            if (badProfile != null) return new LoadResult<Tuning> { Value = new Tuning(), Error = $"{fileLabel}: touch.profiles.{badProfile} (default tuning in use)" };
            foreach (var (field, range) in new[] { ("touch.fatigue_gain", t.Touch.FatigueGain), ("touch.fatigue_recovery", t.Touch.FatigueRecovery),
                ("touch.danger_min_steps", t.Touch.DangerMinSteps), ("touch.composure_sprint_steps", t.Touch.ComposureSprintSteps),
                ("touch.composure_early", t.Touch.ComposureEarly) })
            {
                string? bad = range == null ? "must be an object with at1 and at99"
                    : !float.IsFinite(range.At1) || !float.IsFinite(range.At99) || range.At1 < 0f || range.At99 < 0f ? "at1 and at99 must be finite and not negative" : null;
                if (bad != null) return new LoadResult<Tuning> { Value = new Tuning(), Error = $"{fileLabel}: {field}: {bad} (default tuning in use)" };
            }
            if (!float.IsFinite(t.Touch.DangerRadius) || t.Touch.DangerRadius < 0f)
                return new LoadResult<Tuning> { Value = new Tuning(), Error = $"{fileLabel}: touch.danger_radius: must be finite and not negative (default tuning in use)" };
            if (t.LockOn == null) return new LoadResult<Tuning> { Value = new Tuning(), Error = $"{fileLabel}: lock_on: must be an object (default tuning in use)" };
            var w = new List<LoadWarning>();
            // Keys the lock-on replaced: a file that still has them loads, with a warning each.
            try
            {
                using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("possession", out var poss) && poss.ValueKind == JsonValueKind.Object)
                    foreach (var gone in new[] { "regain_cone_deg", "regain_max_distance" })
                        if (poss.TryGetProperty(gone, out _))
                            w.Add(new LoadWarning(fileLabel, "possession." + gone, "removed (lock-on goes for a loose ball from any direction and distance); ignored"));
            }
            catch (JsonException) { }
            if (t.Defender.Brain is not ("contain" or "legacy"))
            {
                w.Add(new LoadWarning(fileLabel, "defender.brain", $"'{t.Defender.Brain}' is not contain or legacy; using contain"));
                t.Defender.Brain = "contain";
            }
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
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(t, HashOptions));
        return Convert.ToHexString(SHA256.HashData(bytes)).Substring(0, 16).ToLowerInvariant();
    }

    /// <summary>A short stable hash of the defender and defending tuning, recorded in the replay of a round with a defender.</summary>
    public static string DefendingHash(Tuning t)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(t.Defender, CompactOptions) + JsonSerializer.Serialize(t.Defending, CompactOptions));
        return Convert.ToHexString(SHA256.HashData(bytes)).Substring(0, 16).ToLowerInvariant();
    }

    /// <summary>
    /// A short stable hash of the team tuning (passes, control, the team-mate, receiving and the reaction delay), recorded in the
    /// replay of a round with a team-mate.
    /// </summary>
    public static string TeamHash(Tuning t)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(t.Pass, CompactOptions) + JsonSerializer.Serialize(t.Control, CompactOptions)
            + JsonSerializer.Serialize(t.Mate, CompactOptions) + JsonSerializer.Serialize(t.Receive, CompactOptions)
            + JsonSerializer.Serialize(t.LockOn.ReactionRatio, CompactOptions) + t.LockOn.ReactionMaxSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
