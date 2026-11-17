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
        Attacker.Figure.Stats = FootStats; Attacker.Figure.StatsName = "attacker";
        Keeper.Figure.Stats = FootStats; Keeper.Figure.StatsName = "keeper";
        Overlay.Enabled = true; // default on for now
        if (_args.CloseCam) Camera.UseCloseUp();
        if (_args.ShotDir != null) _shots = new ShotTool(this, _args.ShotDir);

        if (_args.FrameTimes)
            GD.Print($"DISPLAY refresh_hz={DisplayServer.ScreenGetRefreshRate()} vsync={DisplayServer.WindowGetVsyncMode()} max_fps={Engine.MaxFps} " +
                     $"screen={DisplayServer.WindowGetCurrentScreen()} screen_size={DisplayServer.ScreenGetSize()} scale={DisplayServer.ScreenGetScale()} screens={DisplayServer.GetScreenCount()}");

        if (!StartSession()) { _quitting = true; GetTree().Quit(1); }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_quitting) return;
        _tick++;
        var pad = Pad.Poll();
        Tick(pad);
        Present();
        // The ball has been placed above; only now follow it.
        Camera.Follow(_args.CloseKeeper ? new Vector3(World.Keeper.Position.X, 0, World.Keeper.Position.Z) : Ball.Position, delta);
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
                if (MomentReached(moment)) _shots.Request(moment);
        }
        if (_shots?.InFlight > 0) return;
        bool done = (_args.QuitAfterTicks > 0 && _tick >= _args.QuitAfterTicks) || (_args.QuitAfterRound && ReportReached);
        if (done) Finish();
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

    public override void _ExitTree() => LogQuit("exit");
}
