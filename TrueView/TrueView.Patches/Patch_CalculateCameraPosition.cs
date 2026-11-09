using System.Reflection;
using EFT;
using EFT.Animations;
using HarmonyLib;
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

    // Sight lock: while aiming the game already places the camera on the
    // gun's sight line (behind the sight, along the gun). Turning the view by
    // the same rotation TrueView gave the gun makes the camera look straight
    // down that line, so rear and front sight stay lined up exactly. The
    // rotation is in the weapon root's parent space; the camera gets it in
    // world space, about its own position. The camera rotation is written
    // last in LerpCamera, so this frame's turn is kept here and applied there.
    // The game rewrites the camera's rotation every frame, so nothing builds up.
    private static Quaternion _lockTurn = Quaternion.identity;
    private static Transform _lockParent;
    private static int _lockFrame = -1;

    private static void KeepSightLock(Transform weaponParent, Quaternion gunTurn)
    {
        _lockTurn = gunTurn;
        _lockParent = weaponParent;
        _lockFrame = Time.frameCount;
    }

    internal static void ApplySightLock(Transform camera)
    {
        float lockAmount = ScopeOpticsSystem.SightLock;
        if (lockAmount < 0.001f || camera == null || _lockParent == null || Time.frameCount - _lockFrame > 1)
            return;
        Quaternion parent = _lockParent.rotation;
        Quaternion worldTurn = parent * _lockTurn * Quaternion.Inverse(parent);
        camera.rotation = Quaternion.Slerp(Quaternion.identity, worldTurn, lockAmount) * camera.rotation;
    }

    private static readonly AccessTools.FieldRef<ProceduralWeaponAnimation, Vector3> CameraTarget =
        AccessTools.FieldRefAccess<ProceduralWeaponAnimation, Vector3>("_vCameraTarget");

    // Sight lock: the game works out the aiming camera's spot from the sight's
    // position on the gun. With TrueView's offset still on the gun it would
    // aim the camera at the shifted gun, and the eye would drift off the sight
    // line, so the gun goes back to its clean pose for that calculation.
    [PatchPrefix]
    private static void Prefix(ProceduralWeaponAnimation __instance)
    {
        if (_lastFrame == Time.frameCount || !HandlingState.Enabled || ScopeOpticsSystem.SightLock < 0.001f)
            return;
        Player player = HandlingManager.LocalPlayer();
        if (player == null || player.ProceduralWeaponAnimation != __instance || player.HandsController is not Player.FirearmController)
            return;
        Transform weaponRoot = __instance.HandsContainer?.WeaponRoot;
        if (weaponRoot != null)
            ProceduralHipSystem.ClearWeaponPosOffset(weaponRoot);
    }

    // Where the game is putting the aiming camera, in the weapon root's parent space.
    private static Vector3 AimEye(ProceduralWeaponAnimation pwa, Transform weaponRoot)
    {
        Transform camParent = pwa.HandsContainer?.CameraTransform?.parent;
        if (camParent == null || weaponRoot.parent == null)
            return weaponRoot.localPosition;
        return weaponRoot.parent.InverseTransformPoint(camParent.TransformPoint(CameraTarget(pwa)));
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
                Transform weaponRoot = __instance.HandsContainer.WeaponRoot;
                Quaternion gunTurn = BarrelAimSystem.Correction * ProceduralHipSystem.WeaponOffset;
                weaponRoot.localRotation = gunTurn * weaponRoot.localRotation;
                float sightLock = ScopeOpticsSystem.SightLock;
                Vector3 eye = sightLock > 0.001f ? AimEye(__instance, weaponRoot) : Vector3.zero;
                ProceduralHipSystem.ApplyWeaponPosOffset(weaponRoot, gunTurn, eye, sightLock);
                KeepSightLock(weaponRoot.parent, gunTurn);
                JitterProbe.AfterTrueView();
            }
            else
            {
                ProceduralHipSystem.ClearWeaponPosOffset(__instance.HandsContainer.WeaponRoot);
            }
        }
    }
}