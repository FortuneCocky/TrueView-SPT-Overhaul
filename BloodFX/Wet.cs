using UnityEngine;

namespace BloodFX;

/// <summary>
/// The one wet-surface colour rule all the volumetric blood shares: the body colour plus a
/// highlight that stays a dark, saturated red (never pink or white) and dims with the scene
/// light already baked into the colour, so blood in a dark spot matches the dark floor stains.
/// </summary>
internal static class Wet
{
    // red of the liquid at full scene light (BloodManager.StreamBase)
    private const float FullLitRed = 0.34f;

    public static Color Shade(Color c, float body, float highlight)
    {
        float w = highlight * Mathf.Clamp01(c.r / FullLitRed);
        return new Color(c.r * body + w * 0.42f, c.g * body + w * 0.1f, c.b * body + w * 0.09f, 1f);
    }
}
