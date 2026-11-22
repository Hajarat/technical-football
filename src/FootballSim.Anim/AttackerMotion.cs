using System.Numerics;

namespace FootballSim.Anim;

/// <summary>
/// One tick of what the simulation asks of the attacker's running body. Angles are the model's: positive toward his left (the
/// simulation's facing turns the other way, so its turn rate and stick angle change sign on the way in).
/// </summary>
public struct LocoInput
{
    /// <summary>The tick's length, seconds.</summary>
    public float Dt;
    /// <summary>The simulation's stride phase in [0, 2): 0.5 at the left foot's mid-stance, 1.5 at the right's.</summary>
    public float Phase;
    /// <summary>The stride clock's rate, steps per second (phase units per second).</summary>
    public float StepFrequency;
    /// <summary>His speed along his facing, model metres per second (world speed over the figure's scale).</summary>
    public float Speed;
    /// <summary>How fast his facing turns, radians per second.</summary>
    public float YawRate;
    /// <summary>The turn asked for: the stick's angle off his facing, radians.</summary>
    public float TurnDemand;
}

/// <summary>
/// The attacker's running body by motion matching (the counterpart of the keeper's <see cref="KeeperMotion"/>, for locomotion): every
/// <see cref="SearchInterval"/> ticks, and whenever the playing recording runs out or falls out of step, the matcher looks in the
/// locomotion set (<see cref="AttackerChooser.Loco"/>) for the frame nearest the query: the playing frame's feet and hips (so the
/// motion carries on), the trajectory the simulation's speed, turn rate and stick give, the simulation's stride phase and the turn
/// demand. A change of frame is blended by the <see cref="Inertializer"/>. The simulation leads the clip: between searches the
/// recording is played at the rate that keeps its own stride phase on the simulation's stride clock (<see cref="RateMin"/> to
/// <see cref="RateMax"/> of its own speed), so the foot the simulation swings is the foot the recording swings. The pose comes out in
/// character space (about the root on the ground under the hips, facing +Z), as <see cref="CycleMotion"/> gives it. No randomness.
/// </summary>
public sealed class AttackerMotion
{
    /// <summary>Ticks between searches while a recording plays in step.</summary>
    public const int SearchInterval = 12;
    /// <summary>A best frame this near (seconds) the playing one in the same recording is the motion already playing.</summary>
    public const float NearSeconds = 0.2f;
    /// <summary>The playback rate's bounds, as a share of the recording's own speed.</summary>
    public const float RateMin = 0.5f, RateMax = 1.6f;
    /// <summary>How much better (as a fraction of its cost) another frame must be before a recording playing in step is left for it.</summary>
    public const float KeepMargin = 0.35f;
    /// <summary>A one-shot recording is left this long (seconds) before its last frame.</summary>
    public const float LeaveBeforeEnd = 0.1f;
    /// <summary>The time (seconds) in which a stride-phase error is closed by the playback rate.</summary>
    public const float CatchUp = 0.1f;
    /// <summary>A recording further than this (stride phase, one step is 1) out of step is searched again at once.</summary>
    public const float OutOfStep = 0.35f;
    /// <summary>Half-life of the query trajectory's springs, seconds.</summary>
    public const float TrajectoryHalfLife = 0.15f;

    readonly Skeleton _sk;
    readonly PoseSet _ps;
    readonly Inertializer _inert;
    readonly int _n;
    readonly float[] _query = new float[Features.Count], _norm = new float[Features.Count];
    readonly Quaternion[] _src, _out, _prev, _prev2, _dst, _dstNext;
    readonly Vector3[] _srcP, _outP, _prevP, _prev2P, _dstP, _dstNextP, _velA, _velB, _velC, _velD;
    readonly float[] _clipStep, _clipSpeed;
    int _clip = -1, _tick;
    float _t, _rate = 1f;
    bool _havePrev, _havePrev2;

    public AttackerMotion(Skeleton skeleton, PoseSet loco)
    {
        _sk = skeleton; _ps = loco; _n = skeleton.Count;
        _inert = new Inertializer(_n);
        _src = new Quaternion[_n]; _out = new Quaternion[_n]; _prev = new Quaternion[_n]; _prev2 = new Quaternion[_n];
        _dst = new Quaternion[_n]; _dstNext = new Quaternion[_n];
        _srcP = new Vector3[_n]; _outP = new Vector3[_n]; _prevP = new Vector3[_n]; _prev2P = new Vector3[_n];
        _dstP = new Vector3[_n]; _dstNextP = new Vector3[_n];
        _velA = new Vector3[_n]; _velB = new Vector3[_n]; _velC = new Vector3[_n]; _velD = new Vector3[_n];
        // each recording's own step length and speed (model metres): its root's path over its strides
        _clipStep = new float[loco.Clips.Count]; _clipSpeed = new float[loco.Clips.Count];
        for (int i = 0; i < loco.Clips.Count; i++)
        {
            var pc = loco.Clips[i];
            int last = pc.Clip.FrameCount - 1;
            float path = 0f;
            for (int f = 1; f <= last; f++) path += Vector2.Distance(pc.Root.Position(f), pc.Root.Position(f - 1));
            float seconds = Math.Max(last / (float)pc.Clip.Fps, 1e-3f);
            float steps = loco.GaitPhase(pc.Start + last) - loco.GaitPhase(pc.Start);
            _clipSpeed[i] = path / seconds;
            _clipStep[i] = float.IsFinite(steps) && steps > 0.25f ? path / steps : float.NaN;
        }
    }

    public PoseSet Set => _ps;
    /// <summary>The playing recording's name, or null before the first tick.</summary>
    public string? ClipName => _clip >= 0 ? _ps.Clips[_clip].Clip.Name : null;
    /// <summary>The side the playing recording moves to: -1 his left, +1 his right, 0 neither (or nothing playing).</summary>
    public int ClipSide => _clip >= 0 ? _ps.Clips[_clip].SideSign : 0;
    /// <summary>The playing recording's time, seconds.</summary>
    public float ClipTime => _t;
    /// <summary>The playback rate of the last tick (recording seconds per second).</summary>
    public float Rate => _rate;
    /// <summary>The simulation's stride phase less the recording's at the last tick's end (one step is 1; within half a stride either way).</summary>
    public float PhaseError { get; private set; }
    /// <summary>Searches made and changes of frame taken since the start.</summary>
    public int SearchCount { get; private set; }
    public int SwitchCount { get; private set; }
    /// <summary>True while a change of frame is still blending out.</summary>
    public bool Blending => _inert.Active;
    /// <summary>The playing recording's own step length and speed, model metres (and per second); the last known ones before the first tick.</summary>
    public float NativeStep { get; private set; } = 1f;
    public float NativeSpeed { get; private set; }

    /// <summary>Forgets the playing recording: the next tick searches afresh and shows its frame as it is (a teleport).</summary>
    public void Reset() { _clip = -1; _havePrev = _havePrev2 = false; _inert.Reset(); _tick = 0; }

    /// <summary>Whether foot <paramref name="foot"/> (0 left) is on the ground in the playing frame (the recording's own contacts).</summary>
    public bool InStance(int foot) =>
        _clip >= 0 && (_ps.Contacts(CurrentFrame()) & (foot == 0 ? Clip.LeftFoot : Clip.RightFoot)) != 0;

    /// <summary>The stride's fore-aft shortening for a step of <paramref name="stepLength"/> world metres on a figure of <paramref name="scale"/>: at most 1.</summary>
    public float Stretch(float stepLength, float scale) => Math.Min(1f, stepLength / Math.Max(NativeStep * scale, 1e-3f));

    /// <summary>The playing recording, its frame, rate and stride-phase error, for a log line.</summary>
    public string Describe() =>
        _clip < 0 ? "match=none" : $"match={ClipName}@{_t * _ps.Clips[_clip].Clip.Fps:F1} rate={_rate:F2} phase_err={PhaseError:F3} switches={SwitchCount}";

    int CurrentFrame()
    {
        var pc = _ps.Clips[_clip];
        return pc.Start + Math.Clamp((int)MathF.Round(_t * pc.Clip.Fps), 0, pc.Clip.FrameCount - 1);
    }

    /// <summary>The recording's stride phase at <paramref name="t"/> seconds (unwrapped along the clip) and its rate there (phase per recording second).</summary>
    float ClipPhase(PoseClip pc, float t, out float rate)
    {
        int last = pc.Clip.FrameCount - 1;
        float f = Math.Clamp(t * pc.Clip.Fps, 0f, last);
        int a = Math.Min((int)MathF.Floor(f), Math.Max(last - 1, 0)), b = Math.Min(a + 1, last);
        float pa = _ps.GaitPhase(pc.Start + a), pb = _ps.GaitPhase(pc.Start + b);
        rate = (pb - pa) * pc.Clip.Fps;
        return pa + (pb - pa) * (f - a);
    }

    static float Wrap2(float d) { d = (d + 1f) % 2f; if (d < 0f) d += 2f; return d - 1f; }

    /// <summary>
    /// One tick: searches when due, plays the recording on in step with the simulation's stride clock and writes the pose's local
    /// rotations to <paramref name="local"/> (the hips' in character space). Returns the hips' place (character space, model metres).
    /// </summary>
    public Vector3 Step(in LocoInput input, Span<Quaternion> local)
    {
        float dt = input.Dt > 0f ? input.Dt : 1f / 120f;
        bool first = _clip < 0;
        int current = first ? -1 : CurrentFrame();
        float err = 0f;
        if (!first) err = Wrap2(input.Phase - ClipPhase(_ps.Clips[_clip], _t, out _));
        // A recording is entered only where its look-ahead features are its own (see AttackerChooser.OptionsFor) but played on
        // to its end: past the searchable frames it is left alone until it runs out, unless it falls out of step.
        bool ending = !first && AtEnd();
        bool due = first || ending || MathF.Abs(err) > OutOfStep || _tick % SearchInterval == 0 && _ps.Searchable(current);
        bool switched = false;
        if (due)
        {
            BuildQuery(input, current);
            _ps.Normalize(_query, _norm);
            // nothing is playing yet: there is no pose to carry on from, so the feet and hips do not count
            if (first) Array.Clear(_norm, Features.FootPos, Features.TrajPos - Features.FootPos);
            // (a recording running out, or out of step, must change: the matcher is asked as if nothing were playing in it)
            int best = ending || !first && !_ps.Searchable(current) ? Leave(current) : Matcher.Search(_ps, _norm, current, NearSeconds, keepMargin: KeepMargin);
            SearchCount++;
            if (best >= 0 && best != current)
            {
                switched = !first;
                var pc = _ps.Locate(best, out int f);
                _clip = IndexOf(pc);   // (by reference: a mirror carries its recording's name)
                _t = f / (float)pc.Clip.Fps;
                if (!first) { StartTransition(dt); SwitchCount++; }
            }
        }
        _tick++;

        // play on: the recording's stride phase follows the simulation's clock
        var clip = _ps.Clips[_clip];
        float phase = ClipPhase(clip, _t, out float phaseRate);
        err = Wrap2(input.Phase - phase);
        float want = input.StepFrequency * dt + err * Math.Min(1f, dt / CatchUp);
        float step = phaseRate > 0.2f ? want / phaseRate : dt;
        step = Math.Clamp(step, RateMin * dt, RateMax * dt);
        _rate = step / dt;
        // (the frame a change lands on is the frame shown this tick: the blend took the new recording one step before it)
        if (!switched) _t = Math.Min(_t + step, (clip.Clip.FrameCount - 1) / (float)clip.Clip.Fps);
        PhaseError = Wrap2(input.Phase + input.StepFrequency * dt - ClipPhase(clip, _t, out _));
        if (float.IsFinite(_clipStep[_clip])) NativeStep = _clipStep[_clip];
        NativeSpeed = _clipSpeed[_clip];

        Sample(_t, _src, _srcP);
        _inert.Update(dt, _src, _srcP, _out, _outP);
        if (_havePrev) { _prev.CopyTo(_prev2, 0); _prevP.CopyTo(_prev2P, 0); _havePrev2 = true; }
        _out.CopyTo(_prev, 0); _outP.CopyTo(_prevP, 0); _havePrev = true;
        for (int k = 0; k < _n; k++) local[k] = _out[k];
        return _outP[0];
    }

    bool AtEnd()
    {
        var pc = _ps.Clips[_clip];
        return !pc.Clip.Loop && _t >= (pc.Clip.FrameCount - 1) / (float)pc.Clip.Fps - LeaveBeforeEnd;
    }

    /// <summary>The best frame to leave the playing recording for: never the stretch of it just played (it would loop a few frames).</summary>
    int Leave(int current)
    {
        var pc = _ps.Locate(current, out _);
        int from = Math.Max(pc.Start, current - (int)MathF.Round(Matcher.LoopBackSeconds * pc.Clip.Fps));
        int best = Matcher.Best(_ps, _norm, out _, from, pc.End);
        return best >= 0 ? best : Matcher.Best(_ps, _norm, out _);
    }

    int IndexOf(PoseClip pc)
    {
        for (int i = 0; i < _ps.Clips.Count; i++) if (ReferenceEquals(_ps.Clips[i], pc)) return i;
        return -1;
    }

    /// <summary>The recording at <paramref name="t"/> in its character space (the root's place and facing taken out).</summary>
    void Sample(float t, Quaternion[] rot, Vector3[] pos)
    {
        var pc = _ps.Clips[_clip];
        var hips = pc.Clip.Sample(t, rot);
        pc.Root.Sample(t, out var r, out float y);
        var inv = Spring.Yaw(-y);
        rot[0] = Quaternion.Normalize(inv * rot[0]);
        pos[0] = Vector3.Transform(hips - new Vector3(r.X, 0, r.Y), inv);
        for (int k = 1; k < _n; k++) pos[k] = _sk.Offsets[k];
    }

    // Into a new frame: the shown pose is last tick's, so the new source is taken at last tick too (one step of playback before the
    // frame it starts on), and the update that follows moves both on by this tick (as the keeper's does).
    void StartTransition(float dt)
    {
        if (!_havePrev) return;
        for (int k = 0; k < _n; k++)
        {
            _velA[k] = _havePrev2 ? Spring.AngularVelocity(_prev2[k], _prev[k], dt) : Vector3.Zero;
            _velB[k] = _havePrev2 ? (_prevP[k] - _prev2P[k]) / dt : Vector3.Zero;
        }
        float rate = Math.Max(_rate, 1e-3f);
        float tPrev = Math.Max(0f, _t - dt * rate);
        Sample(tPrev, _dst, _dstP);
        Sample(_t, _dstNext, _dstNextP);
        float span = (_t - tPrev) / rate;
        for (int k = 0; k < _n; k++)
        {
            _velC[k] = span > 0 ? Spring.AngularVelocity(_dst[k], _dstNext[k], span) : Vector3.Zero;
            _velD[k] = span > 0 ? (_dstNextP[k] - _dstP[k]) / span : Vector3.Zero;
        }
        _inert.Transition(_prev, _velA, _prevP, _velB, _dst, _velC, _dstP, _velD, Inertializer.HalfLife);
    }

    /// <summary>
    /// The raw query: the playing frame's features (its feet and hips carry the motion on), then what the simulation asks: the
    /// trajectory of a body at his speed whose velocity and facing spring toward the stick's direction, the stride phase the
    /// simulation's clock will have at the frame shown, and the turn demand.
    /// </summary>
    void BuildQuery(in LocoInput input, int current)
    {
        if (current >= 0) _ps.Raw(current).CopyTo(_query);
        else Array.Clear(_query);
        float demand = Math.Clamp(input.TurnDemand, -Features.TurnMax, Features.TurnMax);
        Span<Vector2> tp = stackalloc Vector2[3];
        Span<Vector2> td = stackalloc Vector2[3];
        var vel = new Vector2(0f, input.Speed);
        var goal = new Vector2(MathF.Sin(demand), MathF.Cos(demand)) * input.Speed;
        Trajectory.Predict(vel, Vector2.Zero, goal, 0f, input.YawRate, demand, TrajectoryHalfLife, tp, td);
        Features.WriteTrajectory(_query, tp, td);
        Features.WriteGait(_query, input.Phase);
        Features.WriteTurn(_query, demand);
        Features.WriteSave(_query, Vector3.Zero, 0f);
        Features.WriteBall(_query, Vector3.Zero, Vector3.Zero);
    }
}
