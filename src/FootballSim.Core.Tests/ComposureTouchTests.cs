using FootballSim.Core;

namespace FootballSim.Core.Tests;

/// <summary>Composure in touches: a light extra on the sprint cadence and on the early touch after a turn.</summary>
public class ComposureTouchTests
{
    static readonly Tuning T = TestData.Tuning();

    static PlayerSheet Sheet(int dribbling, int composure)
    {
        var s = TestData.Sheet(60);
        s.CloseDribbling = s.SprintDribbling = dribbling;
        s.Composure = composure;
        return s;
    }

    static int Whole(float steps) => Math.Max(1, (int)MathF.Round(steps, MidpointRounding.AwayFromZero));

    static PlayerState Player(PlayerSheet s) => new World(T, TestData.Scenario("penalty"), s, TestData.Player("the_wall"), 1).Attacker;

    [Fact]
    public void TheCurvesRunFromTheirValueAtOneToNoEffectAtNinetyNine()
    {
        Assert.Equal(1.1f, T.Touch.ComposureSprintSteps.ForRating(1), 5);
        Assert.Equal(1f, T.Touch.ComposureSprintSteps.ForRating(99), 5);
        Assert.Equal(1.1f, T.Touch.ComposureEarly.ForRating(1), 5);
        Assert.Equal(1f, T.Touch.ComposureEarly.ForRating(99), 5);
    }

    [Fact]
    public void ComposureNinetyNineGivesTheNumbersWithoutIt()
    {
        var k = T.Touch;
        for (int r = 1; r <= 99; r++)
        {
            var s = Sheet(r, 99);
            Assert.Equal(Whole(k.StepsPerTouch.Sprint.ForRating(r)), TouchModel.StepsPerTouch(s, TouchMode.Sprint, T));
            var p = Player(s);
            foreach (float deg in new[] { 10f, 20f, 30f, 45f, 60f, 90f, 180f })
                Assert.Equal(MathF.Min(1f, k.EarlyExecution.Evaluate(deg) * k.EarlyExecutionDribbling.ForRating(r)), TouchPlanner.EarlyFraction(p, deg, T), 6);
        }
    }

    [Fact]
    public void LowComposureStretchesTheSprintCadenceBeforeTheRounding()
    {
        var k = T.Touch;
        int moved = 0;
        for (int r = 1; r <= 99; r++)
            foreach (int composure in new[] { 1, 30, 50, 72, 99 })
            {
                var s = Sheet(r, composure);
                int steps = TouchModel.StepsPerTouch(s, TouchMode.Sprint, T);
                Assert.Equal(Whole(k.StepsPerTouch.Sprint.ForRating(r) * k.ComposureSprintSteps.ForRating(composure)), steps);
                int composed = TouchModel.StepsPerTouch(Sheet(r, 99), TouchMode.Sprint, T);
                Assert.InRange(steps, composed, composed + 1);
                if (steps > composed) moved++;
                // Only the sprint cadence: a jog's and the close style's are the same at any composure.
                Assert.Equal(TouchModel.StepsPerTouch(Sheet(r, 99), TouchMode.Jog, T), TouchModel.StepsPerTouch(s, TouchMode.Jog, T));
                Assert.Equal(TouchModel.StepsPerTouch(Sheet(r, 99), TouchMode.CloseControl, T), TouchModel.StepsPerTouch(s, TouchMode.CloseControl, T));
            }
        Assert.True(moved > 0, "composure never moved a sprint cadence");
        // The shipped striker (sprint dribbling and composure 72) keeps his two-step sprint; without composure he takes three.
        Assert.Equal(2, TouchModel.StepsPerTouch(TestData.Player("striker_a"), TouchMode.Sprint, T));
        Assert.Equal(3, TouchModel.StepsPerTouch(Sheet(72, 1), TouchMode.Sprint, T));
    }

    [Fact]
    public void LowComposureTakesSomeOfTheEarlyTouchAndNeverPastTheNormalCadence()
    {
        var k = T.Touch;
        foreach (int r in new[] { 1, 40, 72, 99 })
            foreach (float deg in new[] { 10f, 20f, 30f, 45f, 60f, 90f, 180f })
            {
                float rated = k.EarlyExecution.Evaluate(deg) * k.EarlyExecutionDribbling.ForRating(r);
                Assert.Equal(MathF.Min(1f, rated * 1.1f), TouchPlanner.EarlyFraction(Player(Sheet(r, 1)), deg, T), 5);
                float prev = 0f;
                foreach (int composure in new[] { 99, 72, 50, 1 })
                {
                    float f = TouchPlanner.EarlyFraction(Player(Sheet(r, composure)), deg, T);
                    Assert.Equal(MathF.Min(1f, rated * k.ComposureEarly.ForRating(composure)), f, 5);
                    Assert.InRange(f, prev, 1f);
                    prev = f;
                }
            }
    }
}
