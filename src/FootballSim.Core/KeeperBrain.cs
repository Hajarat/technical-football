using System.Numerics;

namespace FootballSim.Core;

/// <summary>What the world tells the keeper each tick about the attacker and the round.</summary>
internal readonly struct KeeperSituation
{
    /// <summary>
    /// Seconds until the attacker can next play the ball as the keeper reads it: his planned touch, the planned meet after a push,
    /// a shot's contact, or (with no plan) his run to the ball. +infinity when he cannot.
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

/// <summary>What decided the standing keeper's movement on a tick (a diagnostic for probes and traces; it changes no behaviour).</summary>
public enum KeeperMode { None, Hold, Arc, CloseDown, Charge, Jockey, Spread, Plant, Rush, Anticipate, Smother, Play }

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
/// spreads. When a shot winds up he plants. From the strike he reacts after a delay from his reflexes (plus a penalty when he
/// was still moving); a chip going over him he backpedals for and leaps at; otherwise he dives at the first point on the
/// ball's predicted path inside his dive wedge that his reach can get to in time, catching, parrying, blocking at the feet,
/// tipping over the bar or fingertipping by the ball's speed and where he meets it. After a dive he lies, then gets up in
/// 1.0 s; a new save can start 0.2 s into getting up.
/// </summary>
public sealed partial class KeeperBrain
{
    enum Phase { Stand, Reacting, Backpedal, Diving, Smother, Held, Grounded, GettingUp }

    readonly Tuning _t;
    readonly Pitch _pitch;
    readonly PlayerState _k;
    readonly PlayerSheet _attacker;
    readonly bool _holdLine;

    Phase _phase = Phase.Stand;
    float _phaseTime;
    bool _struck;               // a shot has been struck this round (ends a held line)

    // Standing: the arc spot.
    bool _hasSpot;
    Vector3 _spotIdeal;         // the ideal spot when the current one was taken
    Vector3 _spot;              // the spot he is moving to (ideal plus his positioning error)
    bool _shuffling;

    // Rush.
    bool _rushing;
    float _misjudge;
    int _misjudgeTick = int.MinValue;

    // Save.
    float _since;               // seconds since the strike
    float _reaction;
    float _jitter;
    Vector3 _home;              // hands' rest point when the dive started
    Vector3 _feet0;             // feet when the dive started
    Vector2 _carry0;            // ground velocity when the dive started
    Vector3 _target;
    float _targetT;             // seconds after the reaction the ball was predicted at the target
    Vector2 _planeN = new(1f, 0f);
    Vector2 _planePoint;        // a point on the save plane (ground X/Z)
    float _spread;              // 0..1: how spread he was at the strike (close to the shooter)
    float _span;                // his hands' sideways span at the strike (0 when he was not set)
    Vector2 _faceAxis = new(-1f, 0f); // ground direction to the ball at the strike
    Vector2 _spreadAxis;        // ground direction to the ball at the strike
    KeeperDive _dive;
    float _reachedFor;          // seconds the dive's reach has been complete
    bool _contact;              // the ball has been met in this dive
    bool _active;               // a shot or loose ball is being played and not yet resolved
    bool _pendingStrike;        // a strike came while he was down; he reacts once he may
    float _pendingSince;

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
    }

    Vector3 HomeFor(Vector3 feet) => new(feet.X, _k.Sheet.HeightM * _t.Keeper.HomeHeightFraction, feet.Z);

    /// <summary>The sphere the ball's path is tested against: the hands (or the body spread in a smother) plus the ball radius. Null when not live.</summary>
    internal SweepSphere? HandsSphere =>
        _active && _phase is Phase.Reacting or Phase.Diving or Phase.Smother
            ? new SweepSphere(_hands, (_phase == Phase.Smother ? _t.Keeper.SmotherRadius : _t.Keeper.HandRadius) + _t.Ball.Radius)
            : null;

    /// <summary>
    /// The keeper's body as a capsule the ball's path is tested against (grown by the ball radius) while he plays a shot: upright
    /// from the shins to the shoulders until his dive gets going, then stretched from his hips toward his hands. Null when not live.
    /// </summary>
    internal SweepCapsule? BodyCapsule
    {
        get
        {
            if (!_active || _phase is not (Phase.Reacting or Phase.Diving)) return null;
            var k = _t.Keeper;
            float r = k.BodyRadius + _t.Ball.Radius;
            var feet = _k.Position;
            float top = k.BodyTopFraction * _k.Sheet.HeightM;
            bool upright = _phase == Phase.Reacting || _dive is KeeperDive.Catch or KeeperDive.Feet || DiveTau <= 0f;
            if (upright) return UprightBody(feet);
            Vector3 to = _hands - _home;
            float dist = to.Length();
            float progress = dist > 1e-3f ? Math.Clamp(Reach(DiveTau) / dist, 0f, 1f) : 1f;
            float hipY = k.HipHeight + (Math.Clamp(_hands.Y, k.BodyLow, top) - k.HipHeight) * progress;
            var hip = new Vector3(feet.X, hipY, feet.Z);
            Vector3 toHands = _hands - hip;
            float len = toHands.Length();
            Vector3 end = len > k.HandRadius ? _hands - toHands / len * k.HandRadius : hip;
            return new SweepCapsule(hip, end, r);
        }
    }

    /// <summary>
    /// The standing body (grown by the ball radius): spread across the shot when he was set close to the shooter at the strike,
    /// else upright from the shins to the shoulders.
    /// </summary>
    SweepCapsule UprightBody(Vector3 feet)
    {
        var k = _t.Keeper;
        if (_spread > 0f)
        {
            // Set close to the shooter he makes himself big: legs and arms spread across the line of the shot.
            var lat = new Vector3(-_spreadAxis.Y, 0f, _spreadAxis.X) * (k.SpreadHalfWidth * _spread);
            var mid = new Vector3(feet.X, k.SpreadHeight, feet.Z);
            return new SweepCapsule(mid - lat, mid + lat, k.SpreadRadius + _t.Ball.Radius);
        }
        return new SweepCapsule(new Vector3(feet.X, k.BodyLow, feet.Z), new Vector3(feet.X, k.BodyTopFraction * _k.Sheet.HeightM, feet.Z),
            k.BodyRadius + _t.Ball.Radius);
    }

    static float SegmentDistance(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float l2 = Vector3.Dot(ab, ab);
        float u = l2 > 1e-9f ? Math.Clamp(Vector3.Dot(p - a, ab) / l2, 0f, 1f) : 0f;
        return Vector3.Distance(p, a + ab * u);
    }

    /// <summary>True while the keeper holds the ball.</summary>
    internal bool Holding => _phase == Phase.Held;

    /// <summary>The arc (or line) spot he is moving to, including his positioning error (tests).</summary>
    internal Vector3 Spot => _spot;
    /// <summary>The ideal spot when the current one was taken (tests).</summary>
    internal Vector3 SpotIdeal => _spotIdeal;

    /// <summary>
    /// Test seam: puts the keeper straight into a dive at <paramref name="target"/>, <paramref name="sinceReaction"/> seconds after
    /// his reaction, from where he stands (no jitter).
    /// </summary>
    internal void BeginDiveForTest(Vector3 target, float sinceReaction)
    {
        Report = new KeeperReport { ReactionS = 0f };
        Enter(Phase.Diving);
        _active = true;
        _contact = false;
        _reaction = 0f;
        _jitter = 0f;
        _since = sinceReaction;
        _feet0 = _k.Position;
        _home = HomeFor(_k.Position);
        _carry0 = Vector2.Zero;
        _reachedFor = 0f;
        _spread = 0f;
        _span = 0f;
        _target = target;
        _targetT = sinceReaction;
        _planePoint = new Vector2(_home.X, _home.Z);
        _dive = Keeper.DiveKindFor(_feet0, target, _t);
        Report.Dive = _dive;
        SetAction(KeeperAction.Diving, _dive);
        StepDive(0f);
    }

    void SetAction(KeeperAction a, KeeperDive d = KeeperDive.None)
    {
        if (_k.KeeperAction != a) { _k.KeeperAction = a; _k.KeeperActionTime = 0f; }
        _k.KeeperDive = a == KeeperAction.Diving ? d : KeeperDive.None;
    }

    void Enter(Phase p) { _phase = p; _phaseTime = 0f; }

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

    // ---- strike ----

    /// <summary>The ball has just been struck: start reacting (or, if he is down, as soon as he may).</summary>
    internal void OnStrike(SimRng rng, Ball? ball = null)
    {
        _struck = true;
        float speed = new Vector2(_k.Velocity.X, _k.Velocity.Z).Length();
        Report = new KeeperReport
        {
            SpeedAtStrikeMs = speed,
            DepthAtStrikeM = _pitch.GoalLineX - _k.Position.X,
            BallDistanceAtStrikeM = ball == null ? float.NaN : Vector2.Distance(new Vector2(ball.Position.X, ball.Position.Z), new Vector2(_pitch.GoalLineX, 0f)),
            PlantedAtStrike = _phase == Phase.Stand && _k.KeeperAction is KeeperAction.Planted or KeeperAction.Spread && speed <= _t.Keeper.StillSpeed,
            Rushed = _roundRushed,
            Charged = _roundCharged,
            Smothered = _roundSmother,
            WonBall = _roundWon,
            Rounded = _roundRounded,
            DepthAtBandM = (float[])_bandDepth.Clone(),
            DepthReversals = _reversals,
            DepthReversalsFirst2S = _reversals2,
        };
        _spread = 0f;
        if (ball != null)
        {
            Report.OnTarget = ball.PredictPlaneCrossing(_pitch.GoalLineX, _t.Dt, 3f, out var cross, out _)
                && MathF.Abs(cross.Z) + _t.Ball.Radius <= _pitch.GoalHalfWidth && cross.Y + _t.Ball.Radius <= _pitch.BarHeight;
            var h = new Vector2(ball.Velocity.X, ball.Velocity.Z);
            _planeN = h.LengthSquared() > 1e-6f ? Vector2.Normalize(h) : new Vector2(1f, 0f);
            // Planted close to the shooter, he is spread at the strike: fully within spread_full, not at all beyond spread_range.
            var toBall = new Vector2(ball.Position.X - _k.Position.X, ball.Position.Z - _k.Position.Z);
            float d = toBall.Length();
            var k = _t.Keeper;
            if (d > 1e-3f) _faceAxis = toBall / d;
            if (_phase == Phase.Stand && _k.KeeperAction is KeeperAction.Planted or KeeperAction.Spread && d < k.SpreadRange)
            {
                _spread = Math.Clamp((k.SpreadRange - d) / (k.SpreadRange - k.SpreadFull), 0f, 1f);
                _spreadAxis = d > 1e-3f ? toBall / d : new Vector2(-1f, 0f);
            }
        }
        else _planeN = new Vector2(1f, 0f);
        switch (_phase)
        {
            case Phase.Held:
                return;
            case Phase.Smother when _active:
                return; // already going down at the ball: the body is in the way or not
            case Phase.Diving when _active:
            case Phase.Backpedal when _active:
                return;
            case Phase.Diving:
            case Phase.Smother:
            case Phase.Grounded:
                _pendingStrike = true; _pendingSince = 0f;
                return;
            case Phase.GettingUp when _phaseTime < _t.Keeper.GetUpSaveAfter:
                _pendingStrike = true; _pendingSince = 0f;
                return;
        }
        BeginReacting(rng, 0f);
    }

    void BeginReacting(SimRng rng, float elapsed)
    {
        float speed = new Vector2(_k.Velocity.X, _k.Velocity.Z).Length();
        Enter(Phase.Reacting);
        _active = true;
        _rushing = false;
        _anticipating = false;
        _fromBackpedal = false;
        _pendingStrike = false;
        _since = elapsed;
        _reaction = Keeper.ReactionAtStrike(_k.Sheet, _t, rng, speed);
        _span = speed <= _t.Keeper.StillSpeed && elapsed <= 0f ? _t.Keeper.HandSpan : 0f;
        Report.ReactionS = _reaction;
        SetAction(KeeperAction.Reacting);
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
                _since += dt;
                CarryMomentum(dt);
                _hands = HomeFor(_k.Position);
                if (_since >= _reaction)
                {
                    if (ChipOverHim(ball)) BeginBackpedal();
                    else StartDive(ball, rng);
                }
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
                StepSmother(dt);
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

    // ---- the save ----

    void StartDive(Ball ball, SimRng rng)
    {
        var k = _t.Keeper;
        var sheet = _k.Sheet;
        Enter(Phase.Diving);
        _feet0 = _k.Position;
        _home = HomeFor(_k.Position);
        _carry0 = new Vector2(_k.Velocity.X, _k.Velocity.Z);
        _reachedFor = 0f;
        _contact = false;
        var toBall = new Vector2(ball.Position.X - _feet0.X, ball.Position.Z - _feet0.Z);
        Vector2 facing = toBall.LengthSquared() > 1e-4f ? Vector2.Normalize(toBall) : new Vector2(-1f, 0f);
        var hv = new Vector2(ball.Velocity.X, ball.Velocity.Z);
        if (hv.LengthSquared() > 1e-4f) _planeN = Vector2.Normalize(hv);

        // Scan the ball's predicted path: the first point in the wedge his reach gets to in time, else as a best effort the
        // point in the wedge he gets closest to in time (or, with none in the wedge, the ball's closest pass).
        var b = ball.ScratchA();
        float dt = _t.Dt;
        int steps = (int)(1.5f / dt);
        bool found = false, crossed = false, passed = false, block = false;
        var body = UprightBody(_feet0);
        Vector3 target = _home, plane = _home, closest = ball.Position;
        float tTarget = 0f, tPlane = 0f, tClosest = 0f, bestDeficit = float.MaxValue;
        float bestNeed = float.MaxValue;
        var home2 = new Vector2(_home.X, _home.Z);
        float prevSide = Vector2.Dot(new Vector2(ball.Position.X, ball.Position.Z) - home2, _planeN);
        Vector3 prev = ball.Position;
        float leap = Keeper.ChipReachHeight(sheet, _t);
        for (int i = 1; i <= steps; i++)
        {
            b.Step(dt, null, null);
            var p = b.Position;
            float t = i * dt;
            float side = Vector2.Dot(new Vector2(p.X, p.Z) - home2, _planeN);
            if (!passed && prevSide < 0f && side >= 0f) passed = true;
            float need = Vector3.Distance(p, _home);
            if (need < bestNeed && p.Y <= leap) { bestNeed = need; closest = p; tClosest = t; }
            // The ball will hit him where he stands: he stays big and blocks it rather than diving out of its way.
            if (p.X <= _pitch.GoalLineX && SegmentDistance(p, body.A, body.B) <= body.Radius) { target = p; tTarget = t; found = true; block = true; break; }
            // Above his leap he cannot get a hand to it; after backpedalling he leaps backward, outside his usual wedge.
            if (p.X <= _pitch.GoalLineX && p.Y <= leap && (_fromBackpedal || Keeper.InWedge(_feet0, facing, p, _t)))
            {
                float deficit = need - Reach(t);
                if (deficit <= 0f) { target = p; tTarget = t; found = true; break; }
                if (deficit < bestDeficit) { bestDeficit = deficit; plane = p; tPlane = t; crossed = true; }
            }
            if (p.X > _pitch.GoalLineX + 0.3f || passed && side > 1.5f) break;
            prevSide = side;
            prev = p;
        }
        if (!found) { target = crossed ? plane : closest; tTarget = crossed ? tPlane : tClosest; }
        _targetT = tTarget;

        // His read of it is not perfect.
        var lateral = new Vector2(-_planeN.Y, _planeN.X);
        float nz = rng.Normal(0f, k.PredictionNoise), ny = rng.Normal(0f, k.PredictionNoise);
        target = new Vector3(target.X + lateral.X * nz, MathF.Max(_t.Ball.Radius * 0.5f, target.Y + ny), target.Z + lateral.Y * nz);
        _target = target;
        // He is beaten once the ball passes his save plane: through his hands' rest point, or through the target if that is
        // further along the ball's travel (a ball taken just behind him).
        float beyond = Vector2.Dot(new Vector2(target.X - _home.X, target.Z - _home.Z), _planeN);
        _planePoint = beyond > 0f ? new Vector2(_home.X, _home.Z) + _planeN * beyond : new Vector2(_home.X, _home.Z);
        float needed = Vector3.Distance(target, _home);
        _jitter = rng.NextFloat() * Keeper.SaveJitterMax(sheet, _t, needed);
        _dive = _fromBackpedal ? KeeperDive.Back : Keeper.DiveKindFor(_feet0, target, _t);
        if (block && _dive is not (KeeperDive.Catch or KeeperDive.Feet))
            _dive = target.Y < k.FeetBlockHeight ? KeeperDive.Feet : KeeperDive.Catch;
        Report.Dive = _dive;
        SetAction(KeeperAction.Diving, _dive);
        StepDive(0f);
    }

    float DiveTau => _since - _reaction - _jitter;

    /// <summary>His reach <paramref name="tau"/> seconds into the dive, from his hand span when he was set at the strike.</summary>
    float Reach(float tau) => Keeper.ReachAt(_k.Sheet, _t, tau, _span);

    /// <summary>His sideways axis at the strike (ground X/Z, unit): across the line to the ball.</summary>
    Vector2 SideAxis => new(-_faceAxis.Y, _faceAxis.X);

    /// <summary>Where a hand rests in the set stance: <c>hand_span</c> to one side (+1 or -1) of his body's centre, at waist height.</summary>
    Vector3 HandRest(float side)
    {
        var feet = _phase == Phase.Diving ? _feet0 : _k.Position;
        var lat = SideAxis * (side * _span);
        return new Vector3(feet.X + lat.X, _t.Keeper.HandSpanHeightFraction * _k.Sheet.HeightM, feet.Z + lat.Y);
    }

    /// <summary>
    /// The set keeper's two hands, held out to the sides, as a capsule the ball's path is tested against (grown by the ball
    /// radius) from the strike until his dive gets going. Null when he was not set at the strike, or once he dives.
    /// </summary>
    internal SweepCapsule? HandsCapsule =>
        _active && _span > 0f && (_phase == Phase.Reacting || _phase == Phase.Diving && DiveTau <= 0f)
            ? new SweepCapsule(HandRest(-1f), HandRest(1f), _t.Keeper.HandRadius + _t.Ball.Radius)
            : null;

    void StepDive(float dt)
    {
        var k = _t.Keeper;
        float tau = DiveTau;
        float reach = Reach(tau);
        Vector3 to = _target - _home;
        float dist = to.Length();
        if (!_contact)
        {
            if (_span > 0f)
            {
                // From the near hand's set position out to the target as the reach grows past the span.
                Vector3 from = HandRest(Vector2.Dot(new Vector2(to.X, to.Z), SideAxis) >= 0f ? 1f : -1f);
                float f = dist <= _span ? 1f : Math.Clamp((reach - _span) / (dist - _span), 0f, 1f);
                _hands = Vector3.Lerp(from, _target, f);
            }
            else _hands = dist > 1e-4f ? _home + to / dist * MathF.Min(reach, dist) : _home;
        }

        // The body follows the hands sideways, carrying any speed it had at the dive's start.
        float tc = MathF.Max(0f, _since - _reaction);
        float v0 = _carry0.Length();
        float stopT = v0 / k.PlantDecel;
        float tt = MathF.Min(tc, stopT);
        Vector2 carry = v0 > 1e-4f ? _carry0 / v0 * (v0 * tt - 0.5f * k.PlantDecel * tt * tt) : Vector2.Zero;
        float follow = _dive is KeeperDive.Catch or KeeperDive.Feet ? 0.5f * k.BodyFollow : k.BodyFollow;
        var old = _k.Position;
        var pos = new Vector3(_feet0.X + carry.X + follow * (_hands.X - _home.X), 0f, _feet0.Z + carry.Y + follow * (_hands.Z - _home.Z));
        var vel = Vector3.Zero;
        _pitch.ClampPlayer(ref pos, ref vel);
        _k.Position = pos;
        if (dt > 0f) _k.Velocity = (pos - old) / dt;

        bool reached = reach >= dist - 1e-3f || reach >= Keeper.Stretch(_k.Sheet, _t) - 1e-3f;
        if (reached || _contact) _reachedFor += dt;
        float sinceReaction = _since - _reaction;
        bool standing = _dive is KeeperDive.Catch or KeeperDive.Feet;
        // He stays in the dive until the ball has had time to arrive, then lands.
        bool due = sinceReaction >= _targetT + _jitter + k.DiveLandSeconds;
        bool land = due && _reachedFor >= k.DiveLandSeconds && (!standing || sinceReaction >= k.StandSaveSeconds)
                    || sinceReaction >= MathF.Max(k.DiveMaxSeconds, _targetT + _jitter + k.DiveLandSeconds);
        if (!land) return;
        _active = false;
        if (standing) ToStand();
        else { Enter(Phase.Grounded); SetAction(KeeperAction.Grounded); }
    }

    void StartSmother(Vector3 point)
    {
        Enter(Phase.Smother);
        _active = true;
        _rushing = false;
        _fromBackpedal = false;
        _contact = false;
        if (!_struck) { _roundSmother = true; Report.Smothered = true; }
        _feet0 = _k.Position;
        _home = HomeFor(_k.Position);
        _carry0 = new Vector2(_k.Velocity.X, _k.Velocity.Z);
        _target = point;
        SetAction(KeeperAction.Smother);
        StepSmother(0f);
    }

    void StepSmother(float dt)
    {
        var k = _t.Keeper;
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

    /// <summary>
    /// If the ball crossed the keeper's save plane (through his hands, across the shot's direction) this tick without contact,
    /// records that he was beaten, with the distance he needed and the reach he had at that moment.
    /// </summary>
    internal void ObservePlane(Vector3 prev, Vector3 now)
    {
        if (!_active || _phase is not (Phase.Reacting or Phase.Diving)) return;
        Vector3 home = _phase == Phase.Reacting ? HomeFor(_k.Position) : _home;
        var h = _phase == Phase.Reacting ? new Vector2(home.X, home.Z) : _planePoint;
        float d0 = Vector2.Dot(new Vector2(prev.X, prev.Z) - h, _planeN);
        float d1 = Vector2.Dot(new Vector2(now.X, now.Z) - h, _planeN);
        if (!(d0 < 0f && d1 >= 0f)) return;
        float f = d0 / (d0 - d1);
        Vector3 at = Vector3.Lerp(prev, now, f);
        Record(at, home, KeeperResult.Beaten);
        _active = false;
    }

    void Record(Vector3 at, Vector3 home, KeeperResult action)
    {
        float needed = (at - home).Length();
        float reach = _phase == Phase.Diving ? Reach(DiveTau) : _phase == Phase.Reacting ? _span : 0f;
        Report.NeededM = needed;
        Report.ReachM = reach;
        Report.MarginM = reach - needed;
        Report.Action = action;
    }

    /// <summary>
    /// The ball's path met the hands (or the smothering body) at <paramref name="contact"/>. Held when it is slow enough for the
    /// save (standing, diving, at the feet or in a smother) and inside the catch zone; otherwise blocked (at the feet, in a smother,
    /// or before he reacted), tipped over the bar (high, near his line), fingertipped (at the edge of his stretch) or parried.
    /// Returns true if held.
    /// </summary>
    internal bool Contact(Ball ball, Vector3 contact, SimRng rng, out float speedAtContact, bool body = false)
    {
        var k = _t.Keeper;
        var sheet = _k.Sheet;
        Vector3 v = ball.Velocity;
        speedAtContact = v.Length();
        Vector3 home = _phase == Phase.Reacting ? HomeFor(_k.Position) : _home;
        float distance = (contact - home).Length();
        _active = false;
        _contact = true;
        bool smother = _phase == Phase.Smother;
        bool early = _phase == Phase.Reacting || _phase == Phase.Diving && DiveTau <= 0f;
        bool feet = _phase == Phase.Diving && _dive == KeeperDive.Feet;
        bool standing = early || _phase == Phase.Diving && _dive == KeeperDive.Catch;
        if (!_struck) { _roundWon = true; Report.WonBall = true; }
        float catchSpeed = _fromBackpedal && _phase == Phase.Diving ? Keeper.DivingCatchSpeed(sheet, _t) * k.BackpedalCatchFactor
            : body ? k.BodyHoldSpeed
            : smother ? k.SmotherHoldSpeed
            : feet ? Keeper.CatchSpeed(sheet, _t) * k.FeetCatchFactor
            : standing ? Keeper.CatchSpeed(sheet, _t)
            : Keeper.DivingCatchSpeed(sheet, _t);
        bool held = speedAtContact < catchSpeed && (smother || body || distance <= k.CatchZoneFraction * Keeper.Stretch(sheet, _t));
        if (held)
        {
            Record(contact, home, KeeperResult.Held);
            Report.Save = smother ? SaveKind.Smother : SaveKind.Catch;
            Enter(Phase.Held);
            SetAction(KeeperAction.Holding);
            _hands = contact;
            _k.HandPosition = _hands;
            ball.Velocity = Vector3.Zero;
            ball.Spin = Vector3.Zero;
            ball.Position = _hands;
            return true;
        }

        Record(contact, home, KeeperResult.Parried);
        if (smother || feet || early || body)
        {
            Block(ball, v, rng);
            Report.Save = SaveKind.Block;
            if (early) ToStand();
            return false;
        }
        bool tipOver = contact.Y >= k.TipOverBarHeight * _pitch.BarHeight && _pitch.GoalLineX - _k.Position.X <= k.TipOverBarDistance;
        bool fingertip = distance >= (1f - k.FingertipZoneFraction) * Keeper.Stretch(sheet, _t);
        if (tipOver) { TipOver(ball, v, contact, rng); Report.Save = SaveKind.TipOver; }
        else if (fingertip) { Fingertip(ball, v, contact, rng); Report.Save = SaveKind.Fingertip; }
        else { Parry(ball, v, contact, home, rng); Report.Save = SaveKind.Parry; }
        return false;
    }

    /// <summary>A parry: 40 to 60% of the speed (at least the minimum parry speed), back out and steered wide by handling.</summary>
    void Parry(Ball ball, Vector3 v, Vector3 contact, Vector3 home, SimRng rng)
    {
        var k = _t.Keeper;
        float handling = Rating.R01(_k.Sheet.Handling);
        var horiz = new Vector2(v.X, v.Z);
        Vector2 back = horiz.LengthSquared() > 1e-6f ? -Vector2.Normalize(horiz) : new Vector2(-1f, 0f);
        float dz = contact.Z - home.Z;
        float side = MathF.Abs(dz) > 0.05f ? MathF.Sign(dz) : (rng.NextFloat() < 0.5f ? -1f : 1f);
        Vector2 lateral = new(-back.Y, back.X);
        // Pick the lateral axis direction that points toward 'side' in world Z.
        if (MathF.Sign(lateral.Y) != side && MathF.Abs(lateral.Y) > 1e-3f) lateral = -lateral;
        Vector2 hd = back + lateral * (handling * k.ParryWideMax);
        float yaw = MathF.Atan2(hd.Y, hd.X) + rng.Normal(0f, k.ParryYawNoiseDeg) * MathF.PI / 180f;
        float speed = MathF.Max(k.MinParrySpeed, v.Length() * rng.Uniform(k.ParrySpeedMin, k.ParrySpeedMax));
        Vector3 dir = Vector3.Normalize(new Vector3(MathF.Cos(yaw), k.ParryLift, MathF.Sin(yaw)));
        ball.Velocity = dir * speed;
        ball.Spin = Vector3.Zero;
    }

    /// <summary>A block (feet, smother, or a ball that hits him before he reacts): back out within the block angle, a quarter to near half the speed, at least the block speed.</summary>
    void Block(Ball ball, Vector3 v, SimRng rng)
    {
        var k = _t.Keeper;
        var horiz = new Vector2(v.X, v.Z);
        Vector2 back = horiz.LengthSquared() > 1e-6f ? -Vector2.Normalize(horiz) : new Vector2(-1f, 0f);
        float yaw = MathF.Atan2(back.Y, back.X) + rng.Uniform(-k.BlockAngleDeg, k.BlockAngleDeg) * MathF.PI / 180f;
        float speed = MathF.Max(k.BlockSpeed, v.Length() * rng.Uniform(k.BlockSpeedMin, k.BlockSpeedMax));
        Vector3 dir = Vector3.Normalize(new Vector3(MathF.Cos(yaw), k.ParryLift, MathF.Sin(yaw)));
        ball.Velocity = dir * speed;
        ball.Spin = Vector3.Zero;
    }

    /// <summary>
    /// A tip over the bar: the ball keeps going toward the goal at 40 to 60% of its speed, pushed up steeply enough to clear the
    /// crossbar with room to spare.
    /// </summary>
    void TipOver(Ball ball, Vector3 v, Vector3 contact, SimRng rng)
    {
        var k = _t.Keeper;
        var h = new Vector2(v.X, v.Z);
        Vector2 dirH = h.LengthSquared() > 1e-6f ? Vector2.Normalize(h) : new Vector2(1f, 0f);
        if (dirH.X < 0.3f) dirH = Vector2.Normalize(new Vector2(0.3f, dirH.Y));
        float speed = MathF.Max(k.MinParrySpeed, v.Length() * rng.Uniform(k.ParrySpeedMin, k.ParrySpeedMax));
        float dx = MathF.Max(0.05f, _pitch.GoalLineX - contact.X);
        float clear = _pitch.BarHeight + _t.Ball.Radius + 0.3f;
        float g = 9.81f;
        float phi = 70f * MathF.PI / 180f;
        for (float deg = 20f; deg <= 70f; deg += 2.5f)
        {
            float a = deg * MathF.PI / 180f;
            float vx = speed * MathF.Cos(a) * dirH.X;
            float tl = dx / MathF.Max(0.1f, vx);
            float y = contact.Y + speed * MathF.Sin(a) * tl - 0.5f * g * tl * tl;
            if (y >= clear) { phi = a; break; }
        }
        ball.Velocity = new Vector3(dirH.X * MathF.Cos(phi), MathF.Sin(phi), dirH.Y * MathF.Cos(phi)) * speed;
        ball.Spin = Vector3.Zero;
    }

    /// <summary>
    /// A fingertip save: the ball keeps <c>fingertip_speed_kept</c> of its speed and its path bends away from the goal's centre by
    /// a half to the whole of <c>fingertip_max_bend_deg</c>. It can still go in.
    /// </summary>
    void Fingertip(Ball ball, Vector3 v, Vector3 contact, SimRng rng)
    {
        var k = _t.Keeper;
        float side = MathF.Abs(contact.Z) > 0.05f ? MathF.Sign(contact.Z) : (rng.NextFloat() < 0.5f ? -1f : 1f);
        float bend = rng.Uniform(0.5f, 1f) * k.FingertipMaxBendDeg * MathF.PI / 180f;
        var h = new Vector2(v.X, v.Z);
        float yaw = MathF.Atan2(h.Y, h.X);
        // Turning toward +Z increases the yaw for a ball heading +X; bend toward 'side'.
        yaw += side * bend;
        float hs = h.Length() * k.FingertipSpeedKept;
        ball.Velocity = new Vector3(MathF.Cos(yaw) * hs, v.Y * k.FingertipSpeedKept, MathF.Sin(yaw) * hs);
        ball.Spin *= k.FingertipSpeedKept;
    }
}
