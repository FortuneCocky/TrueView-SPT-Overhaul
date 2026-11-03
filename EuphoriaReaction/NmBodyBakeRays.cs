using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// The level bake's rays in one batch. A bake casts ~1500 rays (the ground grid and its outer ring, and the
/// wall rays all round), one by one on the main thread, at every takeover and up to 8 rebakes a reaction.
/// They don't depend on each other or on anything the bake adds, so they're all cast first as one
/// RaycastCommand batch (spread over Unity's job workers), and the bake reads each ray's hits in the same
/// order it used to cast them. The same rays, all hits up to the same 64, the same filtering (blockers and
/// characters skipped, the nearest kept): the same bake.
/// </summary>
internal partial class NmBody
{
    private const int BakeMaxHits = 64; // as _rayHits
    private static NativeArray<RaycastCommand> _brCmds;
    private static NativeArray<RaycastHit> _brHits;
    private static readonly List<Vector3> _brFrom = new List<Vector3>(2048), _brDir = new List<Vector3>(2048);
    private static readonly List<float> _brDist = new List<float>(2048);
    private static readonly List<int> _brMask = new List<int>(2048);
    private static bool _brBatched, _brBroken;

    private static void BakeRaysClear()
    {
        _brFrom.Clear(); _brDir.Clear(); _brDist.Clear(); _brMask.Clear();
        _brBatched = false;
    }

    private static int BakeRayAdd(Vector3 from, Vector3 dir, float dist, int mask)
    {
        _brFrom.Add(from); _brDir.Add(dir); _brDist.Add(dist); _brMask.Add(mask);
        return _brFrom.Count - 1;
    }

    /// Casts every ray added since BakeRaysClear, together.
    private static void BakeRaysCast()
    {
        int n = _brFrom.Count;
        if (n == 0 || _brBroken) return;
        try
        {
            if (!_brCmds.IsCreated || _brCmds.Length < n)
            {
                if (_brCmds.IsCreated) _brCmds.Dispose();
                if (_brHits.IsCreated) _brHits.Dispose();
                int cap = Mathf.NextPowerOfTwo(n);
                _brCmds = new NativeArray<RaycastCommand>(cap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _brHits = new NativeArray<RaycastHit>(cap * BakeMaxHits, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            if (Physics.autoSyncTransforms) Physics.SyncTransforms(); // what each Physics.Raycast did for itself
            PhysicsScene scene = Physics.defaultPhysicsScene;
            bool backfaces = Physics.queriesHitBackfaces;
            for (int i = 0; i < n; i++)
                _brCmds[i] = new RaycastCommand(scene, _brFrom[i], _brDir[i],
                    new QueryParameters(_brMask[i], false, QueryTriggerInteraction.Ignore, backfaces), _brDist[i]);
            RaycastCommand.ScheduleBatch(_brCmds.GetSubArray(0, n), _brHits.GetSubArray(0, n * BakeMaxHits), 16, BakeMaxHits).Complete();
            _brBatched = true;
        }
        catch (System.Exception e)
        {
            _brBroken = true; // one ray at a time from now on, as before
            EuphoriaPlugin.Log("Delirium: batched bake rays unavailable, casting one by one: " + e.Message);
        }
    }

    /// Ray `i`'s nearest hit that isn't a character or an invisible blocker (GroundRay / RayWorld).
    private static bool BakeRayHit(int i, out RaycastHit best)
    {
        if (!_brBatched)
        {
            if (_brDir[i] == Vector3.down && _brDist[i] == 8f) return GroundRay(_brFrom[i], _brMask[i], out best);
            return RayWorld(_brFrom[i], _brDir[i], _brDist[i], out best);
        }
        best = default;
        float bd = float.MaxValue;
        bool any = false;
        int at = i * BakeMaxHits;
        for (int k = 0; k < BakeMaxHits; k++)
        {
            RaycastHit h = _brHits[at + k];
            Collider c = h.collider;
            if (c == null) break; // the first empty result ends this ray's hits
            if (h.distance >= bd) continue;
            if (IsBlocker(c)) continue;
            if (OnPlayer(c)) continue;
            best = h; bd = h.distance; any = true;
        }
        return any;
    }
}
