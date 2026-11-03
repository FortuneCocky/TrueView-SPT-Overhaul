using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace EuphoriaReaction;

/// One doll step that may run on a worker thread: native calls on its own agent and world only, and its
/// own fields. Nothing of Unity's.
internal interface IDollStep { void StepOffThread(); }

/// <summary>
/// The dolls' steps side by side. A doll step is ~0.5-0.7 ms of the engine's muscles, balance and contacts,
/// 60 times a second for every reacting bot and every nudge doll; one after the other on the main thread,
/// six of them were ~18 ms a step. Each doll has its own agent and world (and the engine's shared state is
/// read-only or locked), so they step at the same time on a few worker threads, the main thread working
/// along and waiting for the last. One doll alone steps right there, as before.
/// Nothing allocated per run: the jobs go in a reused array, claimed by one atomic counter.
/// </summary>
internal static class DollSteps
{
    private static readonly int WorkerCount = Mathf.Clamp(Environment.ProcessorCount / 2 - 1, 0, 6);
    private static Thread[] _workers;
    private static readonly SemaphoreSlim Go = new SemaphoreSlim(0);
    private static readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
    private static IDollStep[] _jobs = new IDollStep[16];
    // (job count << 32) | next job: one atomic word, so a worker that wakes late never claims a job of the next run
    private static long _state;
    private static int _left;
    private static int _failed;

    internal static bool Threaded => WorkerCount > 0 && (EuphoriaPlugin.Instance?.ThreadedSteps.Value ?? true);

    /// Steps every job once; returns when all are done.
    internal static void Run(List<IDollStep> jobs)
    {
        int n = jobs.Count;
        if (n == 0) return;
        if (n == 1 || !Threaded) { for (int i = 0; i < n; i++) Safe(jobs[i]); return; }
        Start();
        if (_jobs.Length < n) _jobs = new IDollStep[Mathf.NextPowerOfTwo(n)];
        for (int i = 0; i < n; i++) _jobs[i] = jobs[i];
        Volatile.Write(ref _left, n);
        Done.Reset();
        Interlocked.Exchange(ref _state, (long)n << 32); // published last: the jobs are in place
        int wake = Math.Min(n - 1, _workers.Length) - Go.CurrentCount;
        if (wake > 0) Go.Release(wake);
        Work();
        Done.Wait();
        Array.Clear(_jobs, 0, n);
    }

    private static void Work()
    {
        while (true)
        {
            long s = Volatile.Read(ref _state);
            int count = (int)(s >> 32), i = (int)s;
            if (i >= count) return;
            if (Interlocked.CompareExchange(ref _state, s + 1, s) != s) continue;
            try { Safe(_jobs[i]); }
            finally { if (Interlocked.Decrement(ref _left) == 0) Done.Set(); }
        }
    }

    private static void Safe(IDollStep job)
    {
        try { job?.StepOffThread(); }
        catch (Exception e) { if (Interlocked.Increment(ref _failed) <= 3) EuphoriaPlugin.Log("Delirium: doll step: " + e.Message); }
    }

    private static void Start()
    {
        if (_workers != null) return;
        _workers = new Thread[WorkerCount];
        for (int i = 0; i < WorkerCount; i++)
        {
            _workers[i] = new Thread(Loop) { IsBackground = true, Name = "Euphoria doll step " + i };
            _workers[i].Start();
        }
        EuphoriaPlugin.Log($"Delirium: dolls step side by side on {WorkerCount} worker thread(s) + the main thread");
    }

    private static void Loop()
    {
        while (true)
        {
            Go.Wait();
            Work();
        }
    }
}
