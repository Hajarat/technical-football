using System.Numerics;
using FootballSim.Core;
using Xunit.Abstractions;

namespace FootballSim.Core.Tests;

/// <summary>Planned dribble touches, the touch boost and the knock-on, through the real world.</summary>
public class PlannedTouchTests
{
    readonly ITestOutputHelper _out;

    public PlannedTouchTests(ITestOutputHelper output) { _out = output; }

    /// <summary>The default tuning with the touch speed and direction errors switched off.</summary>
    static Tuning Exact()
    {
        var t = new Tuning();
        t.Touch.SpeedError = new RatingRange(0f, 0f);
        t.Touch.DirectionErrorDeg = new RatingRange(0f, 0f);
        return t;
    }

    static PlayerSheet Sheet(int dribbling = 75)
    {
        var s = TestData.Sheet(75);
        s.Dribbling = dribbling;
        return s;
    }

    /// <summary>A long pitch with the ball 0.5 m in front of the attacker.</summary>
    static World Lab(PlayerSheet attacker, ulong seed, Tuning? t = null, float z = 0f)
    {
        var sc = new Scenario
        {
            Id = "lab",
            Pitch = new PitchSpec { HalfLength = 150f },
            Ball = new StartSpot { X = 2.5f, Z = z },
            Attacker = new StartSpot { X = 2.0f, Z = z },
        };
        var w = new World(t ?? new Tuning(), sc, attacker, TestData.Sheet(50), seed);
        w.Begin();
        return w;
    }

    static float BallDistance(World w) => TouchModel.HorizontalDistance(w.Attacker.Position, w.Ball.Position);

    static bool AttackerTouched(World w) => w.Events.Any(e => e.Kind == EventKind.Touch && e.Team == 0 && e.PlayerIndex == 0);

    static void Steps(World w, TickIntent it, int ticks, Action<World>? each = null)
    {
        var buf = new TickIntent[w.PlayerCount];
        for (int i = 0; i < ticks && w.State == RoundState.Live; i++)
        {
            buf[0] = it;
            w.Step(buf);
            each?.Invoke(w);
        }
    }

    static void Gain(World w, TickIntent it)
    {
        for (int i = 0; i < 240 && !w.Attacker.InPossession; i++) Steps(w, it, 1);
        Assert.True(w.Attacker.InPossession);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 50)]
    [InlineData(0, 99)]
    [InlineData(1, 1)]
    [InlineData(1, 50)]
    [InlineData(1, 99)]
    [InlineData(2, 50)]
    public void ExactStraightDribbleMeetsTheBallAtEveryPlannedContact(int mode, int dribbling)
    {
        // Zero error draws: every planned contact has the ball centre inside the contact zone of the planned contact point,
        // on the foot in swing at that tick; the push solve (skid included) lands the ball within 0.03 m of the next meet.
        var t = Exact();
        var s = Sheet(dribbling);
        var it = new TickIntent { MoveX = 1, Sprint = mode == 1 ? 1f : 0f, CloseControl = mode == 2 };
        var w = Lab(s, 1, t);
        Gain(w, it);
        int contacts = 0, left = 0, right = 0;
        float worst = 0f;
        bool lost = false;
        var p = w.Attacker;
        Steps(w, it, 8 * 120, x =>
        {
            lost |= !p.InPossession;
            if (!AttackerTouched(x)) return;
            var a = p.PreviousAction is { Made: true } pa && pa.ContactTick == x.Tick ? pa : p.Action!;
            Assert.True(a.Made);
            Assert.Equal(x.Tick, a.ContactTick);
            Assert.True(p.IsStepping);
            Assert.Equal(p.SwingFoot, a.Foot);
            Assert.InRange(a.ContactMiss, 0f, t.Touch.ContactZoneRadius);
            worst = MathF.Max(worst, a.ContactMiss);
            contacts++;
            if (a.Foot == Foot.Left) left++; else right++;
        });
        _out.WriteLine($"mode {mode} dribbling {dribbling}: {contacts} contacts, L {left} R {right}, worst miss {worst:F3} m, speed {p.Speed:F2}");
        Assert.False(lost, "possession lost");
        Assert.True(contacts >= (mode == 1 ? 4 : 10), $"{contacts} contacts");
        Assert.True(worst <= 0.03f, $"worst miss {worst:F3} m");
        Assert.True(left > 0 && right > 0, $"feet L {left} R {right}");
    }

    [Fact]
    public void JogThenTurnNinetyDegreesKeepsPossession()
    {
        var jog = new TickIntent { MoveX = 1 };
        var turn = new TickIntent { MoveZ = 1 };
        foreach (var exact in new[] { true, false })
        {
            for (ulong seed = 1; seed <= 50; seed++)
            {
                var w = Lab(Sheet(), seed, exact ? Exact() : null, z: -15f);
                Gain(w, jog);
                Steps(w, jog, 120 * 2);
                Assert.True(w.Attacker.InPossession, $"seed {seed}: lost before the turn");
                bool lost = false;
                float max = 0f;
                Steps(w, turn, 120 * 2, x => { lost |= !x.Attacker.InPossession; max = MathF.Max(max, BallDistance(x)); });
                Assert.False(lost, $"seed {seed}, exact {exact}: possession lost in the turn");
                Assert.True(w.Ball.Velocity.Z > 2f, $"seed {seed}: the ball did not follow the turn ({w.Ball.Velocity})");
                Assert.True(max <= 1.5f, $"seed {seed}, exact {exact}: ball {max:F2} m away in the turn");
            }
        }
    }

    [Fact]
    public void TouchBoostShortensTimeToTopSpeedAndExpires()
    {
        var t = new Tuning();
        var s = Sheet(80);
        float top = Movement.TopSpeed(s, t);
        var go = new TickIntent { MoveX = 1, Sprint = 1 };

        // Seconds to reach 80% of top speed from a standstill, with a touch at the start (and every 0.3 s) or without.
        float TimeTo(bool touches)
        {
            var p = TestData.Outfield(s);
            float now = 0f;
            for (int i = 1; i < 2000; i++)
            {
                if (touches && (i - 1) % 36 == 0)
                {
                    p.LastTouchTime = now;
                    p.TouchBoostFrom = now;
                    p.TouchBoostUntil = now + t.Touch.TouchBoostDuration;
                    p.TouchBoostFactor = TouchModel.TouchBoostFactor(s, 0f, t);
                }
                now += t.Dt;
                Movement.Step(p, go, t.Dt, now, false, t, null);
                if (p.Speed >= 0.8f * top) return now;
            }
            return float.NaN;
        }
        float plain = TimeTo(false), boosted = TimeTo(true);
        _out.WriteLine($"time to 80% of top speed: plain {plain:F3} s, touched every 0.3 s {boosted:F3} s");
        Assert.True(boosted < plain - 0.05f, $"boosted {boosted:F3} s, plain {plain:F3} s");

        // The boost lasts touch_boost_duration after the touch, then the time constant is back to normal.
        var q = TestData.Outfield(s);
        q.LastTouchTime = q.TouchBoostFrom = 2f;
        q.TouchBoostUntil = 2f + t.Touch.TouchBoostDuration;
        q.TouchBoostFactor = 1.5f;
        float tau = Movement.TimeConstant(s, t);
        Assert.Equal(tau / 1.5f, Movement.TimeConstantAt(q, 2.1f, t), 5);
        Assert.Equal(tau, Movement.TimeConstantAt(q, 2f + t.Touch.TouchBoostDuration + 0.01f, t));
        q.LastTouchTime = 2.2f;   // another touch (or a strike) ends it early
        Assert.Equal(tau, Movement.TimeConstantAt(q, 2.21f, t));
    }

    [Fact]
    public void ADribbleTouchInTheWorldBoostsThePlayer()
    {
        var w = Lab(Sheet(), 2);
        var it = new TickIntent { MoveX = 1 };
        Gain(w, it);
        bool seen = false;
        Steps(w, it, 120, x =>
        {
            if (!AttackerTouched(x)) return;
            seen = true;
            Assert.True(Movement.TimeConstantAt(x.Attacker, x.SimTime + x.Tuning.Dt, x.Tuning) < Movement.TimeConstant(x.Attacker.Sheet, x.Tuning));
        });
        Assert.True(seen);
    }

    /// <summary>Sprints for two seconds, then holds the knock-on press until the knock-on is made. Returns its time.</summary>
    static float KnockOn(World w, TickIntent sprint)
    {
        Gain(w, sprint);
        Steps(w, sprint, 120 * 2);
        // The press is held until the ball is next in reach (with a low dribbling rating at a sprint, that is the next touch).
        var knock = sprint;
        knock.KnockOn = true;
        for (int i = 0; i < 240 && !AttackerTouched(w); i++) Steps(w, knock, 1);
        Assert.True(AttackerTouched(w), "no knock-on touch");
        Assert.Equal(w.SimTime, w.Attacker.KnockOnFrom);
        return w.SimTime;
    }

    [Fact]
    public void KnockOnIsCaughtAtItsCatchTimeWithPossessionKept()
    {
        foreach (int dribbling in new[] { 1, 50, 99 })
        {
            var t = Exact();
            var s = Sheet(dribbling);
            var sprint = new TickIntent { MoveX = 1, Sprint = 1 };
            var w = Lab(s, 3, t);
            float knockTime = KnockOn(w, sprint);
            float leave = new Vector2(w.Ball.Velocity.X, w.Ball.Velocity.Z).Length();
            float catchTime = TouchModel.KnockOnCatchTime(s, t);

            // Chase it: possession is kept until the player's next touch.
            bool lost = false, reached = false;
            float maxGap = 0f;
            for (int i = 0; i < 120 * 8 && !reached && !lost; i++)
            {
                Steps(w, sprint, 1);
                lost |= !w.Attacker.InPossession;
                maxGap = MathF.Max(maxGap, BallDistance(w));
                reached = AttackerTouched(w);
            }
            float caught = w.SimTime - knockTime;
            _out.WriteLine($"dribbling {dribbling}: leave speed {leave:F2} m/s, peak gap {maxGap:F2} m, touched again after {caught:F2} s (catch time {catchTime:F2} s), lost {lost}");
            Assert.False(lost, $"dribbling {dribbling}: possession lost while chasing (gap {maxGap:F2} m)");
            Assert.True(reached, $"dribbling {dribbling}: never reached the ball (max gap {maxGap:F2} m)");
            Assert.InRange(caught, catchTime - 0.15f, catchTime + 0.15f);
        }
    }

    [Fact]
    public void KnockOnIsLostWhenThePlayerStopsChasing()
    {
        var s = Sheet();
        var sprint = new TickIntent { MoveX = 1, Sprint = 1 };
        var w = Lab(s, 4);
        KnockOn(w, sprint);
        Assert.True(Possession.ChasingKnockOn(w.Attacker, w.Tuning));
        bool lost = false;
        Steps(w, default, 120 * 4, x => lost |= !x.Attacker.InPossession);
        Assert.True(lost, "possession kept by a player who stopped");
    }

    [Fact]
    public void ReplaysWithoutTheKnockOnFieldStillLoad()
    {
        const string json = """
            { "version": 1, "seed": 6, "attacker_id": "striker_a", "keeper_id": "the_wall", "scenario_id": "breakaway",
              "tuning_hash": "x", "players_per_tick": 2,
              "intents": [ { "mx": 1, "mz": 0, "sprint": 1, "close": false, "shoot": false, "finesse": false, "chip": false },
                           { "mx": 0, "mz": 0, "sprint": 0, "close": false, "shoot": false, "finesse": false, "chip": false } ] }
            """;
        var r = Replay.FromJson(json, out var error);
        Assert.Null(error);
        Assert.NotNull(r);
        Assert.Equal(1, r!.TickCount);
        var it = r.Intents[0].ToIntent();
        Assert.Equal(1f, it.MoveX);
        Assert.False(it.KnockOn);

        // The shipped smoke replay predates the field.
        string smoke = Path.Combine(TestData.DataDir, "..", "game", "tests", "smoke_replay.json");
        var s = Replay.Load(smoke, out error);
        Assert.Null(error);
        Assert.NotNull(s);
        Assert.True(s!.TickCount > 0);
        Assert.All(s.Intents, i => Assert.False(i.ToIntent().KnockOn));

        // A knock-on round-trips.
        var rec = IntentRecord.From(new TickIntent { MoveX = 1, KnockOn = true });
        var back = Replay.FromJson(new Replay { Intents = { rec } }.ToJson(), out error);
        Assert.Null(error);
        Assert.True(back!.Intents[0].ToIntent().KnockOn);
    }
}
