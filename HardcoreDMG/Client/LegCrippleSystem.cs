using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;
using UnityEngine;

namespace HardcoreDMGClient;

/// <summary>
/// Leg crippling for both players and bots.
///
/// When either leg drops below the crouch threshold (or is blacked):
/// - The actor is forced into crouch position
/// - Sprinting is disabled
///
/// When both legs drop below the prone threshold (a blacked leg always counts):
/// - The actor is forced into prone and crawls at the game's prone speed
/// - Sprinting is disabled
///
/// With "Stay down until healed", the actor also can't stand back up past
/// what its legs allow (see CrippleLevel). Painkillers temporarily mask it all.
/// </summary>
internal static class LegCrippleSystem
{
    public enum Cripple { None, Crouch, Prone }

    // Per-actor state — keyed by player instance
    private static readonly Dictionary<Player, CrippleState> _states = new();

    private class CrippleState
    {
        public bool WasCrouching;
        public bool WasForcedProne;
    }

    /// <summary>
    /// Tick the leg cripple system. Called every frame from HardcoreDMGPlugin.Update.
    /// Processes the local player and all bots if enabled.
    /// </summary>
    public static void Tick(float dt)
    {
        if (!HardcoreDMGPlugin.EnableLegCripple.Value)
        {
            // Restore everyone when disabled
            if (_states.Count > 0)
            {
                foreach (var kvp in _states)
                    RestoreMovement(kvp.Key, kvp.Value);
                _states.Clear();
            }
            return;
        }

        GameWorld gw = Singleton<GameWorld>.Instance;
        if (gw == null)
            return;

        var allPlayers = gw.AllAlivePlayersList;
        if (allPlayers == null)
            return;

        foreach (Player p in allPlayers)
        {
            if (p != null && AppliesTo(p))
                ProcessPlayer(p);
        }

        // Clean up dead/removed actors
        if (_states.Count > 0)
        {
            var toRemove = new List<Player>();
            foreach (var kvp in _states)
            {
                if (kvp.Key == null || kvp.Key.HealthController == null || !kvp.Key.HealthController.IsAlive)
                    toRemove.Add(kvp.Key);
            }
            foreach (var p in toRemove)
                _states.Remove(p);
        }
    }

    private static bool AppliesTo(Player player) =>
        player.IsYourPlayer ? HardcoreDMGPlugin.LegCrippleEnablePlayer.Value : HardcoreDMGPlugin.LegCrippleEnableBots.Value;

    /// <summary>
    /// What the actor's legs allow right now, from the two thresholds. A
    /// blacked leg counts as below both. Ignores painkillers and the toggles.
    /// </summary>
    private static Cripple LegLimit(Player player)
    {
        float crouchBelow = HardcoreDMGPlugin.LegCrippleCrouchBelow.Value / 100f;
        float proneBelow = HardcoreDMGPlugin.LegCrippleProneBelow.Value / 100f;

        bool leftBlacked = IsBodyPartDestroyed(player, EBodyPart.LeftLeg);
        bool rightBlacked = IsBodyPartDestroyed(player, EBodyPart.RightLeg);
        float left = leftBlacked ? 0f : GetBodyPartHealthFraction(player, EBodyPart.LeftLeg);
        float right = rightBlacked ? 0f : GetBodyPartHealthFraction(player, EBodyPart.RightLeg);

        if ((leftBlacked || left < proneBelow) && (rightBlacked || right < proneBelow))
            return Cripple.Prone;
        if (leftBlacked || rightBlacked || left < crouchBelow || right < crouchBelow)
            return Cripple.Crouch;
        return Cripple.None;
    }

    /// <summary>
    /// How far "Stay down until healed" keeps this actor down: None when it
    /// doesn't apply (leg cripple or the option off, the actor not covered,
    /// or on painkillers), otherwise the limit its legs set.
    /// </summary>
    public static Cripple CrippleLevel(Player player)
    {
        if (player == null || !HardcoreDMGPlugin.EnableLegCripple.Value || !HardcoreDMGPlugin.LegCrippleStayDown.Value)
            return Cripple.None;
        if (!AppliesTo(player) || IsOnPainkillers(player))
            return Cripple.None;
        return LegLimit(player);
    }

    private static void ProcessPlayer(Player player)
    {
        if (player.HealthController == null || !player.HealthController.IsAlive) return;
        if (player.MovementContext == null) return;

        if (!_states.TryGetValue(player, out var state))
        {
            state = new CrippleState();
            _states[player] = state;
        }

        // Painkillers mask everything — restore normal movement
        Cripple limit = IsOnPainkillers(player) ? Cripple.None : LegLimit(player);

        switch (limit)
        {
            case Cripple.Prone:
                if (!state.WasForcedProne)
                {
                    ForceProne(player);
                    state.WasForcedProne = true;
                    state.WasCrouching = false;
                }
                else
                {
                    MaintainProne(player);
                }
                break;

            case Cripple.Crouch:
                // Was prone, legs now only limit to crouch — allow getting up to crouch
                state.WasForcedProne = false;
                if (!state.WasCrouching)
                {
                    ForceCrouch(player);
                    state.WasCrouching = true;
                }
                else
                {
                    MaintainCrouch(player);
                }
                break;

            default:
                if (state.WasCrouching || state.WasForcedProne)
                    RestoreMovement(player, state);
                break;
        }
    }

    /// <summary>
    /// Force the actor into crouch position.
    /// </summary>
    private static void ForceCrouch(Player player)
    {
        MovementContext mc = player.MovementContext;
        if (mc == null) return;

        try
        {
            // Set pose to crouch level (~0.5)
            mc.SetPoseLevel(0.5f, true);
        }
        catch { }

        try
        {
            mc.EnableSprint(false);
        }
        catch { }
    }

    private static void MaintainCrouch(Player player)
    {
        MovementContext mc = player.MovementContext;
        if (mc == null) return;

        try
        {
            // Keep in crouch if trying to stand
            if (mc.PoseLevel > 0.6f)
            {
                mc.SetPoseLevel(0.5f, true);
            }
        }
        catch { }

        try
        {
            if (mc.IsSprintEnabled)
            {
                mc.EnableSprint(false);
            }
        }
        catch { }
    }

    /// <summary>
    /// Force the actor into prone position.
    /// </summary>
    private static void ForceProne(Player player)
    {
        MovementContext mc = player.MovementContext;
        if (mc == null) return;

        try
        {
            mc.IsInPronePose = true;
        }
        catch { }

        try
        {
            mc.EnableSprint(false);
        }
        catch { }
    }

    /// <summary>
    /// Keep the actor in prone position while its legs are below the prone threshold.
    /// </summary>
    private static void MaintainProne(Player player)
    {
        MovementContext mc = player.MovementContext;
        if (mc == null) return;

        try
        {
            if (!mc.IsInPronePose)
            {
                mc.IsInPronePose = true;
            }
        }
        catch { }

        try
        {
            if (mc.IsSprintEnabled)
            {
                mc.EnableSprint(false);
            }
        }
        catch { }
    }

    /// <summary>
    /// Restore normal movement when legs are healed or painkillers are active.
    /// </summary>
    private static void RestoreMovement(Player player, CrippleState state)
    {
        // Don't force-stand — let the actor stand up naturally.
        // Just stop forcing crouch/prone and let vanilla take over.
        state.WasCrouching = false;
        state.WasForcedProne = false;
    }

    /// <summary>
    /// Check if a body part is destroyed (blacked out).
    /// </summary>
    private static bool IsBodyPartDestroyed(Player player, EBodyPart part)
    {
        try
        {
            var hc = player.HealthController as ActiveHealthController;
            if (hc != null)
            {
                return hc.IsBodyPartDestroyed(part);
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Get body part health as a fraction of max (0 = blacked, 1 = full).
    /// </summary>
    private static float GetBodyPartHealthFraction(Player player, EBodyPart part)
    {
        try
        {
            var hc = player.HealthController as ActiveHealthController;
            if (hc != null)
            {
                float current = hc.GetBodyPartHealth(part).Current;
                float max = hc.GetBodyPartHealth(part).Maximum;
                if (max > 0f)
                    return Mathf.Clamp01(current / max);
            }
        }
        catch { }
        return 1f;
    }

    /// <summary>
    /// Check if the player is on painkillers.
    /// </summary>
    private static bool IsOnPainkillers(Player player)
    {
        try
        {
            return player.MovementContext.PhysicalConditionIs(EPhysicalCondition.OnPainkillers);
        }
        catch { }
        return false;
    }

    public static void Reset()
    {
        _states.Clear();
    }
}
