using System.Numerics;
using FootballSim.Core;
using Xunit.Abstractions;

namespace FootballSim.Core.Tests;

/// <summary>Dribbling with the explicit possession model, through the real world and its input path.</summary>
public class PossessionTests
{
    readonly ITestOutputHelper _out;

    public PossessionTests(ITestOutputHelper output) { _out = output; }

    static PlayerSheet Sheet(int dribbling = 75, int firstTouch = 75)
    {
        var s = TestData.Sheet(75);
        s.Dribbling = dribbling;
        s.FirstTouch = firstTouch;
        return s;
    }

    /// <summary>A world far from the goal: the ball 0.5 m in front of the attacker, the goal <paramref name="length"/> m away.</summary>
    static World Lab(PlayerSheet attacker, ulong seed, float z = 0f, float length = 52.5f)
    {
        var sc = new Scenario
        {
            Id = "lab",
            Pitch = new PitchSpec { HalfLength = length },
            Ball = new StartSpot { X = 2.5f, Z = z },
            Attacker = new StartSpot { X = 2.0f, Z = z },
        };
        var w = new World(new Tuning(), sc, attacker, TestData.Sheet(50), seed);
        w.Begin();
        return w;
    }

    static float BallDistance(World w) => TouchModel.HorizontalDistance(w.Attacker.Position, w.Ball.Position);

    static readonly TickIntent Jog = new() { MoveX = 1 };
    static readonly TickIntent Sprint = new() { MoveX = 1, Sprint = 1 };

    sealed class Run
    {
        public bool Lost;
        public float MaxDistance;
        public float StartX = float.NaN;
        public int Touches;
        public readonly List<SimEvent> Events = new();
    }

    /// <summary>Steps with a fixed intent until the attacker has gained possession and then covered <paramref name="metres"/>.</summary>
    static Run Dribble(World w, TickIntent it, float metres, Run? run = null)
    {
        run ??= new Run();
        var buf = new TickIntent[w.PlayerCount];
        buf[0] = it;
        for (int i = 0; i < 120 * 30 && w.State == RoundState.Live; i++)
        {
            w.Step(buf);
            foreach (var e in w.Events)
            {
                run.Events.Add(e);
                if (e.Kind == EventKind.Touch) run.Touches++;
                if (e.Kind == EventKind.PossessionLost) run.Lost = true;
            }
            if (float.IsNaN(run.StartX))
            {
                if (!w.Attacker.InPossession) continue;
                run.StartX = w.Attacker.Position.X;
            }
            run.MaxDistance = MathF.Max(run.MaxDistance, BallDistance(w));
            if (run.Lost || w.Attacker.Position.X - run.StartX >= metres) break;
        }
        return run;
    }

    static void Steps(World w, TickIntent it, int ticks, Action<World>? each = null)
    {
        var buf = new TickIntent[w.PlayerCount];
        buf[0] = it;
        for (int i = 0; i < ticks && w.State == RoundState.Live; i++)
        {
            w.Step(buf);
            each?.Invoke(w);
        }
    }

    // 1.
    [Fact]
    public void JogInAStraightLineKeepsTheBallAtTheFeet()
    {
        for (ulong seed = 1; seed <= 50; seed++)
        {
            var run = Dribble(Lab(Sheet(), seed), Jog, 20f);
            Assert.False(run.Lost, $"seed {seed}: possession lost");
            Assert.True(run.MaxDistance <= 1.2f, $"seed {seed}: ball {run.MaxDistance:F2} m away");
        }
    }

    // 2. A touch is committed: a 90 degree stick change (not more than the replan angle) is played by the next planned
    // touch, at most three jog steps (about 0.95 s) away.
    [Fact]
    public void JogThenTurnNinetyDegreesFollowsWithinOneTouch()
    {
        var turn = new TickIntent { MoveZ = 1 };
        for (ulong seed = 1; seed <= 50; seed++)
        {
            var w = Lab(Sheet(), seed, z: -10f);
            var run = Dribble(w, Jog, 8f);
            Assert.False(run.Lost);
            float max = 0f, followedAt = float.NaN;
            bool lost = false;
            int ticks = 0;
            Steps(w, turn, 240, x =>
            {
                ticks++;
                max = MathF.Max(max, BallDistance(x));
                lost |= x.Events.Any(e => e.Kind == EventKind.PossessionLost);
                var v = x.Ball.Velocity;
                float angle = MathF.Abs(MathF.Atan2(v.X, v.Z)) * 180f / MathF.PI;   // off +Z
                if (float.IsNaN(followedAt) && v.Length() > 0.5f && angle <= 25f) followedAt = ticks * x.Tuning.Dt;
            });
            Assert.False(lost, $"seed {seed}: possession lost in the turn");
            Assert.True(w.Attacker.InPossession);
            Assert.True(followedAt <= 1.0f, $"seed {seed}: ball followed the turn after {followedAt} s");
            Assert.True(max <= 1.5f, $"seed {seed}: ball {max:F2} m away in the turn");
        }
    }

    // 3.
    [Fact]
    public void JogThenReleaseTheStickStopsTheBallAtTheFeet()
    {
        for (ulong seed = 1; seed <= 50; seed++)
        {
            var w = Lab(Sheet(), seed);
            Dribble(w, Jog, 10f);
            Steps(w, default, 600);
            Assert.True(w.Attacker.Speed < 0.1f, $"seed {seed}: player still moving at {w.Attacker.Speed:F2}, ball {BallDistance(w):F2} m away at {w.Ball.Velocity.Length():F2}");
            Assert.True(w.Ball.Velocity.Length() < 0.06f, $"seed {seed}: ball still rolling at {w.Ball.Velocity.Length():F2}");
            Assert.True(BallDistance(w) <= 1.0f, $"seed {seed}: ball stopped {BallDistance(w):F2} m away");
            Assert.True(w.Attacker.InPossession, $"seed {seed}: possession lost");
        }
    }

    // 4.
    [Fact]
    public void CloseControlKeepsTheBallWithinPointEightMetres()
    {
        for (ulong seed = 1; seed <= 50; seed++)
        {
            var run = Dribble(Lab(Sheet(), seed), new TickIntent { MoveX = 1, CloseControl = true }, 10f);
            Assert.False(run.Lost);
            Assert.True(run.MaxDistance <= 0.8f, $"seed {seed}: ball {run.MaxDistance:F2} m away");
        }
    }

    static (double Kept, double MeanMax) SprintStats(int dribbling, float metres, int n)
    {
        int kept = 0;
        double sumMax = 0;
        for (ulong seed = 1; seed <= (ulong)n; seed++)
        {
            var run = Dribble(Lab(Sheet(dribbling), seed), Sprint, metres);
            if (!run.Lost) kept++;
            sumMax += run.MaxDistance;
        }
        return ((double)kept / n, sumMax / n);
    }

    // 5.
    [Fact]
    public void SprintKeepsPossessionAndLowDribblingKnocksItFurther()
    {
        var hi = SprintStats(90, 30f, 200);
        var lo = SprintStats(40, 30f, 200);
        _out.WriteLine($"sprint 30 m: dribbling 90 kept {hi.Kept:P1}, mean max {hi.MeanMax:F2} m; dribbling 40 kept {lo.Kept:P1}, mean max {lo.MeanMax:F2} m");
        Assert.True(hi.Kept >= 0.95, $"dribbling 90 kept {hi.Kept:P1}");
        Assert.True(lo.MeanMax > hi.MeanMax + 0.3, $"mean max distance: dribbling 40 {lo.MeanMax:F2} m, 90 {hi.MeanMax:F2} m");
    }

    // 6. Steps per touch at a sprint is a whole number of steps by dribbling (6 at 1 down to 4 at 99: 5 at 40 and 60, 4 at
    // 80), so the distance falls in steps and must never rise.
    [Fact]
    public void MaxBallDistanceDoesNotRiseWithDribbling()
    {
        double d40 = SprintStats(40, 20f, 200).MeanMax, d60 = SprintStats(60, 20f, 200).MeanMax, d80 = SprintStats(80, 20f, 200).MeanMax;
        _out.WriteLine($"sprint 20 m mean max distance 40/60/80: {d40:F2} / {d60:F2} / {d80:F2}");
        Assert.True(d40 >= d60 - 0.05 && d60 >= d80 - 0.05 && d40 > d80 + 0.1, $"mean max distance 40/60/80: {d40:F2} / {d60:F2} / {d80:F2}");
    }

    /// <summary>
    /// Sprints for two seconds, knocks the ball on, releases the stick once the knock-on is made and the ball is well ahead,
    /// and waits for PossessionLost (only a knock-on or a heavy touch lets the ball be lost by distance).
    /// </summary>
    static bool LoseTheBall(World w)
    {
        var buf = new TickIntent[w.PlayerCount];
        float reach = TouchModel.FootReach(w.Attacker.Sheet, w.Tuning);
        for (int i = 0; i < 120 * 10; i++)
        {
            bool away = w.Attacker.InPossession && Possession.CanLoseByDistance(w.Attacker) && w.Attacker.Speed > 6f && BallDistance(w) > reach + 0.6f
                        && w.Ball.Velocity.X > w.Attacker.Velocity.X;
            buf[0] = away ? default : Sprint;
            buf[0].KnockOn = !away && i >= 240 && w.Attacker.InPossession && w.Attacker.Speed > 6f;
            w.Step(buf);
            if (away) break;
        }
        for (int i = 0; i < 120 * 3; i++)
        {
            buf[0] = default;
            w.Step(buf);
            if (w.Events.Any(e => e.Kind == EventKind.PossessionLost)) return true;
        }
        return false;
    }

    // 7.
    [Fact]
    public void LosingAndRegainingRaisesTheEventsAndFirstTouchMatters()
    {
        double MeanFirstTouchError(int firstTouch)
        {
            double sum = 0; int n = 0;
            for (ulong seed = 1; seed <= 120; seed++)
            {
                var w = Lab(Sheet(40, firstTouch), seed, length: 150f);
                Assert.True(LoseTheBall(w), $"seed {seed}: possession never lost");
                Assert.False(w.Attacker.InPossession);
                Assert.Null(w.BallController);

                // Run straight at the ball until the first contact.
                var buf = new TickIntent[w.PlayerCount];
                bool gained = false;
                for (int i = 0; i < 120 * 10 && !gained; i++)
                {
                    var d = new Vector2(w.Ball.Position.X - w.Attacker.Position.X, w.Ball.Position.Z - w.Attacker.Position.Z);
                    d = Vector2.Normalize(d);
                    buf[0] = new TickIntent { MoveX = d.X, MoveZ = d.Y, Sprint = 1 };
                    w.Step(buf);
                    if (w.Events.Any(e => e.Kind == EventKind.PossessionGained && e.Team == 0 && e.PlayerIndex == 0))
                    {
                        gained = true;
                        // Error = the ball's velocity across the stick, relative to its speed along it. It includes the direction
                        // error and the unwanted part of the relative speed that the contact kept.
                        var v = new Vector2(w.Ball.Velocity.X, w.Ball.Velocity.Z);
                        float along = MathF.Max(Vector2.Dot(v, d), 0.5f);
                        sum += MathF.Abs(v.X * d.Y - v.Y * d.X) / along;
                        n++;
                    }
                }
                Assert.True(gained, $"seed {seed}: never regained the ball ({w.State}, {w.Outcome}, ball {w.Ball.Position}, player {w.Attacker.Position})");
                Assert.True(w.Attacker.InPossession);
                Assert.Same(w.Attacker, w.BallController);
            }
            return sum / n;
        }
        double poor = MeanFirstTouchError(40), good = MeanFirstTouchError(90);
        _out.WriteLine($"first-touch error: first_touch 40 {poor:P1}, 90 {good:P1}");
        Assert.True(poor > 1.5 * good, $"first-touch error: first_touch 40 {poor:P1}, 90 {good:P1}");
    }

    // 8.
    [Fact]
    public void NextTouchPredictionIsWithinFiftyMillisecondsInASteadyJog()
    {
        var errors = new List<float>();
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var w = Lab(Sheet(), seed);
            Dribble(w, Jog, 6f);   // settle into a steady jog first
            var pending = new List<float>();
            Steps(w, Jog, 120 * 3, x =>
            {
                if (x.Events.Any(e => e.Kind == EventKind.Touch))
                {
                    foreach (var p in pending) errors.Add(MathF.Abs(p - x.SimTime));
                    pending.Clear();
                }
                if (!float.IsNaN(x.Attacker.NextTouchEta)) pending.Add(x.SimTime + x.Attacker.NextTouchEta);
            });
        }
        Assert.True(errors.Count > 1000);
        errors.Sort();
        float median = errors[errors.Count / 2];
        _out.WriteLine($"next-touch eta error: median {median:F3} s, 90th percentile {errors[errors.Count * 9 / 10]:F3} s over {errors.Count} predictions");
        Assert.True(median < 0.05f, $"median eta error {median:F3} s");
    }

    // 9.
    [Fact]
    public void ChargingAShotFromAJogStrikes()
    {
        int struck = 0, n = 200;
        for (ulong seed = 1; seed <= (ulong)n; seed++)
        {
            var w = Lab(Sheet(), seed);
            Dribble(w, Jog, 8f);
            Steps(w, new TickIntent { MoveX = 1, ShootHeld = true }, (int)(0.6f * 120));
            var buf = new TickIntent[w.PlayerCount];
            buf[0] = Jog;   // release, still running
            for (int i = 0; i < 60 && w.Shots.Count == 0 && w.State is RoundState.Live or RoundState.Strike; i++) w.Step(buf);
            if (w.Shots.Count == 1) struck++;
        }
        _out.WriteLine($"charge 0.6 s from a jog: {struck} of {n} struck");
        Assert.True(struck >= 0.95 * n, $"{struck} of {n} charges struck");
    }

    // 10.
    [Fact]
    public void TouchAndStrikeEventsCarryTheFootAndThePlayer()
    {
        var w = Lab(Sheet(), 3);
        var run = Dribble(w, Jog, 5f);
        var touches = run.Events.Where(e => e.Kind == EventKind.Touch).ToList();
        Assert.NotEmpty(touches);
        Assert.All(touches, e => { Assert.Equal(0, e.Team); Assert.Equal(0, e.PlayerIndex); });
        Assert.Equal(touches[^1].Foot, w.Attacker.LastTouchFoot);
        Assert.Contains(run.Events, e => e.Kind == EventKind.PossessionGained && e.Team == 0);

        // A ball clearly on the weak (left, -Z) side of a right-footed player is touched with the left foot.
        var s = Sheet();
        Assert.Equal(Foot.Right, s.PreferredFoot);
        var v = Lab(s, 4);
        v.Ball.Position = new Vector3(v.Attacker.Position.X + 0.3f, v.Ball.Radius, v.Attacker.Position.Z - 0.3f);
        Steps(v, Jog, 1);
        var touch = Assert.Single(v.Events, e => e.Kind == EventKind.Touch);
        Assert.Equal(Foot.Left, touch.Foot);
        Assert.Equal(Foot.Left, v.Attacker.LastTouchFoot);

        // The strike event names the foot and the player, and matches the shot and StrikeFoot.
        var p = TestData.Live("penalty", seed: 5);
        SimEvent? strike = null;
        float strikeEta = float.NaN;
        var buf = new TickIntent[p.PlayerCount];
        var script = new ScriptedAttacker();
        for (int i = 0; i < 2000 && strike == null; i++)
        {
            buf[0] = script.Next(p);
            p.Step(buf);
            if (p.State == RoundState.Strike && float.IsNaN(strikeEta)) strikeEta = p.Attacker.StrikeEta;
            foreach (var e in p.Events) if (e.Kind == EventKind.Strike) strike = e;
        }
        Assert.NotNull(strike);
        Assert.Equal(0, strike!.Value.Team);
        Assert.Equal(0, strike.Value.PlayerIndex);
        Assert.Equal(p.Shots[0].Foot, strike.Value.Foot);
        Assert.Equal(p.Shots[0].Foot, p.Attacker.StrikeFoot);
        Assert.InRange(strikeEta, 0.05f, 0.1f);
        Assert.True(float.IsNaN(p.Attacker.StrikeEta));
    }

    // 11.
    [Fact]
    public void DribblingIsDeterministic()
    {
        string Trace(ulong seed)
        {
            var w = Lab(Sheet(), seed);
            var rng = new SimRng(99);
            var buf = new TickIntent[w.PlayerCount];
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 1200 && w.State == RoundState.Live; i++)
            {
                if (i % 30 == 0) buf[0] = new TickIntent { MoveX = rng.Uniform(0.2f, 1f), MoveZ = rng.Uniform(-1f, 1f), Sprint = rng.NextFloat(), CloseControl = rng.NextFloat() < 0.2f };
                w.Step(buf);
                sb.Append(w.Ball.Position).Append(w.Attacker.Position).Append(w.Attacker.NextTouchEta).Append(w.Attacker.InPossession);
                foreach (var e in w.Events) sb.Append(e);
            }
            return sb.ToString();
        }
        Assert.Equal(Trace(7), Trace(7));
        Assert.NotEqual(Trace(7), Trace(8));
    }

    [Fact]
    public void ContractIsFilledWhileDribbling()
    {
        var w = Lab(Sheet(), 1);
        Assert.False(w.Attacker.InPossession);
        Assert.True(float.IsNaN(w.Attacker.NextTouchEta));
        int withEta = 0, ticks = 0;
        Steps(w, Jog, 240, x =>
        {
            if (!x.Attacker.InPossession) return;
            ticks++;
            if (float.IsNaN(x.Attacker.NextTouchEta)) return;
            withEta++;
            Assert.InRange(x.Attacker.NextTouchEta, 0f, x.Tuning.Possession.PredictionHorizon);
            // The planned contact point lies where the player will be at the contact, the contact zone ahead.
            float reachable = x.Attacker.NextTouchEta * Movement.TopSpeed(x.Attacker.Sheet, x.Tuning) + 0.6f;
            Assert.InRange(TouchModel.HorizontalDistance(x.Attacker.Position, x.Attacker.NextTouchPoint), 0f, reachable);
            Assert.True(float.IsNaN(x.Attacker.StrikeEta));
        });
        Assert.True(withEta > 0.9 * ticks, $"{withEta} of {ticks} ticks had a next touch");
        Assert.Same(w.Attacker, w.BallController);
    }

    [Fact]
    public void AStrikeEndsPossessionAndAHeldBallIsTheKeepers()
    {
        for (ulong seed = 1; seed <= 400; seed++)
        {
            var w = TestData.Live("breakaway", seed: seed);
            var script = new ScriptedAttacker();
            var buf = new TickIntent[w.PlayerCount];
            for (int i = 0; i < 4000 && w.State != RoundState.Report; i++)
            {
                buf[0] = script.Next(w);
                bool had = w.Attacker.InPossession;
                w.Step(buf);
                if (w.Events.Any(e => e.Kind == EventKind.Strike))
                {
                    if (had) Assert.Contains(w.Events, e => e.Kind == EventKind.PossessionLost && e.Team == 0);
                    Assert.Null(w.BallController);
                    Assert.False(w.Attacker.InPossession);
                }
                if (w.Outcome == Outcome.Held && w.State == RoundState.Outcome)
                {
                    Assert.Contains(w.Events, e => e.Kind == EventKind.PossessionGained && e.Team == 1);
                    Assert.Same(w.Keeper, w.BallController);
                    Assert.True(w.Keeper.InPossession);
                    return;
                }
            }
        }
        Assert.Fail("no held shot in 400 breakaways");
    }
}
