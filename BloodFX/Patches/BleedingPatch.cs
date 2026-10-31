using System.Collections.Generic;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using Systems.Effects;
using UnityEngine;

namespace BloodFX.Patches;

/// <summary>
/// EFT marks a bleeding character's path by stamping a flat blood decal under them every 1-3 s.
/// This keeps that timing per character but hands each tick to the blood manager, which drips
/// real drops from the wound instead. With BleedDrips off (or BloodFX disabled) EFT's own decals run.
/// </summary>
public class BleedingPatch : ModulePatch
{
    private static readonly FieldInfo Times = AccessTools.Field(typeof(EffectsCommutator), "_playerBleedingDecalTimes");
    private static bool _loggedError;

    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(EffectsCommutator), nameof(EffectsCommutator.UpdatePlayersBleedings));
    }

    [PatchPrefix]
    private static bool Prefix(EffectsCommutator __instance)
    {
        BloodManager m = BloodManager.Instance;
        if (m == null || !m.ReplacesBleeding || Times == null) return true;
        try
        {
            var list = (List<KeyValuePair<IPlayer, float>>)Times.GetValue(__instance);
            if (list == null) return true;
            float time = Time.time;
            Vector2 delta = __instance._minMaxBleedingSpawnDelta;
            for (int i = 0; i < list.Count; i++)
            {
                KeyValuePair<IPlayer, float> kv = list[i];
                if (time <= kv.Value) continue;
                m.OnBleedTick(kv.Key as Player);
                list[i] = new KeyValuePair<IPlayer, float>(kv.Key, time + Random.Range(delta.x, delta.y));
            }
            return false;
        }
        catch (System.Exception e)
        {
            if (!_loggedError) BloodFXPlugin.Log.LogError($"BloodFX bleeding error (EFT's own bleeding decals are used instead): {e}");
            _loggedError = true;
            return true;
        }
    }
}
