using System.Collections.Generic;
using System.Text;
using EFT;
using EFT.Interactive;
using UnityEngine;

namespace BloodFX;

/// <summary>
/// The blood a dragged body smears on the ground. Each hit BloodFX sees is remembered as
/// blood on the bone that was hit; when a body is dragged, every part of it that is on the
/// ground carries that much blood as ink, which is laid down as a streaky smear behind the
/// part as it slides, runs out over a few metres, and slowly wets again from the wound.
/// The parts and their ground contacts come from <see cref="CorpseSystem"/>.
/// </summary>
public partial class BloodManager
{
    // ------------------------------------------------------------ how bloody each body is

    private struct Gore
    {
        public Transform Bone;
        public float Blood;
    }

    private readonly Dictionary<string, List<Gore>> _gore = new Dictionary<string, List<Gore>>();

    /// <summary>Remembers blood on the bone a hit landed on, so a dragged body smears from there.</summary>
    private void NoteGore(Player victim, Transform bone, float damage, EBodyPart part, bool dead)
    {
        if (bone == null) return;
        string id;
        try { id = victim.ProfileId; }
        catch { return; }
        if (string.IsNullOrEmpty(id)) return;
        if (_gore.Count > 256) _gore.Clear();
        if (!_gore.TryGetValue(id, out List<Gore> list))
        {
            list = new List<Gore>(8);
            _gore[id] = list;
        }
        // a wound bleeds about in proportion to the damage; the killing wound, and a head taken
        // apart, bleed the most
        float blood = Mathf.Clamp(damage / 50f, 0.15f, 1f);
        if (dead) blood *= part == EBodyPart.Head ? 2f : 1.5f;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Bone != bone) continue;
            Gore g = list[i];
            g.Blood = Mathf.Min(g.Blood + blood, 3f);
            list[i] = g;
            return;
        }
        if (list.Count >= 24) list.RemoveAt(0);
        list.Add(new Gore { Bone = bone, Blood = blood });
    }

    /// <summary>How much blood is on the part of a body that <paramref name="rb"/> moves (0 = none seen).</summary>
    private float BloodOn(string profile, Rigidbody rb)
    {
        if (string.IsNullOrEmpty(profile) || rb == null || !_gore.TryGetValue(profile, out List<Gore> list)) return 0f;
        float sum = 0f;
        for (int i = 0; i < list.Count; i++)
        {
            Transform bone = list[i].Bone;
            if (bone == null) continue;
            // the hit colliders hang under the ragdoll bones: a wound belongs to the nearest body above it
            Rigidbody owner = bone.GetComponentInParent<Rigidbody>();
            if (owner == rb) sum += list[i].Blood;
        }
        return sum;
    }

    // ------------------------------------------------------------ the drag

    private struct DragPart
    {
        public DragSmears.Handle Track;
        public Vector3 LastP;
        public float Ink, Blood, Seen, Metres;
        public bool Placed, Live;
    }

    private DragSmears _smears;
    private readonly DragPart[] _dragParts = new DragPart[CorpseSystem.MaxParts];
    private Corpse _dragCorpse;
    private float _dragLast, _dragMetres;
    private int _dragTracks, _dragDrops;

    private const float SmearStep = 0.07f;     // metres of slide between samples
    private const float InkPerMetre = 0.28f;   // a fully bloody part smears for about 3-4 m
    private const float RefillRate = 0.03f;    // share of its blood a part wets again with per second
    private const float Baseline = 0.3f;       // every dead body has bled under itself: the torso is always a little wet

    private int SmearCapacity() => Mathf.Clamp(_tier.Wounds * 3, 8, 64);

    private void DragTick(float now)
    {
        if (!_cfg.DragSmears.Value || !CorpseSystem.Dragging || CorpseSystem.DraggedCorpse == null)
        {
            if (_dragCorpse != null) EndDragSession();
            return;
        }
        Corpse corpse = CorpseSystem.DraggedCorpse;
        if (corpse != _dragCorpse) BeginDragSession(corpse, now);
        float dt = Mathf.Clamp(now - _dragLast, 0f, 0.1f);
        _dragLast = now;
        float amount = _cfg.DragSmearAmount.Value;
        float life = _cfg.DecalLifetime.Value;
        int n = CorpseSystem.ContactCount;
        for (int i = 0; i < n; i++)
        {
            ref CorpseSystem.Contact c = ref CorpseSystem.Contacts[i];
            if (c.Index < 0 || c.Index >= _dragParts.Length) continue;
            ref DragPart ps = ref _dragParts[c.Index];
            if (!ps.Live) continue;
            ps.Ink = Mathf.Min(ps.Blood, ps.Ink + ps.Blood * RefillRate * dt);
            bool stale = !ps.Placed || now - ps.Seen > 0.4f;
            ps.Seen = now;
            if (stale)
            {
                // just touched down (or we lost it for a moment): start afresh from here
                ps.LastP = c.Point;
                ps.Placed = true;
                ps.Track = default;
                continue;
            }
            Vector3 step = c.Point - ps.LastP;
            step -= c.Normal * Vector3.Dot(step, c.Normal);
            float d = step.magnitude;
            if (d < SmearStep) continue;
            if (d > 0.6f)
            {
                // it jumped (fell off a ledge, got shoved): don't draw a line through the air
                ps.LastP = c.Point;
                ps.Track = default;
                continue;
            }
            Vector3 dir = step / d;
            float strength = Mathf.Clamp01(ps.Ink * c.Press * amount);
            ps.Ink = Mathf.Max(0f, ps.Ink - d * InkPerMetre * c.Press);
            ps.Metres += d;
            _dragMetres += d;
            if (strength < 0.03f)
            {
                ps.LastP = c.Point;
                ps.Track = default;
                continue;
            }
            float width = c.Width;
            if (!_smears.IsLive(ps.Track) || !_smears.Extend(ps.Track, c.Point, c.Normal, dir, width, strength))
            {
                Color col = (Color)Tint(StreamBase, LightAt(c.Point, c.Normal), 1f);
                ps.Track = _smears.Begin(ps.LastP, c.Normal, dir, width, strength, col, now, life);
                _smears.Extend(ps.Track, c.Point, c.Normal, dir, width, strength);
                _dragTracks++;
            }
            // drops flicked off the edges of the smear as the body goes
            if (Random.value < 0.45f * strength)
            {
                Vector3 side = Vector3.Cross(c.Normal, dir).normalized * (Random.value < 0.5f ? -1f : 1f);
                Vector3 q = c.Point + side * (width * Random.Range(0.45f, 1.1f)) + dir * Random.Range(-0.15f, 0.1f);
                if (Physics.Raycast(q + c.Normal * 0.1f, -c.Normal, out RaycastHit hit, 0.25f, _mask, QueryTriggerInteraction.Ignore)
                    && Vector3.Dot(hit.normal, c.Normal) > 0.6f)
                {
                    Color dc = (Color)Tint(StreamBase, LightAt(hit.point, hit.normal) * Random.Range(0.75f, 1f), 1f);
                    QueueStuck(hit.point, hit.normal, dir, Random.Range(0.002f, 0.0055f) * Mathf.Lerp(0.7f, 1.3f, strength),
                        Random.Range(1.4f, 3f), dc, now, life);
                    _dragDrops++;
                }
            }
            ps.LastP = c.Point;
        }
    }

    private void BeginDragSession(Corpse corpse, float now)
    {
        if (_dragCorpse != null) EndDragSession();
        _dragCorpse = corpse;
        _dragLast = now;
        _dragMetres = 0f;
        _dragTracks = 0;
        _dragDrops = 0;
        for (int i = 0; i < _dragParts.Length; i++) _dragParts[i] = default;
        string profile = corpse.PlayerProfileID;
        int parts = CorpseSystem.PartCount;
        StringBuilder sb = _cfg.DebugLog.Value ? new StringBuilder() : null;
        float total = 0f;
        for (int i = 0; i < parts && i < _dragParts.Length; i++)
        {
            Rigidbody rb = CorpseSystem.Part(i);
            if (rb == null) continue;
            float blood = BloodOn(profile, rb);
            // a body that bled out lies in its own blood: the torso is wet even without a known wound
            string name = rb.name;
            bool torso = name.Contains("Spine") || name.Contains("Pelvis");
            blood = Mathf.Max(blood, torso ? Baseline : Baseline * 0.15f);
            ref DragPart ps = ref _dragParts[i];
            ps.Blood = Mathf.Min(blood, 2f);
            ps.Ink = ps.Blood;
            ps.Live = true;
            total += blood;
            if (sb != null && blood > Baseline) sb.Append(' ').Append(name).Append(' ').Append(blood.ToString("0.0"));
        }
        if (sb != null)
            BloodFXPlugin.Log.LogInfo($"BloodFX drag: body {profile} picked up, blood on its parts {total:0.0}{(sb.Length > 0 ? " (" + sb.ToString().Trim() + ")" : " (none seen, torso baseline only)")}");
    }

    private void EndDragSession()
    {
        if (_dragCorpse != null && _cfg.DebugLog.Value && _dragMetres > 0.05f)
            BloodFXPlugin.Log.LogInfo($"BloodFX drag: let go after {_dragMetres:0.0} m, {_dragTracks} smear tracks, {_dragDrops} flicked drops");
        _dragCorpse = null;
        for (int i = 0; i < _dragParts.Length; i++) _dragParts[i] = default;
    }
}
