using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// The bot's root (his character controller: the capsule the game collides, grounds and moves) follows
/// the doll. It used to be put there by writing the transform, but the controller keeps its own idea of
/// where it is: the zero move sent every frame to keep him grounded (EuphoriaHold) moved it from that
/// stale spot, so the capsule stayed behind while the doll was dragged away and snapped after it.
/// A level move now goes through the controller itself; a jump in height (onto a crate, off a ledge,
/// over a rail) is still placed directly, and the physics scene is told the transform moved.
/// </summary>
internal partial class NmBody
{
    private void MoveRoot(Vector3 want)
    {
        Vector3 root = _p.Transform.position;
        Vector3 d = want - root;
        if (d.sqrMagnitude < 1e-8f) return;
        if (Mathf.Abs(d.y) < 0.01f && d.sqrMagnitude < 1f)
        {
            try
            {
                var cc = _p.MovementContext?.CharacterController;
                if (cc != null)
                {
                    float limit = cc.SpeedLimit;
                    cc.SpeedLimit = -1f; // the doll's speed, not his walking speed
                    try { cc.Move(d, Mathf.Max(Time.deltaTime, 1e-3f)); }
                    finally { cc.SpeedLimit = limit; }
                    return;
                }
            }
            catch { }
        }
        _p.Transform.position = want;
        Physics.SyncTransforms(); // the controller starts its next move from here, not from where it was
    }
}
