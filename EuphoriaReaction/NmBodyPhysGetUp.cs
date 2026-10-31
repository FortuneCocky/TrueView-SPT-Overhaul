using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// Physical get-up: the get-up clip plays on the doll instead of over it. The plain get-up let the doll
/// go and laid the clip on SPT's bones, every frame exactly as authored: no weight, nothing touching
/// anything, and it read as canned. Here the doll stays alive through it:
///  - each frame the clip's pose (on SPT's skeleton, as the plain get-up would show it) is mapped onto
///    the doll through the rig binding and set as the pose its muscles pull towards, like the stance match;
///  - every part gets a soft assist towards where the clip has it (muscles alone can't lift a body off the
///    floor), firmest on the pelvis and chest and growing with how far a part has fallen behind, so it
///    rises on the clip's timing but with mass: hands and knees push on the real floor, limbs lag and
///    settle, but nothing is left stretched out or bent backwards while the rest kneels;
///  - the clip starts from how he really lies: the difference to its first frame fades out over the first
///    second instead of snapping, and the clip slows down while the doll is well behind it;
///  - SPT shows a mix of the clip and the doll ("Get-up physics mix") but never strays more than a hand's
///    width from the doll, so it can't kneel or stand somewhere the doll isn't;
///  - he balances (Euphoria's own balance) once he's nearly up, and a hit while he rises throws him back
///    into the reaction from wherever he is.
/// Roll-overs keep the plain path, and so does anything without a usable binding.
/// </summary>
internal partial class NmBody
{
    private bool _pgu, _pguWant, _pguHave, _pguAlive;
    private float _pguStart;
    private RigBinding _pguBind;
    private DollGeom _pguGeom;
    private readonly Vector3[] _pguPos = new Vector3[Parts], _pguTPos = new Vector3[Parts];
    private readonly Quaternion[] _pguRot = new Quaternion[Parts], _pguLocal = new Quaternion[Parts], _pguTRot = new Quaternion[Parts];
    private readonly float[] _pguZero = new float[Parts * 7];
    private readonly float[] _pgP = new float[3], _pgR = new float[4], _pgV = new float[3], _pgW = new float[3];
    // assist share per part: pelvis and chest carry him, the rest only keeps up
    private static readonly float[] AssistShare =
    {
        1f,                    // pelvis
        0.45f, 0.4f, 0.35f,    // left thigh, calf, foot
        0.45f, 0.4f, 0.35f,    // right
        0.8f, 0.9f, 0f, 1f,    // spine (9 unused)
        0.6f, 0.6f,            // neck, head
        0.3f, 0.3f, 0.3f, 0.3f,
        0.3f, 0.3f, 0.3f, 0.3f,
    };
    private const float PguRampIn = 0.3f;
    private const float PguSettle = 1f;       // s the start difference fades over
    private const float PguShowReach = 0.12f; // m SPT's pelvis may be from the doll's
    private bool _pguOffSet;
    private float _pguGap;                    // m the doll's pelvis is behind the clip's
    private readonly Vector3[] _pguOffPos = new Vector3[Parts];
    private readonly Quaternion[] _pguOffRot = new Quaternion[Parts];

    private static bool PhysGetUpOn => EuphoriaPlugin.Instance?.PhysGetUp.Value ?? true;

    /// PlayClip, in place of letting the doll go: keep it and play the clip through it. False = can't.
    private bool PhysGetUpBegin()
    {
        RigBinding b = _bind ?? _standBind;
        if (!Active || _dying || b == null || !b.Valid) return false;
        DollGeom g = ReadDollGeom();
        if (g == null) return false;
        _pguBind = b; _pguGeom = g;
        _pgu = true; _pguHave = false; _pguAlive = false; _pguOffSet = false; _pguGap = 0f;
        _pguStart = Time.time;
        _matching = false; _matchFadeFrom = 0f;
        for (int k = 0; k < Parts; k++) if (_bones[k] != null) _blendRot[k] = _bones[k].rotation;
        _blendPelvis = _bones[0].position;
        StopFallenPose();
        Send("catchFall", ("start", false));
        TuneDown(); // no balance on the floor: the clip's pose and the assist lift him
        return true;
    }

    private void PhysGetUpStop(string why)
    {
        if (!_pgu) return;
        _pgu = false; _pguHave = false;
        _getUp = null;
        EuphoriaPlugin.Log($"Delirium: {_p?.ProfileId} physical get-up stopped ({why})");
    }

    /// Update, after the physics steps: true while getting up (the rest of the tick is skipped).
    private bool PhysGetUpTick()
    {
        if (!_pgu) return false;
        GetUpClip clip = _getUp;
        if (clip == null) { _pgu = false; return false; }
        if (_lastHitAt > _pguStart)
        {
            PhysGetUpStop("hit");
            TuneAlive();
            return false; // the reaction carries on from wherever he is
        }
        float last = clip.Frames - 1;
        // nearly up: his own balance takes the weight from here
        if (!_pguAlive && _guFrame >= clip.StandFrame * 0.85f)
        {
            _pguAlive = true;
            TuneAlive();
        }
        float speed = Mathf.Max(0.2f, clip.Arms ? EuphoriaPlugin.Instance?.ArmGetUpSpeed.Value ?? 1f : EuphoriaPlugin.Instance?.GetUpSpeed.Value ?? 0.7f);
        bool late = Time.time - _pguStart > last / (clip.Fps * speed) + 3f;
        if (_guFrame >= last || late)
        {
            _pgu = false; _pguHave = false;
            _getUp = null;
            ApplyStance(StartPose.Stand);
            _blendDur = 0.35f;
            EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} physical get-up finished{(late ? " (timed out)" : "")} - back to animation (stand)");
            Release();
        }
        return true;
    }

    /// Update, before the physics steps: the clip's pose (last LateUpdate) as the muscles' target.
    private void PhysGetUpTarget()
    {
        if (!_pgu || !_pguHave) return;
        var tPos = (Vector3[])_pguPos.Clone(); var tRot = (Quaternion[])_pguRot.Clone(); var tOk = new bool[Parts];
        for (int k = 0; k < Parts; k++) tOk[k] = _bones[k] != null;
        if (_pguBind.SpineFitted && !FitSpine(tPos, tRot, tOk, _bones, _pguBind.SpineFrac)) return;
        SeedPoseFK(_pguBind, tPos, tRot, tOk, _pguGeom, out Vector3[] sPos, out Quaternion[] sRot);
        if (!_pguOffSet)
        {
            // how he really lies against the clip's first frame
            _pguOffSet = true;
            for (int k = 0; k < Parts; k++)
            {
                _pguOffPos[k] = Vector3.zero; _pguOffRot[k] = Quaternion.identity;
                if (nm_agent_get_part_transform(_agent, k, _pgP, _pgR) < 0) continue;
                _pguOffPos[k] = ToUnity(_pgP[0], _pgP[1], _pgP[2]) - sPos[k];
                _pguOffRot[k] = ToUnity(_pgR[0], _pgR[1], _pgR[2], _pgR[3]) * Quaternion.Inverse(sRot[k]);
            }
        }
        float f = 1f - Mathf.SmoothStep(0f, 1f, (Time.time - _pguStart) / PguSettle);
        for (int k = 0; k < Parts; k++)
        {
            if (f > 0f)
            {
                sPos[k] += _pguOffPos[k] * f;
                sRot[k] = Quaternion.Slerp(Quaternion.identity, _pguOffRot[k], f) * sRot[k];
            }
            _pguTPos[k] = sPos[k]; _pguTRot[k] = sRot[k];
            Quaternion r = sRot[k];
            ToNm(sPos[k], _pguZero, k * 7);
            _pguZero[k * 7 + 3] = -r.x; _pguZero[k * 7 + 4] = -r.z; _pguZero[k * 7 + 5] = -r.y; _pguZero[k * 7 + 6] = r.w;
        }
        nm_agent_set_zero_pose(_agent, _pguZero, Parts);
    }

    /// After each physics step: every part pulled towards the clip's, position and angle, through its
    /// velocity: the lift the muscles can't give, soft enough that the body's weight and contacts still
    /// show, firmer on a part the further it has fallen behind (up to 3x) so the body stays one shape.
    /// Once he balances the limbs are mostly left to it, so his feet can step.
    private void PhysGetUpAssist()
    {
        if (!_pgu || !_pguHave) return;
        float a = EuphoriaPlugin.Instance?.GetUpAssist.Value ?? 0.6f;
        if (a <= 0f) return;
        for (int k = 0; k < Parts; k++)
        {
            float share = AssistShare[k];
            if (share <= 0f || _bones[k] == null) continue;
            bool core = k == 0 || k == 7 || k == 8 || k == 10;
            if (_pguAlive && !core) share *= 0.3f;
            if (nm_agent_get_part_transform(_agent, k, _pgP, _pgR) < 0) continue;
            nm_agent_get_part_velocity(_agent, k, _pgV, _pgW);
            Vector3 pos = ToUnity(_pgP[0], _pgP[1], _pgP[2]);
            float gap = (_pguTPos[k] - pos).magnitude;
            if (k == 0) _pguGap = gap;
            float s = a * share * (1f + 2f * Mathf.SmoothStep(0f, 1f, (gap - 0.1f) / 0.3f));
            float kp = 90f * s, kd = 2f * Mathf.Sqrt(kp), kr = 70f * s, dr = 2f * Mathf.Sqrt(kr);
            Quaternion rot = ToUnity(_pgR[0], _pgR[1], _pgR[2], _pgR[3]);
            Vector3 v = ToUnity(_pgV[0], _pgV[1], _pgV[2]);
            Vector3 w = -ToUnity(_pgW[0], _pgW[1], _pgW[2]); // angular velocity: the axis swap is a mirror
            v += (kp * (_pguTPos[k] - pos) - kd * v) * Step;
            (_pguTRot[k] * Quaternion.Inverse(rot)).ToAngleAxis(out float ang, out Vector3 axis);
            if (ang > 180f) ang -= 360f;
            Vector3 e = Mathf.Abs(ang) > 0.01f && axis.sqrMagnitude > 0.5f ? axis * (ang * Mathf.Deg2Rad) : Vector3.zero;
            w += (kr * e - dr * w) * Step;
            ToNm(v, _pgV);
            ToNm(-w, _pgW);
            nm_agent_set_part_state(_agent, k, _pgP, _pgR, _pgV, _pgW);
        }
    }

    /// LateUpdate, before the physics drive: pose SPT's bones as the clip has them this frame (the same
    /// retarget the plain get-up shows) and keep that pose; the drive then puts the doll's on top.
    private void PhysGetUpPose()
    {
        GetUpClip clip = _getUp;
        if (!_pgu || clip == null || _dying) return;
        float speed = Mathf.Max(0.2f, clip.Arms ? EuphoriaPlugin.Instance?.ArmGetUpSpeed.Value ?? 1f : EuphoriaPlugin.Instance?.GetUpSpeed.Value ?? 0.7f);
        float wait = 1f - 0.8f * Mathf.SmoothStep(0f, 1f, (_pguGap - 0.2f) / 0.3f); // the doll well behind: the clip slows for it
        _guFrame += Time.deltaTime * clip.Fps * speed * wait;
        float last = clip.Frames - 1;
        float fr = Mathf.Min(_guFrame, last);
        int i0 = (int)fr, i1 = Mathf.Min(i0 + 1, clip.Frames - 1);
        float a = fr - i0;

        Vector3 dp = Vector3.Lerp(clip.P[i0], clip.P[i1], a) - _guP0;
        Vector3 hor = _guF * new Vector3(dp.x, 0f, dp.z) * _guScale;
        Vector3 xz = new Vector3(_guPelvisStart.x + hor.x, 0f, _guPelvisStart.z + hor.z);
        Vector3 step = new Vector3(xz.x - _guLast.x, 0f, xz.z - _guLast.z);
        float dist = step.magnitude;
        if (dist > 1e-4f && RayWorld(new Vector3(_guLast.x, _guPelvisStart.y, _guLast.z), step / dist, dist + 0.25f, out _))
            xz = new Vector3(_guLast.x, 0f, _guLast.z);
        float gy = RayWorld(new Vector3(xz.x, _guPelvisStart.y + 0.5f, xz.z), Vector3.down, 3f, out RaycastHit g) ? g.point.y : _floorY;
        _guGround = Mathf.Lerp(_guGround, gy, Time.deltaTime * 10f);
        float py = _guGround + Mathf.Lerp(clip.P[i0].y, clip.P[i1].y, a) * _guScale;
        Vector3 pelvisTarget = new Vector3(xz.x, py, xz.z);
        _guLast = pelvisTarget;

        Quaternion face = Quaternion.Euler(0f, -_theta * Mathf.Rad2Deg, 0f);
        float endW = clip.StandFrame >= last ? 0f : Mathf.SmoothStep(0f, 1f, (fr - clip.StandFrame) / (last - clip.StandFrame));
        for (int k = 0; k < Parts; k++)
        {
            if (_bones[k] == null) continue;
            int key = KeyOf[k];
            Quaternion rc = Quaternion.Slerp(clip.R[i0][key], clip.R[i1][key], a);
            Quaternion target = _guF * (rc * Quaternion.Inverse(GetUpLibrary.Ref[key])) * face * _standRot[k];
            target = ArmTarget(k, key, rc, target, endW);
            _guTarget[k] = target;
            if (k == 0) _bones[0].SetPositionAndRotation(pelvisTarget, target);
            else _bones[k].rotation = target;
        }
        for (int k = 0; k < Parts; k++)
            if (_bones[k] != null) { _pguPos[k] = _bones[k].position; _pguRot[k] = _bones[k].rotation; _pguLocal[k] = _bones[k].localRotation; }
        _pguHave = true;
    }

    /// LateUpdate, after the physics drive: what SPT shows, the doll's pose mixed with the clip's. The clip
    /// eases in over the first moments and takes over entirely over the end of the clip.
    private void PhysGetUpShow()
    {
        GetUpClip clip = _getUp;
        if (!_pgu || !_pguHave || clip == null || _dying) return;
        float mix = Mathf.Clamp01(EuphoriaPlugin.Instance?.GetUpPhysMix.Value ?? 0.5f);
        float cw = 1f - mix;
        float last = clip.Frames - 1;
        float endFrom = Mathf.Min(clip.StandFrame, last * 0.85f);
        if (_guFrame > endFrom) cw = Mathf.Lerp(cw, 1f, Mathf.SmoothStep(0f, 1f, (_guFrame - endFrom) / Mathf.Max(1f, last - endFrom)));
        cw *= Mathf.SmoothStep(0f, 1f, (Time.time - _pguStart) / PguRampIn);
        // never more than a hand's width from the doll: SPT can't rise from a pose or spot the doll isn't in
        float apart = (_pguPos[0] - _bones[0].position).magnitude;
        if (apart * cw > PguShowReach) cw = PguShowReach / apart;
        if (cw <= 0.001f) return;
        _bones[0].SetPositionAndRotation(Vector3.Lerp(_bones[0].position, _pguPos[0], cw), Quaternion.Slerp(_bones[0].rotation, _pguRot[0], cw));
        for (int k = 1; k < Parts; k++)
            if (_bones[k] != null) _bones[k].localRotation = Quaternion.Slerp(_bones[k].localRotation, _pguLocal[k], cw);
        GroundClamp();
    }
}
