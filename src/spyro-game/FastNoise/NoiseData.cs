namespace SpyroGame.Noise;

internal static class NoiseData
{
    public static float[] CreateFromEncoding(string encoding, int size, out OutputMinMax minMax) => CreateFromEncoding(encoding, 0, 0, size, 0.02f, 1337, out minMax);

    public static float[] CreateFromEncoding(string encoding, int xOffset, int yOffset, int size, float frequency, int seed, out OutputMinMax minMax)
    {
        var noiseData = new float[size * size];
        var nodeTree = FastNoise.FromEncodedNodeTree(encoding);
        minMax = nodeTree.GenUniformGrid2D(noiseData, xOffset, yOffset, size, size, frequency, seed);        
        return noiseData;
    }
}
