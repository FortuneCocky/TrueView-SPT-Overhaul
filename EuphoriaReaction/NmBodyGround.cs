using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// Solid steps and a steadying hand.
///  - Real floor (SPT): the doll stands on its own copy of the level (the bake), which can sit a few cm
///    above or below the real floor; SPT's ankles go where the doll's are, so his feet hovered or sank.
///    While a doll foot is on the ground, SPT's ankle target is put at the real floor under it plus his
///    own ankle height, eased so a step down or up doesn't pop.
///  - Steadying hand: stumbling next to a wall, rail or crate, the free (left) hand reaches out and
///    braces on it (the engine's grab, justbrace, left hand only: the right keeps the gun), held while he
///    stays close and still unsteady.
/// </summary>
internal partial class NmBody
{
    private const float FloorFixMax = 0.25f;   // m: a bigger gap between bake and real floor is left alone (stairs, edges)
    private readonly float[] _floorFix = new float[Parts];
    private float _ankleH = 0.09f;

    /// Attach: his ankles' height above the floor, standing (the real-floor target keeps it).
    private void GroundAttach()
    {
        System.Array.Clear(_floorFix, 0, _floorFix.Length);
        _ankleH = 0.09f;
        if (_startPose == StartPose.Stand && _bones[3] != null && _bones[6] != null)
            _ankleH = Mathf.Clamp(Mathf.Min(_bones[3].position.y, _bones[6].position.y) - _floorY, 0.04f, 0.16f);
        _steadyUntil = 0f; _steadying = false; _nextSteady = 0f;
    }

    /// Lock pass, before the leg IK: a doll foot on the ground puts SPT's ankle on the real floor.
    private void FeetOnRealFloor()
    {
        bool standing = !_dying && _downSince == 0f && _getUp == null;
        for (int s = 0; s < 2; s++)
        {
            int k = s == 0 ? 3 : 6;
            float want = 0f;
            if (standing && _lockOk[k] && nm_agent_get_part_contact(_agent, k) != 0
                && RayWorld(_lockPivot[k] + Vector3.up * 0.4f, Vector3.down, 1.2f, out RaycastHit g) && g.normal.y > 0.6f)
            {
                float fix = g.point.y + _ankleH - _lockPivot[k].y;
                if (Mathf.Abs(fix) < FloorFixMax) want = fix;
            }
            _floorFix[k] = Mathf.Lerp(_floorFix[k], want, 1f - Mathf.Exp(-Time.deltaTime * 14f));
            if (_lockOk[k]) _lockPivot[k].y += _floorFix[k];
        }
    }

    // ------------------------------------------------------------------ steadying hand

    private const float SteadyReach = 0.75f;   // m from the left shoulder
    private const float SteadyHold = 1.2f;     // s per reach (renewed while he stays unsteady and close)
    private float _steadyUntil, _nextSteady;
    private bool _steadying;
    private static readonly float[] SteadyAngles = { 90f, 50f, 130f, 20f, 160f, 0f }; // deg left of his facing

    /// From BraceTick when not bracing: true while the free hand is on something.
    private bool SteadyTick()
    {
        if (_steadying && Time.time > _steadyUntil) EndSteady();
        if (_inAir) { EndSteady(); return false; } // nothing to steady himself for in mid-air
        if (Time.time < _nextSteady || _bones[13] == null) return _steadying;
        _nextSteady = Time.time + 0.25f;

        nm_agent_get_com(_agent, _bp3, _bv3);
        Vector3 v = ToUnity(_bv3[0], _bv3[1], _bv3[2]); v.y = 0f;
        float tilt = TrunkTilt();
        bool unsteady = tilt > 8f || v.magnitude > 0.35f;
        if (!unsteady) { if (_steadying && Time.time > _steadyUntil - SteadyHold * 0.5f) EndSteady(); return _steadying; }

        Vector3 shoulder = _bones[14] != null ? _bones[14].position : _bones[13].position;
        Part(0, out Vector3 pel, out Quaternion pr);
        Part(11, out Vector3 neck, out _);
        Vector3 fwd = _p.Transform.forward; fwd.y = 0f; fwd.Normalize();
        // look where he's going (or leaning) first, then round his free side
        Vector3 lean = neck - pel; lean.y = 0f;
        Vector3 first = v.sqrMagnitude > 0.1f ? v.normalized : lean.sqrMagnitude > 1e-4f ? lean.normalized : -_p.Transform.right;
        int mask = WorldMask & ~CharacterLayers(_p);
        RaycastHit best = default; bool found = false;
        for (int i = -1; i < SteadyAngles.Length; i++)
        {
            Vector3 dir = i < 0 ? first : Quaternion.AngleAxis(-SteadyAngles[i], Vector3.up) * fwd;
            if (Vector3.Dot(dir, _p.Transform.right) > 0.5f) continue; // the gun side: that hand is busy
            for (float dy = 0f; dy >= -0.4f; dy -= 0.4f)
            {
                if (!Physics.Raycast(shoulder + Vector3.up * dy, dir, out RaycastHit h, SteadyReach, mask, QueryTriggerInteraction.Ignore)) continue;
                if (Mathf.Abs(h.normal.y) > 0.5f) continue; // floor or ceiling
                if (!found || h.distance < best.distance) { best = h; found = true; }
            }
        }
        if (!found) { if (_steadying) EndSteady(); return false; }

        Vector3 n = best.normal; n.y = 0f; n.Normalize();
        Vector3 at = best.point + n * 0.02f;
        // engine: pos2 / normalL is the left hand
        Send("grab", ("start", true), ("justbrace", true), ("useLeft", true), ("useRight", false),
             ("pos2", at), ("normalL", n), ("pos1", at), ("normalR", n),
             ("grabDist", SteadyReach + 0.15f), ("reachAngle", 2.4f), ("grabHoldMaxTimer", SteadyHold));
        if (!_steadying)
            EuphoriaPlugin.LogV($"Delirium: {_p.ProfileId} steadies himself on {best.collider.name} ({best.distance:F2} m, lean {tilt:F0} deg)");
        _steadying = true;
        _steadyUntil = Time.time + SteadyHold;
        return true;
    }

    private void EndSteady()
    {
        if (!_steadying) return;
        _steadying = false;
        _nextSteady = Time.time + 0.4f;
        if (Active && !_bracing) Send("grab", ("start", false));
    }
}
