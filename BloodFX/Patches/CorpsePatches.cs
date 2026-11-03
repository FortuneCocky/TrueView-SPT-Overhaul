using System.Reflection;
using EFT;
using EFT.Ballistics;
using EFT.Interactive;
using EFT.InputSystem;
using EFT.UI;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace BloodFX.Patches;

/// <summary>
/// After EFT starts a corpse ragdoll (at death, and again whenever BloodFX
/// wakes a body): add the gear's dead weight and hook the sleep checks.
/// </summary>
public class CorpseRagdollStartPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(CorpseRagdoll), nameof(CorpseRagdoll.Start));
    }

    [PatchPostfix]
    private static void Postfix(CorpseRagdoll __instance)
    {
        try { CorpseSystem.OnRagdollStarted(__instance); }
        catch (System.Exception ex) { BloodFXPlugin.Log.LogError($"[Corpses] ragdoll start: {ex.Message}"); }
    }
}

/// <summary>
/// Tones down EFT's killing-shot push on the corpse to a realistic one.
/// </summary>
public class CorpseImpulsePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(CorpseRagdoll), nameof(CorpseRagdoll.ApplyImpulse),
            new[] { typeof(Collider), typeof(Vector3), typeof(Vector3), typeof(float) });
    }

    [PatchPrefix]
    private static void Prefix(ref float thrust)
    {
        thrust *= CorpseSystem.DeathPushScale();
    }
}

/// <summary>
/// Bullets that hit a body that was already dead push it, even after EFT has
/// frozen it. The killing shot is left to EFT (CorpseImpulsePatch).
/// </summary>
public class CorpseHitPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(BodyPartCollider), nameof(BodyPartCollider.ApplyHit));
    }

    [PatchPrefix]
    private static void Prefix(BodyPartCollider __instance, out Corpse __state)
    {
        __state = BloodFXPlugin.Instance.CorpseShotsOn.Value ? __instance.GetComponentInParent<Corpse>() : null;
    }

    [PatchPostfix]
    private static void Postfix(BodyPartCollider __instance, DamageInfo damageInfo, Corpse __state)
    {
        if (__state == null || !damageInfo.IsForwardHit) return;
        try { CorpseSystem.OnCorpseShot(__state, __instance.GetComponent<Collider>(),damageInfo.Direction, damageInfo.HitPoint, damageInfo.Damage); }
        catch (System.Exception ex) { BloodFXPlugin.Log.LogError($"[Corpses] shot on corpse: {ex.Message}"); }
    }
}

/// <summary>
/// The interaction prompt runs the selected action (Search, for a body) the
/// moment interact is pressed. On a body that press is held back: let go
/// before the hold time and it searches as usual; keep holding and the body
/// is dragged.
/// </summary>
public class InteractPanelPatch : ModulePatch
{
    private static readonly AccessTools.FieldRef<ActionPanel, GamePlayerOwner> OwnerRef =
        AccessTools.FieldRefAccess<ActionPanel, GamePlayerOwner>("_owner");

    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(ActionPanel), nameof(ActionPanel.TranslateCommand));
    }

    [PatchPrefix]
    private static bool Prefix(ActionPanel __instance, ECommand command, ref InputNode.ETranslateResult __result)
    {
        if (command == ECommand.EndInteracting)
        {
            CorpseSystem.OnInteractCommand(false, null);
            return true;
        }
        if (command != ECommand.BeginInteracting) return true;

        var action = OwnerRef(__instance)?.AvailableInteractionState.Value?.SelectedAction?.Action;
        if (!CorpseSystem.OnInteractCommand(true, action)) return true;

        __result = InputNode.ETranslateResult.Block;
        return false;
    }
}

/// <summary>
/// Sees the interact key's release even when no prompt is showing (the player
/// looked away from the body mid-drag).
/// </summary>
public class InteractMovePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(MoveInputTranslator), nameof(MoveInputTranslator.TranslateCommand));
    }

    [PatchPrefix]
    private static void Prefix(ECommand command)
    {
        if (command == ECommand.EndInteracting)
            CorpseSystem.OnInteractCommand(false, null);
        else if (command == ECommand.BeginInteracting && CorpseSystem.Dragging)
            CorpseSystem.OnInteractCommand(true, null);
    }
}
