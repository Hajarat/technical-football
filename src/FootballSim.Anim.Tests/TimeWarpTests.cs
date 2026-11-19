using FootballSim.Anim;

namespace FootballSim.Anim.Tests;

/// <summary>The time warp: rate = clip time to contact ÷ simulated time to it, clamped 0.75–1.35×; a late save clamps and hands over to the IK.</summary>
public class TimeWarpTests
{
    const float Dt = 1f / 120f;

    [Fact]
    public void TheRateClampsAtThreeQuartersAndOnePointThreeFive()
    {
        Assert.Equal(0.75f, TimeWarp.Rate(0.3f, 1.0f, Dt, out bool l1));
        Assert.False(l1);
        Assert.Equal(1.35f, TimeWarp.Rate(1.0f, 0.3f, Dt, out bool l2));
        Assert.False(l2);
        Assert.Equal(1.2f, TimeWarp.Rate(0.6f, 0.5f, Dt, out _), 5);
    }

    [Theory]
    [InlineData(0.50f, 0.45f)]
    [InlineData(0.40f, 0.50f)]
    [InlineData(0.62f, 0.50f)]
    [InlineData(0.30f, 0.38f)]
    public void InsideTheClampTheContactFrameLandsOnTheTargetTick(float clipToContact, float simToContact)
    {
        // a 60 Hz clip whose contact frame is clipToContact ahead, played at the warped rate on the 120 Hz tick
        float rate = TimeWarp.Rate(clipToContact, simToContact, Dt, out bool late);
        Assert.False(late);
        float clipT = 0f;
        int tick = 0;
        while (clipT < clipToContact - 1e-6f) { clipT += Dt * rate; tick++; }
        int targetTick = (int)MathF.Round(simToContact / Dt);
        Assert.InRange(tick, targetTick - 1, targetTick + 1);
    }

    [Theory]
    [InlineData(-0.05f)]
    [InlineData(0f)]
    [InlineData(0.004f)]
    [InlineData(float.NaN)]
    public void ALateContactClampsToTheFastestRateAndTheIkTakesOver(float simToContact)
    {
        Assert.Equal(TimeWarp.SaveMax, TimeWarp.Rate(0.4f, simToContact, Dt, out bool late));
        Assert.True(late);
    }

    [Fact]
    public void AClipPastItsContactPlaysAtItsOwnSpeed()
    {
        Assert.Equal(1f, TimeWarp.Rate(-0.1f, 0.3f, Dt, out bool late));
        Assert.False(late);
        Assert.Equal(1f, TimeWarp.Rate(0f, -0.3f, Dt, out _));
    }
}
