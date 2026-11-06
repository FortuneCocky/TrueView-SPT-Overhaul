using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.Ballistics;
using EFT.HealthSystem;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace HardcoreDMGClient.Patches;

/// <summary>
/// Patches Player.ApplyDamageInfo to apply HardcoreDMG's damage model:
///  - Global damage scaling
///  - Head shot bonus damage
///  - Anatomical torso zones (vital core, spine, flanks, pelvis)
///  - Per-body-part damage multipliers
///  - Bleed chance scaling
///  - Disarm/knockdown chances based on wound severity
/// </summary>
public class ApplyDamageInfoPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(Player).GetMethod("ApplyDamageInfo", BindingFlags.Instance | BindingFlags.Public);
    }

    [PatchPrefix]
    private static void Prefix(Player __instance, ref DamageInfo damageInfo, EBodyPart bodyPartType)
    {
        if (!HardcoreDMGPlugin.DamageModelOn.Value) return;

        // Only modify bullet, melee, sniper, blunt, and explosion damage
        EDamageType dt = damageInfo.DamageType;
        bool isWeaponDamage = dt == EDamageType.Bullet || dt == EDamageType.Melee ||
                              dt == EDamageType.Sniper || dt == EDamageType.Blunt ||
                              dt == EDamageType.Explosion;

        if (!isWeaponDamage) return;

        ShotgunModel.Apply(__instance, ref damageInfo);

        Scales s = ScalesFor(__instance);

        // --- Head damage bonus ---
        if (bodyPartType == EBodyPart.Head)
        {
            damageInfo.Damage *= s.Overall * s.Head;
            ApplyBleedModifiers(ref damageInfo, s, 0.5f); // scalp wounds bleed, but rarely heavily
            return;
        }

        // --- Torso zones (chest/stomach) ---
        if (HardcoreDMGPlugin.TorsoZonesOn.Value &&
            (bodyPartType == EBodyPart.Chest || bodyPartType == EBodyPart.Stomach))
        {
            ApplyTorsoZone(__instance, ref damageInfo, bodyPartType, s);
            return;
        }

        // --- Per-body-part multipliers ---
        switch (bodyPartType)
        {
            case EBodyPart.Chest:
                damageInfo.Damage *= s.Overall * s.Chest;
                break;
            case EBodyPart.Stomach:
                damageInfo.Damage *= s.Overall * s.Stomach;
                break;
            case EBodyPart.LeftArm:
            case EBodyPart.RightArm:
                damageInfo.Damage *= s.Overall * s.Arm;
                ApplyBleedModifiers(ref damageInfo, s, 0.35f);
                TryWeaponDrop(__instance, damageInfo, bodyPartType);
                break;
            case EBodyPart.LeftLeg:
            case EBodyPart.RightLeg:
                damageInfo.Damage *= s.Overall * s.Leg;
                ApplyBleedModifiers(ref damageInfo, s, 0.7f); // femoral arteries make thigh hits bleed
                TryWoundFall(__instance, damageInfo, bodyPartType);
                break;
            default:
                damageInfo.Damage *= s.Overall;
                break;
        }
    }

    // ==================== Damage scales ====================

    /// <summary>
    /// The damage and bleed scales for one hit. Hits on the local player use
    /// the Damage (Player) settings when "Use my own damage settings" is on;
    /// everything else uses the main damage model settings.
    /// </summary>
    private readonly struct Scales
    {
        public readonly float Overall, Head, Chest, Stomach, Arm, Leg, HeavyBleed, LightBleed;

        public Scales(float overall, float head, float chest, float stomach, float arm, float leg, float heavyBleed, float lightBleed)
        {
            Overall = overall;
            Head = head;
            Chest = chest;
            Stomach = stomach;
            Arm = arm;
            Leg = leg;
            HeavyBleed = heavyBleed;
            LightBleed = lightBleed;
        }
    }

    private static Scales ScalesFor(Player target)
    {
        if (target.IsYourPlayer && HardcoreDMGPlugin.PlayerDamageOn.Value)
            return new Scales(
                HardcoreDMGPlugin.PlayerDamageScale.Value, HardcoreDMGPlugin.PlayerHeadshotScale.Value,
                HardcoreDMGPlugin.PlayerChestScale.Value, HardcoreDMGPlugin.PlayerStomachScale.Value,
                HardcoreDMGPlugin.PlayerArmScale.Value, HardcoreDMGPlugin.PlayerLegScale.Value,
                HardcoreDMGPlugin.PlayerHeavyBleedScale.Value, HardcoreDMGPlugin.PlayerLightBleedScale.Value);

        return new Scales(
            HardcoreDMGPlugin.DamageScale.Value, HardcoreDMGPlugin.HeadshotScale.Value,
            HardcoreDMGPlugin.ChestScale.Value, HardcoreDMGPlugin.StomachScale.Value,
            HardcoreDMGPlugin.ArmScale.Value, HardcoreDMGPlugin.LegScale.Value,
            HardcoreDMGPlugin.HeavyBleedScale.Value, HardcoreDMGPlugin.LightBleedScale.Value);
    }

    // ==================== Torso zones ====================

    private enum TorsoZone { Core, Vital, Spinal, UpperFlank, LowerFlank, Pelvic }

    /// <summary>
    /// How a torso zone changes a hit. Damage is a multiplier on the incoming
    /// damage. LethalShare, when above 0, guarantees the hit takes at least that
    /// share of the body part's remaining HP (1 = always blacks the part).
    /// </summary>
    private readonly struct ZoneEffect
    {
        public readonly float Damage;
        public readonly float LethalShare;
        public readonly float HeavyBleed;
        public readonly float LightBleed;

        public ZoneEffect(float damage, float lethalShare, float heavyBleed, float lightBleed)
        {
            Damage = damage;
            LethalShare = lethalShare;
            HeavyBleed = heavyBleed;
            LightBleed = lightBleed;
        }
    }

    // Front upper ribcage covers the heart and great vessels; the spine colliders
    // carry the cord; the flanks are mostly muscle and lung edge; the pelvis holds
    // the iliac arteries.
    private static readonly Dictionary<EBodyPartColliderType, TorsoZone> ColliderZones = new()
    {
        [EBodyPartColliderType.RibcageUp] = TorsoZone.Vital,
        [EBodyPartColliderType.SpineTop] = TorsoZone.Spinal,
        [EBodyPartColliderType.SpineDown] = TorsoZone.Spinal,
        [EBodyPartColliderType.RibcageLow] = TorsoZone.Core,
        [EBodyPartColliderType.LeftSideChestUp] = TorsoZone.UpperFlank,
        [EBodyPartColliderType.RightSideChestUp] = TorsoZone.UpperFlank,
        [EBodyPartColliderType.LeftSideChestDown] = TorsoZone.LowerFlank,
        [EBodyPartColliderType.RightSideChestDown] = TorsoZone.LowerFlank,
        [EBodyPartColliderType.Pelvis] = TorsoZone.Pelvic,
        [EBodyPartColliderType.PelvisBack] = TorsoZone.Pelvic,
    };

    private static ZoneEffect EffectFor(TorsoZone zone) => zone switch
    {
        TorsoZone.Vital => new ZoneEffect(2.5f, 1.0f, 6f, 4f),
        TorsoZone.Spinal => new ZoneEffect(1.8f, 0.85f, 1.4f, 1.2f),
        TorsoZone.UpperFlank => new ZoneEffect(1.05f, 0f, 1.1f, 1.2f),
        TorsoZone.LowerFlank => new ZoneEffect(0.8f, 0f, 0.7f, 0.6f),
        TorsoZone.Pelvic => new ZoneEffect(1.15f, 0f, 1.3f, 1.1f),
        _ => new ZoneEffect(1f, 0f, 1f, 1f),
    };

    /// <summary>
    /// Applies the torso zone for a chest/stomach hit, picked from the collider
    /// that was struck. Unknown colliders count as the core.
    /// </summary>
    private static void ApplyTorsoZone(Player player, ref DamageInfo di, EBodyPart bodyPart, Scales s)
    {
        if (!ColliderZones.TryGetValue(di.BodyPartColliderType, out TorsoZone zone))
            zone = TorsoZone.Core;

        ZoneEffect fx = EffectFor(zone);
        float partMul = bodyPart == EBodyPart.Chest ? s.Chest : s.Stomach;

        float damage = di.Damage * fx.Damage * partMul;

        if (fx.LethalShare > 0f)
        {
            float remaining = CurrentHealth(player, bodyPart);
            if (remaining > 0f)
                damage = Mathf.Max(damage, remaining * fx.LethalShare);
        }

        di.Damage = damage * s.Overall;
        di.HeavyBleedingDelta *= fx.HeavyBleed * s.HeavyBleed;
        di.LightBleedingDelta *= fx.LightBleed * s.LightBleed;
    }

    /// <summary>
    /// Scales bleed deltas by the configured multiplier and a body-part factor.
    /// </summary>
    private static void ApplyBleedModifiers(ref DamageInfo di, Scales s, float bodyPartBleedFactor)
    {
        di.HeavyBleedingDelta *= s.HeavyBleed * bodyPartBleedFactor;
        di.LightBleedingDelta *= s.LightBleed * bodyPartBleedFactor;
    }

    // ==================== Wound severity ====================

    private static float CurrentHealth(Player player, EBodyPart bodyPart)
    {
        try
        {
            var hc = player.ActiveHealthController;
            if (hc != null) return hc.GetBodyPartHealth(bodyPart, false).Current;
        }
        catch { }
        return 0f;
    }

    private static float MaxHealth(Player player, EBodyPart bodyPart)
    {
        try
        {
            var hc = player.ActiveHealthController;
            if (hc != null) return hc.GetBodyPartHealth(bodyPart, false).Maximum;
        }
        catch { }
        return 0f;
    }

    /// <summary>
    /// How bad a single hit is for a limb: the hit's damage as a share of the
    /// limb's max HP, capped at 1.5 so overkill shots don't explode the odds.
    /// </summary>
    private static float WoundSeverity(Player player, EBodyPart bodyPart, float damage)
    {
        float max = MaxHealth(player, bodyPart);
        if (max <= 0f) return 0f;
        return Mathf.Clamp(damage / max, 0f, 1.5f);
    }

    /// <summary>
    /// Chance to drop the weapon when hit in an arm. Bigger wounds relative to
    /// the arm's HP raise the odds; forearm hits (hand and wrist) count extra.
    /// Uses reflection to avoid hard references to obfuscated inventory types.
    /// </summary>
    private static void TryWeaponDrop(Player player, DamageInfo di, EBodyPart bodyPart)
    {
        if (!HardcoreDMGPlugin.WeaponDropOn.Value) return;
        if (!player.IsYourPlayer) return;

        try
        {
            float severity = WoundSeverity(player, bodyPart, di.Damage);
            bool forearm = di.BodyPartColliderType == EBodyPartColliderType.LeftForearm ||
                           di.BodyPartColliderType == EBodyPartColliderType.RightForearm;

            float chancePercent = HardcoreDMGPlugin.WeaponDropOdds.Value * (1f + 2f * severity);
            if (forearm) chancePercent *= 1.4f;

            if (Random.value * 100f >= chancePercent) return;

            var fc = player.HandsController as Player.FirearmController;
            if (fc == null || fc.Item == null) return;

            var controller = player.InventoryController;
            if (controller == null) return;

            var canThrowMethod = controller.GetType().GetMethod("CanThrow");
            var tryThrowMethod = controller.GetType().GetMethod("TryThrowItem");
            if (canThrowMethod == null || tryThrowMethod == null) return;

            if ((bool)canThrowMethod.Invoke(controller, new object[] { fc.Item }))
                tryThrowMethod.Invoke(controller, new object[] { fc.Item, null, false });
        }
        catch { }
    }

    /// <summary>
    /// Chance for the player to fall prone when a leg hit leaves the leg badly
    /// hurt. Only rolls once the leg drops below 35% HP; the lower it goes and
    /// the bigger the wound, the likelier the fall. Thigh hits (femur) count extra.
    /// </summary>
    private static void TryWoundFall(Player player, DamageInfo di, EBodyPart bodyPart)
    {
        if (!HardcoreDMGPlugin.WoundFallOn.Value) return;
        if (!player.IsYourPlayer) return;

        try
        {
            if (player.MovementContext == null || player.MovementContext.IsInPronePose) return;

            float max = MaxHealth(player, bodyPart);
            if (max <= 0f) return;

            float remainingShare = Mathf.Clamp01((CurrentHealth(player, bodyPart) - di.Damage) / max);
            if (remainingShare > 0.35f) return;

            float severity = WoundSeverity(player, bodyPart, di.Damage);
            bool thigh = di.BodyPartColliderType == EBodyPartColliderType.LeftThigh ||
                         di.BodyPartColliderType == EBodyPartColliderType.RightThigh;

            float chancePercent = HardcoreDMGPlugin.WoundFallOdds.Value
                                  * (1f + severity)
                                  * (1.5f - remainingShare);
            if (thigh) chancePercent *= 1.25f;

            if (Random.value * 100f < chancePercent)
                player.ToggleProne();
        }
        catch { }
    }
}
