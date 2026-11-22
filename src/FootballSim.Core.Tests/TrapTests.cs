using System.Numerics;
using FootballSim.Core;
using Xunit.Abstractions;

namespace FootballSim.Core.Tests;

/// <summary>Height-aware traps: a loose ball's first contact is trapped by the band its height is in, and kept.</summary>
public class TrapTests
{
    static readonly Tuning T = new();
    readonly ITestOutputHelper _out;

    public TrapTests(ITestOutputHelper output) { _out = output; }

    [Theory]
    [InlineData(0.11f, ActionKind.TrapLow)]
    [InlineData(0.30f, ActionKind.TrapLow)]
    [InlineData(0.60f, ActionKind.TrapMid)]
    [InlineData(0.90f, ActionKind.TrapMid)]
    [InlineData(1.20f, ActionKind.TrapHigh)]
    [InlineData(1.55f, ActionKind.TrapHigh)]
    public void TheBandPicksTheTrap(float height, ActionKind expect)
    {
        var s = TestData.Sheet(60);
        Assert.Equal(expect, TouchModel.TrapKindFor(s, Vector3.Zero, new Vector3(0.3f, height, 0f), T));
    }

    [Fact]
    public void AHigherBandReachesLessFarAndNothingTakesABallOverTheHighBand()
    {
        var s = TestData.Sheet(60);
        float reach = TouchModel.FootReach(s, T);
        var k = T.Touch.Profiles;
        // just inside the low band's reach, a mid- or high-band ball at that distance is out of its band's reach
        var near = new Vector3(reach * 0.95f, 0f, 0f);
        Assert.Equal(ActionKind.TrapLow, TouchModel.TrapKindFor(s, Vector3.Zero, near with { Y = T.Ball.Radius }, T));
        Assert.Null(TouchModel.TrapKindFor(s, Vector3.Zero, near with { Y = 0.7f }, T));
        Assert.Null(TouchModel.TrapKindFor(s, Vector3.Zero, near with { Y = 1.3f }, T));
        Assert.Equal(ActionKind.TrapHigh, TouchModel.TrapKindFor(s, Vector3.Zero, new Vector3(reach * k.TrapHigh.Reach * 0.95f, 1.3f, 0f), T));
        Assert.Null(TouchModel.TrapKindFor(s, Vector3.Zero, new Vector3(0.2f, k.TrapHigh.MaxBallHeight + 0.05f, 0f), T));
        // a touch at the feet stays in the low band
        Assert.False(TouchModel.InReach(s, Vector3.Zero, new Vector3(0.3f, 0.6f, 0f), T));
    }

    /// <summary>A long empty pitch: the attacker standing at x = 2 facing +X, the ball served at him from ahead.</summary>
    static World Served(float height, ulong seed, out float serveSpeed)
    {
        var sc = new Scenario
        {
            Id = "lab",
            Pitch = new PitchSpec { Length = 150f },
            Ball = new StartSpot { X = 30f },
            Attacker = new StartSpot { X = 2.0f },
        };
        var w = new World(new Tuning(), sc, TestData.Sheet(60), TestData.Sheet(50), seed);
        w.Begin();
        // served from 2.4 m ahead along the ground or in a flat arc that comes back to the same height at the player
        float vx = 5f, from = 4.6f, t = (from - 2.6f) / vx;
        serveSpeed = vx;
        w.Ball.Position = new Vector3(from, height, 0f);
        w.Ball.Velocity = new Vector3(-vx, height > T.Ball.Radius + 0.01f ? 0.5f * T.Ball.Gravity * t : 0f, 0f);
        w.Ball.Spin = Vector3.Zero;
        return w;
    }

    [Theory]
    [InlineData(0.11f, ActionKind.TrapLow)]
    [InlineData(0.70f, ActionKind.TrapMid)]
    [InlineData(1.30f, ActionKind.TrapHigh)]
    public void AServedBallIsTrappedByItsBandAndKept(float height, ActionKind expect)
    {
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var w = Served(height, seed, out float serve);
            var p = w.Attacker;
            var buf = new TickIntent[w.PlayerCount];
            ActionKind? first = null;
            float contactY = float.NaN, outRel = float.NaN;
            bool lost = false;
            int touches = 0;
            float maxAway = 0f;
            for (int i = 0; i < 4 * 120 && w.State == RoundState.Live; i++)
            {
                w.Step(buf);
                foreach (var e in w.Events)
                {
                    if (e.Team != 0) continue;
                    if (e.Kind == EventKind.PossessionLost) lost = true;
                    if (e.Kind != EventKind.Touch) continue;
                    touches++;
                    if (first == null)
                    {
                        first = p.TouchAction!.Kind;
                        contactY = e.Position.Y;
                        outRel = TouchModel.Flat(w.Ball.Velocity - p.Velocity).Length();
                    }
                }
                if (first != null) maxAway = MathF.Max(maxAway, TouchModel.HorizontalDistance(p.Position, w.Ball.Position));
            }
            _out.WriteLine($"seed {seed}: {first} at {contactY:F2} m, out {outRel:F2} m/s relative (served {serve:F1}), touches {touches}, ball up to {maxAway:F2} m away, in possession {p.InPossession}");
            Assert.Equal(expect, first);
            Assert.False(lost, $"seed {seed}: possession lost");
            Assert.True(p.InPossession, $"seed {seed}: not in possession");
            // killed: well under the serve's speed relative to him, and kept near him
            Assert.True(outRel < 0.6f * serve, $"seed {seed}: {outRel:F2} m/s relative after the trap");
            Assert.True(maxAway < 2.0f, $"seed {seed}: ball {maxAway:F2} m away");
        }
    }

    [Fact]
    public void AServedHighBallIsPlayedOnAfterTheTrap()
    {
        // With the stick pushed on, a chested ball is dropped toward the stick and dribbled on: further touches, no loss.
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var w = Served(1.30f, seed, out _);
            var p = w.Attacker;
            var buf = new TickIntent[w.PlayerCount];
            buf[0] = new TickIntent { MoveX = 1 };
            int touches = 0;
            ActionKind? first = null;
            for (int i = 0; i < 4 * 120 && w.State == RoundState.Live; i++)
            {
                w.Step(buf);
                foreach (var e in w.Events)
                {
                    if (e.Team != 0) continue;
                    Assert.False(e.Kind == EventKind.PossessionLost, $"seed {seed}: possession lost");
                    if (e.Kind == EventKind.Touch) { touches++; first ??= p.TouchAction!.Kind; }
                }
            }
            Assert.Equal(ActionKind.TrapHigh, first);
            Assert.True(p.InPossession);
            Assert.True(touches >= 4, $"seed {seed}: {touches} touches");
            Assert.True(p.Position.X > 6f, $"seed {seed}: at x {p.Position.X:F1}");
        }
    }

    [Fact]
    public void AGroundTrapIsTheOldStickReleasedFirstTouch()
    {
        // trap_low's defaults keep the first touch with the stick released as it was: the ball at the player's velocity plus the
        // first_touch share of its relative velocity (rotated by the error), rolling.
        var s = TestData.Sheet(60);
        var p = TestData.Outfield(s);
        p.Velocity = new Vector3(1f, 0, 0);
        p.RunSpeed = 1f;
        var b = new Ball(T.Ball) { Position = new Vector3(0.4f, T.Ball.Radius, 0), Velocity = new Vector3(-6f, 0, 0) };
        var r = TouchPlanner.Step(p, default, b, false, TouchStepMode.Normal, 120, 1f, T, null, new SimRng(3));
        Assert.NotNull(r);
        Assert.Equal(ActionKind.TrapLow, r!.Value.Kind);
        var rel = new Vector3(-7f, 0, 0) * TouchModel.FirstTouchSpeedKept(s, T);
        var expect = new Vector3(1f, 0, 0) + TouchModel.Rotate(rel, r.Value.DirectionErrorDeg);
        Assert.Equal(expect.X, b.Velocity.X, 4);
        Assert.Equal(expect.Z, b.Velocity.Z, 4);
        Assert.Equal(0f, b.Velocity.Y, 4);
    }
}
