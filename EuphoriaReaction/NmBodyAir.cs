using EFT;
using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// Keeping a reacting body honest with the world in the air: momentum and gravity stay the engine's,
/// but these things that left bodies floating or flailing at the end of a jump or a drop are closed:
///  - the air phase: taken over off the ground (or knocked off a drop mid-reaction) the balancer, the
///    upright assist and the shot task stay off until he touches down. In the air they stepped at
///    nothing, lost their balance, and the engine's "fallen" never clears, so every landing played as a
///    fall-over (and a "catches himself" stance match could start in mid-air). He falls held in his
///    pose, hits only shove him, and the normal balance starts on the landing;
///  - the root drops with the body: whenever the ground under the body is lower than the root (off a
///    ledge, down stairs, the end of a jump), the root goes down to it at once (rising onto things
///    still waits for SupportFollow's settle, so dragging never pops him onto crates);
///  - no recovery in the air: "found his feet", the timeouts and the stance match all need him
///    touching the ground (feet for standing/crouching), not just balancing with no sideways speed;
///  - the landing is in the level before he gets there: while falling, his path is traced through the
///    real colliders (gravity and his current velocity) and the level is mirrored around the landing
///    spot ahead of time, instead of after he has already gone through where it should have been.
/// </summary>
internal partial class NmBody
{
    private const float DropMin = 0.08f;      // m: smaller drops are ordinary floor
    private const float FallingSpeed = 1.5f;  // m/s down: trace the landing while falling faster than this
    private readonly float[] _airP = new float[3], _airV = new float[3];

    /// The root goes straight down to the ground under the body when that's lower than the root.
    private bool DropFollow(Vector3 want, Vector3 pelvis, out Vector3 dropped)
    {
        dropped = want;
        if (!RayWorld(new Vector3(want.x, pelvis.y + 0.1f, want.z), Vector3.down, 80f, out RaycastHit g) || g.normal.y < 0.5f) return false;
        if (g.point.y > want.y - DropMin) return false;
        dropped = new Vector3(want.x, g.point.y, want.z);
        return true;
    }

    private float _groundUntil, _nextRootGround;
    private int _rootHighChecks;

    /// After a living hand-back: his root left standing on air (lifted onto something that has since
    /// gone, or never let down) goes down onto the floor under it. Twice in a row, so a step on
    /// stairs or a capsule resting on an edge isn't snapped.
    private void GroundRootTick()
    {
        if (Time.time > _groundUntil || _p == null || _blending || _jumpTaken || _getUp != null || Time.time < _nextRootGround) return;
        _nextRootGround = Time.time + 0.25f;
        if (_p.HealthController?.IsAlive != true) return;
        Vector3 root = _p.Transform.position;
        if (!RayWorld(root + Vector3.up * 0.3f, Vector3.down, 3f, out RaycastHit g) || g.normal.y < 0.5f) { _rootHighChecks = 0; return; }
        float gap = root.y - g.point.y;
        if (gap < 0.2f) { _rootHighChecks = 0; return; }
        if (++_rootHighChecks < 2) return;
        _rootHighChecks = 0;
        MoveRoot(new Vector3(root.x, g.point.y, root.z));
        BotActions.SyncMover(_p);
        EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} was standing {gap:F2} m above the ground after the hand-back ({g.collider.name}) - put down");
    }

    private bool Contact(int k) => nm_agent_get_part_contact(_agent, k) != 0;
    private bool FeetDown => Contact(3) || Contact(6) || Contact(2) || Contact(5);

    private bool AnyContact
    {
        get { for (int k = 0; k < Parts; k++) if (Contact(k)) return true; return false; }
    }

    /// On the ground and not rising or falling: feet for a standing/crouching recovery, anything when lying.
    private bool Grounded(bool feet)
    {
        nm_agent_get_com(_agent, _airP, _airV);
        if (Mathf.Abs(_airV[2]) > 0.8f) return false; // NM z is up
        return feet ? FeetDown : AnyContact;
    }

    /// While falling: trace the path through the real colliders and mirror the level at the landing.
    private void PredictLanding(bool force = false)
    {
        if (!force && (Time.time < _nextRebake || _rebakes >= 8)) return;
        nm_agent_get_com(_agent, _airP, _airV);
        Vector3 p = ToUnity(_airP[0], _airP[1], _airP[2]), v = ToUnity(_airV[0], _airV[1], _airV[2]);
        if (!force && v.y > -FallingSpeed) return;
        const float dt = 0.05f;
        for (int i = 0; i < 80; i++)
        {
            Vector3 next = p + v * dt;
            v.y -= 9.81f * dt;
            Vector3 seg = next - p;
            if (RayWorld(p, seg.normalized, seg.magnitude + 0.9f, out RaycastHit hit)) // + about a body's half height
            {
                Vector3 land = hit.point;
                if (hit.normal.y < 0.5f && RayWorld(hit.point + hit.normal * 0.3f + Vector3.up * 0.5f, Vector3.down, 20f, out RaycastHit under))
                    land = under.point; // hit a wall on the way: he ends up at its foot
                if (new Vector2(land.x - _bakeCentre.x, land.z - _bakeCentre.z).magnitude < 2f && Mathf.Abs(land.y - _bakeCentre.y) < 1f) return;
                _rebakes++;
                _nextRebake = Time.time + 0.4f;
                BakeLevel(_p, land);
                EuphoriaPlugin.LogV($"Delirium: {_p.ProfileId} landing traced {(land - p).magnitude:F1} m ahead - level mirrored there");
                return;
            }
            p = next;
        }
    }

    // ------------------------------------------------------------------ air phase

    private bool _inAir;              // off the ground right now: no balance, steps or catching himself until he lands
    private float _airSince, _airFreeFor, _airPeakFall;
    private const float AirStiffness = 9f;    // muscles holding his pose while he falls
    private const float AirMinTime = 0.08f;   // s off the ground before a touch counts as the landing (take-off contact)
    private const float AirMaxTime = 6f;      // s: never stuck in the air phase (a contact the engine didn't report)
    private const float AirEnterGap = 0.4f;   // m: mid-reaction, his feet this far above the ground counts as off it
    private const float AirEnterFor = 0.15f;  // s touching nothing, falling, before that's an air phase
    private const float LandGap = 0.3f;       // m: a touch with his feet or pelvis this close to the ground is the landing

    /// Attach: the game's own movement says he's off the ground (not grounded, or in its jump state).
    private static bool GameAirborne(Player p, out float freefall)
    {
        freefall = 0f;
        try
        {
            var mc = p?.MovementContext;
            if (mc == null) return false;
            freefall = mc.FreefallTime;
            return !mc.IsGrounded || InJump(p);
        }
        catch { return false; }
    }

    /// Lowest of feet and pelvis above the real ground under each.
    private float LowestGap()
    {
        float gap = float.MaxValue;
        foreach (int k in AirGapParts)
        {
            Part(k, out Vector3 pos, out _);
            float g = RayWorld(pos + Vector3.up * 0.3f, Vector3.down, 40f, out RaycastHit hit) ? pos.y - hit.point.y : 40f;
            gap = Mathf.Min(gap, g);
        }
        return gap;
    }
    private static readonly int[] AirGapParts = { 0, 3, 6 };

    /// Into the air phase: balance and upright assist off, the pose held, momentum left alone.
    private void AirBegin(string why)
    {
        _inAir = true; _airSince = Time.time; _airFreeFor = 0f; _airPeakFall = 0f;
        Send("bodyBalance", ("start", false));
        Send("stayUpright", ("start", false));
        Send("setStiffness", ("bodyStiffness", AirStiffness), ("damping", 1f), ("mask", "fb"));
        EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} in the air ({why}) - no balance or steps until he lands");
    }

    /// Update, alive, after the physics steps: lands him, or starts the air phase when he goes off a drop.
    /// True while he's in the air.
    private bool AirTick()
    {
        nm_agent_get_com(_agent, _airP, _airV);
        float vz = _airV[2]; // NM z is up
        if (_inAir && (_tripped || _stairRoll || _downSince > 0f))
        {
            _inAir = false; // a trip over a rail (or a stair roll) took over in the air: it runs its own behaviours
            EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} air phase ended by a trip/roll after {Time.time - _airSince:F2}s");
        }
        if (_inAir)
        {
            _airPeakFall = Mathf.Max(_airPeakFall, -vz);
            float t = Time.time - _airSince;
            bool touch = AnyContact && LowestGap() < LandGap;
            if (t < AirMaxTime && (t < AirMinTime || vz > 0.5f || !touch)) return true;
            AirLand(t >= AirMaxTime);
            return false;
        }
        // mid-reaction: going off a drop while still on his feet (the balancer hasn't given up yet)
        if (_downSince > 0f || _matching || _pgu || _getUp != null || _rolling || _stairRoll || _tripped
            || nm_agent_has_fallen(_agent) == 1 || AnyContact || vz > -FallingSpeed || LowestGap() < AirEnterGap)
        {
            _airFreeFor = 0f;
            return false;
        }
        _airFreeFor += Time.deltaTime;
        if (_airFreeFor >= AirEnterFor) AirBegin($"went off a drop, falling {-vz:F1} m/s");
        return _inAir;
    }

    /// Touched down: the normal balance (it steps, catches himself or loses it and goes down as usual).
    private void AirLand(bool timedOut)
    {
        _inAir = false;
        ToNm(DollFacing(), _f3);
        nm_agent_task_balance(_agent, _world, "*", _f3);
        TuneAlive();
        _lastHitAt = Time.time; // the landing is a knock: no "found his feet" or planted feet right through it
        _stableFor = 0f;
        _until = Mathf.Max(_until, Time.time + 3f);
        EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} {(timedOut ? "still in the air after " + AirMaxTime.ToString("F0") + "s - balance back on anyway" : "landed")}" +
                           $" after {Time.time - _airSince:F2}s, falling {_airPeakFall:F1} m/s at most");
    }

    /// Hit while in the air: the shove only. The shot task would start the balancer mid-air.
    private void AirHit(Vector3 at, Vector3 dir, float ns)
    {
        dir.y *= 0.25f;
        if (dir.sqrMagnitude < 1e-6f) return;
        dir.Normalize();
        ToNm(at, _f3);
        int part = nm_agent_nearest_part(_agent, _f3);
        ToNm(dir * ns, _g3);
        nm_agent_apply_push(_agent, _f3, _g3);
        _lastHit = (at, dir, part);
        EuphoriaPlugin.Log($"Delirium: hit in the air {ns:F0} N*s - shove only, balance waits for the landing");
    }
}
