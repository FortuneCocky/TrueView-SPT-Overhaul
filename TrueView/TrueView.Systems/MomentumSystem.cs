using EFT;
using TrueView.Configuration;
using TrueView.Core;
using UnityEngine;

namespace TrueView.Systems;

/// <summary>
/// Body momentum: your speed builds up instead of snapping to full, and the
/// body leans into it.
///
///   speed build-up - Patch_LimitMotion hands every walk/run/sprint step to
///                    <see cref="LimitSpeed"/>, which lets your speed rise only
///                    as fast as the body can push off: about 0.3 s to a walk,
///                    0.45 s to a run and 0.65 s into a sprint, longer when
///                    heavily loaded, tired or hurt. The first moment barely
///                    moves you (the lean comes first). Changing direction has
///                    to brake the old one first; slowing down stays EFT's own.
///   start lean     - how far you are below the speed you are asking for
///                    leans the head into the move: forward to walk off, back
///                    to back off, sideways into a strafe. It fades as your
///                    feet catch up.
///   held lean      - moving keeps a small lean with the movement: pitch with
///                    forward/back speed, roll into a strafe.
///   braking        - stopping tips the head the way you were going.
///   turning        - the existing turn roll, scaled by speed.
/// All of it scales with inventory weight and drops while aiming.
/// </summary>
internal static class MomentumSystem
{
	private const float MaxDeltaTime = 1f / 15f;

	private const float ReferenceTurnRate = 180f;

	private const float MaxHeadPitch = 4f;

	private const float MaxHeadYaw = 2.5f;

	private const float MaxHeadRoll = 7f;

	private static Vector3 _lastLocalVelocity;

	private static Vector3 _smoothedLocalVelocity;

	private static Vector3 _smoothedLocalAcceleration;

	private static float _lastYawRate;

	private static float _smoothedYawRate;

	private static float _smoothedYawAcceleration;

	private static bool _hasSample;

	private static Vector3 _headRotation;

	private static Vector3 _headVelocity;

	private static float _aimBlend;

	private static float _bodyWeight;

	// Speed build-up, in body-local m/s (x right, y forward).
	private static Vector2 _allowed;

	private static Vector2 _deficit;

	private static float _movingSince = -1f;

	private static int _limitFrame = -10;

	private static float _rampStart = -1f, _rampTarget;

	public static Vector3 Modify(Vector3 headRotation)
	{
		if (_headRotation == Vector3.zero)
		{
			return headRotation;
		}
		headRotation.x += _headRotation.x;
		headRotation.y += _headRotation.y;
		headRotation.z += _headRotation.z;
		return headRotation;
	}

	public static float WeaponRoll()
	{
		return _headRotation.z;
	}

	/// <summary>
	/// Holds back the horizontal part of this step's motion so speed builds up. Called for the
	/// local player's walk, run and sprint steps after EFT's own limits; never adds speed.
	/// </summary>
	public static void LimitSpeed(Player player, ref Vector3 motion, float deltaTime)
	{
		float buildUp = HandlingConfig.MomentumSpeedBuildUp?.Value ?? 0f;
		if (deltaTime <= 1e-5f || buildUp <= 0f || HandlingConfig.MomentumEnabled?.Value != true)
		{
			_allowed = _deficit = Vector2.zero;
			return;
		}
		_limitFrame = Time.frameCount;

		float yaw = player.Rotation.x * Mathf.Deg2Rad;
		float sin = Mathf.Sin(yaw);
		float cos = Mathf.Cos(yaw);
		Vector2 desired = new Vector2(motion.x * cos - motion.z * sin, motion.x * sin + motion.z * cos) / deltaTime;
		float want = desired.magnitude;
		if (want < 0.05f)
		{
			// Standing: the next move starts from nothing.
			_allowed = _deficit = Vector2.zero;
			_movingSince = -1f;
			return;
		}
		if (_movingSince < 0f)
			_movingSince = Time.time;

		// Time to reach this speed from a standstill: walk ~0.3 s, run ~0.45 s, sprint ~0.65 s,
		// stretched by load, tiredness and injury.
		float time = Mathf.Lerp(0.3f, 0.45f, Mathf.InverseLerp(1.5f, 3.5f, want)) + (HandlingState.IsSprinting ? 0.2f : 0f);
		time *= buildUp * (1f + 0.6f * _bodyWeight) * (1f + 0.5f * (1f - HandlingState.Stamina01)) * InjuryHandlingSystem.MomentumMultiplier;
		float accel = Mathf.Max(want, 3.5f) / Mathf.Max(0.05f, time);
		// Pushing off: the first moment from a standstill is mostly the lean.
		accel *= Mathf.Lerp(0.15f, 1f, Mathf.SmoothStep(0f, 1f, (Time.time - _movingSince) / 0.12f));
		// Slowing toward the new speed or direction is quicker than speeding up.
		bool gaining = Vector2.Dot(desired - _allowed, _allowed) >= 0f && want > _allowed.magnitude;
		_allowed = Vector2.MoveTowards(_allowed, desired, (gaining ? accel : accel * 2.5f) * deltaTime);

		// What actually moves you: the built-up velocity, never faster than EFT asked for.
		Vector2 output = Vector2.ClampMagnitude(_allowed, want);
		_deficit = desired - output;
		Vector2 world = new Vector2(output.x * cos + output.y * sin, -output.x * sin + output.y * cos) * deltaTime;
		motion.x = world.x;
		motion.z = world.y;

		float ratio = output.magnitude / want;
		if (ratio < 0.9f && _rampStart < 0f)
		{
			_rampStart = Time.time;
			_rampTarget = want;
		}
		else if (ratio >= 0.97f && _rampStart >= 0f)
		{
			if (Time.time - _rampStart > 0.1f)
				TrueView.Utilities.Log.Info($"Momentum: reached {_rampTarget:0.0} m/s in {Time.time - _rampStart:0.00}s (sprint {HandlingState.IsSprinting}, load {_bodyWeight:0.00}, stamina {HandlingState.Stamina01:0.00})");
			_rampStart = -1f;
		}
	}

	public static void Tick(Player player, float deltaTime)
	{
		float rateDt = Mathf.Max(deltaTime, 1f / 300f);
		float simDt = Mathf.Clamp(deltaTime, 0f, 1f / 30f);
		if (simDt <= 0f || !HandlingConfig.MomentumEnabled.Value || player == null || !player.IsYourPlayer)
		{
			Reset();
			return;
		}

		_aimBlend = Mathf.Lerp(_aimBlend, HandlingState.IsAiming ? 1f : 0f, 1f - Mathf.Exp(-5f * simDt));
		float hipMul = 1f - _aimBlend * 0.72f;
		float amount = Mathf.Clamp01(HandlingConfig.MomentumAmount.Value);
		float weightInfluence = Mathf.Clamp01(HandlingConfig.MomentumWeight.Value);
		PhysicalBase physical = player.Physical;
		float overweight = physical == null ? 0f : Mathf.Clamp01(physical.Overweight);
		float inertia = physical == null ? 0f : Mathf.Clamp01(physical.Inertia);
		float bodyWeight = Mathf.Clamp01(overweight * 0.65f + inertia * 0.55f);
		_bodyWeight = bodyWeight * weightInfluence;
		float weightScale = 1f + bodyWeight * weightInfluence * 1.35f;

		float yaw = player.Rotation.x * Mathf.Deg2Rad;
		float sin = Mathf.Sin(yaw);
		float cos = Mathf.Cos(yaw);
		Vector3 worldVelocity = MotionTracker.Velocity;
		Vector3 localVelocity = new(
			worldVelocity.x * cos - worldVelocity.z * sin,
			worldVelocity.y,
			worldVelocity.x * sin + worldVelocity.z * cos);

		if (!_hasSample)
		{
			_lastLocalVelocity = localVelocity;
			_smoothedLocalVelocity = localVelocity;
			_lastYawRate = MotionTracker.YawRate;
			_hasSample = true;
		}

		Vector3 rawAcceleration = Vector3.ClampMagnitude((localVelocity - _lastLocalVelocity) / rateDt, 35f);
		_lastLocalVelocity = localVelocity;
		float velocityResponse = Mathf.Lerp(12f, 6f, bodyWeight);
		float accelerationResponse = Mathf.Lerp(10f, 5f, bodyWeight);
		_smoothedLocalVelocity = Vector3.Lerp(_smoothedLocalVelocity, localVelocity, 1f - Mathf.Exp(-velocityResponse * simDt));
		_smoothedLocalAcceleration = Vector3.Lerp(_smoothedLocalAcceleration, rawAcceleration, 1f - Mathf.Exp(-accelerationResponse * simDt));

		float rawYawRate = Mathf.Clamp(MotionTracker.YawRate, -720f, 720f);
		float rawYawAcceleration = Mathf.Clamp((rawYawRate - _lastYawRate) / rateDt, -1800f, 1800f);
		_lastYawRate = rawYawRate;
		_smoothedYawRate = Mathf.Lerp(_smoothedYawRate, rawYawRate, 1f - Mathf.Exp(-Mathf.Lerp(11f, 5f, bodyWeight) * simDt));
		_smoothedYawAcceleration = Mathf.Lerp(_smoothedYawAcceleration, rawYawAcceleration, 1f - Mathf.Exp(-Mathf.Lerp(9f, 4f, bodyWeight) * simDt));

		// No walk/run step this frame or last (standing, jumping, vaulting): nothing to lean into.
		if (Time.frameCount - _limitFrame > 2)
		{
			_deficit = Vector2.zero;
			_allowed = Vector2.zero;
			_movingSince = -1f;
		}

		float forwardSpeed = _smoothedLocalVelocity.z;
		float sideSpeed = _smoothedLocalVelocity.x;
		float forwardAccel = _smoothedLocalAcceleration.z;
		float sideAccel = _smoothedLocalAcceleration.x;
		float speed01 = Mathf.Clamp01(new Vector2(sideSpeed, forwardSpeed).magnitude / 5f);
		float turn01 = Mathf.Clamp(_smoothedYawRate / ReferenceTurnRate, -2f, 2f);
		float turnAccel01 = Mathf.Clamp(_smoothedYawAcceleration / 900f, -1f, 1f);
		float turnStrength = 0.5492957f * HandlingConfig.TurnRollScale;
		float startLean = HandlingConfig.MomentumStartLean?.Value ?? 1f;
		float strafeLean = HandlingConfig.MomentumStrafeLean?.Value ?? 1f;

		// Braking: only the part of the acceleration that works against the movement.
		float brakeForward = forwardSpeed * forwardAccel < 0f ? -forwardAccel : 0f;
		float brakeSide = sideSpeed * sideAccel < 0f ? sideAccel : 0f;

		// Pitch (+ looks down): lean into the start, lean with the speed, tip forward when stopping.
		float pitch = _deficit.y * 0.9f * startLean
			+ forwardSpeed * HandlingConfig.MomentumWalkForwardLean.Value * 0.32f
			+ brakeForward * 0.11f;
		float yawLean = sideAccel * 0.055f + sideSpeed * 0.08f;
		// Roll (+ tilts left): into the strafe while pushing off, a lighter lean held while
		// strafing, and into the brake when stopping one.
		float strafeRoll = -(_deficit.x * 1.2f + sideSpeed * 0.5f + brakeSide * 0.12f) * strafeLean;
		float turnRoll = -(turn01 * (2.2f + speed01 * 2.3f) + turnAccel01 * 1.4f) * turnStrength;
		Vector3 target = new(pitch, yawLean, strafeRoll + turnRoll);
		target *= amount * weightScale * hipMul * LocomotionGaitSystem.MomentumMultiplier * InjuryHandlingSystem.MomentumMultiplier;
		target.x = Mathf.Clamp(target.x, -MaxHeadPitch, MaxHeadPitch);
		target.y = Mathf.Clamp(target.y, -MaxHeadYaw, MaxHeadYaw);
		target.z = Mathf.Clamp(target.z, -MaxHeadRoll, MaxHeadRoll);

		float stopWeight = Mathf.Clamp01(HandlingConfig.MomentumStopDelay.Value / 2f);
		float frequency = Mathf.Lerp(5.5f, 2.2f, Mathf.Clamp01(bodyWeight * 0.7f + stopWeight * 0.3f));
		Spring(ref _headRotation, ref _headVelocity, target, frequency, simDt);
	}

	public static void Reset()
	{
		_lastLocalVelocity = Vector3.zero;
		_smoothedLocalVelocity = Vector3.zero;
		_smoothedLocalAcceleration = Vector3.zero;
		_lastYawRate = 0f;
		_smoothedYawRate = 0f;
		_smoothedYawAcceleration = 0f;
		_hasSample = false;
		_headRotation = Vector3.zero;
		_headVelocity = Vector3.zero;
		_aimBlend = 0f;
		_allowed = Vector2.zero;
		_deficit = Vector2.zero;
		_movingSince = -1f;
		_rampStart = -1f;
	}

	private static void Spring(ref Vector3 current, ref Vector3 velocity, Vector3 target, float frequency, float deltaTime)
	{
		float num = frequency * frequency;
		float num2 = 1.4f * frequency;
		velocity += ((target - current) * num - velocity * num2) * deltaTime;
		current += velocity * deltaTime;
	}
}
