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
                if (w.Attacker.Action is { Pending: true, Phase: ActionPhase.Windup } pa && pa.ContactTick - w.Tick >= 4) a = pa;
                else Step(w, Dir(0f));
            }
            Assert.NotNull(a);
            // Two ticks at neutral (one 60 Hz poll), then the stick is back: the touch is still the committed one, not a stop.
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

    [Theory]
    [InlineData(1, 0f)]
    [InlineData(50, 0f)]
    [InlineData(99, 0f)]
    [InlineData(1, 1f)]
    [InlineData(50, 1f)]
    [InlineData(99, 1f)]
    public void ASharpTurnAtARunIsABrakingArcNotASoleStop(int dribbling, float sprint)
    {
        // A jog or sprint with the ball, the stick turned 150 degrees back and held: no turn at the feet; he slows to no less than
        // 40% of his speed (2.4 m/s at least), keeps the ball and comes out along the stick.
        for (ulong seed = 7; seed <= 12; seed++)
        {
            var w = Lab(seed, dribbling);
            var p = w.Attacker;
            int pre = 360 + 7 * (int)seed;
            for (int i = 0; i < pre; i++) Step(w, Dir(0f) with { Sprint = sprint });
            Assert.True(p.InPossession);
            float entry = new Vector2(p.Velocity.X, p.Velocity.Z).Length();
            float floor = MathF.Max(0.4f * entry, 2.4f), vmin = float.MaxValue;
            int aligned = -1;
            var stick = new Vector2(MathF.Cos(150f * MathF.PI / 180f), MathF.Sin(150f * MathF.PI / 180f));
            for (int i = 0; i < 180; i++)
            {
                Step(w, Dir(150f) with { Sprint = sprint });
                Assert.False(p.SkillMove is { FeetTurn: true }, $"seed {seed}: a turn at the feet at {entry:F1} m/s");
                var v = new Vector2(p.Velocity.X, p.Velocity.Z);
                vmin = MathF.Min(vmin, v.Length());
                if (aligned < 0 && v.Length() > 0.3f && Vector2.Dot(v / v.Length(), stick) >= MathF.Cos(15f * MathF.PI / 180f)) aligned = i + 1;
                Assert.True(p.InPossession, $"seed {seed}: the ball lost in the turn");
            }
            _out.WriteLine($"rating {dribbling} sprint {sprint} seed {seed}: entry {entry:F2} vmin {vmin:F2} (floor {floor:F2}) along the stick after {aligned} ticks");
            Assert.True(vmin >= floor - 0.05f, $"seed {seed}: down to {vmin:F2} m/s from {entry:F2}");
            Assert.InRange(aligned, 1, (int)(1.3f * w.Tuning.TickRate));
        }
    }

    [Theory]
    [InlineData(1, 0f)]
    [InlineData(50, 0f)]
    [InlineData(99, 0f)]
    [InlineData(1, 1f)]
    [InlineData(50, 1f)]
    [InlineData(99, 1f)]
    public void ASharpTurnAtARunIsTight(int dribbling, float sprint)
    {
        static World Field(ulong seed, int dribbling)
        {
            var sheet = TestData.Player("striker_a").Clone();
            sheet.Dribbling = dribbling;
            var sc = new Scenario
            {
                Id = "field",
                Pitch = new PitchSpec { Length = 400f, Width = 200f },
                Ball = new StartSpot { X = 150.5f },
                Attacker = new StartSpot { X = 150f },
            };
            var w = new World(TestData.Tuning(), sc, sheet, TestData.Player("the_wall"), seed);
            w.Begin();
            return w;
        }

        // A jog or sprint with the ball (striker_a at the given dribbling), the stick turned 120, 150 or 180 degrees back and held.
        // From the touch that hooks the ball round until he runs within 15 degrees of the stick the ball stays within 1.0 m of him
        // (0.8 m at dribbling 99), and he gets there within 0.9 s of that touch (1.2 s at dribbling 1; the time to reach a ball
        // already pushed on before the stick turned is the dribble's, not the turn's), at most 2 m off the line he came in on; his
        // speed never falls below 40% of his entry speed (2.4 m/s at least) nor changes by more than 15 m/s^2 in a tick; he keeps
        // the ball.
        float ballMax = dribbling >= 99 ? 0.8f : 1.0f, within = dribbling <= 1 ? 1.2f : 0.9f;
        foreach (float deg in new[] { 120f, 150f, 180f })
            for (ulong seed = 1; seed <= 6; seed++)
            {
                var w = Field(seed, dribbling);
                var p = w.Attacker;
                float dt = w.Tuning.Dt;
                // (the turn at every point of the touch cadence, by the seed)
                int pre = 360 + (int)(seed * 13 % 84);
                for (int i = 0; i < pre; i++) Step(w, Dir(0f) with { Sprint = sprint });
                Assert.True(p.InPossession);
                float entry = p.Speed, prev = entry;
                float floor = MathF.Max(0.4f * entry, 2.4f), vmin = float.MaxValue, hookBall = 0f, lateral = 0f, dspd = 0f;
                float z0 = p.Position.Z;
                int aligned = -1, hookAt = -1;
                bool hooked = false;
                var stick = new Vector2(MathF.Cos(deg * MathF.PI / 180f), MathF.Sin(deg * MathF.PI / 180f));
                string label = $"rating {dribbling} {(sprint > 0f ? "sprint" : "jog")} {deg} deg seed {seed}";
                for (int i = 0; i < 180; i++)
                {
                    Step(w, Dir(deg) with { Sprint = sprint });
                    Assert.True(p.InPossession, $"{label}: the ball lost in the turn");
                    var v = new Vector2(p.Velocity.X, p.Velocity.Z);
                    vmin = MathF.Min(vmin, v.Length());
                    dspd = MathF.Max(dspd, MathF.Abs(v.Length() - prev) / dt);
                    prev = v.Length();
                    if (aligned >= 0) continue;
                    if (!hooked && p.TouchAction is { Made: true, Kind: ActionKind.Turn or ActionKind.Cut } h && h.ContactTick == w.Tick) { hooked = true; hookAt = i; }
                    if (hooked) hookBall = MathF.Max(hookBall, TouchModel.HorizontalDistance(p.Position, w.Ball.Position));
                    lateral = MathF.Max(lateral, MathF.Abs(p.Position.Z - z0));
                    if (v.Length() > 0.3f && Vector2.Dot(v / v.Length(), stick) >= MathF.Cos(15f * MathF.PI / 180f)) aligned = i + 1;
                }
                _out.WriteLine($"{label}: entry {entry:F2} vmin {vmin:F2} (floor {floor:F2}) ball {hookBall:F2} lateral {lateral:F2} along the stick {aligned * dt:F2} s after the stick, {(aligned - hookAt) * dt:F2} s after the hook, dspd {dspd:F1}");
                Assert.True(hooked, $"{label}: no turn or cut touch");
                Assert.True(hookBall <= ballMax, $"{label}: the ball {hookBall:F2} m from him in the turn");
                Assert.True(aligned > 0 && (aligned - hookAt) * dt <= within + 1e-4f, $"{label}: along the stick {(aligned - hookAt) * dt:F2} s after the hook");
                Assert.True(lateral <= 2f, $"{label}: {lateral:F2} m off his line");
                Assert.True(vmin >= floor - 0.01f, $"{label}: down to {vmin:F2} m/s from {entry:F2}");
                Assert.True(dspd <= 15f, $"{label}: speed changed at {dspd:F1} m/s^2");
            }
    }

    /// <summary>
    /// The play-test penalty rounds kept under TestData/playtest (the first session, and every round of the three recorded
    /// 2026-11-19 sessions), replayed with the current code (they play out differently from the recording): every committed
    /// touch is played while the stick is pushed, and possession is only ever lost to the keeper, the shot or a goal. Skipped when the
    /// rounds are not in the tree.
    /// </summary>
    [Fact]
    public void ThePlayTestRoundsPlayEveryCommittedTouchAndKeepPossession()
    {
        string dir = Path.Combine(TestData.DataDir, "..", "src", "FootballSim.Core.Tests", "TestData", "playtest");
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "round-*.json") : Array.Empty<string>();
        if (files.Length == 0) { _out.WriteLine($"no play-test rounds under {dir}; skipped"); return; }
        Assert.True(files.Length >= 18, $"{files.Length} rounds");
        foreach (var f in files.OrderBy(x => x))
        {
            var rp = Replay.Load(f, out var err);
            Assert.True(rp != null, err);
            var t = new Tuning();
            var w = new World(t, TestData.Scenario(rp!.ScenarioId), TestData.Player(rp.AttackerId), TestData.Player(rp.KeeperId), rp.Seed);
            w.Begin();
            var buf = new TickIntent[w.PlayerCount];
            int per = Math.Max(1, rp.PlayersPerTick);
            int unplayed = 0, lost = 0;
            var p = w.Attacker;
            bool keeperOrShot = false;
            for (int tick = 0; tick < rp.TickCount && w.State != RoundState.Report; tick++)
            {
                Array.Clear(buf);
                for (int i = 0; i < w.PlayerCount && i < per; i++) buf[i] = rp.Intents[tick * per + i].ToIntent();
                var waiting = p.TouchAction is { Pending: true } pw ? pw : null;
                w.Step(buf);
                bool pushed = buf[0].Move.Length() >= t.Touch.StickThreshold;
                if (pushed && waiting != null && !waiting.Made && waiting.Phase == ActionPhase.Done && w.Tick >= waiting.ContactTick) unplayed++;
                // (A ball he walks over the goal line ends the round: that is a goal, not a ball lost.)
                keeperOrShot |= w.Shots.Count > 0 || w.Events.Any(e => e.Kind is EventKind.Save or EventKind.Goal);
                if (!keeperOrShot && w.Events.Any(e => e.Kind == EventKind.PossessionLost && e.Team == 0)) lost++;
            }
            _out.WriteLine($"{Path.GetFileName(f)}: {unplayed} committed touches not played with the stick pushed, {lost} possessions lost by himself");
            Assert.Equal(0, unplayed);
            Assert.Equal(0, lost);
        }
    }
}
