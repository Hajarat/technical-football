using Godot;

namespace FootballSim.Game;

/// <summary>Goalkeeper: the same jointed figure in contrasting kit with larger gloves, in every state of the keeper contract (stance, shuffle, rush, dives, smother, holding, lying, getting up); the poses are in PlayerFigure.Keeper.cs.</summary>
public partial class KeeperView : Node3D
{
    /// <summary>How long the get-up takes: the figure rises from lying to the set stance over this many seconds (the core's default get-up time).</summary>
    public const float GetUpSeconds = 1.0f;

    readonly PlayerFigure _figure = new() { Name = "Figure", IsKeeper = true };

    public PlayerFigure Figure => _figure;

    public override void _Ready()
    {
        _figure.Kit = FigureKit.Keeper;
        AddChild(_figure);
    }

    public void SetColor(Color color) => _figure.SetColors(color, new Color(0.12f, 0.12f, 0.12f), new Color(0.12f, 0.12f, 0.12f));

    /// <summary>Plain pose: feet position, facing and an optional hand target (which makes the keeper dive toward it). Call from _PhysicsProcess only.</summary>
    public void Apply(Vector3 position, float facing, Vector3? handPosition = null)
    {
        var f = FigureFrame.At(position, facing);
        if (handPosition is Vector3 h) { f.KeeperAction = FootballSim.Core.KeeperAction.Diving; f.KeeperDive = FootballSim.Core.KeeperDive.Side; f.HandPosition = h; }
        Apply(f);
    }

    /// <summary>Full per-tick input. Call from _PhysicsProcess only.</summary>
    public void Apply(in FigureFrame frame) => _figure.Update(frame);

    public void Teleport(Vector3 position, float facing) => Teleport(FigureFrame.At(position, facing));

    public void Teleport(in FigureFrame frame) => _figure.Teleport(frame);
}
