using System.Numerics;
using FootballSim.Core;
using Xunit;
using static FootballSim.Core.Tests.TestData;
using static FootballSim.Core.Tests.ControlTests;

namespace FootballSim.Core.Tests;

/// <summary>Offside: the line, the positions taken at a strike, the offence (see Offside).</summary>
public class OffsideTests
{
    static PlayerState Man(int team, int index, float x, float z = 0f, PlayerRole role = PlayerRole.Outfield)
        => new(team, index, role, Sheet()) { Position = new Vector3(x, 0f, z) };

    static bool Has(World w, EventKind kind) => w.Events.Any(e => e.Kind == kind);

    [Fact]
    public void TheLineIsTheSecondLastOpponent()
    {
        var opponents = new List<PlayerState> { Man(1, 0, 29.5f, 0f, PlayerRole.Keeper), Man(1, 1, 16f) };
        Assert.Equal(16f, Offside.Line(opponents, new Vector3(8f, 0.11f, 0f)), 3);
        // The keeper out beyond his defender: the second-last opponent is then the keeper.
        opponents = new List<PlayerState> { Man(1, 0, 14f, 0f, PlayerRole.Keeper), Man(1, 1, 18f) };
        Assert.Equal(14f, Offside.Line(opponents, new Vector3(8f, 0.11f, 0f)), 3);
        // Level is onside; a body centre past the line is beyond it.
        Assert.False(Offside.Beyond(Man(0, 1, 14f), 14f));
        Assert.True(Offside.Beyond(Man(0, 1, 14.05f), 14f));
    }

    [Fact]
    public void BallIsLine()
    {
        // The carrier past the defender: the line is the ball, and a square pass is onside.
        var opponents = new List<PlayerState> { Man(1, 0, 29.5f, 0f, PlayerRole.Keeper), Man(1, 1, 16f) };
        Assert.Equal(20f, Offside.Line(opponents, new Vector3(20f, 0.11f, 0f)), 3);
        var w = WithBall(20f, -3f, 19.8f, 4f, defender: (16f, 0f), offside: true);
        w.DefenderBotEnabled = false;
        Assert.True(w.OffsideOn);
        Assert.True(w.OffsideLine > 19.9f);
        Assert.True(StrikePass(w));
        Assert.NotEqual(Outcome.Offside, w.Outcome);
        Assert.False(w.Teams[0][1].OffsideFlag);
        Assert.Equal(1, w.ControlledIndex);
        // A man beyond the ball there is offside.
        var v = WithBall(20f, -3f, 22f, 4f, defender: (16f, 0f), offside: true);
        v.DefenderBotEnabled = false;
        Assert.True(StrikePass(v));
        Assert.Equal(Outcome.Offside, v.Outcome);
    }

    [Fact]
    public void ANamedReceiverBeyondTheLineIsOffsideOnTheStrikeTick()
    {
        var w = WithBall(8f, -3f, 17f, 4f, defender: (16f, 0f), offside: true);
        w.DefenderBotEnabled = false;
        Assert.True(StrikePass(w, holdTicks: 12));
        Assert.Equal(Outcome.Offside, w.Outcome);
        Assert.Equal(RoundState.Outcome, w.State);
        var e = w.Events.Single(x => x.Kind == EventKind.Offside);
        Assert.Equal((0, 1), (e.Team, e.PlayerIndex));
        Assert.Equal(1, w.Offsides);
        // No jump: the pad stays with the passer.
        Assert.False(Has(w, EventKind.ControlChanged));
        Assert.Equal(0, w.ControlledIndex);
        // Level with the defender he is onside.
        var level = WithBall(8f, -3f, 16f, 4f, defender: (16f, 0f), offside: true);
        level.DefenderBotEnabled = false;
        Assert.True(StrikePass(level, holdTicks: 12));
        Assert.Equal(Outcome.None, level.Outcome);
        Assert.Equal(1, level.ControlledIndex);
    }

    [Fact]
    public void WithOffsideOffNobodyIsFlagged()
    {
        var w = WithBall(8f, -3f, 17f, 4f, defender: (16f, 0f), offside: false);
        w.DefenderBotEnabled = false;
        Assert.False(w.OffsideOn);
        Assert.True(StrikePass(w, holdTicks: 12));
        Assert.Equal(Outcome.None, w.Outcome);
        Assert.False(w.Teams[0][1].OffsideFlag);
        Assert.Equal(1, w.ControlledIndex);
    }

    [Fact]
    public void AShotFlagsTheManBeyondTheLineSoHeCannotTakeTheRebound()
    {
        // The team-mate stands beyond the defender when the carrier shoots: he is flagged, the claim on the loose ball skips
        // him, control never jumps to him, and a touch by him would be the offence.
        var w = WithBall(14f, -2f, 22f, 3f, defender: (18f, 0f), offside: true);
        w.DefenderBotEnabled = false;
        var buf = new TickIntent[w.PlayerCount];
        var mate = w.Teams[0][1];
        bool struck = false;
        for (int i = 0; i < 240 && !struck; i++)
        {
            buf[0] = new TickIntent { MoveX = 1f, ShootHeld = i >= 2 && i < 20 };
            w.Step(buf);
            struck = Has(w, EventKind.Strike);
        }
        Assert.True(struck);
        Assert.True(mate.OffsideFlag);
        Assert.False(w.Teams[0][0].OffsideFlag);
        buf[0] = default;
        for (int i = 0; i < 600 && w.State is RoundState.Live or RoundState.Strike or RoundState.Flight; i++)
        {
            w.Step(buf);
            Assert.NotSame(mate, w.Claims[0].Player);
            Assert.Equal(0, w.ControlledIndex);
        }
    }

    [Fact]
    public void AFlaggedPlayersTouchIsTheOffenceAndAnotherPlayersTouchClearsTheFlags()
    {
        // A pass into space with the team-mate beyond the line (not its named receiver: he is nearer than the shortest pass).
        var w = Pair(15f, 0f, 14.6f, 0f, 20f, 0.5f, loose: false, defender: (17f, 6f), offside: true);
        w.DefenderBotEnabled = false;
        var mate = w.Teams[0][1];
        mate.OffsideFlag = true;
        // The carrier's own touch clears it.
        var buf = new TickIntent[w.PlayerCount];
        for (int i = 0; i < 240 && !w.Events.Any(e => e.Kind == EventKind.Touch && e.Team == 0); i++)
        {
            buf[0] = new TickIntent { MoveX = 1f };
            w.Step(buf);
        }
        Assert.False(mate.OffsideFlag);
        Assert.Equal(Outcome.None, w.Outcome);

        // Flagged again with the ball rolling to him and nobody else near: the claim skips him; were he to touch it, offside.
        var v = Pair(18f, 0.5f, 6f, -6f, 20f, 0.5f, speed: 3f, defender: (12f, 8f), offside: true);
        v.DefenderBotEnabled = false;
        v.Teams[0][1].OffsideFlag = true;
        var vb = new TickIntent[v.PlayerCount];
        for (int i = 0; i < 120; i++)
        {
            v.Step(vb);
            Assert.NotSame(v.Teams[0][1], v.Claims[0].Player);
        }
        Assert.Equal(0, v.ControlledIndex);
    }
}
