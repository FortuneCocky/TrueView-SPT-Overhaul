using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// Gear arm bones. Some outfits (seen on USEC PMCs: the whole arm chain) bring their own copy of the arm
/// bones, named like the body's, and their sleeves and gloves are skinned to those instead of the
/// skeleton's. The mod drove the skeleton's arm bones, which no visible mesh used: the gun (on that palm)
/// went with the doll while the arms you see stayed in the weapon hold.
///  - Mapping: a mapped bone no mesh is skinned to is swapped for a same-named one that is, when that one
///    is on the same skeleton (under the pelvis).
///  - Twins: skinned arm bones that aren't on the skeleton are paired, by name, with the skeleton's arm
///    bones (collarbone down, twist bones and fingers included) and copied onto them, world position and
///    rotation, parent-first, every frame we drive the arms, and again right before rendering.
/// </summary>
internal partial class NmBody
{
    private readonly List<(Transform src, Transform dst)> _twins = new List<(Transform, Transform)>();

    private static HashSet<Transform> SkinnedBones(Player p)
    {
        var set = new HashSet<Transform>();
        foreach (SkinnedMeshRenderer smr in p.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            Transform[] bones = smr.bones;
            if (bones == null) continue;
            foreach (Transform t in bones) if (t != null) set.Add(t);
        }
        return set;
    }

    /// MapBones, last step: a bone nothing is skinned to swapped for the same-named skinned one on the skeleton.
    private static void PreferSkinned(Player p, Transform[] b)
    {
        Transform pelvis = b[0];
        if (pelvis == null) return;
        HashSet<Transform> skinned;
        try { skinned = SkinnedBones(p); } catch { return; }
        for (int k = 1; k < b.Length; k++)
        {
            if (b[k] == null || skinned.Contains(b[k])) continue;
            foreach (Transform s in skinned)
                if (s.name == b[k].name && s.IsChildOf(pelvis)) { b[k] = s; break; }
        }
    }

    /// Attach: pair every skinned arm bone off the skeleton with the skeleton's bone of the same name.
    private void TwinAttach()
    {
        _twins.Clear();
        if (_p == null || _bones == null) return;
        var src = new Dictionary<string, Transform>();
        foreach (int root in new[] { 13, 17 })
        {
            if (_bones[root] == null) continue;
            foreach (Transform t in _bones[root].GetComponentsInChildren<Transform>(true))
                if (!src.ContainsKey(t.name)) src[t.name] = t;
        }
        if (src.Count == 0) return;
        HashSet<Transform> skinned;
        try { skinned = SkinnedBones(_p); } catch { return; }
        var found = new List<(Transform, Transform, int)>();
        foreach (Transform d in skinned)
        {
            if (!src.TryGetValue(d.name, out Transform s) || s == d) continue;
            if ((_bones[13] != null && d.IsChildOf(_bones[13])) || (_bones[17] != null && d.IsChildOf(_bones[17]))) continue;
            int depth = 0;
            for (Transform a = d.parent; a != null; a = a.parent) depth++;
            found.Add((s, d, depth));
        }
        found.Sort((x, y) => x.Item3.CompareTo(y.Item3)); // parent-first
        foreach (var (s, d, _) in found) _twins.Add((s, d));
        if (_twins.Count > 0)
            EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} wears arms skinned to their own bones ({_twins.Count}, e.g. {_twins[0].dst.name}) - they copy the driven arms");
    }

    /// The driven arms onto the gear's own arm bones.
    private void SyncTwins()
    {
        for (int i = 0; i < _twins.Count; i++)
        {
            var (s, d) = _twins[i];
            if (s != null && d != null) d.SetPositionAndRotation(s.position, s.rotation);
        }
    }
}
