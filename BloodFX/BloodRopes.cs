using UnityEngine;
using UnityEngine.Rendering;

namespace BloodFX;

/// <summary>
/// Blood pouring out of a wound as a thick rope of liquid: one continuous glossy tube from the
/// wound to the floor, lumpy along its length and around its girth, sagging under gravity with a
/// little air drag. Fluid leaves the nozzle over an emission window (pulsing for arterial spurts,
/// losing pressure for a pour) and every bit of it follows its own arc, so the rope bends and its
/// bulges travel down with the flow. It only thins a little as it falls (this is the heavy,
/// viscous look, not a thread); past the breakup time the end of the rope pinches into rounded
/// drips and a few beads let go, reported as <see cref="BloodStreams.Detach"/>. The rope ends in a
/// heavy rounded head while it falls and stands on a flat foot once it reaches the floor, and every
/// frame reports a <see cref="BloodStreams.Landing"/> (where the fluid is arriving, how fast, how
/// thick) for the pool and splash there. Rings are spread by arc length and shaded per vertex on the
/// CPU in the shared <see cref="Wet"/> look. All ropes share one mesh and one draw call.
/// Fast jets and bursts are not ropes; they are showers of drops (<see cref="BloodStreams"/>).
/// </summary>
internal sealed class BloodRopes
{
    private struct Rope
    {
        public BloodStreams.Spec S;
        public float Born, Phase, BeadAt, Hdt, SourceSpeed;
        public int Seed, HistAt, Serial;
        public bool Live;
    }

    private readonly BloodStreams.Detach[] _detach = new BloodStreams.Detach[32];
    public int DetachCount { get; private set; }
    public BloodStreams.Detach GetDetach(int i) => _detach[i];
    public void ClearDetach() => DetachCount = 0;

    private BloodStreams.Landing[] _landing = new BloodStreams.Landing[4];
    public int LandingCount { get; private set; }
    public BloodStreams.Landing GetLanding(int i) => _landing[i];

    /// <summary>
    /// A rope whose wound moved too fast for a liquid thread to hold together: the part in the air falls free,
    /// and the rest of the flow should go on as a shower of drops from the same wound (the manager spawns it).
    /// </summary>
    private readonly BloodStreams.Spec[] _handoff = new BloodStreams.Spec[16];
    public int HandoffCount { get; private set; }
    public BloodStreams.Spec GetHandoff(int i) => _handoff[i];
    public void ClearHandoff() => HandoffCount = 0;
    private const float BreakSpeed = 0.7f, SnapSpeed = 2f;   // wound speed that tears the rope: sustained, or in one jerk

    private const int Sides = 10;
    private const float FloorLift = 0.004f;
    private static readonly float[] Cos = new float[Sides], Sin = new float[Sides];
    private static readonly Vector3 KeyLight = new Vector3(0.3f, 1f, 0.2f).normalized;

    private readonly Mesh _mesh;
    private readonly MeshRenderer _renderer;
    private Rope[] _s;
    private Vector3[] _v;
    private Color32[] _c;
    // the rope sampled along its length (0 = nozzle, last = oldest fluid), then resampled into rings by arc length
    private int _samples;
    private Vector3[] _sp, _sv;
    private float[] _sa, _st, _sr, _sbrk, _sbead, _cum;
    private Vector3[] _ring, _rvel;
    private float[] _rad, _rtau;
    // anchored ropes: where the nozzle was over the emission window, so each element leaves from where
    // the wound was when it was emitted and keeps the body's velocity, instead of the whole rope moving with it
    private const int HistN = 24;
    private Vector3[] _hist;
    private int _rings, _next, _live, _serial;
    private bool _wasDrawing;

    static BloodRopes()
    {
        for (int j = 0; j < Sides; j++)
        {
            float a = j * Mathf.PI * 2f / Sides;
            Cos[j] = Mathf.Cos(a);
            Sin[j] = Mathf.Sin(a);
        }
    }

    public BloodRopes(Transform parent, Material mat, int capacity, int rings)
    {
        var go = new GameObject("BloodFX_Ropes");
        go.transform.SetParent(parent, false);
        _mesh = new Mesh { name = "BloodFX_Ropes" };
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
        capacity = Mathf.Clamp(capacity, 4, 256);
        _rings = Mathf.Clamp(rings, 8, 32);
        _s = new Rope[capacity];
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
        _rtau = new float[_rings];
        _hist = new Vector3[capacity * HistN];
        _landing = new BloodStreams.Landing[capacity];
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
        DetachCount = 0;
        HandoffCount = 0;
        _mesh.vertices = _v;
        _renderer.enabled = false;
        _wasDrawing = false;
    }

    public void Spawn(ref BloodStreams.Spec spec, float now)
    {
        int i = _next;
        _next = (_next + 1) % _s.Length; // full: the oldest rope is reused
        if (!_s[i].Live) _live++;
        ref Rope st = ref _s[i];
        st.S = spec;
        st.Born = now;
        st.Phase = Random.value * 6.283f;
        st.Seed = Random.Range(0, 100000);
        st.HistAt = 0;
        st.Hdt = Mathf.Max(0.01f, spec.Emit) / (HistN - 1);
        st.BeadAt = now + Random.Range(0.2f, 0.5f);
        st.SourceSpeed = 0f;
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
            ref Rope st = ref _s[i];
            if (!st.Live) continue;
            ref BloodStreams.Spec sp = ref st.S;
            float age = now - st.Born;
            int hb = i * HistN;
            if (sp.Anchor != null && age < sp.Emit)
            {
                Vector3 prevO = sp.Origin;
                sp.Origin = sp.Anchor.TransformPoint(sp.AnchorLocal);
                int at = Mathf.Min(HistN - 1, (int)(age / st.Hdt));
                for (int h = st.HistAt + 1; h <= at; h++) _hist[hb + h] = sp.Origin;
                st.HistAt = Mathf.Max(st.HistAt, at);
                Vector3 flat = sp.Origin - prevO;
                flat.y = 0f;
                float dt = Time.deltaTime;
                float inst = dt > 1e-4f ? Mathf.Min(6f, flat.magnitude / dt) : 0f;
                if (dt > 1e-4f) st.SourceSpeed = Mathf.Lerp(st.SourceSpeed, inst, 0.3f);
                // a liquid thread can't follow a wound that turns or whips away: it tears off. What is in the air
                // falls free as one piece, and the rest of the flow carries on as drops that scatter from the wound.
                if (age > 0.08f && (st.SourceSpeed > BreakSpeed || inst > SnapSpeed) && HandoffCount < _handoff.Length)
                {
                    BloodStreams.Spec rest = sp;
                    float left = sp.Emit - age;
                    rest.Emit = left;
                    rest.Velocity = sp.Velocity * SpeedAt(ref st, age);
                    rest.Decay = Mathf.Clamp01(sp.Decay * left / Mathf.Max(0.01f, sp.Emit));
                    rest.Breakup = Mathf.Max(sp.Breakup, 1.2f);
                    rest.Drift = Vector3.zero;
                    _handoff[HandoffCount++] = rest;
                    sp.Emit = age;          // the rope stops here and lets go of the wound
                }
            }

            if (!RopeRings(ref st, i, age, now, out bool landed, out bool onWall)) { Kill(i); continue; }

            // the tube: rings swept along the rope with a twist-free basis, a lumpy girth, shaded as wet liquid
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
                bool foot = landed && k == _rings - 1;
                bool cap = k == _rings - 1 && !landed;
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
                float tau = _rtau[k];
                for (int j = 0; j < Sides; j++)
                {
                    Vector3 n = a1 * Cos[j] + a2 * Sin[j];
                    // lumpy girth: slow bulges that travel with the fluid, different on every side
                    float lump = cap || foot ? 1f : 1f + 0.5f * (Noise(tau * 5f + j * 2.3f, st.Seed + 17 * j) - 0.5f);
                    Vector3 v = p + n * (r * lump);
                    _v[b + k * Sides + j] = v;
                    if (foot) n = (n * 0.4f + Vector3.up).normalized;
                    float diff = 0.3f + 0.7f * Mathf.Max(0f, Vector3.Dot(n, KeyLight) * 0.55f + Vector3.Dot(n, toCam) * 0.45f);
                    float fres = 1f - Mathf.Abs(Vector3.Dot(n, toCam));
                    float s = Mathf.Max(0f, Vector3.Dot(n, h));
                    s *= s; s *= s; s *= s; s *= s; s *= s; // ^32: a sharp wet glint
                    float shade = diff * (1f - 0.45f * fres);
                    _c[b + k * Sides + j] = Wet.Shade(col, shade, s * 1.6f + fres * fres * 0.15f);
                    min = Vector3.Min(min, v);
                    max = Vector3.Max(max, v);
                }
            }
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

    private void Kill(int i)
    {
        _s[i].Live = false;
        _live--;
        Collapse(i);
    }

    /// <summary>Pressure at the nozzle when the element left: decays over the window, pulses at heart rate.</summary>
    private static float SpeedAt(ref Rope st, float tau)
    {
        ref BloodStreams.Spec sp = ref st.S;
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
    private Vector3 Pos(ref Rope st, int hb, float tau, float a, float k, float g, out Vector3 vel)
    {
        ref BloodStreams.Spec sp = ref st.S;
        float speed = SpeedAt(ref st, tau);
        Vector3 o = sp.Origin, inherit = Vector3.zero;
        if (sp.Anchor != null)
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
        float wob = (Noise(tau * 7f, st.Seed + 7) - 0.5f) * 0.06f * a;
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

    private bool LandedAt(ref Rope st, int hb, float tau, float age, float k, float g)
    {
        Vector3 p = Pos(ref st, hb, tau, age - tau, k, g, out Vector3 v);
        return p.y <= st.S.FloorY + FloorLift && v.y < 0f;
    }

    /// <summary>
    /// Builds the ring positions and radii of one rope for this frame. Returns false when the rope is spent
    /// (nothing left in flight). Reports a landing while fluid is arriving on the floor or a wall.
    /// </summary>
    private bool RopeRings(ref Rope st, int i, float age, float now, out bool landed, out bool onWall)
    {
        ref BloodStreams.Spec sp = ref st.S;
        landed = false;
        onWall = false;
        int hb = i * HistN;
        float head = Mathf.Min(age, sp.Emit);
        float tail0 = Mathf.Max(0f, age - sp.Life);
        bool allOut = age >= sp.Emit;
        if (allOut && tail0 >= sp.Emit) return false;
        float g = 9.81f * sp.Gravity;
        float k = Mathf.Clamp(0.25f + 0.003f / Mathf.Max(0.003f, sp.Radius), 0.25f, 1.2f);

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
                    p += down * (0.22f * Mathf.Max(0f, _sa[m] - aW));
                    _sp[m] = p;
                    _sv[m] = down * 0.22f;
                }
            }
            if (hit && Vector3.Dot(_sp[M - 1] - sp.WallP, sp.WallN) < 0.01f) onWall = true;
        }
        // the character's own body in the way: the rope ends there and the blood runs down the body instead
        bool onBody = false;
        Vector3 bodyN = Vector3.up;
        Collider bodyCol = null;
        if (sp.Body != null && !landed && !onWall)
        {
            for (int m = 1; m < M; m += 2)
            {
                if (_sa[m] < 0.03f || (_sp[m] - sp.Origin).sqrMagnitude < 0.0036f) continue;
                if (!BloodStreams.BodyContact(sp.Body, _sp[m], sp.Radius * 0.8f, out Vector3 bp, out Vector3 bn, out Collider bc)) continue;
                if (allOut && m <= 1) return false;   // the last of it has run onto the body
                onBody = true;
                bodyN = bn;
                bodyCol = bc;
                Vector3 bv = _sv[m];
                for (int mm = m; mm < M; mm++) { _sp[mm] = bp; _sv[mm] = bv; }
                break;
            }
        }
        for (int m = 0; m < M; m++)
            if (_sp[m].y < sp.FloorY + FloorLift) _sp[m].y = sp.FloorY + FloorLift;

        // thickness: heavy and lumpy, bulges travelling with the flow, only a little thinner where it is
        // stretched; past breakup the far end pinches into rounded drips
        float cum = 0f;
        for (int m = 0; m < M; m++)
        {
            float tau = _st[m], a = _sa[m];
            float f = m / (float)(M - 1);
            float speed = SpeedAt(ref st, tau);
            float clump = 0.8f + 0.5f * Noise(tau * 5f, st.Seed);
            float r = sp.Radius * Mathf.Sqrt(speed) * clump / (1f + 0.15f * a);
            if (m > 0)
            {
                float ds = (_sp[m] - _sp[m - 1]).magnitude;
                cum += ds;
                float dtau = _st[m - 1] - _st[m];
                float vn = v0mag * speed;
                if (dtau > 1e-4f && vn > 0.05f)
                {
                    float stretch = ds / (dtau * vn);
                    r *= Mathf.Clamp(1f / Mathf.Sqrt(Mathf.Max(0.3f, stretch)), 0.7f, 1.15f);
                }
            }
            float brk = sp.Breakup > 0f ? Mathf.Clamp01((a - 0.3f) * sp.Breakup) : 0f;
            float bead = 1f;
            if (brk > 0f)
            {
                bead = Mathf.Clamp01((Noise(tau * 12f, st.Seed + 3) - 0.3f) * 2.2f);
                // the body of the rope stays joined through its necks; only the far end can pinch apart
                float floor = f > 0.75f ? 0f : 0.3f;
                r *= Mathf.Lerp(1f, Mathf.Max(floor, bead) * 1.3f, brk);
            }
            _sbrk[m] = brk;
            _sbead[m] = bead;
            _sr[m] = r;
            _cum[m] = cum;
        }

        // beads let go: from necks that are pinching off, and now and then from the heavy head
        if (now >= st.BeadAt && DetachCount < _detach.Length)
        {
            st.BeadAt = now + Random.Range(0.25f, 0.6f);
            int pick = -1;
            for (int tries = 0; tries < 3 && pick < 0; tries++)
            {
                int m = Random.Range(M / 2, M);
                if (_sbrk[m] > 0.3f && _sbead[m] < 0.35f) pick = m;
            }
            if (pick < 0 && !landed && !onWall && Random.value < 0.3f) pick = M - 1;
            if (pick >= 0 && _sp[pick].y > sp.FloorY + 0.02f)
            {
                float br = Mathf.Clamp(sp.Radius * Random.Range(0.25f, 0.45f), 0.002f, 0.009f);
                _detach[DetachCount++] = new BloodStreams.Detach
                {
                    Pos = _sp[pick], Vel = _sv[pick] + Random.insideUnitSphere * 0.4f, Radius = br, Col = sp.Col
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
            float fr = seg > 1e-6f ? Mathf.Clamp01((s - _cum[at]) / seg) : 0f;
            _ring[kk] = Vector3.Lerp(_sp[at], _sp[at + 1], fr);
            _rvel[kk] = Vector3.Lerp(_sv[at], _sv[at + 1], fr);
            _rad[kk] = Mathf.Lerp(_sr[at], _sr[at + 1], fr);
            _rtau[kk] = Mathf.Lerp(_st[at], _st[at + 1], fr);
        }
        _rad[0] *= 0.8f;   // the neck at the wound
        float rTail = _sr[M - 1];
        Vector3 pTail = _sp[M - 1];
        _rtau[body] = _st[M - 1];
        if (landed)
        {
            _rad[body - 1] = Mathf.Max(_rad[body - 1], rTail * 1.2f);
            Vector3 foot = pTail;
            foot.y = sp.FloorY + 0.003f;
            if (_ring[body - 1].y < foot.y + rTail) _ring[body - 1].y = foot.y + rTail;
            _ring[body] = foot;
            _rad[body] = rTail * 1.9f;
        }
        else if (onBody)
        {
            // spreads a little where it meets the body
            _rad[body - 1] = Mathf.Max(_rad[body - 1], rTail * 1.2f);
            _ring[body] = pTail + bodyN * 0.004f;
            _rad[body] = rTail * 1.5f;
        }
        else if (onWall)
        {
            _ring[body] = pTail;
            _rad[body] = rTail * 0.5f;
        }
        else
        {
            // the heavy rounded head leading the rope
            _rad[body - 1] = Mathf.Max(_rad[body - 1], rTail * 1.45f);
            Vector3 dir = _sv[M - 1].sqrMagnitude > 1e-6f ? _sv[M - 1].normalized : Vector3.down;
            _ring[body] = pTail + dir * (rTail * 1.1f);
            _rad[body] = 0.0005f;
        }

        if ((landed || onWall || onBody) && LandingCount < _landing.Length)
        {
            Vector3 lp = pTail;
            if (landed) lp.y = sp.FloorY;
            _landing[LandingCount++] = new BloodStreams.Landing
            {
                Slot = i, Serial = st.Serial, Pos = lp, Vel = landed ? _sv[M - 1] : _rvel[body - 1], WallN = onBody ? bodyN : sp.WallN,
                Radius = rTail, Light = sp.Light, OnWall = onWall && !landed, OnBody = onBody, BodyCol = bodyCol, Col = sp.Col,
                Anchored = true, SourceSpeed = st.SourceSpeed
            };
        }
        return true;
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
