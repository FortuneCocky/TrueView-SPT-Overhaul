using System.Collections.Generic;
using EFT.Animations;
using UnityEngine;

namespace TacticalStances;

/// <summary>
/// Fail-safe that keeps big sights out of the middle of the screen when not
/// aiming. Every frame each mounted sight (taken from the game's own
/// aim-point list) is measured in camera space. Only sights that look big
/// on screen — a large scope close to the eye — are handled; small sights
/// like holos and red dots are left alone. For those, the weapon is pushed
/// down along the camera's down axis just far enough that their top edge
/// sits below a central zone.
///
/// The push is solved directly from the un-pushed pose (the push we applied
/// is added back before measuring), not by nudging until the screen looks
/// clear — so there is no feedback loop to hunt or bounce, even close to
/// the camera in Low Ready. The result is eased with a critically damped
/// spring so the gun glides rather than steps.
/// </summary>
internal static class ScopeClearance
{
    private sealed class Sight
    {
        public readonly List<Renderer> Renderers = new List<Renderer>();
        public bool Qualified;   // size gate, with hysteresis
    }

    private static readonly List<Sight> _sights = new List<Sight>();
    private static readonly HashSet<ScopePrefabCache> _seenCaches = new HashSet<ScopePrefabCache>();
    private static readonly Vector3[] _corners = new Vector3[8];
    private static int _sightSignature;

    // Meters along the camera's down axis: currently applied, and the
    // solved amount it is easing toward.
    private static float _push;
    private static float _targetPush;
    private static float _pushVel;

    internal static void Reset()
    {
        _push = _targetPush = _pushVel = 0f;
        _sights.Clear();
        _sightSignature = 0;
    }

    /// <summary>
    /// The correction to add to WeaponRootAnim's local position (parent
    /// space, meters along the camera's down axis).
    /// </summary>
    internal static Vector3 LocalOffset(Transform weaponRootAnim, Transform cam, bool aiming)
    {
        // ADS: zero from the very first frame, in both passes.
        if (aiming)
            _push = _targetPush = _pushVel = 0f;
        if (_push <= 0f || cam == null || weaponRootAnim == null || weaponRootAnim.parent == null)
            return Vector3.zero;

        return weaponRootAnim.parent.InverseTransformVector(-cam.up * _push);
    }

    /// <summary>
    /// Solves the push for the pose just drawn and eases toward it. Called
    /// once per frame from the visual pass.
    /// </summary>
    internal static void Update(ProceduralWeaponAnimation pwa, bool suppressed, float dt)
    {
        if (pwa != null && pwa.IsAiming)
        {
            _push = _targetPush = _pushVel = 0f;
            return;
        }

        Camera cam = Camera.main;
        if (!TacticalStancesPlugin.EnableScopeClearance.Value || pwa == null || suppressed || cam == null)
        {
            _targetPush = 0f;
        }
        else
        {
            RefreshSights(pwa);
            _targetPush = Mathf.Min(SolvePush(cam), TacticalStancesPlugin.ScopeClearMaxPush.Value);
        }

        if (dt <= 0f)
            return;
        // Slightly quicker to get out of the way than to come back.
        float smooth = _targetPush > _push ? 0.12f : 0.25f;
        _push = Mathf.Max(0f, Mathf.SmoothDamp(_push, _targetPush, ref _pushVel, smooth, Mathf.Infinity, dt));
    }

    // Rebuild the sight list only when the set of aim points changes
    // (weapon swap, attachment change).
    private static void RefreshSights(ProceduralWeaponAnimation pwa)
    {
        var aims = pwa.ScopeAimTransforms;
        int sig = 17;
        if (aims != null)
        {
            foreach (var s in aims)
                sig = sig * 31 + (s?.Bone != null ? s.Bone.GetInstanceID() : 0);
        }
        if (sig == _sightSignature && _sights.Count > 0)
            return;

        _sightSignature = sig;
        _sights.Clear();
        _seenCaches.Clear();
        if (aims == null)
            return;

        foreach (var s in aims)
        {
            ScopePrefabCache cache = s?.ScopePrefabCache;
            if (cache == null || !_seenCaches.Add(cache))
                continue;
            var sight = new Sight();
            sight.Renderers.AddRange(cache.GetComponentsInChildren<Renderer>(true));
            _sights.Add(sight);
        }
    }

    // Meters of downward push needed so every qualifying sight that overlaps
    // the zone horizontally has its top below the zone's lower edge (less
    // the allowed intrusion). Measured with the current push removed.
    private static float SolvePush(Camera cam)
    {
        float tanV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float tanH = tanV * cam.aspect;

        float halfW = TacticalStancesPlugin.ScopeClearZone.Value * 0.5f;
        float halfH = Mathf.Min(0.5f, halfW * cam.aspect);   // square in pixels
        // Viewport-space offsets from screen centre.
        float allowed = TacticalStancesPlugin.ScopeClearTrigger.Value * 2f * halfH;
        float floorY = -halfH + allowed;                      // top of sights must sit below this
        float minSize = TacticalStancesPlugin.ScopeClearMinSize.Value;

        Transform ct = cam.transform;
        float near = cam.nearClipPlane;
        float needed = 0f;

        foreach (Sight sight in _sights)
        {
            // Pass 1: the sight's on-screen footprint (un-pushed) — size gate
            // and horizontal overlap with the zone.
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            bool any = false, behind = false;
            foreach (Renderer r in sight.Renderers)
            {
                if (!Corners(r, ct))
                    continue;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 p = _corners[k];
                    if (p.z <= near) { behind = true; continue; }
                    float vx = p.x / (2f * p.z * tanH);
                    float vy = (p.y + _push) / (2f * p.z * tanV);
                    if (vx < minX) minX = vx;
                    if (vx > maxX) maxX = vx;
                    if (vy < minY) minY = vy;
                    if (vy > maxY) maxY = vy;
                    any = true;
                }
            }

            if (behind)
            {
                // Reaching past the near plane — right in the player's face.
                sight.Qualified = true;
                return float.MaxValue;
            }
            if (!any)
                continue;

            // Size gate: fraction of the screen the sight covers. Clamped to
            // the screen so a huge, close scope doesn't read as infinite.
            float w = Mathf.Clamp(maxX, -0.5f, 0.5f) - Mathf.Clamp(minX, -0.5f, 0.5f);
            float h = Mathf.Clamp(maxY, -0.5f, 0.5f) - Mathf.Clamp(minY, -0.5f, 0.5f);
            float size = Mathf.Max(0f, w) * Mathf.Max(0f, h);
            sight.Qualified = sight.Qualified ? size > minSize * 0.75f : size > minSize;
            if (!sight.Qualified || maxX < -halfW || minX > halfW)
                continue;

            // Pass 2: push needed for its highest point to clear the zone floor.
            foreach (Renderer r in sight.Renderers)
            {
                if (!Corners(r, ct))
                    continue;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 p = _corners[k];
                    float y = p.y + _push;                       // remove our own push
                    float limit = floorY * 2f * p.z * tanV;      // camera-space y of the zone floor at this depth
                    if (y - limit > needed)
                        needed = y - limit;
                }
            }
        }

        // Ignore sub-3mm corrections so a sight resting on the edge doesn't twitch.
        return needed < 0.003f ? 0f : needed;
    }

    // Fills _corners with the renderer's oriented bounds in camera space.
    private static bool Corners(Renderer r, Transform cam)
    {
        if (r == null || !r.enabled || !r.gameObject.activeInHierarchy)
            return false;

        Bounds b = r.localBounds;
        Vector3 c = b.center, e = b.extents;
        Matrix4x4 m = r.transform.localToWorldMatrix;
        int n = 0;
        for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
                for (int z = -1; z <= 1; z += 2)
                    _corners[n++] = cam.InverseTransformPoint(m.MultiplyPoint3x4(c + Vector3.Scale(e, new Vector3(x, y, z))));
        return true;
    }
}
