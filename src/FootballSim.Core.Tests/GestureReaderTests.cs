using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>
/// Right-stick gestures read from scripted stick traces. Traces are written in the player's frame (x forward, y to his right)
/// and turned into world axes for a player running (or facing, standing) at 30 degrees, so the frame itself is tested too.
/// </summary>
public class GestureReaderTests
{
    const float Heading = 30f;

    public enum Entry { Standing, Jog, Sprint }

    /// <summary>One tick of a trace in the player's frame: the right stick, and optionally the left stick and a skill key.</summary>
    record struct Step(Vector2 Right, Vector2 Left = default, SkillKey Key = SkillKey.None);

    static Vector2 Rot(Vector2 v, float deg)
    {
        float a = deg * MathF.PI / 180f, c = MathF.Cos(a), s = MathF.Sin(a);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    static Vector2 Fwd(float m = 1f) => new(m, 0f);
    static Vector2 Back(float m = 1f) => new(-m, 0f);
    static Vector2 Side(int side, float m = 1f) => new(0f, side * m);

    static IEnumerable<Step> Hold(Vector2 v, int ticks) => Enumerable.Repeat(new Step(v), ticks);
    static IEnumerable<Step> Neutral(int ticks) => Hold(Vector2.Zero, ticks);

    /// <summary>A player standing, jogging or sprinting along the heading. Jogging and sprinting, the facing is 20 degrees off the run.</summary>
    static PlayerState Player(Entry e)
    {
        float h = Heading * MathF.PI / 180f;
        var p = TestData.Outfield(facing: e == Entry.Standing ? h : h + 20f * MathF.PI / 180f);
        float speed = e switch { Entry.Standing => 0f, Entry.Jog => 4f, _ => 7f };
        p.Velocity = new Vector3(MathF.Cos(h) * speed, 0f, MathF.Sin(h) * speed);
        return p;
    }

    /// <summary>Feeds a trace (after a neutral lead-in) and returns every gesture recognised, with its tick.</summary>
    static List<(int Tick, GestureRead G)> Read(Entry e, IEnumerable<Step> trace, int lead = 10, int tail = 40)
    {
        var t = TestData.Tuning();
        var p = Player(e);
        var all = Neutral(lead).Concat(trace).Concat(Neutral(tail)).ToList();
        var got = new List<(int, GestureRead)>();
        for (int i = 0; i < all.Count; i++)
        {
            var r = Rot(all[i].Right, Heading);
            var l = Rot(all[i].Left, Heading);
            var it = new TickIntent { RightX = r.X, RightZ = r.Y, MoveX = l.X, MoveZ = l.Y, Sprint = e == Entry.Sprint ? 1f : 0f, SkillKey = all[i].Key };
            var g = GestureReader.Read(p, it, i + 1, t);
            if (g.Kind != Gesture.None) got.Add((i + 1, g));
        }
        return got;
    }

    static void AssertOnly(List<(int Tick, GestureRead G)> got, Gesture kind, SkillSide side)
    {
        Assert.True(got.Count == 1, $"expected one {kind}, got [{string.Join(", ", got.Select(x => $"{x.G.Kind}/{x.G.Side}@{x.Tick}"))}]");
        Assert.Equal(kind, got[0].G.Kind);
        Assert.Equal(side, got[0].G.Side);
    }

    static SkillSide S(int side) => side < 0 ? SkillSide.Left : SkillSide.Right;

    public static IEnumerable<object[]> SidesAndEntries()
    {
        foreach (var e in Enum.GetValues<Entry>())
            foreach (int side in new[] { -1, 1 })
                yield return new object[] { e, side };
    }

    public static IEnumerable<object[]> Entries() => Enum.GetValues<Entry>().Select(e => new object[] { e });

    [Theory]
    [MemberData(nameof(SidesAndEntries))]
    public void StepOverIsForwardThenTheSide(Entry e, int side)
    {
        AssertOnly(Read(e, Hold(Fwd(), 6).Concat(Hold(Side(side), 8))), Gesture.StepOver, S(side));
        // Through neutral, still inside the window.
        AssertOnly(Read(e, Hold(Fwd(), 4).Concat(Neutral(6)).Concat(Hold(Side(side), 8))), Gesture.StepOver, S(side));
        // The side 25 degrees off square (inside the 30 degree step-over tolerance).
        AssertOnly(Read(e, Hold(Fwd(), 5).Concat(Hold(Rot(Side(side), side * -25f), 8))), Gesture.StepOver, S(side));
    }

    [Theory]
    [MemberData(nameof(SidesAndEntries))]
    public void StepOverNeedsTheSideWithinTheWindow(Entry e, int side)
    {
        // Forward, then neutral for 0.4 s, then a quick side flick: not a step-over but a body feint.
        var got = Read(e, Hold(Fwd(0.8f), 5).Concat(Neutral(48)).Concat(Hold(Side(side), 4)));
        Assert.DoesNotContain(got, x => x.G.Kind == Gesture.StepOver);
        Assert.Contains(got, x => x.G.Kind == Gesture.BodyFeint && x.G.Side == S(side));
    }

    [Theory]
    [MemberData(nameof(SidesAndEntries))]
    public void AQuickSideFlickIsABodyFeintAndAHoldIsABallRoll(Entry e, int side)
    {
        foreach (int hold in new[] { 1, 4, 8, 11 })
            AssertOnly(Read(e, Hold(Side(side), hold)), Gesture.BodyFeint, S(side));
        foreach (int hold in new[] { 12, 20, 60 })
        {
            var got = Read(e, Hold(Side(side), hold));
            AssertOnly(got, Gesture.BallRoll, S(side));
            Assert.Equal(10 + 12, got[0].Tick); // recognised on the twelfth tick of the hold (0.1 s)
        }
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void DragBackIsBack(Entry e)
    {
        AssertOnly(Read(e, Hold(Back(), 8)), Gesture.DragBack, SkillSide.None);
        AssertOnly(Read(e, Hold(Rot(Back(), 40f), 8)), Gesture.DragBack, SkillSide.None);
    }

    [Fact]
    public void KnockOnWaitsThenFiresWhileSprinting()
    {
        var got = Read(Entry.Sprint, Hold(Fwd(), 8));
        AssertOnly(got, Gesture.KnockOn, SkillSide.None);
        Assert.Equal(10 + 1 + 18, got[0].Tick); // armed on the first forward tick, fired 0.15 s later
        // Held forward the whole time: still one knock-on.
        AssertOnly(Read(Entry.Sprint, Hold(Fwd(), 60)), Gesture.KnockOn, SkillSide.None);
    }

    [Theory]
    [InlineData(Entry.Standing)]
    [InlineData(Entry.Jog)]
    public void AForwardFlickWithoutSprintDoesNothing(Entry e)
    {
        Assert.Empty(Read(e, Hold(Fwd(), 8)));
        Assert.Empty(Read(e, Hold(Fwd(), 60)));
    }

    [Fact]
    public void AWeakForwardFlickIsNoKnockOn() => Assert.Empty(Read(Entry.Sprint, Hold(Fwd(0.8f), 10)));

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void AForwardFlickTurningToTheSideInsideTheKnockOnWaitIsAStepOver(int side)
    {
        // Sprinting: forward (arms the knock-on) then the side 0.1 s later, inside the 0.15 s wait.
        AssertOnly(Read(Entry.Sprint, Hold(Fwd(), 12).Concat(Hold(Side(side), 8))), Gesture.StepOver, S(side));
        // The side after the wait: the knock-on has already fired; the side, still held once the gap has passed, is a step-over.
        var late = Read(Entry.Sprint, Hold(Fwd(), 20).Concat(Hold(Side(side), 14)));
        Assert.Equal(new[] { Gesture.KnockOn, Gesture.StepOver }, late.Select(x => x.G.Kind));
    }

    [Fact]
    public void GesturesInsideTheGapAreDropped()
    {
        // A drag-back 4 ticks after a body feint (gap 0.08 s = 10 ticks) is ignored.
        var got = Read(Entry.Jog, Hold(Side(1), 3).Concat(Neutral(3)).Concat(Hold(Back(), 6)));
        AssertOnly(got, Gesture.BodyFeint, SkillSide.Right);
        // Twelve ticks later it counts.
        got = Read(Entry.Jog, Hold(Side(1), 3).Concat(Neutral(12)).Concat(Hold(Back(), 6)));
        Assert.Equal(new[] { Gesture.BodyFeint, Gesture.DragBack }, got.Select(x => x.G.Kind));
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void SkillKeysSkipTheStickAndTakeTheirSideFromTheLeftStick(Entry e)
    {
        foreach (var (key, kind) in new[] { (SkillKey.StepOver, Gesture.StepOver), (SkillKey.BodyFeint, Gesture.BodyFeint),
                     (SkillKey.BallRoll, Gesture.BallRoll), (SkillKey.DragBack, Gesture.DragBack) })
        {
            var sided = kind != Gesture.DragBack;
            AssertOnly(Read(e, new[] { new Step(Vector2.Zero, Rot(Fwd(), -60f), key) }), kind, sided ? SkillSide.Left : SkillSide.None);
            AssertOnly(Read(e, new[] { new Step(Vector2.Zero, Rot(Fwd(), 60f), key) }), kind, sided ? SkillSide.Right : SkillSide.None);
            AssertOnly(Read(e, new[] { new Step(Vector2.Zero, Vector2.Zero, key) }), kind, sided ? SkillSide.Right : SkillSide.None);
        }
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void StickNoiseBelowTheActuationDriftAndSpringBackGiveNothing(Entry e)
    {
        var rng = new Random(7);
        // Noise: any direction, below 0.5.
        var noise = Enumerable.Range(0, 600).Select(_ => new Step(Rot(new Vector2((float)rng.NextDouble() * 0.49f, 0f), (float)rng.NextDouble() * 360f)));
        Assert.Empty(Read(e, noise));
        // Slow drift: a worn stick wandering round at 0.45.
        var drift = Enumerable.Range(0, 600).Select(i => new Step(Rot(new Vector2(0.45f, 0f), i * 0.6f)));
        Assert.Empty(Read(e, drift));
        if (e != Entry.Sprint)
        {
            // A forward push released, springing back past centre (an overshoot below the actuation) to rest.
            var spring = Hold(Fwd(), 30).Concat(new[] { new Step(Fwd(0.6f)), new Step(Fwd(0.1f)), new Step(Back(0.35f)), new Step(Back(0.1f)) });
            Assert.Empty(Read(e, spring));
        }
    }
}
