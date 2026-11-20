using System;
using System.Collections.Generic;
using FootballSim.Anim;
using Godot;

namespace FootballSim.Game;

/// <summary>
/// The attacker's body language over the stride cycles (--attacker-anim mocap only; see <see cref="BodyLanguage"/>): stepped each tick from the
/// figure's smoothed motion, laid on the pelvis before the legs are solved and on the trunk, head and arms by <see cref="AttackerAnimator"/>,
/// faded out while a skill or shot clip owns the body. The keeper has none of it.
/// </summary>
public partial class PlayerFigure
{
    readonly BodyLanguage _bl = new();
    /// <summary>BODY_LANG=off in the environment: the layers are not drawn (the pose before them, for comparison); the measurements still run.</summary>
    /// <summary>BODY_LANG_LOG=1: a BODY_LANG_TICK line per tick with the measured and layered amounts.</summary>
    static readonly bool BodyLangLog = System.Environment.GetEnvironmentVariable("BODY_LANG_LOG") == "1";
    static readonly bool BodyLangOff = System.Environment.GetEnvironmentVariable("BODY_LANG") == "off";
    /// <summary>The layers' share this tick (0 while a skill move or a shot owns the body).</summary>
    float _blFree;

    /// <summary>Whether a skill move or a shot owns the body this tick (the body-language layers give way to it).</summary>
    bool BodyOwned(in FigureFrame f) =>
        f.Skill.Valid || f.PendingSkill.Valid || _skActive || f.Shot.Valid || f.ShotCharge > 0.001f || !float.IsNaN(f.StrikeEta) || _kW > 0f || _strikeT >= 0f
        || _atkAnim?.Skills is { Active: true } || _atkAnim?.Shots is { Active: true };

    /// <summary>Steps the layers' amounts for this tick (after the cycles are sampled).</summary>
    void BodyLanguageStep(in FigureFrame f, float dt)
    {
        if (_teleporting) _bl.Reset();
        FacingRate(f, dt);
        _blFree = BodyOwned(f) ? 0f : 1f;
        _bl.Step(new BodyLanguage.Input { Speed = _speedS, Lateral = _latS, Forward = _accFS, Free = _blFree, Dt = dt });
    }

    float _faceRateS, _facePrev = float.NaN;
    /// <summary>How fast (seconds) the facing's turn rate is smoothed; the turn rate (rad/s) at which the look is wholly the turn's.</summary>
    const float FaceRateTau = 0.04f, FaceRateFull = 2.5f;

    /// <summary>0..1: how much of a turn he is in (the facing turning, or the run's sideways acceleration); the look leaves the ball and the travel for the facing by it.</summary>
    float BodyTurnWeight => Math.Clamp(Math.Max(Math.Abs(_faceRateS) / FaceRateFull, _bl.TurnWeight), 0f, 1f);

    /// <summary>
    /// The look's aim ahead of its base in a turn (radians, positive toward his right): the layer's look-ahead along the run's curve plus the
    /// facing's turn over the look's own easing time <paramref name="tau"/> (an eased aim trails a turning target by its rate times its time).
    /// </summary>
    float BodyLookAhead(float tau) => _bl.LookAhead + _faceRateS * tau;

    void FacingRate(in FigureFrame f, float dt)
    {
        float r = float.IsNaN(_facePrev) || _teleporting ? 0f : Mathf.Wrap(f.Facing - _facePrev, -Mathf.Pi, Mathf.Pi) / dt;
        _facePrev = f.Facing;
        _faceRateS = _teleporting ? 0f : Mathf.Lerp(_faceRateS, BodyOwned(f) ? 0f : r, 1f - Mathf.Exp(-dt / FaceRateTau));
    }

    /// <summary>The layers the animator lays over the cycles' trunk, head and arms this tick.</summary>
    BodyLayers BodyLayersNow() => new()
    {
        TrunkYaw = _bl.ShoulderLead, HeadYaw = _bl.HeadLead, ArmOutL = _bl.ArmOut(0), ArmOutR = _bl.ArmOut(1),
    };

    // ---- measurement (BODY_LANG): read from the shown body ----
    int _blTurnSign, _blTurnStart = -1, _blTurnEnd = int.MinValue / 2;
    BodyLangStats.Turn _blTurn;
    int _blFollowTick = -1, _blFollowFoot; Vector3 _blFollowDir, _blFollowRel0;

    /// <summary>
    /// The quantities the body-language acceptance reads, from the shown body: the trunk's bank (hips to neck, rolled toward the inside of a
    /// turn), the shoulders' turn ahead of the hips and the head's ahead of the shoulders, the trunk's lean against the cycles' steady lean at
    /// the same speed, the pelvis against the cycles' own, the head's pitch, a touch's follow-through.
    /// </summary>
    void BodyLangMeasure(in FigureFrame f, Vector3[] ank, Vector3 pel, float lean, Vector3 fwd, float dt)
    {
        if (_atkAnim == null || _body == null || Stats == null) return;
        var s = Stats.BodyLang;
        var b = _body;
        int tick = Stats.Tick;
        Vector3 right = new(-fwd.Z, 0, fwd.X);
        Vector3 up = b.DrivenBoneWorld(4) - pel;
        float bank = Mathf.Atan2(up.Dot(right), up.Y);
        Vector3 hr = b.DrivenBoneWorld(18) - b.DrivenBoneWorld(10), sr = b.DrivenBoneWorld(15) - b.DrivenBoneWorld(7);
        float yawH = Mathf.Atan2(-hr.X, hr.Z), yawS = Mathf.Atan2(-sr.X, sr.Z);
        Vector3 hf = _atkAnim.HeadForward;
        float yawHd = Mathf.Atan2(hf.Z, hf.X);
        float shLead = Mathf.Wrap(yawS - yawH, -Mathf.Pi, Mathf.Pi), hdLead = Mathf.Wrap(yawHd - yawS, -Mathf.Pi, Mathf.Pi);
        float headDown = Mathf.Asin(Mathf.Clamp(-hf.Y, -1f, 1f));
        // less the cycle's own roll and shoulder turn at this phase (its stride sway, there with no layer at all)
        float net = bank - _atkAnim.CycleRoll, shNet = Mathf.Wrap(shLead - _atkAnim.CycleShoulderTurn, -Mathf.Pi, Mathf.Pi);
        bool free = _blFree >= 1f && _mW >= 0.99f && _speedS > 2f;
        const float D = 180f / Mathf.Pi;

        if (BodyLangLog) GD.Print($"BODY_LANG_TICK tick={tick} v={_speedS:F2} lat={_latS:F2} acc={_accFS:F2} free={_blFree:F0} w={_mW:F2} bank={bank * D:F1} net={net * D:F1} sh={shLead * D:F1} sh_net={shNet * D:F1} head={hdLead * D:F1} head_down={headDown * D:F1} lean={lean * D:F1} ref={_atkAnim.SteadyLean(_speedS) * D:F1} l_bank={_bl.Bank * D:F1} l_sh={_bl.ShoulderLead * D:F1} l_head={_bl.HeadLead * D:F1} l_look={_bl.LookAhead * D:F1} hy={_headYaw * D:F0} face={f.Facing * D:F0}");
        // turns: |sideways acceleration| over 1.5 m/s^2 while running freely
        bool turning = free && Math.Abs(_latS) >= 1.5f;
        if (turning && _blTurnStart < 0) { _blTurnStart = tick; _blTurnSign = Math.Sign(_latS); _blTurn = new BodyLangStats.Turn { StartTick = tick }; }
        if (_blTurnStart >= 0 && (!turning || Math.Sign(_latS) != _blTurnSign))
        {
            _blTurn.EndTick = tick;
            if ((tick - _blTurnStart) * dt >= 0.15f) s.Turns.Add(_blTurn);
            _blTurnStart = -1; _blTurnEnd = tick;
        }
        if (_blTurnStart >= 0)
        {
            _blTurn.BankDeg = Math.Max(_blTurn.BankDeg, bank * _blTurnSign * D);
            float bin = net * _blTurnSign;
            if (bin > _blTurn.NetBankDeg / D)
            {
                _blTurn.NetBankDeg = bin * D; _blTurn.ShoulderDeg = shLead * _blTurnSign * D; _blTurn.ShoulderNetDeg = shNet * _blTurnSign * D; _blTurn.HeadDeg = hdLead * _blTurnSign * D;
                _blTurn.Speed = _speedS; _blTurn.PeakTick = tick; _blTurn.LayerBankDeg = _bl.Bank * _blTurnSign * D;
            }
            _blTurn.ShoulderMaxDeg = Math.Max(_blTurn.ShoulderMaxDeg, shLead * _blTurnSign * D);
            _blTurn.HeadMaxDeg = Math.Max(_blTurn.HeadMaxDeg, hdLead * _blTurnSign * D);
        }
        else
        {
            float since = (tick - _blTurnEnd) * dt;
            // 0.5 s to 1 s after a turn ended: the bank should be gone
            if (since >= 0.5f && since <= 1.0f && free && s.Turns.Count > 0) { s.AfterTurnMaxDeg = Math.Max(s.AfterTurnMaxDeg, Math.Abs(bank) * D); s.AfterTurnNetDeg = Math.Max(s.AfterTurnNetDeg, Math.Abs(net) * D); }
            // running straight: no turn for a second and the sideways acceleration small
            if (since > 1.0f && free && Math.Abs(_latS) < 0.5f) { s.StraightMaxDeg = Math.Max(s.StraightMaxDeg, Math.Abs(bank) * D); s.StraightNetDeg = Math.Max(s.StraightNetDeg, Math.Abs(net) * D); s.StraightN++; }
        }

        // lean, pelvis and head, per tick (summarised at the end)
        Vector3 toBall = new(f.BallPos.X - f.Position.X, 0, f.BallPos.Z - f.Position.Z);
        float bd = toBall.Length();
        s.Ticks.Add(new BodyLangStats.Sample
        {
            Speed = _speedS, Lean = lean, Ref = _atkAnim.SteadyLean(_speedS), Acc = _accFS, Lat = _latS, Near = f.InPossession && bd < 1.2f,
            BallDist = bd, Free = free, PelY = pel.Y, CycPelY = _atkAnim.Pelvis.Y, HeadDown = headDown,
        });

        // a dribble touch's follow-through: the touching foot's move along the touch's direction relative to the pelvis over the 0.08 s after the contact
        if (_blFollowTick >= 0 && (tick - _blFollowTick) * dt >= 0.08f - 1e-4f)
        {
            Vector3 rel = ank[_blFollowFoot] - pel; rel.Y = 0;
            s.Follow.Add((rel - _blFollowRel0).Dot(_blFollowDir));
            _blFollowTick = -1;
        }
        if (f.TouchEvent && !f.StrikeEvent && !f.Skill.Valid)
        {
            bool curMade = f.Action.Valid && f.Action.Made && f.Action.Phase == TouchPhase.Contact;
            bool prevMade = f.PrevAction.Valid && f.PrevAction.Made && f.PrevAction.Phase == TouchPhase.Contact;
            var a = curMade ? f.Action : f.PrevAction;
            Vector3 d = new(a.BallOutVelocity.X, 0, a.BallOutVelocity.Z);
            if ((curMade || prevMade) && a.Kind != SoftTouchKind && !a.IsSkill && d.LengthSquared() > 0.04f)
            {
                _blFollowTick = tick; _blFollowFoot = a.Right ? 1 : 0; _blFollowDir = d.Normalized();
                _blFollowRel0 = ank[_blFollowFoot] - pel; _blFollowRel0.Y = 0;
            }
        }
    }
}

/// <summary>The body-language measurements (BODY_LANG lines), collected from the shown attacker in --attacker-anim mocap.</summary>
public sealed class BodyLangStats
{
    /// <summary>One turn (sideways acceleration over 1.5 m/s^2, at least 0.15 s): the peak bank toward the inside and, on that tick, the shoulders' and head's leads.</summary>
    public struct Turn
    {
        public int StartTick, EndTick, PeakTick;
        public float Speed, BankDeg, NetBankDeg, ShoulderDeg, ShoulderNetDeg, HeadDeg, ShoulderMaxDeg, HeadMaxDeg, LayerBankDeg;
    }

    public struct Sample
    {
        public float Speed, Lean, Ref, Acc, Lat, BallDist, PelY, CycPelY, HeadDown;
        public bool Near, Free;
    }

    public readonly List<Turn> Turns = new();
    public readonly List<Sample> Ticks = new();
    public readonly List<float> Follow = new();
    public float StraightMaxDeg, StraightNetDeg, AfterTurnMaxDeg, AfterTurnNetDeg; public int StraightN;

    const float D = 180f / MathF.PI;

    public IEnumerable<string> Lines()
    {
        foreach (var t in Turns)
            yield return $"BODY_LANG_TURN start={t.StartTick} end={t.EndTick} peak={t.PeakTick} speed={t.Speed:F2} bank_in_max_deg={t.BankDeg:F1} bank_in_net_deg={t.NetBankDeg:F1} layer_bank_deg={t.LayerBankDeg:F1} shoulder_lead_deg={t.ShoulderDeg:F1} shoulder_lead_net_deg={t.ShoulderNetDeg:F1} head_lead_deg={t.HeadDeg:F1} shoulder_lead_max_deg={t.ShoulderMaxDeg:F1} head_lead_max_deg={t.HeadMaxDeg:F1}";
        // steady: running freely with neither acceleration to speak of; the trunk's lean less the cycles' own steady lean at that speed
        float resB = 0, resN = 0, pelB = 0, pelN = 0; int nB = 0, nN = 0;
        foreach (var x in Ticks)
        {
            if (!x.Free || Math.Abs(x.Acc) >= 0.5f || Math.Abs(x.Lat) >= 0.5f) continue;
            if (x.Near) { resB += x.Lean - x.Ref; pelB += x.CycPelY - x.PelY; nB++; } else { resN += x.Lean - x.Ref; pelN += x.CycPelY - x.PelY; nN++; }
        }
        float rB = nB > 0 ? resB / nB : float.NaN, rN = nN > 0 ? resN / nN : float.NaN;
        // lean against steady: against the steady residual of the same state (with the ball or without; the cycles' own when a state has no steady ticks)
        float accPk = float.NaN, accSp = 0, accA = 0, brkPk = float.NaN, brkSp = 0, brkA = 0, brkDrop = 0, at6 = float.NaN;
        foreach (var x in Ticks)
        {
            if (!x.Free) continue;
            float r = x.Near ? (nB > 0 ? rB : 0f) : (nN > 0 ? rN : 0f);
            float v = (x.Lean - x.Ref - r) * D;
            if (x.Acc > 1f && (float.IsNaN(accPk) || v > accPk)) { accPk = v; accSp = x.Speed; accA = x.Acc; }
            if (x.Acc < -1f && (float.IsNaN(brkPk) || -v > brkPk)) { brkPk = -v; brkSp = x.Speed; brkA = x.Acc; brkDrop = x.CycPelY - x.PelY; }
            if (x.Acc <= -6f) at6 = float.IsNaN(at6) ? -v : Math.Min(at6, -v);
        }
        float hdSum = 0, hdMin = float.NaN; int hdN = 0;
        foreach (var x in Ticks)
            if (x.Free && x.Near && x.BallDist < 1f) { hdSum += x.HeadDown * D; hdN++; hdMin = float.IsNaN(hdMin) ? x.HeadDown * D : Math.Min(hdMin, x.HeadDown * D); }
        float fMin = float.NaN, fSum = 0;
        foreach (var v in Follow) { fSum += v; fMin = float.IsNaN(fMin) ? v : Math.Min(fMin, v); }
        int best = -1;
        for (int i = 0; i < Turns.Count; i++) if (best < 0 || Turns[i].NetBankDeg > Turns[best].NetBankDeg) best = i;
        var bt = best >= 0 ? Turns[best] : default;
        yield return $"BODY_LANG turns={Turns.Count} bank_in_max_deg={(best >= 0 ? bt.BankDeg : float.NaN):F1} bank_in_net_deg={(best >= 0 ? bt.NetBankDeg : float.NaN):F1} speed={bt.Speed:F2} shoulder_lead_deg={(best >= 0 ? bt.ShoulderDeg : float.NaN):F1} shoulder_lead_net_deg={(best >= 0 ? bt.ShoulderNetDeg : float.NaN):F1} head_lead_deg={(best >= 0 ? bt.HeadDeg : float.NaN):F1}" +
            $" after_turn_bank_max_deg={AfterTurnMaxDeg:F1} net={AfterTurnNetDeg:F1} straight_bank_max_deg={StraightMaxDeg:F1} net={StraightNetDeg:F1} straight_n={StraightN}" +
            $" steady_lean_vs_cycles_deg ball={rB * D:F1} n={nB} noball={rN * D:F1} n={nN}" +
            $" lean_acc_peak_deg={accPk:F1} at_speed={accSp:F2} acc={accA:F1} lean_brake_peak_deg={brkPk:F1} at_speed={brkSp:F2} acc={brkA:F1} brake_pelvis_drop_m={brkDrop:F3} lean_brake_at_6_min_deg={at6:F1}" +
            $" posture_lean_deg={(nB > 0 && nN > 0 ? (rB - rN) * D : float.NaN):F1} posture_pelvis_drop_m={(nB > 0 && nN > 0 ? pelB / nB - pelN / nN : float.NaN):F3} pelvis_below_cycles_ball_m={(nB > 0 ? pelB / nB : float.NaN):F3}" +
            $" head_down_deg_mean={(hdN > 0 ? hdSum / hdN : float.NaN):F1} min={hdMin:F1} n={hdN}" +
            $" follow_m_min={fMin:F3} mean={(Follow.Count > 0 ? fSum / Follow.Count : float.NaN):F3} n={Follow.Count}";
    }
}
