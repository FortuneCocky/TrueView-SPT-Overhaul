using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// A get-up into a crouch comes up into the crouch (GetUpWarp): once the clip is rising, the game is put
/// into the crouch, and each frame the pose the animation plays for it (read before the clip's goes on,
/// like the stance match) is turned to face the way the clip does (hip line) and blended in over the
/// clip's own pose: pelvis height and the body, legs and head. The arms keep the clip's gun hold. The
/// physical get-up's doll follows, since it takes its targets from these bones. At the clip's end he is
/// already in the crouch the animation has, so the hand-back is short.
/// </summary>
internal partial class NmBody
{
    private const float WarpRampIn = 0.3f; // s to ease in once the game is in the crouch
    private const int WarpLastBone = 12;   // pelvis, legs, spine, neck, head; the arms stay with the clip

    private bool _guWarp;
    private StartPose _guWarpTo;
    private float _guWarpAt, _guWarpTry;   // when the game's crouch took (0 = not yet); next stance retry

    /// A get-up starts: warp it only when it ends in a crouch.
    private void WarpReset()
    {
        _guWarpTo = _recoverTarget;
        _guWarp = !_guRoll && GetUpWarp.Applies(_guWarpTo);
        _guWarpAt = 0f; _guWarpTry = 0f;
    }

    /// LateUpdate, before the clip's pose goes on: the animation's pose, once it plays the crouch.
    private void WarpCapture()
    {
        if (_guWarp && _guWarpAt > 0f) CaptureAnimPose();
    }

    /// LateUpdate, right after the clip's pose is on SPT's bones: bend it into the crouch.
    private void WarpTail(float fr, float last, GetUpClip clip)
    {
        if (!_guWarp || _p == null) return;
        float w = GetUpWarp.Weight(fr, clip.StandFrame, last);
        if (w <= 0f) return;
        if (_guWarpAt == 0f)
        {
            if (StanceTaken(_guWarpTo))
            {
                _guWarpAt = Time.time;
                EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} get-up rising into {_guWarpTo.ToString().ToLowerInvariant()}");
            }
            else if (Time.time >= _guWarpTry) { ApplyStance(_guWarpTo); _guWarpTry = Time.time + 0.3f; }
            return; // the animation's pose is read from the next frame on
        }
        if (!_animOk) return;
        w *= Mathf.SmoothStep(0f, 1f, (Time.time - _guWarpAt) / WarpRampIn);
        if (w <= 0f || _bones[1] == null || _bones[4] == null) return;

        // the animation's crouch turned about the vertical so its hip line lies along the clip's
        Vector3 hc = _bones[4].position - _bones[1].position, ha = _stancePos[4] - _stancePos[1];
        hc.y = 0f; ha.y = 0f;
        Quaternion yaw = hc.sqrMagnitude > 1e-4f && ha.sqrMagnitude > 1e-4f
            ? Quaternion.AngleAxis(Vector3.SignedAngle(ha, hc, Vector3.up), Vector3.up) : Quaternion.identity;
        float animH = _stancePos[0].y - _p.Transform.position.y; // the crouch's pelvis height over his root

        for (int k = 0; k <= WarpLastBone; k++) // parents first: each world rotation set after its parent's
        {
            if (_bones[k] == null) continue;
            Quaternion q = Quaternion.Slerp(_bones[k].rotation, yaw * _stanceRot[k], w);
            if (k == 0)
            {
                Vector3 p = _bones[0].position;
                p.y = Mathf.Lerp(p.y, _guGround + animH, w);
                _bones[0].SetPositionAndRotation(p, q);
            }
            else _bones[k].rotation = q;
        }
    }
}
