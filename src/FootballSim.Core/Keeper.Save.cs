using System.Numerics;

namespace FootballSim.Core;

// The save model's numbers: the dive's reach and execution time, the reflex save, the ready stance's hull, save difficulty and
// catch range. The brain's use of them is in KeeperBrain.Save.cs.
public static partial class Keeper
{
    // ---- the dive ----

    /// <summary>Dive speed after the push-off, m/s (diving).</summary>
    public static float DiveSpeed(PlayerSheet s, Tuning t) => t.Keeper.DiveSpeed.ForRating(s.Diving);

    /// <summary>Furthest stretch, metres: lerp(2.2, 2.9) * height / 1.90 (reach).</summary>
    public static float Stretch(PlayerSheet s, Tuning t) =>
        t.Keeper.Stretch.ForRating(s.Reach) * s.HeightM / t.Keeper.StretchHeightReference;

    /// <summary>
    /// The dive's execution time for a save of difficulty <paramref name="difficulty"/>: from his reaction until the push-off
    /// carries him, <c>save_execution_easy</c> for an easy save rising to <c>save_execution_hard</c> at difficulty 1 (both by
    /// reflexes), seconds.
    /// </summary>
    public static float SaveExecution(PlayerSheet s, Tuning t, float difficulty)
    {
        float easy = t.Keeper.SaveExecutionEasy.ForRating(s.Reflexes), hard = t.Keeper.SaveExecutionHard.ForRating(s.Reflexes);
        return easy + (hard - easy) * Math.Clamp(difficulty, 0f, 1f);
    }

    /// <summary>
    /// The largest random delay added to the start of a save (the deviation of its execution time), seconds:
    /// <c>save_time_jitter_easy</c> for an easy save, rising to <c>save_time_jitter_hard</c> at difficulty 1.
    /// </summary>
    public static float SaveJitterMax(Tuning t, float difficulty) =>
        t.Keeper.SaveTimeJitterEasy + (t.Keeper.SaveTimeJitterHard - t.Keeper.SaveTimeJitterEasy) * Math.Clamp(difficulty, 0f, 1f);

    /// <summary>
    /// How far the hands have reached <paramref name="sinceReaction"/> seconds after the keeper reacted, for a save at full
    /// stretch: the 0.9 m arm sweep over 0.15 s, the dive on top once its execution time has passed, capped by his stretch.
    /// Zero before the keeper reacts.
    /// </summary>
    public static float ReachAt(PlayerSheet s, Tuning t, float sinceReaction) => ReachAt(s, t, sinceReaction, 0f);

    /// <summary>
    /// The reach of a keeper whose hands already start <paramref name="span"/> metres out (a set stance), for a save at full
    /// stretch (see <see cref="ReachAt(PlayerSheet, Tuning, float, float, float)"/>).
    /// </summary>
    public static float ReachAt(PlayerSheet s, Tuning t, float sinceReaction, float span) =>
        ReachAt(s, t, sinceReaction, span, SaveExecution(s, t, 1f));

    /// <summary>
    /// The reach of a keeper whose hands already start <paramref name="span"/> metres out (a set stance, hands out to the sides):
    /// <paramref name="span"/> before and at the reaction, then the rest of the arm sweep and the dive on top,
    /// min(stretch, span + (0.9 - span) * min(1, tau / 0.15) + dive speed * (tau - execution)).
    /// </summary>
    public static float ReachAt(PlayerSheet s, Tuning t, float sinceReaction, float span, float execution)
    {
        if (sinceReaction <= 0f) return span;
        var k = t.Keeper;
        float sweep = MathF.Max(0f, k.ArmSweep - span) * MathF.Min(1f, sinceReaction / k.ArmSweepTime);
        float dive = DiveSpeed(s, t) * MathF.Max(0f, sinceReaction - execution);
        return MathF.Min(Stretch(s, t), span + sweep + dive);
    }

    // ---- the reflex save ----

    /// <summary>Mean reflex time, seconds from the strike (reflexes).</summary>
    public static float ReflexMean(PlayerSheet s, Tuning t) => t.Keeper.ReflexTime.ForRating(s.Reflexes);

    /// <summary>
    /// Draws the reflex time for a strike: the mean plus the reaction's clamped noise, never later than
    /// <paramref name="reaction"/>. Moving costs a reflex nothing in time (it is a limb, not a dive); moving the wrong way costs
    /// it difficulty (<see cref="Difficulty"/>).
    /// </summary>
    public static float DrawReflex(PlayerSheet s, Tuning t, SimRng rng, float reaction)
    {
        var k = t.Keeper;
        float span = k.ReactionNoise * k.ReactionNoiseClampSigma;
        float noise = Math.Clamp(rng.Normal(0f, k.ReactionNoise), -span, span);
        return MathF.Min(MathF.Max(k.MinReaction, ReflexMean(s, t) + noise), reaction);
    }

    /// <summary>The furthest a reflex limb gets from his upright axis, metres (reach, scaled by height).</summary>
    public static float ReflexRange(PlayerSheet s, Tuning t) =>
        t.Keeper.ReflexRange.ForRating(s.Reach) * s.HeightM / t.Keeper.StretchHeightReference;

    /// <summary>The highest a reflex can be aimed, metres.</summary>
    public static float ReflexHeight(PlayerSheet s, Tuning t) => s.HeightM * t.Keeper.ReflexHeightFraction;

    /// <summary>The reflex move's execution time for a save of difficulty <paramref name="difficulty"/>, seconds (reflexes).</summary>
    public static float ReflexExecution(PlayerSheet s, Tuning t, float difficulty)
    {
        float easy = t.Keeper.ReflexExecutionEasy.ForRating(s.Reflexes), hard = t.Keeper.ReflexExecutionHard.ForRating(s.Reflexes);
        return easy + (hard - easy) * Math.Clamp(difficulty, 0f, 1f);
    }

    /// <summary>
    /// How far from his upright axis a reflex limb has got <paramref name="sinceReflex"/> seconds after his reflex: from
    /// <paramref name="start"/> (where his stance holds it), once <paramref name="execution"/> has passed, at <c>reflex_speed</c>
    /// (reflexes), up to <see cref="ReflexRange"/>.
    /// </summary>
    public static float ReflexReachAt(PlayerSheet s, Tuning t, float sinceReflex, float start, float execution)
    {
        float range = MathF.Max(start, ReflexRange(s, t));
        float run = MathF.Max(0f, sinceReflex - execution);
        return MathF.Min(range, start + t.Keeper.ReflexSpeed.ForRating(s.Reflexes) * run);
    }

    /// <summary>
    /// The kind of reflex for a target (ball centre) from feet at <paramref name="feet"/>: a leg shot out at a low ball, the
    /// spread body thrown at one when he was ready (at least half), else a hand.
    /// </summary>
    public static KeeperDive ReflexKindFor(Vector3 feet, Vector3 target, float ready, Tuning t)
    {
        var k = t.Keeper;
        if (target.Y <= k.ReflexFootHeight) return KeeperDive.ReflexFoot;
        if (ready >= 0.5f && target.Y <= k.ReflexBodyHeight) return KeeperDive.BodyBlock;
        return KeeperDive.ReflexHand;
    }

    // ---- the ready stance ----

    /// <summary>
    /// How ready a keeper set for the shot is, 0..1, by his distance from the ball at the strike: fully within
    /// <c>ready_full</c>, not at all from <c>ready_range</c>.
    /// </summary>
    public static float Readiness(Tuning t, float distanceToBall) =>
        Math.Clamp((t.Keeper.ReadyRange - distanceToBall) / (t.Keeper.ReadyRange - t.Keeper.ReadyFull), 0f, 1f);

    /// <summary>The radius of his standing body (the hull) for a readiness, metres: <c>body_radius</c> to <c>hull_radius</c>.</summary>
    public static float HullRadius(Tuning t, float ready) => t.Keeper.BodyRadius + (t.Keeper.HullRadius - t.Keeper.BodyRadius) * ready;

    /// <summary>
    /// How far out from his upright axis his hands (or a reflex limb) start at the strike: his hand span when he was still, or
    /// the edge of his hull (less a hand's radius) when he was ready (<paramref name="ready"/> above 0) and that is further.
    /// </summary>
    public static float StanceStart(Tuning t, float ready, bool still) =>
        MathF.Max(still ? t.Keeper.HandSpan : 0f, ready > 0f ? HullRadius(t, ready) - t.Keeper.HandRadius : 0f);

    // ---- save difficulty ----

    /// <summary>
    /// The difficulty of a save by the lateral range it needs (horizontal distance from his upright axis, as a fraction of his
    /// stretch) and the ball's height: the low, mid and high curves, linear between their heights.
    /// </summary>
    public static float RangeDifficulty(PlayerSheet s, Tuning t, float lateral, float height)
    {
        var k = t.Keeper;
        float x = lateral / Stretch(s, t);
        if (height <= k.DifficultyMidHeight)
        {
            float f = Math.Clamp((height - k.DifficultyLowHeight) / (k.DifficultyMidHeight - k.DifficultyLowHeight), 0f, 1f);
            return k.DifficultyLow.Evaluate(x) + (k.DifficultyMid.Evaluate(x) - k.DifficultyLow.Evaluate(x)) * f;
        }
        float g = Math.Clamp((height - k.DifficultyMidHeight) / (k.DifficultyHighHeight - k.DifficultyMidHeight), 0f, 1f);
        return k.DifficultyMid.Evaluate(x) + (k.DifficultyHigh.Evaluate(x) - k.DifficultyMid.Evaluate(x)) * g;
    }

    /// <summary>
    /// His speed away from a save at <paramref name="target"/> (the wrong way), m/s: the part of his ground velocity
    /// <paramref name="velocity"/> pointing from the target to his feet. Zero when he moves toward it or across.
    /// </summary>
    public static float CounterSpeed(Vector3 feet, Vector3 velocity, Vector3 target)
    {
        var to = new Vector2(target.X - feet.X, target.Z - feet.Z);
        float len = to.Length();
        if (len < 1e-3f) return 0f;
        return MathF.Max(0f, -Vector2.Dot(new Vector2(velocity.X, velocity.Z), to / len));
    }

    /// <summary>
    /// The difficulty of a save at <paramref name="target"/> from feet at <paramref name="feet"/>: by the range it needs and its
    /// height (<see cref="RangeDifficulty"/>), plus <c>counter_momentum_penalty</c> for his speed away from it.
    /// </summary>
    public static float Difficulty(PlayerSheet s, Tuning t, Vector3 feet, Vector3 velocity, Vector3 target)
    {
        float lateral = new Vector2(target.X - feet.X, target.Z - feet.Z).Length();
        return RangeDifficulty(s, t, lateral, target.Y) + t.Keeper.CounterMomentumPenalty.Evaluate(CounterSpeed(feet, velocity, target));
    }

    // ---- catch range ----

    /// <summary>
    /// How far from his hands' rest point he can hold a ball meeting him at <paramref name="speed"/> m/s, metres: the standing
    /// or diving range for that speed between handling 1 and 99, scaled by <c>catch_range_time</c> for the seconds he had from
    /// the strike (<paramref name="sinceStrike"/>; less time, smaller range).
    /// </summary>
    public static float CatchRange(PlayerSheet s, Tuning t, float speed, bool diving, float sinceStrike)
    {
        var k = t.Keeper;
        float h = Rating.R01(s.Handling);
        float lo = (diving ? k.CatchRangeDivingMin : k.CatchRangeStandingMin).Evaluate(speed);
        float hi = (diving ? k.CatchRangeDivingMax : k.CatchRangeStandingMax).Evaluate(speed);
        return (lo + (hi - lo) * h) * k.CatchRangeTime.Evaluate(sinceStrike);
    }

    // ---- the wedge and the dive kind ----

    /// <summary>
    /// True if a dive from feet at <paramref name="feet"/>, facing the ball along <paramref name="facing"/> (unit, ground X/Z), can be
    /// aimed at <paramref name="p"/>: within <c>catch_body_radius</c> of him (a save at the body), or <c>wedge_min_radius</c> to
    /// <c>wedge_max_radius</c> away and within <c>wedge_half_angle_deg</c> of his sideways axis (he dives sideways or
    /// forward-diagonally, not straight ahead or back), and above <c>wedge_low_cap</c> only in his save plane (a low ball can be
    /// attacked forward, a high one only sideways).
    /// </summary>
    public static bool InWedge(Vector3 feet, Vector2 facing, Vector3 p, Tuning t)
    {
        var k = t.Keeper;
        var o = new Vector2(p.X - feet.X, p.Z - feet.Z);
        float dist = o.Length();
        if (dist <= k.CatchBodyRadius) return true;
        if (dist < k.WedgeMinRadius || dist > k.WedgeMaxRadius) return false;
        float forward = Vector2.Dot(o, facing);
        float side = MathF.Abs(o.X * facing.Y - o.Y * facing.X);
        float fromSide = MathF.Atan2(MathF.Abs(forward), side) * 180f / MathF.PI;
        if (fromSide > k.WedgeHalfAngleDeg) return false;
        if (p.Y > k.WedgeLowCap && forward > k.WedgeHighForward) return false;
        return true;
    }

    /// <summary>
    /// The save motion for a dive target <paramref name="target"/> (ball centre) from feet at <paramref name="feet"/>: a standing
    /// catch at the body, a block at the feet, or a low, side or high dive by the target's height.
    /// </summary>
    public static KeeperDive DiveKindFor(Vector3 feet, Vector3 target, Tuning t)
    {
        var k = t.Keeper;
        float lateral = new Vector2(target.X - feet.X, target.Z - feet.Z).Length();
        if (lateral <= k.CatchBodyRadius && target.Y >= k.FeetBlockHeight && target.Y <= k.HighDiveHeight + 0.4f) return KeeperDive.Catch;
        if (lateral <= k.FeetBlockRadius && target.Y < k.FeetBlockHeight) return KeeperDive.Feet;
        if (target.Y < k.LowDiveHeight) return KeeperDive.Low;
        if (target.Y < k.HighDiveHeight) return KeeperDive.Side;
        return KeeperDive.High;
    }
}
