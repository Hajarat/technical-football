using System.Numerics;

namespace FootballSim.Core;

/// <summary>
/// The team-mate off the ball. No target spot is ever chosen: each tick a sum of fields F(x, z) is sampled mate.sample_m either
/// side of him on both axes and he moves up its slope, standing still (facing the ball) where it is flat.
/// <list type="bullet">
/// <item>Spread: his distance out of a capsule from the carrier to where the carrier will be in 0.2 s, radius
/// mate.spread_radius_m, clamped at 3 m either way.</item>
/// <item>Opponents: his distance into a circle of mate.opponent_radius_m round each outfield opponent (zero outside it).</item>
/// <item>Free space onside: his distance to the nearest opponent or board, no more than mate.space_cap_m, and zero beyond the
/// offside line: the drop at the line is what holds him onside until a run.</item>
/// <item>Pass lane: 1 where a ground pass from the ball, as hard as the carrier can strike one, gets there before an opponent
/// reaches its lane (the pass's own test).</item>
/// <item>Gap: closeness to a point beyond the line in the widest gap between the defender and the boards; only while armed.</item>
/// <item>Offside line: closeness to a spot mate.armed_onside_m short of the line while armed; on go, his depth beyond it, which pushes him on.</item>
/// <item>Goal mouth: closeness to the goal centre.</item>
/// </list>
/// The run cycle (<see cref="MateRun"/>): support (the fields only); armed, with the ball within mate.arm_ball_to_line_m of the
/// line, when he is pulled onto the line and toward the gap; go, once within mate.go_within_m of that spot, when he runs past it
/// flat out (the moment for the through ball); back, left beyond the line with no pass, when he sprints back onside, and
/// more than mate.rearm_onside_m onside he may arm again after mate.rearm_seconds. A pass under way while he is less than
/// mate.pass_cancel_onside_m onside, the ball within mate.run_cancel_goal_m of the goal line, or his being the pass's receiver
/// cancels the run. Away from the goal he moves at mate.backward_jog_share of the jog pace; toward it between the jog and his
/// top speed by how far up the pitch the ball is. He does not move toward a board within mate.board_margin_m of it, nor
/// toward the goal line within mate.goal_line_margin_m of it; he curves round a body in his path. It draws no random numbers.
/// </summary>
public static class MateBot
{
    static Vector2 Flat(Vector3 v) => new(v.X, v.Z);

    /// <summary>What the fields are computed from on one tick.</summary>
    /// <param name="PassSpeed">The fastest ground pass the carrier can strike, m/s (the pass-lane field allows him that much).</param>
    internal readonly record struct Scene(Vector2 Ball, Vector2 Carrier, Vector2 CarrierAhead, float Line, IReadOnlyList<PlayerState> Opponents,
        Pitch? Pitch, MateRun Run, float PassSpeed);

    internal static Scene SceneOf(World w, PlayerState p)
    {
        var c = w.Attacker;
        var cp = Flat(c.Position);
        return new Scene(Flat(w.Ball.Position), cp, cp + Flat(c.Velocity) * CarrierAheadSeconds, w.OffsideOn ? HeldLine(w, p) : float.PositiveInfinity,
            w.Teams[1], w.Pitch, p.MateRun, PassModel.SpeedCap(c, PassKind.Ground, c.Facing, c.Sheet.PreferredFoot, w.Tuning));
    }

    /// <summary>
    /// The line he holds to: the offside line, or where it will be in mate.line_lead_seconds while it comes back up the pitch at
    /// him (a defender stepping up), so he gives ground before it reaches him ...
    /// </summary>
    internal static float HeldLine(World w, PlayerState p) =>
        w.OffsideLine + MathF.Min(0f, p.MateLineVel) * w.Tuning.Mate.LineLeadSeconds - MathF.Max(0f, p.Velocity.X) * BrakeSeconds;

    /// <summary>... and short of it by his own speed toward it over this long, so he arrives on it instead of through it, seconds.</summary>
    const float BrakeSeconds = 0.25f;

    const float CarrierAheadSeconds = 0.2f, SpreadClampM = 3f;
    /// <summary>Within this of the line he keeps with it when it comes back at him, metres.</summary>
    const float NearLineM = 3f;
    /// <summary>The line's speed is smoothed over this long, seconds.</summary>
    const float LineVelSeconds = 0.15f;
    /// <summary>The gap's point lies this far beyond the line, metres.</summary>
    const float GapBeyondM = 2f;
    /// <summary>The fields are sampled once in this many ticks.</summary>
    const int SlopeTicks = 3;

    static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float l2 = ab.LengthSquared();
        float u = l2 < 1e-9f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / l2, 0f, 1f);
        return Vector2.Distance(p, a + ab * u);
    }

    internal static float Spread(Vector2 at, in Scene s, Tuning t) =>
        Math.Clamp(SegmentDistance(at, s.Carrier, s.CarrierAhead) - t.Mate.SpreadRadiusM, -SpreadClampM, SpreadClampM);

    internal static float Opponents(Vector2 at, in Scene s, Tuning t)
    {
        float sum = 0f;
        foreach (var o in s.Opponents)
            if (o.Role == PlayerRole.Outfield) sum += MathF.Min(0f, Vector2.Distance(at, Flat(o.Position)) - t.Mate.OpponentRadiusM);
        return sum;
    }

    /// <summary>The distance from <paramref name="at"/> to the nearest board (negative outside the arena); infinity with no pitch.</summary>
    internal static float BoardDistance(Vector2 at, Pitch? pitch) =>
        pitch == null ? float.PositiveInfinity : MathF.Min(MathF.Min(at.X - pitch.MinX, pitch.GoalLineX - at.X), pitch.HalfWidth - MathF.Abs(at.Y));

    internal static float Space(Vector2 at, in Scene s, Tuning t)
    {
        if (at.X > s.Line) return 0f;
        float d = BoardDistance(at, s.Pitch);
        foreach (var o in s.Opponents) d = MathF.Min(d, Vector2.Distance(at, Flat(o.Position)));
        return MathF.Min(d, t.Mate.SpaceCapM);
    }

    internal static float Lane(Vector2 at, in Scene s, Tuning t)
    {
        float len = Vector2.Distance(s.Ball, at);
        if (len < t.Pass.MinDistance) return 0f;
        // (the pass is struck harder when that is what beats him, up to the carrier's cap)
        float speed = MathF.Max(PassModel.LaunchSpeed(len, t.Pass.ArrivalSpeed.Ground, t), s.PassSpeed);
        return PassModel.LaneMargin(s.Ball, at, speed, s.Opponents, 0f, s.Pitch, t) >= 0f ? 1f : 0f;
    }

    /// <summary>The point of the run in behind: beyond the line, in the middle of the widest gap between the defender and the boards.</summary>
    internal static Vector2 GapPoint(in Scene s)
    {
        float half = s.Pitch?.HalfWidth ?? 10f;
        float line = float.IsFinite(s.Line) ? s.Line : s.Ball.X;
        // The outfield opponent nearest the line is the one the gaps are measured from.
        PlayerState? def = null;
        foreach (var o in s.Opponents)
            if (o.Role == PlayerRole.Outfield && (def == null || MathF.Abs(o.Position.X - line) < MathF.Abs(def.Position.X - line))) def = o;
        float z = 0f;
        if (def != null)
        {
            float dz = Math.Clamp(def.Position.Z, -half, half);
            z = half - dz >= dz + half ? (dz + half) / 2f : (dz - half) / 2f;
        }
        return new Vector2(line + GapBeyondM, z);
    }

    internal static float Gap(Vector2 at, in Scene s) => -Vector2.Distance(at, GapPoint(s));

    internal static float LinePull(Vector2 at, in Scene s, Tuning t) => float.IsFinite(s.Line) ? -MathF.Abs(at.X - (s.Line - t.Mate.ArmedOnsideM)) : 0f;
    internal static float LinePush(Vector2 at, in Scene s) => float.IsFinite(s.Line) ? at.X - s.Line : 0f;

    internal static float Goal(Vector2 at, in Scene s) => s.Pitch == null ? 0f : -Vector2.Distance(at, new Vector2(s.Pitch.GoalLineX, 0f));

    /// <summary>The sum of the fields at <paramref name="at"/>.</summary>
    internal static float Field(Vector2 at, in Scene s, Tuning t)
    {
        var k = t.Mate;
        float f = k.SpreadWeight * Spread(at, s, t) + k.OpponentWeight * Opponents(at, s, t) + k.GoalWeight * Goal(at, s);
        if (s.Run == MateRun.Go)
            // (on go the line no longer holds him: no drop in the free space beyond it, and its push takes him on)
            f += k.SpaceWeight * Space(at, s with { Line = float.PositiveInfinity }, t) - k.LineWeightGo * LinePush(at, s) + k.GapWeightArmed * Gap(at, s);
        else
        {
            f += k.SpaceWeight * Space(at, s, t) + k.LaneWeight * Lane(at, s, t);
            if (s.Run == MateRun.Armed) f += k.LineWeightArmed * LinePull(at, s, t) + k.GapWeightArmed * Gap(at, s);
        }
        return f;
    }

    /// <summary>The slope of the fields at <paramref name="at"/>, per metre, from samples mate.sample_m either side on both axes.</summary>
    internal static Vector2 Gradient(Vector2 at, in Scene s, Tuning t)
    {
        float d = MathF.Max(0.1f, t.Mate.SampleM);
        return new Vector2(
            (Field(at + new Vector2(d, 0f), s, t) - Field(at - new Vector2(d, 0f), s, t)) / (2f * d),
            (Field(at + new Vector2(0f, d), s, t) - Field(at - new Vector2(0f, d), s, t)) / (2f * d));
    }

    /// <summary>How far up the pitch the attack is: 0 with the ball at the back wall, 1 with it mate.attack_full_from_goal_m from the goal line.</summary>
    internal static float AttackProgress(float ballX, Pitch? pitch, Tuning t)
    {
        if (pitch == null) return 0f;
        float full = pitch.GoalLineX - t.Mate.AttackFullFromGoalM - pitch.MinX;
        return full <= 0f ? 1f : Math.Clamp((ballX - pitch.MinX) / full, 0f, 1f);
    }

    /// <summary>
    /// The run cycle's next state. <paramref name="onside"/> is how far he is onside (negative beyond the line),
    /// <paramref name="passUnderWay"/> a pass winding up or on its way, <paramref name="receiver"/> his being its named receiver,
    /// <paramref name="canArm"/> the ball and the carrier being where a run is armed, <paramref name="beyondFor"/> the seconds his
    /// run has been past the line (negative: it is not past it yet), <paramref name="sinceBack"/> the seconds since his last run ended, <paramref name="lineVel"/>
    /// the line's speed (negative while it comes back up the pitch: he does not go while it does so faster than
    /// mate.go_max_line_speed, when a run would only leave him far offside).
    /// </summary>
    internal static MateRun NextRun(MateRun run, float onside, bool passUnderWay, bool receiver, bool ballNearGoal, bool canArm, float beyondFor, float sinceBack,
        float lineVel, Tuning t)
    {
        var k = t.Mate;
        if (receiver || ballNearGoal || passUnderWay && onside < k.PassCancelOnsideM)
            return onside < 0f ? MateRun.Back : MateRun.Support;
        switch (run)
        {
            case MateRun.Support:
                if (onside < 0f) return MateRun.Back;
                return canArm && !passUnderWay && sinceBack >= k.RearmSeconds ? MateRun.Armed : MateRun.Support;
            case MateRun.Armed:
                if (onside < 0f) return MateRun.Back;
                if (!canArm) return MateRun.Support;
                return onside <= k.ArmedOnsideM + k.GoWithinM && lineVel >= -k.GoMaxLineSpeed ? MateRun.Go : MateRun.Armed;
            case MateRun.Go:
                return beyondFor >= 0f && beyondFor >= k.GoBeyondSeconds ? MateRun.Back : MateRun.Go;
            default:
                return onside > k.RearmOnsideM ? MateRun.Support : MateRun.Back;
        }
    }

    /// <summary>The team-mate's intent for this tick (move and sprint only), and his run cycle moved on.</summary>
    public static TickIntent Next(World w, PlayerState p)
    {
        var t = w.Tuning;
        var k = t.Mate;
        var pitch = w.Pitch;
        var at = Flat(p.Position);
        float top = Movement.TopSpeed(p.Sheet, t);
        float jog = MathF.Min(top, t.Movement.JogSpeed);

        // Turnover: the other team has the ball; he walks back toward his own end.
        if (w.BallController is { Team: 1 })
        {
            p.MateRun = MateRun.Support;
            return Intent(p, new Vector2(-1f, 0f), jog * k.BackwardJogShare, top, jog, w, t);
        }

        float dt = 1f / t.TickRate;
        float line = w.OffsideOn ? w.OffsideLine : float.PositiveInfinity;
        if (float.IsFinite(line))
        {
            if (!float.IsNaN(p.MateLineX)) p.MateLineVel += ((line - p.MateLineX) / dt - p.MateLineVel) * MathF.Min(1f, dt / LineVelSeconds);
            p.MateLineX = line;
        }
        float onside = float.IsFinite(line) ? line - at.X : float.PositiveInfinity;
        float ballX = w.Ball.Position.X;
        float progress = AttackProgress(ballX, pitch, t);
        bool passUnderWay = w.PassUnderWay != PassKind.None || w.Pass != null;
        bool receiver = w.Pass?.Receiver == p;
        bool nearGoal = pitch != null && pitch.GoalLineX - ballX <= k.RunCancelGoalM;
        float toDefender = w.Defender is { } d ? Vector2.Distance(Flat(w.Attacker.Position), Flat(d.Position)) : float.PositiveInfinity;
        bool canArm = float.IsFinite(line) && line - ballX <= k.ArmBallToLineM && (progress <= k.ArmMaxProgress || toDefender <= k.ArmCarrierToDefenderM);
        if (p.MateRun == MateRun.Go && onside < 0f && p.MateBeyondTick < 0) p.MateBeyondTick = w.Tick;
        float beyondFor = p.MateRun == MateRun.Go && p.MateBeyondTick >= 0 ? (w.Tick - p.MateBeyondTick) * dt : -1f;
        float sinceBack = (w.Tick - p.MateRunEndTick) * dt;
        var before = p.MateRun;
        p.MateRun = NextRun(before, onside, passUnderWay, receiver, nearGoal, canArm, beyondFor, sinceBack, p.MateLineVel, t);
        if (p.MateRun != MateRun.Go) p.MateBeyondTick = -1;
        if (before is MateRun.Go or MateRun.Back && p.MateRun is MateRun.Support or MateRun.Armed) p.MateRunEndTick = w.Tick;
        if (before != MateRun.Go && p.MateRun == MateRun.Go) p.MateRuns++;

        // Beyond the line and not on a run: back onside at a sprint.
        if (p.MateRun == MateRun.Back)
            return Intent(p, new Vector2(-1f, 0f), top, top, jog, w, t);

        // The fields are read every few ticks (they move slowly); between readings he keeps the slope he last saw.
        if (w.Tick - p.MateSlopeTick >= SlopeTicks || w.Tick < p.MateSlopeTick || before != p.MateRun)
        {
            p.MateSlope = Gradient(at, SceneOf(w, p), t);
            p.MateSlopeTick = w.Tick;
        }
        var g = p.MateSlope;
        float slope = g.Length();
        if (slope < k.StillGradient) { p.MateHeading = default; return default; }
        var dir = g / slope;
        // Edges: no movement toward a board near it, nor toward the goal line near it.
        if (pitch != null)
        {
            if (dir.Y > 0f && pitch.HalfWidth - at.Y < k.BoardMarginM || dir.Y < 0f && at.Y + pitch.HalfWidth < k.BoardMarginM) dir.Y = 0f;
            if (dir.X < 0f && at.X - pitch.MinX < k.BoardMarginM) dir.X = 0f;
            if (dir.X > 0f && pitch.GoalLineX - at.X < k.GoalLineMarginM) dir.X = 0f;
            if (dir.LengthSquared() < 1e-4f) { p.MateHeading = default; return default; }
            dir = Vector2.Normalize(dir);
        }
        // Speed: slower away from the goal; toward it by how far up the pitch the ball is; flat out on a run. Eased in on a
        // shallow slope so he settles on a ridge instead of crossing it back and forth.
        // (armed, he keeps with a line that comes back at him as fast as it takes)
        float speed = p.MateRun == MateRun.Go ? top
            : dir.X < 0f ? (p.MateRun == MateRun.Armed ? top : jog * k.BackwardJogShare)
            : jog + (top - jog) * progress;
        // (near a line that comes back at him he gives ground as fast as it comes)
        if (dir.X < 0f && p.MateRun != MateRun.Go && onside < NearLineM) speed = MathF.Max(speed, MathF.Min(top, 1f - 1.3f * p.MateLineVel));
        if (p.MateRun != MateRun.Go) speed *= Math.Clamp(slope / MathF.Max(k.StillGradient, k.FullGradient), 0f, 1f);
        return Intent(p, dir, speed, top, jog, w, t);
    }

    /// <summary>
    /// The intent that moves him along <paramref name="dir"/> at <paramref name="speed"/>, round any body in his way: sprint when
    /// the speed is above the jog, the stick the speed over that mode's top speed.
    /// </summary>
    static TickIntent Intent(PlayerState p, Vector2 dir, float speed, float top, float jog, World w, Tuning t)
    {
        dir = Avoid(p, dir, w, t);
        p.MateHeading = dir;
        bool sprint = speed > jog + 1e-3f;
        float mode = sprint ? top * t.Movement.SprintFraction : jog;
        float mag = Math.Clamp(speed / MathF.Max(0.1f, mode), 0f, 1f);
        return new TickIntent { MoveX = dir.X * mag, MoveZ = dir.Y * mag, Sprint = sprint ? 1f : 0f };
    }

    /// <summary>
    /// Bodies: with a player within mate.body_avoid_m of his path ahead he curves round on one side (mate.bump_steer_deg off
    /// his way) until nobody is within mate.body_clear_m; for mate.bump_steer_ticks after a bump he steers at least that far
    /// off the body he bumped.
    /// </summary>
    static Vector2 Avoid(PlayerState p, Vector2 dir, World w, Tuning t)
    {
        var k = t.Mate;
        var at = Flat(p.Position);
        float touching = 2f * t.Defending.BodyRadiusM + 0.05f;
        PlayerState? inPath = null;
        float nearest = float.PositiveInfinity, inPathAlong = float.PositiveInfinity;
        foreach (var team in w.Teams)
            foreach (var o in team)
            {
                if (o == p) continue;
                var to = Flat(o.Position) - at;
                float dist = to.Length();
                nearest = MathF.Min(nearest, dist);
                if (dist <= touching) { p.MateBumpUntil = w.Tick + k.BumpSteerTicks; p.MateBumpYaw = MathF.Atan2(to.Y, to.X); }
                float along = Vector2.Dot(to, dir);
                if (along <= 0f || along > k.BodyClearM) continue;
                float side = MathF.Abs(to.X * dir.Y - to.Y * dir.X);
                if (side < k.BodyAvoidM && along < inPathAlong) { inPath = o; inPathAlong = along; }
            }
        if (nearest > k.BodyClearM) p.MateAvoidSide = 0;
        if (inPath != null && p.MateAvoidSide == 0)
        {
            // Round the side he is already on (the body to the right of his way: he goes left).
            var to = Flat(inPath.Position) - at;
            p.MateAvoidSide = dir.X * to.Y - dir.Y * to.X > 0f ? -1 : 1;
        }
        float yaw = MathF.Atan2(dir.Y, dir.X);
        if (p.MateAvoidSide != 0 && inPath != null) yaw += p.MateAvoidSide * k.BumpSteerDeg * MathF.PI / 180f;
        if (w.Tick < p.MateBumpUntil)
        {
            float off = Movement.WrapAngle(yaw - p.MateBumpYaw);
            float min = k.BumpSteerDeg * MathF.PI / 180f;
            if (MathF.Abs(off) < min) yaw = p.MateBumpYaw + (off >= 0f ? min : -min);
        }
        return new Vector2(MathF.Cos(yaw), MathF.Sin(yaw));
    }
}

public sealed partial class World
{
    /// <summary>False: the team-mates stand (probes that place them); true: the bot moves them.</summary>
    public bool MateBotEnabled { get; set; } = true;

    partial void MateBotIntent(PlayerState p, ref TickIntent it)
    {
        if (MateBotEnabled && State is RoundState.Live or RoundState.Strike or RoundState.Flight) it = MateBot.Next(this, p);
        else if (MateScript != null) it = MateScript(this, p);
    }

    /// <summary>With the bot off: a script that moves a team-mate (probes and tests), or null to leave him standing.</summary>
    public Func<World, PlayerState, TickIntent>? MateScript { get; set; }
}
