using Godot;

namespace FootballSim.Game;

/// <summary>
/// Broadcast side camera on the +Z side. The owner calls Follow() once per physics tick;
/// the transform is only written there, so physics interpolation smooths it.
/// The camera looks at a ground point that follows the ball, and sits a fixed Height above and Distance behind (+Z) that point,
/// so the framing is the same wherever the ball is. Near the goal the point leads the ball so the goal stays in frame.
/// In the arena the point is held back so the left edge of the (16:9) frame, measured on the ground through the point, never
/// goes past the back wall and the right edge never past the back of the goal net plus a margin; and in depth so the frame's
/// bottom edge never reaches nearer the camera than the near boards.
/// </summary>
public partial class CameraRig : Camera3D
{
    public float Height = 6.2f, Distance = 15.5f, SmoothSeconds = 0.35f;
    // Arena of 30 m: the lead starts 22.5 m from the goal and is full 14.5 m out (as on the old full-size half pitch). The
    // end clamps below decide how far toward either end the focus can go.
    public float MinFocusX = 0f, MaxFocusX = 30f, ZFollow = 0.85f, LookZOffset = 0f;
    /// <summary>Lead toward the goal (metres) that is fully applied once the ball is LeadFullX; starts at LeadStartX.</summary>
    public float GoalLead = 3f, LeadStartX = 7.5f, LeadFullX = 15.5f;
    /// <summary>
    /// The frame's left and right edges stay between these X values (the back wall, and the back of the goal net plus a margin);
    /// the bottom edge stays at or beyond NearZ (the near boards). Off for the close-up camera.
    /// </summary>
    public float WallMinX = 0f, WallMaxX = 32.5f, NearZ = 10f, BoardHeight = 1f;
    public bool ClampToWalls = true;
    /// <summary>Width over height of the frame; the window is locked to 16:9.</summary>
    const float Aspect = 16f / 9f;
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
        ClampToWalls = false;
    }

    /// <summary>Debug framing for seeing sideways motion: a camera almost straight above the ball, following it.</summary>
    public void UseTopDown()
    {
        UseCloseUp();
        Height = 6f; Distance = 0.8f; Fov = 28f;
    }

    /// <summary>
    /// Sets the limits of what the frame may show: its left edge stops at <paramref name="minX"/>, its right edge at
    /// <paramref name="maxX"/>, and its bottom edge at <paramref name="nearZ"/>.
    /// </summary>
    public void SetLimits(float minX, float maxX, float nearZ)
    {
        WallMinX = minX; WallMaxX = maxX; NearZ = nearZ;
    }

    /// <summary>
    /// Half the frame's width on the ground through the look point, metres: the distance from the camera to that point times the
    /// tangent of half the horizontal field of view (the field of view set is the vertical one). The camera keeps a fixed
    /// offset from the look point, so this is the same everywhere.
    /// </summary>
    float HalfGroundWidth()
    {
        float dist = new Vector2(Height, Distance).Length();
        float halfH = Mathf.Atan(Mathf.Tan(Mathf.DegToRad(Fov) / 2f) * Aspect);
        return dist * Mathf.Tan(halfH);
    }

    float ClampToEnds(float x)
    {
        if (!ClampToWalls) return x;
        float half = HalfGroundWidth();
        float lo = WallMinX + half, hi = WallMaxX - half;
        return lo > hi ? (WallMinX + WallMaxX) / 2f : Mathf.Clamp(x, lo, hi);
    }

    /// <summary>
    /// The look point's Z for a focus Z, held back so the frame's bottom edge passes over the top of the near boards (NearZ,
    /// BoardHeight): the bottom ray leaves the camera at the camera's tilt plus half the vertical field of view and drops to
    /// board height (Height - BoardHeight) / tan(that) in front of it. Measuring at the board's foot instead lets the 1 m board
    /// rise into the frame and hide a ball played close to it.
    /// </summary>
    float LookZ(float focusZ)
    {
        float z = focusZ * ZFollow + LookZOffset;
        if (!ClampToWalls) return z;
        float bottom = Mathf.Atan2(Height, Distance) + Mathf.DegToRad(Fov) / 2f;
        float reach = bottom < Mathf.Pi / 2f ? (Height - BoardHeight) / Mathf.Tan(bottom) : 0f;
        return Mathf.Min(z, NearZ - Distance + reach);
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
        _focusZ += (ball.Z - _focusZ) * a;
        _focusX += (ClampToEnds(TargetX(ball.X)) - _focusX) * a;
        Place();
    }

    /// <summary>Jump straight to the ball (round reset) without interpolating.</summary>
    public void Snap(Vector3 ball)
    {
        _focusZ = ball.Z;
        _focusX = ClampToEnds(TargetX(ball.X));
        Place();
        ResetPhysicsInterpolation();
    }

    void Place()
    {
        float lookZ = LookZ(_focusZ);
        var pos = new Vector3(_focusX, Height, lookZ + Distance);
        var look = new Vector3(_focusX, 0, lookZ);
        Transform = new Transform3D(Basis.Identity, pos).LookingAt(look, Vector3.Up);
    }
}
