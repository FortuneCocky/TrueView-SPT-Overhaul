using EFT;
using TrueView.Configuration;
using TrueView.Core;
using UnityEngine;

namespace TrueView.Systems;

internal static class LeanSystem
{
	private const float Frequency = 4.5f;

	private const float DampingRatio = 0.8f;

	private static float _lean;

	private static float _velocity;

	public static float Lean => _lean;

	public static void Tick(Player player, float deltaTime)
	{
		if (deltaTime <= 0f)
		{
			return;
		}
		deltaTime = Mathf.Min(deltaTime, 1f / 15f);

		float num = 0f;
		if (HandlingConfig.LeanEnabled.Value && HandlingState.HasWeapon && player != null)
		{
			// Turns only: the strafe lean is MomentumSystem's, built from how you actually move.
			float turn = Mathf.Clamp(MotionTracker.YawRateSlow / 180f, -1.5f, 1.5f);
			num = turn * Mathf.Clamp01(HandlingConfig.LeanMultiplier.Value);
			if (HandlingState.IsAiming)
			{
				num *= 0.55f;
			}
		}
		float num3 = 20.25f;
		float num4 = 7.2000003f;
		_velocity += ((num - _lean) * num3 - _velocity * num4) * deltaTime;
		_lean += _velocity * deltaTime;

	}

	public static Vector3 Modify(Vector3 headRotation)
	{
		headRotation.z -= _lean * HandlingConfig.LeanCameraAngle.Value;
		return headRotation;
	}

	public static float WeaponRoll()
	{
		if (!HandlingConfig.LeanEnabled.Value)
		{
			return 0f;
		}
		return (0f - _lean) * HandlingConfig.LeanWeaponAngle.Value;
	}

	public static void Reset()
	{
		_lean = 0f;
		_velocity = 0f;
	}
}
