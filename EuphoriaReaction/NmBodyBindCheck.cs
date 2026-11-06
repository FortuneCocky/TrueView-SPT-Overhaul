using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// The bind-pose binding, checked against SPT's live skeleton before it is used. Some outfits' meshes
/// carry bind poses that turn his pelvis and thighs half a turn against his calves (bot ...fea0e: both
/// knees 172-176 deg off their hinge on every crouched takeover, feet 49 cm off in the rig check). The
/// leg fixes in BindPoseRest correct one bone's aim or roll and kept every bone of that bot. What holds
/// on any skeleton, in any pose:
///   - a knee's or elbow's hinge axis, carried onto SPT's bones by the upper part's binding and by the
///     lower part's, points the same way (a hinge doesn't turn its own axis);
///   - on the lower bone it lies along EFT's own ragdoll joint axis (either way);
///   - a knee bends the way his does (his bend when bent, else about an axis pointing to his right);
///   - the doll's hip line (L -> R) through the pelvis binding runs the way his thighs sit on his pelvis.
/// The first binding that passes, from a clean outfit, is saved (rig_binding_reference.txt): all bots
/// share EFT's one skeleton, and a part's turn on its bone doesn't depend on the build's size. A bot
/// whose own binding fails takes the failing region from it (pelvis and legs, or an arm), with the
/// parts' offsets from his own build, if the result passes.
/// </summary>
internal partial class NmBody
{
    private const float HingeSidesMax = 90f; // deg: the hinge axis from the upper and the lower part (good bots: knees 0-4, elbows 4-60)
    private const float EftAxisMax = 30f;    // deg: the lower part's hinge axis vs EFT's joint axis (good bots: knees 4, elbows 12)
    private const float HipLineMax = 45f;    // deg: the doll's hip line vs his, on his pelvis
    private const float KnownBindAgree = 15f; // deg: a clean binding this close to the known-good one is the same skeleton
    private const string KnownBindFile = "rig_binding_reference.txt";
    private static readonly (string name, int upper, int lower)[] BindHinges = { ("L knee", 1, 2), ("R knee", 4, 5), ("L elbow", 14, 15), ("R elbow", 18, 19) };
    private static readonly int[] BindMeshParts = { 0, 1, 2, 4, 5, 14, 15, 18, 19 };

    private static RigBinding _knownBind;
    private static bool _knownBindTried;
    private static readonly HashSet<string> _knownBindCompared = new HashSet<string>(), _bindFailLogged = new HashSet<string>();
    private string _bindNote = "";                           // what the check did, for the binding's source (ComputeBinding)
    private readonly string[] _bindMesh = new string[Parts]; // which mesh's bind pose each mapped bone took (BindPoseRest)

    /// End of FromRest: keep the binding if it passes; else the saved known-good one, if there is one.
    private void VerifyBinding(RigBinding bind, Vector3[] bPos, Quaternion[] bRot, Vector3[] bJoint)
    {
        _bindNote = "";
        string bad = CheckBinding(bind, bRot, bJoint, out string measured, out int regions);
        LoadKnownBinding();
        string id = _p.ProfileId;
        if (bad == null)
        {
            EuphoriaPlugin.LogV($"Delirium: {id} binding passes the skeleton check (deg:{measured})");
            bool clean = _legRollFixed == 0 && _legAimFixed == 0 && _restFromLive == 0 && AllBound(bind);
            if (!clean) return;
            if (_knownBind == null) SaveKnownBinding(bind, id);
            else if (_knownBindCompared.Add(id))
            {
                float worst = WorstPart(bind, _knownBind, out int at);
                if (worst > KnownBindAgree) EuphoriaPlugin.Log($"Delirium: {id} clean binding is {worst:F0} deg off the known-good one at {PartBone(at)} (a different skeleton?)");
                else EuphoriaPlugin.LogV($"Delirium: {id} clean binding agrees with the known-good one within {worst:F0} deg");
            }
            return;
        }

        bool first = _bindFailLogged.Add(id);
        var sb = new StringBuilder($"Delirium: {id} binding fails the skeleton check: {bad} (deg:{measured})");
        if (first)
        {
            sb.Append(". Bind poses from:");
            foreach (int k in BindMeshParts)
                if (_bones[k] != null) sb.Append($" {PartBone(k)} <- {_bindMesh[k]};");
            if (_knownBind != null) sb.Append(" Off the known-good binding (deg):").Append(PartsOff(bind, _knownBind, 30f));
        }
        if (_knownBind == null || _knownBind.SpineFitted != bind.SpineFitted)
        {
            string why = _knownBind == null ? "no known-good binding saved yet" : "the known-good binding is fitted differently along the spine";
            EuphoriaPlugin.Log(sb.Append($". {why}: his own kept, crouched or prone knees may fold").ToString());
            _bindNote = $" [fails the skeleton check: {bad}; {why}]";
            return;
        }

        var ownQ = (Quaternion[])bind.Q.Clone();
        var ownP = (Vector3[])bind.P.Clone();
        for (int k = 0; k < Parts; k++)
        {
            if (!InRegion(k, regions) || !bind.Has[k] || !_knownBind.Has[k]) continue;
            // the part's offset from his bone, from his own build: P = Q * the build's centre-from-joint in the part's frame
            bind.Q[k] = _knownBind.Q[k];
            bind.P[k] = _knownBind.Q[k] * (Quaternion.Inverse(bRot[k]) * (bPos[k] - bJoint[k]));
        }
        string still = CheckBinding(bind, bRot, bJoint, out string known, out _);
        if (still != null)
        {
            Array.Copy(ownQ, bind.Q, Parts);
            Array.Copy(ownP, bind.P, Parts);
            EuphoriaPlugin.Log(sb.Append($". The known-good binding ({_knownBind.Source}) fails it on him too ({still}): his own kept").ToString());
            _bindNote = $" [fails the skeleton check: {bad}; the known-good one does too]";
            return;
        }
        string what = (regions & LegsRegion) != 0 ? "pelvis and legs" : "";
        if ((regions & (LArmRegion | RArmRegion)) != 0) what += (what.Length > 0 ? ", " : "") + ((regions & LArmRegion) != 0 ? (regions & RArmRegion) != 0 ? "both arms" : "left arm" : "right arm");
        EuphoriaPlugin.Log(sb.Append($". {what} from the known-good binding ({_knownBind.Source}; deg:{known})").ToString());
        _bindNote = $" [his own failed the skeleton check ({bad}): {what} from the known-good binding, {_knownBind.Source}]";
    }

    /// What's wrong with the binding on his live skeleton (null: nothing). `measured` has every angle.
    private string CheckBinding(RigBinding bind, Quaternion[] bRot, Vector3[] bJoint, out string measured, out int regions)
    {
        regions = 0;
        var bad = new List<string>();
        var sb = new StringBuilder();
        if (_jointsLoaded)
            foreach (var (name, up, lo) in BindHinges)
            {
                if (!bind.Has[up] || !bind.Has[lo] || _bones[up] == null || _bones[lo] == null) continue;
                if (_jointKind[lo] != 'h' || _hingeAxisRest[lo].sqrMagnitude < 0.5f) continue;
                Vector3 h = _hingeAxisRest[lo];
                Vector3 onLower = bind.Q[lo] * (Quaternion.Inverse(bRot[lo]) * h); // the lower bone's own frame
                Vector3 fromUpper = _bones[up].rotation * (bind.Q[up] * (Quaternion.Inverse(bRot[up]) * h));
                bool knee = lo == 2 || lo == 5;
                int region = knee ? LegsRegion : lo == 15 ? LArmRegion : RArmRegion;
                float sides = Vector3.Angle(fromUpper, _bones[lo].rotation * onLower);
                sb.Append($" {name} {sides:F0}");
                if (sides > HingeSidesMax) { bad.Add($"{name} axis {sides:F0} deg apart from the {(knee ? "thigh and the calf" : "upper arm and the forearm")}"); regions |= region; }
                // which way the knee bends. Thigh and calf both bound half a turn agree with each other and with
                // EFT's axis (either way), and still bend the knee backwards (bot ...93c3f prone: axis 178 off his
                // bend). The way the doll's knee bends, on his thigh, against his own bend when the knee is bent,
                // else his hip line (a knee bends about an axis pointing to his right; in lying poses his thighs
                // turn too far for that to hold, up to 165 deg, but his bend is there to read then)
                if (knee && _bones[lo + 1] != null)
                {
                    float flex = Mathf.Abs(_hingeHi[lo]) >= Mathf.Abs(_hingeLo[lo]) ? 1f : -1f;
                    Vector3 u = _bones[lo].position - _bones[up].position, v = _bones[lo + 1].position - _bones[lo].position;
                    bool bent = Vector3.Angle(u, v) > 20f;
                    Vector3 his = bent ? Vector3.Cross(u, v) : _bones[4].position - _bones[1].position;
                    float way = Vector3.Angle(flex * fromUpper, his);
                    sb.Append($" bends {way:F0} off his {(bent ? "bend" : "hip line")}");
                    if (way > 90f) { bad.Add($"{name} bends the wrong way ({way:F0} deg off his {(bent ? "bend" : "hip line")})"); regions |= region; }
                }
                var cj = _bones[lo].GetComponent<CharacterJoint>();
                if (cj == null || cj.axis.sqrMagnitude < 1e-6f) continue;
                float eft = Vector3.Angle(onLower, cj.axis);
                eft = Mathf.Min(eft, 180f - eft);
                sb.Append($" (EFT's {eft:F0})");
                if (eft > EftAxisMax) { bad.Add($"{name} axis {eft:F0} deg off EFT's on the {(knee ? "calf" : "forearm")}"); regions |= region; }
            }
        if (bind.Has[0] && _bones[0] != null && _bones[1] != null && _bones[4] != null)
        {
            Vector3 doll = bind.Q[0] * (Quaternion.Inverse(bRot[0]) * (bJoint[4] - bJoint[1]));
            Vector3 his = Quaternion.Inverse(_bones[0].rotation) * (_bones[4].position - _bones[1].position);
            float hip = Vector3.Angle(doll, his);
            sb.Append($" hip line {hip:F0}");
            if (hip > HipLineMax) { bad.Add($"hip line {hip:F0} deg off his (pelvis turned)"); regions |= LegsRegion; }
        }
        measured = sb.ToString();
        return bad.Count == 0 ? null : string.Join(", ", bad);
    }

    // what a failed check replaces: the pelvis and legs, or one arm below the shoulder. The spine's
    // binding is kept (it is fitted along his spine at his own build's joint fractions).
    private const int LegsRegion = 1, LArmRegion = 2, RArmRegion = 4;
    private static bool InRegion(int k, int regions) =>
        (regions & LegsRegion) != 0 && k <= 6 || (regions & LArmRegion) != 0 && k >= 14 && k <= 16 || (regions & RArmRegion) != 0 && k >= 18 && k <= 20;

    private static bool AllBound(RigBinding bind)
    {
        for (int k = 0; k < Parts; k++) if (!bind.Has[k]) return false;
        return true;
    }

    private string PartBone(int k) => k >= 0 && _bones[k] != null ? _bones[k].name.Replace("Base Human", "") : "part " + k;

    private static float WorstPart(RigBinding a, RigBinding b, out int at)
    {
        float worst = 0f; at = -1;
        for (int k = 0; k < Parts; k++)
        {
            if (!a.Has[k] || !b.Has[k]) continue;
            float d = Quaternion.Angle(a.Q[k], b.Q[k]);
            if (d > worst) { worst = d; at = k; }
        }
        return worst;
    }

    private string PartsOff(RigBinding a, RigBinding b, float over)
    {
        var sb = new StringBuilder();
        for (int k = 0; k < Parts; k++)
        {
            if (!a.Has[k] || !b.Has[k]) continue;
            float d = Quaternion.Angle(a.Q[k], b.Q[k]);
            if (d > over) sb.Append($" {PartBone(k)} {d:F0}");
        }
        return sb.Length > 0 ? sb.ToString() : $" none past {over:F0}";
    }

    private static void LoadKnownBinding()
    {
        if (_knownBind != null || _knownBindTried || _pluginDir == null) return;
        _knownBindTried = true;
        try
        {
            string path = Path.Combine(_pluginDir, KnownBindFile);
            if (!File.Exists(path)) return;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var b = new RigBinding { Valid = true };
            foreach (string line in File.ReadAllLines(path))
            {
                string[] t = line.Trim().Split(' ');
                if (t[0] == "from") { b.Source = line.Trim().Substring(5); continue; }
                if (t[0] == "spine" && t.Length == 2) { b.SpineFitted = t[1] == "1"; continue; }
                if (t.Length != 5 || !int.TryParse(t[0], out int k) || k < 0 || k >= Parts) continue;
                float F(int i) => float.Parse(t[i], ci);
                b.Q[k] = new Quaternion(F(1), F(2), F(3), F(4));
                b.Has[k] = true;
            }
            if (!AllBound(b)) { EuphoriaPlugin.Log($"Delirium: {KnownBindFile} incomplete - not used"); return; }
            _knownBind = b;
        }
        catch (Exception e) { EuphoriaPlugin.Log("Delirium: " + KnownBindFile + ": " + e.Message); }
    }

    private static void SaveKnownBinding(RigBinding bind, string bot)
    {
        if (_pluginDir == null) return;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var b = new RigBinding { Valid = true, SpineFitted = bind.SpineFitted, Source = $"bot {bot}, {DateTime.Now:yyyy-MM-dd HH:mm}" };
        var sb = new StringBuilder("# each doll part's turn on its SPT bone (bone-local), from a clean outfit that passed the skeleton check\n");
        sb.Append("from ").Append(b.Source).Append('\n').Append("spine ").Append(bind.SpineFitted ? 1 : 0).Append('\n');
        for (int k = 0; k < Parts; k++)
        {
            Quaternion q = b.Q[k] = bind.Q[k];
            b.Has[k] = true;
            sb.Append(k).Append(' ').Append(q.x.ToString("R", ci)).Append(' ').Append(q.y.ToString("R", ci)).Append(' ')
              .Append(q.z.ToString("R", ci)).Append(' ').Append(q.w.ToString("R", ci)).Append('\n');
        }
        try
        {
            File.WriteAllText(Path.Combine(_pluginDir, KnownBindFile), sb.ToString());
            _knownBind = b;
            EuphoriaPlugin.Log($"Delirium: known-good rig binding saved ({KnownBindFile}, {b.Source})");
        }
        catch (Exception e) { EuphoriaPlugin.Log("Delirium: " + KnownBindFile + ": " + e.Message); }
    }
}
