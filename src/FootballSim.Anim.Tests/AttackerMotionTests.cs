using System.Numerics;
using FootballSim.Anim;
using Xunit.Abstractions;

namespace FootballSim.Anim.Tests;

/// <summary>The attacker's chooser, the stride phase read from a clip's contacts, and his locomotion by motion matching, on the committed recordings.</summary>
public class AttackerMotionTests
{
    readonly ITestOutputHelper _out;
    public AttackerMotionTests(ITestOutputHelper output) { _out = output; }

    static string Dir(string sub)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var d = Path.Combine(dir.FullName, "game", "assets", "anim", "attacker", sub);
            if (File.Exists(Path.Combine(d, "clips.json"))) return d;
        }
        throw new DirectoryNotFoundException("game/assets/anim/attacker/" + sub);
    }

    static readonly Lazy<ClipLibrary> Library = new(() => ClipLibrary.Load(Dir("")));
    static readonly Lazy<PoseSet> LocoSet = new(() => AttackerChooser.BuildLoco(Library.Value)!);
    static readonly Lazy<SkillClipLibrary> SkillLibrary = new(() =>
        SkillClipLibrary.Parse(File.ReadAllText(Path.Combine(Dir("skills"), "clips.json")), f => File.ReadAllBytes(Path.Combine(Dir("skills"), f))));

    static LocoInput Input(int tick, float speed, float stepsPerSecond, float turn = 0f, float yawRate = 0f) => new()
    {
        Dt = 1f / 120f, Phase = (0.3f + tick * stepsPerSecond / 120f) % 2f, StepFrequency = stepsPerSecond, Speed = speed, TurnDemand = turn, YawRate = yawRate,
    };

    [Fact]
    public void TheStridePhaseIsReadFromTheContacts()
    {
        // A synthetic walk at 1 Hz: the left foot down on the first half of each second, the right on the second. One step is 1, the
        // left foot's mid-stance is at 0.5 and the right's at 1.5 (mod 2), and the phase runs on evenly at two steps a second.
        var sk = TestKit.Legs();
        var clip = TestKit.Walk(sk, "walk", 240, 1.2f, 1f);
        var phase = GaitPhases.Of(clip)!;
        Assert.NotNull(phase);
        for (int f = 1; f < phase.Length; f++) Assert.InRange(phase[f] - phase[f - 1], 2f / 60f - 1e-3f, 2f / 60f + 1e-3f);
        // whole left spans: frames 60..90 (mid 75), right 90..119 (mid 104.5) and so on
        float Mod2(float x) { x %= 2f; return x < 0 ? x + 2f : x; }
        for (int f = 0; f < phase.Length; f++)
        {
            bool left = (clip.Contacts(f) & Clip.LeftFoot) != 0;
            float fromMid = MathF.Abs(Mod2(phase[f] - (left ? 0.5f : 1.5f) + 1f) - 1f);
            Assert.True(fromMid <= 0.55f, $"frame {f}: phase {phase[f]:F2} with the {(left ? "left" : "right")} foot down");
        }
        // a standing clip has no stride
        Assert.Null(GaitPhases.Of(TestKit.Stand(sk, "stand", 120)));
    }

    [Fact]
    public void TheKeepersCostsDoNotSeeTheNewFeatures()
    {
        // At weight 0 (every set but the attacker's) the gait phase, the turn demand and the ball change no cost.
        var sk = TestKit.Legs();
        var set = new PoseSet("walk", new[] { (TestKit.Walk(sk, "a", 180, 1.2f, 1f), "none"), (TestKit.Walk(sk, "b", 180, 2f, 1.5f, 1f), "none") }, sk);
        var q = new float[Features.Count];
        set.Raw(40).CopyTo(q);
        var n0 = new float[Features.Count]; var n1 = new float[Features.Count];
        set.Normalize(q, n0);
        Features.WriteGait(q, 1.3f); Features.WriteTurn(q, 0.9f); Features.WriteBall(q, new Vector3(1, 2, 3), new Vector3(4, 5, 6));
        set.Normalize(q, n1);
        for (int f = 0; f < set.FrameCount; f += 7) Assert.Equal(Matcher.Cost(set, f, n0), Matcher.Cost(set, f, n1));
    }

    [Fact]
    public void TheLocomotionSetHoldsTheStridingRecordingsAndTheirMirrors()
    {
        var set = LocoSet.Value;
        var names = set.Clips.Select(c => c.Clip.Name).ToList();
        _out.WriteLine(string.Join(", ", names));
        foreach (string prefix in new[] { "walk", "jog", "run", "veer", "turn90" }) Assert.Contains(names, n => n.StartsWith(prefix));
        Assert.DoesNotContain(names, n => n.StartsWith("stop") || n.StartsWith("start") || n.StartsWith("kick"));
        Assert.Equal(names.Count(n => !n.EndsWith("_mirror")), names.Count(n => n.EndsWith("_mirror")));
        Assert.Contains(set.Clips, c => c.SideSign < 0);
        Assert.Contains(set.Clips, c => c.SideSign > 0);
        // every frame has a stride phase, rising along its recording
        foreach (var pc in set.Clips)
            for (int f = pc.Start + 1; f < pc.End; f++)
                Assert.True(set.GaitPhase(f) > set.GaitPhase(f - 1), $"{pc.Clip.Name} frame {f - pc.Start}");
        Assert.Equal(AttackerChooser.Loco, AttackerChooser.SetFor(AttackerState.Loco));
        Assert.Equal("skill:ball_roll", AttackerChooser.SetFor(AttackerState.Skill, "ball_roll"));
        Assert.Equal(AttackerChooser.Shot, AttackerChooser.SetFor(AttackerState.Shot));
        Assert.True(AttackerChooser.WeightsFor(AttackerChooser.Loco).GaitPhase > 0f);
        Assert.True(AttackerChooser.WeightsFor("skill:spin").BallPosition > 0f);
    }

    [Theory]
    [InlineData(1.6f, 1.9f, "walk")]
    [InlineData(4.4f, 2.9f, "run")]
    public void AStraightRunPlaysARecordingOfItsPaceInStepWithTheStrideClock(float speed, float steps, string expected)
    {
        var m = new AttackerMotion(Library.Value.Skeleton, LocoSet.Value);
        var local = new Quaternion[Library.Value.Skeleton.Count];
        int inStep = 0, counted = 0, rightClip = 0, stanceAgrees = 0, stanceCounted = 0;
        for (int tick = 0; tick < 600; tick++)
        {
            var input = Input(tick, speed, steps);
            m.Step(input, local);
            if (tick < 60) continue;
            counted++;
            if (MathF.Abs(m.PhaseError) < 0.15f) inStep++;
            if (m.ClipName!.StartsWith(expected)) rightClip++;
            // around a foot's mid-stance on the simulation's clock, that foot is down in the recording shown
            float p = (input.Phase + steps / 120f) % 2f;
            foreach (int foot in new[] { 0, 1 })
                if (MathF.Abs(p - (foot == 0 ? 0.5f : 1.5f)) < 0.1f) { stanceCounted++; if (m.InStance(foot)) stanceAgrees++; }
        }
        _out.WriteLine($"{speed} m/s: {m.ClipName}, in step {100f * inStep / counted:F0}%, {expected} clip {100f * rightClip / counted:F0}%, stance {stanceAgrees}/{stanceCounted}, {m.SwitchCount} switches in {m.SearchCount} searches, native step {m.NativeStep:F2} m");
        Assert.True(inStep >= 0.9f * counted, $"in step {inStep}/{counted}");
        Assert.True(rightClip >= 0.8f * counted, $"a {expected} recording {rightClip}/{counted}");
        Assert.True(stanceAgrees >= 0.9f * stanceCounted, $"stance foot {stanceAgrees}/{stanceCounted}");
        Assert.InRange(m.NativeStep, 0.4f, 2.2f);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void TheStickTurnedBringsInARecordingTurningThatWay(int side)
    {
        // A jog straight on, then the stick turned 70 degrees to one side (the model's angles: positive toward his left) and the
        // facing following: within the next 0.6 s a recording moving to that side is played.
        var m = new AttackerMotion(Library.Value.Skeleton, LocoSet.Value);
        var local = new Quaternion[Library.Value.Skeleton.Count];
        for (int tick = 0; tick < 240; tick++) m.Step(Input(tick, 2.9f, 2.55f), local);
        int straight = m.ClipSide;
        // (his left is the model's positive turn; a clip moving to his left has side -1)
        float turn = -side * 70f * MathF.PI / 180f;
        int turned = 0;
        for (int tick = 240; tick < 312; tick++)
        {
            m.Step(Input(tick, 2.9f, 2.55f, turn, turn / 0.4f), local);
            if (m.ClipSide == side) turned++;
        }
        _out.WriteLine($"side {side}: straight clip side {straight}, {turned} of 72 ticks on a recording turning that way ({m.ClipName})");
        Assert.True(turned >= 24, $"{turned} ticks on a recording moving to side {side}");
    }

    [Fact]
    public void AChangeOfRecordingNeverPopsAndTheRunIsDeterministic()
    {
        // A run through changes of pace and turns: no bone turns more than 12 degrees in a tick (1440 deg/s) and the hips never
        // jump more than 3 cm, whatever the matcher changes to; and the same inputs give the same poses.
        Quaternion[][] Run(out int switches, out float worst, out float worstHips)
        {
            var sk = Library.Value.Skeleton;
            var m = new AttackerMotion(sk, LocoSet.Value);
            var local = new Quaternion[sk.Count];
            var prev = new Quaternion[sk.Count];
            var poses = new List<Quaternion[]>();
            Vector3 prevHips = default;
            worst = 0f; worstHips = 0f;
            for (int tick = 0; tick < 1200; tick++)
            {
                float speed = tick < 300 ? 1.7f : tick < 600 ? 3f : 4.6f, steps = tick < 300 ? 1.9f : tick < 600 ? 2.6f : 3.1f;
                float turn = tick is >= 400 and < 470 ? 1f : tick is >= 800 and < 860 ? -0.9f : 0f;
                var hips = m.Step(Input(tick, speed, steps, turn, turn * 2f), local);
                if (tick > 0)
                {
                    for (int k = 0; k < sk.Count; k++) worst = MathF.Max(worst, TestKit.Angle(prev[k], local[k]) * 180f / MathF.PI);
                    worstHips = MathF.Max(worstHips, Vector3.Distance(prevHips, hips));
                }
                local.CopyTo(prev, 0); prevHips = hips;
                poses.Add((Quaternion[])local.Clone());
            }
            switches = m.SwitchCount;
            return poses.ToArray();
        }
        var a = Run(out int switches, out float worst, out float worstHips);
        var b = Run(out _, out _, out _);
        _out.WriteLine($"{switches} switches, fastest bone turn {worst:F1} deg per tick, largest hips step {worstHips * 100f:F1} cm");
        Assert.True(switches >= 5, $"{switches} switches");
        Assert.True(worst <= 12f, $"a bone turned {worst:F1} degrees in a tick");
        Assert.True(worstHips <= 0.03f, $"the hips moved {worstHips * 100f:F1} cm in a tick");
        for (int i = 0; i < a.Length; i++) Assert.Equal(a[i], b[i]);
    }

    [Fact]
    public void ADribbleSetIsEnteredByTheCheapestClipForTheSpeedAndTheBall()
    {
        var sets = AttackerChooser.BuildSkills(SkillLibrary.Value);
        var lib = SkillLibrary.Value;
        Assert.Contains("skill:ball_roll", sets.Keys);
        var set = sets["skill:ball_roll"];
        // the authored ball is in the features: in front of him at the start of a ball roll
        var raw = set.Raw(set.Clips[0].Start);
        Assert.True(raw[Features.BallPos + 2] > 0.1f, $"ball {raw[Features.BallPos + 2]:F2} m ahead");
        var q = new float[Features.Count];
        string Entry(float speed, int foot)
        {
            // feet under the hips, moving with him at his speed, the ball 0.5 m ahead rolling with him
            AttackerChooser.EntryQuery(q, new Vector3(0.1f, 0.08f, 0f), new Vector3(-0.1f, 0.08f, 0f), new Vector3(0, 0, speed), new Vector3(0, 0, speed),
                new Vector3(0, 0, speed), new Vector3(0, 0.11f, 0.5f), Vector3.Zero, 0f, 0.1f);
            int i = AttackerChooser.PickEntry(set, q, pc => lib.Clips.First(c => ReferenceEquals(c.Clip, pc.Clip)) is { } sc && sc.Foot == foot && sc.Side == "left", out float cost);
            Assert.True(i >= 0 && float.IsFinite(cost));
            return lib.Clips.First(c => ReferenceEquals(c.Clip, set.Clips[i].Clip)).Entry + ":" + set.Clips[i].Clip.Name;
        }
        string slow = Entry(0f, 1), fast = Entry(4.5f, 1);
        _out.WriteLine($"standing: {slow}; running: {fast}");
        Assert.StartsWith("stand", slow);
        Assert.StartsWith("run", fast);
        // the running entry's share against the standing one, by the same costs: none of it standing, most of it at a run,
        // never falling as he comes in faster
        var pair = lib.Pair("ball_roll", 1, -1)!.Value;
        float shareLast = -1f;
        foreach (float v in new[] { 0f, 1f, 2f, 3f, 4.5f })
        {
            AttackerChooser.EntryQuery(q, new Vector3(0.1f, 0.08f, 0f), new Vector3(-0.1f, 0.08f, 0f), new Vector3(0, 0, v), new Vector3(0, 0, v),
                new Vector3(0, 0, v), new Vector3(0, 0.11f, 0.5f), Vector3.Zero, 0f, 0.1f);
            float share = AttackerChooser.EntryShare(set, pair.Stand.Clip, pair.Run.Clip, q);
            _out.WriteLine($"entry {v} m/s: run share {share:F2}");
            Assert.True(share >= shareLast - 1e-4f, $"the run share falls at {v} m/s");
            shareLast = share;
            if (v == 0f) Assert.True(share <= 0.1f, $"run share {share:F2} from standing");
        }
        Assert.True(shareLast >= 0.9f, $"run share {shareLast:F2} at a run");
        Assert.True(float.IsNaN(AttackerChooser.EntryShare(set, pair.Stand.Clip, TestKit.Stand(TestKit.Legs(), "x", 10), q)));
        // the blend share by cost: all of the cheaper entry past the span, even at equal costs, monotone between
        Assert.Equal(0f, AttackerChooser.BlendShare(1f, 5f));
        Assert.Equal(1f, AttackerChooser.BlendShare(5f, 1f));
        Assert.Equal(0.5f, AttackerChooser.BlendShare(2f, 2f), 4);
        float last = 0f;
        for (float c = 0.3f; c < 4f; c += 0.1f) { float s = AttackerChooser.BlendShare(c, 1f); Assert.True(s >= last); last = s; }
    }
}
