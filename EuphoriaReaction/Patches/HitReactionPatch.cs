using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.Ballistics;
using SPT.Reflection.Patching;
using UnityEngine;

namespace EuphoriaReaction.Patches;

/// <summary>
/// On a non-lethal hit the victim gets a Delirium agent — the real
/// Euphoria engine — which takes over the whole body: the bullet's
/// impulse lands at the hit point, the shot task reaches for the wound,
/// and the balance task tries to catch him with steps until he either
/// recovers or goes down. On a lethal hit the death struggle plays, then SPT's corpse ragdoll takes over.
/// </summary>
public class HitReactionPatch : ModulePatch
{
    private static readonly Dictionary<int, (float Time, float Push)> _reactedAt = new Dictionary<int, (float, float)>();
    private static readonly List<int> _stale = new List<int>();

    private static void PruneReacted()
    {
        foreach (var kv in _reactedAt) if (Time.time - kv.Value.Time > 1f) _stale.Add(kv.Key);
        foreach (int k in _stale) _reactedAt.Remove(k);
        _stale.Clear();
    }

    protected override MethodBase GetTargetMethod()
    {
        return typeof(Player).GetMethod("ApplyDamageInfo");
    }

    [PatchPostfix]
    private static void Postfix(Player __instance, DamageInfo damageInfo, EBodyPart bodyPartType, EBodyPartColliderType __2)
    {
        EuphoriaPlugin entry = EuphoriaPlugin.Instance;
        if (entry == null || !entry.Enabled.Value || __instance == null || __instance.IsYourPlayer)
        {
            return;
        }
        // Any pose: crouched and prone bots are seeded onto their pose through the rig binding
        // (a bot it can't bind keeps his own animation / SPT ragdoll; Attach logs why).
        NmBody existing = __instance.GetComponent<NmBody>();
        bool dead = !__instance.HealthController.IsAlive;
        if (!NmBody.PoseAllowed(__instance)) return;
        // Every lethal hit gets its death struggle (whoever fired, from however far): he dies over a
        // few seconds, and his body can't be looted until it's over (DyingLootBlock).
        if (dead)
        {
            if (existing != null && existing.Dying) return; // already struggling
            // Euphoria Style Physics plays his death: its muscles already took the corpse EFT made inside this hit
            if (!entry.DeathEnabled.Value || AmosBridge.OwnsDeaths)
            {
                if (existing != null && existing.Active) existing.Release();
                if (AmosBridge.OwnsDeaths) AmosBridge.LogSkip(__instance, "death");
                return;
            }
            try
            {
                NmBody body = existing ?? __instance.gameObject.AddComponent<NmBody>();
                if (!body.Active && !body.Attach(__instance, dying: true))
                {
                    EuphoriaPlugin.Log($"Delirium: death attach failed on {__instance.ProfileId}");
                    return;
                }
                float push = PushFor(damageInfo.Damage);
                if (entry.BallisticShove.Value)
                {
                    push = StumbleEngine.BallisticShove(damageInfo, __instance, false, false, out string shove);
                    if (entry.LogScores.Value) EuphoriaPlugin.Log($"Delirium: death {bodyPartType} {shove}");
                }
                body.Die(damageInfo.HitPoint, damageInfo.Direction, Mathf.Min(35f * push * entry.Impulse.Value, 90f), bodyPartType);
            }
            catch (System.Exception e) { EuphoriaPlugin.Log($"Delirium: death error: {e}"); }
            return;
        }
        // Euphoria Style Physics' knockdown or stumble has him: he's its until it lets go
        if (AmosBridge.HisBusy(__instance))
        {
            AmosBridge.LogSkip(__instance, "hit while in its knockdown/stumble");
            return;
        }
        // every hit rocks him a little (no ragdoll); a stumble started below takes over from the pose on screen
        // (its hit flinch does this job when it's installed)
        if (entry.HitNudgeOn.Value && !AmosBridge.FlinchesHits && (existing == null || !existing.Busy))
            HitNudge.Hit(__instance, bodyPartType, damageInfo.HitPoint, damageInfo.Direction, PushFor(damageInfo.Damage), damageInfo.Damage);
        if (AmosBridge.OwnsBlast(damageInfo))
        {
            AmosBridge.LogSkip(__instance, "explosion");
            return;
        }
        if (damageInfo.Player == null || damageInfo.Player.iPlayer == null)
        {
            return;
        }
        if (!entry.AiHitsToo.Value && damageInfo.Player.IsAI)
        {
            return;
        }
        if (Vector3.Distance(damageInfo.Player.iPlayer.Position, __instance.Position) > entry.MaxDistance.Value && !StumbleEngine.IsSniper(damageInfo)) // snipers reach any range
        {
            return;
        }
        // ---- non-lethal: score the hit, then roll against the stumble threshold ----
        if (!StumbleEngine.Evaluate(__instance, damageInfo, bodyPartType, __2, out StumbleResult res))
        {
            return;
        }
        // Bosses: normal ballistics math, but Euphoria only once he is nearly dead.
        if (entry.BossGate.Value && StumbleEngine.IsBoss(__instance))
        {
            float hp = StumbleEngine.HpFraction(__instance);
            if (hp > entry.BossHp.Value)
            {
                if (entry.LogScores.Value) EuphoriaPlugin.Log($"Delirium: boss {__instance.ProfileId} at {hp:P0} HP — above {entry.BossHp.Value:P0}, no stumble");
                return;
            }
        }
        int id = __instance.GetInstanceID();
        // Delirium's dedup: a hit registering twice inside 0.3s (bullet +
        // stagger events for the same blow) only counts the stronger one.
        float strength = res.Intensity;
        if (_reactedAt.TryGetValue(id, out var last) && Time.time - last.Time < 0.3f)
        {
            strength = Mathf.Max(0f, strength - last.Push);
            if (strength < 0.12f)
            {
                return;
            }
        }
        if (_reactedAt.Count > 128) PruneReacted(); // one entry per bot ever hit: only the last 0.3 s matter
        _reactedAt[id] = (Time.time, Mathf.Max(res.Intensity, last.Push));
        // pass if score >= roll * threshold  (threshold 1 = the score IS the probability)
        float threshold = Mathf.Max(0.05f, entry.StumbleThreshold.Value / Mathf.Max(1f, res.Blast)); // a close shotgun blast lowers the bar
        bool pass = res.Always || (res.Score >= UnityEngine.Random.value * threshold && UnityEngine.Random.value <= entry.Chance.Value);
        string report = $"{res.Why} thr={threshold:F2} -> {(pass ? "STUMBLE" : "no reaction")}";
        StumbleEngine.Report(report);
        if (entry.LogScores.Value)
        {
            EuphoriaPlugin.Log("Delirium: stumble score " + report);
        }
        if (!pass)
        {
            return;
        }
        try
        {
            Vector3 dir = damageInfo.Direction;
            IPlayer shooter = damageInfo.Player.iPlayer;
            System.Func<Vector3> aimAt = () => shooter.Position + Vector3.up * 1.35f;
            // aim-back: only a man who is still mostly standing and was not driven to his knees
            float aimChance = (res.LegBuckle || res.Intensity > 1.2f) ? 0f : entry.AimBackChance.Value;
            EuphoriaEngine.TriggerProceduralStumble(__instance, dir, res.Intensity, damageInfo.HitPoint, res.LegBuckle, aimAt, aimChance, res.Ballistic);
            // a hard enough blow tears the weapon loose — he can pick it back
            // up only if the reaction leaves him standing.
            if (res.Intensity >= 1.5f && entry.DropGun.Value)
            {
                __instance.GetComponent<NmBody>()?.DropWeapon();
            }
        }
        catch (System.Exception e)
        {
            EuphoriaPlugin.Log($"Delirium: reaction error: {e}");
        }
    }

    // Delirium's damage curve: ref damage pushes at 1x, double the damage
    // 2^0.8 harder, clamped — calibers feel meaningfully different.
    private static float PushFor(float damage)
    {
        return Mathf.Clamp(Mathf.Pow(Mathf.Max(damage, 1f) / 15f, 0.8f), 0.4f, 3f);
    }
}
