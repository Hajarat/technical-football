using System.Numerics;

namespace FootballSim.Core;

/// <summary>
/// A scripted attacker for batches and tests. It produces <see cref="TickIntent"/> values through the same input path a gamepad
/// would use. If the scenario asks for a shooting distance it runs onto the ball and, once in possession, simply points the
/// stick at the goal (the possession assist keeps the feet on the ball). Then it holds shoot for its charge and releases with
/// the stick pointing 0.6 m inside a post. After the shot it stands still.
/// <para>
/// Its charge models a human judging the power: the ideal charge for the ball's distance at the press plus a normal error
/// (<see cref="ChargeNoise"/>), drawn from its own generator seeded from the round's seed, so the world's draws are untouched
/// and a replay of the round (which records only intents) plays out the same. <see cref="Charge"/> fixes it instead.
/// </para>
/// <para>
/// Against the keeper (<see cref="Counters"/>) it reads only what a player sees: the keeper's position and whether he is running
/// out. With the keeper near the ball it shortens its touches: jogging touches within <see cref="JogRange"/>, close control
/// within <see cref="CloseControlRange"/>. A keeper committing to it close (rushing or charging out,
/// within <see cref="RoundRange"/>) it tries to take round, pushing the ball <see cref="RoundAngleDeg"/> off the line to goal on
/// the side away from him, and holds its shot until it is past him. A keeper standing further off his line than he can
/// recover from a chip (<see cref="Keeper.ChipSafeDepth"/>, plus <see cref="ChipMargin"/>) it chips, with a chance that grows
/// with its composure, shooting there and then. Those choices draw from a second generator of its own, so the charge draws do
/// not move.
/// </para>
/// <para>
/// Against a defender it plays one of three styles (<see cref="Style"/>; Default ignores him): Straight runs on at goal through
/// him; Cut, once he is in front within <see cref="CutDistance"/> of the ball, turns <see cref="CutAngleDeg"/> off the line to
/// goal to one side until he is past or left behind; Skill, once he is within <see cref="SkillDistance"/>, plays a step-over to
/// one side and leaves on that side the same way. The side is the one further from him, a coin from the tactics generator when
/// he is square on. Shoot runs at him and shoots as soon as he is in front within <see cref="ShootGap"/> of the ball, through
/// him, whatever the distance to goal.
/// </para>
/// </summary>
public sealed class ScriptedAttacker
{
    /// <summary>How the scripted attacker takes a defender on.</summary>
    public enum AttackStyle { Default, Straight, Cut, Skill, Shoot }

    /// <summary>The style against a defender; Default ignores him (and in a scenario without one every style does).</summary>
    public AttackStyle Style { get; set; }
    /// <summary>Cut: the turn starts with the defender in front within this of the ball, metres ...</summary>
    public float CutDistance { get; set; } = 4f;
    /// <summary>... this far off the line to the goal centre, degrees.</summary>
    public float CutAngleDeg { get; set; } = 50f;
    /// <summary>Skill: the move is played with the defender in front within this of the ball, metres.</summary>
    public float SkillDistance { get; set; } = 3f;
    /// <summary>Shoot: the shot is taken with the defender in front within this of the ball, metres.</summary>
    public float ShootGap { get; set; } = 6f;
    /// <summary>Skill: the move asked for.</summary>
    public SkillKey SkillMove { get; set; } = SkillKey.StepOver;
    /// <summary>Going round the defender, close control is held (short touches) while he is within this of the ball, metres (0: never).</summary>
    public float StyleCloseRange { get; set; }

    /// <summary>What the scripted attacker does with a team-mate: nothing, or one pass and then play on as the receiver.</summary>
    public enum TeamStyle { Solo, Square, Through }

    /// <summary>
    /// The style with a team-mate (a scenario that has one). Solo never passes. Square plays one ground pass to him once the
    /// defender is in front within <see cref="PassDistance"/> of the ball (or the ball is within two metres of the shooting
    /// distance). Through holds the ball up short of the defender and plays one through ball as the team-mate's run reaches
    /// the offside line (or when the defender gets too close to wait). After the pass the script plays on as the
    /// receiver, as it plays alone.
    /// </summary>
    public TeamStyle Team { get; set; }
    /// <summary>Square: the pass is played with the defender in front within this of the ball, metres.</summary>
    public float PassDistance { get; set; } = 8f;
    /// <summary>Through: the pass is pressed with the team-mate's armed run within this of the line, metres.</summary>
    public float ThroughWithin { get; set; } = 2f;
    /// <summary>Through: waiting for the run he stops with the defender in front within this of the ball, and passes anyway once he is within <see cref="ReleaseDistance"/>, metres.</summary>
    public float HoldUpDistance { get; set; } = 8f;
    public float ReleaseDistance { get; set; } = 6f;
    /// <summary>Through: he passes only with the team-mate at least this far onside, metres.</summary>
    public float OnsideRoom { get; set; } = 1f;
    /// <summary>The pass button is held this many ticks (a tap: the pass points just beyond the nearest man on the stick).</summary>
    public int PassHoldTicks { get; set; } = 6;

    bool _passed;                    // the style's one pass has been struck (or given up)
    int _passHeld;                   // ticks the pass button has been held, 0 before the press
    int _controlled = -1;            // the player the script drove last tick

    int _styleSide;                  // going round the defender: +1 to +Z, -1 to -Z, 0 not
    bool _skillPlayed;               // the skill move of the current approach has been asked for
    bool _skillNow;                  // ... on this tick
    bool _shootThrough;              // Shoot: the defender is in front and close enough: shoot now

    /// <summary>A fixed charge held before releasing, 0..1; null judges it like a human (see the class remarks).</summary>
    public float? Charge { get; set; }
    /// <summary>Standard deviation of the human charge error around the ideal charge.</summary>
    public float ChargeNoise { get; set; } = 0.12f;
    /// <summary>Lowest and highest charge the human model releases at.</summary>
    public float MinCharge { get; set; } = 0.05f;
    public float MaxCharge { get; set; } = 1f;
    /// <summary>How far inside the post to aim, metres.</summary>
    public float AimInset { get; set; } = 0.6f;
    /// <summary>+1 aims at the +Z post, -1 at the -Z post, 0 picks the far post (the +Z post from a central ball).</summary>
    public int Side { get; set; }
    public ShotType Type { get; set; } = ShotType.Power;
    /// <summary>Sprint input while running onto the ball, 0..1.</summary>
    public float RunSprint { get; set; } = 1f;
    /// <summary>How far beyond a loose ball the run onto it is aimed, metres.</summary>
    public float LookAhead { get; set; } = 0.6f;
    /// <summary>Hold close control while running onto the ball.</summary>
    public bool RunCloseControl { get; set; }
    /// <summary>In possession, shoot is pressed once the predicted next touch is at least this long before the contact, seconds.</summary>
    public float PressMargin { get; set; } = 0.1f;
    /// <summary>In possession, shoot is pressed regardless once the ball is this much closer than the shooting distance, metres.</summary>
    public float LateDistance { get; set; } = 3f;
    /// <summary>Reacts to the keeper (close control, rounding him, chipping him); off, it only runs and shoots.</summary>
    public bool Counters { get; set; } = true;
    /// <summary>
    /// Sprint is released (jogging touches) while the keeper is within this distance of the ball, about how far a sprint touch
    /// runs before it is met again, metres (the feel probe's sprint touch interval, 0.48 s, at 7.4 m/s; inside the close-control
    /// range, so the scripted attacker now sprints until it takes close control)...
    /// </summary>
    public float JogRange { get; set; } = 4f;
    /// <summary>...and close control is held within this distance, metres.</summary>
    public float CloseControlRange { get; set; } = 6f;
    /// <summary>A keeper rushing out within this distance of the ball is taken round, metres.</summary>
    public float RoundRange { get; set; } = 5f;
    /// <summary>Taking the keeper round, the run is this far off the line to the goal centre, degrees.</summary>
    public float RoundAngleDeg { get; set; } = 60f;
    /// <summary>A chip is considered once the keeper is this much further off his line than his chip-safe depth, metres...</summary>
    public float ChipMargin { get; set; } = 0.5f;
    /// <summary>...with the ball within this distance of the goal centre, metres...</summary>
    public float ChipMaxDistance { get; set; } = 25f;
    /// <summary>...and is played with this chance at composure 1, rising to <see cref="ChipChance99"/> at 99.</summary>
    public float ChipChance1 { get; set; } = 0.2f;
    public float ChipChance99 { get; set; } = 0.8f;

    bool _released;
    float _heldFor;
    int _releaseTick;
    float _pressCharge = float.NaN;  // the charge chosen at the press of the current attempt
    SimRng? _rng;
    SimRng? _tacticRng;
    bool _chipThis;                  // the current attempt is a chip
    bool _chipRolled, _chipWanted;   // the chip chance was rolled for the current exposure of the keeper, and its result
    int _roundSide;                  // taking the keeper round: +1 to +Z, -1 to -Z, 0 not

    /// <summary>The shot type of the current attempt.</summary>
    ShotType AttemptType => _chipThis ? ShotType.Chip : Type;

    /// <summary>True once the shoot button has been released.</summary>
    public bool Fired => _released;

    /// <summary>The charge this attempt releases at, chosen at the press from the ball's distance then.</summary>
    float ChooseCharge(World w)
    {
        if (Charge is float fixedCharge) return fixedCharge;
        _rng ??= new SimRng(w.Seed ^ 0x5C8197ED1A77AC4EUL);
        float ideal = ShotModel.IdealCharge(ShotModel.GoalDistance(w.Ball.Position, w.Pitch), w.Tuning);
        return Math.Clamp(ideal + _rng.Normal(0f, ChargeNoise), MinCharge, MaxCharge);
    }

    /// <summary>The stick direction that points from the ball to 0.6 m inside the chosen post.</summary>
    public Vector2 AimStick(World w)
    {
        int side = Side != 0 ? Side : (w.Ball.Position.Z > 0.5f ? -1 : 1);
        float z = side * (w.Pitch.GoalHalfWidth - AimInset);
        var d = new Vector2(w.Pitch.GoalLineX - w.Ball.Position.X, z - w.Ball.Position.Z);
        return d.LengthSquared() < 1e-6f ? new Vector2(1f, 0f) : Vector2.Normalize(d);
    }

    /// <summary>
    /// In possession: stick at the goal centre (or, taking the keeper round, off to the side). Otherwise: stick toward a point
    /// just beyond the ball, toward the goal, to run onto it.
    /// </summary>
    void RunOntoBall(World w, ref TickIntent it)
    {
        var a = w.Attacker;
        var ball = w.Ball.Position;
        var feet = new Vector2(a.Position.X, a.Position.Z);
        Vector2 d;
        if (w.BallController == a && _roundSide != 0)
        {
            var g = new Vector2(w.Pitch.GoalLineX, 0f) - feet;
            float ang = MathF.Atan2(g.Y, g.X) + _roundSide * RoundAngleDeg * MathF.PI / 180f;
            d = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
        }
        else if (w.BallController == a && _styleSide != 0)
        {
            var g = new Vector2(w.Pitch.GoalLineX, 0f) - feet;
            float ang = MathF.Atan2(g.Y, g.X) + _styleSide * CutAngleDeg * MathF.PI / 180f;
            d = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
        }
        else if (w.BallController == a)
            d = new Vector2(w.Pitch.GoalLineX, 0f) - feet;
        else
        {
            var goal = new Vector2(w.Pitch.GoalLineX - ball.X, -ball.Z);
            goal = goal.LengthSquared() > 1e-6f ? Vector2.Normalize(goal) : new Vector2(1f, 0f);
            d = new Vector2(ball.X, ball.Z) + goal * LookAhead - feet;
        }
        d = d.LengthSquared() > 1e-6f ? Vector2.Normalize(d) : new Vector2(1f, 0f);
        it.MoveX = d.X; it.MoveZ = d.Y; it.Sprint = RunSprint; it.CloseControl = RunCloseControl;
        if (_skillNow) { it.SkillKey = SkillMove; _skillNow = false; }
        if (_styleSide != 0 && w.Defender is { } def && StyleCloseRange > 0f
            && Vector2.Distance(new Vector2(def.Position.X, def.Position.Z), new Vector2(ball.X, ball.Z)) <= StyleCloseRange)
        { it.CloseControl = true; it.Sprint = 0f; }
        if (Counters && w.BallController == a && _roundSide == 0)
        {
            float kd = KeeperToBall(w);
            if (kd <= JogRange) it.Sprint = 0f;
            if (kd <= CloseControlRange) it.CloseControl = true;
        }
    }

    static float KeeperToBall(World w) =>
        Vector2.Distance(new Vector2(w.Keeper.Position.X, w.Keeper.Position.Z), new Vector2(w.Ball.Position.X, w.Ball.Position.Z));

    /// <summary>True while the keeper stands between the ball and the goal: closer to the goal line, near the line to goal.</summary>
    static bool KeeperInFront(World w)
    {
        var b = new Vector2(w.Ball.Position.X, w.Ball.Position.Z);
        var k = new Vector2(w.Keeper.Position.X, w.Keeper.Position.Z);
        var g = new Vector2(w.Pitch.GoalLineX, 0f) - b;
        float len = g.Length();
        if (len < 1e-3f) return false;
        var u = g / len;
        var o = k - b;
        float along = Vector2.Dot(o, u);
        float across = MathF.Abs(o.X * u.Y - o.Y * u.X);
        return along > 0.3f && across < 2.5f;
    }

    /// <summary>
    /// The counters, before the shot decision: start or stop taking the keeper round, and whether the keeper is off his line
    /// beyond what is safe against a chip (rolled once per exposure). Returns true to chip now.
    /// </summary>
    bool ReadKeeper(World w)
    {
        var a = w.Attacker;
        bool mine = w.BallController == a;
        var kp = w.Keeper;
        bool rushing = kp.KeeperAction is KeeperAction.Rush or KeeperAction.Smother or KeeperAction.Slide or KeeperAction.Charge;
        if (_roundSide == 0 && mine && rushing && KeeperToBall(w) <= RoundRange && KeeperInFront(w))
        {
            float dz = w.Ball.Position.Z - kp.Position.Z;
            _roundSide = MathF.Abs(dz) > 0.2f ? MathF.Sign(dz) : (Tactic(w).NextFloat() < 0.5f ? -1 : 1);
        }
        else if (_roundSide != 0 && (!KeeperInFront(w) || KeeperToBall(w) > RoundRange + 3f)) _roundSide = 0;

        float dist = ShotModel.GoalDistance(w.Ball.Position, w.Pitch);
        float depth = w.Pitch.GoalLineX - kp.Position.X;
        bool exposed = mine && dist <= ChipMaxDistance && KeeperInFront(w) && kp.KeeperAction is not (KeeperAction.Diving or KeeperAction.Smother or KeeperAction.Slide)
                       && depth > Keeper.ChipSafeDepth(kp.Sheet, a.Sheet, w.Tuning, w.Pitch, dist) + ChipMargin;
        if (!exposed) { _chipRolled = false; _chipWanted = false; return false; }
        if (!_chipRolled)
        {
            _chipRolled = true;
            float chance = ChipChance1 + (ChipChance99 - ChipChance1) * Rating.R01(a.Sheet.Composure);
            _chipWanted = Tactic(w).NextFloat() < chance;
        }
        return _chipWanted;
    }

    /// <summary>
    /// The style against a defender, before the run: starts going round him (and, for Skill, asks for the move) once he is in
    /// front and close enough, and stops once he is no longer in front of the ball or is left well behind.
    /// </summary>
    void ReadDefender(World w)
    {
        if (Style is AttackStyle.Default or AttackStyle.Straight || w.Defender is not { } def) return;
        var a = w.Attacker;
        var b = new Vector2(w.Ball.Position.X, w.Ball.Position.Z);
        var dp = new Vector2(def.Position.X, def.Position.Z);
        var goal = new Vector2(w.Pitch.GoalLineX, 0f);
        var toGoal = goal - b;
        float len = toGoal.Length();
        if (len < 1e-3f) return;
        var u = toGoal / len;
        var o = dp - b;
        float along = Vector2.Dot(o, u), across = o.X * u.Y - o.Y * u.X;
        bool inFront = along > 0.2f && MathF.Abs(across) < 3f;
        float dist = o.Length();
        if (Style == AttackStyle.Shoot)
        {
            if (w.BallController == a && inFront && dist <= ShootGap) _shootThrough = true;
            return;
        }
        float trigger = Style == AttackStyle.Cut ? CutDistance : SkillDistance;
        if (_styleSide == 0 && w.BallController == a && inFront && dist <= trigger)
        {
            // (across > 0: he is on the -Z side of the line to goal, so the open side is +Z)
            _styleSide = MathF.Abs(across) > 0.15f ? (across > 0f ? 1 : -1) : (Tactic(w).NextFloat() < 0.5f ? -1 : 1);
            if (Style == AttackStyle.Skill && !_skillPlayed) { _skillPlayed = true; _skillNow = true; }
        }
        else if (_styleSide != 0 && (!inFront || dist > trigger + 3f)) { _styleSide = 0; _skillPlayed = false; }
    }

    /// <summary>The team style's pass: true while the script is pressing, holding or waiting on it (<paramref name="it"/> is then the intent).</summary>
    bool PassNow(World w, out TickIntent it)
    {
        it = default;
        var a = w.Attacker;
        PlayerState? mate = null;
        foreach (var m in w.Teams[0]) if (m != a && m.Role == PlayerRole.Outfield) { mate = m; break; }
        if (mate == null || w.State != RoundState.Live) return false;
        if (w.Passes > 0) { _passed = true; return false; }
        var ball = new Vector2(w.Ball.Position.X, w.Ball.Position.Z);
        var mp = new Vector2(mate.Position.X, mate.Position.Z);
        bool through = Team == TeamStyle.Through;
        // The stick: at him for a ground pass; for a through ball up the pitch from him, into his run.
        var aim = mp + (through ? new Vector2(4f, 0f) : Vector2.Zero) - ball;
        aim = aim.LengthSquared() > 1e-6f ? Vector2.Normalize(aim) : new Vector2(1f, 0f);
        if (_passHeld == 0)
        {
            if (w.BallController != a) return false;
            float toGoal = Vector2.Distance(ball, new Vector2(w.Pitch.GoalLineX, 0f));
            float toDefender = w.Defender is { } d && d.Position.X > ball.X ? Vector2.Distance(new Vector2(d.Position.X, d.Position.Z), ball) : float.PositiveInfinity;
            bool now;
            if (through)
            {
                // (onside with room to spare: the line may come back at him while the pass winds up)
                bool onside = !w.OffsideOn || w.OffsideLine - mate.Position.X >= OnsideRoom;
                bool run = mate.MateRun == MateRun.Go || mate.MateRun == MateRun.Armed && w.OffsideLine - mate.Position.X <= ThroughWithin + OnsideRoom;
                // (with the defender on top of him he cannot wait for the run any longer)
                now = onside && (run || toDefender <= ReleaseDistance);
                // Waiting for the run with the defender ahead, he holds the ball up instead of running on into him.
                if (!now && toDefender <= HoldUpDistance) return true;
            }
            else now = toDefender <= PassDistance || w.Scenario.ShootDistance > 0f && toGoal <= w.Scenario.ShootDistance + 2f;
            if (!now) return false;
        }
        // Pressed: held for the tap, then the stick stays on him until the strike (the receiver is chosen again every tick).
        _passHeld++;
        bool held = _passHeld <= PassHoldTicks;
        it = new TickIntent { MoveX = aim.X, MoveZ = aim.Y, PassHeld = held && !through, ThroughHeld = held && through };
        // (given up if nothing was struck in a second and a half: the ball was lost on the way)
        if (_passHeld > 180) { _passed = true; return false; }
        return true;
    }

    SimRng Tactic(World w) => _tacticRng ??= new SimRng(w.Seed ^ 0x3A91C7E25B0D4F68UL);

    /// <summary>The intent for the attacker this tick.</summary>
    public TickIntent Next(World w)
    {
        // Control has moved to another player (a pass, a loose ball): the attempt starts afresh for him.
        if (_controlled >= 0 && w.ControlledIndex != _controlled)
        {
            _released = false; _heldFor = 0f; _pressCharge = float.NaN; _styleSide = 0; _roundSide = 0; _skillPlayed = false; _shootThrough = false;
            _chipRolled = false;
        }
        _controlled = w.ControlledIndex;
        if (Team != TeamStyle.Solo && w.HasMates && !_passed && !_released && _heldFor == 0f && PassNow(w, out var pass)) return pass;
        if (!_released && _heldFor == 0f) ReadDefender(w);
        bool chipNow = !_released && _heldFor == 0f && Counters && ReadKeeper(w);
        if (_heldFor == 0f && !_released) _chipThis = chipNow;
        var type = AttemptType;
        var it = new TickIntent
        {
            Finesse = type is ShotType.Finesse or ShotType.LowFinesse,
            Chip = type == ShotType.Chip,
            Driven = type is ShotType.Driven or ShotType.LowFinesse,
        };
        if (_released)
        {
            // If the release was dropped (ball out of reach, nothing struck), run onto the ball and try again.
            if (w.State == RoundState.Live && w.Shots.Count == 0 && w.Tick - _releaseTick > 50)
            {
                _released = false;
                _heldFor = 0f;
                _pressCharge = float.NaN;
            }
            else return it;
        }

        var a = w.Attacker;
        var ball = w.Ball.Position;
        float toGoal = new Vector2(w.Pitch.GoalLineX - ball.X, ball.Z).Length();
        var k = w.Tuning.Shot;
        // Ready to press: the ball is strikable now, or (in possession) the next touch, which the strike takes the place of once shoot
        // is held, comes early enough in the wind-up for the ball to be strikable at the contact.
        bool strikable = ShotModel.CanStrike(a, w.Ball, w.Tuning);
        bool running = w.Scenario.ShootDistance > 0f;
        float charge = float.IsNaN(_pressCharge) ? (Charge ?? 0f) : _pressCharge;
        float toContact = MathF.Max(k.WindupSeconds, charge * k.ChargeSeconds + k.ReleaseLeadSeconds);
        bool close = strikable
            || w.BallController == a && a.NextTouchEta <= toContact - PressMargin
            || w.BallController == a && running && toGoal <= w.Scenario.ShootDistance - LateDistance;
        // Taking the keeper round, the shot waits until he is beaten; a chip is played as soon as the ball can be struck.
        // With an intended strike distance it presses once the ball, carried on at his speed through the time to the contact,
        // is that close at the strike.
        float pressAt = w.Scenario.StrikeDistance > 0f ? w.Scenario.StrikeDistance + a.Speed * toContact : w.Scenario.ShootDistance;
        bool hold = _heldFor == 0f && (_roundSide != 0 || _styleSide != 0 || !close || running && toGoal > pressAt);
        if (_chipThis && (strikable || close)) hold = false;
        if (_shootThrough && (strikable || close)) hold = false;
        if (hold)
        {
            RunOntoBall(w, ref it);
            return it;
        }

        if (_heldFor == 0f) _pressCharge = ChooseCharge(w);
        _heldFor += w.Tuning.Dt;
        // Hold until the charge reached is the chosen one (the world adds one charge step per held tick).
        if (_heldFor < MathF.Max(_pressCharge * k.ChargeSeconds, 1.5f * w.Tuning.Dt) - 1e-4f)
        {
            it.ShootHeld = true;
            if (running) RunOntoBall(w, ref it); // a shot on the run keeps running while the power builds
            return it;
        }
        var stick = AimStick(w);
        it.MoveX = stick.X; it.MoveZ = stick.Y;
        _released = true;
        _releaseTick = w.Tick;
        return it;
    }
}
