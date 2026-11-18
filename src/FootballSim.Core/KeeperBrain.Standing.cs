using System.Numerics;

namespace FootballSim.Core;

// Standing: the arc or the closing-down spot, the rush race (with anticipation), staying big, and planting for a shot.
public sealed partial class KeeperBrain
{
    // Closing down.
    bool _engaged;              // an attacker has come in with the ball this round: he closes down
    float _chipErr = float.NaN; // his error judging his chip-safe depth, a fraction of it (drawn once a round)

    // Rush anticipation.
    bool _anticipating;

    // Round record (before the first strike).
    readonly float[] _bandDepth = NewBands();
    bool _roundRounded, _roundWon;
    // Depth reversals: the direction he last held (+1 out, -1 back, 0 none), a new direction and how long it has held.
    int _depthDir, _depthNew;
    float _depthNewFor, _clock;
    int _reversals, _reversals2;

    static float[] NewBands()
    {
        var a = new float[KeeperReport.DepthBands.Length];
        Array.Fill(a, float.NaN);
        return a;
    }

    void StepStanding(float dt, Ball ball, SimRng rng, in KeeperSituation s)
    {
        var k = _t.Keeper;
        bool holdLine = _holdLine && !_struck;
        FaceBall(ball, dt);
        Observe(ball, s);

        if (!holdLine && !s.ShotInFlight && !s.BallDead)
        {
            var race = Race(ball, s, rng);
            if (race.Win && race.Smother) { _anticipating = false; StartSmother(race.Point); return; }
            if (race.Win)
            {
                _rushing = true;
                _anticipating = false;
                if (!_struck) { _roundRushed = true; Report.Rushed = true; }
                RunAt(race.Point, Keeper.RushSpeed(_k.Sheet, _t), dt);
                _hands = HomeFor(_k.Position);
                SetAction(KeeperAction.Rush);
                return;
            }
            if (race.Anticipate)
            {
                // Not sure of the race yet, but it is close: he starts toward the ball, ready to abort.
                _anticipating = true;
                RunAt(race.Point, Keeper.RushSpeed(_k.Sheet, _t) * k.AnticipateSpeedFraction, dt);
                _hands = HomeFor(_k.Position);
                SetAction(KeeperAction.Rush);
                return;
            }
            if (_rushing || _anticipating) { _rushing = false; _anticipating = false; _hasSpot = false; }
        }
        else { _rushing = false; _anticipating = false; }

        var goal = new Vector2(_pitch.GoalLineX, 0f);
        var ball2 = new Vector2(ball.Position.X, ball.Position.Z);
        var feet = new Vector2(_k.Position.X, _k.Position.Z);
        float toBall = Vector2.Distance(ball2, feet);
        bool inFront = Keeper.MouthDistance(ball2, _pitch) > Keeper.MouthDistance(feet, _pitch) + 0.3f;
        bool close = !holdLine && inFront && toBall < k.PreSpreadRange;
        bool inRange = Vector2.Distance(ball2, goal) <= k.SetRange;
        // He plants for the strike: in its last plant_lead, or earlier if he needs longer than that to brake to a stop.
        float plantLead = MathF.Max(k.PlantLead, new Vector2(_k.Velocity.X, _k.Velocity.Z).Length() / k.PlantDecel + 0.05f);
        if (s.Windup && s.StrikeIn <= plantLead && inRange && !s.BallDead)
        {
            Brake(k.PlantDecel, dt);
            _hands = HomeFor(_k.Position);
            _shuffling = false;
            SetAction(close ? KeeperAction.Spread : KeeperAction.Planted);
            return;
        }

        // Staying big: an attacker on the ball within a couple of metres that he cannot beat to it. He stops and spreads
        // rather than standing on his spot; once the ball is level with him or past, he goes back to covering the goal.
        if (close && s.AttackerHasBall && !s.BallDead && !s.ShotInFlight)
        {
            Brake(k.PlantDecel, dt);
            _hands = HomeFor(_k.Position);
            _shuffling = false;
            SetAction(KeeperAction.Spread);
            return;
        }

        // Hold the arc (or the line), or close down an attacker who has come in with the ball.
        var look = ball.Position + new Vector3(ball.Velocity.X, 0f, ball.Velocity.Z) * k.ArcLookahead;
        Vector3 ideal = holdLine ? Keeper.LineSpot(look, _pitch, _t) : Keeper.ArcTarget(look, _pitch, _t, _k.Sheet);
        bool engage = false;
        if (!holdLine && !s.BallDead && !s.ShotInFlight && _engaged) ideal = CloseDownSpot(look, ideal, s.AttackerHasBall && inFront && toBall < k.EngageRange, rng, out engage);
        // Not closing down yet, but the attacker has the ball in front of him: he keeps the depth he has within his limits
        // rather than dropping back to his arc only to come out again a moment later.
        else if (!holdLine && !s.BallDead && !s.ShotInFlight && s.AttackerHasBall && inFront) ideal = CloseDownSpot(look, ideal, false, rng, out _, advance: false);
        if (!holdLine && s.AttackerHasBall && !s.BallDead) ideal = Committed(ideal);
        if (!_hasSpot || Vector3.Distance(ideal, _spotIdeal) > k.RepositionThreshold) TakeSpot(ideal, look, holdLine, rng);
        float dist = new Vector2(_spot.X - _k.Position.X, _spot.Z - _k.Position.Z).Length();
        // Getting back goal-side (recovering after being drawn out or rounded) close to goal is a run, not a jog.
        bool urgent = engage || !holdLine && Keeper.MouthDistance(new Vector2(look.X, look.Z), _pitch) <= k.UrgentRange
                      && Keeper.MouthDistance(new Vector2(_spot.X, _spot.Z), _pitch) < Keeper.MouthDistance(feet, _pitch) - 0.5f;
        float vmax = dist <= k.JogThreshold ? Keeper.ShuffleSpeed(_k.Sheet, _t)
            : urgent ? Keeper.RushSpeed(_k.Sheet, _t) : Keeper.JogSpeed(_k.Sheet, _t);
        MoveTo(_spot, vmax, Keeper.MoveAccel(_k.Sheet, _t), dt);
        _hands = HomeFor(_k.Position);
        var vel = new Vector2(_k.Velocity.X, _k.Velocity.Z);
        float speed = vel.Length();
        dist = new Vector2(_spot.X - _k.Position.X, _spot.Z - _k.Position.Z).Length();
        if (_shuffling) { if (speed < 0.2f && dist < 0.1f) _shuffling = false; }
        else if (speed > k.StillSpeed || dist > 0.15f) _shuffling = true;
        var face = new Vector2(MathF.Cos(_k.Facing), MathF.Sin(_k.Facing));
        bool back = Vector2.Dot(vel, face) < -k.BackpedalShowSpeed;
        SetAction(back ? KeeperAction.Backpedal : _shuffling ? KeeperAction.Shuffle : KeeperAction.Set);
    }

    /// <summary>
    /// Commitment: moving off or toward his line, he does not turn round for a small change of mind. When the new spot's depth
    /// is the other way from where his braking would stop him, by less than <c>reverse_threshold</c>, he settles at that
    /// stopping depth instead (still on the new spot's line across).
    /// </summary>
    Vector3 Committed(Vector3 ideal)
    {
        var k = _t.Keeper;
        float vOut = -_k.Velocity.X;
        if (MathF.Abs(vOut) <= 0.3f) return ideal;
        float stop = _pitch.GoalLineX - _k.Position.X + vOut * MathF.Abs(vOut) / (2f * Keeper.MoveAccel(_k.Sheet, _t));
        float want = _pitch.GoalLineX - ideal.X;
        if (MathF.Sign(want - stop) == MathF.Sign(vOut) || MathF.Abs(want - stop) >= k.ReverseThreshold) return ideal;
        return new Vector3(MathF.Min(_pitch.GoalLineX - stop, _pitch.GoalLineX - k.ArcMinFromLine), ideal.Y, ideal.Z);
    }

    /// <summary>
    /// Closing down: the spot on the cover line from the ball at the depth he wants for the ball's distance (see
    /// <see cref="Keeper.CloseDownDistance"/>), never shallower than his arc. He does not come further out than his judged
    /// chip-safe depth, than <c>close_down_gap</c> short of the ball, or than <c>hold_ground_max_out</c>; between what he wants
    /// and those limits he holds the depth he has (no backpedalling while the attacker runs at him). With the attacker on the
    /// ball within <c>engage_range</c> of him (<paramref name="near"/>) he pushes up to the attacker, to just inside his spread
    /// range, whatever he wanted (<paramref name="engage"/>). With <paramref name="advance"/> false he only holds: he wants no
    /// more than his arc.
    /// </summary>
    Vector3 CloseDownSpot(Vector3 look, Vector3 arc, bool near, SimRng rng, out bool engage, bool advance = true)
    {
        var k = _t.Keeper;
        var b = new Vector2(look.X, look.Z);
        float bMouth = Keeper.MouthDistance(b, _pitch);
        float arcD = Keeper.MouthDistance(new Vector2(arc.X, arc.Z), _pitch);
        float want = advance ? Keeper.CloseDownDistance(_k.Sheet, _t, bMouth, arcD) : arcD;
        float chipCap = MathF.Min(ChipSafeJudged(Vector2.Distance(b, new Vector2(_pitch.GoalLineX, 0f)), rng), k.HoldGroundMaxOut);
        float mine = Keeper.MouthDistance(new Vector2(_k.Position.X, _k.Position.Z), _pitch);
        if (near)
        {
            // Pushing up: on to just inside his spread range of the ball, never back while the attacker comes at him.
            float to = MathF.Max(arcD, MathF.Min(chipCap, MathF.Max(mine, bMouth - (k.PreSpreadRange - 0.3f))));
            engage = to > mine + 0.3f;
            return Keeper.CoverSpot(look, _pitch, _t, to, halfWay: false);
        }
        engage = false;
        float cap = MathF.Min(chipCap, bMouth - k.CloseDownGap);
        float lo = MathF.Max(arcD, MathF.Min(want, cap));
        float hi = MathF.Max(lo, cap);
        return Keeper.CoverSpot(look, _pitch, _t, Math.Clamp(mine, lo, hi), halfWay: false);
    }

    /// <summary>His chip-safe depth for a ball <paramref name="ballDistance"/> metres from the goal centre, as he judges it.</summary>
    float ChipSafeJudged(float ballDistance, SimRng rng)
    {
        if (float.IsNaN(_chipErr))
        {
            float sd = Keeper.ChipJudgeErrorSd(_k.Sheet, _t);
            _chipErr = Math.Clamp(rng.Normal(0f, sd), -2f * sd, 2f * sd);
        }
        return Keeper.ChipSafeDepth(_k.Sheet, _attacker, _t, _pitch, ballDistance) * (1f + _chipErr);
    }

    /// <summary>
    /// What he reads of the attacker before the first strike: whether he has come in with the ball (closing down starts and
    /// stays on), the keeper's depth as the ball first comes within each report band, and whether the attacker got the ball
    /// past him (rounded).
    /// </summary>
    void Observe(Ball ball, in KeeperSituation s)
    {
        CountReversal();
        if (_struck || !s.AttackerHasBall || s.BallDead) return;
        var k = _t.Keeper;
        var b = new Vector2(ball.Position.X, ball.Position.Z);
        var feet = new Vector2(_k.Position.X, _k.Position.Z);
        float bMouth = Keeper.MouthDistance(b, _pitch);
        if (!_engaged && bMouth <= k.CloseDownRange)
        {
            var toGoal = new Vector2(_pitch.GoalLineX, 0f) - b;
            float len = toGoal.Length();
            var av = new Vector2(s.AttackerVelocity.X, s.AttackerVelocity.Z);
            if (len > 1e-3f && Vector2.Dot(av, toGoal / len) >= k.CloseDownMinSpeed) _engaged = true;
        }
        float bd = Vector2.Distance(b, new Vector2(_pitch.GoalLineX, 0f));
        var bands = KeeperReport.DepthBands;
        for (int i = 0; i < bands.Length; i++)
            if (float.IsNaN(_bandDepth[i]) && bd <= bands[i]) { _bandDepth[i] = _pitch.GoalLineX - _k.Position.X; Report.DepthAtBandM[i] = _bandDepth[i]; }
        if (!_roundRounded && bMouth < Keeper.MouthDistance(feet, _pitch) - 0.5f && Vector2.Distance(b, feet) < 5f
            && _pitch.GoalLineX - _k.Position.X > 1.5f)
        {
            _roundRounded = true;
            Report.Rounded = true;
        }
    }

    /// <summary>Counts a reversal of his movement off or toward his line (see <see cref="KeeperReport.DepthReversals"/>).</summary>
    void CountReversal()
    {
        _clock += _t.Dt;
        if (_struck) return;
        float vOut = -_k.Velocity.X;
        int dir = vOut > 0.3f ? 1 : vOut < -0.3f ? -1 : 0;
        if (dir == 0 || dir == _depthDir) { _depthNew = 0; _depthNewFor = 0f; return; }
        if (dir != _depthNew) { _depthNew = dir; _depthNewFor = 0f; }
        _depthNewFor += _t.Dt;
        if (_depthNewFor <= 0.1f) return;
        if (_depthDir != 0)
        {
            _reversals++;
            if (_clock <= 2f) _reversals2++;
            Report.DepthReversals = _reversals;
            Report.DepthReversalsFirst2S = _reversals2;
        }
        _depthDir = dir;
        _depthNew = 0;
        _depthNewFor = 0f;
    }

    /// <summary>A new spot: the ideal plus a positioning error drawn now, across the cover line (and a little along it).</summary>
    void TakeSpot(Vector3 ideal, Vector3 ball, bool holdLine, SimRng rng)
    {
        var k = _t.Keeper;
        _spotIdeal = ideal;
        _hasSpot = true;
        float sd = Keeper.PositionErrorSd(_k.Sheet, _t);
        float across = Math.Clamp(rng.Normal(0f, sd), -3f * sd, 3f * sd);
        float along = holdLine ? 0f : Math.Clamp(rng.Normal(0f, 0.5f * sd), -1.5f * sd, 1.5f * sd);
        Vector2 cover = holdLine ? new Vector2(1f, 0f) : Keeper.CoverDirection(new Vector2(ball.X, ball.Z), _pitch, _t);
        var lateral = new Vector2(-cover.Y, cover.X);
        var p = new Vector2(ideal.X, ideal.Z) + lateral * across + cover * along;
        if (!holdLine) p.X = MathF.Min(p.X, _pitch.GoalLineX - k.ArcMinFromLine);
        float lim = _pitch.GoalHalfWidth + 1f;
        p.Y = Math.Clamp(p.Y, -lim, lim);
        _spot = new Vector3(p.X, 0f, p.Y);
    }

    readonly struct RaceResult
    {
        public bool Win { get; init; }
        public bool Smother { get; init; }
        /// <summary>Not won, but within his anticipation of winning: he starts toward <see cref="Point"/>.</summary>
        public bool Anticipate { get; init; }
        public Vector3 Point { get; init; }
    }

    /// <summary>
    /// The rush race: the earliest point on the loose ball's predicted path he can reach (to within a smother dive) before the
    /// ball gets there and before the attacker's next touch, with the margin his rushing rating wants (none once he is rushing,
    /// and none for a ball within a dive: he lunges at a ball going past him), judged with his misjudgement (drawn per attacker
    /// touch). The more time the ball gives him over the attacker, the further out he wins it. Short of the margin by less than
    /// his anticipation (<c>rush_anticipation</c>), he starts toward the ball and aborts if the race turns against him. Only for
    /// a low ball he can take within the rush limit.
    /// </summary>
    RaceResult Race(Ball ball, in KeeperSituation s, SimRng rng)
    {
        var k = _t.Keeper;
        var goal = new Vector2(_pitch.GoalLineX, 0f);
        var b0 = new Vector2(ball.Position.X, ball.Position.Z);
        if (ball.Position.Y > k.ClaimMaxHeight + 0.5f) return default;
        if (Vector2.Distance(b0, goal) > k.RushMaxFromGoal + 4f) return default;
        float tAtt = s.AttackerNextTouch;
        var feet = new Vector2(_k.Position.X, _k.Position.Z);
        float accel = Keeper.MoveAccel(_k.Sheet, _t), vmax = Keeper.RushSpeed(_k.Sheet, _t);
        bool going = _rushing || _anticipating;
        // Quick rejection: even running flat out from now he cannot reach the ball's current spot before the attacker.
        float dNow = Vector2.Distance(feet, b0);
        if (!going && Keeper.TravelTime(dNow - k.SmotherReach - 8f, 0f, accel, vmax) > tAtt) return default;
        if (s.AttackerTouchTick != _misjudgeTick)
        {
            _misjudgeTick = s.AttackerTouchTick;
            _misjudge = rng.Normal(0f, Keeper.RushMisjudgeSd(_k.Sheet, _t));
        }
        float antic = k.RushAnticipation.ForRating(_k.Sheet.RushingOut);
        // Already on his way: he carries on while the race is still within his anticipation (less a little hysteresis).
        float keepOn = _anticipating ? antic + 0.05f : antic;
        float horizon = MathF.Min(k.RushLookahead, tAtt);
        var vel = new Vector2(_k.Velocity.X, _k.Velocity.Z);
        float start = going ? 0f : k.RushStartDelay;
        const float step = 1f / 60f;
        var b = ball.ScratchB();
        float tb = 0f;
        RaceResult anticipate = default;
        for (int i = 0; tb <= horizon + 1e-4f; i++)
        {
            if (i > 0) { b.Step(step, null, null); tb += step; }
            var p = b.Position;
            if (p.Y > k.ClaimMaxHeight) continue;
            var p2 = new Vector2(p.X, p.Z);
            if (p.X > _pitch.GoalLineX - 0.3f || Vector2.Distance(p2, goal) > k.RushMaxFromGoal) continue;
            var to = p2 - feet;
            float d = to.Length();
            float v0 = d > 1e-4f ? Vector2.Dot(vel, to / d) : 0f;
            bool dive = d <= k.SmotherReach;
            float tk = (dive ? 0f : start + Keeper.TravelTime(d - k.SmotherReach, v0, accel, vmax)) + MathF.Min(d, k.SmotherReach) / k.SmotherSpeed;
            float perceived = tk - _misjudge;
            float margin = _rushing || dive ? 0f : Keeper.RushMargin(_k.Sheet, _t, d);
            var point = new Vector3(p.X, _t.Ball.Radius, p.Z);
            bool rest = b.Velocity == Vector3.Zero && b.OnGround;
            // At rest the ball waits for him: he wins it if he gets there before the attacker, whenever that is.
            float arrive = rest ? perceived : tb;
            if ((rest || perceived <= tb) && arrive + margin <= tAtt)
                return new RaceResult { Win = true, Smother = dive, Point = point };
            if (!anticipate.Anticipate && antic > 0f && !dive && (rest || perceived <= tb) && arrive + margin - keepOn <= tAtt)
                anticipate = new RaceResult { Anticipate = true, Point = point };
            if (rest) break;
        }
        return anticipate;
    }
}
