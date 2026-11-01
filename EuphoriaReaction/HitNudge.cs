using EFT;
using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// Hit reaction without a ragdoll: every hit that doesn't start a stumble or a death still moves him.
/// The bullet's push lands as a spin on the bones near the wound (a chest hit rocks the upper spine back,
/// a head hit snaps the neck, an arm hit jolts that shoulder (arms and gun move together), a leg hit moves only the thigh
/// or shin that was hit, a hip hit nothing visible), each on its own spring. Feet and hands keep the animation's
/// rotation throughout
/// that settles in a few tenths of a second. Layered on top of whatever the animation is doing (after it,
/// in LateUpdate), so aiming, walking and the AI carry on underneath.
/// When the same or a later hit starts Euphoria (stumble or death), the nudge steps aside: the doll takes
/// over from the pose on screen, so the motion carries straight into the reaction. It picks up again,
/// from rest, once he's handed back to the animation.
///
/// Heavy nudge: a limb hit with massive damage (config) sends that limb limp for a moment, like a quick
/// local ragdoll: for about a second its bones swing on a loose, barely damped spring, much further, a
/// left arm sagging under its own weight, then a firm spring brings it back to the animated pose. The gun
/// arm only takes a hard shoulder jolt (the gun hangs off the upper body, not the hand, so a limp gun arm
/// would leave the gun floating). A foot keeps its angle (planted), a limp left hand goes with its arm.
/// </summary>
[DefaultExecutionOrder(10000)] // after the animator and other bone layers
internal partial class HitNudge : MonoBehaviour
{
    private const float Freq = 4.2f;     // Hz: spring
    private const float Zeta = 0.45f;    // a little overshoot, so it reads as a body, not a lerp
    private const float KickDeg = 190f;  // deg/s of spin per unit of strength at weight 1 (about 7 deg of bend)

    // nudged bones (doll part indices, parent-first) and how far each may go (deg). Never the pelvis:
    // the legs and the whole upper body hang off it, so any spin there moved everything.
    // Nor the arms or hands: the gun hangs off the upper body, not the hands, so a nudged arm left the
    // gun floating while the hands moved off it. Arm hits jolt the shoulder through the upper torso instead.
    // Nor the feet: they keep the animation's rotation (planted, or mid-step), whatever the leg above does.
    private static readonly int[] Nudged = { 1, 2, 4, 5, 7, 8, 9, 10, 11, 12, 14, 15 }; // left arm: heavy nudge only
    // left as the animation has them: a nudge above them doesn't turn them (feet and hands)
    private static readonly int[] Kept = { 3, 6, 16, 20 };
    private readonly Quaternion[] _keptRot = new Quaternion[21];
    private static readonly float[] MaxDeg = new float[21];

    // each part's segment for finding the bone that was hit: from the bone to this child (-1: the bone's point)
    private static readonly int[] SegEnd = { 7, 2, 3, -1, 5, 6, -1, 8, 9, 10, 11, 12, -1, 14, 15, 16, -1, 18, 19, 20, -1 };

    private Player _p;
    private Transform[] _bones;
    private readonly Vector3[] _theta = new Vector3[21]; // world rotation vector (rad)
    private readonly Vector3[] _omega = new Vector3[21]; // rad/s
    private bool _moving;

    // heavy nudge
    private const float LimpFreq = 0.9f, LimpZeta = 0.3f;     // the limp second: loose and swinging
    private const float BackFreq = 5f, BackZeta = 0.8f;       // then a firm spring back, little overshoot
    private const float HeavyKick = 3f;                       // x the normal spin
    private const float ArmSag = 22f;                         // rad/s^2 of droop on a limp arm
    // the springs step at most this long: in one frame step, the firm spring back went unstable below ~33 fps
    // (and the normal one at 20), and a limp arm flailed between its limits for good instead of coming back
    private const float MaxStep = 1f / 120f;
    private static readonly float[] HeavyMax = new float[21];
    private readonly float[] _limpUntil = new float[21];
    private readonly bool[] _heavy = new bool[21];            // past its normal range: still coming back
    private bool _leftArmLimp;
    private float _leftArmSince;                              // the last heavy hit on the left arm (0: not limp)

    static HitNudge()
    {
        MaxDeg[1] = MaxDeg[4] = 5f; MaxDeg[2] = MaxDeg[5] = 6f;                             // thigh, shin
        MaxDeg[7] = MaxDeg[8] = 8f; MaxDeg[9] = MaxDeg[10] = 12f;                          // spine
        MaxDeg[11] = 14f; MaxDeg[12] = 22f;                                                // neck, head
        MaxDeg[14] = MaxDeg[15] = 0f;                                                      // arms: heavy nudge only
        HeavyMax[1] = HeavyMax[4] = 35f; HeavyMax[2] = HeavyMax[5] = 45f;                  // a leg kicked out
        HeavyMax[10] = HeavyMax[9] = 20f;                                                  // the gun shoulder jolt
        HeavyMax[14] = 70f; HeavyMax[15] = 60f;                                            // a limp left arm
    }

    /// One hit on `part` at `point`, travelling along `dir`, with push `strength` (about 0.4-3).
    internal static void Hit(Player p, EBodyPart part, Vector3 point, Vector3 dir, float strength, float damage = 0f)
    {
        if (p == null || dir.sqrMagnitude < 1e-6f) return;
        HitNudge n = p.GetComponent<HitNudge>();
        if (n == null) { n = p.gameObject.AddComponent<HitNudge>(); p.gameObject.AddComponent<HitNudgeLate>(); } // the gun rides the chest (HitNudgeGun)
        var cfg = EuphoriaPlugin.Instance;
        bool heavy = (cfg?.HeavyNudge.Value ?? true) && damage >= (cfg?.HeavyNudgeDamage.Value ?? 35f)
                     && (part == EBodyPart.LeftArm || part == EBodyPart.RightArm || part == EBodyPart.LeftLeg || part == EBodyPart.RightLeg);
        strength *= cfg?.NudgeStrength.Value ?? 1f;
        if (!heavy && (cfg?.PhysNudge.Value ?? true) && n.Poke(point, dir.normalized, strength)) return;
        n.Kick(part, point, dir.normalized, strength, heavy);
    }

    // ---------------------------------------------------------------- physical nudge
    // Delirium's poke on a doll of his own: a balancing doll, pelvis and feet held where they stand, takes
    // the bullet's impulse at the part that was hit, and its muscles and balance answer it (the chest
    // rocks, the head whips and lags, a leg hit buckles that knee, an arm hit twists the shoulders). How
    // far each part is turned off its rest is laid over the animation on the same bones the spring nudge
    // uses, so the AI, aiming and walking carry on underneath. Hits while it swings poke the same doll.
    // The doll lives in a world of its own (a floor, nothing else), so it never touches the level.
    private const float PokeImpulse = 9f;   // N s per unit of strength (the Playroom's poke is 8 N s)
    private const float PokeLife = 3f;      // s at most
    private static readonly int[] DollParent = { -1, 0, 1, 2, 0, 4, 5, 0, 7, 8, 9, 10, 11, 10, 13, 14, 15, 10, 17, 18, 19 };
    private static readonly int[] Pinned = { 0, 3, 6 };   // pelvis and feet: where the animation has him
    private System.IntPtr _nw, _na;
    private float _yaw0, _pokeAt, _quietFor, _acc;
    private readonly Vector3[] _restPos = new Vector3[21];
    private readonly Quaternion[] _restRot = new Quaternion[21], _dollRot = new Quaternion[21];
    private readonly float[] _p3 = new float[3], _q4 = new float[4], _z3 = new float[3], _z3b = new float[3];

    /// False when there's no doll to poke (the spring nudge takes the hit instead).
    private bool Poke(Vector3 point, Vector3 dir, float strength)
    {
        if (_p == null) _p = GetComponent<Player>();
        if (_bones == null || _bones[0] == null) _bones = _p != null ? NmBody.MapBones(_p) : null;
        if (_bones == null || NmBody.BaseBuild == System.IntPtr.Zero || strength <= 0f) return false;
        int bone = HitBone(point);
        if (bone < 0 || !DollUp()) return false;
        // the hit, in the doll's frame: he may have turned since it was made
        Quaternion back = Quaternion.Inverse(Turn());
        Delirium.Native.nm_agent_get_part_transform(_na, bone, _p3, _q4);
        Vector3 at = U(_p3) + back * (point - _bones[bone].position);
        Nm(at, _p3);
        Nm(back * dir * (PokeImpulse * strength), _z3);
        Delirium.Native.nm_agent_apply_impulse(_na, bone, _p3, _z3);
        _pokeAt = Time.time; _quietFor = 0f;
        _moving = true;
        return true;
    }

    private bool DollUp()
    {
        if (_na != System.IntPtr.Zero) return true;
        _nw = Delirium.Native.nm_world_create();
        if (_nw == System.IntPtr.Zero) return false;
        Delirium.Native.nm_world_add_floor(_nw, 0f, 0f, 0f, 20f);
        _yaw0 = _p.Transform.eulerAngles.y;
        _na = Delirium.Native.nm_agent_create(NmBody.BaseBuild, new float[3], Mathf.PI - _yaw0 * Mathf.Deg2Rad, Random.value);
        if (_na == System.IntPtr.Zero) { DollDown(); return false; }
        Delirium.Native.nm_agent_task_balance(_na, _nw, "*", null);
        // settle on its feet first: its rest is the standing pose its muscles hold
        for (int i = 0; i < 15; i++) Delirium.Native.nm_agent_step(_na, _nw, 1f / 60f);
        for (int k = 0; k < 21; k++)
        {
            Delirium.Native.nm_agent_get_part_transform(_na, k, _p3, _q4);
            _restPos[k] = U(_p3); _restRot[k] = UQ(_q4);
        }
        _acc = 0f;
        return true;
    }

    private void DollDown()
    {
        if (_na != System.IntPtr.Zero) Delirium.Native.nm_agent_destroy(_na);
        if (_nw != System.IntPtr.Zero) Delirium.Native.nm_world_destroy(_nw);
        _na = _nw = System.IntPtr.Zero;
    }

    private void OnDestroy() => DollDown();

    private Quaternion Turn() => Quaternion.Euler(0f, (_p != null ? _p.Transform.eulerAngles.y : _yaw0) - _yaw0, 0f);

    /// LateUpdate: step the doll, then lay its swing over the animation. False once it has settled.
    private bool DollPose(float dt)
    {
        _acc += dt;
        while (_acc >= 1f / 60f)
        {
            _acc -= 1f / 60f;
            foreach (int k in Pinned)
            {
                Nm(_restPos[k], _p3);
                Quaternion r = _restRot[k];
                _q4[0] = -r.x; _q4[1] = -r.z; _q4[2] = -r.y; _q4[3] = r.w;
                Delirium.Native.nm_agent_set_part_state(_na, k, _p3, _q4, _z3b, _z3b);
            }
            Delirium.Native.nm_agent_step(_na, _nw, 1f / 60f);
        }
        for (int k = 0; k < 21; k++)
        {
            Delirium.Native.nm_agent_get_part_transform(_na, k, _p3, _q4);
            _dollRot[k] = UQ(_q4) * Quaternion.Inverse(_restRot[k]); // how far it's turned off its rest
        }
        float age = Time.time - _pokeAt;
        float fade = 1f - Mathf.SmoothStep(0f, 1f, (age - (PokeLife - 0.3f)) / 0.3f);
        Quaternion turn = Turn(), back = Quaternion.Inverse(turn);
        float most = 0f;
        foreach (int k in Nudged) // parent-first: each bone gets what its doll part turned beyond its parent
        {
            Transform b = _bones[k];
            if (b == null || MaxDeg[k] <= 0f) continue;
            Quaternion own = _dollRot[k] * Quaternion.Inverse(_dollRot[DollParent[k]]);
            own.ToAngleAxis(out float ang, out Vector3 axis);
            if (ang > 180f) ang -= 360f;
            if (Mathf.Abs(ang) < 0.05f || axis.sqrMagnitude < 0.5f) continue;
            most = Mathf.Max(most, Mathf.Abs(ang));
            ang = Mathf.Clamp(ang, -2f * MaxDeg[k], 2f * MaxDeg[k]) * fade;
            b.rotation = Quaternion.AngleAxis(ang, turn * axis) * b.rotation;
        }
        _quietFor = most < 0.4f ? _quietFor + dt : 0f;
        if (age > PokeLife || (age > 0.4f && _quietFor > 0.25f)) { DollDown(); return false; }
        return true;
    }

    private static Vector3 U(float[] v) => new Vector3(v[0], v[2], v[1]);
    private static Quaternion UQ(float[] q) => new Quaternion(-q[0], -q[2], -q[1], q[3]);
    private static void Nm(Vector3 v, float[] o) { o[0] = v.x; o[1] = v.z; o[2] = v.y; }

    private void Kick(EBodyPart part, Vector3 point, Vector3 dir, float strength, bool heavy = false)
    {
        if (_p == null) _p = GetComponent<Player>();
        if (_bones == null || _bones[0] == null) _bones = _p != null ? NmBody.MapBones(_p) : null;
        if (_bones == null) return;
        // a plainly held gun at the moment of a hit: a sample for its right-hand grip marker
        try
        {
            Transform root = NmBody.B(_p.PlayerBones?.WeaponRoot);
            if (root != null && root.childCount > 0 && _bones[20] != null)
                GunMarkers.Observe(GunMarkers.KeyOf(_p, root.GetChild(0)), root.GetChild(0), _bones[20]);
        }
        catch { }
        if (strength <= 0f) return;

        // What a hit there moves, by the bone actually hit. A limb hit moves only that piece of the
        // limb (the rest of it rides along as its child); the hips take it without visible motion;
        // a torso hit bends the spine from the waist up, never the legs; the head lags a chest hit.
        int bone = HitBone(point);
        if (heavy && HeavyKickOn(bone)) { _moving = true; return; }
        switch (bone)
        {
            case 0: return;                                                   // hips: nothing visible
            case 1: case 2: case 4: case 5: Add(bone, 1f); break;            // thigh or shin alone
            case 3: case 6: Add(bone - 1, 0.5f); break;                      // foot: a little through the shin, the foot stays put
            case 7: case 8: Add(8, 1f); Add(7, 0.8f); Add(9, 0.3f); break;    // belly: folds at the waist
            case 9: case 10: Add(10, 1f); Add(9, 0.8f); Add(8, 0.3f); Add(11, -0.3f); Add(12, -0.25f); break; // chest
            case 11: Add(11, 1f); Add(12, 0.6f); break;                       // neck
            case 12: Add(12, 1f); Add(11, 0.5f); break;                       // head
            case 13: case 14: case 17: case 18: Add(10, 0.7f); Add(9, 0.3f); break; // shoulder/upper arm: the shoulder jolts (twists the chest), arms and gun together
            case 15: case 16: case 19: case 20: Add(10, 0.35f); break;        // forearm/hand: a small jolt through the shoulder
            default: return;
        }
        _moving = true;

        bool HeavyKickOn(int b)
        {
            float limp = EuphoriaPlugin.Instance?.HeavyNudgeTime.Value ?? 1f;
            switch (b)
            {
                case 1: case 2: case 3: Limp(1, 1f, limp); Limp(2, 0.8f, limp); return true;     // left leg
                case 4: case 5: case 6: Limp(4, 1f, limp); Limp(5, 0.8f, limp); return true;     // right leg
                case 13: case 14: case 15: case 16:                                                // left arm: limp
                    Limp(14, 1f, limp); Limp(15, 0.9f, limp); _leftArmLimp = true;
                    if (_leftArmSince == 0f) EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} heavy nudge - left arm limp for {limp:F1}s ({1f / Mathf.Max(Time.deltaTime, 1e-3f):F0} fps)");
                    _leftArmSince = Time.time;
                    return true;
                case 17: case 18: case 19: case 20:                                                // gun arm: a hard shoulder jolt
                    Limp(10, 1f, limp * 0.5f); Limp(9, 0.5f, limp * 0.5f); return true;
                default: return false;
            }
        }

        void Limp(int k, float w, float time)
        {
            if (_bones[k] == null) return;
            _limpUntil[k] = Time.time + time;
            _heavy[k] = true;
            Add(k, w * HeavyKick);
        }

        void Add(int k, float w)
        {
            Transform b = _bones[k];
            if (b == null) return;
            // spin from the push about the bone's joint; a hit straight down the bone's line just rocks it back
            Vector3 axis = Vector3.Cross(point - b.position, dir);
            if (axis.sqrMagnitude < 1e-6f) axis = Vector3.Cross(Vector3.up, dir);
            if (axis.sqrMagnitude < 1e-6f) return;
            _omega[k] += axis.normalized * (KickDeg * Mathf.Deg2Rad * strength * w);
        }
    }

    /// The bone whose segment the hit point is closest to.
    private int HitBone(Vector3 point)
    {
        int best = -1; float bd = float.MaxValue;
        for (int k = 0; k < 21; k++)
        {
            Transform b = _bones[k];
            if (b == null) continue;
            Vector3 a = b.position, e = SegEnd[k] >= 0 && _bones[SegEnd[k]] != null ? _bones[SegEnd[k]].position : a;
            Vector3 ab = e - a;
            float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector3.Dot(point - a, ab) / ab.sqrMagnitude) : 0f;
            float d = (point - (a + ab * t)).sqrMagnitude;
            if (d < bd) { bd = d; best = k; }
        }
        return best;
    }

    private void LateUpdate()
    {
        if (!_moving || _bones == null) { if (_bones != null && _gunSetValid) RideBegin(); _ride = false; return; }
        NmBody body = _p != null ? _p.GetComponent<NmBody>() : null;
        if (body != null && body.Busy || _p == null || !_p.HealthController.IsAlive) { Rest(); return; } // Euphoria owns the bones

        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        float w = 2f * Mathf.PI * Freq;
        RideBegin();
        bool any = false;
        foreach (int k in Kept) if (_bones[k] != null) _keptRot[k] = _bones[k].rotation;
        if (_na != System.IntPtr.Zero) any = DollPose(dt); // the physical nudge first: the springs (heavy nudge) ride on it
        int steps = Mathf.Max(1, Mathf.CeilToInt(dt / MaxStep));
        float h = dt / steps;
        foreach (int k in Nudged) // parent-first, so a child's nudge rides on its parent's
        {
            Transform b = _bones[k];
            if (b == null) continue;
            bool limp = Time.time < _limpUntil[k];
            float f = limp ? 2f * Mathf.PI * LimpFreq : _heavy[k] ? 2f * Mathf.PI * BackFreq : w;
            float z = limp ? LimpZeta : _heavy[k] ? BackZeta : Zeta;
            // a limp arm hangs: its weight turns it toward straight down
            Vector3 sag = Vector3.zero;
            if (limp && (k == 14 || k == 15) && _bones[k + 1] != null)
            {
                Vector3 along = _bones[k + 1].position - b.position;
                if (along.sqrMagnitude > 1e-6f) sag = Vector3.Cross(along.normalized, Vector3.down) * ArmSag;
            }
            float max = (_heavy[k] ? HeavyMax[k] : MaxDeg[k]) * Mathf.Deg2Rad;
            for (int s = 0; s < steps; s++)
            {
                Vector3 acc = -f * f * _theta[k] - 2f * z * f * _omega[k] + sag;
                _omega[k] += acc * h;
                _theta[k] += _omega[k] * h;
                if (_theta[k].magnitude > max) { _theta[k] = _theta[k].normalized * max; _omega[k] *= 0.5f; }
            }
            if (_heavy[k] && !limp && _theta[k].magnitude <= MaxDeg[k] * Mathf.Deg2Rad + 1e-3f) _heavy[k] = false; // back in its normal range
            float ang = _theta[k].magnitude;
            if (ang < 1e-4f && _omega[k].sqrMagnitude < 1e-6f) { _theta[k] = _omega[k] = Vector3.zero; continue; }
            any = true;
            b.rotation = Quaternion.AngleAxis(ang * Mathf.Rad2Deg, _theta[k] / ang) * b.rotation;
        }
        // a limp left hand goes with its arm; otherwise feet and hands keep the animation's angle
        _leftArmLimp = _leftArmLimp && (_heavy[14] || _heavy[15]);
        if (_leftArmSince > 0f && !_leftArmLimp)
        {
            EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} heavy nudge - left arm back on the animation {Time.time - _leftArmSince:F1}s after the last hit");
            _leftArmSince = 0f;
        }
        foreach (int k in Kept) if (_bones[k] != null && !(k == 16 && _leftArmLimp)) _bones[k].rotation = _keptRot[k];
        RideEnd();
        _moving = any;
    }

    private void Rest()
    {
        for (int k = 0; k < 21; k++) { _theta[k] = _omega[k] = Vector3.zero; _limpUntil[k] = 0f; _heavy[k] = false; }
        _leftArmLimp = false;
        _leftArmSince = 0f;
        _moving = false;
        _ride = false;
        DollDown();
    }
}
