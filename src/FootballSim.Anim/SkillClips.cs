using System.Numerics;
using System.Text.Json;

namespace FootballSim.Anim;

/// <summary>A span of an authored skill clip during which a foot's surface is on the ball (clips.json <c>ball.on</c>), or a touch event (<c>ball.touch</c>: From = To).</summary>
public readonly record struct BallSpan(string From, string To, int Foot);

/// <summary>
/// One authored skill-move clip (tools/anim/author.py) with what the game needs beside its pose: the move, side and playing foot,
/// the entry it was authored from (standing or running, and that entry's speed), its events, the spans its foot is on the ball,
/// the authored ball centre per frame (clip model space, a reference only: the game's ball is the core's) and its root.
/// The root is the clip's own travel with the sway taken out: a straight line on the ground from under the hips at the first
/// frame to under them at the last, at an even pace; its heading is the hips' heading at the first frame and stays fixed, so a turn
/// the clip makes (a fake cut, a spin) is kept relative to it. The pose relative to the root is what is put on the simulation's
/// position and facing. A move the simulation plays from its move sheet is drawn on the sheet's root instead
/// (<see cref="SheetRoot"/>, <see cref="SheetYaw"/>: the clip's own low-passed pelvis track and keyed facing, clips.json <c>root</c>),
/// which is the path the simulation follows, so the body goes where the clip does.
/// </summary>
public sealed class SkillClip
{
    public Clip Clip { get; }
    /// <summary>The move (the set's name after "skill:"), e.g. "ball_roll".</summary>
    public string Move { get; }
    /// <summary>"left", "right" or "none".</summary>
    public string Side { get; }
    /// <summary>The playing foot: 0 left, 1 right.</summary>
    public int Foot { get; }
    public string Entry { get; }
    public float EntrySpeed { get; }
    public float BallRadius { get; }
    public IReadOnlyList<BallSpan> On { get; }
    public IReadOnlyList<BallSpan> Touches { get; }
    readonly Vector3[] _ball;
    readonly Vector2 _root0, _root1;
    /// <summary>The root's fixed heading (model yaw, radians: 0 faces +Z).</summary>
    public float RootYaw { get; }

    /// <summary>The sheet root per frame (model x, z in metres from under the hips at the first frame, yaw in radians toward his left), or null for a clip without one.</summary>
    readonly Vector3[]? _sheetRoot;
    public bool HasSheetRoot => _sheetRoot != null;

    public SkillClip(Clip clip, Skeleton sk, string move, string side, int foot, string entry, float entrySpeed, float ballRadius,
        IReadOnlyList<BallSpan> on, IReadOnlyList<BallSpan> touches, Vector3[]? ball, Vector3[]? sheetRoot = null)
    {
        Clip = clip; Move = move; Side = side; Foot = foot; Entry = entry; EntrySpeed = entrySpeed; BallRadius = ballRadius;
        On = on; Touches = touches;
        _sheetRoot = sheetRoot != null && sheetRoot.Length == clip.FrameCount ? sheetRoot : null;
        _ball = ball != null && ball.Length == clip.FrameCount ? ball : new Vector3[clip.FrameCount];
        var h0 = clip.Hips(0); var h1 = clip.Hips(clip.FrameCount - 1);
        _root0 = new Vector2(h0.X, h0.Z); _root1 = new Vector2(h1.X, h1.Z);
        // the hips' heading per frame, unwrapped (a spin turns through 360 degrees)
        _yaw = new float[clip.FrameCount];
        var inv = Quaternion.Inverse(sk.RestGlobal[sk.Hips]);
        for (int f = 0; f < clip.FrameCount; f++)
        {
            var flat = Features.Heading(clip.Rotations(f)[sk.Hips] * inv);
            float a = flat.LengthSquared() > 1e-8f ? MathF.Atan2(flat.X, flat.Y) : f > 0 ? _yaw[f - 1] : 0f;
            _yaw[f] = f == 0 ? a : _yaw[f - 1] + Spring.WrapPi(a - _yaw[f - 1]);
        }
        RootYaw = _yaw[0];
    }
    readonly float[] _yaw;

    /// <summary>The hips' heading (model yaw, unwrapped from the first frame's) at <paramref name="seconds"/>.</summary>
    public float Heading(float seconds)
    {
        float f = Math.Clamp(seconds * Clip.Fps, 0f, Clip.FrameCount - 1);
        int a = (int)MathF.Floor(f), b = Math.Min(a + 1, Clip.FrameCount - 1);
        return _yaw[a] + (_yaw[b] - _yaw[a]) * (f - a);
    }

    /// <summary>An event's time (seconds into the clip).</summary>
    public float EventSeconds(string e) => Clip.Events[e] / (float)Clip.Fps;
    public bool HasEvent(string e) => Clip.Events.ContainsKey(e);
    /// <summary>The clip's last frame's time (seconds).</summary>
    public float LastSeconds => (Clip.FrameCount - 1) / (float)Clip.Fps;

    /// <summary>This clip's own time map: its events onto the simulation's (see <see cref="SkillTimeMap.ForEvents"/>).</summary>
    public SkillTimeMap TimeMap(IReadOnlyList<(string Event, float Sim)> simEvents, float simOrigin, float? clipFrom = null) =>
        SkillTimeMap.ForEvents(Clip.Events, Clip.Fps, simEvents, simOrigin, clipFrom: clipFrom);

    /// <summary>The root on the ground at <paramref name="seconds"/> (model x, z).</summary>
    public Vector2 Root(float seconds) => Vector2.Lerp(_root0, _root1, Math.Clamp(seconds / Math.Max(LastSeconds, 1e-4f), 0f, 1f));

    Vector3 SheetRow(float seconds)
    {
        float f = Math.Clamp(seconds * Clip.Fps, 0f, Clip.FrameCount - 1);
        int a = (int)MathF.Floor(f), b = Math.Min(a + 1, Clip.FrameCount - 1);
        return Vector3.Lerp(_sheetRoot![a], _sheetRoot[b], f - a);
    }

    /// <summary>The sheet root on the ground at <paramref name="seconds"/> (model x, z, where the hips' first frame stands), for a clip that has one.</summary>
    public Vector2 SheetRoot(float seconds)
    {
        var r = SheetRow(seconds);
        return new Vector2(_root0.X + r.X, _root0.Y + r.Y);
    }

    /// <summary>The sheet root's yaw at <paramref name="seconds"/> (model yaw, radians, toward his left), for a clip that has one.</summary>
    public float SheetYaw(float seconds) => SheetRow(seconds).Z;

    /// <summary>The authored ball centre at <paramref name="seconds"/> (clip model space).</summary>
    public Vector3 Ball(float seconds)
    {
        float f = Math.Clamp(seconds * Clip.Fps, 0f, Clip.FrameCount - 1);
        int a = (int)MathF.Floor(f), b = Math.Min(a + 1, Clip.FrameCount - 1);
        return Vector3.Lerp(_ball[a], _ball[b], f - a);
    }

    /// <summary>The contact bits (<see cref="Clip.LeftFoot"/>, <see cref="Clip.RightFoot"/>) of the frame nearest <paramref name="seconds"/>.</summary>
    public byte Contacts(float seconds) => Clip.Contacts(Math.Clamp((int)MathF.Round(seconds * Clip.Fps), 0, Clip.FrameCount - 1));
}

/// <summary>The authored skill clips (game/assets/anim/attacker/skills): per move its standing and running variants for each playing foot.</summary>
public sealed class SkillClipLibrary
{
    public Skeleton Skeleton { get; }
    public IReadOnlyList<SkillClip> Clips { get; }

    public SkillClipLibrary(Skeleton skeleton, IReadOnlyList<SkillClip> clips) { Skeleton = skeleton; Clips = clips; }

    /// <summary>Parses the skills' clips.json; <paramref name="readFile"/> returns a listed .kclip file's bytes.</summary>
    public static SkillClipLibrary Parse(string json, Func<string, byte[]> readFile)
    {
        var lib = ClipLibrary.Parse(json, readFile);
        using var doc = JsonDocument.Parse(json);
        var list = new List<SkillClip>();
        int i = 0;
        foreach (var c in doc.RootElement.GetProperty("clips").EnumerateArray())
        {
            var e = lib.Clips[i++];
            if (e.Verdict == ClipVerdict.Reject || !e.Set.StartsWith("skill:")) continue;
            string move = e.Set["skill:".Length..];
            int foot = c.TryGetProperty("foot", out var ft) && ft.GetString() == "right" ? 1 : 0;
            string entry = c.TryGetProperty("entry", out var en) ? en.GetString() ?? "stand" : "stand";
            float speed = c.TryGetProperty("entry_speed", out var es) ? es.GetSingle() : 0f;
            float radius = 0.11f;
            var on = new List<BallSpan>(); var touches = new List<BallSpan>();
            Vector3[]? track = null;
            if (c.TryGetProperty("ball", out var ball))
            {
                if (ball.TryGetProperty("radius", out var r)) radius = r.GetSingle();
                if (ball.TryGetProperty("on", out var o))
                    foreach (var s in o.EnumerateArray())
                        on.Add(new BallSpan(s[0].GetString()!, s[1].GetString()!, s[2].GetString() == "right" ? 1 : 0));
                if (ball.TryGetProperty("touch", out var t))
                    foreach (var p in t.EnumerateObject())
                        touches.Add(new BallSpan(p.Name, p.Name, p.Value.GetString() == "right" ? 1 : 0));
                if (ball.TryGetProperty("track", out var tr))
                    track = tr.EnumerateArray().Select(v => new Vector3(v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle())).ToArray();
            }
            Vector3[]? root = c.TryGetProperty("root", out var rt)
                ? rt.EnumerateArray().Select(v => new Vector3(v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle() * MathF.PI / 180f)).ToArray() : null;
            list.Add(new SkillClip(e.Clip, lib.Skeleton, move, e.Side, foot, entry, speed, radius, on, touches, track, root));
        }
        return new SkillClipLibrary(lib.Skeleton, list);
    }

    /// <summary>
    /// The standing and the running variant of <paramref name="move"/> played with <paramref name="foot"/> (0 left) toward
    /// <paramref name="side"/> (-1 left, +1 right, 0 none; a clip whose side is "none" fits any), or null when the library has none.
    /// A move with only one entry gives it for both. The fake cut's two hooks share the move and are told apart by side: the inside
    /// hook (fake_cut) cuts away from the playing foot's side, the outside hook (fake_cut_out) toward it.
    /// </summary>
    public (SkillClip Stand, SkillClip Run)? Pair(string move, int foot, int side)
    {
        SkillClip? stand = null, run = null;
        foreach (var c in Clips)
        {
            if (c.Move != move || c.Foot != foot) continue;
            if (side != 0 && c.Side != "none" && c.Side != (side < 0 ? "left" : "right")) continue;
            // (a sprint entry is picked by name, see ForEntry: only a sheet-driven move has one)
            if (c.Entry == "sprint" || c.Clip.Name.EndsWith("_swap")) continue;
            if (c.Entry == "run") run ??= c; else stand ??= c;
        }
        if (stand == null && run == null) return null;
        return (stand ?? run!, run ?? stand!);
    }

    /// <summary>The clip of <paramref name="move"/> for <paramref name="foot"/> and <paramref name="side"/> from the entry <paramref name="entry"/> ("stand", "run" or "sprint"), or null.</summary>
    public SkillClip? ForEntry(string move, int foot, int side, string entry)
    {
        foreach (var c in Clips)
        {
            if (c.Move != move || c.Foot != foot || c.Entry != entry || c.Clip.Name.EndsWith("_swap")) continue;
            if (side != 0 && c.Side != "none" && c.Side != (side < 0 ? "left" : "right")) continue;
            return c;
        }
        return null;
    }
}

/// <summary>The weight of a skill move's running-entry clip against its standing one, from the player's speed at the move's start.</summary>
public static class SkillEntryBlend
{
    /// <summary>
    /// 0 at the standing clip's entry speed or slower, 1 at the running clip's or faster, eased (smoothstep) between; a pair whose
    /// two entry speeds are the same gives the standing clip below it and the running one from it.
    /// </summary>
    public static float RunWeight(float speed, float standSpeed, float runSpeed)
    {
        if (!(runSpeed > standSpeed + 1e-4f)) return speed >= runSpeed ? 1f : 0f;
        float u = Math.Clamp((speed - standSpeed) / (runSpeed - standSpeed), 0f, 1f);
        return u * u * (3f - 2f * u);
    }
}

/// <summary>
/// The skill clip's time as a function of the simulation's: piecewise onto the core's ticks, the clip's <c>start</c>→<c>contact</c>
/// onto the move's start→contact tick, <c>contact</c>→<c>release</c> onto contact→end tick, then <c>release</c>→<c>end</c> in real
/// time after the end tick (the settle). Between these knots the map is a monotone cubic (Fritsch–Butland slopes), so each event
/// lands exactly on its tick, time never runs backward and the rate changes smoothly across a span boundary instead of stepping.
/// A reach shorter than the clip's by more than <see cref="MaxRate"/> times (1.5: a fake shot's wind-up played twice as fast swings the leg at
/// 20 m/s) starts the clip part way into it (as late as needed to
/// keep the first span's rate at <see cref="MaxRate"/>; a reach of no time starts it at its contact). Times are seconds from the
/// move's start tick; a move whose own motion started earlier (a fake shot's press) gives a negative origin.
/// </summary>
public sealed class SkillTimeMap
{
    public const float MaxRate = 1.5f;
    readonly float[] _x, _y, _m;

    /// <summary>The clip time the map starts at (the clip's start, or later for a short reach).</summary>
    public float ClipStart => _y[0];
    /// <summary>The simulation time the clip ends at (its last knot).</summary>
    public float SimEnd => _x[^1];
    public int KnotCount => _x.Length;

    public SkillTimeMap(float clipStart, float clipContact, float clipRelease, float clipEnd,
        float simOrigin, float simContact, float simEnd, float maxRate = MaxRate)
        : this(clipStart, new[] { (simContact, clipContact), (simEnd, clipRelease) }, clipEnd, simOrigin, maxRate) { }

    /// <summary>
    /// The map through <paramref name="knots"/>: (simulation time, clip time) from the contact to the release, in order (a move with
    /// more events, a second contact or a sole lifting, has them between); knots that would not move both times forward are dropped.
    /// </summary>
    public SkillTimeMap(float clipStart, IReadOnlyList<(float Sim, float Clip)> knots, float clipEnd, float simOrigin, float maxRate = MaxRate)
    {
        var x = new List<float>(); var y = new List<float>();
        float simContact = Math.Max(knots[0].Sim, simOrigin), clipContact = knots[0].Clip;
        float c0 = Math.Max(clipStart, clipContact - maxRate * (simContact - simOrigin));
        if (simContact - simOrigin > 1e-5f && clipContact - c0 > 1e-5f) { x.Add(simOrigin); y.Add(c0); }
        x.Add(simContact); y.Add(clipContact);
        for (int i = 1; i < knots.Count; i++)
        {
            float sx = knots[i].Sim, cy = knots[i].Clip;
            if (i == knots.Count - 1) sx = Math.Max(sx, x[^1] + 1e-3f);
            if (sx <= x[^1] + 1e-5f || cy <= y[^1] + 1e-5f) { if (i < knots.Count - 1) continue; }
            x.Add(sx); y.Add(Math.Max(cy, y[^1] + 1e-5f));
        }
        float clipRelease = y[^1], simEnd = x[^1];
        float settle = Math.Max(0f, clipEnd - clipRelease);
        if (settle > 1e-5f) { x.Add(simEnd + settle); y.Add(clipEnd); }
        _x = x.ToArray(); _y = y.ToArray();
        int n = _x.Length;
        var d = new float[n - 1];
        for (int i = 0; i < n - 1; i++) d[i] = (_y[i + 1] - _y[i]) / (_x[i + 1] - _x[i]);
        _m = new float[n];
        _m[0] = d[0];
        // the settle plays in real time: rate 1 at its end; without a settle the last span's own rate
        _m[n - 1] = settle > 1e-5f ? 1f : d[n - 2];
        for (int i = 1; i < n - 1; i++)
        {
            float h0 = _x[i] - _x[i - 1], h1 = _x[i + 1] - _x[i];
            _m[i] = d[i - 1] * d[i] <= 0f ? 0f : 3f * (h0 + h1) / ((2f * h1 + h0) / d[i - 1] + (h1 + 2f * h0) / d[i]);
        }
    }

    /// <summary>
    /// A clip's map from its own event frames (<paramref name="clipEvents"/> at <paramref name="fps"/>, with <c>start</c> and
    /// <c>end</c>): each of <paramref name="simEvents"/> (the contact first, the release last, a move's other events between, in
    /// order) the clip has lands on its simulation time. Each clip of a move gets its own map from the same simulation events, so
    /// a standing and a running clip whose spans differ (a running spin's swap at the core's length) are blended in event time:
    /// both are at the same event on the same tick. Clips with the same event frames get the same map. <paramref name="clipFrom"/>
    /// starts it later than the clip's start (a map rebuilt part way through a move, from the clip's time then).
    /// </summary>
    public static SkillTimeMap ForEvents(IReadOnlyDictionary<string, int> clipEvents, int fps, IReadOnlyList<(string Event, float Sim)> simEvents,
        float simOrigin, float maxRate = MaxRate, float? clipFrom = null)
    {
        var knots = new List<(float, float)>();
        foreach (var (e, sim) in simEvents)
            if (clipEvents.TryGetValue(e, out int fr)) knots.Add((sim, fr / (float)fps));
        return new SkillTimeMap(clipFrom ?? clipEvents["start"] / (float)fps, knots, clipEvents["end"] / (float)fps, simOrigin, maxRate);
    }

    /// <summary>The clip time (seconds) at simulation time <paramref name="t"/> (seconds from the move's start tick).</summary>
    public float ClipTime(float t)
    {
        int n = _x.Length;
        if (t <= _x[0]) return _y[0];
        if (t >= _x[n - 1]) return _y[n - 1] + (t - _x[n - 1]) * _m[n - 1];
        int i = 0;
        while (i < n - 2 && t > _x[i + 1]) i++;
        float h = _x[i + 1] - _x[i], s = (t - _x[i]) / h;
        float s2 = s * s, s3 = s2 * s;
        return (2f * s3 - 3f * s2 + 1f) * _y[i] + (s3 - 2f * s2 + s) * h * _m[i] + (-2f * s3 + 3f * s2) * _y[i + 1] + (s3 - s2) * h * _m[i + 1];
    }

    /// <summary>The clip's rate (clip seconds per simulation second) at <paramref name="t"/>.</summary>
    public float Rate(float t)
    {
        int n = _x.Length;
        if (t <= _x[0]) return _m[0];
        if (t >= _x[n - 1]) return _m[n - 1];
        int i = 0;
        while (i < n - 2 && t > _x[i + 1]) i++;
        float h = _x[i + 1] - _x[i], s = (t - _x[i]) / h;
        float s2 = s * s;
        return ((6f * s2 - 6f * s) * _y[i] + (3f * s2 - 4f * s + 1f) * h * _m[i] + (-6f * s2 + 6f * s) * _y[i + 1] + (3f * s2 - 2f * s) * h * _m[i + 1]) / h;
    }
}
