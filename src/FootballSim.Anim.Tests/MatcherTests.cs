using System.Numerics;
using FootballSim.Anim;

namespace FootballSim.Anim.Tests;

/// <summary>The search: an exact query finds its frame, ties go to the lower index, a search stays in its set, the current frame is
/// kept unless beaten by 5 %, clip ends are never picked, and an empty set gives nothing to play.</summary>
public class MatcherTests
{
    static readonly Skeleton Sk = TestKit.Legs();

    static PoseSet TwoWalks() => new("walk", new[]
    {
        (TestKit.Walk(Sk, "slow", 120, 0.6f, 1.1f), "none"),
        (TestKit.Walk(Sk, "fast", 150, 2.2f, 1.9f, 0.7f), "none"),
    }, Sk);

    [Fact]
    public void QueryEqualToADatabaseFrameReturnsThatFrame()
    {
        var set = TwoWalks();
        int tested = 0;
        for (int i = 0; i < set.FrameCount; i += 7)
        {
            if (!set.Searchable(i)) continue;
            int best = Matcher.Best(set, set.Normalized(i), out float cost);
            Assert.Equal(i, best);
            Assert.Equal(0f, cost);
            tested++;
        }
        Assert.True(tested > 20);
    }

    [Fact]
    public void TiesPickTheLowerIndex()
    {
        var a = TestKit.Walk(Sk, "a", 100, 1f, 1.5f);
        var b = TestKit.Walk(Sk, "b", 100, 1f, 1.5f);   // the same motion again: every frame of b ties with a's
        var set = new PoseSet("twins", new[] { (a, "none"), (b, "none") }, Sk);
        int best = Matcher.Best(set, set.Normalized(100 + 40), out _);
        Assert.Equal(40, best);
        Assert.Equal(40, Matcher.Search(set, set.Normalized(140), -1));
    }

    [Fact]
    public void SearchStaysInsideTheChosenSet()
    {
        var walk = TestKit.Walk(Sk, "walk", 120, 1.4f, 1.6f);
        var stand = TestKit.Stand(Sk, "stand", 120);
        var lib = new ClipLibrary(Sk, new[]
        {
            new ClipEntry(walk, "run", "none", ClipVerdict.Keep),
            new ClipEntry(stand, "stance", "none", ClipVerdict.Keep),
        });
        var db = new PoseDatabase(lib, new[] { "stance", "run" });
        var stance = db.Set("stance")!;
        var run = db.Set("run")!;
        Assert.All(stance.Clips, c => Assert.StartsWith("stand", c.Clip.Name));
        // the query is a walking frame; searched in the stance set it must still land on a standing frame
        var q = new float[Features.Count];
        stance.Normalize(run.Raw(30), q);
        int best = Matcher.Best(stance, q, out _);
        Assert.InRange(best, 0, stance.FrameCount - 1);
        Assert.StartsWith("stand", stance.Locate(best, out _).Clip.Name);
    }

    [Fact]
    public void AnEmptySetFallsBack()
    {
        var stand = TestKit.Stand(Sk, "stand", 60);
        var block = TestKit.Walk(Sk, "block", 60, 0f, 2f);
        var lib = new ClipLibrary(Sk, new[]
        {
            new ClipEntry(stand, "stance", "none", ClipVerdict.Keep),
            new ClipEntry(block, "body_block", "none", ClipVerdict.Reject),
        });
        var db = new PoseDatabase(lib, new[] { "stance", "body_block", "catch" });
        Assert.Null(db.Set("body_block"));   // only a rejected clip
        Assert.Null(db.Set("catch"));        // no clip at all
        Assert.Null(db.Set(null));
        var empty = new PoseSet("none", Array.Empty<(Clip, string)>(), Sk);
        Assert.Equal(-1, Matcher.Best(empty, new float[Features.Count], out _));
        Assert.Equal(-1, Matcher.Search(empty, new float[Features.Count], -1));
    }

    [Fact]
    public void TheCurrentFrameIsKeptUnlessBeatenByFivePercent()
    {
        var set = TwoWalks();
        var q = set.Normalized(60).ToArray();
        for (int d = Features.TrajPos; d < Features.Count; d++) q[d] += 2f;   // no frame matches exactly; near frames cost about the same
        int best = Matcher.Best(set, q, out float bestCost);
        int kept = -1, beaten = -1;
        for (int i = 0; i < set.FrameCount; i++)
        {
            if (i == best || !set.Searchable(i)) continue;
            float c = Matcher.Cost(set, i, q);
            if (kept < 0 && bestCost >= 0.95f * c) kept = i;
            if (beaten < 0 && bestCost < 0.95f * c) beaten = i;
        }
        Assert.True(kept >= 0 && beaten >= 0);
        Assert.Equal(kept, Matcher.Search(set, q, kept));
        Assert.Equal(best, Matcher.Search(set, q, beaten));
    }

    [Fact]
    public void ABestFrameCloseToTheCurrentOneInTheSameClipIsNoJump()
    {
        var set = TwoWalks();
        var q = set.Normalized(50).ToArray();
        Assert.Equal(50, Matcher.Search(set, q, 62));                 // without the window: jumps back to 50
        Assert.Equal(62, Matcher.Search(set, q, 62, 0.25f));          // 12 frames = 0.2 s: the clip carries on
        Assert.Equal(50, Matcher.Search(set, q, 90, 0.25f));          // 40 frames away: jumps
    }

    [Fact]
    public void FramesNearAOneShotClipsEndAreNeverPicked()
    {
        var set = TwoWalks();
        var slow = set.Clips[0];
        int tail = (int)MathF.Round(PoseSet.EndExclusionSeconds * 60);
        for (int f = 0; f < slow.Clip.FrameCount; f++) Assert.Equal(f < slow.Clip.FrameCount - tail, set.Searchable(slow.Start + f));
        int last = slow.End - 1;
        int best = Matcher.Best(set, set.Normalized(last), out _);
        Assert.True(set.Searchable(best));
        // ... and the current frame is not kept once it is in that tail, nor swapped for the half second just played
        int next = Matcher.Search(set, set.Normalized(last), last);
        Assert.NotEqual(last, next);
        Assert.False(next >= slow.End - tail - 30 && next < slow.End, $"jumped back only to {next}");
        // a looping clip has no tail
        var loop = new PoseSet("loop", new[] { (TestKit.Walk(Sk, "loop", 90, 1f, 1f, 0, loop: true), "none") }, Sk);
        for (int f = 0; f < 90; f++) Assert.True(loop.Searchable(f));
    }

    [Fact]
    public void ARightShuffleQueryAtFacingPiFindsAClipMovingRight()
    {
        // the keeper faces π (down the arena, as in the gallery) and the simulation moves him to his right: world -Z
        float facing = MathF.PI;
        var worldVel = new Vector3(0, 0, -1.6f);
        var mv = ModelSpace.FromWorld(worldVel);
        float yaw = ModelSpace.YawFromFacing(facing);
        Span<Vector2> tp = stackalloc Vector2[3];
        Span<Vector2> td = stackalloc Vector2[3];
        Trajectory.Predict(new Vector2(mv.X, mv.Z) * 0.5f, Vector2.Zero, new Vector2(mv.X, mv.Z), yaw, 0, yaw, 0.15f, tp, td);
        Assert.True(tp[2].X < -0.3f, $"his right is character -X; got {tp[2]}");   // character space: left is +X
        Assert.InRange(MathF.Abs(tp[2].Y), 0, 0.05f);
        Assert.InRange(td[2].Y, 0.999f, 1.001f);

        // trajectory only: the side is what is checked here
        var db = new PoseDatabase(TestKit.Keeper, new[] { KeeperChooser.Stance }, new FeatureWeights(0, 0, 0, 1f, 1.5f));
        var set = db.Set(KeeperChooser.Stance)!;
        var raw = set.Raw(0).ToArray();
        Features.WriteTrajectory(raw, tp, td);
        var q = new float[Features.Count];
        set.Normalize(raw, q);
        int best = Matcher.Best(set, q, out _);
        var pc = set.Locate(best, out _);
        Assert.True(pc.Side == "right", $"picked {pc.Clip.Name} ({pc.Side}); query {string.Join(",", raw.Select(v => v.ToString("F2")))}; frame {string.Join(",", set.Raw(best).ToArray().Select(v => v.ToString("F2")))}");
    }
}
