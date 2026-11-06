using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// The hinge report, written at every takeover with no dev panel needed: for each knee and elbow,
/// how the binding's target sits on the doll's hinge against its (fitted) parent. "bend" is the
/// turn about the hinge axis, signed; "off" is the rest of the turn, which FitJoint drops. A large
/// off on every bot means the binding maps SPT's joint onto the doll the wrong way, not that the
/// limit is wrong. Totals for the session go to joint_report.txt in the plugin folder.
/// </summary>
internal partial class NmBody
{
    private sealed class HingeStats { public int N, Outside, AxisN, PlaneN, UpN; public float OffSum, OffMax, BendMin = 999f, BendMax = -999f, AxisSum, AxisMax, PlaneSum, PlaneMax, UpSum, UpMax; }
    private static readonly Dictionary<int, HingeStats> _hingeStats = new Dictionary<int, HingeStats>();
    private static readonly Queue<string> _jointRecent = new Queue<string>();
    private static int _jointReports;
    private const int JointRecentKept = 30;

    private static string HingeName(int k) => k == 2 ? "L knee" : k == 5 ? "R knee" : k == 15 ? "L elbow" : k == 19 ? "R elbow" : "part " + k;

    // the build's range; Unity's sign about the mapped axis is the build's (crouched knees bend negative)
    private static float HingeLoDeg(int k) => _hingeLo[k] * Mathf.Rad2Deg;
    private static float HingeHiDeg(int k) => _hingeHi[k] * Mathf.Rad2Deg;

    /// Takeover, right after the seed pose is made (SeedFromBinding).
    private void JointReport(RigBinding bind, Quaternion[] tRot, bool[] tOk, Quaternion[] sRot, DollGeom g)
    {
        if (!_jointsLoaded || bind == null) return;
        try
        {
            var line = new StringBuilder($"{DateTime.Now:HH:mm:ss} {_p.ProfileId} {_startPose}:");
            int n = 0;
            for (int k = 1; k < Parts; k++)
            {
                if (_jointKind[k] != 'h' || _hingeAxisRest[k].sqrMagnitude < 0.5f) continue;
                int pa = g.Parent[k];
                if (pa < 0 || !bind.Has[k] || !tOk[k]) continue;
                HingeSplit(k, pa, sRot[pa], HingeWant(k, pa, bind, tRot, tOk, sRot[pa]), g, out float bend, out float off); // as the seed fits it
                bool outside = bend < HingeLoDeg(k) - 5f || bend > HingeHiDeg(k) + 5f;
                if (!_hingeStats.TryGetValue(k, out HingeStats s)) _hingeStats[k] = s = new HingeStats();
                s.N++; s.OffSum += off; s.OffMax = Mathf.Max(s.OffMax, off);
                s.BendMin = Mathf.Min(s.BendMin, bend); s.BendMax = Mathf.Max(s.BendMax, bend);
                if (outside) s.Outside++;
                line.Append($"  {HingeName(k)} {bend:+0;-0} bend / {off:F0} off{(outside ? " OUTSIDE" : "")}");
                // the doll's hinge axis, put on SPT's bone through the binding, against EFT's own ragdoll
                // joint on that bone: a large angle means the binding rolls the part about its length
                Vector3 doll = tRot[k] * bind.Q[k] * (Quaternion.Inverse(g.RestRot[k]) * _hingeAxisRest[k]);
                // the same axis carried by the parent's binding: a hinge doesn't turn its axis, so the two agree
                // unless the binding turns one part about its length (an outfit's thighs half a turn: 180 here)
                Vector3 dollUp = bind.Has[pa] && tOk[pa] ? tRot[pa] * bind.Q[pa] * (Quaternion.Inverse(g.RestRot[pa]) * _hingeAxisRest[k]) : Vector3.zero;
                // SPT's own limb: a hinge's axis is square to both bones of the bend (needs a bend to measure).
                // Small here with a large off means the off is twist about the forearm or shin (a grip), not the binding.
                // Signed: the doll's axis turned the way its hinge bends (knees negative, elbows positive) against
                // SPT's (thigh x shin); 180 is a joint bent the wrong way. It was unsigned and from the lower part
                // only, and read 6 deg on a bot whose thighs were bound reversed.
                if (k + 1 < Parts && _bones[pa] != null && _bones[k] != null && _bones[k + 1] != null && doll.sqrMagnitude > 1e-6f)
                {
                    Vector3 up = _bones[k].position - _bones[pa].position, lo = _bones[k + 1].position - _bones[k].position;
                    Vector3 nrm = Vector3.Cross(up, lo);
                    if (Vector3.Angle(up, lo) > 20f && nrm.sqrMagnitude > 1e-8f)
                    {
                        float flex = Mathf.Abs(_hingeHi[k]) >= Mathf.Abs(_hingeLo[k]) ? 1f : -1f;
                        float planeOff = Vector3.Angle(flex * doll, nrm);
                        line.Append($" axis {planeOff:F0} off the bend");
                        s.PlaneN++; s.PlaneSum += planeOff; s.PlaneMax = Mathf.Max(s.PlaneMax, planeOff);
                        if (dollUp.sqrMagnitude > 1e-6f)
                        {
                            float upOff = Vector3.Angle(flex * dollUp, nrm);
                            line.Append($" ({upOff:F0} from the {(k == 2 || k == 5 ? "thigh" : "upper arm")})");
                            s.UpN++; s.UpSum += upOff; s.UpMax = Mathf.Max(s.UpMax, upOff);
                        }
                    }
                }
                var cj = _bones[k] != null ? _bones[k].GetComponent<CharacterJoint>() : null;
                if (cj != null)
                {
                    Vector3 eft = _bones[k].rotation * cj.axis;
                    if (doll.sqrMagnitude > 1e-6f && eft.sqrMagnitude > 1e-6f)
                    {
                        float axisOff = Mathf.Acos(Mathf.Clamp(Mathf.Abs(Vector3.Dot(doll.normalized, eft.normalized)), 0f, 1f)) * Mathf.Rad2Deg;
                        line.Append($" axis {axisOff:F0} off EFT's");
                        if (!_hingeStats.TryGetValue(k, out HingeStats hs)) _hingeStats[k] = hs = new HingeStats();
                        hs.AxisN++; hs.AxisSum += axisOff; hs.AxisMax = Mathf.Max(hs.AxisMax, axisOff);
                    }
                }
                // how far the fit moved the parent (the hip clamp); the bend above is SPT's own regardless
                if (bind.Has[pa] && tOk[pa])
                {
                    float clamped = Quaternion.Angle(sRot[pa], tRot[pa] * bind.Q[pa]);
                    if (clamped > 5f) line.Append($" (parent clamped {clamped:F0})");
                }
                n++;
            }
            if (n == 0) return;
            _jointReports++;
            EuphoriaPlugin.Log("Delirium joints " + line);
            _jointRecent.Enqueue(line.ToString());
            while (_jointRecent.Count > JointRecentKept) _jointRecent.Dequeue();
            WriteJointReport(bind.Source);
        }
        catch (Exception e) { EuphoriaPlugin.LogV("Delirium: joint report failed: " + e.Message); }
    }

    private static void WriteJointReport(string source)
    {
        if (_pluginDir == null) return;
        var sb = new StringBuilder();
        sb.Append($"Euphoria joint report - {_jointReports} takeover(s) this session, updated {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
        sb.Append($"binding: {source}\n");
        sb.Append("bend = turn about the doll's hinge (deg, signed); off = turn off the hinge the binding asks for (deg, dropped by the fit);\n");
        sb.Append("outside = bend past the build's range (5 deg slack)\n");
        sb.Append("hinge axis vs SPT's bend: signed, 180 = bent the wrong way (from the lower part's binding; in brackets the upper part's);\n");
        sb.Append("hinge axis vs EFT's: either way round (a roll about the limb)\n\n");
        foreach (var kv in _hingeStats)
        {
            int k = kv.Key; HingeStats s = kv.Value;
            sb.Append($"{HingeName(k)}: {s.N} takeovers  off mean {s.OffSum / s.N:F0} max {s.OffMax:F0}  bend {s.BendMin:+0;-0} to {s.BendMax:+0;-0}" +
                      $"  outside {s.Outside}/{s.N} (allowed {HingeLoDeg(k):F0} to {HingeHiDeg(k):F0})" +
                      (s.PlaneN > 0 ? $"  hinge axis vs SPT's bend mean {s.PlaneSum / s.PlaneN:F0} max {s.PlaneMax:F0}" : "") +
                      (s.UpN > 0 ? $" (from the {(k == 2 || k == 5 ? "thigh" : "upper arm")} mean {s.UpSum / s.UpN:F0} max {s.UpMax:F0})" : "") +
                      (s.AxisN > 0 ? $"  hinge axis vs EFT's mean {s.AxisSum / s.AxisN:F0} max {s.AxisMax:F0}" : "") + "\n");
        }
        sb.Append("\nrecent takeovers:\n");
        foreach (string l in _jointRecent) sb.Append(l).Append('\n');
        File.WriteAllText(Path.Combine(_pluginDir, "joint_report.txt"), sb.ToString());
    }
}
