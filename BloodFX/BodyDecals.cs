using UnityEngine;
using UnityEngine.Rendering;

namespace BloodFX;

/// <summary>
/// Marks stuck to bodies: bullet wounds with a trickle that runs down over a few
/// seconds, and armor impact dents. Each mark is a small crater mesh (sunken hole,
/// raised torn or crushed rim) with the texture mapped over it and the relief
/// shaded into vertex colours, so it has real depth. The trickle is a raised
/// ribbon. Marks are stored in the hit bone's local space and re-placed every
/// frame, so they ride along with animation and ragdoll.
///
/// Hit colliders sit inside the visible clothing and gear, so a mark placed on the
/// collider would be buried. Each frame the mark is pulled a few centimetres toward
/// the viewer, which puts it on top of whatever is actually drawn there.
/// All marks share one mesh: one draw call.
/// </summary>
internal sealed class BodyDecals
{
    private struct Entry
    {
        public Transform Bone;
        public Vector3 LPos, LRight, LUp;
        public float Size, Depth, Pull, RunWidth, RunLen, RunGrow, Born, Life;
        public int RunCell, Profile;
        public Color32 Col;
        public bool Live, Run, Flip;
    }

    public const int ProfileWound = 0, ProfileArmor = 1;

    private const int Seg = 14, Rings = 4, RunRows = 8;
    private const int CraterV = 1 + Seg * Rings, RunV = RunRows * 3, Per = CraterV + RunV;
    private const float FadeTime = 8f;
    private static readonly float[] Cos = new float[Seg], Sin = new float[Seg];
    // ring radius (fraction of half-size) and height (fraction of depth) per profile
    private static readonly float[][] RingR = { new[] { 0.24f, 0.42f, 0.66f, 1f }, new[] { 0.17f, 0.3f, 0.6f, 1f } };
    private static readonly float[][] RingH = { new[] { -0.55f, 0.35f, 0.12f, 0f }, new[] { -0.45f, 0.15f, 0.04f, 0f } };
    private static readonly float[] CentreH = { -1f, -0.8f };

    private readonly Mesh _mesh;
    private readonly MeshRenderer _renderer;
    private Entry[] _e;
    private Vector3[] _v;
    private Vector2[] _uv;
    private Color32[] _c;
    private float[] _shade;
    private int _next, _live;

    static BodyDecals()
    {
        for (int j = 0; j < Seg; j++)
        {
            float a = j * Mathf.PI * 2f / Seg;
            Cos[j] = Mathf.Cos(a);
            Sin[j] = Mathf.Sin(a);
        }
    }

    public BodyDecals(Transform parent, Material mat, int capacity)
    {
        var go = new GameObject("BloodFX_Body");
        go.transform.SetParent(parent, false);
        _mesh = new Mesh { name = "BloodFX_Body" };
        _mesh.MarkDynamic();
        go.AddComponent<MeshFilter>().sharedMesh = _mesh;
        _renderer = go.AddComponent<MeshRenderer>();
        _renderer.sharedMaterial = mat;
        _renderer.shadowCastingMode = ShadowCastingMode.Off;
        _renderer.receiveShadows = false;
        _renderer.lightProbeUsage = LightProbeUsage.Off;
        _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        _renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
        Resize(capacity);
    }

    public int Capacity => _e.Length;
    public int LiveCount => _live;

    public void Resize(int capacity)
    {
        capacity = Mathf.Clamp(capacity, 8, 1000);
        _e = new Entry[capacity];
        _v = new Vector3[capacity * Per];
        _uv = new Vector2[capacity * Per];
        _c = new Color32[capacity * Per];
        _shade = new float[capacity * Per];
        int crTris = Seg + (Rings - 1) * Seg * 2, runTris = (RunRows - 1) * 4;
        var tris = new int[capacity * (crTris + runTris) * 3];
        int t = 0;
        for (int i = 0; i < capacity; i++)
        {
            int b = i * Per;
            for (int j = 0; j < Seg; j++)
            {
                int j1 = (j + 1) % Seg;
                tris[t++] = b; tris[t++] = b + 1 + j; tris[t++] = b + 1 + j1;
                for (int k = 0; k < Rings - 1; k++)
                {
                    int a0 = b + 1 + k * Seg + j, a1 = b + 1 + k * Seg + j1, o0 = a0 + Seg, o1 = a1 + Seg;
                    tris[t++] = a0; tris[t++] = o0; tris[t++] = o1;
                    tris[t++] = a0; tris[t++] = o1; tris[t++] = a1;
                }
            }
            int r0 = b + CraterV;
            for (int row = 0; row < RunRows - 1; row++)
            for (int col = 0; col < 2; col++)
            {
                int a = r0 + row * 3 + col, c2 = a + 3;
                tris[t++] = a; tris[t++] = c2; tris[t++] = c2 + 1;
                tris[t++] = a; tris[t++] = c2 + 1; tris[t++] = a + 1;
            }
        }
        _mesh.Clear();
        _mesh.indexFormat = _v.Length > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        _mesh.vertices = _v;
        _mesh.uv = _uv;
        _mesh.colors32 = _c;
        _mesh.triangles = tris;
        _next = 0;
        _live = 0;
        _renderer.enabled = false;
    }

    public void Clear()
    {
        for (int i = 0; i < _e.Length; i++) _e[i].Live = false;
        for (int i = 0; i < _v.Length; i++) _v[i] = Vector3.zero;
        _mesh.vertices = _v;
        _live = 0;
        _renderer.enabled = false;
    }

    /// <param name="normal">Facing of the mark; its "up" is world up projected onto it, so trickles run downhill.</param>
    /// <param name="pull">How far toward the viewer the mark is lifted so it sits on top of clothing or gear.</param>
    public void Add(Transform bone, Vector3 pos, Vector3 normal, float size, int cell, int profile, float pull, Color32 col, float life,
        bool run, float runLen, float runGrow, int runCell, float now)
    {
        int i = _next;
        _next = (_next + 1) % _e.Length; // full: the oldest mark is reused
        if (!_e[i].Live) _live++;
        Vector3 right = Vector3.Cross(Vector3.up, normal);
        if (right.sqrMagnitude < 1e-4f) right = Vector3.Cross(Vector3.forward, normal);
        right.Normalize();
        Vector3 up = Vector3.Cross(normal, right);
        ref Entry e = ref _e[i];
        e.Bone = bone;
        e.LPos = bone.InverseTransformPoint(pos);
        e.LRight = bone.InverseTransformDirection(right);
        e.LUp = bone.InverseTransformDirection(up);
        e.Size = size;
        e.Depth = size * (profile == ProfileArmor ? 0.12f : 0.2f);
        e.Pull = pull;
        e.Profile = profile;
        e.Born = now;
        e.Life = life;
        e.Col = col;
        e.Run = run;
        e.RunLen = runLen;
        e.RunGrow = Mathf.Max(0.1f, runGrow);
        e.RunWidth = size * Random.Range(0.22f, 0.3f);
        e.RunCell = runCell;
        e.Flip = Random.value < 0.5f; // mirror left/right only, the bottom of the wound must stay at the bottom
        e.Live = true;

        // crater UVs and baked relief shading (light from above and slightly in front)
        Rect r = BloodTextures.BodyCell(cell);
        int v = i * Per;
        float[] rr = RingR[profile], rh = RingH[profile];
        _uv[v] = r.center;
        _shade[v] = 0.55f;
        for (int k = 0; k < Rings; k++)
        {
            float hIn = k == 0 ? CentreH[profile] : rh[k - 1], hOut = k == Rings - 1 ? 0f : rh[k + 1];
            float rIn = k == 0 ? 0f : rr[k - 1], rOut = k == Rings - 1 ? rr[k] + 0.3f : rr[k + 1];
            float slope = (hOut - hIn) / (rOut - rIn) * (e.Depth / (size * 0.5f)); // rise per unit radius
            for (int j = 0; j < Seg; j++)
            {
                float x = Cos[j] * rr[k], y = Sin[j] * rr[k];
                float u = e.Flip ? -x : x;
                _uv[v + 1 + k * Seg + j] = new Vector2(r.center.x + u * r.width * 0.5f, r.center.y + y * r.height * 0.5f);
                // surface normal tilts against the slope; light comes from up and toward the viewer
                float ny = -Sin[j] * slope, nz = 1f;
                float len = Mathf.Sqrt(ny * ny + nz * nz + Cos[j] * slope * Cos[j] * slope);
                float lit = (ny * 0.6f + nz * 0.8f) / len;
                _shade[v + 1 + k * Seg + j] = Mathf.Clamp(0.35f + 0.75f * lit, 0.3f, 1.15f) * (k == 0 ? 0.75f : 1f);
            }
        }
        _renderer.enabled = true;
    }

    public void Tick(float now, Vector3 eye, float maxDistSq)
    {
        if (_live == 0) return;
        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), max = -min;
        for (int i = 0; i < _e.Length; i++)
        {
            ref Entry e = ref _e[i];
            if (!e.Live) continue;
            int v = i * Per;
            float age = now - e.Born;
            if (e.Bone == null || age >= e.Life)
            {
                e.Live = false;
                _live--;
                for (int k = 0; k < Per; k++) _v[v + k] = Vector3.zero;
                continue;
            }
            Vector3 p = e.Bone.TransformPoint(e.LPos);
            Vector3 toEye = eye - p;
            float dSq = toEye.sqrMagnitude;
            if (dSq > maxDistSq)
            {
                for (int k = 0; k < Per; k++) _v[v + k] = p; // too far to see: collapse, keep the mark
                continue;
            }
            Vector3 ax = e.Bone.TransformDirection(e.LRight), ay = e.Bone.TransformDirection(e.LUp);
            Vector3 n = Vector3.Cross(ax, ay);
            if (dSq > 1e-4f) p += toEye * (e.Pull / Mathf.Sqrt(dSq));

            Color32 col = e.Col;
            float left = e.Life - age;
            if (left < FadeTime) col.a = (byte)(col.a * (left / FadeTime));

            float half = e.Size * 0.5f;
            float[] rr = RingR[e.Profile], rh = RingH[e.Profile];
            _v[v] = p + n * (e.Depth * CentreH[e.Profile]);
            _c[v] = Mul(col, _shade[v]);
            for (int k = 0; k < Rings; k++)
            {
                Vector3 lift = n * (e.Depth * rh[k]);
                for (int j = 0; j < Seg; j++)
                {
                    int q = v + 1 + k * Seg + j;
                    _v[q] = p + ax * (Cos[j] * rr[k] * half) + ay * (Sin[j] * rr[k] * half) + lift;
                    _c[q] = Mul(col, _shade[q]);
                }
            }

            int rv = v + CraterV;
            if (e.Run)
            {
                // the trickle is revealed from the top down as it runs, a raised bead of blood
                float t = Mathf.Clamp01(age / e.RunGrow);
                t = 1f - (1f - t) * (1f - t);
                float len = Mathf.Max(0.002f, e.RunLen * t);
                Vector3 top = p - ay * (half * 0.3f);
                Rect r = BloodTextures.BodyCell(e.RunCell);
                float vBottom = r.yMax - r.height * (len / e.RunLen);
                for (int row = 0; row < RunRows; row++)
                {
                    float f = row / (float)(RunRows - 1);
                    Vector3 c = top - ay * (len * f);
                    float w = e.RunWidth * 0.5f;
                    float bead = e.RunWidth * 0.35f * (row == RunRows - 1 ? 0.6f : 1f);
                    float vv = Mathf.Lerp(r.yMax, vBottom, f);
                    _v[rv + row * 3] = c - ax * w;
                    _v[rv + row * 3 + 1] = c + n * bead;
                    _v[rv + row * 3 + 2] = c + ax * w;
                    _uv[rv + row * 3] = new Vector2(r.xMin, vv);
                    _uv[rv + row * 3 + 1] = new Vector2(r.center.x, vv);
                    _uv[rv + row * 3 + 2] = new Vector2(r.xMax, vv);
                    _c[rv + row * 3] = Mul(col, 0.7f);
                    _c[rv + row * 3 + 1] = Mul(col, 1.1f);
                    _c[rv + row * 3 + 2] = Mul(col, 0.8f);
                }
                Vector3 bottom = top - ay * len;
                min = Vector3.Min(min, bottom - new Vector3(half, half, half));
                max = Vector3.Max(max, bottom + new Vector3(half, half, half));
            }
            else
            {
                for (int k = 0; k < RunV; k++) _v[rv + k] = p;
            }
            min = Vector3.Min(min, p - new Vector3(e.Size, e.Size, e.Size));
            max = Vector3.Max(max, p + new Vector3(e.Size, e.Size, e.Size));
        }
        _mesh.vertices = _v;
        _mesh.uv = _uv;
        _mesh.colors32 = _c;
        if (_live == 0) _renderer.enabled = false;
        else if (min.x <= max.x)
        {
            var b = new Bounds();
            b.SetMinMax(min, max);
            _mesh.bounds = b;
        }
        else _mesh.bounds = new Bounds(eye, Vector3.one); // everything is far away and collapsed
    }

    private static Color32 Mul(Color32 c, float k)
    {
        return new Color32((byte)Mathf.Min(255f, c.r * k), (byte)Mathf.Min(255f, c.g * k), (byte)Mathf.Min(255f, c.b * k), c.a);
    }
}
