using UnityEngine;

namespace BloodFX;

/// <summary>
/// Textures for marks that stick to the body: bullet wounds, the trickle that
/// runs down from them, and armor impact scuffs. Same convention as the decal
/// atlas: RGB grey shading, alpha coverage, tint from vertex colour (red for
/// flesh, grey for armor).
/// </summary>
internal static partial class BloodTextures
{
    public const int BodyCols = 4, BodyRows = 3;
    public const int WoundCell0 = 0, WoundCells = 4;  // row 0
    public const int RunCell0 = 4, RunCells = 4;      // row 1: travel = -v (top of the cell is the wound)
    public const int ArmorCell0 = 8, ArmorCells = 4;  // row 2

    /// <summary>UV rect of a body atlas cell.</summary>
    public static Rect BodyCell(int c)
    {
        float cw = 1f / BodyCols, ch = 1f / BodyRows;
        return new Rect((c % BodyCols) * cw, 1f - (c / BodyCols + 1) * ch, cw, ch);
    }

    public static Texture2D BodyAtlas()
    {
        const int cell = 192;
        int w = cell * BodyCols, h = cell * BodyRows;
        var px = new Color32[w * h];
        for (int c = 0; c < BodyCols * BodyRows; c++)
        {
            int ox = (c % BodyCols) * cell;
            int oy = (BodyRows - 1 - c / BodyCols) * cell;
            int seed = c * 4231 + 17;
            var rng = new System.Random(seed);
            var specks = new Vector3[10];
            for (int i = 0; i < specks.Length; i++)
            {
                float ang = (float)rng.NextDouble() * 6.283f, dist = 0.62f + 0.3f * (float)rng.NextDouble();
                specks[i] = new Vector3(Mathf.Cos(ang) * dist, Mathf.Sin(ang) * dist, 0.015f + 0.035f * Mathf.Pow((float)rng.NextDouble(), 2f));
            }
            var cracks = new Vector4[4 + c % 3];
            for (int i = 0; i < cracks.Length; i++)
                cracks[i] = new Vector4((float)rng.NextDouble() * 6.283f, 0.45f + 0.35f * (float)rng.NextDouble(),
                    (float)rng.NextDouble() * 6.283f, 8f + 10f * (float)rng.NextDouble());
            float phi = (float)rng.NextDouble() * 6.283f;
            float tongue = (float)(rng.NextDouble() - 0.5) * 0.5f;
            for (int y = 0; y < cell; y++)
            for (int x = 0; x < cell; x++)
            {
                float u = (x + 0.5f) / cell * 2f - 1f, v = (y + 0.5f) / cell * 2f - 1f;
                float g, a;
                if (c < RunCell0) Wound(u, v, seed, specks, tongue, out g, out a);
                else if (c < ArmorCell0) Run(u, v, seed, out g, out a);
                else Armor(u, v, seed, cracks, phi, out g, out a);
                // keep clear of the cell edge so mips don't bleed between cells
                float edge = Mathf.Max(Mathf.Abs(u), Mathf.Abs(v));
                if (c >= RunCell0 && c < ArmorCell0) edge = Mathf.Abs(u);
                a *= 1f - Smooth(0.9f, 0.97f, edge);
                px[(oy + y) * w + ox + x] = Px(g, a);
            }
        }
        return Finish(w, h, px, "BloodFX_Body");
    }

    /// <summary>Dark wet hole, ragged ring of torn flesh, short tongues of blood at the bottom, specks.</summary>
    private static void Wound(float u, float v, int seed, Vector3[] specks, float tongue, out float g, out float a)
    {
        float r = Mathf.Sqrt(u * u + v * v);
        float th = Mathf.Atan2(v, u);
        float cs = Mathf.Cos(th), sn = Mathf.Sin(th);
        float hole = 0.26f * (1f + 0.3f * (Fbm(cs * 2f + 1f, sn * 2f + 1f, seed) - 0.5f));
        float ring = 0.6f * (1f + 0.7f * (Fbm(cs * 1.5f + 5f, sn * 1.5f + 5f, seed + 1) - 0.5f));
        // blood starting to run out of the bottom of the wound
        float down = th + 1.5708f;
        ring += 0.3f * Mathf.Exp(-(down - tongue) * (down - tongue) / 0.03f) + 0.18f * Mathf.Exp(-(down + 0.5f) * (down + 0.5f) / 0.015f);
        float ragged = ring + 0.14f * (Fbm(u * 9f, v * 9f, seed + 2) - 0.5f);
        a = Smooth(-0.03f, 0.03f, ragged - r);
        if (r < hole)
        {
            // pooled blood in the hole: near black with a few wet glints in the lower half
            float glint = Smooth(0.78f, 0.86f, Fbm(u * 22f, v * 22f, seed + 3)) * Smooth(0.05f, -0.15f, v);
            g = 0.07f + 0.5f * glint;
            a = 1f;
            return;
        }
        float t = Mathf.Clamp01((r - hole) / Mathf.Max(0.05f, ring - hole));
        float meat = 0.55f + 0.45f * Fbm(u * 7f, v * 7f, seed + 4);
        // torn fibres radiating out of the hole, with a few wet glints on the raised rim
        float fibre = Fbm(cs * 6f + r * 1.5f, sn * 6f, seed + 6);
        meat *= 0.7f + 0.45f * fibre;
        g = meat * (0.55f + 0.45f * Smooth(0f, 0.18f, t)) * (1f - 0.3f * Smooth(0.65f, 1f, t));
        g += 0.35f * Smooth(0.8f, 0.88f, Fbm(u * 18f, v * 18f, seed + 7)) * (1f - Smooth(0.2f, 0.6f, t));
        // drying specks around the wound
        for (int i = 0; i < specks.Length; i++)
        {
            float dx = u - specks[i].x, dy = v - specks[i].y;
            float d = specks[i].z - Mathf.Sqrt(dx * dx + dy * dy);
            if (d > -0.01f)
            {
                float sa = Smooth(-0.01f, 0.01f, d);
                if (sa > a) { a = sa; g = 0.8f; }
            }
        }
    }

    /// <summary>A trickle running down from the wound, widest at the top, ending in a heavy bead.</summary>
    private static void Run(float u, float v, int seed, out float g, out float a)
    {
        float path = 0.07f * Mathf.Sin(v * 3f + seed) + 0.04f * (Fbm(v * 2f, 1f, seed) - 0.5f);
        float top = Smooth(-1f, 1f, v); // 0 at the bottom, 1 at the top
        float w = (0.08f + 0.12f * top) * (0.6f + 0.8f * Fbm(v * 5f, 3f, seed + 1));
        float f = w - Mathf.Abs(u - path);
        float bx = u - 0.07f * Mathf.Sin(-0.78f * 3f + seed), by = v + 0.78f;
        f = Mathf.Max(f, 0.17f - Mathf.Sqrt(bx * bx * 1.2f + by * by * 0.8f));
        if (v < -0.95f) f = -1f;
        a = Smooth(-0.02f, 0.02f, f);
        float depth = Mathf.Clamp01(f / 0.12f);
        g = Mathf.Lerp(0.95f, 0.55f, depth) * (0.85f + 0.15f * Fbm(u * 8f, v * 8f, seed + 2));
    }

    /// <summary>Armor stopped it: black punch mark, a pale crescent of crushed coating, radial cracks, grainy dust halo.</summary>
    private static void Armor(float u, float v, int seed, Vector4[] cracks, float phi, out float g, out float a)
    {
        float r = Mathf.Sqrt(u * u + v * v);
        float th = Mathf.Atan2(v, u);
        float grain = Fbm(u * 14f, v * 14f, seed) * 0.6f + Fbm(u * 40f, v * 40f, seed + 1) * 0.4f;
        float haloEdge = 0.92f + 0.3f * (Fbm(Mathf.Cos(th) * 2f + 4f, Mathf.Sin(th) * 2f + 4f, seed + 2) - 0.5f);
        a = (1f - Smooth(0.4f, haloEdge, r)) * Smooth(0.12f, 0.6f, grain) * 0.95f;
        g = 0.16f + 0.32f * grain;

        // pale crescent of crushed coating on one side
        float band = Smooth(0.19f, 0.24f, r) * (1f - Smooth(0.32f, 0.4f, r));
        float side = Mathf.Clamp01(0.35f + 0.9f * Mathf.Cos(th - phi));
        float ca = band * side * (0.8f + 0.2f * grain);
        g = Mathf.Lerp(g, 0.95f, ca);
        a = a + ca * (1f - a);

        // radial cracks
        for (int i = 0; i < cracks.Length; i++)
        {
            Vector4 k = cracks[i];
            if (r < 0.17f || r > k.y) continue;
            // jagged: straight runs with sharp kinks, not a smooth wiggle
            float kink = Mathf.Floor(r * 9f + k.z);
            float ang = k.x + 0.05f * (Fbm(kink, k.z, seed + 5) - 0.5f) * 4f / Mathf.Max(0.3f, r);
            float da = Mathf.Abs(Mathf.DeltaAngle(th * Mathf.Rad2Deg, ang * Mathf.Rad2Deg)) * Mathf.Deg2Rad * r;
            float width = 0.016f * (1f - (r - 0.17f) / (k.y - 0.17f)) + 0.003f;
            float la = Smooth(width, width * 0.4f, da);
            g = Mathf.Lerp(g, 0.06f, la);
            a = a + la * (1f - a);
        }

        // punch mark
        float hole = 0.16f * (1f + 0.25f * (Fbm(Mathf.Cos(th) * 2f, Mathf.Sin(th) * 2f, seed + 3) - 0.5f));
        float ha = Smooth(hole + 0.04f, hole - 0.01f, r);
        g = Mathf.Lerp(g, 0.03f, ha);
        a = a + ha * (1f - a);
    }
}
