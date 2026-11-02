using EFT;
using EFT.Animations;
using EFT.CameraControl;
using HarmonyLib;
using UnityEngine;

namespace TacticalStances;

/// <summary>
/// Safeguard for scopes going black while aiming (seen prone). The game
/// draws a scope picture only while the current mode's OpticSight is
/// enabled, the optic camera is attached to it, and the lens material's
/// _SwitchToSight fade is off. If any of those drops out while you are
/// steadily aimed through an optic, this puts it back — the same state the
/// game itself sets for "aiming through this sight". Each kind of fix is
/// logged once per ADS so the log shows whether this was the cause.
/// </summary>
[HarmonyPatch(typeof(Player), "LateUpdate")]
[HarmonyPriority(Priority.Low)]
public static class OpticWatchdogPatch
{
    private static readonly int _switchToSightId = Shader.PropertyToID("_SwitchToSight");

    // ADS-in legitimately toggles the optic for a few frames; only step in
    // once aiming has been held this long.
    private const float SettleSeconds = 0.25f;

    private static float _aimSince = -1f;
    private static int _loggedMask;
    private static bool _loggedState;

    [HarmonyPostfix]
    private static void Postfix(Player __instance)
    {
        if (!TacticalStancesPlugin.EnableOpticWatchdog.Value)
            return;
        if (__instance == null || !__instance.IsYourPlayer)
            return;

        try { Check(__instance); }
        catch { }
    }

    private static void Check(Player player)
    {
        ProceduralWeaponAnimation pwa = player.ProceduralWeaponAnimation;
        if (pwa == null || !pwa.IsAiming || !pwa.FirstPersonPointOfView || pwa.ScopeAimTransforms == null || pwa.ScopeAimTransforms.Count == 0)
        {
            _aimSince = -1f;
            _loggedMask = 0;
            _loggedState = false;
            return;
        }

        if (_aimSince < 0f)
            _aimSince = Time.time;
        if (Time.time - _aimSince < SettleSeconds)
            return;

        ScopePrefabCache cache = pwa.CurrentScope?.ScopePrefabCache;
        if (cache == null || !cache.CurrentModHasOptics)
            return;
        OpticSight optic = cache.CurrentModOpticSight;
        if (optic == null)
            return;

        OpticCameraManager mgr = CameraManager.Instance?.OpticCameraManager;

        if (!optic.enabled)
        {
            optic.enabled = true;   // OnEnable un-fades the lens and binds the optic camera
            Report(1, "optic sight was disabled while aiming — re-enabled");
        }
        else if (mgr != null && mgr.CurrentOpticSight != optic)
        {
            mgr.OnOpticSightEnabled(optic);
            Report(2, "optic camera was not bound to the aimed scope — rebound");
        }

        if (mgr?.Camera != null && !mgr.Camera.gameObject.activeSelf)
        {
            mgr.Camera.gameObject.SetActive(true);
            Report(4, "optic camera was inactive while aiming — reactivated");
        }

        Material lens = optic.LensRenderer != null ? optic.LensRenderer.sharedMaterial : null;
        if (lens != null && lens.HasProperty(_switchToSightId) && lens.GetFloat(_switchToSightId) > 0.5f)
        {
            optic.LensFade(isHide: false);
            Report(8, "scope lens was faded to black while aiming — cleared");
        }

        // One state line per ADS while prone, so a black scope the
        // watchdog didn't have to touch still leaves a clue in the log.
        if (!_loggedState && IsProne(player))
        {
            _loggedState = true;
            Camera main = Camera.main;
            float eye = main != null && pwa.CurrentScope.Bone != null
                ? Vector3.Distance(main.transform.position, pwa.CurrentScope.Bone.position) * 1000f
                : -1f;
            TacticalStancesPlugin.Log.LogInfo(
                $"[OpticWatchdog] prone ADS: optic={optic.name} enabled={optic.enabled} " +
                $"bound={(mgr != null && mgr.CurrentOpticSight == optic)} camActive={(mgr?.Camera != null && mgr.Camera.gameObject.activeSelf)} " +
                $"fade={(lens != null && lens.HasProperty(_switchToSightId) ? lens.GetFloat(_switchToSightId) : -1f):0.00} eyeToAimPoint={eye:0}mm");
        }
    }

    private static void Report(int bit, string message)
    {
        if ((_loggedMask & bit) != 0)
            return;
        _loggedMask |= bit;
        TacticalStancesPlugin.Log.LogWarning("[OpticWatchdog] " + message);
    }

    private static bool IsProne(Player player)
    {
        EPlayerState s = player.MovementContext?.CurrentState?.Name ?? EPlayerState.None;
        return s == EPlayerState.ProneIdle || s == EPlayerState.ProneMove ||
               s == EPlayerState.Transit2Prone || s == EPlayerState.Prone2Stand;
    }
}
