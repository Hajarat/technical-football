using System.Numerics;
using FootballSim.Anim;
using FootballSim.Core;

namespace FootballSim.Anim.Tests;

/// <summary>The chooser's rows for this milestone, and the side check by facing (not world Z), including at a facing of π.</summary>
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
    [InlineData(KeeperAction.Diving, KeeperDive.Side)]
    [InlineData(KeeperAction.Diving, KeeperDive.Catch)]
    [InlineData(KeeperAction.Diving, KeeperDive.ReflexHand)]
    [InlineData(KeeperAction.Smother, KeeperDive.None)]
    [InlineData(KeeperAction.Slide, KeeperDive.None)]
    [InlineData(KeeperAction.Holding, KeeperDive.None)]
    [InlineData(KeeperAction.Grounded, KeeperDive.None)]
    [InlineData(KeeperAction.GettingUp, KeeperDive.None)]
    public void OtherStatesStayProcedural(KeeperAction action, KeeperDive dive) => Assert.Null(KeeperChooser.SetFor(action, dive));

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
