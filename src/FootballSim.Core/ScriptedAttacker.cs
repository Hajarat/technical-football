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
/// </summary>
public sealed class ScriptedAttacker
{
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

    SimRng Tactic(World w) => _tacticRng ??= new SimRng(w.Seed ^ 0x3A91C7E25B0D4F68UL);

    /// <summary>The intent for the attacker this tick.</summary>
    public TickIntent Next(World w)
    {
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
        bool hold = _heldFor == 0f && (_roundSide != 0 || !close || running && toGoal > pressAt);
        if (_chipThis && (strikable || close)) hold = false;
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
