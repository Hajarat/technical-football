using System.Numerics;

namespace FootballSim.Core;

/// <summary>One player's state, read by the game layer to draw the player.</summary>
public sealed class PlayerState
{
    /// <summary>0 or 1; team 0 attacks the goal at X = half length.</summary>
    public int Team { get; }
    /// <summary>Position in the team's list.</summary>
    public int Index { get; }
    /// <summary>Feet position on the ground (Y = 0).</summary>
    public Vector3 Position { get; internal set; }
    public Vector3 Velocity { get; internal set; }
    /// <summary>Radians, 0 = +X, positive turning toward +Z.</summary>
    public float Facing { get; internal set; }
    public PlayerRole Role { get; }
    public PlayerSheet Sheet { get; }
    /// <summary>For keepers: what the keeper is doing. Outfield players stay at Set.</summary>
    public KeeperAction KeeperAction { get; internal set; }
    /// <summary>For keepers: where the hands are this tick, so a view can lean the body. For outfield players, the feet position.</summary>
    public Vector3 HandPosition { get; internal set; }

    // ---- Contract for the on-the-ball rework (possession, feet). The game layer animates from these. ----

    /// <summary>True while this player controls the ball (dribbling it). False when the ball is loose or someone else's.</summary>
    public bool InPossession { get; internal set; }
    /// <summary>
    /// Seconds until this player's next planned touch of the ball (the pending <see cref="Action"/>'s contact), or NaN when
    /// none is planned.
    /// </summary>
    public float NextTouchEta { get; internal set; } = float.NaN;
    /// <summary>The foot expected to make that touch. Meaningful only when <see cref="NextTouchEta"/> is a number.</summary>
    public Foot NextTouchFoot { get; internal set; }
    /// <summary>Where the ball centre is expected to be at that touch (the pending action's <see cref="BallAction.ContactPoint"/>).</summary>
    public Vector3 NextTouchPoint { get; internal set; }
    /// <summary>Seconds until a pending shot makes contact with the ball, or NaN when no strike is pending.</summary>
    public float StrikeEta { get; internal set; } = float.NaN;
    /// <summary>The foot that will strike, or struck last. Meaningful while charging, during the strike delay and just after.</summary>
    public Foot StrikeFoot { get; internal set; }
    /// <summary>The foot used for this player's most recent touch or strike.</summary>
    public Foot LastTouchFoot { get; internal set; }

    /// <summary>Horizontal speed, m/s.</summary>
    public float Speed => new Vector2(Velocity.X, Velocity.Z).Length();

    // ---- Stride clock and touch actions (phase A). The view reads these; it never decides timing itself. ----

    /// <summary>World tick of this player's latest step (the tick the values below belong to).</summary>
    public int CurrentTick { get; internal set; }
    /// <summary>
    /// Stride clock phase in steps, 0..2: [0, 1) is a step of the right foot (it swings, the left is planted), [1, 2) a step of
    /// the left foot. The middle of a swing (0.5 or 1.5) is when that foot can play the ball. See <see cref="Gait"/>.
    /// </summary>
    public float GaitPhase { get; internal set; }
    /// <summary>Steps per second the stride clock advanced on the latest tick (0 when idle).</summary>
    public float StepFrequency { get; internal set; }
    /// <summary>True while the stride clock is stepping (running, or finishing a step or a planned first step).</summary>
    public bool IsStepping => StepFrequency > 0f;
    /// <summary>The foot in swing now.</summary>
    public Foot SwingFoot => Gait.SwingFoot(GaitPhase);
    /// <summary>Progress of the current swing, 0..1; 0.5 is the swing middle (the contact moment of a dribble touch).</summary>
    public float SwingProgress => Gait.SwingProgress(GaitPhase);
    /// <summary>The right foot's stride phase, 0..1: it swings during [0, 0.5) and is planted during [0.5, 1).</summary>
    public float RightFootPhase => Gait.FootPhase(GaitPhase, Foot.Right);
    /// <summary>The left foot's stride phase, 0..1: it swings during [0, 0.5) and is planted during [0.5, 1).</summary>
    public float LeftFootPhase => Gait.FootPhase(GaitPhase, Foot.Left);
    /// <summary>
    /// Predicted world tick of <paramref name="foot"/>'s next swing middle at the current step frequency, or -1 while idle.
    /// A planned touch's <see cref="BallAction.ContactTick"/> is the exact one (it accounts for speed changes).
    /// </summary>
    public int NextSwingMiddleTick(Foot foot, float dt)
    {
        if (StepFrequency <= 0f) return -1;
        return CurrentTick + Math.Max(1, (int)MathF.Ceiling(Gait.StepsToSwingMiddle(GaitPhase, foot) / (StepFrequency * dt) - 1e-4f));
    }

    /// <summary>
    /// The current action: the planned touch waiting for its contact if there is one, otherwise the latest touch (in its
    /// recovery, or done). Null before the first. Its <see cref="BallAction.Phase"/> is updated every tick.
    /// </summary>
    public BallAction? Action { get; internal set; }
    /// <summary>
    /// The touch made before <see cref="Action"/> when that one is still waiting, so its follow-through can be drawn while the
    /// next touch winds up; otherwise null.
    /// </summary>
    public BallAction? PreviousAction { get; internal set; }
    /// <summary>Seconds from now to <see cref="Action"/>'s contact while it waits, else NaN.</summary>
    public float ActionContactEta { get; internal set; } = float.NaN;
    /// <summary>The steering correction added to the velocity on the latest tick to bring the ball into the contact zone, m/s.</summary>
    public Vector3 SteerVelocity { get; internal set; }
    /// <summary>Forward running speed along the facing on the latest tick, without the steering correction, m/s.</summary>
    public float RunSpeed { get; internal set; }
    /// <summary>True while the player runs to a ball that no touch can be planned for yet (the recovery run).</summary>
    public bool Recovering { get; internal set; }

    internal float LastTouchTime = -100f;
    // The touch boost and the knock-on chase belong to the touch made at their *From time: they lapse as soon as the player
    // touches (or strikes) again or the world resets LastTouchTime, so no other code has to clear them.
    /// <summary>Simulation seconds at which the current touch boost ends.</summary>
    internal float TouchBoostUntil = -100f;
    /// <summary>The time constant is divided by this while the touch boost lasts.</summary>
    internal float TouchBoostFactor = 1f;
    /// <summary>Time of the touch that granted the boost.</summary>
    internal float TouchBoostFrom = float.NaN;
    /// <summary>Time of the most recent knock-on.</summary>
    internal float KnockOnFrom = float.NaN;
    /// <summary>Unit ground direction (X, Z) the knock-on was played along.</summary>
    internal Vector2 KnockOnDirection;
    /// <summary>Simulation seconds at which the knock-on chase ends if the ball has not been touched again.</summary>
    internal float KnockOnUntil = -100f;
    /// <summary>Simulation seconds of the player's most recent movement step (lets possession rules that get no clock see the time).</summary>
    internal float MoveTime;
    internal float TurnRefHeading;
    internal float TurnRefTime;
    /// <summary>Ticks left of a stride-clock lock (0 = free running).</summary>
    internal int GaitLockTicks;
    /// <summary>Unwrapped phase the stride clock reaches when the lock runs out.</summary>
    internal float GaitLockTarget;
    /// <summary>The foot whose swing middle the stride clock crossed on the latest tick, or null.</summary>
    internal Foot? SwingMiddleCrossed;
    /// <summary>The planned touch that was already winding up when shoot was pressed: the soft touch waits for its contact.</summary>
    internal BallAction? SoftWaitAction;
    /// <summary>Kind of the most recent touch made (decides whether the ball may be lost by distance).</summary>
    internal ActionKind LastTouchKind = ActionKind.First;
    /// <summary>A waiting touch was just dropped: the replan may pick a swing as close as the windup.</summary>
    internal bool Replanning;
    /// <summary>
    /// The waiting touch was dropped for a stick turn: the replan is an early turn touch, made no earlier than this world tick
    /// (see TouchPlanner); -1 otherwise.
    /// </summary>
    internal int EarlyTurnTick = -1;
    /// <summary>Stick direction on the previous tick, radians (NaN with the stick released).</summary>
    internal float StickAnglePrev = float.NaN;
    /// <summary>How fast the stick is being turned smoothly, radians per second (0 after a jump or a release).</summary>
    internal float StickRate;
    /// <summary>A knock-on was asked for; the next touch made is one.</summary>
    internal bool KnockOnRequested;
    /// <summary>Alternates the next-meet step count for even steps-per-touch so both feet play the ball.</summary>
    internal bool TouchRhythm;
    PlayerState? _scratch;

    /// <summary>A reusable copy of this player for predictions (never part of a world).</summary>
    internal PlayerState Scratch() => (_scratch ??= new PlayerState(Team, Index, Role, Sheet)).CopyFrom(this);

    /// <summary>Copies every value that moving the player reads or writes from <paramref name="o"/>.</summary>
    internal PlayerState CopyFrom(PlayerState o)
    {
        Position = o.Position; Velocity = o.Velocity; Facing = o.Facing; HandPosition = o.HandPosition;
        LastTouchTime = o.LastTouchTime; TouchBoostUntil = o.TouchBoostUntil; TouchBoostFactor = o.TouchBoostFactor;
        TouchBoostFrom = o.TouchBoostFrom; KnockOnFrom = o.KnockOnFrom; KnockOnDirection = o.KnockOnDirection;
        KnockOnUntil = o.KnockOnUntil; MoveTime = o.MoveTime; TurnRefHeading = o.TurnRefHeading; TurnRefTime = o.TurnRefTime;
        GaitPhase = o.GaitPhase; StepFrequency = o.StepFrequency; GaitLockTicks = o.GaitLockTicks; GaitLockTarget = o.GaitLockTarget;
        SteerVelocity = o.SteerVelocity; RunSpeed = o.RunSpeed; CurrentTick = o.CurrentTick; SwingMiddleCrossed = o.SwingMiddleCrossed;
        LastTouchKind = o.LastTouchKind;
        return this;
    }

    internal PlayerState(int team, int index, PlayerRole role, PlayerSheet sheet)
    {
        Team = team;
        Index = index;
        Role = role;
        Sheet = sheet;
    }
}
