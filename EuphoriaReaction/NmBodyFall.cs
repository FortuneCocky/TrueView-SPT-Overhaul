using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// Going down, and which way. Watched every tick while the doll holds him: once he's really going over
/// (trunk tipping well past a stumble, his weight dropping fast, or the engine's fallen flag with a lean)
/// he stops doing anything a man on his feet does: no aim-back, and the free hand no longer reaches back
/// for the gun. The flag holds for a beat so a body rocking through the threshold doesn't flicker it.
/// When he goes over, the catch is set by the way he falls: face first the arms go out ahead to take the
/// floor; backwards they wheel for a moment (he's trying to get his weight back) and then reach behind
/// him; sideways the engine's own catch.
/// </summary>
internal partial class NmBody
{
    private const float FallTilt = 40f;        // deg trunk from vertical: going over whatever the engine says
    private const float FallTiltFallen = 28f;  // deg, once the engine has flagged him fallen
    private const float FallDropSpeed = 1.1f;  // m/s his weight drops: falling, not sinking into a crouch
    private const float FallHold = 0.8f;       // s the flag stays after the last falling frame
    private const float WheelMax = 0.4f;       // s backward fall: arms wheel at most this long before the catch

    private float _fallingUntil;
    private bool _fallLogged;
    private float _catchDueAt;                 // backward fall: when the catch replaces the arm wheel (0 = none due)
    private FallWay _catchWay;

    private enum FallWay { Forward, Backward, Left, Right }

    private static bool FallMotions => EuphoriaPlugin.Instance?.FallMotions.Value ?? true;

    /// Is he going down (or down) right now? (A man who caught himself after a catch isn't, once he's upright.)
    private bool GoingDown => _downSince > 0f || Time.time < _fallingUntil;

    /// Update, before the aim-back and the hand-back checks.
    private void FallWatchTick()
    {
        if (_dying || _startPose == StartPose.Prone || _inAir) return;
        nm_agent_get_com(_agent, _f3, _g3);
        float tilt = TrunkTilt();
        bool fallen = nm_agent_has_fallen(_agent) == 1;
        bool falling = tilt > FallTilt || (fallen && tilt > FallTiltFallen) || (_g3[2] < -FallDropSpeed && !_crouchStumble);
        if (falling) _fallingUntil = Time.time + FallHold;
        if (!GoingDown) { _fallLogged = false; return; }
        if (!_fallLogged)
        {
            _fallLogged = true;
            EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} going down (trunk {tilt:F0} deg, dropping {-_g3[2]:F1} m/s) - no aim-back, the free hand stays off the gun");
        }
        if (_aimBack) StopAim();
    }

    /// Which way he's going over, from the trunk's lean against the way the doll faces.
    private FallWay FallDirection()
    {
        Part(0, out Vector3 pel, out _);
        Part(11, out Vector3 neck, out _);
        Vector3 lean = neck - pel;
        lean.y = 0f;
        nm_agent_get_com(_agent, _f3, _g3);
        Vector3 vel = ToUnity(_g3[0], _g3[1], _g3[2]);
        vel.y = 0f;
        lean += vel * 0.25f; // a body still upright but moving fast falls the way it moves
        if (lean.sqrMagnitude < 1e-4f) return FallWay.Forward;
        lean.Normalize();
        Vector3 fwd = DollFacing();
        float f = Vector3.Dot(lean, fwd);
        if (f > 0.5f) return FallWay.Forward;
        if (f < -0.5f) return FallWay.Backward;
        return Vector3.Dot(lean, Vector3.Cross(Vector3.up, fwd)) > 0f ? FallWay.Right : FallWay.Left;
    }

    /// He's going over: the catch for the way he falls. Every param is sent each time, since the engine keeps
    /// the last catch's values.
    private void StartCatch(float comH)
    {
        if (!FallMotions) { Send("catchFall", ("start", true), ("legsStiffness", 9f)); return; }
        _catchWay = FallDirection();
        // backwards, still high: the arms wheel while he tries to get his weight back over his feet
        if (_catchWay == FallWay.Backward && comH > ComScaled(0.75f, 0.8f))
        {
            Send("armsWindmillAdaptive", ("start", true), ("disableOnImpact", true), ("amplitude", 0.75f), ("period", 0.75f),
                 ("armStiffness", 13f), ("bodyStiffness", 10f));
            _catchDueAt = Time.time + WheelMax;
            EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} going over backwards - arms wheel, then reach back for the floor");
            return;
        }
        SendCatch();
    }

    /// Update while a backward fall's arm wheel runs: the catch takes over once he's low or the wheel is done.
    private void CatchDueTick(float comH)
    {
        if (_catchDueAt == 0f) return;
        if (Time.time < _catchDueAt && comH > ComScaled(0.6f, 0.65f) && _downSince == 0f) return;
        SendCatch();
    }

    private void SendCatch()
    {
        _catchDueAt = 0f;
        switch (_catchWay)
        {
            case FallWay.Forward: // face first: arms well out ahead, firm, to take the floor; head up
                Send("catchFall", ("start", true), ("legsStiffness", 9f), ("torsoStiffness", 9f), ("armsStiffness", 17f),
                     ("forwardMaxArmOffset", 0.6f), ("backwardsMinArmOffset", -0.25f), ("useHeadLook", true));
                break;
            case FallWay.Backward: // backwards: hands reach back and down behind him, the torso curls (chin in)
                Send("catchFall", ("start", true), ("legsStiffness", 9f), ("torsoStiffness", 11f), ("armsStiffness", 13f),
                     ("forwardMaxArmOffset", 0.35f), ("backwardsMinArmOffset", -0.5f), ("useHeadLook", false));
                break;
            default: // sideways: the engine's own catch, the arm on the low side goes out
                Send("catchFall", ("start", true), ("legsStiffness", 9f), ("torsoStiffness", 9f), ("armsStiffness", 15f),
                     ("forwardMaxArmOffset", 0.35f), ("backwardsMinArmOffset", -0.25f), ("useHeadLook", true));
                break;
        }
        EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} going over {WayName(_catchWay)} - {(_catchWay == FallWay.Forward ? "arms out ahead to catch himself" : _catchWay == FallWay.Backward ? "hands back to break the fall" : "arm out on the low side")}");
    }

    private static string WayName(FallWay w) => w == FallWay.Forward ? "face first" : w == FallWay.Backward ? "backwards" : w == FallWay.Left ? "to his left" : "to his right";
}
