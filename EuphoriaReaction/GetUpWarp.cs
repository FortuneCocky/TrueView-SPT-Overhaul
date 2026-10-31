using System;

namespace EuphoriaReaction;

/// <summary>
/// A get-up into a crouch: every clip ends standing, and handing that back into a crouch made him stand
/// all the way up and then sink. Over the clip's rise (from 60 % of the way to its standing frame) its
/// body and legs bend into the crouch the animation plays instead, so he comes up into the crouch. Stand
/// and prone targets keep the clip as it is. (No Unity here: checked by tests/PhaseRulesTests.)
/// </summary>
internal static class GetUpWarp
{
    private const float FromShare = 0.6f; // of the way to the standing frame: still down on a knee before it

    internal static bool Applies(StartPose to) => to == StartPose.Crouch || to == StartPose.DeepCrouch;

    /// How far into the crouch at clip frame `frame` (0 = the clip's own pose, 1 = the crouch).
    internal static float Weight(float frame, float standFrame, float last)
    {
        float s = Math.Min(standFrame, last), from = s * FromShare;
        if (s - from < 1f) return frame >= s ? 1f : 0f;
        float x = Math.Max(0f, Math.Min(1f, (frame - from) / (s - from)));
        return x * x * (3f - 2f * x);
    }
}
