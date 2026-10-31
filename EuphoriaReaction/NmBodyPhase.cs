using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// The reaction in phases: the animation has him (none) -> stumble -> fall, only if he can't hold it ->
/// recover into a stance -> none. One value per bot, changed only through SetPhase, which logs every
/// change. The stumble has a clock (Stumble length): steady after its min he recovers, still not steady
/// at its max he's put down; the engine giving up on his balance sends him into the fall at any time.
/// The rules themselves are in PhaseRules (tested without the game).
/// </summary>
internal partial class NmBody
{
    private Phase _reaction;
    private float _reactionStart;
    private StartPose _recoverTarget;      // the stance he's recovering into (RecoverStance)
    private float _fallWaitFrom, _fallStillSince; // on the ground: since when he waits to get up, since when he's still

    internal Phase CurrentPhase => _reaction;

    private void SetPhase(Phase to, string why)
    {
        if (to == _reaction) return;
        Phase from = _reaction;
        _reaction = to;
        _reactionStart = Time.time;
        if (_p != null) EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} phase {PhaseRules.Name(from)} -> {PhaseRules.Name(to)} ({why})");
    }

    private static (float min, float max) StumbleWindowNow()
    {
        EuphoriaPlugin ep = EuphoriaPlugin.Instance;
        return ep == null ? PhaseRules.StumbleWindow(StumbleLength.Natural, 0f, 0f)
                          : PhaseRules.StumbleWindow(ep.StumbleLen.Value, ep.StumbleMin.Value, ep.StumbleMax.Value);
    }

    /// Update, on his feet in the stumble: the clock. `steady` = his body is steady right now.
    private void StumbleTick(bool steady, float h, bool canFall)
    {
        var (min, max) = StumbleWindowNow();
        float elapsed = Time.time - _reactionStart;
        _stableFor = steady ? _stableFor + Time.deltaTime : 0f;
        bool held = _stableFor > Mathf.Min(RecoverHold, min);
        if (held && _aimBack) return; // steady and shooting back: he stays on his feet until that's over
        bool fallen = nm_agent_has_fallen(_agent) == 1; // the engine gave up on his balance (after the catch-himself roll)
        switch (PhaseRules.StumbleDecision(elapsed, min, max, held, fallen && canFall))
        {
            case StumbleExit.Recover:
                EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} found his feet after {Time.time - _attachAt:F1}s - back to his stance{(_crouchStumble ? " (crouched)" : "")}");
                if (!RecoverOnFeet(false, h)) ReleaseToStance();
                break;
            case StumbleExit.Fall:
                GoDown(h, "going over");
                break;
            case StumbleExit.ForcedFall:
                if (!canFall) { EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} still stumbling at {max:F1} s - back to animation (falls off)"); ReleaseToStance(); break; }
                GoDown(h, $"forced at max {max:F1} s");
                break;
        }
    }

    /// He goes over: the catch for the way he falls (once), and the fall phase.
    private void GoDown(float h, string why)
    {
        if (!_catchFallSent)
        {
            _catchFallSent = true;
            if (!StairRollStart(Vector3.zero)) // steps falling away ahead: he rolls down them instead (NmBodyTrip)
                StartCatch(h); // the catch for the way he falls; it lets go of his balance (NmBodyFall)
            OpenZero("going over"); // stop holding the crouch / gun-hold limbs he was taken over in
        }
        SetPhase(Phase.Fall, why);
    }

    /// The stance he recovers into: his start pose when healthy, one lower when hurt, prone when badly hurt
    /// (PhaseRules.RecoverTarget). `endedLow`: he's at crouch height now, so not back up to standing.
    private StartPose RecoverStance(bool endedLow = false)
    {
        EuphoriaPlugin ep = EuphoriaPlugin.Instance;
        _recoverTarget = PhaseRules.RecoverTarget(_startPose, StumbleEngine.HpFraction(_p), ep?.HurtBelowHp.Value ?? 0.65f,
                                                  ep?.ProneBelowHp.Value ?? 0.35f, endedLow || _crouchStumble);
        return _recoverTarget;
    }

    /// On his feet and recovering: the stance match into his target. Too hurt to stay up (target prone), he
    /// goes down instead, and recovers prone from the ground. False = no match (caller hands back plainly).
    private bool RecoverOnFeet(bool endedLow, float h)
    {
        StartPose to = RecoverStance(endedLow);
        if (to == StartPose.Prone) { GoDown(h, $"too hurt to stay up ({StumbleEngine.HpFraction(_p):P0} HP)"); return true; }
        return BeginMatch(to, DollFacing());
    }

    /// Update, on the ground: is the fall over (down a moment and still, or down too long)?
    private bool FallOverTick()
    {
        bool still = nm_agent_is_settled(_agent) == 1 || nm_agent_get_kinetic_energy(_agent) < PhaseRules.StillEnergy;
        if (!still) _fallStillSince = 0f;
        else if (_fallStillSince == 0f) _fallStillSince = Time.time;
        return PhaseRules.FallDone(Time.time - _fallWaitFrom, still ? Time.time - _fallStillSince : 0f);
    }

    /// He's down: the wait to get up starts (again, if he's hit on the ground).
    private void FallWaitStart()
    {
        _fallWaitFrom = Time.time;
        _fallStillSince = 0f;
    }

    /// Dev panel: the phase, for the bot label.
    private string PhaseLabel()
    {
        switch (_reaction)
        {
            case Phase.Stumble: return $" STUMBLE {Time.time - _reactionStart:F1}s";
            case Phase.Fall: return " FALL";
            case Phase.Recover: return $" RECOVER->{_recoverTarget.ToString().ToLowerInvariant()}";
            default: return "";
        }
    }
}
