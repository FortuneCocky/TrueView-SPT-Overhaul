using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using SPT.Reflection.Patching;
using EuphoriaReaction.Patches;

namespace EuphoriaReaction;

/// <summary>
/// Euphoria-style hit reactions, driven by the real Delirium
/// physics engine (delirium_physics.dll). On a non-lethal hit the victim's
/// body is handed to a Delirium agent: the bullet's impulse lands at the
/// hit point, the shot task makes him reach for the wound, and the
/// balance task tries to catch him — catch-steps, buckling, falls — until
/// the body settles and the animation takes back over.
/// </summary>
[BepInDependency(AmosBridge.Guid, BepInDependency.DependencyFlags.SoftDependency)] // loads after it, so its config and types are there in Awake
[BepInPlugin("com.euphoria.reaction", "Euphoria Reaction", "2.2.0")]
public partial class EuphoriaPlugin : BaseUnityPlugin
{
    public static EuphoriaPlugin Instance { get; private set; }

    public ConfigEntry<bool> Enabled { get; private set; }
    public ConfigEntry<float> Chance { get; private set; }
    public ConfigEntry<float> Impulse { get; private set; }
    public ConfigEntry<bool> HitNudgeOn { get; private set; }
    public ConfigEntry<float> NudgeStrength { get; private set; }
    public ConfigEntry<bool> PhysNudge { get; private set; }
    public ConfigEntry<float> PhysNudgeRange { get; private set; }
    public ConfigEntry<bool> PhysNudgeOffScreen { get; private set; }
    public ConfigEntry<bool> ThreadedSteps { get; private set; }
    public ConfigEntry<bool> HeavyNudge { get; private set; }
    public ConfigEntry<float> HeavyNudgeDamage { get; private set; }
    public ConfigEntry<float> HeavyNudgeTime { get; private set; }
    public ConfigEntry<bool> BallisticShove { get; private set; }
    public ConfigEntry<int> MaxDistance { get; private set; }
    public ConfigEntry<bool> AiHitsToo { get; private set; }
    public ConfigEntry<float> Duration { get; private set; }
    public ConfigEntry<bool> AliveFalls { get; private set; }
    public ConfigEntry<Toughness> Tough { get; private set; }
    public ConfigEntry<bool> DeathEnabled { get; private set; }
    public ConfigEntry<float> DeathStruggle { get; private set; }
    public ConfigEntry<float> AgonyChance { get; private set; }
    public ConfigEntry<float> CrouchStumble { get; private set; }
    public ConfigEntry<float> StuckMax { get; private set; }
    public ConfigEntry<bool> RestTwist { get; private set; }
    public ConfigEntry<float> FailSafeTime { get; private set; }
    public ConfigEntry<float> GroundGrip { get; private set; }
    public ConfigEntry<bool> FallenPose { get; private set; }
    public ConfigEntry<float> GetUpSpeed { get; private set; }
    public ConfigEntry<float> ArmGetUpSpeed { get; private set; }
    public ConfigEntry<bool> SmoothDoll { get; private set; }
    public ConfigEntry<bool> PhysGetUp { get; private set; }
    public ConfigEntry<float> GetUpPhysMix { get; private set; }
    public ConfigEntry<float> GetUpAssist { get; private set; }
    public ConfigEntry<float> ChestGetUp { get; private set; }
    public ConfigEntry<float> ProneBelowHp { get; private set; }
    public ConfigEntry<bool> DropGun { get; private set; }
    public ConfigEntry<bool> CancelActions { get; private set; }
    public ConfigEntry<float> StumbleThreshold { get; private set; }
    public ConfigEntry<float> SprintStumble { get; private set; }
    public ConfigEntry<float> ShotgunForce { get; private set; }
    public ConfigEntry<float> ShotgunRange { get; private set; }
    public ConfigEntry<bool> SniperAlways { get; private set; }
    public ConfigEntry<bool> SniperMarksman { get; private set; }
    public ConfigEntry<float> SniperForce { get; private set; }
    public ConfigEntry<float> LegBuckleDamage { get; private set; }
    public ConfigEntry<bool> LogScores { get; private set; }
    public ConfigEntry<bool> ArmsInStumble { get; private set; }
    public ConfigEntry<bool> LockSkeleton { get; private set; }
    public ConfigEntry<float> OpenHitbox { get; private set; }
    public ConfigEntry<float> SettleTime { get; private set; }
    public ConfigEntry<bool> ImpactDamage { get; private set; }
    public ConfigEntry<float> ImpactSafeSpeed { get; private set; }
    public ConfigEntry<float> JoulesPerHp { get; private set; }
    public ConfigEntry<float> ArmourStop { get; private set; }
    public ConfigEntry<bool> WoundGrab { get; private set; }
    public ConfigEntry<bool> CapturePoses { get; private set; }
    public ConfigEntry<bool> BlockFire { get; private set; }
    public ConfigEntry<float> AimBackChance { get; private set; }
    public ConfigEntry<bool> FallMotions { get; private set; }
    public ConfigEntry<StumbleLength> StumbleLen { get; private set; }
    public ConfigEntry<float> StumbleMin { get; private set; }
    public ConfigEntry<float> StumbleMax { get; private set; }
    public ConfigEntry<float> HurtBelowHp { get; private set; }
    public ConfigEntry<float> AimBackDuration { get; private set; }
    public ConfigEntry<float> AimBackCone { get; private set; }
    public ConfigEntry<bool> BossGate { get; private set; }
    public ConfigEntry<bool> VerboseLog { get; private set; }
    public ConfigEntry<float> BossHp { get; private set; }
    public ConfigEntry<bool> AmosCoop { get; private set; }
    public ConfigEntry<bool> AmosTurnOff { get; private set; }

    internal static void Log(object msg) => Instance?.Logger.LogInfo(msg);
    /// Only written when "Verbose log" is on (Debug section, advanced).
    internal static void LogV(object msg) { if (Instance != null && Instance.VerboseLog != null && Instance.VerboseLog.Value) Instance.Logger.LogInfo(msg); }

    private static ConfigDescription Adv(string text, int order, AcceptableValueBase range = null) =>
        new ConfigDescription(text, range, new ConfigurationManagerAttributes { IsAdvanced = true, Order = order });

    [DllImport("kernel32", SetLastError = true)]
    private static extern System.IntPtr LoadLibrary(string path);

    private void Awake()
    {
        Instance = this;

        Enabled = Config.Bind("1. Hit Reactions", "Enable reactions", true, "Full Euphoria takeover on non-lethal hits — catch-steps, wound clutch, buckling, falls.");
        Chance = Config.Bind("1. Hit Reactions", "Reaction chance", 0.9f, new ConfigDescription("Chance a non-lethal hit triggers a reaction.", new AcceptableValueRange<float>(0f, 1f)));
        MaxDistance = Config.Bind("1. Hit Reactions", "Max distance (m)", 80, new ConfigDescription("Reactions only trigger within this range of the shooter.", new AcceptableValueRange<int>(5, 300)));
        AiHitsToo = Config.Bind("1. Hit Reactions", "Allow AI-caused reactions", true, "When off, only your hits trigger reactions.");
        Duration = Config.Bind("1. Hit Reactions", "Reaction duration (s)", 8f, new ConfigDescription("How long physics owns the body before animation takes back over. Stumbles end by Stumble length; this only limits reactions with no stumble clock (mid-air).", new AcceptableValueRange<float>(2f, 20f)));
        AliveFalls = Config.Bind("1. Hit Reactions", "Alive bots can fall", true, "On: heavy hits/leg shots can knock a living bot down (he stays down, then rises). Off: stumble/balance only.");
        Tough = Config.Bind("1. Hit Reactions", "Toughness", Toughness.Natural,
            "How hard bots are to knock down. All of them stagger the same way, stepping to catch their balance; the physics decides whether "
            + "they stay up, and this only moves the odds. Natural: about a third of hits put him down (mostly leg hits). Tough: about one in eight. "
            + "Easy: about half, and a hard chest hit can too. Custom: your own balance.txt next to the mod (same format as the files in presets/).");
        DeathEnabled = Config.Bind("2. Death", "Death struggle", true, "On a lethal hit Euphoria plays his death to the end, then SPT's corpse ragdoll takes over (he can't be looted before). Off: SPT's ragdoll straight away.");
        AgonyChance = Config.Bind("2. Death", "Agony chance", 0.25f, new ConfigDescription("Chance a lethal torso hit becomes the long agony (stagger, collapse, writhe on the ground) instead of the death fall (the hit reaction carries him down limp, physical until he settles). Head shots always fall, instantly limp; arm and leg hits are 1.6x as likely to be agony.", new AcceptableValueRange<float>(0f, 1f)));
        DeathStruggle = Config.Bind("2. Death", "Death struggle length (s)", 10f, new ConfigDescription("How long the agony lasts before SPT's ragdoll takes over. (A death fall ends when the body settles, within a few seconds.)", new AcceptableValueRange<float>(3f, 20f)));

        FallenPose = Config.Bind("3. Get up", "Hand back in SPT pose", true, "After a fall the bot gets up with a get-up animation (from his belly, his back or his side). Only face down and badly hurt (below) does he stay down in prone. Off: a plain blend back to his animation.");
        ProneBelowHp = Config.Bind("3. Get up", "Stay prone below HP", 0.35f, new ConfigDescription("Recovering with less than this share of his total HP left, he stays down in prone (any way he lies). 0 = always gets up.", new AcceptableValueRange<float>(0f, 1f)));
        PhysGetUp = Config.Bind("3. Get up", "Physical get-up", true, "The get-up plays through the physics body instead of over it: the clip is what its muscles aim for, its pelvis and chest are helped up, and hands and knees push on the real floor, so he rises with weight and settles instead of moving like a canned animation. A hit while he rises knocks him back into the reaction. Off = the clip alone.");
        GetUpPhysMix = Config.Bind("3. Get up", "Get-up physics mix", 0.5f, new ConfigDescription("How much of what you see is the physics body (1) versus the clip (0) while he gets up. The clip takes over at the end, but what you see never strays more than a hand's width from the physics body.", new AcceptableValueRange<float>(0f, 1f)));
        GetUpAssist = Config.Bind("3. Get up", "Get-up assist", 0.6f, new ConfigDescription("How hard the physics body is helped to where the clip has it (pelvis and chest most, the limbs less, more for a part that has fallen behind). Lower: heavier, more physical, but he may not make it up (the clip then hands him back anyway). Higher: follows the clip closely.", new AcceptableValueRange<float>(0f, 1.5f)));
        SmoothDoll = Config.Bind("5. Stumble behaviour", "Smooth doll following", true, "The physics body steps at 60 Hz; at higher frame rates SPT's bones used to change on only some frames and moved in chunks. On: the bones are posed from the doll's state between its last two steps, so it follows smoothly at any frame rate. Off = the latest step as it is.");
        ArmGetUpSpeed = Config.Bind("3. Get up", "Arm get-up speed", 1f, new ConfigDescription("Playback speed of the arm get-ups (back: sit up on the hands, one knee; chest: all fours, one knee; side: hand and knee, one knee). They're made at real speed: up in about 2.2-2.4 s.", new AcceptableValueRange<float>(0.5f, 2f)));
        ChestGetUp = Config.Bind("3. Get up", "Get up from the chest", 0.5f, new ConfigDescription("Chance a bot lying face down pushes himself up to stand with the arm get-up, instead of staying in prone.", new AcceptableValueRange<float>(0f, 1f)));
        GetUpSpeed = Config.Bind("3. Get up", "Get-up speed", 0.7f, new ConfigDescription("Playback speed of the get-up and roll-over clips (lower = slower, heavier).", new AcceptableValueRange<float>(0.3f, 1.5f)));
        HitNudgeOn = Config.Bind("1. Hit Reactions", "Hit nudge", true, "Every hit that doesn't start a stumble or death still rocks the body where it landed (spine, head, arm or hip), on top of the animation. No ragdoll; flows straight into a stumble or death if one starts.");
        HeavyNudge = Config.Bind("1. Hit Reactions", "Heavy nudge", true, "A limb hit with massive damage (below) sends that limb limp for a moment, like a quick local ragdoll, then it springs back to where the animation has it. Legs kick out, the free arm drops and swings; the gun arm takes a hard shoulder jolt instead (the gun would float off a limp gun arm).");
        HeavyNudgeDamage = Config.Bind("1. Hit Reactions", "Heavy nudge damage", 35f, new ConfigDescription("Damage a single arm or leg hit needs for a heavy nudge.", new AcceptableValueRange<float>(5f, 120f)));
        HeavyNudgeTime = Config.Bind("1. Hit Reactions", "Heavy nudge limp time (s)", 1f, new ConfigDescription("How long the limb stays limp before it springs back.", new AcceptableValueRange<float>(0.2f, 3f)));
        PhysNudge = Config.Bind("1. Hit Reactions", "Physical hit nudge", true, "Hit nudges come from a Delirium doll of his own, like poking him in the Playroom: the bullet's push lands on the part that was hit and the doll's muscles and balance answer it (chest rocks, head whips, a knee buckles), laid over his animation. Off: the simple spring nudge.");
        NudgeStrength = Config.Bind("1. Hit Reactions", "Hit nudge strength", 1f, new ConfigDescription("How hard hit nudges rock the body (1 = about 7 degrees of bend on a typical chest hit).", new AcceptableValueRange<float>(0f, 3f)));
        DropGun = Config.Bind("1. Hit Reactions", "Heavy hits drop the weapon", false, "A hard hit tears the weapon out of the hand. Off: the gun stays in his hand.");
        CancelActions = Config.Bind("1. Hit Reactions", "Cancel bot actions during Euphoria", true, "Until he has fully recovered (physics, roll-over / get-up, blend back) a bot does nothing of his own: his AI, pathing, moving and turning are held, and healing/item use is stopped. A bot aiming back at his shooter keeps his AI for that.");

        StumbleThreshold = Config.Bind("4. Stumble scoring", "Stumble threshold", 1f, new ConfigDescription("A hit stumbles when its score >= random(0..1) x this. 1 = the score is the probability; 0.5 = twice as likely; 2 = half as likely.", new AcceptableValueRange<float>(0.1f, 3f)));
        SprintStumble = Config.Bind("4. Stumble scoring", "Sprinting multiplier", 1.8f, new ConfigDescription("Score multiplier when the bot is sprinting (plus a flat +0.25). Sprinting bots are easy to knock off balance.", new AcceptableValueRange<float>(1f, 3f)));
        ShotgunForce = Config.Bind("4. Stumble scoring", "Shotgun close-range force", 2.5f, new ConfigDescription("Score multiplier for a shotgun pellet at point blank (4 m or less), fading to none at the shotgun range below. Up close the blast also shoves with most of its pellets' momentum. 1 = shotguns score like any round.", new AcceptableValueRange<float>(1f, 5f)));
        ShotgunRange = Config.Bind("4. Stumble scoring", "Shotgun range (m)", 15f, new ConfigDescription("Distance where a shotgun stops counting as a close blast.", new AcceptableValueRange<float>(5f, 40f)));
        SniperAlways = Config.Bind("4. Stumble scoring", "Sniper rifles always stumble", true, new ConfigDescription("Every non-lethal hit from a bolt-action sniper rifle triggers a reaction, at any range (past 'Max distance' too), with no roll.", null));
        SniperMarksman = Config.Bind("4. Stumble scoring", "Marksman rifles count as snipers", false, new ConfigDescription("Treat marksman rifles (SVD, M1A, SR-25 ...) like sniper rifles too.", null));
        SniperForce = Config.Bind("4. Stumble scoring", "Sniper force", 1.5f, new ConfigDescription("Shove multiplier for a sniper rifle's round, on top of its own momentum.", new AcceptableValueRange<float>(1f, 3f)));
        OpenHitbox = Config.Bind("4. Stumble scoring", "Unarmoured hitbox multiplier", 1.5f, new ConfigDescription("Score multiplier for a hit on a hitbox no armour covers (neck, face, pelvis, bare chest or sides, limbs). Those hits are also never reduced to a flinch.", new AcceptableValueRange<float>(1f, 3f)));
        ArmourStop = Config.Bind("4. Stumble scoring", "Armour stop multiplier", 0.6f, new ConfigDescription("Score multiplier for a round his armour stops: a smaller blunt shove than a bare hit. 1 = the old blunt-force score.", new AcceptableValueRange<float>(0.2f, 1.5f)));
        LegBuckleDamage = Config.Bind("4. Stumble scoring", "Leg buckle damage", 22f, new ConfigDescription("Leg hits with at least this much damage force the knee to give out.", new AcceptableValueRange<float>(5f, 80f)));
        ArmsInStumble = Config.Bind("5. Stumble behaviour", "Arms in physics while stumbling", true, "The arms join the physics during the stumble (brace, flail, clutch). The right hand keeps the gun.");
        RestTwist = Config.Bind("5. Stumble behaviour", "Twist from the rig rest pose", true, "The aimed skeleton's bone twist (forearm and wrist roll, spine wind) starts from the rig's rest pose, the same for every bot. Off = from the pose he was in when the reaction took him (mid-aim, mid-run), which could leave forearms turned over or the spine wound up for the whole reaction.");
        LockSkeleton = Config.Bind("5. Stumble behaviour", "Aim skeleton at physics joints", true, "While Euphoria owns a bot, every SPT bone (legs, spine, neck, and the arms once he goes over) is turned to point where the physics body's matching limb points, keeping SPT's own bone lengths (no stretched mesh), and the hands and feet are bent onto the physics body's (two-bone IK). Off = the older drive: SPT copies each physics part's rotation, held to its joint ranges.");
        WoundGrab = Config.Bind("5. Stumble behaviour", "Grab wounds", false, "A hit bot reaches for and holds the wound with his free hand. Off for now.");
        CapturePoses = Config.Bind("9. Debug (advanced)", "Capture lying poses", false, "Every time a downed bot settles on the ground, his physics pose is saved to lying_poses.jsonl in the plugin folder (start poses for generating get-up animations).");
        BlockFire = Config.Bind("5. Stumble behaviour", "Block shooting during Euphoria", true, "A bot cannot fire while Euphoria owns him, while he is down, getting up, or blending back.");
        StumbleLen = Config.Bind("5. Stumble behaviour", "Stumble length", StumbleLength.Natural, "How long a stumble may last. Short 0.4-3 s, Natural 0.8-6 s, Long 1.5-8 s, Custom uses the two sliders below. He stumbles at least the shorter time; steady after it he recovers. Whether he falls is the physics' call, not the clock's: still on his feet but not steady at the longer time, he is eased back into his stance.");
        StumbleMin = Config.Bind("5. Stumble behaviour", "Stumble min (s)", 0.8f, new ConfigDescription("Custom stumble length: the least time he stumbles before he may recover.", new AcceptableValueRange<float>(0.1f, 5f)));
        StumbleMax = Config.Bind("5. Stumble behaviour", "Stumble max (s)", 3f, new ConfigDescription("Custom stumble length: still not steady after this long (and not fallen), he is eased back into his stance.", new AcceptableValueRange<float>(0.5f, 12f)));
        HurtBelowHp = Config.Bind("5. Stumble behaviour", "Hurt below HP", 0.65f, new ConfigDescription("Recovering with less than this share of his HP, he comes back one pose lower (stand to crouch, crouch to deep crouch). Below Stay prone below HP he recovers prone.", new AcceptableValueRange<float>(0f, 1f)));
        FallMotions = Config.Bind("5. Stumble behaviour", "Fall reactions by direction", true, "On: going over face first his arms go out ahead to catch the floor; going over backwards they wheel for a moment and then reach back behind him; sideways the arm on the low side goes out. Off: the same catch whichever way he falls.");
        AimBackChance = Config.Bind("5. Stumble behaviour", "Aim-back chance", 0.3f, new ConfigDescription("Chance a stumbling bot tries to aim the gun back at whoever shot him (one-handed) and fire a few shots.", new AcceptableValueRange<float>(0f, 1f)));
        AimBackDuration = Config.Bind("5. Stumble behaviour", "Aim-back window (s)", 2.5f, new ConfigDescription("How long after a hit the bot may try to shoot back, if he is still on his feet.", new AcceptableValueRange<float>(0.5f, 6f)));
        AimBackCone = Config.Bind("5. Stumble behaviour", "Aim-back cone (deg)", 40f, new ConfigDescription("A shot only goes off when the gun points within this angle of the shooter.", new AcceptableValueRange<float>(5f, 90f)));
        SettleTime = Config.Bind("5. Stumble behaviour", "Settled hand-back time", 5f, new ConfigDescription("Seconds a bot in a reaction may rest close to a stance (stand, crouch, prone, or lying for a get-up) before the reaction ends and he is taken into it.", new AcceptableValueRange<float>(1f, 15f)));
        FailSafeTime = Config.Bind("5. Stumble behaviour", "Fail-safe time limit", 25f, new ConfigDescription("Seconds after the takeover before a bot still in the ragdoll, whatever it's doing, is let go and jumps up to stand. 0 turns it off.", new AcceptableValueRange<float>(0f, 120f)));
        StuckMax = Config.Bind("5. Stumble behaviour", "Stuck time limit", 12f, new ConfigDescription("Seconds after the last hit before a bot still in a reaction (stuck lying, draped, wedged) is put in the stance closest to how he lies.", new AcceptableValueRange<float>(5f, 40f)));
        ApplyLegCollision();
        GroundGrip = Config.Bind("5. Stumble behaviour", "Ground grip", 1.5f, new ConfigDescription("Friction of the ground under the physics body (1 = engine default). Higher: feet slip and slide less, bodies slide less once down.", new AcceptableValueRange<float>(0.5f, 3f)));
        CrouchStumble = Config.Bind("5. Stumble behaviour", "Crouch stumble chance", 0.3f, new ConfigDescription("Leg hits only: chance (doubled) the hit leg buckles and he sinks into a crouch (softer legs, still catching his balance) and recovers crouched. Shoves and torso hits never do this: he recovers standing.", new AcceptableValueRange<float>(0f, 1f)));

        ImpactDamage = Config.Bind("7. Impact damage", "Falls and crashes hurt a living bot", true, "While Euphoria owns a living bot, a hard landing or a fast crash (a wall, a rail, the floor) costs him HP: the kinetic energy above the safe speed, over the Joules per HP below.");
        ImpactSafeSpeed = Config.Bind("7. Impact damage", "Safe impact speed (m/s)", 5.4f, new ConfigDescription("Speed below which a landing or crash does nothing. 5.4 m/s is about a 1.5 m drop.", new AcceptableValueRange<float>(1f, 20f)));
        JoulesPerHp = Config.Bind("7. Impact damage", "Joules per HP", 20f, new ConfigDescription("Kinetic energy above the safe speed that costs one HP. Lower = deadlier. An 80 kg body at 8 m/s (about a 3.3 m drop) takes about 70 HP, at 12 m/s (about 7 m) about 230.", new AcceptableValueRange<float>(1f, 500f)));

        BossGate = Config.Bind("6. Bosses", "Bosses only stumble when nearly dead", true, "A boss only gets a Euphoria stumble when the hit meets the normal bullet requirement (score roll, penetration, caliber...) AND his health is at or below the limit below. Death struggles still play.");
        BossHp = Config.Bind("6. Bosses", "Boss health limit", 0.30f, new ConfigDescription("Fraction of total health at or below which a boss can stumble.", new AcceptableValueRange<float>(0.05f, 1f)));

        const string Perf = "10. Performance";
        ThreadedSteps = Config.Bind(Perf, "Step dolls side by side", true, "The physics dolls (every reacting bot, every hit nudge) step at the same time on a few worker threads instead of one after another on the main thread. Same physics, same result; with several bots reacting at once it saves several ms a frame. Off = all on the main thread, as before.");
        PhysNudgeRange = Config.Bind(Perf, "Physical hit nudge range (m)", 100f, new ConfigDescription("Hit nudges on bots further from your camera than this use the simple spring nudge instead of a physics doll of their own (each doll steps 60 times a second for up to 3 s). 0 = physical at any range.", new AcceptableValueRange<float>(0f, 500f)));
        PhysNudgeOffScreen = Config.Bind(Perf, "Physical hit nudge off screen", false, "Off: a bot hit while off screen (behind you, out of view) gets the spring nudge, which nobody sees anyway. On: physical wherever he is.");

        AmosCoop = Config.Bind("8. Euphoria Style Physics", "Work with Euphoria Style Physics", true, "When amos's Euphoria Style Physics is installed, each bot belongs to one mod at a time. Ours: living stumbles, balance, falls and get-ups. Its: deaths and corpses (while its Dying with muscles is on), hit flinch (our hit nudge is off while its flinch is on), stagger and explosions. Its hit handlers leave a bot ours has alone, and ours leaves a bot its knockdown has. Off: both act on every bot (they fight). No effect without it.");
        AmosTurnOff = Config.Bind("8. Euphoria Style Physics", "Turn off its stumble and knockdowns", true, "At game start, its 5. Stumble and 6. Knockdowns main switches are turned off: they do what ours does. Off: they are left as you set them (only a warning in the log), and a bot they take is left to it.");

        // ---- Debug: everything below is Advanced (only shown with "Show advanced settings" in the config manager)
        const string D = "9. Debug (advanced)";
        BallisticShove = Config.Bind(D, "Ballistic bullet shove", true, Adv("The shove comes from the round itself: bullet mass x velocity (the gun's barrel/mod speed factor, slowed over range), how much of it stays in the body (plate-stopped = all, over-penetrating high-pen rounds = less) and its damage. Off = the old shove from the stumble score only. The breakdown is written with 'Log stumble scores'.", 12));
        Impulse = Config.Bind(D, "Bullet shove", 1f, Adv("Overall multiplier on the bullet's physical impulse on the body (1 = the engine's default shot tuning).", 11, new AcceptableValueRange<float>(0f, 3f)));
        LogScores = Config.Bind(D, "Log stumble scores", true, Adv("Write each hit's stumble score breakdown to the BepInEx log.", 6));
        VerboseLog = Config.Bind(D, "Verbose log", false, Adv("Extra Euphoria log lines: mover re-registration, pose hand-offs, freeze / unfreeze, aim-back details.", 5));

        string dir = Path.GetDirectoryName(Info.Location);
        // The engine is a native dll beside us — it isn't on the game's
        // search path, so load it by full path before any P/Invoke call.
        if (LoadLibrary(Path.Combine(dir, "delirium_physics.dll")) == System.IntPtr.Zero)
        {
            Logger.LogError($"delirium_physics.dll failed to load from {dir} (win32 error {Marshal.GetLastWin32Error()})");
            return;
        }
        try
        {
            NmBody.Init(dir);
            NmBody.ApplyBalance(dir, Tough.Value);
            Tough.SettingChanged += (_, _) => NmBody.ApplyBalance(dir, Tough.Value);
        }
        catch (System.Exception e)
        {
            Logger.LogError($"Delirium init failed: {e.Message}");
            return;
        }
        try
        {
            GetUpLibrary.Load(Path.Combine(dir, "getups.bin"));
            Logger.LogInfo($"Get-up clips loaded: {GetUpLibrary.Clips.Count}");
        }
        catch (System.Exception e) { Logger.LogError($"getups.bin: {e.Message}"); }
        Logger.LogInfo("Delirium engine loaded — Euphoria reactions armed.");
        new HitReactionPatch().Enable();
        try { AmosBridge.Init(AmosTurnOff); } catch (System.Exception e) { Logger.LogError($"Euphoria Style Physics bridge: {e.Message}"); }
        try { FireBlock.Install(); } catch (System.Exception e) { Logger.LogError($"fire block: {e.Message}"); }
        try { MedsGuard.Install(); } catch (System.Exception e) { Logger.LogError($"meds guard: {e.Message}"); }
        try { EuphoriaHold.Install(); } catch (System.Exception e) { Logger.LogError($"hold: {e.Message}"); }
        try { Patches.DyingLootBlock.Install(); } catch (System.Exception e) { Logger.LogError($"loot block: {e.Message}"); }
    }
}

partial class EuphoriaPlugin
{
    // the physics body's two legs never collide with each other: a stumbling bot's feet cross freely
    private void ApplyLegCollision()
    {
        try { Delirium.Native.nm_set_leg_self_collision(0); }
        catch (System.Exception e) { Logger.LogError($"legs pass through: needs the matching delirium_physics.dll ({e.GetType().Name})"); }
    }
}

/// <summary>Read by BepInEx.ConfigurationManager by field name: IsAdvanced hides the entry unless "Show advanced settings" is ticked.</summary>
internal sealed class ConfigurationManagerAttributes
{
    public bool? IsAdvanced;
    public int? Order;
}

/// <summary>How hard bots are to knock down: one stagger style, the odds moved (presets/balance_*.txt).</summary>
public enum Toughness { Tough, Natural, Easy, Custom }
