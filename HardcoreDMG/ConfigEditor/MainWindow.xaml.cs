using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace HardcoreDMGUI;

public partial class MainWindow : Window
{
    private const string ClientCfgName = "com.devin.hardcoredmg.client.cfg";
    private const string ModelSection = "1. Damage Model";
    private const string PlayerDamageSection = "1. Damage Model (Player)";
    private const string ReactionSection = "2. Wound Reactions (Player)";
    private const string BulletSection = "3. Bullet Physics";
    private const string CrippleSection = "5. Leg Cripple";
    private const string ShotgunSection = "6. Shotguns";

    private const string PresetFormat = "HardcoreDMG preset";
    private const string SharePrefix = "HDMG1:";

    private string _configPath = "";
    private string _clientCfgPath = @"J:\Games\SPT 4.1.6\BepInEx\config\" + ClientCfgName;

    /// <summary>
    /// A setting the editor manages, under its preset id. Server settings live
    /// in config.jsonc and their id is their JSON path; client settings carry
    /// their BepInEx section and key.
    /// </summary>
    private sealed record BoolSetting(string Id, CheckBox Box, bool Default, string? Section = null, string? Key = null);
    private sealed record FloatSetting(string Id, ScaleRow Row, float Default, string? Section = null, string? Key = null);

    private readonly List<BoolSetting> _bools = new();
    private readonly List<FloatSetting> _floats = new();

    private readonly ScaleRow _toughRegulars, _toughElites, _toughBosses, _playerVitality;
    private readonly ScaleRow _pointBlankRange, _pointBlankBonus, _effectiveRange, _spreadFalloff, _minRangeScale, _clusterBonus;

    private readonly List<Preset> _builtInPresets;

    public MainWindow()
    {
        InitializeComponent();

        // Server defaults match HardcoreDMG/config.jsonc and Models/ModConfig.cs.
        AddBool("Enabled", ModEnabled, true,
            "Turns the server side of HardcoreDMG on or off: the body model, toughness and player vitality. Off = vanilla HP for everyone.");
        AddBool("BodyModel.Regulars", BodyRegulars, true,
            "Scavs, PMCs, snipers and any role not listed elsewhere get HardcoreDMG's body. Off = vanilla HP.");
        AddBool("BodyModel.Guards", BodyGuards, true,
            "Boss guards get HardcoreDMG's body. Off = vanilla HP.");
        AddBool("BodyModel.Raiders", BodyRaiders, true,
            "Raiders and rogues get HardcoreDMG's body. Off = vanilla HP.");
        AddBool("BodyModel.Cultists", BodyCultists, true,
            "Cultist warriors get HardcoreDMG's body. The priest counts as a boss.");
        AddBool("BodyModel.Bosses", BodyBosses, true,
            "Bosses, the Goons and the cultist priest get HardcoreDMG's body, with their own builds (Killa, Tagilla and so on). Off = vanilla HP.");
        AddBool("BodyModel.Infected", BodyInfected, false,
            "Infected get a tanky torso and limbs (about 275-345 chest HP) but a normal head, so headshots still drop them.");
        _toughRegulars = AddFloat("Toughness.Regulars", ToughnessRows, "Regulars", 0.25f, 4f, 0.80f,
            "Scales the HP of scavs, PMCs and snipers after the body model. 1.00 = unchanged, 0.80 = 20% less HP.");
        _toughElites = AddFloat("Toughness.Elites", ToughnessRows, "Elites (guards, raiders, cultists)", 0.25f, 4f, 0.90f,
            "Scales the HP of guards, raiders, rogues and cultist warriors after the body model. 1.00 = unchanged.");
        _toughBosses = AddFloat("Toughness.Bosses", ToughnessRows, "Bosses", 0.25f, 4f, 1.20f,
            "Scales the HP of bosses, the Goons and the cultist priest after the body model. 1.00 = unchanged.");
        _playerVitality = AddFloat("PlayerVitality", PlayerRows, "Vitality", 0.25f, 4f, 1.00f,
            "Scales your max HP on every body part. 1.00 = unchanged, 1.30 = 30% more HP.");

        // Client defaults match HardcoreDMG-Client/HardcoreDMGPlugin.cs.
        AddBool("Client.DamageModel", DamageModelOn, true,
            "Turns all of HardcoreDMG's client-side damage changes on or off, including the shotgun rework.", ModelSection, "Damage model active");
        AddBool("Client.TorsoZones", TorsoZonesOn, true,
            "Reads which part of the chest or stomach was hit: heart (always blacks the part), spine (takes most of what's left), flanks (glancing) and pelvis (bleeds more).", ModelSection, "Anatomical torso zones");
        AddFloat("Client.DamageScale", DamageRows, "Overall damage", 0.1f, 3f, 1.0f,
            "Scales every bullet, melee and explosion hit on the player and bots. 1.00 = unchanged.", ModelSection, "Overall damage scale");
        AddFloat("Client.HeadshotScale", DamageRows, "Headshot damage", 0.1f, 3f, 1.0f,
            "Extra scale for head hits, on top of the overall damage. 1.00 = unchanged.", ModelSection, "Headshot damage scale");
        AddFloat("Client.ChestScale", DamageRows, "Chest damage", 0.1f, 3f, 1.0f,
            "Scales chest hits, after the torso zone. 1.00 = unchanged.", ModelSection, "Chest damage scale");
        AddFloat("Client.StomachScale", DamageRows, "Stomach damage", 0.1f, 3f, 1.0f,
            "Scales stomach hits, after the torso zone. 1.00 = unchanged.", ModelSection, "Stomach damage scale");
        AddFloat("Client.ArmScale", DamageRows, "Arm damage", 0.1f, 3f, 0.85f,
            "Scales arm hits. Arm wounds are rarely fatal, so the default is a little lower.", ModelSection, "Arm damage scale");
        AddFloat("Client.LegScale", DamageRows, "Leg damage", 0.1f, 3f, 0.9f,
            "Scales leg hits. 1.00 = unchanged.", ModelSection, "Leg damage scale");
        AddFloat("Client.HeavyBleed", DamageRows, "Heavy bleed odds", 0f, 3f, 1.0f,
            "Scales the odds that a hit opens a heavy bleed. 0 = never, 1.00 = unchanged.", ModelSection, "Heavy bleed odds scale");
        AddFloat("Client.LightBleed", DamageRows, "Light bleed odds", 0f, 3f, 1.0f,
            "Scales the odds that a hit opens a light bleed. 0 = never, 1.00 = unchanged.", ModelSection, "Light bleed odds scale");

        AddBool("Client.PlayerDamage", PlayerDamageOn, false,
            "Hits on you use only the settings below and ignore the Damage tab. Bots keep using the Damage tab. Off = you take damage like everyone else.", PlayerDamageSection, "Enable player damage settings");
        AddFloat("Client.PlayerDamageScale", PlayerDamageRows, "Overall damage", 0.1f, 3f, 1.0f,
            "Scales every bullet, melee and explosion hit on you. 1.00 = unchanged.", PlayerDamageSection, "Player overall damage scale");
        AddFloat("Client.PlayerHeadshotScale", PlayerDamageRows, "Headshot damage", 0.1f, 3f, 1.0f,
            "Extra scale for head hits on you, on top of your overall damage.", PlayerDamageSection, "Player headshot damage scale");
        AddFloat("Client.PlayerChestScale", PlayerDamageRows, "Chest damage", 0.1f, 3f, 1.0f,
            "Scales chest hits on you, after the torso zone.", PlayerDamageSection, "Player chest damage scale");
        AddFloat("Client.PlayerStomachScale", PlayerDamageRows, "Stomach damage", 0.1f, 3f, 1.0f,
            "Scales stomach hits on you, after the torso zone.", PlayerDamageSection, "Player stomach damage scale");
        AddFloat("Client.PlayerArmScale", PlayerDamageRows, "Arm damage", 0.1f, 3f, 0.85f,
            "Scales arm hits on you.", PlayerDamageSection, "Player arm damage scale");
        AddFloat("Client.PlayerLegScale", PlayerDamageRows, "Leg damage", 0.1f, 3f, 0.9f,
            "Scales leg hits on you.", PlayerDamageSection, "Player leg damage scale");
        AddFloat("Client.PlayerHeavyBleed", PlayerDamageRows, "Heavy bleed odds", 0f, 3f, 1.0f,
            "Scales the odds that a hit on you opens a heavy bleed. 0 = never.", PlayerDamageSection, "Player heavy bleed odds scale");
        AddFloat("Client.PlayerLightBleed", PlayerDamageRows, "Light bleed odds", 0f, 3f, 1.0f,
            "Scales the odds that a hit on you opens a light bleed. 0 = never.", PlayerDamageSection, "Player light bleed odds scale");

        AddBool("Client.WeaponDrop", WeaponDropOn, false,
            "A bad arm wound can knock the weapon out of your hands. Player only.", ReactionSection, "Weapon drop on arm wounds");
        AddFloat("Client.WeaponDropOdds", WeaponDropRows, "Weapon drop odds (%)", 0f, 100f, 2f,
            "Starting odds in percent. Bigger wounds compared with the arm's HP raise them, and hand or wrist hits count extra.", ReactionSection, "Weapon drop base odds percent");
        AddBool("Client.WoundFall", WoundFallOn, false,
            "A leg hit that leaves your leg below 35% HP can knock you prone. Player only.", ReactionSection, "Fall on leg wounds");
        AddFloat("Client.WoundFallOdds", WoundFallRows, "Fall odds (%)", 0f, 100f, 25f,
            "Starting odds in percent. Lower leg HP and bigger wounds raise them, and thigh hits count extra.", ReactionSection, "Fall base odds percent");

        AddBool("Client.BulletPhysics", BulletPhysics, true,
            "Turns HardcoreDMG's bullet changes on or off: fragmentation, armor penetration and ricochets.", BulletSection, "Bullet physics active");
        AddFloat("Client.Fragmentation", BulletRows, "Fragmentation odds", 0.5f, 3f, 1.5f,
            "Scales how often bullets break apart inside the target. Faster rounds fragment more. 1.00 = unchanged.", BulletSection, "Fragmentation odds scale");
        AddFloat("Client.Penetration", BulletRows, "Penetration odds", 0.5f, 3f, 1.2f,
            "Scales how often bullets get through armor. 1.00 = unchanged.", BulletSection, "Penetration odds scale");
        AddFloat("Client.ArmorEnergyLoss", BulletRows, "Energy lost through armor", 0f, 1f, 0.4f,
            "How much damage a weak round loses when it only just gets through armor. 0 = none, 0.40 = up to 40%, 1 = all of it.", BulletSection, "Energy lost through armor");
        AddFloat("Client.Ricochet", BulletRows, "Ricochet odds", 0.5f, 3f, 1.3f,
            "Scales how often bullets glance off at shallow angles. 1.00 = unchanged.", BulletSection, "Ricochet odds scale");

        AddBool("Client.LegCripple", LegCrippleOn, false,
            "Wounded legs force the actor into crouch, and badly wounded legs into prone. Everything below needs this on.", CrippleSection, "Enable leg cripple");
        AddBool("Client.LegCrippleStayDown", LegCrippleStayDown, true,
            "A crippled bot or player can't get back up past what their legs allow: they stay prone while both legs are below the prone threshold, and crouched while either leg is below the crouch threshold. They get up once healed or on painkillers.", CrippleSection, "Stay down until healed");
        AddBool("Client.LegCripplePlayer", LegCripplePlayer, false,
            "Leg cripple also limits you. Off = it only affects bots.", CrippleSection, "Enable for player");
        AddBool("Client.LegCrippleBots", LegCrippleBots, true,
            "Leg cripple affects AI bots (PMCs, scavs and so on).", CrippleSection, "Enable for bots");
        AddFloat("Client.CrippleCrouchBelow", CrippleRows, "Crouch below leg HP (%)", 0f, 90f, 20f,
            "When either leg drops below this share of its max HP, or is blacked, the actor is forced into crouch.", CrippleSection, "Crouch below leg HP percent");
        AddFloat("Client.CrippleProneBelow", CrippleRows, "Prone below leg HP (%)", 0f, 90f, 10f,
            "When both legs drop below this share of their max HP, the actor is forced prone. A blacked leg always counts as below it.", CrippleSection, "Prone below leg HP percent");

        AddBool("Client.Shotguns", ShotgunsOn, true,
            "Reworks buckshot and slug damage: pellet energy, pattern spread with range, pellet count and armor. Needs the damage model active.", ShotgunSection, "Shotgun rework active");
        AddFloat("Client.ShotgunEnergy", ShotgunPelletRows, "Pellet energy model", 0f, 1f, 1.0f,
            "How far pellet and slug damage follows kinetic energy (speed squared) instead of the game's near-linear speed loss. 0 = vanilla, 1 = full energy model.", ShotgunSection, "Pellet energy model");
        _clusterBonus = AddFloat("Client.ShotgunCluster", ShotgunPelletRows, "Pellet cluster bonus", 0f, 1f, 0.3f,
            "Extra damage as more pellets from one shell hit the same target. The last pellet of a full hit gets the whole bonus, so a full hit averages half of it.", ShotgunSection, "Pellet cluster bonus");
        _pointBlankRange = AddFloat("Client.ShotgunPointBlankRange", ShotgunRangeRows, "Point blank range (m)", 0f, 20f, 8f,
            "Up to this distance the buckshot pattern is still one tight mass and gets the full point blank bonus.", ShotgunSection, "Point blank range meters");
        _pointBlankBonus = AddFloat("Client.ShotgunPointBlankBonus", ShotgunRangeRows, "Point blank bonus", 1f, 2f, 1.25f,
            "Buckshot pellet damage multiplier at point blank. Fades to 1.00 at the effective range.", ShotgunSection, "Point blank damage bonus");
        _effectiveRange = AddFloat("Client.ShotgunEffectiveRange", ShotgunRangeRows, "Effective range (m)", 10f, 60f, 25f,
            "Past this distance the pattern opens up and buckshot damage falls off.", ShotgunSection, "Effective range meters");
        _spreadFalloff = AddFloat("Client.ShotgunFalloff", ShotgunRangeRows, "Spread falloff exponent", 0f, 3f, 1.0f,
            "How fast buckshot falls off past the effective range: damage x (effective / distance) to this power. 0 = no falloff, 2 = inverse square.", ShotgunSection, "Spread falloff exponent");
        _minRangeScale = AddFloat("Client.ShotgunMinRangeScale", ShotgunRangeRows, "Minimum range scale", 0.05f, 1f, 0.3f,
            "The spread falloff never takes buckshot below this share of its damage.", ShotgunSection, "Minimum range scale");
        AddFloat("Client.BuckshotBlunt", ShotgunArmorRows, "Buckshot blunt trauma", 0.5f, 3f, 1.25f,
            "Scales the blunt damage of pellets stopped by armor.", ShotgunSection, "Buckshot blunt trauma scale");
        AddFloat("Client.SlugDamage", ShotgunArmorRows, "Slug damage", 0.5f, 2f, 1.15f,
            "Scales slug hits that get through. A slug is one heavy, wide projectile.", ShotgunSection, "Slug damage scale");
        AddFloat("Client.SlugBlunt", ShotgunArmorRows, "Slug blunt trauma", 0.5f, 3f, 1.5f,
            "Scales the blunt damage of slugs stopped by armor. A stopped slug still slams the plate.", ShotgunSection, "Slug blunt trauma scale");

        foreach (ScaleRow row in new[] { _clusterBonus, _pointBlankRange, _pointBlankBonus, _effectiveRange, _spreadFalloff, _minRangeScale })
            row.Changed += UpdateShotgunPreview;

        // Options that only apply while their switch is on are grayed out otherwise.
        DependsOn(WeaponDropOn, WeaponDropRows);
        DependsOn(WoundFallOn, WoundFallRows);
        DependsOn(LegCrippleOn, CrippleOptions);
        DependsOn(PlayerDamageOn, PlayerDamageOptions);

        _builtInPresets = BuildBuiltInPresets();
    }

    private readonly List<(CheckBox Master, FrameworkElement Options)> _dependents = new();

    private void DependsOn(CheckBox master, FrameworkElement options)
    {
        _dependents.Add((master, options));
        master.Checked += (_, _) => UpdateDependents();
        master.Unchecked += (_, _) => UpdateDependents();
    }

    private void UpdateDependents()
    {
        foreach ((CheckBox master, FrameworkElement options) in _dependents)
        {
            bool on = master.IsChecked == true;
            options.IsEnabled = on;
            options.Opacity = on ? 1.0 : 0.4;
        }
    }

    private void AddBool(string id, CheckBox box, bool def, string tip, string? section = null, string? key = null)
    {
        SetTip(box, tip);
        _bools.Add(new BoolSetting(id, box, def, section, key));
    }

    private ScaleRow AddFloat(string id, Panel panel, string label, float min, float max, float def, string tip,
                              string? section = null, string? key = null)
    {
        ScaleRow row = ScaleRow.Add(panel, label, min, max, tip);
        _floats.Add(new FloatSetting(id, row, def, section, key));
        return row;
    }

    private static void SetTip(FrameworkElement element, string tip)
    {
        element.ToolTip = new TextBlock { Text = tip, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
        ToolTipService.SetShowDuration(element, 60000);
        ToolTipService.SetInitialShowDelay(element, 300);
        ToolTipService.SetShowOnDisabled(element, true);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshPresetList(null);
        Dispatcher.BeginInvoke(UpdateShotgunPreview);

        // The editor ships inside the server mod folder, so look next to it first.
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "config.jsonc"),
            @"J:\Games\SPT 4.1.6\SPT_Runtime\user\mods\HardcoreDMG\config.jsonc",
            @"J:\Games\1A-IRLHandling\HardcoreDMG\config.jsonc",
        };

        foreach (string path in candidates)
        {
            if (File.Exists(path))
            {
                LoadConfig(path);
                return;
            }
        }

        ResetToDefaults();
        StatusText.Text = "No config found. Click Browse to locate config.jsonc";
    }

    // ==================== Server config ====================

    private void LoadConfig(string path)
    {
        try
        {
            JsonNode? root = JsonNode.Parse(StripComments(File.ReadAllText(path)));
            if (root == null)
            {
                StatusText.Text = "Failed to parse config.";
                return;
            }

            _configPath = path;
            FindClientConfig(path);

            foreach (BoolSetting s in _bools.Where(s => s.Section == null))
                s.Box.IsChecked = ReadBool(Find(root, s.Id), s.Default);
            foreach (FloatSetting s in _floats.Where(s => s.Section == null))
                s.Row.Value = ReadFloat(Find(root, s.Id), s.Default);

            LoadClientConfig();
            StatusText.Text = "Loaded: " + path;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to load config:\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Load failed";
        }
    }

    private static JsonNode? Find(JsonNode root, string path)
    {
        JsonNode? node = root;
        foreach (string part in path.Split('.'))
            node = node?[part];
        return node;
    }

    private void SaveConfig(string path)
    {
        try
        {
            File.WriteAllText(path, BuildServerConfig());
            _configPath = path;
            SaveClientConfig();
            StatusText.Text = "Saved: " + path;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to save config:\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Save failed";
        }
    }

    private string BuildServerConfig() => $@"{{
    // HardcoreDMG server config
    // Reshapes bot and player HP when the server starts. Damage zones, bleeding,
    // weapon drops, falls and shotguns are client-side (HardcoreDMG-Client.dll, F12 in game).
    ""Enabled"": {Bool(ModEnabled)},

    // Body model: rebuilds a group's HP from HardcoreDMG's base body
    // (head 22, chest 115, stomach 105, arms 58, legs 68). Named bosses and
    // guards get their own builds on top (Killa, Tagilla, the Goons, etc.).
    // Off = the group keeps its vanilla HP.
    ""BodyModel"": {{
        ""Regulars"": {Bool(BodyRegulars)},   // scavs, PMCs, snipers, and any role not listed elsewhere
        ""Guards"": {Bool(BodyGuards)},     // boss guards
        ""Raiders"": {Bool(BodyRaiders)},    // raiders and rogues
        ""Cultists"": {Bool(BodyCultists)},   // cultist warriors (the priest counts as a boss)
        ""Bosses"": {Bool(BodyBosses)},     // bosses, the Goons and the cultist priest
        ""Infected"": {Bool(BodyInfected)}   // infected: tanky torso and limbs (about 275-345 chest), normal head
    }},

    // Toughness: scales HP per tier after the body model. 1.00 = unchanged.
    // Range 0.25 - 4.00.
    ""Toughness"": {{
        ""Regulars"": {Num(_toughRegulars.Value)},
        ""Elites"": {Num(_toughElites.Value)},     // guards, raiders, rogues, cultist warriors
        ""Bosses"": {Num(_toughBosses.Value)}
    }},

    // Scales the player's max HP on every body part. 1.00 = unchanged. Range 0.25 - 4.00.
    ""PlayerVitality"": {Num(_playerVitality.Value)}
}}
";

    // ==================== Client (BepInEx) config ====================

    /// <summary>
    /// A server config inside SPT_Runtime\user\mods\HardcoreDMG sits four
    /// folders below the SPT root; use that install's BepInEx config.
    /// </summary>
    private void FindClientConfig(string serverConfigPath)
    {
        DirectoryInfo? dir = new FileInfo(serverConfigPath).Directory;
        for (int i = 0; i < 4 && dir != null; i++) dir = dir.Parent;
        if (dir == null) return;

        string candidate = Path.Combine(dir.FullName, "BepInEx", "config", ClientCfgName);
        if (Directory.Exists(Path.GetDirectoryName(candidate)))
            _clientCfgPath = candidate;
    }

    private void LoadClientConfig()
    {
        string[] lines = Array.Empty<string>();
        try
        {
            if (File.Exists(_clientCfgPath)) lines = File.ReadAllLines(_clientCfgPath);
        }
        catch { }

        foreach (BoolSetting s in _bools.Where(s => s.Section != null))
            s.Box.IsChecked = ReadCfgBool(lines, s.Key!, s.Default);
        foreach (FloatSetting s in _floats.Where(s => s.Section != null))
            s.Row.Value = ReadCfgFloat(lines, s.Key!, s.Default);
        UpdateDependents();
    }

    private void SaveClientConfig()
    {
        try
        {
            string content = File.Exists(_clientCfgPath) ? File.ReadAllText(_clientCfgPath) : "";

            // Drop settings that moved or were removed, so the plugin doesn't
            // carry stale values over the ones saved here.
            content = RemoveCfgSection(content, "2. Wound Reactions");
            content = RemoveCfgSection(content, "4. Stance Lock");
            content = RemoveCfgKey(content, CrippleSection, "Crawl speed");
            content = RemoveCfgKey(content, CrippleSection, "Crouch health threshold");

            foreach (BoolSetting s in _bools.Where(s => s.Section != null))
                content = SetCfgValue(content, s.Section!, s.Key!, Bool(s.Box));
            foreach (FloatSetting s in _floats.Where(s => s.Section != null))
                content = SetCfgValue(content, s.Section!, s.Key!, Num(s.Row.Value));
            File.WriteAllText(_clientCfgPath, content);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to save the client config:\n" + ex.Message, "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string? ReadCfgValue(string[] lines, string key)
    {
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith(key + " =", StringComparison.OrdinalIgnoreCase)) continue;
            string val = trimmed.Substring(trimmed.IndexOf('=') + 1);
            int hash = val.IndexOf('#');
            return (hash >= 0 ? val.Substring(0, hash) : val).Trim();
        }
        return null;
    }

    private static bool ReadCfgBool(string[] lines, string key, bool fallback) =>
        bool.TryParse(ReadCfgValue(lines, key), out bool b) ? b : fallback;

    private static float ReadCfgFloat(string[] lines, string key, float fallback) =>
        float.TryParse(ReadCfgValue(lines, key), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : fallback;

    /// <summary>
    /// Sets "key = value" inside [section], adding the key or the section if
    /// they are missing. Everything else in the file is left as it is.
    /// </summary>
    private static string SetCfgValue(string content, string section, string key, string value)
    {
        string header = "[" + section + "]";
        string keyLine = key + " = " + value;

        int sectionIdx = content.IndexOf(header, StringComparison.OrdinalIgnoreCase);
        if (sectionIdx < 0)
        {
            if (content.Length > 0 && !content.EndsWith("\n")) content += "\n";
            return content + "\n" + header + "\n" + keyLine + "\n";
        }

        int bodyStart = sectionIdx + header.Length;
        int nextSection = content.IndexOf("\n[", bodyStart, StringComparison.Ordinal);
        if (nextSection < 0) nextSection = content.Length;

        int keyIdx = content.IndexOf("\n" + key + " =", bodyStart, StringComparison.OrdinalIgnoreCase);
        if (keyIdx < 0 || keyIdx > nextSection)
        {
            int headerEnd = content.IndexOf('\n', bodyStart);
            if (headerEnd < 0 || headerEnd > nextSection) headerEnd = nextSection;
            return content.Insert(Math.Min(headerEnd + 1, content.Length), keyLine + "\n");
        }

        int lineStart = keyIdx + 1;
        int lineEnd = content.IndexOf('\n', lineStart);
        if (lineEnd < 0) lineEnd = content.Length;
        if (lineEnd > lineStart && content[lineEnd - 1] == '\r') lineEnd--;
        return content.Substring(0, lineStart) + keyLine + content.Substring(lineEnd);
    }

    /// <summary>
    /// Removes [section] and everything up to the next section. The header must
    /// match exactly, so "[2. Wound Reactions]" leaves "[2. Wound Reactions (Player)]" alone.
    /// </summary>
    private static string RemoveCfgSection(string content, string section)
    {
        string header = "[" + section + "]";
        int start = content.IndexOf(header, StringComparison.Ordinal);
        if (start < 0) return content;

        int end = content.IndexOf("\n[", start + header.Length, StringComparison.Ordinal);
        return end < 0 ? content.Substring(0, start) : content.Remove(start, end + 1 - start);
    }

    /// <summary>
    /// Removes the "key = value" line inside [section]. Its comment lines stay
    /// until BepInEx next rewrites the file.
    /// </summary>
    private static string RemoveCfgKey(string content, string section, string key)
    {
        int sectionIdx = content.IndexOf("[" + section + "]", StringComparison.Ordinal);
        if (sectionIdx < 0) return content;

        int nextSection = content.IndexOf("\n[", sectionIdx + 1, StringComparison.Ordinal);
        if (nextSection < 0) nextSection = content.Length;

        int keyIdx = content.IndexOf("\n" + key + " =", sectionIdx, StringComparison.OrdinalIgnoreCase);
        if (keyIdx < 0 || keyIdx > nextSection) return content;

        int lineEnd = content.IndexOf('\n', keyIdx + 1);
        return lineEnd < 0 ? content.Substring(0, keyIdx) : content.Remove(keyIdx, lineEnd - keyIdx);
    }

    // ==================== Shotguns ====================

    /// <summary>
    /// Shows the buckshot range scale at a few distances. Mirrors
    /// ShotgunModel.RangeScale in HardcoreDMG-Client.
    /// </summary>
    private void UpdateShotgunPreview()
    {
        float pointBlank = _pointBlankRange.Value;
        float effective = Math.Max(_effectiveRange.Value, pointBlank + 0.1f);
        float bonus = _pointBlankBonus.Value;

        float RangeScale(float d)
        {
            if (d <= pointBlank) return bonus;
            if (d <= effective) return bonus + (1f - bonus) * (d - pointBlank) / (effective - pointBlank);
            return Math.Max(MathF.Pow(effective / d, _spreadFalloff.Value), _minRangeScale.Value);
        }

        int[] distances = { 5, 10, 15, 25, 35, 50, 75 };
        ShotgunPreview.Text = "Buckshot range scale:  " +
                              string.Join("   ", distances.Select(d => $"{d} m ×{RangeScale(d):F2}")) +
                              $"\nA shell that lands every pellet on one target averages ×{1f + _clusterBonus.Value / 2f:F2} from the cluster bonus. " +
                              "Pellet energy loss comes on top and depends on the round's speed.";
    }

    // ==================== Presets ====================

    private sealed class Preset
    {
        public string Name = "";
        public string Description = "";
        public JsonObject Settings = new();
        public bool BuiltIn;
        public string? FilePath;

        public override string ToString() => BuiltIn ? Name + "   (built-in)" : Name;
    }

    private static string PresetsDir => Path.Combine(AppContext.BaseDirectory, "presets");

    private JsonObject DefaultSettings()
    {
        var settings = new JsonObject();
        foreach (BoolSetting s in _bools) settings[s.Id] = s.Default;
        foreach (FloatSetting s in _floats) settings[s.Id] = Math.Round((double)s.Default, 2);
        return settings;
    }

    private JsonObject CurrentSettings()
    {
        var settings = new JsonObject();
        foreach (BoolSetting s in _bools) settings[s.Id] = s.Box.IsChecked == true;
        foreach (FloatSetting s in _floats) settings[s.Id] = Math.Round((double)s.Row.Value, 2);
        return settings;
    }

    /// <summary>
    /// Fills the editor from a preset. Settings the preset leaves out keep their
    /// current value; unknown ids are ignored and values are clamped.
    /// </summary>
    private int ApplySettings(JsonObject settings)
    {
        int applied = 0;
        foreach (BoolSetting s in _bools)
        {
            if (settings[s.Id] is not JsonNode node) continue;
            s.Box.IsChecked = ReadBool(node, s.Box.IsChecked == true);
            applied++;
        }
        foreach (FloatSetting s in _floats)
        {
            if (settings[s.Id] is not JsonNode node) continue;
            s.Row.Value = ReadFloat(node, s.Row.Value);
            applied++;
        }
        return applied;
    }

    private List<Preset> BuildBuiltInPresets() => new()
    {
        BuiltIn("HardcoreDMG default", "The mod's own defaults: lethal but fair, with the shotgun rework on."),
        BuiltIn("Lifelike", "Closer to real wounding: deadlier heads and heavy bleeds, shotguns that hit harder up close and fade faster at range, slugs that hurt through armor.",
            ("Client.DamageScale", 1.1), ("Client.HeadshotScale", 1.4), ("Client.HeavyBleed", 1.3),
            ("Client.WeaponDrop", true), ("Client.LegCripple", true), ("Client.LegCripplePlayer", true), ("Client.WoundFall", true),
            ("Client.ShotgunPointBlankBonus", 1.35), ("Client.ShotgunCluster", 0.4), ("Client.ShotgunFalloff", 1.5),
            ("Client.SlugDamage", 1.25), ("Client.SlugBlunt", 1.75)),
        BuiltIn("Hardcore", "Everyone dies fast, you included: more damage, more bleeding, softer bots and less player HP.",
            ("Client.DamageScale", 1.3), ("Client.HeadshotScale", 1.6), ("Client.HeavyBleed", 1.6), ("Client.LightBleed", 1.3),
            ("Toughness.Regulars", 0.7), ("Toughness.Elites", 0.8), ("Toughness.Bosses", 1.1), ("PlayerVitality", 0.85),
            ("Client.WeaponDrop", true), ("Client.WeaponDropOdds", 4), ("Client.LegCripple", true), ("Client.LegCripplePlayer", true), ("Client.WoundFall", true),
            ("Client.ShotgunPointBlankBonus", 1.4), ("Client.ShotgunCluster", 0.45)),
        BuiltIn("Forgiving", "More room for mistakes: more player HP, less bleeding, tougher bots and milder shotguns.",
            ("Client.DamageScale", 0.85), ("Client.HeadshotScale", 0.9), ("Client.HeavyBleed", 0.7), ("Client.LightBleed", 0.8),
            ("Toughness.Regulars", 1.0), ("Toughness.Elites", 1.0), ("Toughness.Bosses", 1.3), ("PlayerVitality", 1.3),
            ("Client.WoundFallOdds", 15), ("Client.ShotgunPointBlankBonus", 1.1), ("Client.ShotgunCluster", 0.15)),
        BuiltIn("Vanilla shotguns", "HardcoreDMG defaults with the shotgun rework turned off.",
            ("Client.Shotguns", false)),
    };

    private Preset BuiltIn(string name, string description, params (string Id, object Value)[] overrides)
    {
        JsonObject settings = DefaultSettings();
        foreach ((string id, object value) in overrides)
        {
            if (!settings.ContainsKey(id)) throw new ArgumentException($"Built-in preset '{name}' names an unknown setting: {id}");
            settings[id] = value is bool b ? JsonValue.Create(b) : JsonValue.Create(Convert.ToDouble(value, CultureInfo.InvariantCulture));
        }
        return new Preset { Name = name, Description = description, Settings = settings, BuiltIn = true };
    }

    private Preset? SelectedPreset => PresetList.SelectedItem as Preset;

    private void RefreshPresetList(string? select)
    {
        var presets = new List<Preset>(_builtInPresets);
        try
        {
            if (Directory.Exists(PresetsDir))
            {
                foreach (string file in Directory.GetFiles(PresetsDir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        Preset p = ParsePreset(File.ReadAllText(file));
                        p.FilePath = file;
                        presets.Add(p);
                    }
                    catch { } // not a preset; leave it alone
                }
            }
        }
        catch { }

        PresetList.ItemsSource = presets;
        PresetList.SelectedItem = presets.FirstOrDefault(p => p.Name == select);
    }

    private void PresetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Preset? p = SelectedPreset;
        PresetDescription.Text = p == null ? "" : string.IsNullOrWhiteSpace(p.Description) ? "(no description)" : p.Description;

        // Saved presets fill the save boxes, ready to rename or re-save.
        if (p is { BuiltIn: false })
        {
            PresetNameBox.Text = p.Name;
            PresetDescriptionBox.Text = p.Description;
        }
    }

    private void RenamePreset_Click(object sender, RoutedEventArgs e)
    {
        if (RequireSelection() is not Preset p) return;
        if (p.BuiltIn || p.FilePath == null)
        {
            MessageBox.Show("Built-in presets can't be renamed. Load it and save it under a new name instead.", "Presets", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string name = PresetNameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show("Type the new name in the Name box.", "Presets", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string newPath = PresetPath(name);
        bool sameFile = string.Equals(Path.GetFullPath(newPath), Path.GetFullPath(p.FilePath), StringComparison.OrdinalIgnoreCase);
        if (!sameFile && NameTaken(name))
        {
            MessageBox.Show("Another preset already has that name. Pick another.", "Presets", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string oldName = p.Name, oldPath = p.FilePath;
        var renamed = new Preset { Name = name, Description = PresetDescriptionBox.Text.Trim(), Settings = p.Settings };
        if (!WritePreset(renamed, newPath)) return;
        try
        {
            if (!sameFile) File.Delete(oldPath);
        }
        catch { }

        RefreshPresetList(name);
        StatusText.Text = oldName == name ? $"Updated the description of '{name}'" : $"Renamed '{oldName}' to '{name}'";
    }

    private Preset? RequireSelection()
    {
        if (SelectedPreset != null) return SelectedPreset;
        MessageBox.Show("Pick a preset in the list first.", "Presets", MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    private void ApplyPreset_Click(object sender, RoutedEventArgs e)
    {
        if (RequireSelection() is not Preset p) return;
        ApplySettings(p.Settings);
        StatusText.Text = $"Loaded preset '{p.Name}' (click Save to write it to the config files)";
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (RequireSelection() is not Preset p) return;
        if (p.BuiltIn || p.FilePath == null)
        {
            MessageBox.Show("Built-in presets can't be deleted.", "Presets", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show($"Delete preset '{p.Name}'?", "Delete preset", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            File.Delete(p.FilePath);
            RefreshPresetList(null);
            StatusText.Text = $"Deleted preset '{p.Name}'";
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to delete the preset:\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        string name = PresetNameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show("Give the preset a name.", "Presets", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_builtInPresets.Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("That name belongs to a built-in preset. Pick another.", "Presets", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var preset = new Preset { Name = name, Description = PresetDescriptionBox.Text.Trim(), Settings = CurrentSettings() };
        string path = PresetPath(name);
        if (File.Exists(path) &&
            MessageBox.Show($"Replace the saved preset '{name}'?", "Save preset", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        if (WritePreset(preset, path))
        {
            RefreshPresetList(name);
            StatusText.Text = $"Saved preset '{name}'";
        }
    }

    private void ExportPreset_Click(object sender, RoutedEventArgs e)
    {
        if (RequireSelection() is not Preset p) return;
        var dlg = new SaveFileDialog
        {
            Filter = "HardcoreDMG preset|*.json|All files|*.*",
            Title = "Export HardcoreDMG preset",
            FileName = SafeFileName(p.Name) + ".json"
        };
        if (dlg.ShowDialog() == true && WritePreset(p, dlg.FileName))
            StatusText.Text = "Exported: " + dlg.FileName;
    }

    private void CopyShareCode_Click(object sender, RoutedEventArgs e)
    {
        if (RequireSelection() is not Preset p) return;
        string code = ToShareCode(p);
        ShareCodeBox.Text = code;
        try
        {
            Clipboard.SetText(code);
            StatusText.Text = $"Share code for '{p.Name}' copied to the clipboard";
        }
        catch
        {
            StatusText.Text = "Couldn't reach the clipboard; copy the code from the import box";
        }
    }

    private void ImportCode_Click(object sender, RoutedEventArgs e)
    {
        string text = ShareCodeBox.Text.Trim();
        if (text.Length == 0)
        {
            MessageBox.Show("Paste a share code or preset JSON into the box first.", "Presets", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            Import(text.StartsWith("{") ? ParsePreset(text) : FromShareCode(text));
        }
        catch (Exception ex)
        {
            MessageBox.Show("That isn't a valid HardcoreDMG preset:\n" + ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "HardcoreDMG preset|*.json|All files|*.*",
            Title = "Import HardcoreDMG preset"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            Import(ParsePreset(File.ReadAllText(dlg.FileName)));
        }
        catch (Exception ex)
        {
            MessageBox.Show("That isn't a valid HardcoreDMG preset:\n" + ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Saves an imported preset next to the user's own, renaming it if the
    /// name is taken, and selects it so it can be applied.
    /// </summary>
    private void Import(Preset preset)
    {
        string baseName = string.IsNullOrWhiteSpace(preset.Name) ? "Imported preset" : preset.Name.Trim();
        string name = baseName;
        for (int i = 2; NameTaken(name); i++) name = $"{baseName} ({i})";
        preset.Name = name;

        if (!WritePreset(preset, PresetPath(name))) return;
        RefreshPresetList(name);
        StatusText.Text = $"Imported preset '{name}'. Click Load to use it";
    }

    private bool NameTaken(string name) =>
        _builtInPresets.Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) || File.Exists(PresetPath(name));

    private static string PresetPath(string name) => Path.Combine(PresetsDir, SafeFileName(name) + ".json");

    private static string SafeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        return sb.ToString().Trim();
    }

    private bool WritePreset(Preset preset, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, PresetJson(preset, indented: true));
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to write the preset:\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private static string PresetJson(Preset preset, bool indented) => new JsonObject
    {
        ["Format"] = PresetFormat,
        ["Version"] = 1,
        ["Name"] = preset.Name,
        ["Description"] = preset.Description,
        ["Settings"] = preset.Settings.DeepClone(),
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = indented });

    private static Preset ParsePreset(string json)
    {
        JsonNode? root = JsonNode.Parse(StripComments(json));
        if (root?["Settings"] is not JsonObject settings)
            throw new InvalidDataException("No Settings block found.");

        return new Preset
        {
            Name = ReadString(root["Name"]) ?? "Imported preset",
            Description = ReadString(root["Description"]) ?? "",
            Settings = (JsonObject)settings.DeepClone(),
        };
    }

    /// <summary>
    /// A share code is the preset's JSON, gzipped and base64'd behind a
    /// version prefix, so it fits in a chat message.
    /// </summary>
    private static string ToShareCode(Preset preset)
    {
        byte[] raw = Encoding.UTF8.GetBytes(PresetJson(preset, indented: false));
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize))
            gz.Write(raw);
        return SharePrefix + Convert.ToBase64String(ms.ToArray());
    }

    private static Preset FromShareCode(string code)
    {
        if (!code.StartsWith(SharePrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Share codes start with " + SharePrefix);

        string base64 = new string(code.Substring(SharePrefix.Length).Where(c => !char.IsWhiteSpace(c)).ToArray());
        using var gz = new GZipStream(new MemoryStream(Convert.FromBase64String(base64)), CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        return ParsePreset(reader.ReadToEnd());
    }

    // ==================== Buttons ====================

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "HardcoreDMG config|config.jsonc|JSON files|*.json;*.jsonc|All files|*.*",
            Title = "Locate HardcoreDMG config.jsonc"
        };
        if (File.Exists(_configPath)) dlg.InitialDirectory = Path.GetDirectoryName(_configPath);
        if (dlg.ShowDialog() == true) LoadConfig(dlg.FileName);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_configPath))
        {
            SaveConfig(_configPath);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "HardcoreDMG config|config.jsonc|JSON files|*.json;*.jsonc|All files|*.*",
            Title = "Save HardcoreDMG config.jsonc",
            FileName = "config.jsonc"
        };
        if (dlg.ShowDialog() == true) SaveConfig(dlg.FileName);
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Reset all values to defaults?", "Confirm Reset",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        ResetToDefaults();
        StatusText.Text = "Reset to defaults (not yet saved)";
    }

    private void ResetToDefaults()
    {
        foreach (BoolSetting s in _bools) s.Box.IsChecked = s.Default;
        foreach (FloatSetting s in _floats) s.Row.Value = s.Default;
        UpdateDependents();
    }

    // ==================== Helpers ====================

    private static bool ReadBool(JsonNode? node, bool fallback)
    {
        try { return node?.GetValue<bool>() ?? fallback; }
        catch { return fallback; }
    }

    private static float ReadFloat(JsonNode? node, float fallback)
    {
        try { return node?.GetValue<float>() ?? fallback; }
        catch { return fallback; }
    }

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static string Bool(CheckBox box) => box.IsChecked == true ? "true" : "false";

    private static string Num(float f) => f.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>
    /// Removes // comments from JSONC, leaving anything inside strings alone.
    /// </summary>
    private static string StripComments(string json)
    {
        var sb = new StringBuilder(json.Length);
        bool inString = false;
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < json.Length) sb.Append(json[++i]);
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"')
            {
                inString = true;
                sb.Append(c);
                continue;
            }
            if (c == '/' && i + 1 < json.Length && json[i + 1] == '/')
            {
                while (i < json.Length && json[i] != '\n') i++;
                if (i < json.Length) sb.Append('\n');
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// One labelled slider with a text box that shows and edits its value.
    /// </summary>
    private sealed class ScaleRow
    {
        private readonly Slider _slider;
        private readonly TextBox _box;

        public event Action? Changed;

        private ScaleRow(Slider slider, TextBox box)
        {
            _slider = slider;
            _box = box;
        }

        public float Value
        {
            get => (float)_slider.Value;
            set
            {
                _slider.Value = Math.Clamp(value, (float)_slider.Minimum, (float)_slider.Maximum);
                _box.Text = Num(Value);
            }
        }

        public static ScaleRow Add(Panel panel, string label, float min, float max, string tip)
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new Label { Content = label };
            var slider = new Slider { Minimum = min, Maximum = max, TickFrequency = (max - min) / 20 };
            var box = new TextBox();
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(box, 2);
            grid.Children.Add(text);
            grid.Children.Add(slider);
            grid.Children.Add(box);
            SetTip(grid, tip);
            panel.Children.Add(grid);

            var row = new ScaleRow(slider, box);
            slider.ValueChanged += (_, _) =>
            {
                box.Text = Num(row.Value);
                row.Changed?.Invoke();
            };
            box.LostFocus += (_, _) => row.CommitText();
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                row.CommitText();
                Keyboard.ClearFocus();
            };
            return row;
        }

        private void CommitText()
        {
            if (float.TryParse(_box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                Value = val;
            else
                _box.Text = Num(Value);
        }
    }
}
