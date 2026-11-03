using System.Reflection;
using EFT;
using EFT.Animations;
using EFT.CameraControl;
using TrueView.Core;
using TrueView.Systems;
using SPT.Reflection.Patching;
using UnityEngine;

namespace TrueView.Patches;

/// <summary>
/// Every FOV transition the game starts (hip, sprint, ADS, mouse look):
/// the target is rebuilt around the FOV slider. The coroutine then eases
/// to it on its own, so DynamicFovSystem sees it as a normal game write.
/// </summary>
internal class Patch_SetFov : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(CameraManager).GetMethod("SetFov", BindingFlags.Instance | BindingFlags.Public);
    }

    [PatchPrefix]
    private static void Prefix(ref float x)
    {
        x = FieldOfViewSystem.Map(x);
    }
}

/// <summary>
/// ADS pull-back: while aiming the game aims the camera at the sight bone;
/// the target is moved back along the view so the eye sits further from the
/// sight. LerpCamera eases to it, so going in and out of ADS stays smooth.
/// </summary>
internal class Patch_AdsPullBack : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(ProceduralWeaponAnimation).GetMethod("CalculateCameraPosition", BindingFlags.Instance | BindingFlags.Public);
    }

    [PatchPostfix]
    private static void Postfix(ProceduralWeaponAnimation __instance)
    {
        if (!__instance.IsAiming || HandlingManager.LocalPlayer()?.ProceduralWeaponAnimation != __instance)
            return;
        float m = FieldOfViewSystem.AdsPullBackMeters;
        if (m > 0f)
            __instance._vCameraTarget.z -= m;
    }
}

/// <summary>
/// The game's settings value is written straight to the camera when it
/// changes and at startup, and handed on to the arms compensation.
/// </summary>
internal class Patch_ApplyFoV : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(CameraManager).GetMethod("ApplyFoV", BindingFlags.Instance | BindingFlags.Public);
    }

    [PatchPrefix]
    private static void Prefix(ref int fov)
    {
        if (FieldOfViewSystem.Active)
            fov = Mathf.RoundToInt(FieldOfViewSystem.TargetFov);
    }
}

/// <summary>
/// EFT pulls the arms closer as FOV widens (ribcage scale 1 at 50 down to
/// 0.65 at 75) so the gun keeps its size on screen. Its callers pass the
/// game setting or the camera's current FOV; with the override on it is
/// always the slider value.
/// </summary>
internal class Patch_CalculateScaleValueByFov : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(Player).GetMethod("CalculateScaleValueByFov", BindingFlags.Instance | BindingFlags.Public);
    }

    [PatchPrefix]
    private static void Prefix(ref float fov)
    {
        if (FieldOfViewSystem.Active)
            fov = FieldOfViewSystem.TargetFov;
    }
}
