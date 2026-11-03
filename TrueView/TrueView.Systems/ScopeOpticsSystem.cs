using EFT;
using EFT.Animations;
using TrueView.Configuration;
using TrueView.Core;
using UnityEngine;

namespace TrueView.Systems;

/// <summary>
/// Magnified optics. The game draws a scope shadow that slides across the
/// lens whenever the gun points away from the eye line, so TrueView's ADS
/// deadzone, lead, cant and float make the lens look detached, and every
/// extra degree of sway is multiplied by the zoom.
///
/// This tracks the zoom of the sight in use and gives the other systems:
///  - Magnified: 0..1 blend while aiming through a magnified optic. At 1
///    TrueView hands the gun, the camera and the sway back to the game, so
///    only vanilla sway, breathing and recoil move the scope.
///  - ScopedHip: 0..1 blend at the hip while a magnified optic is mounted,
///    used to carry the gun a little lower so the scope bell stops covering
///    the middle of the screen.
/// </summary>
internal static class ScopeOpticsSystem
{
    private const float MinMagnified = 1.5f;

    private static float _magnified;
    private static float _scopedHip;
    private static float _logZoom;

    public static float Magnified => _magnified;
    public static float ScopedHip => _scopedHip;

    public static void Tick(Player player, float dt)
    {
        ProceduralWeaponAnimation pwa = player?.ProceduralWeaponAnimation;
        bool aiming = HandlingState.IsAiming;
        float zoom = 1f;
        bool mounted = false;
        if (pwa != null && player.HandsController is Player.FirearmController)
        {
            try
            {
                zoom = CurrentZoom(pwa.CurrentScope);
                mounted = HasMagnifiedOptic(pwa);
            }
            catch { }
        }

        bool magnified = aiming && zoom >= MinMagnified
            && (HandlingConfig.ScopeVanilla?.Value ?? true);
        _magnified = Mathf.MoveTowards(_magnified, magnified ? 1f : 0f, 6f * dt);
        _scopedHip = Mathf.MoveTowards(_scopedHip, !aiming && mounted ? 1f : 0f, 4f * dt);

        if (magnified && Mathf.Abs(zoom - _logZoom) > 0.05f)
        {
            _logZoom = zoom;
            Utilities.Log.Info($"Scope: aiming at {zoom:F1}x, gun, camera and sway handed to the game");
        }
        else if (!magnified)
        {
            _logZoom = 0f;
        }
    }

    /// <summary>Current magnification of a sight, 1 for anything that isn't a magnified optic.</summary>
    public static float CurrentZoom(ProceduralWeaponAnimation.SightNBone sight)
    {
        if (sight?.Mod == null || !sight.IsOptic)
            return 1f;
        // Variable scopes report the live zoom here; fixed ones leave it unset.
        float z = sight.Mod.ScopeZoomValue;
        if (z < 1f)
            z = sight.Mod.GetCurrentOpticZoom();
        return Mathf.Max(1f, z);
    }

    /// <summary>Any sight on the gun that can magnify (2x or more).</summary>
    public static bool HasMagnifiedOptic(ProceduralWeaponAnimation pwa)
    {
        var scopes = pwa.ScopeAimTransforms;
        if (scopes == null)
            return false;
        for (int i = 0; i < scopes.Count; i++)
        {
            var s = scopes[i];
            if (s?.Mod != null && s.ScopePrefabCache != null && s.ScopePrefabCache.CurrentModHasOptics
                && s.Mod.GetMaxOpticZoom() >= 2f)
                return true;
        }
        return false;
    }

    public static void Reset()
    {
        _magnified = 0f;
        _scopedHip = 0f;
        _logZoom = 0f;
    }
}
