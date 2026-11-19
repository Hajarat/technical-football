using System.Numerics;

namespace FootballSim.Core;

/// <summary>
/// What an action does: touches, skill moves and shots.
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

    // ---- skill moves (see SkillMoves) ----
    /// <summary>A foot circles over the ball toward <see cref="BallAction.Side"/>; no ball contact.</summary>
    StepOver,
    /// <summary>The body dips toward <see cref="BallAction.Side"/>; no ball contact.</summary>
    BodyFeint,
    /// <summary>The sole pulls the ball back and the player steps back and stops.</summary>
    DragBack,
    /// <summary>The sole rolls the ball sideways across the body toward <see cref="BallAction.Side"/>.</summary>
    BallRoll,
    /// <summary>A shot aborted into a stop: the ball is stopped under the sole and the player stops.</summary>
    FakeStop,
    /// <summary>A shot aborted into a cut: the ball is hooked toward the left stick.</summary>
    FakeCut,

    // ---- shots (see World): one action from the shoot press to the end of the follow-through ----
    /// <summary>A power shot, struck with the laces.</summary>
    Power,
    /// <summary>A finesse shot, curled with the inside of the foot.</summary>
    Finesse,
    /// <summary>A chip, lofted with backspin.</summary>
    Chip,
    /// <summary>A driven shot: low and flat, laces.</summary>
    Driven,
    /// <summary>A low finesse shot: curled along the ground with the inside of the foot.</summary>
    LowFinesse,
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

    // ---- skill moves ----
    /// <summary>True for a skill move (<see cref="ActionKind.StepOver"/> to <see cref="ActionKind.FakeCut"/>).</summary>
    public bool IsSkill => Kind is >= ActionKind.StepOver and <= ActionKind.FakeCut;
    /// <summary>
    /// For a skill move, the side it goes to relative to the running direction (Left or Right); None for touches and for the
    /// moves without a side (drag-back, fake stop). A fake cut's side is the side of the cut.
    /// </summary>
    public SkillSide Side { get; internal set; }
    /// <summary>
    /// For a skill move, the world tick it started on; -1 for touches. A skill move runs from here to <see cref="EndTick"/>;
    /// its <see cref="ContactTick"/> is the tick the sole first meets the ball, or for a move without contact (step-over,
    /// body feint) the middle of the move, with <see cref="Made"/> left false.
    /// </summary>
    public int StartTick { get; internal set; } = -1;
    /// <summary>
    /// For a skill move, the tick its own motion starts, after a reach: from <see cref="StartTick"/> to here the player closes
    /// on the ball (a sole move's foot reaching for it, a step-over or feint closing in so the ball is within the leg's reach);
    /// from here to <see cref="EndTick"/> the move itself. Equal to StartTick when there is no reach, and to ContactTick for a
    /// sole move (ball roll, drag-back, fake stop). -1 for touches.
    /// </summary>
    public int ExecuteTick { get; internal set; } = -1;
    // ---- shots ----
    /// <summary>
    /// True for a shot (<see cref="ActionKind.Power"/> to <see cref="ActionKind.LowFinesse"/>). A shot is
    /// <see cref="ActionPhase.Windup"/> from the shoot press (<see cref="PlanTick"/>) until contact; its
    /// <see cref="ContactTick"/> is the earliest possible strike while shoot is held, fixed at the release, and final at contact
    /// (later when the ball is not yet in reach then). <see cref="Kind"/> follows the modifiers held until the release. Then
    /// Contact, Recovery until <see cref="EndTick"/>, and Done. A shot dropped (faked, or the ball never came into reach) goes
    /// to Done with <see cref="Made"/> false.
    /// </summary>
    public bool IsShot => Kind >= ActionKind.Power;
    /// <summary>
    /// For a shot: true once shoot was released (charge, type and aim fixed, <see cref="ContactTick"/> final unless the ball is
    /// still out of reach then); false while shoot is held. Always false for touches and skill moves.
    /// </summary>
    public bool Released { get; internal set; }

    /// <summary>For a skill move, the unit ground direction (X, Z) the player was running in when it started; zero for touches.</summary>
    public Vector2 RunDirection => IsSkill ? HoldDirection : Vector2.Zero;

    // ---- planner state ----
    internal LegsMode Legs;
    /// <summary>Unit ground direction (X, Z) the legs hold for <see cref="LegsMode.Hold"/>.</summary>
    internal Vector2 HoldDirection;
    /// <summary>Stick direction (radians) when the action was planned, for the replan test; NaN with the stick released.</summary>
    internal float PlanStickAngle = float.NaN;
    /// <summary>The stick angle the waiting touch was last checked against for drift, radians (NaN: the plan's own).</summary>
    internal float CheckStickAngle = float.NaN;
    /// <summary>The world tick the waiting touch was last checked against the stick (see drift_recheck_deg, recheck_interval).</summary>
    internal int CheckTick = -1;
    /// <summary>A drag-back started by the planner as a turn at the feet (no chain, cooldown or exit burst of a skill move).</summary>
    internal bool FeetTurn;
    /// <summary>Touch mode of the intent when planned, for the replan-on-slowdown test.</summary>
    internal TouchMode PlanMode;
    /// <summary>The steering correction applied on the latest tick, m/s.</summary>
    internal Vector2 Steer;
    /// <summary>
    /// An early turn touch played from further off (turn_reach): the player is stepped toward the ball with the turn's larger,
    /// quicker steering (turn_steer_max, turn_steer_accel), a plant step rather than a stretched leg.
    /// </summary>
    internal bool TurnStep;
    // Skill move state (HoldDirection is the running direction the move started on).
    /// <summary>Running speed along that direction when the move started, m/s.</summary>
    internal float SkillEntry;
    /// <summary>Where the ball was when the move started and where the sole leaves it.</summary>
    internal Vector3 SkillBallFrom, SkillBallTo;
    /// <summary>A fake cut's direction (unit X, Z).</summary>
    internal Vector2 SkillCut;
    /// <summary>The effective skill_moves rating the move was made with.</summary>
    internal float SkillEffective;

    /// <summary>Seconds from world tick <paramref name="tick"/> to the contact (negative once it has passed).</summary>
    public float ContactEta(int tick, float dt) => (ContactTick - tick) * dt;

    /// <summary>The phase at <paramref name="tick"/> from the tick counts alone.</summary>
    internal ActionPhase PhaseAt(int tick, int preWindupTicks, int windupTicks)
    {
        if (IsSkill)
        {
            // A skill move: wind-up from its start to the contact (or the middle), then recovery to its end.
            if (Phase == ActionPhase.Done || tick >= EndTick) return ActionPhase.Done;
            return tick < ContactTick ? ActionPhase.Windup : tick == ContactTick ? ActionPhase.Contact : ActionPhase.Recovery;
        }
        if (IsShot)
        {
            if (Phase == ActionPhase.Done) return ActionPhase.Done;
            if (!Made) return ActionPhase.Windup;
            return tick <= ContactTick ? ActionPhase.Contact : tick < EndTick ? ActionPhase.Recovery : ActionPhase.Done;
        }
        if (Made) return tick <= ContactTick ? ActionPhase.Contact : tick < EndTick ? ActionPhase.Recovery : ActionPhase.Done;
        if (Phase == ActionPhase.Done) return ActionPhase.Done;
        if (tick >= ContactTick) return ActionPhase.Contact;
        if (tick >= ContactTick - windupTicks) return ActionPhase.Windup;
        if (tick >= ContactTick - preWindupTicks) return ActionPhase.PreWindup;
        return ActionPhase.Planned;
    }

    /// <summary>True while a touch waits for its contact. Never true for a skill move or a shot.</summary>
    public bool Pending => !Made && Phase != ActionPhase.Done && !IsSkill && !IsShot;
}
