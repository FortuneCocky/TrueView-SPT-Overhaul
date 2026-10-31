using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using EFT;
using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// Right-hand grip markers, one per weapon. Where the right palm sits on a gun (position and angle in
/// the gun's own frame) is learned from frames where the bot is plainly holding it, saved to
/// gun_markers.txt, and used at a takeover whenever the live hand isn't on the grip (mid-reload,
/// swapping, an animation that had the hand elsewhere), so the gun doesn't hang off the palm at a
/// strange offset. A sample counts only when it agrees with the previous one (within 3 cm, 12 deg)
/// and, if the gun has a pistol-grip mount, when the palm is on it. The first time each weapon is
/// seen, its hand/grip/IK/marker transforms are logged with their distance from the palm.
/// </summary>
internal static class GunMarkers
{
    private const float Agree = 0.03f, AgreeDeg = 12f, GripGate = 0.22f;
    internal const float IkGate = 0.05f;
    // The palm on a right-hand IK marker, across all guns: EFT's IK puts the palm on it the same way on
    // each, so a gun never seen before still sits in the hand at the right angle.
    private const string AnyIk = "* any gun on its right-hand IK marker";

    internal struct Marker
    {
        public Vector3 Pos;   // palm position in the gun's frame
        public Quaternion Rot; // palm rotation in the gun's frame
        public int Seen;      // agreeing samples
    }

    private static readonly Dictionary<string, Marker> _known = new Dictionary<string, Marker>();
    private static readonly Dictionary<string, Marker> _candidate = new Dictionary<string, Marker>();
    private static readonly HashSet<string> _scanned = new HashSet<string>();
    private static string _file;

    internal static void Load(string dir)
    {
        _file = Path.Combine(dir, "gun_markers.txt");
        try
        {
            if (!File.Exists(_file)) return;
            foreach (string line in File.ReadAllLines(_file))
            {
                string[] f = line.Split('|');
                if (f.Length < 4) continue;
                float[] v = Array.ConvertAll((f[1] + "," + f[2]).Split(','), s => float.Parse(s, CultureInfo.InvariantCulture));
                if (v.Length < 7) continue;
                _known[f[0]] = new Marker { Pos = new Vector3(v[0], v[1], v[2]), Rot = new Quaternion(v[3], v[4], v[5], v[6]), Seen = int.Parse(f[3], CultureInfo.InvariantCulture) };
            }
            EuphoriaPlugin.Log($"Delirium: {_known.Count} gun grip marker(s) loaded");
        }
        catch (Exception e) { EuphoriaPlugin.Log("Delirium: gun markers not loaded: " + e.Message); }
    }

    private static void Save()
    {
        if (_file == null) return;
        try
        {
            var sb = new StringBuilder();
            foreach (var kv in _known)
            {
                Marker m = kv.Value;
                sb.Append(kv.Key).Append('|')
                  .Append(string.Join(",", new[] { m.Pos.x, m.Pos.y, m.Pos.z }.Select(F))).Append('|')
                  .Append(string.Join(",", new[] { m.Rot.x, m.Rot.y, m.Rot.z, m.Rot.w }.Select(F))).Append('|')
                  .Append(m.Seen.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            File.WriteAllText(_file, sb.ToString());
        }
        catch (Exception e) { EuphoriaPlugin.Log("Delirium: gun markers not saved: " + e.Message); }
    }

    private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static IEnumerable<string> Select(this float[] a, Func<float, string> f) { foreach (float x in a) yield return f(x); }

    /// The weapon in his hands: its template id (and name, for reading the file), or null.
    internal static string KeyOf(Player p, Transform gun)
    {
        string id = null;
        try { id = p?.HandsController?.Item?.TemplateId.ToString(); } catch { }
        string name = gun != null ? gun.name.Replace("(Clone)", "").Trim() : "?";
        try { name = p?.HandsController?.Item?.ShortName?.ToString() ?? name; } catch { }
        return string.IsNullOrEmpty(id) ? null : id + " " + name.Replace('|', '/');
    }

    /// Where the game itself puts the right hand on this gun (its hand IK target), else the pistol-grip mount.
    /// The IK marker first: the palm sits exactly on it, while the hand marker is often 12-14 cm off.
    internal static Transform GripMount(Transform gun, out float gate)
    {
        gate = GripGate;
        if (gun == null) return null;
        Transform hand = null, grip = null;
        foreach (Transform t in gun.GetComponentsInChildren<Transform>(true))
        {
            string n = t.name.ToLowerInvariant();
            if (n == "weapon_r_ik_marker") { gate = IkGate; return t; }
            if (hand == null && n == "weapon_r_hand_marker") hand = t;
            if (grip == null && (n.Contains("pistol_grip") || n.Contains("pistolgrip"))) grip = t;
        }
        if (hand != null) { gate = IkGate; return hand; }
        return grip;
    }

    /// The palm in a right-hand IK marker's frame, learned across all guns.
    internal static bool TryGetAnyIk(out Vector3 pos, out Quaternion rot)
    {
        pos = default; rot = default;
        if (!_known.TryGetValue(AnyIk, out Marker m) || m.Seen < 2) return false;
        pos = m.Pos; rot = m.Rot;
        return true;
    }

    // a palm on the marker: averaged in (a frame more than 20 deg off the average is an odd one, left out)
    private static void ObserveAnyIk(Transform mount, Transform palm)
    {
        Quaternion inv = Quaternion.Inverse(mount.rotation);
        var s = new Marker { Pos = inv * (palm.position - mount.position), Rot = inv * palm.rotation, Seen = 1 };
        if (_known.TryGetValue(AnyIk, out Marker k))
        {
            if (k.Seen >= 2 && Quaternion.Angle(k.Rot, s.Rot) > 20f) return;
            k.Seen++;
            float w = 1f / Mathf.Min(k.Seen, 20);
            k.Pos = Vector3.Lerp(k.Pos, s.Pos, w); k.Rot = Quaternion.Slerp(k.Rot, s.Rot, w);
            s = k;
        }
        _known[AnyIk] = s;
        if (s.Seen == 2) EuphoriaPlugin.Log("Delirium: grip angle on the right-hand IK marker learned (any gun)");
        if (s.Seen == 2 || s.Seen % 10 == 0) Save();
    }

    internal static bool TryGet(string key, out Marker m)
    {
        m = default;
        return key != null && _known.TryGetValue(key, out m) && m.Seen >= 2;
    }

    /// A frame where he holds `gun` with `palm`: learn from it if it's a plain grip.
    internal static void Observe(string key, Transform gun, Transform palm)
    {
        if (key == null || gun == null || palm == null) return;
        Scan(key, gun, palm);
        Transform mount = GripMount(gun, out float gate);
        if (mount != null && (palm.position - mount.position).magnitude > gate) return; // hand not on the grip
        if (mount != null && gate == IkGate) ObserveAnyIk(mount, palm);
        Quaternion inv = Quaternion.Inverse(gun.rotation);
        var s = new Marker { Pos = inv * (palm.position - gun.position), Rot = inv * palm.rotation, Seen = 1 };
        if (_known.TryGetValue(key, out Marker k))
        {
            if (Same(k, s)) { k.Seen++; k.Pos = Vector3.Lerp(k.Pos, s.Pos, 1f / Mathf.Min(k.Seen, 10)); k.Rot = Quaternion.Slerp(k.Rot, s.Rot, 1f / Mathf.Min(k.Seen, 10)); _known[key] = k; }
            return; // a known grip is never replaced by one odd frame
        }
        if (_candidate.TryGetValue(key, out Marker c) && Same(c, s))
        {
            s.Seen = 2;
            _known[key] = s;
            _candidate.Remove(key);
            EuphoriaPlugin.Log($"Delirium: grip marker learned for {key}");
            Save();
        }
        else _candidate[key] = s;
    }

    private static bool Same(Marker a, Marker b) => (a.Pos - b.Pos).magnitude < Agree && Quaternion.Angle(a.Rot, b.Rot) < AgreeDeg;

    private static void Scan(string key, Transform gun, Transform palm)
    {
        if (!_scanned.Add(key)) return;
        var sb = new StringBuilder($"Delirium: scanned {key} ({gun.name}): palm {(palm.position - gun.position).magnitude:F2} m from its origin");
        foreach (Transform t in gun.GetComponentsInChildren<Transform>(true))
        {
            string n = t.name.ToLowerInvariant();
            if (n.Contains("hand") || n.Contains("grip") || n.Contains("ik") || n.Contains("marker"))
                sb.Append($"\n    {t.name}: {(t.position - palm.position).magnitude:F2} m from the palm");
        }
        EuphoriaPlugin.Log(sb.ToString());
    }
}

/// <summary>
/// Re-places our arm pose and the held gun after every other script this frame (the game's weapon
/// animation included): once at the end of LateUpdate, and once more right before the frame renders.
/// The game's arm IK can run after any LateUpdate order; when it did, the arms were put back into the
/// weapon stance while the gun stayed on the doll's hand.
/// </summary>
[DefaultExecutionOrder(32000)]
internal class GunLatePass : MonoBehaviour
{
    private NmBody _b;
    private static bool _hooked;

    private void Awake()
    {
        if (_hooked) return;
        _hooked = true;
        Application.onBeforeRender += BeforeRender;
    }

    private void LateUpdate()
    {
        if (_b == null) _b = GetComponent<NmBody>();
        if (_b != null && (_b.Active || _b.GettingUp || _b.GunBlending)) _b.HoldGunLate();
    }

    private static void BeforeRender()
    {
        var all = NmBody.AllBodies;
        for (int i = 0; i < all.Count; i++)
        {
            NmBody b = all[i];
            try { if (b != null && (b.Active || b.GettingUp || b.GunBlending)) b.HoldGunLate(true); }
            catch { }
        }
    }
}
