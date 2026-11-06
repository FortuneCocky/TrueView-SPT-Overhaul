using System.Reflection;
using EFT;
using TrueView.Core;
using TrueView.Utilities;
using SPT.Reflection.Patching;
using UnityEngine;

namespace TrueView.Patches;

/// <summary>
/// Safety cap on the local player's weapon animation speeds. Skill buffs,
/// weapon stats and other mods all push their speeds through these setters,
/// and stacked together they can play reloads and draws at silly speeds on
/// any gun. Each value is held to what vanilla itself can reach; anything
/// inside that range passes through untouched. Bots and other players are
/// never touched.
/// </summary>
internal static class AnimationSpeedGuard
{
    // Whole hands animator (every hand animation at once). Vanilla keeps it at 1.
    public const float AnimatorMin = 0.4f, AnimatorMax = 1.3f;
    // Reload clips. Vanilla tops out around 1.3 with maxed skills.
    public const float ReloadMin = 0.2f, ReloadMax = 1.35f;
    // Draw / holster. Vanilla quick-draw tops out at 2.
    public const float DrawMin = 0.4f, DrawMax = 2f;
    // Malfunction clearing. Vanilla Troubleshooting elite stays under 1.5.
    public const float FixMin = 0.25f, FixMax = 1.5f;

    private static float _lastLogged = float.NaN;

    private static bool Active => HandlingState.Enabled;

    public static bool OwnsHands(ObjectInHandsAnimator animator)
    {
        if (!Active || animator == null)
            return false;
        Player player = HandlingManager.LocalPlayer();
        return player != null && player.HandsAnimator == animator;
    }

    public static bool OwnsBody(PlayerAnimator animator)
    {
        if (!Active || animator == null)
            return false;
        Player player = HandlingManager.LocalPlayer();
        return player != null && player.MovementContext?.PlayerAnimator == animator;
    }

    public static void Clamp(ref float value, float min, float max, string what)
    {
        // NaN or infinity from a broken buff: fall back to normal speed.
        float clamped = float.IsNaN(value) || float.IsInfinity(value) ? 1f : Mathf.Clamp(value, min, max);
        if (clamped == value)
            return;
        if (value != _lastLogged)
        {
            _lastLogged = value;
            Log.Info($"Animation speed guard: {what} speed {value:0.###} held to {clamped:0.###}");
        }
        value = clamped;
    }
}

internal class Patch_HandsAnimationSpeed : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => typeof(ObjectInHandsAnimator).GetMethod("SetAnimationSpeed", BindingFlags.Instance | BindingFlags.Public);

    [PatchPrefix]
    private static void Prefix(ObjectInHandsAnimator __instance, ref float speed)
    {
        if (AnimationSpeedGuard.OwnsHands(__instance))
            AnimationSpeedGuard.Clamp(ref speed, AnimationSpeedGuard.AnimatorMin, AnimationSpeedGuard.AnimatorMax, "hands");
    }
}

internal class Patch_WeaponSpeedParameters : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => typeof(FirearmsAnimator).GetMethod("SetSpeedParameters", BindingFlags.Instance | BindingFlags.Public);

    [PatchPrefix]
    private static void Prefix(FirearmsAnimator __instance, ref float reload, ref float draw)
    {
        if (!AnimationSpeedGuard.OwnsHands(__instance))
            return;
        AnimationSpeedGuard.Clamp(ref reload, AnimationSpeedGuard.ReloadMin, AnimationSpeedGuard.ReloadMax, "reload");
        AnimationSpeedGuard.Clamp(ref draw, AnimationSpeedGuard.DrawMin, AnimationSpeedGuard.DrawMax, "draw");
    }
}

internal class Patch_WeaponFixSpeed : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => typeof(FirearmsAnimator).GetMethod("SetMalfRepairSpeed", BindingFlags.Instance | BindingFlags.Public);

    [PatchPrefix]
    private static void Prefix(FirearmsAnimator __instance, ref float fix)
    {
        if (AnimationSpeedGuard.OwnsHands(__instance))
            AnimationSpeedGuard.Clamp(ref fix, AnimationSpeedGuard.FixMin, AnimationSpeedGuard.FixMax, "malfunction fix");
    }
}

// The body animator gets the same reload and draw speeds so third-person
// arms stay in step with the first-person hands.
internal class Patch_BodySpeedParameters : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => typeof(PlayerAnimator).GetMethod("SetSpeedParameters", BindingFlags.Instance | BindingFlags.Public);

    [PatchPrefix]
    private static void Prefix(PlayerAnimator __instance, ref float reload, ref float draw)
    {
        if (!AnimationSpeedGuard.OwnsBody(__instance))
            return;
        AnimationSpeedGuard.Clamp(ref reload, AnimationSpeedGuard.ReloadMin, AnimationSpeedGuard.ReloadMax, "body reload");
        AnimationSpeedGuard.Clamp(ref draw, AnimationSpeedGuard.DrawMin, AnimationSpeedGuard.DrawMax, "body draw");
    }
}
