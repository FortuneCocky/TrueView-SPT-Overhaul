using System;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace TacticalStances;

/// <summary>
/// Weapon-weight swap speed. Spawn() drives the draw/equip animation via
/// FirearmsAnimator.SetAnimationSpeed — scaling its animationSpeed argument
/// makes a pistol snap out fast and an LMG come up heavy.
/// </summary>
internal static class WeaponSwapSpeed
{
    // A built ~4 kg rifle at Swap Time 1 draws at this fraction of the game's rate.
    private const float RifleRate = 0.6f;
    private const float ReferenceKg = 4f;
    // Realism caps, as a fraction of the game's own draw rate: the lightest
    // pistol never snaps out faster than 1.3x, the heaviest LMG never takes
    // more than ~2.5x as long. Applied after Swap Time, so no setting gets past them.
    private const float MinMul = 0.4f;
    private const float MaxMul = 1.3f;

    public static float GetMul(Player player, EFT.InventoryLogic.Item item)
    {
        if (player == null || !player.IsYourPlayer || item == null) return 1f;

        // TotalWeight is the gun as carried: receiver, attachments, mag and
        // ammo. Weight alone is just the receiver, which is ~0.4 kg on AR
        // platforms (ADAR, M4, TX-15, MCX) but ~2 kg on an AK, so AR-type
        // guns drew several times faster than everything else.
        float kg;
        try { kg = Mathf.Max(0.3f, item.TotalWeight); }
        catch { return 1f; }

        // Square root: a gun twice as heavy comes up slower, but not twice
        // as slow (the arm motion itself takes time whatever the weight).
        // ~1 kg pistol 1.26x, 3.5 kg carbine 0.64x, 12 kg LMG 0.4x (capped).
        float swapTime = Mathf.Max(0.1f, TacticalStancesPlugin.SwapTime.Value);
        float mul = RifleRate * Mathf.Sqrt(ReferenceKg / kg) / swapTime;
        mul = Mathf.Clamp(mul, MinMul, MaxMul);
        TacticalStancesPlugin.Log.LogInfo($"Swap speed: {item.LocalizedShortName()} {kg:F2} kg, draw x{mul:F2}");
        return mul;
    }
}

[HarmonyPatch(typeof(Player.FirearmController), "Spawn")]
public static class FirearmSwapSpeedPatch
{
    [HarmonyPrefix]
    private static void Prefix(Player.FirearmController __instance, ref float animationSpeed, ref Action callback)
    {
        try
        {
            animationSpeed *= WeaponSwapSpeed.GetMul(__instance._player, __instance.Item);

            // SetAnimationSpeed writes the persistent Animator.speed — without
            // resetting it, the swap multiplier leaks into reloads and every
            // later animation. Restore 1x once the draw completes.
            var fc = __instance;
            Action orig = callback;
            callback = () =>
            {
                try { orig?.Invoke(); }
                finally
                {
                    try { fc.FirearmsAnimator?.SetAnimationSpeed(1f); } catch { }
                }
            };
        }
        catch { }
    }
}
