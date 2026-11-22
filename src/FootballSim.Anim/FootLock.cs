using System.Numerics;

namespace FootballSim.Anim;

/// <summary>
/// Pins planted feet and keeps the body out of the ground, after the pose is placed in the world (model space at world scale, ground
/// y = 0). A foot whose clip frame marks it in contact is locked where its ankle was when the contact began, and the leg's two-bone IK
/// holds it there, so a planted foot never slides (the ankle may still rise with the clip, as a heel lifts before a step). When the body has moved so far from a locked foot that holding it would stretch the
/// leg more than <see cref="StepDistance"/> (the clip stands while the simulation moves him, or it steps shorter than he travels), the
/// foot takes a quick step instead: it lifts and travels in an arc to where the clip has it, over <see cref="StepSeconds"/>, and plants
/// there again. When the contact ends the foot is released, and the leg's turn away from the clip's (thigh, shin and foot) decays with
/// a critically damped spring, so it never jumps or whips. The foot keeps the clip's orientation. Then, when <c>clampGround</c> is set, the
/// whole body is lifted so no joint is below <see cref="Clearance"/>.
/// </summary>
public sealed class FootLock
{
    public float StepDistance = 0.12f;
    /// <summary>A contact begins only while the ankle is below this height (metres; it stands about 9 cm up).</summary>
    public float MaxPlantedAnkle = 0.15f;
    /// <summary>A locked foot whose clip ankle rises this far (metres) above where it was planted is let go (the heel leaving the ground).</summary>
    public float LiftOff = 0.04f;
    /// <summary>The legs' IK reaches softly past this fraction of their length (see <see cref="TwoBoneIk.Solve(Vector3, float, float, Vector3, Vector3, float, out Vector3, out Vector3)"/>), so a knee never whips straight.</summary>
    public float SoftReach = 0.96f;
    public float StepSeconds = 0.14f;
    public float StepLift = 0.07f;
    /// <summary>The fastest a stepping foot travels (metres per second); a longer step takes longer.</summary>
    public float StepSpeed = 7f;
    public float ReleaseHalfLife = 0.1f;
    public float Clearance = 0.01f;
    /// <summary>A contact that begins this close (metres) to where the released foot is drawn locks there; further, the foot steps.</summary>
    public float PlantSnap = 0.03f;

    enum State { Free, Locked, Stepping }
    readonly State[] _state = new State[2];
    readonly Vector3[] _lock = new Vector3[2], _from = new Vector3[2];
    readonly Vector3[] _target = new Vector3[2], _shown = new Vector3[2];   // where the IK put the ankle (before the ground lift), and as drawn
    readonly float[] _stepT = new float[2];
    readonly Vector3[] _stepOff = new Vector3[2];
    readonly bool[] _stepMoving = new bool[2];
    Quaternion[] _gr = Array.Empty<Quaternion>();
    Vector3[] _gp = Array.Empty<Vector3>();

    /// <summary>True while the foot is held where it was planted.</summary>
    public bool Locked(int foot) => _state[foot] == State.Locked;
    /// <summary>True while the foot is stepping to a new plant.</summary>
    public bool Stepping(int foot) => _state[foot] == State.Stepping;
    /// <summary>How many steps the lock has added since the last <see cref="Reset"/>.</summary>
    public int Steps { get; private set; }
    /// <summary>Where the ankle was put by the latest <see cref="Apply"/> (model space).</summary>
    public Vector3 Ankle(int foot) => _shown[foot];


    public void Reset()
    {
        for (int i = 0; i < 2; i++)
        {
            _state[i] = State.Free;
            for (int j = 0; j < 3; j++) _legOff[i, j] = _legOffVel[i, j] = Vector3.Zero;
        }
        Steps = 0; _lift = _liftVel = 0f;
    }

    /// <summary>
    /// Adjusts the leg rotations in <paramref name="local"/> (and lifts <paramref name="hips"/> when a joint is below the ground) for
    /// one tick. <paramref name="contacts"/> holds <see cref="Clip.LeftFoot"/> / <see cref="Clip.RightFoot"/> for the feet that are planted.
    /// </summary>
    public void Apply(Skeleton sk, Span<Quaternion> local, ref Vector3 hips, ReadOnlySpan<Vector3> localPos, byte contacts, float dt, bool clampGround)
    {
        int n = sk.Count;
        if (_gr.Length != n) { _gr = new Quaternion[n]; _gp = new Vector3[n]; }
        sk.Forward(local, hips, _gr, _gp, localPos);
        bool changed = false;
        Span<int> bones = stackalloc int[3];
        for (int i = 0; i < 2; i++)
        {
            int up = sk.UpLeg[i], leg = sk.Leg[i], foot = sk.Foot[i];
            bones[0] = up; bones[1] = leg; bones[2] = foot;
            Vector3 clipAnkle = _gp[foot];
            float reach = (_gp[leg] - _gp[up]).Length() + (clipAnkle - _gp[leg]).Length();
            // a contact counts only with the foot near the ground (a blend can carry a contact frame's foot up into the air)
            bool contact = (contacts & (i == 0 ? Clip.LeftFoot : Clip.RightFoot)) != 0 && (clipAnkle.Y < MaxPlantedAnkle || _state[i] != State.Free);
            // the clip lifting a locked foot's heel well off where it was planted ends the plant (toe-off): it goes with the clip
            if (contact && _state[i] == State.Locked && clipAnkle.Y > _lock[i].Y + LiftOff) contact = false;
            if (!contact && _state[i] != State.Free)
            {
                // released: the leg's turn away from the clip's (what was last drawn) dies away joint by joint, so it never whips
                for (int j = 0; j < 3; j++)
                {
                    _legOff[i, j] = Spring.Log(Quaternion.Normalize(_drawnLocal[i, j] * Quaternion.Inverse(local[bones[j]])));
                    _legOffVel[i, j] = Vector3.Zero;
                }
                _state[i] = State.Free;
            }
            // the leg as drawn before any IK: the clip's, turned by what is left of a release
            bool offset = false;
            for (int j = 0; j < 3; j++)
            {
                Spring.Decay(ref _legOff[i, j], ref _legOffVel[i, j], ReleaseHalfLife, dt);
                if (_legOff[i, j].LengthSquared() > 1e-12f) { local[bones[j]] = Quaternion.Normalize(Spring.Exp(_legOff[i, j]) * local[bones[j]]); offset = true; }
            }
            Quaternion gUp = _gr[up], gLeg = _gr[leg];
            Vector3 knee = _gp[leg], ankle = clipAnkle;
            if (offset)
            {
                gUp = Quaternion.Normalize(_gr[sk.Parents[up]] * local[up]);
                gLeg = Quaternion.Normalize(gUp * local[leg]);
                knee = _gp[up] + Vector3.Transform(localPos.IsEmpty ? sk.Offsets[leg] : localPos[leg], gUp);
                ankle = knee + Vector3.Transform(localPos.IsEmpty ? sk.Offsets[foot] : localPos[foot], gLeg);
                changed = true;
            }
            Vector3 target = ankle;
            if (contact)
            {
                if (_state[i] == State.Free)
                {
                    if ((ankle - clipAnkle).Length() <= PlantSnap) { _state[i] = State.Locked; _lock[i] = ankle; }
                    else { StartStep(i, ankle); _stepOff[i] = ankle - clipAnkle; _stepMoving[i] = true; }
                }
                var held = new Vector3(_lock[i].X, Math.Max(_lock[i].Y, clipAnkle.Y), _lock[i].Z);   // the heel may still lift with the clip
                float hold = (held - _gp[up]).Length();
                bool outOfReach = hold > reach * 0.985f && hold > (clipAnkle - _gp[up]).Length() + 0.005f;
                if (_state[i] == State.Locked && ((held - clipAnkle).Length() > StepDistance || outOfReach))
                {
                    StartStep(i, _target[i]);   // from where the foot is drawn (short of the lock when the leg could not reach it)
                    _stepMoving[i] = false;
                }
                target = held;
                if (_state[i] == State.Stepping)
                {
                    _stepT[i] += dt;
                    float u = Math.Min(1f, _stepT[i] / StepSeconds);
                    float sm = u * u * (3f - 2f * u);
                    // a planted foot starts from rest; a foot already moving with the clip keeps its pace (the gap to the clip closes)
                    float lift = MathF.Sin(MathF.PI * u);
                    target = (_stepMoving[i] ? clipAnkle + _stepOff[i] * (1f - sm) : Vector3.Lerp(_from[i], clipAnkle, sm))
                             + new Vector3(0, StepLift * lift * lift, 0);
                    // keep the stepping foot within the leg's reach, so the knee never snaps straight
                    Vector3 fromHip = target - _gp[up];
                    if (fromHip.Length() > reach * 0.995f) target = _gp[up] + Vector3.Normalize(fromHip) * reach * 0.995f;
                    // and no faster than a foot moves (it lands later instead)
                    Vector3 move = target - _target[i];
                    float most = StepSpeed * dt;
                    if (move.Length() > most) { target = _target[i] + move * (most / move.Length()); if (u >= 1f) _stepT[i] -= dt; }
                    if (u >= 1f && _stepT[i] >= StepSeconds)
                    {
                        // landed where the clip has the foot: planted there, or (the clip's foot is up in the air) left to the clip
                        _state[i] = clipAnkle.Y < MaxPlantedAnkle ? State.Locked : State.Free;
                        _lock[i] = clipAnkle; target = clipAnkle;
                    }
                }
            }
            _target[i] = _shown[i] = ankle;
            if ((target - ankle).LengthSquared() > 1e-10f)
            {
                // the knee bends the way it already points (so a leg already reaching its target is left exactly as it is); a leg
                // too straight to say leans on the thigh's forward (the rest pose's +Z turned with the thigh)
                Vector3 thighFwd = Vector3.Transform(Vector3.UnitZ, gUp * Quaternion.Inverse(sk.RestGlobal[up]));
                Vector3 line = Vector3.Normalize(ankle - _gp[up]);
                Vector3 kneeOff = (knee - _gp[up]) - line * Vector3.Dot(knee - _gp[up], line);
                var gFoot = Quaternion.Normalize(gLeg * local[foot]);
                TwoBoneIk.Apply(_gp[up], knee, ankle, gUp, gLeg, target, kneeOff + thighFwd * 0.02f, out var gUp2, out var gLeg2, out var reached, SoftReach);
                _target[i] = _shown[i] = reached;
                local[up] = Quaternion.Normalize(Quaternion.Inverse(_gr[sk.Parents[up]]) * gUp2);
                local[leg] = Quaternion.Normalize(Quaternion.Inverse(gUp2) * gLeg2);
                local[foot] = Quaternion.Normalize(Quaternion.Inverse(gLeg2) * gFoot);
                changed = true;
            }
            for (int j = 0; j < 3; j++) _drawnLocal[i, j] = local[bones[j]];
        }
        // the lift rises at once to what is needed and sinks back smoothly, so it never pops (also when clamping stops)
        float need = 0f;
        if (clampGround)
        {
            if (changed) sk.Forward(local, hips, _gr, _gp, localPos);
            float low = float.MaxValue;
            for (int k = 0; k < n; k++) low = Math.Min(low, _gp[k].Y);
            need = Math.Max(0f, Clearance - low);
        }
        Spring.Decay(ref _lift, ref _liftVel, LiftHalfLife, dt);
        if (_lift < need) { _lift = need; _liftVel = 0f; }
        if (_lift > 1e-6f)
        {
            hips.Y += _lift;
            for (int i = 0; i < 2; i++) _shown[i].Y += _lift;
        }
    }

    float _lift, _liftVel;
    public float LiftHalfLife = 0.1f;
    /// <summary>How far the body is lifted off the ground this tick (metres).</summary>
    public float Lifted => _lift;

    // per foot: the leg's last drawn local rotations (thigh, shin, foot) and, once released, their offsets from the clip's
    readonly Quaternion[,] _drawnLocal = new Quaternion[2, 3];
    readonly Vector3[,] _legOff = new Vector3[2, 3], _legOffVel = new Vector3[2, 3];


    void StartStep(int i, Vector3 from)
    {
        _state[i] = State.Stepping;
        _from[i] = from;
        _stepT[i] = 0f;
        Steps++;
    }
}
