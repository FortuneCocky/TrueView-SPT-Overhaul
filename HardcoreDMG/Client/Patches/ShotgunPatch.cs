using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace HardcoreDMGClient.Patches;

/// <summary>
/// Records the shot behind each bullet DamageInfo, so the shotgun model can
/// read what DamageInfo leaves out: the pellet's speed at impact, how many
/// pellets the shell fired, and whether a shotgun fired it. The DamageInfo is
/// built and applied in the same call (GameWorld.ShotDelegate), so the last
/// record always belongs to the hit being applied; HitPoint confirms it.
/// </summary>
public class ShotgunShotPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Constructor(typeof(DamageInfo), new[] { typeof(EDamageType), typeof(Shot) });
    }

    [PatchPostfix]
    private static void Postfix(Shot shot)
    {
        if (!HardcoreDMGPlugin.ShotgunsOn.Value || shot == null) return;
        try
        {
            ShotgunModel.Record(shot);
        }
        catch { }
    }
}

/// <summary>
/// HardcoreDMG's shotgun model, applied to each pellet or slug hit before the
/// body part multipliers. Every pellet is its own hit, so the volley's damage
/// is the sum of what each pellet brings:
///
///  1. Energy: a pellet wounds with its kinetic energy, which falls with the
///     square of its speed. EFT only scales damage roughly linearly with speed,
///     so this adds the rest: r² / (r + (1 - r)·k), where r is impact speed over
///     muzzle speed and k is EFT's damage degradation coefficient. Light pellets
///     shed speed fast, so this is what makes buckshot fade at range.
///  2. Spread: up to point blank the pattern is still one tight mass and hits
///     harder (the bonus); it fades to 1.0 at the effective range. Past that the
///     pattern opens and fewer, weaker pellets reach the vitals, scaling by
///     (effective / distance)^falloff, never below the minimum range scale.
///  3. Pellet count: each extra pellet from the same shell on the same target
///     adds overlapping wound channels. The k-th pellet of an N-pellet shell
///     does 1 + bonus·(k-1)/(N-1), so a full hit averages 1 + bonus/2.
///  4. Armor: pellets stopped by armor still deliver blunt trauma, scaled by
///     the buckshot blunt setting. Slugs are one heavy projectile: they keep
///     their energy far better (the energy term handles that), hit harder, and
///     a plate that stops one still takes a big blunt hit.
/// </summary>
public static class ShotgunModel
{
    private const float ClusterWindowSeconds = 0.25f;
    private const float MinTotalScale = 0.05f, MaxTotalScale = 2.5f;

    private struct ShotRecord
    {
        public bool Valid;
        public Vector3 HitPoint;
        public float SpeedRatio;
        public float Degradation;
        public int Pellets;
        public bool FromShotgun;
    }

    private struct Cluster
    {
        public string Key;
        public int Count;
        public float Time;
    }

    private static ShotRecord _last;
    private static readonly Dictionary<int, Cluster> Clusters = new();

    public static void Record(Shot shot)
    {
        int pellets = ProjectileCount(shot.Ammo);
        bool fromShotgun = shot.Weapon is Weapon weapon && weapon.WeapClass == "shotgun";

        _last = new ShotRecord
        {
            Valid = pellets > 1 || fromShotgun,
            HitPoint = shot.HitPoint,
            SpeedRatio = shot.InitialSpeed > 0f ? Mathf.Clamp01(shot.VelocityMagnitude / shot.InitialSpeed) : 1f,
            Degradation = Mathf.Clamp01(shot.DamageDegradationCoefficient),
            Pellets = pellets,
            FromShotgun = fromShotgun,
        };
    }

    // The ammo item class name changes between game builds, so its
    // ProjectileCount is read by reflection, cached per type.
    private static readonly Dictionary<System.Type, PropertyInfo> ProjectileCountProps = new();

    private static int ProjectileCount(Item ammo)
    {
        if (ammo == null) return 1;
        System.Type type = ammo.GetType();
        if (!ProjectileCountProps.TryGetValue(type, out PropertyInfo prop))
        {
            prop = AccessTools.Property(type, "ProjectileCount");
            ProjectileCountProps[type] = prop;
        }
        return prop?.GetValue(ammo) is int count ? count : 1;
    }

    public static void Apply(Player target, ref DamageInfo di)
    {
        if (!HardcoreDMGPlugin.ShotgunsOn.Value) return;
        if (di.DamageType != EDamageType.Bullet) return;

        ShotRecord shot = _last;
        _last.Valid = false;
        if (!shot.Valid || shot.HitPoint != di.HitPoint) return;

        bool buckshot = shot.Pellets > 1;
        float distance = Vector3.Distance(di.MasterOrigin, di.HitPoint);
        float scale = EnergyScale(shot.SpeedRatio, shot.Degradation);

        if (buckshot)
        {
            scale *= RangeScale(distance);
            scale *= 1f + HardcoreDMGPlugin.ShotgunClusterBonus.Value * ClusterShare(target, di, shot.Pellets);
            if (di.Blunt) scale *= HardcoreDMGPlugin.ShotgunBuckshotBlunt.Value;
        }
        else
        {
            scale *= di.Blunt ? HardcoreDMGPlugin.ShotgunSlugBlunt.Value : HardcoreDMGPlugin.ShotgunSlugDamage.Value;
        }

        di.Damage *= Mathf.Clamp(scale, MinTotalScale, MaxTotalScale);
    }

    /// <summary>
    /// Kinetic energy share over the share EFT already applied, blended by the
    /// energy model setting (0 = vanilla, 1 = pure kinetic energy).
    /// </summary>
    private static float EnergyScale(float r, float k)
    {
        float vanilla = r + (1f - r) * k;
        if (vanilla <= 0.001f) return 1f;
        float energy = r * r / vanilla;
        return Mathf.Lerp(1f, energy, HardcoreDMGPlugin.ShotgunEnergyModel.Value);
    }

    /// <summary>
    /// The buckshot pattern's range scale: the point blank bonus up close,
    /// 1.0 at the effective range, then (effective / distance)^falloff.
    /// </summary>
    public static float RangeScale(float distance)
    {
        float pointBlank = HardcoreDMGPlugin.ShotgunPointBlankRange.Value;
        float effective = Mathf.Max(HardcoreDMGPlugin.ShotgunEffectiveRange.Value, pointBlank + 0.1f);
        float bonus = HardcoreDMGPlugin.ShotgunPointBlankBonus.Value;

        if (distance <= pointBlank) return bonus;
        if (distance <= effective)
            return Mathf.Lerp(bonus, 1f, (distance - pointBlank) / (effective - pointBlank));

        float falloff = Mathf.Pow(effective / distance, HardcoreDMGPlugin.ShotgunSpreadFalloff.Value);
        return Mathf.Max(falloff, HardcoreDMGPlugin.ShotgunMinRangeScale.Value);
    }

    /// <summary>
    /// How far into its shell's pellets this hit is: 0 for the first pellet on
    /// the target, 1 once every pellet of the shell has hit it.
    /// </summary>
    private static float ClusterShare(Player target, DamageInfo di, int pellets)
    {
        if (target == null || pellets < 2) return 0f;

        string key = (di.Weapon?.Id ?? "?") + "#" + di.FireIndex;
        int id = target.GetInstanceID();
        float now = Time.time;

        if (Clusters.Count > 128) Clusters.Clear();
        if (Clusters.TryGetValue(id, out Cluster c) && c.Key == key && now - c.Time < ClusterWindowSeconds)
            c.Count++;
        else
            c = new Cluster { Key = key, Count = 1 };
        c.Time = now;
        Clusters[id] = c;

        return Mathf.Clamp01((c.Count - 1f) / (pellets - 1f));
    }
}
