using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using UnityEngine;

namespace BloodFX;

/// <summary>
/// Dead bodies: dead weight, bullets that still move them, and dragging.
///
/// EFT runs a corpse's ragdoll for a few seconds after death, then freezes it
/// (kinematic) and, once it is off screen, removes its rigidbodies. After that
/// only the killing shot ever moved it. Here a frozen corpse is woken again
/// (the ragdoll is simply started a second time) whenever a bullet hits it or
/// the player grabs it, and kept awake while it is being moved.
/// </summary>
internal static class CorpseSystem
{
    // ==================== Dead weight and shots ====================

    // A real round carries roughly 3-10 N·s of momentum; this is the push of a
    // full-power hit at "Bullet push on corpses" = 1.
    private const float ShotPushNs = 10f;
    // EFT's killing-shot push is 45-150 N·s, enough to fling a 6 kg limb at 25 m/s.
    private const float DeathPushShare = 0.15f;
    private const float MaxGearKg = 40f;
    // How long after the last touch a corpse may only freeze once it has come to rest.
    private const float SettleSeconds = 4f;

    private sealed class RagState
    {
        public float LastTouched;
        public bool Dragged;
    }

    private static readonly Dictionary<CorpseRagdoll, RagState> _rags = new();
    private static readonly HashSet<Rigidbody> _weighted = new();

    /// <summary>Called after every CorpseRagdoll.Start: the first at death, and each re-wake.</summary>
    public static void OnRagdollStarted(CorpseRagdoll rag)
    {
        Hook(rag);
        if (BloodFXPlugin.Instance.DeadWeightOn.Value)
            AddGearWeight(rag);
    }

    /// <summary>Scale for EFT's own corpse impulse (the killing shot).</summary>
    public static float DeathPushScale()
    {
        if (!BloodFXPlugin.Instance.DeadWeightOn.Value) return 1f;
        return DeathPushShare * BloodFXPlugin.Instance.CorpsePushScale.Value;
    }

    /// <summary>A bullet hit a body that was already dead.</summary>
    public static void OnCorpseShot(Corpse corpse, Collider collider, Vector3 direction, Vector3 point, float damage)
    {
        if (!BloodFXPlugin.Instance.CorpseShotsOn.Value || corpse == null || collider == null) return;
        if (!Wake(corpse)) return;

        var spawner = collider.GetComponentInParent<RigidbodySpawner>();
        Rigidbody rb = spawner != null ? spawner.Rigidbody : null;
        if (rb == null || rb.isKinematic) return;

        float thrust = ShotPushNs * Mathf.Clamp(damage / 60f, 0.5f, 1.5f) * BloodFXPlugin.Instance.CorpsePushScale.Value;
        if (!BloodFXPlugin.Instance.DeadWeightOn.Value) thrust *= 3f; // no dead weight: the old, livelier push
        rb.AddForceAtPosition(direction.normalized * thrust, point, ForceMode.Impulse);
    }

    /// <summary>
    /// Makes a corpse's ragdoll physical again. False while something else holds
    /// it kinematic (EuphoriaReaction holds bodies through its dying sequence).
    /// </summary>
    public static bool Wake(Corpse corpse)
    {
        CorpseRagdoll rag = corpse.Ragdoll;
        if (rag == null || rag._rigidbodySpawners == null || rag._rigidbodySpawners.Length == 0) return false;

        Hook(rag);
        _rags[rag].LastTouched = Time.time;

        if (!rag._isPhysicsDone)
        {
            Rigidbody first = rag._rigidbodySpawners[0].Rigidbody;
            if (first == null || first.isKinematic) return false;
            rag.WakeUp();
            return true;
        }

        foreach (var part in rag._rigidbodySleepHierarchy)
            part.Reset();
        rag._velocity = Vector3.zero;
        rag._hasImpulse = false;
        rag.Start();
        return true;
    }

    // Wraps EFT's two callbacks once per ragdoll: the "is it still?" check, so a
    // corpse being moved isn't frozen mid-air by EFT's 15 s limit, and the
    // visibility check, so EFT doesn't remove its rigidbodies while it moves.
    private static void Hook(CorpseRagdoll rag)
    {
        if (_rags.ContainsKey(rag)) return;
        var state = new RagState { LastTouched = -100f };
        _rags[rag] = state;

        Func<bool, float, bool> still = rag._checkCorpseIsStill;
        rag._checkCorpseIsStill = (sleeping, timePass) =>
        {
            if (state.Dragged) return false;
            if (Time.time - state.LastTouched < SettleSeconds) return sleeping && Time.time - state.LastTouched > 1f;
            return still == null || still(sleeping, timePass);
        };

        Func<bool> visible = rag._isVisibleTest;
        rag._isVisibleTest = () => state.Dragged || !rag._isPhysicsDone || (visible != null && visible());
    }

    private static void AddGearWeight(CorpseRagdoll rag)
    {
        var corpse = rag._owner as Corpse;
        float gear;
        try { gear = corpse?.Item != null ? Mathf.Min(corpse.Item.TotalWeight, MaxGearKg) : 0f; }
        catch { return; }
        if (gear <= 0f) return;

        // Armor, rig and backpack sit on the torso: share the weight across the
        // spine and pelvis bodies in proportion to their own mass.
        var torso = new List<Rigidbody>();
        float torsoMass = 0f;
        foreach (RigidbodySpawner s in rag._rigidbodySpawners)
        {
            Rigidbody rb = s.Rigidbody;
            if (rb == null || _weighted.Contains(rb)) continue;
            if (!s.name.Contains("Spine") && !s.name.Contains("Pelvis")) continue;
            torso.Add(rb);
            torsoMass += rb.mass;
        }
        if (torsoMass <= 0f) return;

        foreach (Rigidbody rb in torso)
        {
            rb.mass += gear * rb.mass / torsoMass;
            _weighted.Add(rb);
        }
    }

    // ==================== Dragging ====================

    // A body is dragged, not carried: the grip rides just above the ground under your hand,
    // the pull is mostly along the floor, and the part you hold can only be lifted a little
    // (an arm's worth), so the rest of the body stays down and slides on it.
    private const float GrabReach = 3f;
    private const float GripLift = 0.16f;     // m the held part may be lifted off the floor under it
    private const float LiftShare = 1.5f;     // upward pull at most this many times the held part's own weight
    private const float PullStiffness = 900f; // N per m of stretch
    private const float PullDamping = 120f;  // N per m/s
    private const float MaxPull = 1100f;     // N, about what a person can pull with one arm and their back
    private const float BreakDistance = 2.5f;
    private const float SlideFriction = 0.2f; // extra rub of cloth and gear on the floor, on top of the physics material
    // The held part alone is only pushed this hard (m/s^2); the rest of the pull is spread over the body as if it came
    // through the joints, which PhysX can't carry stiffly: a limb yanked on its own pulls away from its neighbours
    // and the skin between the bones stretches.
    private const float HeldAccel = 12f;
    private const int DragSolverIterations = 24, DragSolverVelocityIterations = 8;
    private const float DragProjection = 0.02f; // m a joint may come apart before it is snapped back together
    private const float Gravity = 9.81f;

    public const int MaxParts = 16;

    /// <summary>A part of the dragged body that is on the ground this physics step.</summary>
    public struct Contact
    {
        public int Index;          // which part (see Part)
        public Rigidbody Body;
        public Vector3 Point, Normal, Velocity;
        public float Press;        // how hard it bears on the floor, about 1 for a limb, more for the torso
        public float Width;        // how wide it is across its travel (metres)
    }

    public static readonly Contact[] Contacts = new Contact[MaxParts];
    public static int ContactCount;
    /// <summary>Mass-weighted speed of the parts sliding on the ground (m/s).</summary>
    public static float SlideSpeed;

    private enum Source { None, Interact, Hotkey }

    private static Corpse _pendingCorpse;
    private static float _pendingSince;
    private static Action _pendingAction;

    private static Corpse _dragCorpse;
    private static Rigidbody _dragBody;
    private static Vector3 _grabLocal;
    private static float _holdDistance;
    private static float _stretchedSince = -1f;
    private static Source _dragSource;
    private static float _speedBefore;
    private static Vector3 _lastDir;
    private static readonly Rigidbody[] _parts = new Rigidbody[MaxParts];
    private static readonly Collider[] _partCols = new Collider[MaxParts];
    private static readonly RaycastHit[] _grabHits = new RaycastHit[16];
    private static readonly Joint[] _partJoints = new Joint[MaxParts];
    private static readonly int[] _solverIter = new int[MaxParts], _solverVelIter = new int[MaxParts];
    private static readonly float[] _projection = new float[MaxParts], _towWeight = new float[MaxParts];
    private static float _jointMax;
    private static int _partCount;
    private static int _groundMask = -1;
    // what the drag did, for the log when it ends
    private static float _dragStart, _groundedSum, _gripMax, _topSpeed;
    private static int _steps;

    private static int _lastCommandFrame = -1;
    private static bool _lastCommandBegin;
    private static bool _lastCommandConsumed;

    private static GameWorld _world;

    public static bool Dragging => _dragBody != null;

    // For effects that follow a dragged body (drag blood, the drag sound).
    public static Corpse DraggedCorpse => _dragCorpse;
    public static Rigidbody DraggedBody => _dragBody;
    public static int PartCount => _partCount;
    public static Rigidbody Part(int i) => i >= 0 && i < _partCount ? _parts[i] : null;

    private static int GroundMask
    {
        get
        {
            if (_groundMask < 0)
            {
                try { _groundMask = LayersMaskController.HighPolyWithTerrainNoGrassMask; }
                catch { _groundMask = ~0; }
            }
            return _groundMask;
        }
    }

    /// <summary>
    /// The interact key went down (begin) or up (end). Returns true when the
    /// command is consumed and the game shouldn't act on it.
    /// </summary>
    public static bool OnInteractCommand(bool begin, Action selectedAction)
    {
        // Several input nodes see the same key; handle each press/release once.
        if (_lastCommandFrame == Time.frameCount && _lastCommandBegin == begin) return _lastCommandConsumed;
        _lastCommandFrame = Time.frameCount;
        _lastCommandBegin = begin;
        _lastCommandConsumed = HandleInteract(begin, selectedAction);
        return _lastCommandConsumed;
    }

    private static bool HandleInteract(bool begin, Action selectedAction)
    {
        if (!begin)
        {
            if (_pendingCorpse != null)
            {
                // A tap: do what the key would have done on press.
                Action tap = _pendingAction;
                ClearPending();
                tap?.Invoke();
                return true;
            }
            if (Dragging && _dragSource == Source.Interact)
            {
                StopDrag();
                return true;
            }
            return false;
        }

        if (Dragging)
        {
            // In case the key works as a toggle in the game's settings.
            if (_dragSource == Source.Interact) StopDrag();
            return true;
        }

        if (!BloodFXPlugin.Instance.CorpseDragOn.Value || !BloodFXPlugin.Instance.HoldInteractToDrag.Value) return false;
        Corpse corpse = LookedAtCorpse();
        if (corpse == null || selectedAction == null) return false;

        _pendingCorpse = corpse;
        _pendingSince = Time.time;
        _pendingAction = selectedAction;
        return true;
    }

    /// <summary>Every frame from the plugin's Update.</summary>
    public static void Tick()
    {
        GameWorld world = Singleton<GameWorld>.Instance;
        if (world != _world)
        {
            Reset(); // new raid, or back in the menu
            _world = world;
        }

        Player player = MainPlayer();
        if (player == null || !player.HealthController.IsAlive)
        {
            ClearPending();
            if (Dragging) StopDrag();
            DragSound.Tick(null, 0f, Time.deltaTime);
            return;
        }

        if (_pendingCorpse != null && Time.time - _pendingSince >= BloodFXPlugin.Instance.DragHoldSeconds.Value)
        {
            Corpse corpse = _pendingCorpse;
            ClearPending();
            if (LookedAtCorpse() == corpse) StartDrag(player, corpse, Source.Interact);
        }

        var hotkey = BloodFXPlugin.Instance.DragHotkey.Value;
        if (hotkey.MainKey != KeyCode.None && BloodFXPlugin.Instance.CorpseDragOn.Value)
        {
            if (!Dragging && hotkey.IsDown())
            {
                Corpse corpse = LookedAtCorpse();
                if (corpse != null) StartDrag(player, corpse, Source.Hotkey);
            }
            else if (Dragging && _dragSource == Source.Hotkey && !hotkey.IsPressed())
            {
                StopDrag();
            }
        }

        if (!Dragging)
        {
            DragSound.Tick(null, 0f, Time.deltaTime);
            return;
        }
        if (!BloodFXPlugin.Instance.CorpseDragOn.Value || _dragCorpse == null)
        {
            StopDrag();
            DragSound.Tick(null, 0f, Time.deltaTime);
            return;
        }

        // Pulling a body is slow work: no sprinting, and walking pace at most.
        MovementContext mc = player.MovementContext;
        float cap = BloodFXPlugin.Instance.DragMoveSpeed.Value;
        if (mc.CharacterMovementSpeed > cap) mc.SetCharacterMovementSpeed(cap);
        if (mc.IsSprintEnabled) mc.EnableSprint(false);

        Transform anchor = _dragCorpse.TrackableTransform != null ? _dragCorpse.TrackableTransform : _dragBody.transform;
        DragSound.Tick(anchor, SlideSpeed, Time.deltaTime);
    }

    /// <summary>Every physics step from the plugin's FixedUpdate.</summary>
    public static void FixedTick()
    {
        if (!Dragging) return;
        Player player = MainPlayer();
        Camera cam = Camera.main;
        if (player == null || cam == null || _dragBody == null || _dragBody.isKinematic)
        {
            StopDrag();
            return;
        }

        Vector3 feet = player.Transform.Original.position;
        Vector3 forward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.01f) forward = player.Transform.Original.forward;
        forward.Normalize();
        Vector3 target = feet + forward * _holdDistance;
        // the hand is low: the grip rides just above whatever floor is under it
        float groundY = feet.y;
        if (Physics.Raycast(target + Vector3.up * 1f, Vector3.down, out RaycastHit ground, 2.5f, GroundMask, QueryTriggerInteraction.Ignore))
            groundY = ground.point.y;
        target.y = groundY + GripLift;

        Vector3 grip = _dragBody.transform.TransformPoint(_grabLocal);
        Vector3 stretch = target - grip;

        if (stretch.magnitude > BreakDistance)
        {
            // Snagged on something: let go rather than tear the body through the wall.
            if (_stretchedSince < 0f) _stretchedSince = Time.time;
            else if (Time.time - _stretchedSince > 0.5f) { StopDrag(); return; }
        }
        else _stretchedSince = -1f;

        // EFT keeps putting resting parts back to sleep every frame; a sleeping bone stays put while its
        // neighbour is pulled, and the skin between them stretches. Keep the whole body awake.
        CorpseRagdoll rag = _dragCorpse.Ragdoll;
        if (rag != null && !rag._isPhysicsDone) rag.WakeUp();

        Vector3 relVel = _dragBody.GetPointVelocity(grip) - player.Velocity;
        Vector3 pull = stretch * PullStiffness - relVel * PullDamping;
        // along the floor as hard as you can; up only enough to lift the held part a little
        Vector3 flat = Vector3.ClampMagnitude(Vector3.ProjectOnPlane(pull, Vector3.up), MaxPull);
        float liftCap = _dragBody.mass * Gravity * LiftShare;
        float up = Mathf.Clamp(pull.y, -liftCap, liftCap);
        Vector3 held = Vector3.ClampMagnitude(flat, _dragBody.mass * HeldAccel);
        _dragBody.AddForceAtPosition(held + Vector3.up * up, grip, ForceMode.Force);
        Tow(flat - held, grip);
        MeasureJoints();

        GroundContacts(Time.fixedDeltaTime, forward);

        _steps++;
        _groundedSum += ContactCount;
        if (SlideSpeed > _topSpeed) _topSpeed = SlideSpeed;
        if (Physics.Raycast(grip + Vector3.up * 0.3f, Vector3.down, out RaycastHit under, 3f, GroundMask, QueryTriggerInteraction.Ignore))
            _gripMax = Mathf.Max(_gripMax, grip.y - under.point.y);
    }

    /// <summary>
    /// The pull the held part can't take on its own goes to the rest of the body, as if it came
    /// through the joints: spread by mass, the parts nearest the grip taking the most.
    /// </summary>
    private static void Tow(Vector3 force, Vector3 grip)
    {
        if (force.sqrMagnitude < 1e-4f) return;
        float sum = 0f;
        for (int i = 0; i < _partCount; i++)
        {
            Rigidbody rb = _parts[i];
            if (rb == null || rb == _dragBody || rb.isKinematic) { _towWeight[i] = 0f; continue; }
            float d = (rb.worldCenterOfMass - grip).magnitude;
            _towWeight[i] = rb.mass / (1f + d / 0.35f);
            sum += _towWeight[i];
        }
        if (sum <= 0f) { _dragBody.AddForce(force, ForceMode.Force); return; }
        for (int i = 0; i < _partCount; i++)
            if (_towWeight[i] > 0f) _parts[i].AddForce(force * (_towWeight[i] / sum), ForceMode.Force);
    }

    /// <summary>How far the joints have come apart, for the log: that is what shows as stretched skin.</summary>
    private static void MeasureJoints()
    {
        for (int i = 0; i < _partCount; i++)
        {
            Joint j = _partJoints[i];
            if (j == null || j.connectedBody == null) continue;
            Vector3 a = j.transform.TransformPoint(j.anchor);
            Vector3 b = j.connectedBody.transform.TransformPoint(j.connectedAnchor);
            float sep = (a - b).magnitude;
            if (sep > _jointMax) _jointMax = sep;
        }
    }

    /// <summary>
    /// Finds the parts of the body that are on the ground, rubs them against it, and
    /// publishes where they touch for the blood smear and the drag sound.
    /// </summary>
    private static void GroundContacts(float dt, Vector3 fallback)
    {
        int n = 0;
        float massSum = 0f, speedSum = 0f;
        for (int i = 0; i < _partCount && n < MaxParts; i++)
        {
            Rigidbody rb = _parts[i];
            Collider col = _partCols[i];
            if (rb == null || col == null || rb.isKinematic) continue;
            Vector3 com = rb.worldCenterOfMass;
            float half = Mathf.Max(0.02f, com.y - col.bounds.min.y);
            if (!Physics.Raycast(com, Vector3.down, out RaycastHit hit, half + 0.05f, GroundMask, QueryTriggerInteraction.Ignore)) continue;

            Vector3 vel = rb.velocity;
            Vector3 slide = Vector3.ProjectOnPlane(vel, hit.normal);
            float speed = slide.magnitude;
            if (speed > 0.01f)
            {
                // cloth and gear rubbing on the floor: a steady push against the slide,
                // never more than would stop the part in this step
                float f = Mathf.Min(SlideFriction * rb.mass * Gravity, rb.mass * speed / dt);
                rb.AddForce(slide * (-f / speed), ForceMode.Force);
            }
            Vector3 dir = speed > 0.05f ? slide / speed : _lastDir;
            if (dir.sqrMagnitude < 0.5f) dir = fallback;
            Vector3 side = Vector3.Cross(Vector3.up, dir);
            Vector3 e = col.bounds.extents;
            float width = 2f * (Mathf.Abs(e.x * side.x) + Mathf.Abs(e.z * side.z));

            ref Contact c = ref Contacts[n++];
            c.Index = i;
            c.Body = rb;
            c.Point = hit.point;
            c.Normal = hit.normal;
            c.Velocity = slide;
            c.Press = Mathf.Clamp(rb.mass / 9f, 0.4f, 1.6f);
            c.Width = Mathf.Clamp(width * 0.85f, 0.08f, 0.4f);
            massSum += rb.mass;
            speedSum += rb.mass * speed;
        }
        ContactCount = n;
        SlideSpeed = massSum > 0f ? speedSum / massSum : 0f;
        if (SlideSpeed > 0.05f)
        {
            Vector3 v = Vector3.ProjectOnPlane(_dragBody.velocity, Vector3.up);
            if (v.sqrMagnitude > 0.0025f) _lastDir = v.normalized;
        }
    }

    private static void StartDrag(Player player, Corpse corpse, Source source)
    {
        if (!Wake(corpse))
        {
            BloodFXPlugin.Log.LogInfo("BloodFX corpse drag: body is still settling, try again in a moment");
            return;
        }

        // Grab the part you are looking at: the first of the body's own colliders along your aim
        // (they are triggers, and your gun is in the way), else whichever part lies nearest your aim.
        Camera cam = Camera.main;
        Vector3 eye = cam != null ? cam.transform.position : player.Transform.Original.position + Vector3.up * 1.5f;
        Vector3 aim = cam != null ? cam.transform.forward : player.Transform.Original.forward;
        bool havePoint = false;
        Vector3 point = Vector3.zero;
        int hits = Physics.RaycastNonAlloc(eye, aim, _grabHits, GrabReach, ~0, QueryTriggerInteraction.Collide);
        float hitDist = float.MaxValue;
        for (int h = 0; h < hits; h++)
        {
            ref RaycastHit rh = ref _grabHits[h];
            if (rh.distance >= hitDist || rh.collider == null || rh.collider.GetComponentInParent<Corpse>() != corpse) continue;
            hitDist = rh.distance;
            point = rh.point;
            havePoint = true;
        }

        Rigidbody best = null;
        float bestDist = float.MaxValue;
        _partCount = 0;
        foreach (RigidbodySpawner s in corpse.Ragdoll._rigidbodySpawners)
        {
            Rigidbody rb = s.Rigidbody;
            if (rb == null) continue;
            if (_partCount < MaxParts)
            {
                int i = _partCount++;
                _parts[i] = rb;
                _partCols[i] = s.GetComponent<Collider>() ?? rb.GetComponent<Collider>();
                _partJoints[i] = rb.GetComponent<Joint>();
                // more solver work while it is being hauled about, so the joints hold the bones together
                _solverIter[i] = rb.solverIterations;
                _solverVelIter[i] = rb.solverVelocityIterations;
                rb.solverIterations = Mathf.Max(rb.solverIterations, DragSolverIterations);
                rb.solverVelocityIterations = Mathf.Max(rb.solverVelocityIterations, DragSolverVelocityIterations);
                _projection[i] = -1f;
                if (_partJoints[i] is CharacterJoint cj) { _projection[i] = cj.projectionDistance; cj.projectionDistance = DragProjection; }
                else if (_partJoints[i] is ConfigurableJoint cfj) { _projection[i] = cfj.projectionDistance; cfj.projectionDistance = DragProjection; }
            }
            Vector3 com = rb.worldCenterOfMass;
            float d;
            if (havePoint) d = (com - point).sqrMagnitude;
            else
            {
                // distance from your line of sight
                Vector3 to = com - eye;
                float t = Mathf.Clamp(Vector3.Dot(to, aim), 0f, GrabReach);
                d = (to - aim * t).sqrMagnitude;
            }
            if (d < bestDist) { bestDist = d; best = rb; }
        }
        if (best == null) { RestoreParts(); return; }
        if (!havePoint)
        {
            // hold it by the side facing you
            Vector3 to = best.worldCenterOfMass - eye;
            point = best.worldCenterOfMass - aim * Mathf.Min(0.06f, to.magnitude * 0.5f);
        }
        if (BloodFXPlugin.Instance.DebugLog.Value)
            BloodFXPlugin.Log.LogInfo($"BloodFX corpse drag: holding {best.name} ({(havePoint ? "aimed at it" : "nearest to the aim")})");
        corpse.Ragdoll.WakeUp();

        _dragCorpse = corpse;
        _dragBody = best;
        _grabLocal = best.transform.InverseTransformPoint(point);
        _holdDistance = Mathf.Clamp(Vector3.ProjectOnPlane(point - player.Transform.Original.position, Vector3.up).magnitude, 0.7f, 1.4f);
        _dragSource = source;
        _stretchedSince = -1f;
        _lastDir = Vector3.ProjectOnPlane(player.Transform.Original.forward, Vector3.up).normalized;
        _speedBefore = player.MovementContext.CharacterMovementSpeed;
        _rags[corpse.Ragdoll].Dragged = true;
        ContactCount = 0;
        SlideSpeed = 0f;
        _dragStart = Time.time;
        _groundedSum = 0f;
        _gripMax = 0f;
        _topSpeed = 0f;
        _jointMax = 0f;
        _steps = 0;
    }

    /// <summary>Puts the solver settings and joint projection back as EFT had them.</summary>
    private static void RestoreParts()
    {
        for (int i = 0; i < _partCount; i++)
        {
            Rigidbody rb = _parts[i];
            if (rb != null)
            {
                rb.solverIterations = _solverIter[i];
                rb.solverVelocityIterations = _solverVelIter[i];
            }
            if (_projection[i] >= 0f)
            {
                if (_partJoints[i] is CharacterJoint cj) cj.projectionDistance = _projection[i];
                else if (_partJoints[i] is ConfigurableJoint cfj) cfj.projectionDistance = _projection[i];
            }
            _parts[i] = null;
            _partCols[i] = null;
            _partJoints[i] = null;
        }
        _partCount = 0;
    }

    private static void StopDrag()
    {
        if (_dragCorpse != null && _dragCorpse.Ragdoll != null && _rags.TryGetValue(_dragCorpse.Ragdoll, out var state))
        {
            state.Dragged = false;
            state.LastTouched = Time.time;
            // Let EFT judge afresh whether the body has come to rest.
            foreach (var part in _dragCorpse.Ragdoll._rigidbodySleepHierarchy)
                part.Reset();
        }

        Player player = MainPlayer();
        if (player != null && player.MovementContext != null && _dragSource != Source.None)
            player.MovementContext.SetCharacterMovementSpeed(_speedBefore);

        if (_dragCorpse != null && _steps > 0 && BloodFXPlugin.Instance.DebugLog.Value)
            BloodFXPlugin.Log.LogInfo($"BloodFX corpse drag: {Time.time - _dragStart:0.0} s, parts on the floor {_groundedSum / _steps:0.0} of {_partCount}, " +
                $"held part at most {_gripMax * 100f:0} cm above the floor, joints came apart up to {_jointMax * 100f:0.0} cm, slid up to {_topSpeed:0.00} m/s");
        RestoreParts();

        _dragCorpse = null;
        _dragBody = null;
        _dragSource = Source.None;
        ContactCount = 0;
        SlideSpeed = 0f;
    }

    private static void ClearPending()
    {
        _pendingCorpse = null;
        _pendingAction = null;
    }

    private static Corpse LookedAtCorpse()
    {
        return MainPlayer()?.InteractableObject as Corpse;
    }

    private static Player MainPlayer()
    {
        GameWorld gw = Singleton<GameWorld>.Instance;
        return gw != null ? gw.MainPlayer : null;
    }

    public static void Reset()
    {
        ClearPending();
        _dragCorpse = null;
        _dragBody = null;
        _dragSource = Source.None;
        RestoreParts();
        ContactCount = 0;
        SlideSpeed = 0f;
        DragSound.Stop();
        _rags.Clear();
        _weighted.Clear();
    }
}
