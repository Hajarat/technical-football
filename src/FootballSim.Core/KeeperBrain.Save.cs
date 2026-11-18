using System.Numerics;

namespace FootballSim.Core;

// The save: the ready stance at the strike (hands out, the hull), the reflex save made before his full reaction when that would
// come too late, the dive at the first point on the ball's path he gets to in time, and what happens when the ball meets him.
// The numbers are in Keeper.Save.cs.
public sealed partial class KeeperBrain
{
    float _since;               // seconds since the strike
    float _reaction;            // his full reaction, seconds after the strike
    float _reflexTime;          // his reflex, seconds after the strike (earlier than the reaction)
    bool _reflexDecided;        // the reflex decision has been made for this shot
    bool _reflex;               // the current save is a reflex (limited range, from the reflex time)
    float _jitter;
    float _exec;                // the current save's execution time (by its difficulty), seconds
    float _difficulty;          // the current save's difficulty
    Vector3 _home;              // hands' rest point when the save started
    Vector3 _feet0;             // feet when the save started
    Vector2 _carry0;            // ground velocity when the save started
    Vector3 _target;
    float _targetT;             // seconds after the save's start the ball was predicted at the target
    Vector2 _planeN = new(1f, 0f);
    Vector2 _planePoint;        // a point on the save plane (ground X/Z)
    float _ready;               // 0..1: how ready he was at the strike (set close to the shooter: his hull is big)
    float _span;                // how far out from his axis his hands start: 0 when he was not set
    float _limbStart;           // how far out from his axis the current reflex limb starts
    Vector2 _faceAxis = new(-1f, 0f); // ground direction to the ball at the strike
    KeeperDive _dive;
    float _reachedFor;          // seconds the save's reach has been complete
    bool _contact;              // the ball has been met in this save
    bool _active;               // a shot or loose ball is being played and not yet resolved
    bool _pendingStrike;        // a strike came while he was down; he reacts once he may
    float _pendingSince;

    /// <summary>The sphere the ball's path is tested against: the hands (or the body spread in a smother) plus the ball radius. Null when not live.</summary>
    internal SweepSphere? HandsSphere =>
        _active && _phase is Phase.Reacting or Phase.Diving or Phase.Smother
            ? new SweepSphere(_hands, (_phase == Phase.Smother ? _t.Keeper.SmotherRadius : _t.Keeper.HandRadius) + _t.Ball.Radius)
            : null;

    /// <summary>
    /// The keeper's body as a capsule the ball's path is tested against (grown by the ball radius) while he plays a shot: his
    /// standing hull until a dive gets going (and through a standing or reflex save), then stretched from his hips toward his
    /// hands. Null when not live.
    /// </summary>
    internal SweepCapsule? BodyCapsule
    {
        get
        {
            if (!_active || _phase is not (Phase.Reacting or Phase.Diving)) return null;
            var k = _t.Keeper;
            float r = k.BodyRadius + _t.Ball.Radius;
            var feet = _k.Position;
            float top = k.BodyTopFraction * _k.Sheet.HeightM;
            bool upright = _phase == Phase.Reacting || _reflex || _dive is KeeperDive.Catch or KeeperDive.Feet || DiveTau <= 0f;
            if (upright) return UprightBody(feet);
            Vector3 to = _hands - _home;
            float dist = to.Length();
            float progress = dist > 1e-3f ? Math.Clamp(Reach(DiveTau) / dist, 0f, 1f) : 1f;
            float hipY = k.HipHeight + (Math.Clamp(_hands.Y, k.BodyLow, top) - k.HipHeight) * progress;
            var hip = new Vector3(feet.X, hipY, feet.Z);
            Vector3 toHands = _hands - hip;
            float len = toHands.Length();
            Vector3 end = len > k.HandRadius ? _hands - toHands / len * k.HandRadius : hip;
            return new SweepCapsule(hip, end, r);
        }
    }

    /// <summary>
    /// The standing body (grown by the ball radius): his hull, upright from the shins to the shoulders, with a radius from
    /// <c>body_radius</c> (not ready) to <c>hull_radius</c> (set within <c>ready_full</c> of the shooter: legs and arms spread).
    /// </summary>
    SweepCapsule UprightBody(Vector3 feet)
    {
        var k = _t.Keeper;
        return new SweepCapsule(new Vector3(feet.X, k.BodyLow, feet.Z), new Vector3(feet.X, k.BodyTopFraction * _k.Sheet.HeightM, feet.Z),
            Keeper.HullRadius(_t, _ready) + _t.Ball.Radius);
    }

    static float SegmentDistance(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float l2 = Vector3.Dot(ab, ab);
        float u = l2 > 1e-9f ? Math.Clamp(Vector3.Dot(p - a, ab) / l2, 0f, 1f) : 0f;
        return Vector3.Distance(p, a + ab * u);
    }

    /// <summary>
    /// Test seam: puts the keeper straight into a dive at <paramref name="target"/>, <paramref name="sinceReaction"/> seconds after
    /// his reaction, from where he stands (no jitter).
    /// </summary>
    internal void BeginDiveForTest(Vector3 target, float sinceReaction)
    {
        Report = new KeeperReport { ReactionS = 0f };
        Enter(Phase.Diving);
        _active = true;
        _contact = false;
        _reflex = false;
        _reaction = 0f;
        _jitter = 0f;
        _since = sinceReaction;
        _feet0 = _k.Position;
        _home = HomeFor(_k.Position);
        _carry0 = Vector2.Zero;
        _reachedFor = 0f;
        _ready = 0f;
        _span = 0f;
        _target = target;
        _targetT = sinceReaction;
        _difficulty = Keeper.Difficulty(_k.Sheet, _t, _feet0, Vector3.Zero, target);
        _exec = Keeper.SaveExecution(_k.Sheet, _t, _difficulty);
        _planePoint = new Vector2(_home.X, _home.Z);
        _dive = Keeper.DiveKindFor(_feet0, target, _t);
        Report.Dive = _dive;
        Report.Difficulty = _difficulty;
        SetAction(KeeperAction.Diving, _dive);
        StepDive(0f);
    }

    // ---- strike ----

    /// <summary>The ball has just been struck: start reacting (or, if he is down, as soon as he may).</summary>
    internal void OnStrike(SimRng rng, Ball? ball = null)
    {
        _struck = true;
        float speed = new Vector2(_k.Velocity.X, _k.Velocity.Z).Length();
        // In a stance for the shot: planted (or still braking into it), spread or jockeying. Still in it, he is set.
        bool stance = _phase == Phase.Stand && _k.KeeperAction is KeeperAction.Planted or KeeperAction.Spread or KeeperAction.Jockey;
        bool set = stance && _k.KeeperAction != KeeperAction.Jockey && speed <= _t.Keeper.StillSpeed;
        Report = new KeeperReport
        {
            SpeedAtStrikeMs = speed,
            DepthAtStrikeM = _pitch.GoalLineX - _k.Position.X,
            BallDistanceAtStrikeM = ball == null ? float.NaN : Vector2.Distance(new Vector2(ball.Position.X, ball.Position.Z), new Vector2(_pitch.GoalLineX, 0f)),
            PlantedAtStrike = set,
            ActionAtStrike = _k.KeeperAction,
            Rushed = _roundRushed,
            Charged = _roundCharged,
            Smothered = _roundSmother,
            WonBall = _roundWon,
            Rounded = _roundRounded,
            DepthAtBandM = (float[])_bandDepth.Clone(),
            DepthReversals = _reversals,
            DepthReversalsFirst2S = _reversals2,
        };
        _ready = 0f;
        if (ball != null)
        {
            Report.OnTarget = ball.PredictPlaneCrossing(_pitch.GoalLineX, _t.Dt, 3f, out var cross, out _)
                && MathF.Abs(cross.Z) + _t.Ball.Radius <= _pitch.GoalHalfWidth && cross.Y + _t.Ball.Radius <= _pitch.BarHeight;
            var h = new Vector2(ball.Velocity.X, ball.Velocity.Z);
            _planeN = h.LengthSquared() > 1e-6f ? Vector2.Normalize(h) : new Vector2(1f, 0f);
            var toBall = new Vector2(ball.Position.X - _k.Position.X, ball.Position.Z - _k.Position.Z);
            float d = toBall.Length();
            if (d > 1e-3f) _faceAxis = toBall / d;
            // In his stance close to the shooter he is ready, big: fully within ready_full, not at all from ready_range. A
            // keeper still braking into a block spreads too; only his reaction pays for the speed.
            if (stance) _ready = Keeper.Readiness(_t, d);
        }
        else _planeN = new Vector2(1f, 0f);
        Report.Ready = _ready;
        switch (_phase)
        {
            case Phase.Held:
                return;
            case Phase.Smother when _active:
                return; // already going down at the ball: the body is in the way or not
            case Phase.Diving when _active:
            case Phase.Backpedal when _active:
                return;
            case Phase.Diving:
            case Phase.Smother:
            case Phase.Grounded:
                _ready = 0f;
                _pendingStrike = true; _pendingSince = 0f;
                return;
            case Phase.GettingUp when _phaseTime < _t.Keeper.GetUpSaveAfter:
                _ready = 0f;
                _pendingStrike = true; _pendingSince = 0f;
                return;
        }
        BeginReacting(rng, 0f);
    }

    void BeginReacting(SimRng rng, float elapsed)
    {
        float speed = new Vector2(_k.Velocity.X, _k.Velocity.Z).Length();
        Enter(Phase.Reacting);
        _active = true;
        _rushing = false;
        _anticipating = false;
        _fromBackpedal = false;
        _pendingStrike = false;
        _reflex = false;
        _reflexDecided = false;
        _since = elapsed;
        _reaction = Keeper.ReactionAtStrike(_k.Sheet, _t, rng, speed);
        _reflexTime = Keeper.DrawReflex(_k.Sheet, _t, rng, _reaction);
        if (elapsed > 0f) _ready = 0f;
        _span = Keeper.StanceStart(_t, _ready, still: speed <= _t.Keeper.StillSpeed && elapsed <= 0f);
        Report.ReactionS = _reaction;
        Report.ReflexS = _reflexTime;
        SetAction(_ready >= 0.5f ? KeeperAction.Spread : KeeperAction.Reacting);
    }

    /// <summary>One reacting tick: at his reflex he decides whether to make a reflex save; at his reaction he dives (or reads a chip).</summary>
    void StepReacting(float dt, Ball ball, SimRng rng)
    {
        _since += dt;
        CarryMomentum(dt);
        _hands = HomeFor(_k.Position);
        if (!_reflexDecided && _since >= _reflexTime)
        {
            _reflexDecided = true;
            if (_since < _reaction && TryReflex(ball, rng)) return;
        }
        if (_since >= _reaction)
        {
            if (ChipOverHim(ball)) BeginBackpedal();
            else StartDive(ball, rng);
        }
    }

    // ---- planning a save ----

    /// <summary>A save planned on the ball's predicted path (see <see cref="PlanSave"/>).</summary>
    readonly struct SavePlan
    {
        /// <summary>His reach gets to the ball in time at <see cref="Target"/> (or it will hit his body: <see cref="Block"/>).</summary>
        public bool Found { get; init; }
        public bool Block { get; init; }
        public Vector3 Target { get; init; }
        /// <summary>Seconds from now the ball is at the target.</summary>
        public float T { get; init; }
        /// <summary>The least he was short by, metres (0 when found).</summary>
        public float Short { get; init; }
        /// <summary>The ball's speed at the target, m/s.</summary>
        public float Speed { get; init; }
        /// <summary>The ball's ground direction now (the normal of his save plane).</summary>
        public Vector2 PlaneN { get; init; }
    }

    /// <summary>
    /// Scans the ball's predicted path for a save from where he stands. For a dive (from <paramref name="lead"/> seconds from now,
    /// his reaction): the first point inside his dive wedge, below his leap, that his reach gets to in time, the dive's execution
    /// time set by that point's difficulty; else, as a best effort, the point in the wedge he gets closest to in time (or, with
    /// none in the wedge, the ball's closest pass). For a reflex (from now): the first point below his reflex height that a reflex
    /// limb gets to in time (horizontally from his upright axis), else the one it gets closest to. A ball that will hit his
    /// standing body is a block, found either way.
    /// </summary>
    SavePlan PlanSave(Ball ball, float lead, bool reflex)
    {
        var sheet = _k.Sheet;
        var feet = _k.Position;
        var home = HomeFor(feet);
        var vel = _k.Velocity;
        var toBall = new Vector2(ball.Position.X - feet.X, ball.Position.Z - feet.Z);
        Vector2 facing = toBall.LengthSquared() > 1e-4f ? Vector2.Normalize(toBall) : new Vector2(-1f, 0f);
        var hv = new Vector2(ball.Velocity.X, ball.Velocity.Z);
        Vector2 planeN = hv.LengthSquared() > 1e-4f ? Vector2.Normalize(hv) : _planeN;
        var b = ball.ScratchA();
        float dt = _t.Dt;
        int steps = (int)(1.5f / dt);
        bool passed = false, crossed = false;
        var body = UprightBody(feet);
        Vector3 best = home, closest = ball.Position;
        float tBest = 0f, tClosest = 0f, bestShort = float.MaxValue, bestNeed = float.MaxValue, vBest = 0f, vClosest = 0f;
        var home2 = new Vector2(home.X, home.Z);
        float prevSide = Vector2.Dot(new Vector2(ball.Position.X, ball.Position.Z) - home2, planeN);
        float top = reflex ? Keeper.ReflexHeight(sheet, _t) : Keeper.ChipReachHeight(sheet, _t);
        for (int i = 1; i <= steps; i++)
        {
            b.Step(dt, null, null);
            var p = b.Position;
            float t = i * dt;
            float side = Vector2.Dot(new Vector2(p.X, p.Z) - home2, planeN);
            if (!passed && prevSide < 0f && side >= 0f) passed = true;
            float lateral = new Vector2(p.X - feet.X, p.Z - feet.Z).Length();
            float need = reflex ? lateral : Vector3.Distance(p, home);
            float speed = b.Velocity.Length();
            if (need < bestNeed && p.Y <= top) { bestNeed = need; closest = p; tClosest = t; vClosest = speed; }
            // The ball will hit him where he stands: he stays big and blocks it rather than moving out of its way.
            if (p.X <= _pitch.GoalLineX && SegmentDistance(p, body.A, body.B) <= body.Radius)
                return new SavePlan { Found = true, Block = true, Target = p, T = t, Speed = speed, PlaneN = planeN };
            // Above his leap he cannot get a hand to it; after backpedalling he leaps backward, outside his usual wedge.
            bool candidate = p.X <= _pitch.GoalLineX && p.Y <= top && (reflex || _fromBackpedal || Keeper.InWedge(feet, facing, p, _t));
            if (candidate)
            {
                float d = Keeper.Difficulty(sheet, _t, feet, vel, p);
                float reach = reflex
                    ? Keeper.ReflexReachAt(sheet, _t, t, LimbStart(Keeper.ReflexKindFor(feet, p, _ready, _t)), Keeper.ReflexExecution(sheet, _t, d))
                    : Keeper.ReachAt(sheet, _t, t - lead, _span, Keeper.SaveExecution(sheet, _t, d));
                float shortBy = need - reach;
                if (shortBy <= 0f) return new SavePlan { Found = true, Target = p, T = t, Speed = speed, PlaneN = planeN };
                if (shortBy < bestShort) { bestShort = shortBy; best = p; tBest = t; vBest = speed; crossed = true; }
            }
            if (p.X > _pitch.GoalLineX + 0.3f || passed && side > 1.5f) break;
            prevSide = side;
        }
        return crossed
            ? new SavePlan { Target = best, T = tBest, Short = bestShort, Speed = vBest, PlaneN = planeN }
            : new SavePlan { Target = closest, T = tClosest, Short = float.MaxValue, Speed = vClosest, PlaneN = planeN };
    }

    /// <summary>
    /// At his reflex: if his full reaction and dive will get to the ball in time, he waits for them. Otherwise, if a reflex limb
    /// gets to it, he makes a reflex save now; or, if neither does, a reflex attempt when the ball passes inside his save hull
    /// (<c>save_hull_radius</c>), or when the reflex gets closer than the dive would and the ball passes within
    /// <c>reflex_attempt_margin</c> of his reflex range. Returns true if he started one.
    /// </summary>
    bool TryReflex(Ball ball, SimRng rng)
    {
        if (ball.Velocity.LengthSquared() < 1f) return false;
        var dive = PlanSave(ball, _reaction - _since, reflex: false);
        if (dive.Found) return false;
        var rf = PlanSave(ball, 0f, reflex: true);
        if (!rf.Found)
        {
            float lateral = new Vector2(rf.Target.X - _k.Position.X, rf.Target.Z - _k.Position.Z).Length();
            bool inHull = lateral <= _t.Keeper.SaveHullRadius;
            if (!inHull && (!(rf.Short < dive.Short) || lateral > Keeper.ReflexRange(_k.Sheet, _t) + _t.Keeper.ReflexAttemptMargin)) return false;
        }
        StartSave(rf, rng, reflex: true);
        return true;
    }

    void StartDive(Ball ball, SimRng rng) => StartSave(PlanSave(ball, 0f, reflex: false), rng, reflex: false);

    /// <summary>
    /// Starts the planned save from where he stands: aimed at the plan's target with his imperfect read of it, its difficulty
    /// setting its execution time and the largest random delay to its start, the motion by the target (a dive kind, or a reflex
    /// kind) and the save type he goes for.
    /// </summary>
    void StartSave(in SavePlan plan, SimRng rng, bool reflex)
    {
        var k = _t.Keeper;
        var sheet = _k.Sheet;
        Enter(Phase.Diving);
        _reflex = reflex;
        _feet0 = _k.Position;
        _home = HomeFor(_k.Position);
        _carry0 = new Vector2(_k.Velocity.X, _k.Velocity.Z);
        _reachedFor = 0f;
        _contact = false;
        _targetT = plan.T;
        _planeN = plan.PlaneN;
        _difficulty = Keeper.Difficulty(sheet, _t, _feet0, _k.Velocity, plan.Target);
        _exec = reflex ? Keeper.ReflexExecution(sheet, _t, _difficulty) : Keeper.SaveExecution(sheet, _t, _difficulty);

        // His read of it is not perfect.
        var lateral = new Vector2(-_planeN.Y, _planeN.X);
        float nz = rng.Normal(0f, k.PredictionNoise), ny = rng.Normal(0f, k.PredictionNoise);
        var target = plan.Target;
        target = new Vector3(target.X + lateral.X * nz, MathF.Max(_t.Ball.Radius * 0.5f, target.Y + ny), target.Z + lateral.Y * nz);
        _target = target;
        // He is beaten once the ball passes his save plane: through his hands' rest point, or through the target if that is
        // further along the ball's travel (a ball taken just behind him).
        float beyond = Vector2.Dot(new Vector2(target.X - _home.X, target.Z - _home.Z), _planeN);
        _planePoint = beyond > 0f ? new Vector2(_home.X, _home.Z) + _planeN * beyond : new Vector2(_home.X, _home.Z);
        _jitter = rng.NextFloat() * Keeper.SaveJitterMax(_t, _difficulty);
        if (reflex)
        {
            _dive = Keeper.ReflexKindFor(_feet0, target, _ready, _t);
            _limbStart = LimbStart(_dive);
        }
        else
        {
            _dive = _fromBackpedal ? KeeperDive.Back : Keeper.DiveKindFor(_feet0, target, _t);
            if (plan.Block && _dive is not (KeeperDive.Catch or KeeperDive.Feet))
                _dive = target.Y < k.FeetBlockHeight ? KeeperDive.Feet : KeeperDive.Catch;
        }
        Report.Dive = _dive;
        Report.Difficulty = _difficulty;
        Report.SaveType = reflex ? (plan.Found ? SaveType.Reflex : SaveType.ReflexAttempt)
            : !plan.Found ? SaveType.FailedAttempt
            : ExpectsCatch(plan) ? SaveType.Catch : SaveType.Parry;
        SetAction(KeeperAction.Diving, _dive);
        StepDive(0f);
    }

    /// <summary>Whether a dive that gets to the planned target expects to hold the ball there (its speed now, his catch range then).</summary>
    bool ExpectsCatch(in SavePlan plan)
    {
        if (plan.Block || _difficulty > _t.Keeper.CatchMaxDifficulty) return false;
        bool diving = _dive is not (KeeperDive.Catch or KeeperDive.Feet);
        return Vector3.Distance(plan.Target, _home) <= Keeper.CatchRange(_k.Sheet, _t, plan.Speed, diving, _since + plan.T);
    }

    // ---- the save in motion ----

    /// <summary>When the current save started, seconds after the strike: his reflex for a reflex save, else his reaction.</summary>
    float SaveStart => _reflex ? _reflexTime : _reaction;

    float DiveTau => _since - SaveStart - _jitter;

    /// <summary>His reach <paramref name="tau"/> seconds into the save: a dive's from his hand span, or a reflex limb's from where it starts.</summary>
    float Reach(float tau) => _reflex
        ? Keeper.ReflexReachAt(_k.Sheet, _t, tau, _limbStart, _exec)
        : Keeper.ReachAt(_k.Sheet, _t, tau, _span, _exec);

    /// <summary>
    /// How far out from his axis a reflex limb of the kind starts: a hand from where his stance holds it (his hand span, or his
    /// hull's edge); a foot from where it stands (<c>stance_foot_offset</c> when he was still, else beside his body), or his
    /// hull's edge when he was ready and that is further; the body from his hull's edge or his hands' span, whichever is further.
    /// </summary>
    float LimbStart(KeeperDive kind)
    {
        var k = _t.Keeper;
        float hull = Keeper.HullRadius(_t, _ready) - k.HandRadius;
        float foot = _span > 0f ? k.StanceFootOffset : k.BodyRadius;
        return kind switch
        {
            KeeperDive.ReflexHand => _span,
            KeeperDive.ReflexFoot => MathF.Max(foot, hull),
            _ => MathF.Max(MathF.Max(k.BodyRadius, hull), _span),
        };
    }

    /// <summary>His sideways axis at the strike (ground X/Z, unit): across the line to the ball.</summary>
    Vector2 SideAxis => new(-_faceAxis.Y, _faceAxis.X);

    /// <summary>Where a hand rests in the set stance: the stance start to one side (+1 or -1) of his body's centre, at waist height.</summary>
    Vector3 HandRest(float side)
    {
        var feet = _phase == Phase.Diving ? _feet0 : _k.Position;
        var lat = SideAxis * (side * _span);
        return new Vector3(feet.X + lat.X, _t.Keeper.HandSpanHeightFraction * _k.Sheet.HeightM, feet.Z + lat.Y);
    }

    /// <summary>
    /// The set keeper's two hands, held out to the sides, as a capsule the ball's path is tested against (grown by the ball
    /// radius) from the strike until his save gets going. Null when he was not set at the strike, or once he moves.
    /// </summary>
    internal SweepCapsule? HandsCapsule =>
        _active && _span > 0f && (_phase == Phase.Reacting || _phase == Phase.Diving && DiveTau <= 0f)
            ? new SweepCapsule(HandRest(-1f), HandRest(1f), _t.Keeper.HandRadius + _t.Ball.Radius)
            : null;

    void StepDive(float dt)
    {
        var k = _t.Keeper;
        float tau = DiveTau;
        float reach = Reach(tau);
        Vector3 to = _target - _home;
        float dist = to.Length();
        // A reflex limb goes out toward the target from where it starts (a hand from its set position, a foot or the body from
        // beside him at the target's height), its progress by its horizontal reach; a dive's hands from their rest point.
        var flat = new Vector2(_target.X - _feet0.X, _target.Z - _feet0.Z);
        float lateral = flat.Length();
        Vector3 limbFrom = _dive == KeeperDive.ReflexHand && _limbStart > 0f
            ? HandRest(Vector2.Dot(flat, SideAxis) >= 0f ? 1f : -1f)
            : lateral > 1e-4f
                ? new Vector3(_feet0.X + flat.X / lateral * _limbStart, _target.Y, _feet0.Z + flat.Y / lateral * _limbStart)
                : new Vector3(_feet0.X, _target.Y, _feet0.Z);
        if (!_contact)
        {
            if (_reflex)
            {
                float f = lateral <= _limbStart ? 1f : Math.Clamp((reach - _limbStart) / (lateral - _limbStart), 0f, 1f);
                _hands = Vector3.Lerp(limbFrom, _target, f);
            }
            else if (_span > 0f)
            {
                // From the near hand's set position out to the target as the reach grows past the span.
                Vector3 from = HandRest(Vector2.Dot(new Vector2(to.X, to.Z), SideAxis) >= 0f ? 1f : -1f);
                float f = dist <= _span ? 1f : Math.Clamp((reach - _span) / (dist - _span), 0f, 1f);
                _hands = Vector3.Lerp(from, _target, f);
            }
            else _hands = dist > 1e-4f ? _home + to / dist * MathF.Min(reach, dist) : _home;
        }

        // The body follows the hands sideways (a reflex's limb only a little: a short lunge), carrying any speed it had.
        float tc = MathF.Max(0f, _since - SaveStart);
        float v0 = _carry0.Length();
        float stopT = v0 / k.PlantDecel;
        float tt = MathF.Min(tc, stopT);
        Vector2 carry = v0 > 1e-4f ? _carry0 / v0 * (v0 * tt - 0.5f * k.PlantDecel * tt * tt) : Vector2.Zero;
        bool standing = _reflex || _dive is KeeperDive.Catch or KeeperDive.Feet;
        float follow = _reflex ? k.ReflexBodyFollow : standing ? 0.5f * k.BodyFollow : k.BodyFollow;
        Vector3 moved = _reflex ? _hands - limbFrom : _hands - _home;
        var old = _k.Position;
        var pos = new Vector3(_feet0.X + carry.X + follow * moved.X, 0f, _feet0.Z + carry.Y + follow * moved.Z);
        var vel = Vector3.Zero;
        _pitch.ClampPlayer(ref pos, ref vel);
        _k.Position = pos;
        if (dt > 0f) _k.Velocity = (pos - old) / dt;

        float need = _reflex ? lateral : dist;
        float most = _reflex ? MathF.Max(_limbStart, Keeper.ReflexRange(_k.Sheet, _t)) : Keeper.Stretch(_k.Sheet, _t);
        bool reached = reach >= need - 1e-3f || reach >= most - 1e-3f;
        if (reached || _contact) _reachedFor += dt;
        float sinceStart = _since - SaveStart;
        // He stays in the save until the ball has had time to arrive, then lands (or, standing, is set again).
        bool due = sinceStart >= _targetT + _jitter + k.DiveLandSeconds;
        bool land = due && _reachedFor >= k.DiveLandSeconds && (!standing || sinceStart >= k.StandSaveSeconds)
                    || sinceStart >= MathF.Max(k.DiveMaxSeconds, _targetT + _jitter + k.DiveLandSeconds);
        if (!land) return;
        _active = false;
        if (standing) ToStand();
        else { Enter(Phase.Grounded); SetAction(KeeperAction.Grounded); }
    }

    /// <summary>
    /// If the ball crossed the keeper's save plane (through his hands, across the shot's direction) this tick without contact,
    /// records that he was beaten, with the distance he needed and the reach he had at that moment.
    /// </summary>
    internal void ObservePlane(Vector3 prev, Vector3 now)
    {
        if (!_active || _phase is not (Phase.Reacting or Phase.Diving)) return;
        Vector3 home = _phase == Phase.Reacting ? HomeFor(_k.Position) : _home;
        var h = _phase == Phase.Reacting ? new Vector2(home.X, home.Z) : _planePoint;
        float d0 = Vector2.Dot(new Vector2(prev.X, prev.Z) - h, _planeN);
        float d1 = Vector2.Dot(new Vector2(now.X, now.Z) - h, _planeN);
        if (!(d0 < 0f && d1 >= 0f)) return;
        float f = d0 / (d0 - d1);
        Vector3 at = Vector3.Lerp(prev, now, f);
        Record(at, home, KeeperResult.Beaten);
        _active = false;
    }

    void Record(Vector3 at, Vector3 home, KeeperResult action)
    {
        float needed = (at - home).Length();
        float reach = _phase == Phase.Diving ? Reach(DiveTau) : _phase == Phase.Reacting ? _span : 0f;
        Report.NeededM = needed;
        Report.ReachM = reach;
        Report.MarginM = reach - needed;
        Report.Action = action;
    }

    // ---- contact ----

    /// <summary>
    /// The ball's path met the hands (or the smothering body, or his standing body) at <paramref name="contact"/>. Held when it
    /// is slow enough for the body or a smother, or, at the hands, within his catch range for its speed and the time he had (a
    /// standing or diving range; at the feet or after backpedalling as for a faster ball) and the save no harder than
    /// <c>catch_max_difficulty</c>. Otherwise blocked (the body, at the feet, a smother, a reflex foot or body block, or before he
    /// moved), tipped over the bar (high, near his line), fingertipped (a dive at least <c>fingertip_difficulty</c> hard) or
    /// parried. Returns true if held.
    /// </summary>
    internal bool Contact(Ball ball, Vector3 contact, SimRng rng, out float speedAtContact, bool body = false)
    {
        var k = _t.Keeper;
        var sheet = _k.Sheet;
        Vector3 v = ball.Velocity;
        speedAtContact = v.Length();
        Vector3 home = _phase == Phase.Reacting ? HomeFor(_k.Position) : _home;
        float distance = (contact - home).Length();
        _active = false;
        _contact = true;
        bool smother = _phase == Phase.Smother;
        bool early = _phase == Phase.Reacting || _phase == Phase.Diving && DiveTau <= 0f;
        bool reflex = _phase == Phase.Diving && _reflex && !early;
        bool feet = _phase == Phase.Diving && _dive == KeeperDive.Feet;
        bool standing = early || reflex || _phase == Phase.Diving && _dive == KeeperDive.Catch;
        bool back = _fromBackpedal && _phase == Phase.Diving;
        float difficulty = _phase == Phase.Diving && !early ? _difficulty : 0f;
        if (!_struck) { _roundWon = true; Report.WonBall = true; }
        bool held;
        if (smother) held = speedAtContact < k.SmotherHoldSpeed;
        else if (body) held = speedAtContact < k.BodyHoldSpeed;
        else
        {
            float range = back ? Keeper.CatchRange(sheet, _t, speedAtContact / k.BackpedalCatchFactor, true, _since)
                : feet ? Keeper.CatchRange(sheet, _t, speedAtContact / k.FeetCatchFactor, false, _since)
                : Keeper.CatchRange(sheet, _t, speedAtContact, !standing, _since);
            held = distance <= range && difficulty <= k.CatchMaxDifficulty;
        }
        if (held)
        {
            Record(contact, home, KeeperResult.Held);
            Report.Save = smother ? SaveKind.Smother : SaveKind.Catch;
            Enter(Phase.Held);
            SetAction(KeeperAction.Holding);
            _hands = contact;
            _k.HandPosition = _hands;
            ball.Velocity = Vector3.Zero;
            ball.Spin = Vector3.Zero;
            ball.Position = _hands;
            return true;
        }

        Record(contact, home, KeeperResult.Parried);
        if (smother || feet || early || body || reflex && _dive != KeeperDive.ReflexHand)
        {
            Block(ball, v, rng);
            Report.Save = SaveKind.Block;
            if (early) ToStand();
            return false;
        }
        bool tipOver = contact.Y >= k.TipOverBarHeight * _pitch.BarHeight && _pitch.GoalLineX - _k.Position.X <= k.TipOverBarDistance;
        bool fingertip = !reflex && difficulty >= k.FingertipDifficulty;
        if (tipOver) { TipOver(ball, v, contact, rng); Report.Save = SaveKind.TipOver; }
        else if (fingertip) { Fingertip(ball, v, contact, rng); Report.Save = SaveKind.Fingertip; }
        else { Parry(ball, v, contact, home, difficulty, rng); Report.Save = SaveKind.Parry; }
        return false;
    }

    /// <summary>
    /// A parry: 40 to 60% of the speed (at least the minimum parry speed), back out and steered wide by handling, less so and with
    /// a larger angle error the harder the save.
    /// </summary>
    void Parry(Ball ball, Vector3 v, Vector3 contact, Vector3 home, float difficulty, SimRng rng)
    {
        var k = _t.Keeper;
        float handling = Rating.R01(_k.Sheet.Handling);
        float d = Math.Clamp(difficulty, 0f, 1.5f);
        var horiz = new Vector2(v.X, v.Z);
        Vector2 back = horiz.LengthSquared() > 1e-6f ? -Vector2.Normalize(horiz) : new Vector2(-1f, 0f);
        float dz = contact.Z - home.Z;
        float side = MathF.Abs(dz) > 0.05f ? MathF.Sign(dz) : (rng.NextFloat() < 0.5f ? -1f : 1f);
        Vector2 lateral = new(-back.Y, back.X);
        // Pick the lateral axis direction that points toward 'side' in world Z.
        if (MathF.Sign(lateral.Y) != side && MathF.Abs(lateral.Y) > 1e-3f) lateral = -lateral;
        Vector2 hd = back + lateral * (handling * k.ParryWideMax * (1f - 0.5f * MathF.Min(1f, d)));
        float yaw = MathF.Atan2(hd.Y, hd.X) + rng.Normal(0f, k.ParryYawNoiseDeg * (0.5f + d)) * MathF.PI / 180f;
        float speed = MathF.Max(k.MinParrySpeed, v.Length() * rng.Uniform(k.ParrySpeedMin, k.ParrySpeedMax));
        Vector3 dir = Vector3.Normalize(new Vector3(MathF.Cos(yaw), k.ParryLift, MathF.Sin(yaw)));
        ball.Velocity = dir * speed;
        ball.Spin = Vector3.Zero;
    }

    /// <summary>A block (body, feet, smother, a reflex foot or body block, or a ball that hits him before he moves): back out within the block angle, a quarter to near half the speed, at least the block speed.</summary>
    void Block(Ball ball, Vector3 v, SimRng rng)
    {
        var k = _t.Keeper;
        var horiz = new Vector2(v.X, v.Z);
        Vector2 back = horiz.LengthSquared() > 1e-6f ? -Vector2.Normalize(horiz) : new Vector2(-1f, 0f);
        float yaw = MathF.Atan2(back.Y, back.X) + rng.Uniform(-k.BlockAngleDeg, k.BlockAngleDeg) * MathF.PI / 180f;
        float speed = MathF.Max(k.BlockSpeed, v.Length() * rng.Uniform(k.BlockSpeedMin, k.BlockSpeedMax));
        Vector3 dir = Vector3.Normalize(new Vector3(MathF.Cos(yaw), k.ParryLift, MathF.Sin(yaw)));
        ball.Velocity = dir * speed;
        ball.Spin = Vector3.Zero;
    }

    /// <summary>
    /// A tip over the bar: the ball keeps going toward the goal at 40 to 60% of its speed, pushed up steeply enough to clear the
    /// crossbar with room to spare.
    /// </summary>
    void TipOver(Ball ball, Vector3 v, Vector3 contact, SimRng rng)
    {
        var k = _t.Keeper;
        var h = new Vector2(v.X, v.Z);
        Vector2 dirH = h.LengthSquared() > 1e-6f ? Vector2.Normalize(h) : new Vector2(1f, 0f);
        if (dirH.X < 0.3f) dirH = Vector2.Normalize(new Vector2(0.3f, dirH.Y));
        float speed = MathF.Max(k.MinParrySpeed, v.Length() * rng.Uniform(k.ParrySpeedMin, k.ParrySpeedMax));
        float dx = MathF.Max(0.05f, _pitch.GoalLineX - contact.X);
        float clear = _pitch.BarHeight + _t.Ball.Radius + 0.3f;
        float g = 9.81f;
        float phi = 70f * MathF.PI / 180f;
        for (float deg = 20f; deg <= 70f; deg += 2.5f)
        {
            float a = deg * MathF.PI / 180f;
            float vx = speed * MathF.Cos(a) * dirH.X;
            float tl = dx / MathF.Max(0.1f, vx);
            float y = contact.Y + speed * MathF.Sin(a) * tl - 0.5f * g * tl * tl;
            if (y >= clear) { phi = a; break; }
        }
        ball.Velocity = new Vector3(dirH.X * MathF.Cos(phi), MathF.Sin(phi), dirH.Y * MathF.Cos(phi)) * speed;
        ball.Spin = Vector3.Zero;
    }

    /// <summary>
    /// A fingertip save: the ball keeps <c>fingertip_speed_kept</c> of its speed and its path bends away from the goal's centre by
    /// a half to the whole of <c>fingertip_max_bend_deg</c>. It can still go in.
    /// </summary>
    void Fingertip(Ball ball, Vector3 v, Vector3 contact, SimRng rng)
    {
        var k = _t.Keeper;
        float side = MathF.Abs(contact.Z) > 0.05f ? MathF.Sign(contact.Z) : (rng.NextFloat() < 0.5f ? -1f : 1f);
        float bend = rng.Uniform(0.5f, 1f) * k.FingertipMaxBendDeg * MathF.PI / 180f;
        var h = new Vector2(v.X, v.Z);
        float yaw = MathF.Atan2(h.Y, h.X);
        // Turning toward +Z increases the yaw for a ball heading +X; bend toward 'side'.
        yaw += side * bend;
        float hs = h.Length() * k.FingertipSpeedKept;
        ball.Velocity = new Vector3(MathF.Cos(yaw) * hs, v.Y * k.FingertipSpeedKept, MathF.Sin(yaw) * hs);
        ball.Spin *= k.FingertipSpeedKept;
    }
}
