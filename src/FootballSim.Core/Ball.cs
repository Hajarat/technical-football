using System.Numerics;

namespace FootballSim.Core;

/// <summary>A sphere the ball's path is tested against (the keeper's hands, grown by the ball radius).</summary>
public readonly record struct SweepSphere(Vector3 Center, float Radius);

/// <summary>What happened to the ball during one step.</summary>
public struct BallStepResult
{
    public bool FrameHit;
    public Vector3 FramePoint;
    public bool Bounced;
    public Vector3 BouncePoint;
    public float BounceSpeed;
    /// <summary>The path met the supplied sphere; the ball stopped at <see cref="SpherePoint"/> and the caller decides what happens.</summary>
    public bool SphereHit;
    public Vector3 SpherePoint;
    /// <summary>The ball met an arena board or the ceiling; the first contact of the tick is reported.</summary>
    public bool BoardHit;
    /// <summary>Ball centre at the first board contact.</summary>
    public Vector3 BoardPoint;
    /// <summary>Ball speed arriving at the first board contact, m/s.</summary>
    public float BoardSpeed;
}

/// <summary>
/// The ball: a free sphere with gravity, speed-dependent drag, spin (Magnus) force, spin decay, bounce with friction, and on the
/// ground a skid (sliding friction while the contact point slips) that turns into rolling.
/// Semi-implicit Euler: velocity is updated from forces first, then position from the new velocity.
/// <see cref="Position"/> is the ball's centre, so a ball at rest on the ground has Y = radius.
/// </summary>
public sealed class Ball
{
    readonly BallTuning _t;
    Vector3 _spin;

    /// <summary>Centre of the ball, metres.</summary>
    public Vector3 Position { get; internal set; }
    /// <summary>Metres per second.</summary>
    public Vector3 Velocity { get; internal set; }
    /// <summary>Angular velocity vector in radians per second (direction = axis, length = rate), capped at <see cref="BallTuning.MaxSpin"/>.</summary>
    public Vector3 Spin
    {
        get => _spin;
        internal set
        {
            float max = _t.MaxSpin, sq = value.LengthSquared();
            _spin = max > 0f && sq > max * max ? value * (max / MathF.Sqrt(sq)) : value;
        }
    }
    public float Radius => _t.Radius;

    public Ball(BallTuning tuning) { _t = tuning; }

    /// <summary>A copy of the state, for prediction.</summary>
    internal Ball Clone() => new(_t) { Position = Position, Velocity = Velocity, Spin = Spin };

    /// <summary>Makes this ball a copy of <paramref name="o"/>'s state (a reusable prediction copy).</summary>
    internal Ball CopyFrom(Ball o) { Position = o.Position; Velocity = o.Velocity; _spin = o._spin; return this; }

    Ball? _scratchA, _scratchB;

    /// <summary>A reusable copy of this ball's current state for predictions (same tuning). Two independent copies.</summary>
    internal Ball ScratchA() => (_scratchA ??= new Ball(_t)).CopyFrom(this);
    internal Ball ScratchB() => (_scratchB ??= new Ball(_t)).CopyFrom(this);

    /// <summary>True when the ball is resting on or rolling along the ground.</summary>
    public bool OnGround => Position.Y <= _t.Radius + 1e-3f && Velocity.Y <= 0.01f;

    /// <summary>Applies forces for one tick and updates velocity (not position).</summary>
    internal void ApplyForces(float dt)
    {
        Vector3 v = Velocity;
        bool ground = OnGround;
        if (!ground)
        {
            Vector3 a = new(0f, -_t.Gravity, 0f);
            float speed = v.Length();
            if (speed > 1e-4f)
            {
                float dragForce = 0.5f * _t.AirDensity * _t.DragCurve.Evaluate(speed) * _t.CrossSection * speed * speed;
                a -= v / speed * (dragForce / _t.Mass);

                Vector3 wxv = Vector3.Cross(Spin, v);
                float cross = wxv.Length();
                if (cross > 1e-6f)
                {
                    float omegaAcross = cross / speed;
                    float cl = _t.LiftCurve.Evaluate(_t.Radius * omegaAcross / speed);
                    float magnus = 0.5f * _t.AirDensity * cl * _t.CrossSection * speed * speed;
                    a += wxv / cross * (magnus / _t.Mass);
                }
            }
            v += a * dt;
            Spin *= MathF.Exp(-_t.SpinDecayPerSecond * dt);
        }
        else
        {
            var h = new Vector3(v.X, 0f, v.Z);
            var contact = new Vector3(0f, -_t.Radius, 0f);
            Vector3 u = h + Vector3.Cross(Spin, contact);
            float slip = u.Length();
            if (slip > SkidSlipEpsilon && _t.SkidFriction > 0f)
            {
                // Skidding: sliding friction mu * g against the contact point's slip, on both the velocity and the spin (the
                // same inertia model as a bounce), never past the impulse that stops the slip; then the ball rolls.
                float k = _t.InertiaFactor;
                float j = MathF.Min(_t.SkidFriction * _t.Gravity * dt, slip * k / (1f + k));
                Vector3 impulse = u * (-j / slip);
                h += impulse;
                Vector3 spin = Spin + Vector3.Cross(contact, impulse) / (k * _t.Radius * _t.Radius);
                if (j >= slip * k / (1f + k)) spin = new Vector3(0f, spin.Y, 0f) + Vector3.Cross(Vector3.UnitY, h) / _t.Radius;
                Spin = spin;
                v = h;
            }
            else
            {
                float sp = h.Length();
                float dec = _t.RollDeceleration * dt;
                h = sp <= dec || sp < _t.RestSpeed ? Vector3.Zero : h * ((sp - dec) / sp);
                v = h;
                // Rolling without slipping: the contact point (radius below the centre) is at rest, so spin = up x v / radius.
                Spin = h == Vector3.Zero ? Vector3.Zero : Vector3.Cross(Vector3.UnitY, h) / _t.Radius;
            }
        }
        Velocity = v;
    }

    /// <summary>Contact-point slip below which a ball on the ground counts as rolling, m/s.</summary>
    const float SkidSlipEpsilon = 1e-3f;

    /// <summary>True while the ball is on the ground and its contact point slips (it skids before it rolls).</summary>
    public bool Skidding => OnGround && (new Vector3(Velocity.X, 0f, Velocity.Z) + Vector3.Cross(Spin, new Vector3(0f, -_t.Radius, 0f))).Length() > SkidSlipEpsilon;

    /// <summary>The spin of a ball rolling without slipping at ground velocity <paramref name="v"/>.</summary>
    internal Vector3 RollingSpin(Vector3 v) => Vector3.Cross(Vector3.UnitY, new Vector3(v.X, 0f, v.Z)) / _t.Radius;

    /// <summary>
    /// Where this ball comes to rest on the ground, ignoring the frame and all players, or its position after
    /// <paramref name="maxSeconds"/> if it is still moving then.
    /// </summary>
    internal Vector3 PredictRest(float dt, float maxSeconds)
    {
        if (OnGround && Velocity == Vector3.Zero) return Position;
        var b = Clone();
        int steps = (int)(maxSeconds / dt);
        for (int i = 0; i < steps && b.Velocity != Vector3.Zero; i++) b.Step(dt, null, null);
        return b.Position;
    }

    /// <summary>
    /// Advances the ball one tick. <paramref name="pitch"/> supplies the goal frame and the arena boards (null = neither). <paramref name="sphere"/> is an
    /// extra obstacle such as the keeper's hands: the ball stops at the first contact and reports it.
    /// Movement is swept along the whole path, so a 36 m/s ball (0.3 m per tick) cannot skip a post or a hand.
    /// </summary>
    internal BallStepResult Step(float dt, Pitch? pitch, SweepSphere? sphere)
    {
        var result = new BallStepResult();
        ApplyForces(dt);

        float remaining = 1f;
        Vector3 pos = Position;
        Vector3 vel = Velocity;
        Vector3 full = vel * dt;
        for (int iter = 0; iter < 4 && remaining > 1e-4f; iter++)
        {
            Vector3 d = full * remaining;
            float tFrame = float.MaxValue, tSphere = float.MaxValue, tBoard = float.MaxValue;
            FrameHit fh = default;
            BoardHit bh = default;
            bool frame = pitch != null && pitch.SweepFrame(pos, pos + d, _t.Radius, out fh);
            if (frame) tFrame = fh.T;
            bool board = pitch != null && pitch.SweepBoards(pos, pos + d, _t.Radius, out bh);
            if (board) tBoard = bh.T;
            if (sphere is { } sp && SweepPointSphere(pos, d, sp, out float ts)) tSphere = ts;

            if (tSphere < float.MaxValue && tSphere <= tFrame && tSphere <= tBoard)
            {
                pos += d * tSphere;
                result.SphereHit = true;
                result.SpherePoint = pos;
                Position = pos;
                Velocity = vel;
                return result;
            }
            if (frame && tFrame <= tBoard)
            {
                pos = fh.Point + fh.Normal * 1e-4f;
                float vn = Vector3.Dot(vel, fh.Normal);
                if (vn < 0f) vel -= (1f + pitch!.FrameRestitution) * vn * fh.Normal;
                remaining *= 1f - fh.T;
                // The rest of the tick continues along the new velocity.
                full = vel * dt;
                if (!result.FrameHit) { result.FrameHit = true; result.FramePoint = fh.Point; }
                continue;
            }
            if (board)
            {
                pos = bh.Point + bh.Normal * 1e-4f;
                float vn = Vector3.Dot(vel, bh.Normal);
                if (vn < 0f)
                {
                    if (!result.BoardHit) { result.BoardHit = true; result.BoardPoint = bh.Point; result.BoardSpeed = vel.Length(); }
                    vel = BoardBounce(vel, bh.Normal, vn, pitch!);
                }
                remaining *= 1f - bh.T;
                full = vel * dt;
                continue;
            }
            pos += d;
            remaining = 0f;
        }

        // Ground.
        if (pos.Y < _t.Radius)
        {
            pos.Y = _t.Radius;
            if (vel.Y < 0f)
            {
                float vIn = -vel.Y;
                float vUp = vIn * _t.RestitutionCurve.Evaluate(vIn);
                if (vUp < _t.MinBounceSpeed) vUp = 0f;
                Spin = ContactFriction(new Vector3(vel.X, 0f, vel.Z), Spin, new Vector3(0f, -_t.Radius, 0f), _t.BounceFriction, vIn + vUp, out var vt);
                vel = new Vector3(vt.X, vUp, vt.Z);
                if (vIn >= _t.BounceEventSpeed)
                {
                    result.Bounced = true;
                    result.BouncePoint = pos;
                    result.BounceSpeed = vIn;
                }
            }
        }
        Position = pos;
        Velocity = vel;
        return result;
    }

    /// <summary>
    /// A bounce off a board or the ceiling with unit normal <paramref name="n"/> (into the arena) and normal speed
    /// <paramref name="vn"/> (negative, into the board): the normal speed comes back scaled by the board restitution, and the
    /// same Coulomb friction as a ground bounce acts at the contact point on the board, changing the along-board speed and spin.
    /// </summary>
    Vector3 BoardBounce(Vector3 vel, Vector3 n, float vn, Pitch pitch)
    {
        float e = pitch.BoardRestitution;
        Vector3 tangential = vel - n * vn;
        Spin = ContactFriction(tangential, Spin, n * -_t.Radius, pitch.BoardFriction, (1f + e) * -vn, out var vt);
        return vt - n * (e * vn);
    }

    /// <summary>
    /// Coulomb friction at a bounce, for any contact surface. <paramref name="vt"/> is the velocity along the surface,
    /// <paramref name="contact"/> the offset from the ball's centre to the contact point (radius times minus the surface normal),
    /// <paramref name="normalImpulse"/> the normal speed change (per unit mass). The contact point's slip is u = v_t + spin x
    /// contact; friction acts against u, up to mu * normalImpulse, and never past the impulse that stops the slip (the ball then
    /// leaves rolling along the surface). Returns the new spin; <paramref name="vtOut"/> is the new velocity along the surface.
    /// </summary>
    Vector3 ContactFriction(Vector3 vt, Vector3 spin, Vector3 contact, float mu, float normalImpulse, out Vector3 vtOut)
    {
        vtOut = vt;
        Vector3 u = vt + Vector3.Cross(spin, contact);
        float slip = u.Length();
        if (slip < 1e-6f) return spin;
        float k = _t.InertiaFactor;
        // Impulse per unit mass that brings the slip to zero: each unit cuts the slip by (1 + k) / k.
        float j = MathF.Min(mu * normalImpulse, slip * k / (1f + k));
        Vector3 impulse = u * (-j / slip);
        vtOut += impulse;
        // Angular: delta omega = (r_contact x J) / I, with I = k m r^2 and J per unit mass.
        return spin + Vector3.Cross(contact, impulse) / (k * _t.Radius * _t.Radius);
    }

    static bool SweepPointSphere(Vector3 p0, Vector3 d, SweepSphere s, out float t)
    {
        t = 0f;
        Vector3 f = p0 - s.Center;
        float cc = Vector3.Dot(f, f) - s.Radius * s.Radius;
        if (cc <= 0f) return true;
        float a = Vector3.Dot(d, d);
        if (a < 1e-12f) return false;
        float b = Vector3.Dot(f, d);
        if (b >= 0f) return false;
        float disc = b * b - a * cc;
        if (disc < 0f) return false;
        t = (-b - MathF.Sqrt(disc)) / a;
        return t >= 0f && t <= 1f;
    }

    /// <summary>
    /// Predicts where this ball crosses the plane X = <paramref name="planeX"/>, ignoring the frame and all players.
    /// Returns false if it does not get there within <paramref name="maxSeconds"/>.
    /// </summary>
    internal bool PredictPlaneCrossing(float planeX, float dt, float maxSeconds, out Vector3 point, out float seconds)
    {
        var b = Clone();
        point = default;
        seconds = 0f;
        if (b.Position.X >= planeX) return false;
        int steps = (int)(maxSeconds / dt);
        for (int i = 0; i < steps; i++)
        {
            Vector3 prev = b.Position;
            b.Step(dt, null, null);
            if (b.Position.X >= planeX)
            {
                float f = (planeX - prev.X) / (b.Position.X - prev.X);
                point = Vector3.Lerp(prev, b.Position, f);
                seconds = (i + f) * dt;
                return true;
            }
            if (b.Velocity.X <= 0f && b.OnGround) return false;
        }
        return false;
    }
}
