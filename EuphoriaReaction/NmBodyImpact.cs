using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// Impacts: when a body part slams into the world (a wall, a crate, a rail) faster than
/// <see cref="ImpactSpeed"/>, the body gets knocked back off it and flinches away, instead of
/// the contact just soaking the speed up and leaving him sliding along the surface.
/// Only horizontal speed counts (landing on the floor isn't an impact) and feet/calves are left
/// out (they touch the ground on every step). One impact per <see cref="ImpactCooldown"/>.
/// </summary>
internal partial class NmBody
{
    private const float ImpactSpeed = 2.5f;     // m/s into the surface
    private const float ImpactCooldown = 0.4f;  // s
    private const float ImpactBase = 15f;       // N*s at the threshold
    private const float ImpactPerMs = 12f;      // N*s per m/s above it
    private const float ImpactMax = 60f;        // N*s cap (about two heavy bullets)

    private readonly bool[] _contactPrev = new bool[Parts];
    private readonly Vector3[] _preVel = new Vector3[Parts];
    private float _nextImpact;

    private static bool ImpactPart(int k) => k != 2 && k != 3 && k != 5 && k != 6;

    /// Before stepping: each part's velocity (the contact will have eaten it afterwards).
    private void ImpactBefore()
    {
        for (int k = 0; k < Parts; k++)
        {
            nm_agent_get_part_velocity(_agent, k, _f3, _g3);
            _preVel[k] = ToUnity(_f3[0], _f3[1], _f3[2]);
        }
    }

    /// After stepping: a part that just touched something at speed knocks the body back.
    private void ImpactAfter()
    {
        int hitPart = -1;
        float hitSpeed = 0f;
        for (int k = 0; k < Parts; k++)
        {
            bool contact = nm_agent_get_part_contact(_agent, k) != 0;
            bool fresh = contact && !_contactPrev[k];
            _contactPrev[k] = contact;
            if (!fresh || !ImpactPart(k)) continue;
            Vector3 v = _preVel[k]; v.y = 0f;
            if (v.magnitude > hitSpeed) { hitSpeed = v.magnitude; hitPart = k; }
        }
        if (hitPart < 0 || hitSpeed < ImpactSpeed || Time.time < _nextImpact) return;
        _nextImpact = Time.time + ImpactCooldown;

        Part(hitPart, out Vector3 at, out _);
        Vector3 into = _preVel[hitPart]; into.y = 0f; into.Normalize();
        float ns = Mathf.Min(ImpactBase + (hitSpeed - ImpactSpeed) * ImpactPerMs, ImpactMax);
        ToNm(at, _f3);
        ToNm(-into * ns + Vector3.up * ns * 0.1f, _g3); // back off the surface, a touch of lift
        nm_agent_apply_push(_agent, _f3, _g3);
        if (!_dying && _downSince == 0f && _startPose != StartPose.Prone && !_inAir) // the flinch starts the balancer: not mid-air
        {
            ToNm(at, _f3);
            nm_agent_task_flinch(_agent, _world, "*", _f3); // flinch away from what he hit
        }
        _lastHitAt = Time.time; // an impact is a hit: no "found his feet" right through it
        EuphoriaPlugin.LogV($"Delirium: impact {DevBoneName(hitPart)} at {hitSpeed:F1} m/s - knocked back {ns:F0} N*s");
    }
}
