namespace FootballSim.Core;

/// <summary>How a scripted round's first shot ended.</summary>
public enum ShotEnd { NoShot, Scored, Held, Parried, Rebound, Wide, Over, Out, TimeUp }

/// <summary>Runs scripted rounds, for the batch runner and the calibration tests.</summary>
public static class RoundRunner
{
    /// <summary>The result of one scripted round.</summary>
    public readonly record struct Result(ShotEnd End, ShotResult? Shot, KeeperReport Keeper, Outcome Outcome);

    /// <summary>
    /// Plays one round with a <see cref="ScriptedAttacker"/> until the first shot is resolved: a goal, a save, a miss, a
    /// rebound back into play, or the clock. Stops early, so it does not wait for the report. In the arena a miss does not end
    /// the round, so a first shot that reaches a board (or the ceiling) before the keeper or the goal is scored here as Wide,
    /// or as Over when it met the goal-end wall above the bar or the ceiling.
    /// </summary>
    public static Result RunFirstShot(Tuning tuning, Scenario scenario, PlayerSheet attacker, PlayerSheet keeper, ulong seed,
        ScriptedAttacker? script = null)
    {
        var world = new World(tuning, scenario, attacker, keeper, seed);
        script ??= new ScriptedAttacker();
        world.Begin();
        var buf = new TickIntent[world.PlayerCount];
        int cap = (int)((tuning.Round.MaxSeconds + 10f) * tuning.TickRate);
        bool shot = false;
        ShotEnd? missed = null;
        for (int i = 0; i < cap; i++)
        {
            buf[0] = script.Next(world);
            world.Step(buf);
            if (world.Shots.Count > 0) shot = true;
            if (world.State is RoundState.Outcome or RoundState.Report) break;
            if (shot && BoardMiss(world) is ShotEnd m) { missed = m; break; } // off the boards: a miss
            if (shot && world.State == RoundState.Live) break; // rebound or parry: the shot is over
        }
        var kr = world.KeeperReport;
        var s = world.Shots.Count > 0 ? world.Shots[0] : null;
        ShotEnd end;
        if (!shot) end = ShotEnd.NoShot; // the round ended without a strike (dribbled in, claimed, or timed out)
        else if (missed is ShotEnd me) end = me;
        else if (world.State is RoundState.Outcome or RoundState.Report)
        {
            end = world.Outcome switch
            {
                Outcome.Goal => ShotEnd.Scored,
                Outcome.Held => ShotEnd.Held,
                Outcome.Wide => ShotEnd.Wide,
                Outcome.Over => ShotEnd.Over,
                Outcome.Out => ShotEnd.Out,
                Outcome.TimeUp => ShotEnd.TimeUp,
                _ => ShotEnd.NoShot,
            };
        }
        else end = kr.Action == KeeperResult.Parried ? ShotEnd.Parried : ShotEnd.Rebound;
        return new Result(end, s, kr, world.Outcome);
    }

    /// <summary>Wide or Over if the ball met a board this tick, else null.</summary>
    static ShotEnd? BoardMiss(World w)
    {
        foreach (var e in w.Events)
            if (e.Kind == EventKind.BoardHit)
                return w.Pitch.IsOverContact(e.Position, w.Ball.Radius) ? ShotEnd.Over : ShotEnd.Wide;
        return null;
    }

    /// <summary>
    /// Plays a whole round with the scripted attacker until the report (a goal, the keeper holding the ball, or the clock),
    /// optionally recording a replay.
    /// </summary>
    public static RoundReport RunToReport(Tuning tuning, Scenario scenario, PlayerSheet attacker, PlayerSheet keeper, ulong seed,
        out Replay replay, ScriptedAttacker? script = null)
    {
        var world = new World(tuning, scenario, attacker, keeper, seed);
        var rec = new ReplayRecorder(world, attacker.Id, keeper.Id, scenario.Id);
        world.Recorder = rec;
        script ??= new ScriptedAttacker();
        world.Begin();
        var buf = new TickIntent[world.PlayerCount];
        int cap = (int)((tuning.Round.MaxSeconds + tuning.Round.OutcomeSeconds + 10f) * tuning.TickRate);
        for (int i = 0; i < cap && world.State != RoundState.Report; i++)
        {
            buf[0] = script.Next(world);
            world.Step(buf);
        }
        replay = rec.Replay;
        return world.Report ?? throw new InvalidOperationException("Round did not reach a report");
    }
}
