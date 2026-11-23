using System.Numerics;
using FootballSim.Core;
using Xunit;
using static FootballSim.Core.Tests.TestData;

namespace FootballSim.Core.Tests;

/// <summary>The pass's arithmetic on a synthetic three-man team: who it is for, where it goes and how hard (see PassModel).</summary>
public class PassModelTests
{
    static readonly Tuning T = new();
    static readonly Vector3 Ball = new(10f, 0.11f, 0f);

    static PlayerState Man(int index, float x, float z, float vx = 0f, float vz = 0f, int team = 0, PlayerRole role = PlayerRole.Outfield, int rating = 70)
        => new(team, index, role, Sheet(rating)) { Position = new Vector3(x, 0f, z), Velocity = new Vector3(vx, 0f, vz) };

    /// <summary>The passer by the ball facing +X, a near team-mate 6 m up the pitch and a far one 16 m up it, a little to the side.</summary>
    static (PlayerState Passer, PlayerState Near, PlayerState Far, List<PlayerState> Team) Three()
    {
        var a = Man(0, 9.6f, 0f); var near = Man(1, 16f, 1f); var far = Man(2, 26f, -1f);
        return (a, near, far, new List<PlayerState> { a, near, far });
    }

    [Fact]
    public void TheHoldPointsThePassFurtherAlongTheStick()
    {
        var aim = new Vector2(1f, 0f);
        var s0 = PassModel.SelectionPoint(Ball, aim, 0f, PassKind.Ground, T);
        var s1 = PassModel.SelectionPoint(Ball, aim, 1f, PassKind.Ground, T);
        var t1 = PassModel.SelectionPoint(Ball, aim, 1f, PassKind.Through, T);
        Assert.Equal(Ball.X + T.Pass.MinDistance, s0.X, 3);
        Assert.Equal(Ball.X + T.Pass.MaxDistance.Ground, s1.X, 3);
        Assert.Equal(Ball.X + T.Pass.MaxDistance.Through, t1.X, 3);
        Assert.Equal(0.5f, PassModel.ChargeAfter(T.Pass.ChargeSeconds / 2f, T), 3);
        Assert.Equal(1f, PassModel.ChargeAfter(10f, T), 3);
        // The stick at rest: the pass points where he faces.
        var p = Outfield(facing: MathF.PI / 2f);
        var rest = PassModel.Aim(p, Vector2.Zero, T);
        Assert.Equal(0f, rest.X, 3);
        Assert.Equal(1f, rest.Y, 3);
    }

    [Fact]
    public void ATapPicksTheNearManAndAFullHoldTheFarOne()
    {
        var (a, near, far, team) = Three();
        var aim = new Vector2(1f, 0f);
        Assert.Same(near, PassModel.ChooseReceiver(a, team, Ball, aim, 0.2f, PassKind.Ground, null, T));
        Assert.Same(far, PassModel.ChooseReceiver(a, team, Ball, aim, 0.75f, PassKind.Ground, null, T));
    }

    [Fact]
    public void TheStickPicksBetweenTwoMenAtTheSameDistance()
    {
        var a = Man(0, 9.6f, 0f); var left = Man(1, 15f, -6f); var right = Man(2, 15f, 6f);
        var team = new List<PlayerState> { a, left, right };
        float charge = (8f - T.Pass.MinDistance) / (T.Pass.MaxDistance.Ground - T.Pass.MinDistance);
        Assert.Same(left, PassModel.ChooseReceiver(a, team, Ball, Vector2.Normalize(new Vector2(5f, -6f)), charge, PassKind.Ground, null, T));
        Assert.Same(right, PassModel.ChooseReceiver(a, team, Ball, Vector2.Normalize(new Vector2(5f, 6f)), charge, PassKind.Ground, null, T));
    }

    [Fact]
    public void GroundScoreFollowsItsFormula()
    {
        var m = Man(1, 16f, 0f);
        var aim = new Vector2(1f, 0f);
        // The point 2 m beyond him, dead ahead: d = 2, the angle at its floor of 5 degrees, reach 2 (he stands).
        var s = new Vector2(18f, 0f);
        float q = 5f / 45f;
        Assert.Equal(1f / (2f * q * q * 2f), PassModel.GroundScore(m, Ball, aim, s, false), 2);
        // Offside he scores a fifth.
        Assert.Equal(PassModel.GroundScore(m, Ball, aim, s, false) / 5f, PassModel.GroundScore(m, Ball, aim, s, true), 2);
        // The point 2 m short of him counts six times as far.
        var shortOf = new Vector2(14f, 0f);
        Assert.Equal(1f / (12f * q * q * 2f), PassModel.GroundScore(m, Ball, aim, shortOf, false), 2);
        // Within half a metre of the point his reach is 0.05.
        Assert.Equal(1f / (0.3f * q * q * 0.05f), PassModel.GroundScore(m, Ball, aim, new Vector2(16.3f, 0f), false), 0);
        // Off the stick's line he scores less: 45 degrees is the unit angle.
        var wide = Man(2, 10f + 6f * MathF.Cos(MathF.PI / 4f), 6f * MathF.Sin(MathF.PI / 4f));
        var ws = new Vector2(wide.Position.X + 2f, wide.Position.Z);
        Assert.Equal(1f / (2f * 1f * 2f), PassModel.GroundScore(wide, Ball, aim, ws, false), 2);
        // Running at the point he reaches more of it in the half second.
        var runner = Man(3, 16f, 0f, vx: 2f);
        Assert.Equal(1f / (2f * q * q * 1f), PassModel.GroundScore(runner, Ball, aim, s, false), 2);
    }

    [Fact]
    public void ThroughScorePrefersTheManAlreadyRunningTheWayTheStickPoints()
    {
        var aim = new Vector2(1f, 0f);
        var s = new Vector2(24f, 0f);
        var stands = Man(1, 16f, 2f);
        var runs = Man(2, 16f, -2f, vx: 6f);
        var runsAway = Man(3, 16f, -2f, vx: -6f);
        float a = PassModel.ThroughScore(stands, aim, s, false), b = PassModel.ThroughScore(runs, aim, s, false), c = PassModel.ThroughScore(runsAway, aim, s, false);
        Assert.True(b > a && a > c, $"{b} > {a} > {c}");
        // [2 + cos + closing / 8] / reach for the standing man: cos and closing are 0, reach his distance to the point.
        Assert.Equal(2f / Vector2.Distance(new Vector2(16f, 2f), s), a, 3);
        Assert.Equal(b / 5f, PassModel.ThroughScore(runs, aim, s, true), 3);
        var team = new List<PlayerState> { Man(0, 9.6f, 0f), stands, runs };
        Assert.Same(runs, PassModel.ChooseReceiver(team[0], team, Ball, aim, 0.5f, PassKind.Through, null, T));
        // Offside, the runner loses to the man standing onside.
        Assert.Same(stands, PassModel.ChooseReceiver(team[0], team, Ball, aim, 0.5f, PassKind.Through, m => m == runs, T));
    }

    [Fact]
    public void NobodyBeyondTheMinimumDistanceIsAPassIntoSpace()
    {
        var a = Man(0, 9.6f, 0f); var close = Man(1, 10.5f, 0.3f);
        var team = new List<PlayerState> { a, close };
        Assert.Null(PassModel.ChooseReceiver(a, team, Ball, new Vector2(1f, 0f), 0.5f, PassKind.Ground, null, T));
        var pitch = new Pitch(Scenario("two_v_one").Pitch, T.Frame, T.Arena);
        var plan = PassModel.Plan(a, team, new List<PlayerState>(), Ball, new Vector2(0f, 1f), 1f, PassKind.Ground, Foot.Right, null, 0f, pitch, T);
        Assert.Null(plan.Receiver);
        // Along the stick, and kept 2 m inside the boards.
        Assert.Equal(Ball.X, plan.Target.X, 2);
        Assert.Equal(pitch.HalfWidth - 2f, plan.Target.Z, 2);
    }

    [Fact]
    public void TheLaunchSpeedBringsARollingBallToTheTargetAtTheArrivalSpeed()
    {
        float launch = PassModel.LaunchSpeed(8f, 5f, T);
        float seconds = PassModel.TravelSeconds(8f, launch, T);
        // Roll a real ball the same distance.
        var b = new Ball(T.Ball) { Position = new Vector3(0f, T.Ball.Radius, 0f) };
        b.Velocity = new Vector3(launch, 0f, 0f);
        b.Spin = b.RollingSpin(b.Velocity);
        int ticks = 0;
        float dt = 1f / T.TickRate;
        while (b.Position.X < 8f && ticks < 2000) { b.Step(dt, null, null); ticks++; }
        Assert.InRange(ticks / (float)T.TickRate, seconds - 0.05f, seconds + 0.05f);
        Assert.InRange(b.Velocity.Length(), 4.6f, 5.4f);
        // A ball rolled off too gently never arrives.
        Assert.True(float.IsPositiveInfinity(PassModel.TravelSeconds(40f, 2f, T)));
    }

    [Fact]
    public void ThePassIsTheLeastSpeedThatArrivesAndMoreOnlyToBeatADefender()
    {
        var (a, near, _, _) = Three();
        var team = new List<PlayerState> { a, near };
        var stick = Vector2.Normalize(new Vector2(6f, 1f));
        var none = new List<PlayerState>();
        var free = PassModel.Plan(a, team, none, Ball, stick, 0.2f, PassKind.Ground, Foot.Right, null, 0f, null, T);
        Assert.Same(near, free.Receiver);
        float len = Vector2.Distance(new Vector2(Ball.X, Ball.Z), new Vector2(free.Target.X, free.Target.Z));
        Assert.Equal(PassModel.LaunchSpeed(len, T.Pass.ArrivalSpeed.Ground, T), free.Speed, 2);
        Assert.True(float.IsPositiveInfinity(free.Margin));
        // To his feet: the target is within a metre of where he stands.
        Assert.InRange(Vector2.Distance(new Vector2(free.Target.X, free.Target.Z), new Vector2(16f, 1f)), 0f, 1.5f);
        // A defender 2.5 m off the lane, halfway: the pass is struck harder, or moved off him, and he is late on it.
        var def = Man(1, 13f, 3.2f, team: 1);
        var pressed = PassModel.Plan(a, team, new List<PlayerState> { def }, Ball, stick, 0.2f, PassKind.Ground, Foot.Right, null, 0f, null, T);
        Assert.True(pressed.Margin >= 0f);
        Assert.True(pressed.Speed >= free.Speed - 1e-3f);
        // Standing on the lane he cannot be beaten: the pass is played anyway, at no more than the cap.
        var on = Man(1, 13f, 0.5f, team: 1);
        var blocked = PassModel.Plan(a, team, new List<PlayerState> { on }, Ball, stick, 0.2f, PassKind.Ground, Foot.Right, null, 0f, null, T);
        Assert.Same(near, blocked.Receiver);
        float yaw = MathF.Atan2(blocked.Direction.Y, blocked.Direction.X);
        Assert.True(blocked.Speed <= PassModel.SpeedCap(a, PassKind.Ground, yaw, Foot.Right, T) + 1e-3f);
    }

    [Fact]
    public void TheSpeedCapRisesWithTheRatingAndFallsWithTheBodyAngleAndTheWeakFoot()
    {
        var ps = Sheet(1); ps.GroundPass = 1; var gs = Sheet(99); gs.GroundPass = 99;
        var poor = Outfield(ps); var good = Outfield(gs);
        Assert.Equal(T.Pass.SpeedCap.Min, PassModel.SpeedCap(poor, PassKind.Ground, 0f, poor.Sheet.PreferredFoot, T) / MathF.Max(1e-3f, T.Shot.AnglePower.Evaluate(0f)), 0);
        Assert.True(PassModel.SpeedCap(good, PassKind.Ground, 0f, good.Sheet.PreferredFoot, T) > PassModel.SpeedCap(poor, PassKind.Ground, 0f, poor.Sheet.PreferredFoot, T) + 5f);
        Assert.True(PassModel.SpeedCap(good, PassKind.Ground, MathF.PI, good.Sheet.PreferredFoot, T) < PassModel.SpeedCap(good, PassKind.Ground, 0f, good.Sheet.PreferredFoot, T));
        var other = good.Sheet.PreferredFoot == Foot.Right ? Foot.Left : Foot.Right;
        Assert.True(PassModel.SpeedCap(good, PassKind.Ground, 0f, other, T) <= PassModel.SpeedCap(good, PassKind.Ground, 0f, good.Sheet.PreferredFoot, T));
        // The wind-up: longer for a full hold and for a pass behind him.
        Assert.Equal(0.3f, PassModel.ExecutionSeconds(good, 0f, 0f, T), 3);
        Assert.Equal(0.4f, PassModel.ExecutionSeconds(good, 1f, 0f, T), 3);
        Assert.True(PassModel.ExecutionSeconds(good, 0f, MathF.PI, T) > 0.5f);
    }

    [Fact]
    public void AThroughBallGoesIntoTheSpaceAheadOfTheRunner()
    {
        var a = Man(0, 9.6f, 0f);
        var runner = Man(1, 14f, 5f, vx: 7f);
        var team = new List<PlayerState> { a, runner };
        var stick = new Vector2(1f, 0f);
        var through = PassModel.Plan(a, team, new List<PlayerState>(), Ball, stick, 0.6f, PassKind.Through, Foot.Right, null, 0f, null, T);
        var ground = PassModel.Plan(a, team, new List<PlayerState>(), Ball, stick, 0.6f, PassKind.Ground, Foot.Right, null, 0f, null, T);
        Assert.Same(runner, through.Receiver);
        float top = Movement.TopSpeed(runner.Sheet, T);
        // Between the nearest and the furthest lead of his run, ahead of him.
        Assert.InRange(through.Target.X - 14f, top * T.Pass.ThroughAheadSeconds[0] - 0.5f, top * T.Pass.ThroughAheadSeconds[1] + 0.5f);
        // A ground pass also leads him (in his stride), but arrives slower.
        Assert.True(ground.Target.X > 14.5f);
        // It is struck hard enough to get there when he does (he is running away from the passer), a little before him.
        float need = MathF.Max(0f, through.Target.X - 14f - 0.7f) / top;
        Assert.InRange(need - through.ArrivalSeconds, 0f, 0.6f);
        float len = Vector2.Distance(new Vector2(Ball.X, Ball.Z), new Vector2(through.Target.X, through.Target.Z));
        Assert.True(through.Speed >= PassModel.LaunchSpeed(len, T.Pass.ArrivalSpeed.Through, T) - 1e-3f);
        // To a man standing still it arrives at the through ball's arrival speed, ahead of him toward the goal.
        var stands = Man(1, 14f, 5f);
        var still = PassModel.Plan(a, new List<PlayerState> { a, stands }, new List<PlayerState>(), Ball, stick, 0.6f, PassKind.Through, Foot.Right, null, 0f, null, T);
        Assert.True(still.Target.X > 15f);
    }

    [Fact]
    public void TheLaneMarginIsTheDefendersLatenessOnTheLane()
    {
        var from = new Vector2(10f, 0f); var to = new Vector2(18f, 0f);
        Assert.True(float.IsPositiveInfinity(PassModel.LaneMargin(from, to, 10f, new List<PlayerState>(), 0f, null, T)));
        var on = Man(1, 14f, 0.3f, team: 1);
        Assert.True(PassModel.LaneMargin(from, to, 10f, new List<PlayerState> { on }, 0f, null, T) < 0f);
        var off = Man(1, 10f, 7f, team: 1);
        float m = PassModel.LaneMargin(from, to, 10f, new List<PlayerState> { off }, 0f, null, T);
        Assert.True(m > 0f);
        // A reaction delay is added to his arrival; a faster ball leaves him later still.
        Assert.Equal(m + 0.3f, PassModel.LaneMargin(from, to, 10f, new List<PlayerState> { off }, 0.3f, null, T), 3);
        Assert.True(PassModel.LaneMargin(from, to, 16f, new List<PlayerState> { off }, 0f, null, T) >= m);
    }
}
