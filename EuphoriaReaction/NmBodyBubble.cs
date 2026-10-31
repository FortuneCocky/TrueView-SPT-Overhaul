using System.Collections.Generic;
using Comfort.Common;
using EFT;
using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// The moving half of the level mirror. The bake is static (triangles can't move once added), so
/// anything that moves near a reacting body goes into the engine's replaceable box set instead,
/// refreshed every frame with its velocity: other players (torso, head and legs as boxes along
/// their bones) and loose physics props / real door leaves inside a bubble around him. A body falling into
/// another bot now hits him instead of passing through; a swinging door shoves.
/// </summary>
internal partial class NmBody
{
    private const float BubbleR = 2.5f;
    private const int MaxBubbleBoxes = 40;

    private static readonly Dictionary<Player, Transform[]> _otherBones = new Dictionary<Player, Transform[]>();
    private readonly Dictionary<Collider, Vector3> _propLast = new Dictionary<Collider, Vector3>();
    private readonly float[] _bubble = new float[MaxBubbleBoxes * 13];
    private static readonly Collider[] _bubbleCols = new Collider[256];
    private int _bubbleCount, _bubblePlayers;
    // the same boxes in Unity space, for the dev overlay and the floating-foot check
    private readonly Vector3[] _bubC = new Vector3[MaxBubbleBoxes], _bubH = new Vector3[MaxBubbleBoxes];
    private readonly Quaternion[] _bubR = new Quaternion[MaxBubbleBoxes];

    // bone segments boxed on another player: (from, to, radius)
    private static readonly (int a, int b, float r)[] Segs =
        { (0, 10, 0.17f), (12, 12, 0.11f), (1, 2, 0.08f), (2, 3, 0.06f), (4, 5, 0.08f), (5, 6, 0.06f) };

    private void BubbleTick()
    {
        _bubbleCount = 0;
        if (_bones == null || _bones[0] == null) return;
        Vector3 c = _bones[0].position;
        float dt = Mathf.Max(Time.deltaTime, 1e-3f);

        PruneBubbleCaches();
        var gw = Singleton<GameWorld>.Instance;
        if (gw != null)
            foreach (Player o in gw.AllAlivePlayersList)
            {
                if (o == null || o == _p || (o.Transform.position - c).sqrMagnitude > (BubbleR + 1f) * (BubbleR + 1f)) continue;
                if (!_otherBones.TryGetValue(o, out Transform[] ob) || ob == null || ob[0] == null)
                    _otherBones[o] = ob = MapBones(o);
                if (ob == null) continue;
                Vector3 vel = Vector3.zero;
                try { vel = o.Velocity; } catch { }
                foreach (var (a, b, r) in Segs)
                {
                    if (ob[a] == null || ob[b] == null) continue;
                    Vector3 pa = ob[a].position, pb = a == b ? ob[a].position + ob[a].up * 0.1f : ob[b].position;
                    Vector3 d = pb - pa;
                    float len = d.magnitude;
                    Quaternion rot = len > 1e-3f ? Quaternion.LookRotation(d / len) : Quaternion.identity;
                    AddBubbleBox((pa + pb) * 0.5f, rot, new Vector3(r, r, len * 0.5f + r * 0.5f), vel);
                }
            }

        _bubblePlayers = _bubbleCount;
        // moving props: doors and loose physics objects
        int found = Physics.OverlapSphereNonAlloc(c, BubbleR, _bubbleCols, WorldMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < found; i++)
        {
            Collider col = _bubbleCols[i];
            if (!IsDynamic(col)) continue;
            Bounds bb = col.bounds;
            Vector3 vel = _propLast.TryGetValue(col, out Vector3 last) ? (bb.center - last) / dt : Vector3.zero;
            _propLast[col] = bb.center;
            // a real oriented box from the collider's own shape, never its world AABB (a rotated or
            // large mesh's AABB was a big invisible block a foot could stand on in mid-air)
            if (!LocalBox(col, out Vector3 centre, out Quaternion rot, out Vector3 half)) continue;
            if (half.x > MaxPropHalf || half.y > MaxPropHalf || half.z > MaxPropHalf) continue; // not a prop or a door leaf
            AddBubbleBox(centre, rot, half, vel);
        }

        nm_world_set_boxes(_world, _bubble, _bubbleCount);
    }

    private const float MaxPropHalf = 1.5f; // m: half-extent cap for a moving prop or door leaf

    // Every bot that ever came near a reacting body kept its bone map here (and every prop its last
    // position) for the rest of the raid, dead and despawned ones too: dropped every 20 s once gone.
    private static float _nextBubblePrune;
    private static readonly List<Player> _pruneP = new List<Player>();
    private static readonly List<Collider> _pruneC = new List<Collider>();

    private void PruneBubbleCaches()
    {
        if (_propLast.Count > 256)
        {
            foreach (Collider k in _propLast.Keys) if (k == null) _pruneC.Add(k);
            foreach (Collider k in _pruneC) _propLast.Remove(k);
            _pruneC.Clear();
            if (_propLast.Count > 256) _propLast.Clear(); // only feeds a one-frame velocity: rebuilt next frame
        }
        if (Time.time < _nextBubblePrune) return;
        _nextBubblePrune = Time.time + 20f;
        foreach (var kv in _otherBones)
            if (kv.Key == null || kv.Value == null || kv.Value[0] == null || kv.Key.HealthController == null || !kv.Key.HealthController.IsAlive)
                _pruneP.Add(kv.Key);
        foreach (Player k in _pruneP) _otherBones.Remove(k);
        _pruneP.Clear();
    }

    private static readonly System.Collections.Generic.Dictionary<Collider, bool> _dynamic = new System.Collections.Generic.Dictionary<Collider, bool>();

    /// Something that moves: a loose (non-kinematic) physics object, or the leaf of a real door (the
    /// game's interactive door component on it or a close parent). Names don't count: level groups
    /// called "door..." (frames, doorways, whole building chunks) were taken out of the static level
    /// and turned into boxes.
    internal static bool IsDynamic(Collider c)
    {
        if (c == null) return false;
        Rigidbody rb = c.attachedRigidbody;
        if (rb != null && !rb.isKinematic) return true;
        if (_dynamic.TryGetValue(c, out bool known)) return known;
        bool door = false;
        Transform t = c.transform;
        for (int up = 0; up < 3 && t != null && !door; up++, t = t.parent)
            foreach (MonoBehaviour m in t.GetComponents<MonoBehaviour>())
                if (m != null && IsDoorType(m.GetType())) { door = true; break; }
        if (_dynamic.Count > 4000) _dynamic.Clear();
        _dynamic[c] = door;
        return door;
    }

    private static bool IsDoorType(System.Type t)
    {
        for (; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            if (t.Name == "Door" || t.Name == "WorldInteractiveObject") return true;
        return false;
    }

    /// The collider as an oriented box in world space, from its own local shape.
    private static bool LocalBox(Collider col, out Vector3 centre, out Quaternion rot, out Vector3 half)
    {
        Transform t = col.transform;
        rot = t.rotation;
        Vector3 c, size;
        switch (col)
        {
            case BoxCollider b: c = b.center; size = b.size; break;
            case MeshCollider m when m.sharedMesh != null: c = m.sharedMesh.bounds.center; size = m.sharedMesh.bounds.size; break;
            case CapsuleCollider cc:
                c = cc.center;
                size = new Vector3(cc.radius * 2f, cc.radius * 2f, cc.radius * 2f);
                size[cc.direction] = Mathf.Max(cc.height, cc.radius * 2f);
                break;
            case SphereCollider sc: c = sc.center; size = Vector3.one * sc.radius * 2f; break;
            default: centre = default; half = default; return false;
        }
        centre = t.TransformPoint(c);
        Vector3 h = Vector3.Scale(size, t.lossyScale) * 0.5f;
        half = new Vector3(Mathf.Abs(h.x), Mathf.Abs(h.y), Mathf.Abs(h.z));
        return true;
    }

    /// Dev overlay: this step's bubble boxes (the first `players` are other players' limbs, the rest props).
    internal int DevBubble(out Vector3[] centre, out Quaternion[] rot, out Vector3[] half, out int players)
    {
        centre = _bubC; rot = _bubR; half = _bubH; players = _bubblePlayers;
        return Active ? _bubbleCount : 0;
    }

    /// The bubble box `p` is in or on (within `margin`), or -1.
    private int BubbleAt(Vector3 p, float margin)
    {
        for (int i = 0; i < _bubbleCount; i++)
        {
            Vector3 l = Quaternion.Inverse(_bubR[i]) * (p - _bubC[i]), h = _bubH[i] + Vector3.one * margin;
            if (Mathf.Abs(l.x) <= h.x && Mathf.Abs(l.y) <= h.y && Mathf.Abs(l.z) <= h.z) return i;
        }
        return -1;
    }

    private void AddBubbleBox(Vector3 centre, Quaternion rot, Vector3 half, Vector3 vel)
    {
        if (_bubbleCount >= MaxBubbleBoxes || half.x < 0.01f || half.y < 0.01f || half.z < 0.01f) return;
        _bubC[_bubbleCount] = centre; _bubR[_bubbleCount] = rot; _bubH[_bubbleCount] = half;
        int o = _bubbleCount++ * 13;
        // Unity -> engine: swap y/z; a rotation conjugated by that swap is (-x, -z, -y, w); local axes swap too
        _bubble[o] = centre.x; _bubble[o + 1] = centre.z; _bubble[o + 2] = centre.y;
        _bubble[o + 3] = -rot.x; _bubble[o + 4] = -rot.z; _bubble[o + 5] = -rot.y; _bubble[o + 6] = rot.w;
        _bubble[o + 7] = half.x; _bubble[o + 8] = half.z; _bubble[o + 9] = half.y;
        _bubble[o + 10] = vel.x; _bubble[o + 11] = vel.z; _bubble[o + 12] = vel.y;
    }
}
