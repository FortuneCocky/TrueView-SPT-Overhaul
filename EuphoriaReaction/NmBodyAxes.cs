using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// His body axes in the doll's pelvis and chest frames, taken at the takeover: forward, belly and up
/// (prone: belly down, head forward). The face-down test, the settle and the stance match read them.
/// (Kept from the old leg diagnostic, which is gone.)
/// </summary>
internal partial class NmBody
{
    private Vector3 _pelvisFwdLocal, _pelvisBellyLocal, _chestBellyLocal, _pelvisUpLocal, _chestUpLocal;
    private bool _bellyAxesOk;
    private float _watchUntil; // after a hand-back: keep ticking until then

    private void BodyAxesAttach()
    {
        _pelvisFwdLocal = Quaternion.Inverse(_partRot0[0]) * _p.Transform.forward;
        bool prone = _startPose == StartPose.Prone;
        Vector3 belly = prone ? Vector3.down : _p.Transform.forward, bodyUp = prone ? _p.Transform.forward : Vector3.up;
        _pelvisBellyLocal = Quaternion.Inverse(_partRot0[0]) * belly;
        _chestBellyLocal = Quaternion.Inverse(_partRot0[10]) * belly;
        _pelvisUpLocal = Quaternion.Inverse(_partRot0[0]) * bodyUp;
        _chestUpLocal = Quaternion.Inverse(_partRot0[10]) * bodyUp;
        _bellyAxesOk = true;
    }
}
