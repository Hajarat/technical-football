using System.Globalization;
using System.Numerics;
using FootballSim.Core;

/// <summary>
/// The keeper probes: an attacker driven like a human through the replay path (one <see cref="TickIntent"/> per tick, built only
/// from what a player sees: the ball, his own feet and the keeper's position and action), never through
/// <see cref="ScriptedAttacker"/>. Each plan runs over many seeds against the default keeper (and, with <c>--rushing</c>, the
/// same keeper with other rushing_out ratings) and prints how the rounds ended and how the keeper came out, then, for the first
/// <c>--timeline</c> seeds of each plan, a timeline every 0.25 s with the keeper's reasoning (<see cref="World.KeeperThought"/>).
/// <para>
/// Plans: <c>sprint</c>, <c>jog</c> and <c>close</c> (close control held) run straight at the goal centre from the back of the
/// arena and never shoot; <c>angled</c> sprints in from wide; <c>sprint_slow</c> sprints, then jogs with close control once the
/// keeper is within 8 m of the ball; <c>knock</c> sprints with a knock-on 20 m out. <c>step_over</c>, <c>drag_back</c> and
/// <c>ball_roll</c> run in (sprint, then close control within 8 m) and make the move when the keeper commits (rushing at the
/// ball within 4 m, or within 2.5 m of it), then take the ball away from him and shoot low at the open side once past him.
/// <c>chip</c> jogs in and chips at the goal centre
/// (the ideal charge plus a human's error) once the keeper is 5 m off his line or within 9 m of the ball, with the ball inside
/// a distance drawn per seed from 14 to 18 m at the strike; at 12 m if not.
/// </para>
/// </summary>
static class KeeperProbe
{
    enum Plan { Sprint, Jog, Close, CloseStraight, Angled, SprintSlow, Knock, StepOver, DragBack, BallRoll, Chip }

    static readonly (string Name, Plan Plan)[] Plans =
    {
        ("sprint", Plan.Sprint), ("jog", Plan.Jog), ("close", Plan.Close), ("close_straight", Plan.CloseStraight), ("angled", Plan.Angled), ("sprint_slow", Plan.SprintSlow),
        ("knock", Plan.Knock), ("step_over", Plan.StepOver), ("drag_back", Plan.DragBack), ("ball_roll", Plan.BallRoll), ("chip", Plan.Chip),
    };

    static bool Skill(Plan p) => p is Plan.StepOver or Plan.DragBack or Plan.BallRoll;

    /// <summary>The probe's start: the back of the arena (the halfway_run spot) or, for the angled run, wide on the +Z side.</summary>
    static Scenario StartFor(Scenario baseSc, Plan plan)
    {
        var sc = new Scenario
        {
            Id = baseSc.Id, Name = baseSc.Name, DefaultAttacker = baseSc.DefaultAttacker, DefaultKeeper = baseSc.DefaultKeeper,
            Pitch = baseSc.Pitch, TeamSize = 1, KeeperOnLine = baseSc.KeeperOnLine, ClockLimit = baseSc.ClockLimit, ShootDistance = 0f,
            Ball = new StartSpot { X = baseSc.Ball.X, Z = baseSc.Ball.Z }, Attacker = new StartSpot { X = baseSc.Attacker.X, Z = baseSc.Attacker.Z },
        };
        if (plan == Plan.Angled)
        {
            sc.Ball = new StartSpot { X = 5f, Z = 7f };
            float a = MathF.Atan2(-7f, 25f);
            sc.Attacker = new StartSpot { X = 5f - 0.6f * MathF.Cos(a), Z = 7f - 0.6f * MathF.Sin(a), FacingDeg = a * 180f / MathF.PI };
        }
        return sc;
    }

    /// <summary>A human-style attacker for one plan: reads only the ball, his feet and the keeper.</summary>
    sealed class Human
    {
        readonly Plan _plan;
        readonly SimRng _rng;
        readonly float _chipAt;
        int _moveTick = -1;          // the tick the skill move started (-1 not yet)
        int _side;                   // the move's sold side in the player's frame: +1 his right, -1 his left
        Vector2 _runDir;             // the running direction when the move started
        int _pressTick = -1, _releaseTick = -1, _holdTicks;
        Vector2 _aim;
        bool _knocked, _sawCharge;

        public bool Moved => _moveTick >= 0;
        public bool Released => _releaseTick >= 0;
        public int MoveTick => _moveTick;
        public bool Pressed => _pressTick >= 0;
        public float ChipAt => _chipAt;
        public float KeeperDepthAtPress = float.NaN;
        public float KeeperToBallAtMove = float.NaN;

        public Human(Plan plan, ulong seed)
        {
            _plan = plan;
            _rng = new SimRng(seed ^ 0x4B50524F42450001UL);
            _chipAt = 14f + 4f * _rng.NextFloat();
        }

        static Vector2 G(Vector3 v) => new(v.X, v.Z);
        static Vector2 Norm(Vector2 v) => v.LengthSquared() > 1e-6f ? Vector2.Normalize(v) : new Vector2(1f, 0f);

        public TickIntent Next(World w)
        {
            var it = new TickIntent();
            var feet = G(w.Attacker.Position);
            var ball = G(w.Ball.Position);
            var kp = G(w.Keeper.Position);
            var goal = new Vector2(w.Pitch.GoalLineX, 0f);
            Vector2 toGoal = Norm(goal - feet);
            float kb = Vector2.Distance(kp, ball);
            float ballGoal = Vector2.Distance(ball, goal);
            bool mine = w.BallController == w.Attacker;

            // A shot under way: hold for the charge, then release with the aim.
            // A release that struck nothing (the ball out of reach): try again, as a player would.
            if (_releaseTick >= 0 && w.State == RoundState.Live && w.Shots.Count == 0 && w.Tick - _releaseTick > 50) { _pressTick = -1; _releaseTick = -1; }
            if (_pressTick >= 0)
            {
                int held = w.Tick - _pressTick;
                it.Chip = _plan == Plan.Chip;
                it.Driven = _plan != Plan.Chip;
                if (_releaseTick < 0 && held < _holdTicks) { it.ShootHeld = true; it.MoveX = toGoal.X; it.MoveZ = toGoal.Y; return it; }
                if (_releaseTick < 0) _releaseTick = w.Tick;
                it.MoveX = _aim.X; it.MoveZ = _aim.Y;
                return it;
            }

            Vector2 stick = toGoal;
            float sprint = _plan is Plan.Jog or Plan.Close or Plan.CloseStraight or Plan.Chip ? 0f : 1f;
            bool close = _plan is Plan.Close or Plan.CloseStraight;
            // Close control against a keeper who has come out to him: shift the ball across him, still under close control,
            // and shoot low at the open side once he is off the line to it (or after 1.5 s).
            if (_plan == Plan.Close && (_moveTick >= 0 || mine && kb <= 3f))
            {
                if (_moveTick < 0)
                {
                    _moveTick = w.Tick;
                    KeeperToBallAtMove = kb;
                    var right = new Vector2(-toGoal.Y, toGoal.X);
                    float off = Vector2.Dot(kp - ball, right);
                    _side = MathF.Abs(off) > 0.2f ? -MathF.Sign(off) : (_rng.NextFloat() < 0.5f ? -1 : 1);
                    _runDir = toGoal;
                }
                var across = new Vector2(-_runDir.Y, _runDir.X) * _side;
                if (mine && (Clear(ball, kp, w) || w.Tick - _moveTick > 180)) return Press(w, ref it, MathF.Sign(across.Y));
                var st = Norm(across * 0.9f + toGoal * 0.45f);
                it.MoveX = st.X; it.MoveZ = st.Y; it.CloseControl = true;
                return it;
            }
            if (_plan == Plan.SprintSlow && kb <= 8f) { sprint = 0f; close = true; }
            if (_plan == Plan.Knock && !_knocked && mine && ballGoal <= 20f) { _knocked = true; it.KnockOn = true; }
            if (Skill(_plan) && _moveTick < 0 && kb <= 8f) { sprint = 0f; close = true; }

            if (Skill(_plan))
            {
                if (_moveTick < 0 && mine)
                {
                    bool rushing = w.Keeper.KeeperAction is KeeperAction.Rush or KeeperAction.Smother or KeeperAction.Charge;
                    if (rushing && kb <= 4f || kb <= 2.5f)
                    {
                        _moveTick = w.Tick;
                        KeeperToBallAtMove = kb;
                        _runDir = toGoal;
                        var right = new Vector2(-_runDir.Y, _runDir.X);
                        float off = Vector2.Dot(kp - ball, right);
                        // Sell toward the side the keeper leans to; go the other way. A central keeper: either side.
                        _side = MathF.Abs(off) > 0.2f ? MathF.Sign(off) : (_rng.NextFloat() < 0.5f ? -1 : 1);
                    }
                }
                if (_moveTick >= 0) return SkillTick(w, ref it, feet, ball, kp, goal, mine);
            }

            // The chip: once the keeper comes out at him from 18 m in (charging or rushing), or from the distance drawn for this
            // seed if he does not.
            if (_plan == Plan.Chip)
            {
                // Seeing the keeper come out (charging or rushing), he chips once the keeper is well off his line (5 m, or within
                // 9 m of the ball), the ball inside a distance drawn per seed from 14 to 18 m at the strike (and not inside 14 m); at 12 m if not.
                bool coming = w.Keeper.KeeperAction is KeeperAction.Charge or KeeperAction.Rush or KeeperAction.Jockey;
                if (coming) _sawCharge = true;
                if (_sawCharge) sprint = 0f;
                float depth = w.Pitch.GoalLineX - kp.X;
                float atStrike = ballGoal - MathF.Max(0f, Vector2.Dot(G(w.Attacker.Velocity), toGoal)) * ShotModel.WindupSeconds(w.Attacker.Sheet, w.Tuning);
                if (mine && (atStrike <= 12f || _sawCharge && atStrike >= 14f && atStrike <= _chipAt && (depth >= 5f || kb <= 9f))) return Press(w, ref it, 0f);
            }

            it.MoveX = stick.X; it.MoveZ = stick.Y; it.Sprint = sprint; it.CloseControl = close;
            return it;
        }

        TickIntent SkillTick(World w, ref TickIntent it, Vector2 feet, Vector2 ball, Vector2 kp, Vector2 goal, bool mine)
        {
            int i = w.Tick - _moveTick;
            var fwd = _runDir;
            var right = new Vector2(-fwd.Y, fwd.X);
            var sold = right * _side;
            var away = -sold;
            // The move itself: the stick trace (as SkillProbe's), the left stick on the run.
            Vector2 left = fwd;
            Vector2 rs = Vector2.Zero;
            bool shoulders = false;
            int moveTicks;
            switch (_plan)
            {
                case Plan.StepOver:
                    rs = i < 6 ? fwd : i < 12 ? sold : Vector2.Zero; moveTicks = 30; break;
                case Plan.DragBack:
                    if (i < 8) { left = -fwd; shoulders = true; }
                    moveTicks = 30; break;
                default:
                    // The ball roll rolls the ball to the side it is held: away from the keeper.
                    rs = i < 20 ? away : Vector2.Zero; moveTicks = 36; break;
            }
            if (i < moveTicks)
            {
                it.RightX = rs.X; it.RightZ = rs.Y;
                it.MoveX = left.X; it.MoveZ = left.Y;
                it.Finesse = shoulders; it.Chip = shoulders;
                it.CloseControl = true;
                return it;
            }
            // Away from him: off at 60 degrees from the goal line for a while, then at the goal; shoot once past him.
            var toGoal = Norm(goal - feet);
            var escape = Norm(Vector2.Normalize(fwd) * MathF.Cos(MathF.PI / 3f) + away * MathF.Sin(MathF.PI / 3f));
            bool past = Keeper.MouthDistance(ball, w.Pitch) < Keeper.MouthDistance(kp, w.Pitch) + 0.5f || Clear(ball, kp, w);
            Vector2 st = i < moveTicks + 45 && !past ? escape : toGoal;
            if (mine && (past || i > moveTicks + 150)) return Press(w, ref it, MathF.Sign(Vector2.Dot(away, new Vector2(0f, 1f))));
            it.MoveX = st.X; it.MoveZ = st.Y; it.Sprint = 1f;
            return it;
        }

        /// <summary>True when the keeper is more than 1.5 m off the line from the ball to the open post.</summary>
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

        /// <summary>Presses shoot: a chip at the goal centre, or a driven shot inside the post on <paramref name="side"/> (0: away from the keeper).</summary>
        TickIntent Press(World w, ref TickIntent it, float side)
        {
            _pressTick = w.Tick;
            KeeperDepthAtPress = w.Pitch.GoalLineX - w.Keeper.Position.X;
            var ball = G(w.Ball.Position);
            float dist = ShotModel.GoalDistance(w.Ball.Position, w.Pitch);
            float charge = Math.Clamp(ShotModel.IdealCharge(dist, w.Tuning) + _rng.Normal(0f, 0.12f), 0.05f, 1f);
            _holdTicks = Math.Max(2, (int)MathF.Ceiling(charge * w.Tuning.Shot.ChargeSeconds / w.Tuning.Dt));
            float z = 0f;
            if (_plan != Plan.Chip)
            {
                if (side == 0f) side = w.Keeper.Position.Z > ball.Y ? -1f : 1f;
                z = side * (w.Pitch.GoalHalfWidth - 0.8f);
            }
            _aim = Norm(new Vector2(w.Pitch.GoalLineX, z) - ball);
            it.ShootHeld = true;
            it.Chip = _plan == Plan.Chip;
            it.Driven = _plan != Plan.Chip;
            var g = Norm(new Vector2(w.Pitch.GoalLineX, 0f) - G(w.Attacker.Position));
            it.MoveX = g.X; it.MoveZ = g.Y;
            return it;
        }
    }

    /// <summary>One round's record.</summary>
    sealed class Rec
    {
        public bool Won, Held, Goal, Rounded, Struck, Chip;
        public float MinKbTo10 = float.PositiveInfinity;  // closest the keeper got to the ball before the attacker reached 10 m (won: 0)
        public float KbAt10 = float.NaN;
        public float Linger;                               // seconds 6-8 m from the ball, attacker on it within 20 m
        public float Band35;                               // seconds 3-5 m from it
        public float Engage;                               // seconds 1.5-3 m from it
        public float WonAt = float.NaN;                    // ball's distance from goal when he won it
        public float FirstRushAt = float.NaN;              // ball's distance from goal at his first rush or anticipation
        public float DepthAtStrike = float.NaN, ChipSafe = float.NaN, StrikeDist = float.NaN;
        public float KbAtMove = float.NaN;
        public bool Moved;
        public readonly int[] Modes = new int[Enum.GetValues<KeeperMode>().Length];
        public readonly int[] Limits = new int[Enum.GetValues<KeeperLimit>().Length];
        public readonly Dictionary<string, int> Blocks = new();
        public readonly float[] Bands = new float[KeeperReport.DepthBands.Length];
    }

    public static int Run(Dictionary<string, string> opts, string dataDir, Tuning tuning)
    {
        int seeds = opts.TryGetValue("seeds", out var s) ? int.Parse(s, CultureInfo.InvariantCulture) : 40;
        int timeline = opts.TryGetValue("timeline", out var tl) ? int.Parse(tl, CultureInfo.InvariantCulture) : 0;
        if (opts.TryGetValue("every", out var ev)) _every = Math.Max(1, int.Parse(ev, CultureInfo.InvariantCulture));
        var only = opts.TryGetValue("only", out var o) ? o.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet() : null;
        var rushings = opts.TryGetValue("rushing", out var r)
            ? r.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => (int?)int.Parse(x, CultureInfo.InvariantCulture)).ToList()
            : new List<int?> { null };
        var baseSc = DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", "halfway_run.json")).Value;
        var attacker = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", "striker_a.json")).Value;
        var keeper = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", "the_wall.json")).Value;
        if (baseSc == null || attacker == null || keeper == null) { Console.Error.WriteLine("halfway_run, striker_a or the_wall not found"); return 1; }
        var ic = CultureInfo.InvariantCulture;
        Console.WriteLine($"keeper probe: striker_a vs the_wall, {seeds} seeds per row, human-style intents (no ScriptedAttacker)");
        Console.WriteLine("won = keeper touched the ball before any shot; kb<=3@10 = keeper within 3 m of the ball by the time the attacker is 10 m from goal (or won it earlier);");
        Console.WriteLine("linger/band35/engage = seconds 6-8 / 3-5 / 1.5-3 m from the ball with the attacker on it inside 20 m; modes and limits are shares of those ticks");
        foreach (var (name, plan) in Plans)
        {
            if (only != null && !only.Contains(name)) continue;
            var sc = StartFor(baseSc, plan);
            foreach (var rush in rushings)
            {
                var kee = keeper;
                if (rush is int rv) { kee = keeper.Clone(); kee.SetRating("rushing_out", rv); }
                var recs = new List<Rec>();
                for (int i = 0; i < seeds; i++)
                {
                    bool print = i < timeline;
                    if (print) Console.WriteLine($"--- timeline {name} rushing={kee.RushingOut} seed {i + 1}");
                    recs.Add(Play(tuning, sc, attacker, kee, (ulong)(i + 1), plan, print));
                }
                Summary(name, kee.RushingOut, plan, recs);
            }
        }
        return 0;
    }

    static Rec Play(Tuning tuning, Scenario sc, PlayerSheet attacker, PlayerSheet keeper, ulong seed, Plan plan, bool print)
    {
        var w = new World(tuning, sc, attacker, keeper, seed);
        w.Begin();
        var human = new Human(plan, seed);
        var buf = new TickIntent[w.PlayerCount];
        var rec = new Rec();
        Array.Fill(rec.Bands, float.NaN);
        int cap = (int)((tuning.Round.MaxSeconds + tuning.Round.OutcomeSeconds + 5f) * tuning.TickRate);
        bool reached10 = false;
        bool shoots = plan is Plan.Chip or Plan.Close || Skill(plan);
        for (int i = 0; i < cap && w.State != RoundState.Report; i++)
        {
            buf[0] = human.Next(w);
            w.Step(buf);
            Observe(w, rec, ref reached10, print, i);
            if (!shoots && rec.Won) break;
        }
        var kr = w.KeeperReport;
        rec.Rounded = kr.Rounded;
        for (int b = 0; b < rec.Bands.Length; b++) rec.Bands[b] = kr.DepthAtBandM[b];
        rec.Goal = w.Outcome == Outcome.Goal;
        rec.Held = w.Outcome == Outcome.Held && !rec.Struck;
        rec.Moved = human.Moved;
        rec.KbAtMove = human.KeeperToBallAtMove;
        if (plan == Plan.Chip && rec.Struck)
        {
            rec.DepthAtStrike = kr.DepthAtStrikeM;
            rec.StrikeDist = kr.BallDistanceAtStrikeM;
            rec.ChipSafe = Keeper.ChipSafeDepth(keeper, attacker, tuning, w.Pitch, kr.BallDistanceAtStrikeM);
        }
        if (print) Console.WriteLine($"    end: outcome {w.Outcome.Label()}, won {rec.Won}, rounded {rec.Rounded}, struck {rec.Struck}");
        return rec;
    }

    /// <summary>Ticks between timeline lines (<c>--every</c>, default 30: every 0.25 s).</summary>
    static int _every = 30;

    static void Observe(World w, Rec rec, ref bool reached10, bool print, int i)
    {
        var goal = new Vector2(w.Pitch.GoalLineX, 0f);
        var feet = new Vector2(w.Attacker.Position.X, w.Attacker.Position.Z);
        var ball = new Vector2(w.Ball.Position.X, w.Ball.Position.Z);
        var kp = new Vector2(w.Keeper.Position.X, w.Keeper.Position.Z);
        float aGoal = Vector2.Distance(feet, goal), bGoal = Vector2.Distance(ball, goal);
        float kb = Vector2.Distance(kp, ball);
        var th = w.KeeperThought;
        if (w.Shots.Count > 0 && !rec.Struck) { rec.Struck = true; rec.Chip = w.Shots[0].Type == ShotType.Chip; }
        if (!rec.Struck && w.KeeperReport.WonBall && !rec.Won) { rec.Won = true; rec.WonAt = bGoal; if (!reached10) rec.MinKbTo10 = 0f; }
        if (!rec.Struck && !rec.Won)
        {
            if (!reached10) rec.MinKbTo10 = MathF.Min(rec.MinKbTo10, kb);
            if (!reached10 && aGoal <= 10f) { reached10 = true; rec.KbAt10 = kb; }
            if (float.IsNaN(rec.FirstRushAt) && th.Mode is KeeperMode.Rush or KeeperMode.Anticipate or KeeperMode.Smother) rec.FirstRushAt = bGoal;
            if (w.BallController == w.Attacker && bGoal <= 20f)
            {
                float dt = w.Tuning.Dt;
                if (kb is >= 6f and <= 8f) rec.Linger += dt;
                if (kb is >= 3f and <= 5f) rec.Band35 += dt;
                if (kb is >= 1.5f and <= 3f) rec.Engage += dt;
                rec.Modes[(int)th.Mode]++;
                if (th.Mode is KeeperMode.CloseDown or KeeperMode.Arc) rec.Limits[(int)th.Limit]++;
                string blk = (th.RaceBlock ?? "") == "race" ? (th.RaceShort <= 0f ? "race won" : th.RaceShort <= 0.1f ? "race short<0.1s"
                    : th.RaceShort <= 0.3f ? "race short<0.3s" : float.IsPositiveInfinity(th.RaceShort) ? "race: no point" : "race short>0.3s") : th.RaceBlock ?? "";
                rec.Blocks[blk] = rec.Blocks.TryGetValue(blk, out var c) ? c + 1 : 1;
            }
        }
        if (print && i % _every == 0)
        {
            var ic = CultureInfo.InvariantCulture;
            float aSpeed = Vector2.Dot(new Vector2(w.Attacker.Velocity.X, w.Attacker.Velocity.Z), Vector2.Normalize(goal - feet));
            Console.WriteLine(string.Create(ic,
                $"    t{i * w.Tuning.Dt,5:F2} K vOut {-w.Keeper.Velocity.X,5:F2} att→goal {aGoal,5:F1} v→goal {aSpeed,4:F1} ball-att {Vector2.Distance(ball, feet),4:F2} {(w.BallController == w.Attacker ? "on" : "  ")} " +
                $"| K depth {w.Pitch.GoalLineX - kp.X,4:F1} K-ball {kb,5:F1} {w.Keeper.KeeperAction,-9} {th.Mode,-10} {th.Limit,-7} " +
                $"want {th.Want,4:F1} chip {th.ChipCap,4:F1} gap {th.GapCap,4:F1} eng {(th.Engaged ? "y" : "n")} race {(float.IsPositiveInfinity(th.RaceShort) ? "  -  " : th.RaceShort.ToString("+0.00;-0.00", ic))} {th.RaceBlock}"));
        }
    }

    static void Summary(string name, int rushing, Plan plan, List<Rec> recs)
    {
        var ic = CultureInfo.InvariantCulture;
        int n = recs.Count;
        string P(int c) => (100.0 * c / Math.Max(1, n)).ToString("F0", ic) + "%";
        static string M(IEnumerable<float> v)
        {
            var a = v.Where(float.IsFinite).OrderBy(x => x).ToArray();
            return a.Length == 0 ? "-" : $"{a.Average().ToString("F1", CultureInfo.InvariantCulture)} (p10 {a[(int)(0.1 * (a.Length - 1))].ToString("F1", CultureInfo.InvariantCulture)}, p90 {a[(int)(0.9 * (a.Length - 1))].ToString("F1", CultureInfo.InvariantCulture)})";
        }
        int won = recs.Count(x => x.Won), goal = recs.Count(x => x.Goal), rounded = recs.Count(x => x.Rounded);
        Console.WriteLine($"== {name} rushing_out={rushing}: n {n}, keeper won {P(won)} (held {P(recs.Count(x => x.Held))}), goal {P(goal)}, rounded {P(rounded)}, " +
                          $"kb<=3@10 {P(recs.Count(x => x.MinKbTo10 <= 3f))}");
        Console.WriteLine($"   keeper-to-ball when attacker 10 m out {M(recs.Select(x => x.KbAt10))}; won at ball-to-goal {M(recs.Select(x => x.WonAt))}; first rush at {M(recs.Select(x => x.FirstRushAt))}");
        Console.WriteLine($"   seconds 6-8 m {M(recs.Select(x => x.Linger))}, 3-5 m {M(recs.Select(x => x.Band35))}, 1.5-3 m {M(recs.Select(x => x.Engage))}; " +
                          "depth at 20/15/10/6 m: " + string.Join(" / ", Enumerable.Range(0, KeeperReport.DepthBands.Length).Select(b => M(recs.Select(x => x.Bands[b])).Split(' ')[0])));
        long mt = recs.Sum(x => x.Modes.Sum());
        Console.WriteLine("   modes: " + string.Join(", ", Enum.GetValues<KeeperMode>().Select(m => (m, c: recs.Sum(x => x.Modes[(int)m]))).Where(e => e.c > 0)
            .Select(e => $"{e.m} {(100.0 * e.c / Math.Max(1, mt)).ToString("F0", ic)}%")));
        long lt = recs.Sum(x => x.Limits.Sum());
        Console.WriteLine("   depth set by: " + string.Join(", ", Enum.GetValues<KeeperLimit>().Select(m => (m, c: recs.Sum(x => x.Limits[(int)m]))).Where(e => e.c > 0)
            .Select(e => $"{e.m} {(100.0 * e.c / Math.Max(1, lt)).ToString("F0", ic)}%")));
        var blocks = new Dictionary<string, int>();
        foreach (var r in recs) foreach (var (k, v) in r.Blocks) blocks[k] = blocks.TryGetValue(k, out var c) ? c + v : v;
        long bt = blocks.Values.Sum();
        Console.WriteLine("   rush race: " + string.Join(", ", blocks.OrderByDescending(e => e.Value).Select(e => $"{e.Key} {(100.0 * e.Value / Math.Max(1, bt)).ToString("F0", ic)}%")));
        if (Skill(plan) || plan == Plan.Close)
        {
            int moved = recs.Count(x => x.Moved);
            var m = recs.Where(x => x.Moved).ToList();
            Console.WriteLine($"   move made {P(moved)} (keeper-to-ball at the move {M(m.Select(x => x.KbAtMove))}); of those: scored {(100.0 * m.Count(x => x.Goal) / Math.Max(1, moved)).ToString("F0", ic)}%, " +
                              $"keeper won it {(100.0 * m.Count(x => x.Won) / Math.Max(1, moved)).ToString("F0", ic)}%, rounded {(100.0 * m.Count(x => x.Rounded) / Math.Max(1, moved)).ToString("F0", ic)}%");
        }
        if (plan == Plan.Chip)
        {
            var all = recs.Where(x => x.Struck && x.Chip).ToList();
            Console.WriteLine($"   all chips {all.Count}, scored {(100.0 * all.Count(x => x.Goal) / Math.Max(1, all.Count)).ToString("F0", ic)}%, struck from {M(all.Select(x => x.StrikeDist))} m; below: struck from 14-18 m");
            var c = all.Where(x => x.StrikeDist is >= 14f and <= 18f).ToList();
            var outC = c.Where(x => x.DepthAtStrike > x.ChipSafe).ToList();
            var deep = c.Where(x => x.DepthAtStrike >= 4f).ToList();
            string S(List<Rec> l) => $"{l.Count}, scored {(100.0 * l.Count(x => x.Goal) / Math.Max(1, l.Count)).ToString("F0", ic)}%";
            Console.WriteLine($"   chips {S(c)}; keeper beyond his chip-safe depth {S(outC)}; keeper 4 m or more out {S(deep)}; depth at strike {M(c.Select(x => x.DepthAtStrike))}, chip-safe {M(c.Select(x => x.ChipSafe))}");
        }
    }

    /// <summary>
    /// Writes one replay per showcase (halfway_run start, default players) for game screenshots: a straight sprint he comes out
    /// to and wins, a close-control approach he jockeys and pounces on, a step-over that beats him when he commits, and a chip
    /// over him when he is out. Prints each file with the ticks of its key moments.
    /// </summary>
    public static int WriteReplays(string dir, string dataDir, Tuning tuning)
    {
        var sc = DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", "halfway_run.json")).Value;
        var attacker = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", "striker_a.json")).Value;
        var keeper = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", "the_wall.json")).Value;
        if (sc == null || attacker == null || keeper == null) { Console.Error.WriteLine("halfway_run, striker_a or the_wall not found"); return 1; }
        Directory.CreateDirectory(dir);
        var wants = new (string Name, Plan Plan, Func<World, List<(int Tick, KeeperAction Action)>, bool> Ok)[]
        {
            ("keeper-charge-wins-sprint", Plan.Sprint, (w, a) => w.KeeperReport.WonBall && a.Any(x => x.Action == KeeperAction.Charge)),
            ("keeper-jockey-pounce", Plan.CloseStraight, (w, a) => w.KeeperReport.WonBall && a.Any(x => x.Action == KeeperAction.Jockey)
                && a.Any(x => x.Action == KeeperAction.Smother)),
            ("keeper-beaten-by-step-over", Plan.StepOver, (w, a) => w.Outcome == Outcome.Goal && !w.KeeperReport.WonBall),
            ("keeper-chipped-when-out", Plan.Chip, (w, a) => w.Outcome == Outcome.Goal && w.Shots.Count > 0 && w.Shots[0].Type == ShotType.Chip
                && w.KeeperReport.DepthAtStrikeM >= 5f),
        };
        foreach (var (name, plan, ok) in wants)
        {
            bool found = false;
            for (ulong seed = 1; seed <= 200 && !found; seed++)
            {
                var w = new World(tuning, StartFor(sc, plan), attacker, keeper, seed);
                var rec = new ReplayRecorder(w, attacker.Id, keeper.Id, sc.Id);
                w.Recorder = rec;
                w.Begin();
                var human = new Human(plan, seed);
                var buf = new TickIntent[w.PlayerCount];
                var changes = new List<(int Tick, KeeperAction Action)>();
                KeeperAction? last = null;
                int strike = -1;
                int cap = (int)((tuning.Round.MaxSeconds + tuning.Round.OutcomeSeconds + 5f) * tuning.TickRate);
                for (int i = 0; i < cap && w.State != RoundState.Report; i++)
                {
                    buf[0] = human.Next(w);
                    w.Step(buf);
                    if (w.Keeper.KeeperAction != last) { last = w.Keeper.KeeperAction; changes.Add((w.Tick, last.Value)); }
                    if (strike < 0 && w.Events.Any(e => e.Kind == EventKind.Strike)) strike = w.Tick;
                    // A round he wins from a dribble ends a little after, like a play-test round the player gives up.
                    if (plan is Plan.Sprint or Plan.CloseStraight && w.KeeperReport.WonBall && changes[^1].Tick + 90 < w.Tick) break;
                }
                if (!ok(w, changes)) continue;
                string file = Path.Combine(dir, $"{name}-s{seed}.json");
                rec.Replay.Save(file);
                string moments = string.Join(", ", changes.Select(c => $"{c.Action}@{c.Tick}"));
                Console.WriteLine($"{Path.GetFileName(file)}: {rec.Replay.TickCount} ticks; keeper {moments}" +
                                  (human.Moved ? $"; move at tick {human.MoveTick}" : "") + (strike >= 0 ? $"; strike at tick {strike}" : ""));
                found = true;
            }
            if (!found) Console.WriteLine($"not found in 200 seeds: {name}");
        }
        return 0;
    }

    /// <summary>Runs each replay in a folder (or one file) with the keeper's timeline every 0.25 s: the play-test path.</summary>
    public static int RunReplays(string path, string dataDir, Tuning tuning, Dictionary<string, string>? opts = null)
    {
        if (opts != null && opts.TryGetValue("every", out var ev)) _every = Math.Max(1, int.Parse(ev, CultureInfo.InvariantCulture));
        var files = Directory.Exists(path) ? Directory.GetFiles(path, "round-*.json").OrderBy(f => f).ToArray() : new[] { path };
        foreach (var f in files)
        {
            var replay = Replay.Load(f, out var err);
            if (replay == null) { Console.Error.WriteLine(err); continue; }
            var sc = DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", replay.ScenarioId + ".json")).Value;
            var att = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", replay.AttackerId + ".json")).Value;
            var kee = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", replay.KeeperId + ".json")).Value;
            if (sc == null || att == null || kee == null) { Console.Error.WriteLine($"{f}: scenario or player missing"); continue; }
            Console.WriteLine($"--- {Path.GetFileName(f)} {replay.ScenarioId} {replay.AttackerId} vs {replay.KeeperId} seed {replay.Seed}" + (replay.MatchesTuning(tuning) ? "" : " (tuning changed since)"));
            var w = new World(tuning, sc, att, kee, replay.Seed);
            w.Begin();
            var buf = new TickIntent[w.PlayerCount];
            int per = Math.Max(1, replay.PlayersPerTick);
            var rec = new Rec();
            bool r10 = false;
            for (int t = 0; t < replay.TickCount && w.State != RoundState.Report; t++)
            {
                Array.Clear(buf);
                for (int p = 0; p < buf.Length && p < per; p++) buf[p] = replay.Intents[t * per + p].ToIntent();
                w.Step(buf);
                Observe(w, rec, ref r10, true, t);
            }
            Console.WriteLine($"    end: outcome {w.Outcome.Label()}, won {rec.Won}, rounded {w.KeeperReport.Rounded}, struck {rec.Struck}");
        }
        return 0;
    }
}
