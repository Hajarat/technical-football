using System;
using System.Collections.Generic;
using Godot;

namespace FootballSim.Game;

/// <summary>Command-line options after "--" (OS.GetCmdlineUserArgs()).</summary>
public sealed class ToolArgs
{
    public int QuitAfterTicks;
    public bool QuitAfterRound;
    public string? ShotDir;
    public readonly List<int> ShotTicks = new();
    public readonly List<string> ShotOn = new();
    public bool FrameTimes, CloseCam, CloseKeeper, CloseAttacker, TopCam;
    public int BurstStart, BurstCount, BurstStep = 1;
    public string? DataDir;
    public string? ReplayPath;
    public bool Background;
    /// <summary>"skinned" (rigged human models, the default) or "procedural" (primitive meshes).</summary>
    public string Figure = "skinned";

    public static ToolArgs Parse()
    {
        var a = new ToolArgs();
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--quit-after-ticks": int.TryParse(Next(), out a.QuitAfterTicks); break;
                case "--quit-after-round": a.QuitAfterRound = true; break;
                case "--background": a.Background = true; break;
                case "--frame-times": a.FrameTimes = true; break;
                case "--close-cam": a.CloseCam = true; break;
                case "--top-cam": a.TopCam = true; break;
                case "--close-attacker": a.CloseCam = true; a.CloseAttacker = true; break;
                case "--close-keeper": a.CloseCam = true; a.CloseKeeper = true; break;
                case "--data": a.DataDir = Next(); break;
                case "--replay": a.ReplayPath = Next(); break;
                case "--figure":
                    a.Figure = (Next() ?? "").ToLowerInvariant();
                    if (a.Figure != "skinned" && a.Figure != "procedural") { GD.PushWarning($"--figure expects skinned or procedural, got '{a.Figure}'; using skinned"); a.Figure = "skinned"; }
                    break;
                case "--shots": a.ShotDir = Next(); break;
                case "--shot-ticks":
                    foreach (var s in (Next() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                        if (int.TryParse(s, out int t)) a.ShotTicks.Add(t);
                    break;
                case "--shot-burst":
                    var parts = (Next() ?? "").Split(':');
                    if (parts.Length >= 2) { int.TryParse(parts[0], out a.BurstStart); int.TryParse(parts[1], out a.BurstCount); }
                    if (parts.Length >= 3 && int.TryParse(parts[2], out int st) && st > 0) a.BurstStep = st;
                    break;
                case "--shot-on":
                    a.ShotOn.AddRange((Next() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                default: GD.PushWarning($"Unknown argument {args[i]}"); break;
            }
        }
        if (a.ShotDir != null)
        {
            // Relative paths resolve against the game project folder, not the launch directory.
            if (!System.IO.Path.IsPathRooted(a.ShotDir)) a.ShotDir = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), a.ShotDir);
            DirAccess.MakeDirRecursiveAbsolute(a.ShotDir);
            if (DisplayServer.GetName() == "headless")
            {
                GD.PushWarning("--shots ignored: headless mode has no rendered image.");
                a.ShotDir = null;
            }
        }
        return a;
    }
}

/// <summary>Saves viewport PNGs on request and keeps count of captures still waiting for a frame.</summary>
public sealed class ShotTool
{
    readonly Node _host;
    readonly string _dir;
    readonly HashSet<string> _taken = new();
    public int InFlight { get; private set; }

    public ShotTool(Node host, string dir) { _host = host; _dir = dir; }

    /// <summary>Takes a shot once per name. Call from _PhysicsProcess; the PNG is written after the next draw.</summary>
    public void Request(string name)
    {
        if (!_taken.Add(name)) return;
        InFlight++;
        Capture(name);
    }

    async void Capture(string name)
    {
        await _host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var img = _host.GetViewport().GetTexture().GetImage();
        string path = System.IO.Path.Combine(_dir, $"shot_{name}.png");
        var err = img.SavePng(path);
        GD.Print($"SHOT {name} {path} size={img.GetWidth()}x{img.GetHeight()} err={err}");
        InFlight--;
    }
}
