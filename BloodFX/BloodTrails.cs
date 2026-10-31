using UnityEngine;
using UnityEngine.Rendering;

namespace BloodFX;

/// <summary>
/// Blood that hits a surface while still moving and drags along it: a thick, glossy
/// smear that lays itself down from the landing blob along a meandering path, with
/// ragged edges, a rounded wet head that advances over a fraction of a second and
/// a tail end that thins out. The path is walked by raycasts when it lands, so it
/// follows the surface and stops at a ledge. Shading is baked into vertex colours;
/// all trails share one mesh and one draw call.
/// </summary>
internal sealed class BloodTrails
{
    private struct Trail
    {
        public float Start, Dur, Life, Width, H;
        public Color Col;
        public int Count;
        public bool Live;
    }

    private const int Samples = 32, Across = 5, Per = Samples * Across;
    private const float Lift = 0.0015f, FadeTime = 10f, TickRate = 1f / 15f;
    private static readonly float[] AcrossF = { -1f, -0.5f, 0f, 0.5f, 1f };
    private static readonly float[] AcrossH = { 0f, 0.75f, 1f, 0.75f, 0f };
    private static readonly float[] AcrossTilt = { -1.4f, -0.5f, 0f, 0.5f, 1.4f };
    private static readonly Vector3 KeyLight = new Vector3(0.3f, 1f, 0.2f).normalized;

    private readonly Mesh _mesh;
    private readonly MeshRenderer _renderer;
    private readonly int _mask;
    private Trail[] _t;
    private Vector3[] _p, _n, _s;
    private float[] _wl, _wr;
    private Vector3[] _v;
    private Color32[] _c;
    private int _next, _live;
    private float _nextTick;

    public BloodTrails(Transform parent, Material mat, int capacity, int mask)
    {
        _mask = mask;
        var go = new GameObject("BloodFX_Trails");
        go.transform.SetParent(parent, false);
        _mesh = new Mesh { name = "BloodFX_Trails" };
        _mesh.MarkDynamic();
        go.AddComponent<MeshFilter>().sharedMesh = _mesh;
        _renderer = go.AddComponent<MeshRenderer>();
        _renderer.sharedMaterial = mat;
        _renderer.shadowCastingMode = ShadowCastingMode.Off;
        _renderer.receiveShadows = false;
        _renderer.lightProbeUsage = LightProbeUsage.Off;
        _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        Resize(capacity);
    }

    public int Capacity => _t.Length;
    public int LiveCount => _live;

    public void Resize(int capacity)
    {
        capacity = Mathf.Clamp(capacity, 4, 128);
        _t = new Trail[capacity];
        _p = new Vector3[capacity * Samples];
        _n = new Vector3[capacity * Samples];
        _s = new Vector3[capacity * Samples];
        _wl = new float[capacity * Samples];
        _wr = new float[capacity * Samples];
        _v = new Vector3[capacity * Per];
        _c = new Color32[capacity * Per];
        var tris = new int[capacity * (Samples - 1) * (Across - 1) * 6];
        int t = 0;
        for (int i = 0; i < capacity; i++)
        for (int k = 0; k < Samples - 1; k++)
        for (int a = 0; a < Across - 1; a++)
        {
            int v = i * Per + k * Across + a;
            tris[t++] = v; tris[t++] = v + Across; tris[t++] = v + Across + 1;
            tris[t++] = v; tris[t++] = v + Across + 1; tris[t++] = v + 1;
        }
        _mesh.Clear();
        _mesh.indexFormat = _v.Length > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        _mesh.vertices = _v;
        _mesh.colors32 = _c;
        _mesh.triangles = tris;
        _next = 0;
        _live = 0;
        _renderer.enabled = false;
    }

    public void Clear()
    {
        for (int i = 0; i < _t.Length; i++)
        {
            if (!_t[i].Live) continue;
            _t[i].Live = false;
            Collapse(i, _p[i * Samples]);
        }
        _live = 0;
        _mesh.vertices = _v;
        _renderer.enabled = false;
    }

    /// <param name="dir">Travel direction along the surface.</param>
    /// <param name="start">When the blood reaches the surface (may be in the future).</param>
    /// <param name="dur">Seconds for the smear to drag out to full length.</param>
    /// <param name="wander">How much the path meanders (0 = dead straight, like a drip running down a wall).</param>
    public void Add(Vector3 pos, Vector3 normal, Vector3 dir, float length, float width, Color col, float start, float dur, float life, float wander = 1f)
    {
        int i = _next;
        _next = (_next + 1) % _t.Length;
        if (!_t[i].Live) _live++;
        ref Trail tr = ref _t[i];
        tr.Start = start;
        tr.Dur = Mathf.Max(0.1f, dur);
        tr.Life = life;
        tr.Width = width;
        tr.H = Mathf.Clamp(width * 0.18f, 0.002f, 0.006f);
        tr.Col = col;
        tr.Live = true;

        // walk the path over the surface, meandering a little, until it runs out of ground
        float seed = Random.value * 100f, bend = Random.Range(-0.12f, 0.12f);
        float step = length / (Samples - 1);
        Vector3 p = pos, n = normal;
        Vector3 head = dir - n * Vector3.Dot(dir, n);
        if (head.sqrMagnitude < 1e-6f) head = Vector3.Cross(n, Vector3.right);
        head.Normalize();
        int b = i * Samples, count = 0;
        // the tail end rounds off over about one width (at least a few samples) instead of starting square
        float tailF = Mathf.Clamp(width * 0.9f / Mathf.Max(length, 0.01f), 5f / (Samples - 1), 0.3f);
        for (int k = 0; k < Samples; k++)
        {
            float f = k / (float)(Samples - 1);
            Vector3 side = Vector3.Cross(n, head).normalized;
            _p[b + k] = p;
            _n[b + k] = n;
            _s[b + k] = side;
            // rounded tail, a slight swell where the drop landed, then a body that narrows toward the head;
            // a little ragged on both sides (less so on the rounded tail)
            float t = Mathf.Clamp01(f / tailF);
            float tail = Mathf.Sqrt(1f - (1f - t) * (1f - t));
            float body = tail * (Mathf.Lerp(1f, 0.6f, f) + 0.2f * Mathf.Exp(-(f - tailF) * (f - tailF) * 80f));
            float rag = 0.3f * t;
            float jagL = 1f - rag + rag * (Mathf.PerlinNoise(seed, k * 1.3f) + 0.6f * (Hash(seed + k) - 0.5f));
            float jagR = 1f - rag + rag * (Mathf.PerlinNoise(seed + 9f, k * 1.3f) + 0.6f * (Hash(seed + 50f + k) - 0.5f));
            _wl[b + k] = width * 0.5f * body * jagL;
            _wr[b + k] = width * 0.5f * body * jagR;
            count = k + 1;
            if (k == Samples - 1) break;

            float turn = (bend * 0.25f + (Mathf.PerlinNoise(seed + 20f, k * 0.15f) - 0.5f) * 0.1f) * wander;
            head = Quaternion.AngleAxis(turn * Mathf.Rad2Deg, n) * head;
            Vector3 q = p + head * step;
            if (!Physics.Raycast(q + n * 0.1f, -n, out RaycastHit hit, 0.25f, _mask, QueryTriggerInteraction.Ignore)) break;
            if (Vector3.Dot(hit.normal, n) < 0.6f) break;
            p = hit.point;
            n = hit.normal;
            head = head - n * Vector3.Dot(head, n);
            if (head.sqrMagnitude < 1e-6f) break;
            head.Normalize();
        }
        tr.Count = Mathf.Max(2, count);
        if (count < 2)
        {
            _p[b + 1] = _p[b];
            _n[b + 1] = _n[b];
            _s[b + 1] = _s[b];
            _wl[b + 1] = _wl[b];
            _wr[b + 1] = _wr[b];
        }
        Collapse(i, pos);
        _nextTick = 0f;
    }

    public void Tick(float now, Vector3 eye, bool haveEye)
    {
        if (_live == 0 || now < _nextTick) return;
        _nextTick = now + TickRate;
        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), max = -min;
        bool any = false;
        for (int i = 0; i < _t.Length; i++)
        {
            ref Trail tr = ref _t[i];
            if (!tr.Live) continue;
            float left = tr.Start + tr.Life - now;
            if (left <= 0f)
            {
                tr.Live = false;
                _live--;
                Collapse(i, _p[i * Samples]);
                continue;
            }
            if (now < tr.Start) continue;
            Build(i, now, left < FadeTime ? left / FadeTime : 1f, eye, haveEye);
            int b = i * Samples;
            for (int k = 0; k < tr.Count; k += 4)
            {
                min = Vector3.Min(min, _p[b + k] - Vector3.one * tr.Width);
                max = Vector3.Max(max, _p[b + k] + Vector3.one * tr.Width);
            }
            min = Vector3.Min(min, _p[b + tr.Count - 1] - Vector3.one * tr.Width);
            max = Vector3.Max(max, _p[b + tr.Count - 1] + Vector3.one * tr.Width);
            any = true;
        }
        _mesh.vertices = _v;
        _mesh.colors32 = _c;
        _renderer.enabled = any;
        if (any)
        {
            var bb = new Bounds();
            bb.SetMinMax(min, max);
            _mesh.bounds = bb;
        }
    }

    private void Build(int i, float now, float fade, Vector3 eye, bool haveEye)
    {
        ref Trail tr = ref _t[i];
        int b = i * Samples, v = i * Per;
        float x = Mathf.Clamp01((now - tr.Start) / tr.Dur);
        // fast at first, then slows as the blood runs out
        float headF = (1f - (1f - x) * (1f - x)) * (tr.Count - 1);
        int hk = Mathf.Min(tr.Count - 2, (int)headF);
        float hf = headF - hk;
        Vector3 headP = Vector3.Lerp(_p[b + hk], _p[b + hk + 1], hf);
        float gloss = Mathf.Lerp(0.25f, 1f, fade);
        for (int k = 0; k < Samples; k++)
        {
            int s = Mathf.Min(k, tr.Count - 1);
            Vector3 p = k <= headF ? _p[b + s] : headP;
            Vector3 n = _n[b + s], side = _s[b + s];
            // rounded head: width and height ease in over the last couple of samples
            float cap = Mathf.Clamp01((headF - k) * 0.6f + 0.25f);
            if (k > headF) cap = 0f;
            float wl = _wl[b + s] * cap, wr = _wr[b + s] * cap;
            float h = tr.H * fade * Mathf.Sqrt(cap) * Mathf.Clamp01(Mathf.Max(wl, wr) / (tr.Width * 0.35f));
            Vector3 toEye = haveEye ? (eye - p).normalized : n;
            for (int a = 0; a < Across; a++)
            {
                float off = AcrossF[a] < 0f ? AcrossF[a] * wl : AcrossF[a] * wr;
                _v[v + k * Across + a] = p + side * off + n * (Lift + h * AcrossH[a]);
                Vector3 nn = (n + side * AcrossTilt[a] * Mathf.Clamp01(h / 0.003f)).normalized;
                _c[v + k * Across + a] = Shade(tr.Col, nn, toEye, gloss, a);
            }
        }
    }

    private static Color32 Shade(Color c, Vector3 n, Vector3 toEye, float gloss, int a)
    {
        float body = (a == 2 ? 0.55f : a == 0 || a == 4 ? 0.7f : 0.6f) * (0.75f + 0.25f * Mathf.Max(0f, Vector3.Dot(n, KeyLight)));
        Vector3 refl = 2f * Vector3.Dot(n, toEye) * n - toEye;
        float spec = Mathf.Pow(Mathf.Max(0f, Vector3.Dot(refl, KeyLight)), 40f) * 0.9f * gloss;
        return Wet.Shade(c, body, spec);
    }

    private void Collapse(int i, Vector3 at)
    {
        int v = i * Per;
        for (int k = 0; k < Per; k++) _v[v + k] = at;
    }

    private static float Hash(float x)
    {
        float s = Mathf.Sin(x * 12.9898f) * 43758.5453f;
        return s - Mathf.Floor(s);
    }
}
