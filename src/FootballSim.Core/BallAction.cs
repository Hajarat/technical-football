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
    /// <summary>
    /// A touch made while the legs slow down to let the ball come back to the foot. No longer made: with the stick pushed the
    /// run slows only as far as the steering needs (see TouchPlanner); kept for older replays and views.
    /// </summary>
    Slowdown,
    /// <summary>The stick was released: the ball is brought to rest just ahead of where the player stops.</summary>
    Stop,
    /// <summary>A touch that sends the ball off its travel by more than the execution angle.</summary>
    Turn,
    /// <summary>A sharp turn at speed: the player loses the balance percentage of speed.</summary>
    Cut,
    /// <summary>A knock-on: pushed far ahead to run onto, planned to be met at the catch time.</summary>
    KnockOn,
    /// <summary>
    /// No longer made: a push is never longer than the player can catch (see TouchPlanner); kept for older replays and views, and
    /// the ball may be lost by distance after it.
    /// </summary>
    Heavy,
    /// <summary>Shoot was pressed in possession: the ball is set to the player's speed so it stays strikable.</summary>
    Soft,
    /// <summary>First contact with a loose ball (first_touch rating). The player gains possession.</summary>
    First,
    /// <summary>
    /// First contact that kills a loose ball at the feet (the low band: the sole or inside, with the stick released); the player
    /// gains possession.
    /// </summary>
    TrapLow,
    /// <summary>First contact that kills a loose ball at shin or thigh height (the mid band) and drops it ahead; the player gains possession.</summary>
    TrapMid,
    /// <summary>First contact that kills a loose ball on the chest (the high band) and drops it ahead; the player gains possession.</summary>
    TrapHigh,

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

    // ---- skill moves added later (after the shots, so the values above keep their numbers) ----
    /// <summary>
    /// The outside of the foot pushes the ball away from <see cref="BallAction.Side"/>, the inside snaps it back across the body
    /// toward it (<see cref="BallAction.Contact2Tick"/>).
    /// </summary>
    Elastico,
    /// <summary>
    /// A 360 roulette toward <see cref="BallAction.Side"/>: one sole drags the ball back and round while the body turns half way,
    /// lifts (<see cref="BallAction.LiftTick"/>), and the other sole takes it on round (<see cref="BallAction.Contact2Tick"/>).
    /// </summary>
    Spin,
    /// <summary>
    /// Straight on: the leading foot's heel knocks the ball back against the trailing foot, which flicks it forward ahead of the
    /// player (<see cref="BallAction.Contact2Tick"/>).
    /// </summary>
    HeelToHeel,
}

/// <summary>Where an action is in time. Phases advance by ticks, so they are a pure function of the world tick.</summary>
public enum ActionPhase
{
    /// <summary>
    /// Not used by touches: the touch controller commits a touch only one windup before its contact (kept for older views and
    /// replays).
    /// </summary>
    Planned,
    /// <summary>Not used by touches (see <see cref="Planned"/>).</summary>
    PreWindup,
    /// <summary>
    /// The foot is drawn back: a touch from its commit (one windup before the contact) to the contact; the player is steered so
    /// the ball arrives in the contact zone.
    /// </summary>
    Windup,
    /// <summary>The contact tick: the ball gets its new velocity and spin.</summary>
    Contact,
    /// <summary>Follow-through after contact, until <see cref="BallAction.EndTick"/>.</summary>
    Recovery,
    /// <summary>Finished, or dropped before contact (see <see cref="BallAction.Made"/>).</summary>
    Done,
}

/// <summary>The part of the foot that plays the ball.</summary>
public enum FootSurface { Inside, Laces, Outside, Sole, Heel }

/// <summary>
/// One action of one player: a touch now, skills and shots later. Created by the touch controller (TouchPlanner), read by the view. Ticks are
/// world ticks at 120 per second; positions are world metres (see Types.cs). The view never decides timing: it reads
/// <see cref="Phase"/>, <see cref="Foot"/>, <see cref="ContactTick"/> and <see cref="ContactPoint"/>.
/// </summary>
public sealed class BallAction
{
    /// <summary>What the action does. Until contact this is the kind expected; at contact it is final.</summary>
    public ActionKind Kind { get; internal set; }
    /// <summary>The phase at the latest world tick.</summary>
    public ActionPhase Phase { get; internal set; }
    /// <summary>
    /// The foot that plays the ball: for a touch, the foot in swing at <see cref="ContactTick"/>; for a shot, the foot chosen for
    /// it (see <see cref="ShotModel.ChooseStrike"/>), fixed from the release on.
    /// </summary>
    public Foot Foot { get; internal set; }
    /// <summary>The part of the foot used (a shot's may be <see cref="FootSurface.Outside"/>; fixed with its foot).</summary>
    public FootSurface Surface { get; internal set; }
    /// <summary>World tick the action was started on (a touch: committed, one windup before its contact).</summary>
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
    /// <summary>
    /// For a sprint push that was cut short because an opponent ahead (the keeper) could reach the path: the share of the
    /// push that was cut, above 0 up to 1 (cut to the fewest steps); 0 for every other touch.
    /// </summary>
    public float DangerCut { get; internal set; }
    /// <summary>True once the contact was made; false while waiting and for a touch not played (the ball out of reach at its contact).</summary>
    public bool Made { get; internal set; }
    /// <summary>Horizontal distance from the ball centre to <see cref="ContactPoint"/> at contact, metres (NaN before).</summary>
    public float ContactMiss { get; internal set; } = float.NaN;

    // ---- skill moves ----
    /// <summary>
    /// True for a skill move (<see cref="ActionKind.StepOver"/> to <see cref="ActionKind.FakeCut"/>, and
    /// <see cref="ActionKind.Elastico"/> to <see cref="ActionKind.HeelToHeel"/>).
    /// </summary>
    public bool IsSkill => Kind is >= ActionKind.StepOver and <= ActionKind.FakeCut or >= ActionKind.Elastico and <= ActionKind.HeelToHeel;
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
    /// <summary>
    /// For a skill move with two contacts (elastico, spin, heel-to-heel), the world tick of the second contact, fixed from the
    /// move's start (a Touch event is raised on it as on <see cref="ContactTick"/>); -1 for every other action.
    /// </summary>
    public int Contact2Tick { get; internal set; } = -1;
    /// <summary>The foot that plays the second contact (the same foot for the elastico, the other foot for the spin and heel-to-heel).</summary>
    public Foot Foot2 { get; internal set; }
    /// <summary>The part of <see cref="Foot2"/> that meets the ball at the second contact.</summary>
    public FootSurface Surface2 { get; internal set; }
    /// <summary>For the spin, the world tick the first sole lifts off the ball (before <see cref="Contact2Tick"/>); -1 otherwise.</summary>
    public int LiftTick { get; internal set; } = -1;
    // ---- shots ----
    /// <summary>
    /// True for a shot (<see cref="ActionKind.Power"/> to <see cref="ActionKind.LowFinesse"/>). A shot is
    /// <see cref="ActionPhase.Windup"/> from the shoot press (<see cref="PlanTick"/>) until contact; its
    /// <see cref="ContactTick"/> is the earliest possible strike while shoot is held, fixed at the release, and final at contact
    /// (later when the ball is not yet in reach then, or the chosen foot's swing comes later). <see cref="Kind"/> follows the modifiers held until the release. Then
    /// Contact, Recovery until <see cref="EndTick"/>, and Done. A shot dropped (faked, or the ball never came into reach) goes
    /// to Done with <see cref="Made"/> false.
    /// </summary>
    public bool IsShot => Kind is >= ActionKind.Power and <= ActionKind.LowFinesse;
    /// <summary>For a shot action: the kind of pass it is (drawn as a side-foot kick), or None for a shot at goal.</summary>
    public PassKind Pass { get; internal set; }
    /// <summary>
    /// For a shot: true once shoot was released (charge, type and aim fixed, <see cref="ContactTick"/> final unless the ball is
    /// still out of reach then); false while shoot is held. Always false for touches and skill moves.
    /// </summary>
    public bool Released { get; internal set; }
    /// <summary>
    /// For a shot: the direction the ball is meant to leave in, radians of world yaw (0 = +X, positive toward +Z), after the aim
    /// assist and before any error. While shoot is held it follows the stick; it is set at the release and fixed from then on.
    /// Zero for touches and skill moves.
    /// </summary>
    public float AimYaw { get; internal set; }

    /// <summary>For a skill move, the unit ground direction (X, Z) the player was running in when it started; zero for touches.</summary>
    public Vector2 RunDirection => IsSkill ? HoldDirection : Vector2.Zero;
    /// <summary>
    /// A <see cref="ActionKind.DragBack"/> started by the touch controller as its own turn at the feet, not a skill gesture of the
    /// player's (no chain, cooldown or exit burst of a skill move, and its own short timing).
    /// </summary>
    public bool FeetTurn { get; internal set; }
    /// <summary>
    /// A <see cref="ActionKind.Spin"/> played as the McGeady spin: the sole on the side he comes out to drags the ball behind him,
    /// he turns away from that side through three quarters of a circle, and the other foot's outside takes the ball out square to
    /// <see cref="Side"/> (the plain spin turns a full circle toward its side and runs on).
    /// </summary>
    public bool McGeady { get; internal set; }

    // ---- touch controller state ----
    /// <summary>For a skill move, the unit ground direction (X, Z) the player was running in when it started.</summary>
    internal Vector2 HoldDirection;
    /// <summary>The stick when the touch was committed (zero: released); at contact the ball is played within 30 degrees of it.</summary>
    internal Vector2 CommitMove;
    /// <summary>The stick at contact was more than 30 degrees from <see cref="CommitMove"/> and the touch was played at that limit.</summary>
    internal bool Clamped;
    /// <summary>Played with the leg stretched beyond its reach (heavier errors): see TouchPlanner.Execute.</summary>
    internal bool Stretched;
    /// <summary>A touch committed on the go-get-it legs (they carry it to the contact), not the stick's.</summary>
    internal bool ChaseLegs;
    // Skill move state (HoldDirection is the running direction the move started on).
    /// <summary>Running speed along that direction when the move started, m/s.</summary>
    internal float SkillEntry;
    /// <summary>Where the ball was when the move started and where the sole leaves it.</summary>
    internal Vector3 SkillBallFrom, SkillBallTo;
    /// <summary>Ball roll, elastico, heel-to-heel: the ball's distance ahead of the player along the run when the sole landed, metres.</summary>
    internal float RollAhead0;
    /// <summary>Elastico, ball roll: the ball's distance toward the move's side from the player when it was first played, metres.</summary>
    internal float SideOffset0;
    /// <summary>Spin: the ball against the player in his body frame (ahead, to his right) at the first contact, metres.</summary>
    internal Vector2 SpinBall0;
    /// <summary>Spin: the player's facing at the first contact, radians, and his ground velocity planned for the turn (see SkillMoves).</summary>
    internal float SpinFacing0;
    internal Vector2 SpinVel0;
    internal float SpinForward, SpinSideBump;
    /// <summary>Where the carried ball was sent on the latest tick, and how far from it the ball was at the second contact, metres (NaN before).</summary>
    internal Vector3 LastTarget;
    internal float Contact2PathMiss = float.NaN;
    /// <summary>Elastico: the exit burst's scale for the way its second contact played the ball on (NaN before it).</summary>
    internal float ExitBurstScale = float.NaN;
    // ---- a sheet-driven move (see SkillMoves, "the follower") ----
    /// <summary>The blended move sheet the simulation follows for this move; null for a move played by its legacy formulas.</summary>
    public SheetPlay? Sheet { get; internal set; }
    /// <summary>The sheet time the move starts at (the sheet's start, or later for a reach much shorter than the sheet's), seconds.</summary>
    internal float SheetS0;
    /// <summary>The nudge that brings the sheet's contact point onto the real ball (world X, Z, metres), how much of it has been made, and its pace now.</summary>
    internal Vector2 SheetCorrection, SheetApplied, SheetCorrVel;
    /// <summary>For a sheet-driven move: the nudge planned at its start to bring the sheet's contact point onto the ball (world X, Z, metres), and how much of it has been made.</summary>
    public Vector2 MeetNudge => SheetCorrection;
    public Vector2 MeetNudgeMade => SheetApplied;
    /// <summary>For a sheet-driven move: what the ball was off the current leg's track on the leg's first tick (world, metres); it fades over the leg.</summary>
    public Vector3 MeetBias => LegBias;
    /// <summary>His velocity over the sheet's own as the move started (world X, Z); it eases out. And the sheet's velocity on the tick before.</summary>
    internal Vector2 SheetResidual, SheetPlanPrev;
    /// <summary>His facing over the sheet's as the move started, radians; it eases out.</summary>
    internal float SheetFacingOff;
    internal bool SheetBegun;
    /// <summary>The ball leg under way (-1 before the first), where the ball was on its first tick, and what the ball was off the leg's track then (it fades over the leg).</summary>
    internal int SheetLeg = -1;
    internal Vector3 LegBall0, LegBias;
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
            return tick < ContactTick ? ActionPhase.Windup : tick == ContactTick || tick == Contact2Tick ? ActionPhase.Contact : ActionPhase.Recovery;
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
