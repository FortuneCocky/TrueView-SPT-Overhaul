using UnityEngine;
using UnityEngine.Rendering;

namespace BloodFX;

/// <summary>
/// Blood running down a body. When a pour or a heavy drop meets the character's own body instead of the
/// floor, the liquid clings to the surface and runs down it: a raised glossy bead with a wet trail behind
/// it, crawling downhill over the body part's hit capsule, slowing as it thins, and dripping off where the
/// surface turns under (the bottom of a limb, the hem of a jacket). The trail is stored in the body part's
/// own space, so it rides with animation and ragdoll. All runs share one mesh and one draw call.
/// </summary>
internal sealed class BodyRuns
{
    private struct Run
    {
        public Transform Bone;
        public Collider Col;
        public float Width, Speed, Born, Life, Length, LastGrow, Lift, SinceLift;
        public int Count;
        public Color Col32;
        public bool Live, Moving;
    }

    private const int Samples = 12, Across = 5, Per = Samples * Across;
    private const float DefaultLift = 0.035f, Pull = 0.006f, FadeTime = 8f, Step = 0.02f;

    /// <summary>How far out from a point on a hit collider the visible surface is along the normal (-1 = unknown right now).</summary>
    public System.Func<Collider, Vector3, Vector3, float> LiftAt;
    private static readonly float[] AcrossF = { -1f, -0.5f, 0f, 0.5f, 1f };
    private static readonly float[] AcrossH = { 0f, 0.75f, 1f, 0.75f, 0f };
    private static readonly float[] AcrossTilt = { -1.4f, -0.5f, 0f, 0.5f, 1.4f };
    private static readonly Vector3 KeyLight = new Vector3(0.3f, 1f, 0.2f).normalized;

    private readonly BloodStreams.Detach[] _detach = new BloodStreams.Detach[32];
    public int DetachCount { get; private set; }
    public BloodStreams.Detach GetDetach(int i) => _detach[i];
    public void ClearDetach() => DetachCount = 0;

    private readonly Mesh _mesh;
    private readonly MeshRenderer _renderer;
    private Run[] _r;
    private Vector3[] _lp, _ln;   // samples in the bone's local space: position, surface normal
    private float[] _ll;          // how far out the visible surface is at each sample
    private Vector3[] _v;
    private Color32[] _c;
    private int _next, _live;

    public BodyRuns(Transform parent, Material mat, int capacity)
    {
        var go = new GameObject("BloodFX_BodyRuns");
        go.transform.SetParent(parent, false);
        _mesh = new Mesh { name = "BloodFX_BodyRuns" };
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

    public int Capacity => _r.Length;
    public int LiveCount => _live;

    public void Resize(int capacity)
    {
        capacity = Mathf.Clamp(capacity, 8, 256);
        _r = new Run[capacity];
        _lp = new Vector3[capacity * Samples];
        _ln = new Vector3[capacity * Samples];
        _ll = new float[capacity * Samples];
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
        for (int i = 0; i < _r.Length; i++) _r[i].Live = false;
        for (int i = 0; i < _v.Length; i++) _v[i] = Vector3.zero;
        _live = 0;
        DetachCount = 0;
        _mesh.vertices = _v;
        _renderer.enabled = false;
    }

    /// <summary>Blood arriving on a body part: starts a run at that point of its hit collider (a still bead if <paramref name="speed"/> is 0).</summary>
    public void Begin(Collider col, Vector3 pos, Vector3 normal, float width, float speed, Color c, float life, float now, float lift = -1f)
    {
        if (col == null) return;
        int i = _next;
        _next = (_next + 1) % _r.Length;
        if (!_r[i].Live) _live++;
        ref Run r = ref _r[i];
        r.Bone = col.transform;
        r.Col = col;
        r.Width = width;
        r.Speed = speed;
        r.Born = now;
        r.LastGrow = now;
        r.Life = life;
        r.Length = 0f;
        r.Col32 = c;
        r.Live = true;
        r.Moving = speed > 0f;
        r.Lift = lift >= 0f ? lift : DefaultLift;
        r.SinceLift = 0f;
        int b = i * Samples;
        _lp[b] = r.Bone.InverseTransformPoint(pos);
        _ln[b] = r.Bone.InverseTransformDirection(normal.normalized);
        _ll[b] = r.Lift;
        r.Count = 1;
        _renderer.enabled = true;
    }

    public void Tick(float now, float dt, Vector3 eye, bool haveEye)
    {
        if (_live == 0) return;
        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), max = -min;
        bool any = false;
        for (int i = 0; i < _r.Length; i++)
        {
            ref Run r = ref _r[i];
            if (!r.Live) continue;
            int v = i * Per, b = i * Samples;
            float left = r.Born + r.Life - now;
            if (r.Bone == null || r.Col == null || left <= 0f)
            {
                r.Live = false;
                _live--;
                for (int k = 0; k < Per; k++) _v[v + k] = Vector3.zero;
                continue;
            }
            if (!r.Bone.gameObject.activeInHierarchy)
            {
                for (int k = 0; k < Per; k++) _v[v + k] = Vector3.zero;
                continue;
            }
            if (r.Moving) Advance(ref r, b, now, dt);
            Build(ref r, v, b, left < FadeTime ? left / FadeTime : 1f, eye, haveEye, ref min, ref max);
            any = true;
        }
        _mesh.vertices = _v;
        _mesh.colors32 = _c;
        _renderer.enabled = any;
        if (any && min.x <= max.x)
        {
            var bb = new Bounds();
            bb.SetMinMax(min, max);
            _mesh.bounds = bb;
        }
    }

    /// <summary>The head crawls downhill over the collider's surface, slowing as the run thins; drips off where the surface turns under.</summary>
    private void Advance(ref Run r, int b, float now, float dt)
    {
        int last = b + r.Count - 1;
        Vector3 head = r.Bone.TransformPoint(_lp[last]);
        Vector3 n = r.Bone.TransformDirection(_ln[last]);
        // thins and slows with distance and time; stops after a while and just stays
        float age = now - r.Born;
        float speed = r.Speed * Mathf.Clamp01(1f - age / 3.5f) * Mathf.Clamp01(1f - r.Length / 0.45f);
        if (speed < 0.005f) { r.Moving = false; return; }
        Vector3 down = Vector3.down - n * Vector3.Dot(Vector3.down, n);
        if (n.y < -0.3f || down.sqrMagnitude < 0.04f)
        {
            // the surface turns under the run (the bottom of a limb): the bead lets go
            DripOff(ref r, head, n);
            return;
        }
        down.Normalize();
        Vector3 cand = head + down * (speed * dt) + n * 0.012f;
        Vector3 proj = r.Col.ClosestPoint(cand);
        Vector3 nn = cand - proj;
        if (nn.sqrMagnitude < 1e-8f)
        {
            // inside the collider: keep the old normal and slide the point back out to the surface
            nn = n;
            proj = cand - n * 0.012f;
        }
        else nn.Normalize();
        float moved = (proj - head).magnitude;
        if (moved < speed * dt * 0.25f || proj.y > head.y - speed * dt * 0.1f)
        {
            // it can't get any lower on this part: the blood gathers and lets go as a drop
            DripOff(ref r, head, n);
            return;
        }
        r.Length += moved;
        r.SinceLift += moved;
        // the clothing surface above the capsule changes along the way (a rig, a belt, bare arm): re-measure every few cm
        if (r.SinceLift > 0.05f && LiftAt != null)
        {
            float l = LiftAt(r.Col, proj, nn);
            if (l >= 0f) { r.Lift = l; r.SinceLift = 0f; }
        }
        Vector3 lpos = r.Bone.InverseTransformPoint(proj), lnorm = r.Bone.InverseTransformDirection(nn);
        if (r.Count >= 2 && (lpos - _lp[last - 1]).magnitude < Step)
        {
            // still within the last step: the head just moves on
            _lp[last] = lpos;
            _ln[last] = lnorm;
            _ll[last] = r.Lift;
            return;
        }
        if (r.Count < Samples)
        {
            _lp[b + r.Count] = lpos;
            _ln[b + r.Count] = lnorm;
            _ll[b + r.Count] = r.Lift;
            r.Count++;
        }
        else
        {
            // the trail behind is as long as it gets: the tail end dries off as the head goes on
            for (int k = 0; k < Samples - 1; k++) { _lp[b + k] = _lp[b + k + 1]; _ln[b + k] = _ln[b + k + 1]; _ll[b + k] = _ll[b + k + 1]; }
            _lp[b + Samples - 1] = lpos;
            _ln[b + Samples - 1] = lnorm;
            _ll[b + Samples - 1] = r.Lift;
        }
    }

    private void DripOff(ref Run r, Vector3 head, Vector3 n)
    {
        r.Moving = false;
        if (DetachCount >= _detach.Length) return;
        _detach[DetachCount++] = new BloodStreams.Detach
        {
            Pos = head + n * (r.Lift + 0.01f), Vel = new Vector3(0f, -0.3f, 0f) + n * 0.1f, Radius = Mathf.Clamp(r.Width * 0.45f, 0.002f, 0.007f), Col = r.Col32
        };
    }

    private void Build(ref Run r, int v, int b, float fade, Vector3 eye, bool haveEye, ref Vector3 min, ref Vector3 max)
    {
        float gloss = Mathf.Lerp(0.3f, 1f, fade);
        int count = r.Count;
        for (int k = 0; k < Samples; k++)
        {
            int s = Mathf.Min(k, count - 1);
            Vector3 p = r.Bone.TransformPoint(_lp[b + s]);
            Vector3 n = r.Bone.TransformDirection(_ln[b + s]);
            Vector3 toEye = haveEye ? (eye - p).normalized : n;
            // the hit capsule sits inside the clothing: the run rides on the measured clothing surface, a touch toward the viewer
            float lift = _ll[b + s] + 0.004f;
            p += n * lift + toEye * Pull;
            Vector3 dir = s > 0 ? p - (r.Bone.TransformPoint(_lp[b + s - 1]) + n * lift) : (s + 1 < count ? r.Bone.TransformPoint(_lp[b + s + 1]) - p : Vector3.down);
            dir -= n * Vector3.Dot(dir, n);
            if (dir.sqrMagnitude < 1e-8f) dir = Vector3.Cross(n, Vector3.right);
            Vector3 side = Vector3.Cross(n, dir).normalized;
            // thin tail, full body, a round swollen head at the front; a single sample is just a bead
            float f = count > 1 ? s / (float)(count - 1) : 1f;
            float body = count > 1 ? Mathf.Lerp(0.45f, 1f, Mathf.Sqrt(f)) : 1f;
            bool headRow = k >= count - 1;
            float w = r.Width * 0.5f * body * (headRow ? 1.25f : 1f) * fade;
            float h = Mathf.Clamp(r.Width * 0.3f, 0.002f, 0.005f) * body * (headRow ? 1.4f : 1f) * fade;
            for (int a = 0; a < Across; a++)
            {
                Vector3 q = p + side * (AcrossF[a] * w) + n * (h * AcrossH[a]);
                _v[v + k * Across + a] = q;
                Vector3 nn = (n + side * AcrossTilt[a] * 0.8f).normalized;
                float diff = 0.5f + 0.5f * Mathf.Max(0f, Vector3.Dot(nn, KeyLight) * 0.5f + Vector3.Dot(nn, toEye) * 0.5f);
                Vector3 hv = (KeyLight + toEye).normalized;
                float sp = Mathf.Max(0f, Vector3.Dot(nn, hv));
                sp *= sp; sp *= sp; sp *= sp; sp *= sp; sp *= sp;
                _c[v + k * Across + a] = Wet.Shade(r.Col32, diff * (a == 2 ? 1f : 0.8f), sp * 1.4f * gloss);
                min = Vector3.Min(min, q);
                max = Vector3.Max(max, q);
            }
        }
    }
}
