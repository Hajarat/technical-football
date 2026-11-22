using System.Numerics;

namespace FootballSim.Core;

/// <summary>The defending state of one outfield player (see <see cref="Defending"/>). Reset at the start of a round.</summary>
internal sealed class DefendState
{
    /// <summary>The lunge or slide under way (None otherwise), from <see cref="StartTick"/> to <see cref="EndTick"/>.</summary>
    public TackleKind Tackle;
    public int StartTick, ContactTick, EndTick;
    /// <summary>The way the tackle goes (unit, world X, Z), and how far the foot is put out at the contact, metres.</summary>
    public Vector2 Dir;
    public float Reach;
    /// <summary>His speed when it began, m/s (a missed lunge stuns by it); a slide's speed now and its deceleration.</summary>
    public float StartSpeed, SlideSpeed, SlideDecel;
    /// <summary>The tackle under way has met the ball; it has met the opponent's body first.</summary>
    public bool Won, Fouled;
    /// <summary>Simulation seconds before which no lunge or slide starts, no auto tackle is made, no barge is thrown.</summary>
    public float TackleCooldownUntil = -100f, AutoCooldownUntil = -100f, BargeCooldownUntil = -100f;
    /// <summary>A barge's lean shows until this time.</summary>
    public float BargeUntil = -100f;
    /// <summary>After a slide: lying until the first, on his feet again at the second.</summary>
    public float GroundedUntil = -100f, GetUpUntil = -100f;
    /// <summary>The tackle, slide and barge buttons on the previous tick (each fires on its press).</summary>
    public bool TacklePrev, SlidePrev, BargePrev;
    /// <summary>Set in front of a shot, and for how long, seconds.</summary>
    public bool Blocking;
    public float BlockSetFor;
    /// <summary>A push he is carried by (world X, Z), m/s; it dies away at barge.push_decel.</summary>
    public Vector2 Push;
    /// <summary>The part of his velocity off his facing left over from a jockey, dying away (see Movement.StepOffBall).</summary>
    public Vector2 Carry;
    /// <summary>The world tick of his latest body contact with an opponent and the direction to that opponent (unit, world X, Z).</summary>
    public int BumpTick = -100000;
    public Vector2 BumpDir;
    /// <summary>Seconds a loose ball has been under his control without a break (see defender.control_s).</summary>
    public float ControlFor;

    public void Reset()
    {
        Tackle = TackleKind.None;
        StartTick = ContactTick = EndTick = 0;
        Dir = default; Reach = 0f; StartSpeed = SlideSpeed = SlideDecel = 0f;
        Won = Fouled = false;
        TackleCooldownUntil = AutoCooldownUntil = BargeCooldownUntil = BargeUntil = GroundedUntil = GetUpUntil = -100f;
        TacklePrev = SlidePrev = BargePrev = false;
        Blocking = false; BlockSetFor = 0f;
        Push = default; Carry = default;
        BumpTick = -100000; BumpDir = default;
        ControlFor = 0f;
    }
}

/// <summary>
/// The defending actions, for any outfield player: the auto tackle (a dribbled ball within his reach is taken without a
/// button), the lunge (a standing tackle), the slide tackle, the shoulder barge and the shot block, and what they do to the
/// player on the other end (a stumble, a moment without the ball). These are rules over a player, the ball and the opponents;
/// the world applies their results (see World.Defender.cs). The numbers are in tuning.defending.
/// </summary>
public static class Defending
{
    /// <summary>The ids of an outfield player's obstacles in the ball's path (see <see cref="BallObstacle"/>).</summary>
    public const int BodyObstacle = 1, FootObstacle = 2, LegObstacle = 3;

    static Vector2 Flat(Vector3 v) => new(v.X, v.Z);

    /// <summary>True while the player stumbles: tackled, barged, or after a lunge that missed.</summary>
    public static bool Stumbling(PlayerState p, float now) => now < p.StumbleUntil;

    /// <summary>True while the player slides, lies on the ground after a slide, or gets up.</summary>
    public static bool Down(PlayerState p, float now) => p.Def.Tackle == TackleKind.Slide || now < p.Def.GetUpUntil;

    /// <summary>True when the player is on his feet with no tackle under way and not stumbling: he can act.</summary>
    public static bool Free(PlayerState p, float now) => p.Def.Tackle == TackleKind.None && now >= p.Def.GetUpUntil && !Stumbling(p, now);

    /// <summary>True when a lunge or a slide pressed now would start.</summary>
    public static bool CanTackle(PlayerState p, float now) => Free(p, now) && now >= p.Def.TackleCooldownUntil;

    /// <summary>How long a player stumbles when tackled or barged, seconds, by balance.</summary>
    public static float StumbleSeconds(PlayerSheet s, Tuning t) => t.Defending.StumbleS.ForRating(s.Balance);

    // ---- auto tackle ----

    /// <summary>The auto tackle's reach now, metres: by his speed (running or jockeying) and stand_tackle.</summary>
    public static float AutoRadius(PlayerState p, Tuning t)
    {
        var a = t.Defending.Auto;
        return (p.Jockeying ? a.RadiusJockeyM : a.RadiusWalkM).Evaluate(p.Speed) * a.RadiusFactor.ForRating(p.Sheet.StandTackle);
    }

    /// <summary>The widest angle off his facing the auto tackle reaches at now, degrees: narrower the faster he moves.</summary>
    public static float AutoAngleDeg(PlayerState p, Tuning t)
    {
        float top = p.Jockeying ? Movement.JockeyTopSpeed(p.Sheet, 1f, t) : Movement.TopSpeed(p.Sheet, t);
        return t.Defending.Auto.AngleDeg.Evaluate(top > 0f ? Math.Clamp(p.Speed / top, 0f, 1f) : 0f);
    }

    /// <summary>
    /// Shielding: the dribbler's body is between the tackler and the ball, within lane_half_m of the line from one to the
    /// other and within shield_m of the ball.
    /// </summary>
    public static bool Shielded(Vector3 tackler, Vector3 ball, Vector3 dribbler, Tuning t)
    {
        var a = t.Defending.Auto;
        Vector2 p = Flat(tackler), b = Flat(ball), o = Flat(dribbler);
        var seg = b - p;
        float len2 = seg.LengthSquared();
        if (len2 < 1e-8f) return false;
        float u = Vector2.Dot(o - p, seg) / len2;
        if (u <= 0f || u >= 1f) return false;
        return Vector2.Distance(o, p + seg * u) <= a.LaneHalfM && Vector2.Distance(o, b) <= a.ShieldM;
    }

    /// <summary>
    /// True when player <paramref name="p"/> takes the ball <paramref name="dribbler"/> is dribbling this tick without a button:
    /// always with the ball within guarantee_m of him and below max_height_m; otherwise with the ball in his front half, inside
    /// his reach and its angle, and not shielded. Never while he is down, stumbling, in a tackle, or inside the cooldown.
    /// </summary>
    public static bool AutoTackleReaches(PlayerState p, PlayerState dribbler, Ball ball, float now, Tuning t)
    {
        // (through a lunge his body is still in the way: the ball at his feet is his, though he reaches for nothing else)
        bool lunging = p.Def.Tackle == TackleKind.Lunge && !Stumbling(p, now);
        if (!Free(p, now) && !lunging || now < p.Def.AutoCooldownUntil) return false;
        var a = t.Defending.Auto;
        if (ball.Position.Y >= a.MaxHeightM) return false;
        var rel = Flat(ball.Position) - Flat(p.Position);
        float d = rel.Length();
        if (d <= a.GuaranteeM) return true;
        if (lunging) return false;
        float off = MathF.Abs(Movement.WrapAngle(MathF.Atan2(rel.Y, rel.X) - p.Facing)) * 180f / MathF.PI;
        if (off >= 90f || off > AutoAngleDeg(p, t) || d > AutoRadius(p, t)) return false;
        return !Shielded(p.Position, ball.Position, dribbler.Position, t);
    }

    /// <summary>
    /// Where a ball won goes: at a speed drawn between poke_speed_min and poke_speed_max, along the direction within
    /// poke_spread_deg of <paramref name="along"/> that passes furthest from every opponent ahead of him (the straightest of
    /// equals).
    /// </summary>
    internal static Vector3 PokeVelocity(PlayerState p, Vector2 along, IReadOnlyList<PlayerState> opponents, SimRng rng, Tuning t)
    {
        var a = t.Defending.Auto;
        float speed = rng.Uniform(a.PokeSpeedMin, a.PokeSpeedMax);
        float yaw = along.LengthSquared() > 1e-8f ? MathF.Atan2(along.Y, along.X) : p.Facing;
        var from = Flat(p.Position);
        Vector2 best = new(MathF.Cos(yaw), MathF.Sin(yaw));
        float bestClear = -1f;
        foreach (int i in PokeOrder)
        {
            float y = yaw + i * (a.PokeSpreadDeg / 3f) * MathF.PI / 180f;
            var dir = new Vector2(MathF.Cos(y), MathF.Sin(y));
            float clear = PokeLook;
            for (int j = 0; j < opponents.Count; j++)
            {
                var o = opponents[j];
                if (o.Role != PlayerRole.Outfield) continue;
                var rel = Flat(o.Position) - from;
                float ahead = Vector2.Dot(rel, dir);
                if (ahead <= 0f || ahead > PokeLook) continue;
                clear = MathF.Min(clear, MathF.Abs(rel.X * dir.Y - rel.Y * dir.X));
            }
            if (clear > bestClear + 1e-4f) { bestClear = clear; best = dir; }
        }
        return new Vector3(best.X, 0f, best.Y) * speed;
    }

    /// <summary>The poke directions tried, in thirds of the spread: straight first, so it wins among equals.</summary>
    static readonly int[] PokeOrder = { 0, -1, 1, -2, 2, -3, 3 };
    /// <summary>An opponent further ahead than this does not stand in a poke's way, metres.</summary>
    const float PokeLook = 6f;

    // ---- lunge (standing tackle) ----

    /// <summary>A lunge's reach now, metres: by stand_tackle and his speed, less when the ball lies well off his run.</summary>
    public static float LungeReach(PlayerState p, Vector3 ball, Tuning t)
    {
        var s = t.Defending.Stand;
        float reach = s.ReachM.ForRating(p.Sheet.StandTackle) * s.ReachSpeedFactor.Evaluate(p.Speed);
        var v = Flat(p.Velocity);
        var rel = Flat(ball) - Flat(p.Position);
        if (v.Length() > 0.5f && rel.LengthSquared() > 1e-6f
            && MathF.Abs(Movement.WrapAngle(MathF.Atan2(rel.Y, rel.X) - MathF.Atan2(v.Y, v.X))) * 180f / MathF.PI > s.OffRunDeg)
            reach *= s.OffRunFactor;
        return reach;
    }

    /// <summary>
    /// Starts a lunge: over the window exec_min_s to exec_max_s after the press it looks for the first tick at which his foot,
    /// reaching from where he will be if he carries on as he moves, meets the ball rolling on as it does now, and lunges for
    /// that meeting. With none in the window he lunges at where the ball is, as far as he reaches.
    /// </summary>
    internal static void StartLunge(PlayerState p, Ball ball, int tick, Tuning t)
    {
        var s = t.Defending.Stand;
        var d = p.Def;
        float dt = t.Dt;
        Vector2 pos = Flat(p.Position), vel = Flat(p.Velocity);
        float reach = LungeReach(p, ball.Position, t);
        int from = Math.Max(1, (int)MathF.Round(s.ExecMinS * t.TickRate)), to = Math.Max(from, (int)MathF.Round(s.ExecMaxS * t.TickRate));
        var b = ball.Clone();
        int contact = -1;
        Vector2 at = default;
        for (int i = 1; i <= to; i++)
        {
            b.Step(dt, null, null);
            if (i < from || b.Position.Y > s.CertainHeightM) continue;
            var rel = Flat(b.Position) - (pos + vel * (i * dt));
            if (rel.Length() <= reach) { contact = i; at = rel; break; }
        }
        if (contact < 0)
        {
            contact = from;
            at = Flat(ball.Position) - pos;
            if (at.Length() > reach) at *= reach / at.Length();
        }
        d.Tackle = TackleKind.Lunge;
        d.StartTick = tick;
        d.ContactTick = tick + contact;
        d.EndTick = tick + to;
        d.Dir = at.LengthSquared() > 1e-8f ? Vector2.Normalize(at) : new Vector2(MathF.Cos(p.Facing), MathF.Sin(p.Facing));
        d.Reach = MathF.Max(at.Length(), t.Defending.BodyRadiusM);
        d.StartSpeed = p.Speed;
        d.Won = d.Fouled = false;
        p.ActionSide = SideOf(p, d.Dir);
        p.TackleContactTime = contact * dt;
    }

    /// <summary>The side of his body a ground direction lies on.</summary>
    internal static SkillSide SideOf(PlayerState p, Vector2 dir) =>
        -MathF.Sin(p.Facing) * dir.X + MathF.Cos(p.Facing) * dir.Y >= 0f ? SkillSide.Right : SkillSide.Left;

    /// <summary>
    /// The lunging foot's centre on world tick <paramref name="tick"/>: put out along the lunge from his feet, all the way at
    /// the contact tick, held there for stand.active_s and then drawn back by the end.
    /// </summary>
    internal static Vector3 LungeFoot(PlayerState p, int tick, Tuning t)
    {
        var d = p.Def;
        int hold = d.ContactTick + ActiveTicks(t);
        float f = tick <= d.ContactTick ? (tick - d.StartTick) / (float)Math.Max(1, d.ContactTick - d.StartTick)
            : tick <= hold ? 1f
            : 1f - (tick - hold) / (float)Math.Max(1, d.EndTick - hold);
        f = Math.Clamp(f, 0f, 1f);
        return new Vector3(p.Position.X + d.Dir.X * d.Reach * f, t.Ball.Radius, p.Position.Z + d.Dir.Y * d.Reach * f);
    }

    static int ActiveTicks(Tuning t) => Math.Max(1, (int)MathF.Round(t.Defending.Stand.ActiveS * t.TickRate));

    /// <summary>True on the ticks a lunge's foot can win the ball or foul: from just before its contact tick through stand.active_s after it.</summary>
    internal static bool LungeActive(PlayerState p, int tick, Tuning t) =>
        p.Def.Tackle == TackleKind.Lunge && tick >= p.Def.ContactTick - 1 && tick <= Math.Min(p.Def.EndTick, p.Def.ContactTick + ActiveTicks(t));

    // ---- slide ----

    /// <summary>
    /// Starts a slide: along the line to where the ball will be when his foot gets there, starting at his speed (slide.min_speed
    /// at least) and slowing evenly to a stop slide.reach_m on.
    /// </summary>
    internal static void StartSlide(PlayerState p, Ball ball, int tick, Tuning t)
    {
        var s = t.Defending.Slide;
        var d = p.Def;
        float v0 = MathF.Max(p.Speed, s.MinSpeed);
        float reach = s.ReachM.ForRating(p.Sheet.SlideTackle);
        Vector2 pos = Flat(p.Position), bp = Flat(ball.Position), bv = Flat(ball.Velocity);
        Vector2 aim = bp;
        for (int i = 0; i < 2; i++)
            aim = bp + bv * (MathF.Max(0f, Vector2.Distance(pos, aim) - s.LungeM) / v0);
        var to = aim - pos;
        d.Tackle = TackleKind.Slide;
        d.StartTick = tick;
        d.ContactTick = tick;
        d.EndTick = tick + Math.Max(1, (int)MathF.Ceiling(2f * reach / v0 * t.TickRate));
        d.Dir = to.LengthSquared() > 1e-8f ? Vector2.Normalize(to) : new Vector2(MathF.Cos(p.Facing), MathF.Sin(p.Facing));
        d.Reach = s.LungeM;
        d.StartSpeed = p.Speed;
        d.SlideSpeed = v0;
        d.SlideDecel = v0 * v0 / (2f * reach);
        d.Won = d.Fouled = false;
        p.ActionSide = SideOf(p, d.Dir);
        p.TackleContactTime = float.NaN;
    }

    /// <summary>The sliding foot's centre: slide.lunge_m ahead of his body along the slide, on the ground.</summary>
    internal static Vector3 SlideFoot(PlayerState p, Tuning t) =>
        new(p.Position.X + p.Def.Dir.X * p.Def.Reach, t.Ball.Radius, p.Position.Z + p.Def.Dir.Y * p.Def.Reach);

    /// <summary>Moves a sliding player one tick along his slide. Returns false once it has run out.</summary>
    internal static bool StepSlide(PlayerState p, int tick, float dt, Pitch? pitch)
    {
        var d = p.Def;
        d.SlideSpeed = MathF.Max(0f, d.SlideSpeed - d.SlideDecel * dt);
        var vel = new Vector3(d.Dir.X, 0f, d.Dir.Y) * d.SlideSpeed;
        var pos = p.Position + vel * dt;
        if (pitch != null && pitch.ClampPlayer(ref pos, ref vel)) d.SlideSpeed = vel.Length();
        p.Position = pos;
        p.HandPosition = pos;
        p.Velocity = vel;
        p.RunSpeed = 0f;
        p.SteerVelocity = Vector3.Zero;
        p.Facing = MathF.Atan2(d.Dir.Y, d.Dir.X);
        p.TurnRefHeading = p.Facing;
        p.Jockeying = false;
        d.Carry = Vector2.Zero;
        return tick < d.EndTick && d.SlideSpeed > 0.05f;
    }

    // ---- barge ----

    /// <summary>
    /// The opponent a barge by <paramref name="p"/> would land on now: an outfield player within barge.distance_m, alongside (his
    /// bearing off the barger's facing between bearing_min_deg and bearing_max_deg) and facing within facing_deg of the barger.
    /// Null when there is none.
    /// </summary>
    public static PlayerState? BargeTarget(PlayerState p, IReadOnlyList<PlayerState> opponents, Tuning t)
    {
        var k = t.Defending.Barge;
        for (int i = 0; i < opponents.Count; i++)
        {
            var o = opponents[i];
            if (o.Role != PlayerRole.Outfield) continue;
            var rel = Flat(o.Position) - Flat(p.Position);
            if (rel.Length() > k.DistanceM || rel.LengthSquared() < 1e-8f) continue;
            float bearing = MathF.Abs(Movement.WrapAngle(MathF.Atan2(rel.Y, rel.X) - p.Facing)) * 180f / MathF.PI;
            if (bearing < k.BearingMinDeg || bearing > k.BearingMaxDeg) continue;
            if (MathF.Abs(Movement.WrapAngle(o.Facing - p.Facing)) * 180f / MathF.PI > k.FacingDeg) continue;
            return o;
        }
        return null;
    }

    /// <summary>How much the barger out-muscles the other: his strength over the other's, times his weight over the other's.</summary>
    public static float BargeRatio(PlayerSheet barger, PlayerSheet other) =>
        (float)barger.Strength / Math.Max(1, other.Strength) * (barger.WeightKg / MathF.Max(1f, other.WeightKg));

    // ---- block ----

    /// <summary>How far his leg reaches after <paramref name="setFor"/> seconds set in front of a shot, metres.</summary>
    public static float BlockReachAfter(PlayerSheet s, float setFor, Tuning t)
    {
        var k = t.Defending;
        float grow = MathF.Max(1e-3f, k.Block.GrowS.ForRating(s.Blocking));
        return k.BodyRadiusM + (k.Block.ReachM - k.BodyRadiusM) * Math.Clamp(setFor / grow, 0f, 1f);
    }

    /// <summary>
    /// The velocity a ball leaves an outfield player's body, leg or foot with: mirrored about the contact normal
    /// <paramref name="n"/> (so turned by the angle it came in at), keeping block.exit_speed of its speed, and no steeper than
    /// block.lift_deg. Null when the ball is not moving into him. The result is against his own velocity too.
    /// </summary>
    /// <param name="velocity">The ball's velocity against the player's own.</param>
    public static Vector3? Deflection(Vector3 velocity, Vector3 n, Tuning t)
    {
        var k = t.Defending.Block;
        float vn = Vector3.Dot(velocity, n);
        float speed = velocity.Length();
        if (vn >= 0f || speed < 1e-4f) return null;
        var r = Vector3.Normalize(velocity - 2f * vn * n) * (speed * k.ExitSpeed.Evaluate(speed));
        float flat = MathF.Sqrt(r.X * r.X + r.Z * r.Z);
        float maxUp = MathF.Tan(k.LiftDeg * MathF.PI / 180f) * flat;
        if (r.Y > maxUp) r.Y = maxUp;
        return r;
    }
}
