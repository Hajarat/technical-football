namespace FootballSim.Anim;

/// <summary>
/// Body language over the attacker's stride cycles (drawing only): smooth per-tick amounts the figure lays over the cycles' pose, each a
/// function of the simulation's motion (smoothed speed, sideways and forward acceleration) and the ball, eased by critically damped
/// springs so nothing pops. Turning: a bank into the turn from the sideways acceleration (speed times turn rate), the pelvis lower in it,
/// the shoulders ahead of the hips and the head ahead of the shoulders toward where he is going, the outside arm wider. All of it fades
/// with <see cref="Input.Free"/> (0 while a skill or shot clip owns the body). No clock and no randomness.
/// </summary>
public sealed class BodyLanguage
{
    /// <summary>One tick's drivers. Angles in radians, lengths in metres for a 1.8 m figure.</summary>
    public struct Input
    {
        /// <summary>Smoothed ground speed (m/s), the sideways acceleration (m/s^2, positive toward his right) and the forward one.</summary>
        public float Speed, Lateral, Forward;
        /// <summary>0..1: the layers' share of the body (1 running freely, 0 while a skill move or shot owns it).</summary>
        public float Free;
        public float Dt;
    }

    // ---- turning ----
    /// <summary>Bank = atan(sideways acceleration / g) times the gain, capped at a jog's and a sprint's limit (blended by speed between the two).</summary>
    public float BankGain = 1.15f, BankMaxJog = 0.30f, BankMaxSprint = 0.42f, JogMs = 4.8f, SprintMs = 7.2f;
    /// <summary>Half-life (seconds) of the bank's and the leads' approach to their targets.</summary>
    public float BankHalfLife = 0.06f, LeadHalfLife = 0.05f;
    /// <summary>The sideways acceleration (m/s^2) under which no bank, lead or wide arm is drawn, and over which they rise in full (the stride's own sway and a steering wobble stay upright).</summary>
    public float LateralDead = 0.6f, LateralFull = 1.6f;
    /// <summary>
    /// The shoulders turn ahead of the hips by the turn rate (sideways acceleration over speed) times this look-ahead time, the head ahead of
    /// the shoulders by its own; each capped (radians).
    /// </summary>
    public float ShoulderLeadS = 0.11f, HeadLeadS = 0.15f, ShoulderLeadMax = 0.38f, HeadLeadMax = 0.42f;
    /// <summary>
    /// The figure's look (where its procedural head is aimed, eased behind its target) is aimed this far ahead along the turn (turn rate times
    /// this time, radians, capped), so the eyes go where he is going instead of trailing the run.
    /// </summary>
    public float LookAheadS = 0.16f, LookAheadMax = 0.6f;
    /// <summary>Pelvis drop (metres at 1.8 m) per radian of bank, and the outside arm's extra abduction per radian of bank.</summary>
    public float PelvisDropPerRad = 0.12f, OuterArmPerRad = 1.1f;

    /// <summary>The bank (radians, positive rolled toward his right), the pelvis drop (metres at 1.8 m), the shoulders' yaw ahead of the
    /// hips and the head's ahead of the shoulders (radians, positive toward his right), the extra abduction of each arm (0 left, 1 right).</summary>
    public float Bank => _bank;
    public float PelvisDrop => PelvisDropPerRad * MathF.Abs(_bank);
    public float ShoulderLead => _shLead;
    public float HeadLead => _hdLead;
    /// <summary>The look's aim ahead along the turn (radians, positive toward his right).</summary>
    public float LookAhead => _look;
    /// <summary>0..1: how much of a turn he is in (the look's aim ahead against <see cref="TurnFullRad"/>); the look gives up its pull toward the ball by it.</summary>
    public float TurnWeight => Math.Clamp(MathF.Abs(_look) / TurnFullRad, 0f, 1f);
    public float TurnFullRad = 0.2f;
    public float ArmOut(int side) => OuterArmPerRad * MathF.Max(0f, side == 0 ? _bank : -_bank);

    /// <summary>The free share eased (half-life <see cref="LeadHalfLife"/>), for amounts the figure lays on itself.</summary>
    public float FreeShare => _free;
    float _free, _freeV;
    float _bank, _bankV, _shLead, _shLeadV, _hdLead, _hdLeadV, _look, _lookV;
    bool _have;

    public void Reset() { _have = false; _bank = _bankV = _shLead = _shLeadV = _hdLead = _hdLeadV = _look = _lookV = 0f; _free = _freeV = 0f; }

    /// <summary>The bank's target for a speed and sideways acceleration, before the free share (radians).</summary>
    public float BankTarget(float speed, float lateral)
    {
        float lat = Dead(lateral);
        float cap = BankMaxJog + (BankMaxSprint - BankMaxJog) * Math.Clamp((speed - JogMs) / (SprintMs - JogMs), 0f, 1f);
        float b = MathF.Atan(lat / 9.81f) * BankGain * Math.Clamp(speed / 3f, 0f, 1f);
        return Math.Clamp(b, -cap, cap);
    }

    /// <summary>The leads' targets (shoulders over hips, head over shoulders) for a speed and sideways acceleration, before the free share.</summary>
    public (float Shoulder, float Head, float Look) LeadTarget(float speed, float lateral)
    {
        float rate = Dead(lateral) / MathF.Max(speed, 2f);   // turn rate, rad/s (slow speeds held at a walking pace's)
        float on = Math.Clamp(speed / 2.5f, 0f, 1f);
        return (Math.Clamp(rate * ShoulderLeadS, -ShoulderLeadMax, ShoulderLeadMax) * on, Math.Clamp(rate * HeadLeadS, -HeadLeadMax, HeadLeadMax) * on,
            Math.Clamp(rate * LookAheadS, -LookAheadMax, LookAheadMax) * on);
    }

    /// <summary>The sideways acceleration faded in smoothly from 0 at <see cref="LateralDead"/> to all of it at <see cref="LateralFull"/>.</summary>
    public float Dead(float lateral)
    {
        float u = Math.Clamp((MathF.Abs(lateral) - LateralDead) / MathF.Max(LateralFull - LateralDead, 1e-3f), 0f, 1f);
        return lateral * u * u * (3f - 2f * u);
    }

    /// <summary>One tick: every amount eased toward its target.</summary>
    public void Step(in Input x)
    {
        float free = Math.Clamp(x.Free, 0f, 1f);
        float bT = BankTarget(x.Speed, x.Lateral) * free;
        var (sT, hT, lT) = LeadTarget(x.Speed, x.Lateral);
        sT *= free; hT *= free; lT *= free;
        if (!_have) { _bank = bT; _shLead = sT; _hdLead = hT; _look = lT; _free = free; _bankV = _shLeadV = _hdLeadV = _lookV = _freeV = 0f; _have = true; return; }
        float dt = x.Dt > 0f ? x.Dt : 1f / 120f;
        Follow(ref _bank, ref _bankV, bT, BankHalfLife, dt);
        Follow(ref _shLead, ref _shLeadV, sT, LeadHalfLife, dt);
        Follow(ref _hdLead, ref _hdLeadV, hT, LeadHalfLife, dt);
        Follow(ref _look, ref _lookV, lT, LeadHalfLife, dt);
        Follow(ref _free, ref _freeV, free, LeadHalfLife, dt);
    }

    /// <summary>A critically damped approach of <paramref name="x"/> (velocity <paramref name="v"/>) to <paramref name="target"/>, exact for any step.</summary>
    public static void Follow(ref float x, ref float v, float target, float halfLife, float dt)
    {
        float off = x - target;
        Spring.Decay(ref off, ref v, halfLife, dt);
        x = target + off;
    }
}
