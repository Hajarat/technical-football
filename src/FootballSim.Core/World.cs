using System.Numerics;

namespace FootballSim.Core;

/// <summary>
/// The simulation: two teams, one ball, the pitch, a clock and the round state machine. The game layer calls
/// <see cref="Begin"/> once, then <see cref="Step"/> once per tick (1/120 s), and reads the state afterwards.
/// All randomness comes from one <see cref="SimRng"/> seeded per round, so the same seed and intents give the same round.
/// </summary>
public sealed class World
{
    readonly List<SimEvent> _events = new();
    readonly List<ShotResult> _shots = new();
    readonly List<string> _diagnostics = new();
    readonly PlayerSheet _attackerSheet;
    readonly PlayerSheet _keeperSheet;
    readonly float _dt;
    SimRng _rng;
    SimRng _keeperRng;         // the keeper's own stream, so his decisions never shift the attacker's draws
    KeeperBrain _brain = null!;
    ReplayRecorder? _recorder;

    // Shoot input.
    bool _shootWasHeld;
    float _charge;
    bool _latchFinesse, _latchChip, _latchDriven;
    Vector2 _lastStick;
    Vector2 _lastPushedStick;  // the latest left stick outside the dead zone, for the aim lock
    int _lastPushedTick;
    PendingShot _pending;      // a released shot waiting for its contact tick (and the ball in reach)
    float _outcomeLeft;
    float _liveSeconds;
    int _endTick;
    float _endClock;
    ShotResult? _lastShot;

    // Possession.
    PlayerState? _controller;  // who controls the ball, or null when it is loose
    float _awayFor;            // seconds the ball has been beyond the controller's lose distance
    bool _softPending;         // shoot pressed in possession: the soft touch is still to be made
    Foot _strikeFoot;          // foot decided at the press, kept through the strike
    int _shotStartTick = -1;   // tick shoot was pressed for the shot under way (for the fake shot), or -1
    bool _shotCancelled;       // the shot was turned into a fake: the rest of this press of shoot is ignored

    const ulong KeeperSeedSalt = 0x6B65657065723144UL;

    struct PendingShot
    {
        public bool Active;
        public float Charge;
        public Vector2 Stick;
        public ShotType Type;
        public Foot Foot;
        /// <summary>The earliest contact: the wind-up after the press and the lead after the release.</summary>
        public int ContactTick;
        /// <summary>Past this tick with the ball still out of reach, the shot is dropped.</summary>
        public int DeadlineTick;
    }

    /// <summary>Creates a world in the Setup state, with the ball, attacker and keeper placed per the scenario.</summary>
    /// <param name="tuning">Every tunable number.</param>
    /// <param name="scenario">Start spot and rules. A team size above 1 adds players who stand still.</param>
    /// <param name="attacker">Sheet of the attacker (team 0, player 0).</param>
    /// <param name="keeper">Sheet of the keeper (team 1, player 0).</param>
    /// <param name="seed">Seed for the round's random generator.</param>
    public World(Tuning tuning, Scenario scenario, PlayerSheet attacker, PlayerSheet keeper, ulong seed)
    {
        Tuning = tuning;
        Scenario = scenario;
        _attackerSheet = attacker;
        _keeperSheet = keeper;
        _dt = tuning.Dt;
        Pitch = new Pitch(scenario.Pitch, tuning.Frame, tuning.Arena);
        Ball = new Ball(tuning.Ball);
        _rng = new SimRng(seed);
        _keeperRng = new SimRng(seed ^ KeeperSeedSalt);
        Seed = seed;
        var t0 = new List<PlayerState>();
        var t1 = new List<PlayerState>();
        for (int i = 0; i < Math.Max(1, scenario.TeamSize); i++)
        {
            t0.Add(new PlayerState(0, i, PlayerRole.Outfield, attacker));
            t1.Add(new PlayerState(1, i, i == 0 ? PlayerRole.Keeper : PlayerRole.Outfield, i == 0 ? keeper : attacker));
        }
        Teams = new IReadOnlyList<PlayerState>[] { t0, t1 };
        Reset(seed);
    }

    // ---- read-only state ----

    /// <summary>The tuning this world runs under.</summary>
    public Tuning Tuning { get; }
    /// <summary>The scenario this world was built from.</summary>
    public Scenario Scenario { get; }
    /// <summary>The arena and goal.</summary>
    public Pitch Pitch { get; }
    /// <summary>The ball: Position, Velocity and Spin are System.Numerics vectors.</summary>
    public Ball Ball { get; }
    /// <summary>Two teams, ordered team 0 (attackers) then team 1 (keeper first). Intents to <see cref="Step"/> follow the same order.</summary>
    public IReadOnlyList<IReadOnlyList<PlayerState>> Teams { get; }
    /// <summary>The round state.</summary>
    public RoundState State { get; private set; }
    /// <summary>How the round ended; None until it has.</summary>
    public Outcome Outcome { get; private set; }
    /// <summary>Seconds spent in Live this round.</summary>
    public float Clock { get; private set; }
    /// <summary>0..1 while shoot is held; 0 otherwise.</summary>
    public float ShotCharge { get; private set; }
    /// <summary>
    /// The ideal charge (0..1) for a shot from the ball's current distance to the goal centre, for the HUD's marker. Charging
    /// past it lifts the shot, short of it keeps it low (a chip goes deeper or shorter instead).
    /// </summary>
    public float IdealCharge { get; private set; }
    /// <summary>Ticks stepped since Begin.</summary>
    public int Tick { get; private set; }
    /// <summary>Simulation seconds since Begin (all states except Setup and Report).</summary>
    public float SimTime { get; private set; }
    /// <summary>Seed of the current round.</summary>
    public ulong Seed { get; private set; }
    /// <summary>The round's report, available once the state is Report.</summary>
    public RoundReport? Report { get; private set; }
    /// <summary>Events raised by the last <see cref="Step"/>.</summary>
    public IReadOnlyList<SimEvent> Events => _events;
    /// <summary>Shots struck so far this round.</summary>
    public IReadOnlyList<ShotResult> Shots => _shots;
    /// <summary>Problems logged this round (for example a value that became not-a-number), each with the seed.</summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;
    /// <summary>The attacker the intents control (team 0, player 0).</summary>
    public PlayerState Attacker => Teams[0][0];
    /// <summary>The keeper (team 1, player 0).</summary>
    public PlayerState Keeper => Teams[1][0];
    /// <summary>The player who controls the ball (dribbling it, or the keeper holding it), or null when the ball is loose.</summary>
    public PlayerState? BallController => _controller;
    /// <summary>The keeper's numbers for the latest shot.</summary>
    public KeeperReport KeeperReport => _brain.Report;
    /// <summary>Why the keeper did what he did on the latest tick (a diagnostic for probes; changes nothing).</summary>
    public KeeperThought KeeperThought => _brain.Thought;
    /// <summary>Number of intents <see cref="Step"/> expects: both teams' players, team 0 first.</summary>
    public int PlayerCount => Teams[0].Count + Teams[1].Count;
    /// <summary>True while the round is being simulated (Live, Strike, Flight or Outcome).</summary>
    public bool IsSimulating => State is RoundState.Live or RoundState.Strike or RoundState.Flight or RoundState.Outcome;
    /// <summary>When set, every simulated tick's intents are recorded for a replay.</summary>
    public ReplayRecorder? Recorder { get => _recorder; set => _recorder = value; }

    /// <summary>
    /// While shoot is held in Live: what a strike made now would aim at and its spread, from the latest stick, charge and
    /// shot type. Null otherwise. Read-only; it draws no random numbers.
    /// </summary>
    public ShotPreview? PreviewShot()
    {
        if (State != RoundState.Live || !_shootWasHeld || ShotCharge <= 0f) return null;
        var ctx = new ShotContext
        {
            Shooter = Attacker, Keeper = Keeper, BallPosition = Ball.Position, Stick = AimStick(_lastStick), Charge = _charge,
            Type = ShotModel.TypeFor(_latchFinesse, _latchChip, _latchDriven), Foot = _strikeFoot,
        };
        return ShotModel.Preview(ctx, Pitch, Tuning);
    }

    // ---- control ----

    /// <summary>Setup to Live. Does nothing in any other state.</summary>
    public void Begin()
    {
        if (State == RoundState.Setup) State = RoundState.Live;
    }

    /// <summary>Report to Setup (or any state to Setup): the next round with a new seed, or a retry with the same seed.</summary>
    public void Reset(ulong seed)
    {
        Seed = seed;
        _rng = new SimRng(seed);
        _keeperRng = new SimRng(seed ^ KeeperSeedSalt);
        State = RoundState.Setup;
        Outcome = Outcome.None;
        Clock = 0f; ShotCharge = 0f; Tick = 0; SimTime = 0f;
        Report = null;
        _events.Clear(); _shots.Clear(); _diagnostics.Clear();
        _shootWasHeld = false; _charge = 0f; _latchFinesse = _latchChip = _latchDriven = false; _lastStick = default;
        _lastPushedStick = default; _lastPushedTick = int.MinValue / 2;
        _pending = default; _outcomeLeft = 0f;
        _liveSeconds = 0f; _endTick = 0; _endClock = 0f; _lastShot = null;
        _controller = null; _awayFor = 0f; _softPending = false; _strikeFoot = Foot.Right;
        _shotStartTick = -1; _shotCancelled = false;

        float r = Tuning.Ball.Radius;
        Ball.Position = new Vector3(Scenario.Ball.X, r, Scenario.Ball.Z);
        Ball.Velocity = Vector3.Zero;
        Ball.Spin = Vector3.Zero;

        var team0 = Teams[0];
        var team1 = Teams[1];
        for (int i = 0; i < team0.Count; i++)
        {
            var p = team0[i];
            p.Position = i == 0
                ? new Vector3(Scenario.Attacker.X, 0f, Scenario.Attacker.Z)
                : ClampToPitch(new Vector3(Scenario.Attacker.X - 4f * i, 0f, Scenario.Attacker.Z + (i % 2 == 0 ? 6f : -6f) * ((i + 1) / 2)));
            p.Facing = i == 0 ? Scenario.Attacker.FacingDeg * MathF.PI / 180f : 0f;
            ResetPlayer(p);
        }
        for (int i = 0; i < team1.Count; i++)
        {
            var p = team1[i];
            p.Position = i == 0
                ? KeeperStart()
                : ClampToPitch(new Vector3(Pitch.GoalLineX - 12f - 3f * i, 0f, (i % 2 == 0 ? 8f : -8f) * ((i + 1) / 2)));
            p.Facing = MathF.PI;
            ResetPlayer(p);
        }
        _brain = new KeeperBrain(Keeper, Pitch, Tuning, Scenario.KeeperHoldLine, Attacker.Sheet)
        { HoldLineThroughDribble = Scenario.KeeperHoldLineThroughDribble };
        IdealCharge = ShotModel.IdealCharge(ShotModel.GoalDistance(Ball.Position, Pitch), Tuning);
    }

    /// <summary>
    /// The keeper's start: on his line (where the cover line from the ball meets it) when the scenario says so, at the scenario's
    /// distance off the line on the ball-to-goal line, or else on his arc spot.
    /// </summary>
    Vector3 KeeperStart()
    {
        var keeperSheet = Teams[1][0].Sheet;
        if (Scenario.KeeperOffLine is not float off)
            return ClampToPitch(Scenario.KeeperOnLine ? Core.Keeper.LineSpot(Ball.Position, Pitch, Tuning) : Core.Keeper.ArcTarget(Ball.Position, Pitch, Tuning, keeperSheet));
        float x = Pitch.GoalLineX - off;
        float dx = Pitch.GoalLineX - Ball.Position.X;
        float z = dx > 1e-3f ? Ball.Position.Z * off / dx : 0f;
        return ClampToPitch(new Vector3(x, 0f, Math.Clamp(z, -Pitch.GoalHalfWidth, Pitch.GoalHalfWidth)));
    }

    Vector3 ClampToPitch(Vector3 v)
    {
        var p = new Vector3(v.X, 0f, v.Z);
        var vel = Vector3.Zero;
        Pitch.ClampPlayer(ref p, ref vel);
        return p;
    }

    static void ResetPlayer(PlayerState p)
    {
        p.Velocity = Vector3.Zero;
        p.HandPosition = p.Position;
        p.KeeperAction = KeeperAction.Set;
        p.KeeperDive = KeeperDive.None;
        p.KeeperActionTime = 0f;
        p.LastTouchTime = -100f;
        p.TurnRefHeading = p.Facing;
        p.TurnRefTime = 0f;
        p.InPossession = false;
        p.NextTouchEta = float.NaN;
        p.NextTouchFoot = p.Sheet.PreferredFoot;
        p.NextTouchPoint = Vector3.Zero;
        p.StrikeEta = float.NaN;
        p.StrikeFoot = p.Sheet.PreferredFoot;
        p.LastTouchFoot = p.Sheet.PreferredFoot;
        p.CurrentTick = 0;
        p.GaitPhase = 0f;
        p.StepFrequency = 0f;
        p.GaitLockTicks = 0;
        p.GaitLockTarget = 0f;
        p.SwingMiddleCrossed = null;
        p.SteerVelocity = Vector3.Zero;
        p.RunSpeed = 0f;
        p.TouchAction = null;
        p.PrevTouchAction = null;
        p.ShotAction = null;
        p.StunUntil = -100f;
        p.ActionContactEta = float.NaN;
        p.Chasing = false;
        p.LastTouchKind = ActionKind.First;
        p.KnockOnRequested = false;
        p.StepsSinceTouch = 0;
        p.PushSteps = 1;
        p.ClearForecast();
        p.SoftWaitAction = null;
        p.TouchBoostFrom = float.NaN;
        p.TouchBoostUntil = -100f;
        p.TouchBoostFactor = 1f;
        p.KnockOnFrom = float.NaN;
        p.KnockOnUntil = -100f;
        p.KnockOnDirection = Vector2.Zero;
        p.MoveTime = 0f;
        GestureReader.Reset(p);
        SkillMoves.Reset(p);
    }

    // ---- stepping ----

    TickIntent IntentFor(ReadOnlySpan<TickIntent> intents, int team, int index)
    {
        int flat = team == 0 ? index : Teams[0].Count + index;
        return flat < intents.Length ? intents[flat] : default;
    }

    /// <summary>
    /// Advances exactly one tick of 1/120 s. Takes one intent per player, team 0 then team 1; missing intents are treated as
    /// no input and extra ones are ignored. The keeper's intent is ignored for now: its brain is internal. Does nothing in
    /// Setup or Report. Never throws for a world with more players per side; extra players just stand still.
    /// </summary>
    public void Step(ReadOnlySpan<TickIntent> intents)
    {
        _events.Clear();
        if (State is RoundState.Setup or RoundState.Report) return;

        _recorder?.Record(intents);
        Tick++;
        SimTime += _dt;
        TickIntent atk = IntentFor(intents, 0, 0);
        StepSimulation(atk);
        PublishContract();
    }

    void StepSimulation(in TickIntent atk)
    {
        _brain.BeginTick(_dt);
        if (State == RoundState.Outcome)
        {
            StepOutcome();
            return;
        }

        // 1. Attacker: movement, shoot input, touches.
        StepAttacker(atk);

        // 2. The released shot: contact on its tick if the ball is in reach, else as soon as it is, until the deadline.
        if (State == RoundState.Strike && _pending.Active && Tick >= _pending.ContactTick)
        {
            if (ShotModel.CanStrike(Attacker, Ball, Tuning)) ExecuteStrike();
            else if (Tick >= _pending.DeadlineTick) DropShot();
        }

        // 3. Keeper.
        _brain.Update(_dt, Ball, _keeperRng, KeeperSituation());

        // 4. Ball, with the frame, the arena boards and the keeper's hands as obstacles.
        Vector3 prev = Ball.Position;
        var res = Ball.Step(_dt, Pitch, _brain.HandsSphere, _brain.BodyCapsule, _brain.HandsCapsule);
        float speed = Ball.Velocity.Length();
        if (res.Bounced) _events.Add(new SimEvent(EventKind.Bounce, res.BouncePoint, res.BounceSpeed));
        if (res.FrameHit)
        {
            _events.Add(new SimEvent(EventKind.FrameHit, res.FramePoint, speed));
            if (State == RoundState.Flight) State = RoundState.Live;
        }
        if (res.BoardHit)
        {
            _events.Add(new SimEvent(EventKind.BoardHit, res.BoardPoint, res.BoardSpeed));
            // A shot that reaches the goal-end wall beside or above the goal records where it got there, like a shot that
            // crosses the goal line.
            if (_lastShot != null && float.IsNaN(_lastShot.Landed.X) && res.BoardPoint.X >= Pitch.GoalLineX - Ball.Radius - 1e-3f)
                _lastShot.Landed = new Vector2(res.BoardPoint.Z, res.BoardPoint.Y);
            // Off the boards the shot is over and the ball is in play again.
            if (State == RoundState.Flight) State = RoundState.Live;
        }
        // A shot that dies before it reaches anything (it comes to rest) is over too, so the clock runs again.
        if (State == RoundState.Flight && Ball.OnGround && Ball.Velocity == Vector3.Zero) State = RoundState.Live;
        TrackLanding(prev, Ball.Position);
        _brain.ObservePlane(prev, Ball.Position);
        // His hands meeting a ball outside his penalty area is no touch: he pulls them away and the ball carries on.
        if (res.SphereHit && !_brain.RefuseHands(res.SpherePoint, res.BodyHit))
        {
            float v;
            bool held = _brain.Contact(Ball, res.SpherePoint, _keeperRng, out v, res.BodyHit);
            _events.Add(new SimEvent(EventKind.Save, res.SpherePoint, v, held));
            // The keeper touching the ball ends anyone else's control; holding it is the keeper's possession.
            SetController(held ? Keeper : null);
            if (held) { EndRound(Outcome.Held); return; }
            if (State == RoundState.Flight) State = RoundState.Live;
        }

        // Possession is lost when the ball stays beyond the lose distance for long enough.
        if (_controller != null && _controller.Role != PlayerRole.Keeper)
        {
            if (TouchModel.HorizontalDistance(_controller.Position, Ball.Position) > Possession.LoseDistance(_controller, Tuning))
            {
                _awayFor += _dt;
                if (_awayFor > Tuning.Possession.LoseSeconds) SetController(null);
            }
            else _awayFor = 0f;
        }

        // 5. Clock.
        if (State == RoundState.Live) { Clock += _dt; }
        _liveSeconds += _dt;

        // 6. Referee.
        if (!IsFinite(Ball.Position) || !IsFinite(Ball.Velocity) || !IsFinite(Attacker.Position) || !IsFinite(Keeper.Position))
        {
            _diagnostics.Add($"seed {Seed} tick {Tick}: position or speed became not-a-number");
            EndRound(Outcome.Out);
            return;
        }
        // The arena keeps the ball in play, so a round ends only on a goal, the keeper holding the ball, or the clock.
        if (Pitch.IsGoal(Ball.Position, Ball.Radius))
        {
            _events.Add(new SimEvent(EventKind.Goal, Ball.Position, speed));
            Ball.Velocity *= Tuning.Frame.NetSpeedKept;
            EndRound(Outcome.Goal);
            return;
        }
        if (State == RoundState.Live)
        {
            float limit = Scenario.ClockLimit ?? float.MaxValue;
            if (Clock >= limit || _liveSeconds >= Tuning.Round.MaxSeconds) EndRound(Outcome.TimeUp);
        }
    }

    /// <summary>
    /// What the keeper reads of the attacker this tick: when the attacker can next play the ball (his committed touch, else the
    /// swing his touch controller steers for, a skill move's sole contact or a shot's contact; with none expected, his run to the
    /// ball, after any stun),
    /// whether a shot is winding up, and whether a struck shot is in flight.
    /// </summary>
    KeeperSituation KeeperSituation()
    {
        var a = Attacker;
        float best = float.PositiveInfinity;
        int tick = -1;
        void Take(int t)
        {
            float e = MathF.Max(0f, (t - Tick) * _dt);
            if (e < best) { best = e; tick = t; }
        }
        if (_controller == a)
        {
            if (a.TouchAction is { Pending: true } ta) Take(ta.ContactTick);
            else if (a.ForecastTick >= Tick) Take(a.ForecastTick);
            if (a.SkillAction is { Made: false } sk && sk.ContactTick > Tick && sk.Phase != ActionPhase.Done) Take(sk.ContactTick);
            if (a.ShotAction is { Made: false } sh) Take(sh.ContactTick);
        }
        if (float.IsPositiveInfinity(best))
        {
            float d = MathF.Max(0f, TouchModel.HorizontalDistance(a.Position, Ball.Position) - Tuning.Keeper.AttackerReach);
            best = MathF.Max(0f, a.StunUntil - SimTime) + d / MathF.Max(1f, Movement.TopSpeed(a.Sheet, Tuning)) + Tuning.Keeper.AttackerReactTime;
            tick = -2;
        }
        return new KeeperSituation
        {
            AttackerNextTouch = best,
            AttackerTouchTick = tick,
            AttackerHasBall = _controller == a,
            Windup = a.ShotAction is { Made: false },
            StrikeIn = a.ShotAction is { Made: false } shot ? MathF.Max(0f, (shot.ContactTick - Tick) * _dt) : float.PositiveInfinity,
            ShotInFlight = State == RoundState.Flight,
            AttackerPosition = a.Position,
            AttackerVelocity = a.Velocity,
        };
    }

    static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    int Ticks(float seconds) => Math.Max(0, (int)MathF.Round(seconds * Tuning.TickRate));

    void StepAttacker(in TickIntent it)
    {
        var p = Attacker;
        var tuning = Tuning;
        _lastStick = it.Move;
        if (it.Move.Length() >= tuning.Movement.StickDeadZone) { _lastPushedStick = it.Move; _lastPushedTick = Tick; }
        bool inPossession = _controller == p;

        // Skill gestures are read every tick so their timing runs on through every state. A fake shot aborts the shot under
        // way and asks for its move; a knock-on flick asks for a knock-on like the knock-on button.
        var gesture = GestureReader.Read(p, it, Tick, tuning);
        bool shotUnderWay = State == RoundState.Strike && _pending.Active || State == RoundState.Live && _shootWasHeld;
        var fake = GestureReader.ReadFakeShot(p, it, Tick, _shotStartTick, shotUnderWay, tuning);
        if (fake.Kind != Gesture.None) { AbortShot(it.ShootHeld); gesture = fake; }
        if (!it.ShootHeld) _shotCancelled = false;
        bool shootHeld = it.ShootHeld && !_shotCancelled;
        bool knockGesture = gesture.Kind == Gesture.KnockOn;
        // A gesture with no move yet (elastico, heel-to-heel, spin) is recognised and ignored.
        if (gesture.Kind is not (Gesture.None or Gesture.KnockOn) && !GestureReader.NotBuilt(gesture.Kind)) SkillMoves.Request(p, gesture, Tick);
        if (State == RoundState.Strike && _pending.Active && shootHeld && !_shootWasHeld) PressShootAgain(p, it);
        if (State == RoundState.Live && !shootHeld
            && SkillMoves.TryStart(p, Ball, inPossession, Tick, SimTime, tuning) is SimEvent started)
            _events.Add(started);

        // A released shot waiting for its contact still steers the ball into the striking foot's zone (the soft touch).
        bool windup = State == RoundState.Live && shootHeld || State == RoundState.Strike && _pending.Active;
        var assist = TouchPlanner.BeginTick(p, it, Ball, inPossession, State == RoundState.Live || windup, Tick, SimTime, tuning, Pitch, out var legs);
        if (windup && inPossession && p.SkillAction == null && p.TouchAction is not { Pending: true }) legs = TouchPlanner.ShotWindupLegs(p, Ball, tuning);
        Movement.Step(p, legs, _dt, SimTime, assist, tuning, Pitch);

        // Other players on both teams stand still (no behaviour yet).
        if (State == RoundState.Live)
        {
            if ((it.KnockOn || knockGesture) && inPossession) TouchPlanner.RequestKnockOn(p);

            if (shootHeld)
            {
                if (!_shootWasHeld) PressShoot(p, inPossession);
                _charge = MathF.Min(1f, _charge + _dt / tuning.Shot.ChargeSeconds);
                _latchFinesse |= it.Finesse;
                _latchChip |= it.Chip;
                _latchDriven |= it.Driven;
                ShotCharge = _charge;
                if (p.ShotAction is { } shot)
                {
                    var type = ShotModel.TypeFor(_latchFinesse, _latchChip, _latchDriven);
                    shot.Kind = ShotModel.KindFor(type);
                    shot.Surface = ShotModel.SurfaceFor(type);
                    // The earliest contact if shoot were released on the next tick.
                    shot.ContactTick = Math.Max(_shotStartTick + Ticks(tuning.Shot.WindupSeconds), Tick + 1 + Ticks(tuning.Shot.ReleaseLeadSeconds));
                }
            }
            else
            {
                ShotCharge = 0f;
                if (_shootWasHeld) ReleaseShoot(p, it);
            }
        }
        else ShotCharge = 0f;
        _shootWasHeld = shootHeld;

        // While the shot winds up only the soft touch is made; during the stun after a strike none at all.
        if (!windup && State != RoundState.Live) return;
        var mode = windup
            ? (_softPending && _controller == p ? TouchStepMode.SoftOnly : TouchStepMode.None)
            : TouchStepMode.Normal;
        if (TouchPlanner.Stunned(p, SimTime)) mode = TouchStepMode.None;
        TryTouch(p, it, mode);
    }

    /// <summary>
    /// The left stick that aims a shot released now: the stick itself, or, at rest, its last pushed direction if that push was
    /// within the aim lock. At rest and older than that it stays at rest (the shot goes where the shooter faces).
    /// </summary>
    Vector2 AimStick(Vector2 stick)
    {
        if (stick.Length() >= Tuning.Movement.StickDeadZone) return stick;
        return Tick - _lastPushedTick <= Ticks(Tuning.Shot.AimLockSeconds) ? _lastPushedStick : stick;
    }

    /// <summary>
    /// Shoot pressed in Live: the shot action starts winding up. In possession the next touch is a soft one, so the ball stays
    /// strikable through the wind-up (a touch already winding up is made first). A skill move under way ends here.
    /// </summary>
    void PressShoot(PlayerState p, bool inPossession)
    {
        var tuning = Tuning;
        _shotStartTick = Tick;
        _charge = 0f;
        _latchFinesse = _latchChip = _latchDriven = false;
        SkillMoves.Abort(p);
        _softPending = inPossession;
        if (_softPending) TouchPlanner.OnShootPressed(p, Tick, tuning);
        _strikeFoot = ShotModel.StrikingFoot(p, Ball.Position, tuning);
        p.StrikeFoot = _strikeFoot;
        p.ShotAction = new BallAction
        {
            Kind = ActionKind.Power,
            Phase = ActionPhase.Windup,
            Foot = _strikeFoot,
            Surface = FootSurface.Laces,
            PlanTick = Tick,
            ContactTick = Tick + Ticks(tuning.Shot.WindupSeconds),
            ContactPoint = new Vector3(Ball.Position.X, Ball.Radius, Ball.Position.Z),
        };
    }

    /// <summary>
    /// Shoot released: the charge, type and aim are fixed, and the contact is set no sooner than the wind-up after the press
    /// and the lead after the release. The round goes to Strike until then.
    /// </summary>
    void ReleaseShoot(PlayerState p, in TickIntent it)
    {
        var k = Tuning.Shot;
        var type = ShotModel.TypeFor(_latchFinesse, _latchChip, _latchDriven);
        int contact = Math.Max(_shotStartTick + Ticks(k.WindupSeconds), Tick + Ticks(k.ReleaseLeadSeconds));
        _pending = new PendingShot
        {
            Active = true,
            Charge = _charge,
            Stick = AimStick(it.Move),
            Type = type,
            Foot = _strikeFoot,
            ContactTick = contact,
            DeadlineTick = contact + Ticks(Tuning.Round.ShootBuffer),
        };
        _charge = 0f;
        _latchFinesse = _latchChip = _latchDriven = false;
        if (p.ShotAction is { } shot)
        {
            shot.Kind = ShotModel.KindFor(type);
            shot.Surface = ShotModel.SurfaceFor(type);
            shot.ContactTick = contact;
            shot.Released = true;
        }
        SkillMoves.Abort(p);
        State = RoundState.Strike;
        ShotCharge = 0f;
    }

    /// <summary>
    /// Shoot pressed again after its release, any time before the strike: the shot becomes a low shot (power to driven,
    /// finesse to low finesse; a chip stays a chip, as there is no low chip yet). The charge stays the first press's, so a
    /// low shot can have any power. Shoulder buttons held on the second press count as if held through the first.
    /// </summary>
    void PressShootAgain(PlayerState p, in TickIntent it)
    {
        var t = _pending.Type;
        bool finesse = t is ShotType.Finesse or ShotType.LowFinesse || it.Finesse;
        bool chip = t == ShotType.Chip || it.Chip;
        _pending.Type = ShotModel.TypeFor(finesse, chip, driven: true);
        if (p.ShotAction is { } shot)
        {
            shot.Kind = ShotModel.KindFor(_pending.Type);
            shot.Surface = ShotModel.SurfaceFor(_pending.Type);
        }
    }

    /// <summary>Lets a player touch the ball and raises the Touch event; a first contact with a loose ball gains possession.</summary>
    void TryTouch(PlayerState p, in TickIntent it, TouchStepMode mode)
    {
        bool inPossession = _controller == p;
        if (TouchPlanner.Step(p, it, Ball, inPossession, mode, Tick, SimTime, Tuning, Pitch, _rng) is not TouchResult touch) return;
        if (touch.Kind == ActionKind.Soft) _softPending = false;
        _events.Add(new SimEvent(EventKind.Touch, touch.ContactPoint, touch.BallVelocity.Length(), Foot: touch.Foot, Team: p.Team, PlayerIndex: p.Index));
        if (!inPossession) SetController(p);
        _awayFor = 0f;
    }

    /// <summary>Changes who controls the ball, raising PossessionLost for the old controller and PossessionGained for the new.</summary>
    void SetController(PlayerState? p)
    {
        if (_controller == p) return;
        float speed = Ball.Velocity.Length();
        if (_controller != null)
        {
            _controller.InPossession = false;
            TouchPlanner.OnPossessionLost(_controller);
            _events.Add(new SimEvent(EventKind.PossessionLost, Ball.Position, speed, Team: _controller.Team, PlayerIndex: _controller.Index));
        }
        _controller = p;
        _awayFor = 0f;
        if (p != null)
        {
            p.InPossession = true;
            _events.Add(new SimEvent(EventKind.PossessionGained, Ball.Position, speed, Team: p.Team, PlayerIndex: p.Index));
        }
    }

    /// <summary>Fills the per-player contract read by the view: action phases, next touch, strike timing and feet.</summary>
    void PublishContract()
    {
        foreach (var team in Teams)
            foreach (var p in team)
            {
                p.NextTouchEta = float.NaN;
                p.StrikeEta = float.NaN;
                p.CurrentTick = Tick;
                TouchPlanner.UpdatePhases(p, Tick, Tuning);
            }
        var a = Attacker;
        if (a.ShotAction is { } shot)
        {
            shot.Phase = shot.PhaseAt(Tick, 0, 0);
            if (shot.Phase == ActionPhase.Done) a.ShotAction = null;
            else if (!shot.Made)
            {
                // The latest prediction of where the ball is met: the ball rolled on to the contact tick.
                float eta = MathF.Max(0f, (shot.ContactTick - Tick) * _dt);
                var at = Ball.Position + new Vector3(Ball.Velocity.X, 0f, Ball.Velocity.Z) * eta;
                shot.ContactPoint = new Vector3(at.X, Ball.Radius, at.Z);
            }
        }
        IdealCharge = ShotModel.IdealCharge(ShotModel.GoalDistance(Ball.Position, Pitch), Tuning);
        if (State == RoundState.Strike && _pending.Active) a.StrikeEta = MathF.Max(0f, (_pending.ContactTick - Tick) * _dt);
        if (State == RoundState.Live && _controller == a)
        {
            var f = Possession.PredictNextTouch(a, Tick, true, Tuning);
            a.NextTouchEta = f.Eta;
            if (!float.IsNaN(f.Eta)) { a.NextTouchFoot = f.Foot; a.NextTouchPoint = f.Point; }
        }
    }

    /// <summary>
    /// A fake shot: the shot under way (charging, waiting for the ball, or released and waiting out the strike delay) is
    /// abandoned without a strike; with shoot still held, the rest of that press is ignored.
    /// </summary>
    void AbortShot(bool shootHeld)
    {
        _shotCancelled = shootHeld;
        _shootWasHeld = false;
        _shotStartTick = -1;
        _charge = 0f; ShotCharge = 0f;
        _latchFinesse = _latchChip = _latchDriven = false;
        _softPending = false;
        TouchPlanner.OnShotAborted(Attacker);
        EndShotAction();
        if (State == RoundState.Strike) { _pending = default; State = RoundState.Live; }
    }

    /// <summary>The released shot never found the ball in reach: no strike, no report, and play goes on.</summary>
    void DropShot()
    {
        _pending = default;
        _softPending = false;
        TouchPlanner.OnShotAborted(Attacker);
        EndShotAction();
        State = RoundState.Live;
    }

    /// <summary>A shot action not struck (faked, dropped, or the round ended) is done at once.</summary>
    void EndShotAction()
    {
        if (Attacker.ShotAction is { Made: false } s) { s.Phase = ActionPhase.Done; Attacker.ShotAction = null; }
    }

    void ExecuteStrike()
    {
        var ctx = new ShotContext
        {
            Shooter = Attacker,
            Keeper = Keeper,
            BallPosition = Ball.Position,
            Stick = _pending.Stick,
            Charge = _pending.Charge,
            Type = _pending.Type,
            Foot = _pending.Foot,
        };
        var result = ShotModel.Compute(ctx, Pitch, Tuning, _rng, Tick);
        var a = Attacker;
        if (a.ShotAction is { } shot)
        {
            shot.Kind = ShotModel.KindFor(result.Type);
            shot.Surface = ShotModel.SurfaceFor(result.Type);
            shot.Foot = result.Foot;
            shot.Made = true;
            shot.ContactTick = Tick;
            shot.ContactPoint = Ball.Position;
            shot.ContactMiss = 0f;
            shot.BallOutVelocity = result.Launch;
            shot.BallOutSpin = result.Spin;
            shot.EndTick = Tick + Ticks(Tuning.Shot.RecoverySeconds);
            shot.Phase = ActionPhase.Contact;
        }
        // The strike plants the shooter: slowed and without touches for a moment, longer after a harder shot.
        a.StunUntil = SimTime + Tuning.Shot.StunBaseSeconds + Tuning.Shot.StunChargeSeconds * result.Charge;
        _softPending = false;
        Ball.Velocity = result.Launch;
        Ball.Spin = result.Spin;
        Attacker.LastTouchTime = SimTime;
        Attacker.LastTouchFoot = result.Foot;
        Attacker.StrikeFoot = result.Foot;
        _shots.Add(result);
        _lastShot = result;
        _pending = default;
        _events.Add(new SimEvent(EventKind.Strike, Ball.Position, result.Speed, Foot: result.Foot, Team: Attacker.Team, PlayerIndex: Attacker.Index));
        SetController(null); // a struck ball is nobody's
        _brain.OnStrike(_keeperRng, Ball);
        State = RoundState.Flight;
    }

    void TrackLanding(Vector3 prev, Vector3 now)
    {
        if (_lastShot == null || !float.IsNaN(_lastShot.Landed.X)) return;
        float gx = Pitch.GoalLineX;
        if (prev.X < gx && now.X >= gx)
        {
            float f = (gx - prev.X) / (now.X - prev.X);
            _lastShot.Landed = new Vector2(prev.Z + (now.Z - prev.Z) * f, prev.Y + (now.Y - prev.Y) * f);
        }
    }

    void EndRound(Outcome outcome)
    {
        Outcome = outcome;
        State = RoundState.Outcome;
        _outcomeLeft = Tuning.Round.OutcomeSeconds;
        _endTick = Tick;
        _endClock = Clock;
        ShotCharge = 0f;
        _pending = default;
        _softPending = false;
        EndShotAction();
        if (outcome != Outcome.Held) SetController(null); // the ball is dead; a held ball stays the keeper's
    }

    void StepOutcome()
    {
        var idle = default(TickIntent);
        Movement.Step(Attacker, idle, _dt, SimTime, false, Tuning, Pitch);
        _brain.Update(_dt, Ball, _keeperRng, new KeeperSituation { AttackerNextTouch = float.PositiveInfinity, AttackerTouchTick = -1, StrikeIn = float.PositiveInfinity, BallDead = true });
        if (Outcome == Outcome.Held) Ball.Position = Keeper.HandPosition;
        else if (Outcome == Outcome.Goal)
        {
            Ball.Step(_dt, null, null);
            ConstrainToNet();
        }
        else Ball.Step(_dt, Pitch, null); // the boards still hold a dead ball
        _outcomeLeft -= _dt;
        if (_outcomeLeft <= 0f) FinishRound();
    }

    /// <summary>The net stops the ball: behind the goal line it cannot pass the net's depth, the side nets or the roof.</summary>
    void ConstrainToNet()
    {
        var p = Ball.Position;
        var v = Ball.Velocity;
        float r = Ball.Radius;
        float maxX = Pitch.GoalLineX + Pitch.NetDepth - r;
        float maxZ = Pitch.GoalHalfWidth - r;
        float maxY = Pitch.BarHeight - r;
        if (p.X > maxX) { p.X = maxX; v.X = 0f; }
        if (MathF.Abs(p.Z) > maxZ) { p.Z = MathF.Sign(p.Z) * maxZ; v.Z = 0f; }
        if (p.Y > maxY) { p.Y = maxY; v.Y = MathF.Min(v.Y, 0f); }
        Ball.Position = p;
        Ball.Velocity = v;
    }

    void FinishRound()
    {
        Report = new RoundReport
        {
            Outcome = Outcome,
            Seed = Seed,
            Ticks = _endTick,
            Clock = _endClock,
            Shots = _shots.ToArray(),
            Keeper = _brain.Report,
        };
        State = RoundState.Report;
    }
}
