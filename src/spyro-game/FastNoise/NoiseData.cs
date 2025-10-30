namespace SpyroGame.Noise;

internal static class NoiseData
{
    public static float[] CreateField(int xOffset, int yOffset, int size, float freq, int seed, out (float min, float max) minMax)
    {
        var n = size * size;
        var outBuf = new float[n];
        var xs = new float[n];
        var ys = new float[n];
        var i = 0;

        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++, i++)
            {
                xs[i] = x + xOffset;   // world-space!
                ys[i] = y + yOffset;
            }

        // NoiseDotNet scales coords internally by xScale/yScale
        NoiseDotNet.Noise.GradientNoise2D(xs, ys, outBuf, freq, freq, 1f, seed);

        float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
        for (i = 0; i < n; i++) { if (outBuf[i] < lo) lo = outBuf[i]; if (outBuf[i] > hi) hi = outBuf[i]; }
        minMax = (lo, hi);
        return outBuf; // [-1,1] ish
    }

    public static float[] CreateDomainWarped(
    int xOffset, int yOffset, int size,
    float baseFreq, float warpFreq, float warpAmp,
    int seedBase, out (float min, float max) minMax)
    {
        var n = size * size;
        var xs = new float[n];
        var ys = new float[n];
        var warpX = new float[n];
        var warpY = new float[n];
        var outBuf = new float[n];

        // world coords
        var k = 0;
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++, k++)
            { xs[k] = x + xOffset; ys[k] = y + yOffset; }

        // two warps (seed-salted to avoid correlation)
        NoiseDotNet.Noise.GradientNoise2D(xs, ys, warpX, warpFreq, warpFreq, warpAmp, seedBase ^ unchecked((int)0x9E3779B9));
        NoiseDotNet.Noise.GradientNoise2D(xs, ys, warpY, warpFreq, warpFreq, warpAmp, seedBase ^ unchecked((int)0x7F4A7C15));

        // apply warp to coords in-place (keeps allocs fixed)
        for (var i = 0; i < n; i++) { xs[i] += warpX[i]; ys[i] += warpY[i]; }

        // final base
        NoiseDotNet.Noise.GradientNoise2D(xs, ys, outBuf, baseFreq, baseFreq, 1f, seedBase);

        float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
        for (var i = 0; i < n; i++) { if (outBuf[i] < lo) lo = outBuf[i]; if (outBuf[i] > hi) hi = outBuf[i]; }
        minMax = (lo, hi);
        return outBuf;
    }

    public static float SampleDomainWarped(float x, float y, float baseFreq, float warpFreq, float warpAmp, int seedBase)
    {
        var warpSeedX = seedBase ^ unchecked((int)0x9E3779B9);
        var warpSeedY = seedBase ^ unchecked((int)0x7F4A7C15);

        var warpX = SampleGradientNoise2D(x, y, warpFreq, warpAmp, warpSeedX);
        var warpY = SampleGradientNoise2D(x, y, warpFreq, warpAmp, warpSeedY);

        var warpedX = x + warpX;
        var warpedY = y + warpY;

        return SampleGradientNoise2D(warpedX, warpedY, baseFreq, 1f, seedBase);
    }

    public static float SampleGradientNoise2D(float x, float y, float freq, float amp, int seed)
    {
        Span<float> xs = stackalloc float[1] { x };
        Span<float> ys = stackalloc float[1] { y };
        Span<float> outBuf = stackalloc float[1];
        NoiseDotNet.Noise.GradientNoise2D(xs, ys, outBuf, freq, freq, amp, seed);
        return outBuf[0];
    }
}
