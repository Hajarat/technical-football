using System.Numerics;

namespace FootballSim.Core;

// World coordinates, shared by the core and the game layer (metres, seconds):
//   X  along the pitch toward the attacked goal. Halfway line X = 0, goal line X = 52.5.
//   Y  up. The ground is Y = 0.
//   Z  across the pitch. Z = 0 is the centre; the touchlines are Z = -34 and Z = +34.
//      The broadcast camera stands beyond the +Z touchline, so screen-right is +X and screen-up is -Z.
// The goal mouth is centred on (52.5, 0, 0): posts at Z = -3.66 and Z = +3.66, crossbar at Y = 2.44.
// Godot uses the same axes and handedness, so positions copy across component by component.

/// <summary>One player's input for one tick. A gamepad fills it now; computer-controlled players will later.</summary>
public struct TickIntent
{
    /// <summary>Desired movement along world X, -1..1. Positive is toward the attacked goal.</summary>
    public float MoveX;
    /// <summary>Desired movement along world Z, -1..1. Negative is toward the far touchline (screen-up).</summary>
    public float MoveZ;
    /// <summary>0..1, analog sprint.</summary>
    public float Sprint;
    public bool CloseControl;
    /// <summary>True while the shoot button is held. Power charges while held; release strikes.</summary>
    public bool ShootHeld;
    public bool Finesse;
    public bool Chip;

    public readonly Vector2 Move => new(MoveX, MoveZ);
}
