using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// Smooth following. The doll steps at a fixed 60 Hz inside Update's accumulator; the bones were set from
/// its latest step every rendered frame, so at 90, 144 Hz... the pose only changed on some frames and the
/// body moved in visible chunks ("copying the doll's position like frames"), most obviously the moment a
/// reaction starts.
///
/// Now the last two steps' part transforms and joint pivots are kept, and while LateUpdate poses the bones
/// everything that reads the doll (Part, PartRot, Joint) returns the state between them, at the fraction
/// of the step that has passed since the last one. The pose shown trails the simulation by under one step.
/// </summary>
internal partial class NmBody
{
    private readonly float[][] _snP = { new float[Parts * 3], new float[Parts * 3] };
    private readonly float[][] _snR = { new float[Parts * 4], new float[Parts * 4] };
    private readonly float[][] _snJ = { new float[Parts * 3], new float[Parts * 3] };
    private readonly bool[][] _snJok = { new bool[Parts], new bool[Parts] };
    private int _snCur;          // which of the two is the latest
    private bool _snOk, _view;
    private float _viewAlpha;
    private readonly float[] _snA = new float[3], _snQ = new float[4], _snL = new float[3], _snM = new float[1];

    private static bool InterpOn => EuphoriaPlugin.Instance?.SmoothDoll.Value ?? true;

    /// After each doll step: the latest state becomes "current", the one before it "previous".
    private void SnapStep()
    {
        if (_agent == System.IntPtr.Zero) return;
        int prev = _snCur;
        _snCur = 1 - _snCur;
        int c = _snCur;
        for (int k = 0; k < Parts; k++)
        {
            nm_agent_get_part_transform(_agent, k, _snA, _snQ);
            System.Array.Copy(_snA, 0, _snP[c], k * 3, 3);
            System.Array.Copy(_snQ, 0, _snR[c], k * 4, 4);
            _snJok[c][k] = false;
            if (k == 0) continue;
            try
            {
                if (nm_agent_get_joint(_agent, k, out int _, _snA, _snQ, _snL, _snM) != 0)
                {
                    System.Array.Copy(_snA, 0, _snJ[c], k * 3, 3);
                    _snJok[c][k] = true;
                }
            }
            catch { }
        }
        if (!_snOk) { // first step: nothing before it
            System.Array.Copy(_snP[c], _snP[prev], _snP[c].Length);
            System.Array.Copy(_snR[c], _snR[prev], _snR[c].Length);
            System.Array.Copy(_snJ[c], _snJ[prev], _snJ[c].Length);
            System.Array.Copy(_snJok[c], _snJok[prev], Parts);
            _snOk = true;
        }
    }

    private void SnapReset() { _snOk = false; _view = false; }

    /// LateUpdate: from here to the end of the pose pass the doll is read between its last two steps.
    private void ViewOn()
    {
        _view = _snOk && Active && !_rigCheck && InterpOn;
        _viewAlpha = Mathf.Clamp01(_acc / Step);
    }

    private void LateUpdate()
    {
        long pf = PerfNow();
        ViewOn();
        try { LateUpdateCore(); PoseSnapshot(); }
        finally { _view = false; PerfFrame(pf); }
    }

    private bool ViewPart(int k, out Vector3 pos, out Quaternion rot)
    {
        pos = default; rot = default;
        if (!_view) return false;
        int c = _snCur, p = 1 - _snCur;
        float a = _viewAlpha;
        var pa = _snP[p]; var pb = _snP[c];
        float x = Mathf.Lerp(pa[k * 3], pb[k * 3], a), y = Mathf.Lerp(pa[k * 3 + 1], pb[k * 3 + 1], a), z = Mathf.Lerp(pa[k * 3 + 2], pb[k * 3 + 2], a);
        pos = ToUnity(x, y, z);
        var qa = _snR[p]; var qb = _snR[c];
        Quaternion r0 = ToUnity(qa[k * 4], qa[k * 4 + 1], qa[k * 4 + 2], qa[k * 4 + 3]);
        Quaternion r1 = ToUnity(qb[k * 4], qb[k * 4 + 1], qb[k * 4 + 2], qb[k * 4 + 3]);
        rot = Quaternion.Slerp(r0, r1, a);
        return true;
    }

    private bool ViewJoint(int k, out Vector3 pivot)
    {
        pivot = default;
        if (!_view || k < 1 || k >= Parts) return false;
        int c = _snCur, p = 1 - _snCur;
        if (!_snJok[c][k] || !_snJok[p][k]) return false;
        float a = _viewAlpha;
        var ja = _snJ[p]; var jb = _snJ[c];
        pivot = ToUnity(Mathf.Lerp(ja[k * 3], jb[k * 3], a), Mathf.Lerp(ja[k * 3 + 1], jb[k * 3 + 1], a), Mathf.Lerp(ja[k * 3 + 2], jb[k * 3 + 2], a));
        return true;
    }
}
