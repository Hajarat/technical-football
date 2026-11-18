using System;
using System.Collections.Generic;
using System.Text.Json;
using FootballSim.Anim;
using Godot;

namespace FootballSim.Game;

/// <summary>
/// --clip-gallery &lt;dir&gt;: the keeper clip audition. Plays every clip in res://assets/anim/keeper (clips.json) on keeper.glb,
/// without the simulation, and writes per clip a contact sheet &lt;dir&gt;/&lt;clip&gt;.png: six evenly spaced moments plus the tagged
/// event frames, side view (from his right) over front view, each tile labelled with the clip, view and frame. With --video it also
/// writes every frame (side and front next to each other) to &lt;dir&gt;/video/&lt;clip&gt;/f_NNNN.png for tools/anim/encode_video.py.
/// Each clip is first played through at the physics tick (120 Hz, frames interpolated) and checked; one CLIP_CHECK line per clip
/// reports non-finite values, the lowest bone joint and the fastest bone turn (deg/s; a pop shows as well over 1,500).
/// --clip-only a,b limits the run to those clips.
/// </summary>
public sealed class ClipGallery
{
    const float Dt = 1f / 120f;
    const int Tile = 480, Crop = 720;
    const float PopDegPerSec = 1500f;
    static readonly string[] Views = { "side", "front" };
    // The glTF model faces +Z with its left on +X; placed so it faces world +X (as SkinnedBody.Pose places it).
    static readonly Basis ModelToWorld = new(new Vector3(0, 0, -1), Vector3.Up, new Vector3(1, 0, 0));

    sealed class Entry
    {
        public Clip Clip = null!;
        public string Set = "", Side = "";
        public int[] Source = Array.Empty<int>();
        public readonly List<(int Frame, string Tag)> Moments = new();
    }

    readonly Match _m;
    readonly string _dir;
    readonly bool _video;
    readonly SkinnedBody _body;
    readonly Label _label;
    readonly Vector3 _origin;
    readonly List<Entry> _clips = new();
    readonly System.Numerics.Quaternion[] _sample;
    readonly Quaternion[] _local, _prevLocal;

    int _ci = -1, _mi, _vf, _view, _settle, _inFlight;
    enum Stage { Start, Sheet, Video, Next, Done }
    Stage _stage = Stage.Start;
    Image? _sheet, _frame;
    bool _failed;

    public ClipGallery(Match m, string dir, bool video, HashSet<string> only, float goalLineX)
    {
        _m = m; _dir = dir; _video = video;
        _origin = new Vector3(goalLineX * 0.4f, 0, 0);
        m.Camera.Fov = 38f; m.Camera.Near = 0.2f;

        var body = SkinnedBody.TryLoad("res://assets/characters/keeper.glb", out string err);
        if (body == null) { GD.PushError($"--clip-gallery: {err}"); _failed = true; _body = null!; }
        else { _body = body; m.AddChild(_body); _body.ApplyKit(FigureKit.Keeper); _body.Transform = new Transform3D(ModelToWorld, _origin); }

        var layer = new CanvasLayer { Layer = 10 };
        m.AddChild(layer);
        _label = new Label
        {
            Position = new Vector2((1280 - Crop) / 2 + 12, 8),
            LabelSettings = new LabelSettings { FontSize = 30, OutlineSize = 7, OutlineColor = new Color(0, 0, 0, 0.9f), FontColor = Colors.White },
        };
        layer.AddChild(_label);

        Load(only);
        int bones = _clips.Count > 0 ? _clips[0].Clip.BoneCount : 22;
        _sample = new System.Numerics.Quaternion[bones];
        _local = new Quaternion[bones]; _prevLocal = new Quaternion[bones];
        if (!_failed && _clips.Count > 0)
        {
            var names = _body.DrivenBoneNames;
            for (int k = 0; k < bones; k++)
                if (k >= names.Count || names[k] != _clips[0].Clip.Bones[k])
                {
                    GD.PushError($"--clip-gallery: clip bone {k} is {_clips[0].Clip.Bones[k]}, the body drives {(k < names.Count ? names[k] : "nothing")}");
                    _failed = true; break;
                }
        }
        if (_clips.Count == 0) { GD.PushError("--clip-gallery: no clips found"); _failed = true; }
        if (_video) DirAccess.MakeDirRecursiveAbsolute(System.IO.Path.Combine(_dir, "video"));
    }

    void Load(HashSet<string> only)
    {
        string folder = ProjectSettings.GlobalizePath("res://assets/anim/keeper");
        string index = System.IO.Path.Combine(folder, "clips.json");
        if (!System.IO.File.Exists(index)) { GD.PushError($"--clip-gallery: {index} not found"); return; }
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(index));
        foreach (var c in doc.RootElement.GetProperty("clips").EnumerateArray())
        {
            string name = c.GetProperty("name").GetString()!;
            if (only.Count > 0 && !only.Contains(name)) continue;
            var e = new Entry
            {
                Clip = ClipReader.Read(System.IO.Path.Combine(folder, c.GetProperty("file").GetString()!)),
                Set = c.GetProperty("set").GetString() ?? "",
                Side = c.GetProperty("side").GetString() ?? "",
            };
            if (c.TryGetProperty("frames", out var fr) && fr.ValueKind == JsonValueKind.Array)
                e.Source = new[] { fr[0].GetInt32(), fr[1].GetInt32() };
            int n = e.Clip.FrameCount;
            for (int i = 0; i < 6; i++) e.Moments.Add(((int)MathF.Round(i * (n - 1) / 5f), ""));
            foreach (var (tag, f) in e.Clip.Events) e.Moments.Add((Math.Clamp(f, 0, n - 1), tag));
            e.Moments.Sort((a, b) => a.Frame != b.Frame ? a.Frame.CompareTo(b.Frame) : string.CompareOrdinal(b.Tag, a.Tag));
            for (int i = e.Moments.Count - 1; i > 0; i--)   // an event on an evenly spaced moment replaces it
                if (e.Moments[i].Frame == e.Moments[i - 1].Frame) e.Moments.RemoveAt(e.Moments[i].Tag.Length == 0 ? i : i - 1);
            _clips.Add(e);
        }
    }

    Entry Cur => _clips[_ci];

    /// <summary>Advances one physics tick. Returns true when every clip is done and every file written.</summary>
    public bool Step()
    {
        if (_failed) return true;
        if (_inFlight > 0) return false;
        if (_settle > 0) { _settle--; if (_settle == 0) Grab(); return false; }
        switch (_stage)
        {
            case Stage.Start:
            case Stage.Next:
                if (_ci >= 0) Finish();
                if (++_ci >= _clips.Count) { _stage = Stage.Done; return false; }
                Check();
                _mi = 0; _view = 0; _vf = 0;
                _sheet = null;
                _stage = Stage.Sheet;
                Show(Cur.Moments[0].Frame, 0);
                return false;
            case Stage.Sheet:
                if (_view == 0) { _view = 1; Show(Cur.Moments[_mi].Frame, 1); return false; }
                _view = 0;
                if (++_mi < Cur.Moments.Count) { Show(Cur.Moments[_mi].Frame, 0); return false; }
                SaveSheet();
                if (_video) { _stage = Stage.Video; _vf = 0; Show(0, 0); }
                else _stage = Stage.Next;
                return false;
            case Stage.Video:
                if (_view == 0) { _view = 1; Show(_vf, 1); return false; }
                _view = 0;
                SaveFrame();
                if (++_vf < Cur.Clip.FrameCount) { Show(_vf, 0); return false; }
                _stage = Stage.Next;
                return false;
            default:
                return true;
        }
    }

    /// <summary>Poses clip frame <paramref name="frame"/>, aims the camera for <paramref name="view"/> and captures after a settle tick.</summary>
    void Show(int frame, int view)
    {
        var clip = Cur.Clip;
        var hips = Pose(clip, frame / (float)clip.Fps);
        _body.ResetInterpolation();
        // look at the body's middle: halfway between the hips and the lowest point, so a lying keeper stays centred
        Vector3 hw = _body.Transform * hips;
        float look = Math.Clamp(hw.Y * 0.85f, 0.35f, 1.6f);
        var at = new Vector3(hw.X, look, hw.Z);
        Vector3 eye = view == 0 ? at + new Vector3(0, 0.4f, 4.4f) : at + new Vector3(4.4f, 0.4f, 0);
        _m.Camera.Transform = new Transform3D(Basis.Identity, eye).LookingAt(at, Vector3.Up);
        _m.Camera.ResetPhysicsInterpolation();
        string tag = "";
        if (_stage == Stage.Sheet) tag = Cur.Moments[_mi].Tag;
        else foreach (var (t, f) in clip.Events) if (f == frame) tag = t;
        string src = Cur.Source.Length == 2 ? $"  src {Cur.Source[0] + frame * 2}" : "";
        _label.Text = $"{clip.Name}\n{Views[view].ToUpperInvariant()}  f {frame}/{clip.FrameCount - 1}{src}  {frame / (float)clip.Fps:F2}s{(tag.Length > 0 ? "  [" + tag + "]" : "")}";
        _settle = 1;
    }

    Vector3 Pose(Clip clip, float t)
    {
        var h = clip.Sample(t, _sample);
        for (int k = 0; k < _sample.Length; k++) _local[k] = new Quaternion(_sample[k].X, _sample[k].Y, _sample[k].Z, _sample[k].W);
        var hips = new Vector3(h.X, h.Y, h.Z);
        _body.PoseBones(_local, hips);
        return hips;
    }

    void Grab()
    {
        _inFlight++;
        Capture(_stage, _view);
    }

    async void Capture(Stage stage, int view)
    {
        await _m.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var img = _m.GetViewport().GetTexture().GetImage();
        int w = img.GetWidth(), h = img.GetHeight();
        int side = Math.Min(Math.Min(w, h), Crop * h / 720);
        var crop = img.GetRegion(new Rect2I((w - side) / 2, (h - side) / 2, side, side));
        crop.Resize(Tile, Tile, Image.Interpolation.Lanczos);
        if (stage == Stage.Sheet)
        {
            int cols = Cur.Moments.Count;
            _sheet ??= Image.CreateEmpty(cols * Tile, 2 * Tile, false, crop.GetFormat());
            _sheet.BlitRect(crop, new Rect2I(0, 0, Tile, Tile), new Vector2I(_mi * Tile, view * Tile));
        }
        else
        {
            _frame ??= Image.CreateEmpty(2 * Tile, Tile, false, crop.GetFormat());
            _frame.BlitRect(crop, new Rect2I(0, 0, Tile, Tile), new Vector2I(view * Tile, 0));
        }
        _inFlight--;
    }

    void SaveSheet()
    {
        if (_sheet == null) return;
        string path = System.IO.Path.Combine(_dir, Cur.Clip.Name + ".png");
        var err = _sheet.SavePng(path);
        GD.Print($"SHEET {Cur.Clip.Name} {path} moments={Cur.Moments.Count} err={err}");
    }

    void SaveFrame()
    {
        if (_frame == null) return;
        string dir = System.IO.Path.Combine(_dir, "video", Cur.Clip.Name);
        if (_vf == 0) DirAccess.MakeDirRecursiveAbsolute(dir);
        _frame.SavePng(System.IO.Path.Combine(dir, $"f_{_vf:D4}.png"));
    }

    void Finish()
    {
        if (_video) GD.Print($"VIDEO_FRAMES {Cur.Clip.Name} {System.IO.Path.Combine(_dir, "video", Cur.Clip.Name)} frames={Cur.Clip.FrameCount}");
    }

    /// <summary>Plays the clip through at the physics tick (no rendering) and prints CLIP_CHECK.</summary>
    void Check()
    {
        var clip = Cur.Clip;
        bool bad = false;
        float lowest = float.MaxValue, maxSpeed = 0f;
        string lowWhere = "", speedWhere = "";
        int ticks = (int)MathF.Ceiling(clip.Duration / Dt);
        var names = clip.Bones;
        for (int i = 0; i <= ticks; i++)
        {
            float t = Math.Min(i * Dt, (clip.FrameCount - 1) / (float)clip.Fps);
            var hips = Pose(clip, t);
            if (!float.IsFinite(hips.X) || !float.IsFinite(hips.Y) || !float.IsFinite(hips.Z)) bad = true;
            for (int k = 0; k < _local.Length; k++)
            {
                var q = _local[k];
                if (!float.IsFinite(q.X) || !float.IsFinite(q.Y) || !float.IsFinite(q.Z) || !float.IsFinite(q.W)) bad = true;
                float y = _body.DrivenBoneOrigin(k).Y;
                if (y < lowest) { lowest = y; lowWhere = $"{Short(names[k])}@{t:F2}s"; }
                if (i > 0)
                {
                    float d = Math.Min(1f, Math.Abs(q.Normalized().Dot(_prevLocal[k].Normalized())));
                    float speed = Mathf.RadToDeg(2f * MathF.Acos(d)) / Dt;
                    if (speed > maxSpeed) { maxSpeed = speed; speedWhere = $"{Short(names[k])}@{t:F2}s"; }
                }
                _prevLocal[k] = q;
            }
        }
        GD.Print($"CLIP_CHECK clip={clip.Name} set={Cur.Set} side={Cur.Side} frames={clip.FrameCount} seconds={clip.Duration:F2} non_finite={bad} " +
                 $"lowest_joint_m={lowest:F3} at={lowWhere} max_bone_deg_per_s={maxSpeed:F0} at={speedWhere}{(maxSpeed > PopDegPerSec ? " POP" : "")}");
    }

    static string Short(string bone) => bone.StartsWith("mixamorig_") ? bone["mixamorig_".Length..] : bone;
}
