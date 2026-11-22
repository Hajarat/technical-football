using System.Text.Json.Nodes;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>Round capture: copies of a recording taken mid-round, partial replays, the capture index and the shot audit.</summary>
public class RoundCaptureTests
{
    static World Recorded(string scenario, ulong seed, out ReplayRecorder rec)
    {
        var w = new World(TestData.Tuning(), TestData.Scenario(scenario), TestData.Player("striker_a"), TestData.Player("the_wall"), seed);
        rec = new ReplayRecorder(w, "striker_a", "the_wall", scenario);
        w.Recorder = rec;
        w.Begin();
        return w;
    }

    [Theory]
    [InlineData("breakaway", 77UL, 150)]
    [InlineData("penalty", 5UL, 200)]
    public void ACopyTakenAtTickNPlaysToTheLiveWorldAtTickN(string scenario, ulong seed, int n)
    {
        var live = Recorded(scenario, seed, out var rec);
        var script = new ScriptedAttacker();
        var buf = new TickIntent[live.PlayerCount];
        for (int i = 0; i < n && live.IsSimulating; i++) { buf[0] = script.Next(live); live.Step(buf); }
        Assert.True(live.IsSimulating);              // cut mid-round
        var copy = rec.Snapshot();
        copy.Partial = true;

        // the live round carries on after the copy; the copy must not see those ticks
        for (int i = 0; i < 50 && live.IsSimulating; i++) { buf[0] = script.Next(live); live.Step(buf); }
        Assert.Equal(n, copy.TickCount);

        var again = Recorded(scenario, seed, out var rec2);
        var script2 = new ScriptedAttacker();
        for (int i = 0; i < n && again.IsSimulating; i++) { buf[0] = script2.Next(again); again.Step(buf); }

        var played = Replay.FromJson(copy.ToJson(), out var err)!.Play(TestData.Tuning(), TestData.Scenario(scenario),
            TestData.Player("striker_a"), TestData.Player("the_wall"));
        Assert.Null(err);
        Assert.Equal(again.Tick, played.Tick);
        Assert.Equal(again.State, played.State);
        Assert.Equal(again.Attacker.Position, played.Attacker.Position);
        Assert.Equal(again.Keeper.Position, played.Keeper.Position);
        Assert.Equal(again.Ball.Position, played.Ball.Position);
        Assert.Equal(again.Shots.Count, played.Shots.Count);
        for (int i = 0; i < again.Shots.Count; i++)
        {
            Assert.Equal(again.Shots[i].Aimed, played.Shots[i].Aimed);
            Assert.Equal(again.Shots[i].Foot, played.Shots[i].Foot);
            Assert.Equal(again.Shots[i].Type, played.Shots[i].Type);
        }
        Assert.Null(played.Report);
    }

    [Fact]
    public void TakingACopyChangesNothingInTheRound()
    {
        var a = Recorded("edge_of_box", 3, out var recA);
        var b = Recorded("edge_of_box", 3, out _);
        var sa = new ScriptedAttacker(); var sb = new ScriptedAttacker();
        var buf = new TickIntent[a.PlayerCount];
        var copies = new List<Replay>();
        int cap = 20 * a.Tuning.TickRate;
        for (int i = 0; i < cap && a.State != RoundState.Report; i++)
        {
            buf[0] = sa.Next(a); a.Step(buf);
            if (i % 97 == 0) copies.Add(recA.Snapshot());   // two or more captures in one round
            buf[0] = sb.Next(b); b.Step(buf);
        }
        Assert.Equal(b.Tick, a.Tick);
        Assert.Equal(b.Ball.Position, a.Ball.Position);
        Assert.Equal(b.Report?.ToJson(), a.Report?.ToJson());
        Assert.True(copies.Count >= 2);
        for (int i = 1; i < copies.Count; i++) Assert.True(copies[i].TickCount > copies[i - 1].TickCount);
    }

    [Fact]
    public void AFullReplayWithoutTheNewFieldsRunsAsBefore()
    {
        string file = Path.Combine(TestData.DataDir, "..", "src", "FootballSim.Core.Tests", "TestData", "playtest", "round-001-penalty-held.json");
        var r = Replay.Load(file, out var err)!;
        Assert.Null(err);
        Assert.False(r.Partial);
        Assert.Null(r.Capture);
        var json = JsonNode.Parse(r.ToJson())!.AsObject();
        Assert.False(json.ContainsKey("partial"));
        Assert.False(json.ContainsKey("capture"));

        // Run is Play plus the idle tail: the same report as stepping the recording by hand
        var t = TestData.Tuning();
        var sc = TestData.Scenario(r.ScenarioId);
        var w = new World(t, sc, TestData.Player(r.AttackerId), TestData.Player(r.KeeperId), r.Seed);
        w.Begin();
        var buf = new TickIntent[w.PlayerCount];
        for (int tick = 0; tick < r.TickCount; tick++)
        {
            Array.Clear(buf);
            for (int p = 0; p < 2; p++) buf[p] = r.Intents[tick * 2 + p].ToIntent();
            w.Step(buf);
        }
        Array.Clear(buf);
        for (int i = 0; i < Replay.TailCap(t) && w.State != RoundState.Report; i++) w.Step(buf);
        Assert.Equal(w.Report!.ToJson(), r.Run(t, sc, TestData.Player(r.AttackerId), TestData.Player(r.KeeperId)).ToJson());
    }

    [Fact]
    public void TheCaptureBlockAndPartialSurviveTheFileWithAnyNote()
    {
        RoundRunner.RunToReport(TestData.Tuning(), TestData.Scenario("penalty"), TestData.Player("striker_a"), TestData.Player("the_wall"), 5, out var replay);
        var c = replay.Copy();
        string note = "the \"wrong\" foot,\nthen a back-slash \\ and ünïcödé · 足球 😀";
        c.Partial = true;
        c.Capture = new ReplayCapture { Id = "C14", Tag = "wrong foot", Note = note, Tick = 321, Clock = 2.675f, Wall = "2026-11-22T20:31:35" };
        var back = Replay.FromJson(c.ToJson(), out var err)!;
        Assert.Null(err);
        Assert.True(back.Partial);
        Assert.Equal("C14", back.Capture!.Id);
        Assert.Equal("wrong foot", back.Capture.Tag);
        Assert.Equal(note, back.Capture.Note);
        Assert.Equal(321, back.Capture.Tick);
        Assert.Equal(2.675f, back.Capture.Clock);
        Assert.Equal(replay.TickCount, back.TickCount);
        // the copy shares nothing with the original
        Assert.False(replay.Partial);
        Assert.Null(replay.Capture);
    }

    // ---- the index ----

    static string Entry(string id, string note = "") => CaptureIndex.Line(new CaptureEntry { Id = id, Session = "20261122-202130", Tag = "keeper", Note = note });

    [Fact]
    public void AMissingIndexGivesC1()
    {
        Assert.Equal("C1", CaptureIndex.NextId(null, out var w1));
        Assert.Empty(w1);
        Assert.Equal("C1", CaptureIndex.NextId("", out _));
        string path = Path.Combine(Path.GetTempPath(), $"captures-{Guid.NewGuid():N}.jsonl");
        Assert.Equal("C1", CaptureIndex.NextIdFromFile(path, out var w2));
        Assert.Empty(w2);
    }

    [Fact]
    public void TheNextIdIsOneMoreThanTheHighest()
    {
        string text = Entry("C3") + "\n" + Entry("C7") + "\n";
        Assert.Equal("C8", CaptureIndex.NextId(text, out var warnings));
        Assert.Empty(warnings);
        Assert.Equal("C8", CaptureIndex.NextId(Entry("C7") + "\n" + Entry("C3") + "\n", out _));
    }

    [Fact]
    public void ACorruptLineIsSkippedWithAWarningAndNeverLowersTheCount()
    {
        string text = Entry("C3") + "\n{ not json\n" + Entry("C7") + "\n{\"id\":\"banana\"}\n\"just a string\"\n" + Entry("C2") + "\n{\"id\":\"C99\"";
        Assert.Equal("C8", CaptureIndex.NextId(text, out var warnings));
        Assert.Equal(4, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("line 2"));
    }

    [Fact]
    public void IndexLinesRoundTripAndAreFoundById()
    {
        string path = Path.Combine(Path.GetTempPath(), $"captures-{Guid.NewGuid():N}.jsonl");
        try
        {
            string note = "quotes \" and\nnewline, ünïcödé 😀";
            Assert.Null(CaptureIndex.Append(path, new CaptureEntry { Id = "C1", Tag = "bad aim" }));
            Assert.Null(CaptureIndex.Append(path, new CaptureEntry
            {
                Id = "C2", Session = "s", Round = 4, Tag = "wrong foot", Note = note, Tick = 99, Clock = 0.825f, Partial = true,
                Scenario = "penalty", Attacker = "custom", Keeper = "the_wall", File = "s/capture-C2-penalty-wrong-foot.json",
            }));
            string text = File.ReadAllText(path);
            Assert.Equal(2, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Contains("ünïcödé", text);                       // readable, so a grep finds it
            Assert.Equal("C3", CaptureIndex.NextIdFromFile(path, out _));
            var e = CaptureIndex.Find(text, "c2", out var warnings)!;
            Assert.Empty(warnings);
            Assert.Equal(note, e.Note);
            Assert.True(e.Partial);
            Assert.Equal(4, e.Round);
            Assert.Equal("s/capture-C2-penalty-wrong-foot.json", e.File);
            Assert.Null(CaptureIndex.Find(text, "C9", out _));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FileNamesUseTheTagSlug()
    {
        Assert.Equal("capture-C14-penalty-shot-power-accuracy.json", CaptureIndex.ReplayFileName("C14", "penalty", "shot power / accuracy"));
        Assert.Equal(new[] { "wrong-foot", "bad-aim", "shot-power-accuracy", "touch-dribble", "keeper", "skill-move", "animation", "other" },
            CaptureTags.All.Select(CaptureTags.Slug).ToArray());
        Assert.Null(CaptureIndex.Number("C0"));
        Assert.Null(CaptureIndex.Number("X3"));
        Assert.Equal(12, CaptureIndex.Number("C12"));
    }

    // ---- the shot audit ----

    [Theory]
    [InlineData("penalty", 5UL)]
    [InlineData("edge_of_box", 11UL)]
    [InlineData("breakaway", 77UL)]
    public void TheShotAuditGivesOneLinePerShotWithTheRuleAtTheRelease(string scenario, ulong seed)
    {
        var t = TestData.Tuning();
        var sc = TestData.Scenario(scenario);
        var att = TestData.Player("striker_a");
        var kee = TestData.Player("the_wall");
        RoundRunner.RunToReport(t, sc, att, kee, seed, out var replay);

        // the rule World.StrikeGeometry gives on each release tick, and the shots struck
        var rules = new List<StrikeRule>();
        BallAction? seen = null;
        var w = replay.Play(t, sc, att, kee, (world, _) =>
        {
            if (world.Attacker.Action is { IsShot: true, Released: true } a && !ReferenceEquals(a, seen)) { seen = a; rules.Add(world.StrikeGeometry.Rule); }
        });
        int struck = w.Shots.Count;

        var audit = ShotReleaseAudit.Run(replay, t, sc, att, kee);
        Assert.NotEmpty(audit);
        Assert.Equal(rules, audit.Select(e => e.Rule).ToList());
        Assert.Equal(struck, audit.Count(e => e.Strike != null));
        for (int i = 0; i < audit.Count; i++)
        {
            string line = audit[i].Line(i);
            Assert.StartsWith($"shot{i} t{audit[i].Tick} ", line);
            Assert.Contains($"rule {audit[i].Rule} ", line);
            Assert.DoesNotContain('\n', line);
        }
    }

    [Fact]
    public void TheShotAuditOfAPartialReplayStopsAtItsLastTick()
    {
        var t = TestData.Tuning();
        var sc = TestData.Scenario("penalty");
        var att = TestData.Player("striker_a");
        var kee = TestData.Player("the_wall");
        RoundRunner.RunToReport(t, sc, att, kee, 5, out var replay);
        var full = ShotReleaseAudit.Run(replay, t, sc, att, kee);
        int cut = full[0].Tick;     // cut on the first release tick: released, not yet struck
        var partial = replay.Copy();
        partial.Partial = true;
        partial.Intents.RemoveRange(cut * partial.PlayersPerTick, partial.Intents.Count - cut * partial.PlayersPerTick);
        var audit = ShotReleaseAudit.Run(partial, t, sc, att, kee);
        Assert.Single(audit);
        Assert.Null(audit[0].Strike);
        Assert.Contains("strike none", audit[0].Line(0));
        Assert.Equal(full[0].Rule, audit[0].Rule);
        Assert.Equal(cut, partial.Play(t, sc, att, kee).Tick);
    }
}
