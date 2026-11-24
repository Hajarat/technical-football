using System.Numerics;

namespace FootballSim.Core;

// The home keeper (keeper.brain "home"): his home depth, setting before the carrier is close, mirroring him, the pounce and the
// run back once taken round. The rush race, planting for a shot, the saves, his penalty area and the yield to a covering defender
// are the same for both keepers (KeeperBrain.Standing.cs).
public sealed partial class KeeperBrain
{
    /// <summary>The home keeper plays (keeper.brain is not "legacy").</summary>
    bool HomeBrain => _t.Keeper.Brain != "legacy";

    bool _set;                  // set on the carrier: braking into his stance, then mirroring him
    bool _settled;              // set and under set_speed once: from then on he mirrors
    bool _pounce;               // the smother under way is a pounce
    bool _roundPounced, _roundPounceWon;

    /// <summary>True while he is set on the carrier (braking into his stance or mirroring him; tests).</summary>
    internal bool IsSet => _set;

    /// <summary>The most he stands off his line closing down, metres from the mouth, for a ball this far from it (home keeper).</summary>
    float HomeCap(float ballMouth) => _t.Keeper.HomeDepthM.Evaluate(ballMouth);

    /// <summary>
    /// The home cap on a spot: no deeper off his line than his home depth for the ball at <paramref name="look"/> (never shallower
    /// than his arc for it, which the cap does not move).
    /// </summary>
    Vector3 CapHome(Vector3 spot, Vector3 look)
    {
        var lb = new Vector2(look.X, look.Z);
        var arc = Keeper.ArcTarget(look, _pitch, _t, _k.Sheet);
        float lim = MathF.Max(HomeCap(Keeper.MouthDistance(lb, _pitch)), _pitch.GoalLineX - arc.X);
        return spot.X < _pitch.GoalLineX - lim ? new Vector3(_pitch.GoalLineX - lim, spot.Y, spot.Z) : spot;
    }

    /// <summary>
    /// Taken round: the ball is nearer his goal line than he is by more than <c>rounded_margin_m</c>. He runs back at his rush
    /// speed to the point <c>recover_depth_m</c> off his line on the line from the ball to the goal centre (half way along it when
    /// the ball is closer to the line than that). Returns false when the ball is not past him.
    /// </summary>
    bool StepRecover(float dt, Vector2 ball)
    {
        var k = _t.Keeper;
        float ballDepth = _pitch.GoalLineX - ball.X;
        float mine = _pitch.GoalLineX - _k.Position.X;
        if (ballDepth >= mine - k.RoundedMarginM) return false;
        _set = false; _settled = false;
        var goal = new Vector2(_pitch.GoalLineX, 0f);
        Vector2 to;
        if (ballDepth <= 1e-3f)
            to = new Vector2(_pitch.GoalLineX - k.ArcMinFromLine, Math.Clamp(ball.Y, -(_pitch.GoalHalfWidth - k.LineInset), _pitch.GoalHalfWidth - k.LineInset));
        else
        {
            float depth = MathF.Min(k.RecoverDepthM, 0.5f * ballDepth);
            to = goal + (ball - goal) * (depth / ballDepth);
        }
        MoveTo(new Vector3(to.X, 0f, to.Y), Keeper.RushSpeed(_k.Sheet, _t), Keeper.MoveAccel(_k.Sheet, _t), dt);
        _hands = HomeFor(_k.Position);
        _hasSpot = false;
        _shuffling = false;
        SetAction(KeeperAction.Rush);
        return true;
    }

    /// <summary>
    /// Whether he is set on the carrier this tick: the attacker on the ball in front of him and closing, within <c>set_gap_m</c>
    /// plus the gap that closes while he brakes to a stop at <c>plant_decel</c> (his own run in, half of it, plus the attacker's
    /// approach). Once set he stays set while the attacker keeps the ball within <c>set_gap_m</c> + 2 m of him and it is not past
    /// him (see <see cref="StepRecover"/>).
    /// </summary>
    bool SetOn(in KeeperSituation s, Vector2 ball, Vector2 feet, float toBall, bool inFront)
    {
        var k = _t.Keeper;
        if (!s.AttackerHasBall) { _set = false; _settled = false; return false; }
        if (_set)
        {
            if (toBall <= k.SetGapM + 2f) return true;
            _set = false; _settled = false;
            return false;
        }
        if (!inFront) return false;
        var dir = toBall > 1e-3f ? (feet - ball) / toBall : new Vector2(1f, 0f);
        float approach = Vector2.Dot(new Vector2(s.AttackerVelocity.X, s.AttackerVelocity.Z), dir);
        if (approach <= k.StillSpeed) return false;
        var vel = new Vector2(_k.Velocity.X, _k.Velocity.Z);
        float tb = vel.Length() / k.PlantDecel;
        float toward = MathF.Max(0f, -Vector2.Dot(vel, dir));
        float closes = (0.5f * toward + approach) * tb;
        if (toBall > k.SetGapM + closes + 0.05f) return false;
        _set = true;
        _settled = false;
        return true;
    }

    /// <summary>
    /// Set on the carrier: he brakes at <c>plant_decel</c> to under <c>set_speed</c>, then mirrors him: each tick he moves to
    /// <see cref="MirrorSpot"/> at up to <c>mirror_speed</c> (positioning) sideways (across the goal) and
    /// <c>backpedal_speed</c> toward his line. Shown as Set (Spread within <c>pre_spread_range</c>) while still, Shuffle or
    /// Backpedal while moving.
    /// </summary>
    KeeperMode StepSet(float dt, Vector2 ball, Vector2 feet, float toBall)
    {
        var k = _t.Keeper;
        var vel = new Vector2(_k.Velocity.X, _k.Velocity.Z);
        _hands = HomeFor(_k.Position);
        _hasSpot = false;
        if (!_settled && vel.Length() >= k.SetSpeed)
        {
            Brake(k.PlantDecel, dt);
            _shuffling = false;
            SetAction(toBall < k.PreSpreadRange ? KeeperAction.Spread : KeeperAction.Set);
            return KeeperMode.Set;
        }
        _settled = true;
        var spot = MirrorSpot(ball, feet, _pitch, _t, MathF.Max(HomeCap(Keeper.MouthDistance(ball, _pitch)), ArcDepthFor(ball)));
        var d = spot - feet;
        float dist = d.Length();
        float accel = Keeper.MoveAccel(_k.Sheet, _t);
        float vmax = Keeper.MirrorSpeed(_k.Sheet, _t);
        Vector2 desired = dist > 0.01f ? d / dist * MathF.Min(vmax, MathF.Sqrt(2f * accel * MathF.Max(0f, dist - 0.01f))) : Vector2.Zero;
        // Across the line to the ball he side-steps at up to mirror_speed; toward goal he backpedals no faster than his backpedal
        // speed.
        var goal = new Vector2(_pitch.GoalLineX, 0f);
        var outward = feet - goal;
        outward = outward.LengthSquared() > 1e-6f ? Vector2.Normalize(outward) : new Vector2(-1f, 0f);
        float radial = Vector2.Dot(desired, outward);
        var across = desired - outward * radial;
        radial = Math.Clamp(radial, -Keeper.BackpedalSpeed(_k.Sheet, _t), vmax);
        if (across.Length() > vmax) across *= vmax / across.Length();
        Steer(across + outward * radial, accel, dt);
        _hands = HomeFor(_k.Position);
        var v = new Vector2(_k.Velocity.X, _k.Velocity.Z);
        float speed = v.Length();
        var face = new Vector2(MathF.Cos(_k.Facing), MathF.Sin(_k.Facing));
        bool backpedal = Vector2.Dot(v, face) < -k.BackpedalShowSpeed;
        _shuffling = speed > k.StillSpeed;
        SetAction(backpedal ? KeeperAction.Backpedal : _shuffling ? KeeperAction.Shuffle
            : toBall < k.PreSpreadRange ? KeeperAction.Spread : KeeperAction.Set);
        return _shuffling ? KeeperMode.Mirror : KeeperMode.Set;
    }

    /// <summary>The depth off his line of his arc spot for a ball at <paramref name="ball"/>, metres.</summary>
    float ArcDepthFor(Vector2 ball) => _pitch.GoalLineX - Keeper.ArcTarget(new Vector3(ball.X, 0f, ball.Y), _pitch, _t, _k.Sheet).X;

    /// <summary>
    /// The mirror spot for a ball at <paramref name="ball"/> and a keeper at <paramref name="feet"/>: on the line from the ball to
    /// the goal centre, at his own distance from the goal centre less however much the ball's distance from it has fallen below
    /// his own plus <c>mirror_lead_m</c> (so min(his distance, the ball's less <c>mirror_lead_m</c>)), no deeper off his line than
    /// <paramref name="maxDepth"/> (his home depth), never nearer the goal centre than <c>arc_min_from_line</c> and never behind
    /// the line <c>arc_min_from_line</c> in front of his goal line. A ball on the goal centre gives the line straight out from
    /// the goal.
    /// </summary>
    internal static Vector2 MirrorSpot(Vector2 ball, Vector2 feet, Pitch pitch, Tuning t, float maxDepth = float.PositiveInfinity)
    {
        var k = t.Keeper;
        var goal = new Vector2(pitch.GoalLineX, 0f);
        var fromGoal = ball - goal;
        float dB = fromGoal.Length();
        var dir = dB > 1e-3f ? fromGoal / dB : new Vector2(-1f, 0f);
        float dK = Vector2.Distance(feet, goal);
        float r = MathF.Min(dK, dB - k.MirrorLeadM);
        if (-dir.X > 1e-3f) r = MathF.Min(r, maxDepth / -dir.X);
        r = MathF.Max(k.ArcMinFromLine, r);
        var spot = goal + dir * r;
        spot.X = MathF.Min(spot.X, pitch.GoalLineX - k.ArcMinFromLine);
        return spot;
    }

    /// <summary>
    /// The pounce, while set: a smother at the first point on the ball's predicted path, low (below <c>slide_max_height</c>) and
    /// in his penalty area, within <c>pounce_reach_m</c> (diving) of his hull, that his hands get to (at the smother's speed, from
    /// his feet) no later than the ball and at least <c>pounce_margin_s</c> (reflexes) before the attacker's next touch, as he
    /// judges it with his rush misjudgement (drawn per attacker touch, as for the rush race). The forecast is the one published
    /// this tick: a move that changes the attacker's next touch after he goes is a feint that beats him.
    /// </summary>
    bool TryPounce(Ball ball, in KeeperSituation s, SimRng rng, out Vector3 point)
    {
        var k = _t.Keeper;
        point = default;
        float tAtt = s.AttackerNextTouch;
        if (!(tAtt > 0f) || !HandsAllowedAt(ball.Position) || ball.Position.Y > k.ClaimMaxHeight) return false;
        if (s.AttackerTouchTick != _misjudgeTick)
        {
            _misjudgeTick = s.AttackerTouchTick;
            _misjudge = rng.Normal(0f, Keeper.RushMisjudgeSd(_k.Sheet, _t));
        }
        var feet = new Vector2(_k.Position.X, _k.Position.Z);
        float reach = k.HullRadius + Keeper.PounceReach(_k.Sheet, _t);
        float margin = Keeper.PounceMargin(_k.Sheet, _t);
        float horizon = MathF.Min(k.RushLookahead, tAtt);
        const float step = 1f / 60f;
        var b = ball.ScratchB();
        float tb = 0f;
        for (int i = 0; tb <= horizon + 1e-4f; i++)
        {
            if (i > 0) { b.Step(step, null, null); tb += step; }
            var p = b.Position;
            bool rest = b.Velocity == Vector3.Zero && b.OnGround;
            if (p.Y <= k.SlideMaxHeight && p.X <= _pitch.GoalLineX - 0.3f && HandsAllowedAt(p))
            {
                float d = Vector2.Distance(new Vector2(p.X, p.Z), feet);
                if (d <= reach)
                {
                    float perceived = MathF.Max(0f, d - k.HullRadius) / k.SmotherSpeed - _misjudge;
                    if ((rest || perceived <= tb) && perceived + margin <= tAtt)
                    {
                        point = new Vector3(p.X, _t.Ball.Radius, p.Z);
                        return true;
                    }
                }
            }
            if (rest) break;
        }
        return false;
    }

    /// <summary>Starts a pounce: today's smother at the point, counted in the round's report.</summary>
    void StartPounce(Vector3 point)
    {
        _set = false; _settled = false;
        _anticipating = false;
        StartSmother(point);
        _pounce = true;
        if (!_struck) { _roundPounced = true; Report.Pounced = true; }
    }
}
