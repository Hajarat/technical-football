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
        _blFacing = f.Facing;
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

    // ---- visible touches (mocap): the swing through the ball, its follow-through, the knee and the trunk with it ----
    /// <summary>Per foot, from the touch drawn this tick: the ball's direction out, seconds since its contact (-1 before it or none) and the surface.</summary>
    readonly Vector3[] _blTouchD = new Vector3[2];
    readonly float[] _blTouchS = { -1f, -1f };
    readonly BootSurface[] _blTouchSurf = new BootSurface[2];
    float _blFacing;
    bool BodyTouchOn => _atkAnim != null && !BodyLangOff;

    /// <summary>
    /// The windup's backswing (metres at 1.8 m) behind the contact along the touch's direction, <paramref name="x"/> = time left over the
    /// windup's span (1 at its start, 0 at the contact): x(2 - x) times <see cref="TouchBackM"/>, flat where it starts and steepest at the contact,
    /// so the foot arrives moving through the ball (the backswing over the span, twice over, is its speed against the body there).
    /// </summary>
    static float TouchSwingBack(float x) { x = Math.Clamp(x, 0f, 1f); return TouchBackM * x * (2f - x); }
    const float TouchBackM = 0.18f;
    /// <summary>The follow-through: along the touch's direction against the body, <see cref="FollowM"/> over <see cref="FollowS"/> (easing out from
    /// its fastest at the contact), lifted <see cref="FollowLiftM"/>; the touch's pose held whole for <see cref="FollowHoldS"/> (at most half
    /// the recovery) and given back to the stride over the rest of it, and from <see cref="FollowHandS"/> to <see cref="FollowS"/> all of
    /// this handed over to the procedural recovery.</summary>
    const float FollowReach = 0.96f;
    const float FollowM = 0.20f, FollowS = 0.125f, FollowHandS = 0.085f, FollowLiftM = 0.07f, FollowHoldS = 0.09f;

    /// <summary>The touch's pose after its contact (mocap): <paramref name="c"/> the ankle at the contact (world, carried with the body), <paramref name="cy"/> its height.</summary>
    /// <remarks>The follow-through stays within <see cref="FollowReach"/> of the leg from its hip (a reach past it would sink the pelvis, see Update).</remarks>
    void TouchFollowThrough(int i, float s, float rec, float k, Vector3 pelXZ, float facing, Vector3 c, float cy, Vector3 d, ref float wgt, ref Vector3 target)
    {
        float w0 = wgt; Vector3 t0 = target;
        // (all given back by the recovery's end, when the core drops the touch: held at most half of it)
        float hold = Math.Min(FollowHoldS, 0.5f * rec);
        wgt = s <= hold ? 1f : 1f - Smooth((s - hold) / Math.Max(rec - hold, 1e-3f));
        float u = Math.Min(1f, s / FollowS);
        target = c + d * (FollowM * k * (1f - (1f - u) * (1f - u)));
        target.Y = cy + FollowLiftM * k * Smooth(s / 0.10f);
        Vector3 right = new(-Mathf.Sin(facing), 0, Mathf.Cos(facing));
        Vector3 hip = new Vector3(pelXZ.X, StandY * _ratioS, pelXZ.Z) + right * (i == 0 ? -HipW : HipW);
        Vector3 dh = target - hip;
        // (never nearer than the contact's own pose: the contact is drawn exactly where the touch has it)
        float most = Math.Max(FollowReach * Leg, (new Vector3(c.X, cy, c.Z) - hip).Length());
        if (dh.Length() > most) target = hip + dh * (most / dh.Length());
        // past the follow-through it hands over to the stride's own recovery (exactly that from FollowS on): a skill move or shot that
        // starts then takes over from the pose it would have taken over from anyway
        float m = FollowShare(s);
        wgt = Mathf.Lerp(w0, wgt, m); target = t0.Lerp(target, m);
    }

    /// <summary>The playing foot's pitch over its stride pitch (radians, toes up positive) after the contact: the laces' toe pointed down through the follow-through.</summary>
    float TouchPitch(int i) => _blTouchS[i] >= 0f && _blTouchSurf[i] == BootSurface.Laces ? -LacesToeDown * Smooth(_blTouchS[i] / 0.05f) * FollowShare(_blTouchS[i]) : 0f;

    /// <summary>The follow-through's share of the touch's pose <paramref name="s"/> seconds after its contact: the cycles' weight, handed over to the procedural recovery from <see cref="FollowHandS"/> to <see cref="FollowS"/> (a touch from standing keeps the procedural one).</summary>
    float FollowShare(float s) => _mW * (1f - Smooth((s - FollowHandS) / (FollowS - FollowHandS)));
    const float LacesToeDown = 0.35f;

    /// <summary>The playing leg's knee pole (mocap): from the facing toward the boot's heading and the touch's direction by the touch's weight, so the knee drives over the ball the way it goes.</summary>
    /// <remarks>Its weight follows the touch's at most <see cref="TouchKneeRate"/> a second and its heading is kept from the last tick a touch drew the foot, so a touch dropped at once
    /// (a forecast hint withdrawn) never twists the leg about its hip-to-ankle line in a tick.</remarks>
    Vector3 TouchKneePole(int i, Vector3 fwd, Vector3 dir, float tw)
    {
        if (tw > 0f)
        {
            Vector3 t = new Vector3(dir.X, 0, dir.Z).Normalized() + 0.5f * _blTouchD[i];
            if (t.LengthSquared() > 1e-4f) _poleT[i] = t.Normalized();
        }
        // (handed back with the follow-through: none of it left once the procedural recovery has the foot)
        if (_blTouchS[i] >= 0f) tw *= 1f - Smooth((_blTouchS[i] - FollowHandS) / (FollowS - FollowHandS));
        _poleW[i] = _teleporting ? tw : _poleW[i] + Math.Clamp(tw - _poleW[i], -TouchKneeRate / 120f, TouchKneeRate / 120f);
        float w = Math.Clamp(_poleW[i], 0f, 1f);
        if (w <= 1e-4f || _poleT[i].LengthSquared() < 0.5f) return fwd;
        Vector3 p = fwd.Lerp(_poleT[i], TouchKneeShare * w);
        return p.LengthSquared() > 1e-4f ? p.Normalized() : fwd;
    }
    const float TouchKneeShare = 0.8f, TouchKneeRate = 8f;
    readonly float[] _poleW = new float[2];
    readonly Vector3[] _poleT = new Vector3[2];

    /// <summary>The trunk's turn toward a turn or cut touch as it is played (radians, positive toward his right): a share of the touch's angle off the facing past <see cref="TouchTurnFrom"/>, capped, by the touch's weight.</summary>
    float TouchTrunkYaw()
    {
        float sum = 0f;
        for (int i = 0; i < 2; i++)
        {
            float w = _twPrev[i];
            if (w <= 0f || _blTouchD[i].LengthSquared() < 0.5f) continue;
            float a = Mathf.Wrap(Mathf.Atan2(_blTouchD[i].Z, _blTouchD[i].X) - _blFacing, -Mathf.Pi, Mathf.Pi);
            float on = Smooth((Math.Abs(a) - TouchTurnFrom) / TouchTurnFrom);
            sum += Math.Clamp(a * TouchTurnShare, -TouchTurnMax, TouchTurnMax) * on * w;
        }
        return Math.Clamp(sum, -TouchTurnMax, TouchTurnMax);
    }
    const float TouchTurnFrom = 0.2f, TouchTurnShare = 0.3f, TouchTurnMax = 0.17f;

    /// <summary>The layers the animator lays over the cycles' trunk, head and arms this tick.</summary>
    BodyLayers BodyLayersNow() => new()
    {
        TrunkYaw = _bl.ShoulderLead + TouchTrunkYaw() * _bl.FreeShare, HeadYaw = _bl.HeadLead, ArmOutL = _bl.ArmOut(0), ArmOutR = _bl.ArmOut(1),
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
            if ((curMade || prevMade) && a.Kind != SoftTouchKind && !a.IsSkill && d.LengthSquared() > 0.04f && _mW < 0.99f) s.FollowStanding++;
            else if ((curMade || prevMade) && a.Kind != SoftTouchKind && !a.IsSkill && d.LengthSquared() > 0.04f)
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
    /// <summary>The fastest bone turn (deg/s, shots left out) outside the whole leg of a foot playing the ball, where, and the ticks over 1,500.</summary>
    public float OffLegMaxDegS; public string OffLegAt = "-"; public int OffLegFast;
    /// <summary>Dribble touches made below the cycles' full weight (from standing, at a walk): left out of the follow-through, counted.</summary>
    public int FollowStanding;
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
            $" follow_m_min={fMin:F3} mean={(Follow.Count > 0 ? fSum / Follow.Count : float.NaN):F3} n={Follow.Count} standing_n={FollowStanding}" +
            $" off_leg_bone_max_deg_s={OffLegMaxDegS:F0} at={OffLegAt} off_leg_over_1500={OffLegFast}";
    }
}
