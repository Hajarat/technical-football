using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>
/// The defender: the bot's hold spot and its switches between holding and pressing, the auto tackle, the lunge, the slide, the
/// barge, the block, body contacts, the keeper's yield, the loose ball he controls, and that all of it is deterministic.
/// </summary>
public class DefenderTests
{
    static readonly Tuning T = new();
    /// <summary>The legacy hold-and-press bot, for the tests of its own rules.</summary>
    static readonly Tuning LegacyT = new() { Defender = new DefenderTuning { Brain = "legacy" } };
    static readonly Scenario Duel = TestData.Scenario("defender_duel");
    static readonly PlayerSheet Striker = TestData.Player("striker_a");
    static readonly PlayerSheet Wall = TestData.Player("the_wall");
    static readonly PlayerSheet CentreBack = TestData.Player("centre_back");
    static float Dt => T.Dt;

    static World Live(ulong seed = 1, PlayerSheet? defender = null, Tuning? tuning = null)
    {
        var w = new World(tuning ?? T, Duel, Striker, Wall, seed, defender ?? CentreBack);
        w.Begin();
        return w;
    }

    static void Step(World w, TickIntent attacker = default, int ticks = 1)
    {
        var buf = new TickIntent[w.PlayerCount];
        buf[0] = attacker;
        for (int i = 0; i < ticks; i++) w.Step(buf);
    }

    /// <summary>The attacker takes his first touch (stick at goal), so he has the ball.</summary>
    static void GiveAttackerTheBall(World w)
    {
        for (int i = 0; i < 30 && w.BallController != w.Attacker; i++) Step(w, new TickIntent { MoveX = 1f });
        Assert.Same(w.Attacker, w.BallController);
    }

    static PlayerState Player(int team, PlayerSheet sheet, float x, float z, float facing = 0f)
    {
        var p = new PlayerState(team, team, PlayerRole.Outfield, sheet) { Position = new Vector3(x, 0f, z), Facing = facing };
        p.TurnRefHeading = facing;
        return p;
    }

    static Ball BallAt(float x, float z, float y = -1f) => new(T.Ball) { Position = new Vector3(x, y < 0f ? T.Ball.Radius : y, z) };

    // ---- data ----

    [Fact]
    public void TheDuelScenarioHasOneDefenderFacingTheAttackerAndOthersHaveNone()
    {
        var w = Live();
        Assert.NotNull(w.Defender);
        Assert.Equal(1, w.Defender!.Team);
        Assert.Equal(1, w.Defender.Index);
        Assert.Equal(3, w.PlayerCount);
        Assert.Equal(16f, w.Defender.Position.X, 3);
        Assert.Equal(MathF.PI, MathF.Abs(w.Defender.Facing), 3);
        var plain = TestData.Live("breakaway");
        Assert.Null(plain.Defender);
        Assert.Null(plain.DefenderBot);
        Assert.Equal(2, plain.PlayerCount);
    }

    [Fact]
    public void ASheetWithoutDefendingRatingsReadsFiftyWithoutWarningsAndIsWrittenWithoutThem()
    {
        var r = DataLoader.LoadPlayer(Path.Combine(TestData.DataDir, "players", "striker_a.json"));
        Assert.True(r.Ok);
        Assert.Empty(r.Warnings);
        foreach (var n in PlayerSheet.DefendingRatingNames) Assert.Equal(50, r.Value!.GetRating(n));
        Assert.DoesNotContain("stand_tackle", r.Value!.ToJson());
        var cb = TestData.Player("centre_back");
        Assert.Equal(78, cb.StandTackle);
        Assert.Contains("\"awareness\": 76", cb.ToJson());
        var back = DataLoader.ParsePlayer(cb.ToJson());
        foreach (var n in PlayerSheet.DefendingRatingNames) Assert.Equal(cb.GetRating(n), back.Value!.GetRating(n));
    }

    [Fact]
    public void TheNewOutcomesKeepTheOldNumbersAndHaveTheirLabels()
    {
        Assert.Equal(7, (int)Outcome.TimeUp);
        Assert.Equal(8, (int)Outcome.Dispossessed);
        Assert.Equal(9, (int)Outcome.Foul);
        Assert.Equal("DISPOSSESSED", Outcome.Dispossessed.Label());
        Assert.Equal("FOUL", Outcome.Foul.Label());
    }

    [Fact]
    public void TheTuningHashLeavesTheDefenderBlocksOutAndTheDefendingHashCoversThem()
    {
        var a = new Tuning();
        var b = new Tuning();
        b.Defender.OvershootM = 0.9f;
        b.Defending.Auto.GuaranteeM = 0.2f;
        Assert.Equal(DataLoader.TuningHash(a), DataLoader.TuningHash(b));
        Assert.NotEqual(DataLoader.DefendingHash(a), DataLoader.DefendingHash(b));
        b = new Tuning();
        b.Keeper.TurnRateDeg += 1f;
        Assert.NotEqual(DataLoader.TuningHash(a), DataLoader.TuningHash(b));
        Assert.Equal(DataLoader.DefendingHash(a), DataLoader.DefendingHash(b));
    }

    // ---- the hold spot ----

    [Theory]
    [InlineData(5f, 0f)]
    [InlineData(12f, 4f)]
    [InlineData(20f, -6f)]
    public void TheHoldSpotIsOnTheBallsLineToTheGoalCentreAtTheDistanceForTheBallsDepth(float bx, float bz)
    {
        var pitch = new Pitch(Duel.Pitch, T.Frame, T.Arena);
        var ball = new Vector2(bx, bz);
        var goal = new Vector2(pitch.GoalLineX, 0f);
        var spot = DefenderBot.HoldSpotFor(ball, 0f, pitch, T);
        var dir = Vector2.Normalize(goal - ball);
        float along = Vector2.Dot(spot - ball, dir);
        float across = (spot - ball).X * dir.Y - (spot - ball).Y * dir.X;
        Assert.Equal(0f, across, 3);
        Assert.Equal(T.Defender.HoldDistanceM.Evaluate(Vector2.Distance(ball, goal)), along, 3);
        // With an offset the spot moves square to that line by exactly the offset.
        var off = DefenderBot.HoldSpotFor(ball, 1.5f, pitch, T);
        Assert.Equal(1.5f, Vector2.Distance(off, spot), 3);
        Assert.Equal(0f, Vector2.Dot(off - spot, dir), 3);
    }

    [Fact]
    public void TheHoldDistanceRunsFromNearToFarWithTheBallsDistanceFromGoal()
    {
        var k = T.Defender.HoldDistanceM;
        Assert.Equal(2.5f, k.Evaluate(5f), 3);
        Assert.Equal(2.5f, k.Evaluate(8f), 3);
        Assert.Equal(3.75f, k.Evaluate(14f), 3);
        Assert.Equal(5f, k.Evaluate(20f), 3);
        Assert.Equal(5f, k.Evaluate(28f), 3);
    }

    [Fact]
    public void TheSideOffsetIsDrawnOncePerHoldSpellInsideItsRange()
    {
        // The defender is kept behind the attacker (not goal-side), so he holds throughout.
        var w = Live(seed: 7, tuning: LegacyT);
        var pitch = w.Pitch;
        var offsets = new List<float>();
        for (int i = 0; i < 240; i++)
        {
            w.Defender!.Position = new Vector3(1.5f, 0f, 2f);
            Step(w, new TickIntent { MoveX = 1f, Sprint = 0f });
            Assert.Equal(DefenderMode.Drop, w.DefenderBot!.Mode);
            var b = new Vector2(w.Ball.Position.X, w.Ball.Position.Z);
            var dir = Vector2.Normalize(new Vector2(pitch.GoalLineX, 0f) - b);
            var rel = w.DefenderBot.HoldSpot - b;
            offsets.Add(Vector2.Dot(rel, new Vector2(-dir.Y, dir.X)));
        }
        float lo = T.Defender.HoldOffsetMinM.Evaluate(CentreBack.Awareness), hi = T.Defender.HoldOffsetMaxM.Evaluate(CentreBack.Awareness);
        Assert.InRange(MathF.Abs(offsets[0]), lo - 1e-3f, hi + 1e-3f);
        Assert.All(offsets, o => Assert.Equal(offsets[0], o, 2));
    }

    [Fact]
    public void TheOffsetRangeNarrowsWithAwareness()
    {
        var k = T.Defender;
        Assert.Equal((3f, 4f), (k.HoldOffsetMinM.Evaluate(1), k.HoldOffsetMaxM.Evaluate(1)));
        Assert.Equal((1.5f, 2f), (k.HoldOffsetMinM.Evaluate(50), k.HoldOffsetMaxM.Evaluate(50)));
        Assert.Equal((0.5f, 1f), (k.HoldOffsetMinM.Evaluate(99), k.HoldOffsetMaxM.Evaluate(99)));
    }

    // ---- hold and press ----

    [Fact]
    public void HePressesOnlyWhenGoalSideByTheMargin()
    {
        var w = Live(tuning: LegacyT);
        GiveAttackerTheBall(w);
        var goal = new Vector3(w.Pitch.GoalLineX, 0f, 0f);
        float dOpp = Vector3.Distance(w.Attacker.Position, goal);
        // Level with the attacker (inside the margin): he holds.
        w.Defender!.Position = goal + Vector3.Normalize(new Vector3(-1f, 0f, 0.4f)) * (dOpp - T.Defender.GoalSideMarginM + 0.2f);
        Step(w);
        Assert.Equal(DefenderMode.Drop, w.DefenderBot!.Mode);
        // Goal-side by more than the margin: he presses (his first hold counts as served).
        w.Defender.Position = goal + Vector3.Normalize(new Vector3(-1f, 0f, 0.4f)) * (dOpp - T.Defender.GoalSideMarginM - 0.5f);
        Step(w);
        Assert.Equal(DefenderMode.Win, w.DefenderBot.Mode);
    }

    [Fact]
    public void BeatenByTheOvershootHeDropsBackAndThenHoldsForHisAwarenessTime()
    {
        var w = Live(tuning: LegacyT);
        GiveAttackerTheBall(w);
        Step(w, default, 3);
        Assert.Equal(DefenderMode.Win, w.DefenderBot!.Mode);
        var goal = new Vector3(w.Pitch.GoalLineX, 0f, 0f);
        float dOpp = Vector3.Distance(w.Attacker.Position, goal);
        // Further from the goal than the attacker by more than the overshoot, well to the side (out of every tackle's reach).
        var beaten = goal + Vector3.Normalize(new Vector3(-1f, 0f, 0.35f)) * (dOpp + T.Defender.OvershootM + 0.3f);
        w.Defender!.Position = beaten;
        Step(w);
        Assert.Equal(DefenderMode.Drop, w.DefenderBot.Mode);
        // Goal-side again at once: he still holds, until his awareness time has run.
        float wait = T.Defender.ToActiveAfterS.Evaluate(CentreBack.Awareness);
        var goalSide = goal + Vector3.Normalize(new Vector3(-1f, 0f, 0.6f)) * (dOpp - 3f);
        int held = 0;
        for (int i = 0; i < 600; i++)
        {
            w.Defender.Position = goalSide;
            Step(w);
            if (w.DefenderBot.Mode == DefenderMode.Win) break;
            held++;
        }
        Assert.InRange(held * Dt, wait - 0.05f, wait + 0.05f);
    }

    [Theory]
    [InlineData(1, 6f, 3f)]
    [InlineData(50, 2f, 5f)]
    [InlineData(99, 1f, 8f)]
    public void TheHoldAndPressTimesFollowAwareness(int awareness, float hold, float active)
    {
        Assert.Equal(hold, T.Defender.ToActiveAfterS.Evaluate(awareness), 3);
        Assert.Equal(active, T.Defender.ActiveMaxS.Evaluate(awareness), 3);
    }

    [Fact]
    public void APressEndsAfterTheActiveTime()
    {
        // A defender too slow to reach the attacker, kept goal-side and far off: only the clock ends his press.
        var slow = CentreBack.Clone();
        slow.SetRating("awareness", 1);
        var w = Live(defender: slow, tuning: LegacyT);
        GiveAttackerTheBall(w);
        var far = new Vector3(w.Pitch.GoalLineX - 3f, 0f, 6f);
        int pressed = 0;
        for (int i = 0; i < 1200; i++)
        {
            w.Defender!.Position = far;
            Step(w);
            if (w.DefenderBot!.Mode == DefenderMode.Win) pressed++;
            else if (pressed > 0) break;
        }
        Assert.InRange(pressed * Dt, 3f - 0.05f, 3f + 0.05f);
    }

    [Fact]
    public void TheBallIsBetweenThemOnlyWithinTheTackleAngle()
    {
        var a = new Vector2(10f, 0f);
        var ball = new Vector2(10.6f, 0f);
        Assert.True(DefenderBot.BallBetween(a, ball, new Vector2(11.6f, 0f), T));
        // 25 degrees off the line attacker to ball: still between; 40 degrees: not.
        Vector2 At(float deg) => ball + new Vector2(MathF.Cos(deg * MathF.PI / 180f), MathF.Sin(deg * MathF.PI / 180f));
        Assert.True(DefenderBot.BallBetween(a, ball, At(25f), T));
        Assert.False(DefenderBot.BallBetween(a, ball, At(40f), T));
        Assert.False(DefenderBot.BallBetween(a, ball, new Vector2(9f, 0f), T));
    }

    // ---- auto tackle ----

    [Fact]
    public void ABallInsideTheGuaranteeIsAlwaysTakenBelowTheHeightLimit()
    {
        var def = Player(1, CentreBack, 10f, 0f, MathF.PI);
        var att = Player(0, Striker, 9.5f, 0f);
        // Behind him (outside his front half), even shielded by the attacker's body: inside 45 cm it is his.
        Assert.True(Defending.AutoTackleReaches(def, att, BallAt(10.4f, 0f), 1f, T));
        Assert.True(Defending.AutoTackleReaches(def, att, BallAt(9.6f, 0.1f), 1f, T));
        Assert.False(Defending.AutoTackleReaches(def, att, BallAt(9.6f, 0.1f, 1.05f), 1f, T));
    }

    [Fact]
    public void BeyondTheGuaranteeItMustBeInFrontInsideTheRadiusAndTheAngle()
    {
        var def = Player(1, CentreBack, 10f, 0f, MathF.PI);   // facing -X, standing
        var att = Player(0, Striker, 7f, 0f);
        float radius = Defending.AutoRadius(def, T);
        Assert.Equal(1.2f * T.Defending.Auto.RadiusFactor.ForRating(CentreBack.StandTackle), radius, 3);
        Assert.True(Defending.AutoTackleReaches(def, att, BallAt(10f - radius + 0.05f, 0f), 1f, T));
        Assert.False(Defending.AutoTackleReaches(def, att, BallAt(10f - radius - 0.05f, 0f), 1f, T));
        // Behind him: never beyond the guarantee.
        Assert.False(Defending.AutoTackleReaches(def, att, BallAt(10.6f, 0f), 1f, T));
        // Standing, the angle is the widest (90 degrees); at full speed it narrows to 45.
        Assert.Equal(90f, Defending.AutoAngleDeg(def, T), 2);
        def.Velocity = new Vector3(-Movement.TopSpeed(CentreBack, T), 0f, 0f);
        Assert.Equal(45f, Defending.AutoAngleDeg(def, T), 2);
        float r = Defending.AutoRadius(def, T) - 0.05f;
        Vector3 Off(float deg) => new(10f - r * MathF.Cos(deg * MathF.PI / 180f), T.Ball.Radius, r * MathF.Sin(deg * MathF.PI / 180f));
        Assert.True(Defending.AutoTackleReaches(def, att, new Ball(T.Ball) { Position = Off(40f) }, 1f, T));
        Assert.False(Defending.AutoTackleReaches(def, att, new Ball(T.Ball) { Position = Off(50f) }, 1f, T));
    }

    [Fact]
    public void TheReachGrowsWithStandTackleAndIsLongerJockeying()
    {
        var weak = CentreBack.Clone(); weak.SetRating("stand_tackle", 1);
        var strong = CentreBack.Clone(); strong.SetRating("stand_tackle", 99);
        var a = Player(1, weak, 0f, 0f);
        var b = Player(1, strong, 0f, 0f);
        Assert.Equal(0.25f * 1.2f, Defending.AutoRadius(a, T), 3);
        Assert.Equal(1.2f, Defending.AutoRadius(b, T), 3);
        b.Jockeying = true;
        Assert.Equal(1.4f, Defending.AutoRadius(b, T), 3);
        b.Velocity = new Vector3(4f, 0f, 0f);
        Assert.Equal(1.0f, Defending.AutoRadius(b, T), 3);
    }

    [Fact]
    public void ShieldingStopsTheAutoTackleAndTheCooldownAndAStumbleDoToo()
    {
        var def = Player(1, CentreBack, 10f, 0f, MathF.PI);
        var ball = BallAt(9.2f, 0f);
        // The attacker's body on the line between them, close to the ball: shielded.
        var shield = Player(0, Striker, 9.55f, 0.1f);
        Assert.True(Defending.Shielded(def.Position, ball.Position, shield.Position, T));
        Assert.False(Defending.AutoTackleReaches(def, shield, ball, 1f, T));
        // The attacker on the far side of the ball: open.
        var open = Player(0, Striker, 8.7f, 0f);
        Assert.False(Defending.Shielded(def.Position, ball.Position, open.Position, T));
        Assert.True(Defending.AutoTackleReaches(def, open, ball, 1f, T));
        // Off the lane by more than its half width: open.
        Assert.False(Defending.Shielded(def.Position, ball.Position, new Vector3(9.6f, 0f, 0.5f), T));
        def.Def.AutoCooldownUntil = 2f;
        Assert.False(Defending.AutoTackleReaches(def, open, ball, 1f, T));
        Assert.True(Defending.AutoTackleReaches(def, open, ball, 2.01f, T));
        def.StumbleUntil = 3f;
        Assert.False(Defending.AutoTackleReaches(def, open, ball, 2.5f, T));
    }

    [Fact]
    public void AWonAutoTacklePokesTheBallNearHisFacingAndTheAttackerStumblesAndIsLocked()
    {
        var w = Live(seed: 3);
        GiveAttackerTheBall(w);
        // The defender right in front of the ball, facing the attacker.
        var b = w.Ball.Position;
        w.Defender!.Position = new Vector3(b.X + 0.4f, 0f, b.Z);
        w.Defender.Facing = MathF.PI;
        Step(w);
        Assert.Contains(w.Events, e => e.Kind == EventKind.Tackle && e.Team == 1 && e.PlayerIndex == 1);
        Assert.Null(w.BallController);
        Assert.Equal(1, w.DefenderReport!.AutoWon);
        float speed = new Vector2(w.Ball.Velocity.X, w.Ball.Velocity.Z).Length();
        Assert.InRange(speed, 2.5f, T.Defending.Auto.PokeSpeedMax + 0.01f);
        float off = MathF.Abs(Movement.WrapAngle(MathF.Atan2(w.Ball.Velocity.Z, w.Ball.Velocity.X) - w.Defender.Facing)) * 180f / MathF.PI;
        Assert.InRange(off, 0f, T.Defending.Auto.PokeSpreadDeg + 15f);
        Assert.True(Defending.Stumbling(w.Attacker, w.SimTime));
        Assert.Equal(w.SimTime + Defending.StumbleSeconds(Striker, T), w.Attacker.StumbleUntil, 3);
        Assert.Equal(w.SimTime + T.Defending.TackledLockS, w.Attacker.TackledLockUntil, 3);
        // Stumbling, he is slowed to the stumble speed and takes no touch.
        Step(w, new TickIntent { MoveX = 1f, Sprint = 1f }, 20);
        Assert.True(w.Attacker.Speed <= T.Defending.StumbleSpeed + 0.5f);
        Assert.NotSame(w.Attacker, w.BallController);
    }

    [Theory]
    [InlineData(1, 0.9f)]
    [InlineData(99, 0.3f)]
    public void TheStumbleLastsByBalance(int balance, float seconds)
    {
        var s = Striker.Clone();
        s.SetRating("balance", balance);
        Assert.Equal(seconds, Defending.StumbleSeconds(s, T), 3);
    }

    // ---- lunge ----

    [Fact]
    public void ALungeGoesForTheFirstMeetingWithTheRollingBallInsideItsWindow()
    {
        var def = Player(1, CentreBack, 10f, 0f, MathF.PI);
        // A ball rolling across in front of him, out of reach now, into reach during the window.
        var ball = new Ball(T.Ball) { Position = new Vector3(9.2f, T.Ball.Radius, -2.2f), Velocity = new Vector3(0f, 0f, 6f) };
        float reach = Defending.LungeReach(def, ball.Position, T);
        Assert.True(Vector3.Distance(ball.Position, def.Position) > reach);
        Defending.StartLunge(def, ball, 100, T);
        var d = def.Def;
        Assert.Equal(TackleKind.Lunge, d.Tackle);
        int from = (int)MathF.Round(T.Defending.Stand.ExecMinS * T.TickRate), to = (int)MathF.Round(T.Defending.Stand.ExecMaxS * T.TickRate);
        Assert.InRange(d.ContactTick - 100, from, to);
        Assert.Equal(100 + to, d.EndTick);
        // The foot is planned onto the ball's place at the contact tick, and no earlier tick in the window was in reach.
        var probe = ball.Clone();
        for (int i = 1; i <= d.ContactTick - 100; i++)
        {
            probe.Step(Dt, null, null);
            float gap = TouchModel.HorizontalDistance(def.Position, probe.Position);
            if (i >= from && i < d.ContactTick - 100) Assert.True(gap > reach);
        }
        var foot = Defending.LungeFoot(def, d.ContactTick, T);
        Assert.True(TouchModel.HorizontalDistance(foot, probe.Position) < 0.02f);
        Assert.True(Defending.LungeActive(def, d.ContactTick, T));
        Assert.False(Defending.LungeActive(def, d.ContactTick - 3, T));
    }

    [Fact]
    public void ALungeWithNothingToMeetGoesAtTheBallAsFarAsHeReaches()
    {
        var def = Player(1, CentreBack, 10f, 0f, MathF.PI);
        var ball = BallAt(6f, 0f);
        Defending.StartLunge(def, ball, 0, T);
        Assert.Equal((int)MathF.Round(T.Defending.Stand.ExecMinS * T.TickRate), def.Def.ContactTick);
        Assert.Equal(Defending.LungeReach(def, ball.Position, T), def.Def.Reach, 3);
        Assert.Equal(-1f, def.Def.Dir.X, 3);
    }

    [Fact]
    public void TheLungeReachFollowsStandTackleSpeedAndTheBallsAngleOffTheRun()
    {
        var weak = CentreBack.Clone(); weak.SetRating("stand_tackle", 1);
        var strong = CentreBack.Clone(); strong.SetRating("stand_tackle", 99);
        var ahead = new Vector3(1f, 0f, 0f);
        Assert.Equal(0.9f * 0.8f, Defending.LungeReach(Player(1, weak, 0f, 0f), ahead, T), 3);
        Assert.Equal(1.5f * 0.8f, Defending.LungeReach(Player(1, strong, 0f, 0f), ahead, T), 3);
        var running = Player(1, strong, 0f, 0f);
        running.Velocity = new Vector3(7f, 0f, 0f);
        Assert.Equal(1.5f * 1.2f, Defending.LungeReach(running, ahead, T), 3);
        // The ball more than 60 degrees off his run: half the reach.
        Assert.Equal(1.5f * 1.2f * 0.5f, Defending.LungeReach(running, new Vector3(0.3f, 0f, 1f), T), 3);
    }

    [Fact]
    public void ALungeThatMeetsTheBallWinsItAndOneThatMissesStunsHim()
    {
        var w = Live(seed: 5);
        GiveAttackerTheBall(w);
        w.DefenderBotEnabled = false;
        var b = w.Ball.Position;
        // Standing a lunge away from a ball at the attacker's feet, out of the auto tackle's reach.
        w.Attacker.Velocity = Vector3.Zero;
        w.Ball.Velocity = Vector3.Zero;
        w.Defender!.Position = new Vector3(b.X + 1.05f, 0f, b.Z);
        w.Defender.Facing = MathF.PI;
        var buf = new TickIntent[w.PlayerCount];
        buf[2] = new TickIntent { Tackle = true };
        w.Step(buf);
        Assert.Equal(DefenderAction.Lunge, w.Defender.DefenderAction);
        Assert.Equal(1, w.DefenderReport!.LungesTried);
        buf[2] = default;
        bool won = false;
        for (int i = 0; i < 80 && !won; i++) { w.Step(buf); won = w.DefenderReport.LungesWon > 0; }
        Assert.True(won);
        Assert.Equal(0, w.DefenderReport.Fouls);

        // A lunge at nothing: after it he stumbles for the miss stun of a standing start.
        var m = Live(seed: 5);
        m.DefenderBotEnabled = false;
        m.Defender!.Position = new Vector3(20f, 0f, 5f);
        var mb = new TickIntent[m.PlayerCount];
        mb[2] = new TickIntent { Tackle = true };
        m.Step(mb);
        mb[2] = default;
        int ticks = (int)MathF.Round(T.Defending.Stand.ExecMaxS * T.TickRate) + 2;
        for (int i = 0; i < ticks; i++) m.Step(mb);
        Assert.Equal(DefenderAction.Stumble, m.Defender.DefenderAction);
        Assert.InRange(m.Defender.StumbleUntil - m.SimTime, 0.2f, T.Defending.Stand.MissStunS.Evaluate(0f) + 0.01f);
        Assert.False(Defending.CanTackle(m.Defender, m.SimTime));
    }

    // ---- slide ----

    [Theory]
    [InlineData(1, 2.0f)]
    [InlineData(99, 3.5f)]
    public void ASlideCarriesHimItsReachThenHeLiesAndGetsUp(int slideTackle, float reach)
    {
        var sheet = CentreBack.Clone();
        sheet.SetRating("slide_tackle", slideTackle);
        var w = Live(defender: sheet);
        w.DefenderBotEnabled = false;
        w.Defender!.Position = new Vector3(20f, 0f, 3f);
        var start = w.Defender.Position;
        var buf = new TickIntent[w.PlayerCount];
        buf[2] = new TickIntent { Slide = true };
        w.Step(buf);
        Assert.Equal(DefenderAction.Slide, w.Defender.DefenderAction);
        Assert.Equal(1, w.DefenderReport!.SlidesTried);
        buf[2] = default;
        int n = 0;
        while (w.Defender.DefenderAction == DefenderAction.Slide && n++ < 600) w.Step(buf);
        Assert.InRange(Vector3.Distance(start, w.Defender.Position), reach - 0.15f, reach + 0.05f);
        Assert.Equal(DefenderAction.Grounded, w.Defender.DefenderAction);
        Assert.True(Defending.Down(w.Defender, w.SimTime));
        int grounded = 0, rising = 0;
        for (int i = 0; i < 600 && w.Defender.DefenderAction is DefenderAction.Grounded or DefenderAction.GettingUp; i++)
        {
            if (w.Defender.DefenderAction == DefenderAction.Grounded) grounded++; else rising++;
            w.Step(buf);
        }
        Assert.InRange(grounded * Dt, T.Defending.Slide.RecoveryMissedS - 0.03f, T.Defending.Slide.RecoveryMissedS + 0.03f);
        Assert.InRange(rising * Dt, T.Defending.Slide.GetupS - 0.03f, T.Defending.Slide.GetupS + 0.03f);
        Assert.True(Defending.Free(w.Defender, w.SimTime));
    }

    [Fact]
    public void ASlideThroughTheAttackersBodyWithoutTheBallIsAFoulThatEndsTheRound()
    {
        var w = Live(seed: 2);
        GiveAttackerTheBall(w);
        w.DefenderBotEnabled = false;
        // From behind the attacker, the ball on his far side: the foot meets his body first.
        var a = w.Attacker.Position;
        w.Attacker.Velocity = Vector3.Zero;
        w.Ball.Velocity = Vector3.Zero;
        w.Ball.Position = new Vector3(a.X + 0.7f, T.Ball.Radius, a.Z);
        w.Defender!.Position = new Vector3(a.X - 2.2f, 0f, a.Z);
        w.Defender.Facing = 0f;
        var buf = new TickIntent[w.PlayerCount];
        buf[2] = new TickIntent { Slide = true };
        w.Step(buf);
        buf[2] = default;
        for (int i = 0; i < 200 && w.Outcome == Outcome.None; i++) w.Step(buf);
        Assert.Equal(Outcome.Foul, w.Outcome);
        Assert.Equal(1, w.DefenderReport!.Fouls);
        Assert.Equal(0, w.DefenderReport.SlidesWon);
    }

    // ---- barge ----

    [Fact]
    public void ABargeLandsOnlyOnAnOpponentAlongsideWithinReachAndFacingTheSameWay()
    {
        var def = Player(1, CentreBack, 10f, 0f, 0f);
        PlayerState[] Opp(float x, float z, float facing = 0f) => new[] { Player(0, Striker, x, z, facing) };
        // Square to his side (90 degrees), a metre off, running the same way.
        Assert.NotNull(Defending.BargeTarget(def, Opp(10f, 1f), T));
        Assert.NotNull(Defending.BargeTarget(def, Opp(10f, -1f), T));
        // Too far.
        Assert.Null(Defending.BargeTarget(def, Opp(10f, 1.3f), T));
        // In front of him (bearing 45), and right behind (bearing 170).
        Assert.Null(Defending.BargeTarget(def, Opp(10.7f, 0.7f), T));
        Assert.Null(Defending.BargeTarget(def, Opp(9f, 0.18f), T));
        // A little behind his shoulder (bearing about 120): still alongside.
        Assert.NotNull(Defending.BargeTarget(def, Opp(9.5f, 0.87f), T));
        // Facing the other way.
        Assert.Null(Defending.BargeTarget(def, Opp(10f, 1f, MathF.PI), T));
    }

    [Fact]
    public void TheStrongerHeavierPlayerWinsTheBargeAndTheOtherStumblesAndLosesTheBall()
    {
        Assert.True(Defending.BargeRatio(CentreBack, Striker) > 1f);
        var w = Live(seed: 4);
        GiveAttackerTheBall(w);
        w.DefenderBotEnabled = false;
        var a = w.Attacker;
        w.Defender!.Position = new Vector3(a.Position.X, 0f, a.Position.Z + 0.9f);
        w.Defender.Facing = a.Facing;
        // (the ball on the attacker's far side, out of the auto tackle's reach)
        w.Ball.Position = new Vector3(a.Position.X + 0.3f, T.Ball.Radius, a.Position.Z - 0.6f);
        w.Ball.Velocity = Vector3.Zero;
        float z0 = a.Position.Z;
        var buf = new TickIntent[w.PlayerCount];
        buf[2] = new TickIntent { Barge = true };
        w.Step(buf);
        Assert.Contains(w.Events, e => e.Kind == EventKind.Barge);
        Assert.Equal(1, w.DefenderReport!.Barges);
        Assert.Equal(1, w.DefenderReport.BargesWon);
        Assert.True(Defending.Stumbling(a, w.SimTime));
        Assert.Null(w.BallController);
        Assert.Equal(DefenderAction.Barge, w.Defender.DefenderAction);
        buf[2] = default;
        for (int i = 0; i < 30; i++) w.Step(buf);
        Assert.True(a.Position.Z < z0 - 0.05f);   // pushed away from the barger

        // The weaker, lighter barger loses: he stumbles and the attacker keeps the ball.
        var feeble = CentreBack.Clone();
        feeble.SetRating("strength", 20);
        feeble.WeightKg = 60f;
        Assert.True(Defending.BargeRatio(feeble, Striker) < 1f);
        var l = Live(seed: 4, defender: feeble);
        GiveAttackerTheBall(l);
        l.DefenderBotEnabled = false;
        l.Defender!.Position = new Vector3(l.Attacker.Position.X, 0f, l.Attacker.Position.Z + 0.9f);
        l.Defender.Facing = l.Attacker.Facing;
        l.Ball.Position = new Vector3(l.Attacker.Position.X + 0.3f, T.Ball.Radius, l.Attacker.Position.Z - 0.6f);
        l.Ball.Velocity = Vector3.Zero;
        var lb = new TickIntent[l.PlayerCount];
        lb[2] = new TickIntent { Barge = true };
        l.Step(lb);
        Assert.True(Defending.Stumbling(l.Defender, l.SimTime));
        Assert.False(Defending.Stumbling(l.Attacker, l.SimTime));
        Assert.Same(l.Attacker, l.BallController);
        Assert.Equal(0, l.DefenderReport!.BargesWon);
    }

    // ---- block ----

    [Theory]
    [InlineData(1, 0.6f)]
    [InlineData(99, 0.25f)]
    public void TheLegReachGrowsFromTheBodyToItsFullLengthOverTheBlockingTime(int blocking, float grow)
    {
        var s = CentreBack.Clone();
        s.SetRating("blocking", blocking);
        Assert.Equal(T.Defending.BodyRadiusM, Defending.BlockReachAfter(s, 0f, T), 3);
        Assert.Equal((T.Defending.BodyRadiusM + T.Defending.Block.ReachM) / 2f, Defending.BlockReachAfter(s, grow / 2f, T), 3);
        Assert.Equal(T.Defending.Block.ReachM, Defending.BlockReachAfter(s, grow, T), 3);
        Assert.Equal(T.Defending.Block.ReachM, Defending.BlockReachAfter(s, grow * 3f, T), 3);
    }

    [Fact]
    public void ADeflectionKeepsLessOfAFasterBallIsMirroredAndIsNoSteeperThanTheLiftCap()
    {
        var n = new Vector3(-1f, 0f, 0f);
        var slow = Defending.Deflection(new Vector3(5f, 0f, 0f), n, T)!.Value;
        var fast = Defending.Deflection(new Vector3(30f, 0f, 0f), n, T)!.Value;
        Assert.Equal(-5f * 0.6f, slow.X, 3);
        Assert.Equal(-30f * 0.35f, fast.X, 3);
        // At an angle it leaves at the mirrored angle.
        var skew = Defending.Deflection(new Vector3(10f, 0f, 10f), n, T)!.Value;
        Assert.True(skew.X < 0f && skew.Z > 0f);
        Assert.Equal(-skew.X, skew.Z, 3);
        // Moving away from him: no deflection.
        Assert.Null(Defending.Deflection(new Vector3(-5f, 0f, 0f), n, T));
        // A ball driven down into a foot comes off no steeper than the cap.
        var up = Defending.Deflection(new Vector3(3f, -10f, 0f), Vector3.Normalize(new Vector3(-0.2f, 1f, 0f)), T)!.Value;
        float lift = MathF.Atan2(up.Y, MathF.Sqrt(up.X * up.X + up.Z * up.Z)) * 180f / MathF.PI;
        Assert.InRange(lift, 0f, T.Defending.Block.LiftDeg + 0.01f);
    }

    [Fact]
    public void AShotIntoHisBodyIsBlockedAndStaysInPlay()
    {
        var w = Live(seed: 9);
        w.DefenderBotEnabled = false;
        // The attacker shoots at once from the spot with the defender three metres in front on the line to goal.
        w.Defender!.Position = new Vector3(w.Ball.Position.X + 3f, 0f, 0f);
        w.Defender.Facing = MathF.PI;
        var buf = new TickIntent[w.PlayerCount];
        bool blocked = false, set = false;
        for (int i = 0; i < 400 && w.Outcome == Outcome.None && !blocked; i++)
        {
            buf[0] = i < 30 ? new TickIntent { ShootHeld = true, Driven = true } : new TickIntent { Driven = true };
            w.Step(buf);
            set |= w.Defender.DefenderAction == DefenderAction.Block && w.Defender.BlockReach > T.Defending.BodyRadiusM;
            blocked = w.Events.Any(e => e.Kind == EventKind.Block);
        }
        Assert.True(set);
        Assert.True(blocked);
        Assert.Single(w.Shots);
        Assert.Equal(1, w.DefenderReport!.Blocks);
        Assert.Equal(RoundState.Live, w.State);
        Assert.True(w.Ball.Velocity.X < 0f);   // back off him
    }

    [Fact]
    public void ADeflectionIntoHisOwnGoalIsAGoal()
    {
        var w = Live(seed: 1);
        w.DefenderBotEnabled = false;
        float gl = w.Pitch.GoalLineX, r = T.Ball.Radius;
        // The defender just off his line; the ball driven across the goal mouth, clipping the goal-side of his body.
        w.Defender!.Position = new Vector3(gl - 0.75f, 0f, 0f);
        w.Keeper.Position = new Vector3(gl - 0.6f, 0f, 3.2f);
        w.Ball.Position = new Vector3(gl - 0.75f + 0.30f, r, -2f);
        w.Ball.Velocity = new Vector3(0f, 0f, 20f);
        w.BallPlayed = true;
        var buf = new TickIntent[w.PlayerCount];
        bool hit = false;
        for (int i = 0; i < 60 && w.Outcome == Outcome.None; i++)
        {
            w.Step(buf);
            hit |= w.Events.Any(e => e.Kind == EventKind.Block);
        }
        Assert.True(hit);
        Assert.Equal(Outcome.Goal, w.Outcome);
    }

    // ---- contacts ----

    [Fact]
    public void OverlappingBodiesArePushedApartTheLighterOneFurther()
    {
        var light = TestData.Sheet(weight: 60f);
        var heavy = TestData.Sheet(weight: 90f);
        var a = Player(0, light, 10f, 0f);
        var b = Player(1, heavy, 10.2f, 0f);
        Assert.True(Contacts.Separate(a, b, 5, 0f, T, null));
        Assert.Equal(2f * T.Defending.BodyRadiusM, b.Position.X - a.Position.X, 3);
        float movedA = 10f - a.Position.X, movedB = b.Position.X - 10.2f;
        Assert.Equal(0.4f, movedA + movedB, 3);
        Assert.Equal(90f / 60f, movedA / movedB, 2);
        Assert.Equal(5, a.Def.BumpTick);
        Assert.Equal(1f, a.Def.BumpDir.X, 3);
        Assert.Equal(-1f, b.Def.BumpDir.X, 3);
        // Apart already: nothing happens.
        Assert.False(Contacts.Separate(a, b, 6, 0f, T, null));
        // On the very same spot they still come apart.
        var c = Player(0, light, 4f, 4f);
        var d = Player(1, light, 4f, 4f);
        Assert.True(Contacts.Separate(c, d, 1, 0f, T, null));
        Assert.Equal(2f * T.Defending.BodyRadiusM, Vector3.Distance(c.Position, d.Position), 3);
    }

    [Fact]
    public void TheStartOfARoundWithBothOnOneSpotSeparatesThem()
    {
        var sc = TestData.Scenario("defender_duel");
        sc.Defender = new StartSpot { X = sc.Attacker.X, Z = sc.Attacker.Z };
        var w = new World(T, sc, Striker, Wall, 1, CentreBack);
        w.Begin();
        Step(w);
        Assert.True(Vector3.Distance(w.Attacker.Position, w.Defender!.Position) >= 2f * T.Defending.BodyRadiusM - 1e-3f);
    }

    [Theory]
    [InlineData(-9.7f, 0f, -1f)]     // into the side board
    [InlineData(0.3f, 1f, 0f)]       // into the back board, side by side
    public void PushedIntoABoardTogetherTheyStayInsideAndApart(float coord, float alongX, float pushZ)
    {
        var pitch = new Pitch(Duel.Pitch, T.Frame, T.Arena);
        bool side = pushZ != 0f;
        var a = Player(0, Striker, side ? 14f : coord, side ? coord : 2f);
        var b = Player(1, CentreBack, side ? 14f : coord + 0.05f * alongX, side ? coord + 0.25f : 2.1f);
        for (int i = 0; i < 5; i++) Contacts.Separate(a, b, i, 0f, T, pitch);
        foreach (var p in new[] { a, b })
        {
            var pos = p.Position;
            var vel = Vector3.Zero;
            Assert.False(pitch.ClampPlayer(ref pos, ref vel));     // neither was pushed through a board
        }
        Assert.True(Vector3.Distance(a.Position, b.Position) >= 2f * T.Defending.BodyRadiusM - 2e-3f);
    }

    [Fact]
    public void AStumblingPlayerGivesWay()
    {
        var a = Player(0, Striker, 10f, 0f);
        var b = Player(1, CentreBack, 10.3f, 0f, MathF.PI);
        a.StumbleUntil = 5f;
        b.Velocity = new Vector3(-5f, 0f, 0f);
        Contacts.Separate(a, b, 1, 1f, T, null);
        Assert.True(10f - a.Position.X > 3f * (b.Position.X - 10.3f));
        Assert.Equal(-5f, b.Velocity.X, 3);       // the one on his feet keeps his run
    }

    // ---- order within a tick ----

    [Fact]
    public void ATouchWithTheDefenderOnTheBallIsTackledOnThatSameTickButAStruckShotFlies()
    {
        // Touch: the attacker's first touch and the tackle land on one tick (the attacker steps first, then the defender).
        var w = Live(seed: 6);
        w.DefenderBotEnabled = false;
        w.Defender!.Position = new Vector3(w.Ball.Position.X + 0.1f, 0f, 0.42f);
        w.Defender.Facing = MathF.PI;
        var buf = new TickIntent[w.PlayerCount];
        buf[0] = new TickIntent { MoveX = 1f };
        bool both = false;
        for (int i = 0; i < 40 && !both; i++)
        {
            w.Step(buf);
            bool touch = w.Events.Any(e => e.Kind == EventKind.Touch && e.Team == 0);
            bool tackle = w.Events.Any(e => e.Kind == EventKind.Tackle);
            Assert.False(tackle && !touch && w.DefenderReport!.AutoWon == 1 && w.Tick == 1);
            both = touch && tackle;
        }
        Assert.True(both);

        // Strike: with the defender standing right beside the ball, the shot struck on a tick is gone before he acts on it.
        var s = Live(seed: 6);
        s.DefenderBotEnabled = false;
        // (on the side the shot does not go to: from a central ball the scripted aim, like a released stick, is not at him)
        s.Defender!.Position = new Vector3(s.Ball.Position.X - 0.1f, 0f, -0.44f);
        s.Defender.Facing = MathF.PI / 2f;
        var sb = new TickIntent[s.PlayerCount];
        bool struck = false;
        for (int i = 0; i < 200 && !struck; i++)
        {
            sb[0] = i < 20 ? new TickIntent { ShootHeld = true } : default;
            s.Step(sb);
            struck = s.Events.Any(e => e.Kind == EventKind.Strike);
            Assert.DoesNotContain(s.Events, e => e.Kind == EventKind.Tackle);
        }
        Assert.True(struck);
        Assert.Equal(RoundState.Flight, s.State);
        Assert.True(s.Ball.Velocity.Length() > 10f);
        Assert.Equal(0, s.DefenderReport!.AutoWon);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void KeepersHandsAndDefendersFootOnTheBallTogetherTheKeeperHasItInsideHisAreaTheDefenderOutside(bool inside)
    {
        var pitch = new Pitch(Duel.Pitch, T.Frame, T.Arena);
        float x = inside ? pitch.PenaltyAreaLineX + 3f : pitch.PenaltyAreaLineX - 3f;
        var ball = new Ball(T.Ball) { Position = new Vector3(x, T.Ball.Radius, 0f), Velocity = new Vector3(6f, 0f, 0f) };
        var at = new Vector3(x + 0.22f, T.Ball.Radius, 0f);
        var hands = new SweepSphere(at, 0.2f);
        var foot = new[] { new BallObstacle(new SweepCapsule(at, at, 0.2f), Defending.FootObstacle) };
        var res = ball.Step(Dt, pitch, hands, null, null, foot);
        Assert.Equal(inside, res.SphereHit);
        Assert.Equal(!inside, res.ObstacleHit);
        if (!inside) Assert.Equal(Defending.FootObstacle, res.ObstacleId);
        // Alone, each of them meets it wherever it is.
        var alone = new Ball(T.Ball) { Position = new Vector3(x, T.Ball.Radius, 0f), Velocity = new Vector3(6f, 0f, 0f) };
        Assert.True(alone.Step(Dt, pitch, null, null, null, foot).ObstacleHit);
    }

    [Fact]
    public void AnObstacleCountsOnlyUpToItsHeight()
    {
        var leg = new[] { new BallObstacle(new SweepCapsule(new Vector3(1f, 0f, 0f), new Vector3(1f, 0f, 0f), 1f), Defending.LegObstacle, 0.75f) };
        var low = new Ball(T.Ball) { Position = new Vector3(-0.1f, 0.3f, 0f), Velocity = new Vector3(30f, 3f, 0f) };
        Assert.True(low.Step(Dt, null, null, null, null, leg).ObstacleHit);
        var high = new Ball(T.Ball) { Position = new Vector3(0.4f, 0.9f, 0f), Velocity = new Vector3(30f, 3f, 0f) };
        Assert.False(high.Step(Dt, null, null, null, null, leg).ObstacleHit);
    }

    // ---- the keeper's yield ----

    [Fact]
    public void TheDefenderCoversOnlyInTheConeNearerTheBallOnHisFeetAndNotBeaten()
    {
        var w = Live();
        GiveAttackerTheBall(w);
        var k = w.Keeper.Position;
        var b = w.Ball.Position;
        var toBall = Vector3.Normalize(new Vector3(b.X - k.X, 0f, b.Z - k.Z));
        var square = new Vector3(-toBall.Z, 0f, toBall.X);
        // On the line, between keeper and ball.
        w.Defender!.Position = k + toBall * 8f;
        Assert.True(w.DefenderCovers());
        // 20 degrees off: still inside the 25-degree cone. 35 degrees: outside.
        w.Defender.Position = k + (toBall * MathF.Cos(20f * MathF.PI / 180f) + square * MathF.Sin(20f * MathF.PI / 180f)) * 8f;
        Assert.True(w.DefenderCovers());
        w.Defender.Position = k + (toBall * MathF.Cos(35f * MathF.PI / 180f) + square * MathF.Sin(35f * MathF.PI / 180f)) * 8f;
        Assert.False(w.DefenderCovers());
        // Beyond the ball (beaten: further from goal than the ball by more than the overshoot).
        w.Defender.Position = new Vector3(b.X - 2f, 0f, b.Z);
        Assert.False(w.DefenderCovers());
        // On the ground or stumbling he covers nothing.
        w.Defender.Position = k + toBall * 8f;
        w.Defender.StumbleUntil = w.SimTime + 1f;
        Assert.False(w.DefenderCovers());
        w.Defender.StumbleUntil = -100f;
        w.Defender.Def.GetUpUntil = w.SimTime + 1f;
        Assert.False(w.DefenderCovers());
    }

    [Fact]
    public void TheKeeperHoldsHisArcWhileTheDefenderCoversAndChargesWhenHeDoesNot()
    {
        // The same breakaway run at the keeper, the defender kept either on the keeper's line to the ball or far out wide.
        bool Charged(bool covering)
        {
            var w = Live(seed: 11);
            w.DefenderBotEnabled = false;
            var buf = new TickIntent[w.PlayerCount];
            for (int i = 0; i < 1500 && w.Outcome == Outcome.None; i++)
            {
                var k = w.Keeper.Position;
                var b = w.Ball.Position;
                var dir = Vector3.Normalize(new Vector3(b.X - k.X, 0f, b.Z - k.Z));
                float gap = MathF.Sqrt((b.X - k.X) * (b.X - k.X) + (b.Z - k.Z) * (b.Z - k.Z));
                // (covering: between them but never within reach of the ball; else: against the far side board)
                w.Defender!.Position = covering ? k + dir * MathF.Max(0.8f, gap - 2.5f) : new Vector3(3f, 0f, 9f);
                w.Defender.Velocity = Vector3.Zero;
                bool near = w.Pitch.GoalLineX - b.X < 9f;
                buf[0] = near ? default : new TickIntent { MoveX = 1f, Sprint = 0.6f };
                w.Step(buf);
                if (w.KeeperReport.Charged) return true;
                if (near && w.SimTime > 8f) break;
            }
            return false;
        }
        Assert.True(Charged(covering: false));
        Assert.False(Charged(covering: true));
    }

    // ---- the loose ball he controls ----

    [Fact]
    public void ALooseBallSlowAtHisFeetForLongEnoughEndsTheRoundDispossessed()
    {
        var w = Live(seed: 8);
        w.DefenderBotEnabled = false;
        w.Attacker.Position = new Vector3(3f, 0f, -6f);
        w.Defender!.Position = new Vector3(15f, 0f, 2f);
        w.Ball.Position = new Vector3(15.4f, T.Ball.Radius, 2f);
        w.Ball.Velocity = Vector3.Zero;
        w.BallPlayed = true;
        int ticks = 0;
        while (w.Outcome == Outcome.None && ticks < 200) { Step(w); ticks++; }
        Assert.Equal(Outcome.Dispossessed, w.Outcome);
        Assert.InRange(ticks * Dt, T.Defender.ControlS - 0.02f, T.Defender.ControlS + 0.02f);
        Assert.True(w.DefenderReport!.Collected);
        while (w.State != RoundState.Report) Step(w);
        Assert.Equal(Outcome.Dispossessed, w.Report!.Outcome);
        Assert.True(w.Report.Defender!.Collected);
        Assert.Contains("\"outcome\": \"dispossessed\"", w.Report.ToJson());
    }

    [Theory]
    [InlineData(0.9f, 0f, 0f)]     // out of his control distance
    [InlineData(0.4f, 5f, 0f)]     // too fast against him
    [InlineData(0.4f, 0f, 0.6f)]   // off the ground
    public void ABallOutOfReachTooFastOrInTheAirIsNotControlled(float gap, float speed, float height)
    {
        var w = Live(seed: 8);
        w.DefenderBotEnabled = false;
        w.Attacker.Position = new Vector3(3f, 0f, -6f);
        w.Defender!.Position = new Vector3(15f, 0f, 2f);
        w.Ball.Position = new Vector3(15f + gap, T.Ball.Radius + height, 2f);
        w.Ball.Velocity = new Vector3(0f, 0f, speed);
        w.BallPlayed = true;
        Step(w, default, 4);
        Assert.Equal(Outcome.None, w.Outcome);
        Assert.Equal(0f, w.Defender.Def.ControlFor, 3);
    }

    [Fact]
    public void ABallOnTheSpotBeforeAnyoneHasPlayedItIsNotHisToCollect()
    {
        var w = Live(seed: 8);
        w.Defender!.Position = new Vector3(w.Ball.Position.X + 0.4f, 0f, 0f);
        w.Attacker.Position = new Vector3(1f, 0f, 6f);
        Step(w, default, 40);
        Assert.Equal(Outcome.None, w.Outcome);
        Assert.NotEqual(DefenderMode.Collect, w.DefenderBot!.Mode);
    }

    [Fact]
    public void EquallyNearALooseBallTheCoinDecidesTheSameWayEveryTimeForASeed()
    {
        string Run(ulong seed)
        {
            var w = Live(seed: seed);
            w.Attacker.Position = new Vector3(14.5f, 0f, 0f);
            w.Attacker.Facing = 0f;
            w.Defender!.Position = new Vector3(15.5f, 0f, 0f);
            w.Defender.Facing = MathF.PI;
            w.Ball.Position = new Vector3(15f, T.Ball.Radius, 0f);
            w.Ball.Velocity = Vector3.Zero;
            w.BallPlayed = true;
            var rec = new ReplayRecorder(w, Striker.Id, Wall.Id, Duel.Id, CentreBack.Id);
            w.Recorder = rec;
            var log = new System.Text.StringBuilder();
            for (int i = 0; i < 60 && w.Outcome == Outcome.None; i++)
            {
                Step(w);
                foreach (var e in w.Events) log.Append($"{w.Tick}:{e.Kind}:{e.Team};");
            }
            return $"{w.Outcome}|{w.BallController?.Team}|{log}|{rec.Replay.ToJson().Length}";
        }
        var results = new HashSet<string>();
        for (ulong seed = 1; seed <= 24; seed++)
        {
            string a = Run(seed), b = Run(seed);
            Assert.Equal(a, b);
            results.Add(a.Split('|')[0] + a.Split('|')[1]);
        }
        // Over the seeds the coin falls both ways: sometimes the attacker has it first, sometimes the defender does.
        Assert.True(results.Count >= 2, string.Join(", ", results));
    }

    // ---- determinism and replays ----

    static RoundReport Scripted(ulong seed, out Replay replay, ScriptedAttacker.AttackStyle style = ScriptedAttacker.AttackStyle.Cut) =>
        RoundRunner.RunToReport(T, Duel, Striker, Wall, seed, out replay, new ScriptedAttacker { Style = style }, CentreBack);

    [Theory]
    [InlineData(ScriptedAttacker.AttackStyle.Straight)]
    [InlineData(ScriptedAttacker.AttackStyle.Cut)]
    [InlineData(ScriptedAttacker.AttackStyle.Skill)]
    [InlineData(ScriptedAttacker.AttackStyle.Shoot)]
    public void ADefenderRoundIsTheSameTwiceAndItsReplayReproducesIt(ScriptedAttacker.AttackStyle style)
    {
        for (ulong seed = 20; seed < 26; seed++)
        {
            var a = Scripted(seed, out var ra, style);
            var b = Scripted(seed, out var rb, style);
            Assert.Equal(a.ToJson(), b.ToJson());
            Assert.Equal(ra.ToJson(), rb.ToJson());
            Assert.NotNull(a.Defender);
            Assert.Equal(3, ra.PlayersPerTick);
            Assert.Equal("centre_back", ra.DefenderId);
            Assert.Equal(DataLoader.DefendingHash(T), ra.DefendingHash);
            var loaded = Replay.FromJson(ra.ToJson(), out var err);
            Assert.Null(err);
            Assert.True(loaded!.MatchesTuning(T));
            Assert.Equal(a.ToJson(), loaded.Run(T, Duel, Striker, Wall, CentreBack).ToJson());
        }
    }

    [Fact]
    public void TheReplayHoldsTheBotsIntentsAndPlayingThemBackWithTheBotOffGivesTheSameRound()
    {
        int pressed = 0;
        for (ulong seed = 30; seed < 40; seed++)
        {
            var original = Scripted(seed, out var replay);
            // The defender's recorded intents are the bot's: he moves, and somewhere over these rounds he presses a button.
            var mine = Enumerable.Range(0, replay.TickCount).Select(t => replay.Intents[t * 3 + 2]).ToList();
            Assert.Contains(mine, i => i.Mx != 0f || i.Mz != 0f);
            pressed += mine.Count(i => i.Tackle || i.Slide || i.Jockey);
            var w = new World(T, Duel, Striker, Wall, replay.Seed, CentreBack) { DefenderBotEnabled = false };
            w.Begin();
            var buf = new TickIntent[3];
            for (int t = 0; t < replay.TickCount; t++)
            {
                for (int p = 0; p < 3; p++) buf[p] = replay.Intents[t * 3 + p].ToIntent();
                w.Step(buf);
            }
            Array.Clear(buf);
            for (int i = 0; i < 5000 && w.State != RoundState.Report; i++) w.Step(buf);
            // (the bot was not there to count its own active time; everything the world did is the same)
            var again = w.Report!;
            Assert.Equal(original.Outcome, again.Outcome);
            Assert.Equal(original.Ticks, again.Ticks);
            Assert.Equal(original.Shots.Count, again.Shots.Count);
            Assert.Equal(original.Defender!.AutoWon, again.Defender!.AutoWon);
            Assert.Equal(original.Defender.LungesTried, again.Defender.LungesTried);
            Assert.Equal(original.Defender.SlidesTried, again.Defender.SlidesTried);
            Assert.Equal(original.Defender.Collected, again.Defender.Collected);
        }
        Assert.True(pressed > 0);
    }

    [Theory]
    [InlineData("penalty")]
    [InlineData("breakaway")]
    [InlineData("loose_parry")]
    public void ARoundWithoutADefenderCarriesNothingOfHim(string scenario)
    {
        var report = RoundRunner.RunToReport(T, TestData.Scenario(scenario), Striker, Wall, 3, out var replay);
        Assert.Null(report.Defender);
        Assert.DoesNotContain("defender", report.ToJson());
        string json = replay.ToJson();
        Assert.Equal(2, replay.PlayersPerTick);
        foreach (var word in new[] { "defender", "defending", "jockey", "tackle", "slide", "barge" }) Assert.DoesNotContain(word, json);
    }

    [Fact]
    public void TheScriptedStylesChangeNothingWithoutADefender()
    {
        var sc = TestData.Scenario("breakaway");
        var plain = RoundRunner.RunToReport(T, sc, Striker, Wall, 9, out var a);
        foreach (var style in Enum.GetValues<ScriptedAttacker.AttackStyle>())
        {
            var styled = RoundRunner.RunToReport(T, sc, Striker, Wall, 9, out var b, new ScriptedAttacker { Style = style });
            Assert.Equal(plain.ToJson(), styled.ToJson());
            Assert.Equal(a.ToJson(), b.ToJson());
        }
    }

    [Fact]
    public void OverManyRoundsNothingBecomesNotANumberAndEveryRoundEnds()
    {
        var seen = new HashSet<Outcome>();
        foreach (var style in Enum.GetValues<ScriptedAttacker.AttackStyle>())
            for (ulong seed = 100; seed < 140; seed++)
            {
                var r = Scripted(seed, out _, style);
                Assert.NotEqual(Outcome.Out, r.Outcome);
                Assert.NotEqual(Outcome.None, r.Outcome);
                seen.Add(r.Outcome);
            }
        Assert.Contains(Outcome.Dispossessed, seen);
        Assert.Contains(Outcome.Goal, seen);
    }

    // ---- the duel's measured bands (the defender probe runs the same rounds, 2,000 per style) ----

    [Fact]
    public void TheDefenderWinsTheBallMostFromAStraightRunLessFromACutLeastFromASkillMove()
    {
        const int n = 400;
        float Won(ScriptedAttacker.AttackStyle style)
        {
            int won = 0;
            Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
            {
                var r = RoundRunner.RunToReport(LegacyT, Duel, Striker, Wall, 1 + (ulong)i, out _, new ScriptedAttacker { Style = style }, CentreBack);
                if (r.Outcome == Outcome.Dispossessed && r.Shots.Count == 0) Interlocked.Increment(ref won);
            });
            return 100f * won / n;
        }
        float straight = Won(ScriptedAttacker.AttackStyle.Straight), cut = Won(ScriptedAttacker.AttackStyle.Cut), skill = Won(ScriptedAttacker.AttackStyle.Skill);
        // The probe's bands, widened by the sampling error of 400 rounds.
        Assert.InRange(straight, 65f - 6f, 85f + 6f);
        Assert.InRange(cut, 30f - 6f, 45f + 6f);
        Assert.InRange(skill, 20f - 6f, 35f + 6f);
        Assert.True(straight > cut && cut > skill, $"straight {straight} cut {cut} skill {skill}");
    }

    // ---- jockeying ----

    [Fact]
    public void JockeyingHeMovesAtTheJockeyPaceFacingThePointHeWatches()
    {
        var p = Player(1, CentreBack, 10f, 0f, MathF.PI);
        var face = new Vector2(0f, 0f);
        var it = new TickIntent { MoveZ = 1f, Jockey = true };
        for (int i = 0; i < 240; i++) Movement.StepJockey(p, it, face, float.PositiveInfinity, Dt, i * Dt, T, null);
        Assert.True(p.Jockeying);
        Assert.Equal(T.Defending.JockeySpeed.ForRating(CentreBack.Jockeying), p.Speed, 2);
        Assert.True(p.Velocity.Z > 0f && MathF.Abs(p.Velocity.X) < 0.05f);
        // He still faces the point, not the way he steps.
        float want = MathF.Atan2(-p.Position.Z, -p.Position.X);
        Assert.Equal(0f, Movement.WrapAngle(p.Facing - want), 1);
        // With sprint the pace is the sprint-jockey one.
        it.Sprint = 1f;
        for (int i = 0; i < 240; i++) Movement.StepJockey(p, it, face, float.PositiveInfinity, Dt, 2f + i * Dt, T, null);
        Assert.Equal(T.Defending.SprintJockeySpeed.ForRating(CentreBack.Jockeying), p.Speed, 2);
        // Released, he slows at the jockey deceleration.
        float v0 = p.Speed;
        Movement.StepJockey(p, new TickIntent { Jockey = true }, face, float.PositiveInfinity, Dt, 5f, T, null);
        Assert.Equal(v0 - T.Defending.JockeyDecel * Dt, p.Speed, 3);
    }

    [Fact]
    public void RunningFastHisHeadingComesRoundSlowerThanAtAJog()
    {
        float TurnIn(float speed)
        {
            var p = Player(1, CentreBack, 10f, 0f, 0f);
            p.Velocity = new Vector3(speed, 0f, 0f);
            p.RunSpeed = speed;
            Movement.StepOffBall(p, new TickIntent { MoveZ = 1f, Sprint = 1f }, null, float.PositiveInfinity, Dt, 1f, T, null);
            return MathF.Abs(p.Facing);
        }
        float accel = T.Defending.RunTurnAccel.ForRating(CentreBack.Agility);
        Assert.True(TurnIn(7f) <= accel / 7f * Dt + 1e-5f);
        Assert.True(TurnIn(3f) > TurnIn(7f));
    }
}
