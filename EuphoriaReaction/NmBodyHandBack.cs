using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// The blend back into the animation as a spring (HandBackSpring) instead of a slide from a frozen pose.
/// The old blend held the last physics / clip frame still and morphed every bone in a straight line into
/// SPT's stance, which read as a tween: the motion stopped dead, then slid. Now the animation has the
/// bones from the first frame, and the gap to the pose he was handed back in rides on top of it, keeping
/// the speed the bones were moving at and dying out. Each bone's world rotation (and the pelvis position)
/// is offset0 * spring, so at the hand-back he is exactly where he was.
/// The speed comes from the pose one frame earlier: every driven frame is kept (PoseSnapshot).
/// </summary>
internal partial class NmBody
{
    private const float MaxSpin = 12f;  // rad/s a bone's carried spin is capped at (a physics spike flung it)
    private const float MaxDrift = 3f;  // m/s the same for the pelvis

    // the last two driven poses (1 = newest), for the speed at the hand-back
    private readonly Quaternion[] _snapRot0 = new Quaternion[Parts], _snapRot1 = new Quaternion[Parts];
    private Vector3 _snapPel0, _snapPel1;
    private float _snapAt0, _snapAt1;
    private int _snapFrame0 = -9, _snapFrame1 = -9;

    private readonly Vector3[] _spinRot = new Vector3[Parts], _offRot = new Vector3[Parts];
    private Vector3 _spinPel, _offPel;
    private bool _springSet;
    private float _springHalf, _springLen;

    /// End of LateUpdate: the pose he was shown in this frame, while physics or a clip drives him.
    private void PoseSnapshot()
    {
        if (_bones == null || _bones[0] == null || _blending || (!Active && _getUp == null) || _snapFrame1 == Time.frameCount) return;
        for (int k = 0; k < Parts; k++) { _snapRot0[k] = _snapRot1[k]; if (_bones[k] != null) _snapRot1[k] = _bones[k].rotation; }
        _snapPel0 = _snapPel1; _snapAt0 = _snapAt1; _snapFrame0 = _snapFrame1;
        _snapPel1 = _bones[0].position; _snapAt1 = Time.time; _snapFrame1 = Time.frameCount;
    }

    /// The hand-back starts: the pose he's in now, and how fast each bone was moving into it.
    private void CaptureHandBack()
    {
        for (int k = 0; k < Parts; k++)
            if (_bones[k] != null) _blendRot[k] = _bones[k].rotation;
        _blendPelvis = _bones[0].position;
        _blendStart = Time.time;
        _springSet = false; // the gap is measured on the first blend frame, against the animation's pose

        // From Update the bones still hold last frame's pose (the newest snapshot): measure against the one before.
        bool same = (_snapPel1 - _blendPelvis).sqrMagnitude < 1e-10f && Quaternion.Angle(_snapRot1[0], _blendRot[0]) < 0.01f;
        Quaternion[] from = same ? _snapRot0 : _snapRot1;
        Vector3 fromPel = same ? _snapPel0 : _snapPel1;
        float dt = same ? _snapAt1 - _snapAt0 : Time.time - _snapAt1;
        int frame = same ? _snapFrame0 : _snapFrame1;
        bool ok = dt > 1e-3f && dt < 0.1f && Time.frameCount - frame <= 3;
        for (int k = 0; k < Parts; k++)
            _spinRot[k] = ok && _bones[k] != null ? Vector3.ClampMagnitude(RotVec(_blendRot[k] * Quaternion.Inverse(from[k])) / dt, MaxSpin) : Vector3.zero;
        _spinPel = ok ? Vector3.ClampMagnitude((_blendPelvis - fromPel) / dt, MaxDrift) : Vector3.zero;
    }

    /// LateUpdate after the animation: its pose plus the gap, dying out.
    private void BlendToAnimation()
    {
        if (!_springSet)
        {
            _springHalf = HandBackSpring.Halflife(_blendDur);
            _springLen = HandBackSpring.Length(_springHalf);
        }
        float t = Time.time - _blendStart;
        if (t >= _springLen || _bones == null || _bones[0] == null)
        {
            _blending = false;
            if (_gunAfterBlend) { _gunAfterBlend = false; if (_p != null && _p.HealthController?.IsAlive == true) BotActions.ToGun(_p); }
            enabled = _jumpTaken || Time.time < _watchUntil || Time.time < _groundUntil;
            return;
        }
        for (int k = 0; k < Parts; k++)
            if (_bones[k] != null) _animRot[k] = _bones[k].rotation;
        Vector3 animPelvis = _bones[0].position;
        if (!_springSet)
        {
            for (int k = 0; k < Parts; k++)
                _offRot[k] = _bones[k] != null ? RotVec(_blendRot[k] * Quaternion.Inverse(_animRot[k])) : Vector3.zero;
            _offPel = _blendPelvis - animPelvis;
            _springSet = true;
        }
        HandBackSpring.Coeffs(_springHalf, t, out float a, out float b);
        for (int k = 0; k < Parts; k++)
        {
            if (_bones[k] == null) continue;
            Quaternion q = VecRot(a * _offRot[k] + b * _spinRot[k]) * _animRot[k];
            if (k == 0) _bones[0].SetPositionAndRotation(animPelvis + a * _offPel + b * _spinPel, q);
            else _bones[k].rotation = q;
        }
        KneeGuard();
        GunBackLate(); // the gun with the blended hand (again before render, after the game's weapon pass)
    }

    /// Rotation -> axis * angle (radians), the short way round.
    private static Vector3 RotVec(Quaternion q)
    {
        if (q.w < 0f) { q.x = -q.x; q.y = -q.y; q.z = -q.z; q.w = -q.w; }
        q.ToAngleAxis(out float deg, out Vector3 axis);
        if (deg < 1e-4f || float.IsNaN(axis.x) || float.IsInfinity(axis.x)) return Vector3.zero;
        return axis.normalized * (deg * Mathf.Deg2Rad);
    }

    private static Quaternion VecRot(Vector3 v)
    {
        float rad = v.magnitude;
        return rad < 1e-6f ? Quaternion.identity : Quaternion.AngleAxis(rad * Mathf.Rad2Deg, v / rad);
    }
}
