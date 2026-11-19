using System.Numerics;

namespace FootballSim.Anim;

/// <summary>What the keeper's motion needs each physics tick, in model space (metres of the 1.80 m model; the caller divides world
/// metres by its body scale): the simulation's root position (x, z), facing (radians about +Y, 0 faces +Z) and velocity, and the set
/// the chooser picked (null for a state drawn procedurally).</summary>
public struct MotionInput
{
    public Vector2 Position;
    public float Yaw;
    public Vector2 Velocity;
    public string? Set;
    /// <summary>World metres per model metre (the snap distance is in world metres).</summary>
    public float Scale;
    public float Dt;
    /// <summary>Keep the chest within <see cref="KeeperMotion.ChestTolerance"/> of <see cref="Yaw"/> (every clip state but a run).</summary>
    public bool SquareChest;
    /// <summary>The stance layer's targets and weight (zero weight: off).</summary>
    public StanceTargets Stance;
}

/// <summary>
/// The keeper's mocap motion without Godot (spec appendix E): every physics tick it advances the playing clip, moves the root by the
/// clip's root motion and pulls it to the simulation's position and facing, searches the chosen set every
/// <see cref="SearchInterval"/> ticks counted from the last <see cref="Teleport"/> (and at once when the set changes), blends every
/// switch through the <see cref="Inertializer"/> (switches to and from the procedural pose too), places the pose and locks the
/// planted feet. Nothing here reads a clock or draws a random number: the same inputs give the same poses.
/// Poses are 22 local rotations plus local positions (index 0 the hips' position); the output is in model space anchored at the
/// world origin (the frame the rigged body is drawn in), the procedural input is given in the same frame.
/// </summary>
public sealed class KeeperMotion
{
    public const int SearchInterval = 12;
    /// <summary>How far (radians) the chest may face away from the simulation's facing while <see cref="MotionInput.SquareChest"/> holds.</summary>
    public const float ChestTolerance = 5f * MathF.PI / 180f;
    /// <summary>Half-life (seconds) of the chest correction's and the stance layer's weights as states come and go.</summary>
    public const float LayerHalfLife = 0.1f;
    public float TrajectoryHalfLife = 0.15f, RootHalfLife = 0.1f, SnapDistance = 0.5f;
    /// <summary>A best frame this close (seconds) to the playing one in the same clip does not cause a jump.</summary>
    public float NearSeconds = 0.25f;
    /// <summary>
    /// Speed warp: a moving clip plays faster or slower so its root keeps pace with the simulation along the way he is going (the CMU
    /// sidesteps and backpedals are slower than the keeper moves), within these rates; below <see cref="WarpMinSpeed"/> (m/s, either
    /// speed) it plays at its own rate. The rate follows its target with <see cref="WarpHalfLife"/>.
    /// </summary>
    public float WarpMin = 0.75f, WarpMax = 3f, WarpMinSpeed = 0.25f, WarpHalfLife = 0.08f;
    /// <summary>The warp never plays a clip so fast that a bone turns quicker than this (degrees per second; the gallery flags 1,500 as a pop).</summary>
    public float WarpBoneLimit = 1000f;

    readonly PoseDatabase _db;
    readonly Skeleton _sk;
    readonly int _n;
    readonly Inertializer _inert;
    readonly FootLock _lock = new();

    bool _first = true;
    int _tick;
    string? _setName;
    PoseSet? _ps;
    int _clip = -1;     // index into _ps.Clips while a clip plays
    float _t;           // seconds into it

    Vector2 _root, _rootErrVel;
    float _yaw, _yawErrVel;
    Vector2 _simVel, _simAcc;
    float _rate = 1f;
    float _chestW, _stanceW;
    StanceTargets _stance;
    float _simYaw, _simYawVel;

    // poses in character space (hips relative to the root, turned so he faces +Z)
    readonly Quaternion[] _src, _out, _prev, _prev2, _proc, _dst, _dstNext;
    readonly Vector3[] _srcP, _outP, _prevP, _prev2P, _procP, _dstP, _dstNextP;
    readonly Vector3[] _velA, _velB, _velC, _velD;
    readonly Quaternion[] _gr;
    readonly Vector3[] _gp;
    readonly float[] _query = new float[Features.Count], _norm = new float[Features.Count];
    bool _havePrev, _havePrev2;

    public KeeperMotion(PoseDatabase db)
    {
        _db = db; _sk = db.Skeleton; _n = _sk.Count;
        _inert = new Inertializer(_n);
        Quaternion[] Q() => new Quaternion[_n];
        Vector3[] V() => new Vector3[_n];
        _src = Q(); _out = Q(); _prev = Q(); _prev2 = Q(); _proc = Q(); _dst = Q(); _dstNext = Q(); _gr = Q();
        _srcP = V(); _outP = V(); _prevP = V(); _prev2P = V(); _procP = V(); _dstP = V(); _dstNextP = V(); _gp = V();
        _velA = V(); _velB = V(); _velC = V(); _velD = V();
    }

    public Skeleton Skeleton => _sk;
    /// <summary>True while a clip plays (the chosen set has frames).</summary>
    public bool PlayingClip => _clip >= 0;
    /// <summary>The playing clip and its frame (60 Hz), or null.</summary>
    public string? ClipName => _clip >= 0 ? _ps!.Clips[_clip].Clip.Name : null;
    public int ClipFrame => _clip >= 0 ? CurrentFrameInClip() : -1;
    /// <summary>True while a switch is still blending.</summary>
    public bool Blending => _inert.Active;
    public FootLock FootLock => _lock;
    /// <summary>The contact labels (<see cref="Clip.LeftFoot"/>, <see cref="Clip.RightFoot"/>) of the frame shown last; 0 without a clip.</summary>
    public byte Contacts { get; private set; }
    public int SearchCount { get; private set; }
    public int SwitchCount { get; private set; }
    public Inertializer Inertializer => _inert;
    /// <summary>The pose of the latest tick before it was placed and foot-locked (character space): what the blend works on.</summary>
    public ReadOnlySpan<Quaternion> AnimationPose => _out;

    /// <summary>A jump (round reset, replay seek): no blend from the old pose, the root snaps, the search clock restarts.</summary>
    public void Teleport() => _first = true;

    /// <summary>
    /// One physics tick. <paramref name="procRot"/>/<paramref name="procPos"/> is this tick's procedural pose (local rotations; local
    /// positions with the hips' position at index 0, model space at the world origin): shown in states without a set, and blended
    /// from or to. Writes the pose to show.
    /// </summary>
    public void Step(in MotionInput input, ReadOnlySpan<Quaternion> procRot, ReadOnlySpan<Vector3> procPos, Span<Quaternion> outRot, Span<Vector3> outPos)
    {
        float dt = input.Dt > 0 ? input.Dt : 1f / 120f;
        if (_first)
        {
            _root = input.Position; _yaw = input.Yaw; _rootErrVel = Vector2.Zero; _yawErrVel = 0;
            _simVel = input.Velocity; _simAcc = Vector2.Zero; _simYaw = input.Yaw; _simYawVel = 0;
            _inert.Reset(); _lock.Reset();
            _tick = 0; _setName = null; _ps = null; _clip = -1; _t = 0; _rate = 1f;
            _chestW = input.SquareChest && _db.Set(input.Set) != null ? 1f : 0f;
            _stanceW = _db.Set(input.Set) != null ? Math.Clamp(input.Stance.Weight, 0f, 1f) : 0f;
            _havePrev = _havePrev2 = false;
        }

        var ps = _db.Set(input.Set);
        bool setChanged = !_first && input.Set != _setName;
        _setName = input.Set;

        // the query trajectory's spring state: his velocity and facing chase the simulation's
        Vector2 dummy = Vector2.Zero;
        Spring.Velocity(ref dummy, ref _simVel, ref _simAcc, input.Velocity, TrajectoryHalfLife, dt);
        Spring.Angle(ref _simYaw, ref _simYawVel, input.Yaw, TrajectoryHalfLife, dt);

        // the playing clip advances and moves the root
        if (_clip >= 0)
        {
            var pc = _ps!.Clips[_clip];
            float target = WarpRate(pc, input);
            _rate += (target - _rate) * (1f - MathF.Exp(-dt * 0.69314718f / WarpHalfLife));
            _rate = Math.Min(_rate, WarpCap(pc));
            pc.Root.Sample(_t, out var p0, out float y0);
            float t1 = Math.Min(_t + dt * _rate, pc.Clip.Loop ? float.MaxValue : (pc.Clip.FrameCount - 1) / (float)pc.Clip.Fps);
            pc.Root.Sample(t1, out var p1, out float y1);
            _t = Math.Max(_t, t1);
            Vector2 d = Features.ToCharacter(p1 - p0, y0);   // the clip's step in its own character space
            _root += Rotate(d, _yaw);
            _yaw = Spring.WrapPi(_yaw + Spring.WrapPi(y1 - y0));
        }
        // ... and the simulation's position and facing pull it (they are authoritative)
        Vector2 err = _root - input.Position;
        if (err.Length() * input.Scale > SnapDistance || _first) { _root = input.Position; _rootErrVel = Vector2.Zero; }
        else
        {
            var e3 = new Vector3(err.X, 0, err.Y); var v3 = new Vector3(_rootErrVel.X, 0, _rootErrVel.Y);
            Spring.Decay(ref e3, ref v3, RootHalfLife, dt);
            _root = input.Position + new Vector2(e3.X, e3.Z); _rootErrVel = new Vector2(v3.X, v3.Z);
        }
        float yerr = Spring.WrapPi(_yaw - input.Yaw);
        Spring.Decay(ref yerr, ref _yawErrVel, RootHalfLife, dt);
        _yaw = Spring.WrapPi(input.Yaw + yerr);

        // this tick's procedural pose in character space
        var proc = _proc; var procP = _procP;
        ToCharacter(procRot, procPos, proc, procP);

        // search
        bool atEnd = _clip >= 0 && !_ps!.Clips[_clip].Clip.Loop && _t >= (_ps.Clips[_clip].Clip.FrameCount - 1) / (float)_ps.Clips[_clip].Clip.Fps - 1e-6f;
        if (ps == null)
        {
            if (_clip >= 0) { _clip = -1; _ps = null; StartTransitionToProc(proc, procP, dt); }
        }
        else if (_first || setChanged || _clip < 0 || _tick % SearchInterval == 0 || atEnd)
        {
            BuildQuery(ps, input, proc, procP);
            ps.Normalize(_query, _norm);
            int current = _clip >= 0 && ReferenceEquals(ps, _ps) ? _ps.Clips[_clip].Start + CurrentFrameInClip() : -1;
            int best = Matcher.Search(ps, _norm, current, NearSeconds);
            SearchCount++;
            if (best != current)
            {
                var pc = ps.Locate(best, out int f);
                bool blend = !_first;
                _ps = ps; _clip = ps.IndexOf(pc.Clip.Name); _t = f / (float)pc.Clip.Fps;
                _rate = Math.Min(_rate, WarpCap(_ps.Clips[_clip]));
                if (blend) StartTransitionToClip(dt);
                SwitchCount++;
            }
        }

        // the source pose: the clip, or the procedural pose
        byte contacts = 0;
        if (_clip >= 0)
        {
            SampleClip(_t, _src, _srcP);
            contacts = _ps!.Contacts(_ps.Clips[_clip].Start + CurrentFrameInClip());
        }
        else { proc.CopyTo(_src, 0); procP.CopyTo(_srcP, 0); }
        Contacts = contacts;

        _inert.Update(dt, _src, _srcP, _out, _outP);

        // remember what was shown (before placing and foot locking: the blend works on the animation pose)
        if (_havePrev) { _prev.CopyTo(_prev2, 0); _prevP.CopyTo(_prev2P, 0); _havePrev2 = true; }
        _out.CopyTo(_prev, 0); _outP.CopyTo(_prevP, 0); _havePrev = true;

        // place at the root, then pin the planted feet
        var yawQ = Spring.Yaw(_yaw);
        for (int k = 0; k < _n; k++) { outRot[k] = _out[k]; outPos[k] = _outP[k]; }
        outRot[0] = Quaternion.Normalize(yawQ * _out[0]);
        outPos[0] = new Vector3(_root.X, 0, _root.Y) + Vector3.Transform(_outP[0], yawQ);
        bool mocapVisible = _clip >= 0 || _inert.Active;
        ApplyLayers(input, outRot, outPos, dt);
        Vector3 hips = outPos[0];
        _lock.Apply(_sk, outRot, ref hips, outPos, contacts, dt, mocapVisible);
        outPos[0] = hips;

        _tick++;
        _first = false;
    }

    /// <summary>
    /// The corrections over the placed clip pose: the chest turned to the simulation's facing, then the stance layer (hips height,
    /// lean, stance width, hands). Their weights follow the state with <see cref="LayerHalfLife"/>, and both are off while the
    /// procedural pose is shown (it already does both), so nothing pops as states come and go.
    /// </summary>
    void ApplyLayers(in MotionInput input, Span<Quaternion> rot, Span<Vector3> pos, float dt)
    {
        float a = 1f - MathF.Exp(-dt * 0.69314718f / LayerHalfLife);
        bool clip = _clip >= 0;
        _chestW += ((input.SquareChest && clip ? 1f : 0f) - _chestW) * a;
        float want = clip ? Math.Clamp(input.Stance.Weight, 0f, 1f) : 0f;
        if (want > 0f) _stance = input.Stance;   // fading out, the last targets stay
        _stanceW += (want - _stanceW) * a;
        if (_chestW < 1e-3f && _stanceW < 1e-3f) return;

        _sk.Forward(rot, pos[0], _gr, _gp, pos);
        if (_chestW >= 1e-3f)
        {
            float turn = PoseLayers.ChestCorrection(PoseLayers.ChestYaw(_sk, _gr), input.Yaw, ChestTolerance) * _chestW;
            PoseLayers.TwistTrunk(_sk, rot, _gr, turn);
            _sk.Forward(rot, pos[0], _gr, _gp, pos);
        }
        if (_stanceW < 1e-3f) return;
        float w = _stanceW;
        // the targets are body-relative: measured about the drawn root (which may trail the simulation's a little)
        var yawQ = Spring.Yaw(_yaw);
        var root = new Vector3(_root.X, 0, _root.Y);
        Vector3 ToModel(Vector3 c) => root + Vector3.Transform(c, yawQ);
        Vector3 ToChar(Vector3 m) => Vector3.Transform(m - root, Quaternion.Inverse(yawQ));

        // lean: the trunk (hips to neck) tipped toward the target's forward lean
        int neck = _sk.Find("Neck");
        Vector3 trunk = ToChar(_gp[neck]) - ToChar(_gp[_sk.Hips]);
        float lean = MathF.Atan2(trunk.Z, trunk.Y);
        PoseLayers.LeanTrunk(_sk, rot, _gr, _yaw, (_stance.Lean - lean) * w);
        // height: the hips toward the target's (the legs bend to keep the feet where they are)
        pos[0] = new Vector3(pos[0].X, pos[0].Y + (_stance.HipsHeight - pos[0].Y) * w, pos[0].Z);
        _sk.Forward(rot, pos[0], _gr, _gp, pos);
        // stance width: each ankle slides sideways toward its target place; feet keep the clip's height and forward place
        for (int i = 0; i < 2; i++)
        {
            Vector3 ank = ToChar(_gp[_sk.Foot[i]]);
            float x = i == 0 ? _stance.LeftAnkleX : _stance.RightAnkleX;
            Vector3 target = ToModel(new Vector3(ank.X + (x - ank.X) * w, ank.Y, ank.Z));
            Vector3 thighFwd = Vector3.Transform(Vector3.UnitZ, _gr[_sk.UpLeg[i]] * Quaternion.Inverse(_sk.RestGlobal[_sk.UpLeg[i]]));
            PoseLayers.Reach(_sk, rot, _gr, _gp, _sk.UpLeg[i], _sk.Leg[i], _sk.Foot[i], target, thighFwd);
        }
        _sk.Forward(rot, pos[0], _gr, _gp, pos);
        // hands: toward the target's place beside the shoulders (arms out low), from where the clip has them
        int la = _sk.Find("LeftArm"), ra = _sk.Find("RightArm");
        Vector3 shoulders = (_gp[la] + _gp[ra]) * 0.5f;
        Vector3 back = Vector3.Transform(-Vector3.UnitZ, yawQ);   // elbows bend back and out
        for (int i = 0; i < 2; i++)
        {
            int arm = i == 0 ? la : ra, fore = _sk.Find(i == 0 ? "LeftForeArm" : "RightForeArm"), hand = _sk.Find(i == 0 ? "LeftHand" : "RightHand");
            Vector3 want3 = shoulders + Vector3.Transform(i == 0 ? _stance.LeftHand : _stance.RightHand, yawQ);
            Vector3 target = Vector3.Lerp(_gp[hand], want3, w);
            PoseLayers.Reach(_sk, rot, _gr, _gp, arm, fore, hand, target, back);
        }
    }

    /// <summary>The playback rate at which the clip's root keeps pace with the simulation's velocity (see <see cref="WarpMin"/>).</summary>
    float WarpRate(PoseClip pc, in MotionInput input)
    {
        float vs = input.Velocity.Length();
        if (vs * input.Scale < WarpMinSpeed) return 1f;
        Vector2 vc = Rotate(pc.Root.Velocity(_t), _yaw);   // the clip's root velocity, turned to where he faces now
        float along = Vector2.Dot(vc, input.Velocity) / vs;
        if (along * input.Scale < WarpMinSpeed) return 1f;
        return Math.Clamp(vs / along, WarpMin, WarpCap(pc));
    }

    /// <summary>The fastest rate the playing frame allows (its bones under <see cref="WarpBoneLimit"/>), never below the clip's own.</summary>
    float WarpCap(PoseClip pc)
    {
        float turn = _ps!.BoneSpeed(pc.Start + CurrentFrameInClip()) * (180f / MathF.PI);
        return turn > 1f ? Math.Clamp(WarpBoneLimit / turn, 1f, WarpMax) : WarpMax;
    }

    /// <summary>The drawn root: position (model space x, z) and facing (yaw).</summary>
    public Vector2 RootPosition => _root;
    public float RootYaw => _yaw;

    /// <summary>The current playback rate (1 = the clip's own speed).</summary>
    public float Rate => _clip >= 0 ? _rate : 1f;

    int CurrentFrameInClip()
    {
        var c = _ps!.Clips[_clip].Clip;
        int f = (int)MathF.Round(_t * c.Fps);
        return c.Loop ? ((f % c.FrameCount) + c.FrameCount) % c.FrameCount : Math.Clamp(f, 0, c.FrameCount - 1);
    }

    static Vector2 Rotate(Vector2 xz, float yaw)
    {
        var v = Vector3.Transform(new Vector3(xz.X, 0, xz.Y), Spring.Yaw(yaw));
        return new Vector2(v.X, v.Z);
    }

    /// <summary>A model-space pose (hips placed in the world) in the character space of the current root.</summary>
    void ToCharacter(ReadOnlySpan<Quaternion> rot, ReadOnlySpan<Vector3> pos, Quaternion[] dstRot, Vector3[] dstPos)
    {
        var inv = Spring.Yaw(-_yaw);
        for (int k = 0; k < _n; k++) { dstRot[k] = rot[k]; dstPos[k] = pos[k]; }
        dstRot[0] = Quaternion.Normalize(inv * rot[0]);
        dstPos[0] = Vector3.Transform(pos[0] - new Vector3(_root.X, 0, _root.Y), inv);
    }

    /// <summary>The playing clip at <paramref name="t"/> in its character space (local offsets at rest).</summary>
    void SampleClip(float t, Quaternion[] rot, Vector3[] pos)
    {
        var pc = _ps!.Clips[_clip];
        var hips = pc.Clip.Sample(t, rot);
        pc.Root.Sample(t, out var r, out float y);
        var inv = Spring.Yaw(-y);
        rot[0] = Quaternion.Normalize(inv * rot[0]);
        pos[0] = Vector3.Transform(hips - new Vector3(r.X, 0, r.Y), inv);
        for (int k = 1; k < _n; k++) pos[k] = _sk.Offsets[k];
    }

    void ShownVelocity(Vector3[] angVel, Vector3[] vel, float dt)
    {
        for (int k = 0; k < _n; k++)
        {
            angVel[k] = _havePrev2 ? Spring.AngularVelocity(_prev2[k], _prev[k], dt) : Vector3.Zero;
            vel[k] = _havePrev2 ? (_prevP[k] - _prev2P[k]) / dt : Vector3.Zero;
        }
    }

    // Into a clip: the shown pose is last tick's, so the new source is taken at last tick too (one step of playback before the frame
    // it starts on): the offset is measured at one instant, and the update that follows moves both on by this tick.
    void StartTransitionToClip(float dt)
    {
        if (!_havePrev) return;
        ShownVelocity(_velA, _velB, dt);
        float rate = Math.Max(_rate, 1e-3f);
        float tPrev = Math.Max(0f, _t - dt * rate);
        SampleClip(tPrev, _dst, _dstP);
        SampleClip(_t, _dstNext, _dstNextP);
        float span = (_t - tPrev) / rate;   // real seconds
        for (int k = 0; k < _n; k++)
        {
            _velC[k] = span > 0 ? Spring.AngularVelocity(_dst[k], _dstNext[k], span) : Vector3.Zero;
            _velD[k] = span > 0 ? (_dstNextP[k] - _dstP[k]) / span : Vector3.Zero;
        }
        _inert.Transition(_prev, _velA, _prevP, _velB, _dst, _velC, _dstP, _velD, Inertializer.HalfLife);
    }

    // Into the procedural pose: it is measured against this tick's procedural pose with no velocity of its own (a procedural state
    // often starts with a jump, which a velocity taken across it would amplify): the shown motion carries on and the gap closes.
    void StartTransitionToProc(Quaternion[] proc, Vector3[] procP, float dt)
    {
        if (!_havePrev) return;
        ShownVelocity(_velA, _velB, dt);
        Array.Clear(_velC); Array.Clear(_velD);
        _inert.Transition(_prev, _velA, _prevP, _velB, proc, _velC, procP, _velD, Inertializer.HalfLife);
    }

    /// <summary>The raw query: the current pose's features (the playing frame's, or the shown pose's) and the predicted trajectory.</summary>
    void BuildQuery(PoseSet ps, in MotionInput input, Quaternion[] proc, Vector3[] procP)
    {
        if (_clip >= 0)
        {
            _ps!.Raw(_ps.Clips[_clip].Start + CurrentFrameInClip()).CopyTo(_query);
        }
        else
        {
            // from the pose shown last tick (or this tick's procedural pose right after a teleport), velocities by difference
            var rot = _havePrev ? _prev : proc; var pos = _havePrev ? _prevP : procP;
            _sk.Forward(rot, pos[0], _gr, _gp, pos);
            Vector3 lf = _gp[_sk.Foot[0]], rf = _gp[_sk.Foot[1]], hp = _gp[_sk.Hips];
            Vector3 lv = Vector3.Zero, rv = Vector3.Zero, hv = Vector3.Zero;
            if (_havePrev && _havePrev2)
            {
                _sk.Forward(_prev2, _prev2P[0], _gr, _gp, _prev2P);
                float dt = input.Dt > 0 ? input.Dt : 1f / 120f;
                lv = (lf - _gp[_sk.Foot[0]]) / dt; rv = (rf - _gp[_sk.Foot[1]]) / dt; hv = (hp - _gp[_sk.Hips]) / dt;
            }
            Features.WritePose(_query, lf, rf, lv, rv, hv);
        }
        Span<Vector2> tp = stackalloc Vector2[3];
        Span<Vector2> td = stackalloc Vector2[3];
        Trajectory.Predict(_simVel, _simAcc, input.Velocity, _yaw, _simYawVel, input.Yaw, TrajectoryHalfLife, tp, td);
        Features.WriteTrajectory(_query, tp, td);
    }
}
