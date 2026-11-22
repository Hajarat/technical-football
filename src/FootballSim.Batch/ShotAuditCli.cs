using FootballSim.Core;

/// <summary>
/// --shot-audit &lt;file|dir&gt; [--capture C14]: every shot of every replay (round-*.json and capture-*.json; a folder is searched
/// with its subfolders) as one line at its release, completed at the strike (see <see cref="ShotReleaseAudit"/>). A captured round's tag
/// and note head its output. --capture finds one capture through captures.jsonl in the folder given (or the one above it).
/// </summary>
static class ShotAuditCli
{
    public static int Run(string path, string? captureId, string dataDir, Tuning tuning)
    {
        var files = new List<string>();
        if (captureId != null)
        {
            var file = FindCapture(path, captureId);
            if (file == null) return 1;
            files.Add(file);
        }
        else if (File.Exists(path)) files.Add(path);
        else if (Directory.Exists(path))
            files.AddRange(Directory.GetFiles(path, "*.json", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f) is var n && (n.StartsWith("round-") || n.StartsWith("capture-")))
                .OrderBy(f => f, StringComparer.Ordinal));
        else { Console.Error.WriteLine($"{path}: no such file or folder"); return 1; }
        if (files.Count == 0) { Console.Error.WriteLine($"{path}: no round-*.json or capture-*.json replays"); return 1; }

        int failed = 0;
        foreach (var file in files) if (!Audit(file, dataDir, tuning)) failed++;
        return failed == files.Count ? 1 : 0;
    }

    static string? FindCapture(string path, string id)
    {
        if (CaptureIndex.Number(id) == null) { Console.Error.WriteLine($"--capture expects an id such as C14, got '{id}'"); return null; }
        string? index = null;
        if (File.Exists(path) && Path.GetFileName(path) == CaptureIndex.FileName) index = path;
        else if (Directory.Exists(path))
        {
            var dir = new DirectoryInfo(Path.GetFullPath(path));
            foreach (var d in new[] { dir, dir.Parent })
                if (d != null && File.Exists(Path.Combine(d.FullName, CaptureIndex.FileName))) { index = Path.Combine(d.FullName, CaptureIndex.FileName); break; }
        }
        if (index == null) { Console.Error.WriteLine($"no {CaptureIndex.FileName} in {path} or the folder above it"); return null; }
        var entry = CaptureIndex.Find(File.ReadAllText(index), id, out var warnings);
        foreach (var w in warnings) Console.Error.WriteLine("warning: " + w);
        if (entry == null) { Console.Error.WriteLine($"{id}: not in {index}"); return null; }
        string file = Path.Combine(Path.GetDirectoryName(index)!, entry.File);
        if (!File.Exists(file)) { Console.Error.WriteLine($"{id}: its replay {file} is missing"); return null; }
        return file;
    }

    static bool Audit(string file, string dataDir, Tuning tuning)
    {
        var r = Replay.Load(file, out var err);
        if (r == null) { Console.WriteLine($"== {file}: {err}"); return false; }
        var sc = DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", r.ScenarioId + ".json")).Value;
        // a round played with the player editor's Custom attacker carries its sheet
        var att = r.ResolveAttacker(id => DataLoader.LoadPlayer(Path.Combine(dataDir, "players", id + ".json")).Value, out var attErr);
        var kee = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", r.KeeperId + ".json")).Value;
        if (sc == null || att == null || kee == null)
        {
            Console.WriteLine($"== {file}: missing data ({(sc == null ? $"scenario '{r.ScenarioId}' " : "")}{attErr ?? ""}{(kee == null ? $" keeper '{r.KeeperId}'" : "")})");
            return false;
        }
        // a capture's tag and note head its block
        if (r.Capture is { } c) Console.WriteLine($"== capture {c.Id}: {c.Tag}{(c.Note.Length > 0 ? " · " + c.Note : "")} (tick {c.Tick})\n   {file}");
        else Console.WriteLine($"== {file}");
        Console.WriteLine($"   {r.ScenarioId}, attacker {r.AttackerId} (foot {att.PreferredFoot}, curve {att.Curve}, traits {att.Traits}), keeper {r.KeeperId}, " +
                          $"{r.TickCount} ticks{(r.Partial ? ", partial" : "")}{(r.MatchesTuning(tuning) ? "" : ", tuning has changed since")}");
        // a scenario with a defender replays with his sheet (the replay's, else the scenario's default)
        PlayerSheet? def = sc.Defenders > 0
            ? DataLoader.LoadPlayer(Path.Combine(dataDir, "players", (r.DefenderId ?? sc.DefaultDefender) + ".json")).Value
            : null;
        if (sc.Defenders > 0 && def == null) { Console.WriteLine($"== {file}: missing data (defender '{r.DefenderId ?? sc.DefaultDefender}')"); return false; }
        var shots = ShotReleaseAudit.Run(r, tuning, sc, att, kee, def);
        if (shots.Count == 0) Console.WriteLine("   no shots");
        for (int i = 0; i < shots.Count; i++) Console.WriteLine("   " + shots[i].Line(i));
        return true;
    }
}
