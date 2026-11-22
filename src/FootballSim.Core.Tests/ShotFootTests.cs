using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>
/// The striking foot and the angle a shot is struck at: the foot chooser's zones (strong foot only across the body, weak foot
/// only beyond the strong foot's limit, both scored in between, the outside of the foot with the trait), its timing terms, the
/// body-angle and goal-mouth terms, the weak-foot and outside-foot terms, and the foot and aim locked in the world.
/// </summary>
public class ShotFootTests
{
    static readonly Tuning T = new();
    static readonly Pitch P = new(new PitchSpec(), T.Frame);
    const float Deg = MathF.PI / 180f;

    /// <summary>striker_a's ratings (curve 72), with the weak foot, preferred foot and trait under test.</summary>
    static PlayerSheet Sheet(int weakFoot, bool trait, Foot preferred = Foot.Right)
    {
        var s = TestData.Player("striker_a").Clone();
        s.WeakFoot = weakFoot;
        s.PreferredFoot = preferred;
        s.Traits = trait ? PlayerTrait.OutsideFoot : PlayerTrait.None;
        return s;
    }

    /// <summary>The choice for a ball straight ahead of a player at the origin facing +X, leaving <paramref name="yawDeg"/> to his right.</summary>
    static StrikeChoice Choose(PlayerSheet s, float yawDeg, ShotType type = ShotType.Power, int right = 0, int left = 0, float ballRight = 0f, Tuning? t = null) =>
        ShotModel.ChooseStrike(s, Vector3.Zero, 0f, new Vector3(0.5f, T.Ball.Radius, ballRight), yawDeg * Deg, type, t ?? T, right, left);

    [Theory]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public void TheFootZonesOfARightFootedPlayerAndTheirMirrorForALeftFootedOne(int weakFoot, bool trait)
    {
        var k = T.Shot;
        // The weak foot's own cost against the strong foot's opening cost: where the weak foot takes over without the outside.
        float weakCost = k.WWeak * (5 - weakFoot) / 4f;
        float takeover = k.OutsideMinDeg + (k.OpenMaxDeg - k.OutsideMinDeg) * weakCost / k.WOpen;
        Assert.Equal(weakFoot == 3 ? 42.75f : 63f, takeover, 2);
        foreach (Foot strong in new[] { Foot.Right, Foot.Left })
        {
            var s = Sheet(weakFoot, trait, strong);
            Foot weak = strong == Foot.Right ? Foot.Left : Foot.Right;
            float cap = ShotModel.OutsideMaxDeg(s, ShotType.Power, T);
            Assert.InRange(cap, 56f, 58f);                         // curve 72: about 57 degrees
            for (float open = -90f; open <= 90f; open += 2.5f)
            {
                // 'open' is toward the strong foot's own side; a left-footed player's is his left.
                var c = Choose(s, strong == Foot.Right ? open : -open);
                string id = $"{strong}-footed, weak foot {weakFoot}, trait {trait}, {open} degrees to the strong side";
                bool outside = trait && open > k.OutsideMinDeg && open <= cap;
                if (open > k.OpenMaxDeg) Assert.True(c.Foot == weak, $"{id}: beyond the strong foot's limit only the weak foot strikes");
                else if (open < -k.OpenMaxDeg) Assert.True(c.Foot == strong, $"{id}: across the body beyond the weak foot's limit only the strong foot strikes");
                else if (outside) Assert.True(c.Foot == strong, $"{id}: the outside of the strong foot");
                else if (open < takeover - 1f) Assert.True(c.Foot == strong, $"{id}: the strong foot is cheaper");
                else if (open > takeover + 1f) Assert.True(c.Foot == weak, $"{id}: the weak foot is cheaper than opening the hips");
                Assert.True((c.Surface == FootSurface.Outside) == (outside && c.Foot == strong), $"{id}: surface {c.Surface}");
                if (c.Surface != FootSurface.Outside) Assert.Equal(FootSurface.Laces, c.Surface);
            }
        }
    }

    [Fact]
    public void TheOutsideOfTheFootNeedsTheTrait()
    {
        foreach (var type in new[] { ShotType.Power, ShotType.Driven, ShotType.Finesse, ShotType.LowFinesse, ShotType.Chip })
            for (float yaw = -180f; yaw <= 180f; yaw += 5f)
                foreach (int wf in new[] { 1, 3, 5 })
                    Assert.NotEqual(FootSurface.Outside, Choose(Sheet(wf, trait: false), yaw, type).Surface);
        Assert.Equal(new StrikeChoice(Foot.Right, FootSurface.Outside), Choose(Sheet(3, trait: true), 45f));
        Assert.Equal(new StrikeChoice(Foot.Left, FootSurface.Outside), Choose(Sheet(3, trait: true, Foot.Left), -45f));
        // The band ends at the curve rating's cap: a poor curler's outside reaches less far.
        var poor = Sheet(3, trait: true);
        poor.Curve = 1;
        Assert.Equal(30f, ShotModel.OutsideMaxDeg(poor, ShotType.Power, T), 3);
        Assert.Equal(FootSurface.Outside, Choose(poor, 28f).Surface);
        Assert.NotEqual(FootSurface.Outside, Choose(poor, 32f).Surface);
    }

    [Fact]
    public void ATieGoesToTheStrongFootAndStraightBehindIsAcrossTheBodyForIt()
    {
        foreach (Foot strong in new[] { Foot.Right, Foot.Left })
        {
            Assert.Equal(strong, Choose(Sheet(5, false, strong), 0f).Foot);     // five stars: both feet cost nothing straight on
            Assert.Equal(strong, Choose(Sheet(3, false, strong), 180f).Foot);
            Assert.Equal(strong, Choose(Sheet(3, false, strong), -180f).Foot);
            Assert.Equal(strong, Choose(Sheet(3, false, strong), 179.9f).Foot);
        }
    }

    [Fact]
    public void AFinesseShotOpensFurtherThanAPowerShot()
    {
        Assert.Equal(67.5f, ShotModel.OpenMaxDeg(ShotType.Power, T));
        Assert.Equal(67.5f, ShotModel.OpenMaxDeg(ShotType.Chip, T));
        Assert.Equal(79f, ShotModel.OpenMaxDeg(ShotType.Finesse, T));
        Assert.Equal(79f, ShotModel.OpenMaxDeg(ShotType.LowFinesse, T));
        // With a weak foot too costly to choose, 75 degrees is still the weak foot's for a power shot, the strong foot's for a finesse.
        var t = new Tuning();
        t.Shot.WWeak = 5f;
        var s = Sheet(1, false);
        Assert.Equal(Foot.Left, Choose(s, 75f, ShotType.Power, t: t).Foot);
        Assert.Equal(Foot.Right, Choose(s, 75f, ShotType.Finesse, t: t).Foot);
        Assert.Equal(Foot.Left, Choose(s, 80f, ShotType.Finesse, t: t).Foot);
    }

    [Fact]
    public void TheBallOnTheOtherFootsSideCountsAgainstAFoot()
    {
        // 0.4 m to the left of a right-footed player: a three-star weak foot takes it, a one-star player still uses his right.
        Assert.Equal(Foot.Left, Choose(Sheet(3, false), 0f, ballRight: -0.4f).Foot);
        Assert.Equal(Foot.Right, Choose(Sheet(1, false), 0f, ballRight: -0.4f).Foot);
        Assert.Equal(Foot.Right, Choose(Sheet(3, false), 0f, ballRight: -0.1f).Foot);
        Assert.Equal(Foot.Right, Choose(Sheet(3, false), 0f, ballRight: 0.4f).Foot);
    }

    [Fact]
    public void TheWaitForAFootsSwingCountsAndAFootTooLateOrWithoutAContactIsPassedOver()
    {
        var s3 = Sheet(3, false);
        var s1 = Sheet(1, false);
        // Straight on, the strong foot's swing 15 ticks (0.125 s) later: worth the wait. 20 ticks (0.167 s): not for three stars.
        Assert.Equal(Foot.Right, Choose(s3, 0f, right: 95, left: 80).Foot);
        Assert.Equal(Foot.Left, Choose(s3, 0f, right: 100, left: 80).Foot);
        Assert.Equal(Foot.Right, Choose(s1, 0f, right: 100, left: 80).Foot);
        // More than foot_wait_max_s later: not waited for, even by a one-star weak foot.
        Assert.Equal(Foot.Left, Choose(s1, 0f, right: 120, left: 80).Foot);
        // ... unless the other foot cannot open that far: across the body the strong foot is waited for.
        Assert.Equal(Foot.Right, Choose(s3, -80f, right: 120, left: 80).Foot);
        // A foot with no contact at all gives way to the other, whatever the angle; with none for either the angle decides.
        Assert.Equal(Foot.Right, Choose(s3, 80f, right: 90, left: -1).Foot);
        Assert.Equal(Foot.Left, Choose(s3, -80f, right: -1, left: 90).Foot);
        Assert.Equal(Foot.Left, Choose(s3, 80f, right: -1, left: -1).Foot);
        Assert.Equal(Foot.Right, Choose(s3, -80f, right: -1, left: -1).Foot);
    }

    static PlayerState Shooter(float facing = 0f, float x = 41f, float z = 0f, PlayerSheet? sheet = null)
    {
        var s = sheet ?? TestData.Sheet(60);
        return TestData.Outfield(s, x, z, facing);
    }

    static ShotContext Ctx(PlayerState p, Vector3? ball = null, ShotType type = ShotType.Power, Vector2? stick = null, Foot? foot = null, FootSurface? surface = null) => new()
    {
        Shooter = p,
        BallPosition = ball ?? new Vector3(p.Position.X + 0.5f, T.Ball.Radius, p.Position.Z),
        Stick = stick ?? new Vector2(1f, 0f),
        Charge = 0.5f,
        Type = type,
        Foot = foot,
        Surface = surface,
    };

    [Fact]
    public void TheBodyAngleSpreadsSlowsAndDelaysTheShot()
    {
        float straight = ShotModel.Compute(Ctx(Shooter()), P, T, new SimRng(1)).Speed;
        foreach (float deg in new[] { 0f, 20f, 45f, 60f, 90f, 120f, 135f, 180f })
        {
            // The shot goes along +X; the shooter faces 'deg' away from it.
            var r = ShotModel.Compute(Ctx(Shooter(facing: deg * Deg)), P, T, new SimRng(1));
            Assert.Equal(deg, r.BodyAngleDeg, 2);
            Assert.Equal(T.Shot.AngleAccuracy.Evaluate(deg), r.Factor("angle"), 3);
            Assert.Equal(T.Shot.AnglePower.Evaluate(deg), r.SpeedFactor("angle"), 3);
            Assert.Equal(straight * T.Shot.AnglePower.Evaluate(deg), r.Speed, 2);    // the same seed: the same speed noise
            Assert.Equal(T.Shot.AngleExtraWindupS.Evaluate(deg), ShotModel.ExtraWindupSeconds(deg * Deg, 0f, T), 4);
        }
        Assert.Equal(1f, T.Shot.AngleAccuracy.Evaluate(30f));
        Assert.Equal(1f, T.Shot.AnglePower.Evaluate(45f));
        Assert.Equal(0f, T.Shot.AngleExtraWindupS.Evaluate(45f));
        Assert.Equal(0.30f, ShotModel.ExtraWindupSeconds(MathF.PI, 0f, T), 4);
        Assert.True(T.Shot.AngleAccuracy.Evaluate(180f) > T.Shot.AngleAccuracy.Evaluate(90f));
        Assert.True(T.Shot.AnglePower.Evaluate(180f) < T.Shot.AnglePower.Evaluate(90f));
    }

    [Fact]
    public void TheGoalMouthTermGrowsAsTheMouthClosesAndStaysFiniteOnAndBehindTheGoalLine()
    {
        float gx = P.GoalLineX, hw = P.GoalHalfWidth, r0 = T.Ball.Radius;
        // In front of goal at any distance the mouth is fully open.
        foreach (float d in new[] { 5f, 11f, 20f, 30f })
            Assert.Equal(1f, ShotModel.GoalMouthOpen(new Vector3(gx - d, r0, 0f), P), 4);
        // Out wide near the goal line it closes.
        float wide = ShotModel.GoalMouthOpen(new Vector3(gx - 4.7f, r0, 8.5f), P);
        float tight = ShotModel.GoalMouthOpen(new Vector3(gx - 0.3f, r0, 5.2f), P);
        Assert.InRange(wide, 0.5f, 0.62f);
        Assert.InRange(tight, 0.1f, 0.16f);
        float first = T.Shot.GoalMouthAccuracy[0].Y;
        foreach (var ball in new[] { new Vector3(gx, r0, hw + 2f), new Vector3(gx + 0.5f, r0, hw + 2f), new Vector3(gx, r0, 0f), new Vector3(gx - 0.3f, r0, 5.2f), new Vector3(gx - 4.7f, r0, 8.5f) })
        {
            var p = Shooter(facing: -MathF.PI / 2, x: ball.X, z: ball.Z + 0.5f);
            foreach (var stick in new[] { Vector2.Zero, new Vector2(0f, -1f), new Vector2(1f, 0f) })
            {
                var r = ShotModel.Compute(Ctx(p, ball, stick: stick), P, T, new SimRng(3));
                Assert.True(float.IsFinite(r.SigmaH) && r.SigmaH > 0f, $"ball {ball}: sigma {r.SigmaH}");
                Assert.True(float.IsFinite(r.Launch.X) && float.IsFinite(r.Launch.Y) && float.IsFinite(r.Launch.Z));
                Assert.Equal(T.Shot.GoalMouthAccuracy.Evaluate(ShotModel.GoalMouthOpen(ball, P)), r.Factor("mouth"), 4);
                if (ball.X >= gx) Assert.Equal(first, r.Factor("mouth"), 4);   // on or behind the line: the curve's first point
            }
        }
        // On or behind the goal line the intended yaw falls back to the stick (the facing with the stick at rest).
        var behind = Shooter(facing: -MathF.PI / 2, x: gx, z: hw + 2.5f);
        var ballOn = new Vector3(gx, r0, hw + 2f);
        Assert.Equal(-MathF.PI / 2, ShotModel.Compute(Ctx(behind, ballOn, stick: Vector2.Zero), P, T, new SimRng(1)).IntendedYaw, 4);
        Assert.Equal(MathF.PI, MathF.Abs(ShotModel.Compute(Ctx(behind, ballOn, stick: new Vector2(-1f, 0f)), P, T, new SimRng(1)).IntendedYaw), 4);
        Assert.True(wide > tight);
        Assert.True(T.Shot.GoalMouthAccuracy.Evaluate(tight) > T.Shot.GoalMouthAccuracy.Evaluate(wide));
    }

    [Fact]
    public void AWeakFootStrikeLosesPowerAndCurlByItsStars()
    {
        foreach (int wf in new[] { 1, 3, 5 })
        {
            var s = TestData.Sheet(60);
            s.WeakFoot = wf;
            var strong = ShotModel.Compute(Ctx(Shooter(sheet: s), type: ShotType.Finesse, foot: Foot.Right), P, T, new SimRng(1));
            var weak = ShotModel.Compute(Ctx(Shooter(sheet: s), type: ShotType.Finesse, foot: Foot.Left), P, T, new SimRng(1));
            float power = T.Shot.WeakFootPower.Evaluate(wf), curl = T.Shot.WeakFootCurl.Evaluate(wf);
            Assert.Equal(1f, strong.SpeedFactor("weak_foot"));
            Assert.Equal(1f, strong.CurlFactor);
            Assert.Equal(power, weak.SpeedFactor("weak_foot"), 4);
            Assert.Equal(curl, weak.CurlFactor, 4);
            Assert.Equal(strong.Speed * power, weak.Speed, 2);
            // Spin grows with speed, so the weak foot's is the strong foot's times both.
            Assert.Equal(strong.Spin.Length() * power * curl, weak.Spin.Length(), 1);
        }
        Assert.Equal(0.85f, T.Shot.WeakFootPower.Evaluate(1f), 4);
        Assert.Equal(1f, T.Shot.WeakFootPower.Evaluate(5f), 4);
        Assert.Equal(0.6f, T.Shot.WeakFootCurl.Evaluate(1f), 4);
        Assert.Equal(1f, T.Shot.WeakFootCurl.Evaluate(5f), 4);
    }

    [Fact]
    public void AnOutsideOfTheFootStrikeIsLessAccurateSlowerAndBendsTheOtherWay()
    {
        var p = Shooter();
        var inside = ShotModel.Compute(Ctx(p, type: ShotType.Finesse, foot: Foot.Right), P, T, new SimRng(1));
        var outside = ShotModel.Compute(Ctx(p, type: ShotType.Finesse, foot: Foot.Right, surface: FootSurface.Outside), P, T, new SimRng(1));
        Assert.Equal(FootSurface.Inside, inside.Surface);
        Assert.Equal(FootSurface.Outside, outside.Surface);
        Assert.Equal(1f, inside.Factor("surface"));
        Assert.Equal(T.Shot.OutsideAccuracy, outside.Factor("surface"), 4);
        Assert.Equal(T.Shot.OutsideSpeed, outside.SpeedFactor("surface"), 4);
        Assert.Equal(inside.SigmaH * T.Shot.OutsideAccuracy, outside.SigmaH, 3);
        // A right foot's inside bends the ball to the left (-Z), its outside to the right: the sideways force is spin x velocity.
        Assert.True(Vector3.Cross(inside.Spin, inside.Launch).Z < 0f);
        Assert.True(Vector3.Cross(outside.Spin, outside.Launch).Z > 0f);
    }

    [Fact]
    public void AWildAimNowMissesTheFrame()
    {
        Assert.Equal(8f, T.Shot.MaxTargetZ);
        var ball = new Vector3(P.GoalLineX - 12f, 0.11f, 0f);
        Assert.Equal(8f, ShotModel.RawTargetZ(new Vector2(1f, 1f), 0f, ball, P, T), 4);
        Assert.Equal(6f, ShotModel.RawTargetZ(new Vector2(1f, 0.5f), 0f, ball, P, T), 4);   // beyond the assist range: left alone
        Assert.Equal(6f, ShotModel.AssistTargetZ(6f, P, T), 4);
    }

    // ---- in the world ----

    static void Step(World w, TickIntent it)
    {
        var buf = new TickIntent[w.PlayerCount];
        buf[0] = it;
        w.Step(buf);
    }

    static TickIntent Dir(float deg, bool shoot = false, float sprint = 0f)
    {
        float a = deg * Deg;
        return new TickIntent { MoveX = MathF.Cos(a), MoveZ = MathF.Sin(a), ShootHeld = shoot, Sprint = sprint };
    }

    [Fact]
    public void ABackToGoalShotIsStruckLaterSlowerAndWilderThanTheSameShotFacingGoal()
    {
        var back = TestData.Scenario("back_to_goal");
        var facing = TestData.Scenario("back_to_goal");
        facing.Attacker = new StartSpot { X = back.Ball.X - 0.6f, Z = 0f, FacingDeg = 0f };
        ShotResult Shoot(Scenario sc, out int pressToStrike)
        {
            var w = new World(new Tuning(), sc, TestData.Player("striker_a"), TestData.Player("the_wall"), 5);
            w.Begin();
            Step(w, new TickIntent { ShootHeld = true });
            int press = w.Tick;
            for (int i = 0; i < 12; i++) Step(w, new TickIntent { ShootHeld = true });
            Step(w, Dir(0f));                                   // released with the stick at the goal
            for (int i = 0; i < 400 && w.Shots.Count == 0; i++) Step(w, default);
            Assert.Single(w.Shots);
            pressToStrike = w.Shots[0].Tick - press;
            return w.Shots[0];
        }
        var b = Shoot(back, out int tb);
        var f = Shoot(facing, out int tf);
        Assert.True(f.BodyAngleDeg < 30f && b.BodyAngleDeg > 150f, $"body angles {f.BodyAngleDeg} and {b.BodyAngleDeg}");
        Assert.Equal(54, tf);                                                         // the 0.45 s wind-up
        Assert.True(tb - tf >= 24, $"struck {tb - tf} ticks later");                  // at least 0.2 s more
        Assert.True(b.Speed <= 0.7f * f.Speed, $"speed {b.Speed} against {f.Speed}");
        Assert.True(b.SigmaH >= 3f * f.SigmaH, $"sigma {b.SigmaH} against {f.SigmaH}");
        Assert.Equal(Foot.Right, b.Foot);                                             // behind him is across the body: the strong foot
    }

    [Fact]
    public void TheShotActionCarriesTheAimTheFootAndTheSurfaceFixedFromTheRelease()
    {
        // The ball at his feet out wide, facing the goal (-61 degrees); released with the stick at -20 degrees, 41 degrees to his
        // right: striker_a has the trait, so the outside of his right foot.
        var w = TestData.Live("tight_angle");
        var p = w.Attacker;
        float facing = p.Facing;
        Step(w, new TickIntent { ShootHeld = true });
        var shot = p.Action!;
        Assert.True(shot.IsShot);
        Assert.Equal(facing, shot.AimYaw, 1);                  // the stick at rest: where he faces
        Assert.Equal(new StrikeChoice(Foot.Right, FootSurface.Laces), new StrikeChoice(shot.Foot, shot.Surface));
        for (int i = 0; i < 10; i++) Step(w, new TickIntent { ShootHeld = true });
        Step(w, Dir(-20f));
        Assert.True(shot.Released);
        float aim = shot.AimYaw;
        Assert.Equal(-20f, aim / Deg, 1);
        Assert.InRange(ShotModel.OpenAngleDeg(Foot.Right, Foot.Right, p.Facing, aim), 35f, 45f);
        Assert.Equal(Foot.Right, shot.Foot);
        Assert.Equal(FootSurface.Outside, shot.Surface);
        for (int i = 0; i < 400 && w.Shots.Count == 0; i++)
        {
            Step(w, default);
            Assert.Equal(aim, shot.AimYaw);
            Assert.Equal(Foot.Right, shot.Foot);
            Assert.Equal(FootSurface.Outside, shot.Surface);
        }
        var r = Assert.Single(w.Shots);
        Assert.Equal(FootSurface.Outside, r.Surface);
        Assert.Equal(Foot.Right, r.Foot);
        Assert.Equal(aim, r.IntendedYaw, 2);
        Assert.Equal(T.Shot.OutsideAccuracy, r.Factor("surface"), 4);
        Assert.Equal(T.Shot.OutsideSpeed, r.SpeedFactor("surface"), 4);

        // The same shot by a player without the trait: no outside.
        var plain = TestData.Player("striker_a").Clone();
        plain.Traits = PlayerTrait.None;
        var w2 = new World(new Tuning(), TestData.Scenario("tight_angle"), plain, TestData.Player("the_wall"), 1);
        w2.Begin();
        for (int i = 0; i < 11; i++) Step(w2, new TickIntent { ShootHeld = true });
        Step(w2, Dir(-20f));
        Assert.NotEqual(FootSurface.Outside, w2.Attacker.Action!.Surface);
        for (int i = 0; i < 400 && w2.Shots.Count == 0; i++) Step(w2, default);
        Assert.NotEqual(FootSurface.Outside, Assert.Single(w2.Shots).Surface);
    }

    [Fact]
    public void InADribbleTheStrikeWaitsForTheFootChosenAtTheReleaseAndNeverChangesIt()
    {
        int shots = 0, waited = 0;
        foreach (var (run, aim) in new[] { (0f, 0f), (0f, 60f), (0f, -60f), (0f, 100f), (0f, -100f), (30f, -40f) })
            foreach (float sprint in new[] { 0f, 1f })
                for (ulong seed = 1; seed <= 6; seed++)
                {
                    var sc = new Scenario { Id = "lab", Pitch = new PitchSpec { Length = 300f, Width = 200f }, Ball = new StartSpot { X = 2.5f }, Attacker = new StartSpot { X = 2.0f } };
                    var w = new World(new Tuning(), sc, TestData.Player("striker_a"), TestData.Sheet(50), seed);
                    w.Begin();
                    var p = w.Attacker;
                    for (int i = 0; i < 300 + (int)seed * 7; i++) Step(w, Dir(run, sprint: sprint));
                    Assert.True(p.InPossession);
                    string id = $"run {run} aim {aim} sprint {sprint} seed {seed}";
                    // Pressed with the ball at the feet it is struck at the earliest strike tick, whatever the stride is doing.
                    bool atFoot = TouchModel.InReach(p, w.Ball, w.Tuning) && p.TouchAction is not { Pending: true };
                    for (int i = 0; i < 20; i++) Step(w, Dir(i < 3 ? run : aim, shoot: true, sprint: sprint));
                    Step(w, Dir(aim, sprint: sprint));
                    var shot = p.Action!;
                    Assert.True(shot.IsShot && shot.Released, id);
                    Foot foot = shot.Foot;
                    var surface = shot.Surface;
                    float aimYaw = shot.AimYaw;
                    // What the release decided is what a fresh choice for its aim gives when the angle rules a foot out.
                    float open = ShotModel.OpenAngleDeg(Foot.Right, Foot.Right, p.Facing, aimYaw);
                    if (open > T.Shot.OpenMaxDeg + 5f) Assert.True(foot == Foot.Left, $"{id}: {open:F0} degrees to the right is the left foot's");
                    if (open < -T.Shot.OpenMaxDeg - 5f) Assert.True(foot == Foot.Right, $"{id}: {open:F0} degrees to the left is the right foot's");
                    for (int i = 0; i < 600 && w.Shots.Count == 0; i++)
                    {
                        Step(w, Dir(run + 150f, sprint: sprint));        // the stick swung away after the release changes nothing
                        if (w.Shots.Count > 0) break;
                        Assert.True(p.Action is { IsShot: true }, $"{id}: the shot was dropped");
                        Assert.True(shot.Foot == foot && p.StrikeFoot == foot, $"{id}: the foot changed after the release");
                        Assert.True(shot.Surface == surface, $"{id}: the surface changed after the release");
                        Assert.Equal(aimYaw, shot.AimYaw);
                    }
                    var r = Assert.Single(w.Shots);
                    shots++;
                    Assert.True(r.Foot == foot, $"{id}: struck with the {r.Foot} foot, chosen {foot}");
                    Assert.Equal(surface, r.Surface);
                    Assert.Equal(foot, shot.Foot);
                    if (!atFoot && p.IsStepping)
                    {
                        Assert.True(p.SwingFoot == foot, $"{id}: struck on the {p.SwingFoot} foot's swing");
                        waited++;
                    }
                }
        Assert.Equal(72, shots);
        Assert.True(waited > 30, $"{waited} strikes on the chosen foot's swing");
    }

    [Fact]
    public void TheFootChoiceAndTheLongerWindupDrawNoRandomNumbers()
    {
        // The same round twice: an angled dribble shot (the chooser's searches, the angle wind-up) gives the same strike.
        ShotResult Run()
        {
            var w = TestData.Live("halfway_run", seed: 9);
            var p = w.Attacker;
            for (int i = 0; i < 260; i++) Step(w, Dir(0f, sprint: 1f));
            for (int i = 0; i < 18; i++) Step(w, Dir(110f, shoot: true, sprint: 1f));
            for (int i = 0; i < 600 && w.Shots.Count == 0; i++) Step(w, Dir(110f, sprint: 1f));
            return Assert.Single(w.Shots);
        }
        var a = Run();
        var b = Run();
        Assert.Equal(a.Tick, b.Tick);
        Assert.Equal(a.Launch, b.Launch);
        Assert.Equal(a.Spin, b.Spin);
        Assert.Equal(a.Foot, b.Foot);
        // And the draws of a strike are the same four as before, whatever the foot: the next draw after two strikes agrees.
        var r1 = new SimRng(4);
        var r2 = new SimRng(4);
        ShotModel.Compute(Ctx(Shooter(), foot: Foot.Right), P, T, r1);
        ShotModel.Compute(Ctx(Shooter(facing: 2f), stick: new Vector2(0.3f, 1f), foot: Foot.Left, surface: FootSurface.Outside), P, T, r2);
        Assert.Equal(r1.NextFloat(), r2.NextFloat());
    }
}
