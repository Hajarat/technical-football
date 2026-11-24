using System.Numerics;

namespace FootballSim.Core;

/// <summary>
/// What a right-stick gesture, a skill key or a cancelled shot asks for (<see cref="SpinAndGo"/> is the spin, a full turn that
/// runs on; <see cref="McGeady"/> the McGeady spin, a drag behind and a turn away that comes out square to the side).
/// </summary>
public enum Gesture { None, StepOver, BodyFeint, BallRoll, DragBack, KnockOn, FakeStop, FakeCut, Elastico, HeelToHeel, SpinAndGo, McGeady }

/// <summary>A recognised gesture: its kind, its side (relative to the running direction) and, for a fake cut, the cut direction.</summary>
/// <param name="CutDirection">Unit ground direction (X, Z) of a fake cut (the left stick); zero otherwise.</param>
public readonly record struct GestureRead(Gesture Kind, SkillSide Side, Vector2 CutDirection)
{
    public static GestureRead None => default;
}

/// <summary>
/// Reads skill-move gestures tick by tick. Each move is a short list of steps for one stick: a direction (or the stick at
/// rest), how long it must be held, how soon after the step before it must begin and how far off the direction it may be.
/// Every move is followed at once, each sided move in both its versions. Directions are taken against the player's facing,
/// fixed for a gesture when its first direction step completes, so turning during the gesture does not bend it. The stick
/// counts as pushed from min_actuation; below that it is at rest.
/// <list type="bullet">
/// <item>Drag-back: the left stick back (drag_back_stick_deg or more from his facing) with both shoulder buttons held
/// (<see cref="TickIntent.Finesse"/> and <see cref="TickIntent.Chip"/>).</item>
/// <item>Ball roll: a side held roll_min_hold.</item>
/// <item>Step-over: forward, then round to a side (within step_over_side_tolerance_deg of square) within roll_step_window,
/// without the stick coming to rest on the way.</item>
/// <item>Heel-to-heel: a forward flick (held flick_hold), rest, then a back flick, each step within combo_step_window.</item>
/// <item>Spin: a forward flick, rest, then a side flick (the side within roll_step_window).</item>
/// <item>Elastico: back, then round to a side as for the step-over.</item>
/// <item>Body feint: a side flick and back to rest within combo_step_window (held on, it is a ball roll).</item>
/// <item>McGeady spin: a back flick, rest, then a flick to the side he comes out to.</item>
/// </list>
/// A completed gesture registers unless sprint is held or another registered within gesture_cooldown before it (it is then
/// dropped, not kept); when two complete on one tick the first in the list above wins. A registered gesture stays held for
/// gesture_cooldown (<see cref="Held"/>). A skill key registers its move directly, sprinting or not.
/// <para>
/// With sprint held the right stick asks for a heavy touch instead (<see cref="ReadHeavyTouch"/>), and a cancelled shot is a
/// fake (<see cref="ReadFakeShot"/>). Reading draws no random numbers and changes nothing but the reader's own state, so a
/// stick at rest leaves the simulation exactly as it was.
/// </para>
/// </summary>
public static class GestureReader
{
    /// <summary>A step's direction, against the gesture's forward; <see cref="Rest"/> is the stick below the actuation.</summary>
    internal enum Dir : byte { Rest, Forward, Left, Right, Back }

    /// <summary>How long a step is held: not at all, a flick (flick_hold) or the ball roll's hold (roll_min_hold).</summary>
    enum HoldOf : byte { None, Flick, Roll }

    /// <summary>How soon after the step before it a step must begin: combo_step_window or roll_step_window.</summary>
    enum WithinOf : byte { Step, Long }

    /// <summary>How far off its direction a step may be: angle_tolerance_deg, step_over_side_tolerance_deg, or the drag-back's.</summary>
    enum TolOf : byte { Default, Tight, Drag }

    readonly record struct Section(Dir Dir, HoldOf Hold = HoldOf.None, WithinOf Within = WithinOf.Step, TolOf Tol = TolOf.Default);

    /// <param name="Flip">The move has a mirrored version (left and right swapped).</param>
    /// <param name="Side">The side the unmirrored version gives.</param>
    /// <param name="LeftStick">Read from the left stick, against the facing of the moment.</param>
    /// <param name="Shoulders">Needs both shoulder buttons held.</param>
    sealed record Combo(Gesture Kind, bool Flip, SkillSide Side, bool LeftStick, bool Shoulders, Section[] Steps);

    /// <summary>One followed gesture (a move, plain or mirrored): where it is in its steps.</summary>
    internal struct Progress
    {
        /// <summary>Index of the step being waited for.</summary>
        public int Section;
        /// <summary>World tick the step before completed (0: no clock runs).</summary>
        public int PrevDone;
        /// <summary>World tick the current step was entered (0: not yet).</summary>
        public int Entered;
        /// <summary>The gesture's forward is fixed at <see cref="RefYaw"/>.</summary>
        public bool Latched;
        public float RefYaw;
    }

    static readonly Section Rest = new(Dir.Rest);

    // In the order they win when two complete on one tick.
    static readonly Combo[] Combos =
    {
        new(Gesture.DragBack, false, SkillSide.None, true, true, new[] { new Section(Dir.Back, Tol: TolOf.Drag) }),
        new(Gesture.BallRoll, true, SkillSide.Right, false, false, new[] { new Section(Dir.Right, HoldOf.Roll) }),
        new(Gesture.StepOver, true, SkillSide.Right, false, false,
            new[] { new Section(Dir.Forward), new Section(Dir.Right, HoldOf.None, WithinOf.Long, TolOf.Tight) }),
        new(Gesture.HeelToHeel, false, SkillSide.None, false, false,
            new[] { Rest, new Section(Dir.Forward, HoldOf.Flick), Rest, new Section(Dir.Back, HoldOf.Flick) }),
        new(Gesture.SpinAndGo, true, SkillSide.Right, false, false,
            new[] { Rest, new Section(Dir.Forward, HoldOf.Flick), Rest, new Section(Dir.Right, HoldOf.Flick, WithinOf.Long) }),
        // The elastico goes out to the side its stick ends on.
        new(Gesture.Elastico, true, SkillSide.Left, false, false,
            new[] { new Section(Dir.Back), new Section(Dir.Left, HoldOf.None, WithinOf.Long, TolOf.Tight) }),
        new(Gesture.BodyFeint, true, SkillSide.Right, false, false, new[] { Rest, new Section(Dir.Right, HoldOf.Flick), Rest }),
        new(Gesture.McGeady, true, SkillSide.Right, false, false,
            new[] { Rest, new Section(Dir.Back, HoldOf.Flick), Rest, new Section(Dir.Right, HoldOf.Flick, WithinOf.Long) }),
    };

    /// <summary>Followed gestures: every move, plain and mirrored (<see cref="PlayerState.GProg"/>'s length).</summary>
    internal const int ProgressCount = 16;

    static int Ticks(float seconds, Tuning t) => Math.Max(0, (int)MathF.Round(seconds * t.TickRate));

    /// <summary>Clears the reader (a new round, a new controlled player).</summary>
    internal static void Reset(PlayerState p)
    {
        Array.Clear(p.GProg);
        p.GDetKind = Gesture.None;
        p.GDetSide = SkillSide.None;
        p.GDetTick = 0;
        p.GPrevRight = Vector2.Zero;
        p.GHeavyTick = -1;
        p.GHeavyDir = Vector2.Zero;
        p.GPrevCancel = false;
        p.LastGesture = Gesture.None;
        p.LastGestureTick = int.MinValue / 2;
    }

    /// <summary>A move's own forward: the running direction when moving, else the facing (unit X, Z).</summary>
    public static Vector2 FrameForward(PlayerState p, Tuning t)
    {
        var v = new Vector2(p.Velocity.X, p.Velocity.Z);
        float speed = v.Length();
        return speed > t.Skill.FrameMinSpeed ? v / speed : new Vector2(MathF.Cos(p.Facing), MathF.Sin(p.Facing));
    }

    /// <summary>The player's right for a forward direction: facing +X, the right is +Z.</summary>
    internal static Vector2 RightOf(Vector2 fwd) => new(-fwd.Y, fwd.X);

    /// <summary>The side a skill key goes to: the left stick's sideways deflection in the player's frame, right by default.</summary>
    internal static SkillSide KeySide(in TickIntent it, Vector2 fwd, Tuning t) =>
        Vector2.Dot(it.Move, RightOf(fwd)) < -t.Skill.KeySideThreshold ? SkillSide.Left : SkillSide.Right;

    static int HoldTicks(in Section s, Tuning t) => s.Hold switch
    {
        HoldOf.Flick => Ticks(t.Skill.FlickHold, t),
        HoldOf.Roll => Ticks(t.Skill.RollMinHold, t),
        _ => 0,
    };

    static int WithinTicks(in Section s, Tuning t) => Ticks(s.Within == WithinOf.Long ? t.Skill.RollStepWindow : t.Skill.ComboStepWindow, t);

    static float ToleranceDeg(in Section s, Tuning t) => s.Tol switch
    {
        TolOf.Tight => t.Skill.StepOverSideToleranceDeg,
        TolOf.Drag => 180f - t.Skill.DragBackStickDeg,
        _ => t.Skill.AngleToleranceDeg,
    };

    /// <summary>A step's direction in degrees from forward, positive toward the right; the mirrored version swaps the sides.</summary>
    static float TargetDeg(Dir d, bool flip) => d switch
    {
        Dir.Forward => 0f,
        Dir.Back => 180f,
        Dir.Right => flip ? -90f : 90f,
        _ => flip ? 90f : -90f,
    };

    /// <summary>
    /// The gesture starts over. After a failed step that followed a rest, a gesture that opens with a rest goes straight back
    /// to its first direction step, as if the opening rest had just been met.
    /// </summary>
    static void Rearm(ref Progress r, Combo c)
    {
        bool afterRest = r.Section > 0 && c.Steps[r.Section - 1].Dir == Dir.Rest && c.Steps[0].Dir == Dir.Rest;
        r = default;
        if (afterRest) r.Section = 1;
    }

    /// <summary>Moves one followed gesture on by a tick. True on the tick its last step completes.</summary>
    static bool Step(ref Progress r, Combo c, bool flip, in TickIntent it, float facing, int now, Tuning t)
    {
        if (c.Shoulders && !(it.Finesse && it.Chip)) { Rearm(ref r, c); return false; }
        var steps = c.Steps;
        Vector2 stick = c.LeftStick ? it.Move : it.Right;
        bool pushed = stick.Length() >= t.Skill.MinActuation;

        // Too slow to begin this step: the gesture starts over (and may begin again on this tick).
        if (r.PrevDone != 0 && r.Entered == 0 && now > r.PrevDone + WithinTicks(steps[r.Section], t)) r = default;

        int i = r.Section;
        Section s = steps[i];
        bool isDir = s.Dir != Dir.Rest;
        bool inTol = false;
        if (isDir && pushed)
        {
            float yaw = c.LeftStick || !r.Latched ? facing : r.RefYaw;
            float cos = MathF.Cos(yaw), sin = MathF.Sin(yaw);
            float deg = MathF.Atan2(stick.Y * cos - stick.X * sin, stick.X * cos + stick.Y * sin) * 180f / MathF.PI;
            float off = MathF.Abs(deg - TargetDeg(s.Dir, flip));
            if (off > 180f) off = 360f - off;
            inTol = off <= ToleranceDeg(s, t);
        }

        // Leaving the step early: a rest pushed out of, two directions in a row not rolled straight through (the stick came
        // to rest between them), or a direction let go of or left.
        if (!isDir && r.Entered != 0 && pushed) { Rearm(ref r, c); return false; }
        if (isDir && r.Entered == 0 && i > 0 && steps[i - 1].Dir != Dir.Rest && !pushed) { r = default; return false; }
        if (isDir && r.Entered != 0 && !inTol) { Rearm(ref r, c); return false; }

        if (r.Entered == 0 && (isDir ? inTol : !pushed)) r.Entered = now;
        if (r.Entered == 0 || now < r.Entered + HoldTicks(s, t)) return false;
        if (i == 0 && !isDir)
        {
            // The opening rest starts no clock: the first flick may come any time after it.
            r.Section = 1; r.PrevDone = 0; r.Entered = 0;
            return false;
        }
        r.Section = i + 1; r.PrevDone = now; r.Entered = 0;
        if (!r.Latched) { r.Latched = true; r.RefYaw = facing; }
        if (r.Section < steps.Length) return false;
        r = default;
        return true;
    }

    /// <summary>
    /// Reads this tick's sticks, shoulder buttons and skill key. Returns the gesture registered on this tick, or
    /// <see cref="GestureRead.None"/>. Call once per tick for the player, before he moves.
    /// </summary>
    public static GestureRead Read(PlayerState p, in TickIntent it, int tick, Tuning t)
    {
        var k = t.Skill;
        if (tick <= 1) Reset(p);
        bool sprinting = it.Sprint >= t.Touch.SprintThreshold;
        int cooldown = Ticks(k.GestureCooldown, t);
        bool free = p.GDetTick == 0 || tick > p.GDetTick + cooldown;
        var found = GestureRead.None;
        for (int c = 0; c < Combos.Length; c++)
        {
            var combo = Combos[c];
            for (int f = 0; f < (combo.Flip ? 2 : 1); f++)
            {
                if (!Step(ref p.GProg[2 * c + f], combo, f == 1, it, p.Facing, tick, t)) continue;
                if (!free || sprinting || found.Kind != Gesture.None) continue;
                var side = combo.Side == SkillSide.None ? SkillSide.None
                    : (combo.Side == SkillSide.Right) != (f == 1) ? SkillSide.Right : SkillSide.Left;
                found = new GestureRead(combo.Kind, side, Vector2.Zero);
            }
        }

        // A skill key skips the stick (its side from the left stick).
        if (it.SkillKey != SkillKey.None && free)
        {
            var kind = it.SkillKey switch
            {
                SkillKey.StepOver => Gesture.StepOver,
                SkillKey.BodyFeint => Gesture.BodyFeint,
                SkillKey.BallRoll => Gesture.BallRoll,
                _ => Gesture.DragBack,
            };
            found = new GestureRead(kind, kind == Gesture.DragBack ? SkillSide.None : KeySide(it, FrameForward(p, t), t), Vector2.Zero);
        }

        if (found.Kind == Gesture.None) return GestureRead.None;
        p.GDetKind = found.Kind;
        p.GDetSide = found.Side;
        p.GDetTick = tick;
        return Commit(p, found, tick);
    }

    /// <summary>
    /// The registered gesture while it is still held: from the tick it registered for gesture_cooldown. The step-over and the
    /// ball roll may start on any of those ticks; the other moves only on the first (the one <see cref="Read"/> returns it on).
    /// </summary>
    public static GestureRead Held(PlayerState p, int tick, Tuning t) =>
        p.GDetTick != 0 && tick >= p.GDetTick && tick <= p.GDetTick + Ticks(t.Skill.GestureCooldown, t)
            ? new GestureRead(p.GDetKind, p.GDetSide, Vector2.Zero) : GestureRead.None;

    /// <summary>
    /// The heavy touch: with sprint held, the right stick flicked past knock_on_actuation (below it on the tick before) arms
    /// one, aimed where the stick points; let go of, the aim is where it pointed last. His next touch is the heavy one, along
    /// that aim (see <see cref="PlayerState.KnockOnRequested"/>). It is dropped when sprint is let go of or
    /// heavy_touch_max_delay passes with no touch. Call every tick (it tracks the stick). True on the tick one is armed.
    /// </summary>
    /// <param name="canArm">Play is live and he has the ball, or his team's claim on a loose one.</param>
    public static bool ReadHeavyTouch(PlayerState p, in TickIntent it, int tick, bool canArm, Tuning t)
    {
        var k = t.Skill;
        Vector2 now = it.Right, prev = p.GPrevRight;
        p.GPrevRight = now;
        float nowLen = now.Length(), prevLen = prev.Length();
        bool sprinting = it.Sprint >= t.Touch.SprintThreshold;
        if (p.GHeavyTick >= 0)
        {
            if (!p.KnockOnRequested) p.GHeavyTick = -1; // played, or dropped with the ball
            else if (!sprinting || tick - p.GHeavyTick > Ticks(k.HeavyTouchMaxDelay, t)) { p.GHeavyTick = -1; p.KnockOnRequested = false; }
            else if (nowLen < k.KnockOnActuation && prevLen >= k.KnockOnActuation) p.GHeavyDir = prev / prevLen;
        }
        if (p.GHeavyTick >= 0 || !canArm || !sprinting || nowLen < k.KnockOnActuation || prevLen >= k.KnockOnActuation) return false;
        p.GHeavyTick = tick;
        p.GHeavyDir = now / nowLen;
        p.KnockOnRequested = true;
        Commit(p, new GestureRead(Gesture.KnockOn, SkillSide.None, Vector2.Zero), tick);
        return true;
    }

    /// <summary>
    /// The fake shot: cancel pressed (this tick, not the one before) while the shot is under way, from the shoot press until
    /// fake_shot_cutoff before its planned strike. The left stick released gives a fake stop; pushed, a fake cut toward it, its
    /// side measured against the running direction. Call every tick (it tracks the cancel button).
    /// </summary>
    /// <param name="strikeTick">World tick the shot under way is planned to strike on (its published contact), or -1.</param>
    /// <param name="shotUnderWay">True while that shot can still be aborted (charging, or released and not yet struck).</param>
    public static GestureRead ReadFakeShot(PlayerState p, in TickIntent it, int tick, int strikeTick, bool shotUnderWay, Tuning t)
    {
        bool press = it.Cancel && !p.GPrevCancel;
        p.GPrevCancel = it.Cancel;
        if (!press || !shotUnderWay || strikeTick < 0 || tick > strikeTick - Ticks(t.Skill.FakeShotCutoff, t)) return GestureRead.None;
        Vector2 stick = it.Move;
        float len = stick.Length();
        if (len < t.Touch.StickThreshold) return Commit(p, new GestureRead(Gesture.FakeStop, SkillSide.None, Vector2.Zero), tick);
        Vector2 dir = stick / len;
        float lateral = Vector2.Dot(dir, RightOf(FrameForward(p, t)));
        var side = lateral < 0f ? SkillSide.Left : SkillSide.Right;
        return Commit(p, new GestureRead(Gesture.FakeCut, side, dir), tick);
    }

    static GestureRead Commit(PlayerState p, GestureRead g, int tick)
    {
        p.LastGesture = g.Kind;
        p.LastGestureTick = tick;
        return g;
    }
}
