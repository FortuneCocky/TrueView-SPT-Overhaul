using System.Collections.Generic;
using EFT;
using UnityEngine;
using UnityEngine.Rendering;

namespace BloodFX;

/// <summary>
/// Wound and armor marks built from the character's own mesh, so they wrap over
/// plates, pouches, folds and skin instead of floating in front of them.
///
/// On a hit, the character's renderers are posed (BakeMesh) and the bullet's line
/// is traced against their triangles to find the outermost surface it went into.
/// The triangles around that point are clipped to the mark's square, the texture
/// is projected onto them, and the patch is lifted a couple of millimetres.
/// When the source mesh is readable, the patch copies its bone weights and is
/// drawn as its own SkinnedMeshRenderer on the same skeleton, so it bends exactly
/// with the body. When it isn't, the patch is frozen in the pose of the hit and
/// rides rigidly on the nearest bone.
///
/// Building a patch costs a few milliseconds, so it is throttled; a hit that
/// can't get one falls back to the floating crater marks.
/// </summary>
internal sealed class SkinnedMarks
{
    private sealed class Mark
    {
        public GameObject Go;
        public Mesh Mesh;
        public Color32[] Cols;
        public Color32 Col;
        public float Born, Life;
    }

    private struct CV
    {
        public float X, Y;
        public Vector3 P, N;
        public BoneWeight W;
    }

    private const float Lift = 0.0025f, FadeTime = 8f, MinGap = 0.06f;
    private const int MaxTris = 600;

    private readonly Material _mat;
    private readonly Mark[] _marks;
    private readonly Mesh _bake;
    private readonly Dictionary<int, SkinnedMeshRenderer[]> _renderers = new Dictionary<int, SkinnedMeshRenderer[]>();
    private readonly List<Vector3> _bv = new List<Vector3>(), _bn = new List<Vector3>();
    private readonly List<Vector3> _sv = new List<Vector3>(), _sn = new List<Vector3>();
    private readonly List<BoneWeight> _sw = new List<BoneWeight>();
    private readonly List<int> _tris = new List<int>();
    private readonly List<Vector3> _oV = new List<Vector3>(), _oN = new List<Vector3>();
    private readonly List<Vector2> _oUV = new List<Vector2>();
    private readonly List<BoneWeight> _oW = new List<BoneWeight>();
    private readonly List<int> _oT = new List<int>();
    private CV[] _polyA = new CV[16], _polyB = new CV[16]; // a triangle clipped by 4 sides has at most 7 corners
    private int _next, _logged;
    private bool _failed;
    private float _nextAllowed, _nextTick;

    public int Built, Skinned, Rigid, Missed;

    // posed copies of a character's renderers, refreshed a few times a second, for surface queries
    private sealed class BakeCache
    {
        public Vector3[] V = new Vector3[0];
        public int[] T = new int[0];
        public int Count;
        public float At = -1f;
    }
    private readonly Dictionary<int, BakeCache> _bakes = new Dictionary<int, BakeCache>();
    private float _nextSurface;

    /// <summary>
    /// How far out from a point on a hit capsule the character's visible surface really is, along
    /// <paramref name="n"/> (clothing and gear sit outside the capsules). -1 when it can't be measured
    /// right now (throttled, nothing baked, no mesh there).
    /// </summary>
    public float SurfaceOffset(Player victim, Vector3 p, Vector3 n, float maxDist, float now)
    {
        if (victim == null || now < _nextSurface) return -1f;
        _nextSurface = now + 0.04f;
        try
        {
            SkinnedMeshRenderer[] smrs = Renderers(victim);
            Vector3 origin = p + n * maxDist;
            Vector3 dir = -n;
            var ray = new Ray(origin, dir);
            float best = -1f;
            float reach2 = (maxDist + 0.05f) * (maxDist + 0.05f);
            for (int r = 0; r < smrs.Length; r++)
            {
                SkinnedMeshRenderer smr = smrs[r];
                if (smr == null || !smr.enabled || !smr.gameObject.activeInHierarchy || smr.sharedMesh == null || !smr.isVisible) continue;
                Bounds b = smr.bounds;
                b.Expand(0.05f);
                if (!b.IntersectRay(ray)) continue;
                BakeCache c = Cached(smr, now);
                if (c == null) continue;
                int[] tri = c.T;
                for (int t = 0; t + 2 < tri.Length; t += 3)
                {
                    Vector3 a = c.V[tri[t]], bb = c.V[tri[t + 1]], d = c.V[tri[t + 2]];
                    if ((a - p).sqrMagnitude > reach2 && (bb - p).sqrMagnitude > reach2 && (d - p).sqrMagnitude > reach2) continue;
                    if (RayTri(origin, dir, a, bb, d, out float tt) && tt <= maxDist + 0.01f && (best < 0f || tt < best)) best = tt;
                }
            }
            if (best < 0f) return -1f;
            return Mathf.Clamp(maxDist - best, 0.003f, maxDist);
        }
        catch { return -1f; }
    }

    private BakeCache Cached(SkinnedMeshRenderer smr, float now)
    {
        int id = smr.GetInstanceID();
        if (!_bakes.TryGetValue(id, out BakeCache c))
        {
            if (_bakes.Count > 512) _bakes.Clear();
            c = new BakeCache();
            _bakes[id] = c;
        }
        if (c.Count > 0 && now - c.At < 0.33f) return c;
        if (!Bake(smr)) return null;
        if (c.V.Length < _bv.Count) c.V = new Vector3[_bv.Count];
        _bv.CopyTo(c.V);
        c.Count = _bv.Count;
        if (c.T.Length == 0) c.T = _bake.triangles;
        c.At = now;
        return c;
    }
    /// <summary>True after a TryAdd that found no body surface anywhere near the hit (a graze past the visible mesh), so no floating mark should be placed either.</summary>
    public bool LastMiss { get; private set; }

    public SkinnedMarks(Material mat, int capacity)
    {
        _mat = mat;
        _marks = new Mark[Mathf.Clamp(capacity, 4, 256)];
        _bake = new Mesh { name = "BloodFX_Bake" };
        _bake.MarkDynamic();
    }

    public void Clear()
    {
        for (int i = 0; i < _marks.Length; i++) Kill(i);
        _renderers.Clear();
    }

    /// <summary>Tries to build a conforming mark; false = caller should use the floating mark instead.</summary>
    /// <param name="dir">Bullet travel direction; the mark goes on the first surface along it.</param>
    /// <param name="runLen">Trickle length below the mark (0 = none).</param>
    public bool TryAdd(Player victim, Transform bone, Vector3 pt, Vector3 dir, float size, int cell, Color32 col, float life,
        float runLen, float runWidth, int runCell, float now)
    {
        LastMiss = false;
        try { return Build(victim, bone, pt, dir, size, cell, col, life, runLen, runWidth, runCell, now); }
        catch (System.Exception e)
        {
            if (!_failed) BloodFXPlugin.Log.LogWarning($"BloodFX: conforming mark failed, using floating marks for this hit: {e.Message}");
            _failed = true;
            Missed++;
            return false;
        }
    }

    private bool Build(Player victim, Transform bone, Vector3 pt, Vector3 dir, float size, int cell, Color32 col, float life,
        float runLen, float runWidth, int runCell, float now)
    {
        if (victim == null || bone == null || now < _nextAllowed) return false;
        _nextAllowed = now + MinGap;

        SkinnedMeshRenderer[] smrs = Renderers(victim);
        Vector3 origin = pt - dir * 0.35f;
        var ray = new Ray(origin, dir);
        float bestT = 0.7f;
        int bestR = -1;
        Vector3 bestN = Vector3.zero;
        float facing = 1f;
        for (int r = 0; r < smrs.Length; r++)
        {
            SkinnedMeshRenderer smr = smrs[r];
            if (smr == null || !smr.enabled || !smr.gameObject.activeInHierarchy || smr.sharedMesh == null || !smr.isVisible) continue;
            Bounds b = smr.bounds;
            b.Expand(0.05f);
            if (!b.IntersectRay(ray, out float enter) || enter > bestT) continue;
            if (!Bake(smr)) continue;
            bool hitHere = false;
            int subs = _bake.subMeshCount;
            for (int s = 0; s < subs; s++)
            {
                _bake.GetTriangles(_tris, s);
                for (int t = 0; t + 2 < _tris.Count; t += 3)
                {
                    Vector3 a = _bv[_tris[t]], bb = _bv[_tris[t + 1]], c = _bv[_tris[t + 2]];
                    if (RayTri(origin, dir, a, bb, c, out float tt) && tt < bestT)
                    {
                        bestT = tt;
                        bestN = Vector3.Cross(bb - a, c - a).normalized;
                        hitHere = true;
                    }
                }
            }
            if (hitHere) bestR = r;
        }
        // a graze: the round clipped the hit capsule outside the visible mesh, so the bullet line meets no surface.
        // The wound goes on the nearest bit of skin or clothing instead of hanging in the air beside the limb.
        int grazeV = -1;
        if (bestR < 0)
        {
            float bestD = 0.12f * 0.12f;
            for (int r = 0; r < smrs.Length; r++)
            {
                SkinnedMeshRenderer smr = smrs[r];
                if (smr == null || !smr.enabled || !smr.gameObject.activeInHierarchy || smr.sharedMesh == null || !smr.isVisible) continue;
                Bounds b = smr.bounds;
                b.Expand(0.12f);
                if (!b.Contains(pt) || !Bake(smr)) continue;
                for (int i = 0; i < _bv.Count; i++)
                {
                    float d = (_bv[i] - pt).sqrMagnitude;
                    if (d < bestD) { bestD = d; grazeV = i; bestR = r; }
                }
            }
            if (bestR < 0) { Missed++; LastMiss = true; return false; }
        }

        SkinnedMeshRenderer src = smrs[bestR];
        if (!Bake(src)) { Missed++; LastMiss = true; return false; }
        Vector3 centre;
        if (grazeV >= 0)
        {
            centre = _bv[grazeV];
            Vector3 nOut = grazeV < _bn.Count ? _bn[grazeV] : Vector3.zero;
            if (nOut.sqrMagnitude < 1e-6f) nOut = pt - bone.position;
            if (nOut.sqrMagnitude < 1e-6f) nOut = -dir;
            bestN = nOut.normalized;
            dir = -bestN;      // project straight onto the surface, keeping its outward-facing triangles
            facing = -1f;
        }
        else
        {
            centre = origin + dir * bestT;
            // winding of the front faces relative to the bullet, so only same-side triangles get the mark
            facing = Vector3.Dot(bestN, dir) > 0f ? 1f : -1f;
            if (facing > 0f) bestN = -bestN; // the mark faces back toward the shooter
        }
        Mesh shared = src.sharedMesh;
        bool skinned = shared.isReadable && shared.vertexCount == _bv.Count && src.bones != null && src.bones.Length > 0;

        // source attributes the patch is built from: bind pose (skinned) or bone-local pose (rigid)
        _sv.Clear(); _sn.Clear(); _sw.Clear();
        if (skinned)
        {
            shared.GetVertices(_sv);
            shared.GetNormals(_sn);
            shared.GetBoneWeights(_sw);
            if (_sn.Count != _sv.Count || _sw.Count != _sv.Count) skinned = false;
        }
        if (!skinned)
        {
            _sv.Clear(); _sn.Clear(); _sw.Clear();
            for (int i = 0; i < _bv.Count; i++)
            {
                _sv.Add(bone.InverseTransformPoint(_bv[i]));
                _sn.Add(i < _bn.Count ? bone.InverseTransformDirection(_bn[i]) : Vector3.zero);
            }
        }

        Vector3 right = Vector3.Cross(Vector3.up, bestN);
        if (right.sqrMagnitude < 1e-4f) right = Vector3.Cross(Vector3.forward, bestN);
        right.Normalize();
        Vector3 up = Vector3.Cross(bestN, right);
        bool flip = Random.value < 0.5f;

        _oV.Clear(); _oN.Clear(); _oUV.Clear(); _oW.Clear(); _oT.Clear();
        float half = size * 0.5f;
        Project(centre, bestN, facing, dir, right, up, half, half, BloodTextures.BodyCell(cell), flip);
        if (runLen > 0f)
        {
            float hl = runLen * 0.5f;
            Project(centre - up * (half * 0.3f + hl), bestN, facing, dir, right, up, runWidth * 0.5f, hl, BloodTextures.BodyCell(runCell), false);
        }
        if (_oT.Count == 0) { Missed++; LastMiss = true; return false; }

        // lift off the surface so it never z-fights
        for (int i = 0; i < _oV.Count; i++) _oV[i] += _oN[i] * Lift;

        var mesh = new Mesh { name = "BloodFX_Mark" };
        mesh.SetVertices(_oV);
        mesh.SetUVs(0, _oUV);
        var cols = new Color32[_oV.Count];
        for (int i = 0; i < cols.Length; i++) cols[i] = col;
        mesh.colors32 = cols;
        mesh.SetTriangles(_oT, 0);

        int slot = _next;
        _next = (_next + 1) % _marks.Length;
        Kill(slot);
        GameObject go;
        if (skinned)
        {
            mesh.boneWeights = _oW.ToArray();
            mesh.bindposes = shared.bindposes;
            go = new GameObject("BloodFX_Mark");
            go.transform.SetParent(src.transform.parent, false);
            var r = go.AddComponent<SkinnedMeshRenderer>();
            r.bones = src.bones;
            r.rootBone = src.rootBone;
            r.sharedMesh = mesh;
            r.localBounds = src.localBounds;
            Setup(r);
            Skinned++;
        }
        else
        {
            mesh.RecalculateBounds();
            go = new GameObject("BloodFX_Mark");
            go.transform.SetParent(bone, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            Setup(go.AddComponent<MeshRenderer>());
            Rigid++;
        }
        Built++;
        if (_logged < 3)
        {
            _logged++;
            BloodFXPlugin.Log.LogInfo($"BloodFX: conforming mark on '{src.name}' ({(skinned ? "skinned, follows the mesh" : "rigid, mesh not readable")}), {_oT.Count / 3} triangles");
        }
        _marks[slot] = new Mark { Go = go, Mesh = mesh, Cols = cols, Col = col, Born = now, Life = life };
        return true;
    }

    public void Tick(float now)
    {
        if (now < _nextTick) return;
        _nextTick = now + 0.25f;
        for (int i = 0; i < _marks.Length; i++)
        {
            Mark m = _marks[i];
            if (m == null) continue;
            float left = m.Born + m.Life - now;
            if (m.Go == null || left <= 0f) { Kill(i); continue; } // body despawned or mark expired
            if (left < FadeTime)
            {
                Color32 c = m.Col;
                c.a = (byte)(c.a * (left / FadeTime));
                for (int k = 0; k < m.Cols.Length; k++) m.Cols[k] = c;
                m.Mesh.colors32 = m.Cols;
            }
        }
    }

    private void Setup(Renderer r)
    {
        r.sharedMaterial = _mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.lightProbeUsage = LightProbeUsage.Off;
        r.reflectionProbeUsage = ReflectionProbeUsage.Off;
    }

    private void Kill(int i)
    {
        Mark m = _marks[i];
        if (m == null) return;
        if (m.Go != null) Object.Destroy(m.Go);
        if (m.Mesh != null) Object.Destroy(m.Mesh);
        _marks[i] = null;
    }

    private SkinnedMeshRenderer[] Renderers(Player victim)
    {
        int id = victim.GetInstanceID();
        if (!_renderers.TryGetValue(id, out SkinnedMeshRenderer[] list) || list.Length == 0 || list[0] == null)
        {
            if (_renderers.Count > 256) _renderers.Clear();
            list = victim.GetComponentsInChildren<SkinnedMeshRenderer>(false);
            _renderers[id] = list;
        }
        return list;
    }

    /// <summary>Poses the renderer's mesh into _bv (world positions) and _bn (world normals).</summary>
    private bool Bake(SkinnedMeshRenderer smr)
    {
        try { smr.BakeMesh(_bake, true); }
        catch { return false; }
        _bake.GetVertices(_bv);
        _bake.GetNormals(_bn);
        Transform t = smr.transform;
        var m = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
        for (int i = 0; i < _bv.Count; i++) _bv[i] = m.MultiplyPoint3x4(_bv[i]);
        for (int i = 0; i < _bn.Count; i++) _bn[i] = m.MultiplyVector(_bn[i]);
        return _bv.Count > 0;
    }

    /// <summary>Clips the posed triangles near <paramref name="c"/> to a rectangle on the surface and appends them, textured.</summary>
    private void Project(Vector3 c, Vector3 n, float facing, Vector3 dir, Vector3 right, Vector3 up, float hx, float hy, Rect uv, bool flip)
    {
        float reach = Mathf.Max(hx, hy) * 1.5f, reachSq = reach * reach;
        float slab = Mathf.Max(hx, hy) * 0.8f;
        int subs = _bake.subMeshCount;
        for (int s = 0; s < subs && _oT.Count < MaxTris * 3; s++)
        {
            _bake.GetTriangles(_tris, s);
            for (int t = 0; t + 2 < _tris.Count && _oT.Count < MaxTris * 3; t += 3)
            {
                int i0 = _tris[t], i1 = _tris[t + 1], i2 = _tris[t + 2];
                Vector3 a = _bv[i0], b = _bv[i1], d = _bv[i2];
                if ((a - c).sqrMagnitude > reachSq && (b - c).sqrMagnitude > reachSq && (d - c).sqrMagnitude > reachSq) continue;
                Vector3 tn = Vector3.Cross(b - a, d - a);
                if (tn.sqrMagnitude < 1e-12f) continue;
                tn.Normalize();
                if (Vector3.Dot(tn, dir) * facing <= 0f) continue;   // facing away: inside of a shell or far side
                if (Mathf.Abs(Vector3.Dot(tn, n)) < 0.25f) continue; // edge-on: the texture would smear
                if (Mathf.Abs(Vector3.Dot(a - c, n)) > slab && Mathf.Abs(Vector3.Dot(b - c, n)) > slab) continue; // other side of the limb
                int cnt = 3;
                _polyA[0] = Vert(i0, a, c, right, up, hx, hy);
                _polyA[1] = Vert(i1, b, c, right, up, hx, hy);
                _polyA[2] = Vert(i2, d, c, right, up, hx, hy);
                cnt = Clip(cnt, 0, -1f); if (cnt < 3) continue;
                cnt = Clip(cnt, 0, 1f); if (cnt < 3) continue;
                cnt = Clip(cnt, 1, -1f); if (cnt < 3) continue;
                cnt = Clip(cnt, 1, 1f); if (cnt < 3) continue;
                int baseIdx = _oV.Count;
                for (int k = 0; k < cnt; k++)
                {
                    CV v = _polyA[k];
                    _oV.Add(v.P);
                    _oN.Add(v.N.sqrMagnitude > 1e-6f ? v.N.normalized : Vector3.zero);
                    _oW.Add(v.W);
                    float x = flip ? -v.X : v.X;
                    _oUV.Add(new Vector2(uv.center.x + x * uv.width * 0.5f, uv.center.y + v.Y * uv.height * 0.5f));
                }
                for (int k = 1; k + 1 < cnt; k++)
                {
                    _oT.Add(baseIdx); _oT.Add(baseIdx + k); _oT.Add(baseIdx + k + 1);
                }
            }
        }
    }

    private CV Vert(int i, Vector3 world, Vector3 c, Vector3 right, Vector3 up, float hx, float hy)
    {
        Vector3 d = world - c;
        return new CV
        {
            X = Vector3.Dot(d, right) / hx,
            Y = Vector3.Dot(d, up) / hy,
            P = _sv[i],
            N = i < _sn.Count ? _sn[i] : Vector3.zero,
            W = i < _sw.Count ? _sw[i] : default
        };
    }

    /// <summary>Sutherland–Hodgman against one side of the unit square (axis 0 = x, 1 = y; side ±1). Result back in _polyA.</summary>
    private int Clip(int cnt, int axis, float side)
    {
        int o = 0;
        for (int k = 0; k < cnt; k++)
        {
            CV a = _polyA[k], b = _polyA[(k + 1) % cnt];
            // inside means coord * side <= 1
            float ca = (axis == 0 ? a.X : a.Y) * side, cb = (axis == 0 ? b.X : b.Y) * side;
            bool ina = ca <= 1f, inb = cb <= 1f;
            if (ina) _polyB[o++] = a;
            if (ina != inb)
            {
                float t = (1f - ca) / (cb - ca);
                _polyB[o++] = new CV
                {
                    X = Mathf.Lerp(a.X, b.X, t),
                    Y = Mathf.Lerp(a.Y, b.Y, t),
                    P = Vector3.Lerp(a.P, b.P, t),
                    N = Vector3.Lerp(a.N, b.N, t),
                    W = t < 0.5f ? a.W : b.W
                };
            }
        }
        CV[] tmp = _polyA; _polyA = _polyB; _polyB = tmp;
        return o;
    }

    private static bool RayTri(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0f;
        Vector3 e1 = b - a, e2 = c - a, p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (det > -1e-8f && det < 1e-8f) return false;
        float inv = 1f / det;
        Vector3 s = o - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) return false;
        Vector3 q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, q) * inv;
        if (v < 0f || u + v > 1f) return false;
        t = Vector3.Dot(e2, q) * inv;
        return t > 0f;
    }
}
