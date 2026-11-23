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
    string _scenarioId = "", _attackerId = "", _keeperId = "", _defenderId = "";
    int _scenarioIndex;
    ulong _seed;
    int _round;                       // number of the round being set up or played; a retry is a new number
    string _lastInputMode = "";
    bool _controlsOn;
    string _lastPadName = "";
    string _lastWarnings = "";
    int _flightTicks, _liveTicks;
    bool _beganReplay;
    string _buildCommit = "", _headCommit = "";

    /// <summary>The round being played. Rebuilt from disk every time a round is set up.</summary>
    public World World { get; private set; } = null!;
    bool ReportReached => World.State == RoundState.Report;

    /// <summary>A skill move as the attacker's figure starts drawing it: which clips (and their blend) or the procedural drawing.</summary>
    void LogSkill(SkillStart m) => _log?.Write("skill", ("round", _round), ("tick", World.Tick), ("move", m.Move), ("foot", m.Right ? "R" : "L"),
        ("side", m.Side < 0 ? "left" : m.Side > 0 ? "right" : "none"), ("stand_clip", m.StandClip), ("run_clip", m.RunClip),
        ("w_run", Math.Round(m.WRun, 2)), ("entry_speed", Math.Round(m.EntrySpeed, 2)), ("drawing", m.Clips ? "clips" : "procedural"));

    bool StartSession()
    {
        string game = ProjectSettings.GlobalizePath("res://");
        _data = new GameData(Path.GetFullPath(_args.DataDir ?? Path.Combine(game, "..", "data")));
        _data.Reload();
        if (_args.LogSession || _args.ReplayPath == null && _args.GalleryDir == null && _args.ClipGalleryDir == null) _log = SessionLog.Create(Path.GetFullPath(Path.Combine(game, "..")));
        _buildCommit = BuildInfo.Commit;
        _headCommit = BuildInfo.ReadHead(game);
        bool stale = BuildInfo.IsStale(_buildCommit, _headCommit);
        GD.Print($"BUILD commit={_buildCommit} head={_headCommit} stale={(stale ? "true" : "false")}");
        if (stale && _args.Interactive)
            Hud.SetStaleBuild($"This game was built from {_buildCommit}, but the checkout is at {_headCommit}. Run: dotnet build game/FootballSim.Game.csproj");
        _log?.Write("start",
            ("build_commit", _buildCommit),
            ("head_commit", _headCommit),
            ("build_stale", stale),
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
            _defenderId = r.DefenderId ?? "";
            // A round played with the editor's Custom player carries its sheet; it plays as Custom whatever the store holds.
            if (r.ParseAttackerSheet() is { } embedded)
            {
                foreach (var w in embedded.Warnings) GD.Print($"WARNING {w}");
                if (!embedded.Ok) { GD.PrintErr(embedded.Error); return false; }
                _data.SetCustom(embedded.Value);
                _attackerId = PlayerPresets.CustomId;
            }
            if (!r.MatchesTuning(_data.Tuning)) GD.Print("Tuning has changed since this replay, so the result may differ.");
        }
        else LoadEditorStore();
        if (!SetUpRound(reload: false, newRound: true)) return false;
        if (_args.PlayerEditor && _replay == null) OpenEditor(force: true);
        if (_args.CapturePanel && !_editor.IsOpen) OpenCapture(force: true);
        return true;
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
        // Custom stays the attacker across start spots; only picking another attacker ends it.
        if (!(_attackerId == PlayerPresets.CustomId && _data.Custom != null)) _attackerId = Pick(_data.Attackers, sc.DefaultAttacker);
        _keeperId = Pick(_data.Keepers, sc.DefaultKeeper);
        _defenderId = sc.Defenders > 0 ? sc.DefaultDefender : "";
    }

    static string Pick(System.Collections.Generic.List<PlayerSheet> list, string wanted) =>
        list.Exists(p => p.Id == wanted) ? wanted : list[0].Id;

    /// <summary>
    /// Re-reads the data files and builds a fresh world in Setup, then teleports the views to it.
    /// <paramref name="newRound"/> advances the round number (first round, next round, retry); changing the start spot or
    /// players while still in Setup rebuilds the same round. <paramref name="retryOf"/> is the round being retried, or 0. A round
    /// that began and has no report is saved as unfinished first, with <paramref name="abandon"/> as the reason.
    /// </summary>
    bool SetUpRound(bool reload = true, bool newRound = false, int retryOf = 0, string abandon = "setup")
    {
        SaveUnfinished(abandon);
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

        // A scenario with a defender: his sheet is the replay's or the scenario's (any outfield player file), else a plain one.
        PlayerSheet? def = null;
        if (sc.Defenders > 0)
        {
            if (_defenderId == "") _defenderId = sc.DefaultDefender;
            def = _data.Attackers.Find(p => p.Id == _defenderId) ?? _data.Attackers.Find(p => p.Id == sc.DefaultDefender);
            if (def == null) _data.Warnings.Add($"{sc.Id}: defender '{_defenderId}' not found in players; a plain sheet is used");
        }
        World = new World(_data.Tuning, sc, att, kee, _seed, def);
        Pitch.Build(World.Pitch);
        // Left edge at the back wall; right edge may reach 0.5 m past the back of the net so the whole goal shows; the bottom
        // edge stops at the near boards.
        Camera.SetLimits(World.Pitch.MinX, World.Pitch.GoalLineX + World.Pitch.NetDepth + 0.5f, World.Pitch.HalfWidth);
        World.Recorder = new ReplayRecorder(World, att.Id, kee.Id, sc.Id, def?.Id);
        Defender.Visible = World.Defender != null && _gallery == null && _clipGallery == null;
        if (att.Id == PlayerPresets.CustomId && att == _data.Custom) World.Recorder.Replay.SetAttackerSheet(att);
        _flightTicks = 0; _liveTicks = 0; _beganReplay = false;
        _log?.Write("round_setup", ("round", _round), ("scenario", sc.Id), ("attacker", att.Id), ("keeper", kee.Id), ("defender", World.Defender?.Sheet.Id),
            ("seed", _seed.ToString()), ("retry_of", retryOf > 0 ? retryOf : null));
        ResetPresentation();
        return true;
    }

    /// <summary>One physics tick of game logic: buttons, then at most one world step.</summary>
    void Tick(in PadSnapshot input)
    {
        var pad = input;
        if (_capture.IsOpen)
        {
            // the world stays frozen and gameplay input is ignored while the capture panel is open
            var (device, kind, _) = ShownDevice();
            _capture.SetDevice(device, kind);
            var choice = _capture.Step(pad.Capture);
            if (choice != CaptureChoice.None) SaveCapture(withNote: choice == CaptureChoice.Save);
            UpdateNotices();
            UpdateHudText();
            return;
        }
        if (_holdAfterCapture)
        {
            // the button that saved (B is also shoot, A also cancel) does nothing in play until it is let go
            if (!pad.ShootHeld && !pad.Cancel) _holdAfterCapture = false;
            else { pad.ShootHeld = false; pad.Cancel = false; }
        }
        if (_editor.IsOpen)
        {
            // gameplay input is ignored while the editor is open
            var (device, kind, _) = ShownDevice();
            _editor.SetDevice(device, kind);
            var r = _editor.Step(pad.Editor, 1f / Engine.PhysicsTicksPerSecond);
            if (r == EditorResult.StoreChanged) WriteEditorStore();
            else if (r == EditorResult.Close) CloseEditor();
            UpdateNotices();
            UpdateHudText();
            return;
        }
        if (pad.OverlayToggle) Overlay.Enabled = !Overlay.Enabled;
        if (pad.OpenCapture) OpenCapture();
        if (_capture.IsOpen) { UpdateNotices(); UpdateHudText(); return; }
        if (pad.ControlsToggle) _controlsOn = !_controlsOn;
        if (pad.Pause)
        {
            _paused = !_paused;
            _log?.Write("pause", ("paused", _paused));
        }
        // The editor opens only while play is stopped: T when paused, in Setup or at the report; the pad's Y while paused (Y at the
        // report is retry).
        bool stopped = _paused || World.State is RoundState.Setup or RoundState.Report;
        if (_replay == null && (pad.OpenEditor && stopped || pad.Retry && _paused)) OpenEditor();
        UpdateNotices();
        Hud.SetWarning(WarningLine());

        if (_paused || _editor.IsOpen) { UpdateHudText(); return; }

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
                    if (World.Recorder?.Replay.AttackerSheet is { } sheet) _log?.Write("custom_sheet", ("round", _round), ("sheet", sheet));
                }
                break;
            case RoundState.Report:
                if (pad.Confirm) { _replay = null; _seed = (ulong)_seeds.NextInt64(); SetUpRound(newRound: true); }
                else if (pad.Retry) { _replay = null; int of = _round; SetUpRound(newRound: true, retryOf: of); }
                break;
        }

        if (World.IsSimulating)
        {
            // one buffer reused tick to tick (World.Step reads it as a span and keeps nothing), cleared as a new array would be
            if (_intents.Length != World.PlayerCount) _intents = new TickIntent[World.PlayerCount];
            else Array.Clear(_intents);
            var intents = _intents;
            intents[0] = _replay != null ? _replay.IntentAt(World.Tick) : FromPad(pad);
            _closeControl = intents[0].CloseControl;
            _stick = intents[0].Move;
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
        if (_editor.IsOpen || _capture.IsOpen) Hud.SetNotice("");
        else if (_paused) Hud.SetNotice("Paused");
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

    TickIntent[] _intents = Array.Empty<TickIntent>();
    string? _hintText;
    InputDevice _hintDevice;
    PadKind _hintKind;

    /// <summary>The corner hint, and the controls panel while paused or toggled (or forced by --controls-panel).</summary>
    void UpdateControls()
    {
        var (device, kind, padName) = ShownDevice();
        bool swapped = Pad.SwapFaceButtons;
        bool forced = _args.ControlsPanel != null;
        if (forced) swapped = false;
        // (the hint's text is made again only when the device or the pad's kind changes, not every tick)
        if (_hintText == null || device != _hintDevice || kind != _hintKind)
        {
            _hintDevice = device; _hintKind = kind;
            _hintText = device == InputDevice.Gamepad ? $"{Bindings.Pad.Pause.Label(kind)}: controls" : $"{Bindings.Controls.Label}: controls";
        }
        Hud.SetControlsHint(_hintText);
        bool show = ((forced && !_args.ControlsHintOnly) || _paused || _controlsOn) && !_editor.IsOpen && !_capture.IsOpen;
        if (show) Hud.Controls.Open(device, kind, padName, swapped);
        else Hud.Controls.Close();
    }

    /// <summary>The device, pad kind and pad name the controls are shown for: the one in use, or the one --controls-panel fakes.</summary>
    (InputDevice Device, PadKind Kind, string PadName) ShownDevice()
    {
        if (_args.ControlsPanel != null)
            return (_args.ControlsPanel == "keyboard" ? InputDevice.Keyboard : InputDevice.Gamepad,
                _args.ControlsPanel switch { "playstation" => PadKind.PlayStation, "nintendo" => PadKind.Nintendo, _ => PadKind.Xbox }, "");
        string padName = Pad.GamepadConnected ? Pad.ActivePadName : "";
        return (Pad.Device, PadBind.KindOf(padName), padName);
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
        Hud.SetStaleBuild("");
        var report = World.Report!;
        string outcome = World.Outcome.Label().ToLowerInvariant();
        string file = $"round-{_round:D3}-{_scenarioId}-{outcome}.json";
        if (_log != null && World.Recorder != null) _log.SaveReplay(World.Recorder.Replay, file);
        if (World.Recorder != null)
            _lastFinished = new CaptureSource(_round, World.Recorder.Snapshot(), false, World.Tick, report.Clock, World.Outcome.Label().ToLowerInvariant());
        _log?.Write("round_end", ("round", _round), ("outcome", World.Outcome.Label()), ("clock", Math.Round(report.Clock, 3)),
            ("report", JsonDocument.Parse(report.ToJson()).RootElement.Clone()), ("replay", _log != null ? file : null));
        GD.Print($"ROUND {_round} {World.Outcome.Label()} {_scenarioId}");
    }

    // ---- capture and unfinished rounds ----

    /// <summary>What a capture holds: the round, a copy of its replay, whether it was cut short, and where it stood.</summary>
    sealed record CaptureSource(int Round, Replay Replay, bool Partial, int Tick, float Clock, string Outcome);

    CaptureSource? _lastFinished;     // the last round that reached its report
    CaptureSource? _captureSource;    // what the open capture panel will save
    bool _holdAfterCapture;

    /// <summary>
    /// What a capture taken now holds: during a round (or at its outcome, before the report) the round so far, partial; in Setup
    /// or at the report the last finished round, whole. Null with no round played yet.
    /// </summary>
    CaptureSource? CaptureSourceNow()
    {
        if (World.State is not (RoundState.Setup or RoundState.Report) && World.Recorder != null)
            return new CaptureSource(_round, World.Recorder.Snapshot(), true, World.Tick, World.Clock, "");
        return _lastFinished is { } f ? f with { Replay = f.Replay.Copy() } : null;
    }

    /// <summary>
    /// Opens the capture panel, which freezes the game until it is saved. With nothing to capture (or no session folder to write
    /// to) it only says so; <paramref name="force"/> (--capture-panel, for screenshots) opens it anyway.
    /// </summary>
    void OpenCapture(bool force = false)
    {
        var src = CaptureSourceNow();
        if (!force && (src == null || _log == null))
        {
            Hud.Toast(src == null ? "Nothing to capture" : "Nothing to capture: no session log (replay mode: add --session-log)");
            return;
        }
        _captureSource = src;
        string title = src == null ? "Capture" : $"Capture · round {src.Round}";
        string detail = src == null ? "Nothing to capture yet: play a round first."
            : src.Partial ? $"The round so far, up to {src.Clock.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} s (tick {src.Tick}). It carries on after the capture."
            : $"Round {src.Round} as it finished: {src.Outcome}.";
        _capture.Open(title, detail);
        var (device, kind, _) = ShownDevice();
        _capture.SetDevice(device, kind);
        Pad.TextEntry = true;
    }

    /// <summary>
    /// Closes the panel and writes the capture: its replay (with the capture block, and partial mid-round) into the session folder,
    /// a capture line in session.jsonl and one line in sessions/captures.jsonl, then the toast.
    /// </summary>
    void SaveCapture(bool withNote)
    {
        var src = _captureSource;
        string tag = _capture.Tag, note = withNote ? _capture.Note : "";
        _capture.Close();
        _captureSource = null;
        Pad.TextEntry = false;
        _holdAfterCapture = true;
        if (src == null || _log == null) { Hud.Toast("Nothing to capture"); return; }

        string id = _log.NextCaptureId();
        var r = src.Replay;
        string wall = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        r.Partial = src.Partial;
        r.Capture = new ReplayCapture { Id = id, Tag = tag, Note = note, Tick = src.Tick, Clock = (float)Math.Round(src.Clock, 3), Wall = wall };
        string file = _log.SaveReplay(r, CaptureIndex.ReplayFileName(id, r.ScenarioId, tag));
        _log.Write("capture", ("id", id), ("round", src.Round), ("tag", tag), ("note", note), ("tick", src.Tick),
            ("clock", Math.Round(src.Clock, 3)), ("partial", src.Partial), ("replay", file));
        _log.AppendCapture(new CaptureEntry
        {
            Id = id, Round = src.Round, Wall = wall, Scenario = r.ScenarioId, Attacker = r.AttackerId, Keeper = r.KeeperId,
            Tag = tag, Note = note, Tick = src.Tick, Clock = (float)Math.Round(src.Clock, 3), Partial = src.Partial,
        }, file);
        Hud.Toast($"Captured {id} · {tag}", 3f);
        GD.Print($"CAPTURE {id} round {src.Round} {tag}{(src.Partial ? " partial" : "")}");
    }

    /// <summary>
    /// Saves the round under way as round-NNN-&lt;scenario&gt;-unfinished.json (partial) and logs a round_abandoned line, when it
    /// began and has no report: on quit, and before a round is set up over it (closing the player editor with a change, for one).
    /// </summary>
    void SaveUnfinished(string reason)
    {
        if (_log == null || World is null || World.Recorder == null || World.State is RoundState.Setup or RoundState.Report) return;
        var r = World.Recorder.Snapshot();
        r.Partial = true;
        string file = _log.SaveReplay(r, $"round-{_round:D3}-{r.ScenarioId}-unfinished.json");
        _log.Write("round_abandoned", ("round", _round), ("reason", reason), ("tick", World.Tick), ("clock", Math.Round(World.Clock, 3)), ("replay", file));
        GD.Print($"ROUND {_round} UNFINISHED {r.ScenarioId} ({reason})");
    }

    /// <summary>A player_editor line for every preset save, load and clear: the slot (1-based), the column it came from and the outcome.</summary>
    void LogPresetAction(PresetResult r) => _log?.Write("player_editor", ("action", r.Action), ("slot", r.Slot + 1),
        ("column", r.FromPresets ? "presets" : "stats"), ("outcome", r.OutcomeName));

    // ---- player editor ----

    const string EditorStorePath = "user://player_editor.json";
    PlayerPresets _store = new();
    int _editorArmedTick = -1000;     // tick of the first open press that would replace unsaved Custom edits
    bool _editorReplacedCustom;       // this opening replaced Custom with a copy of the selected shipped attacker

    /// <summary>The attackers from data/players (Custom left out): what the editor's Base row steps through.</summary>
    System.Collections.Generic.List<PlayerSheet> ShippedAttackers() => _data.Attackers.Where(p => p != _data.Custom).ToList();

    /// <summary>Reads the editor's store from the user folder; Custom joins the attackers, and is selected if it was at quit.</summary>
    void LoadEditorStore()
    {
        _store = PlayerPresets.Load(ProjectSettings.GlobalizePath(EditorStorePath), out var warnings);
        foreach (var w in warnings)
        {
            _log?.Write("warning", ("file", w.File), ("field", w.Field), ("message", w.Message));
            GD.Print($"PLAYER_EDITOR {w}");
        }
        if (_store.Custom == null) return;
        _data.SetCustom(_store.Custom);
        if (_store.Active) _attackerId = PlayerPresets.CustomId;
        LogWarnings();
    }

    /// <summary>Writes the store (temporary file, then renamed over the old one). While the editor is open Custom is its working copy.</summary>
    void WriteEditorStore()
    {
        _store.Custom = _editor.IsOpen ? _editor.Work.Clone() : _data.Custom?.Clone();
        _store.Active = _attackerId == PlayerPresets.CustomId && _data.Custom != null;
        if (_store.Save(ProjectSettings.GlobalizePath(EditorStorePath)) is { } err)
        {
            GD.PrintErr("Player editor store not saved: " + err);
            Hud.Toast("Player editor: could not save (" + err + ")", 5f);
        }
    }

    /// <summary>
    /// Opens the editor. With Custom selected it edits Custom; with a shipped attacker selected, Custom is first replaced by a copy of
    /// it, which takes a second press when Custom holds edits that match no preset and no shipped attacker.
    /// </summary>
    void OpenEditor(bool force = false)
    {
        var shipped = ShippedAttackers();
        if (shipped.Count == 0) return;
        _editorReplacedCustom = false;
        if (_attackerId == PlayerPresets.CustomId && _data.Custom != null)
        {
            _editor.Open(_data.Custom, _store.Base, shipped, _store);
        }
        else
        {
            var cur = shipped.Find(p => p.Id == _attackerId) ?? shipped[0];
            var old = _data.Custom;
            bool loses = old != null && !_store.MatchesAPreset(old) && !shipped.Exists(p => PlayerPresets.SameValues(p, old));
            float window = 2f * Engine.PhysicsTicksPerSecond;
            if (loses && !force && _tick - _editorArmedTick > window)
            {
                _editorArmedTick = _tick;
                var (shownDevice, shownKind, _) = ShownDevice();
                string again = shownDevice == InputDevice.Gamepad ? Bindings.Pad.Retry.Label(shownKind) : Bindings.EditorOpen.Label;
                Hud.Toast($"Custom will be replaced: press {again} again", 2f);
                return;
            }
            _editorArmedTick = -1000;
            _editor.Open(cur, cur.Id, shipped, _store);
            _editorReplacedCustom = true;
        }
        var (dev, kind, _) = ShownDevice();
        _editor.SetDevice(dev, kind);
        _log?.Write("player_editor", ("action", "open"), ("attacker", _attackerId), ("base", _editor.BaseId));
    }

    /// <summary>
    /// Closes the editor. If the sheet changed, Custom becomes the attacker and the round is set up again with the same scenario, start
    /// spot and seed (a live round is abandoned); without a change nothing is set up again and no seed is used.
    /// </summary>
    void CloseEditor()
    {
        bool changed = _editor.Dirty;
        if (changed || _editorReplacedCustom)
        {
            _data.SetCustom(_editor.Work);
            _store.Base = _editor.BaseId;
        }
        _editor.Close();
        _log?.Write("player_editor", ("action", "close"), ("changed", changed));
        if (changed)
        {
            bool live = World.State is not (RoundState.Setup or RoundState.Report);
            bool atReport = World.State == RoundState.Report;
            _attackerId = PlayerPresets.CustomId;
            _log?.Write("selection", ("what", "editor"), ("attacker", _attackerId), ("applies_now", true), ("abandoned", live));
            // a finished round keeps its number (its replay is saved under it); a set-up or abandoned one is rebuilt under the same number
            SetUpRound(newRound: atReport, abandon: "player_editor");
            if (_paused) { _paused = false; _log?.Write("pause", ("paused", false)); }
        }
        WriteEditorStore();
    }

    string WarningLine()
    {
        var w = _data.Warnings;
        if (_figureWarning.Length > 0) return w.Count == 0 ? _figureWarning : $"{_figureWarning} (+{w.Count} data warnings)";
        if (w.Count == 0) return "";
        return w.Count == 1 ? w[0] : $"{w[0]} (+{w.Count - 1} more)";
    }
}
