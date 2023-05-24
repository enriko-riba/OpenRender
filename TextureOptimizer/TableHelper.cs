using OpenRender.Core.Textures;

internal static class TableHelper
{
    public static ConsoleColor BackgroundColor { get; set; } = ConsoleColor.Gray;

    public static void DisplayTableHeader(int unitsCount)
    {
        var separator = new string('-', unitsCount * 7 + 50);        
        Console.BackgroundColor = BackgroundColor;
        Console.WriteLine(separator);
        Console.Write("#   ");
        for (var i = 0; i < unitsCount; i++)
        {
            Console.Write($"{i,-7} ");
        }
        Console.WriteLine(" Δ   Texture handles".PadRight(30));
        Console.WriteLine(separator);
    }

    public static void DisplayTextureUnits(int unitsCount, List<MaterialData> materialList, int currentIndex, TextureUnitUsage[] unitsUsages, TextureUnitUsage[]? previous)
    {
        var material = materialList[currentIndex % materialList.Count];

        Console.ForegroundColor = ConsoleColor.DarkMagenta;
        Console.BackgroundColor = BackgroundColor;
        Console.Write($"{currentIndex,-4}");

        for (var i = 0; i < unitsCount; i++)
        {
            var unit = unitsUsages.Single(r => r.Unit == i);
            var prev = previous?.Single(r => r.Unit == i) ?? new();
            var isSwap = unit.TextureHandle != null && unit.ChangeCount > prev.ChangeCount;
            var textureUnitDisplay = unit.TextureHandle != null ? $"{unit.TextureHandle,-2}:{unit.ChangeCount,-3}" : " -";
            var isCurrentTexture = material.TextureHandles.Any(u => u == unit?.TextureHandle);
            Console.BackgroundColor = isSwap ? ConsoleColor.Red:
                                      isCurrentTexture ? ConsoleColor.DarkGreen : BackgroundColor;            
            Console.ForegroundColor = ConsoleColor.DarkMagenta;
            Console.Write($"{textureUnitDisplay, -7}");
            Console.BackgroundColor = BackgroundColor;
            Console.Write(" ");
        }
        var textureSwaps = unitsUsages.Sum(r => r.ChangeCount);
        Console.Write($"|{textureSwaps,-3}|");
        Console.WriteLine($" {material.Id}=>{string.Join(",", material.TextureHandles),-24}");
        var separator = new string('-', unitsCount * 7 + 50);
        Console.WriteLine(separator);
    }
}
