namespace SpyroGame.World;

public struct Biome
{
    public string Name { get; set; }
    public float C { get; set; } // Continentalness
    public float E { get; set; } // Erosion
    public float T { get; set; } // Temperature
    public float H { get; set; } // Humidity
    public BlockType Top { get; set; }
}
