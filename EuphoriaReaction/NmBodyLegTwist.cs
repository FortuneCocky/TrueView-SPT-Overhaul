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
        for (int i = 0; i < _legHeld.Length; i++) _legHeld[i] = 0f;
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

    /// SPT's shins and feet on his own legs, after a pass has posed them. A knee is a hinge: the shin keeps
    /// the roll it had on its thigh at the takeover. The aim and IK turned it the short way each time (rest ->
    /// aim -> IK -> knee guard), and short turns that don't share a plane leave a roll behind: up to 117 deg
    /// on dying bots (leg roll log). `feet`: each foot takes the doll's ankle (its foot against its shin) on
    /// SPT's shin. Copied in world space, a foot took the doll's shin roll with it, which isn't SPT's (the aim
    /// sets that): after a prone takeover, with the doll's hip clamped up to 108 deg off his, the feet turned
    /// 120-170 deg about the shin.
    /// Then a hip and an ankle turn only so far about the leg: the thigh's roll on the pelvis and the foot's on
    /// the shin are held within HipRollMax / AnkleRollMax of the takeover, in SPT's own skeleton (as KneeGuard
    /// holds the knees). A doll seeded far off his legs (a prone takeover mid-transition or mid-hand-back, the
    /// outfit whose shin rest is snapped to the reference: knees 45-171 deg off their hinge at the takeover)
    /// turns its own legs back into range, and that carried onto his thighs (up to 120 deg) and feet (178).
    private const float HipRollMax = 50f, AnkleRollMax = 45f;
    private readonly float[] _legHeld = new float[4]; // per reaction, the most each was held back: L hip, L ankle, R hip, R ankle

    private void LegUntwist(bool feet)
    {
        for (int s = 0; s < 2; s++)
        {
            int th = s == 0 ? 1 : 4, ca = th + 1, ft = th + 2;
            Transform pel = _bones[0], t = _bones[th], c = _bones[ca], f = _bones[ft];
            if (pel == null || t == null || c == null || f == null) continue;
            Vector3 thigh = c.position - t.position;
            if (thigh.sqrMagnitude > 1e-6f)
                RollWithin(t, pel.rotation * (Quaternion.Inverse(_boneRot0[0]) * _boneRot0[th]), thigh.normalized, HipRollMax, s * 2);
            Vector3 shin = f.position - c.position;
            if (shin.sqrMagnitude < 1e-6f) continue;
            shin.Normalize();
            Quaternion then = t.rotation * (Quaternion.Inverse(_boneRot0[th]) * _boneRot0[ca]);
            float roll = TwistAbout(then, c.rotation, shin);
            if (Mathf.Abs(roll) > 0.5f) c.rotation = Quaternion.AngleAxis(-roll, shin) * c.rotation;
            if (feet)
            {
                // the doll's ankle, put through the takeover offsets: foot = shin * offset(shin) * ankle * offset(foot)^-1
                Quaternion ankle = Quaternion.Inverse(PartRot(ca)) * PartRot(ft);
                Quaternion offShin = Quaternion.Inverse(_boneRot0[ca]) * _partRot0[ca], offFoot = Quaternion.Inverse(_boneRot0[ft]) * _partRot0[ft];
                f.rotation = c.rotation * offShin * ankle * Quaternion.Inverse(offFoot);
            }
            RollWithin(f, c.rotation * (Quaternion.Inverse(_boneRot0[ca]) * _boneRot0[ft]), shin, AnkleRollMax, s * 2 + 1);
        }
    }

    /// Bone b's roll about `axis` (world, unit) from `then` (where it sat on its parent at the takeover) held to ±max.
    private void RollWithin(Transform b, Quaternion then, Vector3 axis, float max, int held)
    {
        float roll = TwistAbout(then, b.rotation, axis);
        float over = roll - Mathf.Clamp(roll, -max, max);
        if (Mathf.Abs(over) < 0.01f) return;
        b.rotation = Quaternion.AngleAxis(-over, axis) * b.rotation;
        if (Mathf.Abs(over) > _legHeld[held]) _legHeld[held] = Mathf.Abs(over);
    }

    private void LegTwistReport()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < LegTwistBones.Length; i++)
            sb.Append(i == 0 ? "" : ", ").Append(LegTwistBones[i].name).Append(' ').Append(_legTwistMax[i].ToString("F0"));
        EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} leg roll this reaction (max deg about itself vs the takeover): {sb}"
                           + $"; held back (most, deg): hips {_legHeld[0]:F0}/{_legHeld[2]:F0}, ankles {_legHeld[1]:F0}/{_legHeld[3]:F0}");
    }
}
