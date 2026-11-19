using System.Numerics;
using FootballSim.Anim;

namespace FootballSim.Anim.Tests;

/// <summary>Inertialization: an offset decays below 1 % within five half-lives without overshooting, the first frame after a switch
/// continues the pose that was shown, and a reset (teleport) drops every offset.</summary>
public class InertializerTests
{
    const float Dt = 1f / 120f;

    static void Switch(Inertializer inz, Quaternion shown, Vector3 shownPos, Quaternion dst, Vector3 dstPos, Vector3 shownAngVel = default, Vector3 shownVel = default)
    {
        int n = inz.BoneCount;
        var sr = Enumerable.Repeat(shown, n).ToArray(); var sp = Enumerable.Repeat(shownPos, n).ToArray();
        var dr = Enumerable.Repeat(dst, n).ToArray(); var dp = Enumerable.Repeat(dstPos, n).ToArray();
        var sw = Enumerable.Repeat(shownAngVel, n).ToArray(); var sv = Enumerable.Repeat(shownVel, n).ToArray();
        var zero = new Vector3[n];
        inz.Transition(sr, sw, sp, sv, dr, zero, dp, zero);
    }

    [Fact]
    public void OffsetDecaysBelowOnePercentWithinFiveHalfLivesWithoutOvershoot()
    {
        var inz = new Inertializer(2);
        var axis = Vector3.Normalize(new Vector3(0.3f, 1f, -0.2f));
        var shown = Quaternion.CreateFromAxisAngle(axis, 1.2f);
        var dst = Quaternion.Identity;
        Switch(inz, shown, new Vector3(0.4f, 0, 0), dst, Vector3.Zero);
        var src = new[] { dst, dst }; var srcP = new Vector3[2];
        var outR = new Quaternion[2]; var outP = new Vector3[2];
        float prevAngle = 1.2f, prevX = 0.4f;
        int ticks = (int)MathF.Round(5 * Inertializer.HalfLife / Dt);
        for (int i = 0; i < ticks; i++)
        {
            inz.Update(Dt, src, srcP, outR, outP);
            var off = Spring.Log(outR[0]);
            float along = Vector3.Dot(off, axis);
            Assert.True(along >= -1e-6f, $"rotation overshot at tick {i}: {along}");
            Assert.True(along <= prevAngle + 1e-6f, "rotation offset grew");
            Assert.True(outP[0].X >= -1e-7f && outP[0].X <= prevX + 1e-7f, $"position overshot or grew at tick {i}");
            prevAngle = along; prevX = outP[0].X;
        }
        Assert.True(prevAngle < 0.01f * 1.2f, $"rotation offset {prevAngle} after five half-lives");
        Assert.True(prevX < 0.01f * 0.4f, $"position offset {prevX} after five half-lives");
    }

    [Fact]
    public void TheFirstFrameAfterASwitchContinuesTheShownPose()
    {
        var inz = new Inertializer(1);
        var shown = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.9f);
        Switch(inz, shown, new Vector3(0, 1, 0), Quaternion.Identity, new Vector3(0, 0.8f, 0));
        var outR = new Quaternion[1]; var outP = new Vector3[1];
        inz.Update(Dt, new[] { Quaternion.Identity }, new[] { new Vector3(0, 0.8f, 0) }, outR, outP);
        Assert.True(TestKit.Angle(outR[0], shown) < 0.02f);
        Assert.InRange(outP[0].Y, 0.99f, 1f);
        Assert.True(inz.Active);
    }

    [Fact]
    public void ShownVelocityCarriesThroughTheSwitch()
    {
        // the shown pose was turning about +Y at 3 rad/s; the new source stands still: the output keeps turning for a moment
        var inz = new Inertializer(1);
        Switch(inz, Quaternion.Identity, Vector3.Zero, Quaternion.Identity, Vector3.Zero, new Vector3(0, 3f, 0));
        var outR = new Quaternion[1]; var outP = new Vector3[1];
        inz.Update(Dt, new[] { Quaternion.Identity }, new Vector3[1], outR, outP);
        Assert.InRange(Spring.Log(outR[0]).Y, 0.5f * 3f * Dt, 1.5f * 3f * Dt);
    }

    [Fact]
    public void ResetDropsEveryOffset()
    {
        var inz = new Inertializer(3);
        Switch(inz, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f), Vector3.One, Quaternion.Identity, Vector3.Zero);
        Assert.True(inz.Active);
        inz.Reset();
        Assert.False(inz.Active);
        var src = Enumerable.Repeat(Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f), 3).ToArray();
        var srcP = Enumerable.Repeat(new Vector3(1, 2, 3), 3).ToArray();
        var outR = new Quaternion[3]; var outP = new Vector3[3];
        inz.Update(Dt, src, srcP, outR, outP);
        for (int k = 0; k < 3; k++) { Assert.True(TestKit.Angle(outR[k], src[k]) < 1e-5f); Assert.Equal(srcP[k], outP[k]); }
    }
}
