using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>
/// The save model: the reflex save before the full reaction, the ready stance's hull, save difficulty by range, height and
/// counter-momentum, the catch range by ball speed and the time he had, and the covered angle that no longer dips mid-range.
/// </summary>
public class KeeperSaveModelTests
{
    static readonly Tuning T = new();
    static readonly Pitch P = new(new PitchSpec(), T.Frame);

    static PlayerSheet Wall() => TestData.Player("the_wall");

    // ---- the reflex save ----

    [Fact]
    public void TheReflexComesBeforeTheReactionAndSoonerWithBetterReflexes()
    {
        var s = Wall();
        for (ulong seed = 1; seed <= 50; seed++)
        {
            float reaction = Keeper.DrawReaction(s, T, new SimRng(seed));
            float reflex = Keeper.DrawReflex(s, T, new SimRng(seed + 1000), reaction);
            Assert.True(reflex <= reaction && reflex >= T.Keeper.MinReaction, $"reflex {reflex} reaction {reaction}");
        }
        s.Reflexes = 20; float slow = Keeper.ReflexMean(s, T);
        s.Reflexes = 90; float quick = Keeper.ReflexMean(s, T);
        Assert.True(quick < slow - 0.05f, $"{slow} -> {quick}");
        Assert.True(Keeper.ReflexMean(s, T) < Keeper.ReactionMean(s, T) - 0.05f);
    }

    [Fact]
    public void AReflexLimbMovesAfterItsExecutionTimeAtReflexSpeedUpToItsRange()
    {
        var s = Wall();
        float range = Keeper.ReflexRange(s, T), speed = T.Keeper.ReflexSpeed.ForRating(s.Reflexes);
        Assert.Equal(0.5f, Keeper.ReflexReachAt(s, T, 0.02f, 0.5f, 0.03f), 4);                 // still executing
        Assert.Equal(0.5f + speed * 0.1f, Keeper.ReflexReachAt(s, T, 0.13f, 0.5f, 0.03f), 3);  // moving out
        Assert.Equal(range, Keeper.ReflexReachAt(s, T, 2f, 0.5f, 0.03f), 4);                     // capped
        Assert.InRange(range, 1.4f, 1.9f);                                                       // his hull plus a short lunge
        Assert.True(Keeper.ReflexRange(s, T) < Keeper.Stretch(s, T) - 0.8f, "a reflex is limited, a dive is not");
        // An easy reflex executes sooner than a hard one.
        Assert.True(Keeper.ReflexExecution(s, T, 0f) < Keeper.ReflexExecution(s, T, 1f));
    }

    [Fact]
    public void AShotTooQuickForTheDiveDrawsAReflexSaveThatGetsToIt()
    {
        // 7 m out at 25 m/s, 1.15 m to his side at waist height: it arrives 0.28 s after the strike, too soon for his reaction
        // and dive to get there (unless he draws a quick reaction), but a reflex hand does.
        int reflex = 0;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            Assert.True(KeeperCoverTests.Touches(T, Wall(), 7f, 25f, 1.0f, 1.15f, seed, out var r), $"seed {seed}");
            if (r.SaveType == SaveType.Reflex) { reflex++; Assert.Equal(KeeperDive.ReflexHand, r.Dive); }
            // A slow reaction leaves the dive too late: then it is always the reflex.
            else if (r.ReactionS >= 0.17f) Assert.Fail($"seed {seed}: {r.SaveType} with reaction {r.ReactionS:F3}");
            Assert.True(r.ReflexS < r.ReactionS);
        }
        Assert.True(reflex >= 10, $"reflex saves {reflex}/20");

        // 1.45 m to his side the reflex hand still gets a touch; without a reflex (it comes no sooner than the reaction) he waits
        // for the dive and it is too late.
        var t = new Tuning();
        t.Keeper.ReflexTime = new RatingRange(1f, 1f);
        int touched = 0, late = 0;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            if (KeeperCoverTests.Touches(T, Wall(), 7f, 25f, 1.0f, 1.45f, seed)) touched++;
            if (KeeperCoverTests.Touches(t, Wall(), 7f, 25f, 1.0f, 1.45f, seed, out var r)) late++;
            else Assert.Equal(SaveType.FailedAttempt, r.SaveType);
        }
        Assert.True(touched >= 16, $"touched with a reflex {touched}/20");
        Assert.True(late <= 4, $"touched without a reflex {late}/20");
    }

    [Fact]
    public void WithTimeForTheDiveHeDivesRatherThanMakingAReflexSave()
    {
        for (ulong seed = 1; seed <= 10; seed++)
        {
            KeeperCoverTests.Touches(T, Wall(), 16f, 25f, 1.0f, 1.5f, seed, out var r);
            Assert.Contains(r.SaveType, new[] { SaveType.Catch, SaveType.Parry });
            Assert.Equal(KeeperDive.Side, r.Dive);
        }
    }

    [Fact]
    public void ALowBallDrawsAFootAndAReadyKeeperThrowsHisBody()
    {
        var feet = new Vector3(40f, 0f, 0f);
        Assert.Equal(KeeperDive.ReflexFoot, Keeper.ReflexKindFor(feet, new Vector3(40f, 0.2f, 1f), 1f, T));
        Assert.Equal(KeeperDive.BodyBlock, Keeper.ReflexKindFor(feet, new Vector3(40f, 1.0f, 1f), 1f, T));
        Assert.Equal(KeeperDive.ReflexHand, Keeper.ReflexKindFor(feet, new Vector3(40f, 1.0f, 1f), 0.2f, T));
        Assert.Equal(KeeperDive.ReflexHand, Keeper.ReflexKindFor(feet, new Vector3(40f, 1.8f, 1f), 1f, T));

        // Through the brain: a low shot from 8 m, 1.0 m to his side.
        KeeperCoverTests.Touches(T, Wall(), 8f, 25f, 0.2f, 1.0f, 3, out var low);
        Assert.Equal(KeeperDive.ReflexFoot, low.Dive);
        // Set 5 m from the shooter he is ready (more than half), and a ball at his hip beyond his hull draws the body block.
        KeeperCoverTests.Touches(T, Wall(), 4.5f, 25f, 1.0f, 1.1f, 3, out var body);
        Assert.True(body.Ready >= 0.5f, $"ready {body.Ready}");
        Assert.Equal(KeeperDive.BodyBlock, body.Dive);
    }

    [Fact]
    public void AReflexAttemptIsMadeWhenNeitherSaveGetsThereButTheReflexGetsCloser()
    {
        // 6 m out at 30 m/s, 1.75 m to his side: beyond what a reflex reaches by then, and far beyond a dive.
        int attempts = 0;
        for (ulong seed = 1; seed <= 10; seed++)
        {
            KeeperCoverTests.Touches(T, Wall(), 6f, 30f, 1.0f, 1.75f, seed, out var r);
            if (r.SaveType == SaveType.ReflexAttempt) attempts++;
        }
        Assert.True(attempts >= 8, $"reflex attempts {attempts}/10");
        // Far beyond any reflex he lets the reflex go and dives anyway.
        KeeperCoverTests.Touches(T, Wall(), 6f, 30f, 1.0f, 3.2f, 1, out var wide);
        Assert.Equal(SaveType.FailedAttempt, wide.SaveType);
    }

    // ---- the ready stance's hull ----

    [Fact]
    public void ReadinessIsFullWithinThreeAndAHalfMetresAndGoneFromSix()
    {
        Assert.Equal(1f, Keeper.Readiness(T, 2f));
        Assert.Equal(1f, Keeper.Readiness(T, 3.5f));
        Assert.Equal(0.5f, Keeper.Readiness(T, 4.75f), 3);
        Assert.Equal(0f, Keeper.Readiness(T, 6f));
        Assert.Equal(0.75f, Keeper.HullRadius(T, 1f), 4);                 // GOALS CapsuleRadius 75 cm
        Assert.Equal(T.Keeper.BodyRadius, Keeper.HullRadius(T, 0f), 4);
    }

    [Fact]
    public void ASetKeeperCloseToTheShooterIsBigEnoughToBlockBesideHimLowAndHighBeforeHeMoves()
    {
        // From 3 m at 30 m/s the ball is past him in 0.1 s, before his reflex: only his standing body can meet it.
        foreach (float height in new[] { 0.2f, 1.0f, 1.9f })
        {
            int blocked = 0;
            for (ulong seed = 1; seed <= 10; seed++)
                if (KeeperCoverTests.Touches(T, Wall(), 3f, 30f, height, 0.75f, seed, out var r))
                {
                    blocked++;
                    Assert.Equal(SaveType.None, r.SaveType);   // met before he moved
                    Assert.Equal(SaveKind.Block, r.Save);
                }
            Assert.True(blocked == 10, $"height {height}: blocked {blocked}/10");
        }
        // Not set (no stance for the shot) the same low ball beside him goes by; set but 8 m out he is not ready either.
        Assert.False(KeeperCoverTests.Touches(T, Wall(), 3f, 30f, 0.2f, 0.75f, 1, out var unset, planted: false));
        Assert.Equal(0f, unset.Ready);
        KeeperCoverTests.Touches(T, Wall(), 8f, 30f, 1.0f, 2.5f, 1, out var far);
        Assert.Equal(0f, far.Ready);
    }

    // ---- difficulty ----

    [Fact]
    public void DifficultyGrowsWithTheRangeNeededAndIsHigherForLowAndHighBalls()
    {
        var s = Wall();
        float stretch = Keeper.Stretch(s, T);
        foreach (float h in new[] { 0.2f, 1.0f, 2.0f })
        {
            float prev = -1f;
            for (float x = 0f; x <= 1.001f; x += 0.1f)
            {
                float d = Keeper.RangeDifficulty(s, T, x * stretch, h);
                Assert.True(d >= prev, $"height {h}: {prev} -> {d} at {x}");
                prev = d;
            }
            Assert.Equal(1f, Keeper.RangeDifficulty(s, T, stretch, h), 3);
        }
        float mid = Keeper.RangeDifficulty(s, T, 0.5f * stretch, 1.0f);
        Assert.True(Keeper.RangeDifficulty(s, T, 0.5f * stretch, 0.2f) > mid);
        Assert.True(Keeper.RangeDifficulty(s, T, 0.5f * stretch, 2.0f) > mid);
        // A longer stretch (a taller keeper with more reach) makes the same save easier.
        var tall = s.Clone(); tall.HeightM = 2.05f;
        Assert.True(Keeper.RangeDifficulty(tall, T, 1.8f, 1.0f) < Keeper.RangeDifficulty(s, T, 1.8f, 1.0f));
    }

    [Fact]
    public void MovingTheWrongWayMakesTheSaveHarderAndSlowerToExecute()
    {
        var s = Wall();
        var feet = new Vector3(40f, 0f, 0f);
        var target = new Vector3(40f, 1.0f, 1.5f);
        float still = Keeper.Difficulty(s, T, feet, Vector3.Zero, target);
        float toward = Keeper.Difficulty(s, T, feet, new Vector3(0f, 0f, 2f), target);
        float away = Keeper.Difficulty(s, T, feet, new Vector3(0f, 0f, -2f), target);
        Assert.Equal(still, toward, 4);
        Assert.Equal(still + T.Keeper.CounterMomentumPenalty.Evaluate(2f), away, 4);
        Assert.True(Keeper.SaveExecution(s, T, away) > Keeper.SaveExecution(s, T, still));
        Assert.True(Keeper.SaveJitterMax(T, away) > Keeper.SaveJitterMax(T, still));
        Assert.True(Keeper.ReachAt(s, T, 0.25f, 0f, Keeper.SaveExecution(s, T, away)) < Keeper.ReachAt(s, T, 0.25f, 0f, Keeper.SaveExecution(s, T, still)));

        // Through the brain: a dive at the same ball, moving away from it at the strike, is reported harder.
        var keeper = new PlayerState(1, 0, PlayerRole.Keeper, s) { Position = new Vector3(P.GoalLineX - 2f, 0f, 0f), Facing = MathF.PI };
        float Dive(Vector3 velocity)
        {
            var k = new PlayerState(1, 0, PlayerRole.Keeper, s) { Position = keeper.Position, Facing = MathF.PI, Velocity = velocity };
            var brain = new KeeperBrain(k, P, T, holdLine: false);
            var ball = new Ball(T.Ball) { Position = new Vector3(P.GoalLineX - 18f, 1.0f, 0f) };
            ball.Velocity = new Vector3(16f, 0.5f * 9.81f * 1f, 1.5f);
            brain.OnStrike(new SimRng(2), ball);
            for (int i = 0; i < 60 && k.KeeperAction != KeeperAction.Diving; i++) brain.Update(T.Dt, ball, new SimRng(3));
            return brain.Report.Difficulty;
        }
        Assert.True(Dive(new Vector3(0f, 0f, -6f)) > Dive(Vector3.Zero) + 0.2f);
    }

    [Fact]
    public void LessTimeAndAFasterBallShrinkTheCatchRange()
    {
        var s = Wall();
        Assert.True(Keeper.CatchRange(s, T, 18f, true, 0.15f) < 0.5f * Keeper.CatchRange(s, T, 18f, true, 0.6f));
        Assert.True(Keeper.CatchRange(s, T, 24f, true, 0.6f) < Keeper.CatchRange(s, T, 16f, true, 0.6f));
        Assert.Equal(0f, Keeper.CatchRange(s, T, 30f, true, 0.6f), 4);
        Assert.True(Keeper.CatchRange(s, T, 18f, true, 0.6f) > Keeper.CatchRange(s, T, 18f, false, 0.6f));

        // The same ball at the same place: held with plenty of time, pushed away with little.
        bool Held(float since)
        {
            var keeper = new PlayerState(1, 0, PlayerRole.Keeper, s) { Position = new Vector3(P.GoalLineX - 2f, 0f, 0f), Facing = MathF.PI };
            var brain = new KeeperBrain(keeper, P, T, holdLine: false);
            var target = keeper.Position + new Vector3(-0.1f, 1.1f, 1.0f);
            brain.BeginDiveForTest(target, since);
            var ball = new Ball(T.Ball) { Position = target, Velocity = new Vector3(17f, 0f, 0f) };
            return brain.Contact(ball, target, new SimRng(1), out _);
        }
        Assert.True(Held(0.6f));
        Assert.False(Held(0.12f));
    }

    [Fact]
    public void AHardSaveIsNeverHeldAndAtTheEdgeIsAFingertip()
    {
        var s = Wall(); s.Reach = 1;   // a short stretch, so a slow ball near its edge is inside his diving catch range
        float stretch = Keeper.Stretch(s, T);
        var keeper = new PlayerState(1, 0, PlayerRole.Keeper, s) { Position = new Vector3(P.GoalLineX - 1.5f, 0f, 0f), Facing = MathF.PI };
        var brain = new KeeperBrain(keeper, P, T, holdLine: false);
        // A slow ball (held anywhere in his diving range by speed alone) at 88% and 97% of his stretch, at mid height.
        var near = keeper.Position + new Vector3(0f, 1.0f, 0.88f * stretch);
        brain.BeginDiveForTest(near, 0.8f);
        Assert.True(brain.Report.Difficulty > T.Keeper.CatchMaxDifficulty);
        Assert.True(Vector3.Distance(near, keeper.Position + new Vector3(0f, s.HeightM * T.Keeper.HomeHeightFraction, 0f)) < Keeper.CatchRange(s, T, 8f, true, 0.8f));
        var ball = new Ball(T.Ball) { Position = near, Velocity = new Vector3(8f, 0f, 0f) };
        Assert.False(brain.Contact(ball, near, new SimRng(1), out _));
        Assert.Equal(SaveKind.Parry, brain.Report.Save);
        var edge = keeper.Position + new Vector3(0f, 1.0f, 0.97f * stretch);
        brain.BeginDiveForTest(edge, 0.8f);
        Assert.True(brain.Report.Difficulty >= T.Keeper.FingertipDifficulty);
        ball = new Ball(T.Ball) { Position = edge, Velocity = new Vector3(8f, 0f, 0f) };
        Assert.False(brain.Contact(ball, edge, new SimRng(1), out _));
        Assert.Equal(SaveKind.Fingertip, brain.Report.Save);
    }

    // ---- the covered angle ----

    /// <summary>
    /// Set and planted, the angle he covers no longer dips 6-8 m from the shooter: at each speed and height, from 4 m out the
    /// covered angle is never more than 10% below the largest covered angle further out.
    /// </summary>
    [Fact]
    public void TheCoveredAngleDoesNotDipMidRange()
    {
        var wall = Wall();
        foreach (float height in new[] { 0.3f, 1.0f })
            foreach (float v in KeeperCoverTests.Speeds)
            {
                var d = KeeperCoverTests.Distances.Where(x => x >= 4f).ToArray();
                var c = d.Select(x => KeeperCoverTests.CoveredAngle(T, wall, x, v, height, seeds: 12)).ToArray();
                for (int i = 0; i < c.Length; i++)
                {
                    float further = c.Skip(i + 1).DefaultIfEmpty(0f).Max();
                    Assert.True(c[i] >= 0.9f * further, $"{v} m/s at {height} m: {c[i]:F3} at {d[i]} m, {further:F3} further out");
                }
            }
    }
}
