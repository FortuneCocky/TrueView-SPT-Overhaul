using EFT;
using EFT.CameraControl;
using EFT.InventoryLogic;
using UnityEngine;

using TrueView.Configuration;

namespace TrueView.Systems;

/// <summary>
/// Drives EFT's built-in VisorEffect post-process for soft full-face covers
/// (balaclavas, gas masks) that have no armored FaceShieldComponent — the
/// visual Realism-style mask frame. Equipping slides the mask frame down
/// into view; removing slides it back up and releases the effect.
/// Armored face shields are left to the game's native visor logic so the
/// two never fight.
/// </summary>
internal static class FaceCoverOverlaySystem
{
    private const float PollInterval = 0.25f;

    // How far above the screen the frame starts, in UV units — slides to 0.
    private const float SlideDistance = 1.25f;

    private static float _pollTimer;
    private static bool _drivenByUs;
    private static bool _wantOverlay;
    private static float _anim;
    private static float _lastOffset;
    private static VisorEffect.EMask _appliedMask = VisorEffect.EMask.NoMask;

    public static void Tick(Player player, float deltaTime)
    {
        _pollTimer -= deltaTime;
        if (_pollTimer <= 0f)
        {
            _pollTimer = PollInterval;
            Evaluate(player);
        }
        Animate(deltaTime);
    }

    public static void Reset()
    {
        _pollTimer = 0f;
    }

    private static VisorEffect GetVisor()
    {
        try
        {
            return CameraManager.Exist ? CameraManager.Instance?.VisorEffect : null;
        }
        catch
        {
            return null;
        }
    }

    private static void Evaluate(Player player)
    {
        bool enabled = HandlingConfig.Enabled?.Value == true && HandlingConfig.LensFaceCoverEnabled?.Value == true;

        // An active armored face shield already drives VisorEffect natively —
        // yield completely so we never stomp its mask or visibility state.
        bool nativeVisor = false;
        try
        {
            FaceShieldComponent faceShield = player?.FaceShieldObserver?.Component;
            nativeVisor = faceShield != null && (faceShield.Togglable == null || faceShield.Togglable.On);
        }
        catch { }
        bool wantVisor = HandlingConfig.LensFaceCoverVisor?.Value == true;
        if (nativeVisor && !wantVisor)
        {
            // Release anything we enabled before yielding — otherwise our
            // mask stays on screen with nothing left to turn it off.
            _wantOverlay = false;
            _anim = 0f;
            if (_drivenByUs)
                Release(GetVisor());
            return;
        }

        if (nativeVisor && wantVisor)
        {
            // Helmet visor is down — drive the tighter goggle-style frame
            // over the native visor effect instead of yielding to it.
            _wantOverlay = enabled;
            if (_wantOverlay && (!_drivenByUs || _appliedMask != VisorEffect.EMask.Narrow))
            {
                VisorEffect visorForHelmet = GetVisor();
                if (visorForHelmet != null)
                {
                    visorForHelmet.SetMask(VisorEffect.EMask.Narrow);
                    visorForHelmet.Visible = true;
                    _appliedMask = VisorEffect.EMask.Narrow;
                    _drivenByUs = true;
                }
            }
            return;
        }

        bool fullCover = false;
        bool narrow = false;
        try
        {
            Item item = player?.Equipment?.GetSlot(EquipmentSlot.FaceCover)?.ContainedItem;
            if (item?.Template is FaceCoverTemplate template && !template.IsHalfMask)
            {
                fullCover = true;
                // Gas masks carry a filter resource — give them the tighter
                // goggle-style mask instead of the wide balaclava frame.
                narrow = item.GetItemComponent<ResourceComponent>() != null;
            }
        }
        catch { }

        _wantOverlay = enabled && fullCover;

        VisorEffect.EMask mask = narrow ? VisorEffect.EMask.Narrow : VisorEffect.EMask.Wide;
        if (_wantOverlay && (!_drivenByUs || mask != _appliedMask))
        {
            VisorEffect visor = GetVisor();
            if (visor != null)
            {
                visor.SetMask(mask);
                visor.Visible = true;
                _appliedMask = mask;
                _drivenByUs = true;
            }
        }
    }

    private static void Release(VisorEffect visor)
    {
        _drivenByUs = false;
        _appliedMask = VisorEffect.EMask.NoMask;
        if (visor == null)
            return;
        try
        {
            SetOffset(visor, 0f);
            visor.SetIntensity(0f);
            visor.SetMask(VisorEffect.EMask.NoMask);
            visor.Visible = false;
        }
        catch { }
    }

    private static void Animate(float deltaTime)
    {
        float speed = HandlingConfig.LensFaceCoverTransition?.Value ?? 2.2f;
        _anim = Mathf.MoveTowards(_anim, _wantOverlay ? 1f : 0f, deltaTime * speed);

        VisorEffect visor = GetVisor();
        if (visor == null)
        {
            return;
        }

        if (!_drivenByUs)
        {
            if (_lastOffset != 0f)
            {
                SetOffset(visor, 0f);
            }
            return;
        }

        // Smoothstep so the slide eases in and out instead of moving linearly.
        float eased = _anim * _anim * (3f - 2f * _anim);
        visor.SetIntensity((HandlingConfig.LensFaceCoverIntensity?.Value ?? 1f) * eased);
        SetOffset(visor, (1f - eased) * SlideDistance);

        if (!_wantOverlay && _anim <= 0f)
            Release(visor);
    }

    private static void SetOffset(VisorEffect visor, float offset)
    {
        if (Mathf.Approximately(_lastOffset, offset))
        {
            return;
        }
        try
        {
            visor.GetMaterial()?.SetTextureOffset("_Mask", new Vector2(0f, offset));
            _lastOffset = offset;
        }
        catch { }
    }
}
