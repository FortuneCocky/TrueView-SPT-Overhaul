using System.Text;
using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// Leg twist watch: how far each leg bone has rolled about its own length on its parent, against how it
/// sat there at the takeover (the animation's pose: his real skeleton). A knee is a hinge, so a shin never
/// rolls on its thigh, and a hip or an ankle only turns so far; past LegTwistWarn the leg is twisted. Logged
/// the first time each bone goes past it in a reaction, with the stage it happened in (the doll's pose, the
/// stance match, a get-up, dying, the hand-back), and the reaction's worst with the SPT vs doll line.
/// </summary>
internal partial class NmBody
{
    private const float LegTwistWarn = 60f;

    // bone, its parent, and the two bones whose line is the roll axis (a foot rolls about its shin)
    private static readonly (int bone, int parent, int from, int to, string name)[] LegTwistBones =
    {
        (1, 0, 1, 2, "LThigh"), (2, 1, 2, 3, "LCalf"), (3, 2, 2, 3, "LFoot"),
        (4, 0, 4, 5, "RThigh"), (5, 4, 5, 6, "RCalf"), (6, 5, 5, 6, "RFoot"),
        (7, 0, 7, 8, "Spine1 on the pelvis"),
    };
    private readonly float[] _legTwistMax = new float[7];
    private int _legTwistWarned; // a bit per bone: logged this reaction

    private void LegTwistAttach()
    {
        for (int i = 0; i < _legTwistMax.Length; i++) _legTwistMax[i] = 0f;
        _legTwistWarned = 0;
    }

    /// After a pass has posed the legs: each bone's roll on its parent against the takeover.
    private void LegTwistTick(string stage)
    {
        if (_bones == null) return;
        for (int i = 0; i < LegTwistBones.Length; i++)
        {
            var (k, pa, from, to, name) = LegTwistBones[i];
            Transform b = _bones[k], p = _bones[pa], a0 = _bones[from], a1 = _bones[to];
            if (b == null || p == null || a0 == null || a1 == null) continue;
            Vector3 axis = a1.position - a0.position;
            if (axis.sqrMagnitude < 1e-6f) continue;
            // where the bone would be, sitting on its parent as it did at the takeover
            Quaternion then = p.rotation * (Quaternion.Inverse(_boneRot0[pa]) * _boneRot0[k]);
            float roll = TwistAbout(then, b.rotation, axis.normalized);
            float abs = Mathf.Abs(roll);
            if (abs > _legTwistMax[i]) _legTwistMax[i] = abs;
            if (abs > LegTwistWarn && (_legTwistWarned & (1 << i)) == 0)
            {
                _legTwistWarned |= 1 << i;
                EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} leg twist - {name} rolled {roll:+0;-0} deg about itself against the takeover ({stage}, phase {_reaction})");
            }
        }
    }

    private void LegTwistReport()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < LegTwistBones.Length; i++)
            sb.Append(i == 0 ? "" : ", ").Append(LegTwistBones[i].name).Append(' ').Append(_legTwistMax[i].ToString("F0"));
        EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} leg roll this reaction (max deg about itself vs the takeover): {sb}");
    }

    /// A leg bone's twist base from the outfit mesh, checked against his skeleton as it stood at the takeover
    /// (`live`: its local rotation then). The aim pass keeps only this roll, and the leg rest the binding
    /// corrects (FixLegRoll: some outfit meshes give a thigh or shin a rest rolled 135-174 deg) was never
    /// corrected here: the shin rolled half a turn about itself and the foot on it pointed sideways or back.
    /// A knee never rolls a shin on its thigh and a hip only turns so far, so a roll past StandingRollMax is the
    /// mesh's: the base takes the skeleton's roll instead.
    private Quaternion LegRestTwist(int bone, int child, Quaternion rest, Quaternion live)
    {
        Transform b = _bones[bone], c = _bones[child];
        if (b == null || c == null) return rest;
        Vector3 axis = Quaternion.Inverse(b.rotation) * (c.position - b.position); // its length, own frame
        if (axis.sqrMagnitude < 1e-8f) return rest;
        axis.Normalize();
        Quaternion d = Quaternion.Inverse(rest) * live; // rest -> the skeleton, in its own frame
        float roll = 2f * Mathf.Atan2(Vector3.Dot(new Vector3(d.x, d.y, d.z), axis), d.w) * Mathf.Rad2Deg;
        if (roll > 180f) roll -= 360f;
        if (roll < -180f) roll += 360f;
        if (Mathf.Abs(roll) <= StandingRollMax) return rest;
        EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} {b.name} twist base rolled {roll:F0} deg against his skeleton (outfit mesh) - corrected");
        return rest * Quaternion.AngleAxis(roll, axis);
    }
}
