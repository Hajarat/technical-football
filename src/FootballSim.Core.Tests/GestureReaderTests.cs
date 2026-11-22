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

    /// <summary>
    /// One tick of a trace in the player's frame: the right stick, and optionally the left stick, a skill key and the shoulder
    /// buttons (left = <see cref="TickIntent.Chip"/>, right = <see cref="TickIntent.Finesse"/>).
    /// </summary>
    record struct Step(Vector2 Right, Vector2 Left = default, SkillKey Key = SkillKey.None, bool Lb = false, bool Rb = false);

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
            var it = new TickIntent { RightX = r.X, RightZ = r.Y, MoveX = l.X, MoveZ = l.Y, Sprint = e == Entry.Sprint ? 1f : 0f, SkillKey = all[i].Key,
                Chip = all[i].Lb, Finesse = all[i].Rb };
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
        // Turned on round the rim (50 degrees, then the side), never through neutral: still a step-over.
        AssertOnly(Read(e, Hold(Fwd(), 6).Concat(Hold(Rot(Fwd(), side * 50f), 2)).Concat(Hold(Side(side), 8))), Gesture.StepOver, S(side));
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

    static IEnumerable<Step> Drag(int ticks, Vector2 left, bool lb = true, bool rb = true) =>
        Enumerable.Repeat(new Step(Vector2.Zero, left, Lb: lb, Rb: rb), ticks);

    [Theory]
    [MemberData(nameof(Entries))]
    public void DragBackIsTheLeftStickBackWithBothShoulders(Entry e)
    {
        AssertOnly(Read(e, Drag(8, Back())), Gesture.DragBack, SkillSide.None);
        AssertOnly(Read(e, Drag(8, Rot(Back(), 40f))), Gesture.DragBack, SkillSide.None);
        // 125 degrees from forward is back enough (drag_back_stick_deg 120); 115 is not.
        AssertOnly(Read(e, Drag(8, Rot(Back(), 55f))), Gesture.DragBack, SkillSide.None);
        AssertOnly(Read(e, Drag(8, Rot(Back(), -55f))), Gesture.DragBack, SkillSide.None);
        Assert.Empty(Read(e, Drag(8, Rot(Back(), 65f))));
        // Shoulders first, then the stick back: fires when the stick arrives; held on, still one.
        AssertOnly(Read(e, Drag(5, Vector2.Zero).Concat(Drag(60, Back()))), Gesture.DragBack, SkillSide.None);
        // One shoulder only, the stick back without shoulders, or both shoulders with the stick sideways: nothing.
        Assert.Empty(Read(e, Drag(8, Back(), lb: false)));
        Assert.Empty(Read(e, Drag(8, Back(), rb: false)));
        Assert.Empty(Read(e, Drag(8, Back(), lb: false, rb: false)));
        Assert.Empty(Read(e, Drag(8, Side(1))));
        Assert.Empty(Read(e, Drag(8, Back(0.4f))));
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void TheRightStickBackAloneDoesNothing(Entry e)
    {
        Assert.Empty(Read(e, Hold(Back(), 8)));
        Assert.Empty(Read(e, Hold(Rot(Back(), 40f), 60)));
        // Back, released, back again.
        Assert.Empty(Read(e, Hold(Back(), 6).Concat(Neutral(20)).Concat(Hold(Back(), 6))));
    }

    [Theory]
    [MemberData(nameof(SidesAndEntries))]
    public void ElasticoIsBackThenTheSide(Entry e, int side)
    {
        AssertOnly(Read(e, Hold(Back(), 6).Concat(Hold(Side(side), 8))), Gesture.Elastico, S(side));
        AssertOnly(Read(e, Hold(Back(), 6).Concat(Hold(Rot(Back(), side * -50f), 2)).Concat(Hold(Side(side), 30))), Gesture.Elastico, S(side));
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void HeelToHeelIsAForwardFlickNeutralThenBack(Entry e)
    {
        var got = Read(e, Hold(Fwd(), 5).Concat(Neutral(4)).Concat(Hold(Back(), 5)));
        AssertOnly(got, Gesture.HeelToHeel, SkillSide.None);
        Assert.Equal(10 + 5 + 4 + 1, got[0].Tick); // on the back flick's first tick
        // The back flick of a heel-to-heel turned to the side is no elastico.
        AssertOnly(Read(e, Hold(Fwd(), 5).Concat(Neutral(4)).Concat(Hold(Back(), 14)).Concat(Hold(Side(1), 4))), Gesture.HeelToHeel, SkillSide.None);
        // Too slow a step (a forward hold or a neutral pause past 0.125 s): nothing.
        Assert.DoesNotContain(Read(e, Hold(Fwd(), 20).Concat(Neutral(4)).Concat(Hold(Back(), 5))), x => x.G.Kind != Gesture.KnockOn);
        Assert.DoesNotContain(Read(e, Hold(Fwd(), 5).Concat(Neutral(20)).Concat(Hold(Back(), 5))), x => x.G.Kind != Gesture.KnockOn);
    }

    [Theory]
    [MemberData(nameof(SidesAndEntries))]
    public void SpinIsAForwardFlickNeutralThenTheSide(Entry e, int side)
    {
        var got = Read(e, Hold(Fwd(), 5).Concat(Neutral(4)).Concat(Hold(Side(side), 4)));
        AssertOnly(got, Gesture.SpinAndGo, S(side));
        Assert.Equal(10 + 5 + 4 + 1, got[0].Tick);
        // The side held on is still only the spin (no roll after it), and released gives no feint.
        AssertOnly(Read(e, Hold(Fwd(), 5).Concat(Neutral(4)).Concat(Hold(Side(side), 40))), Gesture.SpinAndGo, S(side));
    }

    [Theory]
    [MemberData(nameof(SidesAndEntries))]
    public void McGeadyIsABackFlickNeutralThenTheSide(Entry e, int side)
    {
        var got = Read(e, Hold(Back(), 5).Concat(Neutral(4)).Concat(Hold(Side(side), 4)));
        AssertOnly(got, Gesture.McGeady, S(side));
        Assert.Equal(10 + 5 + 4 + 1, got[0].Tick); // on the side flick's first tick
        // The side held on is still only the McGeady spin (no roll after it), and released gives no feint.
        AssertOnly(Read(e, Hold(Back(), 5).Concat(Neutral(4)).Concat(Hold(Side(side), 40))), Gesture.McGeady, S(side));
        // Back straight on to the side is the elastico; the same flicks after a forward flick are the spin.
        AssertOnly(Read(e, Hold(Back(), 5).Concat(Hold(Side(side), 8))), Gesture.Elastico, S(side));
        AssertOnly(Read(e, Hold(Fwd(), 5).Concat(Neutral(4)).Concat(Hold(Side(side), 4))), Gesture.SpinAndGo, S(side));
        // Too slow a step (a back hold or a neutral pause past 0.125 s): the side flick is only a body feint.
        AssertOnly(Read(e, Hold(Back(), 20).Concat(Neutral(4)).Concat(Hold(Side(side), 4))), Gesture.BodyFeint, S(side));
        AssertOnly(Read(e, Hold(Back(), 5).Concat(Neutral(20)).Concat(Hold(Side(side), 4))), Gesture.BodyFeint, S(side));
        // Its back flick starts from neutral: from a side hold it is not one.
        Assert.DoesNotContain(Read(e, Hold(Side(-side), 20).Concat(Hold(Back(), 5)).Concat(Neutral(4)).Concat(Hold(Side(side), 4))),
            x => x.G.Kind == Gesture.McGeady);
        // The back flick that ended a heel-to-heel is not its start.
        var heel = Read(e, Hold(Fwd(), 5).Concat(Neutral(4)).Concat(Hold(Back(), 5)).Concat(Neutral(4)).Concat(Hold(Side(side), 4)));
        Assert.Contains(heel, x => x.G.Kind == Gesture.HeelToHeel);
        Assert.DoesNotContain(heel, x => x.G.Kind == Gesture.McGeady);
    }

    [Theory]
    [MemberData(nameof(SidesAndEntries))]
    public void StepOverAndSpinAreToldApartByNeutral(Entry e, int side)
    {
        // Forward straight on to the side: step-over. Forward, neutral, side: spin. Same timing otherwise.
        AssertOnly(Read(e, Hold(Fwd(), 6).Concat(Hold(Side(side), 8))), Gesture.StepOver, S(side));
        AssertOnly(Read(e, Hold(Fwd(), 6).Concat(Neutral(1)).Concat(Hold(Side(side), 8))), Gesture.SpinAndGo, S(side));
        // A spin needs its forward flick to start from neutral: from a side hold it is a roll then a body feint, not a spin.
        Assert.DoesNotContain(Read(e, Hold(Side(-side), 20).Concat(Hold(Fwd(), 5)).Concat(Neutral(4)).Concat(Hold(Side(side), 4))),
            x => x.G.Kind == Gesture.SpinAndGo);
        // Forward, a long neutral, then a side flick: a body feint (after the knock-on the flick asks for, sprinting).
        var late = Read(e, Hold(Fwd(), 6).Concat(Neutral(30)).Concat(Hold(Side(side), 4)));
        AssertOnly(late.Where(x => x.G.Kind != Gesture.KnockOn).ToList(), Gesture.BodyFeint, S(side));
        Assert.Equal(e == Entry.Sprint ? 1 : 0, late.Count(x => x.G.Kind == Gesture.KnockOn));
    }

    [Fact]
    public void KnockOnWaitsThenFiresWhileSprinting()
    {
        var got = Read(Entry.Sprint, Hold(Fwd(), 8));
        AssertOnly(got, Gesture.KnockOn, SkillSide.None);
        // Armed on the first forward tick; the flick released at once could still start a spin or heel-to-heel, so it fires
        // when that chance has gone: 0.125 s (15 ticks) after the release, past the 0.15 s wait.
        Assert.Equal(10 + 8 + 15 + 1, got[0].Tick);
        // Held forward past the wait: fired 0.15 s after arming, as no spin or heel-to-heel can follow.
        got = Read(Entry.Sprint, Hold(Fwd(), 60));
        AssertOnly(got, Gesture.KnockOn, SkillSide.None);
        Assert.Equal(10 + 1 + 18, got[0].Tick);
        // A flick held longer than a combo step: no wait for the combo either.
        got = Read(Entry.Sprint, Hold(Fwd(), 17));
        AssertOnly(got, Gesture.KnockOn, SkillSide.None);
        Assert.Equal(10 + 1 + 18, got[0].Tick);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void ASprintingForwardFlickThatStartsASpinOrHeelToHeelIsNoKnockOn(int side)
    {
        // The last flick inside the knock-on's own wait, and after it (a slower, still valid combo).
        foreach (int pause in new[] { 3, 14 })
        {
            AssertOnly(Read(Entry.Sprint, Hold(Fwd(), 6).Concat(Neutral(pause)).Concat(Hold(Side(side), 4))), Gesture.SpinAndGo, S(side));
            AssertOnly(Read(Entry.Sprint, Hold(Fwd(), 6).Concat(Neutral(pause)).Concat(Hold(Back(), 4))), Gesture.HeelToHeel, SkillSide.None);
        }
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
        var got = Read(Entry.Jog, Hold(Side(1), 3).Concat(Neutral(3)).Concat(Drag(6, Back())));
        AssertOnly(got, Gesture.BodyFeint, SkillSide.Right);
        // Twelve ticks later it counts.
        got = Read(Entry.Jog, Hold(Side(1), 3).Concat(Neutral(12)).Concat(Drag(6, Back())));
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
