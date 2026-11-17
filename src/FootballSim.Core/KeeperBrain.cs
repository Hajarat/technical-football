using System.Numerics;

namespace FootballSim.Core;

/// <summary>
/// The basic keeper's decision code: stand on the line from the ball to the goal centre, react to a strike after the reaction
/// time, predict where the ball crosses his plane, dive toward it with a reach that grows over time, and on contact catch or parry.
/// Not yet: rushing out, fingertip zone, positioning error, claiming loose balls.
/// </summary>
public sealed class KeeperBrain
{
    enum Phase { Set, Reacting, Diving, Held, Recovering }

    readonly Tuning _t;
    readonly Pitch _pitch;
    readonly PlayerState _k;
    readonly bool _onLine;

    Phase _phase = Phase.Set;
    float _since;
    float _reaction;
    Vector3 _home;
    Vector3 _target;
    Vector3 _hands;
    float _recover;
    bool _active; // a shot is being faced and not yet resolved

    /// <summary>The numbers for the most recent shot.</summary>
    public KeeperReport Report { get; private set; } = new();

    internal KeeperBrain(PlayerState keeper, Pitch pitch, Tuning tuning, bool onLine)
    {
        _k = keeper;
        _pitch = pitch;
        _t = tuning;
        _onLine = onLine;
        _hands = HomeFor(keeper.Position);
        keeper.HandPosition = _hands;
    }

    Vector3 HomeFor(Vector3 feet) => new(feet.X, _k.Sheet.HeightM * _t.Keeper.HomeHeightFraction, feet.Z);

    /// <summary>The sphere the ball's path is tested against: hands radius plus ball radius around the hands. Null when the hands are not live.</summary>
    internal SweepSphere? HandsSphere =>
        _active && _phase is Phase.Reacting or Phase.Diving
            ? new SweepSphere(_hands, _t.Keeper.HandRadius + _t.Ball.Radius)
            : null;

    /// <summary>X of the plane the keeper defends.</summary>
    public float PlaneX => _k.Position.X;

    /// <summary>The ball has just been struck: start reacting.</summary>
    internal void OnStrike(SimRng rng)
    {
        _phase = Phase.Reacting;
        _active = true;
        _since = 0f;
        _reaction = Keeper.DrawReaction(_k.Sheet, _t, rng);
        _home = HomeFor(_k.Position);
        _target = _home;
        Report = new KeeperReport { ReactionS = _reaction };
        _k.Velocity = Vector3.Zero;
    }

    /// <summary>Advances the keeper one tick.</summary>
    internal void Update(float dt, Ball ball, SimRng rng)
    {
        switch (_phase)
        {
            case Phase.Set:
                MoveToSpot(dt, ball);
                _hands = HomeFor(_k.Position);
                _k.KeeperAction = KeeperAction.Set;
                break;
            case Phase.Reacting:
                _since += dt;
                if (_since >= _reaction) Predict(ball, rng);
                UpdateHands(dt);
                break;
            case Phase.Diving:
                _since += dt;
                UpdateHands(dt);
                break;
            case Phase.Recovering:
                _recover -= dt;
                _k.KeeperAction = KeeperAction.Diving;
                if (_recover <= 0f)
                {
                    _phase = Phase.Set;
                    _k.KeeperAction = KeeperAction.Set;
                }
                break;
            case Phase.Held:
                _k.KeeperAction = KeeperAction.Holding;
                break;
        }
        _k.HandPosition = _hands;
    }

    void MoveToSpot(float dt, Ball ball)
    {
        Vector3 goal = Keeper.SetSpot(ball.Position, _pitch, _t, _onLine);
        Vector3 d = goal - _k.Position;
        float len = d.Length();
        float step = _t.Keeper.SetMoveSpeed * dt;
        Vector3 move = len <= step ? d : d / len * step;
        _k.Position += move;
        _k.Velocity = move / dt;
        _k.Facing = MathF.PI;
    }

    void Predict(Ball ball, SimRng rng)
    {
        _phase = Phase.Diving;
        _target = _home;
        if (ball.PredictPlaneCrossing(PlaneX, _t.Dt, 3f, out Vector3 p, out _))
        {
            float n = _t.Keeper.PredictionNoise;
            float z = p.Z + rng.Normal(0f, n);
            float y = MathF.Max(0f, p.Y + rng.Normal(0f, n));
            _target = new Vector3(PlaneX, y, z);
        }
    }

    void UpdateHands(float dt)
    {
        float reach = Keeper.ReachAt(_k.Sheet, _t, _since - _reaction);
        Vector3 toTarget = _target - _home;
        float dist = toTarget.Length();
        Vector3 hands = dist > 1e-4f ? _home + toTarget / dist * MathF.Min(reach, dist) : _home;
        _hands = hands;
        _k.KeeperAction = _since >= _reaction ? KeeperAction.Diving : KeeperAction.Reacting;

        float bodyZ = _home.Z + _t.Keeper.BodyFollow * (hands.Z - _home.Z);
        var old = _k.Position;
        _k.Position = new Vector3(old.X, 0f, bodyZ);
        _k.Velocity = (_k.Position - old) / dt;
    }

    /// <summary>
    /// If the ball crossed the keeper's plane this tick without contact, records that the keeper was beaten, with the
    /// distance he needed and the reach he had at that moment.
    /// </summary>
    internal void ObservePlane(Vector3 prev, Vector3 now)
    {
        if (!_active || _phase is Phase.Set or Phase.Held) return;
        if (!(prev.X < PlaneX && now.X >= PlaneX)) return;
        float f = (PlaneX - prev.X) / (now.X - prev.X);
        Vector3 at = Vector3.Lerp(prev, now, f);
        Record(at, KeeperResult.Beaten);
        _active = false;
    }

    void Record(Vector3 at, KeeperResult action)
    {
        float needed = (at - _home).Length();
        float reach = Keeper.ReachAt(_k.Sheet, _t, _since - _reaction);
        Report.NeededM = needed;
        Report.ReachM = reach;
        Report.MarginM = reach - needed;
        Report.Action = action;
    }

    /// <summary>
    /// The ball's path met the hands at <paramref name="contact"/>. Inside 70% of stretch and under the catch speed the ball
    /// is held; otherwise it is parried: it keeps 40 to 60% of its speed, a high handling rating steers it wide and a low one
    /// leaves it central. Returns true if held.
    /// </summary>
    internal bool Contact(Ball ball, Vector3 contact, SimRng rng, out float speedAtContact)
    {
        var k = _t.Keeper;
        var sheet = _k.Sheet;
        Vector3 v = ball.Velocity;
        speedAtContact = v.Length();
        float distance = (contact - _home).Length();
        bool held = speedAtContact < Keeper.CatchSpeed(sheet, _t)
                    && distance <= k.CatchZoneFraction * Keeper.Stretch(sheet, _t);
        _active = false;
        if (held)
        {
            Record(contact, KeeperResult.Held);
            _phase = Phase.Held;
            _k.KeeperAction = KeeperAction.Holding;
            ball.Velocity = Vector3.Zero;
            ball.Spin = Vector3.Zero;
            ball.Position = _hands;
            return true;
        }

        Record(contact, KeeperResult.Parried);
        float handling = Rating.R01(sheet.Handling);
        var horiz = new Vector2(v.X, v.Z);
        Vector2 back = horiz.LengthSquared() > 1e-6f ? -Vector2.Normalize(horiz) : new Vector2(-1f, 0f);
        float dz = contact.Z - _home.Z;
        float side = MathF.Abs(dz) > 0.05f ? MathF.Sign(dz) : (rng.NextFloat() < 0.5f ? -1f : 1f);
        Vector2 lateral = new(-back.Y, back.X);
        // Pick the lateral axis direction that points toward 'side' in world Z.
        if (MathF.Sign(lateral.Y) != side && MathF.Abs(lateral.Y) > 1e-3f) lateral = -lateral;
        Vector2 hd = back + lateral * (handling * k.ParryWideMax);
        float yaw = MathF.Atan2(hd.Y, hd.X) + rng.Normal(0f, k.ParryYawNoiseDeg) * MathF.PI / 180f;
        float speed = speedAtContact * rng.Uniform(k.ParrySpeedMin, k.ParrySpeedMax);
        Vector3 dir = Vector3.Normalize(new Vector3(MathF.Cos(yaw), k.ParryLift, MathF.Sin(yaw)));
        ball.Velocity = dir * speed;
        _phase = Phase.Recovering;
        _recover = k.RecoverSeconds;
        return false;
    }
}
