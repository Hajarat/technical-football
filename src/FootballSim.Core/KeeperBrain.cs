using System.Numerics;

namespace FootballSim.Core;

/// <summary>What the world tells the keeper each tick about the attacker and the round.</summary>
internal readonly struct KeeperSituation
{
    /// <summary>
    /// Seconds until the attacker can next play the ball as the keeper reads it: his committed touch, else the contact his touch
    /// controller steers for, a skill move's or shot's contact, or (with none) his run to the ball. +infinity when he cannot.
    /// </summary>
    public float AttackerNextTouch { get; init; }
    /// <summary>The world tick of that next touch (-1 none, -2 a run to a loose ball); a change redraws the keeper's misjudgement.</summary>
    public int AttackerTouchTick { get; init; }
    /// <summary>A shot is winding up (pressed, not yet struck).</summary>
    public bool Windup { get; init; }
    /// <summary>While a shot winds up: seconds to its earliest contact (the strike), else +infinity.</summary>
    public float StrikeIn { get; init; }
    /// <summary>A struck shot is in flight and not yet resolved.</summary>
    public bool ShotInFlight { get; init; }
    /// <summary>The attacker controls the ball (dribbling it).</summary>
    public bool AttackerHasBall { get; init; }
    /// <summary>The round is over (outcome showing): the keeper only settles.</summary>
    public bool BallDead { get; init; }
    /// <summary>The attacker's feet and velocity (closing down starts when he comes at goal with the ball).</summary>
    public Vector3 AttackerPosition { get; init; }
    public Vector3 AttackerVelocity { get; init; }

    /// <summary>No attacker information: the keeper holds his arc and reacts to strikes only.</summary>
    public static KeeperSituation None => new() { AttackerNextTouch = float.PositiveInfinity, AttackerTouchTick = -1, StrikeIn = float.PositiveInfinity };
}

/// <summary>
/// What decided the standing keeper's movement on a tick (a diagnostic for probes and traces; it changes no behaviour). Slide and
/// Poke: going for a ball outside his penalty area with his feet; HoldArea: a close race for a ball outside the area, he comes
/// to the edge of the area and no further.
/// </summary>
public enum KeeperMode { None, Hold, Arc, CloseDown, Charge, Jockey, Spread, Plant, Rush, Anticipate, Smother, Play, Slide, Poke, HoldArea }

/// <summary>Which bound set his closing-down depth on a tick.</summary>
public enum KeeperLimit { None, Arc, Want, ChipCap, Gap, HoldCap, Kept, Spread }

/// <summary>
/// Why the standing keeper did what he did on the latest tick (for probes and traces): the mode, the bound that set his depth,
/// the depths in play (metres off the goal mouth), the best rush race he saw: the time he was short of winning it by
/// (negative: he won with that much to spare; +infinity: no race to run, see <see cref="RaceBlock"/>), and the charge decision:
/// the seconds he needs to close the attacker down and the seconds he accepts (NaN when no charge was weighed).
/// </summary>
public readonly record struct KeeperThought(KeeperMode Mode, KeeperLimit Limit, bool Engaged, float Want, float ChipCap, float GapCap,
    float RaceShort, string RaceBlock, float CloseT = float.NaN, float Tolerance = float.NaN);

/// <summary>
/// The keeper's decision code (Phase D). Standing, he holds a spot on an arc off his line on the angle to the ball, biased to the
/// near post and deeper or shallower by his positioning; each new spot carries a positioning error. Once an attacker comes in
/// with the ball he closes down along the cover line, further out as the ball comes closer, but never beyond the depth he
/// judges he can recover from a chip, unless he charges: with the attacker coming on inside the charge range he runs at him once he
/// can close him down quickly enough (the time he accepts set by his rushing rating, cut by how far beyond his chip-safe depth it
/// would take him), and jockeys a couple of metres from the ball, stepping with him. When he can reach a loose ball (between the attacker's touches) before the attacker's
/// next planned touch, with a margin set by his rushing rating, he rushes it, and dives on it (smother) once it is within a
/// dive; a close race starts him toward it on anticipation. Close to an attacker he cannot beat to the ball, he stops and
/// spreads. When a shot winds up he plants; set close to the shooter he is ready, his standing hull big. From the strike he has
/// a reflex and, later, a full reaction (both from his reflexes, plus a penalty when he was still moving): at the reflex, if
/// his full reaction and dive would come too late, he makes a limited-range reflex save (a hand, a foot or the body thrown at
/// it); otherwise, at the reaction, a chip going over him he backpedals for and leaps at, and anything else he dives at, at the
/// first point on the ball's predicted path inside his dive wedge that his reach can get to in time. The save's difficulty
/// (the range it needs by the ball's height, more moving the wrong way) sets its execution time, and with the ball's speed and
/// the time he had, whether he catches, parries, blocks, tips it over the bar or fingertips it (KeeperBrain.Save.cs). After a
/// dive he lies, then gets up in 1.0 s; a new save can start 0.2 s into getting up. He uses his hands only on a ball in his
/// penalty area; outside it he plays a loose ball with his feet, a slide tackle or a standing poke (KeeperBrain.Feet.cs).
/// </summary>
public sealed partial class KeeperBrain
{
    enum Phase { Stand, Reacting, Backpedal, Diving, Smother, Held, Grounded, GettingUp, Slide }

    readonly Tuning _t;
    readonly Pitch _pitch;
    readonly PlayerState _k;
    readonly PlayerSheet _attacker;
    readonly bool _holdLine;

    Phase _phase = Phase.Stand;
    float _phaseTime;
    bool _struck;               // a shot has been struck this round (ends a held line)
    /// <summary>A held line is kept even when the attacker dribbles off the spot (tests).</summary>
    internal bool HoldLineThroughDribble { get; init; }
    bool _lineReleased;         // a penalty taker dribbled off the spot: open play (ends a held line)
    Vector3? _penaltySpot;      // where the ball sat when he first stood (the spot, in a penalty)

    // Standing: the arc spot.
    bool _hasSpot;
    Vector3 _spotIdeal;         // the ideal spot when the current one was taken
    Vector3 _spot;              // the spot he is moving to (ideal plus his positioning error)
    bool _shuffling;

    // Rush.
    bool _rushing;
    float _misjudge;
    int _misjudgeTick = int.MinValue;

    Vector3 _hands;
    bool _tickBegun;
    Vector3 _handsDown;         // where the hands were when he started to get up
    bool _roundRushed, _roundSmother, _roundCharged;

    /// <summary>The numbers for the most recent shot.</summary>
    public KeeperReport Report { get; private set; } = new();

    /// <summary>Why he did what he did on the latest tick (diagnostic only).</summary>
    public KeeperThought Thought { get; private set; }

    /// <param name="attacker">The attacker's sheet, for the chips he judges his depth against (a default sheet when null).</param>
    internal KeeperBrain(PlayerState keeper, Pitch pitch, Tuning tuning, bool holdLine, PlayerSheet? attacker = null)
    {
        _k = keeper;
        _pitch = pitch;
        _t = tuning;
        _holdLine = holdLine;
        _attacker = attacker ?? new PlayerSheet();
        _hands = HomeFor(keeper.Position);
        keeper.HandPosition = _hands;
        keeper.KeeperAction = KeeperAction.Set;
        keeper.KeeperDive = KeeperDive.None;
        keeper.KeeperActionTime = 0f;
        keeper.SaveContactTime = float.NaN;
    }

    Vector3 HomeFor(Vector3 feet) => new(feet.X, _k.Sheet.HeightM * _t.Keeper.HomeHeightFraction, feet.Z);

    /// <summary>True while the keeper holds the ball.</summary>
    internal bool Holding => _phase == Phase.Held;

    /// <summary>The arc (or line) spot he is moving to, including his positioning error (tests).</summary>
    internal Vector3 Spot => _spot;
    /// <summary>The ideal spot when the current one was taken (tests).</summary>
    internal Vector3 SpotIdeal => _spotIdeal;

    void SetAction(KeeperAction a, KeeperDive d = KeeperDive.None)
    {
        if (_k.KeeperAction != a) { _k.KeeperAction = a; _k.KeeperActionTime = 0f; }
        _k.KeeperDive = a == KeeperAction.Diving ? d : KeeperDive.None;
    }

    void Enter(Phase p)
    {
        _phase = p;
        _phaseTime = 0f;
        if (p is not (Phase.Diving or Phase.Smother or Phase.Slide)) _k.SaveContactTime = float.NaN;   // for views only
    }

    /// <summary>Back on his feet and set: a new arc spot is taken on the next tick, the hands go back to their rest point.</summary>
    void ToStand()
    {
        Enter(Phase.Stand);
        _hasSpot = false;
        _fromBackpedal = false;
        SetAction(KeeperAction.Set);
        _hands = HomeFor(_k.Position);
        _k.HandPosition = _hands;
    }

    // ---- tick ----

    /// <summary>Advances the keeper one tick with no attacker information (tests).</summary>
    internal void Update(float dt, Ball ball, SimRng rng) => Update(dt, ball, rng, KeeperSituation.None);

    /// <summary>
    /// Starts a world tick: the time in the current action advances, so an action that changes later in the tick (at a strike,
    /// in the update, or at a contact) reads 0 at the end of it. <see cref="Update(float, Ball, SimRng, in KeeperSituation)"/>
    /// does this itself when the world has not.
    /// </summary>
    internal void BeginTick(float dt)
    {
        _k.KeeperActionTime += dt;
        _tickBegun = true;
    }

    /// <summary>Advances the keeper one tick.</summary>
    internal void Update(float dt, Ball ball, SimRng rng, in KeeperSituation s)
    {
        if (!_tickBegun) _k.KeeperActionTime += dt;
        _tickBegun = false;
        _phaseTime += dt;
        if (_pendingStrike) _pendingSince += dt;
        switch (_phase)
        {
            case Phase.Stand:
                StepStanding(dt, ball, rng, s);
                break;
            case Phase.Reacting:
                StepReacting(dt, ball, rng);
                break;
            case Phase.Backpedal:
                _since += dt;
                StepBackpedal(dt, ball, rng);
                break;
            case Phase.Diving:
                _since += dt;
                StepDive(dt);
                break;
            case Phase.Smother:
                StepSmother(dt, ball);
                break;
            case Phase.Slide:
                StepFeet(dt);
                break;
            case Phase.Grounded:
                Settle(dt);
                if (_phaseTime >= _t.Keeper.GroundedSeconds) { Enter(Phase.GettingUp); _handsDown = _hands; SetAction(KeeperAction.GettingUp); }
                break;
            case Phase.GettingUp:
                Settle(dt);
                _hands = Vector3.Lerp(_handsDown, HomeFor(_k.Position), Math.Clamp(_phaseTime / _t.Keeper.GetUpSeconds, 0f, 1f));
                if (_pendingStrike && _phaseTime >= _t.Keeper.GetUpSaveAfter) BeginReacting(rng, _pendingSince);
                else if (_phaseTime >= _t.Keeper.GetUpSeconds) ToStand();
                break;
            case Phase.Held:
                Settle(dt);
                SetAction(KeeperAction.Holding);
                break;
        }
        _k.HandPosition = _hands;
        if (_phase != Phase.Stand) Thought = new KeeperThought(KeeperMode.Play, KeeperLimit.None, _engaged, float.NaN, float.NaN, float.NaN, float.PositiveInfinity, "");
    }

    // ---- movement ----

    void FaceBall(Ball ball, float dt)
    {
        var d = new Vector2(ball.Position.X - _k.Position.X, ball.Position.Z - _k.Position.Z);
        if (d.LengthSquared() < 1e-4f) return;
        float want = MathF.Atan2(d.Y, d.X);
        float diff = MathF.IEEERemainder(want - _k.Facing, 2f * MathF.PI);
        float max = _t.Keeper.TurnRateDeg * MathF.PI / 180f * dt;
        _k.Facing += Math.Clamp(diff, -max, max);
    }

    /// <summary>Accelerates toward a spot at up to <paramref name="vmax"/>, slowing to arrive (accel-limited both ways).</summary>
    void MoveTo(Vector3 target, float vmax, float accel, float dt)
    {
        var pos = new Vector2(_k.Position.X, _k.Position.Z);
        var d = Keeper.CapTarget(pos, new Vector2(target.X, target.Z), _t.Keeper.MoveTargetCap) - pos;
        float dist = d.Length();
        Vector2 desired = dist > 0.01f ? d / dist * MathF.Min(vmax, MathF.Sqrt(2f * accel * MathF.Max(0f, dist - 0.01f))) : Vector2.Zero;
        Steer(desired, accel, dt);
    }

    /// <summary>Runs through a point at <paramref name="speed"/> (no slowing to arrive), toward a target at most the move cap away.</summary>
    void RunAt(Vector3 target, float speed, float dt)
    {
        var pos = new Vector2(_k.Position.X, _k.Position.Z);
        var d = Keeper.CapTarget(pos, new Vector2(target.X, target.Z), _t.Keeper.MoveTargetCap) - pos;
        float dist = d.Length();
        Vector2 desired = dist > 0.01f ? d / dist * speed : Vector2.Zero;
        Steer(desired, Keeper.MoveAccel(_k.Sheet, _t), dt);
    }

    void Brake(float decel, float dt) => Steer(Vector2.Zero, decel, dt);

    void Steer(Vector2 desired, float accel, float dt)
    {
        var vel = new Vector2(_k.Velocity.X, _k.Velocity.Z);
        var dv = desired - vel;
        float max = accel * dt;
        float len = dv.Length();
        if (len > max) dv *= max / len;
        vel += dv;
        var old = _k.Position;
        var pos = old + new Vector3(vel.X, 0f, vel.Y) * dt;
        var v3 = new Vector3(vel.X, 0f, vel.Y);
        _pitch.ClampPlayer(ref pos, ref v3);
        _k.Position = pos;
        _k.Velocity = v3;
    }

    /// <summary>While reacting, a moving keeper carries on, braking at the plant deceleration.</summary>
    void CarryMomentum(float dt) => Brake(_t.Keeper.PlantDecel, dt);

    /// <summary>On the ground or holding: any speed left dies at the plant deceleration.</summary>
    void Settle(float dt) => Brake(_t.Keeper.PlantDecel, dt);

    // ---- smother ----

    void StartSmother(Vector3 point)
    {
        Enter(Phase.Smother);
        _active = true;
        _rushing = false;
        _fromBackpedal = false;
        _contact = false;
        _handsOff = false;
        if (!_struck) { _roundSmother = true; Report.Smothered = true; }
        _feet0 = _k.Position;
        _home = HomeFor(_k.Position);
        _carry0 = new Vector2(_k.Velocity.X, _k.Velocity.Z);
        _target = point;
        PublishSave(point, Vector3.Distance(point, _home) / _t.Keeper.SmotherSpeed);
        SetAction(KeeperAction.Smother);
        StepSmother(0f, null);
    }

    /// <summary>
    /// One smother tick. A ball that leaves his penalty area before he has it may not be gathered: he turns the smother into a
    /// slide or a poke at it when it is still low and within a slide, else he abandons it and is left on the ground.
    /// </summary>
    void StepSmother(float dt, Ball? ball)
    {
        var k = _t.Keeper;
        if (ball != null && !_contact && !HandsAllowedAt(ball.Position))
        {
            var b = new Vector2(ball.Position.X, ball.Position.Z);
            float d = Vector2.Distance(b, new Vector2(_k.Position.X, _k.Position.Z));
            if (ball.Position.Y <= k.ClaimMaxHeight && d <= k.SlideReach)
                StartFeet(new Vector3(ball.Position.X, _t.Ball.Radius, ball.Position.Z), poke: ball.Position.Y > k.SlideMaxHeight || d <= k.PokeReach);
            else
            {
                _active = false;
                Enter(Phase.Grounded);
                SetAction(KeeperAction.Grounded);
            }
            return;
        }
        Vector3 to = _target - _home;
        float dist = to.Length();
        if (!_contact) _hands = dist > 1e-4f ? _home + to / dist * MathF.Min(k.SmotherSpeed * _phaseTime, dist) : _home;
        var old = _k.Position;
        var pos = new Vector3(_feet0.X + k.BodyFollow * (_hands.X - _home.X), 0f, _feet0.Z + k.BodyFollow * (_hands.Z - _home.Z));
        var vel = Vector3.Zero;
        _pitch.ClampPlayer(ref pos, ref vel);
        _k.Position = pos;
        if (dt > 0f) _k.Velocity = (pos - old) / dt;
        if (_phaseTime >= k.SmotherMaxSeconds || _contact && _phaseTime >= dist / k.SmotherSpeed)
        {
            if (_phase != Phase.Smother) return;
            _active = false;
            Enter(Phase.Grounded);
            SetAction(KeeperAction.Grounded);
        }
    }
}
