using System.Globalization;
using System.Numerics;
using FootballSim.Core;

/// <summary>
/// The human probe: plans that play the attacker like a person with a pad, one <see cref="TickIntent"/> per tick built only from
/// what a player sees (the ball, his feet, the defender, the keeper and his team-mate), as the keeper probe's plans do; never a
/// scripted attacker and never a write to the world. Each plan runs for an even attacker (striker_a) and a fast one (pace_star)
/// and is held to the bands below. Statistics count the first attack only (see <see cref="FirstAttackReport"/>).
///   --probe human [--n 2000] [--seed 1] [--plans a,b] [--attacker even|fast] [--scenario id] [--brain contain|legacy]
/// <para>
/// A person varies: trigger distances, angles, timings, the aim and the power each get a little jitter from the probe's own
/// generator, seeded from the round's seed (never from the world's generators).
/// </para>
/// </summary>
static class HumanProbe
{
    static readonly CultureInfo Ic = CultureInfo.InvariantCulture;

    enum Plan { WideSprint, StopGo, CutInside, SkillWide, ShootSet, Round, RoundClose, FeintRound, PassRound }

    static readonly (string Name, Plan Plan, string Scenario)[] Plans =
    {
        ("wide_sprint", Plan.WideSprint, "defender_duel"), ("stop_go", Plan.StopGo, "defender_duel"),
        ("cut_inside", Plan.CutInside, "defender_duel"), ("skill_wide", Plan.SkillWide, "defender_duel"),
        ("shoot_set", Plan.ShootSet, "breakaway"), ("round", Plan.Round, "breakaway"),
        ("round_close", Plan.RoundClose, "breakaway"), ("feint_round", Plan.FeintRound, "breakaway"),
        ("pass_round", Plan.PassRound, "two_v_one"),
    };

    static readonly (string Name, string Sheet)[] Attackers = { ("even", "striker_a"), ("fast", "pace_star") };

    // ---- the bands (the defence's targets as set on 2026-11-24; percentages of rounds, seconds, metres) ----

    /// <summary>A band: inclusive bounds (null: open), or for round and round_close a ceiling relative to shoot_set's goals.</summary>
    readonly record struct Band(float? Lo, float? Hi, bool Strict = false)
    {
        public bool Holds(float v) => (Lo is not float lo || v >= lo) && (Hi is not float hi || (Strict ? v < hi : v <= hi));
        public string Text(string unit) => Lo is float lo && Hi is float hi ? $"{F(lo)}-{F(hi)}{unit}"
            : Lo is float l ? $">= {F(l)}{unit}" : Hi is float h ? $"{(Strict ? "<" : "<=")} {F(h)}{unit}" : "-";
        static string F(float x) => x.ToString(x == MathF.Round(x) ? "F0" : "F1", Ic);
    }

    /// <summary>Past the defender, % of rounds, per defender_duel plan: (even, fast).</summary>
    static readonly Dictionary<Plan, (Band Even, Band Fast)> PastBands = new()
    {
        [Plan.WideSprint] = (new(null, 30f), new(40f, 60f)),
        [Plan.StopGo] = (new(null, 35f), new(null, 60f)),
        [Plan.CutInside] = (new(25f, 45f), new(null, 60f)),
        [Plan.SkillWide] = (new(35f, 55f), new(45f, 65f)),
    };
    /// <summary>First-attack goals, % of rounds: feint_round (both attackers) and pass_round (even only).</summary>
    static readonly Band FeintRoundGoals = new(25f, 40f);
    static readonly Band PassRoundGoalsEven = new(40f, 55f);
    /// <summary>round and round_close: first-attack goals at most shoot_set's measured rate minus this, points.</summary>
    const float RoundBelowShootSet = 3f;
    /// <summary>defender_duel, all four plans: goals of the best plan, %.</summary>
    static readonly Band BestGoalsEven = new(12f, 22f), BestGoalsFast = new(25f, 38f);
    /// <summary>defender_duel, all four plans: median seconds from the round's start to past the defender, rounds that got past.</summary>
    static readonly Band PastTimeEven = new(3.0f, null), PastTimeFast = new(2.5f, null);
    /// <summary>defender_duel, all four plans: tackles (lunges and slides) tried per round, and fouls, % of rounds.</summary>
    static readonly Band TacklesPerRound = new(0.5f, null);
    static readonly Band Fouls = new(null, 6f, Strict: true);
    /// <summary>breakaway, all plans: p90 of the keeper's depth off his line while the carrier has the ball more than 8 m from him, m.</summary>
    static readonly Band KeeperDepthP90 = new(null, 6.5f);
    const float DepthSampleBeyondM = 8f;
    /// <summary>breakaway, all plans: median seconds from taken round to back on the ball-to-goal line.</summary>
    static readonly Band RecoverMedian = new(null, 1.5f);

    // ---- the person ----

    /// <summary>What a player reads off the screen this tick, in the ground plane.</summary>
    readonly struct View
    {
        public readonly Vector2 Feet, Ball, Goal, ToGoal, Keeper;
        public readonly Vector2? Defender;
        public readonly float BallGoal, KeeperBall, DefenderBall;
        public readonly bool Mine;

        public View(World w)
        {
            Feet = G(w.Attacker.Position);
            Ball = G(w.Ball.Position);
            Goal = new Vector2(w.Pitch.GoalLineX, 0f);
            ToGoal = Norm(Goal - Feet);
            Keeper = G(w.Keeper.Position);
            Defender = w.Defender is { } d ? G(d.Position) : null;
            BallGoal = Vector2.Distance(Ball, Goal);
            KeeperBall = Vector2.Distance(Keeper, Ball);
            DefenderBall = Defender is Vector2 dp ? Vector2.Distance(dp, Ball) : float.PositiveInfinity;
            Mine = w.BallController == w.Attacker;
        }

        /// <summary>The side (+1: +Z of the run to goal, -1: -Z) away from <paramref name="p"/>'s offset off the ball-to-goal line; 0 when he is on it.</summary>
        public int OpenSide(Vector2 p)
        {
            var u = Norm(Goal - Ball);
            float across = Vector2.Dot(p - Ball, new Vector2(-u.Y, u.X));
            return MathF.Abs(across) > 0.15f ? (across > 0f ? -1 : 1) : 0;
        }
    }

    static Vector2 G(Vector3 v) => new(v.X, v.Z);
    static Vector2 Norm(Vector2 v) => v.LengthSquared() > 1e-6f ? Vector2.Normalize(v) : new Vector2(1f, 0f);
    static Vector2 Rot(Vector2 v, float deg)
    {
        float a = deg * MathF.PI / 180f, c = MathF.Cos(a), s = MathF.Sin(a);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    sealed class Human
    {
        readonly Plan _plan;
        readonly SimRng _rng;                 // the probe's own generator: the person's variation, never the world's
        // Jittered once per round: the trigger distance, the angle, a time and which skill move.
        readonly float _trigger, _angle, _wait, _range, _aimJitter;
        readonly bool _elastico;
        int _phase, _phaseTick, _side;
        Vector2 _dir;
        int _pressTick = -1, _releaseTick = -1, _holdTicks, _shotsAtPress;
        Vector2 _aim;
        int _passHeld, _receiver = -1;
        public int Phase => _phase;

        public Human(Plan plan, ulong seed, World w)
        {
            _plan = plan;
            _rng = new SimRng(seed ^ 0x48554D414E505231UL);
            (float trig, float ang) = plan switch
            {
                Plan.WideSprint => (6f, 35f), Plan.StopGo => (5f, 30f), Plan.CutInside => (3f, 50f), Plan.SkillWide => (3.5f, 45f),
                Plan.Round or Plan.RoundClose => (7f, 45f), Plan.FeintRound => (4f, 60f),
                Plan.PassRound => (PassGap(w) + 1f, 0f), _ => (0f, 0f),
            };
            _trigger = trig * _rng.Uniform(0.9f, 1.1f);
            _angle = ang + _rng.Uniform(-5f, 5f);
            _wait = plan == Plan.StopGo ? 0.4f * _rng.Uniform(0.8f, 1.2f) : 0f;
            _range = plan == Plan.ShootSet ? _rng.Uniform(11f, 13f) : ShootRange * _rng.Uniform(0.92f, 1.08f);
            _aimJitter = _rng.Uniform(-0.3f, 0.3f);
            _elastico = _rng.NextFloat() < 0.5f;
            // cut_inside goes wide to the side with more room from the start
            if (plan == Plan.CutInside) _side = PickSide(new View(w), 0);
        }

        /// <summary>The gap a containing defender keeps (4.0 m at jockeying 1, 2.5 m at 99), for the pass trigger.</summary>
        static float PassGap(World w) => w.Defender is { } d ? 4.0f + (2.5f - 4.0f) * Rating.R01(d.Sheet.Jockeying) : 3f;

        /// <summary>The finishing range: a low shot at the far corner from inside it, metres from the goal centre.</summary>
        const float ShootRange = 12f;
        /// <summary>Once past the defender he may shoot from further out: the front of the penalty area, metres.</summary>
        const float PastShootRange = 16.5f;

        int PickSide(in View v, int open) => open != 0 ? open
            : MathF.Abs(v.Ball.Y) > 0.5f ? (v.Ball.Y > 0f ? -1 : 1) : (_rng.NextFloat() < 0.5f ? -1 : 1);

        void Enter(World w, int phase) { _phase = phase; _phaseTick = w.Tick; }
        float InPhase(World w) => (w.Tick - _phaseTick) * w.Tuning.Dt;

        public TickIntent Next(World w)
        {
            var it = new TickIntent();
            var v = new View(w);

            // A shot under way: hold for the power, then release with the aim. A release that struck nothing: try again.
            if (_releaseTick >= 0 && w.State == RoundState.Live && w.Shots.Count == _shotsAtPress && w.Tick - _releaseTick > 50) { _pressTick = -1; _releaseTick = -1; }
            // Struck: the shot is gone; he plays on (after a save or a block, at the loose ball).
            if (_releaseTick >= 0 && w.Shots.Count > _shotsAtPress) { _pressTick = -1; _releaseTick = -1; }
            // A loose ball (a rebound, a tackle that poked it away, a heavy touch) he runs at, as a person would; not while a pass he
            // played is on its way to the team-mate or his own skill move is under way.
            bool passing = _plan == Plan.PassRound && _phase == 1;
            bool moving = w.Attacker.SkillMove != null;
            if (_pressTick < 0 && w.BallController == null && w.State == RoundState.Live && !passing && !moving && w.Tick > 1)
                return Stick(ref it, Norm(v.Ball - v.Feet), 1f);
            if (_pressTick >= 0)
            {
                it.Driven = true;
                if (_releaseTick < 0 && w.Tick - _pressTick < _holdTicks) { it.ShootHeld = true; it.MoveX = v.ToGoal.X; it.MoveZ = v.ToGoal.Y; return it; }
                if (_releaseTick < 0) _releaseTick = w.Tick;
                it.MoveX = _aim.X; it.MoveZ = _aim.Y;
                return it;
            }

            return _plan switch
            {
                Plan.WideSprint or Plan.StopGo or Plan.CutInside or Plan.SkillWide => Duel(w, v, ref it),
                Plan.ShootSet => ShootSet(w, v, ref it),
                Plan.PassRound => PassRound(w, v, ref it),
                _ => Breakaway(w, v, ref it),
            };
        }

        static bool PastDefender(in View v) => v.Defender is not Vector2 d || World.IsPast(v.Ball, d, v.Goal);

        TickIntent Stick(ref TickIntent it, Vector2 dir, float sprint, bool close = false, float size = 1f)
        {
            it.MoveX = dir.X * size; it.MoveZ = dir.Y * size; it.Sprint = sprint; it.CloseControl = close;
            return it;
        }

        /// <summary>Finishing: at goal, a low shot at the far corner once in range, or once past the defender inside the area.</summary>
        TickIntent Finish(World w, in View v, ref TickIntent it, float sprint = 1f, bool close = false)
        {
            if (v.Mine && (v.BallGoal <= _range || PastDefender(v) && v.BallGoal <= PastShootRange)) return Press(w, v, ref it, FarSide(v));
            return Stick(ref it, v.ToGoal, sprint, close);
        }

        int FarSide(in View v) => MathF.Abs(v.Ball.Y) > 0.5f ? (v.Ball.Y > 0f ? -1 : 1) : (v.Keeper.Y > v.Ball.Y ? -1 : 1);

        // The four duel plans: an approach, a move off a trigger distance from the defender, then round him and finish.
        TickIntent Duel(World w, in View v, ref TickIntent it)
        {
            var d = v.Defender!.Value;
            bool past = PastDefender(v);
            var u = Norm(v.Goal - v.Ball);
            // level with him or past: go for goal
            bool level = Vector2.Dot(d - v.Ball, u) <= 0f || past;
            switch (_plan)
            {
                case Plan.WideSprint:
                    if (_phase == 0 && v.Mine && v.DefenderBall <= _trigger) { _side = PickSide(v, v.OpenSide(d)); _dir = Rot(u, _side * _angle); Enter(w, 1); }
                    if (_phase == 1 && (level || InPhase(w) > 2f || MathF.Abs(v.Feet.Y) > 8.5f)) Enter(w, 2);
                    if (_phase == 1) return Stick(ref it, _dir, 1f);
                    return Finish(w, v, ref it);
                case Plan.StopGo:
                    if (_phase == 0 && v.Mine && v.DefenderBall <= _trigger) Enter(w, 1);
                    if (_phase == 1 && InPhase(w) >= _wait) { _side = PickSide(v, v.OpenSide(d)); _dir = Rot(u, _side * _angle); Enter(w, 2); }
                    if (_phase == 1) return Stick(ref it, u, 0f, close: true, size: 0.35f);
                    if (_phase == 2 && (level || InPhase(w) > 2f || MathF.Abs(v.Feet.Y) > 8.5f)) Enter(w, 3);
                    if (_phase == 2) return Stick(ref it, _dir, 1f);
                    return Finish(w, v, ref it);
                case Plan.CutInside:
                    if (_phase == 0 && v.Mine && v.DefenderBall <= _trigger) { _dir = Rot(u, -_side * _angle); Enter(w, 1); }
                    if (_phase == 0) return Stick(ref it, Rot(v.ToGoal, _side * 30f), 1f);
                    if (_phase == 1 && (level || InPhase(w) > 1.2f)) Enter(w, 2);
                    if (_phase == 1) return Stick(ref it, _dir, 1f);
                    return Finish(w, v, ref it);
                default: // skill_wide
                    if (_phase == 0 && v.Mine && v.DefenderBall <= _trigger) { _side = PickSide(v, v.OpenSide(d)); _dir = u; Enter(w, 1); }
                    if (_phase == 0) return Stick(ref it, v.ToGoal, 0f);
                    if (_phase == 1)
                    {
                        int i = w.Tick - _phaseTick;
                        if (i < Gesture) return SkillGesture(ref it, i, _elastico, _elastico ? _side : -_side);
                        _dir = Rot(u, _side * 45f);
                        Enter(w, 2);
                    }
                    if (_phase == 2 && (level || InPhase(w) > 1f || MathF.Abs(v.Feet.Y) > 8.5f)) Enter(w, 3);
                    if (_phase == 2) return Stick(ref it, _dir, 1f);
                    return Finish(w, v, ref it);
            }
        }

        /// <summary>The ticks of a skill gesture (the skill probe's traces: a step-over forward then to its side, an elastico back then to its side).</summary>
        const int Gesture = 30;

        /// <summary>
        /// One tick of a step-over (forward, then round to the sold side) or an elastico (back, then round to its side) on the right
        /// stick, against the run direction <see cref="_dir"/>; the left stick stays on the run and sprint is let go.
        /// </summary>
        TickIntent SkillGesture(ref TickIntent it, int i, bool elastico, int side)
        {
            var fwd = _dir;
            var right = new Vector2(-fwd.Y, fwd.X);
            Vector2 rs = i < 6 ? (elastico ? -fwd : fwd) : i < 12 ? right * side : Vector2.Zero;
            it.RightX = rs.X; it.RightZ = rs.Y;
            it.MoveX = fwd.X; it.MoveZ = fwd.Y;
            it.CloseControl = true;
            return it;
        }

        // breakaway, shoot_set: jogs in and shoots low at the far corner from the distance drawn for the round.
        TickIntent ShootSet(World w, in View v, ref TickIntent it)
        {
            float atStrike = v.BallGoal - MathF.Max(0f, Vector2.Dot(G(w.Attacker.Velocity), v.ToGoal)) * w.Tuning.Shot.WindupSeconds;
            if (v.Mine && atStrike <= _range) return Press(w, v, ref it, FarSide(v));
            return Stick(ref it, v.ToGoal, 0f);
        }

        // breakaway, round / round_close / feint_round: at the keeper, round him, tap in. Phases: 0 the approach, 1 the step-over
        // (feint_round), 2 going round him, 3 the tap-in.
        TickIntent Breakaway(World w, in View v, ref TickIntent it)
        {
            var u = Norm(v.Goal - v.Ball);
            bool closeRound = _plan == Plan.RoundClose;
            if (_phase == 0)
            {
                bool close = (closeRound || _plan == Plan.FeintRound) && v.KeeperBall <= 8f;
                if (!(v.Mine && v.KeeperBall <= _trigger)) return Stick(ref it, v.ToGoal, close ? 0f : 1f, close);
                // round: away from the side he leans to; feint: sold toward it, out the other way
                _side = PickSide(v, v.OpenSide(v.Keeper));
                _dir = u;
                Enter(w, _plan == Plan.FeintRound ? 1 : 2);
                if (_phase == 2) _dir = Rot(u, _side * _angle);
            }
            if (_phase == 1)
            {
                int i = w.Tick - _phaseTick;
                if (i < Gesture) return SkillGesture(ref it, i, false, -_side);
                _dir = Rot(_dir, _side * _angle);
                Enter(w, 2);
            }
            if (_phase == 2)
            {
                bool roundedHim = Keeper.MouthDistance(v.Ball, w.Pitch) < Keeper.MouthDistance(v.Keeper, w.Pitch) + 0.5f;
                if (roundedHim || InPhase(w) > 1.5f || MathF.Abs(v.Feet.Y) > 8.5f) Enter(w, 3);
                else return Stick(ref it, _dir, closeRound ? 0f : 1f, closeRound);
            }
            // Tap in: carried on toward the goal, at the open half of it once he is off the line to the open post inside 9 m, or
            // close in.
            if (v.Mine && (Clear(v.Ball, v.Keeper, w) && v.BallGoal <= TapInRange || v.BallGoal <= 6f || InPhase(w) > 2.5f))
                return Press(w, v, ref it, v.Keeper.Y > v.Ball.Y ? -1 : 1, tapIn: true);
            return Stick(ref it, v.ToGoal, closeRound ? 0f : 1f, closeRound);
        }

        /// <summary>A tap-in is struck inside this distance of the goal centre once the keeper is off the line to the open post, metres.</summary>
        const float TapInRange = 9f;
        /// <summary>A tap-in aims at the middle of the open half of the goal, metres from its centre.</summary>
        const float TapInAimZ = 1.8f;

        /// <summary>True when the keeper is more than 1.5 m off the line from the ball to the open post (as the keeper probe reads it).</summary>
        static bool Clear(Vector2 ball, Vector2 kp, World w)
        {
            float side = kp.Y > ball.Y ? -1f : 1f;
            var post = new Vector2(w.Pitch.GoalLineX, side * (w.Pitch.GoalHalfWidth - 0.6f));
            var d = post - ball;
            float len = d.Length();
            if (len < 1e-3f) return true;
            var u = d / len;
            float along = Vector2.Dot(kp - ball, u);
            if (along < 0f || along > len) return true;
            return MathF.Abs((kp - ball).X * u.Y - (kp - ball).Y * u.X) > 1.5f;
        }

        // two_v_one, pass_round: dribbles at the defender, passes to the team-mate once he is inside the gap + 1 m; the receiver
        // shoots first time (inside 16 m of goal) or after one touch.
        TickIntent PassRound(World w, in View v, ref TickIntent it)
        {
            PlayerState? mate = null;
            foreach (var m in w.Teams[0]) if (m != w.Attacker && m.Role == PlayerRole.Outfield) { mate = m; break; }
            if (_phase == 0)
            {
                if (mate == null || w.Passes > 0) { Enter(w, 2); return Finish(w, v, ref it, 0f); }
                var aim = Norm(G(mate.Position) - v.Ball);
                bool onside = !w.OffsideOn || w.OffsideLine - mate.Position.X >= 0.5f;
                // He times the press so the pass leaves as the defender comes inside the gap + 1 m: the defender's closing speed
                // through the pass's wind-up is allowed for.
                float lead = 0f;
                if (w.Defender is { } dd)
                {
                    var toBall = Norm(v.Ball - v.Defender!.Value);
                    float closing = MathF.Max(0f, Vector2.Dot(G(dd.Velocity) - G(w.Ball.Velocity), toBall));
                    float charge = PassTapTicks * w.Tuning.Dt / MathF.Max(1e-3f, w.Tuning.Pass.ChargeSeconds);
                    lead = closing * PassModel.ExecutionSeconds(w.Attacker, charge, MathF.Atan2(aim.Y, aim.X), w.Tuning);
                }
                if (_passHeld == 0 && !(v.Mine && v.DefenderBall - lead <= _trigger && onside)) return Finish(w, v, ref it, 0f);
                _passHeld++;
                it.MoveX = aim.X; it.MoveZ = aim.Y; it.PassHeld = _passHeld <= PassTapTicks;
                if (w.Passes > 0 || _passHeld > 180) { _receiver = w.Passes > 0 ? w.ControlledIndex : -1; Enter(w, 1); }
                return it;
            }
            if (_phase == 1)
            {
                // the receiver: first time inside 16 m (shoot pressed while the pass comes), else one touch and shoot
                if (w.ControlledIndex == _receiver && w.BallController != w.Attacker && v.BallGoal > 0f && Vector2.Distance(v.Feet, v.Goal) <= 16f && w.Shots.Count == 0)
                    return Press(w, v, ref it, FarSideFrom(v.Feet, v));
                if (w.BallController == w.Attacker) { Enter(w, 2); return Press(w, v, ref it, FarSide(v)); }
                return Stick(ref it, v.ToGoal, 0f);
            }
            return Finish(w, v, ref it, 0f);
        }

        /// <summary>The pass is a tap of the pass button this many ticks long.</summary>
        const int PassTapTicks = 6;

        static int FarSideFrom(Vector2 p, in View v) => MathF.Abs(p.Y) > 0.5f ? (p.Y > 0f ? -1 : 1) : (v.Keeper.Y > p.Y ? -1 : 1);

        /// <summary>Presses shoot: a low (driven) shot inside the post on <paramref name="side"/>, with the ideal power for the distance and a person's error.</summary>
        TickIntent Press(World w, in View v, ref TickIntent it, int side, bool tapIn = false)
        {
            _pressTick = w.Tick;
            _releaseTick = -1;
            _shotsAtPress = w.Shots.Count;
            float dist = ShotModel.GoalDistance(w.Ball.Position, w.Pitch);
            float charge = Math.Clamp(ShotModel.IdealCharge(dist, w.Tuning) + _rng.Normal(0f, 0.08f), 0.05f, 1f);
            _holdTicks = Math.Max(2, (int)MathF.Ceiling(charge * w.Tuning.Shot.ChargeSeconds / w.Tuning.Dt));
            float z = side * (tapIn ? TapInAimZ : w.Pitch.GoalHalfWidth - 0.8f) + _aimJitter;
            _aim = Norm(new Vector2(w.Pitch.GoalLineX, z) - v.Ball);
            it.ShootHeld = true;
            it.Driven = true;
            it.MoveX = v.ToGoal.X; it.MoveZ = v.ToGoal.Y;
            return it;
        }
    }

    // ---- the rounds ----

    /// <summary>One round, read off the world: the first attack's end, past the defender, the defender's tackles, the keeper.</summary>
    sealed class Rec
    {
        public FirstAttackEnd End;
        public bool Goal, Past, Rounded;
        public float PastS = float.NaN, RecoverS = float.NaN;
        public int Tackles, Fouls;
        public readonly List<float> Depths = new();   // the keeper's depth on ticks with the carrier on the ball beyond 8 m of him
    }

    static Rec Play(Tuning tuning, Scenario sc, PlayerSheet attacker, PlayerSheet keeper, PlayerSheet? defender, PlayerSheet? mate, ulong seed, Plan plan, int every = 0)
    {
        var w = new World(tuning, sc, attacker, keeper, seed, defender, mate);
        w.Begin();
        var human = new Human(plan, seed, w);
        var buf = new TickIntent[w.PlayerCount];
        var rec = new Rec();
        int cap = Replay.TailCap(tuning);
        int tail = (int)MathF.Ceiling(World.PastHoldS * tuning.TickRate) + 1;
        for (int i = 0; i < cap && w.State != RoundState.Report; i++)
        {
            buf[0] = human.Next(w);
            w.Step(buf);
            if (every > 0) TraceLine(w, human, buf[0], every);
            if (w.FirstAttackEnded == null && w.BallController is { Team: 0 } c)
            {
                if (Vector2.Distance(G(c.Position), G(w.Keeper.Position)) > DepthSampleBeyondM) rec.Depths.Add(w.Pitch.GoalLineX - w.Keeper.Position.X);
            }
            // The first attack is over: play on a moment for a pass of the defender still waiting on its 0.5 s, then stop.
            if (w.FirstAttackEnded != null && w.Tick - w.FirstAttackTick >= tail) break;
        }
        rec.End = w.FirstAttackEnded ?? FirstAttackEnd.Time;
        rec.Goal = rec.End == FirstAttackEnd.Goal;
        int endTick = w.FirstAttackEnded != null ? w.FirstAttackTick : w.Tick;
        rec.Past = w.PastTick >= 0 && w.PastTick <= endTick;
        if (rec.Past) rec.PastS = w.PastTick * tuning.Dt;
        if (w.DefenderReport is { } d) { rec.Tackles = d.LungesTried + d.SlidesTried; rec.Fouls = d.Fouls; }
        rec.Rounded = w.KeeperReport.Rounded;
        if (w.KeeperRecoverS is float r) rec.RecoverS = r;
        if (every > 0) Console.WriteLine($"end: first attack {rec.End.ToString().ToLowerInvariant()} at tick {w.FirstAttackTick}, past {(rec.Past ? rec.PastS.ToString("F2", Ic) + " s" : "no")}, " +
                                         $"tackles tried {rec.Tackles}, keeper rounded {rec.Rounded}, recover {(float.IsFinite(rec.RecoverS) ? rec.RecoverS.ToString("F2", Ic) + " s" : "-")}, round outcome {w.Outcome.Label()}");
        return rec;
    }

    /// <summary>One line of a traced round: every <paramref name="every"/> ticks and on every event that matters.</summary>
    static void TraceLine(World w, Human h, in TickIntent it, int every)
    {
        var ev = w.Events.Where(e => e.Kind is EventKind.Tackle or EventKind.TackleStart or EventKind.Block or EventKind.Foul or EventKind.Strike or EventKind.Save
            or EventKind.Goal or EventKind.Pass or EventKind.ControlChanged or EventKind.BoardHit or EventKind.FrameHit or EventKind.SkillMove or EventKind.Offside)
            .Select(e => $"{e.Kind}{(e.Team >= 0 ? $"[{e.Team}.{e.PlayerIndex}]" : "")}").ToList();
        if (w.Tick % every != 0 && ev.Count == 0) return;
        string F(float x, int wd = 6) => x.ToString("F2", Ic).PadLeft(wd);
        var a = w.Attacker; var b = w.Ball.Position; var k = w.Keeper.Position;
        string ctl = w.BallController == null ? "-" : w.BallController.Team == 0 ? $"att{w.BallController.Index}" : w.BallController == w.Keeper ? "kee" : "def";
        string def = w.Defender is { } d ? $"def {F(d.Position.X)},{F(d.Position.Z)} {d.DefenderAction,-9}" : "";
        string stick = $"stick {F(it.MoveX, 5)},{F(it.MoveZ, 5)} spr {it.Sprint:F0}{(it.CloseControl ? " cc" : "")}{(it.ShootHeld ? " SHOOT" : "")}{(it.PassHeld ? " PASS" : "")}{(it.RightX != 0 || it.RightZ != 0 ? $" rs {it.RightX:F1},{it.RightZ:F1}" : "")}";
        Console.WriteLine($"{w.Tick,5} {F(w.SimTime)} {w.State,-7} ph {h.Phase} att{w.ControlledIndex} {F(a.Position.X)},{F(a.Position.Z)} v {F(a.Speed, 5)} ball {F(b.X)},{F(b.Z)} {ctl,-4} {def} kee {F(k.X)},{F(k.Z)} {w.Keeper.KeeperAction,-9} | {stick} {string.Join(" ", ev)}");
    }

    /// <summary>--trace &lt;plan&gt; [--seed 1] [--attacker even|fast] [--every 12]: one round of a plan, tick by tick.</summary>
    static int Trace(Dictionary<string, string> opts, string dataDir, Tuning tuning, string planName)
    {
        var entry = Plans.FirstOrDefault(p => p.Name == planName);
        if (entry.Name == null) { Console.Error.WriteLine($"unknown plan '{planName}'"); return 2; }
        ulong seed = opts.TryGetValue("seed", out var ss) ? ulong.Parse(ss, Ic) : 1UL;
        int every = opts.TryGetValue("every", out var ev) ? int.Parse(ev, Ic) : 12;
        string att = opts.TryGetValue("attacker", out var a) ? a : "even";
        PlayerSheet? P(string id) => DataLoader.LoadPlayer(Path.Combine(dataDir, "players", id + ".json")).Value;
        var sc = DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", entry.Scenario + ".json")).Value!;
        var attacker = P(Attackers.First(x => x.Name == att).Sheet)!;
        Console.WriteLine($"trace {planName} {att} seed {seed} ({entry.Scenario})");
        Play(tuning, sc, attacker, P(sc.DefaultKeeper)!, sc.Defenders > 0 ? P(sc.DefaultDefender) : null, sc.Mate != null ? P(sc.DefaultMate) : null, seed, entry.Plan, every);
        return 0;
    }

    /// <summary>The tuning keys (section.key) that --brain legacy sets to "legacy".</summary>
    static readonly string[] BrainKeys = { "keeper.brain" };

    /// <summary>Checks the --brain option: null when it is fine (legacy, contain, or none), else the exit code.</summary>
    public static int? CheckBrain(Dictionary<string, string> opts)
    {
        if (!opts.TryGetValue("brain", out var b) || b is "legacy" or "contain") return null;
        Console.Error.WriteLine($"--brain: unknown brain '{b}' (known: contain, legacy)");
        return 2;
    }

    /// <summary>--brain legacy: the tuning with every brain key set to "legacy"; otherwise the tuning as it is.</summary>
    public static Tuning ApplyBrain(Dictionary<string, string> opts, Tuning tuning)
    {
        if (!opts.TryGetValue("brain", out var b) || b != "legacy") return tuning;
        var node = System.Text.Json.Nodes.JsonNode.Parse(DataLoader.TuningToJson(tuning))!;
        foreach (var key in BrainKeys)
        {
            var path = key.Split('.');
            node[path[0]]![path[1]] = "legacy";
        }
        var t = DataLoader.ParseTuning(node.ToJsonString(), "--brain legacy").Value!;
        t.Sheets = tuning.Sheets;
        return t;
    }

    public static int Run(Dictionary<string, string> opts, string dataDir, Tuning tuning)
    {
        if (CheckBrain(opts) is int bad) return bad;
        if (opts.TryGetValue("trace", out var tracePlan)) return Trace(opts, dataDir, tuning, tracePlan);
        bool comparison = opts.TryGetValue("brain", out var brain) && brain == "legacy";
        int n = opts.TryGetValue("n", out var ns) ? int.Parse(ns, Ic) : 2000;
        ulong seed = opts.TryGetValue("seed", out var ss) ? ulong.Parse(ss, Ic) : 1UL;
        var only = opts.TryGetValue("plans", out var pl) ? pl.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet() : null;
        string? onlyScenario = opts.TryGetValue("scenario", out var so) ? so : null;
        string? onlyAttacker = opts.TryGetValue("attacker", out var ao) ? ao : null;
        if (onlyAttacker is not (null or "even" or "fast")) { Console.Error.WriteLine("--attacker even|fast"); return 2; }
        if (only != null && only.FirstOrDefault(p => Plans.All(x => x.Name != p)) is { } unknown)
        { Console.Error.WriteLine($"unknown plan '{unknown}' (known: {string.Join(", ", Plans.Select(p => p.Name))})"); return 2; }

        PlayerSheet? P(string id)
        {
            var r = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", id + ".json"));
            if (!r.Ok) Console.Error.WriteLine("error: " + r.Error);
            return r.Value;
        }
        var sheets = new Dictionary<string, PlayerSheet>();
        foreach (var (name, id) in Attackers) if (P(id) is { } s) sheets[name] = s; else return 1;

        Console.WriteLine($"human probe: n={n} seed={seed} brain={(comparison ? "legacy" : "as loaded")} (keeper.brain={tuning.Keeper.Brain}){(comparison ? ", a comparison run: marks printed, exit 0" : "")}");
        Console.WriteLine($"attackers: even={Attackers[0].Sheet} fast={Attackers[1].Sheet}; first attacks only; past = the ball {World.PastMarginM.ToString("F1", Ic)} m nearer the goal centre than the defender, " +
                          $"the attackers still on it {World.PastHoldS.ToString("F1", Ic)} s later");
        Console.WriteLine("scenario       plan         attacker      n   goal   miss   held   disp   foul   offs   time  |   past  fa_goals");

        var results = new Dictionary<(Plan, string), List<Rec>>();
        bool ok = true;
        var marks = new List<string>();
        foreach (var scGroup in Plans.GroupBy(p => p.Scenario))
        {
            if (onlyScenario != null && scGroup.Key != onlyScenario) continue;
            var rows = scGroup.Where(p => only == null || only.Contains(p.Name)).ToList();
            if (rows.Count == 0) continue;
            var scr = DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", scGroup.Key + ".json"));
            if (!scr.Ok) { Console.Error.WriteLine("error: " + scr.Error); return 1; }
            var sc = scr.Value!;
            var keeper = P(sc.DefaultKeeper);
            var defender = sc.Defenders > 0 ? P(sc.DefaultDefender) : null;
            var mate = sc.Mate != null ? P(sc.DefaultMate) : null;
            if (keeper == null || sc.Defenders > 0 && defender == null || sc.Mate != null && mate == null) return 1;
            foreach (var (attName, _) in Attackers)
            {
                if (onlyAttacker != null && attName != onlyAttacker) continue;
                foreach (var (name, plan, _) in rows)
                {
                    var recs = new Rec[n];
                    Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
                        recs[i] = Play(tuning, sc, sheets[attName], keeper, defender, mate, seed + (ulong)i, plan));
                    var list = recs.ToList();
                    results[(plan, attName)] = list;
                    string Pc(int c) => (100.0 * c / Math.Max(1, n)).ToString("F1", Ic).PadLeft(5) + "%";
                    int E(FirstAttackEnd e) => list.Count(r => r.End == e);
                    int goals = list.Count(r => r.Goal);
                    Console.WriteLine($"{scGroup.Key,-14} {name,-12} {attName,-8} {n,6} {Pc(E(FirstAttackEnd.Goal))} {Pc(E(FirstAttackEnd.Miss))} {Pc(E(FirstAttackEnd.Held))} " +
                                      $"{Pc(E(FirstAttackEnd.Dispossessed))} {Pc(E(FirstAttackEnd.Foul))} {Pc(E(FirstAttackEnd.Offside))} {Pc(E(FirstAttackEnd.Time))}  | " +
                                      $"{(defender != null ? Pc(list.Count(r => r.Past)) : "     -")}    {Pc(goals)}");
                    float goalPct = 100f * goals / Math.Max(1, n);
                    if (PastBands.TryGetValue(plan, out var pb))
                        ok &= Mark(marks, scGroup.Key, name, attName, "past", 100f * list.Count(r => r.Past) / Math.Max(1, n), attName == "even" ? pb.Even : pb.Fast, "%");
                    if (plan == Plan.FeintRound) ok &= Mark(marks, scGroup.Key, name, attName, "first-attack goals", goalPct, FeintRoundGoals, "%");
                    if (plan == Plan.PassRound && attName == "even") ok &= Mark(marks, scGroup.Key, name, attName, "first-attack goals", goalPct, PassRoundGoalsEven, "%");
                    if (plan is Plan.Round or Plan.RoundClose)
                    {
                        if (results.TryGetValue((Plan.ShootSet, attName), out var set))
                        {
                            float setPct = 100f * set.Count(r => r.Goal) / Math.Max(1, set.Count);
                            ok &= Mark(marks, scGroup.Key, name, attName, $"first-attack goals (shoot_set {setPct.ToString("F1", Ic)}% - {RoundBelowShootSet.ToString("F0", Ic)} points)",
                                goalPct, new Band(null, setPct - RoundBelowShootSet), "%");
                        }
                        else marks.Add($"   --   {scGroup.Key} {name} {attName}: first-attack goals {goalPct.ToString("F1", Ic)}% (no band: shoot_set was not run)");
                    }
                }
                // the summary rows of the scenario, for this attacker
                if (scGroup.Key == "defender_duel")
                {
                    var duel = rows.Where(r => results.ContainsKey((r.Plan, attName))).Select(r => (r.Name, Recs: results[(r.Plan, attName)])).ToList();
                    var best = duel.OrderByDescending(r => r.Recs.Count(x => x.Goal)).ThenBy(r => r.Name, StringComparer.Ordinal).First();
                    var all = duel.SelectMany(r => r.Recs).ToList();
                    var pastT = all.Where(r => r.Past).Select(r => r.PastS).OrderBy(x => x).ToArray();
                    string over = duel.Count == 4 ? "all four" : string.Join("+", duel.Select(r => r.Name));
                    ok &= Mark(marks, scGroup.Key, over, attName, $"goals of the best plan ({best.Name})", 100f * best.Recs.Count(x => x.Goal) / Math.Max(1, best.Recs.Count), attName == "even" ? BestGoalsEven : BestGoalsFast, "%");
                    ok &= Mark(marks, scGroup.Key, over, attName, $"median time to get past when past ({pastT.Length} rounds)", pastT.Length == 0 ? float.NaN : DefenceReport.Quantile(pastT, 0.5), attName == "even" ? PastTimeEven : PastTimeFast, " s");
                    ok &= Mark(marks, scGroup.Key, over, attName, "tackles tried per round", (float)all.Sum(r => r.Tackles) / Math.Max(1, all.Count), TacklesPerRound, "");
                    ok &= Mark(marks, scGroup.Key, over, attName, "fouls", 100f * all.Count(r => r.Fouls > 0) / Math.Max(1, all.Count), Fouls, "%");
                }
                if (scGroup.Key == "breakaway")
                {
                    var all = rows.Where(r => results.ContainsKey((r.Plan, attName))).SelectMany(r => results[(r.Plan, attName)]).ToList();
                    string over = rows.Count == 4 ? "all" : string.Join("+", rows.Select(r => r.Name));
                    var depths = all.SelectMany(r => r.Depths).OrderBy(x => x).ToArray();
                    // (a keeper taken round who is not back by the first attack's end counts as slower than any who is)
                    var rounded = all.Where(r => r.Rounded).Select(r => float.IsFinite(r.RecoverS) ? r.RecoverS : float.PositiveInfinity).OrderBy(x => x).ToArray();
                    int back = rounded.Count(float.IsFinite);
                    ok &= Mark(marks, scGroup.Key, over, attName, $"keeper depth p90, carrier on the ball more than {DepthSampleBeyondM.ToString("F0", Ic)} m from him", depths.Length == 0 ? float.NaN : DefenceReport.Quantile(depths, 0.9), KeeperDepthP90, " m");
                    ok &= Mark(marks, scGroup.Key, over, attName, $"median rounded to back on the ball-to-goal line (back in {back} of {rounded.Length} rounds he was taken round)",
                        rounded.Length == 0 ? float.NaN : rounded[(rounded.Length - 1) / 2], RecoverMedian, " s");
                }
            }
        }
        Console.WriteLine();
        Console.WriteLine("bands:");
        foreach (var m in marks) Console.WriteLine(m);
        Console.WriteLine(ok ? "bands: all inside" : comparison ? "bands: some MISS (a comparison run: exit 0)" : "bands: some MISS");
        return ok || comparison ? 0 : 3;
    }

    static bool Mark(List<string> marks, string scenario, string plan, string attacker, string what, float value, Band band, string unit)
    {
        bool inside = float.IsFinite(value) && band.Holds(value);
        string v = float.IsFinite(value) ? value.ToString(unit == "" || unit == " s" ? "F2" : "F1", Ic) + unit : float.IsPositiveInfinity(value) ? "not back" : "-";
        marks.Add($"   {(inside ? "ok  " : "MISS")} {scenario} {plan} {attacker}: {what} {v} (band {band.Text(unit)})");
        return inside;
    }
}
