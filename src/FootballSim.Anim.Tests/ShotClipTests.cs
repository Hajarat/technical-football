using System.Text.Json.Nodes;
using FootballSim.Anim;

namespace FootballSim.Anim.Tests;

/// <summary>The shot clips' index: every kind, entry, foot and angle has a clip, and a missing kind or bin falls back to the power kick.</summary>
public class ShotClipTests
{
    static readonly string Folder = FindFolder();

    static string FindFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string p = Path.Combine(dir.FullName, "game", "assets", "anim", "attacker", "shots");
            if (File.Exists(Path.Combine(p, "clips.json"))) return p;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("game/assets/anim/attacker/shots not found");
    }

    static ShotClipLibrary Load(Func<JsonNode, bool>? keep = null)
    {
        string json = File.ReadAllText(Path.Combine(Folder, "clips.json"));
        if (keep != null)
        {
            var root = JsonNode.Parse(json)!;
            var clips = root["clips"]!.AsArray();
            for (int i = clips.Count - 1; i >= 0; i--) if (!keep(clips[i]!)) clips.RemoveAt(i);
            json = root.ToJsonString();
        }
        return ShotClipLibrary.Parse(json, f => File.ReadAllBytes(Path.Combine(Folder, f)));
    }

    [Fact]
    public void TheFolderHasTheCapturedKicksAndEveryAuthoredKind()
    {
        var lib = Load();
        Assert.Equal(50, lib.Clips.Count);
        Assert.Equal(4, lib.Clips.Count(c => c.Kind == ShotClipLibrary.Instep));
        foreach (string kind in new[] { "power", "finesse", "chip", "outside" })
            foreach (int foot in new[] { 0, 1 })
                foreach (bool run in new[] { false, true })
                    Assert.Contains(lib.Clips, c => c.Kind == kind && c.Foot == foot && c.Run == run);
        foreach (var c in lib.Clips)
        {
            Assert.InRange(c.BinDeg, c.BinLo, c.BinHi);
            Assert.Equal(c.Clip.FrameCount, c.Root.Length);
            Assert.Equal(c.Kind != ShotClipLibrary.Instep, c.Authored);
            Assert.Equal(c.Authored, c.BallAtContact != null);
            // the ball leaves toward the striking foot's own side for a positive bin: his right (model -X) for the right foot
            if (c.Authored && c.BinDeg != 0f) Assert.Equal(c.Foot == 1 ? -MathF.Sign(c.BinDeg) : MathF.Sign(c.BinDeg), MathF.Sign(c.OutYaw));
        }
    }

    [Fact]
    public void EveryAngleOfAKindsRangePicksItsOwnBinForBothFeetAndEntries()
    {
        var lib = Load();
        var ranges = new (string Kind, float Lo, float Hi)[] { ("power", -180f, 67.5f), ("finesse", -135f, 79f), ("chip", -67.5f, 22.5f), ("outside", 22.5f, 67.5f) };
        foreach (var (kind, lo, hi) in ranges)
            foreach (int foot in new[] { 0, 1 })
                foreach (bool run in new[] { false, true })
                    for (float a = lo; a <= hi; a += 2.5f)
                    {
                        var p = lib.Pick(kind, run, foot, a);
                        Assert.True(p != null && !p.Value.Fallback, $"{kind} {(run ? "run" : "stand")} foot {foot} at {a}: no clip of its own");
                        var c = p!.Value.Clip;
                        Assert.Equal((foot, run), (c.Foot, c.Run));
                        Assert.InRange(a, c.BinLo, c.BinHi);
                        Assert.True(c.Kind == kind || kind == "power" && c.Kind == "instep");
                    }
        // straight on, the power kick is a captured one; out wide it is authored
        Assert.Equal("instep", lib.Pick("power", true, 1, 0f)!.Value.Clip.Kind);
        Assert.Equal(-90f, lib.Pick("power", true, 1, -95f)!.Value.Clip.BinDeg);
        Assert.Equal(-180f, lib.Pick("power", false, 0, -170f)!.Value.Clip.BinDeg);
        Assert.Equal(45f, lib.Pick("outside", true, 1, 40f)!.Value.Clip.BinDeg);
    }

    [Fact]
    public void AnAngleAKindHasNoBinForFallsBackToTheNearestPowerBin()
    {
        var lib = Load();
        // a chip struck 90 degrees across, a finesse straight behind, an outside straight on: the power kick's bin there
        foreach (var (kind, angle, bin) in new[] { ("chip", -90f, -90f), ("finesse", -170f, -180f), ("outside", 0f, 0f), ("chip", 60f, 45f) })
        {
            var p = lib.Pick(kind, true, 1, angle)!.Value;
            Assert.True(p.Fallback);
            Assert.True(p.Clip.Kind is "power" or "instep");
            Assert.Equal(bin, p.Clip.BinDeg);
        }
        // beyond the strong foot's own side there is no bin at all: the nearest one (45 open)
        var far = lib.Pick("power", true, 1, 100f)!.Value;
        Assert.True(far.Fallback);
        Assert.Equal(45f, far.Clip.BinDeg);
        // a wide shot, struck behind him by the foot on the side the ball leaves: the bin behind him holds it from that side too
        foreach (int foot in new[] { 0, 1 })
            foreach (bool run in new[] { false, true })
                foreach (float a in new[] { 160f, 170f, 179f, 180f })
                {
                    var wide = lib.Pick("power", run, foot, a)!.Value;
                    Assert.False(wide.Fallback);
                    Assert.Equal(-180f, wide.Clip.BinDeg);
                }
        // just short of that bin it is still the nearest one
        var nearly = lib.Pick("power", true, 1, 152f)!.Value;
        Assert.True(nearly.Fallback);
        Assert.Equal(-180f, nearly.Clip.BinDeg);
    }

    [Fact]
    public void AFolderWithoutAKindFallsBackToThePowerKickAndOneWithOnlyTheCapturedKicksToThem()
    {
        var noOutside = Load(c => (string?)c["kind"] != "outside");
        Assert.DoesNotContain(noOutside.Clips, c => c.Kind == "outside");
        var p = noOutside.Pick("outside", true, 1, 45f)!.Value;
        Assert.True(p.Fallback);
        Assert.Equal(("power", 45f), (p.Clip.Kind, p.Clip.BinDeg));

        var captured = Load(c => (string?)c["kind"] == "instep");
        Assert.Equal(4, captured.Clips.Count);
        foreach (string kind in new[] { "power", "finesse", "chip", "outside" })
            foreach (float a in new[] { -180f, -90f, 0f, 45f })
            {
                var q = captured.Pick(kind, false, 0, a)!.Value;
                Assert.Equal("instep", q.Clip.Kind);
                Assert.Equal(kind != "power" || a is < -22.5f or > 22.5f, q.Fallback);
            }

        // a clips.json from before the kinds (no kind or bin fields): every clip a captured kick for the 0-degree bin
        var old = Load(c =>
        {
            if ((string?)c["kind"] != "instep") return false;
            var o = c.AsObject();
            o.Remove("kind"); o.Remove("bin_deg"); o.Remove("bin_range");
            return true;
        });
        Assert.All(old.Clips, c => Assert.Equal(("instep", 0f, -22.5f, 22.5f), (c.Kind, c.BinDeg, c.BinLo, c.BinHi)));

        // one entry missing for a foot: the other entry's clip stands in
        var runOnly = Load(c => (string?)c["kind"] == "instep" && (string?)c["entry"] == "run");
        Assert.True(runOnly.Pick("power", false, 1, 0f)!.Value.Clip.Run);
        Assert.Null(Load(c => (string?)c["foot"] == "left").Pick("power", true, 1, 0f));
    }

    [Fact]
    public void TheKindFollowsTheSurfaceThenTheShot()
    {
        Assert.Equal("outside", ShotClipLibrary.KindFor(outside: true, chip: true, finesse: true));
        Assert.Equal("chip", ShotClipLibrary.KindFor(false, true, true));
        Assert.Equal("finesse", ShotClipLibrary.KindFor(false, false, true));
        Assert.Equal("power", ShotClipLibrary.KindFor(false, false, false));
    }
}
