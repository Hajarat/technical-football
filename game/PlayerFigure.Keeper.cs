using System;
using Godot;
using KAct = FootballSim.Core.KeeperAction;
using KDive = FootballSim.Core.KeeperDive;

namespace FootballSim.Game;

/// <summary>
/// The keeper's poses (Phase D). Every state is a blend of up to three poses: the standing pose S (Set, Shuffle, Planted, Reacting, Holding on his feet,
/// and the end of a get-up), the dive pose D (every dive kind, the smother, lying on the ground and the start of a get-up) and, while rushing, the
/// outfield run cycle R. The weights move smoothly, so a change of state never snaps a pose. The simulation's values decide what is drawn; nothing
/// here decides any timing except the figure's own smoothing.
/// </summary>
public partial class PlayerFigure
{
    /// <summary>A whole-body pose as joint positions: knees and ankles follow from the pelvis, the hands are targets for the arm IK.</summary>
    struct KPose
    {
        public Vector3 Pel; public Quaternion Q;
        public Vector3[] Knee, Ank, Dir, Hand, Pole;
    }

    sealed class KeeperFoot { public Vector3 Plant, Ankle, From; public bool Stepping; public float T, Dur; }

    // blend weights: toward the dive pose, toward the outfield run cycle
    float _wD, _wR;
    bool _kInit, _ground, _hasLatch;
    KDive _dKind = KDive.Low;
    bool _dSmother;
    Vector3 _latchHand;
    // smoothed standing parameters
    float _kLegFrac, _kHw, _kLean, _kHf, _kHd, _kHs;
    readonly KeeperFoot[] _kf = { new(), new() };
    // smoothed dive state: tilt from upright (radians), horizontal direction of the tilt, how far the pelvis travels toward it, pelvis height, hands and ankles
    float _dTilt, _dOff, _dPelY;
    Vector3 _dDir = new(1, 0, 0);
    // the direction the body tilts toward and the way its front faces while it is tilted (they leave the dive direction when he rolls or rises)
    Vector3 _dTiltDir = new(1, 0, 0), _dFace = new(1, 0, 0);
    float _smSide;   // which way his front faces while he lies after a smother (+1 / -1 along the horizontal normal of the dive direction), 0 = not chosen
    bool _wasSmother;
    readonly Vector3[] _dHand = new Vector3[2], _dAnk = new Vector3[2];
    KPose _rushSnap;   // the last run-cycle pose, relative to the keeper's feet position (XZ), blended out when the rush ends

    const float Deg = Mathf.Pi / 180f;
    static readonly float[] GetUpG = { 0f, 0.25f, 0.6f, 1f };
    static readonly float[] GetUpTilt = { 86f, 64f, 32f, 8f };

    static void StandTargets(KAct act, out float legFrac, out float hw, out float lean, out float hf, out float hd, out float hs)
    {
        switch (act)
        {
            case KAct.Shuffle: legFrac = 0.82f; hw = 2.5f; lean = 0.20f; hf = 0.29f; hd = -0.10f; hs = 0.11f; break;
            case KAct.Planted: legFrac = 0.78f; hw = 3.0f; lean = 0.27f; hf = 0.33f; hd = -0.08f; hs = 0.15f; break;
            case KAct.Reacting: legFrac = 0.73f; hw = 3.2f; lean = 0.35f; hf = 0.34f; hd = -0.05f; hs = 0.17f; break;
            case KAct.Holding: legFrac = 0.94f; hw = 2.4f; lean = 0.05f; hf = 0.30f; hd = -0.12f; hs = 0.05f; break;
            default: legFrac = 0.82f; hw = 2.5f; lean = 0.15f; hf = 0.25f; hd = -0.20f; hs = 0.10f; break;
        }
    }

    void KeeperInit(in FigureFrame f, Vector3 pos, Vector3 fwd, Vector3 right, float k)
    {
        _kInit = true;
        StandTargets(f.KeeperAction, out _kLegFrac, out _kHw, out _kLean, out _kHf, out _kHd, out _kHs);
        var act = f.KeeperAction;
        _wD = act is KAct.Diving or KAct.Smother or KAct.Grounded ? 1f : act == KAct.GettingUp ? GetUpWeight(Mathf.Clamp(f.KeeperActionTime / KeeperView.GetUpSeconds, 0f, 1f)) : 0f;
        _wR = act == KAct.Rush ? 1f : 0f;
        _ground = act is KAct.Grounded or KAct.GettingUp;
        _dKind = f.KeeperDive == KDive.None ? KDive.Low : f.KeeperDive;
        _dSmother = act == KAct.Smother;
        _hasLatch = false;
        _dDir = fwd; _dTiltDir = fwd; _dFace = fwd; _smSide = 0f; _wasSmother = false;
        float hwf = HipW * _kHw;
        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? -1f : 1f;
            var ft = _kf[i];
            ft.Stepping = false; ft.Plant = pos + right * side * hwf; ft.Plant.Y = AnkleH; ft.Ankle = ft.Plant;
        }
        _dTilt = 0.1f; _dOff = 0f; _dPelY = AnkleH + Leg * _kLegFrac;
        _dHand[0] = _dHand[1] = pos + new Vector3(0, 1f, 0);
        _dAnk[0] = _kf[0].Plant; _dAnk[1] = _kf[1].Plant;
    }

    /// <summary>Draws the keeper for this tick. <paramref name="run"/> is the run-cycle pose when the state is Rush (the outfield code computed it), else null.</summary>
    void UpdateKeeper(in FigureFrame f, Vector3 pos, Vector3 fwd, Vector3 right, Vector3 hv, float speed, float dt, KPose? run)
    {
        float k = H / 1.8f;
        _strikeT = -1f;
        if (!_kInit) KeeperInit(f, pos, fwd, right, k);
        var act = f.KeeperAction;
        float getUp = Mathf.Clamp(f.KeeperActionTime / KeeperView.GetUpSeconds, 0f, 1f);
        KeeperMode(act, f.KeeperDive, getUp, dt);

        var S = KeeperStand(f, pos, fwd, right, hv, speed, dt, k);
        if (_wD < 0.02f)
        {
            // not in a dive: the dive state follows the standing pose, so the next dive starts from the stance
            _dTilt = 0.1f; _dOff = 0f; _dPelY = S.Pel.Y; _dDir = fwd; _dTiltDir = fwd; _dFace = fwd; _smSide = 0f; _hasLatch = _hasLatch && _ground;
            for (int i = 0; i < 2; i++) { _dHand[i] = S.Hand[i]; _dAnk[i] = S.Ank[i]; }
        }
        var D = KeeperDiveShape(f, pos, fwd, right, S, dt, k);
        KPose C = _wD > 0.001f ? Blend(S, D, _wD) : S;

        if (run is KPose r)
        {
            var rel = new Vector3(-pos.X, 0, -pos.Z);
            _rushSnap = Shift(r, rel);
            C = _wR >= 1f ? r : Blend(C, r, Smooth(_wR));
        }
        else if (_wR > 0f)
            C = Blend(C, Shift(_rushSnap, new Vector3(pos.X, 0, pos.Z)), Smooth(_wR));

        Vector3 shoulderC = C.Pel + new Basis(C.Q) * new Vector3(0, 0.035f * H + TorsoLen - 0.03f * H, 0);
        RenderBody(C.Pel, new Basis(C.Q), shoulderC, C.Knee, C.Ank, C.Dir, C.Hand, C.Pole);
        if (run == null)
        {
            _moving = false;
            for (int i = 0; i < 2; i++)
            {
                var ft = _feet[i];
                ft.Planted = false; ft.Timed = false; ft.InStance = false; ft.Released = false; ft.Blend = 0f;
                ft.BlendFrom = C.Ank[i]; ft.Ankle = C.Ank[i]; ft.Plant = new Vector3(C.Ank[i].X, AnkleH, C.Ank[i].Z);
            }
            TrackSlide(new[] { false, false }, C.Ank);
            _lastPos = pos;
        }
    }

    /// <summary>Latches whether he is on the ground and moves the weight toward the dive pose.</summary>
    void KeeperMode(KAct act, KDive dive, float getUp, float dt)
    {
        if (act is KAct.Diving or KAct.Smother)
        {
            _dSmother = act == KAct.Smother;
            _dKind = act == KAct.Smother ? KDive.Low : dive == KDive.None ? KDive.Side : dive;
            _ground = _dKind is KDive.Low or KDive.Side or KDive.High;
        }
        else if (act == KAct.Grounded) _ground = true;
        else if (act is KAct.Set or KAct.Shuffle or KAct.Planted or KAct.Reacting or KAct.Rush) _ground = false;
        else if (act == KAct.GettingUp && getUp >= 1f) _ground = false;

        float target, tau;
        switch (act)
        {
            case KAct.Diving: case KAct.Smother: target = 1f; tau = 0.05f; break;
            case KAct.Grounded: target = 1f; tau = 0.08f; break;
            case KAct.Holding: target = _ground ? 1f : 0f; tau = _ground ? 0.10f : 0.15f; break;
            default: target = 0f; tau = 0.12f; break;
        }
        if (act == KAct.GettingUp) _wD = Math.Min(_wD, GetUpWeight(getUp));
        else _wD += (target - _wD) * (1f - Mathf.Exp(-dt / tau));
    }

    /// <summary>How much of the dive (ground) pose is left at this point of the get-up: all of it while he rolls onto his front and comes up onto his knees, then it hands over to the stance.</summary>
    static float GetUpWeight(float g) => 1f - Smooth(Mathf.Clamp((g - 0.6f) / 0.4f, 0f, 1f));

    /// <summary>Piecewise-linear keyframes (the filter on the tilt rounds the corners): g in 0..1 and the value.</summary>
    static float Keys(float g, float[] gs, float[] vs)
    {
        if (g <= gs[0]) return vs[0];
        for (int i = 1; i < gs.Length; i++)
            if (g <= gs[i]) return Mathf.Lerp(vs[i - 1], vs[i], (g - gs[i - 1]) / (gs[i] - gs[i - 1]));
        return vs[^1];
    }

    // ---------------------------------------------------------------------------------------------------------------
    // standing

    KPose KeeperStand(in FigureFrame f, Vector3 pos, Vector3 fwd, Vector3 right, Vector3 hv, float speed, float dt, float k)
    {
        StandTargets(f.KeeperAction, out float legFrac, out float hw, out float lean, out float hf, out float hd, out float hs);
        float a = 1f - Mathf.Exp(-dt / 0.07f);
        _kLegFrac += (legFrac - _kLegFrac) * a; _kHw += (hw - _kHw) * a; _kLean += (lean - _kLean) * a;
        _kHf += (hf - _kHf) * a; _kHd += (hd - _kHd) * a; _kHs += (hs - _kHs) * a;

        float pelY = AnkleH + Leg * _kLegFrac;
        float hgt = pelY - AnkleH;
        float reach = 0.985f * Leg;
        float hmax = Mathf.Sqrt(Math.Max(0.01f, reach * reach - hgt * hgt));
        float hwf = Math.Min(HipW * _kHw, HipW + 0.85f * hmax);
        Vector3 pel = new(pos.X, pelY, pos.Z);

        // feet: each stays where it is planted and takes a short step to its spot under the hips when the body has moved away; a shuffle leads with
        // the foot on the side he is going and the feet stay on their own sides, so they never cross
        Vector3 lead = hv * 0.10f;
        if (lead.Length() > 0.35f) lead = lead.Normalized() * 0.35f;
        var tgt = new Vector3[2]; var err = new float[2];
        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? -1f : 1f;
            tgt[i] = pel + right * side * hwf + lead; tgt[i].Y = AnkleH;
            err[i] = _kf[i].Stepping ? 0f : Dist2(_kf[i].Plant, tgt[i]);
        }
        float thresh = (speed > 0.4f ? 0.10f : 0.16f) * k;
        float stepDur = Mathf.Clamp(0.30f - 0.035f * speed, 0.12f, 0.26f);
        for (int i = 0; i < 2; i++)
        {
            var ft = _kf[i]; var other = _kf[1 - i];
            if (ft.Stepping)
            {
                ft.T += dt;
                float s = Mathf.Clamp(ft.T / ft.Dur, 0f, 1f);
                ft.Ankle = ft.From.Lerp(tgt[i], Smooth(s));
                ft.Ankle.Y = AnkleH + (0.03f + 0.012f * Math.Min(speed, 4f)) * k * Mathf.Sin(Mathf.Pi * s);
                if (s >= 1f) { ft.Stepping = false; ft.Plant = tgt[i]; ft.Ankle = ft.Plant; }
                continue;
            }
            ft.Ankle = ft.Plant;
            if (err[i] > 0.9f) { ft.Plant = tgt[i]; ft.Ankle = ft.Plant; continue; }   // a jump of the whole keeper: no step to draw
            bool forced = err[i] > 0.40f * k;   // about to be out of reach: step now whatever the other foot does
            bool turn = !other.Stepping && (err[i] >= err[1 - i] || err[1 - i] <= thresh);
            if (err[i] > thresh && (forced || turn))
            {
                ft.Stepping = true; ft.T = 0; ft.Dur = stepDur; ft.From = ft.Plant;
            }
        }
        var ank = new Vector3[2]; var dir = new Vector3[2];
        for (int i = 0; i < 2; i++) { ank[i] = _kf[i].Ankle; dir[i] = fwd; }
        // never let the feet cross: the right foot stays at least a hip width to the right of the left
        float gap = (ank[1] - ank[0]).Dot(right);
        float minGap = 1.2f * HipW;
        if (gap < minGap)
        {
            Vector3 push = right * (0.5f * (minGap - gap));
            ank[0] -= push; ank[1] += push;
        }

        Basis B = new Basis(right, -_kLean) * new Basis(fwd, Vector3.Up, right);
        Vector3 shoulderC = pel + B * new Vector3(0, 0.035f * H + TorsoLen - 0.03f * H, 0);
        var knee = new Vector3[2]; var hand = new Vector3[2]; var pole = new Vector3[2];
        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? -1f : 1f;
            Vector3 hip = pel + B * new Vector3(0, -0.01f * H, side * HipW);
            SolveLeg(hip, ank[i], fwd, out knee[i], out ank[i]);
            Vector3 sh = shoulderC + B * new Vector3(0, 0, side * ShoulderW);
            hand[i] = f.KeeperAction == KAct.Holding
                ? f.HandPosition + right * side * 0.07f
                : sh + B * new Vector3(_kHf * H, _kHd * H, side * _kHs * H);
            pole[i] = (B * new Vector3(-1f, -0.2f, side * 0.5f)).Normalized();
        }
        return new KPose { Pel = pel, Q = B.GetRotationQuaternion(), Knee = knee, Ank = ank, Dir = dir, Hand = hand, Pole = pole };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // diving, smothering, lying

    /// <summary>
    /// The save or the ground pose. The body tilts from upright toward the hands (the amount limited per kind), the pelvis travels toward them when they
    /// are beyond what the arms reach, and the legs trail. Low and Smother go flat along the ground; Side is flat at mid height; High leaps with the arms up;
    /// Feet is a deep standing split with the body forward; Catch is a standing catch at the body. Grounded and GettingUp use the last dive's direction.
    /// </summary>
    KPose KeeperDiveShape(in FigureFrame f, Vector3 pos, Vector3 fwd, Vector3 right, in KPose S, float dt, float k)
    {
        var act = f.KeeperAction;
        bool diving = act is KAct.Diving or KAct.Smother;
        bool lie = act is KAct.Grounded or KAct.GettingUp || (act == KAct.Holding && _ground);
        bool holdBall = act == KAct.Holding;
        bool getting = act == KAct.GettingUp;
        float g = getting ? Mathf.Clamp(f.KeeperActionTime / KeeperView.GetUpSeconds, 0f, 1f) : 0f;
        if (act == KAct.Smother && !_wasSmother) _smSide = 0f;
        _wasSmother = act == KAct.Smother;
        Vector3 hand = f.HandPosition;
        if (diving || holdBall) { _latchHand = hand; _hasLatch = true; }
        else if (_hasLatch) hand = _latchHand;
        KDive kind = lie ? KDive.Low : _dKind;
        bool standing = !lie && kind is KDive.Feet or KDive.Catch;
        if (lie && !holdBall) hand.Y = Math.Min(hand.Y, 0.25f * k);

        float minT, maxT, maxOff;
        switch (kind)
        {
            case KDive.Side: minT = 55f * Deg; maxT = 80f * Deg; maxOff = 2.4f; break;
            case KDive.High: minT = 20f * Deg; maxT = 62f * Deg; maxOff = 1.8f; break;
            case KDive.Feet: minT = 10f * Deg; maxT = 24f * Deg; maxOff = 0.3f; break;
            case KDive.Catch: minT = 6f * Deg; maxT = 16f * Deg; maxOff = 0.3f; break;
            default: minT = 70f * Deg; maxT = 86f * Deg; maxOff = 2.4f; break;
        }
        Vector3 chest = pos + new Vector3(0, 0.5f * H, 0);
        Vector3 d = hand - chest;
        Vector3 horiz = new(d.X, 0, d.Z);
        float hl = horiz.Length();
        Vector3 dirT = hl > 0.08f ? horiz / hl : _dDir;
        float tiltT = lie ? 86f * Deg : Mathf.Clamp(Mathf.Atan2(hl, d.Y), minT, maxT);
        // getting up: he rolls from lying onto his front, pushes up onto his knees and rises with the chest forward, so the tilt turns toward his
        // own facing (never sideways) while it eases from flat to the stance's lean
        if (getting) tiltT = Keys(g, GetUpG, GetUpTilt) * Deg;

        float tau = diving ? 0.05f : getting ? 0.06f : 0.10f;
        float a = 1f - Mathf.Exp(-dt / tau);
        _dTilt += (tiltT - _dTilt) * a;
        Vector3 nd = _dDir + (dirT - _dDir) * a;
        _dDir = nd.LengthSquared() > 1e-6f ? nd.Normalized() : dirT;
        // after a smother he lies on his side: the front faces along the ground, at right angles to the way he went, so the hands meet the ball from
        // above and below and his body stays behind it
        Vector3 tiltDirT = getting ? fwd : dirT, faceT = fwd;
        if (_dSmother && !getting && (diving || lie))
        {
            if (_smSide == 0f)
            {
                var pT = new Vector3(-dirT.Z, 0, dirT.X);
                float dd = pT.Dot(fwd);
                _smSide = Mathf.Abs(dd) > 0.15f ? Mathf.Sign(dd) : pT.Dot(right) >= 0f ? 1f : -1f;
            }
            faceT = new Vector3(-_dDir.Z, 0, _dDir.X) * _smSide;
        }
        float aR = 1f - Mathf.Exp(-dt / (getting ? 0.11f : tau));
        Vector3 nt = _dTiltDir + (tiltDirT - _dTiltDir) * aR;
        _dTiltDir = nt.LengthSquared() > 1e-6f ? nt.Normalized() : tiltDirT;
        Vector3 nf = _dFace + (faceT - _dFace) * aR;
        _dFace = nf.LengthSquared() > 1e-6f ? nf.Normalized() : faceT;
        Vector3 faceRight = new(-_dFace.Z, 0, _dFace.X);

        float shoulderUp = 0.035f * H + TorsoLen - 0.03f * H;
        float armLen = (ArmUp + ArmLow) * 0.92f;
        float reachH = shoulderUp * Mathf.Sin(_dTilt) + armLen * 0.85f;
        float offT = Mathf.Clamp(hl - reachH, 0f, maxOff);
        if (getting) offT *= 1f - Smooth(g / 0.75f);   // the body comes back to where he stands as he rises
        _dOff += (offT - _dOff) * a;

        // pelvis height from the tilt and the kind
        float sin = Mathf.Sin(_dTilt), cos = Mathf.Cos(_dTilt);
        float lift = Mathf.Clamp(_dTilt / 0.6f, 0f, 1f);
        float pelT;
        switch (kind)
        {
            case KDive.Side: pelT = 0.50f * H * cos + k * (0.07f * lift + 0.28f * sin); break;
            case KDive.High: pelT = 0.42f * H * cos + k * (0.30f * lift + 0.22f * sin); break;
            case KDive.Feet: pelT = AnkleH + Leg * 0.52f; break;
            case KDive.Catch: pelT = AnkleH + Leg * 0.80f; break;
            default: pelT = 0.50f * H * cos + k * (0.05f * lift + 0.10f * sin); break;
        }
        if (getting) pelT = Keys(g, GetUpG, new[] { 0.22f * k, 0.36f * k, 0.60f * k, S.Pel.Y });
        _dPelY += (pelT - _dPelY) * (1f - Mathf.Exp(-dt / 0.04f));

        Vector3 axis = Vector3.Up.Cross(_dTiltDir * sin + Vector3.Up * cos);
        Quaternion q = axis.LengthSquared() > 1e-8f ? new Quaternion(axis.Normalized(), _dTilt) : Quaternion.Identity;
        Basis B = new Basis(q) * new Basis(_dFace, Vector3.Up, faceRight);
        float pelY = _dPelY;
        if (!standing)
        {
            // no joint below the ground: the pelvis is at least as high as the hips, shoulders and head need
            float lowest = 0f;
            for (int i = 0; i < 2; i++)
            {
                float side = i == 0 ? -1f : 1f;
                lowest = Math.Min(lowest, (B * new Vector3(0, -0.01f * H, side * HipW)).Y);
                lowest = Math.Min(lowest, (B * new Vector3(0, shoulderUp, side * ShoulderW)).Y);
            }
            lowest = Math.Min(lowest, (B * new Vector3(0, shoulderUp + 0.14f * H, 0)).Y);
            pelY = Math.Max(pelY, 0.07f * k - lowest);
        }
        Vector3 pel = new(pos.X + _dDir.X * _dOff, pelY, pos.Z + _dDir.Z * _dOff);
        Vector3 bodyUp = B.Y, bodyRight = B.Z;

        var knee = new Vector3[2]; var ank = new Vector3[2]; var dir = new Vector3[2]; var handW = new Vector3[2]; var pole = new Vector3[2];
        float aH = 1f - Mathf.Exp(-dt / (diving || holdBall ? 0.04f : getting ? 0.08f : 0.12f));
        float aA = 1f - Mathf.Exp(-dt / 0.05f);
        float kneeFwd = Smooth((g - 0.05f) / 0.40f);
        float flatLeg = Smooth((_dTilt - 0.5f) / 0.8f);   // flat on the ground the knees bend up, never into the ground
        for (int i = 0; i < 2; i++)
        {
            float side = i == 0 ? -1f : 1f;
            Vector3 hip = pel + B * new Vector3(0, -0.01f * H, side * HipW);
            Vector3 at;
            if (kind == KDive.Catch && !lie) at = S.Ank[i];
            else if (kind == KDive.Feet && !lie)
            {
                at = new Vector3(pos.X, AnkleH, pos.Z) + right * side * (0.60f * k) + fwd * (0.15f * k);
                dir[i] = new Basis(Vector3.Up, -side * 0.45f) * fwd;
            }
            else
            {
                float spread = _dSmother && !lie ? 0.18f : 0.10f;
                float scissor = _dSmother && !lie ? side * 0.12f : -0.05f;
                at = hip - bodyUp * (Leg * 0.93f) + bodyRight * side * spread * H + B.X * scissor * H;
                at.Y = Math.Max(at.Y, AnkleH);
            }
            if (dir[i] == Vector3.Zero) dir[i] = fwd;
            _dAnk[i] += (at - _dAnk[i]) * aA;
            // getting up the knees bend up while he lies, then forward and down onto the ground as he comes onto them (a pole that follows the body's front
            // would run along the leg when the torso is half up and flip the knee)
            Vector3 kneePole = getting
                ? (Vector3.Up * (1.2f * (1f - kneeFwd)) + fwd * kneeFwd).Normalized()
                : (B.X * (1f - flatLeg) + Vector3.Up * (1.2f * flatLeg)).Normalized();
            SolveLeg(hip, _dAnk[i], kneePole, out knee[i], out ank[i]);
            Vector3 ht = hand + bodyRight * side * 0.07f * H;
            if (getting)
            {
                // hands on the ground under the shoulders (a little ahead), coming up to the thighs as he reaches his knees
                Vector3 shW = pel + B * new Vector3(0, shoulderUp, side * ShoulderW) + _dTiltDir * (0.08f * H);
                ht = new Vector3(shW.X, 0.06f * k + 0.55f * k * Smooth((g - 0.35f) / 0.35f), shW.Z);
            }
            ht.Y = Math.Max(ht.Y, 0.06f * k);
            _dHand[i] += (ht - _dHand[i]) * aH;
            handW[i] = _dHand[i];
            // flat on the ground the elbows point up, so no joint goes below the ground
            Vector3 pb = (B * new Vector3(-0.3f, -1f, side * 0.6f)).Normalized();
            float flat = Smooth((_dTilt - 0.5f) / 0.8f);
            pole[i] = (pb * (1f - flat) + Vector3.Up * (1.2f * flat)).Normalized();
        }
        return new KPose { Pel = pel, Q = B.GetRotationQuaternion(), Knee = knee, Ank = ank, Dir = dir, Hand = handW, Pole = pole };
    }

    // ---------------------------------------------------------------------------------------------------------------

    static KPose Blend(in KPose a, in KPose b, float w)
    {
        var r = new KPose
        {
            Pel = a.Pel.Lerp(b.Pel, w), Q = a.Q.Slerp(b.Q, w),
            Knee = new Vector3[2], Ank = new Vector3[2], Dir = new Vector3[2], Hand = new Vector3[2], Pole = new Vector3[2],
        };
        for (int i = 0; i < 2; i++)
        {
            r.Knee[i] = a.Knee[i].Lerp(b.Knee[i], w);
            r.Ank[i] = a.Ank[i].Lerp(b.Ank[i], w);
            r.Hand[i] = a.Hand[i].Lerp(b.Hand[i], w);
            Vector3 dv = a.Dir[i].Lerp(b.Dir[i], w);
            r.Dir[i] = dv.LengthSquared() > 1e-6f ? dv.Normalized() : b.Dir[i];
            Vector3 pv = a.Pole[i].Lerp(b.Pole[i], w);
            r.Pole[i] = pv.LengthSquared() > 1e-6f ? pv.Normalized() : b.Pole[i];
        }
        return r;
    }

    /// <summary>The pose moved by <paramref name="by"/> (positions only).</summary>
    static KPose Shift(in KPose p, Vector3 by)
    {
        var r = new KPose
        {
            Pel = p.Pel + by, Q = p.Q, Dir = (Vector3[])p.Dir.Clone(), Pole = (Vector3[])p.Pole.Clone(),
            Knee = new Vector3[2], Ank = new Vector3[2], Hand = new Vector3[2],
        };
        for (int i = 0; i < 2; i++) { r.Knee[i] = p.Knee[i] + by; r.Ank[i] = p.Ank[i] + by; r.Hand[i] = p.Hand[i] + by; }
        return r;
    }
}
