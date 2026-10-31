using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace BloodFX;

/// <summary>
/// Volumetric gore from a headshot: wrinkled, glossy lumps of brain matter and a few skull
/// chips, each a small deformed sphere flying its own ballistic arc (gravity, a little air drag,
/// tumbling). Big lumps stretch and curl into sheets the longer they fly; on contact a chunk
/// flattens onto the surface, slides a little if it hit a wall, then stays until it dries
/// away. Shaded per vertex on the CPU (folded surface, wet highlight) like the streams; all
/// chunks share one mesh and one draw call, oldest reused first.
/// </summary>
internal sealed class BrainChunks
{
    public enum Kind { Lump, Fragment, Bone }

    private struct Chunk
    {
        public Vector3 Pos, Vel, Spin, N, SlideDir;
        public Quaternion Rot;
        public float R, Born, Life, LandT, Drag, Curl, Light, NextBuild;
        public Kind K;
        public bool Live, Landed;
    }

    private const int Lat = 4, Seg = 8;
    private const int Per = 2 + Lat * Seg;   // two poles and the rings between them
    private const float FadeTime = 6f, SlideTime = 1.2f;
    private static readonly Vector3[] Unit = new Vector3[Per];
    private static readonly Color Brain = new Color(0.44f, 0.2f, 0.18f), Blood = new Color(0.34f, 0.014f, 0.012f), BoneCol = new Color(0.62f, 0.56f, 0.48f);
    private static readonly Vector3 KeyLight = new Vector3(-0.35f, 0.85f, 0.4f).normalized;

    private readonly Mesh _mesh;
    private readonly MeshRenderer _renderer;
    private readonly int _mask;
    private Chunk[] _c;
    private float[] _bump, _mix;     // per vertex: fold displacement, blood-vs-matter colour
    private Vector3[] _v;
    private Color32[] _col;
    private int _next, _live;
    private bool _drawing;

    /// <summary>A chunk hit a surface: position, surface normal, velocity at contact, chunk radius.</summary>
    public Action<Vector3, Vector3, Vector3, float> Landed;

    static BrainChunks()
    {
        Unit[0] = Vector3.up;
        Unit[Per - 1] = Vector3.down;
        for (int l = 0; l < Lat; l++)
        {
            float th = Mathf.PI * (l + 1) / (Lat + 1);
            for (int j = 0; j < Seg; j++)
            {
                float ph = Mathf.PI * 2f * (j + 0.5f * (l & 1)) / Seg;
                Unit[1 + l * Seg + j] = new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph));
            }
        }
    }

    public BrainChunks(Transform parent, Material mat, int capacity, int mask)
    {
        _mask = mask;
        var go = new GameObject("BloodFX_BrainChunks");
        go.transform.SetParent(parent, false);
        _mesh = new Mesh { name = "BloodFX_BrainChunks" };
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

    public int Capacity => _c.Length;
    public int LiveCount => _live;

    public void Resize(int capacity)
    {
        capacity = Mathf.Clamp(capacity, 16, 512);
        _c = new Chunk[capacity];
        _bump = new float[capacity * Per];
        _mix = new float[capacity * Per];
        _v = new Vector3[capacity * Per];
        _col = new Color32[capacity * Per];
        var tris = new int[capacity * Seg * Lat * 6];
        int t = 0;
        for (int i = 0; i < capacity; i++)
        {
            int b = i * Per;
            for (int j = 0; j < Seg; j++)
            {
                int j1 = (j + 1) % Seg;
                tris[t++] = b; tris[t++] = b + 1 + j1; tris[t++] = b + 1 + j;     // top cap
                int lb = b + 1 + (Lat - 1) * Seg;
                tris[t++] = b + Per - 1; tris[t++] = lb + j; tris[t++] = lb + j1; // bottom cap
            }
            for (int l = 0; l < Lat - 1; l++)
            for (int j = 0; j < Seg; j++)
            {
                // odd rings are offset half a segment, so pair each vertex with its neighbours below
                int j1 = (j + 1) % Seg;
                int a0 = b + 1 + l * Seg + j, a1 = b + 1 + l * Seg + j1;
                int c0 = b + 1 + (l + 1) * Seg + j, c1 = b + 1 + (l + 1) * Seg + j1;
                if ((l & 1) == 0) { tris[t++] = a0; tris[t++] = a1; tris[t++] = c0; tris[t++] = a1; tris[t++] = c1; tris[t++] = c0; }
                else { tris[t++] = a0; tris[t++] = c1; tris[t++] = c0; tris[t++] = a0; tris[t++] = a1; tris[t++] = c1; }
            }
        }
        _mesh.Clear();
        _mesh.indexFormat = _v.Length > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        _mesh.vertices = _v;
        _mesh.colors32 = _col;
        _mesh.triangles = tris;
        _next = 0;
        _live = 0;
        _renderer.enabled = false;
        _drawing = false;
    }

    public void Clear()
    {
        for (int i = 0; i < _c.Length; i++)
        {
            if (!_c[i].Live) continue;
            _c[i].Live = false;
            Collapse(i);
        }
        _live = 0;
        _mesh.vertices = _v;
        _renderer.enabled = false;
        _drawing = false;
    }

    /// <param name="born">When it appears (later than now for pieces shed from a lump in flight).</param>
    public void Spawn(Kind kind, Vector3 pos, Vector3 vel, float radius, float light, float born, float life)
    {
        int i = _next;
        _next = (_next + 1) % _c.Length;
        if (!_c[i].Live) _live++;
        ref Chunk c = ref _c[i];
        c.K = kind;
        c.Pos = pos;
        c.Vel = vel;
        c.R = radius;
        c.Rot = UnityEngine.Random.rotationUniform;
        c.Spin = UnityEngine.Random.onUnitSphere * UnityEngine.Random.Range(6f, kind == Kind.Lump ? 14f : 25f);
        c.Drag = kind == Kind.Lump ? 0.35f : kind == Kind.Bone ? 0.15f : 0.6f;
        c.Curl = kind == Kind.Lump ? UnityEngine.Random.Range(0.5f, 1f) : kind == Kind.Fragment ? 0.4f : 0f;
        c.Light = light;
        c.Born = born;
        c.Life = life;
        c.Landed = false;
        c.NextBuild = 0f;
        c.Live = true;

        // a wrinkled surface: a few crossing folds of random direction, sharper on brain than on bone
        int b = i * Per;
        Vector3 f1 = UnityEngine.Random.onUnitSphere, f2 = UnityEngine.Random.onUnitSphere, f3 = UnityEngine.Random.onUnitSphere;
        float p1 = UnityEngine.Random.value * 6.28f, p2 = UnityEngine.Random.value * 6.28f;
        float amp = kind == Kind.Bone ? 0.12f : 0.28f;
        for (int j = 0; j < Per; j++)
        {
            Vector3 u = Unit[j];
            float fold = Mathf.Sin(Vector3.Dot(u, f1) * 7f + p1) * 0.6f + Mathf.Sin(Vector3.Dot(u, f2) * 11f + p2) * 0.4f;
            float lobe = 0.75f + 0.5f * Mathf.Abs(Vector3.Dot(u, f3));   // irregular, not round
            _bump[b + j] = lobe * (1f + amp * fold);
            if (kind == Kind.Bone) _bump[b + j] *= Mathf.Lerp(0.35f, 1f, Mathf.Abs(Vector3.Dot(u, f3))); // flat shard
            _mix[b + j] = Mathf.Clamp01(0.5f + 0.9f * fold * (UnityEngine.Random.value - 0.25f));
        }
        if (born > Time.time) Collapse(i);
        _renderer.enabled = true;
    }

    public void Tick(float now, float dt, Vector3 eye)
    {
        if (_live == 0)
        {
            if (_drawing) { _mesh.vertices = _v; _renderer.enabled = false; _drawing = false; }
            return;
        }
        _drawing = true;
        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), max = -min;
        dt = Mathf.Min(dt, 0.05f);
        bool dirty = false;
        for (int i = 0; i < _c.Length; i++)
        {
            ref Chunk c = ref _c[i];
            if (!c.Live || now < c.Born) continue;
            float left = c.Born + c.Life - now;
            if (left <= 0f)
            {
                c.Live = false;
                _live--;
                Collapse(i);
                dirty = true;
                continue;
            }
            float age = now - c.Born;
            if (!c.Landed)
            {
                c.Vel += Physics.gravity * dt;
                c.Vel *= 1f - c.Drag * dt;
                Vector3 step = c.Vel * dt;
                float len = step.magnitude;
                if (len > 1e-5f && Physics.Raycast(c.Pos, step / len, out RaycastHit hit, len + c.R * 0.5f, _mask, QueryTriggerInteraction.Ignore))
                    Land(ref c, hit.point, hit.normal, now);
                else
                {
                    c.Pos += step;
                    c.Rot = Quaternion.AngleAxis(c.Spin.magnitude * dt * Mathf.Rad2Deg, c.Spin) * c.Rot;
                    if (age > 6f) { c.Live = false; _live--; Collapse(i); continue; } // fell out of the world
                }
            }
            else if (now - c.LandT < SlideTime && c.SlideDir != Vector3.zero)
            {
                // on a wall: the wet lump slides a little way down before it grips
                float s = 1f - (now - c.LandT) / SlideTime;
                c.Pos += c.SlideDir * (0.07f * s * s * dt);
            }
            // a chunk at rest only needs its shine and drying refreshed now and then, not every frame
            bool resting = c.Landed && now - c.LandT >= SlideTime;
            if (!resting || now >= c.NextBuild)
            {
                Build(i, now, Mathf.Clamp01(left / FadeTime), eye);
                c.NextBuild = now + 0.5f;
                dirty = true;
            }
            float r = c.R * 2.5f;
            min = Vector3.Min(min, c.Pos - new Vector3(r, r, r));
            max = Vector3.Max(max, c.Pos + new Vector3(r, r, r));
        }
        if (!dirty) return;
        _mesh.vertices = _v;
        _mesh.colors32 = _col;
        if (_live > 0 && min.x <= max.x)
        {
            var bb = new Bounds();
            bb.SetMinMax(min, max);
            _mesh.bounds = bb;
        }
    }

    private void Land(ref Chunk c, Vector3 p, Vector3 n, float now)
    {
        c.Landed = true;
        c.LandT = now;
        c.N = n;
        c.Pos = p + n * 0.002f;
        Vector3 down = Vector3.down - n * Vector3.Dot(Vector3.down, n);
        c.SlideDir = n.y < 0.6f && down.sqrMagnitude > 1e-4f ? down.normalized : Vector3.zero;
        // lies on the surface turned the way it was tumbling, not snapped upright
        Vector3 vt = c.Vel - n * Vector3.Dot(c.Vel, n);
        c.Spin = vt;
        Landed?.Invoke(p, n, c.Vel, c.R);
        c.Vel = Vector3.zero;
    }

    private void Build(int i, float now, float fade, Vector3 eye)
    {
        ref Chunk c = ref _c[i];
        int b = i * Per;
        float r = c.R * Mathf.Lerp(0.3f, 1f, fade);   // opaque geometry can't fade, so it dries and shrinks away
        float age = now - c.Born;
        Vector3 vdir = c.Vel.sqrMagnitude > 1e-4f ? c.Vel.normalized : Vector3.up;
        // big lumps stretch along their flight into curled sheets, thinning across it
        float stretch = c.Landed ? 1f : 1f + c.Curl * Mathf.Clamp01(age * 2.2f) * 1.4f;
        float thin = 1f / Mathf.Sqrt(stretch);
        Vector3 tSlide = c.Spin.sqrMagnitude > 1e-4f ? c.Spin.normalized : Vector3.zero;
        Color matter = c.K == Kind.Bone ? BoneCol : Brain;
        for (int j = 0; j < Per; j++)
        {
            Vector3 w = c.Rot * (Unit[j] * (r * _bump[b + j]));
            Vector3 nrm = c.Rot * Unit[j];
            if (!c.Landed)
            {
                float a = Vector3.Dot(w, vdir);
                Vector3 across = w - vdir * a;
                // the trailing side lags and the sheet bends: curl the across part back along the flight
                w = vdir * (a * stretch) + across * thin - vdir * (across.sqrMagnitude / Mathf.Max(r, 1e-4f) * 0.6f * c.Curl * (stretch - 1f));
            }
            else
            {
                // splatted: squashed against the surface and spread out over it, smeared the way it was going
                float h = Vector3.Dot(w, c.N);
                Vector3 flat = w - c.N * h;
                if (tSlide != Vector3.zero) flat += tSlide * (Vector3.Dot(flat, tSlide) * 0.4f);
                w = flat * (c.K == Kind.Bone ? 1f : 1.3f) + c.N * (Mathf.Abs(h) * (c.K == Kind.Bone ? 0.5f : 0.3f) + 0.0015f);
                nrm = (nrm + c.N * 1.5f).normalized;
            }
            Vector3 p = c.Pos + w;
            _v[b + j] = p;
            Vector3 toEye = (eye - p).normalized;
            float diff = 0.35f + 0.65f * Mathf.Max(0f, Vector3.Dot(nrm, KeyLight) * 0.6f + Vector3.Dot(nrm, toEye) * 0.4f);
            // the folds read through dark creases and wet, pale ridges
            float crease = Mathf.Clamp01((_bump[b + j] - 0.75f) * 1.6f);
            diff *= Mathf.Lerp(0.55f, 1.1f, crease);
            Vector3 half = (KeyLight + toEye).normalized;
            float spec = Mathf.Pow(Mathf.Max(0f, Vector3.Dot(nrm, half)), 24f) * (c.K == Kind.Bone ? 0.35f : 0.8f) * fade;
            Color baseCol = Color.Lerp(matter, Blood, _mix[b + j] * (c.K == Kind.Bone ? 0.6f : 1f));
            _col[b + j] = Wet.Shade(baseCol * c.Light, diff, spec);
        }
    }

    private void Collapse(int i)
    {
        int b = i * Per;
        Vector3 p = _c[i].Pos;
        for (int k = 0; k < Per; k++) _v[b + k] = p;
    }
}
