using System.Numerics;

namespace FootballSim.Core;

/// <summary>Where a swept ball first touched the goal frame.</summary>
/// <param name="T">Fraction 0..1 along the swept path.</param>
/// <param name="Point">Ball centre at contact.</param>
/// <param name="Normal">Unit surface normal, pointing from the frame toward the ball.</param>
public readonly record struct FrameHit(float T, Vector3 Point, Vector3 Normal);

/// <summary>Where a swept ball first touched the arena boards or the ceiling.</summary>
/// <param name="T">Fraction 0..1 along the swept path.</param>
/// <param name="Point">Ball centre at contact.</param>
/// <param name="Normal">Unit surface normal, pointing from the board into the arena.</param>
public readonly record struct BoardHit(float T, Vector3 Point, Vector3 Normal);

/// <summary>
/// The arena and its goal. X runs from the back wall (0) to the goal-end wall (<see cref="GoalLineX"/>), Z from -HalfWidth to
/// +HalfWidth. Seen from above the outline is convex: the two back corners are quarter circles of <see cref="CornerRadius"/>, the
/// two goal-end corners are cut by a straight board <see cref="GoalChamfer"/> long along each wall, so a wide ball is sent back
/// toward the middle. The walls are effectively full height (the drawn boards are low; an invisible wall carries on above them)
/// up to a flat ceiling. The only gap is the goal mouth. The goal frame is two vertical posts and a crossbar, each a cylinder; the
/// goal mouth is centred on (GoalLineX, 0, 0). Lengths come from the scenario, the arena shape from the tuning.
/// </summary>
public sealed class Pitch
{
    /// <summary>
    /// Radius of a player's body on the ground, metres: players are kept this far inside the outline. There is no body model
    /// yet, so this is a plain constant rather than a rating.
    /// </summary>
    public const float PlayerRadius = 0.35f;

    /// <summary>X of the goal line, which is also the goal-end wall.</summary>
    public float GoalLineX { get; }
    /// <summary>X of the back wall.</summary>
    public float MinX => 0f;
    /// <summary>Half of the arena width; the side walls are at Z = plus or minus this.</summary>
    public float HalfWidth { get; }
    public float GoalHalfWidth { get; }
    public float BarHeight { get; }
    public float PostRadius { get; }
    public float FrameRestitution { get; }
    public float NetDepth { get; }
    /// <summary>Radius of the two rounded back corners, metres (0 = square corners).</summary>
    public float CornerRadius { get; }
    /// <summary>How far along each wall the straight board across a goal-end corner starts, metres (0 = square corners).</summary>
    public float GoalChamfer { get; }
    /// <summary>Height of the drawn boards, metres. Only the view uses it: the ball meets an invisible wall above them.</summary>
    public float BoardHeight { get; }
    /// <summary>Height of the ceiling, metres.</summary>
    public float Ceiling { get; }
    public float BoardRestitution { get; }
    public float BoardFriction { get; }

    // The outline's straight walls as outward unit normals (in the X-Z plane) and offsets: a point p is inside when
    // dot(n, p) <= c. Kept in a fixed order so sweeps are deterministic.
    readonly Vector2[] _wallN;
    readonly float[] _wallC;
    readonly int _endWall; // index of the goal-end wall, the one with the goal mouth in it

    public Pitch(PitchSpec spec, FrameTuning frame, ArenaTuning? arena = null)
    {
        arena ??= new ArenaTuning();
        GoalLineX = spec.Length;
        HalfWidth = spec.Width / 2f;
        GoalHalfWidth = spec.GoalWidth / 2f;
        BarHeight = spec.GoalHeight;
        PostRadius = frame.PostRadius;
        FrameRestitution = frame.Restitution;
        NetDepth = frame.NetDepth;
        // Shapes that would cross each other (a corner bigger than the arena) are cut down to the largest that fits.
        CornerRadius = Math.Clamp(arena.CornerRadius, 0f, MathF.Min(HalfWidth, GoalLineX / 2f));
        GoalChamfer = Math.Clamp(arena.GoalCornerChamfer, 0f, MathF.Max(0f, MathF.Min(HalfWidth - GoalHalfWidth, GoalLineX / 2f)));
        BoardHeight = arena.BoardHeight;
        Ceiling = MathF.Max(arena.Ceiling, BarHeight + 0.5f);
        BoardRestitution = arena.BoardRestitution;
        BoardFriction = arena.BoardFriction;

        var n = new List<Vector2> { new(-1f, 0f), new(0f, -1f), new(0f, 1f), new(1f, 0f) };
        var c = new List<float> { 0f, HalfWidth, HalfWidth, GoalLineX };
        _endWall = 3;
        if (GoalChamfer > 0f)
        {
            // The board from (L - C, +-W/2) to (L, +-(W/2 - C)): normal (1, +-1) / sqrt 2.
            float k = 1f / MathF.Sqrt(2f);
            float off = (GoalLineX + HalfWidth - GoalChamfer) * k;
            n.Add(new Vector2(k, k)); c.Add(off);
            n.Add(new Vector2(k, -k)); c.Add(off);
        }
        _wallN = n.ToArray();
        _wallC = c.ToArray();
    }

    /// <summary>Z half-extent of the straight part of the goal-end wall (between the two chamfers).</summary>
    public float EndWallHalfWidth => HalfWidth - GoalChamfer;

    /// <summary>True when the whole ball is past the goal line, between the posts and under the bar.</summary>
    public bool IsGoal(Vector3 centre, float radius) =>
        centre.X - radius > GoalLineX
        && MathF.Abs(centre.Z) + radius <= GoalHalfWidth
        && centre.Y + radius <= BarHeight;

    /// <summary>True for a ball centre inside the goal mouth opening: between the posts and under the bar.</summary>
    bool InMouth(Vector3 centre) => MathF.Abs(centre.Z) < GoalHalfWidth && centre.Y < BarHeight;

    /// <summary>
    /// True when a board contact at ball centre <paramref name="centre"/> was the ceiling, or the goal-end wall above the bar
    /// (a shot that went over rather than wide).
    /// </summary>
    public bool IsOverContact(Vector3 centre, float radius)
    {
        if (centre.Y >= Ceiling - radius - 1e-3f) return true;
        return centre.Y > BarHeight && centre.X >= GoalLineX - radius - 1e-3f && MathF.Abs(centre.Z) <= EndWallHalfWidth + 1e-3f;
    }

    /// <summary>
    /// Swept test of a ball of the given radius moving from <paramref name="from"/> to <paramref name="to"/> against the arena:
    /// the walls (full height), the rounded back corners, the goal-end chamfers and the ceiling. The goal mouth is open while the
    /// ball's centre is between the posts and under the bar (the frame sweep handles its edges). Returns the earliest contact
    /// along the whole path, so a fast ball cannot pass a board in one tick.
    /// </summary>
    public bool SweepBoards(Vector3 from, Vector3 to, float radius, out BoardHit hit)
    {
        hit = default;
        float best = float.MaxValue;
        Vector3 d = to - from;
        var p0 = new Vector2(from.X, from.Z);
        var d2 = new Vector2(d.X, d.Z);

        for (int i = 0; i < _wallN.Length; i++)
        {
            Vector2 n = _wallN[i];
            float ds = Vector2.Dot(n, d2);
            if (ds <= 0f) continue; // moving along or away from this wall
            float s0 = Vector2.Dot(n, p0) - (_wallC[i] - radius);
            float t = s0 >= 0f ? 0f : -s0 / ds;
            if (t > 1f || t >= best) continue;
            Vector3 at = from + d * t;
            if (i == _endWall && InMouth(at)) continue;
            best = t;
            hit = new BoardHit(t, at, new Vector3(-n.X, 0f, -n.Y));
        }

        if (CornerRadius > 0f)
        {
            float rr = CornerRadius - radius;
            for (int s = -1; s <= 1; s += 2)
            {
                // The circle's centre; the arc is the wall only in the corner's own quadrant (x below cx, |z| beyond |cz|).
                var c = new Vector2(CornerRadius, s * (HalfWidth - CornerRadius));
                if (!ExitCircle(p0, d2, c, rr, out float t) || t >= best) continue;
                Vector3 at = from + d * t;
                if (at.X >= c.X || s * at.Z <= s * c.Y) continue;
                var toCentre = c - new Vector2(at.X, at.Z);
                float len = toCentre.Length();
                Vector2 nn = len > 1e-6f ? toCentre / len : new Vector2(1f, -s);
                best = t;
                hit = new BoardHit(t, at, new Vector3(nn.X, 0f, nn.Y));
            }
        }

        if (d.Y > 0f)
        {
            float top = Ceiling - radius;
            float t = from.Y >= top ? 0f : (top - from.Y) / d.Y;
            if (t <= 1f && t < best)
            {
                best = t;
                hit = new BoardHit(t, from + d * t, -Vector3.UnitY);
            }
        }
        return best < float.MaxValue;
    }

    /// <summary>
    /// When a point moving from <paramref name="p0"/> by <paramref name="d"/> leaves a circle of radius R around
    /// <paramref name="c"/>. Already on or outside it and moving outward counts as t = 0.
    /// </summary>
    static bool ExitCircle(Vector2 p0, Vector2 d, Vector2 c, float R, out float t)
    {
        t = 0f;
        float a = Vector2.Dot(d, d);
        if (a < 1e-12f) return false;
        Vector2 f = p0 - c;
        float b = Vector2.Dot(f, d);
        float cc = Vector2.Dot(f, f) - R * R;
        if (cc >= 0f) return b > 0f;
        t = (-b + MathF.Sqrt(b * b - a * cc)) / a;
        return t <= 1f;
    }

    /// <summary>
    /// Keeps a player's centre inside the outline, <see cref="PlayerRadius"/> in from every board. The goal mouth is closed for
    /// players: they stop at the goal-end wall like anywhere else. Any velocity into a board it was pushed off is removed.
    /// Returns true if the position was changed.
    /// </summary>
    public bool ClampPlayer(ref Vector3 pos, ref Vector3 vel)
    {
        bool moved = false;
        // The region is convex, so a second pass settles a point pushed off one wall into a neighbouring one (a corner).
        for (int pass = 0; pass < 2; pass++)
        {
            var p = new Vector2(pos.X, pos.Z);
            for (int i = 0; i < _wallN.Length; i++)
            {
                Vector2 n = _wallN[i];
                float s = Vector2.Dot(n, p) - (_wallC[i] - PlayerRadius);
                if (s <= 0f) continue;
                p -= n * s;
                RemoveInto(ref vel, n);
                moved = true;
            }
            if (CornerRadius > 0f)
            {
                float rr = MathF.Max(0f, CornerRadius - PlayerRadius);
                for (int sgn = -1; sgn <= 1; sgn += 2)
                {
                    var c = new Vector2(CornerRadius, sgn * (HalfWidth - CornerRadius));
                    if (p.X >= c.X || sgn * p.Y <= sgn * c.Y) continue;
                    Vector2 f = p - c;
                    float len = f.Length();
                    if (len <= rr) continue;
                    Vector2 n = f / len;
                    p = c + n * rr;
                    RemoveInto(ref vel, n);
                    moved = true;
                }
            }
            if (!moved) return false;
            pos = new Vector3(p.X, pos.Y, p.Y);
        }
        return moved;
    }

    static void RemoveInto(ref Vector3 vel, Vector2 n)
    {
        float vn = vel.X * n.X + vel.Z * n.Y;
        if (vn > 0f) vel = new Vector3(vel.X - vn * n.X, vel.Y, vel.Z - vn * n.Y);
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
