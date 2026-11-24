using System.Numerics;

namespace FootballSim.Core;

// What the round report measures beyond the rules: the first attack (how the attacking side's first go at goal ended), the
// first time the ball was past the defender, and the keeper's depth, recovery and distance off the shot line. It only reads
// the world after each tick (and at a strike and at the round's end); it changes nothing and draws no random numbers.
public sealed partial class World
{
    /// <summary>The ball must be at least this much nearer the goal centre than the defender to be past him, metres.</summary>
    public const float PastMarginM = 0.5f;
    /// <summary>The attacking side must still have the ball this long after it went past the defender, seconds.</summary>
    public const float PastHoldS = 0.5f;
    /// <summary>The ball went past the defender within this long of a pass: past by the pass, seconds.</summary>
    public const float PastByPassS = 1.5f;
    /// <summary>The keeper is back on the ball-to-goal-centre line (goal-side of the ball) within this distance of it, metres.</summary>
    public const float BackOnLineM = 0.75f;

    FirstAttackEnd? _faEnd;         // how the first attack ended (null while it is on)
    int _faTick;
    int _lastTeam;                  // the team that last had or played the ball (0 the attackers, 1 the keeper's side)
    int _pastCandidate;             // the tick the ball went past the defender, waiting for the attackers to keep it (-1 none)
    int _pastTick;                  // the first confirmed tick past him (-1 not yet)
    bool _pastByPass, _candidateByPass; // whether it went past him within PastByPassS of a pass
    int _passTick;                  // the last tick the attackers struck a pass
    int _passesSeen;
    int _roundedTick, _backTick;    // the keeper taken round, and back on the line after it (-1 not yet)
    float _depthMax;
    readonly List<float> _offLineAtStrike = new();

    /// <summary>How the first attack ended, or null while it is still on.</summary>
    public FirstAttackEnd? FirstAttackEnded => _faEnd;
    /// <summary>The tick the first attack ended (0 while it is on).</summary>
    public int FirstAttackTick => _faTick;
    /// <summary>The first tick the ball was past the defender (see <see cref="IsPast"/>), or -1.</summary>
    public int PastTick => _pastTick;
    /// <summary>Seconds from the keeper's first being taken round until he was back on the ball-to-goal line, or null (so far).</summary>
    public float? KeeperRecoverS => _roundedTick >= 0 && _backTick >= 0 ? (_backTick - _roundedTick) * _dt : null;

    void ResetMeasures()
    {
        _faEnd = null; _faTick = 0;
        _lastTeam = 0;
        _pastCandidate = -1; _pastTick = -1; _pastByPass = _candidateByPass = false;
        _passTick = int.MinValue / 2; _passesSeen = 0;
        _roundedTick = -1; _backTick = -1;
        _depthMax = 0f;
        _offLineAtStrike.Clear();
    }

    /// <summary>
    /// The ball is past the defender: at least <see cref="PastMarginM"/> nearer the goal centre than he is. (It counts once the
    /// attacking side still has the ball <see cref="PastHoldS"/> later: see <see cref="ObserveMeasures"/>.)
    /// </summary>
    public static bool IsPast(Vector2 ball, Vector2 defender, Vector2 goal) =>
        Vector2.Distance(ball, goal) + PastMarginM <= Vector2.Distance(defender, goal);

    /// <summary>The flat distance from <paramref name="p"/> to the segment from <paramref name="a"/> to <paramref name="b"/>, metres.</summary>
    public static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        float u = len2 < 1e-9f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return Vector2.Distance(p, a + ab * u);
    }

    Vector2 GoalCentre => new(Pitch.GoalLineX, 0f);

    /// <summary>
    /// The keeper is back on the line from the ball to the goal centre: goal-side of the ball along it (not behind the ball) and
    /// within <see cref="BackOnLineM"/> of it.
    /// </summary>
    public static bool BackOnLine(Vector2 keeper, Vector2 ball, Vector2 goal)
    {
        var ab = goal - ball;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-9f) return Vector2.Distance(keeper, goal) <= BackOnLineM;
        float u = Vector2.Dot(keeper - ball, ab) / len2;
        return u > 0f && SegmentDistance(keeper, ball, goal) <= BackOnLineM;
    }

    /// <summary>After each simulated tick of play: who has the ball, past the defender, the keeper's depth and his recovery.</summary>
    void ObserveMeasures()
    {
        if (State is not (RoundState.Live or RoundState.Strike or RoundState.Flight)) return;
        bool lost = false;
        foreach (var e in _events)
        {
            if (e.Kind is EventKind.Tackle or EventKind.Block or EventKind.Save) lost = true;
            else if (e.Kind is EventKind.Strike or EventKind.Pass) _lastTeam = 0;
        }
        if (_controller != null) { if (_controller.Team != 0) lost = true; else _lastTeam = 0; }
        if (lost) _lastTeam = 1;
        if (_passes != _passesSeen) { _passesSeen = _passes; _passTick = Tick; }

        if (Defender is { } d && _pastTick < 0)
        {
            if (_lastTeam != 0) _pastCandidate = -1;
            else if (_pastCandidate < 0 && IsPast(Flat(Ball.Position), Flat(d.Position), GoalCentre))
            {
                _pastCandidate = Tick;
                _candidateByPass = Tick - _passTick <= Ticks(PastByPassS);
            }
            if (_pastCandidate >= 0 && Tick - _pastCandidate >= Ticks(PastHoldS)) { _pastTick = _pastCandidate; _pastByPass = _candidateByPass; }
        }

        var kf = Flat(Keeper.Position);
        _depthMax = MathF.Max(_depthMax, Pitch.GoalLineX - Keeper.Position.X);
        if (_roundedTick < 0 && _brain.Report.Rounded) _roundedTick = Tick;
        if (_roundedTick >= 0 && _backTick < 0 && Tick > _roundedTick && BackOnLine(kf, Flat(Ball.Position), GoalCentre)) _backTick = Tick;
    }

    /// <summary>A shot has just been struck: the keeper's distance off the line from the ball to the goal centre.</summary>
    void NoteStrike() => _offLineAtStrike.Add(SegmentDistance(Flat(Keeper.Position), Flat(Ball.Position), GoalCentre));

    /// <summary>A struck shot met the boards with no keeper, frame or defender touch: a clean miss ends the first attack.</summary>
    void NoteMiss()
    {
        if (_faEnd != null) return;
        _faEnd = FirstAttackEnd.Miss;
        _faTick = Tick;
    }

    /// <summary>The round's outcome is decided: it ends the first attack if still on, and settles a pass of the defender in waiting.</summary>
    void NoteOutcome(Outcome outcome)
    {
        if (_pastTick < 0 && _pastCandidate >= 0 && outcome == Outcome.Goal) { _pastTick = _pastCandidate; _pastByPass = _candidateByPass; }
        if (_faEnd != null) return;
        _faTick = Tick;
        _faEnd = outcome switch
        {
            Outcome.Goal => FirstAttackEnd.Goal,
            Outcome.Held => FirstAttackEnd.Held,
            Outcome.Dispossessed => FirstAttackEnd.Dispossessed,
            Outcome.Foul => FirstAttackEnd.Foul,
            Outcome.Offside => FirstAttackEnd.Offside,
            _ => FirstAttackEnd.Time,
        };
    }

    FirstAttackReport FinishFirstAttack() => new()
    {
        Goal = _faEnd == FirstAttackEnd.Goal,
        EndedTick = _faTick,
        EndedBy = _faEnd ?? FirstAttackEnd.Time,
    };

    /// <summary>Adds the measured keeper fields to his report at the round's end.</summary>
    KeeperReport FinishKeeperMeasures(KeeperReport k)
    {
        k.DepthMaxM = _depthMax;
        k.RecoverS = KeeperRecoverS;
        k.OffLineAtStrikeM = _offLineAtStrike.ToArray();
        return k;
    }

    /// <summary>Adds the measured defender fields to his report at the round's end.</summary>
    void FinishDefenderMeasures(DefenderReport? d)
    {
        if (d == null) return;
        d.BeatenS = _pastTick >= 0 ? _pastTick * _dt : null;
        d.BeatenBy = _pastTick < 0 ? BeatenBy.None : _pastByPass ? BeatenBy.Pass : BeatenBy.Dribble;
    }
}
