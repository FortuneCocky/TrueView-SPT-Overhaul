using System;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace BloodFX.Patches;

/// <summary>Every footstep EFT plays, for bots and you: a foot may step in blood or leave a print.</summary>
public class StepPatch : ModulePatch
{
    private static bool _loggedError;

    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(Player), nameof(Player.PlayStepSound));
    }

    [PatchPostfix]
    private static void Postfix(Player __instance)
    {
        BloodManager m = BloodManager.Instance;
        if (m == null || __instance == null) return;
        try { m.OnStep(__instance); }
        catch (Exception e)
        {
            if (_loggedError) return;
            _loggedError = true;
            BloodFXPlugin.Log.LogError($"BloodFX footstep error: {e}");
        }
    }
}
