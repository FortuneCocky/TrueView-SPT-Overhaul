using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// The last word on SPT's knees, in SPT's own skeleton. The joint clamp (NmBodyClamp) holds bones to
/// the doll's ranges through the takeover offset, so a doll whose knee frame came out flipped (some
/// scav outfits) handed SPT a knee bent backwards that the clamp read as in range. Here the kneecap
/// is taken from the animation at the takeover (the game never animates a knee backwards) and kept
/// in the thigh's own frame, where it never moves; every frame, after everything else has posed the
/// legs, each shin is put back on that hinge: bent only backwards, 0 to KneeMax, no sideways bend.
/// </summary>
internal partial class NmBody
{
    private const float KneeMax = 150f;      // deg: heel to the back of the thigh
    private const float KneeSlack = 3f;      // deg: corrections smaller than this are left alone (no jitter)
    private readonly Vector3[] _kneeCapLocal = new Vector3[2];
    private readonly bool[] _kneeOk = new bool[2];
    private bool _kneeLogged;

    /// Attach, while the bones still hold the animation's pose.
    private void KneeGuardAttach()
    {
        _kneeLogged = false;
        for (int s = 0; s < 2; s++)
        {
            _kneeOk[s] = false;
            Transform th = _bones[s == 0 ? 1 : 4], ca = _bones[s == 0 ? 2 : 5], ft = _bones[s == 0 ? 3 : 6];
            if (th == null || ca == null || ft == null) continue;
            Vector3 t = ca.position - th.position, sh = ft.position - ca.position;
            if (t.sqrMagnitude < 1e-6f || sh.sqrMagnitude < 1e-6f) continue;
            t.Normalize();
            // bent: the kneecap faces away from where the shin swings; nearly straight: the way he faces
            Vector3 cap = Vector3.Angle(t, sh) > 12f ? -Vector3.ProjectOnPlane(sh, t) : Vector3.ProjectOnPlane(_p.Transform.forward, t);
            if (cap.sqrMagnitude < 1e-4f) continue; // prone and straight: no way to tell, left to the clamp
            _kneeCapLocal[s] = Quaternion.Inverse(th.rotation) * cap.normalized;
            _kneeOk[s] = true;
        }
    }

    private void KneeGuard()
    {
        for (int s = 0; s < 2; s++)
        {
            if (!_kneeOk[s]) continue;
            Transform th = _bones[s == 0 ? 1 : 4], ca = _bones[s == 0 ? 2 : 5], ft = _bones[s == 0 ? 3 : 6];
            if (th == null || ca == null || ft == null) continue;
            Vector3 t = ca.position - th.position, sh = ft.position - ca.position;
            if (t.sqrMagnitude < 1e-6f || sh.sqrMagnitude < 1e-6f) continue;
            t.Normalize();
            Vector3 cap = Vector3.ProjectOnPlane(th.rotation * _kneeCapLocal[s], t);
            if (cap.sqrMagnitude < 1e-6f) continue;
            cap.Normalize();
            Vector3 dir = sh.normalized;
            // bend in the hinge plane: 0 straight, positive = heel towards the back of the thigh
            float bend = Mathf.Atan2(Vector3.Dot(dir, -cap), Vector3.Dot(dir, t)) * Mathf.Rad2Deg;
            float held = bend;
            if (bend < 0f || bend > KneeMax)
            {
                // outside the range: to whichever end is nearer round the circle
                float toZero = bend < 0f ? -bend : 360f - bend, toMax = bend > KneeMax ? bend - KneeMax : bend + 360f - KneeMax;
                held = toZero <= toMax ? 0f : KneeMax;
            }
            float r = held * Mathf.Deg2Rad;
            Vector3 want = t * Mathf.Cos(r) - cap * Mathf.Sin(r);
            float off = Vector3.Angle(dir, want);
            if (off < KneeSlack) continue;
            ca.rotation = Quaternion.FromToRotation(dir, want) * ca.rotation;
            if (off > 20f && !_kneeLogged)
            {
                _kneeLogged = true;
                EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} {(s == 0 ? "L" : "R")} knee was {bend:F0} deg on its hinge ({off:F0} deg off) - held to SPT's knee");
            }
        }
    }
}
