using System.Numerics;

namespace FootballSim.Core;

// A chip over him: read from the ball's flight after his reaction, he backpedals facing the play and leaps at it.
public sealed partial class KeeperBrain
{
    bool _fromBackpedal;        // the current dive is a backward leap at the end of a backpedal
    Vector2 _chipDir;           // ground direction of the chip's travel

    /// <summary>
    /// True if the struck ball, as it flies now, is on target and passes over him higher than his leap
    /// (<see cref="Keeper.ChipReachHeight"/>): read from its predicted flight, never from the shot type.
    /// </summary>
    bool ChipOverHim(Ball ball)
    {
        var h = new Vector2(ball.Velocity.X, ball.Velocity.Z);
        if (h.LengthSquared() < 1f) return false;
        if (!ball.PredictPlaneCrossing(_pitch.GoalLineX, _t.Dt, 3f, out var cross, out _)) return false;
        if (MathF.Abs(cross.Z) + _t.Ball.Radius > _pitch.GoalHalfWidth || cross.Y + _t.Ball.Radius > _pitch.BarHeight) return false;
        var dir = Vector2.Normalize(h);
        var feet = new Vector2(_k.Position.X, _k.Position.Z);
        if (Vector2.Dot(new Vector2(ball.Position.X, ball.Position.Z) - feet, dir) >= 0f) return false; // already past him
        float leap = Keeper.ChipReachHeight(_k.Sheet, _t);
        var b = ball.ScratchA();
        for (int i = 0; i < 360; i++)
        {
            b.Step(_t.Dt, null, null);
            var p2 = new Vector2(b.Position.X, b.Position.Z);
            if (Vector2.Dot(p2 - feet, dir) < 0f) continue;
            // Level with him: over his leap, and not wide of him.
            float lateral = MathF.Abs((p2 - feet).X * dir.Y - (p2 - feet).Y * dir.X);
            return b.Position.Y > leap && lateral < 1.5f;
        }
        return false;
    }

    /// <summary>True if, leaping now, his reach gets to a point on the ball's path below his leap and before the goal line in time.</summary>
    bool CanLeapNow(Ball ball, float leap)
    {
        var home = HomeFor(_k.Position);
        var b = ball.ScratchB();
        float dt = 1f / 60f, t = 0f;
        // A leap is short: only a ball he meets within its first moments counts (he keeps running otherwise).
        int steps = (int)MathF.Ceiling((_t.Keeper.PushOff + _t.Keeper.ArmSweepTime + 0.15f) / dt);
        for (int i = 0; i < steps; i++)
        {
            b.Step(dt, null, null);
            t += dt;
            var p = b.Position;
            if (p.X > _pitch.GoalLineX) return false;
            if (p.Y > leap) continue;
            if (Vector3.Distance(p, home) <= Keeper.ReachAt(_k.Sheet, _t, t - Keeper.SaveJitterMax(_k.Sheet, _t, Keeper.Stretch(_k.Sheet, _t)))) return true;
        }
        return false;
    }

    void BeginBackpedal()
    {
        Enter(Phase.Backpedal);
        _active = true;
        _fromBackpedal = false;
        SetAction(KeeperAction.Backpedal);
    }

    /// <summary>
    /// Backpedalling under a chip: he heads for the first point on the ball's predicted path, goal-side of him, where it has
    /// dropped to his take height, that he can get within <c>chip_tip_reach</c> of <c>chip_leap_lead</c> before the ball, at his
    /// backpedalling speed (else for where it
    /// crosses the goal line), facing back up the pitch. Once his reach gets to it in time from where he is, or the ball is about
    /// to drop in, he leaps for it: a dive that may tip it over the bar or catch it. If the ball goes past the goal line or away, he
    /// stands again.
    /// </summary>
    void StepBackpedal(float dt, Ball ball, SimRng rng)
    {
        var k = _t.Keeper;
        var h = new Vector2(ball.Velocity.X, ball.Velocity.Z);
        if (h.LengthSquared() > 1e-2f) _chipDir = Vector2.Normalize(h);
        if (ball.Position.X > _pitch.GoalLineX + 0.2f || ball.Velocity.X <= 0f || _phaseTime > 3f)
        {
            _active = false;
            ToStand();
            return;
        }
        // Facing back up the pitch, toward where the chip came from.
        float want = MathF.Atan2(-_chipDir.Y, -_chipDir.X);
        float diff = MathF.IEEERemainder(want - _k.Facing, 2f * MathF.PI);
        float turn = k.TurnRateDeg * MathF.PI / 180f * dt;
        _k.Facing += Math.Clamp(diff, -turn, turn);

        float leap = Keeper.ChipReachHeight(_k.Sheet, _t), take = Keeper.ChipTakeHeight(_k.Sheet, _t);
        float accel = Keeper.MoveAccel(_k.Sheet, _t), vb = Keeper.BackpedalSpeed(_k.Sheet, _t);
        var feet = new Vector2(_k.Position.X, _k.Position.Z);
        var vel = new Vector2(_k.Velocity.X, _k.Velocity.Z);
        const float step = 1f / 60f;
        var b = ball.ScratchA();
        float tb = 0f;
        bool found = false;
        Vector2 target = feet, line = feet;
        float tLine = 0f;
        for (int i = 1; i <= 180; i++)
        {
            b.Step(step, null, null);
            tb += step;
            var p = b.Position;
            var p2 = new Vector2(p.X, p.Z);
            line = p2;
            tLine = tb;
            if (p.X >= _pitch.GoalLineX) break;
            if (p.Y > take || Vector2.Dot(p2 - feet, _chipDir) < -k.ChipTipReach) continue;
            float d = Vector2.Distance(p2, feet);
            float n = MathF.Max(0f, d - k.ChipTipReach);
            float v0 = d > 1e-4f ? Vector2.Dot(vel, (p2 - feet) / d) : 0f;
            if (Keeper.TravelTime(n, v0, accel, vb) + k.ChipLeapLead <= tb) { found = true; target = p2; break; }
        }
        // Leap once his reach gets to the ball in time from where he is (the same test the dive makes), or, out of time, at it
        // anyway as it drops in.
        if (CanLeapNow(ball, leap) || !found && tLine <= k.PushOff + k.ArmSweepTime + 0.1f)
        {
            _reaction = _since;
            _fromBackpedal = true;
            StartDive(ball, rng);
            return;
        }
        var goalTo = found ? target : line;
        var pos = new Vector2(_k.Position.X, _k.Position.Z);
        var to = Keeper.CapTarget(pos, goalTo, k.MoveTargetCap) - pos;
        float dist = to.Length();
        Vector2 desired = dist > 0.01f ? to / dist * MathF.Min(vb, MathF.Sqrt(2f * accel * MathF.Max(0f, dist - 0.01f)) + 0.5f) : Vector2.Zero;
        Steer(desired, accel, dt);
        _hands = HomeFor(_k.Position);
        SetAction(KeeperAction.Backpedal);
    }
}
