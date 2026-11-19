using System.Numerics;
using FootballSim.Core;
using Xunit.Abstractions;

namespace FootballSim.Core.Tests;

/// <summary>
/// The dribbling faults found in the 2026-11-19 gamepad play-test: touches dropped at contact on a drifting stick, easing off
/// with the stick held after a lost touch, a flick through neutral, and a reversal with the ball at the feet.
/// </summary>
public class DribbleFixTests
{
    readonly ITestOutputHelper _out;

    public DribbleFixTests(ITestOutputHelper output) { _out = output; }

    static World Lab(ulong seed, int dribbling = 70)
    {
        var s = TestData.Sheet(75);
        s.Dribbling = dribbling;
        var sc = new Scenario
        {
            Id = "lab",
            Pitch = new PitchSpec { Length = 300f, Width = 200f },
            Ball = new StartSpot { X = 2.5f },
            Attacker = new StartSpot { X = 2.0f },
        };
        var w = new World(new Tuning(), sc, s, TestData.Sheet(50), seed);
        w.Begin();
        return w;
    }

    static TickIntent Dir(float deg, bool close = false)
    {
        float a = deg * MathF.PI / 180f;
        return new TickIntent { MoveX = MathF.Cos(a), MoveZ = MathF.Sin(a), CloseControl = close };
    }

    static void Step(World w, TickIntent it)
    {
        var buf = new TickIntent[w.PlayerCount];
        buf[0] = it;
        w.Step(buf);
    }

    [Fact]
    public void ANeutralFlickShorterThanTheDebounceKeepsTheWaitingTouch()
    {
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var w = Lab(seed);
            for (int i = 0; i < 240; i++) Step(w, Dir(0f));
            BallAction? a = null;
            for (int i = 0; i < 600 && a == null; i++)
            {
                if (w.Attacker.Action is { Pending: true, Phase: ActionPhase.PreWindup } pa) a = pa;
                else Step(w, Dir(0f));
            }
            Assert.NotNull(a);
            // Two ticks at neutral (one 60 Hz poll), then the stick is back: the touch is still the planned one, not a stop.
            Step(w, default);
            Step(w, default);
            Assert.NotEqual(ActionKind.Stop, a!.Kind);
            Assert.True(a.Pending, $"seed {seed}: the waiting touch was dropped by the flick");
            // The next touch made is a dribble touch, never a stop.
            BallAction? made = null;
            for (int i = 0; i < 120 && made == null; i++)
            {
                Step(w, Dir(0f));
                if (w.Events.Any(e => e.Kind == EventKind.Touch && e.Team == 0)) made = w.Attacker.Action;
            }
            Assert.NotNull(made);
            Assert.NotEqual(ActionKind.Stop, made!.Kind);
        }
    }

    [Fact]
    public void AReversalWithTheBallAtTheFeetTurnsItOntoTheStickAtOnce()
    {
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var w = Lab(seed);
            for (int i = 0; i < 300 + 7 * (int)seed; i++) Step(w, Dir(0f, close: true));
            Assert.True(w.Attacker.InPossession);
            float stick = 165f * MathF.PI / 180f;
            int aligned = -1;
            for (int i = 0; i < 120 && aligned < 0; i++)
            {
                Step(w, Dir(165f, close: true));
                var bv = new Vector2(w.Ball.Velocity.X, w.Ball.Velocity.Z);
                if (bv.Length() > 0.3f && MathF.Abs(Movement.WrapAngle(MathF.Atan2(bv.Y, bv.X) - stick)) <= 30f * MathF.PI / 180f) aligned = i + 1;
            }
            _out.WriteLine($"seed {seed}: ball along the stick after {aligned} ticks");
            Assert.InRange(aligned, 1, (int)(0.35f * w.Tuning.TickRate));
            for (int i = 0; i < 240; i++) Step(w, Dir(165f, close: true));
            Assert.True(w.Attacker.InPossession, $"seed {seed}: possession lost after the turn");
        }
    }

    /// <summary>
    /// The three penalty rounds of the play-test, replayed with the current code (they play out differently from the recording):
    /// no touch is dropped at its contact while the stick is pushed, and no slowdown is planned with the stick pushed further off
    /// than slowdown_max_ahead. Skipped when the rounds are not in the tree.
    /// </summary>
    [Fact]
    public void ThePlayTestRoundsDropNoTouchWithTheStickPushed()
    {
        string dir = Path.Combine(TestData.DataDir, "..", "src", "FootballSim.Core.Tests", "TestData", "playtest");
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "round-*.json") : Array.Empty<string>();
        if (files.Length == 0) { _out.WriteLine($"no play-test rounds under {dir}; skipped"); return; }
        foreach (var f in files.OrderBy(x => x))
        {
            var rp = Replay.Load(f, out var err);
            Assert.True(rp != null, err);
            var t = new Tuning();
            var w = new World(t, TestData.Scenario(rp!.ScenarioId), TestData.Player(rp.AttackerId), TestData.Player(rp.KeeperId), rp.Seed);
            w.Begin();
            var buf = new TickIntent[w.PlayerCount];
            int per = Math.Max(1, rp.PlayersPerTick);
            int drops = 0, farSlowdowns = 0;
            BallAction? seen = null;
            var p = w.Attacker;
            for (int tick = 0; tick < rp.TickCount && w.State != RoundState.Report; tick++)
            {
                Array.Clear(buf);
                for (int i = 0; i < w.PlayerCount && i < per; i++) buf[i] = rp.Intents[tick * per + i].ToIntent();
                var waiting = p.TouchAction is { Pending: true } pw ? pw : null;
                w.Step(buf);
                bool pushed = buf[0].Move.Length() >= t.Touch.StickThreshold;
                if (pushed && waiting != null && !waiting.Made && waiting.Phase == ActionPhase.Done && w.Tick >= waiting.ContactTick) drops++;
                if (p.TouchAction is { Pending: true } a && a != seen)
                {
                    seen = a;
                    if (pushed && a.Kind == ActionKind.Slowdown && (a.ContactTick - w.Tick) * t.Dt > t.Touch.SlowdownMaxAhead + 1e-4f) farSlowdowns++;
                }
            }
            _out.WriteLine($"{Path.GetFileName(f)}: {drops} dropped at contact with the stick pushed, {farSlowdowns} far slowdowns");
            Assert.Equal(0, drops);
            Assert.Equal(0, farSlowdowns);
        }
    }
}
