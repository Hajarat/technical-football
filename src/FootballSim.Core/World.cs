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
    KeeperBrain _brain = null!;
    ReplayRecorder? _recorder;

    // Shoot input.
    bool _shootWasHeld;
    float _charge;
    bool _latchFinesse, _latchChip;
    Vector2 _lastStick;
    PendingShot _pending;      // a release waiting out the strike delay
    PendingShot _buffered;     // a release waiting for the ball to come into reach
    float _bufferLeft;
    float _strikeLeft;
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
    TickIntent _lastIntent;    // the attacker's latest intent, for the next-touch prediction

    struct PendingShot
    {
        public bool Active;
        public float Charge;
        public Vector2 Stick;
        public ShotType Type;
        public Foot Foot;
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
        Pitch = new Pitch(scenario.Pitch, tuning.Frame);
        Ball = new Ball(tuning.Ball);
        _rng = new SimRng(seed);
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
    /// <summary>The half pitch and goal.</summary>
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
            Shooter = Attacker, Keeper = Keeper, BallPosition = Ball.Position, Stick = _lastStick, Charge = _charge,
            Type = _latchChip ? ShotType.Chip : _latchFinesse ? ShotType.Finesse : ShotType.Normal, Foot = _strikeFoot,
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
        State = RoundState.Setup;
        Outcome = Outcome.None;
        Clock = 0f; ShotCharge = 0f; Tick = 0; SimTime = 0f;
        Report = null;
        _events.Clear(); _shots.Clear(); _diagnostics.Clear();
        _shootWasHeld = false; _charge = 0f; _latchFinesse = _latchChip = false; _lastStick = default;
        _pending = default; _buffered = default; _bufferLeft = 0f; _strikeLeft = 0f; _outcomeLeft = 0f;
        _liveSeconds = 0f; _endTick = 0; _endClock = 0f; _lastShot = null;
        _controller = null; _awayFor = 0f; _softPending = false; _strikeFoot = Foot.Right; _lastIntent = default;

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
                ? Core.Keeper.SetSpot(Ball.Position, Pitch, Tuning, Scenario.KeeperOnLine)
                : ClampToPitch(new Vector3(Pitch.GoalLineX - 12f - 3f * i, 0f, (i % 2 == 0 ? 8f : -8f) * ((i + 1) / 2)));
            p.Facing = MathF.PI;
            ResetPlayer(p);
        }
        _brain = new KeeperBrain(Keeper, Pitch, Tuning, Scenario.KeeperOnLine);
    }

    Vector3 ClampToPitch(Vector3 v) =>
        new(Math.Clamp(v.X, Pitch.MinX, Pitch.GoalLineX), 0f, Math.Clamp(v.Z, -Pitch.HalfWidth, Pitch.HalfWidth));

    static void ResetPlayer(PlayerState p)
    {
        p.Velocity = Vector3.Zero;
        p.HandPosition = p.Position;
        p.KeeperAction = KeeperAction.Set;
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
        _lastIntent = atk;
        StepSimulation(atk);
        PublishContract();
    }

    void StepSimulation(in TickIntent atk)
    {
        if (State == RoundState.Outcome)
        {
            StepOutcome();
            return;
        }

        // 1. Attacker: movement, shoot input, touches.
        StepAttacker(atk);

        // 2. Strike timer.
        if (State == RoundState.Strike)
        {
            _strikeLeft -= _dt;
            if (_strikeLeft <= 0f) ExecuteStrike();
        }

        // 3. Keeper.
        _brain.Update(_dt, Ball, _rng);

        // 4. Ball, with the frame and the keeper's hands as obstacles.
        Vector3 prev = Ball.Position;
        var res = Ball.Step(_dt, Pitch, _brain.HandsSphere);
        float speed = Ball.Velocity.Length();
        if (res.Bounced) _events.Add(new SimEvent(EventKind.Bounce, res.BouncePoint, res.BounceSpeed));
        if (res.FrameHit)
        {
            _events.Add(new SimEvent(EventKind.FrameHit, res.FramePoint, speed));
            if (State == RoundState.Flight) State = RoundState.Live;
        }
        TrackLanding(prev, Ball.Position);
        _brain.ObservePlane(prev, Ball.Position);
        if (res.SphereHit)
        {
            float v;
            bool held = _brain.Contact(Ball, res.SpherePoint, _rng, out v);
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
        var judged = Pitch.Judge(Ball.Position, Ball.Radius);
        if (judged != Outcome.None)
        {
            if (judged == Outcome.Goal)
            {
                _events.Add(new SimEvent(EventKind.Goal, Ball.Position, speed));
                Ball.Velocity *= Tuning.Frame.NetSpeedKept;
            }
            else _events.Add(new SimEvent(EventKind.OutOfPlay, Ball.Position, speed));
            EndRound(judged);
            return;
        }
        if (State == RoundState.Live)
        {
            float limit = Scenario.ClockLimit ?? float.MaxValue;
            if (Clock >= limit || _liveSeconds >= Tuning.Round.MaxSeconds) EndRound(Outcome.TimeUp);
        }
    }

    static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    void StepAttacker(in TickIntent it)
    {
        var p = Attacker;
        var tuning = Tuning;
        _lastStick = it.Move;
        bool inPossession = _controller == p;
        var assist = Possession.Assist(p, it, Ball, inPossession, tuning);
        var legs = Possession.MovementIntent(p, it, Ball, inPossession, assist.TopSpeed, tuning);
        Movement.Step(p, legs, _dt, SimTime, assist, tuning, Pitch);

        // Other players on both teams stand still (no behaviour yet).
        if (State != RoundState.Live)
        {
            _shootWasHeld = it.ShootHeld;
            ShotCharge = 0f;
            return;
        }

        if (it.ShootHeld)
        {
            if (!_shootWasHeld)
            {
                _charge = 0f;
                _latchFinesse = _latchChip = false;
                _buffered = default;
                _bufferLeft = 0f;
                // In possession the next touch is a soft one, so the ball stays strikable through the charge.
                _softPending = inPossession;
                if (_softPending) TryTouch(p, it, softOnly: true);
                _strikeFoot = ShotModel.StrikingFoot(p, Ball.Position, tuning);
                p.StrikeFoot = _strikeFoot;
            }
            else if (_softPending && _controller == p) TryTouch(p, it, softOnly: true);
            _charge = MathF.Min(1f, _charge + _dt / tuning.Shot.ChargeSeconds);
            _latchFinesse |= it.Finesse;
            _latchChip |= it.Chip;
            ShotCharge = _charge;
        }
        else
        {
            ShotCharge = 0f;
            _softPending = false;
            if (_shootWasHeld)
            {
                var shot = new PendingShot
                {
                    Active = true,
                    Charge = _charge,
                    Stick = it.Move,
                    Type = _latchChip ? ShotType.Chip : _latchFinesse ? ShotType.Finesse : ShotType.Normal,
                    Foot = _strikeFoot,
                };
                _charge = 0f;
                _latchFinesse = _latchChip = false;
                if (ShotModel.CanStrike(p, Ball, tuning)) BeginStrike(shot);
                else { _buffered = shot; _bufferLeft = tuning.Round.ShootBuffer; }
            }
            else if (_buffered.Active)
            {
                _bufferLeft -= _dt;
                if (ShotModel.CanStrike(p, Ball, tuning)) { var s = _buffered; _buffered = default; BeginStrike(s); }
                else if (_bufferLeft <= 0f) _buffered = default; // dropped: no strike, no report
            }
        }
        _shootWasHeld = it.ShootHeld;

        // Dribble touches are blocked while shoot is held and while a released shot waits for the ball.
        if (State == RoundState.Live && !it.ShootHeld && !_buffered.Active) TryTouch(p, it, softOnly: false);
    }

    /// <summary>Lets a player touch the ball and raises the Touch event; a first contact with a loose ball gains possession.</summary>
    void TryTouch(PlayerState p, in TickIntent it, bool softOnly)
    {
        bool inPossession = _controller == p;
        if (!TouchModel.TryTouch(p, it, Ball, SimTime, inPossession, softOnly, Tuning, _rng, out var touch)) return;
        if (touch.Kind == TouchKind.Soft) _softPending = false;
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

    /// <summary>Fills the per-player contract read by the view: next touch, strike timing and feet.</summary>
    void PublishContract()
    {
        foreach (var team in Teams)
            foreach (var p in team)
            {
                p.NextTouchEta = float.NaN;
                p.StrikeEta = float.NaN;
            }
        var a = Attacker;
        if (State == RoundState.Strike && _pending.Active) a.StrikeEta = MathF.Max(0f, _strikeLeft);
        if (State == RoundState.Live && _controller == a)
        {
            var f = Possession.PredictNextTouch(a, _lastIntent, Ball, SimTime, true, _shootWasHeld, _softPending, Tuning);
            a.NextTouchEta = f.Eta;
            if (!float.IsNaN(f.Eta)) { a.NextTouchFoot = f.Foot; a.NextTouchPoint = f.Point; }
        }
    }

    void BeginStrike(PendingShot shot)
    {
        _pending = shot;
        _strikeLeft = Tuning.Round.StrikeDelay;
        State = RoundState.Strike;
        ShotCharge = 0f;
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
        _brain.OnStrike(_rng);
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
        _buffered = default;
        _softPending = false;
        if (outcome != Outcome.Held) SetController(null); // the ball is dead; a held ball stays the keeper's
    }

    void StepOutcome()
    {
        var idle = default(TickIntent);
        Movement.Step(Attacker, idle, _dt, SimTime, false, Tuning, Pitch);
        _brain.Update(_dt, Ball, _rng);
        if (Outcome == Outcome.Held) Ball.Position = Keeper.HandPosition;
        else
        {
            Ball.Step(_dt, null, null);
            if (Outcome == Outcome.Goal) ConstrainToNet();
        }
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
