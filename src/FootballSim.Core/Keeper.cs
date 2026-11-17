using System.Numerics;

namespace FootballSim.Core;

/// <summary>Keeper numbers derived from ratings. The decision code is in <see cref="KeeperBrain"/>.</summary>
public static class Keeper
{
    /// <summary>Mean reaction time, seconds (reflexes).</summary>
    public static float ReactionMean(PlayerSheet s, Tuning t) => t.Keeper.Reaction.ForRating(s.Reflexes);

    /// <summary>Lowest and highest reaction time the model can draw (noise clamped to a few sigma).</summary>
    public static (float Min, float Max) ReactionBounds(PlayerSheet s, Tuning t)
    {
        float mean = ReactionMean(s, t);
        float span = t.Keeper.ReactionNoise * t.Keeper.ReactionNoiseClampSigma;
        return (MathF.Max(t.Keeper.MinReaction, mean - span), mean + span);
    }

    /// <summary>Draws a reaction time: the mean plus clamped normal noise.</summary>
    public static float DrawReaction(PlayerSheet s, Tuning t, SimRng rng)
    {
        float span = t.Keeper.ReactionNoise * t.Keeper.ReactionNoiseClampSigma;
        float noise = Math.Clamp(rng.Normal(0f, t.Keeper.ReactionNoise), -span, span);
        return MathF.Max(t.Keeper.MinReaction, ReactionMean(s, t) + noise);
    }

    /// <summary>Dive speed after the push-off, m/s (diving).</summary>
    public static float DiveSpeed(PlayerSheet s, Tuning t) => t.Keeper.DiveSpeed.ForRating(s.Diving);

    /// <summary>Furthest stretch, metres: lerp(2.2, 2.9) * height / 1.90 (reach).</summary>
    public static float Stretch(PlayerSheet s, Tuning t) =>
        t.Keeper.Stretch.ForRating(s.Reach) * s.HeightM / t.Keeper.StretchHeightReference;

    /// <summary>Ball speed under which a ball inside the catch zone is held, m/s (handling).</summary>
    public static float CatchSpeed(PlayerSheet s, Tuning t) => t.Keeper.CatchSpeed.ForRating(s.Handling);

    /// <summary>
    /// How far the hands have reached <paramref name="sinceReaction"/> seconds after the keeper reacted:
    /// min(stretch, 0.9 + dive speed * (tau - 0.12)), where the 0.9 m arm sweep takes 0.15 s and the dive starts after a 0.12 s push-off.
    /// Zero before the keeper reacts.
    /// </summary>
    public static float ReachAt(PlayerSheet s, Tuning t, float sinceReaction)
    {
        if (sinceReaction <= 0f) return 0f;
        var k = t.Keeper;
        float sweep = k.ArmSweep * MathF.Min(1f, sinceReaction / k.ArmSweepTime);
        float dive = DiveSpeed(s, t) * MathF.Max(0f, sinceReaction - k.PushOff);
        return MathF.Min(Stretch(s, t), sweep + dive);
    }

    /// <summary>Where the basic keeper stands: on the line from the ball to the goal centre, <c>SetDistance</c> off the goal line (on the line for penalties).</summary>
    public static Vector3 SetSpot(Vector3 ball, Pitch pitch, Tuning t, bool onLine)
    {
        float off = onLine ? 0f : t.Keeper.SetDistance;
        float x = pitch.GoalLineX - off;
        float dx = pitch.GoalLineX - ball.X;
        float z = dx > 1e-3f ? ball.Z * (pitch.GoalLineX - x) / dx : 0f;
        z = Math.Clamp(z, -pitch.GoalHalfWidth, pitch.GoalHalfWidth);
        return new Vector3(x, 0f, z);
    }
}
