using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace HardcoreDMGClient.Patches;

/// <summary>
/// Leg Cripple's "Stay down until healed": blocks getting up from prone
/// while the actor's legs are below the prone threshold, so a downed bot
/// doesn't get up and get forced straight back down.
/// </summary>
public class ProneLockPatch : ModulePatch
{
    private static FieldInfo _playerField;

    protected override MethodBase GetTargetMethod()
    {
        _playerField = AccessTools.Field(typeof(EFT.MovementContext), "_player");
        return AccessTools.PropertySetter(typeof(EFT.MovementContext), "IsInPronePose");
    }

    [PatchPrefix]
    private static bool Prefix(EFT.MovementContext __instance, bool value)
    {
        // Only intercept leaving prone; going prone is always allowed.
        if (value || !__instance.IsInPronePose)
            return true;

        return LegCrippleSystem.CrippleLevel(_playerField?.GetValue(__instance) as Player) != LegCrippleSystem.Cripple.Prone;
    }
}

/// <summary>
/// Leg Cripple's "Stay down until healed": blocks standing up from crouch
/// while the actor's legs are below the crouch or prone threshold.
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
        // Only block non-forced attempts to stand (pose level 0 = crouch, 1 = standing).
        // Prone is handled by ProneLockPatch.
        if (force || poseLevel <= 0.5f || __instance.IsInPronePose)
            return true;

        if (LegCrippleSystem.CrippleLevel(_playerField?.GetValue(__instance) as Player) == LegCrippleSystem.Cripple.None)
            return true;

        __instance.SetPoseLevel(0f, true);
        return false;
    }
}
