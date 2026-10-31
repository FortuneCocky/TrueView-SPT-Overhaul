using System;
using System.IO;
using EFT;
using TrueView.Configuration;
using TrueView.Core;
using TrueView.Utilities;
using UnityEngine;

namespace TrueView.Systems;

/// <summary>
/// Your own breathing, from three recorded loops that fade in and out with
/// stamina and adrenaline. At rest with full stamina nothing plays.
///
///   running   - only while sprinting; gets louder as stamina runs out.
///   deep fast - stamina under 50% (louder the emptier you are), and under
///               adrenaline, where it follows the level with a slow recovery.
///   nose      - stamina climbing back from 50%: full at 50%, gone at 85%;
///               and the adrenaline calm-down (below).
///
/// Adrenaline calm-down: every trigger starts an episode that adds up how
/// much adrenaline went through you. When the heavy breathing has settled
/// it hands over (a slow crossfade) to nose breathing whose length, loudness
/// and pace come from that total, so a distant shot gets a short tail and a
/// long fight a long one. The tail slows and fades as it runs, waits while
/// adrenaline is still up and stretches while stamina is low. A new trigger
/// during it cuts straight back to heavy breathing.
///
/// Holding breath silences all of it. Each loop is stopped outright once it
/// has faded to zero, and a watchdog on the audio object fades everything out
/// if the handling tick stops (menus, inventory, death, raid end), so no
/// loop can be left running.
///
/// Also owns the breath cycle for the camera: BodyCamSystem reads
/// <see cref="Phase"/> and <see cref="Depth"/>; AdrenalineSystem reads
/// <see cref="HeartRateMod"/> and <see cref="HeartMask"/>.
///
/// Clips: BepInEx/plugins/TrueView/audio/breath_*.wav (16-bit PCM).
/// </summary>
internal static class BreathingSystem
{
    private sealed class Layer
    {
        public string File;
        public float TrimStart, TrimEnd; // seconds of the recording used for the loop
        public float Gain;               // relative loudness after normalizing
        public float FadeIn, FadeOut;    // seconds for a full fade
        public float[] Data;             // decoded, trimmed, normalized loop (mono)
        public int Rate;
        public AudioClip Clip;
        public AudioSource Source;
        public float RetryAt;
        public float Volume;             // current, 0..1 before master and gain
        public float PitchBase = 1f;     // random per take
        public float Pitch = 1f;         // live multiplier (the calm-down slows the nose loop)
    }

    private static readonly Layer Running = new() { File = "breath_running.wav", TrimStart = 0.5f, TrimEnd = 23.5f, Gain = 0.9f, FadeIn = 0.6f, FadeOut = 1.2f };
    private static readonly Layer DeepFast = new() { File = "breath_deep_fast.wav", TrimStart = 0.25f, TrimEnd = 7.5f, Gain = 0.8f, FadeIn = 1.2f, FadeOut = 2.5f };
    private static readonly Layer Nose = new() { File = "breath_nose.wav", TrimStart = 0.5f, TrimEnd = 25f, Gain = 0.45f, FadeIn = 1.5f, FadeOut = 2f };
    private static readonly Layer[] Layers = { Running, DeepFast, Nose };

    private static bool _loaded, _loadFailed, _warnedNoCamera;
    private static GameObject _root;
    private static float _lastTick;

    private static float _phase;
    private static float _adrenaline; // smoothed: fast attack, slow recovery
    private static float _exertion;
    private static float _lastStamina = -1f;
    private static float _staminaRate;
    private static float _recoveringUntil;

    // Adrenaline episode: trigger -> heavy -> calm-down -> normal.
    private static float _lastLevel;
    private static float _kick;          // short heavy-breath burst from a fresh trigger
    private static float _load, _peak;   // adrenaline-seconds and peak level this episode
    private static bool _heavy;
    private static float _heavySince;
    private static float _calmLeft, _calmTotal, _calmVolume, _calmIntensity, _calmSince;

    private enum State { None, Run, Deep, CalmNose, Nose }
    private static State _state, _pending;
    private static float _pendingSince, _stateSince;

    /// <summary>0..1 how big the episode was: ~0 for a distant shot, ~0.5 for a hit, ~1 for a long fight.</summary>
    private static float Intensity => 1f - Mathf.Exp(-_load / 6f);

    /// <summary>Breath cycle in radians, 0..2pi: inhale first half, exhale second.</summary>
    public static float Phase => _phase;

    /// <summary>0..1 how hard the body is breathing (drives the visual depth).</summary>
    public static float Depth { get; private set; }

    /// <summary>Heart rate multiplier: the heart quickens slightly while inhaling.</summary>
    public static float HeartRateMod => 1f + 0.05f * Mathf.Max(0f, Mathf.Sin(_phase));

    /// <summary>Heartbeat volume multiplier: heavy breathing covers the heart a little.</summary>
    public static float HeartMask => 1f - 0.25f * Mathf.Max(DeepFast.Volume, Running.Volume);

    public static void Tick(Player player, float dt)
    {
        _lastTick = Time.unscaledTime;
        bool on = HandlingConfig.Enabled?.Value == true;
        float master = on ? HandlingConfig.BreathingVolume?.Value ?? 0f : 0f;

        float stamina = HandlingState.Stamina01;
        bool sprinting = HandlingState.IsSprinting;
        bool alive = player != null && player.HealthController != null && player.HealthController.IsAlive;

        // Is stamina climbing back? Smoothed rate, held for a moment so
        // short pauses in the regen do not cut the nose breathing.
        if (_lastStamina < 0f) _lastStamina = stamina;
        float raw = dt > 1e-4f ? (stamina - _lastStamina) / dt : 0f;
        _lastStamina = stamina;
        _staminaRate = Mathf.Lerp(_staminaRate, raw, 1f - Mathf.Exp(-2f * dt));
        if (_staminaRate > 0.002f && !sprinting) _recoveringUntil = Time.time + 1.5f;
        bool recovering = Time.time < _recoveringUntil && !sprinting;

        // Exertion for the camera breathing: quick rise, ~20 s recovery.
        float fatigue = 1f - Mathf.Min(stamina, HandlingState.Oxygen01);
        float exRate = fatigue > _exertion ? 1.2f : 0.06f;
        _exertion = Mathf.Lerp(_exertion, fatigue, 1f - Mathf.Exp(-exRate * 8f * dt));
        float exertionGain = Mathf.Clamp01(_exertion * (HandlingConfig.BodyCamExertion?.Value ?? 1f));

        // Adrenaline: follow spikes at once, recover with a lag on top of
        // the adrenaline drain so you hear yourself settle down after it.
        // The bigger the episode so far, the slower the heavy breathing settles.
        float level = AdrenalineSystem.Level;
        float settle = 3f / Mathf.Max(1f, HandlingConfig.AdrenalineDecay?.Value ?? 8f) / Mathf.Lerp(1f, 1.8f, Intensity);
        float adrRate = level > _adrenaline ? 12f : settle;
        _adrenaline = Mathf.Lerp(_adrenaline, level, 1f - Mathf.Exp(-adrRate * dt));
        float adr = Mathf.Pow(Mathf.Clamp01((_adrenaline - 0.08f) / 0.92f), 1.2f);

        // --- Adrenaline episode: trigger -> heavy -> calm-down -> normal ---
        bool calmOn = HandlingConfig.CalmDownEnabled?.Value != false;
        float jump = level - _lastLevel;
        _lastLevel = level;
        if (alive && jump > 0.04f)
        {
            // A fresh trigger: catch your breath, even after a distant shot.
            _kick = Mathf.Max(_kick, Mathf.Clamp01(0.35f + 1.5f * jump));
            if (_calmTotal > 0f)
            {
                Log.Info($"Breathing: calm-down cut short after {Time.time - _calmSince:0.0}s of {_calmTotal:0.0}s by a new trigger (adrenaline {level:0.00}), back to heavy");
                _calmLeft = _calmTotal = 0f;
            }
        }
        _kick = Mathf.Max(0f, _kick - dt / 4f);
        if (level > 0f) { _load += level * dt; _peak = Mathf.Max(_peak, level); }

        // Heavy breathing hands over to the calm-down once it has settled;
        // during the calm-down it takes a fresh trigger or a real rise in
        // adrenaline to bring it back.
        float need = _heavy ? (calmOn ? 0.2f : 0.05f) : _calmTotal > 0f ? 0.3f : 0.05f;
        bool heavy = alive && (_kick > 0.1f || adr > need);
        if (heavy && !_heavy) _heavySince = Time.time;
        if (!heavy && _heavy && calmOn && alive && _load > 0f) StartCalm(fatigue);
        _heavy = heavy;

        float calm = 0f; // calm-down left, 1 at the handover .. 0 at the end
        if (_calmTotal > 0f)
        {
            if (!calmOn || !alive) _calmLeft = _calmTotal = 0f;
            else
            {
                // Runs as the adrenaline drains (waits while it is still up),
                // slower while out of breath, paused while sprinting.
                float drain = Mathf.Clamp01((0.25f - adr) / 0.2f) * Mathf.Lerp(1f, 0.5f, fatigue);
                if (!sprinting) _calmLeft = Mathf.Max(0f, _calmLeft - dt * drain);
                if (_calmLeft <= 0f && adr < 0.05f)
                {
                    Log.Info($"Breathing: calm-down done after {Time.time - _calmSince:0.0}s (planned {_calmTotal:0.0}s), breathing back to normal");
                    _calmLeft = _calmTotal = 0f;
                }
                else calm = Mathf.Max(0.01f, _calmLeft / _calmTotal);
            }
        }
        if (!_heavy && _calmTotal <= 0f && adr < 0.05f) _load = _peak = 0f;
        float calmPace = calm * Mathf.Lerp(0.5f, 1f, _calmIntensity);

        float rate = Mathf.Max(Mathf.Max(Mathf.Lerp(0.2f, 0.52f, exertionGain), Mathf.Lerp(0.2f, 0.62f, _adrenaline)), Mathf.Lerp(0.2f, 0.42f, calmPace));
        Depth = Mathf.Max(exertionGain, Mathf.Max(_adrenaline * 0.85f, calmPace * 0.4f));
        if (!HandlingState.IsHoldingBreath)
        {
            _phase += Mathf.PI * 2f * rate * dt;
            if (_phase >= Mathf.PI * 2f) _phase -= Mathf.PI * 2f;
        }

        // --- One breathing state at a time, by priority ---
        //   hold breath > sprinting (running) > adrenaline or stamina < 50%
        //   (deep fast) > adrenaline calm-down (nose) > recovering 50..85%
        //   (nose) > silent.
        // The others fade out, so a switch is a short crossfade, never two
        // breathing tracks on top of each other; only the handover from heavy
        // to the calm-down overlaps for a couple of seconds.
        float runVol = Mathf.Max(0.2f + 0.8f * Mathf.SmoothStep(0f, 1f, (0.6f - stamina) / 0.55f), adr);
        float low = stamina < 0.5f ? Mathf.SmoothStep(0f, 1f, (0.5f - stamina) / 0.5f) : 0f;
        float deepVol = Mathf.Max(low, Mathf.Max(adr, _kick));
        float noseVol = 1f - Mathf.SmoothStep(0f, 1f, (stamina - 0.5f) / 0.35f);
        float calmVol = _calmVolume * Mathf.Pow(calm, 0.8f);

        State want = State.None;
        if (!alive || HandlingState.IsHoldingBreath) want = State.None;
        else if (sprinting) want = State.Run;
        // Hysteresis around 50% so deep fast and nose do not flip back and forth.
        else if (heavy || stamina < (_state == State.Deep ? 0.5f : 0.45f)) want = State.Deep;
        else if (calm > 0f) want = State.CalmNose;
        else if (recovering && stamina < 0.85f) want = State.Nose;

        // A new state has to hold for a moment before it takes over (sprint
        // taps, stamina jitter); holding breath, death and a fresh trigger
        // during the calm-down cut in at once.
        if (want != _pending) { _pending = want; _pendingSince = Time.time; }
        bool instant = want == State.None && (!alive || HandlingState.IsHoldingBreath)
            || want == State.Deep && _state == State.CalmNose && _kick > 0.1f;
        if (want != _state && (instant || Time.time - _pendingSince >= 0.4f))
        {
            Log.Info($"Breathing: {_state} -> {want} after {Time.time - _stateSince:0.0}s (stamina {stamina:0.00}, adrenaline {_adrenaline:0.00}, calm-down left {_calmLeft:0.0}s, sprint {sprinting}, recovering {recovering}, volume {master:0.00})");
            _state = want;
            _stateSince = Time.time;
        }

        float run = _state == State.Run ? runVol : 0f;
        float deep = _state == State.Deep ? deepVol : 0f;
        float nose = _state == State.Nose ? noseVol
            : _state == State.CalmNose ? Mathf.Max(calmVol, recovering && stamina < 0.85f ? noseVol : 0f) : 0f;
        // The calm-down nose loop plays a little quick at first and slows as you settle.
        float pitch = _state == State.CalmNose ? Mathf.Lerp(0.95f, 1.06f, calmPace) : 1f;
        Nose.Pitch = Mathf.MoveTowards(Nose.Pitch, pitch, dt * 0.1f);

        if (master <= 0f || !alive)
        {
            if (AnyPlaying()) StopAll();
            return;
        }
        if (!EnsureLoaded()) return;

        bool hold = HandlingState.IsHoldingBreath;
        Drive(Running, run, master, dt, hold);
        bool handover = _state == State.CalmNose;
        Drive(DeepFast, deep, master, dt, hold, handover);
        Drive(Nose, nose, master, dt, hold, handover);
    }

    private static void StartCalm(float fatigue)
    {
        float len = HandlingConfig.CalmDownLength?.Value ?? 20f;
        _calmIntensity = Intensity;
        _calmTotal = _calmLeft = len * (0.15f + 0.85f * _calmIntensity) * (1f + 0.5f * fatigue);
        _calmVolume = (HandlingConfig.CalmDownVolume?.Value ?? 0.6f) * Mathf.Lerp(0.55f, 1f, _calmIntensity);
        _calmSince = Time.time;
        Log.Info($"Breathing: heavy for {Time.time - _heavySince:0.0}s, handing over to calm-down nose breathing for ~{_calmTotal:0.0}s (adrenaline load {_load:0.0}, peak {_peak:0.00}, intensity {_calmIntensity:0.00}, fatigue {fatigue:0.00}, nose volume {_calmVolume:0.00})");
    }

    private static void Drive(Layer l, float target, float master, float dt, bool fast, bool handover = false)
    {
        // A layer that lost the state fades out quickly so the crossfade is
        // short, except at the handover from heavy breathing to the calm-down,
        // where both fade slowly into each other.
        float time = fast ? 0.15f
            : handover && (target <= 0f || target > l.Volume) ? 5f
            : target > l.Volume ? l.FadeIn : target <= 0f ? 0.7f : l.FadeOut;
        l.Volume = Mathf.MoveTowards(l.Volume, Mathf.Clamp01(target), dt / Mathf.Max(0.05f, time));

        if (l.Volume <= 0.001f)
        {
            l.Volume = 0f;
            if (l.Source != null && l.Source.isPlaying) l.Source.Stop();
            return;
        }
        if (l.Source == null && !EnsureSources()) return;
        if (!l.Source.isPlaying && Time.unscaledTime >= l.RetryAt)
        {
            // Unity drops the data of clips made in code when the game
            // reconfigures audio (EFT does on raid start): rebuild from the
            // decoded samples kept in memory.
            if (!ClipValid(l.Clip))
            {
                l.Clip = MakeClip(l);
                l.Source.clip = l.Clip;
            }
            // Start somewhere random in the loop so it never sounds like the same take.
            l.Source.time = UnityEngine.Random.Range(0f, l.Clip.length * 0.9f);
            l.PitchBase = UnityEngine.Random.Range(0.97f, 1.03f);
            l.Source.pitch = l.PitchBase * l.Pitch;
            l.Source.volume = Mathf.Clamp01(master * l.Gain * l.Volume);
            l.Source.Play();
            if (l.Source.isPlaying)
            {
                Log.Verbose($"Breathing: playing {l.Clip.name} ({l.Clip.length:0.0}s) at volume {l.Source.volume:0.00}, mixer {(l.Source.outputAudioMixerGroup != null ? l.Source.outputAudioMixerGroup.name : "none")}");
            }
            else
            {
                // Never retry every frame: a source that will not start waits a second.
                l.RetryAt = Time.unscaledTime + 1f;
                Log.Warning($"Breathing: {l.File} would not start (clip {l.Clip.samples} samples, {l.Clip.frequency} Hz, load state {l.Clip.loadState})");
                l.Clip = null;
            }
        }
        l.Source.volume = Mathf.Clamp01(master * l.Gain * l.Volume);
        l.Source.pitch = l.PitchBase * l.Pitch;
    }

    private static bool AnyPlaying()
    {
        foreach (var l in Layers)
            if (l.Source != null && l.Source.isPlaying) return true;
        return false;
    }

    private static void StopAll()
    {
        foreach (var l in Layers)
        {
            l.Volume = 0f;
            if (l.Source != null && l.Source.isPlaying) l.Source.Stop();
        }
    }

    private static bool EnsureSources()
    {
        if (_root != null) return true;
        Camera cam = Camera.main;
        if (cam == null)
        {
            if (!_warnedNoCamera) Log.Warning("Breathing: no main camera yet, waiting");
            _warnedNoCamera = true;
            return false;
        }
        _root = new GameObject("TrueView_Breathing");
        _root.transform.SetParent(cam.transform, false);
        _root.AddComponent<Watchdog>();
        foreach (var l in Layers)
        {
            var s = _root.AddComponent<AudioSource>();
            RouteToGameMixer(s);
            s.clip = l.Clip;
            s.loop = true;
            s.spatialBlend = 0f;
            s.playOnAwake = false;
            s.priority = 64;
            s.volume = 0f;
            l.Source = s;
        }
        return true;
    }

    /// <summary>
    /// EFT plays its sounds through its own audio mixer; a source left off it
    /// may not be heard. Use the group the game uses for ordinary sounds, so
    /// the game's volume setting applies too.
    /// </summary>
    internal static void RouteToGameMixer(AudioSource s)
    {
        try
        {
            var group = Comfort.Common.Singleton<BetterAudio>.Instance?.VeryStandartMixerGroup;
            if (group != null) s.outputAudioMixerGroup = group;
            else Log.Warning("Breathing: game audio mixer not found, playing unrouted");
        }
        catch (Exception e) { Log.Warning($"Breathing: could not route to game mixer: {e.Message}"); }
    }

    /// Fades everything out when the handling tick stops being called.
    private sealed class Watchdog : MonoBehaviour
    {
        private void Update()
        {
            if (Time.unscaledTime - _lastTick < 0.5f) return;
            foreach (var l in Layers)
            {
                if (l.Source == null || !l.Source.isPlaying) continue;
                l.Volume = Mathf.MoveTowards(l.Volume, 0f, Time.unscaledDeltaTime / 0.5f);
                l.Source.volume = Mathf.Clamp01((HandlingConfig.BreathingVolume?.Value ?? 0f) * l.Gain * l.Volume);
                if (l.Volume <= 0f) l.Source.Stop();
            }
        }

        private void OnDisable() => StopAll();
    }

    // --- Loading ---

    private static bool EnsureLoaded()
    {
        if (_loaded) return true;
        if (_loadFailed) return false;
        try
        {
            string dir = FindAudioDir();
            if (dir == null) throw new FileNotFoundException("breathing audio folder not found (expected plugins/TrueView/audio)");
            foreach (var l in Layers)
            {
                l.Data = LoadLoop(Path.Combine(dir, l.File), l.TrimStart, l.TrimEnd, out l.Rate);
                l.Clip = MakeClip(l);
            }
            _loaded = true;
            Log.Info($"Breathing audio loaded from {dir}");
            return true;
        }
        catch (Exception e)
        {
            _loadFailed = true;
            Log.Warning($"Breathing audio disabled: {e.Message}");
            return false;
        }
    }

    internal static string FindAudioDir()
    {
        string dll = Path.GetDirectoryName(typeof(BreathingSystem).Assembly.Location) ?? "";
        foreach (string d in new[] { Path.Combine(dll, "TrueView", "audio"), Path.Combine(dll, "audio") })
            if (File.Exists(Path.Combine(d, Running.File))) return d;
        return null;
    }

    /// <summary>
    /// Reads a 16-bit PCM WAV, keeps [start, end) seconds, mixes to mono,
    /// normalizes loudness (the recordings differ a lot) and crossfades the
    /// tail into the head so the loop has no seam or click.
    /// </summary>
    internal static bool ClipValid(AudioClip c) => c != null && c.samples > 0 && c.length > 0f;

    /// <summary>
    /// Makes the playable clip from the cached samples. If Unity hands back an
    /// empty clip, falls back to a streamed clip that reads the same samples.
    /// </summary>
    private static AudioClip MakeClip(Layer l)
    {
        string name = Path.GetFileNameWithoutExtension(l.File);
        var clip = AudioClip.Create(name, l.Data.Length, 1, l.Rate, false);
        clip.SetData(l.Data, 0);
        if (ClipValid(clip)) return clip;

        Log.Warning($"Breathing: {name} came back empty ({clip.samples} samples), using a streamed clip");
        float[] data = l.Data;
        int pos = 0;
        clip = AudioClip.Create(name, data.Length, 1, l.Rate, true,
            buf =>
            {
                for (int i = 0; i < buf.Length; i++)
                {
                    buf[i] = data[pos];
                    if (++pos >= data.Length) pos = 0;
                }
            },
            p => pos = Mathf.Clamp(p, 0, data.Length - 1));
        return clip;
    }

    private static float[] LoadLoop(string path, float start, float end, out int sampleRate)
    {
        float[] all = WavFile.ReadMono(path, out int rate);
        int s0 = Mathf.Clamp((int)(start * rate), 0, all.Length);
        int s1 = Mathf.Clamp((int)(end * rate), s0, all.Length);
        int n = s1 - s0;
        int fade = Mathf.Min((int)(0.4f * rate), n / 4);
        int len = n - fade;
        if (len < rate / 2) throw new InvalidDataException($"{Path.GetFileName(path)} is too short");

        var mono = new float[n];
        Array.Copy(all, s0, mono, 0, n);

        // Loop: the last `fade` samples are blended into the first ones.
        var data = new float[len];
        Array.Copy(mono, data, len);
        for (int i = 0; i < fade; i++)
        {
            float t = (float)i / fade;
            data[i] = mono[i] * Mathf.Sqrt(t) + mono[len + i] * Mathf.Sqrt(1f - t);
        }

        double sq = 0; float peak = 1e-6f;
        for (int i = 0; i < len; i++) { sq += data[i] * data[i]; peak = Mathf.Max(peak, Mathf.Abs(data[i])); }
        float rms = (float)Math.Sqrt(sq / len);
        float gain = Mathf.Min(0.12f / Mathf.Max(rms, 1e-6f), 0.95f / peak);
        for (int i = 0; i < len; i++) data[i] *= gain;

        sampleRate = rate;
        return data;
    }

    public static void Reset()
    {
        _adrenaline = 0f;
        _exertion = 0f;
        _lastStamina = -1f;
        _staminaRate = 0f;
        _recoveringUntil = 0f;
        _lastLevel = _kick = _load = _peak = 0f;
        _heavy = false;
        _calmLeft = _calmTotal = 0f;
        Nose.Pitch = 1f;
        _state = _pending = State.None;
        Depth = 0f;
        StopAll();
    }
}
