using Godot;

namespace FootballSim.Game;

/// <summary>
/// Everything that makes one figure look like a particular player: kit colours, skin and hair, and the shirt number.
/// This is the single place to change (or later fill from data/players JSON); the figure only reads it.
/// </summary>
public sealed record FigureKit(
    Color Shirt, Color Shorts, Color Socks, Color Boots, Color Gloves, Color Skin, Color Hair, int Number)
{
    /// <summary>Red shirt, white shorts, red socks, black boots, number 10.</summary>
    public static FigureKit Attacker { get; } = new(
        Shirt: new Color(0.85f, 0.1f, 0.1f), Shorts: new Color(0.95f, 0.95f, 0.95f), Socks: new Color(0.85f, 0.1f, 0.1f),
        Boots: new Color(0.07f, 0.07f, 0.08f), Gloves: new Color(0.9f, 0.9f, 0.9f),
        Skin: new Color(0.80f, 0.60f, 0.46f), Hair: new Color(0.13f, 0.09f, 0.06f), Number: 10);

    /// <summary>Yellow long-sleeved shirt, black shorts and socks, cyan gloves, number 1.</summary>
    public static FigureKit Keeper { get; } = new(
        Shirt: new Color(0.95f, 0.85f, 0.1f), Shorts: new Color(0.12f, 0.12f, 0.12f), Socks: new Color(0.12f, 0.12f, 0.12f),
        Boots: new Color(0.95f, 0.45f, 0.1f), Gloves: new Color(0.1f, 0.9f, 0.9f),
        Skin: new Color(0.62f, 0.44f, 0.32f), Hair: new Color(0.06f, 0.05f, 0.04f), Number: 1);

    /// <summary>Number colour that reads against the shirt.</summary>
    public Color NumberColor => Shirt.Luminance > 0.5f ? new Color(0.05f, 0.05f, 0.08f) : new Color(0.97f, 0.97f, 0.97f);
}
