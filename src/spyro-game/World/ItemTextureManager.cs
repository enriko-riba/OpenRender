using OpenRender;
using OpenRender.Core;
using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SpyroGame.World.Registry;

namespace SpyroGame.World;

/// <summary>
/// Manages item textures and materials.
/// Loads textures for all ItemIds at startup and reports missing ones.
/// </summary>
public static class ItemTextureManager
{
    private static readonly Dictionary<ItemId, Material> materials = [];
    private static readonly Material defaultMaterial = Material.Default;

    private const string ItemTextureDir = "Resources/voxel/items";
    private const string BlockTextureDir = "Resources/voxel/blocks";

    static ItemTextureManager()
    {
        LoadItemMaterials();
    }

    public static bool IsBlockItem(ItemId item) => ItemRegistry.Get(item) is BlockItem;

    private static void LoadItemMaterials()
    {
        var loadedCount = 0;
        var missingItems = new List<ItemId>();

        var allItemIds = Enum.GetValues<ItemId>()
            .Where(i => i != ItemId.Air)
            .ToList();

        foreach (var itemId in allItemIds)
        {
            var texturePath = GetTexturePathForItem(itemId);

            if (File.Exists(texturePath))
            {
                try
                {
                    var descriptor = new TextureDescriptor(
                        texturePath,
                        MinFilter: TextureMinFilter.Nearest,
                        MagFilter: TextureMagFilter.Nearest,
                        TextureWrapS: TextureWrapMode.ClampToEdge,
                        TextureWrapT: TextureWrapMode.ClampToEdge
                    );

                    // Create material
                    // Pass null for shader as DroppedItemRenderer seems to handle it (or uses default)
                    var material = Material.Create(
                        shader: null!, 
                        textureDescriptor: descriptor, 
                        diffuseColor: Vector3.One
                    );

                    materials[itemId] = material;
                    loadedCount++;
                }
                catch (Exception ex)
                {
                    Log.Error($"ItemTextureManager: Failed to create material for {itemId}: {ex.Message}");
                    missingItems.Add(itemId);
                }
            }
            else
            {
                missingItems.Add(itemId);
            }
        }

        // Log warnings for missing textures
        if (missingItems.Count > 0)
        {
            Log.Warn($"ItemTextureManager: Missing textures for {missingItems.Count} item types (will use default):");
            foreach (var item in missingItems)
            {
                var expectedPath = GetTexturePathForItem(item);
                Log.Warn($"  - {item} (expected: {expectedPath})");
            }
        }

        Log.Info($"ItemTextureManager: Loaded {loadedCount} item materials successfully");
    }

    public static Material GetMaterial(ItemId item) => materials.TryGetValue(item, out var mat) ? mat : defaultMaterial;

    private static string GetTexturePathForItem(ItemId itemId)
    {
        // Check if it's a block item
        var itemDef = ItemRegistry.Get(itemId);
        if (itemDef is BlockItem blockItem)
        {
             var blockName = blockItem.BlockId.ToString();
             var blockPath = $"{BlockTextureDir}/{PascalToSnakeCase(blockName)}.png";
             if (File.Exists(blockPath)) return blockPath;
        }

        var enumName = itemId.ToString();
        var snakeCaseName = PascalToSnakeCase(enumName);
        return $"{ItemTextureDir}/{snakeCaseName}.png";
    }

    private static string PascalToSnakeCase(string pascalCase)
    {
        if (string.IsNullOrEmpty(pascalCase))
            return pascalCase;

        var result = new System.Text.StringBuilder();

        for (var i = 0; i < pascalCase.Length; i++)
        {
            var c = pascalCase[i];

            if (char.IsUpper(c))
            {
                if (i > 0)
                    result.Append('_');
                result.Append(char.ToLowerInvariant(c));
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }


    /// <summary>
    /// Creates a new material for the sprite by copying texture handles from the item material.
    /// This creates a new material with a unique ID to ensure proper renderer caching.
    /// </summary>
    /// <param name="item">The item whose textures should be used.</param>
    /// <param name="shader">The shader to use for the new material.</param>
    /// <returns>A new Material instance with the item's textures and the provided shader.</returns>
    public static Material CreateMaterialForItem(ItemId item, Shader shader)
    {
        var sourceMaterial = GetMaterial(item);
        
        // Create a new material with unique ID
        // Clone the TextureDescriptors array to avoid sharing references
        TextureDescriptor[]? clonedDescriptors = null;
        if (sourceMaterial.TextureDescriptors != null && sourceMaterial.TextureDescriptors.Length > 0)
        {
            clonedDescriptors = [.. sourceMaterial.TextureDescriptors];
        }
        
        var material = Material.Create(
            shader,
            clonedDescriptors,
            sourceMaterial.DiffuseColor,
            sourceMaterial.EmissiveColor,
            sourceMaterial.SpecularColor,
            sourceMaterial.Shininess,
            sourceMaterial.DetailTextureScaleFactor,
            sourceMaterial.DetailTextureBlendFactor
        );
        
        return material;
    }

    /// <summary>
    /// Applies the textures from an item to a sprite's existing material.
    /// Note: Due to renderer caching, this may not take effect until the material ID changes.
    /// Consider using CreateMaterialForItem for reliable texture updates.
    /// </summary>
    /// <param name="item">The item whose textures should be applied.</param>
    /// <param name="targetMaterial">The sprite's material that will receive the textures.</param>
    public static void ApplyItemTextures(ItemId item, Material targetMaterial)
    {
        var sourceMaterial = GetMaterial(item);
        
        // Copy texture handles from source to target (preserving target's shader)
        for (var i = 0; i < Material.MaxTextures; i++)
        {
            targetMaterial.Textures[i] = sourceMaterial.Textures[i];
            targetMaterial.BindlessTextureHandles[i] = sourceMaterial.BindlessTextureHandles[i];
        }
    }
}
