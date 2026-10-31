using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// Skeleton aim: while Euphoria owns a bot, SPT's skeleton bends like the doll, in SPT's own shape.
/// Copying each part's rotation onto its bone (the plain drive) left every bone a little off, since
/// SPT's bones don't sit exactly where the doll's joints do and the takeover pose rarely matched; down a
/// chain the errors added up and limbs curled where the doll never bent. Pinning the bones onto the
/// doll's joints fixed that but stretched SPT's proportions into the doll's. So the bones are AIMED:
/// parent-first, each keeps its own position and length and is turned to point the same way as the
/// doll's matching segment (hip -> knee, each spine joint to the next ...).
///
/// Twist (the roll about a bone's own length) is NOT taken from the plain drive: that copied each part's
/// absolute roll against the takeover pose, every bone on its own, and when the doll's parts rolled a
/// little differently from each other the spine, arms and wrists twisted against their neighbours like a
/// wrung-out towel. Each aimed bone starts from its rest-pose roll relative to its PARENT (NmBodyRest), so a chain
/// rolls as one; the doll's real roll comes back only where it shows and in measured amounts: the
/// chest's turn against the pelvis, spread evenly over the spine segments (capped), and each knee and
/// elbow rolled so SPT's hinge matches the bend (hinges learned from the animation).
///
/// Then each limb gets a two-bone IK so the ankle / wrist lands on the doll's, bending in the plane of
/// the doll's knee / elbow: the lengths differ a little, so SPT's knee bends a little more or less than
/// the doll's, but the feet and hands touch what the doll's touch. The feet (unless set to follow the
/// doll's ankles) and the gun palm keep the animation's angle on top; a free hand follows the doll's
/// but no more than a wrist can turn from its takeover angle on the forearm.
/// The arms are aimed whenever they're in the physics, standing too: held near the weapon pose instead,
/// they stayed in the hold while the gun (on the palm) went with the doll's hand.
/// </summary>
internal partial class NmBody
{
    // parent-first: bone, the SPT bone it points at, and the doll joints of that segment
    private static readonly (int bone, int child)[] LockOrder =
    {
        (1, 2), (2, 3), (4, 5), (5, 6),
        (7, 8), (8, 10), (10, 11), (11, 12),                    // part 9 has no SPT bone: bone 8 aims at the chest
        (13, 14), (14, 15), (15, 16),
        (17, 18), (18, 19), (19, 20),
    };
    private static readonly int[] SpineBones = { 7, 8, 10 };
    private const float SpineTwistMax = 50f; // deg the chest may turn against the pelvis
    private const float WristTurnMax = 60f;  // deg a free hand may turn from its takeover angle on the forearm
    private const float HingeRollFrom = 12f, HingeRollFull = 30f; // knee / elbow bend (deg) over which the bend takes over the hinge
    private const float HingeTurnRate = 720f; // deg/s a knee / elbow hinge may turn about its bone: a real one never flips over in a frame

    private readonly Vector3[] _lockPivot = new Vector3[21];
    private readonly bool[] _lockOk = new bool[21];
    private readonly Quaternion[] _lockRest = new Quaternion[21]; // each bone's twist base on its parent: rest pose (NmBodyRest), else the takeover's

    private static bool LockOn => EuphoriaPlugin.Instance?.LockSkeleton.Value ?? true;

    // SPT's knee and elbow hinges, in the upper bone's own space, learned from the animation (the axis the
    // lower bone bends about). The IK places the joint right but nothing in it sets the upper bone's
    // roll; each thigh / upper arm is rolled about itself so this hinge lines up with the bend.
    // 0 L leg, 1 R leg, 2 L arm, 3 R arm
    private static readonly int[,] Limbs = { { 1, 2, 3 }, { 4, 5, 6 }, { 14, 15, 16 }, { 18, 19, 20 } };
    private readonly Vector3[] _kneeAxis = new Vector3[4];
    private readonly bool[] _kneeAxisOk = new bool[4];
    // each knee / elbow's hinge (world) last frame, carried over so it turns smoothly
    private readonly Vector3[] _hinge = new Vector3[4];
    private readonly bool[] _hingeHave = new bool[4];

    private void HingeAttach() { for (int s = 0; s < 4; s++) _hingeHave[s] = false; }

    /// Before the physics overwrites the limbs: where the animation's knees and elbows hinge (only from a bent joint).
    private void KneeAxisSample()
    {
        for (int s = 0; s < 4; s++)
        {
            Transform t = _bones[Limbs[s, 0]], k = _bones[Limbs[s, 1]], f = _bones[Limbs[s, 2]];
            if (t == null || k == null || f == null) continue;
            Vector3 u = k.position - t.position, v = f.position - k.position;
            if (Vector3.Angle(u, v) < 8f) continue;
            _kneeAxis[s] = Quaternion.Inverse(t.rotation) * Vector3.Cross(u, v).normalized;
            _kneeAxisOk[s] = true;
        }
    }

    private readonly Quaternion[] _armPre = new Quaternion[21];

    private void LockToJoints(bool arms, bool lockWrist, float armW = 1f)
    {
        // the arms fade in from wherever the drive left them (the animated hold, as they join the physics)
        if (arms && armW < 0.999f) for (int k = 13; k < Parts; k++) if (_bones[k] != null) _armPre[k] = _bones[k].localRotation;
        for (int k = 1; k < Parts; k++) _lockOk[k] = Joint(k, out _, out _lockPivot[k], out _);
        // where the plain drive put the chest and the free hand, before their parents are reset
        Quaternion chestDoll = _bones[10] != null ? _bones[10].rotation : Quaternion.identity;
        Quaternion handL = _bones[16] != null ? _bones[16].rotation : Quaternion.identity;
        Quaternion handR = _bones[20] != null ? _bones[20].rotation : Quaternion.identity;

        // twist base: every aimed bone at its takeover rotation on its parent (a chain rolls as one)
        foreach (var (bone, _) in LockOrder)
        {
            if (!arms && bone >= 13) break;
            if (_bones[bone] != null) _bones[bone].localRotation = _lockRest[bone];
        }
        foreach (var (bone, child) in LockOrder)
        {
            if (!arms && bone >= 13) break;
            if (bone == 11) SpineTwist(chestDoll); // the chest's turn, before the neck aims off it
            Aim(bone, child);
        }
        FeetOnRealFloor(); // a foot on the ground: SPT's ankle on the real floor, not the bake's (NmBodyGround)
        LimbIK(1, 2, 3, 0);
        LimbIK(4, 5, 6, 1);
        if (!arms) return;
        LimbIK(14, 15, 16, 2);
        LimbIK(18, 19, 20, 3);
        FreeHand(16, handL);
        if (!lockWrist) FreeHand(20, handR);
        if (armW < 0.999f)
            for (int k = 13; k < Parts; k++)
                if (_bones[k] != null && !(k == 20 && lockWrist)) _bones[k].localRotation = Quaternion.Slerp(_armPre[k], _bones[k].localRotation, armW);
    }

    private void Aim(int bone, int child)
    {
        Transform b = _bones[bone], c = _bones[child];
        if (b == null || c == null || !_lockOk[bone] || !_lockOk[child]) return;
        Vector3 cur = c.position - b.position, tgt = _lockPivot[child] - _lockPivot[bone];
        if (cur.sqrMagnitude < 1e-6f || tgt.sqrMagnitude < 1e-6f) return;
        b.rotation = Quaternion.FromToRotation(cur, tgt) * b.rotation;
    }

    /// The doll's chest turn against the pelvis (its roll about the spine), capped and spread evenly over
    /// the spine segments, each turning about its own length so the aim they just got is kept.
    private void SpineTwist(Quaternion chestDoll)
    {
        Transform chest = _bones[10], neck = _bones[11];
        if (chest == null || neck == null) return;
        Vector3 axis = neck.position - chest.position;
        if (axis.sqrMagnitude < 1e-6f) return;
        float t = Mathf.Clamp(TwistAbout(chest.rotation, chestDoll, axis.normalized), -SpineTwistMax, SpineTwistMax);
        if (Mathf.Abs(t) < 0.5f) return;
        float each = t / SpineBones.Length;
        for (int i = 0; i < SpineBones.Length; i++)
        {
            int k = SpineBones[i];
            Transform b = _bones[k];
            Transform c = i + 1 < SpineBones.Length ? _bones[SpineBones[i + 1]] : neck;
            if (b == null || c == null) continue;
            Vector3 along = c.position - b.position;
            if (along.sqrMagnitude < 1e-6f) continue;
            b.rotation = Quaternion.AngleAxis(each, along) * b.rotation;
        }
    }

    /// Signed angle (deg) of the roll about a world axis that takes rotation a to rotation b.
    private static float TwistAbout(Quaternion a, Quaternion b, Vector3 axis)
    {
        Quaternion d = b * Quaternion.Inverse(a); // world-space difference
        Vector3 v = new Vector3(d.x, d.y, d.z);
        float p = Vector3.Dot(v, axis);           // the twist part of the swing-twist split
        float ang = 2f * Mathf.Atan2(p, d.w) * Mathf.Rad2Deg;
        if (ang > 180f) ang -= 360f;
        if (ang < -180f) ang += 360f;
        return ang;
    }

    /// A free hand: the doll's angle, but within a wrist's reach of how it sat on the forearm at the takeover.
    private void FreeHand(int k, Quaternion doll)
    {
        Transform hand = _bones[k];
        if (hand == null || hand.parent == null) return;
        Quaternion local = Quaternion.Inverse(hand.parent.rotation) * doll;
        hand.localRotation = Quaternion.RotateTowards(_lockRest[k], local, WristTurnMax);
    }

    /// Two-bone IK: upper bone a, lower bone b, end bone c. The end goes to the doll's joint c (or as close
    /// as SPT's limb reaches), the middle joint bends toward the doll's joint b. Lengths stay SPT's.
    private void LimbIK(int a, int b, int c, int limb)
    {
        Transform A = _bones[a], B = _bones[b], C = _bones[c];
        if (A == null || B == null || C == null || !_lockOk[b] || !_lockOk[c]) return;
        Vector3 ap = A.position;
        float la = (B.position - ap).magnitude, lb = (C.position - B.position).magnitude;
        Vector3 toT = _lockPivot[c] - ap;
        float d = toT.magnitude;
        if (la < 1e-3f || lb < 1e-3f || d < 1e-3f) return;
        Vector3 dir = toT / d;
        d = Mathf.Clamp(d, Mathf.Abs(la - lb) + 1e-3f, la + lb - 1e-3f);
        // bend plane: the doll's knee / elbow, else the pose the aim pass gave
        Vector3 bend = Vector3.ProjectOnPlane(_lockPivot[b] - ap, dir);
        if (bend.sqrMagnitude < 1e-6f) bend = Vector3.ProjectOnPlane(B.position - ap, dir);
        if (bend.sqrMagnitude < 1e-8f) return;
        bend.Normalize();
        // the hinge this knee / elbow had last frame (at the takeover: the doll's upper arm / thigh's, which is
        // then the animation's). A knee or elbow only bends one way: a doll joint that reads past straight (a
        // locked knee) or flips sides near straight is bent the way the hinge already points, or SPT's limb
        // would roll round and bend backwards with it
        Vector3 prev = !_kneeAxisOk[limb] ? Vector3.zero : _hingeHave[limb] ? _hinge[limb]
            : PartRot(a) * (Quaternion.Inverse(_partRot0[a]) * _boneRot0[a]) * _kneeAxis[limb];
        if (Vector3.Dot(Vector3.Cross(bend, dir), prev) < 0f) bend = -bend;
        float cosA = Mathf.Clamp((la * la + d * d - lb * lb) / (2f * la * d), -1f, 1f);
        Vector3 mid = ap + dir * (la * cosA) + bend * (la * Mathf.Sqrt(1f - cosA * cosA));
        A.rotation = Quaternion.FromToRotation(B.position - ap, mid - ap) * A.rotation;
        Vector3 end = ap + dir * d;
        // knee / elbow: roll the upper bone about itself so SPT's hinge lines up. The aim leaves the roll to
        // FromToRotation off the rest pose, which flips 180 deg as the limb swings past straight up from rest
        // (an arm overhead): the "arm spin". So the roll is set outright: last frame's hinge carried over while
        // the limb is near straight (nothing to read), the bend's once it bends enough to read, and never
        // turning faster than a real joint could.
        if (_kneeAxisOk[limb])
        {
            Vector3 upper = mid - ap, want = Vector3.Cross(upper, end - mid);
            Vector3 keep = Vector3.ProjectOnPlane(prev, upper);
            float w = want.sqrMagnitude > 1e-6f * upper.sqrMagnitude
                ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(HingeRollFrom, HingeRollFull, Vector3.Angle(upper, end - mid))) : 0f;
            Vector3 hinge = keep.sqrMagnitude < 1e-8f ? want
                : Vector3.RotateTowards(keep.normalized, w > 0f ? Vector3.Slerp(keep.normalized, want.normalized, w) : keep.normalized,
                                        HingeTurnRate * Mathf.Deg2Rad * Time.deltaTime, 0f);
            if (hinge.sqrMagnitude > 1e-8f)
            {
                float roll = Vector3.SignedAngle(Vector3.ProjectOnPlane(A.rotation * _kneeAxis[limb], upper), hinge, upper);
                A.rotation = Quaternion.AngleAxis(roll, upper) * A.rotation;
            }
            _hinge[limb] = A.rotation * _kneeAxis[limb];
            _hingeHave[limb] = true;
            HingeSeen(limb, _hinge[limb]);
        }
        B.rotation = Quaternion.FromToRotation(C.position - B.position, end - B.position) * B.rotation;
    }
}
