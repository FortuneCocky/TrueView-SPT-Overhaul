using System;
using System.Reflection;
using EFT;
using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// The game's own jump, closed out when Euphoria takes a bot mid-jump. SPT's jump state switches the
/// foot-placement IK (the "grounder") off at take-off and back on only when that state exits, which
/// needs the animator to see him come down and land. Taken over in the air, he never lands as far as
/// the jump state knows: after the hand-back it kept playing the jump (a leg tucked up), kept lifting
/// the root back towards its take-off height, and kept the grounder off, so a foot hung in the air.
/// Here the jump is ended the way the game ends a bumped jump (jump off, landing on, no more lift), at
/// takeover and again at the hand-back, and the grounder is put back once the state has let go.
/// </summary>
internal partial class NmBody
{
    private static readonly System.Collections.Generic.Dictionary<Type, PropertyInfo> _stateNameProp = new System.Collections.Generic.Dictionary<Type, PropertyInfo>();
    private static readonly System.Collections.Generic.Dictionary<object, string> _stateNames = new System.Collections.Generic.Dictionary<object, string>();
    private bool _jumpTaken;
    private float _jumpTakenAt;

    private static string StateName(Player p)
    {
        try
        {
            object st = p?.MovementContext?.CurrentState;
            if (st == null) return null;
            Type t = st.GetType();
            if (!_stateNameProp.TryGetValue(t, out PropertyInfo pi))
                _stateNameProp[t] = pi = t.GetProperty("Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object v = pi?.GetValue(st, null);
            if (v == null) return null;
            if (!_stateNames.TryGetValue(v, out string s)) _stateNames[v] = s = v.ToString(); // enum: one string per state, not per call
            return s;
        }
        catch { return null; }
    }

    private static bool InJump(Player p) => StateName(p) == "Jump";

    /// End the game's jump if he's in one (attach and hand-back).
    private void EndGameJump(string when)
    {
        if (_p == null || !InJump(_p)) return;
        try
        {
            object st = _p.MovementContext.CurrentState;
            st.GetType().GetMethod("Halt", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(st, null);
            _p.MovementContext.PlayerAnimatorEnableJump(false);
            _p.MovementContext.PlayerAnimatorEnableLanding(true);
        }
        catch (Exception e) { EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} couldn't end the game's jump: {e.Message}"); }
        if (!_jumpTaken) _jumpTakenAt = Time.time;
        _jumpTaken = true;
        EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} game jump ended at {when} (landing forced)");
    }

    /// After the hand-back: once the jump state has let go (or it's had a second), feet IK back on.
    private void JumpAfterTick()
    {
        if (!_jumpTaken || _p == null) return;
        bool still = InJump(_p);
        if (still && Time.time < _jumpTakenAt + 1f) return;
        try { _p.MovementContext.GrounderSetActive(true); } catch { }
        _jumpTaken = false;
        EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} foot IK back on{(still ? " (jump state still stuck - forced)" : "")}");
    }
}
