using System.Numerics;
using FootballSim.Anim;

namespace FootballSim.Anim.Tests;

/// <summary>The keeper's mocap motion on synthetic clips: deterministic, a teleport resets the blend, an empty set shows the procedural
/// pose (and leaving the clips blends into it), the search clock, planted feet and the root's pull and snap.</summary>
public class KeeperMotionTests
{
    const float Dt = 1f / 120f;
    static readonly Skeleton Sk = TestKit.Legs();

    static PoseDatabase Db()
    {
        var lib = new ClipLibrary(Sk, new[]
        {
            new ClipEntry(TestKit.Stand(Sk, "stand", 240), "stance", "none", ClipVerdict.Keep),
            new ClipEntry(TestKit.Walk(Sk, "walk", 200, 1.5f, 1.6f), "run", "left", ClipVerdict.Keep),
            new ClipEntry(TestKit.Walk(Sk, "walk2", 200, 0.7f, 1.2f, 1f), "run", "left", ClipVerdict.Keep),
        });
        return new PoseDatabase(lib, new[] { "stance", "run", "catch" });
    }

    static readonly PoseDatabase Shared = Db();

    sealed class Rig
    {
        public readonly KeeperMotion Motion = new(Shared);
        public readonly Quaternion[] ProcR = Enumerable.Repeat(Quaternion.Identity, Sk.Count).ToArray();
        public readonly Vector3[] ProcP = Sk.Offsets.ToArray();
        public readonly Quaternion[] OutR = new Quaternion[Sk.Count];
        public readonly Vector3[] OutP = new Vector3[Sk.Count];

        public void Step(Vector2 pos, float yaw, Vector2 vel, string? set)
        {
            ProcP[0] = new Vector3(pos.X, 0.95f, pos.Y);
            ProcR[0] = Spring.Yaw(yaw);
            var input = new MotionInput { Position = pos, Yaw = yaw, Velocity = vel, Set = set, Scale = 1f, Dt = Dt };
            Motion.Step(input, ProcR, ProcP, OutR, OutP);
        }

        public Vector3 Ankle(int i)
        {
            var gr = new Quaternion[Sk.Count]; var gp = new Vector3[Sk.Count];
            Sk.Forward(OutR, OutP[0], gr, gp, OutP);
            return gp[Sk.Foot[i]];
        }
    }

    static IEnumerable<(Vector2 Pos, Vector2 Vel, string? Set)> Script()
    {
        var p = new Vector2(3f, -1f);
        for (int i = 0; i < 480; i++)
        {
            string? set = i < 100 ? "stance" : i < 250 ? "run" : i < 350 ? null : "stance";
            var v = set == "run" ? new Vector2(1.4f, 0.1f) : Vector2.Zero;
            p += v * Dt;
            yield return (p, v, set);
        }
    }

    [Fact]
    public void TheSameInputsGiveTheSamePoses()
    {
        var a = new Rig(); var b = new Rig();
        foreach (var (pos, vel, set) in Script())
        {
            a.Step(pos, 0.3f, vel, set); b.Step(pos, 0.3f, vel, set);
            for (int k = 0; k < Sk.Count; k++) { Assert.Equal(a.OutR[k], b.OutR[k]); Assert.Equal(a.OutP[k], b.OutP[k]); }
        }
        Assert.True(a.Motion.SwitchCount >= 3);
    }

    [Fact]
    public void ATeleportMidClipResetsTheInertializer()
    {
        var a = new Rig();
        var pos = new Vector2(1f, 1f);
        for (int i = 0; i < 100; i++) a.Step(pos, 0f, Vector2.Zero, "stance");
        for (int i = 0; i < 3; i++) { pos += new Vector2(1.5f, 0) * Dt; a.Step(pos, 0f, new Vector2(1.5f, 0), "run"); }
        Assert.True(a.Motion.Blending);
        Assert.True(a.Motion.PlayingClip);
        a.Motion.Teleport();
        var far = new Vector2(-4f, 2f);
        a.Step(far, 1f, new Vector2(1.5f, 0), "run");
        Assert.False(a.Motion.Inertializer.Active);
        // exactly what a fresh motion shows on its first tick
        var fresh = new Rig();
        fresh.Step(far, 1f, new Vector2(1.5f, 0), "run");
        for (int k = 0; k < Sk.Count; k++) { Assert.Equal(fresh.OutR[k], a.OutR[k]); Assert.Equal(fresh.OutP[k], a.OutP[k]); }
    }

    [Fact]
    public void AnEmptySetShowsTheProceduralPose()
    {
        var a = new Rig();
        for (int i = 0; i < 60; i++)
        {
            a.Step(new Vector2(0.5f, 0.2f), 0.7f, Vector2.Zero, "catch");
            Assert.False(a.Motion.PlayingClip);
            for (int k = 0; k < Sk.Count; k++)
            {
                Assert.True(TestKit.Angle(a.OutR[k], a.ProcR[k]) < 1e-4f);
                Assert.True((a.OutP[k] - a.ProcP[k]).Length() < 1e-5f);
            }
        }
    }

    [Fact]
    public void LeavingTheClipsBlendsIntoTheProceduralPose()
    {
        var a = new Rig();
        var pos = new Vector2(0, 0);
        for (int i = 0; i < 60; i++) a.Step(pos, 0f, Vector2.Zero, "stance");
        var before = a.OutR.ToArray(); var hipsBefore = a.OutP[0];
        a.Step(pos, 0f, Vector2.Zero, null);
        Assert.True(a.Motion.Blending);
        Assert.True((a.OutP[0] - hipsBefore).Length() < 0.01f, "hips jumped at the switch");
        for (int k = 0; k < Sk.Count; k++) Assert.True(TestKit.Angle(a.OutR[k], before[k]) < 0.05f, $"bone {k} popped by {TestKit.Angle(a.OutR[k], before[k])}: {string.Join(" ", Enumerable.Range(0, Sk.Count).Select(j => TestKit.Angle(a.OutR[j], before[j]).ToString("F3")))}");
        for (int i = 0; i < 240; i++) a.Step(pos, 0f, Vector2.Zero, null);
        for (int k = 0; k < Sk.Count; k++) Assert.True(TestKit.Angle(a.OutR[k], a.ProcR[k]) < 1e-3f);
        Assert.True((a.OutP[0] - a.ProcP[0]).Length() < 1e-3f);
    }

    [Fact]
    public void ItSearchesEveryTwelveTicksAndAtOnceOnASetChange()
    {
        var a = new Rig();
        for (int i = 0; i < 120; i++) a.Step(Vector2.Zero, 0f, Vector2.Zero, "stance");
        Assert.Equal(10, a.Motion.SearchCount);   // ticks 0, 12, ..., 108
        a.Step(new Vector2(0.01f, 0), 0f, new Vector2(1.5f, 0), "run");   // tick 120 (due anyway)
        int n = a.Motion.SearchCount;
        a.Step(new Vector2(0.02f, 0), 0f, new Vector2(1.5f, 0), "stance");   // tick 121: not due, but the set changed
        Assert.Equal(n + 1, a.Motion.SearchCount);
        a.Step(new Vector2(0.02f, 0), 0f, Vector2.Zero, "stance");
        Assert.Equal(n + 1, a.Motion.SearchCount);
    }

    [Fact]
    public void PlantedFeetStayWhereTheyWerePlanted()
    {
        var a = new Rig();
        var pos = Vector2.Zero;
        for (int i = 0; i < 24; i++) a.Step(pos, 0f, Vector2.Zero, "stance");
        Assert.True(a.Motion.FootLock.Locked(0) && a.Motion.FootLock.Locked(1));
        var l0 = a.Ankle(0); var r0 = a.Ankle(1);
        // the simulation nudges him 6 cm sideways: the root follows, the planted feet do not
        for (int i = 0; i < 60; i++) { pos.X += 0.001f; a.Step(pos, 0f, new Vector2(0.12f, 0), "stance"); }
        Assert.True(a.OutP[0].X > 0.03f, "the body did not follow the simulation");
        Assert.True((a.Ankle(0) - l0).Length() < 1e-3f, $"left foot slid {a.Ankle(0) - l0} steps {a.Motion.FootLock.Steps}");
        Assert.True((a.Ankle(1) - r0).Length() < 1e-3f, $"right foot slid {(a.Ankle(1) - r0).Length()}");
    }

    [Fact]
    public void TheRootIsPulledToTheSimulationAndSnapsBeyondHalfAMetre()
    {
        var a = new Rig();
        for (int i = 0; i < 30; i++) a.Step(Vector2.Zero, 0f, Vector2.Zero, "catch");
        // procedural: the pose is the simulation's own, wherever the root is
        a.Step(new Vector2(0.3f, 0), 0f, Vector2.Zero, "catch");
        Assert.Equal(0.3f, a.OutP[0].X, 4);
        var b = new Rig();
        for (int i = 0; i < 30; i++) b.Step(Vector2.Zero, 0f, Vector2.Zero, "stance");
        float x0 = b.OutP[0].X;
        b.Step(new Vector2(0.3f, 0), 0f, Vector2.Zero, "stance");
        Assert.InRange(b.OutP[0].X - x0, 0.001f, 0.1f);   // pulled, not jumped
        for (int i = 0; i < 120; i++) b.Step(new Vector2(0.3f, 0), 0f, Vector2.Zero, "stance");
        Assert.InRange(b.OutP[0].X - x0, 0.28f, 0.32f);
        b.Step(new Vector2(1.2f, 0), 0f, Vector2.Zero, "stance");
        Assert.InRange(b.OutP[0].X - x0, 1.15f, 1.25f);   // 0.9 m away: snapped
    }

    /// <summary>A clip whose root walks off at 5 m/s while the simulation stands still, for the root's drift.</summary>
    static readonly PoseDatabase Drift = new(new ClipLibrary(Sk, new[]
    {
        new ClipEntry(TestKit.Walk(Sk, "drift", 600, 5f, 1.6f, loop: true), "drift", "left", ClipVerdict.Keep),
    }), new[] { "drift" });

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(60)]    // after a free root (a save) has carried it 1.25 m away
    public void AFollowingRootNeverJumpsWhileTheSimulationStandsStill(int freeTicks)
    {
        var m = new KeeperMotion(Drift);
        var procR = Enumerable.Repeat(Quaternion.Identity, Sk.Count).ToArray();
        var procP = Sk.Offsets.ToArray();
        var outR = new Quaternion[Sk.Count]; var outP = new Vector3[Sk.Count];
        Vector2? last = null;
        float most = 0f, off = 0f;
        for (int i = 0; i < 360; i++)
        {
            var input = new MotionInput { Set = "drift", Scale = 1f, Dt = Dt, Root = i < freeTicks ? RootMode.Free : RootMode.Follow };
            m.Step(input, procR, procP, outR, outP);
            var root = m.RootPosition;
            if (last is Vector2 l && i != freeTicks) most = MathF.Max(most, (root - l).Length());
            last = root;
            if (i >= freeTicks + 120) off = MathF.Max(off, root.Length());
        }
        // (the clip's own step is 5 m/s / 120 Hz = 0.042 m: anything much more is a jump he never made)
        Assert.True(most < 0.06f, $"the drawn root moved {most:F3} m in one tick");
        Assert.True(off <= m.SnapDistance + 1e-3f, $"it drifted {off:F3} m from him");
    }
}
