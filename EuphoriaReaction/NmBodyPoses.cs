using EFT;
using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// Takeovers from any pose. Standing bots work as they always have. Crouched and prone bots are
/// seeded onto their live pose through the rig binding (rotations from the binding, positions
/// built through the doll's own joints, so nothing pops), and that pose becomes the zero pose the
/// muscles hold. Prone bots get no balance task: they start downed and are handed back prone.
/// </summary>
internal partial class NmBody
{
    private StartPose _startPose;
    private RigBinding _bind;
    private float _comH0 = 1f; // COM height above the floor at takeover: the yardstick for "losing balance"

    // Standing bone rotations and pelvis height for the get-up clips. Standing takeovers record the
    // bot's own; crouched/prone ones derive them from the doll's standing build through the binding.
    private readonly Quaternion[] _standRot = new Quaternion[Parts];
    private float _standPelvisY;

    internal StartPose StartedIn => _startPose;

    private static bool IsProne(Player p)
    {
        try { return p?.MovementContext != null && p.MovementContext.IsInPronePose; }
        catch { return false; }
    }

    /// Thresholds that assume a standing body, scaled down for one that started lower.
    private float ComScaled(float standing, float fraction) => Mathf.Min(standing, _comH0 * fraction);

    private void CaptureStandingReference()
    {
        if (_bind == null) return;
        for (int k = 0; k < Parts; k++)
        {
            Part(k, out Vector3 pos, out Quaternion rot);
            if (!_bind.Has[k]) { _standRot[k] = _bones[k] != null ? _bones[k].rotation : Quaternion.identity; continue; }
            Quaternion bone = rot * Quaternion.Inverse(_bind.Q[k]);
            _standRot[k] = bone;
            if (k == 0) _standPelvisY = (pos - bone * _bind.P[0]).y;
        }
    }

    /// Put the doll on the bot's current pose and make it the pose the muscles hold.
    private bool SeedFromBinding()
    {
        if (_bind == null || !LiveTargets(_bind, out Vector3[] tPos, out Quaternion[] tRot, out bool[] tOk)) return false;
        DollGeom g = ReadDollGeom();
        if (g == null) return false;
        SeedPoseFK(_bind, tPos, tRot, tOk, g, out Vector3[] sPos, out Quaternion[] sRot);
        JointReport(_bind, tRot, tOk, sRot, g); // hinge report for joint_report.txt (NmBodyJointReport)

        Vector3 lift = _startPose == StartPose.Prone ? Vector3.up * 0.02f : Vector3.zero; // never start in the floor
        float[] states = new float[Parts * 13], zero = new float[Parts * 7];
        for (int k = 0; k < Parts; k++)
        {
            Quaternion r = sRot[k];
            ToNm(sPos[k] + lift, states, k * 13);
            states[k * 13 + 3] = -r.x; states[k * 13 + 4] = -r.z; states[k * 13 + 5] = -r.y; states[k * 13 + 6] = r.w;
            ToNm(sPos[k] + lift, zero, k * 7);
            zero[k * 7 + 3] = -r.x; zero[k * 7 + 4] = -r.z; zero[k * 7 + 5] = -r.y; zero[k * 7 + 6] = r.w;
        }
        if (nm_agent_set_part_states(_agent, states, Parts) < 0 || nm_agent_set_zero_pose(_agent, zero, Parts) < 0)
        {
            EuphoriaPlugin.Log("Delirium: seeding the doll from the binding failed: " + LastError());
            return false;
        }
        return true;
    }

    /// Taken over in the air: the doll starts in his jump pose (tucked legs, arms where they are) instead
    /// of the standing build, whose straight legs hung below SPT's and left his feet floating over the
    /// doll's. Only the state: the muscles still hold the standing pose, so he reaches for the ground
    /// and lands on extended legs.
    private void SeedAirPose(RigBinding bind)
    {
        if (bind == null || !bind.Valid || !LiveTargets(bind, out Vector3[] tPos, out Quaternion[] tRot, out bool[] tOk)) return;
        DollGeom g = ReadDollGeom();
        if (g == null) return;
        SeedPoseFK(bind, tPos, tRot, tOk, g, out Vector3[] sPos, out Quaternion[] sRot);
        float[] states = new float[Parts * 13];
        for (int k = 0; k < Parts; k++)
        {
            Quaternion r = sRot[k];
            ToNm(sPos[k], states, k * 13);
            states[k * 13 + 3] = -r.x; states[k * 13 + 4] = -r.z; states[k * 13 + 5] = -r.y; states[k * 13 + 6] = r.w;
        }
        if (nm_agent_set_part_states(_agent, states, Parts) < 0)
            EuphoriaPlugin.Log("Delirium: seeding the doll in his jump pose failed: " + LastError());
        else
            EuphoriaPlugin.LogV($"Delirium: {_p.ProfileId} doll seeded in his jump pose ({bind.Source})");
    }

    /// Prone: no balance, no upright assist; the muscles just hold the lying pose.
    private void TuneDown()
    {
        Send("stayUpright", ("start", false));
    }

    /// A solid baked here would sit inside the body (any pose): the standing capsule, or any of the
    /// bot's mapped bones inside the box (padded by a limb's thickness).
    private bool BlocksBody(Vector3 centre, Vector3 half, Quaternion rot, Vector3 feet)
    {
        if (_startPose == StartPose.Stand && HitsCapsule(centre, half, rot, feet, 0.3f)) return true;
        Quaternion inv = Quaternion.Inverse(rot);
        Vector3 pad = half + Vector3.one * 0.12f;
        for (int k = 0; k < Parts; k++)
        {
            if (_bones == null || _bones[k] == null) continue;
            Vector3 l = inv * (_bones[k].position - centre);
            if (Mathf.Abs(l.x) < pad.x && Mathf.Abs(l.y) < pad.y && Mathf.Abs(l.z) < pad.z) return true;
        }
        return false;
    }
}
