using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace BloodFX;

/// <summary>
/// What happens where the drops of a blood stream come down, decided per drop as it arrives:
/// each drop slaps down as a round splat with a crown of small satellite drops (small ones as a
/// bead); where many keep landing on one spot they merge into a pool that spreads out from there;
/// when the landing point is travelling (a walking or falling body, a dragged corpse, a jet
/// sweeping the ground) the drops simply come down one after another along the way, a chain of
/// splats and beads that merge where they are dense. Nothing here draws a line: blood on a floor
/// is puddles, round splats and beads, never a filament. Drops meeting a wall stick as beads and
/// now and then leave a run that crawls down.
/// </summary>
public partial class BloodManager
{
    private struct LandState
    {
        public int Serial, Pool, PoolSerial;
        public Vector3 Centroid, PoolPos, FloorN;
        public float LastT, Speed, SplatAt, RayAt, WallRunAt, Settled;
        public bool HaveFloor, HaveCentroid;
    }

    private const float PoolSpeed = 0.3f;      // slower than this and the landing point is "staying put"
    private const float PoolReach = 0.35f;     // a drop this far from its pool starts another one
    private const float PoolMaxRadius = 0.9f;
    private const float PoolDepth = 0.00015f;  // drop volume to pool area: a visual depth, not a real one
    private const int SplatsPerFrame = 16;

    private LandState[] _land = new LandState[0];

    private void LandingTick(float now)
    {
        int n = _streams.LandingCount;
        if (n == 0) return;
        if (_land.Length < _streams.Capacity) _land = new LandState[_streams.Capacity];
        float life = _cfg.DecalLifetime.Value;
        int splats = 0;
        for (int i = 0; i < n; i++)
        {
            BloodStreams.Landing L = _streams.GetLanding(i);
            ref LandState s = ref _land[L.Slot];
            if (s.Serial != L.Serial)
            {
                s = default;
                s.Serial = L.Serial;
                s.Pool = -1;
                s.LastT = now;
            }

            if (L.OnBody)
            {
                // a heavy drop on the body clings and runs down it
                if (L.Radius > 0.003f && now >= s.SplatAt)
                {
                    s.SplatAt = now + 0.12f;
                    _runs.Begin(L.BodyCol, L.Pos, L.WallN, Mathf.Clamp(L.Radius * 1.8f, 0.006f, 0.012f), Random.Range(0.05f, 0.1f), L.Col, life, now,
                        LiftOnMesh(L.BodyCol, L.Pos, L.WallN));
                }
                continue;
            }
            if (L.OnWall)
            {
                WallHit(L, ref s, now, life);
                continue;
            }

            // the real ground under the landing: refreshed as the landing point moves
            if (!s.HaveFloor || now >= s.RayAt || (L.Pos - s.Centroid).sqrMagnitude > 0.15f * 0.15f)
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
                    // nothing under it here (a ledge): the drops fall on
                    s.HaveFloor = false;
                    _streams.SetFloor(L.Slot, L.Serial, L.Pos.y - 1.5f);
                    continue;
                }
            }
            Vector3 fn = s.FloorN;
            Vector3 ground = L.Pos;

            // where the stream's drops are coming down, and how fast that point is travelling
            float dt = now - s.LastT;
            if (!s.HaveCentroid)
            {
                s.Centroid = ground;
                s.HaveCentroid = true;
            }
            else
            {
                Vector3 prev = s.Centroid;
                s.Centroid = Vector3.Lerp(s.Centroid, ground, 0.25f);
                if (dt > 0.01f)
                {
                    Vector3 flat = s.Centroid - prev;
                    flat.y = 0f;
                    s.Speed = Mathf.Lerp(s.Speed, flat.magnitude / dt, 0.4f);
                    s.LastT = now;
                }
            }

            // the drop itself slaps down: a round splat with a crown of satellites, or just a bead when small
            float r = Mathf.Clamp(L.Radius, 0.0015f, 0.011f);
            Color col = L.Col;
            // the heavy drops splat with a crown; the rest are beads; the finest leave nothing (the pool has their volume)
            if (r > 0.0065f && splats < SplatsPerFrame && now >= s.SplatAt)
            {
                splats++;
                s.SplatAt = now + 0.25f;
                DripSplash(ground, fn, r * 0.7f, col, now);
            }
            else if (r > 0.0025f)
            {
                // a flat wet bead a little wider than the drop, smeared a touch the way it was going
                Vector3 slide = L.Vel - fn * Vector3.Dot(L.Vel, fn);
                _stuck.Add(ground, fn, slide, Mathf.Min(r * 1.4f, 0.012f), 1f + Mathf.Clamp(slide.magnitude * 0.12f, 0f, 0.8f), col, life, now);
            }

            // drops that keep coming down on one spot merge into a pool that spreads out from there; while the
            // landing point is on the move (the wound or the arc sweeping) they stay separate splats
            bool still = s.Speed < PoolSpeed && !(L.Anchored && L.SourceSpeed > PoolSpeed);
            if (!still) { s.Settled = 0f; continue; }
            s.Settled += 1f;
            if (s.Settled < 6f) continue;   // a few drops on the spot first, then it starts to puddle
            float area = 4.19f * L.Radius * L.Radius * L.Radius / PoolDepth;
            if (s.Pool < 0 || (ground - s.PoolPos).sqrMagnitude > PoolReach * PoolReach || !_puddles.IsLive(s.Pool, s.PoolSerial))
            {
                // all the blood coming down around here shares one puddle (the body's own pool included)
                if (_puddles.FindNear(ground, PoolReach, out s.Pool, out s.PoolSerial))
                    s.PoolPos = ground;
                else
                {
                    s.Pool = _puddles.Add(ground, fn, Mathf.Clamp(L.Radius * 3f, 0.02f, 0.05f), Tint(PoolBase, L.Light, 1f), now, 0.6f,
                        life * 1.5f, 1f, PoolMaxRadius, out s.PoolSerial);
                    s.PoolPos = ground;
                }
            }
            _puddles.Feed(s.Pool, s.PoolSerial, area, now);
        }
    }

    // ---------------------------------------------------------------- ropes (the pouring kinds)

    private BloodRopes _ropes;
    private BodyRuns _runs;
    private LandState[] _ropeLand = new LandState[0];
    private int RopeCapacity() => Mathf.Max(8, _tier.Streams / 2);
    private int RunCapacity() => _tier.Wounds * 4;

    // the bleeding character's hit colliders, looked up once per character, so its own blood can run down it
    private readonly Dictionary<int, Collider[]> _bodies = new Dictionary<int, Collider[]>();
    private Collider[] _spurtBody;

    private Collider[] BodyShape(Player victim)
    {
        if (victim == null) return null;
        int id = victim.GetInstanceID();
        if (_bodies.TryGetValue(id, out Collider[] list) && list.Length > 0 && list[0] != null) return list;
        if (_bodies.Count > 128) _bodies.Clear();
        BodyPartCollider[] parts = null;
        try { parts = victim.PlayerBones != null ? victim.PlayerBones.BodyPartColliders : null; } catch { }
        if (parts == null || parts.Length == 0) parts = victim.GetComponentsInChildren<BodyPartCollider>(true);
        var cols = new List<Collider>(parts.Length);
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == null) continue;
            Collider c = parts[i].GetComponent<Collider>();
            if (c != null) cols.Add(c);
        }
        list = cols.ToArray();
        _bodies[id] = list;
        return list.Length > 0 ? list : null;
    }

    /// <summary>
    /// A pour aimed along the bullet would start inside the body (the hit point is the entry hole and the bullet
    /// goes on through). The blood comes out of the far side instead: the origin is marched through the body's
    /// capsules to the exit; with no exit in reach (a shot along a limb) it pours out of the entry side.
    /// </summary>
    private void ExitWound(ref Vector3 origin, ref Vector3 vel)
    {
        if (_spurtBody == null) return;
        float speed = vel.magnitude;
        if (speed < 1e-4f) return;
        Vector3 dir = vel / speed;
        if (!BloodStreams.BodyContact(_spurtBody, origin + dir * 0.07f, 0.004f, out _, out _, out _)) return;
        for (float t = 0.09f; t <= 0.6f; t += 0.02f)
        {
            Vector3 q = origin + dir * t;
            if (BloodStreams.BodyContact(_spurtBody, q, 0.004f, out _, out _, out _)) continue;
            origin = q + dir * 0.01f;
            return;
        }
        if (BloodStreams.BodyContact(_spurtBody, origin, 0.06f, out _, out Vector3 n, out _))
        {
            Vector3 flat = n;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-4f) flat = -dir;
            flat.Normalize();
            vel = (flat * 0.7f + Vector3.up * Mathf.Max(0f, dir.y) * 0.3f + Vector3.down * 0.2f).normalized * speed;
            origin += flat * 0.01f;
        }
    }

    // which character a hit collider belongs to, for mesh queries
    private readonly Dictionary<int, Player> _owners = new Dictionary<int, Player>();

    private Player PlayerOf(Collider col)
    {
        if (col == null) return null;
        int id = col.GetInstanceID();
        if (_owners.TryGetValue(id, out Player pl) && pl != null) return pl;
        if (_owners.Count > 1024) _owners.Clear();
        pl = col.GetComponentInParent<Player>();
        _owners[id] = pl;
        return pl;
    }

    /// <summary>How far above a point of a hit capsule the character's clothing really is, from its posed mesh (-1 = not now).</summary>
    private float LiftOnMesh(Collider col, Vector3 p, Vector3 n)
    {
        return _marks.SurfaceOffset(PlayerOf(col), p, n, 0.14f, Time.time);
    }

    /// <summary>A drop that let go of a rope or a run: it falls as a blob and splashes where it lands.</summary>
    private void CarryDrop(BloodStreams.Detach d, float now)
    {
        float fall = 1.2f;
        if (Physics.Raycast(d.Pos, Vector3.down, out RaycastHit hit, 3f, _mask, QueryTriggerInteraction.Ignore))
        {
            float t = BloodStreams.LandTime(d.Pos, d.Vel, 9.81f, hit.point.y);
            fall = t > 0f ? t : Mathf.Sqrt(2f * hit.distance / 9.81f);
            Vector3 land = d.Pos + d.Vel * fall;
            land.y = hit.point.y;
            if (d.Vel.sqrMagnitude < 0.5f || Physics.Raycast(land + Vector3.up * 0.1f, Vector3.down, out hit, 0.3f, _mask, QueryTriggerInteraction.Ignore))
                DripSplash(hit.point, hit.normal, d.Radius, d.Col, now + fall);
        }
        _blobParams.position = d.Pos;
        _blobParams.velocity = d.Vel;
        _blobParams.startLifetime = Mathf.Max(0.05f, fall);
        _blobParams.startSize3D = new Vector3(d.Radius * 2f, d.Radius * 2.6f, d.Radius * 2f);
        _blobParams.startColor = d.Col;
        _blobs.Emit(_blobParams, 1);
    }

    /// <summary>Moves the ropes, carries on the beads that let go of them, and handles where their fluid arrives.</summary>
    private void RopeTick(float now, Vector3 eye)
    {
        if (_runs.LiftAt == null) _runs.LiftAt = LiftOnMesh;
        _ropes.Tick(now, eye);
        // ropes torn off a wound that moved too fast: the rest of their flow goes on as drops
        for (int i = 0; i < _ropes.HandoffCount; i++)
        {
            BloodStreams.Spec rest = _ropes.GetHandoff(i);
            if (rest.Emit > 0.05f) _streams.Spawn(ref rest, now);
        }
        _ropes.ClearHandoff();
        for (int i = 0; i < _ropes.DetachCount; i++) CarryDrop(_ropes.GetDetach(i), now);
        _ropes.ClearDetach();
        // blood running down bodies: moves with them, drips off where their surface turns under
        _runs.Tick(now, Time.deltaTime, eye, eye != Vector3.zero);
        for (int i = 0; i < _runs.DetachCount; i++) CarryDrop(_runs.GetDetach(i), now);
        _runs.ClearDetach();

        int n = _ropes.LandingCount;
        if (n == 0) return;
        if (_ropeLand.Length < _ropes.Capacity) _ropeLand = new LandState[_ropes.Capacity];
        float life = _cfg.DecalLifetime.Value;
        for (int i = 0; i < n; i++)
        {
            BloodStreams.Landing L = _ropes.GetLanding(i);
            ref LandState s = ref _ropeLand[L.Slot];
            if (s.Serial != L.Serial)
            {
                s = default;
                s.Serial = L.Serial;
                s.Pool = -1;
                s.LastT = now;
                s.SplatAt = now + 0.05f;
                s.RayAt = 0f;
            }
            if (L.OnBody)
            {
                // the pour meets the body: it runs down it in a fresh run every so often, like liquid over a surface
                if (now < s.SplatAt) continue;
                s.SplatAt = now + Random.Range(0.25f, 0.45f);
                Vector3 side = Vector3.Cross(L.WallN, Vector3.up).normalized * Random.Range(-0.6f, 0.6f) * L.Radius;
                _runs.Begin(L.BodyCol, L.Pos + side, L.WallN, Mathf.Clamp(L.Radius * 1.2f, 0.008f, 0.016f), Random.Range(0.08f, 0.16f), L.Col, life, now,
                    LiftOnMesh(L.BodyCol, L.Pos + side, L.WallN));
                continue;
            }
            if (L.OnWall)
            {
                if (now < s.SplatAt) continue;
                s.SplatAt = now + 0.12f;
                WallHit(L, ref s, now, life);
                continue;
            }

            // the real ground under the foot of the rope, refreshed as it moves
            if (!s.HaveFloor || now >= s.RayAt || (L.Pos - s.Centroid).sqrMagnitude > 0.15f * 0.15f)
            {
                s.RayAt = now + 0.12f;
                if (Physics.Raycast(L.Pos + Vector3.up * 0.3f, Vector3.down, out RaycastHit hit, 1.6f, _mask, QueryTriggerInteraction.Ignore))
                {
                    s.HaveFloor = true;
                    s.FloorN = hit.normal;
                    _ropes.SetFloor(L.Slot, L.Serial, hit.point.y);
                }
                else
                {
                    s.HaveFloor = false;
                    _ropes.SetFloor(L.Slot, L.Serial, L.Pos.y - 1.5f);
                    continue;
                }
            }
            Vector3 fn = s.FloorN;
            Vector3 ground = L.Pos;
            float dt = Mathf.Clamp(now - s.LastT, 0f, 0.1f);
            s.LastT = now;
            if (!s.HaveCentroid) { s.Centroid = ground; s.HaveCentroid = true; }
            else
            {
                Vector3 prev = s.Centroid;
                s.Centroid = Vector3.Lerp(s.Centroid, ground, 0.3f);
                if (dt > 0.005f)
                {
                    Vector3 flat = s.Centroid - prev;
                    flat.y = 0f;
                    s.Speed = Mathf.Lerp(s.Speed, flat.magnitude / dt, 0.3f);
                }
            }

            // blood hitting the floor: the foot keeps splashing, a crown of satellites and a few beads thrown out
            float r = Mathf.Clamp(L.Radius, 0.004f, 0.02f);
            if (now >= s.SplatAt)
            {
                s.SplatAt = now + Random.Range(0.15f, 0.3f);
                DripSplash(ground, fn, Mathf.Min(r * 0.8f, 0.012f), L.Col, now);
                RopeSplash(ground, fn, L, r);
            }

            // a rope coming down on one spot builds a pool that spreads out from there; a rope whose wound is on
            // the move (a body falling or being dragged) leaves its splashes along the way and pools where it rests
            bool still = s.Speed < PoolSpeed && L.SourceSpeed < PoolSpeed;
            if (!still) continue;
            float flow = Mathf.PI * L.Radius * L.Radius * Mathf.Clamp(L.Vel.magnitude, 0.3f, 8f);
            float area = flow * dt / 0.012f;
            if (s.Pool < 0 || (ground - s.PoolPos).sqrMagnitude > PoolReach * PoolReach || !_puddles.IsLive(s.Pool, s.PoolSerial))
            {
                if (_puddles.FindNear(ground, PoolReach, out s.Pool, out s.PoolSerial))
                    s.PoolPos = ground;
                else
                {
                    s.Pool = _puddles.Add(ground, fn, Mathf.Clamp(r * 3f, 0.03f, 0.06f), Tint(PoolBase, L.Light, 1f), now, 0.6f,
                        life * 1.5f, 1f, PoolMaxRadius, out s.PoolSerial);
                    s.PoolPos = ground;
                }
            }
            _puddles.Feed(s.Pool, s.PoolSerial, area, now);
        }
    }

    /// <summary>Beads thrown out from where a rope hits the floor: blobs that fly a little way and stick where they come down.</summary>
    private void RopeSplash(Vector3 ground, Vector3 n, BloodStreams.Landing L, float r)
    {
        int count = Mathf.Clamp(Mathf.RoundToInt((2f + r / 0.004f) * _load), 1, 5);
        Vector3 t1 = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
        Vector3 t2 = Vector3.Cross(n, t1);
        Vector3 slide = L.Vel - n * Vector3.Dot(L.Vel, n);
        float scale = Mathf.Clamp(Mathf.Sqrt(_cfg.Intensity.Value), 0.5f, 1.6f);
        Vector3 origin = ground + n * 0.01f;
        for (int i = 0; i < count; i++)
        {
            float a = Random.value * 6.283f;
            Vector3 radial = t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a);
            Vector3 v = radial * Random.Range(0.3f, 1.2f) + n * Random.Range(0.4f, 1.1f) + slide * 0.2f;
            float land = BloodStreams.LandTime(origin, v, 9.81f, ground.y);
            _blobParams.position = origin;
            _blobParams.velocity = v;
            _blobParams.startLifetime = Mathf.Clamp(land > 0f ? land + 0.1f : 0.5f, 0.1f, 1.2f);
            float sz = Mathf.Lerp(0.004f, 0.011f, Mathf.Pow(Random.value, 2f)) * scale;
            _blobParams.startSize3D = new Vector3(sz, sz * Random.Range(1f, 1.4f), sz);
            _blobParams.startColor = L.Col;
            _blobs.Emit(_blobParams, 1);
        }
    }

    /// <summary>A drop arriving on a wall: it sticks as a bead where there really is wall; the first heavy one leaves a run crawling down.</summary>
    private void WallHit(BloodStreams.Landing L, ref LandState s, float now, float life)
    {
        Vector3 n = L.WallN;
        if (!Physics.Raycast(L.Pos + n * 0.08f, -n, out RaycastHit hit, 0.2f, _mask, QueryTriggerInteraction.Ignore)) return;
        if (Vector3.Dot(hit.normal, n) < 0.5f) return;
        n = hit.normal;
        Vector3 down = Vector3.down - n * Vector3.Dot(Vector3.down, n);
        if (down.sqrMagnitude > 0.05f) down.Normalize(); else down = Vector3.zero;
        float r = Mathf.Clamp(L.Radius, 0.0015f, 0.011f);
        Vector3 slide = L.Vel - n * Vector3.Dot(L.Vel, n);
        _stuck.Add(hit.point, n, slide + down * 0.5f, Mathf.Min(r * 1.4f, 0.012f), 1f + Mathf.Clamp(slide.magnitude * 0.12f, 0f, 0.8f), L.Col, life, now);
        if (s.WallRunAt > 0f || down == Vector3.zero || r < 0.005f) return;
        s.WallRunAt = now;   // one run per stream
        float len = Mathf.Clamp(r * 25f, 0.08f, 0.35f);
        float w = Mathf.Clamp(r * 1.5f, 0.005f, 0.018f);
        _trails.Add(hit.point, n, down, len, w, L.Col, now + 0.2f, Mathf.Clamp(len / 0.08f, 1.5f, 6f), life, 0.1f);
    }
}
