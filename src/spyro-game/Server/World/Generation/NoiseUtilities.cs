using System.Runtime.CompilerServices;

namespace SpyroGame.Server.World.Generation;

/// <summary>
/// Common noise utility functions used across terrain generation.
/// Provides value noise, hashing, and interpolation helpers.
/// </summary>
internal static class NoiseUtilities
{
    /// <summary>
    /// 3D value noise with trilinear interpolation.
    /// Returns a value in [0, 1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ValueNoise3D(float x, float y, float z, uint seed)
    {
        var xi = (int)MathF.Floor(x);
        var yi = (int)MathF.Floor(y);
        var zi = (int)MathF.Floor(z);

        var fx = x - xi;
        var fy = y - yi;
        var fz = z - zi;

        var c000 = Hash3D(xi, yi, zi, seed);
        var c100 = Hash3D(xi + 1, yi, zi, seed);
        var c010 = Hash3D(xi, yi + 1, zi, seed);
        var c110 = Hash3D(xi + 1, yi + 1, zi, seed);
        var c001 = Hash3D(xi, yi, zi + 1, seed);
        var c101 = Hash3D(xi + 1, yi, zi + 1, seed);
        var c011 = Hash3D(xi, yi + 1, zi + 1, seed);
        var c111 = Hash3D(xi + 1, yi + 1, zi + 1, seed);

        var u = Fade(fx);
        var v = Fade(fy);
        var w = Fade(fz);

        var x0 = Lerp(c000, c100, u);
        var x1 = Lerp(c010, c110, u);
        var x2 = Lerp(c001, c101, u);
        var x3 = Lerp(c011, c111, u);

        var y0 = Lerp(x0, x1, v);
        var y1 = Lerp(x2, x3, v);

        return Lerp(y0, y1, w);
    }

    /// <summary>
    /// 2D value noise with bilinear interpolation.
    /// Returns a value in [0, 1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ValueNoise2D(float x, float z, uint seed)
    {
        var xi = (int)MathF.Floor(x);
        var zi = (int)MathF.Floor(z);

        var fx = x - xi;
        var fz = z - zi;

        var c00 = Hash2D(xi, zi, seed);
        var c10 = Hash2D(xi + 1, zi, seed);
        var c01 = Hash2D(xi, zi + 1, seed);
        var c11 = Hash2D(xi + 1, zi + 1, seed);

        var u = Fade(fx);
        var v = Fade(fz);

        var x0 = Lerp(c00, c10, u);
        var x1 = Lerp(c01, c11, u);

        return Lerp(x0, x1, v);
    }

    /// <summary>
    /// 3D hash function returning a value in [0, 1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Hash3D(int x, int y, int z, uint seed)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + y * 668265263 + z * 2147483647);
            h ^= seed;
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0x00FFFFFF) / 16777216f;
        }
    }

    /// <summary>
    /// 2D hash function returning a value in [0, 1).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Hash2D(int x, int z, uint seed)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + z * 668265263);
            h ^= seed;
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0x00FFFFFF) / 16777216f;
        }
    }

    /// <summary>
    /// Perlin-style fade function for smooth interpolation.
    /// f(t) = 6t^5 - 15t^4 + 10t^3
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Fade(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * t * (t * (t * 6f - 15f) + 10f);
    }

    /// <summary>
    /// Linear interpolation between two values.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>
    /// Hermite smoothstep interpolation.
    /// Returns 0 when x &lt;= edge0, 1 when x &gt;= edge1, smooth transition between.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Smoothstep(float edge0, float edge1, float x)
    {
        if (Math.Abs(edge1 - edge0) < float.Epsilon)
        {
            return x >= edge1 ? 1f : 0f;
        }

        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Bilinear interpolation from 4 grid corners.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float BilinearSample(
        float[] grid,
        int yOffset,
        int idx00, int idx10, int idx01, int idx11,
        float tx, float tz)
    {
        var c00 = grid[yOffset + idx00];
        var c10 = grid[yOffset + idx10];
        var c01 = grid[yOffset + idx01];
        var c11 = grid[yOffset + idx11];

        var x0 = c00 + (c10 - c00) * tx;
        var x1 = c01 + (c11 - c01) * tx;

        return x0 + (x1 - x0) * tz;
    }
}
