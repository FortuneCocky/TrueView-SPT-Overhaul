using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// Dev-panel mouse drag: one doll bone is pulled toward a point under the cursor by a damped
/// spring, applied as an impulse before every physics step. The force is capped, so a bot is
/// dragged and stumbles (or flops, when down or dead) instead of being torn apart.
/// </summary>
internal partial class NmBody
{
    private const float GrabFreq = 2.2f;       // Hz: how stiffly the bone follows the cursor
    private const float GrabZeta = 0.9f;       // damping ratio: no orbiting around the cursor
    private const float GrabMaxForce = 1400f;  // N: drags a man, doesn't rip limbs off
    private const float GrabPickRadius = 0.3f; // m from the cursor ray a bone can be picked at
    private const float DragFootCap = 0.45f;   // x his weight: sideways pull while he's on his feet (feet grip ~1x)
    private const float DragLiftCap = 0.3f;    // x his weight: up/down pull on his feet (lifting him unloads the feet)

    private bool _grabbing;
    private int _grabPart = -1;
    private Vector3 _grabLocal, _grabTarget;

    internal bool Grabbing => _grabbing;
    internal int GrabPart => _grabbing ? _grabPart : -1;

    /// The doll bone closest to the cursor ray (within GrabPickRadius), its distance along the
    /// ray, and the point on the ray nearest it. -1 when none is close enough.
    internal int DevBoneUnderRay(Ray ray, out float depth, out Vector3 point)
    {
        depth = 0f; point = default;
        if (!Active) return -1;
        int best = -1;
        float bestD = GrabPickRadius;
        for (int k = 0; k < Parts; k++)
        {
            Part(k, out Vector3 pos, out _);
            float t = Vector3.Dot(pos - ray.origin, ray.direction);
            if (t <= 0.1f) continue;
            Vector3 onRay = ray.origin + ray.direction * t;
            float d = (pos - onRay).magnitude;
            if (d < bestD)
            {
                best = k; bestD = d; depth = t; point = onRay;
            }
        }
        return best;
    }

    /// The doll bone nearest a world point (the engine's own lookup), -1 if not active.
    internal int DevNearestBone(Vector3 at)
    {
        if (!Active) return -1;
        ToNm(at, _f3);
        return nm_agent_nearest_part(_agent, _f3);
    }

    internal string DevBoneName(int k) =>
        k < 0 || k >= Parts ? "-" : _bones != null && _bones[k] != null ? _bones[k].name : "part " + k;

    /// Grab bone `k` at world point `at` (kept in the bone's frame, so pulling off-centre twists it).
    internal bool DevGrab(int k, Vector3 at)
    {
        if (!Active || k < 0 || k >= Parts) return false;
        Part(k, out Vector3 pos, out Quaternion rot);
        _grabPart = k;
        _grabLocal = Quaternion.Inverse(rot) * Vector3.ClampMagnitude(at - pos, 0.15f);
        _grabTarget = at;
        _grabbing = true;
        _until = Mathf.Max(_until, Time.time + 2f);
        EuphoriaPlugin.Log($"Dev panel: grabbed {DevBoneName(k)} on {_p?.ProfileId}");
        return true;
    }

    internal void DevDragTo(Vector3 target) => _grabTarget = target;

    internal void DevRelease()
    {
        if (!_grabbing) return;
        _grabbing = false;
        _until = Mathf.Max(_until, Time.time + 3f); // let him recover (or not) on his own
        EuphoriaPlugin.Log($"Dev panel: released {DevBoneName(_grabPart)}");
    }

    /// Where the grabbed point is and where it is being pulled, for the overlay line.
    internal bool DevGrabLine(out Vector3 from, out Vector3 to)
    {
        from = to = default;
        if (!_grabbing || !Active) return false;
        Part(_grabPart, out Vector3 pos, out Quaternion rot);
        from = pos + rot * _grabLocal;
        to = _grabTarget;
        return true;
    }

    // Before each physics step: spring the grabbed point toward the cursor target.
    private void GrabStep()
    {
        if (!_grabbing) return;
        Part(_grabPart, out Vector3 pos, out Quaternion rot);
        Vector3 at = pos + rot * _grabLocal;
        nm_agent_get_part_velocity(_agent, _grabPart, _f3, _g3);
        Vector3 v = ToUnity(_f3[0], _f3[1], _f3[2]);
        float m = Mathf.Max(20f, nm_agent_get_mass(_agent));
        float w = 2f * Mathf.PI * GrabFreq;
        Vector3 force = Vector3.ClampMagnitude(m * (w * w * (_grabTarget - at) - 2f * GrabZeta * w * v), GrabMaxForce);
        // On his feet the pull stays under what his soles can hold (friction ~ his weight): he leans
        // into it and steps after the cursor. Above that (1400 N against ~780 N of grip) the feet
        // skated across the floor. Down or dying: full pull, a body dragged along the ground slides.
        if (_downSince == 0f && !_dying && nm_agent_has_fallen(_agent) == 0)
        {
            float weight = m * 9.81f;
            Vector3 flat = Vector3.ClampMagnitude(new Vector3(force.x, 0f, force.z), DragFootCap * weight);
            force = new Vector3(flat.x, Mathf.Clamp(force.y, -DragLiftCap * weight, DragLiftCap * weight), flat.z);
        }
        ToNm(at, _f3);
        ToNm(force * Step, _g3);
        nm_agent_apply_impulse(_agent, _grabPart, _f3, _g3);
        _until = Mathf.Max(_until, Time.time + 1f);
    }
}
