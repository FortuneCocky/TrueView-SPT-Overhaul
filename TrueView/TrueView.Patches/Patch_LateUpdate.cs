using System.Reflection;
using EFT;
using TrueView.Core;
using TrueView.Systems;
using SPT.Reflection.Patching;
using UnityEngine;

namespace TrueView.Patches;

internal class Patch_LateUpdate : ModulePatch
{
	protected override MethodBase GetTargetMethod()
	{
		return typeof(Player).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.Public);
	}

	// The body camera's offset stays on the camera until here, so everything
	// up to now saw the pose the last frame was drawn from.
	[PatchPrefix]
	private static void Prefix(Player __instance)
	{
		if (__instance.IsYourPlayer)
			BodyCamSystem.BeforeGameCamera();
	}

	[PatchPostfix]
	private static void Postfix(Player __instance)
	{
		HandlingManager.Tick(__instance, Time.deltaTime);
	}
}
