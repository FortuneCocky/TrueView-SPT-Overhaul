using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace BloodFX;

/// <summary>
/// Stepping in blood. Every death pool is remembered as a wet spot that grows as the pool
/// does. On each footstep EFT plays (bots and you alike) the planted foot is checked against
/// the wet spots: a foot in a pool gets a splash and comes out bloody, and a bloody foot
/// leaves a print at each step, a little fainter every time, until it is clean again.
/// Nothing runs between steps, and nothing runs at all while no one has blood on their feet
/// and no pool is wet.
/// </summary>
public partial class BloodManager
{
    private struct WetSpot
    {
        public Vector3 Pos, N;
        public float R, Start, Grow, Life;
    }

    private sealed class Feet
    {
        public Transform L, R, LToe, RToe;
        public float InkL, InkR, LastSplash;
        public int LastFoot;
    }

    private const int WetSlots = 48;
    private const float InkPerStep = 0.74f; // what is left on the foot after each print

    private readonly WetSpot[] _wet = new WetSpot[WetSlots];
    private int _wetNext;
    private readonly Dictionary<int, Feet> _feet = new Dictionary<int, Feet>();
    private Footprints _prints;
    private int _wetSteps, _printsMade;

    private int PrintCapacity() => Mathf.Clamp(_tier.Decals / 2, 16, 256);

    /// <summary>A pool of blood on the floor that feet can pick up.</summary>
    private void NoteWet(Vector3 pos, Vector3 n, float r, float start, float grow, float life)
    {
        ref WetSpot w = ref _wet[_wetNext];
        _wetNext = (_wetNext + 1) % WetSlots;
        w.Pos = pos;
        w.N = n;
        w.R = r;
        w.Start = start;
        w.Grow = grow;
        w.Life = life;
    }

    private void ClearWet()
    {
        for (int i = 0; i < WetSlots; i++) _wet[i].Life = 0f;
        _feet.Clear();
    }

    private bool AnyWet(float now)
    {
        for (int i = 0; i < WetSlots; i++)
            if (_wet[i].Life > 0f && now < _wet[i].Start + _wet[i].Life) return true;
        return false;
    }

    /// <summary>How wet the floor is at a point, 0..1: inside a pool that has spread there and not dried.</summary>
    private float WetAt(Vector3 p, float now)
    {
        float best = 0f;
        for (int i = 0; i < WetSlots; i++)
        {
            ref WetSpot w = ref _wet[i];
            if (w.Life <= 0f) continue;
            float age = now - w.Start;
            if (age < 0f || age > w.Life) continue;
            Vector3 d = p - w.Pos;
            if (Mathf.Abs(Vector3.Dot(d, w.N)) > 0.25f) continue; // another floor
            d -= w.N * Vector3.Dot(d, w.N);
            // the pool creeps out the way BloodPools draws it; its lobes average a little under the full radius
            float x = Mathf.Clamp01(age / w.Grow);
            float r = w.R * Mathf.Max(0.12f, 1f - Mathf.Pow(1f - x, 3f)) * 0.9f;
            if (d.sqrMagnitude > r * r) continue;
            // wettest in the middle; drying out over the last stretch of its life
            float wet = Mathf.Lerp(1f, 0.6f, d.magnitude / r) * Mathf.Clamp01((w.Start + w.Life - now) / 20f);
            if (wet > best) best = wet;
        }
        return best;
    }

    /// <summary>EFT played a footstep for this character: the planted foot may pick up or leave blood.</summary>
    internal void OnStep(Player p)
    {
        if (!_ready || !_cfg.Enabled.Value || !_cfg.Footprints.Value || p == null) return;
        float now = Time.time;
        Feet f = FeetOf(p);
        if (f == null) return;
        if (f.InkL <= 0.04f && f.InkR <= 0.04f && !AnyWet(now)) return;
        Vector3 root = p.Transform.Original.position;
        if (Viewer(out Vector3 eye, out _) && (root - eye).sqrMagnitude > _cfg.MaxDistance.Value * _cfg.MaxDistance.Value) return;

        // the planted foot is the lower one; when level, the other one than last time
        Vector3 lp = f.L.position, rp = f.R.position;
        bool right = rp.y < lp.y - 0.01f || (Mathf.Abs(rp.y - lp.y) <= 0.01f && f.LastFoot == 0);
        f.LastFoot = right ? 1 : 0;
        Transform foot = right ? f.R : f.L, toe = right ? f.RToe : f.LToe;
        Vector3 fp = foot.position;
        Vector3 fwd = toe != null ? toe.position - fp : p.Transform.Original.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-4f) fwd = p.Transform.Original.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-4f) return;
        fwd.Normalize();
        // the ankle sits over the back of the sole: the print's centre is a little ahead of it
        Vector3 at = fp + fwd * 0.07f;
        if (!Physics.Raycast(at + Vector3.up * 0.3f, Vector3.down, out RaycastHit hit, 0.7f, _mask, QueryTriggerInteraction.Ignore)) return;

        ref float ink = ref (right ? ref f.InkR : ref f.InkL);
        float wet = WetAt(hit.point, now);
        if (wet > 0.05f)
        {
            ink = Mathf.Max(ink, wet);
            if (now - f.LastSplash > 0.2f)
            {
                f.LastSplash = now;
                StepSplash.Play(hit.point, wet);
            }
            if (_wetSteps++ < 8 && _cfg.DebugLog.Value)
                BloodFXPlugin.Log.LogInfo($"BloodFX: {(p.IsYourPlayer ? "you" : p.Profile?.Nickname ?? "someone")} stepped in blood ({(right ? "right" : "left")} foot, wet {wet:0.00})");
        }
        if (ink < 0.04f) return;
        Color col = (Color)Tint(PoolBase, LightAt(hit.point, hit.normal), 1f);
        _prints.Add(hit.point, hit.normal, fwd, right, ink, col, _cfg.DecalLifetime.Value, now);
        _printsMade++;
        // a wet boot flicks a few stray drops beside the print
        if (ink > 0.25f)
        {
            Vector3 side = Vector3.Cross(hit.normal, fwd).normalized;
            int drops = Random.Range(1, 4);
            for (int k = 0; k < drops; k++)
            {
                Vector3 q = hit.point + side * (Random.Range(0.06f, 0.13f) * (Random.value < 0.5f ? -1f : 1f)) + fwd * Random.Range(-0.12f, 0.16f);
                if (!Physics.Raycast(q + hit.normal * 0.1f, -hit.normal, out RaycastHit dh, 0.25f, _mask, QueryTriggerInteraction.Ignore)) continue;
                QueueStuck(dh.point, dh.normal, fwd, Random.Range(0.0018f, 0.0038f) * Mathf.Lerp(0.8f, 1.2f, ink), Random.Range(1.2f, 2.2f),
                    (Color)Tint(StreamBase, LightAt(dh.point, dh.normal) * Random.Range(0.75f, 1f), 1f), now + 0.05f, _cfg.DecalLifetime.Value);
            }
        }
        ink *= InkPerStep;
    }

    /// <summary>The character's foot bones, looked up once and kept.</summary>
    private Feet FeetOf(Player p)
    {
        int id = p.GetInstanceID();
        if (_feet.TryGetValue(id, out Feet f))
            return f.L != null && f.R != null ? f : null;
        if (_feet.Count > 128) _feet.Clear();
        f = new Feet();
        try
        {
            Transform root = p.Transform.Original;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name;
                if (f.L == null && n.EndsWith("LFoot")) f.L = t;
                else if (f.R == null && n.EndsWith("RFoot")) f.R = t;
                else if (f.LToe == null && n.Contains("LToe")) f.LToe = t;
                else if (f.RToe == null && n.Contains("RToe")) f.RToe = t;
            }
        }
        catch { }
        _feet[id] = f;
        if ((f.L == null || f.R == null) && _cfg.DebugLog.Value)
            BloodFXPlugin.Log.LogWarning($"BloodFX: no foot bones found on {(p.IsYourPlayer ? "you" : p.Profile?.Nickname ?? "a character")}, no footprints for them");
        return f.L != null && f.R != null ? f : null;
    }
}
