using System;
using System.Collections.Generic;
using System.Text.Json;
using FootballSim.Anim;
using Godot;

namespace FootballSim.Game;

/// <summary>
/// --clip-gallery &lt;dir&gt;: the clip audition. Plays every clip in --clip-folder (default res://assets/anim/keeper, clips.json) on --clip-model
/// (default keeper.glb; the attacker's clips: --clip-folder res://assets/anim/attacker --clip-model res://assets/characters/player.glb),
/// without the simulation, and writes per clip a contact sheet &lt;dir&gt;/&lt;clip&gt;.png: six evenly spaced moments plus the tagged
/// event frames, side view (from his right) over front view, each tile labelled with the clip, view and frame. With --video it also
/// writes every frame (side and front next to each other) to &lt;dir&gt;/video/&lt;clip&gt;/f_NNNN.png for tools/anim/encode_video.py.
/// Each clip is first played through at the physics tick (120 Hz, frames interpolated) and checked; one CLIP_CHECK line per clip
/// reports non-finite values, the lowest bone joint and the fastest bone turn (deg/s; a pop shows as well over 1,500).
/// --clip-only a,b limits the run to those clips.
/// A clip whose clips.json entry carries a ball track (the authored skill moves, tools/anim/author.py) is shown with its ball and
/// from a third view, the game camera's angle (zoomed in); its sheet has a moment every 0.05 s plus the events, a second sheet
/// &lt;clip&gt;_contact.png has one tile per physics tick around the "contact" event, and with --video it also writes a 0.25x slow
/// version (&lt;dir&gt;/video/&lt;clip&gt;_slowmo, four frames per clip frame). Its cameras stay still for the whole clip, so the body's
/// and the ball's travel show.
/// </summary>
public sealed class ClipGallery
{
    const float Dt = 1f / 120f;
    const int Tile = 480, Crop = 720;
    const float PopDegPerSec = 1500f;
    static readonly string[] Views = { "side", "front", "game" };
    const float SkillMomentSeconds = 0.05f;
    const int StripTicks = 8;      // ticks either side of the contact in the per-tick strip
    const int SlowFactor = 4;
    // The match camera (CameraRig: 6.2 m up, 15.5 m back on +Z, looking at the ball), its field of view narrowed so the body fills
    // the tile as in the other views.
    static readonly Vector3 GameEye = new(0, 6.2f, 15.5f);
    // The glTF model faces +Z with its left on +X; placed so it faces world +X (as SkinnedBody.Pose places it).
    static readonly Basis ModelToWorld = new(new Vector3(0, 0, -1), Vector3.Up, new Vector3(1, 0, 0));

    sealed class Entry
    {
        public Clip Clip = null!;
        public string Set = "", Side = "";
        public int[] Source = Array.Empty<int>();
        public readonly List<(int Frame, string Tag)> Moments = new();
        public Vector3[]? Ball;          // model space, one per clip frame
        public Basis[]? BallSpin;        // the ball's rolling orientation per clip frame
        public float BallRadius;
        public Vector3 Aim;              // a skill clip's views hold still on the middle of its hips' path, so the travel shows
        public int Views => Ball != null ? 3 : 2;
    }

    enum Out { Sheet, Strip, Video, Slow }
    readonly record struct Shot(Out Out, float T, int View, int Col, string Tag);

    readonly Match _m;
    readonly string _dir, _folder;
    readonly bool _video;
    readonly SkinnedBody _body;
    readonly Label _label;
    readonly Vector3 _origin;
    readonly List<Entry> _clips = new();
    readonly System.Numerics.Quaternion[] _sample;
    readonly Quaternion[] _local, _prevLocal;

    int _ci = -1, _si, _settle, _inFlight;
    readonly List<Shot> _shots = new();
    Image? _sheet, _strip, _frame;
    bool _failed, _done;
    readonly MeshInstance3D _ball;

    public ClipGallery(Match m, string dir, bool video, HashSet<string> only, float goalLineX,
        string folder = "res://assets/anim/keeper", string model = "res://assets/characters/keeper.glb")
    {
        _m = m; _dir = dir; _video = video; _folder = folder;
        _origin = new Vector3(goalLineX * 0.4f, 0, 0);
        m.Camera.Fov = 38f; m.Camera.Near = 0.2f;

        var body = SkinnedBody.TryLoad(model, out string err);
        if (body == null) { GD.PushError($"--clip-gallery: {err}"); _failed = true; _body = null!; }
        else { _body = body; m.AddChild(_body); _body.ApplyKit(model.EndsWith("keeper.glb") ? FigureKit.Keeper : FigureKit.Attacker); _body.Transform = new Transform3D(ModelToWorld, _origin); }

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
        _ball = new MeshInstance3D { Name = "GalleryBall", Visible = false, Mesh = new SphereMesh { Radius = 0.11f, Height = 0.22f, RadialSegments = 24, Rings = 12 }, MaterialOverride = BallMaterial() };
        m.AddChild(_ball);
        if (_video) DirAccess.MakeDirRecursiveAbsolute(System.IO.Path.Combine(_dir, "video"));
    }

    void Load(HashSet<string> only)
    {
        string folder = ProjectSettings.GlobalizePath(_folder);
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
            if (c.TryGetProperty("ball", out var ball) && ball.TryGetProperty("track", out var track) && track.GetArrayLength() == n)
            {
                e.BallRadius = ball.TryGetProperty("radius", out var rr) ? rr.GetSingle() : 0.11f;
                e.Ball = new Vector3[n];
                for (int i = 0; i < n; i++) e.Ball[i] = new Vector3(track[i][0].GetSingle(), track[i][1].GetSingle(), track[i][2].GetSingle());
                e.BallSpin = RollSpin(e.Ball, e.BallRadius);
                var tmp = new System.Numerics.Quaternion[e.Clip.BoneCount];
                var lo = e.Clip.Sample(0f, tmp); var hi = e.Clip.Sample((n - 1) / (float)e.Clip.Fps, tmp);
                e.Aim = new Vector3((lo.X + hi.X) * 0.5f, (lo.Y + hi.Y) * 0.5f, (lo.Z + hi.Z) * 0.5f);
            }
            if (e.Ball != null)
            {
                int step = Math.Max(1, (int)MathF.Round(SkillMomentSeconds * e.Clip.Fps));
                for (int f = 0; f < n; f += step) e.Moments.Add((f, ""));
                if ((n - 1) % step != 0) e.Moments.Add((n - 1, ""));
            }
            else
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
        if (_done) return true;
        if (_ci < 0 || _si >= _shots.Count)
        {
            if (_ci >= 0) Finish();
            if (++_ci >= _clips.Count) { _done = true; return false; }
            Check();
            Plan();
            _si = 0;
        }
        var shot = _shots[_si];
        Show(shot);
        return false;
    }

    /// <summary>Every capture of the current clip, in order: the sheet (each moment in every view), the per-tick contact strip of a
    /// skill clip, then with --video each frame (all views side by side) and, for a skill clip, the slow version.</summary>
    void Plan()
    {
        _shots.Clear();
        _sheet = null; _strip = null;
        var e = Cur;
        float fps = e.Clip.Fps;
        for (int mi = 0; mi < e.Moments.Count; mi++)
            for (int v = 0; v < e.Views; v++) _shots.Add(new Shot(Out.Sheet, e.Moments[mi].Frame / fps, v, mi, e.Moments[mi].Tag));
        if (e.Ball != null && e.Clip.Events.TryGetValue("contact", out int cf))
        {
            for (int k = -StripTicks; k <= StripTicks; k++)
            {
                float t = Math.Clamp(cf / fps + k * Dt, 0f, (e.Clip.FrameCount - 1) / fps);
                for (int v = 0; v < e.Views; v++) _shots.Add(new Shot(Out.Strip, t, v, k + StripTicks, k == 0 ? "contact" : $"{(k > 0 ? "+" : "")}{k} tick"));
            }
        }
        if (_video)
        {
            for (int f = 0; f < e.Clip.FrameCount; f++)
                for (int v = 0; v < e.Views; v++) _shots.Add(new Shot(Out.Video, f / fps, v, f, ""));
            if (e.Ball != null)
                for (int f = 0; f <= (e.Clip.FrameCount - 1) * SlowFactor; f++)
                    for (int v = 0; v < e.Views; v++) _shots.Add(new Shot(Out.Slow, f / (fps * SlowFactor), v, f, ""));
        }
    }

    /// <summary>Poses the clip at the shot's time, aims the camera for its view and captures after a settle tick.</summary>
    void Show(Shot shot)
    {
        var clip = Cur.Clip;
        var hips = Pose(clip, shot.T);
        _body.ResetInterpolation();
        PlaceBall(shot.T);
        // look at the body's middle: halfway between the hips and the lowest point, so a lying keeper stays centred
        Vector3 hw = _body.Transform * (Cur.Ball != null ? Cur.Aim : hips);
        float look = Math.Clamp(hw.Y * 0.85f, 0.35f, 1.6f);
        var at = new Vector3(hw.X, look, hw.Z);
        Vector3 eye = shot.View switch { 0 => at + new Vector3(0, 0.4f, 4.4f), 1 => at + new Vector3(4.4f, 0.4f, 0), _ => at + GameEye };
        // the game view stands as far off as the match camera; its narrower field of view frames the same height of body (3 m)
        _m.Camera.Fov = shot.View == 2 ? 2f * Mathf.RadToDeg(MathF.Atan(4.4f * MathF.Tan(Mathf.DegToRad(19f)) / GameEye.Length())) : 38f;
        _m.Camera.Transform = new Transform3D(Basis.Identity, eye).LookingAt(at, Vector3.Up);
        _m.Camera.ResetPhysicsInterpolation();
        string tag = shot.Tag;
        int frame = (int)MathF.Round(shot.T * clip.Fps);
        if (shot.Out is Out.Video or Out.Slow && MathF.Abs(frame - shot.T * clip.Fps) < 1e-3f)
            foreach (var (t, f) in clip.Events) if (f == frame) tag = t;
        string src = Cur.Source.Length == 2 ? $"  src {Cur.Source[0] + frame * 2}" : "";
        string at60 = MathF.Abs(frame - shot.T * clip.Fps) < 1e-3f ? $"f {frame}/{clip.FrameCount - 1}" : $"f {shot.T * clip.Fps:F1}/{clip.FrameCount - 1}";
        string slow = shot.Out == Out.Slow ? "  0.25x" : "";
        _label.Text = $"{clip.Name}\n{Views[shot.View].ToUpperInvariant()}  {at60}{src}  {shot.T:F3}s{slow}{(tag.Length > 0 ? "  [" + tag + "]" : "")}";
        _settle = 1;
    }

    void PlaceBall(float t)
    {
        var e = Cur;
        if (e.Ball == null || e.BallSpin == null) { _ball.Visible = false; return; }
        float x = Math.Clamp(t * e.Clip.Fps, 0f, e.Ball.Length - 1);
        int a = (int)MathF.Floor(x), b = Math.Min(a + 1, e.Ball.Length - 1);
        float u = x - a;
        Vector3 p = e.Ball[a].Lerp(e.Ball[b], u);
        Basis r = new Basis(e.BallSpin[a].GetRotationQuaternion().Slerp(e.BallSpin[b].GetRotationQuaternion(), u));
        float s = e.BallRadius / 0.11f;
        _ball.Transform = new Transform3D(ModelToWorld * r * Basis.FromScale(new Vector3(s, s, s)), _body.Transform * p);
        _ball.Visible = true;
        _ball.ResetPhysicsInterpolation();
    }

    /// <summary>The ball's orientation per frame, rolling without slipping along its track over the ground.</summary>
    static Basis[] RollSpin(Vector3[] track, float r)
    {
        var o = new Basis[track.Length];
        var cur = Basis.Identity;
        o[0] = cur;
        for (int i = 1; i < track.Length; i++)
        {
            var d = track[i] - track[i - 1];
            d.Y = 0;
            float len = d.Length();
            if (len > 1e-6f) cur = new Basis(Vector3.Up.Cross(d / len).Normalized(), len / r) * cur;
            o[i] = cur.Orthonormalized();
        }
        return o;
    }

    static StandardMaterial3D BallMaterial()
    {
        // two-tone panels so the roll shows
        var img = Image.CreateEmpty(64, 32, false, Image.Format.Rgb8);
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 64; x++)
                img.SetPixel(x, y, ((x / 8) + (y / 8)) % 2 == 0 ? new Color(0.95f, 0.95f, 0.95f) : new Color(0.12f, 0.12f, 0.14f));
        return new StandardMaterial3D { AlbedoTexture = ImageTexture.CreateFromImage(img), Roughness = 0.6f };
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
        Capture(_shots[_si]);
    }

    async void Capture(Shot shot)
    {
        await _m.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var img = _m.GetViewport().GetTexture().GetImage();
        int w = img.GetWidth(), h = img.GetHeight();
        int side = Math.Min(Math.Min(w, h), Crop * h / 720);
        var crop = img.GetRegion(new Rect2I((w - side) / 2, (h - side) / 2, side, side));
        crop.Resize(Tile, Tile, Image.Interpolation.Lanczos);
        var e = Cur;
        var dst = new Vector2I(shot.Col * Tile, shot.View * Tile);
        switch (shot.Out)
        {
            case Out.Sheet:
                _sheet ??= Image.CreateEmpty(e.Moments.Count * Tile, e.Views * Tile, false, crop.GetFormat());
                _sheet.BlitRect(crop, new Rect2I(0, 0, Tile, Tile), dst);
                break;
            case Out.Strip:
                _strip ??= Image.CreateEmpty((2 * StripTicks + 1) * Tile, e.Views * Tile, false, crop.GetFormat());
                _strip.BlitRect(crop, new Rect2I(0, 0, Tile, Tile), dst);
                break;
            default:
                _frame ??= Image.CreateEmpty(3 * Tile, Tile, false, crop.GetFormat());
                if (shot.View == 0) _frame.Fill(Colors.Black);
                _frame.BlitRect(crop, new Rect2I(0, 0, Tile, Tile), new Vector2I(shot.View * Tile, 0));
                break;
        }
        // the last view of a moment, frame or the last shot of a sheet: write what is complete
        bool lastView = shot.View == e.Views - 1;
        bool lastOfKind = _si + 1 >= _shots.Count || _shots[_si + 1].Out != shot.Out;
        if (lastView && shot.Out is Out.Video or Out.Slow) SaveFrame(shot, e.Views);
        if (lastOfKind && shot.Out == Out.Sheet) SaveSheet(_sheet, "");
        if (lastOfKind && shot.Out == Out.Strip) SaveSheet(_strip, "_contact");
        _si++;
        _inFlight--;
    }

    void SaveSheet(Image? sheet, string suffix)
    {
        if (sheet == null) return;
        string path = System.IO.Path.Combine(_dir, Cur.Clip.Name + suffix + ".png");
        var err = sheet.SavePng(path);
        GD.Print($"SHEET {Cur.Clip.Name}{suffix} {path} moments={sheet.GetWidth() / Tile} err={err}");
    }

    void SaveFrame(Shot shot, int views)
    {
        if (_frame == null) return;
        string dir = System.IO.Path.Combine(_dir, "video", Cur.Clip.Name + (shot.Out == Out.Slow ? "_slowmo" : ""));
        if (shot.Col == 0) DirAccess.MakeDirRecursiveAbsolute(dir);
        var img = views == 3 ? _frame : _frame.GetRegion(new Rect2I(0, 0, views * Tile, Tile));
        img.SavePng(System.IO.Path.Combine(dir, $"f_{shot.Col:D4}.png"));
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
