using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>Phase D: the keeper's arc, rush race, plant, reaction, save wedge, save kinds, smother, recovery and contract.</summary>
public class KeeperBrainTests
{
    static readonly Tuning T = new();
    static readonly Pitch P = new(new PitchSpec(), T.Frame);
    static float Dt => T.Dt;

    static PlayerSheet Wall() => TestData.Player("the_wall");

    static (KeeperBrain Brain, PlayerState Keeper, Ball Ball) Rig(PlayerSheet sheet, Vector3 ball, bool holdLine = false, Tuning? t = null)
    {
        t ??= T;
        var b = new Ball(t.Ball) { Position = ball };
        var k = new PlayerState(1, 0, PlayerRole.Keeper, sheet)
        {
            Position = holdLine ? Clamp(Keeper.LineSpot(ball, P, t)) : Keeper.ArcTarget(ball, P, t, sheet),
            Facing = MathF.PI,
        };
        return (new KeeperBrain(k, P, t, holdLine), k, b);
    }

    static Vector3 Clamp(Vector3 v)
    {
        var vel = Vector3.Zero;
        P.ClampPlayer(ref v, ref vel);
        return v;
    }

    static KeeperSituation Loose(float attackerNextTouch) => new()
    {
        AttackerNextTouch = attackerNextTouch, AttackerTouchTick = 7, StrikeIn = float.PositiveInfinity,
    };

    // ---- the arc ----

    [Fact]
    public void ArcSpotIsOnTheBisectorOfThePostsAtLeastSixtyCentimetresOffTheLine()
    {
        var s = Wall();
        foreach (var ball in new[] { new Vector3(P.GoalLineX - 11f, 0.11f, 0f), new Vector3(P.GoalLineX - 12f, 0.11f, 6f), new Vector3(P.GoalLineX - 5f, 0.11f, 8f) })
        {
            var spot = Keeper.ArcTarget(ball, P, T, s);
            Assert.True(spot.X <= P.GoalLineX - T.Keeper.ArcMinFromLine + 1e-4f, $"x {spot.X}");
            float a = MathF.Atan2(spot.Z - ball.Z, spot.X - ball.X);
            float near = MathF.Atan2(P.GoalHalfWidth - ball.Z, P.GoalLineX - ball.X);
            float far = MathF.Atan2(-P.GoalHalfWidth - ball.Z, P.GoalLineX - ball.X);
            Assert.Equal(0.5f * (near + far), a, 3);
        }
        Assert.Equal(0f, Keeper.ArcTarget(new Vector3(P.GoalLineX - 11f, 0.11f, 0f), P, T, s).Z, 4);
    }

    [Fact]
    public void ArcSpotIsNeverCloserThanTheMinimumToTheLine()
    {
        var t = new Tuning();
        t.Keeper.ArcRadius = 0.1f;
        t.Keeper.ArcDepth = Curve.Constant(0f);
        var spot = Keeper.ArcTarget(new Vector3(P.GoalLineX - 20f, 0.11f, 0f), P, t, Wall());
        Assert.Equal(P.GoalLineX - 0.60f, spot.X, 3);
    }

    [Fact]
    public void NearPostBiasTurnsTheCoverLineTowardTheNearPost()
    {
        var ball = new Vector3(P.GoalLineX - 8f, 0.11f, 7f);
        var none = Keeper.ArcTarget(ball, P, T, Wall());
        var t = new Tuning();
        t.Keeper.NearPostBias = Curve.Constant(0.4f);
        var biased = Keeper.ArcTarget(ball, P, t, Wall());
        Assert.True(biased.Z > none.Z + 0.1f, $"{none.Z} -> {biased.Z}");
    }

    [Fact]
    public void BetterPositioningStandsFurtherOutOnTheArc()
    {
        var ball = new Vector3(P.GoalLineX - 20f, 0.11f, 0f);
        var lo = Wall(); lo.Positioning = 1;
        var hi = Wall(); hi.Positioning = 99;
        float dLo = P.GoalLineX - Keeper.ArcTarget(ball, P, T, lo).X;
        float dHi = P.GoalLineX - Keeper.ArcTarget(ball, P, T, hi).X;
        Assert.Equal(T.Keeper.ArcRadius + T.Keeper.ArcDepth.Evaluate(20f) * 0.5f, dLo, 3);
        Assert.Equal(T.Keeper.ArcRadius + T.Keeper.ArcDepth.Evaluate(20f), dHi, 3);
    }

    [Fact]
    public void HeStandsFurtherOutAsTheBallComesCloserAndFurtherStillAtATightAngle()
    {
        var s = Wall();
        float Out(Vector3 ball) => Keeper.MouthDistance(new Vector2(Keeper.ArcTarget(ball, P, T, s).X, Keeper.ArcTarget(ball, P, T, s).Z), P);
        float prev = 0f;
        for (float d = 30f; d >= 6f; d -= 1f)
        {
            float o = Out(new Vector3(P.GoalLineX - d, 0.11f, 0f));
            Assert.True(o >= prev - 1e-4f, $"depth falls at {d} m: {prev} -> {o}");
            prev = o;
        }
        // Same distance from the mouth, but a wide bearing: the bearing term takes him further out to close the angle.
        var wide = new Vector3(P.GoalLineX - 4.7f, 0.11f, P.GoalHalfWidth + 4.84f);
        var central = new Vector3(P.GoalLineX - Keeper.MouthDistance(new Vector2(wide.X, wide.Z), P), 0.11f, 0f);
        Assert.True(Out(wide) > Out(central) + 0.8f, $"central {Out(central)} wide {Out(wide)}");
    }

    [Fact]
    public void HisPositionAtTheStrikeDoesNotDependOnWhereHeStarted()
    {
        double MeanDepth(bool onLine, float? offLine)
        {
            double sum = 0; int n = 0;
            for (ulong seed = 1; seed <= 30; seed++)
            {
                var sc = TestData.Scenario("breakaway");
                sc.KeeperOnLine = onLine; sc.KeeperOffLine = offLine;
                var w = new World(new Tuning(), sc, TestData.Player("striker_a"), Wall(), seed);
                w.Begin();
                TestData.Play(w, new ScriptedAttacker(), 4000, x => x.Shots.Count > 0);
                if (float.IsFinite(w.KeeperReport.DepthAtStrikeM)) { sum += w.KeeperReport.DepthAtStrikeM; n++; }
            }
            Assert.True(n >= 25);
            return sum / n;
        }
        // Starting on his line or on his arc he is in the same place at the strike. (Starting well off his line, within his
        // limits, he may hold that depth while closing down: see the closing-down test.)
        double arc = MeanDepth(false, null), line = MeanDepth(true, null);
        Assert.InRange(line, arc - 0.15, arc + 0.15);
    }

    [Fact]
    public void ClosingDownHeHoldsHisDepthButNotBeyondHisChipSafeDepth()
    {
        // Attacker running at goal with the ball 16 m out. A keeper 4 m out (between his closing-down depth and his limits)
        // holds there; one 11 m out (beyond his chip-safe depth and hold_ground_max_out) comes back.
        var ball = new Vector3(P.GoalLineX - 16f, 0.11f, 0f);
        var s = new KeeperSituation
        {
            AttackerNextTouch = 0.05f, AttackerTouchTick = 1, AttackerHasBall = true, StrikeIn = float.PositiveInfinity,
            AttackerVelocity = new Vector3(6f, 0f, 0f),
        };
        foreach (var (startOut, holds) in new[] { (4.0f, true), (11.0f, false) })
        {
            var (brain, keeper, b) = Rig(Wall(), ball);
            keeper.Position = new Vector3(P.GoalLineX - startOut, 0f, 0f);
            b.Velocity = new Vector3(6f, 0f, 0f);
            for (int i = 0; i < 240; i++) brain.Update(Dt, b, new SimRng(1), s);
            float now = P.GoalLineX - keeper.Position.X;
            if (holds) Assert.InRange(now, startOut - 0.4f, startOut + 0.4f);
            else Assert.True(now < startOut - 1.5f && now <= T.Keeper.HoldGroundMaxOut + 0.3f, $"still {now} m out");
        }
    }

    [Fact]
    public void PositionErrorIsDrawnPerRepositionSeededAndSmallerForGoodPositioning()
    {
        double MeanError(int positioning, ulong seed, out List<Vector3> spots)
        {
            var s = Wall(); s.Positioning = positioning;
            var (brain, _, ball) = Rig(s, new Vector3(P.GoalLineX - 14f, 0.11f, -6f));
            var rng = new SimRng(seed);
            spots = new List<Vector3>();
            double sum = 0; int n = 0;
            // The ball switches between two sides of the goal; each switch moves the ideal spot past the threshold.
            for (int step = 0; step < 40; step++)
            {
                ball.Position = new Vector3(P.GoalLineX - 12f, 0.11f, step % 2 == 0 ? -6f : 6f);
                var before = brain.Spot;
                brain.Update(Dt, ball, rng, Loose(0f));
                Vector3 first = brain.Spot;
                for (int i = 0; i < 5; i++) brain.Update(Dt, ball, rng, Loose(0f));
                Assert.Equal(first, brain.Spot); // fixed between repositions
                if (first != before) { spots.Add(first); sum += Vector3.Distance(first, brain.SpotIdeal); n++; }
            }
            Assert.True(n >= 20, $"repositions {n}");
            return sum / n;
        }
        double good = MeanError(99, 3, out var a);
        double poor = MeanError(1, 3, out _);
        MeanError(99, 3, out var b);
        Assert.Equal(a, b);
        Assert.True(poor > 4 * good, $"poor {poor:F3} good {good:F3}");
        Assert.InRange(good, 0.01, 0.12);
    }

    // ---- the rush race ----

    [Fact]
    public void HeRushesALooseBallHeCanReachWellBeforeTheAttackerButNotOtherwise()
    {
        var spot = new Vector3(P.GoalLineX - 10f, 0.11f, 0f);
        var (brain, keeper, ball) = Rig(Wall(), spot);
        ball.Position = new Vector3(P.GoalLineX - 6f, 0.11f, 0f);
        brain.Update(Dt, ball, new SimRng(1), Loose(2.0f));
        Assert.Equal(KeeperAction.Rush, keeper.KeeperAction);

        var (brain2, keeper2, ball2) = Rig(Wall(), spot);
        ball2.Position = new Vector3(P.GoalLineX - 6f, 0.11f, 0f);
        brain2.Update(Dt, ball2, new SimRng(1), Loose(0.5f));
        Assert.NotEqual(KeeperAction.Rush, keeper2.KeeperAction);
        Assert.NotEqual(KeeperAction.Smother, keeper2.KeeperAction);
    }

    [Fact]
    public void HeDoesNotRushForABallBeyondFifteenMetresFromGoal()
    {
        var (brain, keeper, ball) = Rig(Wall(), new Vector3(P.GoalLineX - 20f, 0.11f, 0f));
        ball.Position = new Vector3(P.GoalLineX - 16.5f, 0.11f, 0f);
        for (int i = 0; i < 30; i++) brain.Update(Dt, ball, new SimRng(1), Loose(5f));
        Assert.NotEqual(KeeperAction.Rush, keeper.KeeperAction);
        ball.Position = new Vector3(P.GoalLineX - 13f, 0.11f, 0f);
        brain.Update(Dt, ball, new SimRng(1), Loose(5f));
        Assert.Equal(KeeperAction.Rush, keeper.KeeperAction);
    }

    [Fact]
    public void EachMovementDecisionSteersAtMostThreeAndAHalfMetresAway()
    {
        var from = new Vector2(20f, 0f);
        Assert.Equal(new Vector2(16.5f, 0f), Keeper.CapTarget(from, new Vector2(10f, 0f), T.Keeper.MoveTargetCap));
        Assert.Equal(new Vector2(18f, 1f), Keeper.CapTarget(from, new Vector2(18f, 1f), T.Keeper.MoveTargetCap));
        Assert.Equal(3.5f, T.Keeper.MoveTargetCap);
    }

    [Fact]
    public void HeSmothersALooseBallWithinADiveAndHoldsIt()
    {
        var (brain, keeper, ball) = Rig(Wall(), new Vector3(P.GoalLineX - 11f, 0.11f, 0f));
        ball.Position = new Vector3(keeper.Position.X - 1.0f, 0.11f, keeper.Position.Z);
        ball.Velocity = new Vector3(1f, 0f, 0f);
        brain.Update(Dt, ball, new SimRng(1), Loose(3f));
        Assert.Equal(KeeperAction.Smother, keeper.KeeperAction);
        bool held = false;
        for (int i = 0; i < 60 && !held; i++)
        {
            brain.Update(Dt, ball, new SimRng(1), Loose(3f));
            var res = ball.Step(Dt, P, brain.HandsSphere, brain.BodyCapsule);
            if (res.SphereHit) held = brain.Contact(ball, res.SpherePoint, new SimRng(2), out _, res.BodyHit);
        }
        Assert.True(held);
        Assert.Equal(KeeperAction.Holding, keeper.KeeperAction);
        Assert.Equal(SaveKind.Smother, brain.Report.Save);
        Assert.True(brain.Report.Smothered);
    }

    // ---- set / plant ----

    [Fact]
    public void HePlantsInTheLastMomentsOfAWindupWithinRange()
    {
        var (brain, keeper, ball) = Rig(Wall(), new Vector3(P.GoalLineX - 16f, 0.11f, 0f));
        keeper.Velocity = new Vector3(0f, 0f, 3f);
        var early = new KeeperSituation { AttackerNextTouch = 0.6f, AttackerTouchTick = 1, Windup = true, StrikeIn = 0.6f };
        brain.Update(Dt, ball, new SimRng(1), early);
        Assert.NotEqual(KeeperAction.Planted, keeper.KeeperAction);
        var late = early with { StrikeIn = 0.2f };
        float v0 = keeper.Velocity.Length();
        brain.Update(Dt, ball, new SimRng(1), late);
        Assert.Equal(KeeperAction.Planted, keeper.KeeperAction);
        // Hard but not instant: the speed drops by the plant deceleration per tick.
        Assert.Equal(MathF.Max(0f, v0 - T.Keeper.PlantDecel * Dt), keeper.Velocity.Length(), 3);
        for (int i = 0; i < 60; i++) brain.Update(Dt, ball, new SimRng(1), late);
        Assert.Equal(0f, keeper.Velocity.Length(), 4);

        var (far, farKeeper, farBall) = Rig(Wall(), new Vector3(P.GoalLineX - 28f, 0.11f, 0f));
        far.Update(Dt, farBall, new SimRng(1), late);
        Assert.NotEqual(KeeperAction.Planted, farKeeper.KeeperAction);
    }

    // ---- reaction ----

    [Fact]
    public void StillMovingAtTheStrikeAddsTheReactionPenalty()
    {
        var s = Wall();
        float still = Keeper.ReactionAtStrike(s, T, new SimRng(9), 0f);
        float moving = Keeper.ReactionAtStrike(s, T, new SimRng(9), 1.5f);
        Assert.Equal(T.Keeper.StillMovingReactionPenalty, moving - still, 5);
        Assert.Equal(0.05f, T.Keeper.StillMovingReactionPenalty);

        var (brain, keeper, ball) = Rig(s, new Vector3(P.GoalLineX - 11f, 0.11f, 0f));
        keeper.Velocity = new Vector3(0f, 0f, 2f);
        brain.OnStrike(new SimRng(9), ball);
        Assert.Equal(moving, brain.Report.ReactionS, 5);
        Assert.False(brain.Report.PlantedAtStrike);
    }

    [Fact]
    public void ReactionComesFromReflexesAndTheSaveJitterRunsEasyToHard()
    {
        var s = Wall();
        Assert.Equal(T.Keeper.Reaction.ForRating(s.Reflexes), Keeper.ReactionMean(s, T), 5);
        Assert.Equal(0.001f, Keeper.SaveJitterMax(s, T, 0f), 5);
        Assert.Equal(0.025f, Keeper.SaveJitterMax(s, T, Keeper.Stretch(s, T)), 5);
        Assert.True(Keeper.SaveJitterMax(s, T, 1f) is > 0.001f and < 0.025f);

        // Through the brain: reacting until the drawn reaction has passed, then diving.
        var (brain, keeper, ball) = Rig(s, new Vector3(P.GoalLineX - 11f, 0.11f, 0f));
        ball.Velocity = new Vector3(24f, 3f, 2f);
        brain.OnStrike(new SimRng(4), ball);
        float r = brain.Report.ReactionS;
        float t = 0f;
        while (t + Dt < r - 1e-4f) { brain.Update(Dt, ball, new SimRng(5)); t += Dt; Assert.Equal(KeeperAction.Reacting, keeper.KeeperAction); }
        for (int i = 0; i < 3; i++) brain.Update(Dt, ball, new SimRng(5));
        Assert.Equal(KeeperAction.Diving, keeper.KeeperAction);
    }

    // ---- the set stance: hands out to the sides ----

    [Fact]
    public void ASetKeepersReachStartsFromHisHandSpan()
    {
        var s = Wall();
        float span = T.Keeper.HandSpan;
        Assert.InRange(span, 0.4f, 0.6f);
        Assert.Equal(span, Keeper.ReachAt(s, T, 0f, span), 5);
        Assert.Equal(0f, Keeper.ReachAt(s, T, 0f), 5);
        Assert.True(Keeper.ReachAt(s, T, 0.05f, span) > Keeper.ReachAt(s, T, 0.05f));
        // Once the arm sweep is done the two agree: the span is a head start, not extra stretch.
        Assert.Equal(Keeper.ReachAt(s, T, 0.3f), Keeper.ReachAt(s, T, 0.3f, span), 4);
    }

    [Fact]
    public void ASetKeepersHandsBlockACloseShotBesideHimBeforeHeReacts()
    {
        bool Stopped(bool set)
        {
            var (brain, keeper, ball) = Rig(Wall(), new Vector3(P.GoalLineX - 11f, 0.11f, 0f));
            if (!set) keeper.Velocity = new Vector3(0f, 0f, 2f);
            // A ball struck from 3 m, passing 0.55 m to his side at waist height: it arrives before he can react.
            var hands = new Vector3(keeper.Position.X, T.Keeper.HandSpanHeightFraction * keeper.Sheet.HeightM, keeper.Position.Z + 0.55f);
            ball.Position = hands - new Vector3(3f, 0f, 0f);
            ball.Velocity = new Vector3(25f, 0f, 0f);
            ball.Spin = Vector3.Zero;
            brain.OnStrike(new SimRng(1), ball);
            for (int i = 0; i < 20; i++)
            {
                brain.Update(Dt, ball, new SimRng(1));
                var res = ball.Step(Dt, null, brain.HandsSphere, brain.BodyCapsule, brain.HandsCapsule);
                if (res.SphereHit) return true;
            }
            return false;
        }
        Assert.True(Stopped(set: true));
        Assert.False(Stopped(set: false));
    }

    // ---- the wedge ----

    [Fact]
    public void TheDiveWedgeCoversSidewaysAndForwardDiagonalButNotStraightAheadBehindOrBeyondReach()
    {
        var feet = new Vector3(28f, 0f, 0f);
        var facing = new Vector2(-1f, 0f);
        Assert.True(Keeper.InWedge(feet, facing, new Vector3(28f, 1.0f, 2.0f), T));      // sideways
        Assert.True(Keeper.InWedge(feet, facing, new Vector3(27f, 0.3f, 2.0f), T));      // low, forward-diagonal
        Assert.True(Keeper.InWedge(feet, facing, new Vector3(28.1f, 1.2f, 0.2f), T));    // at the body
        Assert.False(Keeper.InWedge(feet, facing, new Vector3(26f, 0.3f, 0.1f), T));     // straight ahead, 2 m
        Assert.False(Keeper.InWedge(feet, facing, new Vector3(30f, 0.3f, 0.1f), T));     // straight behind
        Assert.False(Keeper.InWedge(feet, facing, new Vector3(28f, 0.3f, 3.6f), T));     // beyond 3.5 m
        Assert.False(Keeper.InWedge(feet, facing, new Vector3(27f, 1.5f, 2.0f), T));     // high and a metre forward
        Assert.Equal(75f, T.Keeper.WedgeHalfAngleDeg);
        Assert.Equal(0.8f, T.Keeper.WedgeLowCap);
    }

    [Fact]
    public void DiveKindFollowsWhereTheBallIsMet()
    {
        var feet = new Vector3(28f, 0f, 0f);
        Assert.Equal(KeeperDive.Catch, Keeper.DiveKindFor(feet, new Vector3(27.8f, 1.1f, 0.2f), T));
        Assert.Equal(KeeperDive.Feet, Keeper.DiveKindFor(feet, new Vector3(27.8f, 0.15f, 0.4f), T));
        Assert.Equal(KeeperDive.Low, Keeper.DiveKindFor(feet, new Vector3(28f, 0.3f, 2f), T));
        Assert.Equal(KeeperDive.Side, Keeper.DiveKindFor(feet, new Vector3(28f, 1.0f, 2f), T));
        Assert.Equal(KeeperDive.High, Keeper.DiveKindFor(feet, new Vector3(28f, 2.0f, 1.5f), T));
    }

    // ---- save kinds ----

    static (KeeperBrain Brain, PlayerState Keeper, Ball Ball) Diving(Vector3 offset, PlayerSheet? sheet = null)
    {
        var (brain, keeper, ball) = Rig(sheet ?? Wall(), new Vector3(P.GoalLineX - 11f, 0.11f, 0f));
        var target = keeper.Position + offset;
        brain.BeginDiveForTest(target, 0.4f);
        ball.Position = target;
        return (brain, keeper, ball);
    }

    [Fact]
    public void ASlowBallIsCaughtAFastOneParriedAndALowHardOneAtTheFeetBlocked()
    {
        var (b1, k1, ball1) = Diving(new Vector3(-0.2f, 1.1f, 0.2f));
        Assert.Equal(KeeperDive.Catch, k1.KeeperDive);
        ball1.Velocity = new Vector3(12f, 0f, 0f);
        Assert.True(b1.Contact(ball1, ball1.Position, new SimRng(1), out _));
        Assert.Equal(SaveKind.Catch, b1.Report.Save);
        Assert.Equal(KeeperAction.Holding, k1.KeeperAction);

        var (b2, _, ball2) = Diving(new Vector3(-0.2f, 1.0f, 1.2f));
        ball2.Velocity = new Vector3(32f, 0f, 0f);
        Assert.False(b2.Contact(ball2, ball2.Position, new SimRng(1), out _));
        Assert.Equal(SaveKind.Parry, b2.Report.Save);
        Assert.InRange(ball2.Velocity.Length(), 32f * 0.4f - 1e-3f, 32f * 0.6f + 1e-3f);

        var (b3, k3, ball3) = Diving(new Vector3(-0.3f, 0.15f, 0.3f));
        Assert.Equal(KeeperDive.Feet, k3.KeeperDive);
        ball3.Velocity = new Vector3(28f, 0f, 0f);
        Assert.False(b3.Contact(ball3, ball3.Position, new SimRng(1), out _));
        Assert.Equal(SaveKind.Block, b3.Report.Save);
        float v = ball3.Velocity.Length();
        Assert.True(v >= T.Keeper.BlockSpeed - 1e-3f && v <= 28f * T.Keeper.BlockSpeedMax + 1e-3f, $"block speed {v}");
        Assert.True(ball3.Velocity.X < 0f, "blocked back out");
    }

    [Fact]
    public void AParryNeverLeavesSlowerThanTheMinimumParrySpeed()
    {
        var s = Wall(); s.Handling = 1;
        // Beyond the catch zone, so not held however slow; a parry of a 5 m/s ball would leave at 2 to 3 m/s.
        var (b, _, ball) = Diving(new Vector3(-0.2f, 1.0f, 2.2f), s);
        ball.Velocity = new Vector3(5f, 0f, 0f);
        Assert.False(b.Contact(ball, ball.Position, new SimRng(1), out _));
        Assert.True(ball.Velocity.Length() >= T.Keeper.MinParrySpeed - 1e-3f);
    }

    [Fact]
    public void AHighBallNearTheLineIsTippedOverTheBar()
    {
        var (b, keeper, ball) = Diving(new Vector3(0.1f, 2.0f, 1.4f));
        Assert.True(P.GoalLineX - keeper.Position.X <= T.Keeper.TipOverBarDistance);
        Assert.Equal(KeeperDive.High, keeper.KeeperDive);
        ball.Velocity = new Vector3(26f, 1f, 0.5f);
        Assert.False(b.Contact(ball, ball.Position, new SimRng(3), out _));
        Assert.Equal(SaveKind.TipOver, b.Report.Save);
        // Played on, it clears the crossbar.
        for (int i = 0; i < 240 && ball.Position.X < P.GoalLineX; i++)
        {
            var prev = ball.Position;
            ball.Step(Dt, null, null);
            Assert.False(P.IsGoal(ball.Position, ball.Radius));
            if (prev.X < P.GoalLineX && ball.Position.X >= P.GoalLineX) Assert.True(ball.Position.Y > P.BarHeight, $"y at the line {ball.Position.Y}");
        }
    }

    [Fact]
    public void AFingertipAtTheEdgeOfHisStretchKeepsMostOfTheSpeedAndBendsItWide()
    {
        var s = Wall();
        float stretch = Keeper.Stretch(s, T);
        // Low enough not to be tipped over, at 95% of his stretch from his hands' rest point.
        var (b, keeper, ball) = Rig(s, new Vector3(P.GoalLineX - 11f, 0.11f, 0f));
        var home = new Vector3(keeper.Position.X, s.HeightM * T.Keeper.HomeHeightFraction, keeper.Position.Z);
        var contact = home + new Vector3(0f, -0.3f, MathF.Sqrt(MathF.Pow(0.95f * stretch, 2) - 0.09f));
        b.BeginDiveForTest(contact, 0.6f);
        ball.Position = contact;
        ball.Velocity = new Vector3(25f, 0f, 2f);
        float yaw0 = MathF.Atan2(ball.Velocity.Z, ball.Velocity.X);
        Assert.False(b.Contact(ball, contact, new SimRng(5), out _));
        Assert.Equal(SaveKind.Fingertip, b.Report.Save);
        var h = new Vector2(ball.Velocity.X, ball.Velocity.Z);
        Assert.Equal(new Vector2(25f, 2f).Length() * T.Keeper.FingertipSpeedKept, h.Length(), 3);
        float bend = (MathF.Atan2(h.Y, h.X) - yaw0) * 180f / MathF.PI;
        Assert.InRange(bend, 0.5f * T.Keeper.FingertipMaxBendDeg - 1e-3f, T.Keeper.FingertipMaxBendDeg + 1e-3f); // toward +Z, away from the centre
    }

    // ---- recovery ----

    [Fact]
    public void AfterADiveHeLiesThenGetsUpInOneSecondAndCanSaveAgainAfterPointTwo()
    {
        var (brain, keeper, ball) = Diving(new Vector3(0f, 1.0f, 2.0f));
        ball.Position = new Vector3(P.GoalLineX - 20f, 0.11f, 0f);
        int ticks = 0;
        while (keeper.KeeperAction == KeeperAction.Diving && ticks++ < 400) brain.Update(Dt, ball, new SimRng(1));
        Assert.Equal(KeeperAction.Grounded, keeper.KeeperAction);
        while (keeper.KeeperAction == KeeperAction.Grounded && ticks++ < 400) brain.Update(Dt, ball, new SimRng(1));
        Assert.Equal(KeeperAction.GettingUp, keeper.KeeperAction);
        Assert.Equal(0f, keeper.KeeperActionTime);
        int up = 0;
        while (keeper.KeeperAction == KeeperAction.GettingUp && up++ < 400) brain.Update(Dt, ball, new SimRng(1));
        Assert.Equal(KeeperAction.Set, keeper.KeeperAction);
        Assert.InRange(up * Dt, 1.0f - 1.5f * Dt, 1.0f + 1.5f * Dt);

        // A strike 0.1 s into getting up waits; he starts reacting 0.2 s in.
        var (b2, k2, ball2) = Diving(new Vector3(0f, 1.0f, 2.0f));
        ball2.Position = new Vector3(P.GoalLineX - 20f, 0.11f, 0f);
        ticks = 0;
        while (k2.KeeperAction != KeeperAction.GettingUp && ticks++ < 400) b2.Update(Dt, ball2, new SimRng(1));
        for (int i = 0; i < 12; i++) b2.Update(Dt, ball2, new SimRng(1));
        ball2.Velocity = new Vector3(20f, 1f, 0f);
        b2.OnStrike(new SimRng(2), ball2);
        Assert.Equal(KeeperAction.GettingUp, k2.KeeperAction);
        int waited = 0;
        while (k2.KeeperAction == KeeperAction.GettingUp && waited++ < 100) b2.Update(Dt, ball2, new SimRng(1));
        Assert.NotEqual(KeeperAction.GettingUp, k2.KeeperAction);
        Assert.InRange((12 + waited) * Dt, 0.2f - 1.5f * Dt, 0.2f + 1.5f * Dt);
    }

    // ---- the penalty: holding the line ----

    [Fact]
    public void OnAPenaltyHeHoldsHisLineUntilTheStrike()
    {
        var w = TestData.Live("penalty");
        Assert.True(w.Scenario.KeeperHoldLine);
        float lineX = w.Keeper.Position.X;
        Assert.Equal(w.Pitch.GoalLineX - Pitch.PlayerRadius, lineX, 3);
        var a = new TickIntent[2];
        // The penalty taker moves the ball sideways before shooting: the keeper only shuffles along his line.
        w.Ball.Position = new Vector3(w.Ball.Position.X, w.Ball.Radius, 4f);
        for (int i = 0; i < 90; i++) { w.Step(a); Assert.Equal(lineX, w.Keeper.Position.X, 3); Assert.NotEqual(KeeperAction.Rush, w.Keeper.KeeperAction); }
        var spot = Keeper.LineSpot(w.Ball.Position, w.Pitch, w.Tuning);
        Assert.True(spot.Z > 0.2f);
        Assert.InRange(w.Keeper.Position.Z, spot.Z - 0.4f, spot.Z + 0.4f); // shuffled along his line toward the ball's side
    }

    // ---- the contract ----

    [Theory]
    [InlineData("penalty")]
    [InlineData("tight_angle")]
    [InlineData("keeper_rush")]
    [InlineData("breakaway")]
    public void TheContractIsFilledEveryTick(string scenario)
    {
        for (ulong seed = 1; seed <= 6; seed++)
        {
            var w = TestData.Live(scenario, seed: seed);
            var script = new ScriptedAttacker();
            var buf = new TickIntent[w.PlayerCount];
            var lastAction = w.Keeper.KeeperAction;
            float lastTime = w.Keeper.KeeperActionTime;
            for (int i = 0; i < 4000 && w.State != RoundState.Report; i++)
            {
                buf[0] = script.Next(w);
                w.Step(buf);
                var k = w.Keeper;
                if (k.KeeperAction != lastAction) Assert.Equal(0f, k.KeeperActionTime);
                else Assert.Equal(lastTime + w.Tuning.Dt, k.KeeperActionTime, 4);
                Assert.Equal(k.KeeperAction == KeeperAction.Diving, k.KeeperDive != KeeperDive.None);
                if (k.KeeperAction is KeeperAction.Set or KeeperAction.Shuffle or KeeperAction.Planted or KeeperAction.Rush)
                    Assert.Equal(k.Sheet.HeightM * w.Tuning.Keeper.HomeHeightFraction, k.HandPosition.Y, 3);
                Assert.True(k.Velocity.Length() < 12f, $"speed {k.Velocity.Length()}");
                lastAction = k.KeeperAction;
                lastTime = k.KeeperActionTime;
            }
        }
    }
}
