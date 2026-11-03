using System.Reflection;
using EFT;
using TrueView.Systems;
using SPT.Reflection.Patching;

namespace TrueView.Patches;

/// <summary>
/// Player.VisualPass places the gun's root bone and IK-solves both arms onto it.
/// Its end is the first point in the frame where the fireport sits where the
/// shot will leave from, so the barrel aim is measured there.
/// </summary>
internal class Patch_VisualPass : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(Player).GetMethod("VisualPass", BindingFlags.Instance | BindingFlags.Public);
    }

    [PatchPostfix]
    private static void Postfix(Player __instance)
    {
        if (__instance == null || !__instance.IsYourPlayer)
            return;
        BarrelAimSystem.Measure(__instance);
        JitterProbe.AfterIk(__instance);
    }
}
