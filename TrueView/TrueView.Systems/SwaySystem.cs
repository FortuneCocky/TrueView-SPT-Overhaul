using EFT.Animations;
using TrueView.Configuration;
using TrueView.Core;
using TrueView.Utilities;
using UnityEngine;

namespace TrueView.Systems;

internal static class SwaySystem
{
	private static float _appliedTremor = -1f;
	private static float _breathStabilize = 1f;
	private static int _armTier;

	public static void Apply()
	{
		if (HandlingConfig.SwayEnabled.Value && HandlingState.HasWeapon)
		{
			ProceduralWeaponAnimation animation = HandlingState.Animation;
			bool isAiming = HandlingState.IsAiming;
			// Holding breath while aiming stabilizes the arms almost fully.
			float stabTarget = isAiming && HandlingState.IsHoldingBreath ? 0.1f : 1f;
			_breathStabilize = Mathf.Lerp(_breathStabilize, stabTarget, 1f - Mathf.Exp(-6f * Time.deltaTime));
			float num = Fatigue();
			float weightProgress = HandlingState.Stats.WeightProgress;
			float num2 = (isAiming ? HandlingConfig.AimSwayMultiplier.Value : HandlingConfig.HipSwayMultiplier.Value);
			float num3 = num2 * num * _breathStabilize;
			if (animation.Breath != null)
			{
				animation.Breath.Intensity = animation.IntensityByPoseLevel * animation.IntensityByAiming * num3;
				animation.Breath.HipPenalty = HandlingConfig.HipSwayMultiplier.Value * (1f + 0.5f * weightProgress);
			}
			if (animation.MotionReact != null)
			{
				// Tired arms also swing the gun further around when you move and turn.
				float armsLoose = 1f + 0.4f * Mathf.Pow(1f - HandlingState.HandsStamina01, 2f);
				animation.MotionReact.Intensity = 0.45f * HandlingConfig.MotionInertiaMultiplier.Value * (1f + 0.35f * weightProgress) * armsLoose * _breathStabilize;
			}
			ApplyTremor(animation, num * _breathStabilize);
		}
	}

	private static void ApplyTremor(ProceduralWeaponAnimation animation, float fatigue)
	{
		if (animation.HandShakeEffector != null)
		{
			// Tired arms shake: nothing above ~35% arm stamina, then steeply worse toward empty.
			float shaky = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.35f, 0f, HandlingState.HandsStamina01));
			float arms = (HandlingConfig.ArmTremor?.Value ?? 0f) * shaky * _breathStabilize;
			float num = 3f * HandlingConfig.TremorMultiplier.Value * fatigue + arms + AdrenalineSystem.TremorAdd;
			if (!(Mathf.Abs(num - _appliedTremor) < 0.01f))
			{
				_appliedTremor = num;
				animation.HandShakeEffector.Setup(num, 1f, 1f);
			}
			LogArms(shaky, num);
		}
	}

	/// Logs when tired arms start to shake, shake hard, and settle again.
	private static void LogArms(float shaky, float tremor)
	{
		int tier = shaky > 0.6f ? 2 : shaky > 0.05f ? 1 : 0;
		if (tier == _armTier)
			return;
		_armTier = tier;
		string what = tier == 2 ? "shaking hard" : tier == 1 ? "starting to shake" : "steady";
		Log.Info($"Sway: arms {what} (arm stamina {HandlingState.HandsStamina01:0.00}, sway x{Fatigue():0.00}, tremor {tremor:0.00})");
	}

	public static float Fatigue()
	{
		// Arm stamina bites gently at first and hard near empty: 50% -> 0.5, 0% -> 2.2.
		float arms = 1f - HandlingState.HandsStamina01;
		float armFatigue = arms * 0.6f + Mathf.Pow(arms, 3f) * 1.6f;
		float num = Mathf.Max(armFatigue, 1f - HandlingState.Oxygen01);
		float num2 = 0.5f * HandlingState.Stats.WeightProgress * (1f - HandlingState.Stamina01);
		return (1f + HandlingConfig.StaminaSwayInfluence.Value * (num + num2)) * InjuryHandlingSystem.SwayMultiplier;
	}

	public static void Reset()
	{
		_appliedTremor = -1f;
		_armTier = 0;
	}
}
