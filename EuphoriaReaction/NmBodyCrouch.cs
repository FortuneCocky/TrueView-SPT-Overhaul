using System;
using System.Globalization;
using System.IO;
using System.Text;
using EFT;
using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// Standing calibration: the part/bone offsets recorded at a standing takeover (yaw-free, on
/// disk). The rig binding (NmBodyRig) falls back to it for bots whose bind pose can't be read;
/// crouched and prone takeovers themselves are seeded in NmBodyPoses.
/// </summary>
internal partial class NmBody
{
    private const float StandLevel = 0.85f;

    // standing reference, relative to the root (feet on the floor, facing +Z)
    private static readonly Quaternion[] _calPartRot = new Quaternion[Parts], _calBoneRot = new Quaternion[Parts];
    private static readonly Vector3[] _calPartPos = new Vector3[Parts], _calBonePos = new Vector3[Parts];
    private static bool _calOk, _calStill, _calLoaded;
    private static int _calMode = -1;
    private const int CalAttachMode = 3; // the attach pipeline calibrations are tagged with (the old bisect modes are gone; 3 was the default)

    private static string CalPath => Path.Combine(Path.GetDirectoryName(typeof(NmBody).Assembly.Location) ?? "", "crouch_calib.txt");

    /// A standing reference for the current build mode exists, so a crouched bot can be seeded.
    internal static bool CrouchReady
    {
        get
        {
            if (!_calLoaded) LoadCalibration();
            return _calOk && _calMode == CalAttachMode;
        }
    }

    private static bool IsStanding(Player p)
    {
        try { return p?.MovementContext == null || p.MovementContext.PoseLevel >= StandLevel; }
        catch { return true; }
    }

    private static Quaternion YawOf(float theta) => Quaternion.Euler(0f, theta * Mathf.Rad2Deg, 0f);

    /// Standing attach: keep its part/bone references as the reference for later crouched ones.
    /// A still bot replaces one taken mid-stride; after that it is left alone.
    private void CalibrateStanding(Player p, Vector3 origin)
    {
        bool still = false;
        try { still = p.Velocity.magnitude < 0.6f; } catch { }
        if (CrouchReady && (_calStill || !still)) return;
        for (int k = 0; k < Parts; k++)
            if (_bones[k] == null) return; // seeding needs every part

        Quaternion inv = Quaternion.Inverse(YawOf(_theta));
        for (int k = 0; k < Parts; k++)
        {
            _calPartRot[k] = inv * _partRot0[k];
            _calBoneRot[k] = inv * _boneRot0[k];
            _calPartPos[k] = inv * (_partPos0[k] - origin);
            _calBonePos[k] = inv * (_bonePos0[k] - origin);
        }
        _calOk = true;
        _calStill = still;
        _calMode = CalAttachMode;
        SaveCalibration();
        EuphoriaPlugin.Log($"Delirium: crouch calibration taken from {p.ProfileId} ({(still ? "still" : "moving")}, mode {_calMode})");
    }

    private static void SaveCalibration()
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(_calMode.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(_calStill ? 1 : 0).Append('\n');
            for (int k = 0; k < Parts; k++)
            {
                Quaternion a = _calPartRot[k], b = _calBoneRot[k];
                Vector3 c = _calPartPos[k], d = _calBonePos[k];
                float[] row = { a.x, a.y, a.z, a.w, b.x, b.y, b.z, b.w, c.x, c.y, c.z, d.x, d.y, d.z };
                for (int i = 0; i < row.Length; i++)
                    sb.Append(i == 0 ? "" : " ").Append(row[i].ToString("R", CultureInfo.InvariantCulture));
                sb.Append('\n');
            }
            File.WriteAllText(CalPath, sb.ToString());
        }
        catch (Exception e) { EuphoriaPlugin.Log("Delirium: crouch calibration save: " + e.Message); }
    }

    private static void LoadCalibration()
    {
        _calLoaded = true;
        try
        {
            if (!File.Exists(CalPath)) return;
            string[] lines = File.ReadAllLines(CalPath);
            if (lines.Length < Parts + 1) return;
            string[] head = lines[0].Split(' ');
            int mode = int.Parse(head[0], CultureInfo.InvariantCulture);
            for (int k = 0; k < Parts; k++)
            {
                string[] s = lines[k + 1].Split(' ');
                if (s.Length < 14) return;
                float F(int i) => float.Parse(s[i], CultureInfo.InvariantCulture);
                _calPartRot[k] = new Quaternion(F(0), F(1), F(2), F(3));
                _calBoneRot[k] = new Quaternion(F(4), F(5), F(6), F(7));
                _calPartPos[k] = new Vector3(F(8), F(9), F(10));
                _calBonePos[k] = new Vector3(F(11), F(12), F(13));
            }
            _calMode = mode;
            _calStill = head.Length > 1 && head[1] == "1";
            _calOk = true;
            EuphoriaPlugin.Log($"Delirium: crouch calibration loaded (mode {mode})");
        }
        catch (Exception e) { EuphoriaPlugin.Log("Delirium: crouch calibration load: " + e.Message); }
    }
}
