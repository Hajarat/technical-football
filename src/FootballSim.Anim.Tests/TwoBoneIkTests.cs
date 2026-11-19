using System.Numerics;
using FootballSim.Anim;

namespace FootballSim.Anim.Tests;

/// <summary>Two-bone IK: a reachable target is met within 1 mm, an unreachable one gives a straight limb pointing at it, the middle
/// joint bends toward the pole, and bending an existing chain keeps its bone lengths.</summary>
public class TwoBoneIkTests
{
    const float L1 = 0.45f, L2 = 0.43f;

    [Fact]
    public void ReachableTargetsAreMetWithinAMillimetre()
    {
        var rng = new Random(11);
        var root = new Vector3(0.1f, 0.9f, 0);
        for (int i = 0; i < 2000; i++)
        {
            var dir = Vector3.Normalize(new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f));
            float r = 0.05f + (float)rng.NextDouble() * (L1 + L2 - 0.051f);
            var target = root + dir * r;
            TwoBoneIk.Solve(root, L1, L2, target, new Vector3(0, 0, 1), out var mid, out var end);
            Assert.True((end - target).Length() < 1e-3f, $"missed by {(end - target).Length()} at r={r}");
            Assert.InRange((mid - root).Length(), L1 - 1e-4f, L1 + 1e-4f);
            Assert.InRange((end - mid).Length(), L2 - 1e-4f, L2 + 1e-4f);
        }
    }

    [Fact]
    public void AnUnreachableTargetGivesAStraightLimbPointingAtIt()
    {
        var root = Vector3.Zero;
        var target = new Vector3(1.5f, -0.4f, 0.3f);
        TwoBoneIk.Solve(root, L1, L2, target, new Vector3(0, 0, 1), out var mid, out var end);
        var dn = Vector3.Normalize(target);
        Assert.True((end - dn * (L1 + L2)).Length() < 1e-4f);
        var off = mid - dn * Vector3.Dot(mid, dn);
        Assert.True(off.Length() < 2e-3f, $"middle joint {off.Length()} m off the line");
    }

    [Fact]
    public void TheMiddleJointBendsTowardThePole()
    {
        var root = new Vector3(0, 1, 0);
        var target = new Vector3(0, 0.3f, 0.1f);
        foreach (var pole in new[] { new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(-0.6f, 0.2f, -0.7f) })
        {
            TwoBoneIk.Solve(root, L1, L2, target, pole, out var mid, out _);
            var d = Vector3.Normalize(target - root);
            var bend = mid - root - d * Vector3.Dot(mid - root, d);
            Assert.True(Vector3.Dot(bend, pole) > 0, "bends away from the pole");
            var normal = Vector3.Normalize(Vector3.Cross(d, pole - d * Vector3.Dot(pole, d)));
            Assert.InRange(Vector3.Dot(bend, normal), -1e-4f, 1e-4f);   // in the plane of the limb and the pole
        }
    }

    [Fact]
    public void BendingAChainToItsOwnEndChangesNothing()
    {
        var a = new Vector3(0.1f, 0.95f, 0);
        foreach (float bend in new[] { 0.6f, 0.05f, 0.01f })
        {
            var ga = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.2f);
            var gb = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -bend) * ga;
            var b = a + Vector3.Transform(new Vector3(0, -L1, 0), ga);
            var c = b + Vector3.Transform(new Vector3(0, -L2, 0), gb);
            var ac = Vector3.Normalize(c - a);
            var pole = Vector3.Normalize((b - a) - ac * Vector3.Dot(b - a, ac));   // the side the knee is on
            TwoBoneIk.Apply(a, b, c, ga, gb, c, pole, out var ga2, out var gb2, out var reached);
            Assert.True(TestKit.Angle(ga, ga2) < 1e-3f && TestKit.Angle(gb, gb2) < 1e-3f, $"bend {bend}");
            Assert.True((reached - c).Length() < 1e-4f);
        }
    }

    [Fact]
    public void BendingAChainMovesItsEndAndKeepsItsBones()
    {
        var a = new Vector3(0.1f, 0.95f, 0);
        var ga = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.2f);
        var gb = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.3f) * ga;
        var off1 = new Vector3(0, -L1, 0); var off2 = new Vector3(0, -L2, 0);
        var b = a + Vector3.Transform(off1, ga);
        var c = b + Vector3.Transform(off2, gb);
        var target = c + new Vector3(0.05f, 0.08f, -0.06f);
        var acn = Vector3.Normalize(c - a);
        var pole = Vector3.Normalize((b - a) - acn * Vector3.Dot(b - a, acn));
        TwoBoneIk.Apply(a, b, c, ga, gb, target, pole, out var ga2, out var gb2);
        var b2 = a + Vector3.Transform(off1, ga2);
        var c2 = b2 + Vector3.Transform(off2, gb2);
        Assert.True((c2 - target).Length() < 1e-3f, $"end {(c2 - target).Length()} m from the target");
        // the knee stays on the side it was on
        var dn = Vector3.Normalize(c - a);
        var side0 = b - a - dn * Vector3.Dot(b - a, dn);
        var dn2 = Vector3.Normalize(c2 - a);
        var side1 = b2 - a - dn2 * Vector3.Dot(b2 - a, dn2);
        Assert.True(Vector3.Dot(side0, side1) > 0);
    }
}
