using System;

namespace EuphoriaReaction;

/// <summary>
/// The hand-back to the animation as a spring, not a slide: the animation takes the bones from the first
/// frame, and the gap to the pose he was handed back in (and the speed it was moving at) rides on top of
/// it and dies out like a critically damped spring. offset(t) = a * offset0 + b * velocity0, per axis.
/// No frozen frame and no straight-line morph: the motion carries on and settles into the stance.
/// (No Unity here: checked by tests/PhaseRulesTests.)
/// </summary>
internal static class HandBackSpring
{
    internal const float MinHalflife = 0.06f; // s: shorter snapped in visibly
    private const float PerBlend = 0.3f;      // halflife per second of the old blend time (it reached its middle 2x later)
    private const float Lengths = 5f;         // halflives until the offset is under 1 % and the animation has him alone

    /// The spring's halflife for a hand-back that used to blend over `blendDur` seconds.
    internal static float Halflife(float blendDur) => Math.Max(MinHalflife, PerBlend * blendDur);

    /// Seconds until the spring is done.
    internal static float Length(float halflife) => Lengths * halflife;

    /// The offset `t` s after the hand-back is a * offset0 + b * velocity0.
    internal static void Coeffs(float halflife, float t, out float a, out float b)
    {
        float y = 2f * 0.6931472f / Math.Max(halflife, 1e-4f);
        float e = (float)Math.Exp(-y * t);
        a = e * (1f + y * t);
        b = e * t;
    }
}
