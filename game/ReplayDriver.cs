using System.IO;
using FootballSim.Core;
using Godot;

namespace FootballSim.Game;

/// <summary>Feeds a recorded replay's intents to the world in place of the gamepad.</summary>
public sealed class ReplayDriver
{
    public Replay Replay { get; }

    ReplayDriver(Replay r) => Replay = r;

    /// <summary>Loads a replay file; the path may be relative to the launch directory, the repo root or the game folder.</summary>
    public static ReplayDriver? Load(string path)
    {
        string game = ProjectSettings.GlobalizePath("res://");
        foreach (var candidate in new[] { path, Path.Combine(game, "..", path), Path.Combine(game, path) })
        {
            if (!File.Exists(candidate)) continue;
            var r = Replay.Load(candidate, out var err);
            if (r == null) { GD.PrintErr(err); return null; }
            return new ReplayDriver(r);
        }
        GD.PrintErr($"Replay file not found: {path}");
        return null;
    }

    /// <summary>The attacker's recorded intent for a tick (ticks counted from Begin); idle once the recording ends.</summary>
    public TickIntent IntentAt(int tick)
    {
        int per = System.Math.Max(1, Replay.PlayersPerTick);
        return tick < Replay.TickCount ? Replay.Intents[tick * per].ToIntent() : default;
    }
}
