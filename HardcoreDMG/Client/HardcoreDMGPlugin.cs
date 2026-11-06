using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;
using HardcoreDMGClient.Patches;
using HarmonyLib;
using SPT.Reflection.Patching;
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace HardcoreDMGClient;

[BepInPlugin("com.devin.hardcoredmg.client", "HardcoreDMG Client", "1.1.0")]
public class HardcoreDMGPlugin : BaseUnityPlugin
{
    public static ManualLogSource Log;

    // Damage model
    public static ConfigEntry<bool> DamageModelOn;
    public static ConfigEntry<float> DamageScale;
    public static ConfigEntry<float> HeadshotScale;
    public static ConfigEntry<bool> TorsoZonesOn;
    public static ConfigEntry<float> ChestScale;
    public static ConfigEntry<float> StomachScale;
    public static ConfigEntry<float> ArmScale;
    public static ConfigEntry<float> LegScale;
    public static ConfigEntry<float> HeavyBleedScale;
    public static ConfigEntry<float> LightBleedScale;

    // Damage (Player) — the player's own damage scales
    public static ConfigEntry<bool> PlayerDamageOn;
    public static ConfigEntry<float> PlayerDamageScale;
    public static ConfigEntry<float> PlayerHeadshotScale;
    public static ConfigEntry<float> PlayerChestScale;
    public static ConfigEntry<float> PlayerStomachScale;
    public static ConfigEntry<float> PlayerArmScale;
    public static ConfigEntry<float> PlayerLegScale;
    public static ConfigEntry<float> PlayerHeavyBleedScale;
    public static ConfigEntry<float> PlayerLightBleedScale;

    // Wound reactions
    public static ConfigEntry<bool> WeaponDropOn;
    public static ConfigEntry<float> WeaponDropOdds;
    public static ConfigEntry<bool> WoundFallOn;
    public static ConfigEntry<float> WoundFallOdds;

    // Bullet physics
    public static ConfigEntry<bool> BulletPhysicsOn;
    public static ConfigEntry<float> FragmentationScale;
    public static ConfigEntry<float> PenetrationScale;
    public static ConfigEntry<float> ArmorEnergyLoss;
    public static ConfigEntry<float> RicochetScale;

    // Leg Cripple — forces crouch/prone based on leg damage
    public static ConfigEntry<bool> EnableLegCripple;
    public static ConfigEntry<bool> LegCrippleStayDown;
    public static ConfigEntry<bool> LegCrippleEnablePlayer;
    public static ConfigEntry<bool> LegCrippleEnableBots;
    public static ConfigEntry<float> LegCrippleCrouchBelow;
    public static ConfigEntry<float> LegCrippleProneBelow;

    // Shotguns
    public static ConfigEntry<bool> ShotgunsOn;
    public static ConfigEntry<float> ShotgunEnergyModel;
    public static ConfigEntry<float> ShotgunPointBlankRange;
    public static ConfigEntry<float> ShotgunPointBlankBonus;
    public static ConfigEntry<float> ShotgunEffectiveRange;
    public static ConfigEntry<float> ShotgunSpreadFalloff;
    public static ConfigEntry<float> ShotgunMinRangeScale;
    public static ConfigEntry<float> ShotgunClusterBonus;
    public static ConfigEntry<float> ShotgunBuckshotBlunt;
    public static ConfigEntry<float> ShotgunSlugDamage;
    public static ConfigEntry<float> ShotgunSlugBlunt;

    private void Awake()
    {
        Log = Logger;

        const string model = "1. Damage Model";
        const string reactions = "2. Wound Reactions (Player)";
        const string bullets = "3. Bullet Physics";

        DamageModelOn = Config.Bind(model, "Damage model active", true,
            "Turns all of HardcoreDMG's client-side damage changes on or off.");
        DamageScale = Config.Bind(model, "Overall damage scale", 1.0f,
            new ConfigDescription("Scales every bullet, melee and explosion hit on the player and bots. 1.0 = unchanged.", new AcceptableValueRange<float>(0.1f, 3f), Array.Empty<object>()));
        HeadshotScale = Config.Bind(model, "Headshot damage scale", 1.0f,
            new ConfigDescription("Extra scale for head hits, on top of the overall scale. 1.0 = unchanged.", new AcceptableValueRange<float>(0.1f, 3f), Array.Empty<object>()));
        TorsoZonesOn = Config.Bind(model, "Anatomical torso zones", true,
            "Reads which part of the chest or stomach was struck: heart (always blacks the part), spine (takes most of the remaining HP), flanks (glancing), pelvis (bleeds more).");
        ChestScale = Config.Bind(model, "Chest damage scale", 1.0f,
            new ConfigDescription("Scales chest hits, after the torso zone.", new AcceptableValueRange<float>(0.1f, 3f), Array.Empty<object>()));
        StomachScale = Config.Bind(model, "Stomach damage scale", 1.0f,
            new ConfigDescription("Scales stomach hits, after the torso zone.", new AcceptableValueRange<float>(0.1f, 3f), Array.Empty<object>()));
        ArmScale = Config.Bind(model, "Arm damage scale", 0.85f,
            new ConfigDescription("Scales arm hits. Arm wounds are rarely fatal, so the default is a little lower.", new AcceptableValueRange<float>(0.1f, 3f), Array.Empty<object>()));
        LegScale = Config.Bind(model, "Leg damage scale", 0.9f,
            new ConfigDescription("Scales leg hits.", new AcceptableValueRange<float>(0.1f, 3f), Array.Empty<object>()));
        HeavyBleedScale = Config.Bind(model, "Heavy bleed odds scale", 1.0f,
            new ConfigDescription("Scales the odds that a hit opens a heavy bleed.", new AcceptableValueRange<float>(0f, 3f), Array.Empty<object>()));
        LightBleedScale = Config.Bind(model, "Light bleed odds scale", 1.0f,
            new ConfigDescription("Scales the odds that a hit opens a light bleed.", new AcceptableValueRange<float>(0f, 3f), Array.Empty<object>()));

        // Keys start with "Player" so they never match the main damage model keys.
        const string playerDamage = "1. Damage Model (Player)";
        PlayerDamageOn = Config.Bind(playerDamage, "Enable player damage settings", false,
            "Hits on you use only the settings in this section and ignore the main damage model scales. Off = you take damage like everyone else.");
        PlayerDamageScale = Config.Bind(playerDamage, "Player overall damage scale", 1.0f,
            new ConfigDescription("Scales every bullet, melee and explosion hit on you. 1.0 = unchanged.", new AcceptableValueRange<float>(0.1f, 3f), Array.Empty<object>()));
        PlayerHeadshotScale = Config.Bind(playerDamage, "Player headshot damage scale", 1.0f,
            new ConfigDescription("Extra scale for head hits on you, on top of your overall scale.", new AcceptableValueRange<float>(0.1f, 3f), Array.Empty<object>()));
        PlayerChestScale = Config.Bind(playerDamage, "Player chest damage scale", 1.0f,
            new ConfigDescription("Scales chest hits on you, after the torso zone.", new AcceptableValueRange<float>(0.1f, 3f), Array.Empty<object>()));
        PlayerStomachScale = Config.Bind(playerDamage, "Player stomach damage scale", 1.0f,
            new ConfigDescription("Scales stomach hits on you, after the torso zone.", new AcceptableValueRange<float>(0.1f, 3f), Array.Empty<object>()));
        PlayerArmScale = Config.Bind(playerDamage, "Player arm damage scale", 0.85f,
            new ConfigDescription("Scales arm hits on you.", new AcceptableValueRange<float>(0.1f, 3f), Array.Empty<object>()));
        PlayerLegScale = Config.Bind(playerDamage, "Player leg damage scale", 0.9f,
            new ConfigDescription("Scales leg hits on you.", new AcceptableValueRange<float>(0.1f, 3f), Array.Empty<object>()));
        PlayerHeavyBleedScale = Config.Bind(playerDamage, "Player heavy bleed odds scale", 1.0f,
            new ConfigDescription("Scales the odds that a hit on you opens a heavy bleed.", new AcceptableValueRange<float>(0f, 3f), Array.Empty<object>()));
        PlayerLightBleedScale = Config.Bind(playerDamage, "Player light bleed odds scale", 1.0f,
            new ConfigDescription("Scales the odds that a hit on you opens a light bleed.", new AcceptableValueRange<float>(0f, 3f), Array.Empty<object>()));

        WeaponDropOn = Config.Bind(reactions, "Weapon drop on arm wounds", false,
            "Lets a bad arm wound knock the weapon out of your hands. Player only. Off by default.");
        WeaponDropOdds = Config.Bind(reactions, "Weapon drop base odds percent", 2f,
            new ConfigDescription("Starting odds in percent. Bigger wounds compared with the arm's HP raise them, and hand or wrist hits count extra.", new AcceptableValueRange<float>(0f, 100f), Array.Empty<object>()));
        WoundFallOn = Config.Bind(reactions, "Fall on leg wounds", false,
            "A leg hit that leaves your leg below 35% HP can knock you prone. Player only. Off by default.");
        WoundFallOdds = Config.Bind(reactions, "Fall base odds percent", 25f,
            new ConfigDescription("Starting odds in percent. Lower leg HP and bigger wounds raise them, and thigh hits count extra.", new AcceptableValueRange<float>(0f, 100f), Array.Empty<object>()));

        BulletPhysicsOn = Config.Bind(bullets, "Bullet physics active", true,
            "Turns HardcoreDMG's bullet changes on or off: speed-based damage, fragmentation, armor penetration and ricochets.");
        FragmentationScale = Config.Bind(bullets, "Fragmentation odds scale", 1.5f,
            new ConfigDescription("Scales how often bullets break apart inside the target. Faster rounds fragment more. 1.0 = unchanged.", new AcceptableValueRange<float>(0.5f, 3f), Array.Empty<object>()));
        PenetrationScale = Config.Bind(bullets, "Penetration odds scale", 1.2f,
            new ConfigDescription("Scales how often bullets get through armor. 1.0 = unchanged.", new AcceptableValueRange<float>(0.5f, 3f), Array.Empty<object>()));
        ArmorEnergyLoss = Config.Bind(bullets, "Energy lost through armor", 0.4f,
            new ConfigDescription("How much damage a weak round loses when it only just gets through armor. 0 = none, 0.4 = up to 40%, 1 = all of it.", new AcceptableValueRange<float>(0f, 1f), Array.Empty<object>()));
        RicochetScale = Config.Bind(bullets, "Ricochet odds scale", 1.3f,
            new ConfigDescription("Scales how often bullets glance off at shallow angles. 1.0 = unchanged.", new AcceptableValueRange<float>(0.5f, 3f), Array.Empty<object>()));

        // === Leg Cripple ===
        const string cripple = "5. Leg Cripple";
        EnableLegCripple = Config.Bind(cripple, "Enable leg cripple", false,
            "Master toggle for the leg cripple system. Wounded legs force the actor into crouch, and badly wounded legs into prone. Everything else in this section needs it on. Off by default.");
        LegCrippleStayDown = Config.Bind(cripple, "Stay down until healed", true,
            "A crippled bot or player can't get back up past what their legs allow: they stay prone while both legs are below the prone threshold, and crouched while either leg is below the crouch threshold. They get up once healed or on painkillers. Needs leg cripple on.");
        LegCrippleEnablePlayer = Config.Bind(cripple, "Enable for player", false,
            "Lets leg cripple limit the local player. Off = it only affects bots.");
        LegCrippleEnableBots = Config.Bind(cripple, "Enable for bots", true,
            "Apply leg cripple effects to AI bots (PMCs, scavs, etc.).");
        LegCrippleCrouchBelow = Config.Bind(cripple, "Crouch below leg HP percent", 20f,
            new ConfigDescription("When either leg drops below this share of its max HP, or is blacked, the actor is forced into crouch.", new AcceptableValueRange<float>(0f, 90f), Array.Empty<object>()));
        LegCrippleProneBelow = Config.Bind(cripple, "Prone below leg HP percent", 10f,
            new ConfigDescription("When both legs drop below this share of their max HP, the actor is forced prone. A blacked leg always counts as below it, so two blacked legs always mean prone.", new AcceptableValueRange<float>(0f, 90f), Array.Empty<object>()));

        MigrateOldSettings();

        // === Shotguns ===
        const string shotguns = "6. Shotguns";
        ShotgunsOn = Config.Bind(shotguns, "Shotgun rework active", true,
            "Reworks buckshot and slug damage: pellet energy, pattern spread with range, pellet count and armor. Needs the damage model active.");
        ShotgunEnergyModel = Config.Bind(shotguns, "Pellet energy model", 1.0f,
            new ConfigDescription("How far pellet and slug damage follows kinetic energy (speed squared) instead of the game's near-linear speed loss. 0 = vanilla, 1 = full energy model.", new AcceptableValueRange<float>(0f, 1f), Array.Empty<object>()));
        ShotgunPointBlankRange = Config.Bind(shotguns, "Point blank range meters", 8f,
            new ConfigDescription("Up to this distance the buckshot pattern is still one tight mass and gets the full point blank bonus.", new AcceptableValueRange<float>(0f, 20f), Array.Empty<object>()));
        ShotgunPointBlankBonus = Config.Bind(shotguns, "Point blank damage bonus", 1.25f,
            new ConfigDescription("Buckshot pellet damage multiplier at point blank. Fades to 1.0 at the effective range.", new AcceptableValueRange<float>(1f, 2f), Array.Empty<object>()));
        ShotgunEffectiveRange = Config.Bind(shotguns, "Effective range meters", 25f,
            new ConfigDescription("Past this distance the pattern opens up and buckshot damage falls off.", new AcceptableValueRange<float>(10f, 60f), Array.Empty<object>()));
        ShotgunSpreadFalloff = Config.Bind(shotguns, "Spread falloff exponent", 1.0f,
            new ConfigDescription("How fast buckshot falls off past the effective range: damage x (effective / distance) to this power. 0 = no falloff, 2 = pattern area (inverse square).", new AcceptableValueRange<float>(0f, 3f), Array.Empty<object>()));
        ShotgunMinRangeScale = Config.Bind(shotguns, "Minimum range scale", 0.3f,
            new ConfigDescription("The spread falloff never takes buckshot below this share of its damage.", new AcceptableValueRange<float>(0.05f, 1f), Array.Empty<object>()));
        ShotgunClusterBonus = Config.Bind(shotguns, "Pellet cluster bonus", 0.3f,
            new ConfigDescription("Extra damage as more pellets from one shell hit the same target. The last pellet of a full hit gets the whole bonus, so a full hit averages half of it.", new AcceptableValueRange<float>(0f, 1f), Array.Empty<object>()));
        ShotgunBuckshotBlunt = Config.Bind(shotguns, "Buckshot blunt trauma scale", 1.25f,
            new ConfigDescription("Scales the blunt damage of pellets stopped by armor.", new AcceptableValueRange<float>(0.5f, 3f), Array.Empty<object>()));
        ShotgunSlugDamage = Config.Bind(shotguns, "Slug damage scale", 1.15f,
            new ConfigDescription("Scales slug hits that get through. A slug is one heavy, wide projectile.", new AcceptableValueRange<float>(0.5f, 2f), Array.Empty<object>()));
        ShotgunSlugBlunt = Config.Bind(shotguns, "Slug blunt trauma scale", 1.5f,
            new ConfigDescription("Scales the blunt damage of slugs stopped by armor. A stopped slug still slams the plate.", new AcceptableValueRange<float>(0.5f, 3f), Array.Empty<object>()));

        // SPT ModulePatches are not picked up by Harmony.PatchAll; each one has to
        // be enabled. They are always enabled and check their own settings on
        // every call, so toggling a setting in F12 works mid-raid.
        int enabled = 0;
        ModulePatch[] patches =
        {
            new ApplyDamageInfoPatch(), new ApplyDamagePatch(),
            new BallisticsPatch_Create(), new BallisticsPatch_IsBulletFragmented(),
            new BallisticsPatch_IsTargetPenetrated(), new BallisticsPatch_IsRicocheted(),
            new ProneLockPatch(), new CrouchLockPatch(), new ShotgunShotPatch(),
        };
        foreach (ModulePatch patch in patches)
        {
            try
            {
                patch.Enable();
                enabled++;
            }
            catch (Exception ex)
            {
                Log.LogError($"HardcoreDMG Client — {patch.GetType().Name} failed to enable: {ex.Message}");
            }
        }
        Log.LogInfo($"HardcoreDMG Client loaded — {enabled}/{patches.Length} patches enabled");

        if (EnableLegCripple.Value)
        {
            Log.LogInfo("HardcoreDMG Client — leg cripple system enabled (player: " + LegCrippleEnablePlayer.Value + ", bots: " + LegCrippleEnableBots.Value + ")");
        }
    }

    /// <summary>
    /// Carries values over from settings that moved or were replaced, and
    /// drops the ones that are gone, so updating keeps a player's choices and
    /// leaves no dead entries in the cfg. BepInEx keeps unbound entries in its
    /// OrphanedEntries until they are bound or removed.
    /// </summary>
    private void MigrateOldSettings()
    {
        try
        {
            if (AccessTools.Property(typeof(ConfigFile), "OrphanedEntries")?.GetValue(Config) is not Dictionary<ConfigDefinition, string> orphans ||
                orphans.Count == 0)
                return;

            string Take(string section, string key)
            {
                var def = new ConfigDefinition(section, key);
                if (!orphans.TryGetValue(def, out string value)) return null;
                orphans.Remove(def);
                return value;
            }

            const string oldReactions = "2. Wound Reactions";
            foreach (ConfigEntryBase entry in new ConfigEntryBase[] { WeaponDropOn, WeaponDropOdds, WoundFallOn, WoundFallOdds })
            {
                string value = Take(oldReactions, entry.Definition.Key);
                if (value != null) entry.SetSerializedValue(value);
            }

            // The crouch threshold was a 0-1 fraction; it is a percent now.
            string crouch = Take("5. Leg Cripple", "Crouch health threshold");
            if (float.TryParse(crouch, NumberStyles.Float, CultureInfo.InvariantCulture, out float fraction))
                LegCrippleCrouchBelow.Value = fraction * 100f;

            Take("5. Leg Cripple", "Crawl speed");
            foreach (string key in new[] { "Enable prone lock", "Prone lock leg HP threshold", "Enable crouch lock", "Crouch lock leg HP threshold" })
                Take("4. Stance Lock", key);

            Config.Save();
        }
        catch (Exception ex)
        {
            Log.LogWarning($"HardcoreDMG Client — could not carry over old settings: {ex.Message}");
        }
    }

    private void Update()
    {
        // Leg cripple tick runs every frame regardless of damage/ballistics toggles,
        // because it is a movement system, not a damage modifier.
        if (EnableLegCripple == null || !EnableLegCripple.Value)
            return;

        try
        {
            LegCrippleSystem.Tick(Time.deltaTime);
        }
        catch (Exception ex)
        {
            Log.LogError($"[LegCrippleSystem] Tick failed: {ex}");
        }
    }

    private void OnDestroy()
    {
        LegCrippleSystem.Reset();
    }
}
