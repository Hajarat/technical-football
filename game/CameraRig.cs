using Godot;

namespace FootballSim.Game;

/// <summary>
/// Broadcast side camera beyond the +Z touchline. The owner calls Follow() once per physics tick;
/// the transform is only written there, so physics interpolation smooths it.
/// The camera looks at a ground point that follows the ball. Near the goal the point leads the ball so the goal stays in frame.
/// </summary>
public partial class CameraRig : Camera3D
{
    public float Height = 13.5f, Distance = 34f, SmoothSeconds = 0.35f;
    public float MinFocusX = 8f, MaxFocusX = 42f, ZFollow = 0.85f, LookZOffset = 0f;
    /// <summary>Lead toward the goal (metres) that is fully applied once the ball is LeadFullX; starts at LeadStartX.</summary>
    public float GoalLead = 12f, LeadStartX = 22f, LeadFullX = 28f;
    float _focusX, _focusZ;

    public override void _Ready()
    {
        Fov = 25f;
        Near = 1f;
        Far = 400f;
        Current = true;
        Snap(new Vector3(20, 0, 0));
    }

    /// <summary>Debug framing for inspecting limbs: a close camera that follows the ball with no goal lead.</summary>
    public void UseCloseUp()
    {
        Height = 2.2f; Distance = 6.5f; Fov = 40f; ZFollow = 1f; GoalLead = 0f; MinFocusX = -100f; MaxFocusX = 100f; SmoothSeconds = 0.15f;
    }

    float TargetX(float ballX)
    {
        float lead = GoalLead * Mathf.Clamp((ballX - LeadStartX) / (LeadFullX - LeadStartX), 0f, 1f);
        return Mathf.Clamp(ballX + lead, MinFocusX, MaxFocusX);
    }

    /// <summary>Smoothly follow the ball. Call from _PhysicsProcess with the physics delta, after the ball has been placed.</summary>
    public void Follow(Vector3 ball, double delta)
    {
        float a = 1f - Mathf.Exp(-(float)delta / SmoothSeconds);
        _focusX += (TargetX(ball.X) - _focusX) * a;
        _focusZ += (ball.Z - _focusZ) * a;
        Place();
    }

    /// <summary>Jump straight to the ball (round reset) without interpolating.</summary>
    public void Snap(Vector3 ball)
    {
        _focusX = TargetX(ball.X);
        _focusZ = ball.Z;
        Place();
        ResetPhysicsInterpolation();
    }

    void Place()
    {
        var pos = new Vector3(_focusX, Height, Distance);
        var look = new Vector3(_focusX, 0, _focusZ * ZFollow + LookZOffset);
        Transform = new Transform3D(Basis.Identity, pos).LookingAt(look, Vector3.Up);
    }
}
