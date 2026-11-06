using System.Collections.Generic;
using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// The left hand lets go. While the doll owns him the left hand is off the gun (flailing, bracing, holding
/// a wound), but its fingers kept the animation's grip, curled round a handguard that isn't there. They
/// ease toward a relaxed hand: the character's own bind pose (fingers open) with a little of the grip's
/// curl kept, so it reads as a loose, resting hand. Eased in over a quarter second and back out as he's
/// handed back to the animation and the hand returns to the gun.
///
/// Dead hands go limp. EFT's corpse ragdoll has no bodies for the hands, so after the hand-back nothing
/// moves a palm or a finger again: every corpse kept the hands he died with, the wrist bent as far as the
/// limp doll's hand had flopped (60 deg) and the fingers still curled round the gun, a claw. As he dies
/// both free hands ease over half a second to a loose hand (a little of the curl he died with) and a wrist
/// near straight (DeadWristTurn), and that is what the corpse keeps.
/// </summary>
internal partial class NmBody
{
    private const float FingerCurlKept = 0.35f; // how much of the grip's curl a relaxed hand keeps
    private const float FingerEase = 4f;        // 1/s
    private const float DeadHandEase = 0.5f;    // s for a dead hand to go limp
    private const float DeadWristTurn = 20f;    // deg a dead hand may turn from a straight wrist (alive: WristTurnMax)

    private readonly Transform[][] _fingers = new Transform[2][];       // 0 left, 1 right
    private readonly Quaternion[][] _fingerOpen = new Quaternion[2][];  // each finger bone's bind-pose local rotation
    private readonly Quaternion[][] _deadFrom = new Quaternion[2][], _deadTarget = new Quaternion[2][];
    private bool _fingersTried;
    private float _fingerW;
    private int _fingerFrame = -1;

    // how limp his hands are: 0 alive, 1 half a second after he died
    private float DeadHandW => _dying ? Mathf.SmoothStep(0f, 1f, (Time.time - _dyingSince) / DeadHandEase) : 0f;
    private bool RightHandFree => _gun == null || _gunDropped;

    /// Late pass: the left fingers toward relaxed while the reaction holds him; both go limp when he dies.
    private void RelaxLeftFingers()
    {
        if (_fingerFrame == Time.frameCount) return; // before-render pass: already relaxed this frame
        _fingerFrame = Time.frameCount;
        if (!_fingersTried) { _fingersTried = true; FindFingers(0); FindFingers(1); }
        if (_dying)
        {
            if (!Active) return; // handed back: the corpse keeps the hands we left
            DeadFingers(0);
            if (RightHandFree) DeadFingers(1);
            return;
        }
        _deadTarget[0] = _deadTarget[1] = null;
        if (_fingers[0] == null) return;
        _fingerW = Mathf.MoveTowards(_fingerW, Active ? 1f : 0f, Time.deltaTime * FingerEase);
        ApplyFingers();
    }

    private void ApplyFingers()
    {
        Transform[] f = _fingers[0];
        if (f == null || _fingerW <= 0.001f) return;
        for (int i = 0; i < f.Length; i++)
        {
            if (f[i] == null) continue;
            Quaternion relaxed = Quaternion.Slerp(_fingerOpen[0][i], f[i].localRotation, FingerCurlKept);
            f[i].localRotation = Quaternion.Slerp(f[i].localRotation, relaxed, _fingerW);
        }
    }

    /// A dead hand's fingers, from the curl he died with to a loose hand. Both ends are fixed when the hand
    /// goes limp: the animation no longer poses a dead man's fingers, so a target taken from them each frame
    /// would keep opening them.
    private void DeadFingers(int s)
    {
        Transform[] f = _fingers[s];
        if (f == null) return;
        if (_deadTarget[s] == null)
        {
            // the left one is already relaxed as far as _fingerW took it
            float keep = s == 0 ? Mathf.Lerp(FingerCurlKept, 1f, _fingerW) : FingerCurlKept;
            _deadFrom[s] = new Quaternion[f.Length];
            _deadTarget[s] = new Quaternion[f.Length];
            for (int i = 0; i < f.Length; i++)
            {
                if (f[i] == null) continue;
                _deadFrom[s][i] = f[i].localRotation;
                _deadTarget[s][i] = Quaternion.Slerp(_fingerOpen[s][i], f[i].localRotation, keep);
            }
        }
        float w = DeadHandW;
        for (int i = 0; i < f.Length; i++)
            if (f[i] != null) f[i].localRotation = Quaternion.Slerp(_deadFrom[s][i], _deadTarget[s][i], w);
    }

    /// End of the drive, before the arm pose is kept for the late pass: a dead free hand's wrist within
    /// DeadWristTurn of straight (the rest pose's angle on the forearm), narrowing from a live wrist's reach.
    private void LimpDeadWrists()
    {
        float w = DeadHandW;
        if (w <= 0f) return;
        float max = Mathf.Lerp(WristTurnMax, DeadWristTurn, w);
        foreach (int k in new[] { 16, 20 })
        {
            Transform hand = _bones[k];
            if (hand == null || (k == 20 && !RightHandFree)) continue;
            hand.localRotation = Quaternion.RotateTowards(_lockRest[k], hand.localRotation, max);
        }
    }

    /// A hand's finger bones and their bind-pose local rotations, from the body's skinned meshes.
    private void FindFingers(int s)
    {
        Transform palm = _bones != null ? _bones[s == 0 ? 16 : 20] : null;
        string side = s == 0 ? "left" : "right";
        if (palm == null) return;
        var fingers = new List<Transform>();
        foreach (Transform t in palm.GetComponentsInChildren<Transform>(true))
            if (t != palm && t.name.IndexOf("Digit", System.StringComparison.OrdinalIgnoreCase) >= 0) fingers.Add(t);
        if (fingers.Count == 0) { EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} no {side} finger bones under {palm.name}"); return; }
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
        if (keep.Count == 0) { EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} {side} fingers have no bind pose - left as animated"); return; }
        _fingers[s] = keep.ToArray();
        _fingerOpen[s] = open.ToArray();
        EuphoriaPlugin.Log(s == 0 ? $"Delirium: left hand relaxes off the gun ({keep.Count} finger bones)"
                                  : $"Delirium: right hand goes limp when he dies ({keep.Count} finger bones)");
    }
}
