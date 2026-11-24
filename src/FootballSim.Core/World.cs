using System.Numerics;

namespace FootballSim.Core;

/// <summary>
/// The simulation: two teams, one ball, the pitch, a clock and the round state machine. The game layer calls
/// <see cref="Begin"/> once, then <see cref="Step"/> once per tick (1/120 s), and reads the state afterwards.
/// All randomness comes from one <see cref="SimRng"/> seeded per round, so the same seed and intents give the same round.
/// </summary>
public sealed partial class World
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
    TickIntent _lastIntent;    // the attacker's intent on the latest tick (for the view's exit-touch forecast only)
    Vector2 _lastPushedStick;  // the latest left stick outside the dead zone, for the aim lock
    int _lastPushedTick;
    PendingShot _pending;      // a released shot waiting for its strike
    float _outcomeLeft;
    float _liveSeconds;
    int _endTick;
    float _endClock;
    ShotResult? _lastShot;

    // Possession.
    PlayerState? _controller;  // who controls the ball, or null when it is loose
    float _awayFor;            // seconds the ball has been beyond the controller's lose distance
    bool _softPending;         // shoot pressed with the ball at the feet: the soft touch is still to be made
    bool _ballPlayed;          // someone has controlled or struck the ball this round: a loose ball from now on was lost
    bool _shotAtFoot;          // shoot pressed with the ball at the feet: struck at the earliest strike tick if strikable then
    Foot _strikeFoot;          // the striking foot chosen for the shot under way: at the press for the aim then, final at the release
    FootSurface _strikeSurface; // ... and the part of it (the outside of the foot, or the shot type's own)
    StrikeDecision _strikeGeometry; // what the geometry alone gave at the last choice, before the contact check; stands from the release
    float _aimYaw;             // the shot's intended yaw: follows the stick while shoot is held, fixed at the release
    bool _strikeUntimed;       // the foot was chosen in a dribble without each foot's contact tick (a touch was still to be played, or the ball left the feet): chosen once more when they are known
    int _strikeEstMax = -1;    // the latest strike tick published for the shot under way (the estimate never moves earlier)
    int _shotStartTick = -1;   // tick shoot was pressed for the shot under way (for the fake shot), or -1
    bool _shotCancelled;       // the shot was turned into a fake: the rest of this press of shoot is ignored
    GestureRead _fake;         // a fake shot recognised: the shot winds up as a real one until the fake's contact ...
    int _fakeTick = -1;        // ... on this tick (the shot's planned strike, at least fake_contact_after_cancel after the cancel), or -1
    bool _flightPress;         // shoot was pressed while a shot was in flight and is still held: it counts as pressed when play goes live

    // Control.
    int _controlled;           // the index in team 0 of the player the team's intent drives
    int _jumpTick = int.MinValue / 2; // the tick control last jumped on ...
    Vector2 _jumpStick;        // ... and the left stick then (it reads as at rest until it has turned, or the settle time is over)

    // Lock-on.
    readonly LockOnClaim[] _claims = new LockOnClaim[2];
    readonly List<LockOnCandidate>[] _candidates = { new(), new() };

    const ulong KeeperSeedSalt = 0x6B65657065723144UL;

    struct PendingShot
    {
        public bool Active;
        public float Charge;
        public Vector2 Stick;
        public ShotType Type;
        public Foot Foot;
        public FootSurface Surface;
        /// <summary>The intended yaw at the release, radians.</summary>
        public float AimYaw;
        /// <summary>The earliest contact: the wind-up after the press (longer by the body angle at the release) and the lead after the release.</summary>
        public int ContactTick;
        /// <summary>
        /// Past this tick with the ball still out of reach, a shot without possession is dropped (a shot in possession is not:
        /// set when possession is lost).
        /// </summary>
        public int DeadlineTick;
        /// <summary>
        /// In possession past the earliest contact, the tick (plus one; 0: none) from which the controller has had no contact
        /// planned for the strike with him and the ball nearly still: a wind-up that cannot be struck, dropped StuckSeconds
        /// after it.
        /// </summary>
        public int UnplannedFrom;
        /// <summary>The kind of pass this is, or None for a shot at goal.</summary>
        public PassKind Pass;
    }

    /// <summary>Creates a world in the Setup state, with the ball, attacker and keeper placed per the scenario.</summary>
    /// <param name="tuning">Every tunable number.</param>
    /// <param name="scenario">Start spot and rules. A team size above 1 adds players who stand still.</param>
    /// <param name="attacker">Sheet of the attacker (team 0, player 0).</param>
    /// <param name="keeper">Sheet of the keeper (team 1, player 0).</param>
    /// <param name="seed">Seed for the round's random generator.</param>
    /// <param name="defender">Sheet of the defender (team 1, player 1) in a scenario with one; a default sheet when null.</param>
    /// <param name="mate">Sheet of the team-mates (team 0, players 1 and on) in a scenario with one; the attacker's sheet when null.</param>
    public World(Tuning tuning, Scenario scenario, PlayerSheet attacker, PlayerSheet keeper, ulong seed, PlayerSheet? defender = null, PlayerSheet? mate = null)
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
        HasMates = scenario.Mate != null && scenario.TeamSize >= 2;
        for (int i = 0; i < Math.Max(1, scenario.TeamSize); i++)
        {
            t0.Add(new PlayerState(0, i, PlayerRole.Outfield, i > 0 && HasMates ? mate ?? attacker : attacker));
            // (with team-mates the other side is the keeper and the defenders only)
            if (i == 0 || !HasMates) t1.Add(new PlayerState(1, i, i == 0 ? PlayerRole.Keeper : PlayerRole.Outfield, i == 0 ? keeper : attacker));
        }
        if (scenario.Defenders > 0)
        {
            Defender = new PlayerState(1, t1.Count, PlayerRole.Outfield, defender ?? new PlayerSheet { Id = "defender", Name = "Defender" });
            t1.Add(Defender);
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
    /// <summary>
    /// The attacker the intents control: team 0's controlled player (see <see cref="ControlledIndex"/>). Player 0 unless a pass
    /// or a loose ball has moved control to a team-mate.
    /// </summary>
    public PlayerState Attacker => Teams[0][_controlled];
    /// <summary>The index in team 0 of the player the team's intent drives.</summary>
    public int ControlledIndex => _controlled;
    /// <summary>True in a scenario with team-mates: team 0's other players are played by the team-mate bot.</summary>
    public bool HasMates { get; }
    /// <summary>The keeper (team 1, player 0).</summary>
    public PlayerState Keeper => Teams[1][0];
    /// <summary>The player who controls the ball (dribbling it, or the keeper holding it), or null when the ball is loose.</summary>
    public PlayerState? BallController => _controller;
    /// <summary>True once someone has controlled or struck the ball this round: a loose ball from then on can be claimed (see LockOn).</summary>
    public bool BallPlayed { get => _ballPlayed; internal set => _ballPlayed = value; }
    /// <summary>The loose ball's forecast path (see <see cref="BallForecast"/>): built for the current ball path, empty while nobody may claim the ball.</summary>
    public BallForecast Forecast { get; } = new();
    /// <summary>Each team's claim on the loose ball (see <see cref="LockOn"/>): index 0 the attackers', 1 the keeper's side. A null player is no claim.</summary>
    public IReadOnlyList<LockOnClaim> Claims => _claims;
    /// <summary>True when <paramref name="p"/> holds his team's claim on the loose ball this tick.</summary>
    public bool HoldsClaim(PlayerState p) => _claims[p.Team].Player == p;
    /// <summary>The keeper's numbers for the latest shot.</summary>
    public KeeperReport KeeperReport => _brain.Report;
    /// <summary>Why the keeper did what he did on the latest tick (a diagnostic for probes; changes nothing).</summary>
    public KeeperThought KeeperThought => _brain.Thought;
    /// <summary>Number of intents <see cref="Step"/> expects: both teams' players, team 0 first.</summary>
    public int PlayerCount => Teams[0].Count + Teams[1].Count;
    /// <summary>True while the round is being simulated (Live, Strike, Flight or Outcome).</summary>
    public bool IsSimulating => State is RoundState.Live or RoundState.Strike or RoundState.Flight or RoundState.Outcome;
    /// <summary>
    /// The aim assistance level of the round: how far a shot's aim is pulled onto the frame when the stick points wide of it
    /// (see <see cref="ShotModel.AimTargetZ"/>). Set when the round is set up; Off unless set, as in every batch run and probe.
    /// </summary>
    public AimAssistLevel AimAssist { get; set; }
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
            Type = ShotModel.TypeFor(_latchFinesse, _latchChip, _latchDriven), Foot = _strikeFoot, AimAssist = AimAssist,
        };
        ctx.Surface = SurfaceNow(ctx.Type);
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
        _shootWasHeld = false; _charge = 0f; _latchFinesse = _latchChip = _latchDriven = false; _lastStick = default; _lastIntent = default;
        _lastPushedStick = default; _lastPushedTick = int.MinValue / 2;
        _pending = default; _outcomeLeft = 0f;
        _liveSeconds = 0f; _endTick = 0; _endClock = 0f; _lastShot = null;
        _controller = null; _awayFor = 0f; _softPending = false; _ballPlayed = Scenario.BallLoose; _shotAtFoot = false; _strikeFoot = Foot.Right; _strikeSurface = FootSurface.Laces; _strikeGeometry = new StrikeDecision(new StrikeChoice(Foot.Right, FootSurface.Laces), StrikeRule.Ahead); _aimYaw = 0f; _strikeUntimed = false;
        _shotStartTick = -1; _shotCancelled = false; _strikeEstMax = -1; _fakeTick = -1; _fake = default;
        _flightPress = false;
        _controlled = 0; _jumpTick = int.MinValue / 2; _jumpStick = default;
        ResetPass(); _passes = 0; _received = 0; _offsides = 0; _controlChanges = 0;
        ResetMeasures();
        _claims[0] = default; _claims[1] = default;
        Forecast.Clear();

        float r = Tuning.Ball.Radius;
        Ball.Position = new Vector3(Scenario.Ball.X, r, Scenario.Ball.Z);
        float heading = Scenario.BallHeadingDeg * MathF.PI / 180f;
        Ball.Velocity = new Vector3(MathF.Cos(heading), 0f, MathF.Sin(heading)) * Scenario.BallSpeed;
        Ball.Spin = Scenario.BallSpeed > 0f ? Ball.RollingSpin(Ball.Velocity) : Vector3.Zero;

        var team0 = Teams[0];
        var team1 = Teams[1];
        for (int i = 0; i < team0.Count; i++)
        {
            var p = team0[i];
            p.Position = i == 0
                ? new Vector3(Scenario.Attacker.X, 0f, Scenario.Attacker.Z)
                : HasMates
                    // (the first team-mate on his spot, any more beside him)
                    ? ClampToPitch(new Vector3(Scenario.Mate!.X - 3f * (i - 1), 0f, Scenario.Mate.Z + (i % 2 == 0 ? -4f : 4f) * ((i - 1 + 1) / 2)))
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
        ResetDefender(seed);
        _brain = new KeeperBrain(Keeper, Pitch, Tuning, Scenario.KeeperHoldLine, Teams[0][0].Sheet)
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
        p.TurnFatigue = 0f;
        p.NextTouchEta = float.NaN;
        p.NextTouchFoot = p.Sheet.PreferredFoot;
        p.NextTouchPoint = Vector3.Zero;
        p.PendingSkill = null;
        p.SkillExitTouch = null;
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
        p.Regain = RegainMode.None;
        p.LockOnTick = -1;
        p.LockOnPoint = Vector3.Zero;
        p.LockOnBallVelocity = Vector3.Zero;
        p.LockOnResting = false;
        p.LockOnSlack = 0;
        p.LockOnSolve = default;
        p.MinArrivalTick = 0;
        p.MinArrivalHeight = float.NaN;
        p.MinArrivalStrike = false;
        p.MeetNotBefore = 0;
        p.ReceivingPass = false;
        p.ReceiveCooldownUntil = -100f;
        p.OffsideFlag = false; p.MateRun = MateRun.Support; p.MateBeyondTick = -1; p.MateRunEndTick = int.MinValue / 2; p.MateRuns = 0; p.MateSlopeTick = int.MinValue / 2; p.MateLineX = float.NaN; p.MateLineVel = 0f; p.MateAvoidSide = 0; p.MateBumpUntil = 0; p.MateHeading = default;
        p.LastTouchKind = ActionKind.First;
        p.KnockOnRequested = false;
        p.StepsSinceTouch = 0;
        p.PushSteps = 1;
        p.ClearForecast();
        p.ShotEarliestTick = -1;
        p.ShotCommitAllowed = false;
        p.StrikeContactTick = -1;
        p.TouchBoostFrom = float.NaN;
        p.TouchBoostUntil = -100f;
        p.TouchBoostFactor = 1f;
        p.KnockOnFrom = float.NaN;
        p.DangerSlowFrom = float.NaN;
        p.DangerSlowFactor = 1f;
        p.KnockOnUntil = -100f;
        p.KnockOnDirection = Vector2.Zero;
        p.MoveTime = 0f;
        p.Def.Reset();
        p.DefenderAction = DefenderAction.Hold;
        p.DefenderActionTime = 0f;
        p.ActionSide = SkillSide.None;
        p.StumbleUntil = -100f;
        p.TackledLockUntil = -100f;
        p.Jockeying = false;
        p.BlockReach = 0f;
        p.TackleFoot = p.Position;
        p.TackleContactTime = float.NaN;
        p.PushVelocity = Vector3.Zero;
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

        // The team's intent drives its controlled player; just after control has jumped his left stick reads as at rest.
        TickIntent atk = Settled(IntentFor(intents, 0, 0));
        // The claims on a loose ball, for the tick about to be stepped: before the defender's bot, which reads his.
        UpdateLockOn(atk);

        // The defender's intent comes from his bot (or, with the bot off, from the caller); either way it is what is recorded.
        TickIntent def = default;
        if (Defender != null)
        {
            int flat = Teams[0].Count + Defender.Index;
            def = DefenderBotEnabled && State != RoundState.Outcome ? _bot!.Next(this) : IntentFor(intents, 1, Defender.Index);
            if (!DefenderBotEnabled && DefenderBotShadow && State != RoundState.Outcome) _bot!.Next(this);
            if (_recorder != null)
            {
                Span<TickIntent> all = stackalloc TickIntent[PlayerCount];
                for (int i = 0; i < all.Length && i < intents.Length; i++) all[i] = intents[i];
                all[flat] = def;
                _recorder.Record(all);
            }
        }
        else _recorder?.Record(intents);
        Tick++;
        SimTime += _dt;
        StepSimulation(atk, def);
        PublishContract();
        ObserveMeasures();
    }

    /// <summary>
    /// Both teams' claims on the loose ball for the tick about to be stepped (see <see cref="LockOn.Update"/>): they stand only in
    /// Live or Strike, once the ball has been played, while nobody controls it. The attacker and the defender are the candidates.
    /// </summary>
    void UpdateLockOn(in TickIntent atk)
    {
        bool live = State is RoundState.Live or RoundState.Strike && _ballPlayed && _controller == null;
        UpdatePassRecord();
        // A pass on its way to a named receiver: only he goes for it, until he is late.
        var only = _pass is { Receiver: not null, Reopened: false } rec ? rec.Receiver : null;
        _candidates[0].Clear(); _candidates[1].Clear();
        if (live)
        {
            if ((only == null || only == Attacker) && !Attacker.OffsideFlag)
                _candidates[0].Add(new LockOnCandidate(Attacker, atk, Stick: Attacker.ShotAction == null, LockOnRun.Attacker));
            // (a team-mate may hold the claim too: control then jumps to him)
            if (HasMates)
                foreach (var m in Teams[0])
                    if (m != Attacker && !m.OffsideFlag && (only == null || only == m)) _candidates[0].Add(new LockOnCandidate(m, default, Stick: false, LockOnRun.Attacker));
            // (against a team he first has to see the strike: no claim run until his reaction delay is over)
            if (Defender != null && !DefenderReacting) _candidates[1].Add(new LockOnCandidate(Defender, default, Stick: false, LockOnRun.Defender));
        }
        // (the named receiver meets the pass where it was played to, at its expected arrival)
        foreach (var m in Teams[0]) m.MeetNotBefore = only == m ? _pass!.ExpectedArrivalTick : 0;
        LockOn.Update(Forecast, _claims, _candidates, Teams, Keeper, Ball, Pitch, live, Tick + 1, SimTime + _dt, Tuning, pass: _pass != null);
        if (_pass is { PathTick: < 0 } fresh && Forecast.Valid) fresh.PathTick = Forecast.StartTick;
        // (a pass played to him is taken at his feet, not toe-poked from a stride off)
        if (only != null && only.Regain == RegainMode.Free) only.Regain = RegainMode.Contested;
        // Control follows the ball: with it loose, the team's claim holder is the player the intent drives, once he has held
        // the claim for control.claim_jump_seconds.
        if (HasMates && _claims[0].Player is { } holder && holder != Attacker
            && Tick + 1 - _claims[0].SinceTick >= Ticks(Tuning.Control.ClaimJumpSeconds))
            JumpTo(holder.Index, ControlReason.Claim);
    }

    /// <summary>
    /// The team's intent as its controlled player reads it: for control.stick_settle_seconds after control has jumped the left
    /// stick reads as at rest, unless it has turned more than 30 degrees since the jump (so the stick that aimed a pass does not
    /// steer the receiver's first step).
    /// </summary>
    TickIntent Settled(TickIntent it)
    {
        if (Tick + 1 - _jumpTick > Ticks(Tuning.Control.StickSettleSeconds)) return it;
        var stick = it.Move;
        if (stick.Length() < Tuning.Movement.StickDeadZone) return it;
        bool turned = _jumpStick.Length() < Tuning.Movement.StickDeadZone
            || MathF.Abs(Movement.WrapAngle(MathF.Atan2(stick.Y, stick.X) - MathF.Atan2(_jumpStick.Y, _jumpStick.X))) > SettleTurnDeg * MathF.PI / 180f;
        if (turned) { _jumpTick = int.MinValue / 2; return it; }
        it.MoveX = 0f; it.MoveZ = 0f;
        return it;
    }

    /// <summary>A left stick turned further than this since a control jump is a new push, degrees.</summary>
    const float SettleTurnDeg = 30f;

    /// <summary>
    /// Moves the team's intent to player <paramref name="index"/> of team 0. A shot or pass the old player had under way is
    /// dropped, and the world's input state (charge, the shoot edge, latches, the aim lock) starts afresh for the new one: a
    /// button held across the jump is not a fresh press. The new player's gesture reader and stick history start afresh too.
    /// Raises ControlChanged.
    /// </summary>
    void JumpTo(int index, ControlReason reason)
    {
        if (index == _controlled || index < 0 || index >= Teams[0].Count) return;
        var old = Attacker;
        int from = _controlled;
        if (old.ShotAction is { Made: false } s)
        {
            TouchPlanner.OnShotEnded(old);
            s.Phase = ActionPhase.Done;
            old.ShotAction = null;
        }
        _pending = default;
        if (State == RoundState.Strike) State = RoundState.Live;
        _shotStartTick = -1; _charge = 0f; ShotCharge = 0f;
        _latchFinesse = _latchChip = _latchDriven = false;
        _softPending = false; _shotAtFoot = false; _strikeUntimed = false; _strikeEstMax = -1;
        _fakeTick = -1; _fake = default; _flightPress = false;
        // (a shoot button held across the jump: the rest of that press is ignored, so it is neither a press nor a release)
        _shootWasHeld = false;
        _shotCancelled = _lastIntent.ShootHeld;
        _lastPushedStick = default; _lastPushedTick = int.MinValue / 2;
        old.MinArrivalTick = 0; old.MinArrivalHeight = float.NaN; old.MinArrivalStrike = false;
        old.KnockOnRequested = false;
        old.PlannerSteer = Vector2.Zero; old.SteerCarry = Vector2.Zero;
        ResetPassInput(old);

        _controlled = index;
        var now = Attacker;
        GestureReader.Reset(now);
        now.SkillBufTick = -1;
        now.KnockOnRequested = false;
        now.StickAnglePrev = float.NaN; now.StickRate = 0f; now.StickTurnTicks = 0; now.StickSettledAngle = float.NaN;
        now.StickChangeTick = -100000; now.StickReleasedTicks = 0; now.LastStickMove = Vector2.Zero;
        now.StepsSinceTouch = 0; now.PushSteps = 1; now.Chasing = false; now.ClearForecast();
        _jumpTick = Tick;
        // (the run cycle starts afresh for both: the new player is the pad's, the old one the bot's)
        old.MateRun = MateRun.Support; old.MateBeyondTick = -1; Attacker.MateRun = MateRun.Support; Attacker.MateBeyondTick = -1;
        _jumpStick = _lastIntent.Move;
        _controlChanges++;
        _events.Add(new SimEvent(EventKind.ControlChanged, now.Position, now.Speed, Team: 0, PlayerIndex: index, Other: from, Reason: reason));
    }

    /// <summary>The pass input starts afresh (see World.Pass.cs); nothing to do before passes exist.</summary>
    partial void ResetPassInput(PlayerState old);

    /// <summary>
    /// A team-mate's tick: his bot writes an intent (move and sprint only) and he runs it, without the ball. While he holds the
    /// team's claim on a loose ball (the moment before control jumps to him) his legs run to the meeting. He never touches the
    /// ball, shoots or tackles.
    /// </summary>
    void StepMate(PlayerState p)
    {
        var it = MateIntent(p);
        if (_claims[0].Player == p && p.LockOnTick >= 0) it = LockOn.Legs(p, Tick, Tuning);
        p.CurrentTick = Tick;
        p.Regain = _claims[0].Player == p ? p.Regain : RegainMode.None;
        float top = Movement.TopSpeed(p.Sheet, Tuning);
        // (the passer is planted for a moment after his strike, like a shooter)
        bool stunned = TouchPlanner.Stunned(p, SimTime);
        if (Defending.Stumbling(p, SimTime)) top = MathF.Min(top, Tuning.Defending.StumbleSpeed);
        var assist = stunned
            ? new MoveAssist(top * Tuning.Shot.StunSpeedFraction, 1f + Tuning.Possession.OffBallTurnBonus, Vector2.Zero, HardCap: top * Tuning.Shot.StunSpeedFraction, TurnAbout: true)
            : new MoveAssist(top, 1f + Tuning.Possession.OffBallTurnBonus, Vector2.Zero, TurnAbout: true);
        // (standing, he turns to face the ball)
        if (it.Move.LengthSquared() < 1e-6f && p.Speed < 0.5f && !stunned)
        {
            float want = MathF.Atan2(Ball.Position.Z - p.Position.Z, Ball.Position.X - p.Position.X);
            float step = Movement.TurnRateDeg(p.Sheet, Tuning) * MathF.PI / 180f * _dt;
            assist = assist with { Facing = p.Facing + Math.Clamp(Movement.WrapAngle(want - p.Facing), -step, step) };
        }
        Movement.Step(p, it, _dt, SimTime, assist, Tuning, Pitch);
        ApplyPush(p);
    }

    /// <summary>The team-mate bot's intent for <paramref name="p"/> this tick (at rest until the bot exists).</summary>
    TickIntent MateIntent(PlayerState p)
    {
        TickIntent it = default;
        MateBotIntent(p, ref it);
        return it;
    }

    partial void MateBotIntent(PlayerState p, ref TickIntent it);

    /// <summary>Body contacts between the attackers, so two of them never stand on one spot.</summary>
    void SeparateMates()
    {
        var team = Teams[0];
        for (int i = 0; i < team.Count; i++)
            for (int j = i + 1; j < team.Count; j++)
                Contacts.Separate(team[i], team[j], Tick, SimTime, Tuning, Pitch);
    }

    void StepSimulation(in TickIntent atk, in TickIntent def)
    {
        _brain.BeginTick(_dt);
        if (State == RoundState.Outcome)
        {
            StepOutcome();
            return;
        }

        // 0. The team-mates (their bot's run), before the controlled attacker: a pass he strikes this tick moves control to one
        // of them, and each player moves once a tick.
        if (HasMates)
            foreach (var m in Teams[0])
                if (m != Attacker) StepMate(m);

        // 1. Attacker: movement, shoot input, touches.
        StepAttacker(atk);

        // 2. The released shot (see TryStrike); a shot turned into a fake is never struck.
        if (_pending.Active && _fakeTick < 0 && (State == RoundState.Strike || _pending.Pass != PassKind.None && State == RoundState.Live)) TryStrike();

        // 2b. The defender (his movement, the tackles he starts, the auto tackle, a loose ball he controls), then the body
        // contact between him and the attacker. A shot struck above is already on its way.
        if (Defender != null) { if (StepDefender(def)) return; }
        else if (HasMates) SeparateMates();

        // 3. Keeper.
        _brain.Update(_dt, Ball, _keeperRng, KeeperSituation());

        // 4. Ball, with the frame, the arena boards, the keeper's hands and the defender's body, leg and foot as obstacles.
        Vector3 prev = Ball.Position;
        var res = Ball.Step(_dt, Pitch, _brain.HandsSphere, _brain.BodyCapsule, _brain.HandsCapsule, DefenderObstacles());
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
            // Off the boards the shot is over and the ball is in play again; untouched, it was a clean miss.
            if (State == RoundState.Flight && !res.SphereHit && !res.ObstacleHit) NoteMiss();
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
        // The defender's foot, leg or body meeting the ball, and a lunge or slide that meets the attacker instead.
        if (Defender != null && ResolveDefenderContact(res)) return;

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
        // (A shot queued on the way to a loose ball does not stop the clock: it may wait seconds for its meeting.)
        bool clockRuns = State == RoundState.Live || State == RoundState.Strike && _controller == null && _claims[0].Player == Attacker;
        if (clockRuns) { Clock += _dt; }
        _liveSeconds += _dt;

        // 6. Referee.
        if (!IsFinite(Ball.Position) || !IsFinite(Ball.Velocity) || !IsFinite(Attacker.Position) || !IsFinite(Keeper.Position)
            || Defender != null && !IsFinite(Defender.Position))
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
        if (clockRuns)
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
    internal KeeperSituation KeeperSituation()
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
            // Between a two-contact move's contacts its next touch is the second one; through a spin the body is between ball and
            // opponent and the ball counts as at his foot (his next touch is now).
            else if (a.SkillAction is { Made: true, Contact2Tick: > 0 } s2 && s2.Phase != ActionPhase.Done)
            {
                if (s2.Kind == ActionKind.Spin) { best = 0f; tick = s2.StartTick; }
                else if (s2.Contact2Tick > Tick) Take(s2.Contact2Tick);
            }
            if (a.ShotAction is { Made: false } sh) Take(sh.ContactTick);
        }
        // Without the ball but holding his team's claim on it: his meeting is his next touch (one misjudgement per claim).
        if (float.IsPositiveInfinity(best) && _controller == null && _claims[0].Player == a && a.LockOnTick >= 0)
        {
            best = MathF.Max(0f, (a.LockOnTick - Tick) * _dt);
            tick = -2 - _claims[0].SinceTick;
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
            // (a pass winding up is not a shot: he does not plant for it)
            Windup = ShotWindup,
            StrikeIn = ShotWindup && a.ShotAction is { } shot ? MathF.Max(0f, (shot.ContactTick - Tick) * _dt) : float.PositiveInfinity,
            ShotInFlight = State == RoundState.Flight,
            AttackerPosition = a.Position,
            AttackerVelocity = a.Velocity,
            DefenderCovers = Defender != null && DefenderCovers(),
            Coming = PassMeeting,
        };
    }

    static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    int Ticks(float seconds) => Math.Max(0, (int)MathF.Round(seconds * Tuning.TickRate));

    void StepAttacker(in TickIntent it)
    {
        var p = Attacker;
        var tuning = Tuning;
        _lastStick = it.Move;
        _lastIntent = it;
        if (it.Move.Length() >= tuning.Movement.StickDeadZone) { _lastPushedStick = it.Move; _lastPushedTick = Tick; }
        bool inPossession = _controller == p;

        // Skill gestures are read every tick so their timing runs on through every state. A fake shot is recognised on the
        // cancel, but the shot winds up on as a real one (the same legs, the same shot action the keeper reads) until the fake's
        // contact, on the tick the strike was planned for when the cancel came (at least fake_contact_after_cancel after it):
        // only then is the shot aborted and the fake move made, its contact on that tick.
        var gesture = GestureReader.Read(p, it, Tick, tuning);
        // A gesture is taken up on the tick it registers, or not at all; the step-over and the ball roll on any tick it is held.
        bool claimed = _claims[0].Player == p && p.LockOnTick >= 0;
        if (gesture.Kind == Gesture.None && p.SkillBufTick < 0 && GestureReader.Held(p, Tick, tuning) is { Kind: Gesture.StepOver or Gesture.BallRoll } still)
            gesture = still;
        if (gesture.Kind != Gesture.None && !SkillMoves.CanTakeUp(p, gesture.Kind, Ball, inPossession, claimed, SimTime, tuning))
            gesture = GestureRead.None;
        // With sprint held the right stick arms a heavy touch (the knock-on) for his next touch.
        GestureReader.ReadHeavyTouch(p, it, Tick, State == RoundState.Live && (inPossession || claimed), tuning);
        bool shotUnderWay = State == RoundState.Strike && _pending.Active || State == RoundState.Live && _shootWasHeld;
        int strikeTick = _shotStartTick >= 0 && p.ShotAction is { Made: false } planned ? planned.ContactTick : -1;
        var fake = GestureReader.ReadFakeShot(p, it, Tick, _fakeTick < 0 ? strikeTick : -1, shotUnderWay, tuning);
        if (fake.Kind != Gesture.None && _fakeTick < 0)
        {
            _fake = fake;
            _fakeTick = Math.Max(strikeTick, Tick + Ticks(tuning.Skill.FakeContactAfterCancel));
        }
        int fakeFrom = -1;
        if (_fakeTick >= 0 && Tick >= _fakeTick)
        {
            _fakeTick = -1;
            if (shotUnderWay && p.ShotAction is { Made: false })
            {
                fakeFrom = _shotStartTick;
                AbortShot(it.ShootHeld);
                gesture = _fake;
            }
        }
        if (!it.ShootHeld) _shotCancelled = false;
        bool shootHeld = it.ShootHeld && !_shotCancelled;
        if (gesture.Kind != Gesture.None) SkillMoves.Request(p, gesture, Tick, fakeFrom);
        if (gesture.Kind == Gesture.DragBack && inPossession) TouchPlanner.YieldFeetTurn(p, Tick, tuning);
        if (State == RoundState.Strike && _pending.Active && shootHeld && !_shootWasHeld) PressShootAgain(p, it);
        if (State == RoundState.Live && !shootHeld && _passKind == PassKind.None
            && SkillMoves.TryStart(p, Ball, inPossession, Tick, SimTime, tuning) is SimEvent started)
            _events.Add(started);
        // Shoot pressed: before the touch controller runs, so from the press tick on it plays no dribble touch it has not
        // already committed, and its next contact is the strike.
        // A press made while the last shot was still in flight, and still held: it counts on the tick play is live again.
        if (State == RoundState.Flight && shootHeld && !_shootWasHeld) _flightPress = true;
        if (!shootHeld) _flightPress = false;
        if (State == RoundState.Live && shootHeld && (!_shootWasHeld || _flightPress) && _passKind == PassKind.None)
        {
            _flightPress = false;
            PressShoot(p, inPossession);
        }
        // A pass button pressed, with the ball or on the way to a ball he holds the claim on. While a shot is under way the
        // same button is the fake-shot cancel, and no pass is read.
        bool passButton = it.PassHeld || it.ThroughHeld;
        if (!passButton) _passCancelled = false;
        bool passHeld = passButton && !_passCancelled;
        bool claimNow = _claims[0].Player == p && p.LockOnTick >= 0;
        bool shotBusy = shootHeld || _shootWasHeld || _pending.Active || p.ShotAction is { Made: false };
        // (a ball at his feet that nobody has played yet, as at the start of a round, can be passed like one he has)
        bool atFeet = _controller == null && ShotModel.CanStrike(p, Ball, tuning);
        if (State == RoundState.Live && passHeld && !_passWasHeld && !shotBusy && (inPossession || claimNow || atFeet) && !TouchPlanner.Stunned(p, SimTime))
            PressPass(p, inPossession, it.ThroughHeld ? PassKind.Through : PassKind.Ground);
        _passWasHeld = passButton;
        ChooseStrikeTimed(p);
        ShotForController(p);

        // From the press to the strike the controller's go-get-it legs run on to the ball (the left stick only aims).
        bool windup = State == RoundState.Live && shootHeld || State == RoundState.Strike && _pending.Active || _passKind != PassKind.None;
        // A loose ball he holds his team's claim on: his legs run to the meeting, through a shot's wind-up too (see LockOn).
        // A ball loose at the start of the round (on the spot) is not claimed: he stands by it until he plays it.
        bool holder = _claims[0].Player == p && p.LockOnTick >= 0;
        if (!inPossession && !holder) p.KnockOnRequested = false;
        var assist = TouchPlanner.BeginTick(p, it, Ball, inPossession, State == RoundState.Live || windup, Tick, SimTime, tuning, Pitch, out var legs);
        // Stumbling (tackled or barged), his speed is capped.
        if (Defending.Stumbling(p, SimTime))
        {
            float stumble = tuning.Defending.StumbleSpeed;
            assist = assist with { TopSpeed = MathF.Min(assist.TopSpeed, stumble), HardCap = MathF.Min(assist.HardCap, stumble) };
        }
        Movement.Step(p, legs, _dt, SimTime, assist, tuning, Pitch);

        // Other players on both teams stand still (no behaviour yet).
        if (State == RoundState.Live)
        {
            // A knock-on asked for on the way to a ball he has the claim on is kept: his first touch is the knock-on.
            if (it.KnockOn && (inPossession || holder)) TouchPlanner.RequestKnockOn(p);

            if (shootHeld)
            {
                _charge = MathF.Min(1f, _charge + _dt / tuning.Shot.ChargeSeconds);
                _latchFinesse |= it.Finesse;
                _latchChip |= it.Chip;
                _latchDriven |= it.Driven;
                ShotCharge = _charge;
                if (p.ShotAction is { } shot)
                {
                    var type = ShotModel.TypeFor(_latchFinesse, _latchChip, _latchDriven);
                    shot.Kind = ShotModel.KindFor(type);
                    shot.Surface = SurfaceNow(type);
                    // The aim follows the stick until the release (and with it the wind-up the body angle adds).
                    _aimYaw = AimYawFor(p, AimStick(it.Move), type);
                    shot.AimYaw = _aimYaw;
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
        // The pass winding up with its button still held (or just let go): its charge, aim and release.
        if (State == RoundState.Live && _passKind != PassKind.None && !_pending.Active) HoldPass(p, it, passHeld);

        // While the shot winds up the only touches are the soft touch of a ball at the feet and one committed before the press;
        // during the stun after a strike none at all.
        if (windup || State == RoundState.Live)
        {
            var mode = !windup ? TouchStepMode.Normal
                : _controller != p ? TouchStepMode.None
                : _softPending ? TouchStepMode.SoftOnly : TouchStepMode.CommittedOnly;
            // A shot queued on the way to the ball whose only meeting comes before its wind-up is done: a flick close to the
            // goal (see TryFlick); anywhere else a plain first touch, and the shot carries on as a shot in possession.
            bool early = windup && holder && _claims[0].Early && _controller != p && p.ShotAction is { Made: false } && _fakeTick < 0;
            // (a pass is flicked from anywhere, with the ball within 150 degrees of where he faces)
            bool flick = early && Ball.Position.Y < tuning.Shot.StrikeMaxHeight && (_passKind != PassKind.None ? WithinFacing(p, PassFlickDeg) : FlickInRange());
            if (early && !flick) mode = TouchStepMode.Normal;
            if (TouchPlanner.Stunned(p, SimTime)) mode = TouchStepMode.None;
            // A meeting the stick slid to a later sample: he lets the ball come to it rather than take it the moment it is in
            // reach (unless it is getting away from him).
            if (holder && _claims[0].Slid && _controller == null && Tick < _claims[0].ArriveTick - SlideEarlyTicks
                && Vector3.Dot(Ball.Velocity - p.Velocity, p.Position - Ball.Position) > 0f) mode = TouchStepMode.None;
            // Stumbling or just tackled he cannot play the ball; nor a loose ball he and the defender are equally near, if the
            // coin went the defender's way.
            bool barred = Defending.Stumbling(p, SimTime) || SimTime < p.TackledLockUntil;
            if (barred || DefenderDeniesTouch(p)) mode = TouchStepMode.None;
            // Just after receiving a pass he plays no touch for a moment (a strike or pass already queued is not held back).
            if (SimTime < p.ReceiveCooldownUntil && _controller == p && mode == TouchStepMode.Normal) mode = TouchStepMode.None;
            if (flick && !barred && !TouchPlanner.Stunned(p, SimTime) && ShotModel.CanStrike(p, Ball, tuning)) Flick(p, it);
            else
            {
                bool had = _controller == p;
                TryTouch(p, it, mode);
                // The first touch of a shot queued without the ball: from here it is a shot in possession, its foot chosen
                // again once the feet's contacts are known.
                if (!had && _controller == p && p.ShotAction is { Made: false }) { _shotAtFoot = false; _softPending = false; _strikeUntimed = true; }
            }
        }
        UpdateStrikeEstimate();
        // The shot under way writes its earliest strike tick as the earliest meeting his claim may take (see LockOn).
        bool queued = p.ShotAction is { Made: false } && _shotStartTick >= 0 && _fakeTick < 0 && _controller != p;
        p.MinArrivalTick = queued ? EarliestStrikeTick(Tick + 1) : 0;
        p.MinArrivalHeight = queued ? tuning.Shot.StrikeMaxHeight - 0.02f : float.NaN;
        p.MinArrivalStrike = queued;
    }

    /// <summary>A first touch at a meeting the stick slid is made no more than this many ticks before the meeting.</summary>
    const int SlideEarlyTicks = 2;

    /// <summary>A queued pass is flicked with the ball within this of where he faces, degrees.</summary>
    const float PassFlickDeg = 150f;

    /// <summary>True when the ball is within <paramref name="deg"/> of where <paramref name="p"/> faces (or at his feet).</summary>
    bool WithinFacing(PlayerState p, float deg)
    {
        var to = Flat(Ball.Position) - Flat(p.Position);
        if (to.LengthSquared() < 1e-4f) return true;
        return MathF.Abs(Movement.WrapAngle(MathF.Atan2(to.Y, to.X) - p.Facing)) * 180f / MathF.PI <= deg;
    }

    /// <summary>True when the ball is close enough to the goal centre for a flick (shot.flick_max_distance_m).</summary>
    bool FlickInRange() => ShotModel.GoalDistance(Ball.Position, Pitch) <= Tuning.Shot.FlickMaxDistanceM;

    /// <summary>
    /// A flick: a shot queued on the way to the ball is struck at his first contact, before its wind-up is done, because no later
    /// sample of the ball's path is his. Shoot counts as let go now, with the charge built so far; the ball leaves at
    /// shot.flick_power of that charge's speed with the spreads times shot.flick_error.
    /// </summary>
    void Flick(PlayerState p, in TickIntent it)
    {
        if (!_pending.Active)
        {
            if (_passKind != PassKind.None) ReleasePass(p, it);
            else
            {
                ReleaseShoot(p, it);
                // (the rest of this press of shoot is spent: it neither charges again nor makes the flick a low shot)
                _shotCancelled = it.ShootHeld;
            }
        }
        ExecuteStrike(flick: true);
    }

    /// <summary>
    /// The earliest tick the shot under way may be struck on; while shoot is held, as if it were let go on
    /// <paramref name="releaseTick"/> with the aim as it is now: the wind-up after the press, longer by what the body angle adds
    /// (see <see cref="ShotModel.ExtraWindupSeconds"/>), and the lead after the release.
    /// </summary>
    int EarliestStrikeTick(int releaseTick) => _pending.Active
        ? _pending.ContactTick
        // (a pass: its execution time after the press, by the charge so far and the body angle to it)
        : _passKind != PassKind.None ? _shotStartTick + Ticks(PassModel.ExecutionSeconds(Attacker, _passCharge, _aimYaw, Tuning))
        : Math.Max(_shotStartTick + Ticks(Tuning.Shot.WindupSeconds + ShotModel.ExtraWindupSeconds(Attacker.Facing, _aimYaw, Tuning)),
            releaseTick + Ticks(Tuning.Shot.ReleaseLeadSeconds));

    /// <summary>The intended yaw of a shot of <paramref name="type"/> released now with the aim stick <paramref name="stick"/>.</summary>
    float AimYawFor(PlayerState p, Vector2 stick, ShotType type)
    {
        var ctx = new ShotContext { Shooter = p, BallPosition = Ball.Position, Stick = stick, Type = type, AimAssist = AimAssist };
        return ShotModel.IntendedYaw(ctx, Pitch, Tuning, out _);
    }

    /// <summary>
    /// What the geometry alone gave for the shot under way at its last choice, and the rule that gave it (see
    /// <see cref="ShotModel.ChooseByGeometry"/>), fixed from the release. The foot that strikes differs from it only when the
    /// contact check handed the shot over (see <see cref="ShotModel.WithContact"/>).
    /// </summary>
    public StrikeDecision StrikeGeometry => _strikeGeometry;

    /// <summary>The part of the foot the shot under way is struck with: the outside if that was chosen, else the type's own.</summary>
    FootSurface SurfaceNow(ShotType type) => _strikeSurface == FootSurface.Outside ? FootSurface.Outside : ShotModel.SurfaceFor(type);

    /// <summary>
    /// Chooses the striking foot and surface for the shot under way (see <see cref="ShotModel.ChooseStrike"/>) for the intended
    /// yaw <see cref="_aimYaw"/>: by the geometry as it stands now, or, with <paramref name="keepGeometry"/>, by the geometry
    /// of the last choice. In a dribble (in possession, the ball not held at the feet, no touch still to be played first) each
    /// foot's earliest strike contact from <paramref name="earliestTick"/> on is checked: a chosen foot with no contact gives
    /// way to the other, and a choice made fresh (not with <paramref name="keepGeometry"/>) may take the dead-ahead swap (see
    /// <see cref="ShotModel.WithContact"/>). The touch controller then plans the strike on the chosen foot's swings only. With a touch still to be
    /// played first the feet's contacts are not known yet (the touch moves the ball): the foot is chosen without them and
    /// checked once more when the touch has been played (see <see cref="ChooseStrikeTimed"/>). Draws no random numbers.
    /// </summary>
    void ChooseStrike(PlayerState p, ShotType type, int earliestTick, bool keepGeometry = false)
    {
        int right = 0, left = 0;
        bool dribble = _controller == p && !_shotAtFoot, touchFirst = p.TouchAction is { Pending: true };
        _strikeUntimed = dribble && touchFirst;
        if (dribble && !touchFirst)
        {
            // As the controller will search it: never more than a tick before the strike estimate already published.
            int notBefore = _strikeEstMax >= 0 ? _strikeEstMax - 1 : -1;
            right = TouchPlanner.StrikeTickFor(p, Ball, Foot.Right, earliestTick, notBefore, Tick, SimTime, Tuning, Pitch);
            left = TouchPlanner.StrikeTickFor(p, Ball, Foot.Left, earliestTick, notBefore, Tick, SimTime, Tuning, Pitch);
        }
        // (a pass keeps the geometry rules; a finesse at goal takes the foot that curls it in)
        if (!keepGeometry) _strikeGeometry = ShotModel.ChooseByGeometry(p.Sheet, p.Position, p.Facing, Ball.Position, _aimYaw, type, Pitch, Tuning, curlIn: _passKind == PassKind.None);
        var choice = ShotModel.WithContact(_strikeGeometry, type, right, left, allowSwap: !keepGeometry, Tuning);
        _strikeFoot = choice.Foot;
        _strikeSurface = choice.Surface;
        p.StrikeFoot = choice.Foot;
        p.ShotFootLock = choice.Foot;
    }

    /// <summary>
    /// Tells the touch controller about the shot under way before it runs: its earliest strike tick (shoot let go this tick at
    /// the soonest) and whether it may commit the strike (released, in possession, the ball not held at the feet for a strike
    /// at the earliest tick).
    /// </summary>
    void ShotForController(PlayerState p)
    {
        bool shot = p.ShotAction is { Made: false } && _shotStartTick >= 0;
        p.ShotEarliestTick = shot ? EarliestStrikeTick(Tick) : -1;
        p.ShotNotBeforeTick = shot && _strikeEstMax >= 0 ? _strikeEstMax - 1 : -1;
        p.ShotCommitAllowed = shot && _pending.Active && !_shotAtFoot && _controller == p;
    }

    /// <summary>
    /// A foot chosen in a dribble without the feet's contact ticks is checked once more as soon as they are known (the touch
    /// committed before the press has been played, or a ball held at the feet got away), unless the strike is already
    /// committed: from its commit the foot is fixed. After the release the geometry decided there stands and the dead-ahead
    /// swap is no longer taken, so this can change the foot only when the chosen foot has no contact; while shoot is still
    /// held the choice is a forecast and is made afresh. This
    /// runs before the touch controller, which then forecasts the strike on the foot chosen here.
    /// </summary>
    void ChooseStrikeTimed(PlayerState p)
    {
        if (!_strikeUntimed || p.ShotAction is not { Made: false } shot || _shotStartTick < 0) return;
        if (_controller != p || p.StrikeContactTick >= 0) { _strikeUntimed = false; return; }
        if (_shotAtFoot || p.TouchAction is { Pending: true }) return;
        var type = _pending.Active ? _pending.Type : ShotModel.TypeFor(_latchFinesse, _latchChip, _latchDriven);
        _strikeSurface = ShotModel.SurfaceFor(type);
        ChooseStrike(p, type, EarliestStrikeTick(Tick), keepGeometry: _pending.Active);
        if (_pending.Active) { _pending.Foot = _strikeFoot; _pending.Surface = _strikeSurface; }
        shot.Foot = _strikeFoot;
        shot.Surface = _strikeSurface;
    }

    /// <summary>
    /// The shot action's contact tick as best known now, published from the press on, with the foot chosen for the shot: a shot
    /// without possession or with the ball held at the feet at the earliest strike tick; otherwise the committed strike, or the
    /// later of the earliest strike tick and the controller's forecast contact (the chosen foot's next eligible swing).
    /// </summary>
    void UpdateStrikeEstimate()
    {
        var a = Attacker;
        if (a.ShotAction is not { Made: false } shot || _shotStartTick < 0) return;
        int est = EarliestStrikeTick(Tick + 1);
        Foot foot = _strikeFoot;
        // Without the ball the strike falls at his meeting with it.
        if (_controller != a && _claims[0].Player == a && a.LockOnTick >= 0) est = _claims[0].Early ? a.LockOnTick : Math.Max(est, a.LockOnTick);
        if (_controller == a && !_shotAtFoot)
        {
            if (a.StrikeContactTick >= 0) est = a.StrikeContactTick;
            else if (a.TouchAction is not { Pending: true } && a.ForecastTick >= Tick) est = Math.Max(est, a.ForecastTick);
        }
        // Published, the estimate may move later (while the run cannot reach the ball) but never more than a tick earlier.
        if (a.StrikeContactTick < 0) est = Math.Max(est, _strikeEstMax - 1);
        _strikeEstMax = Math.Max(_strikeEstMax, est);
        // A shot turned into a fake winds up to the fake's contact (the strike it was planned for, or later for a late cancel).
        shot.ContactTick = _fakeTick >= 0 ? _fakeTick : est;
        shot.Foot = foot;
        a.StrikeFoot = foot;
    }

    /// <summary>
    /// The released shot, every tick until it is struck. Without possession (pressed without the ball, or the keeper took it
    /// away): struck from the earliest tick as soon as the ball is strikable, dropped after round.shoot_buffer. In possession it
    /// is not dropped while a contact is planned for it: with the ball held at the feet it is struck at the earliest tick if
    /// strikable then (otherwise it goes on as below); else it is struck at the controller's committed contact if the ball is
    /// strikable there and the chosen foot is the swinging foot in its contact window, and a contact missed is let go for that
    /// foot's next one; with no contact planned and he and the ball nearly still (under 0.5 m/s) for 2 s past the earliest
    /// contact, it is dropped.
    /// </summary>
    void TryStrike()
    {
        var a = Attacker;
        // Without the ball the shot is kept for as long as he holds his team's claim on it; when the claim ends without him
        // getting the ball, the buffer starts.
        if (_controller != a)
        {
            if (_claims[0].Player == a) _pending.DeadlineTick = int.MaxValue;
            else if (_pending.DeadlineTick == int.MaxValue) _pending.DeadlineTick = Math.Max(_pending.ContactTick, Tick) + Ticks(Tuning.Round.ShootBuffer);
        }
        // Tackled or stumbling he cannot strike; the shot is dropped after the buffer like any that lost the ball.
        if (Defending.Stumbling(a, SimTime) || SimTime < a.TackledLockUntil)
        {
            if (_controller != a && Tick >= _pending.DeadlineTick) DropShot();
            return;
        }
        if (_controller != a)
        {
            if (Tick < _pending.ContactTick) return;
            // A meeting his claim has planned for the strike (a pass played to him for a later tick): he lets the ball come to
            // it rather than strike the moment it is in reach, unless it is getting away.
            if (_claims[0].Player == a && _claims[0].StrikeReach && Tick < _claims[0].ArriveTick - 1
                && Vector3.Dot(Ball.Velocity - a.Velocity, a.Position - Ball.Position) > 0f) return;
            if (ShotModel.CanStrike(a, Ball, Tuning)) ExecuteStrike();
            else if (Tick >= _pending.DeadlineTick) DropShot();
            return;
        }
        if (_shotAtFoot)
        {
            if (Tick < _pending.ContactTick) return;
            if (ShotModel.CanStrike(a, Ball, Tuning)) { ExecuteStrike(); return; }
            // The ball got away from the feet: it is struck at the controller's next contact instead, on the foot chosen for
            // that (see ChooseStrikeTimed).
            _shotAtFoot = false;
            _strikeUntimed = true;
            return;
        }
        if (a.StrikeContactTick < 0)
        {
            // No contact planned for the strike and he and the ball both nearly still (the ball pinned against a board, or out of
            // his reach): left so for StuckSeconds, nothing will bring a contact, so the wind-up is dropped like one that lost the
            // ball and the round goes on (the clock stands still while a shot is under way).
            bool stuck = Tick >= _pending.ContactTick && a.Speed < StuckSpeed && Ball.Velocity.Length() < StuckSpeed;
            if (!stuck) _pending.UnplannedFrom = 0;
            else if (_pending.UnplannedFrom == 0) _pending.UnplannedFrom = Tick + 1;
            else if (Tick + 1 - _pending.UnplannedFrom >= Ticks(StuckSeconds)) { DropShot(); return; }
            // The published estimate runs away when no swing within reach of the ball can be found (each search starts after
            // the last one's answer): more than RunawaySeconds ahead, the strike will not come either. (Struck wind-ups have run at
            // most 2.8 s ahead.) This also bounds the search, which grows with how far ahead it starts.
            if (_strikeEstMax - Tick > Ticks(RunawaySeconds)) DropShot();
            return;
        }
        _pending.UnplannedFrom = 0;
        if (Tick < a.StrikeContactTick) return;
        Foot foot = a.StrikeContactFoot;
        a.StrikeContactTick = -1;
        if (!ShotModel.CanStrike(a, Ball, Tuning) || !TouchPlanner.FootInWindow(a, foot)) return;
        ExecuteStrike();
    }

    /// <summary>
    /// A wind-up in possession with no contact planned for it, the shooter and the ball both slower than StuckSpeed (m/s) for
    /// StuckSeconds, cannot be struck: it is dropped. (Wind-ups that are struck in the end have been still so for 1.5 s at most.)
    /// </summary>
    const float StuckSpeed = 0.5f, StuckSeconds = 2f;
    /// <summary>A wind-up in possession whose published strike estimate is more than this ahead of now is dropped, seconds.</summary>
    const float RunawaySeconds = 5f;

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
    /// Shoot pressed in Live: the shot action starts winding up. In possession with the ball at the feet (within foot reach, no
    /// touch committed) a soft touch keeps it there and the strike falls at the earliest strike tick; otherwise the strike is
    /// the touch controller's next contact on the chosen foot (a touch already committed is played first). The striking foot is
    /// chosen here for the aim the stick has now, so the strike is forecast on it from the press; the release chooses again
    /// for the aim it locks. A skill move under way ends here.
    /// </summary>
    void PressShoot(PlayerState p, bool inPossession)
    {
        var tuning = Tuning;
        _shotStartTick = Tick;
        _fakeTick = -1;
        _charge = 0f;
        _latchFinesse = _latchChip = _latchDriven = false;
        SkillMoves.Abort(p);
        TouchPlanner.UpdatePhases(p, Tick, tuning);
        _shotAtFoot = inPossession && p.TouchAction is not { Pending: true } && TouchModel.InReach(p, Ball, tuning);
        _softPending = _shotAtFoot;
        p.StrikeContactTick = -1;
        _strikeEstMax = -1;
        var shot = new BallAction
        {
            Kind = ActionKind.Power,
            Phase = ActionPhase.Windup,
            PlanTick = Tick,
            ContactPoint = new Vector3(Ball.Position.X, Ball.Radius, Ball.Position.Z),
        };
        p.ShotAction = shot;
        _strikeSurface = FootSurface.Laces;
        _aimYaw = AimYawFor(p, AimStick(_lastStick), ShotType.Power);
        int earliest = EarliestStrikeTick(Tick);
        ChooseStrike(p, ShotType.Power, earliest);
        shot.Foot = _strikeFoot;
        shot.Surface = _strikeSurface;
        shot.AimYaw = _aimYaw;
        shot.ContactTick = earliest;
    }

    /// <summary>
    /// Shoot released: the charge, type and aim are fixed, and the earliest contact is set: the wind-up after the press, longer
    /// by what the body angle to the aim adds, and the lead after the release. The striking foot and surface are chosen for
    /// that aim and fixed: the strike waits for that foot. The round goes to Strike until the strike.
    /// </summary>
    void ReleaseShoot(PlayerState p, in TickIntent it)
    {
        var k = Tuning.Shot;
        var type = ShotModel.TypeFor(_latchFinesse, _latchChip, _latchDriven);
        Vector2 stick = AimStick(it.Move);
        _aimYaw = AimYawFor(p, stick, type);
        int contact = EarliestStrikeTick(Tick);
        Foot before = _strikeFoot;
        _strikeSurface = ShotModel.SurfaceFor(type);
        ChooseStrike(p, type, contact);
        // The aim locked asks for the other foot: the forecast made on the first one is let go (the estimate published stays
        // the earliest the strike may come).
        if (_strikeFoot != before) p.ClearForecast();
        _pending = new PendingShot
        {
            Active = true,
            Charge = _charge,
            Stick = stick,
            Type = type,
            Foot = _strikeFoot,
            Surface = _strikeSurface,
            AimYaw = _aimYaw,
            ContactTick = contact,
            DeadlineTick = _controller == p ? int.MaxValue : contact + Ticks(Tuning.Round.ShootBuffer),
        };
        _charge = 0f;
        _latchFinesse = _latchChip = _latchDriven = false;
        if (p.ShotAction is { } shot)
        {
            shot.Kind = ShotModel.KindFor(type);
            shot.Foot = _strikeFoot;
            shot.Surface = _strikeSurface;
            shot.AimYaw = _aimYaw;
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
    /// low shot can have any power. Shoulder buttons held on the second press count as if held through the first. A press that
    /// takes the shot into or out of the finesse family (finesse held on it after a power shot, chip after a finesse) changes
    /// the foot rule that applies (see <see cref="ShotModel.ChooseCurlIn"/>): the foot and surface are chosen again for the
    /// new type and the aim locked at the release, unless the strike is already committed.
    /// </summary>
    void PressShootAgain(PlayerState p, in TickIntent it)
    {
        var t = _pending.Type;
        bool finesse = t is ShotType.Finesse or ShotType.LowFinesse || it.Finesse;
        bool chip = t == ShotType.Chip || it.Chip;
        _pending.Type = ShotModel.TypeFor(finesse, chip, driven: true);
        bool family = _pending.Type is ShotType.Finesse or ShotType.LowFinesse;
        if (family != (t is ShotType.Finesse or ShotType.LowFinesse) && p.StrikeContactTick < 0)
        {
            Foot before = _strikeFoot;
            _strikeSurface = ShotModel.SurfaceFor(_pending.Type);
            ChooseStrike(p, _pending.Type, EarliestStrikeTick(Tick));
            if (_strikeFoot != before) p.ClearForecast();
            _pending.Foot = _strikeFoot;
            _pending.Surface = _strikeSurface;
        }
        else
        {
            _pending.Surface = SurfaceNow(_pending.Type);
            _strikeSurface = _pending.Surface;
        }
        if (p.ShotAction is { } shot)
        {
            shot.Kind = ShotModel.KindFor(_pending.Type);
            shot.Foot = _pending.Foot;
            shot.Surface = _pending.Surface;
        }
    }

    /// <summary>Lets a player touch the ball and raises the Touch event; a first contact with a loose ball gains possession.</summary>
    void TryTouch(PlayerState p, in TickIntent it, TouchStepMode mode)
    {
        bool inPossession = _controller == p;
        // A first touch of a team-mate's pass pays for the ball's speed against him.
        p.ReceivingPass = !inPossession && _pass is { } rec && rec.Passer.Team == p.Team && rec.Passer != p;
        float against = TouchPlanner.RelativeBallSpeed(p, Ball);
        if (TouchPlanner.Step(p, it, Ball, inPossession, mode, Tick, SimTime, Tuning, Pitch, _rng, Teams[1 - p.Team]) is not TouchResult touch) { p.ReceivingPass = false; return; }
        if (p.ReceivingPass)
        {
            var k = Tuning.Receive;
            bool queued = p.ShotAction is { Made: false } || p.SkillBufTick >= 0;
            p.ReceiveCooldownUntil = SimTime + (queued ? k.QueuedCooldown : MathF.Max(0f, k.Cooldown.Evaluate(against)) * k.Rating.ForRating(p.Sheet.FirstTouch));
            _received++;
        }
        p.ReceivingPass = false;
        _pass = null;
        if (OffsideTouch(p)) return;
        if (touch.Kind == ActionKind.Soft) _softPending = false;
        _events.Add(new SimEvent(EventKind.Touch, touch.ContactPoint, touch.BallVelocity.Length(), Foot: touch.Foot, Team: p.Team, PlayerIndex: p.Index));
        if (!inPossession) SetController(p);
        _awayFor = 0f;
        p.Def.AutoCooldownUntil = SimTime + Tuning.Defending.Auto.CooldownS;   // (no auto tackle just after he has played the ball himself)
    }

    /// <summary>Changes who controls the ball, raising PossessionLost for the old controller and PossessionGained for the new.</summary>
    void SetController(PlayerState? p)
    {
        if (_controller == p) return;
        float speed = Ball.Velocity.Length();
        if (_controller != null)
        {
            // A shot under way that loses the ball (the keeper took it, or it ran away) is dropped after the buffer.
            if (_controller == Attacker && _pending.Active) _pending.DeadlineTick = Math.Max(_pending.ContactTick, Tick) + Ticks(Tuning.Round.ShootBuffer);
            _controller.InPossession = false;
            TouchPlanner.OnPossessionLost(_controller);   // (his turn fatigue goes with the ball)
            _events.Add(new SimEvent(EventKind.PossessionLost, Ball.Position, speed, Team: _controller.Team, PlayerIndex: _controller.Index));
        }
        _controller = p;
        _awayFor = 0f;
        _looseTie = null;
        if (p != null)
        {
            _ballPlayed = true;
            p.InPossession = true;
            p.TurnFatigue = 0f;
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
        UpdateStrikeEstimate();
        // (a team-mate's kick, struck before control left him, runs out its recovery)
        if (HasMates)
            foreach (var m in Teams[0])
                if (m != a && m.ShotAction is { } old)
                {
                    old.Phase = old.PhaseAt(Tick, 0, 0);
                    if (old.Phase == ActionPhase.Done || !old.Made) m.ShotAction = null;
                }
        if (a.ShotAction is { } shot)
        {
            shot.Phase = shot.PhaseAt(Tick, 0, 0);
            if (shot.Phase == ActionPhase.Done) a.ShotAction = null;
            else if (!shot.Made)
            {
                a.StrikeEta = MathF.Max(0f, (shot.ContactTick - Tick) * _dt);
                // The latest prediction of where the ball is met: the ball rolled on to the contact tick.
                float eta = MathF.Max(0f, (shot.ContactTick - Tick) * _dt);
                var at = Ball.Position + new Vector3(Ball.Velocity.X, 0f, Ball.Velocity.Z) * eta;
                shot.ContactPoint = new Vector3(at.X, Ball.Radius, at.Z);
            }
        }
        // A fake shot read and waiting for its contact: the move it will be, for the view to draw from the cancel on.
        // So is a gesture held back for a touch the controller has committed: the move it will start after that touch.
        a.PendingSkill = _fakeTick > Tick && a.ShotAction is { Made: false } && a.SkillAction == null
            ? SkillMoves.PendingFake(a, _fake, _shotStartTick, _fakeTick, Tick, Ball, Tuning)
            : State == RoundState.Live && a.ShotAction == null ? SkillMoves.PendingHeld(a, Ball, Tick, SimTime, Tuning) : null;
        a.SkillExitTouch = State == RoundState.Live && _controller == a ? SkillMoves.ExitForecast(a, _lastIntent, Ball, Tick, Tuning) : null;
        IdealCharge = ShotModel.IdealCharge(ShotModel.GoalDistance(Ball.Position, Pitch), Tuning);
        bool claims = _controller == null && State is RoundState.Live or RoundState.Strike && _claims[0].Player == a;
        if (State == RoundState.Live && _controller == a || claims)
        {
            var f = Possession.PredictNextTouch(a, Tick, _controller == a, Tuning);
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
        _shotAtFoot = false;
        EndShotAction();
        if (State == RoundState.Strike) { _pending = default; State = RoundState.Live; }
    }

    /// <summary>The released shot never found the ball in reach: no strike, no report, and play goes on.</summary>
    void DropShot()
    {
        _pending = default;
        _passKind = PassKind.None; PassCharge = 0f; PassReceiver = null;
        _softPending = false;
        _shotAtFoot = false;
        EndShotAction();
        State = RoundState.Live;
    }

    /// <summary>A shot action not struck (faked, dropped, or the round ended) is done at once.</summary>
    void EndShotAction()
    {
        TouchPlanner.OnShotEnded(Attacker);
        if (Attacker.ShotAction is { Made: false } s) { s.Phase = ActionPhase.Done; Attacker.ShotAction = null; }
    }

    /// <summary>
    /// The strike. Struck without his controlling the ball first (a shot queued on the way to a loose ball) it is a first-time
    /// strike: the spreads grow with the ball's ground speed against him (shot.first_time_error). <paramref name="flick"/>: struck
    /// before its wind-up was done (see <see cref="Flick"/>).
    /// </summary>
    void ExecuteStrike(bool flick = false)
    {
        // (a player who was beyond the line at the last strike may not play the ball: his first-time strike is the offence)
        if (OffsideTouch(Attacker)) return;
        if (_pending.Pass != PassKind.None) { ExecutePass(flick); return; }
        bool firstTime = _controller != Attacker;
        float against = TouchPlanner.RelativeBallSpeed(Attacker, Ball);
        var ctx = new ShotContext
        {
            FirstTime = firstTime,
            Flick = flick,
            FirstTimeError = firstTime ? MathF.Max(0f, Tuning.Shot.FirstTimeError.Evaluate(against)) : 1f,
            Shooter = Attacker,
            Keeper = Keeper,
            BallPosition = Ball.Position,
            Stick = _pending.Stick,
            Charge = _pending.Charge,
            Type = _pending.Type,
            Foot = _pending.Foot,
            Surface = _pending.Surface,
            AimAssist = AimAssist,
        };
        var result = ShotModel.Compute(ctx, Pitch, Tuning, _rng, Tick);
        var a = Attacker;
        if (a.ShotAction is { } shot)
        {
            shot.Kind = ShotModel.KindFor(result.Type);
            shot.Surface = result.Surface;
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
        _shotAtFoot = false;
        TouchPlanner.OnShotEnded(a);
        a.TurnFatigue = 0f;
        Ball.Velocity = result.Launch;
        Ball.Spin = result.Spin;
        Attacker.LastTouchTime = SimTime;
        Attacker.LastTouchFoot = result.Foot;
        Attacker.StrikeFoot = result.Foot;
        _shots.Add(result);
        NoteStrike();
        StartReaction();
        { bool none = false; OffsideAtStrike(Attacker, null, ref none); } // (a shot's rebound cannot be tapped in by a player who was beyond the line)
        _lastShot = result;
        _pending = default;
        _ballPlayed = true;
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
        NoteOutcome(outcome);
        Outcome = outcome;
        State = RoundState.Outcome;
        _outcomeLeft = Tuning.Round.OutcomeSeconds;
        _endTick = Tick;
        _endClock = Clock;
        ShotCharge = 0f;
        _pending = default;
        _passKind = PassKind.None; PassCharge = 0f; PassReceiver = null; _pass = null;
        _softPending = false;
        EndShotAction();
        if (outcome != Outcome.Held) SetController(null); // the ball is dead; a held ball stays the keeper's
    }

    void StepOutcome()
    {
        var idle = default(TickIntent);
        Movement.Step(Attacker, idle, _dt, SimTime, false, Tuning, Pitch);
        if (HasMates)
        {
            foreach (var m in Teams[0])
                if (m != Attacker) Movement.Step(m, idle, _dt, SimTime, false, Tuning, Pitch);
            SeparateMates();
        }
        if (Defender != null) StepDefenderOutcome();
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
            Keeper = FinishKeeperMeasures(_brain.Report),
            Defender = FinishDefenderReport(),
            FirstAttack = FinishFirstAttack(),
            Team = HasMates ? new TeamReport { Passes = _passes, Received = _received, Offside = Outcome == Outcome.Offside, ControlChanges = _controlChanges } : null,
        };
        State = RoundState.Report;
    }
}
