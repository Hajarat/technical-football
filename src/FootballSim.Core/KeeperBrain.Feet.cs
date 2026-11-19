using System.Numerics;

namespace FootballSim.Core;

// His penalty area: the hands only inside it, and outside it the feet. A loose ball outside the area he goes for with a slide
// tackle (or, when a slide does not fit, a standing poke with a foot), knocking it away; a hand save whose ball is outside the
// area at the contact is refused (he pulls his hands away and the ball carries on).
public sealed partial class KeeperBrain
{
    Vector3 _foot;              // the leading foot in a slide or a poke (the sphere the ball can meet)
    Vector2 _slideDir = new(-1f, 0f);
    bool _poke;                 // the current feet action is a standing poke, not a slide
    float _footEnd;             // how far from his feet at the start the foot goes, metres
    bool _handsOff;             // his hands met a ball outside the area in this save: they are pulled away for the rest of it
    bool _roundSlid;

    /// <summary>How far from the goal mouth he rushes and charges inside his area: out to its front line (its depth).</summary>
    float RushMaxFromGoal => _pitch.PenaltyAreaDepth;
    float ChargeMaxOut => _pitch.PenaltyAreaDepth;

    /// <summary>
    /// True when he may use his hands on a ball centred at <paramref name="ball"/>: any part of it is in his penalty area
    /// (<see cref="Pitch.InPenaltyArea"/>).
    /// </summary>
    bool HandsAllowedAt(Vector3 ball) => _pitch.InPenaltyArea(ball, _t.Ball.Radius);

    /// <summary>True for a save kind played with the hands; a foot block, a reflex foot and a body block are not.</summary>
    static bool IsHandsKind(KeeperDive d) => d is not (KeeperDive.Feet or KeeperDive.ReflexFoot or KeeperDive.BodyBlock);

    /// <summary>
    /// Whether a contact at <paramref name="contact"/> (ball centre) is refused because it would be a touch with the hands with the
    /// ball outside his penalty area: his hands (reacting, or a hand save) rather than his body, a foot or a smother's spread
    /// body. A refused contact pulls his hands away for the rest of the save (<see cref="HandsSphere"/> and
    /// <see cref="HandsCapsule"/> go quiet; his body stays in the way) and the ball carries on untouched.
    /// </summary>
    internal bool RefuseHands(Vector3 contact, bool body)
    {
        if (body) return false;
        bool hands = _phase == Phase.Reacting || _phase == Phase.Diving && IsHandsKind(_dive);
        if (!hands || HandsAllowedAt(contact)) return false;
        _handsOff = true;
        return true;
    }

    /// <summary>
    /// Starts a slide tackle (or, with <paramref name="poke"/>, a standing poke) at <paramref name="point"/>, a ball outside his
    /// area: the leading foot goes along the ground from <c>slide_foot_start</c> ahead of his feet toward the point at
    /// <c>slide_speed</c>, out to <c>slide_reach</c> (a poke: at <c>poke_speed</c>, to the point but no further than
    /// <c>poke_reach</c>), the body following <c>slide_body_follow</c> of it (a poke: a fifth).
    /// </summary>
    void StartFeet(Vector3 point, bool poke)
    {
        var k = _t.Keeper;
        Enter(Phase.Slide);
        _active = true;
        _rushing = false;
        _anticipating = false;
        _fromBackpedal = false;
        _contact = false;
        _handsOff = false;
        _poke = poke;
        if (!_struck) { _roundSlid = true; Report.Slid = true; }
        _feet0 = _k.Position;
        var to = new Vector2(point.X - _k.Position.X, point.Z - _k.Position.Z);
        float d = to.Length();
        _slideDir = d > 1e-3f ? to / d : new Vector2(MathF.Cos(_k.Facing), MathF.Sin(_k.Facing));
        _k.Facing = MathF.Atan2(_slideDir.Y, _slideDir.X);
        _target = point;
        _footEnd = poke ? MathF.Max(k.SlideFootStart, MathF.Min(d, k.PokeReach)) : k.SlideReach;
        // the leading foot reaches the point (or the end of its travel) at its speed from where it starts
        PublishSave(point, MathF.Max(0f, MathF.Min(d, _footEnd) - k.SlideFootStart) / (poke ? k.PokeSpeed : k.SlideSpeed));
        if (poke) SetAction(KeeperAction.Diving, KeeperDive.Feet);
        else SetAction(KeeperAction.Slide);
        StepFeet(0f);
    }

    /// <summary>
    /// One tick of a slide or a poke. The foot stops where it met the ball. A slide ends on the ground (Grounded, then the usual
    /// get-up) once the foot has gone its whole way or after <c>slide_max_seconds</c>; a poke ends set again after
    /// <c>poke_seconds</c>.
    /// </summary>
    void StepFeet(float dt)
    {
        var k = _t.Keeper;
        float speed = _poke ? k.PokeSpeed : k.SlideSpeed;
        float s = _contact ? Vector2.Distance(new Vector2(_foot.X, _foot.Z), new Vector2(_feet0.X, _feet0.Z))
            : MathF.Min(_footEnd, k.SlideFootStart + speed * _phaseTime);
        float follow = _poke ? 0.2f * k.SlideBodyFollow : k.SlideBodyFollow;
        float bodyAlong = follow * MathF.Max(0f, s - k.SlideFootStart);
        float footY = _poke ? Math.Clamp(_target.Y, _t.Ball.Radius, k.ClaimMaxHeight) : _t.Ball.Radius;
        if (!_contact) _foot = new Vector3(_feet0.X + _slideDir.X * s, footY, _feet0.Z + _slideDir.Y * s);
        var old = _k.Position;
        var pos = new Vector3(_feet0.X + _slideDir.X * bodyAlong, 0f, _feet0.Z + _slideDir.Y * bodyAlong);
        var vel = Vector3.Zero;
        _pitch.ClampPlayer(ref pos, ref vel);
        _k.Position = pos;
        if (dt > 0f) _k.Velocity = (pos - old) / dt;
        // The hands stay with the body: low beside the hips in a slide, down at the foot in a poke (the view's foot block).
        _hands = _poke ? _foot : new Vector3(pos.X, 0.3f, pos.Z);
        if (dt <= 0f) return;
        bool done = _poke ? _phaseTime >= k.PokeSeconds
            : _phaseTime >= k.SlideMaxSeconds || s >= _footEnd - 1e-4f && _phaseTime >= (_footEnd - k.SlideFootStart) / speed + 0.1f;
        if (!done) return;
        _active = false;
        if (_poke) ToStand();
        else
        {
            _k.Velocity = Vector3.Zero;
            Enter(Phase.Grounded);
            SetAction(KeeperAction.Grounded);
        }
    }

    /// <summary>
    /// The ball met his foot in a slide or a poke: never held. It is knocked on along the slide's direction, within
    /// <c>slide_knock_angle_deg</c> either side, at <c>slide_knock_speed_min</c>..<c>_max</c>, lifted by <c>slide_knock_lift</c>.
    /// </summary>
    bool FeetContact(Ball ball, Vector3 contact, SimRng rng, out float speedAtContact)
    {
        var k = _t.Keeper;
        speedAtContact = ball.Velocity.Length();
        _active = false;
        _contact = true;
        _foot = contact;
        if (!_struck) { _roundWon = true; Report.WonBall = true; }
        Record(contact, _feet0, KeeperResult.Parried);
        Report.Save = _poke ? SaveKind.Block : SaveKind.Slide;
        float yaw = MathF.Atan2(_slideDir.Y, _slideDir.X) + rng.Uniform(-k.SlideKnockAngleDeg, k.SlideKnockAngleDeg) * MathF.PI / 180f;
        float speed = rng.Uniform(k.SlideKnockSpeedMin, k.SlideKnockSpeedMax);
        var dir = Vector3.Normalize(new Vector3(MathF.Cos(yaw), k.SlideKnockLift, MathF.Sin(yaw)));
        ball.Velocity = dir * speed;
        ball.Spin = Vector3.Zero;
        return false;
    }
}
