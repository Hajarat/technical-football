using System.Numerics;
using FootballSim.Anim;

namespace FootballSim.Anim.Tests;

/// <summary>The corrections over a clip pose: the chest turned to the facing without turning the legs, the stance layer's width and
/// hands, the matcher's clip bias, and both layers fading in rather than popping.</summary>
public class PoseLayersTests
{
    static readonly Skeleton Sk = TestKit.Keeper.Skeleton;

    static (Quaternion[] r, Vector3[] p) Pose(string clip, int frame, float extraYaw = 0f)
    {
        var c = TestKit.Keeper.Clips.First(e => e.Clip.Name == clip).Clip;
        var r = c.Rotations(frame).ToArray();
        r[0] = Quaternion.Normalize(Spring.Yaw(extraYaw) * r[0]);
        var p = Sk.Offsets.ToArray(); p[0] = c.Hips(frame);
        return (r, p);
    }

    static (Quaternion[] gr, Vector3[] gp) Fk(Quaternion[] r, Vector3[] p)
    {
        var gr = new Quaternion[Sk.Count]; var gp = new Vector3[Sk.Count];
        Sk.Forward(r, p[0], gr, gp, p);
        return (gr, gp);
    }

    [Fact]
    public void TwistingTheTrunkTurnsTheChestAndLeavesTheLegs()
    {
        var (r, p) = Pose("stance_ready_77_03", 40, 0.9f);   // the whole body turned 0.9 rad off a facing of 0
        var (gr, gp) = Fk(r, p);
        float before = PoseLayers.ChestYaw(Sk, gr);
        float turn = PoseLayers.ChestCorrection(before, 0f, KeeperMotion.ChestTolerance);
        PoseLayers.TwistTrunk(Sk, r, gr, turn);
        var (gr2, gp2) = Fk(r, p);
        Assert.InRange(MathF.Abs(Spring.WrapPi(PoseLayers.ChestYaw(Sk, gr2))), 0f, KeeperMotion.ChestTolerance + 0.02f);
        for (int i = 0; i < 2; i++)
        {
            Assert.True(TestKit.Angle(gr[Sk.UpLeg[i]], gr2[Sk.UpLeg[i]]) < 1e-3f, "a thigh turned with the trunk");
            Assert.True((gp2[Sk.Foot[i]] - gp[Sk.Foot[i]]).Length() < 0.06f, "a foot moved more than the hip joint's turn allows");
        }
        // a chest already within the tolerance is left alone
        Assert.Equal(0f, PoseLayers.ChestCorrection(0.05f, 0f, KeeperMotion.ChestTolerance));
    }

    [Fact]
    public void ReachMovesALimbEndToItsTargetAndKeepsTheHand()
    {
        var (r, p) = Pose("stance_ready_77_03", 40);
        var (gr, gp) = Fk(r, p);
        int arm = Sk.Find("LeftArm"), fore = Sk.Find("LeftForeArm"), hand = Sk.Find("LeftHand");
        var target = gp[hand] + new Vector3(0.15f, -0.05f, 0.05f);
        var handRot = gr[hand];
        PoseLayers.Reach(Sk, r, gr, gp, arm, fore, hand, target, -Vector3.UnitZ);
        var (gr2, gp2) = Fk(r, p);
        Assert.True((gp2[hand] - target).Length() < 0.01f, $"hand {(gp2[hand] - target).Length()} m off");
        Assert.True(TestKit.Angle(handRot, gr2[hand]) < 1e-3f);
    }

    [Fact]
    public void ABiasedClipIsPickedOnlyWhenItFitsClearlyBetter()
    {
        var sk = TestKit.Legs();
        var a = TestKit.Walk(sk, "a", 100, 1f, 1.5f);
        var b = TestKit.Walk(sk, "b", 100, 1f, 1.5f);   // the same motion: without bias, ties go to a
        var set = new PoseSet("s", new[] { (b, "none", 1f), (a, "none", 2f) }, sk);   // a second and biased
        Assert.Equal(40, Matcher.Best(set, set.Normalized(140), out _));   // a's own frame loses to b's identical one
        Assert.Equal(2f, set.Bias(140));
        var q = set.Normalized(45).ToArray();
        Assert.Equal(2f * Matcher.Cost(set, 40, q), Matcher.Cost(set, 140, q), 4);
        var lib = TestKit.Keeper;
        Assert.Equal(2f, lib.Clips.First(c => c.Clip.Name == "stance_shuffle_83_01").Bias);
        Assert.Equal(1f, lib.Clips.First(c => c.Clip.Name == "stance_shuffle_83_13").Bias);
    }

    [Fact]
    public void ChestAndStanceLayersHoldTheirTargetsAndFadeIn()
    {
        var db = new PoseDatabase(TestKit.Keeper, KeeperChooser.Sets, KeeperChooser.WeightsFor);
        var m = new KeeperMotion(db);
        int n = Sk.Count;
        var procR = Sk.RestLocal.ToArray(); var procP = Sk.Offsets.ToArray(); procP[0] = new Vector3(0, 0.95f, 0);
        var outR = new Quaternion[n]; var outP = new Vector3[n];
        var stance = new StanceTargets { Weight = 1f, HipsHeight = 0.80f, Lean = 0.3f, LeftAnkleX = 0.45f, RightAnkleX = -0.45f,
            LeftHand = new Vector3(0.55f, -0.35f, 0.15f), RightHand = new Vector3(-0.55f, -0.35f, 0.15f) };
        var input = new MotionInput { Position = Vector2.Zero, Yaw = 0f, Set = KeeperChooser.Stance, Scale = 1f, Dt = 1f / 120f, SquareChest = true };
        for (int i = 0; i < 60; i++) m.Step(input, procR, procP, outR, outP);
        float hipsBefore = outP[0].Y;
        // spread: the layer fades in (no jump on its first tick), then holds the width and the hands
        input.Stance = stance;
        m.Step(input, procR, procP, outR, outP);
        Assert.True(MathF.Abs(outP[0].Y - hipsBefore) < 0.02f, "the layer popped in");
        for (int i = 0; i < 120; i++) m.Step(input, procR, procP, outR, outP);
        var (gr, gp) = Fk(outR, outP);
        Assert.InRange(MathF.Abs(Spring.WrapPi(PoseLayers.ChestYaw(Sk, gr))), 0f, KeeperMotion.ChestTolerance + 0.05f);
        float width = gp[Sk.Foot[0]].X - gp[Sk.Foot[1]].X;
        Assert.True(width > 0.6f, $"stance only {width} m wide");
        var shoulders = (gp[Sk.Find("LeftArm")] + gp[Sk.Find("RightArm")]) * 0.5f;
        Assert.True(gp[Sk.Find("LeftHand")].X - shoulders.X > 0.35f, "the left hand is not out to the side");
        Assert.True(shoulders.X - gp[Sk.Find("RightHand")].X > 0.35f, "the right hand is not out to the side");
    }
}
