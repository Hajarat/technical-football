using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace FootballSim.Core;

/// <summary>What a ball leg's track is measured from (see docs/dribbling.md, "Move sheets").</summary>
public enum LegAnchor
{
    /// <summary>His position each tick, axes along and across the run at the gesture: the ball travels with him.</summary>
    Body,
    /// <summary>Where the ball was at the leg's first tick, same axes: the ball keeps its place on the pitch whatever he does.</summary>
    Pitch,
    /// <summary>His position, axes turning with his facing.</summary>
    Facing,
    /// <summary>Nothing: the ball is not driven.</summary>
    Free,
    /// <summary>Nothing: on the leg's first tick the exit rule sets the ball's velocity once, then physics has it.</summary>
    Launch,
}

/// <summary>How a sheet's move ends: a touch handed to the touch planner, a launch a contact already made, or the ball left at rest.</summary>
public enum SheetExitKind { Touch, Launch, Rest }

/// <summary>One row of a sheet's root: where he is on the ground (x to his left, z along his run, metres) and his yaw (degrees, toward his left).</summary>
public readonly record struct RootRow(float X, float Z, float YawDeg);

/// <summary>One ball leg of a move sheet: the span between two events, what its track is measured from, the foot and surface on the ball.</summary>
public sealed class SheetLeg
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public LegAnchor Anchor { get; init; }
    public Foot? Foot { get; init; }
    public FootSurface? Surface { get; init; }
    /// <summary>One row per frame of the leg in the anchor's frame: x to his left, y the lift above rest, z along the run. Empty for free and launch legs.</summary>
    public Vector3[] Track { get; init; } = Array.Empty<Vector3>();
    public bool Driven => Anchor is LegAnchor.Body or LegAnchor.Pitch or LegAnchor.Facing;
}

/// <summary>A sheet's exit rule: its kind, the angle band off the run toward the exit side (degrees), and the touch it is pushed as.</summary>
public sealed class SheetExit
{
    public SheetExitKind Kind { get; init; }
    public float MinDeg { get; init; }
    public float MaxDeg { get; init; } = 180f;
    /// <summary>The angle taken with the stick at rest (NaN: none given).</summary>
    public float RestDeg { get; init; } = float.NaN;
    /// <summary>The touch profile the exit is pushed with ("sprint", "jog", "close", "knock_on"); empty: the stick's own.</summary>
    public string Push { get; init; } = "";
    public Foot? Foot { get; init; }
    public FootSurface? Surface { get; init; }
}

/// <summary>
/// One authored move sheet (tools/anim/author.py writes data/moves/sheets.json): the body's track, the ball's legs and the exit
/// rule of one move from one entry (standing, a run, a sprint), on its authored side. Its frame has its origin under his hips at
/// the gesture, +z along his run and +x to his left. The simulation follows it (see SkillMoves); the clip is drawn on the same
/// root.
/// </summary>
public sealed class MoveSheet
{
    /// <summary>The move file's name (the clip family, e.g. "fake_cut_out") and the move it plays ("fake_cut").</summary>
    public string Name { get; init; } = "";
    public string Move { get; init; } = "";
    /// <summary>"stand", "run" or "sprint".</summary>
    public string Entry { get; init; } = "stand";
    /// <summary>The pace the root comes in at, m/s.</summary>
    public float EntrySpeed { get; init; }
    public SkillSide Side { get; init; }
    /// <summary>The playing foot.</summary>
    public Foot Foot { get; init; }
    /// <summary>The content hash the authoring tool gave it; the clips made from the same file carry it in the clip index.</summary>
    public string Sha { get; init; } = "";
    public int Fps { get; init; } = 60;
    /// <summary>The events in time order (seconds from the start): start, contact, [others], release, end.</summary>
    public (string Name, float Seconds)[] Events { get; init; } = Array.Empty<(string, float)>();
    /// <summary>One row per frame from the start to the end.</summary>
    public RootRow[] Root { get; init; } = Array.Empty<RootRow>();
    /// <summary>The ball's centre at the first contact in the sheet's frame (y: its lift above rest).</summary>
    public Vector3 Ball0 { get; init; }
    public SheetLeg[] Legs { get; init; } = Array.Empty<SheetLeg>();
    public SheetExit Exit { get; init; } = new();
    /// <summary>How far the left stick may turn the sheet's frame, degrees.</summary>
    public float SteerDeg { get; init; }

    public bool HasEvent(string name) => Array.FindIndex(Events, e => e.Name == name) >= 0;

    public float EventSeconds(string name)
    {
        foreach (var e in Events) if (e.Name == name) return e.Seconds;
        throw new KeyNotFoundException($"move sheet {Name}/{Entry} has no event '{name}'");
    }

    /// <summary>The root at <paramref name="seconds"/> (held at its first and last rows outside the sheet), a cubic through the frames.</summary>
    public RootRow RootAt(float seconds)
    {
        if (Root.Length == 0) return default;
        float f = Math.Clamp(seconds * Fps, 0f, Root.Length - 1);
        int i = Math.Min((int)MathF.Floor(f), Math.Max(0, Root.Length - 2));
        float u = f - i;
        RootRow P(int k) => Root[Math.Clamp(k, 0, Root.Length - 1)];
        // (the rows before the first and after the last go on at the end rows' own steps)
        RootRow Ext(int k)
        {
            if (k >= 0 && k < Root.Length) return Root[k];
            RootRow end = k < 0 ? Root[0] : Root[^1], inner = k < 0 ? P(1) : P(Root.Length - 2);
            int steps = k < 0 ? -k : k - (Root.Length - 1);
            return new RootRow(end.X + (end.X - inner.X) * steps, end.Z + (end.Z - inner.Z) * steps, end.YawDeg + (end.YawDeg - inner.YawDeg) * steps);
        }
        var a = Ext(i - 1); var b = P(i); var c = P(i + 1); var d = Ext(i + 2);
        return new RootRow(Cubic(a.X, b.X, c.X, d.X, u), Cubic(a.Z, b.Z, c.Z, d.Z, u), Cubic(a.YawDeg, b.YawDeg, c.YawDeg, d.YawDeg, u));
    }

    /// <summary>A leg's track at <paramref name="share"/> (0..1) of the leg.</summary>
    public Vector3 TrackAt(int leg, float share)
    {
        var tr = Legs[leg].Track;
        if (tr.Length == 0) return Vector3.Zero;
        float f = Math.Clamp(share, 0f, 1f) * (tr.Length - 1);
        int i = Math.Min((int)MathF.Floor(f), Math.Max(0, tr.Length - 2));
        float u = f - i;
        Vector3 P(int k) => tr[Math.Clamp(k, 0, tr.Length - 1)];
        Vector3 Ext(int k) => k < 0 ? tr[0] + (tr[0] - P(1)) * -k : k >= tr.Length ? tr[^1] + (tr[^1] - P(tr.Length - 2)) * (k - tr.Length + 1) : tr[k];
        var a = Ext(i - 1); var b = P(i); var c = P(i + 1); var d = Ext(i + 2);
        return new Vector3(Cubic(a.X, b.X, c.X, d.X, u), Cubic(a.Y, b.Y, c.Y, d.Y, u), Cubic(a.Z, b.Z, c.Z, d.Z, u));
    }

    /// <summary>A Catmull-Rom segment from <paramref name="b"/> to <paramref name="c"/>.</summary>
    static float Cubic(float a, float b, float c, float d, float u)
    {
        float u2 = u * u, u3 = u2 * u;
        return 0.5f * (2f * b + (c - a) * u + (2f * a - 5f * b + 4f * c - d) * u2 + (3f * (b - c) + d - a) * u3);
    }

    static Foot OtherFoot(Foot f) => f == Core.Foot.Left ? Core.Foot.Right : Core.Foot.Left;

    /// <summary>The same move on the other side: left and right swapped, x and every yaw negated, the exit band kept (it is measured toward the exit side).</summary>
    public MoveSheet Mirror() => new()
    {
        Name = Name, Move = Move, Entry = Entry, EntrySpeed = EntrySpeed, Sha = Sha, Fps = Fps, Events = Events.ToArray(), SteerDeg = SteerDeg,
        Side = Side == SkillSide.Left ? SkillSide.Right : Side == SkillSide.Right ? SkillSide.Left : SkillSide.None,
        Foot = OtherFoot(Foot),
        Root = Root.Select(r => new RootRow(-r.X, r.Z, -r.YawDeg)).ToArray(),
        Ball0 = Ball0 with { X = -Ball0.X },
        Legs = Legs.Select(l => new SheetLeg
        {
            From = l.From, To = l.To, Anchor = l.Anchor, Surface = l.Surface, Foot = l.Foot is { } f ? OtherFoot(f) : null,
            Track = l.Track.Select(v => v with { X = -v.X }).ToArray(),
        }).ToArray(),
        Exit = new SheetExit
        {
            Kind = Exit.Kind, MinDeg = Exit.MinDeg, MaxDeg = Exit.MaxDeg, RestDeg = Exit.RestDeg, Push = Exit.Push, Surface = Exit.Surface,
            Foot = Exit.Foot is { } ef ? OtherFoot(ef) : null,
        },
    };

    /// <summary>True when <paramref name="o"/> has the same content (to within <paramref name="eps"/> on every number).</summary>
    public bool ContentEquals(MoveSheet o, float eps = 1e-6f)
    {
        bool F(float a, float b) => float.IsNaN(a) && float.IsNaN(b) || MathF.Abs(a - b) <= eps;
        bool V(Vector3 a, Vector3 b) => F(a.X, b.X) && F(a.Y, b.Y) && F(a.Z, b.Z);
        if (Name != o.Name || Move != o.Move || Entry != o.Entry || !F(EntrySpeed, o.EntrySpeed) || Side != o.Side || Foot != o.Foot || Sha != o.Sha
            || Fps != o.Fps || !F(SteerDeg, o.SteerDeg) || !V(Ball0, o.Ball0)) return false;
        if (Events.Length != o.Events.Length || Root.Length != o.Root.Length || Legs.Length != o.Legs.Length) return false;
        for (int i = 0; i < Events.Length; i++) if (Events[i].Name != o.Events[i].Name || !F(Events[i].Seconds, o.Events[i].Seconds)) return false;
        for (int i = 0; i < Root.Length; i++) if (!F(Root[i].X, o.Root[i].X) || !F(Root[i].Z, o.Root[i].Z) || !F(Root[i].YawDeg, o.Root[i].YawDeg)) return false;
        for (int i = 0; i < Legs.Length; i++)
        {
            SheetLeg a = Legs[i], b = o.Legs[i];
            if (a.From != b.From || a.To != b.To || a.Anchor != b.Anchor || a.Foot != b.Foot || a.Surface != b.Surface || a.Track.Length != b.Track.Length) return false;
            for (int k = 0; k < a.Track.Length; k++) if (!V(a.Track[k], b.Track[k])) return false;
        }
        return Exit.Kind == o.Exit.Kind && F(Exit.MinDeg, o.Exit.MinDeg) && F(Exit.MaxDeg, o.Exit.MaxDeg) && F(Exit.RestDeg, o.Exit.RestDeg)
            && Exit.Push == o.Exit.Push && Exit.Foot == o.Exit.Foot && Exit.Surface == o.Exit.Surface;
    }
}

/// <summary>The sheets of one move for one side and playing foot: standing, a run and (for a move that must shed speed) a sprint.</summary>
public readonly record struct SheetSet(MoveSheet Stand, MoveSheet? Run, MoveSheet? Sprint);

/// <summary>
/// The move sheets as loaded: every authored sheet and its mirror. Loaded with the tuning (see <see cref="DataLoader.LoadSheets"/>);
/// a sheet whose hash the clip index does not carry is refused.
/// </summary>
public sealed class MoveSheets
{
    /// <summary>The sheets as authored (one side each), in file order.</summary>
    public IReadOnlyList<MoveSheet> Authored { get; }
    readonly List<MoveSheet> _all = new();
    /// <summary>A hash over every sheet's own hash, in file order (a replay records it beside the tuning hash).</summary>
    public string Sha { get; }

    public MoveSheets(IReadOnlyList<MoveSheet> authored)
    {
        Authored = authored;
        foreach (var s in authored) { _all.Add(s); _all.Add(s.Mirror()); }
        var bytes = Encoding.UTF8.GetBytes(string.Join(",", authored.Select(s => $"{s.Name}/{s.Entry}:{s.Sha}")));
        Sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).Substring(0, 16).ToLowerInvariant();
    }

    /// <summary>
    /// The sheets of <paramref name="move"/> played with <paramref name="foot"/> toward <paramref name="side"/> (a sheet without a
    /// side fits any), or null when there is no standing sheet for it.
    /// </summary>
    public SheetSet? Find(string move, SkillSide side, Foot foot)
    {
        MoveSheet? stand = null, run = null, sprint = null;
        foreach (var s in _all)
        {
            if (s.Move != move || s.Foot != foot) continue;
            if (s.Side != SkillSide.None && side != SkillSide.None && s.Side != side) continue;
            if (s.Entry == "sprint") sprint ??= s; else if (s.Entry == "run") run ??= s; else stand ??= s;
        }
        stand ??= run;
        return stand == null ? null : new SheetSet(stand, run, sprint);
    }

    // ---- file format ----

    static string Inv(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    static Foot ParseFoot(string s) => s == "right" ? Foot.Right : Foot.Left;

    static FootSurface ParseSurface(string s, string label) => s switch
    {
        "inside" => FootSurface.Inside, "laces" => FootSurface.Laces, "outside" => FootSurface.Outside, "sole" => FootSurface.Sole, "heel" => FootSurface.Heel,
        _ => throw new FormatException($"{label}: unknown foot surface '{s}'"),
    };

    /// <summary>
    /// Parses sheets.json. With <paramref name="clipIndexJson"/> (the skill clips' clips.json) every sheet must be named by a clip
    /// carrying the same hash, and every clip that names a sheet must name one that is there with that hash: a mismatch throws a
    /// <see cref="FormatException"/> naming <paramref name="label"/> and the sheet.
    /// </summary>
    public static MoveSheets Parse(string json, string label = "sheets.json", string? clipIndexJson = null, string clipIndexLabel = "clips.json")
    {
        using var doc = JsonDocument.Parse(json);
        int fps = doc.RootElement.TryGetProperty("fps", out var fp) ? fp.GetInt32() : 60;
        var list = new List<MoveSheet>();
        foreach (var e in doc.RootElement.GetProperty("sheets").EnumerateArray())
        {
            string name = e.GetProperty("name").GetString()!;
            string at = $"{label}: {name}/{(e.TryGetProperty("entry", out var en0) ? en0.GetString() : "stand")}";
            var events = new List<(string, float)>();
            foreach (var p in e.GetProperty("events").EnumerateObject()) events.Add((p.Name, p.Value.GetSingle()));
            for (int i = 1; i < events.Count; i++)
                if (events[i].Item2 < events[i - 1].Item2) throw new FormatException($"{at}: events out of order");
            if (events.Count < 3 || events[0].Item1 != "start" || events[^1].Item1 != "end" || !events.Exists(x => x.Item1 == "contact") || !events.Exists(x => x.Item1 == "release"))
                throw new FormatException($"{at}: events must run from start to end with a contact and a release");
            var legs = new List<SheetLeg>();
            foreach (var l in e.GetProperty("legs").EnumerateArray())
            {
                string an = l.GetProperty("anchor").GetString()!;
                LegAnchor anchor = an switch
                {
                    "body" => LegAnchor.Body, "pitch" => LegAnchor.Pitch, "facing" => LegAnchor.Facing, "free" => LegAnchor.Free, "launch" => LegAnchor.Launch,
                    _ => throw new FormatException($"{at}: unknown leg anchor '{an}'"),
                };
                var leg = new SheetLeg
                {
                    From = l.GetProperty("from").GetString()!, To = l.GetProperty("to").GetString()!, Anchor = anchor,
                    Foot = l.TryGetProperty("foot", out var lf) ? ParseFoot(lf.GetString()!) : null,
                    Surface = l.TryGetProperty("surface", out var ls) ? ParseSurface(ls.GetString()!, at) : null,
                    Track = l.TryGetProperty("track", out var tr) ? tr.EnumerateArray().Select(v => new Vector3(v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle())).ToArray() : Array.Empty<Vector3>(),
                };
                if (!events.Exists(x => x.Item1 == leg.From) || !events.Exists(x => x.Item1 == leg.To)) throw new FormatException($"{at}: a leg names an event the sheet has not got");
                if (leg.Driven && leg.Track.Length < 2) throw new FormatException($"{at}: the {an} leg from {leg.From} has no track");
                legs.Add(leg);
            }
            var exit = new SheetExit();
            if (e.TryGetProperty("exit", out var x))
            {
                string kind = x.TryGetProperty("kind", out var xk) ? xk.GetString()! : "touch";
                exit = new SheetExit
                {
                    Kind = kind switch { "touch" => SheetExitKind.Touch, "launch" => SheetExitKind.Launch, "rest" => SheetExitKind.Rest, _ => throw new FormatException($"{at}: unknown exit kind '{kind}'") },
                    MinDeg = x.TryGetProperty("min_deg", out var mn) ? mn.GetSingle() : 0f,
                    MaxDeg = x.TryGetProperty("max_deg", out var mx) ? mx.GetSingle() : 180f,
                    RestDeg = x.TryGetProperty("rest_deg", out var rd) ? rd.GetSingle() : float.NaN,
                    Push = x.TryGetProperty("push", out var pu) ? pu.GetString()! : "",
                    Foot = x.TryGetProperty("foot", out var xf) ? ParseFoot(xf.GetString()!) : null,
                    Surface = x.TryGetProperty("surface", out var xs) ? ParseSurface(xs.GetString()!, at) : null,
                };
            }
            string side = e.TryGetProperty("side", out var sd) ? sd.GetString()! : "none";
            var b0 = e.TryGetProperty("ball0", out var b0e) ? new Vector3(b0e[0].GetSingle(), b0e[1].GetSingle(), b0e[2].GetSingle()) : Vector3.Zero;
            list.Add(new MoveSheet
            {
                Name = name, Move = e.GetProperty("move").GetString()!, Entry = e.TryGetProperty("entry", out var en) ? en.GetString()! : "stand",
                EntrySpeed = e.TryGetProperty("entry_speed", out var es) ? es.GetSingle() : 0f,
                Side = side == "left" ? SkillSide.Left : side == "right" ? SkillSide.Right : SkillSide.None,
                Foot = ParseFoot(e.GetProperty("foot").GetString()!), Sha = e.TryGetProperty("sha", out var sh) ? sh.GetString()! : "", Fps = fps,
                Events = events.ToArray(),
                Root = e.GetProperty("root").EnumerateArray().Select(r => new RootRow(r[0].GetSingle(), r[1].GetSingle(), r[2].GetSingle())).ToArray(),
                Ball0 = b0, Legs = legs.ToArray(), Exit = exit, SteerDeg = e.TryGetProperty("steer_deg", out var st) ? st.GetSingle() : 0f,
            });
        }
        if (clipIndexJson != null) CheckIndex(list, label, clipIndexJson, clipIndexLabel);
        return new MoveSheets(list);
    }

    static void CheckIndex(List<MoveSheet> sheets, string label, string clipIndexJson, string clipIndexLabel)
    {
        var named = new Dictionary<(string, string), string>();
        using var doc = JsonDocument.Parse(clipIndexJson);
        foreach (var c in doc.RootElement.GetProperty("clips").EnumerateArray())
        {
            if (!c.TryGetProperty("sheet", out var s)) continue;
            var key = (s.GetProperty("name").GetString()!, s.GetProperty("entry").GetString()!);
            string sha = s.GetProperty("sha").GetString()!;
            if (named.TryGetValue(key, out var was) && was != sha)
                throw new FormatException($"{clipIndexLabel}: the clips of {key.Item1}/{key.Item2} carry two sheet hashes ({was}, {sha})");
            named[key] = sha;
        }
        foreach (var s in sheets)
        {
            if (!named.TryGetValue((s.Name, s.Entry), out var sha))
                throw new FormatException($"{label}: the sheet {s.Name}/{s.Entry} ({s.Sha}) is named by no clip in {clipIndexLabel}; run tools/anim/author.py");
            if (sha != s.Sha)
                throw new FormatException($"{label}: the sheet {s.Name}/{s.Entry} has hash {s.Sha}, its clips in {clipIndexLabel} carry {sha}; run tools/anim/author.py");
        }
        foreach (var (key, sha) in named)
            if (!sheets.Exists(s => s.Name == key.Item1 && s.Entry == key.Item2))
                throw new FormatException($"{label}: {clipIndexLabel} names the sheet {key.Item1}/{key.Item2} ({sha}), which is not in the file; run tools/anim/author.py");
    }

    /// <summary>The authored sheets in the file's format (numbers written to round-trip).</summary>
    public string ToJson()
    {
        static string Sur(FootSurface s) => s.ToString().ToLowerInvariant();
        static string Ft(Foot f) => f == Foot.Right ? "right" : "left";
        var sb = new StringBuilder();
        sb.Append("{\"fps\":").Append(Authored.Count > 0 ? Authored[0].Fps : 60).Append(",\"sheets\":[");
        for (int i = 0; i < Authored.Count; i++)
        {
            var s = Authored[i];
            if (i > 0) sb.Append(',');
            sb.Append("\n{\"name\":\"").Append(s.Name).Append("\",\"move\":\"").Append(s.Move).Append("\",\"entry\":\"").Append(s.Entry)
              .Append("\",\"entry_speed\":").Append(Inv(s.EntrySpeed))
              .Append(",\"side\":\"").Append(s.Side == SkillSide.Left ? "left" : s.Side == SkillSide.Right ? "right" : "none")
              .Append("\",\"foot\":\"").Append(Ft(s.Foot)).Append("\",\"events\":{")
              .Append(string.Join(",", s.Events.Select(e => $"\"{e.Name}\":{Inv(e.Seconds)}"))).Append("},\"root\":[")
              .Append(string.Join(",", s.Root.Select(r => $"[{Inv(r.X)},{Inv(r.Z)},{Inv(r.YawDeg)}]"))).Append("],\"ball0\":[")
              .Append(Inv(s.Ball0.X)).Append(',').Append(Inv(s.Ball0.Y)).Append(',').Append(Inv(s.Ball0.Z)).Append("],\"legs\":[");
            for (int k = 0; k < s.Legs.Length; k++)
            {
                var l = s.Legs[k];
                if (k > 0) sb.Append(',');
                sb.Append("{\"from\":\"").Append(l.From).Append("\",\"to\":\"").Append(l.To).Append("\",\"anchor\":\"").Append(l.Anchor.ToString().ToLowerInvariant()).Append('"');
                if (l.Foot is { } lf) sb.Append(",\"foot\":\"").Append(Ft(lf)).Append('"');
                if (l.Surface is { } ls) sb.Append(",\"surface\":\"").Append(Sur(ls)).Append('"');
                if (l.Track.Length > 0) sb.Append(",\"track\":[").Append(string.Join(",", l.Track.Select(v => $"[{Inv(v.X)},{Inv(v.Y)},{Inv(v.Z)}]"))).Append(']');
                sb.Append('}');
            }
            sb.Append("],\"exit\":{\"kind\":\"").Append(s.Exit.Kind.ToString().ToLowerInvariant()).Append("\",\"min_deg\":").Append(Inv(s.Exit.MinDeg))
              .Append(",\"max_deg\":").Append(Inv(s.Exit.MaxDeg));
            if (!float.IsNaN(s.Exit.RestDeg)) sb.Append(",\"rest_deg\":").Append(Inv(s.Exit.RestDeg));
            if (s.Exit.Push.Length > 0) sb.Append(",\"push\":\"").Append(s.Exit.Push).Append('"');
            if (s.Exit.Foot is { } ef) sb.Append(",\"foot\":\"").Append(Ft(ef)).Append('"');
            if (s.Exit.Surface is { } xs) sb.Append(",\"surface\":\"").Append(Sur(xs)).Append('"');
            sb.Append("},\"steer_deg\":").Append(Inv(s.SteerDeg)).Append(",\"sha\":\"").Append(s.Sha).Append("\"}");
        }
        sb.Append("]}\n");
        return sb.ToString();
    }
}

/// <summary>
/// The sheet a move is played from: the two sheets either side of his entry pace blended at one share for the whole move. Events
/// are blended; tracks are blended at the same share of each span between events. The forward travel of a run or sprint sheet is
/// scaled by his entry pace over the sheet's entry speed; sideways travel and yaw are not. The events after the contact are
/// moved onto whole ticks from the contact, so each lands on a tick.
/// </summary>
public sealed class SheetPlay
{
    public MoveSheet A { get; }
    public MoveSheet B { get; }
    /// <summary>The share of <see cref="B"/>, 0..1.</summary>
    public float W { get; }
    readonly float _scaleA, _scaleB;
    readonly float[] _e, _ea, _eb;
    readonly int _contact;

    /// <summary>The sheet's exit rule and legs (the two sheets of a move have the same legs and exit kind; <see cref="A"/>'s unless <see cref="B"/> has most of the blend).</summary>
    public MoveSheet Lead => W > 0.5f ? B : A;
    public int EventCount => _e.Length;
    public string EventName(int i) => A.Events[i].Name;
    /// <summary>Event <paramref name="i"/>'s time in the blended sheet, seconds.</summary>
    public float Event(int i) => _e[i];
    public int EventIndex(string name) => Array.FindIndex(A.Events, e => e.Name == name);
    public float EventSeconds(string name) { int i = EventIndex(name); return i < 0 ? float.NaN : _e[i]; }
    public bool HasEvent(string name) => EventIndex(name) >= 0;
    public int LegCount => A.Legs.Length;
    public SheetLeg Leg(int i) => Lead.Legs[i];

    public SheetPlay(MoveSheet a, MoveSheet b, float w, float entryPace, int tickRate)
    {
        A = a; B = b; W = ReferenceEquals(a, b) ? 0f : Math.Clamp(w, 0f, 1f);
        _scaleA = Scale(a, entryPace); _scaleB = Scale(b, entryPace);
        int n = a.Events.Length;
        _ea = a.Events.Select(e => e.Seconds).ToArray();
        _eb = b.Events.Select(e => e.Seconds).ToArray();
        _e = new float[n];
        for (int i = 0; i < n; i++) _e[i] = _ea[i] + (_eb[i] - _ea[i]) * W;
        _contact = EventIndex("contact");
        for (int i = _contact + 1; i < n; i++)
            _e[i] = _e[_contact] + MathF.Max(i - _contact, MathF.Round((_e[i] - _e[_contact]) * tickRate)) / tickRate;
    }

    static float Scale(MoveSheet s, float pace) => s.Entry != "stand" && s.EntrySpeed > 0.5f ? MathF.Max(0f, pace) / s.EntrySpeed : 1f;

    /// <summary>True when the two sheets can be blended: the same events in the same order and the same legs.</summary>
    public static bool Compatible(MoveSheet a, MoveSheet b)
    {
        if (a.Events.Length != b.Events.Length || a.Legs.Length != b.Legs.Length) return false;
        for (int i = 0; i < a.Events.Length; i++) if (a.Events[i].Name != b.Events[i].Name) return false;
        for (int i = 0; i < a.Legs.Length; i++)
            if (a.Legs[i].Anchor != b.Legs[i].Anchor || a.Legs[i].From != b.Legs[i].From || a.Legs[i].To != b.Legs[i].To) return false;
        return a.Exit.Kind == b.Exit.Kind;
    }

    /// <summary>The span <paramref name="tau"/> falls in and its share of it.</summary>
    void Span(float tau, out int i, out float u)
    {
        int n = _e.Length;
        i = 0;
        while (i < n - 2 && tau >= _e[i + 1]) i++;
        float d = _e[i + 1] - _e[i];
        u = d > 1e-6f ? Math.Clamp((tau - _e[i]) / d, 0f, 1f) : tau >= _e[i + 1] ? 1f : 0f;
    }

    /// <summary>The blended root at sheet time <paramref name="tau"/>: x to his left, z along his run (scaled), yaw toward his left (degrees).</summary>
    public RootRow Root(float tau)
    {
        Span(tau, out int i, out float u);
        var a = A.RootAt(_ea[i] + (_ea[i + 1] - _ea[i]) * u);
        if (W <= 0f) return a with { Z = a.Z * _scaleA };
        var b = B.RootAt(_eb[i] + (_eb[i + 1] - _eb[i]) * u);
        return new RootRow(a.X + (b.X - a.X) * W, a.Z * _scaleA + (b.Z * _scaleB - a.Z * _scaleA) * W, a.YawDeg + (b.YawDeg - a.YawDeg) * W);
    }

    /// <summary>Leg <paramref name="leg"/>'s blended track at <paramref name="share"/> (0..1) of the leg.</summary>
    public Vector3 Track(int leg, float share)
    {
        var a = A.TrackAt(leg, share);
        return W <= 0f ? a : Vector3.Lerp(a, B.TrackAt(leg, share), W);
    }

    /// <summary>The share of leg <paramref name="leg"/> elapsed at <paramref name="tau"/>.</summary>
    public float LegShare(int leg, float tau)
    {
        float from = _e[EventIndex(A.Legs[leg].From)], to = _e[EventIndex(A.Legs[leg].To)];
        return to - from > 1e-6f ? Math.Clamp((tau - from) / (to - from), 0f, 1f) : 1f;
    }

    /// <summary>The ball's centre at the first contact against his root then (x to his left, y its lift, z ahead), blended.</summary>
    public Vector3 ContactOffset()
    {
        static Vector3 Of(MoveSheet s)
        {
            var r = s.RootAt(s.EventSeconds("contact"));
            return new Vector3(s.Ball0.X - r.X, s.Ball0.Y, s.Ball0.Z - r.Z);
        }
        var a = Of(A);
        return W <= 0f ? a : Vector3.Lerp(a, Of(B), W);
    }
}
