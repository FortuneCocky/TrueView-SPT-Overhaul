using Comfort.Common;
using EFT;
using EFT.Animations;
using EFT.CameraControl;
using EFT.Settings;
using TrueView.Configuration;
using TrueView.Core;
using UnityEngine;

namespace TrueView.Systems;

/// <summary>
/// Field of view override. EFT clamps its own setting to 50-75 and builds
/// every camera FOV from it: hip = setting, sprint = setting - 10, iron
/// sights and red dots = setting - 15, optics = a fixed 35 (the scope
/// picture is its own camera). Rather than writing the camera, the game's
/// targets are remapped as if its setting were the slider value
/// (Patch_FieldOfView), so EFT's own transitions, sprint, ADS and optics
/// keep working and DynamicFovSystem's focus zoom and adrenaline pinch
/// still multiply on top. The arms' FOV compensation (ribcage scale) is
/// fed the same value so the gun sits where EFT would put it.
/// </summary>
internal static class FieldOfViewSystem
{
    // EFT's optic camera FOV while aiming through a scope.
    private const float OpticFov = 35f;

    private static bool _lastActive;
    private static int _lastFov;

    public static bool Active =>
        HandlingConfig.Enabled?.Value == true && HandlingConfig.FovOverride?.Value == true;

    public static float GameFov
    {
        get
        {
            try { return Singleton<SettingsManager>.Instance.Game.Settings.FieldOfView.Value; }
            catch { return 75f; }
        }
    }

    public static float TargetFov => HandlingConfig.FieldOfView?.Value ?? 75f;

    /// <summary>
    /// A FOV the game asked for, rebuilt with the slider in place of its
    /// setting. Optic FOV is left alone.
    /// </summary>
    public static float Map(float gameFov)
    {
        if (!Active || IsOpticAim(gameFov))
            return gameFov;
        return Mathf.Clamp(gameFov + (TargetFov - GameFov), 1f, 170f);
    }

    private static bool IsOpticAim(float gameFov)
    {
        if (Mathf.Abs(gameFov - OpticFov) > 0.01f)
            return false;
        try
        {
            ProceduralWeaponAnimation pwa = HandlingManager.LocalPlayer()?.ProceduralWeaponAnimation;
            return pwa != null && pwa.IsAiming && pwa.CurrentScope.IsOptic;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Degrees that look the same on screen at the slider FOV as they did at
    /// the game's setting. Only with "Deadzone follows FOV" on; otherwise
    /// the deadzone stays in real degrees, the same aim at any FOV.
    /// </summary>
    public static float ScaleZone(float degrees)
    {
        if (!Active || HandlingConfig.FovScalesDeadzone?.Value != true)
            return degrees;
        float k = Mathf.Tan(TargetFov * 0.5f * Mathf.Deg2Rad) / Mathf.Tan(GameFov * 0.5f * Mathf.Deg2Rad);
        return Mathf.Atan(Mathf.Tan(Mathf.Clamp(degrees, 0f, 80f) * Mathf.Deg2Rad) * k) * Mathf.Rad2Deg;
    }

    /// <summary>
    /// Runs every frame, enabled or not: when the slider, the toggle or the
    /// master switch changes, the camera eases to the new view and the arms
    /// compensation follows.
    /// </summary>
    public static void Tick(Player player)
    {
        ApplyPullBack(player);
        bool active = Active;
        int fov = Mathf.RoundToInt(TargetFov);
        if (active == _lastActive && (!active || fov == _lastFov))
            return;
        _lastActive = active;
        _lastFov = fov;
        Refresh(player);
    }

    private static PlayerSpring _springOwner;
    private static Vector3 _springOriginal;
    private static Vector3 _springWritten;

    /// <summary>
    /// Camera pull-back: moves the hip camera back from the hands by
    /// shifting the game's own camera offset, so more of the gun and arms
    /// fit in the frame. While aiming the game places the camera from the
    /// sight instead, so the sight picture is unchanged and the move in and
    /// out of ADS stays the game's own.
    /// </summary>
    private static void ApplyPullBack(Player player)
    {
        PlayerSpring hc = player?.ProceduralWeaponAnimation?.HandsContainer;
        if (hc == null)
            return;
        // New hands, or the game re-set the offset: that is the new original.
        if (hc != _springOwner || hc.CameraOffset != _springWritten)
        {
            _springOwner = hc;
            _springOriginal = hc.CameraOffset;
        }
        float cm = HandlingConfig.Enabled?.Value == true ? HandlingConfig.CameraPullBack?.Value ?? 0f : 0f;
        hc.CameraOffset = _springOriginal + new Vector3(0f, 0f, -cm * 0.01f);
        _springWritten = hc.CameraOffset;
        if (!Mathf.Approximately(cm, _loggedPullBack))
        {
            _loggedPullBack = cm;
            Utilities.Log.Info($"[FOV] camera pull-back {cm:F1} cm: offset {_springOriginal} -> {hc.CameraOffset}");
        }
    }

    private static float _loggedPullBack = -1f;

    /// <summary>
    /// How far the hip camera is pulled back, in metres. The head-plane guard
    /// in Patch_LerpCamera gives this much room so it does not undo it.
    /// </summary>
    public static float PullBackMeters =>
        HandlingConfig.Enabled?.Value != true ? 0f
            : HandlingState.IsAiming ? AdsPullBackMeters
            : (HandlingConfig.CameraPullBack?.Value ?? 0f) * 0.01f;

    /// <summary>
    /// ADS pull-back in metres: the eye sits further back from the sight.
    /// Only with Dynamic FOV off (its ADS focus zoom is the alternative), and
    /// faded out through magnified scopes, where eye relief sets the picture.
    /// </summary>
    public static float AdsPullBackMeters =>
        HandlingConfig.Enabled?.Value == true && HandlingConfig.DynamicFovEnabled?.Value != true
            ? (HandlingConfig.AdsPullBack?.Value ?? 0f) * 0.01f * (1f - ScopeOpticsSystem.Magnified)
            : 0f;

    private static void Refresh(Player player)
    {
        CameraManager cm = CameraManager.Instance;
        if (player == null || cm == null || cm.Camera == null)
            return;
        try
        {
            if (player.PointOfView != EPointOfView.FirstPerson || !player.HealthController.IsAlive)
                return;

            // Arms: EFT's own compensation curve, fed the new FOV (the
            // CalculateScaleValueByFov prefix substitutes it).
            player.CalculateScaleValueByFov(GameFov);
            player.SetCompensationScale();

            // Camera: the same target EFT picks on a settings change.
            ProceduralWeaponAnimation pwa = player.ProceduralWeaponAnimation;
            float s = GameFov;
            float x = s;
            bool aiming = pwa != null && pwa.IsAiming;
            if (player.MouseLookControl)
                x = s;
            else if (pwa != null && pwa.Sprint)
                x = s - 10f;
            else if (aiming)
                x = pwa.CurrentScope.IsOptic ? OpticFov : s - 15f;
            cm.SetFov(x, 0.5f, !aiming);
        }
        catch (System.Exception ex)
        {
            Utilities.Log.Error("FOV refresh failed: " + ex.Message);
        }
    }
}
