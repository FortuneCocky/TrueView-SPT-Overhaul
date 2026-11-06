using EFT;
using HarmonyLib;
using TrueView.Utilities;
using UnityEngine;

namespace TrueView.Systems;

/// <summary>
/// Who gets heard when sounds would pile up. Highest first:
///
///   1. your own voice - pain grunts, the game's injured or gas-mask
///      breathing, voice lines (everything on the player's speech source).
///      Breathing and the hold-breath puffs go quiet under it; the
///      heartbeat drops to a murmur.
///   2. the hold-breath puffs - the breathing loops wait until a puff ends.
///   3. one breathing loop at a time (BreathingSystem's own states; the
///      calm-down nose breathing starts only once the heavy breathing has
///      faded).
///
/// Flyby whizzes play one at a time: a closer round cuts off a fainter whiz,
/// a fainter one is skipped while a louder one plays.
/// </summary>
internal static class AudioPriority
{
    private static readonly AccessTools.FieldRef<Player, BetterSource> SpeechSource =
        AccessTools.FieldRefAccess<Player, BetterSource>("_speechSource");

    private static float _voiceUntil;
    private static bool _voiceLogged;

    /// <summary>1 = nothing in the way, 0 = your voice is playing (fades both ways).</summary>
    public static float VoiceDuck { get; private set; } = 1f;

    public static void Tick(Player player, float dt)
    {
        bool voice = false;
        try
        {
            BetterSource s = player != null ? SpeechSource(player) : null;
            voice = s != null && s.source1 != null && s.source1.isPlaying;
        }
        catch { }
        // Held a moment past the end, so breathing doesn't squeeze in between grunts.
        if (voice) _voiceUntil = Time.time + 0.35f;
        bool ducked = Time.time < _voiceUntil;
        float target = ducked ? 0f : 1f;
        VoiceDuck = Mathf.MoveTowards(VoiceDuck, target, dt / (target < VoiceDuck ? 0.08f : 0.6f));

        if (ducked != _voiceLogged)
        {
            _voiceLogged = ducked;
            Log.Info(ducked ? "Audio: your voice is playing, breathing steps back" : "Audio: voice done, breathing back");
        }
    }

    public static void Reset()
    {
        _voiceUntil = 0f;
        _voiceLogged = false;
        VoiceDuck = 1f;
    }
}
