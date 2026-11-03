using UnityEngine;
using UnityEngine.Rendering;

namespace BloodFX;

/// <summary>
/// Bloody boot prints: the blood a foot picked up in a pool, pressed on to the floor at the
/// next steps. A print is a boot tread, not a solid sole: three big lugs at the heel, an
/// empty arch, a wide crescent block at the ball and a grid of small lugs over the forefoot
/// that get smaller toward the toe. Each lug is a raised ring of real geometry with a dark,
/// wet rim and a paler, drier middle, blotchy where the tread didn't press. The wetter the
/// foot the more lugs show; as it dries, lugs drop out from the toe back until only the heel
/// is left. The left print is the mirror of the right. Prints dry and shrink away over the
/// last seconds of their life. All prints share one mesh and one draw call.
/// </summary>
internal sealed class Footprints
{
    private struct Print
    {
        public Vector3 Pos, N, F, S;
        public float Ink, Born, Life, Seed;
        public Color Col;
        public bool Live;
    }

    private struct Pad
    {
        public float X, Y, HX, HY, Need, Curve, Round;
    }

    // A right boot tread, about 29 x 10 cm: x across (+ = outer edge), y along (+ = toe), centred on the arch.
    private static readonly Pad[] Tread = BuildTread();
    private const int Pads = 20, Seg = 8, PerPad = 1 + Seg * 2, Per = Pads * PerPad;
    private const float Lift = 0.0013f, FadeTime = 8f, TickRate = 0.25f;
    private static readonly float[] Cos = new float[Seg], Sin = new float[Seg];

    private readonly Mesh _mesh;
    private readonly MeshRenderer _renderer;
    private Print[] _p;
    private Vector3[] _v;
    private Color32[] _c;
    private int _next, _live;
    private bool _dirty;
    private float _nextTick;
    private Vector3 _min, _max;

    static Footprints()
    {
        for (int j = 0; j < Seg; j++)
        {
            float a = j * Mathf.PI * 2f / Seg;
            Cos[j] = Mathf.Cos(a);
            Sin[j] = Mathf.Sin(a);
        }
    }

    private static Pad[] BuildTread()
    {
        var t = new Pad[Pads];
        int k = 0;
        // heel: three wide rounded blocks, the first to show and the last to go
        t[k++] = new Pad { X = 0f, Y = -0.135f, HX = 0.036f, HY = 0.013f, Need = 0.05f, Round = 3.5f };
        t[k++] = new Pad { X = 0f, Y = -0.100f, HX = 0.038f, HY = 0.013f, Need = 0.03f, Round = 3.5f };
        t[k++] = new Pad { X = 0f, Y = -0.065f, HX = 0.036f, HY = 0.012f, Need = 0.09f, Round = 3.5f };
        // the ball: one wide crescent whose ends bend toward the toe
        t[k++] = new Pad { X = 0f, Y = 0.006f, HX = 0.047f, HY = 0.011f, Need = 0.08f, Curve = 0.014f, Round = 3f };
        // forefoot: a 4 x 4 grid of small lugs, narrower and fainter toward the toe
        float[] rowY = { 0.034f, 0.059f, 0.084f, 0.108f };
        float[] rowW = { 1f, 0.97f, 0.9f, 0.76f };
        for (int r = 0; r < 4; r++)
        for (int c = 0; c < 4; c++)
        {
            float col = c - 1.5f;
            t[k++] = new Pad
            {
                X = col * 0.0225f * rowW[r],
                Y = rowY[r] + (r == 3 ? -0.003f * Mathf.Abs(col) : 0f),
                HX = 0.0095f * (1f - 0.12f * r) * (r == 3 && Mathf.Abs(col) > 1f ? 0.8f : 1f),
                HY = 0.0085f * (1f - 0.1f * r),
                Need = 0.12f + 0.1f * r + 0.035f * Mathf.Abs(col),
                Round = 3f,
            };
        }
        return t;
    }

    public Footprints(Transform parent, Material mat, int capacity)
    {
        var go = new GameObject("BloodFX_Footprints");
        go.transform.SetParent(parent, false);
        _mesh = new Mesh { name = "BloodFX_Footprints" };
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

    public int Capacity => _p.Length;
    public int LiveCount => _live;

    public void Resize(int capacity)
    {
        capacity = Mathf.Clamp(capacity, 16, 256);
        _p = new Print[capacity];
        _v = new Vector3[capacity * Per];
        _c = new Color32[capacity * Per];
        var tris = new int[capacity * Pads * Seg * 9];
        int t = 0;
        for (int i = 0; i < capacity; i++)
        for (int k = 0; k < Pads; k++)
        {
            int b = i * Per + k * PerPad;
            for (int j = 0; j < Seg; j++)
            {
                int j1 = (j + 1) % Seg;
                int m0 = b + 1 + j, m1 = b + 1 + j1, b0 = b + 1 + Seg + j, b1 = b + 1 + Seg + j1;
                tris[t++] = b; tris[t++] = m1; tris[t++] = m0;
                tris[t++] = m0; tris[t++] = m1; tris[t++] = b1;
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
        for (int i = 0; i < _p.Length; i++)
        {
            if (!_p[i].Live) continue;
            _p[i].Live = false;
            Collapse(i);
        }
        _live = 0;
        _mesh.vertices = _v;
        _renderer.enabled = false;
        _min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        _max = -_min;
    }

    /// <param name="pos">Centre of the sole on the floor.</param>
    /// <param name="forward">Heel-to-toe direction along the floor.</param>
    /// <param name="right">Right foot (the left is its mirror image).</param>
    /// <param name="ink">How much blood is on the foot, 0..1.</param>
    public void Add(Vector3 pos, Vector3 normal, Vector3 forward, bool right, float ink, Color col, float life, float now)
    {
        int i = _next;
        _next = (_next + 1) % _p.Length;
        if (!_p[i].Live) _live++;
        Vector3 f = forward - normal * Vector3.Dot(forward, normal);
        if (f.sqrMagnitude < 1e-6f) f = Vector3.Cross(normal, Vector3.right);
        f.Normalize();
        ref Print p = ref _p[i];
        p.Pos = pos;
        p.N = normal;
        p.F = f;
        p.S = Vector3.Cross(normal, f) * (right ? 1f : -1f);
        p.Ink = Mathf.Clamp01(ink);
        p.Born = now;
        p.Life = life;
        p.Seed = Random.value * 100f;
        p.Col = col;
        p.Live = true;
        Build(i, 1f);
        Vector3 e = new Vector3(0.2f, 0.2f, 0.2f);
        _min = Vector3.Min(_min, pos - e);
        _max = Vector3.Max(_max, pos + e);
        _dirty = true;
        _renderer.enabled = true;
    }

    public void Tick(float now)
    {
        if (_live > 0 && now >= _nextTick)
        {
            _nextTick = now + TickRate;
            for (int i = 0; i < _p.Length; i++)
            {
                ref Print p = ref _p[i];
                if (!p.Live) continue;
                float left = p.Born + p.Life - now;
                if (left <= 0f)
                {
                    p.Live = false;
                    _live--;
                    Collapse(i);
                    _dirty = true;
                }
                else if (left < FadeTime)
                {
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

    private void Build(int i, float fade)
    {
        ref Print p = ref _p[i];
        Vector3 basePos = p.Pos + p.N * Lift;
        float ink = p.Ink;
        // a drying foot leaves fewer, thinner, smaller lugs with paler middles
        float h = 0.0015f * Mathf.Sqrt(ink) * fade;
        float size = (0.8f + 0.2f * ink) * fade;
        float gloss = Mathf.Lerp(0.15f, 0.6f, ink) * fade;
        float dry = 1f - ink;
        int v = i * Per;
        for (int k = 0; k < Pads; k++)
        {
            Pad pad = Tread[k];
            int b = v + k * PerPad;
            // each print loses a different set of lugs, so no two look alike
            float need = pad.Need * (0.7f + 0.6f * Hash(p.Seed + k * 3.7f));
            Vector3 c = basePos + p.S * pad.X + p.F * pad.Y;
            if (ink < need)
            {
                for (int j = 0; j < PerPad; j++) _v[b + j] = c;
                continue;
            }
            // lugs that only just show are partial: smaller and a little off, like a light touch
            float show = Mathf.Clamp01((ink - need) / 0.2f);
            float sz = size * (0.6f + 0.4f * show);
            c += p.S * ((Hash(p.Seed + k * 1.3f) - 0.5f) * 0.004f) + p.F * ((Hash(p.Seed + k * 2.9f) - 0.5f) * 0.003f);
            // the middle sits lower and paler than the wet rim: the tread pressed the blood out to its edges
            _v[b] = c + p.N * (h * 0.35f);
            _c[b] = Wet.Shade(p.Col, 1f + 0.4f * dry + 0.15f * Hash(p.Seed + k * 5.1f), 0.1f * gloss);
            for (int j = 0; j < Seg; j++)
            {
                // rounded rectangle, ragged where the tread didn't press
                float ca = Mathf.Abs(Cos[j]), sa = Mathf.Abs(Sin[j]);
                float rr = 1f / Mathf.Pow(Mathf.Pow(ca, pad.Round) + Mathf.Pow(sa, pad.Round), 1f / pad.Round);
                float jag = 0.72f + 0.5f * Hash(p.Seed + k * 11f + j * 0.77f);
                float x = Cos[j] * rr * pad.HX * sz * jag, y = Sin[j] * rr * pad.HY * sz * jag;
                if (pad.Curve > 0f) y += pad.Curve * (x / pad.HX) * (x / pad.HX) * sz; // crescent
                Vector3 edge = c + p.S * x + p.F * y;
                _v[b + 1 + j] = c + p.S * (x * 0.78f) + p.F * (y * 0.78f) + p.N * h;
                _v[b + 1 + Seg + j] = edge;
                float lit = 0.5f + 0.5f * Mathf.Max(0f, -Cos[j] * 0.6f + Sin[j] * 0.8f);
                float blot = 0.85f + 0.3f * Hash(p.Seed + k * 7.3f + j * 1.9f);
                _c[b + 1 + j] = Wet.Shade(p.Col, (0.58f + 0.12f * lit) * blot, lit > 0.85f ? 0.3f * gloss : 0.05f * gloss);
                _c[b + 1 + Seg + j] = Wet.Shade(p.Col, 0.48f * blot, 0f);
            }
        }
    }

    private void Collapse(int i)
    {
        int v = i * Per;
        Vector3 q = _p[i].Pos;
        for (int k = 0; k < Per; k++) _v[v + k] = q;
    }

    private static float Hash(float x)
    {
        float s = Mathf.Sin(x * 12.9898f) * 43758.5453f;
        return s - Mathf.Floor(s);
    }
}
