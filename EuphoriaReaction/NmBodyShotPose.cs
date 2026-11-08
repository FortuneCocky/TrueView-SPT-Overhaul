using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// Your rounds hit the body you see. The game traces your shots in Player.ArmsLateUpdate (and the rounds still in
/// flight in BallisticsCalculator.UpdateShots) with every collider synced to the bones as the animation left them,
/// before this mod's LateUpdate draws the doll: a bot knocked onto his back kept his hitboxes standing where the
/// animation had him, and rounds went through the body on the floor. Around those two calls every bot in a
/// reaction, get-up or blend back gets the pose he was last drawn in on the bones his hitboxes hang from, and the
/// animation's pose back after (Patches/ShotPose).
/// </summary>
internal partial class NmBody
{
    private Transform[] _shotBones;                // every bone from his hitboxes up to (not) his root
    private Vector3[] _shotDrawnPos, _shotLivePos;
    private Quaternion[] _shotDrawnRot, _shotLiveRot;
    private int _shotFrame = -9;                   // the frame the drawn pose was taken in
    private static readonly List<NmBody> _shotPosed = new List<NmBody>(8);
    private static int _shotDepth;                 // the two wraps can nest: posed at the first, put back at the last
    private static bool _shotLogged;

    /// End of each drawn frame (LateUpdate, then the late and before-render passes): the pose he is shown in.
    private void ShotPoseCapture()
    {
        if (_p == null || !Busy) return;
        if (_shotBones == null) FindShotBones();
        if (_shotBones.Length == 0) return;
        for (int i = 0; i < _shotBones.Length; i++)
        {
            Transform t = _shotBones[i];
            if (t == null) continue;
            _shotDrawnPos[i] = t.localPosition;
            _shotDrawnRot[i] = t.localRotation;
        }
        _shotFrame = Time.frameCount;
    }

    private void FindShotBones()
    {
        var bones = new List<Transform>();
        var chain = new List<Transform>();
        Transform root = ((Component)_p).transform; // his GameObject root (Player.transform is marked obsolete)
        foreach (BodyPartCollider bpc in _p.GetComponentsInChildren<BodyPartCollider>(true))
        {
            chain.Clear();
            Transform t = bpc.transform;
            for (; t != null && t != root && !bones.Contains(t); t = t.parent) chain.Add(t);
            if (t != null) bones.AddRange(chain); // only a chain that ends in his rig, never the scene above him
        }
        _shotBones = bones.ToArray();
        _shotDrawnPos = new Vector3[_shotBones.Length]; _shotLivePos = new Vector3[_shotBones.Length];
        _shotDrawnRot = new Quaternion[_shotBones.Length]; _shotLiveRot = new Quaternion[_shotBones.Length];
        if (_shotBones.Length == 0) EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} has no hitboxes in his rig - shots see the animation's pose");
    }

    /// Before rounds are traced: every busy bot's hitbox bones in the pose he was last drawn in, the live pose kept.
    internal static void PoseForShots()
    {
        if (_shotDepth++ > 0) return; // posed already (a nested wrap)
        int frame = Time.frameCount;
        for (int i = 0; i < AllBodies.Count; i++)
        {
            NmBody b = AllBodies[i];
            if (b == null || !b.Busy || b._shotBones == null || b._shotBones.Length == 0 || frame - b._shotFrame > 1) continue;
            Transform[] t = b._shotBones;
            for (int k = 0; k < t.Length; k++)
                if (t[k] != null) { b._shotLivePos[k] = t[k].localPosition; b._shotLiveRot[k] = t[k].localRotation; }
            _shotPosed.Add(b); // put back whatever happens below
            for (int k = 0; k < t.Length; k++)
                if (t[k] != null) t[k].SetLocalPositionAndRotation(b._shotDrawnPos[k], b._shotDrawnRot[k]);
        }
        if (_shotPosed.Count > 0 && !_shotLogged)
        {
            _shotLogged = true;
            EuphoriaPlugin.Log($"Delirium: shots traced against reacting bots as drawn ({_shotPosed[0]._shotBones.Length} hitbox bones each)");
        }
    }

    /// After the rounds are traced: the animation's pose back on every bot PoseForShots moved.
    internal static void AfterShots()
    {
        if (_shotDepth == 0 || --_shotDepth > 0) return; // only the outermost wrap puts them back
        for (int i = 0; i < _shotPosed.Count; i++)
        {
            NmBody b = _shotPosed[i];
            Transform[] t = b._shotBones;
            for (int k = 0; k < t.Length; k++)
                if (t[k] != null) t[k].SetLocalPositionAndRotation(b._shotLivePos[k], b._shotLiveRot[k]);
        }
        _shotPosed.Clear();
    }
}
