using System.Collections.Generic;
using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// Every reacting bot's doll steps together. A bot's Update runs as before up to its steps (the pre-step
/// reads, the bubble, the get-up target), then hands the steps and the rest of its Update to the stepper,
/// which runs at the end of Update: round by round each bot's per-step work on the main thread, the
/// dolls' steps side by side (DollSteps), then each bot's after-step ticks in the order they signed up.
/// Per bot the order is what it always was; only the doll step itself moved off the main thread.
/// </summary>
internal partial class NmBody : IDollStep
{
    private static readonly List<NmBody> Deferred = new List<NmBody>();
    private static readonly List<NmBody> Round = new List<NmBody>();
    private static readonly List<IDollStep> RoundJobs = new List<IDollStep>();
    private static long _roundTicks;

    /// True when the steps (and the rest of this Update) will run in the stepper this frame.
    private bool DeferSteps()
    {
        if (!DollSteps.Threaded) return false;
        DollStepper.Ensure();
        Deferred.Add(this);
        return true;
    }

    public void StepOffThread()
    {
        long ps = PerfNow();
        nm_agent_step(_agent, _world, Step);
        PerfStep(ps);
    }

    /// End of Update: every deferred bot's steps, side by side, then the rest of its Update.
    internal static void RunDeferred()
    {
        if (Deferred.Count == 0) return;
        try
        {
            for (int r = 0; r < 3; r++) // _acc holds at most 3 steps
            {
                Round.Clear(); RoundJobs.Clear();
                foreach (NmBody b in Deferred)
                {
                    if (b == null || !b.Active || b._acc < Step) continue;
                    long pf = PerfNow();
                    b._acc -= Step;
                    b.GrabStep();
                    b.PerfFrame(pf);
                    Round.Add(b); RoundJobs.Add(b);
                }
                if (Round.Count == 0) break;
                long t0 = PerfNow();
                DollSteps.Run(RoundJobs);
                _roundTicks = (PerfNow() - t0) / Round.Count; // the main thread's wait, shared out
                foreach (NmBody b in Round)
                {
                    long pf = PerfNow();
                    b.PhysGetUpAssist(); // getting up through the doll: pelvis and chest helped towards the clip
                    b.SnapStep();        // the state the bones are interpolated from (NmBodyInterp)
                    b._deferStepped = true;
                    b.PerfFrame(pf, _roundTicks);
                }
            }
            foreach (NmBody b in Deferred)
            {
                if (b == null) continue;
                bool stepped = b._deferStepped;
                b._deferStepped = false;
                if (!b.Active) continue;
                long pf = PerfNow();
                try { b.UpdateAfterSteps(stepped); }
                catch (System.Exception e) { EuphoriaPlugin.Log("Delirium: after-step tick: " + e); }
                finally { b.PerfFrame(pf); }
            }
        }
        finally { Deferred.Clear(); Round.Clear(); RoundJobs.Clear(); }
    }

    private bool _deferStepped;
}

/// Runs the deferred doll steps at the end of Update, and the nudge dolls' steps just before HitNudge's LateUpdate.
[DefaultExecutionOrder(30000)]
internal class DollStepper : MonoBehaviour
{
    private static DollStepper _it;

    internal static void Ensure()
    {
        if (_it != null) return;
        var go = new GameObject("Euphoria doll stepper");
        DontDestroyOnLoad(go);
        _it = go.AddComponent<DollStepper>();
        go.AddComponent<NudgeStepper>();
    }

    private void Update() => NmBody.RunDeferred();
}

[DefaultExecutionOrder(9990)] // HitNudge is 10000
internal class NudgeStepper : MonoBehaviour
{
    private void LateUpdate() => HitNudge.StepLiveDolls();
}
