using System.Numerics;

namespace FootballSim.Core;

/// <summary>
/// Keeper numbers derived from ratings, and the pure geometry the decision code uses (arc spot, travel time, dive wedge,
/// dive kind). The decision code is in <see cref="KeeperBrain"/>.
/// </summary>
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

    /// <summary>
    /// The reaction delay for a strike: a drawn reaction time, plus <c>still_moving_reaction_penalty</c> when the keeper's speed at
    /// the contact is above <c>still_speed</c> (he was not set).
    /// </summary>
    public static float ReactionAtStrike(PlayerSheet s, Tuning t, SimRng rng, float speedAtStrike) =>
        DrawReaction(s, t, rng) + (speedAtStrike > t.Keeper.StillSpeed ? t.Keeper.StillMovingReactionPenalty : 0f);

    /// <summary>
    /// The largest random delay added to the start of a dive (save execution time), seconds: <c>save_time_jitter_easy</c> for a
    /// save that needs no reach, rising to <c>save_time_jitter_hard</c> for one at full stretch.
    /// </summary>
    public static float SaveJitterMax(PlayerSheet s, Tuning t, float needed)
    {
        float d = Math.Clamp(needed / Stretch(s, t), 0f, 1f);
        return t.Keeper.SaveTimeJitterEasy + (t.Keeper.SaveTimeJitterHard - t.Keeper.SaveTimeJitterEasy) * d;
    }

    /// <summary>Dive speed after the push-off, m/s (diving).</summary>
    public static float DiveSpeed(PlayerSheet s, Tuning t) => t.Keeper.DiveSpeed.ForRating(s.Diving);

    /// <summary>Furthest stretch, metres: lerp(2.2, 2.9) * height / 1.90 (reach).</summary>
    public static float Stretch(PlayerSheet s, Tuning t) =>
        t.Keeper.Stretch.ForRating(s.Reach) * s.HeightM / t.Keeper.StretchHeightReference;

    /// <summary>Ball speed under which a ball inside the catch zone is held standing, m/s (handling).</summary>
    public static float CatchSpeed(PlayerSheet s, Tuning t) => t.Keeper.CatchSpeed.ForRating(s.Handling);

    /// <summary>Ball speed under which a ball inside the catch zone is held in a dive, m/s (handling).</summary>
    public static float DivingCatchSpeed(PlayerSheet s, Tuning t) => CatchSpeed(s, t) * t.Keeper.DivingCatchFactor;

    /// <summary>
    /// How far the hands have reached <paramref name="sinceReaction"/> seconds after the keeper reacted:
    /// min(stretch, 0.9 + dive speed * (tau - 0.12)), where the 0.9 m arm sweep takes 0.15 s and the dive starts after a 0.12 s push-off.
    /// Zero before the keeper reacts.
    /// </summary>
    public static float ReachAt(PlayerSheet s, Tuning t, float sinceReaction) => ReachAt(s, t, sinceReaction, 0f);

    /// <summary>
    /// The reach of a keeper whose hands already start <paramref name="span"/> metres out (a set stance, hands out to the sides):
    /// <paramref name="span"/> before and at the reaction, then the rest of the arm sweep and the dive on top,
    /// min(stretch, span + (0.9 - span) * min(1, tau / 0.15) + dive speed * (tau - 0.12)). With span 0 it is <see cref="ReachAt(PlayerSheet, Tuning, float)"/>.
    /// </summary>
    public static float ReachAt(PlayerSheet s, Tuning t, float sinceReaction, float span)
    {
        if (sinceReaction <= 0f) return span;
        var k = t.Keeper;
        float sweep = MathF.Max(0f, k.ArmSweep - span) * MathF.Min(1f, sinceReaction / k.ArmSweepTime);
        float dive = DiveSpeed(s, t) * MathF.Max(0f, sinceReaction - k.PushOff);
        return MathF.Min(Stretch(s, t), span + sweep + dive);
    }

    // ---- movement ----

    /// <summary>Side-step speed along the arc, m/s (positioning).</summary>
    public static float ShuffleSpeed(PlayerSheet s, Tuning t) => t.Keeper.ShuffleSpeed.ForRating(s.Positioning);
    /// <summary>Jogging speed for a long reposition, m/s (positioning).</summary>
    public static float JogSpeed(PlayerSheet s, Tuning t) => t.Keeper.JogSpeed.ForRating(s.Positioning);
    /// <summary>Top speed running out at the ball, m/s (rushing_out).</summary>
    public static float RushSpeed(PlayerSheet s, Tuning t) => t.Keeper.RushSpeed.ForRating(s.RushingOut);
    /// <summary>Acceleration on foot, m/s² (rushing_out).</summary>
    public static float MoveAccel(PlayerSheet s, Tuning t) => t.Keeper.MoveAccel.ForRating(s.RushingOut);
    /// <summary>Standard deviation of the error in each new spot on the arc, metres (positioning).</summary>
    public static float PositionErrorSd(PlayerSheet s, Tuning t) => t.Keeper.PositionError.ForRating(s.Positioning);
    /// <summary>Standard deviation of the keeper's error in judging a rush race, seconds (rushing_out).</summary>
    public static float RushMisjudgeSd(PlayerSheet s, Tuning t) => t.Keeper.RushMisjudge.ForRating(s.RushingOut);

    /// <summary>
    /// The time margin the keeper wants over the attacker's next touch before he rushes for a ball <paramref name="distance"/>
    /// metres away: <c>rush_margin(distance)</c> scaled by <c>rush_margin_rating</c> (a high rushing_out rating needs less).
    /// </summary>
    public static float RushMargin(PlayerSheet s, Tuning t, float distance) =>
        t.Keeper.RushMargin.Evaluate(distance) * t.Keeper.RushMarginRating.ForRating(s.RushingOut);

    /// <summary>
    /// Seconds to cover <paramref name="distance"/> metres from speed <paramref name="v0"/> along the way (negative = moving away),
    /// accelerating at <paramref name="accel"/> up to <paramref name="vmax"/>. No slowing down at the end (a run through).
    /// </summary>
    public static float TravelTime(float distance, float v0, float accel, float vmax)
    {
        if (distance <= 0f) return 0f;
        v0 = Math.Clamp(v0, -vmax, vmax);
        float t1 = (vmax - v0) / accel;
        float d1 = (v0 + vmax) * 0.5f * t1;
        if (d1 >= distance) return (-v0 + MathF.Sqrt(MathF.Max(0f, v0 * v0 + 2f * accel * distance))) / accel;
        return t1 + (distance - d1) / vmax;
    }

    /// <summary>The point a movement decision steers at: <paramref name="to"/>, or the point <paramref name="cap"/> metres along the way to it if further.</summary>
    public static Vector2 CapTarget(Vector2 from, Vector2 to, float cap)
    {
        var d = to - from;
        float len = d.Length();
        return len > cap ? from + d * (cap / len) : to;
    }

    // ---- the arc ----

    /// <summary>
    /// Distance from a point in front of the goal to the goal mouth (the segment between the posts on the goal line): the
    /// reference geometry the arc is built around.
    /// </summary>
    public static float MouthDistance(Vector2 p, Pitch pitch)
    {
        float dx = MathF.Max(0f, pitch.GoalLineX - p.X);
        float dz = MathF.Max(0f, MathF.Abs(p.Y) - pitch.GoalHalfWidth);
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// The direction (unit, ground X/Z) from the ball along which the keeper covers the goal: the bisector of the angle the two
    /// posts make at the ball, turned toward the near post by <c>near_post_bias(angle)</c> of the half-angle, where the angle is
    /// the ball's bearing from the goal's centre line (0 straight on, 90 along the goal line).
    /// </summary>
    public static Vector2 CoverDirection(Vector2 ball, Pitch pitch, Tuning t)
    {
        float hw = pitch.GoalHalfWidth;
        float gx = pitch.GoalLineX;
        float side = ball.Y >= 0f ? 1f : -1f;
        float aNear = MathF.Atan2(side * hw - ball.Y, gx - ball.X);
        float aFar = MathF.Atan2(-side * hw - ball.Y, gx - ball.X);
        float bis = 0.5f * (aNear + aFar);
        float bias = t.Keeper.NearPostBias.Evaluate(Bearing(ball, pitch));
        float a = bis + bias * (aNear - bis);
        return new Vector2(MathF.Cos(a), MathF.Sin(a));
    }

    /// <summary>
    /// How far out from the goal mouth the keeper stands: <c>arc_radius</c> plus, scaled by positioning, <c>arc_depth</c> of the
    /// ball's distance from the mouth (further out as the ball comes closer) and <c>arc_depth_bearing</c> of the ball's bearing
    /// from the goal's centre line (further out to close down a wide, tight angle).
    /// </summary>
    public static float ArcDistance(PlayerSheet s, Tuning t, float ballMouthDistance, float bearingDeg) =>
        t.Keeper.ArcRadius + (t.Keeper.ArcDepth.Evaluate(ballMouthDistance) + t.Keeper.ArcDepthBearing.Evaluate(bearingDeg))
        * t.Keeper.ArcDepthPositioning.ForRating(s.Positioning);

    /// <summary>The ball's bearing from the goal's centre line, degrees: 0 straight in front, 90 along the goal line.</summary>
    public static float Bearing(Vector2 ball, Pitch pitch) =>
        MathF.Atan2(MathF.Abs(ball.Y), MathF.Max(1e-3f, pitch.GoalLineX - ball.X)) * 180f / MathF.PI;

    /// <summary>
    /// The keeper's spot on his arc for a ball at <paramref name="ball"/>: on the cover line from the ball (see
    /// <see cref="CoverDirection"/>) where it is <see cref="ArcDistance"/> from the goal mouth, never closer than
    /// <c>arc_min_from_line</c> to the goal line, and at least half way from the ball to that limit.
    /// </summary>
    public static Vector3 ArcTarget(Vector3 ball, Pitch pitch, Tuning t, PlayerSheet s)
    {
        var b = new Vector2(ball.X, ball.Z);
        return CoverSpot(ball, pitch, t, ArcDistance(s, t, MouthDistance(b, pitch), Bearing(b, pitch)));
    }

    /// <summary>
    /// The point on the cover line from the ball (see <see cref="CoverDirection"/>) <paramref name="want"/> metres from the goal
    /// mouth, never closer than <c>arc_min_from_line</c> to the goal line, and at least half way from the ball to that limit
    /// (or, with <paramref name="halfWay"/> false, at least a metre from the ball).
    /// </summary>
    public static Vector3 CoverSpot(Vector3 ball, Pitch pitch, Tuning t, float want, bool halfWay = true)
    {
        var b = new Vector2(ball.X, ball.Z);
        Vector2 dir = CoverDirection(b, pitch, t);
        float ballMouth = MouthDistance(b, pitch);
        // Walk the cover line from the ball toward the goal line: the mouth distance falls along it (convex), so bisect.
        float tMax = (pitch.GoalLineX - t.Keeper.ArcMinFromLine - b.X) / MathF.Max(1e-3f, dir.X);
        if (tMax <= 0f) return new Vector3(pitch.GoalLineX - t.Keeper.ArcMinFromLine, 0f, Math.Clamp(ball.Z, -pitch.GoalHalfWidth, pitch.GoalHalfWidth));
        float lo = 0f, hi = tMax;
        if (MouthDistance(b + dir * hi, pitch) >= want) lo = hi;           // the line limit is further out than the arc
        else if (ballMouth <= want) hi = 0f;
        else
            for (int i = 0; i < 30; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (MouthDistance(b + dir * mid, pitch) > want) lo = mid; else hi = mid;
            }
        // A ball closer to the mouth than the arc: he stands at least half way from it to his line.
        float along = Math.Clamp(0.5f * (lo + hi), halfWay ? 0.5f * tMax : MathF.Min(1f, tMax), tMax);
        var p = b + dir * along;
        return new Vector3(p.X, 0f, p.Y);
    }

    /// <summary>
    /// The keeper's spot when he holds his line (a penalty): on the goal line where the cover line from the ball meets it, kept
    /// inside the posts. The arena stops him a body's radius in front of the end wall.
    /// </summary>
    public static Vector3 LineSpot(Vector3 ball, Pitch pitch, Tuning t)
    {
        var b = new Vector2(ball.X, ball.Z);
        Vector2 dir = CoverDirection(b, pitch, t);
        float along = (pitch.GoalLineX - b.X) / MathF.Max(1e-3f, dir.X);
        float z = b.Y + dir.Y * along;
        float lim = pitch.GoalHalfWidth - t.Keeper.LineInset;
        return new Vector3(pitch.GoalLineX, 0f, Math.Clamp(z, -lim, lim));
    }

    // ---- closing down and chips ----

    /// <summary>
    /// How far out from the goal mouth he comes closing down an attacker on the ball <paramref name="ballMouthDistance"/> metres
    /// from the mouth: the arc distance plus <c>close_down_depth</c>'s excess over it, scaled by <c>close_down_positioning</c>.
    /// </summary>
    public static float CloseDownDistance(PlayerSheet s, Tuning t, float ballMouthDistance, float arcDistance)
    {
        float extra = MathF.Max(0f, t.Keeper.CloseDownDepth.Evaluate(ballMouthDistance) - arcDistance);
        return arcDistance + extra * t.Keeper.CloseDownPositioning.ForRating(s.Positioning);
    }

    /// <summary>Backpedalling speed, m/s (positioning).</summary>
    public static float BackpedalSpeed(PlayerSheet s, Tuning t) => t.Keeper.BackpedalSpeed.ForRating(s.Positioning);

    /// <summary>The highest ball he can get a hand to with a standing leap, metres.</summary>
    public static float ChipReachHeight(PlayerSheet s, Tuning t) => s.HeightM * t.Keeper.ChipReachHeightFraction;

    /// <summary>The height a chip has dropped to where he plans to leap at it after backpedalling, metres.</summary>
    public static float ChipTakeHeight(PlayerSheet s, Tuning t) => s.HeightM * t.Keeper.ChipTakeHeightFraction;

    /// <summary>Standard deviation of his error judging his chip-safe depth, as a fraction of it (positioning).</summary>
    public static float ChipJudgeErrorSd(PlayerSheet s, Tuning t) => t.Keeper.ChipJudgeError.ForRating(s.Positioning);

    const float ChipTableStep = 0.5f, ChipTableMin = 2f, ChipTableMax = 40f;
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Tuning, Dictionary<string, float[]>> ChipTables = new();

    /// <summary>
    /// The chip-safe depth: the furthest he can stand off his line, on the line from the ball to the goal, and still recover from
    /// a chip struck at <paramref name="ballDistance"/> metres from the goal. The chip is <paramref name="attacker"/>'s, at the
    /// ideal charge for that distance (mean backspin, straight at goal). At a depth the ball passes over above his leap
    /// (<see cref="ChipReachHeight"/>), he must backpedal from his mean reaction to within <c>chip_tip_reach</c> of a point where it
    /// has dropped to <see cref="ChipTakeHeight"/>, on or before the goal line, <c>chip_leap_lead</c> before it gets there. Every depth from the line out to the one
    /// returned is safe. A chip that would clear the bar is no threat (the distance itself is returned).
    /// </summary>
    public static float ChipSafeDepth(PlayerSheet keeper, PlayerSheet attacker, Tuning t, Pitch pitch, float ballDistance)
    {
        string key = FormattableString.Invariant($"{keeper.Reflexes}|{keeper.Positioning}|{keeper.RushingOut}|{keeper.HeightM}|{attacker.ShotPower}|{pitch.BarHeight}");
        float[] table;
        lock (ChipTables)
        {
            var byKey = ChipTables.GetOrCreateValue(t);
            if (!byKey.TryGetValue(key, out table!))
            {
                int n = (int)MathF.Round((ChipTableMax - ChipTableMin) / ChipTableStep) + 1;
                table = new float[n];
                for (int i = 0; i < n; i++) table[i] = ComputeChipSafeDepth(keeper, attacker, t, pitch.BarHeight, ChipTableMin + i * ChipTableStep);
                byKey[key] = table;
            }
        }
        float f = (Math.Clamp(ballDistance, ChipTableMin, ChipTableMax) - ChipTableMin) / ChipTableStep;
        int i0 = Math.Min((int)f, table.Length - 1), i1 = Math.Min(i0 + 1, table.Length - 1);
        return table[i0] + (table[i1] - table[i0]) * (f - i0);
    }

    static float ComputeChipSafeDepth(PlayerSheet keeper, PlayerSheet attacker, Tuning t, float barHeight, float distance)
    {
        var k = t.Keeper;
        float gx = 0f; // the goal line, with the ball at -distance on the X axis
        float charge = ShotModel.IdealCharge(distance, t);
        float speed = ShotModel.IntendedSpeed(attacker, ShotType.Chip, charge, t);
        float elev = ShotModel.ElevationDeg(ShotType.Chip, charge, t) * MathF.PI / 180f;
        var ball = new Ball(t.Ball)
        {
            Position = new Vector3(gx - distance, t.Ball.Radius, 0f),
            Velocity = new Vector3(MathF.Cos(elev), MathF.Sin(elev), 0f) * speed,
            // Backspin for a ball heading +X: spin = v x up, about +Z (as the shot model sets it).
            Spin = new Vector3(0f, 0f, 1f) * (0.5f * (t.Shot.ChipBackspinMinRev + t.Shot.ChipBackspinMaxRev) * 2f * MathF.PI),
        };
        var xs = new List<float>(); var ys = new List<float>(); var ts = new List<float>();
        float dt = t.Dt, time = 0f;
        xs.Add(distance); ys.Add(ball.Position.Y); ts.Add(0f);
        while (time < 4f)
        {
            ball.Step(dt, null, null);
            time += dt;
            xs.Add(gx - ball.Position.X); ys.Add(ball.Position.Y); ts.Add(time);
            if (ball.Position.X >= gx) break;
            if (ball.OnGround && ball.Velocity.Y <= 0f && time > 0.2f) break;
        }
        float reachH = ChipReachHeight(keeper, t), takeH = ChipTakeHeight(keeper, t);
        // Where it crosses the goal line (or the last point if it lands short): over the bar is no threat.
        if (ball.Position.X >= gx && ys[^1] > t.Ball.Radius && HeightAt(xs, ys, 0f) > barHeight + t.Ball.Radius) return distance;
        float react = ReactionMean(keeper, t);
        float accel = MoveAccel(keeper, t), vb = BackpedalSpeed(keeper, t);
        float safe = 0f;
        for (float x = 0f; x <= distance - 0.5f; x += 0.1f)
        {
            if (!SafeAt(x)) break;
            safe = x;
        }
        return safe;

        bool SafeAt(float x)
        {
            if (HeightAt(xs, ys, x) <= reachH) return true;   // it passes within his leap: a save as usual
            for (int i = 0; i < xs.Count; i++)
            {
                if (xs[i] > x || xs[i] < 0f || ys[i] > takeH) continue;
                if (react + TravelTime(x - xs[i] - k.ChipTipReach, 0f, accel, vb) + k.ChipLeapLead <= ts[i]) return true;
            }
            return false;
        }
    }

    /// <summary>The chip's height when it passes <paramref name="x"/> metres from the goal line (first pass), from the sampled flight.</summary>
    static float HeightAt(List<float> xs, List<float> ys, float x)
    {
        for (int i = 1; i < xs.Count; i++)
            if (xs[i] <= x)
            {
                float f = (xs[i - 1] - x) / MathF.Max(1e-5f, xs[i - 1] - xs[i]);
                return ys[i - 1] + (ys[i] - ys[i - 1]) * f;
            }
        return ys[^1];
    }

    // ---- saves ----

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
