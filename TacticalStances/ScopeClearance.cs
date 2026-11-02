using System.Collections.Generic;
using EFT.Animations;
using UnityEngine;

namespace TacticalStances;

/// <summary>
/// Fail-safe that keeps mounted sights out of the middle of the screen when
/// not aiming. Every frame the optic renderers (taken from the game's own
/// aim-point list, so any scope/red dot/magnifier is covered) are measured
/// in camera space, and the weapon is pushed down along the camera's down
/// axis just far enough that their top edge sits below a central zone.
///
/// The push is solved directly from the un-pushed pose (the push we applied
/// is added back before measuring), not by nudging until the screen looks
/// clear — so there is no feedback loop to hunt or bounce, even close to
/// the camera in Low Ready. The result is eased with a critically damped
/// spring so the gun glides rather than steps.
/// </summary>
internal static class ScopeClearance
{
    private static readonly List<Renderer> _renderers = new List<Renderer>();
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
        _renderers.Clear();
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
            RefreshRenderers(pwa);
            _targetPush = Mathf.Min(SolvePush(cam), TacticalStancesPlugin.ScopeClearMaxPush.Value);
        }

        if (dt <= 0f)
            return;
        // Slightly quicker to get out of the way than to come back.
        float smooth = _targetPush > _push ? 0.12f : 0.25f;
        _push = Mathf.Max(0f, Mathf.SmoothDamp(_push, _targetPush, ref _pushVel, smooth, Mathf.Infinity, dt));
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

    // Meters of downward push needed so every sight renderer that overlaps
    // the zone horizontally has its top below the zone's lower edge (less
    // the allowed intrusion). Measured with the current push removed.
    private static float SolvePush(Camera cam)
    {
        float tanV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float tanH = tanV * cam.aspect;

        float halfW = TacticalStancesPlugin.ScopeClearZone.Value * 0.5f;
        float halfH = Mathf.Min(0.5f, halfW * cam.aspect);   // square in pixels
        // Viewport-space offsets from screen centre.
        float zoneHalfX = halfW;
        float allowed = TacticalStancesPlugin.ScopeClearTrigger.Value * 2f * halfH;
        float floorY = -halfH + allowed;                      // top of sights must sit below this

        Transform ct = cam.transform;
        float near = cam.nearClipPlane;
        float needed = 0f;

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

            // Horizontal extent on screen (unaffected by a vertical push).
            float minX = float.MaxValue, maxX = float.MinValue;
            bool behind = false;
            for (int k = 0; k < 8; k++)
            {
                Vector3 p = ct.InverseTransformPoint(_corners[k]);
                _corners[k] = p;
                if (p.z <= near) { behind = true; continue; }
                float vx = p.x / (2f * p.z * tanH);
                if (vx < minX) minX = vx;
                if (vx > maxX) maxX = vx;
            }
            if (behind)
                return float.MaxValue;   // sight reaching past the near plane — push all the way
            if (maxX < -zoneHalfX || minX > zoneHalfX)
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

        // Ignore sub-3mm corrections so a sight resting on the edge doesn't twitch.
        return needed < 0.003f ? 0f : needed;
    }
}
