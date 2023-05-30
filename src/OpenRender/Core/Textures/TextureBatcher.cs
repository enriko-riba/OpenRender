using System.Diagnostics;

namespace OpenRender.Core.Textures;

public class TextureBatcher
{
    private readonly TextureUnitUsage[] unitUsages;
    private readonly int unitsCount;
    private Dictionary<int, int> textureFrequencies = new();
    private List<Material> materialsList = new();

//#if DEBUG
//    private readonly List<TextureUnitUsage[]> unitsHistory = new();
//    public IEnumerable<TextureUnitUsage[]> UnitsHistory => unitsHistory;
//#endif

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
    public IReadOnlyCollection<Material> MaterialDataList => materialsList;

    public void SortMaterials(List<Material> materialsList)
    {
        textureFrequencies = CalculateTextureFrequency(materialsList);
        materialsList.Sort((a, b) => GetMaxFrequency(b, textureFrequencies).CompareTo(GetMaxFrequency(a, textureFrequencies)));
        this.materialsList = materialsList;
    }

    public TextureUnitUsage[] GetOptimalTextureUnits(Material material)
    {
        foreach (var handle in material.TextureHandles)
        {
            var existingTextureUnit = unitUsages.FirstOrDefault(r => r.TextureHandle == handle);
            if (existingTextureUnit != null)
            {
                continue; // Texture name is already assigned, skip
            }

            var emptyUnit = unitUsages.FirstOrDefault(uu => !uu.TextureHandle.HasValue);
            if (emptyUnit != null)
            {
                emptyUnit.TextureHandle = handle;
                emptyUnit.ChangeCount = 0;
                continue;   // Assigned to empty unit, counted as a texture swap
            }

            //  calc if there is a texture unit with an assigned texture that is not used anymore in current and all following list items
            var currentIndex = materialsList.IndexOf(material);
            var remainingTextures = materialsList.Where((_, idx) => idx >= currentIndex).SelectMany(ml => ml.TextureHandles).Distinct().Order().ToArray();
            var unitWithUnusedTexture = unitUsages.LastOrDefault(uu => !remainingTextures.Any(a => a == uu.TextureHandle));
            if (unitWithUnusedTexture != null)
            {
                unitWithUnusedTexture.TextureHandle = handle;
                unitWithUnusedTexture.ChangeCount++;
                continue;   // Assigned to unit that was bound to texture that is not going to be used anymore
            }
            else
            {
                var reusableUnit = unitUsages
                    .Where(r => !material.TextureHandles.Any(th => th == r.TextureHandle))
                    .OrderBy(r => textureFrequencies[r.TextureHandle!.Value])
                    .ThenBy(r => r.ChangeCount)
                    .FirstOrDefault();

                if (reusableUnit != null)
                {
                    reusableUnit.TextureHandle = handle;
                    reusableUnit.ChangeCount++; // Assigning new texture and increase counter                        
                }
                else
                {
                    Debug.Assert(false, "No available texture unit found!");
                }
            }
        }


//#if DEBUG
//        var textureUnitUsage = unitUsages.Select(uu => new TextureUnitUsage()
//        {
//            TextureHandle = uu.TextureHandle,
//            Unit = uu.Unit,
//            ChangeCount = uu.ChangeCount
//        }).ToArray();
//        //  save the history for debug purposes
//        unitsHistory.Add(textureUnitUsage);
//#endif

        return unitUsages;
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
            if (frequency.ContainsKey(textureHandle) && frequency[textureHandle] > maxFrequency)
            {
                maxFrequency = frequency[textureHandle];
            }
        }
        return maxFrequency;
    }
}

public class TextureUnitUsage
{
    public int? TextureHandle { get; set; }
    public int Unit { get; set; }
    public int ChangeCount { get; set; }
}
