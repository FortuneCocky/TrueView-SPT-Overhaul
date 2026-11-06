using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BloodFX.Patches;
using UnityEngine;

namespace BloodFX;

public enum QualityTier { Low, Medium, High }

/// <summary>Budget for one quality tier; see docs/DESIGN.md.</summary>
public struct Tier
{
    public int Particles, Decals, Droplets, Mist, HitsPerFrame, Wounds, Streams, Rings, Blobs;
    public bool Trails;

    public static Tier For(QualityTier q)
    {
        switch (q)
        {
            case QualityTier.Low:
                return new Tier { Particles = 600, Decals = 96, Droplets = 14, Mist = 2, HitsPerFrame = 3, Wounds = 6, Streams = 24, Rings = 12, Blobs = 3 };
            case QualityTier.High:
                return new Tier { Particles = 3000, Decals = 384, Droplets = 45, Mist = 6, HitsPerFrame = 10, Wounds = 24, Streams = 96, Rings = 24, Blobs = 10, Trails = true };
            default:
                return new Tier { Particles = 1500, Decals = 192, Droplets = 26, Mist = 4, HitsPerFrame = 6, Wounds = 12, Streams = 48, Rings = 18, Blobs = 6 };
        }
    }
}

/// <summary>
/// Volumetric-looking blood at a fixed budget: pooled particle sprays and mist,
/// predicted wall/floor splats, wound drips and death pools, all drawn from
/// textures generated at load and one batched decal mesh.
/// </summary>
[BepInPlugin("com.irl.bloodfx", "BloodFX", "1.2.0")]
public class BloodFXPlugin : BaseUnityPlugin
{
    public static BloodFXPlugin Instance { get; private set; }
    internal static ManualLogSource Log;

    public ConfigEntry<bool> Enabled { get; private set; }
    public ConfigEntry<float> Intensity { get; private set; }
    public ConfigEntry<QualityTier> Quality { get; private set; }
    public ConfigEntry<int> MaxParticles { get; private set; }
    public ConfigEntry<int> MaxDecals { get; private set; }
    public ConfigEntry<float> MaxDistance { get; private set; }
    public ConfigEntry<float> DecalLifetime { get; private set; }
    public ConfigEntry<bool> WoundDrips { get; private set; }
    public ConfigEntry<bool> BleedDrips { get; private set; }
    public ConfigEntry<bool> BrainMatter { get; private set; }
    public ConfigEntry<bool> WallSplatter { get; private set; }
    public ConfigEntry<bool> HeadDismember { get; private set; }
    public ConfigEntry<float> DismemberChance { get; private set; }
    public ConfigEntry<float> DismemberMinDamage { get; private set; }
    public ConfigEntry<bool> DeathPools { get; private set; }
    public ConfigEntry<bool> PlayerHits { get; private set; }
    public ConfigEntry<float> Brightness { get; private set; }
    public ConfigEntry<bool> Streams { get; private set; }
    public ConfigEntry<int> MaxStreams { get; private set; }
    public ConfigEntry<int> TargetFps { get; private set; }
    public ConfigEntry<bool> DebugLog { get; private set; }
    public ConfigEntry<bool> WoundDecals { get; private set; }
    public ConfigEntry<bool> ArmorMarks { get; private set; }
    public ConfigEntry<float> BodyDecalOffset { get; private set; }
    public ConfigEntry<bool> ConformMarks { get; private set; }
    public ConfigEntry<bool> DragSmears { get; private set; }
    public ConfigEntry<float> DragSmearAmount { get; private set; }
    public ConfigEntry<bool> DragSound { get; private set; }
    public ConfigEntry<float> DragSoundVolume { get; private set; }
    public ConfigEntry<bool> Footprints { get; private set; }
    public ConfigEntry<float> FootprintSplashVolume { get; private set; }
    public ConfigEntry<bool> DeadWeightOn { get; private set; }
    public ConfigEntry<float> CorpsePushScale { get; private set; }
    public ConfigEntry<bool> CorpseShotsOn { get; private set; }
    public ConfigEntry<bool> CorpseDragOn { get; private set; }
    public ConfigEntry<bool> HoldInteractToDrag { get; private set; }
    public ConfigEntry<float> DragHoldSeconds { get; private set; }
    public ConfigEntry<KeyboardShortcut> DragHotkey { get; private set; }
    public ConfigEntry<float> DragMoveSpeed { get; private set; }

    private void Awake()
    {
        Instance = this;
        Log = Logger;
        Enabled = Config.Bind("1. General", "Enabled", true, "Master switch for all blood effects.");
        Intensity = Config.Bind("1. General", "Intensity", 1f,
            new ConfigDescription("Scales droplet/mist counts and splat sizes.", new AcceptableValueRange<float>(0.1f, 3f)));
        Quality = Config.Bind("1. General", "Quality", QualityTier.Medium,
            "Low / Medium / High: particle and decal budgets, hits handled per frame, ribbon trails on High.");
        MaxParticles = Config.Bind("2. Caps", "MaxParticles", 0,
            new ConfigDescription("Hard cap on live particles per system (0 = quality default).", new AcceptableValueRange<int>(0, 10000)));
        MaxDecals = Config.Bind("2. Caps", "MaxDecals", 0,
            new ConfigDescription("Hard cap on splats/pools, oldest is reused (0 = quality default).", new AcceptableValueRange<int>(0, 2000)));
        MaxDistance = Config.Bind("2. Caps", "MaxDistance", 60f,
            new ConfigDescription("No particles for hits farther than this from the camera (metres).", new AcceptableValueRange<float>(10f, 300f)));
        DecalLifetime = Config.Bind("2. Caps", "DecalLifetime", 120f,
            new ConfigDescription("Seconds a splat stays before fading (pools stay twice as long).", new AcceptableValueRange<float>(10f, 1200f)));
        WoundDrips = Config.Bind("3. Effects", "WoundDrips", true, "Wounded bots drip blood for a few seconds after a hit.");
        BrainMatter = Config.Bind("3. Effects", "BrainMatter", true,
            "Headshots throw out 3D chunks of brain matter and skull that fall, tumble and splat where they land.");
        WallSplatter = Config.Bind("3. Effects", "HeadshotWallSplatter", true,
            "A headshot next to a wall paints it: a wide burst of drops, a dense dark core, and drips that run down.");
        HeadDismember = Config.Bind("3. Effects", "HeadDismember", true,
            "Only on a killing headshot: the head is blown apart and whatever is worn on it flies off (visual only).");
        DismemberChance = Config.Bind("3. Effects", "DismemberChance", 0.75f,
            new ConfigDescription("Chance a qualifying headshot takes the head off.", new AcceptableValueRange<float>(0f, 1f)));
        DismemberMinDamage = Config.Bind("3. Effects", "DismemberMinDamage", 40f,
            new ConfigDescription("Least damage a round must do to take the head off (pistols ~40-60, rifles 50-90, buckshot pellets ~25-40).",
                new AcceptableValueRange<float>(0f, 200f)));
        BleedDrips = Config.Bind("3. Effects", "BleedDrips", true,
            "Bleeding characters drip real drops from the wound (they fall, splash and trail behind them) " +
            "instead of EFT's flat bleeding stains.");
        DeathPools = Config.Bind("3. Effects", "DeathPools", true, "A pool slowly spreads under dead bodies.");
        PlayerHits = Config.Bind("3. Effects", "PlayerHits", false, "Also spawn blood when you are hit (can put mist in your view).");
        Brightness = Config.Bind("3. Effects", "Brightness", 1f,
            new ConfigDescription("Scales how strongly scene light brightens the blood.", new AcceptableValueRange<float>(0.2f, 3f)));
        Streams = Config.Bind("3. Effects", "VolumetricStreams", true,
            "3D liquid spurts (jets, bursts, arterial pumping, gushes) as well as the droplet spray.");
        MaxStreams = Config.Bind("2. Caps", "MaxStreams", 0,
            new ConfigDescription("Hard cap on live 3D blood streams, oldest is reused (0 = quality default).", new AcceptableValueRange<int>(0, 512)));
        TargetFps = Config.Bind("2. Caps", "TargetFps", 0,
            new ConfigDescription("Blood effects scale down when the frame rate drops below this (0 = below your usual frame rate).",
                new AcceptableValueRange<int>(0, 240)));
        WoundDecals = Config.Bind("3. Effects", "WoundDecals", true,
            "Bullet wounds stick to the body where it was hit (dark hole, torn ring, a trickle running down).");
        ArmorMarks = Config.Bind("3. Effects", "ArmorMarks", true,
            "Hits stopped by armor leave a grey impact scuff (punch mark, cracks, dust) instead of blood.");
        BodyDecalOffset = Config.Bind("3. Effects", "BodyDecalOffset", 0.02f,
            new ConfigDescription("How far wound/armor marks sit out from the hit collider toward the shooter (metres). " +
                "Raise it if marks vanish inside clothing, lower it if they float.", new AcceptableValueRange<float>(0f, 0.1f)));
        ConformMarks = Config.Bind("3. Effects", "ConformMarks", true,
            "Build wound/armor marks from the character's own mesh so they wrap over gear and skin and bend with the body. " +
            "Off = simpler marks that float just in front of the hit.");
        DragSmears = Config.Bind("3. Effects", "DragSmears", true,
            "A body dragged over the ground leaves a smeared trail behind the wounded parts that touch it: long broken streaks " +
            "in the direction of travel, a dark band where it pressed, drops flicked off along it. The more it was shot, the " +
            "heavier the smear; it thins out over a few metres and fades like other stains.");
        DragSmearAmount = Config.Bind("3. Effects", "DragSmearAmount", 1f,
            new ConfigDescription("Scales how heavy drag smears are.", new AcceptableValueRange<float>(0f, 3f)));
        Footprints = Config.Bind("3. Effects", "BloodyFootprints", true,
            "Anyone who steps in a pool of blood tracks it: left and right boot prints at each step, fainter every time, " +
            "until their feet are clean. Prints fade like other stains.");
        FootprintSplashVolume = Config.Bind("3. Effects", "FootprintSplashVolume", 0.5f,
            new ConfigDescription("Loudness of the wet splash when a foot comes down in blood, heard from the spot (0 = silent).",
                new AcceptableValueRange<float>(0f, 1f)));
        DebugLog = Config.Bind("4. Debug", "DebugLog", true,
            "Every 10 s with activity, log how many hits were seen, spawned and why the rest were skipped.");
        DeadWeightOn = Config.Bind("5. Corpses", "DeadWeight", true,
            "Dead bodies carry their gear's weight (armor, rig, backpack, up to 40 kg) on the torso, and the killing shot " +
            "pushes them about as hard as a real round would, so they don't go flying.");
        CorpsePushScale = Config.Bind("5. Corpses", "CorpsePush", 1f,
            new ConfigDescription("Scales how hard bullets push dead bodies, the killing shot included. 1 = realistic, 0 = not at all.",
                new AcceptableValueRange<float>(0f, 3f)));
        CorpseShotsOn = Config.Bind("5. Corpses", "ShotsMoveCorpses", true,
            "Bullets still push a body after it has come to rest. Off = EFT's own behaviour, where only the killing shot moves it.");
        CorpseDragOn = Config.Bind("5. Corpses", "CorpseDrag", true,
            "Lets you drag dead bodies. You walk slowly and can't sprint while dragging.");
        HoldInteractToDrag = Config.Bind("5. Corpses", "HoldInteractToDrag", true,
            "Hold the interact key on a body to start dragging it, let go to drop it. A quick tap still searches the body, " +
            "but the search now opens when you let go of the key.");
        DragHoldSeconds = Config.Bind("5. Corpses", "DragHoldTime", 2f,
            new ConfigDescription("Seconds to hold interact on a body before you start dragging it.", new AcceptableValueRange<float>(0.5f, 5f)));
        DragHotkey = Config.Bind("5. Corpses", "DragHotkey", KeyboardShortcut.Empty,
            "Optional. Hold this key while looking at a body to drag it right away, with no hold time. Let go to drop it.");
        DragMoveSpeed = Config.Bind("5. Corpses", "DragMoveSpeed", 0.35f,
            new ConfigDescription("Your top walking speed while dragging a body, as a share of full speed.", new AcceptableValueRange<float>(0.1f, 1f)));
        DragSound = Config.Bind("5. Corpses", "DragSound", true,
            "A quiet scrape of cloth and gear from the body while it slides along the ground.");
        DragSoundVolume = Config.Bind("5. Corpses", "DragSoundVolume", 0.35f,
            new ConfigDescription("Loudness of the drag sound.", new AcceptableValueRange<float>(0f, 1f)));

        new BloodHitPatch().Enable();
        new BleedingPatch().Enable();
        new StepPatch().Enable();
        new CorpseRagdollStartPatch().Enable();
        new CorpseImpulsePatch().Enable();
        new CorpseHitPatch().Enable();
        new InteractPanelPatch().Enable();
        new InteractMovePatch().Enable();

        EnsureManager();
        Log.LogInfo("BloodFX loaded");
    }

    private void Update()
    {
        try { CorpseSystem.Tick(); }
        catch (System.Exception ex) { Log.LogError($"[Corpses] tick: {ex}"); }
    }

    private void FixedUpdate()
    {
        try { CorpseSystem.FixedTick(); }
        catch (System.Exception ex) { Log.LogError($"[Corpses] physics tick: {ex}"); }
    }

    private void OnDestroy()
    {
        CorpseSystem.Reset();
    }

    private float _lastRecreate = -999f;

    /// <summary>
    /// The manager lives on BepInEx's own persistent manager object. A
    /// standalone root object got destroyed in raid, which silently turned
    /// all blood off, so if it ever goes missing it is rebuilt here.
    /// </summary>
    public BloodManager EnsureManager()
    {
        if (BloodManager.Instance != null) return BloodManager.Instance;
        if (Time.realtimeSinceStartup - _lastRecreate < 5f) return null; // don't rebuild every pellet if it keeps dying
        _lastRecreate = Time.realtimeSinceStartup;
        GameObject host = this != null ? gameObject : null;
        if (host == null)
        {
            host = new GameObject("BloodFX");
            DontDestroyOnLoad(host);
        }
        if (BloodManager.Instance == null && Time.realtimeSinceStartup > 1f)
            Log.LogWarning("BloodFX: manager was missing, recreating it");
        return host.AddComponent<BloodManager>();
    }

    public Tier CurrentTier()
    {
        Tier t = Tier.For(Quality.Value);
        if (MaxParticles.Value > 0) t.Particles = MaxParticles.Value;
        if (MaxDecals.Value > 0) t.Decals = MaxDecals.Value;
        if (MaxStreams.Value > 0) t.Streams = MaxStreams.Value;
        return t;
    }
}
