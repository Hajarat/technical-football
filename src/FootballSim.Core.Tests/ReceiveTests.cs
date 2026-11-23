using System.Numerics;
using FootballSim.Core;
using Xunit;
using static FootballSim.Core.Tests.TestData;
using static FootballSim.Core.Tests.ControlTests;

namespace FootballSim.Core.Tests;

/// <summary>Receiving a pass: the record on the ball, the receiver's exclusive claim and its late rule, and the first touch.</summary>
public class ReceiveTests
{
    static bool Has(World w, EventKind kind, int team, int index = -1) => w.Events.Any(e => e.Kind == kind && e.Team == team && (index < 0 || e.PlayerIndex == index));

    [Fact]
    public void ThePassRecordNamesTheReceiverAndOnlyHeGoesForTheBall()
    {
        var w = WithBall(10f, 0f, 10f, 8f);
        Assert.Null(w.Pass);
        Assert.True(StrikePass(w));
        var rec = w.Pass;
        Assert.NotNull(rec);
        Assert.Same(w.Teams[0][0], rec!.Passer);
        Assert.Same(w.Teams[0][1], rec.Receiver);
        Assert.Equal(PassKind.Ground, rec.Kind);
        Assert.Equal(w.Tick, rec.StrikeTick);
        Assert.True(rec.ExpectedArrivalTick > w.Tick + 30);
        Assert.InRange(Vector2.Distance(new Vector2(rec.Target.X, rec.Target.Z), new Vector2(10f, 8f)), 0f, 1.5f);
        var buf = new TickIntent[w.PlayerCount];
        int touch = -1;
        for (int i = 0; i < 480 && touch < 0; i++)
        {
            w.Step(buf);
            if (Has(w, EventKind.Touch, 0, 1)) { touch = w.Tick; break; }
            Assert.False(Has(w, EventKind.Touch, 0, 0));
            // While the record is live the team's claim is his or nobody's: the passer, right by the ball at first, never takes it.
            if (w.Pass != null && w.Claims[0].Player != null) Assert.Same(w.Teams[0][1], w.Claims[0].Player);
        }
        Assert.True(touch > 0, "the pass was not received");
        // He meets it when the record said he would, the record ends at his touch, and the ball is his.
        Assert.InRange(touch - rec.ExpectedArrivalTick, -12, 12);
        w.Step(buf);
        Assert.Null(w.Pass);
        Assert.Equal(1, w.PassesReceived);
        for (int i = 0; i < 120 && w.BallController == null; i++) w.Step(buf);
        Assert.Same(w.Teams[0][1], w.BallController);
        Assert.Same(w.Attacker, w.BallController);
    }

    [Fact]
    public void TheStickChoosesTheFirstTouchAndAtRestItIsATrap()
    {
        float SpeedAfter(TickIntent stick)
        {
            var w = WithBall(10f, 0f, 10f, 8f);
            Assert.True(StrikePass(w));
            var buf = new TickIntent[w.PlayerCount];
            buf[0] = stick;
            for (int i = 0; i < 480; i++)
            {
                w.Step(buf);
                if (Has(w, EventKind.Touch, 0, 1)) return new Vector2(w.Ball.Velocity.X, w.Ball.Velocity.Z).Length();
            }
            Assert.Fail("not received");
            return 0f;
        }
        float trap = SpeedAfter(default);
        float push = SpeedAfter(new TickIntent { MoveX = 1f, Sprint = 1f });
        Assert.True(trap < 2.5f, $"trap leaves the ball at {trap} m/s");
        Assert.True(push > trap + 1f, $"push {push} against trap {trap}");
    }

    [Fact]
    public void ReceiverDown()
    {
        // The receiver is stumbling when the pass is struck: the record still names him, and when he is late the claim opens
        // to his whole team, so the passer can go for his own pass and control follows him.
        var w = WithBall(10f, 0f, 10f, 8f);
        var mate = w.Teams[0][1];
        mate.StumbleUntil = 100f;
        Assert.True(StrikePass(w));
        Assert.Same(mate, w.Pass!.Receiver);
        Assert.Equal(1, w.ControlledIndex);
        var buf = new TickIntent[w.PlayerCount];
        bool reopened = false;
        int back = -1;
        for (int i = 0; i < 1200 && back < 0; i++)
        {
            w.Step(buf);
            if (w.Pass is { Reopened: true } r) { reopened = true; Assert.Same(mate, r.Receiver); }
            if (!reopened && w.Pass != null) Assert.NotSame(w.Teams[0][0], w.Claims[0].Player);
            if (w.Events.Any(e => e.Kind == EventKind.ControlChanged && e.Reason == ControlReason.Claim && e.PlayerIndex == 0)) back = w.Tick;
        }
        Assert.True(reopened, "the claim never reopened");
        Assert.True(back > 0, "control never came back to the passer");
        Assert.Equal(0, w.ControlledIndex);
        for (int i = 0; i < 1200 && w.BallController == null && w.State == RoundState.Live; i++) w.Step(buf);
        Assert.Same(w.Teams[0][0], w.BallController);
    }

    [Fact]
    public void Deflected()
    {
        // The defender stands on the lane: the pass hits him, the ball is on a new path, the record ends and the receiver is no
        // longer the only one who may go for it.
        var w = WithBall(8f, -4f, 8f, 4f, defender: (8f, 0f));
        w.DefenderBotEnabled = false;
        Assert.True(StrikePass(w, holdTicks: 3));
        Assert.NotNull(w.Pass);
        var buf = new TickIntent[w.PlayerCount];
        int hit = -1;
        for (int i = 0; i < 240 && hit < 0; i++)
        {
            w.Step(buf);
            if (Has(w, EventKind.Block, 1) || Has(w, EventKind.Tackle, 1) || Has(w, EventKind.Touch, 1)) hit = w.Tick;
        }
        Assert.True(hit > 0, "the pass went through the defender");
        for (int i = 0; i < 3; i++) w.Step(buf);
        Assert.Null(w.Pass);
        Assert.Equal(0, w.PassesReceived);
        foreach (var m in w.Teams[0]) Assert.Equal(0, m.MeetNotBefore);
    }

    [Fact]
    public void AFasterPassCostsALongerWaitBeforeTheNextTouch()
    {
        var t = new Tuning();
        var k = t.Receive;
        Assert.True(k.Cooldown.Evaluate(20f) > k.Cooldown.Evaluate(5f));
        Assert.True(k.Error.Evaluate(20f) > k.Error.Evaluate(0f));
        Assert.True(k.Rating.ForRating(99) < k.Rating.ForRating(1));
        // In the world: after the first touch on a pass the receiver plays no second touch inside the cooldown.
        var w = WithBall(10f, 0f, 10f, 8f);
        Assert.True(StrikePass(w));
        var buf = new TickIntent[w.PlayerCount];
        int first = -1, second = -1;
        float until = 0f;
        for (int i = 0; i < 720 && second < 0; i++)
        {
            buf[0] = first < 0 ? default : new TickIntent { MoveX = 1f };
            w.Step(buf);
            if (!Has(w, EventKind.Touch, 0, 1)) continue;
            if (first < 0) { first = w.Tick; until = w.Teams[0][1].ReceiveCooldownUntil; }
            else second = w.Tick;
        }
        Assert.True(first > 0 && second > 0);
        Assert.True(until > first / (float)t.TickRate + 0.05f, "no cooldown was set at the first touch");
        Assert.True(second / (float)t.TickRate >= until - 1f / t.TickRate, $"second touch at tick {second}, cooldown until {until} s");
    }
}
