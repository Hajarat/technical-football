using System.Collections.Generic;
using System;
using System.Globalization;
using FootballSim.Core;

namespace FootballSim.Game;

/// <summary>Plain-words report card text built from a RoundReport. No simulation rules here, only formatting.</summary>
public static class ReportText
{
    public const string Footer = "A/Enter next  ·  Y/Backspace retry  ·  View/M capture";

    public static string Title(RoundReport r) => r.Outcome switch
    {
        Outcome.Goal => "GOAL",
        Outcome.Held => "SAVED (held)",
        Outcome.Dispossessed => "DISPOSSESSED",
        Outcome.Foul => "FOUL",
        _ when r.Keeper.Action == KeeperResult.Parried => "SAVED (parried, ball was live)",
        Outcome.Wide => "WIDE",
        Outcome.Over => "OVER",
        Outcome.Out => "OUT OF PLAY",
        Outcome.TimeUp => "TIME UP",
        Outcome.Claimed => "CLAIMED",
        _ => r.Outcome.ToString().ToUpperInvariant(),
    };

    public static string[] Lines(RoundReport r)
    {
        var lines = new List<string>();
        if (r.Shots.Count == 0)
        {
            lines.Add($"No shot taken. Round lasted {F(r.Clock, 1)} s.");
        }
        else
        {
            var s = r.Shots[^1];
            lines.Add($"Aimed: {Spot(s.Aimed.X, s.Aimed.Y)}");
            lines.Add(float.IsNaN(s.Landed.X) ? "Went: never reached the goal line" : $"Went: {Spot(s.Landed.X, s.Landed.Y)}");
            lines.Add($"Shot {F(s.Speed * 3.6f, 0)} km/h, {Spread(s)}");
            var k = r.Keeper;
            lines.Add(float.IsNaN(k.ReactionS)
                ? "Keeper: no dive needed"
                : $"Keeper: reacted in {F(k.ReactionS, 2)} s, needed {F(k.NeededM, 1)} m, reach {F(k.ReachM, 1)} m, margin {F(k.MarginM, 1)} m");
        }
        if (r.Defender is { } d) lines.Add(DefenderLine(r, d));
        lines.Add(Footer);
        return lines.ToArray();
    }

    /// <summary>What the defender did, in a round with one.</summary>
    static string DefenderLine(RoundReport r, DefenderReport d)
    {
        var parts = new List<string>();
        if (r.Outcome == Outcome.Foul) parts.Add("fouled the attacker");
        else if (d.Collected)
            parts.Add(d.WonBy switch
            {
                TackleKind.Auto => "took the ball off his feet and kept it",
                TackleKind.Lunge => "won the ball with a standing tackle and kept it",
                TackleKind.Slide => "won the ball with a slide tackle and kept it",
                _ => "picked up the loose ball",
            });
        int tackles = d.AutoWon + d.LungesWon + d.SlidesWon;
        if (!d.Collected && tackles > 0) parts.Add(tackles == 1 ? "won the ball once but lost it again" : $"won the ball {tackles} times but lost it again");
        int missed = d.LungesTried - d.LungesWon + d.SlidesTried - d.SlidesWon;
        if (missed > 0) parts.Add(missed == 1 ? "missed one tackle" : $"missed {missed} tackles");
        if (d.Blocks > 0) parts.Add(d.Blocks == 1 ? "blocked a shot" : $"blocked {d.Blocks} shots");
        if (d.BargesWon > 0) parts.Add("barged the attacker off the ball");
        return parts.Count == 0 ? "Defender: never got to the ball" : "Defender: " + string.Join(", ", parts);
    }

    /// <summary>Z is measured from the goal centre. The camera stands on the +Z side, so +Z is the near post.</summary>
    static string Spot(float z, float height)
    {
        string side = MathF.Abs(z) < 0.05f ? "centre" : z > 0 ? "near post side" : "far post side";
        return MathF.Abs(z) < 0.05f ? $"centre, {F(height, 1)} m high" : $"{F(MathF.Abs(z), 1)} m {side}, {F(height, 1)} m high";
    }

    static string Spread(ShotResult s)
    {
        float m = 1f;
        foreach (var f in s.Factors) m *= f.Value;
        var text = $"spread {F(s.SigmaH, 1)}° = base {F(s.SigmaH / m, 1)}°";
        foreach (var f in s.Factors)
            if (MathF.Abs(f.Value - 1f) >= 0.005f) text += $" × {Pretty(f.Name)} {F(f.Value, 2)}";
        return text;
    }

    static string Pretty(string factor) => factor switch { "speed" => "run", "foot" => "weak foot", _ => factor };
    static string F(float v, int digits) => v.ToString("F" + digits, CultureInfo.InvariantCulture);
}
