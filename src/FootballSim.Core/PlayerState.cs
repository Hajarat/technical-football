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
    /// <summary>For keepers: the save motion while Diving (None otherwise).</summary>
    public KeeperDive KeeperDive { get; internal set; }
    /// <summary>For keepers: seconds since <see cref="KeeperAction"/> last changed.</summary>
    public float KeeperActionTime { get; internal set; }
    /// <summary>For keepers: where the hands are this tick, so a view can lean the body. For outfield players, the feet position.</summary>
    public Vector3 HandPosition { get; internal set; }
    /// <summary>
    /// For keepers: where the brain expects the ball to meet his hands, body or feet in the current save, smother or slide (the
    /// point he aims at). Written for views only; nothing in the core reads it. Meaningful while <see cref="SaveContactTime"/> is a number.
    /// </summary>
    public Vector3 SaveTarget { get; internal set; }
    /// <summary>
    /// For keepers: seconds after the current action began (see <see cref="KeeperActionTime"/>) at which that meeting is predicted,
    /// or NaN when no save, smother or slide is under way. Written for views only; nothing in the core reads it.
    /// </summary>
    public float SaveContactTime { get; internal set; } = float.NaN;

    // ---- Contract for the on-the-ball rework (possession, feet). The game layer animates from these. ----

    /// <summary>True while this player controls the ball (dribbling it). False when the ball is loose or someone else's.</summary>
    public bool InPossession { get; internal set; }
    /// <summary>
    /// Seconds until this player's next expected touch of the ball: the committed touch's contact (the pending
    /// <see cref="Action"/>), else the contact the touch controller steers for (updated every tick), or NaN when none is expected.
    /// </summary>
    public float NextTouchEta { get; internal set; } = float.NaN;
    /// <summary>The foot expected to make that touch. Meaningful only when <see cref="NextTouchEta"/> is a number.</summary>
    public Foot NextTouchFoot { get; internal set; }
    /// <summary>Where the ball centre is expected to be at that touch (the pending action's <see cref="BallAction.ContactPoint"/>).</summary>
    public Vector3 NextTouchPoint { get; internal set; }
    /// <summary>
    /// Seconds until the shot under way makes contact with the ball, from the press on: the best estimate, the later of the
    /// earliest strike (the wind-up after the press, the lead after the release; while shoot is held, as if it were let go on the
    /// next tick) and the contact the touch controller steers for (the committed strike's once it is committed). NaN when no shot
    /// is under way.
    /// </summary>
    public float StrikeEta { get; internal set; } = float.NaN;
    /// <summary>
    /// The foot that will strike (the best estimate, like <see cref="StrikeEta"/>), or struck last. Meaningful from the press, through
    /// the strike delay and just after.
    /// </summary>
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
    /// The current action. While a shot is under way (from the shoot press to the end of its recovery) it is the shot, of kind
    /// Power, Finesse, Chip, Driven or LowFinesse. Otherwise the planned touch waiting for its contact if there is one, or the
    /// latest touch or skill move (in its recovery, or done). Null before the first. Its <see cref="BallAction.Phase"/> is
    /// updated every tick.
    /// </summary>
    public BallAction? Action => ShotAction ?? TouchAction;
    /// <summary>
    /// The touch made before <see cref="Action"/> when that one is still waiting, so its follow-through can be drawn while the
    /// next touch winds up; otherwise null. Always null while a shot is under way.
    /// </summary>
    public BallAction? PreviousAction => ShotAction != null ? null : PrevTouchAction;
    /// <summary>
    /// The touch planner's and skill moves' action, whatever <see cref="Action"/> shows: the touch waiting for its contact, or
    /// the latest touch or skill move (in its recovery, or done). A shot never goes here. While a shot winds up this is where
    /// its soft touch (kind Soft) is seen, from its plan through its recovery, so the view can draw that kick inside the shot.
    /// </summary>
    public BallAction? TouchAction { get; internal set; }
    /// <summary>The touch before <see cref="TouchAction"/> still in its follow-through, or null.</summary>
    internal BallAction? PrevTouchAction;
    /// <summary>Read-only view of the touch before <see cref="TouchAction"/> still in its follow-through, shots or not (for the view: <see cref="PreviousAction"/> hides it during a shot).</summary>
    public BallAction? PreviousTouchAction => PrevTouchAction;
    /// <summary>The shot under way, from the press to the end of its recovery, or null. Shown as <see cref="Action"/>.</summary>
    internal BallAction? ShotAction;
    /// <summary>Simulation seconds until which the shooter is stunned after a strike: slowed, and no touches.</summary>
    internal float StunUntil = -100f;
    /// <summary>Read-only view of <see cref="StunUntil"/> for the presentation (the simulation seconds the stun after a strike ends).</summary>
    public float StunEndTime => StunUntil;
    /// <summary>Seconds from now to <see cref="Action"/>'s contact while it waits, else NaN.</summary>
    public float ActionContactEta { get; internal set; } = float.NaN;
    /// <summary>The steering correction added to the velocity on the latest tick to bring the ball into the contact zone, m/s.</summary>
    public Vector3 SteerVelocity { get; internal set; }
    /// <summary>Forward running speed along the facing on the latest tick, without the steering correction, m/s.</summary>
    public float RunSpeed { get; internal set; }
    /// <summary>
    /// True while the legs run to the ball (the touch controller's go-get-it legs: the stick's own legs cannot bring the ball
    /// within reach at an eligible swing), false while they follow the stick.
    /// </summary>
    public bool Chasing { get; internal set; }
    /// <summary>How eagerly he goes for a loose ball this tick (see <see cref="Core.Regain"/>); None in possession.</summary>
    public RegainMode Regain { get; internal set; }

    // ---- Skill moves (phase B). ----

    /// <summary>The skill move under way (also <see cref="Action"/> while it lasts), or null.</summary>
    public BallAction? SkillMove => SkillAction;
    /// <summary>
    /// A fake shot read but not made yet: from the tick the cancel is read until the fake's contact the shot winds up on as a real one
    /// (it is still <see cref="Action"/>), and this is the move the fake will be, as the core expects it now (kind, side, foot,
    /// surface, its <see cref="BallAction.StartTick"/> the shoot press, its contact and end ticks; phase Windup, not made). On the
    /// contact tick the move itself starts with the same StartTick and this goes back to null. For the view only.
    /// </summary>
    public BallAction? PendingSkill { get; internal set; }
    /// <summary>
    /// While a skill move runs toward its end with the stick pushed: the exit touch the core expects to make at once on the move's
    /// end tick (which has no windup of its own), as an unmade touch in its windup: its foot, kind and surface, the ball's centre
    /// then as <see cref="BallAction.ContactPoint"/>, the stick's direction as the way out (<see cref="BallAction.NextMeetPoint"/>
    /// two metres along it), <see cref="BallAction.ContactTick"/> and <see cref="BallAction.PlanTick"/> the end tick (the exit
    /// touch has that PlanTick too, and so has the touch the controller commits with a windup on the end tick instead, when it
    /// does). Null when no such touch is expected. A forecast for the view only: the touch is decided on the end tick as usual.
    /// </summary>
    public BallAction? SkillExitTouch { get; internal set; }
    /// <summary>The most recent gesture recognised (right stick, skill key or fake shot), and its world tick.</summary>
    public Gesture LastGesture { get; internal set; }
    /// <summary>World tick of <see cref="LastGesture"/>.</summary>
    public int LastGestureTick { get; internal set; } = int.MinValue / 2;
    /// <summary>The effective skill_moves rating of the latest move (the rating times the chain multiplier per earlier move in the chain).</summary>
    public float SkillEffective { get; internal set; }
    /// <summary>How many moves came before the latest one in its chain (0 for the first).</summary>
    public int SkillChainCount { get; internal set; }

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
    /// <summary>Running speed still to be shed after a sharp turn or a cut (at balance_deceleration), m/s.</summary>
    internal float SpeedDebt;
    /// <summary>Simulation seconds of the last turn about without the ball (see reverse_turn_deg).</summary>
    internal float ReverseTime = -100f;
    /// <summary>Until this simulation time the turn rate is at least post_turn_rotation_rate (after a turn or cut touch).</summary>
    internal float PostTurnUntil = -100f;
    /// <summary>A braking arc under way (a sharp turn with the ball at a run): the speed he slows to and keeps until it ends, m/s; 0 when none.</summary>
    internal float TurnArcSpeed;
    /// <summary>The braking arc has had its turning touch: he brakes to <see cref="TurnArcSpeed"/> (armed before it, he runs on to the ball).</summary>
    internal bool TurnArcBraking;
    /// <summary>Out of a braking arc: he speeds up no faster than the arc braked (turn_arc_brake) until the speed law asks for less.</summary>
    internal bool TurnArcRelease;
    /// <summary>The stick the braking arc turns him onto (unit, world X, Z), the latest while it is held.</summary>
    internal Vector2 TurnArcStick;
    /// <summary>
    /// The touch controller's sideways steering correction last tick (world X, Z), m/s: it eases from there, and turns with the
    /// body (see <see cref="SteerFacing"/>); zero during a skill move.
    /// </summary>
    internal Vector2 PlannerSteer;
    /// <summary>The facing <see cref="PlannerSteer"/> was set against, radians.</summary>
    internal float SteerFacing;
    /// <summary>
    /// A skill move's step (world X, Z), m/s: eased in while the move lasts and eased out after it, on top of the planner's
    /// correction.
    /// </summary>
    internal Vector2 SteerCarry;
    internal float TurnRefHeading;
    internal float TurnRefTime;
    /// <summary>Ticks left of a stride-clock lock (0 = free running).</summary>
    internal int GaitLockTicks;
    /// <summary>Unwrapped phase the stride clock reaches when the lock runs out.</summary>
    internal float GaitLockTarget;
    /// <summary>The foot whose swing middle the stride clock crossed on the latest tick, or null.</summary>
    internal Foot? SwingMiddleCrossed;
    /// <summary>
    /// The shot under way, for the touch controller: the earliest tick its strike may fall on (-1: no shot), whether the strike
    /// may be committed (released, and the ball not held at the feet for a strike at the earliest tick), and the committed
    /// strike's contact tick and foot (-1: none committed).
    /// </summary>
    internal int ShotEarliestTick = -1;
    internal bool ShotCommitAllowed;
    /// <summary>The strike is never forecast sooner than this tick (one tick before the latest estimate published), or -1.</summary>
    internal int ShotNotBeforeTick = -1;
    internal int StrikeContactTick = -1;
    internal Foot StrikeContactFoot;
    /// <summary>Kind of the most recent touch made (decides whether the ball may be lost by distance).</summary>
    internal ActionKind LastTouchKind = ActionKind.First;
    /// <summary>Steps the stride clock has begun since the player's last contact with the ball.</summary>
    internal int StepsSinceTouch;
    /// <summary>True when the stride clock began a new step on the latest tick.</summary>
    internal bool StepBegun;
    /// <summary>The steps the last push was solved for (1 after a touch that is not a push): the touch controller's cooldown.</summary>
    internal int PushSteps = 1;
    /// <summary>World tick of the next contact the touch controller expects (the committed touch's, else the swing it steers for), or -1.</summary>
    internal int ForecastTick = -1;
    /// <summary>The foot and the ball centre expected at <see cref="ForecastTick"/>.</summary>
    internal Foot ForecastFoot;
    internal Vector3 ForecastPoint;
    internal void SetForecast(int tick, Foot foot, Vector3 point) { ForecastTick = tick; ForecastFoot = foot; ForecastPoint = point; }
    internal void ClearForecast() => ForecastTick = -1;
    /// <summary>Stick direction on the previous tick, radians (NaN with the stick released).</summary>
    internal float StickAnglePrev = float.NaN;
    /// <summary>How fast the stick is being turned smoothly, radians per second (0 after a jump or a release).</summary>
    internal float StickRate;
    /// <summary>Ticks the smooth stick turn rate has been non-zero without a break (caps how far it is carried on).</summary>
    internal int StickTurnTicks;
    /// <summary>The stick direction its last change was measured from, radians (NaN with the stick released).</summary>
    internal float StickSettledAngle = float.NaN;
    /// <summary>The tick the stick last turned (smoothly or by a jump) or sprint was last pressed or let go.</summary>
    internal int StickChangeTick = -100000;
    /// <summary>Whether sprint was held on the previous tick.</summary>
    internal bool SprintPrev;
    /// <summary>Ticks the stick has been released without a break (0 while pushed).</summary>
    internal int StickReleasedTicks;
    /// <summary>The stick when last pushed (zero before any push this round): a short release reads as this.</summary>
    internal Vector2 LastStickMove;
    /// <summary>A knock-on was asked for; the next touch made is one.</summary>
    internal bool KnockOnRequested;
    /// <summary>Touches pushed with an even steps_per_touch this round, for the foot switch (see foot_switch_every).</summary>
    internal int TouchCadenceCount;

    // Gesture reader (see GestureReader).
    internal GestureReader.Zone GZone, GPrevZone;
    /// <summary>World tick the right stick entered its current zone.</summary>
    internal int GZoneStart;
    /// <summary>Latest world tick the right stick was forward.</summary>
    internal int GFwdLastTick = int.MinValue / 2;
    /// <summary>World tick the right stick last entered forward, and whether it came there from neutral.</summary>
    internal int GFwdStart = int.MinValue / 2;
    internal bool GFwdFromNeutral;
    /// <summary>Latest world tick the right stick was back (the start of an elastico).</summary>
    internal int GBackLastTick = int.MinValue / 2;
    /// <summary>The last two directions the right stick held before its current one, newest first (in-between zones skipped).</summary>
    internal GestureReader.Zone GHist0, GHist1;
    /// <summary>The left stick back with both shoulder buttons held, on the previous tick (the drag-back fires on the change).</summary>
    internal bool GDragHeld;
    /// <summary>The side held now has already given a step-over or a roll.</summary>
    internal bool GSideConsumed;
    /// <summary>The forward push held now has already armed a knock-on.</summary>
    internal bool GKnockUsed;
    /// <summary>World tick a knock-on was armed and is waiting to fire, or -1.</summary>
    internal int GKnockWaitTick = -1;
    /// <summary>The cancel button on the previous tick.</summary>
    internal bool GPrevCancel;

    // Skill moves (see SkillMoves).
    /// <summary>The move under way, or null.</summary>
    internal BallAction? SkillAction;
    /// <summary>A recognised move waiting for the ball to come into reach: kind, side, cut direction and the tick it was made (-1: none).</summary>
    internal Gesture SkillBufKind;
    internal SkillSide SkillBufSide;
    internal Vector2 SkillBufCut;
    internal int SkillBufTick = -1;
    /// <summary>For a buffered fake shot, the tick shoot was pressed (its move starts at once, its contact on the buffered tick); -1 otherwise.</summary>
    internal int SkillBufFakeFrom = -1;
    /// <summary>World tick a move ended on, when the touch planner may make the exit touch at once; -1 otherwise.</summary>
    internal int SkillExitTick = -1;
    /// <summary>World tick a heel-to-heel or elastico ended on, when its exit burst starts (its second contact was the exit touch); -1 otherwise.</summary>
    internal int SkillBurstAtTick = -1;
    /// <summary>Simulation seconds before which no move starts (the drag-back's cooldown).</summary>
    internal float SkillCooldownUntil = -100f;
    /// <summary>Simulation seconds until which a new move continues the chain.</summary>
    internal float SkillChainUntil = -100f;
    /// <summary>The exit burst of the latest move waits for the exit touch until this time ...</summary>
    internal float SkillBoostUntil = -100f;
    /// <summary>... then divides the time constant by this ...</summary>
    internal float SkillBoostFactor = 1f;
    /// <summary>... for this long, seconds ...</summary>
    internal float SkillBoostDuration;
    /// <summary>... and raises the with-ball speed cap by this fraction of top speed.</summary>
    internal float SkillBoostCap;
    /// <summary>The exit burst under way lasts until this time, through any touches made meanwhile ...</summary>
    internal float SkillBurstUntil = -100f;
    /// <summary>... dividing the time constant by this (or the touch boost's factor, if larger) ...</summary>
    internal float SkillBurstFactor = 1f;
    /// <summary>... and raising the with-ball speed cap by this fraction of top speed.</summary>
    internal float SkillBurstCap;
    PlayerState? _scratch;

    /// <summary>A reusable copy of this player for predictions (never part of a world).</summary>
    internal PlayerState Scratch() => (_scratch ??= new PlayerState(Team, Index, Role, Sheet)).CopyFrom(this);

    /// <summary>Copies every value that moving the player reads or writes from <paramref name="o"/>.</summary>
    internal PlayerState CopyFrom(PlayerState o)
    {
        Position = o.Position; Velocity = o.Velocity; Facing = o.Facing; HandPosition = o.HandPosition;
        LastTouchTime = o.LastTouchTime; TouchBoostUntil = o.TouchBoostUntil; TouchBoostFactor = o.TouchBoostFactor;
        TouchBoostFrom = o.TouchBoostFrom; KnockOnFrom = o.KnockOnFrom; KnockOnDirection = o.KnockOnDirection;
        KnockOnUntil = o.KnockOnUntil; MoveTime = o.MoveTime; SpeedDebt = o.SpeedDebt; ReverseTime = o.ReverseTime; PostTurnUntil = o.PostTurnUntil; TurnArcSpeed = o.TurnArcSpeed; TurnArcBraking = o.TurnArcBraking; TurnArcRelease = o.TurnArcRelease; TurnArcStick = o.TurnArcStick; PlannerSteer = o.PlannerSteer; SteerFacing = o.SteerFacing; SteerCarry = o.SteerCarry; TurnRefHeading = o.TurnRefHeading; TurnRefTime = o.TurnRefTime;
        GaitPhase = o.GaitPhase; StepFrequency = o.StepFrequency; GaitLockTicks = o.GaitLockTicks; GaitLockTarget = o.GaitLockTarget;
        SteerVelocity = o.SteerVelocity; RunSpeed = o.RunSpeed; CurrentTick = o.CurrentTick; SwingMiddleCrossed = o.SwingMiddleCrossed; StepBegun = o.StepBegun;
        LastTouchKind = o.LastTouchKind; StunUntil = o.StunUntil; ShotAction = o.ShotAction;
        GZone = o.GZone; GPrevZone = o.GPrevZone; GZoneStart = o.GZoneStart; GFwdLastTick = o.GFwdLastTick;
        GFwdStart = o.GFwdStart; GFwdFromNeutral = o.GFwdFromNeutral; GBackLastTick = o.GBackLastTick;
        GHist0 = o.GHist0; GHist1 = o.GHist1; GDragHeld = o.GDragHeld;
        GSideConsumed = o.GSideConsumed; GKnockUsed = o.GKnockUsed; GKnockWaitTick = o.GKnockWaitTick; GPrevCancel = o.GPrevCancel;
        LastGesture = o.LastGesture; LastGestureTick = o.LastGestureTick; SkillEffective = o.SkillEffective; SkillChainCount = o.SkillChainCount;
        SkillAction = o.SkillAction; SkillBufKind = o.SkillBufKind; SkillBufSide = o.SkillBufSide; SkillBufCut = o.SkillBufCut;
        SkillBufTick = o.SkillBufTick; SkillBufFakeFrom = o.SkillBufFakeFrom; SkillExitTick = o.SkillExitTick; SkillBurstAtTick = o.SkillBurstAtTick; SkillCooldownUntil = o.SkillCooldownUntil; SkillChainUntil = o.SkillChainUntil;
        SkillBoostUntil = o.SkillBoostUntil; SkillBoostFactor = o.SkillBoostFactor; SkillBoostDuration = o.SkillBoostDuration;
        SkillBoostCap = o.SkillBoostCap; SkillBurstUntil = o.SkillBurstUntil; SkillBurstFactor = o.SkillBurstFactor; SkillBurstCap = o.SkillBurstCap;
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
