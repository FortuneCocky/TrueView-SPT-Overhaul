using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace EuphoriaReaction.Patches;

/// <summary>
/// A bot in his death struggle isn't dead yet as far as the player is concerned: no loot / search
/// interaction on his body until Euphoria hands him to SPT's corpse ragdoll. EFT builds the
/// interaction menu in InteractionContextHelper.GetAvailableActions (the generic IInteractive
/// overload and the LootItem one a corpse goes through); while he's dying they return nothing.
/// </summary>
internal static class DyingLootBlock
{
    public static void Install()
    {
        var harmony = new HarmonyLib.Harmony("com.euphoria.reaction.dyinglootblock");
        Type helper = typeof(EFT.Player).Assembly.GetTypes().FirstOrDefault(t => t.Name == "InteractionContextHelper");
        if (helper == null) { EuphoriaPlugin.Log("Delirium: loot block: InteractionContextHelper not found"); return; }
        var prefix = new HarmonyLib.HarmonyMethod(typeof(DyingLootBlock).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
        int n = 0;
        foreach (MethodInfo m in helper.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name != "GetAvailableActions") continue;
            ParameterInfo[] ps = m.GetParameters();
            if (ps.Length != 2 || (ps[1].ParameterType.Name != "IInteractive" && ps[1].ParameterType.Name != "LootItem")) continue;
            if (m.ReturnType.IsValueType) continue; // "nothing" is null
            try { harmony.Patch(m, prefix: prefix); n++; }
            catch (Exception e) { EuphoriaPlugin.Log("Delirium: loot block " + ps[1].ParameterType.Name + ": " + e.Message); }
        }
        EuphoriaPlugin.Log($"Delirium: no looting during a death struggle ({n} interaction method(s))");
    }

    private static int _logged;

    // false = skip: no interactions on a body that is still dying
    // runs every frame for whatever the player looks at: typed arg, no object[] __args
    private static bool Prefix(object __1, ref object __result)
    {
        try
        {
            if (!(__1 is Component c) || c == null) return true;
            NmBody body = c.GetComponent<NmBody>() ?? c.GetComponentInParent<NmBody>();
            if (body == null || !body.Active || !body.Dying) return true;
            __result = null;
            if (_logged++ < 3) EuphoriaPlugin.LogV("Delirium: loot blocked - he's still dying");
            return false;
        }
        catch { return true; }
    }
}
