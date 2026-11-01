using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// The aim pass's twist base from one shared pose. Each aimed bone starts from a rotation on its parent
/// before it's turned to point like the doll; only the roll about its own length survives the aim, so that
/// start sets every bone's twist. It was the bone's rotation at the takeover: a bot taken mid-run, mid-aim
/// or mid-reload kept that moment's roll (a forearm turned over on the grip, a spine wound into the aim)
/// for the whole reaction, and every bot differently.
///
/// Now it's the rig's rest pose, the pose the body mesh was skinned in, read from the mesh's bind poses:
/// the same for every bot on the skeleton, with neutral rolls (forearms and wrists untwisted, spine
/// straight). A bone the mesh doesn't skin goes through its parent with its own current local rotation;
/// one the rest pose can't be found for at all keeps its takeover rotation.
/// </summary>
internal partial class NmBody
{
    private static readonly Dictionary<Mesh, Matrix4x4[]> _bindCache = new Dictionary<Mesh, Matrix4x4[]>();
    private readonly Dictionary<Transform, Quaternion> _restWorld = new Dictionary<Transform, Quaternion>();

    private static bool RestTwist => EuphoriaPlugin.Instance?.RestTwist.Value ?? true;

    /// After the takeover capture: the twist base of every aimed bone from the rest pose.
    private void RestTwistAttach()
    {
        if (!RestTwist || _p == null) return;
        _restWorld.Clear();
        SkinnedMeshRenderer best = null;
        int bestHits = 0;
        try
        {
            // the renderer that skins the most of our bones: one renderer, one consistent rest space
            foreach (SkinnedMeshRenderer smr in _p.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Transform[] bones = smr.bones;
                if (bones == null || smr.sharedMesh == null) continue;
                int hits = 0;
                foreach (var (bone, _) in LockOrder)
                    if (_bones[bone] != null && System.Array.IndexOf(bones, _bones[bone]) >= 0) hits++;
                if (hits > bestHits) { bestHits = hits; best = smr; }
            }
            if (best == null) return;
            if (!_bindCache.TryGetValue(best.sharedMesh, out Matrix4x4[] bind)) _bindCache[best.sharedMesh] = bind = best.sharedMesh.bindposes;
            Transform[] bs = best.bones;
            for (int i = 0; i < bs.Length && i < bind.Length; i++)
                if (bs[i] != null && !_restWorld.ContainsKey(bs[i])) _restWorld[bs[i]] = bind[i].inverse.rotation;
        }
        catch (System.Exception e) { EuphoriaPlugin.Log("Delirium: rest pose: " + e.Message); return; }

        float worst = 0f; int used = 0;
        foreach (var (bone, child) in LockOrder)
        {
            Transform b = _bones[bone];
            if (b == null || b.parent == null || !_restWorld.ContainsKey(b)) continue;
            Quaternion local = Quaternion.Inverse(RestWorld(b.parent, 0)) * _restWorld[b];
            if (bone <= 6) local = LegRestTwist(bone, child, local, _lockRest[bone]); // a leg rolled by the outfit mesh
            worst = Mathf.Max(worst, Quaternion.Angle(local, _lockRest[bone]));
            _lockRest[bone] = local;
            used++;
        }
        // the free hands' wrist limit is measured from the same pose
        foreach (int k in new[] { 16, 20 })
        {
            Transform b = _bones[k];
            if (b != null && b.parent != null && _restWorld.ContainsKey(b)) _lockRest[k] = Quaternion.Inverse(RestWorld(b.parent, 0)) * _restWorld[b];
        }
        EuphoriaPlugin.LogV($"Delirium: {_p.ProfileId} twist from the rest pose ({used} bones, {bestHits} on '{best.name}', up to {worst:F0} deg from the takeover pose)");
    }

    /// A transform's rotation in the rest pose: skinned -> its bind pose; otherwise its parent's rest
    /// rotation with its own current local rotation on top.
    private Quaternion RestWorld(Transform t, int depth)
    {
        if (_restWorld.TryGetValue(t, out Quaternion q)) return q;
        if (t.parent == null || depth > 32) return t.rotation;
        q = RestWorld(t.parent, depth + 1) * t.localRotation;
        _restWorld[t] = q;
        return q;
    }
}
