using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

public class BallTests
{
    static Tuning Vacuum()
    {
        var t = new Tuning();
        t.Ball.DragCurve = Curve.Constant(0f);
        t.Ball.LiftCurve = Curve.Constant(0f);
        return t;
    }

    static Ball Launch(Tuning t, float speed, float elevDeg, Vector3? spin = null)
    {
        var b = new Ball(t.Ball) { Position = new Vector3(0, t.Ball.Radius, 0) };
        float e = elevDeg * MathF.PI / 180f;
        b.Velocity = new Vector3(speed * MathF.Cos(e), speed * MathF.Sin(e), 0);
        b.Spin = spin ?? Vector3.Zero;
        return b;
    }

    /// <summary>Flies the ball until its first bounce and returns the landing X.</summary>
    static float FlyToFirstBounce(Ball b, Tuning t)
    {
        for (int i = 0; i < 20000; i++)
        {
            var r = b.Step(t.Dt, null, null);
            if (r.Bounced) return r.BouncePoint.X;
        }
        throw new Exception("never landed");
    }

    [Fact]
    public void DragFreeRangeMatchesClosedForm()
    {
        var t = Vacuum();
        const float v = 20f, deg = 40f;
        float expected = v * v * MathF.Sin(2 * deg * MathF.PI / 180f) / t.Ball.Gravity;
        float actual = FlyToFirstBounce(Launch(t, v, deg), t);
        Assert.InRange(actual / expected, 0.985f, 1.015f);
    }

    [Fact]
    public void DragShortensTheRange()
    {
        var t = new Tuning();
        float free = FlyToFirstBounce(Launch(Vacuum(), 30f, 20f), Vacuum());
        float drag = FlyToFirstBounce(Launch(t, 30f, 20f), t);
        Assert.True(drag < free * 0.95f, $"drag {drag} vs free {free}");
    }

    [Theory]
    [InlineData(1f, -1)]   // spin about +Y bends a ball travelling +X toward -Z
    [InlineData(-1f, 1)]
    public void SidespinBendsTowardTheExpectedSide(float spinY, int expectedSign)
    {
        var t = new Tuning();
        var b = Launch(t, 25f, 5f, new Vector3(0, spinY * 8f * 2f * MathF.PI, 0));
        for (int i = 0; i < 60; i++) b.Step(t.Dt, null, null);
        Assert.True(MathF.Sign(b.Position.Z) == expectedSign && MathF.Abs(b.Position.Z) > 0.3f, $"z = {b.Position.Z}");
    }

    [Fact]
    public void BackspinLiftsTheBall()
    {
        var t = new Tuning();
        var plain = Launch(t, 20f, 10f);
        var spun = Launch(t, 20f, 10f, Vector3.Cross(plain.Velocity, Vector3.UnitY) / plain.Velocity.Length() * 5f * 2f * MathF.PI);
        for (int i = 0; i < 40; i++) { plain.Step(t.Dt, null, null); spun.Step(t.Dt, null, null); }
        Assert.True(spun.Position.Y > plain.Position.Y + 0.1f);
    }

    [Fact]
    public void BounceHeightsFallByRestitution()
    {
        var t = Vacuum();
        t.Ball.RestitutionCurve = Curve.Constant(0.6f);
        var b = new Ball(t.Ball) { Position = new Vector3(0, 2f, 0) };
        float prevApex = 2f - t.Ball.Radius; // height of the centre above its resting height
        var apexes = new List<float>();
        float apex = 0;
        bool rising = false;
        for (int i = 0; i < 4000 && apexes.Count < 3; i++)
        {
            float before = b.Velocity.Y;
            b.Step(t.Dt, null, null);
            if (b.Velocity.Y > 0) rising = true;
            if (rising) apex = MathF.Max(apex, b.Position.Y - t.Ball.Radius);
            if (rising && b.Velocity.Y <= 0 && before > 0) { apexes.Add(apex); apex = 0; rising = false; }
        }
        Assert.Equal(3, apexes.Count);
        Assert.InRange(apexes[0] / prevApex, 0.34f, 0.38f); // e^2 = 0.36
        Assert.InRange(apexes[1] / apexes[0], 0.34f, 0.38f);
        Assert.InRange(apexes[2] / apexes[1], 0.33f, 0.39f);
    }

    /// <summary>Drops the ball onto the ground in one tick from just above it and returns it after the bounce.</summary>
    static Ball BounceOnce(Tuning t, Vector3 velocity, Vector3 spin)
    {
        var b = new Ball(t.Ball) { Position = new Vector3(0, t.Ball.Radius + 0.01f, 0), Velocity = velocity, Spin = spin };
        var r = b.Step(t.Dt, null, null);
        Assert.True(r.Bounced, "expected a bounce in the first tick");
        return b;
    }

    /// <summary>Speed of the contact point relative to the ground (zero when the ball is rolling).</summary>
    static float Slip(Ball b) => (new Vector3(b.Velocity.X, 0, b.Velocity.Z) + Vector3.Cross(b.Spin, new Vector3(0, -b.Radius, 0))).Length();

    [Fact]
    public void ShallowFastImpactSkidsAndSteepImpactGrips()
    {
        var t = Vacuum();
        var shallow = BounceOnce(t, new Vector3(20f, -3f, 0), Vector3.Zero);
        Assert.InRange(shallow.Velocity.X / 20f, 0.8f, 0.95f); // friction runs out before rolling: most speed kept
        Assert.True(Slip(shallow) > 1f, $"shallow slip {Slip(shallow)}");

        var steep = BounceOnce(t, new Vector3(3f, -10f, 0), Vector3.Zero);
        Assert.True(Slip(steep) < 1e-3f, $"steep slip {Slip(steep)}"); // leaves rolling
        Assert.True(steep.Spin.Z < 0f, "a ball rolling toward +X spins about -Z");
    }

    [Fact]
    public void FullGripLeavesAtSpeedOverOnePlusInertiaFactor()
    {
        var t = Vacuum();
        var b = BounceOnce(t, new Vector3(4f, -8f, 0), Vector3.Zero);
        float expected = 4f / (1f + t.Ball.InertiaFactor);
        Assert.Equal(expected, b.Velocity.X, 3);
        Assert.Equal(-expected / t.Ball.Radius, b.Spin.Z, 2);
    }

    [Fact]
    public void BackspinChecksAndTopspinKicksAtTheBounce()
    {
        var t = Vacuum();
        var v = new Vector3(6f, -8f, 0);
        float plain = BounceOnce(t, v, Vector3.Zero).Velocity.X;
        float back = BounceOnce(t, v, new Vector3(0, 0, 30f)).Velocity.X;   // +Z is backspin for a ball travelling +X
        float top = BounceOnce(t, v, new Vector3(0, 0, -30f)).Velocity.X;
        float heavyTop = BounceOnce(t, v, new Vector3(0, 0, -80f)).Velocity.X;
        Assert.True(back < plain - 0.5f, $"backspin {back} vs plain {plain}");
        Assert.True(top > plain + 0.5f, $"topspin {top} vs plain {plain}");
        Assert.True(heavyTop > 6f, $"heavy topspin {heavyTop} should kick forward past the incoming 6 m/s");
    }

    [Fact]
    public void RollingBallSpinMatchesItsVelocity()
    {
        var t = new Tuning();
        var b = new Ball(t.Ball) { Position = new Vector3(0, t.Ball.Radius, 0), Velocity = new Vector3(3f, 0, 2f) };
        b.Step(t.Dt, null, null);
        Assert.True(Slip(b) < 1e-4f, $"slip {Slip(b)}");
    }

    [Fact]
    public void SpinIsCapped()
    {
        var t = new Tuning();
        var b = Launch(t, 20f, 10f, new Vector3(0, 300f, 0));
        Assert.Equal(t.Ball.MaxSpin, b.Spin.Length(), 3);
        Assert.Equal(1f, b.Spin.Y / b.Spin.Length(), 5); // direction kept
        for (int i = 0; i < 30; i++) b.Step(t.Dt, null, null);
        Assert.True(b.Spin.Length() <= t.Ball.MaxSpin);
    }

    [Fact]
    public void SpinDecaysExponentiallyInTheAir()
    {
        var t = Vacuum();
        t.Ball.SpinDecayPerSecond = 0.5f;
        var b = Launch(t, 20f, 45f, new Vector3(0, 50f, 0));
        for (int i = 0; i < t.TickRate; i++) b.Step(t.Dt, null, null);
        Assert.Equal(50f * MathF.Exp(-0.5f), b.Spin.Length(), 2);
    }

    [Fact]
    public void DragCrisisLetsAFastBallKeepMoreOfItsSpeed()
    {
        // Without gravity the ball flies straight; with a constant Cd both would lose the same fraction over the same distance.
        var t = new Tuning();
        t.Ball.Gravity = 0f;
        float FractionLost(float v0)
        {
            var b = new Ball(t.Ball) { Position = new Vector3(0, 5f, 0), Velocity = new Vector3(v0, 0, 0) };
            while (b.Position.X < 20f) b.Step(t.Dt, null, null);
            return 1f - b.Velocity.X / v0;
        }
        float fast = FractionLost(30f), slow = FractionLost(10f);
        Assert.True(fast < slow - 0.05f, $"30 m/s lost {fast:P1}, 10 m/s lost {slow:P1}");
    }

    [Fact]
    public void CurveIsPiecewiseLinearAndClampedAtTheEnds()
    {
        var c = new Curve((0f, 0.45f), (9f, 0.42f), (14f, 0.22f), (40f, 0.24f));
        Assert.Equal(0.45f, c.Evaluate(-5f), 6);
        Assert.Equal(0.45f, c.Evaluate(0f), 6);
        Assert.Equal(0.42f, c.Evaluate(9f), 6);
        Assert.Equal(0.32f, c.Evaluate(11.5f), 5);
        Assert.Equal(0.23f, c.Evaluate(27f), 5);
        Assert.Equal(0.24f, c.Evaluate(40f), 6);
        Assert.Equal(0.24f, c.Evaluate(100f), 6);
        Assert.Equal(0.7f, Curve.Constant(0.7f).Evaluate(123f));
        Assert.Null(c.Validate());
    }

    [Theory]
    [InlineData("[]", "at least one")]
    [InlineData("[[0, 1], [0, 2]]", "strictly increasing")]
    [InlineData("[[5, 1], [2, 2]]", "strictly increasing")]
    [InlineData("[[0, \"NaN\"]]", "finite")]
    [InlineData("[[0, 1, 2]]", "drag_curve")]
    [InlineData("[[0]]", "drag_curve")]
    [InlineData("0.25", "drag_curve")]
    [InlineData("null", "drag_curve")]
    public void InvalidCurveIsRejectedByTheLoader(string curve, string expected)
    {
        var r = DataLoader.ParseTuning($$"""{ "ball": { "drag_curve": {{curve}} } }""", "t.json");
        Assert.False(r.Ok);
        Assert.NotNull(r.Error);
        Assert.Contains("t.json", r.Error);
        Assert.Contains(expected, r.Error);
        Assert.NotNull(r.Value); // the built-in defaults, so a caller without a last good version still has a tuning
    }

    [Fact]
    public void InvalidCurveFileIsNamedByTheLoader()
    {
        string path = Path.Combine(Path.GetTempPath(), "bad-curve-" + Guid.NewGuid() + ".json");
        File.WriteAllText(path, """{ "ball": { "restitution_curve": [[10, 0.6], [0, 0.7]] } }""");
        try
        {
            var r = DataLoader.LoadTuning(path);
            Assert.False(r.Ok);
            Assert.Contains(path, r.Error);
            Assert.Contains("ball.restitution_curve", r.Error);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CurvesRoundTripThroughJson()
    {
        var r = DataLoader.ParseTuning("""{ "ball": { "lift_curve": [[0, 0], [0.5, 0.3]] } }""", "t.json");
        Assert.True(r.Ok, r.Error);
        Assert.Equal(0.15f, r.Value!.Ball.LiftCurve.Evaluate(0.25f), 5);
        Assert.Equal(0.45f, r.Value.Ball.DragCurve.Evaluate(0f), 6); // omitted curves keep their defaults
        var again = DataLoader.ParseTuning(DataLoader.TuningToJson(r.Value), "t2.json");
        Assert.True(again.Ok, again.Error);
        Assert.Equal(DataLoader.TuningHash(r.Value), DataLoader.TuningHash(again.Value!));
    }

    [Fact]
    public void RollingBallStopsAfterTheExpectedDistance()
    {
        var t = new Tuning();
        var b = new Ball(t.Ball) { Position = new Vector3(0, t.Ball.Radius, 0), Velocity = new Vector3(5f, 0, 0) };
        int ticks = 0;
        while (b.Velocity.LengthSquared() > 0 && ticks++ < 5000) b.Step(t.Dt, null, null);
        Assert.Equal(Vector3.Zero, b.Velocity);
        float expected = 25f / (2 * t.Ball.RollDeceleration);
        Assert.InRange(b.Position.X / expected, 0.97f, 1.03f);
        Assert.Equal(t.Ball.Radius, b.Position.Y, 4);
    }

    [Fact]
    public void FastBallCannotSkipAPostInOneTick()
    {
        var t = new Tuning();
        var pitch = new Pitch(new PitchSpec(), t.Frame);
        // 36 m/s is 0.30 m per tick. Start just short of the post plane so one tick's endpoints straddle the post.
        var b = new Ball(t.Ball)
        {
            Position = new Vector3(pitch.GoalLineX - 0.2f, 1.0f, pitch.GoalHalfWidth),
            Velocity = new Vector3(36f, 0, 0),
        };
        var res = b.Step(t.Dt, pitch, null);
        Assert.True(res.FrameHit);
        Assert.True(b.Velocity.X < 0, "ball should rebound off the post");
        Assert.InRange(MathF.Abs(b.Velocity.X), 36f * 0.6f, 36f * 0.8f); // restitution 0.7 on a head-on hit
    }

    [Fact]
    public void SimulationIsDeterministicForTheBall()
    {
        var t = new Tuning();
        var a = Launch(t, 28f, 12f, new Vector3(1, 30, 2));
        var b = Launch(t, 28f, 12f, new Vector3(1, 30, 2));
        for (int i = 0; i < 300; i++) { a.Step(t.Dt, null, null); b.Step(t.Dt, null, null); }
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.Velocity, b.Velocity);
    }
}
