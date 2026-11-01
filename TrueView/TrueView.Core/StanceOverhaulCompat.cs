using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using TrueView.Utilities;

namespace TrueView.Core;

/// <summary>
/// Soft hook into Fontaine's Stance Overhaul (com.fontaine.spt-stance-overhaul).
/// Nothing is referenced at build time: when the mod is installed, its public
/// stance events are found by reflection, and TrueView hands the gun to it
/// while a stance (low/high ready, short stock, patrol, left shoulder...) is
/// held. Without the mod every query here is a cheap "no".
/// </summary>
internal static class StanceOverhaulCompat
{
    private const string Guid = "com.fontaine.spt-stance-overhaul";

    private static bool _initDone;
    private static PropertyInfo _stanceTypeProp;
    private static readonly HashSet<string> _active = new();
    private static bool _shoulderSwap;

    public static bool Installed { get; private set; }

    /// <summary>A stance owns the gun: TrueView's deadzone and hip offsets stand aside.</summary>
    public static bool HandsOwned => _active.Count > 0;

    /// <summary>True once after its left-shoulder stance starts or ends.</summary>
    public static bool ConsumeShoulderSwap()
    {
        bool swap = _shoulderSwap;
        _shoulderSwap = false;
        return swap;
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
            Assembly asm = info.Instance.GetType().Assembly;
            Type events = asm.GetType("StanceOverhaul.Events.StanceEvents");
            Type stance = asm.GetType("StanceOverhaul.Stances.IStance");
            _stanceTypeProp = stance?.GetProperty("StanceType");
            EventInfo entered = events?.GetEvent("OnStanceEntered");
            EventInfo exited = events?.GetEvent("OnStanceExitedRef");
            if (entered == null || exited == null || _stanceTypeProp == null)
            {
                Log.Warning("Stance Overhaul found, but its stance events have changed: compatibility off");
                return;
            }
            Subscribe(entered, nameof(OnEntered));
            Subscribe(exited, nameof(OnExited));
            Installed = true;
            Log.Info("Stance Overhaul detected: TrueView hands the gun to its stances and leaves hip sway to it");
        }
        catch (Exception e)
        {
            Log.Warning($"Stance Overhaul hook failed, compatibility off: {e.Message}");
        }
    }

    // The events are Action<IStance>; a static method taking object binds to them.
    private static void Subscribe(EventInfo evt, string handler)
    {
        MethodInfo method = typeof(StanceOverhaulCompat).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Static);
        evt.AddEventHandler(null, Delegate.CreateDelegate(evt.EventHandlerType, method));
    }

    private static string StanceName(object stance)
    {
        try { return _stanceTypeProp.GetValue(stance)?.ToString() ?? "None"; }
        catch { return "None"; }
    }

    private static void OnEntered(object stance)
    {
        string name = StanceName(stance);
        // Plain aiming stays TrueView's (ADS deadzone).
        if (name == "None" || name == "Aiming")
            return;
        if (_active.Add(name))
            Log.Info($"Stance Overhaul: {name} stance, TrueView hands the gun over");
        if (name == "LeftShoulder")
            _shoulderSwap = true;
    }

    private static void OnExited(object stance)
    {
        string name = StanceName(stance);
        if (_active.Remove(name))
            Log.Info($"Stance Overhaul: {name} stance ended{(_active.Count == 0 ? ", TrueView takes the gun back" : "")}");
        if (name == "LeftShoulder")
            _shoulderSwap = true;
    }

    /// <summary>Raid end or player change: forget any stance still listed.</summary>
    public static void Clear()
    {
        _active.Clear();
        _shoulderSwap = false;
    }
}
