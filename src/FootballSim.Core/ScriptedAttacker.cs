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

    bool _released;
    float _heldFor;
    int _releaseTick;
    float _pressCharge = float.NaN;  // the charge chosen at the press of the current attempt
    SimRng? _rng;

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
    /// In possession: stick at the goal centre. Otherwise: stick toward a point just beyond the ball, toward the goal, to run
    /// onto it.
    /// </summary>
    void RunOntoBall(World w, ref TickIntent it)
    {
        var a = w.Attacker;
        var ball = w.Ball.Position;
        var feet = new Vector2(a.Position.X, a.Position.Z);
        Vector2 d;
        if (w.BallController == a)
            d = new Vector2(w.Pitch.GoalLineX, 0f) - feet;
        else
        {
            var goal = new Vector2(w.Pitch.GoalLineX - ball.X, -ball.Z);
            goal = goal.LengthSquared() > 1e-6f ? Vector2.Normalize(goal) : new Vector2(1f, 0f);
            d = new Vector2(ball.X, ball.Z) + goal * LookAhead - feet;
        }
        d = d.LengthSquared() > 1e-6f ? Vector2.Normalize(d) : new Vector2(1f, 0f);
        it.MoveX = d.X; it.MoveZ = d.Y; it.Sprint = RunSprint; it.CloseControl = RunCloseControl;
    }

    /// <summary>The intent for the attacker this tick.</summary>
    public TickIntent Next(World w)
    {
        var it = new TickIntent
        {
            Finesse = Type is ShotType.Finesse or ShotType.LowFinesse,
            Chip = Type == ShotType.Chip,
            Driven = Type is ShotType.Driven or ShotType.LowFinesse,
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
        // Ready to press: the ball is strikable now, or (in possession) the next touch, which becomes the soft touch once shoot
        // is held, comes early enough in the wind-up for the ball to be strikable at the contact.
        bool strikable = ShotModel.CanStrike(a, w.Ball, w.Tuning);
        bool running = w.Scenario.ShootDistance > 0f;
        float charge = float.IsNaN(_pressCharge) ? (Charge ?? 0f) : _pressCharge;
        float toContact = MathF.Max(k.WindupSeconds, charge * k.ChargeSeconds + k.ReleaseLeadSeconds);
        bool close = strikable
            || w.BallController == a && a.NextTouchEta <= toContact - PressMargin
            || w.BallController == a && running && toGoal <= w.Scenario.ShootDistance - LateDistance;
        if (_heldFor == 0f && (!close || running && toGoal > w.Scenario.ShootDistance))
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
