using HardcoreDMG.Models;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Bot;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using System.Reflection;
using System.Text.Json;

namespace HardcoreDMG.Core;

/// <summary>
/// Server mod that reshapes bot and player health at server startup. Modifies
/// the IN-MEMORY database tables only — never touches files on disk.
///
/// Every bot role belongs to a group. For each role, in one pass:
///  1. If its group uses the body model, its HP is rebuilt from HardcoreDMG's
///     base body and the role's build.
///  2. Its HP is scaled by its group's toughness tier.
/// The player's HP is scaled by PlayerVitality.
/// </summary>
[Injectable(TypePriority = 999)]
public class HealthModifier : IOnLoad
{
    private const float MinScale = 0.25f, MaxScale = 4f;

    private static readonly string[] PartNames =
        { "Head", "Chest", "Stomach", "LeftArm", "RightArm", "LeftLeg", "RightLeg" };

    private readonly ISptLogger<HealthModifier> _logger;
    private readonly ModHelper _modHelper;
    private readonly BotTable _botTable;
    private readonly GlobalTable _globalTable;

    public HealthModifier(
        ISptLogger<HealthModifier> logger,
        ModHelper modHelper,
        BotTable botTable,
        GlobalTable globalTable)
    {
        _logger = logger;
        _modHelper = modHelper;
        _botTable = botTable;
        _globalTable = globalTable;
    }

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        ModConfig config = GetConfig();
        if (!config.Enabled)
        {
            _logger.Info("HardcoreDMG: Disabled in config, skipping.");
            return;
        }

        _logger.Info("HardcoreDMG: Shaping bot health...");

        int rebuilt = 0, scaled = 0;
        foreach (var (role, botType) in _botTable.Types)
        {
            var bodyPartSets = botType?.BotHealth?.BodyParts;
            if (bodyPartSets == null) continue;

            BotGroup group = GroupOf(role);

            if (UsesBodyModel(config.BodyModel, group))
            {
                var profile = Profile(BuildFor(role, group));
                foreach (var set in bodyPartSets)
                    foreach (var (part, range) in profile)
                        SetPart(set, part, range.Min, range.Max);
                rebuilt++;
            }

            float toughness = ToughnessFor(config.Toughness, group);
            if (toughness != 1f)
            {
                foreach (var set in bodyPartSets)
                    foreach (string part in PartNames)
                        ScaleMinMax(GetPart(set, part), toughness);
                scaled++;
            }
        }

        _logger.Info($"  Body model applied to {rebuilt} roles; toughness scaled {scaled} roles " +
                     $"(regulars x{Clamp(config.Toughness.Regulars)}, elites x{Clamp(config.Toughness.Elites)}, bosses x{Clamp(config.Toughness.Bosses)})");

        float vitality = Clamp(config.PlayerVitality);
        if (vitality != 1f)
            ApplyPlayerVitality(vitality);

        _logger.Info("HardcoreDMG: Done.");
    }

    // ==================== Groups and tiers ====================

    private enum BotGroup { Regular, Guard, Raider, Cultist, Boss, Infected }

    private static BotGroup GroupOf(string role) =>
        RoleGroups.TryGetValue(role, out BotGroup group) ? group : BotGroup.Regular;

    private static bool UsesBodyModel(BodyModelGroups groups, BotGroup group) => group switch
    {
        BotGroup.Guard => groups.Guards,
        BotGroup.Raider => groups.Raiders,
        BotGroup.Cultist => groups.Cultists,
        BotGroup.Boss => groups.Bosses,
        BotGroup.Infected => groups.Infected,
        _ => groups.Regulars,
    };

    // Guards, raiders, rogues and cultist warriors share the elite tier; the
    // infected keep their body model HP untouched.
    private static float ToughnessFor(ToughnessTiers tiers, BotGroup group) => group switch
    {
        BotGroup.Regular => Clamp(tiers.Regulars),
        BotGroup.Guard or BotGroup.Raider or BotGroup.Cultist => Clamp(tiers.Elites),
        BotGroup.Boss => Clamp(tiers.Bosses),
        _ => 1f,
    };

    private static float Clamp(float scale) => Math.Clamp(scale, MinScale, MaxScale);

    // Roles not listed here count as regulars.
    private static readonly Dictionary<string, BotGroup> RoleGroups = BuildRoleGroups();

    private static Dictionary<string, BotGroup> BuildRoleGroups()
    {
        var map = new Dictionary<string, BotGroup>();
        void Add(BotGroup group, params string[] roles)
        {
            foreach (string role in roles) map[role] = group;
        }

        Add(BotGroup.Regular, "usec", "bear", "pmcusec", "pmcbear", "assault", "marksman", "cursedassault");
        Add(BotGroup.Guard,
            "followerbully", "followerkojaniy", "followersanitar", "followertagilla", "followerzryachiy",
            "followergluharassault", "followergluharscout", "followergluharsecurity", "followergluharsnipe",
            "followerboar", "followerboarclose1", "followerboarclose2", "bossboarsniper",
            "followerkolontaysecurity", "followerkolontayassault");
        Add(BotGroup.Raider, "pmcbot", "exusec");
        Add(BotGroup.Cultist, "sectantwarrior");
        Add(BotGroup.Boss,
            "bosssanitar", "bosskojaniy", "bosskilla", "bosskillaagro", "bossgluhar", "bossbully",
            "bosstagilla", "bosstagillaagro", "tagillahelperagro", "bosszryachiy", "bossboar",
            "bosskolontay", "bosspartisan", "bossknight", "followerbigpipe", "followerbirdeye",
            "sectantpriest");
        Add(BotGroup.Infected,
            "infectedassault", "infectedpmc", "infectedcivil", "infectedlaborant", "infectedtagilla");
        return map;
    }

    // ==================== Body model ====================
    // HardcoreDMG's own body model: a small head, a large torso pool and sturdy
    // legs. Each role gets a build that scales this base body; MinScale/MaxScale
    // set the range SPT rolls each bot's HP from.

    private const double BaseHead = 22, BaseChest = 115, BaseStomach = 105, BaseArm = 58, BaseLeg = 68;

    private readonly record struct Build(
        double MinScale, double MaxScale,
        double Head = 1, double Torso = 1, double Arms = 1, double Legs = 1);

    private static readonly Build Standard = new(1, 1);

    private static Build BuildFor(string role, BotGroup group)
    {
        if (NamedBuilds.TryGetValue(role, out Build named)) return named;
        return group switch
        {
            BotGroup.Cultist => new Build(0.92, 1.10),
            // Infected shrug off torso and limb hits but die to headshots like anyone else.
            BotGroup.Infected => new Build(1.00, 1.25, Torso: 2.4, Arms: 2.1, Legs: 2.0),
            _ => Standard,
        };
    }

    // Named roles with their own builds, shaped by how each one fights.
    private static readonly Dictionary<string, Build> NamedBuilds = new()
    {
        ["bossknight"] = new(1.08, 1.08, Torso: 1.04, Arms: 1.05),
        ["followerbigpipe"] = new(1.06, 1.06, Legs: 1.05),
        ["followerbirdeye"] = new(1.02, 1.02),
        ["bosskilla"] = new(1.10, 1.10, Arms: 1.08, Legs: 1.12),
        ["bosskillaagro"] = new(1.10, 1.10, Arms: 1.08, Legs: 1.12),
        ["bosstagilla"] = new(1.12, 1.12, Torso: 1.05),
        ["bosstagillaagro"] = new(1.12, 1.12, Torso: 1.05),
        ["tagillahelperagro"] = new(1.12, 1.12, Torso: 1.05),
        ["bosssanitar"] = new(1.06, 1.06, Head: 1.05),
        ["bossbully"] = new(1.02, 1.02, Torso: 1.05, Arms: 0.95),
        ["sectantpriest"] = new(1.00, 1.18),
        ["followersanitar"] = new(1.03, 1.03),
        ["followerbully"] = new(1.02, 1.02),
        ["exusec"] = new(1.00, 1.08),
    };

    private static Dictionary<string, (double Min, double Max)> Profile(Build b)
    {
        (double Min, double Max) Range(double baseHp, double partScale) =>
            (Math.Round(baseHp * partScale * b.MinScale), Math.Round(baseHp * partScale * b.MaxScale));

        return new()
        {
            ["Head"] = Range(BaseHead, b.Head),
            ["Chest"] = Range(BaseChest, b.Torso),
            ["Stomach"] = Range(BaseStomach, b.Torso),
            ["LeftArm"] = Range(BaseArm, b.Arms),
            ["RightArm"] = Range(BaseArm, b.Arms),
            ["LeftLeg"] = Range(BaseLeg, b.Legs),
            ["RightLeg"] = Range(BaseLeg, b.Legs)
        };
    }

    // ==================== Table access ====================

    private static object? GetPart(object bodyPartSet, string name) =>
        bodyPartSet.GetType().GetProperty(name)?.GetValue(bodyPartSet);

    private static void SetPart(object bodyPartSet, string name, double min, double max)
    {
        object? part = GetPart(bodyPartSet, name);
        if (part == null) return;
        var type = part.GetType();
        type.GetProperty("Min")?.SetValue(part, min);
        type.GetProperty("Max")?.SetValue(part, max);
    }

    private static void ScaleMinMax(object? minMaxObj, float scale)
    {
        if (minMaxObj == null) return;
        var type = minMaxObj.GetType();
        var minProp = type.GetProperty("Min");
        var maxProp = type.GetProperty("Max");
        if (minProp?.GetValue(minMaxObj) is double minVal)
            minProp.SetValue(minMaxObj, Math.Round(minVal * scale));
        if (maxProp?.GetValue(minMaxObj) is double maxVal)
            maxProp.SetValue(minMaxObj, Math.Round(maxVal * scale));
    }

    private void ApplyPlayerVitality(float vitality)
    {
        var parts = _globalTable.Configuration?.Health?.ProfileHealthSettings?.BodyPartsSettings;
        if (parts == null)
        {
            _logger.Warning("HardcoreDMG: Could not find player health settings in global config.");
            return;
        }

        foreach (BodyPartsSetting? setting in new[]
                 { parts.Head, parts.Chest, parts.Stomach, parts.LeftArm, parts.RightArm, parts.LeftLeg, parts.RightLeg })
        {
            if (setting == null) continue;
            setting.Maximum = Math.Round(setting.Maximum * vitality);
            setting.Default = Math.Round(setting.Default * vitality);
        }

        _logger.Info($"  Player vitality x{vitality}");
    }

    // ==================== Config ====================

    private ModConfig GetConfig()
    {
        string modPath = _modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        string configPath = System.IO.Path.Combine(modPath, "config.jsonc");
        if (!File.Exists(configPath))
        {
            _logger.Warning($"HardcoreDMG: Config not found at {configPath}, using defaults.");
            return new ModConfig();
        }

        string json = File.ReadAllText(configPath);
        json = StripComments(json);
        return JsonSerializer.Deserialize<ModConfig>(json) ?? new ModConfig();
    }

    private static string StripComments(string json)
    {
        var lines = json.Split('\n');
        var result = new List<string>();
        bool inBlockComment = false;
        foreach (string line in lines)
        {
            string trimmed = line;
            if (inBlockComment)
            {
                int end = trimmed.IndexOf("*/");
                if (end >= 0)
                {
                    inBlockComment = false;
                    trimmed = trimmed.Substring(end + 2);
                }
                else continue;
            }

            int blockStart = trimmed.IndexOf("/*");
            int lineComment = trimmed.IndexOf("//");
            if (blockStart >= 0 && (lineComment < 0 || blockStart < lineComment))
            {
                int blockEnd = trimmed.IndexOf("*/", blockStart + 2);
                if (blockEnd >= 0)
                    trimmed = trimmed.Substring(0, blockStart) + trimmed.Substring(blockEnd + 2);
                else
                {
                    trimmed = trimmed.Substring(0, blockStart);
                    inBlockComment = true;
                }
            }
            else if (lineComment >= 0)
            {
                trimmed = trimmed.Substring(0, lineComment);
            }
            result.Add(trimmed);
        }
        return string.Join('\n', result);
    }
}
