using SpyroGame.Noise;
using System.Runtime.CompilerServices;

namespace SpyroGame.World;

// NEW: world-scale low-freq fields cached once (continentalness/erosion/temp/humidity)
public sealed class WorldFields
{
    public readonly int TexSize, WorldSize;
    public readonly float[] C, E, T, H; // raw ~[-1,1]

    public WorldFields(int worldSize, int texSize, float cf, float ef, float tf, float hf, int seed)
    {
        WorldSize = worldSize; TexSize = texSize;
        var span = worldSize / (float)texSize;                // blocks per texel → scale freqs

        C = NoiseData.CreateField(0, 0, texSize, cf * span, seed ^ 11, out _);
        E = NoiseData.CreateField(0, 0, texSize, ef * span, seed ^ 23, out _);
        T = NoiseData.CreateField(0, 0, texSize, tf * span, seed ^ 37, out _);
        H = NoiseData.CreateField(0, 0, texSize, hf * span, seed ^ 53, out _);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Sample(float[] tex, float wx, float wz)
    {
        float u = (wx / WorldSize) * (TexSize - 1);
        float v = (wz / WorldSize) * (TexSize - 1);
        int x = (int)u, y = (int)v;
        float fu = u - x, fv = v - y;
        int x1 = Math.Min(x + 1, TexSize - 1), y1 = Math.Min(y + 1, TexSize - 1);
        float a = tex[y * TexSize + x], b = tex[y * TexSize + x1];
        float c = tex[y1 * TexSize + x], d = tex[y1 * TexSize + x1];
        return TerrainBuilder.Lerp(TerrainBuilder.Lerp(a, b, fu), TerrainBuilder.Lerp(c, d, fu), fv); // ~[-1,1]
    }
}

public struct GenParams
{
    public float SeaMin, SeaMax;     // continentalness -> sea bias
    public float MtnLow, MtnHigh;    // continentalness -> height gain
    public float TerraceStep, TerraceStrength;
    public float RidgePow, SmoothPow;
}
