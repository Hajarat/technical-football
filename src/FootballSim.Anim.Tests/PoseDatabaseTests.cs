using System.Numerics;
using FootballSim.Anim;

namespace FootballSim.Anim.Tests;

/// <summary>The pose database from the committed clips: the verdicts are honoured, every kept clip enters with its mirror, features
/// are normalised per group, and the features describe the motion (a stance stands, a run runs forward).</summary>
public class PoseDatabaseTests
{
    static readonly string[] Rejected = { "body_block_144_07", "body_block_144_26", "back_leap_jump_81_12", "smother_roll_90_34" };

    [Fact]
    public void TheLibraryCarriesTheAuditionVerdicts()
    {
        var lib = TestKit.Keeper;
        Assert.Equal(40, lib.Clips.Count);   // 35 retargeted from CMU, 5 baked from the procedural keeper
        Assert.Equal(Rejected.OrderBy(s => s), lib.Clips.Where(c => c.Verdict == ClipVerdict.Reject).Select(c => c.Clip.Name).OrderBy(s => s));
        Assert.Equal(36, lib.Clips.Count(c => c.Verdict == ClipVerdict.Keep));
        Assert.Equal(22, lib.Skeleton.Count);
    }

    [Fact]
    public void RejectedClipsNeverEnterTheDatabaseAndKeptOnesEnterWithTheirMirror()
    {
        var lib = TestKit.Keeper;
        var sets = lib.Clips.Select(c => c.Set).Distinct().ToArray();
        var db = new PoseDatabase(lib, sets);
        var names = db.Sets.SelectMany(s => s.Clips).Select(c => c.Clip.Name).ToList();
        foreach (var r in Rejected) { Assert.DoesNotContain(r, names); Assert.DoesNotContain(r + "_mirror", names); }
        foreach (var c in lib.Clips.Where(c => c.Verdict != ClipVerdict.Reject))
        {
            Assert.Contains(c.Clip.Name, names);
            Assert.Contains(c.Clip.Name + "_mirror", names);
        }
        Assert.Null(db.Set("body_block"));   // both of its clips were rejected
        Assert.NotNull(db.Set(KeeperChooser.Stance));
        Assert.NotNull(db.Set(KeeperChooser.Backpedal));
        Assert.NotNull(db.Set(KeeperChooser.Run));
    }

    [Fact]
    public void AStanceForStandingStillHoldsOnlyIdleClipsAndFallsBackWithoutThem()
    {
        var db = new PoseDatabase(TestKit.Keeper, KeeperChooser.Sets);
        var idle = db.Set(KeeperChooser.StanceIdle)!;
        Assert.All(idle.Clips, c => Assert.StartsWith("stance_ready_77_03", c.Clip.Name));
        Assert.True(db.Set(KeeperChooser.Stance)!.Clips.Count > idle.Clips.Count);
        // a library without idle clips: standing still uses the whole stance set
        var sk = TestKit.Legs();
        var lib = new ClipLibrary(sk, new[] { new ClipEntry(TestKit.Stand(sk, "stand", 60), "stance", "none", ClipVerdict.Keep) });
        var db2 = new PoseDatabase(lib, KeeperChooser.Sets);
        Assert.Same(db2.Set(KeeperChooser.Stance), db2.Set(KeeperChooser.StanceIdle));
    }

    [Fact]
    public void EachFeatureGroupIsNormalisedToItsWeight()
    {
        var db = new PoseDatabase(TestKit.Keeper, KeeperChooser.Sets);
        var w = FeatureWeights.Default;
        foreach (var set in db.Sets.Where(s => s.FrameCount > 0))   // a set with no kept clip (yet) has nothing to normalise
        {
            for (int g = 0; g < Features.Groups.Length; g++)
            {
                var (s0, len) = Features.Groups[g];
                double sumStd = 0;
                for (int d = s0; d < s0 + len; d++)
                {
                    double m = 0, v = 0;
                    for (int i = 0; i < set.FrameCount; i++) m += set.Normalized(i)[d];
                    m /= set.FrameCount;
                    for (int i = 0; i < set.FrameCount; i++) { double e = set.Normalized(i)[d] - m; v += e * e; }
                    sumStd += Math.Sqrt(v / set.FrameCount);
                    Assert.InRange(m, -1e-3, 1e-3);
                }
                // (a group that hardly varies over the set, such as one baked clip's steady facing, is left at its floor scale)
                double rawStd = 0;
                for (int d = s0; d < s0 + len; d++)
                {
                    double m = 0, v = 0;
                    for (int i = 0; i < set.FrameCount; i++) m += set.Raw(i)[d];
                    m /= set.FrameCount;
                    for (int i = 0; i < set.FrameCount; i++) { double e = set.Raw(i)[d] - m; v += e * e; }
                    rawStd += Math.Sqrt(v / set.FrameCount);
                }
                if (rawStd / len < 1e-3) continue;
                Assert.InRange(sumStd / len, Features.Weight(w, g) * 0.999, Features.Weight(w, g) * 1.001);
            }
        }
    }

    [Fact]
    public void FeaturesDescribeTheMotion()
    {
        var lib = TestKit.Keeper;
        var db = new PoseDatabase(lib, KeeperChooser.Sets);
        var stance = db.Set(KeeperChooser.Stance)!;
        var ready = stance.Clips[stance.IndexOf("stance_ready_77_03")];
        var raw = stance.Raw(ready.Start + 30);
        // feet apart, left foot on the left (+X), both near the ground; little motion ahead
        Assert.True(raw[Features.FootPos] > 0.08f && raw[Features.FootPos + 3] < -0.08f);
        Assert.InRange(raw[Features.FootPos + 1], 0.0f, 0.2f);
        Assert.InRange(new Vector2(raw[Features.TrajPos + 4], raw[Features.TrajPos + 5]).Length(), 0f, 0.3f);
        var run = db.Set(KeeperChooser.Run)!;
        var jog = run.Clips[run.IndexOf("run_jog_16_35")];
        var j = run.Raw(jog.Start + jog.Clip.FrameCount / 2);
        Assert.True(j[Features.TrajPos + 5] > 0.8f, $"a jog covers ground forward (+Z) in 0.5 s: {j[Features.TrajPos + 5]}");
        Assert.True(j[Features.HipVel + 2] > 1.5f);
    }
}
