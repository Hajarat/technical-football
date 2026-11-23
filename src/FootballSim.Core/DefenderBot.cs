using System.Numerics;

namespace FootballSim.Core;

/// <summary>What the defender bot is at: holding his spot, pressing the attacker, or going for a loose ball.</summary>
public enum DefenderMode { Hold, Press, Collect }

/// <summary>
/// The defender's bot. Each tick it reads the world and writes a <see cref="TickIntent"/>: a stick, sprint, jockey, and the
/// tackle, slide and barge buttons. It never moves the defender itself, so a human defender goes through the same action code.
/// <para>
/// Hold: he stands off the ball on its line to the goal centre (closer the nearer the goal), a side offset drawn once per spell,
/// sprinting there from far off and jockeying when near. Once he has held for his awareness time and is goal-side of the
/// attacker by the margin, he presses: he runs at where the attacker will be shortly (the attacker's feet if that point is
/// behind him), bending in from the side close up and steering off after a bump. With the ball between them and the attacker
/// within the tackle distance he presses the tackle button, a lunge or, by a roll made once per press, a slide; running
/// alongside he may barge. After his active time, or once the attacker is past him, he drops back to hold. A loose ball he
/// goes for, at his own meeting point with it.
/// </para>
/// The numbers are in tuning.defender. His draws come from his own generator, so nothing else's draws move.
/// </summary>
public sealed class DefenderBot
{
    readonly SimRng _rng;
    float _holdFor, _activeFor;
    float _offset;
    bool _offsetDrawn;
    bool _slideRolled, _slideThisSpell;
    float _buttonLeft;
    int _button;                // 0 none, 1 tackle, 2 slide
    float _bargeRollIn, _bargeLeft;

    /// <summary>What he is at this tick.</summary>
    public DefenderMode Mode { get; private set; }
    /// <summary>Seconds spent pressing this round.</summary>
    public float TimeActive { get; private set; }
    /// <summary>The spot he holds (world X, Z), as last worked out.</summary>
    public Vector2 HoldSpot { get; private set; }

    internal DefenderBot(ulong seed)
    {
        _rng = new SimRng(seed);
        // A round starts in the middle of play: his first hold counts as served, so his first press waits only for the
        // goal-side margin. Every later hold runs its awareness time.
        _holdFor = float.PositiveInfinity;
    }

    static Vector2 Flat(Vector3 v) => new(v.X, v.Z);

    /// <summary>The spot a defender holds for a ball at <paramref name="ball"/>: hold_distance_m off it toward the goal centre, <paramref name="offset"/> to the side.</summary>
    public static Vector2 HoldSpotFor(Vector2 ball, float offset, Pitch pitch, Tuning t)
    {
        var goal = new Vector2(pitch.GoalLineX, 0f);
        float bd = Vector2.Distance(ball, goal);
        var dir = bd > 1e-3f ? (goal - ball) / bd : new Vector2(1f, 0f);
        float hold = MathF.Min(t.Defender.HoldDistanceM.Evaluate(bd), MathF.Max(0f, bd - 1f));
        return ball + dir * hold + new Vector2(-dir.Y, dir.X) * offset;
    }

    /// <summary>True when the ball is between attacker and defender: the direction attacker to ball within tackle_max_angle_deg of the direction ball to defender.</summary>
    public static bool BallBetween(Vector2 attacker, Vector2 ball, Vector2 defender, Tuning t)
    {
        Vector2 ab = ball - attacker, bd = defender - ball;
        if (ab.LengthSquared() < 1e-8f || bd.LengthSquared() < 1e-8f) return true;
        float off = MathF.Abs(Movement.WrapAngle(MathF.Atan2(ab.Y, ab.X) - MathF.Atan2(bd.Y, bd.X)));
        return off * 180f / MathF.PI <= t.Defender.TackleMaxAngleDeg;
    }

    /// <summary>The defender's intent for this tick.</summary>
    internal TickIntent Next(World w)
    {
        var d = w.Defender!;
        var a = w.Attacker;
        var t = w.Tuning;
        var k = t.Defender;
        float dt = t.Dt, now = w.SimTime;
        var goal = new Vector2(w.Pitch.GoalLineX, 0f);
        Vector2 dp = Flat(d.Position), ap = Flat(a.Position), bp = Flat(w.Ball.Position);
        float dSelf = Vector2.Distance(dp, goal), dOpp = Vector2.Distance(ap, goal);
        bool hasBall = w.BallController == a;
        bool loose = w.State is RoundState.Live or RoundState.Strike && w.BallController == null && w.BallPlayed;
        int awareness = d.Sheet.Awareness;

        if (loose)
        {
            Mode = DefenderMode.Collect;
            _buttonLeft = 0f;
            return Collect(w, d, dp, bp);
        }
        if (Mode == DefenderMode.Collect) EnterHold();

        if (Mode == DefenderMode.Hold)
        {
            _holdFor += dt;
            if (hasBall && dOpp >= dSelf + k.GoalSideMarginM && _holdFor >= k.ToActiveAfterS.Evaluate(awareness) && Defending.Free(d, now))
            {
                Mode = DefenderMode.Press;
                _activeFor = 0f;
                _slideRolled = false;
            }
        }
        else
        {
            _activeFor += dt;
            TimeActive += dt;
            if (!hasBall || _activeFor >= k.ActiveMaxS.Evaluate(awareness) || dSelf > dOpp + k.OvershootM) EnterHold();
        }
        var intent = Mode == DefenderMode.Press ? Press(w, d, a, dp, ap, bp) : Hold(w, d, dp, bp);
        intent.Barge = BargeButton(w, d);
        return intent;
    }

    void EnterHold()
    {
        Mode = DefenderMode.Hold;
        _holdFor = 0f;
        _offsetDrawn = false;
        _buttonLeft = 0f;
    }

    TickIntent Hold(World w, PlayerState d, Vector2 dp, Vector2 bp)
    {
        var t = w.Tuning;
        var k = t.Defender;
        if (!_offsetDrawn)
        {
            float lo = k.HoldOffsetMinM.Evaluate(d.Sheet.Awareness), hi = k.HoldOffsetMaxM.Evaluate(d.Sheet.Awareness);
            _offset = _rng.Uniform(MathF.Min(lo, hi), MathF.Max(lo, hi)) * (_rng.NextFloat() < 0.5f ? -1f : 1f);
            _offsetDrawn = true;
        }
        var spot = HoldSpotFor(bp, _offset, w.Pitch, t);
        // A shot winding up: he steps onto the ball's line to the goal centre, at the depth he is at, to get in its way.
        if (w.Attacker.ShotAction is { Made: false } && w.BallController == w.Attacker)
        {
            var goal = new Vector2(w.Pitch.GoalLineX, 0f);
            var line = goal - bp;
            float len2 = line.LengthSquared();
            float u = len2 > 1e-6f ? Vector2.Dot(dp - bp, line) / len2 : 0f;
            if (u > 0f && u < 1f) spot = bp + line * u;
        }
        var spot3 = new Vector3(spot.X, 0f, spot.Y);
        var none = Vector3.Zero;
        w.Pitch.ClampPlayer(ref spot3, ref none);
        spot = Flat(spot3);
        HoldSpot = spot;
        var to = spot - dp;
        float dist = to.Length();
        var it = new TickIntent();
        if (dist < 1e-4f) { it.Jockey = true; return it; }
        var dir = to / dist;
        if (dist > k.PassiveSprintDistanceM)
        {
            it.MoveX = dir.X; it.MoveZ = dir.Y; it.Sprint = 1f;
            return it;
        }
        // Near his spot he jockeys onto it, easing in so he arrives rather than runs through.
        it.Jockey = true;
        float mag = MathF.Min(1f, dist * ArriveGain / MathF.Max(0.1f, Movement.JockeyTopSpeed(d.Sheet, 0f, t)));
        it.MoveX = dir.X * mag; it.MoveZ = dir.Y * mag;
        return it;
    }

    /// <summary>He aims to arrive: the speed asked for is the distance left times this, per second.</summary>
    const float ArriveGain = 3f;

    TickIntent Press(World w, PlayerState d, PlayerState a, Vector2 dp, Vector2 ap, Vector2 bp)
    {
        var t = w.Tuning;
        var k = t.Defender;
        float dt = t.Dt, now = w.SimTime;
        var facing = new Vector2(MathF.Cos(d.Facing), MathF.Sin(d.Facing));
        // Where the attacker will be shortly; his feet if that point is behind the defender.
        var target = ap + Flat(a.Velocity) * k.ExtrapolateS;
        if (Vector2.Dot(target - dp, facing) < 0f) target = ap;
        // A ball he has let get away from his feet is there to be won: the defender goes for the ball itself.
        if (Vector2.Distance(bp, ap) > k.TackleMaxDistanceM) target = bp;
        var to = target - dp;
        float dist = to.Length();
        var dir = dist > 1e-4f ? to / dist : facing;
        float da = Vector2.Distance(ap, dp);

        // Close up and running straight on, he bends in from the side the ball is on.
        if (da < k.TackleMaxDistanceM && da > 1e-4f && Vector2.Dot(facing, dir) > k.BendDot)
        {
            float weight = Math.Clamp(k.BendWeightBase + k.BendWeightGain * (1f - da / k.TackleMaxDistanceM), 0f, 1f);
            var toA = (ap - dp) / da;
            var rel = bp - dp;
            float cross = toA.X * rel.Y - toA.Y * rel.X;
            float sign = MathF.Abs(cross) > 1e-3f ? MathF.Sign(cross) : MathF.Sign(_offset == 0f ? 1f : _offset);
            var side = new Vector2(-toA.Y, toA.X) * sign;
            var bent = dir * (1f - weight) + side * weight;
            if (bent.LengthSquared() > 1e-8f) dir = Vector2.Normalize(bent);
        }
        dir = SteerOffBump(w, d, dir);
        // Far off he sprints; close up he comes in at the jockey pace, facing the attacker, so a cut does not leave him behind.
        var it = new TickIntent { MoveX = dir.X, MoveZ = dir.Y, Sprint = dist > k.PressSprintDistanceM ? 1f : 0f, Jockey = da <= k.PressJockeyDistanceM };
        if (it.Jockey) it.Sprint = k.PressJockeySprint;

        // The buttons: one held for tackle_hold_s at a time.
        if (_buttonLeft > 0f) _buttonLeft -= dt;
        else
        {
            _button = 0;
            if (Defending.CanTackle(d, now) && da <= k.TackleMaxDistanceM && BallBetween(ap, bp, dp, t))
            {
                if (!_slideRolled)
                {
                    _slideRolled = true;
                    _slideThisSpell = _rng.NextFloat() < k.SlidePreference.ForRating(d.Sheet.SlideTackle);
                }
                _button = _slideThisSpell ? 2 : 1;
                _buttonLeft = k.TackleHoldS;
            }
        }
        it.Tackle = _button == 1;
        it.Slide = _button == 2;
        return it;
    }

    /// <summary>
    /// The barge, in any mode but collecting: every barge_roll_s, with an opponent running alongside (see
    /// <see cref="Defending.BargeTarget"/>), a roll against barge_preference presses the button for tackle_hold_s.
    /// </summary>
    bool BargeButton(World w, PlayerState d)
    {
        var t = w.Tuning;
        var k = t.Defender;
        float now = w.SimTime;
        if (_bargeLeft > 0f) { _bargeLeft -= t.Dt; return true; }
        _bargeRollIn -= t.Dt;
        if (_bargeRollIn > 0f) return false;
        _bargeRollIn = k.BargeRollS;
        if (_buttonLeft > 0f || !Defending.Free(d, now) || now < d.Def.BargeCooldownUntil || Defending.BargeTarget(d, w.Teams[0], t) == null
            || _rng.NextFloat() >= k.BargePreference.ForRating(d.Sheet.Strength)) return false;
        _bargeLeft = k.TackleHoldS;
        return true;
    }

    /// <summary>Just after a body contact he steers at least bump_steer_deg off the line to the player he bumped, on the side he already leans to.</summary>
    static Vector2 SteerOffBump(World w, PlayerState d, Vector2 dir)
    {
        var k = w.Tuning.Defender;
        if (w.Tick - d.Def.BumpTick > (int)MathF.Round(k.BumpSteerS * w.Tuning.TickRate)) return dir;
        var bump = d.Def.BumpDir;
        float off = Movement.WrapAngle(MathF.Atan2(dir.Y, dir.X) - MathF.Atan2(bump.Y, bump.X));
        float min = k.BumpSteerDeg * MathF.PI / 180f;
        if (MathF.Abs(off) >= min) return dir;
        float yaw = MathF.Atan2(bump.Y, bump.X) + (off < 0f ? -min : min);
        return new Vector2(MathF.Cos(yaw), MathF.Sin(yaw));
    }

    /// <summary>
    /// A loose ball: he runs to his meeting with it (his team's claim, see <see cref="LockOn"/>; without one, to where the ball
    /// will be shortly), at a velocity that closes on it and matches the ball's own there, so he arrives able to control it.
    /// </summary>
    static TickIntent Collect(World w, PlayerState d, Vector2 dp, Vector2 bp)
    {
        var t = w.Tuning;
        // His claim on the ball: his own meeting with it. Without one, where the ball will be shortly.
        bool claim = w.HoldsClaim(d) && d.LockOnTick >= 0;
        var bv = w.Ball.Velocity;
        var aim = w.Ball.Position + bv * CollectLead;
        var it = claim ? LockOn.CollectLegs(d, d.LockOnPoint, d.LockOnBallVelocity, t) : LockOn.CollectLegs(d, aim, bv, t);
        float m = it.Move.Length();
        if (m > 1e-3f)
        {
            var dir = SteerOffBump(w, d, it.Move / m) * m;
            it.MoveX = dir.X; it.MoveZ = dir.Y;
        }
        return it;
    }

    /// <summary>Without a claim he aims at where the ball will be this long on, seconds.</summary>
    const float CollectLead = 0.25f;
}
