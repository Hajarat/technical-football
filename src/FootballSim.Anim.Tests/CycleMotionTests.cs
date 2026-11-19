using System.Numerics;
using FootballSim.Anim;

namespace FootballSim.Anim.Tests;

/// <summary>The attacker's stride cycles (game/assets/anim/attacker/cycles): phase map, swinging foot, loop seam, speed blend, planted feet.</summary>
public class CycleMotionTests
{
    static (Skeleton Sk, List<StrideCycle> Cycles, List<StrideCycle> Mirrors) Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "game", "assets", "anim", "attacker", "cycles", "clips.json"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var folder = Path.Combine(dir!.FullName, "game", "assets", "anim", "attacker", "cycles");
        return StrideCycle.Load(File.ReadAllText(Path.Combine(folder, "clips.json")), f => File.ReadAllBytes(Path.Combine(folder, f)), mirror: true);
    }

    [Fact]
    public void FourCyclesInOrderOfSpeedWithTheFullMap()
    {
        var (_, cycles, _) = Load();
        Assert.Equal(new[] { "cycle_walk", "cycle_jog", "cycle_run", "cycle_sprint" }, cycles.Select(c => c.Clip.Name));
        Assert.All(cycles, c => Assert.True(c.FullMap, c.Clip.Name));
        Assert.InRange(cycles[3].Speed, 5.1f, 5.3f);
        Assert.InRange(cycles[3].StepLength, 1.6f, 1.75f);
    }

    [Fact]
    public void PhaseMapIsMonotoneContinuousAndInvertible()
    {
        var (_, cycles, _) = Load();
        foreach (var c in cycles)
        {
            int n = c.Clip.FrameCount;
            float prev = c.ClipFrame(0f), total = 0f;
            for (int i = 1; i <= 2000; i++)
            {
                float f = c.ClipFrame(i * 0.001f);
                float d = f - prev; if (d < -n / 2f) d += n;
                Assert.True(d > 0f && d < 0.2f, $"{c.Clip.Name} at {i * 0.001f}: step {d}");
                total += d; prev = f;
            }
            Assert.Equal(n, total, 2);   // one stride of the clock is one loop of the clip
            for (float s = 0f; s < 2f; s += 0.05f)
            {
                float back = c.PhaseOf(c.ClipFrame(s));
                Assert.True(MathF.Abs(Spring.WrapPi((back - s) * MathF.PI)) / MathF.PI < 1e-3f, $"{c.Clip.Name} {s} -> {back}");
            }
        }
    }

    [Fact]
    public void TheDrawnSwingingFootIsTheClocksSwingingFoot()
    {
        var (_, cycles, mirrors) = Load();
        foreach (var c in cycles.Concat(mirrors))
        {
            // around each swing middle the swinging foot is in the air and the other on the ground, in the clip's own contacts
            for (float s = 0.3f; s <= 0.7f; s += 0.02f)
            {
                byte rc = c.Clip.Contacts((int)MathF.Round(c.ClipFrame(s)) % c.Clip.FrameCount);
                Assert.True((rc & Clip.RightFoot) == 0 && (rc & Clip.LeftFoot) != 0, $"{c.Clip.Name} s={s}");
                byte lc = c.Clip.Contacts((int)MathF.Round(c.ClipFrame(s + 1f)) % c.Clip.FrameCount);
                Assert.True((lc & Clip.LeftFoot) == 0 && (lc & Clip.RightFoot) != 0, $"{c.Clip.Name} s={s + 1f}");
            }
            // the stance window agrees: the left foot is down at the right's swing middle, the right not
            Assert.True(CycleMotion.InStance(0, 0.5f, c.StanceShare) && !CycleMotion.InStance(1, 0.5f, c.StanceShare));
            Assert.True(CycleMotion.InStance(1, 1.5f, c.StanceShare) && !CycleMotion.InStance(0, 1.5f, c.StanceShare));
        }
    }

    [Fact]
    public void LoopSeamDoesNotJump()
    {
        var (sk, cycles, _) = Load();
        var a = new Quaternion[sk.Count]; var b = new Quaternion[sk.Count];
        foreach (var c in cycles)
        {
            var m = new CycleMotion(sk, new[] { c });
            m.Step(0f, c.BlendSpeed, 1f / 120f, a);
            // the largest turn of any bone over one tick across the clip's own end and start, against the largest anywhere else
            float seam = 0f, inner = 0f;
            float fSeam = c.Clip.FrameCount - 0.5f;
            float sSeam = c.PhaseOf(fSeam);
            for (float s = 0f; s < 2f; s += 0.01f)
            {
                m.Sample(s, a); m.Sample(s + 0.01f, b);
                float d = 0f;
                for (int k = 0; k < sk.Count; k++) d = Math.Max(d, TestKit.Angle(a[k], b[k]));
                bool nearSeam = MathF.Abs(Spring.WrapPi((s + 0.005f - sSeam) * MathF.PI)) / MathF.PI < 0.03f;
                if (nearSeam) seam = Math.Max(seam, d); else inner = Math.Max(inner, d);
            }
            Assert.True(seam <= inner * 1.25f + 0.01f, $"{c.Clip.Name}: seam {seam * 57.3f:F1} deg per 0.01 phase, elsewhere {inner * 57.3f:F1}");
        }
    }

    [Fact]
    public void SpeedPicksTheNeighbouringCycles()
    {
        var (_, cycles, _) = Load();
        var w = new float[cycles.Count];
        CycleMotion.TargetWeights(cycles, 1.0f, w); Assert.Equal(new[] { 1f, 0f, 0f, 0f }, w);
        CycleMotion.TargetWeights(cycles, 2.6f, w); Assert.True(w[1] > 0.7f && Math.Abs(w.Sum() - 1f) < 1e-5f && w[0] > 0f);
        CycleMotion.TargetWeights(cycles, 4.85f, w); Assert.Equal(new[] { 0f, 0f, 0f, 1f }, w);
        CycleMotion.TargetWeights(cycles, 7.36f, w); Assert.Equal(new[] { 0f, 0f, 0f, 1f }, w);
    }

    [Fact]
    public void WeightsEaseInTime()
    {
        var (sk, cycles, _) = Load();
        var m = new CycleMotion(sk, cycles);
        var pose = new Quaternion[sk.Count];
        m.Step(0f, 1.8f, 1f / 120f, pose);
        Assert.Equal(1f, m.Weights[0], 4);
        m.Step(0.01f, 6f, 1f / 120f, pose);
        Assert.True(m.Weights[0] > 0.9f && m.Weights[3] > 0f && m.Weights[3] < 0.1f);
        float sum = 0f; foreach (var x in m.Weights) sum += x;
        Assert.Equal(1f, sum, 4);
        for (int i = 0; i < 240; i++) m.Step(0.01f * i, 6f, 1f / 120f, pose);
        Assert.True(m.Weights[3] > 0.99f);
    }

    /// <summary>
    /// Runs the blend at a simulation speed: the clock at 2.4 + 0.17 v steps per second, the root moving at v, the feet's fore-aft
    /// travel shortened by <see cref="CycleMotion.Stretch"/> (a slower body) or the stance window shortened (a faster one). A foot on the ground then (nearly) stands still in the world, before any lock.
    /// </summary>
    [Theory]
    [InlineData(1.8f)]
    [InlineData(2.57f)]
    [InlineData(2.88f)]
    [InlineData(4.45f)]
    [InlineData(4.85f)]
    [InlineData(7.36f)]
    public void StretchedStrideLeavesAPlantedFootNearlyStill(float v)
    {
        var (sk, cycles, _) = Load();
        var m = new CycleMotion(sk, cycles);
        var pose = new Quaternion[sk.Count]; var gr = new Quaternion[sk.Count]; var gp = new Vector3[sk.Count];
        float dt = 1f / 120f, f = 2.4f + 0.17f * v, s = 0f;
        var start = new Vector3[2]; var was = new bool[2]; float worst = 0f;
        for (int t = 0; t < 600; t++)
        {
            var hips = m.Step(s, v, dt, pose, v / f, 1f);
            sk.Forward(pose, hips, gr, gp);
            float scale = m.Stretch(v / f, 1f);
            for (int i = 0; i < 2; i++)
            {
                var rel = gp[sk.Foot[i]] - hips;
                var world = new Vector3(rel.X, 0, rel.Z * scale) + new Vector3(hips.X, 0, hips.Z + v * t * dt);
                float pr = m.StanceProgress(i, s);
                bool st = pr >= 0.1f && pr <= 0.6f;   // from the foot flat to the heel rising (the ankle rolls forward over the toes after)
                if (st && !was[i]) start[i] = world;
                if (st && t > 240) worst = Math.Max(worst, (world - start[i]).Length());
                was[i] = st;
            }
            s = (s + f * dt) % 2f;
        }
        // a step is 0.9 to 2 m: the cycle's own stance foot moves a few centimetres (heel to toe roll); the lock takes the rest out
        Assert.True(worst < 0.12f, $"v={v}: a planted foot moves {worst:F3} m");
    }

    [Fact]
    public void PlantLockHoldsThenLetsGoSmoothly()
    {
        var lk = new PlantLock();
        Vector3 W(int t) => new(1 + 0.02f * t, 0.1f + 0.002f * t, 0);
        var first = lk.Apply(true, W(0), false, 0.01f);
        Vector3 p = first;
        for (int t = 1; t < 30; t++)
        {
            var q = lk.Apply(true, W(t), false, 0.01f);
            if (lk.Settled) { Assert.Equal(p.X, q.X); Assert.Equal(p.Z, q.Z); }
            else Assert.True(q.X - p.X <= 0.02f + 1e-6f && q.X >= p.X);   // landing: the motion fades out
            Assert.Equal(W(t).Y, q.Y);   // the height is the pose's
            p = q;
        }
        Assert.True(lk.Settled);
        Assert.InRange(p.X - first.X, 0.01f, 0.06f);
        var held = p;
        var rel = lk.Apply(false, new Vector3(1.62f, 0.15f, 0), false, 0.01f);
        Assert.True((rel - held).Length() < 0.06f);   // no jump to where the pose has it
        Vector3 last = rel;
        for (int t = 0; t < 40; t++) last = lk.Apply(false, new Vector3(1.62f + 0.02f * t, 0.15f, 0), false, 0.01f);
        Assert.True(MathF.Abs(last.X - (1.62f + 0.02f * 39)) < 0.002f);
        // a held foot the body has left behind is let go at once, and not held again in the same stance
        lk.Apply(true, new Vector3(0, 0.1f, 0), false, 0.01f);
        Assert.True(lk.Locked);
        lk.Apply(true, new Vector3(0.5f, 0.1f, 0), true, 0.01f);
        Assert.False(lk.Locked);
        lk.Apply(true, new Vector3(0.6f, 0.1f, 0), false, 0.01f);
        Assert.False(lk.Locked);
        lk.Apply(false, new Vector3(0.7f, 0.1f, 0), false, 0.01f);
        lk.Apply(true, new Vector3(0.8f, 0.1f, 0), false, 0.01f);
        Assert.True(lk.Locked);
    }

    [Fact]
    public void MirroredCycleSwapsTheFeet()
    {
        var (_, cycles, mirrors) = Load();
        for (int i = 0; i < cycles.Count; i++)
        {
            Assert.Equal(cycles[i].Clip.Events["strike_l"], mirrors[i].Clip.Events["strike_r"]);
            Assert.Equal(cycles[i].Clip.Events["toeoff_r"], mirrors[i].Clip.Events["toeoff_l"]);
            Assert.Equal(cycles[i].StanceShare, mirrors[i].StanceShare, 5);
        }
    }
}
