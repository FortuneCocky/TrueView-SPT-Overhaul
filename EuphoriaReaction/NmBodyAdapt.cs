using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// The reaction reads where he is. At each hit (not prone, not down), the way the shot pushes him is
/// checked: a wall or crate within reach behind him -> the balancerCollisionsReaction behaviour (he meets it,
/// braces on it, and slumps down it only if he can't stop); a drop of more than a step -> teeter (he leans
/// away from the edge, and goes over only if he's pushed past it). Steps going down are the stair roll's
/// (NmBodyTrip).
/// </summary>
internal partial class NmBody
{
    private const float WallReach = 1.1f;   // m from his chest: a wall this close is one he meets
    private const float LedgeDrop = 0.7f;   // m: a drop deeper than a step

    private bool _wallSent, _edgeSent;

    private void AdaptAttach() { _wallSent = _edgeSent = false; }

    /// At a hit: what lies the way he's pushed.
    private void AdaptToSurroundings(Vector3 dir)
    {
        Vector3 flat = new Vector3(dir.x, 0f, dir.z);
        if (flat.sqrMagnitude < 1e-4f) return;
        flat.Normalize();
        Part(10, out Vector3 chest, out _);
        Part(0, out Vector3 pelvis, out _);

        // a wall, a car, a crate: something upright he'd be shoved into
        if (!_wallSent && (RayWorld(chest, flat, WallReach, out RaycastHit w) || RayWorld(pelvis, flat, WallReach, out w)) && w.normal.y < 0.5f)
        {
            _wallSent = true;
            Send("balancerCollisionsReaction", ("start", true), ("objectBehindVictimPos", w.point), ("objectBehindVictimNormal", w.normal),
                 ("numStepsTillSlump", 4));
            EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} pushed toward {w.collider.name} {w.distance:F1} m away - meets it and braces");
        }

        // a ledge: the floor drops away more than a step within a stride
        if (!_edgeSent && RayWorld(pelvis, Vector3.down, 2f, out RaycastHit under))
        {
            foreach (float d in new[] { 0.5f, 0.9f })
            {
                Vector3 probe = new Vector3(pelvis.x, under.point.y + 0.4f, pelvis.z) + flat * d;
                if (RayWorld(probe - flat * 0.1f, flat, 0.2f, out _)) break; // a wall in the way, not a drop
                bool floor = RayWorld(probe, Vector3.down, LedgeDrop + 0.4f, out RaycastHit f) && f.normal.y > 0.6f;
                if (floor) continue;
                _edgeSent = true;
                Vector3 edge = new Vector3(pelvis.x, under.point.y, pelvis.z) + flat * (d - 0.2f);
                Vector3 side = Vector3.Cross(Vector3.up, flat) * 0.6f;
                Send("teeter", ("start", true), ("edgeLeft", edge - side), ("edgeRight", edge + side), ("reactDistance", 0.6f));
                EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} near a drop {d:F1} m the way he's pushed - teeters at the edge");
                break;
            }
        }
    }
}
