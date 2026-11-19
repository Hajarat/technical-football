using System.Numerics;
using FootballSim.Core;
using Xunit.Abstractions;

namespace FootballSim.Core.Tests;

/// <summary>
/// A shot's strike takes the place of the attacker's next touch (2026-11-19): from the press no dribble touch is committed,
/// the strike is the touch controller's first eligible contact from the earliest strike tick on, committed one windup ahead on
/// the foot swinging there, a shot in possession is never dropped, the strike estimate is published from the press, and the
/// stun after the strike slows the runner within the deceleration budget.
/// </summary>
public class ShotNextContactTests
{
    readonly ITestOutputHelper _out;

    public ShotNextContactTests(ITestOutputHelper output) { _out = output; }

    static World Lab(ulong seed, int dribbling)
    {
        var s = TestData.Sheet(75);
        s.Dribbling = dribbling;
        var sc = new Scenario
        {
            Id = "lab",
            Pitch = new PitchSpec { Length = 300f, Width = 200f },
            Ball = new StartSpot { X = 2.5f },
            Attacker = new StartSpot { X = 2.0f },
        };
        var w = new World(new Tuning(), sc, s, TestData.Sheet(50), seed);
        w.Begin();
        return w;
    }

    static TickIntent Dir(float deg, float sprint = 0f, bool shoot = false)
    {
        float a = deg * MathF.PI / 180f;
        return new TickIntent { MoveX = MathF.Cos(a), MoveZ = MathF.Sin(a), Sprint = sprint, ShootHeld = shoot };
    }

    static void Step(World w, TickIntent it)
    {
        var buf = new TickIntent[w.PlayerCount];
        buf[0] = it;
        w.Step(buf);
    }

    static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    sealed record Run(bool Struck, bool AtFoot, bool CommittedAfter, bool FootOk, float MaxDspd, int Press, int Strike, float EtaAtPress, int Earliest,
        bool ChosenFoot, int EarlierMoves, int CommitMoves);

    /// <summary>
    /// Dribbles along <paramref name="deg"/> for <paramref name="lead"/> ticks (optionally until the ball is about 1 m ahead
    /// after a push), presses shoot for 0.2 s with the stick on <paramref name="aim"/>, and follows the shot to 0.5 s after the
    /// strike.
    /// </summary>
    static Run Shoot(ulong seed, int dribbling, float deg, float sprint, int lead, float aim, bool afterPush = false)
    {
        var w = Lab(seed, dribbling);
        var p = w.Attacker;
        for (int i = 0; i < lead; i++) Step(w, Dir(deg, sprint));
        if (afterPush)
        {
            bool pushed = false;
            float last = 0f;
            for (int i = 0; i < 240; i++)
            {
                Step(w, Dir(deg, sprint));
                float d = Flat(w.Ball.Position, p.Position);
                if (w.Events.Any(e => e.Kind == EventKind.Touch)) { pushed = true; last = d; continue; }
                if (pushed && p.TouchAction is not { Pending: true } && (d >= 1f || d < last)) break;
                last = d;
            }
        }
        Assert.True(p.InPossession, $"seed {seed}: not in possession at the press");
        bool atFoot = TouchModel.InReach(p, w.Ball, w.Tuning) && p.TouchAction is not { Pending: true };
        var before = p.TouchAction;
        Foot chosen = ShotModel.StrikingFoot(p, w.Ball.Position, w.Tuning);
        Foot struckWith = chosen;
        int prevEst = int.MinValue, commitEst = -1, earlier = 0, moved = 0;
        int windup = (int)MathF.Round(w.Tuning.Touch.Windup * w.Tuning.TickRate);
        int hold = (int)MathF.Round(0.2f * w.Tuning.TickRate);
        int press = -1, strike = -1;
        float eta = float.NaN, maxDspd = 0f;
        bool committed = false, footOk = false;
        float sp0 = p.Speed;
        for (int i = 0; i < hold + 600; i++)
        {
            Step(w, Dir(i < 3 ? deg : aim, sprint, shoot: i < hold));
            if (i == 0) { press = w.Tick; eta = p.StrikeEta; }
            if (!float.IsNaN(p.StrikeEta))
            {
                int e = w.Tick + (int)MathF.Round(p.StrikeEta * w.Tuning.TickRate);
                if (prevEst != int.MinValue && e < prevEst - 1) earlier++;
                if (commitEst >= 0 && e != commitEst) moved++;
                if (commitEst < 0 && !atFoot && w.State == RoundState.Strike && e - w.Tick <= windup) commitEst = e;
                prevEst = e;
            }
            maxDspd = MathF.Max(maxDspd, MathF.Abs(p.Speed - sp0) / w.Tuning.Dt);
            sp0 = p.Speed;
            var ta = p.TouchAction;
            if (strike < 0 && ta != null && ta != before && ta.Kind != ActionKind.Soft && ta.PlanTick >= press) committed = true;
            if (w.Events.FirstOrDefault(e => e.Kind == EventKind.Strike) is { Kind: EventKind.Strike } ev)
            {
                strike = w.Tick;
                struckWith = ev.Foot;
                footOk = TouchPlanner.FootInWindow(p, ev.Foot);
            }
            if (strike >= 0 && w.Tick >= strike + 60) break;
        }
        int earliest = press + (int)MathF.Round(w.Tuning.Shot.WindupSeconds * w.Tuning.TickRate);
        return new Run(strike >= 0, atFoot, committed, footOk, maxDspd, press, strike, eta, earliest, struckWith == chosen, earlier, moved);
    }

    [Fact]
    public void EveryShotPressedInPossessionIsStruckAsTheNextContactWithoutASpeedStep()
    {
        int runs = 0, forecast = 0, chosenFoot = 0;
        foreach (var (deg, sprint, aim) in new[] { (0f, 0f, 0f), (0f, 1f, 0f), (45f, 0f, 0f), (0f, 0f, 90f) })
            foreach (int rating in new[] { 1, 50, 99 })
                for (ulong seed = 1; seed <= 10; seed++)
                {
                    var r = Shoot(seed, rating, deg, sprint, 300 + (int)seed * 2, aim);
                    string id = $"dribble {deg} sprint {sprint} aim {aim} rating {rating} seed {seed}";
                    runs++;
                    Assert.True(r.Struck, $"{id}: no strike");
                    Assert.False(r.CommittedAfter, $"{id}: a dribble touch committed after the press");
                    Assert.True(r.MaxDspd <= 15f, $"{id}: speed step {r.MaxDspd:F1} m/s^2 between the press and 0.5 s after the strike");
                    Assert.True(r.Strike >= r.Earliest, $"{id}: struck before the wind-up");
                    Assert.Equal(0, r.EarlierMoves);                    // the published estimate never moves more than a tick earlier
                    Assert.Equal(0, r.CommitMoves);                     // from the strike's commit its contact tick is fixed
                    if (!r.AtFoot)
                    {
                        forecast++;
                        if (r.ChosenFoot) chosenFoot++;
                        Assert.True(r.FootOk, $"{id}: struck off the swinging foot's contact window");
                    }
                }
        _out.WriteLine($"{runs} shots, {forecast} struck at the controller's contact, {chosenFoot} of them on the foot chosen at the press");
        Assert.True(forecast > 0);
    }

    /// <summary>
    /// The ball about 1 m ahead right after a sprint push: the strike against the estimate published at the press, never before
    /// the earliest strike tick. The estimate may move later while the run cannot reach the ball, so the strike is not always on
    /// it. Measured 2026-11-19 (32 lab runs off the feet, strike minus estimate in ticks): -1 x4, 0 x11, +1 x3, +2 x4, +3 x4, +4,
    /// +5 x2, +13, +56, +66 (18 within one tick; the last two waited for a later swing of the striking foot). The floor is that
    /// share.
    /// </summary>
    [Fact]
    public void WithTheBallAMetreAheadTheStrikeComesAtTheForecastContact()
    {
        int n = 0, within = 0;
        var devs = new List<int>();
        foreach (int rating in new[] { 1, 50, 99 })
            for (ulong seed = 1; seed <= 20; seed++)
            {
                var r = Shoot(seed, rating, 0f, 1f, 300 + (int)seed * 2, 0f, afterPush: true);
                if (r.AtFoot) continue;
                Assert.True(r.Struck, $"rating {rating} seed {seed}: no strike");
                Assert.False(float.IsNaN(r.EtaAtPress));
                int expected = Math.Max(r.Earliest, r.Press + (int)MathF.Round(r.EtaAtPress * 120f));
                n++;
                devs.Add(r.Strike - expected);
                if (Math.Abs(r.Strike - expected) <= 1) within++;
                Assert.True(r.Strike >= r.Earliest, $"rating {rating} seed {seed}: struck before the earliest strike tick");
            }
        devs.Sort();
        _out.WriteLine($"ball about 1 m ahead: {within}/{n} strikes within one tick of the forecast at the press; strike - forecast, ticks: {string.Join(" ", devs)}");
        Assert.True(n >= 20);
        Assert.True(within >= 0.55 * n, $"{within}/{n} within one tick");
    }

    [Fact]
    public void TheStrikeEstimateIsPublishedFromThePress()
    {
        var w = Lab(3, 50);
        var p = w.Attacker;
        for (int i = 0; i < 300; i++) Step(w, Dir(0f));
        Assert.True(p.InPossession);
        Assert.True(float.IsNaN(p.StrikeEta));
        Step(w, Dir(0f, shoot: true));
        Assert.True(float.IsFinite(p.StrikeEta), "no strike estimate on the press tick");
        Assert.True(p.StrikeEta >= w.Tuning.Shot.WindupSeconds - 1e-3f);
        var shot = p.Action!;
        Assert.True(shot.IsShot);
        Assert.Equal(shot.ContactTick, w.Tick + (int)MathF.Round(p.StrikeEta * w.Tuning.TickRate));
        Assert.Equal(shot.Foot, p.StrikeFoot);
        for (int i = 0; i < 400 && w.Shots.Count == 0; i++)
        {
            Step(w, Dir(0f, shoot: i < 20));
            if (w.Shots.Count == 0) Assert.True(float.IsFinite(p.StrikeEta), $"tick {i}: estimate lost before the strike");
        }
        Assert.Single(w.Shots);
        Assert.True(float.IsNaN(p.StrikeEta));
    }

    [Fact]
    public void TheStunSlowsTheShooterWithinTheDecelerationBudget()
    {
        var w = Lab(2, 50);
        var p = w.Attacker;
        for (int i = 0; i < 300; i++) Step(w, Dir(0f, 1f));
        int strike = -1;
        float budget = w.Tuning.Movement.BalanceDeceleration * w.Tuning.Dt + 1e-4f, worst = 0f, run0 = p.RunSpeed;
        for (int i = 0; i < 400 && (strike < 0 || w.Tick < strike + 60); i++)
        {
            Step(w, Dir(0f, 1f, shoot: i < 24));
            if (w.Shots.Count > 0 && strike < 0) strike = w.Tick;
            if (strike >= 0) worst = MathF.Max(worst, run0 - p.RunSpeed);
            run0 = p.RunSpeed;
        }
        Assert.True(strike >= 0);
        Assert.True(worst <= budget, $"running speed fell {worst / w.Tuning.Dt:F1} m/s^2 in a tick after the strike");
        float cap = w.Tuning.Shot.StunSpeedFraction * Movement.TopSpeed(p.Sheet, w.Tuning);
        Assert.True(p.RunSpeed <= cap + 1e-3f || !TouchPlanner.Stunned(p, w.SimTime), "the stun did not bring him down to its cap");
    }

    /// <summary>
    /// The recorded rounds of the 2026-11-19 play-test sessions (144024 and 151333, kept in TestData/playtest): every shot pressed
    /// in possession ends in a strike unless the keeper touched the ball first, no dribble touch is committed after a press, and
    /// a strike at the controller's contact is on the swinging foot in its window.
    /// </summary>
    [Fact]
    public void ThePlayTestShotsInPossessionEndInAStrikeUnlessTheKeeperTouchedFirst()
    {
        string dir = Path.Combine(TestData.DataDir, "..", "src", "FootballSim.Core.Tests", "TestData", "playtest");
        var files = Directory.GetFiles(dir, "round-1*-penalty-*.json").Where(f => Path.GetFileName(f).StartsWith("round-144024") || Path.GetFileName(f).StartsWith("round-151333")).ToArray();
        Assert.NotEmpty(files);
        int presses = 0, struck = 0, keeper = 0, ended = 0, faked = 0;
        foreach (var f in files)
        {
            var rp = Replay.Load(f, out var err);
            Assert.True(rp != null, err);
            var w = new World(new Tuning(), TestData.Scenario(rp!.ScenarioId), TestData.Player(rp.AttackerId), TestData.Player(rp.KeeperId), rp.Seed);
            w.Begin();
            var p = w.Attacker;
            var buf = new TickIntent[w.PlayerCount];
            int per = Math.Max(1, rp.PlayersPerTick);
            bool open = false, atFoot = false;
            int press = -1;
            for (int tick = 0; tick < rp.TickCount && w.State != RoundState.Report; tick++)
            {
                Array.Clear(buf);
                for (int i = 0; i < w.PlayerCount && i < per; i++) buf[i] = rp.Intents[tick * per + i].ToIntent();
                bool hadShot = p.Action is { IsShot: true }, hadBall = p.InPossession;
                bool foot0 = TouchModel.InReach(p, w.Ball, w.Tuning) && p.TouchAction is not { Pending: true };
                var touch0 = p.TouchAction;
                w.Step(buf);
                if (!hadShot && hadBall && p.Action is { IsShot: true }) { open = true; presses++; press = w.Tick; atFoot = foot0; }
                if (!open) continue;
                var ta = p.TouchAction;
                Assert.False(ta != null && ta != touch0 && ta.Kind != ActionKind.Soft && ta.PlanTick >= press, $"{f}: a dribble touch committed after the press");
                if (w.Events.FirstOrDefault(e => e.Kind == EventKind.Strike) is { Kind: EventKind.Strike } ev)
                {
                    struck++; open = false;
                    if (!atFoot) Assert.True(TouchPlanner.FootInWindow(p, ev.Foot), $"{f}: struck off the swinging foot's window");
                }
                else if (w.Events.Any(e => e.Kind == EventKind.Save)) { keeper++; open = false; }
                else if (w.State is RoundState.Outcome or RoundState.Report) { ended++; open = false; }
                else if (p.Action is not { IsShot: true })
                {
                    Assert.True(p.SkillMove is { Kind: ActionKind.FakeStop or ActionKind.FakeCut }, $"{f}: a shot pressed in possession at tick {press} was dropped");
                    faked++; open = false;
                }
            }
        }
        _out.WriteLine($"{files.Length} rounds: {presses} shots pressed in possession, {struck} struck, {keeper} keeper first, {faked} faked, {ended} cut off by the round's end");
        Assert.True(presses > 0);
        Assert.Equal(presses, struck + keeper + faked + ended);
        Assert.Equal(0, ended);
    }
}
