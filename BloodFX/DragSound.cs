using System;
using Comfort.Common;
using UnityEngine;

namespace BloodFX;

/// <summary>
/// The quiet scrape of a body being dragged over the ground: cloth and gear rubbing along,
/// with the odd catch. There is no asset for it, so the loop is synthesised once at first use
/// (band-passed noise with a slow, uneven envelope and small grit bursts) and played from an
/// audio source that follows the body. Volume and pitch follow how fast the body is sliding,
/// and it fades out within a fraction of a second of it stopping.
/// </summary>
internal static class DragSound
{
    private const int Rate = 22050;
    private const float Seconds = 2.4f;

    private static GameObject _go;
    private static AudioSource _src;
    private static AudioClip _clip;
    private static float _vol;
    private static bool _failed, _routed;

    /// <param name="anchor">Where the body is, or null when nothing is being dragged.</param>
    /// <param name="slideSpeed">How fast the parts on the ground are sliding (m/s).</param>
    public static void Tick(Transform anchor, float slideSpeed, float dt)
    {
        BloodFXPlugin cfg = BloodFXPlugin.Instance;
        bool want = anchor != null && cfg != null && cfg.DragSound.Value && cfg.Enabled.Value;
        float target = want ? cfg.DragSoundVolume.Value * Mathf.Clamp01((slideSpeed - 0.04f) / 0.5f) : 0f;
        if (target <= 0f && _vol <= 0.001f)
        {
            if (_src != null && _src.isPlaying) _src.Stop();
            _vol = 0f;
            return;
        }
        if (!Ensure()) return;
        if (anchor != null) _go.transform.position = anchor.position;
        // quick to start as it begins to move, a little slower to die away
        float k = Mathf.Clamp01(dt * (target > _vol ? 12f : 6f));
        _vol = Mathf.Lerp(_vol, target, k);
        if (_vol < 0.001f)
        {
            _vol = 0f;
            if (_src.isPlaying) _src.Stop();
            return;
        }
        _src.volume = _vol;
        _src.pitch = 0.88f + 0.3f * Mathf.Clamp01(slideSpeed / 1.2f);
        if (!_src.isPlaying)
        {
            if (!_routed) Route();
            _src.time = UnityEngine.Random.value * (_clip.length - 0.1f);
            _src.Play();
        }
    }

    public static void Stop()
    {
        _vol = 0f;
        if (_src != null && _src.isPlaying) _src.Stop();
    }

    private static bool Ensure()
    {
        if (_src != null) return true;
        if (_failed) return false;
        try
        {
            _clip = MakeClip();
            _go = new GameObject("BloodFX_DragSound");
            UnityEngine.Object.DontDestroyOnLoad(_go);
            _src = _go.AddComponent<AudioSource>();
            _src.clip = _clip;
            _src.loop = true;
            _src.playOnAwake = false;
            _src.spatialBlend = 1f;
            _src.dopplerLevel = 0f;
            _src.rolloffMode = AudioRolloffMode.Logarithmic;
            _src.minDistance = 1.6f;
            _src.maxDistance = 25f;
            _src.priority = 140;
            _src.volume = 0f;
            Route();
            return true;
        }
        catch (Exception e)
        {
            _failed = true;
            BloodFXPlugin.Log.LogWarning($"BloodFX drag sound: could not set up ({e.Message}); dragging will be silent");
            return false;
        }
    }

    /// <summary>EFT plays through its own mixer; a source left off it may not be heard or follow the volume setting.</summary>
    private static void Route()
    {
        if (_routed) return;
        try
        {
            var group = Singleton<BetterAudio>.Instance?.VeryStandartMixerGroup;
            if (group != null)
            {
                _src.outputAudioMixerGroup = group;
                _routed = true;
            }
        }
        catch (Exception e) { BloodFXPlugin.Log.LogWarning($"BloodFX drag sound: not routed to the game mixer ({e.Message})"); }
    }

    /// <summary>
    /// A seamless loop of scraping: white noise band-passed to the cloth-on-floor range, its
    /// level wandering with a slow random envelope and a faint wobble (the uneven pull), plus
    /// sparse little bursts of grit where something catches. Peak kept well under full scale.
    /// </summary>
    private static AudioClip MakeClip()
    {
        int n = (int)(Rate * Seconds), fade = Rate / 8;
        var d = new float[n + fade];
        var rng = new System.Random(4242);
        float aHi = 1f - Mathf.Exp(-2f * Mathf.PI * 1500f / Rate);
        float aLo = 1f - Mathf.Exp(-2f * Mathf.PI * 220f / Rate);
        float lp1 = 0f, lp2 = 0f, low = 0f, env = 0.5f, envTarget = 0.5f, grit = 0f;
        int envTimer = 0;
        for (int i = 0; i < d.Length; i++)
        {
            float w = (float)rng.NextDouble() * 2f - 1f;
            lp1 += aHi * (w - lp1);
            lp2 += aHi * (lp1 - lp2);
            low += aLo * (lp2 - low);
            float band = lp2 - low;
            if (envTimer-- <= 0)
            {
                envTimer = rng.Next(Rate / 30, Rate / 9);
                envTarget = 0.25f + 0.75f * Mathf.Pow((float)rng.NextDouble(), 1.4f);
            }
            env += (envTarget - env) * 0.0035f;
            float t = i / (float)Rate;
            float wobble = 0.82f + 0.18f * Mathf.Sin(2f * Mathf.PI * 2.6f * t) * Mathf.Sin(2f * Mathf.PI * 0.37f * t + 1f);
            if (rng.NextDouble() < 0.00018) grit = 0.5f + 0.5f * (float)rng.NextDouble();
            grit *= 0.986f;
            d[i] = band * env * wobble + w * grit * 0.35f;
        }
        // cross the tail into the head so the loop has no seam, then drop the head
        var o = new float[n];
        for (int i = 0; i < n; i++)
        {
            int src = i + fade;
            float s = d[src];
            if (i >= n - fade)
            {
                float x = (i - (n - fade)) / (float)fade;
                s = s * (1f - x) + d[i - (n - fade)] * x;
            }
            o[i] = s;
        }
        float peak = 0f;
        for (int i = 0; i < n; i++) peak = Mathf.Max(peak, Mathf.Abs(o[i]));
        float g = peak > 1e-4f ? 0.65f / peak : 1f;
        for (int i = 0; i < n; i++) o[i] *= g;
        var clip = AudioClip.Create("BloodFX_DragScrape", n, 1, Rate, false);
        clip.SetData(o, 0);
        return clip;
    }
}
