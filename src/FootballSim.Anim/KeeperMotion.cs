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

    /// <summary>A save set searches only clips moving to this side: −1 his left, +1 his right, 0 either.</summary>
    public int Side;
    /// <summary>The set is a hybrid save (<see cref="KeeperChooser.IsChain"/>): its take-off clips, then its baked flight.</summary>
    public bool Chain;
    /// <summary>Seconds of simulation left to the set's target event (a save's contact, a get-up's "up"), when <see cref="HasTarget"/>.</summary>
    public bool HasTarget;
    public float TargetTime;
    /// <summary>Where the save meets the ball (model space at the world origin, model metres), when <see cref="HasSavePoint"/>.</summary>
    public bool HasSavePoint;
    public Vector3 SavePoint;
    /// <summary>The time warp's clamp for the target event (zero: the save clamp, <see cref="TimeWarp.SaveMin"/>..<see cref="TimeWarp.SaveMax"/>).</summary>
    public float WarpMin, WarpMax;
    /// <summary>How the drawn root follows the simulation's position (see <see cref="RootMode"/>).</summary>
    public RootMode Root;
    /// <summary>A lying or get-up set: switches blend with <see cref="Inertializer.HalfLifeGround"/>.</summary>
    public bool Ground;
    /// <summary>Glove targets (model space) and how much of each the arms' IK takes.</summary>
    public LimbTargets Hands;
    /// <summary>Ankle targets for a save with a foot (reflex foot, feet, slide) and their weights.</summary>
    public LimbTargets Feet;
}

/// <summary>How the drawn root follows the simulation's position.</summary>
public enum RootMode
{
    /// <summary>Pulled to it every tick (critically damped, <see cref="KeeperMotion.RootHalfLife"/>; snapped beyond <see cref="KeeperMotion.SnapDistance"/>).</summary>
    Follow,
    /// <summary>Not pulled: a save (its pelvis path stretched toward the save point) and lying where it took him; the root moves and turns with the clip.</summary>
    Free,
    /// <summary>
    /// Pulled back slowly (<see cref="KeeperMotion.ReturnHalfLife"/>) as he gets up where the simulation has him; his facing is pulled
    /// to the simulation's alone (the clip's own turning left out), so he rises facing play.
    /// </summary>
    Return,
}

/// <summary>Targets for both limbs of a pair (model space) and their IK weights (0 = the clip's pose, 1 = on the target).</summary>
public struct LimbTargets
{
    public Vector3 Left, Right;
    public float LeftWeight, RightWeight;
    /// <summary>
    /// Both gloves around one point (a ball held or met with both hands): <see cref="Left"/> is the point, <see cref="Spread"/> how far
    /// each glove sits from it along the line between his shoulders.
    /// </summary>
    public bool Pair;
    public float Spread;
    public readonly bool Any => LeftWeight > 1e-4f || RightWeight > 1e-4f;
}

/// <summary>
/// The keeper's mocap motion without Godot (spec appendix E): every physics tick it advances the playing clip, moves the root by the
/// clip's root motion and pulls it to the simulation's position and facing, searches the chosen set every
/// <see cref="SearchInterval"/> ticks counted from the last <see cref="Teleport"/> (and at once when the set changes), blends every
/// switch through the <see cref="Inertializer"/> (switches to and from the procedural pose too), places the pose and locks the
/// planted feet. Saves (spec appendix E): a timed set plays so its target event lands on the simulation's tick (<see cref="TimeWarp"/>),
/// its pelvis path stretched toward the save point and its clip turned to aim at it; a hybrid save plays its CMU take-off, then its
/// baked flight; the gloves (or a reaching foot) go onto their targets by IK, the body following what the limbs cannot reach; lying and
/// getting up keep the root where the save took him and bring it back as he rises. Turn guards keep every bone under a pop.
/// Nothing here reads a clock or draws a random number: the same inputs give the same poses.
/// Poses are 22 local rotations plus local positions (index 0 the hips' position); the output is in model space anchored at the
/// world origin (the frame the rigged body is drawn in), the procedural input is given in the same frame.
/// </summary>
public sealed class KeeperMotion
{
    public const int SearchInterval = 12;
    /// <summary>A save's pelvis path is stretched by at most this much either way (spec appendix E).</summary>
    public const float PathScaleMin = 0.6f, PathScaleMax = 1.5f;
    /// <summary>Beyond this distance (world metres) a free or returning root snaps to the simulation's (a jump the save never made).</summary>
    public float FreeSnapDistance = 3f;
    /// <summary>Half-life (seconds) of the root's way back to the simulation's position as he gets up.</summary>
    public float ReturnHalfLife = 0.25f;
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
    /// <summary>The same for a timed clip (a save or a get-up landing its event on the tick), whose faster bones the turn guard catches.</summary>
    public float TimedBoneLimit = 1300f;

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
    // a save: the playback rate that lands the target event on the tick, the pelvis path's stretch and the hips' height it starts from
    bool _timed, _late;
    float _timedRate = 1f, _pathScale = 1f, _hipsY0;
    // a hybrid save: the set it belongs to and its half (0 take-off, 1 flight)
    string? _chainBase;
    int _chainStage;
    Quaternion _lastHips = Quaternion.Identity;

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
        for (int i = 0; i < 2; i++)
        {
            string side = i == 0 ? "Left" : "Right";
            _arm[i] = _sk.Find(side + "Arm"); _fore[i] = _sk.Find(side + "ForeArm"); _hand[i] = _sk.Find(side + "Hand");
        }
    }

    readonly int[] _arm = new int[2], _fore = new int[2], _hand = new int[2];

    public Skeleton Skeleton => _sk;
    /// <summary>True while a clip plays (the chosen set has frames).</summary>
    public bool PlayingClip => _clip >= 0;
    /// <summary>The set searched last (a hybrid save's half, "dive_side:flight"), or null.</summary>
    public string? SetName => _setName;
    /// <summary>The save's pelvis path stretch (1 = the clip's own).</summary>
    public float PathScale => _clip >= 0 ? _pathScale : 1f;
    /// <summary>The save's target came too late for any rate (under one tick away): the IK holds the limb on it at once.</summary>
    public bool Late => _late;
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

    /// <summary>How the pose shown last lies: its hips' forward axis down (front), level (side) or up (back).</summary>
    public Lying LyingNow()
    {
        var f = Vector3.Transform(Vector3.UnitZ, _lastHips * Quaternion.Inverse(_sk.RestGlobal[_sk.Hips]));
        return f.Y < -0.5f ? Lying.Front : f.Y > 0.5f ? Lying.Back : Lying.Side;
    }

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
            _inert.Reset(); _lock.Reset(); Array.Fill(_ikOff, Quaternion.Identity);
            _tick = 0; _setName = null; _ps = null; _clip = -1; _t = 0; _rate = 1f;
            _timed = _late = false; _timedRate = 1f; _pathScale = 1f; _chainBase = null; _chainStage = 0;
            _chestW = input.SquareChest && _db.Set(input.Set) != null ? 1f : 0f;
            _stanceW = _db.Set(input.Set) != null ? Math.Clamp(input.Stance.Weight, 0f, 1f) : 0f;
            _havePrev = _havePrev2 = false;
        }

        string? setName = ResolveSet(input, out var ps);
        bool setChanged = !_first && setName != _setName;
        _setName = setName;

        // the query trajectory's spring state: his velocity and facing chase the simulation's
        Vector2 dummy = Vector2.Zero;
        Spring.Velocity(ref dummy, ref _simVel, ref _simAcc, input.Velocity, TrajectoryHalfLife, dt);
        Spring.Angle(ref _simYaw, ref _simYawVel, input.Yaw, TrajectoryHalfLife, dt);

        // the playing clip advances and moves the root
        if (_clip >= 0)
        {
            var pc = _ps!.Clips[_clip];
            if (_timed) _rate = Math.Min(_timedRate, WarpCap(pc));
            else
            {
                float target = WarpRate(pc, input);
                _rate += (target - _rate) * (1f - MathF.Exp(-dt * 0.69314718f / WarpHalfLife));
                _rate = Math.Min(_rate, WarpCap(pc));
            }
            pc.Root.Sample(_t, out var p0, out float y0);
            float t1 = Math.Min(_t + dt * _rate, pc.Clip.Loop ? float.MaxValue : (pc.Clip.FrameCount - 1) / (float)pc.Clip.Fps);
            pc.Root.Sample(t1, out var p1, out float y1);
            _t = Math.Max(_t, t1);
            Vector2 d = Features.ToCharacter(p1 - p0, y0);   // the clip's step in its own character space
            _root += Rotate(d, _yaw) * _pathScale;   // a save's path stretched toward its save point
            // (getting up he turns only to the simulation's facing: the clip's own turning as it rolls over is left out, so he rises facing play)
            if (input.Root != RootMode.Return) _yaw = Spring.WrapPi(_yaw + Spring.WrapPi(y1 - y0));
        }
        // ... and the simulation's position and facing pull it (they are authoritative)
        Vector2 err = _root - input.Position;
        float snap = input.Root == RootMode.Follow ? SnapDistance : FreeSnapDistance;
        if (err.Length() * input.Scale > snap || _first) { _root = input.Position; _rootErrVel = Vector2.Zero; }
        else if (input.Root == RootMode.Free) _rootErrVel = Vector2.Zero;
        else
        {
            var e3 = new Vector3(err.X, 0, err.Y); var v3 = new Vector3(_rootErrVel.X, 0, _rootErrVel.Y);
            Spring.Decay(ref e3, ref v3, input.Root == RootMode.Return ? ReturnHalfLife : RootHalfLife, dt);
            _root = input.Position + new Vector2(e3.X, e3.Z); _rootErrVel = new Vector2(v3.X, v3.Z);
        }
        // the facing too, except in a free root: a save and lying turn the body as the clip does (a smother lies along the ground)
        if (input.Root == RootMode.Free) _yawErrVel = 0f;
        else
        {
            float yerr = Spring.WrapPi(_yaw - input.Yaw);
            Spring.Decay(ref yerr, ref _yawErrVel, input.Root == RootMode.Return ? ReturnHalfLife : RootHalfLife, dt);
            _yaw = Spring.WrapPi(input.Yaw + yerr);
        }

        // this tick's procedural pose in character space
        var proc = _proc; var procP = _procP;
        ToCharacter(procRot, procPos, proc, procP);

        // search
        bool atEnd = _clip >= 0 && !_ps!.Clips[_clip].Clip.Loop && _t >= (_ps.Clips[_clip].Clip.FrameCount - 1) / (float)_ps.Clips[_clip].Clip.Fps - 1e-6f;
        if (ps == null)
        {
            if (_clip >= 0) { _clip = -1; _ps = null; StartTransitionToProc(proc, procP, dt); }
        }
        else if (_first || setChanged || _clip < 0 || (!OneShot(input) && (_tick % SearchInterval == 0 || atEnd)))
        {
            // a one-shot set (a save, a hybrid save's half, lying, a get-up) is searched once, as it begins: it then plays through to
            // its target event on the tick (and holds its last frame), re-timed every search interval
            BuildQuery(ps, input, proc, procP);
            ps.Normalize(_query, _norm);
            int current = _clip >= 0 && ReferenceEquals(ps, _ps) ? _ps.Clips[_clip].Start + CurrentFrameInClip() : -1;
            int best = Matcher.Search(ps, _norm, current, NearSeconds, input.Side);
            SearchCount++;
            if (best != current)
            {
                var pc = ps.Locate(best, out int f);
                f = TimedEntry(ps, pc, f, input);
                bool blend = !_first;
                if (_clip >= 0 && _pathScale != 1f) _pathScale = 1f;   // the stretch's root travel stays; its height offset blends away
                _ps = ps; _clip = ps.IndexOf(pc.Clip.Name); _t = f / (float)pc.Clip.Fps;
                // a save clip with a free root turns him as it was recorded: its frames face the way he faces at its start (+Z), so
                // entered mid-way (a baked smother already turning along the ground) the root takes the clip's heading there
                if (blend && input.Root == RootMode.Free && input.HasSavePoint)
                    Reorient(Spring.WrapPi(input.Yaw + pc.Root.Yaw(f) + Aim(pc, f, input, Spring.WrapPi(input.Yaw + pc.Root.Yaw(f)))));
                StartSave(input);
                _rate = Math.Min(_timed ? _timedRate : _rate, WarpCap(_ps.Clips[_clip]));
                if (blend) StartTransitionToClip(dt, input.Ground || _wasGround ? Inertializer.HalfLifeGround : Inertializer.HalfLife);
                SwitchCount++;
            }
            else Retime(input, dt);
        }
        else if (OneShot(input) && _clip >= 0 && _tick % SearchInterval == 0) Retime(input, dt);
        _wasGround = input.Ground;

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
        LimitTurn(outRot, dt);
        ShiftForLimbs(input, outRot, outPos);
        Vector3 hips = outPos[0];
        // lying, no foot is planted (a lying clip's feet resting on the mat are not standing on them; locking them whips the legs)
        if (input.Ground && input.Set != KeeperChooser.GetUp) contacts = 0;
        // a foot reaching for the ball is the IK's, not planted
        if (input.Feet.LeftWeight > 1e-4f) contacts &= unchecked((byte)~Clip.LeftFoot);
        if (input.Feet.RightWeight > 1e-4f) contacts &= unchecked((byte)~Clip.RightFoot);
        _lock.Apply(_sk, outRot, ref hips, outPos, contacts, dt, mocapVisible || input.Hands.Any || input.Feet.Any);
        outPos[0] = hips;
        ApplyLimbIk(input, outRot, outPos);
        LimitOutputTurn(outRot, outPos, dt);
        _lastHips = outRot[0];

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

    /// <summary>
    /// The set to search this tick and its frames (null: draw the procedural pose). A hybrid save begins in its take-off half when
    /// there is time for it (the flight's own lead from take-off to contact at the fastest rate, plus 0.1 s) and moves to its flight
    /// half once the take-off clip reaches its take-off event; a hybrid set missing its flight half is searched whole.
    /// </summary>
    string? ResolveSet(in MotionInput input, out PoseSet? ps)
    {
        if (!input.Chain || input.Set == null)
        {
            _chainBase = null;
            ps = _db.Set(input.Set);
            return input.Set;
        }
        var takeoff = _db.Exact(KeeperChooser.Takeoff(input.Set));
        var flight = _db.Exact(KeeperChooser.Flight(input.Set));
        if (flight == null) { _chainBase = null; ps = _db.Set(input.Set); return input.Set; }
        if (_chainBase != input.Set)
        {
            _chainBase = input.Set;
            _chainStage = takeoff != null && (!input.HasTarget || input.TargetTime > FlightLead(flight, input.Side) / MaxRate(input) + 0.1f) ? 0 : 1;
        }
        if (_chainStage == 0 && takeoff != null && _clip >= 0 && ReferenceEquals(_ps, takeoff))
        {
            var pc = _ps.Clips[_clip];
            int ev = pc.Event("takeoff");
            if (CurrentFrameInClip() >= (ev >= 0 ? ev : pc.Clip.FrameCount - 1)) _chainStage = 1;
        }
        if (_chainStage == 0 && takeoff == null) _chainStage = 1;
        ps = _chainStage == 0 ? takeoff : flight;
        return _chainStage == 0 ? KeeperChooser.Takeoff(input.Set) : KeeperChooser.Flight(input.Set);
    }

    bool _wasGround;

    /// <summary>
    /// The fastest (degrees per second) any bone of the placed pose may turn from one tick to the next, before the foot lock and the
    /// limbs' IK: a last guard against a switch, a blend into the procedural pose or a baked flight swinging a limb faster than a body
    /// can (the gallery flags 1,500 as a pop). The IK's own turn is limited by <see cref="IkTurnLimit"/>.
    /// </summary>
    public float TurnLimit = 1350f;
    readonly Quaternion[] _turnPrev = new Quaternion[64];
    bool _haveTurnPrev;

    readonly Quaternion[] _outPrev = new Quaternion[64];
    bool _haveOutPrev;

    /// <summary>
    /// The same guard on the pose shown (after the foot lock and the IK), for every bone but the legs of a planted foot (holding it
    /// still is the lock's job): the IK's own turn added to a fast clip cannot make a pop either.
    /// </summary>
    void LimitOutputTurn(Span<Quaternion> rot, Span<Vector3> pos, float dt)
    {
        if (_first || !_haveOutPrev)
        {
            for (int k = 0; k < _n; k++) _outPrev[k] = rot[k];
            _haveOutPrev = true;
            return;
        }
        float most = TurnLimit * MathF.PI / 180f * dt;
        bool changed = false;
        for (int k = 0; k < _n; k++)
        {
            bool planted = false;
            for (int i = 0; i < 2; i++)
                if (_lock.Locked(i) && (k == _sk.UpLeg[i] || k == _sk.Leg[i] || k == _sk.Foot[i] || k == _sk.Toe[i])) planted = true;
            var q = rot[k];
            if (Quaternion.Dot(q, _outPrev[k]) < 0) q = -q;
            float ang = 2f * MathF.Acos(Math.Min(1f, MathF.Abs(Quaternion.Dot(q, _outPrev[k]))));
            if (!planted && ang > most) { q = Quaternion.Normalize(Quaternion.Slerp(_outPrev[k], q, most / ang)); changed = true; }
            rot[k] = q;
            _outPrev[k] = q;
        }
        if (changed) GroundClamp(rot, pos);
    }

    void LimitTurn(Span<Quaternion> rot, float dt)
    {
        if (_first || !_haveTurnPrev)
        {
            for (int k = 0; k < _n; k++) _turnPrev[k] = rot[k];
            _haveTurnPrev = true;
            return;
        }
        float most = TurnLimit * MathF.PI / 180f * dt;
        for (int k = 0; k < _n; k++)
        {
            var q = rot[k];
            if (Quaternion.Dot(q, _turnPrev[k]) < 0) q = -q;
            float ang = 2f * MathF.Acos(Math.Min(1f, MathF.Abs(Quaternion.Dot(q, _turnPrev[k]))));
            if (ang > most) q = Quaternion.Normalize(Quaternion.Slerp(_turnPrev[k], q, most / ang));
            rot[k] = q;
            _turnPrev[k] = q;
        }
    }

    /// <summary>Most a save clip is turned (radians) to aim its contact limb at the save point.</summary>
    public float AimMax = 75f * MathF.PI / 180f;
    /// <summary>A reaching foot's toe points down at most this much (sine of the angle below level).</summary>
    public float ToeDrop = 0.3f;

    /// <summary>
    /// How far (radians) to turn a save clip entered at frame <paramref name="f"/> about a root facing <paramref name="baseYaw"/> so the
    /// way its contact limb reaches at the contact frame (seen from above, from its root) points at the save point: a lunge recorded
    /// straight ahead blocks a ball to his side by turning toward it. Clamped to <see cref="AimMax"/>; none for a take-off, a clip
    /// past its contact, or a reach or save point too close to the body to have a direction.
    /// </summary>
    float Aim(PoseClip pc, int f, in MotionInput input, float baseYaw)
    {
        if (_chainStage == 0 && _chainBase != null || pc.TargetFrame <= f) return 0f;
        var r0 = pc.Root.Position(f);
        var limb = Features.ToCharacter(pc.LimbAtTarget - new Vector3(r0.X, 0, r0.Y), pc.Root.Yaw(f));
        var tgt = Features.ToCharacter(input.SavePoint - new Vector3(_root.X, 0, _root.Y), baseYaw);
        float lc = MathF.Sqrt(limb.X * limb.X + limb.Z * limb.Z), lt = MathF.Sqrt(tgt.X * tgt.X + tgt.Z * tgt.Z);
        if (lc < 0.2f || lt < 0.2f) return 0f;
        float a = Spring.WrapPi(MathF.Atan2(tgt.X, tgt.Z) - MathF.Atan2(limb.X, limb.Z));
        return Math.Clamp(a, -AimMax, AimMax);
    }

    /// <summary>Turns the root to <paramref name="yaw"/> keeping the pose shown (the remembered poses are re-expressed about the new root).</summary>
    void Reorient(float yaw)
    {
        float d = Spring.WrapPi(yaw - _yaw);
        if (MathF.Abs(d) < 1e-5f) return;
        var q = Spring.Yaw(-d);
        _prev[0] = Quaternion.Normalize(q * _prev[0]); _prevP[0] = Vector3.Transform(_prevP[0], q);
        _prev2[0] = Quaternion.Normalize(q * _prev2[0]); _prev2P[0] = Vector3.Transform(_prev2P[0], q);
        _yaw = yaw;
    }

    /// <summary>A set played through once it is entered (no periodic search): a save, a hybrid save's half, lying and a get-up.</summary>
    static bool OneShot(in MotionInput input) => input.Chain || input.HasTarget || input.Ground;

    /// <summary>
    /// A hybrid save's flight is entered where its own time to contact matches the simulation's (its clip picked by the matcher), from
    /// its take-off event on, so it plays at about its own rate and travels its whole path; its take-off where the time to the take-off
    /// event matches what is left before the flight's lead; any other timed set (a save, a get-up) at the matcher's frame,
    /// but no earlier than its target event can still land on the tick at the fastest rate.
    /// </summary>
    int TimedEntry(PoseSet ps, PoseClip pc, int f, in MotionInput input)
    {
        if (!input.HasTarget) return f;
        if (!input.Chain)
        {
            // any other timed set: never so early that even the fastest rate would land its target event late
            if (pc.TargetFrame < 0) return f;
            int latest = pc.TargetFrame - (int)MathF.Round(Math.Max(0f, input.TargetTime) * MaxRate(input) * pc.Clip.Fps);
            return Math.Clamp(Math.Max(f, latest), 0, Math.Max(0, pc.TargetFrame - 1));
        }
        if (_chainStage == 0)
        {
            // a take-off likewise: where its time to its take-off event is what is left before the flight's lead
            int ev = pc.Event("takeoff");
            var fl = _chainBase != null ? _db.Exact(KeeperChooser.Flight(_chainBase)) : null;
            if (ev < 0 || fl == null) return f;
            float left = Math.Max(0f, input.TargetTime - FlightLead(fl, input.Side));
            return Math.Clamp(ev - (int)MathF.Round(left * pc.Clip.Fps), 0, Math.Max(0, ev - 1));
        }
        if (pc.TargetFrame < 0) return f;
        int tk = Math.Max(0, pc.Event("takeoff"));
        int want = pc.TargetFrame - (int)MathF.Round(Math.Max(0f, input.TargetTime) * pc.Clip.Fps);
        return Math.Clamp(want, tk, Math.Max(tk, pc.TargetFrame - 1));
    }

    static float MaxRate(in MotionInput input) => input.WarpMax > 0 ? input.WarpMax : TimeWarp.SaveMax;
    static float MinRate(in MotionInput input) => input.WarpMin > 0 ? input.WarpMin : TimeWarp.SaveMin;

    /// <summary>A flight's lead: seconds (at its own rate) from its take-off event to its contact, for the first clip on the side.</summary>
    static float FlightLead(PoseSet flight, int side)
    {
        foreach (var pc in flight.Clips)
        {
            if (side * pc.SideSign < 0) continue;
            int tk = Math.Max(0, pc.Event("takeoff"));
            return pc.TargetFrame > tk ? (pc.TargetFrame - tk) / (float)pc.Clip.Fps : 0f;
        }
        return 0f;
    }

    /// <summary>
    /// A clip has just been entered: in a set with a target event it is timed (<see cref="Retime"/>) and, with a save point, its pelvis
    /// path is stretched so the clip's contact limb at its contact frame reaches the save point (clamped to
    /// <see cref="PathScaleMin"/>..<see cref="PathScaleMax"/>): horizontally its root travel, vertically the hips' rise or fall from here.
    /// </summary>
    void StartSave(in MotionInput input)
    {
        var pc = _ps!.Clips[_clip];
        _pathScale = 1f;
        _hipsY0 = pc.Clip.Hips(CurrentFrameInClip()).Y;
        bool takeoff = _chainStage == 0 && _chainBase != null;
        _timed = input.HasTarget && (takeoff || pc.TargetFrame >= 0);
        _late = false;
        Retime(input, input.Dt > 0 ? input.Dt : 1f / 120f);
        int f0 = CurrentFrameInClip();
        if (!input.HasSavePoint || takeoff || pc.TargetFrame <= f0) return;
        int c = pc.TargetFrame;
        var r0 = pc.Root.Position(f0); float y0 = pc.Root.Yaw(f0);
        var r3 = new Vector3(r0.X, 0, r0.Y);
        Vector3 h0 = Features.ToCharacter(pc.Clip.Hips(f0) - r3, y0);
        Vector3 hc = Features.ToCharacter(pc.Clip.Hips(c) - r3, y0);
        Vector3 lc = Features.ToCharacter(pc.LimbAtTarget - r3, y0);
        Vector3 target = Features.ToCharacter(input.SavePoint - new Vector3(_root.X, 0, _root.Y), _yaw);
        Vector3 path = hc - h0, want = target - h0 - (lc - hc);
        if (path.LengthSquared() > 0.05f * 0.05f)
            _pathScale = Math.Clamp(Vector3.Dot(want, path) / path.LengthSquared(), PathScaleMin, PathScaleMax);
    }

    /// <summary>
    /// The timed rate (spec appendix E's time warp): the clip's time to its target event (a take-off's: to its take-off event plus the
    /// flight's lead) over the simulation's time to it, clamped; recomputed each search.
    /// </summary>
    void Retime(in MotionInput input, float dt)
    {
        if (!_timed || _clip < 0) return;
        var pc = _ps!.Clips[_clip];
        int f = CurrentFrameInClip();
        float clipTo;
        if (_chainStage == 0 && _chainBase != null)
        {
            int ev = pc.Event("takeoff");
            var flight = _db.Exact(KeeperChooser.Flight(_chainBase));
            clipTo = ((ev >= 0 ? ev : pc.Clip.FrameCount - 1) - f) / (float)pc.Clip.Fps + (flight != null ? FlightLead(flight, input.Side) : 0f);
        }
        else clipTo = pc.TargetFrame >= 0 ? (pc.TargetFrame - f) / (float)pc.Clip.Fps : 0f;
        _timedRate = TimeWarp.Rate(clipTo, input.TargetTime, dt, out _late, MinRate(input), MaxRate(input));
    }

    /// <summary>
    /// The limbs' IK over the placed pose: each glove (the wrist plus <see cref="Limbs.GloveReach"/> along the forearm) toward its
    /// target by its weight, the elbow bending the way it already does (up when the arm is straight); a reaching foot's ankle the same.
    /// </summary>
    void ApplyLimbIk(in MotionInput input, Span<Quaternion> rot, Span<Vector3> pos)
    {
        for (int k = 0; k < _n; k++) _preIk[k] = rot[k];
        ApplyLimbIkRaw(input, rot, pos);
        LimitIkTurn(rot, pos, input.Dt > 0 ? input.Dt : 1f / 120f);
        if (input.Hands.Any || input.Feet.Any) GroundClamp(rot, pos);   // the last word: no joint below the ground
    }

    /// <summary>The fastest (degrees per second) the IK's turn of a limb bone away from the clip may change.</summary>
    public float IkTurnLimit = 1200f;
    readonly Quaternion[] _preIk = new Quaternion[64], _ikOff = InitOff();
    static Quaternion[] InitOff() { var a = new Quaternion[64]; Array.Fill(a, Quaternion.Identity); return a; }

    /// <summary>
    /// The IK's turn of each limb bone away from the clip (and back, as the IK releases) changes no faster than <see cref="IkTurnLimit"/>:
    /// the targets come and go and the limbs' own solutions can swing, but a bone never whips. (Measured after the foot lock: only the
    /// reaching limbs' IK is limited.)
    /// </summary>
    bool LimitIkTurn(Span<Quaternion> rot, Span<Vector3> pos, float dt)
    {
        float most = IkTurnLimit * MathF.PI / 180f * dt;
        bool changed = false;
        for (int i = 0; i < 12; i++)
        {
            int side = i % 2, j = i / 2;
            int k = j switch { 0 => _arm[side], 1 => _fore[side], 2 => _hand[side], 3 => _sk.UpLeg[side], 4 => _sk.Leg[side], _ => _sk.Foot[side] };
            if (k < 0) continue;
            var off = Quaternion.Normalize(rot[k] * Quaternion.Inverse(_preIk[k]));
            if (Quaternion.Dot(off, _ikOff[k]) < 0) off = -off;
            float ang = 2f * MathF.Acos(Math.Min(1f, MathF.Abs(Quaternion.Dot(off, _ikOff[k]))));
            if (ang > most)
            {
                off = Quaternion.Normalize(Quaternion.Slerp(_ikOff[k], off, most / ang));
                rot[k] = Quaternion.Normalize(off * _preIk[k]);
                changed = true;
            }
            _ikOff[k] = off;
        }
        if (changed) _sk.Forward(rot, pos[0], _gr, _gp, pos);
        return changed;
    }

    void ApplyLimbIkRaw(in MotionInput input, Span<Quaternion> rot, Span<Vector3> pos)
    {
        if (!input.Hands.Any && !input.Feet.Any) return;
        _sk.Forward(rot, pos[0], _gr, _gp, pos);
        FeetPass(input, rot, pos, out var footGap);
        LastFootGap = footGap;
        if (!input.Hands.Any) { GroundClamp(rot, pos); return; }
        ArmPass(input, rot, pos, out var gap);
        // a gap the shift before the foot lock left (the lock lifted the body off the ground): closed sideways, when no foot is planted
        gap.Y = Math.Max(gap.Y, 0f);
        if (gap.LengthSquared() > 1e-5f && !_lock.Locked(0) && !_lock.Locked(1))
        {
            pos[0] += gap;
            _sk.Forward(rot, pos[0], _gr, _gp, pos);
            ArmPass(input, rot, pos, out _);
        }
        if (GroundClamp(rot, pos)) ArmPass(input, rot, pos, out _);
    }

    /// <summary>The whole-body shift the limbs' IK asked for on the latest tick (model space; for logs).</summary>
    public Vector3 LastShift { get; private set; }
    /// <summary>The reaching foot's gap left by its IK on the latest tick (model space; for logs).</summary>
    public Vector3 LastFootGap { get; private set; }

    readonly Quaternion[] _ikR = new Quaternion[64];
    readonly Vector3[] _ikP = new Vector3[64];

    /// <summary>
    /// What the limbs cannot reach the body closes (a procedural fallback, a clip short of the save point, a lunge that stops short):
    /// before the feet are locked, the IK is tried on a copy of the pose and the whole body moves by the gap the reaching limbs have
    /// left (weighted by their IK weights), so the planted feet are then held by the legs (or step) where the body went.
    /// </summary>
    void ShiftForLimbs(in MotionInput input, Span<Quaternion> rot, Span<Vector3> pos)
    {
        if (!input.Hands.Any && !input.Feet.Any) return;
        var r = new Span<Quaternion>(_ikR, 0, _n); var p = new Span<Vector3>(_ikP, 0, _n);
        rot.CopyTo(r); pos.CopyTo(p);
        _sk.Forward(r, p[0], _gr, _gp, p);
        FeetPass(input, r, p, out var footGap);
        Vector3 armGap = Vector3.Zero;
        if (input.Hands.Any) ArmPass(input, r, p, out armGap);
        Vector3 gap = input.Hands.Any ? armGap : footGap;
        LastShift = gap;
        if (gap.LengthSquared() > 1e-6f) pos[0] += gap;
    }

    void FeetPass(in MotionInput input, Span<Quaternion> rot, Span<Vector3> pos, out Vector3 residual)
    {
        residual = Vector3.Zero;
        float wsum = 0f;
        for (int i = 0; i < 2; i++)
        {
            float w = Math.Clamp(i == 0 ? input.Feet.LeftWeight : input.Feet.RightWeight, 0f, 1f);
            if (w <= 1e-4f) continue;
            Vector3 target = Vector3.Lerp(_gp[_sk.Foot[i]], i == 0 ? input.Feet.Left : input.Feet.Right, w);
            int up = _sk.UpLeg[i], leg = _sk.Leg[i], foot = _sk.Foot[i];
            Vector3 thighFwd = Vector3.Transform(Vector3.UnitZ, _gr[up] * Quaternion.Inverse(_sk.RestGlobal[up]));
            Vector3 a = _gp[up], b = _gp[leg], c = _gp[foot];
            Vector3 line = Vector3.Normalize(c - a);
            Vector3 knee = (b - a) - line * Vector3.Dot(b - a, line);
            TwoBoneIk.Apply(a, b, c, _gr[up], _gr[leg], target, knee + thighFwd * 0.02f, out var g1, out var g2, out _, 0.995f);
            rot[up] = Quaternion.Normalize(Quaternion.Inverse(_gr[_sk.Parents[up]]) * g1);
            rot[leg] = Quaternion.Normalize(Quaternion.Inverse(g1) * g2);
            rot[foot] = Quaternion.Normalize(Quaternion.Inverse(g2) * _gr[foot]);
            _sk.Forward(rot, pos[0], _gr, _gp, pos);
            // the reaching foot meets the ball with its side or sole, near level: a toe pointing down would dig into the ground and
            // lift the whole body off it
            Vector3 toe = _gp[_sk.Toe[i]] - _gp[foot];
            float len = toe.Length();
            if (len > 1e-4f && toe.Y < -ToeDrop * len)
            {
                var flat = new Vector3(toe.X, 0, toe.Z);
                flat = flat.LengthSquared() > 1e-8f ? Vector3.Normalize(flat) : Vector3.UnitZ;
                var want = Vector3.Normalize(flat * MathF.Sqrt(1f - ToeDrop * ToeDrop) - Vector3.UnitY * ToeDrop) * len;
                var turn = TwoBoneIk.FromTo(toe, want);
                rot[foot] = Quaternion.Normalize(Quaternion.Inverse(g2) * turn * _gr[foot]);
                _sk.Forward(rot, pos[0], _gr, _gp, pos);
            }
            residual += (target - _gp[_sk.Foot[i]]) * w;
            wsum += w;
        }
        if (wsum > 0f) residual /= wsum;
    }

    /// <summary>Lifts the body so no joint is below <see cref="FootLock.Clearance"/> (after the IK moved limbs); true if it did.</summary>
    bool GroundClamp(Span<Quaternion> rot, Span<Vector3> pos)
    {
        _sk.Forward(rot, pos[0], _gr, _gp, pos);
        float low = float.MaxValue;
        for (int k = 0; k < _n; k++) low = Math.Min(low, _gp[k].Y);
        if (low >= _lock.Clearance - 1e-4f) return false;
        pos[0].Y += _lock.Clearance - low;
        _sk.Forward(rot, pos[0], _gr, _gp, pos);
        return true;
    }

    /// <summary>One pass of the arms' IK (needs this pose's forward pass); <paramref name="residual"/> is the weighted gap left between gloves and targets.</summary>
    void ArmPass(in MotionInput input, Span<Quaternion> rot, Span<Vector3> pos, out Vector3 residual)
    {
        residual = Vector3.Zero;
        Vector3 tl = input.Hands.Left, tr = input.Hands.Right;
        if (input.Hands.Pair && _hand[0] >= 0 && _hand[1] >= 0)
        {
            // along the shoulders (the gloves' own line flips when the hands cross over in front of him)
            Vector3 across = _gp[_arm[1]] - _gp[_arm[0]];
            across = across.LengthSquared() > 1e-6f ? Vector3.Normalize(across) : Vector3.Transform(-Vector3.UnitX, Spring.Yaw(_yaw));
            tl = input.Hands.Left - across * input.Hands.Spread;
            tr = input.Hands.Left + across * input.Hands.Spread;
        }
        float wsum = 0f;
        for (int i = 0; i < 2; i++)
        {
            float w = Math.Clamp(i == 0 ? input.Hands.LeftWeight : input.Hands.RightWeight, 0f, 1f);
            if (w <= 1e-4f || _hand[i] < 0) continue;   // (a skeleton without arms)
            int arm = _arm[i], fore = _fore[i], hand = _hand[i];
            Vector3 target = i == 0 ? tl : tr;
            Vector3 glove = Limbs.Glove(_gp[fore], _gp[hand]);
            Vector3 want = Vector3.Lerp(glove, target, w);
            // the wrist goes where the glove's middle lands on the target: two passes, the forearm's direction settling
            for (int pass = 0; pass < 2; pass++)
            {
                Vector3 dir = want - _gp[fore];
                Vector3 wrist = dir.LengthSquared() > 1e-8f ? want - Vector3.Normalize(dir) * Limbs.GloveReach : want;
                // the elbow bends the way the arm already does; a straight arm by its own back (the rest pose's elbow points -Z),
                // turned with the upper arm, so the bend never flips
                Vector3 back = Vector3.Transform(-Vector3.UnitZ, _gr[arm] * Quaternion.Inverse(_sk.RestGlobal[arm]));
                Vector3 a = _gp[arm], b = _gp[fore], c = _gp[hand];
                Vector3 line = c - a;
                Vector3 bend = line.LengthSquared() > 1e-8f ? (b - a) - Vector3.Normalize(line) * Vector3.Dot(b - a, Vector3.Normalize(line)) : Vector3.Zero;
                TwoBoneIk.Apply(a, b, c, _gr[arm], _gr[fore], wrist, bend + back * 0.02f, out var g1, out var g2, out _, 0.995f);
                rot[arm] = Quaternion.Normalize(Quaternion.Inverse(_gr[_sk.Parents[arm]]) * g1);
                rot[fore] = Quaternion.Normalize(Quaternion.Inverse(g1) * g2);
                rot[hand] = Quaternion.Normalize(Quaternion.Inverse(g2) * _gr[hand]);
                _sk.Forward(rot, pos[0], _gr, _gp, pos);
            }
            residual += (want - Limbs.Glove(_gp[fore], _gp[hand])) * w;
            wsum += w;
        }
        if (wsum > 0f) residual /= wsum;
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
        float limit = _timed ? TimedBoneLimit : WarpBoneLimit;
        return turn > 1f ? Math.Clamp(limit / turn, 1f, WarpMax) : WarpMax;
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
        pos[0].Y += (_pathScale - 1f) * (hips.Y - _hipsY0);   // a save's rise or fall stretched with its path
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
    void StartTransitionToClip(float dt, float halfLife)
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
        _inert.Transition(_prev, _velA, _prevP, _velB, _dst, _velC, _dstP, _velD, halfLife);
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
            Features.WriteBody(_query, _sk, rot[0], pos[0].Y, 0f);
        }
        // the save: where it meets the ball from the hips shown last, and when (the set's target event)
        Vector3 hipsNow = _havePrev ? _prevP[0] : procP[0];
        Vector3 save = input.HasSavePoint ? Features.ToCharacter(input.SavePoint - new Vector3(_root.X, 0, _root.Y), _yaw) - hipsNow : Vector3.Zero;
        float time = input.HasTarget ? input.TargetTime : 0f;
        // a take-off counts down to its take-off event: the time left before the flight's own lead to the contact
        if (input.HasTarget && ps.Name.EndsWith(":" + KeeperChooser.TakeoffTag) && _chainBase != null && _db.Exact(KeeperChooser.Flight(_chainBase)) is PoseSet fl)
            time -= FlightLead(fl, input.Side);
        Features.WriteSave(_query, save, time);
        Span<Vector2> tp = stackalloc Vector2[3];
        Span<Vector2> td = stackalloc Vector2[3];
        Trajectory.Predict(_simVel, _simAcc, input.Velocity, _yaw, _simYawVel, input.Yaw, TrajectoryHalfLife, tp, td);
        Features.WriteTrajectory(_query, tp, td);
    }
}
