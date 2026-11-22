using System.Numerics;

namespace FootballSim.Core;

// The defender in the world: team 1, player 1, in a scenario with one. His bot writes an intent each tick; everything he does
// with it (running, jockeying, the tackles, the barge, the block) is the action code in Defending, applied here, so a human
// defender needs only the intent.
public sealed partial class World
{
    const ulong DefenderSeedSalt = 0x646566656E646572UL;
    const ulong DefenderBotSeedSalt = 0x626F745F64656631UL;

    SimRng _defRng = new(0);        // the defending actions' own stream (pokes, ties)
    DefenderBot? _bot;
    DefenderReport? _defReport;
    TackleKind _wonBy;              // how the loose ball now in play was won, for the report
    bool? _looseTie;                // the coin for a loose ball both are equally near, drawn once per loose ball
    readonly List<BallObstacle> _obstacles = new();

    /// <summary>The defender (team 1, player 1), or null in a scenario without one.</summary>
    public PlayerState? Defender { get; }
    /// <summary>The defender's bot (its mode and hold spot, for probes and the HUD), or null without a defender.</summary>
    public DefenderBot? DefenderBot => _bot;
    /// <summary>
    /// True (the default): the bot plays the defender and the intent passed for him is ignored. False: the defender follows the
    /// intent passed to <see cref="Step"/> (a recorded bot, or later a human).
    /// </summary>
    public bool DefenderBotEnabled { get; set; } = true;
    /// <summary>The defender's numbers so far this round, or null without a defender.</summary>
    public DefenderReport? DefenderReport => _defReport;

    static Vector2 Flat(Vector3 v) => new(v.X, v.Z);

    void ResetDefender(ulong seed)
    {
        _wonBy = TackleKind.None;
        _looseTie = null;
        _obstacles.Clear();
        if (Defender is not { } p) return;
        _defRng = new SimRng(seed ^ DefenderSeedSalt);
        _bot = new DefenderBot(seed ^ DefenderBotSeedSalt);
        _defReport = new DefenderReport();
        p.Position = ClampToPitch(new Vector3(Scenario.Defender.X, 0f, Scenario.Defender.Z));
        var toAttacker = Flat(Attacker.Position) - Flat(p.Position);
        p.Facing = toAttacker.LengthSquared() > 1e-6f ? MathF.Atan2(toAttacker.Y, toAttacker.X) : MathF.PI;
        ResetPlayer(p);
    }

    // ---- the tick ----

    /// <summary>
    /// The defender's part of a tick, after the attacker's: a push dying away, the tackle he starts or carries on, his
    /// movement, the body contact with the attacker, then the auto tackle and the loose ball he controls. Returns true if the
    /// round ended (he controlled the ball).
    /// </summary>
    bool StepDefender(in TickIntent it)
    {
        var p = Defender!;
        var d = p.Def;
        var k = Tuning.Defending;
        float now = SimTime;
        var a = Attacker;

        bool tackle = it.Tackle && !d.TacklePrev, slide = it.Slide && !d.SlidePrev, barge = it.Barge && !d.BargePrev;
        d.TacklePrev = it.Tackle; d.SlidePrev = it.Slide; d.BargePrev = it.Barge;

        if (d.Tackle == TackleKind.Lunge && Tick >= d.EndTick) EndLunge(p);
        if (Defending.CanTackle(p, now))
        {
            if (slide)
            {
                Defending.StartSlide(p, Ball, Tick, Tuning);
                _defReport!.SlidesTried++;
                _events.Add(new SimEvent(EventKind.TackleStart, p.Position, p.Speed, Team: p.Team, PlayerIndex: p.Index));
            }
            else if (tackle)
            {
                Defending.StartLunge(p, Ball, Tick, Tuning);
                _defReport!.LungesTried++;
                _events.Add(new SimEvent(EventKind.TackleStart, p.Position, p.Speed, Team: p.Team, PlayerIndex: p.Index));
            }
        }
        if (barge && Defending.Free(p, now) && now >= d.BargeCooldownUntil) Barge(p);

        MoveDefender(p, it, live: true);
        ApplyPush(p);
        ApplyPush(a);
        Contacts.Separate(a, p, Tick, SimTime, Tuning, Pitch);
        p.TackleFoot = FootOf(p);

        if (State is RoundState.Live or RoundState.Strike)
        {
            // The auto tackle: a dribbled ball inside his reach is his, no button.
            if (_controller == a && Defending.AutoTackleReaches(p, a, Ball, now, Tuning) && !KeeperHasBall())
                WinBall(p, TackleKind.Auto, new Vector2(MathF.Cos(p.Facing), MathF.Sin(p.Facing)));

            // A loose ball at his feet, slow against him, for long enough: he controls it and the round is over.
            var kd = Tuning.Defender;
            // (the round is in Strike while the attacker winds up a shot at a ball he no longer has: it is loose all the same)
            bool under = _controller == null && _ballPlayed && Defending.Free(p, now)
                && Ball.Position.Y <= TouchModel.FeetHeight(Tuning)
                && TouchModel.HorizontalDistance(p.Position, Ball.Position) <= kd.ControlDistanceM
                && (Flat(Ball.Velocity) - Flat(p.Velocity)).Length() <= kd.ControlSpeed;
            d.ControlFor = under ? d.ControlFor + _dt : 0f;
            if (under && d.ControlFor >= kd.ControlS)
            {
                _defReport!.Collected = true;
                _defReport.WonBy = _wonBy;
                SetController(p);
                PublishDefender(it);
                EndRound(Outcome.Dispossessed);
                return true;
            }
        }
        else d.ControlFor = 0f;
        PublishDefender(it);
        return false;
    }

    /// <summary>The defender while the outcome shows: a slide runs out, he gets up, otherwise he eases to a stop.</summary>
    void StepDefenderOutcome()
    {
        var p = Defender!;
        if (p.Def.Tackle == TackleKind.Lunge && Tick >= p.Def.EndTick) EndLunge(p);
        MoveDefender(p, default, live: false);
        ApplyPush(p);
        ApplyPush(Attacker);
        Contacts.Separate(Attacker, p, Tick, SimTime, Tuning, Pitch);
        p.TackleFoot = FootOf(p);
        PublishDefender(default);
    }

    /// <summary>
    /// Moves the defender one tick: along his slide; lying still on the ground and while he gets up; planted through a lunge;
    /// otherwise by his intent, at the jockey pace while he holds jockey or is set for a shot, slowed while he stumbles.
    /// </summary>
    void MoveDefender(PlayerState p, in TickIntent it, bool live)
    {
        var d = p.Def;
        float now = SimTime;
        if (d.Tackle == TackleKind.Slide)
        {
            if (!Defending.StepSlide(p, Tick, _dt, Pitch)) EndSlide(p);
            return;
        }
        if (now < d.GetUpUntil)
        {
            p.Velocity = Vector3.Zero;
            p.RunSpeed = 0f;
            p.SteerVelocity = Vector3.Zero;
            d.Carry = Vector2.Zero;
            p.Jockeying = false;
            p.BlockReach = 0f; d.Blocking = false; d.BlockSetFor = 0f;
            return;
        }
        if (live) UpdateBlock(p); else { d.Blocking = false; d.BlockSetFor = 0f; p.BlockReach = 0f; }
        var legs = d.Tackle == TackleKind.Lunge ? default : it;
        if (d.Blocking) legs.Jockey = true;
        float cap = Defending.Stumbling(p, now) ? Tuning.Defending.StumbleSpeed : float.PositiveInfinity;
        // Jockeying he faces the ball carrier, or the ball when nobody has it.
        var face = Flat(_controller != null && _controller.Team != p.Team ? _controller.Position : Ball.Position);
        Movement.StepOffBall(p, legs, face, cap, _dt, now, Tuning, Pitch);
    }

    /// <summary>A push (a barge) carries a player along and dies away.</summary>
    void ApplyPush(PlayerState q)
    {
        var d = q.Def;
        if (d.Push == Vector2.Zero) { q.PushVelocity = Vector3.Zero; return; }
        var pos = q.Position + new Vector3(d.Push.X, 0f, d.Push.Y) * _dt;
        var vel = new Vector3(d.Push.X, 0f, d.Push.Y);
        Pitch.ClampPlayer(ref pos, ref vel);
        q.Position = pos;
        q.HandPosition = pos;
        float len = d.Push.Length(), step = Tuning.Defending.Barge.PushDecel * _dt;
        d.Push = len <= step ? Vector2.Zero : d.Push * ((len - step) / len);
        q.PushVelocity = new Vector3(d.Push.X, 0f, d.Push.Y);
    }

    Vector3 FootOf(PlayerState p) => p.Def.Tackle switch
    {
        TackleKind.Lunge => Defending.LungeFoot(p, Tick, Tuning),
        TackleKind.Slide => Defending.SlideFoot(p, Tuning),
        _ => p.Position,
    };

    void EndLunge(PlayerState p)
    {
        var d = p.Def;
        var s = Tuning.Defending.Stand;
        d.Tackle = TackleKind.None;
        d.TackleCooldownUntil = SimTime + s.CooldownS;
        // A lunge that missed leaves him off balance, the longer the faster he went in.
        if (!d.Won && !d.Fouled) p.StumbleUntil = SimTime + s.MissStunS.Evaluate(d.StartSpeed);
        p.TackleContactTime = float.NaN;
    }

    void EndSlide(PlayerState p)
    {
        var d = p.Def;
        var s = Tuning.Defending.Slide;
        d.Tackle = TackleKind.None;
        d.GroundedUntil = SimTime + (d.Fouled ? s.RecoveryHitS : d.Won ? s.RecoveryWonS : s.RecoveryMissedS);
        d.GetUpUntil = d.GroundedUntil + s.GetupS;
        d.TackleCooldownUntil = d.GetUpUntil;
        p.Velocity = Vector3.Zero;
    }

    /// <summary>
    /// Sets for a shot: while the attacker winds one up (or a struck ball comes at him) with the ball within block.set_range_m in
    /// front of him, his leg reach grows by the time he has been set.
    /// </summary>
    void UpdateBlock(PlayerState p)
    {
        var d = p.Def;
        var k = Tuning.Defending.Block;
        var rel = Flat(Ball.Position) - Flat(p.Position);
        float dist = rel.Length();
        // (a shot winding up counts only while the attacker can still strike the ball: it is his, or at his feet)
        bool windup = Attacker.ShotAction is { Made: false }
            && (_controller == Attacker || TouchModel.HorizontalDistance(Attacker.Position, Ball.Position) <= Tuning.Defender.TackleMaxDistanceM);
        bool coming = State == RoundState.Flight && Vector2.Dot(Flat(Ball.Velocity), -rel) > 0f;
        bool front = dist < 1e-3f || MathF.Abs(Movement.WrapAngle(MathF.Atan2(rel.Y, rel.X) - p.Facing)) < MathF.PI / 2f;
        bool set = Defending.Free(p, SimTime) && (windup || coming) && dist <= k.SetRangeM && front;
        if (!set) { d.Blocking = false; d.BlockSetFor = 0f; p.BlockReach = 0f; return; }
        d.Blocking = true;
        d.BlockSetFor += _dt;
        p.BlockReach = Defending.BlockReachAfter(p.Sheet, d.BlockSetFor, Tuning);
        if (dist > 1e-3f) p.ActionSide = Defending.SideOf(p, rel / dist);
    }

    /// <summary>
    /// A shoulder barge: on an opponent running alongside (see <see cref="Defending.BargeTarget"/>) the stronger, heavier of
    /// the two pushes the other sideways, and the one pushed stumbles (and loses the ball if he had it). At nobody, only the lean.
    /// </summary>
    void Barge(PlayerState p)
    {
        var d = p.Def;
        var k = Tuning.Defending.Barge;
        d.BargeCooldownUntil = SimTime + k.CooldownS;
        d.BargeUntil = SimTime + k.LeanS;
        if (Defending.BargeTarget(p, Teams[1 - p.Team], Tuning) is not { } o) return;
        var dir = Vector2.Normalize(Flat(o.Position) - Flat(p.Position));
        float ratio = Math.Clamp(Defending.BargeRatio(p.Sheet, o.Sheet), 1f / 3f, 3f);
        p.ActionSide = Defending.SideOf(p, dir);
        if (_defReport != null && p == Defender) _defReport.Barges++;
        _events.Add(new SimEvent(EventKind.Barge, o.Position, o.Speed, Team: p.Team, PlayerIndex: p.Index));
        if (ratio >= 1f)
        {
            o.Def.Push += dir * (k.Impulse * ratio);
            o.StumbleUntil = SimTime + Defending.StumbleSeconds(o.Sheet, Tuning);
            if (_defReport != null && p == Defender) _defReport.BargesWon++;
            if (_controller == o) { SkillMoves.Abort(o); SetController(null); _wonBy = TackleKind.None; }
        }
        else
        {
            p.Def.Push -= dir * (k.Impulse / ratio);
            p.StumbleUntil = SimTime + Defending.StumbleSeconds(p.Sheet, Tuning);
        }
    }

    /// <summary>
    /// The ball is won from a dribble (or knocked on, when it was loose): poked away along the freest direction near
    /// <paramref name="along"/>, and the player who had it stumbles and cannot touch it for a moment.
    /// </summary>
    void WinBall(PlayerState p, TackleKind kind, Vector2 along)
    {
        var v = Defending.PokeVelocity(p, along, Teams[1 - p.Team], _defRng, Tuning);
        Ball.Velocity = v;
        Ball.Spin = Ball.RollingSpin(v);
        if (_controller is { Role: PlayerRole.Outfield } loser && loser.Team != p.Team)
        {
            loser.StumbleUntil = SimTime + Defending.StumbleSeconds(loser.Sheet, Tuning);
            loser.TackledLockUntil = SimTime + Tuning.Defending.TackledLockS;
            SkillMoves.Abort(loser);
        }
        SetController(null);
        _ballPlayed = true;
        _wonBy = kind;
        p.Def.AutoCooldownUntil = SimTime + Tuning.Defending.Auto.CooldownS;
        if (p.Def.Tackle != TackleKind.None) p.Def.Won = true;
        // A lunge that has the ball is over: he is back on his feet in a moment instead of staying planted for the whole window.
        if (p.Def.Tackle == TackleKind.Lunge)
            p.Def.EndTick = Math.Min(p.Def.EndTick, Tick + Math.Max(1, (int)MathF.Round(Tuning.Defending.Stand.ActiveS * Tuning.TickRate)));
        if (_defReport != null && p == Defender)
        {
            if (kind == TackleKind.Auto) _defReport.AutoWon++;
            else if (kind == TackleKind.Lunge) _defReport.LungesWon++;
            else if (kind == TackleKind.Slide) _defReport.SlidesWon++;
        }
        _events.Add(new SimEvent(EventKind.Tackle, Ball.Position, v.Length(), Team: p.Team, PlayerIndex: p.Index));
    }

    /// <summary>True when the keeper's hands are on the ball inside his penalty area this tick: it is his, not a tackler's.</summary>
    bool KeeperHasBall() =>
        _brain.HandsSphere is { } hs && Vector3.Distance(Ball.Position, hs.Center) <= hs.Radius && Pitch.InPenaltyArea(Ball.Position, Ball.Radius);

    // ---- the ball's obstacles and what meeting them does ----

    /// <summary>
    /// The defender's obstacles in the ball's path this tick: his body and, set for a shot, his leg, against a ball coming at
    /// him (but not a loose ball at his feet slow enough for him to control); and the foot of a lunge or slide that has not met
    /// the ball yet. Null when there are none.
    /// </summary>
    IReadOnlyList<BallObstacle>? DefenderObstacles()
    {
        if (Defender is not { } p) return null;
        _obstacles.Clear();
        var k = Tuning.Defending;
        float r = Ball.Radius;
        var d = p.Def;
        var toHim = Flat(p.Position) - Flat(Ball.Position);
        bool coming = Vector2.Dot(Flat(Ball.Velocity) - Flat(p.Velocity), toHim) > 0f;
        var against = Ball.Velocity - p.Velocity;
        // A loose ball at his feet that he can bring under control is not knocked away by his body.
        bool hisToControl = State is RoundState.Live or RoundState.Strike && _controller == null && Ball.Position.Y <= TouchModel.FeetHeight(Tuning)
            && Defending.Free(p, SimTime) && Flat(against).Length() <= Tuning.Defender.ControlSpeed;
        if (coming && !hisToControl && !Defending.Down(p, SimTime))
        {
            var up = Vector3.UnitY;
            _obstacles.Add(new BallObstacle(new SweepCapsule(p.Position + up * k.BodyRadiusM, p.Position + up * MathF.Max(k.BodyRadiusM, k.BodyHeightM - k.BodyRadiusM), k.BodyRadiusM + r), Defending.BodyObstacle));
            if (p.BlockReach > k.BodyRadiusM)
                _obstacles.Add(new BallObstacle(new SweepCapsule(p.Position, p.Position, p.BlockReach + r), Defending.LegObstacle, k.Block.MaxHeightM));
        }
        if (!d.Won && !d.Fouled)
        {
            if (Defending.LungeActive(p, Tick, Tuning))
            {
                var foot = Defending.LungeFoot(p, Tick, Tuning);
                _obstacles.Add(new BallObstacle(new SweepCapsule(foot, foot, k.Stand.FootRadiusM + r), Defending.FootObstacle));
            }
            else if (d.Tackle == TackleKind.Slide)
            {
                var foot = Defending.SlideFoot(p, Tuning);
                _obstacles.Add(new BallObstacle(new SweepCapsule(foot, foot, k.Slide.BallRadiusM + r), Defending.FootObstacle));
            }
        }
        return _obstacles.Count > 0 ? _obstacles : null;
    }

    /// <summary>
    /// After the ball has moved: the defender's foot, leg or body it met (a tackle won, or a deflection), a lunge that has the
    /// ball for certain, and then a lunge or slide whose foot is on the attacker's body with the ball not won: a foul, which
    /// ends the round. Returns true if the round ended.
    /// </summary>
    bool ResolveDefenderContact(in BallStepResult res)
    {
        var p = Defender!;
        var d = p.Def;
        var k = Tuning.Defending;
        if (res.ObstacleHit)
        {
            bool foot = res.ObstacleId == Defending.FootObstacle;
            if (foot && State != RoundState.Flight) WinBall(p, d.Tackle, d.Dir);
            // A ball dribbled into his body or leg is his like any other inside the guarantee: taken, not bounced.
            // (through a lunge it counts as the lunge's)
            else if (!foot && State != RoundState.Flight && _controller == Attacker && (Defending.Free(p, SimTime) || d.Tackle == TackleKind.Lunge))
                WinBall(p, d.Tackle == TackleKind.Lunge ? TackleKind.Lunge : TackleKind.Auto, new Vector2(MathF.Cos(p.Facing), MathF.Sin(p.Facing)));
            else
            {
                Vector3 n = foot ? Ball.Position - FootOf(p) : new Vector3(Ball.Position.X - p.Position.X, 0f, Ball.Position.Z - p.Position.Z);
                n = n.LengthSquared() > 1e-10f ? Vector3.Normalize(n) : -Vector3.Normalize(Ball.Velocity);
                // (off his body or leg the ball's speed is taken against his own movement)
                var own = foot ? Vector3.Zero : p.Velocity;
                if (Defending.Deflection(Ball.Velocity - own, n, Tuning) is { } outV)
                {
                    float speed = Ball.Velocity.Length();
                    Ball.Velocity = outV + own;
                    Ball.Spin = Vector3.Zero;
                    Ball.Position += n * 1e-3f;
                    _events.Add(new SimEvent(EventKind.Block, res.ObstaclePoint, speed, Team: p.Team, PlayerIndex: p.Index));
                    // (a ball he had already won that bounces off him again is still the tackle's)
                    if (State == RoundState.Flight || _controller != null) _wonBy = TackleKind.None;
                    if (State == RoundState.Flight) { _defReport!.Blocks++; State = RoundState.Live; }
                    if (foot) d.Won = true;
                    if (_controller != null) SetController(null);
                    _ballPlayed = true;
                }
            }
        }
        if (d.Tackle == TackleKind.None || d.Won || d.Fouled || State is not (RoundState.Live or RoundState.Strike or RoundState.Flight)) return false;
        bool lunge = d.Tackle == TackleKind.Lunge;
        if (lunge && !Defending.LungeActive(p, Tick, Tuning)) return false;
        var at = FootOf(p);
        if (lunge && State != RoundState.Flight && Ball.Position.Y < k.Stand.CertainHeightM
            && TouchModel.HorizontalDistance(at, Ball.Position) <= k.Stand.CertainM && !KeeperHasBall())
        {
            WinBall(p, TackleKind.Lunge, d.Dir);
            return false;
        }
        if (TouchModel.HorizontalDistance(at, Attacker.Position) <= (lunge ? k.BodyRadiusM : k.Slide.OpponentRadiusM))
        {
            d.Fouled = true;
            _defReport!.Fouls++;
            _events.Add(new SimEvent(EventKind.Foul, Attacker.Position, p.Speed, Team: p.Team, PlayerIndex: p.Index));
            EndRound(Outcome.Foul);
            return true;
        }
        return false;
    }

    // ---- what the others read of him ----

    /// <summary>
    /// True when the defender covers the ball for the keeper: on his feet, within keeper_yield_cone_deg of the keeper's line to
    /// the ball, nearer to it than the keeper, and not beaten (no further from the goal centre than the ball by more than
    /// overshoot_m).
    /// </summary>
    internal bool DefenderCovers()
    {
        if (Defender is not { } p || Defending.Down(p, SimTime) || Defending.Stumbling(p, SimTime)) return false;
        var kd = Tuning.Defender;
        Vector2 kp = Flat(Keeper.Position), b = Flat(Ball.Position), dp = Flat(p.Position);
        Vector2 toBall = b - kp, toDef = dp - kp;
        if (toBall.LengthSquared() < 1e-6f || toDef.LengthSquared() < 1e-6f) return false;
        float off = MathF.Abs(Movement.WrapAngle(MathF.Atan2(toDef.Y, toDef.X) - MathF.Atan2(toBall.Y, toBall.X))) * 180f / MathF.PI;
        if (off > kd.KeeperYieldConeDeg) return false;
        if (Vector2.Distance(dp, b) >= toBall.Length()) return false;
        var goal = new Vector2(Pitch.GoalLineX, 0f);
        return Vector2.Distance(dp, goal) <= Vector2.Distance(b, goal) + kd.OvershootM;
    }

    /// <summary>
    /// The attacker's regain of a loose ball with a defender about: none while he stumbles, and a race (normal foot reach)
    /// rather than a free one when the defender is as close to the ball as a challenging keeper would be.
    /// </summary>
    RegainMode RegainAgainstDefender(PlayerState p, RegainMode mode)
    {
        if (mode == RegainMode.None || Defending.Stumbling(p, SimTime)) return RegainMode.None;
        var d = Defender!;
        if (mode == RegainMode.Free && !Defending.Down(d, SimTime) && Regain.ContestedBy(p, Ball, d, Movement.TopSpeed(d.Sheet, Tuning), Tuning))
            return RegainMode.Contested;
        return mode;
    }

    /// <summary>
    /// True when the attacker may not take a loose ball this tick because the defender, on his feet with the ball within his
    /// control distance, is nearer to it. Equally near, a coin from the defending stream decides, once per loose ball.
    /// </summary>
    bool DefenderDeniesTouch(PlayerState p)
    {
        if (Defender is not { } d || _controller != null || !Defending.Free(d, SimTime)) return false;
        float dd = TouchModel.HorizontalDistance(d.Position, Ball.Position);
        if (dd > Tuning.Defender.ControlDistanceM) return false;
        float da = TouchModel.HorizontalDistance(p.Position, Ball.Position);
        if (MathF.Abs(dd - da) <= LooseTieM) return _looseTie ??= _defRng.NextFloat() < 0.5f;
        return dd < da;
    }

    /// <summary>Two players whose distances to a loose ball differ by no more than this are equally near it, metres.</summary>
    const float LooseTieM = 0.05f;

    /// <summary>What the defender is doing, for the view: the action under way, else what his bot is at.</summary>
    void PublishDefender(in TickIntent it)
    {
        var p = Defender!;
        var d = p.Def;
        float now = SimTime;
        var mode = _bot?.Mode ?? DefenderMode.Hold;
        var action = d.Tackle == TackleKind.Lunge ? DefenderAction.Lunge
            : d.Tackle == TackleKind.Slide ? DefenderAction.Slide
            : now < d.GroundedUntil ? DefenderAction.Grounded
            : now < d.GetUpUntil ? DefenderAction.GettingUp
            : Defending.Stumbling(p, now) ? DefenderAction.Stumble
            : now < d.BargeUntil ? DefenderAction.Barge
            : d.Blocking ? DefenderAction.Block
            : mode == DefenderMode.Collect ? DefenderAction.Collect
            : mode == DefenderMode.Press ? DefenderAction.Press
            : !it.Jockey && it.Sprint > 0f ? DefenderAction.Recover
            : p.Jockeying && p.Speed > 0.5f ? DefenderAction.Jockey
            : DefenderAction.Hold;
        if (action != p.DefenderAction) { p.DefenderAction = action; p.DefenderActionTime = 0f; }
        else p.DefenderActionTime += _dt;
        if (action is DefenderAction.Hold or DefenderAction.Recover or DefenderAction.Jockey or DefenderAction.Press or DefenderAction.Collect
            or DefenderAction.Grounded or DefenderAction.GettingUp or DefenderAction.Stumble)
            p.ActionSide = SkillSide.None;
    }

    DefenderReport? FinishDefenderReport()
    {
        if (_defReport != null && _bot != null) _defReport.TimeActive = _bot.TimeActive;
        return _defReport;
    }
}
