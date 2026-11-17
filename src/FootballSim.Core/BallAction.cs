using System.Numerics;

namespace FootballSim.Core;

/// <summary>
/// What an action does. Touches are made in phase A; the skill and shot kinds are reserved for the later phases so the view
/// contract does not change when they arrive.
/// </summary>
public enum ActionKind
{
    // ---- touches ----
    /// <summary>A close-control touch (one step per touch).</summary>
    Close,
    /// <summary>A jogging touch.</summary>
    Jog,
    /// <summary>A sprinting touch, the ball pushed further ahead.</summary>
    Sprint,
    /// <summary>A touch made while the legs slow down to let the ball come back to the foot.</summary>
    Slowdown,
    /// <summary>The stick was released: the ball is brought to rest just ahead of where the player stops.</summary>
    Stop,
    /// <summary>A touch that sends the ball off its travel by more than the execution angle.</summary>
    Turn,
    /// <summary>A sharp turn at speed: the player loses the balance percentage of speed.</summary>
    Cut,
    /// <summary>A knock-on: pushed far ahead to run onto, planned to be met at the catch time.</summary>
    KnockOn,
    /// <summary>A planned push that needed more than the largest push speed; the ball may be lost by distance after it.</summary>
    Heavy,
    /// <summary>Shoot was pressed in possession: the ball is set to the player's speed so it stays strikable.</summary>
    Soft,
    /// <summary>First contact with a loose ball (first_touch rating). The player gains possession.</summary>
    First,

    // ---- skills (phase B, not produced yet) ----
    StepOver, BodyFeint, DragBack, BallRoll, FakeStop, FakeCut,

    // ---- shots (phase C, not produced yet) ----
    Power, Finesse, Chip, Driven, LowFinesse,
}

/// <summary>Where an action is in time. Phases advance by ticks, so they are a pure function of the world tick.</summary>
public enum ActionPhase
{
    /// <summary>Chosen, more than the pre-windup before contact. The legs follow the stick.</summary>
    Planned,
    /// <summary>From the pre-windup until the windup: the player is steered so the ball arrives in the contact zone.</summary>
    PreWindup,
    /// <summary>The foot is drawn back; steering continues.</summary>
    Windup,
    /// <summary>The contact tick: the ball gets its new velocity and spin.</summary>
    Contact,
    /// <summary>Follow-through after contact, until <see cref="BallAction.EndTick"/>.</summary>
    Recovery,
    /// <summary>Finished, or dropped before contact (see <see cref="BallAction.Made"/>).</summary>
    Done,
}

/// <summary>The part of the foot that plays the ball.</summary>
public enum FootSurface { Inside, Laces, Outside, Sole }

/// <summary>What the legs do while a planned touch waits for its contact.</summary>
internal enum LegsMode
{
    /// <summary>Follow the stick.</summary>
    Stick,
    /// <summary>Hold the current running speed along a fixed direction (run on to the ball).</summary>
    Hold,
    /// <summary>
    /// Ease off (let the ball come back to the foot): with the stick pushed the legs slow down while turning toward the stick;
    /// with it released they just slow down.
    /// </summary>
    Neutral,
    /// <summary>Run to where the ball will be and arrive at its pace (the recovery run).</summary>
    Approach,
}

/// <summary>
/// One action of one player: a touch now, skills and shots later. Created by the touch planner, read by the view. Ticks are
/// world ticks at 120 per second; positions are world metres (see Types.cs). The view never decides timing: it reads
/// <see cref="Phase"/>, <see cref="Foot"/>, <see cref="ContactTick"/> and <see cref="ContactPoint"/>.
/// </summary>
public sealed class BallAction
{
    /// <summary>What the action does. Until contact this is the kind expected; at contact it is final.</summary>
    public ActionKind Kind { get; internal set; }
    /// <summary>The phase at the latest world tick.</summary>
    public ActionPhase Phase { get; internal set; }
    /// <summary>The foot that plays the ball: the foot in swing at <see cref="ContactTick"/>.</summary>
    public Foot Foot { get; internal set; }
    /// <summary>The part of the foot used.</summary>
    public FootSurface Surface { get; internal set; }
    /// <summary>World tick the action was planned on.</summary>
    public int PlanTick { get; internal set; }
    /// <summary>World tick of the contact (the middle of the foot's swing for a dribble touch).</summary>
    public int ContactTick { get; internal set; }
    /// <summary>World tick the recovery ends.</summary>
    public int EndTick { get; internal set; }
    /// <summary>
    /// Where foot and ball meet at <see cref="ContactTick"/>: the ball centre (Y = ball radius). Before contact it is the latest
    /// prediction (the swinging foot's hip at the contact tick plus the contact-zone lead and the foot's lateral offset); from
    /// contact on it is fixed.
    /// </summary>
    public Vector3 ContactPoint { get; internal set; }
    /// <summary>The ball velocity applied at contact, m/s (zero before contact).</summary>
    public Vector3 BallOutVelocity { get; internal set; }
    /// <summary>The ball spin applied at contact, rad/s (zero before contact).</summary>
    public Vector3 BallOutSpin { get; internal set; }
    /// <summary>For a push: the world tick the ball is meant to meet the next foot, or -1 when none is planned.</summary>
    public int NextMeetTick { get; internal set; } = -1;
    /// <summary>For a push: where the ball centre is meant to be at <see cref="NextMeetTick"/>.</summary>
    public Vector3 NextMeetPoint { get; internal set; }
    /// <summary>The foot planned for the next meet.</summary>
    public Foot NextMeetFoot { get; internal set; }
    /// <summary>True once the contact was made; false while waiting and for an action dropped before contact.</summary>
    public bool Made { get; internal set; }
    /// <summary>Horizontal distance from the ball centre to <see cref="ContactPoint"/> at contact, metres (NaN before).</summary>
    public float ContactMiss { get; internal set; } = float.NaN;

    // ---- planner state ----
    internal LegsMode Legs;
    /// <summary>Unit ground direction (X, Z) the legs hold for <see cref="LegsMode.Hold"/>.</summary>
    internal Vector2 HoldDirection;
    /// <summary>Stick direction (radians) when the action was planned, for the replan test; NaN with the stick released.</summary>
    internal float PlanStickAngle = float.NaN;
    /// <summary>Touch mode of the intent when planned, for the replan-on-slowdown test.</summary>
    internal TouchMode PlanMode;
    /// <summary>The steering correction applied on the latest tick, m/s.</summary>
    internal Vector2 Steer;

    /// <summary>Seconds from world tick <paramref name="tick"/> to the contact (negative once it has passed).</summary>
    public float ContactEta(int tick, float dt) => (ContactTick - tick) * dt;

    /// <summary>The phase at <paramref name="tick"/> from the tick counts alone.</summary>
    internal ActionPhase PhaseAt(int tick, int preWindupTicks, int windupTicks)
    {
        if (Made) return tick <= ContactTick ? ActionPhase.Contact : tick < EndTick ? ActionPhase.Recovery : ActionPhase.Done;
        if (Phase == ActionPhase.Done) return ActionPhase.Done;
        if (tick >= ContactTick) return ActionPhase.Contact;
        if (tick >= ContactTick - windupTicks) return ActionPhase.Windup;
        if (tick >= ContactTick - preWindupTicks) return ActionPhase.PreWindup;
        return ActionPhase.Planned;
    }

    /// <summary>True while the action waits for its contact.</summary>
    public bool Pending => !Made && Phase != ActionPhase.Done;
}
