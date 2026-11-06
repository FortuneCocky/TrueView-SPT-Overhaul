using System.Reflection;
using EFT;
using EFT.Animations.NewRecoil;
using HarmonyLib;
using TrueView.Configuration;
using TrueView.Core;
using SPT.Reflection.Patching;

namespace TrueView.Patches;

/// <summary>
/// The game's own recoil for your shots fired without aiming down sights:
/// scaled by Game recoil, hip, or by Game recoil, stance fire while Tactical
/// Stances has you in Active or Canted Aim. The force feeds both the
/// rotation and position recoil, so the whole impulse scales. ADS recoil
/// is never touched.
/// </summary>
internal class Patch_GameRecoil : ModulePatch
{
	private static readonly AccessTools.FieldRef<Player.FirearmController, Player> PlayerRef =
		AccessTools.FieldRefAccess<Player.FirearmController, Player>("_player");

	protected override MethodBase GetTargetMethod()
	{
		return AccessTools.Method(typeof(NewRecoilShotEffect), nameof(NewRecoilShotEffect.AddRecoilForce));
	}

	[PatchPrefix]
	private static void Prefix(NewRecoilShotEffect __instance, ref float incomingForce)
	{
		if (HandlingConfig.Enabled?.Value != true)
			return;
		Player.FirearmController controller = __instance?._firearmController;
		if (controller == null || controller.IsAiming)
			return;
		Player player = PlayerRef(controller);
		if (player == null || !player.IsYourPlayer)
			return;
		incomingForce *= HandlingConfig.GameRecoilScale(TacticalStancesCompat.StanceFire);
	}
}
