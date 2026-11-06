using System;
using System.Reflection;
using BepInEx.Bootstrap;
using TrueView.Utilities;
using UnityEngine;

namespace TrueView.Core;

/// <summary>
/// Soft hook into Tactical Stances (com.devin.tacticalstances). Nothing is
/// referenced at build time: when the mod is installed its live stance and
/// lean shoulder-swap offsets are read by reflection, so TrueView can tell
/// when a stance is posing the gun. Without the mod every query is a cheap "no".
/// </summary>
internal static class TacticalStancesCompat
{
    private const string Guid = "com.devin.tacticalstances";

    private static bool _initDone;
    private static FieldInfo _posOffset, _rotOffset, _leanPos, _leanRot, _suppress;
    private static FieldInfo _bend, _basePos, _baseRot;
    private static PropertyInfo _effectiveStance;
    private static FieldInfo _enableStances;

    public static bool Installed { get; private set; }

    /// <summary>
    /// A stance or the lean shoulder swap is moving the gun off its normal
    /// hold (more than ~2 mm or ~0.2 deg), including while it blends.
    /// </summary>
    public static bool Posing
    {
        get
        {
            EnsureInit();
            if (!Installed)
                return false;
            try
            {
                float vis = 1f - (float)_suppress.GetValue(null);
                Vector3 pos = ((Vector3)_posOffset.GetValue(null) + (Vector3)_leanPos.GetValue(null)) * vis;
                Vector3 rot = ((Vector3)_rotOffset.GetValue(null) + (Vector3)_leanRot.GetValue(null)) * vis;
                return pos.sqrMagnitude > 4e-6f || rot.sqrMagnitude > 0.04f;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Firing from Active Aim or Canted Aim, the fire-return pose included
    /// (a shot from Low or High Ready that brings the gun up to Active Aim).
    /// </summary>
    public static bool StanceFire
    {
        get
        {
            EnsureInit();
            if (_effectiveStance == null)
                return false;
            try
            {
                if (_enableStances?.GetValue(null) is BepInEx.Configuration.ConfigEntry<bool> on && !on.Value)
                    return false;
                string stance = _effectiveStance.GetValue(null)?.ToString();
                return stance == "ActiveAim" || stance == "CantedAim";
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// For the jitter probe: the stance + lean swap offsets as applied, the
    /// bend, and the game's own animated gun pose it builds on (WeaponRootAnim
    /// local space).
    /// </summary>
    public static bool TryRead(out Vector3 pos, out Vector3 rot, out Vector2 bend, out Vector3 basePos, out Quaternion baseRot)
    {
        pos = rot = basePos = Vector3.zero;
        bend = Vector2.zero;
        baseRot = Quaternion.identity;
        EnsureInit();
        if (!Installed || _bend == null || _basePos == null || _baseRot == null)
            return false;
        try
        {
            float vis = 1f - (float)_suppress.GetValue(null);
            pos = ((Vector3)_posOffset.GetValue(null) + (Vector3)_leanPos.GetValue(null)) * vis;
            rot = ((Vector3)_rotOffset.GetValue(null) + (Vector3)_leanRot.GetValue(null)) * vis;
            bend = (Vector2)_bend.GetValue(null);
            basePos = (Vector3)_basePos.GetValue(null);
            baseRot = (Quaternion)_baseRot.GetValue(null);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void EnsureInit()
    {
        if (_initDone)
            return;
        _initDone = true;
        try
        {
            if (!Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info.Instance == null)
                return;
            Type manager = info.Instance.GetType().Assembly.GetType("TacticalStances.StanceManager");
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            _posOffset = manager?.GetField("_currentPosOffset", flags);
            _rotOffset = manager?.GetField("_currentRotOffset", flags);
            _leanPos = manager?.GetField("_currentLeanSwapPos", flags);
            _leanRot = manager?.GetField("_currentLeanSwapRot", flags);
            _suppress = manager?.GetField("_suppress", flags);
            _bend = manager?.GetField("_bendAngle", flags);
            _basePos = manager?.GetField("_baseLocalPos", flags);
            _baseRot = manager?.GetField("_baseLocalRot", flags);
            Type plugin = info.Instance.GetType();
            _effectiveStance = plugin.GetProperty("EffectiveStance", BindingFlags.Public | BindingFlags.Static);
            _enableStances = plugin.GetField("EnableStances", BindingFlags.Public | BindingFlags.Static);
            if (_posOffset == null || _rotOffset == null || _leanPos == null || _leanRot == null || _suppress == null)
            {
                Log.Warning("Tactical Stances found, but its stance offsets have changed: compatibility off");
                return;
            }
            Installed = true;
            Log.Info("Tactical Stances detected: barrel aim stands aside while a stance or the lean shoulder swap poses the gun");
        }
        catch (Exception e)
        {
            Log.Warning($"Tactical Stances hook failed, compatibility off: {e.Message}");
        }
    }
}
