using System.Numerics;

namespace FootballSim.Anim;

/// <summary>
/// Left/right mirroring of a clip across the body's sagittal plane (model x = 0), done once at load so one recording serves both
/// sides. Each bone takes the turn from rest of its opposite bone, reflected: with D = G · R⁻¹ the global turn of a bone from its rest
/// global rotation R, the mirrored bone gets G' = M(D_opposite) · R, where M reflects a rotation across x = 0 ((x, y, z, w) →
/// (x, −y, −z, w)). Local rotations follow from the parent chain; the hips' x is negated; foot contacts swap; events stay.
/// Mirroring a mirrored clip gives the original back.
/// </summary>
public static class Mirror
{
    /// <summary>A rotation reflected across the plane x = 0.</summary>
    public static Quaternion Reflect(Quaternion q) => new(q.X, -q.Y, -q.Z, q.W);

    public static Vector3 Reflect(Vector3 v) => new(-v.X, v.Y, v.Z);

    /// <summary>The mirrored clip, named <paramref name="clip"/>'s name plus "_mirror".</summary>
    public static Clip Apply(Clip clip, Skeleton sk)
    {
        int n = clip.FrameCount, b = clip.BoneCount;
        if (b != sk.Count) throw new ArgumentException("clip and skeleton differ in bone count");
        var hips = new Vector3[n];
        var rot = new Quaternion[n * b];
        var g = new Quaternion[b];
        var gm = new Quaternion[b];
        var restInv = new Quaternion[b];
        for (int k = 0; k < b; k++) restInv[k] = Quaternion.Inverse(sk.RestGlobal[k]);
        var contacts = new byte[n];
        for (int f = 0; f < n; f++)
        {
            var local = clip.Rotations(f);
            for (int k = 0; k < b; k++) g[k] = sk.Parents[k] < 0 ? local[k] : Quaternion.Normalize(g[sk.Parents[k]] * local[k]);
            for (int k = 0; k < b; k++)
            {
                int o = sk.MirrorOf[k];
                gm[k] = Quaternion.Normalize(Reflect(g[o] * restInv[o]) * sk.RestGlobal[k]);
            }
            for (int k = 0; k < b; k++)
            {
                int p = sk.Parents[k];
                var l = p < 0 ? gm[k] : Quaternion.Inverse(gm[p]) * gm[k];
                l = Quaternion.Normalize(l);
                if (l.W < 0) l = Quaternion.Negate(l);
                rot[f * b + k] = l;
            }
            hips[f] = Reflect(clip.Hips(f));
            byte c = clip.Contacts(f);
            contacts[f] = (byte)(((c & Clip.LeftFoot) != 0 ? Clip.RightFoot : 0) | ((c & Clip.RightFoot) != 0 ? Clip.LeftFoot : 0));
        }
        string name = clip.Name.EndsWith("_mirror") ? clip.Name[..^"_mirror".Length] : clip.Name + "_mirror";
        return new Clip(name, clip.Fps, clip.Loop, clip.Bones, hips, rot, clip.Events, contacts);
    }

    /// <summary>The side a mirrored clip moves to.</summary>
    public static string Side(string side) => side switch { "left" => "right", "right" => "left", _ => side };
}
