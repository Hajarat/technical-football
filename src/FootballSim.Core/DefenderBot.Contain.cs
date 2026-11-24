using System.Numerics;

namespace FootballSim.Core;

// The containing bot (tuning defender.brain = contain). Each tick, on his feet:
//   Drop     the carrier is outside his gap: he goes to his spot on the ball's line to the goal centre at the jockey pace,
//            never further out than engage_line_m, running flat out only to a spot well behind him;
//   Contain  the carrier is inside his gap: he backs off at the carrier's pace along the line, facing him, correcting toward
//            his spot by gap_gain;
//   RunWith  the speed that asks for is beyond his jockey: he turns and runs on the carrier's goal-side shoulder;
//   Win      a loose touch he gets to first, a slow carrier, or his last line: he goes for the ball and presses a button;
//   Recover  beaten: he runs to the point on the carrier's way to goal that he reaches first;
//   Collect  a loose ball he goes for, as the legacy bot does.
// Stumbling, stunned or on the ground he presses no button and keeps his mode until he is free again.
public sealed partial class DefenderBot
{
    enum WinTrigger { None, Loose, Slow, LastLine }

    WinTrigger _trigger;
    bool _winPressed;           // a tackle button was pressed in this Win spell
    float _fastFor;             // seconds the speed containing asks for has been beyond the run-with threshold
    float _slowFor;             // seconds the carrier has been under press_speed
    Vector2 _winPoint;          // where the loose ball will be met ...
    float _winTime;             // ... this many seconds from now
    Ball? _scratch;             // the ball rolled on for the loose-touch race

    /// <summary>Seconds spent containing and running with the carrier this round (the containing bot).</summary>
    public float TimeContain { get; private set; }
    public float TimeRunWith { get; private set; }
    /// <summary>Times the containing bot went for the ball on a loose touch, a slow carrier and on his last line.</summary>
    public int WinsLoose { get; private set; }
    public int WinsSlow { get; private set; }
    public int WinsLastLine { get; private set; }
    /// <summary>The gap he keeps, metres, as last worked out (the containing bot).</summary>
    public float Gap { get; private set; }

    /// <summary>Once containing, he drops again only with the carrier this much beyond the gap, metres (so he does not flicker).</summary>
    const float ContainExitM = 0.5f;
    /// <summary>Within this of last_line_m (and further from goal than it is less) he is on his last line, metres.</summary>
    const float LastLineTolM = 0.3f;
    /// <summary>The shade grows from nothing with the ball on the long centre line to shade_m this far off it, metres.</summary>
    const float ShadeFullM = 1f;
    /// <summary>The loose-touch race and the cut-off search step the ball and the path this finely, seconds and metres.</summary>
    const float RaceStep = 1f / 60f, CutStep = 0.25f;

    /// <summary>
    /// The gap the containing bot keeps, metres: contain_gap_m by jockeying, plus gap_pace_s for each m/s the carrier runs faster
    /// than his sprint-jockey pace, no more than gap_max_m.
    /// </summary>
    public static float GapFor(PlayerSheet defender, float carrierSpeed, Tuning t)
    {
        var k = t.Defender;
        float fast = Movement.JockeyTopSpeed(defender, 1f, t);
        return MathF.Min(k.GapMaxM, k.ContainGapM.ForRating(defender.Jockeying) + k.GapPaceS * MathF.Max(0f, carrierSpeed - fast));
    }

    /// <summary>
    /// The containing bot's spot: on the line from the ball at <paramref name="ball"/> to the goal centre, <paramref name="gap"/>
    /// goal-side of the carrier (<paramref name="carrierFromGoal"/> metres from the goal centre), no further from the goal centre
    /// than engage_line_m while the carrier is outside the gap (<paramref name="outside"/>) and no nearer than last_line_m; then
    /// shade_m square to the line toward the arena's long centre line (less with the ball within a metre of it), and inside the
    /// boards. With the ball on the goal centre the line runs straight out of the goal.
    /// </summary>
    public static Vector2 SpotFor(Vector2 ball, float carrierFromGoal, float gap, bool outside, Pitch pitch, Tuning t)
    {
        var k = t.Defender;
        var goal = new Vector2(pitch.GoalLineX, 0f);
        float bd = Vector2.Distance(ball, goal);
        var u = bd > 1e-3f ? (goal - ball) / bd : new Vector2(1f, 0f);
        float r = carrierFromGoal - gap;
        if (outside) r = MathF.Min(r, k.EngageLineM);
        r = MathF.Max(r, k.LastLineM);
        var spot = goal - u * r;
        var n = new Vector2(-u.Y, u.X);
        float toward = -Math.Clamp(ball.Y / ShadeFullM, -1f, 1f);
        spot += n * (k.ShadeM * toward * (n.Y >= 0f ? 1f : -1f));
        var s3 = new Vector3(spot.X, 0f, spot.Y);
        var none = Vector3.Zero;
        pitch.ClampPlayer(ref s3, ref none);
        return Flat(s3);
    }

    /// <summary>
    /// Beaten, the point he runs to: the nearest point to the carrier on the carrier-to-goal-centre segment that he reaches no
    /// later than the carrier does, each at the given speed, never nearer the goal centre than <paramref name="minGoal"/>; with
    /// none, the point <paramref name="minGoal"/> out on that segment.
    /// </summary>
    public static Vector2 CutOffPoint(Vector2 carrier, float carrierSpeed, Vector2 self, float selfSpeed, Vector2 goal, float minGoal)
    {
        float len = Vector2.Distance(carrier, goal);
        var ug = len > 1e-3f ? (goal - carrier) / len : new Vector2(1f, 0f);
        float sMax = MathF.Max(0f, len - minGoal);
        float vc = MathF.Max(carrierSpeed, 0.5f), vs = MathF.Max(selfSpeed, 0.5f);
        for (float s = 0f; s < sMax; s += CutStep)
        {
            var p = carrier + ug * s;
            if (Vector2.Distance(p, self) / vs <= s / vc) return p;
        }
        return len > minGoal ? carrier + ug * sMax : goal - ug * minGoal;
    }

    /// <summary>The containing bot's intent for this tick.</summary>
    TickIntent NextContain(World w)
    {
        var d = w.Defender!;
        var a = w.Attacker;
        var t = w.Tuning;
        var k = t.Defender;
        float dt = t.Dt, now = w.SimTime;
        var goal = new Vector2(w.Pitch.GoalLineX, 0f);
        Vector2 dp = Flat(d.Position), ap = Flat(a.Position), bp = Flat(w.Ball.Position);
        bool hasBall = w.BallController == a;
        bool loose = w.State is RoundState.Live or RoundState.Strike && w.BallController == null && w.BallPlayed;
        bool free = Defending.Free(d, now);
        if (_buttonLeft > 0f) _buttonLeft -= dt;
        if (!free) { _buttonLeft = 0f; _button = 0; }

        if (loose && w.DefenderGoesForBall)
        {
            if (free) Mode = DefenderMode.Collect;
            _buttonLeft = 0f; _button = 0;
            Count(dt);
            return Collect(w, d, dp, bp);
        }
        // Against a team, a loose ball he is not first to (a pass on its way): he plays on where it will be met.
        if (loose)
        {
            var anchor = w.DefenderAnchor;
            bp = new Vector2(anchor.X, anchor.Z);
            if (free) { _buttonLeft = 0f; _button = 0; }
        }
        var cp = hasBall ? ap : bp;
        var vc = hasBall ? Flat(a.Velocity) : Vector2.Zero;
        float speedC = vc.Length();
        float bd = Vector2.Distance(bp, goal);
        var u = bd > 1e-3f ? (goal - bp) / bd : new Vector2(1f, 0f);
        float dSelf = Vector2.Distance(dp, goal), dOpp = Vector2.Distance(cp, goal);
        float slowJ = Movement.JockeyTopSpeed(d.Sheet, 0f, t), fastJ = Movement.JockeyTopSpeed(d.Sheet, 1f, t);
        float gap = GapFor(d.Sheet, speedC, t);
        Gap = gap;
        // (inside the gap: the carrier no more than the gap further from the goal centre than he is)
        bool inside = dOpp - dSelf <= gap + (Mode is DefenderMode.Contain or DefenderMode.RunWith ? ContainExitM : 0f);
        _slowFor = hasBall && speedC < k.PressSpeed ? _slowFor + dt : 0f;
        bool beaten = hasBall && dSelf > dOpp + k.OvershootM;
        float margin = k.WinMarginS.Evaluate(d.Sheet.Awareness);
        var spotIn = SpotFor(bp, dOpp, gap, false, w.Pitch, t);
        // (Contain's velocity: the carrier's along the line, plus the gain toward his spot; Run with is left on its speed toward goal)
        var want = u * Vector2.Dot(vc, u) + (spotIn - dp) * k.GapGain;

        if (free)
        {
            if (Mode == DefenderMode.Collect) Mode = DefenderMode.Drop;
            // A Win spell ends once its button's action is over, the ball has gone, or its trigger no longer holds.
            if (Mode == DefenderMode.Win)
            {
                bool on = hasBall && !_winPressed && _trigger switch
                {
                    WinTrigger.Loose => LooseRace(w, d, a, 0f, out _winPoint),
                    WinTrigger.Slow => speedC <= k.PressSpeed && !beaten,
                    WinTrigger.LastLine => dSelf <= k.LastLineM + LastLineTolM && inside && !beaten,
                    _ => false,
                };
                if (!on) LeaveWin(inside);
            }
            if (Mode != DefenderMode.Win && hasBall)
            {
                if (LooseRace(w, d, a, margin, out _winPoint)) EnterWin(WinTrigger.Loose);
                else if (!beaten && _slowFor >= k.PressAfterS) EnterWin(WinTrigger.Slow);
                else if (!beaten && inside && dSelf <= k.LastLineM + LastLineTolM && bd - gap <= k.LastLineM) EnterWin(WinTrigger.LastLine);
            }
            if (Mode != DefenderMode.Win)
            {
                if (beaten) Mode = DefenderMode.Recover;
                else if (Mode == DefenderMode.Recover) Mode = inside ? DefenderMode.Contain : DefenderMode.Drop;
                else if (Mode == DefenderMode.RunWith && Vector2.Dot(vc, u) < slowJ) Mode = inside ? DefenderMode.Contain : DefenderMode.Drop;
                else if (Mode == DefenderMode.Drop && inside) Mode = DefenderMode.Contain;
                else if (Mode == DefenderMode.Contain && !inside) Mode = DefenderMode.Drop;
            }
        }

        TickIntent it;
        if (Mode == DefenderMode.Contain || Mode == DefenderMode.Drop)
        {
            if (w.ShotWindup && hasBall) it = StepOntoShotLine(w, d, dp, bp, goal);
            else if (Mode == DefenderMode.Drop) it = Drop(w, d, dp, SpotFor(bp, dOpp, gap, true, w.Pitch, t), dSelf, goal);
            else
            {
                HoldSpot = spotIn;
                _fastFor = want.Length() > k.RunWithFactor * fastJ ? _fastFor + dt : 0f;
                // (not while the carrier comes on toward goal slower than his jockey pace: that ends a run with at once)
                if (free && _fastFor >= k.RunWithAfterS && Vector2.Dot(vc, u) >= slowJ) { Mode = DefenderMode.RunWith; it = RunWith(w, d, dp, cp, vc, u); }
                else it = JockeyAt(want, slowJ, fastJ);
            }
        }
        else if (Mode == DefenderMode.RunWith) it = w.ShotWindup && hasBall ? StepOntoShotLine(w, d, dp, bp, goal) : RunWith(w, d, dp, cp, vc, u);
        else if (Mode == DefenderMode.Recover)
        {
            var cut = CutOffPoint(cp, Movement.TopSpeedWithBall(a.Sheet, 1f, t), dp, Movement.TopSpeed(d.Sheet, t), goal, k.RecoverMinGoalM);
            HoldSpot = cut;
            it = RunAt(w, d, dp, cut, 1f, arrive: true);
        }
        else if (Mode == DefenderMode.Win) it = Win(w, d, a, dp, ap, bp, free);
        else it = new TickIntent { Jockey = true };
        if (Mode != DefenderMode.Contain) _fastFor = 0f;

        if (free && Mode != DefenderMode.Collect) it.Barge = BargeButton(w, d, k.BargePreference);
        Count(dt);
        return it;
    }

    void Count(float dt)
    {
        if (Mode == DefenderMode.Contain) TimeContain += dt;
        else if (Mode == DefenderMode.RunWith) TimeRunWith += dt;
        else if (Mode == DefenderMode.Win) TimeActive += dt;
    }

    void EnterWin(WinTrigger trigger)
    {
        Mode = DefenderMode.Win;
        _trigger = trigger;
        _winPressed = false;
        _slideRolled = false;
        _buttonLeft = 0f; _button = 0;
        if (trigger == WinTrigger.Loose) WinsLoose++;
        else if (trigger == WinTrigger.Slow) WinsSlow++;
        else WinsLastLine++;
    }

    void LeaveWin(bool inside)
    {
        Mode = inside ? DefenderMode.Contain : DefenderMode.Drop;
        _trigger = WinTrigger.None;
        _winPressed = false;
        _slowFor = 0f;
        _buttonLeft = 0f; _button = 0;
    }

    /// <summary>
    /// The loose touch: true when his foot can be at the dribbled ball's predicted spot (the ball rolled on as it goes, up to
    /// win_horizon_s and no further than the carrier's next touch) at least <paramref name="margin"/> before that touch;
    /// <paramref name="point"/> is the first such spot.
    /// </summary>
    internal bool LooseRace(World w, PlayerState d, PlayerState a, float margin, out Vector2 point)
    {
        point = default;
        _winTime = 0f;
        var t = w.Tuning;
        float tAtt = a.NextTouchEta;
        if (w.BallController != a || !(tAtt > 0f)) return false;
        float horizon = MathF.Min(t.Defender.WinHorizonS, tAtt - margin);
        if (horizon < 0f) return false;
        var ball = w.Ball;
        Vector2 dp = Flat(d.Position), b0 = Flat(ball.Position);
        float vmax = Movement.TopSpeed(d.Sheet, t), accel = MathF.Max(0.5f, Movement.AccelCap(d.Sheet, 0.5f * vmax, 1f, t));
        float reach = Defending.LungeReach(d, ball.Position, t);
        // Quick rejection: flat out from now he cannot get to anywhere the ball can be by then.
        if (Vector2.Distance(dp, b0) - reach - Flat(ball.Velocity).Length() * horizon > vmax * horizon) return false;
        var b = _scratch ??= new Ball(t.Ball);
        b.CopyFrom(ball);
        var vel = Flat(d.Velocity);
        float certain = t.Defending.Stand.CertainHeightM;
        for (float tb = 0f; tb <= horizon + 1e-4f; tb += RaceStep)
        {
            if (tb > 0f) b.Step(RaceStep, null, null);
            if (b.Position.Y > certain) continue;
            var p = Flat(b.Position);
            var to = p - dp;
            float dist = to.Length();
            float v0 = dist > 1e-4f ? Vector2.Dot(vel, to / dist) : 0f;
            if (TravelTime(dist - reach, v0, accel, vmax) <= tb) { point = p; _winTime = tb; return true; }
        }
        return false;
    }

    /// <summary>Seconds to cover <paramref name="distance"/> from <paramref name="v0"/> at <paramref name="accel"/> up to <paramref name="vmax"/>.</summary>
    static float TravelTime(float distance, float v0, float accel, float vmax)
    {
        if (distance <= 0f) return 0f;
        // (moving away, he first has to stop: the time that takes, and the ground lost, are added)
        if (v0 < 0f) return -v0 / accel + TravelTime(distance + 0.5f * v0 * v0 / accel, 0f, accel, vmax);
        v0 = MathF.Min(v0, vmax);
        float tAcc = (vmax - v0) / accel, dAcc = 0.5f * (v0 + vmax) * tAcc;
        if (distance <= dAcc) return (-v0 + MathF.Sqrt(v0 * v0 + 2f * accel * distance)) / accel;
        return tAcc + (distance - dAcc) / vmax;
    }

    /// <summary>Drop: to his spot at the jockey pace, facing the carrier; flat out only to a spot far off and nearer the goal than he is.</summary>
    TickIntent Drop(World w, PlayerState d, Vector2 dp, Vector2 spot, float dSelf, Vector2 goal)
    {
        var t = w.Tuning;
        HoldSpot = spot;
        var to = spot - dp;
        float dist = to.Length();
        var it = new TickIntent { Jockey = true };
        if (dist < 1e-4f) return it;
        var dir = to / dist;
        bool goalward = Vector2.Distance(spot, goal) < dSelf;
        if (goalward && dist > t.Defender.DropSprintDistanceM) return RunAt(w, d, dp, spot, 1f, arrive: true);
        float slow = Movement.JockeyTopSpeed(d.Sheet, 0f, t), fast = Movement.JockeyTopSpeed(d.Sheet, 1f, t);
        // (eased in no faster than he can brake from at jockey_decel, so he stops on his spot)
        float need = MathF.Min(dist * ArriveGain, MathF.Sqrt(2f * t.Defending.JockeyDecel * dist));
        // (the sprint-jockey pace only toward his goal: never a sprint up the pitch)
        it.Sprint = goalward ? Math.Clamp((need - slow) / MathF.Max(0.1f, fast - slow), 0f, 1f) : 0f;
        float top = slow + (fast - slow) * it.Sprint;
        float mag = MathF.Min(1f, need / MathF.Max(0.1f, top));
        it.MoveX = dir.X * mag; it.MoveZ = dir.Y * mag;
        return it;
    }

    /// <summary>A jockey at the velocity <paramref name="want"/>: the sprint input only as far as the pace needs it.</summary>
    static TickIntent JockeyAt(Vector2 want, float slow, float fast)
    {
        float s = want.Length();
        var it = new TickIntent { Jockey = true };
        if (s < 1e-4f) return it;
        it.Sprint = Math.Clamp((s - slow) / MathF.Max(0.1f, fast - slow), 0f, 1f);
        float top = slow + (fast - slow) * it.Sprint;
        var m = want / s * MathF.Min(1f, s / MathF.Max(0.1f, top));
        it.MoveX = m.X; it.MoveZ = m.Y;
        return it;
    }

    /// <summary>Run with: flat out (no jockey) toward run_with_inside_m goal-side of the carrier on the line, matching his velocity.</summary>
    TickIntent RunWith(World w, PlayerState d, Vector2 dp, Vector2 cp, Vector2 vc, Vector2 u)
    {
        var t = w.Tuning;
        var target = cp + u * t.Defender.RunWithInsideM;
        HoldSpot = target;
        var want = vc + (target - dp) * t.Defender.GapGain;
        float s = want.Length();
        if (s < 1e-4f) return new TickIntent { Jockey = true };
        var dir = SteerOffBump(w, d, want / s);
        float mag = MathF.Min(1f, s / MathF.Max(0.1f, Movement.TopSpeed(d.Sheet, t)));
        return new TickIntent { MoveX = dir.X * mag, MoveZ = dir.Y * mag, Sprint = 1f };
    }

    /// <summary>
    /// Runs (no jockey) at <paramref name="target"/> with the sprint input given, steering off a bump; with
    /// <paramref name="arrive"/> he eases in so he stops there rather than runs through.
    /// </summary>
    static TickIntent RunAt(World w, PlayerState d, Vector2 dp, Vector2 target, float sprint, bool arrive = false)
    {
        var to = target - dp;
        float dist = to.Length();
        if (dist < 1e-3f) return new TickIntent { Jockey = true };
        var dir = SteerOffBump(w, d, to / dist);
        if (arrive) dir *= MathF.Min(1f, dist * ArriveGain / MathF.Max(0.1f, Movement.TopSpeed(d.Sheet, w.Tuning)));
        return new TickIntent { MoveX = dir.X, MoveZ = dir.Y, Sprint = sprint };
    }

    /// <summary>
    /// A shot winding up: he steps onto the ball's line to the goal centre, at the depth he is at, jockeying (the sprint-jockey
    /// pace as far as the distance needs it).
    /// </summary>
    TickIntent StepOntoShotLine(World w, PlayerState d, Vector2 dp, Vector2 bp, Vector2 goal)
    {
        var t = w.Tuning;
        var line = goal - bp;
        float len2 = line.LengthSquared();
        float uu = len2 > 1e-6f ? Vector2.Dot(dp - bp, line) / len2 : 0f;
        var spot = uu > 0f && uu < 1f ? bp + line * uu : dp;
        HoldSpot = spot;
        var to = spot - dp;
        float dist = to.Length();
        var it = new TickIntent { Jockey = true };
        if (dist < 1e-4f) return it;
        float slowJ = Movement.JockeyTopSpeed(d.Sheet, 0f, t), fastJ = Movement.JockeyTopSpeed(d.Sheet, 1f, t);
        it.Sprint = Math.Clamp((dist * ArriveGain - slowJ) / MathF.Max(0.1f, fastJ - slowJ), 0f, 1f);
        float mag = MathF.Min(1f, dist * ArriveGain / MathF.Max(0.1f, slowJ + (fastJ - slowJ) * it.Sprint));
        it.MoveX = to.X / dist * mag; it.MoveZ = to.Y / dist * mag;
        return it;
    }

    /// <summary>
    /// Win: on a loose touch he runs at the spot where he meets the ball and presses tackle once it is inside his lunge, slide
    /// beyond that inside his slide (never from behind); on a slow carrier or his last line he closes at the sprint-jockey pace
    /// and tackles with the ball between them (a slide by the roll made once per spell, never from behind). No button while the
    /// carrier's pass will be struck before a lunge could meet the ball.
    /// </summary>
    TickIntent Win(World w, PlayerState d, PlayerState a, Vector2 dp, Vector2 ap, Vector2 bp, bool free)
    {
        var t = w.Tuning;
        var k = t.Defender;
        float now = w.SimTime;
        TickIntent it;
        bool canPress = free && _buttonLeft <= 0f && !_winPressed && Defending.CanTackle(d, now) && !PassLeaving(w, a);
        if (_trigger == WinTrigger.Loose)
        {
            HoldSpot = _winPoint;
            it = RunAt(w, d, dp, _winPoint, 1f);
            if (canPress)
            {
                float dist = Vector2.Distance(dp, _winPoint);
                var p3 = new Vector3(_winPoint.X, t.Ball.Radius, _winPoint.Y);
                float lunge = Defending.LungeReach(d, p3, t);
                // (a slide only when running on he would not get the ball inside his lunge in time)
                float vmax = Movement.TopSpeed(d.Sheet, t), accel = MathF.Max(0.5f, Movement.AccelCap(d.Sheet, 0.5f * vmax, 1f, t));
                var to = _winPoint - dp;
                float v0 = dist > 1e-4f ? Vector2.Dot(Flat(d.Velocity), to / dist) : 0f;
                if (dist <= lunge) Press(1, k);
                else if (TravelTime(dist - lunge, v0, accel, vmax) > _winTime
                         && dist <= t.Defending.Slide.ReachM.ForRating(d.Sheet.SlideTackle) + t.Defending.Slide.LungeM && !FromBehind(d, a, _winPoint, k)) Press(2, k);
            }
        }
        else
        {
            HoldSpot = bp;
            var to = bp - dp;
            float dist = to.Length();
            var facing = new Vector2(MathF.Cos(d.Facing), MathF.Sin(d.Facing));
            var dir = dist > 1e-4f ? to / dist : facing;
            float da = Vector2.Distance(ap, dp);
            // Close up and running straight on, he bends in from the side the ball is on.
            if (da < k.TackleMaxDistanceM && da > 1e-4f && Vector2.Dot(facing, dir) > k.BendDot)
            {
                float weight = Math.Clamp(k.BendWeightBase + k.BendWeightGain * (1f - da / k.TackleMaxDistanceM), 0f, 1f);
                var toA = (ap - dp) / da;
                var rel = bp - dp;
                float cross = toA.X * rel.Y - toA.Y * rel.X;
                if (MathF.Abs(cross) > 1e-3f)
                {
                    var bent = dir * (1f - weight) + new Vector2(-toA.Y, toA.X) * (MathF.Sign(cross) * weight);
                    if (bent.LengthSquared() > 1e-8f) dir = Vector2.Normalize(bent);
                }
            }
            dir = SteerOffBump(w, d, dir);
            it = new TickIntent { MoveX = dir.X, MoveZ = dir.Y, Sprint = 1f, Jockey = true };
            if (canPress && da <= k.TackleMaxDistanceM && BallBetween(ap, bp, dp, t))
            {
                if (!_slideRolled)
                {
                    _slideRolled = true;
                    _slideThisSpell = _rng.NextFloat() < k.SlidePreference.ForRating(d.Sheet.SlideTackle);
                }
                Press(_slideThisSpell && !FromBehind(d, a, bp, k) ? 2 : 1, k);
            }
        }
        it.Tackle = _buttonLeft > 0f && _button == 1;
        it.Slide = _buttonLeft > 0f && _button == 2;
        return it;
    }

    void Press(int button, DefenderTuning k)
    {
        _button = button;
        _buttonLeft = k.TackleHoldS;
        _winPressed = true;
    }

    /// <summary>True when the carrier's pass will be struck before a lunge pressed now could meet the ball (within stand.exec_max_s).</summary>
    static bool PassLeaving(World w, PlayerState a) =>
        a.ShotAction is { Made: false } s && !w.ShotWindup && (s.ContactTick - w.Tick) * w.Tuning.Dt <= w.Tuning.Defending.Stand.ExecMaxS;

    /// <summary>
    /// From behind: his bearing to the ball within slide_behind_deg of the carrier's heading (his run, or his facing when nearly
    /// still) with the carrier's body nearer him than the ball.
    /// </summary>
    internal static bool FromBehind(PlayerState d, PlayerState a, Vector2 ball, DefenderTuning k)
    {
        Vector2 dp = Flat(d.Position), ap = Flat(a.Position);
        var toBall = ball - dp;
        if (toBall.LengthSquared() < 1e-8f) return false;
        var av = Flat(a.Velocity);
        float heading = av.Length() > 0.5f ? MathF.Atan2(av.Y, av.X) : a.Facing;
        float off = MathF.Abs(Movement.WrapAngle(MathF.Atan2(toBall.Y, toBall.X) - heading)) * 180f / MathF.PI;
        return off <= k.SlideBehindDeg && Vector2.Distance(ap, dp) < toBall.Length();
    }
}
