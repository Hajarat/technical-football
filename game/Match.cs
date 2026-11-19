using System.Collections.Generic;
using Godot;

namespace FootballSim.Game;

/// <summary>
/// Scene root: builds the pitch, views, camera, HUD, overlay and input reader, then runs the round flow every physics tick.
/// Split over Match.cs (scene and tick), Match.Round.cs (rounds, data, replays) and Match.Present.cs (state to views).
/// </summary>
public partial class Match : Node3D
{
    public Pitch Pitch { get; private set; } = null!;
    public BallView Ball { get; private set; } = null!;
    public PlayerView Attacker { get; private set; } = null!;
    public KeeperView Keeper { get; private set; } = null!;
    public CameraRig Camera { get; private set; } = null!;
    public Hud Hud { get; private set; } = null!;
    public DebugOverlay Overlay { get; private set; } = null!;
    public InputReader Pad { get; private set; } = null!;

    ToolArgs _args = null!;
    ShotTool? _shots;
    int _tick;
    double _frameSum, _frameWorst;
    int _frameCount;
    bool _paused;
    bool _quitting;

    public override void _Ready()
    {
        _args = ToolArgs.Parse();
        if (_args.Background) MacBackground.Apply();

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.55f, 0.7f, 0.85f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.8f, 0.85f, 0.9f),
            AmbientLightEnergy = 0.6f,
        };
        AddChild(new WorldEnvironment { Environment = env });
        var sun = new DirectionalLight3D { ShadowEnabled = true, LightEnergy = 1.1f, DirectionalShadowMaxDistance = 160f };
        sun.Transform = new Transform3D(Basis.LookingAt(new Vector3(-0.3f, -1f, -0.35f).Normalized(), Vector3.Up), Vector3.Zero);
        AddChild(sun);

        Pitch = new Pitch { Name = "Pitch" }; AddChild(Pitch);
        Ball = new BallView { Name = "Ball" }; AddChild(Ball);
        Attacker = new PlayerView { Name = "Attacker" }; AddChild(Attacker);
        Keeper = new KeeperView { Name = "Keeper" }; AddChild(Keeper);
        Overlay = new DebugOverlay { Name = "Overlay" }; AddChild(Overlay);
        Camera = new CameraRig { Name = "Camera" }; AddChild(Camera);
        Hud = new Hud { Name = "Hud" }; AddChild(Hud);
        Pad = new InputReader { Name = "Input" }; AddChild(Pad);

        Attacker.SetTeamColor(new Color(0.85f, 0.1f, 0.1f));
        if (_args.Figure == "skinned") UseSkinnedFigures();
        Attacker.Figure.Stats = FootStats; Attacker.Figure.StatsName = "attacker";
        Attacker.Figure.AttackerAnimMode = _args.AttackerAnim;
        Attacker.Figure.SkillAnimMode = _args.SkillAnim;
        Keeper.Figure.Stats = FootStats; Keeper.Figure.StatsName = "keeper";
        Overlay.Enabled = true; // default on for now
        if (_args.TopCam) Camera.UseTopDown();
        else if (_args.CloseCam) Camera.UseCloseUp();
        if (_args.ShotDir != null) _shots = new ShotTool(this, _args.ShotDir);
        // A burst wants one frame per tick: at most one physics tick per rendered frame (a slow renderer then runs the game slower
        // than real time instead of skipping ticks between frames), drawn without interpolation so each frame is exactly its tick.
        if (_shots != null && _args.BurstCount > 0) { Engine.MaxPhysicsStepsPerFrame = 1; GetTree().PhysicsInterpolation = false; }

        if (_args.FrameTimes)
            GD.Print($"DISPLAY refresh_hz={DisplayServer.ScreenGetRefreshRate()} vsync={DisplayServer.WindowGetVsyncMode()} max_fps={Engine.MaxFps} " +
                     $"screen={DisplayServer.WindowGetCurrentScreen()} screen_size={DisplayServer.ScreenGetSize()} scale={DisplayServer.ScreenGetScale()} screens={DisplayServer.GetScreenCount()}");

        if (!StartSession()) { _quitting = true; GetTree().Quit(1); }
        else if (_args.GalleryDir != null) StartGallery();
        else if (_args.ClipGalleryDir != null) StartClipGallery();
    }

    KeeperGallery? _gallery;
    ClipGallery? _clipGallery;

    /// <summary>--clip-gallery: motion clips (the keeper's unless --clip-folder / --clip-model say otherwise) play on a body of their own; the match figures, ball, overlay and HUD stay out of the shots.</summary>
    void StartClipGallery()
    {
        Attacker.Visible = false; Keeper.Visible = false; Ball.Visible = false; Hud.Visible = false;
        Overlay.Enabled = false;
        _clipGallery = new ClipGallery(this, _args.ClipGalleryDir!, _args.Video, _args.ClipOnly, World.Pitch.GoalLineX, _args.ClipFolder, _args.ClipModel);
    }

    /// <summary>--keeper-gallery: the keeper is driven from a script instead of the simulation; the attacker, overlay and ball ring stay out of the shots.</summary>
    void StartGallery()
    {
        Attacker.Visible = false;
        Overlay.Enabled = false;
        _gallery = new KeeperGallery(this, _args.GalleryDir!, World.Pitch.GoalLineX);
    }

    string _figureWarning = "";

    /// <summary>Swaps the primitive figures for the rigged models; a missing or broken model leaves the primitives and shows a HUD warning.</summary>
    void UseSkinnedFigures()
    {
        var problems = new List<string>();
        if (!Attacker.Figure.TryUseSkinned("res://assets/characters/player.glb", out string e1)) problems.Add(e1);
        if (!Keeper.Figure.TryUseSkinned("res://assets/characters/keeper.glb", out string e2)
            && !Keeper.Figure.TryUseSkinned("res://assets/characters/player.glb", out _)) problems.Add(e2);
        if (problems.Count == 0) return;
        _figureWarning = "Skinned figures unavailable, using procedural: " + string.Join("; ", problems);
        GD.PushWarning(_figureWarning);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_quitting) return;
        _tick++;
        if (_gallery != null)
        {
            if (_gallery.Step()) { _quitting = true; GD.Print("GALLERY_DONE"); GetTree().Quit(0); }
            return;
        }
        if (_clipGallery != null)
        {
            if (_clipGallery.Step()) { _quitting = true; GD.Print("CLIP_GALLERY_DONE"); GetTree().Quit(0); }
            return;
        }
        var pad = Pad.Poll();
        Tick(pad);
        Present();
        // The ball has been placed above; only now follow it.
        Camera.Follow(_args.CloseKeeper ? new Vector3(World.Keeper.Position.X, 0, World.Keeper.Position.Z)
            : _args.CloseAttacker ? new Vector3(World.Attacker.Position.X + 0.6f, 0, World.Attacker.Position.Z) : Ball.Position, delta);
        HandleTools();
    }

    public override void _Process(double delta)
    {
        if (_args.FrameTimes && ++_frameCount > 10) // skip warm-up frames
        {
            _frameSum += delta;
            if (delta > _frameWorst) _frameWorst = delta;
        }
    }

    void HandleTools()
    {
        if (_shots != null)
        {
            if (_args.ShotTicks.Contains(_tick)) _shots.Request($"tick{_tick:D4}");
            if (_args.BurstCount > 0 && _tick >= _args.BurstStart && (_tick - _args.BurstStart) % _args.BurstStep == 0 && (_tick - _args.BurstStart) / _args.BurstStep < _args.BurstCount)
                _shots.Request($"burst{_tick:D4}");
            foreach (var moment in _args.ShotOn)
            {
                if (moment == "skill") SkillShots();
                else if (moment == "shot") ShotShots();
                else if (moment == "keeper") KeeperShots();
                else if (MomentReached(moment)) _shots.Request(moment);
            }
        }
        if (_shots?.InFlight > 0) return;
        bool done = (_args.QuitAfterTicks > 0 && _tick >= _args.QuitAfterTicks) || (_args.QuitAfterRound && ReportReached);
        if (done) Finish();
    }

    bool _skillSeen;

    FootballSim.Core.KeeperAction? _keeperSeen;
    int _keeperBurstLeft, _keeperBurstNext, _keeperShotN;

    /// <summary>--shot-on keeper: a burst of five frames (every 8 ticks) whenever the keeper's action changes, named keeper000_Action[_Dive]_tNNNN.</summary>
    void KeeperShots()
    {
        var k = World.Keeper;
        if (_keeperSeen != k.KeeperAction) { _keeperSeen = k.KeeperAction; _keeperBurstLeft = 5; _keeperBurstNext = _tick; }
        if (_keeperBurstLeft <= 0 || _tick < _keeperBurstNext) return;
        string dive = k.KeeperDive == FootballSim.Core.KeeperDive.None ? "" : "_" + k.KeeperDive;
        _shots!.Request($"keeper{_keeperShotN++:D3}_{k.KeeperAction}{dive}_t{_tick:D4}");
        _keeperBurstLeft--; _keeperBurstNext = _tick + 8;
    }

    /// <summary>--shot-on skill: six frames evenly across the move (skill0..skill5) and one just after it ends (skill6).</summary>
    void SkillShots()
    {
        if (World.Attacker.Action is { IsSkill: true, Phase: not FootballSim.Core.ActionPhase.Done } a)
        {
            _skillSeen = true;
            // a step-over or feint's close-in before its own motion is ordinary running: the frames start where the motion does
            int from = a.Kind is FootballSim.Core.ActionKind.StepOver or FootballSim.Core.ActionKind.BodyFeint ? a.ExecuteTick : a.StartTick;
            int span = System.Math.Max(1, a.EndTick - from);
            int idx = System.Math.Clamp(6 * (World.Attacker.CurrentTick - from) / span, 0, 5);
            _shots!.Request($"skill{idx}");
        }
        else if (_skillSeen) _shots!.Request("skill6");
    }

    int _shotFrames, _shotLastTick = -1, _shotAfter;

    /// <summary>
    /// --shot-on shot: frames across one shot, numbered in order (shot00_...): every 10 ticks from the press while the shoot button is held,
    /// every 2 ticks in the last 10 ticks before the contact, then every 6 ticks from the contact through the recovery, and one after it.
    /// </summary>
    void ShotShots()
    {
        var w = World; int now = w.Attacker.CurrentTick;
        if (now == _shotLastTick) return;
        _shotLastTick = now;
        if (w.Attacker.Action is { IsShot: true, Phase: not FootballSim.Core.ActionPhase.Done } a)
        {
            string? label = null;
            int since = now - a.PlanTick;
            if (a.Made) { int c = now - a.ContactTick; if (c % 6 == 0) label = $"contact{c:D2}"; }
            else if (w.ShotCharge > 0.001f) { if (since % 10 == 0) label = $"wind{since:D3}"; }
            else { int eta = a.ContactTick - now; if (eta <= 10 && eta % 2 == 0) label = $"swing{eta:D2}"; else if (since % 10 == 0) label = $"wind{since:D3}"; }
            if (label != null) _shots!.Request($"shot{_shotFrames++:D2}_{label}");
        }
        else if (_shotFrames > 0 && _shotAfter++ % 6 == 0 && _shotAfter < 14) _shots!.Request($"shot{_shotFrames++:D2}_after");
    }

    void Finish()
    {
        _quitting = true;
        if (_args.QuitAfterRound && World.Report != null)
        {
            var compact = System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonDocument.Parse(World.Report.ToJson()).RootElement);
            GD.Print("REPORT " + compact);
        }
        if (_replay != null)
        {
            Attacker.Figure.FlushStats(); Keeper.Figure.FlushStats();
            GD.Print(FootStats.ContactLine());
            GD.Print(FootStats.ContactTouchLine());
            GD.Print(FootStats.ContactStrikeLine());
            GD.Print(FootStats.PoseLine());
            GD.Print(FootStats.SlideLine());
            GD.Print(FootStats.ContactBallLine());
            GD.Print(FootStats.SkillLine());
            GD.Print(FootStats.TouchFootLine());
            GD.Print(FootStats.TouchMissLine());
            if (Attacker.Figure.IsSkinned) GD.Print(FootStats.SkinLine());
            foreach (var line in FootStats.JumpLines()) GD.Print(line);
        }
        if (_args.FrameTimes)
        {
            int n = System.Math.Max(1, _frameCount - 10);
            GD.Print($"FRAME_TIMES frames={n} avg_ms={_frameSum / n * 1000:F2} worst_ms={_frameWorst * 1000:F2}");
        }
        GD.Print($"DONE ticks={_tick}");
        LogQuit(_args.QuitAfterRound ? "quit-after-round" : "quit-after-ticks");
        GetTree().Quit(0);
    }

    void LogQuit(string reason)
    {
        if (_log == null) return;
        _log.Write("quit", ("reason", reason), ("rounds", _round), ("ticks", _tick));
        _log.Dispose();
        _log = null;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) LogQuit("window-closed");
    }

    public override void _ExitTree()
    {
        LogQuit("exit");
        // Godot objects the managed side made and dropped (the figures' mesh builders and meshes) are collected now: a wrapper the GC
        // has not reached yet when the .NET runtime shuts down keeps its object alive past it, and freeing it then aborts Godot
        // ("script_bindings" FATAL, exit 134). Whether any are left depends on when the GC last ran, so it is done here every time.
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        System.GC.Collect();
    }
}
