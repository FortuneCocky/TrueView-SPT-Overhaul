using System;
using System.Collections.Generic;
using UnityEngine;

namespace BloodFX;

/// <summary>
/// Turns a signed distance field (negative inside) into a mesh with surface nets: one vertex per
/// grid cell the surface passes through, at the average of its edge crossings, and one quad per
/// grid edge the surface cuts. Smooth, watertight, no lookup tables. Normals come from the field's
/// gradient. Used for gore that has to be a solid shape (the neck stump), built once on the CPU.
/// </summary>
internal static class SurfaceNets
{
    private static readonly int[,] Corner =
    {
        { 0, 0, 0 }, { 1, 0, 0 }, { 0, 1, 0 }, { 1, 1, 0 }, { 0, 0, 1 }, { 1, 0, 1 }, { 0, 1, 1 }, { 1, 1, 1 }
    };
    private static readonly int[,] Edge =
    {
        { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 }, { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 }
    };

    /// <param name="min">Corner of the sampled box.</param>
    /// <param name="cell">Grid spacing.</param>
    /// <param name="n">Samples per axis (x, y, z).</param>
    public static void Build(Func<Vector3, float> sdf, Vector3 min, float cell, Vector3Int n,
        List<Vector3> verts, List<Vector3> normals, List<int> tris)
    {
        verts.Clear();
        normals.Clear();
        tris.Clear();
        int nx = n.x, ny = n.y, nz = n.z;
        var f = new float[nx * ny * nz];
        int Id(int x, int y, int z) => x + nx * (y + ny * z);
        for (int z = 0; z < nz; z++)
        for (int y = 0; y < ny; y++)
        for (int x = 0; x < nx; x++)
            f[Id(x, y, z)] = sdf(min + new Vector3(x, y, z) * cell);

        // a vertex for every cell with corners on both sides
        var cellVert = new int[(nx - 1) * (ny - 1) * (nz - 1)];
        int Cid(int x, int y, int z) => x + (nx - 1) * (y + (ny - 1) * z);
        var c = new float[8];
        for (int z = 0; z < nz - 1; z++)
        for (int y = 0; y < ny - 1; y++)
        for (int x = 0; x < nx - 1; x++)
        {
            int mask = 0;
            for (int k = 0; k < 8; k++)
            {
                c[k] = f[Id(x + Corner[k, 0], y + Corner[k, 1], z + Corner[k, 2])];
                if (c[k] < 0f) mask |= 1 << k;
            }
            if (mask == 0 || mask == 255) { cellVert[Cid(x, y, z)] = -1; continue; }
            Vector3 sum = Vector3.zero;
            int cnt = 0;
            for (int e = 0; e < 12; e++)
            {
                int a = Edge[e, 0], b = Edge[e, 1];
                if ((c[a] < 0f) == (c[b] < 0f)) continue;
                float t = c[a] / (c[a] - c[b]);
                sum += Vector3.Lerp(new Vector3(Corner[a, 0], Corner[a, 1], Corner[a, 2]), new Vector3(Corner[b, 0], Corner[b, 1], Corner[b, 2]), t);
                cnt++;
            }
            Vector3 p = min + (new Vector3(x, y, z) + sum / cnt) * cell;
            cellVert[Cid(x, y, z)] = verts.Count;
            verts.Add(p);
            normals.Add(Gradient(sdf, p, cell * 0.5f));
        }

        // a quad across every grid edge the surface cuts, joining the four cells around it
        for (int z = 1; z < nz - 1; z++)
        for (int y = 1; y < ny - 1; y++)
        for (int x = 1; x < nx - 1; x++)
        {
            float f0 = f[Id(x, y, z)];
            bool inside = f0 < 0f;
            if (inside != (f[Id(x + 1, y, z)] < 0f))
                Quad(tris, inside, cellVert[Cid(x, y - 1, z - 1)], cellVert[Cid(x, y, z - 1)], cellVert[Cid(x, y, z)], cellVert[Cid(x, y - 1, z)]);
            if (inside != (f[Id(x, y + 1, z)] < 0f))
                Quad(tris, inside, cellVert[Cid(x - 1, y, z - 1)], cellVert[Cid(x - 1, y, z)], cellVert[Cid(x, y, z)], cellVert[Cid(x, y, z - 1)]);
            if (inside != (f[Id(x, y, z + 1)] < 0f))
                Quad(tris, inside, cellVert[Cid(x - 1, y - 1, z)], cellVert[Cid(x, y - 1, z)], cellVert[Cid(x, y, z)], cellVert[Cid(x - 1, y, z)]);
        }
    }

    private static void Quad(List<int> tris, bool flip, int a, int b, int c, int d)
    {
        if (a < 0 || b < 0 || c < 0 || d < 0) return;
        if (flip) { tris.Add(a); tris.Add(b); tris.Add(c); tris.Add(a); tris.Add(c); tris.Add(d); }
        else { tris.Add(a); tris.Add(c); tris.Add(b); tris.Add(a); tris.Add(d); tris.Add(c); }
    }

    public static Vector3 Gradient(Func<Vector3, float> sdf, Vector3 p, float h)
    {
        var g = new Vector3(
            sdf(p + new Vector3(h, 0f, 0f)) - sdf(p - new Vector3(h, 0f, 0f)),
            sdf(p + new Vector3(0f, h, 0f)) - sdf(p - new Vector3(0f, h, 0f)),
            sdf(p + new Vector3(0f, 0f, h)) - sdf(p - new Vector3(0f, 0f, h)));
        return g.sqrMagnitude > 1e-12f ? g.normalized : Vector3.up;
    }

    // ------------------------------------------------------------ shape helpers (all in metres)

    public static float Sphere(Vector3 p, Vector3 c, float r) => (p - c).magnitude - r;

    /// <summary>Vertical capped cylinder around the y axis through <paramref name="c"/>.</summary>
    public static float Cylinder(Vector3 p, Vector3 c, float r, float y0, float y1)
    {
        Vector3 q = p - c;
        float dr = new Vector2(q.x, q.z).magnitude - r;
        float dy = Mathf.Max(y0 - q.y, q.y - y1);
        return Mathf.Min(Mathf.Max(dr, dy), 0f) + new Vector2(Mathf.Max(dr, 0f), Mathf.Max(dy, 0f)).magnitude;
    }

    /// <summary>Ellipsoid with radii <paramref name="r"/>, turned by the inverse of <paramref name="inv"/> (approximate distance).</summary>
    /// <param name="inv">Inverse of the ellipsoid's orientation, worked out once by the caller (Quaternion.Inverse is a Unity call).</param>
    public static float Ellipsoid(Vector3 p, Vector3 c, Quaternion inv, Vector3 r)
    {
        Vector3 q = inv * (p - c);
        float k0 = new Vector3(q.x / r.x, q.y / r.y, q.z / r.z).magnitude;
        float k1 = new Vector3(q.x / (r.x * r.x), q.y / (r.y * r.y), q.z / (r.z * r.z)).magnitude;
        return k1 > 1e-6f ? k0 * (k0 - 1f) / k1 : -Mathf.Min(r.x, Mathf.Min(r.y, r.z));
    }

    /// <summary>Union that melts the two shapes together over <paramref name="k"/> metres.</summary>
    public static float SmoothMin(float a, float b, float k)
    {
        float h = Mathf.Clamp01(0.5f + 0.5f * (b - a) / k);
        return Mathf.Lerp(b, a, h) - k * h * (1f - h);
    }

    /// <summary>Cheap 3D value noise in [-1, 1] from crossed 2D Perlin.</summary>
    /// <remarks>Pure C# (no Unity calls), so distance fields that use it can be meshed off the main thread.</remarks>
    public static float Noise(Vector3 p, float seed)
    {
        return (Perlin(p.x + seed, p.y) + Perlin(p.y + seed * 1.7f, p.z) +
                Perlin(p.z + seed * 0.6f, p.x)) * (2f / 3f) - 1f;
    }

    private static readonly int[] Perm = BuildPerm();

    private static int[] BuildPerm()
    {
        var p = new int[512];
        var rng = new System.Random(1337);
        for (int i = 0; i < 256; i++) p[i] = i;
        for (int i = 255; i > 0; i--) { int j = rng.Next(i + 1); (p[i], p[j]) = (p[j], p[i]); }
        for (int i = 0; i < 256; i++) p[256 + i] = p[i];
        return p;
    }

    /// <summary>2D gradient noise in [0, 1], like Mathf.PerlinNoise but thread-safe.</summary>
    public static float Perlin(float x, float y)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
        float xf = x - xi, yf = y - yi;
        xi &= 255; yi &= 255;
        float u = xf * xf * xf * (xf * (xf * 6f - 15f) + 10f), v = yf * yf * yf * (yf * (yf * 6f - 15f) + 10f);
        int aa = Perm[Perm[xi] + yi], ab = Perm[Perm[xi] + yi + 1], ba = Perm[Perm[xi + 1] + yi], bb = Perm[Perm[xi + 1] + yi + 1];
        float x1 = Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1f, yf), u);
        float x2 = Lerp(Grad(ab, xf, yf - 1f), Grad(bb, xf - 1f, yf - 1f), u);
        return Lerp(x1, x2, v) * 0.7f + 0.5f;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float Grad(int h, float x, float y)
    {
        switch (h & 7)
        {
            case 0: return x + y;
            case 1: return -x + y;
            case 2: return x - y;
            case 3: return -x - y;
            case 4: return x;
            case 5: return -x;
            case 6: return y;
            default: return -y;
        }
    }
}
