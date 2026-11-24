using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>
/// Skill gestures read from scripted stick traces. Traces are written in the player's frame (x forward, y to his right) and
/// turned into world axes for a player facing 30 degrees (standing) or 50 (running along 30), so the frame itself is tested too.
/// The tick rate is 120: a flick counts from its third tick, a registered gesture is held 20 ticks.
/// </summary>
public class GestureReaderTests
{
    const float Heading = 30f;

    public enum Entry { Standing, Jog, Sprint }

    /// <summary>
    /// One tick of a trace in the player's frame: the right stick, and optionally the left stick, a skill key and the shoulder
    /// buttons (left = <see cref="TickIntent.Chip"/>, right = <see cref="TickIntent.Finesse"/>).
    /// </summary>
    public record struct Step(Vector2 Right, Vector2 Left = default, SkillKey Key = SkillKey.None, bool Lb = false, bool Rb = false);

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
        var p = TestData.Outfield(facing: FacingDeg(e) * MathF.PI / 180f);
        float speed = e switch { Entry.Standing => 0f, Entry.Jog => 4f, _ => 7f };
        p.Velocity = new Vector3(MathF.Cos(h) * speed, 0f, MathF.Sin(h) * speed);
        return p;
    }

    static float FacingDeg(Entry e) => e == Entry.Standing ? Heading : Heading + 20f;

    /// <summary>
    /// Feeds a trace (after a lead-in at rest) and returns every gesture registered, with its tick. Sprint is held for the
    /// sprint entry unless <paramref name="sprint"/> says otherwise; <paramref name="turnAt"/> turns his facing by 70 degrees
    /// from that trace tick on.
    /// </summary>
    static List<(int Tick, GestureRead G)> Read(Entry e, IEnumerable<Step> trace, int lead = 10, int tail = 40, bool? sprint = null, int turnAt = int.MinValue)
    {
        var t = TestData.Tuning();
        var p = Player(e);
        var all = Neutral(lead).Concat(trace).Concat(Neutral(tail)).ToList();
        var got = new List<(int, GestureRead)>();
        for (int i = 0; i < all.Count; i++)
        {
            if (i - lead == turnAt) p.Facing += 70f * MathF.PI / 180f;
            var r = Rot(all[i].Right, FacingDeg(e));
            var l = Rot(all[i].Left, FacingDeg(e));
            var it = new TickIntent { RightX = r.X, RightZ = r.Y, MoveX = l.X, MoveZ = l.Y, Sprint = (sprint ?? e == Entry.Sprint) ? 1f : 0f, SkillKey = all[i].Key,
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

    static readonly Entry[] NoSprint = { Entry.Standing, Entry.Jog };

    public static IEnumerable<object[]> SidesAndEntries()
    {
        foreach (var e in NoSprint)
            foreach (int side in new[] { -1, 1 })
                yield return new object[] { e, side };
    }

    public static IEnumerable<object[]> Entries() => NoSprint.Select(e => new object[] { e });

    public static IEnumerable<object[]> AllEntries() => Enum.GetValues<Entry>().Select(e => new object[] { e });

    [Theory]
    [MemberData(nameof(SidesAndEntries))]
    public void StepOverIsForwardThenRoundToTheSide(Entry e, int side)
    {
        AssertOnly(Read(e, Hold(Fwd(), 6).Concat(Hold(Side(side), 8))), Gesture.StepOver, S(side));
        // Turned on round the rim (50 degrees, then the side), never through rest: still a step-over.
        AssertOnly(Read(e, Hold(Fwd(), 6).Concat(Hold(Rot(Fwd(), side * 50f), 2)).Concat(Hold(Side(side), 8))), Gesture.StepOver, S(side));
        // The side 25 degrees off square (inside the 30 degree tolerance); 35 degrees off is none.
        AssertOnly(Read(e, Hold(Fwd(), 5).Concat(Hold(Rot(Side(side), side * -25f), 8))), Gesture.StepOver, S(side));
        Assert.DoesNotContain(Read(e, Hold(Fwd(), 5).Concat(Hold(Rot(Side(side), side * 35f), 8))), x => x.G.Kind == Gesture.StepOver);
    }

    [Theory]
    [MemberData(nameof(SidesAndEntries))]
    public void StepOverAndSpinAreToldApartByTheRest(Entry e, int side)
    {
        // Forward straight on to the side: step-over. Forward, rest, side: spin. Same timing otherwise.
        AssertOnly(Read(e, Hold(Fwd(), 6).Concat(Hold(Side(side), 8))), Gesture.StepOver, S(side));
        AssertOnly(Read(e, Hold(Fwd(), 6).Concat(Neutral(1)).Concat(Hold(Side(side), 8))), Gesture.SpinAndGo, S(side));
        // Forward, a long rest (0.4 s), then a side flick: only a body feint.
        AssertOnly(Read(e, Hold(Fwd(), 6).Concat(Neutral(48)).Concat(Hold(Side(side), 4))), Gesture.BodyFeint, S(side));
    }

    [Theory]
    [MemberData(nameof(SidesAndEntries))]
    public void AQuickSideFlickIsABodyFeintAndAHoldIsABallRoll(Entry e, int side)
    {
        // A feint registers on the tick the stick is back at rest.
        foreach (int hold in new[] { 3, 8, 20 })
        {
            var got = Read(e, Hold(Side(side), hold));
            AssertOnly(got, Gesture.BodyFeint, S(side));
            Assert.Equal(10 + hold + 1, got[0].Tick);
        }
        // Too short to be a flick (the stick there for less than flick_hold).
        Assert.Empty(Read(e, Hold(Side(side), 2)));
        // Held roll_min_hold (20 ticks after the first): a ball roll, and the release is no feint.
        foreach (int hold in new[] { 21, 30 })
        {
            var got = Read(e, Hold(Side(side), hold));
            AssertOnly(got, Gesture.BallRoll, S(side));
            Assert.Equal(10 + 1 + 20, got[0].Tick);
        }
        // Held on, it registers again as soon as the last one's hold has run out.
        Assert.Equal(new[] { 31, 52 }, Read(e, Hold(Side(side), 60)).Select(x => x.Tick));
    }

    static IEnumerable<Step> Drag(int ticks, Vector2 left, bool lb = true, bool rb = true) =>
        Enumerable.Repeat(new Step(Vector2.Zero, left, Lb: lb, Rb: rb), ticks);

    [Theory]
    [MemberData(nameof(Entries))]
    public void DragBackIsTheLeftStickBackWithBothShoulders(Entry e)
    {
        AssertOnly(Read(e, Drag(8, Back())), Gesture.DragBack, SkillSide.None);
        // Within 45 degrees of straight back from his facing (drag_back_stick_deg 135).
        AssertOnly(Read(e, Drag(8, Rot(Back(), 40f))), Gesture.DragBack, SkillSide.None);
        AssertOnly(Read(e, Drag(8, Rot(Back(), -40f))), Gesture.DragBack, SkillSide.None);
        Assert.Empty(Read(e, Drag(8, Rot(Back(), 50f))));
        // Shoulders first, then the stick back: registers when the stick arrives.
        var got = Read(e, Drag(5, Vector2.Zero).Concat(Drag(8, Back())));
        AssertOnly(got, Gesture.DragBack, SkillSide.None);
        Assert.Equal(10 + 5 + 1, got[0].Tick);
        // Held on, it registers again each time the last one's hold has run out.
        Assert.Equal(new[] { 11, 32, 53 }, Read(e, Drag(60, Back())).Select(x => x.Tick));
        // One shoulder only, the stick back without shoulders, or both shoulders with the stick sideways or barely pushed: nothing.
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
    [MemberData(nameof(Entries))]
    public void AForwardFlickAloneDoesNothing(Entry e)
    {
        Assert.Empty(Read(e, Hold(Fwd(), 8)));
        Assert.Empty(Read(e, Hold(Fwd(), 60)));
    }

    [Theory]
    [MemberData(nameof(SidesAndEntries))]
    public void ElasticoIsBackThenRoundToTheSide(Entry e, int side)
    {
        AssertOnly(Read(e, Hold(Back(), 6).Concat(Hold(Side(side), 8))), Gesture.Elastico, S(side));
        AssertOnly(Read(e, Hold(Back(), 6).Concat(Hold(Rot(Back(), side * -50f), 2)).Concat(Hold(Side(side), 12))), Gesture.Elastico, S(side));
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void HeelToHeelIsAForwardFlickRestThenBack(Entry e)
    {
        var got = Read(e, Hold(Fwd(), 5).Concat(Neutral(4)).Concat(Hold(Back(), 5)));
        AssertOnly(got, Gesture.HeelToHeel, SkillSide.None);
        Assert.Equal(10 + 5 + 4 + 3, got[0].Tick); // on the back flick's third tick (flick_hold)
        // The back flick of a heel-to-heel turned to the side is no elastico.
        AssertOnly(Read(e, Hold(Fwd(), 5).Concat(Neutral(4)).Concat(Hold(Back(), 14)).Concat(Hold(Side(1), 4))), Gesture.HeelToHeel, SkillSide.None);
        // Too long a rest (past combo_step_window): nothing.
        Assert.Empty(Read(e, Hold(Fwd(), 5).Concat(Neutral(25)).Concat(Hold(Back(), 5))));
    }

    [Theory]
    [MemberData(nameof(SidesAndEntries))]
    public void SpinIsAForwardFlickRestThenTheSide(Entry e, int side)
    {
        var got = Read(e, Hold(Fwd(), 5).Concat(Neutral(4)).Concat(Hold(Side(side), 4)));
        AssertOnly(got, Gesture.SpinAndGo, S(side)); // the side's release, inside the spin's hold, is no feint
        Assert.Equal(10 + 5 + 4 + 3, got[0].Tick);
        // The side held on is still only the spin (the roll it would complete falls inside the spin's hold).
        AssertOnly(Read(e, Hold(Fwd(), 5).Concat(Neutral(4)).Concat(Hold(Side(side), 40))), Gesture.SpinAndGo, S(side));
        // Its last flick may come later than a heel-to-heel's (roll_step_window).
        AssertOnly(Read(e, Hold(Fwd(), 5).Concat(Neutral(35)).Concat(Hold(Side(side), 30))), Gesture.SpinAndGo, S(side));
    }

    [Theory]
    [MemberData(nameof(SidesAndEntries))]
    public void McGeadyIsABackFlickRestThenTheSide(Entry e, int side)
    {
        var got = Read(e, Hold(Back(), 5).Concat(Neutral(4)).Concat(Hold(Side(side), 4)));
        AssertOnly(got, Gesture.McGeady, S(side));
        Assert.Equal(10 + 5 + 4 + 3, got[0].Tick);
        AssertOnly(Read(e, Hold(Back(), 5).Concat(Neutral(4)).Concat(Hold(Side(side), 40))), Gesture.McGeady, S(side));
        // Back straight on to the side is the elastico; the same flicks after a forward flick are the spin.
        AssertOnly(Read(e, Hold(Back(), 5).Concat(Hold(Side(side), 8))), Gesture.Elastico, S(side));
        AssertOnly(Read(e, Hold(Fwd(), 5).Concat(Neutral(4)).Concat(Hold(Side(side), 4))), Gesture.SpinAndGo, S(side));
        // Too long a rest: the side flick is only a body feint.
        AssertOnly(Read(e, Hold(Back(), 5).Concat(Neutral(48)).Concat(Hold(Side(side), 4))), Gesture.BodyFeint, S(side));
        // The back flick that ended a heel-to-heel starts none inside the heel-to-heel's hold.
        var heel = Read(e, Hold(Fwd(), 5).Concat(Neutral(4)).Concat(Hold(Back(), 5)).Concat(Neutral(4)).Concat(Hold(Side(side), 4)));
        AssertOnly(heel, Gesture.HeelToHeel, SkillSide.None);
    }

    public static IEnumerable<object[]> EveryGesture()
    {
        yield return new object[] { Hold(Fwd(), 6).Concat(Hold(Side(1), 8)).ToArray() };
        yield return new object[] { Hold(Side(-1), 5).ToArray() };
        yield return new object[] { Hold(Side(1), 60).ToArray() };
        yield return new object[] { Drag(8, Back()).ToArray() };
        yield return new object[] { Hold(Back(), 6).Concat(Hold(Side(-1), 8)).ToArray() };
        yield return new object[] { Hold(Fwd(), 5).Concat(Neutral(4)).Concat(Hold(Back(), 5)).ToArray() };
        yield return new object[] { Hold(Fwd(), 5).Concat(Neutral(4)).Concat(Hold(Side(1), 4)).ToArray() };
        yield return new object[] { Hold(Back(), 5).Concat(Neutral(4)).Concat(Hold(Side(1), 4)).ToArray() };
    }

    [Theory]
    [MemberData(nameof(EveryGesture))]
    public void NoGestureRegistersWhileSprintIsHeld(Step[] trace)
    {
        Assert.NotEmpty(Read(Entry.Jog, trace));
        Assert.Empty(Read(Entry.Sprint, trace));
        // At a sprint's pace with sprint let go of, it registers.
        Assert.NotEmpty(Read(Entry.Sprint, trace, sprint: false));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void DirectionsAreAgainstTheFacingFixedByTheFirstStep(int side)
    {
        // He turns 70 degrees after the first step: the side is still read against the facing he had then.
        AssertOnly(Read(Entry.Jog, Hold(Fwd(), 6).Concat(Hold(Side(side), 8)), turnAt: 3), Gesture.StepOver, S(side));
        AssertOnly(Read(Entry.Jog, Hold(Back(), 6).Concat(Hold(Side(side), 8)), turnAt: 3), Gesture.Elastico, S(side));
        AssertOnly(Read(Entry.Jog, Hold(Fwd(), 5).Concat(Neutral(4)).Concat(Hold(Back(), 5)), turnAt: 7), Gesture.HeelToHeel, SkillSide.None);
        // Turned before the gesture began, the same sticks are read against the new facing: no step-over to that side.
        Assert.DoesNotContain(Read(Entry.Jog, Hold(Fwd(), 6).Concat(Hold(Side(side), 8)), turnAt: -5), x => x.G.Kind == Gesture.StepOver && x.G.Side == S(side));
    }

    [Fact]
    public void AGestureInsideTheLastOnesHoldIsDropped()
    {
        // A drag-back 3 ticks after a body feint (held 20 ticks) is dropped, not kept for later.
        var got = Read(Entry.Jog, Hold(Side(1), 3).Concat(Neutral(3)).Concat(Drag(6, Back())));
        AssertOnly(got, Gesture.BodyFeint, SkillSide.Right);
        // After the hold it counts.
        got = Read(Entry.Jog, Hold(Side(1), 3).Concat(Neutral(25)).Concat(Drag(6, Back())));
        Assert.Equal(new[] { Gesture.BodyFeint, Gesture.DragBack }, got.Select(x => x.G.Kind));
    }

    [Fact]
    public void ARegisteredGestureIsHeldForTheCooldown()
    {
        var t = TestData.Tuning();
        var p = Player(Entry.Jog);
        int at = 0;
        for (int tick = 1; tick <= 80; tick++)
        {
            var s = tick is >= 11 and < 14 ? Rot(Side(1), FacingDeg(Entry.Jog)) : Vector2.Zero;
            var g = GestureReader.Read(p, new TickIntent { RightX = s.X, RightZ = s.Y }, tick, t);
            if (g.Kind != Gesture.None) at = tick;
            var held = GestureReader.Held(p, tick, t);
            if (at > 0 && tick <= at + 20) { Assert.Equal(Gesture.BodyFeint, held.Kind); Assert.Equal(SkillSide.Right, held.Side); }
            else Assert.Equal(Gesture.None, held.Kind);
        }
        Assert.Equal(14, at);
    }

    [Theory]
    [MemberData(nameof(AllEntries))]
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
        // A forward push released, springing back past centre (an overshoot below the actuation) to rest.
        var spring = Hold(Fwd(), 30).Concat(new[] { new Step(Fwd(0.6f)), new Step(Fwd(0.1f)), new Step(Back(0.35f)), new Step(Back(0.1f)) });
        Assert.Empty(Read(e, spring));
    }

    /// <summary>Feeds a right-stick trace (world axes) to the heavy-touch reader; returns the ticks it armed on and the player.</summary>
    static (List<int> Armed, PlayerState P) Heavy(Func<int, Vector2> stick, Func<int, bool> sprint, int ticks, bool canArm = true, Func<int, bool>? played = null)
    {
        var t = TestData.Tuning();
        var p = Player(Entry.Sprint);
        var armed = new List<int>();
        for (int tick = 1; tick <= ticks; tick++)
        {
            if (played != null && played(tick)) p.KnockOnRequested = false;
            var s = stick(tick);
            if (GestureReader.ReadHeavyTouch(p, new TickIntent { RightX = s.X, RightZ = s.Y, Sprint = sprint(tick) ? 1f : 0f }, tick, canArm, t)) armed.Add(tick);
        }
        return (armed, p);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(45f)]
    [InlineData(-120f)]
    public void ASprintingFlickArmsAHeavyTouchWhereTheStickPoints(float deg)
    {
        var dir = Rot(Fwd(), deg);
        var (armed, p) = Heavy(i => i is >= 5 and < 12 ? dir : Vector2.Zero, _ => true, 40);
        Assert.Equal(new[] { 5 }, armed);
        Assert.True(p.KnockOnRequested);
        Assert.True(Vector2.Distance(dir, p.GHeavyDir) < 1e-4f);
        Assert.Equal(Gesture.KnockOn, p.LastGesture);
    }

    [Fact]
    public void TheHeavyTouchAimsWhereTheStickPointedBeforeItsRelease()
    {
        // Flicked forward, swung round to 60 degrees, then let go: the aim is the 60 degrees.
        var (_, p) = Heavy(i => i is >= 5 and < 8 ? Fwd() : i is >= 8 and < 14 ? Rot(Fwd(), 60f) : Vector2.Zero, _ => true, 40);
        Assert.True(p.KnockOnRequested);
        Assert.True(Vector2.Distance(Rot(Fwd(), 60f), p.GHeavyDir) < 1e-4f);
    }

    [Fact]
    public void AHeavyTouchNeedsSprintAStrongFlickAndTheBall()
    {
        Assert.Empty(Heavy(i => i >= 5 ? Fwd() : Vector2.Zero, _ => false, 40).Armed);
        Assert.Empty(Heavy(i => i >= 5 ? Fwd(0.8f) : Vector2.Zero, _ => true, 40).Armed);
        Assert.Empty(Heavy(i => i >= 5 ? Fwd() : Vector2.Zero, _ => true, 40, canArm: false).Armed);
        // The stick already past the actuation when sprint goes down is no flick.
        Assert.Empty(Heavy(_ => Fwd(), i => i >= 10, 40).Armed);
    }

    [Fact]
    public void AnArmedHeavyTouchIsDroppedWithoutSprintOrAfterTheDelay()
    {
        // Sprint let go of 10 ticks after the flick.
        var (armed, p) = Heavy(i => i is >= 5 and < 9 ? Fwd() : Vector2.Zero, i => i < 15, 40);
        Assert.Equal(new[] { 5 }, armed);
        Assert.False(p.KnockOnRequested);
        // No touch within heavy_touch_max_delay (2 s = 240 ticks).
        (_, p) = Heavy(i => i is >= 5 and < 9 ? Fwd() : Vector2.Zero, _ => true, 5 + 240);
        Assert.True(p.KnockOnRequested);
        (_, p) = Heavy(i => i is >= 5 and < 9 ? Fwd() : Vector2.Zero, _ => true, 5 + 242);
        Assert.False(p.KnockOnRequested);
        // Played by a touch, a new flick arms the next.
        (armed, _) = Heavy(i => i is >= 5 and < 9 or >= 30 and < 34 ? Fwd() : Vector2.Zero, _ => true, 60, played: i => i == 20);
        Assert.Equal(new[] { 5, 30 }, armed);
    }
}
