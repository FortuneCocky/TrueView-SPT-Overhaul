using UnityEngine;
using UnityEngine.Rendering;

namespace BloodFX;

/// <summary>
/// Blood pooling on the ground as real geometry: a thick, glossy puddle with a
/// rounded meniscus edge that oozes outward over time. The outline is lobed and
/// each lobe advances at its own pace, so the pool creeps out in tongues rather
/// than growing as a circle. Every spoke is clamped by raycasts to where there is
/// ground, so a pool stops at a ledge instead of hanging over it. Shading (dark
/// body, wet highlight that moves with the viewer) is baked into vertex colours;
/// all pools share one mesh and one draw call.
/// </summary>
internal sealed class BloodPools
{
    private struct Pool
    {
        public Vector3 Pos, N, T1, T2;
        public float Radius, H, Start, Grow, Life;
        public Color Col;
        public int Seed;
        public bool Live;
    }

    private const int Seg = 28, Rings = 7;
    private const int Per = 1 + Seg * Rings;
    private const float Lift = 0.0015f, FadeTime = 10f, TickRate = 0.1f;
    // ring positions as a fraction of the radius, bunched at the edge for the meniscus
    private static readonly float[] RingS = { 0.3f, 0.55f, 0.72f, 0.84f, 0.92f, 0.97f, 1f };
    private static readonly float[] Cos = new float[Seg], Sin = new float[Seg];
    private static readonly Vector3 KeyLight = new Vector3(0.3f, 1f, 0.2f).normalized;

    private readonly Mesh _mesh;
    private readonly MeshRenderer _renderer;
    private readonly int _mask;
    private Pool[] _p;
    private float[] _reach, _lag;
    private Vector3[] _v;
    private Color32[] _c;
    private int _next, _live;
    private readonly float[] _r = new float[Seg], _r2 = new float[Seg];
    private float _nextTick;

    static BloodPools()
    {
        for (int j = 0; j < Seg; j++)
        {
            float a = j * Mathf.PI * 2f / Seg;
            Cos[j] = Mathf.Cos(a);
            Sin[j] = Mathf.Sin(a);
        }
    }

    public BloodPools(Transform parent, Material mat, int capacity, int mask)
    {
        _mask = mask;
        var go = new GameObject("BloodFX_Pools");
        go.transform.SetParent(parent, false);
        _mesh = new Mesh { name = "BloodFX_Pools" };
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

    public int Capacity => _p.Length;

    public void Resize(int capacity)
    {
        capacity = Mathf.Clamp(capacity, 4, 128);
        _p = new Pool[capacity];
        _reach = new float[capacity * Seg];
        _lag = new float[capacity * Seg];
        _v = new Vector3[capacity * Per];
        _c = new Color32[capacity * Per];
        var tris = new int[capacity * (Seg + (Rings - 1) * Seg * 2) * 3];
        int t = 0;
        for (int i = 0; i < capacity; i++)
        {
            int b = i * Per;
            for (int j = 0; j < Seg; j++)
            {
                int j1 = (j + 1) % Seg;
                tris[t++] = b; tris[t++] = b + 1 + j1; tris[t++] = b + 1 + j;
                for (int k = 0; k < Rings - 1; k++)
                {
                    int a0 = b + 1 + k * Seg + j, a1 = b + 1 + k * Seg + j1;
                    int o0 = a0 + Seg, o1 = a1 + Seg;
                    tris[t++] = a0; tris[t++] = a1; tris[t++] = o1;
                    tris[t++] = a0; tris[t++] = o1; tris[t++] = o0;
                }
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
    }

    /// <param name="start">When blood first reaches the ground (may be in the future).</param>
    /// <param name="grow">Seconds to ooze out to full size.</param>
    /// <param name="lobing">How irregular the outline is: 1 = spreading pool with tongues, small = a near-round bead.</param>
    public void Add(Vector3 pos, Vector3 normal, float radius, Color col, float start, float grow, float life, float lobing = 1f)
    {
        int i = _next;
        _next = (_next + 1) % _p.Length;
        if (!_p[i].Live) _live++;
        ref Pool p = ref _p[i];
        p.Pos = pos;
        p.N = normal;
        p.T1 = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
        p.T2 = Vector3.Cross(normal, p.T1);
        p.Radius = radius;
        // thick enough to read as liquid; small pools are thinner, every pool a little different
        p.H = Mathf.Lerp(0.003f, 0.008f, Mathf.Clamp01(radius / 0.6f)) * Random.Range(0.8f, 1.2f);
        p.Start = start;
        p.Grow = Mathf.Max(0.2f, grow);
        p.Life = life;
        p.Col = col;
        p.Seed = Random.Range(0, 100000);
        p.Live = true;

        // lobed outline: smooth noise plus two or three tongues that run farther
        int tongues = Random.Range(2, 4);
        float a0 = Random.value * 6.283f, a1 = Random.value * 6.283f, a2 = Random.value * 6.283f;
        float off = Random.value * 50f;
        int rays = radius > 0.3f ? 4 : 2;
        for (int j = 0; j < Seg; j++)
        {
            float ang = j * 6.283f / Seg;
            float lobe = 0.75f + 0.35f * Noise(Cos[j] * 1.6f + off, Sin[j] * 1.6f + off)
                + 0.12f * Noise(Cos[j] * 4f + off * 2f, Sin[j] * 4f);
            lobe += 0.45f * Bump(ang, a0, 0.6f) + 0.3f * Bump(ang, a1, 0.5f) + (tongues > 2 ? 0.25f * Bump(ang, a2, 0.45f) : 0f);
            lobe = 1f + (lobe - 1f) * lobing;
            float reach = radius * lobe;
            Vector3 dir = p.T1 * Cos[j] + p.T2 * Sin[j];
            // stop at the last bit of ground that's level with the pool
            float ok = 0.15f;
            for (int r = 1; r <= rays; r++)
            {
                float f = (float)r / rays;
                Vector3 q = pos + dir * (reach * f);
                if (!Physics.Raycast(q + normal * 0.15f, -normal, out RaycastHit hit, 0.3f, _mask, QueryTriggerInteraction.Ignore)) break;
                if (Mathf.Abs(Vector3.Dot(hit.point - pos, normal)) > 0.03f || Vector3.Dot(hit.normal, normal) < 0.85f) break;
                ok = f;
            }
            _reach[i * Seg + j] = reach * ok;
            // tongues lag a little then keep creeping after the body has settled
            _lag[i * Seg + j] = Mathf.Clamp01(0.08f * Noise(Cos[j] * 1.5f + off * 3f, Sin[j] * 1.5f) + (lobe - 0.9f) * 0.3f);
        }
        _nextTick = 0f;
    }

    public void Tick(float now, Vector3 eye, bool haveEye)
    {
        if (_live == 0 || now < _nextTick) return;
        _nextTick = now + TickRate;
        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), max = -min;
        bool any = false;
        for (int i = 0; i < _p.Length; i++)
        {
            ref Pool p = ref _p[i];
            if (!p.Live) continue;
            float left = p.Start + p.Life - now;
            if (left <= 0f)
            {
                p.Live = false;
                _live--;
                Collapse(i);
                continue;
            }
            if (now < p.Start)
            {
                Collapse(i);
                continue;
            }
            Build(i, now, left < FadeTime ? left / FadeTime : 1f, eye, haveEye);
            float e = p.Radius * 1.6f;
            min = Vector3.Min(min, p.Pos - new Vector3(e, e, e));
            max = Vector3.Max(max, p.Pos + new Vector3(e, e, e));
            any = true;
        }
        _mesh.vertices = _v;
        _mesh.colors32 = _c;
        _renderer.enabled = any;
        if (any)
        {
            var b = new Bounds();
            b.SetMinMax(min, max);
            _mesh.bounds = b;
        }
    }

    private void Build(int i, float now, float fade, Vector3 eye, bool haveEye)
    {
        ref Pool p = ref _p[i];
        float age = (now - p.Start) / p.Grow;
        float h = p.H * fade;
        Vector3 basePos = p.Pos + p.N * Lift;
        Vector3 toEye = haveEye ? (eye - p.Pos).normalized : p.N;
        // drying: the pool darkens and loses its gloss as it fades
        float gloss = Mathf.Lerp(0.25f, 1f, fade);
        int v = i * Per;
        _v[v] = basePos + p.N * (h * 1.05f);
        _c[v] = Shade(ref p, p.N, toEye, 1f, gloss, 0.5f);
        for (int j = 0; j < Seg; j++)
        {
            float x = Mathf.Clamp01(age * (1f + _lag[i * Seg + j]) - _lag[i * Seg + j]);
            // viscous spread: quick at first, then a slow creep to full size
            float grown = 1f - Mathf.Pow(1f - x, 3f);
            _r[j] = _reach[i * Seg + j] * Mathf.Max(0.12f, grown) * Mathf.Lerp(0.85f, 1f, fade);
        }
        // surface tension keeps the rim smooth: relax each spoke toward its neighbours
        for (int pass = 0; pass < 2; pass++)
        {
            for (int j = 0; j < Seg; j++)
                _r2[j] = _r[j] * 0.5f + (_r[(j + Seg - 1) % Seg] + _r[(j + 1) % Seg]) * 0.25f;
            for (int j = 0; j < Seg; j++) _r[j] = Mathf.Min(_r2[j], _reach[i * Seg + j]);
        }
        for (int j = 0; j < Seg; j++)
        {
            float R = _r[j];
            Vector3 dir = p.T1 * Cos[j] + p.T2 * Sin[j];
            for (int k = 0; k < Rings; k++)
            {
                float s = RingS[k];
                // flat, slightly domed top with a rounded meniscus at the rim
                float s6 = s * s * s * s * s * s;
                float hk = h * Mathf.Sqrt(Mathf.Max(0f, 1f - s6)) * (1f - 0.12f * s * s);
                _v[v + 1 + k * Seg + j] = basePos + dir * (R * s) + p.N * hk;
                float slope = h * 3f * s * s * s * s * s / Mathf.Max(0.02f, Mathf.Sqrt(Mathf.Max(1e-4f, 1f - s6))) / Mathf.Max(0.02f, R);
                Vector3 nn = (p.N + dir * Mathf.Min(slope, 3f)).normalized;
                float mottle = 0.85f + 0.3f * Noise(Cos[j] * s * 6f + p.Seed % 97, Sin[j] * s * 6f + k * 0.3f);
                _c[v + 1 + k * Seg + j] = Shade(ref p, nn, toEye, mottle, gloss, s);
            }
        }
    }

    private static Color32 Shade(ref Pool p, Vector3 n, Vector3 toEye, float mottle, float gloss, float s)
    {
        // body: near black-red, a touch redder toward the thinner rim
        float body = (0.5f + 0.15f * s) * mottle * (0.75f + 0.25f * Mathf.Max(0f, Vector3.Dot(n, KeyLight)));
        Vector3 refl = 2f * Vector3.Dot(n, toEye) * n - toEye;
        float spec = Mathf.Pow(Mathf.Max(0f, Vector3.Dot(refl, KeyLight)), 40f) * 0.9f;
        float fres = Mathf.Pow(1f - Mathf.Clamp01(Vector3.Dot(n, toEye)), 5f) * 0.25f;
        float w = (spec + fres) * gloss;
        Color c = p.Col;
        return new Color(c.r * body + w * 0.6f, c.g * body + w * 0.3f, c.b * body + w * 0.3f, 1f);
    }

    private void Collapse(int i)
    {
        int v = i * Per;
        Vector3 q = _p[i].Pos;
        for (int k = 0; k < Per; k++) _v[v + k] = q;
    }

    private static float Bump(float ang, float centre, float width)
    {
        float d = Mathf.DeltaAngle(ang * Mathf.Rad2Deg, centre * Mathf.Rad2Deg) * Mathf.Deg2Rad;
        return Mathf.Exp(-d * d / (width * width));
    }

    private static float Noise(float x, float y) => Mathf.PerlinNoise(x + 13.1f, y + 7.7f);
}
