using System.Numerics;

namespace FootballSim.Core;

/// <summary>What a right-stick gesture, a skill key or a cancelled shot asks for.</summary>
public enum Gesture { None, StepOver, BodyFeint, BallRoll, DragBack, KnockOn, FakeStop, FakeCut }

/// <summary>A recognised gesture: its kind, its side (relative to the running direction) and, for a fake cut, the cut direction.</summary>
/// <param name="CutDirection">Unit ground direction (X, Z) of a fake cut (the left stick); zero otherwise.</param>
public readonly record struct GestureRead(Gesture Kind, SkillSide Side, Vector2 CutDirection)
{
    public static GestureRead None => default;
}

/// <summary>
/// Reads skill-move gestures from the right stick, tick by tick, in the player's frame: forward is the running direction while
/// the player moves faster than frame_min_speed, otherwise the facing. A pushed stick (min_actuation or more) belongs to its
/// nearest direction (forward, back, left or right); below that it is neutral. Every side gesture works to either side.
/// <list type="bullet">
/// <item>Step-over: forward, then a side within step_over_window (the side within step_over_side_tolerance_deg of square).</item>
/// <item>Body feint: from neutral, a side flick held at least a tick and back at neutral within feint_max_hold (and before
/// it became a roll).</item>
/// <item>Ball roll: a side held roll_min_hold (so a quick flick is a feint and a hold is a roll).</item>
/// <item>Drag-back: back.</item>
/// <item>Knock-on: a forward flick past knock_on_actuation while sprinting, fired knock_on_wait later unless the stick went to
/// a side (then it is a step-over) or another gesture came first. Not sprinting, a forward flick does nothing.</item>
/// <item>Fake shot (<see cref="ReadFakeShot"/>): cancel pressed within fake_shot_window of the shot starting; the left stick
/// released is a fake stop, pushed a fake cut that way.</item>
/// </list>
/// A skill key skips the stick. Recognised gestures are at least gesture_gap apart. Reading draws no random numbers and changes
/// nothing but the reader's own state, so a stick at rest leaves the simulation exactly as it was.
/// </summary>
public static class GestureReader
{
    /// <summary>Where the right stick is: at rest, pushed toward one of the four directions, or pushed between them.</summary>
    internal enum Zone : byte { Neutral, Forward, Back, Left, Right, Between }

    static int Ticks(float seconds, Tuning t) => Math.Max(1, (int)MathF.Round(seconds * t.TickRate));

    /// <summary>Clears the reader (a new round).</summary>
    internal static void Reset(PlayerState p)
    {
        p.GZone = Zone.Neutral;
        p.GPrevZone = Zone.Neutral;
        p.GZoneStart = 0;
        p.GFwdLastTick = int.MinValue / 2;
        p.GSideConsumed = false;
        p.GKnockUsed = false;
        p.GKnockWaitTick = -1;
        p.GPrevCancel = false;
        p.LastGesture = Gesture.None;
        p.LastGestureTick = int.MinValue / 2;
    }

    /// <summary>The gesture frame's forward: the running direction when moving, else the facing (unit X, Z).</summary>
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

    /// <summary>
    /// Reads this tick's right stick and skill key. Returns the gesture recognised on this tick, or <see cref="GestureRead.None"/>.
    /// Call once per tick for the player, before he moves.
    /// </summary>
    public static GestureRead Read(PlayerState p, in TickIntent it, int tick, Tuning t)
    {
        var k = t.Skill;
        if (tick <= 1) Reset(p);
        Vector2 fwd = FrameForward(p, t), right = RightOf(fwd);
        Vector2 s = it.Right;
        float mag = s.Length();
        Zone z = Zone.Neutral;
        float angle = 0f; // degrees from forward, positive toward the right
        if (mag >= k.MinActuation)
        {
            angle = MathF.Atan2(Vector2.Dot(s, right), Vector2.Dot(s, fwd)) * 180f / MathF.PI;
            float a = MathF.Abs(angle);
            z = a <= 45f ? Zone.Forward : a >= 135f ? Zone.Back : angle > 0f ? Zone.Right : Zone.Left;
            // Off its nearest direction by more than the tolerance (only possible below 45 degrees): no direction at all.
            float off = z == Zone.Forward ? a : z == Zone.Back ? 180f - a : MathF.Abs(a - 90f);
            if (off > k.AngleToleranceDeg) z = Zone.Between;
        }

        Gesture found = Gesture.None;
        SkillSide side = SkillSide.None;

        if (z != p.GZone)
        {
            // A side flick from neutral, back at neutral before it became a roll: a body feint.
            if (p.GZone is Zone.Left or Zone.Right && z == Zone.Neutral && !p.GSideConsumed && p.GPrevZone == Zone.Neutral
                && tick - p.GZoneStart >= 1 && tick - p.GZoneStart <= Ticks(k.FeintMaxHold, t))
            {
                found = Gesture.BodyFeint; side = p.GZone == Zone.Left ? SkillSide.Left : SkillSide.Right;
            }
            p.GPrevZone = p.GZone;
            p.GZone = z;
            p.GZoneStart = tick;
            p.GSideConsumed = false;
            if (z == Zone.Forward) p.GKnockUsed = false;
            if (z == Zone.Back && found == Gesture.None) found = Gesture.DragBack;
        }
        if (z == Zone.Forward) p.GFwdLastTick = tick;

        bool sideZone = z is Zone.Left or Zone.Right;
        if (found == Gesture.None && sideZone && !p.GSideConsumed)
        {
            SkillSide zs = z == Zone.Left ? SkillSide.Left : SkillSide.Right;
            float off = MathF.Abs(MathF.Abs(angle) - 90f);
            if (tick - p.GFwdLastTick <= Ticks(k.StepOverWindow, t) && off <= k.StepOverSideToleranceDeg)
            { found = Gesture.StepOver; side = zs; }
            else if (tick - p.GZoneStart + 1 >= Ticks(k.RollMinHold, t)) { found = Gesture.BallRoll; side = zs; }
        }

        // The knock-on: armed by a strong forward flick while sprinting, fired after the wait unless a side or another gesture
        // took over.
        bool sprinting = it.Sprint >= t.Touch.SprintThreshold;
        if (z == Zone.Forward && mag >= k.KnockOnActuation && sprinting && !p.GKnockUsed && p.GKnockWaitTick < 0)
        {
            p.GKnockWaitTick = tick;
            p.GKnockUsed = true;
        }
        if (p.GKnockWaitTick >= 0)
        {
            if (sideZone || found != Gesture.None) p.GKnockWaitTick = -1;
            else if (tick - p.GKnockWaitTick >= Ticks(k.KnockOnWait, t))
            {
                p.GKnockWaitTick = -1;
                found = Gesture.KnockOn;
            }
        }

        // A skill key skips the stick (its side from the left stick).
        if (it.SkillKey != SkillKey.None)
        {
            found = it.SkillKey switch
            {
                SkillKey.StepOver => Gesture.StepOver,
                SkillKey.BodyFeint => Gesture.BodyFeint,
                SkillKey.BallRoll => Gesture.BallRoll,
                _ => Gesture.DragBack,
            };
            side = found == Gesture.DragBack ? SkillSide.None : KeySide(it, fwd, t);
        }

        if (found == Gesture.None) return GestureRead.None;
        // Too soon after the previous gesture: dropped. A side still held (step-over or roll) is tried again next tick; a
        // gesture made on a change of direction (feint, drag-back) or a knock-on is gone.
        if (tick - p.LastGestureTick < Ticks(k.GestureGap, t)) return GestureRead.None;
        if (sideZone && found is Gesture.StepOver or Gesture.BallRoll && it.SkillKey == SkillKey.None) p.GSideConsumed = true;
        if (found == Gesture.StepOver) p.GKnockWaitTick = -1;
        return Commit(p, new GestureRead(found, side, Vector2.Zero), tick);
    }

    /// <summary>
    /// The fake shot: cancel pressed (this tick, not the one before) within fake_shot_window of the shot starting, while the
    /// shot is still under way. The left stick released gives a fake stop; pushed, a fake cut toward it, its side measured
    /// against the running direction. Call every tick (it tracks the cancel button).
    /// </summary>
    /// <param name="shotStartTick">World tick shoot was pressed for the shot under way, or -1.</param>
    /// <param name="shotUnderWay">True while that shot can still be aborted (charging, or released and not yet struck).</param>
    public static GestureRead ReadFakeShot(PlayerState p, in TickIntent it, int tick, int shotStartTick, bool shotUnderWay, Tuning t)
    {
        bool press = it.Cancel && !p.GPrevCancel;
        p.GPrevCancel = it.Cancel;
        if (!press || !shotUnderWay || shotStartTick < 0 || tick - shotStartTick > Ticks(t.Skill.FakeShotWindow, t)) return GestureRead.None;
        if (tick - p.LastGestureTick < Ticks(t.Skill.GestureGap, t)) return GestureRead.None;
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
