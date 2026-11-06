using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// Weight: the legs stop, the body doesn't. Modelled on Delirium's fall-down task (falldown.rs).
///  - Trip: moving (stumbling, shoved, dragged), a shin or foot runs into something low in front of it
///    (a pipe, a kerb, debris) and stays behind while the top half has nothing in its way. The balance
///    controller used to just step up onto it and stay upright. Now balance is dropped and, as the fall-down task does
///    when its stagger gives up, the head is pushed on along his way and the caught foot back the same
///    amount: he pitches over the thing his legs hit.
///  - Over a rail: something at his hips, nothing at his chest. The fallOverWall behaviour on that edge (legs
///    lifted over, spine bent over it) with the same push: he flips over instead of stopping at it.
///  - Down steps: going over (tripped or not) with the ground falling away along his way, he gets the engine's
///    rollDownStairs instead of the catch-fall, rolling the way he was going until he comes to rest.
/// </summary>
internal partial class NmBody
{
    private const float TripSpeed = 0.6f;     // m/s of his body before a caught leg trips him
    private const float TripReach = 0.3f;     // m in front of a shin/foot an obstacle counts
    private const float RailReach = 0.4f;     // m in front of the hips
    private const float StairDrop = 0.35f;    // m the ground must fall within StairLook ahead
    private const float StairLook = 1.2f;

    private bool _tripped, _stairRoll;
    private float _tripAt, _stairSlowFor;
    private readonly float[] _tv = new float[3], _tw = new float[3];

    private void TripAttach()
    {
        _tripped = false; _stairRoll = false; _tripAt = 0f; _stairSlowFor = 0f;
    }

    /// Update, alive, after the physics steps.
    private void TripTick()
    {
        if (_stairRoll) { StairRollTick(); return; }
        if (_tripped || _downSince > 0f || _startPose == StartPose.Prone || _getUp != null || _matching) return;
        if (Time.time - _attachAt < 0.2f || TrunkTilt() > 35f) return;

        nm_agent_get_com(_agent, _f3, _g3);
        Vector3 v = ToUnity(_g3[0], _g3[1], _g3[2]); v.y = 0f;
        float speed = v.magnitude;
        if (speed < TripSpeed) return;
        Vector3 dir = v / speed;
        int mask = WorldMask & ~CharacterLayers(_p);
        Part(0, out Vector3 pel, out _);
        Part(10, out Vector3 chest, out _);

        // the top half must be free: a wall at his chest is a brace, not a trip
        if (Physics.Raycast(chest, dir, RailReach + 0.2f, mask, QueryTriggerInteraction.Ignore)) return;

        // over a rail: something at the hips
        if (Physics.Raycast(pel, dir, out RaycastHit rail, RailReach, mask, QueryTriggerInteraction.Ignore)
            && Mathf.Abs(rail.normal.y) < 0.6f)
        {
            Vector3 n = rail.normal; n.y = 0f; n.Normalize();
            Vector3 edge = Vector3.Cross(n, Vector3.up).normalized;
            Send("stopAllBehaviours");
            Send("fallOverWall", ("start", true), ("useSpecifiedPoint", true), ("fallOverPoint", rail.point), ("fallOverEdge", edge),
                 ("maxDistanceFromPelToHitPoint", RailReach + 0.15f), ("magOfForce", 1f), ("bodyTwist", Random.Range(0.3f, 0.8f)));
            BeginTrip(dir, speed, -1, $"goes over {rail.collider.name} at his hips");
            return;
        }

        for (int s = 0; s < 2; s++)
        {
            int calf = s == 0 ? 2 : 5, foot = s == 0 ? 3 : 6;
            if (!WorldTouch(calf) && !WorldTouch(foot)) continue;
            nm_agent_get_part_velocity(_agent, foot, _tv, _tw);
            Vector3 fv = ToUnity(_tv[0], _tv[1], _tv[2]); fv.y = 0f;
            if (Vector3.Dot(fv, dir) > speed * 0.4f) continue; // the leg keeps up with him
            Part(foot, out Vector3 fp, out _);
            Part(calf, out Vector3 cp, out _);
            RaycastHit hit = default; bool blocked = false;
            for (int fi = 0; fi < 2; fi++) // foot, then calf (no array per leg per frame)
            {
                Vector3 from = fi == 0 ? fp + Vector3.up * 0.08f : cp;
                if (!Physics.Raycast(from, dir, out RaycastHit h, TripReach, mask, QueryTriggerInteraction.Ignore)) continue;
                if (Mathf.Abs(h.normal.y) > 0.7f || h.point.y > pel.y - 0.15f) continue; // the floor, or not low
                hit = h; blocked = true; break;
            }
            if (!blocked) continue;
            Send("stopAllBehaviours");
            BeginTrip(dir, speed, foot, $"trips on {hit.collider.name} ({(s == 0 ? "left" : "right")} leg caught {hit.point.y - _floorY:F2} m up)");
            return;
        }
    }

    // touching the level (the engine's own test: bit 0x20 set, 0x08 clear)
    private bool WorldTouch(int k) => (nm_agent_get_part_contact(_agent, k) & 0x28) == 0x20;

    /// Balance off, the head on along his way and the caught foot (or both, over a rail) back: he goes over.
    private void BeginTrip(Vector3 dir, float speed, int foot, string what)
    {
        _tripped = true; _tripAt = Time.time;
        _overSeen = true;       // no false-alarm hand-back out of this
        _catchFallSent = true;  // the catch-fall (or the roll) comes from here
        float m = Mathf.Max(20f, nm_agent_get_mass(_agent));
        Vector3 j = dir * (m * Mathf.Clamp(speed * 0.5f, 0.3f, 0.6f));
        Impulse(12, j);
        if (foot >= 0) Impulse(foot, -j);
        else { Impulse(3, (-dir + Vector3.up) * (j.magnitude * 0.35f)); Impulse(6, (-dir + Vector3.up) * (j.magnitude * 0.35f)); }
        if (foot >= 0 && !StairRollStart(dir))
            Send("catchFall", ("start", true), ("armsStiffness", 10f), ("torsoStiffness", 7f));
        OpenZero("tripped");
        EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} {what}, {speed:F1} m/s");
    }

    private void Impulse(int k, Vector3 j)
    {
        Part(k, out Vector3 at, out _);
        ToNm(at, _f3); ToNm(j, _g3);
        nm_agent_apply_impulse(_agent, k, _f3, _g3);
    }

    /// Going over with steps (or a slope) falling away ahead: roll down them. False when the ground is level.
    private bool StairRollStart(Vector3 dir)
    {
        if (dir.sqrMagnitude < 1e-4f)
        {
            nm_agent_get_com(_agent, _f3, _g3);
            dir = ToUnity(_g3[0], _g3[1], _g3[2]); dir.y = 0f;
            if (dir.sqrMagnitude < 0.04f) return false; // not going anywhere
            dir.Normalize();
        }
        Part(0, out Vector3 pel, out _);
        float g0 = GroundUnder(pel), prev = g0;
        int mask = WorldMask & ~CharacterLayers(_p);
        for (float d = 0.4f; d <= StairLook + 1e-3f; d += 0.4f)
        {
            if (!Physics.Raycast(pel + dir * d + Vector3.up * 0.3f, Vector3.down, out RaycastHit g, 4f, mask, QueryTriggerInteraction.Ignore)) return false;
            if (g.point.y > prev + 0.05f) return false; // goes up again: not a way down
            prev = g.point.y;
        }
        if (g0 - prev < StairDrop) return false;
        _stairRoll = true; _stairSlowFor = 0f; _tripAt = Time.time;
        Send("rollDownStairs", ("start", true), ("customRollDir", dir), ("useCustomRollDir", true));
        EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} rolls down ({g0 - prev:F2} m drop ahead)");
        return true;
    }

    /// Rolling: until he has slowed down for a moment (or 5 s), then the normal down state takes him.
    private void StairRollTick()
    {
        nm_agent_get_com(_agent, _f3, _g3);
        float speed = new Vector3(_g3[0], _g3[1], _g3[2]).magnitude;
        _stairSlowFor = speed < 0.5f ? _stairSlowFor + Time.deltaTime : 0f;
        if (_stairSlowFor < 0.6f && Time.time - _tripAt < 5f) return;
        _stairRoll = false;
        Send("rollDownStairs", ("start", false));
        EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} stops rolling");
    }
}
