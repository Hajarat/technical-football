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

    // Charging: closing the attacker on the ball down at rush speed, then jockeying.
    bool _charging;
    bool _chargeSoon;           // he would charge but the ball is just outside the charge range: he gives no ground meanwhile

    // The diagnostic for this tick (see KeeperThought).
    KeeperLimit _tLimit;
    float _tWant = float.NaN, _tChip = float.NaN, _tGap = float.NaN, _tRaceShort = float.PositiveInfinity, _tCloseT = float.NaN, _tTol = float.NaN;
    string _tRaceBlock = "";

    void Think(KeeperMode mode) => Thought = new KeeperThought(mode, _tLimit, _engaged, _tWant, _tChip, _tGap, _tRaceShort, _tRaceBlock, _tCloseT, _tTol);

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
        // A penalty keeper stays on his line for the kick; once the taker dribbles the ball off the spot instead (it has
        // moved more than half a metre with no shot winding up), it is open play and he plays as anywhere else.
        _penaltySpot ??= ball.Position;
        if (_holdLine && !HoldLineThroughDribble && !_lineReleased && !s.Windup && !s.ShotInFlight
            && Vector2.Distance(new Vector2(ball.Position.X, ball.Position.Z), new Vector2(_penaltySpot.Value.X, _penaltySpot.Value.Z)) > 0.5f)
            _lineReleased = true;
        bool holdLine = _holdLine && !_struck && !_lineReleased;
        _tLimit = KeeperLimit.None; _tWant = _tChip = _tGap = _tCloseT = _tTol = float.NaN; _tRaceShort = float.PositiveInfinity; _tRaceBlock = holdLine ? "hold line" : "";
        FaceBall(ball, dt);
        Observe(ball, s);

        if (!holdLine && !s.ShotInFlight && !s.BallDead)
        {
            // The home keeper, set on the carrier, pounces on a ball pushed off his foot within his reach.
            if (HomeBrain && _set && TryPounce(ball, s, rng, out var pounceAt)) { StartPounce(pounceAt); Think(KeeperMode.Pounce); return; }
            var race = Race(ball, s, rng);
            var play = AreaPlayFor(race);
            // A defender covers the ball: the keeper leaves it to him and holds his arc. Only a ball within a dive is still his.
            if (s.DefenderCovers)
            {
                if (play != AreaPlay.Smother) play = AreaPlay.None;
                race = race with { Win = false, Anticipate = false };
                _tRaceBlock = "defender covers";
            }
            if (play != AreaPlay.None || race.Win || race.Anticipate) { _set = false; _settled = false; }
            switch (play)
            {
                case AreaPlay.Smother:
                    _anticipating = false; StartSmother(race.Point); Think(KeeperMode.Smother); return;
                case AreaPlay.Slide:
                case AreaPlay.Poke:
                    StartFeet(race.Point, race.Poke); Think(race.Poke ? KeeperMode.Poke : KeeperMode.Slide); return;
                case AreaPlay.HoldAtLine:
                    StepHoldAtLine(race.Point, dt); Think(KeeperMode.HoldArea); return;
            }
            if (race.Win)
            {
                _rushing = true;
                _anticipating = false;
                if (!_struck) { _roundRushed = true; Report.Rushed = true; }
                RunAt(race.Point, Keeper.RushSpeed(_k.Sheet, _t), dt);
                _hands = HomeFor(_k.Position);
                SetAction(KeeperAction.Rush);
                Think(KeeperMode.Rush);
                return;
            }
            if (race.Anticipate)
            {
                // Not sure of the race yet, but it is close: he starts toward the ball, ready to abort.
                _anticipating = true;
                RunAt(race.Point, Keeper.RushSpeed(_k.Sheet, _t) * k.AnticipateSpeedFraction, dt);
                _hands = HomeFor(_k.Position);
                SetAction(KeeperAction.Rush);
                Think(KeeperMode.Anticipate);
                return;
            }
            if (_rushing || _anticipating) { _rushing = false; _anticipating = false; _hasSpot = false; }
        }
        else { _rushing = false; _anticipating = false; if (!holdLine) _tRaceBlock = s.ShotInFlight ? "shot in flight" : "ball dead"; }

        var goal = new Vector2(_pitch.GoalLineX, 0f);
        // (a pass on its way: he sets himself for where it will be met)
        var ball2 = s.Coming is { } coming ? new Vector2(coming.X, coming.Z) : new Vector2(ball.Position.X, ball.Position.Z);
        var feet = new Vector2(_k.Position.X, _k.Position.Z);
        float toBall = Vector2.Distance(ball2, feet);
        bool inFront = Keeper.MouthDistance(ball2, _pitch) > Keeper.MouthDistance(feet, _pitch) + 0.3f;
        bool close = !holdLine && inFront && toBall < k.PreSpreadRange;
        bool inRange = Vector2.Distance(ball2, goal) <= k.SetRange;
        // The home keeper taken round runs back to his line, through a wind-up too.
        if (HomeBrain && !holdLine && !s.BallDead && !s.ShotInFlight && StepRecover(dt, ball2)) { Think(KeeperMode.Recover); return; }
        // He plants for the strike: in its last plant_lead, or earlier if he needs longer than that to brake to a stop.
        float brake = new Vector2(_k.Velocity.X, _k.Velocity.Z).Length() / k.PlantDecel + 0.05f;
        bool blocking = _charging && s.Windup && BlockReach(toBall, ball2, feet, s) <= k.ChargeBlockDistance;
        float plantLead = MathF.Max(k.PlantLead, blocking ? MathF.Min(brake, k.ChargePlantLead) : brake);
        if (s.Windup && s.StrikeIn <= plantLead && inRange && !s.BallDead)
        {
            Brake(k.PlantDecel, dt);
            _hands = HomeFor(_k.Position);
            _shuffling = false;
            SetAction(close ? KeeperAction.Spread : KeeperAction.Planted);
            Think(KeeperMode.Plant);
            return;
        }

        // The home keeper sets on a carrier closing on him, then mirrors him.
        if (HomeBrain && !holdLine && !s.BallDead && !s.ShotInFlight && SetOn(s, ball2, feet, toBall, inFront))
        {
            Think(StepSet(dt, ball2, feet, toBall, new Vector2(s.AttackerVelocity.X, s.AttackerVelocity.Z)));
            return;
        }

        // Charging: an attacker who has come in with the ball, in front of him and inside the charge range. Once he can close
        // him down quickly enough (the chip risk weighed), he runs at him and jockeys a couple of metres from the ball.
        bool chargeable = !holdLine && _engaged && s.AttackerHasBall && inFront && !s.BallDead && !s.ShotInFlight && !s.DefenderCovers;
        _chargeSoon = false;
        _charging = chargeable && ChargeDecision(ball, s, toBall, feet, rng);
        if (_charging)
        {
            StepCharge(dt, ball, toBall);
            Think(_k.KeeperAction == KeeperAction.Charge ? KeeperMode.Charge : KeeperMode.Jockey);
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
            Think(KeeperMode.Spread);
            return;
        }

        // Hold the arc (or the line), or close down an attacker who has come in with the ball.
        var look = ball.Position + new Vector3(ball.Velocity.X, 0f, ball.Velocity.Z) * k.ArcLookahead;
        Vector3 ideal = holdLine ? Keeper.LineSpot(look, _pitch, _t) : Keeper.ArcTarget(look, _pitch, _t, _k.Sheet);
        if (!holdLine && !s.BallDead && !s.ShotInFlight && _engaged) ideal = CloseDownSpot(look, ideal, rng);
        // Not closing down yet, but the attacker has the ball in front of him: he keeps the depth he has within his limits
        // rather than dropping back to his arc only to come out again a moment later.
        else if (!holdLine && !s.BallDead && !s.ShotInFlight && s.AttackerHasBall && inFront) ideal = CloseDownSpot(look, ideal, rng, advance: false);
        if (!holdLine && s.AttackerHasBall && !s.BallDead) ideal = Committed(ideal);
        if (HomeBrain && !holdLine) ideal = CapHome(ideal, look);
        if (!_hasSpot || Vector3.Distance(ideal, _spotIdeal) > k.RepositionThreshold) TakeSpot(ideal, look, holdLine, rng);
        float dist = new Vector2(_spot.X - _k.Position.X, _spot.Z - _k.Position.Z).Length();
        // Getting back goal-side (recovering after being drawn out or rounded) close to goal is a run, not a jog.
        bool urgent = !holdLine && Keeper.MouthDistance(new Vector2(look.X, look.Z), _pitch) <= k.UrgentRange
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
        Think(holdLine ? KeeperMode.Hold : _engaged ? KeeperMode.CloseDown : KeeperMode.Arc);
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
    /// Closing down (not charging): the spot on the cover line from the ball at the depth he wants for the ball's distance (see
    /// <see cref="Keeper.CloseDownDistance"/>), never shallower than his arc. He does not come further out than his judged
    /// chip-safe depth, than <c>close_down_gap</c> short of the ball, or than <c>hold_ground_max_out</c>; between what he wants
    /// and those limits he holds the depth he has (no backpedalling while the attacker runs at him). With
    /// <paramref name="advance"/> false he only holds: he wants no more than his arc.
    /// </summary>
    Vector3 CloseDownSpot(Vector3 look, Vector3 arc, SimRng rng, bool advance = true)
    {
        var k = _t.Keeper;
        var b = new Vector2(look.X, look.Z);
        float bMouth = Keeper.MouthDistance(b, _pitch);
        float arcD = Keeper.MouthDistance(new Vector2(arc.X, arc.Z), _pitch);
        bool home = HomeBrain;
        float want = !advance ? arcD : home ? Keeper.HomeDistance(_k.Sheet, _t, bMouth, arcD) : Keeper.CloseDownDistance(_k.Sheet, _t, bMouth, arcD);
        // (the home keeper's limit is his home depth; the legacy keeper's hold_ground_max_out)
        float outCap = home ? HomeCap(bMouth) : k.HoldGroundMaxOut;
        float chipCap = MathF.Min(ChipSafeJudged(Vector2.Distance(b, new Vector2(_pitch.GoalLineX, 0f)), rng), outCap);
        float mine = Keeper.MouthDistance(new Vector2(_k.Position.X, _k.Position.Z), _pitch);
        float cap = MathF.Min(chipCap, bMouth - k.CloseDownGap);
        float lo = MathF.Max(arcD, MathF.Min(want, cap));
        float hi = MathF.Max(lo, cap);
        if (_chargeSoon) hi = MathF.Max(hi, MathF.Min(mine, ChargeMaxOut));
        _tWant = want; _tChip = chipCap; _tGap = bMouth - k.CloseDownGap;
        float got = Math.Clamp(mine, lo, hi);
        _tLimit = got <= arcD + 1e-3f && arcD >= MathF.Min(want, cap) ? KeeperLimit.Arc
            : got > lo + 1e-3f ? KeeperLimit.Kept
            : want <= cap ? KeeperLimit.Want
            : bMouth - k.CloseDownGap < chipCap ? KeeperLimit.Gap
            : chipCap >= outCap - 1e-3f ? (home ? KeeperLimit.HomeCap : KeeperLimit.HoldCap) : KeeperLimit.ChipCap;
        return Keeper.CoverSpot(look, _pitch, _t, Math.Clamp(mine, lo, hi), halfWay: false);
    }

    /// <summary>
    /// Whether he charges the attacker on the ball this tick. Never if where he would meet him is further beyond his judged
    /// chip-safe depth (for the ball's distance now) than <c>charge_chip_allowance</c> (by rushing_out; once going,
    /// <c>charge_chip_hysteresis</c> more). Else, inside <c>engage_range</c> of the ball he always does. Otherwise, with the
    /// ball inside <c>charge_range</c> of the goal, he weighs the time he needs to get to <c>jockey_distance</c> of it
    /// (running at rush speed while the attacker comes on toward him, <see cref="Keeper.CloseTime"/>) against the time he accepts
    /// (<see cref="Keeper.ChargeTolerance"/>: his rushing rating's, cut by how far beyond his judged chip-safe depth for the
    /// ball's distance now the meeting point would take him). He starts within it and, once going, carries on until it exceeds
    /// <c>charge_drop_factor</c> times it (plus 0.25 s): an attacker who stops or turns away lets him drop back. With the ball
    /// up to <c>charge_hold_margin</c> beyond the range and a charge he would make, he holds the depth he has instead of giving
    /// ground only to come out again a moment later.
    /// </summary>
    bool ChargeDecision(Ball ball, in KeeperSituation s, float toBall, Vector2 feet, SimRng rng)
    {
        var k = _t.Keeper;
        // The home keeper weighs no charge: only an attacker on the ball within engage_range of him.
        if (HomeBrain) return toBall <= k.EngageRange;
        var b = new Vector2(ball.Position.X, ball.Position.Z);
        float bd = Vector2.Distance(b, new Vector2(_pitch.GoalLineX, 0f));
        float range = k.ChargeRange + (_charging ? 2f : 0f);
        if (bd > range + k.ChargeHoldMargin) return false;
        var dir = toBall > 1e-3f ? (b - feet) / toBall : new Vector2(-1f, 0f);
        float v0 = Vector2.Dot(new Vector2(_k.Velocity.X, _k.Velocity.Z), dir);
        float approach = -Vector2.Dot(new Vector2(s.AttackerVelocity.X, s.AttackerVelocity.Z), dir);
        float tClose = Keeper.CloseTime(toBall - k.JockeyDistance, v0, approach, Keeper.MoveAccel(_k.Sheet, _t), Keeper.RushSpeed(_k.Sheet, _t));
        // Where they would meet: the ball that much closer, him jockeying short of it; beyond his chip-safe depth for the ball's
        // distance now, a chip on the way is the risk he weighs.
        float bMouth = Keeper.MouthDistance(b, _pitch);
        float meet = MathF.Min(ChargeMaxOut, bMouth - MathF.Max(0f, approach) * (float.IsFinite(tClose) ? tClose : 0f) - k.JockeyDistance);
        float chipSafe = ChipSafeJudged(bd, rng);
        float excess = meet - chipSafe;
        float tol = Keeper.ChargeTolerance(_k.Sheet, _t, excess);
        _tCloseT = tClose; _tTol = tol; _tChip = chipSafe;
        // Further beyond his chip-safe depth than his rushing rating accepts, he does not go (or carries on): the attacker would
        // only have to lift it over him.
        if (excess > k.ChargeChipAllowance.ForRating(_k.Sheet.RushingOut) + (_charging ? k.ChargeChipHysteresis : 0f)) return false;
        // Just outside the range, a charge he would make soon keeps him where he is rather than giving ground first.
        if (bd > range) { _chargeSoon = tClose <= tol; return false; }
        if (toBall <= k.LegacyEngageRange) return true;
        return _charging ? tClose <= tol * k.ChargeDropFactor + 0.25f : tClose <= tol;
    }

    /// <summary>
    /// Charging and jockeying: on the cover line from the ball (looked ahead) <c>jockey_distance</c> short of it, never shallower
    /// than his arc or further out than the penalty area's depth, run at rush speed (slowing to arrive). Giving ground toward his goal
    /// he moves no faster than <c>jockey_retreat_speed</c> away from the ball, so an attacker who runs at him closes the gap.
    /// Shown as Charge while further than a metre beyond jockeying range, else Jockey.
    /// </summary>
    void StepCharge(float dt, Ball ball, float toBall)
    {
        var k = _t.Keeper;
        var look = ball.Position + new Vector3(ball.Velocity.X, 0f, ball.Velocity.Z) * k.ArcLookahead;
        var lb = new Vector2(look.X, look.Z);
        float bMouth = Keeper.MouthDistance(lb, _pitch);
        var arc = Keeper.ArcTarget(look, _pitch, _t, _k.Sheet);
        float arcD = Keeper.MouthDistance(new Vector2(arc.X, arc.Z), _pitch);
        // (the home keeper comes no closer than set_gap_m to the ball and stays inside his home depth)
        bool home = HomeBrain;
        float gap = home ? k.SetGapM : k.JockeyDistance;
        float to = MathF.Max(arcD, MathF.Min(MathF.Min(bMouth - gap, ChargeMaxOut), home ? HomeCap(bMouth) : float.PositiveInfinity));
        var target = Keeper.CoverSpot(look, _pitch, _t, to, halfWay: false);
        var pos = new Vector2(_k.Position.X, _k.Position.Z);
        var d = Keeper.CapTarget(pos, new Vector2(target.X, target.Z), k.MoveTargetCap) - pos;
        float dist = d.Length();
        float accel = Keeper.MoveAccel(_k.Sheet, _t);
        Vector2 desired = dist > 0.01f ? d / dist * MathF.Min(Keeper.RushSpeed(_k.Sheet, _t), MathF.Sqrt(2f * accel * MathF.Max(0f, dist - 0.01f))) : Vector2.Zero;
        var fromBall = pos - lb;
        if (fromBall.LengthSquared() > 1e-4f)
        {
            var away = Vector2.Normalize(fromBall);
            float back = Vector2.Dot(desired, away);
            if (back > k.JockeyRetreatSpeed) desired -= away * (back - k.JockeyRetreatSpeed);
        }
        Steer(desired, accel, dt);
        _hands = HomeFor(_k.Position);
        _hasSpot = false;
        _shuffling = false;
        if (!_struck) { _roundCharged = true; Report.Charged = true; }
        SetAction(toBall > gap + 1f ? KeeperAction.Charge : KeeperAction.Jockey);
    }

    /// <summary>
    /// Charging into a wind-up: how close to the ball he would be at the strike if he kept coming at the speed he has until the
    /// last <c>charge_plant_lead</c> and then braked, with the attacker coming on at his speed now, metres.
    /// </summary>
    float BlockReach(float toBall, Vector2 ball, Vector2 feet, in KeeperSituation s)
    {
        var k = _t.Keeper;
        var dir = toBall > 1e-3f ? (ball - feet) / toBall : new Vector2(-1f, 0f);
        float v = MathF.Max(0f, Vector2.Dot(new Vector2(_k.Velocity.X, _k.Velocity.Z), dir));
        float approach = MathF.Max(0f, -Vector2.Dot(new Vector2(s.AttackerVelocity.X, s.AttackerVelocity.Z), dir));
        float tIn = float.IsFinite(s.StrikeIn) ? s.StrikeIn : 0f;
        float run = MathF.Max(0f, tIn - k.ChargePlantLead);
        float stopT = MathF.Min(MathF.Min(k.ChargePlantLead, tIn), v / k.PlantDecel);
        float covered = v * run + v * stopT - 0.5f * k.PlantDecel * stopT * stopT;
        return toBall - covered - approach * tIn;
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
        if (!holdLine && HomeBrain) p.X = CapHome(new Vector3(p.X, 0f, p.Y), ball).X;
        float lim = _pitch.GoalHalfWidth + 1f;
        p.Y = Math.Clamp(p.Y, -lim, lim);
        _spot = new Vector3(p.X, 0f, p.Y);
    }

    /// <summary>What he does about the rush race this tick, by where the ball he would win is (see <see cref="AreaPlayFor"/>).</summary>
    enum AreaPlay { None, Smother, Rush, Anticipate, Slide, Poke, HoldAtLine }

    /// <summary>
    /// His penalty area decides how he plays the ball he races for. Inside it, as always: within a dive he smothers it, further
    /// he rushes, a close race he anticipates. Outside it he may not use his hands: within reach he slides at it feet first (or
    /// pokes at it standing, for a bouncing ball or one at his feet), further he rushes on toward it to slide when he gets there,
    /// and a race he is not yet sure of brings him to the edge of his area and no further (holding the line). A race he cannot win
    /// leaves him to his spot (None): he retreats to cover the goal as when there is no race.
    /// </summary>
    static AreaPlay AreaPlayFor(in RaceResult race)
    {
        if (race.Win && race.Smother) return !race.Outside ? AreaPlay.Smother : race.Poke ? AreaPlay.Poke : AreaPlay.Slide;
        if (race.Win) return AreaPlay.Rush;
        if (race.Anticipate) return race.Outside ? AreaPlay.HoldAtLine : AreaPlay.Anticipate;
        return AreaPlay.None;
    }

    /// <summary>
    /// A close race for a ball outside his area: he comes toward it at his anticipation speed, but stops on the area's line
    /// (his spot is taken again once the race is gone).
    /// </summary>
    void StepHoldAtLine(Vector3 point, float dt)
    {
        var k = _t.Keeper;
        _anticipating = true;
        _rushing = false;
        var to = new Vector3(MathF.Max(point.X, _pitch.PenaltyAreaLineX + Pitch.PlayerRadius), 0f, point.Z);
        MoveTo(to, Keeper.RushSpeed(_k.Sheet, _t) * k.AnticipateSpeedFraction, Keeper.MoveAccel(_k.Sheet, _t), dt);
        _hands = HomeFor(_k.Position);
        SetAction(new Vector2(_k.Velocity.X, _k.Velocity.Z).Length() > k.StillSpeed ? KeeperAction.Rush : KeeperAction.Set);
    }

    readonly struct RaceResult
    {
        public bool Win { get; init; }
        /// <summary>Won within his go-down reach: a smother inside his area, a slide or a poke outside it.</summary>
        public bool Smother { get; init; }
        /// <summary>The ball he races for is outside his penalty area at <see cref="Point"/>: he plays it with his feet.</summary>
        public bool Outside { get; init; }
        /// <summary>Outside the area, a standing poke rather than a slide (a bouncing ball, or one within <c>poke_reach</c>).</summary>
        public bool Poke { get; init; }
        /// <summary>Not won, but within his anticipation of winning: he starts toward <see cref="Point"/>.</summary>
        public bool Anticipate { get; init; }
        public Vector3 Point { get; init; }
    }

    /// <summary>
    /// The rush race: the earliest point on the loose ball's predicted path he can reach (to within a smother dive) before the
    /// ball gets there and before the attacker's next touch, with the margin his rushing rating wants (none once he is rushing,
    /// and none for a ball within a dive unless the attacker is within <c>contested_distance</c> of it: he lunges at a ball
    /// going past him), judged with his misjudgement (drawn per attacker
    /// touch). The more time the ball gives him over the attacker, the further out he wins it. Short of the margin by less than
    /// his anticipation (<c>rush_anticipation</c>), he starts toward the ball and aborts if the race turns against him. Only for
    /// a low ball he can take inside his penalty area (within its depth of the goal centre), or up to <c>slide_beyond_area</c> beyond its line
    /// outside it. A point outside the area (judged by the ball there) he races for with his feet: within <c>slide_reach</c> a
    /// slide (the foot from <c>slide_foot_start</c> at <c>slide_speed</c>), or a poke within <c>poke_reach</c> (at
    /// <c>poke_speed</c>) when the ball is above <c>slide_max_height</c> or that close.
    /// </summary>
    RaceResult Race(Ball ball, in KeeperSituation s, SimRng rng)
    {
        var k = _t.Keeper;
        var goal = new Vector2(_pitch.GoalLineX, 0f);
        var b0 = new Vector2(ball.Position.X, ball.Position.Z);
        if (ball.Position.Y > k.ClaimMaxHeight + 0.5f) { _tRaceBlock = "ball high"; return default; }
        if (Vector2.Distance(b0, goal) > RushMaxFromGoal + 4f && b0.X < _pitch.PenaltyAreaLineX - k.SlideBeyondArea - 4f) { _tRaceBlock = "beyond rush max"; return default; }
        float tAtt = s.AttackerNextTouch;
        var feet = new Vector2(_k.Position.X, _k.Position.Z);
        // The attacker still at the ball (close control, a jog): even a dive at it wants his margin.
        bool contested = Vector2.Distance(new Vector2(s.AttackerPosition.X, s.AttackerPosition.Z), b0) <= k.ContestedDistance;
        float accel = Keeper.MoveAccel(_k.Sheet, _t), vmax = Keeper.RushSpeed(_k.Sheet, _t);
        bool going = _rushing || _anticipating;
        // Quick rejection: even running flat out from now he cannot reach the ball's current spot before the attacker.
        float dNow = Vector2.Distance(feet, b0);
        if (!going && Keeper.TravelTime(dNow - k.SmotherReach - 8f, 0f, accel, vmax) > tAtt) { _tRaceBlock = "quick reject"; return default; }
        _tRaceBlock = "race";
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
            if (p.X > _pitch.GoalLineX - 0.3f) continue;
            bool outside = !HandsAllowedAt(p);
            if (outside ? p.X < _pitch.PenaltyAreaLineX - k.SlideBeyondArea : Vector2.Distance(p2, goal) > RushMaxFromGoal) continue;
            var to = p2 - feet;
            float d = to.Length();
            float v0 = d > 1e-4f ? Vector2.Dot(vel, to / d) : 0f;
            // How he goes down at it: a smother inside his area; outside it a slide, or a poke at a bouncing ball or one at his feet.
            bool poke = outside && (p.Y > k.SlideMaxHeight || d <= k.PokeReach);
            float reach = !outside ? k.SmotherReach : poke ? k.PokeReach : k.SlideReach;
            float goDown = !outside ? MathF.Min(d, reach) / k.SmotherSpeed
                : poke ? MathF.Min(d, reach) / k.PokeSpeed
                : MathF.Max(0f, MathF.Min(d, reach) - k.SlideFootStart) / k.SlideSpeed;
            bool dive = d <= reach;
            float tk = (dive ? 0f : start + Keeper.TravelTime(d - reach, v0, accel, vmax)) + goDown;
            float perceived = tk - _misjudge;
            float margin = _rushing || dive && !contested ? 0f : Keeper.RushMargin(_k.Sheet, _t, d);
            var point = new Vector3(p.X, poke ? p.Y : _t.Ball.Radius, p.Z);
            bool rest = b.Velocity == Vector3.Zero && b.OnGround;
            // At rest the ball waits for him: he wins it if he gets there before the attacker, whenever that is.
            float arrive = rest ? perceived : tb;
            float short_ = MathF.Max(rest ? 0f : perceived - tb, arrive + margin - tAtt);
            if (short_ < _tRaceShort) _tRaceShort = short_;
            if ((rest || perceived <= tb) && arrive + margin <= tAtt)
                return new RaceResult { Win = true, Smother = dive, Outside = outside, Poke = poke, Point = point };
            if (!anticipate.Anticipate && antic > 0f && !dive && (rest || perceived <= tb) && arrive + margin - keepOn <= tAtt)
                anticipate = new RaceResult { Anticipate = true, Outside = outside, Point = point };
            if (rest) break;
        }
        return anticipate;
    }
}
