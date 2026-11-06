using System;
using System.Collections.Generic;
using System.Reflection;
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
    // The draw clip's real pace is the animator speed times the game's own
    // draw speed (skill buff, or 1.5-2x on a pistol quick-draw). The game
    // itself never goes past 2x, so weight never takes the total past that.
    private const float MaxDrawRate = 2f;

    public static float GetMul(Player.FirearmController fc, float animationSpeed)
    {
        float mul = GetWeightMul(fc._player, fc.Item);
        if (mul <= 1f)
            return mul;
        float gameRate = animationSpeed * GameDrawSpeed(fc);
        if (gameRate * mul > MaxDrawRate)
        {
            float held = Mathf.Max(MaxDrawRate / gameRate, MinMul);
            TacticalStancesPlugin.Log.LogInfo($"Swap speed: game already draws at x{gameRate:F2}, weight boost x{mul:F2} held to x{held:F2}");
            mul = held;
        }
        return mul;
    }

    // SyncWithCharacterSkills has already written SpeedDraw by the time
    // Spawn runs, so this is exactly what the draw clip will use.
    private static float GameDrawSpeed(Player.FirearmController fc)
    {
        try
        {
            var animator = fc.FirearmsAnimator?.Animator;
            if (animator == null) return 1f;
            float draw = AnimationControllerParametersTable.GetFloatSpeedDraw(animator);
            return draw > 0f && !float.IsNaN(draw) ? draw : 1f;
        }
        catch { return 1f; }
    }

    private static float GetWeightMul(Player player, EFT.InventoryLogic.Item item)
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

    // Fail-safe for the 1x restore: if another mod skips or replaces the
    // draw's callback, the restore never runs and the slowed animator speed
    // sticks. The slowest capped draw is well under this.
    private const float RestoreAfter = 6f;
    private static Player.FirearmController _pending;
    private static float _restoreBy;

    public static void DrawStarted(Player.FirearmController fc)
    {
        _pending = fc;
        _restoreBy = Time.unscaledTime + RestoreAfter;
    }

    public static void DrawDone(Player.FirearmController fc)
    {
        if (_pending == fc)
            _pending = null;
    }

    /// A reload means the draw is over: if its speed is still on the
    /// animator (callback skipped or not yet run), the reload would play
    /// at the draw's speed, so put it back to 1x first.
    public static void ReloadStarting(Player.FirearmController fc)
    {
        if (_pending != fc)
            return;
        _pending = null;
        try { fc.FirearmsAnimator?.SetAnimationSpeed(1f); } catch { }
    }

    /// Called every frame from the plugin's Update.
    public static void Tick()
    {
        if (_pending == null || Time.unscaledTime < _restoreBy)
            return;
        var fc = _pending;
        _pending = null;
        try
        {
            fc.FirearmsAnimator?.SetAnimationSpeed(1f);
            TacticalStancesPlugin.Log.LogWarning($"Swap speed: draw of {fc.Item?.LocalizedShortName()} never finished after {RestoreAfter:F0} s (another mod?), animation speed reset to 1x");
        }
        catch { }
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
            animationSpeed *= WeaponSwapSpeed.GetMul(__instance, animationSpeed);

            // SetAnimationSpeed writes the persistent Animator.speed — without
            // resetting it, the swap multiplier leaks into reloads and every
            // later animation. Restore 1x once the draw completes.
            var fc = __instance;
            Action orig = callback;
            WeaponSwapSpeed.DrawStarted(fc);
            callback = () =>
            {
                try { orig?.Invoke(); }
                finally
                {
                    WeaponSwapSpeed.DrawDone(fc);
                    try { fc.FirearmsAnimator?.SetAnimationSpeed(1f); } catch { }
                }
            };
        }
        catch { }
    }
}

[HarmonyPatch]
public static class ReloadSpeedResetPatch
{
    private static readonly string[] ReloadMethods =
    {
        "ReloadMag", "QuickReloadMag", "ReloadGrenadeLauncher",
        "ReloadCylinderMagazine", "ReloadWithAmmo", "ReloadBarrels",
    };

    // Weapon types override these in their own FirearmController
    // subclasses, so patch every declared one.
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var baseType = typeof(Player.FirearmController);
        foreach (var t in baseType.Assembly.GetTypes())
        {
            if (!baseType.IsAssignableFrom(t)) continue;
            foreach (var name in ReloadMethods)
            {
                var m = AccessTools.DeclaredMethod(t, name);
                if (m != null) yield return m;
            }
        }
    }

    [HarmonyPrefix]
    private static void Prefix(Player.FirearmController __instance)
    {
        WeaponSwapSpeed.ReloadStarting(__instance);
    }
}
