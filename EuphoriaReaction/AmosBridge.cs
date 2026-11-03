using System;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using EFT;
using EFT.Ballistics;
using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// Working alongside amos's Euphoria Style Physics (amos.euphoriastylephysics) when it is installed.
/// A bot belongs to one mod at a time: we own living stumbles, balance, falls and get-ups; his mod owns
/// deaths, corpses, hit flinch, stagger and explosions. His mod hits a bot from Player.ShotReactions, which
/// EFT calls right after our Player.ApplyDamageInfo postfix. From 0.19.0.5 we register an owner check with
/// his public Api (EuphoriaStylePhysics.Api, see its API.md) and ask it whether his knockdown has a bot.
/// Older versions have no Api: there his hit and blast handlers are told to leave our bots alone by
/// prefixes on his internal methods, found by name (a renamed one is logged and simply left unpatched).
/// </summary>
internal static class AmosBridge
{
    internal const string Guid = "amos.euphoriastylephysics";

    private static BaseUnityPlugin _amos;
    private static Func<Player, bool> _hisKnockdown; // his Knockdown.IsActive: his knockdown or stumble has the bot
    private static ConfigEntry<bool> _hisDying, _hisFlinch;
    private static int _leftAlone, _ourSkipped;

    /// His mod is loaded and "Work with Euphoria Style Physics" is on.
    internal static bool On => _amos != null && (EuphoriaPlugin.Instance?.AmosCoop?.Value ?? true);

    /// His dying is on: deaths and corpses are his (our death struggle, corpse hand-back and corpse weights stay off).
    internal static bool OwnsDeaths => On && (_hisDying == null || _hisDying.Value);

    /// His hit flinch is on: it rocks every hit bot, so our hit nudge stays off.
    internal static bool FlinchesHits => On && (_hisFlinch == null || _hisFlinch.Value);

    /// Explosions are his (blast flinch, blast knockdown): no stumble of ours starts from one.
    internal static bool OwnsBlast(DamageInfo di) =>
        On && (di.DamageType & (EDamageType.Explosion | EDamageType.Landmine | EDamageType.GrenadeFragment)) != 0;

    /// His knockdown or stumble has this bot: we don't take him.
    internal static bool HisBusy(Player p)
    {
        if (!On || _hisKnockdown == null || p == null) return false;
        try { return _hisKnockdown(p); }
        catch { return false; }
    }

    internal static void LogSkip(Player p, string what)
    {
        if (_ourSkipped++ == 0 || (EuphoriaPlugin.Instance?.VerboseLog?.Value ?? false))
            EuphoriaPlugin.Log($"Delirium: {p?.ProfileId} {what} left to Euphoria Style Physics");
    }

    internal static void Init(ConfigEntry<bool> turnOffHis)
    {
        if (!Chainloader.PluginInfos.TryGetValue(Guid, out PluginInfo info) || info.Instance == null)
        {
            EuphoriaPlugin.Log("Euphoria Style Physics not installed: every reaction is ours");
            return;
        }
        _amos = info.Instance;
        Assembly asm = _amos.GetType().Assembly;
        ConfigFile cfg = _amos.Config;
        cfg.TryGetEntry("2. Dying", "Dying with muscles", out _hisDying);
        cfg.TryGetEntry("3. Hit flinch", "Hit flinch", out _hisFlinch);
        EuphoriaPlugin.Log($"Euphoria Style Physics {info.Metadata.Version} found: it owns deaths ({(_hisDying != null ? (_hisDying.Value ? "on" : "its dying off: ours") : "setting not found")}), "
                           + $"corpses, hit flinch ({(_hisFlinch != null ? (_hisFlinch.Value ? "on" : "off: our hit nudge") : "setting not found")}), stagger and explosions; "
                           + "we own living stumbles, falls and get-ups");

        // its own stumble and knockdowns do what ours does: two physics bodies on one skeleton
        Switch("5. Stumble", "Stumble when shot", turnOffHis.Value);
        Switch("6. Knockdowns", "Knockdowns", turnOffHis.Value);

        // 0.19.0.5+: its public Api (by reflection: no build reference, and older versions still load)
        Type api = asm.GetType("EuphoriaStylePhysics.Api");
        MethodInfo register = api?.GetMethod("RegisterOwner", new[] { typeof(string), typeof(Func<Player, bool>) });
        MethodInfo knocked = api?.GetMethod("IsKnockedDown", new[] { typeof(Player) });
        if (register != null && knocked != null && knocked.ReturnType == typeof(bool))
        {
            _hisKnockdown = (Func<Player, bool>)Delegate.CreateDelegate(typeof(Func<Player, bool>), knocked);
            register.Invoke(null, new object[] { "com.euphoria.reaction", (Func<Player, bool>)OwnedAlive });
            object ver = api.GetField("ApiVersion")?.GetValue(null) ?? api.GetProperty("ApiVersion")?.GetValue(null); // a const int
            EuphoriaPlugin.Log($"Euphoria Style Physics: Api v{ver ?? "?"} - owner check registered, its knockdowns asked through IsKnockedDown");
            return;
        }
        EuphoriaPlugin.Log("Euphoria Style Physics: no Api (before 0.19.0.5) - patching its hit handlers instead");

        Type kd = asm.GetType("EuphoriaStylePhysics.Knockdown");
        MethodInfo isActive = kd == null ? null : HarmonyLib.AccessTools.Method(kd, "IsActive", new[] { typeof(Player) });
        if (isActive != null && isActive.ReturnType == typeof(bool))
            _hisKnockdown = (Func<Player, bool>)Delegate.CreateDelegate(typeof(Func<Player, bool>), isActive);
        else
            EuphoriaPlugin.Log("Euphoria Style Physics: Knockdown.IsActive not found - its knockdowns can't be told apart from free bots");

        var harmony = new HarmonyLib.Harmony("com.euphoria.reaction.amos");
        var skip = new HarmonyLib.HarmonyMethod(typeof(AmosBridge).GetMethod(nameof(LeaveOursVoid), BindingFlags.Static | BindingFlags.NonPublic));
        var skipBool = new HarmonyLib.HarmonyMethod(typeof(AmosBridge).GetMethod(nameof(LeaveOursBool), BindingFlags.Static | BindingFlags.NonPublic));
        Type[] hit = { typeof(Player), typeof(DamageInfo), typeof(EBodyPart) };
        Type[] blast = { typeof(Player), typeof(Vector3), typeof(float) };
        int n = 0;
        void Patch(string type, string name, Type[] args, HarmonyLib.HarmonyMethod prefix)
        {
            Type t = asm.GetType("EuphoriaStylePhysics." + type);
            MethodInfo m = t == null ? null : args == null ? HarmonyLib.AccessTools.DeclaredMethod(t, name) : HarmonyLib.AccessTools.Method(t, name, args);
            if (m == null || m.GetParameters().Length == 0 || m.GetParameters()[0].ParameterType != typeof(Player))
            {
                EuphoriaPlugin.Log($"Euphoria Style Physics: {type}.{name} not found - it may still act on our bots");
                return;
            }
            try { harmony.Patch(m, prefix: prefix); n++; }
            catch (Exception e) { EuphoriaPlugin.Log($"Euphoria Style Physics: {type}.{name} not patched: {e.Message}"); }
        }
        Patch("Flinch", "OnHit", hit, skip);
        Patch("Stagger", "OnHit", hit, skip);
        Patch("Knockdown", "OnHit", hit, skip);
        Patch("Knockdown", "TryStumble", hit, skip);
        Patch("Knockdown", "Start", null, skip); // last stop: a stumble or knockdown queued before we took him
        Patch("Knockdown", "OnBlast", blast, skipBool);
        Patch("Flinch", "OnBlast", blast, skipBool);
        EuphoriaPlugin.Log($"Euphoria Style Physics: its hit and blast handlers leave our bots alone ({n}/7 method(s))");
    }

    private static void Switch(string section, string key, bool turnOff)
    {
        if (!_amos.Config.TryGetEntry(section, key, out ConfigEntry<bool> e))
        {
            EuphoriaPlugin.Log($"Euphoria Style Physics: setting \"{section} > {key}\" not found");
            return;
        }
        if (!e.Value) return;
        if (turnOff)
        {
            e.Value = false;
            EuphoriaPlugin.Log($"Euphoria Style Physics: \"{section} > {key}\" turned off (ours does it; \"Turn off its stumble and knockdowns\" in our settings)");
        }
        else EuphoriaPlugin.Log($"Euphoria Style Physics: WARNING \"{section} > {key}\" is on - it does what ours does; bots it takes are left to it");
    }

    /// Our doll has the bot (reaction, get-up or the blend back to animation).
    private static bool Ours(Player p) => On && p != null && NmBody.IsBusy(p);

    /// Its owner check, asked on every hit and each frame its flinch or stagger is on a bot. A dead bot is never
    /// ours: EFT makes the corpse inside the killing hit, before our postfix lets him go, and its Api leaves an
    /// owned bot's death to EFT's plain ragdoll - its muscles are meant to play it.
    private static bool OwnedAlive(Player p) => Ours(p) && p.HealthController?.IsAlive == true;

    // false = skip his handler
    private static bool LeaveOursVoid(Player __0)
    {
        if (!Ours(__0)) return true;
        if (_leftAlone++ == 0 || (EuphoriaPlugin.Instance?.VerboseLog?.Value ?? false))
            EuphoriaPlugin.Log($"Euphoria Style Physics: left our bot {__0.ProfileId} alone");
        return false;
    }

    private static bool LeaveOursBool(Player __0, ref bool __result)
    {
        if (LeaveOursVoid(__0)) return true;
        __result = false;
        return false;
    }
}
