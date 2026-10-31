using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// Saves, stuck bodies and how he lies once down.
///  - Save: the moment a stumble goes over (balance lost) he catches himself and is matched into the
///    nearest stance (stand or crouch; a prone bot shoved about stays prone), unless FallReason
///    (NmBodyAdapt) gives a reason: a hard one always falls, a soft one at the config's odds.
///  - Stuck: no reaction may run on forever; long after the last hit he is put in the stance
///    closest to how he lies.
///  - Fallen: never limp while alive: he props himself up and holds the wound, or, badly hurt,
///    shifts slowly, with the limbs' held pose opened (NmBodyOpen).
/// </summary>
internal partial class NmBody
{
    private const float SaveTilt = 40f;    // deg the trunk may lean off vertical and still be caught
    private const float ProneSaveAfter = 0.6f; // s after a hit before a shoved prone bot settles back

    private bool _saveRolled;
    private float _matchCooldown;
    private bool _floorNetLogged;
    private int _fallenStyle = -1;

    private int _matchFell; // stance matches that fell over this reaction

    private static float SaveChance => EuphoriaPlugin.Instance?.StumbleSave.Value ?? 0.7f;
    private static float StuckMax => EuphoriaPlugin.Instance?.StuckMax.Value ?? 12f;

    private void RecoverAttach()
    {
        _saveRolled = false;
        _fallenStyle = -1;
        _matchCooldown = 0f;
        _matchFell = 0;
        _floorNetLogged = _legNetLogged = false;
        _tnHave = _tnLogged = false;
        float grip = EuphoriaPlugin.Instance?.GroundGrip.Value ?? 1.5f;
        if (nm_set_surface(0, grip, 0.1f) != 0) EuphoriaPlugin.Log("Delirium: couldn't set ground grip " + grip);
    }

    /// Trunk lean off vertical (deg), from the doll's pelvis-to-neck line.
    private float TrunkTilt()
    {
        Part(0, out Vector3 pel, out _);
        Part(11, out Vector3 neck, out _);
        Vector3 up = neck - pel;
        return up.sqrMagnitude < 1e-6f ? 90f : Vector3.Angle(up, Vector3.up);
    }

    /// Update, once per stumble: true when he caught himself (the stance match took over).
    private bool StumbleSaveTick(float h)
    {
        // already put down (the stumble's max, or the catch already sent): his balance is gone, and a stance match
        // pulling a falling body upright launched him into whatever was near (7-12 m/s, flips)
        if (_saveRolled || _downSince > 0f || _catchFallSent) return false;
        if (_startPose == StartPose.Prone)
        {
            // shoved while prone: he settles back into prone instead of flailing on
            if (Time.time - _lastHitAt < ProneSaveAfter) return false;
            _saveRolled = true;
            if (Random.value >= SaveChance || !LyingFaceDown(out Vector3 hd)) return false;
            EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} stays down in prone");
            return BeginMatch(StartPose.Prone, hd);
        }
        // the moment the stumble goes over
        float tilt = TrunkTilt();
        bool over = nm_agent_has_fallen(_agent) == 1
                    || (nm_agent_is_balancing(_agent) == 0 && Time.time - _attachAt > 0.3f && tilt > 15f);
        if (!over) return false;
        _saveRolled = true;
        // balance is the rule: he falls only for a reason (NmBodyAdapt), and a soft one only at the odds
        string why = FallReason(tilt, h, out bool hard);
        if (why != null && (hard || Random.value >= SaveChance))
        {
            EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} goes down: {why} (lean {tilt:F0} deg, COM {h:F2}m)");
            return false;
        }
        StartPose to = RecoverStance(h < ComScaled(0.65f, 0.7f)); // by his health and how low he is (NmBodyPhase)
        if (to == StartPose.Prone)
        {
            EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} too hurt to catch himself ({StumbleEngine.HpFraction(_p):P0} HP)");
            return false;
        }
        // still upright: straight back to his feet. Once the engine calls a fall its balancer is gone for good, so a
        // stance match on a man barely leaning had nothing holding him up: he "caught himself" at 4 deg, then
        // collapsed in the match, lay 5 s and got up
        if (to == StartPose.Stand && tilt < 20f && h > ComScaled(0.95f, 0.85f))
        {
            EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} regains his footing{(why != null ? $" despite: {why}" : "")} (lean {tilt:F0} deg, COM {h:F2}m)");
            _blendDur = 0.5f;
            ReleaseToStance();
            return true;
        }
        // the catch: a hard pull upright and firm legs while the stance match blends him in
        Send("stayUpright", ("start", true), ("stiffness", Tune.Upright * 2.5f));
        Send("configureBalance", ("legStiffness", Tune.LegStiff * 1.3f));
        EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} catches himself{(why != null ? $" despite: {why}" : "")} (lean {tilt:F0} deg, COM {h:F2}m) - {to.ToString().ToLowerInvariant()}");
        if (BeginMatch(to, DollFacing())) return true;
        ReleaseToStance();
        return true;
    }

    /// Update: true when he'd been in the reaction too long and was sent to his closest stance.
    private bool StuckTick(float h)
    {
        if (_rolling || _matching || _dying) return false;
        float since = Time.time - Mathf.Max(_attachAt, _lastHitAt);
        if (since < StuckMax) return false;
        EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} stuck {since:F0}s - recovering to the closest stance");
        StopFallenPose();
        // half up (kneeling, sitting): stand him into a stance, unless that already failed this reaction -
        // it fell again and again, 6 s each, before the get-up ever played
        if (!LyingFaceDown(out _) && _matchFell == 0 && h > ComScaled(0.5f, 0.6f) && TrunkTilt() < 60f)
        {
            if (BeginMatch(h > ComScaled(0.85f, 0.9f) ? StartPose.Stand : StartPose.Crouch, DollFacing())) return true;
        }
        else if (HandBackFromGround()) return true; // a get-up, or prone when face down and badly hurt
        _blendDur = GetUpBlend;
        ReleaseToStance();
        return true;
    }

    private static readonly int[] FloorNetParts = { 0, 10 };
    private readonly float[] _fnP = new float[3], _fnR = new float[4], _fnV = new float[3], _fnW = new float[3];

    /// After the physics steps: the pelvis or chest sunk under the real ground (a fast part through the
    /// one-sided ground triangles, or a gap between sampled patches; the backup plane is 30 m down) ->
    /// the whole doll is put back on top, its downward speed taken out.
    private void FloorNet()
    {
        float sink = 0f;
        foreach (int k in FloorNetParts)
        {
            Part(k, out Vector3 p, out _);
            // a floor just above the part (not a table or a ceiling: within 0.45 m)
            if (!RayWorld(p + Vector3.up * 0.45f, Vector3.down, 0.45f, out RaycastHit h) || h.normal.y < 0.7f) continue;
            sink = Mathf.Max(sink, h.point.y - p.y);
        }
        if (sink < 0.08f) { LegFloorNet(); TunnelNet(); return; }
        float lift = sink + 0.05f;
        for (int k = 0; k < Parts; k++)
        {
            if (nm_agent_get_part_transform(_agent, k, _fnP, _fnR) < 0) continue;
            nm_agent_get_part_velocity(_agent, k, _fnV, _fnW);
            _fnP[2] += lift;                      // engine z is up
            if (_fnV[2] < 0f) _fnV[2] = 0f;
            nm_agent_set_part_state(_agent, k, _fnP, _fnR, _fnV, _fnW);
        }
        if (!_floorNetLogged) { _floorNetLogged = true; EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} body sank {sink:F2} m through the ground - put back on top"); }
    }

    private readonly Vector3[] _tnPrev = new Vector3[Parts];
    private bool _tnHave, _tnLogged;

    /// Any part that passed down through a real floor since the last step (through the gaps between the
    /// baked stair treads, a thin landing): the line from where it was to where it is crosses a walkable
    /// surface that is really over it now -> back on top, its downward speed taken out; the pelvis or chest
    /// takes the whole body with it. A fall off a stair's open side never crosses a floor, so it stays a fall.
    private void TunnelNet()
    {
        bool have = _tnHave;
        _tnHave = true;
        for (int k = 0; k < Parts; k++)
        {
            Part(k, out Vector3 at, out _);
            Vector3 was = _tnPrev[k];
            _tnPrev[k] = at;
            if (!have || at.y >= was.y - 0.02f) continue;
            Vector3 d = at - was;
            float len = d.magnitude;
            if (len < 0.02f || len > 3f || !RayWorld(was, d / len, len, out RaycastHit h) || h.normal.y < 0.7f) continue;
            float under = h.point.y - at.y;
            if (!RayWorld(at + Vector3.up * (under + 0.1f), Vector3.down, under + 0.1f, out RaycastHit top) || top.normal.y < 0.7f) continue;
            float lift = top.point.y - at.y + 0.08f;
            bool core = k == 0 || k == 10;
            for (int m = 0; m < Parts; m++)
            {
                if (!core && m != k) continue;
                if (nm_agent_get_part_transform(_agent, m, _fnP, _fnR) < 0) continue;
                nm_agent_get_part_velocity(_agent, m, _fnV, _fnW);
                _fnP[2] += lift;                  // engine z is up
                if (_fnV[2] < 0f) _fnV[2] = 0f;
                nm_agent_set_part_state(_agent, m, _fnP, _fnR, _fnV, _fnW);
                Part(m, out _tnPrev[m], out _);
                _tnPrev[m].y += lift;
            }
            if (!_tnLogged) { _tnLogged = true; EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} part {k} went through the floor ({h.collider.name}, {under:F2} m under) - put back on top"); }
            if (core) return;
        }
    }

    // leg parts, parent-first, with the part they hang from
    private static readonly (int part, int parent)[] LegNetParts = { (1, 0), (2, 1), (3, 2), (4, 0), (5, 4), (6, 5) };
    private bool _legNetLogged;

    /// The feet and shins on their own: a leg part that slipped under the floor (a foot through the thin
    /// ground triangles, between patches) rests there, and the pelvis net never sees it - the hips held
    /// the body up while the legs hung under the map. A walkable surface crossed on the line from the part
    /// it hangs from down to it means it's underneath: it's put back on top, its downward speed taken out.
    private void LegFloorNet()
    {
        foreach (var (k, parent) in LegNetParts)
        {
            Part(parent, out Vector3 from, out _);
            Part(k, out Vector3 at, out _);
            Vector3 d = at - from;
            float len = d.magnitude;
            if (len < 0.05f || !RayWorld(from, d / len, len, out RaycastHit h) || h.normal.y < 0.7f) continue;
            float under = h.point.y - at.y;
            if (under < 0.03f) continue;
            // and that floor really is over the part, not a step edge the leg cuts across to a lower step
            if (!RayWorld(at + Vector3.up * (under + 0.1f), Vector3.down, under + 0.1f, out RaycastHit top) || top.normal.y < 0.7f) continue;
            under = top.point.y - at.y;
            if (nm_agent_get_part_transform(_agent, k, _fnP, _fnR) < 0) continue;
            nm_agent_get_part_velocity(_agent, k, _fnV, _fnW);
            _fnP[2] += under + 0.06f;             // engine z is up
            if (_fnV[2] < 0f) _fnV[2] = 0f;
            nm_agent_set_part_state(_agent, k, _fnP, _fnR, _fnV, _fnW);
            if (!_legNetLogged) { _legNetLogged = true; EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} leg part {k} went {under:F2} m under the ground ({h.collider.name}) - put back on top"); }
        }
    }

    /// Down (or shot while down): lie one of several ways, chosen once per fall.
    private void FallenPose(bool shotAgain)
    {
        bool first = _fallenStyle < 0;
        if (first)
        {
            // alive he never lies limp: he props himself up and holds the wound (the injuredOnGround behaviour),
            // or, badly hurt, shifts in pain. Limp is for the dead. (No foetal curl: it read as curling up.)
            _fallenStyle = StumbleEngine.HpFraction(_p) < 0.4f && Random.value < 0.4f ? 2 : 0;
            // the fall is caught: catchFall kept driving his hips and knees into its landing bend on the
            // ground, folding the legs up behind him (knees at 148 deg) and pushing him along the floor
            Send("catchFall", ("start", false));
            OpenZero("down");
        }
        switch (_fallenStyle)
        {
            case 0: // the wounded rest: up on an elbow, a hand on the wound, looking back where the shot came from
            {
                int wound = _lastHit.part >= 0 ? _lastHit.part : 7;
                Vector3 shooter = _lastHit.dir.sqrMagnitude > 1e-4f ? _lastHit.at - _lastHit.dir.normalized * 8f : Vector3.zero;
                Send("injuredOnGround", ("start", true), ("numInjuries", 1), ("injury1Component", wound), ("attackerPos", shooter));
                break;
            }
            default: // badly hurt: small, slow shifting
                Send("bodyWrithe", ("start", true), ("armAmplitude", 0.3f), ("legAmplitude", 0.25f), ("backAmplitude", 0.3f),
                     ("kneeAmplitude", 0.3f), ("elbowAmplitude", 0.3f), ("armStiffness", 6f), ("legStiffness", 6f),
                     ("backStiffness", 6f), ("legPeriod", 2.6f), ("backPeriod", 3.4f), ("armPeriod", 2.4f));
                break;
        }
        if (first || shotAgain)
            EuphoriaPlugin.Log($"Delirium: down - {(_fallenStyle == 0 ? "props himself up and holds the wound" : "hurt, shifting")}{(shotAgain ? " (shot again)" : "")}");
    }

    private void StopFallenPose()
    {
        Send("injuredOnGround", ("start", false));
        Send("bodyFoetal", ("start", false));
        Send("bodyRelax", ("start", false));
        Send("bodyWrithe", ("start", false));
    }
}
