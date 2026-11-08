using UnityEngine;
using UnityEngine.Rendering;

namespace BloodFX;

/// <summary>
/// Blood leaving a wound as a shower of separate droplets, the way it really flies: over the
/// emission window the nozzle lets go of drops of mixed size (pulsing for arterial spurts, losing
/// pressure for a pour), each one on its own arc under gravity and air drag. Big drops feel less
/// drag so they lead; every drop is drawn as a small teardrop stretched along its velocity (a
/// round head and a tapered tail, longer the faster it moves) and shaded per vertex as wet liquid
/// in the shared <see cref="Wet"/> look. Drops break up as they fall, shedding smaller drops, so a
/// stream starts as a tight column and ends as a scatter of fine droplets. When a drop reaches the
/// floor or a wall it is reported as a <see cref="Landing"/> (where, how fast, how big) and taken
/// away, so the manager can splat, pool or streak it. A wound drip is one teardrop that swells,
/// stretches and lets go. All drops of all streams share one mesh and one draw call.
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
        public Transform Anchor;  // nozzle follows the wound (arterial, gush, pour)
        public Vector3 AnchorLocal;
        public bool Drip;          // hanging teardrop: Radius = bulb, Life = max length, PulseFreq = seconds per drop
        /// <summary>Side strand: a sideways acceleration (m/s^2) that pulls its drops off the main flow after DriftStart seconds of flight.</summary>
        public Vector3 Drift;
        public float DriftStart;
        /// <summary>The bleeding character's own hit colliders: blood that meets them clings and runs instead of flying through.</summary>
        public Collider[] Body;
    }

    /// <summary>A drop that let go of a drip, to be carried on as a blob.</summary>
    public struct Detach
    {
        public Vector3 Pos, Vel;
        public float Radius;
        public Color Col;
    }

    /// <summary>A drop arriving on the floor or a wall this frame.</summary>
    public struct Landing
    {
        public int Slot, Serial;
        public Vector3 Pos, Vel, WallN;
        public float Radius, Light;
        /// <summary>How fast the wound itself was moving when this drop left it (anchored streams only).</summary>
        public float SourceSpeed;
        public bool OnWall, Anchored;
        /// <summary>Set when the fluid met the character's own body: the collider it is on (WallN is the surface normal there).</summary>
        public bool OnBody;
        public Collider BodyCol;
        public Color Col;
    }

    private struct Drop
    {
        public Vector3 P, V;
        public float R, Born, Split;
        public bool Live;
    }

    private struct Stream
    {
        public Spec S;
        public float Born, Phase, NextEmit, LastRb, SourceSpeed;
        public int Seed, Cycle, Serial, Count;
        public Vector3 PrevO, LastBulb;
        public bool Live, HaveO;
    }

    private readonly Detach[] _detach = new Detach[64];
    public int DetachCount { get; private set; }
    public Detach GetDetach(int i) => _detach[i];
    public void ClearDetach() => DetachCount = 0;

    private readonly Landing[] _landing = new Landing[256];
    public int LandingCount { get; private set; }
    public Landing GetLanding(int i) => _landing[i];

    // one teardrop: front tip, front ring, back ring, tail tip
    private const int Sides = 8, DV = 2 + Sides * 2;
    private static readonly float[] Cos = new float[Sides], Sin = new float[Sides];
    private static readonly Vector3 KeyLight = new Vector3(0.3f, 1f, 0.2f).normalized;

    private readonly Mesh _mesh;
    private readonly MeshRenderer _renderer;
    private Stream[] _s;
    private Drop[] _d;
    private Vector3[] _v;
    private Color32[] _c;
    private int _per, _next, _live, _serial;
    private float _lastNow;
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

    public BloodStreams(Transform parent, Material mat, int capacity, int drops)
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
        Resize(capacity, drops);
    }

    public int Capacity => _s.Length;
    /// <summary>Drops per stream (the tier's ring count).</summary>
    public int Rings => _per;
    public int LiveCount => _live;

    public void Resize(int capacity, int drops)
    {
        capacity = Mathf.Clamp(capacity, 4, 512);
        _per = Mathf.Clamp(drops, 6, 32);
        _s = new Stream[capacity];
        _d = new Drop[capacity * _per];
        int n = capacity * _per;
        _v = new Vector3[n * DV];
        _c = new Color32[n * DV];
        var tris = new int[n * Sides * 4 * 3];
        int t = 0;
        for (int i = 0; i < n; i++)
        {
            int b = i * DV;
            for (int j = 0; j < Sides; j++)
            {
                int j1 = (j + 1) % Sides;
                int f0 = b + 1 + j, f1 = b + 1 + j1, k0 = b + 1 + Sides + j, k1 = b + 1 + Sides + j1;
                tris[t++] = b; tris[t++] = f1; tris[t++] = f0;
                tris[t++] = f0; tris[t++] = f1; tris[t++] = k1;
                tris[t++] = f0; tris[t++] = k1; tris[t++] = k0;
                tris[t++] = k0; tris[t++] = k1; tris[t++] = b + DV - 1;
            }
        }
        _mesh.Clear();
        _mesh.indexFormat = _v.Length > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        _mesh.vertices = _v;
        _mesh.colors32 = _c;
        _mesh.triangles = tris;
        _next = 0;
        _live = 0;
        LandingCount = 0;
        _renderer.enabled = false;
    }

    public void Clear()
    {
        for (int i = 0; i < _s.Length; i++)
        {
            if (!_s[i].Live) continue;
            Kill(i);
        }
        _live = 0;
        LandingCount = 0;
        _mesh.vertices = _v;
        _renderer.enabled = false;
        _wasDrawing = false;
    }

    public void Spawn(ref Spec spec, float now)
    {
        int i = _next;
        _next = (_next + 1) % _s.Length; // full: the oldest stream is reused
        if (_s[i].Live) Kill(i);
        _live++;
        ref Stream st = ref _s[i];
        st.S = spec;
        st.Born = now;
        st.Phase = Random.value * 6.283f;
        st.Seed = Random.Range(0, 100000);
        st.Cycle = 0;
        st.LastRb = 0f;
        st.NextEmit = now;
        st.Count = 0;
        st.PrevO = spec.Origin;
        st.HaveO = false;
        st.Serial = ++_serial;
        st.Live = true;
        _renderer.enabled = true;
    }

    /// <summary>Lets the manager correct a stream's floor height from a raycast where its drops are landing.</summary>
    public void SetFloor(int slot, int serial, float y)
    {
        if (slot < 0 || slot >= _s.Length || !_s[slot].Live || _s[slot].Serial != serial) return;
        _s[slot].S.FloorY = Mathf.Lerp(_s[slot].S.FloorY, y, 0.5f);
    }

    /// <summary>Seconds until a ballistic element fired at <paramref name="v"/> reaches <paramref name="floorY"/> (no drag).</summary>
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
        LandingCount = 0;
        float dt = Mathf.Clamp(now - _lastNow, 0f, 0.05f);
        _lastNow = now;
        if (_live == 0)
        {
            if (_wasDrawing) { _mesh.vertices = _v; _renderer.enabled = false; _wasDrawing = false; }
            return;
        }
        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), max = -min;
        for (int i = 0; i < _s.Length; i++)
        {
            ref Stream st = ref _s[i];
            if (!st.Live) continue;
            ref Spec sp = ref st.S;
            float age = now - st.Born;
            Vector3 inherit = Vector3.zero;
            if (sp.Anchor != null && age < sp.Emit)
            {
                sp.Origin = sp.Anchor.TransformPoint(sp.AnchorLocal);
                if (st.HaveO && dt > 1e-4f) inherit = Vector3.ClampMagnitude((sp.Origin - st.PrevO) / dt, 4f);
                st.PrevO = sp.Origin;
                st.HaveO = true;
                Vector3 flat = inherit;
                flat.y = 0f;
                st.SourceSpeed = Mathf.Lerp(st.SourceSpeed, flat.magnitude, 0.3f);
            }
            int b = i * _per;
            if (sp.Drip)
            {
                if (age >= sp.Emit) { Kill(i); continue; }
                DripDrop(ref st, i, age, camPos, ref min, ref max);
                continue;
            }

            // the nozzle lets drops go over the emission window, faster when the pressure is up
            if (age < sp.Emit)
            {
                int guard = 0;
                while (now >= st.NextEmit && guard++ < 12)
                {
                    float tau = st.NextEmit - st.Born;
                    float speed = SpeedAt(ref st, tau);
                    float interval = Mathf.Max(0.008f, sp.Emit / (_per * 2.2f) / speed);
                    st.NextEmit += interval;
                    int slot = FreeSlot(b);
                    if (slot < 0) slot = Oldest(b);
                    ref Drop d = ref _d[slot];
                    if (!d.Live) st.Count++;
                    float vmag = sp.Velocity.magnitude * speed;
                    // a burst of liquid mass, not a thread: the drops leave in a cone and spread out as they go
                    d.V = sp.Velocity * speed + inherit + Random.insideUnitSphere * (0.16f * vmag + 0.06f);
                    // mixed sizes: a few heavy drops, many smaller ones, clustering with the flow's own surges
                    float size = Mathf.Lerp(0.45f, 1.35f, Mathf.Pow(Random.value, 1.7f)) * (0.75f + 0.5f * Noise(tau * 8f, st.Seed));
                    if (st.Count == 1) size = Mathf.Max(size, 1.3f);  // the leading drop is a heavy one
                    // Radius is the flow's thickness; real drops are a few millimetres, the heaviest about a centimetre
                    d.R = Mathf.Clamp(sp.Radius * 0.4f * size, 0.0012f, 0.011f);
                    d.P = sp.Origin + d.V * (Random.value * interval) + Random.insideUnitSphere * (sp.Radius * 0.5f);
                    d.Born = now - Random.value * interval;
                    d.Split = sp.Breakup > 0f ? now + Random.Range(0.08f, 0.3f) / sp.Breakup : float.MaxValue;
                    d.Live = true;
                }
            }

            float g = 9.81f * sp.Gravity;
            Color col = sp.Col;
            for (int q = 0; q < _per; q++)
            {
                int slot = b + q;
                ref Drop d = ref _d[slot];
                if (!d.Live) continue;
                float a = now - d.Born;
                // flight: gravity, air drag (small drops feel it more), a side strand peeling away
                float k = Mathf.Clamp(0.0025f / d.R, 0.1f, 1.6f);
                d.V.y -= g * dt;
                d.V -= d.V * (k * dt);
                if (a > sp.DriftStart && sp.Drift.sqrMagnitude > 1e-8f) d.V += sp.Drift * dt;
                d.P += d.V * dt;

                bool gone = false;
                if (sp.Body != null && a > 0.03f && (d.P - sp.Origin).sqrMagnitude > 0.0036f)
                {
                    // the character's own body in the way: the drop clings to it and runs
                    if (BodyContact(sp.Body, d.P, d.R, out Vector3 bp, out Vector3 bn, out Collider bc))
                    {
                        ReportBody(i, ref st, bp, d.V, d.R, bn, bc);
                        gone = true;
                    }
                }
                if (!gone && sp.HasWall)
                {
                    // the wall is a plane found by one raycast; only drops arriving near where it was hit count as on it
                    float dist = Vector3.Dot(d.P - sp.WallP, sp.WallN);
                    if (dist < d.R && (d.P - sp.WallP).sqrMagnitude < 1f)
                    {
                        Report(i, ref st, d.P - sp.WallN * (dist - 0.002f), d.V, d.R, true);
                        gone = true;
                    }
                }
                if (!gone && d.V.y < 0f && d.P.y < sp.FloorY + d.R * 0.5f)
                {
                    Vector3 lp = d.P;
                    lp.y = sp.FloorY;
                    Report(i, ref st, lp, d.V, d.R, false);
                    gone = true;
                }
                if (!gone && a > sp.Life * 2f + 0.5f) gone = true;
                if (gone)
                {
                    d.Live = false;
                    st.Count--;
                    CollapseDrop(slot, sp.Origin);
                    continue;
                }

                // breakup: a drop in flight sheds a smaller one and both go on splitting
                if (now >= d.Split)
                {
                    d.Split = now + Random.Range(0.08f, 0.3f) / Mathf.Max(0.05f, sp.Breakup);
                    if (d.R > 0.0022f)
                    {
                        int child = FreeSlot(b);
                        if (child >= 0)
                        {
                            ref Drop c = ref _d[child];
                            c.R = d.R * 0.62f;
                            d.R *= 0.82f;
                            c.V = d.V + Random.insideUnitSphere * (0.25f + 0.12f * d.V.magnitude);
                            c.P = d.P + Random.insideUnitSphere * d.R;
                            c.Born = now;
                            c.Split = now + Random.Range(0.1f, 0.35f) / Mathf.Max(0.05f, sp.Breakup);
                            c.Live = true;
                            st.Count++;
                        }
                    }
                }

                // a teardrop along its velocity: round head, tapered tail, longer the faster it goes
                float vm = d.V.magnitude;
                Vector3 u = vm > 1e-4f ? d.V / vm : Vector3.down;
                float elong = Mathf.Clamp(1f + vm * 0.07f, 1f, 1.8f);
                float rc = d.R * Mathf.Pow(elong, -1f / 3f);
                float len = 2f * d.R * Mathf.Pow(elong, 2f / 3f);
                BuildDrop(slot, d.P, u, rc, len, col, camPos, ref min, ref max);
            }
            if (age >= sp.Emit && st.Count <= 0) { Kill(i); continue; }
        }
        _mesh.vertices = _v;
        _mesh.colors32 = _c;
        if (_live > 0 && min.x < float.MaxValue)
        {
            var bounds = new Bounds();
            bounds.SetMinMax(min, max);
            _mesh.bounds = bounds;
        }
        _wasDrawing = true;
        if (_live == 0) _renderer.enabled = false;
    }

    /// <summary>Is the point within <paramref name="r"/> of one of the body's hit colliders? Gives the surface point and normal there.</summary>
    public static bool BodyContact(Collider[] body, Vector3 p, float r, out Vector3 point, out Vector3 normal, out Collider col)
    {
        point = p;
        normal = Vector3.up;
        col = null;
        for (int i = 0; i < body.Length; i++)
        {
            Collider c = body[i];
            if (c == null || !c.enabled) continue;
            Bounds b = c.bounds;
            b.Expand(r * 2f);
            if (!b.Contains(p)) continue;
            Vector3 q = c.ClosestPoint(p);
            Vector3 d = p - q;
            float dd = d.sqrMagnitude;
            if (dd > r * r) continue;
            col = c;
            point = q;
            if (dd > 1e-8f) normal = d / Mathf.Sqrt(dd);
            else
            {
                // inside the capsule: out from its axis
                Vector3 o = p - c.bounds.center;
                o.y *= 0.2f;
                normal = o.sqrMagnitude > 1e-8f ? o.normalized : Vector3.up;
                point = p;
            }
            return true;
        }
        return false;
    }

    private void ReportBody(int slot, ref Stream st, Vector3 pos, Vector3 vel, float r, Vector3 normal, Collider col)
    {
        if (LandingCount >= _landing.Length) return;
        ref Spec sp = ref st.S;
        _landing[LandingCount++] = new Landing
        {
            Slot = slot, Serial = st.Serial, Pos = pos, Vel = vel, Radius = r, OnBody = true, BodyCol = col, WallN = normal,
            Light = sp.Light, Col = sp.Col, Anchored = sp.Anchor != null, SourceSpeed = st.SourceSpeed
        };
    }

    private void Report(int slot, ref Stream st, Vector3 pos, Vector3 vel, float r, bool wall)
    {
        if (LandingCount >= _landing.Length) return;
        ref Spec sp = ref st.S;
        _landing[LandingCount++] = new Landing
        {
            Slot = slot, Serial = st.Serial, Pos = pos, Vel = vel, Radius = r, OnWall = wall, WallN = sp.WallN, Light = sp.Light, Col = sp.Col,
            Anchored = sp.Anchor != null, SourceSpeed = st.SourceSpeed
        };
    }

    private int FreeSlot(int b)
    {
        for (int q = 0; q < _per; q++)
            if (!_d[b + q].Live) return b + q;
        return -1;
    }

    private int Oldest(int b)
    {
        int best = b;
        float born = float.MaxValue;
        for (int q = 0; q < _per; q++)
            if (_d[b + q].Born < born) { born = _d[b + q].Born; best = b + q; }
        return best;
    }

    private void Kill(int i)
    {
        ref Stream st = ref _s[i];
        if (!st.Live) return;
        st.Live = false;
        _live--;
        int b = i * _per;
        for (int q = 0; q < _per; q++)
        {
            _d[b + q].Live = false;
            CollapseDrop(b + q, st.S.Origin);
        }
        st.Count = 0;
    }

    /// <summary>Pressure at the nozzle when the drop left: decays over the window, pulses at heart rate.</summary>
    private static float SpeedAt(ref Stream st, float tau)
    {
        ref Spec sp = ref st.S;
        float speed = (1f - sp.Decay * tau / Mathf.Max(0.01f, sp.Emit)) *
                      (1f + sp.PulseDepth * Mathf.Sin(6.283f * sp.PulseFreq * tau + st.Phase));
        return Mathf.Max(0.05f, speed);
    }

    /// <summary>
    /// A drop forming on a wound: a teardrop hanging from the wound that swells, stretches
    /// and lets go; then the next one starts.
    /// </summary>
    private void DripDrop(ref Stream st, int i, float age, Vector3 camPos, ref Vector3 min, ref Vector3 max)
    {
        ref Spec sp = ref st.S;
        float period = Mathf.Max(0.3f, sp.PulseFreq);
        int cycle = (int)(age / period);
        float c = age / period - cycle;
        if (cycle != st.Cycle)
        {
            if (st.LastRb > 0f && DetachCount < _detach.Length)
                _detach[DetachCount++] = new Detach { Pos = st.LastBulb, Vel = new Vector3(0f, -0.25f, 0f), Radius = st.LastRb, Col = sp.Col };
            st.Cycle = cycle;
        }
        float e = c * c * (3f - 2f * c);
        float rb = sp.Radius * (0.3f + 0.7f * e);
        float len = Mathf.Lerp(rb * 1.6f, sp.Life, e * e * e); // hangs, then stretches fast just before letting go
        float sway = (Noise(age * 1.3f, st.Seed) - 0.5f) * 0.015f;
        Vector3 bulb = sp.Origin;
        bulb.y -= len - rb;
        bulb.x += sway;
        st.LastBulb = bulb;
        st.LastRb = rb;
        int b = i * _per;
        // head down, tail up to the wound
        BuildDrop(b, bulb, Vector3.down, rb, len, sp.Col, camPos, ref min, ref max);
        for (int q = 1; q < _per; q++) CollapseDrop(b + q, sp.Origin);
    }

    /// <summary>One teardrop with its head at +u: tip, a full ring just behind the head, a narrow ring, a tail tip.</summary>
    private void BuildDrop(int slot, Vector3 p, Vector3 u, float rc, float len, Color col, Vector3 camPos, ref Vector3 min, ref Vector3 max)
    {
        int b = slot * DV;
        Vector3 n1 = Vector3.Cross(u, Mathf.Abs(u.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
        Vector3 n2 = Vector3.Cross(u, n1);
        Vector3 toCam = camPos - p;
        toCam.Normalize();
        Vector3 h = (KeyLight + toCam).normalized;
        float tail = Mathf.Max(len - rc, rc * 1.1f);
        Vector3 pf = p + u * rc, pb = p - u * tail;
        _v[b] = pf;
        _c[b] = Shade(col, u, toCam, h);
        _v[b + DV - 1] = pb;
        _c[b + DV - 1] = Shade(col, -u, toCam, h);
        // a rounder body: the back ring sits closer to the head and keeps more of its width, so the tail is a short taper
        Vector3 cf = p + u * (rc * 0.15f), cb = p - u * (tail * 0.4f);
        float rb = rc * 0.62f;
        for (int j = 0; j < Sides; j++)
        {
            Vector3 radial = n1 * Cos[j] + n2 * Sin[j];
            _v[b + 1 + j] = cf + radial * rc;
            _c[b + 1 + j] = Shade(col, (radial * 0.9f + u * 0.35f).normalized, toCam, h);
            _v[b + 1 + Sides + j] = cb + radial * rb;
            _c[b + 1 + Sides + j] = Shade(col, (radial * 0.8f - u * 0.45f).normalized, toCam, h);
        }
        float e = Mathf.Max(rc, tail);
        min = Vector3.Min(min, p - new Vector3(e, e, e));
        max = Vector3.Max(max, p + new Vector3(e, e, e));
    }

    private static Color32 Shade(Color col, Vector3 n, Vector3 toCam, Vector3 h)
    {
        float diff = 0.3f + 0.7f * Mathf.Max(0f, Vector3.Dot(n, KeyLight) * 0.55f + Vector3.Dot(n, toCam) * 0.45f);
        float fres = 1f - Mathf.Abs(Vector3.Dot(n, toCam));
        float s = Mathf.Max(0f, Vector3.Dot(n, h));
        s *= s; s *= s; s *= s; s *= s; s *= s; // ^32: a small sharp wet glint, not plastic
        return Wet.Shade(col, diff * (1f - 0.5f * fres), s * 1.1f);
    }

    private static float Hash(int x, int seed)
    {
        unchecked
        {
            uint hh = (uint)(x * 374761393 + seed * 668265263);
            hh = (hh ^ (hh >> 13)) * 1274126177u;
            return ((hh ^ (hh >> 16)) & 0xFFFF) / 65535f;
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

    private void CollapseDrop(int slot, Vector3 at)
    {
        int b = slot * DV;
        for (int k = 0; k < DV; k++) _v[b + k] = at;
    }
}
