using System.Reflection;
using EFT;
using EFT.Ballistics;
using SPT.Reflection.Patching;

namespace EuphoriaReaction.Patches;

/// <summary>
/// Your rounds are traced against the bot you see (NmBodyShotPose). Player.ArmsLateUpdate syncs every collider and
/// traces your shots before this mod's LateUpdate draws the doll, so the hitboxes stood where the animation held
/// him. Around it every busy bot's hitbox bones take the pose he was last drawn in, and get the animation's pose
/// back after (in a finalizer, so a throw can't leave a bot posed).
/// </summary>
public class ShotPose : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(Player).GetMethod("ArmsLateUpdate", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(float) }, null);
    }

    [PatchPrefix]
    private static void Prefix(Player __instance, out bool __state)
    {
        __state = __instance != null && __instance.IsYourPlayer && NmBody.AnyBusy;
        if (__state) NmBody.PoseForShots();
    }

    [PatchFinalizer]
    private static void Finalizer(bool __state)
    {
        if (__state) NmBody.AfterShots(); // only the wrap that posed puts back
    }
}

/// <summary>
/// The rounds still in flight after their first frame (BallisticsCalculator.UpdateShots, the world tick) hit the
/// bot you see too: posed and the colliders synced before, the animation's pose back after (see ShotPose). Only
/// while a round is in flight and a bot is busy.
/// </summary>
public class ShotPoseInFlight : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(BallisticsCalculator).GetMethod("UpdateShots", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(float) }, null);
    }

    [PatchPrefix]
    private static void Prefix(BallisticsCalculator __instance, out bool __state)
    {
        __state = __instance != null && __instance.Shots != null && __instance.Shots.Count > 0 && NmBody.AnyBusy;
        if (!__state) return;
        NmBody.PoseForShots();
        PhysicsExtensions.Simulation.SyncTransforms();
    }

    [PatchFinalizer]
    private static void Finalizer(bool __state)
    {
        if (__state) NmBody.AfterShots();
    }
}
