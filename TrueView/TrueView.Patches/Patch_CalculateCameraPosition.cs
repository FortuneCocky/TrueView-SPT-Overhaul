using System.Reflection;
using EFT;
using EFT.Animations;
using TrueView.Configuration;
using TrueView.Core;
using TrueView.Systems;
using SPT.Reflection.Patching;
using UnityEngine;

namespace TrueView.Patches;

internal class Patch_CalculateCameraPosition : ModulePatch
{
    private static int _lastFrame = -1;

    protected override MethodBase GetTargetMethod()
    {
        return typeof(ProceduralWeaponAnimation).GetMethod("CalculateCameraPosition", BindingFlags.Instance | BindingFlags.Public);
    }

    [PatchPostfix]
    private static void Postfix(ProceduralWeaponAnimation __instance)
    {
        if (_lastFrame == Time.frameCount)
        {
            return;
        }

        Player player = HandlingManager.LocalPlayer();
        if (player == null || player.ProceduralWeaponAnimation != __instance)
        {
            return;
        }
        _lastFrame = Time.frameCount;

        // TrueView switched off: vanilla owns the camera and the weapon.
        if (!HandlingState.Enabled)
        {
            return;
        }

        // Skip weapon offset during free look and its return to centre —
        // let vanilla handle it
        if (HandlingManager.InFreeLook(player))
        {
            return;
        }

        // Firearms only — eating/drinking/interaction controllers keep
        // ProceduralWeaponAnimation alive, and our camera/weapon writes
        // fight their animations.
        if (player.HandsController is not Player.FirearmController)
        {
            return;
        }

        if (__instance.HandsContainer?.CameraTransform != null)
        {
            // Keep the game's recoil from the last camera update on top.
            __instance.HandsContainer.CameraTransform.localRotation = Quaternion.Euler(HandlingState.HeadRotation) * Patch_LerpCamera.RecoilDelta;
        }

        // Apply the shared procedural weapon offset (weapon rotation relative
        // to camera) so hip fire and ADS preserve the same firing direction
        // through transitions. Skip when in left stance (shoulder swap) so the
        // weapon stays attached to the hands instead of floating off.
        if (__instance.HandsContainer?.WeaponRoot != null)
        {
            bool isLeftStance = false;
            try
            {
                isLeftStance = player.MovementContext?.LeftStanceController?.LeftStance ?? false;
            }
            catch { }

            if (!isLeftStance)
            {
                __instance.HandsContainer.WeaponRoot.localRotation = BarrelAimSystem.Correction * ProceduralHipSystem.WeaponOffset * __instance.HandsContainer.WeaponRoot.localRotation;
                ProceduralHipSystem.ApplyWeaponPosOffset(__instance.HandsContainer.WeaponRoot);
                JitterProbe.AfterTrueView();
            }
            else
            {
                ProceduralHipSystem.ClearWeaponPosOffset(__instance.HandsContainer.WeaponRoot);
            }
        }
    }
}