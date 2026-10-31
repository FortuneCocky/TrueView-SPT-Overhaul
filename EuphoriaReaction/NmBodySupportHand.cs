using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// The support hand during a reaction. Physics owns the left arm (it lets go of the gun to flail,
/// brace or clutch a wound), but where it held the gun at takeover is remembered in the gun's own
/// frame. When he recovers (the stance match before the hand-back), the hand reaches back to that
/// exact spot and takes the grip's angle again, so the animation picks up a two-handed hold that is
/// already there instead of the hand teleporting onto the handguard.
/// </summary>
internal partial class NmBody
{
    private const float SupportMaxGap = 0.75f; // m palm to gun at takeover: further = he wasn't holding it two-handed
    private const float SupportReach = 0.35f;  // s for the hand to get back to the gun

    private Vector3 _supportLocal;
    private Quaternion _supportRot;
    private bool _supportGrip;

    /// At takeover (gun in his right hand): where the left hand sits on it.
    private void RecordSupportGrip()
    {
        _supportGrip = false;
        Transform palm = _bones[16];
        if (palm == null || _gun == null) return;
        if ((palm.position - _gun.position).magnitude > SupportMaxGap) return; // one-handed, or reloading/away
        _supportLocal = Quaternion.Inverse(_gun.rotation) * (palm.position - _gun.position);
        _supportRot = Quaternion.Inverse(_gun.rotation) * palm.rotation;
        _supportGrip = true;
    }

    /// LateUpdate while matching, after the gun has been placed: the left palm back onto its grip.
    private bool _matchFromGround;
    private float _reachCut; // when the reach for the gun was called off this match (0 = it wasn't)
    private const float ReachLetGo = 0.15f; // s for the hand to come off the reach

    private void ReachSupportGrip()
    {
        if (!_supportGrip || _gun == null || _gunDropped || _bones[16] == null) return;
        // from the ground (lying, prone, a get-up's end) the hand isn't put back on the gun: reaching for it
        // through a body turning over threw the arm about; the animation takes the hand back after the hand-back
        if (_matchFromGround) return;
        // going over again while recovering: the hand stops reaching for the gun and the doll has it back
        if (_reachCut == 0f && GoingDown)
        {
            _reachCut = Time.time;
            EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} going down while recovering - the hand stops reaching for the gun");
        }
        float w = Mathf.SmoothStep(0f, 1f, (Time.time - _matchStart) / SupportReach);
        if (_reachCut > 0f) w *= 1f - Mathf.Clamp01((Time.time - _reachCut) / ReachLetGo);
        if (w <= 0.001f) return;
        Vector3 target = _gun.position + _gun.rotation * _supportLocal;
        LeftArmIK(target, w);
        Transform palm = _bones[16];
        palm.rotation = Quaternion.Slerp(palm.rotation, _gun.rotation * _supportRot, w);
    }
}
