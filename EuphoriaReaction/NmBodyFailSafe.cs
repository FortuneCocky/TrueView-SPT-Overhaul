using System;
using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// Last-resort fail-safe: a bot still in the reaction long after the takeover, whatever it's doing
/// (a stance match or roll that never finishes, a body wedged where the stuck check's stances all fail),
/// is let go standing, and the game's own jump plays so he hops up onto his feet. The jump covers the
/// short blend from however he lay to the standing animation.
/// </summary>
internal partial class NmBody
{
    private const float FailSafeBlend = 0.25f;
    private float _failJumpAt;

    private static float FailSafeTime => EuphoriaPlugin.Instance?.FailSafeTime.Value ?? 25f;

    /// Update (active): true when the time limit is up and he was let go.
    private bool FailSafeTick()
    {
        float limit = FailSafeTime;
        if (limit <= 0f || _dying || _grabbing || Time.time - _attachAt < limit) return false;
        EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} in the reaction {Time.time - _attachAt:F0}s - fail-safe: jump up to stand");
        StopFallenPose();
        // the root under the body, facing the way the body does, so the jump starts where he lies
        if (_bones != null && _bones[0] != null)
        {
            Vector3 pel = _bones[0].position, root = _p.Transform.position;
            MoveRoot(new Vector3(pel.x, root.y, pel.z));
            Vector3 face = Vector3.ProjectOnPlane(DollFacing(), Vector3.up);
            if (face.sqrMagnitude > 1e-4f) BotActions.SetMoveRotation(_p, Quaternion.LookRotation(face));
        }
        BotActions.Stand(_p);
        _crouchStumble = false;
        _blendDur = FailSafeBlend;
        Release(false);
        _failJumpAt = Time.time + 0.05f; // after the hand-back has ended any jump state of its own
        return true;
    }

    /// Update (handed back): the jump.
    private void FailJumpTick()
    {
        if (_failJumpAt <= 0f || Time.time < _failJumpAt || _p == null) return;
        _failJumpAt = 0f;
        if (_p.HealthController?.IsAlive != true) return;
        try
        {
            var mc = _p.MovementContext;
            mc.TryJump();
            if (!InJump(_p)) mc.PlayerAnimatorEnableJump(true); // no stamina / state said no: play it anyway
            _jumpTaken = true; _jumpTakenAt = Time.time;         // feet IK back on once he's landed
            EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} fail-safe jump");
        }
        catch (Exception e) { EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} fail-safe jump failed: {e.Message}"); }
    }
}
