using EFT;
using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// Hands up before he hits something. Each frame of a live reaction the body's travel is swept
/// ahead (a chest-high sphere, ~0.6 s of motion) against the level's real colliders:
///  - a wall or the side of an obstacle -> brace: palms flat against it either side of the
///    impact point (the engine's grab with justbrace), so he cushions the hit instead of faceplanting;
///  - an obstacle whose top edge is between his hips and chest (rail, ledge, crate top) -> grab
///    that edge with both hands and hold on for a moment.
/// Off while he's down, dying, or being dragged.
/// </summary>
internal partial class NmBody
{
    private const float BraceLookAhead = 0.6f;  // s of travel swept ahead
    private const float BraceMinSpeed = 0.8f;   // m/s: slower than this he just steps
    private const float BraceRadius = 0.22f;    // m: sweep sphere (about his chest width)
    private const float BraceHold = 1.0f;       // s palms stay on a wall
    private const float GrabHold = 2.5f;        // s he holds an edge
    private const float BraceHandSpread = 0.22f; // m each hand from the impact point

    private readonly float[] _bp3 = new float[3], _bv3 = new float[3];
    private float _nextBrace, _braceUntil;
    private bool _bracing;

    private void BraceTick()
    {
        if (_downSince > 0f || _grabbing || _bones == null || _bones[0] == null) { EndBrace(); EndSteady(); return; }
        if (_bracing && Time.time > _braceUntil) EndBrace();
        if (_bracing) return;
        if (Time.time < _nextBrace) { SteadyTick(); return; } // not heading into anything: the free hand may steady him (NmBodyGround)

        nm_agent_get_com(_agent, _bp3, _bv3);
        Vector3 v = ToUnity(_bv3[0], _bv3[1], _bv3[2]);
        v.y = 0f;
        float speed = v.magnitude;
        if (speed < BraceMinSpeed) { SteadyTick(); return; }
        Vector3 dir = v / speed;

        Vector3 chest = _bones[10] != null ? _bones[10].position : ToUnity(_bp3[0], _bp3[1], _bp3[2]) + Vector3.up * 0.4f;
        float pelvisY = _bones[0].position.y;
        int mask = WorldMask & ~CharacterLayers(_p);
        if (!Physics.SphereCast(chest, BraceRadius, dir, out RaycastHit hit, speed * BraceLookAhead + 0.45f, mask, QueryTriggerInteraction.Ignore))
        { SteadyTick(); return; }
        Vector3 n = hit.normal; n.y = 0f;
        if (n.sqrMagnitude < 0.1f) { SteadyTick(); return; } // a floor or ceiling, not something to put hands on
        n.Normalize();
        Vector3 left = Vector3.Cross(Vector3.up, n).normalized * BraceHandSpread; // n faces him: up x n = his left

        // a top edge within hand reach (rail, ledge, crate top)? look down just behind the face
        bool edge = RayWorld(hit.point - n * 0.12f + Vector3.up * 1.0f, Vector3.down, 1.6f, out RaycastHit top)
                    && top.normal.y > 0.6f && top.point.y > pelvisY + 0.05f && top.point.y < chest.y + 0.35f;
        if (edge)
        {
            Vector3 at = top.point + n * 0.04f; // the near lip
            // engine: pos1 is the right hand, pos2 the left; linegrab slides each hand along pos1-pos2
            Send("grab", ("start", true), ("useLeft", true), ("useRight", true),
                 ("pos1", at - left), ("pos2", at + left), ("normalL", Vector3.up), ("normalR", Vector3.up),
                 ("linegrab", true), ("grabDist", 1.2f), ("reachAngle", 2.6f), ("dontLetGo", false),
                 ("grabHoldMaxTimer", GrabHold), ("pullUpTime", 0f));
            _braceUntil = Time.time + GrabHold;
            EuphoriaPlugin.LogV($"Delirium: {_p.ProfileId} grabs an edge {top.point.y - pelvisY:+0.00;-0.00} m from his hips");
        }
        else
        {
            Vector3 at = new Vector3(hit.point.x, Mathf.Clamp(hit.point.y, pelvisY + 0.2f, chest.y + 0.1f), hit.point.z) + n * 0.02f;
            Send("grab", ("start", true), ("justbrace", true), ("useLeft", true), ("useRight", true),
                 ("pos1", at - left), ("pos2", at + left), ("normalL", n), ("normalR", n),
                 ("grabDist", 1.0f), ("reachAngle", 2.8f), ("grabHoldMaxTimer", BraceHold));
            _braceUntil = Time.time + BraceHold;
            EuphoriaPlugin.LogV($"Delirium: {_p.ProfileId} braces against {hit.collider.name} ({hit.distance:F2} m ahead at {speed:F1} m/s)");
        }
        _bracing = true;
        _steadying = false; // the brace's grab replaces the steadying hand's
        _nextBrace = _braceUntil + 0.5f;
    }

    private void EndBrace()
    {
        if (!_bracing) return;
        _bracing = false;
        if (Active) Send("grab", ("start", false));
    }
}
