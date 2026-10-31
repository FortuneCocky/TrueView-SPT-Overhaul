using System;
using System.Reflection;
using EFT;
using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// While Euphoria (or the get-up) owns a bot's body he cannot shoot: every
/// FirearmController.SetTriggerPressed(true) is turned into a release. The one
/// exception is an "aim-back" shot — a stumbling bot who rolled the aim-back
/// chance has his gun arm pointed at the shooter, and shots that line up are
/// let through at a human rate of fire.
/// </summary>
internal static class FireBlock
{
    private static FieldInfo _playerField;
    private static bool _fieldSearched;
    private static int _blocked, _logged;

    public static void Install()
    {
        var harmony = new HarmonyLib.Harmony("com.euphoria.reaction.fire");
        var pre = new HarmonyLib.HarmonyMethod(typeof(FireBlock).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
        int n = 0;
        foreach (Type t in typeof(Player).Assembly.GetTypes())
        {
            if (!t.IsClass || !t.Name.EndsWith("FirearmController")) continue;
            MethodInfo m = t.GetMethod("SetTriggerPressed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                                       null, new[] { typeof(bool) }, null);
            if (m == null) continue;
            try { harmony.Patch(m, prefix: pre); n++; EuphoriaPlugin.Log($"Delirium: fire block on {t.FullName}.SetTriggerPressed"); }
            catch (Exception e) { EuphoriaPlugin.Log("Delirium: fire block " + t.Name + ": " + e.Message); }
        }
        // second layer: the bullet launch itself (the AI may not go through the trigger call)
        var pre2 = new HarmonyLib.HarmonyMethod(typeof(FireBlock).GetMethod(nameof(LaunchPrefix), BindingFlags.Static | BindingFlags.NonPublic));
        foreach (Type t in typeof(Player).Assembly.GetTypes())
        {
            if (!t.IsClass || !t.Name.EndsWith("FirearmController")) continue;
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (m.Name != "InitiateShot" || m.IsAbstract) continue;
                try { harmony.Patch(m, prefix: pre2); n++; EuphoriaPlugin.Log($"Delirium: fire block on {t.FullName}.InitiateShot"); }
                catch (Exception e) { EuphoriaPlugin.Log("Delirium: fire block launch " + t.Name + ": " + e.Message); }
            }
        }
        EuphoriaPlugin.Log($"Delirium: fire block on {n} method(s)");
    }

    private static Player PlayerOf(object ctl)
    {
        if (!_fieldSearched)
        {
            _fieldSearched = true;
            for (Type t = ctl.GetType(); t != null && _playerField == null; t = t.BaseType)
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (f.FieldType == typeof(Player)) { _playerField = f; break; }
        }
        return _playerField?.GetValue(ctl) as Player;
    }

    private static int _launchLogged;

    // Returning false skips the launch: no bullet, no ammo spent.
    private static bool LaunchPrefix(object __instance)
    {
        try
        {
            Player p = PlayerOf(__instance);
            if (p != null && p.IsYourPlayer) return true;
            if (!(EuphoriaPlugin.Instance?.BlockFire.Value ?? true)) return true;
            if (p == null) return true;
            if (NmBody.LaunchAllowedFor(p)) return true;
            if (_launchLogged < 5) { _launchLogged++; EuphoriaPlugin.Log("Delirium: blocked a bot bullet launch during Euphoria"); }
            return false;
        }
        catch { return true; }
    }

    // Harmony prefix: rewrite the argument, never skip the call (the release must still happen).
    // typed ref arg, not object[] __args: that made Harmony build an array and box the bool on every call
    private static void Prefix(object __instance, ref bool __0)
    {
        if (!__0) return;
        try
        {
            Player p = PlayerOf(__instance);
            if (p != null && p.IsYourPlayer) return;
            if (!(EuphoriaPlugin.Instance?.BlockFire.Value ?? true)) return;
            if (p == null) return;
            if (NmBody.ShotAllowedFor(p)) return;
            __0 = false;
            if (_logged < 3 && ++_blocked == 1) { _logged++; EuphoriaPlugin.Log("Delirium: blocked a bot trigger pull during Euphoria"); }
        }
        catch { }
    }
}
