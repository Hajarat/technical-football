using System.Numerics;

namespace FootballSim.Core;

/// <summary>
/// Offside, for a scenario that turns it on (<c>"offside": true</c>). The line is the second-last opponent, or the ball when it
/// is further up the pitch: x = max(second-last opponent's x, ball x). With the keeper and one defender the second-last opponent
/// is whichever of them is further from their goal line (normally the defender). A player's position is his body centre, and
/// level is onside. Positions are judged at each strike (a pass or a shot) by the attacking team: the team-mates beyond the line
/// then are flagged until a team-mate who was not flagged next touches the ball. A flagged player named as the pass's receiver
/// is the offence on the strike tick; otherwise it is the offence when a flagged player touches the ball. The claim on a loose
/// ball skips flagged players, so control never jumps to one.
/// </summary>
public static class Offside
{
    /// <summary>The offside line's x for attackers playing toward +X against <paramref name="opponents"/>, with the ball at <paramref name="ball"/>.</summary>
    public static float Line(IReadOnlyList<PlayerState> opponents, Vector3 ball)
    {
        // The second-last opponent: the second-nearest to their own goal line (the greatest x but one).
        float last = float.NegativeInfinity, second = float.NegativeInfinity;
        foreach (var o in opponents)
        {
            float x = o.Position.X;
            if (x > last) { second = last; last = x; }
            else if (x > second) second = x;
        }
        return MathF.Max(second, ball.X);
    }

    /// <summary>True when <paramref name="m"/> is beyond the line (level is onside).</summary>
    public static bool Beyond(PlayerState m, float line) => m.Position.X > line + Level;

    /// <summary>A body centre within this of the line is level with it, metres.</summary>
    public const float Level = 1e-3f;
}

public sealed partial class World
{
    /// <summary>True when the scenario calls offside (and there is a team-mate to be offside).</summary>
    public bool OffsideOn => Scenario.Offside && HasMates;
    /// <summary>The offside line's x now (see <see cref="Offside.Line"/>).</summary>
    public float OffsideLine => Offside.Line(Teams[1], Ball.Position);

    partial void BeyondLine(PlayerState m, ref bool beyond) => beyond = OffsideOn && Offside.Beyond(m, OffsideLine);

    /// <summary>
    /// The positions at a strike by <paramref name="striker"/>: every team-mate beyond the line is flagged (the striker never
    /// is). A flagged named receiver is the offence at once.
    /// </summary>
    partial void OffsideAtStrike(PlayerState striker, PlayerState? receiver, ref bool offence)
    {
        if (!OffsideOn || striker.Team != 0) return;
        float line = OffsideLine;
        foreach (var m in Teams[0]) m.OffsideFlag = m != striker && m.Role == PlayerRole.Outfield && Offside.Beyond(m, line);
        if (receiver is { OffsideFlag: true }) { CallOffside(receiver); offence = true; }
    }

    /// <summary>
    /// A touch (or a first-time strike) by <paramref name="p"/>: the offence if he is flagged (the round ends, true is returned);
    /// otherwise, by an attacker, it clears his team's flags.
    /// </summary>
    bool OffsideTouch(PlayerState p)
    {
        if (p.Team != 0) return false;
        if (p.OffsideFlag) { CallOffside(p); return true; }
        foreach (var m in Teams[0]) m.OffsideFlag = false;
        return false;
    }

    void CallOffside(PlayerState p)
    {
        _events.Add(new SimEvent(EventKind.Offside, p.Position, 0f, Team: p.Team, PlayerIndex: p.Index));
        _offsides++;
        EndRound(Outcome.Offside);
    }

    int _offsides;
    /// <summary>Offside offences this round (0 or 1: the offence ends it).</summary>
    public int Offsides => _offsides;
}
