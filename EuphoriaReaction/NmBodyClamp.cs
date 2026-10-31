using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// SPT's skeleton follows the doll (the plain rotation copy, SPT's own shape and proportions), but
/// every bone is then held to the joint ranges the doll itself has: whatever the copy, the arm blend
/// or a hard hit does, no SPT joint bends further, or a way, the physics joint can't. Parent-first,
/// each bone's rotation is read back as the doll part it stands for (the takeover offset between the
/// two), relative to its parent, and fitted: knees and elbows keep only their hinge bend, within
/// range; hips their forward swing and small twist; ball joints (spine, shoulders, wrists, head) a
/// swing cone and twist range; locked joints (neck, clavicles) their rest angle. The doll's soft
/// limits can be pushed past on a big impact, and the arms are part animation while he's standing:
/// this is what stopped knees, elbows and the back folding the wrong way on screen.
/// Feet and the gun wrist keep the animation's angle (the doll has no such joint) and aren't fitted.
/// </summary>
internal partial class NmBody
{
    private DollGeom _clampGeom;
    private readonly Vector3[] _clampLim = new Vector3[21];  // lean0, lean1, twist (rad) from the engine
    private readonly Quaternion[] _clampJ = new Quaternion[21]; // bone -> doll part rotation offset
    private bool _clampReady;

    /// Attach, after the takeover pose is captured.
    private void ClampAttach()
    {
        _clampReady = false;
        try { _clampGeom = ReadDollGeom(); } catch { _clampGeom = null; }
        if (_clampGeom == null || !_jointsLoaded)
        {
            EuphoriaPlugin.LogV($"Delirium: joint clamp off for this reaction ({(_clampGeom == null ? "doll geometry unreadable" : "no joint model")})");
            return;
        }
        for (int k = 0; k < Parts; k++)
        {
            _clampJ[k] = Quaternion.Inverse(_boneRot0[k]) * _partRot0[k];
            if (!Joint(k, out _, out _, out _clampLim[k])) _clampLim[k] = Vector3.zero;
        }
        _clampReady = true;
    }

    private void ClampToJoints(bool arms, bool lockWrist)
    {
        if (!_clampReady) return;
        DollGeom g = _clampGeom;
        for (int k = 1; k < Parts; k++) // parents come before children in the doll's part order
        {
            if (k == 3 || k == 6) continue;                    // free ankles
            if (k >= 12) continue;                             // head and arms: its fits for them were wrong (palms twisted 110-130 deg, forearms 60-100)
            if (k >= 13 && !arms) continue;                    // arms on the animation: nothing of ours to fit
            if (k == 20 && lockWrist) continue;                // gun wrist held straight
            int pa = g.Parent[k];
            if (pa < 0 || _bones[k] == null || _bones[pa] == null) continue; // part 9 has no SPT bone: the chest is skipped
            Quaternion parentRot = _bones[pa].rotation * _clampJ[pa];
            Quaternion want = _bones[k].rotation * _clampJ[k];
            Quaternion fit = _jointKind[k] == 'b' && k != 1 && k != 4
                ? FitBall(k, pa, parentRot, want, g)
                : FitJoint(k, pa, parentRot, want, g);
            // Only what goes past the doll's own limit is corrected: the fits sit LimitMargin inside the
            // limit (where seeding needs them), and pulling every limb at its limit back that far curled
            // legs and arms off a falling doll that was doing nothing wrong.
            float off = Quaternion.Angle(fit, want) - LimitMargin;
            if (off > 0.5f)
            {
                _bones[k].rotation = Quaternion.RotateTowards(want, fit, off) * Quaternion.Inverse(_clampJ[k]);
                if (off > _gapClamp[k]) _gapClamp[k] = off;
            }
        }
    }

    /// Ball joint: twist about the joint's axis within its twist range, the rest as a swing cone.
    private Quaternion FitBall(int k, int pa, Quaternion parentRot, Quaternion want, DollGeom g)
    {
        Vector3 lim = _clampLim[k];
        float maxSwing = Mathf.Max(lim.x, lim.y) * Mathf.Rad2Deg - LimitMargin, maxTwist = lim.z * Mathf.Rad2Deg - LimitMargin;
        if (_hingeAxisRest[k].sqrMagnitude < 0.5f || (maxSwing < 1f && maxTwist < 1f)) return want;
        Quaternion restRel = Quaternion.Inverse(g.RestRot[pa]) * g.RestRot[k];
        Quaternion delta = Quaternion.Inverse(parentRot) * want * Quaternion.Inverse(restRel);
        Vector3 axis = (Quaternion.Inverse(g.RestRot[pa]) * _hingeAxisRest[k]).normalized;

        Vector3 v = new Vector3(delta.x, delta.y, delta.z), p = axis * Vector3.Dot(v, axis);
        var twist = new Quaternion(p.x, p.y, p.z, delta.w);
        float m = Mathf.Sqrt(twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w);
        twist = m < 1e-6f ? Quaternion.identity : new Quaternion(twist.x / m, twist.y / m, twist.z / m, twist.w / m);
        Quaternion swing = delta * Quaternion.Inverse(twist);

        twist.ToAngleAxis(out float tDeg, out Vector3 tAx);
        if (tDeg > 180f) tDeg -= 360f;
        if (Mathf.Abs(tDeg) > Mathf.Max(maxTwist, 0f)) twist = Quaternion.AngleAxis(Mathf.Sign(tDeg) * Mathf.Max(maxTwist, 0f), tAx);
        swing.ToAngleAxis(out float sDeg, out Vector3 sAx);
        if (sDeg > 180f) sDeg -= 360f;
        if (Mathf.Abs(sDeg) > Mathf.Max(maxSwing, 0f)) swing = Quaternion.AngleAxis(Mathf.Sign(sDeg) * Mathf.Max(maxSwing, 0f), sAx);
        return parentRot * (swing * twist) * restRel;
    }
}
