using System;
using FootballSim.Anim;
using Godot;
using NQuat = System.Numerics.Quaternion;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace FootballSim.Game;

/// <summary>
/// --keeper-anim mocap: drives the keeper's rigged body from motion-captured clips (milestone 2: his stance and movement states). Each
/// physics tick: the figure's frame goes to the chooser (state → clip set), <see cref="KeeperMotion"/> searches, plays, blends, places
/// and foot-locks the clip pose, and the bone rotations go to <see cref="SkinnedBody.PoseBones(ReadOnlySpan{Quaternion}, Vector3, ReadOnlySpan{Vector3})"/>.
/// States without a set keep the procedural pose (PlayerFigure.Keeper.cs), solved by the body as before; every switch between the two
/// is blended at the bone rotations, so nothing pops. The clip data is read once per run from res://assets/anim/keeper. No clock and no
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
            _db = new PoseDatabase(lib, KeeperChooser.Sets, KeeperChooser.WeightsFor);
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
    public void Teleport() => _motion.Teleport();

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
        var input = new MotionInput
        {
            Position = new NVec2(pos.X, pos.Z), Velocity = new NVec2(vel.X, vel.Z), Yaw = ModelSpace.YawFromFacing(f.Facing),
            Set = KeeperChooser.SetFor(f.KeeperAction, f.KeeperDive), Scale = scale, Dt = f.Dt > 0 ? f.Dt : 1f / 120f,
            SquareChest = KeeperChooser.SquaresChest(f.KeeperAction),
        };
        input.Stance = StanceFrom(j, input, scale, KeeperChooser.StanceWeight(f.KeeperAction));
        _motion.Step(input, _procR, _procP, _outR, _outP);
        _ticks++;
        if (_log && (_motion.SwitchCount != _lastSwitches || _motion.PlayingClip != _lastPlaying))
            GD.Print($"KEEPER_MM tick={_ticks} action={f.KeeperAction} set={input.Set ?? "-"} now={Describe()} rate={_motion.Rate:F2} speed={f.Velocity.Length():F2}");
        _lastSwitches = _motion.SwitchCount; _lastPlaying = _motion.PlayingClip;
        for (int k = 0; k < _outR.Length; k++)
        {
            _showR[k] = new Quaternion(_outR[k].X, _outR[k].Y, _outR[k].Z, _outR[k].W);
            _showP[k] = new Vector3(_outP[k].X, _outP[k].Y, _outP[k].Z);
        }
        _body.PoseBones(_showR, _showP[0], _showP);
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
    public string Describe() => _motion.ClipName is string c ? $"{c} f{_motion.ClipFrame}{(_motion.Blending ? " (blend)" : "")}" : _motion.Blending ? "procedural (blend)" : "procedural";
}
