using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// The reaction reads where he is. Balance is the rule and a fall the exception:
///  - At each hit (not prone, not down), the way the shot pushes him is checked: a wall or crate within
///    reach behind him -> the balancerCollisionsReaction behaviour (he meets it, braces on it, and slumps down it
///    only if he can't stop); a drop of more than a step -> teeter (he leans away from the edge, and goes
///    over only if he's pushed past it). Steps going down are the stair roll's (NmBodyTrip).
///  - When a stumble goes over, he catches himself unless something says he can't (FallReason):
///    hard reasons (leaning past saving, tripped, rolling down stairs, a knee, hip or hand already on the
///    floor) always fall; soft ones (bumped into something, shot in the leg, badly hurt, leaning far, low)
///    fall at the config's "doesn't catch himself" odds; with none he always catches himself. A hand or
///    shoulder on a wall is support, not a fall.
/// </summary>
internal partial class NmBody
{
    private const float HardTilt = 65f;     // deg: past this nobody saves it
    private const float WallReach = 1.1f;   // m from his chest: a wall this close is one he meets
    private const float LedgeDrop = 0.7f;   // m: a drop deeper than a step

    private bool _wallSent, _edgeSent;
    private bool _bumped; // met a wall or an object this reaction

    private void AdaptAttach() { _wallSent = _edgeSent = _bumped = false; }

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

    /// Why this stumble can't be caught, or null. `hard`: it always falls; otherwise it falls at the odds.
    private string FallReason(float tilt, float h, out bool hard)
    {
        hard = true;
        if (tilt > HardTilt) return $"leaning {tilt:F0} deg, past saving";
        if (_tripped) return "tripped";
        if (_stairRoll) return "going down the stairs";
        bool wall = false;
        for (int k = 0; k < Parts; k++)
        {
            if (k == 3 || k == 6 || Delirium.Native.nm_agent_get_part_contact(_agent, k) == 0) continue;
            Part(k, out Vector3 at, out _);
            // on the floor under him (a knee, hip or hand down) is a fall already; against something upright is support
            if (RayWorld(at + Vector3.up * 0.05f, Vector3.down, 0.3f, out RaycastHit g) && g.normal.y > 0.6f) return $"part {k} already on the floor";
            wall = true;
        }
        if (wall) _bumped = true;
        hard = false;
        if (_bumped && _wallSent) return "bumped into something";
        if (_lastHit.part >= 1 && _lastHit.part <= 6) return "shot in the leg";
        if (StumbleEngine.HpFraction(_p) < 0.35f) return "badly hurt";
        if (tilt > SaveTilt) return $"leaning {tilt:F0} deg";
        if (h <= ComScaled(0.55f, 0.65f)) return $"low (COM {h:F2} m)";
        return null;
    }
}
