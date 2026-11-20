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
    /// <summary>--keeper-gallery &lt;dir&gt;: play every keeper state from a script (no simulation) and write screenshots there.</summary>
    public string? GalleryDir;
    /// <summary>--clip-gallery &lt;dir&gt;: play every keeper clip (res://assets/anim/keeper) and write a contact sheet per clip there.</summary>
    public string? ClipGalleryDir;
    /// <summary>--clip-folder &lt;res:// path&gt;: the clip gallery's clips.json folder (default res://assets/anim/keeper).</summary>
    public string ClipFolder = "res://assets/anim/keeper";
    /// <summary>--clip-model &lt;res:// path&gt;: the model the clip gallery plays them on (default res://assets/characters/keeper.glb; any other model wears the attacker's kit).</summary>
    public string ClipModel = "res://assets/characters/keeper.glb";
    /// <summary>--video: the clip gallery (and the keeper gallery) also writes every frame, for tools/anim/encode_video.py.</summary>
    public bool Video;
    /// <summary>--clip-only a,b: the clip gallery shows only these clips.</summary>
    public readonly HashSet<string> ClipOnly = new();
    /// <summary>--gallery-only a,b: the keeper gallery plays only the scripts whose names start with one of these.</summary>
    public readonly List<string> GalleryOnly = new();
    public bool Background;
    /// <summary>--controls-panel keyboard|xbox|playstation|nintendo: open the controls panel for that device (for screenshots; no pad needed).</summary>
    public string? ControlsPanel;
    /// <summary>--controls-panel &lt;device&gt;:hint fakes the device but leaves the panel closed, to see the corner hint during play.</summary>
    public bool ControlsHintOnly;
    /// <summary>"skinned" (rigged human models, the default) or "procedural" (primitive meshes).</summary>
    public string Figure = "skinned";
    /// <summary>--keeper-anim mocap|procedural: the keeper's body from motion clips (the default; needs the skinned figure) or from the hand-built poses (the fallback).</summary>
    public string KeeperAnim = "mocap";
    /// <summary>--attacker-anim mocap|procedural: the attacker's running body from the motion-captured stride cycles (the default; needs the skinned figure; see AttackerAnimator) or from the procedural run cycle.</summary>
    public string AttackerAnim = "mocap";
    /// <summary>--skill-anim clips|procedural: the attacker's skill moves from the authored full-body clips (the default; needs --attacker-anim mocap; see SkillClipLayer) or drawn procedurally (the fallback, and for moves without clips).</summary>
    public string SkillAnim = "clips";
    /// <summary>--keeper-anim-log: print a KEEPER_MM line at every mocap switch (clip, frame, playback rate).</summary>
    public bool KeeperAnimLog;
    /// <summary>--keeper-anim-drop-set a,b: the pose database leaves these clip sets empty (the procedural fallback's check).</summary>
    public readonly List<string> KeeperAnimDropSets = new();
    /// <summary>--bake-keeper-pose &lt;dir&gt;: play the keeper gallery's dive, smother and slide scripts procedurally and write their bone rotations there (tools/anim/bake_procedural.py).</summary>
    public string? BakeDir;

    /// <summary>A person playing: a window, and no replay, screenshot, gallery, bake or quit option.</summary>
    public bool Interactive => DisplayServer.GetName() != "headless" && ReplayPath == null && ShotDir == null && ShotTicks.Count == 0 && ShotOn.Count == 0
        && BurstCount == 0 && GalleryDir == null && ClipGalleryDir == null && BakeDir == null && QuitAfterTicks == 0 && !QuitAfterRound
        && ControlsPanel == null && !Background;

    /// <summary>The options of this run (the latest <see cref="Parse"/>), for views created after it.</summary>
    public static ToolArgs? Current { get; private set; }

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
                case "--keeper-gallery": a.GalleryDir = Next(); break;
                case "--clip-gallery": a.ClipGalleryDir = Next(); break;
                case "--clip-folder": a.ClipFolder = Next() ?? a.ClipFolder; break;
                case "--clip-model": a.ClipModel = Next() ?? a.ClipModel; break;
                case "--video": a.Video = true; break;
                case "--clip-only":
                    foreach (var s in (Next() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) a.ClipOnly.Add(s);
                    break;
                case "--gallery-only":
                    a.GalleryOnly.AddRange((Next() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--controls-panel":
                    a.ControlsPanel = (Next() ?? "").ToLowerInvariant();
                    if (a.ControlsPanel.EndsWith(":hint")) { a.ControlsHintOnly = true; a.ControlsPanel = a.ControlsPanel[..^5]; }
                    if (a.ControlsPanel is not ("keyboard" or "xbox" or "playstation" or "nintendo"))
                    { GD.PushWarning($"--controls-panel expects keyboard, xbox, playstation or nintendo, got '{a.ControlsPanel}'"); a.ControlsPanel = null; }
                    break;
                case "--figure":
                    a.Figure = (Next() ?? "").ToLowerInvariant();
                    if (a.Figure != "skinned" && a.Figure != "procedural") { GD.PushWarning($"--figure expects skinned or procedural, got '{a.Figure}'; using skinned"); a.Figure = "skinned"; }
                    break;
                case "--keeper-anim":
                    a.KeeperAnim = (Next() ?? "").ToLowerInvariant();
                    if (a.KeeperAnim != "procedural" && a.KeeperAnim != "mocap") { GD.PushWarning($"--keeper-anim expects mocap or procedural, got '{a.KeeperAnim}'; using mocap"); a.KeeperAnim = "mocap"; }
                    break;
                case "--keeper-anim-log": a.KeeperAnimLog = true; break;
                case "--attacker-anim":
                    a.AttackerAnim = (Next() ?? "").ToLowerInvariant();
                    if (a.AttackerAnim != "procedural" && a.AttackerAnim != "mocap") { GD.PushWarning($"--attacker-anim expects mocap or procedural, got '{a.AttackerAnim}'; using procedural"); a.AttackerAnim = "procedural"; }
                    break;
                case "--skill-anim":
                    a.SkillAnim = (Next() ?? "").ToLowerInvariant();
                    if (a.SkillAnim != "procedural" && a.SkillAnim != "clips") { GD.PushWarning($"--skill-anim expects clips or procedural, got '{a.SkillAnim}'; using clips"); a.SkillAnim = "clips"; }
                    break;
                case "--keeper-anim-drop-set":
                    a.KeeperAnimDropSets.AddRange((Next() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--bake-keeper-pose": a.BakeDir = Next(); break;
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
        if (a.GalleryDir != null)
        {
            if (!System.IO.Path.IsPathRooted(a.GalleryDir)) a.GalleryDir = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), a.GalleryDir);
            DirAccess.MakeDirRecursiveAbsolute(a.GalleryDir);
        }
        if (a.BakeDir != null)
        {
            if (!System.IO.Path.IsPathRooted(a.BakeDir)) a.BakeDir = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), a.BakeDir);
            DirAccess.MakeDirRecursiveAbsolute(a.BakeDir);
        }
        if (a.ClipGalleryDir != null)
        {
            if (!System.IO.Path.IsPathRooted(a.ClipGalleryDir)) a.ClipGalleryDir = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), a.ClipGalleryDir);
            DirAccess.MakeDirRecursiveAbsolute(a.ClipGalleryDir);
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
        Current = a;
        return a;
    }
}

/// <summary>Saves viewport PNGs on request and keeps count of captures still waiting for a frame.</summary>
public sealed class ShotTool
{
    readonly Node _host;
    readonly string _dir;
    readonly HashSet<string> _taken = new();
    readonly string _prefix;
    public int InFlight { get; private set; }

    public ShotTool(Node host, string dir, string prefix = "shot_") { _host = host; _dir = dir; _prefix = prefix; }

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
        string path = System.IO.Path.Combine(_dir, $"{_prefix}{name}.png");
        var err = img.SavePng(path);
        GD.Print($"SHOT {name} {path} size={img.GetWidth()}x{img.GetHeight()} err={err}");
        InFlight--;
    }
}
