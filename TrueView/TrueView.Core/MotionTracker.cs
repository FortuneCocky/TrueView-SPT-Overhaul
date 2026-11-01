using EFT;
using UnityEngine;

namespace TrueView.Core;

internal static class MotionTracker
{
	private const float MaxDeltaTime = 1f / 15f;

	private const float MaxRate = 720f;

	private const float FastSmoothing = 14f;

	private const float SlowSmoothing = 5f;

	private static Vector2 _lastRotation;

	private static Vector3 _lastPosition;

	private static bool _hasSample;

	private static float _yawRate;

	private static float _pitchRate;

	private static float _yawRateSlow;

	public static float YawRate => _yawRate;

	public static float PitchRate => _pitchRate;

	public static float YawRateSlow => _yawRateSlow;

	public static float TurnRate => new Vector2(_yawRate, _pitchRate).magnitude;

	// View turns TrueView made itself (camera settle after firing). Taken off
	// the next measured delta so they don't read as mouse input.
	private static Vector2 _selfRotation;

	public static void AddSelfRotation(Vector2 delta) => _selfRotation += delta;

	public static bool IsMoving { get; private set; }

	public static float NormalizedSpeed { get; private set; }

	public static Vector2 InputDirection { get; private set; }

	public static float Lean { get; private set; }

	public static void Update(Player player, float deltaTime)
	{
		// Smoothing integrates with a capped dt so hitches don't overshoot,
		// but the rate measurement must use the real frame time — dividing a
		// 200ms frame's rotation delta by a 66ms clamp inflates turn rates up
		// to 3x, which spikes every rate-driven layer (whip, lead, lean).
		float rateDt = Mathf.Max(deltaTime, 0.0033333334f);
		deltaTime = Mathf.Clamp(deltaTime, 0.0033333334f, 1f / 15f);
		Vector2 rotation = player.Rotation;
		Vector3 position = player.Position;
		MovementContext movementContext = player.MovementContext;
		NormalizedSpeed = ((movementContext == null) ? 0f : Mathf.Clamp01(movementContext.CharacterMovementSpeed / 0.6f));
		Lean = ((movementContext == null) ? 0f : Mathf.Clamp(movementContext.Tilt / 5f, -1f, 1f));
		InputDirection = player.InputDirection;
		if (!_hasSample)
		{
			_lastRotation = rotation;
			_lastPosition = position;
			_hasSample = true;
			return;
		}
		IsMoving = InputDirection.sqrMagnitude > 0.0001f || NormalizedSpeed > 0.05f;
		_lastPosition = position;
		float num = Mathf.DeltaAngle(_lastRotation.x, rotation.x) - _selfRotation.x;
		float num2 = rotation.y - _lastRotation.y - _selfRotation.y;
		_selfRotation = Vector2.zero;
		_lastRotation = rotation;
		if (Mathf.Abs(num) > 90f)
		{
			num = 0f;
		}
		if (Mathf.Abs(num2) > 90f)
		{
			num2 = 0f;
		}
		// Recoil does not touch player.Rotation in this game version (it only
		// moves the rendered camera and the hands), so the mouse keeps
		// driving every motion layer while firing. Zeroing the rates during a
		// shot froze the torso lag, lead, bend and turn roll, and the view
		// locked rigidly to the gun until the shot settled.
		TrackShotWindow(num, num2);
		float target = Mathf.Clamp(num / rateDt, -720f, 720f);
		float target2 = Mathf.Clamp(num2 / rateDt, -720f, 720f);
		_yawRate = Smooth(_yawRate, target, deltaTime, 14f);
		_pitchRate = Smooth(_pitchRate, target2, deltaTime, 14f);
		_yawRateSlow = Smooth(_yawRateSlow, target, deltaTime, 5f);
	}

	public static void Reset(Player player)
	{
		_hasSample = false;
		_yawRate = 0f;
		_pitchRate = 0f;
		_yawRateSlow = 0f;
		IsMoving = false;
		NormalizedSpeed = 0f;
		InputDirection = Vector2.zero;
		Lean = 0f;
		if (player != null)
		{
			_lastRotation = player.Rotation;
			_lastPosition = player.Position;
			_hasSample = true;
		}
	}

	// One log line per shot window: how far the mouse turned and whether the
	// motion layers saw it (they used to be frozen to zero while firing).
	private static bool _shotWindow;
	private static float _shotTurn;
	private static float _shotPeakRate;

	private static void TrackShotWindow(float pitchDelta, float yawDelta)
	{
		if (Systems.ShotSystem.InShotRecovery)
		{
			_shotWindow = true;
			_shotTurn += new Vector2(pitchDelta, yawDelta).magnitude;
			_shotPeakRate = Mathf.Max(_shotPeakRate, TurnRate);
			return;
		}
		if (!_shotWindow)
			return;
		_shotWindow = false;
		Utilities.Log.Verbose($"Shot window: mouse turned {_shotTurn:F1} deg, peak turn rate seen {_shotPeakRate:F0} deg/s, aiming {HandlingState.IsAiming}, gun off center {Systems.ProceduralHipSystem.ControlAngle:F1} deg");
		_shotTurn = 0f;
		_shotPeakRate = 0f;
	}

	private static float Smooth(float current, float target, float deltaTime, float speed)
	{
		return Mathf.Lerp(current, target, 1f - Mathf.Exp((0f - deltaTime) * speed));
	}
}
