using SpyroGame.Noise;
using System.Runtime.CompilerServices;

namespace SpyroGame.World;

// NEW: world-scale low-freq fields cached once (continentalness/erosion/temp/humidity)
public sealed class WorldFields
{
    public readonly int TexSize, WorldSize;
    public readonly float[] C, E, T, H; // raw ~[-1,1]
    public readonly float[] TDetail, HDetail; // extra mid-scale variation channels

    public WorldFields(int worldSize, int texSize, float cf, float ef, float tf, float hf, int seed)
    {
        WorldSize = worldSize; TexSize = texSize;
        var span = worldSize / (float)texSize;                // blocks per texel → scale freqs

        var cFreq = MathF.Max(cf * span, 1e-4f);
        var eFreq = MathF.Max(ef * span, 1e-4f);
        var tFreq = MathF.Max(tf * span, 1e-4f);
        var hFreq = MathF.Max(hf * span, 1e-4f);

        C = NoiseData.CreateField(0, 0, texSize, cFreq, seed ^ 11, out _);
        E = NoiseData.CreateField(0, 0, texSize, eFreq, seed ^ 23, out _);
        T = NoiseData.CreateField(0, 0, texSize, tFreq, seed ^ 37, out _);
        H = NoiseData.CreateField(0, 0, texSize, hFreq, seed ^ 53, out _);

        var tDetailBase = MathF.Max(tFreq * 3.2f, tFreq + 1e-4f);
        var hDetailBase = MathF.Max(hFreq * 3f, hFreq + 1e-4f);
        TDetail = NoiseData.CreateDomainWarped(
            0, 0, texSize,
            baseFreq: tDetailBase,
            warpFreq: tDetailBase * 0.45f,
            warpAmp: 3.0f,
            seedBase: seed ^ unchecked((int)0x17B5A2C3),
            out _);
        HDetail = NoiseData.CreateDomainWarped(
            0, 0, texSize,
            baseFreq: hDetailBase,
            warpFreq: hDetailBase * 0.4f,
            warpAmp: 2.8f,
            seedBase: seed ^ unchecked((int)0x3F4C1D27),
            out _);
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
