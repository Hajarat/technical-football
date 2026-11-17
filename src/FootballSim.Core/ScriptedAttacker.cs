using System.Numerics;

namespace FootballSim.Core;

/// <summary>
/// A scripted attacker for batches and tests. It produces <see cref="TickIntent"/> values through the same input path a gamepad
/// would use. If the scenario asks for a shooting distance it runs onto the ball and, once in possession, simply points the
/// stick at the goal (the possession assist keeps the feet on the ball). Then it holds shoot for a fixed charge and releases
/// with the stick pointing 0.6 m inside a post. After the shot it stands still.
/// </summary>
public sealed class ScriptedAttacker
{
    /// <summary>Fixed charge held before releasing, 0..1.</summary>
    public float Charge { get; set; } = 0.6f;
    /// <summary>How far inside the post to aim, metres.</summary>
    public float AimInset { get; set; } = 0.6f;
    /// <summary>+1 aims at the +Z post, -1 at the -Z post, 0 picks the far post (the +Z post from a central ball).</summary>
    public int Side { get; set; }
    public ShotType Type { get; set; } = ShotType.Normal;
    /// <summary>Sprint input while running onto the ball, 0..1.</summary>
    public float RunSprint { get; set; } = 1f;
    /// <summary>How far beyond a loose ball the run onto it is aimed, metres.</summary>
    public float LookAhead { get; set; } = 0.6f;
    /// <summary>Hold close control while running onto the ball.</summary>
    public bool RunCloseControl { get; set; }
    /// <summary>In possession, shoot is pressed once the predicted next touch is at least this long before the release, seconds.</summary>
    public float PressMargin { get; set; } = 0.1f;
    /// <summary>In possession, shoot is pressed regardless once the ball is this much closer than the shooting distance, metres.</summary>
    public float LateDistance { get; set; } = 3f;
    /// <summary>Longest extra hold past the charge while waiting for the ball to be strikable, seconds.</summary>
    public float ExtraHold { get; set; } = 0.5f;

    bool _released;
    float _heldFor;
    int _releaseTick;

    /// <summary>True once the shoot button has been released.</summary>
    public bool Fired => _released;

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
        var it = new TickIntent { Finesse = Type == ShotType.Finesse, Chip = Type == ShotType.Chip };
        if (_released)
        {
            // If the release was dropped (ball out of reach, nothing struck), run onto the ball and try again.
            if (w.State == RoundState.Live && w.Shots.Count == 0 && w.Tick - _releaseTick > 50)
            {
                _released = false;
                _heldFor = 0f;
            }
            else return it;
        }

        var a = w.Attacker;
        var ball = w.Ball.Position;
        float toGoal = new Vector2(w.Pitch.GoalLineX - ball.X, ball.Z).Length();
        float chargeSeconds = Charge * w.Tuning.Shot.ChargeSeconds;
        // Ready to press: the ball is strikable now, or (in possession) the next touch, which becomes the soft touch once shoot
        // is held, comes early enough in the charge for the ball to be strikable at the release.
        bool strikable = ShotModel.CanStrike(a, w.Ball, w.Tuning);
        bool running = w.Scenario.ShootDistance > 0f;
        bool close = strikable
            || w.BallController == a && a.NextTouchEta <= chargeSeconds - PressMargin
            || w.BallController == a && running && toGoal <= w.Scenario.ShootDistance - LateDistance;
        if (_heldFor == 0f && (!close || running && toGoal > w.Scenario.ShootDistance))
        {
            RunOntoBall(w, ref it);
            return it;
        }

        _heldFor += w.Tuning.Dt;
        // Hold for the charge; if the ball is not strikable yet (a sprint knock still rolling back), hold a little longer.
        if (_heldFor < chargeSeconds || !strikable && _heldFor < chargeSeconds + ExtraHold)
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
