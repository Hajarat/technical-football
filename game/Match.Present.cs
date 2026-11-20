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
    TickIntent _intent;          // the attacker's intent on the latest simulated tick (presentation only)
    int _looseSince = int.MinValue;   // the world tick the attacker last stopped being in possession
    public FigureStats FootStats { get; } = new();

    /// <summary>Everything the attacker's figure needs this tick, copied from the world's state and events. No gameplay is decided here.</summary>
    /// <summary>The figure's name for a skill move's kind (the moves added after the shots have their own values).</summary>
    static SkillKind SkillKindOf(ActionKind k) => k switch
    {
        ActionKind.Elastico => SkillKind.Elastico, ActionKind.Spin => SkillKind.Spin, ActionKind.HeelToHeel => SkillKind.HeelToHeel,
        _ => (SkillKind)((int)k - (int)ActionKind.StepOver),
    };

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
                Valid = true, Kind = SkillKindOf(sk.Kind), Id = sk.StartTick,
                Side = sk.Side == SkillSide.Left ? -1f : sk.Side == SkillSide.Right ? 1f : 0f,
                Right = sk.Foot == Foot.Right, Surface = (BootSurface)(int)sk.Surface,
                Elapsed = (p.CurrentTick - sk.StartTick) * f.Dt, Duration = (sk.EndTick - sk.StartTick) * f.Dt,
                ExecElapsed = (p.CurrentTick - sk.ExecuteTick) * f.Dt, ExecDuration = (sk.EndTick - sk.ExecuteTick) * f.Dt,
                ContactElapsed = (p.CurrentTick - sk.ContactTick) * f.Dt, ReachSpan = (sk.ContactTick - sk.StartTick) * f.Dt,
                ContactPoint = G(sk.ContactPoint), OutVelocity = G(sk.BallOutVelocity),
                Contact2Elapsed = sk.Contact2Tick >= 0 ? (p.CurrentTick - sk.Contact2Tick) * f.Dt : float.NaN,
                LiftElapsed = sk.LiftTick >= 0 ? (p.CurrentTick - sk.LiftTick) * f.Dt : float.NaN,
                Right2 = sk.Foot2 == Foot.Right, Surface2 = (BootSurface)(int)sk.Surface2,
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
        if (p.Action is { IsShot: true, Phase: not ActionPhase.Done } sa)
            f.Shot = new FigureShot
            {
                Valid = true, Kind = (ShotKind)((int)sa.Kind - (int)ActionKind.Power), Right = sa.Foot == Foot.Right, Surface = (BootSurface)(int)sa.Surface,
                Id = sa.PlanTick, Phase = (TouchPhase)(int)sa.Phase,
                Elapsed = (p.CurrentTick - sa.PlanTick) * f.Dt, ContactEta = sa.ContactEta(p.CurrentTick, f.Dt), RecoverySeconds = (sa.EndTick - sa.ContactTick) * f.Dt,
                ContactPoint = G(sa.ContactPoint), BallOutVelocity = G(sa.BallOutVelocity), Made = sa.Made,
                Held = !sa.Released,
                Aim = new Vector3(w.Pitch.GoalLineX - G(sa.ContactPoint).X, 0, -G(sa.ContactPoint).Z),
            };
        f.Upcoming = UpcomingTouch(p, out f.UpcomingReady);
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
        f.KeeperAction = k.KeeperAction; f.KeeperDive = k.KeeperDive; f.KeeperActionTime = k.KeeperActionTime;
        f.Holding = k.KeeperAction == KeeperAction.Holding;
        f.HandPosition = G(k.HandPosition);
        f.SaveTarget = G(k.SaveTarget); f.SaveContactTime = k.SaveContactTime;
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
        f.PrevAction = ActionFrame(p.PreviousTouchAction, p, dt);   // a touch made just before a shot's soft touch still follows through
    }

    /// <summary>
    /// The touch the core is about to make at once, with no windup of its own (an immediate touch: the exit touch on the tick a skill
    /// move ends, the soft touch that keeps the ball at the foot during a shot's windup, the first touch of a loose ball), predicted
    /// from the current state and stick so the figure can swing the right foot toward it beforehand. Presentation only: it reads the
    /// world and decides nothing in it; the core's own touch, when it comes, is drawn from <see cref="FigureFrame.Action"/> as usual.
    /// <paramref name="ready"/> is set for a still ball at the feet of a standing player, where the touch waits for the stick.
    /// </summary>
    FigureAction UpcomingTouch(PlayerState p, out bool ready)
    {
        ready = false;
        var w = World;
        var t = w.Tuning;
        float dt = t.Dt, r = w.Ball.Radius;
        const int Horizon = 18;   // ticks looked ahead (0.15 s)
        if (p.InPossession) _looseSince = int.MinValue;
        else if (_looseSince == int.MinValue) _looseSince = p.CurrentTick;
        if (w.Keeper.InPossession || w.State is RoundState.Report or RoundState.Outcome) return default;
        var ball = w.Ball;
        var stick = new System.Numerics.Vector2(_intent.MoveX, _intent.MoveZ);
        bool stickOn = stick.Length() >= t.Touch.StickThreshold;
        var pv = new System.Numerics.Vector3(p.Velocity.X, 0f, p.Velocity.Z);
        var bv = new System.Numerics.Vector3(ball.Velocity.X, 0f, ball.Velocity.Z);
        float reach = TouchModel.FootReach(p.Sheet, t);
        bool InReachAt(int j, float margin = 0f)
        {
            var pp = p.Position + pv * (j * dt);
            var bp = ball.Position + bv * (Math.Max(0, j - 1) * dt);   // the player moves before the touch on a tick, the ball after it
            return ball.Position.Y <= t.Touch.MaxBallHeight && new System.Numerics.Vector2(bp.X - pp.X, bp.Z - pp.Z).Length() <= reach + margin;
        }
        Foot FootAt(int j)
        {
            if (p.IsStepping) { float s = (p.GaitPhase + p.StepFrequency * j * dt) % 2f; return Gait.SwingFoot(s); }
            return TouchModel.ChooseFoot(p.Sheet, p.Position + pv * (j * dt), p.Facing, ball.Position + bv * (Math.Max(0, j - 1) * dt), t);
        }

        int j = -1, anchor; ActionKind kind; System.Numerics.Vector3 dir;
        float turnRef = MathF.Atan2(bv.Z, bv.X);
        if (p.Action is { IsSkill: true } sk && sk.Phase != ActionPhase.Done)
        {
            // the exit touch: made on the move's last tick if the stick is pushed and the ball is in reach then
            int n = sk.EndTick - p.CurrentTick;
            // (with some slack: the legs and the ball change course on that tick; a touch drawn for but not made fades back)
            if (n < 1 || n > Horizon || !stickOn || !InReachAt(n, 0.05f)) return default;
            j = n; anchor = sk.StartTick;
            dir = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(stick.X, 0f, stick.Y));
            var mode = TouchModel.ModeFor(_intent, t);
            kind = mode == TouchMode.CloseControl ? ActionKind.Close : mode == TouchMode.Jog ? ActionKind.Jog : ActionKind.Sprint;
            // the ball's travel on that tick: a ball roll leaves it rolling along the run at the player's pace, a drag-back or fake stop at rest
            var rd = new System.Numerics.Vector3(sk.RunDirection.X, 0f, sk.RunDirection.Y);
            var bvEnd = sk.Kind == ActionKind.BallRoll ? rd * MathF.Max(0f, System.Numerics.Vector3.Dot(pv, rd))
                : sk.Kind is ActionKind.DragBack or ActionKind.FakeStop ? System.Numerics.Vector3.Zero : bv;
            float refA = bvEnd.Length() > 0.3f ? MathF.Atan2(bvEnd.Z, bvEnd.X) : p.Facing;
            turnRef = refA;
            float turnDeg = MathF.Abs(Movement.WrapAngle(MathF.Atan2(dir.Z, dir.X) - refA)) * 180f / MathF.PI;
            if (p.RunSpeed > t.Touch.ExecutionMinSpeed && turnDeg > t.Touch.ExecutionAngle) kind = ActionKind.Turn;
            if (turnDeg > t.Touch.CutDeg && p.RunSpeed > t.Touch.CutMinSpeed) kind = ActionKind.Cut;
        }
        else if (p.Action is { IsShot: true, Made: false } shot && p.InPossession
            // a touch already winding up at the press is waited for; one planned after it is dropped for the soft touch
            && (p.TouchAction is not { Pending: true } pt || pt.PlanTick > shot.PlanTick)
            && p.TouchAction is not { Kind: ActionKind.Soft, Made: true } && p.PreviousTouchAction is not { Kind: ActionKind.Soft, Made: true })
        {
            // the soft touch: made on the first tick of the windup the ball is in reach
            for (int k = 1; k <= Horizon && j < 0; k++) if (InReachAt(k)) j = k;
            if (j < 0) return default;
            anchor = shot.PlanTick; kind = ActionKind.Soft;
            dir = pv.Length() > 0.5f ? System.Numerics.Vector3.Normalize(pv) : new System.Numerics.Vector3(MathF.Cos(p.Facing), 0f, MathF.Sin(p.Facing));
        }
        else if (!p.InPossession && p.Action is not { IsShot: true } && w.State is RoundState.Setup or RoundState.Live)
        {
            // the first touch of a loose ball: in reach and either the stick pushed or the ball moving against the player
            anchor = _looseSince == int.MinValue ? -1 : _looseSince; kind = ActionKind.First;
            bool still = (bv - pv).Length() < t.Touch.FirstTouchMinRelativeSpeed;
            if (still && !p.IsStepping && InReachAt(0) && !stickOn && w.SimTime >= p.StunEndTime)
            {
                ready = true; j = 0;
                dir = new System.Numerics.Vector3(MathF.Cos(p.Facing), 0f, MathF.Sin(p.Facing));
            }
            else
            {
                if (!stickOn && still) return default;
                // no touch until the stun after a strike is over
                for (int k = 1; k <= Horizon && j < 0; k++) if (InReachAt(k) && w.SimTime + k * dt >= p.StunEndTime) j = k;
                if (j < 0) return default;
                dir = stickOn ? System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(stick.X, 0f, stick.Y))
                    : pv.Length() > 0.5f ? System.Numerics.Vector3.Normalize(pv) : new System.Numerics.Vector3(MathF.Cos(p.Facing), 0f, MathF.Sin(p.Facing));
            }
        }
        else return default;

        var foot = FootAt(j);
        var c = ball.Position + bv * (Math.Max(0, j - 1) * dt);
        float turnSign = MathF.Sign(Movement.WrapAngle(MathF.Atan2(dir.Z, dir.X) - turnRef));
        var surface = kind switch
        {
            ActionKind.Soft => FootSurface.Sole,
            ActionKind.Sprint => FootSurface.Laces,
            ActionKind.Turn or ActionKind.Cut => (turnSign > 0f) == (foot == Foot.Right) ? FootSurface.Outside : FootSurface.Inside,
            _ => FootSurface.Inside,
        };
        var cp = new System.Numerics.Vector3(c.X, r, c.Z);
        return new FigureAction
        {
            Valid = true, Kind = (int)kind, Phase = TouchPhase.Windup, Right = foot == Foot.Right, Surface = (BootSurface)(int)surface,
            // stable for one predicted touch while its tick estimate moves: a negative id per kind and the tick its situation began
            Id = -1 - ((int)kind << 22) - (anchor & 0x3FFFFF), ContactTick = p.CurrentTick + j, ContactEta = j * dt, RecoverySeconds = 0f,
            ContactPoint = G(cp), BallOutVelocity = Vector3.Zero, NextMeetPoint = G(cp + dir * 2f), ContactMiss = float.NaN,
        };
    }

    static FigureAction ActionFrame(BallAction? a, PlayerState p, float dt)
    {
        if (a == null || a.IsShot) return default;   // a shot is drawn from FigureFrame.Shot
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
        _intent = default; _looseSince = int.MinValue;
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
        FootStats.Tick = _tick;
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
        if (w.Attacker.Action is { IsShot: true } shot && shot.PlanTick != _shotId)
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
