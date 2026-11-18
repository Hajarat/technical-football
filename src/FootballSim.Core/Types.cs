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
    /// <summary>Held: close control (the gamepad's shield button, left trigger).</summary>
    public bool CloseControl;
    /// <summary>
    /// True while the shoot button is held. Power charges while held; release strikes. Pressed again within
    /// low_shot_window of the first press, before the strike, it makes the shot a low one (as <see cref="Driven"/>).
    /// </summary>
    public bool ShootHeld;
    /// <summary>
    /// Held: finesse shot (with <see cref="Driven"/> or shoot tapped twice, a low finesse shot); the right bumper. With
    /// <see cref="Chip"/> and the left stick back, a drag-back.
    /// </summary>
    public bool Finesse;
    /// <summary>Held: chip shot. Wins over every other modifier. The left bumper (see <see cref="Finesse"/> for the drag-back).</summary>
    public bool Chip;
    /// <summary>Held: driven shot (with <see cref="Finesse"/>, a low finesse shot). The keyboard's modifier and the scripted attacker's; the gamepad taps shoot twice instead.</summary>
    public bool Driven;
    /// <summary>True on the tick a knock-on (a heavy touch far ahead) is asked for; one tick per press.</summary>
    public bool KnockOn;
    /// <summary>Raw right stick along world X, -1..1, on the same axes as <see cref="MoveX"/>. Read for skill-move gestures.</summary>
    public float RightX;
    /// <summary>Raw right stick along world Z, -1..1, on the same axes as <see cref="MoveZ"/> (negative is screen-up).</summary>
    public float RightZ;
    /// <summary>True while the cancel button is held. Pressed just after a shot starts, it turns the shot into a fake.</summary>
    public bool Cancel;
    /// <summary>
    /// A skill move asked for directly (keyboard keys 1-4), on one tick per press; the side comes from the left stick. It skips
    /// the right-stick gesture but obeys the same gap, execution radius and chain rules.
    /// </summary>
    public SkillKey SkillKey;

    public readonly Vector2 Move => new(MoveX, MoveZ);
    /// <summary>The right stick as a ground vector (X, Z).</summary>
    public readonly Vector2 Right => new(RightX, RightZ);
}

/// <summary>A skill move asked for by a key rather than a right-stick gesture.</summary>
public enum SkillKey { None, StepOver, BodyFeint, BallRoll, DragBack }

/// <summary>The side of a skill move, relative to the player's running direction.</summary>
public enum SkillSide { None, Left, Right }
