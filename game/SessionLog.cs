using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FootballSim.Core;
using Godot;

namespace FootballSim.Game;

/// <summary>
/// One folder per launch under sessions/ at the repo root: session.jsonl (one JSON object per line, flushed after every
/// line) plus a replay file per finished, unfinished or captured round. Captures are also indexed across sessions in
/// sessions/captures.jsonl (see <see cref="CaptureIndex"/>). Game layer only; timestamps are the game's own clock and never reach the core.
/// </summary>
public sealed class SessionLog : IDisposable
{
    readonly StreamWriter _writer;
    readonly ulong _startMsec = Time.GetTicksMsec();
    bool _closed;

    public string Dir { get; }
    /// <summary>The session folder's name (<c>yyyyMMdd-HHmmss</c>).</summary>
    public string Name => Path.GetFileName(Dir);
    /// <summary>sessions/captures.jsonl, beside the session folders.</summary>
    public string CaptureIndexPath => Path.Combine(Path.GetDirectoryName(Dir) ?? Dir, CaptureIndex.FileName);

    SessionLog(string dir, StreamWriter w) { Dir = dir; _writer = w; }

    /// <summary>Creates sessions/&lt;yyyyMMdd-HHmmss&gt;/ under <paramref name="root"/>. Returns null (and prints why) if it cannot.</summary>
    public static SessionLog? Create(string root)
    {
        try
        {
            string dir = Path.Combine(root, "sessions", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(dir);
            var fs = new FileStream(Path.Combine(dir, "session.jsonl"), FileMode.Append, System.IO.FileAccess.Write, FileShare.Read);
            return new SessionLog(dir, new StreamWriter(fs) { AutoFlush = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            GD.PrintErr("Session log disabled: " + e.Message);
            return null;
        }
    }

    /// <summary>Appends one line: t, wall, type, then the given fields. Values may be strings, numbers, bools, JsonElements or lists of dictionaries.</summary>
    public void Write(string type, params (string Key, object? Value)[] fields)
    {
        if (_closed) return;
        var line = new Dictionary<string, object?>
        {
            ["t"] = Math.Round((Time.GetTicksMsec() - _startMsec) / 1000.0, 3),
            ["wall"] = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz"),
            ["type"] = type,
        };
        foreach (var (k, v) in fields) line[k] = v;
        try { _writer.WriteLine(JsonSerializer.Serialize(line)); }
        catch (IOException e) { GD.PrintErr("Session log write failed: " + e.Message); }
    }

    /// <summary>Saves a replay into the session folder and returns its file name.</summary>
    public string SaveReplay(FootballSim.Core.Replay replay, string fileName)
    {
        try { replay.Save(Path.Combine(Dir, fileName)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { GD.PrintErr("Replay save failed: " + e.Message); }
        return fileName;
    }

    /// <summary>The next capture id from the index (C1 when it is missing); a line it cannot read is skipped with a warning.</summary>
    public string NextCaptureId()
    {
        string id = CaptureIndex.NextIdFromFile(CaptureIndexPath, out var warnings);
        foreach (var w in warnings)
        {
            GD.PushWarning(w);
            Write("warning", ("file", CaptureIndex.FileName), ("field", ""), ("message", w));
        }
        return id;
    }

    /// <summary>Appends a capture to the index, with this session's name and the replay's path relative to sessions/.</summary>
    public void AppendCapture(CaptureEntry entry, string fileName)
    {
        entry.Session = Name;
        entry.File = Name + "/" + fileName;
        if (CaptureIndex.Append(CaptureIndexPath, entry) is { } err) GD.PrintErr("Capture index not written: " + err);
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _writer.Dispose();
    }
}
