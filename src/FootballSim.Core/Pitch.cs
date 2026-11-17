using System.Numerics;

namespace FootballSim.Core;

/// <summary>Where a swept ball first touched the goal frame.</summary>
/// <param name="T">Fraction 0..1 along the swept path.</param>
/// <param name="Point">Ball centre at contact.</param>
/// <param name="Normal">Unit surface normal, pointing from the frame toward the ball.</param>
public readonly record struct FrameHit(float T, Vector3 Point, Vector3 Normal);

/// <summary>
/// The half pitch and its goal. Dimensions come from the scenario. The goal frame is two vertical posts and a crossbar,
/// each a cylinder; the goal mouth is centred on (HalfLength, 0, 0).
/// </summary>
public sealed class Pitch
{
    /// <summary>X of the goal line.</summary>
    public float GoalLineX { get; }
    /// <summary>X of the back boundary (the halfway line); a ball wholly behind it is out.</summary>
    public float MinX => 0f;
    /// <summary>Half of the pitch width; touchlines are at Z = plus or minus this.</summary>
    public float HalfWidth { get; }
    public float GoalHalfWidth { get; }
    public float BarHeight { get; }
    public float PostRadius { get; }
    public float FrameRestitution { get; }
    public float NetDepth { get; }

    public Pitch(PitchSpec spec, FrameTuning frame)
    {
        GoalLineX = spec.HalfLength;
        HalfWidth = spec.Width / 2f;
        GoalHalfWidth = spec.GoalWidth / 2f;
        BarHeight = spec.GoalHeight;
        PostRadius = frame.PostRadius;
        FrameRestitution = frame.Restitution;
        NetDepth = frame.NetDepth;
    }

    /// <summary>True when the whole ball is past the goal line, between the posts and under the bar.</summary>
    public bool IsGoal(Vector3 centre, float radius) =>
        centre.X - radius > GoalLineX
        && MathF.Abs(centre.Z) + radius <= GoalHalfWidth
        && centre.Y + radius <= BarHeight;

    /// <summary>
    /// Judges the ball's position. Returns Goal, Wide, Over or Out once the whole ball is out of play (past the goal line,
    /// past a touchline, or behind the back boundary); otherwise <see cref="Outcome.None"/>.
    /// Past the goal line, a ball not wholly between the posts is Wide, one wholly between them but above the bar is Over.
    /// </summary>
    public Outcome Judge(Vector3 centre, float radius)
    {
        if (centre.X - radius > GoalLineX)
        {
            if (IsGoal(centre, radius)) return Outcome.Goal;
            if (MathF.Abs(centre.Z) + radius > GoalHalfWidth) return Outcome.Wide;
            if (centre.Y + radius > BarHeight) return Outcome.Over;
            return Outcome.Goal;
        }
        if (MathF.Abs(centre.Z) - radius > HalfWidth || centre.X + radius < MinX) return Outcome.Out;
        return Outcome.None;
    }

    /// <summary>
    /// Swept test of a ball of the given radius moving from <paramref name="from"/> to <paramref name="to"/> against both
    /// posts and the crossbar. Returns the earliest hit. Uses the whole path, so a fast ball cannot skip the frame in one tick.
    /// </summary>
    public bool SweepFrame(Vector3 from, Vector3 to, float radius, out FrameHit hit)
    {
        hit = default;
        float best = float.MaxValue;
        float rr = radius + PostRadius;
        Vector3 d = to - from;

        // Posts: vertical cylinders in the X-Z plane, from the ground to the bar.
        for (int s = -1; s <= 1; s += 2)
        {
            var c = new Vector2(GoalLineX, s * GoalHalfWidth);
            if (SweepCircle(new Vector2(from.X, from.Z), new Vector2(d.X, d.Z), c, rr, out float t, out Vector2 n)
                && t < best)
            {
                float y = from.Y + t * d.Y;
                if (y >= 0f && y <= BarHeight + PostRadius)
                {
                    best = t;
                    hit = new FrameHit(t, from + d * t, new Vector3(n.X, 0f, n.Y));
                }
            }
        }

        // Crossbar: horizontal cylinder along Z, in the X-Y plane.
        {
            var c = new Vector2(GoalLineX, BarHeight);
            if (SweepCircle(new Vector2(from.X, from.Y), new Vector2(d.X, d.Y), c, rr, out float t, out Vector2 n)
                && t < best)
            {
                float z = from.Z + t * d.Z;
                if (MathF.Abs(z) <= GoalHalfWidth + PostRadius)
                {
                    best = t;
                    hit = new FrameHit(t, from + d * t, new Vector3(n.X, n.Y, 0f));
                }
            }
        }
        return best < float.MaxValue;
    }

    /// <summary>Earliest contact of a moving point with a circle of radius R. Overlapping at the start counts as a hit at t = 0 only if moving inward.</summary>
    static bool SweepCircle(Vector2 p0, Vector2 d, Vector2 c, float R, out float t, out Vector2 n)
    {
        t = 0f;
        n = default;
        float a = Vector2.Dot(d, d);
        if (a < 1e-12f) return false;
        Vector2 f = p0 - c;
        float cc = Vector2.Dot(f, f) - R * R;
        float b = Vector2.Dot(f, d);
        if (cc <= 0f)
        {
            if (b >= 0f) return false;
            t = 0f;
            n = f.LengthSquared() > 1e-12f ? Vector2.Normalize(f) : -Vector2.Normalize(d);
            return true;
        }
        if (b >= 0f) return false;
        float disc = b * b - a * cc;
        if (disc < 0f) return false;
        t = (-b - MathF.Sqrt(disc)) / a;
        if (t < 0f || t > 1f) return false;
        n = Vector2.Normalize(f + d * t);
        return true;
    }
}
