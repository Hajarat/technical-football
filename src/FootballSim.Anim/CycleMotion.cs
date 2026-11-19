using System.Numerics;
using System.Text.Json;

namespace FootballSim.Anim;

/// <summary>
/// One looping stride from tools/anim/cycles.py: a clip that plays in place (its travel taken out) with each foot's strike and
/// toe-off as events ("strike_l", "toeoff_l", "strike_r", "toeoff_r", first and last frame of the stance), its native speed and
/// steps per second, and the speed it is shown alone at in a blend. Its playback position is a function of the simulation's stride
/// clock only (<see cref="ClipFrame"/>), never of a timer of its own.
/// </summary>
/// <remarks>
/// The stride clock (FootballSim.Core.Gait) has a phase s in [0, 2): the right foot swings during [0, 1), the left during [1, 2), and
/// a touch is played at a swing middle (0.5 right, 1.5 left). The phase map puts each foot's mid-stance on the other foot's swing
/// middle (the left's on 0.5, the right's on 1.5) and its strike and toe-off on the edges of a stance window centred there, as wide
/// as the cycle's own stance share (averaged over the two feet): so the drawn swinging foot is the clock's swinging foot, and a
/// running cycle keeps its flight phases (and a walk its double support), which putting the strikes on the clock's half-stride
/// boundaries themselves would erase. Between those six anchors the map is linear, so it is monotone and continuous round the loop.
/// </remarks>
public sealed class StrideCycle
{
    public Clip Clip { get; }
    /// <summary>The cycle's own speed (m/s on the model), steps per second and step length (metres).</summary>
    public float Speed { get; }
    public float StepsPerSecond { get; }
    public float StepLength => Speed / StepsPerSecond;
    /// <summary>The speed at which the blend shows this cycle alone.</summary>
    public float BlendSpeed { get; }
    /// <summary>The share of the stride a foot is on the ground (both feet's mean).</summary>
    public float StanceShare { get; }
    /// <summary>False when the cycle's events do not come in the order the map needs (it then uses the two mid-stances only).</summary>
    public bool FullMap { get; }

    float[] _frame, _phase;   // anchors: clip frame (unwrapped, increasing) -> phase (unwrapped, increasing), plus the wrap
    readonly int _n;
    readonly float _sl, _tl, _sr, _tr, _midL, _midR;
    float _mapShare;   // the stance share the anchors were built for

    public StrideCycle(Clip clip, float speed, float stepsPerSecond, float blendSpeed)
    {
        if (!clip.Loop) throw new ArgumentException($"{clip.Name}: a stride cycle must loop");
        Clip = clip; Speed = speed; StepsPerSecond = stepsPerSecond; BlendSpeed = blendSpeed;
        _n = clip.FrameCount;
        int Ev(string e) => clip.Events.TryGetValue(e, out int f) ? f : throw new ArgumentException($"{clip.Name}: no '{e}' event");
        int sl = Ev("strike_l"), tl = Ev("toeoff_l"), sr = Ev("strike_r"), tr = Ev("toeoff_r");
        float lenL = Mod(tl - sl, _n) + 1, lenR = Mod(tr - sr, _n) + 1;
        StanceShare = (lenL + lenR) * 0.5f / _n;
        _sl = sl; _tl = tl; _sr = sr; _tr = tr;
        _midL = sl + (lenL - 1) * 0.5f; _midR = sr + (lenR - 1) * 0.5f;
        FullMap = Build(StanceShare);
        if (!FullMap) TryAnchors(new List<(float, float)> { (_midL, 0.5f), (_midR, 1.5f) }, _midL, out _frame, out _phase);
        _mapShare = StanceShare;
    }

    /// <summary>The six anchors for a stance window of <paramref name="share"/> (half of it either side of each mid-stance, in phase units).</summary>
    bool Build(float share)
    {
        float h = share;   // half the stance in phase units (a stride is 2)
        var raw = new List<(float F, float S)>
        {
            (_sl - 0.5f, 0.5f - h), (_midL, 0.5f), (_tl + 0.5f, 0.5f + h),
            (_sr - 0.5f, 1.5f - h), (_midR, 1.5f), (_tr + 0.5f, 1.5f + h),
        };
        bool ok = TryAnchors(raw, _midL, out var f, out var p);
        if (ok) { _frame = f; _phase = p; }
        return ok;
    }

    static float Mod(float a, float m) { a %= m; return a < 0 ? a + m : a; }

    /// <summary>Anchors unwrapped from the left mid-stance on: frames in [midL, midL + n), phases in [0.5, 2.5); false unless both increase.</summary>
    bool TryAnchors(List<(float F, float S)> raw, float midL, out float[] frame, out float[] phase)
    {
        var a = raw.Select(r => (F: midL + Mod(r.F - midL, _n), S: 0.5f + Mod(r.S - 0.5f, 2f))).OrderBy(r => r.F).ToList();
        a.Add((a[0].F + _n, a[0].S + 2f));
        frame = a.Select(r => r.F).ToArray(); phase = a.Select(r => r.S).ToArray();
        for (int i = 1; i < a.Count; i++)
            if (!(frame[i] > frame[i - 1]) || !(phase[i] > phase[i - 1])) return false;
        return true;
    }

    /// <summary>The clip frame (continuous, in [0, frame count)) shown at stride phase <paramref name="s"/>.</summary>
    public float ClipFrame(float s) => ClipFrame(s, StanceShare);

    /// <summary>
    /// <see cref="ClipFrame(float)"/> with the stance window <paramref name="share"/> of the stride (at most the cycle's own): the
    /// cycle's stance frames played over a shorter part of the stride and its swing over a longer one, as a runner faster than the
    /// cycle spends less of each stride on the ground. A planted foot then travels back under the body as far as in the cycle while
    /// the body covers a longer step.
    /// </summary>
    public float ClipFrame(float s, float share)
    {
        share = Math.Clamp(share, 0.05f, StanceShare);
        if (FullMap && MathF.Abs(share - _mapShare) > 1e-6f) { Build(share); _mapShare = share; }
        float u = 0.5f + Mod(s - 0.5f, 2f);
        int k = 0;
        while (k < _phase.Length - 2 && u >= _phase[k + 1]) k++;
        float t = (u - _phase[k]) / (_phase[k + 1] - _phase[k]);
        return Mod(_frame[k] + t * (_frame[k + 1] - _frame[k]), _n);
    }

    /// <summary>The clip time (seconds) shown at stride phase <paramref name="s"/>.</summary>
    public float ClipTime(float s) => ClipFrame(s) / Clip.Fps;

    /// <summary>The stride phase at which clip frame <paramref name="frame"/> is shown (the map's inverse).</summary>
    public float PhaseOf(float frame)
    {
        float f = _frame[0] + Mod(frame - _frame[0], _n);
        int k = 0;
        while (k < _frame.Length - 2 && f >= _frame[k + 1]) k++;
        float t = (f - _frame[k]) / (_frame[k + 1] - _frame[k]);
        return Mod(_phase[k] + t * (_phase[k + 1] - _phase[k]), 2f);
    }

    /// <summary>
    /// The cycles of a cycles folder (tools/anim/cycles.py's clips.json: per clip its speed, steps_per_s and blend_speed) in order of
    /// blend speed, with the skeleton they were made for; <paramref name="mirror"/> adds each cycle's mirror (left and right swapped,
    /// <see cref="Mirror"/>) after it under the name suffix "_mirror", not part of the blend.
    /// </summary>
    public static (Skeleton Skeleton, List<StrideCycle> Cycles, List<StrideCycle> Mirrors) Load(string json, Func<string, byte[]> readFile, bool mirror = false)
    {
        var lib = ClipLibrary.Parse(json, readFile);
        using var doc = JsonDocument.Parse(json);
        var list = new List<StrideCycle>();
        var mirrors = new List<StrideCycle>();
        int i = 0;
        foreach (var c in doc.RootElement.GetProperty("clips").EnumerateArray())
        {
            var clip = lib.Clips[i++].Clip;
            float sp = c.GetProperty("speed").GetSingle(), st = c.GetProperty("steps_per_s").GetSingle(), bs = c.GetProperty("blend_speed").GetSingle();
            list.Add(new StrideCycle(clip, sp, st, bs));
            if (mirror) mirrors.Add(new StrideCycle(Mirrored(clip, lib.Skeleton), sp, st, bs));
        }
        list.Sort((a, b) => a.BlendSpeed.CompareTo(b.BlendSpeed));
        return (lib.Skeleton, list, mirrors);
    }

    /// <summary>A cycle's mirror: <see cref="Mirror.Apply"/> with the feet's events swapped too (its left foot is the original's right).</summary>
    public static Clip Mirrored(Clip clip, Skeleton sk)
    {
        var m = Mirror.Apply(clip, sk);
        int n = m.FrameCount, b = m.BoneCount;
        var hips = new Vector3[n]; var rot = new Quaternion[n * b]; var contacts = new byte[n];
        for (int f = 0; f < n; f++) { hips[f] = m.Hips(f); m.Rotations(f).CopyTo(rot.AsSpan(f * b, b)); contacts[f] = m.Contacts(f); }
        var ev = m.Events.ToDictionary(e => e.Key.EndsWith("_l") ? e.Key[..^2] + "_r" : e.Key.EndsWith("_r") ? e.Key[..^2] + "_l" : e.Key, e => e.Value);
        return new Clip(m.Name, m.Fps, m.Loop, m.Bones, hips, rot, ev, contacts);
    }

    /// <summary>The stride phase of a foot's (0 = left) mid-stance: the other foot's swing middle.</summary>
    public static float MidStance(int foot) => foot == 0 ? 0.5f : 1.5f;
}

/// <summary>
/// The attacker's running body from stride cycles (spec "phase-locked cycles"): every cycle is sampled at the simulation's stride
/// phase through its phase map (<see cref="StrideCycle"/>) and the neighbours of the smoothed speed are blended (weights eased in
/// time), giving the local bone rotations and the hips' place relative to a root moving with the body (model space at the model's
/// size: faces +Z, ground y = 0). It also says which foot is on the ground (a stance window around each foot's mid-stance, as wide as
/// the blended cycles') and how far the stride must be stretched so one step covers the simulation's step (<see cref="StrideScale"/>).
/// No clock and no randomness: the same phases and speeds give the same poses.
/// </summary>
public sealed class CycleMotion
{
    public Skeleton Skeleton { get; }
    public IReadOnlyList<StrideCycle> Cycles { get; }
    /// <summary>Half-life (seconds) of the blend weights' approach to the speed's.</summary>
    public float WeightHalfLife = 0.12f;

    readonly float[] _w, _target, _share;
    readonly Quaternion[] _tmp;
    bool _init;

    public CycleMotion(Skeleton skeleton, IReadOnlyList<StrideCycle> cycles)
    {
        if (cycles.Count == 0) throw new ArgumentException("no cycles");
        for (int i = 1; i < cycles.Count; i++)
            if (cycles[i].BlendSpeed <= cycles[i - 1].BlendSpeed) throw new ArgumentException("cycles must be in order of blend speed");
        foreach (var c in cycles)
            if (c.Clip.BoneCount != skeleton.Count) throw new ArgumentException($"{c.Clip.Name}: bone count differs from the skeleton");
        Skeleton = skeleton; Cycles = cycles;
        _w = new float[cycles.Count]; _target = new float[cycles.Count]; _share = new float[cycles.Count];
        _tmp = new Quaternion[skeleton.Count];
    }

    /// <summary>The current (eased) blend weight of each cycle; they sum to 1.</summary>
    public ReadOnlySpan<float> Weights => _w;
    /// <summary>The blended cycles' stance share, native step length (metres) and native speed (m/s).</summary>
    public float StanceShare { get; private set; }
    public float NativeStep { get; private set; }
    public float NativeSpeed { get; private set; }

    /// <summary>
    /// The weights the speed asks for: the cycle whose blend speed it is, or the two either side of it in proportion; below the first
    /// cycle's speed the first alone, above the last's the last alone.
    /// </summary>
    public static void TargetWeights(IReadOnlyList<StrideCycle> cycles, float speed, Span<float> w)
    {
        w.Clear();
        int n = cycles.Count;
        if (speed <= cycles[0].BlendSpeed) { w[0] = 1f; return; }
        if (speed >= cycles[n - 1].BlendSpeed) { w[n - 1] = 1f; return; }
        for (int i = 0; i < n - 1; i++)
        {
            float a = cycles[i].BlendSpeed, b = cycles[i + 1].BlendSpeed;
            if (speed < b) { float u = (speed - a) / (b - a); w[i] = 1f - u; w[i + 1] = u; return; }
        }
    }

    /// <summary>Forget the weights' history: the next <see cref="Step"/> starts at the speed's own weights.</summary>
    public void Reset() => _init = false;

    /// <summary>
    /// One tick: eases the weights toward the speed's, then writes the blended pose at stride phase <paramref name="phase"/> into
    /// <paramref name="local"/> (every bone's local rotation) and returns the hips' position relative to the root.
    /// </summary>
    public Vector3 Step(float phase, float speed, float dt, Span<Quaternion> local) => Step(phase, speed, dt, local, float.NaN, 1f);

    /// <summary>
    /// <see cref="Step(float, float, float, Span{Quaternion})"/> for a simulation step of <paramref name="stepLength"/> metres (a body
    /// <paramref name="scale"/> times the model's size): a cycle whose own step is shorter shortens its stance window in proportion
    /// (<see cref="StrideCycle.ClipFrame(float, float)"/>), so its planted foot stands still under the faster body without the foot
    /// reaching further from the hips than the cycle does; a longer one is shortened instead by <see cref="Stretch"/>.
    /// </summary>
    public Vector3 Step(float phase, float speed, float dt, Span<Quaternion> local, float stepLength, float scale)
    {
        for (int i = 0; i < Cycles.Count; i++)
        {
            float sc = float.IsFinite(stepLength) ? stepLength / Math.Max(Cycles[i].StepLength * scale, 1e-3f) : 1f;
            _share[i] = Cycles[i].StanceShare / Math.Max(sc, 1f);
        }
        TargetWeights(Cycles, speed, _target);
        float a = _init ? 1f - MathF.Exp(-dt * 0.69314718f / Math.Max(WeightHalfLife, 1e-4f)) : 1f;
        float sum = 0f;
        for (int i = 0; i < _w.Length; i++) { _w[i] += (_target[i] - _w[i]) * a; if (_w[i] < 1e-4f) _w[i] = 0f; sum += _w[i]; }
        for (int i = 0; i < _w.Length; i++) _w[i] /= sum;
        _init = true;
        return Sample(phase, local);
    }

    /// <summary>The pose at <paramref name="phase"/> with the current weights (no easing).</summary>
    public Vector3 Sample(float phase, Span<Quaternion> local)
    {
        int b = Skeleton.Count;
        Vector3 hips = Vector3.Zero;
        float share = 0f, step = 0f, spd = 0f;
        bool first = true;
        for (int i = 0; i < Cycles.Count; i++)
        {
            float w = _w[i];
            if (w <= 0f) continue;
            var c = Cycles[i];
            float sh = _share[i] > 0f ? _share[i] : c.StanceShare;
            hips += c.Clip.Sample(c.ClipFrame(phase, sh) / c.Clip.Fps, _tmp) * w;
            share += Math.Min(sh, c.StanceShare) * w; step += c.StepLength * w; spd += c.Speed * w;
            for (int k = 0; k < b; k++)
            {
                if (first) { local[k] = _tmp[k] * w; continue; }
                var q = _tmp[k];
                if (Quaternion.Dot(q, local[k]) < 0f) q = Quaternion.Negate(q);
                local[k] += q * w;
            }
            first = false;
        }
        for (int k = 0; k < b; k++) local[k] = Quaternion.Normalize(local[k]);
        StanceShare = share; NativeStep = step; NativeSpeed = spd;
        return hips;
    }

    /// <summary>Whether foot <paramref name="foot"/> (0 = left) is on the ground at stride phase <paramref name="phase"/> in the blended cycles.</summary>
    public bool InStance(int foot, float phase) => InStance(foot, phase, StanceShare);

    public static bool InStance(int foot, float phase, float stanceShare) => MathF.Abs(Spring.WrapPi((phase - StrideCycle.MidStance(foot)) * MathF.PI) / MathF.PI) < stanceShare;

    /// <summary>How far through its stance (0 at the strike, 1 at the toe-off) foot <paramref name="foot"/> is, or a value outside [0, 1] in its swing.</summary>
    public float StanceProgress(int foot, float phase) =>
        (Spring.WrapPi((phase - StrideCycle.MidStance(foot)) * MathF.PI) / MathF.PI + StanceShare) / (2f * Math.Max(StanceShare, 1e-4f));

    /// <summary>
    /// The factor the feet's fore-aft travel (relative to the hips) is stretched by so one step covers <paramref name="stepLength"/>
    /// metres (the simulation's speed over its steps per second) with the blended cycles' own step: a foot planted in the cycle then
    /// stands still under a body moving at the simulation's speed. <paramref name="scale"/> is the body's size relative to the model.
    /// </summary>
    public float StrideScale(float stepLength, float scale) => stepLength / Math.Max(NativeStep * scale, 1e-3f);

    /// <summary>The fore-aft stretch of the feet for a step of <paramref name="stepLength"/>: the stride scale where the step is shorter than the cycles', 1 where it is longer (the stance is shortened instead).</summary>
    public float Stretch(float stepLength, float scale) => Math.Min(1f, StrideScale(stepLength, scale));
}

/// <summary>
/// Holds a foot where it landed for its stance, whatever the pose under it does: on the first tick of a stance the foot locks where it
/// is drawn, stays there (its height may still rise with the pose, as a heel lifts) until the stance ends or the body has moved more
/// than it can reach (the caller says so; it is then not held again until its next stance), then follows the pose again, the difference between where it was held and where the pose has it
/// dying away with a critically damped spring (<see cref="ReleaseHalfLife"/>), so it never jumps.
/// </summary>
public sealed class PlantLock
{
    public float ReleaseHalfLife = 0.05f;
    /// <summary>
    /// Seconds a landing foot still goes on with the pose's motion (less and less) before it is held still: a foot lands with some speed
    /// left (a heel sliding into the ground), which stopping in one tick would show as a jolt of the leg.
    /// </summary>
    public float SettleSeconds = 0.05f;
    /// <summary>
    /// A held foot whose pose rises this far (metres) above the lowest it stood at is let go: the heel leaving the ground, after which
    /// the foot rolls over its toes as the pose has it (holding the ankle there would drag it behind and snap it forward at the end).
    /// </summary>
    public float LiftOff = 0.025f;
    public bool Locked { get; private set; }
    /// <summary>Held and no longer settling: the foot stands still.</summary>
    public bool Settled => Locked && _t >= SettleSeconds;
    public Vector3 Lock { get; private set; }
    Vector3 _off, _offVel, _shown, _prevWanted;
    bool _have, _spent;
    float _t, _low;

    public void Reset() { Locked = false; _off = _offVel = Vector3.Zero; _have = false; _spent = false; _t = 0f; }

    /// <summary>
    /// The foot to draw this tick: <paramref name="wanted"/> is where the pose has it (world), <paramref name="stance"/> whether it is
    /// on the ground, <paramref name="outOfReach"/> whether a held foot is now too far from the body to hold. A held foot keeps its
    /// ground place; its height is the pose's (a heel rising, a foot settling).
    /// </summary>
    public Vector3 Apply(bool stance, Vector3 wanted, bool outOfReach, float dt)
    {
        if (!stance) _spent = false;
        if (Locked) _low = Math.Min(_low, wanted.Y);
        bool lifted = Locked && wanted.Y > _low + LiftOff;
        if (Locked && (!stance || outOfReach || lifted))
        {
            Locked = false; _spent = stance;   // let go for reach or a lifting heel: not held again before the next stance
            _off = _shown - wanted; _offVel = Vector3.Zero;
        }
        else if (!Locked && stance && !_spent)
        {
            Locked = true; _t = 0f; _low = wanted.Y;
            Lock = _have ? _shown : wanted;   // where it is drawn now (a release still dying away included)
            _off = _offVel = Vector3.Zero;
        }
        Vector3 p;
        if (Locked)
        {
            if (_t < SettleSeconds && _have)
            {
                // still landing: the pose's motion carries on, fading out over the settle
                float k = 1f - _t / Math.Max(SettleSeconds, 1e-4f);
                Lock += (wanted - _prevWanted) * k;
            }
            _t += dt;
            p = new Vector3(Lock.X, wanted.Y, Lock.Z);
        }
        else
        {
            Spring.Decay(ref _off, ref _offVel, ReleaseHalfLife, dt);
            p = wanted + _off;
        }
        _shown = p; _have = true; _prevWanted = wanted;
        return p;
    }
}
