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
}

/// <summary>
/// The ball: a free sphere with gravity, speed-dependent drag, spin (Magnus) force, spin decay, bounce with friction and rolling.
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
            float sp = h.Length();
            float dec = _t.RollDeceleration * dt;
            h = sp <= dec || sp < _t.RestSpeed ? Vector3.Zero : h * ((sp - dec) / sp);
            v = h;
            // Rolling without slipping: the contact point (radius below the centre) is at rest, so spin = up x v / radius.
            Spin = h == Vector3.Zero ? Vector3.Zero : Vector3.Cross(Vector3.UnitY, h) / _t.Radius;
        }
        Velocity = v;
    }

    /// <summary>
    /// Advances the ball one tick. <paramref name="pitch"/> supplies the goal frame (null = none). <paramref name="sphere"/> is an
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
            float tFrame = float.MaxValue, tSphere = float.MaxValue;
            FrameHit fh = default;
            bool frame = pitch != null && pitch.SweepFrame(pos, pos + d, _t.Radius, out fh);
            if (frame) tFrame = fh.T;
            if (sphere is { } sp && SweepPointSphere(pos, d, sp, out float ts)) tSphere = ts;

            if (tSphere < float.MaxValue && tSphere <= tFrame)
            {
                pos += d * tSphere;
                result.SphereHit = true;
                result.SpherePoint = pos;
                Position = pos;
                Velocity = vel;
                return result;
            }
            if (frame)
            {
                pos = fh.Point + fh.Normal * 1e-4f;
                float vn = Vector3.Dot(vel, fh.Normal);
                if (vn < 0f) vel -= (1f + pitch!.FrameRestitution) * vn * fh.Normal;
                remaining *= 1f - fh.T;
                if (!result.FrameHit) { result.FrameHit = true; result.FramePoint = fh.Point; }
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
                Spin = GroundFriction(vel, Spin, vIn + vUp, out var vt);
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
    /// Coulomb friction at a ground bounce. <paramref name="normalImpulse"/> is the vertical speed change (per unit mass). The contact
    /// point's slip is u = v_t + spin x (0, -r, 0); friction acts against u, up to mu * normalImpulse, and never past the impulse that
    /// stops the slip (the ball then leaves rolling). Returns the new spin; <paramref name="vt"/> is the new horizontal velocity.
    /// </summary>
    Vector3 GroundFriction(Vector3 vel, Vector3 spin, float normalImpulse, out Vector3 vt)
    {
        vt = new Vector3(vel.X, 0f, vel.Z);
        var contact = new Vector3(0f, -_t.Radius, 0f);
        Vector3 u = vt + Vector3.Cross(spin, contact);
        float slip = u.Length();
        if (slip < 1e-6f) return spin;
        float k = _t.InertiaFactor;
        // Impulse per unit mass that brings the slip to zero: each unit cuts the slip by (1 + k) / k.
        float j = MathF.Min(_t.BounceFriction * normalImpulse, slip * k / (1f + k));
        Vector3 impulse = u * (-j / slip);
        vt += impulse;
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
