using System.Numerics;

namespace FootballSim.Core;

/// <summary>
/// The loose ball's path, forecast once per ball path: a copy of the ball stepped ahead through the pitch (boards, goal frame,
/// ceiling) with no players, one sample per tick. Sample i is the ball at the end of tick <see cref="StartTick"/> + i, so sample 0
/// is the ball as it was when the forecast was built. It is the same step the real ball takes, so until somebody plays the ball
/// the real ball follows the samples tick for tick; <see cref="Matches"/> tells when it no longer does. The forecast grows a tick
/// at a time (<see cref="Extend"/>) to stay <c>lock_on.horizon_seconds</c> ahead, stops growing once the ball is at rest (a
/// resting ball stays where it is) and ends where the ball crosses into the goal.
/// </summary>
public sealed class BallForecast
{
    readonly List<Vector3> _pos = new();
    readonly List<Vector3> _vel = new();
    Ball? _tail;
    Pitch? _pitch;
    float _dt;

    /// <summary>The world tick of sample 0 (the ball as it was at the build), or -1 when nothing is forecast.</summary>
    public int StartTick { get; private set; } = -1;
    /// <summary>Samples made so far.</summary>
    public int Count => _pos.Count;
    /// <summary>The index of the first sample at rest on the ground (every later tick is the same spot), or -1 while it moves.</summary>
    public int RestIndex { get; private set; } = -1;
    /// <summary>The index of the sample at which the ball is in the goal (the forecast ends there), or -1.</summary>
    public int GoalIndex { get; private set; } = -1;
    /// <summary>True when a forecast has been built and not cleared.</summary>
    public bool Valid => StartTick >= 0;
    /// <summary>True when the path ends in the goal: nobody claims such a ball.</summary>
    public bool EndsInGoal => GoalIndex >= 0;
    /// <summary>The last world tick with a sample of its own (a resting ball has every later tick too).</summary>
    public int LastTick => StartTick + _pos.Count - 1;

    /// <summary>Forgets the path (the ball is controlled or held, or play stopped).</summary>
    public void Clear()
    {
        _pos.Clear(); _vel.Clear();
        StartTick = -1; RestIndex = -1; GoalIndex = -1;
    }

    /// <summary>Starts a new path from the ball as it is at the end of world tick <paramref name="tick"/>, forecast to <paramref name="untilTick"/>.</summary>
    public void Build(Ball ball, Pitch? pitch, int tick, int untilTick, float dt)
    {
        Clear();
        _tail = (_tail ?? ball.Clone()).CopyFrom(ball);
        _pitch = pitch;
        _dt = dt;
        StartTick = tick;
        Add();
        Extend(untilTick);
    }

    void Add()
    {
        var b = _tail!;
        int i = _pos.Count;
        _pos.Add(b.Position);
        _vel.Add(b.Velocity);
        if (_pitch != null && _pitch.IsGoal(b.Position, b.Radius)) GoalIndex = i;
        else if (b.OnGround && b.Velocity == Vector3.Zero) RestIndex = i;
    }

    /// <summary>Steps the copy on until the samples reach world tick <paramref name="untilTick"/> (or the ball rests or is in the goal).</summary>
    public void Extend(int untilTick)
    {
        if (!Valid) return;
        while (RestIndex < 0 && GoalIndex < 0 && LastTick < untilTick)
        {
            _tail!.Step(_dt, _pitch, null);
            Add();
        }
    }

    /// <summary>True when the forecast says where the ball is at the end of world tick <paramref name="tick"/>.</summary>
    public bool Has(int tick) => Valid && tick >= StartTick && (tick <= LastTick || RestIndex >= 0);

    int Index(int tick) => Math.Clamp(tick - StartTick, 0, _pos.Count - 1);

    /// <summary>The ball centre at the end of world tick <paramref name="tick"/> (the nearest sample outside the forecast).</summary>
    public Vector3 PositionAt(int tick) => _pos[Index(tick)];
    /// <summary>The ball's velocity at the end of world tick <paramref name="tick"/>.</summary>
    public Vector3 VelocityAt(int tick) => _vel[Index(tick)];
    /// <summary>True when the ball is at rest at the end of world tick <paramref name="tick"/>.</summary>
    public bool RestingAt(int tick) => RestIndex >= 0 && tick - StartTick >= RestIndex;

    /// <summary>
    /// True while the real ball, as it is at the end of world tick <paramref name="tick"/>, is still on this path: within
    /// <paramref name="drift"/> metres of the sample and at the sample's velocity. A ball somebody played is not.
    /// </summary>
    public bool Matches(Ball ball, int tick, float drift)
    {
        if (!Has(tick)) return false;
        if (Vector3.Distance(ball.Position, PositionAt(tick)) > drift) return false;
        return (ball.Velocity - VelocityAt(tick)).LengthSquared() <= VelocityTolerance * VelocityTolerance;
    }

    /// <summary>A ball whose velocity is further than this from the sample's has been played, m/s.</summary>
    const float VelocityTolerance = 0.02f;
}
