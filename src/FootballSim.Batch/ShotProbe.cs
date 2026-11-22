using System.Globalization;
using System.Numerics;
using FootballSim.Core;

/// <summary>
/// The shot probe: scripted shots pressed in possession on a long empty pitch (no keeper involved), per situation and
/// dribbling 1/50/99: standing, a straight jog and sprint, a jog at 45 degrees to the aim (the stick swung onto the aim three
/// ticks after the press), the press right after a sprint push with the ball about 1 m ahead, and the press 0.1 s into a 90
/// degree turn of the stick. Shoot is held 0.2 s. Per row: the time from the press to the strike, the ball's distance from the
/// feet at the press and at the strike, the runs in which a dribble touch was committed after the press or played after it,
/// the strike made (of all runs), the striking foot against the swinging foot at the strike (`foot_ok`: the swinging foot, in
/// its contact window, or the player standing), the strike against the strike estimate published at the press
/// (`vs_eta`, ticks, mean / max of |x|), and the speed steps from the press to 0.5 s after the strike (`shot`, see
/// <see cref="SpeedSteps"/>).
/// </summary>
static class ShotProbe
{
    static TickIntent Dir(float deg, float sprint = 0f, bool shoot = false)
    {
        float a = deg * MathF.PI / 180f;
        return new TickIntent { MoveX = MathF.Cos(a), MoveZ = MathF.Sin(a), Sprint = sprint, ShootHeld = shoot };
    }

    static string F(double v, int d = 2) => double.IsNaN(v) ? "-" : v.ToString("F" + d, CultureInfo.InvariantCulture);

    static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    /// <summary>One situation: the stick before the press (by time, s), whether to wait for a push, the stick during the shot.</summary>
    sealed record Case(string Name, Func<float, TickIntent> Before, float LeadSeconds, Func<int, TickIntent> During, bool AfterPush = false);

    static readonly Case[] Cases =
    {
        new("standing", t => t < 2f ? Dir(0f) : default, 3.5f, _ => default),
        new("jog_straight", _ => Dir(0f), 3f, _ => Dir(0f)),
        new("sprint_straight", _ => Dir(0f, 1f), 3f, _ => Dir(0f, 1f)),
        new("dribble_45_aim_0", _ => Dir(45f), 3f, i => Dir(i < 3 ? 45f : 0f)),
        new("after_sprint_push_1m", _ => Dir(0f, 1f), 2.5f, _ => Dir(0f, 1f), AfterPush: true),
        new("turning_90", t => Dir(t < 3f ? 0f : 90f), 3.1f, _ => Dir(90f)),
    };

    sealed class Row
    {
        public int ChosenFoot, EarlierJumps, CommitMoved;
        public int Runs, Struck, CommittedAfter, PlayedAfter, FootOk, ShotSteps, ShotTicks, NoPress, AtFoot, Forecast;
        public readonly List<float> Time = new(), DPress = new(), DStrike = new(), VsEta = new(), VsTouch = new();
    }

    static bool List;

    public static int Run(Dictionary<string, string> opts, string dataDir, Tuning tuning)
    {
        List = opts.ContainsKey("list");
        int seeds = opts.TryGetValue("seeds", out var ns) ? int.Parse(ns, CultureInfo.InvariantCulture) : 20;
        int[] ratings = opts.TryGetValue("ratings", out var rs) ? rs.Split(',').Select(int.Parse).ToArray() : new[] { 1, 50, 99 };
        var keeper = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", "the_wall.json")).Value!;
        Console.WriteLine($"shot probe: striker_a by dribbling, seeds={seeds}, shoot held 0.2 s; time = press to strike s (mean/max), d_press / d_strike = ball from the feet m (mean/max), committed / played = runs with a dribble touch committed / played after the press, at_foot = strikes pressed with the ball at the feet (struck at the earliest strike tick), foot_ok = the other strikes on the swinging foot in its window (or standing) / those strikes, vs_eta = strike tick - (press + StrikeEta at the press) (mean, max |x|, runs within one tick), vs_touch = strike tick - the later of the earliest strike tick and the first dribble touch of the same run without the press (mean, max |x|, runs within one tick), shot = speed steps (|d speed|/dt > 15 m/s^2) from the press to 0.5 s after the strike / ticks; chosen_foot = the other strikes on the foot chosen for the shot at the release (it never changes after), est_earlier = ticks the published estimate moved more than a tick earlier, commit_moved = ticks it moved after the strike's commit");
        Console.WriteLine($"{"case",-22} {"rating",6} {"runs",5} {"struck",7} {"time",11} {"d_press",11} {"d_strike",11} {"committed",9} {"played",6} {"at_foot",7} {"foot_ok",7} {"vs_eta",13} {"vs_touch",13} {"shot",9}");
        if (opts.TryGetValue("trace", out var tr))
        {
            // --trace <case> --ratings R --index i: one run tick by tick from the press.
            var tc = Cases.First(c => c.Name == tr);
            int idx = opts.TryGetValue("index", out var ix) ? int.Parse(ix, CultureInfo.InvariantCulture) : 0;
            RunOne(tc, ratings[0], (ulong)(idx + 1), idx, tuning, dataDir, keeper, new Row(), trace: true);
            return 0;
        }
        var total = new Row();
        foreach (var c in Cases)
            foreach (int rating in ratings)
            {
                var row = new Row();
                for (int s = 0; s < seeds; s++) RunOne(c, rating, (ulong)(s + 1), s, tuning, dataDir, keeper, row);
                Print(c.Name, rating.ToString(), row);
                Merge(total, row);
            }
        Print("all", "-", total);
        return 0;
    }

    static void Merge(Row a, Row b)
    {
        a.Runs += b.Runs; a.Struck += b.Struck; a.CommittedAfter += b.CommittedAfter; a.PlayedAfter += b.PlayedAfter; a.FootOk += b.FootOk;
        a.ShotSteps += b.ShotSteps; a.ShotTicks += b.ShotTicks; a.NoPress += b.NoPress; a.AtFoot += b.AtFoot; a.Forecast += b.Forecast; a.ChosenFoot += b.ChosenFoot; a.EarlierJumps += b.EarlierJumps; a.CommitMoved += b.CommitMoved;
        a.Time.AddRange(b.Time); a.DPress.AddRange(b.DPress); a.DStrike.AddRange(b.DStrike); a.VsEta.AddRange(b.VsEta); a.VsTouch.AddRange(b.VsTouch);
    }

    static string MM(List<float> l, int d = 2) => l.Count == 0 ? "-" : $"{F(l.Average(), d)}/{F(l.Max(), d)}";

    static void Print(string name, string rating, Row r)
    {
        string vt = r.VsTouch.Count == 0 ? "-" : $"{F(r.VsTouch.Average(), 1)}/{F(r.VsTouch.Max(MathF.Abs), 0)}/{r.VsTouch.Count(x => MathF.Abs(x) <= 1f)}";
        string vs = r.VsEta.Count == 0 ? "-" : $"{F(r.VsEta.Average(), 1)}/{F(r.VsEta.Max(MathF.Abs), 0)}/{r.VsEta.Count(x => MathF.Abs(x) <= 1f)}";
        Console.WriteLine($"{name,-22} {rating,6} {r.Runs,5} {F(100.0 * r.Struck / Math.Max(1, r.Runs), 1),7} {MM(r.Time),11} {MM(r.DPress),11} {MM(r.DStrike),11} {r.CommittedAfter,9} {r.PlayedAfter,6} {r.AtFoot,7} {r.FootOk + "/" + r.Forecast,7} {vs,13} {vt,13} {r.ShotSteps + "/" + r.ShotTicks,9}"
            + $" chosen_foot {r.ChosenFoot}/{r.Forecast} est_earlier {r.EarlierJumps} commit_moved {r.CommitMoved}"
            + (r.NoPress > 0 ? $"  (no press in {r.NoPress})" : ""));
    }

    static void RunOne(Case c, int rating, ulong seed, int index, Tuning t, string dataDir, PlayerSheet keeper, Row row, bool trace = false)
    {
        // The same run without the press: the tick of the first dribble touch from the press tick on (the touch the strike takes
        // the place of), or -1.
        int cfTouch = -1;
        {
            var cw = Setup(c, rating, seed, index, t, dataDir, keeper, out _);
            var cb = new TickIntent[cw.PlayerCount];
            for (int i = 0; i < 360 && cfTouch < 0; i++)
            {
                cb[0] = c.During(i);
                cw.Step(cb);
                if (cw.Events.Any(e => e.Kind == EventKind.Touch)) cfTouch = i;
            }
        }
        var w = Setup(c, rating, seed, index, t, dataDir, keeper, out _);
        RunShot(c, rating, index, t, w, cfTouch, row, trace);
    }

    static World Setup(Case c, int rating, ulong seed, int index, Tuning t, string dataDir, PlayerSheet keeper, out SpeedSteps steps)
    {
        var sheet = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", "striker_a.json")).Value!;
        sheet.CloseDribbling = sheet.SprintDribbling = rating;
        var sc = new Scenario
        {
            Id = "probe",
            Pitch = new PitchSpec { Length = 400f, Width = 200f },
            Ball = new StartSpot { X = 150.5f },
            Attacker = new StartSpot { X = 150f },
        };
        var w = new World(t, sc, sheet, keeper, seed);
        w.Begin();
        var p = w.Attacker;
        var buf = new TickIntent[w.PlayerCount];
        var st = new SpeedSteps();
        steps = st;
        void Step(TickIntent it)
        {
            buf[0] = it;
            st.Before(p);
            w.Step(buf);
            st.After(p, t.Dt);
        }
        // The lead varies by seed so the press falls at a different point of the stride.
        int lead = (int)MathF.Round((c.LeadSeconds + 0.013f * index) * t.TickRate);
        for (int i = 0; i < lead; i++) Step(c.Before(i * t.Dt));
        if (c.AfterPush)
        {
            // The next dribble touch, then until the ball is about 1 m ahead of the feet (or as far as it goes, no touch committed).
            bool pushed = false;
            float last = 0f;
            for (int i = 0; i < 240; i++)
            {
                Step(c.Before((lead + i) * t.Dt));
                float d = Flat(w.Ball.Position, p.Position);
                if (w.Events.Any(e => e.Kind == EventKind.Touch)) { pushed = true; last = d; continue; }
                if (pushed && p.TouchAction is not { Pending: true } && (d >= 1.0f || d < last)) break;
                last = d;
            }
        }
        return w;
    }

    static void RunShot(Case c, int rating, int index, Tuning t, World w, int cfTouch, Row row, bool trace)
    {
        var p = w.Attacker;
        var buf = new TickIntent[w.PlayerCount];
        if (!p.InPossession) { row.NoPress++; return; }
        row.Runs++;
        var steps = new SpeedSteps();
        void Step(TickIntent it)
        {
            buf[0] = it;
            steps.Before(p);
            w.Step(buf);
            steps.After(p, t.Dt);
        }
        float dPress = Flat(w.Ball.Position, p.Position);
        // Pressed with the ball at the feet (within foot reach, no touch committed): struck at the earliest strike tick.
        bool atFoot = TouchModel.InReach(p, w.Ball, t) && p.TouchAction is not { Pending: true };
        Foot chosen = Foot.Right;   // the foot chosen for the shot, as published at the release
        int prevEst = int.MinValue, commitAt = -1;
        var before = p.TouchAction;
        int hold = (int)MathF.Round(0.2f * t.TickRate);
        int press = -1, strike = -1, earliest = 0;
        float eta = float.NaN, dStrike = float.NaN;
        Foot strikeFoot = Foot.Right;
        bool committed = false, played = false, footOk = false;
        var changes = new List<string>();
        int lastEst = int.MinValue;
        float traceSp = new Vector2(p.Velocity.X, p.Velocity.Z).Length();
        for (int i = 0; i < hold + 360; i++)
        {
            var it = c.During(i);
            it.ShootHeld = i < hold;
            Step(it);
            if (i == 0) { press = w.Tick; eta = p.StrikeEta; }
            if (i == hold) chosen = p.StrikeFoot;
            if (i == hold) earliest = Math.Max((int)MathF.Round(t.Shot.WindupSeconds * t.TickRate), hold + (int)MathF.Round(t.Shot.ReleaseLeadSeconds * t.TickRate));
            if (!float.IsNaN(p.StrikeEta))
            {
                int e = w.Tick + (int)MathF.Round(p.StrikeEta * t.TickRate);
                if (prevEst != int.MinValue && e < prevEst - 1) row.EarlierJumps++;
                // From the commit (one windup before the contact) the contact tick is fixed.
                if (commitAt >= 0 && e != commitAt) row.CommitMoved++;
                if (commitAt < 0 && e - w.Tick <= (int)MathF.Round(t.Touch.Windup * t.TickRate) && w.State == RoundState.Strike && !atFoot) commitAt = e;
                prevEst = e;
            }
            if (List && !float.IsNaN(p.StrikeEta))
            {
                int est = w.Tick - press + (int)MathF.Round(p.StrikeEta * t.TickRate);
                if (est != lastEst) { changes.Add($"{w.Tick - press}:{est}"); lastEst = est; }
            }
            if (trace)
            {
                float sp = new Vector2(p.Velocity.X, p.Velocity.Z).Length();
                var sa = p.Action;
                Console.WriteLine($"+{w.Tick - press,3} st={w.State} poss={(p.InPossession ? 1 : 0)} d={F(Flat(w.Ball.Position, p.Position), 3)} spd={F(sp, 3)} dspd={F((sp - traceSp) / t.Dt, 1)} run={F(p.RunSpeed, 3)} steer=({F(p.SteerVelocity.X, 2)},{F(p.SteerVelocity.Z, 2)}) swing={p.SwingFoot}/{F(p.SwingProgress, 2)} act={sa?.Kind}/{sa?.Phase}/{sa?.Foot}/c{sa?.ContactTick - press} eta={F(p.StrikeEta, 3)} sf={p.StrikeFoot} nte={F(p.NextTouchEta, 3)} touch={p.TouchAction?.Kind}/{p.TouchAction?.Phase} ev={string.Join(",", w.Events.Select(e => e.Kind))}");
                traceSp = sp;
            }
            var ta = p.TouchAction;
            if (ta != null && ta != before && ta.Kind != ActionKind.Soft && ta.PlanTick >= press) committed = true;
            if (strike < 0 && w.Events.Any(e => e.Kind == EventKind.Touch) && ta is { Kind: not ActionKind.Soft }) played = true;
            var ev = w.Events.FirstOrDefault(e => e.Kind == EventKind.Strike);
            if (ev.Kind == EventKind.Strike)
            {
                strike = w.Tick;
                dStrike = Flat(ev.Position, p.Position);
                strikeFoot = ev.Foot;
                footOk = !p.IsStepping || p.SwingFoot == ev.Foot && p.SwingProgress >= 0.22f && p.SwingProgress <= 0.88f;
            }
            if (strike >= 0 && w.Tick > strike + 60) break;
            if (!w.IsSimulating) break;
        }
        if (List) Console.WriteLine($"  {c.Name} r{rating} #{index} at_foot={atFoot} d={F(dPress)} strike=+{(strike < 0 ? -1 : strike - press)} touch_without_press=+{cfTouch} est(tick:at) {string.Join(" ", changes)}");
        row.DPress.Add(dPress);
        row.ShotSteps += steps.ShotSteps; row.ShotTicks += steps.ShotTicks;
        if (committed) row.CommittedAfter++;
        if (played) row.PlayedAfter++;
        if (strike < 0) return;
        row.Struck++;
        if (atFoot) row.AtFoot++;
        else { row.Forecast++; if (footOk) row.FootOk++; if (strikeFoot == chosen) row.ChosenFoot++; }
        row.Time.Add((strike - press) * t.Dt);
        row.DStrike.Add(dStrike);
        if (!float.IsNaN(eta)) row.VsEta.Add(strike - (press + MathF.Round(eta * t.TickRate)));
        // Against the dribble touch it takes the place of (or the earliest strike tick when that is later).
        if (cfTouch >= 0) row.VsTouch.Add(strike - press - Math.Max(cfTouch, earliest));
    }
}

/// <summary>
/// Follows every shot pressed in possession in a round: whether it was struck, the keeper touched the ball first, or neither
/// (dropped or faked); dribble touches committed after the press; strikes on the swinging foot in its contact window.
/// </summary>
sealed class ShotAudit
{
    public int Presses, Struck, KeeperFirst, Neither, CommittedAfter, FootOk;
    bool _hadShot, _hadBall, _open;
    int _press;
    BallAction? _touch0;

    public void Before(PlayerState p)
    {
        _hadShot = p.Action is { IsShot: true };
        _hadBall = p.InPossession;
        _touch0 = p.TouchAction;
    }

    public void After(World w)
    {
        var p = w.Attacker;
        bool shot = p.Action is { IsShot: true };
        if (shot && !_hadShot && _hadBall) { Presses++; _open = true; _press = w.Tick; }
        if (!_open) return;
        var ta = p.TouchAction;
        if (ta != null && ta != _touch0 && ta.PlanTick >= _press && ta.Kind != ActionKind.Soft) CommittedAfter++;
        var strike = w.Events.FirstOrDefault(e => e.Kind == EventKind.Strike);
        if (strike.Kind == EventKind.Strike)
        {
            Struck++;
            if (!p.IsStepping || p.SwingFoot == strike.Foot && p.SwingProgress >= 0.22f && p.SwingProgress <= 0.88f) FootOk++;
            _open = false;
        }
        else if (w.Events.Any(e => e.Kind == EventKind.Save)) { KeeperFirst++; _open = false; }
        else if (!shot || !w.IsSimulating) { Neither++; _open = false; }
    }

    public void Add(ShotAudit o)
    {
        Presses += o.Presses; Struck += o.Struck; KeeperFirst += o.KeeperFirst; Neither += o.Neither;
        CommittedAfter += o.CommittedAfter; FootOk += o.FootOk;
    }
}
