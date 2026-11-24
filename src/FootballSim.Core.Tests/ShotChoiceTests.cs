using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>
/// Shots worth choosing, stage 1: the aim pull by assistance level, the finesse family's curl-in foot rule, and the launch
/// solve that turns every shot against its own bend.
/// </summary>
public class ShotChoiceTests
{
    static readonly Tuning T = new();
    static readonly Pitch P = new(new PitchSpec(), T.Frame);
    const float Deg = MathF.PI / 180f;
    static readonly AimAssistLevel[] Levels = { AimAssistLevel.Off, AimAssistLevel.Low, AimAssistLevel.Medium, AimAssistLevel.High };
    static readonly ShotType[] Types = { ShotType.Power, ShotType.Driven, ShotType.Finesse, ShotType.LowFinesse, ShotType.Chip };

    static Vector3 BallAt(float outM, float z) => new(P.GoalLineX - outM, T.Ball.Radius, z);
    static Vector2 Stick(float yaw) => new(MathF.Cos(yaw), MathF.Sin(yaw));
    static float Bearing(Vector3 ball, float z) => MathF.Atan2(z - ball.Z, P.GoalLineX - ball.X);
    static float Aim(Vector3 ball, float yaw, AimAssistLevel level) => ShotModel.AimTargetZ(Stick(yaw), 0f, ball, level, P, T);

    static readonly (float Out, float Z)[] Spots = { (6f, 0f), (11f, 0f), (16f, -4f), (20f, 7f), (12f, 9f), (25f, -2f), (3f, 5f) };

    // ---- the aim pull ----

    [Fact]
    public void AnAimInsideTheFrameIsNeverTouchedAndOffIsTheRawRayEverywhere()
    {
        foreach (var (outM, z) in Spots)
        {
            var ball = BallAt(outM, z);
            float left = Bearing(ball, -P.GoalHalfWidth), right = Bearing(ball, P.GoalHalfWidth);
            for (int i = 0; i <= 20; i++)
            {
                // (from a hair inside one post to a hair inside the other)
                float yaw = left + (right - left) * (i + 0.01f) / 20.02f;
                float raw = ShotModel.RawTargetZ(Stick(yaw), 0f, ball, P, T);
                foreach (var level in Levels) Assert.Equal(raw, Aim(ball, yaw, level));
            }
            for (float deg = -180f; deg < 180f; deg += 3.7f)
                Assert.Equal(ShotModel.RawTargetZ(Stick(deg * Deg), 0f, ball, P, T), Aim(ball, deg * Deg, AimAssistLevel.Off));
        }
    }

    [Fact]
    public void ACapturedAimIsTheSafePointAndThePullFadesOutWithoutAStep()
    {
        float safe = P.GoalHalfWidth - T.Shot.AimAssist.InsetM;
        foreach (var level in Levels.Skip(1))
        {
            var band = T.Shot.AimAssist.For(level)!;
            foreach (var (outM, z) in Spots)
            {
                var ball = BallAt(outM, z);
                foreach (float side in new[] { -1f, 1f })
                {
                    float post = Bearing(ball, side * P.GoalHalfWidth);
                    float At(float phiDeg) => Aim(ball, post + side * phiDeg * Deg, level);
                    // Captured: the safe point, from just outside the post to the capture angle.
                    foreach (float phi in new[] { 0.01f, band.CaptureDeg * 0.5f, band.CaptureDeg - 0.01f })
                        Assert.Equal(side * safe, At(phi), 4);
                    // Beyond the release angle the stick is the aim.
                    foreach (float phi in new[] { band.ReleaseDeg + 0.01f, band.ReleaseDeg + 20f })
                        Assert.Equal(ShotModel.RawTargetZ(Stick(post + side * phi * Deg), 0f, ball, P, T), At(phi));
                    // Continuous through the capture and the release angle, and it only ever moves outward between them.
                    // (measured as the aim's bearing: a Z on the goal line runs away as the bearing nears the line)
                    float AimDeg(float phi) => Bearing(ball, At(phi)) / Deg;
                    float clamp = Bearing(ball, side * T.Shot.MaxTargetZ) / Deg;
                    foreach (float edge in new[] { band.CaptureDeg, band.ReleaseDeg })
                    {
                        float before = AimDeg(edge - 0.02f), after = AimDeg(edge + 0.02f);
                        // (an aim already on the clamp does not move at all)
                        if (MathF.Abs(before - clamp) > 1e-3f) Assert.InRange(MathF.Abs(after - before), 0f, 0.5f);
                    }
                    float last = side * AimDeg(band.CaptureDeg);
                    for (float phi = band.CaptureDeg; phi <= band.ReleaseDeg; phi += 0.25f)
                    {
                        float now = side * AimDeg(phi);
                        Assert.True(now >= last - 1e-3f, $"{level} at {outM} m, z {z}, side {side}: the aim came back in at {phi} degrees");
                        last = now;
                    }
                }
            }
        }
    }

    [Fact]
    public void AimPullNearTheGoalLine()
    {
        // Within a metre of the goal line, and level with a post, the two posts' bearings open wide (past 150 degrees from the
        // closest spots). A stick wide of one post is pulled to that post's side, never across to the other.
        foreach (var (outM, z) in new[] { (0.9f, 0f), (0.5f, 0f), (0.3f, 2f), (0.9f, 3.66f), (0.6f, -3.66f), (0.4f, -3f), (1f, 5f), (0.5f, -6f) })
        {
            var ball = BallAt(outM, z);
            foreach (var level in Levels.Skip(1))
            {
                var band = T.Shot.AimAssist.For(level)!;
                foreach (float side in new[] { -1f, 1f })
                {
                    float post = Bearing(ball, side * P.GoalHalfWidth);
                    for (float phi = 0.5f; phi < band.ReleaseDeg + 30f; phi += 0.5f)
                    {
                        float yaw = post + side * phi * Deg;
                        float aimed = Aim(ball, yaw, level), raw = ShotModel.RawTargetZ(Stick(yaw), 0f, ball, P, T);
                        Assert.True(float.IsFinite(aimed));
                        // On the stick's side of the safe point (or on it), and never further out than the stick itself.
                        float safe = side * (P.GoalHalfWidth - T.Shot.AimAssist.InsetM);
                        Assert.True(side * aimed >= side * safe - 1e-3f, $"{level}, {outM} m, z {z}, side {side}, {phi} degrees: aimed {aimed} is across the safe point {safe}");
                        Assert.True(side * aimed <= side * raw + 1e-3f, $"{level}, {outM} m, z {z}, side {side}, {phi} degrees: aimed {aimed} is outside the stick's {raw}");
                    }
                }
            }
        }
        // On or behind the goal line there is no pull.
        foreach (var level in Levels)
            Assert.Equal(ShotModel.RawTargetZ(Stick(0.3f), 0f, BallAt(-0.5f, 2f), P, T), Aim(BallAt(-0.5f, 2f), 0.3f, level));
    }

    [Fact]
    public void AimPullOnFacing()
    {
        // The stick at rest: the facing is the aim, and it takes the pull exactly as a stick pointing that way does.
        foreach (var (outM, z) in Spots)
        {
            var ball = BallAt(outM, z);
            foreach (var level in Levels)
                for (float deg = -80f; deg <= 80f; deg += 2.5f)
                {
                    float byFacing = ShotModel.AimTargetZ(Vector2.Zero, deg * Deg, ball, level, P, T);
                    Assert.Equal(Aim(ball, deg * Deg, level), byFacing, 3);
                    // ... and through the whole shot: the context's level reaches the intended yaw.
                    var p = TestData.Outfield(TestData.Sheet(60), ball.X - 0.5f, ball.Z, deg * Deg);
                    var ctx = new ShotContext { Shooter = p, BallPosition = ball, Stick = Vector2.Zero, Charge = 0.5f, Type = ShotType.Power, AimAssist = level };
                    ShotModel.IntendedYaw(ctx, P, T, out float targetZ);
                    Assert.Equal(byFacing, targetZ, 3);
                }
        }
    }

    /// <summary>
    /// The recorded aims kept under TestData/aims (a person's struck shots: the ball and the stick's direction; written by the
    /// batch tool's <c>--probe aim --record</c>): of those whose stick pointed wide of the frame, none ends inside the posts at
    /// Off, and at least 35%, 55% and 75% do at Low, Medium and High. The batch tool's <c>--probe aim</c> prints the table.
    /// </summary>
    [Fact]
    public void RecordedAimsThatMissedTheFrameArePulledOntoItByLevel()
    {
        string file = Path.Combine(TestData.DataDir, "..", "src", "FootballSim.Core.Tests", "TestData", "aims", "recorded_aims.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
        var arena = new Pitch(TestData.Scenario("penalty").Pitch, T.Frame, T.Arena);
        Assert.Equal(arena.GoalLineX, doc.RootElement.GetProperty("goal_line_x").GetSingle(), 3);
        Assert.Equal(arena.GoalHalfWidth, doc.RootElement.GetProperty("goal_half_width").GetSingle(), 3);
        var aims = doc.RootElement.GetProperty("aims").EnumerateArray()
            .Select(a => (Ball: new Vector3(a.GetProperty("ball_x").GetSingle(), T.Ball.Radius, a.GetProperty("ball_z").GetSingle()), Yaw: a.GetProperty("stick_deg").GetSingle() * Deg))
            .ToList();
        float Z((Vector3 Ball, float Yaw) a, AimAssistLevel level) => ShotModel.AimTargetZ(Stick(a.Yaw), 0f, a.Ball, level, arena, T);
        var off = aims.Where(a => MathF.Abs(Z(a, AimAssistLevel.Off)) > arena.GoalHalfWidth).ToList();
        Assert.True(off.Count >= 10, $"only {off.Count} recorded aims missed the frame");
        int Inside(AimAssistLevel level) => off.Count(a => MathF.Abs(Z(a, level)) < arena.GoalHalfWidth);
        int low = Inside(AimAssistLevel.Low), medium = Inside(AimAssistLevel.Medium), high = Inside(AimAssistLevel.High);
        Assert.Equal(0, Inside(AimAssistLevel.Off));
        Assert.True(low >= (int)(0.35 * off.Count), $"Low: {low} of {off.Count}");
        Assert.True(medium >= (int)(0.55 * off.Count) && medium >= low, $"Medium: {medium} of {off.Count}");
        Assert.True(high >= (int)(0.75 * off.Count) && high >= medium, $"High: {high} of {off.Count}");
        // An aim inside the frame is the same at every level.
        foreach (var a in aims.Except(off))
            foreach (var level in Levels) Assert.Equal(Z(a, AimAssistLevel.Off), Z(a, level));
    }

    // ---- curl-in ----

    static PlayerSheet Sheet(Foot preferred, bool trait, int curve = 72, int weakFoot = 50)
    {
        var s = TestData.Player("striker_a").Clone();
        s.PreferredFoot = preferred; s.WeakFoot = weakFoot; s.Curve = curve;
        s.Traits = trait ? PlayerTrait.OutsideFoot : PlayerTrait.None;
        return s;
    }

    /// <summary>The sideways force on a struck ball, along Z: positive bends it toward +Z.</summary>
    static float BendZ(ShotResult r) => Vector3.Cross(r.Spin, r.Launch).Z;

    [Fact]
    public void AFinesseTakesTheFootThatCurlsItIn()
    {
        foreach (var type in new[] { ShotType.Finesse, ShotType.LowFinesse })
            foreach (Foot strong in new[] { Foot.Right, Foot.Left })
                foreach (bool trait in new[] { false, true })
                    foreach (float aimZ in new[] { -3f, -0.6f, -0.4f, 0f, 0.4f, 0.6f, 3f })
                    {
                        var s = Sheet(strong, trait);
                        Foot weak = strong == Foot.Right ? Foot.Left : Foot.Right;
                        // On the spot, facing the goal centre: an aim at his strong side's half is within his outside cap.
                        var pos = new Vector3(P.GoalLineX - 11f, 0f, 0f);
                        var ball = new Vector3(pos.X + 0.5f, T.Ball.Radius, 0f);
                        float yaw = Bearing(ball, aimZ);
                        var d = ShotModel.ChooseByGeometry(s, pos, 0f, ball, yaw, type, P, T, curlIn: true);
                        string id = $"{type}, {strong}-footed, trait {trait}, aimed {aimZ}";
                        // The instep that bends an aim at +Z back in is the right foot's.
                        Foot instep = aimZ > 0f ? Foot.Right : Foot.Left;
                        bool own = strong == Foot.Right ? aimZ > 0f : aimZ < 0f;   // the aim is toward his strong foot's side
                        StrikeDecision want =
                            MathF.Abs(aimZ) <= T.Shot.CurlInCentreM || instep == strong ? new(new StrikeChoice(strong, FootSurface.Inside), StrikeRule.CurlIn)
                            : trait && own ? new(new StrikeChoice(strong, FootSurface.Outside), StrikeRule.CurlInOutside)
                            : new(new StrikeChoice(weak, FootSurface.Inside), StrikeRule.CurlIn);
                        Assert.True(want == d, $"{id}: {d}, expected {want}");
                        // Struck, it bends toward the goal centre (outside the centre band, where there is a side to bend from).
                        var shooter = TestData.Outfield(s, pos.X, pos.Z, 0f);
                        var r = ShotModel.Compute(new ShotContext { Shooter = shooter, BallPosition = ball, Stick = Stick(yaw), Charge = 0.5f, Type = type }, P, T, new SimRng(3));
                        Assert.Equal(d.Choice.Foot, r.Foot);
                        Assert.Equal(d.Choice.Surface, r.Surface);
                        if (MathF.Abs(aimZ) > T.Shot.CurlInCentreM) Assert.True(BendZ(r) * aimZ < 0f, $"{id}: bends away from the goal centre");
                    }
    }

    [Fact]
    public void TheOutsideOfTheStrongFootNeedsTheTraitAndAnAimWithinHisCapOnHisStrongSide()
    {
        // A right-footer aiming at the -Z half needs a bend toward +Z: his left instep, or the outside of his right foot.
        var with = Sheet(Foot.Right, trait: true);
        float cap = ShotModel.OutsideCapDeg(with, ShotType.Finesse, T);
        var pos = new Vector3(P.GoalLineX - 16f, 0f, 0f);
        var ball = new Vector3(pos.X, T.Ball.Radius, 0f);
        float yaw = Bearing(ball, -3f);
        StrikeDecision At(PlayerSheet s, float ownDeg) => ShotModel.ChooseByGeometry(s, pos, yaw - ownDeg * Deg, ball, yaw, ShotType.Finesse, P, T, curlIn: true);
        var outside = new StrikeDecision(new StrikeChoice(Foot.Right, FootSurface.Outside), StrikeRule.CurlInOutside);
        var weak = new StrikeDecision(new StrikeChoice(Foot.Left, FootSurface.Inside), StrikeRule.CurlIn);
        Assert.Equal(outside, At(with, 0f));
        Assert.Equal(outside, At(with, cap - 0.5f));
        Assert.Equal(weak, At(with, cap + 0.5f));     // beyond the cap
        Assert.Equal(weak, At(with, -10f));           // across his body
        Assert.Equal(weak, At(Sheet(Foot.Right, trait: false), 10f));
    }

    [Fact]
    public void PowerDrivenAndChipKeepTheirFootRulesAndAPassKeepsTheGeometry()
    {
        var s = Sheet(Foot.Right, trait: false);
        var pos = new Vector3(P.GoalLineX - 16f, 0f, 2f);
        var ball = new Vector3(pos.X, T.Ball.Radius, pos.Z);
        for (float deg = -170f; deg <= 170f; deg += 10f)
        {
            foreach (var type in new[] { ShotType.Power, ShotType.Driven, ShotType.Chip })
                Assert.Equal(ShotModel.ChooseByGeometry(s, pos, 0f, ball, deg * Deg, type, P, T), ShotModel.ChooseByGeometry(s, pos, 0f, ball, deg * Deg, type, P, T, curlIn: true));
            // (a pass is chosen as a finesse without the curl-in rule: none of its decisions is a curl-in one)
            var pass = ShotModel.ChooseByGeometry(s, pos, 0f, ball, deg * Deg, ShotType.Finesse, P, T);
            Assert.True(pass.Rule is not (StrikeRule.CurlIn or StrikeRule.CurlInOutside));
        }
    }

    [Fact]
    public void CurlInHandOverKeepsTheBend()
    {
        // The chosen foot has no strike contact in the dribble and the other has one: the other foot strikes with the opposite
        // surface, so the ball still bends in. Both feet with a contact: the chosen foot waits, however much later (no swap).
        foreach (Foot strong in new[] { Foot.Right, Foot.Left })
            foreach (bool trait in new[] { false, true })
                foreach (float aimZ in new[] { -3f, 3f })
                {
                    var s = Sheet(strong, trait);
                    var pos = new Vector3(P.GoalLineX - 11f, 0f, 0f);
                    var ball = new Vector3(pos.X + 0.5f, T.Ball.Radius, 0f);
                    float yaw = Bearing(ball, aimZ);
                    var d = ShotModel.ChooseByGeometry(s, pos, 0f, ball, yaw, ShotType.Finesse, P, T, curlIn: true);
                    bool right = d.Choice.Foot == Foot.Right;
                    var handed = ShotModel.WithContact(d, ShotType.Finesse, right ? -1 : 40, right ? 40 : -1, allowSwap: true, T);
                    Assert.NotEqual(d.Choice.Foot, handed.Foot);
                    Assert.Equal(d.Choice.Surface == FootSurface.Inside ? FootSurface.Outside : FootSurface.Inside, handed.Surface);
                    Assert.Equal(d.Choice, ShotModel.WithContact(d, ShotType.Finesse, right ? 90 : 10, right ? 10 : 90, allowSwap: true, T));
                    var shooter = TestData.Outfield(s, pos.X, pos.Z, 0f);
                    ShotResult Struck(StrikeChoice c) => ShotModel.Compute(new ShotContext
                    {
                        Shooter = shooter, BallPosition = ball, Stick = Stick(yaw), Charge = 0.5f, Type = ShotType.Finesse, Foot = c.Foot, Surface = c.Surface,
                    }, P, T, new SimRng(3));
                    Assert.True(BendZ(Struck(d.Choice)) * aimZ < 0f);
                    Assert.True(BendZ(Struck(handed)) * aimZ < 0f, $"{strong}-footed, trait {trait}, aimed {aimZ}: handed to the {handed.Foot} foot's {handed.Surface}, it bends away");
                }
    }

    static void Step(World w, TickIntent it)
    {
        var buf = new TickIntent[w.PlayerCount];
        buf[0] = it;
        w.Step(buf);
    }

    [Fact]
    public void CurlInFootFixedAtRelease()
    {
        // The aim hovers on the centre band's edge while shoot is held; the foot is the one for the aim at the release, and the
        // stick moving on after it changes neither the foot nor the aim.
        ShotResult Shoot(float releaseZ, float afterZ)
        {
            var w = new World(new Tuning { ErrorScale = 0f }, TestData.Scenario("penalty"), Sheet(Foot.Right, trait: false), TestData.Player("the_wall"), 5);
            w.Begin();
            TickIntent At(float z, bool shoot)
            {
                var dir = Vector2.Normalize(new Vector2(w.Pitch.GoalLineX - w.Ball.Position.X, z - w.Ball.Position.Z));
                return new TickIntent { MoveX = dir.X, MoveZ = dir.Y, ShootHeld = shoot, Finesse = shoot };
            }
            float band = w.Tuning.Shot.CurlInCentreM;
            for (int i = 0; i < 20; i++) Step(w, At(i % 2 == 0 ? -(band + 0.05f) : -(band - 0.05f), shoot: true));
            Step(w, At(releaseZ, shoot: true));
            Step(w, At(releaseZ, shoot: false));
            for (int i = 0; i < 600 && w.Shots.Count == 0; i++) Step(w, At(afterZ, shoot: false));
            Assert.Single(w.Shots);
            Assert.Equal(releaseZ, w.Shots[0].Aimed.X, 1);
            return w.Shots[0];
        }
        float edge = T.Shot.CurlInCentreM;
        // A right-footer aiming at the -Z half needs his left instep; inside the band it is his right.
        var outside = Shoot(-(edge + 0.1f), afterZ: 3f);
        Assert.Equal((Foot.Left, FootSurface.Inside), (outside.Foot, outside.Surface));
        var inside = Shoot(-(edge - 0.1f), afterZ: -3f);
        Assert.Equal((Foot.Right, FootSurface.Inside), (inside.Foot, inside.Surface));
    }

    [Fact]
    public void AShotTurnedIntoALowFinesseByTheSecondPressTakesTheCurlInFoot()
    {
        // Power pressed and let go at the -Z post (a right-footer's strong foot, by the geometry), then shoot again with
        // finesse held before the strike: a low finesse, which needs his left instep to curl in.
        ShotResult Shoot(bool finesseOnSecondPress)
        {
            var w = new World(new Tuning { ErrorScale = 0f }, TestData.Scenario("penalty"), Sheet(Foot.Right, trait: false), TestData.Player("the_wall"), 5);
            w.Begin();
            var dir = Vector2.Normalize(new Vector2(w.Pitch.GoalLineX - w.Ball.Position.X, -3f - w.Ball.Position.Z));
            var stick = new TickIntent { MoveX = dir.X, MoveZ = dir.Y };
            for (int i = 0; i < 20; i++) Step(w, stick with { ShootHeld = true });
            Step(w, stick);
            Step(w, stick with { ShootHeld = true, Finesse = finesseOnSecondPress });
            for (int i = 0; i < 600 && w.Shots.Count == 0; i++) Step(w, stick);
            Assert.Single(w.Shots);
            return w.Shots[0];
        }
        var driven = Shoot(false);
        Assert.Equal((ShotType.Driven, Foot.Right), (driven.Type, driven.Foot));
        var low = Shoot(true);
        Assert.Equal((ShotType.LowFinesse, Foot.Left, FootSurface.Inside), (low.Type, low.Foot, low.Surface));
        Assert.True(BendZ(low) > 0f, "aimed at the -Z post, it bends toward +Z");
    }

    // ---- the launch solve ----

    /// <summary>Where a struck ball crosses the goal line (Z), flying over the ground alone; NaN if it never gets there.</summary>
    static float CrossesAt(Vector3 origin, ShotResult r, Tuning t)
    {
        var b = new Ball(t.Ball) { Position = origin, Velocity = r.Launch, Spin = r.Spin };
        for (int i = 0; i < 8 * t.TickRate; i++)
        {
            var before = b.Position;
            b.Step(t.Dt, null, null);
            if (b.Position.X >= P.GoalLineX)
                return before.Z + (b.Position.Z - before.Z) * (P.GoalLineX - before.X) / (b.Position.X - before.X);
        }
        return float.NaN;
    }

    [Fact]
    public void AShotWithNoErrorCrossesTheLineWhereItWasAimed()
    {
        // The acceptance grid: five types, curve 30 / 72 / 95, both feet, 8 to 25 m and two angled positions, both posts.
        var t = new Tuning { ErrorScale = 0f };
        t.Shot.SpeedNoise = 0f;
        float post = P.GoalHalfWidth - 0.6f;
        var misses = new List<string>();
        float worst = 0f, biggestTurn = 0f;
        int rows = 0;
        foreach (var type in Types)
            foreach (int curve in new[] { 30, 72, 95 })
                foreach (Foot foot in new[] { Foot.Right, Foot.Left })
                    foreach (var (outM, z) in new[] { (8f, 0f), (12f, 0f), (16f, 0f), (20f, 0f), (25f, 0f), (12f, 6f), (16f, -8f) })
                        foreach (float aimZ in new[] { -post, post })
                        {
                            var ball = BallAt(outM, z);
                            float yaw = Bearing(ball, aimZ);
                            var s = Sheet(Foot.Right, trait: false, curve);
                            var shooter = TestData.Outfield(s, ball.X - 0.5f * MathF.Cos(yaw), ball.Z - 0.5f * MathF.Sin(yaw), yaw);
                            float charge = ShotModel.IdealCharge(ShotModel.GoalDistance(ball, P), t);
                            var ctx = new ShotContext { Shooter = shooter, BallPosition = ball, Stick = Stick(yaw), Charge = charge, Type = type, Foot = foot };
                            for (ulong seed = 1; seed <= 2; seed++)   // (a power or driven shot's light curl goes to a drawn side)
                            {
                                var r = ShotModel.Compute(ctx, P, t, new SimRng(seed));
                                Assert.Equal(aimZ, r.Aimed.X, 3);
                                float at = CrossesAt(ball, r, t);
                                string id = $"{type}, curve {curve}, {foot} foot, {outM} m at z {z}, aimed {aimZ:F2}, seed {seed}";
                                rows++;
                                if (float.IsNaN(at)) { misses.Add($"{id}: never reached the goal line"); continue; }
                                float miss = MathF.Abs(at - aimZ);
                                worst = MathF.Max(worst, miss); biggestTurn = MathF.Max(biggestTurn, MathF.Abs(r.AdjustDeg));
                                if (miss > 0.05f) misses.Add($"{id}: crossed at {at:F3} ({miss:F3} m off, turned {r.AdjustDeg:F2} degrees)");
                            }
                        }
        Assert.True(misses.Count == 0, $"{misses.Count} of {rows} rows missed (worst {worst:F3} m):\n" + string.Join("\n", misses.Take(40)));
        // (the solve is doing something: a finesse from range is turned by several degrees)
        Assert.True(biggestTurn > 3f, $"the largest turn was only {biggestTurn:F2} degrees");
    }

    [Fact]
    public void TheSolveTurnsTheSpinWithTheLaunchAndTheErrorIsAddedAfter()
    {
        var p = TestData.Outfield(Sheet(Foot.Right, trait: false), P.GoalLineX - 16.5f, 0f, 0f);
        var ball = BallAt(16f, 0f);
        var ctx = new ShotContext { Shooter = p, BallPosition = ball, Stick = Stick(Bearing(ball, 3f)), Charge = 0.6f, Type = ShotType.Finesse, Foot = Foot.Right };
        var r = ShotModel.Compute(ctx, P, T, new SimRng(9));
        // A right instep bends toward -Z, so the launch is turned toward +Z; the yaw error drawn sits on top of the turn.
        Assert.True(r.AdjustDeg > 1f);
        float launchYaw = MathF.Atan2(r.Launch.Z, r.Launch.X);
        Assert.Equal(r.IntendedYaw + (r.AdjustDeg + r.YawErrorDeg) * Deg, launchYaw, 4);
        Assert.Equal(ball, r.Origin);
        Assert.Equal(Bearing(ball, 3f), r.StickYaw, 4);
        // The same draws in the same order as before the solve existed: yaw, pitch, speed (a finesse draws nothing for its spin).
        var rng = new SimRng(9);
        Assert.Equal(rng.Normal(0f, r.SigmaH), r.YawErrorDeg, 4);
        // The spin is square to the launch's own direction (its side spin turned with it), not to the intended one.
        Assert.Equal(0f, Vector3.Dot(Vector3.Normalize(r.Spin), Vector3.Normalize(r.Launch)), 3);
    }

    [Fact]
    public void SolveWhenTheFlightFallsShort()
    {
        // A flight that never reaches the aim's distance (a ball that stops, a weak shot from range): the turn is 0.
        var ball = BallAt(25f, 0f);
        Assert.Equal(0f, ShotModel.LaunchTurn(ball, 0f, 25f, new Vector3(2f, 0f, 0f), new Vector3(0f, 30f, 0f), T));
        Assert.Equal(0f, ShotModel.LaunchTurn(ball, 0f, 25f, Vector3.Zero, Vector3.Zero, T));
        Assert.Equal(0f, ShotModel.LaunchTurn(ball, 0f, 0f, new Vector3(20f, 3f, 0f), new Vector3(0f, 30f, 0f), T));
        Assert.Equal(0f, ShotModel.LaunchTurn(ball, 0f, float.NaN, new Vector3(20f, 3f, 0f), new Vector3(0f, 30f, 0f), T));
        // Through the whole shot: the weakest finesse there is, from as far out as the pitch goes. Nothing is NaN.
        var weak = TestData.Sheet(1);
        var far = new Vector3(P.GoalLineX - 60f, T.Ball.Radius, 5f);
        var p = TestData.Outfield(weak, far.X - 0.5f, far.Z, 0f);
        foreach (var type in Types)
        {
            var r = ShotModel.Compute(new ShotContext { Shooter = p, BallPosition = far, Stick = Stick(Bearing(far, -3f)), Charge = 0f, Type = type }, P, T, new SimRng(2));
            Assert.Equal(0f, r.AdjustDeg);
            Assert.True(float.IsFinite(r.Launch.X) && float.IsFinite(r.Launch.Y) && float.IsFinite(r.Launch.Z));
            Assert.True(float.IsFinite(r.Spin.X) && float.IsFinite(r.Spin.Y) && float.IsFinite(r.Spin.Z));
        }
        // A ball on or behind the goal line: the solve is skipped.
        var on = new Vector3(P.GoalLineX, T.Ball.Radius, 5f);
        var behind = TestData.Outfield(TestData.Sheet(60), on.X, on.Z + 0.5f, -MathF.PI / 2);
        Assert.Equal(0f, ShotModel.Compute(new ShotContext { Shooter = behind, BallPosition = on, Stick = new Vector2(0f, -1f), Charge = 0.5f, Type = ShotType.Finesse }, P, T, new SimRng(2)).AdjustDeg);
    }

    // ---- the level in the world and the replay ----

    [Fact]
    public void TheLevelRoundTripsThroughAReplayAndAReplayWithoutItRunsAtOff()
    {
        var t = new Tuning();
        var sc = TestData.Scenario("penalty");
        var att = TestData.Player("striker_a"); var kee = TestData.Player("the_wall");
        // A stick 5 degrees wide of the +Z post: at Off the shot is aimed wide, at Medium it is aimed at the safe point.
        Replay Record(AimAssistLevel level)
        {
            var w = new World(t, sc, att, kee, 7) { AimAssist = level };
            w.Recorder = new ReplayRecorder(w, att.Id, kee.Id, sc.Id);
            w.Begin();
            float yaw = MathF.Atan2(w.Pitch.GoalHalfWidth - w.Ball.Position.Z, w.Pitch.GoalLineX - w.Ball.Position.X) + 5f * Deg;
            var held = new TickIntent { MoveX = MathF.Cos(yaw), MoveZ = MathF.Sin(yaw), ShootHeld = true };
            for (int i = 0; i < 30; i++) Step(w, held);
            for (int i = 0; i < 400 && w.Shots.Count == 0; i++) Step(w, held with { ShootHeld = false });
            Assert.Single(w.Shots);
            return w.Recorder.Replay;
        }
        float safe = P.GoalHalfWidth - t.Shot.AimAssist.InsetM;
        var medium = Record(AimAssistLevel.Medium);
        Assert.Contains("\"aim_assist\": \"medium\"", medium.ToJson());
        var back = Replay.FromJson(medium.ToJson(), out var error);
        Assert.Null(error);
        Assert.Equal(AimAssistLevel.Medium, back!.AimAssist);
        Assert.Equal(AimAssistLevel.Medium, back.Copy().AimAssist);
        var played = back.Play(t, sc, att, kee);
        Assert.Equal(AimAssistLevel.Medium, played.AimAssist);
        Assert.Equal(safe, played.Shots[0].Aimed.X, 3);

        // Off is left out of the file, and a file without the field runs at Off: the same intents, aimed where the stick points.
        var off = Record(AimAssistLevel.Off);
        Assert.DoesNotContain("aim_assist", off.ToJson());
        string stripped = medium.ToJson().Replace("\"aim_assist\": \"medium\",", "");
        Assert.DoesNotContain("aim_assist", stripped);
        var old = Replay.FromJson(stripped, out error);
        Assert.Null(error);
        Assert.Equal(AimAssistLevel.Off, old!.AimAssist);
        var wide = old.Play(t, sc, att, kee);
        Assert.Equal(AimAssistLevel.Off, wide.AimAssist);
        Assert.True(wide.Shots[0].Aimed.X > P.GoalHalfWidth + 0.5f, $"aimed {wide.Shots[0].Aimed.X}");
        Assert.Equal(off.Play(t, sc, att, kee).Shots[0].Aimed.X, wide.Shots[0].Aimed.X);
    }
}
