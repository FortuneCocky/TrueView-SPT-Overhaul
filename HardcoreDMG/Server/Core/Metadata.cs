using SPTarkov.Server.Core.Models.Spt.Mod;

namespace HardcoreDMG.Core;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.devin.hardcoredmg";
    public string Name { get; init; } = "HardcoreDMG";
    public string Author { get; init; } = "Devin";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.0.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.6");
    public bool HasPrepatcher { get; init; } = false;
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
}