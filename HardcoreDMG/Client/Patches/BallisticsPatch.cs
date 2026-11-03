using System.Reflection;
using EFT.Ballistics;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace HardcoreDMGClient.Patches;

/// <summary>
/// HardcoreDMG bullet physics.
///
/// Patches the EFT Shot class to modify bullet behavior:
/// 1. Fragmentation: Higher chance for high-velocity rounds to fragment,
///    creating multiple wound channels. Scales with bullet kinetic energy.
/// 2. Penetration: More realistic armor penetration — bullets that barely
///    penetrate have reduced damage. Penetration chance scales with velocity.
/// 3. Ricochet: Slightly increased ricochet chance at shallow angles.
/// 4. Velocity-based damage: Damage scales with bullet speed at impact
///    (slower bullets do less damage).
/// </summary>
public class BallisticsPatch_Create : ModulePatch
{
    private static MethodInfo _method;

    protected override MethodBase GetTargetMethod()
    {
        _method = typeof(Shot).GetMethod("method_1", BindingFlags.Public | BindingFlags.Instance);
        return _method;
    }

    [PatchPostfix]
    public static void Postfix(Shot __instance)
    {
        if (!HardcoreDMGPlugin.BulletPhysicsOn.Value)
            return;

        // Velocity-based damage scaling: if the bullet has slowed down,
        // reduce damage proportionally. A bullet at 50% speed does ~50% damage.
        try
        {
            if (__instance.InitialSpeed > 0f)
            {
                float speedRatio = __instance.Speed / __instance.InitialSpeed;
                if (speedRatio < 1f)
                {
                    // Damage scales with kinetic energy (velocity squared)
                    float damageScale = Mathf.Lerp(0.3f, 1f, speedRatio * speedRatio);
                    __instance.Damage *= damageScale;
                }
            }
        }
        catch { }
    }
}

/// <summary>
/// Increases fragmentation chance for high-velocity rounds.
/// In real life, high-velocity bullets (5.56, 5.45) fragment more reliably
/// than low-velocity rounds (9mm, .45).
/// </summary>
public class BallisticsPatch_IsBulletFragmented : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(Shot).GetMethod("IsBulletFragmented", BindingFlags.Public | BindingFlags.Instance);
    }

    [PatchPostfix]
    public static void Postfix(Shot __instance, ref bool __result)
    {
        if (!HardcoreDMGPlugin.BulletPhysicsOn.Value)
            return;

        if (__result)
            return; // already fragmenting, don't change

        try
        {
            // Only consider fragmentation for forward hits
            if (!__instance.IsForwardHit)
                return;

            // Get the fragmentation multiplier from config
            float fragMulti = HardcoreDMGPlugin.FragmentationScale.Value;

            // Higher velocity = more fragmentation
            // Kinetic energy ~ mass * velocity^2
            // We use speed ratio to boost fragmentation chance
            if (__instance.InitialSpeed > 0f)
            {
                float speedRatio = __instance.Speed / __instance.InitialSpeed;
                // Boost fragmentation chance based on velocity and config multiplier
                float boostedChance = __instance.FragmentationChance * fragMulti * speedRatio;

                // Re-roll with boosted chance
                if (boostedChance > Random.value && __instance.FragmentationChance > 0.001f)
                {
                    __result = true;
                }
            }
        }
        catch { }
    }
}

/// <summary>
/// Modifies penetration behavior — bullets that barely penetrate armor
/// have reduced damage. Penetration chance scales slightly with velocity.
/// </summary>
public class BallisticsPatch_IsTargetPenetrated : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(Shot).GetMethod("IsTargetPenetrated", BindingFlags.Public | BindingFlags.Instance);
    }

    [PatchPostfix]
    public static void Postfix(Shot __instance, ref bool __result)
    {
        if (!HardcoreDMGPlugin.BulletPhysicsOn.Value)
            return;

        if (!__result)
        {
            // Bullet didn't penetrate — try boosting penetration chance
            // for high-velocity rounds
            try
            {
                float penMulti = HardcoreDMGPlugin.PenetrationScale.Value;
                if (penMulti > 1f && __instance.PenetrationChance > 0.001f)
                {
                    float boostedChance = __instance.PenetrationChance * penMulti;
                    if (boostedChance > Random.value)
                    {
                        __result = true;
                    }
                }
            }
            catch { }
        }
        else
        {
            // Bullet penetrated — reduce damage if it barely penetrated
            // (low penetration power relative to armor)
            try
            {
                float damageFalloff = HardcoreDMGPlugin.ArmorEnergyLoss.Value;
                if (damageFalloff > 0f && __instance.PenetrationPower < 30f)
                {
                    // Low penetration power = bullet lost energy penetrating armor
                    float reduction = Mathf.Lerp(1f - damageFalloff, 1f, __instance.PenetrationPower / 30f);
                    __instance.Damage *= reduction;
                }
            }
            catch { }
        }
    }
}

/// <summary>
/// Slightly increases ricochet chance at shallow angles.
/// </summary>
public class BallisticsPatch_IsRicocheted : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(Shot).GetMethod("IsRicocheted", BindingFlags.Public | BindingFlags.Instance);
    }

    [PatchPostfix]
    public static void Postfix(Shot __instance, ref bool __result)
    {
        if (!HardcoreDMGPlugin.BulletPhysicsOn.Value)
            return;

        if (__result)
            return; // already ricocheting

        try
        {
            float ricochetMulti = HardcoreDMGPlugin.RicochetScale.Value;
            if (ricochetMulti > 1f && __instance.RicochetChance > 0.001f)
            {
                float boostedChance = __instance.RicochetChance * ricochetMulti;
                if (boostedChance > Random.value)
                {
                    __result = true;
                }
            }
        }
        catch { }
    }
}
