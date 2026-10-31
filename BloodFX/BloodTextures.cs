using UnityEngine;

namespace BloodFX;

/// <summary>
/// All blood textures, generated once at load from noise and distance fields.
/// RGB is grey wet shading (1 = full tint), alpha is coverage; the red comes
/// from vertex colour so one texture serves every light level.
/// </summary>
internal static partial class BloodTextures
{
    public const int AtlasCols = 4, AtlasRows = 4;
    public const int SplatCells = 4;   // cells 0..3: impact splats (top row)
    public const int PoolCell0 = 4;    // cells 4,5: pools
    public const int DripCell0 = 6;    // cells 6,7: small drip drops
    public const int StreakCell0 = 8;  // cells 8..11: directional streaks, travel = +u
    public const int StreakCells = 4;
    public const int SpatterCell0 = 12; // cells 12..15: spray spatter (fine specks + a trail of big drops), travel = +u
    public const int SpatterCells = 4;

    public static Texture2D Droplet()
    {
        const int n = 32;
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
            float r = Mathf.Sqrt(u * u + v * v);
            float a = 1f - Smooth(0.55f, 1f, r);
            // offset bright core sells a wet bead
            float hx = u + 0.3f, hy = v - 0.3f;
            float shine = Mathf.Exp(-(hx * hx + hy * hy) * 14f);
            float g = 0.7f + 0.3f * shine;
            px[y * n + x] = Px(g, a);
        }
        return Finish(n, n, px, "BloodFX_Droplet");
    }

    public static Texture2D Mist()
    {
        const int n = 64;
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
            float r = Mathf.Sqrt(u * u + v * v);
            float fall = 1f - Smooth(0.15f, 1f, r);
            float noise = Fbm(u * 2.5f + 7.1f, v * 2.5f + 3.3f, 11);
            float a = fall * fall * Mathf.Clamp01(0.35f + 0.9f * noise);
            px[y * n + x] = Px(0.9f, a);
        }
        return Finish(n, n, px, "BloodFX_Mist");
    }

    public static Texture2D DecalAtlas()
    {
        const int cell = 192;
        int w = cell * AtlasCols, h = cell * AtlasRows;
        var px = new Color32[w * h];
        var field = new float[cell * cell];
        for (int c = 0; c < AtlasCols * AtlasRows; c++)
        {
            if (c >= SpatterCell0)
            {
                StampSpatter(c, field, cell);
            }
            else
            {
                var shape = new Shape(c);
                for (int y = 0; y < cell; y++)
                for (int x = 0; x < cell; x++)
                    field[y * cell + x] = shape.Field((x + 0.5f) / cell * 2f - 1f, (y + 0.5f) / cell * 2f - 1f);
            }
            int ox = (c % AtlasCols) * cell;
            int oy = (AtlasRows - 1 - c / AtlasCols) * cell; // cell 0 at top-left
            for (int y = 0; y < cell; y++)
            for (int x = 0; x < cell; x++)
            {
                float u = (x + 0.5f) / cell * 2f - 1f, v = (y + 0.5f) / cell * 2f - 1f;
                float f = field[y * cell + x];
                float a = Smooth(-0.015f, 0.015f, f);
                // thin blood reads bright red, thick blood goes almost black in the middle
                float depth = Mathf.Clamp01(f / 0.25f);
                float g = Mathf.Lerp(1f, 0.38f, Mathf.Sqrt(depth)) * (0.85f + 0.15f * Fbm(u * 6f, v * 6f, c * 31 + 5));
                px[(oy + y) * w + ox + x] = Px(g, a);
            }
        }
        return Finish(w, h, px, "BloodFX_Decals");
    }

    /// <summary>UV rect of an atlas cell.</summary>
    public static Rect Cell(int c)
    {
        float cw = 1f / AtlasCols, ch = 1f / AtlasRows;
        return new Rect((c % AtlasCols) * cw, 1f - (c / AtlasCols + 1) * ch, cw, ch);
    }

    /// <summary>Signed coverage field for one atlas cell: &gt;0 inside.</summary>
    private struct Shape
    {
        private readonly int _seed, _kind; // 0 splat, 1 pool, 2 drip, 3 streak
        private readonly float _p1, _p2, _base;
        private readonly Vector4[] _blobs; // x,y centre, z radius, w stretch along ray
        private readonly Vector4[] _strand;

        public Shape(int cell)
        {
            _seed = cell * 7919 + 13;
            _kind = cell < SplatCells ? 0 : cell < DripCell0 ? 1 : cell < StreakCell0 ? 2 : 3;
            var rng = new System.Random(_seed);
            _p1 = (float)rng.NextDouble() * 6.28f;
            _p2 = (float)rng.NextDouble() * 6.28f;
            int count;
            switch (_kind)
            {
                case 0: _base = 0.36f; count = 18; break;
                case 1: _base = 0.44f; count = 16; break;
                case 2: _base = 0.24f; count = 5; break;
                default: _base = 0f; count = 5 + cell % 3; break;
            }
            if (_kind == 3)
            {
                // strands: x start, x end, y offset, start width / slope, wiggle amp, wiggle freq, phase
                _blobs = new Vector4[count];
                _strand = new Vector4[count];
                for (int i = 0; i < count; i++)
                {
                    float main = i == 0 ? 1f : 0f;
                    _blobs[i] = new Vector4(-0.88f + 0.25f * (float)rng.NextDouble() * (1f - main),
                        0.35f + 0.55f * (float)rng.NextDouble(),
                        (float)(rng.NextDouble() - 0.5) * 0.25f * (1f - main),
                        main > 0f ? 0.075f : 0.015f + 0.035f * (float)rng.NextDouble());
                    _strand[i] = new Vector4((float)(rng.NextDouble() - 0.5) * 0.8f * (1f - main * 0.7f),
                        0.02f + 0.05f * (float)rng.NextDouble(), 4f + 6f * (float)rng.NextDouble(), (float)rng.NextDouble() * 6.28f);
                }
                return;
            }
            _strand = null;
            _blobs = new Vector4[count];
            for (int i = 0; i < count; i++)
            {
                float ang = (float)rng.NextDouble() * 6.28f;
                float dist, rad;
                if (_kind == 0) { dist = 0.45f + 0.45f * (float)rng.NextDouble(); rad = 0.02f + 0.05f * (float)rng.NextDouble(); }
                else if (_kind == 1) { dist = 0.55f + 0.33f * (float)rng.NextDouble(); rad = 0.02f + 0.045f * (float)rng.NextDouble(); }
                else { dist = 0.35f + 0.4f * (float)rng.NextDouble(); rad = 0.04f + 0.05f * (float)rng.NextDouble(); }
                _blobs[i] = new Vector4(Mathf.Cos(ang) * dist, Mathf.Sin(ang) * dist, rad, _kind <= 1 ? 1.4f + 1.6f * (float)rng.NextDouble() : 1f);
            }
        }

        public float Field(float u, float v)
        {
            if (_kind == 3) return StreakField(u, v);
            float r = Mathf.Sqrt(u * u + v * v);
            float th = Mathf.Atan2(v, u);
            float wob = _kind == 1 ? 0.1f : 0.05f;
            float R = _base * (1f + wob * Mathf.Sin(3f * th + _p1) + wob * 0.7f * Mathf.Sin(5f * th + _p2)
                + (_kind == 1 ? 0.4f : 0.22f) * (Fbm(Mathf.Cos(th) * 1.7f + 3f, Mathf.Sin(th) * 1.7f + 3f, _seed) - 0.5f)
                + (_kind == 1 ? 0.12f * (Fbm(Mathf.Cos(th) * 5f + 9f, Mathf.Sin(th) * 5f + 9f, _seed + 3) - 0.5f) : 0f));
            float f = R - r;
            for (int i = 0; i < _blobs.Length; i++)
            {
                Vector4 b = _blobs[i];
                float dx = u - b.x, dy = v - b.y;
                // ellipse stretched along the ray from the centre: flung droplets
                float len = Mathf.Sqrt(b.x * b.x + b.y * b.y) + 1e-5f;
                float ax = b.x / len, ay = b.y / len;
                float along = (dx * ax + dy * ay) / b.w, across = -dx * ay + dy * ax;
                f = Mathf.Max(f, b.z - Mathf.Sqrt(along * along + across * across));
                if (_kind <= 1)
                {
                    // tapered finger joining the body to a flung drop (splash spikes)
                    float t = Mathf.Clamp01((u * ax + v * ay - _base * 0.6f) / (len - _base * 0.6f));
                    float sx = u - ax * Mathf.Lerp(_base * 0.6f, len, t), sy = v - ay * Mathf.Lerp(_base * 0.6f, len, t);
                    float width = _kind == 1
                        ? Mathf.Lerp(0.06f, 0.006f, Mathf.Sqrt(t)) * (i % 2 == 0 ? 1f : 0f)
                        : Mathf.Lerp(0.035f, 0.008f, t) * (i % 3 == 0 ? 1f : 0f);
                    f = Mathf.Max(f, width - Mathf.Sqrt(sx * sx + sy * sy));
                }
            }
            // keep shapes off the cell border so mips don't bleed
            return Mathf.Min(f, 0.93f - r);
        }

        /// <summary>Blood that hit the ground moving: a heavy head where it landed, strands dragged forward, flung drops beyond.</summary>
        private float StreakField(float u, float v)
        {
            float f = -1f;
            for (int i = 0; i < _blobs.Length; i++)
            {
                Vector4 a = _blobs[i], b = _strand[i];
                float x0 = a.x, x1 = a.y;
                float path = a.z + b.x * (u - x0) + b.y * Mathf.Sin(b.z * u + b.w);
                float pathAt0 = a.z + b.y * Mathf.Sin(b.z * x0 + b.w);
                float hx = u - (x0 + 0.06f), hy = v - pathAt0;
                f = Mathf.Max(f, a.w * 1.5f - Mathf.Sqrt(hx * hx * 0.6f + hy * hy));
                if (u >= x0 && u <= x1)
                {
                    float t = (u - x0) / (x1 - x0);
                    float w = a.w * Mathf.Pow(1f - t, 0.7f) * (0.55f + 0.9f * Fbm(u * 7f, i * 3.7f, _seed + i));
                    f = Mathf.Max(f, w - Mathf.Abs(v - path));
                }
                for (int d = 1; d <= 2; d++)
                {
                    float dx = x1 + 0.05f * d + 0.03f * i, dy = a.z + b.x * (dx - x0) + b.y * Mathf.Sin(b.z * dx + b.w);
                    float ex = (u - dx) * 0.7f, ey = v - dy;
                    f = Mathf.Max(f, a.w * 0.45f / d - Mathf.Sqrt(ex * ex + ey * ey));
                }
            }
            return Mathf.Min(Mathf.Min(f, 0.95f - Mathf.Abs(u)), 0.93f - Mathf.Abs(v));
        }
    }

    /// <summary>
    /// Spray hitting a surface: a curved trail of large elongated drops plus a widening
    /// cone of fine specks with a few mid-size drops (power-law sizes). Stamped drop by
    /// drop, so a couple of hundred drops cost only their own pixels.
    /// </summary>
    private static void StampSpatter(int c, float[] f, int n)
    {
        for (int i = 0; i < f.Length; i++) f[i] = -1f;
        var rng = new System.Random(c * 104729 + 7);
        float curve = (float)(rng.NextDouble() - 0.5) * 0.7f;
        float lane = (float)(rng.NextDouble() - 0.5) * 0.3f;
        int big = 6 + rng.Next(5);
        for (int i = 0; i < big; i++)
        {
            float t = i / (float)(big - 1);
            float u = -0.82f + 1.2f * t;
            float v = lane + curve * u * u + (float)(rng.NextDouble() - 0.5) * 0.1f;
            float r = Mathf.Lerp(0.048f, 0.02f, t) * (0.7f + 0.6f * (float)rng.NextDouble());
            float st = 1.3f + 1.4f * (float)rng.NextDouble();
            Stamp(f, n, u, v, r, st);
            Stamp(f, n, u + r * st * 1.25f, v, r * 0.35f, 1.4f); // small bead the drop threw ahead
        }
        for (int i = 0; i < 260; i++)
        {
            float u = -0.88f + 1.76f * Mathf.Sqrt((float)rng.NextDouble());
            float spread = 0.2f + 0.7f * (u + 0.88f) / 1.76f;
            float g = (float)(rng.NextDouble() + rng.NextDouble() + rng.NextDouble() - 1.5) * 0.8f;
            float v = Mathf.Clamp(lane + g * spread, -0.88f, 0.88f);
            float r = 0.009f + 0.035f * Mathf.Pow((float)rng.NextDouble(), 6f);
            float st = r > 0.018f ? 1f + 1.2f * (float)rng.NextDouble() : 1f + 0.3f * (float)rng.NextDouble();
            Stamp(f, n, u, v, r, st);
        }
    }

    private static void Stamp(float[] f, int n, float cu, float cv, float r, float stretch)
    {
        float ru = r * stretch + 0.02f, rv = r + 0.02f;
        int x0 = Mathf.Max(0, (int)((cu - ru + 1f) * 0.5f * n)), x1 = Mathf.Min(n - 1, (int)((cu + ru + 1f) * 0.5f * n) + 1);
        int y0 = Mathf.Max(0, (int)((cv - rv + 1f) * 0.5f * n)), y1 = Mathf.Min(n - 1, (int)((cv + rv + 1f) * 0.5f * n) + 1);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
            if (Mathf.Abs(u) > 0.94f || Mathf.Abs(v) > 0.94f) continue;
            float du = (u - cu) / stretch, dv = v - cv;
            float d = r - Mathf.Sqrt(du * du + dv * dv);
            int i = y * n + x;
            if (d > f[i]) f[i] = d;
        }
    }

    private static Color32 Px(float g, float a)
    {
        byte gb = (byte)(Mathf.Clamp01(g) * 255f);
        return new Color32(gb, gb, gb, (byte)(Mathf.Clamp01(a) * 255f));
    }

    private static Texture2D Finish(int w, int h, Color32[] px, string name)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
        tex.SetPixels32(px);
        tex.Apply(true, true); // mips, then drop the CPU copy
        return tex;
    }

    private static float Smooth(float a, float b, float x)
    {
        float t = Mathf.Clamp01((x - a) / (b - a));
        return t * t * (3f - 2f * t);
    }

    private static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777215f;
        }
    }

    private static float Noise(float x, float y, int seed)
    {
        int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
        float fx = x - xi, fy = y - yi;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);
        float a = Hash(xi, yi, seed), b = Hash(xi + 1, yi, seed);
        float c = Hash(xi, yi + 1, seed), d = Hash(xi + 1, yi + 1, seed);
        return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
    }

    private static float Fbm(float x, float y, int seed)
    {
        return Noise(x, y, seed) * 0.57f + Noise(x * 2.1f, y * 2.1f, seed + 1) * 0.29f + Noise(x * 4.3f, y * 4.3f, seed + 2) * 0.14f;
    }
}
