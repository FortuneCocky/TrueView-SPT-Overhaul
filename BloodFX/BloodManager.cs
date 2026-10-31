using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Ballistics;
using UnityEngine;
using UnityEngine.Rendering;

namespace BloodFX;

/// <summary>
/// Owns the shared particle systems, the stream mesh and the decal batch,
/// turns hits into spurts, sprays, mist and delayed splats, and runs wound
/// drips and death pools out of fixed-size slot arrays. Nothing on the hit
/// path allocates.
/// </summary>
public class BloodManager : MonoBehaviour
{
    public static BloodManager Instance { get; private set; }

    private struct Pending
    {
        public Vector3 Pos, Normal, Along;
        public float Due, Size0, Size1, Grow, Aspect, Life;
        public int Cell;
        public Color32 Col;
        public bool Active;
        public bool Impact;        // also play the volumetric splash when it lands
        public bool NoDecal;       // splash only, the stain is made of stuck drops
        public float ImpactPower, FloorY, Light;
        public Vector3 Incoming;
    }

    private struct PendingDrop
    {
        public Vector3 Pos, Normal, Along;
        public float Due, R, Elong;
        public Color Col;
        public bool Active;
    }

    private struct PoolJob
    {
        public Transform T;
        public float Due, Size;
        public bool Active;
    }

    /// <summary>Spurt shapes, picked per hit; see SpawnSpurts.</summary>
    private enum Spurt { ExitJet, ExitBurst, BackSpatter, Arterial, Gush, HeadBurst, Fan, Dribble, Pour }

    private const int PendingSlots = 192, WoundSlots = 24, PoolSlots = 32, PendingDropSlots = 768;
    private static readonly Color DropBase = new Color(0.5f, 0.03f, 0.025f, 1f);
    private static readonly Color StreamBase = new Color(0.34f, 0.014f, 0.012f, 1f); // the floor stains' tone (DecalBase)
    private static readonly Color DecalBase = new Color(0.36f, 0.015f, 0.012f, 0.95f);
    private static readonly Color PoolBase = new Color(0.26f, 0.008f, 0.008f, 0.94f);

    private BloodFXPlugin _cfg;
    private Tier _tier;
    private bool _ready;
    private ParticleSystem _drops, _mist, _blobs;
    private ParticleSystem.EmitParams _blobParams;
    private DecalBatch _decals;
    private BloodStreams _streams;
    private BodyDecals _body;
    private SkinnedMarks _marks;
    private StuckDrops _stuck;
    private BloodPools _puddles, _splats;
    private BloodTrails _trails;
    private BrainChunks _brains;
    private HeadGib _heads;
    private int _decapitations, _halfHeads;
    // frame-time load: 1 = full effects, down to 0.3 when frames run slower than usual
    private float _load = 1f, _fastDt, _slowDt;
    private readonly PendingDrop[] _pendingDrops = new PendingDrop[PendingDropSlots];
    private int _pendingDropNext;
    private static readonly Color WoundBase = new Color(0.55f, 0.03f, 0.025f, 1f);
    private static readonly Color ArmorBase = new Color(0.42f, 0.42f, 0.4f, 0.92f);
    private readonly Pending[] _pending = new Pending[PendingSlots];
    private readonly float[] _dripEnd = new float[WoundSlots];
    private readonly PoolJob[] _pools = new PoolJob[PoolSlots];
    private readonly HashSet<int> _pooledBodies = new HashSet<int>();
    private int _pendingNext;
    private int _mask;
    private float _worldCheck, _statsAt;
    private object _world;
    private int _frame, _hitsThisFrame;
    private int _seen, _spawned, _skipArmor, _skipType, _skipFar, _skipBudget, _skipPlayer, _lastSeen;
    private readonly Vector3[] _shDir = new Vector3[1];
    private readonly Color[] _shOut = new Color[1];

    private System.EventHandler _onTier, _onEnabled;

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        _cfg = BloodFXPlugin.Instance;
        Shader sprite = FindShader("Sprites/Default", "Legacy Shaders/Particles/Alpha Blended", "Mobile/Particles/Alpha Blended", "UI/Default");
        Shader solid = FindShader("Hidden/Internal-Colored");
        if (sprite == null)
        {
            BloodFXPlugin.Log.LogError("BloodFX: no usable transparent shader found, effects disabled");
            return;
        }
        BloodFXPlugin.Log.LogInfo($"BloodFX: using shader {sprite.name}, volumetric shader {(solid != null ? solid.name : "none (falls back to " + sprite.name + ")")}");

        var dropMat = new Material(sprite) { name = "BloodFX_Drop", mainTexture = BloodTextures.Droplet(), renderQueue = 3001 };
        var mistMat = new Material(sprite) { name = "BloodFX_Mist", mainTexture = BloodTextures.Mist(), renderQueue = 3002 };
        var decalMat = new Material(sprite) { name = "BloodFX_Decal", mainTexture = BloodTextures.DecalAtlas(), renderQueue = 2990 };
        Material liquidMat = solid != null ? SolidMaterial(solid) : new Material(sprite) { name = "BloodFX_Liquid", renderQueue = 3000 };

        _tier = _cfg.CurrentTier();
        _mask = LayersMaskController.HighPolyWithTerrainNoGrassMask;
        _drops = MakeSystem("BloodFX_Drops", dropMat, 0);
        _mist = MakeSystem("BloodFX_Mist", mistMat, 1);
        _blobs = MakeSystem("BloodFX_Blobs", liquidMat, 2);
        _blobs.gameObject.AddComponent<BlobHits>().Init(_blobs, this);
        _decals = new DecalBatch(transform, decalMat, _tier.Decals, _mask);
        var bodyMat = new Material(sprite) { name = "BloodFX_Body", mainTexture = BloodTextures.BodyAtlas(), renderQueue = 2995 };
        _body = new BodyDecals(transform, bodyMat, BodyCapacity());
        _marks = new SkinnedMarks(bodyMat, 64);
        _stuck = new StuckDrops(transform, liquidMat, StuckCapacity());
        _puddles = new BloodPools(transform, liquidMat, PuddleCapacity(), _mask);
        _splats = new BloodPools(transform, liquidMat, SplatCapacity(), _mask);
        _trails = new BloodTrails(transform, liquidMat, TrailCapacity(), _mask);
        _streams = new BloodStreams(transform, liquidMat, _tier.Streams, _tier.Rings);
        _brains = new BrainChunks(transform, liquidMat, BrainCapacity(), _mask);
        _brains.Landed = OnChunkLanded;
        _heads = new HeadGib(transform, liquidMat, _mask);
        _heads.Landed = OnChunkLanded;
        ApplyTier();
        _onTier = (s, e) => { if (this != null) ApplyTier(); };
        _onEnabled = (s, e) => { if (this != null && !_cfg.Enabled.Value) ClearAll(); };
        _cfg.Quality.SettingChanged += _onTier;
        _cfg.MaxParticles.SettingChanged += _onTier;
        _cfg.MaxDecals.SettingChanged += _onTier;
        _cfg.MaxStreams.SettingChanged += _onTier;
        _cfg.Enabled.SettingChanged += _onEnabled;

        _ready = true;
    }

    private void OnDestroy()
    {
        if (_cfg != null && _onTier != null)
        {
            _cfg.Quality.SettingChanged -= _onTier;
            _cfg.MaxParticles.SettingChanged -= _onTier;
            _cfg.MaxDecals.SettingChanged -= _onTier;
            _cfg.MaxStreams.SettingChanged -= _onTier;
            _cfg.Enabled.SettingChanged -= _onEnabled;
        }
        if (Instance == this)
        {
            Instance = null;
            BloodFXPlugin.Log.LogWarning("BloodFX: manager object was destroyed (it will be recreated on the next hit)");
        }
    }

    private static Shader FindShader(params string[] names)
    {
        foreach (string n in names)
        {
            Shader s = Shader.Find(n);
            if (s != null) return s;
        }
        return null;
    }

    /// <summary>Opaque, depth-writing, unlit vertex colour: the liquid is shaded per vertex on the CPU.</summary>
    private static Material SolidMaterial(Shader shader)
    {
        var m = new Material(shader) { name = "BloodFX_Liquid", renderQueue = 2460 };
        m.SetInt("_SrcBlend", (int)BlendMode.One);
        m.SetInt("_DstBlend", (int)BlendMode.Zero);
        m.SetInt("_Cull", (int)CullMode.Off);
        m.SetInt("_ZWrite", 1);
        m.SetInt("_ZTest", (int)CompareFunction.LessEqual);
        return m;
    }

    /// <param name="kind">0 stretched droplets, 1 mist puffs, 2 3D blobs.</param>
    private ParticleSystem MakeSystem(string name, Material mat, int kind)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = true;
        main.duration = 1f;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        main.startSpeed = 0f;
        main.gravityModifier = kind == 1 ? 0.03f : 1f;
        main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
        main.startSize3D = kind == 2; // blobs are slightly squashed
        var emission = ps.emission;
        emission.enabled = false;
        var shape = ps.shape;
        shape.enabled = false;

        if (kind != 2)
        {
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                kind == 0
                    ? new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.75f), new GradientAlphaKey(0f, 1f) }
                    : new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.12f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(grad);
        }
        if (kind != 1)
        {
            // drops hit the world instead of flying through walls; volumetric ones report
            // where they hit so they can stay stuck to the surface
            var coll = ps.collision;
            coll.enabled = true;
            coll.type = ParticleSystemCollisionType.World;
            coll.mode = ParticleSystemCollisionMode.Collision3D;
            coll.quality = kind == 2 ? ParticleSystemCollisionQuality.High : ParticleSystemCollisionQuality.Medium;
            coll.collidesWith = _mask;
            coll.lifetimeLoss = 1f;
            coll.bounce = 0f;
            coll.radiusScale = 0.5f;
            coll.enableDynamicColliders = false;
            coll.sendCollisionMessages = kind == 2;
        }
        if (kind == 1)
        {
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.45f, 1f, 1f));
            var drag = ps.limitVelocityOverLifetime;
            drag.enabled = true;
            drag.drag = 3f;
        }
        else if (kind == 2)
        {
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.85f));
        }

        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.lightProbeUsage = LightProbeUsage.Off;
        r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        r.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
        switch (kind)
        {
            case 0:
                r.renderMode = ParticleSystemRenderMode.Stretch;
                r.velocityScale = 0.05f;
                r.lengthScale = 1.5f;
                r.maxParticleSize = 0.1f;
                r.trailMaterial = mat;
                break;
            case 1:
                r.renderMode = ParticleSystemRenderMode.Billboard;
                r.maxParticleSize = 0.2f; // overdraw cap: a puff never fills the screen
                break;
            default:
                r.renderMode = ParticleSystemRenderMode.Mesh;
                r.mesh = BlobMesh();
                r.alignment = ParticleSystemRenderSpace.World;
                break;
        }
        ps.Play();
        return ps;
    }

    /// <summary>Low-poly sphere with wet shading baked in vertex colour (lit from above, glint up-front).</summary>
    private static Mesh BlobMesh()
    {
        const int seg = 8, rings = 5;
        var v = new List<Vector3>();
        var c = new List<Color32>();
        var t = new List<int>();
        for (int i = 0; i <= rings; i++)
        {
            float phi = Mathf.PI * i / rings;
            for (int j = 0; j <= seg; j++)
            {
                float th = 2f * Mathf.PI * j / seg;
                var n = new Vector3(Mathf.Sin(phi) * Mathf.Cos(th), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(th));
                v.Add(n * 0.5f);
                float diff = 0.45f + 0.55f * Mathf.Max(0f, n.y);
                float s = Mathf.Max(0f, Vector3.Dot(n, new Vector3(0.3f, 0.8f, 0.5f).normalized));
                s = Mathf.Pow(s, 12f) * 0.8f;
                c.Add(new Color(Mathf.Min(1f, diff + s), Mathf.Min(1f, diff * 0.95f + s), Mathf.Min(1f, diff * 0.95f + s), 1f));
            }
        }
        for (int i = 0; i < rings; i++)
        for (int j = 0; j < seg; j++)
        {
            int a = i * (seg + 1) + j, b = a + seg + 1;
            t.Add(a); t.Add(b); t.Add(a + 1);
            t.Add(a + 1); t.Add(b); t.Add(b + 1);
        }
        var m = new Mesh { name = "BloodFX_Blob" };
        m.SetVertices(v);
        m.SetColors(c);
        m.SetTriangles(t, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    private void ApplyTier()
    {
        if (_drops == null) return;
        _tier = _cfg.CurrentTier();
        var dm = _drops.main;
        dm.maxParticles = _tier.Particles;
        var mm = _mist.main;
        mm.maxParticles = Mathf.Max(16, _tier.Particles / 6);
        var bm = _blobs.main;
        bm.maxParticles = Mathf.Max(64, _tier.Particles / 2);
        var trails = _drops.trails;
        trails.enabled = _tier.Trails;
        if (_tier.Trails)
        {
            trails.ratio = 0.25f;
            trails.lifetime = new ParticleSystem.MinMaxCurve(0.07f);
            trails.dieWithParticles = true;
            trails.inheritParticleColor = true;
            trails.widthOverTrail = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0f));
        }
        if (_decals.Capacity != Mathf.Clamp(_tier.Decals, 8, 16000)) _decals.Resize(_tier.Decals);
        if (_body.Capacity != BodyCapacity()) _body.Resize(BodyCapacity());
        if (_stuck.Capacity != Mathf.Clamp(StuckCapacity(), 32, 8000)) _stuck.Resize(StuckCapacity());
        if (_puddles.Capacity != Mathf.Clamp(PuddleCapacity(), 4, 128)) _puddles.Resize(PuddleCapacity());
        if (_splats.Capacity != Mathf.Clamp(SplatCapacity(), 4, 128)) _splats.Resize(SplatCapacity());
        if (_trails.Capacity != Mathf.Clamp(TrailCapacity(), 4, 128)) _trails.Resize(TrailCapacity());
        if (_brains.Capacity != Mathf.Clamp(BrainCapacity(), 16, 512)) _brains.Resize(BrainCapacity());
        if (_streams.Capacity != Mathf.Clamp(_tier.Streams, 4, 512) || _streams.Rings != _tier.Rings)
            _streams.Resize(_tier.Streams, _tier.Rings);
    }

    private void ClearAll()
    {
        if (!_ready || _drops == null) return;
        _drops.Clear();
        _mist.Clear();
        _blobs.Clear();
        _decals.Clear();
        _body.Clear();
        _marks.Clear();
        _stuck.Clear();
        _puddles.Clear();
        _splats.Clear();
        _trails.Clear();
        _brains.Clear();
        _heads.Clear();
        for (int i = 0; i < PendingDropSlots; i++) _pendingDrops[i].Active = false;
        _streams.Clear();
        for (int i = 0; i < PendingSlots; i++) _pending[i].Active = false;
        for (int i = 0; i < WoundSlots; i++) _dripEnd[i] = 0f;
        _streams.ClearDetach();
        for (int i = 0; i < PoolSlots; i++) _pools[i].Active = false;
        _pooledBodies.Clear();
        _wounds.Clear();
        _bleedDripUntil.Clear();
    }

    // ---------------------------------------------------------------- hits

    internal void OnHit(Player victim, ref DamageInfo di, EBodyPart part)
    {
        if (!_ready || !_cfg.Enabled.Value) return;
        _seen++;
        if (victim.IsYourPlayer && !_cfg.PlayerHits.Value) { _skipPlayer++; return; }
        if (di.BlockedBy.HasValue || di.DeflectedBy.HasValue)
        {
            _skipArmor++;
            if (_cfg.ArmorMarks.Value && di.HitCollider != null) ArmorMark(victim, ref di);
            return;
        }
        if (di.Damage <= 0f) { _skipArmor++; return; }
        EDamageType type = di.DamageType;
        const EDamageType ballisticTypes = EDamageType.Bullet | EDamageType.Sniper | EDamageType.Melee;
        const EDamageType blastTypes = EDamageType.Explosion | EDamageType.GrenadeFragment | EDamageType.Landmine
            | EDamageType.Artillery | EDamageType.Btr | EDamageType.ThermobaricExplosion;
        bool ballistic = (type & ballisticTypes) != 0;
        bool blast = (type & blastTypes) != 0;
        if (!ballistic && !blast) { _skipType++; return; }
        if (di.HitCollider != null) RememberWound(victim, di.HitCollider.transform, di.HitPoint,
            di.HitNormal.sqrMagnitude > 0.01f ? di.HitNormal : -di.Direction);
        bool melee = (type & EDamageType.Melee) != 0 && !blast;

        bool dead = victim.HealthController != null && !victim.HealthController.IsAlive;
        if (dead && _cfg.DeathPools.Value) QueuePool(victim);

        if (Time.frameCount != _frame) { _frame = Time.frameCount; _hitsThisFrame = 0; }
        if (++_hitsThisFrame > Mathf.Max(1, Mathf.RoundToInt(_tier.HitsPerFrame * _load))) { _skipBudget++; return; } // shotgun storms: the rest are dropped

        Vector3 pt = di.HitPoint;
        Vector3 dir = di.Direction;
        if (dir.sqrMagnitude < 1e-4f) dir = Random.onUnitSphere;
        dir.Normalize();

        float maxDist = _cfg.MaxDistance.Value;
        float dist = 0f;
        bool onScreen = true;
        if (Viewer(out Vector3 eye, out Vector3 look))
        {
            Vector3 to = pt - eye;
            dist = to.magnitude;
            onScreen = dist < 0.01f || Vector3.Dot(look, to / dist) > 0.35f;
        }
        if (dist > maxDist * 1.5f) { _skipFar++; return; }
        _spawned++;
        bool particles = dist <= maxDist && (onScreen || dist < 5f);
        float lod = Mathf.Lerp(1f, 0.3f, Mathf.InverseLerp(15f, maxDist, dist));
        float intensity = _cfg.Intensity.Value;
        float power = Mathf.Clamp(di.Damage / 40f, 0.4f, 2f) * intensity;
        float sizeScale = Mathf.Clamp(Mathf.Sqrt(intensity), 0.5f, 1.6f);
        // far away everything is drawn a bit thicker so it stays readable
        float visBoost = 1f + Mathf.Clamp01(dist / 40f);
        bool head = part == EBodyPart.Head;
        float light = LightAt(pt, -dir);
        float now = Time.time;
        float life = _cfg.DecalLifetime.Value;
        Color32 decalCol = Tint(DecalBase, light * Random.Range(0.8f, 1f), 1f);

        RaycastHit hit;
        float floorY = pt.y - 1.6f;
        Vector3 floorN = Vector3.up;
        if (Physics.Raycast(pt + Vector3.up * 0.1f, Vector3.down, out hit, 3f, _mask, QueryTriggerInteraction.Ignore))
        {
            floorY = hit.point.y;
            floorN = hit.normal;
        }

        // killing headshots only: a head that's still alive (helmet stop, graze) is never taken apart
        if (head && ballistic && !melee && _cfg.HeadDismember.Value && dist <= maxDist && di.Damage >= _cfg.DismemberMinDamage.Value
            && (dead || HeadGone(victim)) && !_heads.IsDone(victim) && Random.value < _cfg.DismemberChance.Value)
            Decapitate(victim, ref di, dir, power, sizeScale, floorY, floorN, light, decalCol, now);

        if (particles)
        {
            Color32 liquidCol = Tint(StreamBase, light, 1f);
            int n = Mathf.RoundToInt(_tier.Droplets * power * lod * (melee ? 0.5f : 1f));
            float sprayScale = sizeScale * visBoost;
            if (blast)
            {
                EmitSpray(pt, Vector3.up, n, 1.4f, 1.5f, 5f, liquidCol, sprayScale, floorY);
                EmitSpray(pt, Vector3.up, n / 2, 1.4f, 1.5f, 5f, liquidCol, sprayScale * 0.35f, floorY);
            }
            else
            {
                int exit = Mathf.RoundToInt(n * 0.7f);
                EmitSpray(pt, dir, exit, 0.3f, 2.5f, 6.5f, liquidCol, sprayScale, floorY);
                EmitSpray(pt, -dir, n - exit, 0.55f, 0.8f, 2.8f, liquidCol, sprayScale, floorY);
                EmitSpray(pt, dir, n / 2, 0.35f, 2.5f, 6.5f, liquidCol, sprayScale * 0.35f, floorY); // fine specks
            }

            if (_cfg.Streams.Value && dist <= Mathf.Min(maxDist, 40f))
                MistBurst(pt, blast ? Vector3.up : dir, power * (head ? 1.5f : 1f), sprayScale, floorY, light, now);
            if (_cfg.Streams.Value && dist <= Mathf.Min(maxDist, 50f))
                SpawnSpurts(victim, ref di, pt, dir, head, melee, blast, dead, power, sizeScale * visBoost, floorY, floorN, light, decalCol, now);
        }

        if (!melee && !blast && Physics.Raycast(pt + dir * 0.25f, dir, out hit, head ? 4f : 2.5f, _mask, QueryTriggerInteraction.Ignore))
        {
            float s = Random.Range(0.3f, 0.55f) * sizeScale * Mathf.Sqrt(power) * (head ? 1.3f : 1f);
            QueueImpact(hit.point, hit.normal, dir, now + 0.03f + hit.distance / (head ? 8f : 5f), s, power * (head ? 1.4f : 1f),
                floorY, light, decalCol);
        }
        int floorSplats = blast ? 2 : 1;
        for (int k = 0; k < floorSplats; k++)
        {
            Vector3 off = blast ? Random.insideUnitSphere * 1.2f : dir * Random.Range(0.25f, 1.3f);
            off.y = 0f;
            if (Physics.Raycast(pt + off + Vector3.up * 0.2f, Vector3.down, out hit, 3f, _mask, QueryTriggerInteraction.Ignore))
            {
                float fall = Mathf.Sqrt(2f * Mathf.Max(0.05f, hit.distance - 0.2f) / 9.81f);
                float s = Random.Range(0.25f, 0.45f) * sizeScale * Mathf.Sqrt(power);
                SpatterDrops(hit.point, hit.normal, off.sqrMagnitude > 1e-4f ? off.normalized * 0.7f + Vector3.down * 0.7f : Vector3.down,
                    now + fall, s, power * 0.6f, light);
            }
        }

        if (_cfg.WoundDecals.Value && di.HitCollider != null && dist <= maxDist)
        {
            Vector3 face = BodyFacing(ref di, dir);
            // a real entry wound: a ~1.5 cm hole in a dark ring, 3.5-6 cm across in all (fragments a bit smaller)
            float size = Mathf.Clamp(Random.Range(0.035f, 0.05f) * Mathf.Sqrt(power), 0.03f, 0.06f) * (blast ? 0.75f : 1f);
            bool run = Random.value < 0.45f;
            float runLen = Mathf.Clamp(size * Random.Range(1.5f, 2.6f) * Mathf.Clamp(power, 0.7f, 1.3f), 0.05f, 0.12f);
            int woundCell = BloodTextures.WoundCell0 + Random.Range(0, BloodTextures.WoundCells);
            int runCell = BloodTextures.RunCell0 + Random.Range(0, BloodTextures.RunCells);
            if (!(_cfg.ConformMarks.Value && _marks.TryAdd(victim, di.HitCollider.transform, pt, dir, size, woundCell,
                    Tint(WoundBase, light, 1f), _cfg.DecalLifetime.Value * 2f, run ? runLen : 0f, size * 0.26f, runCell, now)))
                _body.Add(di.HitCollider.transform, pt + face * _cfg.BodyDecalOffset.Value, face, size, woundCell, BodyDecals.ProfileWound,
                    0.035f, Tint(WoundBase, light, 1f), _cfg.DecalLifetime.Value * 2f, run, runLen, Random.Range(2f, 5f), runCell, now);
        }
        if (ballistic && _cfg.WoundDrips.Value && di.HitCollider != null && dist <= maxDist)
        {
            Vector3 nrm = di.HitNormal.sqrMagnitude > 0.01f ? di.HitNormal.normalized : -dir;
            AddDrip(di.HitCollider.transform, pt, nrm, power, sizeScale * visBoost, light, now);
        }
    }

    /// <summary>Picks a mix of spurt shapes for the hit, like a library of baked fluid variants but generated.</summary>
    private void SpawnSpurts(Player victim, ref DamageInfo di, Vector3 pt, Vector3 dir, bool head, bool melee, bool blast, bool dead,
        float power, float scale, float floorY, Vector3 floorN, float light, Color32 decalCol, float now)
    {
        Transform anchor = di.HitCollider != null ? di.HitCollider.transform : null;
        if (blast)
        {
            Spawn(Spurt.HeadBurst, pt, Vector3.up, power, scale, floorY, floorN, light, decalCol, now, null);
            Spawn(Spurt.Gush, pt, dir, power, scale, floorY, floorN, light, decalCol, now, anchor);
            return;
        }
        if (melee)
        {
            Spawn(Spurt.Fan, pt, dir, power * 0.7f, scale, floorY, floorN, light, decalCol, now, null);
            return;
        }
        if (head)
        {
            if (_cfg.BrainMatter.Value) BrainBurst(pt, dir, power, scale, light, now);
            if (_cfg.WallSplatter.Value) WallHeadSplatter(pt, dir, power, scale, floorY, light, now);
            Spawn(Spurt.HeadBurst, pt, dir, power, scale, floorY, floorN, light, decalCol, now, null);
            Spawn(Spurt.BackSpatter, pt, -dir, power, scale, floorY, floorN, light, decalCol, now, null);
            return;
        }
        Spawn(Random.value < 0.5f ? Spurt.ExitJet : Spurt.ExitBurst, pt, dir, power, scale, floorY, floorN, light, decalCol, now, null);
        if (Random.value < 0.4f) Spawn(Spurt.BackSpatter, pt, -dir, power, scale, floorY, floorN, light, decalCol, now, null);
        // the signature effect: a rope of blood pouring out of the wound for a few seconds
        if (Random.value < (dead ? 0.45f : 0.18f)) Spawn(Spurt.Pour, pt, dir, power, scale, floorY, floorN, light, decalCol, now, anchor);
        if (dead && Random.value < 0.1f) Spawn(Spurt.Pour, pt, -dir, power, scale, floorY, floorN, light, decalCol, now, anchor);
        float r = Random.value;
        if (r < 0.1f + 0.08f * power) Spawn(Spurt.Arterial, pt, dir, power, scale, floorY, floorN, light, decalCol, now, anchor);
        else if (dead ? r < 0.45f : r < 0.25f) Spawn(Spurt.Gush, pt, dir, power, scale, floorY, floorN, light, decalCol, now, anchor);
        else if (r < 0.4f) Spawn(Spurt.Dribble, pt, dir, power, scale, floorY, floorN, light, decalCol, now, anchor);
    }

    private void Spawn(Spurt kind, Vector3 pt, Vector3 dir, float power, float scale, float floorY, Vector3 floorN, float light,
        Color32 decalCol, float now, Transform anchor)
    {
        float p = Mathf.Sqrt(power);
        switch (kind)
        {
            case Spurt.ExitJet:
            {
                int n = 1;
                for (int i = 0; i < n; i++)
                    Stream(pt, Jitter(dir, 0.15f) * Random.Range(3f, 5.2f) * p, Random.Range(0.25f, 0.45f), 0.5f,
                        Random.Range(0.024f, 0.04f) * scale, 0.75f, 0f, 0f, 1f, 1f, floorY, floorN, light, decalCol, now, null, i == 0);
                break;
            }
            case Spurt.ExitBurst:
            {
                int n = Random.Range(2, 4);
                for (int i = 0; i < n; i++)
                    Stream(pt, Jitter(dir, 0.45f) * Random.Range(2.5f, 5f) * p, Random.Range(0.07f, 0.14f), 0.3f,
                        Random.Range(0.014f, 0.026f) * scale, 0.5f, 0f, 0f, 1.6f, 1f, floorY, floorN, light, decalCol, now, null, i < 2);
                break;
            }
            case Spurt.BackSpatter:
            {
                int n = Random.Range(1, 3);
                for (int i = 0; i < n; i++)
                    Stream(pt, Jitter(dir, 0.55f) * Random.Range(1.4f, 3f) * p, 0.06f, 0.25f,
                        Random.Range(0.009f, 0.016f) * scale, 0.4f, 0f, 0f, 2f, 1f, floorY, floorN, light, decalCol, now, null, false);
                break;
            }
            case Spurt.Arterial:
            {
                // pumping jet: pressure pulses at heart rate, slowly dropping
                Vector3 d = (Jitter(dir, 0.5f) + Vector3.up * 0.45f).normalized;
                Stream(pt, d * Random.Range(2.2f, 3.2f), Random.Range(1.3f, 2.2f), 0.5f,
                    Random.Range(0.013f, 0.022f) * scale, 0.55f, Random.Range(1.4f, 2f), 0.5f, 0.8f, 1f, floorY, floorN, light, decalCol, now, anchor, true);
                break;
            }
            case Spurt.Gush:
            {
                Vector3 d = dir;
                d.y = 0f;
                d = (d.normalized * 0.6f + Vector3.down * 0.4f + Random.insideUnitSphere * 0.2f).normalized;
                Stream(pt, d * Random.Range(0.6f, 1.3f), Random.Range(0.8f, 1.4f), 0.8f,
                    Random.Range(0.026f, 0.042f) * scale, 0.6f, 0f, 0f, 0.6f, 1f, floorY, floorN, light, decalCol, now, anchor, true);
                break;
            }
            case Spurt.HeadBurst:
            {
                if (dir.y > 0.95f) // blasts: radial burst
                {
                    int m = Random.Range(3, 6);
                    for (int i = 0; i < m; i++)
                        Stream(pt, Jitter(dir, 0.9f) * Random.Range(2f, 6f) * p, Random.Range(0.05f, 0.1f), 0.35f,
                            Random.Range(0.008f, 0.014f) * scale, 0.5f, 0f, 0f, 1.8f, 1f, floorY, floorN, light, decalCol, now, null, i < 2);
                    break;
                }
                // long glossy ribbons forking out of the exit wound. Straight along the bullet they'd
                // fly directly away from the shooter and be seen end-on, so the fork opens sideways
                // across the viewer's line of sight and the whole burst is slower and thicker.
                Vector3 side = Vector3.Cross(dir, Vector3.up);
                if (side.sqrMagnitude < 1e-3f) side = Vector3.right;
                side.Normalize();
                if (Viewer(out Vector3 eyeH, out _))
                {
                    Vector3 toEye = eyeH - pt;
                    Vector3 across = Vector3.Cross(toEye, Vector3.up);
                    if (across.sqrMagnitude > 1e-3f) side = across.normalized * (Vector3.Dot(across, side) < 0f ? -1f : 1f);
                }
                Vector3 baseDir = (dir * 0.6f + side * Random.Range(0.25f, 0.6f) + Vector3.up * Random.Range(0.1f, 0.3f)).normalized;
                Vector3 lift = Vector3.Cross(side, baseDir).normalized;
                int n = Random.value < 0.4f ? 3 : 2;
                float fork = Random.Range(0.25f, 0.4f);
                for (int i = 0; i < n; i++)
                {
                    float o = i - (n - 1) * 0.5f;
                    Vector3 d = (baseDir + lift * (o * fork) + side * Random.Range(-0.05f, 0.05f)).normalized;
                    Stream(pt, d * Random.Range(3.2f, 5f) * p, Random.Range(0.14f, 0.22f), 0.6f,
                        Random.Range(0.011f, 0.017f) * scale, 0.55f, 0f, 0f, 0.7f, 1f, floorY, floorN, light, decalCol, now, null, i == 0);
                }
                Color32 liquid = Tint(StreamBase, light, 1f);
                int cloud = Mathf.RoundToInt(_tier.Droplets * 1.2f * power);
                EmitSpray(pt, baseDir, cloud, 0.6f, 1.5f, 6f, liquid, scale * 0.7f, floorY);         // drops scattered around the ribbons
                EmitSpray(pt, baseDir, cloud / 2, 0.9f, 0.8f, 3.5f, liquid, scale * 0.3f, floorY);  // fine spray
                EmitSpray(pt, baseDir, 5 + _tier.Droplets / 6, 0.05f, 5.5f, 7.5f, liquid, scale * 0.7f, floorY); // leader drops out front
                break;
            }
            case Spurt.Fan:
            {
                Vector3 side = Vector3.Cross(dir, Vector3.up);
                if (side.sqrMagnitude < 1e-3f) side = Vector3.right;
                side.Normalize();
                int n = 4;
                for (int i = 0; i < n; i++)
                {
                    float f = (i / (float)(n - 1)) * 2f - 1f;
                    Vector3 d = (dir + side * f * 0.8f + Vector3.up * 0.15f).normalized;
                    Stream(pt, d * Random.Range(2f, 3.8f) * p, 0.1f, 0.3f,
                        Random.Range(0.011f, 0.018f) * scale, 0.5f, 0f, 0f, 1.5f, 1f, floorY, floorN, light, decalCol, now, null, i == 1);
                }
                break;
            }
            case Spurt.Pour:
            {
                // leaves the wound sideways, then gravity bends it into a hanging rope down to the floor
                Vector3 d = dir;
                d.y = 0f;
                if (d.sqrMagnitude < 1e-3f) d = Random.insideUnitSphere;
                d.y = 0f;
                d = (d.normalized * 0.75f + Vector3.up * 0.1f + Random.insideUnitSphere * 0.15f).normalized;
                Stream(pt, d * Random.Range(0.7f, 1.4f), Random.Range(1.2f, 2.4f), 0.9f,
                    Random.Range(0.011f, 0.018f) * scale, 0.65f, Random.Range(1.1f, 1.6f), 0.22f, 0.35f, 1f, floorY, floorN, light, decalCol, now, anchor, true);
                break;
            }
            case Spurt.Dribble:
                Stream(pt, (dir * 0.3f + Vector3.down * 0.2f) * Random.Range(0.5f, 1f), Random.Range(1f, 1.8f), 0.7f,
                    Random.Range(0.006f, 0.01f) * scale, 0.3f, 0f, 0f, 0.5f, 1f, floorY, floorN, light, decalCol, now, anchor, true);
                break;
        }
    }

    private void Stream(Vector3 origin, Vector3 vel, float emit, float life, float radius, float decay, float pulseF, float pulseD,
        float breakup, float gravity, float floorY, Vector3 floorN, float light, Color32 decalCol, float now, Transform anchor, bool splat)
    {
        var s = new BloodStreams.Spec
        {
            Origin = origin, Velocity = vel, Emit = emit, Life = life, Radius = radius, Decay = decay,
            PulseFreq = pulseF, PulseDepth = pulseD, Breakup = breakup, Gravity = gravity, FloorY = floorY,
            Light = light, Col = Tint(StreamBase, light, 1f), Anchor = anchor,
            AnchorLocal = anchor != null ? anchor.InverseTransformPoint(origin) : Vector3.zero
        };
        float speed = vel.magnitude;
        if (splat && speed > 0.5f && Physics.Raycast(origin, vel / speed, out RaycastHit wall, 2.5f, _mask, QueryTriggerInteraction.Ignore)
            && wall.normal.y < 0.6f)
        {
            s.HasWall = true;
            s.WallP = wall.point;
            s.WallN = wall.normal;
            float sz = Mathf.Clamp(radius * 14f * (1f + emit), 0.12f, 0.7f);
            QueueImpact(wall.point, wall.normal, vel / speed, now + wall.distance / speed, sz, 0.5f, floorY, light, decalCol);
        }
        else if (splat)
        {
            float tl = BloodStreams.LandTime(origin, vel, 9.81f * gravity, floorY);
            if (tl > 0f && tl < 3f)
            {
                Vector3 land = origin + vel * tl;
                land.y = floorY;
                Vector3 flat = new Vector3(vel.x, 0f, vel.z);
                float hs = flat.magnitude;
                if (hs > 0.8f)
                {
                    // hit the ground moving: sometimes the blood drags out into a smear along the travel direction
                    if (Random.value < 0.35f)
                    {
                        float len = Mathf.Clamp(radius * 14f * (1f + emit) * Mathf.Sqrt(hs), 0.15f, 0.8f);
                        float width = Mathf.Clamp(radius * 1.8f * (1f + emit * 0.3f), 0.012f, 0.055f);
                        Trail(land, floorN, flat / hs, len, width, now + tl, Mathf.Clamp(len / (hs * 0.6f), 0.3f, 1.6f), light);
                    }
                }
                else
                {
                    // a pour feeds a pool that keeps spreading for as long as it runs
                    float sz = Mathf.Clamp(radius * 16f * (1f + emit), 0.15f, 0.9f);
                    _puddles.Add(land, floorN, sz * 0.4f, Tint(PoolBase, light, 1f), now + tl,
                        Mathf.Max(1f, life * 1.5f), _cfg.DecalLifetime.Value * 1.5f);
                }
            }
        }
        _streams.Spawn(ref s, now);

        // thin strands peeling off the main flow
        if (radius < 0.016f || emit < 0.15f || _load < 0.7f) return;
        int strands = Random.Range(1, 3);
        for (int i = 0; i < strands; i++)
        {
            BloodStreams.Spec t = s;
            t.Velocity = vel * Random.Range(0.85f, 1.1f) + Random.insideUnitSphere * (0.12f * speed);
            t.Radius = radius * Random.Range(0.22f, 0.35f);
            t.Emit = emit * Random.Range(0.5f, 0.9f);
            t.Breakup = breakup * 1.6f + 0.4f;
            _streams.Spawn(ref t, now);
        }
    }

    /// <summary>
    /// The burst when a round goes through: a dense dark-red puff that fans out and in a
    /// fraction of a second tears into a web of thin strands and fine droplets. Built from
    /// very thin, fast, short-lived streams that break up hard, plus a cloud of tiny 3D drops.
    /// </summary>
    private void MistBurst(Vector3 pt, Vector3 dir, float power, float scale, float floorY, float light, float now)
    {
        float p = Mathf.Sqrt(Mathf.Clamp(power, 0.3f, 2.5f));
        Color col = (Color)Tint(StreamBase, light, 1f);
        int strands = Mathf.Max(2, Mathf.RoundToInt(Mathf.Clamp(_tier.Mist + 2, 3, 10) * _load));
        for (int i = 0; i < strands; i++)
        {
            var s = new BloodStreams.Spec
            {
                Origin = pt, Velocity = Jitter(dir, 0.6f) * Random.Range(2.5f, 6f) * p,
                Emit = Random.Range(0.03f, 0.07f), Life = Random.Range(0.12f, 0.2f),
                Radius = Random.Range(0.003f, 0.007f) * scale, Decay = 0.5f, Breakup = Random.Range(1.8f, 2.4f),
                Gravity = 1f, FloorY = floorY, Light = light, Col = col
            };
            _streams.Spawn(ref s, now);
        }
        Color32 c32 = Tint(StreamBase, light, 1f);
        EmitSpray(pt, dir, 4 + _tier.Mist, 0.35f, 0.8f, 2.2f, c32, scale * 1.3f, floorY);       // dense core
        EmitSpray(pt, dir, _tier.Droplets, 0.7f, 1.5f, 5.5f, c32, scale * 0.22f, floorY);      // fine droplets of the web
    }

    /// <summary>
    /// Headshot gore: the round carries a tight ball of brain matter out of the exit wound, which in the
    /// first instant tears into a few big wrinkled lumps fanning out along the shot, a spray of small
    /// fragments, and some skull chips. Lumps keep shedding pieces as they fly; everything falls,
    /// tumbles, and splats where it lands. Real-world sizes: lumps 2-5 cm, fragments under 2 cm.
    /// </summary>
    private void BrainBurst(Vector3 pt, Vector3 dir, float power, float scale, float light, float now)
    {
        float s = Mathf.Clamp(scale, 0.8f, 1.25f);
        float life = _cfg.DecalLifetime.Value;
        Vector3 exit = pt + dir * 0.08f;
        if (Physics.Linecast(pt, exit, _mask, QueryTriggerInteraction.Ignore)) exit = pt;
        float speed = Random.Range(3f, 5f) * Mathf.Clamp(Mathf.Sqrt(power), 0.8f, 1.3f);
        Vector3 g = Physics.gravity;
        int lumps = Random.Range(3, 6);
        for (int i = 0; i < lumps; i++)
        {
            Vector3 d = (dir + Random.insideUnitSphere * 0.45f + Vector3.up * 0.15f).normalized;
            Vector3 v = d * speed * Random.Range(0.6f, 1.05f);
            _brains.Spawn(BrainChunks.Kind.Lump, exit + Random.insideUnitSphere * 0.015f, v, Random.Range(0.011f, 0.022f) * s, light, now, life);
            // pieces tearing off the lump in flight, from where it is by then
            int shed = Random.Range(1, 4);
            for (int k = 0; k < shed; k++)
            {
                float t = Random.Range(0.04f, 0.3f);
                Vector3 at = exit + v * t + 0.5f * t * t * g;
                if (Physics.Linecast(exit, at, _mask, QueryTriggerInteraction.Ignore)) continue;
                _brains.Spawn(BrainChunks.Kind.Fragment, at, v + g * t + Random.onUnitSphere * Random.Range(0.5f, 1.5f),
                    Random.Range(0.004f, 0.009f) * s, light, now + t, life);
            }
        }
        int frags = Mathf.RoundToInt(Random.Range(4, 9) * _load);
        for (int i = 0; i < frags; i++)
            _brains.Spawn(BrainChunks.Kind.Fragment, exit, Jitter(dir, 0.7f) * speed * Random.Range(0.5f, 1.2f),
                Random.Range(0.003f, 0.007f) * s, light, now, life);
        int chips = Random.Range(2, 4);
        for (int i = 0; i < chips; i++)
            _brains.Spawn(BrainChunks.Kind.Bone, exit, Jitter(dir, 0.6f) * speed * Random.Range(0.8f, 1.3f),
                Random.Range(0.008f, 0.015f) * s, light, now, life);
    }

    /// <summary>
    /// A headshot next to a wall paints it, in the order real spatter arrives: a few leading drops,
    /// then within ~80 ms a wide ragged field of small drops (wider than tall), then over the next
    /// quarter second the heavy, slower mass fills a dense dark core, and from there two or three
    /// thick drips start to creep straight down and keep lengthening for seconds. Thin jets fly
    /// from the head and flatten on the wall so the blood is seen crossing the gap. Added on top
    /// of the usual headshot effects.
    /// </summary>
    private void WallHeadSplatter(Vector3 pt, Vector3 dir, float power, float scale, float floorY, float light, float now)
    {
        if (!Physics.Raycast(pt + dir * 0.05f, dir, out RaycastHit wall, 2.5f, _mask, QueryTriggerInteraction.Ignore)) return;
        Vector3 n = wall.normal;
        if (Mathf.Abs(n.y) > 0.6f) return; // walls and steep surfaces only; floors have their own splats
        float s = Mathf.Clamp(scale, 0.8f, 1.25f);
        float life = _cfg.DecalLifetime.Value;
        float gap = wall.distance + 0.05f;
        float t0 = now + gap / 7f;
        // the cone of blood widens with the gap: ~0.4 m across up close, ~1 m at two metres
        float R = Mathf.Clamp(0.17f + 0.17f * gap, 0.18f, 0.5f) * Mathf.Clamp(Mathf.Sqrt(power), 0.8f, 1.2f) * s;
        Vector3 h = Vector3.Cross(Vector3.up, n);
        if (h.sqrMagnitude < 1e-4f) h = Vector3.Cross(Vector3.forward, n);
        h.Normalize();
        Vector3 v = Vector3.Cross(n, h);
        Vector3 slide = dir - n * Vector3.Dot(dir, n);   // oblique shots smear the pattern along the wall
        Vector3 centre = wall.point;
        float lightW = LightAt(centre, n);

        // the wide field: many small drops, denser toward the middle, radial tails, a little ragged
        int halo = Mathf.RoundToInt(_tier.Droplets * 3f * Mathf.Clamp(power, 0.7f, 1.4f) * _load);
        for (int i = 0; i < halo; i++)
        {
            float a = Random.value * 6.283f, d = Mathf.Pow(Random.value, 0.75f);
            Vector3 radial = h * (Mathf.Cos(a) * 1.25f) + v * (Mathf.Sin(a) * 0.95f);
            Vector3 off = radial * (R * d * Random.Range(0.85f, 1.1f)) + slide * (R * d * 0.6f);
            float rr = Mathf.Lerp(0.0015f, 0.009f, Mathf.Pow(Random.value, 3.5f)) * s;
            float el = 1f + d * Random.Range(0.3f, 1.6f);
            PlaceOnWall(centre + off, n, radial + slide * 0.5f, rr, el, lightW * Random.Range(0.75f, 1f), t0 + Random.value * 0.05f, life);
        }
        // the dense core arrives a moment later and keeps building up
        int core = Mathf.RoundToInt(_tier.Droplets * 2f * Mathf.Clamp(power, 0.7f, 1.4f) * _load);
        float rc = R * 0.38f;
        for (int i = 0; i < core; i++)
        {
            float a = Random.value * 6.283f, d = Mathf.Pow(Random.value, 0.8f);
            Vector3 radial = h * (Mathf.Cos(a) * 1.15f) + v * Mathf.Sin(a);
            Vector3 off = radial * (rc * d) + slide * (rc * d * 0.5f);
            float rr = Random.Range(0.005f, 0.016f) * s;
            PlaceOnWall(centre + off, n, radial, rr, Random.Range(1f, 1.4f), lightW * Random.Range(0.55f, 0.8f),
                t0 + 0.04f + Mathf.Pow(Random.value, 1.5f) * 0.22f, life);
        }
        int merged = Random.Range(3, 6);
        for (int i = 0; i < merged; i++)
        {
            Vector2 o = Random.insideUnitCircle * rc * 0.7f;
            Vector3 p = centre + h * o.x + v * o.y;
            if (!Physics.Raycast(p + n * 0.12f, -n, out RaycastHit hit, 0.3f, _mask, QueryTriggerInteraction.Ignore) || Vector3.Dot(hit.normal, n) < 0.6f) continue;
            _splats.Add(hit.point, hit.normal, Random.Range(0.018f, 0.04f) * s, Tint(PoolBase, lightW, 1f),
                t0 + 0.06f + Random.value * 0.2f, Random.Range(0.15f, 0.3f), life, 0.6f);
        }

        // drips: thick runs creeping straight down out of the core, one sometimes from the outer field
        Vector3 down = Vector3.down - n * Vector3.Dot(Vector3.down, n);
        if (down.sqrMagnitude > 0.05f)
        {
            down.Normalize();
            int runs = Random.Range(2, 4);
            for (int i = 0; i < runs; i++)
            {
                bool outer = i == runs - 1 && Random.value < 0.4f;
                Vector2 o = Random.insideUnitCircle * (outer ? R * 0.75f : rc * 0.6f);
                Vector3 p = centre + h * o.x + v * o.y;
                if (!Physics.Raycast(p + n * 0.12f, -n, out RaycastHit hit, 0.3f, _mask, QueryTriggerInteraction.Ignore) || Vector3.Dot(hit.normal, n) < 0.6f) continue;
                float len = Random.Range(0.12f, 0.35f) * (R / 0.35f) * (outer ? 0.5f : 1f);
                float w = Random.Range(0.007f, 0.012f) * s * (outer ? 0.7f : 1f);
                _trails.Add(hit.point, hit.normal, down, len, w, (Color)Tint(StreamBase, lightW * 0.7f, 1f),
                    t0 + Random.Range(0.4f, 1.2f), Random.Range(2.5f, 5f), life, 0.1f);
            }
        }

        // the blood crossing the gap: thin fast jets that flatten on the wall
        Color jet = Tint(StreamBase, light, 1f);
        int jets = Random.Range(5, 9);
        for (int i = 0; i < jets; i++)
        {
            Vector2 o = Random.insideUnitCircle * R * 0.8f;
            Vector3 aim = (centre + h * o.x + v * o.y) - pt;
            var sp = new BloodStreams.Spec
            {
                Origin = pt, Velocity = aim.normalized * Random.Range(6f, 9f),
                Emit = Random.Range(0.03f, 0.07f), Life = Random.Range(0.12f, 0.2f), Radius = Random.Range(0.004f, 0.008f) * s,
                Decay = 0.5f, Breakup = Random.Range(1.4f, 2f), Gravity = 1f, FloorY = floorY, Light = light, Col = jet,
                HasWall = true, WallP = wall.point, WallN = n
            };
            _streams.Spawn(ref sp, now);
        }
    }

    /// <summary>One drop of wall spatter, laid on the real surface (skipped where there is none).</summary>
    private void PlaceOnWall(Vector3 p, Vector3 n, Vector3 travel, float r, float el, float light, float due, float life)
    {
        if (!Physics.Raycast(p + n * 0.12f, -n, out RaycastHit hit, 0.3f, _mask, QueryTriggerInteraction.Ignore)) return;
        if (Vector3.Dot(hit.normal, n) < 0.5f) return;
        QueueStuck(hit.point, hit.normal, travel, r, el, (Color)Tint(StreamBase, light, 1f), due, life);
    }

    /// <summary>A gore chunk splatted: big lumps leave a small spatter of blood around them, small ones a single bead.</summary>
    private void OnChunkLanded(Vector3 p, Vector3 n, Vector3 vel, float r)
    {
        if (!_cfg.Enabled.Value) return;
        float light = LightAt(p, n);
        if (r > 0.01f)
            SpatterDrops(p, n, vel.sqrMagnitude > 1e-4f ? vel.normalized : -n, Time.time, r * 7f, 0.35f, light);
        else
        {
            Vector3 slide = vel - n * Vector3.Dot(vel, n);
            Color col = (Color)Tint(StreamBase, light * Random.Range(0.75f, 1f), 1f);
            _stuck.Add(p, n, slide, r * 1.6f, 1f + Mathf.Clamp(slide.magnitude * 0.3f, 0f, 1.5f), col, _cfg.DecalLifetime.Value, Time.time);
        }
    }

    private static bool HeadGone(Player victim)
    {
        try { return victim.HealthController != null && victim.HealthController.GetBodyPartHealth(EBodyPart.Head).Current <= 0f; }
        catch { return false; }
    }

    /// <summary>
    /// The head comes off: shards of it fly, a torn stump is left on the neck, and the neck keeps
    /// pumping blood for a few seconds and then drips. The usual headshot effects still play.
    /// </summary>
    private void Decapitate(Player victim, ref DamageInfo di, Vector3 dir, float power, float scale, float floorY, Vector3 floorN, float light,
        Color32 decalCol, float now)
    {
        bool ok;
        Vector3 cut, up;
        Transform neck;
        // most kills keep the entry side of the face; heavy rounds and the rest take the whole head
        bool half = di.Damage < _cfg.WholeHeadDamage.Value && Random.value < _cfg.HalfFaceChance.Value;
        try { ok = _heads.Dismember(victim, dir, ref half, light, _cfg.DecalLifetime.Value, now, out cut, out up, out neck); }
        catch (System.Exception e)
        {
            BloodFXPlugin.Log.LogWarning($"BloodFX: head dismember failed: {e.Message}");
            return;
        }
        if (!ok) return;
        _decapitations++;
        if (half) _halfHeads++;
        Vector3 mid = half ? cut : cut + up * 0.08f;
        if (_cfg.BrainMatter.Value) BrainBurst(mid, (dir + Vector3.up * 0.3f).normalized, power * (half ? 1.2f : 1.5f), scale, light, now);
        MistBurst(mid, dir, power * 2f, scale, floorY, light, now);
        if (!_cfg.Streams.Value) return;
        if (half)
        {
            // the open side of the head pours and drips; no neck jets, the neck is whole
            Spawn(Spurt.Pour, cut, up, power, scale, floorY, floorN, light, decalCol, now, neck);
            Spawn(Spurt.Gush, cut, up, power, scale, floorY, floorN, light, decalCol, now, neck);
            AddDrip(neck, cut, up, power, scale, light, now);
            return;
        }
        // the neck: two pumping jets out of the stump, a slower pour, then it drips
        Spawn(Spurt.Arterial, cut, up, power, scale, floorY, floorN, light, decalCol, now, neck);
        Spawn(Spurt.Arterial, cut, (up + Random.insideUnitSphere * 0.4f).normalized, power * 0.7f, scale * 0.8f, floorY, floorN, light, decalCol, now, neck);
        Spawn(Spurt.Pour, cut, dir, power, scale, floorY, floorN, light, decalCol, now, neck);
        AddDrip(neck, cut, up, power, scale, light, now);
    }

    private void LateUpdate()
    {
        if (_ready && _heads != null) _heads.LateTick();
    }

    private static Vector3 Jitter(Vector3 dir, float spread)
    {
        return (dir + Random.insideUnitSphere * spread).normalized;
    }

    /// <summary>
    /// The spray itself: 3D drops with power-law sizes (lots of small ones, a few heavy
    /// ones). Each lives exactly until it reaches the floor height, so none fall through.
    /// </summary>
    private void EmitSpray(Vector3 pos, Vector3 dir, int count, float spread, float vMin, float vMax, Color32 col, float scale, float floorY)
    {
        count = Mathf.RoundToInt(count * _load);
        for (int i = 0; i < count; i++)
        {
            Vector3 d = (dir + Random.insideUnitSphere * spread).normalized;
            Vector3 v = d * Random.Range(vMin, vMax);
            Vector3 p = pos + Random.insideUnitSphere * 0.03f;
            float land = BloodStreams.LandTime(p, v, 9.81f, floorY);
            _blobParams.position = p;
            _blobParams.velocity = v;
            _blobParams.startLifetime = land > 0f ? Mathf.Clamp(land + 0.15f, 0.1f, 1.4f) : 0.8f;
            float s = Mathf.Lerp(0.004f, 0.03f, Mathf.Pow(Random.value, 3f)) * scale;
            _blobParams.startSize3D = new Vector3(s, s * Random.Range(1f, 1.6f), s);
            _blobParams.startColor = col;
            _blobs.Emit(_blobParams, 1);
        }
    }

    private void QueueDecal(Vector3 pos, Vector3 normal, Vector3 along, float due, float size0, float size1, float grow,
        float aspect, float life, int cell, Color32 col)
    {
        ref Pending p = ref _pending[_pendingNext];
        _pendingNext = (_pendingNext + 1) % PendingSlots;
        p.Pos = pos; p.Normal = normal; p.Along = along; p.Due = due;
        p.Size0 = size0; p.Size1 = size1; p.Grow = grow; p.Aspect = aspect; p.Life = life;
        p.Cell = cell; p.Col = col; p.Active = true; p.Impact = false; p.NoDecal = false;
    }

    /// <summary>A splat that, when it lands, also bursts as a 3D splash across the wall.</summary>
    private void QueueImpact(Vector3 pos, Vector3 normal, Vector3 incoming, float due, float size, float power,
        float floorY, float light, Color32 col)
    {
        SpatterDrops(pos, normal, incoming, due, size, power, light);
        ref Pending p = ref _pending[_pendingNext];
        _pendingNext = (_pendingNext + 1) % PendingSlots;
        p.Pos = pos; p.Normal = normal; p.Due = due; p.Col = col;
        p.Active = true;
        p.NoDecal = true;
        p.Impact = true;
        p.ImpactPower = power;
        p.FloorY = floorY;
        p.Light = light;
        p.Incoming = incoming;
    }

    private int StuckCapacity() => _tier.Decals * 6;
    private int _landedThisFrame;

    /// <summary>A flying blob touched the world: it stays there as a bead, tail pointing the way it slid.</summary>
    internal void OnBlobLanded(Vector3 pos, Vector3 normal, Vector3 vel)
    {
        if (!_cfg.Enabled.Value || _landedThisFrame >= 48) return;
        _landedThisFrame++;
        float scale = Mathf.Clamp(Mathf.Sqrt(_cfg.Intensity.Value), 0.5f, 1.6f);
        Vector3 slide = vel - normal * Vector3.Dot(vel, normal);
        float r = Mathf.Lerp(0.0025f, 0.012f, Mathf.Pow(Random.value, 3f)) * scale;
        float el = 1f + Mathf.Clamp(slide.magnitude * 0.35f, 0f, 2f);
        Color col = (Color)Tint(StreamBase, LightAt(pos, normal) * Random.Range(0.75f, 1f), 1f);
        _stuck.Add(pos, normal, slide, r, el, col, _cfg.DecalLifetime.Value, Time.time);
    }
    private int PuddleCapacity() => _tier.Wounds * 2;
    private int SplatCapacity() => _tier.Wounds * 5;
    private int TrailCapacity() => _tier.Wounds * 2;
    private int BrainCapacity() => _tier.Wounds * 10;

    /// <summary>
    /// The stain left by spray hitting a surface, built from real drops: a few heavy
    /// beads at the centre, then many smaller ones spreading out (power-law sizes).
    /// The farther out a drop lands the longer its tail, pointing the way it flew,
    /// and oblique hits smear the whole pattern along the slide direction. Every drop
    /// is placed by its own short raycast, so blood only lands where there is surface.
    /// </summary>
    private void SpatterDrops(Vector3 pos, Vector3 n, Vector3 incoming, float due, float size, float power, float light)
    {
        Vector3 slide = incoming - n * Vector3.Dot(incoming, n);
        float slideMag = slide.magnitude;
        Vector3 sdir = slideMag > 1e-3f ? slide / slideMag : Vector3.zero;
        Vector3 t1 = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
        Vector3 t2 = Vector3.Cross(n, t1);
        float scale = Mathf.Clamp(Mathf.Sqrt(_cfg.Intensity.Value), 0.5f, 1.6f);
        float reach = size * 0.5f;
        int core = Random.Range(2, 5);
        int count = core + Mathf.RoundToInt(_tier.Droplets * 1.5f * Mathf.Clamp(power, 0.4f, 1.6f) * _load);
        float life = _cfg.DecalLifetime.Value;
        for (int i = 0; i < count; i++)
        {
            bool isCore = i < core;
            float a = Random.value * 6.283f;
            Vector3 radial = t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a);
            float dist = isCore ? reach * 0.12f * Random.value : reach * Mathf.Pow(Random.value, 0.6f);
            Vector3 off = radial * dist + sdir * (dist * slideMag * 0.8f);
            float rr = (isCore ? Random.Range(0.009f, 0.02f) : Mathf.Lerp(0.0015f, 0.01f, Mathf.Pow(Random.value, 4f))) * scale;
            float el = isCore ? Random.Range(1f, 1.3f)
                : 1f + Mathf.Clamp01(dist / reach) * Random.Range(0.5f, 2.2f) * (rr < 0.004f ? 0.5f : 1f);
            Vector3 travel = (dist > 1e-4f ? off / off.magnitude : radial) + sdir * 0.5f;
            if (!Physics.Raycast(pos + off + n * 0.12f, -n, out RaycastHit hit, 0.3f, _mask, QueryTriggerInteraction.Ignore)) continue;
            if (Vector3.Dot(hit.normal, n) < 0.4f) continue;
            Color col = (Color)Tint(StreamBase, light * Random.Range(0.75f, 1f), 1f);
            QueueStuck(hit.point, hit.normal, travel, rr, el, col, due + dist * 0.05f, life);
        }
    }

    /// <summary>
    /// Blood hitting a surface while still moving: a thick smear dragged out along its travel
    /// direction, with droplets flung off the sides and ahead of the head as it goes.
    /// </summary>
    private void Trail(Vector3 start, Vector3 n, Vector3 along, float length, float width, float due, float dur, float light)
    {
        Color col = (Color)Tint(StreamBase, light, 1f);
        _trails.Add(start, n, along, length, width, col, due, dur, _cfg.DecalLifetime.Value);
        Vector3 perp = Vector3.Cross(n, along).normalized;
        float life = _cfg.DecalLifetime.Value;
        int flung = Random.Range(4, 10);
        for (int k = 0; k < flung; k++)
        {
            float f = Mathf.Sqrt(Random.value);
            Vector3 q = start + along * (length * f * 1.05f) + perp * (Random.Range(-1f, 1f) * width * Random.Range(0.8f, 2.2f));
            if (!Physics.Raycast(q + n * 0.1f, -n, out RaycastHit hit, 0.25f, _mask, QueryTriggerInteraction.Ignore)) continue;
            Color dc = (Color)Tint(StreamBase, light * Random.Range(0.75f, 1f), 1f);
            QueueStuck(hit.point, hit.normal, along, width * Random.Range(0.08f, 0.22f), Random.Range(1.1f, 1.8f), dc,
                due + dur * (1f - (1f - f) * (1f - f)), life);
        }
    }

    /// <summary>
    /// A falling drop landing: a domed bead that spreads out in a blink. Drops from the same
    /// drip land close together, so their beads overlap and merge into one lumpy blob.
    /// Each impact flings a few tiny specks well out from it.
    /// </summary>
    private void DripSplash(Vector3 pos, Vector3 n, float radius, Color col, float due)
    {
        float life = _cfg.DecalLifetime.Value;
        Vector3 jitter = Random.insideUnitSphere * radius * 1.2f;
        _splats.Add(pos + (jitter - n * Vector3.Dot(jitter, n)), n, radius * Random.Range(1.8f, 2.5f), col, due,
            Random.Range(0.12f, 0.2f), life, 0.25f);
        Vector3 t1 = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
        Vector3 t2 = Vector3.Cross(n, t1);
        int crown = Random.Range(4, 10);
        for (int k = 0; k < crown; k++)
        {
            float a = Random.value * 6.283f;
            Vector3 radial = t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a);
            Vector3 q = pos + radial * radius * Random.Range(5f, 16f);
            if (!Physics.Raycast(q + n * 0.05f, -n, out RaycastHit hit, 0.12f, _mask, QueryTriggerInteraction.Ignore)) continue;
            QueueStuck(hit.point, hit.normal, radial, radius * Random.Range(0.1f, 0.25f), Random.Range(1.3f, 2.2f), col, due + 0.05f, life);
        }
    }

    private void QueueStuck(Vector3 pos, Vector3 normal, Vector3 along, float r, float elong, Color col, float due, float life)
    {
        ref PendingDrop p = ref _pendingDrops[_pendingDropNext];
        _pendingDropNext = (_pendingDropNext + 1) % PendingDropSlots;
        p.Pos = pos; p.Normal = normal; p.Along = along; p.R = r; p.Elong = elong; p.Col = col; p.Due = due;
        p.Active = true;
    }

    /// <summary>
    /// Blood arriving at a wall: drops burst outward along the surface and peel off it,
    /// short sheets skid across the wall (clamped to its plane), and a run or two crawls
    /// down. Matches the radial splat/spatter decals queued for the same moment.
    /// </summary>
    private void WallImpact(Vector3 pos, Vector3 normal, Vector3 incoming, float power, float floorY, float light, Color32 decalCol, float now)
    {
        if (!_cfg.Streams.Value) return;
        if (Viewer(out Vector3 eye, out _) && (pos - eye).sqrMagnitude > _cfg.MaxDistance.Value * _cfg.MaxDistance.Value) return;
        Vector3 t1 = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
        Vector3 t2 = Vector3.Cross(normal, t1);
        Vector3 slide = incoming - normal * Vector3.Dot(incoming, normal); // what the blood keeps of its travel along the wall
        float scale = Mathf.Clamp(Mathf.Sqrt(_cfg.Intensity.Value), 0.5f, 1.6f);
        Color32 liquid = Tint(StreamBase, light, 1f);
        Vector3 origin = pos + normal * 0.01f;

        int drops = Mathf.RoundToInt(_tier.Droplets * 0.8f * power);
        for (int i = 0; i < drops; i++)
        {
            float a = Random.value * 6.283f;
            Vector3 radial = t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a);
            Vector3 v = radial * Random.Range(0.8f, 3f) + normal * Random.Range(0.2f, 0.9f) + slide * 0.4f;
            float land = BloodStreams.LandTime(origin, v, 9.81f, floorY);
            _blobParams.position = origin;
            _blobParams.velocity = v;
            _blobParams.startLifetime = Mathf.Min(Random.Range(0.25f, 0.6f), land > 0f ? land : 1f);
            float sz = Mathf.Lerp(0.003f, 0.018f, Mathf.Pow(Random.value, 3f)) * scale;
            _blobParams.startSize3D = new Vector3(sz, sz * Random.Range(1f, 1.5f), sz);
            _blobParams.startColor = liquid;
            _blobs.Emit(_blobParams, 1);
        }

        int sheets = Random.Range(3, 6);
        for (int i = 0; i < sheets; i++)
        {
            float a = Random.value * 6.283f;
            Vector3 radial = t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a);
            var sp = new BloodStreams.Spec
            {
                Origin = origin, Velocity = radial * Random.Range(1.2f, 2.6f) + slide * 0.5f + normal * 0.05f,
                Emit = Random.Range(0.04f, 0.09f), Life = 0.22f, Radius = Random.Range(0.006f, 0.012f) * scale * Mathf.Sqrt(power),
                Decay = 0.6f, Breakup = 1.6f, Gravity = 1f, FloorY = floorY, Light = light, Col = Tint(StreamBase, light, 1f),
                HasWall = true, WallP = pos, WallN = normal
            };
            _streams.Spawn(ref sp, now);
        }

        int runs = power > 0.8f ? Random.Range(1, 3) : (Random.value < 0.5f ? 1 : 0);
        for (int i = 0; i < runs; i++)
        {
            Vector3 start = origin + (t1 * Random.Range(-0.05f, 0.05f) + t2 * Random.Range(-0.05f, 0.05f)) * scale;
            Vector3 down = Vector3.down - normal * Vector3.Dot(Vector3.down, normal);
            var sp = new BloodStreams.Spec
            {
                Origin = start, Velocity = down.normalized * Random.Range(0.15f, 0.35f),
                Emit = Random.Range(0.8f, 1.6f), Life = 1.2f, Radius = Random.Range(0.004f, 0.008f) * scale,
                Decay = 0.5f, Breakup = 0.2f, Gravity = 0.05f, FloorY = floorY, Light = light, Col = Tint(StreamBase, light, 1f),
                HasWall = true, WallP = pos, WallN = normal
            };
            _streams.Spawn(ref sp, now);
        }
    }

    /// <summary>A wound that keeps forming hanging teardrops which let go, fall and spot the floor.</summary>
    private bool AddDrip(Transform t, Vector3 worldPoint, Vector3 normal, float power, float scale, float light, float now)
    {
        int limit = Mathf.Min(_tier.Wounds, WoundSlots), slot = -1;
        for (int i = 0; i < limit; i++)
            if (_dripEnd[i] <= now) { slot = i; break; }
        if (slot < 0) return false;
        float dur = Mathf.Clamp(3f + 4f * power, 3f, 12f);
        _dripEnd[slot] = now + dur;
        Vector3 origin = worldPoint + normal * 0.012f;
        var s = new BloodStreams.Spec
        {
            Drip = true, Origin = origin, Velocity = Vector3.down, Emit = dur,
            Radius = Random.Range(0.006f, 0.01f) * scale, Life = Random.Range(0.05f, 0.11f) * scale,
            PulseFreq = Random.Range(0.7f, 1.3f), FloorY = -10000f, Light = light, Col = Tint(StreamBase, light, 1f),
            Anchor = t, AnchorLocal = t.InverseTransformPoint(origin)
        };
        _streams.Spawn(ref s, now);
        return true;
    }

    // ---------------------------------------------------------------- bleeding

    private struct Wound
    {
        public Transform T;
        public Vector3 Local, LocalN;
    }

    private readonly Dictionary<int, Wound> _wounds = new Dictionary<int, Wound>();
    private readonly Dictionary<int, float> _bleedDripUntil = new Dictionary<int, float>();

    internal bool ReplacesBleeding => _ready && _cfg.Enabled.Value && _cfg.BleedDrips.Value;

    /// <summary>The latest hole in each character, so their bleeding drips from where they were hit.</summary>
    private void RememberWound(Player victim, Transform t, Vector3 pt, Vector3 n)
    {
        if (_wounds.Count > 512) _wounds.Clear();
        _wounds[victim.GetInstanceID()] = new Wound { T = t, Local = t.InverseTransformPoint(pt), LocalN = t.InverseTransformDirection(n.normalized) };
    }

    /// <summary>
    /// EFT's bleeding tick (every 1-3 s per bleeding character): instead of a flat stain under them, a
    /// teardrop keeps forming at the wound, lets go, falls and splashes, so a moving character leaves
    /// a trail of real drops. Without a known wound the drops fall from the hips.
    /// </summary>
    internal void OnBleedTick(Player p)
    {
        if (p == null || !_ready) return;
        float now = Time.time;
        int id = p.GetInstanceID();
        Transform t = null;
        Vector3 pt, n;
        if (_wounds.TryGetValue(id, out Wound w) && w.T != null && w.T.gameObject.activeInHierarchy)
        {
            t = w.T;
            pt = t.TransformPoint(w.Local);
            n = t.TransformDirection(w.LocalN);
        }
        else
        {
            Transform pelvis = p.PlayerBones?.Pelvis?.Original != null ? ((Component)p.PlayerBones.Pelvis.Original).transform : p.Transform.Original;
            pt = pelvis.position + Random.insideUnitSphere * 0.08f;
            n = Vector3.down;
        }
        if (Viewer(out Vector3 eye, out _) && (pt - eye).sqrMagnitude > _cfg.MaxDistance.Value * _cfg.MaxDistance.Value) return;
        float light = LightAt(pt, n);
        float scale = Mathf.Clamp(Mathf.Sqrt(_cfg.Intensity.Value), 0.5f, 1.6f);
        // your own wounds would drip in front of the camera; you get only the drops landing
        bool ownView = p.IsYourPlayer && !_cfg.PlayerHits.Value;
        if (!ownView && t != null && _cfg.Streams.Value && (!_bleedDripUntil.TryGetValue(id, out float until) || now >= until))
        {
            if (AddDrip(t, pt, n, 0.5f, scale, light, now))
            {
                if (_bleedDripUntil.Count > 512) _bleedDripUntil.Clear();
                _bleedDripUntil[id] = now + 4f; // renewed just before it runs dry, so the dripping doesn't stop
                return;
            }
        }
        if (ownView && t != null) pt = p.Transform.position + Vector3.up * 0.9f + Random.insideUnitSphere * 0.1f;
        FallingDrop(pt, light, now);
    }

    /// <summary>One drop let go at <paramref name="pos"/>: falls as a blob and splashes where it lands.</summary>
    private void FallingDrop(Vector3 pos, float light, float now)
    {
        if (!Physics.Raycast(pos, Vector3.down, out RaycastHit hit, 3f, _mask, QueryTriggerInteraction.Ignore)) return;
        float r = Random.Range(0.0035f, 0.006f) * Mathf.Clamp(Mathf.Sqrt(_cfg.Intensity.Value), 0.5f, 1.6f);
        float fall = Mathf.Sqrt(2f * hit.distance / 9.81f);
        Color col = (Color)Tint(StreamBase, light, 1f);
        DripSplash(hit.point, hit.normal, r, col, now + fall);
        _blobParams.position = pos;
        _blobParams.velocity = new Vector3(0f, -0.25f, 0f);
        _blobParams.startLifetime = Mathf.Max(0.05f, fall);
        _blobParams.startSize3D = new Vector3(r * 2f, r * 2.6f, r * 2f);
        _blobParams.startColor = col;
        _blobs.Emit(_blobParams, 1);
    }

    private int BodyCapacity() => Mathf.Max(32, _tier.Decals / 3);

    /// <summary>Marks face between the collider surface and the shooter, so they read from where you shot.</summary>
    private static Vector3 BodyFacing(ref DamageInfo di, Vector3 dir)
    {
        Vector3 n = di.HitNormal.sqrMagnitude > 0.01f ? di.HitNormal.normalized : -dir;
        Vector3 face = n - dir;
        return face.sqrMagnitude > 1e-4f ? face.normalized : -dir;
    }

    /// <summary>Armor stopped the round: a grey scuff on the plate or helmet, no blood.</summary>
    private void ArmorMark(Player victim, ref DamageInfo di)
    {
        Vector3 pt = di.HitPoint;
        if (Viewer(out Vector3 eye, out _) && (pt - eye).sqrMagnitude > _cfg.MaxDistance.Value * _cfg.MaxDistance.Value) return;
        Vector3 dir = di.Direction.sqrMagnitude > 1e-4f ? di.Direction.normalized : Vector3.forward;
        Vector3 face = BodyFacing(ref di, dir);
        float light = LightAt(pt, face);
        float size = Random.Range(0.06f, 0.09f);
        int cell = BloodTextures.ArmorCell0 + Random.Range(0, BloodTextures.ArmorCells);
        if (_cfg.ConformMarks.Value && _marks.TryAdd(victim, di.HitCollider.transform, pt, dir, size, cell, Tint(ArmorBase, light, 1f),
                _cfg.DecalLifetime.Value * 2f, 0f, 0f, 0, Time.time))
            return;
        _body.Add(di.HitCollider.transform, pt + face * _cfg.BodyDecalOffset.Value, face, size, cell, BodyDecals.ProfileArmor, 0.05f, Tint(ArmorBase, light, 1f),
            _cfg.DecalLifetime.Value * 2f, false, 0f, 1f, 0, Time.time);
    }

    private void QueuePool(Player victim)
    {
        if (_pooledBodies.Count > 1024) _pooledBodies.Clear();
        if (!_pooledBodies.Add(victim.GetInstanceID())) return;
        Transform pelvis = victim.PlayerBones != null && victim.PlayerBones.Pelvis != null ? victim.PlayerBones.Pelvis.Original : null;
        if (pelvis == null) return;
        for (int i = 0; i < PoolSlots; i++)
        {
            if (_pools[i].Active) continue;
            _pools[i].T = pelvis;
            _pools[i].Due = Time.time + 2.5f;
            _pools[i].Size = Random.Range(0.75f, 1.2f) * Mathf.Clamp(Mathf.Sqrt(_cfg.Intensity.Value), 0.6f, 1.5f);
            _pools[i].Active = true;
            return;
        }
    }

    // ---------------------------------------------------------------- update

    private void Update()
    {
        TrackLoad();
        if (!_ready) return;
        float now = Time.time;
        if (now >= _worldCheck)
        {
            _worldCheck = now + 1f;
            object world = Singleton<GameWorld>.Instance;
            if (!ReferenceEquals(world, _world)) { _world = world; ClearAll(); }
        }
        if (_cfg.DebugLog.Value && now >= _statsAt)
        {
            _statsAt = now + 10f;
            if (_seen != _lastSeen)
            {
                _lastSeen = _seen;
                BloodFXPlugin.Log.LogInfo($"BloodFX: hits seen {_seen}, spawned {_spawned}; skipped player {_skipPlayer}, armor/zero {_skipArmor}, " +
                    $"type {_skipType}, far {_skipFar}, budget {_skipBudget}; conforming marks {_marks.Built} (skinned {_marks.Skinned}, rigid {_marks.Rigid}, missed {_marks.Missed}); live streams {_streams.LiveCount}, blobs {_blobs.particleCount}, " +
                    $"decals {_decals.LiveCount}, body marks {_body.LiveCount}, stuck drops {_stuck.LiveCount}, pools {_puddles.LiveCount}, " +
                    $"splats {_splats.LiveCount}, trails {_trails.LiveCount}, brain chunks {_brains.LiveCount}, " +
                    $"load {_load:F2}, heads off {_decapitations} (half face {_halfHeads}; on bodies {_heads.CutCount}, shards {_heads.ShardCount})");
            }
        }
        if (!_cfg.Enabled.Value) return;
        // a fault in one effect must not stop the others (or the hits) without a word in the log
        try { TickEffects(now); }
        catch (System.Exception e)
        {
            if (_tickErrors++ < 3) BloodFXPlugin.Log.LogError($"BloodFX: update error: {e}");
        }
    }

    private int _tickErrors;

    /// <summary>
    /// Watches frame time. When frames run slower than this player's usual (or than TargetFps, if set),
    /// spawn counts scale down at once, as low as 30%, and recover over a couple of seconds once it's smooth again.
    /// </summary>
    private void TrackLoad()
    {
        float dt = Time.unscaledDeltaTime;
        if (dt <= 0f || dt > 0.5f) return; // loading screens and pauses tell us nothing
        _fastDt = _fastDt <= 0f ? dt : Mathf.Lerp(_fastDt, dt, 0.15f);
        _slowDt = _slowDt <= 0f ? dt : Mathf.Lerp(_slowDt, dt, 0.003f);
        float budget = _cfg.TargetFps.Value > 0 ? 1f / _cfg.TargetFps.Value : _slowDt * 1.2f;
        float want = Mathf.Clamp(budget / Mathf.Max(1e-4f, _fastDt), 0.3f, 1f);
        _load = want < _load ? want : Mathf.MoveTowards(_load, want, 0.4f * dt);
    }

    private void TickEffects(float now)
    {
        _landedThisFrame = 0;

        for (int i = 0; i < PendingSlots; i++)
        {
            ref Pending p = ref _pending[i];
            if (!p.Active || now < p.Due) continue;
            p.Active = false;
            if (!p.NoDecal) _decals.Add(p.Pos, p.Normal, p.Along, p.Size0, p.Size1, p.Grow, p.Aspect, p.Life, p.Cell, p.Col, now);
            if (p.Impact)
            {
                p.Impact = false;
                WallImpact(p.Pos, p.Normal, p.Incoming, p.ImpactPower, p.FloorY, p.Light, p.Col, now);
            }
        }

        bool haveEye = Viewer(out Vector3 eye, out _);

        for (int i = 0; i < PoolSlots; i++)
        {
            ref PoolJob j = ref _pools[i];
            if (!j.Active || now < j.Due) continue;
            j.Active = false;
            if (j.T == null) continue;
            Vector3 from = j.T.position + Vector3.up * 0.3f;
            if (!Physics.Raycast(from, Vector3.down, out RaycastHit hit, 2f, _mask, QueryTriggerInteraction.Ignore)) continue;
            Color32 col = Tint(PoolBase, LightAt(hit.point, hit.normal), 1f);
            _puddles.Add(hit.point, hit.normal, j.Size * 0.5f, col, now, Random.Range(25f, 45f), _cfg.DecalLifetime.Value * 2f);
        }

        _streams.Tick(now, haveEye ? eye : Vector3.zero);
        _brains.Tick(now, Time.deltaTime, haveEye ? eye : Vector3.zero);
        _heads.Tick(now, Time.deltaTime);
        for (int i = 0; i < _streams.DetachCount; i++)
        {
            BloodStreams.Detach d = _streams.GetDetach(i);
            float fall = 1.2f;
            if (Physics.Raycast(d.Pos, Vector3.down, out RaycastHit hit, 3f, _mask, QueryTriggerInteraction.Ignore))
            {
                fall = Mathf.Sqrt(2f * hit.distance / 9.81f);
                DripSplash(hit.point, hit.normal, d.Radius, d.Col, now + fall);
            }
            _blobParams.position = d.Pos;
            _blobParams.velocity = new Vector3(0f, -0.25f, 0f);
            _blobParams.startLifetime = Mathf.Max(0.05f, fall);
            _blobParams.startSize3D = new Vector3(d.Radius * 2f, d.Radius * 2.6f, d.Radius * 2f);
            _blobParams.startColor = d.Col;
            _blobs.Emit(_blobParams, 1);
        }
        _streams.ClearDetach();
        _decals.Tick(now);
        for (int i = 0; i < PendingDropSlots; i++)
        {
            ref PendingDrop d = ref _pendingDrops[i];
            if (!d.Active || now < d.Due) continue;
            d.Active = false;
            _stuck.Add(d.Pos, d.Normal, d.Along, d.R, d.Elong, d.Col, _cfg.DecalLifetime.Value, now);
        }
        _stuck.Tick(now);
        _puddles.Tick(now, eye, haveEye);
        _splats.Tick(now, eye, haveEye);
        _trails.Tick(now, eye, haveEye);
        _marks.Tick(now);
        _body.Tick(now, haveEye ? eye : Vector3.zero, haveEye ? _cfg.MaxDistance.Value * _cfg.MaxDistance.Value : float.MaxValue);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Where you are looking from: your player's camera bone, else the main camera.</summary>
    private static bool Viewer(out Vector3 eye, out Vector3 look)
    {
        GameWorld world = Singleton<GameWorld>.Instance;
        Player me = world != null ? world.MainPlayer : null;
        Transform cam = me != null ? me.CameraPosition : null;
        if (cam == null)
        {
            Camera c = Camera.main;
            cam = c != null ? c.transform : null;
        }
        if (cam == null)
        {
            eye = look = Vector3.zero;
            return false;
        }
        eye = cam.position;
        look = me != null ? me.LookDirection : cam.forward;
        return true;
    }

    /// <summary>Scene brightness at a point from baked probes and ambient, for unlit-but-plausible blood.</summary>
    private float LightAt(Vector3 pos, Vector3 normal)
    {
        _shDir[0] = normal;
        LightProbes.GetInterpolatedProbe(pos, null, out SphericalHarmonicsL2 probe);
        probe.Evaluate(_shDir, _shOut);
        float l = Luma(_shOut[0]);
        RenderSettings.ambientProbe.Evaluate(_shDir, _shOut);
        l = Mathf.Max(l, Luma(_shOut[0]));
        return Mathf.Clamp(l * 2f * _cfg.Brightness.Value, 0.35f, 1.4f);
    }

    private static float Luma(Color c) => c.r * 0.3f + c.g * 0.59f + c.b * 0.11f;

    private static Color32 Tint(Color baseCol, float light, float alpha)
    {
        return new Color(baseCol.r * light, baseCol.g * light, baseCol.b * light, baseCol.a * alpha);
    }
}
