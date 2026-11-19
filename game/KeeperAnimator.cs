using System;
using FootballSim.Anim;
using FootballSim.Core;
using Godot;
using NQuat = System.Numerics.Quaternion;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace FootballSim.Game;

/// <summary>
/// --keeper-anim mocap: drives the keeper's rigged body from motion-captured clips in every state that has a set (spec appendix F):
/// stance and movement, saves (timed so the clip's contact lands on the simulation's contact tick, the pelvis path stretched toward the
/// save point, the gloves or the reaching foot brought onto the simulation's hand point by IK), lying and getting up. Each physics tick:
/// the figure's frame goes to the chooser (state → clip set), <see cref="KeeperMotion"/> searches, plays, blends, places, foot-locks and
/// IKs the clip pose, and the bone rotations go to <see cref="SkinnedBody.PoseBones(ReadOnlySpan{Quaternion}, Vector3, ReadOnlySpan{Vector3})"/>.
/// A set with no kept clips (body block, backward leap) keeps the procedural pose (PlayerFigure.Keeper.cs), solved by the body as
/// before, with the same hand IK; every switch between the two is blended at the bone rotations, so nothing pops. The clip data is read once per run from res://assets/anim/keeper. No clock and no
/// randomness: the same replay draws the same poses.
/// </summary>
public sealed class KeeperAnimator
{
    const string ClipFolder = "res://assets/anim/keeper";

    static PoseDatabase? _db;
    static string? _loadError;

    readonly SkinnedBody _body;
    readonly KeeperMotion _motion;
    readonly NQuat[] _procR, _outR;
    readonly NVec3[] _procP, _outP;
    readonly Quaternion[] _showR;
    readonly Vector3[] _showP;
    readonly bool _log = ToolArgs.Current?.KeeperAnimLog == true;
    int _ticks, _lastSwitches;
    bool _lastPlaying;
    // down on the ground after a dive, smother or slide (and how the last clip left him lying), as the procedural keeper latches it
    bool _onGround, _lyingLatched, _smothered;
    Lying _lying = Lying.Side;
    // the hand IK's release: weights held from the last save or hold, fading over ReleaseSeconds
    float _releaseL, _releaseR, _releaseT = 1e9f;
    bool _releasePair, _releaseFeet;
    NVec3 _releaseAtL, _releaseAtR;
    float _returnT = 1e9f;

    /// <summary>Hand IK: weight 0 → 1 over this long before contact; released over <see cref="ReleaseSeconds"/>.</summary>
    public const float RampSeconds = 0.15f, ReleaseSeconds = 0.2f;
    /// <summary>A reaching foot's IK rises over longer (a leg swings further than an arm to a ball off to the side).</summary>
    public const float FootRampSeconds = 0.25f;
    /// <summary>Each glove sits this far (metres) from the ball's centre when both hands meet it.</summary>
    public const float PairSpread = 0.09f;
    /// <summary>A get-up may play at up to this rate to reach its "up" on the simulation's tick (the CMU get-ups are slow).</summary>
    public const float GetUpWarpMax = 2.2f;
    /// <summary>The get-up clip's "up" lands this far through the core's get-up, so he is upright and facing play before Set takes over.</summary>
    public const float GetUpUpShare = 0.7f;

    KeeperAnimator(SkinnedBody body, PoseDatabase db)
    {
        _body = body;
        _motion = new KeeperMotion(db);
        int n = db.Skeleton.Count;
        _procR = new NQuat[n]; _outR = new NQuat[n]; _procP = new NVec3[n]; _outP = new NVec3[n];
        _showR = new Quaternion[n]; _showP = new Vector3[n];
    }

    public SkinnedBody Body => _body;
    public KeeperMotion Motion => _motion;

    /// <summary>The animator for <paramref name="body"/>, or null (and the reason) when the clips are missing or do not fit the body.</summary>
    public static KeeperAnimator? TryCreate(SkinnedBody body, out string error)
    {
        error = "";
        var db = Database(out error);
        if (db == null) return null;
        var names = body.DrivenBoneNames;
        if (names.Count != db.Skeleton.Count) { error = $"the body drives {names.Count} bones, the clips {db.Skeleton.Count}"; return null; }
        for (int k = 0; k < names.Count; k++)
            if (names[k] != db.Skeleton.Names[k]) { error = $"clip bone {k} is {db.Skeleton.Names[k]}, the body drives {names[k]}"; return null; }
        return new KeeperAnimator(body, db);
    }

    /// <summary>The pose database, built once from the clip folder (the chooser's sets only; rejected clips left out).</summary>
    static PoseDatabase? Database(out string error)
    {
        error = _loadError ?? "";
        if (_db != null || _loadError != null) return _db;
        try
        {
            var json = FileAccess.GetFileAsString(ClipFolder + "/clips.json");
            if (string.IsNullOrEmpty(json)) throw new System.IO.FileNotFoundException($"{ClipFolder}/clips.json not found");
            var lib = ClipLibrary.Parse(json, file =>
            {
                var bytes = FileAccess.GetFileAsBytes($"{ClipFolder}/{file}");
                if (bytes == null || bytes.Length == 0) throw new System.IO.FileNotFoundException($"{ClipFolder}/{file} not found");
                return bytes;
            });
            var drop = ToolArgs.Current?.KeeperAnimDropSets;
            _db = new PoseDatabase(lib, KeeperChooser.Sets, KeeperChooser.WeightsFor, KeeperChooser.OptionsFor, true,
                drop != null && drop.Count > 0 ? drop : null);
            foreach (var s in _db.Sets) GD.Print($"KEEPER_ANIM set={s.Name} clips={s.Clips.Count} frames={s.FrameCount}");
        }
        catch (Exception e)
        {
            _loadError = $"keeper clips: {e.Message}";
            error = _loadError;
        }
        return _db;
    }

    /// <summary>Forget the history (round reset, seek): the next tick shows its pose with no blend from the last one.</summary>
    public void Teleport()
    {
        _motion.Teleport();
        _onGround = _lyingLatched = _smothered = false; _releaseT = 1e9f; _returnT = 1e9f;
    }

    /// <summary>One physics tick: <paramref name="j"/> is this tick's procedural pose (the figure's joints), <paramref name="f"/> the frame it came from.</summary>
    public void Present(FigureJoints j, in FigureFrame f)
    {
        _body.Solve(j);
        var sr = _body.SolvedRotations; var sp = _body.SolvedPositions;
        for (int k = 0; k < _procR.Length; k++)
        {
            _procR[k] = new NQuat(sr[k].X, sr[k].Y, sr[k].Z, sr[k].W);
            _procP[k] = new NVec3(sp[k].X, sp[k].Y, sp[k].Z);
        }
        float scale = _body.ScaleFor(j.Height);
        var pos = ModelSpace.FromWorld(new NVec3(f.Position.X, f.Position.Y, f.Position.Z)) / scale;
        var vel = ModelSpace.FromWorld(new NVec3(f.Velocity.X, 0, f.Velocity.Z)) / scale;
        var act = f.KeeperAction;
        Latch(f);
        string? set = KeeperChooser.SetFor(act, f.KeeperDive, _lying, _onGround);
        // after a smother he stays down where its flight left him, on his side behind the ball with his hands around it, until he gets up
        if (_smothered && (act == KeeperAction.Grounded || act == KeeperAction.Holding && _onGround)) set = KeeperChooser.Smother;
        var input = new MotionInput
        {
            Position = new NVec2(pos.X, pos.Z), Velocity = new NVec2(vel.X, vel.Z), Yaw = ModelSpace.YawFromFacing(f.Facing),
            Set = set, Scale = scale, Dt = f.Dt > 0 ? f.Dt : 1f / 120f,
            SquareChest = KeeperChooser.SquaresChest(act), Chain = KeeperChooser.IsChain(set), Ground = KeeperChooser.IsGround(set),
        };
        input.Stance = StanceFrom(j, input, scale, KeeperChooser.StanceWeight(act));
        SaveInputs(ref input, f, scale);
        _motion.Step(input, _procR, _procP, _outR, _outP);
        _ticks++;
        if (_log && (_motion.SwitchCount != _lastSwitches || _motion.PlayingClip != _lastPlaying))
            GD.Print($"KEEPER_MM tick={_ticks} action={f.KeeperAction} set={input.Set ?? "-"} now={Describe()} rate={_motion.Rate:F2} speed={f.Velocity.Length():F2}");
        if (_log && (input.Hands.Any || input.Feet.Any || act == KeeperAction.GettingUp && _ticks % 6 == 0))
            GD.Print($"KEEPER_IK tick={_ticks} action={act} wl={input.Hands.LeftWeight:F2} wr={input.Hands.RightWeight:F2} fl={input.Feet.LeftWeight:F2} fr={input.Feet.RightWeight:F2} shift={_motion.LastShift.Length():F3} footgap={_motion.LastFootGap.Length():F3} lifted={_motion.FootLock.Lifted:F3} now={Describe()}");
        _lastSwitches = _motion.SwitchCount; _lastPlaying = _motion.PlayingClip;
        for (int k = 0; k < _outR.Length; k++)
        {
            _showR[k] = new Quaternion(_outR[k].X, _outR[k].Y, _outR[k].Z, _outR[k].W);
            _showP[k] = new Vector3(_outP[k].X, _outP[k].Y, _outP[k].Z);
        }
        _body.PoseBones(_showR, _showP[0], _showP);
    }

    NVec3 M(Vector3 world, float scale) => ModelSpace.FromWorld(new NVec3(world.X, world.Y, world.Z)) / scale;

    static bool IsSave(KeeperAction a) => a is KeeperAction.Diving or KeeperAction.Smother or KeeperAction.Slide;
    static bool FootSave(in FigureFrame f) => f.KeeperAction == KeeperAction.Slide
        || f.KeeperAction == KeeperAction.Diving && f.KeeperDive is FootballSim.Core.KeeperDive.Feet or FootballSim.Core.KeeperDive.ReflexFoot;
    static bool OneHand(in FigureFrame f) => f.KeeperAction == KeeperAction.Diving
        && f.KeeperDive is FootballSim.Core.KeeperDive.ReflexHand or FootballSim.Core.KeeperDive.BodyBlock;

    /// <summary>
    /// Whether he is down (a dive, smother or slide took him there; cleared once he is on his feet again) and, as he goes down to lie
    /// or to hold the ball there, how the last clip left him lying (front, side or back), kept until he is up.
    /// </summary>
    void Latch(in FigureFrame f)
    {
        var a = f.KeeperAction;
        if (a == KeeperAction.Slide || a == KeeperAction.Smother
            || a == KeeperAction.Diving && f.KeeperDive is FootballSim.Core.KeeperDive.Low or FootballSim.Core.KeeperDive.Side
                or FootballSim.Core.KeeperDive.High or FootballSim.Core.KeeperDive.Back or FootballSim.Core.KeeperDive.None)
            _onGround = true;
        else if (a is KeeperAction.Set or KeeperAction.Shuffle or KeeperAction.Planted or KeeperAction.Reacting or KeeperAction.Rush
                     or KeeperAction.Charge or KeeperAction.Backpedal or KeeperAction.Spread or KeeperAction.Jockey
                 || a == KeeperAction.Diving)
            _onGround = false;
        if (a == KeeperAction.Smother) _smothered = true;
        else if (a is not (KeeperAction.Grounded or KeeperAction.Holding)) _smothered = false;
        bool lying = a == KeeperAction.Grounded || a == KeeperAction.Holding && _onGround;
        if (lying && !_lyingLatched) { _lying = _motion.LyingNow(); _lyingLatched = true; }
        if (!lying && a != KeeperAction.Holding) _lyingLatched = false;
    }

    /// <summary>
    /// The save's inputs: the side, the target time (a save's contact, a get-up's "up"), the save point, the root mode, and the IK
    /// targets and weights. The hands (both around the ball, or the near one for a reflex hand or body block) or the reaching foot
    /// (reflex foot, feet, slide) go onto the simulation's hand point (the slide's foot onto its meeting point) with a weight rising
    /// 0 → 1 over the last <see cref="RampSeconds"/> before contact (at once when the contact is under a tick away), held while the
    /// ball is in his hands and released over <see cref="ReleaseSeconds"/>.
    /// </summary>
    void SaveInputs(ref MotionInput input, in FigureFrame f, float scale)
    {
        var a = f.KeeperAction;
        float dt = input.Dt;
        bool save = IsSave(a) && float.IsFinite(f.SaveContactTime);
        if (save)
        {
            input.HasTarget = true;
            input.TargetTime = f.SaveContactTime - f.KeeperActionTime;
            input.HasSavePoint = true;
            input.SavePoint = M(f.SaveTarget, scale);
            input.Side = KeeperChooser.SideOf(new NVec3(f.SaveTarget.X, f.SaveTarget.Y, f.SaveTarget.Z), new NVec3(f.Position.X, f.Position.Y, f.Position.Z), f.Facing);
        }
        else if (a == KeeperAction.GettingUp)
        {
            input.HasTarget = true;
            input.TargetTime = GetUpUpShare * KeeperView.GetUpSeconds - f.KeeperActionTime;
            input.WarpMax = GetUpWarpMax;
        }

        // the root: free while a save (its path stretched toward the save point) and lying take him away from where the simulation has his feet, pulled back as he rises
        bool travel = IsSave(a);
        bool down = a == KeeperAction.Grounded || a == KeeperAction.Holding && _onGround;
        if (travel || down) { input.Root = RootMode.Free; _returnT = 0f; }
        else if (a == KeeperAction.GettingUp || _returnT < 0.6f) { input.Root = RootMode.Return; _returnT += a == KeeperAction.GettingUp ? 0f : dt; }
        else input.Root = RootMode.Follow;

        // IK weights
        float w = 0f;
        if (save)
        {
            float tc = Math.Max(f.SaveContactTime, 0f), rem = tc - f.KeeperActionTime;
            // before contact the ramp (a contact under a tick away from the start: at once); after it the release
            float ramp = FootSave(f) ? FootRampSeconds : RampSeconds;
            w = rem > 0f ? Math.Max(0f, 1f - rem / ramp) : Math.Max(0f, 1f + rem / ReleaseSeconds);
        }
        bool holding = a == KeeperAction.Holding || _smothered && a == KeeperAction.Grounded;
        if (holding) w = 1f;
        var hand = Smoothed(M(f.HandPosition, scale), w > 0f, dt, scale);
        if (w > 0f)
        {
            if (FootSave(f))
            {
                var target = a == KeeperAction.Slide ? Smoothed(M(f.SaveTarget, scale), true, 0f, scale) : hand;
                int foot = a == KeeperAction.Slide ? 1 : NearSide(f) > 0 ? 1 : 0;   // the slide leads with the right leg, as baked
                if (foot == 0) { input.Feet.Left = target; input.Feet.LeftWeight = w; } else { input.Feet.Right = target; input.Feet.RightWeight = w; }
                Remember(input.Feet, true, false);
            }
            else if (OneHand(f))
            {
                if (NearSide(f) > 0) { input.Hands.Right = hand; input.Hands.RightWeight = w; } else { input.Hands.Left = hand; input.Hands.LeftWeight = w; }
                Remember(input.Hands, false, false);
            }
            else
            {
                input.Hands = new LimbTargets { Left = hand, LeftWeight = w, RightWeight = w, Pair = true, Spread = PairSpread / scale };
                Remember(input.Hands, false, true);
            }
            _releaseT = 0f;
        }
        else if (_releaseT < ReleaseSeconds)
        {
            // fading out from the last weights, toward the clip's own pose, at the last targets
            _releaseT += dt;
            float k = Math.Max(0f, 1f - _releaseT / ReleaseSeconds);
            var t = new LimbTargets { Left = _releaseAtL, Right = _releaseAtR, LeftWeight = _releaseL * k, RightWeight = _releaseR * k, Pair = _releasePair, Spread = PairSpread / scale };
            if (_releaseFeet) input.Feet = t; else input.Hands = t;
        }
    }

    NVec3 _tgtPrev, _tgtOff, _tgtStep;
    bool _tgtHave;
    /// <summary>
    /// An IK target's step may differ from its last step by at most this (world metres per tick, about 580 m/s²): more is a jump of the
    /// simulation's hand point (a new phase), taken up by an offset that dies away with <see cref="TargetJumpHalfLife"/>.
    /// </summary>
    public const float TargetStepChangeMax = 0.04f, TargetJumpHalfLife = 0.05f;

    /// <summary>The IK target from the simulation's point: the same point, its jumps spread out (see <see cref="TargetStepChangeMax"/>), so the arms never snap.</summary>
    NVec3 Smoothed(NVec3 raw, bool active, float dt, float scale)
    {
        if (dt <= 0f) return raw;   // (a second target of the same tick: as it is)
        if (!active || !_tgtHave) { _tgtPrev = raw; _tgtOff = NVec3.Zero; _tgtStep = NVec3.Zero; _tgtHave = active; return raw; }
        var step = raw - _tgtPrev;
        var change = step - _tgtStep;
        float len = change.Length(), most = TargetStepChangeMax / scale;
        if (len > most) { var excess = change * (1f - most / len); _tgtOff -= excess; step -= excess; }
        _tgtStep = step;
        _tgtOff *= MathF.Exp(-dt * 0.69314718f / TargetJumpHalfLife);
        _tgtPrev = raw;
        return raw + _tgtOff;
    }

    void Remember(in LimbTargets t, bool feet, bool pair)
    {
        _releaseL = t.LeftWeight; _releaseR = t.RightWeight; _releaseAtL = t.Left; _releaseAtR = t.Right; _releasePair = pair; _releaseFeet = feet;
    }

    /// <summary>The side of the save (+1 his right, −1 his left) by the hand point relative to his facing.</summary>
    static int NearSide(in FigureFrame f)
    {
        var p = f.KeeperAction == KeeperAction.Slide ? f.SaveTarget : f.HandPosition;
        int s = KeeperChooser.SideOf(new NVec3(p.X, p.Y, p.Z), new NVec3(f.Position.X, f.Position.Y, f.Position.Z), f.Facing);
        return s == 0 ? 1 : s;
    }

    /// <summary>
    /// The stance layer's targets from this tick's procedural pose (its StandTargets: stance width, hands out low, lean, height), in the
    /// character space of the simulation's root.
    /// </summary>
    StanceTargets StanceFrom(FigureJoints j, in MotionInput input, float scale, float weight)
    {
        var t = new StanceTargets { Weight = weight };
        if (weight <= 0f) return t;
        var root = new NVec3(input.Position.X, 0, input.Position.Y);
        var inv = NQuat.Inverse(Spring.Yaw(input.Yaw));
        NVec3 C(Vector3 w) => NVec3.Transform(ModelSpace.FromWorld(new NVec3(w.X, w.Y, w.Z)) / scale - root, inv);
        var shoulders = (C(j.Shoulder[0]) + C(j.Shoulder[1])) * 0.5f;
        var trunk = shoulders - C(j.Pelvis);
        t.HipsHeight = _procP[0].Y;
        t.Lean = MathF.Atan2(trunk.Z, trunk.Y);
        t.LeftAnkleX = C(j.Ankle[0]).X; t.RightAnkleX = C(j.Ankle[1]).X;
        t.LeftHand = C(j.Hand[0]) - shoulders; t.RightHand = C(j.Hand[1]) - shoulders;
        return t;
    }

    /// <summary>
    /// Whether the foot (0 = left) is planted this tick: held by the foot lock where it landed (the playing clip marks it in contact and
    /// it is on the ground; a contact foot the lock is stepping to a new spot is not planted until it lands).
    /// </summary>
    public bool FootPlanted(int foot) => _motion.FootLock.Locked(foot);

    /// <summary>What is playing, for labels and logs: the clip and frame, or "procedural".</summary>
    public string Describe() => _motion.ClipName is string c
        ? $"{c} f{_motion.ClipFrame}{(_motion.Blending ? " (blend)" : "")}{(_motion.Rate != 1f ? $" x{_motion.Rate:F2}" : "")}{(_motion.PathScale != 1f ? $" path{_motion.PathScale:F2}" : "")}"
        : _motion.Blending ? "procedural (blend)" : "procedural";
}
