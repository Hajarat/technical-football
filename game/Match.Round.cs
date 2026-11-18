using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using FootballSim.Core;
using Godot;

namespace FootballSim.Game;

public partial class Match
{
    const string NoPadNotice = "No gamepad detected. macOS has to list it as a game controller: try the controller's Bluetooth, Switch or PlayStation mode. " +
                               "Keyboard works meanwhile: WASD to move, left mouse or Space to shoot, Enter to start.";

    GameData _data = null!;
    ReplayDriver? _replay;
    SessionLog? _log;
    Random _seeds = new();            // session-level seed source: the core never makes its own seeds
    string _scenarioId = "", _attackerId = "", _keeperId = "";
    int _scenarioIndex;
    ulong _seed;
    int _round;                       // number of the round being set up or played; a retry is a new number
    int _lastFinishedRound;
    string _lastInputMode = "";
    bool _controlsOn;
    string _lastPadName = "";
    string _lastWarnings = "";
    int _flightTicks, _liveTicks;
    bool _beganReplay;

    /// <summary>The round being played. Rebuilt from disk every time a round is set up.</summary>
    public World World { get; private set; } = null!;
    bool ReportReached => World.State == RoundState.Report;

    bool StartSession()
    {
        string game = ProjectSettings.GlobalizePath("res://");
        _data = new GameData(Path.GetFullPath(_args.DataDir ?? Path.Combine(game, "..", "data")));
        _data.Reload();
        if (_args.ReplayPath == null && _args.GalleryDir == null) _log = SessionLog.Create(Path.GetFullPath(Path.Combine(game, "..")));
        _log?.Write("start",
            ("godot", Engine.GetVersionInfo()["string"].AsString()),
            ("refresh_hz", float.IsFinite(DisplayServer.ScreenGetRefreshRate()) ? DisplayServer.ScreenGetRefreshRate() : 0f), // infinite under a virtual display
            ("window", $"{DisplayServer.WindowGetSize().X}x{DisplayServer.WindowGetSize().Y}"),
            ("display", DisplayServer.GetName()),
            ("data_dir", _data.Dir));
        LogPads();
        Pad.SwapChanged += (name, swapped, reason) =>
        {
            _log?.Write("controls", ("pad", name), ("swap_face_buttons", swapped), ("reason", reason));
            if (reason == "toggle") Hud.Toast(swapped ? "Face buttons: swapped" : "Face buttons: standard");
        };
        Input.JoyConnectionChanged += (device, connected) => { LogPads(); GD.Print($"PADS device {device} {(connected ? "connected" : "disconnected")}"); };

        if (_data.Scenarios.Count == 0 || _data.Attackers.Count == 0 || _data.Keepers.Count == 0)
        {
            GD.PrintErr($"No usable data in {_data.Dir} (scenarios {_data.Scenarios.Count}, attackers {_data.Attackers.Count}, keepers {_data.Keepers.Count}). Use -- --data <dir>.");
            foreach (var w in _data.Warnings) GD.PrintErr(w);
            LogWarnings();
            return false;
        }

        _seed = (ulong)_seeds.NextInt64();
        int penalty = _data.Scenarios.FindIndex(s => s.Id == "penalty");
        _scenarioIndex = Math.Max(0, penalty);
        UseScenarioDefaults();

        if (_args.ReplayPath != null)
        {
            _replay = ReplayDriver.Load(_args.ReplayPath);
            if (_replay == null) return false;
            var r = _replay.Replay;
            _scenarioIndex = _data.Scenarios.FindIndex(s => s.Id == r.ScenarioId);
            if (_scenarioIndex < 0) { GD.PrintErr($"Replay scenario '{r.ScenarioId}' not found in {_data.Dir}"); return false; }
            _scenarioId = r.ScenarioId; _attackerId = r.AttackerId; _keeperId = r.KeeperId; _seed = r.Seed;
            if (!r.MatchesTuning(_data.Tuning)) GD.Print("Tuning has changed since this replay, so the result may differ.");
        }
        return SetUpRound(reload: false, newRound: true);
    }

    void LogPads()
    {
        var pads = Input.GetConnectedJoypads().Select(id => new System.Collections.Generic.Dictionary<string, object?>
        {
            ["id"] = (int)id, ["name"] = Input.GetJoyName((int)id), ["guid"] = Input.GetJoyGuid((int)id),
        }).ToList();
        _log?.Write("pads", ("count", pads.Count), ("pads", pads));
    }

    void LogWarnings()
    {
        string key = string.Join("\n", _data.WarningRecords.Select(w => $"{w.File}|{w.Field}|{w.Message}"));
        if (key == _lastWarnings) return;   // only log when the set of problems changes, not every round
        _lastWarnings = key;
        foreach (var (file, field, message) in _data.WarningRecords)
        {
            _log?.Write("warning", ("file", file), ("field", field), ("message", message));
            GD.Print($"WARNING {file} {field} {message}");
        }
    }

    void UseScenarioDefaults()
    {
        var sc = _data.Scenarios[_scenarioIndex];
        _scenarioId = sc.Id;
        _attackerId = Pick(_data.Attackers, sc.DefaultAttacker);
        _keeperId = Pick(_data.Keepers, sc.DefaultKeeper);
    }

    static string Pick(System.Collections.Generic.List<PlayerSheet> list, string wanted) =>
        list.Exists(p => p.Id == wanted) ? wanted : list[0].Id;

    /// <summary>
    /// Re-reads the data files and builds a fresh world in Setup, then teleports the views to it.
    /// <paramref name="newRound"/> advances the round number (first round, next round, retry); changing the start spot or
    /// players while still in Setup rebuilds the same round. <paramref name="retryOf"/> is the round being retried, or 0.
    /// </summary>
    bool SetUpRound(bool reload = true, bool newRound = false, int retryOf = 0)
    {
        if (reload) _data.Reload();
        LogWarnings();
        // Selections that no longer exist (file deleted or renamed) fall back to something that does.
        if (_data.Scenarios.Count == 0 || _data.Attackers.Count == 0 || _data.Keepers.Count == 0) return false;
        _scenarioIndex = Math.Clamp(_data.Scenarios.FindIndex(s => s.Id == _scenarioId) is var i and >= 0 ? i : _scenarioIndex, 0, _data.Scenarios.Count - 1);
        var sc = _data.Scenarios[_scenarioIndex];
        _scenarioId = sc.Id;
        var att = _data.Attackers.Find(p => p.Id == _attackerId) ?? _data.Attackers[0];
        var kee = _data.Keepers.Find(p => p.Id == _keeperId) ?? _data.Keepers[0];
        _attackerId = att.Id; _keeperId = kee.Id;
        if (newRound) _round++;

        World = new World(_data.Tuning, sc, att, kee, _seed);
        Pitch.Build(World.Pitch);
        // Left edge at the back wall; right edge may reach 0.5 m past the back of the net so the whole goal shows; the bottom
        // edge stops at the near boards.
        Camera.SetLimits(World.Pitch.MinX, World.Pitch.GoalLineX + World.Pitch.NetDepth + 0.5f, World.Pitch.HalfWidth);
        World.Recorder = new ReplayRecorder(World, att.Id, kee.Id, sc.Id);
        _flightTicks = 0; _liveTicks = 0; _beganReplay = false;
        _log?.Write("round_setup", ("round", _round), ("scenario", sc.Id), ("attacker", att.Id), ("keeper", kee.Id),
            ("seed", _seed.ToString()), ("retry_of", retryOf > 0 ? retryOf : null));
        ResetPresentation();
        return true;
    }

    /// <summary>One physics tick of game logic: buttons, then at most one world step.</summary>
    void Tick(in PadSnapshot pad)
    {
        if (pad.OverlayToggle) Overlay.Enabled = !Overlay.Enabled;
        if (pad.Flag) Flag();
        if (pad.ControlsToggle) _controlsOn = !_controlsOn;
        if (pad.Pause)
        {
            _paused = !_paused;
            _log?.Write("pause", ("paused", _paused));
        }
        UpdateNotices();
        Hud.SetWarning(WarningLine());

        if (_paused) { UpdateHudText(); return; }

        // Start spot / player selection. In Setup it takes effect now, otherwise at the next Setup.
        int spot = (pad.NextSpot ? 1 : 0) - (pad.PrevSpot ? 1 : 0);
        bool changed = false;
        if (spot != 0 && _replay == null)
        {
            _scenarioIndex = (_scenarioIndex + spot + _data.Scenarios.Count) % _data.Scenarios.Count;
            UseScenarioDefaults();
            changed = true;
            _log?.Write("selection", ("what", "spot"), ("scenario", _scenarioId), ("attacker", _attackerId), ("keeper", _keeperId), ("applies_now", World.State == RoundState.Setup));
        }
        if (pad.NextAttacker && _replay == null)
        {
            _attackerId = Cycle(_data.Attackers, _attackerId); changed = true;
            _log?.Write("selection", ("what", "attacker"), ("attacker", _attackerId), ("applies_now", World.State == RoundState.Setup));
        }
        if (pad.NextKeeper && _replay == null)
        {
            _keeperId = Cycle(_data.Keepers, _keeperId); changed = true;
            _log?.Write("selection", ("what", "keeper"), ("keeper", _keeperId), ("applies_now", World.State == RoundState.Setup));
        }
        if (changed && World.State == RoundState.Setup) SetUpRound();

        switch (World.State)
        {
            case RoundState.Setup:
                if (pad.Confirm || _replay != null && !_beganReplay && _tick >= 30)
                {
                    World.Begin();
                    _beganReplay = true;
                    _log?.Write("round_begin", ("round", _round));
                }
                break;
            case RoundState.Report:
                if (pad.Confirm) { _replay = null; _seed = (ulong)_seeds.NextInt64(); SetUpRound(newRound: true); }
                else if (pad.Retry) { _replay = null; int of = _round; SetUpRound(newRound: true, retryOf: of); }
                break;
        }

        if (World.IsSimulating)
        {
            var intents = new TickIntent[World.PlayerCount];
            intents[0] = _replay != null ? _replay.IntentAt(World.Tick) : FromPad(pad);
            _closeControl = intents[0].CloseControl;
            _intent = intents[0];
            World.Step(intents);
            if (World.State == RoundState.Report) FinishRound();
        }
        UpdateHudText();
    }

    /// <summary>Gamepad notice and pad-name toast; the input mode is logged when it changes.</summary>
    void UpdateNotices()
    {
        Pad.KeyboardEnabled = _replay == null;
        if (_paused) Hud.SetNotice("Paused");
        else if (_replay == null && !Pad.GamepadConnected && !_controlsOn && _args.ControlsPanel == null) Hud.SetNotice(NoPadNotice);
        else Hud.SetNotice("");

        string name = Pad.GamepadConnected ? Pad.ActivePadName : "";
        if (name != _lastPadName)
        {
            _lastPadName = name;
            if (name.Length > 0) Hud.Toast("Gamepad: " + name, 4f);
        }

        string mode = Pad.Mode;
        if (mode != _lastInputMode)
        {
            _lastInputMode = mode;
            _log?.Write("input_mode", ("mode", mode));
        }
        UpdateControls();
    }

    /// <summary>The corner hint, and the controls panel while paused or toggled (or forced by --controls-panel).</summary>
    void UpdateControls()
    {
        var device = Pad.Device;
        string padName = Pad.GamepadConnected ? Pad.ActivePadName : "";
        var kind = PadBind.KindOf(padName);
        bool swapped = Pad.SwapFaceButtons;
        bool forced = _args.ControlsPanel != null;
        if (forced)
        {
            device = _args.ControlsPanel == "keyboard" ? InputDevice.Keyboard : InputDevice.Gamepad;
            kind = _args.ControlsPanel switch { "playstation" => PadKind.PlayStation, "nintendo" => PadKind.Nintendo, _ => PadKind.Xbox };
            padName = ""; swapped = false;
        }
        Hud.SetControlsHint(device == InputDevice.Gamepad ? $"{Bindings.Pad.Pause.Label(kind)}: controls" : $"{Bindings.Controls.Label}: controls");
        bool show = (forced && !_args.ControlsHintOnly) || _paused || _controlsOn;
        if (show) Hud.Controls.Open(device, kind, padName, swapped);
        else Hud.Controls.Close();
    }

    string Cycle(System.Collections.Generic.List<PlayerSheet> list, string current)
    {
        int i = list.FindIndex(p => p.Id == current);
        return list[(i + 1 + list.Count) % list.Count].Id;
    }

    static TickIntent FromPad(in PadSnapshot p) => new()
    {
        MoveX = p.MoveX, MoveZ = p.MoveZ, Sprint = p.Sprint, CloseControl = p.CloseControl,
        ShootHeld = p.ShootHeld, Finesse = p.Finesse, Chip = p.Chip, Driven = p.Driven, KnockOn = p.KnockOn,
        RightX = p.RightX, RightZ = p.RightZ, Cancel = p.Cancel, SkillKey = p.SkillKey,
    };

    /// <summary>The round reached Report: save its replay into the session folder, log it, print one console line.</summary>
    void FinishRound()
    {
        _lastFinishedRound = _round;
        var report = World.Report!;
        string outcome = World.Outcome.Label().ToLowerInvariant();
        string file = $"round-{_round:D3}-{_scenarioId}-{outcome}.json";
        if (_log != null && World.Recorder != null) _log.SaveReplay(World.Recorder.Replay, file);
        _log?.Write("round_end", ("round", _round), ("outcome", World.Outcome.Label()), ("clock", Math.Round(report.Clock, 3)),
            ("report", JsonDocument.Parse(report.ToJson()).RootElement.Clone()), ("replay", _log != null ? file : null));
        GD.Print($"ROUND {_round} {World.Outcome.Label()} {_scenarioId}");
    }

    void Flag()
    {
        if (_lastFinishedRound == 0) { Hud.Toast("No finished round to flag yet"); return; }
        _log?.Write("flag", ("round", _lastFinishedRound));
        Hud.Toast($"Flagged round {_lastFinishedRound}");
        GD.Print($"FLAG round {_lastFinishedRound}");
    }

    string WarningLine()
    {
        var w = _data.Warnings;
        if (_figureWarning.Length > 0) return w.Count == 0 ? _figureWarning : $"{_figureWarning} (+{w.Count} data warnings)";
        if (w.Count == 0) return "";
        return w.Count == 1 ? w[0] : $"{w[0]} (+{w.Count - 1} more)";
    }
}
