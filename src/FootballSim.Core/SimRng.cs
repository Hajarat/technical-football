using System.Numerics;

namespace FootballSim.Core;

/// <summary>
/// Small seeded random generator (xoshiro256** seeded through splitmix64). The same seed gives the same sequence on every
/// .NET version, which <c>System.Random</c> does not promise. Normal draws use the polar method and keep one spare value.
/// </summary>
public sealed class SimRng
{
    ulong _s0, _s1, _s2, _s3;
    bool _hasSpare;
    double _spare;

    /// <summary>Creates a generator from a seed.</summary>
    public SimRng(ulong seed)
    {
        ulong x = seed;
        _s0 = SplitMix(ref x);
        _s1 = SplitMix(ref x);
        _s2 = SplitMix(ref x);
        _s3 = SplitMix(ref x);
    }

    static ulong SplitMix(ref ulong x)
    {
        ulong z = x += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

    /// <summary>Next 64 random bits.</summary>
    public ulong NextUInt64()
    {
        ulong result = Rotl(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = Rotl(_s3, 45);
        return result;
    }

    /// <summary>Uniform double in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>Uniform float in [0, 1).</summary>
    public float NextFloat() => (NextUInt64() >> 40) * (1f / 16777216f);

    /// <summary>Uniform float in [min, max).</summary>
    public float Uniform(float min, float max) => min + (max - min) * NextFloat();

    /// <summary>Normal draw with the given mean and standard deviation.</summary>
    public float Normal(float mean = 0f, float sd = 1f)
    {
        if (sd == 0f) return mean;
        if (_hasSpare)
        {
            _hasSpare = false;
            return mean + sd * (float)_spare;
        }
        double u, v, s;
        do
        {
            u = NextDouble() * 2.0 - 1.0;
            v = NextDouble() * 2.0 - 1.0;
            s = u * u + v * v;
        } while (s >= 1.0 || s == 0.0);
        double m = Math.Sqrt(-2.0 * Math.Log(s) / s);
        _spare = v * m;
        _hasSpare = true;
        return mean + sd * (float)(u * m);
    }

    /// <summary>A uniformly random unit vector.</summary>
    public Vector3 UnitVector()
    {
        while (true)
        {
            var v = new Vector3(Normal(), Normal(), Normal());
            float l = v.Length();
            if (l > 1e-6f) return v / l;
        }
    }
}
