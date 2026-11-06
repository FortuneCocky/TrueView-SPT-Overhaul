using EFT;
using TrueView.Configuration;
using TrueView.Core;
using TrueView.Utilities;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace TrueView.Systems;

/// <summary>
/// Chest-mounted body camera. Builds one camera-space offset from player
/// inputs only (turn rate, gait phase, shots, hits, breathing) and adds it to
/// the main camera after every script's LateUpdate, then puts the camera back
/// just before the game places it for the next frame (Player.LateUpdate). The
/// game's camera code, the weapon and the deadzone never see the offset, so it
/// cannot feed back into the hands — the gun simply moves in the frame.
///
/// Everything else sees the camera exactly where the last frame was drawn
/// from, as in vanilla. That matters: TAA, upscalers and motion vectors read
/// the camera at different points of the frame, and the grass renderer
/// (GPUInstancer) builds its culling and its grass motion vectors in Update.
/// A camera that was put back any earlier than the game's own placement left
/// the grass moving against a pose that was never drawn, so TAA smeared it.
/// </summary>
internal static class BodyCamSystem
{
    private const float MaxPosition = 0.04f;
    private const float MaxPitchYaw = 6f;
    private const float MaxRoll = 12f;
    private const float MaxFollow = 8f;

    // Stock bump spring: ~3 Hz, under-damped, over in ~0.2 s. Impulse = peak * BumpGain.
    private const float BumpOmega = 20f;
    private const float BumpZeta = 0.5f;
    private const float BumpGain = 36f;

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
    // Last frame the player loop ran our apply step, and when we last re-hooked.
    private static int _loopFrame = -1;
    private static float _lastHookTime = -10f;
    private static int _rehooks;
    private static bool _applied;
    private static Camera _appliedCamera;
    // Kept in the camera's local space: the head animates under it between
    // the apply and the put-back, which moves the world pose but not this.
    private static Vector3 _savedPos;
    private static Quaternion _savedRot;
    private static Vector3 _writtenPos;
    private static Quaternion _writtenRot;
    // Last frame the game was about to place the camera (Player.LateUpdate).
    private static int _gameCameraFrame = -1;
    private static bool _loggedApply;

    // Player loop markers for the two steps below.
    private struct BodyCamApply { }
    private struct BodyCamRestore { }

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

    // --- Chest mount: where the camera sits and what it turns with ---
    // Body space below the eye (x right, y up, z forward), metres.
    private static Vector3 _mount;
    // x yaw, y pitch: the chest turned toward the gun, degrees.
    private static Vector2 _follow;
    private static Vector2 _followVel;
    private static float _sprintBlend;
    private static float _leanRoll;

    // --- Landing jar ---
    private static bool _wasGrounded = true;
    private static float _airTime;
    private static float _fallSpeed;
    private static float _lastY = float.NaN;

    // --- Stock bump ---
    private static bool _wasAiming;
    private static Vector3 _bump;
    private static Vector3 _bumpVel;
    private static Vector3 _bumpPos;
    private static Vector3 _bumpPosVel;

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
        TickChest(player, on, dt);

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

        // Sprinting pitches the chest forward; no head keeps it level.
        float sprintPitch = _sprintBlend * 2.5f * (on ? HandlingConfig.BodyCamUnstabilised?.Value ?? 0f : 0f);

        // A magnified scope shows every wobble, so the stock bump stays out of it.
        float bumpScale = 1f - ScopeOpticsSystem.Magnified;

        Vector3 body = new Vector3(_lag.y + breathPitch + sprintPitch, _lag.x, mountRoll + _leanRoll) + _stepRot + _stepWander;
        Vector3 euler = (body + _jolt) * bodyScale + _knock + _injuryDrift + _bump * bumpScale;
        Vector3 pos = (_stepPos + breathPos) * bodyScale + _knockPos + _bumpPos * bumpScale;

        // The chest follow has its own limit: it is a slow pose, not a shake.
        _euler = ClampEuler(euler, MaxPitchYaw, MaxRoll) + new Vector3(_follow.y, _follow.x, 0f);
        _pos = Vector3.ClampMagnitude(pos, MaxPosition);
    }

    private static void TickChest(Player player, bool on, float dt)
    {
        MovementContext mc = player.MovementContext;
        bool prone = mc?.CurrentState != null && (mc.CurrentState.Name == EPlayerState.ProneMove || mc.CurrentState.Name == EPlayerState.ProneIdle);
        // Aiming puts the eye back at the sight, whatever ADS fade says: a
        // camera on the chest would see the sight from below.
        float eye = 1f - _adsBlend;
        _sprintBlend = Mathf.MoveTowards(_sprintBlend, HandlingState.IsSprinting ? 1f : 0f, 4f * dt);

        // Mount: below the eye, on the chest. Prone, the chest is on the
        // ground and the camera would be in the dirt.
        float height = on ? (HandlingConfig.BodyCamMountHeight?.Value ?? 0f) * 0.01f : 0f;
        float side = on ? (HandlingConfig.BodyCamMountSide?.Value ?? 0f) * 0.01f : 0f;
        if (prone)
            height *= 0.25f;
        Vector3 mountTarget = new Vector3(side, -height, 0f) * eye;
        _mount = Vector3.Lerp(_mount, mountTarget, 1f - Mathf.Exp(-20f * dt));

        // Chest follow: the chest turns with the gun, so the view swings part
        // of the way toward where the gun points inside the deadzone. Reads
        // the deadzone's own offset every frame, so its settings apply live.
        float follow = on ? HandlingConfig.BodyCamChestFollow?.Value ?? 0f : 0f;
        if (!HandlingState.HasWeapon || HandlingManager.InFreeLook(player) || StanceOverhaulCompat.HandsOwned)
            follow = 0f;
        follow *= eye * (1f - _sprintBlend);
        Vector2 followTarget = Vector2.ClampMagnitude(ProceduralHipSystem.ControlOffset * follow, MaxFollow);
        const float followOmega = 7f;
        const float followZeta = 0.8f;
        _followVel += ((followTarget - _follow) * (followOmega * followOmega) - _followVel * (2f * followZeta * followOmega)) * dt;
        _follow += _followVel * dt;

        // Unstabilised body: a lean rolls the torso further than the head,
        // which tilts back toward level. Tilt > 0 leans right, roll < 0 rolls right.
        float unstable = on ? HandlingConfig.BodyCamUnstabilised?.Value ?? 0f : 0f;
        float lean = mc != null ? Mathf.Clamp(mc.SmoothedTilt / 5f, -1f, 1f) : 0f;
        _leanRoll = Mathf.Lerp(_leanRoll, -lean * 4f * unstable, 1f - Mathf.Exp(-8f * dt));

        TickLanding(player, mc, unstable, dt);
        TickStockBump(player, on, dt);
    }

    private static void TickLanding(Player player, MovementContext mc, float unstable, float dt)
    {
        // Fall speed from the body's own height change, so nothing about the
        // game's velocity bookkeeping matters.
        float y = player.Position.y;
        float vy = float.IsNaN(_lastY) ? 0f : (y - _lastY) / dt;
        _lastY = y;

        bool grounded = mc == null || mc.IsGrounded;
        if (!grounded)
        {
            _airTime += dt;
            _fallSpeed = Mathf.Max(_fallSpeed, Mathf.Min(-vy, 20f));
        }
        else
        {
            // A jump lands at ~3 m/s, a 3 m drop at ~7.5 m/s.
            if (!_wasGrounded && _airTime > 0.15f && unstable > 0f)
            {
                float impact = Mathf.Clamp01((_fallSpeed - 1f) / 6f) * unstable;
                _stepPosVel.y -= 0.5f * impact;
                _stepRotVel.x += 70f * impact;
            }
            _airTime = 0f;
            _fallSpeed = 0f;
        }
        _wasGrounded = grounded;
    }

    private static void TickStockBump(Player player, bool on, float dt)
    {
        bool aiming = HandlingState.IsAiming;
        if (aiming != _wasAiming)
        {
            _wasAiming = aiming;
            float bump = on && HandlingState.HasWeapon ? HandlingConfig.BodyCamStockBump?.Value ?? 0f : 0f;
            if (!aiming)
                bump *= 0.5f;
            if (bump > 0f)
            {
                // The stock pushes the shooting shoulder back: the camera
                // tips up and turns a little toward that side.
                float s = 1f;
                try { if (player.MovementContext?.LeftStanceController?.LeftStance == true) s = -1f; } catch { }
                if (!aiming)
                    s = -s;
                _bumpVel += new Vector3(-0.8f, 0.3f * s, -0.3f * s) * (BumpGain * bump);
                _bumpPosVel += new Vector3(0f, 0f, aiming ? -0.006f : 0.004f) * (BumpGain * bump);
            }
        }

        int n = Mathf.CeilToInt(dt / (1f / 240f));
        float h = dt / n;
        for (int i = 0; i < n; i++)
        {
            Spring(ref _bump, ref _bumpVel, BumpOmega, BumpZeta, h);
            Spring(ref _bumpPos, ref _bumpPosVel, BumpOmega, BumpZeta, h);
        }
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
        // The game keeps its own copy of the player loop
        // (PlayerLoopSystemHelpers) and puts it back whenever it edits it,
        // around raid loading for one, which drops our steps without a word.
        // If the apply step has not run for a few frames, hook in again.
        if (_installed && Time.frameCount - _loopFrame > 5 && Time.unscaledTime - _lastHookTime > 1f)
        {
            _installed = false;
            if (_applied)
                Restore();
            _rehooks++;
            if (_rehooks <= 5)
                Log.Info($"[BodyCam] the game replaced the player loop, hooking in again ({_rehooks})");
        }
        if (_installed)
            return;
        _installed = true;
        _lastHookTime = Time.unscaledTime;
        PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
        if (!InsertFirst(ref loop, typeof(PostLateUpdate), typeof(BodyCamApply), ApplyBeforeRender)
            || !InsertFirst(ref loop, typeof(EarlyUpdate), typeof(BodyCamRestore), RestoreIfStale))
        {
            Log.Warning("[BodyCam] could not hook the player loop, body camera off");
            return;
        }
        PlayerLoop.SetPlayerLoop(loop);
        Log.Info("[BodyCam] hooked: applied after LateUpdate, held until the game places the camera for the next frame");
    }

    // Puts a step at the front of one phase of the player loop.
    private static bool InsertFirst(ref PlayerLoopSystem loop, System.Type phase, System.Type marker, PlayerLoopSystem.UpdateFunction step)
    {
        if (loop.subSystemList == null)
            return false;
        for (int i = 0; i < loop.subSystemList.Length; i++)
        {
            if (loop.subSystemList[i].type != phase)
                continue;
            PlayerLoopSystem[] old = loop.subSystemList[i].subSystemList ?? new PlayerLoopSystem[0];
            foreach (PlayerLoopSystem s in old)
            {
                if (s.type == marker)
                    return true;
            }
            var list = new PlayerLoopSystem[old.Length + 1];
            list[0] = new PlayerLoopSystem { type = marker, updateDelegate = step };
            System.Array.Copy(old, 0, list, 1, old.Length);
            loop.subSystemList[i].subSystemList = list;
            return true;
        }
        return false;
    }

    // After every LateUpdate (the game has placed the camera for this frame),
    // before anything renders.
    private static void ApplyBeforeRender()
    {
        _loopFrame = Time.frameCount;
        if (_applied)
            Restore();
        if (_pos.sqrMagnitude < 1e-12f && _euler.sqrMagnitude < 1e-8f && _mount.sqrMagnitude < 1e-12f)
            return;
        Camera cam = Camera.main;
        if (cam == null)
            return;

        Transform t = cam.transform;
        _savedPos = t.localPosition;
        _savedRot = t.localRotation;
        // The mount hangs below the eye in body space: straight down and
        // across the chest, whatever the head's pitch.
        Vector3 eyePos = t.position;
        Quaternion eyeRot = t.rotation;
        Quaternion body = Quaternion.Euler(0f, eyeRot.eulerAngles.y, 0f);
        t.SetPositionAndRotation(eyePos + body * _mount + eyeRot * _pos, eyeRot * Quaternion.Euler(_euler));
        _writtenPos = t.localPosition;
        _writtenRot = t.localRotation;
        _appliedCamera = cam;
        _applied = true;

        if (!_loggedApply)
        {
            _loggedApply = true;
            Log.Info($"[BodyCam] applying to camera '{cam.name}'");
        }
    }

    /// <summary>
    /// Player.LateUpdate is about to run: hand the game back its own camera
    /// pose before it reads it to place this frame's camera.
    /// </summary>
    public static void BeforeGameCamera()
    {
        _gameCameraFrame = Time.frameCount;
        Restore();
    }

    // Fallback at the start of a frame: no player has placed the camera for
    // a while (raid over, menus), so nobody else will put it back.
    private static void RestoreIfStale()
    {
        if (_applied && Time.frameCount - _gameCameraFrame > 2)
            Restore();
    }

    // If something already moved the camera, leave it be.
    private static void Restore()
    {
        if (!_applied)
            return;
        _applied = false;
        Camera cam = _appliedCamera;
        _appliedCamera = null;
        if (cam == null)
            return;
        Transform t = cam.transform;
        if ((t.localPosition - _writtenPos).sqrMagnitude < 1e-10f && Quaternion.Dot(t.localRotation, _writtenRot) > 0.9999999f)
            t.SetLocalPositionAndRotation(_savedPos, _savedRot);
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
        _mount = Vector3.zero;
        _follow = Vector2.zero;
        _followVel = Vector2.zero;
        _sprintBlend = 0f;
        _leanRoll = 0f;
        _wasGrounded = true;
        _airTime = 0f;
        _fallSpeed = 0f;
        _lastY = float.NaN;
        _wasAiming = false;
        _bump = Vector3.zero;
        _bumpVel = Vector3.zero;
        _bumpPos = Vector3.zero;
        _bumpPosVel = Vector3.zero;
    }
}
