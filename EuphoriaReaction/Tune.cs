namespace EuphoriaReaction;

/// <summary>Arm tuning for the physics body (read by the tick every frame).</summary>
internal static class Tune
{
    public static readonly float ArmLimit = 1f;          // x all stumble arm swing limits
    public static readonly float GunArm = 0.6f;          // x for the gun arm (right)
    public static readonly float GunArmAim = 1.8f;       // x extra while aiming back
}
