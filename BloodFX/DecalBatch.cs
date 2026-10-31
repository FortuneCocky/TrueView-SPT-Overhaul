using UnityEngine;
using UnityEngine.Rendering;

namespace BloodFX;

/// <summary>
/// Every splat and streak in one mesh: a ring buffer of decals drawn in a single
/// call. Each decal is a small grid whose vertices are raycast onto the surface
/// when it lands, so it bends over bumps and steps, and any part that would hang
/// in the air (past an edge or a doorway) is faded out. Full buffer = oldest
/// decal is reused. Growth and fade run at 15 Hz and the mesh is only
/// re-uploaded when something changed.
/// </summary>
internal sealed class DecalBatch
{
    private struct Decal
    {
        public Vector3 Pos, Right, Up, N;
        public float Born, Life, Size0, Size1, Grow, Aspect;
        public Color32 Col;
        public bool Live, Growing;
    }

    private const float FadeTime = 10f, TickRate = 1f / 15f, Lift = 0.006f;
    private const int Grid = 4, GV = Grid + 1, Per = GV * GV;

    private readonly int _mask;
    private float[] _depth;
    private bool[] _valid;
    private readonly Mesh _mesh;
    private readonly MeshRenderer _renderer;
    private Decal[] _d;
    private Vector3[] _v;
    private Vector2[] _uv;
    private Color32[] _c;
    private int _next, _live;
    private bool _dirtyGeom, _dirtyCol, _dirtyUv;
    private float _nextTick;

    public DecalBatch(Transform parent, Material mat, int capacity, int mask)
    {
        _mask = mask;
        var go = new GameObject("BloodFX_Decals");
        go.transform.SetParent(parent, false);
        _mesh = new Mesh { name = "BloodFX_Decals" };
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

    /// <summary>Reallocates the buffer (settings change only) and clears it.</summary>
    public void Resize(int capacity)
    {
        capacity = Mathf.Clamp(capacity, 8, 16000);
        _d = new Decal[capacity];
        _v = new Vector3[capacity * Per];
        _uv = new Vector2[capacity * Per];
        _c = new Color32[capacity * Per];
        _depth = new float[capacity * Per];
        _valid = new bool[capacity * Per];
        var tris = new int[capacity * Grid * Grid * 6];
        int t = 0;
        for (int i = 0; i < capacity; i++)
        {
            int b = i * Per;
            for (int y = 0; y < Grid; y++)
            for (int x = 0; x < Grid; x++)
            {
                int v = b + y * GV + x;
                tris[t++] = v; tris[t++] = v + GV; tris[t++] = v + GV + 1;
                tris[t++] = v; tris[t++] = v + GV + 1; tris[t++] = v + 1;
            }
        }
        _mesh.Clear();
        _mesh.indexFormat = capacity * Per > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        _mesh.vertices = _v;
        _mesh.uv = _uv;
        _mesh.colors32 = _c;
        _mesh.triangles = tris;
        _next = 0;
        _live = 0;
        _renderer.enabled = false;
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
        _dirtyGeom = true;
        _renderer.enabled = false;
    }

    /// <param name="along">Stretch direction on the surface (zero = random spin).</param>
    public void Add(Vector3 pos, Vector3 normal, Vector3 along, float size0, float size1, float grow, float aspect,
        float life, int cell, Color32 col, float now)
    {
        int i = _next;
        _next = (_next + 1) % _d.Length;
        if (!_d[i].Live) _live++;

        Vector3 right = along - normal * Vector3.Dot(along, normal);
        if (right.sqrMagnitude < 1e-4f)
        {
            right = Mathf.Abs(normal.y) < 0.95f ? Vector3.Cross(normal, Vector3.up) : Vector3.Cross(normal, Vector3.forward);
            right = Quaternion.AngleAxis(Random.value * 360f, normal) * right;
        }
        right.Normalize();
        ref Decal d = ref _d[i];
        d.Pos = pos + normal * (Lift + (i & 7) * 0.0004f);
        d.N = normal;
        d.Right = right;
        d.Up = Vector3.Cross(normal, right);
        d.Born = now;
        d.Life = life;
        d.Size0 = size0;
        d.Size1 = size1;
        d.Grow = grow;
        d.Aspect = aspect;
        d.Col = col;
        d.Live = true;
        d.Growing = grow > 0f;

        Rect r = BloodTextures.Cell(cell);
        // mirror across the travel axis only, so directional streaks keep pointing the way the blood flew
        bool flip = Random.value < 0.5f;
        float v0 = flip ? r.yMax : r.yMin, v1 = flip ? r.yMin : r.yMax;
        int b = i * Per;
        for (int y = 0; y < GV; y++)
        for (int x = 0; x < GV; x++)
        {
            float fx = (float)x / Grid, fy = (float)y / Grid;
            int k = b + y * GV + x;
            _uv[k] = new Vector2(Mathf.Lerp(r.xMin, r.xMax, fx), Mathf.Lerp(v0, v1, fy));
            // find the surface under this grid point at full size
            Vector3 q = pos + right * ((fx * 2f - 1f) * size1 * aspect) + d.Up * ((fy * 2f - 1f) * size1);
            bool ok = Physics.Raycast(q + normal * 0.08f, -normal, out RaycastHit hit, 0.2f, _mask, QueryTriggerInteraction.Ignore)
                && Vector3.Dot(hit.normal, normal) > 0.5f;
            _valid[k] = ok;
            _depth[k] = ok ? Vector3.Dot(hit.point - pos, normal) : 0f;
        }
        Place(i, d.Growing ? size0 : size1);
        SetColor(i, col);
        _dirtyGeom = _dirtyCol = _dirtyUv = true;
        _renderer.enabled = true;
    }

    public void Tick(float now)
    {
        if (_live > 0 && now >= _nextTick)
        {
            _nextTick = now + TickRate;
            for (int i = 0; i < _d.Length; i++)
            {
                ref Decal d = ref _d[i];
                if (!d.Live) continue;
                float age = now - d.Born;
                if (age >= d.Life)
                {
                    d.Live = false;
                    _live--;
                    Collapse(i);
                    _dirtyGeom = true;
                    continue;
                }
                if (d.Growing)
                {
                    float t = Mathf.Clamp01(age / d.Grow);
                    if (t >= 1f) d.Growing = false;
                    t = 1f - (1f - t) * (1f - t) * (1f - t); // fast spread, slow creep
                    Place(i, Mathf.Lerp(d.Size0, d.Size1, t));
                    _dirtyGeom = true;
                }
                float left = d.Life - age;
                if (left < FadeTime)
                {
                    Color32 c = d.Col;
                    c.a = (byte)(c.a * (left / FadeTime));
                    SetColor(i, c);
                    _dirtyCol = true;
                }
            }
            if (_live == 0) _renderer.enabled = false;
        }
        Flush();
    }

    private void Flush()
    {
        if (_dirtyGeom)
        {
            _mesh.vertices = _v;
            _mesh.bounds = LiveBounds();
            _dirtyGeom = false;
        }
        if (_dirtyUv) { _mesh.uv = _uv; _dirtyUv = false; }
        if (_dirtyCol) { _mesh.colors32 = _c; _dirtyCol = false; }
    }

    private Bounds LiveBounds()
    {
        bool any = false;
        Vector3 min = default, max = default;
        for (int i = 0; i < _d.Length; i++)
        {
            if (!_d[i].Live) continue;
            float e = _d[i].Size1 * Mathf.Max(1f, _d[i].Aspect);
            Vector3 lo = _d[i].Pos - new Vector3(e, e, e), hi = _d[i].Pos + new Vector3(e, e, e);
            if (!any) { min = lo; max = hi; any = true; }
            else { min = Vector3.Min(min, lo); max = Vector3.Max(max, hi); }
        }
        var b = new Bounds();
        if (any) b.SetMinMax(min, max);
        return b;
    }

    private void Place(int i, float size)
    {
        ref Decal d = ref _d[i];
        Vector3 rx = d.Right * (size * d.Aspect), uy = d.Up * size;
        int b = i * Per;
        for (int y = 0; y < GV; y++)
        for (int x = 0; x < GV; x++)
        {
            int k = b + y * GV + x;
            _v[k] = d.Pos + rx * ((float)x / Grid * 2f - 1f) + uy * ((float)y / Grid * 2f - 1f) + d.N * _depth[k];
        }
    }

    /// <summary>Grid points with no surface under them are see-through, so nothing hangs in the air.</summary>
    private void SetColor(int i, Color32 c)
    {
        Color32 clear = c;
        clear.a = 0;
        int b = i * Per;
        for (int k = 0; k < Per; k++) _c[b + k] = _valid[b + k] ? c : clear;
    }

    private void Collapse(int i)
    {
        int b = i * Per;
        Vector3 p = _d[i].Pos;
        for (int k = 0; k < Per; k++) _v[b + k] = p;
    }
}
