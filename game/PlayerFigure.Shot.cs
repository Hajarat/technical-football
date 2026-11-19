using System;
using Godot;

namespace FootballSim.Game;

/// <summary>
/// The attacker's shot drawing (with <c>PlanShot</c>): the strike sequence is timed backwards from the core's contact estimate, the
/// plant foot, and the upper body's share of the shot pose over the cycles.
/// </summary>
public partial class PlayerFigure
{
    /// <summary>SHOT_LOG=1 in the environment prints the shot state the figure draws from, per tick (replay mode).</summary>
    static readonly bool ShotLog = System.Environment.GetEnvironmentVariable("SHOT_LOG") == "1";

    /// <summary>The simulation's soft touch (the one a shot pressed with the ball at the feet makes on its press tick), as <see cref="FigureAction.Kind"/>.</summary>
    const int SoftTouchKind = (int)FootballSim.Core.ActionKind.Soft;

    /// <summary>The strike sequence's length (plant step, backswing and swing) for a full charge, seconds, per kind; a lighter charge shortens it.</summary>
    static float SequenceSeconds(ShotKind kind, float charge)
    {
        float full = kind switch { ShotKind.Power => 0.28f, ShotKind.Driven => 0.26f, ShotKind.Chip => 0.22f, _ => 0.24f };
        return full * (0.6f + 0.4f * Mathf.Clamp(charge, 0f, 1f));
    }

    /// <summary>The farthest the estimated contact point may be from where the body will be at the contact for the strike to be drawn (metres at 1.8 m).</summary>
    const float ShotReachM = 1.3f;

    /// <summary>How long a sequence under way takes to ease back into the run when the estimate slips later (seconds).</summary>
    const float SlipFade = 0.12f;

    // the sequence's state for the shot being drawn: its weight gate (eased out on a slip), the estimate last tick, the frozen time-to-contact while easing out
    float _sqGate = 1f, _sqTauPrev = float.MaxValue, _sqFreeze; bool _sqFading;
    // the drawn leave direction until the contact; the boot's yaw at the contact
    Vector3 _shDirPre = new(1, 0, 0); float _shPsiC;
    // the striking foot's place and velocity relative to the body last tick (the swing's), and the follow-through's start velocity and time
    Vector3 _shRelPrev, _shVRel, _shV0; bool _shRelPrevOk, _shTfSet; float _shTf;
    /// <summary>The shot pose's share of the upper body over the cycles (0..1, eased by the sequence).</summary>
    float _shUpper;
    /// <summary>How much of the trunk and arms the shot pose takes over from the cycles at <see cref="_shUpper"/> = 1.</summary>
    const float ShotUpperShare = 0.5f;

    // the plant: 0 undecided, 1 the stride's own stance foot, 2 a step to beside the ball; its foot, weight, ankle, direction, held flag,
    // the point it lands on, whether it has landed, and the release's progress (0..1)
    int _plMode, _plFoot; float _plW, _plRel, _plDhLand; Vector3 _plAnkle, _plDir = new(1, 0, 0), _plPoint; bool _plHeld, _plLanded;

    /// <summary>The hip-to-ankle ground distance (share of a straight-ish leg's ground reach) at which a held plant foot is let go while the body runs on.</summary>
    const float PlantReleaseReach = 0.92f;
    /// <summary>How far ahead of the hip (share of the straight-ish leg's ground reach) the plant may land while running: short of straight, so the knee is not whipped as it lands.</summary>
    const float PlantReach = 0.72f;

    /// <summary>Seconds before the contact the plant step lands (half the swing, at least 0.06 s).</summary>
    static float PlantLand(float swingT) => Math.Max(0.06f, 0.5f * swingT);

    void ShotSequenceReset(Vector3 fwd)
    {
        _sqGate = 1f; _sqTauPrev = float.MaxValue; _sqFading = false; _shDirPre = fwd;
        _plMode = 0; _plLanded = false; _plRel = 0f; _plHeld = false;
    }

    /// <summary>
    /// The time-to-contact the sequence is drawn at (<paramref name="len"/>: the longer of the strike sequence and the plant step). The estimate may move later by a whole step until the strike's commit: a
    /// sequence under way then eases out from where it was (the pose held at the old time while its weight falls), and starts again
    /// when the new estimate comes within its length. So does one whose ball is out of reach at the contact (<paramref name="inRange"/> false).
    /// </summary>
    float ShotSlip(float tau, float len, float swingT, float dt, bool inRange)
    {
        bool slip = tau > _sqTauPrev + 2.5f * dt && _sqTauPrev < len;
        if (!_sqFading && (slip || !inRange) && _sqGate > 0f && !_plLanded && Math.Min(tau, _sqTauPrev) < len) { _sqFading = true; _sqFreeze = slip ? _sqTauPrev : tau; }
        _sqTauPrev = tau;
        if (_sqFading)
        {
            _sqGate = Math.Max(0f, _sqGate - dt / SlipFade);
            if (_sqGate > 0f) return _sqFreeze;
            _sqFading = false; _plMode = 0;
            return tau;
        }
        // the ball out of the body's reach at the estimated contact (a shot pressed without the ball): no sequence until it is
        if (!inRange) { _sqGate = 0f; if (!_plLanded) _plMode = 0; return tau; }
        // back from an ease-out: the weight comes back no slower than it must to be all there by the swing
        if (_sqGate < 1f) _sqGate = Math.Min(1f, _sqGate + Math.Max(dt / 0.10f, (1f - _sqGate) * dt / Math.Max(tau - swingT, dt)));
        return tau;
    }

    /// <summary>The leave direction drawn until the contact: the facing turned toward the aim estimate (at most 0.8 rad).</summary>
    Vector3 AimEstimate(in FigureFrame f, Vector3 fwd)
    {
        Vector3 a = new(f.Shot.Aim.X, 0, f.Shot.Aim.Z);
        if (a.LengthSquared() < 0.01f) return fwd;
        float diff = Mathf.Wrap(Mathf.Atan2(a.Z, a.X) - Mathf.Atan2(fwd.Z, fwd.X), -Mathf.Pi, Mathf.Pi);
        float yaw = Mathf.Atan2(fwd.Z, fwd.X) + Math.Clamp(diff, -0.8f, 0.8f);
        return new Vector3(Mathf.Cos(yaw), 0, Mathf.Sin(yaw));
    }

    /// <summary>
    /// The plant foot (the non-striking foot <c>1 - fi</c>). Decided when the sequence starts: if the stride has that foot on the ground
    /// at the contact, the stride's own stance foot is the plant and nothing is drawn over it. Otherwise (standing, or an at-feet strike
    /// on the foot the stride has down then) the foot steps from where it is to beside the ball's contact point, a little behind it and
    /// within the leg's reach, lands one swing-half before the contact and is held where it landed until the follow-through carries the
    /// body on (or the leg runs out of reach), then lifts back to the stride.
    /// </summary>
    void PlanPlant(in FigureFrame f, Vector3 pos, Vector3 fwd, Vector3 right, float k, float hmax, int fi, float side, Vector3 centre,
        float tau, float tauD, float s, float len, float swingT)
    {
        float dt = f.Dt > 0f ? f.Dt : 1f / 120f;
        int j = 1 - fi;
        bool stepping = f.HasGait && f.IsStepping;
        bool made = f.Shot.Made;
        if (_plMode == 0)
        {
            if (!made && tauD > len) { PlantIdle(dt); return; }
            _plMode = 2;
            if (stepping)
            {
                // the plant foot's own phase at the contact (each foot's phase runs at half the step rate; its stance is about 0.75)
                float q = Frac((j == 0 ? f.LeftFootPhase : f.RightFootPhase) + 0.5f * f.StepFrequency * Math.Max(tau, 0f));
                if (q > 0.58f && q < 0.92f) _plMode = 1;
            }
            _plFoot = j; _plLanded = false; _plRel = 0f;
            if (ShotLog && Stats != null) GD.Print($"SHOT_PLANT tick={Stats.Tick} mode={(_plMode == 1 ? "stride" : "step")} foot={(j == 1 ? "R" : "L")} tau={tau:F3}");
        }
        if (_plMode == 1) { PlantIdle(dt); return; }
        _plFoot = j;
        Vector3 posXZ = new(pos.X, 0, pos.Z);
        Vector3 hv = new(f.Velocity.X, 0, f.Velocity.Z);
        float land = PlantLand(swingT);
        if (!_plLanded)
        {
            // beside where the ball is met and a little behind it, within reach of the hip where the body will be at the landing
            Vector3 pt = new Vector3(centre.X, 0, centre.Z) + right * (-side * 0.22f * k) - fwd * (0.05f * k);
            Vector3 hipL = posXZ + hv * Math.Max(0f, tau - land) + right * (-side * HipW);
            Vector3 dv = pt - hipL;
            float maxD = stepping ? PlantReach * hmax : hmax;
            if (dv.Length() > maxD) pt = hipL + dv.Normalized() * maxD;
            pt.Y = AnkleH;
            _plPoint = pt; _plDhLand = Dist2(hipL, pt);
            float wp = made ? 1f : Smooth((len - tauD) / Math.Max(len - land, 0.02f)) * _sqGate;
            Vector3 a = pt + Vector3.Up * (0.08f * k * Mathf.Sin(Mathf.Pi * wp));
            _plAnkle = a; _plW = wp; _plDir = fwd; _plHeld = false;
            if (wp >= 0.999f && (made || tauD <= land + 0.5f * dt) && !_sqFading) { _plLanded = true; _plAnkle = _plPoint; _plHeld = true; }
            return;
        }
        // landed: held until the follow-through has carried the body on, or the leg would run out of reach
        // (before the leg is near straight: a freed foot that far out would be drawn in toward the hip at once, see MocapSoftReach)
        Vector3 hip = posXZ + right * (-side * HipW);
        bool far = stepping && Dist2(hip, _plPoint) > PlantReleaseReach * Math.Max(hmax, 0.3f);
        if (_plRel <= 0f && !(made && s >= 0.10f) && !far) { _plW = 1f; _plAnkle = _plPoint; _plHeld = true; return; }
        if (_plRel <= 0f && !stepping)
        {
            // standing: the foot stays where it landed (the idle stance takes it over there)
            var ft = _feet[j];
            ft.Plant = _plPoint; ft.Ankle = _plPoint; ft.Planted = true; ft.Timed = false;
        }
        _plRel = Math.Min(1f, _plRel + dt / 0.12f);
        _plW = 1f - Smooth(_plRel);
        _plAnkle = _plPoint + Vector3.Up * (stepping ? 0.10f * k * Smooth(_plRel) : 0f);
        _plHeld = false;
    }

    /// <summary>No plant drawn: an earlier one's weight (a shot dropped while it was down) fades out from where it was.</summary>
    void PlantIdle(float dt)
    {
        _plHeld = false;
        if (_plW > 0f) _plW = Math.Max(0f, _plW - dt / 0.12f);
    }

    /// <summary>Outside a shot: no upper-body share and no plant (an earlier plant fades out).</summary>
    void ShotIdle(float dt)
    {
        _shUpper = Math.Max(0f, _shUpper - dt / 0.15f);
        PlantIdle(dt);
    }

    /// <summary>SHOT_BONE: the fastest bone turn on a shot tick, the striking leg (thigh, shin, foot, toes) left out while the shot draws it.</summary>
    void ShotBoneSpeeds(SkinnedBody b, int n, float dt)
    {
        if (Stats == null) return;
        Stats.ShotBoneTicks++;
        int legFrom = _kFoot == 0 ? 10 : 18;   // driven order per side from 6 / 14: shoulder, arm, forearm, hand, thigh, shin, foot, toe
        for (int k = 0; k < n; k++)
        {
            if (_kW > 0f && k >= legFrom && k < legFrom + 4) continue;
            float deg = _bonePrev[k].AngleTo(b.DrivenLocalRotation(k)) * 180f / Mathf.Pi / dt;
            if (deg > 1500f) { Stats.ShotBoneFast++; if (ShotLog) GD.Print($"SHOT_FAST tick={Stats.Tick} bone={b.DrivenBoneNames[k]} deg_s={deg:F0} kW={_kW:F2} plW={_plW:F2} mW={_mW:F2}"); }
            if (deg > Stats.ShotBoneMaxDegS) { Stats.ShotBoneMaxDegS = deg; Stats.ShotBoneAt = $"{b.DrivenBoneNames[k]}@{Stats.Tick}"; }
        }
    }

    /// <summary>
    /// How far the pelvis comes down for the plant foot (eased in with the plant's weight): what a straight-ish leg lacks to reach the
    /// plant point from the hip, so the leg reaches it as it lands instead of the planted-foot cap pulling the pelvis down in one tick.
    /// </summary>
    float PlantPelvisDrop(Vector3 pel, Vector3 right)
    {
        if (IsKeeper || _plW <= 0f) return 0f;
        Vector3 hip = pel + right * (_plFoot == 0 ? -HipW : HipW);
        // (from where the hip will be at the landing, or nearer: the body is still coming up to it)
        float dh = Math.Min(Dist2(hip, _plPoint), _plDhLand), lim = 0.985f * Leg;
        if (dh >= lim) return 0f;
        float need = (pel.Y - _plPoint.Y) - Mathf.Sqrt(lim * lim - dh * dh);
        return Math.Max(0f, need) * Smooth(_plW);
    }

    // the latest made dribble touch seen in the core's action slot, carried on while the shot's soft touch has taken its place
    FigureAction _lastTouch;

    /// <summary>
    /// The frame the touch overlay draws from: as given, except that a made touch still following through when the shot's soft touch
    /// took the core's action slot (the core then drops it from the previous slot) is carried on as the previous action until its
    /// recovery ends, so its follow-through hands back to the stride as it would have.
    /// </summary>
    FigureFrame SoftTouchFrame(in FigureFrame f, float dt)
    {
        var g = f;
        if (IsKeeper) return g;
        if (f.Action.Valid && f.Action.Kind == SoftTouchKind && !f.PrevAction.Valid)
        {
            if (_lastTouch.Valid && _lastTouch.Made && !_teleporting)
            {
                _lastTouch.ContactEta -= dt;
                _lastTouch.Phase = -_lastTouch.ContactEta < Math.Max(_lastTouch.RecoverySeconds, 0.06f) ? TouchPhase.Recovery : TouchPhase.Done;
                if (_lastTouch.Phase == TouchPhase.Done) _lastTouch.Valid = false;
                else g.PrevAction = _lastTouch;
            }
            return g;
        }
        _lastTouch = f.Action.Valid && f.Action.Made && !f.Action.IsSkill ? f.Action : default;
        return g;
    }
}
