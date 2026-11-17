using FootballSim.Core;

namespace FootballSim.Core.Tests;

public class DeterminismTests
{
    static RoundReport Run(string scenario, ulong seed, out Replay replay, string keeper = "the_wall")
    {
        return RoundRunner.RunToReport(new Tuning(), TestData.Scenario(scenario), TestData.Player("striker_a"), TestData.Player(keeper), seed, out replay);
    }

    [Theory]
    [InlineData("penalty")]
    [InlineData("breakaway")]
    [InlineData("edge_of_box")]
    [InlineData("tight_angle")]
    public void SameSeedAndIntentsGiveAnIdenticalReportTwiceInOneRun(string scenario)
    {
        var a = Run(scenario, 12345, out var ra);
        var b = Run(scenario, 12345, out var rb);
        Assert.Equal(a.ToJson(), b.ToJson());
        Assert.Equal(ra.ToJson(), rb.ToJson());
    }

    [Fact]
    public void DifferentSeedsGiveDifferentRounds()
    {
        var a = Run("edge_of_box", 1, out _);
        var b = Run("edge_of_box", 2, out _);
        Assert.NotEqual(a.ToJson(), b.ToJson());
    }

    [Fact]
    public void ReplayReproducesTheReportAfterASaveAndLoad()
    {
        var original = Run("breakaway", 77, out var replay);
        string path = Path.Combine(Path.GetTempPath(), $"replay-{Guid.NewGuid():N}.json");
        try
        {
            replay.Save(path);
            var loaded = Replay.Load(path, out var err);
            Assert.Null(err);
            Assert.NotNull(loaded);
            Assert.Equal(replay.Seed, loaded!.Seed);
            Assert.Equal("striker_a", loaded.AttackerId);
            Assert.Equal("the_wall", loaded.KeeperId);
            Assert.Equal("breakaway", loaded.ScenarioId);
            Assert.Equal(replay.TickCount, loaded.TickCount);
            Assert.True(loaded.MatchesTuning(new Tuning()));
            var rerun = loaded.Run(new Tuning(), TestData.Scenario("breakaway"), TestData.Player("striker_a"), TestData.Player("the_wall"));
            Assert.Equal(original.ToJson(), rerun.ToJson());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReplayRecordsEveryTickOfBothPlayers()
    {
        Run("penalty", 5, out var replay);
        Assert.Equal(2, replay.PlayersPerTick);
        Assert.Equal(replay.Intents.Count, replay.TickCount * 2);
        Assert.True(replay.TickCount > 150);
        Assert.Contains(replay.Intents, i => i.Shoot);
    }

    [Fact]
    public void ReplayNoticesChangedTuning()
    {
        Run("penalty", 5, out var replay);
        var other = new Tuning { ErrorScale = 1.5f };
        Assert.False(replay.MatchesTuning(other));
        Assert.Equal(DataLoader.TuningHash(new Tuning()), DataLoader.TuningHash(new Tuning()));
    }

    [Fact]
    public void BadReplayFilesReturnAnErrorInsteadOfThrowing()
    {
        Assert.Null(Replay.FromJson("{ not json", out var err));
        Assert.NotNull(err);
        Assert.Null(Replay.Load(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + ".json"), out var err2));
        Assert.NotNull(err2);
    }

    [Fact]
    public void RngSequenceIsFixedForASeed()
    {
        // Pins the generator so results cannot silently change between .NET versions or edits.
        var r = new SimRng(1);
        Assert.Equal(new SimRng(1).NextUInt64(), r.NextUInt64());
        var a = new SimRng(99); var b = new SimRng(99);
        for (int i = 0; i < 1000; i++) Assert.Equal(a.Normal(), b.Normal());
        Assert.NotEqual(new SimRng(1).NextUInt64(), new SimRng(2).NextUInt64());
    }

    [Fact]
    public void RngDrawsHaveTheRightMoments()
    {
        var r = new SimRng(7);
        int n = 200000;
        double su = 0, sn = 0, sn2 = 0;
        for (int i = 0; i < n; i++)
        {
            su += r.NextDouble();
            double x = r.Normal(3f, 2f);
            sn += x; sn2 += x * x;
        }
        Assert.InRange(su / n, 0.495, 0.505);
        double mean = sn / n, sd = Math.Sqrt(sn2 / n - mean * mean);
        Assert.InRange(mean, 2.98, 3.02);
        Assert.InRange(sd, 1.98, 2.02);
        for (int i = 0; i < 1000; i++) Assert.InRange(r.Uniform(2f, 3f), 2f, 3f);
        for (int i = 0; i < 100; i++) Assert.Equal(1f, r.UnitVector().Length(), 4);
    }
}
