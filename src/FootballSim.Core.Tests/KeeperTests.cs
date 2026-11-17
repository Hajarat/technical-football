using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

public class KeeperTests
{
    static readonly Tuning T = new();
    static readonly Pitch P = new(new PitchSpec(), T.Frame);

    static (KeeperBrain brain, PlayerState keeper, Ball ball) Rig(PlayerSheet sheet, bool onLine = true)
    {
        var ball = new Ball(T.Ball) { Position = new Vector3(P.GoalLineX - 11f, T.Ball.Radius, 0) };
        var keeper = new PlayerState(1, 0, PlayerRole.Keeper, sheet)
        {
            Position = Keeper.SetSpot(ball.Position, P, T, onLine),
            Facing = MathF.PI,
        };
        return (new KeeperBrain(keeper, P, T, onLine), keeper, ball);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(99)]
    public void ReactionTimeStaysInsideItsBounds(int reflexes)
    {
        var s = TestData.Sheet(50); s.Reflexes = reflexes;
        var (lo, hi) = Keeper.ReactionBounds(s, T);
        var rng = new SimRng(5);
        double sum = 0; int n = 2000;
        for (int i = 0; i < n; i++)
        {
            float r = Keeper.DrawReaction(s, T, rng);
            Assert.InRange(r, lo, hi);
            sum += r;
        }
        Assert.InRange(sum / n, Keeper.ReactionMean(s, T) - 0.003, Keeper.ReactionMean(s, T) + 0.003);
    }

    [Fact]
    public void ReactionMeanRunsFromPointThreeFourToPointOneSix()
    {
        var s = TestData.Sheet(50);
        s.Reflexes = 1; Assert.Equal(0.34f, Keeper.ReactionMean(s, T), 3);
        s.Reflexes = 99; Assert.Equal(0.16f, Keeper.ReactionMean(s, T), 3);
    }

    [Fact]
    public void ReachGrowsOverTimeAndIsCappedByStretch()
    {
        var s = TestData.Sheet(50, height: 1.90f); s.Diving = 50; s.Reach = 50;
        float v = Keeper.DiveSpeed(s, T), stretch = Keeper.Stretch(s, T);
        Assert.Equal(0f, Keeper.ReachAt(s, T, -0.1f));
        Assert.Equal(0f, Keeper.ReachAt(s, T, 0f));
        Assert.True(Keeper.ReachAt(s, T, 0.05f) < 0.9f);
        Assert.Equal(0.9f + v * 0.03f, Keeper.ReachAt(s, T, 0.15f), 3);
        Assert.Equal(MathF.Min(stretch, 0.9f + v * 0.28f), Keeper.ReachAt(s, T, 0.4f), 3);
        float prev = 0;
        for (float t = 0; t < 1.5f; t += 0.01f)
        {
            float r = Keeper.ReachAt(s, T, t);
            Assert.True(r >= prev - 1e-6f && r <= stretch + 1e-6f);
            prev = r;
        }
        Assert.Equal(stretch, Keeper.ReachAt(s, T, 5f), 4);
    }

    [Fact]
    public void StretchScalesWithRatingAndHeight()
    {
        var s = TestData.Sheet(50, height: 1.90f);
        s.Reach = 1; Assert.Equal(2.2f, Keeper.Stretch(s, T), 3);
        s.Reach = 99; Assert.Equal(2.9f, Keeper.Stretch(s, T), 3);
        s.HeightM = 2.0f; Assert.Equal(2.9f * 2.0f / 1.9f, Keeper.Stretch(s, T), 3);
    }

    [Fact]
    public void DiveSpeedAndCatchSpeedRunWithRatings()
    {
        var s = TestData.Sheet(50);
        s.Diving = 1; Assert.Equal(3f, Keeper.DiveSpeed(s, T), 3);
        s.Diving = 99; Assert.Equal(6f, Keeper.DiveSpeed(s, T), 3);
        s.Handling = 1; Assert.Equal(14f, Keeper.CatchSpeed(s, T), 3);
        s.Handling = 99; Assert.Equal(26f, Keeper.CatchSpeed(s, T), 3);
    }

    [Fact]
    public void SetSpotIsOnTheLineFromBallToGoalCentre()
    {
        var ball = new Vector3(P.GoalLineX - 12f, 0.11f, 14f);
        var spot = Keeper.SetSpot(ball, P, T, onLine: false);
        Assert.Equal(P.GoalLineX - 1.5f, spot.X, 3);
        Assert.Equal(14f * 1.5f / 12f, spot.Z, 3);
        var pen = Keeper.SetSpot(new Vector3(P.GoalLineX - 11f, 0.11f, 0f), P, T, onLine: true);
        Assert.Equal(P.GoalLineX, pen.X, 3);
        Assert.Equal(0f, pen.Z, 3);
    }

    [Fact]
    public void SlowBallInsideTheCatchZoneIsHeld()
    {
        var s = TestData.Sheet(50); s.Handling = 99;
        var (brain, keeper, ball) = Rig(s);
        brain.OnStrike(new SimRng(1));
        var home = new Vector3(keeper.Position.X, s.HeightM * 0.55f, keeper.Position.Z);
        ball.Position = home - new Vector3(0.3f, 0, 0);
        ball.Velocity = new Vector3(12f, 0, 0);
        bool held = brain.Contact(ball, ball.Position, new SimRng(2), out float v);
        Assert.True(held);
        Assert.Equal(12f, v, 3);
        Assert.Equal(Vector3.Zero, ball.Velocity);
        Assert.Equal(KeeperResult.Held, brain.Report.Action);
    }

    [Fact]
    public void FastBallIsParriedAtFortyToSixtyPercentOfItsSpeed()
    {
        var s = TestData.Sheet(50); s.Handling = 1;
        var (brain, keeper, ball) = Rig(s);
        for (ulong i = 0; i < 40; i++)
        {
            brain.OnStrike(new SimRng(i));
            ball.Position = new Vector3(keeper.Position.X - 0.3f, 1.0f, 0.3f);
            ball.Velocity = new Vector3(30f, 0, 0);
            bool held = brain.Contact(ball, ball.Position, new SimRng(i), out _);
            Assert.False(held);
            Assert.InRange(ball.Velocity.Length(), 30f * 0.4f - 1e-3f, 30f * 0.6f + 1e-3f);
            Assert.True(ball.Velocity.X < 0, "parried ball comes back out");
            Assert.Equal(KeeperResult.Parried, brain.Report.Action);
        }
    }

    [Fact]
    public void SlowBallBeyondTheCatchZoneIsStillParried()
    {
        var s = TestData.Sheet(50); s.Handling = 99; s.Reach = 99;
        var (brain, keeper, ball) = Rig(s);
        brain.OnStrike(new SimRng(1));
        float far = 0.8f * Keeper.Stretch(s, T);
        ball.Position = new Vector3(keeper.Position.X - 0.3f, s.HeightM * 0.55f, far);
        ball.Velocity = new Vector3(8f, 0, 0);
        Assert.False(brain.Contact(ball, ball.Position, new SimRng(1), out _));
    }

    [Fact]
    public void HighHandlingSteersAParryWideLowHandlingLeavesItCentral()
    {
        double Sideways(int handling)
        {
            var s = TestData.Sheet(50); s.Handling = handling;
            var (brain, keeper, ball) = Rig(s);
            double sum = 0;
            for (ulong i = 0; i < 100; i++)
            {
                brain.OnStrike(new SimRng(i));
                ball.Position = new Vector3(keeper.Position.X - 0.3f, 1.0f, 0.5f);
                ball.Velocity = new Vector3(33f, 0, 0);
                brain.Contact(ball, ball.Position, new SimRng(i + 1000), out _);
                sum += MathF.Abs(ball.Velocity.Z) / MathF.Abs(ball.Velocity.X);
            }
            return sum / 100;
        }
        Assert.True(Sideways(99) > 3 * Sideways(1) + 0.5, "wide for high handling");
        Assert.True(Sideways(1) < 0.4);
    }

    [Fact]
    public void KeeperReactsOnlyAfterTheReactionTimeAndDivesTowardTheBall()
    {
        var s = TestData.Sheet(50); s.Reflexes = 50; s.Diving = 80;
        var (brain, keeper, ball) = Rig(s);
        ball.Position = new Vector3(P.GoalLineX - 11f, 0.11f, 0f);
        ball.Velocity = new Vector3(25f, 4f, 2.5f);   // heading to about z = 1.1 at the line
        brain.OnStrike(new SimRng(3));
        float reaction = brain.Report.ReactionS;
        float t = 0;
        while (t < reaction - 0.02f) { brain.Update(T.Dt, ball, new SimRng(4)); t += T.Dt; }
        Assert.Equal(KeeperAction.Reacting, keeper.KeeperAction);
        Assert.Equal(keeper.Position.Z, keeper.HandPosition.Z, 3);
        for (int i = 0; i < 30; i++) brain.Update(T.Dt, ball, new SimRng(4));
        Assert.Equal(KeeperAction.Diving, keeper.KeeperAction);
        Assert.True(keeper.HandPosition.Z > 0.2f, $"hands z {keeper.HandPosition.Z}");
    }

    [Fact]
    public void SweptTestCatchesAGrazingHandContactTheBallWouldSkip()
    {
        // 36 m/s moves 0.3 m per tick. A path 0.28 m off the hand centre cuts a chord of only 0.15 m through the hand sphere,
        // so no sampled position is inside it, but the swept test still reports the contact.
        var s = TestData.Sheet(50);
        var (brain, keeper, ball) = Rig(s);
        brain.OnStrike(new SimRng(1));
        var hands = new Vector3(keeper.Position.X, s.HeightM * 0.55f, 0f);
        brain.Update(T.Dt, ball, new SimRng(1));
        var sphere = brain.HandsSphere;
        Assert.NotNull(sphere);
        float radius = sphere!.Value.Radius;
        float offset = radius - 0.03f;
        ball.Position = new Vector3(hands.X - 0.45f, hands.Y + offset, 0f);
        ball.Velocity = new Vector3(36f, 0, 0);
        bool sampledInside = false;
        var probe = ball.Position;
        for (int i = 0; i < 6; i++) { probe += new Vector3(0.3f, 0, 0); if ((probe - hands).Length() < radius) sampledInside = true; }
        Assert.False(sampledInside, "test setup: sampled positions must all miss");
        bool hit = false;
        for (int i = 0; i < 6 && !hit; i++) hit = ball.Step(T.Dt, null, sphere).SphereHit;
        Assert.True(hit);
    }

    [Fact]
    public void ShotStraightAtTheKeeperIsSavedInTheFullWorld()
    {
        int saves = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var w = TestData.Live("penalty", seed: seed);
            var a = new TickIntent[2];
            for (int i = 0; i < 54; i++) { a[0] = new TickIntent { ShootHeld = true }; w.Step(a); }
            a[0] = new TickIntent { MoveX = 1, MoveZ = 0 };
            w.Step(a);
            bool saved = false;
            for (int i = 0; i < 200 && !saved && w.State != RoundState.Report; i++)
            {
                a[0] = default;
                w.Step(a);
                saved |= w.Events.Any(e => e.Kind == EventKind.Save);
            }
            if (saved) saves++;
        }
        Assert.True(saves >= 38, $"saves {saves}/40");
    }

    [Fact]
    public void KeeperReportRecordsReachNeededAndMargin()
    {
        int found = 0;
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var w = TestData.Live("penalty", keeper: "stand_in", seed: seed);
            TestData.Play(w, new ScriptedAttacker(), stopWhen: x => x.State == RoundState.Outcome);
            var k = w.KeeperReport;
            if (k.Action == KeeperResult.None) continue; // the shot hit the frame and never reached the keeper's plane
            found++;
            Assert.True(k.ReactionS >= 0.15f);
            Assert.True(k.NeededM > 0f && k.ReachM >= 0f);
            Assert.Equal(k.ReachM - k.NeededM, k.MarginM, 4);
        }
        Assert.True(found >= 20);
    }
}
