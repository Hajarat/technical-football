using System;
using System.Collections.Generic;
using Godot;

namespace FootballSim.Game;

/// <summary>
/// The joint positions and orientations <see cref="PlayerFigure"/> computes every physics tick (world space, metres; index 0 = the character's left,
/// 1 = right). Read-only for consumers: the figure owns and refreshes the instance.
/// </summary>
public sealed class FigureJoints
{
    /// <summary>Pelvis joint (the figure's root).</summary>
    public Vector3 Pelvis;
    /// <summary>Trunk orientation: X = forward, Y = up, Z = the character's right. Pelvis, spine, neck and head share it.</summary>
    public Basis Torso;
    /// <summary>Figure height in metres and the ankle joint's height above the sole.</summary>
    public float Height, AnkleHeight;
    public readonly Vector3[] Hip = new Vector3[2], Knee = new Vector3[2], Ankle = new Vector3[2], FootDir = new Vector3[2];
    public readonly Vector3[] Shoulder = new Vector3[2], Elbow = new Vector3[2], Hand = new Vector3[2];
}

/// <summary>
/// A rigged human body (MPFB2 / Mixamo-compatible skeleton, see game/assets/characters/SOURCES.md) driven by <see cref="FigureJoints"/>:
/// every physics tick the pelvis, spine, neck, head, clavicles, arms and legs are posed so that knees, ankles, elbows and hands land on the figure's
/// joints (the limbs are solved with the model's own bone lengths, so a joint is reproduced exactly unless a limb is too short to reach it). The
/// result is interpolated between physics ticks like the primitive meshes are. Nothing is allocated after loading.
/// </summary>
public sealed partial class SkinnedBody : Node3D
{
    // The glTF model faces +Z with the character's left on +X; the figure faces +X (its right is +Z).
    static readonly Basis ModelToWorld = new(new Vector3(0, 0, -1), Vector3.Up, new Vector3(1, 0, 0));
    static readonly Basis WorldToModel = ModelToWorld.Transposed();
    const float HipDropMax = 0.06f, ShoulderReachMax = 0.14f;

    Skeleton3D _sk = null!;
    /// <summary>Standing height of the model the build script produces (HEIGHT_M in tools/characters/build_characters.py); the figure scales it to its own.</summary>
    const float ModelHeight = 1.8f;
    float _scale = 1f, _restAnkleHeight;
    Basis[] _restBasis = Array.Empty<Basis>();
    Vector3[] _restPos = Array.Empty<Vector3>();
    int[] _parent = Array.Empty<int>();
    Transform3D[] _glob = Array.Empty<Transform3D>();

    // bone indices
    int _hips, _spine, _spine1, _spine2, _neck, _head;
    readonly int[] _clav = new int[2], _arm = new int[2], _fore = new int[2], _hand = new int[2];
    readonly int[] _up = new int[2], _leg = new int[2], _foot = new int[2], _toe = new int[2];
    int[] _trunk = Array.Empty<int>();
    float _thigh0, _shin0, _arm0, _fore0;   // model bone lengths
    /// <summary>The model is scaled so that its leg (hip to ankle) is as long as the figure's (0.48 of the height): longer legs would stay bent at every pelvis height.</summary>
    float _fit = 1f;

    // driven bones in hierarchy order, with the last two physics ticks' local poses for interpolation
    int[] _driven = Array.Empty<int>();
    Vector3[] _posPrev = Array.Empty<Vector3>(), _posCur = Array.Empty<Vector3>();
    Quaternion[] _rotPrev = Array.Empty<Quaternion>(), _rotCur = Array.Empty<Quaternion>();
    bool _havePose;
    ulong _poseFrame;   // physics frame of the latest Pose(); on a tick without one the figure stood still, so there is nothing to blend

    readonly List<(MeshInstance3D mesh, int surface, string role, StandardMaterial3D mat)> _surfaces = new();
    Label3D _number = null!;

    /// <summary>Loads the glTF at <paramref name="path"/>; returns null (and the reason) if it is missing, unreadable or lacks the expected skeleton.</summary>
    public static SkinnedBody? TryLoad(string path, out string error)
    {
        error = "";
        try
        {
            if (!ResourceLoader.Exists(path)) { error = $"{path} not found"; return null; }
            var scene = ResourceLoader.Load<PackedScene>(path);
            if (scene == null) { error = $"{path} failed to load"; return null; }
            var root = scene.Instantiate<Node3D>();
            var body = new SkinnedBody { Name = "SkinnedBody" };
            body.AddChild(root);
            if (!body.Bind(root, out error)) { body.Free(); return null; }
            return body;
        }
        catch (Exception e)
        {
            error = $"{path}: {e.Message}";
            return null;
        }
    }

    bool Bind(Node3D root, out string error)
    {
        error = "";
        string missing = "";
        _sk = FindSkeleton(root)!;
        if (_sk == null) { error = "no Skeleton3D in the model"; return false; }
        int n = _sk.GetBoneCount();
        _restBasis = new Basis[n]; _restPos = new Vector3[n]; _parent = new int[n]; _glob = new Transform3D[n];
        for (int i = 0; i < n; i++)
        {
            var g = _sk.GetBoneGlobalRest(i);
            _restBasis[i] = g.Basis.Orthonormalized(); _restPos[i] = g.Origin; _parent[i] = _sk.GetBoneParent(i);
        }
        int Find(string token)
        {
            for (int i = 0; i < n; i++)
            {
                string name = _sk.GetBoneName(i);
                int cut = Math.Max(name.LastIndexOf('_'), name.LastIndexOf(':'));
                if (name.Substring(cut + 1) == token) return i;
            }
            missing = $"model has no bone '{token}'";
            return -1;
        }
        _hips = Find("Hips"); _spine = Find("Spine"); _spine1 = Find("Spine1"); _spine2 = Find("Spine2"); _neck = Find("Neck"); _head = Find("Head");
        string[] sides = { "Left", "Right" };
        for (int i = 0; i < 2; i++)
        {
            _clav[i] = Find(sides[i] + "Shoulder"); _arm[i] = Find(sides[i] + "Arm"); _fore[i] = Find(sides[i] + "ForeArm"); _hand[i] = Find(sides[i] + "Hand");
            _up[i] = Find(sides[i] + "UpLeg"); _leg[i] = Find(sides[i] + "Leg"); _foot[i] = Find(sides[i] + "Foot"); _toe[i] = Find(sides[i] + "ToeBase");
        }
        if (missing.Length > 0) { error = missing; return false; }
        _thigh0 = (_restPos[_leg[0]] - _restPos[_up[0]]).Length(); _shin0 = (_restPos[_foot[0]] - _restPos[_leg[0]]).Length();
        _arm0 = (_restPos[_fore[0]] - _restPos[_arm[0]]).Length(); _fore0 = (_restPos[_hand[0]] - _restPos[_fore[0]]).Length();

        _trunk = new[] { _hips, _spine, _spine1, _spine2, _neck, _head };
        var driven = new List<int> { _hips, _spine, _spine1, _spine2, _neck, _head };
        for (int i = 0; i < 2; i++) driven.AddRange(new[] { _clav[i], _arm[i], _fore[i], _hand[i], _up[i], _leg[i], _foot[i], _toe[i] });
        _driven = driven.ToArray();
        _posPrev = new Vector3[_driven.Length]; _posCur = new Vector3[_driven.Length];
        _rotPrev = new Quaternion[_driven.Length]; _rotCur = new Quaternion[_driven.Length];

        _fit = 0.48f * ModelHeight / (_thigh0 + _shin0);
        Measure(root);
        BuildNumber();
        SetProcess(true);
        return true;
    }

    static Skeleton3D? FindSkeleton(Node node)
    {
        if (node is Skeleton3D s) return s;
        foreach (var c in node.GetChildren())
            if (FindSkeleton(c) is Skeleton3D found) return found;
        return null;
    }

    /// <summary>Collects the materials (by role name) and the sole height of the boots, in skeleton space.</summary>
    void Measure(Node3D root)
    {
        float bootLow = 0f;
        foreach (var child in _sk.GetChildren())
        {
            if (child is not MeshInstance3D mi || mi.Mesh == null) continue;
            mi.ExtraCullMargin = 3f;   // skinned bounds are the rest pose's; a diving keeper leaves them
            var aabb = mi.Mesh.GetAabb();
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                var src = mi.Mesh.SurfaceGetMaterial(s) as StandardMaterial3D;
                if (src == null) continue;
                var mat = (StandardMaterial3D)src.Duplicate();   // per-figure copy so two figures from one file never share a tint
                mi.SetSurfaceOverrideMaterial(s, mat);
                _surfaces.Add((mi, s, src.ResourceName, mat));
                if (src.ResourceName == "boots") bootLow = aabb.Position.Y;
            }
        }
        _restAnkleHeight = _restPos[_foot[0]].Y - bootLow;
        BuildSurfaces();
    }

    // ---- boot contact surfaces: points on the boot mesh (rest pose), carried rigidly by the foot bone ----
    readonly Vector3[,] _surfRest = new Vector3[2, 4];   // rest offset (model space) from the foot bone to the surface point
    readonly Basis[] _footRot = new Basis[2];
    readonly Vector3[] _footPos = new Vector3[2];
    bool _hasSurfaces;

    /// <summary>True when the boot mesh was found and the contact surfaces measured from it.</summary>
    public bool HasSurfaces => _hasSurfaces;

    /// <summary>
    /// Picks one point per boot surface from the boot mesh vertices of each foot: the medial and lateral extremes at mid-foot (inside, outside),
    /// the top of the vamp (laces) and the underside at mid-foot (sole).
    /// </summary>
    void BuildSurfaces()
    {
        var verts = new List<Vector3>[] { new(), new() };
        foreach (var (mesh, surface, role, _) in _surfaces)
        {
            if (role != "boots") continue;
            var arr = (Vector3[])mesh.Mesh.SurfaceGetArrays(surface)[(int)Mesh.ArrayType.Vertex];
            foreach (var raw in arr)
            {
                var v = mesh.Transform * raw;
                int f = Math.Abs(v.X - _restPos[_foot[0]].X) <= Math.Abs(v.X - _restPos[_foot[1]].X) ? 0 : 1;
                verts[f].Add(v);
            }
        }
        for (int i = 0; i < 2; i++)
        {
            var list = verts[i];
            if (list.Count < 8) return;
            Vector3 b = _restPos[_foot[i]];
            float outward = i == 0 ? 1f : -1f;   // the left foot sits at +X; outward is away from the body's centre line
            float zmin = float.MaxValue, zmax = float.MinValue, ymin = float.MaxValue, lmin = float.MaxValue, lmax = float.MinValue;
            foreach (var v in list)
            {
                zmin = Math.Min(zmin, v.Z); zmax = Math.Max(zmax, v.Z); ymin = Math.Min(ymin, v.Y);
                float l = (v.X - b.X) * outward; lmin = Math.Min(lmin, l); lmax = Math.Max(lmax, l);
            }
            float dz = zmax - zmin, half = (lmax - lmin) * 0.5f, lmid = (lmax + lmin) * 0.5f;
            float yTop = ymin;
            foreach (var v in list) if (v.Z > zmin + 0.4f * dz && v.Z < zmin + 0.7f * dz) yTop = Math.Max(yTop, v.Y);
            float hz = yTop - ymin;

            Vector3 inside = default, outside = default, laces = default, sole = default;
            float bi = float.MaxValue, bo = float.MinValue, bl = float.MinValue, bs = float.MaxValue;
            foreach (var v in list)
            {
                float l = (v.X - b.X) * outward;
                bool mid = v.Z > zmin + 0.4f * dz && v.Z < zmin + 0.7f * dz;
                if (mid && v.Y > ymin + 0.25f * hz && v.Y < ymin + 0.7f * hz)
                {
                    if (l < bi) { bi = l; inside = v; }
                    if (l > bo) { bo = l; outside = v; }
                }
                bool central = Math.Abs(l - lmid) < 0.25f * half;
                if (central && v.Z > zmin + 0.5f * dz && v.Z < zmin + 0.8f * dz && v.Y > bl) { bl = v.Y; laces = v; }
                if (central && mid && v.Y < bs) { bs = v.Y; sole = v; }
            }
            _surfRest[i, (int)BootSurface.Inside] = inside - b;
            _surfRest[i, (int)BootSurface.Outside] = outside - b;
            _surfRest[i, (int)BootSurface.Laces] = laces - b;
            _surfRest[i, (int)BootSurface.Sole] = sole - b;
        }
        _hasSurfaces = true;
    }

    /// <summary>The surface point's offset from the ankle joint in the foot's frame (forward, up, the character's right), metres, for a figure of the given height.</summary>
    public Vector3 SurfaceOffset(int foot, BootSurface surface, float height, float ankleH)
    {
        float sc = height / ModelHeight * _fit;
        var o = _surfRest[foot, (int)surface];
        return new Vector3(o.Z * sc, (o.Y + _restAnkleHeight) * sc - ankleH, -o.X * sc);
    }

    /// <summary>The surface point on the boot mesh in world space, from the latest <see cref="Pose"/> (the physics-tick pose, not the interpolated one).</summary>
    public Vector3 SurfacePoint(int foot, BootSurface surface)
    {
        Vector3 pm = _footPos[foot] + _footRot[foot] * _surfRest[foot, (int)surface];
        return ModelToWorld * pm * _scale;
    }

    /// <summary>Shirt number on the upper back, riding the chest bone.</summary>
    void BuildNumber()
    {
        // The back of the shirt: the rearmost skin or cloth vertex around the shoulder blades.
        float backZ = -0.13f, y = _restPos[_spine2].Y + 0.06f;
        foreach (var (mesh, surface, role, _) in _surfaces)
        {
            if (role != "shirt") continue;
            var verts = (Vector3[])mesh.Mesh.SurfaceGetArrays(surface)[(int)Mesh.ArrayType.Vertex];
            float minZ = float.MaxValue;
            foreach (var v in verts)
                if (Math.Abs(v.X) < 0.06f && v.Y > y - 0.08f && v.Y < y + 0.08f) minZ = Math.Min(minZ, v.Z);
            if (minZ < float.MaxValue) backZ = minZ;
            break;
        }
        var att = new BoneAttachment3D { Name = "NumberMount", BoneName = _sk.GetBoneName(_spine2) };
        _sk.AddChild(att);
        _number = new Label3D
        {
            Name = "Number", PixelSize = 0.0016f, FontSize = 150, OutlineSize = 0, Shaded = false, DoubleSided = false,
            AlphaCut = Label3D.AlphaCutMode.Discard, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        att.AddChild(_number);
        // Spine2's rest transform maps bone space to skeleton space; place the label just behind the cloth, facing backwards (-Z).
        var rest = _sk.GetBoneGlobalRest(_spine2);
        var want = new Transform3D(new Basis(Vector3.Up, Mathf.Pi), new Vector3(0, y, backZ - 0.004f));
        _number.Transform = rest.AffineInverse() * want;
    }

    /// <summary>Tints the clothing, skin and hair materials and sets the shirt number from the kit.</summary>
    public void ApplyKit(FigureKit kit)
    {
        foreach (var (_, _, role, mat) in _surfaces)
        {
            switch (role)
            {
                case "shirt": mat.AlbedoColor = kit.Shirt; break;
                case "shorts": mat.AlbedoColor = kit.Shorts; break;
                case "socks": mat.AlbedoColor = kit.Socks; break;
                case "boots": mat.AlbedoColor = kit.Boots; break;
                case "gloves": mat.AlbedoColor = kit.Gloves; break;
                case "skin": mat.AlbedoColor = kit.Skin; break;
                case "hair": mat.AlbedoColor = kit.Hair; break;
            }
        }
        _number.Text = kit.Number.ToString();
        _number.Modulate = kit.NumberColor;
    }

    // -----------------------------------------------------------------------------------------------------------------

    static Basis AimFrame(Vector3 aim, Vector3 pole)
    {
        aim = aim.Normalized();
        pole -= aim * pole.Dot(aim);
        if (pole.LengthSquared() < 1e-8f)
        {
            pole = Math.Abs(aim.Y) < 0.9f ? Vector3.Up : Vector3.Right;
            pole -= aim * pole.Dot(aim);
        }
        pole = pole.Normalized();
        return new Basis(aim, pole, aim.Cross(pole));
    }

    /// <summary>The rotation that turns the rest-pose limb (aim, pole) into the wanted one.</summary>
    static Basis Swing(Vector3 restAim, Vector3 restPole, Vector3 aim, Vector3 pole) => AimFrame(aim, pole) * AimFrame(restAim, restPole).Transposed();

    /// <summary>Two-bone solve with the given bone lengths; the middle joint is pushed toward <paramref name="pole"/>.</summary>
    static void Solve(Vector3 root, Vector3 target, Vector3 pole, float l1, float l2, out Vector3 mid, out Vector3 end)
    {
        Vector3 d = target - root;
        float len = d.Length();
        Vector3 dn = len > 1e-5f ? d / len : Vector3.Down;
        len = Mathf.Clamp(len, Math.Abs(l1 - l2) + 0.01f, (l1 + l2) * 0.9995f);
        end = root + dn * len;
        float x = (len * len + l1 * l1 - l2 * l2) / (2f * len);
        float h = Mathf.Sqrt(Math.Max(0f, l1 * l1 - x * x));
        Vector3 p = pole - dn * pole.Dot(dn);
        if (p.LengthSquared() < 1e-8f) p = Vector3.Forward - dn * Vector3.Forward.Dot(dn);
        mid = root + dn * x + p.Normalized() * h;
    }

    Vector3 Sk(Vector3 world) => WorldToModel * world / _scale;

    void Put(int bone, Basis restRelative, Vector3 pos)
    {
        _glob[bone] = new Transform3D(restRelative * _restBasis[bone], pos);
    }

    /// <summary>Poses the skeleton from the figure's joints. Call once per physics tick, after the figure has computed them.</summary>
    public void Pose(FigureJoints j)
    {
        float s = j.Height / ModelHeight * _fit;
        if (Math.Abs(s - _scale) > 1e-4f)
        {
            _scale = s;
            Transform = new Transform3D(ModelToWorld * Basis.FromScale(Vector3.One * s), Vector3.Zero);
        }

        // Trunk: one rigid rotation (the figure's torso basis), expressed as a turn of the rest pose; the legs' hip joints centre on the figure's.
        Basis dT = WorldToModel * j.Torso * ModelToWorld;
        Vector3 hipMid = Sk((j.Hip[0] + j.Hip[1]) * 0.5f);
        Vector3 restMid = (_restPos[_up[0]] + _restPos[_up[1]]) * 0.5f;
        Vector3 pHips = hipMid - dT * (restMid - _restPos[_hips]);

        float ankleH = j.AnkleHeight / _scale;
        Span<Vector3> ankleT = stackalloc Vector3[2];
        Span<Vector3> pole = stackalloc Vector3[2];
        for (int i = 0; i < 2; i++)
        {
            // Sole height stays where the figure's sole is: the model's ankle sits (its own ankle height) above it.
            ankleT[i] = Sk(j.Ankle[i]) + new Vector3(0, _restAnkleHeight - ankleH, 0);
            Vector3 bend = j.Knee[i] - (j.Hip[i] + j.Ankle[i]) * 0.5f;
            if (bend.LengthSquared() < 1e-6f) bend = j.Torso.X;
            pole[i] = WorldToModel * bend;
        }

        // A limb that cannot reach (the model's legs are a little shorter than the figure's) lowers the pelvis instead of lifting the foot.
        float reach = (_thigh0 + _shin0) * 0.999f, drop = 0f;
        for (int i = 0; i < 2; i++)
        {
            Vector3 hip = pHips + dT * (_restPos[_up[i]] - _restPos[_hips]);
            Vector3 d = ankleT[i] - hip;
            float over = d.Length() - reach;
            if (over > 0f) drop = Math.Max(drop, over * d.Length() / Math.Max(0.3f * d.Length(), Math.Abs(d.Y)));
        }
        pHips.Y -= Math.Min(drop, HipDropMax);

        // trunk bones
        foreach (int b in _trunk)
            Put(b, dT, pHips + dT * (_restPos[b] - _restPos[_hips]));

        for (int i = 0; i < 2; i++)
        {

            // leg
            Vector3 hip = pHips + dT * (_restPos[_up[i]] - _restPos[_hips]);
            Solve(hip, ankleT[i], pole[i], _thigh0, _shin0, out Vector3 knee, out Vector3 ankle);
            Vector3 rp = Vector3.Back;   // +Z: the knee points forward in the rest pose
            Basis thigh = Swing(_restPos[_leg[i]] - _restPos[_up[i]], rp, knee - hip, pole[i]);
            Basis shin = Swing(_restPos[_foot[i]] - _restPos[_leg[i]], rp, ankle - knee, pole[i]);
            Put(_up[i], thigh, hip);
            Put(_leg[i], shin, knee);
            // foot: flat on the ground, turned to the figure's foot direction
            Vector3 fd = WorldToModel * j.FootDir[i];
            fd.Y = 0;
            fd = fd.LengthSquared() > 1e-6f ? fd.Normalized() : Vector3.Back;
            Basis foot = new Basis(fd, Vector3.Up, fd.Cross(Vector3.Up)) * new Basis(Vector3.Back, Vector3.Up, Vector3.Back.Cross(Vector3.Up)).Transposed();
            Put(_foot[i], foot, ankle);
            _footRot[i] = foot; _footPos[i] = ankle;
            Put(_toe[i], foot, ankle + foot * (_restPos[_toe[i]] - _restPos[_foot[i]]));

            // arm: the figure's arms are longer than the model's, so the shoulder girdle reaches toward a hand that is out of reach (as a real one does)
            Vector3 clavHead = pHips + dT * (_restPos[_clav[i]] - _restPos[_hips]);
            Vector3 armHead = pHips + dT * (_restPos[_arm[i]] - _restPos[_hips]);
            Vector3 handT = Sk(j.Hand[i]);
            Vector3 toHand = handT - armHead;
            float over = toHand.Length() - (_arm0 + _fore0) * 0.999f;
            if (over > 0f) armHead += toHand.Normalized() * Math.Min(over, ShoulderReachMax);
            Put(_clav[i], Swing(_restPos[_arm[i]] - _restPos[_clav[i]], Vector3.Up, armHead - clavHead, dT * Vector3.Up), clavHead);
            Vector3 elbowPole = WorldToModel * (j.Elbow[i] - (j.Shoulder[i] + j.Hand[i]) * 0.5f);
            if (elbowPole.LengthSquared() < 1e-6f) elbowPole = -(WorldToModel * j.Torso.X);
            Solve(armHead, handT, elbowPole, _arm0, _fore0, out Vector3 elbow, out Vector3 hand);
            Vector3 rpArm = Vector3.Forward;   // -Z: the elbow points backwards in the rest pose
            Basis upper = Swing(_restPos[_fore[i]] - _restPos[_arm[i]], rpArm, elbow - armHead, elbowPole);
            Basis lower = Swing(_restPos[_hand[i]] - _restPos[_fore[i]], rpArm, hand - elbow, elbowPole);
            Put(_arm[i], upper, armHead);
            Put(_fore[i], lower, elbow);
            Put(_hand[i], lower, hand);
        }

        // local poses (the hips have no driven parent: their local pose is their global one)
        for (int k = 0; k < _driven.Length; k++)
        {
            int b = _driven[k];
            Vector3 pos; Quaternion rot;
            if (k == 0) { pos = _glob[b].Origin; rot = _glob[b].Basis.Orthonormalized().GetRotationQuaternion(); }
            else
            {
                Transform3D parent = _glob[_parent[b]];
                Basis inv = parent.Basis.Transposed();
                pos = inv * (_glob[b].Origin - parent.Origin);
                rot = (inv * _glob[b].Basis).Orthonormalized().GetRotationQuaternion();
            }
            _posPrev[k] = _havePose ? _posCur[k] : pos; _rotPrev[k] = _havePose ? _rotCur[k] : rot;
            _posCur[k] = pos; _rotCur[k] = rot;
        }
        _havePose = true;
        _poseFrame = Engine.GetPhysicsFrames();
    }

    /// <summary>Forget the previous tick (teleport): the next render shows the new pose with no blend.</summary>
    public void ResetInterpolation()
    {
        for (int k = 0; k < _driven.Length; k++) { _posPrev[k] = _posCur[k]; _rotPrev[k] = _rotCur[k]; }
    }

    public override void _Process(double delta)
    {
        if (!_havePose) return;
        float f = _poseFrame == Engine.GetPhysicsFrames() ? (float)Engine.GetPhysicsInterpolationFraction() : 1f;
        for (int k = 0; k < _driven.Length; k++)
        {
            int b = _driven[k];
            _sk.SetBonePosePosition(b, _posPrev[k].Lerp(_posCur[k], f));
            _sk.SetBonePoseRotation(b, _rotPrev[k].Slerp(_rotCur[k], f));
        }
    }
}
