using UnityEngine;
using UnityEngine.Rendering;

namespace BloodFX;

/// <summary>
/// The smear a bleeding body leaves when it is dragged over the ground: a band of long
/// parallel streaks in the direction of travel, each one breaking up and thinning as the
/// blood on the body runs out, with a denser, darker band in the middle where the body
/// pressed hardest. Every streak is a thin raised ribbon, so it catches the light like the
/// trails and pools do. A smear is laid down sample by sample as the body moves; when a
/// track is full the next one carries on from its last sample. All smears share one mesh
/// and one draw call, oldest reused first.
/// </summary>
internal sealed class DragSmears
{
    public struct Handle
    {
        public int Slot, Gen;
    }

    private struct Track
    {
        public float Born, Life, Seed;
        public Color Col;
        public int Count, Gen;
        public bool Live;
    }

    public const int Samples = 24;
    private const int Streaks = 7, Band = 3; // streak 3 is the dense band where the body pressed
    private const int Across = Streaks * 3, Per = Samples * Across;
    private const float Lift = 0.0014f, FadeTime = 10f, TickRate = 1f / 15f;
    private static readonly Vector3 KeyLight = new Vector3(0.3f, 1f, 0.2f).normalized;

    private readonly Mesh _mesh;
    private readonly MeshRenderer _renderer;
    private Track[] _t;
    private Vector3[] _p, _n, _s;   // per sample: point, surface normal, side (across the travel)
    private float[] _w, _k;         // per sample: width of the part across the travel, smear strength 0..1
    private float[] _su, _sw, _sph; // per streak: lateral offset (-1..1), width share, noise phase
    private Vector3[] _v;
    private Color32[] _c;
    private int _next, _live;
    private float _nextTick;

    public DragSmears(Transform parent, Material mat, int capacity)
    {
        var go = new GameObject("BloodFX_DragSmears");
        go.transform.SetParent(parent, false);
        _mesh = new Mesh { name = "BloodFX_DragSmears" };
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
        capacity = Mathf.Clamp(capacity, 8, 64);
        _t = new Track[capacity];
        _p = new Vector3[capacity * Samples];
        _n = new Vector3[capacity * Samples];
        _s = new Vector3[capacity * Samples];
        _w = new float[capacity * Samples];
        _k = new float[capacity * Samples];
        _su = new float[capacity * Streaks];
        _sw = new float[capacity * Streaks];
        _sph = new float[capacity * Streaks];
        _v = new Vector3[capacity * Per];
        _c = new Color32[capacity * Per];
        var tris = new int[capacity * (Samples - 1) * Streaks * 12];
        int t = 0;
        for (int i = 0; i < capacity; i++)
        for (int j = 0; j < Samples - 1; j++)
        for (int k = 0; k < Streaks; k++)
        {
            int a = i * Per + j * Across + k * 3, b = a + Across;
            // two quads per streak segment: left edge to crest, crest to right edge
            tris[t++] = a; tris[t++] = b; tris[t++] = b + 1;
            tris[t++] = a; tris[t++] = b + 1; tris[t++] = a + 1;
            tris[t++] = a + 1; tris[t++] = b + 1; tris[t++] = b + 2;
            tris[t++] = a + 1; tris[t++] = b + 2; tris[t++] = a + 2;
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

    /// <summary>Starts a smear at <paramref name="pos"/>; extend it as the body moves on.</summary>
    /// <param name="dir">Direction of travel along the surface.</param>
    /// <param name="width">How wide the part is across the travel (metres).</param>
    /// <param name="strength">How much blood is going down, 0..1: sets how many streaks there are and how solid.</param>
    public Handle Begin(Vector3 pos, Vector3 normal, Vector3 dir, float width, float strength, Color col, float now, float life)
    {
        int i = _next;
        _next = (_next + 1) % _t.Length;
        if (!_t[i].Live) _live++;
        ref Track tr = ref _t[i];
        tr.Born = now;
        tr.Life = life;
        tr.Seed = Random.value * 100f;
        tr.Col = col;
        tr.Count = 0;
        tr.Gen++;
        tr.Live = true;
        for (int k = 0; k < Streaks; k++)
        {
            int sk = i * Streaks + k;
            if (k == Band)
            {
                _su[sk] = Random.Range(-0.15f, 0.15f);
                _sw[sk] = Random.Range(0.11f, 0.15f);
            }
            else
            {
                // spread across the width, a little uneven, the outer ones thinnest
                float u = (k - Band) / (float)Band * 0.9f + Random.Range(-0.08f, 0.08f);
                _su[sk] = u;
                _sw[sk] = Random.Range(0.025f, 0.065f) * (1f - 0.35f * Mathf.Abs(u));
            }
            _sph[sk] = Random.value * 50f;
        }
        AddSample(i, pos, normal, dir, width, strength, Vector3.zero);
        return new Handle { Slot = i, Gen = tr.Gen };
    }

    /// <summary>Lays the smear on to <paramref name="pos"/>. False when this track is full or gone: begin a new one.</summary>
    public bool Extend(Handle h, Vector3 pos, Vector3 normal, Vector3 dir, float width, float strength)
    {
        if (h.Slot < 0 || h.Slot >= _t.Length) return false;
        ref Track tr = ref _t[h.Slot];
        if (!tr.Live || tr.Gen != h.Gen || tr.Count >= Samples) return false;
        AddSample(h.Slot, pos, normal, dir, width, strength, _s[h.Slot * Samples + tr.Count - 1]);
        return true;
    }

    public bool IsLive(Handle h) => h.Slot >= 0 && h.Slot < _t.Length && _t[h.Slot].Live && _t[h.Slot].Gen == h.Gen;

    private void AddSample(int i, Vector3 pos, Vector3 normal, Vector3 dir, float width, float strength, Vector3 prevSide)
    {
        ref Track tr = ref _t[i];
        int b = i * Samples + tr.Count;
        Vector3 side = Vector3.Cross(normal, dir);
        if (side.sqrMagnitude < 1e-6f) side = prevSide.sqrMagnitude > 1e-6f ? prevSide : Vector3.Cross(normal, Vector3.right);
        side.Normalize();
        // keep the side pointing the same way as the sample before, or the ribbon would twist over
        if (prevSide.sqrMagnitude > 1e-6f && Vector3.Dot(side, prevSide) < 0f) side = -side;
        _p[b] = pos;
        _n[b] = normal;
        _s[b] = side;
        _w[b] = width;
        _k[b] = strength;
        tr.Count++;
        if (tr.Count == 1) Collapse(i, pos);
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
            ref Track tr = ref _t[i];
            if (!tr.Live) continue;
            float left = tr.Born + tr.Life - now;
            if (left <= 0f || tr.Count < 2)
            {
                if (left <= 0f)
                {
                    tr.Live = false;
                    _live--;
                    Collapse(i, _p[i * Samples]);
                }
                continue;
            }
            Build(i, left < FadeTime ? left / FadeTime : 1f, eye, haveEye);
            int b = i * Samples;
            for (int j = 0; j < tr.Count; j += 4)
            {
                Vector3 e = Vector3.one * (_w[b + j] * 0.6f);
                min = Vector3.Min(min, _p[b + j] - e);
                max = Vector3.Max(max, _p[b + j] + e);
            }
            Vector3 el = Vector3.one * (_w[b + tr.Count - 1] * 0.6f);
            min = Vector3.Min(min, _p[b + tr.Count - 1] - el);
            max = Vector3.Max(max, _p[b + tr.Count - 1] + el);
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

    private void Build(int i, float fade, Vector3 eye, bool haveEye)
    {
        ref Track tr = ref _t[i];
        int b = i * Samples, v = i * Per;
        // drying: the smear loses its gloss, then shrinks away
        float gloss = Mathf.Lerp(0.2f, 1f, fade);
        for (int j = 0; j < Samples; j++)
        {
            int s = b + Mathf.Min(j, tr.Count - 1);
            Vector3 p = _p[s], n = _n[s], side = _s[s];
            float w = _w[s], k = _k[s];
            // the smear fades in over the first sample and thins toward the end of the track
            float along = j >= tr.Count ? 0f : 1f;
            Vector3 toEye = haveEye ? (eye - p).normalized : n;
            Vector3 basePos = p + n * Lift;
            float keyLit = 0.75f + 0.25f * Mathf.Max(0f, Vector3.Dot(n, KeyLight));
            for (int st = 0; st < Streaks; st++)
            {
                int sk = i * Streaks + st;
                int vi = v + j * Across + st * 3;
                float lat = _su[sk] * w * 0.5f;
                // streaks break up where there's little blood left: noise against a threshold set by the strength
                float noise = SurfaceNets.Perlin(tr.Seed + st * 7.3f, j * 0.45f + _sph[sk]);
                float th = (st == Band ? 0.58f : 0.32f) + 0.5f * k;
                float f = Mathf.Clamp01((th - noise) / 0.12f) * along;
                float hw = w * _sw[sk] * f * fade * (0.55f + 0.6f * k);
                float h = (st == Band ? 0.0025f : 0.0012f) * Mathf.Sqrt(Mathf.Max(k, 0f)) * f * fade;
                Vector3 c = basePos + side * lat;
                if (hw < 1e-5f)
                {
                    _v[vi] = _v[vi + 1] = _v[vi + 2] = c;
                    _c[vi] = _c[vi + 1] = _c[vi + 2] = Wet.Shade(tr.Col, 0.5f, 0f);
                    continue;
                }
                _v[vi] = c - side * hw;
                _v[vi + 1] = c + n * h;
                _v[vi + 2] = c + side * hw;
                float mottle = 0.85f + 0.3f * SurfaceNets.Perlin(tr.Seed + 50f + st, j * 1.3f);
                float body = (st == Band ? 0.46f : 0.6f) * mottle * keyLit;
                float tilt = Mathf.Clamp01(h / 0.002f) * 1.3f;
                _c[vi] = Shade(tr.Col, (n - side * tilt).normalized, toEye, body * 1.08f, gloss * f);
                _c[vi + 1] = Shade(tr.Col, n, toEye, body, gloss * f);
                _c[vi + 2] = Shade(tr.Col, (n + side * tilt).normalized, toEye, body * 1.08f, gloss * f);
            }
        }
    }

    private static Color32 Shade(Color c, Vector3 n, Vector3 toEye, float body, float gloss)
    {
        Vector3 refl = 2f * Vector3.Dot(n, toEye) * n - toEye;
        float spec = Mathf.Pow(Mathf.Max(0f, Vector3.Dot(refl, KeyLight)), 40f) * 0.8f * gloss;
        return Wet.Shade(c, body, spec);
    }

    private void Collapse(int i, Vector3 at)
    {
        int v = i * Per;
        for (int k = 0; k < Per; k++) _v[v + k] = at;
    }
}
