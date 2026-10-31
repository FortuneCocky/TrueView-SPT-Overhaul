using System;
using System.Collections.Generic;
using System.IO;
using TrueView.Configuration;
using TrueView.Utilities;
using UnityEngine;

namespace TrueView.Systems;

/// <summary>
/// Rounds passing your head. EFT's FlyingBulletSoundPlayer notices every
/// round that passes within 10 m of the camera, once per round and not for
/// rounds that hit someone; the flyby patches hand each one here with how
/// close it came and which side it passed on. Your own rounds don't count.
///
///   whiz       - a random take from plugins/TrueView/audio/whiz_*.wav (never
///                the same twice in a row), panned to the side the round
///                passed, louder the closer it came. While the clips are
///                there they replace the game's own flyby sound; without
///                them the game's plays as before.
///   flinch     - a beat later (reaction time) the head camera ducks and
///                jerks away from the round, harder the closer it was.
///   adrenaline - a near miss feeds adrenaline like nearby gunfire does.
/// </summary>
internal static class FlybySystem
{
    private sealed class Take
    {
        public string Name;
        public float[] Data;
        public int Rate;
        public AudioClip Clip;
    }

    private static readonly List<Take> Takes = new();
    private static bool _loaded;
    private static GameObject _root;
    private static AudioSource[] _sources;
    private static int _nextSource, _lastTake = -1;

    private static float _pendingPan, _pendingAt = -1f;
    private static float _flinchAt = -1f, _flinchSide, _flinchCloseness;

    private static bool On => HandlingConfig.Enabled?.Value == true && HandlingConfig.FlybyEnabled?.Value == true;

    /// <summary>True when our whiz takes play instead of the game's flyby sound.</summary>
    public static bool ReplacesGameSound => On && EnsureLoaded() && (HandlingConfig.FlybyVolume?.Value ?? 0f) > 0f;

    /// <summary>The game is about to report a flyby on this side (-0.9 left .. 0.9 right).</summary>
    public static void Pending(float pan)
    {
        _pendingPan = pan;
        _pendingAt = Time.time;
    }

    /// <summary>
    /// The game reported the flyby (its vignette starts): closeness 0 at 10 m .. 1 past your ear.
    /// Only acts on a flyby announced by <see cref="Pending"/>, not the random ones the game
    /// makes up for sniper zones.
    /// </summary>
    public static void OnFlyby(float closeness)
    {
        if (_pendingAt < 0f || Time.time - _pendingAt > 0.25f)
            return;
        _pendingAt = -1f;
        if (!On)
            return;
        closeness = Mathf.Clamp01(closeness);
        float pan = _pendingPan;

        string take = PlayWhiz(closeness, pan);
        AdrenalineSystem.OnFlyby(closeness);
        float delay = 0f;
        if ((HandlingConfig.FlybyFlinchStrength?.Value ?? 0f) > 0f && (_flinchAt < 0f || closeness > _flinchCloseness))
        {
            delay = UnityEngine.Random.Range(0.06f, 0.12f);
            _flinchAt = Time.time + delay;
            _flinchSide = Mathf.Sign(pan) * Mathf.Max(0.35f, Mathf.Abs(pan));
            _flinchCloseness = closeness;
        }
        Log.Info($"Flyby: {(pan >= 0f ? "right" : "left")} side (pan {pan:0.00}), closeness {closeness:0.00}, whiz {take ?? "game's own"}, flinch {(delay > 0f ? $"in {delay * 1000f:0}ms" : "-")}, adrenaline {AdrenalineSystem.Level:0.00}");
    }

    public static void Tick()
    {
        if (_flinchAt < 0f || Time.time < _flinchAt)
            return;
        _flinchAt = -1f;
        BodyCamSystem.OnFlinch(_flinchSide, _flinchCloseness);
        // The gun is jerked along with the head, less.
        float s = (HandlingConfig.FlybyFlinchStrength?.Value ?? 1f) * Mathf.Lerp(0.15f, 1f, _flinchCloseness * _flinchCloseness);
        ProceduralHipSystem.AddImpulse(-_flinchSide * 0.5f * s, 0.3f * s);
    }

    private static string PlayWhiz(float closeness, float pan)
    {
        float volume = HandlingConfig.FlybyVolume?.Value ?? 0f;
        if (volume <= 0f || !EnsureLoaded() || !EnsureSources())
            return null;

        int i = UnityEngine.Random.Range(0, Takes.Count);
        if (Takes.Count > 1 && i == _lastTake) i = (i + 1 + UnityEngine.Random.Range(0, Takes.Count - 1)) % Takes.Count;
        _lastTake = i;
        Take t = Takes[i];
        // Unity drops clips made in code when the game reconfigures audio: rebuild from the samples.
        if (!BreathingSystem.ClipValid(t.Clip))
        {
            t.Clip = AudioClip.Create(t.Name, t.Data.Length, 1, t.Rate, false);
            t.Clip.SetData(t.Data, 0);
        }

        AudioSource s = _sources[_nextSource];
        _nextSource = (_nextSource + 1) % _sources.Length;
        s.Stop();
        s.clip = t.Clip;
        s.panStereo = pan;
        s.pitch = UnityEngine.Random.Range(0.92f, 1.08f);
        s.volume = Mathf.Clamp01(volume * Mathf.Lerp(0.3f, 1f, closeness));
        s.Play();
        return t.Name;
    }

    private static bool EnsureSources()
    {
        if (_root != null) return true;
        Camera cam = Camera.main;
        if (cam == null) return false;
        _root = new GameObject("TrueView_Flyby");
        _root.transform.SetParent(cam.transform, false);
        // A few voices so a burst of rounds overlaps instead of cutting itself off.
        _sources = new AudioSource[4];
        for (int i = 0; i < _sources.Length; i++)
        {
            var s = _root.AddComponent<AudioSource>();
            BreathingSystem.RouteToGameMixer(s);
            s.loop = false;
            s.spatialBlend = 0f;
            s.playOnAwake = false;
            s.priority = 32;
            _sources[i] = s;
        }
        return true;
    }

    private static bool EnsureLoaded()
    {
        if (_loaded) return Takes.Count > 0;
        _loaded = true;
        string dir = BreathingSystem.FindAudioDir();
        if (dir == null)
        {
            Log.Warning("Flyby: audio folder not found, the game's own flyby sound plays");
            return false;
        }
        foreach (string path in Directory.GetFiles(dir, "whiz_*.wav"))
        {
            try
            {
                float[] data = WavFile.ReadMono(path, out int rate);
                Takes.Add(new Take { Name = Path.GetFileNameWithoutExtension(path), Data = data, Rate = rate });
            }
            catch (Exception e) { Log.Warning($"Flyby: skipped {Path.GetFileName(path)}: {e.Message}"); }
        }
        if (Takes.Count > 0) Log.Info($"Flyby: {Takes.Count} whiz takes loaded from {dir}");
        else Log.Info($"Flyby: no whiz_*.wav in {dir}, the game's own flyby sound plays");
        return Takes.Count > 0;
    }

    public static void Reset()
    {
        _pendingAt = -1f;
        _flinchAt = -1f;
        if (_sources != null)
            foreach (var s in _sources)
                if (s != null) s.Stop();
    }
}
