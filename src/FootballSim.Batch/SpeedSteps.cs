using System.Numerics;
using FootballSim.Core;

/// <summary>
/// The speed-step count every probe shares: ticks on which the attacker's ground speed |v| changes faster than
/// <see cref="Threshold"/> while he has the ball (in possession before and after the tick), shots excluded. Skill-move ticks
/// count, and are also counted on their own; ticks on which the arena boards stopped him are counted apart (<see cref="Wall"/>). The per-tick acceleration |dv|/dt over the same ticks is kept for its maximum.
/// </summary>
sealed class SpeedSteps
{
    /// <summary>A speed change faster than this is a step, m/s^2.</summary>
    public const float Threshold = 15f;

    public int Ticks, Steps, SkillSteps, Wall;
    /// <summary>
    /// Ticks from a shoot press to 0.5 s after the strike (in possession or not), and the speed steps among them (the boards
    /// apart): the <c>shot</c> column.
    /// </summary>
    public int ShotTicks, ShotSteps;
    int _shotUntil = int.MinValue;
    public float AccMax, DspdMax;
    readonly List<float> _acc = new();
    readonly List<float> _dspd = new();
    Vector3 _v0;
    bool _had;
    bool _skill0;

    /// <summary>Call before the world steps.</summary>
    public void Before(PlayerState p)
    {
        _v0 = p.Velocity;
        _had = p.InPossession;
        _skill0 = p.SkillMove != null;
    }

    /// <summary>Call after the world steps; <paramref name="measure"/> false skips the tick (a warm-up). Returns true on a step.</summary>
    /// <param name="measureShot">Whether the tick counts for the shot column (default: <paramref name="measure"/>).</param>
    public bool After(PlayerState p, float dt, bool measure = true, bool? measureShot = null)
    {
        if (p.Action is { IsShot: true } shot && shot.Made) _shotUntil = shot.ContactTick + (int)MathF.Round(0.5f / dt);
        if ((measureShot ?? measure) && (p.Action is { IsShot: true } || p.CurrentTick <= _shotUntil))
        {
            ShotTicks++;
            float s1 = new Vector2(p.Velocity.X, p.Velocity.Z).Length(), s0 = new Vector2(_v0.X, _v0.Z).Length();
            if (MathF.Abs(s1 - s0) / dt > Threshold && !Walled(p)) ShotSteps++;
        }
        if (!measure || !_had || !p.InPossession || p.Action is { Kind: >= ActionKind.Power }) return false;
        float sp = new Vector2(p.Velocity.X, p.Velocity.Z).Length(), sp0 = new Vector2(_v0.X, _v0.Z).Length();
        float acc = new Vector2(p.Velocity.X - _v0.X, p.Velocity.Z - _v0.Z).Length() / dt;
        float d = MathF.Abs(sp - sp0) / dt;
        Ticks++;
        _acc.Add(acc); _dspd.Add(d);
        AccMax = MathF.Max(AccMax, acc);
        DspdMax = MathF.Max(DspdMax, d);
        if (d <= Threshold) return false;
        // The boards stopped him (his velocity is not his run plus the steering): counted on its own, not as a step.
        if (Walled(p)) { Wall++; return false; }
        Steps++;
        if (_skill0 || p.SkillMove != null) SkillSteps++;
        return true;
    }

    /// <summary>The boards stopped him this tick: his velocity is not his run plus the steering.</summary>
    static bool Walled(PlayerState p)
    {
        var fwd = new Vector2(MathF.Cos(p.Facing), MathF.Sin(p.Facing));
        var moved = fwd * p.RunSpeed + new Vector2(p.SteerVelocity.X, p.SteerVelocity.Z);
        return Vector2.Distance(moved, new Vector2(p.Velocity.X, p.Velocity.Z)) > 0.05f;
    }

    public void Add(SpeedSteps o)
    {
        Ticks += o.Ticks; Steps += o.Steps; SkillSteps += o.SkillSteps; Wall += o.Wall; ShotTicks += o.ShotTicks; ShotSteps += o.ShotSteps;
        AccMax = MathF.Max(AccMax, o.AccMax); DspdMax = MathF.Max(DspdMax, o.DspdMax);
        _acc.AddRange(o._acc); _dspd.AddRange(o._dspd);
    }

    public float AccP99 => P(_acc, 0.99);
    public float DspdP99 => P(_dspd, 0.99);
    public IReadOnlyList<float> Acc => _acc;
    public IReadOnlyList<float> Dspd => _dspd;

    /// <summary>Steps per 1000 measured ticks.</summary>
    public double PerThousand => 1000.0 * Steps / Math.Max(1, Ticks);

    static float P(List<float> l, double q)
    {
        if (l.Count == 0) return float.NaN;
        var s = new List<float>(l); s.Sort();
        return s[Math.Min(s.Count - 1, (int)(q * s.Count))];
    }
}
