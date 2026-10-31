using System.Collections.Generic;
using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// The left hand lets go. While the doll owns him the left hand is off the gun (flailing, bracing, holding
/// a wound), but its fingers kept the animation's grip, curled round a handguard that isn't there. They
/// ease toward a relaxed hand: the character's own bind pose (fingers open) with a little of the grip's
/// curl kept, so it reads as a loose, resting hand. Eased in over a quarter second and back out as he's
/// handed back to the animation and the hand returns to the gun.
/// </summary>
internal partial class NmBody
{
    private const float FingerCurlKept = 0.35f; // how much of the grip's curl a relaxed hand keeps
    private const float FingerEase = 4f;        // 1/s

    private Transform[] _lFingers;
    private Quaternion[] _lFingerOpen;         // each finger bone's bind-pose local rotation
    private bool _fingersTried;
    private float _fingerW;
    private int _fingerFrame = -1;

    /// Late pass: the left fingers toward relaxed while the reaction holds him.
    private void RelaxLeftFingers()
    {
        if (_fingerFrame == Time.frameCount) return; // before-render pass: already relaxed this frame
        _fingerFrame = Time.frameCount;
        if (!_fingersTried) FindLeftFingers();
        if (_lFingers == null) return;
        bool want = Active && !_dying;
        _fingerW = Mathf.MoveTowards(_fingerW, want ? 1f : 0f, Time.deltaTime * FingerEase);
        ApplyFingers();
    }

    private void ApplyFingers()
    {
        if (_lFingers == null || _fingerW <= 0.001f) return;
        for (int i = 0; i < _lFingers.Length; i++)
        {
            Transform f = _lFingers[i];
            if (f == null) continue;
            Quaternion relaxed = Quaternion.Slerp(_lFingerOpen[i], f.localRotation, FingerCurlKept);
            f.localRotation = Quaternion.Slerp(f.localRotation, relaxed, _fingerW);
        }
    }

    /// The left hand's finger bones and their bind-pose local rotations, from the body's skinned mesh.
    private void FindLeftFingers()
    {
        _fingersTried = true;
        Transform palm = _bones != null ? _bones[16] : null;
        if (palm == null) return;
        var fingers = new List<Transform>();
        foreach (Transform t in palm.GetComponentsInChildren<Transform>(true))
            if (t != palm && t.name.IndexOf("Digit", System.StringComparison.OrdinalIgnoreCase) >= 0) fingers.Add(t);
        if (fingers.Count == 0) { EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} no left finger bones under {palm.name}"); return; }
        var open = new List<Quaternion>();
        var keep = new List<Transform>();
        foreach (SkinnedMeshRenderer smr in _p.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            Transform[] bones = smr.bones;
            Matrix4x4[] bind = smr.sharedMesh != null ? smr.sharedMesh.bindposes : null;
            if (bones == null || bind == null || bind.Length != bones.Length) continue;
            var at = new Dictionary<Transform, int>();
            for (int i = 0; i < bones.Length; i++) if (bones[i] != null) at[bones[i]] = i;
            foreach (Transform f in fingers)
            {
                if (keep.Contains(f) || !at.TryGetValue(f, out int fi) || !at.TryGetValue(f.parent, out int pi)) continue;
                // bind pose world rotations, then the finger's relative to its parent
                Quaternion fw = bind[fi].inverse.rotation, pw = bind[pi].inverse.rotation;
                open.Add(Quaternion.Inverse(pw) * fw);
                keep.Add(f);
            }
            if (keep.Count == fingers.Count) break;
        }
        if (keep.Count == 0) { EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} left fingers have no bind pose - left as animated"); return; }
        _lFingers = keep.ToArray();
        _lFingerOpen = open.ToArray();
        EuphoriaPlugin.Log($"Delirium: left hand relaxes off the gun ({_lFingers.Length} finger bones)");
    }
}
