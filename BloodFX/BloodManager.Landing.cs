using UnityEngine;

namespace BloodFX;

/// <summary>
/// What happens where a rope of blood arrives, decided every frame from how it is arriving:
/// fluid landing on one spot feeds a pool that keeps spreading from there; a landing point that
/// is travelling (a walking or falling body, a dragged corpse, a jet sweeping the ground) lays a
/// streak along the floor, with pools wherever it lingers; fluid meeting a wall leaves a run that
/// crawls down it. Every arrival throws a few beads outward that stick where they land.
/// </summary>
public partial class BloodManager
{
    private struct LandState
    {
        public int Serial, Pool, PoolSerial, Trail, TrailSerial;
        public Vector3 LastPos, PoolPos, FloorN;
        public float LastT, Speed, SplashAt, RayAt, Volume;
        public bool HaveFloor, WallDone;
    }

    private const float PoolSpeed = 0.35f;     // slower than this and the landing point is "staying put"
    private const float PoolReach = 0.25f;     // a landing this far from its pool starts another one
    private const float PoolMaxRadius = 0.9f;
    private const float PoolDepth = 0.012f;    // fluid volume to pool area: a visual depth, not a real one

    private LandState[] _land = new LandState[0];

    private void LandingTick(float now)
    {
        int n = _streams.LandingCount;
        if (n == 0) return;
        if (_land.Length < _streams.Capacity) _land = new LandState[_streams.Capacity];
        float life = _cfg.DecalLifetime.Value;
        for (int i = 0; i < n; i++)
        {
            BloodStreams.Landing L = _streams.GetLanding(i);
            ref LandState s = ref _land[L.Slot];
            if (s.Serial != L.Serial)
            {
                s = default;
                s.Serial = L.Serial;
                s.Pool = -1;
                s.Trail = -1;
                s.LastPos = L.Pos;
                s.LastT = now;
                s.SplashAt = now + 0.05f;
            }
            float dt = Mathf.Clamp(now - s.LastT, 0f, 0.1f);
            s.LastT = now;
            // how much fluid arrived since last frame (the rope's cross-section times its speed)
            float flow = Mathf.PI * L.Radius * L.Radius * Mathf.Clamp(L.Vel.magnitude, 0.3f, 8f);
            float amount = flow * dt;

            if (L.OnWall)
            {
                if (s.WallDone) continue;
                s.WallDone = true;
                WallRun(L, now, life);
                continue;
            }

            // the real ground under the landing point, refreshed as it moves
            Vector3 flat = L.Pos - s.LastPos;
            flat.y = 0f;
            bool moved = flat.sqrMagnitude > 0.08f * 0.08f;
            if (!s.HaveFloor || moved || now >= s.RayAt)
            {
                s.RayAt = now + 0.12f;
                if (Physics.Raycast(L.Pos + Vector3.up * 0.3f, Vector3.down, out RaycastHit hit, 1.6f, _mask, QueryTriggerInteraction.Ignore))
                {
                    s.HaveFloor = true;
                    s.FloorN = hit.normal;
                    _streams.SetFloor(L.Slot, L.Serial, hit.point.y);
                }
                else
                {
                    // nothing under it here (a ledge): the blood keeps falling, nothing lands
                    s.HaveFloor = false;
                    _streams.SetFloor(L.Slot, L.Serial, L.Pos.y - 1.5f);
                    s.LastPos = L.Pos;
                    continue;
                }
            }
            if (dt > 0.005f)
            {
                float v = flat.magnitude / dt;
                s.Speed = Mathf.Lerp(s.Speed, v, 0.5f);
            }
            s.LastPos = L.Pos;
            Vector3 ground = L.Pos;
            Vector3 fn = s.HaveFloor ? s.FloorN : Vector3.up;

            if (s.Speed < PoolSpeed)
            {
                // staying put: it builds up a pool that spreads out from here
                s.Trail = -1;
                float area = amount / PoolDepth;
                if (s.Pool < 0 || (ground - s.PoolPos).sqrMagnitude > PoolReach * PoolReach || !_puddles.IsLive(s.Pool, s.PoolSerial))
                {
                    s.Pool = _puddles.Add(ground, fn, Mathf.Clamp(L.Radius * 2.5f, 0.02f, 0.06f), Tint(PoolBase, L.Light, 1f), now, 0.5f,
                        life * 1.5f, 1f, PoolMaxRadius, out s.PoolSerial);
                    s.PoolPos = ground;
                    s.Volume = 0f;
                }
                s.Volume += area;
                _puddles.Feed(s.Pool, s.PoolSerial, area, now);
            }
            else
            {
                // on the move: a streak along the ground, wider the more is arriving
                s.Pool = -1;
                float width = Mathf.Clamp(L.Radius * 3.5f * Mathf.Sqrt(Mathf.Clamp(flow / 0.0008f, 0.4f, 2f)), 0.012f, 0.06f);
                if (s.Trail < 0 || !_trails.Extend(s.Trail, s.TrailSerial, ground, fn, width, now))
                {
                    s.Trail = _trails.Begin(ground, fn, width, (Color)L.Col, life, now, out s.TrailSerial);
                }
            }

            // the splash: beads thrown outward from the impact that stick where they land
            if (now >= s.SplashAt)
            {
                s.SplashAt = now + Random.Range(0.18f, 0.45f);
                Splash(ground, fn, L, flow);
            }
        }
    }

    /// <summary>Blood arriving on a wall: a run crawls down from the hit, with a few drops thrown around it.</summary>
    private void WallRun(BloodStreams.Landing L, float now, float life)
    {
        if (!Physics.Raycast(L.Pos + L.WallN * 0.1f, -L.WallN, out RaycastHit hit, 0.3f, _mask, QueryTriggerInteraction.Ignore)) return;
        Vector3 down = Vector3.down - hit.normal * Vector3.Dot(Vector3.down, hit.normal);
        if (down.sqrMagnitude < 0.05f) return;
        down.Normalize();
        float len = Mathf.Clamp(L.Radius * 30f, 0.1f, 0.6f);
        float w = Mathf.Clamp(L.Radius * 1.6f, 0.006f, 0.03f);
        _trails.Add(hit.point, hit.normal, down, len, w, (Color)L.Col, now + 0.15f, Mathf.Clamp(len / 0.1f, 1.5f, 6f), life, 0.1f);
        if (Random.value < 0.5f)
        {
            Vector3 side = Vector3.Cross(hit.normal, down).normalized * Random.Range(-0.04f, 0.04f);
            _trails.Add(hit.point + side, hit.normal, down, len * Random.Range(0.3f, 0.7f), w * 0.6f, (Color)L.Col,
                now + Random.Range(0.3f, 0.8f), 3f, life, 0.1f);
        }
        SpatterDrops(hit.point, hit.normal, L.Vel.sqrMagnitude > 1e-4f ? L.Vel.normalized : -hit.normal, now,
            Mathf.Clamp(L.Radius * 8f, 0.04f, 0.25f), 0.35f, L.Light);
    }

    /// <summary>A few beads thrown out from where the rope hits: blobs that fly and stick where they come down.</summary>
    private void Splash(Vector3 ground, Vector3 n, BloodStreams.Landing L, float flow)
    {
        int count = Mathf.Clamp(Mathf.RoundToInt((2f + flow / 0.0004f) * _load), 1, 5);
        Vector3 t1 = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
        Vector3 t2 = Vector3.Cross(n, t1);
        Vector3 slide = L.Vel - n * Vector3.Dot(L.Vel, n);
        float scale = Mathf.Clamp(Mathf.Sqrt(_cfg.Intensity.Value), 0.5f, 1.6f);
        Vector3 origin = ground + n * 0.01f;
        for (int i = 0; i < count; i++)
        {
            float a = Random.value * 6.283f;
            Vector3 radial = t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a);
            Vector3 v = radial * Random.Range(0.35f, 1.3f) + n * Random.Range(0.4f, 1.1f) + slide * 0.25f;
            float land = BloodStreams.LandTime(origin, v, 9.81f, ground.y);
            _blobParams.position = origin;
            _blobParams.velocity = v;
            _blobParams.startLifetime = Mathf.Clamp(land > 0f ? land + 0.1f : 0.5f, 0.1f, 1.2f);
            float sz = Mathf.Lerp(0.004f, 0.012f, Mathf.Pow(Random.value, 2f)) * scale;
            _blobParams.startSize3D = new Vector3(sz, sz * Random.Range(1f, 1.4f), sz);
            _blobParams.startColor = L.Col;
            _blobs.Emit(_blobParams, 1);
        }
    }
}
