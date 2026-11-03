using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using EFT;
using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// The nudge dolls' cost. Each one stepped on the main thread in its bot's LateUpdate (~0.5 ms a step,
/// 60 a second, up to 3 s a nudge), and a new one first stood 15 steps to settle: an ~8 ms hitch on the
/// first hit of every bot. Now every swinging nudge doll steps side by side just before the nudges'
/// LateUpdate (NudgeStepper), a settled doll is parked with its bot for his next nudge, and new ones come
/// ready-settled from <see cref="NudgeDolls"/>, made on a background thread.
/// </summary>
internal partial class HitNudge : IDollStep
{
    private static readonly List<HitNudge> Live = new List<HitNudge>();
    private static readonly List<IDollStep> LiveJobs = new List<IDollStep>();
    private bool _dollLiveFlag;
    private int _steppedFrame = -1;
    private float _jobDt;

    /// The doll swings and steps (false: none, or parked settled).
    private bool _dollLive
    {
        get => _dollLiveFlag;
        set
        {
            if (value == _dollLiveFlag) return;
            _dollLiveFlag = value;
            if (value) Live.Add(this); else Live.Remove(this);
        }
    }

    public void StepOffThread() => StepDoll(_jobDt);

    /// Just before HitNudge's LateUpdate: every doll that LateUpdate is about to pose, stepped side by side.
    internal static void StepLiveDolls()
    {
        if (Live.Count == 0) return;
        float dt = Mathf.Min(Time.deltaTime, 0.05f); // what LateUpdate steps them by
        LiveJobs.Clear();
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            HitNudge n = Live[i];
            if (n == null) { Live.RemoveAt(i); continue; }
            if (!n.WillPose()) continue;
            n._jobDt = dt;
            LiveJobs.Add(n);
        }
        if (LiveJobs.Count < 2 || !DollSteps.Threaded) { LiveJobs.Clear(); return; } // one alone steps in its own LateUpdate, as before
        DollSteps.Run(LiveJobs);
        int f = Time.frameCount;
        for (int i = 0; i < LiveJobs.Count; i++) ((HitNudge)LiveJobs[i])._steppedFrame = f;
        LiveJobs.Clear();
    }

    /// LateUpdate will step and pose the doll this frame (its own checks, in its order).
    private bool WillPose()
    {
        if (!isActiveAndEnabled || !_moving || _bones == null || _na == IntPtr.Zero || !_dollLive || _p == null) return false;
        NmBody body = _p.GetComponent<NmBody>();
        if (body != null && body.Busy) return false;
        return _p.HealthController != null && _p.HealthController.IsAlive;
    }
}

/// <summary>
/// Nudge dolls made ahead of time: a few spare, each already stood its 15 settling steps on a background
/// thread (its own agent and world, nothing shared). All face the world's +z; HitNudge turns hits into
/// and swings out of the doll's frame by the bot's yaw, as it does for one made facing him.
/// </summary>
internal static class NudgeDolls
{
    private sealed class Doll
    {
        public IntPtr W, A;
        public readonly Vector3[] RestPos = new Vector3[21];
        public readonly Quaternion[] RestRot = new Quaternion[21];
    }

    private const int Spare = 4;
    private static readonly ConcurrentQueue<Doll> Ready = new ConcurrentQueue<Doll>();
    private static int _making, _generation;
    private static readonly System.Random Rng = new System.Random();

    /// A settled spare doll, false when none is ready (the caller makes one as before).
    internal static bool Take(out IntPtr world, out IntPtr agent, Vector3[] restPos, Quaternion[] restRot)
    {
        world = agent = IntPtr.Zero;
        bool got = Ready.TryDequeue(out Doll d);
        Fill();
        if (!got) return false;
        world = d.W; agent = d.A;
        Array.Copy(d.RestPos, restPos, 21);
        Array.Copy(d.RestRot, restRot, 21);
        return true;
    }

    /// Spares made with the old balance tuning are dropped (a balance preset change), new ones made.
    internal static void Flush()
    {
        Interlocked.Increment(ref _generation);
        while (Ready.TryDequeue(out Doll d)) Destroy(d);
        Fill();
    }

    /// Tops the spares up on a background thread.
    internal static void Fill()
    {
        if (NmBody.BaseBuild == IntPtr.Zero || Ready.Count >= Spare) return;
        if (Interlocked.CompareExchange(ref _making, 1, 0) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                while (Ready.Count < Spare)
                {
                    int gen = Volatile.Read(ref _generation);
                    Doll d = Make();
                    if (d == null) break;
                    if (gen != Volatile.Read(ref _generation)) { Destroy(d); continue; }
                    Ready.Enqueue(d);
                }
            }
            catch (Exception e) { EuphoriaPlugin.Log("Delirium: spare nudge doll: " + e.Message); }
            finally { Volatile.Write(ref _making, 0); }
        });
    }

    // what HitNudge.DollUp does, facing +z
    private static Doll Make()
    {
        var d = new Doll { W = nm_world_create() };
        if (d.W == IntPtr.Zero) return null;
        nm_world_add_floor(d.W, 0f, 0f, 0f, 20f);
        float seed;
        lock (Rng) seed = (float)Rng.NextDouble();
        d.A = nm_agent_create(NmBody.BaseBuild, new float[3], Mathf.PI, seed);
        if (d.A == IntPtr.Zero) { Destroy(d); return null; }
        nm_agent_task_balance(d.A, d.W, "*", null);
        for (int i = 0; i < 15; i++) nm_agent_step(d.A, d.W, 1f / 60f);
        float[] p3 = new float[3], q4 = new float[4];
        for (int k = 0; k < 21; k++)
        {
            nm_agent_get_part_transform(d.A, k, p3, q4);
            d.RestPos[k] = new Vector3(p3[0], p3[2], p3[1]);
            d.RestRot[k] = new Quaternion(-q4[0], -q4[2], -q4[1], q4[3]);
        }
        return d;
    }

    private static void Destroy(Doll d)
    {
        if (d.A != IntPtr.Zero) nm_agent_destroy(d.A);
        if (d.W != IntPtr.Zero) nm_world_destroy(d.W);
        d.A = d.W = IntPtr.Zero;
    }
}
