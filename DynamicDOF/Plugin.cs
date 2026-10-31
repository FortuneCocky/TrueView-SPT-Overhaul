using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using Prism.Utils;
using UnityEngine;

namespace DynamicDOF;

/// <summary>
/// Cinematic, eye-like depth of field for the first-person camera, driven
/// through the game's own PRISM post-processing (its DoF shader ships with
/// EFT, so no new shader is needed).
///
/// Focus follows what is at the centre of the screen (a few rays, median
/// distance) and refocuses in diopters, the way an eye or lens does: fast to
/// near, a little slower to far. Focus never goes nearer than the minimum
/// focus distance, so anything closer (a wall in your face) stays soft. The
/// gun and arms in front of you blur gently when you look past them; while
/// aiming the near blur is dropped so the sights stay sharp.
/// </summary>
[BepInPlugin("com.dynamicdof.prism", "Dynamic DOF", "1.0.0")]
public class DofPlugin : BaseUnityPlugin
{
    private enum AimMode { SightsSharp, NoBlur, Unchanged }

    private ConfigEntry<bool> _enabled, _nearBlur, _blurSky, _hud;
    private ConfigEntry<KeyboardShortcut> _toggleKey, _debugKey;
    private ConfigEntry<float> _strength, _bokeh, _minFocus, _maxFocus, _refocusNear, _refocusFar, _farRange, _nearRange, _cone;
    private ConfigEntry<DoFSamples> _samples;
    private ConfigEntry<AimMode> _aimMode;

    private Camera _cam;
    private PrismEffects _prism;
    private Saved _saved;
    private float _diopters = 0.1f; // current focus as 1/metres
    private float _rawDistance;
    private bool _active, _aiming;
    private bool _debug; // never saved: every game start begins with the debug view off
    private readonly RaycastHit[] _hits = new RaycastHit[16];
    private readonly float[] _dist = new float[5];

    /// PRISM's DoF settings as the game had them, put back when the mod lets go.
    private struct Saved
    {
        public bool Valid, UseDof, NearBlur, BlurSky, Debug;
        public float Point, Distance, NearDistance, Radius, Bokeh;
        public DoFSamples Samples;
        public Transform FocusTransform;
    }

    private void Awake()
    {
        // Keys and descriptions avoid apostrophes: one in a Config.Bind key kills Awake.
        _enabled = Config.Bind("1. General", "Enabled", true, "Turn the dynamic depth of field on or off.");
        _toggleKey = Config.Bind("1. General", "Toggle key", new KeyboardShortcut(KeyCode.Keypad7), "Turns the effect on or off in raid.");
        _debugKey = Config.Bind("1. General", "Debug view key", new KeyboardShortcut(KeyCode.Keypad8), "Shows the blur mask (white = blurred) and the focus readout, for tuning.");
        _hud = Config.Bind("1. General", "Show focus readout in debug view", true, "Small text with the focus distance while the debug view is on.");

        _strength = Config.Bind("2. Blur", "Blur strength", 0.35f, new ConfigDescription("Blur radius. Low values keep it subtle so you can still see.", new AcceptableValueRange<float>(0.05f, 2f)));
        _bokeh = Config.Bind("2. Blur", "Bokeh factor", 60f, new ConfigDescription("How much bright spots bloom into bokeh discs.", new AcceptableValueRange<float>(0f, 200f)));
        _samples = Config.Bind("2. Blur", "Quality", DoFSamples.Medium, "Blur samples. Low is cheaper.");
        _blurSky = Config.Bind("2. Blur", "Blur sky", true, "Let the sky blur when focus is near.");
        _farRange = Config.Bind("2. Blur", "Far sharp range", 0.6f, new ConfigDescription("How deep the sharp zone reaches behind the focus point, as a fraction of the focus distance (plus 1 m). Larger = less background blur.", new AcceptableValueRange<float>(0.05f, 5f)));
        _nearBlur = Config.Bind("2. Blur", "Near blur", true, "Blur things in front of the focus point: your gun and arms when looking past them, walls you are too close to.");
        _nearRange = Config.Bind("2. Blur", "Near sharp range", 0.7f, new ConfigDescription("How far in front of the focus point things stay sharp, as a fraction of the focus distance. Larger = subtler gun blur.", new AcceptableValueRange<float>(0.05f, 1f)));

        _minFocus = Config.Bind("3. Focus", "Minimum focus distance (m)", 0.45f, new ConfigDescription("The eye or lens cannot focus nearer than this; anything closer stays blurred.", new AcceptableValueRange<float>(0.1f, 3f)));
        _maxFocus = Config.Bind("3. Focus", "Maximum focus distance (m)", 400f, new ConfigDescription("Focus used when the centre of the screen hits nothing (sky).", new AcceptableValueRange<float>(20f, 2000f)));
        _refocusNear = Config.Bind("3. Focus", "Refocus speed to near", 7f, new ConfigDescription("How fast focus pulls closer (per second). Higher = snappier.", new AcceptableValueRange<float>(0.5f, 30f)));
        _refocusFar = Config.Bind("3. Focus", "Refocus speed to far", 4f, new ConfigDescription("How fast focus relaxes to distance (per second).", new AcceptableValueRange<float>(0.5f, 30f)));
        _cone = Config.Bind("3. Focus", "Focus area (degrees)", 1.5f, new ConfigDescription("Spread of the focus rays around the screen centre. Wider = steadier focus at edges.", new AcceptableValueRange<float>(0f, 8f)));

        _aimMode = Config.Bind("4. Aiming", "While aiming", AimMode.SightsSharp, "SightsSharp: no near blur, so sights and optics stay crisp. NoBlur: depth of field off while aiming. Unchanged: same as hip.");

        Logger.LogInfo("Dynamic DOF loaded");
    }

    private void Update()
    {
        if (_toggleKey.Value.IsDown()) _enabled.Value = !_enabled.Value;
        if (_debugKey.Value.IsDown() && _active) _debug = !_debug;
    }

    private void LateUpdate()
    {
        Player player = null;
        try
        {
            var world = Singleton<GameWorld>.Instance;
            player = world != null ? world.MainPlayer : null;
        }
        catch { }

        bool want = _enabled.Value && player != null && player.HealthController != null && player.HealthController.IsAlive;
        if (want && !FindPrism()) want = false;
        if (!want) { Release(); return; }

        _aiming = player.ProceduralWeaponAnimation != null && player.ProceduralWeaponAnimation.IsAiming;
        if (_aiming && _aimMode.Value == AimMode.NoBlur) { Release(); return; }

        if (!_active) Grab(player);
        Focus(player);
        Apply();
    }

    private bool FindPrism()
    {
        var cam = Camera.main;
        if (cam == null) return false;
        if (cam != _cam)
        {
            Release();
            _cam = cam;
            _prism = cam.GetComponent<PrismEffects>();
            if (_prism == null) Logger.LogWarning("Main camera has no PrismEffects; depth of field unavailable");
        }
        return _prism != null && _prism.isActiveAndEnabled;
    }

    private void Grab(Player player)
    {
        var p = _prism;
        _saved = new Saved
        {
            Valid = true, UseDof = p.useDof, NearBlur = p.useNearDofBlur, BlurSky = p.dofBlurSkybox, Debug = p.debugDofPass,
            Point = p.dofFocusPoint, Distance = p.dofFocusDistance, NearDistance = p.dofNearFocusDistance,
            Radius = p.dofRadius, Bokeh = p.dofBokehFactor, Samples = p.dofSampleAmount, FocusTransform = p.dofFocusTransform,
        };
        _active = true;
        _diopters = 1f / Mathf.Max(Measure(player), _minFocus.Value); // start in focus, no pull on spawn
    }

    private void Release()
    {
        if (_active && _prism != null && _saved.Valid)
        {
            var p = _prism;
            p.useDof = _saved.UseDof;
            p.useNearDofBlur = _saved.NearBlur;
            p.dofBlurSkybox = _saved.BlurSky;
            p.debugDofPass = _saved.Debug;
            p.dofFocusPoint = _saved.Point;
            p.dofFocusDistance = _saved.Distance;
            p.dofNearFocusDistance = _saved.NearDistance;
            p.dofRadius = _saved.Radius;
            p.dofBokehFactor = _saved.Bokeh;
            p.dofSampleAmount = _saved.Samples;
            p.dofFocusTransform = _saved.FocusTransform;
        }
        _active = false;
        _saved.Valid = false;
    }

    /// Median distance of five rays around the screen centre, ignoring the player's own body.
    private float Measure(Player player)
    {
        Transform cam = _cam.transform;
        float max = _maxFocus.Value;
        float a = _cone.Value;
        for (int i = 0; i < 5; i++)
        {
            Vector3 dir = i switch
            {
                0 => cam.forward,
                1 => Quaternion.AngleAxis(a, cam.up) * cam.forward,
                2 => Quaternion.AngleAxis(-a, cam.up) * cam.forward,
                3 => Quaternion.AngleAxis(a, cam.right) * cam.forward,
                _ => Quaternion.AngleAxis(-a, cam.right) * cam.forward,
            };
            _dist[i] = Cast(cam.position, dir, max, player.Transform.Original);
        }
        System.Array.Sort(_dist);
        return _dist[2];
    }

    private float Cast(Vector3 from, Vector3 dir, float max, Transform self)
    {
        int n = Physics.RaycastNonAlloc(from, dir, _hits, max, LayersMaskController.HighPolyWithTerrainNoGrassMask, QueryTriggerInteraction.Ignore);
        float best = max;
        for (int i = 0; i < n; i++)
        {
            var h = _hits[i];
            if (h.distance >= best || h.collider == null) continue;
            if (self != null && h.collider.transform.IsChildOf(self)) continue;
            best = h.distance;
        }
        return best;
    }

    private void Focus(Player player)
    {
        _rawDistance = Measure(player);
        float target = 1f / Mathf.Clamp(_rawDistance, _minFocus.Value, _maxFocus.Value);
        float rate = target > _diopters ? _refocusNear.Value : _refocusFar.Value;
        _diopters += (target - _diopters) * (1f - Mathf.Exp(-rate * Time.unscaledDeltaTime));
    }

    private void Apply()
    {
        var p = _prism;
        float focus = 1f / Mathf.Max(_diopters, 1e-4f);
        p.useDof = true;
        p.dofFocusTransform = null;
        p.dofFocusPoint = focus;
        p.dofFocusDistance = focus * _farRange.Value + 1f;
        bool near = _nearBlur.Value && !(_aiming && _aimMode.Value == AimMode.SightsSharp);
        p.useNearDofBlur = near;
        p.dofNearFocusDistance = Mathf.Max(0.05f, focus * _nearRange.Value);
        p.dofRadius = _strength.Value;
        p.dofBokehFactor = _bokeh.Value;
        p.dofSampleAmount = _samples.Value;
        p.dofBlurSkybox = _blurSky.Value;
        p.debugDofPass = _debug;
    }

    private void OnGUI()
    {
        if (!_active || _prism == null || !_debug || !_hud.Value || Event.current.type != EventType.Repaint) return;
        float focus = 1f / Mathf.Max(_diopters, 1e-4f);
        GUI.Label(new Rect(20, Screen.height - 60, 600, 24),
            $"Dynamic DOF  focus {focus:0.00} m  (ray {_rawDistance:0.00} m)  near blur {(_prism.useNearDofBlur ? "on" : "off")}{(_aiming ? "  aiming" : "")}");
    }

    private void OnDisable() => Release();
}
