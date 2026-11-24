using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>
/// What the round report measures without changing the round: the first attack and how it ended, the first time the ball got
/// past the defender (and how), the keeper's depth, his recovery after being taken round and his distance off the shot line at
/// each strike; and that a report saved before these fields existed still loads. Each is checked against what the test itself
/// sees tick by tick.
/// </summary>
public class RoundMeasureTests
{
    static readonly Tuning T = TestData.Tuning();
    /// <summary>The legacy hold-and-press defender bot, for a test of what that bot reports.</summary>
    static readonly Tuning LegacyT = new() { Sheets = T.Sheets, Defender = new DefenderTuning { Brain = "legacy" } };
    static float Dt => T.Dt;

    /// <summary>One tick as the test saw it after the step.</summary>
    sealed record Seen(int Tick, RoundState Before, RoundState After, EventKind[] Events, Vector2 Ball, Vector2 Keeper, Vector2? Defender,
        int ControllerTeam, bool Rounded, int Shots, int Passes, Vector2? StrikeAt, bool DefenderOn = false, bool PassOn = false);

    sealed record Observed(RoundReport Report, List<Seen> Ticks)
    {
        /// <summary>The tick of the first shot that met the boards with no keeper, frame or defender touch since its strike, or -1.</summary>
        public int CleanMissTick()
        {
            bool flying = false;
            foreach (var s in Ticks)
            {
                if (s.Events.Contains(EventKind.Strike)) flying = true;
                if (!flying) continue;
                if (s.Events.Any(e => e is EventKind.Save or EventKind.FrameHit or EventKind.Block)) { flying = false; continue; }
                if (s.Events.Contains(EventKind.BoardHit) && s.Before == RoundState.Flight) return s.Tick;
                if (s.After != RoundState.Flight && !s.Events.Contains(EventKind.Strike)) flying = false;
            }
            return -1;
        }
    }

    static Vector2 G(Vector3 v) => new(v.X, v.Z);

    static Observed Run(World w, Func<World, TickIntent> intent)
    {
        var ticks = new List<Seen>();
        var buf = new TickIntent[w.PlayerCount];
        int cap = Replay.TailCap(w.Tuning) + 2000;
        for (int i = 0; i < cap && w.State != RoundState.Report; i++)
        {
            var before = w.State;
            buf[0] = intent(w);
            w.Step(buf);
            ticks.Add(new Seen(w.Tick, before, w.State, w.Events.Select(e => e.Kind).ToArray(), G(w.Ball.Position), G(w.Keeper.Position),
                w.Defender is { } d ? G(d.Position) : null, w.BallController?.Team ?? -1, w.KeeperReport.Rounded, w.Shots.Count, w.Passes,
                w.Events.Where(e => e.Kind == EventKind.Strike).Select(e => (Vector2?)G(e.Position)).FirstOrDefault(),
                w.Defender is { } dd && (w.BallController == dd || w.Events.Any(e => e.Kind is EventKind.Tackle or EventKind.Block && e.Team == dd.Team && e.PlayerIndex == dd.Index)),
                w.Pass != null));
        }
        Assert.NotNull(w.Report);
        return new Observed(w.Report!, ticks);
    }

    static World NewWorld(string scenario, ulong seed, string? attacker = null, Tuning? tuning = null)
    {
        var sc = TestData.Scenario(scenario);
        return new World(tuning ?? T, sc, TestData.Player(attacker ?? sc.DefaultAttacker), TestData.Player(sc.DefaultKeeper), seed,
            sc.Defenders > 0 ? TestData.Player(sc.DefaultDefender) : null, sc.Mate != null ? TestData.Player(sc.DefaultMate) : null);
    }

    static Observed Scripted(string scenario, ulong seed, ScriptedAttacker? script = null, Tuning? tuning = null)
    {
        var w = NewWorld(scenario, seed, tuning: tuning);
        script ??= new ScriptedAttacker();
        w.Begin();
        return Run(w, script.Next);
    }

    /// <summary>The first scripted round over the seeds that matches, or fails the test.</summary>
    static (Observed Round, ulong Seed) Find(string[] scenarios, int seeds, Func<Observed, bool> match, Func<ScriptedAttacker>? script = null, Tuning? tuning = null)
    {
        for (ulong seed = 1; seed <= (ulong)seeds; seed++)
            foreach (var sc in scenarios)
            {
                var o = Scripted(sc, seed, script?.Invoke(), tuning);
                if (match(o)) return (o, seed);
            }
        Assert.Fail($"no round over {seeds} seeds of {string.Join(", ", scenarios)} shows it");
        return default;
    }

    // ---- the first attack ----

    [Fact]
    public void AGoalEndsTheFirstAttackAsAGoal()
    {
        var (o, _) = Find(new[] { "penalty" }, 50, o => o.Report.Outcome == Outcome.Goal && o.CleanMissTick() < 0);
        var fa = o.Report.FirstAttack!;
        Assert.True(fa.Goal);
        Assert.Equal(FirstAttackEnd.Goal, fa.EndedBy);
        Assert.Equal(o.Report.Ticks, fa.EndedTick);
    }

    [Fact]
    public void ACleanMissOffTheBoardsEndsTheFirstAttackAndTheRoundPlaysOn()
    {
        var (o, _) = Find(new[] { "penalty", "edge_of_box", "breakaway" }, 300, o => o.CleanMissTick() >= 0);
        var fa = o.Report.FirstAttack!;
        Assert.Equal(FirstAttackEnd.Miss, fa.EndedBy);
        Assert.False(fa.Goal);
        Assert.Equal(o.CleanMissTick(), fa.EndedTick);
        // the arena is live after a miss: the round itself went on past it
        Assert.True(o.Report.Ticks > fa.EndedTick, $"round ended at {o.Report.Ticks}, the miss was at {fa.EndedTick}");
        Assert.Contains(o.Ticks, s => s.Tick > fa.EndedTick && s.After == RoundState.Live);
    }

    [Fact]
    public void AShotKeptAliveByTheKeeperOrTheFrameThenAGoalIsAFirstAttackGoal()
    {
        static bool KeptAlive(Observed o)
        {
            if (o.Report.Outcome != Outcome.Goal || o.CleanMissTick() >= 0) return false;
            int strike = o.Ticks.FindIndex(s => s.Events.Contains(EventKind.Strike));
            int goal = o.Ticks.FindIndex(s => s.Events.Contains(EventKind.Goal));
            return strike >= 0 && o.Ticks.Skip(strike).Take(goal - strike).Any(s => s.Events.Any(e => e is EventKind.Save or EventKind.FrameHit));
        }
        var (o, _) = Find(new[] { "penalty", "edge_of_box", "breakaway", "angled_run" }, 600, KeptAlive);
        Assert.True(o.Report.FirstAttack!.Goal);
        Assert.Equal(FirstAttackEnd.Goal, o.Report.FirstAttack.EndedBy);
    }

    [Fact]
    public void TheKeeperHoldingTheBallEndsTheFirstAttackAsHeld()
    {
        var (o, _) = Find(new[] { "penalty", "breakaway" }, 100, o => o.Report.Outcome == Outcome.Held && o.CleanMissTick() < 0);
        Assert.Equal(FirstAttackEnd.Held, o.Report.FirstAttack!.EndedBy);
        Assert.False(o.Report.FirstAttack.Goal);
        Assert.Equal(o.Report.Ticks, o.Report.FirstAttack.EndedTick);
    }

    [Fact]
    public void TheDefenderControllingTheBallEndsTheFirstAttackAsDispossessed()
    {
        var (o, _) = Find(new[] { "defender_duel" }, 50, o => o.Report.Outcome == Outcome.Dispossessed && o.CleanMissTick() < 0,
            () => new ScriptedAttacker { Style = ScriptedAttacker.AttackStyle.Straight });
        Assert.Equal(FirstAttackEnd.Dispossessed, o.Report.FirstAttack!.EndedBy);
        Assert.Equal(o.Report.Ticks, o.Report.FirstAttack.EndedTick);
    }

    [Fact]
    public void TheClockEndsAFirstAttackWithNoShotAsTime()
    {
        var w = NewWorld("penalty", 3);
        w.Begin();
        var o = Run(w, _ => default);
        Assert.Equal(Outcome.TimeUp, o.Report.Outcome);
        Assert.Equal(FirstAttackEnd.Time, o.Report.FirstAttack!.EndedBy);
        Assert.False(o.Report.FirstAttack.Goal);
        Assert.Equal(o.Report.Ticks, o.Report.FirstAttack.EndedTick);
    }

    // ---- past the defender ----

    /// <summary>
    /// The first tick the ball was past the defender, worked out from what the test saw: the ball at least 0.5 m nearer the goal
    /// centre than he is on a tick the attackers have it (one of them controls it, or a pass they struck is on its way; a shot
    /// in flight is not theirs), and the defender does not win, touch or control it in the 0.5 s after (the round ending
    /// inside that time counts, unless it ends with him controlling the ball). -1 when never.
    /// </summary>
    static int SeenPastTick(Observed o)
    {
        var goal = new Vector2(30f, 0f);
        int hold = (int)MathF.Round(World.PastHoldS / Dt);
        var live = o.Ticks.Where(s => s.After is RoundState.Live or RoundState.Strike or RoundState.Flight || s.Events.Contains(EventKind.Goal)).ToList();
        for (int i = 0; i < live.Count; i++)
        {
            var s = live[i];
            bool theirs = s.ControllerTeam == 0 || s.ControllerTeam == -1 && s.PassOn;
            if (!theirs || s.DefenderOn || !World.IsPast(s.Ball, s.Defender!.Value, goal)) continue;
            bool kept = true;
            for (int j = i + 1; j < live.Count && live[j].Tick - s.Tick <= hold; j++) if (live[j].DefenderOn) { kept = false; break; }
            bool reached = live.Any(x => x.Tick >= s.Tick + hold) || o.Report.Outcome != Outcome.Dispossessed;
            if (kept && reached) return s.Tick;
            // (a candidate he wins back waits for the next past tick with the ball theirs again)
        }
        return -1;
    }

    [Fact]
    public void AShotStruckFromInFrontOfHimThatGoesInIsNotPast()
    {
        // The scripted shooter strikes as soon as the defender is in front within 6 m: the ball flies by him, but a shot in
        // flight is not the attackers' ball, so it never makes him past.
        var (o, _) = Find(new[] { "defender_duel" }, 300, o => o.Report.Outcome == Outcome.Goal && o.Report.Shots.Count == 1
            && o.Ticks.Where(s => s.Tick <= o.Ticks.First(t => t.Events.Contains(EventKind.Strike)).Tick).All(s => s.Ball.X < s.Defender!.Value.X),
            () => new ScriptedAttacker { Style = ScriptedAttacker.AttackStyle.Shoot });
        Assert.Null(o.Report.Defender!.BeatenS);
        Assert.Equal(BeatenBy.None, o.Report.Defender.BeatenBy);
        Assert.Equal(-1, SeenPastTick(o));
    }

    [Fact]
    public void ADribblePastHimFollowedByAShotInsideHalfASecondIsPast()
    {
        // Past him on the ball, then the shot struck before 0.5 s had run: he was past all the same.
        var (o, _) = Find(new[] { "defender_duel" }, 400, o =>
        {
            if (o.Report.Defender!.BeatenS is not float b) return false;
            int t = (int)MathF.Round(b / Dt);
            return o.Ticks.Any(s => s.Events.Contains(EventKind.Strike) && s.Tick > t && s.Tick - t < (int)MathF.Round(World.PastHoldS / Dt));
        }, () => new ScriptedAttacker { Style = ScriptedAttacker.AttackStyle.Cut });
        int tick = (int)MathF.Round(o.Report.Defender!.BeatenS!.Value / Dt);
        Assert.Equal(0, o.Ticks.First(s => s.Tick == tick).ControllerTeam);
        Assert.Equal(BeatenBy.Dribble, o.Report.Defender.BeatenBy);
        Assert.Equal(SeenPastTick(o), tick);
    }

    [Fact]
    public void ADribblePastTheDefenderIsReportedWithItsTimeAndWay()
    {
        var (o, _) = Find(new[] { "defender_duel" }, 200, o => o.Report.Defender!.BeatenS != null,
            () => new ScriptedAttacker { Style = ScriptedAttacker.AttackStyle.Cut });
        var d = o.Report.Defender!;
        Assert.Equal(BeatenBy.Dribble, d.BeatenBy);
        int tick = (int)MathF.Round(d.BeatenS!.Value / Dt);
        Assert.Equal(tick * Dt, d.BeatenS.Value, 4);
        Assert.Equal(0, o.Ticks.First(s => s.Tick == tick).Passes);
        Assert.Equal(SeenPastTick(o), tick);
    }

    [Fact]
    public void APassPastTheDefenderIsReportedAsByPass()
    {
        var (o, _) = Find(new[] { "two_v_one" }, 100, o => o.Report.Defender!.BeatenBy == BeatenBy.Pass,
            () => new ScriptedAttacker { Team = ScriptedAttacker.TeamStyle.Square });
        var d = o.Report.Defender!;
        Assert.NotNull(d.BeatenS);
        int tick = (int)MathF.Round(d.BeatenS!.Value / Dt);
        int passTick = o.Ticks.Last(s => s.Tick <= tick && s.Events.Contains(EventKind.Pass)).Tick;
        Assert.InRange(tick - passTick, 0, (int)MathF.Round(World.PastByPassS / Dt));
        Assert.Equal(SeenPastTick(o), tick);
    }

    [Fact]
    public void ARoundTheDefenderNeverGotPastReportsNone()
    {
        var (o, _) = Find(new[] { "defender_duel" }, 50, o => o.Report.Outcome == Outcome.Dispossessed && o.Ticks.All(s => s.Ball.X < s.Defender!.Value.X),
            () => new ScriptedAttacker { Style = ScriptedAttacker.AttackStyle.Straight }, LegacyT);
        Assert.Null(o.Report.Defender!.BeatenS);
        Assert.Equal(BeatenBy.None, o.Report.Defender.BeatenBy);
        Assert.Equal(0f, o.Report.Defender.TimeContain);
        Assert.Equal(0, o.Report.Defender.WinsLoose + o.Report.Defender.WinsSlow + o.Report.Defender.WinsLastLine);
    }

    // ---- the keeper ----

    [Fact]
    public void TheKeepersDepthAndHisDistanceOffTheShotLineAreMeasured()
    {
        var (o, _) = Find(new[] { "breakaway", "halfway_run" }, 50, o => o.Report.Shots.Count > 0);
        var k = o.Report.Keeper;
        float max = o.Ticks.Where(s => s.After is RoundState.Live or RoundState.Strike or RoundState.Flight).Max(s => 30f - s.Keeper.X);
        Assert.Equal(MathF.Max(0f, max), k.DepthMaxM, 4);
        Assert.Equal(o.Report.Shots.Count, k.OffLineAtStrikeM.Length);
        var goal = new Vector2(30f, 0f);
        int i = 0;
        foreach (var s in o.Ticks.Where(s => s.Events.Contains(EventKind.Strike)))
        {
            // (measured at the strike; the test sees the keeper after his step that tick)
            var ball = s.StrikeAt!.Value;
            Assert.Equal(World.SegmentDistance(s.Keeper, ball, goal), k.OffLineAtStrikeM[i], 1);
            i++;
        }
        Assert.False(k.Pounced);
        Assert.False(k.PounceWon);
    }

    [Fact]
    public void TheKeepersRecoveryRunsFromTakenRoundToBackOnTheLine()
    {
        var goal = new Vector2(30f, 0f);
        var (o, _) = Find(new[] { "breakaway", "halfway_run", "keeper_rush" }, 300, o => o.Ticks.Any(s => s.Rounded));
        int rounded = o.Ticks.First(s => s.Rounded).Tick;
        var back = o.Ticks.FirstOrDefault(s => s.Tick > rounded && s.After is RoundState.Live or RoundState.Strike or RoundState.Flight
            && World.BackOnLine(s.Keeper, s.Ball, goal));
        var k = o.Report.Keeper;
        if (back == null) Assert.Null(k.RecoverS);
        else Assert.Equal((back.Tick - rounded) * Dt, k.RecoverS!.Value, 4);
    }

    [Fact]
    public void BackOnTheLineIsGoalSideOfTheBallAndCloseToIt()
    {
        var goal = new Vector2(30f, 0f);
        var ball = new Vector2(20f, 0f);
        Assert.True(World.BackOnLine(new Vector2(25f, 0.5f), ball, goal));
        Assert.False(World.BackOnLine(new Vector2(25f, 1.0f), ball, goal));
        Assert.False(World.BackOnLine(new Vector2(19.5f, 0.2f), ball, goal), "behind the ball is not back");
        Assert.Equal(1f, World.SegmentDistance(new Vector2(19f, 0f), ball, goal), 4);
    }

    // ---- saved reports ----

    /// <summary>A two v one round's report as the game saved it on 24 November 2026, before the measured fields existed.</summary>
    const string OldReport = """
        {"outcome":"dispossessed","seed":8300539501227938001,"ticks":744,"clock":5.949995,"shots":[],"keeper":{"reaction_s":"NaN","needed_m":1.6831497,"reach_m":0,"margin_m":-1.6831497,"action":"parried","save":"block","dive":"none","save_type":"none","difficulty":"NaN","ready":0,"reflex_s":"NaN","speed_at_strike_ms":"NaN","depth_at_strike_m":"NaN","ball_distance_at_strike_m":"NaN","on_target":false,"action_at_strike":"set","planted_at_strike":false,"rushed":false,"charged":true,"smothered":true,"slid":false,"won_ball":true,"rounded":false,"depth_at_band_m":[2.6351643,7.340584,"NaN","NaN"],"depth_reversals":3,"depth_reversals_first2_s":1},"defender":{"auto_won":1,"lunges_tried":0,"lunges_won":0,"slides_tried":0,"slides_won":0,"blocks":0,"barges":0,"barges_won":0,"fouls":0,"time_active":1.449999,"collected":true,"won_by":"auto"},"team":{"passes":2,"received":1,"offside":false,"control_changes":5}}
        """;

    [Fact]
    public void AReportSavedBeforeTheMeasuredFieldsStillLoads()
    {
        var r = RoundReport.FromJson(OldReport);
        Assert.NotNull(r);
        Assert.Equal(Outcome.Dispossessed, r!.Outcome);
        Assert.Equal(744, r.Ticks);
        Assert.Null(r.FirstAttack);
        Assert.True(r.Keeper.Charged);
        Assert.Empty(r.Keeper.OffLineAtStrikeM);
        Assert.Null(r.Keeper.RecoverS);
        Assert.False(r.Keeper.Pounced);
        Assert.Equal(1, r.Defender!.AutoWon);
        Assert.Null(r.Defender.BeatenS);
        Assert.Equal(BeatenBy.None, r.Defender.BeatenBy);
        Assert.Equal(2, r.Team!.Passes);
    }

    [Fact]
    public void TheMeasuredFieldsAreInTheReportsJsonAndReadBack()
    {
        var (o, _) = Find(new[] { "defender_duel" }, 200, o => o.Report.Defender!.BeatenS != null && o.Report.Shots.Count > 0,
            () => new ScriptedAttacker { Style = ScriptedAttacker.AttackStyle.Cut });
        string json = o.Report.ToJson();
        foreach (var key in new[] { "\"first_attack\"", "\"ended_tick\"", "\"ended_by\"", "\"beaten_s\"", "\"beaten_by\"", "\"time_contain\"", "\"time_run_with\"",
                     "\"wins_loose\"", "\"wins_slow\"", "\"wins_last_line\"", "\"pounced\"", "\"pounce_won\"", "\"depth_max_m\"", "\"recover_s\"", "\"off_line_at_strike_m\"" })
            Assert.Contains(key, json);
        var back = RoundReport.FromJson(json)!;
        Assert.Equal(o.Report.FirstAttack!.EndedBy, back.FirstAttack!.EndedBy);
        Assert.Equal(o.Report.FirstAttack.EndedTick, back.FirstAttack.EndedTick);
        Assert.Equal(o.Report.Defender!.BeatenS, back.Defender!.BeatenS);
        Assert.Equal(o.Report.Defender.BeatenBy, back.Defender.BeatenBy);
        Assert.Equal(o.Report.Keeper.OffLineAtStrikeM, back.Keeper.OffLineAtStrikeM);
    }
}
