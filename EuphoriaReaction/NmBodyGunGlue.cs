using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// The held gun glued to the palm by its own grip point (EFT's right-hand IK marker, where the game puts
/// the palm; else the pistol grip once the grip is known), not by where the gun happened to sit at the
/// takeover. Each late pass the whole gun is moved so that point is on the palm again, so a bad offset at
/// the takeover (hand off the grip, the gun picked up mid-reaction) or the gun's own animation sliding
/// the model under its root can't leave it hanging off the hand. The angle in the hand comes from the
/// live grip, else this gun's learned grip, else the one learned across all guns (GunMarkers).
/// </summary>
internal partial class NmBody
{
    private Transform _gripPt;
    private float _gripGate, _gripOff0;   // how close counts as on it; the hand's distance from it at the takeover
    private Vector3 _palmOnGripPos;       // the palm in the grip point's frame
    private Quaternion _palmOnGripRot;
    private Vector3 _gripInGun0;          // the grip point in the gun's frame at the takeover
    private float _glueSlide;             // m: the most the gun's own animation moved its grip point
    private bool _glue;

    /// GunToHand, before anything moves the gun: its grip point, and how far the hand is from it.
    private void GlueFind()
    {
        _glue = false; _glueSlide = 0f;
        _gripPt = GunMarkers.GripMount(_gun, out _gripGate);
        _gripOff0 = _gripPt != null && _bones[20] != null ? (_bones[20].position - _gripPt.position).magnitude : -1f;
    }

    /// GunToHand, once the gun is on the hand (live grip or learned marker): glued by its grip point from now on.
    private void GlueAttach(bool gripKnown)
    {
        Transform palm = _bones[20];
        if (_gripPt == null || palm == null) { EuphoriaPlugin.Log("Delirium: gun has no grip point - held by its takeover offset"); return; }
        Quaternion inv = Quaternion.Inverse(_gripPt.rotation);
        bool ik = _gripGate == GunMarkers.IkGate;
        bool onGrip = (palm.position - _gripPt.position).magnitude <= _gripGate;
        string from;
        if (onGrip || gripKnown)
        {
            _palmOnGripPos = inv * (palm.position - _gripPt.position);
            _palmOnGripRot = inv * palm.rotation;
            from = _gripOff0 <= _gripGate ? "his live grip" : "this gun's learned grip";
        }
        else if (ik && GunMarkers.TryGetAnyIk(out Vector3 pos, out Quaternion rot))
        {
            _palmOnGripPos = pos; _palmOnGripRot = rot;
            from = "the grip learned across all guns";
        }
        else if (ik)
        {
            _palmOnGripPos = Vector3.zero; // the game's IK puts the palm right on it
            _palmOnGripRot = inv * palm.rotation;
            from = "the angle it was held at (no grip learned yet)";
        }
        else { EuphoriaPlugin.Log($"Delirium: gun grip {_gripPt.name} not learned yet - held by its takeover offset"); return; }
        _gripInGun0 = Quaternion.Inverse(_gun.rotation) * (_gripPt.position - _gun.position);
        _glue = true;
        GlueApply();
        EuphoriaPlugin.Log($"Delirium: gun glued by its {_gripPt.name}, hand {_gripOff0 * 100f:F0} cm off it at the takeover, angle from {from}");
    }

    /// The gun moved so its grip point is on the palm (the gun's own animation of that point included).
    private void GlueApply()
    {
        Transform palm = _bones[20];
        if (_gripPt == null) { _glue = false; return; } // gone with its model: the plain lock holds it
        Quaternion gunRot = _gun.rotation, toGun = Quaternion.Inverse(gunRot);
        Quaternion gripInGun = toGun * _gripPt.rotation;
        Vector3 at = toGun * (_gripPt.position - _gun.position);
        float slide = (at - _gripInGun0).magnitude;
        if (slide > _glueSlide) _glueSlide = slide;
        Quaternion gripRot = palm.rotation * Quaternion.Inverse(_palmOnGripRot);
        Vector3 gripPos = palm.position - gripRot * _palmOnGripPos;
        Quaternion rot = gripRot * Quaternion.Inverse(gripInGun);
        Vector3 pos = gripPos - rot * at;
        _gun.SetPositionAndRotation(pos, rot);
        _gunHeldRot = rot;
        _gripLocal = Quaternion.Inverse(rot) * (palm.position - pos); // the hand-back blend starts from the glued grip
    }

    private void GlueEnd()
    {
        if (_glue && _glueSlide > 0.01f)
            EuphoriaPlugin.Log($"Delirium: the gun's own animation slid its grip up to {_glueSlide * 100f:F0} cm - kept on the hand");
        _glue = false; _gripPt = null;
    }
}
