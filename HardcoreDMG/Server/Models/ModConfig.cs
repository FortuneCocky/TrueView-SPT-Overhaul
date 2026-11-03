namespace HardcoreDMG.Models;

public class ModConfig
{
    public bool Enabled { get; set; } = true;

    // Which bot groups get HardcoreDMG's body model instead of their vanilla HP.
    public BodyModelGroups BodyModel { get; set; } = new();

    // HP scale per toughness tier, applied after the body model (or to vanilla
    // HP when a group's body model is off). 1.0 = unchanged.
    public ToughnessTiers Toughness { get; set; } = new();

    // Scales the player's max HP on every body part. 1.0 = unchanged.
    public float PlayerVitality { get; set; } = 1.0f;
}

public class BodyModelGroups
{
    public bool Regulars { get; set; } = true;
    public bool Guards { get; set; } = true;
    public bool Raiders { get; set; } = true;
    public bool Cultists { get; set; } = true;
    public bool Bosses { get; set; } = true;
    public bool Infected { get; set; } = false;
}

public class ToughnessTiers
{
    public float Regulars { get; set; } = 0.80f;
    public float Elites { get; set; } = 0.90f;
    public float Bosses { get; set; } = 1.20f;
}
