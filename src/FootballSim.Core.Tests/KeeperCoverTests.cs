using System.Numerics;
using FootballSim.Core;
using Xunit.Abstractions;

namespace FootballSim.Core.Tests;

/// <summary>
/// The angle a set, planted keeper covers against a straight shot, by the shooter's distance: the widest sideways offset (at
/// his position, across the shot) that he still gets a hand, foot or body to, divided by the distance. Measured through the
/// brain and the ball's swept contact tests, the median over seeds (each seed draws his reaction and his read of the shot).
/// </summary>
public class KeeperCoverTests
{
    static readonly Pitch P = new(new PitchSpec(), new Tuning().Frame);
    readonly ITestOutputHelper _out;

    public KeeperCoverTests(ITestOutputHelper output) { _out = output; }

    internal static readonly float[] Distances = { 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f, 11f, 12f, 14f, 16f, 20f };
    internal static readonly float[] Speeds = { 20f, 25f, 30f };

    /// <summary>
    /// Whether the keeper, planted for the shot <paramref name="distance"/> metres in front of him, touches a ball struck at
    /// <paramref name="speed"/> that passes <paramref name="offset"/> metres to his side at <paramref name="height"/>.
    /// </summary>
    internal static bool Touches(Tuning t, PlayerSheet sheet, float distance, float speed, float height, float offset, ulong seed)
    {
        var feet = new Vector3(P.GoalLineX - 2f, 0f, 0f);
        var ball = new Ball(t.Ball) { Position = new Vector3(feet.X - distance, height, 0f) };
        var keeper = new PlayerState(1, 0, PlayerRole.Keeper, sheet) { Position = feet, Facing = MathF.PI };
        var brain = new KeeperBrain(keeper, P, t, holdLine: false);
        // The shot winds up: he plants (and, this close, spreads) for it.
        var windup = new KeeperSituation
        {
            AttackerNextTouch = 0.1f, AttackerTouchTick = 1, Windup = true, StrikeIn = 0.1f, AttackerHasBall = true,
            AttackerPosition = ball.Position - new Vector3(0.5f, 0f, 0f),
        };
        var rng = new SimRng(seed);
        for (int i = 0; i < 6; i++) brain.Update(t.Dt, ball, rng, windup);
        keeper.Position = feet;
        keeper.Velocity = Vector3.Zero;
        // Aimed to pass the keeper's position offset metres to his side at the given height (flat apart from gravity).
        float flight = distance / speed;
        var h = Vector2.Normalize(new Vector2(distance, offset)) * speed;
        ball.Velocity = new Vector3(h.X, 0.5f * 9.81f * flight, h.Y);
        ball.Spin = Vector3.Zero;
        brain.OnStrike(rng, ball);
        var flying = new KeeperSituation { AttackerNextTouch = float.PositiveInfinity, AttackerTouchTick = -1, StrikeIn = float.PositiveInfinity, ShotInFlight = true };
        for (int i = 0; i < 240; i++)
        {
            brain.Update(t.Dt, ball, rng, flying);
            var res = ball.Step(t.Dt, null, brain.HandsSphere, brain.BodyCapsule, brain.HandsCapsule);
            if (res.SphereHit) return true;
            if (ball.Position.X > feet.X + 1.5f) return false;
        }
        return false;
    }

    /// <summary>The widest offset he touches (bisected, assuming a nearer ball is touched too), metres.</summary>
    internal static float CoveredOffset(Tuning t, PlayerSheet sheet, float distance, float speed, float height, ulong seed)
    {
        float lo = 0f, hi = 4f;
        if (!Touches(t, sheet, distance, speed, height, lo, seed)) return 0f;
        for (int i = 0; i < 12; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (Touches(t, sheet, distance, speed, height, mid, seed)) lo = mid; else hi = mid;
        }
        return lo;
    }

    /// <summary>The median covered angle (covered offset / distance) over <paramref name="seeds"/> seeds.</summary>
    internal static float CoveredAngle(Tuning t, PlayerSheet sheet, float distance, float speed, float height, int seeds = 24)
    {
        var v = new float[seeds];
        Parallel.For(0, seeds, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => v[i] = CoveredOffset(t, sheet, distance, speed, height, 100 + (ulong)i));
        Array.Sort(v);
        return v[seeds / 2] / distance;
    }

    [Fact]
    public void ReportCoveredAngleByDistance()
    {
        var t = new Tuning();
        var wall = TestData.Player("the_wall");
        foreach (float height in new[] { 0.3f, 1.0f })
        {
            _out.WriteLine($"covered angle (offset / distance), the_wall set and planted, ball at {height:F1} m");
            _out.WriteLine("speed  " + string.Join("  ", Distances.Select(d => $"{d,5:F0}m")));
            foreach (float v in Speeds)
                _out.WriteLine($"{v,3:F0}m/s " + string.Join("  ", Distances.Select(d => $"{CoveredAngle(t, wall, d, v, height),6:F3}")));
        }
    }
}
