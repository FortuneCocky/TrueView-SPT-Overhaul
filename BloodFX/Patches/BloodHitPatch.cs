using System;
using System.Reflection;
using EFT;
using EFT.Ballistics;
using SPT.Reflection.Patching;

namespace BloodFX.Patches;

/// <summary>Every damage application on a player hands the hit to the blood manager.</summary>
public class BloodHitPatch : ModulePatch
{
    private static bool _loggedError;

    protected override MethodBase GetTargetMethod()
    {
        return typeof(Player).GetMethod("ApplyDamageInfo");
    }

    [PatchPostfix]
    private static void Postfix(Player __instance, DamageInfo damageInfo, EBodyPart bodyPartType)
    {
        if (__instance == null) return;
        BloodManager m = BloodManager.Instance;
        if (m == null) m = BloodFXPlugin.Instance?.EnsureManager();
        if (m == null) return;
        try
        {
            m.OnHit(__instance, ref damageInfo, bodyPartType);
        }
        catch (Exception e)
        {
            if (_loggedError) return; // one report, not one per pellet
            _loggedError = true;
            BloodFXPlugin.Log.LogError($"BloodFX hit error: {e}");
        }
    }
}
