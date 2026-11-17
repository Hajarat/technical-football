using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace FootballSim.Game;

/// <summary>
/// One folder per launch under sessions/ at the repo root: session.jsonl (one JSON object per line, flushed after every
/// line) plus a replay file per finished round. Game layer only; timestamps are the game's own clock and never reach the core.
/// </summary>
public sealed class SessionLog : IDisposable
{
    readonly StreamWriter _writer;
    readonly ulong _startMsec = Time.GetTicksMsec();
    bool _closed;

    public string Dir { get; }

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

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _writer.Dispose();
    }
}
