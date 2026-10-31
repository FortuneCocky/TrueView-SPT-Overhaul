using System.Reflection;
using HarmonyLib;
using TrueView.Core;
using TrueView.Systems;
using SPT.Reflection.Patching;

namespace TrueView.Patches;

/// <summary>
/// The game has accepted a flyby (once per round, a couple of frames after PlaySound) and starts
/// its vignette; coef is how close the round came, 0 at 10 m .. 1 past your ear. FlybySystem
/// plays the whiz, queues the flinch and feeds adrenaline.
/// </summary>
internal class Patch_FlyingBulletVignette : ModulePatch
{
	protected override MethodBase GetTargetMethod()
	{
		return AccessTools.Method(typeof(FlyingBulletSoundPlayer), nameof(FlyingBulletSoundPlayer.StartVignetteEffect));
	}

	[PatchPostfix]
	private static void Postfix(float coef)
	{
		if (HandlingState.Enabled)
			FlybySystem.OnFlyby(coef);
	}
}
