using System.Collections.Generic;
using System;
using System.Globalization;
using FootballSim.Core;

namespace FootballSim.Game;

/// <summary>Plain-words report card text built from a RoundReport. No simulation rules here, only formatting.</summary>
public static class ReportText
{
    public const string Footer = "A/Enter next  ·  Y/R retry  ·  View/F flag";

    public static string Title(RoundReport r) => r.Outcome switch
    {
        Outcome.Goal => "GOAL",
        Outcome.Held => "SAVED (held)",
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
        lines.Add(Footer);
        return lines.ToArray();
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
