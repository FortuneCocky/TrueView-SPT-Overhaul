namespace EuphoriaReaction;

/// <summary>The pose a bot is in when the doll takes him, and the one he recovers into.</summary>
internal enum StartPose { Stand, Crouch, DeepCrouch, Prone }

/// <summary>How long a stumble may last (Stumble length setting).</summary>
public enum StumbleLength { Short, Natural, Long, Custom }

/// <summary>Where a reaction is: None = the animation has him; then stumble, fall (only if he can't hold it), recover.</summary>
internal enum Phase { None, Stumble, Fall, Recover }

/// <summary>What the stumble clock says this tick.</summary>
internal enum StumbleExit { Stay, Recover, Fall, TooLong }

/// <summary>
/// The reaction's phase rules, kept free of Unity so they can be checked without the game
/// (tests/PhaseRulesTests): how long a stumble may last, when it ends and how, when a fall is over,
/// the pose he starts in and the one he recovers into.
/// </summary>
internal static class PhaseRules
{
    public const float FallMinDown = 0.3f;     // s on the ground before he may start getting up
    public const float FallStillHold = 0.3f;   // s his body must be still
    public const float FallMaxDown = 3f;       // s on the ground: he gets up even if never quite still
    public const float DeepCrouchLevel = 0.25f; // pose level at or below which a crouch is a deep crouch
    public const float StillEnergy = 8f;       // doll kinetic energy below which he counts as still
    private const float MinWindow = 0.2f;      // s a custom max stays above its min

    /// The least and most time a stumble lasts. The max is a safety net, long enough for the physics to decide:
    /// offline, a shot leg on Tough folds about 4.7 s after the hit.
    public static (float min, float max) StumbleWindow(StumbleLength len, float customMin, float customMax)
    {
        switch (len)
        {
            case StumbleLength.Short: return (0.4f, 3.0f);
            case StumbleLength.Long: return (1.5f, 8.0f);
            case StumbleLength.Custom: return (customMin, customMax < customMin + MinWindow ? customMin + MinWindow : customMax);
            default: return (0.8f, 6.0f);
        }
    }

    /// The stumble clock: going over (the physics' call) always wins; steady past the min recovers; still on his
    /// feet but wobbling at the max, he's eased back into his stance. The clock never puts him down.
    public static StumbleExit StumbleDecision(float elapsed, float min, float max, bool steady, bool goingDown)
    {
        if (goingDown) return StumbleExit.Fall;
        if (steady && elapsed >= min) return StumbleExit.Recover;
        return elapsed >= max ? StumbleExit.TooLong : StumbleExit.Stay;
    }

    /// Is the fall over: down a moment and still, or down too long.
    public static bool FallDone(float sinceLanded, float stillFor)
        => (sinceLanded >= FallMinDown && stillFor >= FallStillHold) || sinceLanded >= FallMaxDown;

    /// The game's pose as a start pose.
    public static StartPose PoseFromGame(bool prone, float level, float standLevel)
    {
        if (prone) return StartPose.Prone;
        if (level >= standLevel) return StartPose.Stand;
        return level <= DeepCrouchLevel ? StartPose.DeepCrouch : StartPose.Crouch;
    }

    /// The pose he recovers into: his start pose when healthy, one lower when hurt, prone when badly hurt.
    /// Ended low (a crouch stumble, or caught himself at crouch height): never back up to standing.
    public static StartPose RecoverTarget(StartPose start, float hp, float hurtBelow, float proneBelow, bool endedLow)
    {
        StartPose to;
        if (hp <= proneBelow) to = StartPose.Prone;
        else if (hp <= hurtBelow) to = start == StartPose.Stand ? StartPose.Crouch : start == StartPose.Crouch ? StartPose.DeepCrouch : start;
        else to = start;
        return endedLow && to == StartPose.Stand ? StartPose.Crouch : to;
    }

    /// The game's pose level for a stance (prone has none).
    public static float StanceLevel(StartPose p) => p == StartPose.Stand ? 1f : p == StartPose.Crouch ? 0.5f : 0f;

    public static string Name(Phase p) => p.ToString().ToLowerInvariant();
}
