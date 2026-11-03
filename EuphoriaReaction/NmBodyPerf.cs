using System.Diagnostics;
using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// What a reaction cost the main thread, logged once at its release: the takeover (Attach, with its
/// level bake), the rebakes, and each frame's Update + LateUpdate (with the doll's own steps in it).
/// Stopwatch ticks only, nothing allocated: cheap enough to stay on.
/// </summary>
internal partial class NmBody
{
    private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;
    private const double SlowFrameMs = 4.0; // a frame of ours above this shows as a hitch at 60+ fps

    private long _pfAttach, _pfBakeSum, _pfBakeMax, _pfStepSum, _pfFrameSum, _pfFrameMax, _pfThisFrame;
    private int _pfBakes, _pfSteps, _pfFrames, _pfSlow, _pfFrameNo = -1;

    private void PerfReset()
    {
        _pfAttach = _pfBakeSum = _pfBakeMax = _pfStepSum = _pfFrameSum = _pfFrameMax = _pfThisFrame = 0;
        _pfBakes = _pfSteps = _pfFrames = _pfSlow = 0;
        _pfFrameNo = -1;
    }

    private static long PerfNow() => Stopwatch.GetTimestamp();

    private void PerfBake(long from)
    {
        long t = Stopwatch.GetTimestamp() - from;
        _pfBakeSum += t; _pfBakes++;
        if (t > _pfBakeMax) _pfBakeMax = t;
    }

    // (may run on a doll-step worker thread: this bot's fields only)
    private void PerfStep(long from) { _pfStepSum += Stopwatch.GetTimestamp() - from; _pfSteps++; }

    /// Update and LateUpdate of one frame add up to one frame's cost.
    private void PerfFrame(long from) => PerfFrame(from, 0);

    /// `extra`: this bot's share of time spent outside the measured span (the side-by-side doll steps).
    private void PerfFrame(long from, long extra)
    {
        if (!Active && _pfFrameNo < 0) return;
        long t = Stopwatch.GetTimestamp() - from + extra;
        if (_pfFrameNo != Time.frameCount) { PerfCloseFrame(); _pfFrameNo = Time.frameCount; }
        _pfThisFrame += t;
    }

    private void PerfCloseFrame()
    {
        if (_pfFrameNo < 0) return;
        _pfFrames++; _pfFrameSum += _pfThisFrame;
        if (_pfThisFrame > _pfFrameMax) _pfFrameMax = _pfThisFrame;
        if (_pfThisFrame * TickMs > SlowFrameMs) _pfSlow++;
        _pfThisFrame = 0;
    }

    private void PerfReport()
    {
        PerfCloseFrame();
        if (_pfFrames == 0 && _pfAttach == 0) return;
        double bakes = _pfBakes > 0 ? _pfBakeSum * TickMs / _pfBakes : 0.0;
        EuphoriaPlugin.Log($"Delirium: perf {_p?.ProfileId}: takeover {_pfAttach * TickMs:F1} ms, {_pfBakes} level bakes avg {bakes:F1} max {_pfBakeMax * TickMs:F1} ms, "
                           + $"{_pfFrames} frames avg {(_pfFrames > 0 ? _pfFrameSum * TickMs / _pfFrames : 0.0):F2} max {_pfFrameMax * TickMs:F1} ms ({_pfSlow} over {SlowFrameMs:F0} ms), "
                           + $"doll steps avg {(_pfSteps > 0 ? _pfStepSum * TickMs / _pfSteps : 0.0):F2} ms");
        PerfReset();
    }
}
