namespace EuphoriaReaction;

/// <summary>Balance and arm tuning for the physics body (read by the tick every frame).</summary>
internal static class Tune
{
    public static readonly float ArmLimit = 1f;          // x all stumble arm swing limits
    public static readonly float GunArm = 0.6f;          // x for the gun arm (right)
    public static readonly float GunArmAim = 1.8f;       // x extra while aiming back
    public static readonly float Upright = 6f;           // stayUpright stiffness (4 crumpled too easily)
    public static readonly float LegStiff = 16f;         // balance leg stiffness
    public static readonly float BalanceAbort = 0.6f;    // balance abort threshold (0.4 gave up on balance too soon)
    public static readonly float WoundCollapse = 1.6f;   // s before a non-leg wound may fold the legs (leg hits: 0.3)
}
