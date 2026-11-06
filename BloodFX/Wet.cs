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

    /// <summary>Dark blood for vertices nothing has shaded yet, so an unset colour can never show as something else.</summary>
    public static readonly Color32 Unset = new Color32(40, 2, 2, 255);

    public static void Fill(Color32[] c)
    {
        for (int i = 0; i < c.Length; i++) c[i] = Unset;
    }

    /// <summary>
    /// Debug check: blood is always red-dominant, so a vertex colour with more green or blue
    /// than red means a mesh drew something it never shaded. Logs the first one per mesh.
    /// </summary>
    public static void Audit(Color32[] c, int count, string who, ref bool reported)
    {
        if (reported) return;
        int n = Mathf.Min(count, c.Length);
        for (int i = 0; i < n; i++)
        {
            Color32 k = c[i];
            if (k.b > k.r + 16 || k.g > k.r + 16)
            {
                reported = true;
                BloodFXPlugin.Log.LogWarning($"BloodFX: off-colour vertex in {who}: index {i}, rgba ({k.r},{k.g},{k.b},{k.a})");
                return;
            }
        }
    }
}
