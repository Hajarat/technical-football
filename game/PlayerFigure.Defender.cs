using System;
using Godot;
using DAct = FootballSim.Core.DefenderAction;

namespace FootballSim.Game;

/// <summary>
/// What the core publishes of an outfield player's defending, for his figure: the action and the seconds it has run, the side
/// it goes to (-1 his left, +1 his right, 0 none), the leading foot of a lunge or slide, his leg reach while set for a shot,
/// and whether he jockeys. An attacker's frame carries it too while he stumbles. Not valid on a frame that has none.
/// </summary>
public struct FigureDefender
{
    public bool Valid;
    public DAct Action;
    public float ActionTime;
    public float Side;
    public Vector3 TackleFoot;
    /// <summary>Seconds after a lunge began at which its foot meets the ball (NaN when none is planned).</summary>
    public float ContactTime;
    public float BlockReach;
    public bool Jockeying;
    /// <summary>Seconds the getting-up lasts, and a slide's grounded time left is not needed: the pose only reads the action time.</summary>
    public float GetUpSeconds;
}

// The defending poses, laid over the outfield figure's own running body: the jockey's crouch, the lunge, the slide, lying and
// getting up, the stumble, the barge's lean and the block. The legs keep the stride clock's feet except where a pose places a
// foot itself. Each pose is a pelvis height, a trunk tilt and, where needed, ankle and hand targets, eased in and out by the
// action's weight; the core decides every timing (the action, its seconds, the foot).
public partial class PlayerFigure
{
    float _dfW;                 // how much of the pose shows, 0..1
    DAct _dfAct = DAct.Hold;    // the pose being shown (kept while it eases out)
    float _dfSide, _dfTime;
    Vector3 _dfFoot;
    float _dfGetUp = 0.5f, _dfBlock;
    float _dfWobble;

    /// <summary>The weight of the defending pose now (0 when none shows): the mocap trunk gives way to it by this much.</summary>
    float DefenderPoseWeight => _dfW;

    static bool IsPose(DAct a) => a is DAct.Jockey or DAct.Lunge or DAct.Slide or DAct.Grounded or DAct.GettingUp or DAct.Stumble or DAct.Barge or DAct.Block;

    /// <summary>
    /// Lays the defending pose over the body about to be rendered: lowers and shifts the pelvis, tilts the trunk (and with it the
    /// shoulders, head and hands), places the feet a pose owns, and solves the knees again.
    /// </summary>
    void DefenderOverlay(in FigureFrame f, Vector3 pos, Vector3 fwd, Vector3 right, float dt, ref Vector3 pel, ref Basis B, ref Vector3 shoulderC,
        Vector3[] knee, Vector3[] ankle, Vector3[] dir, Vector3[] handT, Vector3[] elbowPole)
    {
        var d = f.Defender;
        bool on = d.Valid && IsPose(d.Action);
        if (on)
        {
            // (lying to getting up is one motion: the weight stays, the pose itself rises)
            if (d.Action != _dfAct && !(_dfAct == DAct.Grounded && d.Action == DAct.GettingUp) && !(_dfAct == DAct.Slide && d.Action == DAct.Grounded)) _dfW = Math.Min(_dfW, 0.35f);
            _dfAct = d.Action; _dfSide = d.Side; _dfTime = d.ActionTime; _dfFoot = d.TackleFoot;
            _dfGetUp = d.GetUpSeconds > 0.05f ? d.GetUpSeconds : 0.5f; _dfBlock = d.BlockReach;
        }
        else _dfTime += dt;
        float rate = on ? (_dfAct is DAct.Lunge or DAct.Slide or DAct.Stumble ? 0.07f : 0.14f) : 0.22f;
        _dfW = _teleporting ? (on ? 1f : 0f) : Mathf.MoveToward(_dfW, on ? 1f : 0f, dt / rate);
        if (_dfW <= 0f) return;
        float w = Smooth(_dfW);
        float k = H / 1.8f;
        int foot = _dfSide < 0f ? 0 : 1;          // the leg a sided pose uses (0 = left)
        float side = foot == 0 ? -1f : 1f;

        // the pose: pelvis height (a share of standing), pelvis shift, trunk pitch (forward positive) and roll (to his right positive)
        float height = 1f, pitch = 0f, roll = 0f, handsOut = 0f, handsDown = 0f;
        Vector3 shift = Vector3.Zero;
        bool ownFeet = false, lying = false;
        float up = 0f;   // getting up: 0 lying, 1 standing
        switch (_dfAct)
        {
            case DAct.Jockey:
                height = 0.88f; pitch = 14f; handsOut = 0.22f; handsDown = 0.08f; break;
            case DAct.Block:
                height = 0.86f; pitch = 6f; roll = -side * 8f; handsOut = 0.18f; handsDown = 0.25f; break;
            case DAct.Lunge:
                height = 0.74f; pitch = 16f; roll = side * 10f; handsOut = 0.30f; break;
            case DAct.Barge:
                height = 0.93f; roll = side * 20f; pitch = 8f; handsDown = 0.15f; break;
            case DAct.Stumble:
                _dfWobble += dt * 9f;
                height = 0.90f; pitch = 24f + 6f * Mathf.Sin(_dfWobble); roll = 9f * Mathf.Sin(_dfWobble * 0.7f); handsOut = 0.35f; break;
            case DAct.Slide:
                height = 0.30f; pitch = -52f; roll = side * 14f; handsDown = 0.45f; ownFeet = true; lying = true; break;
            case DAct.Grounded:
                height = 0.20f; pitch = -82f; roll = side * 10f; handsDown = 0.5f; ownFeet = true; lying = true; break;
            case DAct.GettingUp:
                up = Smooth(_dfTime / _dfGetUp);
                height = Mathf.Lerp(0.20f, 1f, up); pitch = Mathf.Lerp(-82f, 12f, up); roll = side * 10f * (1f - up); handsDown = 0.5f * (1f - up);
                ownFeet = up < 0.6f; lying = up < 0.6f; break;
        }

        Vector3 pel0 = pel;
        float standY = StandY;
        Vector3 target = new(pel.X, Mathf.Min(pel.Y, standY * height), pel.Z);
        if (_dfAct == DAct.Lunge)
        {
            // the weight goes over the leading foot
            Vector3 to = new(_dfFoot.X - pos.X, 0, _dfFoot.Z - pos.Z);
            target += to * 0.32f;
        }
        else if (_dfAct == DAct.Block) target += right * side * (0.25f * Math.Max(0f, _dfBlock - 0.3f));
        else if (lying) target -= fwd * (0.25f * k * (1f - up));
        pel = pel0.Lerp(target, w);

        // the trunk: pitched about his right, rolled about his forward
        Basis R = new Basis(fwd, Mathf.DegToRad(roll) * w) * new Basis(right, -Mathf.DegToRad(pitch) * w);
        Vector3 pelNow = pel;
        Vector3 Turn(Vector3 p) => pelNow + R * (p - pel0);
        shoulderC = Turn(shoulderC);
        B = R * B;
        if (_split) { _headB = R * _headB; _pelB = new Basis(fwd, Mathf.DegToRad(roll) * w * 0.5f) * new Basis(right, -Mathf.DegToRad(pitch) * w * 0.5f) * _pelB; }
        for (int i = 0; i < 2; i++)
        {
            float s = i == 0 ? -1f : 1f;
            handT[i] = Turn(handT[i]) + (right * s * handsOut + Vector3.Down * handsDown) * (k * w);
            if (lying) handT[i] = handT[i].Lerp(new Vector3(pel.X, 0.08f * k, pel.Z) + right * s * 0.34f * k - fwd * 0.25f * k, w * (1f - up));
            elbowPole[i] = R * elbowPole[i];
        }

        // the feet a pose places itself
        if (_dfAct == DAct.Lunge)
        {
            Vector3 a = new(_dfFoot.X, AnkleH, _dfFoot.Z);
            ankle[foot] = ankle[foot].Lerp(a, w);
            Vector3 fd = new(_dfFoot.X - pos.X, 0, _dfFoot.Z - pos.Z);
            if (fd.LengthSquared() > 1e-4f) dir[foot] = dir[foot].Lerp(fd.Normalized(), w).Normalized();
        }
        else if (_dfAct == DAct.Block)
        {
            Vector3 a = pos + right * side * Math.Max(HipW, _dfBlock * 0.92f) + fwd * 0.10f * k; a.Y = AnkleH + 0.04f * k;
            ankle[foot] = ankle[foot].Lerp(a, w);
            dir[foot] = dir[foot].Lerp((fwd + right * side * 0.8f).Normalized(), w).Normalized();
        }
        else if (ownFeet)
        {
            // feet first along the ground: the leading leg out straight (to the core's foot while it slides), the other bent under
            float ws = w * (1f - Smooth(up / 0.6f));
            for (int i = 0; i < 2; i++)
            {
                float s = i == 0 ? -1f : 1f;
                bool lead = i == foot;
                Vector3 a = _dfAct == DAct.Slide && lead ? new Vector3(_dfFoot.X, 0, _dfFoot.Z)
                    : pos + fwd * (lead ? 0.80f : 0.42f) * k + right * s * (lead ? 0.10f : 0.24f) * k;
                a.Y = AnkleH + (lead ? 0.05f : 0.02f) * k;
                ankle[i] = ankle[i].Lerp(a, ws);
                dir[i] = dir[i].Lerp((fwd + Vector3.Up * 0.5f).Normalized(), ws).Normalized();
            }
        }

        // knees again, for the pelvis and feet as they are now
        Basis P = _split ? _pelB : B;
        for (int i = 0; i < 2; i++)
        {
            Vector3 hip = pel + P * new Vector3(0, 0, i == 0 ? -HipW : HipW);
            Vector3 pole = lying ? (fwd + Vector3.Up * 1.5f).Normalized() : fwd;
            SolveLeg(hip, ankle[i], pole, out knee[i], out ankle[i]);
        }
    }
}
