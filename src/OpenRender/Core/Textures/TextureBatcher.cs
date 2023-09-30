namespace OpenRender.Core.Textures;

public class TextureBatcher
{
    private readonly TextureUnitUsage[] unitUsages;
    private readonly int unitsCount;
    private Dictionary<int, int> textureFrequencies = new();
    private List<Material> materialsList = new();

    public TextureBatcher(int textureUnitsCount)
    {
        unitsCount = textureUnitsCount;
        unitUsages = new TextureUnitUsage[textureUnitsCount];
        Reset();
    }

    public void Reset()
    {
        for (var i = 0; i < unitsCount; i++)
        {
            unitUsages[i] = new TextureUnitUsage() { Unit = i };
        }
    }

    public void SortMaterials(List<Material> materialsList)
    {
        textureFrequencies = CalculateTextureFrequency(materialsList);
        materialsList.Sort((a, b) =>
        {
            var aMaxFrequency = GetMaxFrequency(a, textureFrequencies);
            var bMaxFrequency = GetMaxFrequency(b, textureFrequencies);
            return aMaxFrequency == bMaxFrequency
                ? a.TextureHandles.FirstOrDefault() - b.TextureHandles.FirstOrDefault()
                : bMaxFrequency - aMaxFrequency;
        });
        this.materialsList = materialsList;
    }

    public TextureUnitUsage[] GetOptimalTextureUnits(Material material)
    {
        foreach (var handle in material.TextureHandles)
        {
            if (Find(r => r.TextureHandle == handle) >= 0)
            {
                // Texture is already bound to an unit, skip
                continue; 
            }

            var idx = Find(uu => !uu.TextureHandle.HasValue);
            if (idx >= 0)
            {
                // empty unit found, bind texture to it, 
                unitUsages[idx].TextureHandle = handle;
                unitUsages[idx].ChangeCount = 0; // not counted as a texture swap
                continue;   
            }

            //  search for a texture unit bound to a texture that is not used anymore in current and all following materials
            var currentIndex = materialsList.IndexOf(material);
            var remainingTextures = materialsList.Where((_, idx) => idx >= currentIndex).SelectMany(ml => ml.TextureHandles).Distinct().Order();
            var unitWithUnusedTexture = unitUsages.LastOrDefault(uu => !remainingTextures.Any(a => a == uu.TextureHandle));
            if (unitWithUnusedTexture.TextureHandle != null)
            {
                // found unit bound to texture that is not going to be used anymore
                idx = unitWithUnusedTexture.Unit;
                unitUsages[idx].TextureHandle = handle;
                unitUsages[idx].ChangeCount++;
                continue;   
            }
            else
            {
                idx = unitUsages
                    .Where(r => !material.TextureHandles.Any(th => th == r.TextureHandle))  //  any unit not already bound to a texture in the material
                    .OrderBy(r => textureFrequencies[r.TextureHandle!.Value])               //  sort to find unit bound to least frequent used texture
                    .Select(r => r.Unit)
                    .First();
                unitUsages[idx].TextureHandle = handle;
                unitUsages[idx].ChangeCount++;
            }
        }
        return unitUsages;
    }

    public int GetTextureUnitWithTexture(int textureHandle)
    {
        var idx = Find(tu => tu.TextureHandle == textureHandle);
        return idx == -1 ? -1 : unitUsages[idx].Unit;
    }

    private int Find(Predicate<TextureUnitUsage> predicate)
    {
        for (var i = 0; i < unitUsages.Length; i++)
        {
            if (predicate(unitUsages[i]))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Calculates the frequency of each unique texture name in the list of arrays.
    /// </summary>
    /// <param name="materials">The list of arrays of texture handles.</param>
    /// <returns>A dictionary containing the texture names as keys and their frequencies as values.</returns>
    private static Dictionary<int, int> CalculateTextureFrequency(List<Material> materials)
    {
        var frequencies = new Dictionary<int, int>();
        foreach (var material in materials)
        {
            foreach (var textureHandle in material.TextureHandles)
            {
                if (frequencies.ContainsKey(textureHandle))
                {
                    frequencies[textureHandle]++;
                }
                else
                {
                    frequencies[textureHandle] = 1;
                }
            }
        }
        return frequencies;
    }

    /// <summary>
    /// Finds what texture from the given array has the maximum frequency value based on the frequency dictionary.
    /// </summary>
    /// <param name="material"></param>
    /// <param name="frequency"></param>
    /// <returns>The handle of the texture that has the highest frequency</returns>
    private static int GetMaxFrequency(Material material, Dictionary<int, int> frequency)
    {
        var maxFrequency = 0;
        foreach (var textureHandle in material.TextureHandles)
        {
            if (/*frequency.ContainsKey(textureHandle) &&*/ frequency[textureHandle] > maxFrequency)
            {
                maxFrequency = frequency[textureHandle];
            }
        }
        return maxFrequency;
    }
}

public struct TextureUnitUsage
{
    public static readonly TextureUnitUsage EMPTY = default;

    public int? TextureHandle { get; set; }
    public int Unit { get; set; }
    public int ChangeCount { get; set; }
}
