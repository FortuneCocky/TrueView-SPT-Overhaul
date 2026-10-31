using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using EFT;
using UnityEngine;

namespace EuphoriaReaction;

/// <summary>One get-up clip: pelvis position + 17 body rotations per frame (Unity world, clip faces +Z).</summary>
internal class GetUpClip
{
    public string Name, Start;
    public bool Arms;     // authored at real speed: keyframed arm get-ups and the captured ones
    public bool Captured; // a captured full-body get-up ("cap_" clips), preferred when it fits how he lies
    public float Fps;
    public int StandFrame, Frames;
    public Vector3[] P;
    public Quaternion[][] R; // [frame][key]
}

/// <summary>
/// Get-up clips generated with NVIDIA MotionBricks (Unitree G1), packed by
/// Euphoria Reaction's pipeline into getups.bin and retargeted at runtime.
/// </summary>
internal static class GetUpLibrary
{
    public const int Keys = 17;
    public static float RefPelvisY = 0.793f;
    public static readonly Quaternion[] Ref = new Quaternion[Keys];
    public static readonly List<GetUpClip> Clips = new List<GetUpClip>();

    public static void Load(string path) => Load(path, Clips);

    public static void Load(string path, List<GetUpClip> into)
    {
        List<GetUpClip> Clips = into;
        Clips.Clear();
        if (!File.Exists(path)) return;
        using (var br = new BinaryReader(File.OpenRead(path)))
        {
            if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "GUP1") throw new InvalidDataException("bad magic");
            int keys = br.ReadInt32(), count = br.ReadInt32();
            if (keys != Keys) throw new InvalidDataException("key count " + keys);
            RefPelvisY = br.ReadSingle();
            for (int k = 0; k < Keys; k++) Ref[k] = Q(br);
            for (int c = 0; c < count; c++)
            {
                var clip = new GetUpClip { Name = Str(br), Start = Str(br) };
                clip.Captured = clip.Name.StartsWith("cap_");
                clip.Arms = clip.Name.Contains("_arms_") || clip.Captured;
                clip.Fps = br.ReadSingle();
                clip.StandFrame = br.ReadInt32();
                clip.Frames = br.ReadInt32();
                clip.P = new Vector3[clip.Frames];
                clip.R = new Quaternion[clip.Frames][];
                for (int f = 0; f < clip.Frames; f++)
                {
                    clip.P[f] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    clip.R[f] = new Quaternion[Keys];
                    for (int k = 0; k < Keys; k++) clip.R[f][k] = Q(br);
                }
                Clips.Add(clip);
            }
        }
    }

    private static Quaternion Q(BinaryReader br)
    {
        var q = new Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        return m < 1e-6f ? Quaternion.identity : new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
    }

    private static string Str(BinaryReader br) => Encoding.UTF8.GetString(br.ReadBytes(br.ReadByte()));
}

/// <summary>
/// Best-effort: stop a bot's own actions (healing, using items) while Euphoria
/// owns his body. Reflection only — member names are logged once so they can
/// be tightened.
/// </summary>
internal static class BotActions
{
    private static bool _dumped;
    private static readonly string[] StopNames = { "StopUse", "Stop", "Cancel", "StopUsing", "CancelCurrent", "Interrupt" };
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // the member each (type, name) resolved to, found once: this runs every frame for held bots
    private static readonly Dictionary<(Type, string), MemberInfo> _getCache = new Dictionary<(Type, string), MemberInfo>();

    internal static object Get(object o, string name)
    {
        if (o == null) return null;
        try
        {
            Type type = o.GetType();
            if (!_getCache.TryGetValue((type, name), out MemberInfo m))
            {
                m = null;
                for (Type t = type; t != null && m == null; t = t.BaseType)
                {
                    PropertyInfo pi = t.GetProperty(name, All | BindingFlags.DeclaredOnly);
                    if (pi != null && pi.GetIndexParameters().Length == 0) m = pi;
                    else m = t.GetField(name, All | BindingFlags.DeclaredOnly);
                }
                _getCache[(type, name)] = m;
            }
            if (m is PropertyInfo p) return p.GetValue(o, null);
            if (m is FieldInfo f) return f.GetValue(o);
        }
        catch { }
        return null;
    }

    private static readonly Dictionary<Type, PropertyInfo> _rotProp = new Dictionary<Type, PropertyInfo>();

    /// The movement's look rotation (yaw, pitch), set with the root's yaw.
    internal static void SetMoveRotation(Player p, Quaternion yaw)
    {
        object mc = Get(p, "MovementContext");
        if (mc == null) return;
        Type t = mc.GetType();
        if (!_rotProp.TryGetValue(t, out PropertyInfo rp))
        {
            rp = t.GetProperty("Rotation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (rp != null && (rp.PropertyType != typeof(Vector2) || !rp.CanWrite)) rp = null;
            _rotProp[t] = rp;
        }
        rp?.SetValue(mc, new Vector2(yaw.eulerAngles.y, 0f), null);
    }

    /// The BotOwner a bot subsystem belongs to (any field of that type, base classes included).
    internal static object OwnerOf(object o)
    {
        if (o == null) return null;
        try
        {
            for (Type t = o.GetType(); t != null; t = t.BaseType)
                foreach (FieldInfo f in t.GetFields(All | BindingFlags.DeclaredOnly))
                    if (f.FieldType.Name == "BotOwner") return f.GetValue(o);
        }
        catch { }
        return null;
    }

    private static void Dump(string label, object o)
    {
        if (o == null) { EuphoriaPlugin.Log($"Delirium: actions: {label} = null"); return; }
        var sb = new StringBuilder();
        foreach (MemberInfo m in o.GetType().GetMembers(All | BindingFlags.DeclaredOnly))
            if (m is MethodInfo mi && !mi.IsSpecialName) sb.Append(mi.Name).Append("(").Append(mi.GetParameters().Length).Append(") ");
            else if (m is FieldInfo || m is PropertyInfo) sb.Append(m.Name).Append(' ');
        EuphoriaPlugin.Log($"Delirium: actions: {label} is {o.GetType().FullName}: {sb}");
    }

    private static readonly Dictionary<int, float> _gunAsked = new Dictionary<int, float>();

    /// Something other than a weapon in his hands (meds, food, a grenade, nothing): his main weapon back.
    /// Stopping the AI's healing left the item's animation running in his hands; it fought the physics
    /// and he stuck in it after the hand-back. Switching hands is how the game itself interrupts an item
    /// (it goes back in his pockets, not on the floor). Only once he's back on his animation (the blend
    /// home is over): switched while Euphoria still held him, the hands and the doll fought and he bugged out.
    public static void ToGun(Player p)
    {
        if (p == null || !(EuphoriaPlugin.Instance?.CancelActions.Value ?? true)) return;
        try { HandsToGun(p, Get(Get(p, "AIData"), "BotOwner")); }
        catch (Exception e) { EuphoriaPlugin.Log("Delirium: actions: weapon back: " + (e.InnerException ?? e).Message); }
        finally { HandsWatch.Begin(p); } // and checked for a few seconds that it took (HandsWatch)
    }

    private static void HandsToGun(Player p, object bo)
    {
        object hc = Get(p, "HandsController");
        if (hc == null) return;
        string kind = hc.GetType().Name;
        if (kind.Contains("Firearm") || kind.Contains("Knife")) return;
        int id = p.GetInstanceID();
        if (_gunAsked.TryGetValue(id, out float at) && Time.time - at < 3f) return;
        _gunAsked[id] = Time.time;
        object sel = Get(Get(bo, "WeaponManager"), "Selector");
        bool asked = false;
        if (sel != null)
        {
            try
            {
                MethodInfo mi = sel.GetType().GetMethod("ChangeToMain", All, null, Type.EmptyTypes, null);
                asked = mi != null && mi.Invoke(sel, null) is bool b && b;
                if (!asked)
                {
                    mi = sel.GetType().GetMethod("TakeMainWeapon", All, null, Type.EmptyTypes, null);
                    if (mi != null) { mi.Invoke(sel, null); asked = true; }
                }
            }
            catch (Exception e) { EuphoriaPlugin.Log("Delirium: actions: weapon switch: " + (e.InnerException ?? e).Message); }
        }
        EuphoriaPlugin.Log($"Delirium: {p.ProfileId} had {kind} in his hands - {(asked ? "back to his main weapon" : "no weapon to switch to")}");
    }

    private static void Stop(object o)
    {
        if (o == null) return;
        foreach (string n in StopNames)
        {
            try
            {
                MethodInfo mi = o.GetType().GetMethod(n, All, null, Type.EmptyTypes, null);
                if (mi != null) mi.Invoke(o, null);
            }
            catch { }
        }
    }

    private static bool _aimAllowed(Player p) => false;

    internal static object BotOwnerOf(Player p) => Get(Get(p, "AIData"), "BotOwner");

    private static int _syncLogged;

    /// Tell the bot's mover where the body really is. Without this the mover keeps pulling the root back
    /// to the path position it had when Euphoria took over (the bot "teleports back" when released).
    internal static void SyncMover(Player p)
    {
        try
        {
            object mover = Get(BotOwnerOf(p), "Mover");
            if (mover == null) return;
            MethodInfo mi = mover.GetType().GetMethod("Teleport", All, null, new[] { typeof(Vector3) }, null);
            if (mi == null) { if (_syncLogged++ < 2) EuphoriaPlugin.Log("Delirium: mover has no Teleport(Vector3)"); return; }
            mi.Invoke(mover, new object[] { p.Transform.position });
            if (_syncLogged++ < 2) EuphoriaPlugin.LogV("Delirium: bot mover re-registered at the body's position");
        }
        catch (Exception e) { if (_syncLogged++ < 4) EuphoriaPlugin.Log("Delirium: mover sync: " + (e.InnerException ?? e).Message); }
    }

    private static MethodInfo _lookDir, _setYByDir;
    private static bool _lookLogged;
    private static readonly object[] _lookArg = new object[1];

    /// Point the bot AI's steering (where he looks and turns his body) along dir.
    internal static void LookTo(Player p, Vector3 dir)
    {
        try
        {
            object st = Get(BotOwnerOf(p), "Steering");
            if (st == null) return;
            Type t = st.GetType();
            if (_lookDir == null || !_lookDir.DeclaringType.IsAssignableFrom(t))
            {
                _lookDir = t.GetMethod("LookToDirection", All, null, new[] { typeof(Vector3) }, null);
                _setYByDir = t.GetMethod("SetYByDir", All, null, new[] { typeof(Vector3) }, null);
                if (_lookDir == null && !_lookLogged) { _lookLogged = true; EuphoriaPlugin.Log("Delirium: bot steering has no LookToDirection(Vector3)"); }
            }
            _lookArg[0] = dir; // boxed once, the argument array reused
            _lookDir?.Invoke(st, _lookArg);
            _setYByDir?.Invoke(st, _lookArg);
        }
        catch (Exception e) { if (!_lookLogged) { _lookLogged = true; EuphoriaPlugin.Log("Delirium: steering look: " + (e.InnerException ?? e).Message); } }
    }

    /// Out of prone (if he's in it) and standing.
    internal static void Stand(Player p)
    {
        try
        {
            object mover = Get(BotOwnerOf(p), "Mover");
            mover?.GetType().GetMethod("DoProne", All, null, new[] { typeof(bool) }, null)?.Invoke(mover, new object[] { false });
        }
        catch (Exception e) { EuphoriaPlugin.Log("Delirium: out of prone: " + (e.InnerException ?? e).Message); }
        SetPose(p, 1f, false);
    }

    /// SPT pose: 1 stand, ~0.5 crouch; prone through the mover.
    internal static void SetPose(Player p, float level, bool prone)
    {
        try
        {
            object bo = BotOwnerOf(p);
            object mover = Get(bo, "Mover");
            if (prone)
            {
                MethodInfo dp = mover?.GetType().GetMethod("DoProne", All, null, new[] { typeof(bool) }, null);
                if (dp != null) { dp.Invoke(mover, new object[] { true }); EuphoriaPlugin.LogV("Delirium: bot set to prone"); return; }
            }
            MethodInfo sp = bo?.GetType().GetMethod("SetPose", All, null, new[] { typeof(float) }, null);
            if (sp != null) { sp.Invoke(bo, new object[] { level }); EuphoriaPlugin.LogV($"Delirium: bot pose level {level:F2}"); return; }
        }
        catch (Exception e) { EuphoriaPlugin.Log("Delirium: set pose: " + (e.InnerException ?? e).Message); }
    }

    /// Dev panel: put a (frozen) bot straight into a pose. level 1 stand, 0.5 crouch, 0 deep crouch;
    /// prone toggles EFT's prone state. Returns what was done (or why not).
    internal static string ForcePose(Player p, float level, bool prone)
    {
        try
        {
            MovementContext mc = p?.MovementContext;
            if (mc == null) return "no movement context";
            if (prone)
            {
                if (!mc.IsInPronePose) p.ToggleProne();
                return mc.IsInPronePose ? "prone" : "prone requested (EFT plays the drop; give it a second)";
            }
            if (mc.IsInPronePose) p.ToggleProne(); // up from prone first; EFT lands in a crouch
            mc.SetPoseLevel(level, true);
            return $"pose level {mc.PoseLevel:F2}{(mc.IsInPronePose ? " (still getting up from prone)" : "")}";
        }
        catch (Exception e) { return "force pose failed: " + (e.InnerException ?? e).Message; }
    }

    public static void Cancel(Player p)
    {
        if (p == null || !(EuphoriaPlugin.Instance?.CancelActions.Value ?? true)) return;
        try
        {
            object bo = Get(Get(p, "AIData"), "BotOwner");
            object med = Get(bo, "Medecine");
            object fa = Get(med, "FirstAid"), sk = Get(med, "SurgicalKit"), st = Get(med, "Stimulators");
            if (!_dumped)
            {
                _dumped = true;
                Dump("BotOwner.Medecine", med);
                Dump("Medecine.FirstAid", fa);
                Dump("Medecine.SurgicalKit", sk);
                Dump("Medecine.Stimulators", st);
                object hc = Get(p, "HandsController");
                EuphoriaPlugin.Log("Delirium: actions: hands controller " + (hc == null ? "null" : hc.GetType().FullName));
            }
            Stop(fa); Stop(sk); Stop(st); Stop(med);
            // the weapon comes back after the hand-back (ToGun): switching hands while Euphoria holds him bugged out
            // and no trigger: drop whatever shot the AI had started
            object sd = Get(bo, "ShootData");
            if (sd != null && (EuphoriaPlugin.Instance?.BlockFire.Value ?? true) && !_aimAllowed(p))
            {
                try { sd.GetType().GetMethod("EndShoot", All, null, Type.EmptyTypes, null)?.Invoke(sd, null); } catch { }
            }
        }
        catch (Exception e) { EuphoriaPlugin.Log("Delirium: actions: " + e.Message); }
    }
}

internal partial class NmBody
{
    // NM parts 0..12 -> clip keys (pelvis, L leg x3, R leg x3, spine_lo x2, spine_hi x2 + neck + head)
    private static readonly int[] KeyOf = { 0, 1, 2, 3, 4, 5, 6, 7, 7, 8, 8, 8, 8, 9, 10, 11, 12, 13, 14, 15, 16 };

    private GetUpClip _getUp;
    private bool _guRoll; // the clip is a roll-over: it ends prone, not crouched
    private float _guFrame, _guStartTime, _guScale, _guGround;
    private Quaternion _guF;
    private Vector3 _guPelvisStart, _guP0, _guLast;

    public bool GettingUp => _getUp != null;

    /// Lying face down? Read off the physics body: the belly direction of the pelvis and the chest
    /// together (their axes taken at the takeover), pointing at the ground. SPT's bones (the old test,
    /// kept as the fallback) lag and twist away from the doll, and called many plainly face-down
    /// bodies "not face down" (-0.1 to -0.3 against -0.45), so they were sent to roll over instead of
    /// going prone. Checked against captured lying poses: face down -0.64..-0.93, face up above +0.4.
    private bool LyingFaceDown(out Vector3 headDir)
    {
        headDir = Vector3.forward;
        if (Active && _bellyAxesOk)
        {
            Vector3 belly = (PartRot(0) * _pelvisBellyLocal + PartRot(10) * _chestBellyLocal).normalized;
            Vector3 up = (PartRot(0) * _pelvisUpLocal + PartRot(10) * _chestUpLocal).normalized;
            if (Mathf.Abs(up.y) > 0.7f) return false; // sitting up (or on his head): not lying
            Vector3 hd = new Vector3(up.x, 0f, up.z);
            if (hd.sqrMagnitude > 1e-4f) headDir = hd.normalized;
            return belly.y < -0.45f;
        }
        Transform pel = _bones[0], head = _bones[12] ?? _bones[11], ls = _bones[13], rs = _bones[17];
        if (pel == null || head == null || ls == null || rs == null) return false;
        Vector3 h = (head.position - pel.position);
        if (h.magnitude < 0.1f) return false;
        h.Normalize();
        if (Mathf.Abs(h.y) > 0.8f) return false;
        Vector3 f = Vector3.Cross(h, (ls.position - rs.position).normalized).normalized;
        headDir = new Vector3(h.x, 0f, h.z).normalized;
        return f.y < -0.45f;
    }

    /// Face down: sometimes (config) he pushes himself up with an arm get-up (push-up, all fours, one
    /// knee, stand) instead of staying down in prone.
    private bool ChestGetUp()
    {
        float chance = EuphoriaPlugin.Instance?.ChestGetUp.Value ?? 0.5f;
        return chance > 0f && UnityEngine.Random.value < chance && BeginGetUp();
    }

    /// Face down: no get-up clip. Turn the bot to the way his head points and hand him back as SPT's prone.
    private bool HandOffProne()
    {
        if (!LyingFaceDown(out Vector3 hd)) return false;
        try
        {
            FaceRoot(hd);
        }
        catch { }
        BotActions.SyncMover(_p);
        BotActions.SetPose(_p, 0f, true);
        EuphoriaPlugin.Log("Delirium: landed face down - handed back as SPT prone");
        return true;
    }

    /// Turn the bot's root (and his movement's look rotation) to face along hd.
    private void FaceRoot(Vector3 hd)
    {
        Quaternion yaw = Quaternion.LookRotation(hd, Vector3.up);
        _p.Transform.rotation = yaw;
        _heldLook = hd;
        BotActions.LookTo(_p, hd);
        try
        {
            object mc = BotActions.Get(_p, "MovementContext");
            PropertyInfo rp = mc?.GetType().GetProperty("Rotation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (rp != null && rp.PropertyType == typeof(Vector2) && rp.CanWrite) rp.SetValue(mc, new Vector2(yaw.eulerAngles.y, 0f), null);
        }
        catch { }
    }

    /// Down and done: how he's handed back from the ground. Into prone only when he lies face down and is
    /// badly hurt; otherwise he gets up, with a get-up clip (the arm get-up from his belly, else the clip
    /// closest to how he lies, however far). No more rolling over onto the front to go prone: it read as
    /// a canned move, and scavs in particular ended up prone mid-fight. False = nothing played.
    private bool HandBackFromGround()
    {
        bool faceDown = LyingFaceDown(out Vector3 hd);
        float hp = StumbleEngine.HpFraction(_p);
        float below = EuphoriaPlugin.Instance?.ProneBelowHp.Value ?? 0.35f;
        if (faceDown && hp <= below)
        {
            EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} face down at {hp:P0} HP - stays down in prone");
            if (BeginMatch(StartPose.Prone, hd)) return true;
            if (HandOffProne()) { _blendDur = GetUpBlend; Release(); return true; }
        }
        if (faceDown && ChestGetUp()) return true;
        return BeginGetUp() || BeginGetUp(99f);
    }

    /// Hand the lying body to a get-up clip. False = no usable clip (caller falls back to a plain blend).
    private bool BeginGetUp(float maxCost = GetUpMatch)
    {
        EuphoriaPlugin cfg = EuphoriaPlugin.Instance;
        if (cfg == null) return false;
        _pguWant = PhysGetUpOn; // play it through the live doll (NmBodyPhysGetUp) when it can
        bool played = PlayClip(GetUpLibrary.Clips, false, maxCost);
        _pguWant = false;
        if (!played) return false;
        // SPT stands up NOW, under the clip: switched at the end instead, a bot the game still had prone or
        // crouched played its own rise after the clip's, and got up twice
        ApplyStance(StartPose.Stand);
        // Face his root the way the clip stands up facing, now: the stand animation he's handed back to and
        // the game's weapon/arm IK both follow the root, which still faced the way he did before the fall
        // (often the opposite way: he got up "backwards" and turned round at the end).
        GetUpClip c = _getUp;
        Vector3 fe = _guF * (c.R[c.Frames - 1][0] * Quaternion.Inverse(GetUpLibrary.Ref[0])) * Vector3.forward;
        fe.y = 0f;
        if (fe.sqrMagnitude > 1e-4f) { try { FaceRoot(fe.normalized); } catch { } }
        return true;
    }

    // How far his lying pose may be from a get-up clip's start (belly-up + side-up difference, 0..4).
    // The get-ups start from one captured pose: lying much differently, he rolls over instead.
    private const float GetUpMatch = 0.9f;

    private bool PlayClip(List<GetUpClip> lib, bool roll, float maxCost)
    {
        if (lib.Count == 0) return false;
        Transform pel = _bones[0], head = _bones[12] ?? _bones[11], ls = _bones[13], rs = _bones[17];
        if (pel == null || head == null || ls == null || rs == null) return false;
        Vector3 h = head.position - pel.position;
        if (h.magnitude < 0.1f) return false;
        h.Normalize();
        if (Mathf.Abs(h.y) > 0.8f) return false; // not lying: nothing to get up from
        Vector3 sh = (ls.position - rs.position).normalized;      // towards his left
        Vector3 f = Vector3.Cross(h, sh).normalized;                // which way he faces
        // Pick the clip whose frame-0 lying pose is closest to his (belly up/down and which
        // side is up), measured from the clips themselves, not from their names.
        float fy = f.y, sy = sh.y;
        var scored = new List<KeyValuePair<float, GetUpClip>>();
        float best = float.MaxValue;
        foreach (GetUpClip c in lib)
        {
            Quaternion r0 = c.R[0][8];
            float cf = (r0 * Vector3.forward).y, cs = (r0 * Vector3.left).y;
            float cost = Mathf.Abs(cf - fy) + Mathf.Abs(cs - sy);
            scored.Add(new KeyValuePair<float, GetUpClip>(cost, c));
            if (cost < best) best = cost;
        }
        if (best > maxCost)
        {
            EuphoriaPlugin.Log($"Delirium: no {(roll ? "roll" : "get-up")} clip close to how he lies (face {fy:F2} side {sy:F2}, best {best:F2})");
            return false;
        }
        var pool = new List<GetUpClip>();
        foreach (var kv in scored) if (kv.Key <= best + 0.2f) pool.Add(kv.Value);
        // the captured get-ups first, when one starts close enough to how he lies
        if (!roll)
        {
            var captured = new List<GetUpClip>();
            foreach (var kv in scored) if (kv.Value.Captured && kv.Key <= maxCost) captured.Add(kv.Value);
            if (captured.Count > 0) pool = captured;
        }
        GetUpClip clip = pool[UnityEngine.Random.Range(0, pool.Count)];
        string cat = $"face {fy:F2} side {sy:F2} (clip cost {best:F2})";

        // turn the clip so its lying head direction matches his
        Vector3 hc = clip.R[0][8] * Vector3.up;
        if (new Vector2(hc.x, hc.z).magnitude < 0.2f) return false;
        float azC = Mathf.Atan2(hc.x, hc.z), azB = Mathf.Atan2(h.x, h.z);

        if (!(_pguWant && PhysGetUpBegin()))
        {
            _holdOver = true; // keep the gun in his hand and the speed limit on while he rises
            Release(false);
            _holdOver = false;
        }
        _blending = false;
        _getUp = clip;
        _guRoll = roll;
        _guF = Quaternion.Euler(0f, (azB - azC) * Mathf.Rad2Deg, 0f);
        _guScale = Mathf.Max(0.8f, (_standPelvisY - _floorY) / GetUpLibrary.RefPelvisY);
        _guPelvisStart = _blendPelvis;
        _guLast = _blendPelvis;
        _guP0 = clip.P[0];
        _guFrame = 0f;
        _guStartTime = Time.time;
        _guGround = _blendPelvis.y - 0.15f;
        // each arm bone's own axis towards its child (upper arm -> forearm, forearm -> hand)
        foreach (int k in ArmLimbs)
        {
            Transform b = _bones[k], c = _bones[k + 1];
            _armAxis[k] = b != null && c != null && (c.position - b.position).sqrMagnitude > 1e-6f
                ? Quaternion.Inverse(b.rotation) * (c.position - b.position).normalized : Vector3.zero;
        }
        enabled = true;
        BotActions.Cancel(_p);
        EuphoriaPlugin.Log($"Delirium: {(roll ? "roll over" : "get up")} ({clip.Name}, lying {cat}, scale {_guScale:F2})");
        return true;
    }

    // Arms by direction, not by rotation: the G1's rest arms hang with the elbows bent forward, his stand
    // arms hold a rifle, so the same rotation from rest bent his arms backwards. Upper arm and forearm
    // point where the G1's do (the G1's rest directions: upper arm down, forearm forward), keeping the
    // twist the rotation gave; the hand keeps his grip angle on the forearm.
    private static readonly int[] ArmLimbs = { 14, 15, 18, 19 };
    private readonly Vector3[] _armAxis = new Vector3[Parts];
    private readonly Quaternion[] _guTarget = new Quaternion[Parts];

    private Quaternion ArmTarget(int k, int key, Quaternion rc, Quaternion target, float endW)
    {
        if (k == 14 || k == 15 || k == 18 || k == 19)
        {
            if (_armAxis[k] == Vector3.zero) return target;
            Vector3 rest = k == 14 || k == 18 ? Vector3.down : Vector3.forward;
            Vector3 d = _guF * (rc * Quaternion.Inverse(GetUpLibrary.Ref[key])) * rest;
            Quaternion aligned = Quaternion.FromToRotation(target * _armAxis[k], d) * target;
            return Quaternion.Slerp(aligned, target, endW);
        }
        if (k == 16 || k == 20)
        {
            Quaternion held = _guTarget[k - 1] * Quaternion.Inverse(_standRot[k - 1]) * _standRot[k];
            return Quaternion.Slerp(held, target, endW);
        }
        return target;
    }

    private void AbortGetUp()
    {
        _armWritten = false;
        _getUp = null;
        _blending = false;
        enabled = false;
        if (_p != null && _speedLimitApplied)
        {
            try { _p.RemoveStateSpeedLimit(Player.ESpeedLimit.Shot); } catch { }
            _speedLimitApplied = false;
        }
        RestoreGun();
    }

    private void GetUpTick()
    {
        GetUpClip clip = _getUp;
        if (_p == null || _bones == null || _bones[0] == null || _p.HealthController?.IsAlive != true)
        {
            AbortGetUp();
            return;
        }
        float speed = Mathf.Max(0.2f, clip.Arms ? EuphoriaPlugin.Instance?.ArmGetUpSpeed.Value ?? 1f : EuphoriaPlugin.Instance?.GetUpSpeed.Value ?? 0.7f);
        _guFrame += Time.deltaTime * clip.Fps * speed;
        float last = clip.Frames - 1;
        bool done = _guFrame >= last;
        float fr = Mathf.Min(_guFrame, last);
        int i0 = (int)fr, i1 = Mathf.Min(i0 + 1, clip.Frames - 1);
        float a = fr - i0;
        float w = Mathf.SmoothStep(0f, 1f, (Time.time - _guStartTime) / 0.3f);

        // pelvis: clip displacement scaled to his size; height above the real ground
        Vector3 dp = Vector3.Lerp(clip.P[i0], clip.P[i1], a) - _guP0;
        Vector3 hor = _guF * new Vector3(dp.x, 0f, dp.z) * _guScale;
        Vector3 xz = new Vector3(_guPelvisStart.x + hor.x, 0f, _guPelvisStart.z + hor.z);
        Vector3 step = new Vector3(xz.x - _guLast.x, 0f, xz.z - _guLast.z);
        float dist = step.magnitude;
        if (dist > 1e-4f && RayWorld(new Vector3(_guLast.x, _guPelvisStart.y, _guLast.z), step / dist, dist + 0.25f, out _))
            xz = new Vector3(_guLast.x, 0f, _guLast.z); // a wall: stay put
        float gy = RayWorld(new Vector3(xz.x, _guPelvisStart.y + 0.5f, xz.z), Vector3.down, 3f, out RaycastHit g) ? g.point.y : _floorY;
        _guGround = Mathf.Lerp(_guGround, gy, Time.deltaTime * 10f);
        float py = _guGround + Mathf.Lerp(clip.P[i0].y, clip.P[i1].y, a) * _guScale;
        Vector3 pelvisTarget = new Vector3(xz.x, py, xz.z);
        _guLast = pelvisTarget;

        Quaternion face = Quaternion.Euler(0f, -_theta * Mathf.Rad2Deg, 0f);
        // a get-up ends in his stand: over its last stretch the arms go from the clip's to his gun pose
        float endW = _guRoll || clip.StandFrame >= last ? 0f : Mathf.SmoothStep(0f, 1f, (fr - clip.StandFrame) / (last - clip.StandFrame));
        for (int k = 0; k < Parts; k++)
        {
            if (_bones[k] == null) continue;
            int key = KeyOf[k];
            Quaternion rc = Quaternion.Slerp(clip.R[i0][key], clip.R[i1][key], a);
            Quaternion target = _guF * (rc * Quaternion.Inverse(GetUpLibrary.Ref[key])) * face * _standRot[k];
            target = ArmTarget(k, key, rc, target, endW);
            _guTarget[k] = target;
            Quaternion q = Quaternion.Slerp(_blendRot[k], target, w);
            if (k == 0) _bones[0].SetPositionAndRotation(Vector3.Lerp(_blendPelvis, pelvisTarget, w), q);
            else _bones[k].rotation = q;
        }
        GroundClamp();
        // the arms as the clip posed them, put back after the game's weapon IK (HoldGunLate)
        for (int k = 13; k < Parts; k++)
            if (_bones[k] != null) { _armWrite[k] = _bones[k].localRotation; _armWriteLocal[k] = _bones[k].localPosition; }
        _armWritten = true;
        _armWriteFrame = Time.frameCount;

        // the AI/hitbox root follows him (XZ only)
        Vector3 root = _p.Transform.position;
        MoveRoot(new Vector3(_bones[0].position.x - _pelvisDelta.x, root.y, _bones[0].position.z - _pelvisDelta.z));
        if (Time.time >= _nextSync) { _nextSync = Time.time + 0.25f; BotActions.SyncMover(_p); }

        if (done) FinishGetUp();
    }

    private void FinishGetUp()
    {
        _armWritten = false;
        for (int k = 0; k < Parts; k++)
            if (_bones[k] != null) _blendRot[k] = _bones[k].rotation;
        _blendPelvis = _bones[0].position;
        _blendStart = Time.time;
        _blendDur = 0.5f;
        _getUp = null;
        _blending = true;
        if (_p != null && _speedLimitApplied)
        {
            try { _p.RemoveStateSpeedLimit(Player.ESpeedLimit.Shot); } catch { }
            _speedLimitApplied = false;
        }
        RestoreGunBlended(_blendDur); // the gun goes home with his hands, over the same blend
        BotActions.SyncMover(_p);
        _gunAfterBlend = true;
        if (_guRoll)
        {
            ApplyStance(StartPose.Prone);
            bool taken = StanceTaken(StartPose.Prone);
            if (!taken) _blendDur = 0.9f; // the game's own drop is still finishing
            EuphoriaPlugin.Log($"Delirium: rolled onto his front - back to animation (prone{(taken ? "" : ", still settling")})");
            return;
        }
        // the clip ends in his own standing pose: hand back standing
        ApplyStance(StartPose.Stand);
        EuphoriaPlugin.Log("Delirium: get-up finished - back to animation (stand)");
    }
}

/// <summary>While Euphoria owns a bot's body, his medical subsystems may not start anything.</summary>
internal static class MedsGuard
{
    private static bool _warned;

    public static void Install()
    {
        var harmony = new HarmonyLib.Harmony("com.euphoria.reaction.meds");
        var pre = new HarmonyLib.HarmonyMethod(typeof(MedsGuard).GetMethod(nameof(Block), BindingFlags.Static | BindingFlags.NonPublic));
        int n = 0;
        foreach (string tn in new[] { "BotMedecine", "BotFirstAid", "BotSurgicalKit", "BotStimulators" })
        {
            Type t = null;
            foreach (Type x in typeof(Player).Assembly.GetTypes()) if (x.Name == tn) { t = x; break; }
            if (t == null) continue;
            MethodInfo m = t.GetMethod("Activate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            if (m == null) continue;
            try { harmony.Patch(m, prefix: pre); n++; } catch (Exception e) { EuphoriaPlugin.Log("Delirium: meds guard " + tn + ": " + e.Message); }
        }
        EuphoriaPlugin.Log($"Delirium: meds guard on {n} bot medical methods");
    }

    private static bool Block(object __instance)
    {
        try
        {
            object bo = BotActions.OwnerOf(__instance);
            if (bo == null)
            {
                if (!_warned) { _warned = true; EuphoriaPlugin.Log("Delirium: meds guard: no BotOwner on " + __instance.GetType().Name); }
                return true;
            }
            Player p = BotActions.Get(bo, "GetPlayer") as Player;
            if (p == null) return true;
            NmBody nb = p.GetComponent<NmBody>();
            if (nb != null && nb.Busy && (EuphoriaPlugin.Instance?.CancelActions.Value ?? true)) return false;
        }
        catch { }
        return true;
    }
}
