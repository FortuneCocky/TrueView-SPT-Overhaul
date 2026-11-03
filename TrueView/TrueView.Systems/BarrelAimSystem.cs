using EFT;
using TrueView.Configuration;
using TrueView.Core;
using UnityEngine;

namespace TrueView.Systems;

/// <summary>
/// Barrel alignment (03 - Deadzone): holds the hip-fire barrel exactly on TrueView's aim line.
///
/// The deadzone decides where the gun should point (camera rotation times
/// ProceduralHipSystem.WeaponOffset), but the hip animation pose under it never
/// points the barrel quite there, so shots land off the deadzone line by an
/// amount that changes with weapon and pose. The game fires from the fireport
/// along the barrel, and the arms are IK-solved onto the gun every frame, so
/// fixing the gun fixes the shot and the hands follow on their own.
///
/// Each frame, after the arms are solved (Player.VisualPass), this measures the
/// real barrel line against the line from the fireport to the aim point (the
/// deadzone direction, at the zero distance) and integrates the difference
/// into a small yaw/pitch correction that Patch_CalculateCameraPosition puts
/// in front of the weapon offset. The integrator is slow (about a quarter of a
/// second) and holds still while a shot settles, so recoil, step bob and sway
/// still show; only the steady pose error is taken out.
///
/// Hip fire only. It lets go (eases out) in ADS, sprint, free look, hand
/// animations, shoulder swap, and whenever the barrel is far off the line on
/// purpose (stances, wall pull-back), and never corrects more than a few
/// degrees.
/// </summary>
internal static class BarrelAimSystem
{
    // Larger misses are intentional poses, not pose error.
    private const float MaxCorrectionDeg = 6f;
    // Integrator rate, 1/s: ~0.3 s to take out a steady error.
    private const float Gain = 3.5f;
    private const float WeightRate = 6f;
    private const float ReleaseRateDeg = 12f;
    private const float LogInterval = 3f;

    // x = yaw, y = pitch (Unity sign: positive pitches the barrel down), camera space.
    private static Vector2 _correction;
    private static float _weight;
    private static int _lastFrame = -1;

    private static float _logTimer;
    private static int _samples;
    private static float _residualSum;
    private static float _residualMax;
    private static int _heldFrames;
    private static string _lastHold = "";

    /// <summary>Rotation to put in front of the weapon offset on WeaponRoot.</summary>
    public static Quaternion Correction =>
        _weight > 0.0001f
            ? Quaternion.Euler(_correction.y * _weight, _correction.x * _weight, 0f)
            : Quaternion.identity;

    public static void Measure(Player player)
    {
        if (_lastFrame == Time.frameCount)
            return;
        _lastFrame = Time.frameCount;
        float dt = Mathf.Min(Time.deltaTime, 0.05f);

        if (HandlingConfig.BarrelAimEnabled?.Value != true)
        {
            Reset();
            return;
        }

        if (player.HandsController is not Player.FirearmController fc)
        {
            Release(dt, "no firearm");
            return;
        }

        Transform cam = player.ProceduralWeaponAnimation?.HandsContainer?.CameraTransform;
        if (cam == null || fc.CurrentFireport == null)
        {
            Release(dt, "no fireport");
            return;
        }

        string hold = HoldReason(player);

        // The aim line: from the eye along the deadzone direction, out to the
        // zero distance. The barrel should pass through that point.
        float zero = HandlingConfig.BarrelAimZeroDistance?.Value ?? 25f;
        Quaternion camRot = cam.rotation;
        Vector3 aimPoint = cam.position + camRot * (ProceduralHipSystem.WeaponOffset * Vector3.forward) * zero;
        Vector3 desired = aimPoint - fc.FireportPosition;
        Vector3 actual = fc.WeaponDirection;
        if (desired.sqrMagnitude < 1e-6f || actual.sqrMagnitude < 1e-6f)
        {
            Release(dt, "no barrel");
            return;
        }

        Quaternion toCam = Quaternion.Inverse(camRot);
        Vector2 a = YawPitch(toCam * actual);
        Vector2 d = YawPitch(toCam * desired);
        Vector2 residual = new(Mathf.DeltaAngle(a.x, d.x), Mathf.DeltaAngle(a.y, d.y));

        if (hold == null && residual.magnitude > MaxCorrectionDeg)
            hold = "barrel far off the line";

        if (hold != null)
        {
            Release(dt, hold);
            return;
        }

        _weight = Mathf.MoveTowards(_weight, 1f, WeightRate * dt);

        // Only integrate at full weight (otherwise the part of the correction
        // that is not applied would read as error and wind up), and never while
        // a shot settles, so the kick is not absorbed.
        if (_weight >= 0.999f && !ShotSystem.InShotRecovery)
        {
            _correction += residual * (Gain * dt);
            _correction = Vector2.ClampMagnitude(_correction, MaxCorrectionDeg);
        }

        _samples++;
        float r = residual.magnitude;
        _residualSum += r;
        _residualMax = Mathf.Max(_residualMax, r);
        TickLog(dt);
    }

    private static string HoldReason(Player player)
    {
        if (HandlingManager.InFreeLook(player)) return "free look";
        if (HandlingState.IsAiming) return "aiming";
        if (HandlingState.IsSprinting) return "sprinting";
        // A stance mod posing the gun (ready stances, lean shoulder swap) is
        // an intended pose, not pose error. Tactical Stances also re-applies
        // its pose after this measurement, so the barrel read here can flip
        // between frames and the correction would make the gun stutter.
        if (StanceOverhaulCompat.HandsOwned) return "stance";
        if (TacticalStancesCompat.Posing) return "tactical stance";
        try
        {
            if (player.HandsController.IsHandsProcessing()) return "hand animation";
            if (player.MovementContext?.LeftStanceController?.LeftStance == true) return "shoulder swap";
        }
        catch { }
        return null;
    }

    private static void Release(float dt, string reason)
    {
        _weight = Mathf.MoveTowards(_weight, 0f, WeightRate * dt);
        if (_weight <= 0f)
            _correction = Vector2.MoveTowards(_correction, Vector2.zero, ReleaseRateDeg * dt);
        _heldFrames++;
        _lastHold = reason;
        TickLog(dt);
    }

    // Yaw (+ right) and pitch (+ down, Unity's x sign) of a camera-space direction, degrees.
    private static Vector2 YawPitch(Vector3 v)
    {
        float yaw = Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
        float pitch = -Mathf.Atan2(v.y, new Vector2(v.x, v.z).magnitude) * Mathf.Rad2Deg;
        return new Vector2(yaw, pitch);
    }

    private static void TickLog(float dt)
    {
        _logTimer += dt;
        if (_logTimer < LogInterval)
            return;
        if (_samples > 0)
        {
            Utilities.Log.Info($"Barrel aim: correcting pose error yaw {_correction.x:F2} pitch {_correction.y:F2} deg, " +
                $"barrel off the aim line avg {_residualSum / _samples:F2} max {_residualMax:F2} deg over {_samples} frames, " +
                $"weight {_weight:F2}, held {_heldFrames} frames (last: {_lastHold})");
        }
        else if (_heldFrames > 0)
        {
            Utilities.Log.Info($"Barrel aim: held for {_heldFrames} frames ({_lastHold}), correction yaw {_correction.x:F2} pitch {_correction.y:F2} deg");
        }
        _logTimer = 0f;
        _samples = 0;
        _residualSum = 0f;
        _residualMax = 0f;
        _heldFrames = 0;
    }

    public static void Reset()
    {
        _correction = Vector2.zero;
        _weight = 0f;
    }
}
