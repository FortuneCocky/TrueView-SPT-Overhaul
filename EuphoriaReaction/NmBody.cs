using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using EFT;
using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// One Delirium (Euphoria-style) agent driving a victim's legs, spine
/// and head in LEAD mode (arms stay on the animation). Alive: stumble and
/// catch-step only, and if balance is lost the body goes back to the animation
/// before it hits the floor. Dead: a death struggle (stagger, collapse, writhe,
/// go limp) for the configured time, with EFT's corpse ragdoll held kinematic,
/// then the corpse ragdoll takes over.
///
/// Level collision: nearby box colliders exactly, readable mesh colliders, and
/// ray-sampled wall patches, all wound to face the doll (engine triangles are
/// one-sided: wrong winding is how bodies passed through walls).
/// </summary>
[DefaultExecutionOrder(32000)]
internal partial class NmBody : MonoBehaviour
{
    private const int Parts = 21;
    private const float Step = 1f / 60f;
    private const float MaxCarrySpeed = 5f;   // m/s of running momentum carried into the doll
    private const float BlendOut = 0.3f, GetUpBlend = 1.1f;
    private float _blendDur = BlendOut, _downSince;

    private static bool _init;
    internal const string BodyFile = "body.json";
    private static IntPtr _baseBuild;
    internal static IntPtr BaseBuild => _baseBuild;

    // NM parts measured by one child joint, for nm_build_scale (the pelvis and the spine are measured
    // their own way: hip width, and per spine segment).
    private static readonly int[] ChildOf =
    {
        -1, 2, 3, -1, 5, 6, -1, // pelvis, L thigh->calf->foot, R thigh->calf->foot
        -1, -1, -1, -1, 12, -1, // spine x4, neck->head, head
        14, 15, 16, -1,         // L clavicle->upper arm->forearm->hand (the clavicle: shoulder width)
        18, 19, 20, -1,         // R
    };

    private Player _p;
    private IntPtr _world, _agent, _build;
    private bool _ownBuild;
    private Transform[] _bones = new Transform[Parts];
    private readonly float[] _f3 = new float[3], _g3 = new float[3], _q4 = new float[4];
    private float _acc, _until;

    public bool Active => _agent != IntPtr.Zero;
    internal IntPtr Agent => _agent;
    public bool Fallen => _agent != IntPtr.Zero && nm_agent_has_fallen(_agent) == 1;

    public static void Init(string dir)
    {
        if (_init) return;
        _pluginDir = dir;
        // the physics character: our own, laid out on Tarkov's skeleton (character/make_character.py)
        string body = File.ReadAllText(Path.Combine(dir, BodyFile));
        if (nm_init_character(body) < 0)
            throw new Exception(BodyFile + ": " + LastError());
        _baseBuild = nm_build_from_character(body);
        if (_baseBuild == IntPtr.Zero) throw new Exception(BodyFile + " (build): " + LastError());
        LoadJointModel(body);
        GunMarkers.Load(dir);
        CaptureInit(dir);
        _init = true;
        NudgeDolls.Fill(); // spare nudge dolls, settled on a background thread before the first hit
    }

    // Behaviour tuning for the next shot or balance task (it is read when a task starts, so a change applies
    // from the next hit): one of our presets, or the player's own balance.txt. Engine = no overrides.
    internal static void ApplyBalance(string dir, BalancePreset preset)
    {
        string file = preset == BalancePreset.Custom ? Path.Combine(dir, "balance.txt")
            : Path.Combine(dir, "presets", "balance_" + preset.ToString().ToLowerInvariant() + ".txt");
        if (!File.Exists(file))
        {
            EuphoriaPlugin.Log($"Delirium: balance preset {preset} - {file} not found, engine tuning kept");
            return;
        }
        if (nm_load_task_params(file) < 0) EuphoriaPlugin.Log($"Delirium: balance preset {preset} - {LastError()}");
        else { EuphoriaPlugin.Log($"Delirium: balance preset {preset} ({Path.GetFileName(file)})"); NudgeDolls.Flush(); }
    }

    // ---------------------------------------------------------------- bones

    internal static Transform B(BifacialTransform b) => b?.Original == null ? null : ((Component)b.Original).transform;

    // The bones under the last root Find walked, with their stripped names: MapBones asks 16 times
    // under the same pelvis, and each walk used to build a new array and two new strings per bone.
    private static Transform _findRoot;
    private static readonly List<Transform> _findAll = new List<Transform>(512);
    private static readonly List<string> _findNames = new List<string>(512);

    // First bone under `root` whose name (spaces/underscores stripped) contains any of `parts`.
    private static Transform Find(Transform root, params string[] parts)
    {
        if (root == null) return null;
        if (root != _findRoot)
        {
            _findAll.Clear(); _findNames.Clear();
            root.GetComponentsInChildren(true, _findAll);
            for (int i = 0; i < _findAll.Count; i++) _findNames.Add(_findAll[i].name.Replace("_", "").Replace(" ", ""));
            _findRoot = root;
        }
        foreach (string part in parts)
            for (int i = 0; i < _findAll.Count; i++)
                if (_findNames[i].IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) return _findAll[i];
        return null;
    }

    private static void FindDone() { _findRoot = null; _findAll.Clear(); _findNames.Clear(); }

    private Transform[] MapBones() => MapBones(_p);
    internal static Transform[] MapBones(Player _p)
    {
        PlayerBones pb = _p.PlayerBones;
        Transform pelvis = B(pb?.Pelvis), head = B(pb?.Head);
        if (pelvis == null || head == null) return null;
        var anim = _p.GetComponentInChildren<Animator>();
        Transform HB(HumanBodyBones h)
        {
            try { return anim != null && anim.isHuman ? anim.GetBoneTransform(h) : null; }
            catch { return null; }
        }
        Transform Or(params Transform[] c) { foreach (var t in c) if (t != null) return t; return null; }

        Transform neck = Or(pb.Neck, HB(HumanBodyBones.Neck), Find(pelvis, "humanneck", "neck"));
        // Spine = the real chain between pelvis and neck (ancestors of the head),
        // so no stray "spine"-named gear bone or ribcage sibling gets mapped,
        // and no two NM parts write the same bone.
        var chain = new List<Transform>();
        for (Transform t = (neck ?? head).parent; t != null && t != pelvis; t = t.parent)
            chain.Insert(0, t);
        Transform S(int i) => i >= 0 && i < chain.Count ? chain[i] : null;
        Transform s0, s1, s2, s3;
        switch (chain.Count)
        {
            case 0: s0 = s1 = s2 = s3 = null; break;
            case 1: s0 = S(0); s1 = s2 = s3 = null; break;
            case 2: s0 = S(0); s1 = s2 = null; s3 = S(1); break;
            case 3: s0 = S(0); s1 = S(1); s2 = null; s3 = S(2); break;
            default: s0 = S(0); s1 = S(1); s2 = S(2); s3 = S(chain.Count - 1); break;
        }

        var b = new Transform[Parts]
        {
            pelvis,
            Or(Find(pelvis, "lthigh1", "lthigh"), B(pb.LeftThigh1), HB(HumanBodyBones.LeftUpperLeg)),
            Or(Find(pelvis, "lcalf", "lshin"), HB(HumanBodyBones.LeftLowerLeg)),
            Or(Find(pelvis, "lfoot"), HB(HumanBodyBones.LeftFoot)),
            Or(Find(pelvis, "rthigh1", "rthigh"), B(pb.RightThigh1), HB(HumanBodyBones.RightUpperLeg)),
            Or(Find(pelvis, "rcalf", "rshin"), HB(HumanBodyBones.RightLowerLeg)),
            Or(Find(pelvis, "rfoot"), HB(HumanBodyBones.RightFoot)),
            s0, s1, s2, s3,
            neck,
            head,
            Or(Find(pelvis, "lcollarbone", "lclavicle"), B(pb.LeftShoulder), HB(HumanBodyBones.LeftShoulder)),
            Or(Find(pelvis, "lupperarm"), HB(HumanBodyBones.LeftUpperArm)),
            Or(Find(pelvis, "lforearm1", "lforearm"), HB(HumanBodyBones.LeftLowerArm)),
            Or(Find(pelvis, "lpalm"), pb.LeftPalm, HB(HumanBodyBones.LeftHand)),
            Or(Find(pelvis, "rcollarbone", "rclavicle"), B(pb.RightShoulder), HB(HumanBodyBones.RightShoulder)),
            Or(Find(pelvis, "rupperarm"), HB(HumanBodyBones.RightUpperArm)),
            Or(Find(pelvis, "rforearm1", "rforearm"), HB(HumanBodyBones.RightLowerArm)),
            Or(Find(pelvis, "rpalm"), pb.RightPalm, HB(HumanBodyBones.RightHand)),
        };
        // a calf is the thigh's child, a foot the calf's
        b[2] ??= b[1]?.childCount > 0 ? b[1].GetChild(0) : null;
        b[3] ??= b[2]?.childCount > 0 ? b[2].GetChild(0) : null;
        b[5] ??= b[4]?.childCount > 0 ? b[4].GetChild(0) : null;
        b[6] ??= b[5]?.childCount > 0 ? b[5].GetChild(0) : null;
        FindDone(); // gear can change before the next map
        PreferSkinned(_p, b); // a bone no mesh uses (gear brought its own) -> the one the mesh does
        return b;
    }

    // Homer stretched to this skeleton's limb lengths. nm_build_scale keeps
    // Homer's own part frames, joint limits and muscles, so it stays stable
    // (unlike remapping onto EFT bone axes, which collapses the doll).
    private IntPtr ScaledBuild()
    {
        var len = new float[Parts];
        for (int k = 0; k < Parts; k++) len[k] = 1f;
        float Homer(int a, int c)
        {
            float[] pa = new float[3], pc = new float[3];
            nm_build_part_joint(_baseBuild, a, pa);
            nm_build_part_joint(_baseBuild, c, pc);
            return new Vector3(pa[0] - pc[0], pa[1] - pc[1], pa[2] - pc[2]).magnitude;
        }
        float Ratio(Transform a, Transform c, float homer) =>
            a == null || c == null || homer < 0.02f ? 1f
                : Mathf.Clamp(Vector3.Distance(a.position, c.position) / homer, 0.7f, 1.4f);
        // every joint the doll has a bone for: limbs, clavicles (shoulder width), neck
        for (int k = 0; k < Parts; k++)
            if (ChildOf[k] >= 0) len[k] = Ratio(_bones[k], _bones[ChildOf[k]], Homer(k, ChildOf[k]));
        // spine: one ratio for the whole Spine1 -> neck chain. Per segment, and the pelvis by hip width, didn't
        // measure like for like on SPT's skeleton (every bot came out at the caps: hips 0.71, lower spine 1.40)
        // and squeezed the doll's pelvis and stretched its lower back; the pelvis keeps the doll's own size.
        float sr = Ratio(_bones[7], _bones[11], Homer(7, 11));
        for (int k = 7; k <= 10; k++) len[k] = sr;
        IntPtr b = nm_build_scale(ShapeBase, len, 1f);
        EuphoriaPlugin.Log($"Delirium: build fit to SPT - legs {len[1]:F2}/{len[2]:F2} {len[4]:F2}/{len[5]:F2} spine {sr:F2} neck {len[11]:F2}"
                           + $" shoulders {len[13]:F2}/{len[17]:F2} arms {len[14]:F2}/{len[15]:F2} {len[18]:F2}/{len[19]:F2} -> {(b != IntPtr.Zero ? "ok" : LastError())}");
        return b;
    }

    // ---------------------------------------------------------------- world

    // Shared hit buffer and per-collider verdicts for the rays below. They run dozens of times a frame
    // per reacting bot; RaycastAll (a new array each call) and collider.name (a new string per hit) made
    // this mod the biggest garbage source in a raid.
    private static readonly RaycastHit[] _rayHits = new RaycastHit[64];
    private static readonly Dictionary<Collider, bool> _blocker = new Dictionary<Collider, bool>();
    private static readonly Dictionary<Collider, bool> _onPlayer = new Dictionary<Collider, bool>();

    private static bool IsBlocker(Collider c)
    {
        if (_blocker.TryGetValue(c, out bool b)) return b;
        if (_blocker.Count > 8000) _blocker.Clear();
        return _blocker[c] = c.name.IndexOf("blocker", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool OnPlayer(Collider c)
    {
        if (_onPlayer.TryGetValue(c, out bool b)) return b;
        if (_onPlayer.Count > 8000) _onPlayer.Clear();
        return _onPlayer[c] = c.GetComponentInParent<Player>() != null || IsRagdoll(c);
    }

    // A corpse is no longer under a Player (EFT moves the ragdoll to its corpse object), so it read as
    // floor: roots went up onto dead bodies and were left in the air when those settled or moved.
    private static bool IsRagdoll(Collider c)
    {
        Rigidbody rb = c.attachedRigidbody;
        return rb != null && (rb.GetComponent<CharacterJoint>() != null || rb.GetComponentInChildren<CharacterJoint>(true) != null);
    }

    // Downward ground ray on the non-character layers, skipping invisible "blocker" colliders.
    private static bool GroundRay(Vector3 from, int mask, out RaycastHit best)
    {
        best = default;
        float bd = float.MaxValue;
        bool any = false;
        int n = Physics.RaycastNonAlloc(from, Vector3.down, _rayHits, 8f, mask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            RaycastHit h = _rayHits[i];
            if (h.collider == null || h.distance >= bd) continue;
            if (IsBlocker(h.collider)) continue;
            if (OnPlayer(h.collider)) continue; // bodies and corpses are not ground
            best = h; bd = h.distance; any = true;
        }
        return any;
    }

    // Raycast that ignores every character's colliders (the victim's own
    // hitboxes would otherwise become "floor" inside his legs, and the
    // shooter would become an invisible wall).
    private static bool RayWorld(Vector3 from, Vector3 dir, float dist, out RaycastHit best)
    {
        best = default;
        float bd = float.MaxValue;
        bool any = false;
        int n = Physics.RaycastNonAlloc(from, dir, _rayHits, dist, WorldMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            RaycastHit h = _rayHits[i];
            if (h.collider == null || h.distance >= bd) continue;
            if (OnPlayer(h.collider)) continue;
            if (IsBlocker(h.collider)) continue; // invisible AI/player blockers are not ground
            best = h; bd = h.distance; any = true;
        }
        return any;
    }

    // Engine triangles are ONE-SIDED: a part is only pushed out along the
    // face normal, so every triangle must face away from the solid (toward
    // the doll). Wrong or doubled winding is why bodies passed through walls.
    private static void Tri(List<float> pos, List<uint> tris, Vector3 a, Vector3 b, Vector3 c, Vector3 outward)
    {
        Vector3 na = new Vector3(a.x, a.z, a.y), nb = new Vector3(b.x, b.z, b.y), nc = new Vector3(c.x, c.z, c.y);
        Vector3 want = new Vector3(outward.x, outward.z, outward.y);
        if (Vector3.Dot(Vector3.Cross(nb - na, nc - na), want) < 0f) { var t = nb; nb = nc; nc = t; }
        uint n = (uint)(pos.Count / 3);
        pos.Add(na.x); pos.Add(na.y); pos.Add(na.z); // (a new 3-vector array here per triangle was most of a bake's garbage)
        pos.Add(nb.x); pos.Add(nb.y); pos.Add(nb.z);
        pos.Add(nc.x); pos.Add(nc.y); pos.Add(nc.z);
        tris.Add(n); tris.Add(n + 1); tris.Add(n + 2);
    }

    private static void Quad(List<float> pos, List<uint> tris, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward)
    {
        Tri(pos, tris, a, b, c, outward);
        Tri(pos, tris, a, c, d, outward);
    }

    // Does this box (world centre/half/rot) overlap the victim's standing capsule?
    private static bool HitsCapsule(Vector3 centre, Vector3 half, Quaternion rot, Vector3 feet, float radius)
    {
        Quaternion inv = Quaternion.Inverse(rot);
        Vector3 l0 = inv * (feet + Vector3.up * 0.35f - centre), l1 = inv * (feet + Vector3.up * 1.6f - centre);
        for (int s = 0; s <= 6; s++)
        {
            Vector3 pt = Vector3.Lerp(l0, l1, s / 6f);
            Vector3 cl = Vector3.Min(Vector3.Max(pt, -half), half);
            if ((pt - cl).sqrMagnitude < radius * radius) return true;
        }
        return false;
    }

    // Layers used by character colliders (hitboxes/capsules), so ground rays skip people.
    // Kept for a second: the trip, brace and steadying-hand checks ask several times a frame, and
    // GetComponentsInChildren made a new collider array every time.
    private int _charLayers;
    private float _charLayersAt = -10f;
    private Player _charLayersOf;
    private static readonly List<Collider> _charCols = new List<Collider>();

    private int CharacterLayers(Player p)
    {
        if (p == _charLayersOf && Time.time - _charLayersAt < 1f) return _charLayers;
        int m = 1 << p.gameObject.layer;
        p.GetComponentsInChildren(true, _charCols);
        for (int i = 0; i < _charCols.Count; i++) m |= 1 << _charCols[i].gameObject.layer;
        _charCols.Clear();
        _charLayersOf = p; _charLayersAt = Time.time; _charLayers = m;
        return m;
    }

    private int _groundPatches, _rebakes;
    private Vector3 _bakeCentre;
    private float _nextRebake, _hoverSince;

    // Real ground height under a point (falls back to the attach floor).
    private float GroundUnder(Vector3 at)
    {
        return RayWorld(at + Vector3.up * 0.3f, Vector3.down, 6f, out RaycastHit h) ? h.point.y : _floorY;
    }

    /// Mirror the level around the victim into the engine: exact box
    /// colliders, readable mesh colliders, and ray-sampled wall patches for
    /// everything else (most EFT level meshes aren't CPU-readable).
    // The bake's working buffers, shared (bakes run one at a time on the main thread): it ran at every
    // takeover and up to 8 rebakes each, and building new lists, grids and arrays each time made it
    // the mod's biggest allocation.
    private static readonly List<float> _bakePos = new List<float>(8192);
    private static readonly List<uint> _bakeTris = new List<uint>(4096);
    private static readonly HashSet<Collider> _bakeDone = new HashSet<Collider>();
    private static readonly Collider[] _bakeCols = new Collider[1024];
    private static readonly List<int> _bakeIdx = new List<int>();
    private static readonly List<Vector3> _bakeVerts = new List<Vector3>();
    private static float[] _bakePosArr = new float[0];
    private static uint[] _bakeTriArr = new uint[0];
    private static byte[] _bakeMat = new byte[0]; // never written: stays all zero (default material)
    private static readonly float[] BakeHeights = { 0.35f, 0.85f, 1.35f, 1.85f };
    private static float[,] _gHy;
    private static bool[,] _gFloor, _gPatched;
    private static RaycastHit[,] _gHits;

    private static T[] Fit<T>(T[] a, int n) => a.Length >= n ? a : new T[Mathf.NextPowerOfTwo(n)];

    private static readonly int[] BakeFeet = { 3, 6 };

    private void BakeLevel(Player p, Vector3 bakeAt)
    {
        long pf = PerfNow();
        var pos = _bakePos; pos.Clear();
        var tris = _bakeTris; tris.Clear();
        var baked = _bakeDone; baked.Clear();
        Vector3 feet = bakeAt;
        int boxes = 0, meshTris = 0, patches = 0, ground = 0, columns = 0, shapes = 0, pads = 0, risers = 0;
        const int MaxTris = 14000;
        try
        {
            GroundPoints.Clear(); WallPoints.Clear();
            int noChars = ~CharacterLayers(p) & WorldMask;
            // Real shapes first (boxes, readable meshes, capsules/spheres, thin rails and beams),
            // so the ground grid below can leave whatever they already cover alone.
            Vector3 sphereAt = feet + Vector3.up;
            int found = Physics.OverlapSphereNonAlloc(sphereAt, 6f, _bakeCols, WorldMask, QueryTriggerInteraction.Ignore);
            Collider[] cols = _bakeCols;
            if (found >= _bakeCols.Length) // more than the buffer holds (rare): the full list, as before
            {
                cols = Physics.OverlapSphere(sphereAt, 6f, WorldMask, QueryTriggerInteraction.Ignore);
                found = cols.Length;
            }
            for (int ci = 0; ci < found; ci++)
            {
                Collider c = cols[ci];
                if (tris.Count / 3 > MaxTris) break;
                if (c == null || !c.enabled || c.isTrigger) continue;
                if (OnPlayer(c)) continue;
                if (IsDynamic(c)) continue; // doors and loose props go in the moving bubble, not the static bake
                if (IsBlocker(c)) continue;
                if (c is BoxCollider bc)
                {
                    Transform t = c.transform;
                    Vector3 half = Vector3.Scale(bc.size, t.lossyScale) * 0.5f;
                    half = new Vector3(Mathf.Abs(half.x), Mathf.Abs(half.y), Mathf.Abs(half.z));
                    if (half.x < 0.01f || half.y < 0.01f || half.z < 0.01f || half.magnitude > 60f) continue;
                    Vector3 centre = t.TransformPoint(bc.center);
                    Quaternion rot = t.rotation;
                    if (BlocksBody(centre, half, rot, feet)) continue; // never spawn the doll inside a solid
                    Box(pos, tris, centre, half, rot);
                    baked.Add(c); boxes++;
                }
                else if (c is MeshCollider mc && mc.sharedMesh != null && mc.sharedMesh.isReadable && !mc.convex)
                {
                    Mesh m = mc.sharedMesh;
                    // the same triangles m.triangles gives (every submesh in order), into reused lists
                    long indices = 0;
                    for (int s = 0; s < m.subMeshCount; s++) indices += m.GetIndexCount(s);
                    if (indices / 3 > 60000) continue;
                    m.GetVertices(_bakeVerts);
                    Matrix4x4 w = c.transform.localToWorldMatrix;
                    bool flip = w.determinant < 0f;
                    int added = 0;
                    for (int s = 0; s < m.subMeshCount && tris.Count / 3 < MaxTris; s++)
                    {
                        if (m.GetTopology(s) != MeshTopology.Triangles) continue;
                        m.GetTriangles(_bakeIdx, s);
                        var idx = _bakeIdx; var v = _bakeVerts;
                        for (int i = 0; i + 2 < idx.Count && tris.Count / 3 < MaxTris; i += 3)
                        {
                            Vector3 a = w.MultiplyPoint3x4(v[idx[i]]), b = w.MultiplyPoint3x4(v[idx[i + 1]]), cc = w.MultiplyPoint3x4(v[idx[i + 2]]);
                            Vector3 mid = (a + b + cc) / 3f, rel = mid - feet;
                            if (new Vector2(rel.x, rel.z).sqrMagnitude > 36f || rel.y < -1.5f || rel.y > 3.5f) continue;
                            // skip faces through the victim's own standing space
                            if (new Vector2(rel.x, rel.z).sqrMagnitude < 0.09f && rel.y > 0.1f && rel.y < 1.9f) continue;
                            Vector3 n = Vector3.Cross(b - a, cc - a); // Unity front-face normal
                            if (n.sqrMagnitude < 1e-10f) continue;
                            Tri(pos, tris, a, b, cc, flip ? -n : n);
                            added++;
                        }
                    }
                    _bakeIdx.Clear(); _bakeVerts.Clear();
                    if (added > 0) { baked.Add(c); meshTris += added; }
                }
                else if (c is CapsuleCollider cap)
                {
                    // railings, pipes, posts: a rounded bar a body can roll over
                    Transform t = c.transform;
                    Vector3 s = t.lossyScale;
                    Vector3 axisLocal = cap.direction == 0 ? Vector3.right : cap.direction == 1 ? Vector3.up : Vector3.forward;
                    float axisScale = Mathf.Abs(cap.direction == 0 ? s.x : cap.direction == 1 ? s.y : s.z);
                    float radScale = cap.direction == 0 ? Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)) : cap.direction == 1 ? Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z)) : Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y));
                    float r = cap.radius * radScale;
                    float halfLen = Mathf.Max(0f, cap.height * axisScale * 0.5f - r);
                    if (r < 0.01f || r > 3f || halfLen > 30f) continue;
                    Vector3 centre = t.TransformPoint(cap.center), axis = (t.rotation * axisLocal).normalized;
                    if (BlocksBody(centre, new Vector3(r, r, halfLen + r), Quaternion.LookRotation(axis), feet)) continue;
                    Bar(pos, tris, centre - axis * halfLen, centre + axis * halfLen, r);
                    baked.Add(c); shapes++; WallPoints.Add(centre);
                }
                else if (c is SphereCollider sph)
                {
                    Transform t = c.transform;
                    Vector3 s = t.lossyScale;
                    float r = sph.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
                    if (r < 0.01f || r > 3f) continue;
                    Vector3 centre = t.TransformPoint(sph.center);
                    if (BlocksBody(centre, Vector3.one * r, Quaternion.identity, feet)) continue;
                    Bar(pos, tris, centre - Vector3.up * r * 0.3f, centre + Vector3.up * r * 0.3f, r * 0.95f);
                    baked.Add(c); shapes++; WallPoints.Add(centre);
                }
                else if (c is MeshCollider tm && tm.sharedMesh != null)
                {
                    // Level meshes EFT won't let us read: a thin one (rail, beam, pipe, ledge lip) is
                    // well described by its own oriented bounds; big ones are left to the rays below.
                    Transform t = c.transform;
                    Bounds lb;
                    try { lb = tm.sharedMesh.bounds; } catch { continue; } // unreadable here: the rays cover it
                    Vector3 half = Vector3.Scale(lb.extents, t.lossyScale);
                    half = new Vector3(Mathf.Abs(half.x), Mathf.Abs(half.y), Mathf.Abs(half.z));
                    float thin = Mathf.Min(half.x, Mathf.Min(half.y, half.z)) * 2f, longest = Mathf.Max(half.x, Mathf.Max(half.y, half.z)) * 2f;
                    if (thin > ThinShape || longest > MaxThinLength || longest < 0.2f) continue;
                    Vector3 centre = t.TransformPoint(lb.center);
                    Quaternion rot = t.rotation;
                    if (BlocksBody(centre, half, rot, feet)) continue;
                    Box(pos, tris, centre, Vector3.Max(half, Vector3.one * 0.015f), rot);
                    baked.Add(c); shapes++; WallPoints.Add(centre);
                }
            }

            // Real ground (no infinite plane: that held bodies up in mid-air after a step or ledge).
            // Near him: one continuous surface through height samples every GroundCell, so feet plant
            // and slide on it like a real floor (overlapping per-point squares left little ridges that
            // snagged catch-steps). Where a cell spans a step (corners more than GroundSeam apart) it
            // keeps per-point squares instead of a ramp. Beyond GroundNear: per-point squares as before.
            int gridR = Mathf.RoundToInt(GroundNear / GroundCell);
            int size = gridR * 2 + 1;
            if (_gFloor == null || _gFloor.GetLength(0) != size)
            {
                _gHy = new float[size, size]; _gFloor = new bool[size, size];
                _gPatched = new bool[size, size]; _gHits = new RaycastHit[size, size];
            }
            var hy = _gHy;
            var floor = _gFloor;
            var hits = _gHits;
            Array.Clear(floor, 0, floor.Length); // hy and hits are only read where floor is set
            Array.Clear(_gPatched, 0, _gPatched.Length);
            // every ray below, cast together first (NmBodyBakeRays): the grid, its outer ring, the wall rays
            BakeRaysClear();
            for (int i = 0; i < size; i++)
                for (int j = 0; j < size; j++)
                    BakeRayAdd(feet + new Vector3((i - gridR) * GroundCell, 2.5f, (j - gridR) * GroundCell), Vector3.down, 8f, noChars);
            int ringRays = _brFrom.Count;
            for (float gx = -7f; gx <= 7.01f; gx += 0.5f)
                for (float gz = -7f; gz <= 7.01f; gz += 0.5f)
                    if (!(Mathf.Abs(gx) <= GroundNear && Mathf.Abs(gz) <= GroundNear))
                        BakeRayAdd(feet + new Vector3(gx, 2.5f, gz), Vector3.down, 8f, noChars);
            int wallRays = _brFrom.Count;
            for (int i = 0; i < 24; i++)
            {
                Vector3 dir = Quaternion.Euler(0, i * 15f, 0) * Vector3.forward;
                foreach (float h in BakeHeights) BakeRayAdd(feet + Vector3.up * h, dir, 6f, WorldMask);
            }
            BakeRaysCast();
            for (int i = 0; i < size; i++)
                for (int j = 0; j < size; j++)
                {
                    if (tris.Count / 3 > MaxTris) break;
                    Vector3 from = feet + new Vector3((i - gridR) * GroundCell, 2.5f, (j - gridR) * GroundCell);
                    if (GroundSample(from, noChars, feet, baked, pos, tris, ref columns, out RaycastHit gh, i * size + j))
                    { floor[i, j] = true; hy[i, j] = gh.point.y; hits[i, j] = gh; GroundPoints.Add(gh.point); }
                }
            var patched = _gPatched;
            for (int i = 0; i < size - 1; i++)
                for (int j = 0; j < size - 1; j++)
                {
                    if (!floor[i, j] || !floor[i + 1, j] || !floor[i, j + 1] || !floor[i + 1, j + 1]) continue;
                    float lo = Mathf.Min(Mathf.Min(hy[i, j], hy[i + 1, j]), Mathf.Min(hy[i, j + 1], hy[i + 1, j + 1]));
                    float hi = Mathf.Max(Mathf.Max(hy[i, j], hy[i + 1, j]), Mathf.Max(hy[i, j + 1], hy[i + 1, j + 1]));
                    if (hi - lo > GroundSeam)
                    {
                        // a step: no ramp over it; its corners get squares (below) so nothing is left open
                        patched[i, j] = patched[i + 1, j] = patched[i, j + 1] = patched[i + 1, j + 1] = true;
                        continue;
                    }
                    Vector3 a = hits[i, j].point, b = hits[i + 1, j].point, c = hits[i + 1, j + 1].point, d = hits[i, j + 1].point;
                    Tri(pos, tris, a, b, c, Vector3.up);
                    Tri(pos, tris, a, c, d, Vector3.up);
                    ground += 2;
                }
            // step faces: the squares either side of a step left its riser open, and a limb slid in under
            // the upper tread (one-sided, invisible from below) and the body went through the stairs to the
            // floor below. A wall from the lower tread up to the upper one, facing the lower side, closes it.
            for (int i = 0; i < size; i++)
                for (int j = 0; j < size; j++)
                    for (int dir = 0; dir < 2; dir++)
                    {
                        int i2 = dir == 0 ? i + 1 : i, j2 = dir == 0 ? j : j + 1;
                        if (i2 >= size || j2 >= size || !floor[i, j] || !floor[i2, j2]) continue;
                        float dh = hy[i2, j2] - hy[i, j];
                        if (Mathf.Abs(dh) <= GroundSeam || Mathf.Abs(dh) > StepRiserMax) continue;
                        Vector3 lo = dh > 0f ? hits[i, j].point : hits[i2, j2].point, hiP = dh > 0f ? hits[i2, j2].point : hits[i, j].point;
                        Vector3 face = new Vector3(lo.x - hiP.x, 0f, lo.z - hiP.z).normalized; // toward the lower tread
                        Vector3 mid = (lo + hiP) * 0.5f, side = Vector3.Cross(Vector3.up, face) * (GroundCell * 0.55f);
                        float y0 = Mathf.Min(lo.y, hiP.y) - 0.02f, y1 = Mathf.Max(lo.y, hiP.y);
                        Vector3 b0 = new Vector3(mid.x, y0, mid.z), b1 = new Vector3(mid.x, y1, mid.z);
                        Quad(pos, tris, b0 - side, b0 + side, b1 + side, b1 - side, face);
                        risers++;
                    }
            // samples no cell covers (edges of the grid, beside a hole) or beside a step: a square each
            for (int i = 0; i < size; i++)
                for (int j = 0; j < size; j++)
                {
                    if (!floor[i, j]) continue;
                    bool covered = (i > 0 && j > 0 && floor[i - 1, j - 1] && floor[i, j - 1] && floor[i - 1, j]) ||
                                   (i < size - 1 && j < size - 1 && floor[i + 1, j + 1] && floor[i, j + 1] && floor[i + 1, j]) ||
                                   (i > 0 && j < size - 1 && floor[i - 1, j + 1] && floor[i - 1, j] && floor[i, j + 1]) ||
                                   (i < size - 1 && j > 0 && floor[i + 1, j - 1] && floor[i + 1, j] && floor[i, j - 1]);
                    if (covered && !patched[i, j]) continue;
                    GroundSquare(pos, tris, hits[i, j], GroundCell * 0.55f);
                    ground++;
                }
            // the outer ring, per-point squares as before
            int ring = ringRays;
            for (float gx = -7f; gx <= 7.01f; gx += 0.5f)
                for (float gz = -7f; gz <= 7.01f; gz += 0.5f)
                {
                    if (tris.Count / 3 > MaxTris) break;
                    if (Mathf.Abs(gx) <= GroundNear && Mathf.Abs(gz) <= GroundNear) continue;
                    int ray = ring++;
                    Vector3 from = feet + new Vector3(gx, 2.5f, gz);
                    if (!GroundSample(from, noChars, feet, baked, pos, tris, ref columns, out RaycastHit gh, ray)) continue;
                    GroundSquare(pos, tris, gh, 0.27f); // 0.54 m squares on a 0.5 m grid: overlap, no gaps
                    ground++;
                    GroundPoints.Add(gh.point);
                }

            // Support under each foot: the grid can have holes (a sample ray that missed, a cell that
            // spans a step), and a doll with no floor under a foot hangs there. A flat square at the real
            // floor under the foot fills any hole and changes nothing where the grid already has ground.
            if (_bones != null)
                foreach (int fk in BakeFeet)
                {
                    Transform ft = _bones[fk];
                    if (ft == null || !RayWorld(ft.position + Vector3.up * 0.5f, Vector3.down, 3f, out RaycastHit fh) || fh.normal.y < 0.5f) continue;
                    if (baked.Contains(fh.collider)) continue; // a real shape: already in the bake
                    GroundSquare(pos, tris, fh, 0.2f);
                    pads++;
                }

            // Ray patches for the rest: small quads on every surface the rays
            // find, facing back along the hit normal.
            float[] heights = BakeHeights;
            for (int i = 0; i < 24 && tris.Count / 3 < MaxTris; i++)
            {
                Vector3 dir = Quaternion.Euler(0, i * 15f, 0) * Vector3.forward;
                int hk = 0;
                foreach (float h in heights)
                {
                    if (!BakeRayHit(wallRays + i * heights.Length + hk++, out RaycastHit hit)) continue;
                    if (baked.Contains(hit.collider) || hit.distance < 0.3f || Mathf.Abs(hit.normal.y) > 0.75f) continue;
                    Vector3 n = hit.normal;
                    Vector3 r = Vector3.Cross(Vector3.up, n);
                    if (r.sqrMagnitude < 1e-4f) continue;
                    r = r.normalized * Mathf.Max(0.35f, hit.distance * 0.15f + 0.1f);
                    Vector3 u = Vector3.Cross(n, r).normalized * 0.3f;
                    Vector3 o = hit.point;
                    Quad(pos, tris, o - r - u, o + r - u, o + r + u, o - r + u, n);
                    patches++;
                    WallPoints.Add(o);
                }
            }
            if (tris.Count > 0)
            {
                // reused arrays, only as long as needed: the engine reads the counts, not the lengths
                _bakePosArr = Fit(_bakePosArr, pos.Count); pos.CopyTo(_bakePosArr);
                _bakeTriArr = Fit(_bakeTriArr, tris.Count); tris.CopyTo(_bakeTriArr);
                _bakeMat = Fit(_bakeMat, tris.Count / 3);
                nm_world_add_mesh(_world, _bakePosArr, pos.Count / 3, _bakeTriArr, tris.Count / 3, _bakeMat);
            }
        }
        catch (Exception e) { EuphoriaPlugin.Log("Delirium: level bake: " + e.Message); }
        PerfBake(pf);
        EuphoriaPlugin.Log($"Delirium: level bake {boxes} boxes, {shapes} rails/pipes/beams, {meshTris} mesh tris, {patches} wall patches, {ground} ground patches, {risers} step faces, {columns} obstacle columns, {pads} foot supports ({tris.Count / 3} tris)");
        _groundPatches = ground;
        _bakeCentre = feet;
    }

    // ---------------------------------------------------------------- attach

    /// Any pose can be taken over: crouched and prone bots are seeded through the rig binding, and
    /// Attach refuses (and logs why) only when it can't build one for this bot.
    internal static bool PoseAllowed(Player p) => p != null;

    /// Takes over the body. `dying`: the bot is already dead (death struggle).
    public bool Attach(Player p, bool dying = false)
    {
        // The doll is built standing. A crouched or prone bot is seeded onto his live pose through the
        // rig binding (NmBodyRig) and holds that pose; without a usable binding he keeps his own
        // animation (and SPT's normal ragdoll when he dies).
        if (!PoseAllowed(p)) return false;
        long pf = PerfNow();
        PerfReset();
        float level = 1f;
        try { level = p.MovementContext?.PoseLevel ?? 1f; } catch { }
        _startPose = PhaseRules.PoseFromGame(IsProne(p), level, StandLevel);
        _p = p;
        _bones = MapBones();
        TwinAttach();
        if (_bones == null || _bones[0] == null || _bones[12] == null)
        {
            EuphoriaPlugin.Log($"Delirium: no usable skeleton on {p.ProfileId}");
            return false;
        }
        for (int k = 0; k < Parts; k++)
            if (_bones[k] == null) EuphoriaPlugin.Log($"Delirium: part {k} unmapped (skipped) on {p.ProfileId}");
        _blending = false;
        _getUp = null;
        _dying = false;
        _corpse.Clear(); _corpseWasKinematic.Clear();
        _corpseGate = null; _corpseRag = null;
        _downSince = 0f; _armsSince = 0f;
        _blendDur = BlendOut;
        _groundLift = 0f;
        _vaultLogged = false;
        _supportLogged = false;
        _supportSince = 0f;
        _bracing = false;
        _nextBrace = 0f;
        _matching = false;
        _rolling = false;
        _catchFallSent = false;
        _fallingUntil = 0f; _fallLogged = false; _catchDueAt = 0f;
        RecoverAttach();
        AdaptAttach();
        SettleAttach();
        OpenAttach();
        GapAttach();
        HingeAttach();
        _crouchStumble = false; _staggering = false;
        _deathStyle = DeathStyle.Agony; // a death that never went through Die (bleed-out) struggles
        _stableFor = 0f;
        _lastHitAt = Time.time;
        _nextImpact = 0f;
        System.Array.Clear(_contactPrev, 0, _contactPrev.Length);

        _ownBuild = true;
        _build = _ownBuild ? ChooseBuild() : _baseBuild; // stretched doll, or built on SPT's skeleton (NmBodyFit)
        if (_build == IntPtr.Zero) { _ownBuild = false; _build = _baseBuild; }
        _bind = null;
        if (_startPose != StartPose.Stand)
        {
            _bind = ComputeBinding();
            if (!_bind.Valid)
            {
                EuphoriaPlugin.Log($"Delirium: {p.ProfileId} is {_startPose.ToString().ToLowerInvariant()} but has no rig binding ({_bind.Source}) - Euphoria skipped");
                if (_ownBuild && _build != IntPtr.Zero) nm_build_destroy(_build);
                _build = IntPtr.Zero; _ownBuild = false; _bind = null;
                return false;
            }
        }
        else
        {
            // standing: not needed now, but a binding that had to read his legs live is only valid
            // from a standing moment, so take it now for when he's crouched or prone later
            try { _standBind = ComputeBinding(); } catch { _standBind = null; }
        }

        _world = nm_world_create();
        Vector3 at = p.Transform.position;
        // The real ground, however far down: a bot taken over mid-jump or mid-fall keeps his height and
        // falls onto it (the floor used to be put at his feet, so he stood on thin air).
        float floorY = at.y - 30f;
        if (RayWorld(at + Vector3.up * 0.5f, Vector3.down, 60f, out RaycastHit g) && g.point.y <= at.y + 0.25f)
            floorY = g.point.y;
        // in the air: his feet well off the ground under him, or the game's movement says so (a jump's
        // take-off and landing are under AirGap, and he still has the jump's speed then)
        bool gameAir = GameAirborne(p, out float freefall);
        _airborne = at.y - floorY > AirGap || (gameAir && _startPose == StartPose.Stand);
        float feetY = _airborne ? Mathf.Max(at.y, floorY) : floorY;
        _inAir = false;
        _floorY = floorY;
        GroundAttach(); // his ankle height, pins and the steadying hand reset (NmBodyGround)
        TripAttach();   // trips, rails and stairs (NmBodyTrip)
        _rebakes = 0; _nextRebake = 0f; _hoverSince = 0f;
        BakeLevel(p, new Vector3(at.x, floorY, at.z));
        // fallback plane: real floor if the ground sampling found nothing,
        // otherwise a deep safety net so nothing falls forever
        nm_world_add_floor(_world, at.x, at.z, _groundPatches > 0 ? floorY - 30f : floorY, 60f);

        // Homer faces NM -Y: a bot at Unity yaw theta needs NM yaw PI - theta.
        float theta = p.Transform.eulerAngles.y * Mathf.Deg2Rad;
        _theta = theta;
        ToNm(new Vector3(at.x, feetY, at.z), _f3);
        _agent = nm_agent_create(_build, _f3, Mathf.PI - theta, UnityEngine.Random.value);
        if (_agent == IntPtr.Zero) { EuphoriaPlugin.Log("Delirium: agent_create " + LastError()); Release(); return false; }

        // standing reference for the get-up clips, from the doll as created (standing) before any seeding
        CaptureStandingReference();

        Vector3 fwd = p.Transform.forward; fwd.y = 0f;
        ToNm(fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward, _f3);
        if (_startPose == StartPose.Prone) TuneDown();
        else if (!_airborne) // in the air the balance waits for the landing (AirBegin below, NmBodyAir)
        {
            nm_agent_task_balance(_agent, _world, "*", _f3);
            TuneAlive();
        }
        if (_startPose != StartPose.Stand && !SeedFromBinding()) { Release(); return false; }
        if (_startPose == StartPose.Stand && _airborne) SeedAirPose(_standBind); // in his jump pose, not straight-legged

        // Carry running momentum: horizontal, clamped (teleports/hitches
        // read as tens of m/s and used to launch the doll).
        Vector3 vel = Vector3.zero;
        try { vel = p.Velocity; } catch { }
        float gameVy = vel.y;
        float vy = _airborne && !float.IsNaN(vel.y) ? Mathf.Clamp(vel.y, -15f, 5f) : 0f; // in the air: keep the rise/fall
        // no vertical speed from the game but he's been falling a while (off a ledge, not a jump): gravity's
        if (_airborne && Mathf.Abs(vy) < 0.05f && !InJump(p) && freefall > 0.05f) vy = Mathf.Max(-9.81f * freefall, -15f);
        vel.y = 0f;
        if (float.IsNaN(vel.x) || float.IsNaN(vel.z)) vel = Vector3.zero;
        vel = Vector3.ClampMagnitude(vel, MaxCarrySpeed);
        vel.y = vy;
        if (vel.sqrMagnitude > 0.01f) { ToNm(vel, _f3); nm_agent_add_velocity(_agent, _f3); }
        if (_airborne) PredictLanding(force: true); // the landing is in the level before he gets there

        for (int k = 0; k < Parts; k++)
        {
            if (_bones[k] == null) continue;
            Part(k, out _partPos0[k], out _partRot0[k]);
            _bonePos0[k] = _bones[k].position;
            _boneRot0[k] = _bones[k].rotation;
            _lockRest[k] = _bones[k].localRotation; // the aim pass's twist base: this bone on its parent
        }
        RestTwistAttach(); // ... from the rig's rest pose instead, when it can be read (NmBodyRest)
        BodyAxesAttach();
        ClampAttach();
        KneeGuardAttach();
        Vector3 origin = new Vector3(at.x, feetY, at.z);
        if (_startPose == StartPose.Stand)
        {
            if (!dying) CalibrateStanding(p, origin); // binding fallback for bots whose bind pose is unreadable
            for (int k = 0; k < Parts; k++) _standRot[k] = _boneRot0[k];
            _standPelvisY = _bonePos0[0].y;
        }
        _pelvisDelta = _bones[0].position - p.Transform.position;
        nm_agent_get_com(_agent, _f3, _g3);
        _comH0 = Mathf.Max(0.15f, _f3[2] - feetY);
        _jumpTaken = false;
        EndGameJump("takeover");
        if (_airborne)
        {
            EuphoriaPlugin.Log($"Delirium: {p.ProfileId} taken over in the air, {at.y - floorY:F2} m up, vertical {vy:F1} m/s " +
                               $"(game: {gameVy:F2} m/s, {(gameAir ? "not grounded" : "grounded")}, jump state {_jumpTaken}, freefall {freefall:F2}s)");
            if (_startPose != StartPose.Prone) AirBegin("taken over off the ground");
        }
        if (_startPose == StartPose.Prone && !dying) { _downSince = Time.time; FallWaitStart(); } // he starts on the ground

        if (!dying && !_speedLimitApplied)
        {
            try { _p.AddStateSpeedLimit(0f, Player.ESpeedLimit.Shot); _speedLimitApplied = true; } catch { }
        }
        GunToHand();
        BotActions.Cancel(_p);
        _nextCancel = Time.time + 0.5f;
        _until = Time.time + (EuphoriaPlugin.Instance?.Duration.Value ?? 8f);
        _attachAt = Time.time;
        if (!dying) SetPhase(_startPose == StartPose.Prone ? Phase.Fall : Phase.Stumble, "hit"); // a prone bot is already down
        _acc = 0f;
        _peakSpeed = 0f;
        ImpactDamageReset();
        _haveSafeRoot = false;
        enabled = true;
        nm_agent_get_com(_agent, _f3, _g3);
        EuphoriaPlugin.Log($"Delirium: attached {p.ProfileId} {_startPose.ToString().ToLowerInvariant()}{(dying ? " (dying)" : "")}, COM {_comH0:F2}m above floor, carry {vel.magnitude:F1} m/s"
                           + (_bind != null ? $", binding: {_bind.Source}" : ""));
        _pfAttach = PerfNow() - pf;
        return true;
    }

    // Alive = stumble/balance only. Tuned headless against 10-round dumps:
    // softer upright assist, early-step balance, no wounded-leg collapse.
    private void TuneAlive()
    {
        // crouch stumble: softer upright pull and legs, so he sinks while still catching himself
        float upright = _crouchStumble ? Tune.Upright * 0.45f : Tune.Upright;
        float legs = _crouchStumble ? Tune.LegStiff * 0.55f : Tune.LegStiff;
        Send("stayUpright", ("start", true), ("stiffness", upright));
        Send("configureBalance", ("legStiffness", legs), ("balanceAbortThreshold", Tune.BalanceAbort));
    }

    private bool _crouchStumble, _staggering;
    private const float RunStagger = 1.6f; // m/s his body is moving at for a hit to make him stagger on

    /// Back to animation in the stance the reaction ended in: crouched after a crouch stumble.
    private void ReleaseToStance()
    {
        if (_crouchStumble && _p != null && _p.HealthController?.IsAlive == true) BotActions.SetPose(_p, 0.5f, false);
        Release();
    }

    /// Non-lethal shot: shove + shot task, then re-assert "don't fall".
    public void Hit(Vector3 at, Vector3 dir, float newtonSeconds, bool legBuckle = false, Func<Vector3> aimAt = null, float aimChance = 0f)
    {
        if (!Active || _dying || _rigCheck) return;
        if (_inAir)
        {
            AirHit(at, dir, newtonSeconds * AlivePushScale); // no shot task, stagger or crouch stumble mid-air (NmBodyAir)
            _lastHitAt = Time.time;
            _stableFor = 0f;
            _until = Mathf.Max(_until, Time.time + 4f);
            return;
        }
        Shoot(at, dir, newtonSeconds * AlivePushScale, 100f);
        _lastHitAt = Time.time;
        if (_reaction == Phase.Stumble) _reactionStart = Time.time; // a new hit restarts the stumble clock
        _stableFor = 0f;
        if (_downSince == 0f) _saveRolled = false; // a new stumble: a new chance to catch himself
        nm_agent_get_com(_agent, _f3, _g3);
        float moving = new Vector2(_g3[0], _g3[1]).magnitude;
        // Crouch stumble: a leg hit sometimes buckles the knee and he takes it by sinking into a crouch
        // and recovering there. Only leg hits: on a shove or a torso hit a man who stumbles upright stays
        // upright (softened legs after a clean stumble read as his knees suddenly giving out, then a drop).
        // Never on the run: the crouch stopped him dead, legs held where the hit found them.
        if (_startPose == StartPose.Stand && !_crouchStumble && _downSince == 0f && legBuckle && moving < RunStagger)
        {
            float chance = (EuphoriaPlugin.Instance?.CrouchStumble.Value ?? 0.3f) * 2f;
            if (UnityEngine.Random.value < chance)
            {
                _crouchStumble = true;
                legBuckle = false;
                EuphoriaPlugin.Log("Delirium: crouch stumble - he sinks into a crouch and recovers there");
            }
        }
        if (!(EuphoriaPlugin.Instance?.AliveFalls.Value ?? true))
            Send("shot", ("timeBeforeCollapseWoundLeg", 99f), ("reachForWound", WoundGrab)); // stumble-only mode: no wounded-leg collapse
        else if (legBuckle)
            Send("shot", ("timeBeforeCollapseWoundLeg", 0.3f), ("reachForWound", WoundGrab)); // the knee gives out almost at once
        else
            Send("shot", ("timeBeforeCollapseWoundLeg", Tune.WoundCollapse), ("reachForWound", WoundGrab)); // a torso hit doesn't fold the legs at once
        // Shot on the run: the staggerFall behaviour. He keeps going the way he was, stumbling step after step
        // with his weight thrown forward, the steps tiring until he catches himself or goes down. The
        // balancer alone planted his feet at running speed: legs frozen where the hit found them, sliding.
        if (_startPose == StartPose.Stand && _downSince == 0f && !_staggering && moving >= RunStagger)
        {
            _staggering = true;
            Send("staggerFall", ("start", true), ("lowerBodyStiffness", 12f), ("lowerBodyStiffnessEnd", legBuckle ? 4f : 9f),
                 ("stepsTillStartEnd", legBuckle ? 4 : 7), ("staggerStepProb", 0.6f), ("leanInDirMaxF", 0.35f));
            EuphoriaPlugin.Log($"Delirium: shot on the run ({moving:F1} m/s) - stagger");
        }
        if (_downSince > 0f) { FallenPose(true); FallWaitStart(); } // shot on the ground: back into how he lies, the wait to get up starts over
        if (aimAt != null && !_aimBack && _gun != null && !GoingDown && !_catchFallSent && UnityEngine.Random.value < aimChance)
        {
            var ep = EuphoriaPlugin.Instance;
            _aimBack = true; _aimTarget = aimAt; _aimOn = false; _nextAim = 0f; _aimShots = 0;
            _aimStart = Time.time + 0.45f;                                  // the beat it takes him to react
            _aimEnd = Time.time + (ep?.AimBackDuration.Value ?? 2.5f);
            EuphoriaPlugin.Log("Delirium: aim-back armed (right arm will point the gun at the shooter)");
        }
        StartClutch(at, dir, 2.6f);
        if (_startPose != StartPose.Prone && _downSince == 0f) AdaptToSurroundings(dir); // a wall or a drop the way he's pushed (NmBodyAdapt)
        if (_startPose == StartPose.Prone) TuneDown(); // shot on the ground: don't try to stand him up
        else TuneAlive();
        _until = Mathf.Max(_until, Time.time + 4f);
        EuphoriaPlugin.Log($"Delirium: hit {newtonSeconds * AlivePushScale:F0} N*s");
    }

    private void Shoot(Vector3 at, Vector3 dir, float ns, float health)
    {
        dir.y *= 0.25f; // bullets shove sideways/back, they don't launch
        if (dir.sqrMagnitude < 1e-6f) return;
        dir.Normalize();
        ToNm(at, _f3);
        int part = nm_agent_nearest_part(_agent, _f3);
        ToNm(dir * ns, _g3);
        nm_agent_apply_push(_agent, _f3, _g3);
        ToNm(dir, _g3);
        nm_agent_task_shot(_agent, _world, "POWERFUL_WEAPON", part, _f3, _g3, health);
        Send("shot", ("reachForWound", WoundGrab)); // the weapon preset turns the wound reach on
        _lastHit = (at, dir, part);
    }

    /// Lethal shot: full death struggle — stagger, collapse, writhe on the
    /// ground, go limp — then hand over to SPT's own corpse ragdoll.
    public void Die(Vector3 at, Vector3 dir, float newtonSeconds, EBodyPart part = EBodyPart.Chest)
    {
        if (!Active || _rigCheck) return;
        // Which death: the common death fall (the hit carries him down, limp, until he settles) or
        // the rarer agony (stagger, collapse, writhe). Head shots always fall, instantly limp;
        // limb hits lean toward agony.
        bool head = part == EBodyPart.Head;
        bool limb = part == EBodyPart.LeftArm || part == EBodyPart.RightArm || part == EBodyPart.LeftLeg || part == EBodyPart.RightLeg;
        float agony = head ? 0f : Mathf.Clamp01((EuphoriaPlugin.Instance?.AgonyChance.Value ?? 0.25f) * (limb ? 1.6f : 1f));
        _deathStyle = UnityEngine.Random.value < agony ? DeathStyle.Agony : DeathStyle.Fall;
        BeginDying();
        if (_deathStyle == DeathStyle.Fall)
        {
            float now = Time.time;
            _collapseAt = now + (head ? 0f : UnityEngine.Random.Range(0.08f, 0.35f)); // a beat of stagger, none for a head shot
            _deathEnd = now + FallMaxTime;
            _relaxAt = _deathEnd;
        }
        Shoot(at, dir, newtonSeconds, 100f); // the hit itself; the collapse is scheduled
        if (head && _deathStyle == DeathStyle.Fall)
        {
            // lights out: no stagger, the body just goes with the bullet
            _phase = DeathPhase.Collapse;
            DropOnDeath();
            Send("bodyRelax", ("start", true), ("relaxation", 100f), ("damping", 0.6f));
        }
        else if (_deathStyle == DeathStyle.Agony)
        {
            StartClutch(at, dir, _collapseAt - Time.time + 0.6f); // hold the wound until he goes down
            Send("configureBalance", ("legStiffness", 9f));
        }
        else Send("configureBalance", ("legStiffness", 5f)); // the legs are already going
        EuphoriaPlugin.Log(_deathStyle == DeathStyle.Agony
            ? $"Delirium: death ({part}) - agony {_deathEnd - Time.time:F0}s, collapse in {_collapseAt - Time.time:F1}s"
            : $"Delirium: death ({part}) - {(head ? "head shot, instantly limp" : "death fall")}, settles into SPT's ragdoll within {FallMaxTime:F0}s");
    }

    private enum DeathStyle { Agony, Fall }
    private DeathStyle _deathStyle = DeathStyle.Agony;
    private const float FallMaxTime = 5f;    // s cap on a death fall before SPT's ragdoll takes over
    private const float FallSettled = 0.7f;  // s a fallen body must lie still before hand-over
    private float _stillSince;

    private void BeginDying()
    {
        if (_dying) return;
        _dying = true;
        SetPhase(Phase.None, "died"); // the death struggle takes over
        _dyingSince = Time.time;
        _stillSince = 0f;
        _writheAt = 0f; _writheFaded = false; _groundLift = 0f;
        _phase = DeathPhase.Stagger;
        float len = Mathf.Max(3f, EuphoriaPlugin.Instance?.DeathStruggle.Value ?? 10f);
        _deathEnd = Time.time + len;
        _collapseAt = Time.time + UnityEngine.Random.Range(1.2f, 2.2f);
        if (_inAir) { _inAir = false; _collapseAt = Time.time; } // no stagger in the air: he goes limp as he falls
        _relaxAt = _deathEnd - 2f;
        if (_speedLimitApplied && _p != null)
        {
            try { _p.RemoveStateSpeedLimit(Player.ESpeedLimit.Shot); } catch { }
            _speedLimitApplied = false;
        }
        // The gun never goes back to the animated weapon root (it hung in the air at the aim point).
        // In the agony he lets go of it to hold the wound; a death fall keeps it until he collapses.
        GunToHand();
        if (_deathStyle == DeathStyle.Agony) DropOnDeath();
    }

    private enum DeathPhase { Stagger, Collapse, Writhe, Relax }
    private DeathPhase _phase;
    private bool _dying;
    private float _deathEnd, _collapseAt, _relaxAt, _floorY, _dyingSince, _writheAt;
    private bool _airborne;           // taken over with his feet off the ground (jumping, falling)
    private const float AirGap = 0.2f; // m between feet and ground that counts as in the air
    private bool _writheFaded;
    private (Vector3 at, Vector3 dir, int part) _lastHit;
    private const float AlivePushScale = 0.67f; // ~30 N*s base: staggers without bowling him over
    private readonly List<Rigidbody> _corpse = new List<Rigidbody>();
    private readonly Dictionary<Rigidbody, bool> _corpseWasKinematic = new Dictionary<Rigidbody, bool>(); // as EFT had each body before we held it

    private void UpdateDying()
    {
        float now = Time.time;
        // the gun he let go goes into the weapon body as soon as EFT has made it (a frame after he died)
        if (_gunDropped && _gunBody == null && _gun != null && now >= _nextGunTry)
        {
            _nextGunTry = now + 0.05f;
            GunToCorpse(out _);
        }
        switch (_phase)
        {
            case DeathPhase.Stagger:
                if (now >= _collapseAt || nm_agent_has_fallen(_agent) == 1)
                {
                    _phase = DeathPhase.Collapse;
                    DropOnDeath();
                    ToNm(_lastHit.at, _f3); ToNm(_lastHit.dir.sqrMagnitude > 0 ? _lastHit.dir : -_p.Transform.forward, _g3);
                    nm_agent_task_shot(_agent, _world, "POWERFUL_WEAPON", Mathf.Max(0, _lastHit.part), _f3, _g3, 20f); // fatal -> shotRelax
                    Send("shot", ("reachForWound", WoundGrab));
                }
                break;
            case DeathPhase.Collapse:
                nm_agent_get_com(_agent, _f3, _g3);
                bool down = nm_agent_has_fallen(_agent) == 1 && _f3[2] - GroundUnder(ToUnity(_f3[0], _f3[1], _f3[2])) < 0.45f;
                if (_deathStyle == DeathStyle.Fall && (down || now > _collapseAt + 2.5f))
                {
                    // death fall: no writhe, he's gone - limp until the body comes to rest
                    _phase = DeathPhase.Relax;
                    Send("stopAllBehaviours");
                    Send("bodyRelax", ("start", true), ("relaxation", 100f), ("damping", 0.8f));
                }
                else if (down)
                {
                    _phase = DeathPhase.Writhe;
                    _writheAt = now;
                    // subdued: about a third of the default writhe's energy, slow cycle
                    Send("bodyWrithe", ("start", true), ("armAmplitude", 0.5f), ("legAmplitude", 0.5f), ("backAmplitude", 0.5f),
                         ("kneeAmplitude", 0.5f), ("elbowAmplitude", 0.5f), ("armStiffness", 8f), ("legStiffness", 8f),
                         ("backStiffness", 8f), ("legPeriod", 2.2f), ("backPeriod", 2.9f), ("armPeriod", 2.0f));
                }
                else if (now > _collapseAt + 3f) _phase = DeathPhase.Writhe; // stuck upright on something: skip ahead
                break;
            case DeathPhase.Writhe:
                if (!_writheFaded && _writheAt > 0f && now > _writheAt + 3f)
                {
                    _writheFaded = true; // strength fading
                    Send("bodyWrithe", ("armAmplitude", 0.2f), ("legAmplitude", 0.2f), ("backAmplitude", 0.2f),
                         ("kneeAmplitude", 0.2f), ("elbowAmplitude", 0.2f), ("armStiffness", 5.5f), ("legStiffness", 5.5f), ("backStiffness", 5.5f));
                }
                if (now >= _relaxAt)
                {
                    _phase = DeathPhase.Relax;
                    Send("stopAllBehaviours");
                    Send("bodyRelax", ("start", true), ("relaxation", 100f), ("damping", 1f));
                }
                break;
            case DeathPhase.Relax:
                // a death fall hands over as soon as the body has come to rest on the ground
                if (_deathStyle == DeathStyle.Fall)
                {
                    bool still = nm_agent_is_settled(_agent) == 1 || nm_agent_get_kinetic_energy(_agent) < 8f;
                    _stillSince = still ? (_stillSince == 0f ? now : _stillSince) : 0f;
                    if (_stillSince > 0f && now - _stillSince > FallSettled)
                    {
                        EuphoriaPlugin.Log($"Delirium: death fall settled after {now - _dyingSince:F1}s - SPT ragdoll takes over");
                        Release(true);
                        return;
                    }
                }
                break;
        }
        if (now >= _deathEnd) Release(true);
    }

    // EFT built its corpse ragdoll at death; hold its bodies kinematic so they
    // follow our bones during the struggle, then let them go.
    private void HoldCorpse()
    {
        if (Time.time < _dyingSince + 2f || _corpse.Count == 0)
        {
            foreach (Rigidbody rb in _p.GetComponentsInChildren<Rigidbody>(true))
                if (rb != null && rb != _gunBody && !_corpse.Contains(rb)) { _corpse.Add(rb); _corpseWasKinematic[rb] = rb.isKinematic; }
        }
        foreach (Rigidbody rb in _corpse)
            if (rb != null && !rb.isKinematic) rb.isKinematic = true;
        GateCorpseSleep();
    }

    // EFT's corpse puts itself to sleep a second after death once its bodies stop moving, and a body we
    // hold kinematic never moves: it was frozen for good wherever the doll had him (in the air, when the
    // doll lay on something), and the bodies we then let go had left EFT's physics update - no gravity,
    // so a bot landing on him floated him off. Its still-check waits until we let go, then counts afresh.
    // Its sleep loop is held off too: each frame it marks every body that isn't moving "must sleep" for
    // good and calls Sleep() on it, and a held body never moves. Every body was marked while we held him,
    // so the ones we let go were put back to sleep on the next frame, hung there with no gravity, and a
    // second later the still-check froze him in mid-air for good. The loop gets no bodies while he's held
    // and for a second after (a body let go at rest would sleep before gravity moved it), then its own
    // list back with every mark cleared.
    // A body EFT's loop puts to sleep has its speed zeroed, and each frame after it's put back to sleep
    // before gravity can move it: one that was still for a moment off the ground (resting on a body or
    // prop that then moved) hung there, and the loop's 15 s timeout froze him in mid-air for good. While
    // he's asleep with his lowest part off what's under him, or as EFT is about to stop him like that, the
    // loop is held off again for a second with every mark cleared, so he falls before it counts him again.
    private sealed class CorpseGate { internal bool Held = true; internal float ReleasedAt, Made = Time.time, NextAirCheck; internal int AirWakes; }
    private CorpseGate _corpseGate;
    private EFT.Interactive.CorpseRagdoll _corpseRag;
    private const float CorpseAirGap = 0.3f; // m: a corpse whose lowest part is this far above what's under it isn't lying on anything
    private const int MaxAirWakes = 6;
    private static readonly RaycastHit[] _airHits = new RaycastHit[16];

    private void GateCorpseSleep()
    {
        if (_corpseGate != null) return;
        var rag = _p.GetComponentInChildren<EFT.Interactive.Corpse>(true)?.Ragdoll;
        if (rag == null || rag._checkCorpseIsStill == null) return;
        var gate = new CorpseGate();
        System.Func<bool, float, bool> eft = rag._checkCorpseIsStill;
        var bodies = rag._rigidbodySleepHierarchy;
        var none = bodies?.GetRange(0, 0);
        if (bodies != null) rag._rigidbodySleepHierarchy = none;
        bool SleepLoopBack()
        {
            if (bodies == null || rag._rigidbodySleepHierarchy != none) return false;
            foreach (var h in bodies) h.Reset();
            rag._rigidbodySleepHierarchy = bodies;
            return true; // the loop counts its own bodies from the next frame on
        }
        bool SleepLoopOff()
        {
            if (bodies == null || rag._rigidbodySleepHierarchy == none) return false;
            rag._rigidbodySleepHierarchy = none;
            foreach (var h in bodies) h.WakeUp(); // mark cleared, gravity has it again
            return true;
        }
        Transform root = _p.gameObject.transform;
        string id = _p.ProfileId;
        // how far his lowest part is above whatever is under it (m); -1 when no body is asleep and none is asked for
        float Gap(bool always)
        {
            if (bodies == null || root == null) return -1f;
            bool asleep = always;
            float low = float.MaxValue;
            foreach (var h in bodies)
            {
                Collider c = h.RigidbodySpawner != null ? h.RigidbodySpawner.GetComponent<Collider>() : null;
                if (c == null) continue;
                Rigidbody rb = h.RigidbodySpawner.Rigidbody;
                if (rb != null && rb.IsSleeping()) asleep = true;
                Bounds b = c.bounds;
                float under = b.center.y - 10f;
                int n = Physics.RaycastNonAlloc(b.center, Vector3.down, _airHits, 10f, ~0, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                    if (!_airHits[i].collider.transform.IsChildOf(root) && _airHits[i].point.y > under) under = _airHits[i].point.y;
                low = Mathf.Min(low, Mathf.Max(0f, b.min.y - under));
            }
            return asleep && low < float.MaxValue ? low : -1f;
        }
        rag._checkCorpseIsStill = (sleeping, timePass) =>
        {
            if (gate.Held)
                return Time.time > gate.Made + 30f && !SleepLoopBack() && eft(sleeping, timePass); // never let go: EFT's own rule after a while
            if (Time.time < gate.ReleasedAt + 1f || SleepLoopBack()) return false;
            float since = Time.time - gate.ReleasedAt - 1f;
            bool stop = eft(sleeping, since);
            float gap = -1f;
            if ((stop || Time.time >= gate.NextAirCheck) && gate.AirWakes < MaxAirWakes)
            {
                gate.NextAirCheck = Time.time + 0.5f;
                gap = Gap(stop);
                if (gap > CorpseAirGap && SleepLoopOff())
                {
                    gate.AirWakes++;
                    gate.ReleasedAt = Time.time; // a second of gravity before the loop counts him again
                    EuphoriaPlugin.Log($"Delirium: {id} corpse asleep {gap * 100f:F0} cm off what's under it ({(stop ? "EFT about to stop him" : "sleep loop")}) - woken to fall");
                    return false;
                }
            }
            if (stop)
            {
                if (gap < 0f) gap = Gap(true);
                EuphoriaPlugin.Log($"Delirium: {id} corpse stopped by EFT {since:F1}s after the hand-back ({(sleeping ? "at rest" : "timed out moving")}), " +
                                   $"lowest part {gap * 100f:F0} cm above what's under it{(gate.AirWakes > 0 ? $", woken {gate.AirWakes}x in the air" : "")}, " +
                                   $"physics {(global::PhysicsExtensions.Simulation.CanRunSimulate ? "running" : "PAUSED")}");
            }
            return stop;
        };
        _corpseGate = gate;
        _corpseRag = rag;
        if (rag._isPhysicsDone) EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} corpse was already put to sleep by EFT");
    }

    private void ReleaseCorpse()
    {
        CorpseWeights();
        if (_corpseGate != null) { _corpseGate.Held = false; _corpseGate.ReleasedAt = Time.time; }
        // EFT already stopped it (its bodies out of its physics update): let go, it'd hang there weightless
        bool asleep = _corpseRag != null && _corpseRag._isPhysicsDone;
        _corpseGate = null; _corpseRag = null;
        if (asleep)
        {
            EuphoriaPlugin.Log("Delirium: corpse already asleep in EFT - left where it lies");
            RestoreNonRagdoll();
            _corpse.Clear();
            _corpseWasKinematic.Clear();
            return;
        }
        int kept = RestoreNonRagdoll();
        foreach (Rigidbody rb in _corpse)
        {
            if (rb == null || !rb.name.StartsWith("Base Human")) continue;
            rb.isKinematic = false;
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.WakeUp();
        }
        if (_corpse.Count > 0) EuphoriaPlugin.Log($"Delirium: corpse ragdoll handed back ({_corpse.Count - kept} bodies, {kept} others put back as EFT had them)");
        _corpse.Clear();
        _corpseWasKinematic.Clear();
    }

    // Bodies we held that aren't his ragdoll (his own root body: 1 kg, no gravity, kinematic in EFT) go
    // back to how EFT had them. Let go with the ragdoll, his root was left loose and weightless; once EFT
    // took the ragdoll bodies off (as soon as he was out of view) all his limb colliders landed on it, and
    // he floated up off the floor to the ceiling - or flew off into the sky when a shot hit him.
    private int RestoreNonRagdoll()
    {
        int n = 0;
        foreach (Rigidbody rb in _corpse)
        {
            if (rb == null || rb.name.StartsWith("Base Human")) continue;
            rb.isKinematic = (_p != null && rb.gameObject == _p.gameObject) || !_corpseWasKinematic.TryGetValue(rb, out bool was) || was;
            n++;
        }
        return n;
    }

    // Each corpse body's share of the weight (fractions of body mass, standard adult segment tables), a
    // ragdoll body carrying the segments below it that have none of their own (a calf its foot ...).
    private static readonly (string bone, float share)[] SegmentShare =
    {
        ("Head", 0.069f), ("Neck", 0.012f), ("Pelvis", 0.112f), ("Spine", 0.323f), ("Thigh", 0.142f), ("Calf", 0.043f),
        ("Foot", 0.014f), ("Upperarm", 0.027f), ("Forearm", 0.016f), ("Palm", 0.006f),
    };
    private static bool _corpseWeightsLogged;

    /// SPT's corpse ragdoll with realistic weights, and no drag holding it up in the air.
    private void CorpseWeights()
    {
        var bodies = new List<Rigidbody>();
        foreach (Rigidbody rb in _corpse) if (rb != null && rb.name.StartsWith("Base Human") && !bodies.Contains(rb)) bodies.Add(rb); // his body, not the gun or gear
        if (bodies.Count == 0) return;
        float total = 0f;
        int spines = 0;
        foreach (Rigidbody rb in bodies) { total += rb.mass; if (rb.name.Contains("Spine")) spines++; }
        bool hasFoot = bodies.Exists(b => b.name.Contains("Foot")), hasPalm = bodies.Exists(b => b.name.Contains("Palm"));
        bool hasNeck = bodies.Exists(b => b.name.Contains("Neck"));
        var share = new float[bodies.Count];
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < bodies.Count; i++)
        {
            string n = bodies[i].name;
            foreach (var (bone, s) in SegmentShare)
            {
                if (!n.Contains(bone)) continue;
                share[i] = bone == "Spine" ? s / Mathf.Max(1, spines)
                    : bone == "Calf" && !hasFoot ? s + 0.014f
                    : bone == "Forearm" && !hasPalm ? s + 0.006f
                    : bone == "Head" && !hasNeck ? s + 0.012f : s;
                break;
            }
            if (!_corpseWeightsLogged)
                sb.Append($" {n.Replace("Base Human", "")} {bodies[i].mass:F1}kg drag {bodies[i].drag:F2}/{bodies[i].angularDrag:F2}{(bodies[i].useGravity ? "" : " NO GRAVITY")}");
        }
        float sum = 0f;
        foreach (float s in share) sum += s;
        if (sum <= 0f) return;
        float mass = Mathf.Clamp(total, 60f, 100f); // his weight: EFT's total, kept to a human range
        for (int i = 0; i < bodies.Count; i++)
        {
            Rigidbody rb = bodies[i];
            if (share[i] > 0f) rb.mass = mass * share[i] / sum;
            rb.useGravity = true;
            rb.drag = Mathf.Min(rb.drag, 0.05f);
            rb.angularDrag = Mathf.Min(rb.angularDrag, 0.5f);
        }
        if (!_corpseWeightsLogged)
        {
            _corpseWeightsLogged = true;
            EuphoriaPlugin.Log($"Delirium: SPT corpse ragdoll as EFT set it ({total:F1} kg):{sb}; now {mass:F0} kg by body segment, drag at most 0.05/0.5, gravity on");
        }
    }

    // ---------------------------------------------------------------- weapon

    private Vector3 _gunHandPos;
    private Quaternion _gunHandRot;
    // Grip point in the gun's own frame, and the smoothed orientation it is held at: the gun stays
    // in the palm, but its rotation trails the flailing wrist instead of copying every twitch.
    private Vector3 _gripLocal;
    private Quaternion _gunHeldRot;
    private bool _gunHeldInit;
    // The grip lock: from the takeover to the end of the reaction the gun is held rigidly in the palm at
    // the pose it had then (after the grip-marker fix), with no easing, so it can't drift off the hand.
    private bool _gunLocked;
    private Vector3 _lockPalmPos;
    private Quaternion _lockPalmRot;
    private const float GunTurnRate = 14f;      // 1/s, how fast the held gun catches up with the wrist
    private const float GunMaxTurn = 420f;      // deg/s cap, so a single bad physics frame can't spin it
    private readonly System.Collections.Generic.List<(Collider c, bool on)> _gunColliders = new System.Collections.Generic.List<(Collider, bool)>();

    // The weapon must not collide with anything while physics owns the body (it clipped and snagged).
    private void GunCollidersOff()
    {
        _gunColliders.Clear();
        foreach (Collider c in _gun.GetComponentsInChildren<Collider>(true))
        {
            _gunColliders.Add((c, c.enabled));
            c.enabled = false;
        }
    }

    private void GunCollidersRestore()
    {
        foreach (var (c, on) in _gunColliders)
            if (c != null) c.enabled = on;
        _gunColliders.Clear();
    }

    private void GunToHand()
    {
        if (_gun != null) return;
        GunBackEnd();
        if (_bones[20] == null) return;
        Transform root = B(_p?.PlayerBones?.WeaponRoot);
        if (root == null || root.childCount == 0) return; // weapon not in his hands (healing, swapping): retried from Update
        _gun = root.GetChild(0);
        _fireport = null;
        foreach (Transform t in _gun.GetComponentsInChildren<Transform>(true))
            if (t.name.IndexOf("fireport", StringComparison.OrdinalIgnoreCase) >= 0) { _fireport = t; break; }
        EuphoriaPlugin.Log($"Delirium: gun {_gun.name}, fireport {(_fireport != null ? _fireport.name : "not found (using gun forward)")}");
        _gunParent = _gun.parent;
        _gunLocal = (_gun.localPosition, _gun.localRotation, _gun.localScale);
        _gun.SetParent(_bones[20], true);
        _gunHandPos = _bones[20].InverseTransformPoint(_gun.position);
        _gunHandRot = Quaternion.Inverse(_bones[20].rotation) * _gun.rotation;
        _gripLocal = Quaternion.Inverse(_gun.rotation) * (_bones[20].position - _gun.position);
        _gunHeldRot = _gun.rotation;
        GlueFind();
        bool gripKnown = UseGripMarker();
        RecordSupportGrip();
        GlueAttach(gripKnown);
        if (GetComponent<GunLatePass>() == null) gameObject.AddComponent<GunLatePass>();
        _gunHeldInit = true;
        _gunDropped = false;
        _armOverrideLogged = false;
        _gunLocked = true;
        _lockPalmPos = _bones[20].InverseTransformPoint(_gun.position);
        _lockPalmRot = Quaternion.Inverse(_bones[20].rotation) * _gun.rotation;
        GunCollidersOff();
    }

    /// The locked gun: its pose in the palm, every frame.
    private void ApplyGunLock()
    {
        if (_glue) { GlueApply(); if (_glue) return; } // by its grip point (NmBodyGunGlue)
        Transform palm = _bones[20];
        _gunHeldRot = palm.rotation * _lockPalmRot;
        _gun.SetPositionAndRotation(palm.TransformPoint(_lockPalmPos), _gunHeldRot);
    }

    // This gun's learned right-hand grip, used when the live hand isn't on it at the takeover (mid-reload,
    // swapping, an animation with the hand elsewhere): otherwise the gun hung off the palm at that offset.
    // true when this gun's grip is known (the hand is on it now, either way)
    private bool UseGripMarker()
    {
        Transform palm = _bones[20];
        string key = GunMarkers.KeyOf(_p, _gun);
        if (!GunMarkers.TryGet(key, out GunMarkers.Marker m)) { GunMarkers.Observe(key, _gun, palm); return false; }
        Quaternion inv = Quaternion.Inverse(_gun.rotation);
        float off = ((inv * (palm.position - _gun.position)) - m.Pos).magnitude, turn = Quaternion.Angle(inv * palm.rotation, m.Rot);
        if (off < 0.05f && turn < 15f) { GunMarkers.Observe(key, _gun, palm); return true; } // the live grip is the real one
        _gripLocal = m.Pos;
        _gunHandRot = Quaternion.Inverse(m.Rot);
        _gunHandPos = -(_gunHandRot * m.Pos);
        _gunHeldRot = palm.rotation * _gunHandRot;
        _gun.SetPositionAndRotation(palm.position - _gunHeldRot * _gripLocal, _gunHeldRot);
        EuphoriaPlugin.Log($"Delirium: hand was off the grip of {key} ({off * 100f:F0} cm, {turn:F0} deg) - gun set on its grip marker");
        return true;
    }

    /// After every other script this frame: the gun where HoldGun put it (no easing step; the game's
    /// weapon animation moves the container we hold, and on some guns it ran after us).
    internal void HoldGunLate(bool beforeRender = false)
    {
        if (_bones == null) return;
        GunBackLate();
        RelaxLeftFingers(); // the left hand off the gun lets go of its grip (NmBodyFingers)
        // The game's own hand IK / weapon animation can run after our LateUpdate and pull the arms back
        // into the weapon stance, leaving the gun to follow a hand nobody sees: the arms we drove go back.
        // local rotations only, and only this frame's: positions (or a stale frame) pulled the arms to where
        // the body used to be and stretched them
        if (_armWritten && _armWriteFrame == Time.frameCount)
        {
            int moved = 0;
            for (int k = 13; k < Parts; k++)
            {
                if (_bones[k] == null) continue;
                if (Quaternion.Angle(_bones[k].localRotation, _armWrite[k]) > 1f) moved++;
                _bones[k].localRotation = _armWrite[k]; // the arm pose we gave him, on wherever his chest is now
                _bones[k].localPosition = _armWriteLocal[k]; // and its own length: an IK that moved the bone can't stretch the arm
            }
            if (moved > 0 && !_armOverrideLogged)
            {
                _armOverrideLogged = true;
                EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} the game re-posed {moved} arm bone(s) after the physics{(beforeRender ? " (after every LateUpdate, before render)" : "")} - put back");
            }
        }
        if (_twins.Count > 0) SyncTwins();
        if (_gun == null || _gunDropped || _bones[20] == null) return;
        if (_gunLocked) ApplyGunLock();
        else _gun.SetPositionAndRotation(_bones[20].position - _gunHeldRot * _gripLocal, _gunHeldRot);
        TrackGunMotion();
    }

    // How the held gun is moving, so it leaves his hand with that motion when he lets go of it.
    private Vector3 _gunLastPos, _gunVel, _gunSpin;
    private Quaternion _gunLastRot;
    private int _gunLastFrame = -10;

    private void TrackGunMotion()
    {
        int frame = Time.frameCount;
        float dt = Time.deltaTime;
        if (_gunLastFrame == frame - 1 && dt > 1e-4f)
        {
            _gunVel = Vector3.Lerp(_gunVel, (_gun.position - _gunLastPos) / dt, 0.5f);
            (_gun.rotation * Quaternion.Inverse(_gunLastRot)).ToAngleAxis(out float deg, out Vector3 axis);
            if (deg > 180f) deg -= 360f;
            Vector3 spin = float.IsInfinity(axis.x) || float.IsNaN(axis.x) ? Vector3.zero : axis * (deg * Mathf.Deg2Rad / dt);
            _gunSpin = Vector3.Lerp(_gunSpin, spin, 0.5f);
        }
        else if (_gunLastFrame != frame) { _gunVel = Vector3.zero; _gunSpin = Vector3.zero; } // a second pass this frame keeps it
        _gunLastPos = _gun.position; _gunLastRot = _gun.rotation; _gunLastFrame = frame;
    }

    // Re-assert the grip after everything else has moved this frame.
    private void HoldGun()
    {
        if (_gun == null || _gunDropped || _bones[20] == null) return;
        if (_gunLocked) { ApplyGunLock(); return; }
        Quaternion want = _bones[20].rotation * _gunHandRot;
        float dt = Mathf.Max(Time.deltaTime, 1e-4f);
        if (!_gunHeldInit) { _gunHeldRot = want; _gunHeldInit = true; }
        Quaternion eased = Quaternion.Slerp(_gunHeldRot, want, 1f - Mathf.Exp(-GunTurnRate * dt));
        _gunHeldRot = Quaternion.RotateTowards(_gunHeldRot, eased, GunMaxTurn * dt);
        // pivot about the grip: the palm stays on the gun while its orientation trails the wrist
        _gun.SetPositionAndRotation(_bones[20].position - _gunHeldRot * _gripLocal, _gunHeldRot);
    }

    // Back to the weapon root it came from, always: dead or alive, held or dropped. EFT keeps the
    // weapon's model where it put it; a dead bot's gun left on the floor (or under his palm) meant
    // whoever looted and equipped it got empty hands, the model never in their weapon root.
    private Coroutine _gunRoutine;

    private static string TreePath(Transform t)
    {
        string path = t.name;
        for (int i = 0; i < 6 && (t = t.parent) != null; i++) path = t.name + "/" + path;
        return path;
    }

    private void RestoreGun()
    {
        if (_gunRoutine != null) { StopCoroutine(_gunRoutine); _gunRoutine = null; } // a drop still moving it
        GunCollidersRestore(); // first: a dead man's gun then gets EFT's own (GunToCorpse)
        if (_gun != null && _gunParent != null && _gunBody == null) // let go into its body while he died: it's home, falling on its own
        {
            bool dead = _p != null && _p.HealthController?.IsAlive != true;
            string why = null;
            if (!dead || !GunToCorpse(out why))
            {
                GunHome();
                if (dead) EuphoriaPlugin.Log($"Delirium: dead man's gun - {why}; snapped home");
            }
        }
        GlueEnd();
        _gun = null;
        _gunBody = null;
        _gunDropped = false;
        _gunHeldInit = false;
        _gunLocked = false;
    }

    private void GunHome()
    {
        _gun.SetParent(_gunParent, false);
        _gun.localPosition = _gunLocal.pos;
        _gun.localRotation = _gunLocal.rot;
        _gun.localScale = _gunLocal.scale;
    }

    // A dead man's gun: EFT gave the weapon prefab its own rigidbody at death, and the prefab sits above
    // the weapon root. Snapped home alone, the gun jumped from the doll's hand to wherever that body had
    // been dragged and seemed to vanish; the body is moved so the gun lands home exactly where it is now,
    // then left to its physics, nothing of his holding it: EFT ties that body to the corpse (a spring
    // strap to his chest, or fixed to the hand), and the tie is cut, or the gun swung and flopped about on
    // it as the body moved; it hangs in his rig's bones (the weapon root, moved by the animation and the
    // doll), and is taken out to his root, which stays put while he dies, so it still goes with the corpse;
    // it doesn't collide with his own body, which follows the doll while he dies and batted it away. It
    // leaves the hand moving as the hand moved it, and EFT makes that body a frame after he dies, so a gun
    // let go before then waits for it (UpdateDying).
    private Rigidbody _gunBody; // the weapon body let go while he dies: not held with the corpse
    private float _nextGunTry;
    private bool GunToCorpse(out string why)
    {
        bool dead = _p != null && _p.HealthController?.IsAlive != true;
        var rag = dead ? _p.GetComponentInChildren<EFT.Interactive.Corpse>(true)?.Ragdoll : null;
        Rigidbody drop = rag?._weaponRigidbody;
        why = !dead ? "alive" : rag == null ? "no corpse ragdoll" : drop == null ? "ragdoll has no weapon body"
            : !_gunParent.IsChildOf(drop.transform) ? $"weapon body {TreePath(drop.transform)} is not above the gun's home {TreePath(_gunParent)}" : null;
        if (why != null) return false;
        if (_gunRoutine != null) { StopCoroutine(_gunRoutine); _gunRoutine = null; }
        Vector3 seenPos = _gun.position;
        Quaternion seenRot = _gun.rotation;
        GunHome();
        Transform body = drop.transform;
        Quaternion relRot = Quaternion.Inverse(body.rotation) * _gun.rotation;
        Vector3 relPos = Quaternion.Inverse(body.rotation) * (_gun.position - body.position);
        Quaternion rot = seenRot * Quaternion.Inverse(relRot);
        Vector3 pos = seenPos - rot * relPos;
        bool tied = rag._weaponJoint != null;
        if (tied) { UnityEngine.Object.Destroy(rag._weaponJoint); rag._weaponJoint = null; }
        string was = body.parent != null ? body.parent.name : "none";
        body.SetParent(_p.gameObject.transform, true);
        body.SetPositionAndRotation(pos, rot);
        int solid = GunCollidersDead(body);
        Collider[] own = _p.GetComponentsInChildren<Collider>(false);
        foreach (Collider g in body.GetComponentsInChildren<Collider>(false))
            foreach (Collider c in own)
                if (!c.transform.IsChildOf(body)) Physics.IgnoreCollision(g, c);
        _corpse.Remove(drop);
        _gunBody = drop;
        bool moving = _gunLastFrame >= Time.frameCount - 3; // its motion in the hand is fresh
        drop.position = pos; drop.rotation = rot;
        drop.isKinematic = false;
        drop.velocity = moving ? Vector3.ClampMagnitude(_gunVel, GunLetGoMaxSpeed) : Vector3.zero;
        drop.angularVelocity = moving ? Vector3.ClampMagnitude(_gunSpin, GunLetGoMaxSpin) : Vector3.zero;
        drop.WakeUp();
        EuphoriaPlugin.Log($"Delirium: dead man's gun let go where he held it ({body.name}, out of {was}{(tied ? ", cut from the corpse" : "")}, " +
                           $"{solid} solid collider(s), {drop.velocity.magnitude:F1} m/s) - left to its physics");
        return true;
    }

    // The colliders EFT gave the dead man's gun for the floor (Player.DropItemDead: its pool object's solid
    // ones, on, on the Deadbody layer). The ones we switched off while he held it had been put back as they
    // were in his hands, off, after EFT turned them on: the gun fell through the ground.
    private int GunCollidersDead(Transform body)
    {
        _gunColliders.Clear(); // EFT's now: never put back to the hands' state
        int deadLayer = LayerMask.NameToLayer("Deadbody"), n = 0;
        foreach (EFT.AssetsManager.AssetPoolObject apo in body.GetComponentsInChildren<EFT.AssetsManager.AssetPoolObject>(true))
            foreach (Collider c in apo.Colliders)
                if (c != null && !c.isTrigger && c.gameObject.layer == deadLayer) { c.enabled = true; n++; }
        return n;
    }

    private const float GunLetGoMaxSpeed = 6f; // m/s
    private const float GunLetGoMaxSpin = 12f; // rad/s

    // The hand-back: the gun goes home to the weapon root, but over the same blend the arms take, from
    // his hand to where the game's weapon animation has it. Snapped home at once, it sat at the animated
    // spot while his hands were still on their way there from the get-up (or the stumble).
    private Transform _gb;
    private float _gbStart, _gbDur;
    private Quaternion _gbHandRot, _gbLocalRot;
    private Vector3 _gbGrip, _gbLocalPos;
    internal bool GunBlending => _gb != null;

    private void RestoreGunBlended(float dur)
    {
        Transform gun = _gun;
        _gunLocked = false; // the blend takes the gun from the hand now
        if (gun == null || _gunDropped || _gunParent == null || _bones[20] == null || dur <= 0.01f) { RestoreGun(); return; }
        _gbHandRot = Quaternion.Inverse(_bones[20].rotation) * _gunHeldRot; // the grip as held now
        _gbGrip = _gripLocal;
        Vector3 pos = gun.position; Quaternion rot = gun.rotation;
        RestoreGun();
        _gb = gun; _gbStart = Time.time; _gbDur = dur;
        _gbLocalPos = gun.localPosition; _gbLocalRot = gun.localRotation;
        gun.SetPositionAndRotation(pos, rot); // no pop this frame
    }

    /// Late in the frame, while the gun goes home: from the hand towards the animated spot.
    private void GunBackLate()
    {
        if (_gb == null) return;
        float t = (Time.time - _gbStart) / _gbDur;
        _gb.localPosition = _gbLocalPos; _gb.localRotation = _gbLocalRot; // where the game has it this frame
        if (t >= 1f || _bones[20] == null) { GunBackEnd(); return; }
        float w = Mathf.SmoothStep(0f, 1f, t);
        Quaternion held = _bones[20].rotation * _gbHandRot;
        Vector3 heldPos = _bones[20].position - held * _gbGrip;
        _gb.SetPositionAndRotation(Vector3.Lerp(heldPos, _gb.position, w), Quaternion.Slerp(held, _gb.rotation, w));
    }

    private void GunBackEnd()
    {
        _gunLocked = false;
        if (_gb != null) { _gb.localPosition = _gbLocalPos; _gb.localRotation = _gbLocalRot; }
        _gb = null;
    }

    public void DropWeapon()
    {
        if (_dying || !(EuphoriaPlugin.Instance?.DropGun.Value ?? false)) return;
        if (_gun == null) GunToHand();
        if (_gun == null || _gunDropped) return;
        _gunDropped = true;
        GunCollidersRestore(); // a dropped gun is a world object again
        _gun.SetParent(null, true);
        _gunRoutine = StartCoroutine(DropGunRoutine(_gun));
    }

    /// Dying: the gun leaves his hand (whatever "Heavy hits drop the weapon" says) as the agony starts,
    /// or as a death fall collapses him, and falls under EFT's own physics for it; not later, when SPT's
    /// ragdoll takes the body. Still his item: only its model leaves the hand.
    private void DropOnDeath()
    {
        if (_gun == null) GunToHand();
        if (_gun == null || _gunDropped) return;
        _gunDropped = true;
        GunCollidersRestore(); // a dropped gun is a world object again
        if (GunToCorpse(out string why)) return;
        EuphoriaPlugin.LogV($"Delirium: dying gun falls on its own until it can go into its body ({why})");
        _nextGunTry = 0f;
        _gun.SetParent(null, true);
        _gunRoutine = StartCoroutine(SettleGunRoutine(_gun));
        EuphoriaPlugin.LogV("Delirium: the gun slips from his hand as he goes down");
    }

    private IEnumerator SettleGunRoutine(Transform gun)
    {
        Vector3 v = Vector3.zero;
        for (float t = 0f; t < 3f && gun != null; t += Time.deltaTime)
        {
            v.y -= 9.8f * Time.deltaTime;
            Vector3 pos = gun.position + v * Time.deltaTime;
            if (RayWorld(gun.position + Vector3.up * 0.4f, Vector3.down, 4f, out RaycastHit g) && pos.y <= g.point.y + 0.04f)
            {
                gun.position = new Vector3(pos.x, g.point.y + 0.04f, pos.z);
                yield break;
            }
            gun.position = pos;
            yield return null;
        }
    }

    private IEnumerator DropGunRoutine(Transform gun)
    {
        Vector3 v = _p.Transform.forward * 1.2f + Vector3.up * 0.8f;
        Quaternion spin = Quaternion.Euler(UnityEngine.Random.Range(140f, 320f), 0, UnityEngine.Random.Range(40f, 90f)) * gun.rotation;
        float ground = _p.Transform.position.y + 0.05f;
        for (float t = 0f; t < 1.2f && gun != null; t += Time.deltaTime)
        {
            v.y -= 9.8f * Time.deltaTime;
            Vector3 pos = gun.position + v * Time.deltaTime;
            if (pos.y < ground) { pos.y = ground; v = Vector3.zero; }
            gun.position = pos;
            gun.rotation = Quaternion.Slerp(gun.rotation, spin, Time.deltaTime * 6f);
            yield return null;
        }
    }

    private Transform _gun, _gunParent, _fireport;
    private Vector3 BarrelDir => _fireport != null ? _fireport.forward : _gun.forward;
    private (Vector3 pos, Quaternion rot, Vector3 scale) _gunLocal;
    private float _nextSync;
    private bool _speedLimitApplied, _gunDropped, _blending, _holdOver;
    private float _attachAt, _peakSpeed, _blendStart, _theta, _nextCancel, _nextGun, _armsSince;
    private Vector3 _pelvisDelta, _blendPelvis;
    private readonly Vector3[] _partPos0 = new Vector3[Parts], _bonePos0 = new Vector3[Parts];
    private readonly Quaternion[] _partRot0 = new Quaternion[Parts], _boneRot0 = new Quaternion[Parts];
    private readonly Quaternion[] _blendRot = new Quaternion[Parts], _animRot = new Quaternion[Parts];

    // ---------------------------------------------------------------- release

    public bool Dying => Active && _dying;

    // ---- registry / busy state (fire + action blocking) ----
    internal static readonly List<NmBody> AllBodies = new List<NmBody>();
    private static readonly int[] FootParts = { 3, 6 };

    /// Any bot in a reaction, a get-up or a blend right now (the per-frame hold patches bail out early otherwise).
    internal static bool AnyBusy
    {
        get
        {
            for (int i = 0; i < AllBodies.Count; i++) if (AllBodies[i].Busy && AllBodies[i] != null) return true; // Busy first: plain fields
            return false;
        }
    }
    private void Awake() { AllBodies.Add(this); }
    /// Euphoria owns the body, he is getting up, or the blend back to animation is still running.
    internal bool Busy => Active || _getUp != null || _blending;
    internal Player Owner => _p;

    internal static bool IsBusy(Player p)
    {
        for (int i = 0; i < AllBodies.Count; i++)
            if (AllBodies[i].Busy && AllBodies[i] != null && AllBodies[i]._p == p) return true;
        return false;
    }

    /// Called from the hold patches: is this bot's own AI kept off right now (busy with Euphoria and not
    /// aiming back, whose shots are his AI's own)?
    internal static bool HoldsAI(Player p)
    {
        for (int i = 0; i < AllBodies.Count; i++)
        {
            NmBody b = AllBodies[i];
            if (b.Busy && b != null && b._p == p) return !b._aimBack;
        }
        return false;
    }

    /// Called from the trigger patch: may this bot's shot go off right now?
    internal static bool ShotAllowedFor(Player p)
    {
        for (int i = 0; i < AllBodies.Count; i++)
        {
            NmBody b = AllBodies[i];
            if (b.Busy && b != null && b._p == p) return b.AimShotAllowed();
        }
        return true;
    }

    // ---- aim-back: right arm points the gun at whoever shot him ----
    private bool _aimBack, _aimOn;
    private Func<Vector3> _aimTarget;
    private float _aimStart, _aimEnd, _nextAim, _nextShot;
    private int _aimShots, _aimLogs;

    private bool AimShotAllowed()
    {
        if (!_aimBack || !_aimOn || !Active || _dying || _gun == null) return false;
        if (nm_agent_has_fallen(_agent) == 1) return false;
        if (Time.time < _nextShot) return false;
        Vector3 t;
        try { t = _aimTarget(); } catch { return false; }
        Vector3 muzzle = _fireport != null ? _fireport.position : _gun.position;
        float ang = Vector3.Angle(BarrelDir, (t - muzzle).normalized);
        if (_aimLogs++ < 6) EuphoriaPlugin.Log($"Delirium: aim-back angle {ang:F0} deg");
        if (ang > (EuphoriaPlugin.Instance?.AimBackCone.Value ?? 40f)) return false;
        _nextShot = Time.time + UnityEngine.Random.Range(0.28f, 0.6f);
        _grantUntil = Time.time + 0.35f;
        _aimShots++;
        return true;
    }

    private float _grantUntil, _gunArmAnim;
    private readonly Vector3[] _armPivot = new Vector3[6];     // shoulder, elbow, wrist joints of each arm (Unity space)
    private readonly bool[] _armPivotOk = new bool[2];

    /// The actual bullet launch (InitiateShot): only inside a window the trigger check granted.
    internal static bool LaunchAllowedFor(Player p)
    {
        for (int i = 0; i < AllBodies.Count; i++)
        {
            NmBody b = AllBodies[i];
            if (b.Busy && b != null && b._p == p) return Time.time < b._grantUntil;
        }
        return true;
    }

    private void AimBackTick()
    {
        if (!_aimBack) return;
        bool fallen = nm_agent_has_fallen(_agent) == 1;
        if (fallen || Time.time > _aimEnd || _gun == null || _gunDropped) { StopAim(); return; }
        if (Time.time < _aimStart || Time.time < _nextAim) return;
        _nextAim = Time.time + 0.15f;
        Vector3 t;
        try { t = _aimTarget(); } catch { StopAim(); return; }
        if (!_aimOn)
        {
            Send("pointArm", ("start", true), ("useLeftArm", false), ("target", t), ("twist", -0.4f),
                 ("armStraightness", 0.92f), ("pointSwingLimit", 1.3f), ("armStiffness", 18f));
            _aimOn = true;
        }
        else Send("pointArm", ("start", true), ("useLeftArm", false), ("target", t), ("twist", -0.4f),
                  ("armStraightness", 0.92f), ("pointSwingLimit", 1.3f), ("armStiffness", 18f));
    }

    private void StopAim()
    {
        if (_aimOn && _agent != IntPtr.Zero) Send("pointArm", ("start", false));
        if (_aimBack) EuphoriaPlugin.Log($"Delirium: aim-back over ({_aimShots} shots let through)");
        _aimBack = false; _aimOn = false; _aimTarget = null;
    }

    public void Release() => Release(false);

    private Vector3 _safeRoot;  // his root on the last frame the doll moved at a sane speed (runaway fallback)
    private bool _haveSafeRoot;

    private void Release(bool finished)
    {
        if (_pgu) { _pgu = false; _pguHave = false; _getUp = null; }
        SnapReset();
        if (_rigCheck) { DevRigCheckEnd(); return; } // nothing to blend back: the bones were never driven
        _watchUntil = finished ? 0f : Time.time + 3f;
        if (Active) GapReport();
        SetPhase(Phase.None, _dying ? "death over" : "handed back");
        if (Active && !_dying) HoldLookFor(_blendDur + 0.8f); // he keeps the heading he's handed back in
        _grabbing = false;
        _rolling = false;
        _matching = false;
        bool alive = _p != null && _p.HealthController?.IsAlive == true;
        if (Active)
        {
            EuphoriaPlugin.Log($"Delirium: release{(_dying ? " (death struggle over -> SPT ragdoll)" : "")} after {Time.time - _attachAt:F1}s, peak part speed {_peakSpeed:F1} m/s");
            PerfReport();
        }
        if (_aimBack) StopAim();
        if (_dying || (!alive && !AmosBridge.OwnsDeaths)) ReleaseCorpse(); // its death motion has the corpse: hands off (no weights either)
        if (_p != null && _speedLimitApplied && !_holdOver)
        {
            try { _p.RemoveStateSpeedLimit(Player.ESpeedLimit.Shot); } catch { }
            _speedLimitApplied = false;
        }
        if (_agent != IntPtr.Zero) nm_agent_destroy(_agent);
        if (_world != IntPtr.Zero) nm_world_destroy(_world);
        if (_build != IntPtr.Zero && _ownBuild) nm_build_destroy(_build);
        _agent = _world = _build = IntPtr.Zero;
        _ownBuild = false;
        _dying = false;
        if (!_holdOver) { if (alive) RestoreGunBlended(_blendDur); else RestoreGun(); }
        // Alive: ease the bones back into the animation instead of snapping.
        if (alive && _bones != null && _bones[0] != null)
        {
            EndGameJump("hand-back");
            // last safety net: the root is never handed back above the ground under the body
            if (_p != null && !_holdOver && RayWorld(_bones[0].position + Vector3.up * 0.1f, Vector3.down, 80f, out RaycastHit floor)
                && floor.normal.y > 0.5f && _p.Transform.position.y > floor.point.y + DropMin)
                MoveRoot(new Vector3(_p.Transform.position.x, floor.point.y, _p.Transform.position.z));
            if (_p != null && !_holdOver) BotActions.SyncMover(_p);
            CaptureHandBack(); // the pose and its speed, for the spring back into the animation (NmBodyHandBack)
            _blending = true;
            _gunAfterBlend = !_holdOver;
            _groundUntil = Time.time + _blendDur + 3f; _rootHighChecks = 0;
            enabled = true;
        }
        else
        {
            _blending = false;
            enabled = false;
        }
    }

    // ---------------------------------------------------------------- tick

    private void Update()
    {
        long pf = PerfNow();
        try { UpdateCore(); }
        finally { PerfFrame(pf); }
    }

    private void UpdateCore()
    {
        if (!Active && !_rigCheck)
        {
            LookHoldTick();
            if (Busy && _p != null && !_aimBack && (EuphoriaPlugin.Instance?.CancelActions.Value ?? true)) EuphoriaHold.HeldMove(_p); // keeps him grounded
            // still recovering (get-up / roll clip, blend back): nothing he'd started may carry on
            if (Busy && _p != null && Time.time >= _nextCancel) { BotActions.Cancel(_p); _nextCancel = Time.time + 0.5f; }
            FailJumpTick();
            JumpAfterTick();
            GroundRootTick();
            if (!_blending && !_jumpTaken && _getUp == null && Time.time > _watchUntil && Time.time > _groundUntil) enabled = false;
        }
        if (!Active || _rigCheck) return; // a Rig check doll is frozen: no stepping, no bone driving
        if (_p == null) { Release(true); return; }
        if (!_aimBack && (EuphoriaPlugin.Instance?.CancelActions.Value ?? true)) EuphoriaHold.HeldMove(_p); // the game's grounding tick, no walking
        bool alive = _p.HealthController?.IsAlive == true;
        // Died some other way mid-reaction (bleed-out, a hit we didn't see):
        // go straight into the death struggle.
        if (!alive && !_dying)
        {
            if (AmosBridge.OwnsDeaths) { Release(); return; } // Euphoria Style Physics' muscles have his corpse
            BeginDying();
        }

        if (_gun == null && Time.time >= _nextGun) { _nextGun = Time.time + 0.4f; GunToHand(); }
        if (Time.time >= _nextCancel) { BotActions.Cancel(_p); _nextCancel = Time.time + 0.5f; }
        // at most 3 steps a frame: after a hitch, 6 steps of catch-up made the next frame a hitch too;
        // below 20 fps the doll runs a little slow instead
        _acc = Mathf.Min(_acc + Time.deltaTime, Step * 3f);
        if (_acc >= Step) { ImpactBefore(); BubbleTick(); PhysGetUpTarget(); }
        // the steps and everything after them run with the other reacting bots' at the end of Update,
        // the dolls side by side (NmBodySteps)
        if (_acc >= Step && DeferSteps()) return;
        bool stepped = false;
        while (_acc >= Step)
        {
            _acc -= Step;
            GrabStep();
            long ps = PerfNow();
            nm_agent_step(_agent, _world, Step);
            PerfStep(ps);
            PhysGetUpAssist(); // getting up through the doll: pelvis and chest helped towards the clip
            SnapStep();      // the state the bones are interpolated from (NmBodyInterp)
            stepped = true;
        }
        UpdateAfterSteps(stepped);
    }

    /// Update from the steps on: the doll's new state read, and every tick that follows from it.
    private void UpdateAfterSteps(bool stepped)
    {
        if (stepped) { ImpactAfter(); FloorNet(); }

        float vmax = 0f;
        for (int k = 0; k < Parts; k++)
        {
            nm_agent_get_part_velocity(_agent, k, _f3, _g3);
            vmax = Mathf.Max(vmax, new Vector3(_f3[0], _f3[1], _f3[2]).magnitude);
        }
        _peakSpeed = Mathf.Max(_peakSpeed, vmax);
        ImpactDamageTick();
        if (vmax > 25f)
        {
            // the doll blew up: his root and bones may already be metres off. Root back to the last
            // frame the doll was sane, and straight into the animation (no blend from the blown-up pose).
            float moved = _haveSafeRoot ? Vector3.Distance(_p.Transform.position, _safeRoot) : 0f;
            if (_haveSafeRoot) MoveRoot(_safeRoot);
            EuphoriaPlugin.Log($"Delirium: runaway ({vmax:F0} m/s) — releasing, root put back {moved:F2} m");
            Release(true);
            _blending = false;
            if (_haveSafeRoot) BotActions.SyncMover(_p);
            return;
        }
        if (vmax < 12f) { _safeRoot = _p.Transform.position; _haveSafeRoot = true; }

        if (CheckLevelAndHover(vmax)) return;
        if (_dying) { if (_pgu) PhysGetUpStop("died"); if (_aimBack) StopAim(); UpdateDying(); return; }
        if (PhysGetUpTick()) return; // getting up through the doll (NmBodyPhysGetUp)
        FallWatchTick(); // going over: no aim-back, no reach for the gun (NmBodyFall)
        AimBackTick();
        BraceTick();
        TripTick(); // legs caught on something low, a rail at his hips, rolling down steps (NmBodyTrip)
        bool inAir = AirTick(); // off the ground: nothing below that needs his feet on it until he lands (NmBodyAir)

        nm_agent_get_com(_agent, _f3, _g3);
        float comSpeed = new Vector2(_g3[0], _g3[1]).magnitude; // horizontal (NM z is up)
        float h = _f3[2] - GroundUnder(ToUnity(_f3[0], _f3[1], _f3[2]));
        bool canFall = EuphoriaPlugin.Instance?.AliveFalls.Value ?? true;
        if (FailSafeTick()) return; // in the reaction far too long, whatever he's doing: jump up to stand
        if (_grabbing) return; // held by the dev-panel drag: no hand-back, get-up or timeout until let go
        FacingTick(); // the root faces the way the body does, so no hand-back turns him back
        if (RollTick()) return; // rolling onto his front to go prone
        if (MatchTick(comSpeed)) return; // moving into the hand-back stance
        if (!inAir && SettleTick(comSpeed, vmax, h)) return; // resting close to a stance for a while: into it
        if (StuckTick(h)) return; // far too long since the last hit: closest stance
        if (!inAir && StumbleSaveTick(h)) return; // going over close to upright: most of the time he catches himself
        // heights assume a standing body: scaled to where a crouched bot's COM started; a prone
        // bot is already down (stumble-only mode doesn't apply to him)
        if (!canFall && _startPose != StartPose.Prone)
        {
            // stumble-only: hand back before he hits the floor
            if (h < ComScaled(0.65f, 0.75f) || (nm_agent_has_fallen(_agent) == 1 && h < ComScaled(0.85f, 0.9f)))
            {
                EuphoriaPlugin.Log($"Delirium: losing balance (COM {h:F2}m) — back to animation");
                ReleaseToStance();
                return;
            }
        }
        else if (_downSince == 0f && !_stairRoll && nm_agent_has_fallen(_agent) == 1 && h < ComScaled(0.6f, 0.7f))
        {
            CatchDueTick(0f); // a backward fall's arm wheel ends as he lands
            _downSince = Time.time; // he's down: protected, and up again once he's still (NmBodyPhase)
            FallWaitStart();
            SetPhase(Phase.Fall, "down");
            FallenPose(false); // on the ground: one of several ways of lying, not always a ball
            EuphoriaPlugin.Log($"Delirium: down (COM {h:F2}m)");
        }
        // going over: arms out to catch the fall (once, before he lands)
        if (_downSince == 0f && !_catchFallSent && nm_agent_has_fallen(_agent) == 1) GoDown(h, "going over"); // (NmBodyPhase)
        CatchDueTick(h);
        if (_downSince > 0f)
        {
            if (FallOverTick()) // still a moment, or down too long: up again (NmBodyPhase)
            {
                if (EuphoriaPlugin.Instance?.CapturePoses.Value ?? true) CaptureLyingPose("auto"); // get-up start pose, for the clip generator
                StartPose target = RecoverStance();
                SetPhase(Phase.Recover, $"still after {Time.time - _downSince:F1} s down, target {target.ToString().ToLowerInvariant()}: {StumbleEngine.HpFraction(_p):P0} HP");
                // Hand back from the ground: a get-up, or prone when face down and badly hurt (GetUp)
                if ((EuphoriaPlugin.Instance?.FallenPose.Value ?? true) && HandBackFromGround()) return;
                _blendDur = GetUpBlend; // plain blend back
                Release();
            }
            return;
        }
        // Found his feet: balancing, barely drifting, upright for his pose, a beat after the last
        // hit -> back to his own stance now instead of waiting out the reaction.
        float uprightEnough = _crouchStumble ? ComScaled(0.5f, 0.55f) : ComScaled(0.8f, 0.85f); // a crouch counts as recovered
        bool steadyBody = nm_agent_is_balancing(_agent) == 1 && comSpeed < RecoverSpeed && h > uprightEnough
                          && Grounded(true) && TrunkTilt() < 35f && Time.time >= _matchCooldown;
        if (_reaction == Phase.Stumble && !inAir) { StumbleTick(steadyBody, h, canFall); return; } // the stumble clock (NmBodyPhase)
        bool steady = steadyBody && Time.time - _lastHitAt > RecoverAfterHit && Time.time - _attachAt > RecoverAfterHit && !_aimBack;
        _stableFor = steady ? _stableFor + Time.deltaTime : 0f;
        if (_stableFor > RecoverHold)
        {
            EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} found his feet after {Time.time - _attachAt:F1}s - back to his stance{(_crouchStumble ? " (crouched)" : "")}");
            if (!RecoverOnFeet(false, h)) ReleaseToStance();
            return;
        }
        if ((Time.time > _until || nm_agent_is_settled(_agent) == 1) && (Grounded(false) || Time.time > _until + 4f)) // never hand back mid-air
            if (!RecoverOnFeet(false, h)) ReleaseToStance();
    }

    private const float RecoverSpeed = 0.35f;    // m/s COM drift that still counts as steady
    private const float RecoverAfterHit = 1.2f;  // s after the last hit before he may recover
    private const float RecoverHold = 0.6f;      // s steady before handing back
    private float _lastHitAt, _stableFor;
    private bool _catchFallSent;

    private void LateUpdateCore()
    {
        if (_rigCheck) return;
        if (_getUp != null && !_pgu) { WarpCapture(); GetUpTick(); HoldGun(); if (_twins.Count > 0) SyncTwins(); return; }
        if (!Active)
        {
            if (_blending) { BlendToAnimation(); if (_twins.Count > 0) SyncTwins(); }
            return;
        }
        if (_dying) HoldCorpse();
        if (_matching || MatchFading) CaptureAnimPose(); // the stance the animation plays, before the physics overwrites it
        if (_pgu) { WarpCapture(); PhysGetUpPose(); } // getting up: the clip's pose on the bones, kept, before the doll's goes on
        // The gun hand keeps the wrist the animation gave it (relative to the forearm) while he holds
        // a gun: the physics wrist flopped the hand, and the gun swung with it.
        bool lockWrist = _gun != null && !_gunDropped && _bones[20] != null;
        Quaternion palmAnimLocal = lockWrist ? _bones[20].localRotation : Quaternion.identity;
        // SPT's feet copy the doll's foot: its ankle is a real joint (NmBodyFeet).
        KneeAxisSample();

        Part(0, out Vector3 pp, out Quaternion pr);
        // rigid offset: the pelvis bone isn't at the part's centre, so the
        // offset must turn with the part (translation-only sank a lying body)
        Vector3 pelvisTarget = pp + pr * (Quaternion.Inverse(_partRot0[0]) * (_bonePos0[0] - _partPos0[0]));

        // Root follows on XZ only, first, and never through a wall — the
        // AI/hitbox root must stay on the same side as the body. A low obstacle the body has
        // gone over (rail, ledge) is the exception: the root lands on the far side with it.
        if (!_dying)
        {
            Vector3 root = _p.Transform.position;
            Vector3 want = new Vector3(pelvisTarget.x - _pelvisDelta.x, root.y, pelvisTarget.z - _pelvisDelta.z);
            Vector3 step = want - root;
            float dist = step.magnitude;
            if (dist > 1e-4f)
            {
                if (RayWorld(root + Vector3.up * 0.5f, step / dist, dist + 0.3f, out RaycastHit wall))
                {
                    if (CanFollowOver(root, want, step / dist, dist, wall, pelvisTarget, out Vector3 landed)) want = landed;
                    else want = root + step / dist * Mathf.Max(0f, wall.distance - 0.3f);
                }
            }
            // resting on something higher or lower (dropped on a crate, knocked off a ledge): the
            // root goes there with the body, onto the spot under the pelvis
            Vector3 underPelvis = new Vector3(pelvisTarget.x - _pelvisDelta.x, root.y, pelvisTarget.z - _pelvisDelta.z);
            if (SupportFollow(root, pelvisTarget, underPelvis, out Vector3 supported)) want = supported;
            else if (DropFollow(want, pelvisTarget, out Vector3 dropped)) want = dropped; // never left above the ground under him
            MoveRoot(want); // through his controller, so the capsule goes with him (NmBodyRoot)
            if (Time.time >= _nextSync) { _nextSync = Time.time + 0.25f; BotActions.SyncMover(_p); }
        }

        // Bones in world space, parent-first. Standing, the arms stay on the
        // animation (aim, weapon hold); once he's down (or dying and collapsed)
        // the arms join the physics so they can brace, flail and take a get-up.
        bool armsOn = _downSince > 0f || _catchFallSent || (_dying && _phase != DeathPhase.Stagger)
                      || (!_dying && (EuphoriaPlugin.Instance?.ArmsInStumble.Value ?? true));
        if (armsOn && _armsSince == 0f) _armsSince = Time.time;
        if (!armsOn) _armsSince = 0f;
        float armW = armsOn ? Mathf.SmoothStep(0f, 1f, (Time.time - _armsSince) / 0.3f) : 0f;
        int driven = armsOn ? Parts : 13;
        // going over (catching the fall), down, or dying: the arms are the doll's. Held near the animated
        // hold until he hit the floor, they stayed in the gun pose while the doll threw them out and the
        // torso turned over, and read as arms folding in mid-fall.
        bool lyingArms = _downSince > 0f || _dying || _catchFallSent;
        // Doll limb directions (joint to joint). The skeleton's arms are matched to these DIRECTIONS rather than
        // to the part rotations alone: the skeleton's shoulders sit where the animation put them, not where the
        // doll's do, so copying rotations leaves every arm bone pointing slightly wrong and the chain folds.
        bool haveArmPose = false;
        for (int side = 0; side < 2 && armsOn; side++)
        {
            int b0 = side == 0 ? 14 : 18;
            bool okj = true;
            for (int j = 0; j < 3; j++) okj &= Joint(b0 + j, out _, out _armPivot[side * 3 + j], out _);
            if (okj) { _armPivotOk[side] = true; haveArmPose = true; } else _armPivotOk[side] = false;
        }
        for (int k = 0; k < driven; k++)
        {
            if (_bones[k] == null) continue;
            Quaternion rot = k == 0 ? pr : PartRot(k);
            Quaternion delta = rot * Quaternion.Inverse(_partRot0[k]);
            if (k == 0) _bones[0].SetPositionAndRotation(pelvisTarget, delta * _boneRot0[0]);
            else if (k == 20 && lockWrist)
            {
                _bones[20].localRotation = palmAnimLocal; // straight wrist: follows the (physics) forearm only
            }
            else if (k >= 13)
            {
                // Arms: physics, but while he is still on his feet they may only stray so far from the
                // animated pose (this frame's animation carried onto the physics torso). Unclamped,
                // the doll's elbows fold into the chest and the arms curl unnaturally.
                Quaternion anim = _bones[k].rotation, want = delta * _boneRot0[k];
                int limb = (k == 14 || k == 15) ? 0 : (k == 18 || k == 19) ? 1 : -1;     // upper arm / forearm of each side
                if (haveArmPose && limb >= 0 && _armPivotOk[limb] && _bones[k + 1] != null)
                {
                    int pj = limb * 3 + (k - (limb == 0 ? 14 : 18));
                    _bones[k].rotation = want;                                           // child bone moves with it
                    Vector3 cur = _bones[k + 1].position - _bones[k].position;
                    Vector3 tgt = _armPivot[pj + 1] - _armPivot[pj];
                    if (cur.sqrMagnitude > 1e-6f && tgt.sqrMagnitude > 1e-6f)
                        want = Quaternion.FromToRotation(cur, tgt) * want;
                }
                if (!lyingArms && !LockOn) // on his feet (older drive only): no further than so far from the hold
                {
                    int lk = k >= 17 ? k - 4 : k;                              // mirror right arm onto left indices
                    float lim = (lk == 13 ? 30f : lk == 14 ? 70f : lk == 15 ? 55f : 40f) * Tune.ArmLimit;
                    if (k >= 17) lim *= Tune.GunArm;                         // the gun arm stays closer to its hold
                    if (k >= 17 && _aimOn) lim *= Tune.GunArmAim;            // the gun arm swings out to aim
                    want = Quaternion.RotateTowards(anim, want, lim);
                    // gun arm: if the hand has swung behind the body, pull it back to the animated hold
                    if (k >= 17 && !_aimOn && _gunArmAnim > 0.001f) want = Quaternion.Slerp(want, anim, _gunArmAnim);
                }
                _bones[k].rotation = Quaternion.Slerp(anim, want, armW);
            }
            else _bones[k].rotation = delta * _boneRot0[k];
        }

        if (LockOn) LockToJoints(armsOn, lockWrist, armW); // bones aimed at the doll's joints, hands and feet IK'd onto it
        else ClampToJoints(armsOn, lockWrist); // SPT follows, held to the doll's joint ranges
        LegUntwist(true); // shins without a roll on their thighs, feet on the doll's ankle (NmBodyLegTwist)

        // Gun-hand guard: where is the gun hand relative to the body? In front is right; level with
        // the chest or behind means the physics arm flung it back, so the animated hold takes over.
        if (!lyingArms && !LockOn && _bones[20] != null)
        {
            Vector3 local = _p.Transform.InverseTransformPoint(_bones[20].position);
            float wantAnim = _aimOn ? 0f : Mathf.Clamp01(Mathf.InverseLerp(0.30f, 0.05f, local.z));
            _gunArmAnim = Mathf.MoveTowards(_gunArmAnim, wantAnim, Time.deltaTime * 6f);
        }
        else _gunArmAnim = 0f;

        // Foot grounding (alive): match the skeleton's lowest foot to the doll's (the aim pass puts the
        // ankles on the doll's already).
        if (!_dying && !LockOn)
        {
            float lift = float.MaxValue;
            foreach (int f in FootParts)
            {
                if (_bones[f] == null) continue;
                Part(f, out Vector3 fp, out _);
                lift = Mathf.Min(lift, _bones[f].position.y - fp.y);
            }
            if (lift != float.MaxValue && Mathf.Abs(lift) > 0.002f)
                _bones[0].position += Vector3.up * Mathf.Clamp(-lift, -0.15f, 0.15f);
        }

        if (!armsOn) ApplyClutch();
        if (_dying || _downSince > 0f) GroundClamp();
        if (!_matching) GapTick(armsOn, lockWrist); // how far SPT is from the doll (reported at the hand-back)
        if (_matching || MatchFading) MatchVisual(); // recovering: SPT's bones settle into the stance he's handed back in (eased off when cancelled)
        if (_pgu) PhysGetUpShow(); // getting up: the clip mixed back in over the doll
        KneeGuard(); // whatever posed them: knees bend only the way a knee does (NmBodyKneeGuard)
        LegUntwist(false); // the guard's turn leaves no roll either
        LegTwistTick(_pgu ? "physical get-up" : _matching ? "stance match" : _dying ? "dying" : "doll's pose"); // diagnostic (NmBodyLegTwist)
        HoldGun();
        if (_matching) ReachSupportGrip(); // recovering: the free hand finds its place on the gun again
        // what we gave the arms this frame, for the late pass to put back if the game re-poses them after us
        _armWritten = armsOn;
        if (armsOn) for (int k = 13; k < Parts; k++) if (_bones[k] != null) { _armWrite[k] = _bones[k].localRotation; _armWriteLocal[k] = _bones[k].localPosition; }
        _armWriteFrame = Time.frameCount;
        if (_twins.Count > 0) SyncTwins();
    }

    private readonly Quaternion[] _armWrite = new Quaternion[21];
    private readonly Vector3[] _armWriteLocal = new Vector3[21];
    private int _armWriteFrame = -1;
    private bool _armWritten, _armOverrideLogged;

    // Lying body: the EFT mesh is bulkier than the doll (armour, backpack),
    // so lift the pelvis until the trunk is above the real ground. Limbs
    // only get a small say, and the probe starts just above each bone, so a
    // curb or prop beside the body can't lift it into the air.
    private static readonly int[] ClampBones = { 0, 7, 8, 10, 12, 1, 4, 2, 5, 3, 6, 14, 18 };
    private static readonly float[] ClampRadius = { 0.14f, 0.14f, 0.16f, 0.2f, 0.13f, 0.09f, 0.09f, 0.07f, 0.07f, 0.06f, 0.06f, 0.06f, 0.06f };
    private static readonly float[] ClampMax = { 0.45f, 0.45f, 0.45f, 0.45f, 0.45f, 0.15f, 0.15f, 0.1f, 0.1f, 0.08f, 0.08f, 0.08f, 0.08f };
    private float _groundLift;

    private void GroundClamp()
    {
        float need = 0f;
        for (int i = 0; i < ClampBones.Length; i++)
        {
            Transform b = _bones[ClampBones[i]];
            if (b == null) continue;
            Vector3 p = b.position;
            float ground = RayWorld(p + Vector3.up * 0.3f, Vector3.down, 1.2f, out RaycastHit h) ? h.point.y : p.y - 10f;
            need = Mathf.Max(need, Mathf.Min(ground + ClampRadius[i] - p.y, ClampMax[i]));
        }
        need = Mathf.Clamp(need, 0f, 0.45f);
        // rise immediately, settle back quickly (no jitter, no hovering)
        _groundLift = need >= _groundLift ? need : Mathf.Lerp(_groundLift, need, Time.deltaTime * 12f);
        if (_groundLift > 0.001f) _bones[0].position += Vector3.up * _groundLift;
    }

    // Level follows the body (re-bake when he travels or drops), and a
    // safety net for a body hovering on something that isn't there.
    private bool CheckLevelAndHover(float vmax)
    {
        nm_agent_get_com(_agent, _f3, _g3);
        Vector3 com = ToUnity(_f3[0], _f3[1], _f3[2]);
        PredictLanding();
        Vector2 off = new Vector2(com.x - _bakeCentre.x, com.z - _bakeCentre.z);
        bool far = off.magnitude > 2.5f || _bakeCentre.y - com.y > 1.0f;
        if (far && Time.time >= _nextRebake && _rebakes < 8)
        {
            _rebakes++;
            _nextRebake = Time.time + 0.4f;
            float gy = RayWorld(com + Vector3.up * 0.3f, Vector3.down, 25f, out RaycastHit g) ? g.point.y : _bakeCentre.y;
            BakeLevel(_p, new Vector3(com.x, gy, com.z));
            return false;
        }
        // hovering: down (or dying and past the stagger), nearly still, trunk well above the real ground
        // not while he gets up or matches a stance: a pelvis rising off the floor isn't hovering (it ended get-ups halfway)
        bool lying = nm_agent_has_fallen(_agent) == 1 && (!_dying || _phase != DeathPhase.Stagger) && !_pgu && !_matching;
        if (lying && vmax < 1.5f)
        {
            Part(0, out Vector3 pp, out _);
            float gap = pp.y - (RayWorld(pp + Vector3.up * 0.3f, Vector3.down, 8f, out RaycastHit hg) ? hg.point.y : _floorY);
            if (gap > 0.5f)
            {
                if (_hoverSince == 0f) _hoverSince = Time.time;
                else if (Time.time - _hoverSince > 0.7f)
                {
                    EuphoriaPlugin.Log($"Delirium: hovering {gap:F2}m above the ground on {(hg.collider != null ? hg.collider.name : "nothing")} — ending the reaction");
                    _hoverSince = 0f;
                    if (_dying) Release(true); else { _blendDur = 0.4f; Release(); }
                    return true;
                }
            }
            else _hoverSince = 0f;
        }
        else _hoverSince = 0f;
        return false;
    }

    // ------------------------------------------------ wound clutch (left hand)

    private int _clutchBone = -1;
    private Vector3 _clutchLocal;
    private float _clutchAt, _clutchEnd;

    // The left hand grabs the last wound (the right hand keeps the gun),
    // pinned to the hit bone so it follows the body as he staggers.
    private static bool WoundGrab => EuphoriaPlugin.Instance?.WoundGrab.Value ?? false;

    private void StartClutch(Vector3 at, Vector3 dir, float hold)
    {
        if (!WoundGrab) { _clutchBone = -1; return; } // switched off: the hands don't go to the wound
        if (_bones[14] == null || _bones[15] == null || _bones[16] == null) return;
        int best = -1; float bd = float.MaxValue, armd = float.MaxValue;
        for (int k = 0; k < Parts; k++)
        {
            if (_bones[k] == null) continue;
            float d = (_bones[k].position - at).sqrMagnitude;
            if (k <= 12) { if (d < bd) { bd = d; best = k; } }
            else armd = Mathf.Min(armd, d);
        }
        if (best < 0 || armd < bd) return;            // hit in the arm: nothing to clutch
        if (best == 3 || best == 6 || best == 2 || best == 5) return; // can't reach the shins/feet standing
        Vector3 grab = at - dir.normalized * 0.05f;    // on the surface, shooter side
        _clutchBone = best;
        _clutchLocal = _bones[best].InverseTransformPoint(grab);
        _clutchAt = Time.time;
        _clutchEnd = Time.time + hold;
    }

    private void ApplyClutch()
    {
        if (_clutchBone < 0 || _bones[_clutchBone] == null) return;
        float now = Time.time;
        float w = Mathf.SmoothStep(0f, 1f, (now - _clutchAt - 0.12f) / 0.25f);
        if (now > _clutchEnd) w *= 1f - Mathf.Clamp01((now - _clutchEnd) / 0.4f);
        if (now > _clutchEnd + 0.4f) { _clutchBone = -1; return; }
        if (w <= 0.001f) return;
        LeftArmIK(_bones[_clutchBone].TransformPoint(_clutchLocal), w);
    }

    /// Two-bone IK on the left arm: the palm towards `target`, weight w, elbow kept on its side.
    private void LeftArmIK(Vector3 target, float w)
    {
        Transform up = _bones[14], fo = _bones[15], ha = _bones[16];
        if (up == null || fo == null || ha == null) return;
        Vector3 s = up.position, e = fo.position, h = ha.position;
        float a = (e - s).magnitude, b = (h - e).magnitude;
        if (a < 1e-3f || b < 1e-3f) return;
        Vector3 toT = target - s;
        float d = Mathf.Clamp(toT.magnitude, Mathf.Abs(a - b) + 0.01f, a + b - 0.005f);
        Vector3 tn = toT.normalized, pole = e - s;
        Vector3 n = Vector3.Cross(tn, pole);
        if (n.sqrMagnitude < 1e-6f) n = Vector3.Cross(tn, _p.Transform.right);
        n.Normalize();
        float ang = Mathf.Acos(Mathf.Clamp((a * a + d * d - b * b) / (2f * a * d), -1f, 1f)) * Mathf.Rad2Deg;
        Vector3 e1 = Quaternion.AngleAxis(ang, n) * tn, e2 = Quaternion.AngleAxis(-ang, n) * tn;
        Vector3 ed = Vector3.Dot(e1, pole) >= Vector3.Dot(e2, pole) ? e1 : e2; // keep the elbow on its side
        Quaternion upOrig = up.rotation;
        up.rotation = Quaternion.Slerp(upOrig, Quaternion.FromToRotation(e - s, ed) * upOrig, w);
        e = fo.position; h = ha.position;
        Quaternion foOrig = fo.rotation;
        fo.rotation = Quaternion.Slerp(foOrig, Quaternion.FromToRotation(h - e, target - e) * foOrig, w);
    }

    private bool _gunAfterBlend; // he's handed back for good (no get-up to follow): his weapon back once the blend ends

    private void OnDestroy()
    {
        AllBodies.Remove(this);
        // his kept standing binding goes with him once he's gone (despawned or dead), not before
        if ((object)_p != null && (_p == null || _p.HealthController == null || !_p.HealthController.IsAlive))
            _standingBindings.Remove(_p.GetInstanceID());
        Release(true);
    }

    // ---------------------------------------------------------------- helpers

    private Quaternion PartRot(int k)
    {
        if (ViewPart(k, out _, out Quaternion vr)) return vr;
        nm_agent_get_part_transform(_agent, k, _f3, _q4);
        return ToUnity(_q4[0], _q4[1], _q4[2], _q4[3]);
    }

    private void Part(int k, out Vector3 pos, out Quaternion rot)
    {
        if (ViewPart(k, out pos, out rot)) return;
        nm_agent_get_part_transform(_agent, k, _f3, _q4);
        pos = ToUnity(_f3[0], _f3[1], _f3[2]);
        rot = ToUnity(_q4[0], _q4[1], _q4[2], _q4[3]);
    }

    /// Any of the engine's messages, e.g. Send("bodyBalance", ("start", true)).
    internal int Send(string message, params (string name, object value)[] ps)
    {
        if (!Active) return -1;
        nm_msg_begin(_agent, message);
        foreach (var (k, v) in ps)
        {
            switch (v)
            {
                case float f: nm_msg_set_float(_agent, k, f); break;
                case int i: nm_msg_set_int(_agent, k, i); break;
                case bool b: nm_msg_set_bool(_agent, k, b ? 1 : 0); break;
                case Vector3 p: ToNm(p, _f3); nm_msg_set_vec3(_agent, k, _f3); break;
                case string s: nm_msg_set_string(_agent, k, s); break;
            }
        }
        return nm_msg_send(_agent, _world);
    }

    private static Vector3 ToUnity(float x, float y, float z) => new Vector3(x, z, y);
    private static Quaternion ToUnity(float x, float y, float z, float w) => new Quaternion(-x, -z, -y, w);
    private static void ToNm(Vector3 v, float[] o, int at = 0) { o[at] = v.x; o[at + 1] = v.z; o[at + 2] = v.y; }
}
