using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Comfort.Common;
using UnityEngine;
using UnityEngine.Networking;

namespace BloodFX;

/// <summary>
/// The wet slap of a boot coming down in a pool of blood. The sound comes from
/// sounds/blood_steps.mp3 next to the DLL: a recording of several steps in puddles, one
/// after the other. It is decoded by Unity at startup and cut into one clip per splash
/// wherever the waveform jumps out of the quiet between steps; each bloody step then plays
/// a random one of those clips, a little different in pitch and level each time, from a
/// small pool of spatial sources at the foot: loud up close, a murmur across the room.
/// Until the file has loaded (or if it is missing) a synthesised splash stands in.
/// </summary>
internal static class StepSplash
{
    private const int Rate = 22050, Sources = 6, MaxClips = 24;
    private static readonly List<AudioClip> _clips = new List<AudioClip>();
    private static AudioClip[] _fallback;
    private static AudioSource[] _src;
    private static GameObject _go;
    private static int _nextSrc;
    private static bool _failed, _routed, _loading, _loaded;

    public static int ClipCount => _clips.Count;

    /// <summary>Starts decoding the recording in the background; safe to call more than once.</summary>
    public static void Load(MonoBehaviour host)
    {
        if (_loading || _loaded || host == null) return;
        _loading = true;
        host.StartCoroutine(LoadRoutine());
    }

    public static void Play(Vector3 at, float wet)
    {
        BloodFXPlugin cfg = BloodFXPlugin.Instance;
        if (cfg == null || cfg.FootprintSplashVolume.Value <= 0f || !Ensure()) return;
        AudioClip clip;
        if (_clips.Count > 0) clip = _clips[UnityEngine.Random.Range(0, _clips.Count)];
        else clip = _fallback[UnityEngine.Random.Range(0, _fallback.Length)];
        AudioSource s = null;
        for (int i = 0; i < Sources; i++)
        {
            AudioSource c = _src[(_nextSrc + i) % Sources];
            if (!c.isPlaying) { s = c; break; }
        }
        if (s == null) s = _src[_nextSrc];
        _nextSrc = (_nextSrc + 1) % Sources;
        if (!_routed) Route();
        s.transform.position = at;
        s.volume = cfg.FootprintSplashVolume.Value * Mathf.Lerp(0.6f, 1f, wet) * UnityEngine.Random.Range(0.8f, 1f);
        s.pitch = UnityEngine.Random.Range(0.92f, 1.08f);
        s.clip = clip;
        s.Play();
    }

    private static IEnumerator LoadRoutine()
    {
        string path = null;
        try
        {
            string dir = Path.GetDirectoryName(typeof(BloodFXPlugin).Assembly.Location);
            path = Path.Combine(dir ?? "", "sounds", "blood_steps.mp3");
        }
        catch (Exception e) { BloodFXPlugin.Log.LogWarning($"BloodFX step splash: {e.Message}"); }
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            BloodFXPlugin.Log.LogWarning($"BloodFX step splash: sounds/blood_steps.mp3 not found next to the DLL, using the synthesised splash");
            _loaded = true;
            yield break;
        }
        string url = new Uri(path).AbsoluteUri;
        using (UnityWebRequest req = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.MPEG))
        {
            ((DownloadHandlerAudioClip)req.downloadHandler).streamAudio = false;
            yield return req.SendWebRequest();
            AudioClip whole = null;
            try
            {
                if (req.result != UnityWebRequest.Result.Success)
                    BloodFXPlugin.Log.LogWarning($"BloodFX step splash: could not decode blood_steps.mp3 ({req.error}), using the synthesised splash");
                else
                    whole = DownloadHandlerAudioClip.GetContent(req);
            }
            catch (Exception e) { BloodFXPlugin.Log.LogWarning($"BloodFX step splash: decode failed ({e.Message})"); }
            if (whole != null)
            {
                // the clip may still be loading its samples
                float wait = 0f;
                while (whole.loadState == AudioDataLoadState.Loading && wait < 10f) { wait += Time.unscaledDeltaTime; yield return null; }
                try { Split(whole); }
                catch (Exception e) { BloodFXPlugin.Log.LogWarning($"BloodFX step splash: could not cut the recording ({e.Message})"); }
                UnityEngine.Object.Destroy(whole);
            }
        }
        _loaded = true;
    }

    /// <summary>
    /// Cuts the recording into one clip per splash. The loudness envelope (10 ms windows) is
    /// followed; a splash starts where it jumps well above the quiet before it, and ends where
    /// it has died back down, at the next splash, or after a second and a half at most.
    /// </summary>
    private static void Split(AudioClip whole)
    {
        int ch = whole.channels, freq = whole.frequency, n = whole.samples;
        if (n <= 0 || freq <= 0) return;
        var data = new float[n * ch];
        whole.GetData(data, 0);
        // mono mix
        var mono = new float[n];
        for (int i = 0; i < n; i++)
        {
            float s = 0f;
            for (int c = 0; c < ch; c++) s += data[i * ch + c];
            mono[i] = s / ch;
        }
        int win = freq / 100, hop = win / 2; // 10 ms windows, 5 ms apart
        int frames = Mathf.Max(1, (n - win) / hop);
        var env = new float[frames];
        float peak = 1e-6f;
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            int b = f * hop;
            for (int i = 0; i < win; i++) sum += mono[b + i] * mono[b + i];
            env[f] = Mathf.Sqrt((float)(sum / win));
            if (env[f] > peak) peak = env[f];
        }
        float on = peak * 0.12f, off = peak * 0.035f;
        int lookBack = 150 / 5, minGap = 250 / 5, quietNeeded = 120 / 5, maxLen = 1500 / 5;
        var starts = new List<int>();
        var ends = new List<int>();
        int last = -minGap;
        for (int f = lookBack; f < frames; f++)
        {
            if (f - last < minGap || env[f] < on) continue;
            float floor = float.MaxValue;
            for (int k = f - lookBack; k < f - 2; k++) if (env[k] < floor) floor = env[k];
            if (env[f] < floor * 2.5f && floor > off) continue; // just a louder stretch of an ongoing sound
            // run to the end of this splash
            int quiet = 0, e = f;
            for (; e < frames && e - f < maxLen; e++)
            {
                if (env[e] < off) { if (++quiet >= quietNeeded) break; }
                else quiet = 0;
            }
            if ((e - f) * hop < freq / 8) { last = f; continue; } // under 125 ms: a click, not a step
            starts.Add(f);
            ends.Add(Mathf.Min(e, frames - 1));
            last = f;
            f = e;
        }
        for (int k = 0; k < starts.Count && _clips.Count < MaxClips; k++)
        {
            int s0 = Mathf.Max(0, starts[k] * hop - freq / 100);
            int s1 = Mathf.Min(n, ends[k] * hop + win);
            if (k + 1 < starts.Count) s1 = Mathf.Min(s1, starts[k + 1] * hop - freq / 200);
            int len = s1 - s0;
            if (len < freq / 8) continue;
            var d = new float[len];
            Array.Copy(mono, s0, d, 0, len);
            int fin = Mathf.Min(len / 4, freq / 200), fout = Mathf.Min(len / 3, freq * 3 / 100);
            float p = 1e-6f;
            for (int i = 0; i < len; i++)
            {
                if (i < fin) d[i] *= i / (float)fin;
                if (i >= len - fout) d[i] *= (len - i) / (float)fout;
                p = Mathf.Max(p, Mathf.Abs(d[i]));
            }
            float g = 0.7f / p;
            for (int i = 0; i < len; i++) d[i] *= g;
            var clip = AudioClip.Create("BloodFX_Splash" + k, len, 1, freq, false);
            clip.SetData(d, 0);
            _clips.Add(clip);
        }
        if (_clips.Count == 0)
        {
            BloodFXPlugin.Log.LogWarning($"BloodFX step splash: no splashes found in blood_steps.mp3 ({n / (float)freq:0.0} s), using the synthesised splash");
            return;
        }
        float shortest = float.MaxValue, longest = 0f;
        foreach (AudioClip c in _clips) { shortest = Mathf.Min(shortest, c.length); longest = Mathf.Max(longest, c.length); }
        BloodFXPlugin.Log.LogInfo($"BloodFX step splash: blood_steps.mp3 ({n / (float)freq:0.0} s, {freq} Hz) cut into {_clips.Count} splashes of {shortest:0.00}-{longest:0.00} s");
    }

    private static bool Ensure()
    {
        if (_src != null) return true;
        if (_failed) return false;
        try
        {
            _fallback = new AudioClip[3];
            for (int i = 0; i < _fallback.Length; i++) _fallback[i] = MakeClip(1000 + i * 7);
            _go = new GameObject("BloodFX_StepSplash");
            UnityEngine.Object.DontDestroyOnLoad(_go);
            _src = new AudioSource[Sources];
            for (int i = 0; i < Sources; i++)
            {
                var child = new GameObject("splash" + i);
                child.transform.SetParent(_go.transform, false);
                AudioSource s = child.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.loop = false;
                s.spatialBlend = 1f;
                s.dopplerLevel = 0f;
                s.rolloffMode = AudioRolloffMode.Logarithmic;
                s.minDistance = 1.5f;
                s.maxDistance = 22f;
                s.priority = 120;
                _src[i] = s;
            }
            Route();
            return true;
        }
        catch (Exception e)
        {
            _failed = true;
            BloodFXPlugin.Log.LogWarning($"BloodFX step splash: could not set up ({e.Message}); stepping in blood will be silent");
            return false;
        }
    }

    private static void Route()
    {
        try
        {
            var group = Singleton<BetterAudio>.Instance?.VeryStandartMixerGroup;
            if (group == null) return;
            for (int i = 0; i < Sources; i++) _src[i].outputAudioMixerGroup = group;
            _routed = true;
        }
        catch (Exception e) { BloodFXPlugin.Log.LogWarning($"BloodFX step splash: not routed to the game mixer ({e.Message})"); }
    }

    /// <summary>The stand-in: a short slap of noise, a soft thud and a few drops plipping back down.</summary>
    private static AudioClip MakeClip(int seed)
    {
        const float seconds = 0.32f;
        int n = (int)(Rate * seconds);
        var d = new float[n];
        var rng = new System.Random(seed);
        int plips = rng.Next(4, 8);
        var plipT = new float[plips];
        var plipF = new float[plips];
        for (int k = 0; k < plips; k++)
        {
            plipT[k] = 0.03f + 0.2f * (float)rng.NextDouble();
            plipF[k] = 1400f + 1300f * (float)rng.NextDouble();
        }
        float lp = 0f, lp2 = 0f, low = 0f;
        float aLow = 1f - Mathf.Exp(-2f * Mathf.PI * 160f / Rate);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float w = (float)rng.NextDouble() * 2f - 1f;
            float cutoff = Mathf.Lerp(3200f, 450f, Mathf.Clamp01(t / 0.11f));
            float a = 1f - Mathf.Exp(-2f * Mathf.PI * cutoff / Rate);
            lp += a * (w - lp);
            lp2 += a * (lp - lp2);
            low += aLow * (lp2 - low);
            float env = t < 0.004f ? t / 0.004f : Mathf.Exp(-(t - 0.004f) / 0.065f);
            float s = (lp2 - low) * env * 1.6f;
            s += Mathf.Sin(2f * Mathf.PI * 85f * t) * Mathf.Exp(-t / 0.035f) * 0.45f;
            for (int k = 0; k < plips; k++)
            {
                float dt = t - plipT[k];
                if (dt < 0f || dt > 0.05f) continue;
                float f = plipF[k] * (1f - 0.4f * dt / 0.03f);
                s += Mathf.Sin(2f * Mathf.PI * f * dt) * Mathf.Exp(-dt / 0.011f) * 0.22f;
            }
            if (t > seconds - 0.04f) s *= (seconds - t) / 0.04f;
            d[i] = s;
        }
        float peak = 0f;
        for (int i = 0; i < n; i++) peak = Mathf.Max(peak, Mathf.Abs(d[i]));
        float g = peak > 1e-4f ? 0.8f / peak : 1f;
        for (int i = 0; i < n; i++) d[i] *= g;
        var clip = AudioClip.Create("BloodFX_StepSplashSynth" + seed, n, 1, Rate, false);
        clip.SetData(d, 0);
        return clip;
    }
}
