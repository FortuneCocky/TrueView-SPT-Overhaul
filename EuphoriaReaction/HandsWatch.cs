using System;
using System.Reflection;
using EFT;
using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// After a hand-back: his weapon really back in his hands. The one switch asked at the end of the blend
/// didn't always take - a grenade throw cut off by a reaction left the grenade controller, then empty
/// hands, and he ran about with nothing in them. For a few seconds his hands are checked; a grenade or
/// nothing still there is switched again, each try a firmer way in (the AI's weapon selector, then the
/// player's last equipped weapon, then the first item he can hold). Meds get a grace period: the AI may
/// rightly heal once he's on his feet, but a heal cut off by a reaction left the meds in his hands for
/// good (the AI still waiting on it, every switch refused); past the grace the use is finished at once
/// (FastForward) and the weapon asked for again. The watch runs on while meds are in his hands.
/// </summary>
internal class HandsWatch : MonoBehaviour
{
    private const float Watch = 10f;   // s after the hand-back
    private const float Retry = 2f;    // s between tries (a real throw is done within it)
    private const float MedsGrace = 9f; // s of meds in his hands that can still be a real heal
    private const float MedsWatch = 40f; // s the watch may run on while meds stay in his hands
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private Player _p;
    private float _until, _next, _start, _medsSince;
    private int _tries;

    /// Start (or restart) the watch on him.
    internal static void Begin(Player p)
    {
        if (p == null) return;
        HandsWatch w = p.GetComponent<HandsWatch>() ?? p.gameObject.AddComponent<HandsWatch>();
        w._p = p;
        w._until = Time.time + Watch;
        w._next = Time.time + Retry;
        w._tries = 0;
        w._start = Time.time;
        w._medsSince = 0f;
        w.enabled = true;
    }

    private void Update()
    {
        if (_p == null || _p.HealthController?.IsAlive != true || Time.time > _until) { enabled = false; return; }
        if (Time.time < _next) return;
        _next = Time.time + Retry;
        object hc = BotActions.Get(_p, "HandsController");
        string kind = hc?.GetType().Name ?? "nothing";
        if (kind.Contains("Firearm") || kind.Contains("Knife"))
        {
            if (_tries > 0) EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} has his weapon again ({kind}, after {_tries} more tries)");
            enabled = false;
            return;
        }
        bool meds = kind.Contains("Meds") || kind.Contains("Usable");
        if (meds)
        {
            // a heal may run its course; one that never ends is stuck
            if (_medsSince == 0f) _medsSince = Time.time;
            _until = Mathf.Max(_until, Mathf.Min(_start + MedsWatch, Time.time + Retry * 2f));
            if (Time.time - _medsSince < MedsGrace) return;
        }
        else _medsSince = 0f;
        if (!(hc == null || meds || kind.Contains("Grenade") || kind.Contains("Empty"))) return; // anything else: the AI's own doing
        _tries++;
        string ff = meds ? Call(hc, "FastForward") + ", " : "";
        string how = ff + (_tries == 1 ? Selector() : _tries == 2 ? Call(_p, "TrySetLastEquippedWeapon") : Call(_p, "SetFirstAvailableItem"));
        EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} still has {kind} in his hands - weapon again ({how})");
    }

    private string Selector()
    {
        object sel = BotActions.Get(BotActions.Get(BotActions.Get(BotActions.Get(_p, "AIData"), "BotOwner"), "WeaponManager"), "Selector");
        if (sel == null) return Call(_p, "TrySetLastEquippedWeapon");
        string r = Call(sel, "ChangeToMain");
        return r.EndsWith("True") ? r : r + ", " + Call(sel, "TakeMainWeapon");
    }

    /// A method by name with every parameter at its default (null callbacks, false flags): what it returned.
    private static string Call(object o, string name)
    {
        try
        {
            foreach (MethodInfo mi in o.GetType().GetMethods(All))
            {
                if (mi.Name != name || mi.IsGenericMethodDefinition) continue;
                ParameterInfo[] ps = mi.GetParameters();
                var args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                    args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue
                            : ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
                object r = mi.Invoke(o, args);
                return $"{name}({ps.Length}) -> {r ?? "done"}";
            }
            return name + " not found";
        }
        catch (Exception e) { return name + " failed: " + (e.InnerException ?? e).Message; }
    }
}
