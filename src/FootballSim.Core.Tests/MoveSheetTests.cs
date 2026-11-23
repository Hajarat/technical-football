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

/// <summary>The follower's inputs the design implies but does not spell out (see SkillMoveTests.Sheets.cs for the rest).</summary>
public class MoveSheetFollowerTests
{
    [Theory]
    [InlineData(0.55f)]
    [InlineData(-0.55f)]
    public void MeetBeyondCorrectionFadesAsBias(float aside)
    {
        // The ball further from the sheet's contact point than meet_correction_max at the gesture: no jump. His path is nudged by
        // the most allowed, what is left is the first leg's bias, and it fades over the leg.
        var t = TestData.Tuning();
        var set = TestData.Sheets.Find("elastico", SkillSide.Left, Foot.Right)!.Value;
        var off = new SheetPlay(set.Stand, set.Stand, 0f, 0f, t.TickRate).ContactOffset();
        var root = set.Stand.RootAt(set.Stand.EventSeconds("contact"));
        // (his frame: +X along his run, +Z to his right; the sheet's x is to his left)
        var b = new SkillMoveTests.Bench(0f, root.Z + off.Z, -(root.X + off.X) + aside, 0f, t);
        Assert.True(b.Start(Gesture.Elastico, SkillSide.Left));
        var m = b.Move!;
        Assert.NotNull(m.Sheet);
        Assert.True(m.ContactTick > m.StartTick, "the move reaches for the ball");
        Assert.Equal(t.Skill.MeetCorrectionMax, m.MeetNudge.Length(), 3);
        Assert.Equal(MathF.Sign(aside), MathF.Sign(m.MeetNudge.Y));
        Vector3 prev = b.B.Position, prevV = b.B.Velocity;
        float bias0 = float.NaN;
        while (b.Tick < m.Contact2Tick)
        {
            int tick = b.Tick;
            b.Step();
            float moved = (b.B.Position - prev).Length();
            Assert.True(moved <= t.Touch.MaxPush * t.Dt + 1e-4f, $"tick +{tick - m.StartTick}: the ball moved {moved:F3} m in a tick");
            if (tick == m.ContactTick)
            {
                bias0 = new Vector2(m.MeetBias.X, m.MeetBias.Z).Length();
                // the contact changes the ball's speed by no more than a touch may give it
                Assert.True((b.B.Velocity - prevV).Length() <= t.Touch.MaxPush, $"the contact changed the ball's velocity by {(b.B.Velocity - prevV).Length():F2} m/s");
            }
            prev = b.B.Position; prevV = b.B.Velocity;
        }
        Assert.True(m.MeetNudgeMade.Length() <= t.Skill.MeetCorrectionMax + 1e-3f, $"nudged {m.MeetNudgeMade.Length():F3} m");
        Assert.True(bias0 > 0.15f, $"the bias at the contact was {bias0:F3} m");
        // by the end of the leg the ball is where the sheet has it against him: the bias has faded
        var end = m.Sheet!.Track(0, 1f);
        var want = b.P.Position + new Vector3(end.Z, 0f, -end.X);
        float left = new Vector2(b.B.Position.X - want.X, b.B.Position.Z - want.Z).Length();
        Assert.True(left < 0.03f, $"the ball ends {left:F3} m off the leg's end");
    }

    [Fact]
    public void EntrySweepStaysWithinLimits()
    {
        // Entry pace between sheets and above the fastest (0 to 8.5 m/s in 0.25 steps): the body's acceleration stays within the
        // files' limit at every step (scaled where he comes in faster than the fastest sheet: forward travel scales with his
        // pace), and no driven leg has the ball beyond the playing foot's reach of him.
        var t = TestData.Tuning();
        // (the largest plant_accel of each move's files, tools/anim/moves)
        foreach (var (kind, side, foot, fileLimit) in new[] { (ActionKind.Elastico, SkillSide.Left, Foot.Right, 23f), (ActionKind.DragBack, SkillSide.None, Foot.Right, 47f) })
        {
            var set = TestData.Sheets.Find(SkillMoves.SheetName(kind, false), side, foot)!.Value;
            float fastest = (set.Sprint ?? set.Run!).EntrySpeed;
            float prevReleaseZ = float.NaN;
            for (float entry = 0f; entry <= 8.5f + 1e-3f; entry += 0.25f)
            {
                var s = SkillMoves.SheetFor(kind, side, foot, entry, false, t);
                Assert.NotNull(s);
                float limit = fileLimit * MathF.Max(1f, entry / fastest) * 1.1f, dt = t.Dt;
                float ec = s!.EventSeconds("contact"), er = s.EventSeconds("release");
                for (float tau = s.Event(0) + dt; tau + dt <= er + 1e-4f; tau += dt)
                {
                    RootRow a = s.Root(tau - dt), b = s.Root(tau), c = s.Root(tau + dt);
                    float acc = new Vector2(a.X - 2f * b.X + c.X, a.Z - 2f * b.Z + c.Z).Length() / (dt * dt);
                    Assert.True(acc <= limit, $"{kind} entry {entry}: {acc:F1} m/s^2 at {tau:F3} s (limit {limit:F1})");
                }
                // he comes in at his own pace
                float pace0 = (s.Root(s.Event(0) + 2f * dt).Z - s.Root(s.Event(0)).Z) / (2f * dt);
                // (below the run blend's end the standing sheet has a share, and he eases onto the blend)
                Assert.True(pace0 <= entry + 0.6f, $"{kind} entry {entry}: the sheet comes in at {pace0:F2} m/s");
                if (entry >= 2.5f) Assert.True(MathF.Abs(pace0 - entry) <= 0.12f * entry, $"{kind} entry {entry}: the sheet comes in at {pace0:F2} m/s");
                // the ball on a driven leg stays within the leg's reach of his hips
                var off = s.ContactOffset();
                var rc = s.Root(ec);
                for (int leg = 0; leg < s.LegCount; leg++)
                {
                    if (!s.Leg(leg).Driven) continue;
                    float from = s.EventSeconds(s.Leg(leg).From), to = s.EventSeconds(s.Leg(leg).To);
                    for (float tau = from; tau <= to + 1e-4f; tau += dt)
                    {
                        var tr = s.Track(leg, s.LegShare(leg, tau));
                        var r = s.Root(tau);
                        Vector2 rel = s.Leg(leg).Anchor == LegAnchor.Pitch
                            ? new Vector2(off.X + tr.X - (r.X - rc.X), off.Z + tr.Z - (r.Z - rc.Z))
                            : new Vector2(tr.X, tr.Z);
                        Assert.True(rel.Length() <= 0.85f, $"{kind} entry {entry}: the ball is {rel.Length():F2} m from his hips at {tau:F3} s");
                    }
                }
                // and the blend is continuous in the entry pace: a quarter of a metre a second more moves the release a little
                float releaseZ = s.Root(er).Z;
                if (!float.IsNaN(prevReleaseZ)) Assert.True(MathF.Abs(releaseZ - prevReleaseZ) < 0.25f, $"{kind} entry {entry}: the release moved {releaseZ - prevReleaseZ:F2} m");
                prevReleaseZ = releaseZ;
            }
        }
    }
}
