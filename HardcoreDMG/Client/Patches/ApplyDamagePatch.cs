using System.Reflection;
using EFT;
using EFT.Ballistics;
using EFT.HealthSystem;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
namespace HardcoreDMGClient.Patches;

/// <summary>
/// Patches ActiveHealthController.ApplyDamage for the local player so that
/// slow attrition can hurt you but never finishes you off:
///  - Hunger, thirst and poison damage can't take a body part below 1 HP.
///  - Light bleeding can't take the head or chest below 15% of their max HP;
///    only heavy bleeds and new wounds can kill from there.
/// </summary>
public class ApplyDamagePatch : ModulePatch
{
    private const float SurvivalFloorHp = 1f;
    private const float LightBleedFloorShare = 0.15f;

    private static FieldInfo _playerField;

    protected override MethodBase GetTargetMethod()
    {
        _playerField = AccessTools.Field(typeof(ActiveHealthController), "Player");
        return typeof(ActiveHealthController).GetMethod("ApplyDamage", BindingFlags.Instance | BindingFlags.Public);
    }

    [PatchPrefix]
    private static void Prefix(ActiveHealthController __instance, EBodyPart bodyPart, ref float damage, DamageInfo damageInfo)
    {
        if (!HardcoreDMGPlugin.DamageModelOn.Value) return;

        Player player = _playerField?.GetValue(__instance) as Player;
        if (player == null || !player.IsYourPlayer) return;

        EDamageType dt = damageInfo.DamageType;
        bool survival = dt == EDamageType.Dehydration || dt == EDamageType.Exhaustion || dt == EDamageType.Poison;
        bool vitalLightBleed = dt == EDamageType.LightBleeding &&
                               (bodyPart == EBodyPart.Head || bodyPart == EBodyPart.Chest);
        if (!survival && !vitalLightBleed) return;

        var health = __instance.GetBodyPartHealth(bodyPart, false);
        float floor = survival ? SurvivalFloorHp : health.Maximum * LightBleedFloorShare;
        damage = Mathf.Clamp(health.Current - floor, 0f, damage);
    }
}
