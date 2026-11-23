using System.Globalization;
using System.Numerics;

namespace FootballSim.Core;

/// <summary>
/// One shot's geometry as a replay plays it: taken on the tick the shot is released, then completed at the strike. Angles are
/// degrees on the ground plane (0 toward the goal, +X; positive toward +Z).
/// </summary>
public sealed class ShotReleaseEntry
{
    /// <summary>The world tick of the release (ticks from Begin) and its time, seconds.</summary>
    public int Tick { get; init; }
    public float Time { get; init; }
    public Vector2 Position { get; init; }
    public float FacingDeg { get; init; }
    /// <summary>The left stick on the release tick: its direction (degrees) and size (0 to 1).</summary>
    public float StickDeg { get; init; }
    public float StickSize { get; init; }
    public float AimDeg { get; init; }
    /// <summary>The direction from the ball to the goal centre.</summary>
    public float GoalDeg { get; init; }
    /// <summary>The signed angle from the facing to the aim (positive toward +Z).</summary>
    public float ThetaDeg { get; init; }
    /// <summary>The outside cap for the shot's type (<see cref="ShotModel.OutsideCapDeg"/>).</summary>
    public float CapDeg { get; init; }
    public ShotType Type { get; init; }
    /// <summary>The foot rule and the geometry's choice at the release (<see cref="World.StrikeGeometry"/>).</summary>
    public StrikeRule Rule { get; init; }
    public Foot GeometryFoot { get; init; }
    public FootSurface GeometrySurface { get; init; }
    /// <summary>The aimed point on the goal line (Z).</summary>
    public float AimZ { get; init; }
    /// <summary>The ball's offset from the body: to the side (positive on the +Z side when he faces the goal) and forward.</summary>
    public float BallSide { get; init; }
    public float BallForward { get; init; }
    public float Speed { get; init; }
    /// <summary>What struck the ball, or null when the replay ended (or the shot was faked) before a strike.</summary>
    public ShotResult? Strike { get; internal set; }

    /// <summary>The audit line for this shot.</summary>
    public string Line(int index)
    {
        var ic = CultureInfo.InvariantCulture;
        string F(float v, string f = "F0") => v.ToString(f, ic);
        string strike = Strike is { } s ? $"strike {s.Foot}/{s.Surface} {s.Type}{(s.Flick ? " flick" : s.FirstTime ? " first-time" : "")}" : "strike none";
        return $"shot{index} t{Tick} ({F(Time, "F2")} s) pos ({F(Position.X, "F1")},{F(Position.Y, "F1")}) facing {F(FacingDeg)} " +
               $"stick {F(StickDeg)} |{F(StickSize, "F2")}| aim {F(AimDeg)} | goal dir {F(GoalDeg)} theta {F(ThetaDeg)} cap {F(CapDeg)} " +
               $"| rule {Rule} geo {GeometryFoot}/{GeometrySurface} | aimZ {F(AimZ, "F2")} ball side {F(BallSide, "F2")} fwd {F(BallForward, "F2")} " +
               $"speed {F(Speed, "F1")} | {strike}";
    }
}

/// <summary>
/// Plays a replay and records every released shot (see <see cref="ShotReleaseEntry"/>). A finished replay is followed by idle ticks
/// up to its report, as <see cref="Replay.Run"/> does; a partial one stops at its last recorded tick.
/// </summary>
public static class ShotReleaseAudit
{
    public static List<ShotReleaseEntry> Run(Replay replay, Tuning tuning, Scenario scenario, PlayerSheet attacker, PlayerSheet keeper,
        PlayerSheet? defender = null)
    {
        var entries = new List<ShotReleaseEntry>();
        BallAction? seen = null;
        int shots = 0;
        void Observe(World w, TickIntent intent)
        {
            var a = w.Attacker;
            if (a.Action is { IsShot: true, Released: true } act && !ReferenceEquals(act, seen))
            {
                seen = act;
                entries.Add(At(w, act, intent.Move, attacker, tuning));
            }
            while (w.Shots.Count > shots)
            {
                var s = w.Shots[shots++];
                var open = entries.FindLast(e => e.Strike == null);
                if (open != null) open.Strike = s;
            }
        }
        var world = replay.Play(tuning, scenario, attacker, keeper, Observe, defender);
        if (!replay.Partial)
        {
            var idle = new TickIntent[world.PlayerCount];
            int cap = Replay.TailCap(tuning);
            for (int i = 0; i < cap && world.State != RoundState.Report; i++) { world.Step(idle); Observe(world, default); }
        }
        return entries;
    }

    static ShotReleaseEntry At(World w, BallAction act, Vector2 stick, PlayerSheet sheet, Tuning t)
    {
        const float Deg = 180f / MathF.PI;
        var a = w.Attacker;
        var ball = w.Ball.Position;
        var type = TypeOf(act.Kind);
        var g = w.StrikeGeometry;
        var rel = new Vector2(ball.X - a.Position.X, ball.Z - a.Position.Z);
        var fwd = new Vector2(MathF.Cos(a.Facing), MathF.Sin(a.Facing));
        var left = new Vector2(-MathF.Sin(a.Facing), MathF.Cos(a.Facing));
        return new ShotReleaseEntry
        {
            Tick = w.Tick,
            Time = w.Tick / (float)t.TickRate,
            Position = new Vector2(a.Position.X, a.Position.Z),
            FacingDeg = a.Facing * Deg,
            StickDeg = stick.LengthSquared() > 1e-8f ? MathF.Atan2(stick.Y, stick.X) * Deg : 0f,
            StickSize = stick.Length(),
            AimDeg = act.AimYaw * Deg,
            GoalDeg = MathF.Atan2(-ball.Z, w.Pitch.GoalLineX - ball.X) * Deg,
            ThetaDeg = Movement.WrapAngle(act.AimYaw - a.Facing) * Deg,
            CapDeg = ShotModel.OutsideCapDeg(sheet, type, t),
            Type = type,
            Rule = g.Rule,
            GeometryFoot = g.Choice.Foot,
            GeometrySurface = g.Choice.Surface,
            AimZ = ShotModel.AimZ(ball, act.AimYaw, w.Pitch, t),
            BallSide = Vector2.Dot(rel, left),
            BallForward = Vector2.Dot(rel, fwd),
            Speed = a.Speed,
        };
    }

    /// <summary>The shot type of a shot action's kind.</summary>
    public static ShotType TypeOf(ActionKind kind) => kind switch
    {
        ActionKind.Finesse => ShotType.Finesse,
        ActionKind.Chip => ShotType.Chip,
        ActionKind.Driven => ShotType.Driven,
        ActionKind.LowFinesse => ShotType.LowFinesse,
        _ => ShotType.Power,
    };
}
