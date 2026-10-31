using System.Text;
using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// How far SPT's skeleton is from the doll it copies, per reaction: for every joint the largest angle
/// between the SPT bone (read back as the doll part it stands for) and the doll part, how much of that
/// the joint clamp put there, and how far SPT's wrists, ankles and head joint ended up from the doll's
/// (cm). Logged at the hand-back, worst first, so a curled or folded limb can be traced to the joint
/// and the pass that bent it. Measured after the bone passes, not while matching into a stance (that
/// eases away from the doll on purpose) and not on the feet or the gun wrist (they keep the animation).
/// </summary>
internal partial class NmBody
{
    private readonly float[] _gapMax = new float[Parts], _gapClamp = new float[Parts];
    private static readonly int[] GapEnds = { 3, 6, 12, 16, 20 }; // ankles, head, wrists
    private readonly float[] _gapEnd = new float[Parts];
    private int _gapFrames;

    private void GapAttach()
    {
        for (int k = 0; k < Parts; k++) _gapMax[k] = _gapClamp[k] = _gapEnd[k] = 0f;
        _gapFrames = 0;
    }

    /// LateUpdate, after the bones are written from the doll.
    private void GapTick(bool arms, bool lockWrist)
    {
        _gapFrames++;
        for (int k = 1; k < Parts; k++)
        {
            if (_bones[k] == null || k == 3 || k == 6 || (k >= 13 && !arms) || (k == 20 && lockWrist)) continue;
            // the bone read back as its doll part, through the takeover offset between the two
            float a = Quaternion.Angle(_bones[k].rotation * (Quaternion.Inverse(_boneRot0[k]) * _partRot0[k]), PartRot(k));
            if (a > _gapMax[k]) _gapMax[k] = a;
        }
        foreach (int k in GapEnds)
        {
            if (_bones[k] == null || (k >= 13 && !arms)) continue;
            if (!Joint(k, out _, out Vector3 pivot, out _)) continue;
            float d = Vector3.Distance(_bones[k].position, pivot);
            if (d > _gapEnd[k]) _gapEnd[k] = d;
        }
    }

    private void GapReport()
    {
        if (_gapFrames < 10 || _p == null) return;
        var order = new System.Collections.Generic.List<int>();
        for (int k = 1; k < Parts; k++) if (_gapMax[k] > 0f) order.Add(k);
        order.Sort((x, y) => _gapMax[y].CompareTo(_gapMax[x]));
        var sb = new StringBuilder();
        for (int i = 0; i < order.Count && i < 5; i++)
        {
            int k = order[i];
            sb.Append(i == 0 ? "" : ", ").Append(_bones[k].name.Replace("Base Human", "")).Append(' ').Append(_gapMax[k].ToString("F0")).Append('°');
            if (_gapClamp[k] >= 1f) sb.Append(" (clamp ").Append(_gapClamp[k].ToString("F0")).Append("°)");
        }
        var ends = new StringBuilder();
        foreach (int k in GapEnds)
            if (_gapEnd[k] > 0f) ends.Append(ends.Length == 0 ? "" : ", ").Append(_bones[k].name.Replace("Base Human", "")).Append(' ').Append((_gapEnd[k] * 100f).ToString("F0")).Append("cm");
        EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} SPT vs doll this reaction - joints: {(sb.Length > 0 ? sb.ToString() : "none")}; ends: {(ends.Length > 0 ? ends.ToString() : "n/a")}"
                           + (_clampReady ? "" : " (joint clamp was off: its setup failed at the takeover)"));
    }
}
