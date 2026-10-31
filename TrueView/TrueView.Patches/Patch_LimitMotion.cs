using System.Reflection;
using EFT;
using HarmonyLib;
using TrueView.Core;
using TrueView.Systems;
using UnityEngine;
using SPT.Reflection.Patching;

namespace TrueView.Patches;

/// <summary>
/// Every walk, run and sprint step of the local player, after EFT's own speed limits and
/// before the character controller moves: MomentumSystem holds back the horizontal part so
/// speed builds up. Jumps, vaults, prone, mounting and bots are untouched.
/// </summary>
internal class Patch_LimitMotion : ModulePatch
{
	protected override MethodBase GetTargetMethod()
	{
		return AccessTools.Method(typeof(MovementState), nameof(MovementState.LimitMotion));
	}

	[PatchPostfix]
	private static void Postfix(MovementState __instance, ref Vector3 motion, float deltaTime)
	{
		if (__instance is not MovePlayerState || !HandlingState.Enabled)
			return;
		Player player = HandlingManager.LocalPlayer();
		if (player == null || __instance.MovementContext != player.MovementContext)
			return;
		MomentumSystem.LimitSpeed(player, ref motion, deltaTime);
	}
}
