using Comfort.Common;
using EFT;
using TrueView.Configuration;
using TrueView.Utilities;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace TrueView.Systems;

/// <summary>
/// Camera lens (formerly the Lens RL mod): barrel/fisheye distortion,
/// vignette and chromatic aberration through a global PostProcessing v2
/// volume on the FPS camera (EFT already runs a PostProcessLayer on it, so
/// nothing else is needed; if the shaders were stripped the volume stays
/// inert). Also draws the bodycam frame and drives the face cover overlay.
/// All of it is screen-space, so it never touches the deadzone, the aim or
/// the camera pose.
/// </summary>
internal sealed class CameraLensSystem : MonoBehaviour
{
    // Lens RL as a separate plugin would stack a second set of effects.
    private const string LensRlGuid = "com.lensrl";

    public static bool IsInRaid { get; private set; }

    private PostProcessVolume _volume;
    private LensDistortion _lens;
    private Vignette _vignette;
    private UnityEngine.Rendering.PostProcessing.ChromaticAberration _chromatic;
    private Camera _attachedCam;
    private bool _dirty = true;
    private bool _standaloneLoaded;

    public static void Create(GameObject host)
    {
        host.AddComponent<CameraLensSystem>();
    }

    // Start, not Awake: every plugin has loaded by the first frame.
    private void Start()
    {
        _standaloneLoaded = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(LensRlGuid);
        if (_standaloneLoaded)
            Log.Warning("Camera lens: the Lens RL plugin is also installed. TrueView's camera lens stays off until LensRL.dll is removed from BepInEx/plugins.");
    }

    private void Awake()
    {
        System.EventHandler dirty = (_, __) => _dirty = true;
        HandlingConfig.Enabled.SettingChanged += dirty;
        HandlingConfig.LensEnabled.SettingChanged += dirty;
        HandlingConfig.LensIntensity.SettingChanged += dirty;
        HandlingConfig.LensIntensityX.SettingChanged += dirty;
        HandlingConfig.LensIntensityY.SettingChanged += dirty;
        HandlingConfig.LensScale.SettingChanged += dirty;
        HandlingConfig.LensVignetteEnabled.SettingChanged += dirty;
        HandlingConfig.LensVignetteIntensity.SettingChanged += dirty;
        HandlingConfig.LensVignetteSmoothness.SettingChanged += dirty;
        HandlingConfig.LensVignetteRoundness.SettingChanged += dirty;
        HandlingConfig.LensChromaticEnabled.SettingChanged += dirty;
        HandlingConfig.LensChromaticIntensity.SettingChanged += dirty;
    }

    private static bool On => HandlingConfig.Enabled?.Value == true;

    private static Player LocalPlayer()
    {
        try
        {
            return Singleton<GameWorld>.Instance?.MainPlayer;
        }
        catch
        {
            return null;
        }
    }

    private void Update()
    {
        if (_standaloneLoaded)
            return;

        Player player = LocalPlayer();
        IsInRaid = player != null;
        if (IsInRaid)
            FaceCoverOverlaySystem.Tick(player, Time.deltaTime);
        else
            FaceCoverOverlaySystem.Reset();

        Camera cam = Camera.main;
        if (cam != _attachedCam)
        {
            // Camera changed (raid start, spectate, menu) — reattach.
            _attachedCam = cam;
            Detach();
        }

        bool any = HandlingConfig.LensEnabled.Value || HandlingConfig.LensVignetteEnabled.Value ||
            HandlingConfig.LensChromaticEnabled.Value;
        if (cam == null || !On || !any)
        {
            if (_volume != null) Detach();
            return;
        }

        if (_volume == null)
            Attach(cam);

        if (_dirty && _volume != null)
            PushValues();
    }

    private void Attach(Camera cam)
    {
        try
        {
            if (cam.GetComponent<PostProcessLayer>() == null)
                return; // not a post-processed camera (menu cam, etc.)

            var profile = ScriptableObject.CreateInstance<PostProcessProfile>();
            _lens = profile.AddSettings<LensDistortion>();
            _lens.enabled.Override(true);
            _lens.enabled.overrideState = true;

            _vignette = profile.AddSettings<Vignette>();
            _vignette.enabled.Override(true);
            _vignette.enabled.overrideState = true;
            _vignette.mode.Override(VignetteMode.Classic);
            _vignette.rounded.Override(false);

            _chromatic = profile.AddSettings<UnityEngine.Rendering.PostProcessing.ChromaticAberration>();
            _chromatic.enabled.Override(true);
            _chromatic.enabled.overrideState = true;
            _chromatic.fastMode.Override(true);

            PushValues();

            _volume = cam.gameObject.AddComponent<PostProcessVolume>();
            _volume.isGlobal = true;
            _volume.priority = 1000f;
            _volume.sharedProfile = profile;
        }
        catch (System.Exception e)
        {
            Log.Warning("Camera lens attach failed: " + e.Message);
        }
    }

    private void PushValues()
    {
        _dirty = false;
        if (_lens == null)
            return;
        _lens.intensity.Override(HandlingConfig.LensEnabled.Value ? HandlingConfig.LensIntensity.Value : 0f);
        _lens.intensityX.Override(HandlingConfig.LensIntensityX.Value);
        _lens.intensityY.Override(HandlingConfig.LensIntensityY.Value);
        _lens.scale.Override(HandlingConfig.LensEnabled.Value ? HandlingConfig.LensScale.Value : 1f);

        _vignette.intensity.Override(HandlingConfig.LensVignetteEnabled.Value ? HandlingConfig.LensVignetteIntensity.Value : 0f);
        _vignette.smoothness.Override(HandlingConfig.LensVignetteSmoothness.Value);
        _vignette.roundness.Override(HandlingConfig.LensVignetteRoundness.Value);

        _chromatic.intensity.Override(HandlingConfig.LensChromaticEnabled.Value ? HandlingConfig.LensChromaticIntensity.Value : 0f);
    }

    private void Detach()
    {
        if (_volume != null)
        {
            Destroy(_volume.sharedProfile);
            Destroy(_volume);
            _volume = null;
        }
        _lens = null;
        _vignette = null;
        _chromatic = null;
        _dirty = true;
    }

    private void OnGUI()
    {
        if (!_standaloneLoaded && On && IsInRaid && HandlingConfig.LensBodycamFrame?.Value == true)
            BodycamFrame.Draw();
    }

    private void OnDestroy() => Detach();

    /// <summary>
    /// Always-on bodycam-style frame — a rounded dark border around the
    /// screen edges drawn via IMGUI.
    /// </summary>
    private static class BodycamFrame
    {
        private const int TexSize = 512;
        private static Texture2D _texture;

        public static void Draw()
        {
            if (_texture == null)
                _texture = BuildTexture();

            float intensity = HandlingConfig.LensBodycamFrameIntensity?.Value ?? 0.85f;
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, intensity);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), _texture);
            GUI.color = old;
        }

        private static Texture2D BuildTexture()
        {
            var tex = new Texture2D(TexSize, TexSize, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            var pixels = new Color32[TexSize * TexSize];
            for (int y = 0; y < TexSize; y++)
            {
                float v = (y + 0.5f) / TexSize * 2f - 1f;
                for (int x = 0; x < TexSize; x++)
                {
                    float u = (x + 0.5f) / TexSize * 2f - 1f;
                    // Super-ellipse (n=4) — a rounded rectangle shape; the
                    // stretch to the screen keeps the shape, so one texture
                    // works for any aspect ratio.
                    float d = Mathf.Pow(Mathf.Pow(Mathf.Abs(u), 4f) + Mathf.Pow(Mathf.Abs(v), 4f), 0.25f);
                    float alpha = Mathf.Pow(Mathf.InverseLerp(0.55f, 1.05f, d), 1.3f);
                    pixels[y * TexSize + x] = new Color32(0, 0, 0, (byte)(alpha * 255f));
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return tex;
        }
    }
}
