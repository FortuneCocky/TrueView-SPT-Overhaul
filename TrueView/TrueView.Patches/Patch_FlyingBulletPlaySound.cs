using System.Reflection;
using EFT;
using EFT.Ballistics;
using HarmonyLib;
using TrueView.Core;
using TrueView.Systems;
using UnityEngine;
using SPT.Reflection.Patching;

namespace TrueView.Patches;

/// <summary>
/// A round came within earshot of the local player's camera. Notes which side it passed on
/// for FlybySystem, and while TrueView's whiz takes are in use keeps the game from playing
/// its own flyby sound for it (the game skips the sound while its last clip "is playing").
/// Rounds that hit someone and the player's own rounds are left alone.
/// </summary>
internal class Patch_FlyingBulletPlaySound : ModulePatch
{
	protected override MethodBase GetTargetMethod()
	{
		return AccessTools.Method(typeof(FlyingBulletSoundPlayer), nameof(FlyingBulletSoundPlayer.PlaySound));
	}

	[PatchPrefix]
	private static void Prefix(Shot shot, Vector3 forward, Vector3 normal, Player ____player, Vector2 ____minMaxRadius, ref float ____lastClipPlayingTime)
	{
		if (shot == null || ____player == null || !____player.IsYourPlayer || !HandlingState.Enabled)
			return;
		// the same early-outs the game takes: out of earshot, or the round hit someone
		if (normal.magnitude > ____minMaxRadius.y || shot.HittedBallisticCollider is BodyPartCollider)
			return;
		if (shot.PlayerProfileID == ____player.ProfileId)
			return;
		FlybySystem.Pending(BulletSoundsUtils.CalculatePan(forward, normal));
		if (FlybySystem.ReplacesGameSound)
			____lastClipPlayingTime = Time.time + 1f;
	}
}
