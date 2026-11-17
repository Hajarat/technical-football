using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

public class BallTests
{
    static Tuning Vacuum()
    {
        var t = new Tuning();
        t.Ball.DragCoefficient = 0f;
        t.Ball.MaxLiftCoefficient = 0f;
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
        t.Ball.BounceSidewaysKept = 1f;
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

    [Fact]
    public void BounceKeepsSidewaysSpeedFraction()
    {
        var t = Vacuum();
        var b = new Ball(t.Ball) { Position = new Vector3(0, 0.5f, 0), Velocity = new Vector3(10f, 0, 0) };
        for (int i = 0; i < 100; i++)
        {
            var r = b.Step(t.Dt, null, null);
            if (r.Bounced) { Assert.InRange(b.Velocity.X, 7.4f, 7.6f); return; }
        }
        Assert.Fail("no bounce");
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
