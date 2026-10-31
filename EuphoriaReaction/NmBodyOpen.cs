using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// Open limbs once he goes over. At the takeover the doll's muscles are given the bot's animated pose to
/// hold (the "zero pose"): for a crouched bot holding a gun that is deep-bent knees and folded elbows. Kept
/// through the fall and on the ground, the muscles pulled the limbs back into that shape against a body
/// lying any way at all, and the legs and arms curled up (knees to 148 deg, past the doll's own limit).
/// Since SPT follows the doll exactly, that curl is what you saw.
///
/// When he goes over (catching the fall) and when he's down, the limbs' held pose is swapped for an open
/// one: each limb part keeps the doll's build pose relative to its parent (legs near straight, arms down
/// with a soft elbow), hung off the torso as it is right now. The torso, neck and head keep holding what
/// they held. The stance match and the get-ups set their own pose afterwards as before.
/// </summary>
internal partial class NmBody
{
    // limb parts, parent-first, with their parent part
    private static readonly (int part, int parent)[] OpenLimbs =
    {
        (1, 0), (2, 1), (3, 2), (4, 0), (5, 4), (6, 5),          // legs
        (13, 10), (14, 13), (15, 14), (16, 15),                   // left arm
        (17, 10), (18, 17), (19, 18), (20, 19),                   // right arm
    };

    private readonly float[] _openZero = new float[Parts * 7];
    private readonly Quaternion[] _openRot = new Quaternion[Parts];
    private readonly Quaternion[] _buildRot = new Quaternion[Parts];
    private bool _buildRotOk;

    private void OpenAttach() => _buildRotOk = false;

    /// The muscles hold open legs and arms from now on (the torso keeps what it held).
    private void OpenZero(string why)
    {
        if (!Active || _build == System.IntPtr.Zero) return;
        if (!_buildRotOk)
        {
            for (int k = 0; k < Parts; k++)
            {
                if (nm_build_part_frame(_build, k, _f3, _q4) < 0) return;
                _buildRot[k] = ToUnity(_q4[0], _q4[1], _q4[2], _q4[3]);
            }
            _buildRotOk = true;
        }
        // base: where every part is now
        for (int k = 0; k < Parts; k++)
        {
            Part(k, out Vector3 p, out Quaternion r);
            _openRot[k] = r;
            ToNm(p, _openZero, k * 7);
        }
        // limbs: the build's own angle on the parent, chained down from the torso as it is
        foreach (var (k, parent) in OpenLimbs)
            _openRot[k] = _openRot[parent] * (Quaternion.Inverse(_buildRot[parent]) * _buildRot[k]);
        for (int k = 0; k < Parts; k++)
        {
            Quaternion r = _openRot[k];
            _openZero[k * 7 + 3] = -r.x; _openZero[k * 7 + 4] = -r.z; _openZero[k * 7 + 5] = -r.y; _openZero[k * 7 + 6] = r.w;
        }
        if (nm_agent_set_zero_pose(_agent, _openZero, Parts) < 0) EuphoriaPlugin.Log("Delirium: open pose failed: " + LastError());
        else EuphoriaPlugin.LogV($"Delirium: {_p.ProfileId} limbs open ({why})");
    }
}
