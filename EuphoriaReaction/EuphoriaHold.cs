using System;
using System.Linq;
using System.Reflection;
using EFT;
using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// A bot does nothing of his own until he has fully recovered from Euphoria: while the physics has him,
/// while a roll-over or get-up clip plays, and through the blend back into his animation. His AI tick,
/// his mover (pathing) and his own move/turn calls are skipped, at the same EFT choke points the dev
/// freeze uses, so no decision (grenade, reload, heal, door, loot, walking off, turning away) starts
/// mid-reaction, whatever bot mods are installed. Player.Move itself is not skipped, only emptied, and
/// Euphoria sends it a zero move every frame (<see cref="HeldMove"/>): the game's character controller
/// is what keeps the root on the ground, and without its tick he was left hanging in the air (a held
/// stance pose floating above stairs) until the hold lifted. Stance and facing changes Euphoria makes itself go
/// through direct calls and still work. The one exception: a bot aiming back at his shooter keeps his
/// AI for that, since his shots are the AI's own trigger pulls let through.
/// </summary>
internal static class EuphoriaHold
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int _skipped;

    public static void Install()
    {
        var harmony = new HarmonyLib.Harmony("com.euphoria.reaction.hold");
        Assembly eft = typeof(Player).Assembly;
        Type mover = eft.GetType("BotMover") ?? eft.GetTypes().FirstOrDefault(t => t.Name == "BotMover");
        var pre = new HarmonyLib.HarmonyMethod(typeof(EuphoriaHold).GetMethod(nameof(SkipIfHeld), BindingFlags.Static | BindingFlags.NonPublic));
        int n = 0;
        void Patch(Type t, string name, params Type[] args) => PatchWith(t, name, pre, args);
        void PatchWith(Type t, string name, HarmonyLib.HarmonyMethod prefix, params Type[] args)
        {
            if (t == null) return;
            MethodInfo m = args.Length == 0 && t.GetMethods(All | BindingFlags.DeclaredOnly).Count(x => x.Name == name) == 1
                ? t.GetMethod(name, All | BindingFlags.DeclaredOnly)
                : t.GetMethod(name, All | BindingFlags.DeclaredOnly, null, args, null);
            if (m == null) { EuphoriaPlugin.Log($"Delirium: hold: {t.Name}.{name} not found"); return; }
            try { harmony.Patch(m, prefix: prefix); n++; }
            catch (Exception e) { EuphoriaPlugin.Log($"Delirium: hold {t.Name}.{name}: {e.Message}"); }
        }
        Patch(typeof(BotOwner), "UpdateManual");
        Patch(typeof(BotOwner), "FixedUpdate");
        Patch(mover, "ManualUpdate");
        Patch(mover, "ManualFixedUpdate");
        Patch(mover, "MovePlayer");
        PatchWith(typeof(Player), "Move", new HarmonyLib.HarmonyMethod(typeof(EuphoriaHold).GetMethod(nameof(ZeroMoveIfHeld), BindingFlags.Static | BindingFlags.NonPublic)), typeof(Vector2));
        Patch(typeof(Player), "Rotate", typeof(Vector2), typeof(bool));
        EuphoriaPlugin.Log($"Delirium: bots held (no AI, pathing, moving or turning) until recovered: {n} method(s)");
    }

    // false = skip the original
    private static bool SkipIfHeld(object __instance)
    {
        // runs for every bot's AI and mover every frame: nothing to look up unless someone is in a reaction
        if (!NmBody.AnyBusy || !(EuphoriaPlugin.Instance?.CancelActions.Value ?? true)) return true;
        Player p = __instance as Player ?? (__instance as BotOwner)?.GetPlayer ?? MoverPlayer(__instance);
        if (p == null || p.IsYourPlayer || !NmBody.HoldsAI(p)) return true;
        if (_skipped++ == 0) EuphoriaPlugin.Log("Delirium: holding a bot's AI until he has recovered");
        return false;
    }

    // Player.Move: runs (it grounds him), but with no input while he's held.
    private static void ZeroMoveIfHeld(Player __instance, ref Vector2 __0)
    {
        if (!NmBody.AnyBusy || !(EuphoriaPlugin.Instance?.CancelActions.Value ?? true)) return;
        if (__instance == null || __instance.IsYourPlayer || !NmBody.HoldsAI(__instance)) return;
        __0 = Vector2.zero;
    }

    private static MethodInfo _move;
    private static readonly object[] ZeroMove = { Vector2.zero };

    /// Every frame while held: the movement tick his AI and mover no longer send (zero input).
    internal static void HeldMove(Player p)
    {
        try
        {
            if (_move == null) _move = typeof(Player).GetMethod("Move", All, null, new[] { typeof(Vector2) }, null);
            _move?.Invoke(p, ZeroMove);
        }
        catch { }
    }

    private static FieldInfo _moverOwner;

    private static Player MoverPlayer(object mover)
    {
        if (mover == null) return null;
        try
        {
            if (_moverOwner == null || !_moverOwner.DeclaringType.IsInstanceOfType(mover))
                for (Type t = mover.GetType(); t != null && (_moverOwner == null || !_moverOwner.DeclaringType.IsInstanceOfType(mover)); t = t.BaseType)
                    _moverOwner = t.GetFields(All | BindingFlags.DeclaredOnly).FirstOrDefault(f => f.FieldType == typeof(BotOwner) || typeof(Player).IsAssignableFrom(f.FieldType)) ?? _moverOwner;
            object o = _moverOwner?.GetValue(mover);
            return o as Player ?? (o as BotOwner)?.GetPlayer;
        }
        catch { return null; }
    }
}
