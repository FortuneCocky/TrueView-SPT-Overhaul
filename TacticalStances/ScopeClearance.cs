using System.Collections.Generic;
using EFT.Animations;
using UnityEngine;

namespace TacticalStances;

/// <summary>
/// Fail-safe that keeps mounted sights out of the middle of the screen when
/// not aiming. Every frame the optic renderers (taken from the game's own
/// aim-point list, so any scope/red dot/magnifier is covered) are projected
/// to the screen; if they cover too much of a central zone, the weapon is
/// pushed down along the camera's down axis until the zone is clear again.
///
/// Pure feedback loop with hysteresis — it measures the pose it already
/// corrected, rises while covered, holds inside the band, and only lets go
/// once the scope is well clear — so it works in every stance and with any
/// stance tuning without knowing the weapon geometry up front.
/// </summary>
internal static class ScopeClearance
{
    private static readonly List<Renderer> _renderers = new List<Renderer>();
    private static readonly HashSet<ScopePrefabCache> _seenCaches = new HashSet<ScopePrefabCache>();
    private static readonly Vector3[] _corners = new Vector3[8];
    private static int _sightSignature;

    // 0 = no correction, 1 = full Max Push applied.
    private static float _amount;

    /// <summary>Last measured fraction of the clear zone covered by sights (0..1).</summary>
    internal static float Coverage { get; private set; }

    internal static void Reset()
    {
        _amount = 0f;
        Coverage = 0f;
        _renderers.Clear();
        _sightSignature = 0;
    }

    /// <summary>
    /// The correction to add to WeaponRootAnim's local position (parent
    /// space, meters along the camera's down axis).
    /// </summary>
    internal static Vector3 LocalOffset(Transform weaponRootAnim, Transform cam)
    {
        if (_amount <= 0f || cam == null || weaponRootAnim == null || weaponRootAnim.parent == null)
            return Vector3.zero;

        float t = Mathf.SmoothStep(0f, 1f, _amount);
        Vector3 world = -cam.up * (TacticalStancesPlugin.ScopeClearMaxPush.Value * t);
        return weaponRootAnim.parent.InverseTransformVector(world);
    }

    /// <summary>
    /// Measures the pose just written and steps the correction for next
    /// frame. Called once per frame from the visual pass.
    /// </summary>
    internal static void Update(ProceduralWeaponAnimation pwa, bool suppressed, float dt)
    {
        if (!TacticalStancesPlugin.EnableScopeClearance.Value || pwa == null || pwa.IsAiming || suppressed)
        {
            // ADS must sit exactly on the sight — drop the correction at once.
            _amount = (pwa != null && pwa.IsAiming) ? 0f : Mathf.MoveTowards(_amount, 0f, 2f * dt);
            Coverage = 0f;
            return;
        }

        Camera cam = Camera.main;
        if (cam == null)
            return;

        RefreshRenderers(pwa);
        Coverage = MeasureCoverage(cam);

        float trigger = TacticalStancesPlugin.ScopeClearTrigger.Value;
        if (Coverage > trigger)
            _amount = Mathf.MoveTowards(_amount, 1f, 4f * dt);
        else if (Coverage < trigger * 0.4f)
            _amount = Mathf.MoveTowards(_amount, 0f, 1.5f * dt);
    }

    // Rebuild the renderer list only when the set of aim points changes
    // (weapon swap, attachment change).
    private static void RefreshRenderers(ProceduralWeaponAnimation pwa)
    {
        var sights = pwa.ScopeAimTransforms;
        int sig = 17;
        if (sights != null)
        {
            foreach (var s in sights)
                sig = sig * 31 + (s?.Bone != null ? s.Bone.GetInstanceID() : 0);
        }
        if (sig == _sightSignature && _renderers.Count > 0)
            return;

        _sightSignature = sig;
        _renderers.Clear();
        _seenCaches.Clear();
        if (sights == null)
            return;

        foreach (var s in sights)
        {
            ScopePrefabCache cache = s?.ScopePrefabCache;
            if (cache == null || !_seenCaches.Add(cache))
                continue;
            _renderers.AddRange(cache.GetComponentsInChildren<Renderer>(true));
        }
    }

    // Fraction of the central clear zone covered by the union of the sight
    // renderers' oriented bounds, in viewport space.
    private static float MeasureCoverage(Camera cam)
    {
        float halfW = TacticalStancesPlugin.ScopeClearZone.Value * 0.5f;
        float halfH = Mathf.Min(0.5f, halfW * cam.aspect);   // square in pixels
        float zMinX = 0.5f - halfW, zMaxX = 0.5f + halfW;
        float zMinY = 0.5f - halfH, zMaxY = 0.5f + halfH;

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        bool any = false;

        for (int i = 0; i < _renderers.Count; i++)
        {
            Renderer r = _renderers[i];
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy)
                continue;

            Bounds b = r.localBounds;
            Vector3 c = b.center, e = b.extents;
            Matrix4x4 m = r.transform.localToWorldMatrix;
            int n = 0;
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                        _corners[n++] = m.MultiplyPoint3x4(c + Vector3.Scale(e, new Vector3(x, y, z)));

            for (int k = 0; k < 8; k++)
            {
                Vector3 v = cam.WorldToViewportPoint(_corners[k]);
                // A sight reaching behind the near plane is right in the
                // player's face — treat the zone as fully blocked.
                if (v.z < cam.nearClipPlane)
                    return 1f;
                if (v.x < minX) minX = v.x;
                if (v.x > maxX) maxX = v.x;
                if (v.y < minY) minY = v.y;
                if (v.y > maxY) maxY = v.y;
                any = true;
            }
        }

        if (!any)
            return 0f;

        float ox = Mathf.Min(maxX, zMaxX) - Mathf.Max(minX, zMinX);
        float oy = Mathf.Min(maxY, zMaxY) - Mathf.Max(minY, zMinY);
        if (ox <= 0f || oy <= 0f)
            return 0f;
        return (ox * oy) / ((zMaxX - zMinX) * (zMaxY - zMinY));
    }
}
