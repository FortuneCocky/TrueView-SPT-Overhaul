using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EFT;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace BloodFX;

/// <summary>
/// Head dismemberment, in two kinds. Whole: the posed head is baked and broken into jagged shards
/// that keep the character's own skin texture (with a raw underside), the head bone is collapsed,
/// and a ragged neck stump with no skull is grown in its place from a distance field. Face: one side
/// of the face, brow to chin, is torn out; the rest of the head stays on as a baked copy riding the
/// head bone, and the hole is filled with a wound grown the same way (eye socket, cheekbone, both jaws
/// with teeth, tongue, torn meat, peeled skin flaps) so the hollow skin is never seen through. Either
/// way whatever is worn on the head (helmet, mask, glasses, headset) is knocked off and flies as a
/// rigid piece. Purely visual: gear, loot and hit zones are untouched.
/// </summary>
internal sealed class HeadGib
{
    private sealed class Shard
    {
        public GameObject Go;
        public List<Mesh> Meshes;
        public Vector3 Vel, Spin;
        public float Born, Life, R;
        public bool Landed;
    }

    private sealed class Cut
    {
        public Transform Head;
        public bool Collapse;
        public List<Renderer> Hidden;
        public List<GameObject> Owned;
        public List<Mesh> Meshes;
        public bool Released;
    }

    private struct Tri
    {
        public Vector3 A, B, C, Na, Nb, Nc;
        public Vector2 Ua, Ub, Uc;
        public Material M;
    }

    private const int MaxShards = 96, MaxCuts = 24, ShardsPerHead = 14;
    private const float CollapsedScale = 0.001f, FadeTime = 4f;
    private static readonly Color Meat = new Color(0.4f, 0.035f, 0.03f), Fat = new Color(0.62f, 0.46f, 0.34f),
        BoneCol = new Color(0.7f, 0.63f, 0.52f), Hole = new Color(0.1f, 0.006f, 0.006f), Inner = new Color(0.3f, 0.015f, 0.012f),
        Ivory = new Color(0.84f, 0.78f, 0.64f), Tongue = new Color(0.42f, 0.13f, 0.14f);
    private static readonly Vector3 KeyLight = new Vector3(-0.35f, 0.85f, 0.4f).normalized;

    private readonly Transform _root;
    private readonly Material _flesh;
    private readonly int _mask;
    private readonly List<Shard> _shards = new List<Shard>();
    private readonly List<Cut> _cuts = new List<Cut>();
    private readonly HashSet<int> _done = new HashSet<int>();
    private readonly Mesh _bake = new Mesh { name = "BloodFX_HeadBake" };
    private readonly List<Vector3> _bv = new List<Vector3>(), _bn = new List<Vector3>();
    private readonly List<Vector2> _buv = new List<Vector2>();
    private readonly List<int> _tri = new List<int>();

    /// <summary>A shard hit a surface: position, normal, velocity at contact, size.</summary>
    public Action<Vector3, Vector3, Vector3, float> Landed;

    public int ShardCount => _shards.Count;
    public int CutCount => _cuts.Count;

    public HeadGib(Transform root, Material flesh, int mask)
    {
        _root = root;
        _flesh = flesh;
        _mask = mask;
        _bake.MarkDynamic();
    }

    public bool IsDone(Player p) => p != null && _done.Contains(p.GetInstanceID());

    /// <summary>Destroys the head, wholly or one side of the face. Returns false (and changes nothing) if the bones can't be found.</summary>
    /// <param name="half">Tear out one side of the face instead of taking the whole head.</param>
    /// <param name="wound">Where the blood comes out (neck cut, or the torn face), world.</param>
    /// <param name="bleedDir">Which way the wound faces, world.</param>
    /// <param name="anchor">Bone the wound rides on (neck, or the head itself).</param>
    public bool Dismember(Player victim, Vector3 dir, ref bool half, float light, float life, float now,
        out Vector3 wound, out Vector3 bleedDir, out Transform anchor)
    {
        wound = bleedDir = Vector3.zero;
        anchor = null;
        if (victim == null || IsDone(victim)) return false;
        PlayerBones pb = victim.PlayerBones;
        Transform head = pb?.Head?.Original != null ? ((Component)pb.Head.Original).transform : null;
        Transform neck = pb?.Neck != null ? pb.Neck : head != null ? head.parent : null;
        if (head == null || neck == null) return false;
        Vector3 up = head.position - neck.position;
        if (up.sqrMagnitude < 1e-4f) up = head.up;
        up.Normalize();
        Vector3 cutPt = head.position;
        Vector3 centre = cutPt + up * 0.085f;
        _done.Add(victim.GetInstanceID());

        var cut = new Cut { Head = head, Hidden = new List<Renderer>(), Owned = new List<GameObject>(), Meshes = new List<Mesh>() };
        Transform[] keepOut = { pb.LeftPalm, pb.RightPalm, pb.Weapon_Root_Third, pb.Weapon_Root_Anim, pb.WeaponMeleeRoot, pb.WeaponPistolRoot };
        var skins = new List<SkinnedMeshRenderer>();
        var gear = new Dictionary<Transform, List<Renderer>>();
        Sort(victim, head, centre, keepOut, skins, gear);
        foreach (var g in gear) KnockOff(g.Key, g.Value, head, dir, up, now, life, cut);

        // everything drawn on the head above the neck (face, hair, eyes, the top of the neck), posed as it is now
        List<Tri> tris = Gather(victim.GetComponentsInChildren<SkinnedMeshRenderer>(false), gear, centre, cutPt, up);
        if (half && tris.Count < 24) half = false;
        // the live head goes in both kinds: collapsing its bone takes everything skinned to it
        head.localScale = Vector3.one * CollapsedScale;
        cut.Collapse = true;

        if (!half)
        {
            Shatter(tris, centre, dir, up, light, life, now);
            AddStump(head, neck, cutPt, up, light, cut);
            wound = cutPt;
            bleedDir = up;
            anchor = neck;
        }
        else
        {
            // one side of the face, brow to chin, is torn out (the right, or the left when the round went that way)
            float seed = Random.value * 100f;
            Face face = FaceFrame(victim, head, centre, up, dir);
            var kept = new List<Tri>();
            var lost = new List<Tri>();
            foreach (Tri t in tris) (FaceRegion(face.Local((t.A + t.B + t.C) / 3f), seed) < 0f ? lost : kept).Add(t);
            Shatter(lost, centre, dir, up, light, life, now);
            // the rest of the head stays on the body, riding the (collapsed) head bone at full size
            AddKeptFace(head, kept, cut);
            AddFaceWound(head, face, seed, light, cut);
            wound = face.World(new Vector3(0.045f, -0.03f, 0.05f));
            bleedDir = (face.Right * 0.6f + face.Fwd * 0.5f + Vector3.down * 0.5f).normalized;
            anchor = head;
        }
        _cuts.Add(cut);
        while (_cuts.Count > MaxCuts) { Release(_cuts[0]); _cuts.RemoveAt(0); }
        return true;
    }

    /// <summary>
    /// Splits what's drawn at the head into the head's own skin (separate small skinned meshes, not
    /// hung under the head bone) and what's worn on it (anything hung under the head bone, or small
    /// separate meshes right at the head), grouped per worn item. Hands and weapon are left alone.
    /// </summary>
    private static void Sort(Player victim, Transform head, Vector3 centre, Transform[] keepOut,
        List<SkinnedMeshRenderer> skins, Dictionary<Transform, List<Renderer>> gear)
    {
        foreach (Renderer r in victim.GetComponentsInChildren<Renderer>(false))
        {
            if (r == null || !r.enabled || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
            Transform t = r.transform;
            bool under = false;
            foreach (Transform k in keepOut) if (k != null && t.IsChildOf(k)) { under = true; break; }
            if (under) continue;
            Bounds b = r.bounds;
            bool onHead = t.IsChildOf(head) && t != head;
            bool near = (b.center - centre).sqrMagnitude < 0.18f * 0.18f && b.extents.magnitude < 0.35f;
            if (!onHead && !near) continue;
            if (!onHead && r is SkinnedMeshRenderer smr) { skins.Add(smr); continue; }
            if (r is SkinnedMeshRenderer && !onHead) continue;
            // a worn item: the topmost transform below the head bone (or the renderer's own parent)
            Transform key = t;
            if (onHead) while (key.parent != null && key.parent != head) key = key.parent;
            else if (t.parent != null) key = t.parent;
            if (!gear.TryGetValue(key, out List<Renderer> list)) gear[key] = list = new List<Renderer>();
            // of an item with levels of detail, only the most detailed level flies
            LODGroup lod = r.GetComponentInParent<LODGroup>();
            if (lod != null)
            {
                LOD[] lods = lod.GetLODs();
                if (lods.Length > 0 && Array.IndexOf(lods[0].renderers, r) < 0) { list.Add(null); r.enabled = false; continue; }
            }
            list.Add(r);
        }
    }

    // ------------------------------------------------------------------ worn items

    private void KnockOff(Transform key, List<Renderer> parts, Transform head, Vector3 dir, Vector3 up, float now, float life, Cut cut)
    {
        var go = new GameObject("BloodFX_HeadGear");
        go.transform.SetParent(_root, false);
        Vector3 c = Vector3.zero;
        int n = 0;
        foreach (Renderer r in parts) if (r != null) { c += r.bounds.center; n++; }
        if (n == 0) { Object.Destroy(go); return; }
        c /= n;
        go.transform.SetPositionAndRotation(c, Quaternion.identity);
        var meshes = new List<Mesh>();
        float size = 0.05f;
        foreach (Renderer r in parts)
        {
            if (r == null) continue;
            Mesh mesh = null;
            Matrix4x4 m;
            if (r is SkinnedMeshRenderer smr)
            {
                mesh = new Mesh { name = "BloodFX_GearBake" };
                try { smr.BakeMesh(mesh, true); } catch { Object.Destroy(mesh); continue; }
                meshes.Add(mesh);
                m = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
            }
            else
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                mesh = mf.sharedMesh;
                m = r.transform.localToWorldMatrix;
            }
            var part = new GameObject("part");
            part.transform.SetParent(go.transform, false);
            // keep the part exactly where it was, relative to the item's centre
            part.transform.localPosition = m.MultiplyPoint3x4(Vector3.zero) - c;
            part.transform.localRotation = m.rotation;
            part.transform.localScale = m.lossyScale;
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = part.AddComponent<MeshRenderer>();
            mr.sharedMaterials = r.sharedMaterials;
            mr.shadowCastingMode = ShadowCastingMode.On;
            mr.lightProbeUsage = LightProbeUsage.BlendProbes;
            size = Mathf.Max(size, r.bounds.extents.magnitude);
            r.enabled = false;
            cut.Hidden.Add(r);
        }
        Vector3 away = c - head.position;
        away = away.sqrMagnitude > 1e-5f ? away.normalized : up;
        _shards.Add(new Shard
        {
            Go = go, Meshes = meshes, Born = now, Life = life, R = size,
            Vel = dir * Random.Range(2.5f, 5f) + away * Random.Range(1f, 2.5f) + up * Random.Range(1.5f, 3f),
            Spin = Random.onUnitSphere * Random.Range(6f, 14f)
        });
    }

    // ------------------------------------------------------------------ skin

    /// <summary>The posed triangles of these renderers inside the head, above the neck cut, in world space.</summary>
    private List<Tri> Gather(SkinnedMeshRenderer[] smrs, Dictionary<Transform, List<Renderer>> skip, Vector3 centre, Vector3 cutPt, Vector3 up)
    {
        const float radius = 0.15f;
        var tris = new List<Tri>();
        foreach (SkinnedMeshRenderer smr in smrs)
        {
            if (smr == null || !smr.gameObject.activeInHierarchy) continue;
            if (skip != null && IsGear(smr, skip)) continue;
            if (smr.bounds.SqrDistance(centre) > radius * radius) continue;
            try { smr.BakeMesh(_bake, true); }
            catch { continue; }
            _bake.GetVertices(_bv);
            _bake.GetNormals(_bn);
            _bake.GetUVs(0, _buv);
            if (_bv.Count == 0) continue;
            Transform t = smr.transform;
            var m = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
            for (int i = 0; i < _bv.Count; i++) _bv[i] = m.MultiplyPoint3x4(_bv[i]);
            for (int i = 0; i < _bn.Count; i++) _bn[i] = m.MultiplyVector(_bn[i]);
            Material[] mats = smr.sharedMaterials;
            for (int s = 0; s < _bake.subMeshCount; s++)
            {
                Material mat = mats.Length > 0 ? mats[Mathf.Min(s, mats.Length - 1)] : null;
                if (mat == null) continue;
                _bake.GetTriangles(_tri, s);
                for (int k = 0; k + 2 < _tri.Count; k += 3)
                {
                    int i0 = _tri[k], i1 = _tri[k + 1], i2 = _tri[k + 2];
                    Vector3 a = _bv[i0], b = _bv[i1], c = _bv[i2];
                    if (!Above(a, cutPt, up) || !Above(b, cutPt, up) || !Above(c, cutPt, up)) continue;
                    if ((a - centre).sqrMagnitude > radius * radius || (b - centre).sqrMagnitude > radius * radius ||
                        (c - centre).sqrMagnitude > radius * radius) continue;
                    tris.Add(new Tri
                    {
                        A = a, B = b, C = c,
                        Na = i0 < _bn.Count ? _bn[i0] : up, Nb = i1 < _bn.Count ? _bn[i1] : up, Nc = i2 < _bn.Count ? _bn[i2] : up,
                        Ua = i0 < _buv.Count ? _buv[i0] : Vector2.zero, Ub = i1 < _buv.Count ? _buv[i1] : Vector2.zero,
                        Uc = i2 < _buv.Count ? _buv[i2] : Vector2.zero, M = mat
                    });
                }
            }
        }
        return tris;
    }

    private static bool IsGear(Renderer r, Dictionary<Transform, List<Renderer>> gear)
    {
        foreach (var g in gear) if (g.Value.Contains(r)) return true;
        return false;
    }

    private static bool Above(Vector3 p, Vector3 cut, Vector3 up) => Vector3.Dot(p - cut, up) > -0.01f;

    /// <summary>Breaks triangles into jagged shards that fly away from the head.</summary>
    private void Shatter(List<Tri> tris, Vector3 centre, Vector3 dir, Vector3 up, float light, float life, float now)
    {
        if (tris.Count < 12) return;
        // jagged break lines: each triangle joins the nearest of a few seeds, nudged by noise
        int k0 = Mathf.Min(ShardsPerHead, tris.Count / 6);
        var seeds = new Vector3[k0];
        for (int i = 0; i < k0; i++)
        {
            Tri t = tris[Random.Range(0, tris.Count)];
            seeds[i] = (t.A + t.B + t.C) / 3f;
        }
        var groups = new List<Tri>[k0];
        for (int i = 0; i < k0; i++) groups[i] = new List<Tri>();
        float ns = Random.value * 50f;
        foreach (Tri t in tris)
        {
            Vector3 g = (t.A + t.B + t.C) / 3f;
            int best = 0;
            float bd = float.MaxValue;
            for (int s = 0; s < k0; s++)
            {
                float d = (g - seeds[s]).sqrMagnitude * (1f + 0.6f * SurfaceNets.Noise(g * 40f + Vector3.one * s, ns));
                if (d < bd) { bd = d; best = s; }
            }
            groups[best].Add(t);
        }
        foreach (List<Tri> grp in groups)
        {
            if (grp.Count < 3) continue;
            Vector3 c = Vector3.zero;
            foreach (Tri t in grp) c += (t.A + t.B + t.C) / 3f;
            c /= grp.Count;
            Mesh mesh = SkinMesh(grp, c, Quaternion.identity, light, out Material[] mats);
            var go = new GameObject("BloodFX_HeadShard");
            go.transform.SetParent(_root, false);
            go.transform.SetPositionAndRotation(c, Quaternion.identity);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = mats;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.lightProbeUsage = LightProbeUsage.BlendProbes;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            Vector3 outward = c - centre;
            outward = outward.sqrMagnitude > 1e-5f ? outward.normalized : Random.onUnitSphere;
            _shards.Add(new Shard
            {
                Go = go, Meshes = new List<Mesh> { mesh }, Born = now, Life = life, R = Mathf.Max(0.01f, mesh.bounds.extents.magnitude),
                Vel = dir * Random.Range(2f, 4.5f) + outward * Random.Range(2f, 5f) + up * Random.Range(0.5f, 2f),
                Spin = Random.onUnitSphere * Random.Range(8f, 20f)
            });
        }
        while (_shards.Count > MaxShards) Kill(0);
    }

    /// <summary>
    /// A mesh of skin triangles relative to <paramref name="origin"/> (in the frame <paramref name="frame"/>),
    /// one submesh per skin material plus a raw meat underside a few millimetres in.
    /// </summary>
    private Mesh SkinMesh(List<Tri> grp, Vector3 origin, Quaternion frame, float light, out Material[] materials)
    {
        Quaternion inv = Quaternion.Inverse(frame);
        var mats = new List<Material>();
        var bySub = new List<List<int>>();
        var v = new List<Vector3>();
        var n = new List<Vector3>();
        var uv = new List<Vector2>();
        var col = new List<Color32>();
        var under = new List<int>();
        Color32 white = Color.white;
        foreach (Tri t in grp)
        {
            int sub = mats.IndexOf(t.M);
            if (sub < 0) { sub = mats.Count; mats.Add(t.M); bySub.Add(new List<int>()); }
            int b0 = v.Count;
            v.Add(inv * (t.A - origin)); v.Add(inv * (t.B - origin)); v.Add(inv * (t.C - origin));
            n.Add(inv * t.Na); n.Add(inv * t.Nb); n.Add(inv * t.Nc);
            uv.Add(t.Ua); uv.Add(t.Ub); uv.Add(t.Uc);
            col.Add(white); col.Add(white); col.Add(white);
            bySub[sub].Add(b0); bySub[sub].Add(b0 + 1); bySub[sub].Add(b0 + 2);
            int u0 = v.Count;
            Vector3 fn = Vector3.Cross(t.B - t.A, t.C - t.A).normalized;
            v.Add(inv * (t.A - origin - fn * 0.004f)); v.Add(inv * (t.B - origin - fn * 0.004f)); v.Add(inv * (t.C - origin - fn * 0.004f));
            Vector3 nIn = inv * -fn;
            n.Add(nIn); n.Add(nIn); n.Add(nIn);
            uv.Add(Vector2.zero); uv.Add(Vector2.zero); uv.Add(Vector2.zero);
            for (int q = 0; q < 3; q++)
            {
                float lit = (0.45f + 0.4f * Mathf.Max(0f, Vector3.Dot(-fn, KeyLight))) * light * Random.Range(0.8f, 1.1f);
                col.Add(Wet.Shade(Inner * lit, 1f, 0.15f));
            }
            under.Add(u0); under.Add(u0 + 2); under.Add(u0 + 1);
        }
        var mesh = new Mesh { name = "BloodFX_HeadSkin" };
        mesh.indexFormat = v.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.SetVertices(v);
        mesh.SetNormals(n);
        mesh.SetUVs(0, uv);
        mesh.SetColors(col);
        mesh.subMeshCount = mats.Count + 1;
        for (int q = 0; q < mats.Count; q++) mesh.SetTriangles(bySub[q], q);
        mesh.SetTriangles(under, mats.Count);
        mesh.RecalculateBounds();
        mats.Add(_flesh);
        materials = mats.ToArray();
        return mesh;
    }

    /// <summary>What's left of the head stays on, as a baked copy riding the head bone.</summary>
    private void AddKeptFace(Transform head, List<Tri> kept, Cut cut)
    {
        if (kept.Count == 0) return;
        Mesh mesh = SkinMesh(kept, head.position, head.rotation, 1f, out Material[] mats);
        var go = new GameObject("BloodFX_KeptFace");
        go.transform.SetParent(head, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Unscale(head);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterials = mats;
        mr.shadowCastingMode = ShadowCastingMode.On;
        mr.lightProbeUsage = LightProbeUsage.BlendProbes;
        cut.Owned.Add(go);
        cut.Meshes.Add(mesh);
    }

    /// <summary>Local scale that cancels the head bone's (collapsed) scale, so a child stays full size.</summary>
    private static Vector3 Unscale(Transform t)
    {
        Vector3 ls = t.lossyScale;
        return new Vector3(1f / Mathf.Max(1e-6f, ls.x), 1f / Mathf.Max(1e-6f, ls.y), 1f / Mathf.Max(1e-6f, ls.z));
    }

    // ------------------------------------------------------------------ grown gore (distance fields)

    /// <summary>A distance-field mesh built on a worker thread, attached on the main thread once it's ready.</summary>
    private sealed class Job
    {
        public Task<Grown> Work;
        public Action<Grown> Attach;
    }

    private sealed class Grown
    {
        public readonly List<Vector3> V = new List<Vector3>(), N = new List<Vector3>();
        public readonly List<int> T = new List<int>();
        public Color32[] C;
    }

    private readonly List<Job> _jobs = new List<Job>();
    private static bool _jobErrorLogged;

    /// <summary>
    /// The face's own frame, world space: origin at the skull centre, x toward the wounded side of
    /// the face, y up the head, z out of the face. The wound is designed in these coordinates.
    /// </summary>
    private struct Face
    {
        public Vector3 Centre, Right, Up, Fwd;
        public bool Mirrored;
        public Vector3 Local(Vector3 w) { Vector3 d = w - Centre; return new Vector3(Vector3.Dot(d, Right), Vector3.Dot(d, Up), Vector3.Dot(d, Fwd)); }
        public Vector3 World(Vector3 l) => Centre + WorldDir(l);
        public Vector3 WorldDir(Vector3 l) => Right * l.x + Up * l.y + Fwd * l.z;
    }

    private static Face FaceFrame(Player victim, Transform head, Vector3 centre, Vector3 up, Vector3 dir)
    {
        Vector3 look = Vector3.zero;
        try { look = victim.LookDirection; } catch { }
        Vector3 fwd = Vector3.ProjectOnPlane(look, up);
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(head.forward, up);
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(Vector3.forward, up);
        fwd.Normalize();
        // the head bone's axes follow no convention; the one pointing most along the look is where the face is
        float bd = 0.5f;
        foreach (Vector3 ax in new[] { head.forward, -head.forward, head.right, -head.right, head.up, -head.up })
        {
            Vector3 a = Vector3.ProjectOnPlane(ax, up);
            if (a.sqrMagnitude < 1e-4f) continue;
            a.Normalize();
            float d = Vector3.Dot(a, fwd);
            if (d > bd) { bd = d; fwd = a; }
        }
        Vector3 right = Vector3.Cross(up, fwd).normalized;
        bool mirrored = Vector3.Dot(dir, right) < -0.3f; // the round came out the left side
        if (mirrored) right = -right;
        return new Face { Centre = centre, Right = right, Up = up, Fwd = fwd, Mirrored = mirrored };
    }

    /// <summary>Negative where the face is torn out: one cheek, eye and jaw, front of the head, brow to chin, ragged edge.</summary>
    private static float FaceRegion(Vector3 p, float seed)
    {
        return SurfaceNets.RoundBox(p, new Vector3(0.042f, -0.03f, 0.058f), new Vector3(0.04f, 0.05f, 0.05f), 0.012f)
               - 0.012f * SurfaceNets.Noise(p * 32f, seed);
    }

    /// <summary>
    /// The wound where the face was: the skull under it with the eye socket, cheekbone and both jaws showing,
    /// rows of teeth (a few knocked out, a few loose in the meat), the tongue behind the lower teeth, raw
    /// meat and clotted pockets over the rest, peeled skin flaps along the rim and a wet lip over the join
    /// with the skin that's left. Solid, so the hollow head is never seen through it.
    /// </summary>
    private void AddFaceWound(Transform head, Face face, float seed, float light, Cut cut)
    {
        // everything random or from Unity is taken here; the worker only does arithmetic
        Vector3 skull = new Vector3(0f, 0f, -0.008f);
        var radii = new Vector3(0.066f, 0.084f, 0.08f);
        Quaternion none = Quaternion.identity;
        // z of the skull surface at (x, y), used to sit things on it
        float Surface(float x, float y)
        {
            float k = 1f - (x * x) / (radii.x * radii.x) - (y * y) / (radii.y * radii.y);
            return skull.z + radii.z * Mathf.Sqrt(Mathf.Max(0.05f, k));
        }
        var lumps = new List<(Vector3 c, float r)>();
        for (int i = 0; i < 9; i++)
        {
            float x = Random.Range(0.012f, 0.078f), y = Random.Range(-0.085f, 0.022f);
            lumps.Add((new Vector3(x, y, Surface(x, y) - 0.003f), Random.Range(0.007f, 0.013f)));
        }
        // teeth along the two jaw arcs, hanging from the upper jaw and standing on the lower
        var teeth = new List<(Vector3 c, Quaternion inv, Vector3 r)>();
        for (int row = 0; row < 2; row++)
        {
            float y = row == 0 ? -0.0485f : -0.0595f;
            for (int i = 0; i < 7; i++)
            {
                if (Random.value < 0.2f) continue; // knocked out
                float a = (-12f + i * 16f + (row == 0 ? 0f : 8f)) * Mathf.Deg2Rad;
                var c = new Vector3(0.046f * Mathf.Sin(a), y, 0.022f + 0.046f * Mathf.Cos(a));
                float tilt = Random.value < 0.25f ? 35f : 12f;
                Quaternion q = Quaternion.Euler(Random.Range(-tilt, tilt), a * Mathf.Rad2Deg, Random.Range(-tilt, tilt));
                teeth.Add((c, Quaternion.Inverse(q), new Vector3(0.0035f, 0.0055f, 0.003f)));
            }
        }
        for (int i = 0; i < 3; i++) // loose, lying in the meat
        {
            float x = Random.Range(0.015f, 0.07f), y = Random.Range(-0.085f, -0.01f);
            teeth.Add((new Vector3(x, y, Surface(x, y) + 0.002f), Quaternion.Inverse(Random.rotation), new Vector3(0.0035f, 0.0055f, 0.003f)));
        }
        var shards = new List<(Vector3 c, Quaternion inv, Vector3 r)>(); // slivers of bone in the meat
        for (int i = 0; i < 4; i++)
        {
            float x = Random.Range(0.012f, 0.075f), y = Random.Range(-0.08f, 0.02f);
            shards.Add((new Vector3(x, y, Surface(x, y) + 0.001f), Quaternion.Inverse(Random.rotation), new Vector3(0.009f, 0.005f, 0.002f)));
        }
        // skin peeled back along the rim: thin sheets, their z axis the sheet's normal
        var flaps = new List<(Vector3 c, Quaternion inv, Vector3 r)>
        {
            (new Vector3(0.088f, -0.035f, 0.035f), Quaternion.Inverse(Quaternion.LookRotation(new Vector3(1f, 0f, -0.4f))), new Vector3(0.012f, 0.03f, 0.0035f)),
            (new Vector3(0.03f, -0.098f, 0.05f), Quaternion.Inverse(Quaternion.LookRotation(new Vector3(0f, -0.4f, 1f))), new Vector3(0.026f, 0.016f, 0.0035f)),
            (new Vector3(-0.012f, -0.04f, 0.074f), Quaternion.Inverse(Quaternion.LookRotation(new Vector3(-0.3f, 0f, 1f))), new Vector3(0.012f, 0.03f, 0.0035f)),
            (new Vector3(0.04f, 0.034f, 0.06f), Quaternion.Inverse(Quaternion.LookRotation(new Vector3(0f, 0.5f, 1f))), new Vector3(0.03f, 0.01f, 0.0035f)),
        };
        Vector3 socket = new Vector3(0.03f, 0.005f, 0.052f), nose = new Vector3(0.006f, -0.02f, 0.07f);
        Vector3 cheek = new Vector3(0.045f, -0.012f, 0.045f), cheekR = new Vector3(0.022f, 0.008f, 0.02f);
        Vector3 mouth = new Vector3(0.02f, -0.053f, 0.045f), mouthR = new Vector3(0.07f, 0.009f, 0.05f);
        Vector3 tongue = new Vector3(0f, -0.058f, 0.03f), tongueR = new Vector3(0.02f, 0.0055f, 0.03f);
        Quaternion tongueInv = Quaternion.Inverse(Quaternion.Euler(-12f, 0f, 0f));
        Vector3 lightLocal = face.Local(face.Centre + KeyLight);
        bool mirrored = face.Mirrored;
        Vector3 headPos = head.position;
        Quaternion headInv = Quaternion.Inverse(head.rotation);

        Grown Work()
        {
            float Region(Vector3 p) => FaceRegion(p, seed);
            float Shell(Vector3 p) => SurfaceNets.Ellipsoid(p, skull, none, radii);
            float Jaw(Vector3 p, float y, float r)
            {
                // a bar of bone along the tooth arc
                float d = float.MaxValue;
                for (int i = 0; i <= 7; i++)
                {
                    float a = (-20f + i * 17f) * Mathf.Deg2Rad;
                    d = Mathf.Min(d, SurfaceNets.Sphere(p, new Vector3(0.046f * Mathf.Sin(a), y, 0.022f + 0.046f * Mathf.Cos(a)), r));
                }
                return d;
            }
            float Teeth(Vector3 p)
            {
                float d = float.MaxValue;
                foreach (var t in teeth) d = Mathf.Min(d, SurfaceNets.Ellipsoid(p, t.c, t.inv, t.r));
                return d;
            }
            float Shards(Vector3 p)
            {
                float d = float.MaxValue;
                foreach (var s in shards) d = Mathf.Min(d, SurfaceNets.Ellipsoid(p, s.c, s.inv, s.r));
                return d;
            }
            float Flaps(Vector3 p)
            {
                float d = float.MaxValue;
                foreach (var f in flaps) d = Mathf.Min(d, SurfaceNets.Ellipsoid(p, f.c, f.inv, f.r));
                return d;
            }
            float Pockets(Vector3 p) => Mathf.Min(SurfaceNets.Sphere(p, socket, 0.017f), SurfaceNets.Sphere(p, nose, 0.011f));
            float Mouth(Vector3 p) => SurfaceNets.RoundBox(p, mouth, mouthR, 0.003f);
            float TongueD(Vector3 p) => SurfaceNets.Ellipsoid(p, tongue, tongueInv, tongueR);
            // the join with the skin: a band just outside the skull, either side of the ragged rim
            float Lip(Vector3 p) => Mathf.Max(Mathf.Abs(Region(p)) - 0.005f, Mathf.Abs(Shell(p) - 0.009f) - 0.004f);
            float Sdf(Vector3 p)
            {
                float d = Shell(p);                                                              // solid skull filling the hole
                foreach (var l in lumps) d = SurfaceNets.SmoothMin(d, SurfaceNets.Sphere(p, l.c, l.r), 0.006f);
                d = SurfaceNets.SmoothMin(d, SurfaceNets.Ellipsoid(p, cheek, none, cheekR), 0.005f);
                d = SurfaceNets.SmoothMin(d, Jaw(p, -0.04f, 0.01f), 0.005f);
                d = SurfaceNets.SmoothMin(d, Jaw(p, -0.066f, 0.011f), 0.005f);
                d = Mathf.Max(d, -Pockets(p));                                                   // eye socket, nasal cavity
                d = Mathf.Max(d, -Mouth(p));                                                     // the mouth, between the jaws
                d = Mathf.Min(d, Teeth(p));
                d = Mathf.Min(d, Shards(p));
                d = SurfaceNets.SmoothMin(d, TongueD(p), 0.003f);
                d = Mathf.Max(d, Region(p) + 0.002f);                                            // only inside the torn-out area
                d = Mathf.Min(d, Flaps(p));
                d = Mathf.Min(d, Lip(p));
                return d + 0.0015f * SurfaceNets.Noise(p * 160f, seed + 5f);                     // raw, uneven surface
            }
            var g = new Grown();
            SurfaceNets.Build(Sdf, new Vector3(-0.03f, -0.12f, -0.015f), 0.0035f, new Vector3Int(40, 50, 40), g.V, g.N, g.T);
            g.C = new Color32[g.V.Count];
            Vector3 half = (lightLocal + Vector3.up).normalized;
            for (int i = 0; i < g.V.Count; i++)
            {
                Vector3 p = g.V[i], n = g.N[i];
                Color c;
                float m = SurfaceNets.Noise(p * 110f, seed + 9f);
                if (Teeth(p) < 0.0015f) c = Ivory * (0.9f + 0.15f * m);
                else if (TongueD(p) < 0.0015f) c = Tongue * (0.9f + 0.2f * m);
                else if (Lip(p) < 0.0015f) c = m > 0.3f ? Inner : Hole;
                else if (Flaps(p) < 0.0015f) c = m > 0.25f ? Color.Lerp(Inner, Fat, (m - 0.25f) * 1.2f) : Inner * (0.85f + 0.3f * m);
                else if (Pockets(p) < 0.002f || Mouth(p) < 0.002f) c = m > 0.5f ? Inner : Hole;
                else if (Shards(p) < 0.0015f || Jaw(p, -0.04f, 0.01f) < 0.002f || Jaw(p, -0.066f, 0.011f) < 0.002f
                         || SurfaceNets.Ellipsoid(p, cheek, none, cheekR) < 0.002f) c = BoneCol * (0.9f + 0.15f * m);
                else if (Shell(p) > -0.006f && SurfaceNets.Noise(p * 60f, seed + 2f) > 0.35f) c = BoneCol; // bone through the meat
                else
                {
                    c = m > 0f ? Meat * (0.85f + 0.3f * m) : Color.Lerp(Inner, Meat, 1f + m);
                    if (m < -0.5f) c = Hole;                                                     // dark clotted pockets
                }
                float diff = 0.4f + 0.6f * Mathf.Max(0f, Vector3.Dot(n, lightLocal));
                float spec = Mathf.Pow(Mathf.Max(0f, Vector3.Dot(n, half)), 20f) * 0.6f;
                g.C[i] = Wet.Shade(c * light, diff, spec);
                // into the head bone's frame so it rides the ragdoll with the rest of the head
                g.V[i] = headInv * (face.World(p) - headPos);
                g.N[i] = headInv * face.WorldDir(n);
            }
            // a mirrored frame turns the triangles inside out
            if (mirrored) for (int i = 0; i + 2 < g.T.Count; i += 3) (g.T[i + 1], g.T[i + 2]) = (g.T[i + 2], g.T[i + 1]);
            return g;
        }

        Queue(Work, g =>
        {
            if (cut.Released || head == null) return;
            GameObject go = MeshObject("BloodFX_FaceWound", g, cut);
            go.transform.SetParent(head, false);
            go.transform.localScale = Unscale(head);
        });
    }

    private void AddStump(Transform head, Transform neck, Vector3 cutPt, Vector3 up, float light, Cut cut)
    {
        // frame at the cut: y along the neck, z toward the face
        Vector3 fwd = Vector3.ProjectOnPlane(head.forward, up);
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(Vector3.forward, up);
        Quaternion rot = Quaternion.LookRotation(fwd.normalized, up);
        // where it sits on the neck, taken now: the body keeps falling while the mesh is built
        Vector3 localPos = neck.InverseTransformPoint(cutPt);
        Quaternion localRot = Quaternion.Inverse(neck.rotation) * rot;

        float seed = Random.value * 100f;
        var lumps = new List<(Vector3 c, float r)>();
        int nl = Random.Range(6, 10);
        for (int i = 0; i < nl; i++)
        {
            float a = Random.value * 6.283f;
            lumps.Add((new Vector3(Mathf.Cos(a) * Random.Range(0.025f, 0.045f), Random.Range(-0.004f, 0.016f), Mathf.Sin(a) * Random.Range(0.025f, 0.045f)),
                Random.Range(0.011f, 0.02f)));
        }
        var flaps = new List<(Vector3 c, Quaternion inv, Vector3 r)>();
        int nf = Random.Range(2, 4);
        for (int i = 0; i < nf; i++)
        {
            float a = Random.value * 6.283f;
            Vector3 radial = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            // torn skin peeled outward and hanging down over the neck
            Quaternion q = Quaternion.LookRotation(radial, Vector3.up) * Quaternion.Euler(Random.Range(20f, 55f), 0f, 0f);
            flaps.Add((radial * 0.05f + Vector3.up * 0.002f, Quaternion.Inverse(q), new Vector3(Random.Range(0.016f, 0.026f), 0.0035f, Random.Range(0.022f, 0.034f))));
        }
        Vector3 spine = new Vector3(0f, 0f, -0.018f), pipe = new Vector3(0f, 0f, 0.024f);
        Vector3 lightLocal = Quaternion.Inverse(rot) * KeyLight;

        Grown Work()
        {
            float Sdf(Vector3 p)
            {
                // the neck itself, cut off with a torn, uneven top
                float top = 0.004f + 0.012f * SurfaceNets.Noise(new Vector3(p.x, 0f, p.z) * 55f, seed);
                float d = Mathf.Max(SurfaceNets.Cylinder(p, Vector3.zero, 0.05f, -0.045f, 0.03f), p.y - top);
                foreach (var l in lumps) d = SurfaceNets.SmoothMin(d, SurfaceNets.Sphere(p, l.c, l.r), 0.008f);
                foreach (var f in flaps) d = SurfaceNets.SmoothMin(d, SurfaceNets.Ellipsoid(p, f.c, f.inv, f.r), 0.006f);
                d = SurfaceNets.SmoothMin(d, SurfaceNets.Cylinder(p, spine, 0.009f, -0.02f, 0.026f), 0.004f);   // spine end sticking out
                d = Mathf.Max(d, -SurfaceNets.Cylinder(p, pipe, 0.0075f, -0.04f, 0.05f));                       // windpipe
                return d + 0.0025f * SurfaceNets.Noise(p * 160f, seed + 3f);                                     // raw, uneven surface
            }
            var g = new Grown();
            SurfaceNets.Build(Sdf, new Vector3(-0.085f, -0.05f, -0.085f), 0.0045f, new Vector3Int(39, 25, 39), g.V, g.N, g.T);
            g.C = new Color32[g.V.Count];
            Vector3 half = (lightLocal + Vector3.up).normalized;
            for (int i = 0; i < g.V.Count; i++)
            {
                Vector3 p = g.V[i], n = g.N[i];
                Color c;
                if (SurfaceNets.Cylinder(p, spine, 0.012f, -0.02f, 0.03f) < 0.002f) c = BoneCol;
                else if (SurfaceNets.Cylinder(p, pipe, 0.011f, -0.04f, 0.05f) < 0.001f) c = Hole;
                else
                {
                    float m = SurfaceNets.Noise(p * 120f, seed + 7f);
                    c = m > 0.45f ? Color.Lerp(Meat, Fat, (m - 0.45f) * 3f) : Meat * (0.8f + 0.35f * m);
                    if (p.y < -0.02f) c = Color.Lerp(c, Inner, 0.5f); // the lower part is soaked
                }
                float diff = 0.4f + 0.6f * Mathf.Max(0f, Vector3.Dot(n, lightLocal));
                float spec = Mathf.Pow(Mathf.Max(0f, Vector3.Dot(n, half)), 20f) * 0.6f;
                g.C[i] = Wet.Shade(c * light, diff, spec);
            }
            return g;
        }

        Queue(Work, g =>
        {
            if (cut.Released || neck == null) return;
            GameObject go = MeshObject("BloodFX_NeckStump", g, cut);
            go.transform.SetParent(neck, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot;
            go.transform.localScale = Unscale(neck);
        });
    }

    private void Queue(Func<Grown> work, Action<Grown> attach)
    {
        _jobs.Add(new Job { Work = Task.Run(work), Attach = attach });
    }

    /// <summary>Attaches the grown meshes whose workers have finished.</summary>
    private void FinishJobs()
    {
        for (int i = _jobs.Count - 1; i >= 0; i--)
        {
            Job j = _jobs[i];
            if (!j.Work.IsCompleted) continue;
            _jobs.RemoveAt(i);
            if (j.Work.IsFaulted || j.Work.IsCanceled)
            {
                if (!_jobErrorLogged) BloodFXPlugin.Log.LogWarning($"BloodFX: gore mesh build failed: {j.Work.Exception?.GetBaseException().Message}");
                _jobErrorLogged = true;
                continue;
            }
            if (j.Work.Result.T.Count > 0) j.Attach(j.Work.Result);
        }
    }

    private GameObject MeshObject(string name, Grown g, Cut cut)
    {
        var mesh = new Mesh { name = name };
        mesh.indexFormat = g.V.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.SetVertices(g.V);
        mesh.SetNormals(g.N);
        mesh.colors32 = g.C;
        mesh.SetTriangles(g.T, 0);
        mesh.RecalculateBounds();
        var go = new GameObject(name);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = _flesh;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        mr.lightProbeUsage = LightProbeUsage.Off;
        mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
        cut.Owned.Add(go);
        cut.Meshes.Add(mesh);
        return go;
    }

    // ------------------------------------------------------------------ per frame

    public void Tick(float now, float dt)
    {
        if (_jobs.Count > 0) FinishJobs();
        dt = Mathf.Min(dt, 0.05f);
        Vector3 g = Physics.gravity;
        for (int i = _shards.Count - 1; i >= 0; i--)
        {
            Shard s = _shards[i];
            if (s.Go == null) { _shards.RemoveAt(i); continue; }
            float left = s.Born + s.Life - now;
            if (left <= 0f) { Kill(i); continue; }
            Transform t = s.Go.transform;
            if (!s.Landed)
            {
                s.Vel += g * dt;
                s.Vel *= 1f - 0.4f * dt;
                Vector3 step = s.Vel * dt;
                float len = step.magnitude;
                if (len > 1e-5f && Physics.Raycast(t.position, step / len, out RaycastHit hit, len + s.R * 0.3f, _mask, QueryTriggerInteraction.Ignore))
                {
                    s.Landed = true;
                    // settles flat-ish on whatever it hit
                    t.position = hit.point + hit.normal * Mathf.Min(0.03f, s.R * 0.4f);
                    t.rotation = Quaternion.FromToRotation(t.up, hit.normal) * t.rotation;
                    Landed?.Invoke(hit.point, hit.normal, s.Vel, Mathf.Min(0.02f, s.R * 0.5f));
                    s.Vel = Vector3.zero;
                }
                else
                {
                    t.position += step;
                    t.rotation = Quaternion.AngleAxis(s.Spin.magnitude * dt * Mathf.Rad2Deg, s.Spin) * t.rotation;
                    if (now - s.Born > 6f) { Kill(i); continue; } // fell out of the world
                }
            }
            if (left < FadeTime) t.localScale = Vector3.one * Mathf.Max(0.05f, left / FadeTime);
        }
    }

    /// <summary>Animation or other mods may rewrite bone scale or re-enable renderers; keep what's gone gone.</summary>
    public void LateTick()
    {
        for (int i = _cuts.Count - 1; i >= 0; i--)
        {
            Cut c = _cuts[i];
            if (c.Head == null) { Release(c); _cuts.RemoveAt(i); continue; }
            if (c.Collapse && c.Head.localScale.x > CollapsedScale * 2f) c.Head.localScale = Vector3.one * CollapsedScale;
            foreach (Renderer r in c.Hidden) if (r != null && r.enabled) r.enabled = false;
        }
    }

    public void Clear()
    {
        for (int i = _shards.Count - 1; i >= 0; i--) Kill(i);
        foreach (Cut c in _cuts) Release(c);
        _cuts.Clear();
        _jobs.Clear(); // whatever they finish is dropped
        _done.Clear();
    }

    private static void Release(Cut c)
    {
        c.Released = true;
        foreach (Mesh m in c.Meshes) if (m != null) Object.Destroy(m);
        foreach (GameObject go in c.Owned) if (go != null) Object.Destroy(go);
    }

    private void Kill(int i)
    {
        Shard s = _shards[i];
        if (s.Meshes != null) foreach (Mesh m in s.Meshes) if (m != null) Object.Destroy(m);
        if (s.Go != null) Object.Destroy(s.Go);
        _shards.RemoveAt(i);
    }
}
