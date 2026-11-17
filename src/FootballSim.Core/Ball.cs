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
/// The ball: a free sphere with gravity, drag, spin (Magnus) force, spin decay, bounce and rolling.
/// Semi-implicit Euler: velocity is updated from forces first, then position from the new velocity.
/// <see cref="Position"/> is the ball's centre, so a ball at rest on the ground has Y = radius.
/// </summary>
public sealed class Ball
{
    readonly BallTuning _t;

    /// <summary>Centre of the ball, metres.</summary>
    public Vector3 Position { get; internal set; }
    /// <summary>Metres per second.</summary>
    public Vector3 Velocity { get; internal set; }
    /// <summary>Angular velocity vector in radians per second (direction = axis, length = rate).</summary>
    public Vector3 Spin { get; internal set; }
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
                float dragForce = 0.5f * _t.AirDensity * _t.DragCoefficient * _t.CrossSection * speed * speed;
                a -= v / speed * (dragForce / _t.Mass);

                Vector3 wxv = Vector3.Cross(Spin, v);
                float cross = wxv.Length();
                if (cross > 1e-6f)
                {
                    float omegaAcross = cross / speed;
                    float cl = MathF.Min(_t.MaxLiftCoefficient, _t.Radius * omegaAcross / speed);
                    float magnus = 0.5f * _t.AirDensity * cl * _t.CrossSection * speed * speed;
                    a += wxv / cross * (magnus / _t.Mass);
                }
            }
            v += a * dt;
            Spin *= 1f - _t.SpinDecayPerSecond * dt;
        }
        else
        {
            var h = new Vector3(v.X, 0f, v.Z);
            float sp = h.Length();
            float dec = _t.RollDeceleration * dt;
            h = sp <= dec || sp < _t.RestSpeed ? Vector3.Zero : h * ((sp - dec) / sp);
            v = h;
            if (h == Vector3.Zero) Spin = Vector3.Zero;
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
                float vUp = vIn * _t.BounceRestitution;
                if (vUp < _t.MinBounceSpeed) vUp = 0f;
                vel = new Vector3(vel.X * _t.BounceSidewaysKept, vUp, vel.Z * _t.BounceSidewaysKept);
                Spin *= _t.BounceSpinKept;
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
