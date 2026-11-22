using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>Finds the repo's data/ folder and loads shipped files for tests.</summary>
static class TestData
{
    public static readonly string DataDir = FindData();

    static string FindData()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var d = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(d, "tuning", "default.json"))) return d;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("data/ not found above " + AppContext.BaseDirectory);
    }

    public static PlayerSheet Player(string id) => Ok(DataLoader.LoadPlayer(Path.Combine(DataDir, "players", id + ".json")));
    public static Scenario Scenario(string id) => Ok(DataLoader.LoadScenario(Path.Combine(DataDir, "scenarios", id + ".json")));
    public static Tuning Tuning() => new();

    static T Ok<T>(LoadResult<T> r) where T : class
    {
        Assert.True(r.Ok, r.Error);
        return r.Value!;
    }

    /// <summary>A world in the Live state on a scenario, with the shipped default players.</summary>
    public static World Live(string scenario = "penalty", string attacker = "striker_a", string keeper = "the_wall", ulong seed = 1, Tuning? tuning = null)
    {
        var w = new World(tuning ?? Tuning(), Scenario(scenario), Player(attacker), Player(keeper), seed);
        w.Begin();
        return w;
    }

    /// <summary>Plain sheet with every rating set to <paramref name="rating"/>.</summary>
    public static PlayerSheet Sheet(int rating = 50, float height = 1.80f, float weight = 75f)
    {
        var s = new PlayerSheet { Id = "t", Name = "t", HeightM = height, WeightKg = weight };
        foreach (var n in PlayerSheet.RatingNames) s.SetRating(n, n == "weak_foot" ? 50 : rating);
        return s;
    }

    public static PlayerState Outfield(PlayerSheet? sheet = null, float x = 0, float z = 0, float facing = 0)
    {
        var p = new PlayerState(0, 0, PlayerRole.Outfield, sheet ?? Sheet())
        {
            Position = new System.Numerics.Vector3(x, 0, z),
            Facing = facing,
        };
        p.TurnRefHeading = facing;
        return p;
    }

    /// <summary>Plays a scripted round tick by tick until the report (or a cap), calling <paramref name="onTick"/> after each step.</summary>
    public static void Play(World w, ScriptedAttacker script, int maxTicks = 20000, Func<World, bool>? stopWhen = null)
    {
        var buf = new TickIntent[w.PlayerCount];
        for (int i = 0; i < maxTicks && w.State != RoundState.Report; i++)
        {
            buf[0] = script.Next(w);
            w.Step(buf);
            if (stopWhen != null && stopWhen(w)) return;
        }
    }

    /// <summary>Scoring rate of scripted first shots. Rounds run in parallel; each depends only on its own seed, so the count is deterministic.</summary>
    public static (int Scored, int N) Rate(Scenario sc, PlayerSheet att, PlayerSheet kee, int n, ulong seed, Tuning? t = null)
    {
        t ??= Tuning();
        int scored = 0;
        Parallel.For(0, n, i =>
        {
            if (RoundRunner.RunFirstShot(t, sc, att, kee, seed + (ulong)i).End == ShotEnd.Scored) Interlocked.Increment(ref scored);
        });
        return (scored, n);
    }
}
