using System;
using FootballSim.Core;
using Godot;

namespace FootballSim.Game;

public partial class Match
{
    System.Numerics.Vector3 _strikeOrigin;
    bool _hasStrike;
    int _shotId = int.MinValue;   // press tick of the shot whose type the HUD last named
    int _shotsSeen;

    int _strikeTicks, _touchCount;
    bool _closeControl;
    public FigureStats FootStats { get; } = new();

    /// <summary>Everything the attacker's figure needs this tick, copied from the world's state and events. No gameplay is decided here.</summary>
    FigureFrame AttackerFrame(PlayerState p)
    {
        var w = World;
        var f = FigureFrame.At(G(p.Position), p.Facing);
        f.Velocity = G(p.Velocity);
        f.HeightM = p.Sheet.HeightM;
        f.InPossession = p.InPossession;
        f.CloseControl = _closeControl;
        f.BallPos = G(w.Ball.Position); f.BallVel = G(w.Ball.Velocity); f.BallRadius = w.Ball.Radius;
        f.Dt = w.Tuning.Dt;
        if (p.Action is { IsSkill: true } sk && sk.Phase != ActionPhase.Done)
            f.Skill = new FigureSkill
            {
                Valid = true, Kind = (SkillKind)((int)sk.Kind - (int)ActionKind.StepOver), Id = sk.StartTick,
                Side = sk.Side == SkillSide.Left ? -1f : sk.Side == SkillSide.Right ? 1f : 0f,
                Right = sk.Foot == Foot.Right, Surface = (BootSurface)(int)sk.Surface,
                Elapsed = (p.CurrentTick - sk.StartTick) * f.Dt, Duration = (sk.EndTick - sk.StartTick) * f.Dt,
                ExecElapsed = (p.CurrentTick - sk.ExecuteTick) * f.Dt, ExecDuration = (sk.EndTick - sk.ExecuteTick) * f.Dt,
                ContactElapsed = (p.CurrentTick - sk.ContactTick) * f.Dt, ReachSpan = (sk.ContactTick - sk.StartTick) * f.Dt,
                ContactPoint = G(sk.ContactPoint), OutVelocity = G(sk.BallOutVelocity),
            };
        foreach (var e in w.Events)
        {
            if (e.Team is not (-1 or 0) || e.PlayerIndex is not (-1 or 0)) continue;
            // a skill move's sole contact also raises a Touch: it is drawn by the move, never as a dribble touch
            if (e.Kind == EventKind.Touch && f.Skill.Valid) f.SkillTouch = true;
            else if (e.Kind == EventKind.Touch) { f.TouchEvent = true; f.EventRight = e.Foot == Foot.Right; f.BallPos = G(e.Position); }
            else if (e.Kind == EventKind.Strike) { f.StrikeEvent = true; f.EventRight = e.Foot == Foot.Right; f.BallPos = G(e.Position); }
        }
        FillGait(ref f, p);
        if (p.Action is { Kind: >= ActionKind.Power, Phase: not ActionPhase.Done } sa)
            f.Shot = new FigureShot
            {
                Valid = true, Kind = (ShotKind)((int)sa.Kind - (int)ActionKind.Power), Right = sa.Foot == Foot.Right, Surface = (BootSurface)(int)sa.Surface,
                Id = sa.PlanTick, Phase = (TouchPhase)(int)sa.Phase,
                Elapsed = (p.CurrentTick - sa.PlanTick) * f.Dt, ContactEta = sa.ContactEta(p.CurrentTick, f.Dt), RecoverySeconds = (sa.EndTick - sa.ContactTick) * f.Dt,
                ContactPoint = G(sa.ContactPoint), BallOutVelocity = G(sa.BallOutVelocity), Made = sa.Made,
                Held = !sa.Released,
            };
        f.NextTouchEta = p.NextTouchEta; f.NextTouchRight = p.NextTouchFoot == Foot.Right; f.NextTouchPoint = G(p.NextTouchPoint);
        f.ShotCharge = w.ShotCharge;
        // The core counts StrikeEta down through the strike delay and fixes StrikeFoot at the button press.
        f.StrikeEta = p.StrikeEta; f.StrikeRight = p.StrikeFoot == Foot.Right;
        return f;
    }

    FigureFrame KeeperFrame(PlayerState k)
    {
        var f = FigureFrame.At(G(k.Position), k.Facing);
        f.Velocity = G(k.Velocity);
        f.HeightM = k.Sheet.HeightM;
        f.Dt = World.Tuning.Dt;
        f.Diving = k.KeeperAction == KeeperAction.Diving;
        f.Holding = k.KeeperAction == KeeperAction.Holding;
        f.Reacting = k.KeeperAction == KeeperAction.Reacting;
        f.HandPosition = G(k.HandPosition);
        f.BallPos = G(World.Ball.Position);
        FillGait(ref f, k);
        return f;
    }

    /// <summary>The simulation's stride clock and touches as plain values: the figure draws them and decides no timing of its own.</summary>
    void FillGait(ref FigureFrame f, PlayerState p)
    {
        float dt = World.Tuning.Dt;
        f.HasGait = true; f.IsStepping = p.IsStepping; f.SwingRight = p.SwingFoot == Foot.Right;
        f.GaitPhase = p.GaitPhase; f.StepFrequency = p.StepFrequency; f.SwingProgress = p.SwingProgress;
        f.RightFootPhase = p.RightFootPhase; f.LeftFootPhase = p.LeftFootPhase;
        f.RunSpeed = p.RunSpeed;
        f.Action = ActionFrame(p.TouchAction, p, dt);   // the touch planner's slot: the soft touch at a shot's press is drawn under the shot windup
        f.PrevAction = ActionFrame(p.PreviousAction, p, dt);
    }

    static FigureAction ActionFrame(BallAction? a, PlayerState p, float dt)
    {
        if (a == null || a.Kind >= ActionKind.Power) return default;   // a shot is drawn from FigureFrame.Shot
        return new FigureAction
        {
            Valid = true, Kind = (int)a.Kind, Phase = (TouchPhase)(int)a.Phase, Right = a.Foot == Foot.Right, Surface = (BootSurface)(int)a.Surface,
            Id = a.PlanTick, ContactTick = a.ContactTick, ContactEta = a.ContactEta(p.CurrentTick, dt), RecoverySeconds = (a.EndTick - a.ContactTick) * dt,
            ContactPoint = G(a.ContactPoint), BallOutVelocity = G(a.BallOutVelocity),
            NextMeetPoint = a.NextMeetTick >= 0 ? G(a.NextMeetPoint) : new Vector3(float.NaN, 0, 0),
            ContactMiss = a.ContactMiss, Made = a.Made, IsSkill = a.IsSkill,
        };
    }

    static Vector3 G(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>A new world was built: snap every view to it and clear the overlay.</summary>
    void ResetPresentation()
    {
        _hasStrike = false; _shotId = int.MinValue;
        _shotsSeen = 0;
        Overlay.ClearWedge(); Overlay.ClearAimed(); Overlay.ClearLanded(); Overlay.ClearKeeperReach();
        Hud.HideReport();
        Hud.SetPower(0); Hud.SetIdealMarker(0, false);
        _strikeTicks = 0; _touchCount = 0;
        Ball.Teleport(G(World.Ball.Position));
        Ball.SetPossession(false);
        Attacker.Teleport(AttackerFrame(World.Attacker), World.Attacker.InPossession);
        Keeper.Teleport(KeeperFrame(World.Keeper));
        Camera.Snap(Ball.Position);
        UpdateHudText();
    }

    /// <summary>Copy the world's state onto the views, HUD and overlay. Runs once per physics tick, before the camera follows.</summary>
    void Present()
    {
        var w = World;
        Ball.Apply(G(w.Ball.Position));
        Ball.SetPossession(w.Attacker.InPossession || w.Keeper.InPossession);
        var af = AttackerFrame(w.Attacker);
        Attacker.Apply(af, w.Attacker.InPossession);
        Keeper.Apply(KeeperFrame(w.Keeper));
        if (af.TouchEvent || af.StrikeEvent)
        {
            if (af.TouchEvent && !af.StrikeEvent) _touchCount++;
            if (_replay != null)
                GD.Print($"FOOT_EVENT tick={_tick} kind={(af.StrikeEvent ? "strike" : "touch")} foot={(af.EventRight ? "R" : "L")} err_m={Attacker.Figure.LastContactError:F3}");
        }

        Hud.SetPower(w.ShotCharge);
        Hud.SetIdealMarker(w.IdealCharge, w.ShotCharge > 0.001f);
        if (w.Attacker.Action is { Kind: >= ActionKind.Power } shot && shot.PlanTick != _shotId)
        {
            _shotId = shot.PlanTick;
            Hud.ShowShotType(shot.Kind switch { ActionKind.Finesse => "FINESSE", ActionKind.Chip => "CHIP", ActionKind.Driven => "DRIVEN", ActionKind.LowFinesse => "LOW FINESSE", _ => "POWER" });
        }
        Hud.SetClock(w.Clock);
        if (w.State == RoundState.Report && w.Report != null)
            Hud.ShowReport(ReportText.Title(w.Report), ReportText.Lines(w.Report));
        else Hud.HideReport();

        if (w.State == RoundState.Flight) _flightTicks++;
        if (w.State == RoundState.Live) _liveTicks++;
        foreach (var e in w.Events)
        {
            if (e.Kind == EventKind.Strike) { _strikeOrigin = e.Position; _hasStrike = true; }
            Rumble(e);
        }
        UpdateOverlay();
    }

    void UpdateHudText()
    {
        var sc = World.Scenario;
        string hint = World.State == RoundState.Setup ? "  ·  A/Enter to start" : "";
        Hud.SetSpotLine($"Round {_round}  ·  {sc.Name}  ·  {World.Attacker.Sheet.Name} vs {World.Keeper.Sheet.Name}{hint}");
    }

    void UpdateOverlay()
    {
        var w = World;
        float gx = w.Pitch.GoalLineX;
        if (w.Shots.Count > 0 && _hasStrike)
        {
            var s = w.Shots[^1];
            WedgeFrom(_strikeOrigin, s.IntendedYaw, s.SigmaH, gx);
            Overlay.SetAimed(new Vector3(gx, s.Aimed.Y, s.Aimed.X));
            if (!float.IsNaN(s.Landed.X)) Overlay.SetLanded(new Vector3(gx, s.Landed.Y, s.Landed.X));
            float reach = w.KeeperReport.ReachM;
            if (float.IsFinite(reach)) Overlay.SetKeeperReach(G(w.Keeper.Position), reach);
        }
        else if (w.PreviewShot() is { } p)
        {
            WedgeFrom(p.Origin, p.IntendedYaw, p.SigmaH, gx);
        }
        else if (w.Shots.Count == 0)
        {
            Overlay.ClearWedge();
        }
    }

    /// <summary>Wedge spanning the intended direction +/- 2 sigma_h, out to the goal line.</summary>
    void WedgeFrom(System.Numerics.Vector3 origin, float yaw, float sigmaDeg, float goalX)
    {
        float spread = 2f * sigmaDeg * Mathf.Pi / 180f;
        float cos = MathF.Cos(yaw);
        float length = cos > 0.05f ? Math.Clamp((goalX - origin.X) / cos, 3f, 70f) : 20f;
        Vector3 Dir(float a) => new(MathF.Cos(a), 0, MathF.Sin(a));
        Overlay.SetWedge(G(origin), Dir(yaw - spread), Dir(yaw + spread), length);
    }

    // ---- feedback ----

    void Rumble(SimEvent e)
    {
        (float weak, float strong, float secs) = e.Kind switch
        {
            EventKind.Strike => (0.3f, 0.2f, 0.12f),
            EventKind.FrameHit => (0.6f, 0.5f, 0.2f),
            EventKind.Goal => (1.0f, 0.9f, 0.6f),
            _ => (0f, 0f, 0f),
        };
        if (secs <= 0f) return;
        foreach (int id in Input.GetConnectedJoypads()) Input.StartJoyVibration(id, weak, strong, secs);
    }

    // ---- screenshot moments for --shot-on ----

    bool MomentReached(string name) => name switch
    {
        "setup" => World.State == RoundState.Setup && _tick >= 2,
        "live" => World.State == RoundState.Live && _liveTicks >= 60,
        "charge" => World.State == RoundState.Live && World.ShotCharge >= 0.5f,
        _ when name.StartsWith("touch") && int.TryParse(name[5..], out int n) => _touchCount >= n,
        "strike" => World.State == RoundState.Strike,
        "skill" => World.Attacker.Action is { IsSkill: true },
        "flight" => World.State == RoundState.Flight && _flightTicks >= 24,
        "outcome" or "report" => World.State == RoundState.Report,
        _ => false,
    };
}
