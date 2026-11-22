using System.Numerics;
using FootballSim.Core;

/// <summary>
/// <c>--write-shot-replays &lt;dir&gt;</c>: one replay per kind of kick and ball-out angle the game's shot clips are picked by, for
/// its screenshots and the shot-clip check (game/tests/shot_angles.sh). Each starts on the edge of the box, dribbles at a jog
/// along a heading, holds shoot 0.2 s with the stick at the goal's centre and lets go: the heading against the goal gives the
/// angle (running across the goal a shot 90 degrees across the body, running away from it one struck straight behind). A case is
/// written only when the strike came off as meant (foot, surface and angle); the dribble is tried a little longer until it does.
/// </summary>
static class ShotReplays
{
    /// <param name="Heading">The dribble's direction, degrees (0 at the goal, positive toward +Z, his right when he faces the goal).</param>
    /// <param name="Lo">The ball-out angle wanted at the strike, degrees toward the striking foot's own side, from ... to.</param>
    sealed record Case(string File, float Heading, bool Finesse, bool Chip, bool StrongFoot, FootSurface Surface, float Lo, float Hi);

    static readonly Case[] Cases =
    {
        new("shot-power-0.json", 0f, false, false, true, FootSurface.Laces, -22.5f, 22.5f),
        new("shot-power-across90.json", 90f, false, false, true, FootSurface.Laces, -112.5f, -67.5f),
        new("shot-power-across180.json", 180f, false, false, true, FootSurface.Laces, -180f, -157.5f),
        new("shot-finesse-across45.json", 45f, true, false, true, FootSurface.Inside, -67.5f, -22.5f),
        new("shot-chip-0.json", 0f, false, true, true, FootSurface.Inside, -22.5f, 22.5f),
        new("shot-outside-open45.json", -45f, false, false, true, FootSurface.Outside, 22.5f, 67.5f),
        // 90 degrees to his strong side is beyond that foot: the weak foot strikes it, across its own body
        new("shot-weak-foot-open90.json", -90f, false, false, false, FootSurface.Laces, -112.5f, -67.5f),
    };

    public static int Write(string dir, string dataDir, Tuning tuning)
    {
        var sc = DataLoader.LoadScenario(Path.Combine(dataDir, "scenarios", "edge_of_box.json")).Value;
        var attacker = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", "striker_a.json")).Value;
        var keeper = DataLoader.LoadPlayer(Path.Combine(dataDir, "players", "the_wall.json")).Value;
        if (sc == null || attacker == null || keeper == null) { Console.Error.WriteLine("edge_of_box, striker_a or the_wall not found"); return 1; }
        Directory.CreateDirectory(dir);
        int failed = 0;
        foreach (var c in Cases)
        {
            bool done = false;
            // the press about 1.2 s into the dribble; a little later while the strike does not come off as meant
            for (int delay = 0; delay <= 72 && !done; delay += 4)
                done = WriteOne(Path.Combine(dir, c.File), c, (int)(1.2f * tuning.TickRate) + delay, sc, attacker, keeper, tuning);
            if (!done) { failed++; Console.WriteLine($"NOT written {c.File}: no dribble length gave the strike meant"); }
        }
        return failed == 0 ? 0 : 1;
    }

    static bool WriteOne(string path, Case c, int press, Scenario sc, PlayerSheet attacker, PlayerSheet keeper, Tuning tuning)
    {
        var w = new World(tuning, sc, attacker, keeper, 1);
        var rec = new ReplayRecorder(w, attacker.Id, keeper.Id, sc.Id);
        w.Recorder = rec;
        w.Begin();
        var p = w.Attacker;
        var buf = new TickIntent[w.PlayerCount];
        int hold = (int)(0.2f * tuning.TickRate);
        float a = c.Heading * MathF.PI / 180f;
        ShotResult? shot = null;
        float angle = float.NaN;
        StrikeDecision decision = default;
        int after = 0;
        for (int tick = 1; tick <= press + 6 * tuning.TickRate && w.State != RoundState.Report && after < tuning.TickRate; tick++)
        {
            var it = new TickIntent { Finesse = c.Finesse, Chip = c.Chip };
            var toGoal = new Vector2(w.Pitch.GoalLineX - w.Ball.Position.X, -w.Ball.Position.Z);
            toGoal = toGoal.LengthSquared() > 1e-6f ? Vector2.Normalize(toGoal) : new Vector2(1f, 0f);
            if (tick <= press) { it.MoveX = MathF.Cos(a); it.MoveZ = MathF.Sin(a); }
            else if (tick <= press + hold + 1) { it.MoveX = toGoal.X; it.MoveZ = toGoal.Y; it.ShootHeld = tick <= press + hold; }
            float facing = p.Facing;
            buf[0] = it;
            w.Step(buf);
            if (shot == null && w.Shots.Count > 0)
            {
                shot = w.Shots[0];
                angle = ShotModel.OpenAngleDeg(shot.Foot, attacker.PreferredFoot, facing, shot.IntendedYaw);
                decision = w.StrikeGeometry;            // fixed from the release
            }
            if (shot != null) after++;
        }
        if (shot == null) return false;
        bool strong = shot.Foot == attacker.PreferredFoot;
        if (strong != c.StrongFoot || shot.Surface != c.Surface || angle < c.Lo || angle > c.Hi) return false;
        rec.Replay.Save(path);
        Console.WriteLine($"wrote {path} ({rec.Replay.TickCount} ticks): pressed at tick {press + 1}, struck at {shot.Tick} with the {(strong ? "strong" : "weak")} foot's {shot.Surface.ToString().ToLowerInvariant()}, " +
                          $"{shot.Type}, ball-out angle {angle:F0} degrees, body angle {shot.BodyAngleDeg:F0}, rule {decision.Rule.ToString().ToLowerInvariant()}, far_post {(decision.Rule == StrikeRule.FarPost ? "yes" : "no")}, run_speed {shot.SpeedFactor("run_speed"):F3}, speed {shot.Speed:F1} m/s");
        return true;
    }
}
