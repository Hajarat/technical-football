using System;
using System.Collections.Generic;
using FootballSim.Anim;
using FootballSim.Core;
using Godot;

namespace FootballSim.Game;

/// <summary>
/// --defender-gallery &lt;dir&gt;: shows every defending action, and the attacker's reactions to it, without the simulation. It drives
/// the defender's figure (the attacker's for a reaction) from scripted frames, the same FigureFrame the match builds: his stance,
/// jockeying each way, back and closing down, a lunge with either foot and from a run, the slide with the lying and the get-up
/// after it, a block each side, a barge, a stumble; the attacker's stumble, barged and fall. Per script it writes a contact sheet
/// &lt;dir&gt;/&lt;script&gt;.png: a moment every 0.15 s or so, the side view (from his right) over the front view, each tile labelled with
/// the action and what is drawn (the clip and frame, or "figure" for the procedural pose). A DEFENDER_GALLERY line per script gives
/// the clips that played, the fastest bone turn (POP above 1,500 deg/s, with the number of such ticks), the lowest bone joint and,
/// for a lunge or slide, how far the tackling foot's ankle was from the core's foot point at the contact tick.
/// Run it with --defender-anim procedural for the poses the clips replace. --gallery-only a,b plays only the scripts whose names
/// start with one of those.
/// </summary>
public sealed class DefenderGallery
{
    const float Dt = 1f / 120f;
    const int Tile = 400, Crop = 720, MaxColumns = 14;
    const float PopDegPerSec = 1500f;
    static readonly string[] Views = { "side", "front" };

    sealed class Beat
    {
        public DefenderAction Action; public Reaction Reaction; public float Dur;
        /// <summary>His velocity at the step's start and end, along his facing and to his right (m/s), eased between.</summary>
        public Vector2 V0, V1;
        public bool Jockeying; public float Side;
        /// <summary>A lunge: seconds to its contact and how far the foot goes; a slide: how far ahead its foot is; a block: the leg's reach when grown.</summary>
        public float Contact = float.NaN, Reach;
    }

    sealed class Script
    {
        public string Name = ""; public bool Attacker;
        public readonly List<Beat> Steps = new();
        public float Total { get { float t = 0; foreach (var s in Steps) t += s.Dur; return t; } }
    }

    readonly Match _m;
    readonly string _dir;
    readonly Label _label;
    readonly List<Script> _scripts = new();
    readonly Vector3 _p0;
    const float Facing = 0f;
    static readonly Vector3 Fwd = new(1, 0, 0), Right = new(0, 0, 1);

    int _si = -1;
    Script _s = null!;
    float _t, _gait, _next, _every;
    Vector3 _pos;
    int _col, _view = -1, _settle, _inFlight;
    bool _done;
    Image? _sheet;
    FigureFrame _frame;
    // checks
    readonly List<string> _clips = new();
    Quaternion[]? _prevRot;
    float _boneSpeed, _low, _footErr;
    string _boneWhere = "", _lowWhere = "";
    int _pops;

    public DefenderGallery(Match m, string dir, float goalLineX)
    {
        _m = m; _dir = dir;
        _p0 = new Vector3(goalLineX * 0.15f, 0, -3f);   // clear of the pitch markings for the longest script
        m.Camera.Fov = 38f; m.Camera.Near = 0.2f;
        var layer = new CanvasLayer { Layer = 10 };
        m.AddChild(layer);
        _label = new Label
        {
            Position = new Vector2((1280 - Crop) / 2 + 12, 8),
            LabelSettings = new LabelSettings { FontSize = 26, OutlineSize = 7, OutlineColor = new Color(0, 0, 0, 0.9f), FontColor = Colors.White },
        };
        layer.AddChild(_label);
        Build();
        var only = ToolArgs.Current?.GalleryOnly;
        if (only != null && only.Count > 0) _scripts.RemoveAll(sc => !only.Exists(o => sc.Name.StartsWith(o, StringComparison.Ordinal)));
    }

    // ---- scripts ----

    static Beat St(DefenderAction a, float dur, float fwd0 = 0, float right0 = 0, float fwd1 = float.NaN, float right1 = float.NaN, bool jockey = false, float side = 0) =>
        new() { Action = a, Dur = dur, V0 = new Vector2(fwd0, right0), V1 = new Vector2(float.IsNaN(fwd1) ? fwd0 : fwd1, float.IsNaN(right1) ? right0 : right1), Jockeying = jockey, Side = side };

    Script Add(string name, params Beat[] steps)
    {
        var s = new Script { Name = name };
        s.Steps.AddRange(steps);
        _scripts.Add(s);
        return s;
    }

    void Build()
    {
        Add("set", St(DefenderAction.Hold, 1.5f, jockey: true));
        Add("jockey_right", St(DefenderAction.Hold, 0.3f, jockey: true), St(DefenderAction.Jockey, 0.3f, 0, 0, 0, 3.5f, true), St(DefenderAction.Jockey, 1.2f, 0, 3.5f, jockey: true), St(DefenderAction.Hold, 0.5f, 0, 3.5f, 0, 0, true));
        Add("jockey_left", St(DefenderAction.Hold, 0.3f, jockey: true), St(DefenderAction.Jockey, 0.3f, 0, 0, 0, -3.5f, true), St(DefenderAction.Jockey, 1.2f, 0, -3.5f, jockey: true), St(DefenderAction.Hold, 0.5f, 0, -3.5f, 0, 0, true));
        Add("jockey_back", St(DefenderAction.Hold, 0.3f, jockey: true), St(DefenderAction.Jockey, 0.3f, 0, 0, -3f, 0, true), St(DefenderAction.Jockey, 1.2f, -3f, 0, jockey: true), St(DefenderAction.Hold, 0.5f, -3f, 0, 0, 0, true));
        Add("jockey_back_diagonal", St(DefenderAction.Jockey, 0.3f, 0, 0, -2.5f, 2f, true), St(DefenderAction.Jockey, 0.9f, -2.5f, 2f, jockey: true), St(DefenderAction.Jockey, 0.3f, -2.5f, 2f, -2.5f, -2f, true), St(DefenderAction.Jockey, 0.9f, -2.5f, -2f, jockey: true));
        Add("jockey_close", St(DefenderAction.Press, 0.3f, 0, 0, 3.5f, 0, true), St(DefenderAction.Press, 1.0f, 3.5f, 0, jockey: true), St(DefenderAction.Hold, 0.4f, 3.5f, 0, 0, 0, true), St(DefenderAction.Hold, 0.5f, jockey: true));
        Add("jockey_turns", St(DefenderAction.Jockey, 0.3f, 0, 0, 0, 3.5f, true), St(DefenderAction.Jockey, 0.8f, 0, 3.5f, jockey: true), St(DefenderAction.Jockey, 0.35f, 0, 3.5f, 0, -3.5f, true), St(DefenderAction.Jockey, 0.8f, 0, -3.5f, jockey: true), St(DefenderAction.Hold, 0.4f, 0, -3.5f, 0, 0, true));
        foreach (float side in new[] { 1f, -1f })
        {
            var lunge = St(DefenderAction.Lunge, 0.42f, side: side); lunge.Contact = 0.3f; lunge.Reach = 1.2f;
            Add(side > 0 ? "lunge_right" : "lunge_left", St(DefenderAction.Hold, 0.5f, jockey: true), lunge, St(DefenderAction.Hold, 0.8f, jockey: true));
        }
        {
            var lunge = St(DefenderAction.Lunge, 0.32f, side: 1f); lunge.Contact = 0.2f; lunge.Reach = 1.4f;
            Add("lunge_run", St(DefenderAction.Press, 0.5f, 5f, 0), lunge, St(DefenderAction.Stumble, 0.5f, 1.5f, 0), St(DefenderAction.Press, 0.5f, 1.5f, 0, 4f, 0));
        }
        {
            var slide = St(DefenderAction.Slide, 0.9f, 6f, 0, 0f, 0, side: 1f); slide.Reach = 0.8f;
            Add("slide", St(DefenderAction.Press, 0.5f, 6f, 0), slide, St(DefenderAction.Grounded, 0.7f), St(DefenderAction.GettingUp, 0.5f), St(DefenderAction.Hold, 0.6f, jockey: true));
        }
        foreach (float side in new[] { 1f, -1f })
        {
            var block = St(DefenderAction.Block, 1.0f, jockey: true, side: side); block.Contact = 0.4f; block.Reach = 0.9f;
            Add(side > 0 ? "block_right" : "block_left", St(DefenderAction.Hold, 0.5f, jockey: true), block, St(DefenderAction.Hold, 0.6f, jockey: true));
        }
        Add("barge_right", St(DefenderAction.Press, 0.5f, 6f, 0), St(DefenderAction.Barge, 0.35f, 6f, 0, side: 1f), St(DefenderAction.Press, 0.5f, 6f, 0));
        Add("stumble", St(DefenderAction.Press, 0.5f, 4f, 0), St(DefenderAction.Stumble, 0.6f, 2f, 0), St(DefenderAction.Press, 0.6f, 2f, 0, 5f, 0));

        Beat Re(Reaction r, float dur, float fwd0, float right0, float fwd1, float right1, float side = 0) =>
            new() { Action = DefenderAction.Stumble, Reaction = r, Dur = dur, V0 = new Vector2(fwd0, right0), V1 = new Vector2(fwd1, right1), Side = side };
        Beat Run(float dur, float v0, float v1) => new() { Action = DefenderAction.Hold, Dur = dur, V0 = new Vector2(v0, 0), V1 = new Vector2(v1, 0), Contact = -1f };
        Add("attacker_stumble", Run(0.6f, 6f, 6f), Re(Reaction.Stumble, 0.6f, 2f, 0, 2f, 0), Run(0.7f, 2f, 6f)).Attacker = true;
        Add("attacker_barged", Run(0.6f, 6f, 6f), Re(Reaction.Barged, 0.6f, 2f, 1.5f, 2f, 0, 1f), Run(0.7f, 2f, 6f)).Attacker = true;
        Add("attacker_fall", Run(0.6f, 5f, 5f), Re(Reaction.Fall, 0.5f, 3f, 0, 0, 0), Re(Reaction.Fall, 1.6f, 0, 0, 0, 0)).Attacker = true;
    }

    // ---- stepping ----

    PlayerView View => _s.Attacker ? _m.Attacker : _m.Defender;

    /// <summary>Advances one physics tick. Returns true when every script is done and every sheet written.</summary>
    public bool Step()
    {
        if (_inFlight > 0) return false;
        if (_settle > 0) { if (--_settle == 0) Grab(); return false; }
        if (_view >= 0)
        {
            // the moment's next view, or on with the script
            if (++_view < Views.Length) { Aim(_view); return false; }
            _view = -1; _col++;
        }
        if (_done) return true;
        if (_si < 0 || _t >= _s.Total)
        {
            if (_si >= 0) Finish();
            if (++_si >= _scripts.Count) { _done = true; return false; }
            Begin();
            return false;
        }
        _frame = Frame(_t);
        View.Apply(_frame, false);
        Check();
        _t += Dt;
        if (_t >= _next && _col < MaxColumns) { _next += _every; _view = 0; Aim(0); }
        return false;
    }

    void Begin()
    {
        _s = _scripts[_si];
        _t = 0; _gait = 0; _pos = _p0; _col = 0; _view = -1; _sheet = null;
        _every = Math.Max(0.15f, _s.Total / (MaxColumns - 0.5f)); _next = _every * 0.5f;
        _clips.Clear(); _prevRot = null; _boneSpeed = 0; _low = 1e9f; _footErr = -1f; _pops = 0; _boneWhere = _lowWhere = "";
        _m.Attacker.Visible = _s.Attacker; _m.Defender.Visible = !_s.Attacker;
        _frame = Frame(0);
        View.Teleport(_frame, false);
        _m.Camera.ResetPhysicsInterpolation();
    }

    /// <summary>The scripted frame at <paramref name="t"/>; advances the position and the stride clock by one tick.</summary>
    FigureFrame Frame(float t)
    {
        float acc = 0; int si = 0;
        for (int i = 0; i < _s.Steps.Count; i++)
        {
            if (t < acc + _s.Steps[i].Dur || i == _s.Steps.Count - 1) { si = i; break; }
            acc += _s.Steps[i].Dur;
        }
        var st = _s.Steps[si];
        // the action's own clock runs on through consecutive steps of the same action
        float carry = 0f;
        for (int i = si - 1; i >= 0 && _s.Steps[i].Action == st.Action && _s.Steps[i].Reaction == st.Reaction; i--) carry += _s.Steps[i].Dur;
        float u = Math.Clamp(t - acc, 0f, st.Dur), x = st.Dur > 0 ? u / st.Dur : 1f;
        float e = x * x * (3f - 2f * x);
        Vector2 v = st.V0.Lerp(st.V1, st.Action == DefenderAction.Slide ? x : e);
        Vector3 vel = Fwd * v.X + Right * v.Y;
        if (t > 0) _pos += vel * Dt;
        var f = FigureFrame.At(_pos, Facing);
        f.Velocity = vel; f.RunSpeed = vel.Length(); f.Dt = Dt; f.HeightM = 1.8f;
        f.BallPos = _pos + Fwd * 2.5f + new Vector3(0, 0.11f, 0); f.BallRadius = 0.11f;
        f.NextTouchEta = float.NaN; f.StrikeEta = float.NaN;
        if (vel.Length() > 0.05f)
        {
            float freq = Mathf.Clamp(1.8f + 0.28f * vel.Length(), 1.8f, 4.2f);
            _gait += freq * Dt;
            float gp = _gait % 2f;
            f.HasGait = true; f.IsStepping = true; f.StepFrequency = freq; f.GaitPhase = gp;
            f.SwingRight = gp < 1f; f.SwingProgress = gp % 1f;
            f.RightFootPhase = gp / 2f; f.LeftFootPhase = (gp / 2f + 0.5f) % 1f;
        }
        if (_s.Attacker && st.Reaction == Reaction.None) return f;   // the attacker just running
        Vector3 foot = _pos;
        float reach = 0f;
        if (st.Action == DefenderAction.Lunge)
        {
            // as the core's lunging foot: out along the lunge to its reach at the contact, held, then drawn back
            float c = st.Contact, hold = c + 0.1f;
            float k = u <= c ? u / c : u <= hold ? 1f : 1f - (u - hold) / Math.Max(1e-3f, st.Dur - hold);
            foot = _pos + (Fwd + Right * (0.25f * st.Side)).Normalized() * (st.Reach * Math.Clamp(k, 0f, 1f)) + new Vector3(0, 0.11f, 0);
        }
        else if (st.Action == DefenderAction.Slide) foot = _pos + Fwd * st.Reach + new Vector3(0, 0.11f, 0);
        else if (st.Action == DefenderAction.Block) reach = 0.3f + (st.Reach - 0.3f) * Math.Clamp(u / st.Contact, 0f, 1f);
        f.Defender = new FigureDefender
        {
            Valid = true, Action = st.Action, ActionTime = u + carry, Side = st.Side, TackleFoot = foot,
            ContactTime = st.Action == DefenderAction.Lunge ? st.Contact : float.NaN, BlockReach = reach, Jockeying = st.Jockeying,
            GetUpSeconds = 0.5f, BlockGrowSeconds = st.Action == DefenderAction.Block ? st.Contact : 0f,
            Reaction = st.Reaction, ReactionSide = st.Side,
        };
        return f;
    }

    // ---- sheets ----

    void Aim(int view)
    {
        var look = new Vector3(_frame.Position.X, 0.85f, _frame.Position.Z);
        // side: from his right; front: from ahead of him, looking back
        Vector3 eye = view == 0 ? look + Right * 5.0f + new Vector3(0, 0.4f, 0) : look + Fwd * 5.0f + new Vector3(0, 0.4f, 0);
        _m.Camera.Transform = new Transform3D(Basis.Identity, eye).LookingAt(look, Vector3.Up);
        _m.Camera.ResetPhysicsInterpolation();
        var d = _frame.Defender;
        string what = !d.Valid ? "running" : d.Reaction != Reaction.None ? d.Reaction.ToString() : d.Action.ToString();
        _label.Text = $"{_s.Name}  {Views[view].ToUpperInvariant()}  {_t:F2}s\n{what} t={d.ActionTime:F2}s | {Drawn()}";
        _settle = 2;
    }

    string Drawn() => View.Figure.DefenderAnim is { } a ? a.Describe() : "procedural";

    void Grab()
    {
        _inFlight++;
        Capture(_col, _view);
    }

    async void Capture(int col, int view)
    {
        await _m.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var img = _m.GetViewport().GetTexture().GetImage();
        int w = img.GetWidth(), h = img.GetHeight();
        int side = Math.Min(Math.Min(w, h), Crop * h / 720);
        var crop = img.GetRegion(new Rect2I((w - side) / 2, (h - side) / 2, side, side));
        crop.Resize(Tile, Tile, Image.Interpolation.Lanczos);
        _sheet ??= Image.CreateEmpty(MaxColumns * Tile, Views.Length * Tile, false, crop.GetFormat());
        _sheet.BlitRect(crop, new Rect2I(0, 0, Tile, Tile), new Vector2I(col * Tile, view * Tile));
        _inFlight--;
    }

    void Finish()
    {
        if (_sheet != null && _col > 0)
        {
            var sheet = _sheet.GetRegion(new Rect2I(0, 0, _col * Tile, Views.Length * Tile));
            string path = System.IO.Path.Combine(_dir, _s.Name + ".png");
            var err = sheet.SavePng(path);
            GD.Print($"SHEET {_s.Name} {path} moments={_col} err={err}");
        }
        GD.Print($"DEFENDER_GALLERY script={_s.Name} drawn={(View.Figure.DefenderAnim != null ? "mocap" : "procedural")} clips={(_clips.Count > 0 ? string.Join(",", _clips) : "-")} " +
                 $"max_bone_deg_per_s={_boneSpeed:F0} at={_boneWhere}{(_boneSpeed > PopDegPerSec ? " POP" : "")} pop_ticks={_pops} lowest_joint_m={_low:F3} at={_lowWhere}" +
                 (_footErr >= 0 ? $" foot_error_cm={_footErr * 100:F1}" : ""));
    }

    // ---- checks ----

    void Check()
    {
        var fig = View.Figure;
        if (fig.DefenderAnim?.Motion.ClipName is string c && !_clips.Contains(c)) _clips.Add(c);
        var body = fig.Body;
        if (body == null) return;
        int n = body.DrivenCount;
        var names = body.DrivenBoneNames;
        _prevRot ??= new Quaternion[n];
        bool pop = false;
        for (int k = 0; k < n; k++)
        {
            var q = body.DrivenLocalRotation(k);
            float y = body.DrivenBoneWorld(k).Y;
            if (y < _low) { _low = y; _lowWhere = $"{Short(names[k])}@{_t:F2}s"; }
            if (_t > Dt * 1.5f)
            {
                float dot = Math.Min(1f, Math.Abs(q.Normalized().Dot(_prevRot[k].Normalized())));
                float speed = Mathf.RadToDeg(2f * MathF.Acos(dot)) / Dt;
                if (speed > _boneSpeed) { _boneSpeed = speed; _boneWhere = $"{Short(names[k])}@{_t:F2}s"; }
                if (speed > PopDegPerSec) pop = true;
            }
            _prevRot[k] = q;
        }
        if (pop) _pops++;
        // the tackling foot against the core's foot point on the contact tick
        var d = _frame.Defender;
        if (d.Valid && d.Action == DefenderAction.Lunge && float.IsFinite(d.ContactTime) && Math.Abs(d.ActionTime - d.ContactTime) < Dt * 0.6f)
        {
            int ankle = -1;
            string want = d.Side < 0 ? "LeftFoot" : "RightFoot";
            for (int k = 0; k < n; k++) if (names[k].EndsWith(want)) ankle = k;
            if (ankle >= 0)
            {
                Vector3 a = body.DrivenBoneWorld(ankle);
                _footErr = new Vector2(a.X - d.TackleFoot.X, a.Z - d.TackleFoot.Z).Length();
            }
        }
    }

    static string Short(string bone) => bone.StartsWith("mixamorig_") ? bone["mixamorig_".Length..] : bone;
}
