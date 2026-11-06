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
