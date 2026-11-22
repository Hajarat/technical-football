using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>
/// The keeper's read-only save fields for views (<see cref="PlayerState.SaveTarget"/>, <see cref="PlayerState.SaveContactTime"/>):
/// set while a save, smother or slide is under way, NaN otherwise.
/// </summary>
public class SaveFieldsTests
{
    static readonly Tuning T = new();
    static readonly Pitch P = new(new PitchSpec(), T.Frame);
    static readonly float R = T.Ball.Radius;
    static float Dt => T.Dt;

    static (KeeperBrain Brain, PlayerState Keeper, Ball Ball) Rig(Vector3 feet, Vector3 ball)
    {
        var b = new Ball(T.Ball) { Position = ball };
        var k = new PlayerState(1, 0, PlayerRole.Keeper, TestData.Player("the_wall")) { Position = feet, Facing = MathF.PI };
        return (new KeeperBrain(k, P, T, holdLine: false), k, b);
    }

    static KeeperSituation Loose() => new() { AttackerNextTouch = 3f, AttackerTouchTick = 7, StrikeIn = float.PositiveInfinity };

    static bool InSave(KeeperAction a) => a is KeeperAction.Diving or KeeperAction.Smother or KeeperAction.Slide;

    [Fact]
    public void ADiveCarriesItsTargetAndContactTimeUntilItEnds()
    {
        var (brain, keeper, ball) = Rig(new Vector3(P.GoalLineX - 1f, 0f, 0f), new Vector3(P.GoalLineX - 11f, R, 0f));
        Assert.True(float.IsNaN(keeper.SaveContactTime));
        var target = keeper.Position + new Vector3(-0.2f, 1.0f, 1.6f);
        brain.BeginDiveForTest(target, 0.4f);
        Assert.Equal(KeeperAction.Diving, keeper.KeeperAction);
        Assert.Equal(target, keeper.SaveTarget);
        Assert.Equal(0.4f, keeper.SaveContactTime);
        ball.Position = new Vector3(P.GoalLineX - 11f, R, 8f);   // nowhere near him: the dive plays out and he lands
        var rng = new SimRng(1);
        bool left = false;
        for (int i = 0; i < 4 * T.TickRate && !left; i++)
        {
            brain.Update(Dt, ball, rng);
            if (keeper.KeeperAction == KeeperAction.Diving) Assert.Equal(0.4f, keeper.SaveContactTime);
            else left = true;
        }
        Assert.True(left);
        Assert.True(float.IsNaN(keeper.SaveContactTime), $"after the dive: {keeper.KeeperAction} {keeper.SaveContactTime}");
    }

    [Fact]
    public void ASmotherAimsAtItsBallAndMeetsItAtTheSmotherSpeed()
    {
        float line = P.PenaltyAreaLineX;
        var ballAt = new Vector3(line + 3f, R, 0f);
        var (brain, keeper, ball) = Rig(new Vector3(line + 4.2f, 0f, 0f), ballAt);
        brain.Update(Dt, ball, new SimRng(1), Loose());
        Assert.Equal(KeeperAction.Smother, keeper.KeeperAction);
        Assert.True(float.IsFinite(keeper.SaveContactTime));
        Assert.True(keeper.SaveContactTime > 0f && keeper.SaveContactTime < T.Keeper.SmotherMaxSeconds, $"{keeper.SaveContactTime}");
        Assert.True(Vector3.Distance(new Vector3(keeper.SaveTarget.X, 0, keeper.SaveTarget.Z), new Vector3(ballAt.X, 0, ballAt.Z)) < 1.5f,
            $"target {keeper.SaveTarget}");
        ball.Position = new Vector3(line + 9f, R, 6f);   // taken away: he lies, then gets up
        var seen = new List<KeeperAction>();
        for (int i = 0; i < 4 * T.TickRate; i++)
        {
            brain.Update(Dt, ball, new SimRng(1), Loose());
            if (seen.Count == 0 || seen[^1] != keeper.KeeperAction) seen.Add(keeper.KeeperAction);
            Assert.Equal(InSave(keeper.KeeperAction), float.IsFinite(keeper.SaveContactTime));
        }
        Assert.Contains(KeeperAction.Grounded, seen);
    }

    [Fact]
    public void ASlideAimsAtTheBallAndIsClearedOnTheGround()
    {
        float line = P.PenaltyAreaLineX;
        var ballAt = new Vector3(line - 1f, R, 0f);
        var (brain, keeper, ball) = Rig(new Vector3(line + 1f, 0f, 0f), ballAt);
        brain.Update(Dt, ball, new SimRng(1), Loose());
        Assert.Equal(KeeperAction.Slide, keeper.KeeperAction);
        Assert.Equal(ballAt.X, keeper.SaveTarget.X, 3);
        Assert.Equal(ballAt.Z, keeper.SaveTarget.Z, 3);
        // the foot starts slide_foot_start ahead and travels at the slide speed: 2 m away less the start
        float expect = (2f - T.Keeper.SlideFootStart) / T.Keeper.SlideSpeed;
        Assert.Equal(expect, keeper.SaveContactTime, 3);
        ball.Position = new Vector3(line - 10f, R, 5f);
        bool grounded = false;
        for (int i = 0; i < 2 * T.TickRate; i++)
        {
            brain.Update(Dt, ball, new SimRng(1), Loose());
            Assert.Equal(InSave(keeper.KeeperAction), float.IsFinite(keeper.SaveContactTime));
            grounded |= keeper.KeeperAction == KeeperAction.Grounded;
        }
        Assert.True(grounded);
    }

    [Fact]
    public void InFullRoundsTheFieldsAreSetExactlyWhileASaveIsUnderWay()
    {
        int dives = 0;
        for (ulong seed = 1; seed <= 12; seed++)
        {
            var w = TestData.Live("penalty", seed: seed);
            var a = new TickIntent[2];
            for (int i = 0; i < 18; i++) { a[0] = new TickIntent { ShootHeld = true }; w.Step(a); }
            a[0] = new TickIntent { MoveX = 1, MoveZ = 0 };
            w.Step(a);
            var prev = KeeperAction.Set;
            for (int i = 0; i < 400 && w.State != RoundState.Report; i++)
            {
                a[0] = default;
                w.Step(a);
                var k = w.Keeper;
                Assert.Equal(InSave(k.KeeperAction), float.IsFinite(k.SaveContactTime));
                if (k.KeeperAction == KeeperAction.Diving && prev != KeeperAction.Diving)
                {
                    dives++;
                    Assert.True(k.SaveContactTime >= 0f && k.SaveContactTime < 2f, $"seed {seed}: contact in {k.SaveContactTime}");
                    Assert.True(k.SaveTarget.Y > 0f && k.SaveTarget.Y < 3.5f, $"seed {seed}: target {k.SaveTarget}");
                }
                prev = k.KeeperAction;
            }
        }
        Assert.True(dives >= 6, $"dives {dives}");
    }
}
