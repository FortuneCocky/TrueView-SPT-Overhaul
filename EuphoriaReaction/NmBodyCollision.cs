using System;
using System.Linq;
using System.Reflection;
using System.Text;
using EFT;
using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// Which Unity colliders become Delirium collision: exactly what blocks an SPT player's movement
/// capsule (EFT's own PlayerStaticCollisionsMask, else EFTHardSettings.MOVEMENT_MASK), never water.
/// Baking every layer (~0) turned water planes into floors and bullet-only detail meshes and
/// walk-through props into obstacles.
/// </summary>
internal partial class NmBody
{
    private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // Ground hits higher than this above the feet are obstacles (solid columns), not floor.
    private const float StepHeight = 0.45f;

    private static int _worldMask;
    private static bool _maskResolved;

    /// Layers the level bake and every world ray use.
    internal static int WorldMask
    {
        get
        {
            if (!_maskResolved) ResolveWorldMask();
            return _worldMask;
        }
    }

    private static void ResolveWorldMask()
    {
        _maskResolved = true;
        string source = null;
        int mask = 0, water = 0;
        try
        {
            Type[] types = typeof(Player).Assembly.GetTypes();
            Type lmc = types.FirstOrDefault(t => t.Name == "LayersMaskController");
            Type hard = types.FirstOrDefault(t => t.Name == "EFTHardSettings");

            mask = MaskOf(StaticValue(lmc, "PlayerStaticCollisionsMask"));
            if (mask != 0) source = "LayersMaskController.PlayerStaticCollisionsMask";
            if (mask == 0)
            {
                object inst = StaticValue(hard, "Instance");
                mask = MaskOf(InstanceValue(inst, "MOVEMENT_MASK"));
                if (mask != 0) source = "EFTHardSettings.MOVEMENT_MASK";
            }
            water = MaskOf(StaticValue(lmc, "WaterLayer"));
        }
        catch (Exception e) { EuphoriaPlugin.Log("Delirium: collision mask lookup failed: " + e.Message); }

        int waterByName = LayerMask.NameToLayer("Water");
        if (waterByName >= 0) water |= 1 << waterByName;
        if (mask == 0)
        {
            mask = ~0;
            source = "all layers (EFT's player mask not found)";
        }
        _worldMask = mask & ~water;

        var names = new StringBuilder();
        for (int i = 0; i < 32; i++)
            if ((_worldMask & (1 << i)) != 0)
                names.Append(names.Length > 0 ? ", " : "").Append(string.IsNullOrEmpty(LayerMask.LayerToName(i)) ? i.ToString() : LayerMask.LayerToName(i));
        EuphoriaPlugin.Log($"Delirium: collision from {source}, water excluded: {names}");
    }

    private static int MaskOf(object v) => v is LayerMask lm ? lm.value : v is int i ? i : 0;

    private static object StaticValue(Type t, string name)
    {
        if (t == null) return null;
        FieldInfo f = t.GetField(name, AnyStatic);
        if (f != null) return f.GetValue(null);
        PropertyInfo p = t.GetProperty(name, AnyStatic);
        return p != null && p.GetIndexParameters().Length == 0 ? p.GetValue(null, null) : null;
    }

    private static object InstanceValue(object o, string name)
    {
        if (o == null) return null;
        FieldInfo f = o.GetType().GetField(name, AnyInstance);
        if (f != null) return f.GetValue(o);
        PropertyInfo p = o.GetType().GetProperty(name, AnyInstance);
        return p != null && p.GetIndexParameters().Length == 0 ? p.GetValue(o, null) : null;
    }

    private const float ThinShape = 0.35f;     // m: a mesh this thin (or less) is a rail/beam/pipe/lip, baked as its bounds
    private const float MaxThinLength = 8f;    // m: ...and no longer than this (a long wall isn't a rail)
    private const float MaxVault = 1.3f;   // m: obstacles lower than this can be gone over (rails, ledges)
    private const float VaultClear = 0.15f; // m the pelvis must be past the obstacle
    private bool _vaultLogged;

    /// The root was blocked at hip height by `wall`. True (and where to stand) when that obstacle
    /// is low, the body has really gone over it, and there's walkable floor on the far side that a
    /// standing body fits on: then the root follows the body over instead of pulling it back.
    private bool CanFollowOver(Vector3 root, Vector3 want, Vector3 dir, float dist, RaycastHit wall, Vector3 pelvis, out Vector3 landed)
    {
        landed = want;
        // low: nothing in the way at vault height (a wall would block there too)
        if (RayWorld(root + Vector3.up * MaxVault, dir, dist + 0.3f, out _)) return false;
        // over: the pelvis is clearly past the obstacle
        Vector3 rel = pelvis - wall.point; rel.y = 0f;
        if (Vector3.Dot(rel, dir) < VaultClear) return false;
        // floor on the far side, walkable, within a step up or a fall down
        if (!RayWorld(new Vector3(want.x, root.y + MaxVault + 0.2f, want.z), Vector3.down, MaxVault + 3.4f, out RaycastHit floor)) return false;
        if (floor.normal.y < 0.6f || floor.point.y > root.y + 1.2f || floor.point.y < root.y - 3f) return false;
        // and a standing body fits there
        landed = new Vector3(want.x, floor.point.y, want.z);
        if (Physics.CheckCapsule(landed + Vector3.up * 0.45f, landed + Vector3.up * 1.5f, 0.28f, WorldMask & ~CharacterLayers(_p), QueryTriggerInteraction.Ignore))
            return false;
        if (!_vaultLogged)
        {
            _vaultLogged = true;
            EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} went over an obstacle - root follows the body to the far side ({floor.point.y - root.y:+0.00;-0.00} m)");
        }
        return true;
    }

    private const float GroundCell = 0.35f;  // m between ground samples near him (continuous surface)
    private const float GroundNear = 5.6f;   // m: continuous surface out to here, per-point squares beyond
    private const float GroundSeam = 0.15f;  // m: a cell whose corners differ more spans a step, not a slope
    private const float StepRiserMax = 0.6f; // m: a height change up to this between samples gets a closed step face

    /// One downward ground sample. True = floor at gh (for the ground surface). An obstacle top
    /// higher than a step becomes a solid column from the floor up (false); one already baked as its
    /// real shape is left to it (false); walls are for the wall pass (false).
    private bool GroundSample(Vector3 from, int mask, Vector3 feet, System.Collections.Generic.HashSet<Collider> baked,
                              System.Collections.Generic.List<float> pos, System.Collections.Generic.List<uint> tris, ref int columns, out RaycastHit gh,
                              int bakeRay = -1)
    {
        // bakeRay: this sample's ray from the bake's batch (NmBodyBakeRays), already cast
        bool hit = bakeRay >= 0 ? BakeRayHit(bakeRay, out gh) : GroundRay(from, mask, out gh);
        if (!hit || gh.normal.y < 0.35f) return false;
        float up = gh.point.y - feet.y;
        if (up <= StepHeight) return true;
        if (baked.Contains(gh.collider)) return false; // a rail keeps the open space under it
        // A lone patch up there had no sides, so catch-steps slipped between wall patches and
        // climbed onto it: a solid column from the floor up instead.
        float bottom = feet.y - 0.1f;
        var colCentre = new Vector3(gh.point.x, (gh.point.y + bottom) * 0.5f, gh.point.z);
        var colHalf = new Vector3(0.22f, (gh.point.y - bottom) * 0.5f, 0.22f);
        if (BlocksBody(colCentre, colHalf, Quaternion.identity, feet)) return false; // never inside the doll
        Box(pos, tris, colCentre, colHalf, Quaternion.identity);
        columns++;
        WallPoints.Add(gh.point);
        return false;
    }

    // A flat square on the surface at gh, along its normal.
    private static void GroundSquare(System.Collections.Generic.List<float> pos, System.Collections.Generic.List<uint> tris, RaycastHit gh, float half)
    {
        Vector3 gn = gh.normal;
        Vector3 gr = Vector3.Cross(gn, Vector3.forward);
        if (gr.sqrMagnitude < 1e-4f) gr = Vector3.Cross(gn, Vector3.right);
        gr = gr.normalized * half;
        Vector3 gu = Vector3.Cross(gn, gr).normalized * half;
        Vector3 go = gh.point;
        Quad(pos, tris, go - gr - gu, go + gr - gu, go + gr + gu, go - gr + gu, gn);
    }

    private const float SupportUpDrag = 2.5f;   // m the root may climb onto what the body rests on while dragged
    private const float SupportUp = 1.3f;       // ...otherwise (tables, low crates he plausibly lands on)
    private const float SupportDown = 3f;       // m it may drop to follow a body that fell off something
    private float MaxDrop => _airborne ? 60f : SupportDown; // taken over in the air: follow him all the way down
    private const float SupportStep = 0.25f;    // m: smaller height changes are ordinary floor
    private const float SupportSettle = 0.25f;  // s the body must rest at the new height first
    private const float SupportRest = 1.2f;     // m: pelvis at most this high above the surface (resting/standing on it)
    private readonly float[] _sp3 = new float[3], _sv3 = new float[3];
    private float _supportSince, _supportY;
    private bool _supportLogged;

    /// The body is resting on a surface at another height than the root (dropped onto a crate,
    /// knocked off a ledge): once it has settled there and a standing body fits, the root goes there
    /// too, so the hand-back happens on top of the object instead of pulling him back down to it.
    private bool SupportFollow(Vector3 root, Vector3 pelvis, Vector3 xz, out Vector3 landed)
    {
        landed = default;
        if (!RayWorld(pelvis + Vector3.up * 0.1f, Vector3.down, SupportRest + MaxDrop + 0.2f, out RaycastHit under) || under.normal.y < 0.6f)
        { _supportSince = 0f; return false; }
        float rise = under.point.y - root.y;
        bool resting = pelvis.y - under.point.y < SupportRest;
        float maxUp = _grabbing ? SupportUpDrag : SupportUp;
        if (Mathf.Abs(rise) < SupportStep || !resting || rise > maxUp || rise < -MaxDrop) { _supportSince = 0f; return false; }

        // settled: the body's centre of mass barely moving up or down, at about this height, for a moment
        nm_agent_get_com(_agent, _sp3, _sv3);
        bool settled = Mathf.Abs(_sv3[2]) < 0.6f && new Vector2(_sv3[0], _sv3[1]).magnitude < 1.5f;
        if (!settled || Mathf.Abs(under.point.y - _supportY) > 0.15f) { _supportY = under.point.y; _supportSince = Time.time; return false; }
        if (Time.time - _supportSince < SupportSettle) return false;

        landed = new Vector3(xz.x, under.point.y, xz.z);
        if (Physics.CheckCapsule(landed + Vector3.up * 0.45f, landed + Vector3.up * 1.5f, 0.28f, WorldMask & ~CharacterLayers(_p), QueryTriggerInteraction.Ignore))
            return false; // no room to stand there (under a beam, wedged in a gap)
        if (!_supportLogged)
        {
            _supportLogged = true;
            EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} is resting on something {(rise > 0f ? "higher" : "lower")} - root follows the body ({rise:+0.00;-0.00} m){(_grabbing ? " [dragged]" : "")}");
        }
        return true;
    }

    // An n-sided bar from a to b with pointed caps (railings, pipes, spheres): rounded enough that
    // a body pivots over it instead of catching on a box edge.
    private static void Bar(System.Collections.Generic.List<float> pos, System.Collections.Generic.List<uint> tris, Vector3 a, Vector3 b, float radius, int sides = 8)
    {
        Vector3 axis = b - a;
        if (axis.sqrMagnitude < 1e-8f) axis = Vector3.up * 1e-3f;
        Vector3 n = axis.normalized;
        Vector3 u = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
        Vector3 v = Vector3.Cross(n, u);
        Vector3 tipA = a - n * radius, tipB = b + n * radius, mid = (a + b) * 0.5f;
        if (_barA.Length < sides) { _barA = new Vector3[sides]; _barB = new Vector3[sides]; }
        Vector3[] ra = _barA, rb = _barB;
        for (int i = 0; i < sides; i++)
        {
            float t = i * Mathf.PI * 2f / sides;
            Vector3 o = (u * Mathf.Cos(t) + v * Mathf.Sin(t)) * radius;
            ra[i] = a + o; rb[i] = b + o;
        }
        for (int i = 0; i < sides; i++)
        {
            int j = (i + 1) % sides;
            Vector3 side = (ra[i] + ra[j] + rb[i] + rb[j]) * 0.25f;
            Quad(pos, tris, ra[i], ra[j], rb[j], rb[i], side - (mid + n * Vector3.Dot(side - mid, n)));
            Tri(pos, tris, tipA, ra[j], ra[i], (ra[i] + ra[j] + tipA) / 3f - mid);
            Tri(pos, tris, tipB, rb[i], rb[j], (rb[i] + rb[j] + tipB) / 3f - mid);
        }
    }

    // corner and ring buffers for Box and Bar (bakes run one at a time, on the main thread)
    private static readonly Vector3[] _boxK = new Vector3[8];
    private static Vector3[] _barA = new Vector3[8], _barB = new Vector3[8];
    private static readonly int[] BoxFaces = { 0, 1, 3, 2, 4, 6, 7, 5, 0, 4, 5, 1, 2, 3, 7, 6, 0, 2, 6, 4, 1, 5, 7, 3 };

    // A closed box (6 outward faces) for the engine.
    private static void Box(System.Collections.Generic.List<float> pos, System.Collections.Generic.List<uint> tris, Vector3 centre, Vector3 half, Quaternion rot)
    {
        Vector3[] k = _boxK;
        for (int i = 0; i < 8; i++)
            k[i] = centre + rot * Vector3.Scale(half, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
        int[] q = BoxFaces;
        for (int f = 0; f < 6; f++)
        {
            Vector3 a = k[q[f * 4]], b = k[q[f * 4 + 1]], c = k[q[f * 4 + 2]], d = k[q[f * 4 + 3]];
            Quad(pos, tris, a, b, c, d, (a + b + c + d) * 0.25f - centre);
        }
    }
}
