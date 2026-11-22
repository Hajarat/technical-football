using System.Numerics;
using FootballSim.Core;
using Xunit.Abstractions;

namespace FootballSim.Core.Tests;

/// <summary>Turn fatigue: what adds to it, how it drains and resets, and what it does to the early touch after a turn.</summary>
public class TurnFatigueTests
{
    readonly ITestOutputHelper _out;
    public TurnFatigueTests(ITestOutputHelper output) => _out = output;

    static World Lab(ulong seed, int close, Tuning? t = null, int skillMoves = 75)
    {
        var s = TestData.Sheet(75);
        s.CloseDribbling = close;
        s.SprintDribbling = 75;
        s.SkillMoves = skillMoves;
        var sc = new Scenario
        {
            Id = "lab",
            Pitch = new PitchSpec { Length = 300f, Width = 200f },
            Ball = new StartSpot { X = 150.5f, Z = 0f },
            Attacker = new StartSpot { X = 150f, Z = 0f },
        };
        var w = new World(t ?? new Tuning(), sc, s, TestData.Sheet(50), seed);
        w.Begin();
        return w;
    }

    static TickIntent Stick(float deg, float sprint = 0f)
    {
        float a = deg * MathF.PI / 180f;
        return new TickIntent { MoveX = MathF.Cos(a), MoveZ = MathF.Sin(a), Sprint = sprint };
    }

    static void Step(World w, TickIntent it)
    {
        var buf = new TickIntent[w.PlayerCount];
        buf[0] = it;
        w.Step(buf);
    }

    static bool Touched(World w) => w.Events.Any(e => e.Kind == EventKind.Touch && e.Team == 0);

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(30f, 0f)]
    [InlineData(45f, 5f)]
    [InlineData(60f, 10f)]
    [InlineData(90f, 20f)]
    [InlineData(135f, 32f)]
    [InlineData(180f, 40f)]
    [InlineData(-90f, 20f)]
    public void TheGainFollowsTheAngleFromTheFacingAndCloseDribbling(float deg, float turn)
    {
        var t = new Tuning();
        float facing = 0.7f, a = facing + deg * MathF.PI / 180f;
        var push = new Vector3(MathF.Cos(a), 0.3f, MathF.Sin(a)) * 6f;
        foreach (var (rating, gain) in new[] { (1, 1.5f), (50, 1.0f), (99, 0.5f) })
        {
            var s = TestData.Sheet(60);
            s.CloseDribbling = rating;
            s.SprintDribbling = 100 - rating;   // (sprint dribbling plays no part)
            Assert.Equal(turn * gain, TouchPlanner.TurnFatigueGain(s, facing, push, t), 2);
        }
        Assert.Equal(0f, TouchPlanner.TurnFatigueGain(TestData.Sheet(60), facing, Vector3.Zero, t));
    }

    [Fact]
    public void OnlyTheTouchControllersTouchesAddFatigue()
    {
        foreach (var k in new[] { ActionKind.Close, ActionKind.Jog, ActionKind.Sprint, ActionKind.Turn, ActionKind.Cut, ActionKind.Stop, ActionKind.KnockOn })
            Assert.True(TouchPlanner.AddsTurnFatigue(k), k.ToString());
        foreach (var k in Enum.GetValues<ActionKind>())
            if (k is ActionKind.First or ActionKind.Soft or ActionKind.TrapLow or ActionKind.TrapMid or ActionKind.TrapHigh || k >= ActionKind.Power)
                Assert.False(TouchPlanner.AddsTurnFatigue(k), k.ToString());
    }

    /// <summary>A zig-zag: the stick swung 90 degrees (45 either side of +X) on the tick after each touch that followed the last swing.</summary>
    static IEnumerable<(World W, bool Touch, float Before, float FacingAtContact)> ZigZag(World w, int ticks, float sprint = 0f)
    {
        float side = 1f;
        for (int i = 0; i < 120; i++) { Step(w, Stick(0f, sprint)); }
        for (int i = 0; i < ticks; i++)
        {
            float before = w.Attacker.TurnFatigue;
            Step(w, Stick(45f * side, sprint));
            bool touch = Touched(w);
            yield return (w, touch, before, w.Attacker.Facing);
            if (touch && w.Attacker.TouchAction is { Made: true, Kind: ActionKind.Turn or ActionKind.Cut }) side = -side;
        }
    }

    [Fact]
    public void EachContactAddsItsGainOnceAndNothingElseDoes()
    {
        // No recovery: the fatigue only ever changes at a contact, by that contact's gain.
        var t = new Tuning();
        t.Touch.FatigueRecovery = new RatingRange(0f, 0f);
        foreach (ulong seed in new ulong[] { 1, 2, 3 })
        {
            var w = Lab(seed, 40, t);
            int contacts = 0, turned = 0;
            foreach (var (_, touch, before, facing) in ZigZag(w, 5 * 120))
            {
                var p = w.Attacker;
                if (!p.InPossession) continue;
                float expected = before;
                if (touch)
                {
                    var a = p.TouchAction!;
                    Assert.True(a.Made && a.ContactTick == w.Tick);
                    if (TouchPlanner.AddsTurnFatigue(a.Kind))
                    {
                        float gain = TouchPlanner.TurnFatigueGain(p.Sheet, facing, a.BallOutVelocity, t);
                        expected = MathF.Min(PlayerState.TurnFatigueMax, before + gain);
                        contacts++;
                        if (gain > 0f) turned++;
                    }
                }
                Assert.Equal(expected, p.TurnFatigue, 3);
            }
            _out.WriteLine($"seed {seed}: {contacts} contacts, {turned} with a gain, fatigue {w.Attacker.TurnFatigue:F1}");
            Assert.True(turned >= 4, $"seed {seed}: only {turned} turning contacts");
        }
    }

    [Fact]
    public void RecoveryIsLinearByCloseDribblingAndStopsAtZero()
    {
        foreach (var (rating, perSecond) in new[] { (1, 10f), (50, 20f), (99, 30f) })
        {
            var w = Lab(1, rating);
            for (int i = 0; i < 60; i++) Step(w, Stick(0f));
            var p = w.Attacker;
            Assert.True(p.InPossession);
            p.TurnFatigue = 80f;
            // A straight jog adds nothing (the ball is pushed where he faces), so only the recovery acts.
            for (int i = 1; i <= 120; i++)
            {
                Step(w, Stick(0f));
                Assert.Equal(80f - perSecond * i * w.Tuning.Dt, p.TurnFatigue, 2);
            }
            for (int i = 0; i < 8 * 120; i++) Step(w, Stick(0f));
            Assert.Equal(0f, p.TurnFatigue);
        }
    }

    [Fact]
    public void FatigueIsGoneWhenPossessionChangesAndAfterAShot()
    {
        // Gained: the first touch of a loose ball starts from zero.
        var w = Lab(1, 40);
        var p = w.Attacker;
        p.TurnFatigue = 70f;
        for (int i = 0; i < 240 && !p.InPossession; i++) Step(w, Stick(0f));
        Assert.True(p.InPossession);
        Assert.Equal(0f, p.TurnFatigue);

        // A shot: zero from the strike on.
        p.TurnFatigue = 90f;
        var shoot = Stick(0f); shoot.ShootHeld = true;
        for (int i = 0; i < 30; i++) Step(w, shoot);
        for (int i = 0; i < 240 && w.Shots.Count == 0; i++) Step(w, Stick(0f));
        Assert.True(w.Shots.Count > 0, "no shot");
        Assert.Equal(0f, p.TurnFatigue);

        // Lost (a knock-on left to run away): zero.
        var w2 = Lab(2, 40);
        var q = w2.Attacker;
        for (int i = 0; i < 120; i++) Step(w2, Stick(0f, 1f));
        var knock = Stick(0f, 1f); knock.KnockOn = true;
        for (int i = 0; i < 60 && q.LastTouchKind != ActionKind.KnockOn; i++) Step(w2, knock);
        Assert.Equal(ActionKind.KnockOn, q.LastTouchKind);
        q.TurnFatigue = 60f;
        for (int i = 0; i < 10 * 120 && q.InPossession; i++) Step(w2, default);
        Assert.False(q.InPossession);
        Assert.Equal(0f, q.TurnFatigue);
    }

    [Fact]
    public void TheEarlyFractionIsTodaysAtZeroFatigueAndTheNormalCadenceAtFullPenalty()
    {
        var t = new Tuning();
        foreach (int rating in new[] { 1, 30, 50, 72, 99 })
        {
            var p = Lab(1, rating, t).Attacker;
            foreach (float deg in new[] { 0f, 10f, 20f, 30f, 45f, 60f, 90f, 150f })
            {
                float today = MathF.Min(1f, t.Touch.EarlyExecution.Evaluate(deg) * t.Touch.EarlyExecutionDribbling.ForRating(rating));
                p.TurnFatigue = 0f;
                Assert.Equal(today, TouchPlanner.EarlyFraction(p, deg, t), 5);
                p.TurnFatigue = 20f;   // the penalty curve's first knee: still none
                Assert.Equal(today, TouchPlanner.EarlyFraction(p, deg, t), 5);
                p.TurnFatigue = 40f;   // half the penalty: half way back to the normal cadence
                Assert.Equal(today + (1f - today) * 0.5f, TouchPlanner.EarlyFraction(p, deg, t), 5);
                foreach (float full in new[] { 60f, 80f, 100f })
                {
                    p.TurnFatigue = full;
                    Assert.Equal(1f, TouchPlanner.EarlyFraction(p, deg, t), 5);
                }
                for (float f = 0f; f <= 100f; f += 5f)
                {
                    p.TurnFatigue = f;
                    Assert.InRange(TouchPlanner.EarlyFraction(p, deg, t), today - 1e-5f, 1f);
                }
            }
        }
    }

    /// <summary>The number of the zig-zag's turning touch at which the early touch is first gone (the penalty whole), or -1.</summary>
    int CutsUntilTheEarlyTouchIsGone(int close, ulong seed)
    {
        var w = Lab(seed, close);
        int cuts = 0;
        float peak = 0f;
        foreach (var (_, touch, _, _) in ZigZag(w, 12 * 120))
        {
            var p = w.Attacker;
            Assert.True(p.InPossession, $"close dribbling {close} seed {seed}: lost the ball at tick {w.Tick}");
            if (!touch || p.TouchAction is not { Kind: ActionKind.Turn or ActionKind.Cut }) continue;
            cuts++;
            peak = MathF.Max(peak, p.TurnFatigue);
            if (w.Tuning.Touch.FatiguePenalty.Evaluate(p.TurnFatigue) >= 1f)
            {
                _out.WriteLine($"close dribbling {close} seed {seed}: early touch gone at turn {cuts} (fatigue {p.TurnFatigue:F1})");
                return cuts;
            }
        }
        _out.WriteLine($"close dribbling {close} seed {seed}: early touch kept through {cuts} turns (peak fatigue {peak:F1})");
        return -1;
    }

    [Fact]
    public void AZigZagLosesItsEarlyTouchesSoonerAtLowCloseDribbling()
    {
        foreach (ulong seed in new ulong[] { 1, 2, 3, 4, 5 })
        {
            int lo = CutsUntilTheEarlyTouchIsGone(30, seed), hi = CutsUntilTheEarlyTouchIsGone(90, seed);
            Assert.True(lo > 0, $"seed {seed}: close dribbling 30 never lost its early touch");
            Assert.True(hi < 0 || hi > lo, $"seed {seed}: close dribbling 90 lost it at turn {hi}, 30 at turn {lo}");
        }
    }

    [Fact]
    public void ASkillMovesOwnContactsAddNoFatigue()
    {
        var t = new Tuning();
        t.Touch.FatigueRecovery = new RatingRange(0f, 0f);
        var w = Lab(1, 40, t);
        var p = w.Attacker;
        for (int i = 0; i < 120; i++) Step(w, Stick(0f));
        // A ball roll to his right (the right stick held to the side), the left stick released so no dribble touch is asked for.
        bool moved = false;
        for (int i = 0; i < 240; i++)
        {
            float before = p.TurnFatigue;
            bool inMove = p.SkillMove != null;
            Step(w, new TickIntent { MoveX = 1f, RightZ = i < 30 ? 1f : 0f });
            if (inMove && p.SkillMove != null)
            {
                moved = true;
                Assert.Equal(before, p.TurnFatigue);
            }
        }
        Assert.True(moved, "no skill move was made");
    }
}
