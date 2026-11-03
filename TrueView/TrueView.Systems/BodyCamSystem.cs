using EFT;
using TrueView.Configuration;
using TrueView.Core;
using TrueView.Utilities;
using UnityEngine;

namespace TrueView.Systems;

/// <summary>
/// Chest-mounted body camera. Builds one camera-space offset from player
/// inputs only (turn rate, gait phase, shots, hits, breathing) and adds it to
/// the main camera right before it renders, then puts the camera back right
/// after. The game, the weapon and the deadzone never see the offset, so it
/// cannot feed back into the hands — the gun simply moves in the frame.
/// </summary>
internal static class BodyCamSystem
{
    private const float MaxPosition = 0.04f;
    private const float MaxPitchYaw = 6f;
    private const float MaxRoll = 8f;

    // Shot jolt spring: ~6 Hz, under-damped — peaks in ~40 ms, a small
    // overshoot, settled in ~150 ms. Impulse = peak * JoltGain.
    private const float JoltOmega = 37.7f;
    private const float JoltZeta = 0.45f;
    private const float JoltGain = 65.7f;

    // Step spring: ~2.2 Hz, lightly under-damped. Impulse = peak * StepGain.
    private const float StepOmega = 14f;
    private const float StepZeta = 0.7f;
    private const float StepGain = 30.4f;

    private static int _lastTickFrame = -1;

    // Final offset this frame, camera space (x right, y up, z forward) and
    // euler degrees (x>0 looks down).
    private static Vector3 _pos;
    private static Vector3 _euler;
    private static float _adsBlend;

    // --- Render-time application ---
    private static bool _installed;
    private static bool _applied;
    private static int _appliedFrame;
    private static Camera _appliedCamera;
    private static Vector3 _savedPos;
    private static Quaternion _savedRot;
    private static bool _loggedApply;

    // --- Torso lag: the chest trails the mouse turn ---
    private static Vector2 _lag;
    private static Vector2 _lagVel;

    // --- Footsteps ---
    private static int _lastStep = int.MinValue;
    private static float _stepAmp;
    private static Vector3 _stepPos;
    private static Vector3 _stepPosVel;
    private static Vector3 _stepRot;
    private static Vector3 _stepRotVel;
    private static float _wanderTime;
    private static float _legRoll;
    private static Vector3 _stepWander;

    // --- Shot jolt ---
    private static Vector3 _jolt;
    private static Vector3 _joltVel;
    private static int _shotCount;

    // --- Knock: hits and flyby flinches ---
    private static Vector3 _knock;
    private static Vector3 _knockVel;
    private static Vector3 _knockPos;
    private static Vector3 _knockPosVel;

    private static Vector3 _injuryDrift;
    private static float _injuryTime;

    /// <summary>
    /// A shot was fired. <paramref name="kick"/> is the weapon/skill/fatigue
    /// scaled kick from ShotSystem (~0.35 light .. ~1 heavy and tired).
    /// </summary>
    public static void OnShot(float kick)
    {
        if (HandlingConfig.BodyCamEnabled?.Value != true)
            return;
        float s = kick * HandlingConfig.ShotJoltScale;
        if (s <= 0f)
            return;

        // Muzzle-up pitch, a roll that flips side shot to shot, a little yaw.
        _shotCount++;
        float side = (_shotCount & 1) == 0 ? 1f : -1f;
        _joltVel += new Vector3(
            -1.6f * Random.Range(0.8f, 1.2f),
            Random.Range(-0.5f, 0.5f),
            side * Random.Range(0.4f, 1f)) * (JoltGain * s);
    }

    private static void AddKnock(Vector3 euler)
    {
        _knock += euler;
        _knock = Vector3.ClampMagnitude(_knock, 6f);
        _knockVel += euler * 7f;
        _knockVel = Vector3.ClampMagnitude(_knockVel, 35f);
    }

    /// <summary>
    /// The local player was hit: the view is knocked the way the round travelled (pitched back
    /// from the front, turned and tilted from the side), harder for the head, a stomach hit
    /// doubles him forward, a leg hit dips the view. Scaled by damage and "Hit reaction strength".
    /// </summary>
    public static void OnHitTaken(EBodyPart part, Vector3 direction, float damage, Player player)
    {
        float s = (HandlingConfig.HitReactionStrength?.Value ?? 0.8f) * Mathf.Clamp(0.35f + damage / 40f, 0.35f, 1.5f);
        if (s <= 0f)
            return;
        Transform view = Camera.main != null ? Camera.main.transform : player.Transform.Original;
        Vector3 local = direction.sqrMagnitude > 1e-4f ? view.InverseTransformDirection(direction.normalized) : Vector3.forward;
        Vector3 euler = new(local.z * 2.2f, local.x * 1.6f, -local.x * 2.4f); // x>0 looks down: hit from the front pitches him back
        float push = 1f;
        switch (part)
        {
            case EBodyPart.Head: euler *= 1.7f; push = 1.5f; break;
            case EBodyPart.Stomach: euler.x = Mathf.Abs(euler.x) * 0.6f + 1.4f; break;
            case EBodyPart.LeftArm: case EBodyPart.RightArm: euler = new Vector3(euler.x * 0.3f, euler.y * 0.8f, euler.z * 0.8f); push = 0.5f; break;
            case EBodyPart.LeftLeg: case EBodyPart.RightLeg: euler = new Vector3(1.6f, euler.y * 0.3f, euler.z * 0.6f); push = 0.6f; break;
        }
        AddKnock(euler * s);
        _knockPosVel += (local * 0.03f + (part is EBodyPart.LeftLeg or EBodyPart.RightLeg ? Vector3.down * 0.03f : Vector3.zero)) * push * s;
        _knockPosVel = Vector3.ClampMagnitude(_knockPosVel, 0.09f);
    }

    /// <summary>
    /// A round just missed: the view ducks and jerks away from the side it passed on
    /// (side -1 left .. 1 right), more the closer it came. Scaled by "Flyby flinch strength".
    /// </summary>
    public static void OnFlinch(float side, float closeness)
    {
        float s = (HandlingConfig.FlybyFlinchStrength?.Value ?? 1f) * Mathf.Lerp(0.15f, 1f, closeness * closeness);
        if (s <= 0f)
            return;
        AddKnock(new Vector3(0.9f, -side * 0.9f, side * 1.3f) * s); // away from the round, like being shoved from that side
        _knockPosVel += new Vector3(-side * 0.012f, -0.008f, 0f) * s;
        _knockPosVel = Vector3.ClampMagnitude(_knockPosVel, 0.09f);
    }

    public static void Tick(Player player, float deltaTime)
    {
        if (_lastTickFrame == Time.frameCount)
            return;
        _lastTickFrame = Time.frameCount;
        EnsureInstalled();

        float dt = Mathf.Min(deltaTime, 1f / 30f);
        if (dt <= 0f)
            return;
        if (player == null || player.HealthController == null || !player.HealthController.IsAlive)
        {
            Reset();
            return;
        }

        // Inputs are gated, the springs are not — switching off lets
        // everything settle back to zero instead of snapping.
        bool on = HandlingState.Enabled && HandlingConfig.BodyCamEnabled?.Value == true;
        _adsBlend = Mathf.MoveTowards(_adsBlend, HandlingState.IsAiming ? 1f : 0f, 6f * dt);
        // A scope magnifies every wobble of the head, so it always fades fully there.
        float adsFade = Mathf.Max(Mathf.Clamp01(HandlingConfig.BodyCamAdsFade?.Value ?? 1f), ScopeOpticsSystem.Magnified);
        float bodyScale = 1f - _adsBlend * adsFade;

        TickTorsoLag(on, dt);
        TickSteps(player, on, dt);

        // Shot jolt — sub-stepped, the spring is stiff.
        int n = Mathf.CeilToInt(dt / (1f / 240f));
        float h = dt / n;
        for (int i = 0; i < n; i++)
            Spring(ref _jolt, ref _joltVel, JoltOmega, JoltZeta, h);
        _jolt = ClampEuler(_jolt, 3f, 3f);

        // Knock — near-critical, softer the smaller it is.
        float recovery = 10f;
        float frequency = Mathf.Lerp(recovery * 0.38f, recovery, Mathf.Clamp01(_knock.magnitude / 1.5f));
        _knockVel += (-_knock * (frequency * frequency) - _knockVel * (2f * frequency)) * dt;
        _knock += _knockVel * dt;
        _knockPosVel += (-_knockPos * 400f - _knockPosVel * 26f) * dt;
        _knockPos += _knockPosVel * dt;

        // Injury drift: a hurt head or torso can't hold the view still.
        float injury = on ? InjuryHandlingSystem.HeadPenalty + InjuryHandlingSystem.TorsoPenalty * 0.35f : 0f;
        _injuryTime += dt * Mathf.Lerp(0.6f, 1.4f, Mathf.Clamp01(injury));
        Vector3 injuryTarget = new(
            (Mathf.PerlinNoise(_injuryTime, 41.3f) - 0.5f) * injury * 0.9f,
            (Mathf.PerlinNoise(73.1f, _injuryTime) - 0.5f) * injury * 0.7f,
            (Mathf.PerlinNoise(_injuryTime * 0.7f, 19.9f) - 0.5f) * injury * 1.1f);
        _injuryDrift = Vector3.Lerp(_injuryDrift, injuryTarget, 1f - Mathf.Exp(-3f * dt));

        // Breathing: chest rise and fall on the cycle you hear.
        float breathGain = on ? HandlingConfig.BodyCamBreath?.Value ?? 0f : 0f;
        float breathAmp = breathGain * Mathf.Lerp(0.35f, 1.4f, Mathf.Max(1f - HandlingState.Stamina01, BreathingSystem.Depth));
        float bIn = Mathf.Sin(BreathingSystem.Phase);
        float bOut = 1f - Mathf.Cos(BreathingSystem.Phase);
        Vector3 breathPos = new(0f, bOut * 0.0035f * breathAmp, bOut * 0.0015f * breathAmp);
        float breathPitch = bIn * 0.28f * breathAmp;

        // Mount tilt: the camera never sits quite level on a moving chest.
        float tilt = on ? HandlingConfig.BodyCamTilt?.Value ?? 0f : 0f;
        float mountRoll = (Mathf.PerlinNoise(Time.time * 0.05f, 3.7f) - 0.5f) * 2f * tilt;

        Vector3 body = new Vector3(_lag.y + breathPitch, _lag.x, mountRoll) + _stepRot + _stepWander;
        Vector3 euler = (body + _jolt) * bodyScale + _knock + _injuryDrift;
        Vector3 pos = (_stepPos + breathPos) * bodyScale + _knockPos;

        _euler = ClampEuler(euler, MaxPitchYaw, MaxRoll);
        _pos = Vector3.ClampMagnitude(pos, MaxPosition);
    }

    private static void TickTorsoLag(bool on, float dt)
    {
        // Turning right: the gun goes with the mouse, the chest arrives late,
        // so the view sits left of the aim and the gun leads across the frame.
        float amount = on ? HandlingConfig.BodyCamTurnLag?.Value ?? 0f : 0f;
        Vector2 target = new Vector2(-MotionTracker.YawRate, -MotionTracker.PitchRate * 0.5f) * (0.012f * amount);
        target.x = Mathf.Clamp(target.x, -4f, 4f);
        target.y = Mathf.Clamp(target.y, -2.5f, 2.5f);
        const float omega = 9f;
        const float zeta = 0.55f;
        _lagVel += ((target - _lag) * (omega * omega) - _lagVel * (2f * zeta * omega)) * dt;
        _lag += _lagVel * dt;
    }

    private static void TickSteps(Player player, bool on, float dt)
    {
        MovementContext mc = player.MovementContext;
        Vector3 vel = player.Velocity;
        float speed = new Vector2(vel.x, vel.z).magnitude;
        float move = Mathf.Clamp01(speed / 0.8f);
        float pace = Mathf.Clamp01(speed / 4.5f);
        float pose = mc != null ? mc.PoseLevel : 1f;
        bool prone = mc?.CurrentState != null && (mc.CurrentState.Name == EPlayerState.ProneMove || mc.CurrentState.Name == EPlayerState.ProneIdle);
        bool grounded = mc == null || mc.IsGrounded;

        float bounce = on ? HandlingConfig.BodyCamStepBounce?.Value ?? 0f : 0f;
        float targetAmp = bounce * move * Mathf.Lerp(0.55f, 1.25f, pace) * Mathf.Lerp(0.7f, 1f, Mathf.InverseLerp(0.4f, 0.9f, pose)) * (prone ? 0.4f : 1f);
        _stepAmp = Mathf.Lerp(_stepAmp, targetAmp, 1f - Mathf.Exp(-6f * dt));

        // Every π of the gait phase is a footfall.
        int step = Mathf.FloorToInt(LocomotionGaitSystem.StepPhase / Mathf.PI);
        if (_lastStep == int.MinValue)
            _lastStep = step;
        if (step != _lastStep)
        {
            _lastStep = step;
            if (_stepAmp > 0.01f && grounded)
                Footfall(step);
        }

        int n = Mathf.CeilToInt(dt / (1f / 120f));
        float h = dt / n;
        for (int i = 0; i < n; i++)
        {
            Spring(ref _stepPos, ref _stepPosVel, StepOmega, StepZeta, h);
            Spring(ref _stepRot, ref _stepRotVel, StepOmega, StepZeta, h);
        }

        // Irregular gait: slow wander so no two strides look the same.
        float irregular = HandlingConfig.BodyCamIrregularity?.Value ?? 0f;
        _wanderTime += dt * Mathf.Lerp(0.5f, 1.1f, pace);
        Vector3 wander = new Vector3(
            Mathf.PerlinNoise(_wanderTime, 11.1f) - 0.5f,
            Mathf.PerlinNoise(23.3f, _wanderTime) - 0.5f,
            Mathf.PerlinNoise(_wanderTime * 0.8f, 57.7f) - 0.5f) * (0.8f * irregular * _stepAmp);

        // A hurt leg leans the whole body to that side while walking.
        _legRoll = Mathf.Lerp(_legRoll, on ? InjuryHandlingSystem.LegBias * 1.2f * move : 0f, 1f - Mathf.Exp(-4f * dt));
        _stepWander = wander + new Vector3(0f, 0f, _legRoll);
    }

    private static void Footfall(int step)
    {
        float side = (step & 1) == 0 ? -1f : 1f;
        float irregular = HandlingConfig.BodyCamIrregularity?.Value ?? 0f;
        float r = 1f + Random.Range(-0.3f, 0.3f) * irregular * 2f;
        float tired = Mathf.Lerp(0.9f, 1.3f, 1f - HandlingState.Stamina01);
        float a = _stepAmp * Mathf.Max(0.2f, r) * tired * StepGain;

        // Weight drop and pitch dip on each strike, the chest rolling onto
        // the planted foot — alternating, so the roll runs at stride rate.
        _stepPosVel += new Vector3(side * 0.003f, -0.008f, 0f) * a;
        _stepRotVel += new Vector3(0.9f, side * 0.3f * Random.Range(0.5f, 1f), side * 1.2f) * a;
    }

    private static void Spring(ref Vector3 x, ref Vector3 v, float omega, float zeta, float h)
    {
        v += (-x * (omega * omega) - v * (2f * zeta * omega)) * h;
        x += v * h;
    }

    private static Vector3 ClampEuler(Vector3 e, float pitchYaw, float roll)
    {
        return new Vector3(
            Mathf.Clamp(e.x, -pitchYaw, pitchYaw),
            Mathf.Clamp(e.y, -pitchYaw, pitchYaw),
            Mathf.Clamp(e.z, -roll, roll));
    }

    private static void EnsureInstalled()
    {
        if (_installed)
            return;
        Camera.onPreCull += OnPreCull;
        Camera.onPostRender += OnPostRender;
        _installed = true;
        Log.Info("[BodyCam] render hook installed");
    }

    private static void OnPreCull(Camera cam)
    {
        // Post-render never came back. Same frame: undo before re-applying.
        // A later frame: the game has already rewritten the camera pose
        // (FirstPersonCameraState sets it every LateUpdate), so restoring the
        // saved pose would drag the camera back a frame — just forget it.
        if (_applied && Time.frameCount != _appliedFrame)
        {
            _applied = false;
            _appliedCamera = null;
        }
        else if (_applied && cam == _appliedCamera)
        {
            Restore();
        }
        if (_applied || cam == null || cam != Camera.main)
            return;
        if (_pos.sqrMagnitude < 1e-12f && _euler.sqrMagnitude < 1e-8f)
            return;

        Transform t = cam.transform;
        _savedPos = t.position;
        _savedRot = t.rotation;
        t.SetPositionAndRotation(_savedPos + _savedRot * _pos, _savedRot * Quaternion.Euler(_euler));
        _appliedCamera = cam;
        _appliedFrame = Time.frameCount;
        _applied = true;

        if (!_loggedApply)
        {
            _loggedApply = true;
            Log.Info($"[BodyCam] applying to camera '{cam.name}'");
        }
    }

    private static void OnPostRender(Camera cam)
    {
        if (_applied && cam == _appliedCamera)
            Restore();
    }

    private static void Restore()
    {
        if (_appliedCamera != null)
            _appliedCamera.transform.SetPositionAndRotation(_savedPos, _savedRot);
        _appliedCamera = null;
        _applied = false;
    }

    public static void Reset()
    {
        if (_applied)
            Restore();
        _pos = Vector3.zero;
        _euler = Vector3.zero;
        _adsBlend = 0f;
        _lag = Vector2.zero;
        _lagVel = Vector2.zero;
        _lastStep = int.MinValue;
        _stepAmp = 0f;
        _stepPos = Vector3.zero;
        _stepPosVel = Vector3.zero;
        _stepRot = Vector3.zero;
        _stepRotVel = Vector3.zero;
        _stepWander = Vector3.zero;
        _wanderTime = 0f;
        _legRoll = 0f;
        _jolt = Vector3.zero;
        _joltVel = Vector3.zero;
        _knock = Vector3.zero;
        _knockVel = Vector3.zero;
        _knockPos = Vector3.zero;
        _knockPosVel = Vector3.zero;
        _injuryDrift = Vector3.zero;
        _injuryTime = 0f;
    }
}
