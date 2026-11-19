namespace FootballSim.Anim;

/// <summary>
/// Landing a clip's target event on the simulation's tick (spec "Landing on the tick"): the playback rate is the clip's time to its
/// event over the simulation's time to it, clamped (0.75–1.35× for saves). Past the clamp the event lands early or late and the hand
/// IK and the pelvis stretch close the gap: the core's tick always wins.
/// </summary>
public static class TimeWarp
{
    public const float SaveMin = 0.75f, SaveMax = 1.35f;

    /// <summary>
    /// The rate for a clip <paramref name="clipToTarget"/> seconds (at its own speed) before its event, with the simulation's event
    /// <paramref name="simToTarget"/> seconds away. A clip already past its event plays at its own speed. A simulated time that is
    /// negative, NaN or under one tick (<paramref name="dt"/>) cannot be met: the rate clamps to <paramref name="max"/> and
    /// <paramref name="late"/> says the IK must take over at once.
    /// </summary>
    public static float Rate(float clipToTarget, float simToTarget, float dt, out bool late, float min = SaveMin, float max = SaveMax)
    {
        late = false;
        if (!(clipToTarget > 0f)) return 1f;
        if (!(simToTarget > dt)) { late = true; return max; }
        return Math.Clamp(clipToTarget / simToTarget, min, max);
    }

    /// <summary>Where (seconds into the clip's remaining time) the event lands when played at <paramref name="rate"/>: clip time over rate.</summary>
    public static float Lands(float clipToTarget, float rate) => clipToTarget / rate;
}
