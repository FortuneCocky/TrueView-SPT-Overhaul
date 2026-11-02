using EFT;
using HarmonyLib;

namespace TacticalStances;

/// <summary>
/// Keeps the scope on the eye while aiming prone. When the barrel touches
/// something (ground, grass, own body), the game's TurnAway effector pulls
/// the gun back and shifts the camera — with a stronger prone preset, and
/// scaled up at high FOV. Aimed through a scope that puts the eye off its
/// axis and the lens goes black. While the local player is aiming prone,
/// the effector's overlap input is zeroed so it applies no pull. The game's
/// own "barrel buried → drop out of ADS" check reads the controller's
/// overlap value, not this, so it still works.
/// </summary>
[HarmonyPatch(typeof(TurnAwayEffector), nameof(TurnAwayEffector.Process))]
public static class ProneScopeGuardPatch
{
    private static TurnAwayEffector _localTurnAway;
    private static bool _guard;

    [HarmonyPrefix]
    private static void Prefix(TurnAwayEffector __instance)
    {
        if (!_guard || __instance != _localTurnAway)
            return;
        __instance.OverlapDepth = 0f;
        __instance.OverlapValue = 0f;
    }

    /// <summary>Called every frame from the plugin Update for the local player.</summary>
    internal static void Tick(Player player)
    {
        _guard = false;
        _localTurnAway = null;
        if (player == null || !TacticalStancesPlugin.EnableProneScopeGuard.Value)
            return;

        try
        {
            var pwa = player.ProceduralWeaponAnimation;
            if (pwa == null)
                return;
            _localTurnAway = pwa.TurnAway;
            _guard = pwa.IsAiming && pwa.FirstPersonPointOfView &&
                     player.MovementContext != null && player.MovementContext.IsInPronePose;
        }
        catch { }
    }
}
