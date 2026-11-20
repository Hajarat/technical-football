using System.Numerics;

namespace FootballSim.Core;

/// <summary>How eagerly a player without the ball goes for a loose ball this tick.</summary>
public enum RegainMode
{
    /// <summary>Not going for it: the legs follow the stick.</summary>
    None,
    /// <summary>Going for it in a race with an opponent: the legs run on to the ball, normal foot reach.</summary>
    Contested,
    /// <summary>Going for it with a clear head start: the legs run on to the ball and the regain reach applies.</summary>
    Free,
}

/// <summary>
/// The eager regain of a loose ball: the nearest players go for it by nature. A loose ball in front of a player (within
/// regain_cone_deg of his facing and regain_max_distance) turns his legs into the go-get-it legs, whatever the stick and
/// sprint button say, and he takes it with a first touch as soon as it is in reach, even with the stick released. With a
/// clear head start on the keeper (see <see cref="Contested"/>) he also takes it from as far as regain_reach (a toe-poke,
/// at the stretched errors). Holding the finesse modifier with the stick pushed away from the ball lets it run.
/// </summary>
public static class Regain
{
    /// <summary>
    /// The regain mode of player <paramref name="p"/> for a loose ball this tick. Call only while the ball is loose and play
    /// is live and the ball has been played this round (a ball loose at the start is not lost); the caller rules out a stunned
    /// player.
    /// </summary>
    public static RegainMode Assess(PlayerState p, in TickIntent intent, Ball ball, PlayerState keeper, Tuning t)
    {
        var k = t.Possession;
        var rel = new Vector2(ball.Position.X - p.Position.X, ball.Position.Z - p.Position.Z);
        float d = rel.Length();
        if (d > k.RegainMaxDistance) return RegainMode.None;
        // Within foot reach the ball counts as in front whatever the angle (it is at his feet).
        if (d > TouchModel.FootReach(p.Sheet, t))
        {
            float off = MathF.Abs(Movement.WrapAngle(MathF.Atan2(rel.Y, rel.X) - p.Facing));
            if (off > k.RegainConeDeg * MathF.PI / 180f) return RegainMode.None;
        }
        if (LetsRun(intent, rel, t)) return RegainMode.None;
        return Contested(p, ball, keeper, t) ? RegainMode.Contested : RegainMode.Free;
    }

    /// <summary>Let the ball run: the finesse modifier held with the stick pushed more than 90 deg away from the ball.</summary>
    public static bool LetsRun(in TickIntent intent, Vector2 toBall, Tuning t)
    {
        if (!intent.Finesse || intent.Move.Length() < t.Touch.StickThreshold) return false;
        return Vector2.Dot(intent.Move, toBall) < 0f;
    }

    /// <summary>
    /// True when the keeper challenges for the ball: he is within regain_keeper_distance of it, or he would get there no more
    /// than regain_keeper_margin seconds after the attacker. Each time is the gap beyond foot reach over a top speed (the
    /// keeper's rush speed, the attacker's sprint speed); a keeper on the ground or getting up is timed as if he could run
    /// at once, so a recovering keeper still counts as close.
    /// </summary>
    public static bool Contested(PlayerState p, Ball ball, PlayerState keeper, Tuning t)
    {
        var k = t.Possession;
        float dk = TouchModel.HorizontalDistance(keeper.Position, ball.Position);
        if (dk <= k.RegainKeeperDistance) return true;
        float reach = TouchModel.FootReach(p.Sheet, t);
        float ta = MathF.Max(0f, TouchModel.HorizontalDistance(p.Position, ball.Position) - reach) / MathF.Max(1f, Movement.TopSpeed(p.Sheet, t));
        float tk = MathF.Max(0f, dk - reach) / MathF.Max(1f, Keeper.RushSpeed(keeper.Sheet, t));
        return tk <= ta + k.RegainKeeperMargin;
    }

    /// <summary>True when a Free regain lets the player take the ball where it is now: low enough, and within regain_reach.</summary>
    public static bool InRegainReach(PlayerState p, Ball ball, Tuning t) =>
        p.Regain == RegainMode.Free && ball.Position.Y <= t.Touch.MaxBallHeight
        && TouchModel.HorizontalDistance(p.Position, ball.Position) <= t.Possession.RegainReach;
}
