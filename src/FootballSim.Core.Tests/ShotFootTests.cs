using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>
/// The striking foot and the angle a shot is struck at: the foot rules (the strong foot across the body, its outside toward its
/// own side up to the cap, the weak foot beyond, the foot on the ball's side for a wide shot, the far-post rule, the contact
/// check and its dead-ahead swap), the body-angle and goal-mouth terms, the weak-foot and outside-foot terms, and the foot and aim locked in the world.
/// </summary>
public class ShotFootTests
{
    static readonly Tuning T = new();
    static readonly Pitch P = new(new PitchSpec(), T.Frame);
    const float Deg = MathF.PI / 180f;
    static readonly ShotType[] Types = { ShotType.Power, ShotType.Driven, ShotType.Finesse, ShotType.LowFinesse, ShotType.Chip };

    /// <summary>striker_a's ratings (curve 72: a 57 degree cap), with the weak foot, preferred foot and trait under test.</summary>
    static PlayerSheet Sheet(int weakFoot = 50, bool trait = false, Foot preferred = Foot.Right)
    {
        var s = TestData.Player("striker_a").Clone();
        s.WeakFoot = weakFoot;
        s.PreferredFoot = preferred;
        s.Traits = trait ? PlayerTrait.OutsideFoot : PlayerTrait.None;
        return s;
    }

    static Foot Other(Foot f) => f == Foot.Right ? Foot.Left : Foot.Right;

    /// <summary>
    /// The choice for a player on the penalty spot facing the goal (+X), the ball leaving <paramref name="yawDeg"/> to his right.
    /// On the centre line he is on neither flank, so the far-post rule is off.
    /// </summary>
    static StrikeChoice Choose(PlayerSheet s, float yawDeg, ShotType type = ShotType.Power, int right = 0, int left = 0)
    {
        var spot = new Vector3(P.GoalLineX - 11f, 0f, 0f);
        return ShotModel.ChooseStrike(s, spot, 0f, new Vector3(spot.X + 0.5f, T.Ball.Radius, 0f), yawDeg * Deg, type, P, T, right, left);
    }

    /// <summary>
    /// The choice for a player at (<paramref name="outM"/> metres from the goal line, <paramref name="z"/>) with the ball at his
    /// feet, aiming at <paramref name="aimZ"/> on the goal line, that aim <paramref name="thetaDeg"/> to his right.
    /// </summary>
    static StrikeChoice ChooseAt(PlayerSheet s, float outM, float z, float aimZ, float thetaDeg, ShotType type = ShotType.Power, int right = 0, int left = 0)
    {
        var pos = new Vector3(P.GoalLineX - outM, 0f, z);
        var ball = new Vector3(pos.X, T.Ball.Radius, pos.Z);
        float yaw = MathF.Atan2(aimZ - z, outM);
        return ShotModel.ChooseStrike(s, pos, yaw - thetaDeg * Deg, ball, yaw, type, P, T, right, left);
    }

    /// <summary>The geometry's decision for <see cref="Choose"/>'s player on the spot.</summary>
    static StrikeDecision Decide(PlayerSheet s, float yawDeg, ShotType type = ShotType.Power)
    {
        var spot = new Vector3(P.GoalLineX - 11f, 0f, 0f);
        return ShotModel.ChooseByGeometry(s, spot, 0f, new Vector3(spot.X + 0.5f, T.Ball.Radius, 0f), yawDeg * Deg, type, P, T);
    }

    /// <summary>The geometry's decision for <see cref="ChooseAt"/>'s player.</summary>
    static StrikeDecision DecideAt(PlayerSheet s, float outM, float z, float aimZ, float thetaDeg, ShotType type = ShotType.Power)
    {
        var pos = new Vector3(P.GoalLineX - outM, 0f, z);
        float yaw = MathF.Atan2(aimZ - z, outM);
        return ShotModel.ChooseByGeometry(s, pos, yaw - thetaDeg * Deg, new Vector3(pos.X, T.Ball.Radius, pos.Z), yaw, type, P, T);
    }

    /// <summary>Each case for a right-footed player and, mirrored, for a left-footed one: <paramref name="check"/> gets the strong foot and the sign of his own side.</summary>
    static void BothFeet(Action<Foot, float> check)
    {
        check(Foot.Right, 1f);
        check(Foot.Left, -1f);
    }

    [Fact]
    public void AcrossTheBodyTheStrongFootStrikesWhateverTheWeakFootIsWorth()
    {
        BothFeet((strong, own) =>
        {
            foreach (int wf in new[] { 1, 99 })
                foreach (float across in new[] { 2f, 30f, 90f, 149f })
                    Assert.Equal(new StrikeChoice(strong, FootSurface.Laces), Choose(Sheet(wf, false, strong), -own * across));
        });
    }

    [Fact]
    public void TowardHisOwnSideTheOutsideOfTheStrongFootStrikesUpToTheCapAndTheWeakFootBeyond()
    {
        BothFeet((strong, own) =>
        {
            var plain = Sheet(50, false, strong);
            var trait = Sheet(50, true, strong);
            Assert.InRange(ShotModel.OutsideCapDeg(plain, ShotType.Power, T), 56.5f, 57.5f);     // curve 72: about 57 degrees
            Assert.Equal(67.5f, ShotModel.OutsideCapDeg(trait, ShotType.Power, T), 3);           // + 12, held at the ceiling
            // Everyone has the outside, with or without the trait.
            Assert.Equal(new StrikeChoice(strong, FootSurface.Outside), Choose(plain, own * 40f));
            Assert.Equal(new StrikeChoice(strong, FootSurface.Outside), Choose(trait, own * 40f));
            // Beyond his cap the weak foot strikes with the type's own surface; the trait's wider cap still covers 60 degrees.
            Assert.Equal(new StrikeChoice(Other(strong), FootSurface.Laces), Choose(plain, own * 60f));
            Assert.Equal(new StrikeChoice(strong, FootSurface.Outside), Choose(trait, own * 60f));
            Assert.Equal(new StrikeChoice(Other(strong), FootSurface.Laces), Choose(trait, own * 70f));
            Assert.Equal(new StrikeChoice(Other(strong), FootSurface.Inside), Choose(plain, own * 60f, ShotType.Finesse));
            // The weak foot's rating has no say in the choice.
            foreach (int wf in new[] { 1, 99 })
            {
                Assert.Equal(strong, Choose(Sheet(wf, false, strong), own * 40f).Foot);
                Assert.Equal(Other(strong), Choose(Sheet(wf, false, strong), own * 60f).Foot);
            }
        });
    }

    [Fact]
    public void TheCapComesFromTheCurveRatingTheTraitWidensItAndTheShotTypeCeilsIt()
    {
        var s = Sheet();
        s.Curve = 1;
        Assert.Equal(30f, ShotModel.OutsideCapDeg(s, ShotType.Power, T), 3);
        s.Traits = PlayerTrait.OutsideFoot;
        Assert.Equal(42f, ShotModel.OutsideCapDeg(s, ShotType.Power, T), 3);
        s.Curve = 99;
        foreach (var type in Types)
            Assert.Equal(type is ShotType.Finesse or ShotType.LowFinesse ? 79f : 67.5f, ShotModel.OutsideCapDeg(s, type, T), 3);
        s.Traits = PlayerTrait.None;
        Assert.Equal(67.5f, ShotModel.OutsideCapDeg(s, ShotType.Finesse, T), 3);
        // A finesse reaches further for a curler with the trait: 75 degrees is still his strong foot's.
        s.Traits = PlayerTrait.OutsideFoot;
        Assert.Equal(new StrikeChoice(Foot.Left, FootSurface.Laces), Choose(s, 75f, ShotType.Power));
        Assert.Equal(new StrikeChoice(Foot.Right, FootSurface.Outside), Choose(s, 75f, ShotType.Finesse));
        Assert.Equal(new StrikeChoice(Foot.Left, FootSurface.Inside), Choose(s, 80f, ShotType.Finesse));
    }

    [Fact]
    public void AnAngleOnALimitCountsAsInsideIt()
    {
        BothFeet((strong, own) =>
        {
            foreach (int curve in new[] { 1, 40, 72, 99 })
            {
                var s = Sheet(50, false, strong);
                s.Curve = curve;
                float cap = ShotModel.OutsideCapDeg(s, ShotType.Power, T);
                Assert.Equal(new StrikeChoice(strong, FootSurface.Outside), Choose(s, own * cap));
                Assert.Equal(new StrikeChoice(Other(strong), FootSurface.Laces), Choose(s, own * (cap + 0.1f)));
            }
            var p = Sheet(50, false, strong);
            // At foot_by_side_deg itself the shot is not wide yet: the general rule still holds either way.
            Assert.Equal(150f, T.Shot.FootBySideDeg);
            Assert.Equal(new StrikeChoice(Other(strong), FootSurface.Laces), Choose(p, own * 150f));
            Assert.Equal(new StrikeChoice(strong, FootSurface.Laces), Choose(p, -own * 150f));
            // Dead ahead (within dead_ahead_deg) is the strong foot's own surface, not its outside.
            Assert.Equal(new StrikeChoice(strong, FootSurface.Laces), Choose(p, 0f));
            Assert.Equal(new StrikeChoice(strong, FootSurface.Laces), Choose(p, own * T.Shot.DeadAheadDeg));
            Assert.Equal(new StrikeChoice(strong, FootSurface.Outside), Choose(p, own * (T.Shot.DeadAheadDeg + 0.1f)));
            Assert.Equal(22.5f, T.Shot.DeadAheadDeg);
        });
    }

    [Fact]
    public void AWideShotIsStruckByTheFootOnTheSideTheBallLeaves()
    {
        foreach (Foot strong in new[] { Foot.Right, Foot.Left })
            foreach (int wf in new[] { 1, 99 })
                foreach (bool trait in new[] { false, true })
                    foreach (var type in Types)
                    {
                        var s = Sheet(wf, trait, strong);
                        foreach (float wide in new[] { 150.1f, 160f, 179f })
                        {
                            Assert.Equal(new StrikeChoice(Foot.Right, ShotModel.SurfaceFor(type)), Choose(s, wide, type));
                            Assert.Equal(new StrikeChoice(Foot.Left, ShotModel.SurfaceFor(type)), Choose(s, -wide, type));
                        }
                    }
    }

    [Fact]
    public void DeadAheadAndStraightBehindGoToTheStrongFoot()
    {
        BothFeet((strong, own) =>
        {
            var s = Sheet(50, false, strong);
            // Straight behind is never "wide", from either side of the wrap-around.
            foreach (float yaw in new[] { 0f, 180f, -180f, 179.9f, -179.9f, 179.6f, -179.6f, 540f, -540f })
                Assert.Equal(new StrikeChoice(strong, FootSurface.Laces), Choose(s, yaw));
        });
        // Just short of straight behind the side decides again.
        Assert.Equal(Foot.Left, Choose(Sheet(), -179f).Foot);
        Assert.Equal(Foot.Right, Choose(Sheet(50, false, Foot.Left), 179f).Foot);
    }

    [Fact]
    public void TheSameShotGivesTheSameStrikeAndNoOutsideAcrossTheBody()
    {
        foreach (var type in Types)
            for (float yaw = -180f; yaw <= 180f; yaw += 2.5f)
            {
                var s = Sheet(50, true);
                var c = Choose(s, yaw, type);
                Assert.Equal(c, Choose(s, yaw, type));
                Assert.Equal(c, Choose(Sheet(1, true), yaw, type));        // the weak foot's rating never decides
                float cap = ShotModel.OutsideCapDeg(s, type, T);
                Assert.Equal(yaw > T.Shot.DeadAheadDeg && yaw <= cap, c.Surface == FootSurface.Outside);
                if (c.Surface != FootSurface.Outside) Assert.Equal(ShotModel.SurfaceFor(type), c.Surface);
            }
    }

    [Fact]
    public void AnInvertedShooterCurlsAFarPostShotWithHisNormalSurface()
    {
        // A right-footer on the left flank (Z below 0), 16 m out, aiming at the far post 30 degrees to his right: within his
        // cap, where the general rule gives the outside of the foot, the far-post rule keeps the type's own surface.
        Assert.Equal(new StrikeChoice(Foot.Right, FootSurface.Laces), ChooseAt(Sheet(), 16f, -6f, 3f, 30f));
        Assert.Equal(new StrikeChoice(Foot.Right, FootSurface.Inside), ChooseAt(Sheet(), 16f, -6f, 3f, 30f, ShotType.Finesse));
        // On his natural flank the same far-post aim to his right (he faces away from goal) is the outside as usual.
        Assert.Equal(new StrikeChoice(Foot.Right, FootSurface.Outside), ChooseAt(Sheet(), 16f, 6f, -3f, 30f));
        // The mirror: a left-footer on the right flank, the far post to his left.
        var lefty = Sheet(50, false, Foot.Left);
        Assert.Equal(new StrikeChoice(Foot.Left, FootSurface.Laces), ChooseAt(lefty, 16f, 6f, -3f, -30f));
        Assert.Equal(new StrikeChoice(Foot.Left, FootSurface.Outside), ChooseAt(lefty, 16f, -6f, 3f, -30f));
        // Only that one case changes: beyond the cap it is the weak foot, across the body the strong foot, a near-post aim the outside.
        Assert.Equal(new StrikeChoice(Foot.Left, FootSurface.Laces), ChooseAt(Sheet(), 16f, -6f, 3f, 60f));
        Assert.Equal(new StrikeChoice(Foot.Right, FootSurface.Laces), ChooseAt(Sheet(), 16f, -6f, 3f, -30f));
        Assert.Equal(new StrikeChoice(Foot.Right, FootSurface.Outside), ChooseAt(Sheet(), 16f, -6f, -3f, 30f));
        // With the trait the wider cap covers 60 degrees, and the far-post rule with it.
        Assert.Equal(new StrikeChoice(Foot.Right, FootSurface.Laces), ChooseAt(Sheet(50, true), 16f, -6f, 3f, 60f));
    }

    [Fact]
    public void OnTheCentreLineOrAimedAtTheCentreTheFarPostRuleStaysOff()
    {
        var s = Sheet();
        float flank = T.Shot.FarPostFlankMinM;
        Assert.Equal(0.3f, flank);
        // Within far_post_flank_min_m of the centre line he is on neither flank, whichever side of it he stands.
        foreach (float z in new[] { 0f, 0.1f, -0.1f, flank - 0.01f, -(flank - 0.01f) })
            foreach (float aimZ in new[] { 3f, -3f })
                Assert.Equal(new StrikeChoice(Foot.Right, FootSurface.Outside), ChooseAt(s, 16f, z, aimZ, 30f));
        Assert.Equal(new StrikeChoice(Foot.Right, FootSurface.Laces), ChooseAt(s, 16f, -(flank + 0.01f), 3f, 30f));
        // An aim at the centre of the goal is at neither post.
        Assert.Equal(new StrikeChoice(Foot.Right, FootSurface.Outside), ChooseAt(s, 16f, -6f, 0f, 30f));
        var ball = new Vector3(P.GoalLineX - 16f, T.Ball.Radius, -6f);
        Assert.False(ShotModel.FarPostApplies(ShotType.Power, ball, 0f, ball, P, T));
        Assert.False(ShotModel.FarPostApplies(ShotType.Power, ball, 0.0005f, ball, P, T));
        Assert.True(ShotModel.FarPostApplies(ShotType.Power, ball, 0.5f, ball, P, T));
        Assert.False(ShotModel.FarPostApplies(ShotType.Power, ball, -0.5f, ball, P, T));
        Assert.True(ShotModel.Inverted(Foot.Right, ball) && !ShotModel.Inverted(Foot.Left, ball));
        var centre = new Vector3(ball.X, 0f, 0f);
        Assert.False(ShotModel.Inverted(Foot.Right, centre) || ShotModel.Inverted(Foot.Left, centre));
    }

    [Fact]
    public void TheFarPostRuleHoldsOnlyInsideTheShotTypesDistanceBand()
    {
        var s = Sheet();
        // A chip only within 2.5 m of the goal centre: from 16 m the general rule stands.
        Assert.Equal(new StrikeChoice(Foot.Right, FootSurface.Outside), ChooseAt(s, 16f, -6f, 3f, 30f, ShotType.Chip));
        // From 1.6 m off the goal line and 1.2 m off centre (2 m from the goal centre) it is on.
        Assert.Equal(new StrikeChoice(Foot.Right, FootSurface.Inside), ChooseAt(s, 1.6f, -1.2f, 2f, 30f, ShotType.Chip));
        Assert.Equal(new StrikeChoice(Foot.Right, FootSurface.Outside), ChooseAt(s, 2.4f, -1.2f, 2f, 30f, ShotType.Chip));
        // Power and driven up to 50 m, finesse and low finesse at any distance.
        foreach (var type in Types)
        {
            var b = T.Shot.FarPostBand.For(type);
            Assert.True(b.Min < 0f);
            Assert.Equal(type switch { ShotType.Chip => 2.5f, ShotType.Power or ShotType.Driven => 50f, _ => -1f }, b.Max);
        }
        var near = new Vector3(P.GoalLineX - 49f, T.Ball.Radius, -6f);
        var far = new Vector3(P.GoalLineX - 51f, T.Ball.Radius, -6f);
        foreach (var type in new[] { ShotType.Power, ShotType.Driven })
        {
            Assert.True(ShotModel.FarPostApplies(type, near, 3f, near, P, T));
            Assert.False(ShotModel.FarPostApplies(type, far, 3f, far, P, T));
        }
        foreach (var type in new[] { ShotType.Finesse, ShotType.LowFinesse })
            Assert.True(ShotModel.FarPostApplies(type, far, 3f, far, P, T));
        // A lower end too, when a band has one.
        var t = new Tuning();
        t.Shot.FarPostBand.Power = new[] { 20f, 30f };
        Assert.False(ShotModel.FarPostApplies(ShotType.Power, new Vector3(P.GoalLineX - 16f, 0f, -6f), 3f, near, P, t));
        Assert.True(ShotModel.FarPostApplies(ShotType.Power, new Vector3(P.GoalLineX - 25f, 0f, -6f), 3f, near, P, t));
    }

    [Fact]
    public void OnOrBehindTheGoalLineTheFarPostRuleIsStillAPureFunctionOfThePositions()
    {
        var s = Sheet();
        foreach (float dx in new[] { 0f, -0.5f, -3f })
            foreach (float z in new[] { -6f, -5f, 5f, 6f })
                foreach (float yawDeg in new[] { -170f, -90f, -30f, 0f, 30f, 90f, 170f })
                    foreach (var type in Types)
                    {
                        var pos = new Vector3(P.GoalLineX - dx, 0f, z);
                        var ball = new Vector3(pos.X, T.Ball.Radius, pos.Z);
                        float yaw = yawDeg * Deg, aimZ = ShotModel.AimZ(ball, yaw, P, T);
                        Assert.True(float.IsFinite(aimZ) && MathF.Abs(aimZ) <= T.Shot.MaxTargetZ);
                        bool far = ShotModel.FarPostApplies(type, ball, aimZ, pos, P, T);
                        Assert.Equal(far, ShotModel.FarPostApplies(type, ball, aimZ, pos, P, T));
                        // The distance to the goal centre still sets the band, and the halves of the pitch the rest.
                        var (_, max) = T.Shot.FarPostBand.For(type);
                        bool inBand = max < 0f || ShotModel.GoalDistance(ball, P) <= max;
                        Assert.Equal(inBand && MathF.Abs(aimZ) >= 1e-3f && MathF.Sign(aimZ) != MathF.Sign(z), far);
                        var c = ShotModel.ChooseStrike(s, pos, 0f, ball, yaw, type, P, T);
                        Assert.Equal(c, ShotModel.ChooseStrike(s, pos, 0f, ball, yaw, type, P, T));
                    }
    }

    [Fact]
    public void AChosenFootWithNoContactHandsTheShotToTheOtherAndTheStrideNeverChoosesOtherwise()
    {
        var s = Sheet();
        // Across the body the geometry gives the right foot. With no contact for it and one for the left, the left strikes.
        Assert.Equal(new StrikeChoice(Foot.Left, FootSurface.Laces), Choose(s, -30f, right: -1, left: 30));
        // With no contact for either the choice stands.
        Assert.Equal(new StrikeChoice(Foot.Right, FootSurface.Laces), Choose(s, -30f, right: -1, left: -1));
        // However much later the chosen foot's swing comes, it is waited for.
        Assert.Equal(new StrikeChoice(Foot.Right, FootSurface.Laces), Choose(s, -30f, right: 400, left: 30));
        Assert.Equal(new StrikeChoice(Foot.Right, FootSurface.Laces), Choose(s, -30f, right: 30, left: -1));
        // An outside-of-the-foot choice handed over is struck with the type's own surface.
        Assert.Equal(new StrikeChoice(Foot.Right, FootSurface.Outside), Choose(s, 40f, right: 90, left: 30));
        Assert.Equal(new StrikeChoice(Foot.Left, FootSurface.Laces), Choose(s, 40f, right: -1, left: 30));
        Assert.Equal(new StrikeChoice(Foot.Left, FootSurface.Inside), Choose(s, 40f, ShotType.Finesse, right: -1, left: 30));
        // A weak-foot choice (beyond the cap) hands over to the strong foot the same way.
        Assert.Equal(new StrikeChoice(Foot.Left, FootSurface.Laces), Choose(s, 80f, right: 30, left: 90));
        Assert.Equal(new StrikeChoice(Foot.Right, FootSurface.Laces), Choose(s, 80f, right: 30, left: -1));
        // A wide shot too.
        Assert.Equal(new StrikeChoice(Foot.Right, FootSurface.Laces), Choose(s, -160f, right: 30, left: -1));
        // Both ticks zero: no timing at all.
        Assert.Equal(new StrikeChoice(Foot.Right, FootSurface.Laces), Choose(s, -30f, right: 0, left: 0));
    }

    [Fact]
    public void DeadAheadAWeakFootThatSwingsSoonEnoughTakesTheShot()
    {
        Assert.Equal(12, ShotModel.SwapLeadTicks(T));           // 0.1 s in whole ticks
        BothFeet((strong, own) =>
        {
            var s = Sheet(preferred: strong);
            Foot weak = Other(strong);
            // Each foot's earliest strike tick, as (right, left).
            StrikeChoice At(float yawDeg, int strongTick, int weakTick) =>
                strong == Foot.Right ? Choose(s, yawDeg, right: strongTick, left: weakTick) : Choose(s, yawDeg, right: weakTick, left: strongTick);
            foreach (float yaw in new[] { 10f, -10f, 0f, T.Shot.DeadAheadDeg, -T.Shot.DeadAheadDeg })
            {
                // The weak foot 0.1 s sooner: it strikes, with the type's own surface. A tick less than that: the strong foot.
                Assert.Equal(new StrikeChoice(weak, FootSurface.Laces), At(yaw, 40, 28));
                Assert.Equal(new StrikeChoice(weak, FootSurface.Laces), At(yaw, 400, 28));
                Assert.Equal(new StrikeChoice(strong, FootSurface.Laces), At(yaw, 40, 29));
                Assert.Equal(new StrikeChoice(strong, FootSurface.Laces), At(yaw, 28, 40));
                // A foot with no contact: the other one, as anywhere (the contact check).
                Assert.Equal(new StrikeChoice(strong, FootSurface.Laces), At(yaw, 40, -1));
                Assert.Equal(new StrikeChoice(weak, FootSurface.Laces), At(yaw, -1, 40));
                Assert.Equal(new StrikeChoice(strong, FootSurface.Laces), At(yaw, -1, -1));
                // Both ticks zero: no timing, no swap.
                Assert.Equal(new StrikeChoice(strong, FootSurface.Laces), At(yaw, 0, 0));
            }
            Assert.Equal(new StrikeChoice(weak, FootSurface.Inside), strong == Foot.Right
                ? Choose(s, 10f, ShotType.Finesse, right: 40, left: 28) : Choose(s, 10f, ShotType.Finesse, right: 28, left: 40));
            // However poor the weak foot is, the swap is taken.
            var poor = Sheet(1, preferred: strong);
            Assert.Equal(weak, (strong == Foot.Right ? Choose(poor, 10f, right: 40, left: 28) : Choose(poor, 10f, right: 28, left: 40)).Foot);

            // Outside the dead-ahead zone the same ticks change nothing: across the body, the outside, the weak foot beyond the
            // cap, wide either way, straight behind.
            Assert.Equal(new StrikeChoice(strong, FootSurface.Laces), At(-own * 30f, 40, 28));
            Assert.Equal(new StrikeChoice(strong, FootSurface.Outside), At(own * 40f, 40, 28));
            Assert.Equal(new StrikeChoice(strong, FootSurface.Outside), At(own * (T.Shot.DeadAheadDeg + 0.1f), 40, 28));
            Assert.Equal(new StrikeChoice(weak, FootSurface.Laces), At(own * 60f, 28, 40));
            Assert.Equal(new StrikeChoice(strong, FootSurface.Laces), At(own * 160f, 40, 28));
            Assert.Equal(new StrikeChoice(weak, FootSurface.Laces), At(-own * 160f, 28, 40));
            Assert.Equal(new StrikeChoice(strong, FootSurface.Laces), At(180f, 40, 28));
            // ... and the far-post case (an inverted shooter, 30 degrees toward his own side).
            Assert.Equal(new StrikeChoice(strong, FootSurface.Laces), strong == Foot.Right
                ? ChooseAt(s, 16f, -6f, 3f, 30f, right: 40, left: 28) : ChooseAt(s, 16f, 6f, -3f, -30f, right: 28, left: 40));

            // The swap is only for a foot chosen fresh: without it the decision stands, but for a foot with no contact.
            var ahead = Decide(s, own * 10f);
            int r = strong == Foot.Right ? 40 : 28, l = strong == Foot.Right ? 28 : 40;
            Assert.Equal(new StrikeChoice(weak, FootSurface.Laces), ShotModel.WithContact(ahead, ShotType.Power, r, l, allowSwap: true, T));
            Assert.Equal(new StrikeChoice(strong, FootSurface.Laces), ShotModel.WithContact(ahead, ShotType.Power, r, l, allowSwap: false, T));
            Assert.Equal(new StrikeChoice(weak, FootSurface.Laces),
                ShotModel.WithContact(ahead, ShotType.Power, strong == Foot.Right ? -1 : 40, strong == Foot.Right ? 40 : -1, allowSwap: false, T));
        });

        // The lead is dead_ahead_swap_s in whole ticks.
        var t = new Tuning();
        t.Shot.DeadAheadSwapS = 0.2f;
        Assert.Equal(24, ShotModel.SwapLeadTicks(t));
        var d = Decide(Sheet(), 10f);
        Assert.Equal(Foot.Right, ShotModel.WithContact(d, ShotType.Power, 40, 17, allowSwap: true, t).Foot);
        Assert.Equal(Foot.Left, ShotModel.WithContact(d, ShotType.Power, 40, 16, allowSwap: true, t).Foot);
    }

    [Fact]
    public void TheGeometryNamesTheRuleThatDecided()
    {
        BothFeet((strong, own) =>
        {
            var s = Sheet(preferred: strong);
            Foot weak = Other(strong);
            StrikeDecision D(Foot foot, FootSurface surface, StrikeRule rule) => new(new StrikeChoice(foot, surface), rule);
            Assert.Equal(D(strong, FootSurface.Laces, StrikeRule.Behind), Decide(s, 180f));
            Assert.Equal(D(strong, FootSurface.Laces, StrikeRule.Behind), Decide(s, -179.8f));
            Assert.Equal(D(strong, FootSurface.Laces, StrikeRule.Wide), Decide(s, own * 160f));
            Assert.Equal(D(weak, FootSurface.Laces, StrikeRule.Wide), Decide(s, -own * 160f));
            // Dead ahead on both sides of the facing, up to and on dead_ahead_deg; just past it, the outside or across.
            foreach (float yaw in new[] { 0f, 10f, -10f, T.Shot.DeadAheadDeg, -T.Shot.DeadAheadDeg })
                Assert.Equal(D(strong, FootSurface.Laces, StrikeRule.Ahead), Decide(s, yaw));
            Assert.Equal(D(strong, FootSurface.Outside, StrikeRule.Outside), Decide(s, own * (T.Shot.DeadAheadDeg + 0.1f)));
            Assert.Equal(D(strong, FootSurface.Laces, StrikeRule.Across), Decide(s, -own * (T.Shot.DeadAheadDeg + 0.1f)));
            Assert.Equal(D(strong, FootSurface.Laces, StrikeRule.Across), Decide(s, -own * 90f));
            Assert.Equal(D(strong, FootSurface.Outside, StrikeRule.Outside), Decide(s, own * 40f));
            Assert.Equal(D(weak, FootSurface.Laces, StrikeRule.Weak), Decide(s, own * 60f));
            Assert.Equal(D(weak, FootSurface.Inside, StrikeRule.Weak), Decide(s, own * 80f, ShotType.Finesse));
            // The far-post rule for an inverted shooter; on his natural flank the same aim is the outside's.
            Assert.Equal(D(strong, FootSurface.Laces, StrikeRule.FarPost), DecideAt(s, 16f, -own * 6f, own * 3f, own * 30f));
            Assert.Equal(D(strong, FootSurface.Outside, StrikeRule.Outside), DecideAt(s, 16f, own * 6f, -own * 3f, own * 30f));
            Assert.Equal(D(weak, FootSurface.Laces, StrikeRule.Weak), DecideAt(s, 16f, -own * 6f, own * 3f, own * 60f));
        });
    }

    static PlayerState Shooter(float facing = 0f, float x = 41f, float z = 0f, PlayerSheet? sheet = null)
    {
        var s = sheet ?? TestData.Sheet(60);
        return TestData.Outfield(s, x, z, facing);
    }

    static ShotContext Ctx(PlayerState p, Vector3? ball = null, ShotType type = ShotType.Power, Vector2? stick = null, Foot? foot = null, FootSurface? surface = null) => new()
    {
        Shooter = p,
        BallPosition = ball ?? new Vector3(p.Position.X + 0.5f, T.Ball.Radius, p.Position.Z),
        Stick = stick ?? new Vector2(1f, 0f),
        Charge = 0.5f,
        Type = type,
        Foot = foot,
        Surface = surface,
    };

    [Fact]
    public void TheBodyAngleSpreadsSlowsAndDelaysTheShot()
    {
        float straight = ShotModel.Compute(Ctx(Shooter()), P, T, new SimRng(1)).Speed;
        foreach (float deg in new[] { 0f, 20f, 45f, 60f, 90f, 120f, 135f, 180f })
        {
            // The shot goes along +X; the shooter faces 'deg' away from it.
            var r = ShotModel.Compute(Ctx(Shooter(facing: deg * Deg)), P, T, new SimRng(1));
            Assert.Equal(deg, r.BodyAngleDeg, 2);
            Assert.Equal(T.Shot.AngleAccuracy.Evaluate(deg), r.Factor("angle"), 3);
            Assert.Equal(T.Shot.AnglePower.Evaluate(deg), r.SpeedFactor("angle"), 3);
            Assert.Equal(straight * T.Shot.AnglePower.Evaluate(deg), r.Speed, 2);    // the same seed: the same speed noise
            Assert.Equal(T.Shot.AngleExtraWindupS.Evaluate(deg), ShotModel.ExtraWindupSeconds(deg * Deg, 0f, T), 4);
        }
        Assert.Equal(1f, T.Shot.AngleAccuracy.Evaluate(30f));
        Assert.Equal(1f, T.Shot.AnglePower.Evaluate(45f));
        Assert.Equal(0f, T.Shot.AngleExtraWindupS.Evaluate(45f));
        Assert.Equal(0.30f, ShotModel.ExtraWindupSeconds(MathF.PI, 0f, T), 4);
        Assert.True(T.Shot.AngleAccuracy.Evaluate(180f) > T.Shot.AngleAccuracy.Evaluate(90f));
        Assert.True(T.Shot.AnglePower.Evaluate(180f) < T.Shot.AnglePower.Evaluate(90f));
    }

    [Fact]
    public void TheGoalMouthTermGrowsAsTheMouthClosesAndStaysFiniteOnAndBehindTheGoalLine()
    {
        float gx = P.GoalLineX, hw = P.GoalHalfWidth, r0 = T.Ball.Radius;
        // In front of goal at any distance the mouth is fully open.
        foreach (float d in new[] { 5f, 11f, 20f, 30f })
            Assert.Equal(1f, ShotModel.GoalMouthOpen(new Vector3(gx - d, r0, 0f), P), 4);
        // Out wide near the goal line it closes.
        float wide = ShotModel.GoalMouthOpen(new Vector3(gx - 4.7f, r0, 8.5f), P);
        float tight = ShotModel.GoalMouthOpen(new Vector3(gx - 0.3f, r0, 5.2f), P);
        Assert.InRange(wide, 0.5f, 0.62f);
        Assert.InRange(tight, 0.1f, 0.16f);
        float first = T.Shot.GoalMouthAccuracy[0].Y;
        foreach (var ball in new[] { new Vector3(gx, r0, hw + 2f), new Vector3(gx + 0.5f, r0, hw + 2f), new Vector3(gx, r0, 0f), new Vector3(gx - 0.3f, r0, 5.2f), new Vector3(gx - 4.7f, r0, 8.5f) })
        {
            var p = Shooter(facing: -MathF.PI / 2, x: ball.X, z: ball.Z + 0.5f);
            foreach (var stick in new[] { Vector2.Zero, new Vector2(0f, -1f), new Vector2(1f, 0f) })
            {
                var r = ShotModel.Compute(Ctx(p, ball, stick: stick), P, T, new SimRng(3));
                Assert.True(float.IsFinite(r.SigmaH) && r.SigmaH > 0f, $"ball {ball}: sigma {r.SigmaH}");
                Assert.True(float.IsFinite(r.Launch.X) && float.IsFinite(r.Launch.Y) && float.IsFinite(r.Launch.Z));
                Assert.Equal(T.Shot.GoalMouthAccuracy.Evaluate(ShotModel.GoalMouthOpen(ball, P)), r.Factor("mouth"), 4);
                if (ball.X >= gx) Assert.Equal(first, r.Factor("mouth"), 4);   // on or behind the line: the curve's first point
            }
        }
        // On or behind the goal line the intended yaw falls back to the stick (the facing with the stick at rest).
        var behind = Shooter(facing: -MathF.PI / 2, x: gx, z: hw + 2.5f);
        var ballOn = new Vector3(gx, r0, hw + 2f);
        Assert.Equal(-MathF.PI / 2, ShotModel.Compute(Ctx(behind, ballOn, stick: Vector2.Zero), P, T, new SimRng(1)).IntendedYaw, 4);
        Assert.Equal(MathF.PI, MathF.Abs(ShotModel.Compute(Ctx(behind, ballOn, stick: new Vector2(-1f, 0f)), P, T, new SimRng(1)).IntendedYaw), 4);
        Assert.True(wide > tight);
        Assert.True(T.Shot.GoalMouthAccuracy.Evaluate(tight) > T.Shot.GoalMouthAccuracy.Evaluate(wide));
    }

    [Fact]
    public void AWeakFootStrikeLosesAccuracyPowerAndCurlByItsRating()
    {
        foreach (int wf in new[] { 1, 26, 50, 75, 99 })
        {
            var s = TestData.Sheet(60);
            s.WeakFoot = wf;
            var strong = ShotModel.Compute(Ctx(Shooter(sheet: s), type: ShotType.Finesse, foot: Foot.Right), P, T, new SimRng(1));
            var weak = ShotModel.Compute(Ctx(Shooter(sheet: s), type: ShotType.Finesse, foot: Foot.Left), P, T, new SimRng(1));
            float spread = T.Shot.WeakFootAccuracy.Evaluate(wf), power = T.Shot.WeakFootPower.Evaluate(wf), curl = T.Shot.WeakFootCurl.Evaluate(wf);
            Assert.Equal(1f, strong.Factor("foot"));
            Assert.Equal(1f, strong.SpeedFactor("weak_foot"));
            Assert.Equal(1f, strong.CurlFactor);
            Assert.Equal(spread, weak.Factor("foot"), 4);
            Assert.Equal(strong.SigmaH * spread, weak.SigmaH, 3);
            Assert.Equal(power, weak.SpeedFactor("weak_foot"), 4);
            Assert.Equal(curl, weak.CurlFactor, 4);
            Assert.Equal(strong.Speed * power, weak.Speed, 2);
            // Spin grows with speed, so the weak foot's is the strong foot's times both.
            Assert.Equal(strong.Spin.Length() * power * curl, weak.Spin.Length(), 1);
        }
        Assert.Equal(1.72f, T.Shot.WeakFootAccuracy.Evaluate(1f), 4);
        Assert.Equal(1f, T.Shot.WeakFootAccuracy.Evaluate(99f), 4);
        Assert.Equal(0.85f, T.Shot.WeakFootPower.Evaluate(1f), 4);
        Assert.Equal(1f, T.Shot.WeakFootPower.Evaluate(99f), 4);
        Assert.Equal(0.6f, T.Shot.WeakFootCurl.Evaluate(1f), 4);
        Assert.Equal(1f, T.Shot.WeakFootCurl.Evaluate(99f), 4);
        Assert.Equal(1.4f, T.Touch.WeakFootError.Evaluate(1f), 4);
        Assert.Equal(1f, T.Touch.WeakFootError.Evaluate(99f), 4);
        // The ratings 1, 26, 50, 75 and 99 stand where one to five stars stood: the same numbers within half a rating point.
        int[] ratings = { 1, 26, 50, 75, 99 };
        for (int stars = 1; stars <= 5; stars++)
        {
            float r = ratings[stars - 1];
            Assert.Equal(1f + 0.18f * (5 - stars), T.Shot.WeakFootAccuracy.Evaluate(r), 0.0045f);
            Assert.Equal(0.85f + 0.15f * (stars - 1) / 4f, T.Shot.WeakFootPower.Evaluate(r), 0.001f);
            Assert.Equal(0.6f + 0.4f * (stars - 1) / 4f, T.Shot.WeakFootCurl.Evaluate(r), 0.0025f);
            Assert.Equal(1f + 0.10f * (5 - stars), T.Touch.WeakFootError.Evaluate(r), 0.0025f);
        }
    }

    [Fact]
    public void RunningSpeedScalesTheLaunchSpeed()
    {
        var s = TestData.Sheet(60);
        float top = Movement.TopSpeed(s, T);
        float At(float share)
        {
            var p = Shooter(sheet: s);
            p.Velocity = new Vector3(top * share, 0f, 0f);
            var r = ShotModel.Compute(Ctx(p, foot: Foot.Right), P, T, new SimRng(1));
            Assert.Equal(T.Shot.RunSpeedPower.Evaluate(share), r.SpeedFactor("run_speed"), 4);
            return r.Speed / r.SpeedFactor("run_speed");
        }
        float still = At(0f);
        foreach (float share in new[] { 0.25f, 0.5f, 0.75f, 1f }) Assert.Equal(still, At(share), 2);   // the same seed: only the factor moves it
        Assert.Equal(0.97f, T.Shot.RunSpeedPower.Evaluate(0f), 4);
        Assert.Equal(1f, T.Shot.RunSpeedPower.Evaluate(0.5f), 4);
        Assert.Equal(1.03f, T.Shot.RunSpeedPower.Evaluate(1f), 4);
        // Faster than his top speed counts as flat out.
        var fast = Shooter(sheet: s);
        fast.Velocity = new Vector3(top * 1.5f, 0f, 0f);
        Assert.Equal(1.03f, ShotModel.Compute(Ctx(fast, foot: Foot.Right), P, T, new SimRng(1)).SpeedFactor("run_speed"), 4);
    }

    [Fact]
    public void AnOutsideOfTheFootStrikeIsLessAccurateSlowerAndBendsTheOtherWay()
    {
        var p = Shooter();
        var inside = ShotModel.Compute(Ctx(p, type: ShotType.Finesse, foot: Foot.Right), P, T, new SimRng(1));
        var outside = ShotModel.Compute(Ctx(p, type: ShotType.Finesse, foot: Foot.Right, surface: FootSurface.Outside), P, T, new SimRng(1));
        Assert.Equal(FootSurface.Inside, inside.Surface);
        Assert.Equal(FootSurface.Outside, outside.Surface);
        Assert.Equal(1f, inside.Factor("surface"));
        Assert.Equal(T.Shot.OutsideAccuracy, outside.Factor("surface"), 4);
        Assert.Equal(T.Shot.OutsideSpeed, outside.SpeedFactor("surface"), 4);
        Assert.Equal(inside.SigmaH * T.Shot.OutsideAccuracy, outside.SigmaH, 3);
        // A right foot's inside bends the ball to the left (-Z), its outside to the right: the sideways force is spin x velocity.
        Assert.True(Vector3.Cross(inside.Spin, inside.Launch).Z < 0f);
        Assert.True(Vector3.Cross(outside.Spin, outside.Launch).Z > 0f);
    }

    [Fact]
    public void AWildAimNowMissesTheFrame()
    {
        Assert.Equal(8f, T.Shot.MaxTargetZ);
        var ball = new Vector3(P.GoalLineX - 12f, 0.11f, 0f);
        Assert.Equal(8f, ShotModel.RawTargetZ(new Vector2(1f, 1f), 0f, ball, P, T), 4);
        Assert.Equal(6f, ShotModel.RawTargetZ(new Vector2(1f, 0.5f), 0f, ball, P, T), 4);   // beyond the assist range: left alone
        Assert.Equal(6f, ShotModel.AssistTargetZ(6f, P, T), 4);
    }

    // ---- in the world ----

    static void Step(World w, TickIntent it)
    {
        var buf = new TickIntent[w.PlayerCount];
        buf[0] = it;
        w.Step(buf);
    }

    static TickIntent Dir(float deg, bool shoot = false, float sprint = 0f)
    {
        float a = deg * Deg;
        return new TickIntent { MoveX = MathF.Cos(a), MoveZ = MathF.Sin(a), ShootHeld = shoot, Sprint = sprint };
    }

    [Fact]
    public void ABackToGoalShotIsStruckLaterSlowerAndWilderThanTheSameShotFacingGoal()
    {
        var back = TestData.Scenario("back_to_goal");
        var facing = TestData.Scenario("back_to_goal");
        facing.Attacker = new StartSpot { X = back.Ball.X - 0.6f, Z = 0f, FacingDeg = 0f };
        ShotResult Shoot(Scenario sc, out int pressToStrike, out float rightDeg)
        {
            var w = new World(new Tuning(), sc, TestData.Player("striker_a"), TestData.Player("the_wall"), 5);
            w.Begin();
            Step(w, new TickIntent { ShootHeld = true });
            int press = w.Tick;
            for (int i = 0; i < 12; i++) Step(w, new TickIntent { ShootHeld = true });
            Step(w, Dir(0f));                                   // released with the stick at the goal
            // How far to his right the locked aim is from where he faces at the release, degrees.
            rightDeg = Movement.WrapAngle(w.Attacker.Action!.AimYaw - w.Attacker.Facing) / Deg;
            for (int i = 0; i < 400 && w.Shots.Count == 0; i++) Step(w, default);
            Assert.Single(w.Shots);
            pressToStrike = w.Shots[0].Tick - press;
            return w.Shots[0];
        }
        var b = Shoot(back, out int tb, out float side);
        var f = Shoot(facing, out int tf, out _);
        Assert.True(f.BodyAngleDeg < 30f && b.BodyAngleDeg > 150f, $"body angles {f.BodyAngleDeg} and {b.BodyAngleDeg}");
        Assert.Equal(54, tf);                                                         // the 0.45 s wind-up
        Assert.True(tb - tf >= 24, $"struck {tb - tf} ticks later");                  // at least 0.2 s more
        Assert.True(b.Speed <= 0.7f * f.Speed, $"speed {b.Speed} against {f.Speed}");
        Assert.True(b.SigmaH >= 3f * f.SigmaH, $"sigma {b.SigmaH} against {f.SigmaH}");
        // Wide of foot_by_side_deg the foot on the side the ball leaves strikes; only straight behind is the strong foot's.
        Assert.True(MathF.Abs(side) > T.Shot.FootBySideDeg, $"released {side} degrees round");
        Assert.Equal(MathF.Abs(side) > 179.5f || side > 0f ? Foot.Right : Foot.Left, b.Foot);
        Assert.Equal(Foot.Right, f.Foot);
    }

    [Fact]
    public void TheShotActionCarriesTheAimTheFootAndTheSurfaceFixedFromTheRelease()
    {
        // The ball at his feet out wide, facing the goal (-61 degrees); released with the stick at -20 degrees, 41 degrees to his
        // right, inside the cap: the outside of his right foot (he is on his natural flank).
        var w = TestData.Live("tight_angle");
        var p = w.Attacker;
        float facing = p.Facing;
        Step(w, new TickIntent { ShootHeld = true });
        var shot = p.Action!;
        Assert.True(shot.IsShot);
        Assert.Equal(facing, shot.AimYaw, 1);                  // the stick at rest: where he faces
        Assert.Equal(new StrikeChoice(Foot.Right, FootSurface.Laces), new StrikeChoice(shot.Foot, shot.Surface));
        for (int i = 0; i < 10; i++) Step(w, new TickIntent { ShootHeld = true });
        Step(w, Dir(-20f));
        Assert.True(shot.Released);
        float aim = shot.AimYaw;
        Assert.Equal(-20f, aim / Deg, 1);
        Assert.InRange(ShotModel.OpenAngleDeg(Foot.Right, Foot.Right, p.Facing, aim), 35f, 45f);
        Assert.Equal(Foot.Right, shot.Foot);
        Assert.Equal(FootSurface.Outside, shot.Surface);
        for (int i = 0; i < 400 && w.Shots.Count == 0; i++)
        {
            Step(w, default);
            Assert.Equal(aim, shot.AimYaw);
            Assert.Equal(Foot.Right, shot.Foot);
            Assert.Equal(FootSurface.Outside, shot.Surface);
        }
        var r = Assert.Single(w.Shots);
        Assert.Equal(FootSurface.Outside, r.Surface);
        Assert.Equal(Foot.Right, r.Foot);
        Assert.Equal(aim, r.IntendedYaw, 2);
        Assert.Equal(T.Shot.OutsideAccuracy, r.Factor("surface"), 4);
        Assert.Equal(T.Shot.OutsideSpeed, r.SpeedFactor("surface"), 4);

        // The same shot by a poor curler without the trait (a 30 degree cap): beyond his cap, so his left foot.
        var plain = TestData.Player("striker_a").Clone();
        plain.Traits = PlayerTrait.None;
        plain.Curve = 1;
        var w2 = new World(new Tuning(), TestData.Scenario("tight_angle"), plain, TestData.Player("the_wall"), 1);
        w2.Begin();
        for (int i = 0; i < 11; i++) Step(w2, new TickIntent { ShootHeld = true });
        Step(w2, Dir(-20f));
        Assert.Equal(new StrikeChoice(Foot.Left, FootSurface.Laces), new StrikeChoice(w2.Attacker.Action!.Foot, w2.Attacker.Action!.Surface));
        for (int i = 0; i < 400 && w2.Shots.Count == 0; i++) Step(w2, default);
        var r2 = Assert.Single(w2.Shots);
        Assert.Equal(new StrikeChoice(Foot.Left, FootSurface.Laces), new StrikeChoice(r2.Foot, r2.Surface));
    }

    [Fact]
    public void InADribbleTheStrikeWaitsForTheFootChosenAtTheReleaseAndNeverChangesIt()
    {
        int shots = 0, waited = 0, byGeometry = 0, handedOver = 0, swapped = 0;
        foreach (var (run, aim) in new[] { (0f, 0f), (0f, 60f), (0f, -60f), (0f, 100f), (0f, -100f), (30f, -40f) })
            foreach (float sprint in new[] { 0f, 1f })
                for (ulong seed = 1; seed <= 6; seed++)
                {
                    var sc = new Scenario { Id = "lab", Pitch = new PitchSpec { Length = 300f, Width = 200f }, Ball = new StartSpot { X = 2.5f }, Attacker = new StartSpot { X = 2.0f } };
                    var w = new World(new Tuning(), sc, TestData.Player("striker_a"), TestData.Sheet(50), seed);
                    w.Begin();
                    var p = w.Attacker;
                    for (int i = 0; i < 300 + (int)seed * 7; i++) Step(w, Dir(run, sprint: sprint));
                    Assert.True(p.InPossession);
                    string id = $"run {run} aim {aim} sprint {sprint} seed {seed}";
                    // Pressed with the ball at the feet it is struck at the earliest strike tick, whatever the stride is doing.
                    bool atFoot = TouchModel.InReach(p, w.Ball, w.Tuning) && p.TouchAction is not { Pending: true };
                    for (int i = 0; i < 20; i++) Step(w, Dir(i < 3 ? run : aim, shoot: true, sprint: sprint));
                    Step(w, Dir(aim, sprint: sprint));
                    var shot = p.Action!;
                    Assert.True(shot.IsShot && shot.Released, id);
                    Foot foot = shot.Foot;
                    var surface = shot.Surface;
                    float aimYaw = shot.AimYaw;
                    // The release decides by the geometry: the foot differs from it only through the contact check (a chosen
                    // foot with no contact, or the dead-ahead swap), which hands the shot over with the type's own surface.
                    var decision = w.StrikeGeometry;
                    var geometry = decision.Choice;
                    float open = ShotModel.OpenAngleDeg(Foot.Right, Foot.Right, p.Facing, aimYaw);
                    float cap = ShotModel.OutsideCapDeg(p.Sheet, ShotType.Power, T);
                    if (open > cap + 5f && open < 145f) Assert.True(geometry.Foot == Foot.Left, $"{id}: {open:F0} degrees to the right is the left foot's");
                    if (open < T.Shot.DeadAheadDeg - 5f && open > -145f) Assert.True(geometry == new StrikeChoice(Foot.Right, FootSurface.Laces), $"{id}: {open:F0} degrees to the right is the right foot's laces");
                    if (open > T.Shot.DeadAheadDeg + 5f && open < cap - 5f) Assert.True(geometry == new StrikeChoice(Foot.Right, FootSurface.Outside), $"{id}: {open:F0} degrees to the right is the outside of the right foot");
                    if (foot == geometry.Foot) { Assert.True(surface == geometry.Surface, $"{id}: surface {surface}, by the geometry {geometry.Surface}"); byGeometry++; }
                    else
                    {
                        Assert.True(surface == FootSurface.Laces, $"{id}: handed over with {surface}");
                        handedOver++;
                        // Only a shot the Ahead rule decided is handed over for the stride's sake, and here, at the release:
                        // the loop below holds the foot fixed from now to the strike.
                        if (decision.Rule == StrikeRule.Ahead) { Assert.True(foot == Foot.Left, $"{id}: swapped to the {foot} foot"); swapped++; }
                    }
                    if (MathF.Abs(open) <= T.Shot.DeadAheadDeg - 1f) Assert.True(decision.Rule == StrikeRule.Ahead, $"{id}: {open:F0} degrees decided by {decision.Rule}");
                    if (MathF.Abs(open) > T.Shot.DeadAheadDeg + 1f) Assert.True(decision.Rule != StrikeRule.Ahead, $"{id}: {open:F0} degrees decided by Ahead");
                    for (int i = 0; i < 600 && w.Shots.Count == 0; i++)
                    {
                        Step(w, Dir(run + 150f, sprint: sprint));        // the stick swung away after the release changes nothing
                        if (w.Shots.Count > 0) break;
                        Assert.True(p.Action is { IsShot: true }, $"{id}: the shot was dropped");
                        Assert.True(shot.Foot == foot && p.StrikeFoot == foot, $"{id}: the foot changed after the release");
                        Assert.True(shot.Surface == surface, $"{id}: the surface changed after the release");
                        // A choice made again once a pending touch has been played keeps the geometry of the release.
                        Assert.True(w.StrikeGeometry == decision, $"{id}: the geometry was decided again after the release");
                        Assert.Equal(aimYaw, shot.AimYaw);
                    }
                    var r = Assert.Single(w.Shots);
                    shots++;
                    Assert.True(r.Foot == foot, $"{id}: struck with the {r.Foot} foot, chosen {foot}");
                    Assert.True(w.StrikeGeometry == decision, $"{id}: the decision changed at the strike");
                    Assert.Equal(surface, r.Surface);
                    Assert.Equal(foot, shot.Foot);
                    if (!atFoot && p.IsStepping)
                    {
                        Assert.True(p.SwingFoot == foot, $"{id}: struck on the {p.SwingFoot} foot's swing");
                        waited++;
                    }
                }
        Assert.Equal(72, shots);
        // Dead ahead about half the strikes go to the foot that swings sooner; everywhere else the geometry's foot strikes.
        Assert.True(byGeometry + swapped > 60, $"{byGeometry} strikes on the foot the geometry gave, {handedOver} handed over, {swapped} of them by the dead-ahead swap");
        Assert.True(swapped > 5, $"{swapped} strikes handed over by the dead-ahead swap");
        Assert.True(waited > 30, $"{waited} strikes on the chosen foot's swing");
    }

    [Fact]
    public void TheFootChoiceAndTheLongerWindupDrawNoRandomNumbers()
    {
        // The same round twice: an angled dribble shot (the chooser's searches, the angle wind-up) gives the same strike.
        ShotResult Run()
        {
            var w = TestData.Live("halfway_run", seed: 9);
            var p = w.Attacker;
            for (int i = 0; i < 260; i++) Step(w, Dir(0f, sprint: 1f));
            for (int i = 0; i < 18; i++) Step(w, Dir(110f, shoot: true, sprint: 1f));
            for (int i = 0; i < 600 && w.Shots.Count == 0; i++) Step(w, Dir(110f, sprint: 1f));
            return Assert.Single(w.Shots);
        }
        var a = Run();
        var b = Run();
        Assert.Equal(a.Tick, b.Tick);
        Assert.Equal(a.Launch, b.Launch);
        Assert.Equal(a.Spin, b.Spin);
        Assert.Equal(a.Foot, b.Foot);
        // And the draws of a strike are the same four as before, whatever the foot: the next draw after two strikes agrees.
        var r1 = new SimRng(4);
        var r2 = new SimRng(4);
        ShotModel.Compute(Ctx(Shooter(), foot: Foot.Right), P, T, r1);
        ShotModel.Compute(Ctx(Shooter(facing: 2f), stick: new Vector2(0.3f, 1f), foot: Foot.Left, surface: FootSurface.Outside), P, T, r2);
        Assert.Equal(r1.NextFloat(), r2.NextFloat());
    }
}
