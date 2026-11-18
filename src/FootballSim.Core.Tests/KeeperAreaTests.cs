using System.Numerics;
using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>The penalty area: the query, the hands only inside it, and the keeper's feet (slide or poke) outside it.</summary>
public class KeeperAreaTests
{
    static readonly Tuning T = new();
    static readonly Pitch P = new(new PitchSpec(), T.Frame);
    static readonly float R = T.Ball.Radius;
    static float Dt => T.Dt;
    static float Line => P.PenaltyAreaLineX;

    static PlayerSheet Wall() => TestData.Player("the_wall");

    /// <summary>A keeper standing at <paramref name="feet"/> facing up the pitch, and a ball at rest at <paramref name="ball"/>.</summary>
    static (KeeperBrain Brain, PlayerState Keeper, Ball Ball) Rig(Vector3 feet, Vector3 ball, Pitch? pitch = null)
    {
        var b = new Ball(T.Ball) { Position = ball };
        var k = new PlayerState(1, 0, PlayerRole.Keeper, Wall()) { Position = feet, Facing = MathF.PI };
        return (new KeeperBrain(k, pitch ?? P, T, holdLine: false), k, b);
    }

    /// <summary>
    /// A 14 m deep area, inside the 15 m he rushes to (<c>rush_max_from_goal</c>): with the full 16.5 m area he never races for a
    /// ball inside it near its line in front of goal, so a smother there cannot start.
    /// </summary>
    static readonly Pitch Shallow = new(new PitchSpec { PenaltyAreaDepth = 14f }, T.Frame);

    static KeeperSituation Loose(float attackerNextTouch) => new()
    {
        AttackerNextTouch = attackerNextTouch, AttackerTouchTick = 7, StrikeIn = float.PositiveInfinity,
    };

    // ---- the area ----

    [Fact]
    public void ThePenaltyAreaIsSixteenAndAHalfMetresDeepAndTheBallIsInWhileAnyOfItIsOnTheLine()
    {
        Assert.Equal(16.5f, P.PenaltyAreaDepth);
        Assert.Equal(P.GoalLineX - 16.5f, Line);
        Assert.True(P.InPenaltyArea(new Vector3(Line + 0.01f, R, 0f)));
        Assert.False(P.InPenaltyArea(new Vector3(Line - 0.01f, R, 0f)));
        // A ball whose centre is just outside the line still has a part over it.
        Assert.True(P.InPenaltyArea(new Vector3(Line - R + 0.01f, R, 0f), R));
        Assert.False(P.InPenaltyArea(new Vector3(Line - R - 0.01f, R, 0f), R));
        // Height does not matter.
        Assert.True(P.InPenaltyArea(new Vector3(Line + 1f, 2f, 0f), R));
    }

    [Fact]
    public void TheAreaIsFortyMetresWideButNeverWiderThanTheArena()
    {
        Assert.Equal(20.16f, P.PenaltyAreaHalfWidth, 3);
        Assert.True(P.InPenaltyArea(new Vector3(P.GoalLineX - 3f, R, 20f)));
        Assert.False(P.InPenaltyArea(new Vector3(P.GoalLineX - 3f, R, 21f)));

        // The arena of the scenarios is 20 m wide: the area's sides are the side walls.
        var narrow = new Pitch(new PitchSpec { Length = 30f, Width = 20f, PenaltyAreaDepth = 12f }, T.Frame);
        Assert.Equal(narrow.HalfWidth, narrow.PenaltyAreaHalfWidth);
        Assert.Equal(18f, narrow.PenaltyAreaLineX);
        Assert.True(narrow.InPenaltyArea(new Vector3(19f, R, narrow.HalfWidth - 0.1f)));
    }

    [Fact]
    public void TheScenarioCarriesTheAreaAndTheLoaderClampsIt()
    {
        var sc = DataLoader.LoadScenario(Path.Combine(TestData.DataDir, "scenarios", "keeper_rush.json"));
        Assert.Equal(16.5f, sc.Value!.Pitch.PenaltyAreaDepth);
        Assert.Equal(40.32f, sc.Value.Pitch.PenaltyAreaWidth);
        var fixedUp = DataLoader.ParseScenario("""{ "pitch": { "length": 30, "penalty_area_depth": 50 } }""", "s.json");
        Assert.Equal(30f, fixedUp.Value!.Pitch.PenaltyAreaDepth);
        Assert.Single(fixedUp.Warnings);
    }

    // ---- hands only inside ----

    /// <summary>A keeper at <paramref name="feet"/> in a standing catch at a ball meeting his hands 0.4 m in front of him.</summary>
    static (KeeperBrain Brain, Ball Ball, Vector3 Contact) Catch(Vector3 feet)
    {
        var (brain, keeper, ball) = Rig(feet, new Vector3(feet.X - 3f, R, feet.Z));
        var contact = new Vector3(feet.X - 0.4f, keeper.Sheet.HeightM * T.Keeper.HomeHeightFraction, feet.Z + 0.1f);
        brain.BeginDiveForTest(contact, 0.5f);
        ball.Position = contact;
        ball.Velocity = new Vector3(3f, 0f, 0f);
        return (brain, ball, contact);
    }

    [Fact]
    public void ACatchIsRefusedForABallOutsideTheArea()
    {
        // Inside the area the same easy catch is held.
        var (inBrain, inBall, inAt) = Catch(new Vector3(Line + 1.5f, 0f, 0f));
        Assert.True(inBrain.Contact(inBall, inAt, new SimRng(1), out _));
        Assert.Equal(SaveKind.Catch, inBrain.Report.Save);

        // He stands inside the area, the ball meets his hands outside it: no touch, the ball carries on and his hands are withdrawn.
        var (brain, ball, at) = Catch(new Vector3(Line + 0.2f, 0f, 0f));
        Assert.False(P.InPenaltyArea(at, R));
        Assert.True(brain.RefuseHands(at, body: false));
        Assert.False(brain.Contact(ball, at, new SimRng(1), out _));
        Assert.Equal(new Vector3(3f, 0f, 0f), ball.Velocity);
        Assert.Equal(SaveKind.None, brain.Report.Save);
        Assert.Null(brain.HandsSphere);
        Assert.Null(brain.HandsCapsule);
    }

    [Fact]
    public void OutsideTheAreaHisBodyBlocksASlowBallInsteadOfHoldingIt()
    {
        var (inBrain, inBall, inAt) = Catch(new Vector3(Line + 1.5f, 0f, 0f));
        Assert.True(inBrain.Contact(inBall, inAt, new SimRng(1), out _, body: true));

        var (brain, ball, at) = Catch(new Vector3(Line + 0.2f, 0f, 0f));
        Assert.False(brain.RefuseHands(at, body: true));
        Assert.False(brain.Contact(ball, at, new SimRng(1), out _, body: true));
        Assert.Equal(SaveKind.Block, brain.Report.Save);
        Assert.True(ball.Velocity.X < 0f, "blocked back out");
    }

    [Fact]
    public void AWorldRoundNeverHasTheKeeperHoldTheBallOutsideHisArea()
    {
        var sc = DataLoader.LoadScenario(Path.Combine(TestData.DataDir, "scenarios", "keeper_rush.json")).Value!;
        var att = TestData.Player(sc.DefaultAttacker);
        var kee = TestData.Player(sc.DefaultKeeper);
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var w = new World(T, sc, att, kee, seed);
            var script = new ScriptedAttacker();
            w.Begin();
            var buf = new TickIntent[w.PlayerCount];
            for (int i = 0; i < 20 * T.TickRate && w.State is not (RoundState.Outcome or RoundState.Report); i++)
            {
                buf[0] = script.Next(w);
                w.Step(buf);
                foreach (var e in w.Events)
                    if (e.Kind == EventKind.Save && e.Held) Assert.True(w.Pitch.InPenaltyArea(e.Position, R), $"seed {seed}: held at {e.Position}");
            }
        }
    }

    // ---- outside the area: the feet ----

    [Fact]
    public void ForALooseBallOutsideTheAreaHeSlidesInsteadOfSmothering()
    {
        // The same race inside the area is a smother.
        var (inBrain, inKeeper, inBall) = Rig(new Vector3(Line + 4.2f, 0f, 0f), new Vector3(Line + 3f, R, 0f));
        inBrain.Update(Dt, inBall, new SimRng(1), Loose(3f));
        Assert.Equal(KeeperAction.Smother, inKeeper.KeeperAction);

        var (brain, keeper, ball) = Rig(new Vector3(Line + 1f, 0f, 0f), new Vector3(Line - 1f, R, 0f));
        brain.Update(Dt, ball, new SimRng(1), Loose(3f));
        Assert.Equal(KeeperAction.Slide, keeper.KeeperAction);
        Assert.True(brain.Report.Slid);
        Assert.False(brain.Report.Smothered);
    }

    [Fact]
    public void ABallOutsideTheAreaAtHisFeetIsPokedStanding()
    {
        var (brain, keeper, ball) = Rig(new Vector3(Line + 0.5f, 0f, 0f), new Vector3(Line - 0.3f, R, 0f));
        brain.Update(Dt, ball, new SimRng(1), Loose(3f));
        Assert.Equal(KeeperAction.Diving, keeper.KeeperAction);
        Assert.Equal(KeeperDive.Feet, keeper.KeeperDive);
        Assert.True(brain.Report.Slid);
        Assert.False(brain.Report.Smothered);
    }

    [Fact]
    public void ASlideThatReachesTheBallKnocksItAwayAndLeavesHimToGetUp()
    {
        var (brain, keeper, ball) = Rig(new Vector3(Line + 1f, 0f, 0f), new Vector3(Line - 1f, R, 0f));
        var rng = new SimRng(3);
        brain.Update(Dt, ball, rng, Loose(3f));
        Assert.Equal(KeeperAction.Slide, keeper.KeeperAction);
        bool hit = false, getUp = false;
        for (int i = 0; i < 3 * T.TickRate && !getUp; i++)
        {
            brain.Update(Dt, ball, rng, Loose(3f));
            var res = ball.Step(Dt, P, brain.HandsSphere, brain.BodyCapsule, brain.HandsCapsule);
            if (res.SphereHit && !brain.RefuseHands(res.SpherePoint, res.BodyHit))
            {
                Assert.False(brain.Contact(ball, res.SpherePoint, rng, out _, res.BodyHit));
                hit = true;
                Assert.Equal(SaveKind.Slide, brain.Report.Save);
                Assert.True(brain.Report.WonBall);
                // Knocked on along the slide (up the pitch, away from goal) at the knock speed.
                var h = new Vector2(ball.Velocity.X, ball.Velocity.Z);
                Assert.InRange(h.Length(), 0.95f * T.Keeper.SlideKnockSpeedMin, T.Keeper.SlideKnockSpeedMax);
                Assert.True(-h.X / h.Length() >= MathF.Cos((T.Keeper.SlideKnockAngleDeg + 1f) * MathF.PI / 180f));
            }
            Assert.NotEqual(KeeperAction.Holding, keeper.KeeperAction);
            getUp = keeper.KeeperAction == KeeperAction.GettingUp;
        }
        Assert.True(hit);
        Assert.True(getUp);
    }

    [Fact]
    public void AMissedSlideLeavesHimGroundedThenGettingUp()
    {
        var (brain, keeper, ball) = Rig(new Vector3(Line + 1f, 0f, 0f), new Vector3(Line - 1f, R, 0f));
        brain.Update(Dt, ball, new SimRng(1), Loose(3f));
        Assert.Equal(KeeperAction.Slide, keeper.KeeperAction);
        ball.Position = new Vector3(Line - 10f, R, 5f); // gone (the attacker took it away)
        var seen = new List<KeeperAction>();
        for (int i = 0; i < 2 * T.TickRate; i++)
        {
            brain.Update(Dt, ball, new SimRng(1), Loose(3f));
            if (seen.Count == 0 || seen[^1] != keeper.KeeperAction) seen.Add(keeper.KeeperAction);
        }
        Assert.Equal(new[] { KeeperAction.Slide, KeeperAction.Grounded, KeeperAction.GettingUp }, seen.Take(3));
        Assert.Equal(SaveKind.None, brain.Report.Save);
    }

    [Fact]
    public void ASmotherWhoseBallLeavesTheAreaTurnsIntoASlideOrIsAbandoned()
    {
        // The ball is inside when he goes down at it, then it is out before he has it: a slide at it, never a gather.
        float line = Shallow.PenaltyAreaLineX;
        var (brain, keeper, ball) = Rig(new Vector3(line + 1.2f, 0f, 0f), new Vector3(line + 0.3f, R, 0f), Shallow);
        brain.Update(Dt, ball, new SimRng(1), Loose(3f));
        Assert.Equal(KeeperAction.Smother, keeper.KeeperAction);
        ball.Position = new Vector3(line - 0.6f, R, 0f);
        brain.Update(Dt, ball, new SimRng(1), Loose(3f));
        Assert.Equal(KeeperAction.Slide, keeper.KeeperAction);

        // Out of a slide's reach by then: he abandons it and is left on the ground.
        var (brain2, keeper2, ball2) = Rig(new Vector3(line + 1.2f, 0f, 0f), new Vector3(line + 0.3f, R, 0f), Shallow);
        brain2.Update(Dt, ball2, new SimRng(1), Loose(3f));
        Assert.Equal(KeeperAction.Smother, keeper2.KeeperAction);
        ball2.Position = new Vector3(line - 5f, R, 0f);
        brain2.Update(Dt, ball2, new SimRng(1), Loose(3f));
        Assert.Equal(KeeperAction.Grounded, keeper2.KeeperAction);
        Assert.Null(brain2.HandsSphere);
    }

    [Fact]
    public void AStruckBallOutsideTheAreaGetsAFootOrBodyBlockNeverAHandSave()
    {
        // He is at the edge of his area; a ball struck across him passes just outside it.
        var (brain, keeper, ball) = Rig(new Vector3(Line + 0.3f, 0f, 0f), new Vector3(Line - 0.8f, 0.3f, -4f));
        ball.Velocity = new Vector3(0f, 0f, 12f);
        var rng = new SimRng(5);
        brain.OnStrike(rng, ball);
        bool dived = false;
        for (int i = 0; i < T.TickRate && !dived; i++)
        {
            brain.Update(Dt, ball, rng, KeeperSituation.None);
            if (keeper.KeeperAction == KeeperAction.Diving)
            {
                dived = true;
                Assert.Contains(keeper.KeeperDive, new[] { KeeperDive.Feet, KeeperDive.ReflexFoot, KeeperDive.BodyBlock });
                Assert.NotEqual(SaveType.Catch, brain.Report.SaveType);
            }
            ball.Step(Dt, null, null);
        }
        Assert.True(dived);
    }
}
