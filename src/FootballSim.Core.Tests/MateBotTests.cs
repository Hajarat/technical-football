using System.Numerics;
using FootballSim.Core;
using Xunit;
using static FootballSim.Core.Tests.TestData;

namespace FootballSim.Core.Tests;

/// <summary>The team-mate off the ball: each field, the slope he follows and each edge of the run cycle (see MateBot).</summary>
public class MateBotTests
{
    static readonly Tuning T = new();
    static readonly Pitch Arena = new(Scenario("two_v_one").Pitch, T.Frame, T.Arena);

    static PlayerState Man(int team, int index, float x, float z, PlayerRole role = PlayerRole.Outfield)
        => new(team, index, role, Sheet(70)) { Position = new Vector3(x, 0f, z) };

    /// <summary>The carrier with the ball at (8, 0), the defender at (16, 0) (the line), the keeper on his line.</summary>
    static MateBot.Scene Scene(MateRun run = MateRun.Support, float defenderZ = 0f, float line = 16f) =>
        new(new Vector2(8.5f, 0f), new Vector2(8f, 0f), new Vector2(8.6f, 0f), line,
            new List<PlayerState> { Man(1, 0, 29.5f, 0f, PlayerRole.Keeper), Man(1, 1, 16f, defenderZ) }, Arena, run, 20f);

    [Fact]
    public void SpreadIsHisDistanceOutOfTheCarriersCapsuleClampedAtThreeMetres()
    {
        var s = Scene();
        float r = T.Mate.SpreadRadiusM;
        Assert.Equal(-3f, MateBot.Spread(new Vector2(8.3f, 0f), s, T), 3);          // on the capsule's axis: clamped
        Assert.Equal(0f, MateBot.Spread(new Vector2(8.3f, r), s, T), 3);            // on its edge
        Assert.Equal(1f, MateBot.Spread(new Vector2(8.3f, r + 1f), s, T), 3);
        Assert.Equal(3f, MateBot.Spread(new Vector2(8.3f, r + 9f), s, T), 3);       // clamped
        // Ahead of the carrier the capsule reaches to where he will be.
        Assert.Equal(MateBot.Spread(new Vector2(8f - 2f, 0f), s, T), MateBot.Spread(new Vector2(8.6f + 2f, 0f), s, T), 3);
    }

    [Fact]
    public void OpponentsCountsOnlyInsideTheCircleRoundAnOutfieldOpponent()
    {
        var s = Scene();
        Assert.Equal(0f, MateBot.Opponents(new Vector2(10f, 5f), s, T), 3);
        Assert.Equal(-(T.Mate.OpponentRadiusM - 1f), MateBot.Opponents(new Vector2(15f, 0f), s, T), 3);
        // The keeper has no circle.
        Assert.Equal(0f, MateBot.Opponents(new Vector2(28.5f, 0.2f), s, T), 3);
    }

    [Fact]
    public void FreeSpaceIsTheDistanceToTheNearestOpponentOrBoardAndNothingBeyondTheLine()
    {
        var s = Scene();
        Assert.Equal(1f, MateBot.Space(new Vector2(10f, Arena.HalfWidth - 1f), s, T), 3);    // a board
        Assert.Equal(2f, MateBot.Space(new Vector2(14f, 0f), s, T), 3);                      // the defender
        Assert.Equal(T.Mate.SpaceCapM, MateBot.Space(new Vector2(8f, 0f), s, T), 3);         // free enough
        Assert.Equal(0f, MateBot.Space(new Vector2(18f, 5f), s, T), 3);                      // beyond the line
        Assert.True(MateBot.Space(new Vector2(15.5f, 5f), s, T) > 0f);
        Assert.True(MateBot.BoardDistance(new Vector2(-1f, 0f), Arena) < 0f);
    }

    [Fact]
    public void ThePassLaneIsOpenWhereTheDefenderCannotGetToItFirst()
    {
        var s = Scene();
        Assert.Equal(1f, MateBot.Lane(new Vector2(9f, 7f), s, T));
        // Straight through the defender: closed.
        Assert.Equal(0f, MateBot.Lane(new Vector2(19f, 0f), s, T));
        // Nearer than the shortest pass: not a lane.
        Assert.Equal(0f, MateBot.Lane(new Vector2(8.9f, 0.3f), s, T));
    }

    [Fact]
    public void TheGapIsTheWiderSideOfTheDefenderBeyondTheLine()
    {
        var left = MateBot.GapPoint(Scene(defenderZ: 3f));
        Assert.Equal(16f + 2f, left.X, 3);
        Assert.Equal((3f - Arena.HalfWidth) / 2f, left.Y, 3);           // the gap on the -Z side is wider
        var right = MateBot.GapPoint(Scene(defenderZ: -3f));
        Assert.Equal((Arena.HalfWidth - 3f) / 2f, right.Y, 3);
        var s = Scene(defenderZ: 3f);
        Assert.True(MateBot.Gap(left, s) > MateBot.Gap(left + new Vector2(0f, 3f), s));
    }

    [Fact]
    public void TheLinePullsHimOnWhileArmedAndPushesHimOnOnGo()
    {
        var s = Scene(MateRun.Armed);
        float spot = 16f - T.Mate.ArmedOnsideM;
        Assert.Equal(0f, MateBot.LinePull(new Vector2(spot, 5f), s, T), 3);
        Assert.Equal(-2f, MateBot.LinePull(new Vector2(spot - 2f, 5f), s, T), 3);
        Assert.Equal(-2f, MateBot.LinePull(new Vector2(spot + 2f, 5f), s, T), 3);
        Assert.Equal(3f, MateBot.LinePush(new Vector2(19f, 5f), s), 3);
        Assert.Equal(-1f, MateBot.LinePush(new Vector2(15f, 5f), s), 3);
        Assert.True(MateBot.Goal(new Vector2(25f, 0f), s) > MateBot.Goal(new Vector2(15f, 0f), s));
    }

    [Fact]
    public void TheSlopeHoldsHimOnsideInSupportTakesHimToTheLineArmedAndPastItOnGo()
    {
        // Two metres short of the line, wide of the defender: in support the drop in free space beyond it holds him.
        var at = new Vector2(15f, 6f);
        Assert.True(MateBot.Gradient(at, Scene(MateRun.Support), T).X < 0f);
        // Armed, well short of the line, he is pulled on to it.
        Assert.True(MateBot.Gradient(new Vector2(11f, 6f), Scene(MateRun.Armed), T).X > 1f);
        // On go the line pushes him past it.
        Assert.True(MateBot.Gradient(at, Scene(MateRun.Go), T).X > 1f);
        Assert.True(MateBot.Gradient(new Vector2(17f, 6f), Scene(MateRun.Go), T).X > 1f);
        // On top of the carrier, the spread moves him off.
        var g = MateBot.Gradient(new Vector2(8.3f, 1f), Scene(), T);
        Assert.True(g.Y > 0f);
    }

    [Fact]
    public void AttackProgressRunsFromTheBackWallToEightMetresFromTheGoal()
    {
        Assert.Equal(0f, MateBot.AttackProgress(0f, Arena, T), 3);
        Assert.Equal(1f, MateBot.AttackProgress(Arena.GoalLineX - T.Mate.AttackFullFromGoalM, Arena, T), 3);
        Assert.Equal(1f, MateBot.AttackProgress(Arena.GoalLineX, Arena, T), 3);
        Assert.Equal(0.5f, MateBot.AttackProgress((Arena.GoalLineX - T.Mate.AttackFullFromGoalM) / 2f, Arena, T), 3);
    }

    static MateRun Next(MateRun run, float onside, bool pass = false, bool receiver = false, bool nearGoal = false, bool canArm = true,
        float beyondFor = -1f, float sinceBack = 100f, float lineVel = 0f) =>
        MateBot.NextRun(run, onside, pass, receiver, nearGoal, canArm, beyondFor, sinceBack, lineVel, T);

    [Fact]
    public void RunCycleArm()
    {
        Assert.Equal(MateRun.Armed, Next(MateRun.Support, 6f));
        Assert.Equal(MateRun.Support, Next(MateRun.Support, 6f, canArm: false));
        Assert.Equal(MateRun.Support, Next(MateRun.Support, 6f, sinceBack: T.Mate.RearmSeconds - 0.1f));
        Assert.Equal(MateRun.Support, Next(MateRun.Support, 6f, pass: true));
        Assert.Equal(MateRun.Support, Next(MateRun.Armed, 6f, canArm: false));
    }

    [Fact]
    public void RunCycleGo()
    {
        float within = T.Mate.ArmedOnsideM + T.Mate.GoWithinM;
        Assert.Equal(MateRun.Armed, Next(MateRun.Armed, within + 0.2f));
        Assert.Equal(MateRun.Go, Next(MateRun.Armed, within - 0.1f));
        // Not while the line comes back up the pitch at him.
        Assert.Equal(MateRun.Armed, Next(MateRun.Armed, within - 0.1f, lineVel: -(T.Mate.GoMaxLineSpeed + 0.5f)));
        // On go he keeps going until he has been past the line long enough.
        Assert.Equal(MateRun.Go, Next(MateRun.Go, 0.2f));
        Assert.Equal(MateRun.Back, Next(MateRun.Go, -1f, beyondFor: T.Mate.GoBeyondSeconds + 0.01f));
    }

    [Fact]
    public void RunCycleBackAndRearm()
    {
        // Beyond the line and not on a run: back.
        Assert.Equal(MateRun.Back, Next(MateRun.Support, -0.5f));
        Assert.Equal(MateRun.Back, Next(MateRun.Armed, -0.5f));
        Assert.Equal(MateRun.Back, Next(MateRun.Back, T.Mate.RearmOnsideM - 0.1f));
        Assert.Equal(MateRun.Support, Next(MateRun.Back, T.Mate.RearmOnsideM + 0.1f));
    }

    [Fact]
    public void RunCycleCancel()
    {
        // A pass under way while he is less than 3.5 m onside; the ball near the goal; his being the receiver.
        Assert.Equal(MateRun.Support, Next(MateRun.Armed, T.Mate.PassCancelOnsideM - 0.5f, pass: true));
        Assert.Equal(MateRun.Support, Next(MateRun.Go, 0.2f, pass: true));
        Assert.Equal(MateRun.Armed, Next(MateRun.Armed, T.Mate.PassCancelOnsideM + 1f, pass: true));
        Assert.Equal(MateRun.Support, Next(MateRun.Armed, 5f, nearGoal: true));
        Assert.Equal(MateRun.Support, Next(MateRun.Go, 0.2f, receiver: true));
        // Cancelled while beyond the line, he comes back.
        Assert.Equal(MateRun.Back, Next(MateRun.Go, -0.4f, pass: true));
    }

    [Fact]
    public void InARoundHeStaysOnsideMovesOffTheCarrierAndNeverTouchesTheBall()
    {
        var sc = Scenario("two_v_one");
        var w = new World(T, sc, Player(sc.DefaultAttacker), Player(sc.DefaultKeeper), 3, Player(sc.DefaultDefender), Player(sc.DefaultMate));
        w.Begin();
        var buf = new TickIntent[w.PlayerCount];
        var mate = w.Teams[0][1];
        int offside = 0, ticks = 0;
        for (int i = 0; i < 240 && w.State == RoundState.Live && w.ControlledIndex == 0; i++)
        {
            buf[0] = new TickIntent { MoveX = 1f };
            w.Step(buf);
            ticks++;
            if (Offside.Beyond(mate, w.OffsideLine)) offside++;
            Assert.False(w.Events.Any(e => e.Kind == EventKind.Touch && e.Team == 0 && e.PlayerIndex == 1));
        }
        Assert.True(ticks > 100);
        Assert.True(offside < ticks / 5, $"offside in {offside} of {ticks} ticks");
        Assert.True(Vector3.Distance(mate.Position, new Vector3(sc.Mate!.X, 0f, sc.Mate.Z)) > 1f, "he never moved");
        // With the bot off he stands.
        var still = new World(T, sc, Player(sc.DefaultAttacker), Player(sc.DefaultKeeper), 3, Player(sc.DefaultDefender), Player(sc.DefaultMate)) { MateBotEnabled = false };
        still.Begin();
        for (int i = 0; i < 120; i++) still.Step(buf);
        Assert.Equal(sc.Mate.X, still.Teams[0][1].Position.X, 2);
    }

    [Fact]
    public void HeDoesNotMoveTowardABoardHeIsNear()
    {
        // Put right by the side board with the carrier inside him: whatever the slope says, his intent has no push at the board.
        var w = ControlTests.WithBall(10f, 8.2f, 10.5f, 9.3f);
        w.MateBotEnabled = true;
        var it = MateBot.Next(w, w.Teams[0][1]);
        Assert.True(it.MoveZ <= 1e-4f, $"MoveZ {it.MoveZ}");
    }
}
