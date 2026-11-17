namespace FootballSim.Core;

/// <summary>
/// The stride clock. Each player carries a step phase s in [0, 2): s in [0, 1) is a step of the right foot (the right foot
/// swings, the left is planted), s in [1, 2) a step of the left foot. The phase advances by f(v) * dt each tick, with
/// f(v) = base + per_speed * v steps per second and v the running speed (the steering correction left out), so it runs on
/// smoothly through speed changes. Below the idle speed the current step is finished at the base rate and then the clock
/// holds at the step boundary (no new step). The middle of a swing (s = 0.5 or 1.5) is when a foot can play the ball.
/// A planner may lock the clock for a while so a swing middle falls on a given tick (a first step from a standstill).
/// </summary>
public static class Gait
{
    /// <summary>Steps per second at running speed <paramref name="speed"/>, or 0 below the idle speed.</summary>
    public static float Frequency(float speed, Tuning t)
    {
        var m = t.Movement;
        return speed < m.StepIdleSpeed ? 0f : m.StepFrequencyBase + m.StepFrequencyPerSpeed * speed;
    }

    /// <summary>The foot whose step it is at phase <paramref name="s"/> (the foot in swing).</summary>
    public static Foot SwingFoot(float s) => s < 1f ? Foot.Right : Foot.Left;

    /// <summary>Progress through the current step's swing at phase <paramref name="s"/>, 0..1 (0.5 is the swing middle).</summary>
    public static float SwingProgress(float s) => s < 1f ? s : s - 1f;

    /// <summary>A foot's own stride phase, 0..1: it swings during [0, 0.5) and is planted during [0.5, 1).</summary>
    public static float FootPhase(float s, Foot foot)
    {
        float r = s * 0.5f;
        return foot == Foot.Right ? r : (r + 0.5f) % 1f;
    }

    /// <summary>Steps still to go from phase <paramref name="s"/> to the next swing middle of <paramref name="foot"/> (always &gt; 0).</summary>
    public static float StepsToSwingMiddle(float s, Foot foot)
    {
        float mid = foot == Foot.Right ? 0.5f : 1.5f;
        float d = mid - s;
        if (d <= 0f) d += 2f;
        return d;
    }

    /// <summary>
    /// Advances the player's stride clock one tick at running speed <paramref name="speed"/>. Returns the foot whose swing
    /// middle was crossed this tick, or null.
    /// </summary>
    internal static Foot? Advance(PlayerState p, float speed, float dt, Tuning t)
    {
        float s0 = p.GaitPhase;
        float ds;
        bool locked = p.GaitLockTicks > 0;
        if (locked)
        {
            ds = (p.GaitLockTarget - s0) / p.GaitLockTicks;
            p.GaitLockTicks--;
        }
        else
        {
            float f = Frequency(speed, t);
            if (f > 0f) ds = f * dt;
            else
            {
                // Idle: finish the step under way, then hold at the step boundary.
                float boundary = MathF.Floor(s0) + 1f;
                ds = s0 == MathF.Floor(s0) ? 0f : MathF.Min(t.Movement.StepFrequencyBase * dt, boundary - s0);
            }
        }
        p.StepFrequency = ds / dt;
        float s1 = locked && p.GaitLockTicks == 0 ? p.GaitLockTarget : s0 + ds;
        Foot? crossed = null;
        if (s0 < 0.5f && s1 >= 0.5f) crossed = Foot.Right;
        else if (s0 < 1.5f && s1 >= 1.5f) crossed = Foot.Left;
        else if (s0 < 2.5f && s1 >= 2.5f) crossed = Foot.Right;
        while (s1 >= 2f) { s1 -= 2f; p.GaitLockTarget -= 2f; }
        p.GaitPhase = s1;
        p.SwingMiddleCrossed = crossed;
        return crossed;
    }
}
