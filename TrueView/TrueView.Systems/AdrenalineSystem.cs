using System;
using EFT;
using TrueView.Configuration;
using TrueView.Core;
using TrueView.Utilities;
using UnityEngine;

namespace TrueView.Systems;

/// <summary>
/// Adrenaline dump. Being shot at (nearby enemy gunfire, rounds passing
/// close) raises an adrenaline level that decays over several seconds; a hit
/// taken spikes it to full and starts a separate hit level. While elevated
/// it feeds a slight hands tremor (read by SwaySystem), a tunnel-vision FOV
/// pinch (read by DynamicFovSystem), and a synthesized low-frequency
/// heartbeat. No audio assets needed — the lub-dub clip is generated
/// procedurally at first use.
///
/// Shot at: a soft, slow heartbeat (BreathingSystem plays quiet nose
/// breathing). Hit: a loud, fast heartbeat with heavy breathing, easing
/// through the calm-down into slower, softer beats until it stops.
/// </summary>
internal static class AdrenalineSystem
{
    private const float HeartSampleRate = 22050f;

    private static float _level;
    private static float _hit;
    private static float _nextBeat;
    private static AudioClip _heartClip;
    private static AudioSource _heartSource;

    /// <summary>0..1 — how wound up the player currently is.</summary>
    public static float Level => _level;

    /// <summary>0..1 — the part of it that comes from being hit (1 on a hit, drains like the level).</summary>
    public static float HitLevel => _hit;

    /// <summary>FOV multiplier contribution (tunnel vision under adrenaline).</summary>
    public static float FovFactor =>
        1f - _level * (HandlingConfig.AdrenalineFovPinch?.Value ?? 0f) * 0.01f;

    /// <summary>Extra hands-tremor amplitude contribution for SwaySystem.</summary>
    public static float TremorAdd =>
        _level * (HandlingConfig.AdrenalineTremor?.Value ?? 0f);

    /// <summary>Called for every shooter in the raid — spikes if near/self.</summary>
    public static void OnShot(Player shooter, Player localPlayer)
    {
        if (HandlingConfig.AdrenalineEnabled?.Value != true ||
            HandlingConfig.Enabled?.Value != true || localPlayer == null)
            return;

        float spike;
        if (shooter == localPlayer)
        {
            // Own shots alone don't trigger adrenaline — the player firing at
            // nothing isn't under stress. Once combat is already active
            // (level > 0 from being shot at or hit), each shot keeps it fed.
            if (_level <= 0f) return;
            spike = 0.1f;
        }
        else
        {
            float dist;
            try { dist = Vector3.Distance(shooter.Position, localPlayer.Position); }
            catch { return; }
            float radius = HandlingConfig.AdrenalineRadius?.Value ?? 40f;
            if (dist > radius) return;
            spike = Mathf.Lerp(0.45f, 0.12f, dist / radius);
        }
        _level = Mathf.Clamp01(_level + spike);
    }

    /// <summary>A round passed close by: 0 = at the edge of earshot (10 m), 1 = past your ear.</summary>
    public static void OnFlyby(float closeness)
    {
        if (HandlingConfig.AdrenalineEnabled?.Value == true)
            _level = Mathf.Clamp01(_level + Mathf.Lerp(0.08f, 0.3f, closeness));
    }

    /// <summary>Called when the local player takes a hit — full spike.</summary>
    public static void OnHit()
    {
        if (HandlingConfig.AdrenalineEnabled?.Value == true)
        {
            if (_hit < 0.5f)
                Log.Info($"Adrenaline: hit taken, spiked to full (was {_level:0.00})");
            _level = 1f;
            _hit = 1f;
        }
    }

    public static void Tick(Player player, float dt)
    {
        if (HandlingConfig.AdrenalineEnabled?.Value != true || HandlingConfig.Enabled?.Value != true)
        {
            if (_level > 0f) Reset();
            return;
        }

        float decay = Mathf.Max(1f, HandlingConfig.AdrenalineDecay?.Value ?? 8f);
        _level = Mathf.Max(0f, _level - dt / decay);
        if (_level < 0.005f) _level = 0f;
        _hit = Mathf.Min(_level, Mathf.Max(0f, _hit - dt / decay));

        UpdateHeartbeat(dt);
    }

    private static void UpdateHeartbeat(float dt)
    {
        // Shot at: soft. Hit: full. After a hit the calm-down nose breathing
        // keeps a faint beat going that fades out with it.
        float tail = BreathingSystem.CalmTail;
        float strength = Mathf.Max(Mathf.Max(_level * 0.45f, _hit), tail * 0.3f);
        float volume = (HandlingConfig.AdrenalineHeartbeat?.Value ?? 0f) * strength;
        if (volume < 0.02f)
        {
            if (_heartSource != null && _heartSource.isPlaying)
                _heartSource.Stop();
            _nextBeat = 0f;
            return;
        }

        if (_heartSource == null)
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            // Always our own source: its pitch changes per beat, which must
            // never touch a source the game put on the camera.
            _heartSource = cam.gameObject.AddComponent<AudioSource>();
            BreathingSystem.RouteToGameMixer(_heartSource);
            _heartSource.spatialBlend = 0f;
            _heartSource.loop = false;
            _heartSource.playOnAwake = false;
            _heartSource.ignoreListenerPause = true;
        }
        // Rebuilt if Unity dropped its data on an audio reconfigure.
        if (!BreathingSystem.ClipValid(_heartClip))
            _heartClip = BuildHeartbeatClip();

        _nextBeat -= dt;
        if (_nextBeat <= 0f && !_heartSource.isPlaying)
        {
            _heartSource.clip = _heartClip;
            // A heavy exhale covers the heart a little. A harder-pumping
            // heart lands a touch higher; no two beats quite alike.
            _heartSource.volume = Mathf.Clamp01(volume * 1.6f * BreathingSystem.HeartMask * UnityEngine.Random.Range(0.9f, 1f));
            _heartSource.pitch = Mathf.Lerp(0.95f, 1.05f, strength) * UnityEngine.Random.Range(0.98f, 1.02f);
            _heartSource.Play();
            // Shot at: up to ~95 bpm. Hit: up to ~140 bpm. The calm-down
            // slows it toward ~58 bpm as it fades. A touch quicker on each
            // inhale (as a real heart does).
            float bpm = Mathf.Max(Mathf.Max(Mathf.Lerp(60f, 95f, _level), Mathf.Lerp(60f, 140f, _hit)),
                Mathf.Lerp(58f, 72f, tail)) * BreathingSystem.HeartRateMod;
            _nextBeat = 60f / bpm;
        }
    }

    /// <summary>
    /// Synthesizes a single "lub-dub" heard from inside the chest: each sound
    /// is a low thump whose pitch drops as it lands, a soft muffled knock on
    /// top, and a deep body rumble under it. A gentle saturation adds the
    /// overtones that let headphones and small speakers carry a ~50 Hz beat,
    /// and a low-pass takes the edge off, so it reads as felt rather than heard.
    /// </summary>
    private static AudioClip BuildHeartbeatClip()
    {
        int len = (int)(HeartSampleRate * 0.75f);
        var samples = new float[len];
        var rng = new System.Random(7);
        Thump(samples, rng, 0.00f, 72f, 40f, 0.20f, 0.055f, 1f);    // lub: deeper, longer
        Thump(samples, rng, 0.17f, 88f, 52f, 0.14f, 0.035f, 0.72f); // dub: shorter, a touch higher

        // Saturate, low-pass at ~350 Hz (the chest wall), then normalize.
        float rc = 1f / (2f * Mathf.PI * 350f);
        float alpha = (1f / HeartSampleRate) / (rc + 1f / HeartSampleRate);
        float lp = 0f, peak = 1e-6f;
        for (int i = 0; i < len; i++)
        {
            float x = (float)Math.Tanh(samples[i] * 1.8f);
            lp += alpha * (x - lp);
            samples[i] = lp;
            peak = Mathf.Max(peak, Mathf.Abs(lp));
        }
        for (int i = 0; i < len; i++)
            samples[i] *= 0.95f / peak;

        var clip = AudioClip.Create("adrenaline_heartbeat", len, 1, (int)HeartSampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    private static void Thump(float[] samples, System.Random rng, float startSec, float freqStart, float freqEnd,
        float duration, float decay, float amp)
    {
        int start = (int)(startSec * HeartSampleRate);
        int count = (int)(duration * HeartSampleRate);
        float phase = 0f, noise = 0f;
        for (int i = 0; i < count && start + i < samples.Length; i++)
        {
            float t = i / HeartSampleRate;
            float freq = Mathf.Lerp(freqStart, freqEnd, Mathf.Sqrt(t / duration));
            phase += 2f * Mathf.PI * freq / HeartSampleRate;
            // 4 ms attack, then an exponential fall; tapered to zero at the end.
            float env = Mathf.Min(1f, t / 0.004f) * Mathf.Exp(-t / decay) * (1f - t / duration);
            float body = Mathf.Sin(phase) + 0.35f * Mathf.Sin(2f * phase);
            // Muffled knock: smoothed noise for the first few milliseconds.
            noise += 0.08f * ((float)rng.NextDouble() * 2f - 1f - noise);
            float knock = noise * 3f * Mathf.Exp(-t / 0.006f);
            float rumble = 0.5f * Mathf.Sin(2f * Mathf.PI * 32f * t) * Mathf.Exp(-t / 0.12f);
            samples[start + i] += (body * env + knock + rumble * (1f - t / duration)) * amp;
        }
    }

    public static void Reset()
    {
        _level = 0f;
        _hit = 0f;
        _nextBeat = 0f;
        if (_heartSource != null && _heartSource.isPlaying)
            _heartSource.Stop();
    }
}
