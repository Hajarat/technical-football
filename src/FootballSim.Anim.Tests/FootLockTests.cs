using System.Numerics;
using FootballSim.Anim;

namespace FootballSim.Anim.Tests;

/// <summary>Foot locking: a planted foot holds while the hips move, a released foot glides back to the clip without a jump, a leg
/// stretched too far lets go, and no joint ends below the ground.</summary>
public class FootLockTests
{
    const float Dt = 1f / 120f;
    static readonly Skeleton Sk = TestKit.Legs();

    static Quaternion[] Bent()
    {
        var r = Enumerable.Repeat(Quaternion.Identity, Sk.Count).ToArray();
        // a crouch: knees well bent (forward, +Z), so the legs work away from full reach
        r[Sk.Leg[0]] = r[Sk.Leg[1]] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.8f);
        r[Sk.UpLeg[0]] = r[Sk.UpLeg[1]] = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.4f);
        return r;
    }

    static Vector3 Ankle(Quaternion[] local, Vector3 hips, int i)
    {
        var gr = new Quaternion[Sk.Count]; var gp = new Vector3[Sk.Count];
        Sk.Forward(local, hips, gr, gp);
        return gp[Sk.Foot[i]];
    }

    [Fact]
    public void APlantedFootHoldsAndAReleasedOneGlidesBack()
    {
        var fl = new FootLock();
        var hips = new Vector3(0, 0.90f, 0);
        var r = Bent();
        fl.Apply(Sk, r, ref hips, default, Clip.LeftFoot, Dt, false);
        var planted = Ankle(r, hips, 0);
        Vector3 last = planted;
        for (int i = 1; i <= 30; i++)
        {
            var h = hips + new Vector3(0.002f * i, 0, 0.001f * i);
            r = Bent();
            fl.Apply(Sk, r, ref h, default, Clip.LeftFoot, Dt, false);
            last = Ankle(r, h, 0);
            Assert.True((last - planted).Length() < 1e-3f, $"tick {i}: slid {(last - planted).Length()}");
        }
        // contact ends: the foot starts where it was held and moves smoothly to where the clip has it
        var hp = hips + new Vector3(0.06f, 0, 0.03f);
        var free = Ankle(Bent(), hp, 0);
        float prevGap = (last - free).Length();
        for (int i = 0; i < 60; i++)
        {
            var h = hp; r = Bent();
            fl.Apply(Sk, r, ref h, default, 0, Dt, false);
            var a = Ankle(r, h, 0);
            Assert.True((a - last).Length() < 0.01f, $"tick {i}: jumped {(a - last).Length()}");
            float gap = (a - free).Length();
            Assert.True(gap <= prevGap + 1e-5f, $"tick {i}: gap {gap} after {prevGap}");
            last = a; prevGap = gap;
        }
        Assert.True(prevGap < 1e-3f);
    }

    [Fact]
    public void AFootLeftBehindStepsInsteadOfSliding()
    {
        var fl = new FootLock();
        var hips = new Vector3(0, 0.90f, 0);
        var r = Bent();
        fl.Apply(Sk, r, ref hips, default, Clip.RightFoot, Dt, false);
        Assert.True(fl.Locked(1));
        var planted = Ankle(r, hips, 1);
        // the body moves away 1 cm per tick while the clip keeps the foot in contact
        bool stepped = false;
        float maxLift = 0;
        for (int i = 1; i <= 60; i++)
        {
            var h = hips + new Vector3(0.01f * i, 0, 0);
            r = Bent();
            fl.Apply(Sk, r, ref h, default, Clip.RightFoot, Dt, false);
            var a = Ankle(r, h, 1);
            if (fl.Locked(1) && !stepped) Assert.True((a - planted).Length() < 1e-3f, $"tick {i}: a locked foot slid");
            if (fl.Stepping(1)) { stepped = true; maxLift = Math.Max(maxLift, a.Y - planted.Y); }
        }
        Assert.True(stepped && fl.Steps >= 2, $"steps {fl.Steps}");
        Assert.True(maxLift > 0.04f, $"the step lifted only {maxLift}");
    }

    [Fact]
    public void NoJointEndsBelowTheGround()
    {
        var fl = new FootLock();
        var hips = new Vector3(0, 0.80f, 0);   // too low: the straight legs' toes would be under the ground
        var r = Enumerable.Repeat(Quaternion.Identity, Sk.Count).ToArray();
        fl.Apply(Sk, r, ref hips, default, 0, Dt, true);
        var gr = new Quaternion[Sk.Count]; var gp = new Vector3[Sk.Count];
        Sk.Forward(r, hips, gr, gp);
        float low = gp.Min(p => p.Y);
        Assert.InRange(low, fl.Clearance - 1e-5f, fl.Clearance + 1e-5f);
    }
}
