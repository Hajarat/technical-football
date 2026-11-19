using System.Numerics;
using FootballSim.Anim;
using FootballSim.Core;

namespace FootballSim.Anim.Tests;

/// <summary>The chooser's rows (spec appendix F), and the side check by facing (not world Z), including at a facing of π.</summary>
public class KeeperChooserTests
{
    [Theory]
    [InlineData(KeeperAction.Set, KeeperChooser.StanceIdle)]
    [InlineData(KeeperAction.Planted, KeeperChooser.StanceIdle)]
    [InlineData(KeeperAction.Reacting, KeeperChooser.StanceIdle)]
    [InlineData(KeeperAction.Shuffle, KeeperChooser.Stance)]
    [InlineData(KeeperAction.Spread, KeeperChooser.Stance)]
    [InlineData(KeeperAction.Jockey, KeeperChooser.Stance)]
    [InlineData(KeeperAction.Backpedal, KeeperChooser.Backpedal)]
    [InlineData(KeeperAction.Rush, KeeperChooser.Run)]
    [InlineData(KeeperAction.Charge, KeeperChooser.Run)]
    public void StanceAndMovementStatesHaveASet(KeeperAction action, string set) => Assert.Equal(set, KeeperChooser.SetFor(action, KeeperDive.None));

    [Theory]
    [InlineData(KeeperAction.Diving, KeeperDive.Low, KeeperChooser.DiveLow)]
    [InlineData(KeeperAction.Diving, KeeperDive.Side, KeeperChooser.DiveSide)]
    [InlineData(KeeperAction.Diving, KeeperDive.High, KeeperChooser.DiveHigh)]
    [InlineData(KeeperAction.Diving, KeeperDive.Feet, KeeperChooser.Feet)]
    [InlineData(KeeperAction.Diving, KeeperDive.Catch, KeeperChooser.Catch)]
    [InlineData(KeeperAction.Diving, KeeperDive.Back, KeeperChooser.BackLeap)]
    [InlineData(KeeperAction.Diving, KeeperDive.ReflexHand, KeeperChooser.ReflexHand)]
    [InlineData(KeeperAction.Diving, KeeperDive.ReflexFoot, KeeperChooser.ReflexFoot)]
    [InlineData(KeeperAction.Diving, KeeperDive.BodyBlock, KeeperChooser.BodyBlock)]
    [InlineData(KeeperAction.Smother, KeeperDive.None, KeeperChooser.Smother)]
    [InlineData(KeeperAction.Slide, KeeperDive.None, KeeperChooser.Slide)]
    [InlineData(KeeperAction.Holding, KeeperDive.None, KeeperChooser.Hold)]
    [InlineData(KeeperAction.GettingUp, KeeperDive.None, KeeperChooser.GetUp)]
    public void SaveAndGroundStatesHaveASet(KeeperAction action, KeeperDive dive, string set) => Assert.Equal(set, KeeperChooser.SetFor(action, dive));

    [Theory]
    [InlineData(Lying.Front, KeeperChooser.LieFront)]
    [InlineData(Lying.Side, KeeperChooser.LieSide)]
    [InlineData(Lying.Back, KeeperChooser.LieBack)]
    public void LyingIsChosenByHowTheLastClipLeftHim(Lying lying, string set)
    {
        Assert.Equal(set, KeeperChooser.SetFor(KeeperAction.Grounded, KeeperDive.None, lying));
        // holding the ball where he lies keeps him lying; standing he has the hold set
        Assert.Equal(set, KeeperChooser.SetFor(KeeperAction.Holding, KeeperDive.None, lying, onGround: true));
    }

    [Fact]
    public void TheHybridSavesAreTheThreeDivesAndTheSmother()
    {
        foreach (var s in new[] { KeeperChooser.DiveLow, KeeperChooser.DiveSide, KeeperChooser.DiveHigh, KeeperChooser.Smother })
        {
            Assert.True(KeeperChooser.IsChain(s));
            Assert.Contains(KeeperChooser.Takeoff(s), KeeperChooser.Sets);
            Assert.Contains(KeeperChooser.Flight(s), KeeperChooser.Sets);
        }
        Assert.False(KeeperChooser.IsChain(KeeperChooser.Catch));
        Assert.Equal("contact", KeeperChooser.OptionsFor(KeeperChooser.ReflexHand)!.TargetEvent);
        Assert.Equal(ContactLimb.ReachingFoot, KeeperChooser.OptionsFor(KeeperChooser.Slide)!.Limb);
        Assert.Equal("up", KeeperChooser.OptionsFor(KeeperChooser.GetUp)!.TargetEvent);
    }

    [Fact]
    public void SideIsBySidesOfHisFacingNotWorldZ()
    {
        var pos = new Vector3(14f, 0, 0.5f);
        var minusZ = pos + new Vector3(0.3f, 1f, -2f);
        Assert.Equal(-1, KeeperChooser.SideOf(minusZ, pos, 0f));          // facing +X: -Z is his left
        Assert.Equal(1, KeeperChooser.SideOf(minusZ, pos, MathF.PI));     // facing π (-X): -Z is his right
        Assert.Equal(1, KeeperChooser.SideOf(minusZ, pos, -MathF.PI));
        Assert.Equal(0, KeeperChooser.SideOf(pos + new Vector3(-3f, 0, 0), pos, MathF.PI));
        Assert.Equal(1, KeeperChooser.SideOf(pos + new Vector3(-1f, 0, 0), pos, MathF.PI / 2));   // facing +Z: -X is his right
    }

    [Fact]
    public void ModelSpaceTurnsTheWorldAQuarterTurn()
    {
        // facing π in the world: the model's facing (+Z turned by the yaw) must point along world -X
        float yaw = ModelSpace.YawFromFacing(MathF.PI);
        var fwdModel = Vector3.Transform(Vector3.UnitZ, Spring.Yaw(yaw));
        var fwdWorld = ModelSpace.ToWorld(fwdModel);
        Assert.True((fwdWorld - new Vector3(-1, 0, 0)).Length() < 1e-5f);
        var v = new Vector3(1.5f, 0.2f, -0.7f);
        Assert.True((ModelSpace.ToWorld(ModelSpace.FromWorld(v)) - v).Length() < 1e-6f);
        // the model's left (+X) at facing 0 is the world's -Z (his right is +Z)
        Assert.True((ModelSpace.ToWorld(Vector3.UnitX) - new Vector3(0, 0, -1)).Length() < 1e-6f);
    }
}
