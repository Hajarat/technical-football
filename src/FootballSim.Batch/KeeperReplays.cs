using System.Globalization;
using FootballSim.Core;

/// <summary>
/// Keeper tools for the batch runner: a per-tick trace of the keeper for one scripted round, and a set of replays, one per visible
/// keeper action (and per closing-down, long-touch, chip and rounding situation), found by searching scripted rounds over seeds,
/// for game screenshots.
/// </summary>
static class KeeperReplays
{
    /// <summary>One wanted replay: its file stem, and a test over the round's per-tick keeper record that finds the tick range.</summary>
    sealed record Want(string Name, string Shows, Func<List<Frame>, (int From, int To)?> Find, string[]? Prefer = null, int Seeds = 0);

    /// <summary>
    /// The keeper's visible state after one tick, with his depth off his line, whether the first shot (if struck) is a chip, whether
    /// he has won the ball from a dribble and whether he has been taken round.
    /// </summary>
    readonly record struct Frame(int Tick, KeeperAction Action, KeeperDive Dive, SaveKind Save, float Depth = 0f, bool Struck = false,
        bool Chip = false, bool Won = false, bool Rounded = false);

    static readonly string[] SearchScenarios = { "penalty", "edge_of_box", "tight_angle", "breakaway", "angled_run", "keeper_rush", "halfway_run" };

    /// <summary>Ranges of consecutive ticks where <paramref name="match"/> holds.</summary>
    static IEnumerable<(int From, int To)> Runs(List<Frame> f, Func<Frame, bool> match)
    {
        int start = -1;
        for (int i = 0; i <= f.Count; i++)
        {
            bool m = i < f.Count && match(f[i]);
            if (m && start < 0) start = i;
            if (!m && start >= 0) { yield return (f[start].Tick, f[i - 1].Tick); start = -1; }
        }
    }

    static (int, int)? First(List<Frame> f, Func<Frame, bool> match, int minTicks = 1) =>
        Runs(f, match).Where(r => r.To - r.From + 1 >= minTicks).Select(r => ((int, int)?)r).FirstOrDefault();

    /// <summary>The first dive of this kind (whose save, when given, was of that kind before it ended), whole.</summary>
    static (int, int)? Dive(List<Frame> f, KeeperDive kind, SaveKind? save = null) =>
        Runs(f, x => x.Action == KeeperAction.Diving && x.Dive == kind)
            .Where(r => save == null || f.Any(x => x.Tick >= r.From && x.Tick <= r.To && x.Save == save))
            .Select(r => ((int, int)?)r).FirstOrDefault();

    static readonly Want[] Wants =
    {
        new("keeper-shuffle-along-arc", "Shuffle: side-stepping along the arc as the ball moves", f => First(f, x => x.Action == KeeperAction.Shuffle, 36),
            new[] { "angled_run" }),
        new("keeper-rush", "Rush: running out at a loose ball", f => First(f, x => x.Action == KeeperAction.Rush, 24), new[] { "keeper_rush" }),
        new("keeper-planted-then-reacting", "Planted for the shot, then Reacting after the strike", f =>
        {
            foreach (var p in Runs(f, x => x.Action == KeeperAction.Planted))
            {
                int next = f.FindIndex(x => x.Tick == p.To + 1);
                if (next >= 0 && f[next].Action == KeeperAction.Reacting)
                    return (p.From, Runs(f, x => x.Action == KeeperAction.Reacting).First(r => r.From == p.To + 1).To);
            }
            return null;
        }),
        new("keeper-dive-low", "Diving, Low: along the ground", f => Dive(f, KeeperDive.Low)),
        new("keeper-dive-side", "Diving, Side: a flat dive at mid height", f => Dive(f, KeeperDive.Side)),
        new("keeper-dive-high", "Diving, High: a leaping dive", f => Dive(f, KeeperDive.High)),
        new("keeper-dive-high-tip-over-bar", "Diving, High, tipping the ball over the bar", f => Dive(f, KeeperDive.High, SaveKind.TipOver)),
        new("keeper-dive-feet-block", "Diving, Feet: a standing block at the feet", f => Dive(f, KeeperDive.Feet)),
        new("keeper-dive-catch", "Diving, Catch: a standing catch at the body", f => Dive(f, KeeperDive.Catch)),
        new("keeper-smother", "Smother: diving at the ball at the attacker's feet", f => First(f, x => x.Action == KeeperAction.Smother),
            new[] { "keeper_rush" }),
        new("keeper-close-down", "Closing down: coming off his line as the attacker runs in, to the strike", f =>
        {
            // From the first tick to the last one before the strike, when he came at least 2.5 m further out over it.
            int strike = f.FindIndex(x => x.Struck);
            if (strike < 1) return null;
            return f[strike - 1].Depth - f[0].Depth >= 2.5f ? (f[0].Tick, f[strike - 1].Tick) : null;
        }, new[] { "halfway_run" }),
        new("keeper-spread", "Spread: stopped close to the attacker on the ball, made big", f => First(f, x => x.Action == KeeperAction.Spread, 12),
            new[] { "keeper_rush", "halfway_run" }),
        new("keeper-wins-long-touch", "Rush then Smother: winning a long sprint touch before the attacker gets back to it", f =>
        {
            int w = f.FindIndex(x => x.Won && !x.Struck);
            if (w < 0) return null;
            int start = w;
            while (start > 0 && f[start - 1].Action is KeeperAction.Rush or KeeperAction.Smother) start--;
            return f[start].Action is KeeperAction.Rush or KeeperAction.Smother ? (f[start].Tick, f[Math.Min(f.Count - 1, w + 30)].Tick) : null;
        }, new[] { "keeper_rush" }),
        new("keeper-backpedal-chip", "Backpedal under a chip over him, then the leap", f =>
        {
            int strike = f.FindIndex(x => x.Struck);
            if (strike < 0 || !f[strike].Chip) return null;
            // Under the first shot, a chip: he backpedals within its flight.
            var r = Runs(f, x => x.Action == KeeperAction.Backpedal).Where(x => x.From >= f[strike].Tick && x.From <= f[strike].Tick + 60 && x.To - x.From >= 12)
                .Select(x => ((int, int)?)x).FirstOrDefault();
            if (r is not (int from, int to)) return null;
            int i = f.FindIndex(x => x.Tick == to);
            int end = i;
            while (end + 1 < f.Count && f[end + 1].Action == KeeperAction.Diving) end++;
            return (from, f[end].Tick);
        }, new[] { "keeper_rush", "breakaway" }),
        new("keeper-dive-back", "Diving, Back: the backward leap at the end of a backpedal under a chip", f => Dive(f, KeeperDive.Back),
            new[] { "keeper_rush" }),
        new("keeper-rounded", "Taken round: the attacker gets the ball past him", f =>
        {
            int i = f.FindIndex(x => x.Rounded);
            return i < 0 ? null : (f[Math.Max(0, i - 60)].Tick, f[Math.Min(f.Count - 1, i + 60)].Tick);
        }, new[] { "keeper_rush", "halfway_run" }, Seeds: 3000),
        new("keeper-parry-grounded-getting-up", "A diving parry, then Grounded, then GettingUp", f =>
        {
            int i = f.FindIndex(x => x.Action == KeeperAction.Diving && x.Save == SaveKind.Parry);
            if (i < 0) return null;
            int g = f.FindIndex(i, x => x.Action == KeeperAction.Grounded);
            if (g < 0) return null;
            int u = f.FindIndex(g, x => x.Action == KeeperAction.GettingUp);
            if (u < 0) return null;
            int end = u;
            while (end + 1 < f.Count && f[end + 1].Action == KeeperAction.GettingUp) end++;
            int start = i;
            while (start > 0 && f[start - 1].Action == KeeperAction.Diving) start--;
            return (f[start].Tick, f[end].Tick);
        }),
    };

    /// <summary>
    /// Searches scripted rounds (its preferred scenarios first, then each scenario in turn, seeds 1 upward) for each visible
    /// keeper action and writes the first round that shows it as <c>&lt;name&gt;-&lt;scenario&gt;-s&lt;seed&gt;.json</c>. Prints each file with the tick range of the action.
    /// </summary>
    public static int WriteReplays(string dir, string dataDir, Tuning tuning, int maxSeeds = 300)
    {
        var attacker = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", "striker_a.json")).Value;
        var keeper = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", "the_wall.json")).Value;
        if (attacker == null || keeper == null) { Console.Error.WriteLine("striker_a or the_wall not found"); return 1; }
        Directory.CreateDirectory(dir);
        var played = new Dictionary<(string, ulong), (List<Frame>, Replay)>();
        foreach (var want in Wants)
        {
            bool found = false;
            foreach (var id in (want.Prefer ?? Array.Empty<string>()).Concat(SearchScenarios).Distinct())
            {
                var sc = DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", id + ".json")).Value;
                if (sc == null) continue;
                int seeds = want.Seeds > 0 ? want.Seeds : maxSeeds;
                for (ulong seed = 1; seed <= (ulong)seeds && !found; seed++)
                {
                    if (!played.TryGetValue((id, seed), out var round)) played[(id, seed)] = round = Play(tuning, sc, attacker, keeper, seed);
                    if (want.Find(round.Item1) is not (int from, int to)) continue;
                    string file = Path.Combine(dir, $"{want.Name}-{id}-s{seed}.json");
                    round.Item2.Save(file);
                    Console.WriteLine($"{Path.GetFileName(file)}: ticks {from}-{to} ({want.Shows})");
                    found = true;
                }
                if (found) break;
            }
            if (!found) Console.WriteLine($"not found in {(want.Seeds > 0 ? want.Seeds : maxSeeds)} seeds per scenario: {want.Name} ({want.Shows})");
        }
        return 0;
    }

    static (List<Frame>, Replay) Play(Tuning tuning, Scenario sc, PlayerSheet attacker, PlayerSheet keeper, ulong seed)
    {
        var world = new World(tuning, sc, attacker, keeper, seed);
        var rec = new ReplayRecorder(world, attacker.Id, keeper.Id, sc.Id);
        world.Recorder = rec;
        var script = new ScriptedAttacker();
        world.Begin();
        var buf = new TickIntent[world.PlayerCount];
        var frames = new List<Frame>();
        int cap = (int)((tuning.Round.MaxSeconds + tuning.Round.OutcomeSeconds + 10f) * tuning.TickRate);
        for (int i = 0; i < cap && world.State != RoundState.Report; i++)
        {
            buf[0] = script.Next(world);
            world.Step(buf);
            var k = world.Keeper;
            var r = world.KeeperReport;
            frames.Add(new Frame(world.Tick, k.KeeperAction, k.KeeperDive, r.Save, world.Pitch.GoalLineX - k.Position.X, world.Shots.Count > 0,
                world.Shots.Count > 0 && world.Shots[0].Type == ShotType.Chip, r.WonBall, r.Rounded));
        }
        return (frames, rec.Replay);
    }

    /// <summary>Prints the keeper's state on every tick his action changes, and around the strike, for one scripted round.</summary>
    public static int Trace(Tuning tuning, Scenario sc, PlayerSheet attacker, PlayerSheet keeper, ulong seed)
    {
        var ic = CultureInfo.InvariantCulture;
        var world = new World(tuning, sc, attacker, keeper, seed);
        var script = new ScriptedAttacker();
        world.Begin();
        var buf = new TickIntent[world.PlayerCount];
        KeeperAction? last = null;
        int strikeTick = -1;
        int cap = (int)((tuning.Round.MaxSeconds + tuning.Round.OutcomeSeconds + 10f) * tuning.TickRate);
        for (int i = 0; i < cap && world.State != RoundState.Report; i++)
        {
            buf[0] = script.Next(world);
            world.Step(buf);
            var k = world.Keeper;
            bool strike = world.Events.Any(e => e.Kind == EventKind.Strike);
            if (strike) strikeTick = world.Tick;
            bool near = strikeTick >= 0 && world.Tick - strikeTick <= 60 && (world.Tick - strikeTick) % 3 == 0;
            if (k.KeeperAction != last || strike || near || world.Events.Any(e => e.Kind is EventKind.Save or EventKind.Goal))
            {
                last = k.KeeperAction;
                var b = world.Ball.Position;
                var a = world.Attacker.Position;
                Console.WriteLine(string.Create(ic, $"t{world.Tick,4} {world.State,-7} {k.KeeperAction,-9} {k.KeeperDive,-5} kt={k.KeeperActionTime:F2} " +
                    $"K=({k.Position.X:F2},{k.Position.Z:F2}) v={new System.Numerics.Vector2(k.Velocity.X, k.Velocity.Z).Length():F2} " +
                    $"H=({k.HandPosition.X:F2},{k.HandPosition.Y:F2},{k.HandPosition.Z:F2}) B=({b.X:F2},{b.Y:F2},{b.Z:F2}) |vB|={world.Ball.Velocity.Length():F1} " +
                    $"A=({a.X:F2},{a.Z:F2}) {string.Join(",", world.Events.Select(e => e.Kind.ToString()))}"));
            }
        }
        Console.WriteLine($"outcome {world.Outcome.Label()}");
        Console.WriteLine(world.KeeperReport is { } r ? DataLoaderJson(r) : "");
        return 0;
    }

    static string DataLoaderJson(KeeperReport r) => System.Text.Json.JsonSerializer.Serialize(r, DataLoader.Options);
}
