using System.Collections.Generic;
using System.IO;
using System.Linq;
using FootballSim.Core;

namespace FootballSim.Game;

/// <summary>
/// The data folder as the game sees it: tuning, scenarios (the start spots), attackers and keepers.
/// Reload() re-reads every file; a file that fails keeps its last good version and adds a warning.
/// </summary>
public sealed class GameData
{
    readonly Dictionary<string, object> _lastGood = new();
    public string Dir { get; }
    public Tuning Tuning { get; private set; } = new();
    public List<Scenario> Scenarios { get; } = new();
    public List<PlayerSheet> Attackers { get; } = new();
    public List<PlayerSheet> Keepers { get; } = new();
    /// <summary>One line per problem found by the latest Reload(): file, field and what was done.</summary>
    public List<string> Warnings { get; } = new();
    /// <summary>The same problems with file, field and message separated (field is empty for a whole-file error).</summary>
    public List<(string File, string Field, string Message)> WarningRecords { get; } = new();

    public GameData(string dir) => Dir = dir;

    /// <summary>
    /// The player editor's Custom attacker (id <c>custom</c>), or null. It is kept across reloads and joins <see cref="Attackers"/>
    /// after the shipped ones; a data/players file with the same id is left out with a warning.
    /// </summary>
    public PlayerSheet? Custom { get; private set; }

    /// <summary>Sets (a copy of) the Custom attacker, or removes it with null, and updates <see cref="Attackers"/> at once.</summary>
    public void SetCustom(PlayerSheet? sheet)
    {
        Custom = sheet == null ? null : PlayerPresets.AsCustom(sheet);
        ApplyCustom();
    }

    void ApplyCustom()
    {
        if (Custom == null) return;
        foreach (var clash in Attackers.Concat(Keepers).Where(p => p.Id == PlayerPresets.CustomId && p != Custom).ToList())
        {
            Attackers.Remove(clash); Keepers.Remove(clash);
            Warn(Path.Combine(Dir, "players"), "id", $"a player file uses the id '{PlayerPresets.CustomId}', which is the player editor's; the editor's Custom is used");
        }
        if (!Attackers.Contains(Custom)) Attackers.Add(Custom);
    }

    /// <summary>
    /// The core has no role field on a sheet, so the game decides: a player whose average goalkeeping rating
    /// (reflexes, diving, reach, positioning, handling, rushing_out) beats the average of the outfield ratings is a keeper.
    /// </summary>
    public static bool IsKeeper(PlayerSheet s)
    {
        double keeper = PlayerSheet.KeeperRatingNames.Average(n => (double)s.GetRating(n));
        var outfield = PlayerSheet.RatingNames.Where(n => !PlayerSheet.KeeperRatingNames.Contains(n) && n != "weak_foot").ToList();
        return keeper > outfield.Average(n => (double)s.GetRating(n));
    }

    public void Reload()
    {
        Warnings.Clear();
        WarningRecords.Clear();
        var tr = DataLoader.LoadTuning(Path.Combine(Dir, "tuning", "default.json"));
        if (tr.Error != null)
        {
            Warn(Path.Combine(Dir, "tuning", "default.json"), "", tr.Error + (_lastGood.ContainsKey("tuning") ? "; keeping the last good tuning" : ""));
            if (_lastGood.TryGetValue("tuning", out var t)) Tuning = (Tuning)t;
            else Tuning = tr.Value ?? new Tuning();
        }
        else
        {
            Tuning = tr.Value!;
            _lastGood["tuning"] = Tuning;
            foreach (var w in tr.Warnings) Warn(w);
        }

        Scenarios.Clear();
        foreach (var path in Files("scenarios"))
            if (Take(DataLoader.LoadScenario(path), path) is { } sc) Scenarios.Add(sc);
        Scenarios.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        Attackers.Clear();
        Keepers.Clear();
        foreach (var path in Files("players"))
        {
            if (Take(DataLoader.LoadPlayer(path), path) is not { } p) continue;
            (IsKeeper(p) ? Keepers : Attackers).Add(p);
        }
        Attackers.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        Keepers.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        ApplyCustom();
    }

    IEnumerable<string> Files(string sub)
    {
        string d = Path.Combine(Dir, sub);
        return Directory.Exists(d) ? Directory.GetFiles(d, "*.json").OrderBy(f => f, System.StringComparer.Ordinal) : Enumerable.Empty<string>();
    }

    T? Take<T>(LoadResult<T> r, string path) where T : class
    {
        foreach (var w in r.Warnings) Warn(w);
        if (r.Value != null && r.Error == null)
        {
            _lastGood[path] = r.Value;
            return r.Value;
        }
        bool have = _lastGood.TryGetValue(path, out var old);
        Warn(path, "", r.Error + (have ? "; keeping the last good version" : ""));
        return have ? (T)old! : null;
    }

    void Warn(LoadWarning w) => Warn(w.File, w.Field, w.Message);

    void Warn(string file, string field, string message)
    {
        string Short(string t) => t.Replace(Dir + Path.DirectorySeparatorChar, "");
        file = Short(file);
        message = Short(message);
        WarningRecords.Add((file, field, message));
        Warnings.Add(field.Length > 0 ? $"{file}: {field}: {message}" : message);
    }
}
