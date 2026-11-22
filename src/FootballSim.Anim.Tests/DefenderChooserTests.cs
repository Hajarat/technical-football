using System.Numerics;
using FootballSim.Anim;
using FootballSim.Core;

namespace FootballSim.Anim.Tests;

/// <summary>
/// The defender's chooser rows, and the committed defender clips played by the keeper's engine as the game's animator asks: every
/// set has a kept clip, a lunge's contact lands on the core's tick with the foot of its side leading, the slide leads with the
/// leg of its side, and a half-second get-up is entered where its "up" can still land on time.
/// </summary>
public class DefenderChooserTests
{
    const float Dt = 1f / 120f;
    static readonly PoseDatabase Db = new(TestKit.Defender, DefenderChooser.Sets, DefenderChooser.WeightsFor, DefenderChooser.OptionsFor);

    [Theory]
    [InlineData(DefenderAction.Jockey, true, true, DefenderChooser.Jockey)]
    [InlineData(DefenderAction.Jockey, true, false, DefenderChooser.SetIdle)]
    [InlineData(DefenderAction.Hold, true, false, DefenderChooser.SetIdle)]
    [InlineData(DefenderAction.Hold, false, false, DefenderChooser.SetIdle)]
    [InlineData(DefenderAction.Hold, true, true, DefenderChooser.Jockey)]
    [InlineData(DefenderAction.Press, true, true, DefenderChooser.Jockey)]
    [InlineData(DefenderAction.Block, true, false, DefenderChooser.Block)]
    [InlineData(DefenderAction.Lunge, false, false, DefenderChooser.Lunge)]
    [InlineData(DefenderAction.Barge, false, true, DefenderChooser.Barge)]
    [InlineData(DefenderAction.Slide, false, true, DefenderChooser.Slide)]
    [InlineData(DefenderAction.Grounded, false, false, DefenderChooser.Slide)]
    [InlineData(DefenderAction.GettingUp, false, false, DefenderChooser.GetUp)]
    [InlineData(DefenderAction.Stumble, false, true, DefenderChooser.Stumble)]
    public void AnActionWithClipsHasItsSet(DefenderAction action, bool jockeying, bool moving, string set) =>
        Assert.Equal(set, DefenderChooser.SetFor(action, jockeying, moving));

    [Theory]
    [InlineData(DefenderAction.Hold)]
    [InlineData(DefenderAction.Press)]
    [InlineData(DefenderAction.Recover)]
    [InlineData(DefenderAction.Collect)]
    public void RunningIsLeftToTheFigure(DefenderAction action) => Assert.Null(DefenderChooser.SetFor(action, jockeying: false, moving: true));

    [Fact]
    public void TheAttackersReactionsHaveTheirSets()
    {
        Assert.Null(DefenderChooser.SetFor(Reaction.None));
        Assert.Equal(DefenderChooser.Stumble, DefenderChooser.SetFor(Reaction.Stumble));
        Assert.Equal(DefenderChooser.Barged, DefenderChooser.SetFor(Reaction.Barged));
        Assert.Equal(DefenderChooser.Fall, DefenderChooser.SetFor(Reaction.Fall));
    }

    [Fact]
    public void StandingAndSteppingDoNotFlipAtOneSpeed()
    {
        Assert.False(DefenderChooser.Moving(0.5f, was: false));   // not yet stepping
        Assert.True(DefenderChooser.Moving(0.5f, was: true));     // still stepping
        Assert.True(DefenderChooser.Moving(DefenderChooser.MoveSpeed, was: false));
        Assert.False(DefenderChooser.Moving(DefenderChooser.StandSpeed, was: true));
    }

    [Fact]
    public void EverySetButTheBargeHasAKeptClip()
    {
        // (the barge keeps the figure's lean when its clip is rejected: the chooser's set may be empty)
        foreach (var set in DefenderChooser.Sets)
            if (set != DefenderChooser.Barge) Assert.True(Db.Set(set) is { FrameCount: > 0 }, set);
    }

    sealed class Rig
    {
        public readonly KeeperMotion Motion = new(Db);
        readonly Quaternion[] _procR, _outR;
        readonly Vector3[] _procP, _outP;
        public Rig()
        {
            var sk = Db.Skeleton;
            _procR = sk.RestLocal.ToArray(); _procP = sk.Offsets.ToArray(); _outR = new Quaternion[sk.Count]; _outP = new Vector3[sk.Count];
            _procP[0] = new Vector3(0, 0.95f, 0);
        }
        public void Step(MotionInput input)
        {
            input.Scale = 1f; input.Dt = Dt;
            Motion.Step(input, _procR, _procP, _outR, _outP);
        }
        public void Stand(int ticks) { for (int i = 0; i < ticks; i++) Step(new MotionInput { Set = DefenderChooser.SetIdle, SquareChest = true }); }
        public PoseClip Playing(string set) { var ps = Db.Set(set)!; return ps.Clips[ps.IndexOf(Motion.ClipName!)]; }
    }

    [Theory]
    [InlineData(1, 0.30f)]
    [InlineData(-1, 0.30f)]
    [InlineData(1, 0.18f)]
    [InlineData(-1, 0.45f)]
    public void ALungesContactLandsOnTheCoresTickWithTheFootOfItsSideLeading(int side, float contact)
    {
        var rig = new Rig();
        rig.Stand(60);
        int contactTick = (int)MathF.Round(contact / Dt), landed = -1;
        for (int i = 0; i <= contactTick + 10; i++)
        {
            rig.Step(new MotionInput { Set = DefenderChooser.Lunge, HasTarget = true, TargetTime = contact - i * Dt, Side = side, WarpMin = 0.6f, WarpMax = 2.2f, EntryRate = 1f, Root = RootMode.Free });
            var pc = rig.Playing(DefenderChooser.Lunge);
            Assert.True(pc.SideSign * side >= 0, $"{pc.Clip.Name} leads with the other foot");
            if (landed < 0 && rig.Motion.ClipFrame >= pc.TargetFrame) landed = i;
        }
        Assert.InRange(landed, contactTick - 4, contactTick + 4);
    }

    [Theory]
    [InlineData(1, "slide_baked")]
    [InlineData(-1, "slide_baked_mirror")]
    public void TheSlideLeadsWithTheLegOfItsSide(int side, string clip)
    {
        var rig = new Rig();
        rig.Stand(30);
        for (int i = 0; i < 20; i++)
            rig.Step(new MotionInput { Set = DefenderChooser.Slide, HasTarget = true, TargetTime = 0.28f - i * Dt, Side = side, EntryRate = 1f, Velocity = new Vector2(0, 5f) });
        Assert.Equal(clip, rig.Motion.ClipName);
    }

    [Fact]
    public void AHalfSecondGetUpIsUpOnTime()
    {
        var rig = new Rig();
        rig.Stand(30);
        for (int i = 0; i < 90; i++) rig.Step(new MotionInput { Set = DefenderChooser.Slide, HasTarget = true, TargetTime = 0.28f - i * Dt, Side = 1, Ground = i > 40 });
        const float up = 0.4f;   // the game's share of the core's 0.5 s get-up
        int upTick = (int)MathF.Round(up / Dt), landed = -1, entered = -1;
        for (int i = 0; i <= upTick + 20; i++)
        {
            rig.Step(new MotionInput { Set = DefenderChooser.GetUp, HasTarget = true, TargetTime = up - i * Dt, WarpMax = 2.6f, EntryRate = 2.6f, Ground = true, Root = RootMode.Return });
            var pc = rig.Playing(DefenderChooser.GetUp);
            if (i == 0) entered = pc.TargetFrame - rig.Motion.ClipFrame;
            if (landed < 0 && rig.Motion.ClipFrame >= pc.TargetFrame) landed = i;
        }
        // entered well before "up" (more of the rise than the 24 frames its own speed would show), and up within a few ticks of the target
        Assert.InRange(entered, 30, 70);
        Assert.InRange(landed, upTick - 6, upTick + 6);
    }
}
