using System.Numerics;
using FootballSim.Core;
using static FootballSim.Core.Tests.TestData;

namespace FootballSim.Core.Tests;

/// <summary>Worlds and steps shared by the lock-on tests.</summary>
static class LockOnLab
{
    /// <summary>An open lab pitch: the attacker at x = 2 facing +X, the keeper on his line 50 m away, nothing touched yet.</summary>
    public static World Open(ulong seed = 1, Tuning? tuning = null)
    {
        var sc = new Scenario
        {
            Id = "lab",
            Pitch = new PitchSpec { Length = 52.5f },
            Ball = new StartSpot { X = 2.5f, Z = 0f },
            Attacker = new StartSpot { X = 2.0f, Z = 0f },
        };
        var w = new World(tuning ?? new Tuning(), sc, Sheet(), Sheet(50), seed);
        w.Begin();
        return w;
    }

    /// <summary>
    /// The arena with a loose ball from the start: the ball at (bx, bz) rolling at <paramref name="speed"/> along
    /// <paramref name="headingDeg"/>, the attacker at (ax, az); the keeper holds his line; a defender when a spot is given.
    /// </summary>
    public static World Arena(float bx, float bz, float speed, float headingDeg, float ax, float az, float facingDeg = 0f,
        (float X, float Z)? defender = null, ulong seed = 1, Tuning? tuning = null)
    {
        var sc = new Scenario
        {
            Id = "lab_arena", Pitch = Scenario("loose_parry").Pitch,
            Ball = new StartSpot { X = bx, Z = bz }, BallSpeed = speed, BallHeadingDeg = headingDeg, BallLoose = true,
            Attacker = new StartSpot { X = ax, Z = az, FacingDeg = facingDeg },
            KeeperOnLine = true, KeeperHoldLine = true, KeeperHoldLineThroughDribble = true,
            Defenders = defender != null ? 1 : 0,
            Defender = defender is { } d ? new StartSpot { X = d.X, Z = d.Z } : new StartSpot(),
            ClockLimit = 20f,
        };
        var w = new World(tuning ?? new Tuning(), sc, Player("striker_a"), Player("the_wall"), seed, defender != null ? Player("centre_back") : null);
        w.Begin();
        return w;
    }

    /// <summary>Puts the loose ball at (dx, dz) from the attacker, rolling at (vx, vz), as a ball that has been played.</summary>
    public static void Loose(World w, float dx, float dz, float vx = 0f, float vz = 0f)
    {
        w.BallPlayed = true;
        var a = w.Attacker.Position;
        w.Ball.Position = new Vector3(a.X + dx, w.Ball.Radius, a.Z + dz);
        w.Ball.Velocity = new Vector3(vx, 0f, vz);
        w.Ball.Spin = w.Ball.RollingSpin(w.Ball.Velocity);
    }

    /// <summary>Steps with the attacker's intent until he gains the ball or the ticks run out; the tick count, or -1.</summary>
    public static int TicksToRegain(World w, TickIntent intent, int max, List<RegainMode>? modes = null)
    {
        var buf = new TickIntent[w.PlayerCount];
        for (int i = 1; i <= max; i++)
        {
            buf[0] = intent;
            w.Step(buf);
            modes?.Add(w.Attacker.Regain);
            if (w.BallController == w.Attacker) return i;
        }
        return -1;
    }

    public static bool Has(World w, EventKind kind, int team = 0) => w.Events.Any(e => e.Kind == kind && e.Team == team);

    /// <summary>Steps with a per-tick intent until <paramref name="stop"/> says so or the ticks run out; true if it stopped.</summary>
    public static bool Run(World w, Func<int, TickIntent> intent, int max, Func<World, bool> stop)
    {
        var buf = new TickIntent[w.PlayerCount];
        for (int i = 0; i < max && w.State != RoundState.Report; i++)
        {
            buf[0] = intent(i);
            w.Step(buf);
            if (stop(w)) return true;
        }
        return false;
    }
}
