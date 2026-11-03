using UnityEngine;
using UnityEngine.Rendering;

namespace BloodFX;

/// <summary>
/// Blood pouring out of a wound as a rope of liquid: one continuous tube running from the
/// wound to wherever it lands. Fluid leaves the nozzle over an emission window (pulsing for
/// arterial spurts, losing pressure for a pour) and every bit of it follows its own arc under
/// gravity and a little air drag, so the rope sags and bends. Its thickness is conserved
/// mass: where the fluid speeds up the rope thins into a neck, where it slows it bulges, and
/// clumps travel down it with the flow. Past a breakup time the necks pinch off and the end
/// becomes separate beads; a few beads are flung off the rope and reported as <see cref="Detach"/>.
/// The rope ends in a rounded, heavy head while it flies; once that head reaches the floor the
/// rope stands on a flat foot there and every frame reports a <see cref="Landing"/> (where the
/// fluid is arriving, how fast, how thick) so the manager can pool, streak or run it down a wall.
/// Rings are spread by arc length along the rope and the tube is shaded per vertex on the CPU
/// (wet diffuse, rim darkening, a sharp glint) in the shared <see cref="Wet"/> look. All streams
/// share one mesh and one draw call.
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
        /// <summary>Side strand: shares the main rope's root, then this sideways acceleration (m/s^2) peels it off after DriftStart seconds of flight.</summary>
        public Vector3 Drift;
        public float DriftStart;
    }

    /// <summary>A bead that let go of a rope (or a drop that let go of a drip), to be carried on as a blob.</summary>
    public struct Detach
    {
        public Vector3 Pos, Vel;
        public float Radius;
        public Color Col;
    }

    /// <summary>Where a rope's fluid is arriving this frame.</summary>
    public struct Landing
    {
        public int Slot, Serial;
        public Vector3 Pos, Vel, WallN;
        public float Radius, Light;
        public bool OnWall;
        public Color Col;
    }

    private struct Stream
    {
        public Spec S;
        public float Born, Phase, LastRb, BeadAt, Hdt;
        public int Seed, Cycle, HistAt, Serial;
        public Vector3 LastBulb;
        public bool Live, Anchored;
    }

    private readonly Detach[] _detach = new Detach[64];
    public int DetachCount { get; private set; }
    public Detach GetDetach(int i) => _detach[i];
    public void ClearDetach() => DetachCount = 0;

    private Landing[] _landing = new Landing[4];
    public int LandingCount { get; private set; }
    public Landing GetLanding(int i) => _landing[i];

    private const int Sides = 8;
    private const float FloorLift = 0.004f;
    private static readonly float[] Cos = new float[Sides], Sin = new float[Sides];
    private static readonly Vector3 KeyLight = new Vector3(0.3f, 1f, 0.2f).normalized;

    private readonly Mesh _mesh;
    private readonly MeshRenderer _renderer;
    private Stream[] _s;
    private Vector3[] _v;
    private Color32[] _c;
    // the rope sampled along its length (0 = nozzle, last = oldest fluid), then resampled into rings by arc length
    private int _samples;
    private Vector3[] _sp, _sv;
    private float[] _sa, _st, _sr, _sbrk, _sbead, _cum;
    private Vector3[] _ring, _rvel;
    private float[] _rad;
    // anchored streams: where the nozzle was over the emission window, so each element leaves from where
    // the wound was when it was emitted and keeps the body's velocity, instead of the whole rope moving with it
    private const int HistN = 24;
    private Vector3[] _hist;
    private int _rings, _next, _live, _serial;
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
        _samples = _rings * 2;
        _sp = new Vector3[_samples];
        _sv = new Vector3[_samples];
        _sa = new float[_samples];
        _st = new float[_samples];
        _sr = new float[_samples];
        _sbrk = new float[_samples];
        _sbead = new float[_samples];
        _cum = new float[_samples];
        _ring = new Vector3[_rings];
        _rvel = new Vector3[_rings];
        _rad = new float[_rings];
        _hist = new Vector3[capacity * HistN];
        _landing = new Landing[capacity];
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
        LandingCount = 0;
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
        LandingCount = 0;
        _mesh.vertices = _v;
        _renderer.enabled = false;
        _wasDrawing = false;
    }

    public void Spawn(ref Spec spec, float now)
    {
        int i = _next;
        _next = (_next + 1) % _s.Length; // full: the oldest stream is reused
        if (!_s[i].Live) _live++;
        ref Stream st = ref _s[i];
        st.S = spec;
        st.Born = now;
        st.Phase = Random.value * 6.283f;
        st.Seed = Random.Range(0, 100000);
        st.Cycle = 0;
        st.LastRb = 0f;
        st.HistAt = 0;
        st.Hdt = Mathf.Max(0.01f, spec.Emit) / (HistN - 1);
        st.Anchored = spec.Anchor != null && !spec.Drip;
        st.BeadAt = now + Random.Range(0.1f, 0.3f);
        st.Serial = ++_serial;
        _hist[i * HistN] = spec.Origin;
        st.Live = true;
        _renderer.enabled = true;
    }

    /// <summary>Lets the manager correct a rope's floor height from a raycast where it is actually landing.</summary>
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
            int hb = i * HistN;
            if (sp.Anchor != null && age < sp.Emit)
            {
                sp.Origin = sp.Anchor.TransformPoint(sp.AnchorLocal);
                if (st.Anchored)
                {
                    int at = Mathf.Min(HistN - 1, (int)(age / st.Hdt));
                    for (int h = st.HistAt + 1; h <= at; h++) _hist[hb + h] = sp.Origin;
                    st.HistAt = Mathf.Max(st.HistAt, at);
                }
            }

            bool landed = false, onWall = false;
            if (sp.Drip)
            {
                if (age >= sp.Emit) { Kill(i); continue; }
                DripRings(ref st, age);
            }
            else
            {
                if (!RopeRings(ref st, i, age, now, out landed, out onWall)) { Kill(i); continue; }
            }

            // the tube: rings swept along the rope with a twist-free basis, shaded as wet liquid
            int b = i * per;
            Color col = sp.Col;
            Vector3 n1 = Vector3.zero;
            for (int k = 0; k < _rings; k++)
            {
                Vector3 p = _ring[k];
                Vector3 t = k < _rings - 1 ? _ring[k + 1] - p : p - _ring[k - 1];
                if (t.sqrMagnitude < 1e-10f) t = k > 0 ? _ring[k] - _ring[k - 1] : sp.Velocity;
                if (t.sqrMagnitude < 1e-10f) t = Vector3.down;
                t.Normalize();
                if (k == 0 || n1.sqrMagnitude < 1e-6f)
                    n1 = Vector3.Cross(t, Mathf.Abs(t.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                else
                {
                    n1 -= t * Vector3.Dot(n1, t);
                    if (n1.sqrMagnitude < 1e-6f) n1 = Vector3.Cross(t, Mathf.Abs(t.y) < 0.9f ? Vector3.up : Vector3.right);
                    n1.Normalize();
                }
                Vector3 n2 = Vector3.Cross(t, n1);
                float r = _rad[k];
                bool foot = !sp.Drip && landed && k == _rings - 1;
                Vector3 a1 = n1, a2 = n2;
                if (foot)
                {
                    // the rope stands on a flat foot where it meets the floor
                    a1 = Mathf.Abs(t.y) > 0.5f ? n1 : Vector3.Cross(Vector3.up, t).normalized;
                    a2 = Vector3.Cross(Vector3.up, a1);
                }
                Vector3 toCam = camPos - p;
                toCam.Normalize();
                Vector3 h = (KeyLight + toCam).normalized;
                for (int j = 0; j < Sides; j++)
                {
                    Vector3 n = a1 * Cos[j] + a2 * Sin[j];
                    Vector3 v = p + n * r;
                    _v[b + k * Sides + j] = v;
                    if (foot) n = (n * 0.4f + Vector3.up).normalized;
                    float diff = 0.3f + 0.7f * Mathf.Max(0f, Vector3.Dot(n, KeyLight) * 0.55f + Vector3.Dot(n, toCam) * 0.45f);
                    float fres = 1f - Mathf.Abs(Vector3.Dot(n, toCam));
                    float s = Mathf.Max(0f, Vector3.Dot(n, h));
                    s *= s; s *= s; s *= s; s *= s; s *= s; // ^32: a small sharp wet glint, not plastic
                    float shade = diff * (1f - 0.5f * fres);
                    _c[b + k * Sides + j] = Wet.Shade(col, shade, s * 1.1f);
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

    private void Kill(int i)
    {
        _s[i].Live = false;
        _live--;
        Collapse(i);
    }

    /// <summary>Pressure at the nozzle when the element left: decays over the window, pulses at heart rate.</summary>
    private static float SpeedAt(ref Stream st, float tau)
    {
        ref Spec sp = ref st.S;
        float speed = (1f - sp.Decay * tau / Mathf.Max(0.01f, sp.Emit)) *
                      (1f + sp.PulseDepth * Mathf.Sin(6.283f * sp.PulseFreq * tau + st.Phase));
        return Mathf.Max(0.05f, speed);
    }

    /// <summary>
    /// Where the fluid that left the nozzle at <paramref name="tau"/> is after <paramref name="a"/> seconds of
    /// flight: an arc under gravity with linear air drag (closed form), leaving from where the wound was at
    /// the time and keeping the body's velocity, wandering a little sideways the longer it flies, and for a
    /// side strand peeling away from the main rope.
    /// </summary>
    private Vector3 Pos(ref Stream st, int hb, float tau, float a, float k, float g, out Vector3 vel)
    {
        ref Spec sp = ref st.S;
        float speed = SpeedAt(ref st, tau);
        Vector3 o = sp.Origin, inherit = Vector3.zero;
        if (st.Anchored)
        {
            // recorded samples up to HistAt, then the live nozzle for the newest stretch
            float hx = tau / st.Hdt;
            int h0 = Mathf.Min((int)hx, st.HistAt);
            Vector3 a0 = _hist[hb + h0];
            Vector3 a1 = h0 < st.HistAt ? _hist[hb + h0 + 1] : sp.Origin;
            o = Vector3.Lerp(a0, a1, Mathf.Clamp01(hx - h0));
            if (h0 < st.HistAt) inherit = (a1 - a0) / st.Hdt;
            else if (st.HistAt > 0) inherit = (a0 - _hist[hb + h0 - 1]) / st.Hdt;
            inherit = Vector3.ClampMagnitude(inherit, 4f);
        }
        Vector3 v0 = sp.Velocity * speed + inherit;
        Vector3 gk = new Vector3(0f, -g / k, 0f);
        float e = Mathf.Exp(-k * a);
        Vector3 p = o + (v0 - gk) * ((1f - e) / k) + gk * a;
        vel = (v0 - gk) * e + gk;
        float wob = (Noise(tau * 9f, st.Seed + 7) - 0.5f) * 0.05f * a;
        p.x += wob * sp.Velocity.z;
        p.z -= wob * sp.Velocity.x;
        if (a > sp.DriftStart && sp.Drift.sqrMagnitude > 1e-8f)
        {
            float da = a - sp.DriftStart;
            p += sp.Drift * (0.5f * da * da);
            vel += sp.Drift * da;
        }
        return p;
    }

    private bool LandedAt(ref Stream st, int hb, float tau, float age, float k, float g)
    {
        Vector3 p = Pos(ref st, hb, tau, age - tau, k, g, out Vector3 v);
        return p.y <= st.S.FloorY + FloorLift && v.y < 0f;
    }

    /// <summary>
    /// Builds the ring positions and radii of one rope for this frame. Returns false when the rope is spent
    /// (nothing left in flight). Reports a landing while fluid is arriving on the floor or a wall.
    /// </summary>
    private bool RopeRings(ref Stream st, int i, float age, float now, out bool landed, out bool onWall)
    {
        ref Spec sp = ref st.S;
        landed = false;
        onWall = false;
        int hb = i * HistN;
        float head = Mathf.Min(age, sp.Emit);
        float tail0 = Mathf.Max(0f, age - sp.Life);
        bool allOut = age >= sp.Emit;
        if (allOut && tail0 >= sp.Emit) return false;
        float g = 9.81f * sp.Gravity;
        // thin ropes feel the air more
        float k = Mathf.Clamp(0.25f + 0.004f / Mathf.Max(0.002f, sp.Radius), 0.25f, 1.6f);

        // the landing front: the oldest fluid still in the air. Everything older has arrived.
        float tauLow = tail0;
        if (!sp.HasWall && g > 0.5f)
        {
            if (LandedAt(ref st, hb, head, age, k, g))
            {
                if (allOut) return false;
                tauLow = head;    // the nozzle itself is at the floor: just a stub
                landed = true;
            }
            else if (LandedAt(ref st, hb, tail0, age, k, g))
            {
                float lo = tail0, hi = head;
                for (int it = 0; it < 10; it++)
                {
                    float mid = 0.5f * (lo + hi);
                    if (LandedAt(ref st, hb, mid, age, k, g)) lo = mid; else hi = mid;
                }
                tauLow = lo;
                landed = true;
            }
        }
        if (allOut && tauLow >= sp.Emit - 1e-4f) return false;

        // sample the rope from the nozzle (newest) to the oldest fluid
        int M = _samples;
        float v0mag = sp.Velocity.magnitude;
        for (int m = 0; m < M; m++)
        {
            float tau = head + (tauLow - head) * m / (M - 1);
            float a = Mathf.Max(0f, age - tau);
            _st[m] = tau;
            _sa[m] = a;
            _sp[m] = Pos(ref st, hb, tau, a, k, g, out _sv[m]);
        }
        // a wall in the way: fluid flattens onto it and runs down from where it hit
        if (sp.HasWall)
        {
            bool hit = false;
            float aW = 0f;
            Vector3 down = Vector3.down - sp.WallN * Vector3.Dot(Vector3.down, sp.WallN);
            if (down.sqrMagnitude > 1e-4f) down.Normalize(); else down = Vector3.zero;
            for (int m = 0; m < M; m++)
            {
                float d = Vector3.Dot(_sp[m] - sp.WallP, sp.WallN);
                if (!hit && d < 0.006f)
                {
                    hit = true;
                    aW = _sa[m];
                    if (m > 0)
                    {
                        float dp = Vector3.Dot(_sp[m - 1] - sp.WallP, sp.WallN);
                        float f = Mathf.Clamp01((dp - 0.006f) / Mathf.Max(1e-4f, dp - d));
                        aW = Mathf.Lerp(_sa[m - 1], _sa[m], f);
                    }
                }
                if (hit)
                {
                    Vector3 p = _sp[m] - sp.WallN * (d - 0.006f);
                    float run = Mathf.Max(0f, _sa[m] - aW);
                    p += down * (0.22f * run);
                    _sp[m] = p;
                    _sv[m] = down * 0.22f;
                }
            }
            if (hit && Vector3.Dot(_sp[M - 1] - sp.WallP, sp.WallN) < 0.01f) onWall = true;
        }
        for (int m = 0; m < M; m++)
            if (_sp[m].y < sp.FloorY + FloorLift) _sp[m].y = sp.FloorY + FloorLift;

        // thickness: the flow's own clumps, thinned where the rope is stretched, beading past breakup
        float cum = 0f;
        for (int m = 0; m < M; m++)
        {
            float tau = _st[m], a = _sa[m];
            float speed = SpeedAt(ref st, tau);
            float clump = 0.7f + 0.6f * Noise(tau * 8f, st.Seed);
            float r = sp.Radius * Mathf.Sqrt(speed) * clump / (1f + 0.35f * a);
            if (m > 0)
            {
                float ds = (_sp[m] - _sp[m - 1]).magnitude;
                cum += ds;
                float dtau = _st[m - 1] - _st[m];
                float vn = v0mag * speed;
                if (dtau > 1e-4f && vn > 0.05f)
                {
                    float stretch = ds / (dtau * vn);
                    r /= Mathf.Sqrt(Mathf.Clamp(stretch, 0.7f, 5f));
                }
            }
            float brk = sp.Breakup > 0f ? Mathf.Clamp01((a - 0.2f) * sp.Breakup * 1.5f) : 0f;
            float bead = 1f;
            if (brk > 0f)
            {
                bead = Mathf.Clamp01((Noise(tau * 16f, st.Seed + 3) - 0.42f) * 2.8f);
                r *= Mathf.Lerp(1f, bead * 1.4f, brk);
            }
            _sbrk[m] = brk;
            _sbead[m] = bead;
            _sr[m] = r;
            _cum[m] = cum;
        }

        // beads flung off: necks that are pinching off, and the heavy head when it flies fast
        if (now >= st.BeadAt && DetachCount < _detach.Length)
        {
            st.BeadAt = now + Random.Range(0.12f, 0.35f);
            int pick = -1;
            for (int tries = 0; tries < 3 && pick < 0; tries++)
            {
                int m = Random.Range(1, M);
                if (_sbrk[m] > 0.3f && _sbead[m] < 0.35f) pick = m;
            }
            if (pick < 0 && !landed && !onWall && _sv[M - 1].magnitude > 2.5f && Random.value < 0.35f) pick = M - 1;
            if (pick >= 0 && _sp[pick].y > sp.FloorY + 0.02f)
            {
                float br = Mathf.Clamp(sp.Radius * Random.Range(0.35f, 0.7f), 0.002f, 0.012f);
                _detach[DetachCount++] = new Detach
                {
                    Pos = _sp[pick], Vel = _sv[pick] + Random.insideUnitSphere * 0.35f, Radius = br, Col = sp.Col
                };
            }
        }

        // rings by arc length: the last ring is a cap (flat foot on the floor, rounded head in the air)
        int body = _rings - 1;
        float L = _cum[M - 1];
        int at = 0;
        for (int kk = 0; kk < body; kk++)
        {
            float s = L * kk / Mathf.Max(1, body - 1);
            while (at < M - 2 && _cum[at + 1] < s) at++;
            float seg = _cum[at + 1] - _cum[at];
            float f = seg > 1e-6f ? Mathf.Clamp01((s - _cum[at]) / seg) : 0f;
            _ring[kk] = Vector3.Lerp(_sp[at], _sp[at + 1], f);
            _rvel[kk] = Vector3.Lerp(_sv[at], _sv[at + 1], f);
            _rad[kk] = Mathf.Lerp(_sr[at], _sr[at + 1], f);
        }
        _rad[0] *= 0.75f;   // the neck at the wound
        float rTail = _sr[M - 1];
        Vector3 pTail = _sp[M - 1];
        if (landed)
        {
            _rad[body - 1] = Mathf.Max(_rad[body - 1], rTail * 1.15f);
            Vector3 foot = pTail;
            foot.y = sp.FloorY + 0.003f;
            if (_ring[body - 1].y < foot.y + rTail) _ring[body - 1].y = foot.y + rTail;
            _ring[body] = foot;
            _rad[body] = rTail * 1.7f;
        }
        else if (onWall)
        {
            _ring[body] = pTail;
            _rad[body] = rTail * 0.5f;
        }
        else
        {
            // the heavy rounded head leading the rope
            _rad[body - 1] = Mathf.Max(_rad[body - 1], rTail * 1.3f);
            Vector3 dir = _sv[M - 1].sqrMagnitude > 1e-6f ? _sv[M - 1].normalized : Vector3.down;
            _ring[body] = pTail + dir * (rTail * 0.9f);
            _rad[body] = 0.0005f;
        }

        if ((landed || onWall) && LandingCount < _landing.Length)
        {
            Vector3 lp = pTail;
            if (landed) lp.y = sp.FloorY;
            _landing[LandingCount++] = new Landing
            {
                Slot = i, Serial = st.Serial, Pos = lp, Vel = landed ? _sv[M - 1] : _rvel[body - 1], WallN = sp.WallN,
                Radius = rTail, Light = sp.Light, OnWall = onWall && !landed, Col = sp.Col
            };
        }
        return true;
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
                _detach[DetachCount++] = new Detach { Pos = st.LastBulb, Vel = new Vector3(0f, -0.25f, 0f), Radius = st.LastRb, Col = sp.Col };
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
