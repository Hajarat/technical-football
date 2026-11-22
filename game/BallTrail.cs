using System.Collections.Generic;
using Godot;

namespace FootballSim.Game;

/// <summary>
/// The smoke trail behind a shot: a camera-facing ribbon through the ball's recent positions, sampled once per rendered frame.
/// A shot's strike starts it; a save, frame hit, board hit, goal or bounce stops it at once, as does a ball slower than
/// <see cref="StopSpeed"/> or <see cref="MaxSeconds"/> after the strike; the rest of the ribbon then fades over <see cref="FadeSeconds"/>.
/// View only: it reads the ball and the simulation's events and decides nothing.
/// </summary>
public partial class BallTrail : Node3D
{
    // Strength runs from nothing at SlowSpeed to full at FastSpeed (m/s) and scales the ribbon (the reference's per-event strength curve).
    public const float SlowSpeed = 12f, FastSpeed = 30f;
    /// <summary>The ball slower than this (m/s) stops the trail.</summary>
    public const float StopSpeed = 8f;
    /// <summary>The trail always stops this long after the strike (the reference's fall-off time), seconds.</summary>
    public const float MaxSeconds = 1.0f;
    /// <summary>After stopping, the remaining ribbon fades out over this long, seconds.</summary>
    public const float FadeSeconds = 0.3f;
    /// <summary>Ribbon length (seconds of ball path kept) = BaseSeconds + ExtraSeconds * strength.</summary>
    public const float BaseSeconds = 0.25f, ExtraSeconds = 0.35f;
    /// <summary>Ribbon width at the ball (metres) = BaseWidth + ExtraWidth * strength; it tapers to 0 at the tail.</summary>
    public const float BaseWidth = 0.10f, ExtraWidth = 0.12f;
    /// <summary>Opacity at the ball = MaxOpacity * strength; 0 at the tail.</summary>
    public const float MaxOpacity = 0.55f;

    struct Sample { public Vector3 Pos; public float Time, Width, Opacity, Length; }

    readonly List<Sample> _samples = new();
    readonly ImmediateMesh _mesh = new();
    MeshInstance3D _view = null!;
    bool _emitting;
    float _age, _stoppedAt = -1f, _now;
    float _strength;
    Node3D? _follow;

    public bool Emitting => _emitting;

    public static float Strength(float speed) => Mathf.Clamp((speed - SlowSpeed) / (FastSpeed - SlowSpeed), 0f, 1f);

    public override void _Ready()
    {
        _view = new MeshInstance3D
        {
            Name = "Ribbon", Mesh = _mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://ball_trail.gdshader"), RenderPriority = 5 },
            ExtraCullMargin = 100f,
        };
        AddChild(_view);
    }

    /// <summary>The node whose (interpolated) position is sampled each rendered frame.</summary>
    public void Follow(Node3D ball) => _follow = ball;

    /// <summary>A shot's strike: starts the trail if the ball leaves faster than <see cref="SlowSpeed"/>.</summary>
    public void Start(float ballSpeed)
    {
        Clear();
        if (Strength(ballSpeed) <= 0f) return;
        _emitting = true; _age = 0f; _strength = Strength(ballSpeed);
    }

    /// <summary>Called every physics tick while the trail runs: the ball's speed and the time that tick covers.</summary>
    public void Tick(float ballSpeed, float dt)
    {
        if (!_emitting) return;
        _age += dt;
        _strength = Strength(ballSpeed);
        if (ballSpeed < StopSpeed || _age >= MaxSeconds) Stop();
    }

    /// <summary>An event that ends the trail at once (save, frame hit, board hit, goal, bounce).</summary>
    public void Stop()
    {
        if (!_emitting) return;
        _emitting = false; _stoppedAt = _now;
    }

    public void Clear()
    {
        _emitting = false; _stoppedAt = -1f; _samples.Clear(); _mesh.ClearSurfaces();
    }

    public override void _Process(double delta)
    {
        _now += (float)delta;
        if (_emitting && _follow != null)
        {
            Vector3 pos = _follow.GetGlobalTransformInterpolated().Origin;
            float len = BaseSeconds + ExtraSeconds * _strength;
            _samples.Add(new Sample { Pos = pos, Time = _now, Width = BaseWidth + ExtraWidth * _strength, Opacity = MaxOpacity * _strength, Length = len });
        }
        // drop samples older than their length (and everything once the fade is over)
        while (_samples.Count > 0 && _now - _samples[0].Time > _samples[0].Length) _samples.RemoveAt(0);
        float fade = 1f;
        if (_stoppedAt >= 0f)
        {
            fade = 1f - (_now - _stoppedAt) / FadeSeconds;
            if (fade <= 0f) _samples.Clear();
        }
        Rebuild(fade);
    }

    void Rebuild(float fade)
    {
        _mesh.ClearSurfaces();
        int n = _samples.Count;
        if (n < 2) return;
        var cam = GetViewport().GetCamera3D();
        if (cam == null) return;
        Vector3 camPos = cam.GlobalPosition;
        _mesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip);
        for (int i = n - 1; i >= 0; i--)   // from the ball (newest) back to the tail
        {
            var s = _samples[i];
            float age = _now - s.Time, taper = Mathf.Clamp(1f - age / s.Length, 0f, 1f);
            Vector3 prev = _samples[Mathf.Min(i + 1, n - 1)].Pos, next = _samples[Mathf.Max(i - 1, 0)].Pos;
            Vector3 tangent = prev - next;
            if (tangent.LengthSquared() < 1e-10f) tangent = Vector3.Right;
            Vector3 side = tangent.Cross(camPos - s.Pos);
            side = side.LengthSquared() < 1e-12f ? Vector3.Up : side.Normalized();
            float half = s.Width * taper * 0.5f;
            float u = 1f - taper;
            var col = new Color(1, 1, 1, s.Opacity * taper * Mathf.Clamp(fade, 0f, 1f));
            _mesh.SurfaceSetColor(col); _mesh.SurfaceSetUV(new Vector2(u, -1f)); _mesh.SurfaceAddVertex(s.Pos - side * half);
            _mesh.SurfaceSetColor(col); _mesh.SurfaceSetUV(new Vector2(u, 1f)); _mesh.SurfaceAddVertex(s.Pos + side * half);
        }
        _mesh.SurfaceEnd();
    }
}
