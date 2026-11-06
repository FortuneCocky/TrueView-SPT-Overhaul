using Comfort.Common;
using EFT;
using EFT.Animations;
using TrueView.Configuration;
using TrueView.Systems;
using UnityEngine;

namespace TrueView.Core;

internal static class HandlingManager
{
    private static int _lastFrame = -1;

    private static bool _wasActive;

    private static bool _lastLeftStance;

    // Track free-look state to detect the transition out of free look
    private static bool _wasFreeLooking;

    // Track the current firearm instance so we can detect weapon swaps
    // and reset procedural state — prevents the weapon from being
    // misplaced when switching weapons mid-sprint.
    private static object _lastFirearm;

    /// <summary>
    /// Vanilla free look owns the camera and the gun: while the key is held,
    /// and while the game swings the head back to centre after release
    /// (player.HeadRotation is only ever set by free look and is zero otherwise).
    /// TrueView stands aside for all of it and takes over again once the view
    /// is back at centre.
    /// </summary>
    public static bool InFreeLook(Player player)
    {
        return player.MouseLookControl || player.HeadRotation.sqrMagnitude > 1e-6f;
    }

    public static void Tick(Player player, float dt)
    {
        if (player == null || !player.IsYourPlayer)
        {
            return;
        }
        DriftProbe.Tick(player, dt);
        TickSystems(player, dt);
        FieldOfViewSystem.Tick(player);
        // Last, after every input it reads has updated — and every frame, so
        // free look, inventory or a disabled mod let the camera settle back.
        BodyCamSystem.Tick(player, dt);
    }

    private static void TickSystems(Player player, float dt)
    {
        if (InFreeLook(player))
        {
            // Hand the camera and gun to vanilla free look: drop every
            // procedural offset once on the way in, then stay out until the
            // game has swung the head back to centre.
            if (!_wasFreeLooking)
            {
                ResetAll(player);
                ProceduralHipSystem.Reset();
                _wasFreeLooking = true;
                Utilities.Log.Info("Free look: TrueView paused");
            }
            return;
        }

        // The view is back at centre: start every system fresh from the rest
        // pose, which is exactly where vanilla free look left the camera.
        if (_wasFreeLooking)
        {
            ResetAll(player);
            ProceduralHipSystem.Reset();
            Utilities.Log.Info("Free look: view back at centre, TrueView resumed");
        }
        // Reset all procedural state when inventory/looting is open so the
        // weapon position doesn't get corrupted by stale offsets.
        if (player.IsInventoryOpened)
        {
            if (_wasActive)
            {
                ResetAll(player);
            }
            return;
        }
        // Reset hip deadzone when shoulder swap changes so the weapon
        // doesn't float off the hands due to stale offsets.
        bool currentLeftStance = false;
        try
        {
            currentLeftStance = player.MovementContext?.LeftStanceController?.LeftStance ?? false;
        }
        catch { }
        if (currentLeftStance != _lastLeftStance)
        {
            ProceduralHipSystem.Reset();
            _lastLeftStance = currentLeftStance;
        }
        if (!HandlingState.Enabled)
        {
            if (_wasActive)
            {
                ResetAll(player);
            }
        }
        else if (_lastFrame != Time.frameCount)
        {
            _lastFrame = Time.frameCount;
            _wasActive = true;
            HandlingState.Update(player);

            bool handsTransition = false;
            try { handsTransition = player.HandsController?.IsHandsProcessing() ?? false; } catch { }
            ProceduralHipSystem.SetHandsTransition(handsTransition);

            // Detect weapon swap — reset procedural state so the new weapon
            // starts from a clean position instead of inheriting stale
            // offsets from the previous weapon. This fixes pistols (and
            // any weapon) being misplaced when equipped mid-sprint.
            object currentFirearm = HandlingState.Firearm;
            if (currentFirearm != _lastFirearm)
            {
                _lastFirearm = currentFirearm;
                ProceduralHipSystem.Reset();
                ProceduralHipSystem.SuppressCameraFollow(1.5f);
                SwaySystem.Reset();
                CameraLagSystem.Reset(player.ProceduralWeaponAnimation);
            }

            MotionTracker.Update(player, dt);
            InjuryHandlingSystem.Tick(player, dt);
            LocomotionGaitSystem.Tick(player, dt);
            TurnInPlaceSystem.Tick(player, dt);
            DynamicFovSystem.Tick(player, dt);
            FlybySystem.Tick();
            AdrenalineSystem.Tick(player, dt);
            BreathingSystem.Tick(player, dt);
            SprintCancelSystem.Tick(player);
            ScopeOpticsSystem.Tick(player, dt);
            if (HandlingState.HasWeapon)
            {
                AimingSystem.Apply();
                SwaySystem.Apply();
            }
            ProceduralHipSystem.Tick(player, dt);
            MomentumSystem.Tick(player, dt);
            LeanSystem.Tick(player, dt);
            TurnRollSystem.Tick(player, dt);
            if (ProceduralHipSystem.ControlsCamera)
                CameraLagSystem.Reset(player.ProceduralWeaponAnimation);
            else
                CameraLagSystem.Tick(dt);
            FluidMovementSystem.Tick(player, dt);
        }
    }

    public static void OnWeaponVariablesUpdated(Player player)
    {
        if (!(player == null) && player.IsYourPlayer && HandlingState.Enabled)
        {
            HandlingState.Update(player);
            HandlingState.RefreshStats();
            ShotSystem.ApplyWeaponVariables(player.ProceduralWeaponAnimation);
            AimingSystem.Apply();
        }
    }

    public static void ResetAll(Player player)
    {
        _wasActive = false;
        _wasFreeLooking = false;
        _lastFirearm = null;
        SwaySystem.Reset();
        ShotSystem.Reset();
        CameraLagSystem.Reset(player?.ProceduralWeaponAnimation);
        LeanSystem.Reset();
        TurnRollSystem.Reset();
        MomentumSystem.Reset();
        LocomotionGaitSystem.Reset();
        TurnInPlaceSystem.Reset();
        DynamicFovSystem.Reset();
        FlybySystem.Reset();
        AdrenalineSystem.Reset();
        BreathingSystem.Reset();
        InjuryHandlingSystem.Reset();
        MotionTracker.Reset(player);
        FluidMovementSystem.Reset();
        BarrelAimSystem.Reset();
        ScopeOpticsSystem.Reset();
        if (player != null && player.ProceduralWeaponAnimation != null)
        {
            player.ProceduralWeaponAnimation.UpdateWeaponVariables();
            ApplyHeadRotation(player);
        }
        HandlingState.Clear();
    }

    private static void ApplyHeadRotation(Player player)
    {
        ProceduralWeaponAnimation proceduralWeaponAnimation = player.ProceduralWeaponAnimation;
        if (proceduralWeaponAnimation != null)
        {
            proceduralWeaponAnimation.SetHeadRotation(player.HeadRotation);
        }
    }

    public static Player LocalPlayer()
    {
        Player player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (!(player != null) || !player.IsYourPlayer)
        {
            return null;
        }
        return player;
    }
}