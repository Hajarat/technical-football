using FootballSim.Anim;

namespace FootballSim.Anim.Tests;

/// <summary>The body-language amounts over the stride cycles: they follow the motion toward the right side, stay out of straight running,
/// give way to a skill or shot, and ease rather than jump.</summary>
public class BodyLanguageTests
{
    const float Dt = 1f / 120f;

    static void Run(BodyLanguage b, float speed, float lateral, float seconds, float free = 1f, float forward = 0f)
    {
        for (int i = 0; i < (int)(seconds / Dt); i++) b.Step(new BodyLanguage.Input { Speed = speed, Lateral = lateral, Forward = forward, Free = free, Dt = Dt });
    }

    [Fact]
    public void ATurnBanksToTheInsideWithTheShouldersAndHeadAhead()
    {
        foreach (float sign in new[] { 1f, -1f })
        {
            var b = new BodyLanguage();
            Run(b, 5f, 0f, 0.2f);
            Run(b, 5f, 8f * sign, 0.4f);
            Assert.True(b.Bank * sign > 12f * MathF.PI / 180f, $"bank {b.Bank}");
            Assert.True(b.ShoulderLead * sign > 0.1f && b.HeadLead * sign > 0.1f && b.LookAhead * sign > 0.1f);
            Assert.True(b.PelvisDrop > 0.01f);
            // the outside arm (the left in a turn to his right) goes wider, the inside one not
            int outside = sign > 0 ? 0 : 1;
            Assert.True(b.ArmOut(outside) > 0.1f && b.ArmOut(1 - outside) == 0f);
        }
    }

    [Fact]
    public void TheBankIsCappedHigherAtASprint()
    {
        var b = new BodyLanguage();
        float jog = b.BankTarget(4.8f, 30f), sprint = b.BankTarget(7.2f, 30f);
        Assert.Equal(b.BankMaxJog, jog, 4);
        Assert.Equal(b.BankMaxSprint, sprint, 4);
        Assert.True(MathF.Abs(b.BankTarget(4.8f, -30f) + jog) < 1e-5f);
    }

    [Fact]
    public void RunningStraightOrWobblingDrawsNothing()
    {
        var b = new BodyLanguage();
        Run(b, 6f, 0.5f, 1f);
        Assert.Equal(0f, b.Bank, 5);
        Assert.Equal(0f, b.ShoulderLead, 5);
        Assert.Equal(0f, b.HeadLead, 5);
    }

    [Fact]
    public void TheTurnIsGoneHalfASecondAfterItEnds()
    {
        var b = new BodyLanguage();
        Run(b, 5f, 10f, 0.5f);
        Run(b, 5f, 0f, 0.5f);
        Assert.InRange(MathF.Abs(b.Bank) * 180f / MathF.PI, 0f, 0.5f);
        Assert.InRange(MathF.Abs(b.ShoulderLead) * 180f / MathF.PI, 0f, 0.5f);
    }

    [Fact]
    public void ASkillOrShotTakesTheLayersAwaySmoothly()
    {
        var b = new BodyLanguage();
        Run(b, 5f, 10f, 0.5f);
        float before = b.Bank, prev = before, most = 0f;
        for (int i = 0; i < 60; i++)
        {
            b.Step(new BodyLanguage.Input { Speed = 5f, Lateral = 10f, Free = 0f, Dt = Dt });
            most = MathF.Max(most, MathF.Abs(b.Bank - prev)); prev = b.Bank;
        }
        Assert.True(MathF.Abs(b.Bank) < 0.02f * before, "the bank did not fade");
        Assert.True(most < 0.15f * before, $"the bank jumped {most} of {before} in a tick");
    }

    [Fact]
    public void TheFreeShareEasesBetweenOwnedAndFree()
    {
        var b = new BodyLanguage();
        Run(b, 5f, 0f, 0.2f);
        Assert.Equal(1f, b.FreeShare, 4);
        b.Step(new BodyLanguage.Input { Speed = 5f, Free = 0f, Dt = Dt });
        Assert.InRange(b.FreeShare, 0.8f, 1f);
        Run(b, 5f, 0f, 0.4f, free: 0f);
        Assert.InRange(b.FreeShare, 0f, 0.01f);
    }

    [Fact]
    public void TheFirstTickStartsAtItsTargetAndResetForgets()
    {
        var b = new BodyLanguage();
        b.Step(new BodyLanguage.Input { Speed = 5f, Lateral = 8f, Free = 1f, Dt = Dt });
        Assert.Equal(b.BankTarget(5f, 8f), b.Bank, 5);
        b.Reset();
        b.Step(new BodyLanguage.Input { Speed = 5f, Lateral = 0f, Free = 1f, Dt = Dt });
        Assert.Equal(0f, b.Bank, 5);
    }
}
