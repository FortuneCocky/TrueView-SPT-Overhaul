using System.Reflection;
using EFT;
using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// Pose-matched recovery: instead of handing a body back to the animation in whatever pose physics
/// left it (and blending across the gap), the doll first moves itself into the stance it is handed
/// back in. The game's stance is switched at the start (stand, crouch or prone) and the root turned to
/// where the body faces, so the animator, still running underneath, plays that stance at the body's
/// spot. Each frame its pose is read before the physics overwrites the bones (the rig check's mapping:
/// SPT bones -> doll parts through the binding, fitted to the doll's joints) and ramped in as the
/// muscles' target. Once the doll is within a few degrees of it, the hand-back is a short blend.
/// Stand/crouch keep the balance task through it; prone (landed face down) lets go of the curl and
/// lies into the prone pose. A new hit or a fall cancels it and the reaction carries on.
/// </summary>
internal partial class NmBody
{
    private const float MatchRamp = 0.45f;   // s to ramp the muscle target from his pose to the stance
    private const float MatchTol = 15f;      // deg: worst joint off the stance that still hands back
    private const float MatchMax = 1.8f;     // s before handing back anyway
    private const float MatchBlend = 0.18f;  // s blend once matched
    private const float MatchLateBlend = 0.45f;

    private RigBinding _standBind;           // binding taken at a standing takeover (for matching later)
    private bool _matching, _animOk;
    private StartPose _matchTo;
    private float _matchStart, _matchErr;
    private int _matchWorst;
    private RigBinding _matchBind;
    private DollGeom _matchGeom;
    private readonly Vector3[] _matchFromPos = new Vector3[Parts], _stancePos = new Vector3[Parts];
    private readonly Quaternion[] _matchFromRot = new Quaternion[Parts], _stanceRot = new Quaternion[Parts], _stanceLocal = new Quaternion[Parts];
    private const float MatchVisualRamp = 0.6f;  // s for SPT's bones to ease from the physics pose into the stance
    private const float StanceWait = 2.5f;       // s for the game to actually be in the stance before giving up on it
    private float _stanceAt, _nextStanceTry;      // when the game's stance took (0 = not yet)
    private const float MatchVisualHold = 0.25f; // s at full stance before handing back when the doll itself is still off
    private float[] _matchZero = new float[Parts * 7];

    internal bool Matching => _matching;

    // The stance shown on SPT's bones never hides a fall: it gives way as the doll's trunk tips past
    // FallTipFrom (gone by the "fell" limit, 45 deg), and when the match is cancelled it fades out over
    // MatchFadeOut instead of vanishing in a frame. Cutting it showed him standing, then on the floor.
    private const float FallTipFrom = 25f, FallTipTo = 45f;
    private const float MatchFadeOut = 0.35f;
    private float _matchVisW, _matchFadeFrom, _matchFadeAt;
    private bool MatchFading => !_matching && _matchFadeFrom > 0.001f && Time.time - _matchFadeAt < MatchFadeOut;

    /// Start moving into `to`. False = can't (no binding): the caller hands back the old way.
    private bool BeginMatch(StartPose to, Vector3 faceDir)
    {
        if (_dying || !Active) return false;
        RigBinding b = _bind ?? _standBind;
        if (b == null || !b.Valid) return false;
        DollGeom g = ReadDollGeom();
        if (g == null) return false;

        faceDir.y = 0f;
        if (faceDir.sqrMagnitude < 1e-4f) faceDir = _p.Transform.forward;
        SetRootYaw(Quaternion.LookRotation(faceDir.normalized, Vector3.up));
        ApplyStance(to);
        _stanceAt = 0f; _nextStanceTry = Time.time + 0.3f;

        for (int k = 0; k < Parts; k++) Part(k, out _matchFromPos[k], out _matchFromRot[k]);
        StopFallenPose();
        if (to == StartPose.Prone)
        {
            Send("catchFall", ("start", false));
            TuneDown();
        }
        _clutchBone = -1; // the free hand goes back to the gun, not to the wound
        _matchFromGround = _downSince > 0f || to == StartPose.Prone || TrunkTilt() > 50f;
        _reachCut = GoingDown ? Time.time - 1f : 0f; // still going down: the hand doesn't go for the gun at all
        _matchFadeFrom = 0f; _matchVisW = 0f;
        _matchTo = to; _matchBind = b; _matchGeom = g;
        _matching = true; _animOk = false; _matchStart = Time.time; _matchErr = 180f;
        EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} moving into {to.ToString().ToLowerInvariant()} before the hand-back");
        SetPhase(Phase.Recover, $"target {to.ToString().ToLowerInvariant()}");
        return true;
    }

    /// The doll's heading on the ground (body forward at takeover, carried by the pelvis).
    private Vector3 DollFacing()
    {
        Vector3 f = PartRot(0) * _pelvisFwdLocal;
        f.y = 0f;
        return f.sqrMagnitude > 1e-4f ? f.normalized : _p.Transform.forward;
    }

    private void SetRootYaw(Quaternion yaw)
    {
        try
        {
            _p.Transform.rotation = yaw;
            _heldLook = yaw * Vector3.forward;
            BotActions.LookTo(_p, _heldLook);
            BotActions.SetMoveRotation(_p, yaw);
        }
        catch { }
        BotActions.SyncMover(_p);
    }

    /// LateUpdate, before the physics writes the bones: what the animation wants this frame.
    private void CaptureAnimPose()
    {
        for (int k = 0; k < Parts; k++)
            if (_bones[k] != null) { _stancePos[k] = _bones[k].position; _stanceRot[k] = _bones[k].rotation; _stanceLocal[k] = _bones[k].localRotation; }
        _animOk = true;
    }

    /// Update: true while matching (the rest of the tick is skipped).
    private bool MatchTick(float comSpeed)
    {
        if (!_matching) return false;
        float t = Time.time - _matchStart;
        // Fell for real: trunk well over or his weight down at the floor. Delirium's has-fallen flag alone
        // isn't it: it stays set after a deep stumble or a sink into a crouch, and cancelled every match
        // (and every catch) of a man still on his feet, who then dropped.
        bool fell = false;
        if (_matchTo != StartPose.Prone && nm_agent_has_fallen(_agent) == 1)
        {
            nm_agent_get_com(_agent, _f3, _g3);
            float comH = _f3[2] - GroundUnder(ToUnity(_f3[0], _f3[1], _f3[2]));
            fell = TrunkTilt() > 45f || comH < ComScaled(0.4f, 0.5f);
        }
        if (_lastHitAt > _matchStart || fell)
        {
            EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} stance match cancelled ({(fell ? "fell" : "hit again")})");
            SetPhase(fell || _matchFromGround ? Phase.Fall : Phase.Stumble, fell ? "fell while recovering" : "hit while recovering");
            _matching = false;
            _matchFadeFrom = _matchVisW; _matchFadeAt = Time.time; // ease off the stance shown, don't drop it
            _matchCooldown = Time.time + 1f; // no new recovery straight away: it looped match/cancel for seconds
            if (fell) _matchFell++;
            if (_matchTo != StartPose.Prone) TuneAlive();
            return false;
        }
        if (!_animOk) return true;

        // Nothing is matched until the game is really in the stance (and done with its own drop to
        // prone): the bot AI's request alone often didn't take, the animation stayed standing, and a
        // lying body was eased up into a standing pose.
        if (_stanceAt == 0f)
        {
            if (StanceTaken(_matchTo)) _stanceAt = Time.time;
            else
            {
                if (Time.time >= _nextStanceTry) { ApplyStance(_matchTo); _nextStanceTry = Time.time + 0.3f; }
                if (t > StanceWait)
                {
                    if (_matchTo == StartPose.Prone)
                    {
                        EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} couldn't go prone here - crouching instead");
                        _matchTo = StartPose.Crouch; _matchStart = Time.time; ApplyStance(StartPose.Crouch);
                        return true;
                    }
                    HandBack(MatchLateBlend, "stance didn't take");
                }
                return true;
            }
        }
        t = Time.time - _stanceAt;

        // the animated stance -> doll part targets (the rig check's mapping)
        var tPos = (Vector3[])_stancePos.Clone(); var tRot = (Quaternion[])_stanceRot.Clone(); var tOk = new bool[Parts];
        for (int k = 0; k < Parts; k++) tOk[k] = _bones[k] != null;
        if (_matchBind.SpineFitted && !FitSpine(tPos, tRot, tOk, _bones, _matchBind.SpineFrac)) { HandBack(MatchLateBlend, "spine unreadable"); return true; }
        SeedPoseFK(_matchBind, tPos, tRot, tOk, _matchGeom, out Vector3[] sPos, out Quaternion[] sRot);

        float w = Mathf.SmoothStep(0f, 1f, t / MatchRamp);
        for (int k = 0; k < Parts; k++)
        {
            Quaternion r = Quaternion.Slerp(_matchFromRot[k], sRot[k], w);
            ToNm(Vector3.Lerp(_matchFromPos[k], sPos[k], w), _matchZero, k * 7);
            _matchZero[k * 7 + 3] = -r.x; _matchZero[k * 7 + 4] = -r.z; _matchZero[k * 7 + 5] = -r.y; _matchZero[k * 7 + 6] = r.w;
        }
        nm_agent_set_zero_pose(_agent, _matchZero, Parts);

        // how far off: worst joint (child relative to parent), body and legs only
        float err = 0f;
        for (int k = 1; k < Parts; k++)
        {
            int pa = _matchGeom.Parent[k];
            if (pa < 0 || k == 3 || k == 6 || k >= 13) continue; // arms: the gun hold and support-hand reach own them
            Quaternion now = Quaternion.Inverse(PartRot(pa)) * PartRot(k);
            Quaternion want = Quaternion.Inverse(sRot[pa]) * sRot[k];
            float e = Quaternion.Angle(now, want);
            if (e > err) { err = e; _matchWorst = k; }
        }
        _matchErr = err;

        bool shown = t > MatchVisualRamp + MatchVisualHold; // SPT's bones are already in the stance on screen
        if (w >= 1f && (err < MatchTol || shown) && comSpeed < 0.5f && Grounded(_matchTo != StartPose.Prone))
            HandBack(MatchBlend, err < MatchTol ? "matched" : "shown in stance");
        else if (t > MatchMax) HandBack(MatchLateBlend, "timed out");
        return true;
    }

    /// LateUpdate while matching, after the physics drive: SPT's bones ease from the doll's pose into the
    /// stance the animation is playing (the same pose the rig check maps), local rotations parent-first,
    /// so the body visibly settles into stand/crouch/prone while the doll is still getting there. Upper
    /// body fully; the legs half while he balances on them (stand/crouch), fully when lying (prone).
    private void MatchVisual()
    {
        if (!_animOk || _stanceAt == 0f) return; // the animation isn't playing the stance yet
        float w;
        if (_matching)
        {
            w = Mathf.SmoothStep(0f, 1f, (Time.time - _stanceAt) / MatchVisualRamp);
            if (_matchTo != StartPose.Prone) w *= 1f - Mathf.SmoothStep(0f, 1f, (TrunkTilt() - FallTipFrom) / (FallTipTo - FallTipFrom));
            _matchVisW = w;
        }
        else w = _matchFadeFrom * (1f - Mathf.SmoothStep(0f, 1f, (Time.time - _matchFadeAt) / MatchFadeOut));
        if (w <= 0.001f) return;
        float legs = _matchTo == StartPose.Prone ? 1f : 0.5f;
        if (_bones[0] != null)
            _bones[0].SetPositionAndRotation(Vector3.Lerp(_bones[0].position, _stancePos[0], w * legs),
                                             Quaternion.Slerp(_bones[0].rotation, _stanceRot[0], w));
        for (int k = 1; k < Parts; k++)
            if (_bones[k] != null)
                _bones[k].localRotation = Quaternion.Slerp(_bones[k].localRotation, _stanceLocal[k], k <= 6 ? w * legs : w);
    }

    /// Put the game into the stance: straight on his movement (as the dev panel's force pose does) and
    /// through the bot AI too, so the AI's own stance doesn't undo it.
    private void ApplyStance(StartPose to)
    {
        bool prone = to == StartPose.Prone;
        float level = PhaseRules.StanceLevel(to);
        BotActions.SetPose(_p, prone ? 0f : level, prone);
        try
        {
            MovementContext mc = _p.MovementContext;
            if (prone) { if (!mc.IsInPronePose) _p.ToggleProne(); }
            else { if (mc.IsInPronePose) _p.ToggleProne(); mc.SetPoseLevel(level, true); }
        }
        catch { }
    }

    private bool StanceTaken(StartPose to)
    {
        MovementContext mc = _p?.MovementContext;
        if (mc == null) return true;
        string st = StateName(_p);
        if (st == "Transit2Prone" || st == "Prone2Stand") return false; // still playing the game's own drop / rise
        if (to == StartPose.Prone) return mc.IsInPronePose;
        if (mc.IsInPronePose) return false;
        if (to == StartPose.DeepCrouch) return mc.PoseLevel <= PhaseRules.DeepCrouchLevel + 0.05f;
        return to == StartPose.Crouch ? mc.PoseLevel < 0.8f : mc.PoseLevel > 0.85f;
    }

    private void HandBack(float blend, string why)
    {
        EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} handed back {_matchTo.ToString().ToLowerInvariant()} ({why}, worst joint {_matchErr:F0}° at {DevBoneName(_matchWorst)} after {Time.time - _matchStart:F2}s)");
        _matching = false;
        _blendDur = blend;
        Release();
    }

    /// Which stance a recovered (not downed) body goes back to.
}
