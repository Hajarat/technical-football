using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>
/// The shot's base wind-up runs by the shooter's finishing rating (2026-11-24): 0.55 s at 1, 0.45 s at 50, 0.30 s at 99, linear
/// between; the body angle's extra and the lead after the release add on as before.
/// </summary>
public class ShotWindupTests
{
    static PlayerSheet Finisher(int finishing)
    {
        var s = TestData.Player("striker_a");
        s.Finishing = finishing;
        return s;
    }

    [Fact]
    public void TheWindupRunsByFinishing()
    {
        var t = new Tuning();
        Assert.Equal(0.55f, ShotModel.WindupSeconds(Finisher(1), t), 4);
        Assert.Equal(0.45f, ShotModel.WindupSeconds(Finisher(50), t), 4);
        Assert.Equal(0.30f, ShotModel.WindupSeconds(Finisher(99), t), 4);
        Assert.Equal(0.50f, ShotModel.WindupSeconds(Finisher(25), t), 2);         // linear between the points
        Assert.Equal(0.45f - 0.15f * 28f / 49f, ShotModel.WindupSeconds(Finisher(78), t), 4);
        for (int r = 2; r <= 99; r++)
            Assert.True(ShotModel.WindupSeconds(Finisher(r), t) <= ShotModel.WindupSeconds(Finisher(r - 1), t), $"finishing {r} winds up longer than {r - 1}");
    }

    [Fact]
    public void TheShippedTuningHoldsTheSameCurve()
    {
        var t = DataLoader.LoadTuning(Path.Combine(TestData.DataDir, "tuning", "default.json")).Value!;
        foreach (int r in new[] { 1, 25, 50, 78, 99 })
            Assert.Equal(ShotModel.WindupSeconds(Finisher(r), new Tuning()), ShotModel.WindupSeconds(Finisher(r), t), 4);
    }

    /// <summary>
    /// From the penalty spot, facing the shot, shoot pressed for one tick: the earliest strike tick, and the strike itself, fall
    /// the finishing's wind-up after the press (the release lead, 0.12 s after a release one tick in, ends sooner).
    /// </summary>
    [Theory]
    [InlineData(1, 0.55f)]
    [InlineData(50, 0.45f)]
    [InlineData(99, 0.30f)]
    public void TheEarliestStrikeTickFollowsTheFinishingWindup(int finishing, float windup)
    {
        var w = new World(TestData.Tuning(), TestData.Scenario("penalty"), Finisher(finishing), TestData.Player("the_wall"), 1);
        w.Begin();
        var buf = new TickIntent[w.PlayerCount];
        buf[0] = new TickIntent { ShootHeld = true };
        w.Step(buf);
        int press = w.Tick;
        var shot = Assert.IsType<BallAction>(w.Attacker.Action);
        Assert.True(shot.IsShot);
        int expected = press + (int)MathF.Round(windup * w.Tuning.TickRate);
        Assert.Equal(expected, shot.ContactTick);                 // the earliest strike tick while still held
        buf[0] = default;
        w.Step(buf);
        Assert.True(shot.Released);
        Assert.Equal(expected, shot.ContactTick);
        while (w.Shots.Count == 0 && w.Tick < press + 200) w.Step(buf);
        Assert.Single(w.Shots);
        Assert.Equal(expected, w.Tick);
    }
}
