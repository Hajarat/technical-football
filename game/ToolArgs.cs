using System;
using System.Collections.Generic;
using Godot;

namespace FootballSim.Game;

/// <summary>Command-line options after "--" (OS.GetCmdlineUserArgs()).</summary>
public sealed class ToolArgs
{
    public int QuitAfterTicks;
    public bool QuitAfterRound;
    /// <summary>--session-log: write the session folder (session.jsonl, round replays) in replay mode too.</summary>
    public bool LogSession;
    public string? ShotDir;
    public readonly List<int> ShotTicks = new();
    public readonly List<string> ShotOn = new();
    /// <summary>--perf: per-frame engine and render cost (see PerfProbe); --perf-vsync on|off (on = vsync, max_fps 60; off = uncapped), --perf-csv &lt;path&gt;, --perf-warmup &lt;frames&gt;.</summary>
    public bool Perf;
    public bool? PerfVsync;
    public string? PerfCsv;
    public int PerfWarmup = 60;
    /// <summary>--perf-scale &lt;f&gt; (3D render scale) and --perf-msaa 0|2|4: diagnostics that change the GPU load to tell a GPU-bound or display-capped frame apart.</summary>
    public float PerfScale = 1f;
    public int PerfMsaa = -1;
    public bool FrameTimes, CloseCam, CloseKeeper, CloseAttacker, TopCam;
    /// <summary>--side-cam: the close camera on the attacker until his first skill move starts, then fixed side-on to his run there (CameraRig.FixSideOn).</summary>
    public bool SideCam;
    public int BurstStart, BurstCount, BurstStep = 1;
    public string? DataDir;
    public string? ReplayPath;
    /// <summary>--aim-assist off|low|medium|high: the aim assistance level of rounds played here, instead of the saved setting (a replay keeps its own).</summary>
    public FootballSim.Core.AimAssistLevel? AimAssist;
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
    /// <summary>--attacker-loco match|cycles: in --attacker-anim mocap, the running body from the locomotion recordings chosen by motion matching (the default; see AttackerMotion) or from the stride cycles blended by speed (the fallback).</summary>
    public string AttackerLoco = "match";
    /// <summary>--skill-anim clips|procedural: the attacker's skill moves from the authored full-body clips (the default; needs --attacker-anim mocap; see SkillClipLayer) or drawn procedurally (the fallback, and for moves without clips).</summary>
    public string SkillAnim = "clips";
    /// <summary>--shot-anim mocap|procedural: the attacker's struck shot from the motion-captured kicks (needs --attacker-anim mocap; see ShotClipLayer) or drawn procedurally (PlanShot, the default).</summary>
    public string ShotAnim = "mocap";
    /// <summary>--keeper-anim-log: print a KEEPER_MM line at every mocap switch (clip, frame, playback rate).</summary>
    public bool KeeperAnimLog;
    /// <summary>--keeper-anim-drop-set a,b: the pose database leaves these clip sets empty (the procedural fallback's check).</summary>
    public readonly List<string> KeeperAnimDropSets = new();
    /// <summary>--defender-anim mocap|procedural: the defender's stance, tackles, block, slide and get-up, and the attacker's stumble and fall, from the motion clips in assets/anim/defender (the default; needs the skinned figure; see DefenderAnimator) or from the procedural poses alone.</summary>
    public string DefenderAnim = "mocap";
    /// <summary>--defender-anim-log: print a DEFENDER_MM line at every switch of the defending clips (who, action, set, clip, frame, rate).</summary>
    public bool DefenderAnimLog;
    /// <summary>--defender-anim-drop-set a,b: the defender's pose database leaves these clip sets empty (the procedural fallback's check).</summary>
    public readonly List<string> DefenderAnimDropSets = new();
    /// <summary>--defender-gallery &lt;dir&gt;: play every defending action and reaction from a script (no simulation) and write screenshots there.</summary>
    public string? DefenderGalleryDir;
    /// <summary>--bake-keeper-pose &lt;dir&gt;: play the keeper gallery's dive, smother and slide scripts procedurally and write their bone rotations there (tools/anim/bake_procedural.py).</summary>
    public string? BakeDir;
    /// <summary>--player-editor: start with the player editor open (for screenshots).</summary>
    public bool PlayerEditor;
    /// <summary>--capture-panel: start with the capture panel open (for screenshots).</summary>
    public bool CapturePanel;

    /// <summary>A person playing: a window, and no replay, screenshot, gallery, bake or quit option.</summary>
    public bool Interactive => DisplayServer.GetName() != "headless" && ReplayPath == null && ShotDir == null && ShotTicks.Count == 0 && ShotOn.Count == 0
        && BurstCount == 0 && GalleryDir == null && DefenderGalleryDir == null && ClipGalleryDir == null && BakeDir == null && QuitAfterTicks == 0 && !QuitAfterRound
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
                case "--session-log": a.LogSession = true; break;
                case "--background": a.Background = true; break;
                case "--frame-times": a.FrameTimes = true; break;
                case "--perf": a.Perf = true; break;
                case "--perf-vsync": a.PerfVsync = Next() == "on"; break;
                case "--perf-scale": float.TryParse(Next(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out a.PerfScale); break;
                case "--perf-msaa": int.TryParse(Next(), out a.PerfMsaa); break;
                case "--perf-csv": a.PerfCsv = Next(); break;
                case "--perf-warmup": int.TryParse(Next(), out a.PerfWarmup); break;
                case "--close-cam": a.CloseCam = true; break;
                case "--top-cam": a.TopCam = true; break;
                case "--close-attacker": a.CloseCam = true; a.CloseAttacker = true; break;
                case "--close-keeper": a.CloseCam = true; a.CloseKeeper = true; break;
                case "--side-cam": a.CloseCam = true; a.CloseAttacker = true; a.SideCam = true; break;
                case "--data": a.DataDir = Next(); break;
                case "--replay": a.ReplayPath = Next(); break;
                case "--aim-assist":
                    string level = Next() ?? "";
                    if (Enum.TryParse(level, ignoreCase: true, out FootballSim.Core.AimAssistLevel aim) && Enum.IsDefined(aim) && !int.TryParse(level, out _)) a.AimAssist = aim;
                    else GD.PushWarning($"--aim-assist expects off, low, medium or high, got '{level}'");
                    break;
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
                case "--defender-anim":
                    a.DefenderAnim = (Next() ?? "").ToLowerInvariant();
                    if (a.DefenderAnim != "procedural" && a.DefenderAnim != "mocap") { GD.PushWarning($"--defender-anim expects mocap or procedural, got '{a.DefenderAnim}'; using mocap"); a.DefenderAnim = "mocap"; }
                    break;
                case "--defender-anim-log": a.DefenderAnimLog = true; break;
                case "--defender-anim-drop-set":
                    a.DefenderAnimDropSets.AddRange((Next() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--defender-gallery": a.DefenderGalleryDir = Next(); break;
                case "--attacker-anim":
                    a.AttackerAnim = (Next() ?? "").ToLowerInvariant();
                    if (a.AttackerAnim != "procedural" && a.AttackerAnim != "mocap") { GD.PushWarning($"--attacker-anim expects mocap or procedural, got '{a.AttackerAnim}'; using procedural"); a.AttackerAnim = "procedural"; }
                    break;
                case "--attacker-loco":
                    a.AttackerLoco = (Next() ?? "").ToLowerInvariant();
                    if (a.AttackerLoco != "match" && a.AttackerLoco != "cycles") { GD.PushWarning($"--attacker-loco expects match or cycles, got '{a.AttackerLoco}'; using match"); a.AttackerLoco = "match"; }
                    break;
                case "--skill-anim":
                    a.SkillAnim = (Next() ?? "").ToLowerInvariant();
                    if (a.SkillAnim != "procedural" && a.SkillAnim != "clips") { GD.PushWarning($"--skill-anim expects clips or procedural, got '{a.SkillAnim}'; using clips"); a.SkillAnim = "clips"; }
                    break;
                case "--shot-anim":
                    a.ShotAnim = (Next() ?? "").ToLowerInvariant();
                    if (a.ShotAnim != "procedural" && a.ShotAnim != "mocap") { GD.PushWarning($"--shot-anim expects mocap or procedural, got '{a.ShotAnim}'; using mocap"); a.ShotAnim = "mocap"; }
                    break;
                case "--keeper-anim-drop-set":
                    a.KeeperAnimDropSets.AddRange((Next() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--bake-keeper-pose": a.BakeDir = Next(); break;
                case "--player-editor": a.PlayerEditor = true; break;
                case "--capture-panel": a.CapturePanel = true; break;
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
        if (a.DefenderGalleryDir != null)
        {
            if (!System.IO.Path.IsPathRooted(a.DefenderGalleryDir)) a.DefenderGalleryDir = System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), a.DefenderGalleryDir);
            DirAccess.MakeDirRecursiveAbsolute(a.DefenderGalleryDir);
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
