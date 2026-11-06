using System.Reflection;
using EFT;
using EFT.HealthSystem;
using HarmonyLib;
using TrueView.Configuration;
using TrueView.Utilities;
using SPT.Reflection.Patching;

namespace TrueView.Patches;

/// <summary>
/// EFT's own exhausted gasp (the voice's breathing phrase when stamina or oxygen runs out)
/// stays silent for the local player while TrueView's breathing is on, since that already
/// carries the exhaustion. The game still plays its breathing when you are badly injured
/// without a painkiller or wearing a gas mask, and for everyone else.
/// </summary>
internal class Patch_ExhaustedBreath : ModulePatch
{
	protected override MethodBase GetTargetMethod()
	{
		return AccessTools.Method(typeof(Player), nameof(Player.UpdateBreathStatus));
	}

	[PatchPrefix]
	private static bool Prefix(Player __instance, bool ____exhaustionIsAudible)
	{
		if (!____exhaustionIsAudible || __instance == null || !__instance.IsYourPlayer)
			return true;
		if (HandlingConfig.Enabled?.Value != true || (HandlingConfig.BreathingVolume?.Value ?? 0f) <= 0f)
			return true;
		ETagStatus health = __instance.HealthStatus;
		bool injured = (health == ETagStatus.BadlyInjured || health == ETagStatus.Dying)
			&& __instance.HealthController?.FindActiveEffect<IPainKiller>() == null;
		if (injured || __instance.Muffled)
			return true;
		if (UnityEngine.Time.time - _lastLog > 10f)
			Log.Info("Breathing: muted the game's exhausted gasp (TrueView's breathing covers it)");
		_lastLog = UnityEngine.Time.time;
		return false;
	}

	private static float _lastLog = -100f;
}
