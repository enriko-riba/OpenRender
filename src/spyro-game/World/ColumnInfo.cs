namespace SpyroGame.World;

/// <summary>
/// Structure for saving/retriving debug information about a terrain column.
/// </summary>
/// <param name="biome"></param>
/// <param name="c"></param>
/// <param name="e"></param>
/// <param name="t"></param>
/// <param name="h"></param>
/// <param name="heightY"></param>
/// <param name="height01"></param>
public readonly struct ColumnInfo(string biome, float c, float e, float t, float h, byte heightY, float height01)
{
    public readonly string Biome = biome;
    public readonly float Continentalness = c;
    public readonly float Erosion = e;
    public readonly float Temperature = t;
    public readonly float Humidity = h;
    public readonly float Height01 = height01;
    public readonly byte HeightY = heightY;
}