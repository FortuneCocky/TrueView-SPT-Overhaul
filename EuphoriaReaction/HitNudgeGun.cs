using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// The gun rides the chest. A nudge turns the spine, but the game places the gun afterwards where the
/// aim and animation put it, and its hand IK pulls the hands back onto it: the body rocked while the gun
/// hung in the air. So the chest's move this frame (animated -> nudged, a turn about the chest) is laid
/// on the gun too, after the game's weapon pass (once at the end of LateUpdate, again before render),
/// and the arms are bent to the moved gun: gun, hands and forearms move as one piece with the chest.
/// What already rode along (a gun or hand the game left where the nudge carried it) isn't moved twice.
/// A limp left arm (heavy nudge) is left hanging.
/// </summary>
internal partial class HitNudge
{
    private const float Same = 5e-4f;      // m: "the same place"
    private const float SameDeg = 0.1f;

    private static readonly List<HitNudge> Riding = new List<HitNudge>();
    private static bool _renderHooked;

    private bool _ride;                    // the chest moved this frame: the late pass carries the gun
    private Vector3 _chestA; private Quaternion _chestRotA;   // chest as animated
    private Vector3 _rideFrom; private Quaternion _rideTurn;  // the chest's move: x -> chestN + turn * (x - chestA)
    private Vector3 _chestN;
    private Transform _gunRoot;
    private Vector3 _gunA, _gunN; private Quaternion _gunRotA, _gunRotN; // the gun before and after the nudge
    private readonly Vector3[] _handN = new Vector3[2];                  // hands after the nudge
    private Vector3 _gunSet, _gunBase; private Quaternion _gunSetRot, _gunBaseRot; // what we put it at, from where
    private bool _gunSetValid;
    private readonly Vector3[] _handSet = new Vector3[2];
    private int _setFrame = -1;

    private static readonly int[][] Arms = { new[] { 14, 15, 16 }, new[] { 18, 19, 20 } }; // left, right

    /// LateUpdate start (after the animator): the chest and gun as animated.
    private void RideBegin()
    {
        _ride = false;
        Transform chest = _bones[10];
        if (chest == null) return;
        _gunRoot = NmBody.B(_p.PlayerBones?.WeaponRoot);
        // a gun nobody re-placed since we moved it last frame: back where it was, or it would creep
        if (_gunRoot != null && _gunSetValid && Near(_gunRoot.position, _gunSet) && Quaternion.Angle(_gunRoot.rotation, _gunSetRot) < SameDeg)
            _gunRoot.SetPositionAndRotation(_gunBase, _gunBaseRot);
        _gunSetValid = false;
        _chestA = chest.position; _chestRotA = chest.rotation;
        if (_gunRoot != null) { _gunA = _gunRoot.position; _gunRotA = _gunRoot.rotation; }
    }

    /// LateUpdate end: how far the nudge moved the chest, and where that carried the gun and hands.
    private void RideEnd()
    {
        Transform chest = _bones[10];
        if (chest == null || _gunRoot == null) return;
        _chestN = chest.position;
        _rideTurn = chest.rotation * Quaternion.Inverse(_chestRotA);
        _rideFrom = _chestA;
        if (Quaternion.Angle(_rideTurn, Quaternion.identity) < 0.05f && Near(_chestN, _chestA)) return;
        _gunN = _gunRoot.position; _gunRotN = _gunRoot.rotation;
        for (int i = 0; i < 2; i++) _handN[i] = _bones[Arms[i][2]] != null ? _bones[Arms[i][2]].position : Vector3.zero;
        _ride = true;
        _setFrame = -1;
        if (!Riding.Contains(this)) Riding.Add(this);
        if (!_renderHooked) { _renderHooked = true; Application.onBeforeRender += BeforeRender; }
    }

    private Vector3 Carry(Vector3 x) => _chestN + _rideTurn * (x - _rideFrom);

    /// After the game's weapon pass: the gun and hands onto the chest's move.
    internal void RideLate()
    {
        if (!_ride || _gunRoot == null || _bones == null || _p == null || !_p.HealthController.IsAlive) return;
        NmBody body = _p.GetComponent<NmBody>();
        if (body != null && body.Busy) { _ride = false; return; }
        bool again = _setFrame == Time.frameCount;
        _setFrame = Time.frameCount;

        // the gun: unless it rode along already (a child of the chest the game left alone), or is still where we put it
        Vector3 g = _gunRoot.position; Quaternion gr = _gunRoot.rotation;
        bool rode = Near(g, _gunN) && Quaternion.Angle(gr, _gunRotN) < SameDeg && !(Near(_gunN, _gunA) && Quaternion.Angle(_gunRotN, _gunRotA) < SameDeg);
        bool ours = again && _gunSetValid && Near(g, _gunSet) && Quaternion.Angle(gr, _gunSetRot) < SameDeg;
        if (!rode && !ours)
        {
            _gunBase = g; _gunBaseRot = gr;
            _gunSet = Carry(g); _gunSetRot = _rideTurn * gr;
            _gunRoot.SetPositionAndRotation(_gunSet, _gunSetRot);
            _gunSetValid = true;
        }

        // the hands: unless they rode along already, or are still where we put them
        for (int i = 0; i < 2; i++)
        {
            if (i == 0 && (_leftArmLimp || _heavy[14] || _heavy[15] || Time.time < _limpUntil[14])) continue;
            Transform up = _bones[Arms[i][0]], fo = _bones[Arms[i][1]], ha = _bones[Arms[i][2]];
            if (up == null || fo == null || ha == null) continue;
            Vector3 h = ha.position;
            if (Near(h, _handN[i]) || (again && Near(h, _handSet[i]))) continue;
            Quaternion handRot = _rideTurn * ha.rotation;
            Vector3 target = Carry(h), pole = _rideTurn * (fo.position - up.position);
            ArmTo(up, fo, ha, target, pole);
            ha.rotation = handRot;
            _handSet[i] = ha.position;
            if (!_handLogged) { _handLogged = true; EuphoriaPlugin.Log($"Delirium: nudge - {(i == 0 ? "left" : "right")} hand bent onto the carried gun (the game's IK had put it back)"); }
        }
        if (!_gunLogged) { _gunLogged = true; EuphoriaPlugin.Log($"Delirium: nudge - gun {(rode ? "rode along with the chest by itself" : "carried with the chest")}"); }
    }

    private static bool _gunLogged, _handLogged;

    /// Two-bone IK: the palm onto `target`, the elbow toward `pole` (from the shoulder).
    private static void ArmTo(Transform up, Transform fo, Transform ha, Vector3 target, Vector3 pole)
    {
        Vector3 s = up.position, e = fo.position, h = ha.position;
        float a = (e - s).magnitude, b = (h - e).magnitude;
        if (a < 1e-3f || b < 1e-3f) return;
        Vector3 toT = target - s;
        float d = Mathf.Clamp(toT.magnitude, Mathf.Abs(a - b) + 0.01f, a + b - 0.005f);
        Vector3 tn = toT.normalized;
        Vector3 n = Vector3.Cross(tn, pole);
        if (n.sqrMagnitude < 1e-6f) return;
        n.Normalize();
        float ang = Mathf.Acos(Mathf.Clamp((a * a + d * d - b * b) / (2f * a * d), -1f, 1f)) * Mathf.Rad2Deg;
        Vector3 e1 = Quaternion.AngleAxis(ang, n) * tn, e2 = Quaternion.AngleAxis(-ang, n) * tn;
        Vector3 ed = Vector3.Dot(e1, pole) >= Vector3.Dot(e2, pole) ? e1 : e2; // keep the elbow on its side
        up.rotation = Quaternion.FromToRotation(e - s, ed) * up.rotation;
        e = fo.position; h = ha.position;
        fo.rotation = Quaternion.FromToRotation(h - e, target - e) * fo.rotation;
    }

    private static bool Near(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < Same * Same;

    private static void BeforeRender()
    {
        for (int i = Riding.Count - 1; i >= 0; i--)
        {
            HitNudge n = Riding[i];
            if (n == null || !n._ride) { Riding.RemoveAt(i); continue; }
            try { n.RideLate(); } catch { }
        }
    }
}

/// The ride's late pass: after every other script's LateUpdate (the game's weapon animation included).
[DefaultExecutionOrder(32000)]
internal class HitNudgeLate : MonoBehaviour
{
    private HitNudge _n;
    private void LateUpdate()
    {
        if (_n == null) _n = GetComponent<HitNudge>();
        _n?.RideLate();
    }
}
