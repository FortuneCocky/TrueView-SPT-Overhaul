using EFT;
using EFT.InventoryLogic;
using TrueView.Core;
using UnityEngine;

namespace TrueView.Systems;

/// <summary>
/// Diagnostic for the "character drifts off out of control" report (after
/// looting, with a gun in the secondary slot). Always on, quiet until
/// something looks wrong. Every line starts with "Drift probe:".
///
/// Logs:
///   - inventory open/close, with the three weapon slots and what's in hands
///   - every hands change (controller type and item)
///   - the hands stuck "processing" for over 1.5 s (TrueView widens the
///     deadzone to 45 degrees for as long as that lasts)
///   - the body moving with no movement key held, for over 0.5 s
///   - the view turning with no movement key held while the deadzone
///     transition or camera settle is active
/// Repeated lines are held to one per second.
/// </summary>
internal static class DriftProbe
{
    private const float Repeat = 1f;

    private static int _frame = -1;
    private static bool _inventory;
    private static string _hands = "";
    private static float _busySince = -1f, _busyLast;
    private static float _slideSince = -1f, _slideLast;
    private static float _turnLast;
    private static Vector2 _lastRot;
    private static bool _haveRot;

    public static void Tick(Player player, float dt)
    {
        if (player == null || !player.IsYourPlayer || _frame == Time.frameCount)
            return;
        _frame = Time.frameCount;
        float now = Time.unscaledTime;
        try
        {
            bool inv = player.IsInventoryOpened;
            if (inv != _inventory)
            {
                _inventory = inv;
                Utilities.Log.Info($"Drift probe: inventory {(inv ? "opened" : "closed")}; {Slots(player)}; {State(player)}");
            }

            string hands = Hands(player);
            if (hands != _hands)
            {
                Utilities.Log.Info($"Drift probe: hands {(_hands.Length == 0 ? "-" : _hands)} -> {hands}");
                _hands = hands;
            }

            bool busy = false;
            try { busy = player.HandsController?.IsHandsProcessing() ?? false; } catch { }
            if (!busy)
            {
                if (_busySince >= 0f && now - _busySince > 1.5f)
                    Utilities.Log.Info($"Drift probe: hands done after {now - _busySince:F1} s busy");
                _busySince = -1f;
            }
            else if (_busySince < 0f)
                _busySince = now;
            else if (now - _busySince > 1.5f && now - _busyLast > Repeat)
            {
                _busyLast = now;
                Utilities.Log.Warning($"Drift probe: hands busy for {now - _busySince:F1} s; {State(player)}");
            }

            Vector3 v = player.Velocity;
            float speed = new Vector2(v.x, v.z).magnitude;
            bool noKeys = player.InputDirection.sqrMagnitude < 0.0001f;
            if (noKeys && speed > 0.3f && !inv)
            {
                if (_slideSince < 0f)
                    _slideSince = now;
                else if (now - _slideSince > 0.5f && now - _slideLast > Repeat)
                {
                    _slideLast = now;
                    Utilities.Log.Warning($"Drift probe: moving {speed:F2} m/s with no movement key for {now - _slideSince:F1} s; {State(player)}");
                }
            }
            else
                _slideSince = -1f;

            Vector2 rot = player.Rotation;
            if (_haveRot && dt > 0f)
            {
                float yawRate = Mathf.DeltaAngle(_lastRot.x, rot.x) / dt;
                if (noKeys && Mathf.Abs(yawRate) > 15f && (ProceduralHipSystem.IsZoneTransitionActive || ProceduralHipSystem.CameraSettling)
                    && now - _turnLast > Repeat)
                {
                    _turnLast = now;
                    Utilities.Log.Info($"Drift probe: view turning {yawRate:F0} deg/s; {State(player)}");
                }
            }
            _lastRot = rot;
            _haveRot = true;
        }
        catch (System.Exception e)
        {
            if (now - _busyLast > 5f)
            {
                _busyLast = now;
                Utilities.Log.Warning("Drift probe failed: " + e.Message);
            }
        }
    }

    private static string Hands(Player player)
    {
        var hc = player.HandsController;
        if (hc == null)
            return "none";
        string item = hc.Item != null ? hc.Item.ShortName.Localized() : "no item";
        return $"{hc.GetType().Name} ({item})";
    }

    private static string Slots(Player player)
    {
        var eq = player.Inventory?.Equipment;
        if (eq == null)
            return "no equipment";
        return $"primary {Slot(eq, EquipmentSlot.FirstPrimaryWeapon)}, secondary {Slot(eq, EquipmentSlot.SecondPrimaryWeapon)}, holster {Slot(eq, EquipmentSlot.Holster)}";
    }

    private static string Slot(InventoryEquipment eq, EquipmentSlot slot)
    {
        Item item = eq.GetSlot(slot)?.ContainedItem;
        return item != null ? item.ShortName.Localized() : "empty";
    }

    private static string State(Player player)
    {
        var mc = player.MovementContext;
        Vector3 v = player.Velocity;
        bool busy = false;
        try { busy = player.HandsController?.IsHandsProcessing() ?? false; } catch { }
        return $"state {mc?.CurrentState?.Name}, pose {mc?.PoseLevel:F2}, grounded {mc?.IsGrounded}, keys {player.InputDirection}, " +
            $"velocity ({v.x:F2}, {v.y:F2}, {v.z:F2}), hands {_hands}, busy {busy}, free look {HandlingManager.InFreeLook(player)}, " +
            $"TrueView {(HandlingState.Enabled ? "on" : "off")}, deadzone {ProceduralHipSystem.ZoneDegrees:F1} deg " +
            $"(transition {ProceduralHipSystem.IsZoneTransitionActive}, gun off {ProceduralHipSystem.ControlAngle:F1} deg, settle {ProceduralHipSystem.CameraSettling})";
    }
}
