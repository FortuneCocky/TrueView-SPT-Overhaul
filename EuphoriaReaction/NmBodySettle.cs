using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// Settled hand-back: a body that has come to rest close to one of the stances he can be handed back in
/// ends the reaction after a few seconds (config, 5) instead of lying out the full down time or the stuck
/// limit, and is taken into that stance.
///
/// Which stance is closest is measured on the doll every frame, from five features:
///   height   COM above the ground, over his standing COM height (1 standing, ~0.6 crouched, ~0.2 lying)
///   tilt     trunk lean off vertical, pelvis to neck (0 upright, 90 flat)
///   knees    mean knee bend, hip -> knee -> ankle (0 straight)
///   belly    which way the belly faces: -1 at the ground (face down), +1 at the sky (on his back)
///   feet     whether a foot carries him (contact)
/// Each stance is a template: a target and a tolerance per feature. The distance to a template is the
/// root mean square of (feature - target) / tolerance, so 1 means every feature is at the edge of its
/// tolerance; the closest template under 1 is the pose he's in. Only a body that stays in the same pose,
/// with the COM and every part all but still, counts as settled.
///
/// The way back in is picked per stance, from the animations there are:
///   stand / crouch  the stance match (doll blended into the game's own stand or crouch animation)
///   prone           the prone match, head the way he lies
///   on back / side  a MotionBricks get-up clip (several; the one closest to how he lies), else a
///                   roll-over clip onto his front into prone, else the physics roll
/// </summary>
internal partial class NmBody
{
    private enum Settle { None, Stand, Crouch, Prone, Lying }

    // per template: height, tilt, knees, belly; tolerances in the same order
    private static readonly float[,] SettleTarget =
    {
        { 0f, 0f, 0f, 0f },        // None (unused)
        { 1.00f, 5f, 10f, 0f },    // Stand
        { 0.62f, 18f, 95f, 0f },   // Crouch
        { 0.20f, 88f, 20f, -0.9f },// Prone
        { 0.22f, 85f, 30f, 0.6f }, // Lying (back or side)
    };
    private static readonly float[,] SettleTol =
    {
        { 1f, 1f, 1f, 1f },
        { 0.12f, 20f, 30f, 99f },  // upright: belly doesn't matter
        { 0.14f, 28f, 40f, 99f },
        { 0.15f, 28f, 60f, 0.45f },
        { 0.18f, 30f, 70f, 0.75f },
    };
    private static readonly string[] SettleNames = { "none", "stand", "crouch", "prone", "lying" };

    private const float SettleSpeed = 0.25f;    // m/s COM drift that still counts as resting
    private const float SettlePartSpeed = 1.2f; // m/s: no part may still be swinging
    private const float SettleAfterHit = 1f;    // s after the last hit before resting counts

    private Settle _settlePose;
    private float _settleFor, _settleDist;

    private static float SettleTime => EuphoriaPlugin.Instance?.SettleTime.Value ?? 5f;

    private void SettleAttach()
    {
        _settlePose = Settle.None;
        _settleFor = 0f;
    }

    /// His standing COM height: the takeover height, scaled up if he was taken over crouched or prone.
    private float StandComH => _startPose == StartPose.Stand ? _comH0 : _startPose == StartPose.Crouch ? _comH0 / 0.68f : 0.95f;

    /// Mean knee bend (deg) from the doll's hip, knee and ankle joints.
    private float KneeBend()
    {
        float sum = 0f;
        int n = 0;
        for (int s = 0; s < 2; s++)
        {
            int hip = s == 0 ? 1 : 4;
            if (!Joint(hip, out _, out Vector3 a, out _) || !Joint(hip + 1, out _, out Vector3 b, out _)
                || !Joint(hip + 2, out _, out Vector3 c, out _)) continue;
            sum += Vector3.Angle(b - a, c - b);
            n++;
        }
        return n > 0 ? sum / n : 0f;
    }

    /// -1 belly at the ground .. +1 belly at the sky (0 on his side or upright).
    private float BellyUp()
    {
        if (!_bellyAxesOk) return LyingFaceDown(out _) ? -1f : 0.5f;
        return (PartRot(0) * _pelvisBellyLocal + PartRot(10) * _chestBellyLocal).normalized.y;
    }

    /// The stance he's closest to right now, and how close (RMS of the scaled feature errors).
    private Settle ClosestPose(float h, out float best)
    {
        float hn = h / Mathf.Max(0.3f, StandComH);
        float tilt = TrunkTilt(), knees = KneeBend(), belly = BellyUp();
        bool feet = nm_agent_get_part_contact(_agent, 3) != 0 || nm_agent_get_part_contact(_agent, 6) != 0;
        Settle pick = Settle.None;
        best = float.MaxValue;
        for (int i = 1; i < SettleNames.Length; i++)
        {
            // stand and crouch are carried by the feet; a body off its feet can't be either
            if ((i == (int)Settle.Stand || i == (int)Settle.Crouch) && !feet) continue;
            float e0 = (hn - SettleTarget[i, 0]) / SettleTol[i, 0];
            float e1 = (tilt - SettleTarget[i, 1]) / SettleTol[i, 1];
            float e2 = (knees - SettleTarget[i, 2]) / SettleTol[i, 2];
            float e3 = (belly - SettleTarget[i, 3]) / SettleTol[i, 3];
            float d = Mathf.Sqrt((e0 * e0 + e1 * e1 + e2 * e2 + e3 * e3) / 4f);
            if (d < best) { best = d; pick = (Settle)i; }
        }
        return best < 1f ? pick : Settle.None;
    }

    /// Update: true when he had rested close to a stance long enough and was taken into it.
    private bool SettleTick(float comSpeed, float vmax, float h)
    {
        if (_rolling || _matching || _dying || _aimBack || _getUp != null) { _settleFor = 0f; return false; }
        if (Time.time - _lastHitAt < SettleAfterHit || Time.time < _matchCooldown) { _settleFor = 0f; return false; }
        Settle now = comSpeed < SettleSpeed && vmax < SettlePartSpeed ? ClosestPose(h, out _settleDist) : Settle.None;
        if (now == Settle.None || now != _settlePose) { _settlePose = now; _settleFor = 0f; return false; }
        _settleFor += Time.deltaTime;
        if (_settleFor < SettleTime) return false;

        EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} rested {_settleFor:F1}s close to {SettleNames[(int)now]} (fit {_settleDist:F2}) - into it");
        _settleFor = 0f;
        StopFallenPose();
        bool ok;
        switch (now)
        {
            case Settle.Stand:
                Send("stayUpright", ("start", true), ("stiffness", Tune.Upright * 2f));
                ok = BeginMatch(StartPose.Stand, DollFacing());
                break;
            case Settle.Crouch:
                ok = BeginMatch(StartPose.Crouch, DollFacing());
                break;
            default: // lying: a get-up, or prone when face down and badly hurt (GetUp)
                ok = HandBackFromGround();
                break;
        }
        if (ok) return true;
        _blendDur = GetUpBlend;
        ReleaseToStance();
        return true;
    }
}
