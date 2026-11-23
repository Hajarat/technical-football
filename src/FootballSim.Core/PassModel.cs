using System.Numerics;

namespace FootballSim.Core;

/// <summary>A pass as it is planned: who it is for, where it goes and how hard.</summary>
/// <param name="Receiver">The named receiver, or null for a pass into space.</param>
/// <param name="Target">Where pass and receiver should meet (the ball centre).</param>
/// <param name="Direction">The ground direction the ball leaves along (unit, X and Z).</param>
/// <param name="Speed">The launch speed, m/s.</param>
/// <param name="ArrivalSeconds">How long the ball takes to the target.</param>
/// <param name="Margin">How much later than the ball the defender is on its lane at his best, seconds (negative: he is there first).</param>
public readonly record struct PassPlan(PlayerState? Receiver, Vector3 Target, Vector2 Direction, float Speed, float ArrivalSeconds, float Margin);

/// <summary>The pass struck last, while it is on its way: who struck it, who it is for and when it should reach him.</summary>
public sealed class PassRecord
{
    public PlayerState Passer { get; init; } = null!;
    public PassKind Kind { get; init; }
    /// <summary>The named receiver, or null for a pass into space.</summary>
    public PlayerState? Receiver { get; init; }
    public Vector3 Target { get; init; }
    /// <summary>The world tick the ball should reach the target on.</summary>
    public int ExpectedArrivalTick { get; init; }
    /// <summary>The world tick it was struck on.</summary>
    public int StrikeTick { get; init; }
    /// <summary>The forecast path the pass travels on (its start tick), or -1 until the forecast has been built for it.</summary>
    public int PathTick { get; internal set; } = -1;
    /// <summary>The receiver was late, so the claim on the pass is open to his whole team again.</summary>
    public bool Reopened { get; internal set; }
}

/// <summary>
/// Passes along the ground: a ground pass to a team-mate's feet and a through ball into the space he runs to. The hold does not
/// set power: it sets how far along the stick the player is pointing (<see cref="SelectionPoint"/>). The team-mates are scored
/// against that point (<see cref="ChooseReceiver"/>), a target is searched over meeting points against the defender
/// (<see cref="Plan"/>), and the ball is given the least speed that arrives, more only to beat him. A pass leaves rolling, so its
/// travel is the rolling law: no direction error is drawn. It draws no random numbers.
/// </summary>
public static class PassModel
{
    static Vector2 Flat(Vector3 v) => new(v.X, v.Z);

    /// <summary>The charge after holding the pass button for <paramref name="seconds"/>: 0 to 1 over pass.charge_seconds.</summary>
    public static float ChargeAfter(float seconds, Tuning t) => Math.Clamp(seconds / MathF.Max(1e-3f, t.Pass.ChargeSeconds), 0f, 1f);

    /// <summary>The direction a pass is pointed: the stick, or the facing with the stick at rest (unit).</summary>
    public static Vector2 Aim(PlayerState passer, Vector2 stick, Tuning t)
    {
        if (stick.Length() >= t.Touch.StickThreshold) return Vector2.Normalize(stick);
        return new Vector2(MathF.Cos(passer.Facing), MathF.Sin(passer.Facing));
    }

    /// <summary>The selection point: from the ball along the aim, pass.min_distance at no hold to the kind's maximum at a full one.</summary>
    public static Vector2 SelectionPoint(Vector3 ball, Vector2 aim, float charge, PassKind kind, Tuning t)
    {
        var k = t.Pass;
        float d = k.MinDistance + (k.MaxDistance.For(kind) - k.MinDistance) * Math.Clamp(charge, 0f, 1f);
        return Flat(ball) + aim * d;
    }

    /// <summary>
    /// A team-mate's score as the receiver of a ground pass pointed at <paramref name="s"/>: 1 / [(1 + 4 offside) d (a / 45)^2
    /// reach], d his distance from the point (six times as much when the point is short of him), a the angle between the aim and
    /// the line to him (at least 5 degrees), reach 0.05 when he is within 0.5 m of the point and otherwise what is left of his
    /// distance to it after half a second of his run (at least 0.05).
    /// </summary>
    public static float GroundScore(PlayerState m, Vector3 ball, Vector2 aim, Vector2 s, bool offside)
    {
        var mp = Flat(m.Position);
        var from = Flat(ball);
        float d = Vector2.Distance(mp, s);
        if (Vector2.Distance(from, s) < Vector2.Distance(from, mp)) d *= 6f;
        var to = mp - from;
        float a = to.LengthSquared() < 1e-6f ? 5f
            : MathF.Max(5f, MathF.Abs(Movement.WrapAngle(MathF.Atan2(to.Y, to.X) - MathF.Atan2(aim.Y, aim.X))) * 180f / MathF.PI);
        float reach = 0.05f;
        if (d > 0.5f)
        {
            var toS = s - mp;
            float closing = toS.LengthSquared() > 1e-6f ? MathF.Max(0f, Vector2.Dot(Flat(m.Velocity), Vector2.Normalize(toS))) : 0f;
            reach = MathF.Max(0.05f, Vector2.Distance(mp, s) - closing * 0.5f);
        }
        float q = a / 45f;
        return 1f / ((offside ? 5f : 1f) * MathF.Max(0.05f, d) * q * q * reach);
    }

    /// <summary>
    /// A team-mate's score as the receiver of a through ball: [2 + cos(angle between his velocity and the aim) + (his speed toward
    /// the point) / 8] / [(1 + 4 offside) reach], reach as for a ground pass.
    /// </summary>
    public static float ThroughScore(PlayerState m, Vector2 aim, Vector2 s, bool offside)
    {
        var mp = Flat(m.Position);
        var v = Flat(m.Velocity);
        float speed = v.Length();
        float cos = speed > 0.3f ? Vector2.Dot(v / speed, aim) : 0f;
        var toS = s - mp;
        float d = toS.Length();
        float closing = d > 1e-3f ? Vector2.Dot(v, toS / d) : 0f;
        float reach = d <= 0.5f ? 0.05f : MathF.Max(0.05f, d - MathF.Max(0f, closing) * 0.5f);
        return (2f + cos + closing / 8f) / ((offside ? 5f : 1f) * reach);
    }

    /// <summary>
    /// The receiver of a pass pointed along <paramref name="aim"/> with <paramref name="charge"/>: the best-scored team-mate
    /// further than pass.min_distance from the ball, or null when there is none (a pass into space). <paramref name="offside"/>
    /// says whether a team-mate is beyond the offside line now (he then scores a fifth).
    /// </summary>
    public static PlayerState? ChooseReceiver(PlayerState passer, IReadOnlyList<PlayerState> team, Vector3 ball, Vector2 aim, float charge,
        PassKind kind, Func<PlayerState, bool>? offside, Tuning t)
    {
        var s = SelectionPoint(ball, aim, charge, kind, t);
        PlayerState? best = null;
        float bestScore = float.NegativeInfinity;
        foreach (var m in team)
        {
            if (m == passer || m.Role != PlayerRole.Outfield) continue;
            if (Vector2.Distance(Flat(m.Position), Flat(ball)) <= t.Pass.MinDistance) continue;
            bool off = offside != null && offside(m);
            float score = kind == PassKind.Through ? ThroughScore(m, aim, s, off) : GroundScore(m, ball, aim, s, off);
            if (score > bestScore) { bestScore = score; best = m; }
        }
        return best;
    }

    // ---- the ball's travel (a pass leaves rolling: it slows at the rolling deceleration and nothing else) ----

    /// <summary>The launch speed that brings a rolling ball <paramref name="distance"/> metres on at <paramref name="arrival"/> m/s.</summary>
    public static float LaunchSpeed(float distance, float arrival, Tuning t) =>
        MathF.Sqrt(arrival * arrival + 2f * t.Ball.RollDeceleration * MathF.Max(0f, distance));

    /// <summary>Seconds a ball rolled off at <paramref name="speed"/> takes to <paramref name="distance"/> metres; infinity if it stops short.</summary>
    public static float TravelSeconds(float distance, float speed, Tuning t)
    {
        float a = t.Ball.RollDeceleration;
        float disc = speed * speed - 2f * a * MathF.Max(0f, distance);
        if (disc < 0f) return float.PositiveInfinity;
        return a > 1e-6f ? (speed - MathF.Sqrt(disc)) / a : distance / MathF.Max(1e-3f, speed);
    }

    /// <summary>
    /// The fastest the passer can strike this pass: the speed cap by his rating for the kind (min at 1, max at 99), times a shot's
    /// power for the body angle to <paramref name="yaw"/> and for a weak-foot strike.
    /// </summary>
    public static float SpeedCap(PlayerState passer, PassKind kind, float yaw, Foot foot, Tuning t)
    {
        var k = t.Pass;
        int rating = kind == PassKind.Through ? passer.Sheet.ThroughPass : passer.Sheet.GroundPass;
        float cap = k.SpeedCap.Min + (k.SpeedCap.Max - k.SpeedCap.Min) * Math.Clamp(rating / 99f, 0f, 1f);
        float angle = MathF.Max(0f, t.Shot.AnglePower.Evaluate(ShotModel.BodyAngleDeg(passer.Facing, yaw)));
        float weak = foot == passer.Sheet.PreferredFoot ? 1f : MathF.Max(0f, t.Shot.WeakFootPower.Evaluate(passer.Sheet.WeakFoot));
        return cap * angle * weak;
    }

    /// <summary>From the press to the strike, seconds: pass.execution_seconds by the charge plus pass.angle_extra_seconds by the body angle to the pass.</summary>
    public static float ExecutionSeconds(PlayerState passer, float charge, float yaw, Tuning t) =>
        MathF.Max(0f, t.Pass.ExecutionSeconds.Evaluate(Math.Clamp(charge, 0f, 1f)))
        + MathF.Max(0f, t.Pass.AngleExtraSeconds.Evaluate(ShotModel.BodyAngleDeg(passer.Facing, yaw)));

    // ---- the defender against a lane ----

    /// <summary>How close to a lane an opponent must get to cut the pass out, metres (his body and a stride of his leg).</summary>
    const float InterceptReach = 0.9f;
    /// <summary>The lane is checked at points this far apart, metres.</summary>
    const float LaneStep = 1f;

    /// <summary>
    /// How much later than the ball the opponents are on the lane from <paramref name="from"/> to <paramref name="to"/> at their
    /// best, seconds, for a ball rolled off at <paramref name="speed"/>: at each point of the lane, the earliest opponent's
    /// arrival (the gap beyond his reach run from his speed toward it now, see <see cref="RunSeconds"/>, after <paramref name="delay"/>) less the ball's. Negative: one of
    /// them is there first. Infinity with no opponent. The keeper counts only for points he can come to inside his area.
    /// </summary>
    public static float LaneMargin(Vector2 from, Vector2 to, float speed, IReadOnlyList<PlayerState> opponents, float delay, Pitch? pitch, Tuning t)
    {
        var lane = to - from;
        float len = lane.Length();
        if (len < 1e-3f) return float.PositiveInfinity;
        var dir = lane / len;
        float margin = float.PositiveInfinity;
        foreach (var o in opponents)
        {
            bool keeper = o.Role == PlayerRole.Keeper;
            float top = keeper ? Keeper.RushSpeed(o.Sheet, t) : Movement.TopSpeed(o.Sheet, t);
            float tau = Movement.TimeConstant(o.Sheet, t);
            var op = Flat(o.Position);
            var ov = Flat(o.Velocity);
            for (float d = 0f; ; d += LaneStep)
            {
                if (d > len) d = len;
                var at = from + dir * d;
                if (!keeper || pitch == null || pitch.InPenaltyArea(new Vector3(at.X, 0f, at.Y)))
                {
                    float ball = TravelSeconds(d, speed, t);
                    var toAt = at - op;
                    float gap = toAt.Length();
                    float closing = gap > 1e-3f ? MathF.Max(0f, Vector2.Dot(ov, toAt / gap)) : 0f;
                    float his = delay + RunSeconds(MathF.Max(0f, gap - InterceptReach), closing, MathF.Max(1f, top), tau);
                    margin = MathF.Min(margin, his - ball);
                }
                if (d >= len) break;
            }
        }
        return margin;
    }

    /// <summary>
    /// Seconds a player takes to cover <paramref name="distance"/> metres from <paramref name="speed"/> m/s toward it, speeding
    /// up to <paramref name="top"/> on the movement law's time constant <paramref name="tau"/> (dv/dt = (top - v) / tau).
    /// </summary>
    public static float RunSeconds(float distance, float speed, float top, float tau)
    {
        if (distance <= 0f) return 0f;
        float lack = MathF.Max(0f, top - speed) * MathF.Max(0f, tau);
        float seconds = distance / top;
        for (int i = 0; i < 4; i++) seconds = (distance + lack * (1f - MathF.Exp(-seconds / MathF.Max(1e-3f, tau)))) / top;
        return seconds;
    }

    // ---- the target ----

    /// <summary>Keeps a target a pass can be played to: 2 m inside the boards and 3 m short of the goal line.</summary>
    static Vector2 Inside(Vector2 p, Pitch? pitch)
    {
        if (pitch == null) return p;
        float x = Math.Clamp(p.X, pitch.MinX + 2f, pitch.GoalLineX - 3f);
        float z = Math.Clamp(p.Y, -(pitch.HalfWidth - 2f), pitch.HalfWidth - 2f);
        return new Vector2(x, z);
    }

    /// <summary>Where the receiver will be after <paramref name="seconds"/>: on at his velocity, no faster than his top speed.</summary>
    static Vector2 Ahead(PlayerState m, float seconds) => Flat(m.Position) + Flat(m.Velocity) * seconds;

    /// <summary>
    /// The pass struck now: the receiver (<see cref="ChooseReceiver"/>), the target and the launch speed.
    /// <para>
    /// Ground pass: candidate points on radii 0.85 to 1.25 of the receiver's distance from the ball, fanned round the bearing to
    /// where he will be when the ball arrives; a point scores on the defender's lateness on the lane to it (a lane he is on first
    /// is rejected), on how easily the receiver reaches it by then, on cos^3 of its angle to the aim and on its closeness to
    /// where he will be. With none valid the pass goes to where he will be.
    /// </para>
    /// <para>
    /// Through ball: candidate points along his run pass.through_ahead_seconds ahead of him (toward the goal when he stands),
    /// fanned up to 15 degrees toward the aim, scored on the defender's lateness and the angle to the aim, and on the ball
    /// getting there just before he does, so he takes it in his stride.
    /// </para>
    /// The speed is the least that brings the ball to the target at pass.arrival_speed (for a through ball, also no less than
    /// gets it there when the receiver does), raised in steps of pass.speed_step while
    /// the defender is still on the lane first, up to the passer's cap; if the cap cannot beat him the pass is played at the cap
    /// anyway. No receiver: a pass into space along the aim at the selection distance.
    /// </summary>
    /// <param name="delay">The opponents' reaction delay the lane test allows them, seconds.</param>
    public static PassPlan Plan(PlayerState passer, IReadOnlyList<PlayerState> team, IReadOnlyList<PlayerState> opponents, Vector3 ball, Vector2 stick,
        float charge, PassKind kind, Foot foot, Func<PlayerState, bool>? offside, float delay, Pitch? pitch, Tuning t, float capScale = 1f)
    {
        var k = t.Pass;
        var from = Flat(ball);
        var aim = Aim(passer, stick, t);
        var receiver = ChooseReceiver(passer, team, ball, aim, charge, kind, offside, t);
        float arrival = k.ArrivalSpeed.For(kind);

        // (meetSeconds: when the receiver gets there; a ball that would be later than that is struck hard enough to be on time)
        PassPlan To(Vector2 target, PlayerState? who, float meetSeconds = float.NaN)
        {
            var lane = target - from;
            float len = lane.Length();
            var dir = len > 1e-3f ? lane / len : aim;
            float cap = SpeedCap(passer, kind, MathF.Atan2(dir.Y, dir.X), foot, t) * capScale;
            float speed = LaunchSpeed(len, arrival, t);
            if (float.IsFinite(meetSeconds) && meetSeconds > 0.05f) speed = MathF.Max(speed, len / meetSeconds + 0.5f * t.Ball.RollDeceleration * meetSeconds);
            speed = MathF.Min(cap, speed);
            float margin = LaneMargin(from, target, speed, opponents, delay, pitch, t);
            while (margin < 0f && speed < cap - 1e-3f)
            {
                speed = MathF.Min(cap, speed + MathF.Max(0.1f, k.SpeedStep));
                margin = LaneMargin(from, target, speed, opponents, delay, pitch, t);
            }
            return new PassPlan(who, new Vector3(target.X, t.Ball.Radius, target.Y), dir, speed, TravelSeconds(len, speed, t), margin);
        }

        if (receiver == null)
            return To(Inside(SelectionPoint(ball, aim, charge, kind, t), pitch), null);

        // Where he will be when the ball gets to him (the ball's time to his spot, taken twice).
        var rp = Flat(receiver.Position);
        float eta = TravelSeconds(Vector2.Distance(from, rp), LaunchSpeed(Vector2.Distance(from, rp), arrival, t), t);
        var will = Inside(Ahead(receiver, eta), pitch);
        eta = TravelSeconds(Vector2.Distance(from, will), LaunchSpeed(Vector2.Distance(from, will), arrival, t), t);
        will = Inside(Ahead(receiver, eta), pitch);
        float top = MathF.Max(1f, Movement.TopSpeed(receiver.Sheet, t));

        PassPlan best = default;
        float bestScore = float.NegativeInfinity;
        void Consider(Vector2 point, bool closeness)
        {
            point = Inside(point, pitch);
            // Can he be there by the ball's arrival? (his distance beyond a stride, at his top speed)
            float need = MathF.Max(0f, Vector2.Distance(rp, point) - 0.7f) / top;
            // (a through ball into the path of a man running away from the passer has to travel faster than he runs)
            var plan = closeness ? To(point, receiver) : To(point, receiver, need - ThroughBestLeadSeconds);
            if (plan.Margin < 0f || !float.IsFinite(plan.ArrivalSeconds)) return;
            float spare = plan.ArrivalSeconds - need;
            var to = point - from;
            float cos = to.LengthSquared() > 1e-6f ? Vector2.Dot(Vector2.Normalize(to), aim) : 1f;
            float score = MathF.Min(1f, plan.Margin) + cos * cos * cos;
            if (closeness)
            {
                // To feet: he must be there by the ball's arrival, the sooner the better.
                if (spare < 0f) return;
                score += MathF.Min(1f, spare) + 1f / (1f + Vector2.Distance(point, will));
            }
            else
            {
                // Into space: the ball should be there just before him, so he takes it in his stride; a point he would wait
                // at, or one the ball reaches long before him, is not one.
                if (spare > ThroughWaitSeconds || spare < -ThroughLeadSeconds) return;
                score += 1f - MathF.Abs(spare + ThroughBestLeadSeconds);
                // (and the gentler it comes the better: the further ahead, the less it has to outrun him)
                float arrive = MathF.Sqrt(MathF.Max(0f, plan.Speed * plan.Speed - 2f * t.Ball.RollDeceleration * to.Length()));
                score -= MathF.Max(0f, arrive - arrival) / 10f;
            }
            if (score > bestScore) { bestScore = score; best = plan; }
        }

        if (kind == PassKind.Through)
        {
            // Along his run (toward the goal when he stands), fanned toward the aim.
            var v = Flat(receiver.Velocity);
            var run = v.Length() > 0.5f ? Vector2.Normalize(v) : new Vector2(1f, 0f);
            float runYaw = MathF.Atan2(run.Y, run.X), aimYaw = MathF.Atan2(aim.Y, aim.X);
            float toward = Movement.WrapAngle(aimYaw - runYaw);
            float lo = k.ThroughAheadSeconds.Length > 0 ? k.ThroughAheadSeconds[0] : 0.6f, hi = k.ThroughAheadSeconds.Length > 1 ? k.ThroughAheadSeconds[1] : 1.4f;
            for (int i = 0; i <= 4; i++)
            {
                float ahead = lo + (hi - lo) * i / 4f;
                for (int f = 0; f <= 2; f++)
                {
                    float yaw = runYaw + Math.Clamp(toward, -1f, 1f) * (f * 7.5f * MathF.PI / 180f) * (MathF.Abs(toward) > 1e-3f ? 1f : 0f);
                    Consider(rp + new Vector2(MathF.Cos(yaw), MathF.Sin(yaw)) * (top * ahead), closeness: false);
                }
            }
            if (bestScore == float.NegativeInfinity)
            {
                var fallback = Inside(rp + run * (top * hi), pitch);
                return To(fallback, receiver, MathF.Max(0f, Vector2.Distance(rp, fallback) - 0.7f) / top - ThroughBestLeadSeconds);
            }
            return best;
        }

        float dist = Vector2.Distance(from, will);
        var bearing = dist > 1e-3f ? (will - from) / dist : aim;
        float baseYaw = MathF.Atan2(bearing.Y, bearing.X);
        foreach (float radius in Radii)
            foreach (float fan in Fan)
            {
                float yaw = baseYaw + fan * MathF.PI / 180f;
                Consider(from + new Vector2(MathF.Cos(yaw), MathF.Sin(yaw)) * (dist * radius), closeness: true);
            }
        return bestScore == float.NegativeInfinity ? To(will, receiver) : best;
    }

    /// <summary>A through ball's target: the receiver may wait for the ball no longer than this, the ball may be there no longer than this before him, and best this long before him, seconds.</summary>
    const float ThroughWaitSeconds = 0.1f, ThroughLeadSeconds = 0.6f, ThroughBestLeadSeconds = 0.15f;

    static readonly float[] Radii = { 1f, 0.85f, 0.95f, 1.1f, 1.25f };
    static readonly float[] Fan = { 0f, -6f, 6f, -12f, 12f };
}
