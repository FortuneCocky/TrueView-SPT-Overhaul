using System;
using System.Reflection;
using EFT;
using EFT.Ballistics;
using UnityEngine;
using static Delirium.Native;

namespace EuphoriaReaction;

/// <summary>
/// Impact damage while Euphoria owns a living bot. Two ways to get hurt:
///  - a fall: he drops and lands. The impact speed is the fastest measured fall, or the speed a drop of
///    the height he fell from gives (sqrt(2gh), height above the floor where the fall began), whichever is higher.
///  - a crash: a fast body loses most of its speed in one frame against the world (a wall, a rail, a crate,
///    the floor). Never while the stance match steers him: its pull read as 7.7 m/s crashes and killed him.
/// Damage is the kinetic energy above the safe speed, 0.5 * m * (v^2 - v0^2), over JoulesPerHp. The game's
/// own damage path takes it, so the bleeding and the death follow the normal rules.
/// </summary>
internal partial class NmBody
{
    private const float ImpactMass = 80f;   // kg: a body with his kit, for the energy
    private const float Gravity = 9.81f;
    private const float FallStart = 1.5f;   // m/s down: a fall has begun
    private const float CrashDrop = 0.6f;   // share of speed lost in one frame that counts as a crash

    private bool _falling;
    private float _fallFromY, _fallPeak, _prevSpeed;

    private void ImpactDamageReset() { _falling = false; _fallPeak = 0f; _prevSpeed = 0f; }

    private void ImpactDamageTick()
    {
        var ep = EuphoriaPlugin.Instance;
        if (ep == null || !ep.ImpactDamage.Value || _p == null || _dying || _rigCheck || _p.HealthController?.IsAlive != true)
        {
            ImpactDamageReset();
            return;
        }
        if (_matching)
        {
            // the stance match steers the body: its speed is the match's, not a crash's
            ImpactDamageReset();
            return;
        }
        nm_agent_get_com(_agent, _airP, _airV);
        Vector3 p = ToUnity(_airP[0], _airP[1], _airP[2]), v = ToUnity(_airV[0], _airV[1], _airV[2]);
        float speed = v.magnitude, safe = ep.ImpactSafeSpeed.Value;
        bool landed = false;

        if (!_falling && v.y < -FallStart)
        {
            _falling = true;
            _fallPeak = 0f;
            _fallFromY = HeightAbove(p);
        }
        if (_falling)
        {
            _fallPeak = Mathf.Max(_fallPeak, -v.y);
            if (AnyContact)
            {
                float drop = Mathf.Max(0f, _fallFromY - HeightAbove(p));
                float impact = Mathf.Max(_fallPeak, Mathf.Sqrt(2f * Gravity * drop));
                _falling = false;
                landed = true;
                Hurt(impact, safe, $"fell {drop:F1} m, landed at {impact:F1} m/s");
            }
            else if (v.y > -0.5f) _falling = false; // hung up, no longer falling
        }
        else if (_prevSpeed > safe && speed < _prevSpeed * (1f - CrashDrop) && AnyContact) // stopped by something, not by his own limbs
        {
            Hurt(_prevSpeed, safe, $"crashed at {_prevSpeed:F1} m/s, down to {speed:F1}");
        }
        _prevSpeed = landed ? 0f : speed; // the landing's own speed drop is not a crash
    }

    /// Height of a point above the floor under it (the real level).
    private static float HeightAbove(Vector3 p) =>
        RayWorld(p + Vector3.up * 0.2f, Vector3.down, 40f, out RaycastHit h) ? p.y - h.point.y : p.y;

    private static readonly FieldInfo LastDamageField =
        typeof(Player).GetField("LastDamageInfo", BindingFlags.Instance | BindingFlags.NonPublic);

    private void Hurt(float speed, float safe, string why)
    {
        float excess = 0.5f * ImpactMass * (speed * speed - safe * safe);
        if (excess <= 0f) return;
        float dmg = excess / EuphoriaPlugin.Instance.JoulesPerHp.Value;
        try
        {
            var di = new DamageInfo
            {
                DamageType = EDamageType.Fall,
                Damage = dmg,
                Direction = Vector3.down,
                HitPoint = _p.Transform.position + Vector3.up,
                HitNormal = Vector3.up,
                PenetrationPower = 0f,
            };
            // The game credits a death to whoever dealt the last damage. A fall with no shooter would clear
            // that and the kill would read "Died", so the fall carries the last shooter and his weapon.
            if (LastDamageField?.GetValue(_p) is DamageInfo last && last.Player != null)
            {
                di.Player = last.Player;
                di.Weapon = last.Weapon;
            }
            _p.ApplyDamageInfo(di, EBodyPart.Chest, EBodyPartColliderType.SpineTop, 0f);
            EuphoriaPlugin.Log($"Delirium: {_p.ProfileId} {why} - {dmg:F0} damage");
        }
        catch (Exception e) { EuphoriaPlugin.Log("Delirium: impact damage failed: " + e.Message); }
    }
}
