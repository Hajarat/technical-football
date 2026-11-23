using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>Move sheets: the loader, the mirror and the blend (see MoveSheet.cs).</summary>
public class MoveSheetTests
{
    static string SheetsJson => File.ReadAllText(DataLoader.SheetsPath(TestData.DataDir));
    static string IndexJson => File.ReadAllText(DataLoader.SheetClipIndexPath(TestData.DataDir));

    [Fact]
    public void TheShippedSheetsLoadAgainstTheClipIndex()
    {
        var r = DataLoader.LoadSheets(TestData.DataDir);
        Assert.True(r.Ok, r.Error);
        Assert.Empty(r.Warnings);
        // every move has a standing sheet for each foot it can be played with
        foreach (var move in new[] { "step_over", "body_feint", "ball_roll", "drag_back", "elastico", "heel_to_heel", "spin", "fake_stop", "fake_cut" })
            Assert.Contains(r.Value!.Authored, s => s.Move == move && s.Entry == "stand");
        var t = DataLoader.LoadTuning(Path.Combine(TestData.DataDir, "tuning", "default.json"));
        Assert.NotNull(t.Value!.Sheets);
        Assert.Equal(r.Value!.Sha, t.Value.Sheets!.Sha);
    }

    [Fact]
    public void ASheetRoundTrips()
    {
        var a = MoveSheets.Parse(SheetsJson);
        var b = MoveSheets.Parse(a.ToJson());
        Assert.Equal(a.Authored.Count, b.Authored.Count);
        for (int i = 0; i < a.Authored.Count; i++) Assert.True(a.Authored[i].ContentEquals(b.Authored[i]), a.Authored[i].Name);
        Assert.Equal(a.Sha, b.Sha);
    }

    [Fact]
    public void TheMirrorOfAMirroredSheetIsTheSheet()
    {
        foreach (var s in TestData.Sheets.Authored)
        {
            var m = s.Mirror();
            Assert.True(s.ContentEquals(m.Mirror()), s.Name);
            if (s.Root.Any(r => MathF.Abs(r.X) > 1e-3f)) Assert.False(s.ContentEquals(m), s.Name);
            Assert.NotEqual(s.Foot, m.Foot);
            for (int i = 0; i < s.Root.Length; i++)
            {
                Assert.Equal(-s.Root[i].X, m.Root[i].X);
                Assert.Equal(s.Root[i].Z, m.Root[i].Z);
                Assert.Equal(-s.Root[i].YawDeg, m.Root[i].YawDeg);
            }
        }
    }

    [Fact]
    public void BothSidesOfAMoveAreFound()
    {
        var sheets = TestData.Sheets;
        var left = sheets.Find("elastico", SkillSide.Left, Foot.Right);
        var right = sheets.Find("elastico", SkillSide.Right, Foot.Left);
        Assert.NotNull(left); Assert.NotNull(right);
        Assert.NotNull(left!.Value.Run);
        Assert.Null(sheets.Find("elastico", SkillSide.Left, Foot.Left));
        // a move without a side is found by its foot alone
        Assert.NotNull(sheets.Find("drag_back", SkillSide.None, Foot.Left));
        Assert.NotNull(sheets.Find("drag_back", SkillSide.None, Foot.Right));
        // the fake cut's two hooks share the move and are told apart by side and foot
        Assert.Equal("fake_cut", sheets.Find("fake_cut", SkillSide.Left, Foot.Right)!.Value.Stand.Name);
        Assert.Equal("fake_cut_out", sheets.Find("fake_cut", SkillSide.Right, Foot.Right)!.Value.Stand.Name);
    }

    [Fact]
    public void AHashTheClipIndexDoesNotCarryIsRefusedWithTheFileNamed()
    {
        var sheets = MoveSheets.Parse(SheetsJson);
        string sha = sheets.Authored[0].Sha;
        string bad = SheetsJson.Replace($"\"sha\":\"{sha}\"", "\"sha\":\"0000000000000000\"");
        var e = Assert.Throws<FormatException>(() => MoveSheets.Parse(bad, "data/moves/sheets.json", IndexJson, "clips.json"));
        Assert.Contains("data/moves/sheets.json", e.Message);
        Assert.Contains(sheets.Authored[0].Name, e.Message);
        Assert.Contains("0000000000000000", e.Message);
        // a clip index that names a sheet the file has not got is refused too
        var one = new MoveSheets(sheets.Authored.Skip(1).ToList());
        Assert.Throws<FormatException>(() => MoveSheets.Parse(one.ToJson(), "s.json", IndexJson));
        // and the same sheets against their own index load
        Assert.Equal(sheets.Sha, MoveSheets.Parse(SheetsJson, "s.json", IndexJson).Sha);
    }

    [Fact]
    public void AReplayRecordsTheSheetsHashBesideTheTuningHash()
    {
        // the sheets are not part of the tuning hash: a round played without them is recorded as before they existed
        var plain = new Tuning();
        var with = new Tuning { Sheets = TestData.Sheets };
        Assert.Equal(DataLoader.TuningHash(plain), DataLoader.TuningHash(with));
        var w = new World(with, TestData.Scenario("penalty"), TestData.Player("striker_a"), TestData.Player("the_wall"), 1);
        var rec = new ReplayRecorder(w, "striker_a", "the_wall", "penalty");
        Assert.Equal(TestData.Sheets.Sha, rec.Replay.SheetsHash);
        Assert.True(rec.Replay.MatchesTuning(with));
        Assert.False(rec.Replay.MatchesTuning(new Tuning { Sheets = new MoveSheets(TestData.Sheets.Authored.Skip(1).ToList()) }));
        Assert.False(rec.Replay.MatchesTuning(plain));
        var w0 = new World(plain, TestData.Scenario("penalty"), TestData.Player("striker_a"), TestData.Player("the_wall"), 1);
        var rec0 = new ReplayRecorder(w0, "striker_a", "the_wall", "penalty");
        Assert.Null(rec0.Replay.SheetsHash);
        Assert.True(rec0.Replay.MatchesTuning(with));
    }

    static SheetSet Elastico => TestData.Sheets.Find("elastico", SkillSide.Left, Foot.Right)!.Value;

    [Fact]
    public void AtASheetsOwnEntrySpeedTheBlendIsThatSheet()
    {
        var set = Elastico;
        var run = set.Run!;
        foreach (var (w, own, pace) in new[] { (0f, set.Stand, 0f), (1f, run, run.EntrySpeed) })
        {
            var play = new SheetPlay(set.Stand, run, w, pace, 120);
            float end = own.EventSeconds("end");
            for (float tau = 0f; tau <= end; tau += 1f / 240f)
            {
                var a = play.Root(tau); var b = own.RootAt(tau);
                Assert.True(MathF.Abs(b.X - a.X) < 1e-5f && MathF.Abs(b.Z - a.Z) < 1e-5f && MathF.Abs(b.YawDeg - a.YawDeg) < 1e-3f, $"tau={tau}: {a} vs {b}");
            }
            for (int leg = 0; leg < play.LegCount; leg++)
                for (float u = 0f; u <= 1f; u += 0.05f)
                    Assert.True((play.Track(leg, u) - own.TrackAt(leg, u)).Length() < 1e-5f);
            Assert.True((play.ContactOffset() - (own.Ball0 - new Vector3(own.RootAt(own.EventSeconds("contact")).X, 0f, own.RootAt(own.EventSeconds("contact")).Z))).Length() < 1e-5f);
        }
    }

    [Fact]
    public void TheBlendIsContinuousInItsShareAndKeepsTheEventsInOrder()
    {
        var set = Elastico;
        var run = set.Run!;
        RootRow? prevMid = null;
        for (int k = 0; k <= 100; k++)
        {
            float w = k / 100f;
            var play = new SheetPlay(set.Stand, run, w, run.EntrySpeed, 120);
            for (int i = 1; i < play.EventCount; i++) Assert.True(play.Event(i) > play.Event(i - 1), $"w={w} event {i}");
            // events after the contact fall on whole ticks from it
            for (int i = play.EventIndex("contact") + 1; i < play.EventCount; i++)
            {
                float ticks = (play.Event(i) - play.EventSeconds("contact")) * 120f;
                Assert.Equal(MathF.Round(ticks), ticks, 3);
            }
            float tau = play.EventSeconds("contact") + 0.5f * (play.EventSeconds("release") - play.EventSeconds("contact"));
            var mid = play.Root(tau);
            if (prevMid is { } p)
            {
                // (a step of a hundredth in the share moves the root by centimetres at most; the snap of an event onto a tick a little more)
                Assert.True(MathF.Abs(mid.X - p.X) < 0.02f && MathF.Abs(mid.Z - p.Z) < 0.06f, $"w={w}: {p} -> {mid}");
            }
            prevMid = mid;
        }
    }

    [Fact]
    public void ForwardTravelScalesWithTheEntryPaceAndSidewaysTravelDoesNot()
    {
        var run = Elastico.Run!;
        float end = run.EventSeconds("release");
        var same = new SheetPlay(run, run, 0f, run.EntrySpeed, 120).Root(end);
        var fast = new SheetPlay(run, run, 0f, run.EntrySpeed * 1.5f, 120).Root(end);
        Assert.Equal(same.Z * 1.5f, fast.Z, 3);
        Assert.Equal(same.X, fast.X, 5);
        Assert.Equal(same.YawDeg, fast.YawDeg, 5);
    }

    [Fact]
    public void TheRootIsSmoothBetweenItsFrames()
    {
        // sampled at the simulation's tick, the cubic through the 60 Hz rows passes through them and never steps
        foreach (var s in TestData.Sheets.Authored)
        {
            for (int i = 0; i < s.Root.Length; i++)
            {
                var r = s.RootAt(i / (float)s.Fps);
                Assert.True(MathF.Abs(s.Root[i].X - r.X) < 1e-5f && MathF.Abs(s.Root[i].Z - r.Z) < 1e-5f, $"{s.Name} frame {i}");
            }
            float last = (s.Root.Length - 1) / (float)s.Fps;
            var held = s.RootAt(last + 1f);
            Assert.True(MathF.Abs(s.Root[^1].X - held.X) < 1e-5f && MathF.Abs(s.Root[^1].Z - held.Z) < 1e-5f, s.Name);
        }
    }
}
