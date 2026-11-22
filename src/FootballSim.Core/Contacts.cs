using System.Numerics;

namespace FootballSim.Core;

/// <summary>
/// Body contacts between outfield players: each is a disc of defending.body_radius_m, and two that overlap are pushed apart
/// along the line between them, the lighter one further. The boards hold both, so a pair pushed into a board is separated
/// along it instead of through it. A player who stumbles gives way: he is moved most of the way and the other keeps his run. The
/// keeper is not part of this (his contact with an attacker is the keeper's own code).
/// </summary>
public static class Contacts
{
    /// <summary>
    /// Separates <paramref name="a"/> and <paramref name="b"/> if their body discs overlap, and takes out of each one's velocity
    /// the part that runs into the other. Returns true if they touched.
    /// </summary>
    public static bool Separate(PlayerState a, PlayerState b, int tick, float now, Tuning t, Pitch? pitch)
    {
        float min = 2f * t.Defending.BodyRadiusM;
        var pa = new Vector2(a.Position.X, a.Position.Z);
        var pb = new Vector2(b.Position.X, b.Position.Z);
        var d = pb - pa;
        float dist = d.Length();
        if (dist >= min - 1e-5f) return false;
        // Standing on the same spot: apart along the way the first one faces.
        Vector2 n = dist > 1e-5f ? d / dist : new Vector2(MathF.Cos(a.Facing), MathF.Sin(a.Facing));
        float wa = MathF.Max(1f, a.Sheet.WeightKg), wb = MathF.Max(1f, b.Sheet.WeightKg);
        // One of them stumbling (and the other not): he counts as a quarter of his weight, and the other runs on through.
        bool sa = Defending.Stumbling(a, now), sb = Defending.Stumbling(b, now);
        if (sa != sb) { if (sa) wa *= StumbleWeight; else wb *= StumbleWeight; }
        float shareA = wb / (wa + wb);
        for (int pass = 0; pass < 3; pass++)
        {
            float overlap = min - Vector2.Dot(pb - pa, n);
            if (overlap <= 1e-5f) break;
            pa = Clamp(pa - n * (overlap * shareA), pitch);
            pb = Clamp(pb + n * (overlap * (1f - shareA)), pitch);
            // A board held one of them: the other takes what is left.
            overlap = min - Vector2.Dot(pb - pa, n);
            if (overlap <= 1e-5f) break;
            var tryB = Clamp(pb + n * overlap, pitch);
            if (min - Vector2.Dot(tryB - pa, n) <= 1e-5f) { pb = tryB; break; }
            pb = tryB;
            pa = Clamp(pa - n * (min - Vector2.Dot(pb - pa, n)), pitch);
            // Both held (a corner): slide them apart along the board instead.
            if (min - Vector2.Distance(pa, pb) > 1e-4f)
            {
                var side = new Vector2(-n.Y, n.X);
                float need = MathF.Sqrt(MathF.Max(0f, min * min - MathF.Pow(Vector2.Dot(pb - pa, n), 2f))) - Vector2.Dot(pb - pa, side);
                pa = Clamp(pa - side * (need * shareA), pitch);
                pb = Clamp(pb + side * (need * (1f - shareA)), pitch);
                var dd = pb - pa;
                if (dd.Length() > 1e-5f) n = dd / dd.Length();
            }
        }
        Place(a, pa, n, keepRun: sb && !sa);
        Place(b, pb, -n, keepRun: sa && !sb);
        a.Def.BumpTick = tick; a.Def.BumpDir = n;
        b.Def.BumpTick = tick; b.Def.BumpDir = -n;
        return true;
    }

    static Vector2 Clamp(Vector2 p, Pitch? pitch)
    {
        if (pitch == null) return p;
        var pos = new Vector3(p.X, 0f, p.Y);
        var vel = Vector3.Zero;
        pitch.ClampPlayer(ref pos, ref vel);
        return new Vector2(pos.X, pos.Z);
    }

    /// <summary>Moves a player to his separated spot and removes the part of his velocity toward the other (along <paramref name="toOther"/>).</summary>
    static void Place(PlayerState p, Vector2 at, Vector2 toOther, bool keepRun)
    {
        p.Position = new Vector3(at.X, 0f, at.Y);
        p.HandPosition = p.Position;
        if (keepRun) return;
        float into = p.Velocity.X * toOther.X + p.Velocity.Z * toOther.Y;
        if (into > 0f) p.Velocity -= new Vector3(toOther.X, 0f, toOther.Y) * into;
    }

    /// <summary>The share of his weight a stumbling player counts for against one who is not.</summary>
    const float StumbleWeight = 0.25f;
}
