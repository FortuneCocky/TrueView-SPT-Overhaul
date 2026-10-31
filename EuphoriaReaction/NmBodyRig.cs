using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using EFT;
using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// How each doll part sits on its SPT bone, independent of pose:
///   partRot = boneRot * Q[k]        partPos = bonePos + boneRot * P[k]
/// so the doll can be placed on SPT's skeleton in any pose (standing, crouched, prone).
/// The doll's four spine parts don't have one bone each (EFT has three spine bones), so their
/// "bones" are points fitted along SPT's spine curve (see FitSpine), in rest and live alike.
/// </summary>
internal sealed class RigBinding
{
    public readonly Quaternion[] Q = new Quaternion[21];
    public readonly Vector3[] P = new Vector3[21];
    public readonly bool[] Has = new bool[21];
    public readonly float[] SpineFrac = new float[21];
    public string Source = "none";
    public bool Valid, SpineFitted;
}

/// <summary>
/// Rig binding and the dev-panel Rig check.
///
/// Bind-pose binding (preferred): SPT's rest skeleton comes from the body meshes' bind poses
/// (the same for every bot on that skeleton, whatever he is doing); the doll's rest comes from
/// the scaled build's own part frames. The two rest poses differ (the doll's vs EFT's rig), so
/// each part is first turned so its segment (joint -> next joint) and a shared body-forward
/// reference match SPT's, then its offset from the bone is read off. Fallback: the standing
/// calibration recorded at a standing takeover (crouch_calib.txt).
/// </summary>
internal partial class NmBody
{
    // part -> part its segment runs to (-1: an end part, which takes its parent's alignment)
    private static readonly int[] SegChild = { 7, 2, 3, -1, 5, 6, -1, 8, 9, 10, 11, 12, -1, 14, 15, 16, -1, 18, 19, 20, -1 };
    private static readonly int[] SegParent = { -1, 0, 1, 2, 0, 4, 5, 0, 7, 8, 9, 10, 11, 10, 13, 14, 15, 10, 17, 18, 19 };
    private const int SpineBase = 7, SpineTop = 10, Neck = 11;

    private bool _rigCheck;
    internal bool RigChecking => _rigCheck;

    /// The binding for this bot's skeleton and the current build (_bones and _build must be set).
    private RigBinding ComputeBinding()
    {
        var bind = new RigBinding();
        string why = null;
        int id = _p.GetInstanceID();
        if (BindPoseRest(out Vector3[] restPos, out Quaternion[] restRot, ref why))
        {
            bool standing = false;
            // on the ground too: mid-jump his legs are tucked, not a standing rest
            try { standing = !_p.MovementContext.IsInPronePose && _p.MovementContext.PoseLevel >= 0.95f && _p.MovementContext.IsGrounded; } catch { }
            if (_legsFromLive && !standing)
            {
                // a bent leg taken as "rest" twists that leg in every pose: never from a crouch/prone
                if (_standingBindings.TryGetValue(id, out RigBinding kept))
                    return kept;
                why = "his leg bones have no bind pose and he isn't standing to take them from (rig check or shoot him once while he stands)";
            }
            else if (FromRest(bind, restPos, restRot, ref why))
            {
                bind.Source = "bind pose" + (_restFromLive > 0 ? $" ({_restFromLive} bone(s) without one: taken from their live pose under a bound parent{(_legsFromLive ? ", legs while standing" : "")})" : "")
                              + (bind.SpineFitted ? ", spine fitted along SPT's spine" : "");
                bind.Valid = true;
                if (_legsFromLive)
                {
                    var kept = new RigBinding { Source = bind.Source + " [kept from when he stood]", Valid = true, SpineFitted = bind.SpineFitted };
                    Array.Copy(bind.Q, kept.Q, Parts); Array.Copy(bind.P, kept.P, Parts);
                    Array.Copy(bind.Has, kept.Has, Parts); Array.Copy(bind.SpineFrac, kept.SpineFrac, Parts);
                    _standingBindings[id] = kept;
                }
                return bind;
            }
        }
        if (CrouchReady)
        {
            for (int k = 0; k < Parts; k++)
            {
                bind.Q[k] = Quaternion.Inverse(_calBoneRot[k]) * _calPartRot[k];
                bind.P[k] = Quaternion.Inverse(_calBoneRot[k]) * (_calPartPos[k] - _calBonePos[k]);
                bind.Has[k] = _bones[k] != null;
            }
            bind.Source = $"standing calibration (bind pose unusable: {why})";
            bind.Valid = true;
            return bind;
        }
        bind.Source = $"none (bind pose unusable: {why}; no standing calibration yet)";
        return bind;
    }

    // Mapped bones whose rest came from their live pose under a bound ancestor (last BindPoseRest),
    // and whether any of them is a leg bone.
    private int _restFromLive;
    private bool _legsFromLive;

    // A leg's live pose is only a fair "rest" when he stands; bindings that needed one are kept per
    // bot from a standing moment, for when he's crouched or prone later.
    private static readonly Dictionary<int, RigBinding> _standingBindings = new Dictionary<int, RigBinding>();

    /// SPT's rest skeleton for the mapped bones, from the skinned meshes' bind poses, placed at
    /// each mesh's current root (so it stands where the bot is). Gear can replace the meshes that
    /// skin some bones (seen: a whole arm chain), so a bone without a bind pose is hung, in its
    /// current local shape, under its nearest ancestor that has one; everything stays in one frame.
    private bool BindPoseRest(out Vector3[] pos, out Quaternion[] rot, ref string why)
    {
        pos = new Vector3[Parts];
        rot = new Quaternion[Parts];
        _restFromLive = 0;
        _legsFromLive = false;
        var bindWorld = new Dictionary<Transform, Matrix4x4>();
        try
        {
            foreach (SkinnedMeshRenderer smr in _p.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Mesh mesh = smr.sharedMesh;
                Transform[] bones = smr.bones;
                if (mesh == null || bones == null) continue;
                Matrix4x4[] bp = mesh.bindposes;
                if (bp == null || bp.Length != bones.Length) continue;
                Matrix4x4 root = smr.transform.localToWorldMatrix;
                for (int i = 0; i < bones.Length; i++)
                    if (bones[i] != null && !bindWorld.ContainsKey(bones[i])) bindWorld[bones[i]] = root * bp[i].inverse;
            }
        }
        catch (Exception e) { why = "bind poses unreadable (" + e.GetType().Name + ")"; return false; }

        var missing = new List<string>();
        for (int k = 0; k < Parts; k++)
        {
            Transform b = _bones[k];
            if (b == null) continue;
            if (!bindWorld.TryGetValue(b, out Matrix4x4 m))
            {
                Transform a = b.parent;
                while (a != null && !bindWorld.ContainsKey(a)) a = a.parent;
                if (a == null) { missing.Add(b.name); continue; }
                // the bone's current shape under that ancestor, hung on the ancestor's rest
                m = bindWorld[a] * (a.worldToLocalMatrix * b.localToWorldMatrix);
                _restFromLive++;
                if (k >= 1 && k <= 6) _legsFromLive = true;
            }
            pos[k] = m.GetColumn(3);
            rot[k] = Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));
        }
        if (missing.Count > 0) { why = "no bind pose (or bound ancestor) for " + string.Join(", ", missing.Take(4)) + (missing.Count > 4 ? "..." : ""); return false; }
        FixLegAim(pos, rot);
        FixLegRoll(rot);
        return true;
    }

    /// A bone points at its child the same way in its own frame whatever the animation does (the
    /// child's local offset is fixed), so the rest must agree with the live skeleton on it. Some scav
    /// outfits' meshes give a shin a rest that points elsewhere (up the thigh, sideways): the roll check
    /// below can't see that, the knee hinge came out wrong and the joint limits turned his shins
    /// 160-180 deg (rig check), and the doll fought them until it blew up. Off by more than 25 deg:
    /// the rest is turned so it points the way the bone really does; the roll check then runs on that.
    private void FixLegAim(Vector3[] pos, Quaternion[] rest)
    {
        var sb = new StringBuilder();
        _legAimFixed = 0;
        foreach (var (k, _, c) in LegRoll)
        {
            Transform b = _bones[k], ch = _bones[c];
            if (b == null || ch == null) continue;
            Vector3 live = Quaternion.Inverse(b.rotation) * (ch.position - b.position);
            Vector3 restDir = Quaternion.Inverse(rest[k]) * (pos[c] - pos[k]);
            if (live.sqrMagnitude < 1e-8f || restDir.sqrMagnitude < 1e-8f) continue;
            float off = Vector3.Angle(live, restDir);
            sb.Append(' ').Append(b.name.Replace("Base Human", "")).Append(' ').Append(off.ToString("F0"));
            if (off <= 25f) continue;
            rest[k] = rest[k] * Quaternion.FromToRotation(live, restDir); // now rest[k] * live == the rest direction
            sb.Append("(fixed)");
            _legAimFixed++;
            EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} {b.name} rest pose points {off:F0} deg off the skeleton (outfit mesh) - corrected");
        }
        EuphoriaPlugin.LogV($"Delirium: {_p.ProfileId} leg rest aim vs skeleton (deg):{sb}");
    }

    // leg bone -> its parent and the bone it points at (feet: no child to measure a roll about)
    private static readonly (int bone, int parent, int child)[] LegRoll = { (1, 0, 2), (2, 1, 3), (4, 0, 5), (5, 4, 6) };
    private int _legRollFixed, _legAimFixed;
    private static readonly Dictionary<int, Quaternion[]> _legRollStanding = new Dictionary<int, Quaternion[]>(); // per bot, rest-local roll fixes found standing

    /// A leg bone's rest roll (about its own length) against its parent's must agree with the live
    /// skeleton's: a knee bends and a hip swings, neither rolls a shin half a turn. Some scav outfits'
    /// meshes give a shin (or thigh) a rest rolled ~180 deg; the doll's knee hinge then came out
    /// flipped and his legs broke backwards on the joint limits (rig check: shin 160-180 deg "joint
    /// limit"). A roll past 90 deg is the mesh's, not his: the rest takes the live roll instead.
    /// Only measured while he stands: crouched or prone, a hip and knee really do turn the leg against its
    /// parent, and taking that as the mesh's put the pose into the binding (joint report: knees 88-138 deg
    /// off their hinge on prone and crouched takeovers, fine on the same bot standing). What was found
    /// standing is kept per bot and reused. Before he has stood, his rest is checked against the leg rest
    /// recorded at a standing takeover (any bot: they share EFT's one skeleton), saved to
    /// leg_rest_reference.txt; a bot forced into a crouch with a rolled outfit mesh otherwise kept its
    /// raw bind pose and flipped both knees (joint report: 160-173 deg off).
    private void FixLegRoll(Quaternion[] rest)
    {
        _legRollFixed = 0;
        int id = _p.GetInstanceID();
        bool standing = false;
        try { standing = !_p.MovementContext.IsInPronePose && _p.MovementContext.PoseLevel >= 0.95f && _p.MovementContext.IsGrounded; } catch { }
        if (!standing)
        {
            if (_legRollStanding.TryGetValue(id, out Quaternion[] kept))
            {
                foreach (var (k, _, _) in LegRoll)
                    if (Quaternion.Angle(kept[k], Quaternion.identity) > 0.1f) { rest[k] = rest[k] * kept[k]; _legRollFixed++; }
                // what he stood with can still be off (the odd outfit the first reference came from: 169-174 deg
                // off at every crouched takeover after he'd stood)
                SnapToReference(rest, "crouched or prone, fixes kept from when he stood");
                return;
            }
            if (!LoadLegReference()) return;
            // the whole relation, not just a roll past 90: a crouched bot passed that check at 60-90 deg and
            // still had both knees 166-171 deg off their hinge (joint report). Thigh first, so the shin's
            // check runs against the thigh as fixed. Only against a reference clean standing bots agreed on:
            // the first one saved came from an odd bot (every other bot 34/123 deg from it) and snapping to
            // it bent crouched knees backwards. Unconfirmed, only a roll past 90 is corrected, as before.
            var sb = new StringBuilder();
            foreach (var (k, pa, c) in LegRoll)
            {
                if (_bones[k] == null) continue;
                float off = Quaternion.Angle(Quaternion.Inverse(rest[pa]) * rest[k], _legRef[k]);
                float roll = RollAgainst(rest, k, pa, c, _legRef[k], out Vector3 axis);
                sb.Append(' ').Append(_bones[k].name.Replace("Base Human", "")).Append($" {off:F0} (roll {roll:+0;-0})");
                if (_legRefConfirmed && off > LegRefSnap) rest[k] = rest[pa] * _legRef[k];
                else if (!_legRefConfirmed && Mathf.Abs(roll) > 90f) rest[k] = rest[k] * Quaternion.AngleAxis(roll, axis);
                else continue;
                _legRollFixed++;
                sb.Append(" fixed");
            }
            EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} not standing - leg rest vs the {(_legRefConfirmed ? "confirmed" : "unconfirmed")} standing reference (deg):{sb}");
            return;
        }
        var fix = new Quaternion[Parts];
        for (int k = 0; k < Parts; k++) fix[k] = Quaternion.identity;
        _legRollStanding[id] = fix;
        foreach (var (k, pa, c) in LegRoll)
        {
            if (_bones[k] == null || _bones[pa] == null) continue;
            float roll = RollAgainst(rest, k, pa, c, Quaternion.Inverse(_bones[pa].rotation) * _bones[k].rotation, out Vector3 axis);
            // standing, a leg bone never rolls this far against its parent: past it, it's the mesh's. Was 90, and
            // two bots rolled exactly 90 on every leg bone passed as clean, became the reference and folded crouched
            if (Mathf.Abs(roll) <= StandingRollMax) continue;
            fix[k] = Quaternion.AngleAxis(roll, axis);
            rest[k] = rest[k] * fix[k];
            _legRollFixed++;
            EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} {_bones[k].name} rest pose rolled {roll:F0} deg against the skeleton (outfit mesh) - corrected");
        }
        // the reference: only from a clean outfit (nothing corrected, every leg bone bound), and trusted once
        // two clean bots agree on it. One that disagrees with it is kept as a candidate; a second clean bot
        // agreeing with the candidate replaces it.
        bool clean = _legRollFixed == 0 && _legAimFixed == 0 && !_legsFromLive;
        var r = new Quaternion[Parts];
        foreach (var (k, pa, _) in LegRoll) r[k] = Quaternion.Inverse(rest[pa]) * rest[k];
        LoadLegReference();
        float vsRef = _legRef != null ? LegRelOff(r, _legRef) : -1f;
        string note;
        if (!clean) note = "corrected outfit, not used for the reference";
        else if (_legRef == null) { SaveLegReference(r, false); note = "first reference saved"; }
        else if (vsRef <= LegRefAgree)
        {
            note = _legRefConfirmed ? "agrees" : "agrees - reference confirmed";
            if (!_legRefConfirmed) SaveLegReference(_legRef, true);
        }
        else if (_legCandidate != null && _legCandidateBot != _p.ProfileId && LegRelOff(r, _legCandidate) <= LegRefAgree)
        {
            SaveLegReference(r, true);
            _legCandidate = null;
            note = "two clean bots agree against it - reference replaced";
        }
        else { _legCandidate = r; _legCandidateBot = _p.ProfileId; note = "disagrees - kept as a candidate"; }
        EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} standing - leg rest {(vsRef >= 0f ? $"{vsRef:F0} deg" : "n/a")} from the {(_legRefConfirmed ? "confirmed" : "unconfirmed")} reference: {note}");
        if (!clean) SnapToReference(rest, "standing, corrected outfit");
    }

    /// Against a confirmed reference, any leg bone's rest further than LegRefSnap from it takes it (thigh
    /// first, so the shin is checked against the thigh as fixed).
    private void SnapToReference(Quaternion[] rest, string when)
    {
        if (!_legRefConfirmed || _legRef == null) return;
        var sb = new StringBuilder();
        foreach (var (k, pa, _) in LegRoll)
        {
            if (_bones[k] == null) continue;
            float off = Quaternion.Angle(Quaternion.Inverse(rest[pa]) * rest[k], _legRef[k]);
            if (off <= LegRefSnap) continue;
            rest[k] = rest[pa] * _legRef[k];
            _legRollFixed++;
            sb.Append(' ').Append(_bones[k].name.Replace("Base Human", "")).Append($" {off:F0}");
        }
        if (sb.Length > 0) EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} {when} - leg rest set to the confirmed reference (was off, deg):{sb}");
    }

    private const float LegRefSnap = 30f;  // deg: a leg rest further than this from the confirmed reference takes the reference
    private const float LegRefAgree = 10f; // deg: two leg rests this close are the same skeleton
    private const float StandingRollMax = 45f; // deg: a standing leg bone's roll against its rest past this is the outfit mesh's
    private static bool _legRefConfirmed;
    private static Quaternion[] _legCandidate;
    private static string _legCandidateBot; // the candidate's bot: the same bot taken over twice is not a second opinion

    /// The largest angle between two sets of leg rest relations (leg bones only).
    private static float LegRelOff(Quaternion[] a, Quaternion[] b)
    {
        float m = 0f;
        foreach (var (k, _, _) in LegRoll) m = Mathf.Max(m, Quaternion.Angle(a[k], b[k]));
        return m;
    }

    /// Roll (deg, about the bone's own length) from leg bone k's rest against its parent's to `target`
    /// (a parent-local rotation: his live pose, or the shared reference). 0 when the bones are missing.
    private float RollAgainst(Quaternion[] rest, int k, int pa, int c, Quaternion target, out Vector3 axis)
    {
        axis = Vector3.zero;
        Transform b = _bones[k], ch = _bones[c];
        if (b == null || ch == null) return 0f;
        axis = Quaternion.Inverse(b.rotation) * (ch.position - b.position); // its length, own frame (pose-independent)
        if (axis.sqrMagnitude < 1e-8f) return 0f;
        axis.Normalize();
        Quaternion d = Quaternion.Inverse(Quaternion.Inverse(rest[pa]) * rest[k]) * target; // rest -> target, in its own frame
        float roll = 2f * Mathf.Atan2(Vector3.Dot(new Vector3(d.x, d.y, d.z), axis), d.w) * Mathf.Rad2Deg;
        if (roll > 180f) roll -= 360f;
        if (roll < -180f) roll += 360f;
        return roll;
    }

    private static Quaternion[] _legRef;      // leg bones' rest against their parents, from a standing takeover
    private static bool _legRefTried;
    private const string LegRefFile = "leg_rest_reference.txt";

    private static bool LoadLegReference()
    {
        if (_legRef != null) return true;
        if (_legRefTried || _pluginDir == null) return false;
        _legRefTried = true;
        try
        {
            string path = Path.Combine(_pluginDir, LegRefFile);
            if (!File.Exists(path)) return false;
            var r = new Quaternion[Parts];
            int n = 0;
            bool confirmed = false;
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.Trim() == "confirmed") { confirmed = true; continue; }
                string[] t = line.Split(' ');
                if (t.Length != 5) continue;
                float F(int i) => float.Parse(t[i], System.Globalization.CultureInfo.InvariantCulture);
                int k = int.Parse(t[0], System.Globalization.CultureInfo.InvariantCulture);
                if (k < 0 || k >= Parts) continue;
                r[k] = new Quaternion(F(1), F(2), F(3), F(4));
                n++;
            }
            if (n < LegRoll.Length) return false;
            _legRef = r;
            _legRefConfirmed = confirmed;
            return true;
        }
        catch (Exception e) { EuphoriaPlugin.Log("Delirium: " + LegRefFile + ": " + e.Message); return false; }
    }

    /// `r`: the leg bones' rest relations to their parents. Confirmed: two clean standing bots agreed on it.
    private static void SaveLegReference(Quaternion[] r, bool confirmed)
    {
        if (_pluginDir == null) return;
        var sb = new StringBuilder();
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        if (confirmed) sb.Append("confirmed\n");
        foreach (var (k, _, _) in LegRoll)
        {
            sb.Append(k).Append(' ').Append(r[k].x.ToString("R", ci)).Append(' ').Append(r[k].y.ToString("R", ci)).Append(' ')
              .Append(r[k].z.ToString("R", ci)).Append(' ').Append(r[k].w.ToString("R", ci)).Append('\n');
        }
        try
        {
            File.WriteAllText(Path.Combine(_pluginDir, LegRefFile), sb.ToString());
            _legRef = r;
            _legRefConfirmed = confirmed;
            EuphoriaPlugin.Log($"Delirium: leg rest reference saved from a standing takeover ({LegRefFile}, {(confirmed ? "confirmed" : "unconfirmed")})");
        }
        catch (Exception e) { EuphoriaPlugin.Log("Delirium: " + LegRefFile + ": " + e.Message); }
    }

    /// Where each doll spine joint sits along its own spine (base 0 -> neck 1), from the build's rest.
    private bool SpineFractions(float[] frac)
    {
        var j = new Vector3[Parts];
        for (int k = SpineBase; k <= Neck; k++)
        {
            if (nm_build_part_joint(_build, k, _g3) < 0) return false;
            j[k] = ToUnity(_g3[0], _g3[1], _g3[2]);
        }
        float total = 0f;
        for (int k = SpineBase + 1; k <= Neck; k++) total += (j[k] - j[k - 1]).magnitude;
        if (total < 1e-4f) return false;
        float run = 0f;
        frac[SpineBase] = 0f;
        for (int k = SpineBase + 1; k <= Neck; k++) { run += (j[k] - j[k - 1]).magnitude; frac[k] = run / total; }
        return true;
    }

    /// Replace the doll spine parts' "bones" (SpineBase+1 .. SpineTop) with points at the doll's own
    /// joint fractions along SPT's spine curve: first spine bone -> every distinct mapped spine
    /// bone -> neck. Rotation blends between the SPT bones on either side of each point.
    private static bool FitSpine(Vector3[] pos, Quaternion[] rot, bool[] ok, Transform[] bones, float[] frac)
    {
        if (!ok[SpineBase] || !ok[Neck]) return false;
        var chain = new List<int> { SpineBase };
        for (int k = SpineBase + 1; k <= SpineTop; k++)
            if (ok[k] && bones[k] != null && !chain.Any(c => bones[c] == bones[k])) chain.Add(k);
        chain.Add(Neck);
        var len = new float[chain.Count];
        for (int i = 1; i < chain.Count; i++) len[i] = len[i - 1] + (pos[chain[i]] - pos[chain[i - 1]]).magnitude;
        float total = len[len.Length - 1];
        if (total < 1e-4f) return false;

        var outPos = new Vector3[Parts]; var outRot = new Quaternion[Parts];
        for (int k = SpineBase + 1; k <= SpineTop; k++)
        {
            float s = frac[k] * total;
            int i = 1;
            while (i < chain.Count - 1 && len[i] < s) i++;
            float t = Mathf.Clamp01((s - len[i - 1]) / Mathf.Max(1e-5f, len[i] - len[i - 1]));
            outPos[k] = Vector3.Lerp(pos[chain[i - 1]], pos[chain[i]], t);
            outRot[k] = Quaternion.Slerp(rot[chain[i - 1]], rot[chain[i]], t);
        }
        for (int k = SpineBase + 1; k <= SpineTop; k++) { pos[k] = outPos[k]; rot[k] = outRot[k]; ok[k] = true; }
        return true;
    }

    /// Fill the binding from SPT's rest skeleton and the build's rest frames.
    private bool FromRest(RigBinding bind, Vector3[] restPos, Quaternion[] restRot, ref string why)
    {
        // the build's rest: part frames and joints (the root's joint is its centre)
        var bPos = new Vector3[Parts]; var bRot = new Quaternion[Parts]; var bJoint = new Vector3[Parts];
        for (int k = 0; k < Parts; k++)
        {
            if (nm_build_part_frame(_build, k, _f3, _q4) < 0 || nm_build_part_joint(_build, k, _g3) < 0) { why = "build frames unreadable"; return false; }
            bPos[k] = ToUnity(_f3[0], _f3[1], _f3[2]);
            bRot[k] = ToUnity(_q4[0], _q4[1], _q4[2], _q4[3]);
            bJoint[k] = ToUnity(_g3[0], _g3[1], _g3[2]);
        }
        RefitHingeAxes(bRot, _p.ProfileId);

        // SPT's rest with the doll spine fitted along it
        var ok = new bool[Parts];
        for (int k = 0; k < Parts; k++) ok[k] = _bones[k] != null;
        bind.SpineFitted = SpineFractions(bind.SpineFrac) && FitSpine(restPos, restRot, ok, _bones, bind.SpineFrac);

        // one body frame per rig: hips left->right, pelvis->neck up, forward from the two
        if (!BodyFrame(restPos[4] - restPos[1], restPos[Neck] - restPos[0], out Vector3 sFwd, out Vector3 sUp)
            || !BodyFrame(bJoint[4] - bJoint[1], bJoint[Neck] - bJoint[0], out Vector3 dFwd, out Vector3 dUp))
        { why = "hips or spine degenerate in a rest pose"; return false; }

        // per-part alignment: build rest -> SPT rest, matching segment direction and body forward
        var align = new Quaternion[Parts];
        for (int k = 0; k < Parts; k++)
        {
            int c = SegChild[k];
            while (c >= 0 && !ok[c]) c = SegChild[c]; // skip anything still unplaced
            if (c < 0 || !ok[k])
            {
                align[k] = SegParent[k] >= 0 ? align[SegParent[k]] : Quaternion.identity;
                continue;
            }
            Quaternion s = Frame(restPos[c] - restPos[k], sFwd, sUp);
            Quaternion d = Frame(bJoint[c] - bJoint[k], dFwd, dUp);
            align[k] = s * Quaternion.Inverse(d);
            int elbow = k == 14 || k == 15 ? 15 : k == 18 || k == 19 ? 19 : -1;
            if (elbow > 0) align[k] = ElbowRoll(k, elbow, restPos[c] - restPos[k], restRot, align[k]);
        }

        for (int k = 0; k < Parts; k++)
        {
            if (!ok[k]) continue;
            Quaternion partRest = align[k] * bRot[k];
            Vector3 partRestPos = restPos[k] + align[k] * (bPos[k] - bJoint[k]);
            Quaternion inv = Quaternion.Inverse(restRot[k]);
            bind.Q[k] = inv * partRest;
            bind.P[k] = inv * (partRestPos - restPos[k]);
            bind.Has[k] = true;
        }
        return true;
    }

    /// The body-forward alignment sets an arm part's roll about its length, and the doll's rest and EFT's hold
    /// the forearm at different rolls: the doll's elbow hinge came out 64-77 deg off the plane SPT's own
    /// arm bends in, on every bot (joint report), and the fit dropped the rest of each elbow pose. The upper
    /// arm and forearm (both carry the elbow's axis: parent side and child side) are rolled about their own
    /// segment so the doll's elbow axis lands on EFT's ragdoll elbow axis on SPT's forearm (bone-local, so
    /// pose-independent; it agreed with SPT's bend plane within a few degrees). Its sign is flexion's: the
    /// live bend (upper arm x forearm) when he holds the elbow bent, else the doll axis's own side.
    private Quaternion ElbowRoll(int k, int elbow, Vector3 seg, Quaternion[] restRot, Quaternion align)
    {
        Transform fore = _bones[elbow], upper = _bones[SegParent[elbow]], hand = elbow + 1 < Parts ? _bones[elbow + 1] : null;
        var cj = fore != null ? fore.GetComponent<CharacterJoint>() : null;
        if (cj == null || !_jointsLoaded || _hingeAxisRest[elbow].sqrMagnitude < 0.5f || seg.sqrMagnitude < 1e-8f) return align;
        Vector3 target = restRot[elbow] * cj.axis.normalized; // SPT's rest, world
        // the doll's elbow axis in its rest frame, carried by this part's alignment (the part's own rest
        // frame is the build's; at rest the elbow axis is the same vector seen from either side)
        Vector3 doll = align * _hingeAxisRest[elbow];
        float sign = Vector3.Dot(doll, target) < 0f ? -1f : 1f;
        string by = "doll side";
        if (upper != null && hand != null)
        {
            Vector3 up = fore.position - upper.position, lo = hand.position - fore.position;
            if (Vector3.Angle(up, lo) > 25f)
            {
                sign = Vector3.Dot(fore.rotation * cj.axis, Vector3.Cross(up, lo)) < 0f ? -1f : 1f;
                by = "his bend";
            }
        }
        target *= sign;
        Vector3 ax = seg.normalized;
        Vector3 a = Vector3.ProjectOnPlane(doll, ax), b = Vector3.ProjectOnPlane(target, ax);
        if (a.sqrMagnitude < 1e-6f || b.sqrMagnitude < 1e-6f) return align;
        float roll = Vector3.SignedAngle(a, b, ax);
        // what a roll can't reach: either axis leaning along the segment (a hinge axis should be square to it)
        float left = Vector3.Angle(Quaternion.AngleAxis(roll, ax) * doll, target);
        EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} {(k == elbow ? fore.name + " (forearm)" : upper?.name + " (upper arm)")} rolled {roll:F0} deg for the elbow axis, " +
                           $"{left:F0} deg still off EFT's (doll axis {90f - Vector3.Angle(doll, ax):F0} deg along the segment, EFT's {90f - Vector3.Angle(target, ax):F0}; sign by {by})");
        return Mathf.Abs(roll) < 2f ? align : Quaternion.AngleAxis(roll, ax) * align;
    }

    /// The live "bones" the binding applies to: SPT's bones, with the doll spine fitted along
    /// SPT's spine exactly as in the rest binding.
    private bool LiveTargets(RigBinding bind, out Vector3[] pos, out Quaternion[] rot, out bool[] ok)
    {
        pos = new Vector3[Parts]; rot = new Quaternion[Parts]; ok = new bool[Parts];
        for (int k = 0; k < Parts; k++)
        {
            if (_bones[k] == null) continue;
            pos[k] = _bones[k].position; rot[k] = _bones[k].rotation; ok[k] = true;
        }
        if (bind.SpineFitted && !FitSpine(pos, rot, ok, _bones, bind.SpineFrac)) return false;
        return true;
    }

    /// The doll's own skeleton, from the build's rest: each part's parent, rest rotation, and
    /// where its joint sits in its own frame and in its parent's frame.
    private sealed class DollGeom
    {
        public readonly int[] Parent = new int[21];
        public readonly Quaternion[] RestRot = new Quaternion[21];
        public readonly Vector3[] JointInSelf = new Vector3[21];
        public readonly Vector3[] JointInParent = new Vector3[21];
    }

    private DollGeom ReadDollGeom()
    {
        var g = new DollGeom();
        var pos = new Vector3[Parts]; var joint = new Vector3[Parts];
        float[] m = new float[1], r = new float[1], h = new float[1];
        for (int k = 0; k < Parts; k++)
        {
            if (nm_build_part_frame(_build, k, _f3, _q4) < 0 || nm_build_part_joint(_build, k, _g3) < 0) return null;
            pos[k] = ToUnity(_f3[0], _f3[1], _f3[2]);
            g.RestRot[k] = ToUnity(_q4[0], _q4[1], _q4[2], _q4[3]);
            joint[k] = ToUnity(_g3[0], _g3[1], _g3[2]);
            nm_build_part_info(_build, k, m, r, h, out g.Parent[k]);
        }
        for (int k = 0; k < Parts; k++)
        {
            g.JointInSelf[k] = Quaternion.Inverse(g.RestRot[k]) * (joint[k] - pos[k]);
            int pa = g.Parent[k];
            if (pa >= 0) g.JointInParent[k] = Quaternion.Inverse(g.RestRot[pa]) * (joint[k] - pos[pa]);
        }
        return g;
    }

    /// The doll posed on SPT's pose: every part's rotation from the binding, positions built out
    /// from the pelvis through the doll's own joints, so its joints always meet (placing parts on
    /// SPT's bones independently tore them apart wherever the two bodies' proportions differ,
    /// and the solver would snap them together with a pop on the first step).
    private void SeedPoseFK(RigBinding bind, Vector3[] tPos, Quaternion[] tRot, bool[] tOk, DollGeom g,
                            out Vector3[] pos, out Quaternion[] rot)
    {
        pos = new Vector3[Parts]; rot = new Quaternion[Parts];
        var done = new bool[Parts];
        for (int pass = 0; pass < Parts; pass++) // parents first, whatever the part order
            for (int k = 0; k < Parts; k++)
            {
                if (done[k]) continue;
                int pa = g.Parent[k];
                if (pa >= 0 && !done[pa]) continue;
                bool own = bind.Has[k] && tOk[k];
                if (pa < 0)
                {
                    rot[k] = own ? tRot[k] * bind.Q[k] : Quaternion.identity;
                    pos[k] = own ? tPos[k] + tRot[k] * bind.P[k] : Vector3.zero;
                }
                else
                {
                    // no target: keep its rest angle to its parent; with one: the nearest pose its
                    // joint can hold (locked feet stay on the shin, knees stay on their hinge)
                    rot[k] = own ? FitJoint(k, pa, rot[pa], HingeWant(k, pa, bind, tRot, tOk, rot[pa]), g)
                                 : rot[pa] * Quaternion.Inverse(g.RestRot[pa]) * g.RestRot[k];
                    Vector3 joint = pos[pa] + rot[pa] * g.JointInParent[k];
                    pos[k] = joint - rot[k] * g.JointInSelf[k];
                }
                done[k] = true;
            }
    }

    /// Part k's target for the seed. A hinge (knee, elbow) keeps SPT's own bend: measured against SPT's
    /// parent and put on the fitted one. Against the fitted parent, whatever the hip clamp took off the
    /// thigh landed on the knee (joint report: 170 deg flipped knees on deep "dying" crouches).
    private static Quaternion HingeWant(int k, int pa, RigBinding bind, Quaternion[] tRot, bool[] tOk, Quaternion fittedParent)
    {
        Quaternion want = tRot[k] * bind.Q[k];
        if (_jointKind[k] != 'h' || !bind.Has[pa] || !tOk[pa]) return want;
        return fittedParent * Quaternion.Inverse(tRot[pa] * bind.Q[pa]) * want;
    }

    private static bool BodyFrame(Vector3 right, Vector3 up, out Vector3 fwd, out Vector3 upN)
    {
        fwd = Vector3.Cross(right, up);
        upN = up.normalized;
        if (fwd.sqrMagnitude < 1e-8f || up.sqrMagnitude < 1e-8f) return false;
        fwd.Normalize();
        return true;
    }

    // Rotation whose forward is the segment and whose up leans toward the body forward
    // (or body up when the segment itself points forward, e.g. a foot).
    private static Quaternion Frame(Vector3 seg, Vector3 bodyFwd, Vector3 bodyUp)
    {
        Vector3 s = seg.normalized;
        Vector3 hint = Mathf.Abs(Vector3.Dot(s, bodyFwd)) > 0.9f ? bodyUp : bodyFwd;
        return Quaternion.LookRotation(s, hint);
    }

    // ---------------------------------------------------------------- dev-panel Rig check

    /// Place a frozen doll on the bot's current pose through the binding (no physics) and
    /// measure how far each doll joint lands from SPT's bone. Returns the report.
    private static readonly (string, int, int)[] RigSegs =
    {
        ("L thigh", 1, 2), ("L shin", 2, 3), ("R thigh", 4, 5), ("R shin", 5, 6),
        ("L upper arm", 14, 15), ("L forearm", 15, 16), ("R upper arm", 18, 19), ("R forearm", 19, 20),
    };

    /// Dev-only (the dev panel's Rig check): writes the binding, Unity space, one line per bound part
    /// ("k qx qy qz qw px py pz") to rig_binding_dump.txt in the plugin folder, for the playground's matches_mod test.
    private static void DumpBinding(RigBinding bind)
    {
        if (_pluginDir == null) return;
        try
        {
            var sb = new StringBuilder();
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            for (int k = 0; k < Parts; k++)
            {
                if (!bind.Has[k]) continue;
                Quaternion q = bind.Q[k]; Vector3 v = bind.P[k];
                sb.Append(k).Append(' ')
                  .Append(q.x.ToString("R", ci)).Append(' ').Append(q.y.ToString("R", ci)).Append(' ').Append(q.z.ToString("R", ci)).Append(' ').Append(q.w.ToString("R", ci)).Append(' ')
                  .Append(v.x.ToString("R", ci)).Append(' ').Append(v.y.ToString("R", ci)).Append(' ').Append(v.z.ToString("R", ci)).Append('\n');
            }
            File.WriteAllText(Path.Combine(_pluginDir, "rig_binding_dump.txt"), sb.ToString());
            EuphoriaPlugin.Log("Delirium: rig binding dumped to rig_binding_dump.txt (" + bind.Source + ")");
        }
        catch (Exception e) { EuphoriaPlugin.Log("Delirium: rig binding dump failed: " + e.Message); }
    }

    internal string DevRigCheck(Player p)
    {
        if (Active && !_rigCheck) return "bot is in a live Euphoria reaction: release the agent first";
        if (_rigCheck) DevRigCheckEnd();
        if (!_init) return "Delirium not initialised";
        _p = p;
        _bones = MapBones();
        if (_bones == null || _bones[0] == null) return "no usable skeleton on this bot";

        _ownBuild = true;
        _build = ScaledBuild();
        if (_build == IntPtr.Zero) { _ownBuild = false; _build = _baseBuild; }
        RigBinding bind = ComputeBinding();
        if (!bind.Valid || !LiveTargets(bind, out Vector3[] tPos, out Quaternion[] tRot, out bool[] tOk))
        {
            string src = bind.Source;
            if (_ownBuild && _build != IntPtr.Zero) nm_build_destroy(_build);
            _build = IntPtr.Zero; _ownBuild = false;
            return "no binding: " + src;
        }
        DumpBinding(bind); // dev-only: the playground's Rust port is checked against this file

        _world = nm_world_create();
        Vector3 at = p.Transform.position;
        _theta = p.Transform.eulerAngles.y * Mathf.Deg2Rad;
        ToNm(at, _f3);
        _agent = nm_agent_create(_build, _f3, Mathf.PI - _theta, 0f);
        if (_agent == IntPtr.Zero) { DevRigCheckEnd(); return "agent_create failed: " + LastError(); }
        _rigCheck = true;
        enabled = true;

        DollGeom geom = ReadDollGeom();
        if (geom == null) { DevRigCheckEnd(); return "build geometry unreadable"; }
        SeedPoseFK(bind, tPos, tRot, tOk, geom, out Vector3[] sPos, out Quaternion[] sRot);
        var zero = new float[3];
        var q = new float[4];
        int untargeted = 0;
        for (int k = 0; k < Parts; k++)
        {
            if (!bind.Has[k] || !tOk[k]) untargeted++;
            Quaternion r = sRot[k];
            ToNm(sPos[k], _f3);
            q[0] = -r.x; q[1] = -r.z; q[2] = -r.y; q[3] = r.w;
            nm_agent_set_part_state(_agent, k, _f3, q, zero, zero);
        }

        // 1. joint fit: does the engine see the doll's joints meeting? (anything here pops on step 1)
        float fit = 0f;
        for (int k = 1; k < Parts; k++)
            if (Joint(k, out _, out Vector3 pivot, out _))
                fit = Mathf.Max(fit, (pivot - (sPos[k] + sRot[k] * geom.JointInSelf[k])).magnitude * 100f);

        // 2. feet: doll ankles vs SPT's, height only (a body seeded into the floor or in the air jumps)
        float footDy = 0f;
        foreach (int k in new[] { 3, 6 })
            if (_bones[k] != null && Joint(k, out _, out Vector3 ankle, out _))
            {
                float dy = (ankle.y - _bones[k].position.y) * 100f;
                if (Mathf.Abs(dy) > Mathf.Abs(footDy)) footDy = dy;
            }

        // 3. shape difference to SPT's joints (the doll's vs EFT's proportions): information only,
        //    SPT's bones follow the doll's rotations, not its joint positions
        var gaps = new List<(float cm, int k)>();
        for (int k = 1; k < Parts; k++)
        {
            if (_bones[k] == null) continue;
            if (bind.SpineFitted && k > SpineBase && k <= SpineTop) continue;
            if (!Joint(k, out _, out Vector3 pivot, out _)) continue;
            gaps.Add(((pivot - _bones[k].position).magnitude * 100f, k));
        }
        gaps.Sort((a, b) => b.cm.CompareTo(a.cm));
        float shapeMean = gaps.Count > 0 ? gaps.Average(g => g.cm) : 0f;

        float absFoot = Mathf.Abs(footDy);
        string verdict = fit <= 1f && absFoot <= 4f ? "PASS" : fit <= 2f && absFoot <= 8f ? "usable" : "FAIL";
        string pose = "standing";
        try { var mc = p.MovementContext; if (mc != null) pose = mc.IsInPronePose ? "prone" : mc.PoseLevel < 0.85f ? $"crouched ({mc.PoseLevel:F2})" : "standing"; } catch { }
        var sb = new StringBuilder();
        sb.Append($"RIG CHECK {p.ProfileId}  pose {pose}\nbinding: {bind.Source}\n");
        sb.Append($"-> {verdict}   joint fit {fit:F1} cm (must be ~0)   feet {footDy:+0.0;-0.0} cm vs SPT\n");
        if (untargeted > 0) sb.Append($"  {untargeted} part(s) without a target kept their rest angle\n");
        sb.Append($"shape difference to SPT's joints (info): mean {shapeMean:F1} cm\n");
        foreach (var (cm, k) in gaps.Take(5)) sb.Append($"  {DevBoneName(k)}: {cm:F1} cm\n");
        // 4. per limb segment: which way the doll's segment points against SPT's (binding), and how far
        //    the doll's joint limits had to turn the part from what the binding asked (limits)
        // SPT's own legs as the animation (and anything after it) left them: a folded or lifted leg here is
        // SPT's pose, not the binding
        foreach (var (side, h, kn, f) in new[] { ("L", 1, 2, 3), ("R", 4, 5, 6) })
        {
            if (_bones[h] == null || _bones[kn] == null || _bones[f] == null) continue;
            float bend = 180f - Vector3.Angle(_bones[h].position - _bones[kn].position, _bones[f].position - _bones[kn].position);
            float up = RayWorld(_bones[f].position + Vector3.up * 0.3f, Vector3.down, 3f, out RaycastHit gh) ? (_bones[f].position.y - gh.point.y) * 100f : float.NaN;
            sb.Append($"SPT {side} leg: knee bent {bend:F0} deg, foot {up:F0} cm above the ground\n");
        }
        // the doll's knees as the binding asks for them: the bend on the hinge, and what is off it
        // (FitJoint drops that part; a large one means the binding, not the limit, folds the leg)
        foreach (var (side, kn) in new[] { ("L", 2), ("R", 5) })
        {
            int pa = geom.Parent[kn];
            if (pa < 0 || !bind.Has[kn] || !tOk[kn] || _jointKind[kn] != 'h') continue;
            HingeSplit(kn, pa, sRot[pa], HingeWant(kn, pa, bind, tRot, tOk, sRot[pa]), geom, out float hb, out float ho);
            sb.Append($"doll {side} knee: {hb:+0;-0} deg on its hinge (build allows {HingeLoDeg(kn):F0} to {HingeHiDeg(kn):F0}), {ho:F0} deg off the hinge\n");
        }
        sb.Append("segments (doll vs SPT direction / turned by joint limits):\n");
        foreach (var (name, a, b) in RigSegs)
        {
            if (_bones[a] == null || _bones[b] == null || !Joint(a, out _, out Vector3 pa, out _) || !Joint(b, out _, out Vector3 pb, out _)) continue;
            float dir = Vector3.Angle(pb - pa, _bones[b].position - _bones[a].position);
            float lim = bind.Has[a] && tOk[a] ? Quaternion.Angle(sRot[a], tRot[a] * bind.Q[a]) : 0f;
            sb.Append($"  {name}: {dir:F0} deg / {lim:F0} deg{(dir > 20f ? (lim > 15f ? "  <- joint limit" : "  <- binding") : "")}\n");
        }
        string report = sb.ToString().TrimEnd();
        EuphoriaPlugin.Log("Delirium: " + report.Replace("\n", " | "));
        return report;
    }

    /// Worst distance (cm) from a doll spine joint, as its parent part places it, to its fitted
    /// target point on SPT's spine.
    private float SpineCurveGap(Vector3[] target)
    {
        float worst = 0f;
        for (int k = SpineBase + 1; k <= SpineTop; k++)
            if (Joint(k, out _, out Vector3 pivot, out _))
                worst = Mathf.Max(worst, (pivot - target[k]).magnitude * 100f);
        return worst;
    }

    internal void DevRigCheckEnd()
    {
        if (_agent != IntPtr.Zero) nm_agent_destroy(_agent);
        if (_world != IntPtr.Zero) nm_world_destroy(_world);
        if (_build != IntPtr.Zero && _ownBuild) nm_build_destroy(_build);
        _agent = _world = _build = IntPtr.Zero;
        _ownBuild = false;
        _rigCheck = false;
        enabled = false;
    }
}
