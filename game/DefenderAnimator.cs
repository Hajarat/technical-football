using System;
using FootballSim.Anim;
using FootballSim.Core;
using Godot;
using NQuat = System.Numerics.Quaternion;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace FootballSim.Game;

/// <summary>
/// --defender-anim mocap: draws an outfield player's defending from the motion clips in res://assets/anim/defender, over the body
/// the figure has already posed this tick (its run cycles and the procedural defending poses). The chooser turns the core's action
/// into a clip set (<see cref="DefenderChooser"/>): his stance and its steps, the lunge, the block, the slide and the lying after
/// it, getting up, the stumble; for the attacker the reactions (stumble, barged, fall). <see cref="KeeperMotion"/>, the keeper's
/// engine, searches the set, plays and blends the clip, keeps its planted feet where they landed and brings the tackling foot onto
/// the core's foot point by IK, with a tackle's contact timed onto the core's contact. An action with no set, or a set with no kept
/// clip, stays as the figure drew it; every hand-over is blended at the bone rotations. The clips are read once per run. No clock
/// and no randomness: the same replay draws the same poses.
/// </summary>
public sealed class DefenderAnimator
{
    const string ClipFolder = "res://assets/anim/defender";

    static PoseDatabase? _db;
    static string? _loadError;

    readonly SkinnedBody _body;
    readonly KeeperMotion _motion;
    readonly NQuat[] _procR, _outR;
    readonly NVec3[] _procP, _outP;
    readonly Quaternion[] _showR;
    readonly Vector3[] _showP;
    readonly bool _log = ToolArgs.Current?.DefenderAnimLog == true;
    readonly string _who;
    int _ticks, _lastSwitches;
    bool _lastPlaying;
    string? _set;
    float _setTime;        // seconds since the set now playing was entered
    bool _moving;
    float _returnT = 1e9f; // seconds since a free root (a lunge, a fall) was let go
    float _footW;          // the reaching foot's IK weight, eased
    int _footSide = 1;

    /// <summary>The baked slide's foot meets the ball this long after it starts (its contact event, seconds).</summary>
    public const float SlideContactSeconds = 0.28f;
    /// <summary>The reaching foot's IK: weight 0 → 1 over this long before the contact, let go over <see cref="ReleaseSeconds"/> after the hold.</summary>
    public const float FootRampSeconds = 0.2f, ReleaseSeconds = 0.2f;
    /// <summary>A get-up may play at up to this rate to be up on the core's tick (its get-up is half a second, the recorded ones seconds).</summary>
    public const float GetUpWarpMax = 2.6f;
    /// <summary>The get-up clip's "up" lands this far through the core's get-up.</summary>
    public const float GetUpUpShare = 0.8f;
    /// <summary>A lunge and a block may be played this much slower or faster to land their contact on the core's tick.</summary>
    public const float TackleWarpMin = 0.6f, TackleWarpMax = 2.2f;

    DefenderAnimator(SkinnedBody body, PoseDatabase db, string who)
    {
        _body = body; _who = who;
        _motion = new KeeperMotion(db);
        int n = db.Skeleton.Count;
        _procR = new NQuat[n]; _outR = new NQuat[n]; _procP = new NVec3[n]; _outP = new NVec3[n];
        _showR = new Quaternion[n]; _showP = new Vector3[n];
    }

    public SkinnedBody Body => _body;
    public KeeperMotion Motion => _motion;
    /// <summary>True while a clip (or the blend out of one) shows instead of the figure's own pose.</summary>
    public bool Showing { get; private set; }
    /// <summary>The set chosen on the latest tick (null: the figure's own pose).</summary>
    public string? Set => _set;

    /// <summary>The animator for <paramref name="body"/>, or null (and the reason) when the clips are missing or do not fit the body.</summary>
    public static DefenderAnimator? TryCreate(SkinnedBody body, string who, out string error)
    {
        var db = Database(out error);
        if (db == null) return null;
        var names = body.DrivenBoneNames;
        if (names.Count != db.Skeleton.Count) { error = $"the body drives {names.Count} bones, the clips {db.Skeleton.Count}"; return null; }
        for (int k = 0; k < names.Count; k++)
            if (names[k] != db.Skeleton.Names[k]) { error = $"clip bone {k} is {db.Skeleton.Names[k]}, the body drives {names[k]}"; return null; }
        return new DefenderAnimator(body, db, who);
    }

    /// <summary>The pose database, built once from the clip folder (the chooser's sets only; rejected clips left out).</summary>
    public static PoseDatabase? Database(out string error)
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
            var drop = ToolArgs.Current?.DefenderAnimDropSets;
            _db = new PoseDatabase(lib, DefenderChooser.Sets, DefenderChooser.WeightsFor, DefenderChooser.OptionsFor, true,
                drop != null && drop.Count > 0 ? drop : null);
            foreach (var s in _db.Sets) GD.Print($"DEFENDER_ANIM set={s.Name} clips={s.Clips.Count} frames={s.FrameCount}");
        }
        catch (Exception e)
        {
            _loadError = $"defender clips: {e.Message}";
            error = _loadError;
        }
        return _db;
    }

    /// <summary>Forget the history (round reset, seek): the next tick shows its pose with no blend from the last one.</summary>
    public void Teleport()
    {
        _motion.Teleport();
        _set = null; _setTime = 0f; _moving = false; _returnT = 1e9f; _footW = 0f; Showing = false;
    }

    /// <summary>
    /// One physics tick, after the figure has posed the body: reads that pose back as the fallback, steps the motion and, while a
    /// clip shows or blends, puts its pose on the body in its place. Returns whether it did.
    /// </summary>
    public bool Present(in FigureFrame f, float height)
    {
        int n = _procR.Length;
        for (int k = 0; k < n; k++)
        {
            var r = _body.DrivenLocalRotation(k); var p = _body.DrivenLocalPosition(k);
            _procR[k] = new NQuat(r.X, r.Y, r.Z, r.W);
            _procP[k] = new NVec3(p.X, p.Y, p.Z);
        }
        float scale = _body.ScaleFor(height);
        float dt = f.Dt > 0 ? f.Dt : 1f / 120f;
        var pos = ModelSpace.FromWorld(new NVec3(f.Position.X, f.Position.Y, f.Position.Z)) / scale;
        var vel = ModelSpace.FromWorld(new NVec3(f.Velocity.X, 0, f.Velocity.Z)) / scale;
        var d = f.Defender;

        string? set = null;
        if (d.Valid)
        {
            _moving = DefenderChooser.Moving(new Vector2(f.Velocity.X, f.Velocity.Z).Length(), _moving);
            set = d.Reaction != Reaction.None ? DefenderChooser.SetFor(d.Reaction) : DefenderChooser.SetFor(d.Action, d.Jockeying, _moving);
        }
        // (no kept clip of being pushed sideways: he stumbles, the push itself carries him)
        if (set == DefenderChooser.Barged && _db?.Set(DefenderChooser.Barged) == null) set = DefenderChooser.Stumble;
        if (set != _set) { _set = set; _setTime = 0f; } else _setTime += dt;

        var input = new MotionInput
        {
            Position = new NVec2(pos.X, pos.Z), Velocity = new NVec2(vel.X, vel.Z), Yaw = ModelSpace.YawFromFacing(f.Facing),
            Set = set, Scale = scale, Dt = dt, SquareChest = DefenderChooser.SquaresChest(set),
        };
        Timing(ref input, d, set, scale, f);
        _motion.Step(input, _procR, _procP, _outR, _outP);
        _ticks++;
        if (_log && (_motion.SwitchCount != _lastSwitches || _motion.PlayingClip != _lastPlaying))
            GD.Print($"DEFENDER_MM who={_who} tick={_ticks} action={(d.Valid ? d.Action.ToString() : "-")} reaction={d.Reaction} set={set ?? "-"} now={Describe()} rate={_motion.Rate:F2} speed={f.Velocity.Length():F2} foot_w={_footW:F2}");
        _lastSwitches = _motion.SwitchCount; _lastPlaying = _motion.PlayingClip;

        Showing = _motion.PlayingClip || _motion.Blending || _footW > 1e-3f;
        if (!Showing) return false;
        for (int k = 0; k < n; k++)
        {
            _showR[k] = new Quaternion(_outR[k].X, _outR[k].Y, _outR[k].Z, _outR[k].W);
            _showP[k] = new Vector3(_outP[k].X, _outP[k].Y, _outP[k].Z);
        }
        _body.Repose(_showR, _showP[0], _showP);
        return true;
    }

    /// <summary>
    /// The set's timing, root and foot: a one-shot set is entered once and played through; a lunge's, block's and slide's contact
    /// and a get-up's "up" land on the core's time; the root is the simulation's except through a lunge (he is planted there while
    /// the clip steps in and back) and a fall (he lies where it leaves him); the tackling or blocking foot goes onto the core's
    /// foot point by IK, its weight rising into the contact and let go after it.
    /// </summary>
    void Timing(ref MotionInput input, in FigureDefender d, string? set, float scale, in FigureFrame f)
    {
        float dt = input.Dt;
        bool lying = set == DefenderChooser.Slide && d.Action == DefenderAction.Grounded || set == DefenderChooser.Fall;
        input.Ground = lying || set == DefenderChooser.GetUp;
        input.HasTarget = DefenderChooser.OneShot(set);
        float rem = float.NaN;   // seconds to the foot's contact (negative after it)
        float hold = 0f;         // seconds the foot stays on its point after the contact
        int side = d.Side < 0f ? -1 : 1;
        Vector3 footAt = default;
        switch (set)
        {
            case DefenderChooser.Lunge:
                rem = (float.IsFinite(d.ContactTime) ? d.ContactTime : 0.25f) - d.ActionTime;
                hold = 0.1f; footAt = d.TackleFoot;
                input.TargetTime = rem; input.Side = side; input.WarpMin = TackleWarpMin; input.WarpMax = TackleWarpMax; input.EntryRate = 1f;
                break;
            case DefenderChooser.Slide:
                rem = SlideContactSeconds - _setTime;
                hold = d.Action == DefenderAction.Slide ? 1e3f : 0f; footAt = d.TackleFoot;
                input.TargetTime = rem; input.Side = side; input.EntryRate = 1f;
                break;
            case DefenderChooser.Block:
                float grow = d.BlockGrowSeconds > 0.05f ? d.BlockGrowSeconds : 0.4f;
                rem = grow - d.ActionTime;
                hold = 1e3f;
                // the leg out to the side the ball is on, as far as the core's leg reach has grown
                {
                    Vector3 fwd = new(MathF.Cos(f.Facing), 0, MathF.Sin(f.Facing)), right = new(-fwd.Z, 0, fwd.X);
                    footAt = f.Position + right * (side * Math.Max(0.2f, d.BlockReach * 0.92f)) + fwd * 0.1f;
                    footAt.Y = 0.12f;
                }
                input.TargetTime = rem; input.Side = side; input.WarpMin = TackleWarpMin; input.WarpMax = TackleWarpMax; input.EntryRate = 1f;
                break;
            case DefenderChooser.GetUp:
                input.TargetTime = GetUpUpShare * (d.GetUpSeconds > 0.05f ? d.GetUpSeconds : 0.5f) - d.ActionTime;
                input.WarpMax = GetUpWarpMax; input.EntryRate = GetUpWarpMax;
                break;
            case DefenderChooser.Barged:
                input.Side = d.ReactionSide < 0f ? -1 : d.ReactionSide > 0f ? 1 : 0;
                break;
        }

        // the root
        bool free = set is DefenderChooser.Lunge or DefenderChooser.Fall;
        if (free) { input.Root = RootMode.Free; _returnT = 0f; }
        else if (set == DefenderChooser.GetUp || _returnT < 0.5f) { input.Root = RootMode.Return; _returnT += set == DefenderChooser.GetUp ? 0f : dt; }
        else input.Root = RootMode.Follow;

        // the reaching foot
        float want = 0f;
        if (float.IsFinite(rem))
        {
            want = rem > 0f ? Math.Max(0f, 1f - rem / FootRampSeconds) : -rem <= hold ? 1f : Math.Max(0f, 1f - (-rem - hold) / ReleaseSeconds);
            if (set == DefenderChooser.Block) want *= 0.85f;
            _footSide = side; _footAt = ModelSpace.FromWorld(new NVec3(footAt.X, footAt.Y, footAt.Z)) / scale;
            _footW = want;
        }
        else if (_footW > 0f) _footW = Math.Max(0f, _footW - dt / ReleaseSeconds);   // the set left mid-reach: let go where it was
        if (_footW > 1e-4f)
        {
            if (_footSide < 0) { input.Feet.Left = _footAt; input.Feet.LeftWeight = _footW; }
            else { input.Feet.Right = _footAt; input.Feet.RightWeight = _footW; }
        }
    }

    NVec3 _footAt;

    /// <summary>Whether the foot (0 = left) is held planted by the foot lock this tick.</summary>
    public bool FootPlanted(int foot) => _motion.FootLock.Locked(foot);

    /// <summary>What is playing, for labels and logs: the clip and frame, or "figure".</summary>
    public string Describe() => _motion.ClipName is string c
        ? $"{c} f{_motion.ClipFrame}{(_motion.Blending ? " (blend)" : "")}{(_motion.Rate != 1f ? $" x{_motion.Rate:F2}" : "")}"
        : _motion.Blending ? "figure (blend)" : "figure";
}

public partial class PlayerFigure
{
    DefenderAnimator? _defAnim;
    bool _defAnimFailed;
    /// <summary>"mocap" (defending and the reactions to it from the clips where a set has one; needs the skinned body) or "procedural" (the poses of PlayerFigure.Defender.cs alone). Outfield only.</summary>
    public string DefenderAnimMode { get; set; } = "procedural";
    /// <summary>The clip layer while --defender-anim mocap is on and a frame has asked for it, else null.</summary>
    public DefenderAnimator? DefenderAnim => _defAnim;

    /// <summary>
    /// After the body is posed: the defending clips take it over where the chooser has a set (see <see cref="DefenderAnimator"/>).
    /// The animator is made on the first frame that carries a defending action or a reaction, so a round without a defender loads
    /// no clips.
    /// </summary>
    void DefenderPresent()
    {
        if (IsKeeper || _body == null || DefenderAnimMode != "mocap") { _defAnim = null; return; }
        if (_defAnim == null)
        {
            if (_defAnimFailed || !_animFrame.Defender.Valid) return;
            _defAnim = DefenderAnimator.TryCreate(_body, StatsName ?? "attacker", out string err);
            if (_defAnim == null) { _defAnimFailed = true; GD.PushWarning($"--defender-anim mocap unavailable, using the procedural poses: {err}"); return; }
            _defAnim.Teleport();
            // a first tick on the figure's own pose, so the clip that starts now is blended into from it
            var before = _animFrame; before.Defender = default;
            _defAnim.Present(before, H);
        }
        if (_teleporting) _defAnim.Teleport();
        _defAnim.Present(_animFrame, H);
    }
}
