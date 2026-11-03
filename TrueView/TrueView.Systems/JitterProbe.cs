using System.Collections.Generic;
using System.Text;
using EFT;
using EFT.Animations;
using TrueView.Configuration;
using TrueView.Core;
using UnityEngine;

namespace TrueView.Systems;

/// <summary>
/// Diagnostic: finds which stage of the frame makes the gun or the arms
/// stutter on screen.
///
/// Every frame the gun and hands are sampled at the stages that move them:
///   1. TrueView's offsets, as Patch_CalculateCameraPosition writes them
///   2. Tactical Stances' stance, lean swap and bend offsets, and the game's
///      own animated gun pose under them (its captured base)
///   3. the gun and both palms right after the arms are IK-solved (VisualPass)
///   4. the gun and both palms as rendered (camera pre-cull, after every
///      LateUpdate, including Tactical Stances' visual re-pose)
/// all in camera space, so only motion you can see on screen counts.
///
/// A stutter is a zig-zag: a channel moves one way then straight back. Each
/// channel's second difference (frame-to-frame change in velocity) is
/// tracked, and a frame whose rendered gun or palms zig-zag hard is logged
/// with every channel's change that frame, the frame time and the lean.
/// The first stage that zig-zags in the same frame is the source. Every two
/// seconds a summary gives each channel's jitter level and zig-zag count.
/// </summary>
internal static class JitterProbe
{
    private const float SummaryInterval = 2f;
    private const int MaxEventsPerWindow = 6;
    // Rendered gun/palm zig-zag that counts as a visible stutter, mm / deg.
    private const float EventMm = 0.8f;
    private const float EventDeg = 0.12f;

    private sealed class Channel
    {
        public readonly string Name;
        public readonly string Unit;
        private readonly float _zigMin;
        private Vector3 _prev, _prevVel;
        private int _have;
        public Vector3 Value;
        public float Accel;      // |second difference| this frame
        public bool Zig;         // reversed direction this frame
        public double SumSq;
        public float Max;
        public int Zigs;

        public Channel(string name, string unit, float zigMin)
        {
            Name = name;
            Unit = unit;
            _zigMin = zigMin;
        }

        public void Push(Vector3 x)
        {
            Value = x;
            Vector3 vel = x - _prev;
            if (_have >= 2)
            {
                Accel = (vel - _prevVel).magnitude;
                Zig = Vector3.Dot(vel, _prevVel) < 0f && vel.magnitude > _zigMin && _prevVel.magnitude > _zigMin;
                SumSq += Accel * Accel;
                Max = Mathf.Max(Max, Accel);
                if (Zig) Zigs++;
            }
            else
            {
                Accel = 0f;
                Zig = false;
            }
            _have = Mathf.Min(_have + 1, 2);
            _prevVel = vel;
            _prev = x;
        }

        public void Clear() => _have = 0;

        public void ResetWindow()
        {
            SumSq = 0;
            Max = 0f;
            Zigs = 0;
        }

        public string Summary(int frames) =>
            $"{Name} {Mathf.Sqrt((float)(SumSq / Mathf.Max(1, frames))):F2}{Unit} (max {Max:F2}, zigzag {Zigs})";
    }

    // Upstream to downstream, in frame order.
    private static readonly Channel CamRot = new("camera turn", "deg", 0.02f);
    private static readonly Channel CamRoll = new("camera roll", "deg", 0.02f);
    private static readonly Channel TvRot = new("TrueView gun rot", "deg", 0.02f);
    private static readonly Channel TvPos = new("TrueView gun pos", "mm", 0.1f);
    private static readonly Channel Barrel = new("barrel aim", "deg", 0.02f);
    private static readonly Channel GameRot = new("game anim gun rot", "deg", 0.02f);
    private static readonly Channel GamePos = new("game anim gun pos", "mm", 0.1f);
    private static readonly Channel TsRot = new("stance rot", "deg", 0.02f);
    private static readonly Channel TsPos = new("stance pos", "mm", 0.1f);
    private static readonly Channel TsBend = new("stance bend", "deg", 0.02f);
    private static readonly Channel IkGunRot = new("gun after IK rot", "deg", 0.02f);
    private static readonly Channel IkGunPos = new("gun after IK pos", "mm", 0.1f);
    private static readonly Channel ScrGunRot = new("gun on screen rot", "deg", 0.02f);
    private static readonly Channel ScrGunPos = new("gun on screen pos", "mm", 0.1f);
    private static readonly Channel ScrLeft = new("left palm on screen", "mm", 0.1f);
    private static readonly Channel ScrRight = new("right palm on screen", "mm", 0.1f);
    private static readonly Channel LeftOffGun = new("left palm vs gun", "mm", 0.1f);
    private static readonly Channel RightOffGun = new("right palm vs gun", "mm", 0.1f);
    private static readonly Channel MovedAfterIk = new("gun moved after IK", "mm", 0.1f);
    private static readonly Channel CamPush = new("camera pushback clamp", "mm", 0.1f);
    private static readonly Channel CamPushWanted = new("pushback the head asks for", "mm", 0.1f);
    private static readonly Channel CamVsBody = new("camera vs gun parent", "mm", 0.1f);
    private static readonly Channel CamVsHead = new("camera vs head", "mm", 0.1f);
    private static readonly Channel VanCamPos = new("game camera position", "mm", 0.1f);
    private static readonly Channel VanCamTarget = new("game camera target", "mm", 0.1f);
    private static readonly Channel VanCamSpring = new("game camera spring", "mm", 0.1f);
    private static readonly Channel CamParentVsGunParent = new("camera parent vs gun parent", "mm", 0.1f);

    private static readonly Channel[] All =
    {
        CamRot, CamRoll, VanCamTarget, VanCamSpring, VanCamPos, CamParentVsGunParent, CamPushWanted, CamPush, CamVsHead, CamVsBody, TvRot, TvPos, Barrel, GameRot, GamePos, TsRot, TsPos, TsBend,
        IkGunRot, IkGunPos, MovedAfterIk, ScrGunRot, ScrGunPos, ScrLeft, ScrRight, LeftOffGun, RightOffGun,
    };

    // Stage samples for the current frame.
    private static Vector3 _tvRot, _tvPos, _barrel;
    private static Vector3 _ikGunPos, _ikGunRot;
    private static int _ikFrame = -1;
    private static Vector3 _camVsBody, _camVsHead;
    private static float _push, _pushWanted, _pushSum;
    private static int _pushFrame = -1;
    private static int _pushFrames;

    /// <summary>Patch_LerpCamera's pushback clamp moved the camera forward by this much (m) this frame.</summary>
    private static Vector3 _vanPos, _vanTarget, _vanSpring;
    private static int _vanFrame = -1;
    private static float _lastFixedTime;
    private static int _physicsSteps;
    private static readonly int[] _stepHist = new int[4];

    /// <summary>The game's own camera placement, right after its LerpCamera and before TrueView touches it.</summary>
    public static void VanillaCamera(ProceduralWeaponAnimation pwa)
    {
        if (!Enabled)
            return;
        var hc = pwa.HandsContainer;
        if (hc?.CameraTransform == null)
            return;
        _vanPos = hc.CameraTransform.localPosition * 1000f;
        _vanTarget = pwa.VCameraTarget * 1000f;
        try { _vanSpring = hc.CameraPosition.GetRelative() * 1000f; } catch { _vanSpring = Vector3.zero; }
        _vanFrame = Time.frameCount;
    }

    public static void Pushback(float meters, float wanted)
    {
        if (!Enabled)
            return;
        _push = meters;
        _pushWanted = wanted;
        _pushFrame = Time.frameCount;
    }

    private static Vector3 _camLast;
    private static float _camPitch, _camYaw;

    private static bool _hooked;
    private static int _lastFrame = -1;
    private static int _frames;
    private static float _window;
    private static float _dtMin = float.MaxValue, _dtMax, _dtSum;
    private static int _events;
    private static float _leanMax;
    private static readonly List<string> _eventLines = new();

    private static bool Enabled => HandlingConfig.JitterProbe?.Value == true;

    /// <summary>Stage 1: right after TrueView has written the weapon offsets.</summary>
    public static void AfterTrueView()
    {
        if (!Enabled)
            return;
        _tvRot = SmallEuler(ProceduralHipSystem.WeaponOffset);
        _tvPos = ProceduralHipSystem.WeaponPosOffset * 1000f;
        _barrel = SmallEuler(BarrelAimSystem.Correction);
    }

    /// <summary>Stage 3: the arms are IK-solved onto the gun (Player.VisualPass end).</summary>
    public static void AfterIk(Player player)
    {
        if (!Enabled)
            return;
        EnsureHooked();
        Transform cam = Camera(player);
        Transform gun = player.ProceduralWeaponAnimation?.HandsContainer?.WeaponRootAnim;
        if (cam == null || gun == null)
            return;
        _ikGunPos = cam.InverseTransformPoint(gun.position) * 1000f;
        _ikGunRot = SmallEuler(Quaternion.Inverse(cam.rotation) * gun.rotation);
        Transform body = player.ProceduralWeaponAnimation.HandsContainer.WeaponRoot?.parent;
        _camVsBody = body != null ? body.InverseTransformPoint(cam.position) * 1000f : Vector3.zero;
        Transform head = player.PlayerBones?.Head?.Original;
        _camVsHead = head != null ? head.InverseTransformPoint(cam.position) * 1000f : Vector3.zero;
        _ikFrame = Time.frameCount;
    }

    private static void EnsureHooked()
    {
        if (_hooked)
            return;
        _hooked = true;
        UnityEngine.Camera.onPreCull += OnPreCull;
        Utilities.Log.Info("Jitter probe: on. Summary every 2 s while a gun is in hand; stutter frames are logged with every stage's change");
    }

    private static Transform Camera(Player player) =>
        player?.ProceduralWeaponAnimation?.HandsContainer?.CameraTransform;

    /// <summary>Stage 4: what is about to be rendered.</summary>
    private static void OnPreCull(UnityEngine.Camera camera)
    {
        if (!Enabled || camera == null || camera != UnityEngine.Camera.main || _lastFrame == Time.frameCount)
            return;
        Player player = HandlingManager.LocalPlayer();
        if (player == null || player.HandsController is not Player.FirearmController || _ikFrame != Time.frameCount)
        {
            foreach (var c in All) c.Clear();
            return;
        }
        _lastFrame = Time.frameCount;

        Transform cam = Camera(player);
        Transform gun = player.ProceduralWeaponAnimation.HandsContainer.WeaponRootAnim;
        PlayerBones bones = player.PlayerBones;
        if (cam == null || gun == null || bones == null || bones.LeftPalm == null || bones.RightPalm == null)
            return;

        float dt = Time.unscaledDeltaTime;
        Vector3 camEuler = cam.rotation.eulerAngles;
        // Unwrapped so crossing 0/360 isn't a jump.
        _camPitch += Mathf.DeltaAngle(_camLast.x, camEuler.x);
        _camYaw += Mathf.DeltaAngle(_camLast.y, camEuler.y);
        _camLast = camEuler;
        CamRot.Push(new Vector3(_camPitch, _camYaw, 0f));
        CamRoll.Push(new Vector3(0f, 0f, Mathf.DeltaAngle(0f, camEuler.z)));
        // Physics steps since the last frame: springs stepped at the physics
        // rate beat against the frame rate (0, 1 or 2 steps per frame).
        float fdt = Time.fixedDeltaTime;
        _physicsSteps = fdt > 0f ? Mathf.RoundToInt((Time.fixedTime - _lastFixedTime) / fdt) : 0;
        _lastFixedTime = Time.fixedTime;
        _stepHist[Mathf.Clamp(_physicsSteps, 0, 3)]++;
        bool van = _vanFrame == Time.frameCount;
        VanCamPos.Push(van ? _vanPos : VanCamPos.Value);
        VanCamTarget.Push(van ? _vanTarget : VanCamTarget.Value);
        VanCamSpring.Push(van ? _vanSpring : VanCamSpring.Value);
        Transform camParent = cam.parent;
        Transform gunParent = player.ProceduralWeaponAnimation.HandsContainer.WeaponRoot?.parent;
        CamParentVsGunParent.Push(camParent != null && gunParent != null
            ? gunParent.InverseTransformPoint(camParent.position) * 1000f : Vector3.zero);
        bool pushed = _pushFrame == Time.frameCount && _push > 0f;
        if (pushed) _pushFrames++;
        if (pushed) _pushSum += _push;
        CamPushWanted.Push(new Vector3(_pushFrame == Time.frameCount ? _pushWanted * 1000f : 0f, 0f, 0f));
        CamPush.Push(new Vector3(pushed ? _push * 1000f : 0f, 0f, 0f));
        CamVsHead.Push(_camVsHead);
        CamVsBody.Push(_camVsBody);
        TvRot.Push(_tvRot);
        TvPos.Push(_tvPos);
        Barrel.Push(_barrel);

        if (TacticalStancesCompat.TryRead(out Vector3 tsPos, out Vector3 tsRot, out Vector2 bend, out Vector3 basePos, out Quaternion baseRot))
        {
            TsPos.Push(tsPos * 1000f);
            TsRot.Push(tsRot);
            TsBend.Push(new Vector3(bend.x, bend.y, 0f));
            GamePos.Push(basePos * 1000f);
            GameRot.Push(SmallEuler(baseRot));
        }
        else
        {
            GamePos.Push(gun.localPosition * 1000f);
            GameRot.Push(SmallEuler(gun.localRotation));
        }

        IkGunPos.Push(_ikGunPos);
        IkGunRot.Push(_ikGunRot);
        Vector3 gunPos = cam.InverseTransformPoint(gun.position) * 1000f;
        ScrGunPos.Push(gunPos);
        ScrGunRot.Push(SmallEuler(Quaternion.Inverse(cam.rotation) * gun.rotation));
        MovedAfterIk.Push(gunPos - _ikGunPos);
        ScrLeft.Push(cam.InverseTransformPoint(bones.LeftPalm.position) * 1000f);
        ScrRight.Push(cam.InverseTransformPoint(bones.RightPalm.position) * 1000f);
        LeftOffGun.Push(gun.InverseTransformPoint(bones.LeftPalm.position) * 1000f);
        RightOffGun.Push(gun.InverseTransformPoint(bones.RightPalm.position) * 1000f);

        float lean = 0f;
        try { lean = player.MovementContext.Tilt; } catch { }
        _leanMax = Mathf.Max(_leanMax, Mathf.Abs(lean));

        _frames++;
        _window += dt;
        _dtMin = Mathf.Min(_dtMin, dt);
        _dtMax = Mathf.Max(_dtMax, dt);
        _dtSum += dt;

        bool stutter = ScrGunPos.Accel > EventMm || ScrGunRot.Accel > EventDeg
            || ScrLeft.Accel > EventMm || ScrRight.Accel > EventMm;
        if (stutter && _events < MaxEventsPerWindow)
        {
            _events++;
            _eventLines.Add(EventLine(dt, lean));
        }

        if (_window >= SummaryInterval)
            FlushWindow();
    }

    private static string EventLine(float dt, float lean)
    {
        var sb = new StringBuilder();
        sb.Append($"Jitter probe: STUTTER frame {Time.frameCount}, frame time {dt * 1000f:F1} ms, physics steps {_physicsSteps}, lean {lean:F2}, aiming {HandlingState.IsAiming}. Change in velocity this frame:");
        foreach (var c in All)
        {
            if (c.Accel < 0.005f)
                continue;
            sb.Append($" | {c.Name} {c.Accel:F2}{c.Unit}{(c.Zig ? " ZIGZAG" : "")}");
        }
        string first = FirstJittering();
        sb.Append(first != null ? $" || first stage to zigzag: {first}" : " || no stage zigzagged, a one-off jump");
        return sb.ToString();
    }

    // The most upstream channel that reversed this frame.
    private static string FirstJittering()
    {
        foreach (var c in All)
        {
            if (c.Zig && c.Accel > (c.Unit == "mm" ? 0.3f : 0.04f))
                return c.Name;
        }
        return null;
    }

    private static void FlushWindow()
    {
        float avg = _dtSum / Mathf.Max(1, _frames);
        var sb = new StringBuilder();
        sb.Append($"Jitter probe {_window:F1}s: {_frames} frames, frame time avg {avg * 1000f:F1} min {_dtMin * 1000f:F1} max {_dtMax * 1000f:F1} ms, max lean {_leanMax:F2}, stutter frames {_events}{(_events >= MaxEventsPerWindow ? "+" : "")}, camera pushback clamp active {_pushFrames * 100 / Mathf.Max(1, _frames)}% of frames (avg {_pushSum * 1000f / Mathf.Max(1, _pushFrames):F1} mm), physics steps per frame 0/1/2/3+: {_stepHist[0]}/{_stepHist[1]}/{_stepHist[2]}/{_stepHist[3]} (fixed step {Time.fixedDeltaTime * 1000f:F1} ms). RMS change in velocity per frame:");
        foreach (var c in All)
            sb.Append($"\n    {c.Summary(_frames)}");
        Utilities.Log.Info(sb.ToString());
        foreach (string line in _eventLines)
            Utilities.Log.Info(line);
        _eventLines.Clear();

        foreach (var c in All) c.ResetWindow();
        _frames = 0;
        _window = 0f;
        _dtMin = float.MaxValue;
        _dtMax = 0f;
        _dtSum = 0f;
        _events = 0;
        _leanMax = 0f;
        _pushFrames = 0;
        _pushSum = 0f;
        System.Array.Clear(_stepHist, 0, _stepHist.Length);
    }

    // Rotation as signed pitch/yaw/roll degrees around zero.
    private static Vector3 SmallEuler(Quaternion q)
    {
        Vector3 e = q.eulerAngles;
        return new Vector3(Mathf.DeltaAngle(0f, e.x), Mathf.DeltaAngle(0f, e.y), Mathf.DeltaAngle(0f, e.z));
    }
}
