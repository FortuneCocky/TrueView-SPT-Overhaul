using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// Lying-pose capture, step 1 of the captured get-ups: every time a downed bot settles on the ground
/// (the moment a get-up would start), the doll's real pose is appended to lying_poses.jsonl next to
/// the plugin: all 21 parts (Unity world position + rotation), their joint pivots, SPT's 21 mapped
/// bones, the centre of mass, and how he lies (face up/down, side, head direction, height). The dev
/// panel's "Capture lying pose" button saves the selected bot's current pose the same way. The doll's
/// rest skeleton (part names, parents, rest rotations, joint offsets) is written once to
/// lying_poses_rig.json so the get-up generator can read the poses without the game.
/// These are the start poses the MotionBricks get-ups will be generated from.
/// </summary>
internal partial class NmBody
{
    private static string _captureDir;
    private static int _captured;

    private static void CaptureInit(string dir) => _captureDir = dir;

    /// Append this bot's current doll pose. `src` = "auto" (settled after a fall) or "manual" (dev panel).
    internal bool CaptureLyingPose(string src)
    {
        if (!Active || _captureDir == null || _bones == null) return false;
        try
        {
            WriteRigOnce();
            var sb = new StringBuilder(4096);
            Part(0, out Vector3 pp, out Quaternion pr);
            Vector3 up = pr * Vector3.up;
            nm_agent_get_com(_agent, _airP, _airV);
            Vector3 com = ToUnity(_airP[0], _airP[1], _airP[2]);
            float ground = RayWorld(com + Vector3.up * 0.5f, Vector3.down, 5f, out RaycastHit g) ? g.point.y : pp.y - 0.15f;
            bool faceDown = LyingFaceDown(out Vector3 headDir);

            sb.Append('{');
            sb.Append("\"time\":\"").Append(DateTime.Now.ToString("s", CultureInfo.InvariantCulture)).Append("\",");
            sb.Append("\"src\":\"").Append(src).Append("\",");
            sb.Append("\"bot\":\"").Append(_p?.ProfileId).Append("\",");
            sb.Append("\"start\":\"").Append(_startPose).Append("\",");
            sb.Append("\"downFor\":").Append(F4(_downSince > 0f ? Time.time - _downSince : 0f)).Append(',');
            sb.Append("\"settled\":").Append(nm_agent_is_settled(_agent) == 1 ? "true" : "false").Append(',');
            sb.Append("\"faceDown\":").Append(faceDown ? "true" : "false").Append(',');
            sb.Append("\"headDir\":").Append(V(headDir)).Append(',');
            sb.Append("\"pelvisUp\":").Append(V(up)).Append(',');
            sb.Append("\"ground\":").Append(F4(ground)).Append(',');
            sb.Append("\"com\":").Append(V(com)).Append(',');
            sb.Append("\"parts\":[");
            for (int k = 0; k < Parts; k++)
            {
                Part(k, out Vector3 p, out Quaternion q);
                if (k > 0) sb.Append(',');
                sb.Append(PQ(p, q));
            }
            sb.Append("],\"pivots\":[");
            for (int k = 0; k < Parts; k++)
            {
                if (k > 0) sb.Append(',');
                sb.Append(Joint(k, out _, out Vector3 pv, out _) ? V(pv) : "null");
            }
            sb.Append("],\"bones\":[");
            for (int k = 0; k < Parts; k++)
            {
                if (k > 0) sb.Append(',');
                Transform b = _bones[k];
                sb.Append(b != null ? PQ(b.position, b.rotation) : "null");
            }
            sb.Append("]}\n");
            File.AppendAllText(Path.Combine(_captureDir, "lying_poses.jsonl"), sb.ToString());
            _captured++;
            EuphoriaPlugin.Log($"Delirium: lying pose captured ({src}, {(faceDown ? "face down" : up.y > 0.5f ? "face up" : "on his side")}, pelvis {pp.y - ground:F2} m up) - {_captured} this session");
            return true;
        }
        catch (Exception e) { EuphoriaPlugin.Log("Delirium: lying pose not captured: " + e.Message); return false; }
    }

    private void WriteRigOnce()
    {
        string path = Path.Combine(_captureDir, "lying_poses_rig.json");
        if (File.Exists(path)) return;
        DollGeom g = ReadDollGeom();
        if (g == null) return;
        var sb = new StringBuilder();
        sb.Append("{\"coords\":\"Unity world, Y up\",\"parts\":[");
        for (int k = 0; k < Parts; k++)
        {
            if (k > 0) sb.Append(',');
            sb.Append("{\"index\":").Append(k)
              .Append(",\"parent\":").Append(g.Parent[k])
              .Append(",\"bone\":\"").Append(_bones[k] != null ? _bones[k].name : "").Append('"')
              .Append(",\"joint\":\"").Append(_jointKind[k] == '\0' ? '?' : _jointKind[k]).Append('"')
              .Append(",\"restRot\":").Append(Q(g.RestRot[k]))
              .Append(",\"jointInSelf\":").Append(V(g.JointInSelf[k]))
              .Append(",\"jointInParent\":").Append(V(g.JointInParent[k]))
              .Append('}');
        }
        sb.Append("]}\n");
        File.WriteAllText(path, sb.ToString());
    }

    private static string F4(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
    private static string V(Vector3 v) => $"[{F4(v.x)},{F4(v.y)},{F4(v.z)}]";
    private static string Q(Quaternion q) => $"[{F4(q.x)},{F4(q.y)},{F4(q.z)},{F4(q.w)}]";
    private static string PQ(Vector3 p, Quaternion q) => $"{{\"p\":{V(p)},\"q\":{Q(q)}}}";
}
