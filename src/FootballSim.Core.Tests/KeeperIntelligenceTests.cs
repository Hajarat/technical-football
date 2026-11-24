using System.Numerics;
using FootballSim.Core;
using Xunit.Abstractions;

namespace FootballSim.Core.Tests;

/// <summary>D2, keeper intelligence: closing down, winning a long touch, staying big, planting, chips and being taken round.</summary>
public class KeeperIntelligenceTests(ITestOutputHelper output)
{
    static readonly Tuning T = new();
    static readonly Pitch P = new(new PitchSpec(), T.Frame);
    static float Dt => T.Dt;
    static readonly PlayerSheet Striker = TestData.Player("striker_a");
    /// <summary>The legacy keeper (keeper.brain legacy), for the charge and jockey he alone makes.</summary>
    static readonly Tuning Legacy = new() { Keeper = { Brain = "legacy" } };

    static (KeeperBrain Brain, PlayerState Keeper, Ball Ball) Rig(PlayerSheet sheet, Vector3 ball, Vector3 keeperAt, Tuning? t = null)
    {
        t ??= T;
        var b = new Ball(t.Ball) { Position = ball };
        var k = new PlayerState(1, 0, PlayerRole.Keeper, sheet) { Position = keeperAt, Facing = MathF.PI };
        return (new KeeperBrain(k, P, t, false, Striker), k, b);
    }

    static float Depth(PlayerState k) => P.GoalLineX - k.Position.X;

    /// <summary>The tuning with charging switched off (he never charges an attacker on the ball): closing down and racing only.</summary>
    static Tuning NoCharge(Tuning? t = null)
    {
        t ??= new Tuning();
        t.Keeper.ChargeTime = new RatingRange(0f, 0f);
        t.Keeper.EngageRange = 0f;
        return t;
    }

    /// <summary>An attacker on the ball, touching it every step, running at <paramref name="speed"/> along +X.</summary>
    static KeeperSituation Dribbling(Vector3 ball, float speed, float nextTouch = 0.05f, int touchTick = 1) => new()
    {
        AttackerNextTouch = nextTouch, AttackerTouchTick = touchTick, AttackerHasBall = true, StrikeIn = float.PositiveInfinity,
        AttackerPosition = ball - new Vector3(0.5f, ball.Y, 0f), AttackerVelocity = new Vector3(speed, 0f, 0f),
    };

    // ---- closing down ----

    [Fact]
    public void ClosingDownHeComesFurtherOutAsTheAttackerApproachesAndStaysWithinHisChipSafeDepth()
    {
        void Run(Tuning t, string keeperId, ulong seed, bool capBinds)
        {
            var s = TestData.Player(keeperId);
            var start = new Vector3(P.GoalLineX - 28f, 0.11f, 0f);
            var (brain, keeper, ball) = Rig(s, start, Keeper.ArcTarget(start, P, t, s), t);
            var rng = new SimRng(seed);
            float sd = Keeper.ChipJudgeErrorSd(s, t);
            var marks = new[] { 24f, 20f, 16f, 13f, 11f };
            var depths = new List<float>();
            int next = 0;
            const float v = 6f;
            ball.Velocity = new Vector3(v, 0f, 0f);
            while (next < marks.Length)
            {
                ball.Position += new Vector3(v * Dt, 0f, 0f);
                brain.Update(Dt, ball, rng, Dribbling(ball.Position, v));
                float d = P.GoalLineX - ball.Position.X;
                // Never beyond what he judges he could recover from a chip from here (his judging error is at most 2 sd).
                float safe = Keeper.ChipSafeDepth(s, Striker, t, P, d);
                Assert.True(Depth(keeper) <= safe * (1f + 2f * sd) + 0.35f, $"{keeperId} {d:F1} m: depth {Depth(keeper):F2} safe {safe:F2}");
                if (d <= marks[next]) { depths.Add(Depth(keeper)); next++; }
            }
            output.WriteLine($"{keeperId} cap {capBinds}: " + string.Join(", ", marks.Select((m, i) => $"{m} m -> {depths[i]:F2}")));
            // Unconstrained, he only ever comes further out; held by his chip-safe depth he may give ground as it shrinks.
            if (!capBinds)
                for (int i = 1; i < depths.Count; i++) Assert.True(depths[i] >= depths[i - 1] - 0.15f, $"depth fell: {depths[i - 1]:F2} -> {depths[i]:F2}");
            float arc = P.GoalLineX - Keeper.ArcTarget(new Vector3(P.GoalLineX - 11f, 0.11f, 0f), P, t, s).X;
            if (!capBinds) Assert.True(depths[^1] > arc + 1.5f, $"at 11 m only {depths[^1]:F2} out (arc {arc:F2})");
            else Assert.True(depths[^1] < Keeper.CloseDownDistance(s, t, 11f, arc) - 0.5f, $"the chip cap did not hold him back: {depths[^1]:F2}");
        }
        // Closing down without charging (charging is tested below).
        var t = NoCharge();
        Run(t, "the_wall", 1, false);
        Run(t, "stand_in", 2, false);
        // A keeper who cannot get back (slow backpedal, low leap): his chip-safe depth holds him short of where he would close down to.
        var slow = NoCharge();
        slow.Keeper.BackpedalSpeed = new RatingRange(1f, 1f);
        slow.Keeper.ChipReachHeightFraction = 1.0f;
        slow.Keeper.ChipTakeHeightFraction = 0.9f;
        Run(slow, "the_wall", 3, true);
    }

    [Fact]
    public void ChipSafeDepthGrowsWithTheBallsDistanceOnceAChipCanClearHimAndIsShorterForASlowerKeeper()
    {
        var wall = TestData.Player("the_wall");
        var poor = TestData.Player("stand_in");
        float at15 = Keeper.ChipSafeDepth(wall, Striker, T, P, 15f), at25 = Keeper.ChipSafeDepth(wall, Striker, T, P, 25f);
        Assert.True(at15 > 2f && at15 < 15f, $"15 m: {at15}");
        Assert.True(at25 > at15, $"15 m {at15}, 25 m {at25}");
        Assert.True(Keeper.ChipSafeDepth(poor, Striker, T, P, 20f) < Keeper.ChipSafeDepth(wall, Striker, T, P, 20f));
        // From close in, a chip at the ideal charge cannot rise over his leap before it reaches him: no chip limit.
        Assert.True(Keeper.ChipSafeDepth(wall, Striker, T, P, 8f) >= 7f);
    }

    // ---- winning the ball between touches ----

    /// <summary>
    /// A sprint touch: the ball rolls on toward goal ahead of the attacker, who next meets it <paramref name="nextTouch"/> seconds
    /// later. Returns true if the keeper got to the ball first (held or blocked it) before that touch.
    /// </summary>
    static bool RaceForTouch(Tuning t, Vector3 keeperAt, float ballFromGoal, float nextTouch, ulong seed, out bool rushed)
    {
        var s = TestData.Player("the_wall");
        var start = new Vector3(P.GoalLineX - ballFromGoal, 0.11f, 0f);
        var (brain, keeper, ball) = Rig(s, start, keeperAt, t);
        ball.Velocity = new Vector3(8f, 0f, 0f);
        var rng = new SimRng(seed);
        rushed = false;
        float left = nextTouch;
        while (left > 0f)
        {
            var sit = new KeeperSituation
            {
                AttackerNextTouch = left, AttackerTouchTick = 9, AttackerHasBall = true, StrikeIn = float.PositiveInfinity,
                AttackerVelocity = new Vector3(7.5f, 0f, 0f),
            };
            brain.Update(Dt, ball, rng, sit);
            rushed |= keeper.KeeperAction is KeeperAction.Rush or KeeperAction.Smother;
            var res = ball.Step(Dt, P, brain.HandsSphere, brain.BodyCapsule, brain.HandsCapsule);
            if (res.SphereHit) { brain.Contact(ball, res.SpherePoint, new SimRng(5), out _, res.BodyHit); return true; }
            left -= Dt;
        }
        return false;
    }

    /// <summary>The share of 40 seeds (his misjudgement differs) in which he wins the touch.</summary>
    static double WinRate(Tuning t, Vector3 keeperAt, float ballFromGoal, float nextTouch, out double rushRate)
    {
        int won = 0, rushedN = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            if (RaceForTouch(t, keeperAt, ballFromGoal, nextTouch, seed, out bool rushed)) won++;
            if (rushed) rushedN++;
        }
        rushRate = rushedN / 40.0;
        return won / 40.0;
    }

    [Fact]
    public void HeWinsALongSprintTouchFromWhereHeClosedDownThatHeWouldNotWinFromHisArc()
    {
        // The ball 16 m out rolling on at 8 m/s, the sprinting attacker's next touch 1.5 s off (it runs about 8 m by then). The
        // race alone, with charging switched off (a charging keeper wins it from his arc too: see the charge tests).
        var s = TestData.Player("the_wall");
        const float from = 16f, nt = 1.5f;
        var t = NoCharge();
        var arcSpot = Keeper.ArcTarget(new Vector3(P.GoalLineX - from, 0.11f, 0f), P, t, s);
        var closed = new Vector3(P.GoalLineX - Keeper.CloseDownDistance(s, t, from, P.GoalLineX - arcSpot.X), 0f, 0f);
        var before = NoCharge();
        before.Keeper.RushAnticipation = new RatingRange(0f, 0f);
        double arc = WinRate(t, arcSpot, from, nt, out _), cdBefore = WinRate(before, closed, from, nt, out _);
        double cd = WinRate(t, closed, from, nt, out double rushes);
        double close = WinRate(t, closed, from, 0.4f, out double chased);
        output.WriteLine($"from the arc {arc:P0}, closed down without anticipation {cdBefore:P0}, closed down {cd:P0} (rushed {rushes:P0}); " +
                         $"touch 0.4 s off: won {close:P0}, chased {chased:P0}");
        Assert.Equal(0.0, arc);
        Assert.Equal(0.0, cdBefore);
        Assert.True(cd >= 0.7, $"closed down he won {cd:P0}");
        // The same ball met again within 0.4 s (close control) he neither chases nor wins.
        Assert.Equal(0.0, close);
        Assert.Equal(0.0, chased);
    }

    [Fact]
    public void TheLongerTheBallRunsAheadTheFurtherOutHeCommitsAndAnticipationStartsHimEarlier()
    {
        // Ball 14 m out rolling at 8 m/s, keeper 4 m out: the more time before the attacker's touch, the sooner he goes.
        int FirstGo(float nextTouch, Tuning t)
        {
            var s = TestData.Player("the_wall");
            var (brain, keeper, ball) = Rig(s, new Vector3(P.GoalLineX - 14f, 0.11f, 0f), new Vector3(P.GoalLineX - 4f, 0f, 0f), t);
            ball.Velocity = new Vector3(8f, 0f, 0f);
            for (int i = 0; i < 200; i++)
            {
                brain.Update(Dt, ball, new SimRng(4), new KeeperSituation
                {
                    AttackerNextTouch = nextTouch - i * Dt, AttackerTouchTick = 9, AttackerHasBall = true, StrikeIn = float.PositiveInfinity,
                });
                if (keeper.KeeperAction is KeeperAction.Rush or KeeperAction.Smother) return i;
                ball.Step(Dt, null, null);
            }
            return int.MaxValue;
        }
        int late = FirstGo(1.1f, T), early = FirstGo(1.6f, T);
        output.WriteLine($"first move: touch in 1.1 s -> tick {late}, 1.6 s -> tick {early}");
        Assert.True(early < late, $"{early} vs {late}");
        var noAnticipation = new Tuning();
        noAnticipation.Keeper.RushAnticipation = new RatingRange(0f, 0f);
        Assert.True(FirstGo(1.1f, T) <= FirstGo(1.1f, noAnticipation));
    }

    [Fact]
    public void HeClosesACloseControlDribblerDownAndJockeysButDoesNotDiveInAtABallHeCannotBeat()
    {
        // A close-control dribbler (a touch every 0.3 s, the attacker half a metre behind the ball) comes on from 13 m to about
        // 3 m from him, then holds the ball there. He charges, then jockeys a couple of metres off the ball; he neither races
        // for it nor dives in, since the attacker always plays it first.
        var s = TestData.Player("the_wall");
        var keeperAt = new Vector3(P.GoalLineX - 4f, 0f, 0f);
        var ballAt = new Vector3(P.GoalLineX - 13f, 0.11f, 0f);
        var (brain, keeper, ball) = Rig(s, ballAt, keeperAt, Legacy);
        var rng = new SimRng(6);
        const float v = 3f;
        bool charged = false, jockeyed = false;
        int tick = 0;
        for (int i = 0; i < 300; i++)
        {
            float cycle = 0.3f - (i % 36) * Dt;
            float gap = Vector2.Distance(new Vector2(keeper.Position.X, keeper.Position.Z), new Vector2(ball.Position.X, ball.Position.Z));
            float vb = gap > 3f ? v : 0f;
            ball.Position += new Vector3(vb * Dt, 0f, 0f);
            ball.Velocity = new Vector3(vb, 0f, 0f);
            if (i % 36 == 0) tick++;
            brain.Update(Dt, ball, rng, Dribbling(ball.Position, vb, cycle, tick));
            Assert.NotEqual(KeeperAction.Rush, keeper.KeeperAction);
            Assert.NotEqual(KeeperAction.Smother, keeper.KeeperAction);
            charged |= keeper.KeeperAction == KeeperAction.Charge;
            jockeyed |= keeper.KeeperAction == KeeperAction.Jockey;
        }
        float end = Vector2.Distance(new Vector2(keeper.Position.X, keeper.Position.Z), new Vector2(ball.Position.X, ball.Position.Z));
        output.WriteLine($"charged {charged}, jockeyed {jockeyed}, ends {end:F2} m from the ball, {Depth(keeper):F2} m off his line");
        Assert.True(charged, "he should charge the dribbler coming at him");
        Assert.True(jockeyed, "close to the dribbler he should jockey");
        Assert.InRange(end, 1.5f, 2.6f);
    }

    [Fact]
    public void JockeyingHeMirrorsTheDribblerAcrossAndGivesGroundOnlySlowly()
    {
        // A close-control dribbler comes in from 16 m at 3 m/s until the keeper jockeys 2 m off the ball, then shifts the ball
        // across at 3 m/s: he steps across with him, staying on the cover line; when the dribbler then comes straight at him at
        // 3 m/s, he gives ground no faster than jockey_retreat_speed.
        var s = TestData.Player("the_wall");
        var ballAt = new Vector3(P.GoalLineX - 16f, 0.11f, 0f);
        var (brain, keeper, ball) = Rig(s, ballAt, new Vector3(P.GoalLineX - 5f, 0f, 0f), Legacy);
        var rng = new SimRng(8);
        int tick = 0;
        void Step(Vector3 vel, int i)
        {
            ball.Position += vel * Dt;
            ball.Velocity = vel;
            if (i % 36 == 0) tick++;
            var sit = Dribbling(ball.Position, vel.X, 0.3f - (i % 36) * Dt, tick) with { AttackerVelocity = vel };
            brain.Update(Dt, ball, rng, sit);
        }
        for (int i = 0; i < 600 && keeper.KeeperAction != KeeperAction.Jockey; i++) Step(new Vector3(3f, 0f, 0f), i);
        Assert.Equal(KeeperAction.Jockey, keeper.KeeperAction);
        for (int i = 0; i < 30; i++) Step(Vector3.Zero, i);
        for (int i = 0; i < 120; i++) Step(new Vector3(0f, 0f, 3f), i);
        var cover = Keeper.CoverDirection(new Vector2(ball.Position.X, ball.Position.Z), P, T);
        var toK = new Vector2(keeper.Position.X - ball.Position.X, keeper.Position.Z - ball.Position.Z);
        float off = MathF.Abs(toK.X * cover.Y - toK.Y * cover.X);
        output.WriteLine($"after the shift: {toK.Length():F2} m from the ball, {off:F2} m off the cover line, {keeper.KeeperAction}");
        Assert.True(off < 0.6f, $"{off:F2} m off the cover line");
        Assert.InRange(toK.Length(), 1.5f, 2.8f);
        float maxBack = 0f;
        for (int i = 0; i < 40; i++)
        {
            Step(new Vector3(3f, 0f, 0f), i);
            maxBack = MathF.Max(maxBack, keeper.Velocity.X);
        }
        output.WriteLine($"coming at him: gives ground at most {maxBack:F2} m/s");
        Assert.True(maxBack <= T.Keeper.JockeyRetreatSpeed + 0.05f, $"{maxBack:F2} m/s");
    }

    // ---- the windup ----

    [Fact]
    public void HeStopsAdvancingOnceAShotWindsUp()
    {
        var s = TestData.Player("the_wall");
        var ballAt = new Vector3(P.GoalLineX - 20f, 0.11f, 0f);
        // Closing down (not charging: a charge into a wind-up is tested with the charge).
        var t = NoCharge();
        var (brain, keeper, ball) = Rig(s, ballAt, Keeper.ArcTarget(ballAt, P, t, s), t);
        var rng = new SimRng(7);
        const float v = 6f;
        ball.Velocity = new Vector3(v, 0f, 0f);
        // Closing down while the attacker runs in from 20 m to 15 m.
        while (P.GoalLineX - ball.Position.X > 15f)
        {
            ball.Position += new Vector3(v * Dt, 0f, 0f);
            brain.Update(Dt, ball, rng, Dribbling(ball.Position, v));
        }
        // Coming out is toward -X (the goal line is at +X).
        Assert.True(-keeper.Velocity.X > 0.5f, $"he should still be coming out ({-keeper.Velocity.X:F2} m/s)");
        // The shot winds up, contact in 0.3 s: he plants and comes no further than his braking allows.
        float depth0 = Depth(keeper), vx = -keeper.Velocity.X;
        for (int i = 0; i < 36; i++)
        {
            var sit = Dribbling(ball.Position, 0f) with { Windup = true, StrikeIn = 0.3f - i * Dt };
            brain.Update(Dt, ball, rng, sit);
            Assert.Equal(KeeperAction.Planted, keeper.KeeperAction);
        }
        Assert.Equal(0f, keeper.Velocity.Length(), 2);
        Assert.True(Depth(keeper) <= depth0 + vx * vx / (2f * t.Keeper.PlantDecel) + 0.02f, $"{depth0:F2} -> {Depth(keeper):F2}");
    }

    // ---- chips ----

    /// <summary>
    /// A chip at the ideal charge for <paramref name="from"/> metres, straight at goal, with the keeper standing
    /// <paramref name="depth"/> off his line. Returns how it ended and whether he backpedalled.
    /// </summary>
    static (SaveKind Save, bool Goal, bool Backpedalled) Chip(float from, float depth, ulong seed) => Chip(from, depth, seed, out _);

    static (SaveKind Save, bool Goal, bool Backpedalled) Chip(float from, float depth, ulong seed, out KeeperDive dive)
    {
        var s = TestData.Player("the_wall");
        var ballAt = new Vector3(P.GoalLineX - from, 0.11f, 0f);
        var (brain, keeper, ball) = Rig(s, ballAt, new Vector3(P.GoalLineX - depth, 0f, 0f));
        var rng = new SimRng(seed);
        float charge = ShotModel.IdealCharge(from, T);
        float speed = ShotModel.IntendedSpeed(Striker, ShotType.Chip, charge, T);
        float elev = ShotModel.ElevationDeg(ShotType.Chip, charge, T) * MathF.PI / 180f;
        ball.Velocity = new Vector3(MathF.Cos(elev), MathF.Sin(elev), 0f) * speed;
        ball.Spin = new Vector3(0f, 0f, 1f) * (5f * 2f * MathF.PI);
        brain.OnStrike(rng, ball);
        bool back = false;
        var flight = new KeeperSituation { AttackerNextTouch = float.PositiveInfinity, AttackerTouchTick = -1, StrikeIn = float.PositiveInfinity, ShotInFlight = true };
        for (int i = 0; i < 480; i++)
        {
            brain.Update(Dt, ball, rng, flight);
            back |= keeper.KeeperAction == KeeperAction.Backpedal;
            var prev = ball.Position;
            var res = ball.Step(Dt, P, brain.HandsSphere, brain.BodyCapsule, brain.HandsCapsule);
            brain.ObservePlane(prev, ball.Position);
            if (res.SphereHit)
            {
                brain.Contact(ball, res.SpherePoint, rng, out _, res.BodyHit);
                dive = brain.Report.Dive;
                return (brain.Report.Save, false, back);
            }
            if (P.IsGoal(ball.Position, ball.Radius)) { dive = brain.Report.Dive; return (SaveKind.None, true, back); }
            if (ball.Position.X > P.GoalLineX + 1f || ball.Velocity.X < 0f) break;
        }
        dive = brain.Report.Dive;
        return (SaveKind.None, false, back);
    }

    [Fact]
    public void AChipOverHimIsReadFromItsFlightHeBackpedalsAndCanTipItOverOrCatchIt()
    {
        var s = TestData.Player("the_wall");
        const float from = 20f;
        float safe = Keeper.ChipSafeDepth(s, Striker, T, P, from);
        output.WriteLine($"chip-safe depth from {from} m: {safe:F2} m");
        int saved = 0, tipped = 0, back = 0, n = 20;
        for (ulong seed = 1; seed <= (ulong)n; seed++)
        {
            var r = Chip(from, safe - 0.5f, seed, out var dive);
            if (r.Backpedalled) back++;
            if (r.Save != SaveKind.None) Assert.Equal(KeeperDive.Back, dive); // the leap at the end of a backpedal is the backward one
            if (r.Save is SaveKind.TipOver or SaveKind.Catch) saved++;
            if (r.Save == SaveKind.TipOver) tipped++;
        }
        output.WriteLine($"inside his safe depth: backpedalled {back}/{n}, saved {saved}/{n} (tipped over {tipped})");
        Assert.Equal(n, back);
        Assert.True(saved >= 0.8 * n, $"saved {saved}/{n}");
        Assert.True(tipped > 0);

        // Well beyond his safe depth he still backpedals, but the chip beats him.
        int beaten = 0, back2 = 0;
        for (ulong seed = 1; seed <= (ulong)n; seed++)
        {
            var r = Chip(from, safe + 4f, seed);
            if (r.Backpedalled) back2++;
            if (r.Goal) beaten++;
        }
        output.WriteLine($"4 m beyond: backpedalled {back2}/{n}, beaten {beaten}/{n}");
        Assert.Equal(n, back2);
        Assert.True(beaten >= 0.8 * n, $"beaten {beaten}/{n}");
    }

    [Fact]
    public void AChipThatDoesNotClearHimIsPlayedAsAnOrdinarySave()
    {
        // From 8 m the ideal chip peaks under his leap: no backpedal, he saves it where he stands.
        var r = Chip(8f, 3f, 1);
        Assert.False(r.Backpedalled);
    }

    // ---- commitment ----

    [Trait("Category", "Slow")]
    [Fact]
    public void StartingWellOffHisLineHeDoesNotGoBackAndForth()
    {
        // keeper_rush: he starts 8 m out with the attacker running in from 25 m. In the first 2 s his movement off or toward his
        // line changes direction at most once in 95% of rounds.
        var sc = TestData.Scenario("keeper_rush");
        var counts = new List<int>();
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var r = RoundRunner.RunFirstShot(T, sc, Striker, TestData.Player("the_wall"), seed);
            counts.Add(r.Keeper.DepthReversalsFirst2S);
        }
        counts.Sort();
        output.WriteLine($"reversals in the first 2 s: mean {counts.Average():F2}, p95 {counts[189]}, max {counts[^1]}");
        Assert.True(counts[189] <= 1, $"p95 {counts[189]}");
    }

    // ---- taken round ----

    [Fact]
    public void TakenRoundHeLungesAtTheBallGoingPastWithinReachOrElseRecoversTowardHisGoal()
    {
        var s = TestData.Player("the_wall");
        // The ball pushed across and past him, within a dive, the attacker's next touch 0.6 s off: he dives at it.
        var keeperAt = new Vector3(P.GoalLineX - 6f, 0f, 0f);
        var (brain, keeper, ball) = Rig(s, new Vector3(P.GoalLineX - 6.8f, 0.11f, -0.8f), keeperAt);
        ball.Velocity = new Vector3(1.5f, 0f, 3f);
        var sit = new KeeperSituation { AttackerNextTouch = 0.6f, AttackerTouchTick = 3, AttackerHasBall = true, StrikeIn = float.PositiveInfinity };
        brain.Update(Dt, ball, new SimRng(8), sit);
        Assert.Equal(KeeperAction.Smother, keeper.KeeperAction);

        // The attacker has it under control beside and past him, out of reach: he runs back toward his goal.
        var (brain2, keeper2, ball2) = Rig(s, new Vector3(P.GoalLineX - 5f, 0.11f, 3.5f), keeperAt);
        ball2.Velocity = new Vector3(3f, 0f, 1f);
        float depth0 = Depth(keeper2);
        for (int i = 0; i < 120; i++)
        {
            ball2.Position += ball2.Velocity * Dt;
            brain2.Update(Dt, ball2, new SimRng(9), Dribbling(ball2.Position, 3f, 0.1f, 4));
            Assert.NotEqual(KeeperAction.Spread, keeper2.KeeperAction);
        }
        Assert.True(Depth(keeper2) < depth0 - 1.5f, $"{depth0:F2} -> {Depth(keeper2):F2}");
        Assert.True(brain2.Report.Rounded, "the report should record that he was taken round");
    }
}
