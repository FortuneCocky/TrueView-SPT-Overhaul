using UnityEngine;
using UnityEngine.Rendering;

namespace BloodFX;

/// <summary>
/// Blood that landed on a surface, as geometry instead of a stretched texture:
/// each drop is a small domed bead (teardrop footprint, tail pointing the way it
/// was flying) sitting on the exact raycast point, so nothing hangs over edges
/// and it stays sharp up close. Shading is baked into vertex colours when the
/// drop lands; all drops share one mesh and one draw call, oldest reused first.
/// </summary>
internal sealed class StuckDrops
{
    private struct Drop
    {
        public Vector3 Pos, N, T;
        public float R, Elong, H, Born, Life;
        public Color Col;
        public bool Live, Fading;
    }

    private const int Seg = 8;
    private const int Per = 1 + Seg * 2;   // top, mid ring, base ring
    private const float FadeTime = 6f, TickRate = 0.25f;
    private static readonly float[] Cos = new float[Seg], Sin = new float[Seg];

    private readonly Mesh _mesh;
    private readonly MeshRenderer _renderer;
    private Drop[] _d;
    private Vector3[] _v;
    private Color32[] _c;
    private int _next, _live;
    private bool _dirty;
    private float _nextTick;
    private Vector3 _min, _max;

    static StuckDrops()
    {
        for (int j = 0; j < Seg; j++)
        {
            float a = j * Mathf.PI * 2f / Seg;
            Cos[j] = Mathf.Cos(a);
            Sin[j] = Mathf.Sin(a);
        }
    }

    public StuckDrops(Transform parent, Material mat, int capacity)
    {
        var go = new GameObject("BloodFX_StuckDrops");
        go.transform.SetParent(parent, false);
        _mesh = new Mesh { name = "BloodFX_StuckDrops" };
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

    public int Capacity => _d.Length;

    public void Resize(int capacity)
    {
        capacity = Mathf.Clamp(capacity, 32, 8000);
        _d = new Drop[capacity];
        _v = new Vector3[capacity * Per];
        _c = new Color32[capacity * Per];
        var tris = new int[capacity * Seg * 9];
        int t = 0;
        for (int i = 0; i < capacity; i++)
        {
            int b = i * Per;
            for (int j = 0; j < Seg; j++)
            {
                int j1 = (j + 1) % Seg;
                int m0 = b + 1 + j, m1 = b + 1 + j1, b0 = b + 1 + Seg + j, b1 = b + 1 + Seg + j1;
                tris[t++] = b; tris[t++] = m1; tris[t++] = m0;          // cap
                tris[t++] = m0; tris[t++] = m1; tris[t++] = b1;         // side
                tris[t++] = m0; tris[t++] = b1; tris[t++] = b0;
            }
        }
        _mesh.Clear();
        _mesh.indexFormat = _v.Length > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        _mesh.vertices = _v;
        _mesh.colors32 = _c;
        _mesh.triangles = tris;
        _next = 0;
        _live = 0;
        _renderer.enabled = false;
        _min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        _max = -_min;
    }

    public void Clear()
    {
        for (int i = 0; i < _d.Length; i++)
        {
            if (!_d[i].Live) continue;
            _d[i].Live = false;
            Collapse(i);
        }
        _live = 0;
        _mesh.vertices = _v;
        _renderer.enabled = false;
        _min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        _max = -_min;
    }

    /// <param name="along">Travel direction of the drop; its tail stretches that way (zero = round).</param>
    public void Add(Vector3 pos, Vector3 normal, Vector3 along, float radius, float elong, Color col, float life, float now)
    {
        int i = _next;
        _next = (_next + 1) % _d.Length;
        if (!_d[i].Live) _live++;
        Vector3 t = along - normal * Vector3.Dot(along, normal);
        if (t.sqrMagnitude < 1e-6f)
        {
            t = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right);
            elong = 1f;
        }
        ref Drop d = ref _d[i];
        d.Pos = pos;
        d.N = normal;
        d.T = t.normalized;
        d.R = radius;
        d.Elong = elong;
        d.H = radius * Random.Range(0.25f, 0.4f) / Mathf.Sqrt(elong);
        d.Born = now;
        d.Life = life;
        d.Col = col;
        d.Live = true;
        d.Fading = false;
        Build(i, 1f);
        float e = radius * elong;
        _min = Vector3.Min(_min, pos - new Vector3(e, e, e));
        _max = Vector3.Max(_max, pos + new Vector3(e, e, e));
        _dirty = true;
        _renderer.enabled = true;
    }

    public void Tick(float now)
    {
        if (_live > 0 && now >= _nextTick)
        {
            _nextTick = now + TickRate;
            for (int i = 0; i < _d.Length; i++)
            {
                ref Drop d = ref _d[i];
                if (!d.Live) continue;
                float left = d.Born + d.Life - now;
                if (left <= 0f)
                {
                    d.Live = false;
                    _live--;
                    Collapse(i);
                    _dirty = true;
                }
                else if (left < FadeTime)
                {
                    // opaque geometry can't fade, so old drops dry and shrink away
                    Build(i, left / FadeTime);
                    _dirty = true;
                }
            }
            if (_live == 0)
            {
                _renderer.enabled = false;
                _min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                _max = -_min;
            }
        }
        if (!_dirty) return;
        _dirty = false;
        _mesh.vertices = _v;
        _mesh.colors32 = _c;
        if (_live > 0)
        {
            var b = new Bounds();
            b.SetMinMax(_min, _max);
            _mesh.bounds = b;
        }
    }

    private void Build(int i, float k)
    {
        ref Drop d = ref _d[i];
        Vector3 n = d.N, t = d.T, b = Vector3.Cross(n, t);
        float r = d.R * k, h = d.H * k;
        int v = i * Per;
        Vector3 basePos = d.Pos + n * 0.0012f;
        // the dome's peak sits a little toward the tail end, like a drop that skidded
        float shift = (d.Elong - 1f) * r * 0.35f;
        _v[v] = basePos + n * h + t * shift;
        Color top = Shade(d.Col, 1f, 0.55f);
        _c[v] = top;
        for (int j = 0; j < Seg; j++)
        {
            float cx = Cos[j], sy = Sin[j];
            float stretch = cx > 0f ? 1f + (d.Elong - 1f) * cx * cx : 1f; // round back, tapered tail forward
            float px = cx * r * stretch, py = sy * r * (cx > 0f ? 1f - 0.45f * cx * (d.Elong - 1f) / d.Elong : 1f);
            _v[v + 1 + j] = basePos + n * (h * 0.7f) + t * (px * 0.72f + shift * 0.5f) + b * (py * 0.72f);
            _v[v + 1 + Seg + j] = basePos + t * px + b * py;
            // fake key light from above-left: one side of the dome catches a wet glint
            float lit = 0.5f + 0.5f * Mathf.Max(0f, -cx * 0.6f + sy * 0.8f);
            _c[v + 1 + j] = Shade(d.Col, 0.75f + 0.25f * lit, lit > 0.9f ? 0.35f : 0f);
            _c[v + 1 + Seg + j] = Shade(d.Col, 0.5f, 0f);
        }
    }

    private static Color Shade(Color c, float diffuse, float spec)
    {
        return new Color(c.r * diffuse + spec * 0.5f, c.g * diffuse + spec * 0.25f, c.b * diffuse + spec * 0.25f, 1f);
    }

    private void Collapse(int i)
    {
        int v = i * Per;
        Vector3 p = _d[i].Pos;
        for (int k = 0; k < Per; k++) _v[v + k] = p;
    }
}
