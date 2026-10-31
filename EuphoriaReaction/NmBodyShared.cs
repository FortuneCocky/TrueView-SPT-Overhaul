using System.Collections.Generic;
using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>Collision points gathered while baking the world around a bot, and joint lookup on the physics body.</summary>
internal partial class NmBody
{
    private readonly float[] _jA = new float[3], _jQ = new float[4], _jL = new float[3], _jM = new float[1];
    internal readonly List<Vector3> GroundPoints = new List<Vector3>();
    internal readonly List<Vector3> WallPoints = new List<Vector3>();

    /// <summary>A physics joint's parent part, pivot (world) and limits.</summary>
    internal bool Joint(int k, out int parent, out Vector3 pivot, out Vector3 limits)
    {
        parent = -1; pivot = default; limits = default;
        if (!Active) return false;
        try
        {
            int r = nm_agent_get_joint(_agent, k, out parent, _jA, _jQ, _jL, _jM);
            if (r == 0) return false;
            pivot = ToUnity(_jA[0], _jA[1], _jA[2]);
            if (_view) ViewJoint(k, out pivot); // the bones are being posed: the joint between the last two steps
            limits = new Vector3(_jL[0], _jL[1], _jL[2]);
            return true;
        }
        catch { return false; }
    }
}
