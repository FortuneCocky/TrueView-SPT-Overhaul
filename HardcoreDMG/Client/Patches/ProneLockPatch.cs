using System;
using System.Reflection;
using EFT;
using EFT.HealthSystem;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace HardcoreDMGClient.Patches;

/// <summary>
/// Patches MovementContext.IsInPronePose setter to block standing up from prone
/// when leg HP is below the configured threshold. This prevents the "stand up then
/// forced back down" loop that happens when bots/players have damaged legs.
///
/// The player/bot must heal their legs above the threshold to stand up.
/// </summary>
public class ProneLockPatch : ModulePatch
{
    private static FieldInfo _playerField;

    protected override MethodBase GetTargetMethod()
    {
        // Access the private _player field via the MovementContext type
        _playerField = AccessTools.Field(typeof(EFT.MovementContext), "_player");
        // Target the IsInPronePose property setter
        return AccessTools.PropertySetter(typeof(EFT.MovementContext), "IsInPronePose");
    }

    [PatchPrefix]
    private static bool Prefix(EFT.MovementContext __instance, bool value)
    {
        // Only intercept when trying to STAND UP from prone (value = false)
        // Don't block going INTO prone (value = true)
        if (value)
            return true; // allow going prone

        if (!HardcoreDMGPlugin.EnableProneLock.Value)
            return true;

        // Check if currently prone — if not prone, don't interfere
        if (!__instance.IsInPronePose)
            return true;

        // Get the player
        Player player = _playerField?.GetValue(__instance) as Player;
        if (player == null)
            return true;

        // Leg effects on the local player follow Leg Cripple's "Enable for player"
        if (player.IsYourPlayer && !HardcoreDMGPlugin.LegCrippleEnablePlayer.Value)
            return true;

        // Check leg health
        var hc = player.ActiveHealthController;
        if (hc == null)
            return true;

        float threshold = HardcoreDMGPlugin.ProneLockLegThreshold.Value / 100f;

        float leftLegHp = GetLegHpPercent(hc, EBodyPart.LeftLeg);
        float rightLegHp = GetLegHpPercent(hc, EBodyPart.RightLeg);

        // If either leg is below threshold, block standing up
        if (leftLegHp < threshold || rightLegHp < threshold)
        {
            // Block the setter — stay prone
            return false;
        }

        // Legs are healed enough, allow standing
        return true;
    }

    private static float GetLegHpPercent(ActiveHealthController hc, EBodyPart part)
    {
        try
        {
            var health = hc.GetBodyPartHealth(part, false);
            if (health.Maximum <= 0f)
                return 1f;
            return health.Current / health.Maximum;
        }
        catch
        {
            return 1f;
        }
    }
}

/// <summary>
/// Patches MovementContext.SetPoseLevel to block raising the pose level
/// (standing up from crouch) when leg HP is below the configured threshold.
/// This prevents bots/players from standing fully upright when their legs
/// are severely damaged — they stay crouched until healed.
/// </summary>
public class CrouchLockPatch : ModulePatch
{
    private static FieldInfo _playerField;

    protected override MethodBase GetTargetMethod()
    {
        _playerField = AccessTools.Field(typeof(EFT.MovementContext), "_player");
        return typeof(EFT.MovementContext).GetMethod("SetPoseLevel", BindingFlags.Public | BindingFlags.Instance);
    }

    [PatchPrefix]
    private static bool Prefix(EFT.MovementContext __instance, float poseLevel, bool force)
    {
        if (!HardcoreDMGPlugin.EnableCrouchLock.Value)
            return true;

        // Only block non-forced attempts to raise pose level
        if (force)
            return true;

        // Only block if trying to stand up (poseLevel > crouch threshold ~0.5)
        // PoseLevel: 0 = crouch, 1 = standing
        if (poseLevel <= 0.5f)
            return true; // allow crouching

        // If already prone, don't interfere (prone lock handles that)
        if (__instance.IsInPronePose)
            return true;

        Player player = _playerField?.GetValue(__instance) as Player;
        if (player == null)
            return true;

        if (player.IsYourPlayer && !HardcoreDMGPlugin.LegCrippleEnablePlayer.Value)
            return true;

        var hc = player.ActiveHealthController;
        if (hc == null)
            return true;

        float threshold = HardcoreDMGPlugin.CrouchLockLegThreshold.Value / 100f;

        float leftLegHp = GetLegHpPercent(hc, EBodyPart.LeftLeg);
        float rightLegHp = GetLegHpPercent(hc, EBodyPart.RightLeg);

        // If either leg is below threshold, block standing up — force crouch
        if (leftLegHp < threshold || rightLegHp < threshold)
        {
            // Clamp to crouch level
            __instance.SetPoseLevel(0f, true);
            return false;
        }

        return true;
    }

    private static float GetLegHpPercent(ActiveHealthController hc, EBodyPart part)
    {
        try
        {
            var health = hc.GetBodyPartHealth(part, false);
            if (health.Maximum <= 0f)
                return 1f;
            return health.Current / health.Maximum;
        }
        catch
        {
            return 1f;
        }
    }
}
