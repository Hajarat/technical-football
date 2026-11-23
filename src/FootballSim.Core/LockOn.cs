using System.Numerics;

namespace FootballSim.Core;

/// <summary>How a player stands in the race for a loose ball this tick.</summary>
public enum RegainMode
{
    /// <summary>Not his team's claim holder: the legs follow the stick.</summary>
    None,
    /// <summary>He holds the claim in a race with an opponent: the legs run to the meeting, normal foot reach.</summary>
    Contested,
    /// <summary>He holds the claim with a clear head start: the legs run to the meeting and the regain reach applies.</summary>
    Free,
}

/// <summary>
/// One team's claim on the loose ball: the player who goes for it and where he meets it. <see cref="Player"/> is null when the
/// team has no claim.
/// </summary>
public struct LockOnClaim
{
    /// <summary>The holder, or null.</summary>
    public PlayerState? Player;
    /// <summary>The world tick of the forecast sample he meets (the ball at the end of that tick).</summary>
    public int BallTick;
    /// <summary>The world tick his touch is made on: the tick after <see cref="BallTick"/>, when he has moved and the ball is where that sample says.</summary>
    public int ArriveTick;
    /// <summary>The ball centre at the meeting.</summary>
    public Vector3 Point;
    /// <summary>The ticks his flat-out run to the point takes from now (less than the ticks he has when the meeting leaves him slack).</summary>
    public int RunTicks;
    /// <summary>The world tick he took the claim on.</summary>
    public int SinceTick;
    /// <summary>
    /// He has an action queued for a later tick (see <see cref="PlayerState.MinArrivalTick"/>) but no sample from that tick on is
    /// his, or an opponent is there first: the meeting is his earliest one, and the action is a flick or gives way to a first touch.
    /// </summary>
    public bool Early;
    /// <summary>The nearest opponent's arrival (the other team's holder, or the keeper's rush), world tick; int.MaxValue when none comes.</summary>
    public int OpponentTick;
    /// <summary>The stick slid the meeting to a later sample than his earliest: his first touch waits for the ball to come to it.</summary>
    public bool Slid;
    /// <summary>The meeting is his queued shot's: solved with the strike reach instead of a first touch's.</summary>
    public bool StrikeReach;
}

/// <summary>How a candidate's run is stepped when the solver predicts it.</summary>
public enum LockOnRun
{
    /// <summary>The attacker's off-the-ball run (the touch planner's legs without the ball).</summary>
    Attacker,
    /// <summary>An outfield defender's run (see <see cref="Movement.StepOffBall"/>).</summary>
    Defender,
}

/// <summary>A player who may hold his team's claim this tick, with the intent he plays (the stick slides the meeting and can let the ball run).</summary>
public readonly record struct LockOnCandidate(PlayerState Player, TickIntent Intent, bool Stick, LockOnRun Run);

/// <summary>A candidate's last solves, kept between the ticks they are made afresh on (see <see cref="LockOn.Update"/>).</summary>
internal struct LockOnSolveCache
{
    public bool Solved, Fresh, Early, Slid, Contested;
    /// <summary>The world tick of the solve, and the forecast's start tick it was made against.</summary>
    public int Tick, PathTick;
    /// <summary>His earliest meeting; the one from his queued action's tick (<see cref="LaterMin"/>); and the holder's meeting after the action and the stick.</summary>
    public LockOnMeeting First, Later, Final;
    public int LaterMin, FinalSlack, FreeHint;
}

/// <summary>A solved meeting of a player and the forecast ball. <see cref="RunTicks"/> is the movement ticks his flat-out run needs.</summary>
public readonly record struct LockOnMeeting(bool Found, int ArriveTick, int RunTicks, Vector3 Point)
{
    public static LockOnMeeting None => new(false, -1, 0, Vector3.Zero);
}

/// <summary>
/// Lock-on: the player who will reach a loose ball first goes for it by himself, from any direction. The ball's path is forecast
/// once per ball path (<see cref="BallForecast"/>); each team's candidates are given the earliest sample they can run to in time
/// at a height they can play (<see cref="Solve"/>), and one of them holds the team's claim (<see cref="Update"/>). The holder's
/// legs run to the meeting whatever the stick and sprint button say (<see cref="Legs"/>); the stick can slide the meeting to a
/// later sample or, with the finesse modifier, let the ball run (<see cref="LetsRun"/>). An action queued on the way (a shot)
/// writes its tick as the earliest allowed meeting. It draws no random numbers.
/// </summary>
public static class LockOn
{
    /// <summary>The solver keeps this much inside the reach, so the player it sends is in reach when he gets there, metres.</summary>
    const float ReachMargin = 0.04f;
    /// <summary>Within this share of his reach of the meeting point he stands and waits for the ball.</summary>
    const float StandShare = 0.5f;
    /// <summary>A meeting with no more slack than this is run flat out, ticks.</summary>
    const int FlatOutSlack = 4;
    /// <summary>The search for the first reachable sample never jumps further than this, ticks.</summary>
    const int MaxStride = 12;
    /// <summary>A run is stepped this far past the tick it is tried for, ticks: a sample missed by less says by how much, and the search jumps there.</summary>
    const int MissTicks = 60;
    /// <summary>The stick slide looks at one sample in this many, ticks.</summary>
    const int SlideStride = 6;
    /// <summary>The ball is coming to him when its travel at his earliest meeting is within this cosine of the direction to him.</summary>
    const float ComingCos = 0.2f;
    /// <summary>A candidate's meeting is solved afresh every this many ticks (and at once on a new ball path).</summary>
    const int ResolveTicks = 6;
    /// <summary>... and every this many once the meeting is within <see cref="FineTicks"/>.</summary>
    const int NearResolveTicks = 2;

    static Vector2 Flat(Vector3 v) => new(v.X, v.Z);

    /// <summary>Let the ball run: the finesse modifier held with the stick pushed more than 90 deg away from the ball.</summary>
    public static bool LetsRun(in TickIntent intent, Vector2 toBall, Tuning t)
    {
        if (!intent.Finesse || intent.Move.Length() < t.Touch.StickThreshold) return false;
        return Vector2.Dot(intent.Move, toBall) < 0f;
    }

    /// <summary>True when a Free claim lets the player take the ball where it is now: low enough, and within regain_reach.</summary>
    public static bool InRegainReach(PlayerState p, Ball ball, Tuning t) =>
        p.Regain == RegainMode.Free && ball.Position.Y <= TouchModel.FeetHeight(t)
        && TouchModel.HorizontalDistance(p.Position, ball.Position) <= t.Possession.RegainReach;

    /// <summary>The highest ball centre a first touch plays, metres (the high trap's).</summary>
    public static float PlayableHeight(Tuning t) => t.Touch.Profiles.TrapHigh.MaxBallHeight;

    /// <summary>How close his feet must be to a ball at <paramref name="height"/> for the trap of that band, less the solver's margin, metres.</summary>
    public static float ReachAt(PlayerSheet s, float height, Tuning t)
    {
        var k = t.Touch.Profiles;
        var prof = height <= k.TrapLow.MaxBallHeight ? k.TrapLow : height <= k.TrapMid.MaxBallHeight ? k.TrapMid : k.TrapHigh;
        return MathF.Max(0.05f, TouchModel.FootReach(s, t) * prof.Reach - ReachMargin);
    }

    /// <summary>
    /// The reach a candidate's arrival is solved with: the trap's for the ball's height (see <see cref="ReachAt"/>); a defender's
    /// is no more than his control distance, since he has the ball only once it is at his feet; a queued shot's is the strike
    /// reach (<paramref name="strike"/>).
    /// </summary>
    static float ReachFor(PlayerSheet s, float height, LockOnRun run, bool strike, Tuning t, bool free = false)
    {
        if (strike) return MathF.Max(0.05f, ShotModel.StrikeReach(s, t) - ReachMargin);
        float reach = ReachAt(s, height, t);
        // (a free claim takes a ball at the feet from as far as regain_reach)
        if (free && height <= TouchModel.FeetHeight(t)) reach = MathF.Max(reach, t.Possession.RegainReach - ReachMargin);
        return run == LockOnRun.Defender ? MathF.Min(reach, MathF.Max(0.05f, t.Defender.ControlDistanceM - ReachMargin)) : reach;
    }

    /// <summary>True when the player may hold a claim: an outfield player on his feet who is not stunned, stumbling or just tackled.</summary>
    public static bool Eligible(PlayerState p, float now) =>
        p.Role == PlayerRole.Outfield && !TouchPlanner.Stunned(p, now) && !Defending.Stumbling(p, now) && now >= p.TackledLockUntil
        && !Defending.Down(p, now) && p.Def.Tackle == TackleKind.None;

    // ---- the legs ----

    /// <summary>
    /// The holder's legs: toward the meeting point, flat out when the meeting leaves him no slack, otherwise at the pace that
    /// arrives on the meeting tick; never slower than 0.8 m/s nor faster than the off-ball top speed. Running on to a ball that
    /// rolls away from him or lies still he comes in no faster than the ball's own speed away plus the gap over his speed time
    /// constant, so he arrives at a pace that lets him play it. Close enough to the point he stands and waits.
    /// </summary>
    internal static TickIntent Legs(PlayerState p, Vector3 point, Vector3 ballVelocity, bool resting, float reach, int slackTicks, int ticksLeft, Tuning t)
    {
        var to = new Vector2(point.X - p.Position.X, point.Z - p.Position.Z);
        float d = to.Length();
        float stand = reach * StandShare;
        if (d <= stand || d < 1e-4f) return default;
        var dir = to / d;
        float top = MathF.Max(0.1f, Movement.TopSpeed(p.Sheet, t));
        float pace = slackTicks <= FlatOutSlack ? top : (d - stand) / ((ticksLeft + 1) * t.Dt);
        float away = MathF.Max(0f, Vector2.Dot(Flat(ballVelocity), dir));
        if (resting || away > 0.1f) pace = MathF.Min(pace, away + MathF.Max(0f, d - 0.3f) / Movement.TimeConstant(p.Sheet, t));
        float mag = Math.Clamp(pace, 0.8f, top) / top;
        return new TickIntent { MoveX = dir.X * mag, MoveZ = dir.Y * mag, Sprint = 1f };
    }

    /// <summary>The legs of the holder <paramref name="p"/> for his published meeting (see <see cref="PlayerState.LockOnTick"/>).</summary>
    internal static TickIntent Legs(PlayerState p, int tick, Tuning t) =>
        Legs(p, p.LockOnPoint, p.LockOnBallVelocity, p.LockOnResting, ReachAt(p.Sheet, p.LockOnPoint.Y, t), p.LockOnSlack, Math.Max(0, p.LockOnTick - tick), t);


    /// <summary>He closes on a loose ball as if braking at this, m/s squared (so he arrives slow against it).</summary>
    const float CollectBrake = 3.5f;

    /// <summary>
    /// A defender's legs to his meeting with a loose ball: toward the point at a velocity that closes on it no faster than he can
    /// brake from, on top of the ball's own velocity there, so he arrives able to control it. The solver steps a defender's run
    /// with these legs, so his arrival is what he will really do.
    /// </summary>
    internal static TickIntent CollectLegs(PlayerState d, Vector3 point, Vector3 ballVelocity, Tuning t)
    {
        var bv = Flat(ballVelocity);
        var to = Flat(point) - Flat(d.Position);
        float gap = to.Length();
        float top = MathF.Max(0.1f, Movement.TopSpeed(d.Sheet, t));
        var want = bv + (gap > 1e-4f ? to / gap * MathF.Min(top, MathF.Sqrt(2f * CollectBrake * gap)) : Vector2.Zero);
        float m = want.Length();
        var it = new TickIntent { Sprint = 1f };
        if (m > 1e-3f)
        {
            var dir = want / m * MathF.Min(1f, m / top);
            it.MoveX = dir.X; it.MoveZ = dir.Y;
        }
        return it;
    }

    static void StepRun(PlayerState c, in TickIntent legs, LockOnRun run, float dt, float now, Tuning t, Pitch? pitch)
    {
        if (run == LockOnRun.Defender) Movement.StepOffBall(c, legs, null, float.PositiveInfinity, dt, now, t, pitch);
        else Movement.Step(c, legs, dt, now, new MoveAssist(Movement.TopSpeed(c.Sheet, t), 1f + t.Possession.OffBallTurnBonus, Vector2.Zero, TurnAbout: true), t, pitch);
    }

    /// <summary>The predicted run is stepped this many ticks at a time (the arrival is placed inside the last step by the distance closed).</summary>
    const int RunStride = 4;
    /// <summary>A meeting no further off than this is solved on every tick and its run stepped tick by tick, ticks.</summary>
    const int FineTicks = 30;

    /// <summary>
    /// The movement ticks the player's flat-out run needs to bring <paramref name="point"/> within <paramref name="reach"/>, his
    /// run stepped on a copy of his state (<see cref="RunStride"/> ticks at a time); int.MaxValue when it takes more than
    /// <paramref name="maxTicks"/>.
    /// </summary>
    internal static int RunTicks(PlayerState p, Vector3 point, Vector3 ballVelocity, bool resting, float reach, int maxTicks, LockOnRun run,
        float now, Tuning t, Pitch? pitch)
    {
        float d0 = TouchModel.HorizontalDistance(p.Position, point);
        if (d0 <= reach) return 0;
        var c = p.Scratch();
        c.Jockeying = false;
        c.Def.Carry = Vector2.Zero;
        // (a short run is stepped finely, so a meeting close at hand is placed to the tick)
        int stride = maxTicks <= FineTicks ? 1 : maxTicks <= 2 * FineTicks ? 2 : maxTicks <= 4 * FineTicks ? RunStride : 2 * RunStride;
        float dt = t.Dt * stride;
        for (int k = 0; k < maxTicks; k += stride)
        {
            var legs = run == LockOnRun.Defender ? CollectLegs(c, point, ballVelocity, t) : Legs(c, point, ballVelocity, resting, reach, 0, 0, t);
            StepRun(c, legs, run, dt, now + k * t.Dt, t, pitch);
            float d = TouchModel.HorizontalDistance(c.Position, point);
            if (d <= reach)
            {
                float share = d0 - d > 1e-6f ? Math.Clamp((d0 - reach) / (d0 - d), 0f, 1f) : 1f;
                int ticks = k + Math.Max(1, (int)MathF.Ceiling(share * stride));
                return ticks <= maxTicks ? ticks : int.MaxValue;
            }
            d0 = d;
        }
        return int.MaxValue;
    }

    // ---- the solver ----

    /// <summary>
    /// The earliest meeting of player <paramref name="p"/> and the forecast ball with his touch on a tick in
    /// [<paramref name="fromTick"/>, <paramref name="toTick"/>]: the first sample no higher than <paramref name="maxHeight"/> that
    /// he can bring within reach by its tick. <paramref name="now"/> is the world tick about to be stepped (he has not moved in
    /// it yet): a touch on tick a meets the ball as it is at the end of tick a - 1, after a - now + 1 steps of his run. A distance
    /// gate comes first (distance less reach no more than top speed times the time), then his run is stepped on a copy of his
    /// state toward the sample at a sprint. A resting sample is reachable at any later tick. <paramref name="hint"/> is the
    /// arrive tick of his last solve, where the search starts.
    /// </summary>
    public static LockOnMeeting Solve(PlayerState p, BallForecast f, int now, int fromTick, int toTick, float nowSeconds, Tuning t, Pitch? pitch,
        LockOnRun run = LockOnRun.Attacker, float maxHeight = float.NaN, int hint = -1, bool strike = false, bool free = false)
    {
        if (!f.Valid || f.EndsInGoal && f.GoalIndex == 0) return LockOnMeeting.None;
        if (float.IsNaN(maxHeight)) maxHeight = PlayableHeight(t);
        int lo = Math.Max(now, Math.Max(fromTick, f.StartTick + 1));
        int hi = toTick;
        if (f.RestIndex < 0) hi = Math.Min(hi, f.LastTick + 1);
        if (f.GoalIndex >= 0) hi = Math.Min(hi, f.StartTick + f.GoalIndex);
        if (lo > hi) return LockOnMeeting.None;
        float top = Movement.TopSpeed(p.Sheet, t);

        // A resting ball waits for him: the meeting is his run's own arrival (or the first allowed tick after it).
        int restTick = f.RestIndex >= 0 ? f.StartTick + f.RestIndex : int.MaxValue;
        if (restTick <= lo - 1)
        {
            var point = f.PositionAt(restTick);
            if (point.Y > maxHeight) return LockOnMeeting.None;
            int r = RunTicks(p, point, Vector3.Zero, true, ReachFor(p.Sheet, point.Y, run, strike, t, free), toTick - now + 1, run, nowSeconds, t, pitch);
            if (r == int.MaxValue) return LockOnMeeting.None;
            int arrive = Math.Max(lo, now + Math.Max(1, r) - 1);
            return arrive <= toTick ? new LockOnMeeting(true, arrive, r, point) : LockOnMeeting.None;
        }

        // Whether a touch on tick a is his, and the ticks his run needs (int.MaxValue: not known, not his).
        bool Try(int a, out int need)
        {
            var point = f.PositionAt(a - 1);
            need = int.MaxValue;
            if (point.Y > maxHeight) return false;
            float reach = ReachFor(p.Sheet, point.Y, run, strike, t, free);
            int n = a - now + 1;
            float gap = TouchModel.HorizontalDistance(p.Position, point) - reach;
            if (gap > top * n * t.Dt)
            {
                need = (int)MathF.Ceiling(gap / MathF.Max(0.1f, top * t.Dt));
                return false;
            }
            bool resting = f.RestingAt(a - 1);
            // (the run is stepped a little past the tick, so a sample he just misses says by how much)
            need = RunTicks(p, point, f.VelocityAt(a - 1), resting, reach, n + MissTicks, run, nowSeconds, t, pitch);
            return need <= n;
        }

        int bad = lo - 1, good = -1, goodNeed = 0;
        int at = hint >= lo && hint <= hi ? hint : lo;
        for (int guard = 0; at <= hi && guard < 200; guard++)
        {
            if (Try(at, out int need)) { good = at; goodNeed = need; break; }
            bad = at;
            int short_ = need == int.MaxValue ? MaxStride : need - (at - now + 1);
            int next = at + Math.Clamp(short_, 1, need == int.MaxValue ? MaxStride : MissTicks);
            // The last sample is always tried before giving up.
            at = at < hi && next > hi ? hi : next;
        }
        if (good < 0) return LockOnMeeting.None;
        // The hint was his: is an earlier one too? One tick back first (the usual case: the meeting stands), then by halves.
        if (good == at && at == hint && bad == lo - 1 && good > lo)
        {
            // (a run that needs every tick it has cannot make a sample a tick sooner)
            if (goodNeed >= good - now) bad = good - 1;
            else if (Try(good - 1, out int n1)) { good--; goodNeed = n1; }
            else bad = good - 1;
        }
        while (good - bad > 1)
        {
            int mid = (good + bad) / 2;
            if (Try(mid, out int nm)) { good = mid; goodNeed = nm; }
            else bad = mid;
        }
        return new LockOnMeeting(true, good, goodNeed, f.PositionAt(good - 1));
    }

    /// <summary>The check of one later tick for the stick slide: his if his run brings the sample within reach by then.</summary>
    static bool Reaches(PlayerState p, BallForecast f, int now, int arrive, float nowSeconds, Tuning t, Pitch? pitch, LockOnRun run, float maxHeight, out int need)
    {
        var point = f.PositionAt(arrive - 1);
        need = int.MaxValue;
        if (point.Y > maxHeight) return false;
        float reach = ReachFor(p.Sheet, point.Y, run, false, t);
        int n = arrive - now + 1;
        if (TouchModel.HorizontalDistance(p.Position, point) - reach > Movement.TopSpeed(p.Sheet, t) * n * t.Dt) return false;
        need = RunTicks(p, point, f.VelocityAt(arrive - 1), f.RestingAt(arrive - 1), reach, n, run, nowSeconds, t, pitch);
        return need <= n;
    }

    /// <summary>
    /// The stick slide: with the stick pushed, among the samples from the earliest meeting to <paramref name="lastTick"/> the one
    /// whose bearing from him best matches the stick; if that is within lock_on.stick_slide_deg of the stick and his to reach, it
    /// is the meeting. Otherwise <paramref name="earliest"/> stands.
    /// </summary>
    internal static LockOnMeeting Slide(PlayerState p, BallForecast f, in LockOnMeeting earliest, Vector2 stick, int now, int lastTick,
        float nowSeconds, Tuning t, Pitch? pitch, LockOnRun run, float maxHeight)
    {
        if (!earliest.Found || stick.Length() < t.Touch.StickThreshold) return earliest;
        int hi = f.RestIndex >= 0 ? Math.Min(lastTick, f.StartTick + f.RestIndex + 1) : Math.Min(lastTick, f.LastTick + 1);
        if (f.GoalIndex >= 0) hi = Math.Min(hi, f.StartTick + f.GoalIndex);
        if (hi <= earliest.ArriveTick) return earliest;
        float stickYaw = MathF.Atan2(stick.Y, stick.X);
        float limit = t.LockOn.StickSlideDeg * MathF.PI / 180f;
        float Off(int a)
        {
            var to = Flat(f.PositionAt(a - 1)) - Flat(p.Position);
            return to.LengthSquared() < 1e-6f ? MathF.PI : MathF.Abs(Movement.WrapAngle(MathF.Atan2(to.Y, to.X) - stickYaw));
        }
        // The best match on a coarse pass, made exact round it.
        int best = earliest.ArriveTick;
        float bestOff = Off(best);
        for (int a = earliest.ArriveTick + SlideStride; ; a += SlideStride)
        {
            if (a > hi) a = hi;
            float o = Off(a);
            if (o < bestOff) { bestOff = o; best = a; }
            if (a >= hi) break;
        }
        for (int a = Math.Max(earliest.ArriveTick, best - SlideStride + 1); a <= Math.Min(hi, best + SlideStride - 1); a++)
        {
            float o = Off(a);
            if (o < bestOff) { bestOff = o; best = a; }
        }
        if (best == earliest.ArriveTick || bestOff > limit) return earliest;
        return Reaches(p, f, now, best, nowSeconds, t, pitch, run, maxHeight, out int need)
            ? new LockOnMeeting(true, best, need, f.PositionAt(best - 1)) : earliest;
    }

    // ---- the claims ----

    /// <summary>The ticks the keeper's rush takes to a point: the gap beyond foot reach over his rush speed.</summary>
    static int KeeperTicks(PlayerState keeper, Vector3 point, float reach, Tuning t)
    {
        float gap = MathF.Max(0f, TouchModel.HorizontalDistance(keeper.Position, point) - reach);
        return (int)MathF.Ceiling(gap / MathF.Max(1f, Keeper.RushSpeed(keeper.Sheet, t)) * t.TickRate);
    }

    /// <summary>
    /// One tick of both teams' claims, before anybody moves on world tick <paramref name="now"/>. <paramref name="live"/> is false
    /// when no claim may stand (the ball is controlled or held, has not been played yet, or play is not in Live or Strike): both
    /// claims and the forecast are cleared. Otherwise the forecast is rebuilt when the ball has left its path (which clears both
    /// claims) and grown to the horizon; each team's candidates are solved; with no holder the candidate with the earliest
    /// meeting takes the claim, and a team-mate replaces him only by arriving lock_on.takeover_margin_seconds earlier. The
    /// holder's meeting is then solved from his <see cref="PlayerState.MinArrivalTick"/> and slid by his stick. Every player's
    /// <see cref="PlayerState.Regain"/>, <see cref="PlayerState.LockOnPoint"/> and <see cref="PlayerState.LockOnTick"/> are written.
    /// </summary>
    /// <param name="candidates">Per team, the players who may hold its claim (already filtered by the caller's own rules).</param>
    /// <param name="keeper">The keeper team 0's holder races (team 1's), or null.</param>
    public static void Update(BallForecast f, LockOnClaim[] claims, IReadOnlyList<LockOnCandidate>[] candidates,
        IReadOnlyList<IReadOnlyList<PlayerState>> teams, PlayerState? keeper, Ball ball, Pitch? pitch, bool live, int now, float nowSeconds, Tuning t)
    {
        foreach (var team in teams)
            foreach (var q in team) { q.Regain = RegainMode.None; q.LockOnTick = -1; }
        if (!live)
        {
            f.Clear();
            claims[0] = default; claims[1] = default;
            return;
        }
        var k = t.LockOn;
        int horizon = Math.Max(1, (int)MathF.Round(k.HorizonSeconds * t.TickRate));
        // The ball as it is now is the ball at the end of the tick before this one.
        if (!f.Matches(ball, now - 1, k.RebuildDriftM))
        {
            f.Build(ball, pitch, now - 1, now - 1 + horizon, t.Dt);
            claims[0] = default; claims[1] = default;
        }
        else f.Extend(now - 1 + horizon);
        if (f.EndsInGoal) { claims[0] = default; claims[1] = default; return; }

        int toTick = now + horizon;
        int margin = Math.Max(1, (int)MathF.Round(k.TakeoverMarginSeconds * t.TickRate));
        Span<LockOnMeeting> first = stackalloc LockOnMeeting[2];
        Span<int> holderAt = stackalloc int[2];
        for (int team = 0; team < 2; team++)
        {
            holderAt[team] = -1;
            first[team] = LockOnMeeting.None;
            var list = candidates[team];
            ref var claim = ref claims[team];
            int best = -1, held = -1;
            LockOnMeeting bestM = LockOnMeeting.None, heldM = LockOnMeeting.None;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                var p = c.Player;
                var toBall = Flat(ball.Position) - Flat(p.Position);
                if (!Eligible(p, nowSeconds) || c.Stick && LetsRun(c.Intent, toBall, t)) { p.LockOnSolve = default; continue; }
                // His earliest meeting is solved afresh every few ticks (and at once on a new ball path); in between, the run
                // goes as the last solve stepped it, so its meeting stands.
                ref var cache = ref p.LockOnSolve;
                bool near = cache.First.Found && cache.First.ArriveTick - now <= FineTicks || cache.Final.Found && cache.Final.ArriveTick - now <= FineTicks;
                bool fresh = !cache.Solved || cache.PathTick != f.StartTick || now - cache.Tick >= (near ? NearResolveTicks : ResolveTicks)
                    || cache.First.Found && cache.First.ArriveTick < now || cache.Final.Found && cache.Final.ArriveTick < now;
                if (fresh)
                {
                    var solved = Solve(p, f, now, now, toTick, nowSeconds, t, pitch, c.Run, hint: cache.First.Found ? cache.First.ArriveTick : -1);
                    cache = new LockOnSolveCache { Solved = true, Tick = now, PathTick = f.StartTick, First = solved, Later = cache.Later, LaterMin = cache.LaterMin, Final = cache.Final, Early = cache.Early, Fresh = true };
                }
                else cache.Fresh = false;
                var m = cache.First;
                if (!m.Found) continue;
                if (p == claim.Player) { held = i; heldM = m; }
                if (best < 0 || m.ArriveTick < bestM.ArriveTick) { best = i; bestM = m; }
            }
            if (best < 0) { claim = default; continue; }
            // The holder keeps the claim unless a team-mate is there the takeover margin earlier.
            if (held >= 0 && bestM.ArriveTick > heldM.ArriveTick - margin) { best = held; bestM = heldM; }
            if (claim.Player != list[best].Player) claim = new LockOnClaim { Player = list[best].Player, SinceTick = now };
            holderAt[team] = best;
            first[team] = bestM;
        }

        for (int team = 0; team < 2; team++)
        {
            if (holderAt[team] < 0) continue;
            var c = candidates[team][holderAt[team]];
            var p = c.Player;
            ref var claim = ref claims[team];
            var m = first[team];
            // The nearest opponent: the other team's holder, and for team 0 the keeper's rush.
            int opponent = first[1 - team].Found ? first[1 - team].ArriveTick : int.MaxValue;
            int keeperTick = int.MaxValue;
            bool keeperClose = false;
            if (team == 0 && keeper != null)
            {
                keeperTick = now + KeeperTicks(keeper, m.Point, TouchModel.FootReach(p.Sheet, t), t);
                keeperClose = TouchModel.HorizontalDistance(keeper.Position, ball.Position) <= t.Possession.RegainKeeperDistance;
            }
            int nearest = Math.Min(opponent, keeperTick);

            int marginTicks = (int)MathF.Round(t.Possession.RegainKeeperMargin * t.TickRate);
            bool contested = keeperClose || keeperTick <= m.ArriveTick + marginTicks || opponent <= m.ArriveTick + marginTicks;

            // An action queued for a later tick: the first sample from that tick on, unless none is his or an opponent is first.
            // Solved on the same beat as his earliest meeting, and at once when the action's tick jumps (a press).
            bool early = false;
            float maxHeight = float.IsNaN(p.MinArrivalHeight) ? PlayableHeight(t) : p.MinArrivalHeight;
            ref var cache = ref p.LockOnSolve;
            bool again = cache.Fresh || claim.SinceTick == now || Math.Abs(p.MinArrivalTick - cache.LaterMin) > ResolveTicks
                || (p.MinArrivalTick > 0) != (cache.LaterMin > 0) || cache.Final.Found && cache.Final.ArriveTick < now;
            if (again)
            {
                if (p.MinArrivalTick > 0)
                {
                    // The action's own meeting: the first sample from its tick on, at the reach and height it plays.
                    var later = Solve(p, f, now, Math.Max(now, p.MinArrivalTick), toTick, nowSeconds, t, pitch, c.Run, maxHeight,
                        cache.Later.Found ? cache.Later.ArriveTick : -1, strike: p.MinArrivalStrike);
                    cache.Later = later;
                    cache.LaterMin = p.MinArrivalTick;
                    if (m.ArriveTick >= p.MinArrivalTick)
                    {
                        // His earliest meeting already leaves the action its time.
                        if (later.Found) m = later;
                        else early = true;
                    }
                    else
                    {
                        // He could meet the ball before the action is ready. He waits for the later sample only while the ball
                        // is still coming to him at his earliest meeting (it travels toward where he is, or lies still) and no
                        // opponent is there first: a ball going across or away is played at the first contact instead.
                        var bv = Flat(f.VelocityAt(m.ArriveTick - 1));
                        var toHim = Flat(p.Position) - Flat(m.Point);
                        bool coming = bv.Length() < 0.5f || toHim.Length() < 1e-3f
                            || Vector2.Dot(bv / bv.Length(), toHim / toHim.Length()) > ComingCos;
                        if (later.Found && later.ArriveTick <= nearest && coming) m = later;
                        else early = true;
                    }
                }
                else
                {
                    cache.Later = LockOnMeeting.None;
                    cache.LaterMin = 0;
                    // A free claim takes the ball from further off (regain_reach): its meeting comes that much sooner.
                    if (!contested && m.Point.Y <= TouchModel.FeetHeight(t))
                    {
                        var free = Solve(p, f, now, now, m.ArriveTick, nowSeconds, t, pitch, c.Run, hint: cache.FreeHint, free: true);
                        cache.FreeHint = free.Found ? free.ArriveTick : -1;
                        if (free.Found && free.ArriveTick < m.ArriveTick) m = free;
                    }
                    else cache.FreeHint = -1;
                    // The stick slides the meeting along the path, never later than the nearest opponent's arrival.
                    if (c.Stick) m = Slide(p, f, m, c.Intent.Move, now, Math.Min(toTick, nearest), nowSeconds, t, pitch, c.Run, maxHeight);
                }
                cache.Slid = !early && m.ArriveTick > first[team].ArriveTick && p.MinArrivalTick <= 0;
                cache.Contested = contested;
                cache.Final = m;
                cache.Early = early;
                cache.FinalSlack = Math.Max(0, m.ArriveTick - now + 1 - m.RunTicks);
            }
            else { m = cache.Final; early = cache.Early; }
            // (the mode stands between solves, so a meeting solved at the free reach is taken at the free reach)
            contested = cache.Contested;

            claim.BallTick = m.ArriveTick - 1;
            claim.ArriveTick = m.ArriveTick;
            claim.Point = m.Point;
            claim.RunTicks = m.RunTicks;
            claim.Early = early;
            claim.Slid = cache.Slid;
            claim.StrikeReach = !early && p.MinArrivalTick > 0 && p.MinArrivalStrike;
            claim.OpponentTick = nearest;
            p.Regain = contested ? RegainMode.Contested : RegainMode.Free;
            p.LockOnTick = m.ArriveTick;
            p.LockOnPoint = m.Point;
            p.LockOnBallVelocity = f.VelocityAt(m.ArriveTick - 1);
            p.LockOnResting = f.RestingAt(m.ArriveTick - 1);
            p.LockOnSlack = cache.FinalSlack;
        }
    }
}
