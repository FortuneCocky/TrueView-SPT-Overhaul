using System.Reflection;
using EFT;
using EFT.Animations;
using HarmonyLib;
using TrueView.Core;
using TrueView.Systems;
using SPT.Reflection.Patching;
using UnityEngine;

namespace TrueView.Patches;

internal class Patch_LerpCamera : ModulePatch
{
    private static readonly FieldInfo _headRotationVecField = AccessTools.Field(typeof(ProceduralWeaponAnimation), "_headRotationVec");

    protected override MethodBase GetTargetMethod()
    {
        return typeof(ProceduralWeaponAnimation).GetMethod("LerpCamera", BindingFlags.Instance | BindingFlags.Public);
    }

    /// <summary>
    /// The game's own recoil on the view this frame, relative to TrueView's
    /// camera rotation. Patch_CalculateCameraPosition writes the camera too
    /// and reuses it so the recoil survives whichever runs last.
    /// </summary>
    internal static Quaternion RecoilDelta = Quaternion.identity;

    [PatchPostfix]
    private static void Postfix(ProceduralWeaponAnimation __instance, float dt)
    {
        Player player = HandlingManager.LocalPlayer();
        if (player == null || player.ProceduralWeaponAnimation != __instance)
        {
            return;
        }

        HandlingManager.Tick(player, Time.deltaTime);

        // TrueView switched off: vanilla owns the camera.
        if (!HandlingState.Enabled)
        {
            RecoilDelta = Quaternion.identity;
            return;
        }

        // Skip camera override during free look and its return to centre —
        // let vanilla handle it
        if (HandlingManager.InFreeLook(player))
        {
            RecoilDelta = Quaternion.identity;
            return;
        }

        // Firearms only — non-weapon hand states (eating, drinking, planting)
        // must keep their own camera animation.
        if (player.HandsController is not Player.FirearmController)
        {
            return;
        }

        JitterProbe.VanillaCamera(__instance);
        ApplyCamera(__instance, dt);
        ClampCameraPushback(__instance, player);
    }

    // Vanilla TurnAway pushes the camera backward to keep the sight line when
    // the barrel is blocked — far enough that you end up staring at the back
    // of your own helmet/arms. Clamp the camera to stay at least slightly in
    // front of the head's forward plane; the weapon still collapses normally.
    internal static void ClampCameraPushback(ProceduralWeaponAnimation pwa, Player player)
    {
        Transform cam = pwa.HandsContainer?.CameraTransform;
        Transform head = player?.PlayerBones?.Head?.Original;
        if (cam == null || head == null)
            return;

        // forward axis = view direction, closest thing to the face plane.
        Vector3 rel = cam.position - head.position;
        float fwd = Vector3.Dot(rel, cam.forward);
        const float minForward = 0.02f;
        if (fwd < minForward)
        {
            cam.position += cam.forward * (minForward - fwd);
            JitterProbe.Pushback(minForward - fwd, minForward - fwd);
        }
    }

    // TrueView's view replaces the camera rotation vanilla just built, so the
    // game's own recoil is put back on top of it, the way vanilla applies it:
    // first the ADS camera follow of the hands' recoil, then the camera kick.
    internal static void ApplyCamera(ProceduralWeaponAnimation __instance, float dt)
    {
        Transform cam = __instance.HandsContainer?.CameraTransform;
        if (cam == null)
            return;

        Vector3 head = HandlingState.HeadRotation;
        Quaternion view = Quaternion.Euler(head);
        cam.localRotation = view;
        try
        {
            ApplyGameRecoil(__instance, cam, head, dt);
        }
        catch
        {
            cam.localRotation = view;
        }
        _headRotationVecField?.SetValue(__instance, head);
        RecoilDelta = Quaternion.Inverse(view) * cam.localRotation;
    }

    private static void ApplyGameRecoil(ProceduralWeaponAnimation pwa, Transform cam, Vector3 head, float dt)
    {
        var recoil = pwa.Shootingg?.CurrentRecoilEffect;
        if (recoil == null)
            return;

        // Vanilla skips the hand-recoil follow whenever the head rotation is
        // non-zero, because it flattens the camera's local yaw. Run it on the
        // view without its yaw and with the head rotation cleared, then put
        // the yaw back. At hip it only keeps its own state in step (it never
        // adds recoil there), so its smoothing is not kept.
        Quaternion yaw = Quaternion.Euler(0f, head.y, 0f);
        Quaternion noYaw = Quaternion.Inverse(yaw) * cam.localRotation;
        cam.localRotation = noYaw;
        _headRotationVecField?.SetValue(pwa, Vector3.zero);
        pwa.AddHandRecoilRotateToCamera(dt);
        cam.localRotation = yaw * (pwa.IsAiming ? cam.localRotation : noYaw);

        cam.localEulerAngles += recoil.GetCameraRotationRecoil() + recoil.WeaponRecoilEffect.GetCameraRotationRecoil();
    }
}