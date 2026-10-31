using UnityEngine;
using UnityEngine.Rendering;

namespace BloodFX;

/// <summary>
/// Volumetric blood streams: each stream is a tube of rings through fluid
/// elements fired from a nozzle over an emission window, every element on its
/// own ballistic arc. The tube thins and beads apart with age, flattens on the
/// floor and on a wall plane, and is shaded per vertex on the CPU (wet diffuse,
/// rim darkening, a specular glint) so an unlit opaque shader still reads as
/// liquid. All streams share one mesh and one draw call.
/// </summary>
internal sealed class BloodStreams
{
    public struct Spec
    {
        public Vector3 Origin, Velocity;
        public float Emit, Life, Radius, Decay, PulseFreq, PulseDepth, Breakup, Gravity, FloorY, Light;
        public Color Col;
        public bool HasWall;
        public Vector3 WallP, WallN;
        public Transform Anchor;  // nozzle follows the wound (arterial, gush)
        public Vector3 AnchorLocal;
        public bool Drip;          // hanging teardrop: Radius = bulb, Life = max length, PulseFreq = seconds per drop
    }

    /// <summary>A drop that just let go of a drip.</summary>
    public struct Detach
    {
        public Vector3 Pos;
        public float Radius;
        public Color Col;
    }

    private struct Stream
    {
        public Spec S;
        public float Born, Phase, Flat, Twist, LastRb;
        public int Seed, Cycle;
        public Vector3 LastBulb;
        public bool Live;
    }

    private readonly Detach[] _detach = new Detach[64];
    public int DetachCount { get; private set; }
    public Detach GetDetach(int i) => _detach[i];
    public void ClearDetach() => DetachCount = 0;

    private const int Sides = 6;
    private static readonly float[] Cos = new float[Sides], Sin = new float[Sides];

    private readonly Mesh _mesh;
    private readonly MeshRenderer _renderer;
    private Stream[] _s;
    private Vector3[] _v;
    private Color32[] _c;
    private Vector3[] _ring;
    private float[] _rad;
    private int _rings, _next, _live;
    private bool _wasDrawing;

    static BloodStreams()
    {
        for (int j = 0; j < Sides; j++)
        {
            float a = j * Mathf.PI * 2f / Sides;
            Cos[j] = Mathf.Cos(a);
            Sin[j] = Mathf.Sin(a);
        }
    }

    public BloodStreams(Transform parent, Material mat, int capacity, int rings)
    {
        var go = new GameObject("BloodFX_Streams");
        go.transform.SetParent(parent, false);
        _mesh = new Mesh { name = "BloodFX_Streams" };
        _mesh.MarkDynamic();
        go.AddComponent<MeshFilter>().sharedMesh = _mesh;
        _renderer = go.AddComponent<MeshRenderer>();
        _renderer.sharedMaterial = mat;
        _renderer.shadowCastingMode = ShadowCastingMode.Off;
        _renderer.receiveShadows = false;
        _renderer.lightProbeUsage = LightProbeUsage.Off;
        _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        _renderer.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
        Resize(capacity, rings);
    }

    public int Capacity => _s.Length;
    public int Rings => _rings;
    public int LiveCount => _live;

    public void Resize(int capacity, int rings)
    {
        capacity = Mathf.Clamp(capacity, 4, 512);
        _rings = Mathf.Clamp(rings, 6, 32);
        _s = new Stream[capacity];
        int per = _rings * Sides;
        _v = new Vector3[capacity * per];
        _c = new Color32[capacity * per];
        _ring = new Vector3[_rings];
        _rad = new float[_rings];
        var tris = new int[capacity * (_rings - 1) * Sides * 6];
        int t = 0;
        for (int s = 0; s < capacity; s++)
        {
            int b = s * per;
            for (int k = 0; k < _rings - 1; k++)
            for (int j = 0; j < Sides; j++)
            {
                int j1 = (j + 1) % Sides;
                int a0 = b + k * Sides + j, a1 = b + k * Sides + j1;
                int c0 = b + (k + 1) * Sides + j, c1 = b + (k + 1) * Sides + j1;
                tris[t++] = a0; tris[t++] = c1; tris[t++] = a1;
                tris[t++] = a0; tris[t++] = c0; tris[t++] = c1;
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
        for (int i = 0; i < _s.Length; i++)
        {
            if (!_s[i].Live) continue;
            _s[i].Live = false;
            Collapse(i);
        }
        _live = 0;
        _mesh.vertices = _v;
        _renderer.enabled = false;
        _wasDrawing = false;
    }

    public void Spawn(ref Spec spec, float now)
    {
        int i = _next;
        _next = (_next + 1) % _s.Length; // full: the oldest stream is reused
        if (!_s[i].Live) _live++;
        _s[i].S = spec;
        _s[i].Born = now;
        _s[i].Phase = Random.value * 6.283f;
        _s[i].Flat = Random.Range(0.35f, 0.7f);   // ribbon/sheet cross-section, not a pipe
        _s[i].Twist = Random.Range(-6f, 6f);
        _s[i].Seed = Random.Range(0, 100000);
        _s[i].Cycle = 0;
        _s[i].LastRb = 0f;
        _s[i].Live = true;
        _renderer.enabled = true;
    }

    /// <summary>Seconds until a ballistic element fired at <paramref name="v"/> reaches <paramref name="floorY"/>.</summary>
    public static float LandTime(Vector3 p0, Vector3 v, float g, float floorY)
    {
        float h = p0.y - floorY;
        if (g <= 0.01f) return -1f;
        float disc = v.y * v.y + 2f * g * h;
        if (disc < 0f) return -1f;
        return (v.y + Mathf.Sqrt(disc)) / g;
    }

    public void Tick(float now, Vector3 camPos)
    {
        if (_live == 0)
        {
            if (_wasDrawing) { _mesh.vertices = _v; _renderer.enabled = false; _wasDrawing = false; }
            return;
        }
        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), max = -min;
        int per = _rings * Sides;
        for (int i = 0; i < _s.Length; i++)
        {
            ref Stream st = ref _s[i];
            if (!st.Live) continue;
            ref Spec sp = ref st.S;
            float age = now - st.Born;
            float head = Mathf.Min(age, sp.Emit);
            float tail = Mathf.Max(0f, age - sp.Life);
            if (sp.Drip ? age >= sp.Emit : tail >= sp.Emit)
            {
                st.Live = false;
                _live--;
                Collapse(i);
                continue;
            }
            if (sp.Anchor != null && age < sp.Emit) sp.Origin = sp.Anchor.TransformPoint(sp.AnchorLocal);
            bool attached = age < sp.Emit;
            float g = 9.81f * sp.Gravity;

            if (sp.Drip) DripRings(ref st, age);
            // ring 0 = newest element (nozzle end), last ring = oldest (leading tip)
            else for (int k = 0; k < _rings; k++)
            {
                float f = k / (float)(_rings - 1);
                float tau = head + (tail - head) * f;
                float a = age - tau;
                float speed = (1f - sp.Decay * tau / Mathf.Max(0.01f, sp.Emit)) *
                              (1f + sp.PulseDepth * Mathf.Sin(6.283f * sp.PulseFreq * tau + st.Phase));
                speed = Mathf.Max(0.05f, speed);
                Vector3 p = sp.Origin + sp.Velocity * (speed * a);
                p.y -= 0.5f * g * a * a;
                // noise is keyed to the element (tau), so clumps and necks travel with the fluid
                float clump = Noise(tau * 14f, st.Seed);
                float r = sp.Radius * Mathf.Sqrt(speed) / (1f + 1.1f * a) * (0.45f + 1.1f * clump);
                // instability: the sheet wanders sideways more the longer it flies
                float wob = (Noise(tau * 9f, st.Seed + 7) - 0.5f) * 0.06f * a;
                p.x += wob * sp.Velocity.z;
                p.z -= wob * sp.Velocity.x;
                // breaks apart irregularly near the old end: necks go to nothing, clumps survive
                float brk = Mathf.Clamp01((a - 0.25f) * sp.Breakup * 1.5f);
                r *= Mathf.Lerp(1f, Mathf.Clamp01((clump - 0.35f) * 3f) * 1.2f, brk);
                if (k == _rings - 1) r *= 0.55f;               // round leading blob
                else if (k == _rings - 2) r *= 1.15f;
                if (k == 0) r *= attached ? 0.8f : 0.25f;      // detached tail tapers off
                if (p.y < sp.FloorY + 0.006f) { p.y = sp.FloorY + 0.006f; r *= 0.55f; }
                if (sp.HasWall)
                {
                    float d = Vector3.Dot(p - sp.WallP, sp.WallN);
                    if (d < 0.006f) { p -= sp.WallN * (d - 0.006f); r *= 0.6f; }
                }
                _ring[k] = p;
                _rad[k] = r;
            }

            int b = i * per;
            Color col = sp.Col;
            float light = sp.Light;
            for (int k = 0; k < _rings; k++)
            {
                Vector3 p = _ring[k];
                Vector3 t = k < _rings - 1 ? _ring[k + 1] - p : p - _ring[k - 1];
                if (t.sqrMagnitude < 1e-8f) t = sp.Velocity;
                t.Normalize();
                Vector3 n1 = Vector3.Cross(t, Mathf.Abs(t.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                Vector3 n2 = Vector3.Cross(t, n1);
                float tw = st.Twist * k / (float)_rings + st.Phase;
                float cw = Mathf.Cos(tw), sw = Mathf.Sin(tw);
                Vector3 a1 = n1 * cw + n2 * sw, a2 = n2 * cw - n1 * sw;
                Vector3 toCam = camPos - p;
                toCam.Normalize();
                Vector3 h = (Vector3.up + toCam).normalized;
                float r = _rad[k];
                float tauK = k * 0.37f + st.Seed;
                for (int j = 0; j < Sides; j++)
                {
                    Vector3 n = a1 * Cos[j] + a2 * Sin[j];
                    // flattened, ragged edge: each side gets its own jitter
                    float rag = sp.Drip ? 1f : 0.75f + 0.5f * Noise(tauK + j * 5.3f, st.Seed + j);
                    Vector3 v = p + (a1 * (Cos[j] * r) + a2 * (Sin[j] * r * (sp.Drip ? 1f : st.Flat))) * rag;
                    _v[b + k * Sides + j] = v;
                    float diff = 0.3f + 0.7f * Mathf.Max(0f, Vector3.Dot(n, Vector3.up) * 0.6f + Vector3.Dot(n, toCam) * 0.4f);
                    float fres = 1f - Mathf.Abs(Vector3.Dot(n, toCam));
                    float s = Mathf.Max(0f, Vector3.Dot(n, h));
                    s *= s; s *= s; s *= s; s *= s; s *= s; // ^32: small wet glint, not plastic
                    float shade = diff * (1f - 0.5f * fres);
                    float spec = s * 0.35f * light;
                    _c[b + k * Sides + j] = new Color(col.r * shade + spec, col.g * shade + spec * 0.45f, col.b * shade + spec * 0.45f, 1f);
                    min = Vector3.Min(min, v);
                    max = Vector3.Max(max, v);
                }
            }
        }
        _mesh.vertices = _v;
        _mesh.colors32 = _c;
        if (_live > 0)
        {
            var bounds = new Bounds();
            bounds.SetMinMax(min, max);
            _mesh.bounds = bounds;
        }
        _wasDrawing = true;
        if (_live == 0) _renderer.enabled = false;
    }

    /// <summary>
    /// A drop forming on a wound: a thin thread hanging straight down that swells
    /// into a teardrop bulb, stretches, and lets go; the thread snaps back and the
    /// next drop starts. Rings are packed toward the bulb so it stays round.
    /// </summary>
    private void DripRings(ref Stream st, float age)
    {
        ref Spec sp = ref st.S;
        float period = Mathf.Max(0.3f, sp.PulseFreq);
        int cycle = (int)(age / period);
        float c = age / period - cycle;
        if (cycle != st.Cycle)
        {
            if (st.LastRb > 0f && DetachCount < _detach.Length)
                _detach[DetachCount++] = new Detach { Pos = st.LastBulb, Radius = st.LastRb, Col = sp.Col };
            st.Cycle = cycle;
        }
        float e = c * c * (3f - 2f * c);
        float rb = sp.Radius * (0.3f + 0.7f * e);
        float len = Mathf.Lerp(rb * 1.4f, sp.Life, e * e * e); // hangs, then stretches fast just before letting go
        Vector3 top = sp.Origin;
        float sway = (Noise(age * 1.3f, st.Seed) - 0.5f) * 0.02f;
        for (int k = 0; k < _rings; k++)
        {
            float u = k / (float)(_rings - 1);
            float t = 1f - (1f - u) * (1f - u);
            float d = len * t;
            Vector3 p = top;
            p.y -= d;
            p.x += sway * t * t;
            float x = (d - (len - rb)) / rb;
            float r;
            if (x >= 0f)
            {
                r = rb * Mathf.Sqrt(Mathf.Max(0f, 1f - x * x)); // round underside
            }
            else
            {
                float neck = rb * 0.2f * (1f + 2f * Mathf.Pow(1f - t, 4f)); // wider where it clings to the wound
                float w = Mathf.Clamp01((x + 2.6f) / 2.6f);
                r = Mathf.Lerp(neck, rb, w * w * w);                          // teardrop shoulder
                if (x > -1f) r = Mathf.Max(r, rb * Mathf.Sqrt(1f - x * x));
            }
            _ring[k] = p;
            _rad[k] = r;
        }
        Vector3 bulb = top;
        bulb.y -= len - rb;
        st.LastBulb = bulb;
        st.LastRb = rb;
    }

    private static float Hash(int x, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + seed * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
        }
    }

    /// <summary>Smooth 1D value noise in 0..1.</summary>
    private static float Noise(float x, int seed)
    {
        int i = Mathf.FloorToInt(x);
        float f = x - i;
        f = f * f * (3f - 2f * f);
        return Mathf.Lerp(Hash(i, seed), Hash(i + 1, seed), f);
    }

    private void Collapse(int i)
    {
        int per = _rings * Sides, b = i * per;
        Vector3 p = _s[i].S.Origin;
        for (int k = 0; k < per; k++) _v[b + k] = p;
    }
}
