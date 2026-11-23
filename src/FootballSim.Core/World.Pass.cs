using System.Numerics;

namespace FootballSim.Core;

// Passes in the world: the pass buttons beside the shoot button, the strike that writes the pass record and moves control to
// the receiver, and the receiver's exclusive claim on the pass. A pass reuses the shot's pipeline (the same wind-up action,
// striking-foot rules and contact on the touch controller's next swing); what differs is here.
public sealed partial class World
{
    PassKind _passKind;         // the pass under way, from its press to its strike (None: a shot, or nothing)
    bool _passWasHeld;          // a pass button was held on the previous tick
    bool _passCancelled;        // the rest of this press of a pass button is ignored (released for the player, or held across a jump)
    float _passCharge;          // 0..1 while the pass button is held
    Vector2 _passStick;         // the left stick that points the pass, the latest while it is held
    PassRecord? _pass;          // the pass on its way

    /// <summary>The pass on its way (struck, not yet touched by anyone), or null.</summary>
    public PassRecord? Pass => _pass;
    /// <summary>0..1 while a pass button is held: how far along the stick the pass is pointed. 0 otherwise.</summary>
    public float PassCharge { get; private set; }
    /// <summary>The kind of pass winding up (from its press to its strike), or None.</summary>
    public PassKind PassUnderWay => _passKind;
    /// <summary>While a pass winds up: the team-mate it would go to if struck now (chosen again every tick), or null.</summary>
    public PlayerState? PassReceiver { get; private set; }
    /// <summary>True while a shot (not a pass) winds up: from its press to its strike.</summary>
    public bool ShotWindup => Attacker.ShotAction is { Made: false } && _passKind == PassKind.None;

    void ResetPass()
    {
        _passKind = PassKind.None; _passWasHeld = false; _passCharge = 0f; _passStick = default;
        // (a pass button already down when the round starts, as the button that started it, is not a press)
        _passCancelled = true;
        _pass = null; PassCharge = 0f; PassReceiver = null; _reactTick = -1;
    }

    partial void ResetPassInput(PlayerState old)
    {
        _passKind = PassKind.None; _passCharge = 0f; PassCharge = 0f; PassReceiver = null;
        // (a pass button held across the jump is not a fresh press for the new player)
        _passCancelled = _lastIntent.PassHeld || _lastIntent.ThroughHeld;
    }

    /// <summary>True when <paramref name="m"/> would be offside if the pass were struck now (false with offside off).</summary>
    bool OffsideNow(PlayerState m) => IsBeyondOffsideLine(m);

    partial void OffsideAtStrike(PlayerState striker, PlayerState? receiver, ref bool offence);
    bool IsBeyondOffsideLine(PlayerState m)
    {
        bool beyond = false;
        BeyondLine(m, ref beyond);
        return beyond;
    }
    partial void BeyondLine(PlayerState m, ref bool beyond);

    /// <summary>The yaw a pass struck now would leave along: toward its receiver as he stands, or along the stick with none.</summary>
    float PassAimYaw(PlayerState p)
    {
        var aim = PassModel.Aim(p, _passStick, Tuning);
        PassReceiver = PassModel.ChooseReceiver(p, Teams[0], Ball.Position, aim, _passCharge, _passKind, OffsideNow, Tuning);
        if (PassReceiver is { } r)
        {
            var to = Flat(r.Position) - Flat(Ball.Position);
            if (to.LengthSquared() > 1e-6f) return MathF.Atan2(to.Y, to.X);
        }
        return MathF.Atan2(aim.Y, aim.X);
    }

    /// <summary>
    /// A pass button pressed in Live, with the ball or holding the team's claim on it: the pass winds up like a shot (the same
    /// action, of the side-foot kind; the same soft touch with the ball at the feet), and its strike is the touch controller's
    /// next contact on the chosen foot from the execution time on.
    /// </summary>
    void PressPass(PlayerState p, bool inPossession, PassKind kind)
    {
        var tuning = Tuning;
        _passKind = kind;
        _passCharge = 0f;
        _passStick = _lastStick;
        _shotStartTick = Tick;
        _fakeTick = -1;
        SkillMoves.Abort(p);
        TouchPlanner.UpdatePhases(p, Tick, tuning);
        _shotAtFoot = inPossession && p.TouchAction is not { Pending: true } && TouchModel.InReach(p, Ball, tuning);
        _softPending = _shotAtFoot;
        p.StrikeContactTick = -1;
        _strikeEstMax = -1;
        var shot = new BallAction
        {
            Kind = ActionKind.Finesse,
            Phase = ActionPhase.Windup,
            PlanTick = Tick,
            ContactPoint = new Vector3(Ball.Position.X, Ball.Radius, Ball.Position.Z),
            Pass = kind,
        };
        p.ShotAction = shot;
        _strikeSurface = ShotModel.SurfaceFor(ShotType.Finesse);
        _aimYaw = PassAimYaw(p);
        int earliest = EarliestStrikeTick(Tick);
        ChooseStrike(p, ShotType.Finesse, earliest);
        shot.Foot = _strikeFoot;
        shot.Surface = _strikeSurface;
        shot.AimYaw = _aimYaw;
        shot.ContactTick = earliest;
    }

    /// <summary>
    /// The pass button held: the charge grows over pass.charge_seconds (it points the pass further along the stick), the aim
    /// follows the receiver chosen this tick, and at a full charge or when the button is let go the pass is released.
    /// </summary>
    void HoldPass(PlayerState p, in TickIntent it, bool held)
    {
        _passStick = it.Move;
        _passCharge = MathF.Min(1f, _passCharge + _dt / MathF.Max(1e-3f, Tuning.Pass.ChargeSeconds));
        PassCharge = _passCharge;
        _aimYaw = PassAimYaw(p);
        if (p.ShotAction is { } shot) shot.AimYaw = _aimYaw;
        if (!held || _passCharge >= 1f) ReleasePass(p, it);
    }

    /// <summary>
    /// The pass released (the button let go, or the full hold reached): its charge and stick are fixed, and its earliest contact
    /// is the execution time after the press. The striking foot is chosen for the direction to the receiver and fixed.
    /// </summary>
    void ReleasePass(PlayerState p, in TickIntent it)
    {
        _aimYaw = PassAimYaw(p);
        int contact = EarliestStrikeTick(Tick);
        Foot before = _strikeFoot;
        _strikeSurface = ShotModel.SurfaceFor(ShotType.Finesse);
        ChooseStrike(p, ShotType.Finesse, contact);
        if (_strikeFoot != before) p.ClearForecast();
        _pending = new PendingShot
        {
            Active = true,
            Charge = _passCharge,
            Stick = _passStick,
            Type = ShotType.Finesse,
            Foot = _strikeFoot,
            Surface = _strikeSurface,
            AimYaw = _aimYaw,
            ContactTick = contact,
            DeadlineTick = _controller == p ? int.MaxValue : contact + Ticks(Tuning.Round.ShootBuffer),
            Pass = _passKind,
        };
        if (p.ShotAction is { } shot)
        {
            shot.Foot = _strikeFoot;
            shot.Surface = _strikeSurface;
            shot.AimYaw = _aimYaw;
            shot.ContactTick = contact;
            shot.Released = true;
        }
        SkillMoves.Abort(p);
        // (released for the player at the full hold: the rest of that press is spent)
        _passCancelled = it.PassHeld || it.ThroughHeld;
    }

    /// <summary>
    /// The pass struck: the receiver, target and speed are worked out now (<see cref="PassModel.Plan"/>), the ball leaves rolling
    /// with no direction error, the passer is planted for pass.stun_seconds, the pass record is written, the offside positions are
    /// taken, and control jumps to the named receiver on this tick. <paramref name="flick"/>: struck at the first contact with no
    /// time for it, at pass.flick_speed of the speed cap.
    /// </summary>
    void ExecutePass(bool flick)
    {
        var a = Attacker;
        var kind = _pending.Pass;
        var plan = PassModel.Plan(a, Teams[0], Teams[1], Ball.Position, _pending.Stick, _pending.Charge, kind, _pending.Foot, OffsideNow,
            OpponentReactionSeconds(), Pitch, Tuning, flick ? Tuning.Pass.FlickSpeed : 1f);
        var launch = new Vector3(plan.Direction.X, 0f, plan.Direction.Y) * plan.Speed;
        if (a.ShotAction is { } shot)
        {
            shot.Foot = _pending.Foot;
            shot.Surface = _pending.Surface;
            shot.Made = true;
            shot.ContactTick = Tick;
            shot.ContactPoint = Ball.Position;
            shot.ContactMiss = 0f;
            shot.BallOutVelocity = launch;
            shot.BallOutSpin = Ball.RollingSpin(launch);
            shot.AimYaw = MathF.Atan2(plan.Direction.Y, plan.Direction.X);
            shot.EndTick = Tick + Ticks(Tuning.Shot.RecoverySeconds);
            shot.Phase = ActionPhase.Contact;
        }
        a.StunUntil = SimTime + Tuning.Pass.StunSeconds;
        _softPending = false;
        _shotAtFoot = false;
        TouchPlanner.OnShotEnded(a);
        a.TurnFatigue = 0f;
        Ball.Velocity = launch;
        Ball.Spin = Ball.RollingSpin(launch);
        a.LastTouchTime = SimTime;
        a.LastTouchFoot = _pending.Foot;
        a.StrikeFoot = _pending.Foot;
        Foot foot = _pending.Foot;
        _pending = default;
        _passKind = PassKind.None;
        PassCharge = 0f;
        PassReceiver = null;
        _ballPlayed = true;
        _passes++;
        _events.Add(new SimEvent(EventKind.Pass, Ball.Position, plan.Speed, Foot: foot, Team: a.Team, PlayerIndex: a.Index, Other: plan.Receiver?.Index ?? -1));
        SetController(null); // a struck ball is nobody's
        if (State == RoundState.Strike) State = RoundState.Live;
        // The expected arrival: when the ball comes within the receiver's foot reach of the target.
        float len = TouchModel.HorizontalDistance(Ball.Position, plan.Target);
        float reach = plan.Receiver != null ? TouchModel.FootReach(plan.Receiver.Sheet, Tuning) : 0f;
        float arrive = PassModel.TravelSeconds(MathF.Max(0f, len - reach), plan.Speed, Tuning);
        _pass = new PassRecord
        {
            Passer = a, Kind = kind, Receiver = plan.Receiver, Target = plan.Target, StrikeTick = Tick,
            ExpectedArrivalTick = Tick + (float.IsFinite(arrive) ? (int)MathF.Ceiling(arrive * Tuning.TickRate) : Ticks(Tuning.LockOn.HorizonSeconds)),
        };
        StartReaction();
        // Offside is judged on the positions at the strike: a named receiver beyond the line ends the round here.
        bool offence = false;
        OffsideAtStrike(a, plan.Receiver, ref offence);
        if (offence) return;
        if (plan.Receiver != null) JumpTo(plan.Receiver.Index, ControlReason.Pass);
    }

    int _passes, _received, _controlChanges;
    /// <summary>Passes struck this round.</summary>
    public int Passes => _passes;
    /// <summary>Passes a team-mate's first touch received this round.</summary>
    public int PassesReceived => _received;

    /// <summary>
    /// The reaction delay the pass planner allows the defender for a pass struck now, seconds: his reaction distance (see
    /// <see cref="StartReaction"/>) at an assumed ball speed, no more than lock_on.reaction_max_seconds; 0 without a team-mate.
    /// </summary>
    float OpponentReactionSeconds()
    {
        if (!HasMates || Defender is not { } d) return 0f;
        float need = TouchModel.HorizontalDistance(d.Position, Ball.Position) * MathF.Max(0f, Tuning.LockOn.ReactionRatio.Evaluate(d.Sheet.Awareness));
        return MathF.Min(Tuning.LockOn.ReactionMaxSeconds, need / ReactionSpeedGuess);
    }

    /// <summary>The ball speed the pass planner assumes when it turns the defender's reaction distance into a time, m/s.</summary>
    const float ReactionSpeedGuess = 10f;

    int _reactTick = -1;        // the tick of the attackers' last strike the defender is still reacting to, or -1
    Vector3 _reactBall;         // the ball at that strike
    float _reactDistance;       // the ball must travel this far before he reacts, metres

    /// <summary>
    /// A strike by the attackers in a scenario with a team-mate: the computer-controlled defender reacts to it only once the
    /// ball has travelled his distance to it at the strike times lock_on.reaction_ratio (by his awareness), or
    /// lock_on.reaction_max_seconds have passed. Until then he makes no claim run and holds to where the ball was struck.
    /// </summary>
    void StartReaction()
    {
        if (!HasMates || Defender is not { } d) return;
        _reactTick = Tick;
        _reactBall = Ball.Position;
        _reactDistance = TouchModel.HorizontalDistance(d.Position, Ball.Position) * MathF.Max(0f, Tuning.LockOn.ReactionRatio.Evaluate(d.Sheet.Awareness));
    }

    /// <summary>True while the defender has not yet reacted to the attackers' last strike (see <see cref="StartReaction"/>).</summary>
    public bool DefenderReacting => _reactTick >= 0 && _controller == null
        && (Tick - _reactTick) * _dt < Tuning.LockOn.ReactionMaxSeconds
        && TouchModel.HorizontalDistance(Ball.Position, _reactBall) < _reactDistance;

    /// <summary>While a pass is on its way: where it will be met (its receiver's meeting point, or its target until he has one). Null with no pass.</summary>
    public Vector3? PassMeeting => _pass == null ? null : _claims[0].Player != null ? _claims[0].Point : _pass.Target;

    /// <summary>
    /// What the defender's bot anchors on with the ball loose in a scenario with a team-mate: where the ball was struck while he
    /// has not reacted, then the attackers' meeting point with it (their claim), or the ball when they have none.
    /// </summary>
    public Vector3 DefenderAnchor => DefenderReacting ? _reactBall : _claims[0].Player != null ? _claims[0].Point : Ball.Position;

    /// <summary>
    /// True when the defender goes for the loose ball himself: always in a scenario without a team-mate; with one, only when he
    /// has reacted and his own meeting with the ball is no later than the attackers' (he cuts it out), or they have none.
    /// </summary>
    public bool DefenderGoesForBall
    {
        get
        {
            if (!HasMates) return true;
            if (DefenderReacting) return false;
            var mine = _claims[1]; var theirs = _claims[0];
            if (theirs.Player == null) return true;
            return mine.Player != null && mine.ArriveTick <= theirs.ArriveTick;
        }
    }

    /// <summary>
    /// The pass record's life, once a tick before the claims are taken: it ends when the ball has left the path the pass was
    /// struck on (anyone's touch, a save, a deflection) or is controlled; and the receiver's claim is opened to his whole team
    /// when he is late: past the expected arrival and still more than half a second from the ball, or solved to arrive more than
    /// a second after it.
    /// </summary>
    void UpdatePassRecord()
    {
        if (_pass is not { } r) return;
        if (_controller != null || State is not (RoundState.Live or RoundState.Strike)) { _pass = null; return; }
        if (r.PathTick >= 0 && (!Forecast.Valid || Forecast.StartTick != r.PathTick || !Forecast.Matches(Ball, Tick, Tuning.LockOn.RebuildDriftM))) { _pass = null; return; }
        if (r.Receiver == null || r.Reopened) return;
        int now = Tick + 1;
        var c = _claims[0];
        bool his = c.Player == r.Receiver;
        if (now > r.ExpectedArrivalTick && (!his || c.ArriveTick - now > Ticks(LateSeconds))
            || his && c.ArriveTick > r.ExpectedArrivalTick + Ticks(VeryLateSeconds))
            r.Reopened = true;
    }

    /// <summary>A receiver still this far from the ball past its expected arrival is late; one solved to arrive this long after it is late at once, seconds.</summary>
    const float LateSeconds = 0.5f, VeryLateSeconds = 1f;
}
