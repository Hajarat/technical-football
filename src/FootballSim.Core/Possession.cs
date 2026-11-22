using System.Numerics;

namespace FootballSim.Core;

/// <summary>A predicted touch: seconds from now, the foot and the expected ball centre. Eta is NaN when none is expected.</summary>
public readonly record struct TouchForecast(float Eta, Foot Foot, Vector3 Point)
{
    /// <summary>No touch expected.</summary>
    public static TouchForecast None => new(float.NaN, Foot.Right, Vector3.Zero);
}

/// <summary>
/// Possession rules that are not touches: when control can be lost, the speed cap, and the next-touch forecast a view reads.
/// In possession with no defender involved the ball is never lost by distance, except after a knock-on (once the chase is
/// over) or a heavy touch. Models take a player and the ball.
/// </summary>
public static class Possession
{
    /// <summary>True when the player's last touch lets the ball be lost by distance: a knock-on or a heavy touch.</summary>
    public static bool CanLoseByDistance(PlayerState p) => p.LastTouchKind is ActionKind.KnockOn or ActionKind.Heavy;

    /// <summary>
    /// Distance beyond which the ball counts as away from the player, when it can be lost by distance at all (see
    /// <see cref="CanLoseByDistance"/>): 2.0 m at walking pace to 3.5 m at a sprint. While the player chases a knock-on (see
    /// <see cref="ChasingKnockOn"/>) the ball is never away.
    /// </summary>
    public static float LoseDistance(PlayerState p, Tuning t)
    {
        if (!CanLoseByDistance(p) || ChasingKnockOn(p, t)) return float.PositiveInfinity;
        var k = t.Possession;
        float frac = Math.Clamp(p.Speed / Movement.TopSpeed(p.Sheet, t), 0f, 1f);
        return k.LoseDistanceSlow + (k.LoseDistanceFast - k.LoseDistanceSlow) * frac;
    }

    /// <summary>
    /// True while the player's last touch was a knock-on, its planned catch time plus the grace has not passed, and the player
    /// still runs along it at knock_on_chase_min_speed or faster. The next touch, the grace running out, or easing off ends
    /// the chase; then the normal lose distance applies.
    /// </summary>
    public static bool ChasingKnockOn(PlayerState p, Tuning t) =>
        p.KnockOnFrom == p.LastTouchTime
        && p.MoveTime < p.KnockOnUntil
        && Vector2.Dot(new Vector2(p.Velocity.X, p.Velocity.Z), p.KnockOnDirection) >= t.Touch.KnockOnChaseMinSpeed;

    /// <summary>Legs at a full sprint: the speed dribbling style (what the go-get-it legs and a skill move's legs run with).</summary>
    internal static TickIntent SpeedStyleLegs => new() { Sprint = 1f };

    /// <summary>
    /// The speed cap: off-ball top speed off possession or while chasing a knock-on; in possession the with-ball cap of the
    /// dribbling style <paramref name="legs"/> ask for (close into speed by the sprint input), or 97% of top speed during a
    /// recovery run. Through a sprint push that was cut short near an opponent the with-ball cap is lowered by its
    /// danger_speed factor (see <see cref="DangerSlow"/>). The legs given here are the ones moved under the cap, so a plan and
    /// the step it plans for agree.
    /// </summary>
    internal static float SpeedCap(PlayerState p, bool inPossession, bool recovering, in TickIntent legs, Tuning t)
    {
        if (!inPossession || ChasingKnockOn(p, t)) return Movement.TopSpeed(p.Sheet, t);
        // A skill move's exit burst lifts the with-ball cap a little while it lasts.
        float burst = p.MoveTime < p.SkillBurstUntil ? Movement.TopSpeed(p.Sheet, t) * p.SkillBurstCap : 0f;
        float withBall = Movement.TopSpeedWithBall(p.Sheet, legs, t) * DangerSlow(p);
        if (recovering) return MathF.Max(withBall + burst, Movement.TopSpeed(p.Sheet, t) * t.Possession.ChaseSpeedFraction);
        return withBall + burst;
    }

    /// <summary>
    /// The factor on the with-ball speed cap while the player's last touch is a sprint push that was cut short near an
    /// opponent: danger_speed by the share of the push that was cut, 1 otherwise. It lapses with his next touch or strike.
    /// </summary>
    internal static float DangerSlow(PlayerState p) => p.DangerSlowFrom == p.LastTouchTime ? p.DangerSlowFactor : 1f;

    /// <summary>
    /// The next touch of a player in possession: the committed touch (seconds to its contact tick, its foot and its contact
    /// point), else the swing the touch controller steers for (its forecast, updated every tick). <see cref="TouchForecast.None"/>
    /// when none is expected.
    /// </summary>
    public static TouchForecast PredictNextTouch(PlayerState p, int tick, bool inPossession, Tuning t)
    {
        if (!inPossession) return TouchForecast.None;
        if (p.TouchAction is { Pending: true } a) return new TouchForecast(a.ContactEta(tick, t.Dt), a.Foot, a.ContactPoint);
        if (p.ForecastTick < tick) return TouchForecast.None;
        return new TouchForecast((p.ForecastTick - tick) * t.Dt, p.ForecastFoot, p.ForecastPoint);
    }
}
