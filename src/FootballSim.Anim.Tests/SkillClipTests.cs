using FootballSim.Anim;

namespace FootballSim.Anim.Tests;

/// <summary>The authored skill clips' timing: the piecewise time map onto the core's ticks, and the entry-speed blend of the standing and running clips.</summary>
public class SkillClipTests
{
    const float Dt = 1f / 120f;

    // the ball roll as authored: start 0, contact 0.117 (frame 7), release 0.517 (frame 31), end 0.767 (frame 46)
    const float CStart = 0f, CContact = 7 / 60f, CRelease = 31 / 60f, CEnd = 46 / 60f;

    [Theory]
    [InlineData(0.10f, 0.40f)]   // the core's reach and roll today
    [InlineData(0.12f, 0.52f)]   // as authored
    [InlineData(0.00f, 0.30f)]   // no reach: the clip starts at its contact
    [InlineData(0.025f, 0.225f)] // a short reach and roll: part way in, faster
    public void EventsLandOnTheirTicksAndTimeNeverRunsBack(float contact, float end)
    {
        var map = new SkillTimeMap(CStart, CContact, CRelease, CEnd, 0f, contact, end);
        Assert.Equal(CContact, map.ClipTime(contact), 4);
        Assert.Equal(CRelease, map.ClipTime(end), 4);
        Assert.Equal(CEnd, map.ClipTime(end + (CEnd - CRelease)), 4);
        float prev = map.ClipTime(0f);
        for (float t = 0f; t < end + 0.5f; t += Dt / 8f)
        {
            float c = map.ClipTime(t);
            Assert.True(c >= prev - 1e-6f, $"time ran back at {t}: {prev} -> {c}");
            Assert.True(map.Rate(t) >= -1e-5f);
            prev = c;
        }
        // after the settle it runs on in real time
        Assert.Equal(1f, map.Rate(end + 1f), 4);
    }

    [Fact]
    public void TheRateChangesSmoothlyAcrossTheSpanBoundaries()
    {
        // contact→release played 1.33x faster than authored (the core's 0.30 s roll), the settle in real time
        var map = new SkillTimeMap(CStart, CContact, CRelease, CEnd, 0f, 0.10f, 0.40f);
        foreach (float knot in new[] { 0.10f, 0.40f, 0.40f + (CEnd - CRelease) })
        {
            float before = map.Rate(knot - 1e-4f), after = map.Rate(knot + 1e-4f);
            Assert.True(MathF.Abs(after - before) < 0.02f, $"rate steps at {knot}: {before} -> {after}");
        }
        // no tick's rate changes by more than a tenth of the clip's speed (a linear piecewise map would step 0.17 -> 1.33 -> 1)
        float r0 = map.Rate(0f);
        for (float t = Dt; t < 0.8f; t += Dt)
        {
            float r = map.Rate(t);
            Assert.True(MathF.Abs(r - r0) < 0.1f, $"rate steps by {r - r0} at {t}");
            r0 = r;
        }
    }

    [Fact]
    public void AShortReachStartsTheClipPartWayInAtMostMaxRate()
    {
        var none = new SkillTimeMap(CStart, CContact, CRelease, CEnd, 0f, 0f, 0.3f);
        Assert.Equal(CContact, none.ClipStart, 5);
        var shortReach = new SkillTimeMap(CStart, CContact, CRelease, CEnd, 0f, 0.025f, 0.3f);
        Assert.Equal(CContact - SkillTimeMap.MaxRate * 0.025f, shortReach.ClipStart, 5);
        Assert.Equal(CContact, shortReach.ClipTime(0.025f), 4);
        var full = new SkillTimeMap(CStart, CContact, CRelease, CEnd, 0f, 0.10f, 0.4f);
        Assert.Equal(0f, full.ClipStart, 5);
    }

    [Fact]
    public void AnEarlierOriginCountsTheTimeAlreadyGone()
    {
        // a fake shot: the wind-up began 0.08 s before the move's first tick, the contact 0.02 s after it
        var map = new SkillTimeMap(0f, 0.30f, 0.40f, 0.68f, -0.08f, 0.02f, 0.12f);
        Assert.Equal(0.30f - SkillTimeMap.MaxRate * 0.10f, map.ClipStart, 5);
        Assert.True(map.ClipTime(0f) > map.ClipStart);
        Assert.Equal(0.30f, map.ClipTime(0.02f), 4);
        Assert.Equal(0.40f, map.ClipTime(0.12f), 4);
    }

    [Fact]
    public void TheMiddleOfAContactlessMoveIsItsContact()
    {
        // a step-over: the core's contact is the middle tick of the move after a close-in
        float close = 0.05f, dur = 0.25f;
        var map = new SkillTimeMap(0f, 13 / 60f, 25 / 60f, 40 / 60f, 0f, close + dur / 2f, close + dur);
        Assert.Equal(13 / 60f, map.ClipTime(close + dur / 2f), 4);
        Assert.Equal(25 / 60f, map.ClipTime(close + dur), 4);
    }

    [Fact]
    public void ASpinsLiftAndSecondContactLandOnTheirTicks()
    {
        // the spin as authored: contact 7, lift 23, contact2 25, release 43, end 55 (60 fps); the core: contact after a 0.1 s reach,
        // lift at contact + 30 ticks, contact2 at + 36, end at + 72
        float c(int fr) => fr / 60f;
        float sc = 0.1f, sl = sc + 30 * Dt, s2 = sc + 36 * Dt, se = sc + 72 * Dt;
        var map = new SkillTimeMap(0f, new[] { (sc, c(7)), (sl, c(23)), (s2, c(25)), (se, c(43)) }, c(55), 0f);
        Assert.Equal(c(7), map.ClipTime(sc), 4);
        Assert.Equal(c(23), map.ClipTime(sl), 4);
        Assert.Equal(c(25), map.ClipTime(s2), 4);
        Assert.Equal(c(43), map.ClipTime(se), 4);
        float prev = -1f;
        for (float t = 0f; t < se + 0.3f; t += Dt / 4f) { float v = map.ClipTime(t); Assert.True(v >= prev - 1e-6f); prev = v; }
    }

    [Fact]
    public void TwoClipsWithDifferentSpansMeetAtEachEventOnItsTick()
    {
        // the standing spin (lift to contact2 0.03 s) and a running one with the core's 0.05 s: each clip's own map puts each of its
        // events on the core's tick, so the blend is at the same event on the same tick
        var stand = new Dictionary<string, int> { ["start"] = 0, ["contact"] = 7, ["lift"] = 23, ["contact2"] = 25, ["release"] = 43, ["end"] = 55 };
        var run = new Dictionary<string, int> { ["start"] = 0, ["contact"] = 7, ["lift"] = 22, ["contact2"] = 25, ["release"] = 43, ["end"] = 55 };
        float sc = 0.1f, sl = sc + 30 * Dt, s2 = sc + 36 * Dt, se = sc + 72 * Dt;
        var ev = new[] { ("contact", sc), ("lift", sl), ("contact2", s2), ("release", se) };
        var ms = SkillTimeMap.ForEvents(stand, 60, ev, 0f);
        var mr = SkillTimeMap.ForEvents(run, 60, ev, 0f);
        foreach (var (e, t) in ev)
        {
            Assert.Equal(stand[e] / 60f, ms.ClipTime(t), 4);
            Assert.Equal(run[e] / 60f, mr.ClipTime(t), 4);
        }
        Assert.Equal(ms.SimEnd, mr.SimEnd, 4);
        // the same events give the same map; an event the clip does not have is left out
        var same = SkillTimeMap.ForEvents(stand, 60, ev, 0f);
        var direct = new SkillTimeMap(0f, new[] { (sc, 7 / 60f), (sl, 23 / 60f), (s2, 25 / 60f), (se, 43 / 60f) }, 55 / 60f, 0f);
        for (float t = 0f; t < se + 0.3f; t += Dt / 4f) { Assert.Equal(ms.ClipTime(t), same.ClipTime(t)); Assert.Equal(direct.ClipTime(t), ms.ClipTime(t)); }
        var roll = new Dictionary<string, int> { ["start"] = 0, ["contact"] = 7, ["release"] = 31, ["end"] = 46 };
        var mroll = SkillTimeMap.ForEvents(roll, 60, ev, 0f);
        Assert.Equal(31 / 60f, mroll.ClipTime(se), 4);
        Assert.Equal(4, mroll.KnotCount);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(-1f, 0f)]
    [InlineData(0.75f, 0.5f)]
    [InlineData(1.5f, 1f)]
    [InlineData(6f, 1f)]
    public void TheEntrySpeedBlendsStandingIntoRunning(float speed, float want)
    {
        Assert.Equal(want, SkillEntryBlend.RunWeight(speed, 0f, 1.5f), 4);
    }

    [Fact]
    public void TheEntryBlendIsMonotoneAndEased()
    {
        float prev = 0f;
        for (float v = 0f; v <= 3f; v += 0.01f)
        {
            float w = SkillEntryBlend.RunWeight(v, 0f, 3f);
            Assert.True(w >= prev - 1e-6f);
            prev = w;
        }
        // eased at both ends: the first and last tenth of the speed range move the weight by less than a tenth
        Assert.True(SkillEntryBlend.RunWeight(0.3f, 0f, 3f) < 0.05f);
        Assert.True(SkillEntryBlend.RunWeight(2.7f, 0f, 3f) > 0.95f);
        // a pair with one entry speed picks one clip
        Assert.Equal(0f, SkillEntryBlend.RunWeight(1f, 2f, 2f));
        Assert.Equal(1f, SkillEntryBlend.RunWeight(2f, 2f, 2f));
    }

    static string? SkillClipDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var d = Path.Combine(dir.FullName, "game", "assets", "anim", "attacker", "skills");
            if (File.Exists(Path.Combine(d, "clips.json"))) return d;
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void EveryMoveInTheSimulationHasBothEntriesForEachFoot()
    {
        var dir = SkillClipDir();
        Assert.NotNull(dir);
        var lib = SkillClipLibrary.Parse(File.ReadAllText(Path.Combine(dir!, "clips.json")), f => File.ReadAllBytes(Path.Combine(dir!, f)));
        foreach (var move in new[] { "ball_roll", "drag_back", "step_over", "body_feint", "fake_stop", "fake_cut", "elastico", "spin", "heel_to_heel" })
            for (int foot = 0; foot < 2; foot++)
            {
                int side = move is "ball_roll" or "elastico" or "spin" ? (foot == 1 ? -1 : 1) : move is "step_over" or "body_feint" ? (foot == 1 ? 1 : -1) : move == "fake_cut" ? (foot == 1 ? -1 : 1) : 0;
                var pair = lib.Pair(move, foot, side);
                Assert.True(pair.HasValue, $"{move} foot {foot}");
                var (stand, run) = pair!.Value;
                Assert.Equal("stand", stand.Entry); Assert.Equal("run", run.Entry);
                Assert.True(run.EntrySpeed > stand.EntrySpeed);
                // the two variants have the same events in the same order (each has its own time map onto them; the spans may differ)
                Assert.Equal(stand.Clip.Events.OrderBy(e => e.Value).Select(e => e.Key), run.Clip.Events.OrderBy(e => e.Value).Select(e => e.Key));
                Assert.True(MathF.Abs(stand.RootYaw) < 0.2f, $"{stand.Clip.Name} root yaw {stand.RootYaw}");
            }
    }

    [Theory]
    [InlineData(1, -1, "fake_cut_left")]       // the right foot's cut to the left: the inside hook
    [InlineData(1, 1, "fake_cut_out_right")]   // to its own side: the outside hook
    [InlineData(0, 1, "fake_cut_right")]
    [InlineData(0, -1, "fake_cut_out_left")]
    public void AFakeCutTowardTheFootsOwnSideGetsTheOutsideHook(int foot, int side, string clip)
    {
        var dir = SkillClipDir();
        Assert.NotNull(dir);
        var lib = SkillClipLibrary.Parse(File.ReadAllText(Path.Combine(dir!, "clips.json")), f => File.ReadAllBytes(Path.Combine(dir!, f)));
        var pair = lib.Pair("fake_cut", foot, side);
        Assert.True(pair.HasValue);
        Assert.Equal(clip, pair!.Value.Stand.Clip.Name);
        Assert.Equal(clip + "_run", pair.Value.Run.Clip.Name);
        Assert.Equal(foot, pair.Value.Stand.Foot);
    }
}
