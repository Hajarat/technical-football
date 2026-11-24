using System.Globalization;
using System.Numerics;
using System.Text.Json;
using FootballSim.Core;

/// <summary>
/// The aim probe (<c>--probe aim [--aims &lt;file&gt;]</c>): reads the recorded aims kept with the tests (a person's struck shots:
/// where the ball was and where the stick pointed) and prints, per assistance level, what becomes of the aims that missed the
/// frame: captured (aimed at the safe point), eased (pulled part of the way) or left where the stick points, and how many end
/// inside the posts, with the band each level is held to. Exit code 3 when a band is missed.
/// <para>
/// <c>--probe aim --record &lt;sessions dir&gt; [--out &lt;file&gt;]</c> writes that file again from session folders: every round with
/// a replay file is played with the current code, and each shot is kept while the round still plays as it was recorded (a
/// shot struck on the tick, of the type and at the charge the session's own report names for it).
/// </para>
/// </summary>
static class AimProbe
{
    public sealed class Aim
    {
        public string Session { get; set; } = "";
        public string Round { get; set; } = "";
        public string Type { get; set; } = "";
        public float BallX { get; set; }
        public float BallZ { get; set; }
        /// <summary>The direction the stick gave (the facing, with the stick at rest), degrees: 0 toward the goal end (+X), positive toward +Z.</summary>
        public float StickDeg { get; set; }
    }

    public sealed class Fixture
    {
        public string Note { get; set; } = "";
        public float GoalLineX { get; set; }
        public float GoalHalfWidth { get; set; }
        public List<Aim> Aims { get; set; } = new();
    }

    /// <summary>The share of the off-frame aims each level must put inside the posts (Off: none at all).</summary>
    public static readonly (AimAssistLevel Level, double MinShare)[] Bands =
    {
        (AimAssistLevel.Low, 0.35), (AimAssistLevel.Medium, 0.55), (AimAssistLevel.High, 0.75),
    };

    public static string DefaultPath(string root) => Path.Combine(root, "src", "FootballSim.Core.Tests", "TestData", "aims", "recorded_aims.json");

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true, WriteIndented = true,
    };

    public static int Run(Dictionary<string, string> opts, string root, string dataDir, Tuning tuning)
    {
        string path = opts.TryGetValue("aims", out var a) ? a : opts.TryGetValue("out", out var o) ? o : DefaultPath(root);
        if (opts.TryGetValue("record", out var sessions)) return Record(sessions, path, dataDir, tuning);
        if (!File.Exists(path)) { Console.Error.WriteLine($"aim probe: no fixture at {path}"); return 2; }
        var fx = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path), Json)!;
        var sc = DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", "penalty.json")).Value!;
        var pitch = new Pitch(sc.Pitch, tuning.Frame, tuning.Arena);
        if (MathF.Abs(pitch.GoalLineX - fx.GoalLineX) > 1e-3f || MathF.Abs(pitch.GoalHalfWidth - fx.GoalHalfWidth) > 1e-3f)
        {
            Console.Error.WriteLine($"aim probe: the fixture was recorded with the goal line at {fx.GoalLineX} and posts at {fx.GoalHalfWidth}; the arena now has {pitch.GoalLineX} and {pitch.GoalHalfWidth}");
            return 2;
        }
        var ic = CultureInfo.InvariantCulture;
        float half = pitch.GoalHalfWidth;
        float Target(Aim aim, AimAssistLevel level) =>
            ShotModel.AimTargetZ(new Vector2(MathF.Cos(aim.StickDeg * MathF.PI / 180f), MathF.Sin(aim.StickDeg * MathF.PI / 180f)), 0f,
                new Vector3(aim.BallX, tuning.Ball.Radius, aim.BallZ), level, pitch, tuning);
        var off = fx.Aims.Where(x => MathF.Abs(Target(x, AimAssistLevel.Off)) > half).ToList();
        Console.WriteLine($"aim probe: {fx.Aims.Count} recorded aims from {path}");
        Console.WriteLine($"  {fx.Note}");
        foreach (var g in fx.Aims.GroupBy(x => x.Type).OrderBy(g => g.Key))
            Console.WriteLine(string.Create(ic, $"  {g.Key,-12} {g.Count(),4} aims, {g.Count(x => MathF.Abs(Target(x, AimAssistLevel.Off)) > half),3} outside the posts ({100.0 * g.Count(x => MathF.Abs(Target(x, AimAssistLevel.Off)) > half) / g.Count():F0}%)"));
        Console.WriteLine($"  {off.Count} aims missed the frame; of those, per level:");
        Console.WriteLine($"  {"level",-8} {"captured",8} {"eased",6} {"free",5} {"inside the posts",17}  band");
        bool ok = true;
        float safe = half - tuning.Shot.AimAssist.InsetM;
        foreach (var level in Enum.GetValues<AimAssistLevel>())
        {
            int captured = 0, eased = 0, free = 0, inside = 0;
            foreach (var aim in off)
            {
                float raw = Target(aim, AimAssistLevel.Off), z = Target(aim, level);
                if (MathF.Abs(z) < half) inside++;
                if (z == raw) free++;
                else if (MathF.Abs(MathF.Abs(z) - safe) < 1e-4f) captured++;
                else eased++;
            }
            string band; bool pass;
            if (level == AimAssistLevel.Off) { pass = inside == 0; band = "0"; }
            else
            {
                int need = (int)Math.Floor(Bands.First(b => b.Level == level).MinShare * off.Count);
                pass = inside >= need; band = $"at least {need}";
            }
            ok &= pass;
            Console.WriteLine($"  {level,-8} {captured,8} {eased,6} {free,5} {inside,17}  {band}{(pass ? "" : "  MISSED")}");
        }
        return ok ? 0 : 3;
    }

    static int Record(string sessions, string outPath, string dataDir, Tuning tuning)
    {
        if (!Directory.Exists(sessions)) { Console.Error.WriteLine($"aim probe: no folder {sessions}"); return 2; }
        var fx = new Fixture();
        int rounds = 0, played = 0, shots = 0, kept = 0;
        foreach (var log in Directory.GetFiles(sessions, "session.jsonl", SearchOption.AllDirectories).OrderBy(f => f))
        {
            string dir = Path.GetDirectoryName(log)!, session = Path.GetFileName(dir);
            foreach (var line in File.ReadLines(log))
            {
                if (!line.Contains("\"round_end\"")) continue;
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                if (!e.TryGetProperty("type", out var ty) || ty.GetString() != "round_end") continue;
                if (!e.TryGetProperty("replay", out var rf) || rf.ValueKind != JsonValueKind.String) continue;
                if (!e.TryGetProperty("report", out var rep) || !rep.TryGetProperty("shots", out var recorded)) continue;
                rounds++;
                string file = Path.Combine(dir, rf.GetString()!);
                if (!File.Exists(file)) continue;
                var rp = Replay.Load(file, out _);
                if (rp == null) continue;
                var sc = DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", rp.ScenarioId + ".json")).Value;
                var att = rp.ParseAttackerSheet() is { } emb ? emb.Value : DataLoader.LoadPlayer(Path.Combine(dataDir, "players", rp.AttackerId + ".json")).Value;
                var kee = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", rp.KeeperId + ".json")).Value;
                if (sc == null || att == null || kee == null) continue;
                var def = sc.Defenders > 0 ? DataLoader.LoadPlayer(Path.Combine(dataDir, "players", (rp.DefenderId ?? sc.DefaultDefender) + ".json")).Value : null;
                var mate = sc.Mate != null ? DataLoader.LoadPlayer(Path.Combine(dataDir, "players", (rp.MateId ?? sc.DefaultMate) + ".json")).Value : null;
                if (sc.Defenders > 0 && def == null || sc.Mate != null && mate == null) continue;
                World world;
                try { world = rp.Play(tuning, sc, att, kee, defender: def, mate: mate); }
                catch (Exception) { continue; }
                played++;
                fx.GoalLineX = world.Pitch.GoalLineX; fx.GoalHalfWidth = world.Pitch.GoalHalfWidth;
                int i = 0;
                foreach (var was in recorded.EnumerateArray())
                {
                    shots++;
                    // The round still plays as recorded up to this shot: struck on the same tick, the same type and charge.
                    if (i >= world.Shots.Count) break;
                    var s = world.Shots[i++];
                    if (was.GetProperty("tick").GetInt32() != s.Tick || !string.Equals(was.GetProperty("type").GetString(), Snake(s.Type.ToString()), StringComparison.Ordinal)
                        || MathF.Abs(was.GetProperty("charge").GetSingle() - s.Charge) > 1e-4f) break;
                    kept++;
                    fx.Aims.Add(new Aim
                    {
                        Session = session, Round = Path.GetFileNameWithoutExtension(file), Type = Snake(s.Type.ToString()),
                        BallX = MathF.Round(s.Origin.X, 3), BallZ = MathF.Round(s.Origin.Z, 3), StickDeg = MathF.Round(s.StickYaw * 180f / MathF.PI, 2),
                    });
                }
            }
        }
        fx.Note = $"Struck shots from play-test sessions, recorded {DateTime.Now:yyyy-MM-dd}: {rounds} rounds in the session logs, {played} with a replay file, " +
                  $"{kept} of their {shots} shots still struck as recorded (same tick, type and charge) when replayed.";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(fx, Json) + "\n");
        Console.WriteLine($"aim probe: wrote {fx.Aims.Count} aims to {outPath}");
        Console.WriteLine("  " + fx.Note);
        return 0;
    }

    static string Snake(string name) => JsonNamingPolicy.SnakeCaseLower.ConvertName(name);
}
