using System.Numerics;
using FootballSim.Anim;

namespace FootballSim.Anim.Tests;

/// <summary>Mirroring: the mirror of a mirror is the clip, the mirrored left foot is the reflected right foot, contacts swap, and a
/// keeper clip that moves to his right moves to his left once mirrored.</summary>
public class MirrorTests
{
    [Fact]
    public void MirroringTwiceGivesTheClipBack()
    {
        var lib = TestKit.Keeper;
        var clip = lib.Clips.First(c => c.Clip.Name == "stance_shuffle_83_01").Clip;
        var back = Mirror.Apply(Mirror.Apply(clip, lib.Skeleton), lib.Skeleton);
        Assert.Equal(clip.Name, back.Name);
        for (int f = 0; f < clip.FrameCount; f += 5)
        {
            Assert.True((back.Hips(f) - clip.Hips(f)).Length() < 1e-5f);
            Assert.Equal(clip.Contacts(f), back.Contacts(f));
            var a = clip.Rotations(f); var b = back.Rotations(f);
            for (int k = 0; k < clip.BoneCount; k++) Assert.True(TestKit.Angle(a[k], b[k]) < 1e-3f, $"bone {k} frame {f}");
        }
    }

    [Fact]
    public void TheMirroredLeftFootIsTheReflectedRightFoot()
    {
        var sk = TestKit.Legs();
        var clip = TestKit.Walk(sk, "walk", 90, 1.2f, 1.7f);
        var m = Mirror.Apply(clip, sk);
        var gr = new Quaternion[sk.Count]; var gp = new Vector3[sk.Count];
        var mr = new Quaternion[sk.Count]; var mp = new Vector3[sk.Count];
        for (int f = 0; f < clip.FrameCount; f++)
        {
            sk.Forward(clip.Rotations(f), clip.Hips(f), gr, gp);
            sk.Forward(m.Rotations(f), m.Hips(f), mr, mp);
            for (int k = 0; k < sk.Count; k++)
                Assert.True((mp[k] - Mirror.Reflect(gp[sk.MirrorOf[k]])).Length() < 1e-5f, $"bone {k} frame {f}");
            byte c = clip.Contacts(f), mc = m.Contacts(f);
            Assert.Equal((c & Clip.LeftFoot) != 0, (mc & Clip.RightFoot) != 0);
            Assert.Equal((c & Clip.RightFoot) != 0, (mc & Clip.LeftFoot) != 0);
        }
    }

    [Fact]
    public void AKeeperClipMovingRightMovesLeftWhenMirrored()
    {
        var lib = TestKit.Keeper;
        var e = lib.Clips.First(c => c.Clip.Name == "stance_shuffle_83_13");
        Assert.Equal("right", e.Side);
        var m = Mirror.Apply(e.Clip, lib.Skeleton);
        var r = new RootTrack(e.Clip, lib.Skeleton);
        var rm = new RootTrack(m, lib.Skeleton);
        int last = e.Clip.FrameCount - 1;
        // his right is the model's -X (and his facing stays +Z)
        Vector2 move = Features.ToCharacter(r.Position(last) - r.Position(0), r.Yaw(0));
        Vector2 moveM = Features.ToCharacter(rm.Position(last) - rm.Position(0), rm.Yaw(0));
        Assert.True(move.X < -0.3f, $"original moves {move}");
        Assert.True(moveM.X > 0.3f, $"mirror moves {moveM}");
        // the real skeleton is close to symmetric: feet mirror within 2 cm
        var sk = lib.Skeleton;
        var gr = new Quaternion[sk.Count]; var gp = new Vector3[sk.Count];
        var mr = new Quaternion[sk.Count]; var mp = new Vector3[sk.Count];
        for (int f = 0; f <= last; f += 10)
        {
            sk.Forward(e.Clip.Rotations(f), e.Clip.Hips(f), gr, gp);
            sk.Forward(m.Rotations(f), m.Hips(f), mr, mp);
            for (int i = 0; i < 2; i++)
                Assert.True((mp[sk.Foot[i]] - Mirror.Reflect(gp[sk.Foot[1 - i]])).Length() < 0.02f, $"foot {i} frame {f}");
        }
    }
}
