using System.Numerics;
using FootballSim.Anim;

namespace FootballSim.Anim.Tests;

/// <summary>
/// Saves on the committed keeper clips: the contact lands on the simulation's tick, a late save hands over to the IK at once, a hybrid
/// dive plays its take-off then its baked flight (straight to the flight when there is no time), the gloves meet a reachable target,
/// a teleport mid-save shows the new pose with no blend, and the heading of a lying body.
/// </summary>
public class KeeperSaveTests
{
    const float Dt = 1f / 120f;
    static readonly PoseDatabase Db = new(TestKit.Keeper, KeeperChooser.Sets, KeeperChooser.WeightsFor, KeeperChooser.OptionsFor);

    sealed class Rig
    {
        public readonly KeeperMotion Motion = new(Db);
        readonly Skeleton _sk = Db.Skeleton;
        public readonly Quaternion[] ProcR, OutR;
        public readonly Vector3[] ProcP, OutP;
        public Rig()
        {
            int n = _sk.Count;
            ProcR = _sk.RestLocal.ToArray(); ProcP = _sk.Offsets.ToArray(); OutR = new Quaternion[n]; OutP = new Vector3[n];
            ProcP[0] = new Vector3(0, 0.95f, 0);
        }
        public void Step(MotionInput input)
        {
            input.Scale = 1f; input.Dt = Dt;
            Motion.Step(input, ProcR, ProcP, OutR, OutP);
        }
        public Vector3 GlovesMid()
        {
            var gr = new Quaternion[_sk.Count]; var gp = new Vector3[_sk.Count];
            _sk.Forward(OutR, OutP[0], gr, gp, OutP);
            return Limbs.Point(_sk, gp, ContactLimb.Hands);
        }
        public void Stand(int ticks) { for (int i = 0; i < ticks; i++) Step(new MotionInput { Set = KeeperChooser.StanceIdle }); }
    }

    static MotionInput Save(string set, float timeLeft, Vector3 point, int side = 0) => new()
    {
        Set = set, Chain = KeeperChooser.IsChain(set), HasTarget = true, TargetTime = timeLeft, HasSavePoint = true, SavePoint = point,
        Side = side, Root = RootMode.Free,
    };

    [Fact]
    public void TheCatchesContactFrameLandsOnTheSimulatedContactTick()
    {
        var rig = new Rig();
        rig.Stand(60);
        const float contact = 0.4f;
        var point = new Vector3(0, 1.2f, 0.45f);
        int contactTick = (int)MathF.Round(contact / Dt), landed = -1;
        for (int i = 0; i <= contactTick + 10; i++)
        {
            rig.Step(Save(KeeperChooser.Catch, contact - i * Dt, point));
            var ps = Db.Set(KeeperChooser.Catch)!;
            int c = ps.Clips[ps.IndexOf(rig.Motion.ClipName!)].TargetFrame;
            if (landed < 0 && rig.Motion.ClipFrame >= c) landed = i;
        }
        Assert.InRange(landed, contactTick - 3, contactTick + 3);
    }

    [Fact]
    public void ALateSaveClampsTheRateAndHandsOverToTheIk()
    {
        var rig = new Rig();
        rig.Stand(60);
        rig.Step(Save(KeeperChooser.ReflexHand, -0.05f, new Vector3(-1.0f, 1.1f, 0.3f), side: 1));
        Assert.True(rig.Motion.PlayingClip);
        Assert.True(rig.Motion.Late);
        rig.Step(Save(KeeperChooser.ReflexHand, 0.004f, new Vector3(-1.0f, 1.1f, 0.3f), side: 1));
        Assert.True(rig.Motion.Late);
    }

    [Fact]
    public void AHybridDiveTakesOffOnCmuThenFliesOnTheBakedClip()
    {
        var rig = new Rig();
        rig.Stand(60);
        var point = new Vector3(-2.0f, 1.0f, 0.3f);   // model -X: his right
        var seen = new List<string?>();
        for (int i = 0; i < 70; i++)
        {
            rig.Step(Save(KeeperChooser.DiveSide, 0.55f - i * Dt, point, side: 1));
            if (seen.Count == 0 || seen[^1] != rig.Motion.SetName) seen.Add(rig.Motion.SetName);
        }
        Assert.Equal(new[] { KeeperChooser.Takeoff(KeeperChooser.DiveSide), KeeperChooser.Flight(KeeperChooser.DiveSide) }, seen);
        Assert.StartsWith("dive_side_flight_baked", rig.Motion.ClipName);

        // no time for a take-off: straight into the flight
        var late = new Rig();
        late.Stand(60);
        late.Step(Save(KeeperChooser.DiveSide, 0.2f, point, side: 1));
        Assert.Equal(KeeperChooser.Flight(KeeperChooser.DiveSide), late.Motion.SetName);
    }

    [Fact]
    public void TheGlovesMeetAReachableTarget()
    {
        var rig = new Rig();
        rig.Stand(60);
        var target = rig.GlovesMid() + new Vector3(0.1f, 0.15f, 0.15f);
        for (int i = 0; i < 60; i++)
        {
            var input = new MotionInput { Set = KeeperChooser.StanceIdle };
            input.Hands = new LimbTargets { Left = target, LeftWeight = 1f, RightWeight = 1f, Pair = true, Spread = 0.09f };
            rig.Step(input);
        }
        Assert.True((rig.GlovesMid() - target).Length() < 0.01f, $"off by {(rig.GlovesMid() - target).Length():F3} m");
    }

    [Fact]
    public void ATeleportMidSaveShowsTheNewPoseWithNoBlend()
    {
        var rig = new Rig();
        rig.Stand(60);
        for (int i = 0; i < 20; i++) rig.Step(Save(KeeperChooser.DiveSide, 0.55f - i * Dt, new Vector3(-2f, 1f, 0.3f), side: 1));
        Assert.True(rig.Motion.PlayingClip);
        rig.Motion.Teleport();
        rig.Step(new MotionInput { Set = KeeperChooser.StanceIdle, Position = new Vector2(3f, -2f) });
        Assert.False(rig.Motion.Inertializer.Active);
        var fresh = new Rig();
        fresh.Step(new MotionInput { Set = KeeperChooser.StanceIdle, Position = new Vector2(3f, -2f) });
        for (int k = 0; k < fresh.OutR.Length; k++) Assert.Equal(fresh.OutR[k], rig.OutR[k]);
    }

    [Fact]
    public void ALyingBodyFacesTheWayHeWillRise()
    {
        // face down, head toward +X: he faces +X (he pushes up that way); on his back, head toward -X: he faces +X (toward his feet)
        var turnX = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var h = Features.Heading(turnX * Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2));
        Assert.True(Vector2.Dot(Vector2.Normalize(h), Vector2.UnitX) > 0.99f, $"{h}");
        h = Features.Heading(turnX * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2));
        Assert.True(Vector2.Dot(Vector2.Normalize(h), Vector2.UnitX) > 0.99f, $"{h}");
        h = Features.Heading(Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f));
        Assert.True(Vector2.Dot(Vector2.Normalize(h), new Vector2(MathF.Sin(0.4f), MathF.Cos(0.4f))) > 0.999f);
    }
}
