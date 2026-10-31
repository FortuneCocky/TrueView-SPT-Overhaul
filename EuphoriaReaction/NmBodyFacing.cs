using System;
using System.Reflection;
using UnityEngine;

namespace EuphoriaReaction;

/// <summary>
/// The bot's root keeps facing the way his body does while Euphoria has him: on his feet, the pelvis's
/// front; lying, the way his head points (the way prone and the get-ups face). Before, the root kept the
/// heading he had before the hit, so every hand-back that didn't set it turned the recovering body back
/// into the old heading. The bot AI's steering is pointed the same way, during the reaction and for the
/// hand-back blend after it, so it doesn't turn him back either.
/// </summary>
internal partial class NmBody
{
    private const float RootTurnRate = 240f; // deg/s the root follows the body at
    private Vector3 _heldLook;
    private float _holdLookUntil, _nextLook;

    /// Update while Euphoria drives him.
    private void FacingTick()
    {
        if (!_matching && !_rolling && !_dying)
        {
            Vector3 f;
            if (TrunkTilt() < 50f) f = DollFacing();
            else
            {
                Part(0, out Vector3 pel, out _);
                Part(12, out Vector3 head, out _);
                f = head - pel;
            }
            f.y = 0f;
            if (f.sqrMagnitude > 1e-4f)
            {
                Quaternion now = _p.Transform.rotation;
                Quaternion want = Quaternion.RotateTowards(now, Quaternion.LookRotation(f.normalized, Vector3.up), RootTurnRate * Time.deltaTime);
                if (Quaternion.Angle(now, want) > 0.05f) SetRootRotation(want);
                _heldLook = want * Vector3.forward;
            }
        }
        LookTick();
    }

    /// Update after the hand-back: keep the AI looking the way he was handed back while he blends in.
    private void LookHoldTick()
    {
        if (Time.time < _holdLookUntil || _getUp != null) LookTick(); // and through a whole get-up or roll clip
    }

    private void HoldLookFor(float seconds)
    {
        if (_p == null) return;
        _heldLook = _p.Transform.forward;
        _holdLookUntil = Time.time + seconds;
    }

    private void LookTick()
    {
        if (Time.time < _nextLook || _heldLook.sqrMagnitude < 1e-4f) return;
        _nextLook = Time.time + 0.1f;
        BotActions.LookTo(_p, _heldLook);
    }

    /// The root and the movement's look rotation (no mover sync: that's for hand-backs).
    private void SetRootRotation(Quaternion yaw)
    {
        try
        {
            _p.Transform.rotation = yaw;
            BotActions.SetMoveRotation(_p, yaw);
        }
        catch { }
    }
}
