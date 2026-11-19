using System.Numerics;
using FootballSim.Core;

/// <summary>
/// The speed-step count every probe shares: ticks on which the attacker's ground speed |v| changes faster than
/// <see cref="Threshold"/> while he has the ball (in possession before and after the tick), shots excluded. Skill-move ticks
/// count, and are also counted on their own. The per-tick acceleration |dv|/dt over the same ticks is kept for its maximum.
/// </summary>
sealed class SpeedSteps
{
    /// <summary>A speed change faster than this is a step, m/s^2.</summary>
    public const float Threshold = 15f;

    public int Ticks, Steps, SkillSteps;
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
    public bool After(PlayerState p, float dt, bool measure = true)
    {
        if (!measure || !_had || !p.InPossession || p.Action is { Kind: >= ActionKind.Power }) return false;
        float sp = new Vector2(p.Velocity.X, p.Velocity.Z).Length(), sp0 = new Vector2(_v0.X, _v0.Z).Length();
        float acc = new Vector2(p.Velocity.X - _v0.X, p.Velocity.Z - _v0.Z).Length() / dt;
        float d = MathF.Abs(sp - sp0) / dt;
        Ticks++;
        _acc.Add(acc); _dspd.Add(d);
        AccMax = MathF.Max(AccMax, acc);
        DspdMax = MathF.Max(DspdMax, d);
        if (d <= Threshold) return false;
        Steps++;
        if (_skill0 || p.SkillMove != null) SkillSteps++;
        return true;
    }

    public void Add(SpeedSteps o)
    {
        Ticks += o.Ticks; Steps += o.Steps; SkillSteps += o.SkillSteps;
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
