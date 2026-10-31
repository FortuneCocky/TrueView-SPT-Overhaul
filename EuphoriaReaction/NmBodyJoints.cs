using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// The doll's joint model, so a pose given to it is one it can actually hold. In body.json the knees
/// and elbows are hinges and the rest ball joints (only the pelvis, the root, is locked). Seeding a
/// crouch or prone pose straight from SPT's bones put feet at an ankle angle the doll doesn't have and
/// knees off their hinge; the solver snapped them back on the first step, which read as the legs and
/// feet twisting. Joint kinds, hinge axes and ranges come from body.json (rest pose, joint i drives part i+1).
/// </summary>
internal partial class NmBody
{
    private static readonly char[] _jointKind = new char[Parts];         // 'l' locked, 'h' hinge, 'b' ball
    private static readonly Vector3[] _hingeAxisRest = new Vector3[Parts]; // Unity, rest pose
    private static readonly float[] _hingeMax = new float[Parts];          // rad, magnitude
    private static readonly float[] _hingeLo = new float[Parts], _hingeHi = new float[Parts]; // rad, the build's own (engine sign)
    private static bool _jointsLoaded;
    private static readonly Vector3[] _hingeAxisDump = new Vector3[Parts];   // as body.json has it (the unfitted build)
    private static readonly Quaternion[] _dumpPartRot = new Quaternion[Parts]; // each part's rest frame there (rows read as its axes)
    private static readonly Quaternion[] _dumpPartRotT = new Quaternion[Parts]; // the same matrix read the other way (columns as its axes)
    private static bool _dumpFrames;
    private static string _hingeRefitNote;

    private static float F2(string s) => float.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// The dump's hinge axes are world vectors of the unfitted build. nm_build_fit turns the arm parts to
    /// SPT's arms (the legs hang straight in both), so the dump's elbow axis leant 43-61 deg along the
    /// fitted forearm (elbow-roll log; in the dump itself it is square to it) and no roll could put it on
    /// SPT's elbow. Each hinge axis is carried with its own part, dump frame -> fitted frame. Skipped if
    /// the pelvis, spine or thighs disagree (they aren't turned by the fit: a disagreement there means the
    /// dump's frames weren't read right).
    private static void RefitHingeAxes(Quaternion[] fittedRot, string who)
    {
        if (!_jointsLoaded || !_dumpFrames) return;
        // each part's turn, dump -> fitted. Which way the dump's frame matrices read (rows or columns as the
        // axes) and whether a constant turn sits between the two spaces (the pelvis's, cancelled) isn't
        // known for sure: all four readings are tried and the one the unfitted parts (pelvis, spine,
        // thighs) agree on best is used. The first reading alone (rows, pelvis-cancelled) came out 133 deg off.
        Quaternion[] frames = _dumpPartRot; bool cancel = true; float check = float.MaxValue;
        var tried = new StringBuilder();
        foreach (var fr in new[] { _dumpPartRot, _dumpPartRotT })
            foreach (bool c in new[] { true, false })
            {
                Quaternion pv = c ? fittedRot[0] * Quaternion.Inverse(fr[0]) : Quaternion.identity;
                float worst = 0f;
                foreach (int k in new[] { 1, 4, 7, 8 })
                    worst = Mathf.Max(worst, Quaternion.Angle(Quaternion.Inverse(pv) * fittedRot[k] * Quaternion.Inverse(fr[k]), Quaternion.identity));
                tried.Append($" {(fr == _dumpPartRot ? "rows" : "cols")}{(c ? "+pelvis" : "")} {worst:F0}");
                if (worst < check) { check = worst; frames = fr; cancel = c; }
            }
        Quaternion pelvis = cancel ? fittedRot[0] * Quaternion.Inverse(frames[0]) : Quaternion.identity;
        Quaternion Turn(int k) => Quaternion.Inverse(pelvis) * fittedRot[k] * Quaternion.Inverse(frames[k]);
        var sb = new StringBuilder($" (readings, deg off:{tried})");
        bool use = check < 30f; // columns + pelvis came in at 25: the leg fit itself turns the thighs a little
        for (int k = 1; k < Parts; k++)
        {
            if (_jointKind[k] != 'h' || _hingeAxisDump[k].sqrMagnitude < 0.5f) continue;
            Vector3 fitted = (Turn(k) * _hingeAxisDump[k]).normalized;
            sb.Append($" part {k} turned {Quaternion.Angle(Turn(k), Quaternion.identity):F0} (axis moved {Vector3.Angle(fitted, _hingeAxisDump[k]):F0})");
            _hingeAxisRest[k] = use ? fitted : _hingeAxisDump[k];
        }
        string note = (use ? "hinge axes carried onto the fitted build:" : $"hinge axes left as dumped (pelvis/spine/thighs {check:F0} deg off the dump - frames misread?):") + sb;
        if (note != _hingeRefitNote) { _hingeRefitNote = note; EuphoriaPlugin.Log($"Delirium: {who} {note}; pelvis/spine/thighs agree within {check:F0} deg"); }
    }

    private static void LoadJointModel(string dump)
    {
        try
        {
            // each build part's joint kind, from its limit: "locked", {"bone_hinge": ...} or {"bone": ...}
            int buildAt = dump.IndexOf("\"build\"", StringComparison.Ordinal), part = 0;
            if (buildAt >= 0)
                foreach (Match m in Regex.Matches(dump.Substring(buildAt), @"""limit"":\s*(""locked""|\{\s*""(\w+)"")"))
                {
                    if (part >= Parts) break;
                    string kind = m.Groups[2].Success ? m.Groups[2].Value : "locked";
                    _jointKind[part++] = kind == "locked" ? 'l' : kind == "bone_hinge" ? 'h' : 'b';
                }

            var rx = new Regex(@"""index"":\s*(\d+).*?""twist_axis"":\s*\[([^\]]*)\].*?""limits"":\s*\[\s*\[\s*([^,\]]+),\s*([^\]]+)\]", RegexOptions.Singleline);
            foreach (Match m in rx.Matches(dump))
            {
                int k = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + 1;
                if (k <= 0 || k >= Parts) continue;
                string[] a = m.Groups[2].Value.Split(',');
                if (a.Length < 3) continue;
                float F(string s) => float.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
                _hingeAxisDump[k] = _hingeAxisRest[k] = ToUnity(F(a[0]), F(a[1]), F(a[2])).normalized;
                _hingeLo[k] = F(m.Groups[3].Value);
                _hingeHi[k] = F(m.Groups[4].Value);
                _hingeMax[k] = Mathf.Max(Mathf.Abs(_hingeLo[k]), Mathf.Abs(_hingeHi[k]));
            }
            // each part's rest frame in the dump: rows are its x, y, z axes, then its position (engine space)
            var fx = new Regex(@"""frame"":\s*\[\s*\[([^\]]*)\],\s*\[([^\]]*)\],\s*\[([^\]]*)\]");
            int pi = 0;
            int partsAt = dump.IndexOf("\"parts\"", StringComparison.Ordinal);
            if (partsAt >= 0)
                foreach (Match m in fx.Matches(dump, partsAt))
                {
                    if (pi >= Parts) break;
                    Vector3 Row(int g) { string[] s = m.Groups[g].Value.Split(','); return ToUnity(F2(s[0]), F2(s[1]), F2(s[2])); }
                    // ToUnity swaps y and z: the engine's y axis becomes Unity's z column and z becomes y
                    _dumpPartRot[pi] = Quaternion.LookRotation(Row(2), Row(3));
                    // columns as the axes: column j is (row1[j], row2[j], row3[j])
                    string[] r1 = m.Groups[1].Value.Split(','), r2 = m.Groups[2].Value.Split(','), r3 = m.Groups[3].Value.Split(',');
                    Vector3 Col(int j) => ToUnity(F2(r1[j]), F2(r2[j]), F2(r3[j]));
                    _dumpPartRotT[pi++] = Quaternion.LookRotation(Col(1), Col(2));
                }
            _dumpFrames = pi == Parts;
            _jointsLoaded = true;
            int hinges = 0, locked = 0;
            for (int k = 0; k < Parts; k++) { if (_jointKind[k] == 'h') hinges++; else if (_jointKind[k] == 'l') locked++; }
            EuphoriaPlugin.Log($"Delirium: joint model loaded ({hinges} hinges, {locked} locked parts)");
        }
        catch (Exception e) { EuphoriaPlugin.Log("Delirium: joint model unavailable (" + e.Message + "); seeds won't be fitted to the doll's joints"); }
    }

    /// The rotation part k (child of `pa`) can actually take, given its parent's: a locked part
    /// keeps its rest angle to the parent; a hinge keeps only the bend about its axis (in range);
    /// a ball joint takes the target as is.
    private static Quaternion FitJoint(int k, int pa, Quaternion parentRot, Quaternion want, DollGeom g)
    {
        if (!_jointsLoaded || pa < 0) return want;
        Quaternion restRel = Quaternion.Inverse(g.RestRot[pa]) * g.RestRot[k];
        char kind = _jointKind[k];
        if (kind == 'l') return parentRot * restRel;
        if (k == 1 || k == 4) return FitHip(k, parentRot, want, restRel, g);
        if (kind != 'h' || _hingeAxisRest[k].sqrMagnitude < 0.5f) return want;

        // the change from the rest relation, in the parent's frame; keep its twist about the hinge axis
        Quaternion delta = Quaternion.Inverse(parentRot) * want * Quaternion.Inverse(restRel);
        Vector3 axis = (Quaternion.Inverse(g.RestRot[pa]) * _hingeAxisRest[k]).normalized;
        Vector3 v = new Vector3(delta.x, delta.y, delta.z);
        float along = Vector3.Dot(v, axis);
        var twist = new Quaternion(axis.x * along, axis.y * along, axis.z * along, delta.w);
        float m = Mathf.Sqrt(twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w);
        twist = m < 1e-6f ? Quaternion.identity : new Quaternion(twist.x / m, twist.y / m, twist.z / m, twist.w / m);
        twist.ToAngleAxis(out float deg, out Vector3 ax);
        if (deg > 180f) deg -= 360f;
        // the build's own range, one-sided for knees (-140..0) and elbows (0..150); the joint report showed
        // Unity's sign about this axis is the build's (crouched knees bend negative). Inside the limit, not
        // on it (on it the solver pushes), except at 0: that end is the straight limb itself.
        float signedDeg = Vector3.Dot(ax, axis) < 0f ? -deg : deg;
        float lo = _hingeLo[k] * Mathf.Rad2Deg, hi = _hingeHi[k] * Mathf.Rad2Deg;
        if (hi - lo > 1f)
        {
            float a = Mathf.Abs(lo) < 0.5f ? lo : lo + LimitMargin, b = Mathf.Abs(hi) < 0.5f ? hi : hi - LimitMargin;
            float held = Mathf.Clamp(signedDeg, a, b);
            if (held != signedDeg) twist = Quaternion.AngleAxis(held, axis);
        }
        return parentRot * twist * restRel;
    }

    /// Rig check: how part k's target sits on its hinge against its parent, the bend about the hinge
    /// (deg, signed about the Unity axis) and the rest of the turn, which FitJoint drops (deg).
    private static void HingeSplit(int k, int pa, Quaternion parentRot, Quaternion want, DollGeom g, out float bend, out float offHinge)
    {
        Quaternion restRel = Quaternion.Inverse(g.RestRot[pa]) * g.RestRot[k];
        Quaternion delta = Quaternion.Inverse(parentRot) * want * Quaternion.Inverse(restRel);
        Vector3 axis = (Quaternion.Inverse(g.RestRot[pa]) * _hingeAxisRest[k]).normalized;
        float along = Vector3.Dot(new Vector3(delta.x, delta.y, delta.z), axis);
        bend = 2f * Mathf.Atan2(along, delta.w) * Mathf.Rad2Deg;
        if (bend > 180f) bend -= 360f;
        if (bend < -180f) bend += 360f;
        offHinge = Quaternion.Angle(delta, Quaternion.AngleAxis(bend, axis));
    }

    // Kept well inside the hip ranges in body.json (112 deg forward, 34 deg twist about the thigh).
    // A crouch seeded past them (SPT's deep crouch bends the hips well over 90) was wrenched back into
    // range by the solver on the first steps by rotating the whole leg: knees and feet ended up facing
    // sideways or backwards (legcheck: twist 100-140 deg within 0.2 s of a crouched takeover).
    private const float LimitMargin = 8f;    // deg kept clear of every limit
    private const float HipMaxSwing = 82f;   // deg off the rest (straight down) thigh direction
    private const float HipMaxTwist = 12f;   // deg about the thigh

    private static Quaternion FitHip(int k, Quaternion parentRot, Quaternion want, Quaternion restRel, DollGeom g)
    {
        Vector3 thighSelf = g.JointInParent[k + 1] - g.JointInSelf[k];   // hip -> knee, in the thigh's frame
        if (thighSelf.sqrMagnitude < 1e-6f) return want;
        Vector3 restDir = (restRel * thighSelf).normalized;              // in the pelvis frame, at rest
        Quaternion delta = Quaternion.Inverse(parentRot) * want * Quaternion.Inverse(restRel);

        // swing-twist about the rest thigh direction
        Vector3 v = new Vector3(delta.x, delta.y, delta.z);
        Vector3 p = restDir * Vector3.Dot(v, restDir);
        var twist = new Quaternion(p.x, p.y, p.z, delta.w);
        float m = Mathf.Sqrt(twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w);
        twist = m < 1e-6f ? Quaternion.identity : new Quaternion(twist.x / m, twist.y / m, twist.z / m, twist.w / m);
        Quaternion swing = delta * Quaternion.Inverse(twist);

        twist.ToAngleAxis(out float tDeg, out Vector3 tAx);
        if (tDeg > 180f) tDeg -= 360f;
        if (Mathf.Abs(tDeg) > HipMaxTwist) twist = Quaternion.AngleAxis(Mathf.Sign(tDeg) * HipMaxTwist, tAx);
        swing.ToAngleAxis(out float sDeg, out Vector3 sAx);
        if (sDeg > 180f) sDeg -= 360f;
        if (Mathf.Abs(sDeg) > HipMaxSwing) swing = Quaternion.AngleAxis(Mathf.Sign(sDeg) * HipMaxSwing, sAx);

        return parentRot * (swing * twist) * restRel;
    }
}
